using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;

namespace MentorRecorder.Collector.UnitTests;

public sealed class CalibrationZoneBurstTests
{
    // Large messages travel outside loads too, as do a zone marker and a territory message
    // after the duty. Large does not mean loading.
    [Theory]
    [InlineData(0xE001, 2416)]
    [InlineData(0xA100, 3000)]
    public void LargeBackgroundTrafficDoesNotHideACorroboratedExit(int opcode, int length)
    {
        var (snapshot, draft) = Observe(CalibrationObserverTests.Session1()
            .Concat(Background((ushort)opcode, length)));

        Assert.Equal(3, snapshot.Clusters.Count);
        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.True(draft.Progress.DutyExitSeen);
        Assert.Equal((ushort)0xA107, draft.Messages.Single(m => m.Name == "ZONE_INITIALIZATION").Opcode);
        Assert.Equal((ushort)0xA108, draft.Messages.Single(m => m.Name == "ZONE_TERRITORY").Opcode);
        Assert.Equal(3, draft.SampleCounts["messages.ZONE_INITIALIZATION.opcode"]);
        Assert.Single(draft.Events, e => e.Kind == "duty_exit");
    }

    [Theory]
    [InlineData(0xA107)]
    [InlineData(0xA108)]
    public void BothPreviouslyCorroboratedShapesAreRequired(int missingOpcode)
    {
        var traffic = BeforeExit().Concat(Background())
            .Concat(CalibrationObserverTests.Cluster(215_000, 5000)
                .Where(m => m.Opcode != missingOpcode))
            .Concat(CalibrationObserverTests.Noise(220_000, 240_000));
        var (snapshot, draft) = Observe(traffic);

        Assert.Equal(2, snapshot.Clusters.Count);
        Assert.False(draft.Progress.DutyExitSeen);
        Assert.NotEqual(CalibrationDraftStatus.Ready, draft.Status);
    }

    [Fact]
    public void UnrelatedConnectionCannotBorrowTheLearnedShapes()
    {
        var traffic = BeforeExit().Concat(Background(connection: "other"))
            .Concat(CalibrationObserverTests.Cluster(215_000, 5000, connection: "other"))
            .Concat(CalibrationObserverTests.Noise(220_000, 240_000, connection: "other"));
        var (snapshot, draft) = Observe(traffic);

        Assert.Equal(2, snapshot.Clusters.Count);
        Assert.False(draft.Progress.DutyExitSeen);
    }

    [Fact]
    public void ARepeatedSignatureWithoutAKnownDutyCannotBypassQuiet()
    {
        var traffic = CalibrationObserverTests.Cluster(5_000, 5000)
            .Concat(CalibrationObserverTests.Cluster(125_000, 5000))
            .Concat(Background())
            .Concat(CalibrationObserverTests.Cluster(215_000, 5000));

        Assert.Equal(2, Observe(traffic).Snapshot.Clusters.Count);
    }

    [Fact]
    public void ASignatureSeenOnlyOnceCannotBypassQuiet()
    {
        var traffic = CalibrationObserverTests.Cluster(125_000, 1039)
            .Concat(Background())
            .Concat(CalibrationObserverTests.Cluster(215_000, 5000));

        Assert.Single(Observe(traffic).Snapshot.Clusters);
    }

    [Fact]
    public void ShapesThatFrequentlyTravelOutsideLoadsCannotBypassQuiet()
    {
        var strays = Enumerable.Range(0, 6).SelectMany(i => CalibrationObserverTests.Cluster(180_000 + i * 3000, 5000)
            .Where(m => m.Opcode is 0xA107 or 0xA108));
        var traffic = BeforeExit().Concat(strays).Concat(Background())
            .Concat(CalibrationObserverTests.Cluster(215_000, 5000));

        Assert.Equal(2, Observe(traffic).Snapshot.Clusters.Count);
    }

    [Fact]
    public void AnEarlierMissedBurstDoesNotPoisonTheNextExitOrTerritory()
    {
        var strays = CalibrationObserverTests.Cluster(180_000, 5000)
            .Where(m => m.Opcode is 0xA107 or 0xA108);
        var traffic = CalibrationObserverTests.Session1().Concat(strays).Concat(Background());
        var (snapshot, draft) = Observe(traffic);

        Assert.Equal(3, snapshot.Clusters.Count);
        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.True(draft.Progress.DutyExitSeen);
        Assert.Contains(draft.Messages, m => m.Name == "ZONE_TERRITORY" && m.Opcode == 0xA108);
        Assert.True(CalibrationEvidenceSummary.From(snapshot, CalibrationObserverTests.Template()).TerritoryCandidates > 0);
    }

