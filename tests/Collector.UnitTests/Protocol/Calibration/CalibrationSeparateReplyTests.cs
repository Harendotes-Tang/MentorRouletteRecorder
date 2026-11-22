using System.Text.Json;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Domain.StateMachine;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Pipeline;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Calibration with a template whose queue reply and match announcement are two messages
/// (<c>calibration.finder_reply</c>; GLOBAL 2026.11.03, docs/protocol-profile-format.md section 11.3).
/// The traffic is synthetic and shaped like the GLOBAL client the tester's files describe: request C2S
/// 24 bytes with the roulette at byte 0; two replies S2C 40 bytes (byte 0 = 1, job at byte 1, roulette at
/// byte 16, the second with 15 at byte 2); the popup S2C 40 bytes with the roulette as a u16 at offset 2;
/// the zone marker S2C 136 bytes carrying the territory at offset 2. Every opcode is changed, as a patch
/// changes them, and only field values are set - no real payload is in here.
/// </summary>
public sealed class CalibrationSeparateReplyTests : IDisposable
{
    private const string Session = "calibration-session";
    private static readonly DateTimeOffset Start = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    private const ushort Request = 0xE215;
    private const ushort Reply = 0xE16F;
    internal const ushort Pop = 0xE3BF;
    internal const ushort Confirm = 0xE2CB;
    private const ushort Zone = 0xE0AB;
    private const ushort Decoy136 = 0xE27D;
    private const ushort Anchor = 0xB100;
    private const ushort Movement = 0xE22B;
    private const ushort ControlSelf = 0xE0A9;
    private const ushort Stray = 0xE0D0;
    private const ushort RetainerList = 0xE0F5;

    private const int Town = 641;          // a housing district: not in the duty table
    private const int Fractal = 430;       // the Fractal Continuum
    private const int Clyteum = 1345;      // the Clyteum
    private const byte Mentor = 9;
    private const byte Expert = 5;
    private const byte NormalRaids = 15;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "MentorRecorder.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    /// <summary>The GLOBAL 2026.11.03 structure with a two-message calibration section, every opcode the template build's.</summary>
    /// <param name="replyCarriesJob">Whether the reply is also the job message.</param>
    /// <param name="zoneCarriesTerritory">Whether the zone marker declares the territory.</param>
    internal static CalibrationTemplate GlobalTemplate(bool replyCarriesJob = true, bool zoneCarriesTerritory = true)
    {
        var profile = new ProtocolProfile(
            "global.template", Region.Global, "2026.11.03.0000.0000", Start, 9, ProfileCompatibilityStatus.Verified,
            TimeSpan.FromSeconds(120),
            new[]
            {
                new ProfileMessage("CONTENT_FINDER_POP", 0x03bf, PacketDirection.ServerToClient, null, 40, null, null,
                    Array.Empty<long>(), new[]
                    {
                        new ProfileField("roulette_id", 2, ProfileFieldType.U16, 0, ProfileEndian.Little,
                            new ProfileFieldConstraints(0, 255, null)),
                    }),
                new ProfileMessage("ZONE_INITIALIZATION", 0x00ab, PacketDirection.ServerToClient, null, 136, null, null,
                    Array.Empty<long>(), zoneCarriesTerritory
                        ? new[]
                        {
                            new ProfileField("territory_id", 2, ProfileFieldType.U16, 0, ProfileEndian.Little,
                                new ProfileFieldConstraints(1, null, null)),
                        }
                        : Array.Empty<ProfileField>()),
                new ProfileMessage("PLAYER_JOB", 0x016f, PacketDirection.ServerToClient, null, 40, null, null,
                    Array.Empty<long>(), new[]
                    {
                        new ProfileField("finder_state", 0, ProfileFieldType.U8, 0, ProfileEndian.Little,
                            new ProfileFieldConstraints(null, null, new long[] { 1 }), ProfileFieldRole.Selector),
                        new ProfileField("job_id", 1, ProfileFieldType.U8, 0, ProfileEndian.Little,
                            new ProfileFieldConstraints(1, 43, null)),
                    }),
            },
            Array.Empty<ProfileFixtureReference>(), "template", new string('0', 64), "", false)
        {
            Calibration = new ProfileCalibration(
                new CalibrationFinderRequest(PacketDirection.ClientToServer, 24,
                    new ProfileField("roulette_id", 0, ProfileFieldType.U8, 0, ProfileEndian.Little,
                        new ProfileFieldConstraints(1, 255, null))),
                TimeSpan.FromSeconds(1))
            {
                FinderReply = new CalibrationFinderReply(PacketDirection.ServerToClient, 40,
                    new ProfileField("roulette_id", 16, ProfileFieldType.U8, 0, ProfileEndian.Little,
                        new ProfileFieldConstraints(1, 255, null)),
                    replyCarriesJob),
            },
        };
        return CalibrationTemplate.From(profile)!;
    }

