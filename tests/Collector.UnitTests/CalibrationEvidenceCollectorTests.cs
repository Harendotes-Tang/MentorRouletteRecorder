using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Sharing;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.UnitTests;

public sealed class CalibrationEvidenceCollectorTests
{
    private static CalibrationTemplate Template => SharedCalibrationTestBed.Template;

    private static CalibrationEvidenceCollector Collector(string? root = null) =>
        new(new Protocol.Sharing.CalibrationEvidenceReceiptStore(root));

    private static SharedCode Register(CalibrationEvidenceCollector collector, SharedCalibrationTestBed bed)
    {
        var code = bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        collector.Register(Region.Cn, SharedCalibrationTestBed.Build, Template,
            SharedCandidateVerifier.Candidate(code.Payload, Template)!, 0);
        return code;
    }

    private static void Feed(CalibrationEvidenceCollector collector, string session, string name = CalibrationTrafficCases.ReplyState)
    {
        foreach (var message in CalibrationTrafficCases.Traffic(name).OrderBy(message => message.Mono))
            collector.Accept(message with { CaptureSessionId = session });
    }

    [Fact]
    public async Task RejectedCandidatesReleasePublicObservationSlotsAcrossRediscovery()
    {
        using var bed = new SharedCalibrationTestBed();
        var source = bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        var pipeline = bed.Pipeline(bed.Services(fetch: false));
        pipeline.Refresh(SharedCalibrationTestBed.Game());
        SharedCode? last = null;
        for (var index = 0; index <= CalibrationObserver.MaxCandidates; index++)
        {
            last = SharedCalibrationTestBed.Encode(source.Payload with { ZoneOpcode = (ushort)(60000 + index) });
            Assert.Equal(SharedImportOutcome.Applied, pipeline.ImportCalibrationCode(last.Code).Outcome);
            if (index == CalibrationObserver.MaxCandidates) break;
            Assert.Equal(1, pipeline.RejectSharedCalibration().DroppedCandidates);
            pipeline.DiscardCalibration();
            await SharedCalibrationTestBed.Idle(pipeline);
        }

        var session = bed.Start(pipeline);
        SharedCalibrationTestBed.Feed(pipeline, session, SharedCalibrationTestBed.Evening());
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);

