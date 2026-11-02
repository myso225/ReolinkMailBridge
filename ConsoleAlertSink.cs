using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ReolinkMailBridge;

/// <summary>Used instead of <see cref="MqttPublisher"/> when MQTT is disabled: prints what would be published.</summary>
public sealed class ConsoleAlertSink(ActivityStore store, ILogger<ConsoleAlertSink> logger) : IAlertSink, IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("MQTT disabled, alerts are printed only");
        foreach (var state in store.All())
            Print(state);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task AlertAsync(CameraAlert alert, CameraState state, CancellationToken ct)
    {
        Console.WriteLine($"""
            ===== ALERT =====
              Camera:   {alert.CameraName} (id {alert.CameraId})
              Type:     {alert.Type} ("{alert.RawEvent}")
              Time:     {alert.Time:yyyy-MM-dd HH:mm:ss zzz}
              Picture:  {(alert.Snapshot is null ? "none" : $"{alert.Snapshot.Length} bytes -> {store.PictureFile(alert.CameraId)}")}
            """);
        Print(state);
        return Task.CompletedTask;
    }

    public Task StateAsync(CameraState state, CancellationToken ct)
    {
        Print(state);
        return Task.CompletedTask;
    }

    static void Print(CameraState s) => Console.WriteLine(
        $"  [{s.Name}] last motion {s.LastMotion?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-"} ({s.LastType ?? "-"}), " +
        $"motions ({s.WindowHours:0.#} h): {s.Motions}, battery: {(s.Battery is { } b ? $"{b} % ({s.ChargeStatus}, {s.BatteryTemperature} °C, {s.BatteryUpdated:HH:mm})" : "unknown")}, " +
        $"picture: {s.PictureTaken?.ToString("yyyy-MM-dd HH:mm:ss") ?? "none"}");
}
