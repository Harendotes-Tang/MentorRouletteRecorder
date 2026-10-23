using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Contracts.Texts;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Tests;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The capture sentences travel twice (contracts/collector-texts.md §2.1): the Chinese an older
/// Desktop shows, unchanged, and beside it the catalogue reference that renders back to exactly that
/// Chinese, or null exactly when the Chinese is.
/// </summary>
public sealed class CaptureTextRefTests
{
    private static CaptureDiagnosticsSnapshot Snapshot(
        NpcapDetection npcap, UserText? hint = null, ProfileStatusSnapshot? profile = null) => new()
    {
        State = CaptureControllerState.Running,
        Npcap = npcap,
        Game = GameProcessDetection.NotRunning,
        Profile = profile ?? NoProfileStatusProvider.Instance.Current,
        Hint = hint,
    };

    private static NpcapDetection Healthy() => new NpcapDetector(FakeNpcapEnvironment.Healthy()).Detect();

    private static NpcapDetection Missing() => new NpcapDetector(new FakeNpcapEnvironment()).Detect();

    [Fact]
    public void CaptureStatus_CarriesTheHintAndItsReference()
    {
        var status = CaptureWire.CaptureStatus(Snapshot(Healthy(), CaptureController.MidstreamReconnectedHint));

        Assert.Equal(CaptureController.MidstreamReconnectedHint.Chinese, status["hint"]!.GetValue<string>());
        Assert.Equal("capture.hint.midstream_reconnected", status["hint_ref"]!["key"]!.GetValue<string>());
        CollectorTextCatalogue.AssertParallel(status["hint"], status["hint_ref"]);
    }

    [Fact]
    public void CaptureStatus_HasANullReferenceExactlyWhenThereIsNoHint()
    {
        var status = CaptureWire.CaptureStatus(Snapshot(Healthy()));

        Assert.True(status.ContainsKey("hint_ref"));
        Assert.Null(status["hint"]);
        Assert.Null(status["hint_ref"]);
    }

    [Theory]
    [InlineData(CaptureSilentReason.Midstream, "capture.hint.midstream")]
    [InlineData(CaptureSilentReason.NoPacketsOnAdapter, "capture.hint.no_packets")]
    [InlineData(CaptureSilentReason.NoStreamOwnership, "capture.hint.no_ownership")]
    public void EveryHintIsACatalogueText(CaptureSilentReason reason, string key)
    {
        var hint = CaptureController.HintFor(reason)!;

        Assert.Equal(key, hint.Key);
        Assert.Equal(hint.Chinese, CollectorTextCatalogue.Render(hint.ToJson()));
    }

    [Fact]
    public void TheNpcapGuidanceTravelsWithItsReferenceInBothPlaces()
    {
        var snapshot = Snapshot(Missing());

        var adapters = CaptureWire.Adapters(snapshot, Array.Empty<CaptureAdapterView>());
        var npcap = CaptureWire.StatusExtras(snapshot)["npcap"]!;

        foreach (var node in new[] { adapters, npcap })
        {
            Assert.Equal("capture.npcap.not_installed", node["install_hint_ref"]!["key"]!.GetValue<string>());
            CollectorTextCatalogue.AssertParallel(node["install_hint"], node["install_hint_ref"]);
        }
    }

    [Fact]
    public void AUsableNpcapHasNeitherAHintNorAReference()
    {
        var snapshot = Snapshot(Healthy());

        foreach (var node in new[]
                 {
                     CaptureWire.Adapters(snapshot, Array.Empty<CaptureAdapterView>()),
                     CaptureWire.StatusExtras(snapshot)["npcap"]!.AsObject(),
                 })
        {
            Assert.True(node.ContainsKey("install_hint_ref"));
            Assert.Null(node["install_hint"]);
            Assert.Null(node["install_hint_ref"]);
        }
    }

    [Theory]
    [InlineData(NpcapStatus.NotInstalled)]
    [InlineData(NpcapStatus.LoadFailed)]
    [InlineData(NpcapStatus.NotWinPcapCompatible)]
    [InlineData(NpcapStatus.AdminOnly)]
    [InlineData(NpcapStatus.Ready)]
    public void EveryNpcapVerdictIsWordedByTheCatalogue(NpcapStatus status)
    {
        var environment = status switch
        {
            NpcapStatus.NotInstalled => new FakeNpcapEnvironment(),
            NpcapStatus.LoadFailed => new FakeNpcapEnvironment().WithKey(NpcapDetector.RegistryKey),
            NpcapStatus.NotWinPcapCompatible => FakeNpcapEnvironment.Healthy()
                .WithValue(NpcapDetector.RegistryKey, NpcapDetector.WinPcapCompatibleValue, 0),
            NpcapStatus.AdminOnly => FakeNpcapEnvironment.Healthy()
                .WithValue(NpcapDetector.RegistryKey, NpcapDetector.AdminOnlyValue, 1),
            _ => FakeNpcapEnvironment.Healthy(),
        };

        var detection = new NpcapDetector(environment).Detect();

        Assert.Equal(status, detection.Status);
        Assert.Equal(detection.Guidance.Chinese, CollectorTextCatalogue.Render(detection.Guidance.ToJson()));
    }

