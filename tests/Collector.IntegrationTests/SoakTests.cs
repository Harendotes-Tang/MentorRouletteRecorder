using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Pipeline;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Reference;
using MentorRecorder.Collector.Storage;
using MentorRecorder.Collector.Storage.Repositories;
using Xunit.Abstractions;

namespace MentorRecorder.Collector.IntegrationTests;

// WorkingSet64 measures the whole test-host process, so concurrent tests must not allocate
// into the soak's working-set budget.
[CollectionDefinition("Process memory soak", DisableParallelization = true)]
public sealed class ProcessMemorySoakCollection;

/// <summary>
/// Sustained mixed-traffic run through the shipping capture stack: the real bounded queue,
/// parser thread, profile parser, state machine and a real SQLite file. Three pieces are
/// substituted through the extension points in <c>CaptureExtensionPoints.cs</c>: the packet
/// source is <see cref="FakeCaptureSource"/>, the machine detectors are the suite's fakes, and
/// the profile is the checked-in synthetic one remapped onto the IPC segment type so it can be
/// driven through the real framing reader. Nothing in <c>src/</c> is modified or bypassed.
///
/// Guards the failures a short test cannot see: memory that only grows, counters that stop
/// reconciling, a queue that loses messages silently, a diagnostics ring that is not really
/// bounded, database growth without runs, and a dashboard query that degrades as rows
/// accumulate.
///
/// Duration is <see cref="DefaultDuration"/> and never exceeds <see cref="MaxDefaultDuration"/>;
/// <c>MR_SOAK_MINUTES</c> lifts the cap for a release soak.
/// </summary>
[Trait("Category", "Soak")]
[Collection("Process memory soak")]
public sealed class SoakTests : IDisposable
{
    /// <summary>Environment variable that extends the soak beyond the default.</summary>
    public const string DurationVariable = "MR_SOAK_MINUTES";

    /// <summary>Duration used when nothing asks for more.</summary>
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(15);

    /// <summary>Hard ceiling on the default run, so an ordinary `dotnet test` stays quick.</summary>
    public static readonly TimeSpan MaxDefaultDuration = TimeSpan.FromSeconds(90);

    /// <summary>Messages per second the pusher aims for outside a burst.</summary>
    public const int TargetRatePerSecond = 20_000;

    /// <summary>Lowest sustained push rate for the run to count as a valid soak.</summary>
    public const int MinimumRatePerSecond = 5_000;

    /// <summary>Messages pushed back to back in one burst.</summary>
    public const int BurstSize = 20_000;

    /// <summary>How often a burst is emitted.</summary>
    public static readonly TimeSpan BurstInterval = TimeSpan.FromSeconds(2);

    /// <summary>How often a complete, valid mentor run is driven through the stack.</summary>
    public static readonly TimeSpan CycleInterval = TimeSpan.FromMilliseconds(400);

    /// <summary>Largest working-set growth the soak tolerates over the whole run.</summary>
    public const long MaxWorkingSetGrowthBytes = 50L * 1024 * 1024;

    /// <summary>Longest the dashboard query may take once the database has filled up.</summary>
    public static readonly TimeSpan MaxDashboardLatency = TimeSpan.FromMilliseconds(200);

    // Opcodes. The four cycle opcodes come from the checked-in synthetic profile; the noise
    // opcodes are deliberately outside it so the parser has to refuse them.
    private const ushort OpcodePop = 61441;
    private const ushort OpcodeZone = 61442;
    private const ushort OpcodeResult = 61443;
    private const ushort OpcodeJob = 61444;
    private const ushort OpcodeUnknownA = 60000;
    private const ushort OpcodeUnknownB = 60001;

    private const int MentorRouletteId = 42;
    private const string GameBuild = "soak-build";

    private readonly ITestOutputHelper _output;
    private readonly string _directory;

