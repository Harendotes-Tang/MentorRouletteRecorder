using System.Text.Json;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Domain.StateMachine;
using MentorRecorder.Collector.Export;
using MentorRecorder.Collector.Protocol.Parsing;
using MentorRecorder.Collector.Protocol.Pipeline;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Reference;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

public sealed class CandidateRoundReporterTests : IDisposable
{
    private const int Registration = 0x0215;
    private const int FinderState = 0x016f;
    private const int ZoneEntry = 0x00ab;

    private readonly TestDatabase _fixture = new();
    private readonly CandidateObservationRepository _ledger;
    private readonly SettingsRepository _settings;
    private readonly string _session = Guid.NewGuid().ToString("D");
    private ProtocolProfile? _profile = GuessingProfile();
    private long _t;

    public CandidateRoundReporterTests()
    {
        _ledger = new CandidateObservationRepository(_fixture.Database, _fixture.Clock);
        _settings = new SettingsRepository(_fixture.Database, _fixture.Clock);
    }

    private CandidateRoundReporter Reporter() => new(_ledger, _settings, () => _profile);

    [Fact]
    public void TheRoundIsReadAtTheGuessedPositionsAsItUnfolds()
    {
        var duty = DutyCatalog.Default.Documents.First(document => document.Region == Region.Global && !document.IsSample)
            .Rows.Values.First(row => row.TerritoryId is not null);
        StartRound();
        Assert.Equal(new CandidateRoundReport(_fixture.Clock.UtcNow, null, null, null, null), Reporter().Read());

        Add(ZoneEntry, 136, 2, 641);             // logging in: a zone, but no queue yet
        Add(Registration, 24, 0, 2);
        Add(FinderState, 40, 1, 40);
        var first = Reporter().Read()!;
        Assert.Equal(new RoundGuess(2, RouletteCatalog.Default.NameOf(2, Region.Global)), first.FirstRoulette);
        Assert.Equal(new RoundGuess(40, JobCatalog.Default.Find(40)!.NameZh), first.JobBefore);
        Assert.Null(first.JobAfter);
        Assert.Null(first.Duty);

        Add(ZoneEntry, 136, 2, 641);             // the restart between the two queues
        Add(Registration, 24, 0, 9);
        Add(FinderState, 40, 1, 24);
        Add(ZoneEntry, 136, 2, duty.TerritoryId!.Value);
        Add(ZoneEntry, 136, 2, 641);             // leaving the duty
        var done = Reporter().Read()!;
        Assert.Equal(first.FirstRoulette, done.FirstRoulette);
        Assert.Equal(first.JobBefore, done.JobBefore);
        Assert.Equal(new RoundGuess(24, JobCatalog.Default.Find(24)!.NameZh), done.JobAfter);
        Assert.Equal(duty.TerritoryId, done.Duty!.Id);
        Assert.NotNull(done.Duty.Name);
        Assert.NotNull(done.Duty.ContentId);
    }

    [Fact]
    public void AZoneThatIsNoDutyKeepsItsNumberAndHasNoName()
    {
        StartRound();
        Add(Registration, 24, 0, 2);
        Add(Registration, 24, 0, 9);
        Add(ZoneEntry, 136, 2, 65_000);

        Assert.Equal(new RoundGuess(65_000, null), Reporter().Read()!.Duty);
    }

    [Fact]
    public void AnEarlierRoundIsNotShownAsThisOne()
    {
        StartRound();
        Add(Registration, 24, 0, 5);
        Stop();
        _fixture.Clock.UtcNow = _fixture.Clock.UtcNow.AddMinutes(1);
        // Switched off, the round the export follows is still the one just finished.
        Assert.Equal(5, Reporter().Read()!.FirstRoulette!.Id);

        StartRound();
        Assert.Null(Reporter().Read()!.FirstRoulette);
        Add(Registration, 24, 0, 2);
        Assert.Equal(2, Reporter().Read()!.FirstRoulette!.Id);
    }

    [Fact]
    public void NothingIsReportedWithoutARoundOrWithoutGuesses()
    {
        Assert.Null(Reporter().Read());
        StartRound();
        _profile = _profile! with { Hypotheses = _profile.Hypotheses.Select(h => h with { Guesses = [] }).ToArray() };
        Assert.Null(Reporter().Read());
        _profile = null;
        Assert.Null(Reporter().Read());
    }

