namespace ReolinkMailBridge;

public sealed class BridgeOptions
{
    public SmtpOptions Smtp { get; set; } = new();
    public MqttOptions Mqtt { get; set; } = new();
    public List<CameraOptions> Cameras { get; set; } = [];

    /// <summary>Seconds a detection binary_sensor stays ON after an alert (HA off_delay).</summary>
    public int MotionOffDelaySeconds { get; set; } = 30;

    /// <summary>Folder for activity.json and each camera's last picture.</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>Window of the "Motions (N h)" counter.</summary>
    public double ActivityWindowHours { get; set; } = 8;

    /// <summary>Folder for raw .eml copies of received alerts. Empty disables saving.</summary>
    public string SaveMailDirectory { get; set; } = "data/mail";
    public int MaxSavedMails { get; set; } = 200;

    /// <summary>Delay after an alert email before the battery is read, so the camera has finished sending.</summary>
    public int BatteryReadDelaySeconds { get; set; } = 5;

    /// <summary>Battery is also read when the last reading is older than this; each such read wakes the camera.</summary>
    public double BatteryFallbackHours { get; set; } = 6;
}

public sealed class SmtpOptions
{
    public int Port { get; set; } = 25;
    public string ServerName { get; set; } = "reolink-mail-bridge";

    /// <summary>When set, the camera must log in with exactly these credentials; otherwise any login is accepted.</summary>
    public string? Username { get; set; }
    public string? Password { get; set; }

    public int MaxMessageSizeBytes { get; set; } = 20 * 1024 * 1024;
}

public sealed class MqttOptions
{
    /// <summary>When false, alerts are only written to the console (for testing camera emails).</summary>
    public bool Enabled { get; set; } = true;
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string ClientId { get; set; } = "reolink-mail-bridge";
    public string BaseTopic { get; set; } = "reolink";
    public string DiscoveryPrefix { get; set; } = "homeassistant";
}

public sealed class CameraOptions
{
    /// <summary>Name as the camera writes it in alert emails, e.g. "Garage".</summary>
    public string Name { get; set; } = "";

    /// <summary>Optional sender address configured on this camera; matched before the name.</summary>
    public string? SenderAddress { get; set; }

    public string? Model { get; set; }

    /// <summary>MQTT/entity id; derived from Name when empty.</summary>
    public string? Id { get; set; }

    public string Key => Id is { Length: > 0 } id ? id : AlertParser.Slug(Name);

    /// <summary>Camera LAN address and UID (Reolink app → Device Info), needed only for reading the battery.</summary>
    public string? Ip { get; set; }
    public string? Uid { get; set; }

    /// <summary>Camera login; set the password through Bridge__Cameras__N__Password, not in appsettings.json.</summary>
    public string Username { get; set; } = "admin";
    public string? Password { get; set; }

    public bool CanReadBattery => !string.IsNullOrEmpty(Ip) && !string.IsNullOrEmpty(Uid) && !string.IsNullOrEmpty(Password);
}
