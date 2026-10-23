using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// What the calibration card and the confirmation timeline tell the player, held to what is
/// actually happening. Both cases come from the first evening of 1.2.1 on real machines.
/// </summary>
public sealed class CalibrationCardTruthTests
{
    private const ushort Announce = 0xF00D;

    [Fact]
    public void ABlockedDraftBlocksACalibrationThatHasNotStartedWorking()
    {
        Assert.Equal(CalibrationState.Blocked, CalibrationCoordinator.StateFor(
            CalibrationDraftStatus.Blocked, CalibrationMatchSource.ReplyState, provisional: false, retaining: false));
    }

    /// <summary>
    /// A queue-inferred profile is in force and recording. The search for the server's own
    /// announcement running underneath it got stuck, and the card said "本机校准无法继续，当前不会
    /// 生成记录" over a blocker line that said recording works. Being unable to upgrade is not
    /// being unable to record, so the state the desktop keys its headline off stays OBSERVING.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AStuckUpgradeDoesNotSayNothingIsBeingRecorded(bool provisional, bool retaining)
    {
        Assert.Equal(CalibrationState.Observing, CalibrationCoordinator.StateFor(
            CalibrationDraftStatus.Blocked, CalibrationMatchSource.ReplyState, provisional, retaining));
    }

    /// <summary>
    /// The player's own machine, 2026-09-18: a chatty 40-byte opcode overflowed the pop table
    /// (overflow 356), so the search underneath the recording profile could never finish, while
    /// the card went on saying that two more roulettes would do it. When the search is stuck the
    /// card says so, says it costs no records, and names the one thing that restarts it. That search
    /// was for the popup the profile in force did not read (said outright since re-review 2026-10-20,
    /// RV1-3: the card beside a profile that reads it is the next test's).
    /// </summary>
    [Fact]
    public void AStuckUpgradeSaysItIsStuckAndHowToRestartIt()
    {
        var coordinator = new CalibrationCoordinator();
        coordinator.Arm(CalibrationObserverTests.Template(), Region.Cn, CalibrationTrafficCases.Build);
        coordinator.UseProvisional(true, lacking: new HashSet<string> { CalibratedShape.AnnouncedName });
        coordinator.Begin("calibration-session");
        var traffic = CalibrationObserverTests.QueueAndPop(1_000, 1, 100_000).Take(2)
            .Concat(Enumerable.Range(0, 300).Select(index => CalibrationObserverTests.Message(
                MessageDirection.Inbound, CalibrationTrafficCases.Reply,
                CalibrationObserverTests.Bytes(40, (9, (byte)(index % 256)), (16, (byte)(1 + index / 256))), 3_000 + index)));
        foreach (var message in traffic)
        {
            coordinator.Accept(message);
        }

        var status = coordinator.Snapshot();

        Assert.Equal(CalibrationState.Observing, status.State);
        Assert.Contains(status.Blockers, text => text.Chinese.Contains("导随记录照常进行", StringComparison.Ordinal));
        Assert.Contains(status.Blockers, text => text.Chinese.Contains("清空校准进度", StringComparison.Ordinal));
        Assert.DoesNotContain(status.Blockers, text => text.Chinese.Contains("就够了", StringComparison.Ordinal));
        Assert.DoesNotContain(status.Blockers, text => text.Chinese.Contains("记不到", StringComparison.Ordinal));
    }

    /// <summary>
    /// The card beside a recording profile that is still looking for the announcement. The mentor
    /// roulette can only be queued alone, so the player always sends that request and no mentor
    /// run is ever missed for being queued by someone else; the card must not suggest otherwise.
    /// Party queues still matter to calibration, which learns from other roulettes too, and the
    /// card says so.
    /// </summary>
    [Fact]
    public void AnUpgradeStillLookingNeverSaysAMentorRunIsMissedForAPartyQueue()
    {
        var coordinator = new CalibrationCoordinator();
        coordinator.Arm(CalibrationObserverTests.Template(), Region.Cn, CalibrationTrafficCases.Build);
        coordinator.UseProvisional(true);
        coordinator.Begin("calibration-session");

        var status = coordinator.Snapshot();

        Assert.Contains(status.Blockers, text => text.Chinese.Contains("尝试不同的随机任务", StringComparison.Ordinal));
        Assert.Contains(status.Blockers, text => text.Chinese.Contains("组队时请由你来申请", StringComparison.Ordinal));
        Assert.DoesNotContain(status.Blockers, text => text.Chinese.Contains("记不到", StringComparison.Ordinal));
    }