    public SoakTests(ITestOutputHelper output)
    {
        _output = output;
        _directory = Path.Combine(
            Path.GetTempPath(), "MentorRecorder.Soak", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    /// <summary>Duration this run will use, honouring <see cref="DurationVariable"/>.</summary>
    public static TimeSpan ResolveDuration()
    {
        var raw = Environment.GetEnvironmentVariable(DurationVariable);
        if (!string.IsNullOrWhiteSpace(raw) &&
            double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) &&
            minutes > 0)
        {
            return TimeSpan.FromMinutes(minutes);
        }

        return DefaultDuration < MaxDefaultDuration ? DefaultDuration : MaxDefaultDuration;
    }

    [Fact]
    public void TheDefaultSoakIsBoundedSoTheSuiteStaysUsable()
    {
        var previous = Environment.GetEnvironmentVariable(DurationVariable);
        try
        {
            Environment.SetEnvironmentVariable(DurationVariable, null);
            Assert.True(
                ResolveDuration() <= MaxDefaultDuration,
                "the default soak must never exceed " + MaxDefaultDuration);

            Environment.SetEnvironmentVariable(DurationVariable, "30");
            Assert.Equal(TimeSpan.FromMinutes(30), ResolveDuration());

            // Anything unparseable falls back to the bounded default rather than to zero:
            // a typo in a CI variable must not silently turn the soak off.
            Environment.SetEnvironmentVariable(DurationVariable, "not-a-number");
            Assert.Equal(DefaultDuration, ResolveDuration());
        }
        finally
        {
            Environment.SetEnvironmentVariable(DurationVariable, previous);
        }
    }

    [Fact]
    public void AMixedStreamSustainedForTheWholeDurationLeavesEveryInvariantIntact()
    {
        var duration = ResolveDuration();
        var databasePath = Path.Combine(_directory, "soak.db");

        using var harness = SoakHarness.Start(databasePath);

        var baselineDatabaseBytes = harness.DatabaseBytes();
        var baselineWorkingSet = MeasureWorkingSet();
        var report = harness.Drive(duration);

        // ---------------------------------------------------------------- throughput ----
        Assert.True(
            report.PushRatePerSecond >= MinimumRatePerSecond,
            $"the soak must sustain at least {MinimumRatePerSecond} msg/s to be worth " +
            $"anything; it managed {report.PushRatePerSecond:F0} msg/s " +
            $"({report.Pushed} messages in {report.Elapsed.TotalSeconds:F1}s)");

        Assert.True(report.Bursts > 0, "the soak must have emitted at least one burst");
        Assert.True(
            report.Final.DuplicateCount > 0,
            "the stream repeats messages verbatim, so the parser must have seen duplicates");
        Assert.True(report.CompletedCycles > 0, "the soak must have driven at least one full run");

        // ------------------------------------------------------------ queue accounting ----
        // Identity from docs/capture-diagnostics.md section 6: every message the framing reader
        // accepted was either handed to the parser or counted as a drop.
        var accepted = report.Final.ParseOkCount + report.Final.ParseFailCount + report.Final.IgnoredCount;
        Assert.Equal(report.Final.MessagesDecoded, accepted + report.Final.DroppedCount);
        Assert.True(
            report.Final.IgnoredCount > 0,
            "the noise stream carries undeclared opcodes, which must be counted as ignored");
        Assert.True(report.Final.DroppedCount >= 0);
        Assert.True(
            report.Final.QueueDepth <= report.Final.QueueCapacity,
            "the bounded queue must never hold more than its capacity");

        // Malformed frames never reach the queue: framing refuses them and they land in the
        // decode-error bucket instead of the parser buckets.
        Assert.True(
            report.Final.DecodeErrors >= report.MalformedPushed,
            "every malformed frame must be counted as a decode error");

        // ---------------------------------------------------------------- no escapes ----
        Assert.Equal(CaptureControllerState.Running, report.StateBeforeStop);
        Assert.Null(report.Final.LastErrorCode);
        Assert.Empty(harness.SinkFailures);

        // ------------------------------------------------------------------- memory ----
        var growth = MeasureWorkingSet() - baselineWorkingSet;
        Assert.True(
            growth < MaxWorkingSetGrowthBytes,
            $"working set grew by {growth / (1024 * 1024)} MiB over {report.Pushed} messages; " +
            $"the budget is {MaxWorkingSetGrowthBytes / (1024 * 1024)} MiB");

        // ----------------------------------------------------------------- database ----
        // Exactly one COMPLETED run per driven cycle and no run of any other shape: the
        // database grows from runs, not from noise passing through.
        var runs = harness.AllRuns();
        Assert.Equal(report.CompletedCycles, runs.Count);
        Assert.All(runs, run => Assert.Equal(RunResult.Completed, run.Result));
        Assert.All(runs, run => Assert.False(run.PendingReview));
        Assert.All(runs, run => Assert.Equal(MentorRouletteId, run.MentorRouletteId));
        Assert.All(runs, run => Assert.Equal(19, run.JobId));

        var eventRows = harness.CountRows("run_events");
        Assert.True(eventRows > 0, "a completed run leaves an event trail");
        Assert.Equal(eventRows, harness.CountEventsBelongingToRuns());

        // Refusals are diagnostics and diagnostics are bounded: sustained refusal traffic may
        // not grow parser_errors past its cap.
        var parserErrorRows = harness.CountRows("parser_errors");
        Assert.True(
            parserErrorRows <= ParserErrorRepository.MaxRows,
            $"parser_errors holds {parserErrorRows} rows; the cap is {ParserErrorRepository.MaxRows}");
        Assert.True(
            report.Final.ParseFailCount > parserErrorRows,
            "the soak must have refused far more messages than it kept rows for, " +
            "otherwise the bound was never exercised");
        // Undeclared opcodes are the bulk of the noise and must produce no rows at all, so the
        // cap above is exercised by genuine refusals only.
        Assert.Equal(0, harness.CountParserErrorRowsOfKind("E_UNKNOWN_OPCODE"));

        var databaseGrowth = harness.DatabaseBytes() - baselineDatabaseBytes;
        var budget = (report.CompletedCycles * 64L * 1024) + (8L * 1024 * 1024);
        Assert.True(
            databaseGrowth < budget,
            $"the database grew by {databaseGrowth} bytes for {report.CompletedCycles} runs; " +
            $"the budget is {budget} bytes");

        // ---------------------------------------------------------------- dashboard ----
        // Statistics must still answer promptly with the soak's rows in place.
        var stopwatch = Stopwatch.StartNew();
        var dashboard = harness.Statistics.GetDashboard();
        stopwatch.Stop();

        Assert.Equal(report.CompletedCycles, dashboard.CompletedCount);
        Assert.Equal(report.CompletedCycles, dashboard.AttemptCount);
        Assert.Equal(0, dashboard.UnfinishedPendingReview);
        Assert.True(
            stopwatch.Elapsed < MaxDashboardLatency,
            $"GetDashboard took {stopwatch.Elapsed.TotalMilliseconds:F0} ms after the soak; " +
            $"the budget is {MaxDashboardLatency.TotalMilliseconds:F0} ms");

        // docs/release-checklist.md cites these numbers as release evidence, so print them
        // instead of leaving them inside a passing assertion.
        _output.WriteLine(
            "soak: {0:F1}s  pushed={1}  rate={2:F0}/s  bursts={3}  cycles={4}  retries={5}  " +
            "quiet_timeouts={6}",
            report.Elapsed.TotalSeconds, report.Pushed, report.PushRatePerSecond,
            report.Bursts, report.CompletedCycles, harness.RetriedPushes, harness.QuietTimeouts);
        _output.WriteLine(
            "soak: decoded={0}  accepted={1}  dropped={2}  decode_errors={3}  " +
            "parse_ok={4}  parse_fail={5}  ignored={6}  duplicates={7}  depth={8}/{9}",
            report.Final.MessagesDecoded, accepted, report.Final.DroppedCount,
            report.Final.DecodeErrors, report.Final.ParseOkCount, report.Final.ParseFailCount,
            report.Final.IgnoredCount, report.Final.DuplicateCount, report.Final.QueueDepth,
            report.Final.QueueCapacity);
        _output.WriteLine(
            "soak: working_set_growth={0} bytes  db_growth={1} bytes  parser_error_rows={2}  " +
            "dashboard={3:F1} ms",
            growth, databaseGrowth, parserErrorRows, stopwatch.Elapsed.TotalMilliseconds);

        // ------------------------------------------------------------- state machine ----
        // The deadline can fall inside the noise interval that follows a cycle, where a valid
        // job event normalizes Completed to Idle without changing the stored result. Neither
        // state leaves a run in flight for teardown to interrupt.
        Assert.Contains(report.RunStateBeforeStop, new[] { RunState.Completed, RunState.Idle });
        Assert.Equal(RunState.Idle, harness.Pipeline.RunState);
        Assert.Equal(CaptureControllerState.Idle, harness.Controller.State);

        var session = harness.Sessions.Get(report.CaptureSessionId)!;
        Assert.NotNull(session.EndedAtUtc);
    }

    [Fact]
    public void JobNoiseAfterACompletedCycleClearsOnlyTheObservableTerminalState()
    {
        var databasePath = Path.Combine(_directory, "completed-then-noise.db");
        using var harness = SoakHarness.Start(databasePath);
        harness.Controller.Start();
        var epoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Assert.Equal(4, harness.PushCycle(0, epoch));
        Assert.Equal(RunState.Completed, harness.Pipeline.RunState);
        var completed = Assert.Single(harness.AllRuns());
        Assert.Equal(RunResult.Completed, completed.Result);
        var eventRows = harness.CountRows("run_events");
        var revisionRows = harness.CountRows("run_revisions");

        Assert.Equal(1, harness.PushJobNoiseAndWait(epoch + 2_000_000));

        Assert.Equal(RunState.Idle, harness.Pipeline.RunState);
        Assert.Null(harness.Pipeline.GetCurrentRun().Run);
        Assert.Equal(completed, Assert.Single(harness.AllRuns()));
        Assert.Equal(eventRows, harness.CountRows("run_events"));
        Assert.Equal(revisionRows, harness.CountRows("run_revisions"));
        Assert.Equal(5, harness.Controller.Snapshot().ParseOkCount);
        Assert.Empty(harness.SinkFailures);
    }

    /// <summary>
    /// The bounded queue in isolation: offered far more than it can hold, against a
    /// deliberately slow sink, delivered plus dropped must still equal offered exactly.
    /// </summary>
    [Fact]
    public void TheBoundedQueueAccountsForEveryMessageUnderSustainedOverload()
    {
        const int offered = 200_000;
        var sink = new SlowCountingSink(everyNth: 5_000);
        using var queue = new DecodedMessageQueue(sink, DecodedMessageQueue.MinCapacity);

        for (var index = 0; index < offered; index++)
        {
            queue.Offer(Message(index));
        }

        queue.Complete(TimeSpan.FromSeconds(30));

        Assert.Equal(offered, queue.DeliveredCount + queue.DroppedCount);
        Assert.Equal(queue.DeliveredCount, sink.AcceptedCount);
        Assert.True(queue.DroppedCount > 0, "an overloaded bounded queue must drop");
        Assert.True(queue.Depth <= queue.Capacity);
        Assert.Equal(0, queue.SinkErrorCount);
    }

    private static Protocol.Decoded.DecodedMessage Message(int index) =>
        new(
            "soak-session",
            Protocol.Decoded.MessageDirection.Inbound,
            DateTimeOffset.UnixEpoch.AddMilliseconds(index),
            TimeSpan.FromMilliseconds(index),
            index,
            FfxivFraming.SegmentTypeIpc,
            OpcodeUnknownA,
            Array.Empty<byte>(),
            ConnectionKey.From("soak-session", 0, 1, 0, 0));

    private static long MeasureWorkingSet()
    {
        // Two collections with a finalizer pass between them: the first queues finalizable
        // objects, the second reclaims what they held. Without it the measured growth is
        // mostly uncollected garbage.
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return process.WorkingSet64;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Windows can hold a WAL handle briefly; the temp cleaner will get it.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }
    }

