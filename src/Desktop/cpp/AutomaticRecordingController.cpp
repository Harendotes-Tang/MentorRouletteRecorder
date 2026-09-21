#include "AutomaticRecordingController.h"

#include "Formatters.h"

namespace mr {
AutomaticRecordingController::AutomaticRecordingController(IBackend *backend, QObject *parent)
    : QObject(parent), m_backend(backend)
{
    m_timer.setInterval(kActivePollMs);
    connect(&m_timer, &QTimer::timeout, this, &AutomaticRecordingController::refresh);
    connect(backend, &IBackend::connectionChanged, this, [this] {
        // A connection change is the one moment nothing may be assumed about
        // the game, so the fast period comes back until an observation says
        // otherwise.
        setPollInterval(kActivePollMs);
        ++m_generation;
        m_busy = false;
        m_followAttempted = m_stopAttempted = false;
        m_stopPending = false;
        m_followError.clear();
        m_validationError.clear();
        // A disconnect is not evidence of recovery; retain acknowledged/pending incidents.
        setState(blocked() ? QStringLiteral("blocked") : QStringLiteral("checking"),
                 blocked() ? m_message : tr("正在连接采集服务，尚未确认自动记录能力。"));
        refresh();
    });
    m_timer.start();
    QTimer::singleShot(0, this, &AutomaticRecordingController::refresh);
}

void AutomaticRecordingController::setMaintenance(bool enabled)
{
    if (m_maintenance == enabled) return;
    m_maintenance = enabled;
    ++m_generation;
    m_busy = false;
    m_followError.clear();
    m_validationError.clear();
    m_followAttempted = m_stopAttempted = false;
    m_stopPending = false;
    clearIncident();
    refresh();
}

void AutomaticRecordingController::setPollInterval(int milliseconds)
{
    if (m_timer.interval() == milliseconds)
        return;
    m_timer.setInterval(milliseconds);
    if (m_timer.isActive())
        m_timer.start();
}

void AutomaticRecordingController::setState(const QString &state, const QString &message)
{
    m_state = state;
    m_message = message;
    Q_EMIT changed();
}

void AutomaticRecordingController::clearIncident()
{
    m_incident.clear();
    m_pending = false;
}

void AutomaticRecordingController::block(const QString &key, const QString &message)
{
    const bool fresh = m_incident != key;
    m_incident = key;
    if (fresh) m_pending = true;
    setState(QStringLiteral("blocked"), message);
    if (fresh && !m_maintenance) Q_EMIT incidentRaised();
}

void AutomaticRecordingController::acknowledge()
{
    m_pending = false;
    Q_EMIT changed();
}

void AutomaticRecordingController::retry()
{
    if (m_busy) return;
    m_followAttempted = m_stopAttempted = false;
    m_stopPending = false;
    m_followError.clear();
    m_validationError.clear();
    refresh();
}

void AutomaticRecordingController::refresh()
{
    if (m_maintenance || m_busy || !m_backend || !m_backend->isConnected()) return;
    m_busy = true;
    readSettings(m_generation);
}

void AutomaticRecordingController::finishUnknown(const QString &message)
{
    m_busy = false;
    // Missing facts cannot clear an incident, or resurrect a green status.
    setState(blocked() ? QStringLiteral("blocked") : QStringLiteral("checking"),
             blocked() ? m_message : message);
}

void AutomaticRecordingController::readSettings(quint64 generation)
{
    m_followReady = false;
    m_validationReady = false;
    if (!m_backend) return;
    m_backend->getCaptureSettings()->whenDone(this,
        [this, generation](bool ok, const QVariantMap &p, const QString &, const QString &) {
        if (generation != m_generation) return;
        if (!ok || !p.contains(QStringLiteral("follow_game"))) {
            setState(blocked() ? QStringLiteral("blocked") : QStringLiteral("checking"),
                     blocked() ? m_message : tr("尚未确认自动跟随设置…"));
            readValidation(generation); return;
        }
        Q_EMIT settingsConfirmed(p);
        m_followReady = p.value(QStringLiteral("follow_game")).toBool();
        if (!m_followReady) {
            setState(blocked() ? QStringLiteral("blocked") : QStringLiteral("checking"),
                     blocked() ? m_message : tr("正在启用自动跟随，尚未确认可记录…"));
            if (m_followAttempted || !m_backend) {
                m_followError = tr("自动跟随尚未启用，无法自动记录。请重试。");
                readValidation(generation); return;
            }
            m_followAttempted = true;
            m_backend->updateCaptureSettings({{QStringLiteral("follow_game"), true}})->whenDone(this,
                [this, generation](bool saved, const QVariantMap &settings, const QString &, const QString &message) {
                if (generation != m_generation) return;
                m_followReady = saved && settings.value(QStringLiteral("follow_game")).toBool();
                if (!m_followReady)
                    m_followError = tr("自动跟随启用失败，无法自动记录。请重试或查看诊断。%1").arg(message);
                else { m_followError.clear(); Q_EMIT settingsConfirmed(settings); }
                readValidation(generation);
            });
            return;
        }
        m_followError.clear();
        readValidation(generation);
    });
}

void AutomaticRecordingController::readValidation(quint64 generation)
{
    if (!m_backend) return;
    m_backend->getCaptureValidationStatus()->whenDone(this,
        [this, generation](bool ok, const QVariantMap &p, const QString &, const QString &) {
        if (generation != m_generation) return;
        if (!ok || !p.contains(QStringLiteral("active"))) {
            setState(blocked() ? QStringLiteral("blocked") : QStringLiteral("checking"),
                     blocked() ? m_message : tr("尚未确认验证工具是否占用采集…"));
            readCapture(generation); return;
        }
        m_validationReady = !p.value(QStringLiteral("active")).toBool();
        if (!m_validationReady) {
            setState(blocked() ? QStringLiteral("blocked") : QStringLiteral("checking"),
                     blocked() ? m_message : tr("正在释放验证工具占用的采集资源，尚未确认可记录…"));
            if (!m_stopAttempted && m_backend) {
                m_stopAttempted = true;
                m_backend->stopCaptureValidation()->whenDone(this,
                    [this, generation](bool stopped, const QVariantMap &status, const QString &, const QString &message) {
                    if (generation != m_generation) return;
                    m_validationReady = stopped && status.contains(QStringLiteral("active"))
                        && !status.value(QStringLiteral("active")).toBool();
                    // Stop acknowledges ownership drainage before it completes: STOPPING
                    // remains active in the existing IPC contract and is not a refusal.
                    m_stopPending = stopped && status.value(QStringLiteral("active")).toBool()
                        && status.value(QStringLiteral("state")).toString() == QLatin1String("STOPPING");
                    if (!m_validationReady && !m_stopPending)
                        m_validationError = tr("验证工具仍占用采集，无法自动记录。请重试释放。%1").arg(message);
                    else m_validationError.clear();
                    readCapture(generation);
                });
                return;
            }
            if (m_stopPending && p.value(QStringLiteral("state")).toString() == QLatin1String("STOPPING"))
                m_validationError.clear();
            else {
                m_stopPending = false;
                m_validationError = tr("验证工具仍占用采集，无法自动记录。请重试释放。");
            }
        } else {
            m_stopPending = false;
            m_validationError.clear();
        }
        readCapture(generation);
    });
}

void AutomaticRecordingController::readCapture(quint64 generation)
{
    if (!m_backend) return;
    m_backend->getStatus()->whenDone(this,
        [this, generation](bool ok, const QVariantMap &p, const QString &, const QString &error) {
        if (generation != m_generation) return;
        if (!ok) { finishUnknown(tr("无法确认采集状态。%1").arg(error)); return; }
        auto capture = p.value(QStringLiteral("capture")).toMap();
        const auto game = p.value(QStringLiteral("game")).toMap();
        if (game.contains(QStringLiteral("install_path_readable")))
            capture.insert(QStringLiteral("install_path_readable"), game.value(QStringLiteral("install_path_readable")));
        if (!capture.contains(QStringLiteral("ffxiv_running"))) {
            finishUnknown(tr("采集状态尚未完整，正在检查自动记录能力…")); return;
        }
        m_busy = false;
        Q_EMIT captureObserved(capture);
        project(capture);
    });
}

void AutomaticRecordingController::project(const QVariantMap &c)
{
    const QString initError = !m_followError.isEmpty() ? m_followError : m_validationError;
    if (!c.value(QStringLiteral("ffxiv_running")).toBool()) {
        // Nothing this poll reads can change while the game is not running,
        // and the Collector publishes a status event when it starts. Three
        // requests every two seconds all evening is pure noise.
        setPollInterval(kIdlePollMs);
        clearIncident();
        setState(QStringLiteral("waiting"), initError.isEmpty()
                 ? tr("等待游戏启动 · 可查看历史记录与统计") : initError);
        return;
    }
    setPollInterval(kActivePollMs);
    const QString session = c.value(QStringLiteral("ffxiv_process_id")).toString() + QLatin1Char(':');
    const QString profile = c.value(QStringLiteral("profile_status")).toString();
    if (profile.isEmpty()) { finishUnknown(tr("正在检查协议档案，尚未确认自动记录能力…")); return; }
    const bool pathUnreadable = c.contains(QStringLiteral("install_path_readable"))
                                && !c.value(QStringLiteral("install_path_readable")).toBool();
    const bool npcapMissing = c.contains(QStringLiteral("npcap_installed"))
                              && !c.value(QStringLiteral("npcap_installed")).toBool();
    // 本机校准 outranks "协议档案不匹配": a mismatched profile is the situation
    // calibration exists for. An unreadable install path still wins - nothing can
    // be calibrated for an unknown build - and so does a missing Npcap, with no
    // packets to learn from.
    if (!pathUnreadable && !npcapMissing && profile != QLatin1String("VERIFIED") && projectCalibration(c))
        return;
    if (profile != QLatin1String("VERIFIED")) {
        block(session + QStringLiteral("profile:") + profile, pathUnreadable
              // 路径取自内核进程表，本就不需要额外权限，提权对此没有帮助，
              // 因此这里不再建议以管理员身份运行（审查 2026-09-21 第 1 条）。
              ? tr("无法读取游戏安装路径，区服与客户端版本未知，无法自动记录。请确认游戏仍在运行，并查看诊断。")
              : tr("协议档案不匹配，无法自动记录。继续游戏不会生成自动导随记录，请查看诊断。")); return;
    }
    if (!initError.isEmpty()) {
        block(session + (m_followError.isEmpty() ? QStringLiteral("validation") : QStringLiteral("follow")), initError);
        return;
    }
    if (!m_followReady || !m_validationReady) {
        finishUnknown(tr("正在确认自动跟随与采集资源，尚未确认可记录…")); return;
    }
    if (c.contains(QStringLiteral("npcap_installed")) && !c.value(QStringLiteral("npcap_installed")).toBool()) {
        block(session + QStringLiteral("npcap"), tr("未检测到 Npcap，无法自动记录。请查看诊断。")); return;
    }
    const QString error = c.value(QStringLiteral("last_error_code")).toString();
    const QString reason = silentReason(c);
    if (c.value(QStringLiteral("midstream_suspected")).toBool()
        || reason == QLatin1String("MIDSTREAM")) {
        block(session + QStringLiteral("capture:midstream"), silentMessage(c, reason)); return;
    }
    if (!error.isEmpty() && error != QLatin1String("NONE")) {
        // A player cannot act on "ERR_DB_BUSY". The sentence is the whole
        // explanation; the raw token only ever trails it, and only for a code
        // this build has no wording for.
        const QString detail = Formatters::captureErrorKnown(error)
            ? Formatters::captureErrorLabel(error)
            : tr("%1（代码 %2）").arg(Formatters::captureErrorLabel(error), error);
        block(session + QStringLiteral("capture:") + error,
              tr("采集链路受阻，无法自动记录。%1 请查看诊断。").arg(detail)); return;
    }
    if (c.value(QStringLiteral("state")).toString() != QLatin1String("RUNNING")) {
        finishUnknown(tr("正在等待自动采集启动，尚未确认可记录…")); return;
    }
    if (!reason.isEmpty()) {
        // RUNNING is a fact about a socket, not about the game: a capture that
        // decodes nothing must not be shown as a green 「自动监听中」.
        clearIncident();
        setState(QStringLiteral("listening_silent"), silentMessage(c, reason)); return;
    }
    clearIncident();
    // A profile this machine calibrated is a VERIFIED profile like any other,
    // but the user should be able to see where it came from without opening
    // the diagnostics page.
    const QString origin = c.value(QStringLiteral("profile_origin")).toString();
    if (origin == QLatin1String("LOCAL_CALIBRATION")) {
        setState(QStringLiteral("listening"),
                 tr("自动监听中 · 已按本机校准的档案记录，等待导随事件")); return;
    }
    if (origin == QLatin1String("SHARED_CALIBRATION")) {
        setState(QStringLiteral("listening"),
                 tr("自动监听中 · 已按其他玩家分享、本机核实过的校准记录，等待导随事件")); return;
    }
    setState(QStringLiteral("listening"), tr("自动监听中 · 等待导随事件，记录生成后可在历史中查看"));
}

/// The three calibration projections the capture page card binds to.
/// Returns false when \a c is not calibrating, so the caller falls through to
/// the ordinary profile rules.
///
/// None of them calls block(): calibration is a normal, expected patch-day
/// state, not an incident, so it raises no alert dialog and there is nothing
/// to acknowledge. It does set the attention flag, because no run is recorded
/// while it runs and a green badge would be dishonest.
bool AutomaticRecordingController::projectCalibration(const QVariantMap &c)
{
    const QVariant raw = c.value(QStringLiteral("calibration"));
    if (!raw.isValid() || raw.isNull())
        return false;
    const QVariantMap calibration = raw.toMap();
    const QString state = calibration.value(QStringLiteral("state")).toString();

    if (state == QLatin1String("WAITING")
        || (state == QLatin1String("OBSERVING") && c.contains(QStringLiteral("state"))
            && c.value(QStringLiteral("state")).toString() != QLatin1String("RUNNING"))) {
        // Armed but nothing is being captured: say so instead of pretending the
        // roulette the player is about to run will be seen.
        const QString error = c.value(QStringLiteral("last_error_code")).toString();
        const QString detail = (!error.isEmpty() && error != QLatin1String("NONE"))
            ? tr("抓包上次没有成功（%1），正在自动重试；请查看诊断。").arg(Formatters::captureErrorLabel(error))
            : tr("正在等待抓包启动。");
        clearIncident();
        setState(QStringLiteral("calibrating"),
                 tr("游戏更新到了新版本，本软件准备重新校准。%1").arg(detail));
        return true;
    }
    if (state == QLatin1String("OBSERVING")) {
        clearIncident();
        // A shared calibration that infers the match waits for the player's consent, and
        // nothing records until they give it; the banner has to send them to the card.
        const QVariantMap shared = calibration.value(QStringLiteral("shared")).toMap();
        if (shared.value(QStringLiteral("phase")).toString() == QLatin1String("AWAITING_CONSENT")
            && !shared.value(QStringLiteral("user_rejected")).toBool()) {
            setState(QStringLiteral("calibrating"),
                     tr("找到了其他玩家分享的校准，同意一次就能开始自动记录：请到捕获诊断页的校准卡片上查看。"));
            return true;
        }
        // The four progress ticks live on the capture page's calibration card. This
        // sentence also appears in the dashboard's fixed-height 当前导随 card, so it
        // carries no progress suffix.
        setState(QStringLiteral("calibrating"),
                 tr("游戏更新到了新版本，本软件正在重新校准："
                    "正常打一把随机任务（进本、打完出本）就好，期间不会生成记录。"));
        return true;
    }
    if (state == QLatin1String("READY")) {
        int required = 0;
        const auto events = calibration.value(QStringLiteral("events")).toList();
        for (const auto &event : events) {
            if (event.toMap().value(QStringLiteral("requires_confirmation")).toBool())
                ++required;
        }
        clearIncident();
        setState(QStringLiteral("calibration_ready"),
                 tr("校准完成，核对 %1 件事就能开始自动记录。").arg(required));
        return true;
    }
    if (state == QLatin1String("BLOCKED")) {
        // The Collector's blockers are already written for the player
        // (section 2.2); repeating the first one verbatim is the whole message.
        const auto blockers = calibration.value(QStringLiteral("blockers")).toList();
        const QString first = blockers.isEmpty() ? QString() : blockers.first().toString();
        clearIncident();
        setState(QStringLiteral("calibration_blocked"),
                 first.isEmpty() ? tr("本机校准无法继续，当前不会生成记录。请查看诊断。")
                                 : tr("%1 请查看诊断。").arg(first));
        return true;
    }
    // IDLE and DONE are not calibration projections: DONE has already produced
    // a VERIFIED profile, so the ordinary listening path applies.
    return false;
}

/// Why this RUNNING capture is producing nothing, or an empty string when it
/// is producing something (or has not been observed long enough to tell).
///
/// The Collector's own \c silent_reason is authoritative in both directions:
/// a value of NONE means it measured and found nothing wrong, and the local
/// heuristic must not overrule that. Only when the field is absent - an older
/// Collector - does the desktop fall back to the counters, and then only after
/// a full minute, because a capture that just started has measured nothing.
QString AutomaticRecordingController::silentReason(const QVariantMap &c)
{
    const QVariant reported = c.value(QStringLiteral("silent_reason"));
    if (reported.isValid() && !reported.isNull()) {
        const QString reason = reported.toString();
        return reason == QLatin1String("NONE") ? QString() : reason;
    }
    const QVariant decoded = c.value(QStringLiteral("messages_decoded"));
    const QVariant uptime = c.value(QStringLiteral("uptime_ms"));
    // Absent means "not measured"; that is never turned into an accusation.
    if (!decoded.isValid() || decoded.isNull() || !uptime.isValid() || uptime.isNull())
        return QString();
    if (decoded.toLongLong() > 0 || uptime.toLongLong() <= kSilenceGraceMs)
        return QString();
    return QStringLiteral("NO_STREAM_OWNERSHIP");
}

/// What to tell the player about \a reason. The Collector's hint wins when it
/// sent one: it knows the specific connection. No branch ever renders the
/// reason token itself.
QString AutomaticRecordingController::silentMessage(const QVariantMap &c, const QString &reason)
{
    const QString hint = c.value(QStringLiteral("hint")).toString();
    if (!hint.isEmpty())
        return hint;
    if (reason == QLatin1String("MIDSTREAM"))
        return tr("监听开始时游戏已登录，本次连接无法识别；"
                  "请回到标题画面重新登录，或先开本软件再开游戏。");
    if (reason == QLatin1String("NO_PACKETS_ON_ADAPTER"))
        return tr("所选网卡上没有游戏流量（可能在用加速器/VPN），"
                  "请到捕获诊断页重新选择网卡。");
    return tr("正在监听，但还没有收到可识别的游戏数据。");
}
}
