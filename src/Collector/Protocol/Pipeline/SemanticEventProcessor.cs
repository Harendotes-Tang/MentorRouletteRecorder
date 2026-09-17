using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.StateMachine;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Protocol.Parsing;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Reference;
using MentorRecorder.Collector.Storage;
using MentorRecorder.Collector.Storage.Mutations;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.Protocol.Pipeline;

/// <summary>What one event did to the state machine.</summary>
/// <param name="EventType">Event that was fed in.</param>
/// <param name="FromState">State before.</param>
/// <param name="ToState">State after.</param>
/// <param name="Accepted">True when the event was acted on.</param>
/// <param name="Duplicate">True when it was ignored as a duplicate.</param>
/// <param name="RunId">Run it applied to, when any.</param>
public sealed record ProcessedEvent(
    string EventType,
    RunState FromState,
    RunState ToState,
    bool Accepted,
    bool Duplicate,
    string? RunId);

/// <summary>How a processor identifies the session it is writing for.</summary>
/// <param name="CaptureSessionId">Session every event and run belongs to.</param>
/// <param name="Region">Region written on the runs.</param>
/// <param name="ProtocolProfileId">Profile identifier written on the runs.</param>
/// <param name="IdSeed">
/// Namespace for the deterministic identifiers of runs, events and revisions. Two processors
/// given the same seed and the same events produce byte-identical rows, which is what makes a
/// replay idempotent.
/// </param>
/// <param name="GameBuild">Client build selected for this live session, when known.</param>
public sealed record SemanticEventProcessorOptions(
    string CaptureSessionId,
    Region Region,
    string? ProtocolProfileId,
    string IdSeed,
    string? GameBuild = null);

/// <summary>
/// The one path from a verified semantic event to rows in the database.
///
/// Live capture and offline replay both go through this class, on purpose: if replay had its
/// own copy of the persistence rules, a fixture passing would stop being evidence that live
/// capture behaves the same way. The state machine decides, this class applies, and neither
/// of them ever guesses (docs/state-machine.md section 6).
///
/// It is not thread-safe: the capture layer feeds it from its single parser thread.
/// </summary>
public sealed class SemanticEventProcessor : ISemanticEventSink, ICaptureLifecycleListener
{
    private sealed record ProcessorCheckpoint(
        StateMachineCheckpoint Machine,
        HashSet<string> TouchedRunIds,
        HashSet<string> NewRunIds,
        HashSet<string> AlreadyPersistedRunIds,
        List<string> PendingRevisionRunIds,
        Dictionary<string, TimeSpan> MatchMonoByRun,
        long LifecycleOrdinal,
        int RunsUpdated,
        int EventsAppended,
        int EventsDeduped,
        int RevisionsAppended);

    private readonly MentorRunStateMachine _machine;
    private readonly SemanticEventProcessorOptions _options;
    private readonly RunRepository _runs;
    private readonly RunEventRepository _events;
    private readonly RunRevisionRepository _revisions;
    private readonly ManualRunFieldProtection _manualFields;
    private readonly ParserErrorRepository _parserErrors;
    private readonly JobCatalog _jobs;
    private readonly DutyCatalog _duties;
    private readonly Action<SemanticEvent>? _beforeEvent;
    private readonly Action<Action<SqliteTransaction>> _runInTransaction;

    private readonly HashSet<string> _touchedRunIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _newRunIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _alreadyPersistedRunIds = new(StringComparer.Ordinal);
    private readonly List<string> _pendingRevisionRunIds = new();
    private readonly Dictionary<string, TimeSpan> _matchMonoByRun = new(StringComparer.Ordinal);

    private long _lifecycleOrdinal;

