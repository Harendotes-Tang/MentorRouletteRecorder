using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Profiles;
using static MentorRecorder.Collector.UnitTests.CalibrationObserverTests;

namespace MentorRecorder.Collector.UnitTests;

public sealed class CalibrationObserverSafetyTests
{
    private const ushort Reply = 0xC002;
    private static readonly ProfileField ContentField = new("content_id", 20, ProfileFieldType.U16, 0, ProfileEndian.Little,
        new ProfileFieldConstraints(1, 60_000, null), ProfileFieldRole.Value);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void DiagnosticsCountMatchingShapesBeforeTheDraftHasEnoughBursts(int count)
    {
        var traffic = Enumerable.Range(0, count).SelectMany(index => Cluster(5_000 + index * 30_000, 5000));
        var (snapshot, draft) = Observe(traffic);

        Assert.Equal(1, CalibrationEvidenceSummary.From(snapshot, Template()).ZoneCandidates);
        Assert.NotEqual(CalibrationDraftStatus.Ready, draft.Status);
        Assert.Empty(draft.Messages);
    }

    [Fact]
    public void AFullPopEvidenceTableBlocksInsteadOfHidingLostStates()
    {
        var traffic = QueueAndPop(1_000, 1, 100_000).Take(2)
            .Concat(Enumerable.Range(0, 300).Select(index => Message(
                MessageDirection.Inbound, Reply,
                Bytes(40, (9, (byte)(index % 256)), (16, (byte)(1 + index / 256))), 3_000 + index)));
        var (snapshot, draft) = Observe(traffic);

        Assert.Equal(CalibrationObserver.MaxPops, snapshot.Pops.Count);
        Assert.Equal(301 - CalibrationObserver.MaxPops, snapshot.OverflowCount);
        Assert.Equal(CalibrationDraftStatus.Blocked, draft.Status);
        Assert.Empty(draft.Messages);
    }

    [Theory]
    [InlineData(12, "zone")]
    [InlineData(1, "other-zone")]
    public void AnotherRouletteOrConnectionCannotEvictTheActualPop(byte roulette, string connection)
    {
        var earlier = Enumerable.Range(0, CalibrationObserver.MaxPopsPerBucket)
            .Select(index => Message(MessageDirection.Inbound, Reply,
                Bytes(40, (9, 3), (16, roulette)), 100_000 + index, connection));
        var (snapshot, _) = Observe(Session1().Concat(earlier));

        Assert.Contains(snapshot.Pops, pop => pop.RouletteId == 1 && pop.TMs == 120_000);
        Assert.Equal(0, snapshot.OverflowCount);
    }

    [Fact]
    public void DiscardingTheOnlyPopOutsideALoadInvalidatesTheEvidence()
    {
        // The login burst fills the state-3 bucket. The later genuine match shares that
        // bucket key, but its timestamp makes it separate evidence.
        var duringLogin = Enumerable.Range(0, CalibrationObserver.MaxPopsPerBucket)
            .Select(index => Message(MessageDirection.Inbound, Reply,
                Bytes(40, (9, 3), (16, 1)), 5_500 + index));
        var (snapshot, draft) = Observe(Session1().Concat(duringLogin));

        Assert.Equal(1, snapshot.OverflowCount);
        Assert.DoesNotContain(snapshot.Pops, pop => pop.TMs == 120_000);
        Assert.Equal(CalibrationDraftStatus.Blocked, draft.Status);
        Assert.Empty(draft.Messages);
    }

    [Fact]
    public void RepeatedKnownReplyStatesStayBoundedAndAccountForCompression()
    {
        var chatter = Enumerable.Range(0, 2_000)
            .Select(index => Message(MessageDirection.Inbound, Reply,
                Bytes(40, (9, 5), (16, 1)), 130_000 + index * 10));
        var (snapshot, draft) = Observe(Session1().Concat(chatter));

        Assert.Equal(CalibrationObserver.MaxPopsPerBucket,
            snapshot.Pops.Count(pop => !pop.WithinEcho && pop.Selectors.Single().Value == 5));
        Assert.Equal(2_000 - CalibrationObserver.MaxPopsPerBucket, snapshot.DiagnosticsOverflow);
        Assert.Equal(0, snapshot.OverflowCount);
        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
    }

    [Fact]
    public void TerritoryEvidenceOverflowBlocksInsteadOfHidingAnAmbiguousDutyEntry()
    {
        var successful = Session1().ToArray();
        var entryTerritory = successful.Single(message =>
            message.Mono == TimeSpan.FromMilliseconds(125_000));
        var repeated = Enumerable.Range(1, CalibrationObserver.MaxClusterKeys - 1)
            .Select(index => Message(MessageDirection.Inbound, entryTerritory.Opcode,
                entryTerritory.Payload.ToArray(), 125_000 + index));
        var differentKnownDuty = Message(MessageDirection.Inbound, entryTerritory.Opcode,
            Bytes(136, (2, (byte)(1036 & 0xff)), (3, (byte)(1036 >> 8))),
            125_000 + CalibrationObserver.MaxClusterKeys);
        var (snapshot, draft) = Observe(successful.Concat(repeated).Append(differentKnownDuty));

        Assert.Equal(1, snapshot.OverflowCount);
        Assert.Contains(snapshot.Clusters, cluster => cluster.OverflowCount == 1);
        Assert.Equal(CalibrationDraftStatus.Blocked, draft.Status);
        Assert.Empty(draft.Messages);
    }