    private static DecodedMessage Message(MessageDirection direction, ushort opcode, byte[] payload, long t) =>
        new(Session, direction, Start + TimeSpan.FromMilliseconds(t), TimeSpan.FromMilliseconds(t), 0, 3, opcode, payload,
            "zone");

    private static byte[] Bytes(int length, params (int Offset, byte Value)[] writes) =>
        CalibrationObserverTests.Bytes(length, writes);

    private static DecodedMessage ZoneMessage(long t, int territory) => Message(MessageDirection.Inbound, Zone,
        Bytes(136, (2, (byte)(territory & 0xff)), (3, (byte)(territory >> 8))), t);

    /// <summary>One zone load: the marker carrying the territory, an anchor, five large shapes and an unrelated 136-byte message.</summary>
    internal static IEnumerable<DecodedMessage> Load(long t, int territory)
    {
        yield return ZoneMessage(t, territory);
        yield return Message(MessageDirection.Inbound, Anchor, new byte[3000], t + 100);
        for (var i = 1; i <= 5; i++)
        {
            yield return Message(MessageDirection.Inbound, (ushort)(Anchor + i), new byte[300 + i * 100], t + 100 + i * 50);
        }

        // Another message of the marker's length in every load; in a duty it reads 0xffff, never a known territory.
        yield return Message(MessageDirection.Inbound, Decoy136, Bytes(136, (2, 0xff), (3, 0xff)), t + 200);
        yield return Message(MessageDirection.Outbound, Movement, Bytes(24, (0, 200)), t + 800);
    }

    /// <summary>
    /// Movement every half second, and a 40-byte server message - the shape of the pop and of the reply - that
    /// carries a small category at byte 0 and nothing at offset 2.
    /// </summary>
    internal static IEnumerable<DecodedMessage> Noise(long from, long to)
    {
        for (var t = from; t < to; t += 500)
        {
            yield return Message(MessageDirection.Outbound, Movement, Bytes(24, (0, 200)), t);
            yield return Message(MessageDirection.Inbound, ControlSelf, Bytes(40, (0, 0x0e)), t + 100);
        }
    }

    /// <summary>The request and the two replies the GLOBAL 2026.11.03 client receives about 63 and 250 ms later.</summary>
    internal static IEnumerable<DecodedMessage> Queue(long at, byte roulette, byte job, byte replyState = 1)
    {
        yield return Message(MessageDirection.Outbound, Request, Bytes(24, (0, roulette), (8, 2), (16, 0x0f)), at);
        yield return Message(MessageDirection.Inbound, Reply,
            Bytes(40, (0, replyState), (1, job), (8, 2), (16, roulette)), at + 63);
        yield return Message(MessageDirection.Inbound, Reply,
            Bytes(40, (0, replyState), (1, job), (2, 15), (8, 0x42), (9, 7), (10, 6), (16, roulette), (19, 1)), at + 250);
    }

    /// <summary>The popup: state 3 at byte 0, the roulette as a u16 at offset 2, then the two confirm-progress messages.</summary>
    internal static IEnumerable<DecodedMessage> Match(long at, byte roulette)
    {
        yield return Message(MessageDirection.Inbound, Pop,
            Bytes(40, (0, 3), (2, roulette), (8, 0x42), (9, 7), (10, 6), (33, 1), (35, 1), (37, 2)), at);
        yield return Message(MessageDirection.Inbound, Confirm, Bytes(24, (0, 4), (2, roulette)), at + 4_000);
        yield return Message(MessageDirection.Inbound, Confirm, Bytes(24, (0, 4), (2, roulette)), at + 5_500);
    }

    /// <summary>
    /// Login, a mentor roulette queued as a black mage that matches after 85 seconds, the duty entered 26
    /// seconds after the popup, and the exit: the 11-21 evening, minus everything but the field values.
    /// </summary>
    internal static IEnumerable<DecodedMessage> Evening(long popDelay = 85_000, byte roulette = Mentor, byte job = 25)
    {
        const long queueAt = 60_000;
        var popAt = queueAt + popDelay;
        var entryAt = popAt + 26_000;
        return Load(5_000, Town)
            .Concat(Noise(10_000, queueAt))
            .Concat(Queue(queueAt, roulette, job))
            .Concat(Noise(queueAt + 1_000, entryAt - 1_000))
            .Concat(Match(popAt, roulette))
            .Concat(Load(entryAt, Fractal))
            .Concat(Noise(entryAt + 5_000, entryAt + 90_000))
            .Concat(Load(entryAt + 90_000, Town))
            .Concat(Noise(entryAt + 95_000, entryAt + 110_000));
    }