    /// <summary>A sink that is slow on purpose, to force the queue into its drop path.</summary>
    private sealed class SlowCountingSink : Protocol.Decoded.IDecodedMessageSink
    {
        private readonly int _everyNth;
        private long _accepted;

        public SlowCountingSink(int everyNth) => _everyNth = everyNth;

        public long AcceptedCount => Interlocked.Read(ref _accepted);

        public void Accept(Protocol.Decoded.DecodedMessage message)
        {
            var count = Interlocked.Increment(ref _accepted);
            if (count % _everyNth == 0)
            {
                Thread.Sleep(1);
            }
        }
    }

    /// <summary>One soak run, measured through the shipping diagnostics surface.</summary>
    private sealed record SoakReport(
        TimeSpan Elapsed,
        long Pushed,
        long MalformedPushed,
        int Bursts,
        int CompletedCycles,
        double PushRatePerSecond,
        CaptureControllerState StateBeforeStop,
        RunState RunStateBeforeStop,
        string CaptureSessionId,
        CaptureDiagnosticsSnapshot Final);

    /// <summary>
    /// The graph <c>CollectorHost.BuildCaptureServices</c> builds in the shipping process, with
    /// the source and the machine detectors substituted, assembled by hand so the test holds
    /// the objects it reads counters from.
    /// </summary>
    private sealed class SoakHarness : IDisposable
    {
        private readonly SqliteDatabase _database;
        private readonly FakeCaptureSource _source;
        private readonly List<string> _sinkFailures = new();
        private bool _disposed;

