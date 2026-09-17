using System.Text.Json;
using System.Text.Json.Serialization;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.StateMachine;
using MentorRecorder.Collector.Domain.Statistics;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Protocol.Pipeline;
using MentorRecorder.Collector.Reference;
using MentorRecorder.Collector.Storage;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.Replay;

/// <summary>One parsed fixture event, echoed so a replay shows its own input.</summary>
/// <param name="EventType">Semantic event type.</param>
/// <param name="EventKey">Deduplication key declared by the fixture.</param>
/// <param name="ObservedAtUtc">Wall-clock time of the observation.</param>
/// <param name="MonotonicMs">Monotonic reading of the observation.</param>
public sealed record ReplayParsedEvent(
    string EventType,
    string EventKey,
    DateTimeOffset ObservedAtUtc,
    long MonotonicMs);

/// <summary>One state machine transition caused by one event.</summary>
/// <param name="EventType">Event that caused it.</param>
/// <param name="FromState">State before.</param>
/// <param name="ToState">State after.</param>
/// <param name="Accepted">True when the event was acted on.</param>
/// <param name="Duplicate">True when it was ignored as a duplicate.</param>
/// <param name="RunId">Run it applied to, when any.</param>
public sealed record ReplayTransition(
    string EventType,
    RunState FromState,
    RunState ToState,
    bool Accepted,
    bool Duplicate,
    string? RunId);

/// <summary>What the replay wrote to the database.</summary>
/// <param name="RunsCreated">Run rows inserted.</param>
/// <param name="RunsUpdated">Run rows updated.</param>
/// <param name="EventsAppended">Trail rows inserted.</param>
/// <param name="EventsDeduped">Trail rows skipped because their key already existed.</param>
/// <param name="RevisionsAppended">Audit rows inserted.</param>
/// <param name="DuplicateEvents">Events the state machine ignored as duplicates.</param>
/// <param name="ParserErrors">Events refused because the profile was not usable.</param>
/// <param name="IdempotentReplay">True when this replay found runs it had already written.</param>
public sealed record ReplayWriteSummary(
    int RunsCreated,
    int RunsUpdated,
    int EventsAppended,
    int EventsDeduped,
    int RevisionsAppended,
    int DuplicateEvents,
    int ParserErrors,
    bool IdempotentReplay);

/// <summary>Everything one replay produced.</summary>
/// <param name="FixtureId">Fixture identifier.</param>
/// <param name="FixtureSha256">Verified hash of the fixture file.</param>
/// <param name="DatabasePath">Database the replay wrote to.</param>
/// <param name="ProfileStatus">Profile status the fixture declared.</param>
/// <param name="ProfileUsable">Whether the state machine was allowed to act at all.</param>
/// <param name="ParsedEvents">Events read out of the fixture.</param>
/// <param name="Transitions">Transitions they caused.</param>
/// <param name="FinalState">State the machine ended in.</param>
/// <param name="Runs">Runs the replay touched.</param>
/// <param name="Statistics">Dashboard statistics after the replay.</param>
/// <param name="Writes">Database write summary.</param>
public sealed record FixtureReplayResult(
    string FixtureId,
    string FixtureSha256,
    string DatabasePath,
    string ProfileStatus,
    bool ProfileUsable,
    IReadOnlyList<ReplayParsedEvent> ParsedEvents,
    IReadOnlyList<ReplayTransition> Transitions,
    RunState FinalState,
    IReadOnlyList<MentorRun> Runs,
    DashboardStatistics Statistics,
    ReplayWriteSummary Writes);