    private static (CalibrationSnapshot Snapshot, CalibrationDraft Draft) Run(
        IEnumerable<DecodedMessage> traffic, CalibrationTemplate? template = null, CalibrationRejections? rejections = null)
    {
        template ??= GlobalTemplate();
        var observer = new CalibrationObserver(template, Region.Global, Session);
        foreach (var message in traffic.OrderBy(message => message.Mono))
        {
            observer.Accept(message);
        }

        observer.Flush();
        var snapshot = observer.Snapshot();
        return (snapshot, CalibrationDraft.Derive(snapshot, template, rejections));
    }

    [Fact]
    public void GlobalTemplateReadsTheReplyByItsOwnShape()
    {
        var template = GlobalTemplate();

        Assert.True(template.SeparateReply);
        Assert.True(template.ReplyCarriesJob);
        Assert.True(template.TerritoryInZone);
        Assert.Same(template.ZoneInitialization, template.TerritoryShape);
        Assert.Equal(40, template.Reply.ExpectedLength);
        Assert.Equal(16, template.ReplyRoulette!.Offset);
        Assert.NotSame(template.Pop, template.Reply);
    }

    [Fact]
    public void OneMentorEveningLearnsTheRequestReplyPopZoneAndJob()
    {
        var (snapshot, draft) = Run(Evening());

        Assert.Equal(0, snapshot.OverflowCount);
        Assert.Equal(3, snapshot.Clusters.Count);
        Assert.Equal(2, snapshot.Pairs.Count);
        Assert.All(snapshot.Pairs, pair =>
        {
            Assert.Equal((Request, Reply), (pair.RequestOpcode, pair.ReplyOpcode));
            Assert.Equal(25L, pair.JobId);
        });
        Assert.Equal(new PopTally(1, 1), snapshot.PopTallies[Pop]);

        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.Empty(draft.Blockers);
        Assert.Equal(CalibrationMatchSource.Announcement, draft.MatchSource);
        Assert.True(draft.DeclaredPop);
        Assert.Equal(Request, draft.FinderRequestOpcode);
        var byName = draft.Messages.ToDictionary(message => message.Name);
        Assert.Equal(new[] { "CONTENT_FINDER_POP", "ZONE_INITIALIZATION", "PLAYER_JOB" }, draft.Messages.Select(message => message.Name));

        var pop = byName["CONTENT_FINDER_POP"];
        Assert.Equal((Pop, PacketDirection.ServerToClient, (int?)40), (pop.Opcode, pop.Direction, pop.ExpectedLength));
        var roulette = Assert.Single(pop.Fields);
        Assert.Equal(("roulette_id", 2, ProfileFieldType.U16, (long?)0, (long?)255),
            (roulette.Name, roulette.Offset, roulette.Type, roulette.Constraints.Min, roulette.Constraints.Max));

        var zone = byName["ZONE_INITIALIZATION"];
        Assert.Equal((Zone, (int?)136), (zone.Opcode, zone.ExpectedLength));
        Assert.Equal(2, zone.Field("territory_id")!.Offset);

        var job = byName["PLAYER_JOB"];
        Assert.Equal(Reply, job.Opcode);
        Assert.Equal(new long[] { 1 }, job.Field("finder_state")!.Constraints.In);
        Assert.Equal(1, job.Field("job_id")!.Offset);
        Assert.Equal(new long[] { Mentor }, draft.ConfirmedRouletteIds);
    }

    [Fact]
    public void TimelineAsksAboutTheQueuePopupEntryAndExit()
    {
        var (_, draft) = Run(Evening());

        Assert.Equal(new[] { "login", "finder_request", "pop", "duty_enter", "duty_exit" },
            draft.Events.Select(item => item.Kind));
        var entry = draft.Events.Single(item => item.Kind == "duty_enter");
        Assert.Equal(Fractal, entry.TerritoryId);
        Assert.NotNull(entry.DutyName);
        Assert.All(draft.Events.Where(item => item.Kind != "login"), item => Assert.True(item.RequiresConfirmation));
    }

    [Fact]
    public void InstantTankMatchInsideTheReplyWindowIsAnOrdinaryMatch()
    {
        // 11-22: Expert queued as a paladin popped 0.37 s after the request, 7.3 s before the loading screen.
        var traffic = Load(5_000, Town)
            .Concat(Noise(10_000, 60_000))
            .Concat(Queue(60_000, Expert, 19))
            .Concat(Match(60_370, Expert))
            .Concat(Load(67_670, Clyteum))
            .Concat(Noise(73_000, 160_000))
            .Concat(Load(160_000, Town));

        var (snapshot, draft) = Run(traffic);

        Assert.Contains(snapshot.Pops, pop => pop.Opcode == Pop && pop.WithinEcho);
        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.Equal(Pop, draft.Messages.Single(message => message.Name == "CONTENT_FINDER_POP").Opcode);
        Assert.Equal(Reply, draft.Messages.Single(message => message.Name == "PLAYER_JOB").Opcode);
    }