        private SoakHarness(
            SqliteDatabase database,
            FakeCaptureSource source,
            CaptureController controller,
            LiveProtocolPipeline pipeline,
            RunRepository runs,
            CaptureSessionRepository sessions,
            StatisticsRepository statistics)
        {
            _database = database;
            _source = source;
            Controller = controller;
            Pipeline = pipeline;
            Runs = runs;
            Sessions = sessions;
            Statistics = statistics;
        }

        public CaptureController Controller { get; }

        public LiveProtocolPipeline Pipeline { get; }

        public RunRepository Runs { get; }

        public CaptureSessionRepository Sessions { get; }

        public StatisticsRepository Statistics { get; }

        /// <summary>Times the parser thread could not be waited down to an empty queue.</summary>
        public int QuietTimeouts { get; private set; }

        /// <summary>Times a run message had to be pushed again because the queue had dropped it.</summary>
        public int RetriedPushes { get; private set; }

        public IReadOnlyList<string> SinkFailures
        {
            get
            {
                lock (_sinkFailures)
                {
                    return _sinkFailures.ToArray();
                }
            }
        }

        public static SoakHarness Start(string databasePath)
        {
            var clock = SystemClock.Instance;
            var database = SqliteDatabase.Open(databasePath, clock);
            try
            {
                var settings = new SettingsRepository(database, clock);
                settings.EnsureDefaults();

                var runs = new RunRepository(database);
                var sessions = new CaptureSessionRepository(database);
                var statistics = new StatisticsRepository(
                    database, settings, JobCatalog.Default, DutyCatalog.Default);
                var liveEvents = new LiveEventBus(clock);
                var profile = LoadSoakProfile();
                var pipeline = new LiveProtocolPipeline(
                    database,
                    clock,
                    liveEvents,
                    game => new ProfileSelection(
                        ProfileCompatibilityStatus.Verified,
                        profile.ToBinding(),
                        profile,
                        game.Region,
                        game.GameBuild,
                        "soak profile"));

                var source = new FakeCaptureSource();
                var services = CaptureFakes.Ready(source, GameBuild) with
                {
                    Sink = pipeline,
                    ParserStats = pipeline,
                    Profile = pipeline,
                    Lifecycle = pipeline,
                    RunState = () => pipeline.RunState,
                    Clock = clock,
                    Settings = settings,
                    Sessions = sessions,
                    Database = database,
                    CollectorVersion = Program.Version,
                };

                var controller = new CaptureController(services);
                return new SoakHarness(
                    database, source, controller, pipeline, runs, sessions, statistics);
            }
            catch
            {
                database.Dispose();
                throw;
            }
        }