    [Fact]
    public void FrequentTerritoryMessagesAreNotWrittenToTheProfile()
    {
        var strays = Enumerable.Range(0, 6).SelectMany(i => CalibrationObserverTests.Cluster(180_000 + i * 3000, 5000)
            .Where(m => m.Opcode == 0xA108));
        var (_, draft) = Observe(CalibrationObserverTests.Session1().Concat(strays));

        Assert.DoesNotContain(draft.Messages, m => m.Name == "ZONE_TERRITORY");
    }

    [Fact]
    public void TerritoryMustBePresentInTheSelectedExit()
    {
        var traffic = CalibrationObserverTests.Session1()
            .Where(m => !(m.Mono.TotalMilliseconds >= 215_000 && m.Opcode == 0xA108))
            .Concat(CalibrationObserverTests.Cluster(300_000, 5000));
        var (_, draft) = Observe(traffic);

        Assert.True(draft.Progress.DutyExitSeen);
        Assert.DoesNotContain(draft.Messages, m => m.Name == "ZONE_TERRITORY");
    }

    [Fact]
    public void AnAmbiguousMarkerCannotBypassQuiet()
    {
        var decoys = new long[] { 5_420, 125_420, 215_420 }.Select(t =>
            CalibrationObserverTests.Message(MessageDirection.Inbound, 0xE002, new byte[456], t));
        var traffic = CalibrationObserverTests.Session1().Concat(Background()).Concat(decoys);

        Assert.Equal(2, Observe(traffic).Snapshot.Clusters.Count);
    }

    [Fact]
    public void TheSignatureAloneDoesNotReplaceTheLoadBurst()
    {
        var traffic = BeforeExit().Concat(Background())
            .Concat(CalibrationObserverTests.Cluster(215_000, 5000)
                .Where(m => m.Opcode is 0xA107 or 0xA108));

        Assert.Equal(2, Observe(traffic).Snapshot.Clusters.Count);
    }

    [Theory]
    [InlineData(CalibrationMatchSource.ReplyState)]
    [InlineData(CalibrationMatchSource.QueueRequest)]
    [InlineData(CalibrationMatchSource.Announcement)]
    [InlineData(CalibrationMatchSource.MarkerOffset)]
    public void ACompletedLaterDutyReplacesAnOldEntryWhoseExitWasMissed(CalibrationMatchSource source)
    {
        var later = CalibrationObserverTests.Session1().Select(message =>
        {
            var payload = message.Payload.ToArray();
            if (message.Opcode == 0xC001) payload[0] = 2;
            if (message.Opcode == 0xC002) payload[16] = 2;
            return message with
            {
                Payload = payload,
                ConnectionKey = "after-relogin",
                Mono = message.Mono + TimeSpan.FromMinutes(10),
                ObservedAtUtc = message.ObservedAtUtc + TimeSpan.FromMinutes(10),
            };
        });
        var traffic = BeforeExit().Concat(later).SelectMany(message =>
        {
            if (message.Opcode != 0xC002 || message.Payload.Span[9] != 3 || source == CalibrationMatchSource.ReplyState)
                return new[] { message };
            if (source == CalibrationMatchSource.QueueRequest)
                return Array.Empty<DecodedMessage>();
            var offset = source == CalibrationMatchSource.MarkerOffset ? 37 : 16;
            return new[] { message with { Opcode = 0xF00D,
                Payload = CalibrationObserverTests.Bytes(64, (offset, message.Payload.Span[16])) } };
        });
        var (_, draft) = Observe(traffic);

        Assert.Equal(source, draft.MatchSource);
        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.True(draft.Progress.DutyExitSeen);
        Assert.Equal(725_100, Assert.Single(draft.Events, e => e.Kind == "duty_enter").TMs);
        Assert.Equal(815_100, Assert.Single(draft.Events, e => e.Kind == "duty_exit").TMs);
    }