    [Fact]
    public void JobEvidenceOverflowBlocksInsteadOfHidingAChangedValidJob()
    {
        var successful = Session1().ToArray();
        var entryJob = successful.Single(message =>
            message.Mono == TimeSpan.FromMilliseconds(125_450));
        var repeated = Enumerable.Range(0, CalibrationObserver.MaxClusterKeys - 2)
            .Select(index => Message(MessageDirection.Inbound, entryJob.Opcode,
                entryJob.Payload.ToArray(), 125_461 + index));
        var differentValidJob = Message(MessageDirection.Inbound, entryJob.Opcode,
            Bytes(16, (0, 22)), 125_461 + CalibrationObserver.MaxClusterKeys - 2);
        var (snapshot, draft) = Observe(successful.Concat(repeated).Append(differentValidJob));

        Assert.Equal(1, snapshot.OverflowCount);
        Assert.Contains(snapshot.Clusters, cluster => cluster.OverflowCount == 1);
        Assert.Equal(CalibrationDraftStatus.Blocked, draft.Status);
        Assert.Empty(draft.Messages);
    }

    [Fact]
    public void AReplyBeyondTheDiagnosticLimitCannotHideACompetingMatchedState()
    {
        var traffic = FillReplyDiagnostics().Concat(Session1()).Append(
            Message(MessageDirection.Inbound, Reply, Bytes(40, (9, 7), (16, 1)), 100_000));
        var (snapshot, draft) = Observe(traffic);

        Assert.Equal(CalibrationObserver.MaxReplyOpcodes + 1, snapshot.Pairs.Count);
        Assert.Equal(0, snapshot.OverflowCount);
        Assert.Equal(CalibrationDraftStatus.Blocked, draft.Status);
        Assert.Empty(draft.Messages);
        Assert.Contains(snapshot.Pops, pop => pop.Opcode == Reply && pop.WithinEcho &&
            pop.Selectors.Single().Value == 5);
        Assert.Contains(snapshot.Pops, pop => pop.Opcode == Reply && !pop.WithinEcho &&
            pop.Selectors.Single().Value == 7);
    }

    [Fact]
    public void AReplyBeyondTheDiagnosticLimitCanStillLearnAUniqueChangedMatchedState()
    {
        var (snapshot, draft) = Observe(FillReplyDiagnostics().Concat(Session1(popState: 7)));

        Assert.Equal(CalibrationObserver.MaxReplyOpcodes + 1, snapshot.Pairs.Count);
        Assert.Equal(0, snapshot.OverflowCount);
        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        var pop = Assert.Single(draft.Messages, message => message.Name == "CONTENT_FINDER_POP");
        Assert.Equal(Reply, pop.Opcode);
        Assert.Equal(new long[] { 7 }, pop.Field("finder_state")!.Constraints.In);
        Assert.DoesNotContain(snapshot.FinderSelectors.Keys, key => key.Opcode == Reply);
        Assert.True(snapshot.FinderSelectors.Keys.Select(key => key.Opcode).Distinct().Count() <=
            CalibrationObserver.MaxReplyOpcodes);
    }

    [Fact]
    public void ValueRoleFieldsNeverBecomeSelectorEvidenceOrLearnedConstraints()
    {
        var template = TemplateWithContentField();
        var traffic = Session1().Select(message =>
        {
            if (message.Opcode != Reply)
            {
                return message;
            }

            var payload = message.Payload.ToArray();
            payload[20] = message.Mono.TotalMilliseconds < 100_000 ? (byte)17 : (byte)29;
            payload[21] = 4;
            return message with { Payload = payload };
        });
        var (snapshot, draft) = Observe(traffic, template);
        var evidence = CalibrationEvidenceSummary.From(snapshot, template);

        Assert.All(snapshot.Pops, pop => Assert.All(pop.Selectors,
            reading => Assert.Equal("finder_state", reading.Field)));
        Assert.DoesNotContain(snapshot.FinderSelectors.Keys, key => key.Field == "content_id");
        Assert.DoesNotContain(evidence.FinderStates, state => state.Contains("content_id", StringComparison.Ordinal));
        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.Equal(ContentField, draft.Messages.Single(message => message.Name == "CONTENT_FINDER_POP").Field("content_id"));
    }

