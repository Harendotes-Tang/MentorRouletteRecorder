using System.Text.Json;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.StateMachine;
using MentorRecorder.Collector.Domain.Statistics;
using MentorRecorder.Collector.Protocol.Parsing;
using MentorRecorder.Collector.Protocol.Pipeline;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Reference;
using MentorRecorder.Collector.Storage;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.Replay;

/// <summary>What the parser did, echoed into the replay report.</summary>
/// <param name="ParseOk">Messages that produced a semantic event.</param>
/// <param name="ParseFailed">Messages refused.</param>
/// <param name="Duplicates">Messages whose event key had already been seen.</param>
/// <param name="Ignored">Messages no profile message claims; ordinary traffic, not refusals.</param>
/// <param name="Errors">Refusal codes and their counts, ordered by code.</param>
public sealed record DecodedReplayParserSummary(
    long ParseOk,
    long ParseFailed,
    long Duplicates,
    long Ignored,
    IReadOnlyList<DecodedReplayErrorCount> Errors);

/// <summary>How often one refusal code was hit.</summary>
/// <param name="Code">Refusal code.</param>
/// <param name="Count">Number of refusals with that code.</param>
public sealed record DecodedReplayErrorCount(string Code, int Count);

/// <summary>Everything one decoded replay produced.</summary>
/// <param name="FixtureId">Fixture identifier.</param>
/// <param name="FixtureSha256">Verified hash of the fixture file.</param>
/// <param name="DatabasePath">Database the replay wrote to.</param>
/// <param name="ProfilePath">Profile file the replay parsed with.</param>
/// <param name="ProfileId">Profile identifier.</param>
/// <param name="ProfileStatus">Compatibility status of the profile.</param>
/// <param name="ProfileUsable">Whether the parser was allowed to parse at all.</param>
/// <param name="BuildMatched">Whether the fixture build equals the profile build.</param>
/// <param name="MessagesRead">Decoded messages fed to the parser.</param>
/// <param name="ParsedEvents">Semantic events the parser produced.</param>
/// <param name="Transitions">Transitions they caused.</param>
/// <param name="FinalState">State the machine ended in.</param>
/// <param name="Runs">Runs the replay touched.</param>
/// <param name="Statistics">Dashboard statistics after the replay.</param>
/// <param name="Writes">Database write summary.</param>
/// <param name="Parser">Parser counters.</param>
public sealed record DecodedReplayResult(
    string FixtureId,
    string FixtureSha256,
    string DatabasePath,
    string ProfilePath,
    string? ProfileId,
    string ProfileStatus,
    bool ProfileUsable,
    bool BuildMatched,
    int MessagesRead,
    IReadOnlyList<ReplayParsedEvent> ParsedEvents,
    IReadOnlyList<ReplayTransition> Transitions,
    RunState FinalState,
    IReadOnlyList<MentorRun> Runs,
    DashboardStatistics Statistics,
    ReplayWriteSummary Writes,
    DecodedReplayParserSummary Parser);