    [Fact]
    public void ReplyCarryingTheQueuedRouletteAtThePopOffsetIsStillNotThePop()
    {
        // The second reply carries 15 at offset 2; queued for Normal Raids (15) it agrees with the queue there.
        var (snapshot, draft) = Run(Evening(roulette: NormalRaids));

        Assert.Contains(snapshot.Pops, pop => pop.Opcode == Reply);
        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.Equal(Pop, draft.Messages.Single(message => message.Name == "CONTENT_FINDER_POP").Opcode);
    }

    [Fact]
    public void StrayEchoInsideTheReplyWindowDoesNotMakeTheReplyAmbiguous()
    {
        // Any 40-byte server message with the queued roulette at byte 16 inside the reply window pairs with the
        // request too. It answered once, the reply twice, so only the reply is vouched for by the popup.
        var traffic = Evening().Append(Message(MessageDirection.Inbound, Stray, Bytes(40, (16, Mentor)), 60_500));

        var (snapshot, draft) = Run(traffic);

        Assert.Contains(snapshot.Pairs, pair => pair.ReplyOpcode == Stray);
        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.Equal(Reply, draft.Messages.Single(message => message.Name == "PLAYER_JOB").Opcode);
        Assert.Equal(Pop, draft.Messages.Single(message => message.Name == "CONTENT_FINDER_POP").Opcode);
    }

    [Fact]
    public void ListThatCarriesOtherValuesWhileQueuedIsNeverThePop()
    {
        // A retainer-style list sends one row per slot, 0 to 9, at the pop's offset; with the mentor roulette
        // queued, row 9 agrees and nine rows do not.
        var rows = Enumerable.Range(0, 10).Select(slot =>
            Message(MessageDirection.Inbound, RetainerList, Bytes(40, (2, (byte)slot)), 100_000 + slot * 10)).ToArray();

        var withPopup = Run(Evening().Concat(rows));
        Assert.Equal(new PopTally(10, 1), withPopup.Snapshot.PopTallies[RetainerList]);
        Assert.Equal(CalibrationDraftStatus.Ready, withPopup.Draft.Status);
        Assert.Equal(Pop, withPopup.Draft.Messages.Single(message => message.Name == "CONTENT_FINDER_POP").Opcode);

        // The confirm-progress messages are left out too: they carry the roulette at the same offset, and the
        // announcement search behind the declared pop would rightly offer them.
        var withoutPopup = Run(Evening().Where(message => message.Opcode is not (Pop or Confirm)).Concat(rows));
        Assert.NotEqual(CalibrationDraftStatus.Ready, withoutPopup.Draft.Status);
        Assert.Empty(withoutPopup.Draft.Messages);
        Assert.False(withoutPopup.Draft.Progress.PopSeen);
    }

    [Fact]
    public void DeclinedMatchSaysNothingAndTheNextMatchedRouletteDecides()
    {
        // Queued, popup, declined: no duty within the match window behind it.
        var declined = Load(5_000, Town)
            .Concat(Noise(10_000, 60_000))
            .Concat(Queue(60_000, Mentor, 25))
            .Concat(Match(100_000, Mentor))
            .Concat(Noise(101_000, 300_000))
            .ToArray();
        var first = Run(declined);
        Assert.NotEqual(CalibrationDraftStatus.Ready, first.Draft.Status);
        Assert.False(first.Draft.Progress.PopSeen);

        // Queued again later, matched and played.
        var played = declined
            .Concat(Queue(300_000, Mentor, 25))
            .Concat(Match(330_000, Mentor))
            .Concat(Load(356_000, Fractal))
            .Concat(Noise(361_000, 450_000))
            .Concat(Load(450_000, Town));
        var second = Run(played);
        Assert.Equal(CalibrationDraftStatus.Ready, second.Draft.Status);
        Assert.Equal(330_000, Assert.Single(second.Draft.Events, item => item.Kind == "pop").TMs);
    }

    [Fact]
    public void FiveMentorRunsBeforeConfirmingKeepTheNewestSamplesWithoutOverflowing()
    {
        var traffic = Evening();
        for (var run = 1; run <= 4; run++)
        {
            var at = 300_000L * run;
            traffic = traffic
                .Concat(Queue(at, Mentor, 25))
                .Concat(Noise(at + 1_000, at + 29_000))
                .Concat(Match(at + 30_000, Mentor))
                .Concat(Load(at + 56_000, Fractal))
                .Concat(Noise(at + 61_000, at + 150_000))
                .Concat(Load(at + 150_000, Town));
        }

        var (snapshot, draft) = Run(traffic);

        Assert.Equal(0, snapshot.OverflowCount);
        Assert.Equal(CalibrationObserver.MaxPopsPerBucket, snapshot.Pops.Count(pop => pop.Opcode == Pop));
        Assert.DoesNotContain(snapshot.Pops, pop => pop.Opcode == Pop && pop.AtUtc == Start.AddMilliseconds(145_000));
        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
    }