    /// <summary>
    /// Audit 2026-10-03, OCal-9. Beside a recording profile the card asked the player to check a
    /// timeline under a line saying the software was still looking and would ask them to check
    /// once it found something - which is what it was doing right then. A ready upgrade says what
    /// confirming it brings, and that recording carries on meanwhile.
    /// </summary>
    [Theory]
    [InlineData(CalibrationTrafficCases.QueueRequestAnnounced, "MATCH_ANNOUNCED", "匹配弹窗")]
    [InlineData(CalibrationTrafficCases.QueueRequest, "PLAYER_JOB", "职业")]
    [InlineData(CalibrationTrafficCases.MarkerOffset, "PLAYER_JOB", "匹配弹窗")]
    public void AnUpgradeReadyToCheckSaysWhatConfirmingItBrings(string traffic, string lacking, string brings)
    {
        var coordinator = new CalibrationCoordinator();
        coordinator.Arm(CalibrationObserverTests.Template(), Region.Cn, CalibrationTrafficCases.Build);
        // The profile in force is the one the same evening would have written, without what it lacks.
        var inForce = CalibrationTrafficCases.Derive(traffic).Messages
            .Where(message => message.Name != lacking)
            .ToArray();
        coordinator.UseProvisional(true, "cn.2026.09.01.0000.0000.local", new HashSet<string> { lacking }, inForce);
        coordinator.Begin("calibration-session");
        foreach (var message in CalibrationTrafficCases.Traffic(traffic)
            .Concat(CalibrationObserverTests.Noise(470_000, 490_000))
            .OrderBy(message => message.Mono))
        {
            coordinator.Accept(message);
        }

        var status = coordinator.Snapshot();

        Assert.Equal(CalibrationState.Ready, status.State);
        Assert.DoesNotContain(status.Blockers, text => text.Chinese.Contains("找到后会请你再核对", StringComparison.Ordinal));
        Assert.DoesNotContain(status.Blockers, text => text.Chinese.Contains("打两把不同的随机任务", StringComparison.Ordinal));
        Assert.Contains(status.Blockers, text => text.Chinese.Contains("核对下面的时间线", StringComparison.Ordinal) &&
                                                 text.Chinese.Contains(brings, StringComparison.Ordinal));
        Assert.Contains(status.Blockers, text => text.Chinese.Contains("记录照常", StringComparison.Ordinal));
        Assert.All(status.Blockers, text => Assert.DoesNotContain("0x", text.Chinese, StringComparison.Ordinal));
    }

    /// <summary>
    /// Audit 2026-10-20, C-7. Beside every queue-inferred profile the card said the software was still
    /// trying to recognise the match popup and would ask the player to check it once found - also beside
    /// a profile that recognises the popup already. What the line says the search is for comes from what
    /// the profile in force still lacks.
    /// </summary>
    [Theory]
    [InlineData("PLAYER_JOB", "职业")]
    [InlineData(null, "继续校准")]
    public void ASearchBesideAProfileThatReadsThePopupDoesNotSayItIsLookingForThePopup(string? lacking, string says)
    {
        var coordinator = new CalibrationCoordinator();
        coordinator.Arm(CalibrationObserverTests.Template(), Region.Cn, CalibrationTrafficCases.Build);
        coordinator.UseProvisional(
            true, "cn.2026.09.01.0000.0000.local", lacking is null ? null : new HashSet<string> { lacking });
        coordinator.Begin("calibration-session");

        var status = coordinator.Snapshot();

        Assert.Equal(CalibrationState.Observing, status.State);
        Assert.Contains(status.Blockers, text => text.Chinese.Contains("导随记录照常进行", StringComparison.Ordinal) &&
                                                 text.Chinese.Contains(says, StringComparison.Ordinal));
        Assert.DoesNotContain(status.Blockers, text => text.Chinese.Contains("识别匹配弹窗", StringComparison.Ordinal));
        Assert.DoesNotContain(status.Blockers, text => text.Chinese.Contains("仅靠排队和进本核对", StringComparison.Ordinal));
    }

