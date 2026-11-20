using System.Text.Json.Nodes;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Contracts.Texts;
using MentorRecorder.Collector.Domain.Time;

namespace MentorRecorder.Collector.Ipc;

/// <summary>
/// <c>SetDisplayTimeZone</c>: the Desktop's 显示时区 (<see cref="DisplayTimeZone"/>), sent after every
/// connection and whenever the player picks another zone. Process state, never stored: a Collector
/// that restarts follows the computer's zone until the Desktop tells it again.
///
/// Like every message it is reachable only through the per-user pipe ACL; it needs no further
/// authorization class, because it changes neither the database nor anything outside this process,
/// only how days are cut in the answers to later queries.
/// </summary>
public static class DisplayTimeZoneHandlers
{
    /// <summary>Handles <c>SetDisplayTimeZone</c>.</summary>
    /// <param name="reader">Request payload: <c>time_zone</c>, an IANA id or null for the computer's zone.</param>
    public static JsonObject Set(PayloadReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        reader.RejectUnknown("time_zone");
        if (!reader.Has("time_zone"))
        {
            throw CollectorException.BadRequest(CollectorTexts.IpcFieldMissing("time_zone"), "time_zone");
        }

        var id = reader.String("time_zone", DisplayTimeZone.MaxIdLength);
        TimeZoneInfo zone;
        try
        {
            zone = DisplayTimeZone.Set(id);
        }
        catch (TimeZoneNotFoundException)
        {
            throw CollectorException.BadRequest(CollectorTexts.IpcRequestTimeZoneUnknown(), "time_zone");
        }

        var now = DateTimeOffset.UtcNow;
        return new JsonObject
        {
            // The id as it was put in force, not a later request's from another connection.
            ["time_zone"] = string.IsNullOrWhiteSpace(id) ? null : id.Trim(),
            ["utc_offset_minutes"] = (int)zone.GetUtcOffset(now).TotalMinutes,
        };
    }
}