    [Fact]
    public void RejectedPopupFallsBackToTheQueueWhichTheZoneMarkerCanConfirm()
    {
        // A second roulette queued and cancelled, so the reply locks on two roulettes without the popup. The
        // confirm-progress messages are left out: they carry the roulette at the pop's offset, and the
        // announcement search would offer them before the queue is fallen back on.
        var traffic = Evening().Where(message => message.Opcode != Confirm).Concat(Queue(30_000, 2, 25)).ToArray();
        var rejections = CalibrationRejections.None with { PopOpcodes = new HashSet<ushort> { Pop } };

        var (_, draft) = Run(traffic, rejections: rejections);

        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.Equal(CalibrationMatchSource.QueueRequest, draft.MatchSource);
        Assert.False(draft.DeclaredPop);
        var pop = draft.Messages.Single(message => message.Name == "CONTENT_FINDER_POP");
        Assert.Equal((Request, PacketDirection.ClientToServer), (pop.Opcode, pop.Direction));
        // No ZONE_TERRITORY exists on this template: the territory rides on the zone marker, which is enough.
        Assert.DoesNotContain(draft.Messages, message => message.Name == "ZONE_TERRITORY");
        Assert.NotNull(draft.Messages.Single(message => message.Name == "ZONE_INITIALIZATION").Field("territory_id"));
    }

    [Fact]
    public void TeleportWhileQueuedStillSeesThePopup()
    {
        // The game keeps the queue across a zone change; so must the search for the popup (review 2026-11-22, H1).
        var traffic = Evening().Concat(Load(100_000, Town)).ToArray();

        var (snapshot, draft) = Run(traffic);

        Assert.Equal(4, snapshot.Clusters.Count);
        Assert.Contains(snapshot.Pops, pop => pop.Opcode == Pop && pop.AtUtc == Start.AddMilliseconds(145_000));
        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.Equal(Pop, draft.Messages.Single(message => message.Name == "CONTENT_FINDER_POP").Opcode);
    }

    /// <summary>
    /// Twice the player queues the mentor roulette and withdraws - which the traffic does not show - and a popup
    /// carrying another roulette (a party leader's Expert queue) follows while the withdrawn queue still seems to stand.
    /// </summary>
    /// <param name="playedAfterwards">Mentor runs of their own afterwards.</param>
    /// <param name="partyDutyFollows">Whether the other popup was accepted and its duty entered.</param>
    private static IEnumerable<DecodedMessage> WithdrawnQueuesThenOwnRuns(int playedAfterwards, bool partyDutyFollows)
    {
        var traffic = Load(5_000, Town).Concat(Noise(10_000, 20_000)).ToList();
        for (var stale = 0; stale < 2; stale++)
        {
            var at = 20_000L + stale * 200_000;
            traffic.AddRange(Queue(at, Mentor, 25));
            traffic.AddRange(Noise(at + 1_000, at + 30_000));
            traffic.AddRange(Match(at + 40_000, Expert));
            if (partyDutyFollows)
            {
                traffic.AddRange(Load(at + 66_000, Clyteum));
                traffic.AddRange(Noise(at + 71_000, at + 150_000));
                traffic.AddRange(Load(at + 150_000, Town));
            }
        }

        for (var run = 0; run < playedAfterwards; run++)
        {
            var at = 500_000L + run * 200_000;
            traffic.AddRange(Queue(at, Mentor, 25).Concat(Match(at + 30_000, Mentor))
                .Concat(Load(at + 56_000, Fractal)).Concat(Noise(at + 61_000, at + 150_000))
                .Concat(Load(at + 150_000, Town)));
        }

        return traffic;
    }

    [Fact]
    public void AnotherQueuesPopupFollowedByItsDutyIsNotHeldAgainstThePopup()
    {
        // The duty right behind it shows the popup was a match after all, just not for the withdrawn queue
        // (review 2026-11-22, M1): one match of the player's own is enough again.
        var (snapshot, draft) = Run(WithdrawnQueuesThenOwnRuns(1, partyDutyFollows: true));

        Assert.Equal(new PopTally(1, 1), snapshot.PopTallies[Pop]);
        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.Equal(Pop, draft.Messages.Single(message => message.Name == "CONTENT_FINDER_POP").Opcode);
    }

