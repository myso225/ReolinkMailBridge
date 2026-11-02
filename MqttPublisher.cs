using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;

namespace ReolinkMailBridge;

/// <summary>
/// MQTT sink with Home Assistant discovery, laid out like EufyNet. Per camera:
/// <c>{base}/{cam}/state</c> (JSON, retained), <c>{base}/{cam}/image</c> (JPEG, retained),
/// <c>{base}/{cam}/event</c> (JSON per alert, not retained, for Node-RED) and
/// <c>{base}/{cam}/{motion|person|vehicle|animal}</c> (<c>ON</c>, not retained).
/// After every (re)connect the state and pictures are published again, so retained topics survive a broker restart.
/// </summary>
public sealed class MqttPublisher(
    IOptions<BridgeOptions> options,
    ActivityStore store,
    ILogger<MqttPublisher> logger) : BackgroundService, IAlertSink
{
    static readonly string[] DetectionKinds = ["motion", "person", "vehicle", "animal"];

    readonly BridgeOptions _o = options.Value;
    readonly IMqttClient _client = new MqttClientFactory().CreateMqttClient();
    readonly SemaphoreSlim _connectLock = new(1, 1);
    readonly ConcurrentDictionary<string, bool> _announced = new();

    string StatusTopic => $"{_o.Mqtt.BaseTopic}/bridge/status";
    string Topic(string id, string leaf) => $"{_o.Mqtt.BaseTopic}/{id}/{leaf}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _client.ApplicationMessageReceivedAsync += async e =>
        {
            // HA publishes "online" on homeassistant/status after a restart; discovery must be resent then.
            if (e.ApplicationMessage.Topic == $"{_o.Mqtt.DiscoveryPrefix}/status"
                && e.ApplicationMessage.ConvertPayloadToString() == "online")
            {
                logger.LogInformation("Home Assistant came online, re-sending discovery");
                _announced.Clear();
                await PublishAllAsync(stoppingToken);
            }
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ConnectAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("MQTT connect to {Host}:{Port} failed: {Message}", _o.Mqtt.Host, _o.Mqtt.Port, ex.Message);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) { }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_client.IsConnected)
        {
            await PublishAsync(StatusTopic, "offline", retain: true, cancellationToken);
            await _client.DisconnectAsync(cancellationToken: cancellationToken);
        }
        await base.StopAsync(cancellationToken);
    }

    public async Task AlertAsync(CameraAlert alert, CameraState state, CancellationToken ct)
    {
        await WithRetryAsync(async () =>
        {
            await AnnounceAsync(state, ct);
            if (alert.Snapshot is not null)
                await PublishAsync(Topic(state.Id, "image"), alert.Snapshot, retain: true, ct);
            await PublishAsync(Topic(state.Id, "state"), StateJson(state), retain: true, ct);

            var evt = new JsonObject
            {
                ["camera"] = alert.CameraName,
                ["type"] = alert.Type.ToString().ToLowerInvariant(),
                ["raw"] = alert.RawEvent,
                ["time"] = alert.Time.ToString("o"),
                ["has_picture"] = alert.Snapshot is not null,
            };
            await PublishAsync(Topic(state.Id, "event"), evt.ToJsonString(), retain: false, ct);

            // Every alert is motion; a smart detection additionally triggers its own sensor.
            await PublishAsync(Topic(state.Id, "motion"), "ON", retain: false, ct);
            if (alert.Type != DetectionType.Motion)
                await PublishAsync(Topic(state.Id, alert.Type.ToString().ToLowerInvariant()), "ON", retain: false, ct);
        }, ct);
        logger.LogInformation("Published {Type} alert of {Camera}, motions ({Hours} h): {Motions}",
            alert.Type, state.Name, state.WindowHours, state.Motions);
    }

    public Task StateAsync(CameraState state, CancellationToken ct) =>
        WithRetryAsync(() => PublishAsync(Topic(state.Id, "state"), StateJson(state), retain: true, ct), ct);

    async Task WithRetryAsync(Func<Task> publish, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await ConnectAsync(ct);
                await publish();
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && attempt < 12)
            {
                logger.LogWarning("MQTT publish failed ({Message}), retrying", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    async Task ConnectAsync(CancellationToken ct)
    {
        if (_client.IsConnected) return;
        await _connectLock.WaitAsync(ct);
        try
        {
            if (_client.IsConnected) return;

            var builder = new MqttClientOptionsBuilder()
                .WithTcpServer(_o.Mqtt.Host, _o.Mqtt.Port)
                .WithClientId(_o.Mqtt.ClientId)
                .WithCleanSession()
                .WithWillTopic(StatusTopic)
                .WithWillPayload("offline")
                .WithWillRetain()
                .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
            if (!string.IsNullOrEmpty(_o.Mqtt.Username))
                builder.WithCredentials(_o.Mqtt.Username, _o.Mqtt.Password);

            await _client.ConnectAsync(builder.Build(), ct);
            logger.LogInformation("Connected to MQTT {Host}:{Port}, topics under {Base}/", _o.Mqtt.Host, _o.Mqtt.Port, _o.Mqtt.BaseTopic);

            await _client.SubscribeAsync($"{_o.Mqtt.DiscoveryPrefix}/status", cancellationToken: ct);
            await PublishAsync(StatusTopic, "online", retain: true, ct);
            _announced.Clear();
            await PublishAllAsync(ct);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    async Task PublishAllAsync(CancellationToken ct)
    {
        foreach (var state in store.All())
        {
            await AnnounceAsync(state, ct);
            await PublishAsync(Topic(state.Id, "state"), StateJson(state), retain: true, ct);
            if (store.Picture(state.Id) is { } jpeg)
                await PublishAsync(Topic(state.Id, "image"), jpeg, retain: true, ct);
        }
    }

    static string StateJson(CameraState s) => new JsonObject
    {
        ["camera"] = s.Name,
        ["model"] = s.Model,
        ["last_motion"] = s.LastMotion?.ToString("o"),
        ["last_type"] = s.LastType,
        ["motions"] = s.Motions,
        ["window_hours"] = s.WindowHours,
        ["battery"] = s.Battery,
        ["battery_updated"] = s.BatteryUpdated?.ToString("o"),
        ["charging"] = s.ChargeStatus is null ? null : s.ChargeStatus == "charging",
        ["charge_status"] = s.ChargeStatus,
        ["adapter"] = s.AdapterStatus,
        ["battery_temperature"] = s.BatteryTemperature,
        ["battery_voltage"] = s.BatteryVoltage,
        ["picture_taken"] = s.PictureTaken?.ToString("o"),
    }.ToJsonString();

    async Task AnnounceAsync(CameraState s, CancellationToken ct)
    {
        if (!_announced.TryAdd(s.Id, true)) return;

        var device = new JsonObject
        {
            ["identifiers"] = new JsonArray($"reolink_mail_{s.Id}"),
            ["name"] = s.Name,
            ["manufacturer"] = "Reolink",
            ["model"] = s.Model ?? "Battery camera",
        };
        var stateTopic = Topic(s.Id, "state");

        JsonObject Entity(string objectId, string name) => new()
        {
            ["name"] = name,
            ["unique_id"] = $"reolink_mail_{s.Id}_{objectId}",
            ["availability_topic"] = StatusTopic,
            ["device"] = device.DeepClone(),
        };

        JsonObject StateSensor(string objectId, string name, string field)
        {
            var e = Entity(objectId, name);
            e["state_topic"] = stateTopic;
            e["value_template"] = $"{{{{ value_json.{field} }}}}";
            return e;
        }

        foreach (var kind in DetectionKinds)
        {
            var e = Entity(kind, char.ToUpperInvariant(kind[0]) + kind[1..]);
            e["state_topic"] = Topic(s.Id, kind);
            e["device_class"] = kind == "motion" ? "motion" : "occupancy";
            e["off_delay"] = _o.MotionOffDelaySeconds;
            await DiscoverAsync("binary_sensor", s.Id, kind, e, ct);
        }

        var picture = Entity("picture", "Last picture");
        picture["image_topic"] = Topic(s.Id, "image");
        picture["content_type"] = "image/jpeg";
        await DiscoverAsync("image", s.Id, "picture", picture, ct);

        var lastMotion = StateSensor("last_motion", "Last motion", "last_motion");
        lastMotion["device_class"] = "timestamp";
        lastMotion["json_attributes_topic"] = stateTopic;
        await DiscoverAsync("sensor", s.Id, "last_motion", lastMotion, ct);

        var lastType = StateSensor("last_detection", "Last detection", "last_type");
        lastType["icon"] = "mdi:cctv";
        await DiscoverAsync("sensor", s.Id, "last_detection", lastType, ct);

        var motions = StateSensor("motions", $"Motions ({s.WindowHours:0.#} h)", "motions");
        motions["state_class"] = "measurement";
        motions["icon"] = "mdi:motion-sensor";
        await DiscoverAsync("sensor", s.Id, "motions", motions, ct);

        var battery = StateSensor("battery", "Battery", "battery");
        battery["device_class"] = "battery";
        battery["unit_of_measurement"] = "%";
        battery["state_class"] = "measurement";
        await DiscoverAsync("sensor", s.Id, "battery", battery, ct);

        var temperature = StateSensor("battery_temperature", "Battery temperature", "battery_temperature");
        temperature["device_class"] = "temperature";
        temperature["unit_of_measurement"] = "°C";
        temperature["state_class"] = "measurement";
        temperature["entity_category"] = "diagnostic";
        await DiscoverAsync("sensor", s.Id, "battery_temperature", temperature, ct);

        var charging = Entity("charging", "Charging");
        charging["state_topic"] = stateTopic;
        charging["value_template"] = "{{ 'ON' if value_json.charging else 'OFF' if value_json.charging is false else 'None' }}";
        charging["device_class"] = "battery_charging";
        await DiscoverAsync("binary_sensor", s.Id, "charging", charging, ct);

        logger.LogInformation("Announced camera {Name} ({Id}) to Home Assistant", s.Name, s.Id);
    }

    Task DiscoverAsync(string component, string id, string objectId, JsonObject config, CancellationToken ct) =>
        PublishAsync($"{_o.Mqtt.DiscoveryPrefix}/{component}/reolink_mail_{id}/{objectId}/config",
            config.ToJsonString(), retain: true, ct);

    Task PublishAsync(string topic, string payload, bool retain, CancellationToken ct) =>
        PublishAsync(topic, System.Text.Encoding.UTF8.GetBytes(payload), retain, ct);

    async Task PublishAsync(string topic, byte[] payload, bool retain, CancellationToken ct)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(retain)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
        await _client.PublishAsync(message, ct);
    }
}
