using System.Text.Json;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Export;
using MentorRecorder.Collector.Protocol.Parsing;
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