        /// <summary>
        /// The checked-in synthetic profile, remapped onto the IPC segment type.
        ///
        /// The profile on disk declares segment type 61440 so it can never match a real client,
        /// but the framing reader exposes an opcode only for the IPC segment type. Opcodes,
        /// offsets, lengths and constraints are untouched.
        /// </summary>
        private static ProtocolProfile LoadSoakProfile()
        {
            var path = Path.Combine(
                AppContext.BaseDirectory, "protocol-profiles", "synthetic", "synthetic-v1.json");
            var loaded = ProfileLoader.Validate(path).Profile
                ?? throw new InvalidOperationException(
                    "the checked-in synthetic profile must load: " + path);

            return loaded with
            {
                ProfileId = "soak-synthetic",
                Region = Region.Cn,
                GameBuild = GameBuild,
                Status = ProfileCompatibilityStatus.Verified,
                Messages = loaded.Messages
                    .Select(message => message with { SegmentType = FfxivFraming.SegmentTypeIpc })
                    .ToArray(),
            };
        }

        public long DatabaseBytes()
        {
            long total = 0;
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var path = _database.Path + suffix;
                if (File.Exists(path))
                {
                    total += new FileInfo(path).Length;
                }
            }

            return total;
        }

        /// <summary>
        /// Every stored run, paged the way a client has to page it.
        ///
        /// The page count is bounded so that a paging bug which never advances fails the soak
        /// with its state rather than hanging it.
        /// </summary>
        public IReadOnlyList<MentorRun> AllRuns()
        {
            const int pageSize = 200;
            const int maxPages = 1000;

            var all = new List<MentorRun>();
            for (var page = 1; page <= maxPages; page++)
            {
                var slice = Runs.Query(null, null, page, pageSize);
                all.AddRange(slice.Items);
                if (all.Count >= slice.Total || slice.Items.Count == 0)
                {
                    return all;
                }
            }

            throw new InvalidOperationException(
                $"paging did not terminate after {maxPages} pages of {pageSize}; " +
                $"collected {all.Count} runs");
        }

