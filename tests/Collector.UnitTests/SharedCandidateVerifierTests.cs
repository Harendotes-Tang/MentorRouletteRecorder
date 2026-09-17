using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Protocol.Sharing;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Local verification of a shared calibration: pass on positive evidence from anywhere,
/// contradict only on what two healthy, complete capture sessions observed after the candidate
/// was registered, and wait in every other case.
/// </summary>
public sealed class SharedCandidateVerifierTests : IDisposable
{
    private static readonly DateTimeOffset Confirmed = new(2026, 9, 9, 12, 30, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "MentorRecorder.Tests", "verifier-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Covers a directory that no test in this class created.
        }
    }

    private static CalibrationTemplate Template => CalibrationObserverTests.Template();

    /// <summary>The code a player who played <paramref name="name"/> on evening A would share.</summary>
    private ShareCodePayload CodeFromEveningA(string name)
    {
        var draft = CalibrationTrafficCases.Derive(name);
        var written = LocalProfileWriter.Write(draft, Template, CalibrationTrafficCases.Build, Confirmed,
            Path.Combine(_root, "evening-a", Guid.NewGuid().ToString("N")));
        var exported = SharedProfileBuilder.ToShareCode(ProfileLoader.Load(written.Path), Template);
        Assert.Null(exported.Reason);
        return exported.Payload!;
    }

    private static DeclaredCandidate Candidate(ShareCodePayload payload) =>
        SharedCandidateVerifier.Candidate(payload, Template) ?? throw new InvalidOperationException("no candidate");

    private static CalibrationObserver Watching(DeclaredCandidate candidate)
    {
        var observer = new CalibrationObserver(Template, Region.Cn, "evening-b1");
        Assert.True(observer.RegisterCandidate(candidate));
        return observer;
    }

    private static CaptureSessionHealth Healthy(string session) => new(session, CaptureSilentReason.None, 0, 0);

    /// <summary>Plays one capture session of traffic, <paramref name="hour"/> hours after the helpers' clock.</summary>
    private static void Play(
        CalibrationObserver observer, string session, int hour, IEnumerable<DecodedMessage> traffic,
        CaptureSessionHealth? health = null, bool recordHealth = true)
    {
        observer.AdoptSession(session);
        var shift = TimeSpan.FromHours(hour);
        foreach (var message in traffic.OrderBy(message => message.Mono))
        {
            observer.Accept(message with
            {
                CaptureSessionId = session,
                ObservedAtUtc = message.ObservedAtUtc + shift,
                Mono = message.Mono + shift,
            });
        }

        observer.Flush();
        if (recordHealth)
        {
            Assert.True(observer.RecordSessionHealth(health ?? Healthy(session)));
        }
    }

    /// <summary>Judged by the strict gate unless a test says otherwise: an imported code must have every criterion pass.</summary>
    private static SharedVerification Verify(
        CalibrationObserver observer, DeclaredCandidate candidate, SharedCandidateProvenance provenance = SharedCandidateProvenance.Imported) =>
        SharedCandidateVerifier.Verify(observer.Snapshot(), Template, candidate, provenance);

    /// <summary>The login burst and nothing after it: no queue, no match, no duty.</summary>
    private static IEnumerable<DecodedMessage> LoginOnly(IEnumerable<DecodedMessage> traffic) =>
        traffic.Where(message => message.Mono < TimeSpan.FromMilliseconds(10_000));

    private static SharedGate GateOf(SharedVerification verification, string message) =>
        Assert.Single(verification.Criteria, criterion => criterion.Message == message).Gate;

    // ------------------------------------------------------------------ gates by provenance (plan §18.3)

    [Theory]
    [InlineData(CalibrationTrafficCases.ReplyState)]
    [InlineData(CalibrationTrafficCases.Announcement)]
    [InlineData(CalibrationTrafficCases.QueueRequest)]
    public void APublishedCodePassesOnTheLoginBurstAloneWhileAnImportedOneWaitsForTheMatch(string name)
    {
        var candidate = Candidate(CodeFromEveningA(name));
        var observer = Watching(candidate);

        Play(observer, "evening-b1", 24, LoginOnly(CalibrationTrafficCases.Traffic(name)));
        var published = Verify(observer, candidate, SharedCandidateProvenance.Published);
        var imported = Verify(observer, candidate, SharedCandidateProvenance.Imported);

        Assert.Equal(SharedVerdict.Pass, published.Verdict);
        Assert.True(published.AuditPending);
        Assert.Equal(SharedVerdict.Pass, Assert.Single(published.Criteria, criterion => criterion.Message == CalibratedShape.ZoneName).Verdict);
        Assert.Equal(SharedVerdict.Wait, Assert.Single(published.Criteria, criterion => criterion.Message == CalibratedShape.PopName).Verdict);

        Assert.Equal(SharedVerdict.Wait, imported.Verdict);
        Assert.False(imported.AuditPending);

        // The rest of the evening settles the audit; the imported code passes only now.
        Play(observer, "evening-b2", 48, CalibrationTrafficCases.Traffic(name));
        Assert.False(Verify(observer, candidate, SharedCandidateProvenance.Published).AuditPending);
        Assert.Equal(SharedVerdict.Pass, Verify(observer, candidate, SharedCandidateProvenance.Imported).Verdict);
    }

    [Fact]
    public void AZoneOnlyCodeWaitsForASecondZoneChangeWhateverItsProvenance()
    {
        var candidate = Candidate(CodeFromEveningA(CalibrationTrafficCases.ReplyStateMinimal));
        var observer = Watching(candidate);

        Play(observer, "evening-b1", 24, LoginOnly(CalibrationTrafficCases.Traffic(CalibrationTrafficCases.ReplyStateMinimal)));

        Assert.Equal(SharedVerdict.Wait, Verify(observer, candidate, SharedCandidateProvenance.Published).Verdict);
        Assert.Equal(SharedVerdict.Wait, Verify(observer, candidate, SharedCandidateProvenance.Imported).Verdict);
    }

    [Fact]
    public void TheGatesFollowTheProvenanceAndTheJobIsAlwaysOptional()
    {
        var candidate = Candidate(CodeFromEveningA(CalibrationTrafficCases.ReplyState));
        var observer = Watching(candidate);

        var published = Verify(observer, candidate, SharedCandidateProvenance.Published);
        var imported = Verify(observer, candidate, SharedCandidateProvenance.Imported);

        Assert.Equal(SharedGate.Required, GateOf(published, CalibratedShape.ZoneName));
        Assert.Equal(SharedGate.Audit, GateOf(published, CalibratedShape.PopName));
        Assert.Equal(SharedGate.Audit, GateOf(published, CalibratedShape.TerritoryName));
        Assert.Equal(SharedGate.Optional, GateOf(published, CalibratedShape.JobName));
        Assert.Equal(SharedGate.Required, GateOf(imported, CalibratedShape.ZoneName));
        Assert.Equal(SharedGate.Required, GateOf(imported, CalibratedShape.PopName));
        Assert.Equal(SharedGate.Required, GateOf(imported, CalibratedShape.TerritoryName));
        Assert.Equal(SharedGate.Optional, GateOf(imported, CalibratedShape.JobName));
    }

    [Theory]
    [MemberData(nameof(WrongOpcodes))]
    public void AWrongOpcodeContradictsAPublishedCodeTooWhateverTheGate(string name, string which)
    {
        var candidate = Candidate(Wrong(CodeFromEveningA(name), which));
        var observer = Watching(candidate);

        Play(observer, "evening-b1", 24, CalibrationTrafficCases.Traffic(name));
        Play(observer, "evening-b2", 48, CalibrationTrafficCases.Traffic(name));

        var result = Verify(observer, candidate, SharedCandidateProvenance.Published);
        Assert.Equal(SharedVerdict.Contradicted, result.Verdict);
        Assert.Equal(CriterionFor(which), Assert.Single(result.Criteria, criterion => criterion.Verdict == SharedVerdict.Contradicted).Message);
    }

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var name in CalibrationTrafficCases.All)
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ACodeFromOneEveningPassesOnAnotherEveningsEvidence(string name)
    {
        var candidate = Candidate(CodeFromEveningA(name));
        var observer = Watching(candidate);

        Play(observer, "evening-b1", 24, CalibrationTrafficCases.Traffic(name));
        var result = Verify(observer, candidate);

        Assert.Equal(SharedVerdict.Pass, result.Verdict);
        Assert.All(result.Criteria, criterion => Assert.Equal(SharedVerdict.Pass, criterion.Verdict));
        Assert.Equal(candidate.CandidateId, result.CandidateId);

        // A second healthy evening of the same true traffic contradicts nothing.
        Play(observer, "evening-b2", 48, CalibrationTrafficCases.Traffic(name));
        Assert.Equal(SharedVerdict.Pass, Verify(observer, candidate).Verdict);
    }

    public static TheoryData<string, string> WrongOpcodes() => new()
    {
        { CalibrationTrafficCases.ReplyState, "pop" },
        { CalibrationTrafficCases.ReplyState, "zone" },
        { CalibrationTrafficCases.ReplyState, "territory" },
        { CalibrationTrafficCases.ReplyStateMinimal, "zone" },
        { CalibrationTrafficCases.Announcement, "pop" },
        { CalibrationTrafficCases.MarkerOffset, "pop" },
        { CalibrationTrafficCases.QueueRequest, "pop" },
        { CalibrationTrafficCases.QueueRequest, "territory" },
    };

    private static ShareCodePayload Wrong(ShareCodePayload payload, string which) => which switch
    {
        "pop" => payload with { Pop = payload.Pop with { Opcode = 0x7777 } },
        "zone" => payload with { ZoneOpcode = 0x7778 },
        "territory" => payload with { TerritoryOpcode = 0x7779 },
        "job" => payload with { JobOpcode = 0x777A },
        _ => throw new ArgumentOutOfRangeException(nameof(which)),
    };

    private static string CriterionFor(string which) => which switch
    {
        "pop" => "CONTENT_FINDER_POP",
        "zone" => "ZONE_INITIALIZATION",
        "territory" => "ZONE_TERRITORY",
        _ => "PLAYER_JOB",
    };

    [Theory]
    [MemberData(nameof(WrongOpcodes))]
    public void AnySingleWrongOpcodeWaitsAfterOneHealthySessionAndIsContradictedAfterTwo(string name, string which)
    {
        var candidate = Candidate(Wrong(CodeFromEveningA(name), which));
        var observer = Watching(candidate);

        Play(observer, "evening-b1", 24, CalibrationTrafficCases.Traffic(name));
        var afterOne = Verify(observer, candidate);

        Assert.Equal(SharedVerdict.Wait, afterOne.Verdict);
        Assert.DoesNotContain(afterOne.Criteria, criterion => criterion.Verdict == SharedVerdict.Contradicted);

        Play(observer, "evening-b2", 48, CalibrationTrafficCases.Traffic(name));
        var afterTwo = Verify(observer, candidate);

        Assert.Equal(SharedVerdict.Contradicted, afterTwo.Verdict);
        var contradicted = Assert.Single(afterTwo.Criteria, criterion => criterion.Verdict == SharedVerdict.Contradicted);
        Assert.Equal(CriterionFor(which), contradicted.Message);
        Assert.Equal(2, contradicted.ContradictingSessions);
    }

    public static TheoryData<string, CaptureSilentReason, int?, long, bool> UnhealthyReadings() => new()
    {
        { "midstream", CaptureSilentReason.Midstream, 0, 0L, true },
        { "attached to open connections", CaptureSilentReason.None, 2, 0L, true },
        { "unknown preexisting connections", CaptureSilentReason.None, null, 0L, true },
        { "adapter dropped packets", CaptureSilentReason.None, 0, 12L, true },
        { "no reading at all", CaptureSilentReason.None, 0, 0L, false },
    };

    [Theory]
    [MemberData(nameof(UnhealthyReadings))]
    public void AbsenceInSessionsThatCouldNotHaveSeenItOnlyWaits(
        string why, CaptureSilentReason silent, int? preexisting, long dropped, bool record)
    {
        var candidate = Candidate(Wrong(CodeFromEveningA(CalibrationTrafficCases.ReplyState), "zone"));
        var observer = Watching(candidate);

        foreach (var (session, hour) in new[] { ("evening-b1", 24), ("evening-b2", 48), ("evening-b3", 72) })
        {
            Play(observer, session, hour, CalibrationTrafficCases.Traffic(CalibrationTrafficCases.ReplyState),
                new CaptureSessionHealth(session, silent, preexisting, dropped), record);
        }

        Assert.True(Verify(observer, candidate).Verdict == SharedVerdict.Wait, why);
    }

    [Fact]
    public void AnObservationTableThatOverflowedCannotContradictAnything()
    {
        var candidate = Candidate(Wrong(CodeFromEveningA(CalibrationTrafficCases.ReplyState), "zone"));
        var observer = Watching(candidate);
        Play(observer, "evening-b1", 24, CalibrationTrafficCases.Traffic(CalibrationTrafficCases.ReplyState));
        Play(observer, "evening-b2", 48, CalibrationTrafficCases.Traffic(CalibrationTrafficCases.ReplyState));

        var overflowed = observer.Snapshot() with { OverflowCount = 1 };

        Assert.Equal(SharedVerdict.Wait, SharedCandidateVerifier.Verify(overflowed, Template, candidate, SharedCandidateProvenance.Imported).Verdict);
    }

    /// <summary>
    /// Evidence read back from disk has no capture health and belongs to no live session: it can
    /// make a true code pass, and however much of it there is, it cannot contradict a wrong one.
    /// </summary>
    [Fact]
    public void CarriedEvidenceCanOnlyEverSupportAPass()
    {
        var truth = CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        var earlier = new CalibrationObserver(Template, Region.Cn, "evening-b1");
        Play(earlier, "evening-b1", 24, CalibrationTrafficCases.Traffic(CalibrationTrafficCases.ReplyState));
        Play(earlier, "evening-b2", 48, CalibrationTrafficCases.Traffic(CalibrationTrafficCases.ReplyState));
        Assert.True(CalibrationEvidenceStore.Save(_root, Region.Cn, CalibrationTrafficCases.Build, "t", earlier.Snapshot()));
        var carried = CalibrationEvidenceStore.Load(_root, Region.Cn, CalibrationTrafficCases.Build, "t")!;

        foreach (var (payload, expected) in new[]
        {
            (truth, SharedVerdict.Pass),
            (Wrong(truth, "zone"), SharedVerdict.Wait),
            (Wrong(truth, "territory"), SharedVerdict.Wait),
            // A job the evidence cannot vouch for waits on its own criterion; it never holds the code back.
            (Wrong(truth, "job"), SharedVerdict.Pass),
            (Wrong(truth, "pop"), SharedVerdict.Wait),
        })
        {
            var candidate = Candidate(payload);
            var restarted = new CalibrationObserver(Template, Region.Cn, "evening-c");
            restarted.AdoptEvidence(carried);
            restarted.RegisterCandidate(candidate);
            restarted.RecordSessionHealth(Healthy("evening-c"));

            Assert.Equal(expected, Verify(restarted, candidate).Verdict);
        }
    }

    /// <summary>
    /// Queueing and then entering a duty long after the match window - by another roulette, a
    /// party finder, anything - is ordinary play. It gives a match message nothing to explain, so
    /// it only makes the pop wait, however many sessions do it.
    /// </summary>
    [Fact]
    public void QueueingAndThenEnteringAnotherDutyWithoutAPopOnlyWaits()
    {
        var candidate = Candidate(CodeFromEveningA(CalibrationTrafficCases.ReplyState));
        var observer = Watching(candidate);
        var evening = CalibrationObserverTests.Cluster(5_000, 5000)
            .Concat(new[]
            {
                CalibrationObserverTests.Message(
                    MessageDirection.Outbound, CalibrationTrafficCases.Request, CalibrationObserverTests.Bytes(24, (0, 1)), 60_000),
                CalibrationObserverTests.Message(
                    MessageDirection.Inbound, CalibrationTrafficCases.Reply, CalibrationObserverTests.Bytes(40, (9, 5), (16, 1)), 60_120),
            })
            .Concat(CalibrationObserverTests.Noise(61_000, 690_000))
            .Concat(CalibrationObserverTests.Cluster(700_000, 1039))
            .Concat(CalibrationObserverTests.Noise(705_000, 790_000))
            .Concat(CalibrationObserverTests.Cluster(800_000, 5000))
            .ToArray();

        Play(observer, "evening-b1", 24, evening);
        Play(observer, "evening-b2", 48, evening);
        var result = Verify(observer, candidate);

        Assert.Equal(SharedVerdict.Wait, result.Verdict);
        Assert.Equal(SharedVerdict.Wait, Assert.Single(result.Criteria, criterion => criterion.Message == "CONTENT_FINDER_POP").Verdict);
    }

    /// <summary>A declared pop that keeps arriving without a request, in two healthy sessions, is not a pop.</summary>
    [Fact]
    public void ADeclaredPopThatKeepsArrivingWithoutARequestIsContradicted()
    {
        var candidate = Candidate(CodeFromEveningA(CalibrationTrafficCases.ReplyState));
        var observer = Watching(candidate);
        var evening = CalibrationObserverTests.Session1().Concat(new[] { 1_000L, 2_000L }.Select(at =>
            CalibrationObserverTests.Message(
                MessageDirection.Inbound, CalibrationTrafficCases.Reply, CalibrationObserverTests.Bytes(40, (9, 3), (16, 1)), at)));

        Play(observer, "evening-b1", 24, evening);
        Play(observer, "evening-b2", 48, evening);
        var result = Verify(observer, candidate);

        Assert.Equal(SharedVerdict.Contradicted, result.Verdict);
        Assert.Equal(SharedVerdict.Contradicted,
            Assert.Single(result.Criteria, criterion => criterion.Message == "CONTENT_FINDER_POP").Verdict);
    }

    private static DecodedMessage Moved(DecodedMessage message, long toMs) => message with
    {
        Mono = TimeSpan.FromMilliseconds(toMs),
        ObservedAtUtc = message.ObservedAtUtc + (TimeSpan.FromMilliseconds(toMs) - message.Mono),
    };

    /// <summary>
    /// The duty was recognised by another territory-shaped message while the declared one arrived a
    /// few seconds before the burst began. Its absence from the burst is the burst boundary, not the
    /// build, and two sessions of it must not throw a true code away.
    /// </summary>
    [Fact]
    public void ADeclaredTerritoryThatMissedTheDutyBurstIsNotContradictedByItsAbsence()
    {
        var candidate = Candidate(CodeFromEveningA(CalibrationTrafficCases.ReplyState));
        var observer = Watching(candidate);
        var evening = CalibrationObserverTests.Session1()
            .Select(message => message.Opcode == CalibrationTrafficCases.Territory &&
                message.Mono == TimeSpan.FromMilliseconds(125_000)
                    ? Moved(message, 121_000)
                    : message)
            .Append(CalibrationObserverTests.Message(MessageDirection.Inbound, CalibrationTrafficCases.DecoyTerritory,
                CalibrationObserverTests.Bytes(136, (2, 15), (3, 4)), 125_010))
            .ToArray();

        Play(observer, "evening-b1", 24, evening);
        Play(observer, "evening-b2", 48, evening);
        var result = Verify(observer, candidate);

        Assert.NotEqual(SharedVerdict.Contradicted, result.Verdict);
        Assert.Equal(SharedVerdict.Wait,
            Assert.Single(result.Criteria, criterion => criterion.Message == "ZONE_TERRITORY").Verdict);
    }

    /// <summary>
    /// The same boundary forgiveness for the job message: seen just outside the exit burst, twice. Its absence
    /// from that burst contradicts nothing, and the other two bursts are still a majority that vouches for it.
    /// </summary>
    [Fact]
    public void ADeclaredJobThatMissedABurstIsNotContradictedByItsAbsence()
    {
        var candidate = Candidate(CodeFromEveningA(CalibrationTrafficCases.ReplyState));
        var observer = Watching(candidate);
        var evening = CalibrationObserverTests.Session1()
            .Select(message => message.Opcode == CalibrationTrafficCases.Job &&
                message.Mono >= TimeSpan.FromMilliseconds(215_000) && message.Mono < TimeSpan.FromMilliseconds(216_000)
                    ? Moved(message, (long)message.Mono.TotalMilliseconds - 4_000)
                    : message)
            .ToArray();

        Play(observer, "evening-b1", 24, evening);
        Play(observer, "evening-b2", 48, evening);
        var result = Verify(observer, candidate);

        Assert.Equal(SharedVerdict.Pass, result.Verdict);
        var job = Assert.Single(result.Criteria, criterion => criterion.Message == "PLAYER_JOB");
        Assert.Equal(SharedVerdict.Pass, job.Verdict);
        Assert.Equal(0, job.ContradictingSessions);
    }

    /// <summary>
    /// On some builds the login burst does not carry the job. Entry and exit do, which is all a record needs
    /// and all local calibration declares, so the shared verifier must not ask for more.
    /// </summary>
    [Fact]
    public void AJobMissingFromTheLoginBurstStillPasses()
    {
        var candidate = Candidate(CodeFromEveningA(CalibrationTrafficCases.ReplyState));
        var observer = Watching(candidate);
        var evening = CalibrationTrafficCases.Traffic(CalibrationTrafficCases.ReplyState)
            .Where(message => !(message.Opcode == CalibrationTrafficCases.Job && message.Mono < TimeSpan.FromMilliseconds(10_000)))
            .ToArray();

        Play(observer, "evening-b1", 24, evening);
        var result = Verify(observer, candidate);

        Assert.Equal(SharedVerdict.Pass, result.Verdict);
        Assert.Equal(SharedVerdict.Pass, Assert.Single(result.Criteria, criterion => criterion.Message == "PLAYER_JOB").Verdict);
    }

    /// <summary>
    /// Seen in fewer than half of the bursts since the code arrived, the job waits; however many sessions look like
    /// that, its absence from the other bursts contradicts nothing, and the waiting job does not hold the code back.
    /// </summary>
    [Fact]
    public void AJobSeenInFewerThanHalfOfTheBurstsSinceRegistrationWaitsWithoutHoldingTheCodeBack()
    {
        var candidate = Candidate(CodeFromEveningA(CalibrationTrafficCases.ReplyState));
        var observer = Watching(candidate);
        var evening = CalibrationTrafficCases.Traffic(CalibrationTrafficCases.ReplyState)
            .Where(message => !(message.Opcode == CalibrationTrafficCases.Job && message.Mono < TimeSpan.FromMilliseconds(200_000)))
            .ToArray();

        Play(observer, "evening-b1", 24, evening);
        Play(observer, "evening-b2", 48, evening);
        var result = Verify(observer, candidate);

        Assert.Equal(SharedVerdict.Pass, result.Verdict);
        var job = Assert.Single(result.Criteria, criterion => criterion.Message == "PLAYER_JOB");
        Assert.Equal(SharedVerdict.Wait, job.Verdict);
        Assert.Equal(0, job.ContradictingSessions);
    }

    /// <summary>
    /// A job message that never arrives - the wrong opcode, or a build that does not send it where this one looks -
    /// is only absent, and absence proves nothing about a job: it waits for ever and never holds the code back.
    /// Records are then made without a job, exactly as after a local calibration that could not name one.
    /// </summary>
    [Fact]
    public void AJobMessageThatNeverArrivesWaitsWithoutHoldingBackACodeThatOtherwisePasses()
    {
        var candidate = Candidate(Wrong(CodeFromEveningA(CalibrationTrafficCases.ReplyState), "job"));
        var observer = Watching(candidate);

        Play(observer, "evening-b1", 24, CalibrationTrafficCases.Traffic(CalibrationTrafficCases.ReplyState));
        Play(observer, "evening-b2", 48, CalibrationTrafficCases.Traffic(CalibrationTrafficCases.ReplyState));
        var result = Verify(observer, candidate);

        Assert.Equal(SharedVerdict.Pass, result.Verdict);
        var job = Assert.Single(result.Criteria, criterion => criterion.Message == "PLAYER_JOB");
        Assert.Equal(SharedVerdict.Wait, job.Verdict);
        Assert.Equal(0, job.ContradictingSessions);
        Assert.All(result.Criteria.Where(criterion => criterion.Message != "PLAYER_JOB"),
            criterion => Assert.Equal(SharedVerdict.Pass, criterion.Verdict));
    }

    public static TheoryData<string> UnreadableJobs() => new() { "out-of-range", "disagreeing" };

    /// <summary>
    /// What does contradict a job message is a reading no job message can produce inside a burst - out of range,
    /// or two different jobs within one zone change - and, like every other criterion, only in two healthy
    /// sessions. One such session leaves the job waiting, which does not hold back a code that otherwise passed.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnreadableJobs))]
    public void AJobMessageThatReadsLikeNoJobIsContradictedAfterTwoHealthySessions(string how)
    {
        var candidate = Candidate(Wrong(CodeFromEveningA(CalibrationTrafficCases.ReplyState), "job"));
        var observer = Watching(candidate);
        var evening = CalibrationTrafficCases.Traffic(CalibrationTrafficCases.ReplyState)
            .Concat(new[] { 5_000L, 125_000L, 215_000L }.SelectMany(burst => how == "out-of-range"
                ? new[] { WrongJobAt(burst + 470, 200) }
                : new[] { WrongJobAt(burst + 470, 7), WrongJobAt(burst + 480, 8) }))
            .ToArray();

        Play(observer, "evening-b1", 24, evening);
        var afterOne = Verify(observer, candidate);

        Assert.Equal(SharedVerdict.Pass, afterOne.Verdict);
        var waiting = Assert.Single(afterOne.Criteria, criterion => criterion.Message == "PLAYER_JOB");
        Assert.Equal(SharedVerdict.Wait, waiting.Verdict);
        Assert.Equal(1, waiting.ContradictingSessions);

        Play(observer, "evening-b2", 48, evening);
        var afterTwo = Verify(observer, candidate);

        Assert.Equal(SharedVerdict.Contradicted, afterTwo.Verdict);
        var contradicted = Assert.Single(afterTwo.Criteria, criterion => criterion.Verdict == SharedVerdict.Contradicted);
        Assert.Equal("PLAYER_JOB", contradicted.Message);
        Assert.Equal(2, contradicted.ContradictingSessions);
    }

    /// <summary>A job-shaped message on the opcode <see cref="Wrong"/> declares for the job.</summary>
    private static DecodedMessage WrongJobAt(long t, byte value) =>
        CalibrationObserverTests.Message(MessageDirection.Inbound, 0x777A, CalibrationObserverTests.Bytes(16, (0, value)), t);

    /// <summary>
    /// A session in which the player never queued can still hold one coincidental pair: a client
    /// message that happened to carry a roulette-sized number the server echoed. One such pair is
    /// not evidence that the request is another opcode.
    /// </summary>
    [Fact]
    public void AStrayPairInSessionsWithoutAQueueDoesNotContradictAQueueInferredCode()
    {
        var candidate = Candidate(CodeFromEveningA(CalibrationTrafficCases.QueueRequest));
        var observer = Watching(candidate);
        var evening = CalibrationObserverTests.Cluster(5_000, 5000)
            .Concat(new[]
            {
                CalibrationObserverTests.Message(MessageDirection.Outbound, 0xBEEF, CalibrationObserverTests.Bytes(24, (0, 2)), 60_000),
                CalibrationObserverTests.Message(MessageDirection.Inbound, CalibrationTrafficCases.Reply,
                    CalibrationObserverTests.Bytes(40, (9, 5), (16, 2)), 60_120),
            })
            .Concat(CalibrationObserverTests.Noise(61_000, 120_000))
            .Concat(CalibrationObserverTests.Cluster(125_000, 1039))
            .Concat(CalibrationObserverTests.Noise(130_000, 210_000))
            .Concat(CalibrationObserverTests.Cluster(215_000, 5000))
            .ToArray();

        Play(observer, "evening-b1", 24, evening);
        Play(observer, "evening-b2", 48, evening);
        var result = Verify(observer, candidate);

        Assert.NotEqual(SharedVerdict.Contradicted, result.Verdict);
        Assert.Equal(SharedVerdict.Wait,
            Assert.Single(result.Criteria, criterion => criterion.Message == "CONTENT_FINDER_POP").Verdict);
    }

    /// <summary>
    /// Review finding: the queue-request criterion must, like the others, judge only what arrived
    /// after the candidate was registered. Two sessions that ended before the code arrived say
    /// nothing against it, however clearly they paired another request opcode.
    /// </summary>
    [Fact]
    public void SessionsPlayedBeforeRegistrationCannotContradictAQueueInferredCode()
    {
        var wrong = Candidate(Wrong(CodeFromEveningA(CalibrationTrafficCases.QueueRequest), "pop"));
        var observer = new CalibrationObserver(Template, Region.Cn, "evening-b1");
        Play(observer, "evening-b1", 24, CalibrationTrafficCases.Traffic(CalibrationTrafficCases.QueueRequest));
        Play(observer, "evening-b2", 48, CalibrationTrafficCases.Traffic(CalibrationTrafficCases.QueueRequest));

        Assert.True(observer.RegisterCandidate(wrong));
        var result = Verify(observer, wrong);

        Assert.Equal(SharedVerdict.Wait, result.Verdict);
        Assert.Equal(0, Assert.Single(result.Criteria, criterion => criterion.Message == "CONTENT_FINDER_POP").ContradictingSessions);
    }

    [Fact]
    public void EveryCriterionExplainsItselfInPlainChinese()
    {
        var candidate = Candidate(Wrong(CodeFromEveningA(CalibrationTrafficCases.ReplyState), "zone"));
        var observer = Watching(candidate);
        Play(observer, "evening-b1", 24, CalibrationTrafficCases.Traffic(CalibrationTrafficCases.ReplyState));
        Play(observer, "evening-b2", 48, CalibrationTrafficCases.Traffic(CalibrationTrafficCases.ReplyState));

        var result = Verify(observer, candidate);

        Assert.Equal(new[] { "CONTENT_FINDER_POP", "ZONE_INITIALIZATION", "ZONE_TERRITORY", "PLAYER_JOB" },
            result.Criteria.Select(criterion => criterion.Message));
        Assert.All(result.Criteria, criterion =>
        {
            Assert.Matches("[\\u4e00-\\u9fff]", criterion.Reason);
            Assert.DoesNotContain("0x", criterion.Reason, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void ACodeForAnotherTemplateHasNoCandidateHere()
    {
        var payload = CodeFromEveningA(CalibrationTrafficCases.ReplyState);

        Assert.Null(SharedCandidateVerifier.Candidate(payload with { TemplateSha256 = new string('1', 64) }, Template));
        Assert.Null(SharedCandidateVerifier.Candidate(
            payload with { Pop = payload.Pop with { SelectorValues = new long[] { 3, 4 } } }, Template));
        Assert.Equal(ShareCode.Sha256(payload), Candidate(payload).CandidateId);
    }
}
