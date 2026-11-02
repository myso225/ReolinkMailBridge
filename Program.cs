using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ReolinkMailBridge;

if (args is ["battery-probe", ..])
    return await BatteryProbe.RunAsync(args);

var builder = Host.CreateApplicationBuilder(args);

// Machine-specific settings (camera IPs and UIDs), not committed; placed right after appsettings.json so environment
// variables and the command line still override it.
var sources = builder.Configuration.Sources;
var local = new JsonConfigurationSource
    { Path = "appsettings.Local.json", Optional = true, FileProvider = builder.Environment.ContentRootFileProvider };
sources.Insert(sources.ToList().FindLastIndex(s => s is JsonConfigurationSource) + 1, local);

builder.Services.Configure<BridgeOptions>(builder.Configuration.GetSection("Bridge"));
builder.Services.AddSingleton(Channel.CreateUnbounded<CameraAlert>());
builder.Services.AddSingleton<ActivityStore>();

// The sink is also a hosted service: MQTT keeps its connection, the console prints the state at start.
if (builder.Configuration.GetValue("Bridge:Mqtt:Enabled", true))
    builder.Services.AddSingleton<MqttPublisher>().AddSingleton<IAlertSink>(sp => sp.GetRequiredService<MqttPublisher>())
        .AddHostedService(sp => sp.GetRequiredService<MqttPublisher>());
else
    builder.Services.AddSingleton<ConsoleAlertSink>().AddSingleton<IAlertSink>(sp => sp.GetRequiredService<ConsoleAlertSink>())
        .AddHostedService(sp => sp.GetRequiredService<ConsoleAlertSink>());

builder.Services.AddHostedService<SmtpListener>();
builder.Services.AddHostedService<AlertProcessor>();
builder.Services.AddSingleton<BatteryMonitor>().AddHostedService(sp => sp.GetRequiredService<BatteryMonitor>());

await builder.Build().RunAsync();
return 0;