    /// <summary>Creates a processor over an open database.</summary>
    /// <param name="database">Open database; the processor is its only writer while it runs.</param>
    /// <param name="machine">State machine bound to the profile in force.</param>
    /// <param name="options">Session identity and identifier seed.</param>
    /// <param name="clock">Clock used to stamp diagnostic rows.</param>
    /// <param name="jobs">Job display mapping.</param>
    /// <param name="duties">Duty display mapping.</param>
    /// <param name="beforeEvent">
    /// Optional hook invoked before each event is applied. Replay uses it to advance its
    /// deterministic clock to the timestamp the event carries.
    /// </param>
    /// <param name="runInTransaction">
    /// Optional transaction runner, defaulting to the database's own. Injected so a test can
    /// make one commit fail the way a busy database does, without a real lock contender.
    /// </param>
    public SemanticEventProcessor(
        SqliteDatabase database,
        MentorRunStateMachine machine,
        SemanticEventProcessorOptions options,
        IClock clock,
        JobCatalog? jobs = null,
        DutyCatalog? duties = null,
        Action<SemanticEvent>? beforeEvent = null,
        Action<Action<SqliteTransaction>>? runInTransaction = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        _machine = machine;
        _options = options;
        _runs = new RunRepository(database);
        _events = new RunEventRepository(database);
        _revisions = new RunRevisionRepository(database);
        _manualFields = new ManualRunFieldProtection(database);
        _parserErrors = new ParserErrorRepository(database, clock);
        _jobs = jobs ?? JobCatalog.Default;
        _duties = duties ?? DutyCatalog.Default;
        _beforeEvent = beforeEvent;
        // Live capture commits with the short busy timeout: this thread holds the pipeline
        // lock, and every IPC read waits behind it (review finding L-11).
        _runInTransaction = runInTransaction ?? database.RunLiveCaptureTransaction;
    }

    /// <summary>State machine the processor drives.</summary>
    public MentorRunStateMachine Machine => _machine;

    /// <summary>Run rows inserted.</summary>
    public int RunsCreated => _newRunIds.Count;

    /// <summary>Run rows updated.</summary>
    public int RunsUpdated { get; private set; }

    /// <summary>Trail rows inserted.</summary>
    public int EventsAppended { get; private set; }

    /// <summary>Trail rows skipped because their key already existed.</summary>
    public int EventsDeduped { get; private set; }

    /// <summary>Audit rows inserted.</summary>
    public int RevisionsAppended { get; private set; }

    /// <summary>Runs that were already in the database when this processor met them again.</summary>
    public int AlreadyPersistedRuns => _alreadyPersistedRunIds.Count;

    /// <summary>Every run this processor touched, in no particular order.</summary>
    public IReadOnlyCollection<string> TouchedRunIds => _touchedRunIds;

    /// <summary>Applies one event inside an existing transaction.</summary>
    /// <param name="semanticEvent">Verified semantic event.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    public ProcessedEvent Process(SemanticEvent semanticEvent, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(semanticEvent);
        ArgumentNullException.ThrowIfNull(transaction);

        _beforeEvent?.Invoke(semanticEvent);
        var transition = _machine.Handle(semanticEvent);
        foreach (var command in transition.Commands)
        {
            Apply(command, semanticEvent, transaction);
        }

