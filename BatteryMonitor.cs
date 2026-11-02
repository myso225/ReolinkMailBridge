using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReolinkMailBridge.Baichuan;

namespace ReolinkMailBridge;

/// <summary>
/// Reads each camera's battery over Baichuan/UDP: shortly after an alert email, while the camera is awake anyway,
/// and as a fallback when the last reading is older than <see cref="BridgeOptions.BatteryFallbackHours"/>.
/// Reads run one at a time; cameras without Ip, Uid and Password are skipped.
/// </summary>
public sealed class BatteryMonitor(
    IOptions<BridgeOptions> options,
    ActivityStore store,
    IAlertSink sink,
    ILogger<BatteryMonitor> logger) : BackgroundService
{
    static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);
    static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(60);

    /// <summary>A burst of alerts from one camera gives one read; a failed read may be retried sooner.</summary>
    static readonly TimeSpan MinGap = TimeSpan.FromMinutes(2);
    static readonly TimeSpan MinRetryGap = TimeSpan.FromSeconds(30);

    readonly BridgeOptions _o = options.Value;
    readonly Channel<CameraOptions> _queue = Channel.CreateUnbounded<CameraOptions>();
    readonly ConcurrentDictionary<string, DateTimeOffset> _lastAttempt = new();

    public void AfterAlert(string cameraId)
    {
        if (_o.Cameras.FirstOrDefault(c => c.Key == cameraId && c.CanReadBattery) is not { } camera) return;
        _ = Task.Delay(TimeSpan.FromSeconds(_o.BatteryReadDelaySeconds)).ContinueWith(_ => _queue.Writer.TryWrite(camera));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var camera in _o.Cameras.Where(c => !c.CanReadBattery))
            logger.LogInformation("Battery of {Camera} is not read: set its Ip, Uid and Password", camera.Name);
        var cameras = _o.Cameras.Where(c => c.CanReadBattery).ToList();
        if (cameras.Count == 0) return;

        _ = FallbackLoopAsync(cameras, stoppingToken);
        await foreach (var camera in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            var now = DateTimeOffset.Now;
            if (now - _lastAttempt.GetValueOrDefault(camera.Key) < MinRetryGap
                || now - store.Get(camera.Key)?.BatteryUpdated < MinGap) continue;
            _lastAttempt[camera.Key] = DateTimeOffset.Now;
            await ReadAsync(camera, stoppingToken);
        }
    }

    async Task FallbackLoopAsync(List<CameraOptions> cameras, CancellationToken ct)
    {
        var fallback = TimeSpan.FromHours(_o.BatteryFallbackHours);
        using var timer = new PeriodicTimer(CheckInterval);
        try
        {
            do
            {
                foreach (var camera in cameras)
                {
                    var updated = store.Get(camera.Key)?.BatteryUpdated;
                    var attempted = _lastAttempt.GetValueOrDefault(camera.Key);
                    var last = updated > attempted ? updated.Value : attempted;
                    if (DateTimeOffset.Now - last > fallback)
                        _queue.Writer.TryWrite(camera);
                }
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
    }

    async Task ReadAsync(CameraOptions camera, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ReadTimeout);
        try
        {
            await using var client = await BcClient.ConnectAsync(IPAddress.Parse(camera.Ip!), camera.Uid!, logger, timeout.Token);
            await client.LoginAsync(camera.Username, camera.Password!, timeout.Token);
            var battery = await client.GetBatteryAsync(0, timeout.Token);

            var state = store.RecordBattery(camera.Key, camera.Name, battery);
            logger.LogInformation("Battery of {Camera}: {Percent} %, {Charge}, {Temperature} °C, {Voltage} mV",
                camera.Name, battery.Percent, battery.ChargeStatus, battery.Temperature, battery.Voltage);
            await sink.StateAsync(state, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Reading battery of {Camera} failed: {Type}: {Message}", camera.Name, ex.GetType().Name, ex.Message);
        }
    }
}
