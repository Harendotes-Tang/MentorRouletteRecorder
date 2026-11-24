using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Sharing;
using MentorRecorder.Collector.Protocol.Profiles;
using Bed = MentorRecorder.Collector.UnitTests.SharedCalibrationTestBed;

namespace MentorRecorder.Collector.UnitTests;

public sealed class CalibrationEvidenceCollectorTests
{
    private static CalibrationTemplate Template => Bed.Template;

    private static CalibrationEvidenceCollector Collector(string? root = null) =>
        new(new CalibrationEvidenceReceiptStore(root));

    /// <summary>Registers the code a player who played the reply-state evening would share, at epoch 0.</summary>
    private static SharedCode Register(CalibrationEvidenceCollector collector, Bed bed)
    {
        var code = bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        Register(collector, code);
        return code;
    }

    private static void Register(CalibrationEvidenceCollector collector, SharedCode code, int epoch = 0) =>
        collector.Register(Region.Cn, Bed.Build, Template, SharedCandidateVerifier.Candidate(code.Payload, Template)!, epoch);

    private static void Feed(CalibrationEvidenceCollector collector, string session, string name = CalibrationTrafficCases.ReplyState)
    {
        foreach (var message in CalibrationTrafficCases.Traffic(name).OrderBy(message => message.Mono))
            collector.Accept(message with { CaptureSessionId = session });
    }

    /// <summary>A healthy capture session that has seen the reply-state evening and is still running.</summary>
    private static void Observe(CalibrationEvidenceCollector collector, string session)
    {
        collector.Start(session);
        collector.Health(Bed.Healthy(session));
        Feed(collector, session);
    }

    /// <summary><see cref="Observe"/>, then the capture session ends.</summary>
    private static void CompleteCapture(
        CalibrationEvidenceCollector collector, string session, CaptureEndReason end = CaptureEndReason.UserStop)
    {
        Observe(collector, session);
        collector.Complete(session, end);
    }

    [Fact]
    public async Task RejectedCandidatesReleasePublicObservationSlotsAcrossRediscovery()
    {
        using var bed = new Bed();
        var source = bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        var pipeline = bed.Pipeline(bed.Services(fetch: false));
        pipeline.Refresh(Bed.Game());
        SharedCode? last = null;
        for (var index = 0; index <= CalibrationObserver.MaxCandidates; index++)
        {
            last = Bed.Encode(source.Payload with { ZoneOpcode = (ushort)(60000 + index) });
            Assert.Equal(SharedImportOutcome.Applied, pipeline.ImportCalibrationCode(last.Code).Outcome);
            if (index == CalibrationObserver.MaxCandidates) break;
            Assert.Equal(1, pipeline.RejectSharedCalibration().DroppedCandidates);
            pipeline.DiscardCalibration();
            await Bed.Idle(pipeline);
        }

        var session = bed.Start(pipeline);
        Bed.Feed(pipeline, session, Bed.Evening());
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);

