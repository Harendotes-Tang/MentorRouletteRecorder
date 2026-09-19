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
    /// card says so, says it costs no records, and names the one thing that restarts it.
    /// </summary>
    [Fact]
    public void AStuckUpgradeSaysItIsStuckAndHowToRestartIt()
    {
        var coordinator = new CalibrationCoordinator();
        coordinator.Arm(CalibrationObserverTests.Template(), Region.Cn, CalibrationTrafficCases.Build);
        coordinator.UseProvisional(true);
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
        Assert.Contains(status.Blockers, text => text.Contains("已经可以正常记录导随了", StringComparison.Ordinal));
        Assert.Contains(status.Blockers, text => text.Contains("重新观察", StringComparison.Ordinal));
        Assert.DoesNotContain(status.Blockers, text => text.Contains("打两把不同的随机任务就够了", StringComparison.Ordinal));
    }

    /// <summary>
    /// The desktop tells "a profile is recording while the search goes on" from the profile id on
    /// the status. That id used to be set only by the confirmation itself, so after a restart, or
    /// after 清空进度并重新观察, the card fell back to "正在重新校准…期间不会生成记录" above a
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
        Assert.Contains("3", pops[0].Label, StringComparison.Ordinal);
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
        Assert.Contains(named[0].DutyName!, named[0].Label, StringComparison.Ordinal);
        Assert.False(named[0].RequiresConfirmation);
    }
}
