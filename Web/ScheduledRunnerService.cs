using QA_agent.QaAgent.Core.Services;
using QaAgent.Core.Models;

namespace QA_agent.Web;

/// <summary>
/// Background service that monitors the API spec for new or previously-failed endpoints
/// and automatically tests only those — skipping endpoints that already passed.
///
/// Behaviour:
///   • First run  → tests ALL endpoints in the spec.
///   • Next runs  → compares spec with previous run results:
///       - New endpoint added by the developer → tested automatically.
///       - Previously failed endpoint          → retested.
///       - Already passed endpoint             → skipped (no duplicate work).
///   • If nothing new or failed → logs "Nothing to test" and waits for next interval.
/// </summary>
public sealed class ScheduledRunnerService : BackgroundService
{
    private readonly QaRunnerService _runner;
    private readonly ILogger<ScheduledRunnerService> _logger;

    public ScheduledRunnerService(QaRunnerService runner, ILogger<ScheduledRunnerService> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("[Scheduler] Background monitor started.");

        // Small startup delay so the web UI is fully ready before first check
        await Task.Delay(TimeSpan.FromSeconds(10), ct);

        while (!ct.IsCancellationRequested)
        {
            // Always reload config fresh — picks up any changes saved via Settings UI
            var cfg = await ConfigService.LoadAsync();

            if (!cfg.Schedule.IsConfigured)
            {
                // Scheduling not configured — check again in 5 minutes
                await Task.Delay(TimeSpan.FromMinutes(5), ct);
                continue;
            }

            var interval = TimeSpan.FromHours(Math.Max(1, cfg.Schedule.IntervalHours));

            _logger.LogInformation(
                "[Scheduler] Checking for new/failed endpoints in {Url} (every {H}h)",
                cfg.Schedule.SwaggerUrl, cfg.Schedule.IntervalHours);

            var request = new RunRequest(
                SwaggerUrl:          cfg.Schedule.SwaggerUrl,
                BaseUrl:             cfg.Schedule.BaseUrl,
                Model:               cfg.Ollama.Model,
                Auth:                cfg.Auth.IsConfigured ? cfg.Auth : null,
                SelectedTags:        null,
                SkipPassedEndpoints: true);   // ← KEY: only test new/failed endpoints

            if (_runner.IsRunning)
            {
                _logger.LogWarning("[Scheduler] Skipped — a run is already in progress.");
            }
            else
            {
                var started = _runner.TryStart(request);
                if (started)
                    _logger.LogInformation("[Scheduler] ✅ Smart run started (new/failed endpoints only).");
            }

            // Wait for the next check interval
            _logger.LogInformation("[Scheduler] Next check in {H} hour(s).", cfg.Schedule.IntervalHours);
            await Task.Delay(interval, ct);
        }

        _logger.LogInformation("[Scheduler] Stopped.");
    }
}