        public long CountRows(string table) =>
            _database.Read(_ =>
            {
                using var command = _database.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM " + table + ";";
                return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            });

        public long CountParserErrorRowsOfKind(string kind) =>
            _database.Read(_ =>
            {
                using var command = _database.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM parser_errors WHERE kind = $kind;";
                command.Parameters.AddWithValue("$kind", kind);
                return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            });

        public long CountEventsBelongingToRuns() =>
            _database.Read(_ =>
            {
                using var command = _database.CreateCommand();
                command.CommandText =
                    "SELECT COUNT(*) FROM run_events e " +
                    "WHERE EXISTS (SELECT 1 FROM mentor_runs r WHERE r.run_id = e.run_id);";
                return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            });

        /// <summary>Runs the mixed stream for <paramref name="duration"/> and reports.</summary>
        public SoakReport Drive(TimeSpan duration)
        {
            var started = Controller.Start();
            var sessionId = started.CaptureSessionId
                ?? throw new InvalidOperationException("a started capture has a session id");

            var epoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var clock = Stopwatch.StartNew();
            var deadline = duration;
            var nextBurst = BurstInterval;
            var nextCycle = CycleInterval;

            long pushed = 0;
            long malformed = 0;
            var bursts = 0;
            var cycles = 0;
            var noiseIndex = 0;

            while (clock.Elapsed < deadline)
            {
                // --- paced noise until the next scheduled event -------------------------
                var until = Min(Min(nextBurst, nextCycle), deadline);
                while (clock.Elapsed < until)
                {
                    var batchStart = clock.Elapsed;
                    for (var i = 0; i < 200 && clock.Elapsed < until; i++)
                    {
                        malformed += PushNoise(noiseIndex++, epoch);
                        pushed++;
                    }

                    // Rate governor. Sleeping rather than spinning keeps the parser thread
                    // scheduled, so drop counts reflect the queue instead of the pusher
                    // starving its own consumer.
                    var expected = TimeSpan.FromSeconds(200d / TargetRatePerSecond);
                    var actual = clock.Elapsed - batchStart;
                    if (actual < expected)
                    {
                        Thread.Sleep(1);
                    }
                }

                if (clock.Elapsed >= deadline)
                {
                    break;
                }

                // --- burst ---------------------------------------------------------------
                if (clock.Elapsed >= nextBurst)
                {
                    for (var i = 0; i < BurstSize; i++)
                    {
                        malformed += PushNoise(noiseIndex++, epoch);
                        pushed++;
                    }

                    bursts++;
                    nextBurst = clock.Elapsed + BurstInterval;
                }

                // --- one complete, valid mentor run --------------------------------------
                if (clock.Elapsed >= nextCycle)
                {
                    // Let the flood clear first: a drop-oldest queue would eat the run's four
                    // messages. The soak measures whether the stack stays correct under load,
                    // not whether four specific packets survive a deliberate overload.
                    WaitForQuietQueue();
                    pushed += PushCycle(cycles, epoch);
                    cycles++;
                    nextCycle = clock.Elapsed + CycleInterval;
                }
            }

            clock.Stop();

            // Read every counter from one snapshot taken while the session is still open:
            // Stop() releases the run and the queue, and their counters go with them.
            WaitForQuietQueue(TimeSpan.FromSeconds(30));
            var stateBefore = Controller.State;
            var runStateBefore = Pipeline.RunState;
            var final = Controller.Snapshot();

            Controller.Stop();

            return new SoakReport(
                clock.Elapsed,
                pushed,
                malformed,
                bursts,
                cycles,
                pushed / Math.Max(clock.Elapsed.TotalSeconds, 0.001),
                stateBefore,
                runStateBefore,
                sessionId,
                final);
        }

