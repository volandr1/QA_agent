using System.Text;
using System.Text.Json;
using QaAgent.Core.Models;

namespace QA_agent.QaAgent.Core.Services;

/// <summary>
/// Відправляє Telegram-повідомлення через Bot API після завершення тестового запуску.
/// Без сторонніх залежностей — тільки HttpClient.
/// </summary>
public class TelegramNotificationService
{
    private readonly TelegramConfig _cfg;

    public TelegramNotificationService(TelegramConfig cfg)
    {
        _cfg = cfg;
    }

    /// <summary>
    /// Відправляє повідомлення якщо увімкнено і умови виконані.
    /// Повертає null при успіху або рядок з помилкою.
    /// </summary>
    public async Task<string?> SendAsync(RunRecord run, CancellationToken ct = default)
    {
        if (!_cfg.IsConfigured) return null;
        if (_cfg.OnlyOnFailure && run.Failed == 0 && run.Errors == 0) return null;

        try
        {
            var text = BuildMessage(run);
            var url  = $"https://api.telegram.org/bot{_cfg.BotToken}/sendMessage";

            var payload = JsonSerializer.Serialize(new
            {
                chat_id                  = _cfg.ChatId,
                text                     = text,
                parse_mode               = "HTML",
                disable_web_page_preview = true
            });

            using var http    = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response      = await http.PostAsync(url, content, ct);
            var body          = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                Log($"Telegram error {(int)response.StatusCode}: {body}");
                return $"Telegram API error: {body}";
            }

            Log($"Telegram sent to chat {_cfg.ChatId}");
            return null;
        }
        catch (Exception ex)
        {
            Log($"Telegram failed: {ex.Message}");
            return ex.Message;
        }
    }

    // ── Message builder ───────────────────────────────────────────────────────

    private static string BuildMessage(RunRecord run)
    {
        var icon        = run.Failed + run.Errors == 0 ? "✅" : "❌";
        var healthEmoji = run.PassRate >= 70 ? "🟢" : run.PassRate >= 40 ? "🟡" : "🔴";
        var api         = Esc(run.ApiTitle ?? run.BaseUrl);
        var runTime     = run.RunAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        var sb = new StringBuilder();
        sb.AppendLine($"{icon} <b>QA Agent Report</b>");
        sb.AppendLine();
        sb.AppendLine($"📡 <b>API:</b> {api}");
        sb.AppendLine($"🌐 <b>URL:</b> <code>{Esc(run.BaseUrl)}</code>");
        sb.AppendLine($"🕐 <b>Time:</b> {runTime}");
        sb.AppendLine($"⏱ <b>Duration:</b> {run.TotalDurationSeconds:F1}s");
        sb.AppendLine();
        sb.AppendLine($"{healthEmoji} <b>Pass Rate: {run.PassRate:F0}%</b>");
        sb.AppendLine();
        sb.AppendLine($"✅ Passed:  <b>{run.Passed}</b>");
        sb.AppendLine($"❌ Failed:  <b>{run.Failed}</b>");
        sb.AppendLine($"⚠️ Errors:  <b>{run.Errors}</b>");
        sb.AppendLine($"⏭ Skipped: <b>{run.Skipped}</b>");
        sb.AppendLine($"📊 Total:   <b>{run.Total}</b>");

        // Failed tests (max 5)
        var failed = run.Results
            .Where(r => r.Status is "Failed" or "Error")
            .Take(5)
            .ToList();

        if (failed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("🔴 <b>Failed tests:</b>");
            foreach (var t in failed)
            {
                var err = t.ErrorMessage ?? "";
                if (err.Length > 80) err = err[..80] + "…";
                sb.AppendLine($"  • <code>{Esc(t.EndpointLabel)}</code> — {Esc(t.ScenarioName)}");
                if (!string.IsNullOrWhiteSpace(err))
                    sb.AppendLine($"    <i>{Esc(err)}</i>");
            }

            if (run.Failed + run.Errors > 5)
                sb.AppendLine($"  <i>...and {run.Failed + run.Errors - 5} more</i>");
        }

        // AI analysis (first 200 chars)
        if (!string.IsNullOrWhiteSpace(run.AiAnalysis))
        {
            sb.AppendLine();
            sb.AppendLine("🤖 <b>AI:</b>");
            var analysis = run.AiAnalysis.Trim();
            if (analysis.Length > 200) analysis = analysis[..200] + "…";
            sb.AppendLine($"<i>{Esc(analysis)}</i>");
        }

        return sb.ToString().Trim();
    }

    private static string Esc(string s) =>
        s.Replace("&", "&amp;")
         .Replace("<", "&lt;")
         .Replace(">", "&gt;");

    private static void Log(string msg)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[TelegramService] {msg}");
        Console.ResetColor();
    }
}
