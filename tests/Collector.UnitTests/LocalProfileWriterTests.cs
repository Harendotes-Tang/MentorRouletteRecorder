using System.Text.Json;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// A confirmed calibration becomes a VERIFIED profile file the ordinary loader accepts, with
/// evidence for every opcode and none of the template-only sections.
/// </summary>
public sealed class LocalProfileWriterTests : IDisposable
{
    private const string Build = "2026.09.01.0000.0000";
    private static readonly DateTimeOffset Confirmed = new(2026, 9, 9, 12, 30, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MentorRecorder.Tests", Guid.NewGuid().ToString("N"), "protocol-profiles");

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static CalibrationDraft ReadyDraft(CalibrationTemplate template)
    {
        var messages = new[]
        {
            template.Pop with { Opcode = 0xC002 },
            template.ZoneInitialization with { Opcode = 0xA107 },
            template.ZoneTerritory! with { Opcode = 0xA108 },
            template.PlayerJob! with { Opcode = 0xA109 },
        };
        var events = new[]
        {
            new CalibrationEvent("finder_request-60000", "finder_request", 60_000, Confirmed.AddMinutes(-30), "排本：练级迷宫", 1, null, null, true),
            new CalibrationEvent("pop-120000", "pop", 120_000, Confirmed.AddMinutes(-29), "匹配弹窗：练级迷宫", 1, null, null, true),
            new CalibrationEvent("duty_enter-125000", "duty_enter", 125_000, Confirmed.AddMinutes(-28), "进入副本：沙斯塔夏溶洞", null, 1039, "沙斯塔夏溶洞", true),
            new CalibrationEvent("duty_exit-215000", "duty_exit", 215_000, Confirmed.AddMinutes(-5), "离开副本", null, null, null, true),
        };
        var samples = new Dictionary<string, int>
        {
            ["messages.CONTENT_FINDER_POP.opcode"] = 3,
            ["messages.ZONE_INITIALIZATION.opcode"] = 3,
            ["messages.ZONE_TERRITORY.opcode"] = 1,
            ["messages.PLAYER_JOB.opcode"] = 3,
            [ProfileLoader.CalibrationEvidenceKey] = 2,
        };
        return new CalibrationDraft(
            CalibrationDraftStatus.Ready, Array.Empty<string>(), new CalibrationProgress(true, true, 3, true, true),
            events, messages, 0xC001, samples, new long[] { 1 }, template.Source.ProfileId);
    }

    /// <summary>
    /// A profile whose match is inferred from the queue has to survive the ordinary loader like
    /// any other, carry the request as its CONTENT_FINDER_POP, and open a window long enough for
    /// a real queue: the announcement path measures an accept timer in seconds, this one
    /// measures a mentor roulette queued as a damage dealer.
    /// </summary>
    [Fact]
    public void AQueueInferredProfileIsWrittenAsTheRequestWithAQueueLengthWindow()
    {
        var template = CalibrationObserverTests.Template();
        var request = template.Calibration.FinderRequest;
        var draft = ReadyDraft(template) with
        {
            Messages = new[]
            {
                template.Pop with
                {
                    Opcode = 0xC001,
                    Direction = request.Direction,
                    ExpectedLength = request.ExpectedLength,
                    Fields = new[] { request.RouletteField },
                },
                template.ZoneInitialization with { Opcode = 0xA107 },
                template.ZoneTerritory! with { Opcode = 0xA108 },
                template.PlayerJob! with { Opcode = 0xA109 },
            },
        };
        draft = SetSource(draft, CalibrationMatchSource.QueueRequest);

        var result = LocalProfileWriter.Write(draft, template, Build, Confirmed, _root);

        var report = ProfileLoader.Validate(result.Path);
        Assert.Empty(report.Errors);
        var profile = Assert.IsType<ProtocolProfile>(report.Profile);
        Assert.Equal(ProfileCompatibilityStatus.Verified, profile.Status);
        Assert.Equal(TimeSpan.FromHours(1), profile.MatchWindow);
        var pop = profile.Message("CONTENT_FINDER_POP")!;
        Assert.Equal(PacketDirection.ClientToServer, pop.Direction);
        Assert.Equal(0xC001, pop.Opcode);
        Assert.Equal(24, pop.ExpectedLength);
        Assert.Equal(0, pop.Field("roulette_id")!.Offset);
        Assert.True(profile.MatchFromQueue);
        Assert.True(profile.ToBinding().MatchFromQueue);
        Assert.Contains("推断", profile.ProvenanceSummary, StringComparison.Ordinal);
    }

    private static CalibrationDraft SetSource(CalibrationDraft draft, CalibrationMatchSource source) =>
        draft with { MatchSource = source };

    [Fact]
    public void WritesAVerifiedProfileTheLoaderAndCatalogAccept()
    {
        var template = CalibrationObserverTests.Template();

        var result = LocalProfileWriter.Write(ReadyDraft(template), template, Build, Confirmed, _root);

        Assert.Equal(Path.Combine(_root, "cn", "cn.2026.09.01.0000.0000.local.json"), result.Path);
        Assert.Equal("cn.2026.09.01.0000.0000.local", result.ProfileId);
        Assert.True(File.Exists(result.Path));

        var report = ProfileLoader.Validate(result.Path);
        Assert.Empty(report.Errors);
        var profile = Assert.IsType<ProtocolProfile>(report.Profile);
        Assert.Equal(ProfileCompatibilityStatus.Verified, profile.Status);
        Assert.Equal(Build, profile.GameBuild);
        Assert.Equal(Region.Cn, profile.Region);
        Assert.Equal(9, profile.MentorRouletteId);
        Assert.Equal(TimeSpan.FromSeconds(120), profile.MatchWindow);
        Assert.Equal(result.Sha256, profile.ProfileSha256);
        Assert.Equal(0xC002, profile.Message("CONTENT_FINDER_POP")!.Opcode);
        Assert.Equal(0xA107, profile.Message("ZONE_INITIALIZATION")!.Opcode);
        Assert.Equal(0xA108, profile.Message("ZONE_TERRITORY")!.Opcode);
        Assert.Equal(0xA109, profile.Message("PLAYER_JOB")!.Opcode);
        Assert.Equal(ProfileFieldRole.Selector, profile.Message("CONTENT_FINDER_POP")!.Field("finder_state")!.Role);
        Assert.Equal(new long[] { 3 }, profile.Message("CONTENT_FINDER_POP")!.Field("finder_state")!.Constraints.In);
        Assert.True(profile.ToBinding().IsUsable);

        // Never a template for the next build, never a candidate, never fixture-bound.
        Assert.Null(profile.Calibration);
        Assert.Empty(profile.Hypotheses);
        Assert.Empty(profile.Fixtures);
        using var document = JsonDocument.Parse(File.ReadAllText(result.Path));
        Assert.False(document.RootElement.TryGetProperty("calibration", out _));
        Assert.False(document.RootElement.TryGetProperty("hypotheses", out _));

        var catalog = ProfileCatalog.Load(_root);
        Assert.Contains(catalog.UsableFor(Region.Cn), entry => entry.ProfileId == result.ProfileId);
        Assert.DoesNotContain(Directory.GetFiles(Path.Combine(_root, "cn")), file => file.Contains(".tmp-", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryMessageCarriesObservedEvidenceAndTheRequiredOnesUserConfirmation()
    {
        var template = CalibrationObserverTests.Template();
        var result = LocalProfileWriter.Write(ReadyDraft(template), template, Build, Confirmed, _root);

        using var document = JsonDocument.Parse(File.ReadAllText(result.Path));
        var evidence = document.RootElement.GetProperty("provenance").GetProperty("evidence").EnumerateArray()
            .Select(item => (Field: item.GetProperty("field").GetString(), Method: item.GetProperty("method").GetString()))
            .ToArray();
        foreach (var name in new[] { "CONTENT_FINDER_POP", "ZONE_INITIALIZATION", "ZONE_TERRITORY", "PLAYER_JOB" })
        {
            Assert.Contains(evidence, item => item.Field == "messages." + name + ".opcode" && item.Method == "OBSERVED_LOCAL_TRAFFIC");
        }

        Assert.Contains(evidence, item => item.Field == "messages.CONTENT_FINDER_POP.opcode" && item.Method == "USER_CONFIRMED");
        Assert.Contains(evidence, item => item.Field == "messages.ZONE_INITIALIZATION.opcode" && item.Method == "USER_CONFIRMED");
        Assert.DoesNotContain(evidence, item => item.Method == "SYNTHETIC");
        Assert.Equal("2026-09-09T12:30:00.000Z", document.RootElement.GetProperty("generated_at").GetString());
        var observed = document.RootElement.GetProperty("provenance").GetProperty("evidence").EnumerateArray()
            .Where(item => item.GetProperty("method").GetString() == "OBSERVED_LOCAL_TRAFFIC")
            .ToDictionary(item => item.GetProperty("field").GetString()!, item => item.GetProperty("note").GetString()!);
        Assert.Contains("唯一已知副本进本佐证", observed["messages.CONTENT_FINDER_POP.opcode"], StringComparison.Ordinal);
        Assert.Contains("进本、出本簇各恰好一次", observed["messages.ZONE_INITIALIZATION.opcode"], StringComparison.Ordinal);
    }

    [Fact]
    public void ADraftThatIsNotReadyIsRefusedAndNothingIsWritten()
    {
        var template = CalibrationObserverTests.Template();
        var draft = ReadyDraft(template) with { Status = CalibrationDraftStatus.Observing };

        Assert.Throws<InvalidOperationException>(() => LocalProfileWriter.Write(draft, template, Build, Confirmed, _root));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void ADraftMissingARequiredMessageIsRefusedByTheLoaderBeforeWriting()
    {
        var template = CalibrationObserverTests.Template();
        var ready = ReadyDraft(template);
        var draft = ready with { Messages = ready.Messages.Where(message => message.Name != "ZONE_INITIALIZATION").ToArray() };

        var error = Assert.Throws<InvalidOperationException>(() => LocalProfileWriter.Write(draft, template, Build, Confirmed, _root));
        Assert.Contains("E_PROFILE_MISSING_MESSAGE", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void WritingTheSameBuildAgainReplacesTheFile()
    {
        var template = CalibrationObserverTests.Template();
        var first = LocalProfileWriter.Write(ReadyDraft(template), template, Build, Confirmed, _root);
        var later = ReadyDraft(template) with { Messages = ReadyDraft(template).Messages.Select(m => m.Name == "PLAYER_JOB" ? m with { Opcode = 0xA110 } : m).ToArray() };

        var second = LocalProfileWriter.Write(later, template, Build, Confirmed.AddHours(1), _root);

        Assert.Equal(first.Path, second.Path);
        Assert.NotEqual(first.Sha256, second.Sha256);
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "cn")));
        Assert.Equal(0xA110, ProfileLoader.Load(second.Path).Message("PLAYER_JOB")!.Opcode);
    }

    [Theory]
    [InlineData(Region.Cn, "2026.09.01.0000.0000", "cn.2026.09.01.0000.0000.local")]
    [InlineData(Region.Global, "2026.09.01.0000.0000", "global.2026.09.01.0000.0000.local")]
    [InlineData(Region.Cn, "Weird Build_1", "cn.weird-build-1.local")]
    public void ProfileIdsAreLowercaseFileSafeAndSchemaValid(Region region, string build, string expected)
    {
        var id = LocalProfileWriter.ProfileIdFor(region, build);

        Assert.Equal(expected, id);
        Assert.Matches("^[a-z0-9][a-z0-9.-]{0,63}$", id);
    }

    [Fact]
    public void AnUnknownRegionHasNoDirectory()
    {
        Assert.Throws<ArgumentException>(() => LocalProfileWriter.RegionDirectory(Region.Unknown));
    }
}
