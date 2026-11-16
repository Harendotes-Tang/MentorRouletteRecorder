using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;

namespace MentorRecorder.Collector.Ipc;

/// <summary>
/// The three character profile messages, kept out of <see cref="MessageDispatcher"/>. They answer
/// against the host the dispatcher owns and hold no state of their own.
///
/// Every answer carries the complete list of profiles with their progress, so the client shows the
/// result without a second round trip; <c>stats_invalidated</c> is published for a change that
/// actually happened, because the dashboard of every other window reads the same profiles.
/// </summary>
public static class CharacterProfileHandlers
{
    /// <summary>Answers <c>CreateCharacterProfile</c>.</summary>
    /// <param name="host">Open collector host.</param>
    /// <param name="requestId">Envelope request id; the idempotency key.</param>
    /// <param name="reader">Request payload.</param>
    public static JsonObject Create(CollectorHost host, string requestId, PayloadReader reader)
    {
        ArgumentNullException.ThrowIfNull(host);
        return Render(host, host.Profiles.Create(RequestParsers.CreateCharacterProfile(requestId, reader)));
    }

    /// <summary>Answers <c>UpdateCharacterProfile</c>.</summary>
    /// <param name="host">Open collector host.</param>
    /// <param name="requestId">Envelope request id; the idempotency key.</param>
    /// <param name="reader">Request payload.</param>
    public static JsonObject Update(CollectorHost host, string requestId, PayloadReader reader)
    {
        ArgumentNullException.ThrowIfNull(host);
        return Render(host, host.Profiles.Update(RequestParsers.UpdateCharacterProfile(requestId, reader)));
    }

    /// <summary>Answers <c>DeleteCharacterProfile</c>.</summary>
    /// <param name="host">Open collector host.</param>
    /// <param name="requestId">Envelope request id; the idempotency key.</param>
    /// <param name="reader">Request payload.</param>
    public static JsonObject Delete(CollectorHost host, string requestId, PayloadReader reader)
    {
        ArgumentNullException.ThrowIfNull(host);
        return Render(host, host.Profiles.Delete(RequestParsers.DeleteCharacterProfile(requestId, reader)));
    }

    private static JsonObject Render(CollectorHost host, CharacterProfileOutcome outcome)
    {
        if (!outcome.IdempotentReplay)
        {
            host.LiveEvents.PublishStatsInvalidated("角色档案已更新，统计需要重新查询。");
        }

        return new JsonObject
        {
            ["profile_id"] = outcome.ProfileId,
            ["region"] = EnumWire<Region>.Format(outcome.Region),
            ["deleted"] = outcome.Profile is null,
            ["profiles"] = new JsonArray(
                host.Statistics.GetProfiles().Select(value => (JsonNode?)Wire.Profile(value)).ToArray()),
            ["idempotent_replay"] = outcome.IdempotentReplay,
        };
    }
}
