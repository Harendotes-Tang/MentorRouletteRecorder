using System.Text.Json;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.Capture;

/// <summary>The last player round, with the consented profile and its durable capture state.</summary>
internal sealed record CandidatePlayerRound(string Id, string ProfileId, DateTimeOffset StartedAtUtc, bool Active)
{
    public const string Setting = "capture.candidate_player_round";

    public static CandidatePlayerRound? Read(SettingsRepository? settings, SqliteTransaction? transaction = null)
    {
        if (settings?.GetSetting(Setting, transaction) is not { } json) return null;
        try
        {
            var round = JsonSerializer.Deserialize<CandidatePlayerRound>(json);
            return round is { ProfileId.Length: > 0 } && Guid.TryParseExact(round.Id, "D", out _)
                ? round : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
