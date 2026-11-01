using System.Globalization;
using System.Text;

namespace PCDoctor.Core.Knowledge;

/// <summary>
/// Intérprete de nivel 1 (sin IA generativa): relaciona lo que escribe la persona con las fichas de la
/// biblioteca usando las frases coloquiales de cada ficha, sinónimos y pesos por rareza (IDF).
/// Funciona en cualquier PC, incluso de 4 GB. El LLM (fase 5) se apoya en este mismo resultado.
/// </summary>
public sealed class SymptomMatcher
{
    public const double Threshold = 0.34;

    private static readonly Dictionary<string, string> Synonyms = BuildSynonyms(new()
    {
        ["pc"] = ["compu", "computadora", "computador", "ordenador", "maquina", "pc", "notebook", "laptop", "portatil", "equipo", "netbook", "windows"],
        ["lento"] = ["lenta", "lento", "lentitud", "lentisima", "lentisimo", "tarda", "tardar", "demora", "tilda", "tildada", "tildado", "colgada", "colgado", "cuelga", "traba", "trabada", "trabado", "congela", "congelada", "pesada", "pesado", "lag", "lenteja", "eternidad", "siglos", "despacio", "lentisima"],
        ["internet"] = ["internet", "wifi", "wi", "fi", "conexion", "conectar", "conecta", "navegar", "navegador", "paginas", "pagina", "web", "router", "modem", "red", "online"],
        ["abre"] = ["abre", "abren", "abrir", "abrio", "inicia", "iniciar", "ejecuta", "ejecutar", "carga", "cargar"],
        ["sonido"] = ["sonido", "audio", "parlantes", "parlante", "altavoz", "altavoces", "volumen", "escucho", "escucha", "suena", "oigo", "oye", "oir", "auriculares", "mudo", "silencio"],
        ["imprimir"] = ["impresora", "imprime", "imprimir", "impresion", "imprima"],
        ["virus"] = ["virus", "malware", "troyano", "hackeado", "hackearon", "hackeo", "infectada", "infectado", "spyware", "ransomware", "amenaza"],
        ["teclado"] = ["teclado", "teclas", "tecla", "tipeo", "tipear"],
        ["escribir"] = ["escribe", "escribir", "escribo", "sale", "salen", "aparece"],
        ["letra"] = ["letra", "letras", "texto", "textos", "fuente", "chico", "chica", "chiquito", "chiquita", "chiquitas", "pequena", "pequeno", "diminuta", "grande", "agrandar", "achicar"],
        ["pantalla"] = ["pantalla", "monitor", "imagen", "display", "pantallas"],
        ["apaga"] = ["apaga", "apago", "reinicia", "reinicio", "reinicios", "apagones"],
        ["azul"] = ["pantallazo", "azul", "celeste", "bsod", "pantallazos"],
        ["espacio"] = ["espacio", "lleno", "llena", "almacenamiento", "disco", "lugar"],
        ["bateria"] = ["bateria", "cargador", "enchufada", "dura"],
        ["juego"] = ["juego", "juegos", "jugar", "steam", "fps", "gaming", "gamer"],
        ["word"] = ["word", "winword"],
        ["excel"] = ["excel"],
        ["arroba"] = ["arroba"],
        ["enie"] = ["enie", "ene"], // «eñe», el nombre de la letra ñ, queda «ene» al quitar el tilde
        ["acento"] = ["acento", "acentos", "tilde", "tildes"],
        ["hora"] = ["hora", "fecha", "reloj", "atrasada", "adelantada"],
        ["microfono"] = ["microfono", "mic", "microfonos"],
        ["camara"] = ["camara", "webcam"],
        ["mouse"] = ["mouse", "raton", "puntero", "cursor", "touchpad", "panel"],
        ["actualizar"] = ["actualizar", "actualizacion", "actualizaciones", "update"],
        ["caliente"] = ["caliente", "calienta", "temperatura", "ventilador", "ruido"],
        ["contrasena"] = ["contrasena", "clave", "password", "pin"],
        ["estafa"] = ["llamaron", "llamo", "llamada", "estafa", "estafaron", "soporte", "anydesk", "teamviewer"],
        ["prender"] = ["prendo", "prende", "prender", "prendes", "encender", "enciendo", "enciende", "arranca", "arrancar", "arranque"],
        ["video"] = ["video", "videos", "pelicula", "peliculas", "youtube", "netflix"],
        ["activar"] = ["activar", "activado", "activada", "activacion", "genuino", "genuina", "original"],
        ["mensajes"] = ["mensajes", "mensaje", "whatsapp", "chat"],
        ["celular"] = ["celular", "celulares", "telefono", "telefonos", "movil", "iphone", "android", "smartphone"],
        ["borrar"] = ["borrar", "borre", "borro", "borra", "borraron", "borrado", "borrada", "borrados", "borradas", "eliminar", "elimine", "elimino", "elimina", "eliminaron", "eliminado", "eliminada"],
        ["publicidad"] = ["publicidad", "anuncios", "propaganda", "notificaciones", "ventanas"],
    });

