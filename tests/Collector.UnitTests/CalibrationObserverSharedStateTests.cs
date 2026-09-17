using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// What the shared-calibration verifier needs from the observer on top of what a draft needs:
/// territory readings per opcode, which bursts are on the lobby connection, per-session
/// capture health, and bounded counts for declared candidates, with persistence that still
/// reads every evidence file written before those fields existed.
/// </summary>
public sealed class CalibrationObserverSharedStateTests : IDisposable
{
    private const string Session = "calibration-session";
    private const string Build = CalibrationTrafficCases.Build;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "MentorRecorder.Tests", "shared-state-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Also covers a run in which no test created the directory.
        }
    }

    internal static CalibratedValues TrueValues => new(
        new CalibratedPop(CalibrationMatchSource.ReplyState, CalibrationTrafficCases.Reply,
            Selectors: new[] { new CalibrationSelectorReading("finder_state", 3) }),
        CalibrationTrafficCases.ZoneInit, CalibrationTrafficCases.Territory, CalibrationTrafficCases.Job);

    internal static DeclaredCandidate Candidate(string id, CalibratedValues values) =>
        DeclaredCandidate.From(id, CalibratedShape.Messages(CalibrationObserverTests.Template(), values).Messages)!;

    private static CalibrationObserver Observer() =>
        new(CalibrationObserverTests.Template(), Region.Cn, Session);

    private static void Feed(CalibrationObserver observer, IEnumerable<DecodedMessage> traffic)
    {
        foreach (var message in traffic.OrderBy(message => message.Mono))
        {
            observer.Accept(message);
        }
    }

    private static CalibrationSnapshot Observe(IEnumerable<DecodedMessage> traffic)
    {
        var observer = Observer();
        Feed(observer, traffic);
        observer.Flush();
        return observer.Snapshot();
    }

    private static DecodedMessage ReplyPop(long at, byte roulette = 1, byte state = 3, int length = 40) =>
        CalibrationObserverTests.Message(
            MessageDirection.Inbound, CalibrationTrafficCases.Reply,
            CalibrationObserverTests.Bytes(length, (9, state), (16, roulette)), at);

    // ------------------------------------------------------------------ territory readings

    [Fact]
    public void TerritoryReadingsAreKeptPerOpcodeInEveryBurstNotOnlyForKnownDuties()
    {
        var snapshot = Observe(CalibrationObserverTests.Session1());

        var login = snapshot.Clusters[0];
        Assert.Empty(login.TerritoryHits);
        var town = Assert.Single(login.TerritoryReadings);
        Assert.Equal(new TerritoryReading(CalibrationTrafficCases.Territory, Valid: 1, Invalid: 0, KnownDuty: 0), town);
        var duty = Assert.Single(snapshot.Clusters[1].TerritoryReadings);
        Assert.Equal(new TerritoryReading(CalibrationTrafficCases.Territory, Valid: 1, Invalid: 0, KnownDuty: 1), duty);
    }

    [Fact]
    public void ATerritoryReadingOutsideTheTemplatesConstraintsIsInvalid()
    {
        var snapshot = Observe(CalibrationObserverTests.Cluster(5_000, territory: 0));

        Assert.Equal(new TerritoryReading(CalibrationTrafficCases.Territory, Valid: 0, Invalid: 1, KnownDuty: 0),
            Assert.Single(Assert.Single(snapshot.Clusters).TerritoryReadings));
    }

    [Fact]
    public void SeveralTerritoryShapedMessagesInOneBurstAreReadSeparately()
    {
        var snapshot = Observe(CalibrationTrafficCases.Traffic(CalibrationTrafficCases.ReplyStateMinimal));

        var duty = snapshot.Clusters[1];
        Assert.Equal(
            new[] { CalibrationTrafficCases.Territory, CalibrationTrafficCases.DecoyTerritory },
            duty.TerritoryReadings.Select(reading => reading.Opcode).OrderBy(opcode => opcode));
        Assert.All(duty.TerritoryReadings, reading => Assert.Equal(1, reading.KnownDuty));
    }

    // ------------------------------------------------------------------ lobby bursts

    [Fact]
    public void ABurstOnAConnectionThatNeverShowedItselfToBeTheGameIsALobbyBurst()
    {
        var snapshot = Observe(CalibrationObserverTests.Cluster(1_000, 5000, connection: "lobby")
            .Concat(CalibrationObserverTests.Session1()));

        var lobby = Assert.Single(snapshot.Clusters, cluster => cluster.Lobby == true);
        Assert.Equal(1_100, lobby.LoadStartTMs);
        Assert.Equal(3, snapshot.Clusters.Count(cluster => cluster.Lobby == false));
    }

    [Fact]
    public void AQueueRequestLaterOnTheSameConnectionProvesItsFirstBurstIsNotTheLobby()
    {
        var observer = Observer();
        Feed(observer, CalibrationObserverTests.Cluster(5_000, 5000));
        observer.Flush();
        Assert.True(Assert.Single(observer.Snapshot().Clusters).Lobby);

        Feed(observer, CalibrationObserverTests.QueueAndPop(60_000, 1, 120_000));

        Assert.False(Assert.Single(observer.Snapshot().Clusters).Lobby);
    }

    [Fact]
    public void ABurstThatNamesAKnownDutyIsNeverTheLobby()
    {
        Assert.False(Assert.Single(Observe(CalibrationObserverTests.Cluster(5_000, 1039)).Clusters).Lobby);
    }

    // ------------------------------------------------------------------ capture health

    [Theory]
    [InlineData(CaptureSilentReason.None, 0, 0L, true)]
    [InlineData(CaptureSilentReason.Midstream, 0, 0L, false)]
    [InlineData(CaptureSilentReason.NoStreamOwnership, 0, 0L, false)]
    [InlineData(CaptureSilentReason.None, 2, 0L, false)]
    [InlineData(CaptureSilentReason.None, null, 0L, false)]
    [InlineData(CaptureSilentReason.None, 0, 3L, false)]
    public void OnlyASessionWithNothingToExplainAwayIsHealthy(
        CaptureSilentReason silent, int? preexisting, long dropped, bool healthy)
    {
        Assert.Equal(healthy, new CaptureSessionHealth(Session, silent, preexisting, dropped).IsHealthy);
    }

    [Fact]
    public void HealthIsKeptPerSessionAndMergesToTheWorstReading()
    {
        var observer = Observer();
        observer.AdoptSession("session-two");

        Assert.True(observer.RecordSessionHealth(new CaptureSessionHealth(Session, CaptureSilentReason.Midstream, 1, 0)));
        Assert.True(observer.RecordSessionHealth(new CaptureSessionHealth(Session, CaptureSilentReason.None, 0, 5)));
        Assert.True(observer.RecordSessionHealth(new CaptureSessionHealth("session-two", CaptureSilentReason.None, 0, 0)));
        Assert.False(observer.RecordSessionHealth(new CaptureSessionHealth("never-accepted", CaptureSilentReason.None, 0, 0)));

        var health = observer.Snapshot().SessionHealth;
        Assert.Equal(new CaptureSessionHealth(Session, CaptureSilentReason.Midstream, 1, 5), health[Session]);
        Assert.True(health["session-two"].IsHealthy);
        Assert.False(health.ContainsKey("never-accepted"));
    }

    [Fact]
    public void EveryLiveConnectionTagNamesTheCaptureSessionItBelongsTo()
    {
        var observer = Observer();
        Feed(observer, CalibrationObserverTests.Cluster(5_000, 5000));
        observer.AdoptSession("session-two");
        Feed(observer, CalibrationObserverTests.Cluster(60_000, 5000)
            .Select(message => message with { CaptureSessionId = "session-two" }));
        observer.Flush();

        var snapshot = observer.Snapshot();

        Assert.Equal(2, snapshot.Clusters.Count);
        Assert.Equal(Session, snapshot.ConnectionSessions[snapshot.Clusters[0].ConnectionTag]);
        Assert.Equal("session-two", snapshot.ConnectionSessions[snapshot.Clusters[1].ConnectionTag]);
    }

    // ------------------------------------------------------------------ declared candidates

    [Fact]
    public void ACandidatesPopIsCountedWithAndWithoutAnOutstandingRequest()
    {
        var observer = Observer();
        Assert.True(observer.RegisterCandidate(Candidate("true", TrueValues)));

        Feed(observer, CalibrationObserverTests.Session1().Append(ReplyPop(2_000)));
        observer.Flush();

        var counts = Assert.Single(observer.Snapshot().Candidates).Sessions[Session];
        Assert.Equal(1, counts.PopWithRequest);
        Assert.Equal(1, counts.PopWithoutRequest);
        Assert.True(counts.SightingsComplete);
        Assert.Equal(new[] { false, true }, counts.Sightings.Select(sighting => sighting.WithRequest));
        Assert.Equal(0, counts.ZoneOutside);
        Assert.Equal(0, counts.TerritoryOutside);
    }

    /// <summary>
    /// A load opening forgets the outstanding queue, so a player who teleports while queued and
    /// then gets the pop receives it with nothing outstanding. That is a sighting but not an
    /// unrequested pop: the roulette was requested on that same connection.
    /// </summary>
    [Fact]
    public void APopAfterATeleportClearedTheQueueIsASightingButNotUnrequested()
    {
        var observer = Observer();
        observer.RegisterCandidate(Candidate("true", TrueValues));

        Feed(observer, CalibrationObserverTests.Session1().Concat(CalibrationObserverTests.Cluster(90_000, 5000)));
        observer.Flush();

        var counts = Assert.Single(observer.Snapshot().Candidates).Sessions[Session];
        Assert.Equal(0, counts.PopWithRequest);
        Assert.Equal(0, counts.PopWithoutRequest);
        Assert.False(Assert.Single(counts.Sightings).WithRequest);
    }

    [Fact]
    public void OnlyWhatArrivesAfterRegistrationIsCounted()
    {
        var observer = Observer();
        var traffic = CalibrationObserverTests.Session1().Append(ReplyPop(2_000)).OrderBy(message => message.Mono).ToArray();
        Feed(observer, traffic.Where(message => message.Mono < TimeSpan.FromMilliseconds(100_000)));
        observer.RegisterCandidate(Candidate("late", TrueValues));
        var rest = traffic.Where(message => message.Mono >= TimeSpan.FromMilliseconds(100_000)).ToArray();

        Feed(observer, rest);

        var counts = Assert.Single(observer.Snapshot().Candidates).Sessions[Session];
        Assert.Equal(rest[0].ObservedAtUtc, counts.ObservedFromUtc);
        Assert.Equal(1, counts.PopWithRequest);
        Assert.Equal(0, counts.PopWithoutRequest);
    }

    [Fact]
    public void TheDeclaredZoneAndTerritoryTravellingOutsideABurstAreCountedPerSession()
    {
        var observer = Observer();
        observer.RegisterCandidate(Candidate("true", TrueValues));
        var strays = CalibrationObserverTests.Cluster(180_000, 5000)
            .Where(message => message.Opcode is CalibrationTrafficCases.ZoneInit or CalibrationTrafficCases.Territory);

        Feed(observer, CalibrationObserverTests.Session1().Concat(strays));
        observer.Flush();

        var counts = Assert.Single(observer.Snapshot().Candidates).Sessions[Session];
        Assert.Equal(1, counts.ZoneOutside);
        Assert.Equal(1, counts.TerritoryOutside);
    }

    [Fact]
    public void AMessageThatDoesNotParseAsTheDeclaredPopIsNotASighting()
    {
        var observer = Observer();
        observer.RegisterCandidate(Candidate("true", TrueValues));

        Feed(observer, new[]
        {
            ReplyPop(1_000, state: 5),
            ReplyPop(2_000, length: 41),
            ReplyPop(3_000, roulette: 0),
            ReplyPop(4_000, roulette: 250),
        });

        var counts = Assert.Single(observer.Snapshot().Candidates).Sessions[Session];
        Assert.Equal(0, counts.PopWithRequest);
        Assert.Equal(0, counts.PopWithoutRequest);
        Assert.Empty(counts.Sightings);
    }

    [Fact]
    public void CandidateRegistrationIsBounded()
    {
        var observer = Observer();
        for (var index = 0; index < CalibrationObserver.MaxCandidates; index++)
        {
            Assert.True(observer.RegisterCandidate(Candidate("c" + index, TrueValues)));
        }

        Assert.False(observer.RegisterCandidate(Candidate("one-too-many", TrueValues)));
        Assert.False(observer.RegisterCandidate(Candidate("c0", TrueValues)));
        Assert.True(observer.UnregisterCandidate("c0"));
        Assert.False(observer.UnregisterCandidate("c0"));
        Assert.True(observer.RegisterCandidate(Candidate("one-too-many", TrueValues)));
        Assert.Equal(CalibrationObserver.MaxCandidates, observer.Snapshot().Candidates.Count);
    }

    [Fact]
    public void SightingsAreBoundedAndSayWhenTheyAreIncomplete()
    {
        var observer = Observer();
        observer.RegisterCandidate(Candidate("true", TrueValues));

        Feed(observer, Enumerable.Range(0, CalibrationObserver.MaxCandidateSightings + 8).Select(i => ReplyPop(1_000 + i * 10)));

        var counts = Assert.Single(observer.Snapshot().Candidates).Sessions[Session];
        Assert.Equal(CalibrationObserver.MaxCandidateSightings + 8, counts.PopWithoutRequest);
        Assert.Equal(CalibrationObserver.MaxCandidateSightings, counts.Sightings.Count);
        Assert.False(counts.SightingsComplete);
    }

    [Fact]
    public void AQueueInferredCandidateHasNoServerPopToCount()
    {
        var observer = Observer();
        observer.RegisterCandidate(Candidate("queue", new CalibratedValues(
            new CalibratedPop(CalibrationMatchSource.QueueRequest, CalibrationTrafficCases.Request),
            CalibrationTrafficCases.ZoneInit, CalibrationTrafficCases.Territory)));

        Feed(observer, CalibrationObserverTests.Session1());

        var counts = Assert.Single(observer.Snapshot().Candidates).Sessions[Session];
        Assert.Equal(0, counts.PopWithRequest + counts.PopWithoutRequest);
    }

    // ------------------------------------------------------------------ persistence

    /// <summary>
    /// The file in tests/Fixtures/calibration-evidence was written by the evidence store before the
    /// fields above existed. Discarding it would lose the session it records, so the layout version
    /// does not change and every new field reads back as "not known".
    /// </summary>
    [Fact]
    public void AnEvidenceFileWrittenBeforeTheseFieldsExistedStillLoads()
    {
        Directory.CreateDirectory(_root);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "calibration-evidence", "cn.2026.09.01.0000.0000.json"),
            Path.Combine(_root, CalibrationEvidenceStore.FileNameFor(Region.Cn, Build)));

        Assert.Equal("OK", CalibrationEvidenceStore.Explain(_root, Region.Cn, Build, "template-sha"));
        var carried = CalibrationEvidenceStore.Load(_root, Region.Cn, Build, "template-sha");

        Assert.NotNull(carried);
        Assert.Equal(3, carried!.Clusters.Count);
        Assert.All(carried.Clusters, cluster =>
        {
            Assert.Null(cluster.Lobby);
            Assert.Empty(cluster.TerritoryReadings);
        });
        Assert.Empty(carried.SessionHealth);
        Assert.Empty(carried.ConnectionSessions);
        Assert.Empty(carried.Candidates);
        var draft = CalibrationDraft.Derive(carried, CalibrationObserverTests.Template());
        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.Equal(CalibrationTrafficCases.ExpectedMessages(CalibrationTrafficCases.ReplyState),
            draft.Messages.Select(message => message.Name));

        var observer = new CalibrationObserver(CalibrationObserverTests.Template(), Region.Cn, "session-two");
        observer.AdoptEvidence(carried);
        Assert.All(observer.Snapshot().Clusters, cluster => Assert.Null(cluster.Lobby));
    }

    /// <summary>
    /// The other layout already on players' disks: the file in tests/Fixtures/calibration-evidence ending
    /// in <c>.job-violations.json</c> comes from the 0.7.11 job fix's evidence store (25082e8 plus that
    /// fix), which added per-burst job violations to <c>jobs</c> and none of the fields above. It loads
    /// with its violations, the shared fields read as "not known", and the draft leaves out the job its
    /// duty-entry burst contradicts.
    /// </summary>
    [Fact]
    public void AnEvidenceFileWrittenWithPerBurstJobViolationsButNoSharedFieldsStillLoads()
    {
        Directory.CreateDirectory(_root);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "calibration-evidence", "cn.2026.09.01.0000.0000.job-violations.json"),
            Path.Combine(_root, CalibrationEvidenceStore.FileNameFor(Region.Cn, Build)));

        Assert.Equal("OK", CalibrationEvidenceStore.Explain(_root, Region.Cn, Build, "template-sha"));
        var carried = CalibrationEvidenceStore.Load(_root, Region.Cn, Build, "template-sha");

        Assert.NotNull(carried);
        Assert.Equal(3, carried!.Clusters.Count);
        Assert.Empty(carried.Clusters[0].JobViolations);
        Assert.Equal(1, carried.Clusters[1].JobViolations[CalibrationObserverTests.JobOpcode]);
        Assert.Equal(new long[] { 21, 21 }, carried.Clusters[1].JobValues[CalibrationObserverTests.JobOpcode]);
        Assert.All(carried.Clusters, cluster =>
        {
            Assert.Null(cluster.Lobby);
            Assert.Empty(cluster.TerritoryReadings);
        });
        Assert.Empty(carried.SessionHealth);
        Assert.Empty(carried.Candidates);
        var draft = CalibrationDraft.Derive(carried, CalibrationObserverTests.Template());
        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.Equal(new[] { "CONTENT_FINDER_POP", "ZONE_INITIALIZATION", "ZONE_TERRITORY" },
            draft.Messages.Select(message => message.Name));
        Assert.False(draft.Progress.JobSeen);

        // Saved again, the violations survive in the merged layout next to the shared-calibration fields.
        Assert.True(CalibrationEvidenceStore.Save(_root, Region.Cn, Build, "template-sha", carried));
        Assert.Equal(1, CalibrationEvidenceStore.Load(_root, Region.Cn, Build, "template-sha")!
            .Clusters[1].JobViolations[CalibrationObserverTests.JobOpcode]);
    }

    [Fact]
    public void BurstFactsSurviveARestartButHealthAndCandidatesDoNot()
    {
        var observer = Observer();
        observer.RegisterCandidate(Candidate("true", TrueValues));
        observer.RecordSessionHealth(new CaptureSessionHealth(Session, CaptureSilentReason.None, 0, 0));
        Feed(observer, CalibrationObserverTests.Cluster(1_000, 5000, connection: "lobby")
            .Concat(CalibrationObserverTests.Session1()));
        observer.Flush();
        var before = observer.Snapshot();

        Assert.True(CalibrationEvidenceStore.Save(_root, Region.Cn, Build, "template-sha", before));
        var text = File.ReadAllText(Path.Combine(_root, CalibrationEvidenceStore.FileNameFor(Region.Cn, Build)));
        var carried = CalibrationEvidenceStore.Load(_root, Region.Cn, Build, "template-sha")!;

        Assert.DoesNotContain("health", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("candidate", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before.Clusters.Select(cluster => cluster.Lobby), carried.Clusters.Select(cluster => cluster.Lobby));
        Assert.Equal(
            before.Clusters.SelectMany(cluster => cluster.TerritoryReadings),
            carried.Clusters.SelectMany(cluster => cluster.TerritoryReadings));
        Assert.Empty(carried.SessionHealth);
        Assert.Empty(carried.Candidates);
        Assert.Empty(carried.ConnectionSessions);

        var restored = new CalibrationObserver(CalibrationObserverTests.Template(), Region.Cn, "session-two");
        restored.AdoptEvidence(carried);
        var after = restored.Snapshot();
        Assert.Equal(before.Clusters.Select(cluster => cluster.Lobby), after.Clusters.Select(cluster => cluster.Lobby));
        Assert.All(after.Clusters, cluster => Assert.False(after.ConnectionSessions.ContainsKey(cluster.ConnectionTag)));
    }
}