    [Fact]
    public void DeclinedPopupsOfWithdrawnQueuesCountOnlyUntilThePopupAgreesMoreOften()
    {
        // With no duty behind them the two other popups stay counted; the ratio lets the popup through once it has
        // agreed more often than that.
        var even = Run(WithdrawnQueuesThenOwnRuns(2, partyDutyFollows: false));
        Assert.Equal(new PopTally(4, 2), even.Snapshot.PopTallies[Pop]);
        Assert.NotEqual(CalibrationDraftStatus.Ready, even.Draft.Status);

        var ahead = Run(WithdrawnQueuesThenOwnRuns(3, partyDutyFollows: false));
        Assert.Equal(new PopTally(5, 3), ahead.Snapshot.PopTallies[Pop]);
        Assert.Equal(CalibrationDraftStatus.Ready, ahead.Draft.Status);
        Assert.Equal(Pop, ahead.Draft.Messages.Single(message => message.Name == "CONTENT_FINDER_POP").Opcode);
    }

    [Fact]
    public void AListIsNeverExcusedByTheDutyThatFollowsIt()
    {
        // The retainer-style rows arrive while queued and a duty follows within the match window: nine rows disagree
        // at once, so the duty excuses none of them.
        var rows = Enumerable.Range(0, 10).Select(slot =>
            Message(MessageDirection.Inbound, RetainerList, Bytes(40, (2, (byte)slot)), 150_000 + slot * 10));

        var (snapshot, draft) = Run(Evening().Concat(rows));

        Assert.Equal(new PopTally(10, 1), snapshot.PopTallies[RetainerList]);
        Assert.Equal(Pop, draft.Messages.Single(message => message.Name == "CONTENT_FINDER_POP").Opcode);
    }

    [Fact]
    public void StatusRepeatedBeforeTheLoadingScreenNeverOutvotesThePopup()
    {
        // A popup-shaped message carrying the queued roulette, sent four times between the popup and the loading
        // screen. Counted by samples it would outvote the popup, which is sent once (review 2026-11-22, M2); counted
        // by duty entries the two tie, and a tie names nothing.
        const ushort status = 0xE5A7;
        var repeats = Enumerable.Range(0, 4).Select(index =>
            Message(MessageDirection.Inbound, status, Bytes(40, (0, 7), (2, Mentor)), 150_000 + index * 3_000));

        var (snapshot, draft) = Run(Evening().Concat(repeats));

        Assert.Equal(4, snapshot.Pops.Count(pop => pop.Opcode == status));
        Assert.NotEqual(CalibrationDraftStatus.Ready, draft.Status);
        Assert.DoesNotContain(draft.Messages, message => message.Opcode == status);
    }

    [Fact]
    public void ZoneMarkerIsTheShapeThatNamedTheDuty()
    {
        // The decoy has the marker's length and appears exactly once in every load, like the marker; only the
        // marker named the duty. Without that test both qualify and the draft blocks as ambiguous.
        var (snapshot, draft) = Run(Evening());

        Assert.All(snapshot.Clusters, cluster => Assert.Equal(1,
            cluster.Members[new MessageKey(PacketDirection.ServerToClient, Decoy136, 136)]));
        Assert.Equal(Zone, draft.Messages.Single(message => message.Name == "ZONE_INITIALIZATION").Opcode);
    }

    [Fact]
    public void BusyExitStillOpensALoadThroughTheMarkerThatNamedTheDuty()
    {
        // A large message three seconds before the exit's loading screen breaks the five quiet seconds a load
        // needs; the marker that named the duty in the earlier loads vouches for this one (11-22 trace, exit).
        const long exitAt = 60_000 + 85_000 + 26_000 + 90_000;
        var busy = Message(MessageDirection.Inbound, (ushort)(Anchor + 9), new byte[2500], exitAt - 3_000);
        var traffic = Evening().Append(busy).ToArray();

        var (snapshot, draft) = Run(traffic);
        Assert.Equal(3, snapshot.Clusters.Count);
        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);

