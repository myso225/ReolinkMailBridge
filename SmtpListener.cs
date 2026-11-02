using System.Buffers;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using SmtpServer;
using SmtpServer.Authentication;
using SmtpServer.Protocol;
using SmtpServer.Storage;

namespace ReolinkMailBridge;

/// <summary>Runs a plain SMTP server the cameras send their alert emails to.</summary>
public sealed class SmtpListener(
    IOptions<BridgeOptions> options,
    Channel<CameraAlert> alerts,
    ILogger<SmtpListener> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        var serverOptions = new SmtpServerOptionsBuilder()
            .ServerName(o.Smtp.ServerName)
            .MaxMessageSize(o.Smtp.MaxMessageSizeBytes)
            // Cameras log in over plain SMTP (SSL/TLS off in the Reolink app), so AUTH must work unencrypted.
            .Endpoint(e => e.Port(o.Smtp.Port).AllowUnsecureAuthentication(true).AuthenticationRequired(false))
            .Build();

        var services = new SmtpServer.ComponentModel.ServiceProvider();
        services.Add(new AlertStore(o, alerts, logger));
        services.Add(new CameraAuthenticator(o.Smtp, logger));

        logger.LogInformation("SMTP listening on port {Port}", o.Smtp.Port);
        await new SmtpServer.SmtpServer(serverOptions, services).StartAsync(stoppingToken);
    }

    sealed class CameraAuthenticator(SmtpOptions smtp, ILogger logger) : UserAuthenticator
    {
        public override Task<bool> AuthenticateAsync(ISessionContext context, string user, string password, CancellationToken cancellationToken)
        {
            var ok = string.IsNullOrEmpty(smtp.Username)
                     || (user == smtp.Username && password == (smtp.Password ?? ""));
            if (!ok) logger.LogWarning("Rejected SMTP login for {User}", user);
            return Task.FromResult(ok);
        }
    }

    sealed class AlertStore(BridgeOptions o, Channel<CameraAlert> alerts, ILogger logger) : MessageStore
    {
        public override async Task<SmtpResponse> SaveAsync(ISessionContext context, IMessageTransaction transaction,
            ReadOnlySequence<byte> buffer, CancellationToken cancellationToken)
        {
            try
            {
                var raw = buffer.ToArray();
                SaveRaw(raw);

                using var stream = new MemoryStream(raw);
                var message = await MimeMessage.LoadAsync(stream, cancellationToken);
                logger.LogInformation("Mail from {From}, subject \"{Subject}\", parts [{Parts}]\n{Body}",
                    message.From, message.Subject,
                    string.Join(", ", message.BodyParts.Select(p => p.ContentType.MimeType)),
                    (message.TextBody ?? message.HtmlBody ?? "").Trim());
                if (AlertParser.IsTestMail(message))
                {
                    logger.LogInformation("Test email from {From}, not treated as an alert", message.From);
                    return SmtpResponse.Ok;
                }

                var alert = AlertParser.Parse(message, o.Cameras);
                logger.LogInformation("Alert from {Camera} ({Id}): {Type} \"{Raw}\" at {Time}, snapshot {Bytes} bytes",
                    alert.CameraName, alert.CameraId, alert.Type, alert.RawEvent, alert.Time, alert.Snapshot?.Length ?? 0);

                await alerts.Writer.WriteAsync(alert, cancellationToken);
                return SmtpResponse.Ok;
            }
            catch (Exception ex)
            {
                // Still accept the mail: a camera retrying an unparseable message helps nobody.
                logger.LogError(ex, "Failed to process alert email");
                return SmtpResponse.Ok;
            }
        }

        void SaveRaw(byte[] raw)
        {
            if (string.IsNullOrWhiteSpace(o.SaveMailDirectory)) return;
            try
            {
                Directory.CreateDirectory(o.SaveMailDirectory);
                File.WriteAllBytes(Path.Combine(o.SaveMailDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.eml"), raw);

                foreach (var old in new DirectoryInfo(o.SaveMailDirectory).GetFiles("*.eml")
                             .OrderByDescending(f => f.Name).Skip(o.MaxSavedMails))
                    old.Delete();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not save raw email");
            }
        }
    }
}
