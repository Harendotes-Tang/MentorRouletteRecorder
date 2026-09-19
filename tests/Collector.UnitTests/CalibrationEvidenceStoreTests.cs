using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Evidence survives a restart of the Collector: what the observer has learned is persisted,
/// so installing a new version or restarting the software does not throw it away and cost the
/// player another evening of play to collect it again.
/// </summary>
public sealed class CalibrationEvidenceStoreTests : IDisposable
{
    private const string Build = "2026.09.01.0000.0000";
    private const string TemplateSha = "template-sha";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "MentorRecorder.Tests", "evidence-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    /// <summary>The session id the shared traffic helpers stamp their messages with.</summary>
    private const string FirstSession = "calibration-session";

    private static CalibrationSnapshot Observe(IEnumerable<DecodedMessage> traffic, string session = FirstSession)
    {
        var observer = new CalibrationObserver(CalibrationObserverTests.Template(), Region.Cn, session);
        foreach (var message in traffic.OrderBy(message => message.Mono))
        {
            observer.Accept(message);
        }

        observer.Flush();
        return observer.Snapshot();
    }

    /// <summary>
    /// A job reading that broke the constraints inside a burst disqualifies the shape for that
    /// burst, and the file has to say so, or a restart would turn a contradicted shape into a
    /// clean one and declare it.
    /// </summary>
    [Fact]
    public void AJobViolationInsideABurstSurvivesARestart()
    {
        var template = CalibrationObserverTests.Template();
        var before = Observe(CalibrationObserverTests.Session1().Concat(new[]
        {
            CalibrationObserverTests.Message(MessageDirection.Inbound, CalibrationObserverTests.JobOpcode,
                CalibrationObserverTests.Bytes(16, (0, 200)), 125_470),
        }));
        Assert.DoesNotContain(CalibrationDraft.Derive(before, template).Messages, message => message.Name == "PLAYER_JOB");

        Assert.True(CalibrationEvidenceStore.Save(_root, Region.Cn, Build, TemplateSha, before));
        var carried = CalibrationEvidenceStore.Load(_root, Region.Cn, Build, TemplateSha);

        Assert.NotNull(carried);
        Assert.Equal(1, carried!.Clusters[1].JobViolations[CalibrationObserverTests.JobOpcode]);
        Assert.Equal(new long[] { 21, 21 }, carried.Clusters[2].JobValues[CalibrationObserverTests.JobOpcode]);
        Assert.DoesNotContain(CalibrationDraft.Derive(carried, template).Messages, message => message.Name == "PLAYER_JOB");
    }