        return new ProcessedEvent(
            semanticEvent.EventType,
            transition.FromState,
            transition.ToState,
            transition.Accepted,
            transition.Duplicate,
            transition.RunId);
    }

    /// <summary>
    /// Appends the CREATE_AUTO revision of every run created since the last call. Revisions
    /// are written after the run row has reached its final shape so the audit trail records
    /// what was actually stored.
    /// </summary>
    /// <param name="transaction">Enclosing transaction.</param>
    public void AppendPendingRevisions(SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        foreach (var runId in _pendingRevisionRunIds)
        {
            var run = _runs.GetInternal(runId, transaction);
            if (run is null)
            {
                continue;
            }

            _revisions.Append(new RunRevision
            {
                RevisionId = DeterministicId(runId + ":revision:1"),
                RunId = runId,
                Revision = 1,
                ChangedAtUtc = run.UpdatedAtUtc,
                ChangeKind = ChangeKind.CreateAuto,
                Actor = RevisionActor.System,
                Changes = DescribeCreation(run),
            }, transaction);
            RevisionsAppended++;
        }

        _pendingRevisionRunIds.Clear();
    }

    /// <summary>Commit attempts, including the first, before a retryable failure is given up on.</summary>
    public const int StorageAttempts = 3;

    /// <summary>Backoff before the second attempt; the third waits twice as long.</summary>
    private static readonly TimeSpan StorageRetryDelay = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Applies one event in its own transaction. This is the live-capture entry point and
    /// the implementation of <see cref="ISemanticEventSink"/>; it never throws, because the
    /// capture parser thread cannot afford to lose its loop to a storage hiccup.
    ///
    /// A retryable failure -- a busy or locked database, which is transient by definition --
    /// is tried again a couple of times with a short backoff before the event is given up on.
    /// Dropping one event is not a small loss: if it was the pop, the run never exists at
    /// all, and the capture then faults on the next message with the latch below.
    /// </summary>
    /// <param name="semanticEvent">Verified semantic event.</param>
    public void Accept(SemanticEvent semanticEvent)
    {
        if (semanticEvent is null)
        {
            return;
        }

        var checkpoint = CaptureCheckpoint();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                _runInTransaction(transaction =>
                {
                    Process(semanticEvent, transaction);
                    AppendPendingRevisions(transaction);
                });
                LastStorageError = null;
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                // The machine advanced inside the failed transaction, so it is put back
                // before anything else: a retry has to replay the event from the state it
                // started in, and a give-up has to leave no trace of an event never stored.
                RestoreCheckpoint(checkpoint);
                if (attempt < StorageAttempts && IsRetryable(ex))
                {
                    Thread.Sleep(StorageRetryDelay * attempt);
                    continue;
                }

                LastStorageError = Describe(ex);
                return;
            }
        }
    }

    /// <summary>
    /// Description of the most recent storage failure swallowed by <see cref="Accept"/>,
    /// or null. It carries the message as well as the kind, because this string is the whole
    /// of what the capture fault text can tell the user about why recording stopped.
    /// </summary>
    public string? LastStorageError { get; private set; }

    /// <summary>True for a failure that a second attempt may survive.</summary>
    /// <param name="ex">Failure thrown by the commit.</param>
    private static bool IsRetryable(Exception ex) => ex switch
    {
        CollectorException collector => collector.Retryable,

        // SQLITE_BUSY and SQLITE_LOCKED, in case a caller hands us a raw failure rather than
        // the wrapped one SqliteDatabase produces.
        SqliteException sqlite => sqlite.SqliteErrorCode is 5 or 6,
        _ => false,
    };

    /// <summary>Names a storage failure without repeating anything from a packet.</summary>
    /// <param name="ex">Failure thrown by the commit.</param>
    private static string Describe(Exception ex) => ex switch
    {
        CollectorException collector => collector.Code + ": " + collector.Message,
        _ => ex.GetType().Name + ": " + ex.Message,
    };

    private ProcessorCheckpoint CaptureCheckpoint() =>
        new(
            _machine.Checkpoint(),
            new HashSet<string>(_touchedRunIds, StringComparer.Ordinal),
            new HashSet<string>(_newRunIds, StringComparer.Ordinal),
            new HashSet<string>(_alreadyPersistedRunIds, StringComparer.Ordinal),
            new List<string>(_pendingRevisionRunIds),
            new Dictionary<string, TimeSpan>(_matchMonoByRun, StringComparer.Ordinal),
            _lifecycleOrdinal,
            RunsUpdated,
            EventsAppended,
            EventsDeduped,
            RevisionsAppended);

    private void RestoreCheckpoint(ProcessorCheckpoint checkpoint)
    {
        _machine.Restore(checkpoint.Machine);

        _touchedRunIds.Clear();
        _touchedRunIds.UnionWith(checkpoint.TouchedRunIds);

        _newRunIds.Clear();
        _newRunIds.UnionWith(checkpoint.NewRunIds);

        _alreadyPersistedRunIds.Clear();
        _alreadyPersistedRunIds.UnionWith(checkpoint.AlreadyPersistedRunIds);

        _pendingRevisionRunIds.Clear();
        _pendingRevisionRunIds.AddRange(checkpoint.PendingRevisionRunIds);

        _matchMonoByRun.Clear();
        foreach (var pair in checkpoint.MatchMonoByRun)
        {
            _matchMonoByRun[pair.Key] = pair.Value;
        }

        _lifecycleOrdinal = checkpoint.LifecycleOrdinal;
        RunsUpdated = checkpoint.RunsUpdated;
        EventsAppended = checkpoint.EventsAppended;
        EventsDeduped = checkpoint.EventsDeduped;
        RevisionsAppended = checkpoint.RevisionsAppended;
    }

    /// <inheritdoc />
    public void OnCaptureStopped(bool gameExited, DateTimeOffset observedAtUtc, TimeSpan mono) =>
        Accept(new CaptureStopped
        {
            Key = LifecycleKey("CAPTURE_STOPPED", mono),
            ObservedAtUtc = observedAtUtc,
            Mono = mono,
            GameExited = gameExited,
        });

    /// <inheritdoc />
    public void OnConnectionLost(DateTimeOffset observedAtUtc, TimeSpan mono) =>
        Accept(new ConnectionLost
        {
            Key = LifecycleKey("CONNECTION_LOST", mono),
            ObservedAtUtc = observedAtUtc,
            Mono = mono,
        });

    /// <inheritdoc />
    public void OnEventsDropped(long droppedCount, DateTimeOffset observedAtUtc, TimeSpan mono) =>
        Accept(new EventSequenceGap
        {
            Key = LifecycleKey("EVENT_SEQUENCE_GAP", mono),
            ObservedAtUtc = observedAtUtc,
            Mono = mono,
            DroppedCount = droppedCount <= 0 ? 1 : droppedCount,
        });

    /// <summary>Deterministic UUIDv4-shaped identifier derived from <paramref name="seed"/>.</summary>
    /// <param name="seed">Seed text; the same seed always yields the same identifier.</param>
    public static string DeterministicId(string seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        var variant = "89ab"[bytes[8] & 0x03];
        return $"{hex[..8]}-{hex[8..12]}-4{hex[13..16]}-{variant}{hex[17..20]}-{hex[20..32]}";
    }

    private EventKey LifecycleKey(string kind, TimeSpan mono)
    {
        var ordinal = _lifecycleOrdinal++;
        return new EventKey(
            _options.CaptureSessionId,
            PacketDirection.None,
            kind,
            (long)mono.TotalMilliseconds,
            null,
            $"{_options.IdSeed}:lifecycle:{kind}:{ordinal}");
    }

    private void Apply(StateCommand command, SemanticEvent semanticEvent, SqliteTransaction transaction)
    {
        switch (command)
        {
            case CreateRunCommand create:
                CreateRun(create, semanticEvent, transaction);
                break;

            case RecordParserErrorCommand parserError:
                _parserErrors.Record(
                    parserError.Kind, parserError.Detail, _options.CaptureSessionId, transaction);
                break;

            case EnterDutyCommand enter when !_alreadyPersistedRunIds.Contains(enter.RunId):
                RunsUpdated++;
                Update(enter.RunId, transaction, current =>
                {
                    var mapped = _duties.Find(enter.ContentId, current.Region);
                    return current with
                    {
                        ContentId = enter.ContentId ?? current.ContentId,
                        TerritoryId = enter.TerritoryId ?? current.TerritoryId,
                        DutyName = current.DutyName ?? mapped?.LocalizedName,
                        DutyCategory = current.DutyCategory ?? mapped?.DutyCategory,
                        DutySource = current.DutySource ?? Provenance(
                            enter.ContentId ?? current.ContentId, enter.TerritoryId ?? current.TerritoryId),
                        EnteredAtUtc = enter.EnteredAtUtc,
                        UpdatedAtUtc = enter.EnteredAtUtc,
                    };
                });
                break;

            case SetDutyCommand setDuty when !_alreadyPersistedRunIds.Contains(setDuty.RunId):
                SetDuty(setDuty, semanticEvent, transaction);
                break;

            case SetJobCommand setJob when !_alreadyPersistedRunIds.Contains(setJob.RunId):
                RunsUpdated++;
                Update(setJob.RunId, transaction, current =>
                {
                    var job = _jobs.Find(setJob.JobId);
                    return current with
                    {
                        JobId = setJob.JobId,
                        JobName = job?.NameZh ?? JobCatalog.UnknownJobName,
                        Role = job?.Role ?? Role.Unknown,
                        UpdatedAtUtc = semanticEvent.ObservedAtUtc,
                    };
                });
                break;

            case FinishRunCommand finish when !_alreadyPersistedRunIds.Contains(finish.RunId):
                RunsUpdated++;
                Update(finish.RunId, transaction, current => current with
                {
                    EndedAtUtc = finish.EndedAtUtc,
                    DurationMs = finish.DurationMs,
                    Result = finish.Result,
                    DetectionConfidence = finish.Confidence,
                    PendingReview = finish.PendingReview || current.PendingReview,
                    UpdatedAtUtc = finish.EndedAtUtc,
                });
                break;

            case AppendEventCommand append when !_alreadyPersistedRunIds.Contains(append.RunId):
                AppendTrail(append, transaction);
                break;

            default:
                break;
        }
    }

    /// <summary>Provenance of a duty identity the capture just established.</summary>
    /// <param name="contentId">Content id on the run after the write, if any.</param>
    /// <param name="territoryId">Territory id on the run after the write, if any.</param>
    private static DutySource? Provenance(int? contentId, int? territoryId) =>
        contentId is not null ? Domain.DutySource.ContentId
        : territoryId is not null ? Domain.DutySource.Territory
        : null;

    /// <summary>
    /// Names the duty of a run that was identified by territory rather than by content id.
    ///
    /// The duty reference file is a local display mapping, not protocol evidence, so this fills
    /// in what is still empty and never raises the run's detection confidence. A territory no
    /// reference file knows leaves the run with the territory alone, which a later version of
    /// the file can still resolve.
    ///
    /// It deliberately does <em>not</em> write <c>content_id</c>: every duty statistic
    /// aggregates on that column, and the CN <c>ZONE_TERRITORY</c> offset has not been
    /// confirmed against an in-duty payload. A wrong offset lands inside the mapped territory
    /// range about half the time, so back-inferring a content id would file the attempt under a
    /// duty the player never entered, indistinguishably from an observed one. The territory and
    /// the display name are enough, and <c>duty_source</c> records that this identity is an
    /// inference (review finding M-5).
    /// </summary>
    /// <param name="setDuty">Command carrying the observed territory.</param>
    /// <param name="semanticEvent">Observation that produced it; supplies the update stamp.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    private void SetDuty(SetDutyCommand setDuty, SemanticEvent semanticEvent, SqliteTransaction transaction)
    {
        var current = _runs.GetInternal(setDuty.RunId, transaction)
            ?? throw new InvalidDataException("a state command refers to a run that was never created");

        var mapped = _duties.FindByTerritory(setDuty.TerritoryId, current.Region);
        var updated = _manualFields.Merge(current, current with
        {
            TerritoryId = setDuty.TerritoryId,
            DutyName = current.DutyName ?? mapped?.LocalizedName,
            DutyCategory = current.DutyCategory ?? mapped?.DutyCategory,
            DutySource = current.DutySource ?? Provenance(current.ContentId, setDuty.TerritoryId),
            UpdatedAtUtc = semanticEvent.ObservedAtUtc,
        }, transaction);

        if (updated.TerritoryId == current.TerritoryId &&
            updated.ContentId == current.ContentId &&
            string.Equals(updated.DutyName, current.DutyName, StringComparison.Ordinal) &&
            string.Equals(updated.DutyCategory, current.DutyCategory, StringComparison.Ordinal))
        {
            // Nothing new was learned; bumping the revision would only churn the audit trail
            // and the live event the desktop card listens to.
            return;
        }

        RunsUpdated++;
        _runs.Update(updated, current.Revision, transaction);
    }

    private void CreateRun(CreateRunCommand create, SemanticEvent semanticEvent, SqliteTransaction transaction)
    {
        _touchedRunIds.Add(create.RunId);
        if (_runs.GetInternal(create.RunId, transaction) is not null)
        {
            // The same fixture, or the same session, has already been written once. Writing
            // it again would duplicate history rather than record it.
            _alreadyPersistedRunIds.Add(create.RunId);
            return;
        }

        var mappedDuty = _duties.Find(create.ContentId, _options.Region);
        _runs.Insert(new MentorRun
        {
            RunId = create.RunId,
            Revision = 1,
            CaptureSessionId = _options.CaptureSessionId,
            Region = _options.Region,
            GameBuild = _options.GameBuild,
            ProtocolProfileId = _options.ProtocolProfileId,
            MentorRouletteId = create.MentorRouletteId,
            ContentId = create.ContentId,
            TerritoryId = mappedDuty?.TerritoryId,
            DutyName = mappedDuty?.LocalizedName,
            DutyCategory = mappedDuty?.DutyCategory,
            DutySource = Provenance(create.ContentId, mappedDuty?.TerritoryId),
            Role = Role.Unknown,
            MatchedAtUtc = create.MatchedAtUtc,
            Result = RunResult.Unknown,
            DetectionConfidence = DetectionConfidence.Medium,
            Source = RunSource.AutoNetwork,
            ContributesToGoal = true,
            CreatedAtUtc = create.MatchedAtUtc,
            UpdatedAtUtc = create.MatchedAtUtc,
        }, transaction);

        _newRunIds.Add(create.RunId);
        _pendingRevisionRunIds.Add(create.RunId);
        _matchMonoByRun[create.RunId] = create.MatchedMono ?? semanticEvent.Mono;
    }

    private void AppendTrail(AppendEventCommand append, SqliteTransaction transaction)
    {
        var sequence = _events.NextSequence(append.RunId, transaction);
        var startMono = _matchMonoByRun.GetValueOrDefault(append.RunId, append.Event.Mono);
        var offset = append.Event.Mono - startMono;
        var appended = _events.Append(new RunEvent
        {
            EventId = DeterministicId(append.RunId + ":event:" + append.Event.Key),
            RunId = append.RunId,
            Sequence = sequence,
            OccurredAtUtc = append.Event.ObservedAtUtc,
            MonotonicOffsetMs = Math.Max(0, (long)offset.TotalMilliseconds),
            EventType = append.Event.EventType,
            FromState = append.FromState,
            ToState = append.ToState,
            Confidence = append.Confidence,
            EventKey = ScopedEventKey(append.RunId, append.Event.Key),
            DetailJson = DetailJson(append.Event),
        }, transaction);

        if (appended)
        {
            EventsAppended++;
        }
        else
        {
            EventsDeduped++;
        }
    }

    /// <summary>
    /// Deduplication key of one observation inside one run.
    ///
    /// <c>run_events.event_key</c> is globally unique (migrations/0001_initial.sql,
    /// <c>ux_events_key</c>), but a single observation legitimately belongs to two runs: the
    /// pop that closes the run in flight is also the pop that opens the next one. Unqualified,
    /// the second row would be dropped by the index and the new run would start with an empty
    /// trail. Scoping the stored key by run keeps the deduplication the index exists for -- a
    /// repeated replay or a restart still cannot write the same observation twice for the same
    /// run -- while letting both runs keep their own copy.
    ///
    /// The run is appended rather than prefixed so that <c>Ipc.EventIdentity</c> still reads
    /// direction, opcode and payload digest from the leading fields of the canonical key. The
    /// state machine's in-memory duplicate set keeps using the unqualified key: an observation
    /// is still one observation.
    /// </summary>
    /// <param name="runId">Run the trail row belongs to.</param>
    /// <param name="key">Observation identity produced by the parser.</param>
    private static string ScopedEventKey(string runId, EventKey key) =>
        key.ToCanonicalString() + "|run:" + runId;

    private void Update(string runId, SqliteTransaction transaction, Func<MentorRun, MentorRun> update)
    {
        var current = _runs.GetInternal(runId, transaction)
            ?? throw new InvalidDataException("a state command refers to a run that was never created");
        var merged = _manualFields.Merge(current, update(current), transaction);
        _runs.Update(merged, current.Revision, transaction);
    }

    private static string? DetailJson(SemanticEvent semanticEvent)
    {
        object? detail = semanticEvent switch
        {
            ContentFinderPop pop => new { roulette_id = pop.RouletteId, content_id = pop.ContentId },
            ZoneInitialization zone => new { content_id = zone.ContentId, territory_id = zone.TerritoryId },
            TerritoryObserved territory => new { territory_id = territory.TerritoryId },
            PlayerJob job => new { job_id = job.JobId },
            EventSequenceGap gap => new { dropped_count = gap.DroppedCount },
            _ => null,
        };
        return detail is null ? null : JsonSerializer.Serialize(detail);
    }

    private static IReadOnlyList<RunFieldChange> DescribeCreation(MentorRun run) =>
        new RunFieldChange[]
        {
            new("run_id", null, run.RunId),
            new("revision", null, run.Revision),
            new("capture_session_id", null, run.CaptureSessionId),
            new("region", null, EnumWire<Region>.Format(run.Region)),
            new("protocol_profile_id", null, run.ProtocolProfileId),
            new("mentor_roulette_id", null, run.MentorRouletteId),
            new("content_id", null, run.ContentId),
            new("territory_id", null, run.TerritoryId),
            new("duty_name", null, run.DutyName),
            new("duty_category", null, run.DutyCategory),
            new("job_id", null, run.JobId),
            new("job_name", null, run.JobName),
            new("role", null, EnumWire<Role>.Format(run.Role)),
            new("matched_at_utc", null, UtcTimestamp.ToTextOrNull(run.MatchedAtUtc)),
            new("entered_at_utc", null, UtcTimestamp.ToTextOrNull(run.EnteredAtUtc)),
            new("ended_at_utc", null, UtcTimestamp.ToTextOrNull(run.EndedAtUtc)),
            new("duration_ms", null, run.DurationMs),
            new("result", null, EnumWire<RunResult>.Format(run.Result)),
            new("detection_confidence", null, EnumWire<DetectionConfidence>.Format(run.DetectionConfidence)),
            new("source", null, EnumWire<RunSource>.Format(run.Source)),
            new("contributes_to_goal", null, run.ContributesToGoal),
            new("manually_created", null, run.ManuallyCreated),
            new("manually_corrected", null, run.ManuallyCorrected),
            new("soft_deleted", null, run.SoftDeleted),
            new("created_at_utc", null, UtcTimestamp.ToText(run.CreatedAtUtc)),
            new("updated_at_utc", null, UtcTimestamp.ToText(run.UpdatedAtUtc)),
        };
}
