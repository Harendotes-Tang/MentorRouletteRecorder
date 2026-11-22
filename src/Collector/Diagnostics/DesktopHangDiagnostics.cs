using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace MentorRecorder.Collector.Diagnostics;

internal sealed record DesktopHangEvent(
    int EventId, DateTimeOffset RecordedAtUtc, Version? DesktopVersion,
    uint? ImageTimestamp, ushort? Signature, Guid? ReportId);

internal sealed record DesktopHangReading(bool Available, DesktopHangEvent? Latest = null)
{
    internal const int LookbackDays = 7;
    internal const string DesktopImage = "MentorRecorder.Desktop.exe";
    internal const string ApplicationHang = "Application Hang";
    internal const string WindowsErrorReporting = "Windows Error Reporting";
    internal const string AppHangB1 = "AppHangB1";
    internal const int HangEventId = 1002;
    internal const int WerEventId = 1001;

    /// <summary>Validates event fields and keeps the most recent distinct hang.</summary>
    internal static DesktopHangReading FromEvents(IEnumerable<string> events, DateTimeOffset now)
    {
        var earliest = now.AddDays(-LookbackDays);
        var matches = events.Select(Parse).OfType<DesktopHangEvent>()
            .Where(e => e.RecordedAtUtc >= earliest && e.RecordedAtUtc <= now)
            .ToArray();
        var joined = matches.Where(e => e.ReportId.HasValue).GroupBy(e => e.ReportId)
            .Select(Merge);
        return new DesktopHangReading(true, joined.Concat(matches.Where(e => !e.ReportId.HasValue))
            .MaxBy(e => e.RecordedAtUtc));
    }

    private static DesktopHangEvent Merge(IEnumerable<DesktopHangEvent> events)
    {
        var hang = events.Where(e => e.EventId == HangEventId).MaxBy(e => e.RecordedAtUtc);
        var wer = events.Where(e => e.EventId == WerEventId).MaxBy(e => e.RecordedAtUtc);
        if (hang is null)
            return wer!;

        return hang with
        {
            DesktopVersion = hang.DesktopVersion ?? wer?.DesktopVersion,
            ImageTimestamp = wer?.ImageTimestamp,
            Signature = wer?.Signature,
        };
    }

    private static DesktopHangEvent? Parse(string xml)
    {
        // Event XML comes from the bounded Win32 render buffer. Parsing XML keeps the
        // provider keys independent of the installed Windows display language.
        XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
        using var reader = XmlReader.Create(new StringReader(xml));
        var root = XDocument.Load(reader).Root;
        var system = root?.Element(ns + "System");
        var provider = (string?)system?.Element(ns + "Provider")?.Attribute("Name");
        var time = (string?)system?.Element(ns + "TimeCreated")?.Attribute("SystemTime");
        if (!int.TryParse((string?)system?.Element(ns + "EventID"), out var id) ||
            !DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var recordedAt))
        {
            return null;
        }

        var fields = root?.Element(ns + "EventData")?.Elements(ns + "Data").ToArray() ?? [];
        string? Read(string name) => fields.FirstOrDefault(f => (string?)f.Attribute("Name") == name)?.Value;
        // Older Application Hang events used the same fields in positional order.
        string? ReadHangField(string name, int position) => Read(name) ??
            (fields.ElementAtOrDefault(position) is { } field && field.Attribute("Name") is null ? field.Value : null);
        var isWer = id == WerEventId && provider == WindowsErrorReporting && Read("EventName") == AppHangB1;
        var isHang = id == HangEventId && provider == ApplicationHang;
        if ((!isHang && !isWer) ||
            !string.Equals(isWer ? Read("P1") : ReadHangField("AppName", 0), DesktopImage, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        Version.TryParse(isWer ? Read("P2") : ReadHangField("AppVersion", 1), out var version);
        var reportId = Guid.TryParse(isWer ? Read("ReportId") : ReadHangField("ReportId", 6), out var parsedId)
            && parsedId != Guid.Empty
            ? parsedId : (Guid?)null;
        var timestamp = isWer && Read("P3") is { Length: 8 } image &&
            uint.TryParse(image, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var stamp)
            ? stamp : (uint?)null;
        var signature = isWer && Read("P4") is { Length: 4 } hangSignature &&
            ushort.TryParse(hangSignature, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var sig)
            ? sig : (ushort?)null;
        return new DesktopHangEvent(id, recordedAt, version, timestamp, signature, reportId);
    }
}
