using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Domain.StateMachine;
using MentorRecorder.Collector.Protocol.Pipeline;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Automatic recording on a verified CN capture session: a live state machine and the semantic event
/// processor that writes its runs. Every accepted event first moves the test clock to the event, as live
/// capture would, and must be stored without an error. Times are offsets in seconds from <see cref="Start"/>,
/// the clock's time when the bed was made.
/// </summary>
internal sealed class LiveRunTestBed
{
    private readonly TestDatabase _database;

    /// <param name="database">The database the runs are written to.</param>
    /// <param name="binding">The profile binding of the state machine; its profile id is stamped on the runs.</param>
    /// <param name="identityAtMatch">The character identity looked up when a match is made, or null.</param>
    public LiveRunTestBed(
        TestDatabase database,
        ProfileBinding binding,
        Func<SqliteTransaction, CharacterIdentityObservation?>? identityAtMatch = null)
    {
        _database = database;
        Start = database.Clock.UtcNow;
        SessionId = database.OpenCaptureSession(Region.Cn, null, ProfileStatus.Verified);
        Machine = new MentorRunStateMachine(binding);
        Processor = new SemanticEventProcessor(database.Database, Machine,
            new SemanticEventProcessorOptions(SessionId, Region.Cn, binding.ProfileId, SessionId), database.Clock,
            identityAtMatch: identityAtMatch);
    }

    public string SessionId { get; }

    public DateTimeOffset Start { get; }

    public MentorRunStateMachine Machine { get; }

    public SemanticEventProcessor Processor { get; }

    /// <summary>The key of an event of <paramref name="kind"/> observed <paramref name="seconds"/> after the start.</summary>
    public EventKey Key(string kind, int seconds) =>
        new(SessionId, PacketDirection.ServerToClient, kind, seconds, null, kind + seconds);

    /// <summary>Moves the clock to the event, processes it and asserts it was stored.</summary>
    public void Accept(SemanticEvent value)
    {
        _database.Clock.UtcNow = value.ObservedAtUtc;
        _database.Clock.Elapsed = value.Mono;
        Processor.Accept(value);
        Assert.Null(Processor.LastStorageError);
    }

    /// <summary>The roulette pop at the start; returns the id of the run it opened.</summary>
    public string Pop(int? contentId = 900001)
    {
        Accept(new ContentFinderPop
        {
            Key = Key("pop", 0), ObservedAtUtc = Start, Mono = TimeSpan.Zero, RouletteId = 42, ContentId = contentId,
        });
        return Machine.CurrentRunId!;
    }

    /// <summary>The duty zone entered a second after the start.</summary>
    public void Enter(int? contentId = 900001, int? territoryId = 800001, bool? isDutyInstance = null) =>
        Accept(new ZoneInitialization
        {
            Key = Key("enter", 1), ObservedAtUtc = Start.AddSeconds(1), Mono = TimeSpan.FromSeconds(1),
            ContentId = contentId, TerritoryId = territoryId, IsDutyInstance = isDutyInstance,
        });

    /// <summary>The player's job announced <paramref name="seconds"/> after the start.</summary>
    public void Job(int job, int seconds) => Accept(new PlayerJob
    {
        Key = Key("job", seconds), ObservedAtUtc = Start.AddSeconds(seconds),
        Mono = TimeSpan.FromSeconds(seconds), JobId = job,
    });

    /// <summary>A territory announcement observed inside the duty.</summary>
    public void Territory(int territoryId, int seconds) => Accept(new TerritoryObserved
    {
        Key = Key("territory", seconds), ObservedAtUtc = Start.AddSeconds(seconds),
        Mono = TimeSpan.FromSeconds(seconds), TerritoryId = territoryId,
    });

    /// <summary>Leaving the zone a minute after the start.</summary>
    public void Exit() => ExitAt(60);

    /// <summary>Leaving the zone a minute after the start by the monotonic clock, stamped <paramref name="seconds"/> after it.</summary>
    public void ExitAt(int seconds) => Accept(new ZoneLeft
    {
        Key = Key("exit", seconds), ObservedAtUtc = Start.AddSeconds(seconds), Mono = TimeSpan.FromSeconds(60),
    });

    /// <summary>The duty cleared a minute after the start.</summary>
    public void Victory() => Accept(new DutyResult
    {
        Key = Key("victory", 60), ObservedAtUtc = Start.AddSeconds(60), Mono = TimeSpan.FromSeconds(60), Victory = true,
    });
}
