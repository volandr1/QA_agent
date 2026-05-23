using System.Text;
using QaAgent.Core.Models;

namespace QA_agent.QaAgent.Core.Services;

/// <summary>
/// Generates a self-contained HTML report from a RunRecord.
/// No external dependencies — all CSS and JS are inlined.
/// </summary>
public static class HtmlReportGenerator
{
    public static string Generate(RunRecord run)
    {
        var sb = new StringBuilder();

        var passRate    = run.PassRate;
        var healthColor = passRate >= 70 ? "#16a34a" : passRate >= 40 ? "#d97706" : "#dc2626";
        var healthLabel = passRate >= 70 ? "Healthy"  : passRate >= 40 ? "Degraded" : "Critical";

        var failed  = run.Results.Where(r => r.Status is "Failed" or "Error").ToList();
        var passed  = run.Results.Where(r => r.Status == "Passed").ToList();
        var skipped = run.Results.Where(r => r.Status == "Skipped").ToList();

        // ── Head + CSS (plain string — no interpolation, avoids }} conflict) ──
        sb.Append("""
<!DOCTYPE html>
<html lang="uk">
<head>
<meta charset="UTF-8"/>
<meta name="viewport" content="width=device-width,initial-scale=1"/>
<style>
*{box-sizing:border-box;margin:0;padding:0}
body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;background:#f8fafc;color:#1e293b;font-size:14px;line-height:1.6}
.wrap{max-width:1100px;margin:0 auto;padding:32px 20px}
h1{font-size:1.7rem;font-weight:700;margin-bottom:4px}
.meta{color:#64748b;font-size:.85rem;margin-bottom:24px}
.meta span{margin-right:16px}
.stats{display:grid;grid-template-columns:repeat(auto-fit,minmax(120px,1fr));gap:12px;margin-bottom:28px}
.stat{background:#fff;border:1px solid #e2e8f0;border-radius:10px;padding:16px;text-align:center;box-shadow:0 1px 3px rgba(0,0,0,.06)}
.stat-val{font-size:2rem;font-weight:700;line-height:1}
.stat-lbl{font-size:.75rem;color:#64748b;margin-top:4px;text-transform:uppercase;letter-spacing:.05em}
.green{color:#16a34a}.red{color:#dc2626}.yellow{color:#d97706}.gray{color:#94a3b8}
.card{background:#fff;border:1px solid #e2e8f0;border-radius:10px;overflow:hidden;margin-bottom:20px;box-shadow:0 1px 3px rgba(0,0,0,.06)}
table{width:100%;border-collapse:collapse}
th{background:#f1f5f9;text-align:left;padding:9px 14px;font-size:.78rem;font-weight:600;color:#475569;text-transform:uppercase;letter-spacing:.04em;border-bottom:1px solid #e2e8f0}
td{padding:9px 14px;border-bottom:1px solid #f1f5f9;font-size:.85rem;vertical-align:top}
tr:last-child td{border-bottom:none}
.row-fail{background:#fff5f5}
.row-err{background:#fffbeb}
.row-pass{background:#f0fdf4}
.tag{display:inline-block;padding:2px 8px;border-radius:4px;font-size:.75rem;font-weight:500}
.tag-fail{background:#fee2e2;color:#991b1b}
.tag-err{background:#fef3c7;color:#92400e}
.tag-pass{background:#dcfce7;color:#166534}
.http{font-family:monospace;font-weight:600}
.ep{font-family:monospace;font-size:.8rem;color:#3b82f6}
.err-txt{color:#7f1d1d;font-size:.8rem;word-break:break-word;max-width:380px}
.ai-box{background:#fff;border:1px solid #e2e8f0;border-left:4px solid #6366f1;border-radius:10px;padding:20px;margin-bottom:20px;line-height:1.8;box-shadow:0 1px 3px rgba(0,0,0,.06)}
.code-hdr{background:#1e293b;color:#94a3b8;padding:10px 16px;border-radius:10px 10px 0 0;display:flex;align-items:center;justify-content:space-between;font-size:.8rem;cursor:pointer;user-select:none}
.code-hdr .ep{color:#7dd3fc}
.code-hdr .tog{color:#64748b;font-size:.75rem}
pre{background:#0f172a;color:#e2e8f0;padding:16px;overflow-x:auto;border-radius:0 0 10px 10px;font-size:.78rem;line-height:1.6;max-height:400px;overflow-y:auto;margin:0}
.section-title{font-size:1rem;font-weight:600;margin:24px 0 10px;display:flex;align-items:center;gap:8px}
.count{background:#e2e8f0;color:#475569;border-radius:999px;padding:1px 8px;font-size:.75rem}
.ok{color:#16a34a;background:#f0fdf4;border:1px solid #bbf7d0;border-radius:8px;padding:12px 16px;margin-bottom:16px}
footer{text-align:center;color:#94a3b8;font-size:.8rem;margin-top:40px;padding-top:20px;border-top:1px solid #e2e8f0}
.badge{display:inline-block;padding:3px 12px;border-radius:999px;font-weight:600;font-size:.8rem;color:#fff}
</style>
</head>
<body>
<div class="wrap">
""");

        // ── Header ────────────────────────────────────────────────────────────
        sb.AppendLine($"""
<h1>&#129514; QA Agent Report</h1>
<div class="meta">
  <span>&#128225; <strong>{Esc(run.ApiTitle ?? "API")}</strong></span>
  <span>&#128279; {Esc(run.BaseUrl)}</span>
  <span>&#129302; {Esc(run.ModelName)}</span>
  <span>&#128336; {run.RunAt:yyyy-MM-dd HH:mm} UTC</span>
  <span>&#9200; {run.TotalDurationSeconds:F1}s</span>
  <span><span class="badge" style="background:{healthColor}">{healthLabel}</span></span>
</div>
""");

        // ── Stats ─────────────────────────────────────────────────────────────
        sb.AppendLine($"""
<div class="stats">
  <div class="stat"><div class="stat-val">{run.Total}</div><div class="stat-lbl">Total</div></div>
  <div class="stat"><div class="stat-val green">{run.Passed}</div><div class="stat-lbl">Passed</div></div>
  <div class="stat"><div class="stat-val red">{run.Failed}</div><div class="stat-lbl">Failed</div></div>
  <div class="stat"><div class="stat-val yellow">{run.Errors}</div><div class="stat-lbl">Errors</div></div>
  <div class="stat"><div class="stat-val gray">{run.Skipped}</div><div class="stat-lbl">Skipped</div></div>
  <div class="stat"><div class="stat-val" style="color:{healthColor}">{passRate:F1}%</div><div class="stat-lbl">Pass Rate</div></div>
</div>
""");

        // ── Failed & Error ────────────────────────────────────────────────────
        if (failed.Count == 0)
        {
            sb.AppendLine("""<div class="ok">&#9989; Всі тести пройшли — проблем не виявлено!</div>""");
        }
        else
        {
            sb.AppendLine($"""
<div class="section-title">&#128027; Провалені тести <span class="count">{failed.Count}</span></div>
<div class="card"><table>
<thead><tr><th>Endpoint</th><th>Сценарій</th><th>Статус</th><th>HTTP</th><th>Помилка</th></tr></thead>
<tbody>
""");
            foreach (var r in failed)
            {
                var rowCss  = r.Status == "Error" ? "row-err" : "row-fail";
                var tagCss  = r.Status == "Error" ? "tag-err" : "tag-fail";
                var httpVal = r.ActualStatus.HasValue
                    ? $"""<span class="http" style="color:{(r.ActualStatus >= 500 ? "#dc2626" : "#d97706")}">{r.ActualStatus}</span>"""
                    : "<span style='color:#94a3b8'>&#8212;</span>";
                var err = Esc(r.ErrorMessage ?? "");
                if (err.Length > 250) err = err[..250] + "&#8230;";

                sb.AppendLine($"""
<tr class="{rowCss}">
  <td><span class="ep">{Esc(r.EndpointLabel)}</span></td>
  <td>{Esc(r.ScenarioName)}</td>
  <td><span class="tag {tagCss}">{r.Status}</span></td>
  <td>{httpVal}</td>
  <td><span class="err-txt">{err}</span></td>
</tr>
""");
            }
            sb.AppendLine("</tbody></table></div>");
        }

        // ── Passed ────────────────────────────────────────────────────────────
        if (passed.Count > 0)
        {
            sb.AppendLine($"""
<div class="section-title">&#9989; Пройдені тести <span class="count">{passed.Count}</span></div>
<div class="card"><table>
<thead><tr><th>Endpoint</th><th>Сценарій</th><th style="text-align:right">Час</th></tr></thead>
<tbody>
""");
            foreach (var r in passed)
                sb.AppendLine($"""
<tr class="row-pass">
  <td><span class="ep">{Esc(r.EndpointLabel)}</span></td>
  <td>{Esc(r.ScenarioName)}</td>
  <td style="text-align:right;color:#64748b">{r.DurationMs:F0}ms</td>
</tr>
""");
            sb.AppendLine("</tbody></table></div>");
        }

        // ── Skipped ───────────────────────────────────────────────────────────
        if (skipped.Count > 0)
        {
            sb.AppendLine($"""
<div class="section-title">&#9197; Пропущені <span class="count">{skipped.Count}</span></div>
<div class="card"><table>
<thead><tr><th>Endpoint</th><th>Причина</th></tr></thead><tbody>
""");
            foreach (var r in skipped)
                sb.AppendLine($"<tr><td><span class=\"ep\">{Esc(r.EndpointLabel)}</span></td><td style='color:#64748b'>{Esc(r.ErrorMessage ?? "")}</td></tr>");
            sb.AppendLine("</tbody></table></div>");
        }

        // ── AI Analysis ───────────────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(run.AiAnalysis))
        {
            sb.AppendLine($"""
<div class="section-title">&#129302; AI Аналіз</div>
<div class="ai-box">{Esc(run.AiAnalysis)}</div>
""");
        }

        // ── Generated Code ────────────────────────────────────────────────────
        if (run.GeneratedFiles.Count > 0)
        {
            sb.AppendLine($"""<div class="section-title">&#128196; Згенерований код <span class="count">{run.GeneratedFiles.Count} файл(ів)</span></div>""");

            foreach (var file in run.GeneratedFiles)
            {
                var safeId = SanitizeId(file.EndpointLabel);
                var lines  = file.Code.Split('\n').Length;

                sb.AppendLine($"""
<div style="margin-bottom:16px">
  <div class="code-hdr" onclick="toggle('{safeId}')">
    <span class="ep">{Esc(file.EndpointLabel)}</span>
    <span class="tog" id="tog_{safeId}">{lines} рядків — показати [+]</span>
  </div>
  <pre id="pre_{safeId}" style="display:none"><code>{Esc(file.Code)}</code></pre>
</div>
""");
            }
        }

        // ── Footer + JS (plain string — no interpolation) ─────────────────────
        sb.AppendLine($"<footer>Звіт згенеровано QA Agent &middot; {run.RunAt:yyyy-MM-dd HH:mm} UTC &middot; Run ID: {run.Id}</footer>");

        sb.Append("""
</div>
<script>
function toggle(id) {
  var pre = document.getElementById('pre_' + id);
  var tog = document.getElementById('tog_' + id);
  var lines = (pre.innerText || '').split('\n').length;
  if (pre.style.display === 'none') {
    pre.style.display = 'block';
    tog.textContent = lines + ' рядків — згорнути [-]';
  } else {
    pre.style.display = 'none';
    tog.textContent = lines + ' рядків — показати [+]';
  }
}
</script>
</body>
</html>
""");

        return sb.ToString();
    }

    private static string Esc(string s) =>
        s.Replace("&", "&amp;")
         .Replace("<", "&lt;")
         .Replace(">", "&gt;")
         .Replace("\"", "&quot;");

    private static string SanitizeId(string label) =>
        new string(label.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
}
