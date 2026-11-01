using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PCDoctor.Core.Rules;

/// <summary>Resultado de una condición con lógica de tres valores: si falta un dato, el resultado es Desconocido.</summary>
public enum Tri { False, True, Unknown }

public sealed class RuleSyntaxException(string message) : Exception(message);

/// <summary>
/// Lenguaje de reglas de la biblioteca. Es deliberadamente pequeño y NO ejecuta código:
///   red.dns_ok == false and (ram.uso_pct >= 90 or not cpu.avx2)
///   existe(disco.sistema.temperatura)   contiene(gpu.nombre, 'NVIDIA')
/// Operadores: and, or, not, ==, !=, &lt;, &lt;=, &gt;, &gt;=. Literales: números, 'texto', true, false.
/// </summary>
public abstract record Expr
{
    public static Expr Parse(string source) => new Parser(source).ParseAll();

    /// <summary>Rutas de hechos que usa la expresión (para validar la biblioteca).</summary>
    public IEnumerable<string> References() => this switch
    {
        Ref r => [r.Path],
        Not n => n.Inner.References(),
        Binary b => b.Left.References().Concat(b.Right.References()),
        Call c => c.Args.SelectMany(a => a.References()),
        _ => [],
    };
}

public sealed record Literal(object Value) : Expr;
public sealed record Ref(string Path) : Expr;
public sealed record Not(Expr Inner) : Expr;
public sealed record Binary(string Op, Expr Left, Expr Right) : Expr;
public sealed record Call(string Name, Expr[] Args) : Expr;

public static class Evaluator
{
    private sealed class UnknownValue { public static readonly UnknownValue Instance = new(); }

    public static Tri Evaluate(Expr expr, FactStore facts)
    {
        var v = Eval(expr, facts);
        return v switch
        {
            bool b => b ? Tri.True : Tri.False,
            UnknownValue => Tri.Unknown,
            _ => throw new RuleSyntaxException($"La condición no da verdadero/falso sino '{v}'."),
        };
    }

    private static object Eval(Expr e, FactStore facts)
    {
        switch (e)
        {
            case Literal l: return l.Value;
            case Ref r: return facts.TryGet(r.Path, out var v) ? v : UnknownValue.Instance;
            case Not n:
                var inner = Eval(n.Inner, facts);
                return inner switch
                {
                    bool b => !b,
                    UnknownValue => UnknownValue.Instance,
                    _ => throw new RuleSyntaxException($"'not' necesita un valor verdadero/falso, recibió '{inner}'."),
                };
            case Binary { Op: "and" } a:
            {
                var l = AsTri(Eval(a.Left, facts), "and");
                if (l == Tri.False) return false;
                var r = AsTri(Eval(a.Right, facts), "and");
                if (r == Tri.False) return false;
                return l == Tri.True && r == Tri.True ? true : UnknownValue.Instance;
            }
            case Binary { Op: "or" } o:
            {
                var l = AsTri(Eval(o.Left, facts), "or");
                if (l == Tri.True) return true;
                var r = AsTri(Eval(o.Right, facts), "or");
                if (r == Tri.True) return true;
                return l == Tri.False && r == Tri.False ? false : UnknownValue.Instance;
            }
            case Binary c:
                return Compare(c.Op, Eval(c.Left, facts), Eval(c.Right, facts));
            case Call { Name: "existe" } ex:
                if (ex.Args is not [Ref path]) throw new RuleSyntaxException("existe() recibe exactamente un hecho.");
                return facts.Has(path.Path);
            case Call { Name: "contiene" } ct:
            {
                if (ct.Args.Length != 2) throw new RuleSyntaxException("contiene() recibe dos argumentos.");
                var hay = Eval(ct.Args[0], facts);
                var needle = Eval(ct.Args[1], facts);
                if (hay is UnknownValue || needle is UnknownValue) return UnknownValue.Instance;
                return (hay.ToString() ?? "").Contains(needle.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
            }
            case Call unknownCall:
                throw new RuleSyntaxException($"Función desconocida: {unknownCall.Name}()");
            default:
                throw new RuleSyntaxException($"Expresión no soportada: {e}");
        }
    }

    private static Tri AsTri(object v, string op) => v switch
    {
        bool b => b ? Tri.True : Tri.False,
        UnknownValue => Tri.Unknown,
        _ => throw new RuleSyntaxException($"'{op}' necesita valores verdadero/falso, recibió '{v}'."),
    };

    private static object Compare(string op, object l, object r)
    {
        if (l is UnknownValue || r is UnknownValue) return UnknownValue.Instance;
        int cmp;
        switch (l, r)
        {
            case (double a, double b): cmp = a.CompareTo(b); break;
            case (string a, string b): cmp = string.Compare(a, b, StringComparison.OrdinalIgnoreCase); break;
            case (bool a, bool b):
                if (op is "==" or "!=") { cmp = a == b ? 0 : 1; break; }
                throw new RuleSyntaxException($"No se puede usar '{op}' con verdadero/falso.");
            default:
                // Tipos distintos: solo tiene sentido la igualdad (y nunca es igual).
                if (op == "==") return false;
                if (op == "!=") return true;
                throw new RuleSyntaxException($"No se puede comparar '{l}' con '{r}' usando '{op}'.");
        }
        return op switch
        {
            "==" => cmp == 0,
            "!=" => cmp != 0,
            "<" => cmp < 0,
            "<=" => cmp <= 0,
            ">" => cmp > 0,
            ">=" => cmp >= 0,
            _ => throw new RuleSyntaxException($"Operador desconocido: {op}"),
        };
    }
}

/// <summary>Reemplaza {hecho} o {hecho:0.0} en los mensajes por el valor medido.</summary>
public static partial class Template
{
    [GeneratedRegex(@"\{([a-zA-Z_][a-zA-Z0-9_.]*)(?::([^}]+))?\}")]
    private static partial Regex Placeholder();