        private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;

        /// <summary>
        /// One noise message. Returns 1 when it was deliberately malformed, so the caller can
        /// reconcile the decode-error counter afterwards.
        /// </summary>
        private long PushNoise(int index, long epoch)
        {
            switch (index % 11)
            {
                case 0:
                case 1:
                case 2:
                case 3:
                    // An opcode no profile claims: parsed, refused, counted.
                    _source.PushRaw(IpcMessage(OpcodeUnknownA, new byte[8]), epochMs: epoch + index);
                    return 0;

                case 4:
                case 5:
                    _source.PushRaw(IpcMessage(OpcodeUnknownB, new byte[4]), epochMs: epoch + index);
                    return 0;

                case 6:
                    // A declared length that does not fit the buffer: the framing reader
                    // refuses it before the queue and counts it as a decode error, a different
                    // bucket from a parser refusal.
                    _source.PushRaw(TruncatedMessage(), epochMs: epoch + index);
                    return 1;

                case 7:
                    // A profile opcode carrying the wrong payload length: framing accepts it,
                    // the parser refuses it on the declared length rule.
                    _source.PushRaw(IpcMessage(OpcodeZone, new byte[3]), epochMs: epoch + index);
                    return 0;

                case 8:
                    // A field outside the profile's declared constraints: roulette_id 0 is
                    // below the declared minimum, so the message is refused even though its
                    // opcode and length are right.
                    _source.PushRaw(IpcMessage(OpcodePop, new byte[8]), epochMs: epoch + index);
                    return 0;

                case 9:
                    // A valid message the state machine has no use for while idle. It keeps the
                    // stream from being exclusively refusals and gives the next case something
                    // to repeat, so the duplicate it produces is a real one.
                    _source.PushRaw(ValidJobMessage(), epochMs: epoch + index);
                    return 0;

                default:
                    // An exact repeat of the previous message: same bytes, same epoch, so the
                    // event key is identical and the parser must recognise it as already seen
                    // rather than as new evidence.
                    _source.PushRaw(ValidJobMessage(), epochMs: epoch + index - 1);
                    return 0;
            }
        }

