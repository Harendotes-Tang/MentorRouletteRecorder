using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
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
    /// <summary>Player round that supplied the status guesses. Exported evidence carries its scope separately.</summary>
    public string? PlayerRoundId { get; init; }

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
/// <param name="onError">Told when the report could not be built.</param>
public sealed class CandidateRoundReporter(
    CandidateObservationRepository ledger, SettingsRepository settings, Func<ProtocolProfile?> profile,
    Action<Exception>? onError = null)
{
    /// <summary>
    /// The report, or null when no round was started, the profile guesses nothing, or it could
    /// not be built. The guesses are a convenience beside the evidence: failing to read them must
    /// never take the status or the export of a round down with it.
    /// </summary>
    public CandidateRoundReport? Read() => TryRead(() =>
    {
        var current = profile();
        if (CandidatePlayerRound.Read(settings) is { Active: true } round)
        {
            if (current is not { Status: ProfileCompatibilityStatus.Candidate } || current.ProfileId != round.ProfileId)
                return null;
            var report = Build(current, round.StartedAtUtc, positions => ledger.ReadPlayerGuessValues(round, positions));
            return report is null ? null : report with { PlayerRoundId = round.Id };
        }
        return current is { Status: ProfileCompatibilityStatus.Candidate } &&
               settings.GetSettingUpdatedAtUtc(CaptureSettingsStore.CandidateRoundMarkSetting) is { } started
            ? Build(current, started, positions => ledger.ReadGuessValues(current.ProfileId, started, positions))
            : null;
    });

    /// <summary>Builds an export's report from its exact observations, even after the client changed.</summary>
    internal CandidateRoundReport? Read(CandidatePlayerRound round, SqliteTransaction transaction) => TryRead(() =>
    {
        var candidate = profile();
        if (candidate is not { Status: ProfileCompatibilityStatus.Candidate } || candidate.ProfileId != round.ProfileId)
            candidate = ProfileCatalog.LoadDefault(allowCandidate: true).Entries.Select(entry => entry.Profile)
                .SingleOrDefault(item => item is { Status: ProfileCompatibilityStatus.Candidate } && item.ProfileId == round.ProfileId);
        return candidate is null ? null : Build(candidate, round.StartedAtUtc,
            positions => ledger.ReadPlayerGuessValues(round, positions, transaction));
    });

    private CandidateRoundReport? TryRead(Func<CandidateRoundReport?> build)
    {
        try
        {
            return build();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            onError?.Invoke(ex);
            return null;
        }
    }

    private static CandidateRoundReport? Build(ProtocolProfile candidate, DateTimeOffset started,
        Func<IEnumerable<CandidateGuessPosition>, IReadOnlyDictionary<CandidateGuessPosition,
            IReadOnlyList<(DateTimeOffset At, int Value)>>> readValues)
    {
        var guesses = candidate.Hypotheses
            .SelectMany(hypothesis => hypothesis.Guesses.Select(guess => (guess.Kind, Position: new CandidateGuessPosition(
                hypothesis.Direction == PacketDirection.ServerToClient ? "S2C" : "C2S",
                hypothesis.Opcode, hypothesis.ExpectedLength!.Value, guess.Offset, guess.Size))))
            .ToDictionary(pair => pair.Kind, pair => pair.Position);
        if (guesses.Count == 0) return null;

        var values = readValues(guesses.Values);
        IReadOnlyList<(DateTimeOffset At, int Value)> Values(ProfileGuessKind kind) =>
            guesses.TryGetValue(kind, out var position)
                ? values[position]
                : Array.Empty<(DateTimeOffset, int)>();

        var roulettes = Values(ProfileGuessKind.Roulette);
        if (roulettes.Count == 0) return new CandidateRoundReport(started, null, null, null, null);

        var firstRegistration = roulettes[0];
        var jobs = Values(ProfileGuessKind.Job);
        // Replies before the final registration, including repeats and withdrawal from the
        // first queue, cannot establish the job used for the final queue.
        DateTimeOffset? finalRegistration = roulettes.Count >= 2 ? roulettes[^1].At : null;
        var jobBefore = jobs
            .Where(row => row.At >= firstRegistration.At && (finalRegistration is null || row.At < finalRegistration.Value))
            .Select(row => (int?)row.Value).FirstOrDefault();
        var jobAfter = finalRegistration is { } second
            ? jobs.Where(row => row.At >= second).Select(row => (int?)row.Value).FirstOrDefault()
            : null;
        var entered = finalRegistration is { } registration
            ? Values(ProfileGuessKind.Territory).Where(row => row.At > registration).Select(row => (int?)row.Value).FirstOrDefault()
            : null;
        return new CandidateRoundReport(
            started,
            Roulette(firstRegistration.Value, candidate.Region),
            jobBefore is { } before ? Job(before) : null,
            jobAfter is { } after ? Job(after) : null,
            entered is { } territory ? Duty(territory, candidate.Region) : null);
    }

    private static RoundGuess Roulette(int id, Region region) => new(id, RouletteCatalog.Default.NameOf(id, region));

    private static RoundGuess Job(int id) => new(id, JobCatalog.Default.Find(id)?.NameZh);

    private static RoundGuess Duty(int territory, Region region) =>
        DutyCatalog.Default.FindByTerritory(territory, region) is { } duty
            ? new RoundGuess(territory, duty.LocalizedName, duty.ContentId)
            : new RoundGuess(territory, null);
}