        // Control: a template whose marker carries no territory has no signature to lean on, and loses the exit.
        var (control, _) = Run(traffic, GlobalTemplate(zoneCarriesTerritory: false));
        Assert.Equal(2, control.Clusters.Count);
    }

    [Theory]
    [InlineData(1, 0)]   // the job-carrying state with an impossible job: the reply is refused as the job message
    [InlineData(2, 25)]  // another state: no reply ever carried the job
    public void ReplyIsTheJobMessageOnlyWhenItsJobReadsCleanly(byte replyState, byte job)
    {
        var traffic = Load(5_000, Town)
            .Concat(Noise(10_000, 60_000))
            .Concat(Queue(60_000, Mentor, job, replyState))
            .Concat(Noise(61_000, 144_000))
            .Concat(Match(145_000, Mentor))
            .Concat(Load(171_000, Fractal))
            .Concat(Noise(176_000, 260_000))
            .Concat(Load(261_000, Town));

        var (_, draft) = Run(traffic);

        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.DoesNotContain(draft.Messages, message => message.Name == "PLAYER_JOB");
        Assert.False(draft.Progress.JobSeen);
    }

    [Fact]
    public void WithoutPlayerJobTheReplyNeverBecomesTheJobMessage()
    {
        var (_, draft) = Run(Evening(), GlobalTemplate(replyCarriesJob: false));

        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.DoesNotContain(draft.Messages, message => message.Name == "PLAYER_JOB");
    }

    [Fact]
    public void WrittenLocalProfileHasTheShippedGlobalShapeOnTheNewOpcodes()
    {
        var template = GlobalTemplate();
        var (_, draft) = Run(Evening(), template);

        var written = LocalProfileWriter.Write(draft, template, "2026.12.01.0000.0000", Start.AddHours(1), _root);

        Assert.Equal(Path.Combine(_root, "global", "global.2026.12.01.0000.0000.local.json"), written.Path);
        var report = ProfileLoader.Validate(written.Path);
        Assert.Empty(report.Errors);
        var profile = report.Profile!;
        Assert.Equal(ProfileCompatibilityStatus.Verified, profile.Status);
        Assert.Null(profile.Calibration);
        Assert.False(profile.MatchFromQueue);
        Assert.True(profile.KnowsDutyTerritory);
        Assert.Equal(120, (int)profile.MatchWindow.TotalSeconds);
        Assert.True(profile.ToBinding().IsUsable);

        using var document = JsonDocument.Parse(File.ReadAllText(written.Path));
        var evidence = document.RootElement.GetProperty("provenance").GetProperty("evidence").EnumerateArray()
            .Select(item => item.GetProperty("note").GetString()!)
            .ToArray();
        Assert.Contains(evidence, note => note.Contains("calibration.finder_reply", StringComparison.Ordinal));
        Assert.Contains(evidence, note => note.Contains("calibration.finder_reply.player_job", StringComparison.Ordinal));
        // The job message's state selector is the template's; nothing claims it was learned on this machine.
        Assert.DoesNotContain(evidence, note => note.Contains("来自本机观察，不是继承", StringComparison.Ordinal));

        // A share code carries what was learned: the pop is a message of its own, never a reply state.
        var values = CalibratedShape.Read(template, profile.Messages)!;
        Assert.Equal((CalibrationMatchSource.Announcement, Pop, (int?)40), (values.MatchSource, values.Pop.Opcode, values.Pop.Length));
        Assert.Equal((Zone, (ushort?)null, (ushort?)Reply), (values.ZoneOpcode, values.TerritoryOpcode, values.JobOpcode));
        var asState = values with
        {
            Pop = new CalibratedPop(CalibrationMatchSource.ReplyState, Pop, Selectors: Array.Empty<CalibrationSelectorReading>()),
        };
        Assert.Contains("two messages", CalibratedShape.Messages(template, asState).Error, StringComparison.Ordinal);
    }

    [Fact]
    public void EvidenceFileKeepsThePopTallyAndTheJobTheRepliesCarried()
    {
        var template = GlobalTemplate();
        var (snapshot, _) = Run(Evening(), template);
        const string build = "2026.12.01.0000.0000";

        Assert.True(CalibrationEvidenceStore.Save(_root, Region.Global, build, "sha", snapshot));
        var carried = CalibrationEvidenceStore.Load(_root, Region.Global, build, "sha")!;

        Assert.Equal(snapshot.PopTallies.OrderBy(entry => entry.Key), carried.PopTallies.OrderBy(entry => entry.Key));
        Assert.Equal(snapshot.Pairs.Select(pair => pair.JobId), carried.Pairs.Select(pair => pair.JobId));
        var observer = new CalibrationObserver(template, Region.Global, "next-session");
        observer.AdoptEvidence(carried);
        var draft = CalibrationDraft.Derive(observer.Snapshot(), template);
        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.Equal(Reply, draft.Messages.Single(message => message.Name == "PLAYER_JOB").Opcode);
    }

    [Fact]
    public void ShippedGlobalTemplateLearnsTheEvening()
    {
        var shipped = CalibrationTemplate.Select(ProfileCatalog.LoadDefault(), Region.Global)!;

        var (_, draft) = Run(Evening(), shipped);

        Assert.Equal("global.2026.11.03", draft.TemplateProfileId);
        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.Equal(new[] { (Pop, "CONTENT_FINDER_POP"), (Zone, "ZONE_INITIALIZATION"), (Reply, "PLAYER_JOB") },
            draft.Messages.Select(message => (message.Opcode, message.Name)));
        // Everything but the opcodes is the shipped profile's.
        foreach (var message in draft.Messages)
        {
            var original = shipped.Source.Message(message.Name)!;
            Assert.Equal((original.Direction, original.ExpectedLength), (message.Direction, message.ExpectedLength));
            Assert.Equal(original.Fields, message.Fields);
        }
    }

    /// <summary>
    /// The whole path on a GLOBAL build nobody has a profile for: the shipped template arms, one mentor evening is
    /// observed, the player confirms the timeline, the local profile is written and bound in the same session, and
    /// the next mentor match is recorded - matched at the popup, entered at the zone change, with the job.
    /// Rehearsed (<see cref="CalibrationRehearsal"/>), the same path runs on the shipped build itself: its shipped
    /// profile is withheld from selection, so the build reads as a new one of a region that records, the shipped
    /// profile is still the template, and the local profile written for the build records - which the shipped
    /// profile shadows again once the switch is gone.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownGlobalBuildCalibratesAndRecordsTheNextMentorRunInTheSameSession(bool rehearsed)
    {
        var build = rehearsed ? "2026.11.03.0000.0000" : "2026.12.01.0000.0000";
        using var db = new TestDatabase();
        var localRoot = Path.Combine(_root, "profiles");
        var template = CalibrationTemplate.Select(ProfileCatalog.LoadDefault(), Region.Global)!;
        var rehearsal = rehearsed ? new CalibrationRehearsal(Region.Global, build) : null;
        ProfileSelector Selector(CalibrationRehearsal? withheld) =>
            new(ProfileCatalog.LoadMerged(ProfileCatalog.FindDefaultRoot(), localRoot, null, withheld));
        var services = new CalibrationServices(
            region => region == Region.Global ? template : null,
            () =>
            {
                var selector = Selector(rehearsal);
                return game => selector.Select(game.Region, game.GameBuild);
            },
            (draft, chosen, gameBuild, now) => LocalProfileWriter.Prepare(draft, chosen, gameBuild, now, localRoot));
        var initial = Selector(rehearsal);
        Assert.Equal(ProfileSelectionReason.NoMatch, initial.Select(Region.Global, build).ReasonCode);
        var pipeline = new LiveProtocolPipeline(db.Database, db.Clock, new LiveEventBus(db.Clock),
            game => initial.Select(game.Region, game.GameBuild), null, services);

        pipeline.Refresh(GameProcessDetection.NotRunning with { Region = Region.Global, GameBuild = build });
        Assert.True(pipeline.CalibrationArmed);
        var sessionId = Guid.NewGuid().ToString("D");
        db.Database.RunInTransaction(tx => new CaptureSessionRepository(db.Database).Insert(new CaptureSession
        {
            CaptureSessionId = sessionId,
            StartedAtUtc = db.Clock.UtcNow,
            CollectorVersion = "test",
            Region = Region.Global,
            GameBuild = build,
            ProtocolProfileId = null,
            ProfileStatus = ProfileStatus.UnsupportedBuild,
            PacketsObserved = 0,
        }, tx));
        pipeline.OnCaptureStarted(sessionId);
        void Feed(IEnumerable<DecodedMessage> traffic)
        {
            foreach (var message in traffic.OrderBy(message => message.Mono))
            {
                pipeline.Accept(message with { CaptureSessionId = sessionId });
            }
        }

        Feed(Evening());
        var ready = pipeline.CalibrationStatus();
        Assert.Equal(CalibrationState.Ready, ready.State);
        Assert.Equal("global.2026.11.03", ready.TemplateProfileId);

        var result = pipeline.ConfirmCalibration(ready.Events.Where(item => item.RequiresConfirmation)
            .ToDictionary(item => item.EventId, _ => CalibrationVerdict.Correct, StringComparer.Ordinal));

        Assert.True(result.BoundInSession);
        Assert.Equal("global." + build + ".local", result.ProfileId);
        Assert.Equal(ProfileOrigin.Local, pipeline.Current.Origin);
        var withoutSwitch = Selector(null).Select(Region.Global, build);
        (string?, ProfileOrigin?) expected = rehearsed
            ? ("global.2026.11.03", ProfileOrigin.Shipped) : (result.ProfileId, ProfileOrigin.Local);
        Assert.Equal(expected, (withoutSwitch.Profile?.ProfileId, withoutSwitch.Origin));

        Feed(Queue(600_000, Mentor, 40).Concat(Match(630_000, Mentor)));
        Assert.Equal(RunState.MentorMatched, pipeline.RunState);
        Feed(Load(656_000, Fractal));
        Assert.Equal(RunState.EnteredDuty, pipeline.RunState);
        using var command = db.Database.CreateCommand();
        command.CommandText = "SELECT job_id FROM mentor_runs";
        Assert.Equal(40L, command.ExecuteScalar());
    }

    [Fact]
    public void DiagnosticsReportShowsThePopTally()
    {
        var template = GlobalTemplate();
        var (snapshot, _) = Run(Evening(), template);

        var summary = CalibrationEvidenceSummary.From(snapshot, template, Start.AddHours(1));

        Assert.Equal($"0x{Pop:x4}=1/1", summary.PopTallies[0]);
        Assert.Contains(summary.PopTallies, row => row.StartsWith($"0x{ControlSelf:x4}=0/", StringComparison.Ordinal));
    }
}