    /// <summary>
    /// Re-review 2026-10-20, RV1-1. Bursts are a window of the newest 64 (C-4), while sightings outside a burst are counted
    /// for the evidence's whole life. Weighed against the window alone, the true marker - filed outside a burst on one slow
    /// load in ten - stopped qualifying for good at about two hundred zone loads: the draft could only say 进出副本的时间还在
    /// 检查中, and the repeated signature no longer opened an exit that background traffic hides. The loads let go still
    /// count as marked, and that count travels with the evidence across restarts.
    /// </summary>
    [Fact]
    public void TheTrueMarkerKeepsQualifyingAfterTwoHundredLoadsWithOneInTenFiledOutside()
    {
        const string build = "2026.09.01.0000.0000";
        const string templateSha = "template-sha";
        var template = CalibrationObserverTests.Template();
        var zone = new MessageKey(PacketDirection.ServerToClient, 0xA107, 456);
        var root = Path.Combine(Path.GetTempPath(), "MentorRecorder.Rv11." + Guid.NewGuid().ToString("N"));
        try
        {
            CalibrationSnapshot? carried = null;
            for (var session = 0; session < 10; session++)
            {
                var id = "session-" + session;
                var observer = new CalibrationObserver(template, Region.Cn, id);
                if (carried is not null)
                {
                    observer.AdoptEvidence(carried);
                }

                var shift = TimeSpan.FromHours(session * 2);
                foreach (var message in LoadsWithOneInTenMarkersOutside(session * 20).OrderBy(m => m.Mono))
                {
                    observer.Accept(message with { CaptureSessionId = id, ObservedAtUtc = message.ObservedAtUtc + shift });
                }

                observer.Flush();
                Assert.True(CalibrationEvidenceStore.Save(root, Region.Cn, build, templateSha, observer.Snapshot()));
                carried = CalibrationEvidenceStore.Load(root, Region.Cn, build, templateSha);
                Assert.NotNull(carried);
            }

            var snapshot = carried!;
            Assert.Equal(0, snapshot.OverflowCount);
            Assert.Equal(CalibrationObserver.MaxClusters, snapshot.Clusters.Count);
            Assert.Equal(20, CalibrationDraft.Outside(snapshot, zone));
            Assert.True(CalibrationDraft.MarksEnough(snapshot, zone));
            Assert.Equal(1, CalibrationEvidenceSummary.From(snapshot, template).ZoneCandidates);

            // One more restart, then a duty whose exit background traffic hides: the signature still opens it.
            var next = new CalibrationObserver(template, Region.Cn, "session-10");
            next.AdoptEvidence(snapshot);
            foreach (var message in BeforeExit().Concat(Background()).Concat(CalibrationObserverTests.Cluster(215_000, 5000))
                         .OrderBy(m => m.Mono))
            {
                next.Accept(message with
                {
                    CaptureSessionId = "session-10", ObservedAtUtc = message.ObservedAtUtc + TimeSpan.FromHours(20),
                });
            }

            next.Flush();
            var played = next.Snapshot();
            Assert.Equal(3, played.Clusters.Count(cluster =>
                played.ConnectionSessions.TryGetValue(cluster.ConnectionTag, out var owner) && owner == "session-10"));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Debris in the temp folder is not worth failing a test over.
            }
        }
    }

    /// <summary>Twenty town loads; on every tenth load overall the marker arrives early and is filed outside the burst.</summary>
    private static IEnumerable<DecodedMessage> LoadsWithOneInTenMarkersOutside(int firstLoad) =>
        Enumerable.Range(0, 20).SelectMany(index =>
        {
            var t = 5_000 + (index * 30_000L);
            return (firstLoad + index) % 10 == 9
                ? CalibrationObserverTests.Cluster(t, 5000, includeZoneInit: false).Prepend(
                    CalibrationObserverTests.Message(MessageDirection.Inbound, 0xA107, new byte[456], t - 10_000))
                : CalibrationObserverTests.Cluster(t, 5000);
        });

    private static IEnumerable<DecodedMessage> BeforeExit() =>
        CalibrationObserverTests.Session1().Where(m => m.Mono.TotalMilliseconds < 215_000);

    private static IEnumerable<DecodedMessage> Background(
        ushort opcode = 0xE001, int length = 2416, string connection = "zone") =>
        Enumerable.Range(0, 60).Select(i => CalibrationObserverTests.Message(
            MessageDirection.Inbound, opcode, new byte[length], 200_000 + i * 500, connection));

    private static (CalibrationSnapshot Snapshot, CalibrationDraft Draft) Observe(IEnumerable<DecodedMessage> traffic)
    {
        var template = CalibrationObserverTests.Template();
        var observer = new CalibrationObserver(template, Region.Cn, "calibration-session");
        foreach (var message in traffic.OrderBy(m => m.Mono))
        {
            observer.Accept(message);
        }

        observer.Flush();
        var snapshot = observer.Snapshot();
        return (snapshot, CalibrationDraft.Derive(snapshot, template));
    }
}
