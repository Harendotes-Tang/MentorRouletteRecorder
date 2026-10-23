using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Domain.StateMachine;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Parsing;
using MentorRecorder.Collector.Protocol.Pipeline;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Domain.Events;

namespace MentorRecorder.Collector.UnitTests;

public sealed class CandidateObserverTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 5, 1, 0, 0, TimeSpan.Zero);

    internal static ProtocolProfile Profile() => new(
        "candidate-test", Region.Cn, "test-build", Start, null, ProfileCompatibilityStatus.Candidate,
        TimeSpan.FromSeconds(45), [], [], "synthetic test only", new string('0', 64), "", false)
    {
        Hypotheses = Enumerable.Range(1, 7).Select(i => new ProfileHypothesis(
            "ZONE_MEMBER_" + i, (ushort)(0xf100 + i), PacketDirection.ServerToClient,
            8, null, null, "synthetic", "zone_load")).ToArray(),
    };

    [Fact]
    public void PipelineDescribesCandidateHypothesesWithoutEnablingValidation()
    {
        using var db = new TestDatabase();
        var candidate = Profile();
        var selections = 0;
        var normal = new ProfileSelection(ProfileCompatibilityStatus.Unsupported, ProfileBinding.FailClosed,
            null, Region.Cn, "test-build", "test");
        var pipeline = new LiveProtocolPipeline(db.Database, db.Clock, new LiveEventBus(db.Clock),
            _ => normal, _ => { selections++; return candidate; });
        pipeline.Refresh(GameProcessDetection.NotRunning with { Region = Region.Cn, GameBuild = "test-build" });

        Assert.False(pipeline.CandidateValidationEnabled);
        var views = pipeline.DescribeCandidateHypotheses();

        Assert.Equal(7, views.Count);
        Assert.Equal("0xf101", views[0].Opcode);
        Assert.Equal("ZONE_MEMBER_1", views[0].Label);
        Assert.All(views, view => Assert.True(view.ResearchEligible));
        // Cached per client build: a second read does not call the selector again, and
        // describing never enables validation or creates an observer.
        Assert.Same(views, pipeline.DescribeCandidateHypotheses());
        Assert.Equal(1, selections);
        Assert.False(pipeline.CandidateValidationEnabled);
        Assert.Null(pipeline.CandidateProfileId);
    }

    [Fact]
    public void PipelineListsTheOnlyCandidateWhileTheGameIsNotRunning()
    {
        using var db = new TestDatabase();
        var candidate = Profile();
        var normal = new ProfileSelection(ProfileCompatibilityStatus.Unsupported, ProfileBinding.FailClosed,
            null, Region.Unknown, null, "test");
        var pipeline = new LiveProtocolPipeline(db.Database, db.Clock, new LiveEventBus(db.Clock),
            _ => normal, _ => throw new InvalidOperationException("selector must not run without a build"))
        {
            ListCandidateProfiles = () => new[] { candidate },
        };

        // No game, no build: the whitelist can still be labelled from the single candidate.
        var views = pipeline.DescribeCandidateHypotheses();
        Assert.Equal(7, views.Count);
        Assert.Equal("candidate-test", views[0].ProfileId);

        // Two candidates are ambiguous, so no labelling is produced.
        var ambiguous = new LiveProtocolPipeline(db.Database, db.Clock, new LiveEventBus(db.Clock),
            _ => normal, _ => null)
        {
            ListCandidateProfiles = () => new[] { candidate, candidate with { ProfileId = "other" } },
        };
        Assert.Empty(ambiguous.DescribeCandidateHypotheses());
    }

    internal static DecodedMessage Message(int member = 1, long t = 0, string session = "session", string connection = "private-connection") =>
        new(session, MessageDirection.Inbound, Start.AddMilliseconds(t), TimeSpan.FromMilliseconds(t),
            0, 3, (ushort)(0xf100 + member), new byte[8], connection);

    /// <summary>A profile with the queue registration hypothesis next to the zone members.</summary>
    internal static ProtocolProfile QueueProfile() => Profile() with
    {
        Hypotheses = Profile().Hypotheses.Append(new ProfileHypothesis(
            CandidateObserver.QueueWindowTrigger, 0x03bb, PacketDirection.ClientToServer,
            128, null, null, "synthetic", "queue")).Append(new ProfileHypothesis(
            "FINDER_STATE_NOTIFICATION", 0x0323, PacketDirection.ServerToClient,
            40, null, null, "synthetic", CandidateObserver.FinderGroup)).ToArray(),
    };

    private static DecodedMessage Raw(ushort opcode, int length, long t, MessageDirection direction = MessageDirection.Inbound,
        string connection = "private-connection", byte fill = 0) =>
        new("session", direction, Start.AddMilliseconds(t), TimeSpan.FromMilliseconds(t),
            0, 3, opcode, Enumerable.Repeat(fill, length).ToArray(), connection);

    [Fact]
    public void QueueWindowSamplesEveryOpcodeBetweenRegistrationAndTheNextZoneLoad()
    {
        var observations = new List<CandidateObservation>();
        var observer = new CandidateObserver(QueueProfile(), "session", observations.Add);

        observer.Accept(Raw(0x0aaa, 16, 100));                          // before the window: ignored
        observer.Accept(Raw(0x03bb, 128, 1000, MessageDirection.Outbound)); // registration opens it
        observer.Accept(Raw(0x0aaa, 16, 1500));
        observer.Accept(Raw(0x0aaa, 16, 1600, fill: 7));                // same key, second occurrence
        observer.Accept(Raw(0x0bbb, 300, 1700));                        // the unnamed pop candidate
        observer.Accept(Raw(0x0aaa, 24, 1800));                         // different length = different key
        for (var member = 1; member <= 5; member++) observer.Accept(Message(member, 2000 + member * 100)); // duty entry burst

        var samples = observations.Where(o => o.HypothesisName == CandidateObserver.QueueWindowSampleName).ToList();
        // aaa/16, bbb/300, aaa/24 + the five zone members themselves. The registration that
        // opened the window is its own observation already and is not sampled.
        Assert.Equal(3 + 5, samples.Count);
        Assert.DoesNotContain(samples, s => s.Opcode == 0x03bb);
        Assert.All(samples, s =>
        {
            Assert.Equal(CandidateObserver.QueueWindowGroup, s.Group);
            Assert.Null(s.PayloadHex);
            Assert.Equal(s.TMs, s.FirstTMs);
            Assert.Matches("^[0-9a-f]{12}$", s.PayloadHash12);
        });
        var aaa = samples.Single(s => s.Opcode == 0x0aaa && s.Length == 16);
        Assert.Equal(2, aaa.Occurrences);
        Assert.Equal(1500, aaa.TMs);                                    // first occurrence, first digest
        Assert.Equal(1600, aaa.LastTMs);                                // last occurrence, for "seen last before exit"
        Assert.Equal("S2C", aaa.Direction);
        Assert.Equal(1, samples.Single(s => s.Opcode == 0x0bbb).Occurrences);
        // Flushed once the burst closed the window; the anchor itself was emitted before.
        Assert.True(observations.FindIndex(o => o.HypothesisName == "ZONE_LOAD") <
                    observations.FindIndex(o => o.HypothesisName == CandidateObserver.QueueWindowSampleName));

        // Closed: later traffic is not sampled, and Flush has nothing left.
        observer.Accept(Raw(0x0ccc, 8, 5000));
        observer.Flush();
        Assert.DoesNotContain(observations, o => o.Opcode == 0x0ccc);
    }

    [Fact]
    public void DutyWindowSamplesFromTheEntryBurstAfterAFinderUpdateUntilTheNextZoneLoad()
    {
        var observations = new List<CandidateObservation>();
        var observer = new CandidateObserver(QueueProfile(), "session", observations.Add);

        // A teleport with no finder update in the last three minutes opens nothing.
        for (var member = 1; member <= 5; member++) observer.Accept(Message(member, 1000 + member * 10));
        observer.Accept(Raw(0x0aaa, 16, 2000));
        Assert.DoesNotContain(observations, o => o.HypothesisName == CandidateObserver.DutyWindowSampleName);

        // Pop (finder group), then the entry burst within three minutes: the window opens.
        observer.Accept(Raw(0x0323, 40, 10_000));
        for (var member = 1; member <= 5; member++) observer.Accept(Message(member, 20_000 + member * 10));
        observer.Accept(Raw(0x0aaa, 16, 30_000));
        observer.Accept(Raw(0x0aaa, 16, 40_000, fill: 9));
        observer.Accept(Raw(0x0bbb, 48, 1_700_000));                    // the unnamed result candidate, once, last
        observer.Accept(Message(1, 1_800_000));                          // zone member outside a burst: not sampled
        Assert.DoesNotContain(observations, o => o.HypothesisName == CandidateObserver.DutyWindowSampleName);

        // The exit burst closes the window and writes it out; its members are not samples.
        for (var member = 1; member <= 5; member++) observer.Accept(Message(member, 1_900_000 + member * 10));
        var samples = observations.Where(o => o.HypothesisName == CandidateObserver.DutyWindowSampleName).ToList();
        Assert.Equal(new ushort[] { 0x0aaa, 0x0bbb }, samples.Select(s => (ushort)s.Opcode!).ToArray());
        Assert.All(samples, s =>
        {
            Assert.Equal(CandidateObserver.DutyWindowGroup, s.Group);
            Assert.Null(s.PayloadHex);
        });
        var aaa = samples.Single(s => s.Opcode == 0x0aaa);
        Assert.Equal(2, aaa.Occurrences);
        Assert.Equal(30_000, aaa.FirstTMs);
        Assert.Equal(40_000, aaa.LastTMs);
        var bbb = samples.Single(s => s.Opcode == 0x0bbb);
        Assert.Equal(1, bbb.Occurrences);
        Assert.Equal(1_700_000, bbb.LastTMs);

        // The exit burst had no recent finder update, so no new window: later traffic is dropped.
        observer.Accept(Raw(0x0ccc, 8, 1_950_000));
        observer.Flush();
        Assert.DoesNotContain(observations, o => o.Opcode == 0x0ccc);
    }

    [Fact]
    public void QueueWindowFlushesOnTimeoutOnSessionEndAndNeverCrossesConnections()
    {
        var observations = new List<CandidateObservation>();
        var observer = new CandidateObserver(QueueProfile(), "session", observations.Add);
        observer.Accept(Raw(0x03bb, 128, 1000, MessageDirection.Outbound));
        observer.Accept(Raw(0x0aaa, 16, 1500));
        observer.Accept(Raw(0x0aaa, 16, 1600, connection: "other-connection")); // no window there
        Assert.DoesNotContain(observations, o => o.HypothesisName == CandidateObserver.QueueWindowSampleName);

        // Past the maximum window: flushed, and the late message itself is not part of it.
        observer.Accept(Raw(0x0ddd, 8, 1000 + CandidateObserver.QueueWindowMaxMs + 1));
        var samples = observations.Where(o => o.HypothesisName == CandidateObserver.QueueWindowSampleName).ToList();
        Assert.Equal(new ushort[] { 0x0aaa }, samples.Select(s => (ushort)s.Opcode!).ToArray());
        Assert.All(samples, s => Assert.Matches("^[0-9a-f]{12}$", s.ConnectionTag));

        // A fresh registration, then the session ends: Flush emits what it had.
        observer.Accept(Raw(0x03bb, 128, 2_000_000, MessageDirection.Outbound));
        observer.Accept(Raw(0x0eee, 8, 2_000_100));
        observer.Flush();
        Assert.Contains(observations, o => o.Opcode == 0x0eee && o.HypothesisName == CandidateObserver.QueueWindowSampleName);
    }

    [Fact]
    public void QueueWindowWritesASegmentAtEveryFinderMessageAndKeepsSampling()
    {
        var observations = new List<CandidateObservation>();
        var observer = new CandidateObserver(QueueProfile(), "session", observations.Add);
        observer.Accept(Raw(0x03bb, 128, 1000, MessageDirection.Outbound));
        observer.Accept(Raw(0x0aaa, 16, 2000));
        observer.Accept(Raw(0x0bbb, 300, 30 * 60 * 1000));            // the pop, half an hour later
        observer.Accept(Raw(0x0323, 40, 30 * 60 * 1000 + 500));        // finder state update: segment ends

        var first = observations.Where(o => o.HypothesisName == CandidateObserver.QueueWindowSampleName).ToList();
        Assert.Equal(new ushort[] { 0x0aaa, 0x0bbb, 0x0323 }, first.Select(s => (ushort)s.Opcode!).ToArray());
        // The state update itself is the last row of the segment it closes.
        Assert.True(observations.FindIndex(o => o.HypothesisName == "FINDER_STATE_NOTIFICATION") <
                    observations.FindIndex(o => o.HypothesisName == CandidateObserver.QueueWindowSampleName));

        // Still open: the next segment starts empty and is closed by the duty entry burst.
        observer.Accept(Raw(0x0aaa, 16, 31 * 60 * 1000));
        for (var member = 1; member <= 5; member++) observer.Accept(Message(member, 32 * 60 * 1000 + member * 100));
        var second = observations.Where(o => o.HypothesisName == CandidateObserver.QueueWindowSampleName).Skip(first.Count).ToList();
        Assert.Equal(0x0aaa, second[0].Opcode);
        Assert.Equal(1, second[0].Occurrences);
        Assert.Equal(31 * 60 * 1000, second[0].TMs);
        Assert.Equal(1 + 5, second.Count);
        observer.Flush();
        Assert.Equal(first.Count + second.Count, observations.Count(o => o.HypothesisName == CandidateObserver.QueueWindowSampleName));
    }

    [Fact]
    public void ZoneMembersAreRecordedOnlyAsPartOfABurst()
    {
        var rows = new List<CandidateObservation>();
        var observer = new CandidateObserver(Profile(), "session", rows.Add);
        // The movement packet alone, a thousand times: nothing at all reaches the ledger.
        for (var i = 0; i < 1000; i++) observer.Accept(Message(1, i * 20));
        Assert.Empty(rows);

        // A real burst: its five members and the anchor, members first.
        for (var member = 1; member <= 5; member++) observer.Accept(Message(member, 100_000 + member * 100));
        Assert.Equal(6, rows.Count);
        Assert.Equal("ZONE_LOAD", rows[5].HypothesisName);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, rows.Take(5).Select(r => r.Opcode!.Value - 0xf100).ToArray());

        // Inside the burst the movement packet keeps coming: recorded once, not per packet.
        for (var i = 0; i < 100; i++) observer.Accept(Message(1, 100_600 + i * 10));
        observer.Accept(Message(6, 101_700));
        Assert.Equal(7, rows.Count);
        Assert.Equal(0xf106, rows[6].Opcode);
    }

    [Fact]
    public void QueueWindowCapsDistinctKeys()
    {
        var observations = new List<CandidateObservation>();
        var observer = new CandidateObserver(QueueProfile(), "session", observations.Add);
        observer.Accept(Raw(0x03bb, 128, 1000, MessageDirection.Outbound));
        for (var i = 0; i < CandidateObserver.MaxQueueWindowKeys + 50; i++)
            observer.Accept(Raw((ushort)(0x1000 + i), 8, 1001 + i));
        observer.Flush();
        Assert.Equal(CandidateObserver.MaxQueueWindowKeys,
            observations.Count(o => o.HypothesisName == CandidateObserver.QueueWindowSampleName));
    }

    /// <summary>
    /// Review finding L-12. Every queue-window sample must carry the dropped-key count.
    /// Without it a window that hit the cap is indistinguishable from a quiet one, and the
    /// reader cannot tell "this opcode never appeared" from "sampling stopped".
    /// </summary>
    [Fact]
    public void QueueWindowSamplesCarryHowManyKeysWereDropped()
    {
        var observations = new List<CandidateObservation>();
        var observer = new CandidateObserver(QueueProfile(), "session", observations.Add);
        observer.Accept(Raw(0x03bb, 128, 1000, MessageDirection.Outbound));
        for (var i = 0; i < CandidateObserver.MaxQueueWindowKeys + 50; i++)
            observer.Accept(Raw((ushort)(0x1000 + i), 8, 1001 + i));
        observer.Flush();

        var samples = observations
            .Where(o => o.HypothesisName == CandidateObserver.QueueWindowSampleName)
            .ToList();

        Assert.Equal(CandidateObserver.MaxQueueWindowKeys, samples.Count);

        // Every row of the window carries the same count: the omission is a property of the
        // window, not of any one key.
        Assert.All(samples, sample => Assert.Equal(50, sample.OverflowCount));
    }

    /// <summary>A window that never hit the cap reports a zero count.</summary>
    [Fact]
    public void QueueWindowSamplesReportZeroWhenNothingWasDropped()
    {
        var observations = new List<CandidateObservation>();
        var observer = new CandidateObserver(QueueProfile(), "session", observations.Add);
        observer.Accept(Raw(0x03bb, 128, 1000, MessageDirection.Outbound));
        observer.Accept(Raw(0x1000, 8, 1100));
        observer.Flush();

        Assert.All(
            observations.Where(o => o.HypothesisName == CandidateObserver.QueueWindowSampleName),
            sample => Assert.Equal(0, sample.OverflowCount));
    }

    [Fact]
    public void FiveDistinctMembersWithinThreeSeconds_EmitOneAnchorWithWindow()
    {
        var observations = new List<CandidateObservation>();
        var observer = new CandidateObserver(Profile(), "session", observations.Add);
        for (var i = 1; i <= 7; i++) observer.Accept(Message(i, i * 400));
        var anchor = Assert.Single(observations, o => o.HypothesisName == "ZONE_LOAD");
        Assert.Equal(400, anchor.FirstTMs);
        Assert.Equal(2000, anchor.LastTMs);
        Assert.Equal(Start.AddMilliseconds(400), anchor.FirstObservedAtUtc);
        Assert.Equal(Start.AddMilliseconds(2000), anchor.LastObservedAtUtc);
        Assert.Null(anchor.Opcode);
        Assert.Equal(8, observations.Count);
        Assert.All(observations, o => Assert.Matches("^[0-9a-f]{12}$", o.ConnectionTag));
    }

    [Fact]
    public void RepeatedMemberDoesNotMeetThreshold_AndQuietGapStartsNewBurst()
    {
        var rows = new List<CandidateObservation>();
        var observer = new CandidateObserver(Profile(), "session", rows.Add);
        for (var i = 0; i < 10; i++) observer.Accept(Message(1, i));
        Assert.DoesNotContain(rows, o => o.HypothesisName == "ZONE_LOAD");
        for (var burst = 0; burst < 2; burst++)
            for (var member = 1; member <= 5; member++) observer.Accept(Message(member, 4000 + burst * 4000 + member));
        Assert.Equal(2, rows.Count(o => o.HypothesisName == "ZONE_LOAD"));
    }

    [Fact]
    public void SparseRepeatedMemberBetweenClustersDoesNotSuppressNextAnchor()
    {
        var rows = new List<CandidateObservation>();
        var observer = new CandidateObserver(Profile(), "session", rows.Add);
        for (var member = 1; member <= 5; member++) observer.Accept(Message(member, member));
        for (var t = 1000; t <= 10000; t += 1000) observer.Accept(Message(1, t));
        for (var member = 2; member <= 5; member++) observer.Accept(Message(member, 10000 + member));
        Assert.Equal(2, rows.Count(o => o.HypothesisName == "ZONE_LOAD"));
    }

    [Fact]
    public void ExpiredMembersAndSeparateConnectionsNeverCombine()
    {
        var rows = new List<CandidateObservation>();
        var observer = new CandidateObserver(Profile(), "session", rows.Add);
        for (var member = 1; member <= 5; member++) observer.Accept(Message(member, member * 1000));
        observer.Accept(Message(5, 5001, connection: "different"));
        observer.Accept(Message(1, 5002, session: "different"));
        Assert.DoesNotContain(rows, o => o.HypothesisName == "ZONE_LOAD");
    }

    [Fact]
    public void WrongDirectionLengthOpcodeAndObfuscatedAreIgnored()
    {
        var rows = new List<CandidateObservation>();
        var observer = new CandidateObserver(Profile(), "session", rows.Add);
        observer.Accept(Message() with { Direction = MessageDirection.Outbound });
        observer.Accept(Message() with { Payload = new byte[7] });
        observer.Accept(Message() with { Opcode = 0xffff });
        Assert.Empty(rows);
        var blocked = Profile() with { ObfuscatedOpcodes = [0xf101] };
        // Obfuscated is a constructor-derived value, so construct the changed profile directly.
        blocked = new ProtocolProfile(blocked.ProfileId, blocked.Region, blocked.GameBuild, Start, null,
            blocked.Status, blocked.MatchWindow, [], [], "test", new string('0', 64), "", false, [0xf101])
            { Hypotheses = blocked.Hypotheses };
        new CandidateObserver(blocked, "session", rows.Add).Accept(Message());
        Assert.Empty(rows);
    }

    [Fact]
    public void EnabledPipelineLeavesStateAndEveryFormalLedgerUntouched_AndDisableIsImmediate()
    {
        using var db = new TestDatabase();
        var candidate = Profile();
        var normal = new ProfileSelection(ProfileCompatibilityStatus.Unsupported, ProfileBinding.FailClosed,
            null, Region.Cn, "test-build", "test");
        var pipeline = new LiveProtocolPipeline(db.Database, db.Clock, new LiveEventBus(db.Clock),
            _ => normal, _ => candidate);
        var rows = new List<CandidateObservation>();
        pipeline.CandidateObserved += rows.Add;
        pipeline.ApplyCandidateSettings(true);
        pipeline.Refresh(GameProcessDetection.NotRunning with { Region = Region.Cn, GameBuild = "test-build" });
        Assert.Equal(ProfileStatus.UnsupportedBuild, pipeline.Current.Status);
        pipeline.OnCaptureStarted("session");
        for (var member = 1; member <= 5; member++) pipeline.Accept(Message(member, member));
        Assert.Equal(6, rows.Count);
        Assert.Equal(RunState.Idle, pipeline.RunState);
        Assert.Null(pipeline.GetCurrentRun().Run);
        foreach (var table in new[] { "mentor_runs", "run_events", "run_revisions" })
        {
            using var command = db.Database.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM " + table;
            Assert.Equal(0L, command.ExecuteScalar());
        }
        pipeline.ApplyCandidateSettings(false);
        pipeline.Accept(Message(6, 8));
        Assert.Equal(6, rows.Count);
        Assert.Null(pipeline.CandidateProfileId);
    }

    [Fact]
    public void DefaultPipelineDoesNotObserve_AndCannotSwitchOrdinaryActiveSession()
    {
        using var db = new TestDatabase();
        var pipeline = new LiveProtocolPipeline(db.Database, db.Clock, new LiveEventBus(db.Clock),
            _ => new ProfileSelection(ProfileCompatibilityStatus.Unsupported, ProfileBinding.FailClosed,
                null, Region.Cn, "test-build", "test"), _ => Profile());
        var rows = new List<CandidateObservation>();
        pipeline.CandidateObserved += rows.Add;
        pipeline.OnCaptureStarted("session");
        pipeline.Accept(Message());
        Assert.Empty(rows);
        Assert.Throws<CollectorException>(() => pipeline.ApplyCandidateSettings(true));
    }
}
