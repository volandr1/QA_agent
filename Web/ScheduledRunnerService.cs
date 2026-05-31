using QA_agent.QaAgent.Core.Services;
using QaAgent.Core.Models;

namespace QA_agent.Web;

/// <summary>
/// Background service that automatically triggers test runs on a configurable interval.
/// Runs as a hosted service alongside the Blazor web UI.
/// </summary>
public sealed class ScheduledRunnerService : BackgroundService
{
    private readonly QaRunnerService  _runner;
    private readonly ILogger<ScheduledRunnerService> _logger;

    public ScheduledRunnerService(QaRunnerService runner, ILogger<ScheduledRunnerService> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("[Scheduler] Started. Waiting for first scheduled run...");

        while (!ct.IsCancellationRequested)
        {
            // Reload config fresh each iteration so changes from Settings UI take effect
            var cfg = await ConfigService.LoadAsync();

            if (!cfg.Schedule.IsConfigured)
            {
                // Not configured — check again in 5 minutes
                await Task.Delay(TimeSpan.FromMinutes(5), ct);
                continue;
            }

            var interval = TimeSpan.FromHours(Math.Max(1, cfg.Schedule.IntervalHours));
            _logger.LogInformation("[Scheduler] Next run in {Hours}h — {Url}", cfg.Schedule.IntervalHours, cfg.Schedule.SwaggerUrl);

            await Task.Delay(interval, ct);

            if (ct.IsCancellationRequested) break;

            // Reload config again right before run (user may have updated it during the wait)
            cfg = await ConfigService.LoadAsync();

            if (!cfg.Schedule.IsConfigured) continue;

            _logger.LogInformation("[Scheduler] 🚀 Launching scheduled run for {Url}", cfg.Schedule.SwaggerUrl);

            var request = new RunRequest(
                SwaggerUrl:   cfg.Schedule.SwaggerUrl,
                BaseUrl:      cfg.Schedule.BaseUrl,
                Model:        cfg.Ollama.Model,
                Auth:         cfg.Auth.IsConfigured ? cfg.Auth : null,
                SelectedTags: null);

            var started = _runner.TryStart(request);

            if (!started)
                _logger.LogWarning("[Scheduler] Skipped — a run is already in progress.");
            else
                _logger.LogInformation("[Scheduler] ✅ Scheduled run started.");
        }

        _logger.LogInformation("[Scheduler] Stopped.");
    }
}