        Assert.True(pipeline.GetCalibrationEvidence(last!.Sha).Available);
    }

    [Fact]
    public void ReplacingSelectedLocalProfilesReleasesPublicObservationSlots()
    {
        using var bed = new Bed();
        var draft = CalibrationTrafficCases.Derive(CalibrationTrafficCases.ReplyState);
        var pipeline = new Protocol.Pipeline.LiveProtocolPipeline(bed.Db.Database, bed.Db.Clock,
            new Ipc.LiveEventBus(bed.Db.Clock), game => bed.DiskSelect()(game), null, bed.Services(fetch: false));
        string? code = null;
        for (var index = 0; index <= CalibrationObserver.MaxCandidates; index++)
        {
            var changed = draft with
            {
                Messages = draft.Messages.Select(message => message.Name == CalibratedShape.ZoneName
                    ? message with { Opcode = (ushort)(60000 + index) } : message).ToArray(),
            };
            var written = LocalProfileWriter.Write(changed, Template, Bed.Build, Bed.Confirmed, bed.LocalRoot);
            code = SharedProfileBuilder.ToShareCode(ProfileLoader.Load(written.Path), Template).CodeSha256;
            Assert.Equal(ProfileOrigin.Local, pipeline.Refresh(Bed.Game()).Origin);
        }

        var session = bed.Start(pipeline);
        Bed.Feed(pipeline, session, Bed.Evening());
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);

        Assert.True(pipeline.GetCalibrationEvidence(code).Available);
    }

    [Fact]
    public void DisarmingCalibrationKeepsTheSameSelectedProfilesPublicObservation()
    {
        using var bed = new Bed();
        var written = bed.WriteLocalProfile(CalibrationTrafficCases.QueueRequest);
        var code = SharedProfileBuilder.ToShareCode(ProfileLoader.Load(written), Template).CodeSha256;
        var pipeline = bed.Pipeline(bed.Services(fetch: false));
        Assert.Equal(ProfileOrigin.Local, pipeline.Refresh(Bed.Game()).Origin);
        Assert.True(pipeline.CalibrationArmed);
        var session = bed.Start(pipeline);
        Bed.Feed(pipeline, session, CalibrationTrafficCases.Traffic(CalibrationTrafficCases.QueueRequest));

        pipeline.ApplyCalibrationSetting(false);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);

        Assert.True(pipeline.GetCalibrationEvidence(code).Available);
    }

    [Fact]
    public async Task BindingCannotRelabelPublicEvidenceWithTheOldEpochWhenStateWriteFails()
    {
        using var bed = new Bed();
        var code = bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        bed.Publish(code);
        var pipeline = await bed.ReadyPipelineAsync();
        using var stateLock = bed.HoldStateFile(FileShare.Read);
        bed.PublishListed(new Bed.Listing(code, Quarantined: true, QuarantineEpoch: 1));
        bed.Transport.BeforeAnswer = (uri, _) => uri == SharedCalibrationClient.IndexUri(SharedCalibrationSource.GithubRaw)
            ? Task.CompletedTask : Task.FromException(new IOException("code file unavailable"));
        Assert.Equal(SharedCheckOutcome.Started, pipeline.CheckSharedCalibrationNow());
        await Bed.Idle(pipeline);
        Assert.Equal(0, Assert.Single(bed.Store.LoadCandidates(Region.Cn, Bed.Build, Bed.TemplateSha)).QuarantineEpoch);

        for (var hour = 0; hour < 3; hour++)
        {
            var session = bed.Start(pipeline);
            await Bed.FeedAndSettleAsync(pipeline, session, Bed.Evening(), hour);
            await Bed.StopAndSettleAsync(pipeline, session);
        }

        Assert.Equal(ProfileStatus.Verified, pipeline.Current.Status);
        var evidence = pipeline.GetCalibrationEvidence(code.Sha);
        Assert.True(evidence.Available);
        Assert.Equal(1, JsonNode.Parse(evidence.EvidenceJson!)!["target"]!["quarantine_epoch"]!.GetValue<int>());
    }

    [Fact]
    public void ReleasingACandidateKeepsReceiptsButRequiresANewSessionForReRegistration()
    {
        using var bed = new Bed();
        var collector = Collector(Path.Combine(bed.Root, "public-evidence"));
        var code = Register(collector, bed);
        var candidate = SharedCandidateVerifier.Candidate(code.Payload, Template)!;
        CompleteCapture(collector, "completed");
        var receipt = collector.Export(Region.Cn, Bed.Build, code.Sha);
        Assert.True(receipt.Available);

        Observe(collector, "removed-during-capture");
        collector.Register(Region.Cn, Bed.Build, Template, candidate, 1);
        collector.Unregister(code.Sha);
        Assert.Equal("CANDIDATE_NOT_OBSERVED", collector.Export(Region.Cn, Bed.Build, code.Sha).Reason);
        collector.Register(Region.Cn, Bed.Build, Template, candidate, 0);
        Assert.Equal(receipt, collector.Export(Region.Cn, Bed.Build, code.Sha));
        Feed(collector, "removed-during-capture");
        collector.Complete("removed-during-capture", CaptureEndReason.UserStop);
        Assert.Equal(receipt, collector.Export(Region.Cn, Bed.Build, code.Sha));

        CompleteCapture(collector, "fresh-capture");
        var json = JsonNode.Parse(collector.Export(Region.Cn, Bed.Build, code.Sha).EvidenceJson!)!;
        Assert.Equal(2, json["sessions"]!.AsArray().Count);
        Assert.Equal(0, json["target"]!["quarantine_epoch"]!.GetValue<int>());
    }

    [Fact]
    public void PreviewNeverCompletesASessionAndTwoEndsKeepTwoDistinctAliasesAcrossRestart()
    {
        using var bed = new Bed();
        var root = Path.Combine(bed.Root, "public-evidence");
        var collector = Collector(root);
        var code = Register(collector, bed);
        Observe(collector, "private-session-1");
        Assert.False(collector.Export(Region.Cn, Bed.Build, code.Sha).Available);
        collector.Complete("private-session-1", CaptureEndReason.UserStop);
        var first = collector.Export(Region.Cn, Bed.Build, code.Sha);
        Assert.True(first.Available);
        Assert.Equal(first, collector.Export(Region.Cn, Bed.Build, code.Sha));

        collector = Collector(root);
        Register(collector, bed);
        Assert.Equal(first, collector.Export(Region.Cn, Bed.Build, code.Sha));
        CompleteCapture(collector, "private-session-2", CaptureEndReason.ProcessExit);
        var json = JsonNode.Parse(collector.Export(Region.Cn, Bed.Build, code.Sha).EvidenceJson!)!;
        Assert.Equal(2, json["sessions"]!.AsArray().Count);
        Assert.Equal(2, json["sessions"]!.AsArray().Select(item => item!["session_alias"]!.ToString()).Distinct().Count());
        Assert.DoesNotContain("private-session", json.ToJsonString());
        Assert.DoesNotContain("ConnectionTag", json.ToJsonString());
        Assert.Equal(new[] { "evidence_id", "format", "kind", "observations", "producer", "schema_version", "sessions", "target" },
            json.AsObject().Select(property => property.Key).OrderBy(key => key, StringComparer.Ordinal));
        Assert.All(json["sessions"]!.AsArray(), item =>
            Assert.Equal(new[] { "checks", "complete", "health", "session_alias" },
                item!.AsObject().Select(property => property.Key).OrderBy(key => key, StringComparer.Ordinal)));
        if (Environment.GetEnvironmentVariable("MENTORRECORDER_TEST_EVIDENCE_FIXTURE") is { } fixture)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(fixture))!);
            File.WriteAllText(fixture, json.ToJsonString(new() { WriteIndented = true }));
            File.WriteAllText(Path.ChangeExtension(fixture, "one-session.json"), first.EvidenceJson);
        }
    }

    [Theory]
    [InlineData("missing-health")]
    [InlineData("unknown-connections")]
    [InlineData("dropped")]
    [InlineData("damaged")]
    [InlineData("error")]
    [InlineData("unknown-end")]
    public void UnknownLossAndUnfinishedCapturesNeverBecomeHealthyEvidence(string kind)
    {
        using var bed = new Bed();
        var collector = Collector();
        var code = Register(collector, bed);
        collector.Start("private-session");
        if (kind != "missing-health") collector.Health(new CaptureSessionHealth("private-session", CaptureSilentReason.None,
            kind == "unknown-connections" ? null : 0, kind == "dropped" ? 1 : 0,
            DamagedGameDirections: kind == "damaged" ? 1 : 0));
        Feed(collector, "private-session");
        collector.Complete("private-session", kind == "error" ? CaptureEndReason.Error :
            kind == "unknown-end" ? CaptureEndReason.Unknown : CaptureEndReason.UserStop);
        Assert.False(collector.Export(Region.Cn, Bed.Build, code.Sha).Available);
    }

    [Fact]
    public void RegistrationAfterTrafficCannotReuseAnEarlierPositiveResult()
    {
        using var bed = new Bed();
        var collector = Collector();
        Observe(collector, "private-session");
        var code = Register(collector, bed);
        collector.Complete("private-session", CaptureEndReason.UserStop);
        Assert.False(collector.Export(Region.Cn, Bed.Build, code.Sha).Available);
    }

    [Fact]
    public void OneHealthyContradictingSessionIsRepresentedOnceRatherThanAsAnAggregatePass()
    {
        using var bed = new Bed();
        var collector = Collector();
        var original = bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        var wrong = Bed.Encode(original.Payload with { ZoneOpcode = 65500 });
        Register(collector, wrong);
        CompleteCapture(collector, "one");
        var json = JsonNode.Parse(collector.Export(Region.Cn, Bed.Build, wrong.Sha).EvidenceJson!)!;
        var session = Assert.Single(json["sessions"]!.AsArray());
        Assert.Equal("CONTRADICT", session!["checks"]!.AsArray().Single(item => item!["field"]!.ToString() == "zone")!["verdict"]!.ToString());
    }

    /// <summary>
    /// Audit 2026-10-20, N1-1: the repository takes a session alias as one account's single use, whatever code it came
    /// with. Two codes watched in the same capture session therefore each get an alias of their own, or the second code's
    /// evidence would be refused every time it is submitted.
    /// </summary>
    [Fact]
    public void TwoCodesWatchedInOneCaptureSessionEachGetTheirOwnAlias()
    {
        using var bed = new Bed();
        var collector = Collector();
        var original = bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        var other = Bed.Encode(original.Payload with { ZoneOpcode = 65500 });
        foreach (var code in new[] { original, other })
        {
            Register(collector, code);
        }

        CompleteCapture(collector, "one");

        string Alias(SharedCode code) => Assert.Single(JsonNode.Parse(
            collector.Export(Region.Cn, Bed.Build, code.Sha).EvidenceJson!)!["sessions"]!.AsArray())!["session_alias"]!.ToString();
        Assert.NotEqual(Alias(original), Alias(other));
    }

    [Fact]
    public void NewEpochRequiresPostEpochObservationAndDoesNotRelabelEarlierSessions()
    {
        using var bed = new Bed();
        var collector = Collector();
        var code = Register(collector, bed);
        CompleteCapture(collector, "before-isolation");
        Assert.True(collector.Export(Region.Cn, Bed.Build, code.Sha).Available);
        Register(collector, code, epoch: 1);
        Assert.False(collector.Export(Region.Cn, Bed.Build, code.Sha).Available);
        CompleteCapture(collector, "epoch-one");
        var json = JsonNode.Parse(collector.Export(Region.Cn, Bed.Build, code.Sha).EvidenceJson!)!;
        Assert.Equal(1, json["target"]!["quarantine_epoch"]!.GetValue<int>());
        Assert.Single(json["sessions"]!.AsArray());
        Assert.False(collector.Export(Region.Global, Bed.Build, code.Sha).Available);
        Assert.False(collector.Export(Region.Cn, Bed.OtherBuild, code.Sha).Available);
    }

    [Fact]
    public async Task PipelineKeepsObservingAfterBindingAndFinalLossCannotPublishAHealthySession()
    {
        using var bed = new Bed();
        var code = bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        bed.Publish(code);
        var pipeline = await bed.ReadyPipelineAsync();
        var session = bed.Start(pipeline);
        await Bed.FeedAndSettleAsync(pipeline, session, Bed.Evening());
        Assert.Equal(ProfileStatus.Verified, pipeline.Current.Status);
        Assert.False(pipeline.GetCalibrationEvidence(code.Sha).Available);
        // This reading arrives after the profile has completed its local watch.
        pipeline.OnCaptureHealth(Bed.Healthy(session) with { AdapterDropped = 1 });
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
        Assert.False(pipeline.GetCalibrationEvidence(code.Sha).Available);

        var second = bed.Start(pipeline);
        Bed.Feed(pipeline, second, Bed.Evening(), hour: 1);
        pipeline.OnCaptureStopped(second, CaptureEndReason.UserStop);
        var json = JsonNode.Parse(pipeline.GetCalibrationEvidence(code.Sha).EvidenceJson!)!;
        Assert.Single(json["sessions"]!.AsArray());
        Assert.Equal(code.Sha, json["target"]!["code_sha256"]!.ToString());
        Assert.Equal(new[] { "PASS", "PASS", "PASS", "PASS" },
            json["sessions"]![0]!["checks"]!.AsArray().Select(check => check!["verdict"]!.ToString()));
    }

    [Fact]
    public void EpochRefreshDuringCaptureWaitsForEntirelyNewSessions()
    {
        using var bed = new Bed();
        var collector = Collector();
        var code = Register(collector, bed);
        collector.Start("request-before-epoch");
        collector.Health(Bed.Healthy("request-before-epoch"));
        foreach (var message in CalibrationTrafficCases.Traffic(CalibrationTrafficCases.ReplyState)
                     .Where(message => message.Mono < TimeSpan.FromSeconds(100)).OrderBy(message => message.Mono))
            collector.Accept(message with { CaptureSessionId = "request-before-epoch" });
        Register(collector, code, epoch: 1);
        foreach (var message in CalibrationTrafficCases.Traffic(CalibrationTrafficCases.ReplyState)
                     .Where(message => message.Mono >= TimeSpan.FromSeconds(100)).OrderBy(message => message.Mono))
            collector.Accept(message with { CaptureSessionId = "request-before-epoch" });
        collector.Complete("request-before-epoch", CaptureEndReason.UserStop);
        Assert.False(collector.Export(Region.Cn, Bed.Build, code.Sha).Available);
        foreach (var session in new[] { "new-epoch-1", "new-epoch-2" })
        {
            CompleteCapture(collector, session);
        }
        var json = JsonNode.Parse(collector.Export(Region.Cn, Bed.Build, code.Sha).EvidenceJson!)!;
        Assert.Equal(2, json["sessions"]!.AsArray().Count);
        Assert.All(json["sessions"]!.AsArray(), session => Assert.All(session!["checks"]!.AsArray(),
            check => Assert.Equal("PASS", check!["verdict"]!.ToString())));
    }

    [Fact]
    public void NewTemplateAndMultipleCandidatesNeverReuseAnotherTargetsSessions()
    {
        using var bed = new Bed();
        var collector = Collector();
        var code = Register(collector, bed);
        CompleteCapture(collector, "old-template");
        Assert.True(collector.Export(Region.Cn, Bed.Build, code.Sha).Available);
        var changed = CalibrationTemplate.From(Template.Source with { ProfileSha256 = new string('1', 64) })!;
        var payload = code.Payload with { TemplateSha256 = changed.Source.ProfileSha256 };
        var candidate = SharedCandidateVerifier.Candidate(payload, changed)!;
        collector.Register(Region.Cn, Bed.Build, changed, candidate, 0);
        Assert.False(collector.Export(Region.Cn, Bed.Build, code.Sha).Available);
        Assert.False(collector.Export(Region.Cn, Bed.Build, candidate.CandidateId).Available);
        var second = SharedCandidateVerifier.Candidate(payload with { ZoneOpcode = 65500 }, changed)!;
        collector.Register(Region.Cn, Bed.Build, changed, second, 0);
        Assert.Equal("SELECT_CANDIDATE", collector.Export(Region.Cn, Bed.Build, null).Reason);
    }

    [Fact]
    public void RegionMismatchAndNegativeEpochCannotReplaceTheActiveEvidenceScope()
    {
        using var bed = new Bed();
        var collector = Collector();
        var code = Register(collector, bed);
        var candidate = SharedCandidateVerifier.Candidate(code.Payload, Template)!;
        collector.Register(Region.Global, Bed.Build, Template, candidate, 0);
        collector.Register(Region.Cn, Bed.Build, Template, candidate, -1);
        CompleteCapture(collector, "valid-scope");
        var json = JsonNode.Parse(collector.Export(Region.Cn, Bed.Build, code.Sha).EvidenceJson!)!;
        Assert.Equal("CN", json["target"]!["region"]!.ToString());
        Assert.Equal(0, json["target"]!["quarantine_epoch"]!.GetValue<int>());
    }

    [Fact]
    public void ObservationOverflowAndDuplicateLifecycleCannotCreateAdditionalHealthySessions()
    {
        using var bed = new Bed();
        var collector = Collector();
        var code = Register(collector, bed);
        collector.Start("one-complete-session");
        collector.Health(Bed.Healthy("one-complete-session"));
        Feed(collector, "one-complete-session");
        collector.Start("one-complete-session"); // Duplicate notification preserves the original identity and health.
        collector.Complete("one-complete-session", CaptureEndReason.UserStop);
        var first = collector.Export(Region.Cn, Bed.Build, code.Sha);
        Assert.True(first.Available);
        CompleteCapture(collector, "one-complete-session");
        Assert.Equal(first, collector.Export(Region.Cn, Bed.Build, code.Sha));

        Observe(collector, "overflow-session");
        var source = CalibrationTrafficCases.Traffic(CalibrationTrafficCases.ReplyState).First();
        for (var index = 0; index <= CalibrationObserver.MaxOpcodeCounts; index++)
            collector.Accept(source with { CaptureSessionId = "overflow-session", Opcode = (ushort)index,
                Payload = new byte[1], Mono = TimeSpan.FromSeconds(1000 + index),
                ObservedAtUtc = source.ObservedAtUtc + TimeSpan.FromSeconds(1000 + index) });
        collector.Complete("overflow-session", CaptureEndReason.UserStop);
        Assert.Equal(first, collector.Export(Region.Cn, Bed.Build, code.Sha));
    }
}