    private static readonly HashSet<string> StopWords =
    [
        "de", "la", "el", "los", "las", "un", "una", "unos", "unas", "y", "o", "me", "mi", "mis", "se", "que",
        "en", "con", "por", "para", "no", "lo", "le", "les", "al", "del", "es", "esta", "estan", "muy", "re",
        "tengo", "tiene", "hay", "puedo", "puede", "nada", "cuando", "como", "ya", "a", "mas", "pero", "sin",
        "anda", "funciona", "funcionan", "andan", "va", "sirve", "ni", "nunca", "siempre", "todo", "todos",
        "eso", "esto", "este", "ese", "su", "sus", "si", "porque", "cada", "vez", "ahora", "hace", "dice",
        "algo", "otra", "otro", "mucho", "mucha", "poco", "bien", "mal", "solo", "sola", "quiero", "necesito",
        "ayuda", "problema", "ser", "son", "fue", "estoy", "deja", "dejo", "dejar", "anduvo",
        "desde", "hasta", "despues", "antes", "aunque", "tambien",
    ];

    private readonly List<(ProblemDef Problem, List<HashSet<string>> Phrases)> _index = new();
    private readonly Dictionary<string, double> _idf = new();

    public SymptomMatcher(KnowledgeBase kb)
    {
        var docFreq = new Dictionary<string, int>();
        var totalDocs = 0;
        foreach (var p in kb.Problems)
        {
            var phrases = p.Frases.Append(p.Titulo.Sencillo).Select(Tokenize).Where(t => t.Count > 0).ToList();
            _index.Add((p, phrases));
            foreach (var set in phrases)
            {
                totalDocs++;
                foreach (var t in set) docFreq[t] = docFreq.GetValueOrDefault(t) + 1;
            }
        }
        foreach (var (t, df) in docFreq) _idf[t] = Math.Log(1 + (double)totalDocs / df);
    }

    public IReadOnlyList<(ProblemDef Problem, double Score)> Match(string text, int max = 5)
    {
        var query = Tokenize(text);
        if (query.Count == 0) return [];
        var results = new List<(ProblemDef, double)>();
        foreach (var (problem, phrases) in _index)
        {
            var best = phrases.Select(ph => Similarity(query, ph)).DefaultIfEmpty(0).Max();
            if (best >= Threshold) results.Add((problem, best));
        }
        return results.OrderByDescending(r => r.Item2).Take(max).ToList();
    }

    private double Weight(string token) => _idf.GetValueOrDefault(token, 1.0);

