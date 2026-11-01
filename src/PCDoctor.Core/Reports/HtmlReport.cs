using System.Net;
using System.Text;
using PCDoctor.Core.Diagnostics;

namespace PCDoctor.Core.Reports;

/// <summary>Informe autocontenido (un solo .html): abre en cualquier navegador y se imprime a PDF con "Microsoft Print to PDF".</summary>
public static class HtmlReport
{
    public static string Render(DiagnosticReport report, bool technical)
    {
        var sb = new StringBuilder();
        string H(string s) => WebUtility.HtmlEncode(s);
        sb.Append("""
            <!doctype html><html lang="es"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Informe PC Doctor</title>
            <style>
            :root{--ok:#1b7f3b;--warn:#b26a00;--bad:#b3261e;--phys:#6a3fb5;--unk:#5f6368;--bg:#fafafa;--card:#fff;--ink:#1f1f1f;--muted:#5f6368;--line:#e3e3e3}
            body{font-family:"Segoe UI",system-ui,sans-serif;margin:0;background:var(--bg);color:var(--ink);font-size:17px;line-height:1.5}
            main{max-width:920px;margin:0 auto;padding:24px 16px}
            h1{font-size:28px;margin:0 0 4px} h2{font-size:21px;margin:28px 0 10px}
            .muted{color:var(--muted)} .card{background:var(--card);border:1px solid var(--line);border-left:6px solid var(--unk);border-radius:8px;padding:12px 16px;margin:10px 0}
            .s4{border-left-color:var(--bad)} .s3{border-left-color:var(--phys)} .s2{border-left-color:var(--warn)} .s1{border-left-color:var(--unk)} .s0{border-left-color:var(--ok)}
            .tag{display:inline-block;font-size:13px;font-weight:600;padding:1px 8px;border-radius:10px;color:#fff;background:var(--unk);margin-right:6px}
            .t4{background:var(--bad)} .t3{background:var(--phys)} .t2{background:var(--warn)} .t1{background:var(--unk)} .t0{background:var(--ok)}
            table{border-collapse:collapse;width:100%;font-size:15px} td{border-top:1px solid var(--line);padding:4px 6px;vertical-align:top} td:first-child{color:var(--muted);width:42%}
            ol{margin:6px 0 0 20px;padding:0} code{font-size:13px}
            @media print{body{background:#fff}.card{break-inside:avoid}}
            </style></head><body><main>
            """);
        sb.Append($"<h1>Informe de salud de la computadora</h1><div class=\"muted\">{H(report.MachineName)} · {report.CreatedAt:dd/MM/yyyy HH:mm}");
        if (!report.Elevated) sb.Append(" · análisis sin permiso de administrador (algunas comprobaciones no se pudieron hacer)");
        sb.Append("</div>");
        if (report.Symptom is { } sym) sb.Append($"<p>Consulta: «{H(sym)}»</p>");

        var issues = report.Issues.ToList();
        sb.Append("<h2>Resumen</h2>");
        sb.Append($"<p><span class=\"tag t{(int)report.Overall}\">{H(Labels.Status(report.Overall))}</span>");
        sb.Append($"{issues.Count(f => f.Status == HealthStatus.Problem)} problemas · {issues.Count(f => f.Status == HealthStatus.Warning)} advertencias · ");
        sb.Append($"{issues.Count(f => f.Status == HealthStatus.NeedsPhysicalCheck)} para revisión física · {issues.Count(f => f.Status == HealthStatus.Unverified)} sin poder comprobar</p>");

        void Section(string title, IEnumerable<Finding> list)
        {
            var items = list.ToList();
            if (items.Count == 0) return;
            sb.Append($"<h2>{H(title)}</h2>");
            foreach (var f in items)
            {
                sb.Append($"<div class=\"card s{(int)f.Status}\"><span class=\"tag t{(int)f.Status}\">{H(f.Kind == FindingKind.Suggestion ? "Sugerencia" : Labels.Status(f.Status))}</span>");
                sb.Append($"<strong>{H(f.Problem.Titulo.For(technical))}</strong>");
                if (f.FromSymptom) sb.Append(" <span class=\"muted\">(relacionado con lo que contaste)</span>");
                sb.Append($"<p>{H(technical ? f.TechnicalMessage : f.Message)}</p>");
                if (f.Problem.Pasos.Count > 0)
                {
                    sb.Append("<ol>");
                    foreach (var step in f.Problem.Pasos) sb.Append($"<li>{H(step.Texto)}</li>");
                    sb.Append("</ol>");
                }
                if (f.Problem.Derivar is { } d) sb.Append($"<p><em>{H(d)}</em></p>");
                if (technical)
                {
                    sb.Append($"<p class=\"muted\"><code>{H(f.Problem.Id)}</code> · madurez {H(f.Problem.Madurez)}</p>");
                    if (f.Evidence.Count > 0)
                    {
                        sb.Append("<table>");
                        foreach (var e in f.Evidence) sb.Append($"<tr><td><code>{H(e.Label)}</code></td><td>{H(e.Value)}</td></tr>");
                        sb.Append("</table>");
                    }
                    foreach (var src in f.Problem.Fuentes) sb.Append($"<div class=\"muted\">Fuente: {H(src)}</div>");
                }
                sb.Append("</div>");
            }
        }

        Section("Problemas detectados", issues.Where(f => f.Status == HealthStatus.Problem));
        Section("Requiere revisión física", issues.Where(f => f.Status == HealthStatus.NeedsPhysicalCheck));
        Section("Advertencias", issues.Where(f => f.Status == HealthStatus.Warning));
        Section("No se pudo comprobar", issues.Where(f => f.Status == HealthStatus.Unverified));
        Section("Mejoras sugeridas", report.Suggestions);

        sb.Append("<h2>Componentes revisados</h2>");
        foreach (var p in report.Probes)
        {
            var st = report.ComponentStatus(p);
            sb.Append($"<div class=\"card s{(int)st}\"><span class=\"tag t{(int)st}\">{H(Labels.Status(st))}</span><strong>{H(p.Title)}</strong>");
            if (p.Error is { } err) sb.Append($"<p>{H(err)}</p>");
            if (p.Details.Count > 0)
            {
                sb.Append("<table>");
                foreach (var d in p.Details) sb.Append($"<tr><td>{H(d.Label)}</td><td>{H(d.Value)}</td></tr>");
                sb.Append("</table>");
            }
            foreach (var u in p.Unverified) sb.Append($"<div class=\"muted\">No se pudo comprobar: {H(u)}</div>");
            if (technical) sb.Append($"<div class=\"muted\">Tiempo: {p.Duration.TotalSeconds:0.0} s</div>");
            sb.Append("</div>");
        }

        if (technical)
        {
            sb.Append("<h2>Hechos medidos</h2><table>");
            foreach (var (k, v) in report.Facts.All.OrderBy(kv => kv.Key))
                sb.Append($"<tr><td><code>{H(k)}</code></td><td>{H(FactStore.Format(v))}</td></tr>");
            sb.Append("</table>");
        }
        sb.Append("<p class=\"muted\">Generado por PC Doctor AI. Este informe separa lo comprobado de lo que no se pudo comprobar; no reemplaza una revisión física cuando se indica.</p>");
        sb.Append("</main></body></html>");
        return sb.ToString();
    }
}
