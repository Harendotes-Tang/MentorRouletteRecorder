using MentorRecorder.Collector.Contracts.Texts;
using MentorRecorder.Collector.Domain;

namespace MentorRecorder.Collector.Capture;

/// <summary>
/// 将已采样事实投影为诊断状态、警告和提示。此模块不读取系统表、不访问采集源、不取得资源锁；
/// 控制器负责采样时序与会话归属，投影只负责既有展示优先级。
/// </summary>
internal static class CaptureDiagnosticsProjection
{
    /// <summary>只有空闲且驱动不可用时显示 Unavailable，不覆盖故障、启停或运行状态。</summary>
    internal static CaptureControllerState State(CaptureControllerState state, NpcapDetection npcap) =>
        state == CaptureControllerState.Idle && !npcap.Usable ? CaptureControllerState.Unavailable : state;

    /// <summary>
    /// 按游戏、驱动、租约、档案、已有连接的顺序生成警告。validationLeaseReleasing 为 null 表示没有
    /// 其他采集持有者；非空值区分验证仍在采集或只在等待释放。decodedIpcMessages 为 null 表示无会话。
    /// </summary>
    internal static IReadOnlyList<UserText> Warnings(
        GameProcessDetection game, NpcapDetection npcap, ProfileStatusSnapshot profile, CaptureControllerState state,
        bool? validationLeaseReleasing, int? preexisting, PathRecoveryState recoveryState,
        long? decodedIpcMessages, CaptureIngressCounters ingress)
    {
        var warnings = new List<UserText>(game.Warnings);
        if (npcap.Status != NpcapStatus.Ready)
        {
            warnings.Add(npcap.Guidance);
        }

        // Somebody else holds the capture lease, and the only other holder is a validation
        // session. Automatic recording waits for it without a refusal of its own, so without
        // this sentence nothing says why nothing is recorded (audit 2026-10-03, OA-8).
        if (validationLeaseReleasing is not null)
        {
            // A validation that has already ended and only keeps the lease until its source is
            // released has nothing left for the player to stop (audit 2026-10-20, A2-2).
            warnings.Add(validationLeaseReleasing.Value
                ? CollectorTexts.CaptureWarningValidationReleasing()
                : CollectorTexts.CaptureWarningValidationHolds());
        }

        if (state == CaptureControllerState.Running
            && profile.Status != ProfileStatus.Verified)
        {
            warnings.Add(profile.CalibrationActive
                ? CollectorTexts.CaptureWarningRecalibrating()
                : CollectorTexts.CaptureWarningProfileUnverified());
        }

        // The game was already logged in when capture started, so the connections it
        // already held cannot be read at all. Play on them counts for nothing, and
        // calibration looks broken rather than deaf: it can only report what it received.
        // Once the game is readable -- the player logged in again, as asked -- the "you already
        // reconnected" sentence is untrue, and following "restart the software" would cause the
        // very late attach it warns about (audit 2026-11-03, A1-4). The plain sentence stays
        // while a connection the game held at start is still in use: a chat connection that
        // reconnected on its own decodes while the zone connection stays unreadable
        // (re-review 2026-11-03, RV1-2).
        if (state == CaptureControllerState.Running && preexisting > 0 &&
            recoveryState != PathRecoveryState.Recovering)
        {
            // Which of the two sentences depends on whether the login already happened:
            // telling a player to go back to the title screen when they just did reads as
            // the software not listening to them, and is the wrong instruction besides.
            if (decodedIpcMessages == 0)
            {
                warnings.Add(ingress.ReconnectedUnreadable(preexisting)
                    ? CollectorTexts.CaptureWarningPreexistingReconnected()
                    : CollectorTexts.CaptureWarningPreexisting());
            }
            else if (ingress.GameConnectionsNow > ingress.GameConnectionsReadable)
            {
                warnings.Add(CollectorTexts.CaptureWarningPreexisting());
            }
        }

        return warnings;
    }

    /// <summary>恢复中的提示优先于沉默原因；没有会话且不在恢复时不产生提示。</summary>
    internal static UserText? Hint(PathRecoveryState recoveryState, bool hasRun,
        CaptureSilentReason reason, CaptureIngressCounters ingress, int? preexisting) =>
        recoveryState == PathRecoveryState.Recovering
            ? CollectorTexts.CaptureHintPathRecovering()
            : hasRun ? HintFor(reason, ingress, preexisting) : null;

    /// <summary>解释已经作出的沉默判定，不重新采样或改变恢复策略。</summary>
    internal static UserText? HintFor(CaptureSilentReason reason, CaptureIngressCounters ingress, int? preexisting) => reason switch
    {
        CaptureSilentReason.Midstream => ingress.ReconnectedUnreadable(preexisting)
            ? CollectorTexts.CaptureHintMidstreamReconnected()
            : CollectorTexts.CaptureHintMidstream(),
        CaptureSilentReason.NoPacketsOnAdapter => CollectorTexts.CaptureHintNoPackets(),
        CaptureSilentReason.NoStreamOwnership => CollectorTexts.CaptureHintNoOwnership(),
        _ => null,
    };
}