    [Fact]
    public void AFailedValueConstraintCannotBecomeAPopOnAPairedOpcode()
    {
        // The default fixture leaves content_id at zero, below the template's minimum.
        // Both the echo's old selector value and the match's accepted selector value must
        // still obey that value constraint once their opcode has been paired.
        var (snapshot, draft) = Observe(Session1(), TemplateWithContentField());

        Assert.Single(snapshot.Pairs);
        Assert.DoesNotContain(snapshot.Pops, pop => pop.Opcode == Reply);
        Assert.Contains(snapshot.PopRefusals, entry => entry.Key.Opcode == Reply && entry.Key.Field == "content_id");
        Assert.NotEqual(CalibrationDraftStatus.Ready, draft.Status);
        Assert.Empty(draft.Messages);
    }

    [Theory]
    [InlineData(4, 128)]
    [InlineData(CalibrationObserver.MaxClusters, CalibrationObserver.MaxClusterKeys)]
    public void OnceOnlySummaryPreservesCountsAndOrderAtBoundedTableSizes(int clusterCount, int membersPerCluster)
    {
        var clusters = new List<ZoneCluster>();
        var outside = new Dictionary<MessageKey, int>();
        for (var clusterIndex = 0; clusterIndex < clusterCount; clusterIndex++)
        {
            var members = new Dictionary<MessageKey, int>();
            for (var index = 0; index < membersPerCluster; index++)
            {
                // Shared shapes exercise exact-once versus repeated occurrences and ties;
                // distinct shapes also reach the observer's largest total member population.
                var opcode = index < 16 ? index : clusterIndex * membersPerCluster + index;
                var key = new MessageKey(index % 11 == 0 ? PacketDirection.ClientToServer : PacketDirection.ServerToClient,
                    (ushort)opcode, 400 + index % 3);
                members.Add(key, (index + clusterIndex) % 7 == 0 ? 2 : 1);
                if (index % 79 == 0)
                {
                    outside[key] = 1;
                }
            }

            clusters.Add(new ZoneCluster(clusterIndex, "zone", clusterIndex * 30_000, clusterIndex * 30_000 + 500,
                DateTimeOffset.UnixEpoch.AddSeconds(clusterIndex * 30),
                DateTimeOffset.UnixEpoch.AddSeconds(clusterIndex * 30 + 1), members,
                Array.Empty<TerritoryHit>(), new Dictionary<ushort, IReadOnlyList<long>>(), 0));
        }

        var snapshot = new CalibrationSnapshot("summary", Array.Empty<FinderPairHit>(), Array.Empty<PopHit>(),
            clusters, outside, new Dictionary<(PacketDirection, ushort), int>(), new Dictionary<ushort, int>(), 0, 0);
        var expected = clusters.SelectMany(cluster => cluster.Members.Keys).Distinct()
            .Where(key => key.Direction == PacketDirection.ServerToClient && !outside.ContainsKey(key))
            .Select(key => (Key: key, Count: clusters.Count(cluster => cluster.Members.TryGetValue(key, out var count) && count == 1)))
            .Where(entry => entry.Count >= 1)
            .OrderByDescending(entry => entry.Count).ThenBy(entry => entry.Key.Length).Take(8)
            .Select(entry => $"0x{entry.Key.Opcode:x4}:{entry.Key.Length}@{entry.Count}").ToArray();

        Assert.Equal(expected, CalibrationEvidenceSummary.From(snapshot, Template()).ZoneOnceOnly);
    }

    private static IEnumerable<DecodedMessage> FillReplyDiagnostics()
    {
        // Each earlier opcode only answers one request. None has a later matched state,
        // so these fill the diagnostic membership without supplying a competing duty chain.
        for (var index = 0; index < CalibrationObserver.MaxReplyOpcodes; index++)
        {
            foreach (var message in QueueAndPop(10_000 + index * 2_000, 1, 100_000).Take(2))
            {
                yield return message.Opcode == Reply
                    ? message with { Opcode = (ushort)(0xB000 + index) }
                    : message;
            }
        }
    }

    private static (CalibrationSnapshot Snapshot, CalibrationDraft Draft) Observe(
        IEnumerable<DecodedMessage> traffic, CalibrationTemplate? template = null)
    {
        template ??= Template();
        var observer = new CalibrationObserver(template, Region.Cn, "calibration-session");
        foreach (var message in traffic.OrderBy(message => message.Mono))
        {
            observer.Accept(message);
        }

        observer.Flush();
        var snapshot = observer.Snapshot();
        return (snapshot, CalibrationDraft.Derive(snapshot, template));
    }

    private static CalibrationTemplate TemplateWithContentField()
    {
        var source = Template().Source;
        return CalibrationTemplate.From(source with
        {
            Messages = source.Messages.Select(message => message.Name == "CONTENT_FINDER_POP"
                ? message with { Fields = message.Fields.Append(ContentField).ToArray() }
                : message).ToArray(),
        })!;
    }
}
