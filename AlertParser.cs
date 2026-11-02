using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MimeKit;

namespace ReolinkMailBridge;

public enum DetectionType { Motion, Person, Vehicle, Animal }

public sealed record CameraAlert(
    string CameraId,
    string CameraName,
    string? Model,
    DetectionType Type,
    string RawEvent,
    DateTimeOffset Time,
    byte[]? Snapshot,
    string? SnapshotContentType);

/// <summary>
/// Turns a Reolink alert email into a <see cref="CameraAlert"/>. Argus Eco Pro (fw v3.0.0.2951) sends
/// subject "Person Detected from Garage at 2026/11/2 12:50:08", body lines "Alarm Camera Name:Garage",
/// "Alarm Event:Person Detected", "Alarm Device Name:Garage" and a JPEG attachment, with no Date header.
/// Other firmware words things differently, so every field has a fallback.
/// </summary>
public static partial class AlertParser
{
    /// <summary>The message the Reolink app makes the camera send when email settings are saved or tested.</summary>
    public static bool IsTestMail(MimeMessage message) =>
        (message.Subject ?? "").Contains("Test e-mail", StringComparison.OrdinalIgnoreCase)
        || (message.TextBody ?? "").Contains("successfully set up and tested", StringComparison.OrdinalIgnoreCase);

    public static CameraAlert Parse(MimeMessage message, IReadOnlyList<CameraOptions> cameras)
    {
        var body = message.TextBody ?? StripHtml(message.HtmlBody) ?? "";
        var subject = message.Subject ?? "";
        var sender = message.From.Mailboxes.FirstOrDefault();

        var nameInMail = Field(body, "Camera Name") ?? Field(body, "Device Name") ?? SubjectCameraName(subject);
        var camera = cameras.FirstOrDefault(c => !string.IsNullOrEmpty(c.SenderAddress)
                         && string.Equals(c.SenderAddress, sender?.Address, StringComparison.OrdinalIgnoreCase))
                     ?? cameras.FirstOrDefault(c => nameInMail is not null
                         && string.Equals(c.Name, nameInMail, StringComparison.OrdinalIgnoreCase))
                     ?? cameras.FirstOrDefault(c => subject.Contains(c.Name, StringComparison.OrdinalIgnoreCase));

        var name = camera?.Name ?? nameInMail ?? sender?.Name ?? sender?.LocalPart ?? "Unknown";
        var rawEvent = Field(body, "Event") ?? subject;
        var time = ParseTime(Field(body, "Time")) ?? ParseTime(SubjectTime(subject))
                   ?? (message.Date != default ? message.Date : DateTimeOffset.Now);

        var image = message.BodyParts.OfType<MimePart>().FirstOrDefault(p => p.ContentType.MediaType.Equals("image", StringComparison.OrdinalIgnoreCase));
        byte[]? snapshot = null;
        if (image?.Content is not null)
        {
            using var ms = new MemoryStream();
            image.Content.DecodeTo(ms);
            snapshot = ms.ToArray();
        }

        return new CameraAlert(
            camera?.Key ?? Slug(name),
            name,
            camera?.Model,
            Classify(rawEvent + " " + subject),
            rawEvent.Trim(),
            time,
            snapshot,
            image?.ContentType.MimeType);
    }

    public static DetectionType Classify(string text)
    {
        var t = text.ToLowerInvariant();
        if (t.Contains("person") || t.Contains("people") || t.Contains("human")) return DetectionType.Person;
        if (t.Contains("vehicle")) return DetectionType.Vehicle;
        if (t.Contains("animal") || t.Contains("pet")) return DetectionType.Animal;
        return DetectionType.Motion;
    }

    public static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_');
        }
        var slug = MultiUnderscore().Replace(sb.ToString(), "_").Trim('_');
        return slug.Length > 0 ? slug : "camera";
    }

    /// <summary>Finds a "Label: value" line; an optional "Alarm " prefix is allowed before the label.</summary>
    static string? Field(string body, string label)
    {
        var m = Regex.Match(body, @"^\s*(?:Alarm\s+)?" + Regex.Escape(label) + @"\s*[:：]\s*(.+?)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value : null;
    }

    static string? SubjectCameraName(string subject)
    {
        var m = Regex.Match(subject, @"\bfrom\s+(.+?)(?:\s+at\s+\d{4}/.*)?\s*$", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    static string? SubjectTime(string subject)
    {
        var m = Regex.Match(subject, @"\bat\s+(\d{4}/\d{1,2}/\d{1,2}\s+\d{1,2}:\d{2}:\d{2})\s*$", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    static DateTimeOffset? ParseTime(string? value)
    {
        if (value is null) return null;
        string[] formats = ["yyyy/M/d H:mm:ss", "yyyy-M-d H:mm:ss", "d/M/yyyy H:mm:ss", "M/d/yyyy H:mm:ss"];
        return DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt)
            ? new DateTimeOffset(dt)
            : null;
    }

    static string? StripHtml(string? html) =>
        html is null ? null : WebTags().Replace(BreakTags().Replace(html, "\n"), "");

    [GeneratedRegex("_+")] private static partial Regex MultiUnderscore();
    [GeneratedRegex(@"<br\s*/?>|</p>|</div>|</tr>", RegexOptions.IgnoreCase)] private static partial Regex BreakTags();
    [GeneratedRegex("<[^>]+>")] private static partial Regex WebTags();
}