    [Fact]
    public void AFailureToReadTheGuessesIsReportedAndLeavesTheExportWhole()
    {
        StartRound();
        Add(Registration, 24, 0, 2);
        var failures = new List<Exception>();
        var broken = new CandidateRoundReporter(_ledger, _settings, () => throw new InvalidOperationException("catalogue unreadable"), failures.Add);

        Assert.Null(broken.Read());
        Assert.IsType<InvalidOperationException>(Assert.Single(failures));

        var result = new CandidateEvidenceExporter(_ledger, _fixture.Clock, _fixture.Path, broken.Read)
            .Export(answers: new CandidateRoundAnswers("Leveling", null, null, null));
        using var document = JsonDocument.Parse(File.ReadAllText(result.TargetPath));
        Assert.Equal(1, document.RootElement.GetProperty("observation_count").GetInt32());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("round_report").GetProperty("guessed").ValueKind);
        Assert.Equal("Leveling", document.RootElement.GetProperty("round_report").GetProperty("answered").GetProperty("first_roulette").GetString());
    }

    [Fact]
    public void OnlySwitchingTheRoundOnMovesItsStart()
    {
        StartRound();
        var started = _settings.GetSettingUpdatedAtUtc(CaptureSettingsStore.CandidateRoundMarkSetting);
        Assert.Equal(_fixture.Clock.UtcNow, started);

        _fixture.Clock.UtcNow = _fixture.Clock.UtcNow.AddMinutes(5);
        CaptureSettingsStore.Apply(_settings, new CaptureSettingsUpdate { CandidateValidationEnabled = true });
        CaptureSettingsStore.Apply(_settings, new CaptureSettingsUpdate { UpdateCheckEnabled = false });
        Stop();
        Assert.Equal(started, _settings.GetSettingUpdatedAtUtc(CaptureSettingsStore.CandidateRoundMarkSetting));

        StartRound();
        Assert.Equal(_fixture.Clock.UtcNow, _settings.GetSettingUpdatedAtUtc(CaptureSettingsStore.CandidateRoundMarkSetting));
    }

    [Fact]
    public void TheEvidenceFileCarriesTheGuessesBesideWhatThePlayerWrote()
    {
        StartRound();
        Add(Registration, 24, 0, 2);
        Stop();
        var exporter = new CandidateEvidenceExporter(_ledger, _fixture.Clock, _fixture.Path, Reporter().Read);

        var result = exporter.Export(answers: new CandidateRoundAnswers("Leveling", null, null, "The Praetorium"));

        using var document = JsonDocument.Parse(File.ReadAllText(result.TargetPath));
        var report = document.RootElement.GetProperty("round_report");
        Assert.Equal(2, report.GetProperty("guessed").GetProperty("first_roulette").GetProperty("id").GetInt32());
        Assert.Equal(JsonValueKind.Null, report.GetProperty("guessed").GetProperty("duty").ValueKind);
        Assert.Equal("Leveling", report.GetProperty("answered").GetProperty("first_roulette").GetString());
        Assert.Equal(JsonValueKind.Null, report.GetProperty("answered").GetProperty("job_before").ValueKind);
        Assert.Equal("The Praetorium", report.GetProperty("answered").GetProperty("duty").GetString());

        // No reporter and no answers: the file is what it was before this section existed.
        var plain = new CandidateEvidenceExporter(_ledger, _fixture.Clock, _fixture.Path).Export();
        using var bare = JsonDocument.Parse(File.ReadAllText(plain.TargetPath));
        Assert.False(bare.RootElement.TryGetProperty("round_report", out _));
    }

    /// <summary>
    /// The round a tester is asked to do, start to finish, on the candidate profile that ships:
    /// the real pipeline, the real ledger with its table constraints, the report and the export.
    /// Two launches of the game, a queue that is withdrawn, a job change, the mentor queue, the
    /// duty and the way out, with a few thousand movement messages in between.
    /// </summary>
    [Fact]
    public void AWholeRoundOnTheShippedGlobalCandidateIsRecordedGuessedAndExported()
    {
        const string build = "2026.11.03.0000.0000";
        var shipped = ProfileLoader.Load(Path.Combine(ProfileCatalog.FindDefaultRoot()!, "global", "global.2026.11.03.candidate.json"));
        var duty = DutyCatalog.Default.Documents.First(document => document.Region == Region.Global && !document.IsSample)
            .Rows.Values.First(row => row.TerritoryId is not null);
        var eligible = shipped.Hypotheses.Where(h => ResearchPayloadPolicy.IsEligible(shipped, h))
            .Select(h => "0x" + h.Opcode.ToString("x4")).ToArray();
        CaptureSettingsStore.Apply(_settings, new CaptureSettingsUpdate
            { CandidateValidationEnabled = true, ResearchPayloadOpcodes = eligible });

        var pipeline = new LiveProtocolPipeline(_fixture.Database, _fixture.Clock, new Ipc.LiveEventBus(_fixture.Clock),
            _ => new ProfileSelection(ProfileCompatibilityStatus.Unsupported, ProfileBinding.FailClosed, null, Region.Global, build, "test"),
            _ => shipped);
        pipeline.CandidateObserved += row => Assert.True(_ledger.Add(row), row.HypothesisName);
        pipeline.ApplyCandidateSettings(true, researchPayloadOpcodes: eligible);
        pipeline.Refresh(GameProcessDetection.NotRunning with { Region = Region.Global, GameBuild = build });

        var start = _fixture.Clock.UtcNow;
        var session = "";
        long t = 0;
        void Send(int opcode, int length, bool outbound = false, int offset = 0, int value = 0, long gap = 50)
        {
            t += gap;
            var payload = new byte[length];
            payload[offset] = (byte)value;
            if (value > byte.MaxValue) payload[offset + 1] = (byte)(value >> 8);
            pipeline.Accept(new Protocol.Decoded.DecodedMessage(session,
                outbound ? Protocol.Decoded.MessageDirection.Outbound : Protocol.Decoded.MessageDirection.Inbound,
                start.AddMilliseconds(t), TimeSpan.FromMilliseconds(t), 0, 3, (ushort)opcode, payload, "game-connection"));
        }
        void ZoneChange(int territory, bool withLoginOnlyMember)
        {
            Send(0x02f5, 16, gap: 10_000);
            Send(0x02d8, 16);
            Send(0x010d, 640);
            Send(0x03b0, 16);
            Send(0x027d, 136);
            if (withLoginOnlyMember) Send(0x0317, 1216);
            Send(ZoneEntry, 136, offset: 2, value: territory);
            Send(0x02ff, 16);
            Send(0x031f, 456);
            Send(0x0346, 808);
            Send(0x0176, 656);
            Send(0x02fc, 664);
        }
        void Launch()
        {
            session = Guid.NewGuid().ToString("D");
            t = 0;
            start = start.AddMinutes(30);
            pipeline.OnCaptureStarted(session);
        }

        Launch();                                                   // first launch: queue, pop, withdraw
        ZoneChange(641, withLoginOnlyMember: true);
        for (var i = 0; i < 40; i++) Send(0x022b, 24, outbound: true);
        Send(Registration, 24, outbound: true, value: 2, gap: 5000);
        Send(FinderState, 40, offset: 1, value: 40);
        Send(0x03bf, 40);
        Send(0x0337, 8);
        Send(0x0332, 16, gap: 2000);
        Send(0x02cb, 24);
        Send(FinderState, 40, offset: 1, value: 40, gap: 10_000);   // withdrawn
        pipeline.OnCaptureStopped(session, CaptureEndReason.ProcessExit);

        Launch();                                                   // second launch: the mentor queue and the duty
        ZoneChange(641, withLoginOnlyMember: true);
        Send(Registration, 24, outbound: true, value: 9, gap: 5000);
        Send(FinderState, 40, offset: 1, value: 24);
        for (var i = 0; i < 3000; i++) Send(0x022b, 24, outbound: true, gap: 160);   // walking about while queued
        Send(0x02cb, 24);
        Send(FinderState, 40, offset: 1, value: 24);
        ZoneChange(duty.TerritoryId!.Value, withLoginOnlyMember: false);
        for (var i = 0; i < 2000; i++) Send(0x0ddd, 32, gap: 300);  // fighting: messages nobody has named
        Send(0x0eee, 48);                                           // once, at the end: what a clear would look like
        ZoneChange(641, withLoginOnlyMember: true);
        pipeline.OnCaptureStopped(session, CaptureEndReason.ProcessExit);

        var stored = _ledger.ReadEvidence().Observations.Select(entry => entry.Observation).ToList();
        Assert.Equal(4, stored.Count(row => row.HypothesisName == "ZONE_LOAD"));
        Assert.Equal(2, stored.Count(row => row.HypothesisName == "QUEUE_REGISTRATION" && row.PayloadHex is not null));
        // The movement flood neither stopped anything nor pushed the start of the round out.
        Assert.InRange(stored.Count(row => row.Opcode == 0x022b && row.HypothesisName == "FINDER_ACTION_022B"),
            1, 2 * CandidateObserver.MaxRowsPerHypothesis);
        Assert.Equal(3000, stored.Where(row => row.HypothesisName == CandidateObserver.QueueWindowSampleName && row.Opcode == 0x022b)
            .Sum(row => row.Occurrences));
        var clear = stored.Single(row => row.HypothesisName == CandidateObserver.DutyWindowSampleName && row.Opcode == 0x0eee);
        Assert.Equal(1, clear.Occurrences);
        Assert.Equal(2000, stored.Single(row => row.HypothesisName == CandidateObserver.DutyWindowSampleName && row.Opcode == 0x0ddd).Occurrences);

        var report = new CandidateRoundReporter(_ledger, _settings, pipeline.CandidateDisplayProfile).Read()!;
        Assert.Equal(2, report.FirstRoulette!.Id);
        Assert.Equal(40, report.JobBefore!.Id);
        Assert.Equal(24, report.JobAfter!.Id);
        Assert.Equal(duty.TerritoryId, report.Duty!.Id);

        Stop();
        var result = new CandidateEvidenceExporter(_ledger, _fixture.Clock, _fixture.Path,
            new CandidateRoundReporter(_ledger, _settings, pipeline.CandidateDisplayProfile).Read).Export();
        using var document = JsonDocument.Parse(File.ReadAllText(result.TargetPath));
        Assert.Equal(stored.Count, document.RootElement.GetProperty("observations").GetArrayLength());
        Assert.Equal(duty.TerritoryId, document.RootElement.GetProperty("round_report").GetProperty("guessed")
            .GetProperty("duty").GetProperty("id").GetInt32());
    }

    private static ProtocolProfile GuessingProfile() => CandidateObserverTests.Profile() with
    {
        Region = Region.Global,
        Hypotheses =
        [
            Hypothesis("QUEUE_REGISTRATION", Registration, PacketDirection.ClientToServer, 24, new ProfileGuess(ProfileGuessKind.Roulette, 0, 1)),
            Hypothesis("FINDER_STATE_NOTIFICATION", FinderState, PacketDirection.ServerToClient, 40, new ProfileGuess(ProfileGuessKind.Job, 1, 1)),
            Hypothesis("TERRITORY_CANDIDATE", ZoneEntry, PacketDirection.ServerToClient, 136, new ProfileGuess(ProfileGuessKind.Territory, 2, 2)),
        ],
    };

    private static ProfileHypothesis Hypothesis(string name, int opcode, PacketDirection direction, int length, ProfileGuess guess) =>
        new(name, (ushort)opcode, direction, length, null, null, "synthetic", null) { Guesses = [guess] };

    private void StartRound()
    {
        _settings.SetSetting(CaptureSettingsStore.ResearchPayloadOpcodesSetting, "[\"0x0215\",\"0x016f\",\"0x00ab\"]");
        CaptureSettingsStore.Apply(_settings, new CaptureSettingsUpdate { CandidateValidationEnabled = true });
    }

    private void Stop() =>
        CaptureSettingsStore.Apply(_settings, new CaptureSettingsUpdate { CandidateValidationEnabled = false });

    /// <summary>Stores one message whose content is zero except for <paramref name="value"/> at <paramref name="offset"/>.</summary>
    private void Add(int opcode, int length, int offset, int value)
    {
        var payload = new byte[length];
        payload[offset] = (byte)value;
        if (value > byte.MaxValue) payload[offset + 1] = (byte)(value >> 8);
        _t += 100;
        Assert.True(_ledger.Add(new CandidateObservation(Guid.NewGuid().ToString("D"), _session, "candidate-test",
            "SYNTHETIC", null, opcode == Registration ? "C2S" : "S2C", opcode, length, "0123456789ab", "abcdef012345",
            _fixture.Clock.UtcNow.AddMilliseconds(_t), _t, PayloadHex: Convert.ToHexString(payload).ToLowerInvariant())));
    }

    public void Dispose() => _fixture.Dispose();
}
