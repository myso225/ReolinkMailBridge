using System.Net;
using Microsoft.Extensions.Logging;
using ReolinkMailBridge.Baichuan;

namespace ReolinkMailBridge;

/// <summary>
/// <c>battery-probe &lt;ip&gt; &lt;uid&gt; [user]</c>: logs in to one camera over UDP and prints its version and battery.
/// The password is asked for on the console, or read from REOLINK_PASSWORD.
/// </summary>
public static class BatteryProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("Usage: battery-probe <camera ip> <camera uid> [username, default admin]");
            return 2;
        }
        var ip = IPAddress.Parse(args[1]);
        var uid = args[2];
        var user = args.Length > 3 ? args[3] : "admin";
        var password = Environment.GetEnvironmentVariable("REOLINK_PASSWORD") ?? ReadPassword($"Password for {user}@{uid}: ");

        using var loggerFactory = LoggerFactory.Create(b => b
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; })
            .SetMinimumLevel(LogLevel.Debug));
        var logger = loggerFactory.CreateLogger("Probe");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        try
        {
            logger.LogInformation("Connecting to {Uid} at {Ip} over UDP...", uid, ip);
            await using var client = await BcClient.ConnectAsync(ip, uid, logger, cts.Token);

            var deviceInfo = await client.LoginAsync(user, password, cts.Token);
            logger.LogInformation("Logged in. DeviceInfo:\n{Xml}", deviceInfo);

            var version = await client.GetVersionAsync(cts.Token);
            logger.LogInformation("Version:\n{Xml}", version);

            var battery = await client.GetBatteryAsync(0, cts.Token);
            Console.WriteLine($"""

                ===== BATTERY =====
                  Percent:     {battery.Percent}
                  Charging:    {battery.ChargeStatus}
                  Adapter:     {battery.AdapterStatus}
                  Temperature: {battery.Temperature}
                  Voltage:     {battery.Voltage}
                  Low power:   {battery.LowPower}
                ===================
                """);
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogError("Probe failed: {Type}: {Message}", ex.GetType().Name, ex.Message);
            return 1;
        }
    }

    static string ReadPassword(string prompt)
    {
        Console.Write(prompt);
        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); }
            else chars.Add(key.KeyChar);
        }
        Console.WriteLine();
        return new string([.. chars]);
    }
}