    [Fact]
    public void EvidenceSurvivesARestartAndStillDerivesTheSameDraft()
    {
        var template = CalibrationObserverTests.Template();
        var before = Observe(CalibrationObserverTests.Session1());
        var expected = CalibrationDraft.Derive(before, template);

        Assert.True(CalibrationEvidenceStore.Save(_root, Region.Cn, Build, TemplateSha, before));
        var carried = CalibrationEvidenceStore.Load(_root, Region.Cn, Build, TemplateSha);

        Assert.NotNull(carried);
        var restored = new CalibrationObserver(template, Region.Cn, "session-two");
        restored.AdoptEvidence(carried!);
        var after = restored.Snapshot();

        Assert.Equal(before.Pairs.Count, after.Pairs.Count);
        Assert.Equal(before.Pops.Count, after.Pops.Count);
        Assert.Equal(before.Clusters.Count, after.Clusters.Count);
        Assert.Equal(before.MessagesSeen, after.MessagesSeen);
        Assert.Equal(before.OutsideCounts.Count, after.OutsideCounts.Count);
        Assert.Equal(before.Markers.Count, after.Markers.Count);
        Assert.Equal(before.FirstMessageAtUtc, after.FirstMessageAtUtc);
        Assert.Equal(before.LastMessageAtUtc, after.LastMessageAtUtc);

        // The invariant that matters: the same draft comes out the other side.
        var actual = CalibrationDraft.Derive(after, template);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.MatchSource, actual.MatchSource);
        Assert.Equal(
            expected.Messages.Select(message => (message.Name, message.Opcode, message.ExpectedLength)),
            actual.Messages.Select(message => (message.Name, message.Opcode, message.ExpectedLength)));
        Assert.Equal(expected.Events.Select(item => item.Kind), actual.Events.Select(item => item.Kind));
    }

    [Fact]
    public void AnObserverCarriesOnCollectingAfterAdopting()
    {
        var template = CalibrationObserverTests.Template();
        // Everything except the duty the player has not finished yet.
        var firstEvening = CalibrationObserverTests.Session1()
            .Where(message => message.Mono < TimeSpan.FromMilliseconds(200_000))
            .ToArray();
        var before = Observe(firstEvening);
        Assert.True(CalibrationEvidenceStore.Save(_root, Region.Cn, Build, TemplateSha, before));

        var restored = new CalibrationObserver(template, Region.Cn, "session-two");
        restored.AdoptEvidence(CalibrationEvidenceStore.Load(_root, Region.Cn, Build, TemplateSha)!);
        foreach (var message in CalibrationObserverTests.Session1()
            .Where(message => message.Mono >= TimeSpan.FromMilliseconds(200_000))
            .OrderBy(message => message.Mono))
        {
            restored.Accept(message with { CaptureSessionId = "session-two" });
        }

        restored.Flush();
        var after = restored.Snapshot();

        // Adopting is a starting point, not a freeze: the evening carries on into the same
        // tables. (Whether the draft then completes also depends on the entry and the exit
        // sharing a capture session, which is a separate rule.)
        Assert.True(after.Clusters.Count > before.Clusters.Count);
        Assert.True(after.MessagesSeen > before.MessagesSeen);
        Assert.True(after.SessionCount >= 2);
        Assert.Equal(before.FirstMessageAtUtc, after.FirstMessageAtUtc);
        Assert.True(after.LastMessageAtUtc > before.LastMessageAtUtc);
    }

    /// <summary>
    /// The one failure mode persistence can invent. An observer that failed to adopt -
    /// unreadable file, changed template, anything - starts empty, and the periodic save must
    /// not write that emptiness over an evening's work.
    /// </summary>
    [Fact]
    public void APoorerSnapshotNeverOverwritesARicherOne()
    {
        var rich = Observe(CalibrationObserverTests.Session1());
        Assert.True(CalibrationEvidenceStore.Save(_root, Region.Cn, Build, TemplateSha, rich));

        var thin = Observe(CalibrationObserverTests.Cluster(5_000, 5000));
        Assert.True(thin.MessagesSeen < rich.MessagesSeen);
        Assert.False(CalibrationEvidenceStore.Save(_root, Region.Cn, Build, TemplateSha, thin));

        var kept = CalibrationEvidenceStore.Load(_root, Region.Cn, Build, TemplateSha);
        Assert.NotNull(kept);
        Assert.Equal(rich.MessagesSeen, kept!.MessagesSeen);
        Assert.Equal(rich.Clusters.Count, kept.Clusters.Count);
        Assert.Equal(rich.Pairs.Count, kept.Pairs.Count);
    }

    /// <summary>
    /// Discarding is the one legitimate way to end up with less, and it still works: the file is
    /// deleted first, so the next save has nothing to be poorer than.
    /// </summary>
    [Fact]
    public void AfterDiscardingASmallSnapshotIsWrittenNormally()
    {
        var rich = Observe(CalibrationObserverTests.Session1());
        CalibrationEvidenceStore.Save(_root, Region.Cn, Build, TemplateSha, rich);
        CalibrationEvidenceStore.Delete(_root, Region.Cn, Build);

        var thin = Observe(CalibrationObserverTests.Cluster(5_000, 5000));

        Assert.True(CalibrationEvidenceStore.Save(_root, Region.Cn, Build, TemplateSha, thin));
        Assert.Equal(
            thin.MessagesSeen,
            CalibrationEvidenceStore.Load(_root, Region.Cn, Build, TemplateSha)!.MessagesSeen);
    }

    /// <summary>
    /// The report must distinguish adopted evidence from a fresh start, or "it started over"
    /// stays a guess: an observer that adopted nothing reads carried = 0.
    /// </summary>
    [Fact]
    public void TheReportSaysHowMuchWasCarried()
    {
        var template = CalibrationObserverTests.Template();
        var before = Observe(CalibrationObserverTests.Session1());
        CalibrationEvidenceStore.Save(_root, Region.Cn, Build, TemplateSha, before);

        var restored = new CalibrationObserver(template, Region.Cn, "session-two");
        restored.AdoptEvidence(CalibrationEvidenceStore.Load(_root, Region.Cn, Build, TemplateSha)!);

        Assert.Equal(
            before.MessagesSeen,
            CalibrationEvidenceSummary.From(restored.Snapshot(), template).Carried);
        Assert.Equal(0, CalibrationEvidenceSummary.From(before, template).Carried);
    }

    /// <summary>
    /// Why a run started from nothing. A report of carried = 0 alone reads the same whether the
    /// file was absent, written under another template, or unreadable, so the store has to name
    /// which of them it was.
    /// </summary>
    [Fact]
    public void TheStoreSaysWhyItHandedNothingOver()
    {
        Assert.Equal("NO_FILE", CalibrationEvidenceStore.Explain(_root, Region.Cn, Build, TemplateSha));

        CalibrationEvidenceStore.Save(
            _root, Region.Cn, Build, TemplateSha, Observe(CalibrationObserverTests.Session1()));

        Assert.Equal("OK", CalibrationEvidenceStore.Explain(_root, Region.Cn, Build, TemplateSha));
        Assert.Equal("OTHER_TEMPLATE",
            CalibrationEvidenceStore.Explain(_root, Region.Cn, Build, "a-different-template"));
        Assert.Equal("NO_FILE",
            CalibrationEvidenceStore.Explain(_root, Region.Cn, "2026.10.01.0000.0000", TemplateSha));

        File.WriteAllText(Path.Combine(_root, CalibrationEvidenceStore.FileNameFor(Region.Cn, Build)), "{ no");
        Assert.Equal("UNREADABLE", CalibrationEvidenceStore.Explain(_root, Region.Cn, Build, TemplateSha));
    }

    [Fact]
    public void EvidenceCollectedUnderAnotherTemplateIsNotAdopted()
    {
        var before = Observe(CalibrationObserverTests.Session1());
        CalibrationEvidenceStore.Save(_root, Region.Cn, Build, TemplateSha, before);

        Assert.Null(CalibrationEvidenceStore.Load(_root, Region.Cn, Build, "a-different-template"));
        Assert.Null(CalibrationEvidenceStore.Load(_root, Region.Cn, "2026.10.01.0000.0000", TemplateSha));
    }

    [Fact]
    public void ACorruptOrMissingFileIsSimplyNoEvidence()
    {
        Assert.Null(CalibrationEvidenceStore.Load(_root, Region.Cn, Build, TemplateSha));

        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, CalibrationEvidenceStore.FileNameFor(Region.Cn, Build)), "{ not json");

        Assert.Null(CalibrationEvidenceStore.Load(_root, Region.Cn, Build, TemplateSha));
    }

    [Fact]
    public void DiscardingRemovesTheFileSoARestartDoesNotBringItBack()
    {
        var before = Observe(CalibrationObserverTests.Session1());
        CalibrationEvidenceStore.Save(_root, Region.Cn, Build, TemplateSha, before);
        Assert.NotNull(CalibrationEvidenceStore.Load(_root, Region.Cn, Build, TemplateSha));

        CalibrationEvidenceStore.Delete(_root, Region.Cn, Build);

        Assert.Null(CalibrationEvidenceStore.Load(_root, Region.Cn, Build, TemplateSha));
        CalibrationEvidenceStore.Delete(_root, Region.Cn, Build); // Twice is not an error.
    }

    /// <summary>
    /// The timing tables are the slowest evidence of all to collect - they need two duties on two
    /// different roulettes - so losing them to a restart would cost the player the whole evening
    /// twice over. A sighting still waiting for a duty to explain it keeps waiting: the software
    /// stopping is not the window running out.
    /// </summary>
    [Fact]
    public void TheTimingTablesSurviveARestart()
    {
        var template = CalibrationObserverTests.Template();
        var before = Observe(CalibrationTrafficCases.Traffic(CalibrationTrafficCases.QueueRequestAnnounced));
        var announced = before.TimedShapes.Single(shape => shape.Opcode == CalibrationTrafficCases.Announce);
        Assert.True(announced.InQueue > 0);

        Assert.True(CalibrationEvidenceStore.Save(_root, Region.Cn, Build, TemplateSha, before));
        var carried = CalibrationEvidenceStore.Load(_root, Region.Cn, Build, TemplateSha);

        Assert.NotNull(carried);
        var restored = new CalibrationObserver(template, Region.Cn, "session-two");
        restored.AdoptEvidence(carried!);
        var after = restored.Snapshot();

        Assert.Equal(before.TimedShapes.Count, after.TimedShapes.Count);
        Assert.Equal(before.TimedDead.Count, after.TimedDead.Count);
        Assert.Equal(before.TimingOverflow, after.TimingOverflow);
        var carriedShape = after.TimedShapes.Single(shape => shape.Opcode == CalibrationTrafficCases.Announce);
        Assert.Equal(
            (announced.Length, announced.Total, announced.InQueue, announced.PreDuty, announced.Stray,
                announced.SightingsComplete),
            (carriedShape.Length, carriedShape.Total, carriedShape.InQueue, carriedShape.PreDuty,
                carriedShape.Stray, carriedShape.SightingsComplete));
        Assert.Equal(
            announced.Sightings.Select(sighting => (sighting.AtUtc, sighting.TMs, sighting.ConnectionTag)),
            carriedShape.Sightings.Select(sighting => (sighting.AtUtc, sighting.TMs, sighting.ConnectionTag)));

        // The invariant that matters: the same message comes out the other side.
        Assert.Equal(
            CalibrationDraft.Derive(before, template).TimedAnnouncement?.Shape,
            CalibrationDraft.Derive(after, template).TimedAnnouncement?.Shape);
    }

    /// <summary>
    /// A file written by a version that had no timing tables still loads. Players upgrade in the
    /// middle of a build's life, and refusing their evidence would send them back to the start.
    /// </summary>
    [Fact]
    public void AFileWrittenBeforeTheTimingTablesExistedStillLoads()
    {
        CalibrationEvidenceStore.Save(
            _root, Region.Cn, Build, TemplateSha, Observe(CalibrationObserverTests.Session1()));
        var path = Path.Combine(_root, CalibrationEvidenceStore.FileNameFor(Region.Cn, Build));
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        document.Remove("timed_shapes");
        document.Remove("timed_dead");
        document.Remove("timing_overflow");
        File.WriteAllText(path, document.ToJsonString());

        var carried = CalibrationEvidenceStore.Load(_root, Region.Cn, Build, TemplateSha);

        Assert.NotNull(carried);
        Assert.Empty(carried!.TimedShapes);
        Assert.Empty(carried.TimedDead);
        Assert.Equal(0, carried.TimingOverflow);
    }

    [Fact]
    public void NoPayloadEverReachesTheFile()
    {
        // The snapshot has never held a payload byte, and this is the one place it would leak
        // if one ever started to. The duty name is the only free text a snapshot carries, and
        // it comes from the shipped duty table rather than from the wire.
        var before = Observe(CalibrationObserverTests.Session1());
        CalibrationEvidenceStore.Save(_root, Region.Cn, Build, TemplateSha, before);
        var text = File.ReadAllText(Path.Combine(_root, CalibrationEvidenceStore.FileNameFor(Region.Cn, Build)));

        Assert.DoesNotContain("payload", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bytes", text, StringComparison.OrdinalIgnoreCase);
        foreach (var cluster in before.Clusters)
        {
            Assert.DoesNotContain(cluster.ConnectionTag + "|", text, StringComparison.Ordinal);
        }
    }
}
