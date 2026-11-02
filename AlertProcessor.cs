using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ReolinkMailBridge;

/// <summary>Where alerts and state changes go: MQTT, or the console when MQTT is disabled.</summary>
public interface IAlertSink
{
    Task AlertAsync(CameraAlert alert, CameraState state, CancellationToken ct);
    Task StateAsync(CameraState state, CancellationToken ct);
}

/// <summary>
/// Records each alert in the <see cref="ActivityStore"/>, hands it to the sink and asks for a battery reading while the
/// camera is awake; ages out the counters.
/// </summary>
public sealed class AlertProcessor(
    Channel<CameraAlert> alerts,
    ActivityStore store,
    IAlertSink sink,
    BatteryMonitor battery,
    ILogger<AlertProcessor> logger) : BackgroundService
{
    static readonly TimeSpan PruneInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = PruneLoopAsync(stoppingToken);

        await foreach (var alert in alerts.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var state = store.Record(alert);
                await sink.AlertAsync(alert, state, stoppingToken);
                battery.AfterAlert(alert.CameraId);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Failed to handle alert from {Camera}", alert.CameraName);
            }
        }
    }

    /// <summary>Without this, a "Motions (8 h)" counter would only go down at the camera's next alert.</summary>
    async Task PruneLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PruneInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                foreach (var state in store.Prune(DateTimeOffset.Now))
                {
                    try { await sink.StateAsync(state, ct); }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        logger.LogWarning("Publishing state of {Camera} failed: {Message}", state.Name, ex.Message);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