        Assert.True(pipeline.GetCalibrationEvidence(last!.Sha).Available);
    }

    [Fact]
    public void ReplacingSelectedLocalProfilesReleasesPublicObservationSlots()
    {
        using var bed = new SharedCalibrationTestBed();
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
            var written = LocalProfileWriter.Write(changed, Template, SharedCalibrationTestBed.Build,
                SharedCalibrationTestBed.Confirmed, bed.LocalRoot);
            code = SharedProfileBuilder.ToShareCode(ProfileLoader.Load(written.Path), Template).CodeSha256;
            Assert.Equal(ProfileOrigin.Local, pipeline.Refresh(SharedCalibrationTestBed.Game()).Origin);
        }

        var session = bed.Start(pipeline);
        SharedCalibrationTestBed.Feed(pipeline, session, SharedCalibrationTestBed.Evening());
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);

        Assert.True(pipeline.GetCalibrationEvidence(code).Available);
    }

    [Fact]
    public void DisarmingCalibrationKeepsTheSameSelectedProfilesPublicObservation()
    {
        using var bed = new SharedCalibrationTestBed();
        var written = LocalProfileWriter.Write(CalibrationTrafficCases.Derive(CalibrationTrafficCases.QueueRequest),
            Template, SharedCalibrationTestBed.Build, SharedCalibrationTestBed.Confirmed, bed.LocalRoot);
        var code = SharedProfileBuilder.ToShareCode(ProfileLoader.Load(written.Path), Template).CodeSha256;
        var pipeline = bed.Pipeline(bed.Services(fetch: false));
        Assert.Equal(ProfileOrigin.Local, pipeline.Refresh(SharedCalibrationTestBed.Game()).Origin);
        Assert.True(pipeline.CalibrationArmed);
        var session = bed.Start(pipeline);
        SharedCalibrationTestBed.Feed(pipeline, session, CalibrationTrafficCases.Traffic(CalibrationTrafficCases.QueueRequest));

        pipeline.ApplyCalibrationSetting(false);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);

        Assert.True(pipeline.GetCalibrationEvidence(code).Available);
    }

    [Fact]
    public async Task BindingCannotRelabelPublicEvidenceWithTheOldEpochWhenStateWriteFails()
    {
        using var bed = new SharedCalibrationTestBed();
        var code = bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        bed.Publish(code);
        var pipeline = bed.Pipeline(bed.Services());
        pipeline.Refresh(SharedCalibrationTestBed.Game());
        await SharedCalibrationTestBed.Idle(pipeline);
        using var stateLock = new FileStream(Path.Combine(SharedCalibrationStore.DirectoryFor(bed.StoreRoot,
            Region.Cn, SharedCalibrationTestBed.Build), SharedCalibrationStore.StateFileName),
            FileMode.Open, FileAccess.Read, FileShare.Read);
        bed.PublishListed(new SharedCalibrationTestBed.Listing(code, Quarantined: true, QuarantineEpoch: 1));
        bed.Transport.BeforeAnswer = (uri, _) => uri == SharedCalibrationClient.IndexUri(SharedCalibrationSource.GithubRaw)
            ? Task.CompletedTask : Task.FromException(new IOException("code file unavailable"));
        Assert.Equal(SharedCheckOutcome.Started, pipeline.CheckSharedCalibrationNow());
        await SharedCalibrationTestBed.Idle(pipeline);
        Assert.Equal(0, Assert.Single(bed.Store.LoadCandidates(Region.Cn, SharedCalibrationTestBed.Build,
            SharedCalibrationTestBed.TemplateSha)).QuarantineEpoch);

        for (var hour = 0; hour < 3; hour++)
        {
            var session = bed.Start(pipeline);
            SharedCalibrationTestBed.Feed(pipeline, session, SharedCalibrationTestBed.Evening(), hour);
            await SharedCalibrationTestBed.Idle(pipeline);
            pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
            await SharedCalibrationTestBed.Idle(pipeline);
        }

        Assert.Equal(ProfileStatus.Verified, pipeline.Current.Status);
        var evidence = pipeline.GetCalibrationEvidence(code.Sha);
        Assert.True(evidence.Available);
        Assert.Equal(1, JsonNode.Parse(evidence.EvidenceJson!)!["target"]!["quarantine_epoch"]!.GetValue<int>());
    }

    [Fact]
    public void ReleasingACandidateKeepsReceiptsButRequiresANewSessionForReRegistration()
    {
        using var bed = new SharedCalibrationTestBed();
        var collector = Collector(Path.Combine(bed.Root, "public-evidence"));
        var code = Register(collector, bed);
        var candidate = SharedCandidateVerifier.Candidate(code.Payload, Template)!;
        collector.Start("completed");
        collector.Health(SharedCalibrationTestBed.Healthy("completed"));
        Feed(collector, "completed");
        collector.Complete("completed", CaptureEndReason.UserStop);
        var receipt = collector.Export(Region.Cn, SharedCalibrationTestBed.Build, code.Sha);
        Assert.True(receipt.Available);

        collector.Start("removed-during-capture");
        collector.Health(SharedCalibrationTestBed.Healthy("removed-during-capture"));
        Feed(collector, "removed-during-capture");
        collector.Register(Region.Cn, SharedCalibrationTestBed.Build, Template, candidate, 1);
        collector.Unregister(code.Sha);
        Assert.Equal("CANDIDATE_NOT_OBSERVED", collector.Export(Region.Cn, SharedCalibrationTestBed.Build, code.Sha).Reason);
        collector.Register(Region.Cn, SharedCalibrationTestBed.Build, Template, candidate, 0);
        Assert.Equal(receipt, collector.Export(Region.Cn, SharedCalibrationTestBed.Build, code.Sha));
        Feed(collector, "removed-during-capture");
        collector.Complete("removed-during-capture", CaptureEndReason.UserStop);
        Assert.Equal(receipt, collector.Export(Region.Cn, SharedCalibrationTestBed.Build, code.Sha));

        collector.Start("fresh-capture");
        collector.Health(SharedCalibrationTestBed.Healthy("fresh-capture"));
        Feed(collector, "fresh-capture");
        collector.Complete("fresh-capture", CaptureEndReason.UserStop);
        var json = JsonNode.Parse(collector.Export(Region.Cn, SharedCalibrationTestBed.Build, code.Sha).EvidenceJson!)!;
        Assert.Equal(2, json["sessions"]!.AsArray().Count);
        Assert.Equal(0, json["target"]!["quarantine_epoch"]!.GetValue<int>());
    }

    [Fact]
    public void PreviewNeverCompletesASessionAndTwoEndsKeepTwoDistinctAliasesAcrossRestart()
    {
        using var bed = new SharedCalibrationTestBed();
        var root = Path.Combine(bed.Root, "public-evidence");
        var collector = Collector(root);
        var code = Register(collector, bed);
        collector.Start("private-session-1");
        collector.Health(SharedCalibrationTestBed.Healthy("private-session-1"));
        Feed(collector, "private-session-1");
        Assert.False(collector.Export(Region.Cn, SharedCalibrationTestBed.Build, code.Sha).Available);
        collector.Complete("private-session-1", CaptureEndReason.UserStop);
        var first = collector.Export(Region.Cn, SharedCalibrationTestBed.Build, code.Sha);
        Assert.True(first.Available);
        Assert.Equal(first, collector.Export(Region.Cn, SharedCalibrationTestBed.Build, code.Sha));

        collector = Collector(root);
        Register(collector, bed);
        Assert.Equal(first, collector.Export(Region.Cn, SharedCalibrationTestBed.Build, code.Sha));
        collector.Start("private-session-2");
        collector.Health(SharedCalibrationTestBed.Healthy("private-session-2"));
        Feed(collector, "private-session-2");
        collector.Complete("private-session-2", CaptureEndReason.ProcessExit);
        var json = JsonNode.Parse(collector.Export(Region.Cn, SharedCalibrationTestBed.Build, code.Sha).EvidenceJson!)!;
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
        using var bed = new SharedCalibrationTestBed();
        var collector = Collector();
        var code = Register(collector, bed);
        collector.Start("private-session");
        if (kind != "missing-health") collector.Health(new CaptureSessionHealth("private-session", CaptureSilentReason.None,
            kind == "unknown-connections" ? null : 0, kind == "dropped" ? 1 : 0,
            DamagedGameDirections: kind == "damaged" ? 1 : 0));
        Feed(collector, "private-session");
        collector.Complete("private-session", kind == "error" ? CaptureEndReason.Error :
            kind == "unknown-end" ? CaptureEndReason.Unknown : CaptureEndReason.UserStop);
        Assert.False(collector.Export(Region.Cn, SharedCalibrationTestBed.Build, code.Sha).Available);
    }

    [Fact]
    public void RegistrationAfterTrafficCannotReuseAnEarlierPositiveResult()
    {
        using var bed = new SharedCalibrationTestBed();
        var collector = Collector();
        collector.Start("private-session");
        collector.Health(SharedCalibrationTestBed.Healthy("private-session"));
        Feed(collector, "private-session");
        var code = Register(collector, bed);
        collector.Complete("private-session", CaptureEndReason.UserStop);
        Assert.False(collector.Export(Region.Cn, SharedCalibrationTestBed.Build, code.Sha).Available);
    }

    [Fact]
    public void OneHealthyContradictingSessionIsRepresentedOnceRatherThanAsAnAggregatePass()
    {
        using var bed = new SharedCalibrationTestBed();
        var collector = Collector();
        var original = bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        var wrong = SharedCalibrationTestBed.Encode(original.Payload with { ZoneOpcode = 65500 });
        collector.Register(Region.Cn, SharedCalibrationTestBed.Build, Template,
            SharedCandidateVerifier.Candidate(wrong.Payload, Template)!, 0);
        collector.Start("one");
        collector.Health(SharedCalibrationTestBed.Healthy("one"));
        Feed(collector, "one");
        collector.Complete("one", CaptureEndReason.UserStop);
        var json = JsonNode.Parse(collector.Export(Region.Cn, SharedCalibrationTestBed.Build, wrong.Sha).EvidenceJson!)!;
        var session = Assert.Single(json["sessions"]!.AsArray());
        Assert.Equal("CONTRADICT", session!["checks"]!.AsArray().Single(item => item!["field"]!.ToString() == "zone")!["verdict"]!.ToString());
    }

    [Fact]
    public void NewEpochRequiresPostEpochObservationAndDoesNotRelabelEarlierSessions()
    {
        using var bed = new SharedCalibrationTestBed();
        var collector = Collector();
        var code = Register(collector, bed);
        collector.Start("before-isolation");
        collector.Health(SharedCalibrationTestBed.Healthy("before-isolation"));
        Feed(collector, "before-isolation");
        collector.Complete("before-isolation", CaptureEndReason.UserStop);
        Assert.True(collector.Export(Region.Cn, SharedCalibrationTestBed.Build, code.Sha).Available);
        collector.Register(Region.Cn, SharedCalibrationTestBed.Build, Template,
            SharedCandidateVerifier.Candidate(code.Payload, Template)!, 1);
        Assert.False(collector.Export(Region.Cn, SharedCalibrationTestBed.Build, code.Sha).Available);
        collector.Start("epoch-one");
        collector.Health(SharedCalibrationTestBed.Healthy("epoch-one"));
        Feed(collector, "epoch-one");
        collector.Complete("epoch-one", CaptureEndReason.UserStop);
        var json = JsonNode.Parse(collector.Export(Region.Cn, SharedCalibrationTestBed.Build, code.Sha).EvidenceJson!)!;
        Assert.Equal(1, json["target"]!["quarantine_epoch"]!.GetValue<int>());
        Assert.Single(json["sessions"]!.AsArray());
        Assert.False(collector.Export(Region.Global, SharedCalibrationTestBed.Build, code.Sha).Available);
        Assert.False(collector.Export(Region.Cn, SharedCalibrationTestBed.OtherBuild, code.Sha).Available);
    }

    [Fact]
    public async Task PipelineKeepsObservingAfterBindingAndFinalLossCannotPublishAHealthySession()
    {
        using var bed = new SharedCalibrationTestBed();
        var code = bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        bed.Publish(code);
        var pipeline = bed.Pipeline(bed.Services());
        pipeline.Refresh(SharedCalibrationTestBed.Game());
        await SharedCalibrationTestBed.Idle(pipeline);
        var session = bed.Start(pipeline);
        SharedCalibrationTestBed.Feed(pipeline, session, SharedCalibrationTestBed.Evening());
        await SharedCalibrationTestBed.Idle(pipeline);
        Assert.Equal(ProfileStatus.Verified, pipeline.Current.Status);
        Assert.False(pipeline.GetCalibrationEvidence(code.Sha).Available);
        // This reading arrives after the profile has completed its local watch.
        pipeline.OnCaptureHealth(SharedCalibrationTestBed.Healthy(session) with { AdapterDropped = 1 });
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
        Assert.False(pipeline.GetCalibrationEvidence(code.Sha).Available);

        var second = bed.Start(pipeline);
        SharedCalibrationTestBed.Feed(pipeline, second, SharedCalibrationTestBed.Evening(), hour: 1);
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
        using var bed = new SharedCalibrationTestBed();
        var collector = Collector();
        var code = Register(collector, bed);
        collector.Start("request-before-epoch");
        collector.Health(SharedCalibrationTestBed.Healthy("request-before-epoch"));
        foreach (var message in CalibrationTrafficCases.Traffic(CalibrationTrafficCases.ReplyState)
                     .Where(message => message.Mono < TimeSpan.FromSeconds(100)).OrderBy(message => message.Mono))
            collector.Accept(message with { CaptureSessionId = "request-before-epoch" });
        collector.Register(Region.Cn, SharedCalibrationTestBed.Build, Template,
            SharedCandidateVerifier.Candidate(code.Payload, Template)!, 1);
        foreach (var message in CalibrationTrafficCases.Traffic(CalibrationTrafficCases.ReplyState)
                     .Where(message => message.Mono >= TimeSpan.FromSeconds(100)).OrderBy(message => message.Mono))
            collector.Accept(message with { CaptureSessionId = "request-before-epoch" });
        collector.Complete("request-before-epoch", CaptureEndReason.UserStop);
        Assert.False(collector.Export(Region.Cn, SharedCalibrationTestBed.Build, code.Sha).Available);
        foreach (var session in new[] { "new-epoch-1", "new-epoch-2" })
        {
            collector.Start(session);
            collector.Health(SharedCalibrationTestBed.Healthy(session));
            Feed(collector, session);
            collector.Complete(session, CaptureEndReason.UserStop);
        }
        var json = JsonNode.Parse(collector.Export(Region.Cn, SharedCalibrationTestBed.Build, code.Sha).EvidenceJson!)!;
        Assert.Equal(2, json["sessions"]!.AsArray().Count);
        Assert.All(json["sessions"]!.AsArray(), session => Assert.All(session!["checks"]!.AsArray(),
            check => Assert.Equal("PASS", check!["verdict"]!.ToString())));
    }

    [Fact]
    public void NewTemplateAndMultipleCandidatesNeverReuseAnotherTargetsSessions()
    {
        using var bed = new SharedCalibrationTestBed();
        var collector = Collector();
        var code = Register(collector, bed);
        collector.Start("old-template");
        collector.Health(SharedCalibrationTestBed.Healthy("old-template"));
        Feed(collector, "old-template");
        collector.Complete("old-template", CaptureEndReason.UserStop);
        Assert.True(collector.Export(Region.Cn, SharedCalibrationTestBed.Build, code.Sha).Available);
        var changed = CalibrationTemplate.From(Template.Source with { ProfileSha256 = new string('1', 64) })!;
        var payload = code.Payload with { TemplateSha256 = changed.Source.ProfileSha256 };
        var candidate = SharedCandidateVerifier.Candidate(payload, changed)!;
        collector.Register(Region.Cn, SharedCalibrationTestBed.Build, changed, candidate, 0);
        Assert.False(collector.Export(Region.Cn, SharedCalibrationTestBed.Build, code.Sha).Available);
        Assert.False(collector.Export(Region.Cn, SharedCalibrationTestBed.Build, candidate.CandidateId).Available);
        var second = SharedCandidateVerifier.Candidate(payload with { ZoneOpcode = 65500 }, changed)!;
        collector.Register(Region.Cn, SharedCalibrationTestBed.Build, changed, second, 0);
        Assert.Equal("SELECT_CANDIDATE", collector.Export(Region.Cn, SharedCalibrationTestBed.Build, null).Reason);
    }

    [Fact]
    public void RegionMismatchAndNegativeEpochCannotReplaceTheActiveEvidenceScope()
    {
        using var bed = new SharedCalibrationTestBed();
        var collector = Collector();
        var code = Register(collector, bed);
        var candidate = SharedCandidateVerifier.Candidate(code.Payload, Template)!;
        collector.Register(Region.Global, SharedCalibrationTestBed.Build, Template, candidate, 0);
        collector.Register(Region.Cn, SharedCalibrationTestBed.Build, Template, candidate, -1);
        collector.Start("valid-scope");
        collector.Health(SharedCalibrationTestBed.Healthy("valid-scope"));
        Feed(collector, "valid-scope");
        collector.Complete("valid-scope", CaptureEndReason.UserStop);
        var json = JsonNode.Parse(collector.Export(Region.Cn, SharedCalibrationTestBed.Build, code.Sha).EvidenceJson!)!;
        Assert.Equal("CN", json["target"]!["region"]!.ToString());
        Assert.Equal(0, json["target"]!["quarantine_epoch"]!.GetValue<int>());
    }

    [Fact]
    public void ObservationOverflowAndDuplicateLifecycleCannotCreateAdditionalHealthySessions()
    {
        using var bed = new SharedCalibrationTestBed();
        var collector = Collector();
        var code = Register(collector, bed);
        collector.Start("one-complete-session");
        collector.Health(SharedCalibrationTestBed.Healthy("one-complete-session"));
        Feed(collector, "one-complete-session");
        collector.Start("one-complete-session"); // Duplicate notification preserves the original identity and health.
        collector.Complete("one-complete-session", CaptureEndReason.UserStop);
        var first = collector.Export(Region.Cn, SharedCalibrationTestBed.Build, code.Sha);
        Assert.True(first.Available);
        collector.Start("one-complete-session");
        collector.Health(SharedCalibrationTestBed.Healthy("one-complete-session"));
        Feed(collector, "one-complete-session");
        collector.Complete("one-complete-session", CaptureEndReason.UserStop);
        Assert.Equal(first, collector.Export(Region.Cn, SharedCalibrationTestBed.Build, code.Sha));

        collector.Start("overflow-session");
        collector.Health(SharedCalibrationTestBed.Healthy("overflow-session"));
        Feed(collector, "overflow-session");
        var source = CalibrationTrafficCases.Traffic(CalibrationTrafficCases.ReplyState).First();
        for (var index = 0; index <= CalibrationObserver.MaxOpcodeCounts; index++)
            collector.Accept(source with { CaptureSessionId = "overflow-session", Opcode = (ushort)index,
                Payload = new byte[1], Mono = TimeSpan.FromSeconds(1000 + index),
                ObservedAtUtc = source.ObservedAtUtc + TimeSpan.FromSeconds(1000 + index) });
        collector.Complete("overflow-session", CaptureEndReason.UserStop);
        Assert.Equal(first, collector.Export(Region.Cn, SharedCalibrationTestBed.Build, code.Sha));
    }
}