    /// <summary>
    /// The same when the search is stuck: the card says it is stuck, not that the popup is missing - nor that runs are dated
    /// by the queue alone, which a profile that reads the popup does not do (re-review 2026-10-20, RV1-3).
    /// </summary>
    [Fact]
    public void AStuckSearchBesideAProfileThatReadsThePopupDoesNotSayThePopupIsMissing()
    {
        var coordinator = new CalibrationCoordinator();
        coordinator.Arm(CalibrationObserverTests.Template(), Region.Cn, CalibrationTrafficCases.Build);
        coordinator.UseProvisional(true, "cn.2026.09.01.0000.0000.local", new HashSet<string> { "PLAYER_JOB" });
        coordinator.Begin("calibration-session");
        var traffic = CalibrationObserverTests.QueueAndPop(1_000, 1, 100_000).Take(2)
            .Concat(Enumerable.Range(0, 300).Select(index => CalibrationObserverTests.Message(
                MessageDirection.Inbound, CalibrationTrafficCases.Reply,
                CalibrationObserverTests.Bytes(40, (9, (byte)(index % 256)), (16, (byte)(1 + index / 256))), 3_000 + index)));
        foreach (var message in traffic)
        {
            coordinator.Accept(message);
        }

        var status = coordinator.Snapshot();

        Assert.Equal(CalibrationState.Observing, status.State);
        Assert.Contains(status.Blockers, text => text.Chinese.Contains("清空校准进度", StringComparison.Ordinal) &&
                                                 text.Chinese.Contains("现用校准会继续记录", StringComparison.Ordinal));
        Assert.Contains(status.Blockers, text => text.Chinese.Contains("导随记录照常进行", StringComparison.Ordinal) &&
                                                 text.Chinese.Contains("检测到匹配弹窗时会记入弹窗时间", StringComparison.Ordinal));
        Assert.DoesNotContain(status.Blockers, text => text.Chinese.Contains("还没认出匹配弹窗", StringComparison.Ordinal));
        Assert.DoesNotContain(status.Blockers, text => text.Chinese.Contains("仅靠排队和进本核对", StringComparison.Ordinal));
    }

    /// <summary>
    /// Audit 2026-10-20, C-8. The popup was found by the roulette it carries, but no duty the table knows
    /// followed it - a patch-day duty missing from the table, or matches the player declined. The draft
    /// waited with an empty list of reasons, and the card hid its hint box: nothing told the player what
    /// to do next. A draft that is not ready always says what it is waiting for.
    /// </summary>
    [Fact]
    public void ADraftThatFoundThePopupButNoDutySaysWhatItIsWaitingFor()
    {
        var town = CalibrationObserverTests.Bytes(136, (2, 0x88), (3, 0x13));
        var traffic = CalibrationTrafficCases.Traffic(CalibrationTrafficCases.MarkerOffset)
            .Select(message => message.Opcode == CalibrationTrafficCases.Territory
                ? message with { Payload = town }
                : message);

        var draft = CalibrationDraft.Derive(CalibrationTrafficCases.Observe(traffic), CalibrationObserverTests.Template());

        Assert.Equal(CalibrationMatchSource.MarkerOffset, draft.MatchSource);
        Assert.Equal(CalibrationDraftStatus.Observing, draft.Status);
        Assert.Contains(draft.Blockers, text => text.Key == "calibration.blocker.no_duty");
    }

    /// <summary>The queue-request evening without the job in its exit burst: the job cannot be declared.</summary>
    /// <param name="traffic">Case from <see cref="CalibrationTrafficCases"/>.</param>
    private static DecodedMessage[] WithoutTheExitJob(string traffic) => CalibrationTrafficCases.Traffic(traffic)
        .Where(message => !(message.Opcode == CalibrationTrafficCases.Job &&
            message.Mono >= TimeSpan.FromMilliseconds(215_000) && message.Mono < TimeSpan.FromMilliseconds(216_000)))
        .ToArray();