/// <summary>
/// Replays a synthetic decoded-message fixture through the real parser, the real state
/// machine and the real persistence path.
///
/// This is the only place in the repository where bytes become runs without a game, and it
/// is deliberately the same code that live capture uses: the fixture supplies the messages
/// live capture would otherwise hand to <see cref="ProfileMessageParser"/>, and everything
/// after that is production code.
///
/// The build check is fail-closed rather than advisory: a fixture that claims a different
/// client build than the profile is parsed with no profile at all, so every message is
/// refused and nothing is written. That is the offline reproduction of a game update.
/// </summary>
public static class DecodedReplayRunner
{
    /// <summary>Replays one decoded fixture and reports everything it did.</summary>
    /// <param name="fixturePath">Decoded fixture file.</param>
    /// <param name="profilePath">
    /// Profile to parse with. When null, the synthetic profile named by the fixture is
    /// looked up in the installed <c>protocol-profiles</c> directory.
    /// </param>
    /// <param name="databasePath">Database to write to; a hash-named temp file when null.</param>
    public static DecodedReplayResult Run(
        string fixturePath, string? profilePath = null, string? databasePath = null)
    {
        var fixture = DecodedFixtureLoader.Load(fixturePath);
        profilePath ??= ResolveProfilePath(fixture.ProfileId);
        var selection = ProfileSelector.SelectExplicit(profilePath, allowSynthetic: true);
        var profile = selection.Profile;
        var buildMatched =
            profile is not null &&
            string.Equals(profile.ProfileId, fixture.ProfileId, StringComparison.Ordinal) &&
            string.Equals(profile.GameBuild, fixture.GameBuild, StringComparison.OrdinalIgnoreCase);

        // A build mismatch removes the profile entirely instead of tolerating it: parsing a
        // new build with an old structure is exactly the failure this project refuses to have.
        var effectiveProfile = buildMatched ? profile : null;

        databasePath ??= Path.Combine(
            Path.GetTempPath(), "MentorRecorder", "replay-decoded", fixture.Sha256 + ".db");

        var clock = new FixtureReplayRunner.ReplayClock(fixture.StartedAtUtc);
        using var database = SqliteDatabase.Open(databasePath, clock);
        var settings = new SettingsRepository(database, clock);
        settings.EnsureDefaults();
        var runs = new RunRepository(database);
        var sessions = new CaptureSessionRepository(database);
        var parserErrors = new ParserErrorRepository(database, clock);
        var jobs = JobCatalog.Default;
        var duties = DutyCatalog.Default;

        var runOrdinal = 0;
        var binding = effectiveProfile?.ToBinding() ?? ProfileBinding.FailClosed;
        var machine = new MentorRunStateMachine(
            binding,
            new StateMachineOptions
            {
                MatchWindow = effectiveProfile?.MatchWindow ?? StateMachineOptions.Default.MatchWindow,
            },
            () => SemanticEventProcessor.DeterministicId(fixture.FixtureId + ":run:" + runOrdinal++));

        var processor = new SemanticEventProcessor(
            database,
            machine,
            new SemanticEventProcessorOptions(
                fixture.CaptureSessionId, Region.Unknown, effectiveProfile?.ProfileId, fixture.FixtureId),
            clock,
            jobs,
            duties,
            semanticEvent =>
            {
                clock.UtcNow = semanticEvent.ObservedAtUtc;
                clock.Elapsed = semanticEvent.Mono;
            });

        var parsedEvents = new List<ReplayParsedEvent>();
        var transitions = new List<ReplayTransition>();
        var sink = new TransactionSink((semanticEvent, transaction) =>
        {
            parsedEvents.Add(new ReplayParsedEvent(
                semanticEvent.EventType,
                semanticEvent.Key.SemanticKey,
                semanticEvent.ObservedAtUtc,
                (long)semanticEvent.Mono.TotalMilliseconds));
            var processed = processor.Process(semanticEvent, transaction);
            transitions.Add(new ReplayTransition(
                processed.EventType,
                processed.FromState,
                processed.ToState,
                processed.Accepted,
                processed.Duplicate,
                processed.RunId));
        });

        var parser = new ProfileMessageParser(effectiveProfile, sink);

        database.RunInTransaction(transaction =>
        {
            sessions.Insert(new CaptureSession
            {
                CaptureSessionId = fixture.CaptureSessionId,
                StartedAtUtc = fixture.StartedAtUtc,
                CollectorVersion = Program.Version,
                Region = Region.Unknown,
                ProtocolProfileId = effectiveProfile?.ProfileId,
                ProfileStatus = binding.Status,
                PacketsObserved = fixture.Messages.Count,
                PacketsDropped = 0,
            }, transaction);

            sink.Current = transaction;
            foreach (var message in fixture.Messages)
            {
                clock.UtcNow = message.ObservedAtUtc;
                clock.Elapsed = message.Mono;
                parser.Accept(message);
            }

            sink.Current = null;
            processor.AppendPendingRevisions(transaction);

            foreach (var error in parser.GetParserStats().RecentErrors)
            {
                parserErrors.Record(error.Kind, error.Message, fixture.CaptureSessionId, transaction);
            }

            sessions.Close(
                fixture.CaptureSessionId,
                fixture.Messages[^1].ObservedAtUtc,
                CaptureEndReason.UserStop,
                transaction);
        });

        var stats = parser.GetParserStats();
        var finalRuns = processor.TouchedRunIds
            .Select(id => runs.Get(id)!)
            .OrderBy(run => run.CreatedAtUtc)
            .ToArray();

        return new DecodedReplayResult(
            fixture.FixtureId,
            fixture.Sha256,
            Path.GetFullPath(databasePath),
            Path.GetFullPath(profilePath),
            effectiveProfile?.ProfileId,
            EnumWire<ProfileCompatibilityStatus>.Format(
                effectiveProfile?.Status ?? ProfileCompatibilityStatus.Unsupported),
            binding.IsUsable,
            buildMatched,
            fixture.Messages.Count,
            parsedEvents,
            transitions,
            machine.State,
            finalRuns,
            new StatisticsRepository(database, settings, jobs, duties).GetDashboard(),
            new ReplayWriteSummary(
                processor.RunsCreated,
                processor.RunsUpdated,
                processor.EventsAppended,
                processor.EventsDeduped,
                processor.RevisionsAppended,
                machine.DuplicateCount,
                machine.ParserErrorCount,
                processor.AlreadyPersistedRuns > 0),
            new DecodedReplayParserSummary(
                stats.ParseOk,
                stats.ParseFailed,
                stats.Duplicates,
                stats.Ignored,
                stats.RecentErrors
                    .GroupBy(error => error.Kind, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => new DecodedReplayErrorCount(group.Key, group.Count()))
                    .ToArray()));
    }

    /// <summary>Serialises a decoded replay result as indented snake_case JSON.</summary>
    /// <param name="result">Result to serialise.</param>
    public static string Serialize(DecodedReplayResult result) =>
        JsonSerializer.Serialize(result, ReplayJson.Options);

    /// <summary>Finds the installed profile file a fixture names.</summary>
    /// <param name="profileId">Profile identifier declared by the fixture.</param>
    public static string ResolveProfilePath(string profileId)
    {
        var root = ProfileCatalog.FindDefaultRoot()
            ?? throw new FileNotFoundException("no protocol-profiles directory was found");
        var candidate = Path.Combine(root, "synthetic", profileId + ".json");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        var found = Directory
            .EnumerateFiles(root, profileId + ".json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .FirstOrDefault();
        return found ?? throw new FileNotFoundException("no profile named " + profileId + " is installed");
    }

    /// <summary>Bridges the parser to the processor while one transaction is open.</summary>
    private sealed class TransactionSink(Action<SemanticEvent, SqliteTransaction> handler) : ISemanticEventSink
    {
        public SqliteTransaction? Current { get; set; }

        public void Accept(SemanticEvent semanticEvent)
        {
            if (Current is { } transaction)
            {
                handler(semanticEvent, transaction);
            }
        }
    }
}