/// <summary>
/// Deterministically replays already-semantic synthetic events. It deliberately has no
/// packet decoder and cannot make an unverified live profile usable.
///
/// The persistence half lives in
/// <see cref="MentorRecorder.Collector.Protocol.Pipeline.SemanticEventProcessor"/>, shared
/// with live capture, so a passing fixture is evidence about the live path and not only
/// about the replay tool.
/// </summary>
public static class FixtureReplayRunner
{
    /// <summary>Replays one fixture into a database and reports everything it did.</summary>
    /// <param name="fixturePath">Fixture file to replay.</param>
    /// <param name="databasePath">Database to write to; a hash-named temp file when null.</param>
    public static FixtureReplayResult Run(string fixturePath, string? databasePath = null)
    {
        var fixture = ReplayFixtureLoader.Load(fixturePath);
        databasePath ??= Path.Combine(
            Path.GetTempPath(), "MentorRecorder", "replay", fixture.Sha256 + ".db");
        var clock = new ReplayClock(fixture.Session.StartedAtUtc);
        using var database = SqliteDatabase.Open(databasePath, clock);
        var settings = new SettingsRepository(database, clock);
        settings.EnsureDefaults();
        var runs = new RunRepository(database);
        var sessions = new CaptureSessionRepository(database);
        var jobs = JobCatalog.Default;
        var duties = DutyCatalog.Default;
        var runOrdinal = 0;
        var binding = BindProfile(fixture.Profile);
        var machine = new MentorRunStateMachine(
            binding,
            new StateMachineOptions { MatchWindow = TimeSpan.FromSeconds(fixture.Profile.MatchWindowSeconds) },
            () => SemanticEventProcessor.DeterministicId(fixture.FixtureId + ":run:" + runOrdinal++));
        var processor = new SemanticEventProcessor(
            database,
            machine,
            new SemanticEventProcessorOptions(
                fixture.Session.CaptureSessionId, Region.Unknown, fixture.Profile.ProfileId, fixture.FixtureId),
            clock,
            jobs,
            duties,
            semanticEvent =>
            {
                clock.UtcNow = semanticEvent.ObservedAtUtc;
                clock.Elapsed = semanticEvent.Mono;
            });

        var transitions = new List<ReplayTransition>();
        var parsedEvents = fixture.Events
            .Select(ev => new ReplayParsedEvent(
                ev.EventType, ev.Key.SemanticKey, ev.ObservedAtUtc, (long)ev.Mono.TotalMilliseconds))
            .ToArray();

        database.RunInTransaction(tx =>
        {
            sessions.Insert(new CaptureSession
            {
                CaptureSessionId = fixture.Session.CaptureSessionId,
                StartedAtUtc = fixture.Session.StartedAtUtc,
                CollectorVersion = Program.Version,
                Region = Region.Unknown,
                ProtocolProfileId = fixture.Profile.ProfileId,
                ProfileStatus = binding.Status,
                PacketsObserved = fixture.Events.Count,
                PacketsDropped = 0,
            }, tx);

            foreach (var semanticEvent in fixture.Events)
            {
                var processed = processor.Process(semanticEvent, tx);
                transitions.Add(new ReplayTransition(
                    processed.EventType,
                    processed.FromState,
                    processed.ToState,
                    processed.Accepted,
                    processed.Duplicate,
                    processed.RunId));
            }

            processor.AppendPendingRevisions(tx);
            var ended = fixture.Events[^1].ObservedAtUtc;
            sessions.Close(fixture.Session.CaptureSessionId, ended, CaptureEndReason.UserStop, tx);
        });

        var finalRuns = processor.TouchedRunIds
            .Select(id => runs.Get(id)!)
            .OrderBy(run => run.CreatedAtUtc)
            .ToArray();
        var statistics = new StatisticsRepository(database, settings, jobs, duties).GetDashboard();
        return new FixtureReplayResult(
            fixture.FixtureId,
            fixture.Sha256,
            Path.GetFullPath(databasePath),
            EnumWire<ProfileStatus>.Format(binding.Status),
            binding.IsUsable,
            parsedEvents,
            transitions,
            machine.State,
            finalRuns,
            statistics,
            new ReplayWriteSummary(
                processor.RunsCreated,
                processor.RunsUpdated,
                processor.EventsAppended,
                processor.EventsDeduped,
                processor.RevisionsAppended,
                machine.DuplicateCount,
                machine.ParserErrorCount,
                processor.AlreadyPersistedRuns > 0));
    }

    /// <summary>
    /// Binds the state machine to the profile the fixture declares. Anything but
    /// <c>SYNTHETIC</c> produces a live binding that is not verified, and therefore refuses
    /// every event: that is how the fail-closed path is replayed offline.
    /// </summary>
    /// <param name="profile">Profile declared by the fixture.</param>
    private static ProfileBinding BindProfile(ReplayProfile profile)
    {
        if (profile.IsSynthetic)
        {
            return ProfileBinding.Synthetic(profile.ProfileId, profile.MentorRouletteId);
        }

        var status = EnumWire<ProfileStatus>.TryParse(profile.Status, out var parsed)
            ? parsed
            : ProfileStatus.UnsupportedBuild;
        return ProfileBinding.Live(
            profile.ProfileId, Region.Unknown, status, profile.MentorRouletteId, canDetectDutyResult: true);
    }

    /// <summary>Serialises a replay result as indented snake_case JSON.</summary>
    /// <param name="result">Result to serialise.</param>
    public static string Serialize(FixtureReplayResult result) =>
        JsonSerializer.Serialize(result, ReplayJson.Options);

    /// <summary>Deterministic clock driven by the timestamps a fixture carries.</summary>
    /// <param name="utcNow">Initial wall-clock reading.</param>
    internal sealed class ReplayClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public TimeSpan Elapsed { get; set; }
    }
}

/// <summary>Shared JSON settings for every replay report.</summary>
public static class ReplayJson
{
    /// <summary>Indented snake_case options with UTC timestamps and UPPER_SNAKE enums.</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters =
        {
            new UtcDateTimeOffsetJsonConverter(),
            new JsonStringEnumConverter(UpperSnakeCaseNamingPolicy.Instance),
        },
    };

    /// <summary>Writes and reads timestamps in the one canonical UTC form.</summary>
    public sealed class UtcDateTimeOffsetJsonConverter : JsonConverter<DateTimeOffset>
    {
        /// <inheritdoc />
        public override DateTimeOffset Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            UtcTimestamp.Parse(reader.GetString());

        /// <inheritdoc />
        public override void Write(
            Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(UtcTimestamp.ToText(value));
    }
}