    private double Similarity(HashSet<string> q, HashSet<string> p)
    {
        var inter = q.Where(p.Contains).Sum(Weight);
        if (inter == 0) return 0;
        var nq = Math.Sqrt(q.Sum(Weight));
        var np = Math.Sqrt(p.Sum(Weight));
        return inter / (nq * np);
    }

    public static HashSet<string> Tokenize(string text)
    {
        var prepared = text.ToLowerInvariant()
            .Replace("@", " arroba ")
            .Replace(" ñ ", " enie ")
            .Replace("la ñ", "la enie")
            .Replace("wi-fi", "wifi");
        if (prepared.Trim() == "ñ") prepared = "enie";
        var sb = new StringBuilder();
        foreach (var c in prepared.Normalize(NormalizationForm.FormD))
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        // Expresiones que cambian de sentido según el contexto («no se oye» = sonido; «no me oye» = micrófono).
        var normalized = " " + System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ") + " ";
        foreach (var (pattern, replacement) in Idioms) normalized = pattern.Replace(normalized, replacement);
        // «carga» es «cargar la batería» si se habla del cargador o la batería; si no, es «la página carga» (abrir).
        if (ChargingContext.IsMatch(normalized)) normalized = ChargingVerb.Replace(normalized, " bateria ");
        var tokens = new HashSet<string>();
        foreach (var raw in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (StopWords.Contains(raw)) continue;
            if (Synonyms.TryGetValue(raw, out var canon)) { tokens.Add(canon); continue; }
            var stem = Stem(raw);
            if (Synonyms.TryGetValue(stem, out canon)) { tokens.Add(canon); continue; }
            if (stem.Length >= 2) tokens.Add(stem);
        }
        return tokens;
    }

    /// <summary>Expresiones del español cotidiano que se reemplazan por su sentido antes de separar palabras (texto ya sin acentos).</summary>
    private static readonly (System.Text.RegularExpressions.Regex pattern, string replacement)[] Idioms =
    [
        (Rx(@" no me (oye|oyen|escucha|escuchan) "), " microfono "),
        (Rx(@" (no )?(oyen|escuchan) lo que digo "), " microfono "),
        (Rx(@" no me (ve|ven|pueden ver|puede ver) "), " camara "),
        (Rx(@" (se )?(pone|ponga|puso|ponen) al dia | ponerse al dia "), " actualizar "),
        (Rx(@" (carita|cara|carita) triste "), " pantallazo "),
        (Rx(@" copia (no )?original | no es original "), " activar "),
        // «me dijo por teléfono» habla de una llamada, no del aparato (lo distingue de «el internet del teléfono»).
        (Rx(@" por (telefono|celular) "), " llamada "),
        // «el símbolo del mail / de los correos» es la arroba.
        (Rx(@" (simbolo|signo) (del?|de los|de las) (mail|mails|email|emails|correo|correos)( electronicos?)? "), " arroba "),
        // «la notebook se descarga» es la batería; «descargar un archivo» es bajarlo de Internet.
        (Rx(@" (notebook|laptop|portatil|bateria|pila) se (descarga|descargo|descargan) "), " bateria dura poco "),
    ];

    private static readonly System.Text.RegularExpressions.Regex ChargingContext = Rx(@" (cargador|cargadores|bateria|baterias|enchufad[oa]|enchufe) ");
    private static readonly System.Text.RegularExpressions.Regex ChargingVerb = Rx(@" (carga|cargar|cargando|cargue|cargo) ");

    private static System.Text.RegularExpressions.Regex Rx(string p) =>
        new(p, System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string Stem(string w)
    {
        if (w.Length > 5 && w.EndsWith("es")) return w[..^2];
        if (w.Length > 4 && w.EndsWith('s')) return w[..^1];
        return w;
    }

    private static Dictionary<string, string> BuildSynonyms(Dictionary<string, string[]> groups)
    {
        var map = new Dictionary<string, string>();
        foreach (var (canon, words) in groups)
            foreach (var w in words) map[w] = canon;
        return map;
    }
}
