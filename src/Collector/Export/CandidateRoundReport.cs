using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Reference;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.Export;

/// <summary>One value the software read at a guessed position, with the name it stands for.</summary>
/// <param name="Id">The number read: a roulette, job or territory id.</param>
/// <param name="Name">Reference name for it, or null when the tables have none.</param>
/// <param name="ContentId">For a territory that is a duty, the duty's content id.</param>
public sealed record RoundGuess(int Id, string? Name, int? ContentId = null)
{
    /// <summary>Wire and export shape.</summary>
    public JsonObject Wire() => new() { ["id"] = Id, ["name"] = Name, ["content_id"] = ContentId };
}

/// <summary>
/// What the software takes a content round to have been, read at the candidate profile's guessed
/// positions (docs/protocol-profile-format.md 3.5.1). Every member is a guess for the player to
/// confirm or correct; none of it reaches a record.
/// </summary>
/// <param name="StartedAtUtc">When the round was switched on.</param>
/// <param name="FirstRoulette">Roulette of the first queue registration of the round.</param>
/// <param name="JobBefore">Job at the first queue registration.</param>
/// <param name="JobAfter">Job at the last registration, once there have been two.</param>
/// <param name="Duty">First zone entered after the last registration, once there have been two.</param>
public sealed record CandidateRoundReport(
    DateTimeOffset StartedAtUtc, RoundGuess? FirstRoulette, RoundGuess? JobBefore, RoundGuess? JobAfter, RoundGuess? Duty)
{
    /// <summary>Wire and export shape.</summary>
    public JsonObject Wire() => new()
    {
        ["started_at_utc"] = UtcTimestamp.ToText(StartedAtUtc),
        ["first_roulette"] = FirstRoulette?.Wire(),
        ["job_before"] = JobBefore?.Wire(),
        ["job_after"] = JobAfter?.Wire(),
        ["duty"] = Duty?.Wire(),
    };
}

/// <summary>
/// What the card showed for each item when the player finished: the guess where they left it,
/// their own words where they changed it; null where it was empty.
/// </summary>
public sealed record CandidateRoundAnswers(string? FirstRoulette, string? JobBefore, string? JobAfter, string? Duty)
{
    /// <summary>Longest answer accepted, in characters.</summary>
    public const int MaxLength = 100;

    /// <summary>Export shape.</summary>
    public JsonObject Wire() => new()
    {
        ["first_roulette"] = FirstRoulette,
        ["job_before"] = JobBefore,
        ["job_after"] = JobAfter,
        ["duty"] = Duty,
    };
}

/// <summary>
/// Builds the report from the ledger. Only the guessed bytes leave the ledger
/// (<see cref="CandidateObservationRepository.ReadGuessValues"/>), and only rows of the current
/// round are read, so an earlier round's queue is never shown as this one's.
/// </summary>
/// <param name="ledger">Candidate ledger.</param>
/// <param name="settings">Settings, for the time the round was switched on.</param>
/// <param name="profile">The candidate profile for the current client, or null.</param>
public sealed class CandidateRoundReporter(
    CandidateObservationRepository ledger, SettingsRepository settings, Func<ProtocolProfile?> profile)
{
    /// <summary>The report, or null when no round was started or the profile guesses nothing.</summary>
    public CandidateRoundReport? Read()
    {
        if (profile() is not { Status: ProfileCompatibilityStatus.Candidate } candidate ||
            settings.GetSettingUpdatedAtUtc(CaptureSettingsStore.CandidateRoundMarkSetting) is not { } started)
            return null;
        var guesses = candidate.Hypotheses
            .SelectMany(hypothesis => hypothesis.Guesses.Select(guess => (Hypothesis: hypothesis, Guess: guess)))
            .ToDictionary(pair => pair.Guess.Kind);
        if (guesses.Count == 0) return null;

        IReadOnlyList<(DateTimeOffset At, int Value)> Values(ProfileGuessKind kind) =>
            guesses.TryGetValue(kind, out var pair)
                ? ledger.ReadGuessValues(started,
                    pair.Hypothesis.Direction == PacketDirection.ServerToClient ? "S2C" : "C2S",
                    pair.Hypothesis.Opcode, pair.Hypothesis.ExpectedLength!.Value, pair.Guess.Offset, pair.Guess.Size)
                : Array.Empty<(DateTimeOffset, int)>();

        var roulettes = Values(ProfileGuessKind.Roulette);
        var jobs = Values(ProfileGuessKind.Job);
        // The round queues twice: once to withdraw, once for the duty. Until the second
        // registration there is no "after" and no duty to speak of.
        var entered = roulettes.Count >= 2
            ? Values(ProfileGuessKind.Territory).Where(row => row.At > roulettes[^1].At).Select(row => (int?)row.Value).FirstOrDefault()
            : null;
        return new CandidateRoundReport(
            started,
            roulettes.Count > 0 ? Roulette(roulettes[0].Value, candidate.Region) : null,
            jobs.Count > 0 ? Job(jobs[0].Value) : null,
            jobs.Count >= 2 ? Job(jobs[^1].Value) : null,
            entered is { } territory ? Duty(territory, candidate.Region) : null);
    }

    private static RoundGuess Roulette(int id, Region region) => new(id, RouletteCatalog.Default.NameOf(id, region));

    private static RoundGuess Job(int id) => new(id, JobCatalog.Default.Find(id)?.NameZh);

    private static RoundGuess Duty(int territory, Region region) =>
        DutyCatalog.Default.FindByTerritory(territory, region) is { } duty
            ? new RoundGuess(territory, duty.LocalizedName, duty.ContentId)
            : new RoundGuess(territory, null);
}