        /// <summary>
        /// Drives one complete run: pop, zone, job, victory. Returns how many messages were
        /// pushed, which is four plus any retry.
        ///
        /// Retries are needed because the deliberately overloaded queue drops the oldest waiting
        /// message and can legitimately lose one of the four. Re-pushing identical bytes with an
        /// identical epoch is safe by construction: the event key is derived from exactly those,
        /// so a late copy is recognised as a duplicate by both the parser and the state machine.
        /// This is the idempotency the replay path relies on.
        /// </summary>
        public int PushCycle(int cycle, long epoch)
        {
            var baseEpoch = epoch + 1_000_000 + (cycle * 1000);
            var contentId = (uint)(900_000 + cycle);

            var pop = new byte[8];
            BinaryPrimitives.WriteUInt16LittleEndian(pop, MentorRouletteId);
            BinaryPrimitives.WriteUInt32LittleEndian(pop.AsSpan(4), contentId);

            var zone = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(zone, 2001);
            BinaryPrimitives.WriteUInt32LittleEndian(zone.AsSpan(4), contentId);
            BinaryPrimitives.WriteUInt32LittleEndian(zone.AsSpan(8), (uint)(3000 + cycle));
            zone[12] = 1;

            var job = new byte[4];
            job[0] = 19;

            var result = new byte[4];
            result[0] = 1;

            var pushed = 0;
            pushed += PushUntil(IpcMessage(OpcodePop, pop), baseEpoch, RunState.MentorMatched);
            pushed += PushUntil(IpcMessage(OpcodeZone, zone), baseEpoch + 1, RunState.EnteredDuty);

            // The job message does not move the state machine, it fills a field on the run in
            // flight, so there is no state to wait on and one push is enough.
            _source.PushRaw(IpcMessage(OpcodeJob, job), epochMs: baseEpoch + 2);
            pushed++;

            pushed += PushUntil(IpcMessage(OpcodeResult, result), baseEpoch + 3, RunState.Completed);
            return pushed;
        }

        /// <summary>Pushes a fresh, valid job event and waits until its idle state is observable.</summary>
        public int PushJobNoiseAndWait(long epochMs) =>
            PushUntil(ValidJobMessage(), epochMs, RunState.Idle);

        /// <summary>Pushes one message until the state machine reports <paramref name="expected"/>.</summary>
        private int PushUntil(byte[] message, long epochMs, RunState expected)
        {
            const int maxAttempts = 20;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                _source.PushRaw(message, epochMs: epochMs);
                if (WaitForRunState(expected, TimeSpan.FromSeconds(3)))
                {
                    return attempt;
                }

                RetriedPushes++;
            }

            throw new TimeoutException(
                $"the state machine never reached {expected} after {maxAttempts} pushes; " +
                $"it is in {Pipeline.RunState}. " + Describe());
        }

        /// <summary>Counters worth seeing when the soak gives up on a step.</summary>
        private string Describe()
        {
            var snapshot = Controller.Snapshot();
            return string.Create(
                CultureInfo.InvariantCulture,
                $"state={snapshot.State} decoded={snapshot.MessagesDecoded} " +
                $"dropped={snapshot.DroppedCount} depth={snapshot.QueueDepth}/{snapshot.QueueCapacity} " +
                $"parse_ok={snapshot.ParseOkCount} parse_fail={snapshot.ParseFailCount} " +
                $"decode_errors={snapshot.DecodeErrors} error={snapshot.LastErrorCode}");
        }

        /// <summary>
        /// Waits for the parser thread to catch up. Returns false on timeout rather than
        /// throwing: a queue that never empties is a legitimate outcome of a deliberate
        /// overload and the caller retries.
        /// </summary>
        private bool WaitForQuietQueue(TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
            while (DateTime.UtcNow < deadline)
            {
                if (Controller.Snapshot().QueueDepth == 0)
                {
                    return true;
                }

                Thread.Sleep(2);
            }

            QuietTimeouts++;
            return false;
        }

        private bool WaitForRunState(RunState expected, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (Pipeline.RunState == expected)
                {
                    return true;
                }

                Thread.Sleep(2);
            }

            return false;
        }

        private static byte[] IpcMessage(ushort opcode, ReadOnlySpan<byte> payload)
        {
            var message = FakeCaptureSource.BuildIpcMessage(opcode, payload.Length);
            payload.CopyTo(message.AsSpan(FfxivFraming.HeaderBytes));
            return message;
        }

        /// <summary>A well-formed PLAYER_JOB message the profile accepts.</summary>
        private static byte[] ValidJobMessage()
        {
            var job = new byte[4];
            job[0] = 19;
            return IpcMessage(OpcodeJob, job);
        }

        /// <summary>A frame whose declared length runs off the end of the buffer.</summary>
        private static byte[] TruncatedMessage()
        {
            var message = FakeCaptureSource.BuildIpcMessage(OpcodeUnknownA, 8);
            BinaryPrimitives.WriteUInt32LittleEndian(message, (uint)message.Length + 4096);
            return message;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Controller.Dispose();
            _source.Dispose();
            _database.Dispose();
        }
    }
}
