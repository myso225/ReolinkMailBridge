using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReolinkMailBridge.Baichuan;

namespace ReolinkMailBridge;

/// <summary>What the bridge knows about a camera, as published on <c>reolink/&lt;cam&gt;/state</c>.</summary>
public sealed record CameraState(
    string Id,
    string Name,
    string? Model,
    DateTimeOffset? LastMotion,
    string? LastType,
    int Motions,
    double WindowHours,
    int? Battery,
    DateTimeOffset? BatteryUpdated,
    string? ChargeStatus,
    string? AdapterStatus,
    int? BatteryTemperature,
    int? BatteryVoltage,
    DateTimeOffset? PictureTaken);

/// <summary>
/// Per-camera activity kept in <c>activity.json</c>, plus the last picture as <c>&lt;id&gt;.jpg</c>, both in
/// <see cref="BridgeOptions.DataDirectory"/>, so times, counters and pictures survive a restart.
/// </summary>
public sealed class ActivityStore
{
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    sealed class Entry
    {
        public string Name { get; set; } = "";
        public string? Model { get; set; }
        public DateTimeOffset? LastMotion { get; set; }
        public string? LastType { get; set; }
        /// <summary>Alert times within the window; older ones are dropped by <see cref="Prune"/>.</summary>
        public List<DateTimeOffset> Motions { get; set; } = [];
        public int? Battery { get; set; }
        public DateTimeOffset? BatteryUpdated { get; set; }
        public string? ChargeStatus { get; set; }
        public string? AdapterStatus { get; set; }
        public int? BatteryTemperature { get; set; }
        public int? BatteryVoltage { get; set; }
        public DateTimeOffset? PictureTaken { get; set; }
    }

    readonly string _dir;
    readonly ILogger<ActivityStore> _logger;
    readonly Dictionary<string, Entry> _cameras;
    readonly Lock _lock = new();

    public ActivityStore(IOptions<BridgeOptions> options, ILogger<ActivityStore> logger)
    {
        _dir = options.Value.DataDirectory;
        Window = TimeSpan.FromHours(options.Value.ActivityWindowHours);
        _logger = logger;
        _cameras = Load();

        // Configured cameras exist from the start, so Home Assistant sees them before their first alert.
        foreach (var cam in options.Value.Cameras)
        {
            var e = GetOrAdd(cam.Key, cam.Name);
            e.Model = cam.Model ?? e.Model;
        }
        Prune(DateTimeOffset.Now);
    }

    public TimeSpan Window { get; }

    string ActivityPath => Path.Combine(_dir, "activity.json");
    string PicturePath(string id) => Path.Combine(_dir, $"{id}.jpg");

    public IReadOnlyList<CameraState> All()
    {
        lock (_lock) return _cameras.Select(kv => ToState(kv.Key, kv.Value)).ToList();
    }

    public CameraState Record(CameraAlert alert)
    {
        lock (_lock)
        {
            var e = GetOrAdd(alert.CameraId, alert.CameraName);
            e.Model = alert.Model ?? e.Model;
            e.LastMotion = alert.Time;
            e.LastType = alert.Type.ToString().ToLowerInvariant();
            e.Motions.Add(alert.Time);
            if (alert.Snapshot is not null)
            {
                e.PictureTaken = alert.Time;
                TryWrite(PicturePath(alert.CameraId), alert.Snapshot);
            }
            PruneEntry(e, DateTimeOffset.Now);
            Save();
            return ToState(alert.CameraId, e);
        }
    }

    public CameraState RecordBattery(string id, string name, BatteryInfo battery)
    {
        lock (_lock)
        {
            var e = GetOrAdd(id, name);
            e.Battery = battery.Percent;
            e.BatteryUpdated = DateTimeOffset.Now;
            e.ChargeStatus = battery.ChargeStatus;
            e.AdapterStatus = battery.AdapterStatus;
            e.BatteryTemperature = battery.Temperature;
            e.BatteryVoltage = battery.Voltage;
            Save();
            return ToState(id, e);
        }
    }

    public CameraState? Get(string id)
    {
        lock (_lock) return _cameras.TryGetValue(id, out var e) ? ToState(id, e) : null;
    }

    /// <summary>Drops alerts older than the window; returns the cameras whose counter changed.</summary>
    public IReadOnlyList<CameraState> Prune(DateTimeOffset now)
    {
        lock (_lock)
        {
            var changed = _cameras.Where(kv => PruneEntry(kv.Value, now)).Select(kv => ToState(kv.Key, kv.Value)).ToList();
            if (changed.Count > 0) Save();
            return changed;
        }
    }

    public byte[]? Picture(string id)
    {
        var path = PicturePath(id);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public string PictureFile(string id) => Path.GetFullPath(PicturePath(id));

    Entry GetOrAdd(string id, string name)
    {
        if (!_cameras.TryGetValue(id, out var e))
            _cameras[id] = e = new Entry();
        e.Name = name;
        return e;
    }

    bool PruneEntry(Entry e, DateTimeOffset now) => e.Motions.RemoveAll(t => now - t > Window) > 0;

    CameraState ToState(string id, Entry e) => new(id, e.Name, e.Model, e.LastMotion, e.LastType, e.Motions.Count,
        Window.TotalHours, e.Battery, e.BatteryUpdated, e.ChargeStatus, e.AdapterStatus, e.BatteryTemperature, e.BatteryVoltage,
        e.PictureTaken);

    Dictionary<string, Entry> Load()
    {
        try
        {
            if (File.Exists(ActivityPath))
                return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(ActivityPath)) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read {Path}, starting empty", ActivityPath);
        }
        return [];
    }

    void Save() => TryWrite(ActivityPath, JsonSerializer.SerializeToUtf8Bytes(_cameras, JsonOptions));

    void TryWrite(string path, byte[] data)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllBytes(path, data);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write {Path}", path);
        }
    }
}