    public static string Render(string template, FactStore facts) =>
        Placeholder().Replace(template, m =>
        {
            if (!facts.TryGet(m.Groups[1].Value, out var v)) return "(dato no disponible)";
            if (v is double d && m.Groups[2].Success) return d.ToString(m.Groups[2].Value, Labels.Culture);
            return FactStore.Format(v);
        });

    public static IEnumerable<string> References(string template) =>
        Placeholder().Matches(template).Select(m => m.Groups[1].Value);
}

internal sealed class Parser
{
    private readonly List<(string Kind, string Text)> _tokens;
    private int _pos;
    private readonly string _source;

    public Parser(string source)
    {
        _source = source;
        _tokens = Tokenize(source);
    }

    public Expr ParseAll()
    {
        var e = ParseOr();
        if (_pos != _tokens.Count) throw Error($"sobra '{_tokens[_pos].Text}'");
        return e;
    }

    private Expr ParseOr()
    {
        var left = ParseAnd();
        while (Accept("kw", "or")) left = new Binary("or", left, ParseAnd());
        return left;
    }

    private Expr ParseAnd()
    {
        var left = ParseNot();
        while (Accept("kw", "and")) left = new Binary("and", left, ParseNot());
        return left;
    }

    private Expr ParseNot() => Accept("kw", "not") ? new Not(ParseNot()) : ParseComparison();

    private Expr ParseComparison()
    {
        var left = ParsePrimary();
        if (Peek("op")) return new Binary(_tokens[_pos++].Text, left, ParsePrimary());
        return left;
    }

    private Expr ParsePrimary()
    {
        if (_pos >= _tokens.Count) throw Error("la condición está incompleta");
        var (kind, text) = _tokens[_pos++];
        switch (kind)
        {
            case "num": return new Literal(double.Parse(text, CultureInfo.InvariantCulture));
            case "str": return new Literal(text);
            case "kw" when text == "true": return new Literal(true);
            case "kw" when text == "false": return new Literal(false);
            case "(":
                var inner = ParseOr();
                Expect(")");
                return inner;
            case "id":
                if (!Accept("(", "(")) return new Ref(text);
                var args = new List<Expr>();
                if (!Accept(")", ")"))
                {
                    do args.Add(ParseOr()); while (Accept(",", ","));
                    Expect(")");
                }
                return new Call(text, args.ToArray());
            default:
                throw Error($"no esperaba '{text}'");
        }
    }

    private bool Peek(string kind) => _pos < _tokens.Count && _tokens[_pos].Kind == kind;

    private bool Accept(string kind, string text)
    {
        if (_pos < _tokens.Count && _tokens[_pos].Kind == kind && _tokens[_pos].Text == text) { _pos++; return true; }
        return false;
    }

    private void Expect(string kind)
    {
        if (!Accept(kind, kind)) throw Error($"falta '{kind}'");
    }

    private RuleSyntaxException Error(string what) => new($"Regla inválida ({what}): {_source}");

    private static List<(string, string)> Tokenize(string s)
    {
        var tokens = new List<(string, string)>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c is '(' or ')' or ',') { tokens.Add((c.ToString(), c.ToString())); i++; continue; }
            if (c is '=' or '!' or '<' or '>')
            {
                var two = i + 1 < s.Length ? s.Substring(i, 2) : "";
                if (two is "==" or "!=" or "<=" or ">=") { tokens.Add(("op", two)); i += 2; continue; }
                if (c is '<' or '>') { tokens.Add(("op", c.ToString())); i++; continue; }
                throw new RuleSyntaxException($"Regla inválida (símbolo '{c}'): {s}");
            }
            if (c is '\'' or '"')
            {
                var end = s.IndexOf(c, i + 1);
                if (end < 0) throw new RuleSyntaxException($"Regla inválida (texto sin cerrar): {s}");
                tokens.Add(("str", s.Substring(i + 1, end - i - 1)));
                i = end + 1;
                continue;
            }
            if (char.IsDigit(c) || (c == '-' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
            {
                var start = i++;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                tokens.Add(("num", s[start..i]));
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                var sb = new StringBuilder();
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '.')) sb.Append(s[i++]);
                var word = sb.ToString();
                tokens.Add(word is "and" or "or" or "not" or "true" or "false" ? ("kw", word) : ("id", word));
                continue;
            }
            throw new RuleSyntaxException($"Regla inválida (carácter '{c}'): {s}");
        }
        return tokens;
    }
}
