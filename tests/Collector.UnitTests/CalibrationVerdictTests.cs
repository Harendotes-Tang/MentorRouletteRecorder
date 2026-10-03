using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Audit 2026-10-03, OCal-2. A line the player marks 错 must cost the draft what that line was
/// about, and nothing else: never the identical draft offered straight back, never "this version
/// needs new software" over a duty line, and never a block that 重新观察 cannot clear.
///
/// The realistic trigger: the player queues, cancels, and within the hour a friend's party brings
/// them into a duty. The timeline asks "进入副本：X（本机把上面那次排本记为这一把的匹配）", and the
/// truthful answer is 错.
/// </summary>
public sealed class CalibrationVerdictTests
{
    private const string Session = "calibration-session";

    private static CalibrationCoordinator Coordinator(IEnumerable<DecodedMessage> traffic)
    {
        var coordinator = new CalibrationCoordinator();
        coordinator.Arm(CalibrationObserverTests.Template(), Region.Cn, CalibrationTrafficCases.Build);
        coordinator.Begin(Session);
        Feed(coordinator, traffic);
        return coordinator;
    }

    private static void Feed(CalibrationCoordinator coordinator, IEnumerable<DecodedMessage> traffic)
    {
        foreach (var message in traffic.OrderBy(message => message.Mono))
        {
            coordinator.Accept(message);
        }
    }

    private static Dictionary<string, CalibrationVerdict> Marking(CalibrationStatusSnapshot status, string wrongKind) =>
        status.Events.Where(item => item.RequiresConfirmation).ToDictionary(
            item => item.EventId,
            item => item.Kind == wrongKind ? CalibrationVerdict.Wrong : CalibrationVerdict.Correct,
            StringComparer.Ordinal);

    /// <summary>Everything a confirmation would write and everything the player was shown.</summary>
    private static string Offer(CalibrationDraft draft) =>
        $"{draft.Status} {draft.MatchSource} " +
        string.Join(";", draft.Messages.Select(message => CalibratedProfileDocument.Message(message).ToJsonString())) + " " +
        string.Join(";", draft.Events.Select(item => item.EventId + "=" + item.Label));

    [Theory]
    [InlineData("finder_request")]
    [InlineData("duty_enter")]
    [InlineData("duty_exit")]
    public void AWrongLineOfAnInferredDraftIsNeitherOfferedBackNorBlamedOnTheBuild(string kind)
    {
        var coordinator = Coordinator(CalibrationTrafficCases.Traffic(CalibrationTrafficCases.QueueRequest));
        var offered = coordinator.Snapshot();
        Assert.Equal(CalibrationState.Ready, offered.State);
        var before = Offer(coordinator.CurrentDraft()!);
        Assert.Contains(offered.Events, item => item.Kind == kind && item.RequiresConfirmation);

        Assert.Null(coordinator.Judge(Marking(offered, kind)));

        var again = coordinator.CurrentDraft()!;
        Assert.NotEqual(before, Offer(again));
        Assert.NotEqual(CalibrationDraftStatus.Blocked, again.Status);
        Assert.DoesNotContain(again.Blockers, text => text.Contains("新版本", StringComparison.Ordinal));
        Assert.NotEqual(CalibrationState.Ready, coordinator.Snapshot().State);
    }

    /// <summary>
    /// The popup line of a draft that read the match off the queue reply is about the state that
    /// reply carried at that moment, not about the reply opcode: the reply still answers every
    /// queue request. Rejecting the opcode left nothing to pair with, so the promised "再打一把随机
    /// 任务后会重新核对" could never happen; now another roulette is enough.
    /// </summary>
    [Fact]
    public void AWrongPopupOnTheQueueReplyLeavesTheReplyToPairWith()
    {
        var coordinator = Coordinator(CalibrationObserverTests.Session1());
        var offered = coordinator.Snapshot();
        Assert.Equal(CalibrationState.Ready, offered.State);
        Assert.Equal(CalibrationMatchSource.ReplyState, coordinator.CurrentDraft()!.MatchSource);

        Assert.Null(coordinator.Judge(Marking(offered, "pop")));
        Assert.NotEqual(CalibrationState.Ready, coordinator.Snapshot().State);

        Feed(coordinator, CalibrationTrafficCases.SecondQueue());

        var again = coordinator.CurrentDraft()!;
        Assert.Equal(CalibrationDraftStatus.Ready, again.Status);
        Assert.Equal(CalibrationMatchSource.QueueRequest, again.MatchSource);
        Assert.Equal(CalibrationTrafficCases.Request, again.FinderRequestOpcode);
    }

    /// <summary>重新观察 means "forget what you saw", and what the player rejected is part of that.</summary>
    [Fact]
    public void ObservingAgainForgetsWhatThePlayerRejected()
    {
        var traffic = CalibrationTrafficCases.Traffic(CalibrationTrafficCases.QueueRequest);
        var coordinator = Coordinator(traffic);
        Assert.Null(coordinator.Judge(Marking(coordinator.Snapshot(), "duty_enter")));
        Assert.NotEqual(CalibrationState.Ready, coordinator.Snapshot().State);

        coordinator.Discard(Session);
        Feed(coordinator, traffic);

        Assert.Equal(CalibrationState.Ready, coordinator.Snapshot().State);
    }

    /// <summary>
    /// What the traffic itself disproved is not the player's opinion: a pop opcode a local profile
    /// was withdrawn over stays refused for the rest of the run (docs/protocol-profile-format.md
    /// §11.4), 重新观察 or not.
    /// </summary>
    [Fact]
    public void ObservingAgainKeepsWhatTheTrafficDisproved()
    {
        var traffic = CalibrationTrafficCases.Traffic(CalibrationTrafficCases.MarkerOffset);
        var coordinator = Coordinator(traffic);
        Assert.Equal(CalibrationMatchSource.MarkerOffset, coordinator.CurrentDraft()!.MatchSource);

        coordinator.RejectPopOpcode(CalibrationTrafficCases.Announce);
        coordinator.Discard(Session);
        Feed(coordinator, traffic);

        Assert.NotEqual(CalibrationMatchSource.MarkerOffset, coordinator.CurrentDraft()!.MatchSource);
    }
}