    /// <summary>
    /// Audit 2026-10-20, C-1. Beside a recording, queue-inferred profile a ready draft was offered as soon
    /// as it declared one message the profile lacked, and confirming it rewrites the profile from the draft
    /// alone. A draft that found the popup but had lost the job - a gearset change inside a zone load rules
    /// the job message out - would have written the job out of the profile, and every later record would
    /// say 职业未知; the mirror case loses the popup. An upgrade is offered only when it keeps everything
    /// the profile already declares, name for name and byte for byte.
    /// </summary>
    [Theory]
    [InlineData(CalibrationTrafficCases.QueueRequest, "MATCH_ANNOUNCED", CalibrationTrafficCases.QueueRequestAnnounced, "PLAYER_JOB")]
    [InlineData(CalibrationTrafficCases.QueueRequestAnnounced, "PLAYER_JOB", CalibrationTrafficCases.QueueRequest, "MATCH_ANNOUNCED")]
    public void AnUpgradeThatWouldDropADeclaredMessageIsNotOffered(
        string profileTraffic, string lacking, string eveningTraffic, string dropped)
    {
        var inForce = CalibrationTrafficCases.Derive(profileTraffic).Messages
            .Where(message => message.Name != lacking)
            .ToArray();
        var evening = lacking == "MATCH_ANNOUNCED"
            ? WithoutTheExitJob(eveningTraffic)
            : CalibrationTrafficCases.Traffic(eveningTraffic).ToArray();
        var draft = CalibrationDraft.Derive(CalibrationTrafficCases.Observe(evening), CalibrationObserverTests.Template());
        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.Contains(inForce, message => message.Name == dropped);
        Assert.DoesNotContain(draft.Messages, message => message.Name == dropped);
        Assert.Contains(draft.Messages, message => message.Name == lacking);

        var coordinator = new CalibrationCoordinator();
        coordinator.Arm(CalibrationObserverTests.Template(), Region.Cn, CalibrationTrafficCases.Build);
        coordinator.UseProvisional(true, "cn.2026.09.01.0000.0000.local", new HashSet<string> { lacking }, inForce);
        coordinator.Begin("calibration-session");
        foreach (var message in evening
            .Concat(CalibrationObserverTests.Noise(470_000, 490_000))
            .OrderBy(message => message.Mono))
        {
            coordinator.Accept(message);
        }

        Assert.Equal(CalibrationState.Observing, coordinator.Snapshot().State);
    }

    /// <summary>
    /// The desktop tells "a profile is recording while the search goes on" from the profile id on
    /// the status. That id used to be set only by the confirmation itself, so after a restart, or
    /// after 清空进度并清空校准进度, the card fell back to "正在重新校准…期间不会生成记录" above a
    /// line saying recording works. The profile in force is a fact about the selection, not about
    /// this process's memory of having written it.
    /// </summary>
    [Fact]
    public void AProfileInForceIsStillNamedAfterARestartOrADiscard()
    {
        var coordinator = new CalibrationCoordinator();
        coordinator.Arm(CalibrationObserverTests.Template(), Region.Cn, CalibrationTrafficCases.Build);
        coordinator.UseProvisional(true, "cn.2026.09.01.0000.0000.local");

        Assert.Equal("cn.2026.09.01.0000.0000.local", coordinator.Snapshot().LocalProfileId);

        coordinator.Begin("calibration-session");
        coordinator.Discard("calibration-session");
        Assert.Equal("cn.2026.09.01.0000.0000.local", coordinator.Snapshot().LocalProfileId);

        coordinator.UseProvisional(false);
        Assert.Null(coordinator.Snapshot().LocalProfileId);
    }

    /// <summary>
    /// "职业还是没有识别出来" on two machines, and nothing in either report said why: the job rule
    /// needs exactly one shape that the entry and exit bursts vouch for, and the report named
    /// neither the shapes nor what each one read. One row per job-shaped opcode says which of
    /// "none qualifies", "two tie" or "one was contradicted" it is.
    /// </summary>
    [Fact]
    public void TheReportSaysWhatEveryJobShapedMessageRead()
    {
        var snapshot = CalibrationTrafficCases.Observe(CalibrationObserverTests.Session1());

        var rows = CalibrationEvidenceSummary.From(snapshot, CalibrationObserverTests.Template()).JobShapes;

        Assert.NotNull(rows);
        // Session1: three bursts, the job message in each, job 21 throughout, nothing out of range.
        Assert.Contains($"0x{CalibrationTrafficCases.Job:x4}=3/3!0 v21", rows!);
    }

    /// <summary>A second 16-byte message in every burst, carrying a job of its own choosing at byte 0.</summary>
    private static IEnumerable<DecodedMessage> WithATwin(ushort opcode, Func<long, byte> job) =>
        CalibrationObserverTests.Session1().Concat(new long[] { 5_000, 125_000, 215_000 }.Select(t =>
            CalibrationObserverTests.Message(MessageDirection.Inbound, opcode, CalibrationObserverTests.Bytes(16, (0, job(t))), t + 470)))
            .OrderBy(message => message.Mono);