    [Fact]
    public void TheProfileMessageTravelsWithItsReference()
    {
        var profile = new ProfileStatusSnapshot(
            ProfileStatus.UnsupportedBuild, null, Region.Cn, "2026.10.01.0000.0000", null, null,
            CollectorTexts.CaptureProfileUnavailable("no profile matches this region and build"));

        var wire = CaptureWire.Profile(profile);

        Assert.Equal("协议档案不可用，暂时不会自动记录：no profile matches this region and build", wire["message"]!.GetValue<string>());
        Assert.Equal("no profile matches this region and build", wire["message_ref"]!["args"]!["reason"]!.GetValue<string>());
        CollectorTextCatalogue.AssertParallel(wire["message"], wire["message_ref"]);
    }

    [Fact]
    public void AProfileWithoutAMessageHasANullReference()
    {
        var wire = CaptureWire.Profile(new ProfileStatusSnapshot(
            ProfileStatus.Verified, "cn.test", Region.Cn, "2026.10.01.0000.0000", null, null, null));

        Assert.True(wire.ContainsKey("message_ref"));
        Assert.Null(wire["message"]);
        Assert.Null(wire["message_ref"]);
    }

    [Fact]
    public void TheNoProfileDefaultIsACatalogueText()
    {
        var wire = CaptureWire.Profile(NoProfileStatusProvider.Instance.Current);

        Assert.Equal("capture.profile.none", wire["message_ref"]!["key"]!.GetValue<string>());
        CollectorTextCatalogue.AssertParallel(wire["message"], wire["message_ref"]);
    }

    /// <summary>
    /// The adapter-list sentence is also logic: MachinaCaptureSource.Translate recognises it inside
    /// an exception message. The constant it matches on and the catalogue text must stay one sentence.
    /// </summary>
    [Fact]
    public void TheAdapterListChangedRefusalIsTheSentenceTranslateRecognises()
    {
        var refusal = NpcapPacketReader.AdapterListChanged("wifi");

        Assert.Equal(NpcapPacketReader.AdapterListChangedMessage, refusal.Message);
        Assert.Equal("capture.start.adapter_list_changed", refusal.Text!.Key);
        Assert.Same(refusal, MachinaCaptureSource.Translate(refusal, "wifi"));
        Assert.Equal(
            "capture.start.adapter_list_changed",
            MachinaCaptureSource.Translate(new InvalidOperationException(NpcapPacketReader.AdapterListChangedMessage), "wifi")
                .Text!.Key);
    }

    [Fact]
    public void AFaultTheCatalogueWordsReachesAnObserverThatOnlyKnowsStrings()
    {
        var observer = new StringObserver();

        ((ICaptureSourceObserver)observer).OnFault(CollectorTexts.CaptureFaultMonitor(), null);

        Assert.Equal(CollectorTexts.CaptureFaultMonitor().Chinese, Assert.Single(observer.Faults));
    }

    [Fact]
    public void StartupTextLine_IsTheReferenceOnOneCompactUtf8Line()
    {
        var path = "D:\\导随记录\\data\nmentor.db";

        var line = Program.StartupTextLine(CollectorTexts.StartupDbLocked(path));

        Assert.StartsWith(Program.StartupTextPrefix, line, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.Contains("导随记录", line, StringComparison.Ordinal);
        Assert.Equal(
            "MR-TEXT {\"key\":\"startup.db_locked\",\"args\":{\"path\":\"D:\\\\导随记录\\\\data\\nmentor.db\"}}",
            line);
        var reference = JsonNode.Parse(line[Program.StartupTextPrefix.Length..]);
        Assert.Equal(path, reference!["args"]!["path"]!.GetValue<string>());
        Assert.Equal(CollectorTexts.StartupDbLocked(path).Chinese, CollectorTextCatalogue.Render(reference));
    }

    [Fact]
    public void WriteRefusal_PutsTheReferenceRightBeforeTheUnchangedLastLine()
    {
        var text = CollectorTexts.StartupAlreadyRunning();
        var error = new StringWriter();

        Program.WriteRefusal(error, ErrorCodes.AlreadyRunning + ": " + text.Chinese, text);

        var lines = error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal(Program.StartupTextLine(text), lines[0]);
        Assert.Equal("ERR_ALREADY_RUNNING: Collector 已在本机运行。请复用现有实例，而不是再次启动。", lines[1]);
    }

    [Fact]
    public void WriteRefusal_WithoutACatalogueTextWritesOnlyTheLastLine()
    {
        var error = new StringWriter();

        Program.WriteRefusal(error, "failed: 系统原文", null);

        Assert.Equal("failed: 系统原文" + Environment.NewLine, error.ToString());
    }

    private sealed class StringObserver : ICaptureSourceObserver
    {
        public List<string> Faults { get; } = new();

        public void OnMessage(Protocol.Decoded.DecodedMessage message)
        {
        }

        public void OnDecodeError()
        {
        }

        public void OnFault(string reason, Exception? error) => Faults.Add(reason);
    }
}