    /// <summary>
    /// CN 2026.09.15, the player's own machine: job_shapes read "0x0266=12/18!0 v19;25;27;40" and
    /// "0x0314=12/18!0 v19;25;27;40". Two messages announce the job, in the same bursts, with the
    /// same value every time, through four class changes - and "exactly one candidate" left every
    /// record on that build 职业未知. Twins that never disagree are one answer, and the lower
    /// opcode is taken so every machine on the build writes the same profile and share code.
    /// </summary>
    [Fact]
    public void TwoJobMessagesThatAlwaysAgreeAreOneAnswer()
    {
        var lower = (ushort)(CalibrationTrafficCases.Job - 1);
        var draft = CalibrationDraft.Derive(
            CalibrationTrafficCases.Observe(WithATwin(lower, _ => 21)), CalibrationObserverTests.Template());

        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.True(draft.Progress.JobSeen);
        Assert.Equal(lower, draft.Messages.Single(message => message.Name == "PLAYER_JOB").Opcode);
    }

    /// <summary>Two candidates that read different jobs in the same burst are still nobody's answer.</summary>
    [Fact]
    public void TwoJobMessagesThatDisagreeAreStillLeftOut()
    {
        var draft = CalibrationDraft.Derive(
            CalibrationTrafficCases.Observe(WithATwin((ushort)(CalibrationTrafficCases.Job - 1), _ => 5)),
            CalibrationObserverTests.Template());

        Assert.DoesNotContain(draft.Messages, message => message.Name == "PLAYER_JOB");
        Assert.False(draft.Progress.JobSeen);
    }

    /// <summary>Two duties in one evening, the announcement sent several times per match, as the marker path sees it.</summary>
    private static CalibrationDraft TwoDutyEvening() => CalibrationDraft.Derive(
        CalibrationTrafficCases.Observe(CalibrationTrafficCases.WithoutTheMatch()
            .Concat(CalibrationTrafficCases.SecondQueue())
            .Concat(new[]
            {
                CalibrationObserverTests.Message(MessageDirection.Inbound, Announce, CalibrationObserverTests.Bytes(24, (8, 1)), 120_000),
                CalibrationObserverTests.Message(MessageDirection.Inbound, Announce, CalibrationObserverTests.Bytes(24, (8, 1)), 120_400),
                CalibrationObserverTests.Message(MessageDirection.Inbound, Announce, CalibrationObserverTests.Bytes(24, (8, 1)), 121_000),
                CalibrationObserverTests.Message(MessageDirection.Inbound, Announce, CalibrationObserverTests.Bytes(24, (8, 2)), 340_000),
                CalibrationObserverTests.Message(MessageDirection.Inbound, Announce, CalibrationObserverTests.Bytes(24, (8, 2)), 340_500),
            })
            .Concat(CalibrationObserverTests.Cluster(345_000, 1039))
            .Concat(CalibrationObserverTests.Cluster(435_000, 5000))
            .OrderBy(message => message.Mono)),
        CalibrationObserverTests.Template());

    /// <summary>
    /// The player's friend saw four "匹配弹窗：大型任务" lines for one match, each with its own
    /// 对/错 buttons. A message the server repeats for one match is one thing to confirm.
    /// </summary>
    [Fact]
    public void AnAnnouncementRepeatedForOneMatchIsOneLineToConfirm()
    {
        var draft = TwoDutyEvening();

        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.Equal(CalibrationMatchSource.MarkerOffset, draft.MatchSource);
        var pops = draft.Events.Where(item => item.Kind == "pop").ToArray();
        Assert.Equal(2, pops.Length);
        Assert.Contains("3", pops[0].Label.Chinese, StringComparison.Ordinal);
    }

    /// <summary>
    /// Only the one entry the draft rests on is a "进入副本" line. The second duty of the evening
    /// was recognised just as well and was printed as a bare "换区", so the player reported the
    /// trial's name as missing.
    /// </summary>
    [Fact]
    public void ASecondDutyOfTheEveningIsNamedOnTheTimeline()
    {
        var draft = TwoDutyEvening();

        var named = draft.Events.Where(item => item.Kind == "zone" && item.DutyName is not null).ToArray();
        Assert.Single(named);
        Assert.Contains(named[0].DutyName!, named[0].Label.Chinese, StringComparison.Ordinal);
        Assert.False(named[0].RequiresConfirmation);
    }
}
