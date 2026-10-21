#include "AutomaticRecordingController.h"

#include "CollectorText.h"
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
        ++m_captureRevision;
        m_latestCapture.clear();
        m_initializationPending = false;
        m_busy = false;
        m_followAttempted = m_stopAttempted = false;
        m_stopPending = false;
        m_followError.clear();
        m_validationError.clear();
        // A disconnect is not evidence of recovery; retain acknowledged/pending incidents.
        setState(blocked() ? QStringLiteral("blocked") : QStringLiteral("checking"),
                 blocked() ? m_message : LiveText([] { return tr("正在连接自动记录服务，请稍候。"); }));
        refresh();
    });
    // The message is a recipe (LiveText): reading it again is the whole re-translation.
    onLanguageChanged(this, [this] { Q_EMIT changed(); });
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

void AutomaticRecordingController::setUserObservation(bool enabled)
{
    if (m_userObservation == enabled) return;
    m_userObservation = enabled;
    ++m_generation; // 已在途的初始化不能越过玩家刚开始的采证。
    m_busy = false;
    m_stopAttempted = false;
    m_stopPending = false;
    m_validationError.clear();
    clearIncident();
    refresh();
}

void AutomaticRecordingController::setState(const QString &state, const LiveText &message)
{
    // Both texts are built now, in the language on screen, so a switch between
    // two polls does not count as something new to say.
    if (state != m_state || message.text() != m_message.text())
        ++m_messageSerial;
    m_state = state;
    m_message = message;
    Q_EMIT changed();
}

void AutomaticRecordingController::clearIncident()
{
    m_incident.clear();
    m_pending = false;
}

void AutomaticRecordingController::block(const QString &key, const LiveText &message)
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
    if (m_userObservation) {
        setState(QStringLiteral("observing"), [] { return tr("正在进行你主动开启的元数据采证，自动记录已暂停。请在「捕获诊断」停止采证。"); });
        return;
    }
    m_busy = true;
    m_initializationPending = true;
    readSettings(m_generation);
}

void AutomaticRecordingController::observeCapture(const QVariantMap &capture)
{
    // 缺失或空的字段不是“游戏已退出”，不能用它清除已确认的故障。
    if (capture.value(QStringLiteral("ffxiv_running")).metaType().id() != QMetaType::Bool)
        return;
    const auto revision = ++m_captureRevision;
    m_latestCapture = capture;
    Q_EMIT captureObserved(capture);
    if (revision != m_captureRevision || m_maintenance || m_userObservation
        || !m_backend || !m_backend->isConnected())
        return;
    // 初始化尚未答复时，不拿上一轮的设置错误为新会话建立故障。
    // 游戏退出、选择对象以及档案不可用则已有充分事实，可以立即投影。
    if (m_initializationPending && capture.value(QStringLiteral("ffxiv_running")).toBool()
        && capture.value(QStringLiteral("profile_status")).toString() == QLatin1String("VERIFIED")
        && !capture.value(QStringLiteral("game_selection_required")).toBool()) {
        finishUnknown([] { return tr("正在准备自动记录，请稍候…"); });
        return;
    }
    project(capture);
}

void AutomaticRecordingController::finishUnknown(const LiveText &message)
{
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
                     blocked() ? m_message : LiveText([] { return tr("正在检查自动记录设置…"); }));
            readValidation(generation); return;
        }
        Q_EMIT settingsConfirmed(p);
        m_followReady = p.value(QStringLiteral("follow_game")).toBool();
        if (!m_followReady) {
            setState(blocked() ? QStringLiteral("blocked") : QStringLiteral("checking"),
                     blocked() ? m_message : LiveText([] { return tr("正在开启游戏启动后自动记录…"); }));
            if (m_followAttempted || !m_backend) {
                m_followError = [] { return tr("还没能开启自动记录，请点「重试自动记录」。"); };
                readValidation(generation); return;
            }
            m_followAttempted = true;
            m_backend->updateCaptureSettings({{QStringLiteral("follow_game"), true}})->whenDone(this,
                [this, generation](bool saved, const QVariantMap &settings, const QString &, const QString &message) {
                if (generation != m_generation) return;
                m_followReady = saved && settings.value(QStringLiteral("follow_game")).toBool();
                if (!m_followReady)
                    m_followError = [message] { return tr("没能开启自动记录，请点「重试自动记录」或查看「捕获诊断」。%1").arg(message); };
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
                     blocked() ? m_message : LiveText([] { return tr("正在检查验证工具是否已停止…"); }));
            readCapture(generation); return;
        }
        m_validationReady = !p.value(QStringLiteral("active")).toBool();
        if (!m_validationReady) {
            setState(blocked() ? QStringLiteral("blocked") : QStringLiteral("checking"),
                     blocked() ? m_message : LiveText([] { return tr("正在停止验证工具，请稍候。停止后才能自动记录。"); }));
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
                        m_validationError = [message] { return tr("验证工具还没有停止，暂时不能自动记录。请点「重试自动记录」。%1").arg(message); };
                    else m_validationError.clear();
                    readCapture(generation);
                });
                return;
            }
            if (m_stopPending && p.value(QStringLiteral("state")).toString() == QLatin1String("STOPPING"))
                m_validationError.clear();
            else {
                m_stopPending = false;
                m_validationError = [] { return tr("验证工具还没有停止，暂时不能自动记录。请点「重试自动记录」。"); };
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
    m_initializationPending = false;
    const auto revision = m_captureRevision;
    m_backend->getStatus()->whenDone(this,
        [this, generation, revision](bool ok, const QVariantMap &p, const QString &, const QString &error) {
        if (generation != m_generation) return;
        m_busy = false;
        if (revision != m_captureRevision) {
            if (!m_latestCapture.isEmpty() && !m_maintenance && !m_userObservation)
                project(m_latestCapture);
            return;
        }
        if (!ok) { finishUnknown([error] { return tr("暂时无法确认自动记录服务是否正常。%1").arg(error); }); return; }
        auto capture = p.value(QStringLiteral("capture")).toMap();
        const auto game = p.value(QStringLiteral("game")).toMap();
        if (game.contains(QStringLiteral("install_path_readable")))
            capture.insert(QStringLiteral("install_path_readable"), game.value(QStringLiteral("install_path_readable")));
        if (capture.value(QStringLiteral("ffxiv_running")).metaType().id() != QMetaType::Bool) {
            finishUnknown([] { return tr("正在获取自动记录状态，请稍候…"); }); return;
        }
        observeCapture(capture);
    });
}

void AutomaticRecordingController::project(const QVariantMap &c)
{
    // A choice is asked for only while there is a listed client to choose. With
    // the only client closed (EXITED) or every client gone (MULTIPLE) the list
    // is empty, and that is the ordinary wait for the game below (review OD-1).
    const int listed = int(c.value(QStringLiteral("game_processes")).toList().size());
    if (c.value(QStringLiteral("game_selection_required")).toBool() && listed > 0) {
        setPollInterval(kActivePollMs);
        clearIncident();
        const QString reason = c.value(QStringLiteral("game_selection_reason")).toString();
        setState(QStringLiteral("choosing_game"), [reason, listed] {
            return reason == QLatin1String("EXITED")
                ? tr("所选游戏已退出，记录已暂停。单开时重新启动同一游戏会自动接续；无法确认时请在总览或捕获诊断页重新选择游戏窗口。")
                : reason == QLatin1String("IDENTITY_UNAVAILABLE")
                ? tr("暂时无法确认游戏的启动时间，记录已暂停。请重新检测后选择游戏窗口。")
                : listed > 1
                ? tr("检测到多个游戏客户端，请在总览或捕获诊断页选择要记录的游戏窗口。")
                : tr("尚未确定要记录的游戏，请在总览或捕获诊断页选择要记录的游戏窗口。");
        });
        return;
    }
    const LiveText initError = !m_followError.isEmpty() ? m_followError : m_validationError;
    if (!c.value(QStringLiteral("ffxiv_running")).toBool()) {
        // Nothing this poll reads can change while the game is not running,
        // and the Collector publishes a status event when it starts. Three
        // requests every two seconds all evening is pure noise.
        setPollInterval(kIdlePollMs);
        clearIncident();
        setState(QStringLiteral("waiting"), initError.isEmpty()
                 ? LiveText([] { return tr("等待游戏启动 · 可查看历史记录与统计"); }) : initError);
        return;
    }
    setPollInterval(kActivePollMs);
    const QString session = c.value(QStringLiteral("ffxiv_process_id")).toString() + QLatin1Char(':');
    const QString profile = c.value(QStringLiteral("profile_status")).toString();
    if (profile.isEmpty()) { finishUnknown([] { return tr("正在检查当前游戏版本能否自动记录…"); }); return; }
    const bool pathUnreadable = c.contains(QStringLiteral("install_path_readable"))
                                && !c.value(QStringLiteral("install_path_readable")).toBool();
    const bool npcapMissing = c.contains(QStringLiteral("npcap_installed"))
                              && !c.value(QStringLiteral("npcap_installed")).toBool();
    const bool regionUnknown = c.contains(QStringLiteral("region"))
        && (c.value(QStringLiteral("region")).toString().isEmpty()
            || c.value(QStringLiteral("region")).toString() == QLatin1String("UNKNOWN"));
    const bool buildUnknown = c.contains(QStringLiteral("game_build"))
        && c.value(QStringLiteral("game_build")).toString().isEmpty();
    if (!c.contains(QStringLiteral("install_path_readable")) && buildUnknown) {
        // GetCaptureStatus/事件没有路径可读性，须等待完整 GetStatus 才能区分
        // 安装位置不可读和可读目录没有区服标记；缺字段不能当成路径可读。
        finishUnknown([] { return tr("正在检查游戏安装位置、区服和版本，请稍候…"); });
        return;
    }
    // 本机校准 outranks "协议档案不匹配": a mismatched profile is the situation
    // calibration exists for. An unreadable install path still wins - nothing can
    // be calibrated for an unknown build - and so does a missing Npcap, with no
    // packets to learn from.
    if (!pathUnreadable && !npcapMissing && !regionUnknown && !buildUnknown
        && profile != QLatin1String("VERIFIED") && projectCalibration(c))
        return;
    if (profile != QLatin1String("VERIFIED")) {
        const QString reason = pathUnreadable ? QStringLiteral("path")
            : regionUnknown ? QStringLiteral("region") : buildUnknown ? QStringLiteral("build")
            : QStringLiteral("profile:") + profile;
        block(session + reason, [pathUnreadable, regionUnknown, buildUnknown] {
            if (regionUnknown && !pathUnreadable)
                return tr("无法识别游戏区服，暂时不能自动记录。请在「设置 → 通用」中选择实际区服（国服 / 国际服），保存后会重新检查。");
            if (buildUnknown && !pathUnreadable)
                return tr("无法读取游戏版本，暂时不能自动记录。请确认游戏文件完整，再点「重新检测游戏」。");
            return pathUnreadable
                // 路径取自内核进程表，本就不需要额外权限，提权对此没有帮助，
                // 因此这里不再建议以管理员身份运行（审查 2026-09-21 第 1 条）。
                ? tr("找不到游戏的安装位置，无法确认游戏版本，暂时不能自动记录。请确认游戏已启动，再查看「捕获诊断」。")
                : tr("还没有适用于当前游戏版本的支持文件，暂时不能自动记录。请查看「捕获诊断」，期间可以先手动记录。");
        }); return;
    }
    if (!initError.isEmpty()) {
        block(session + (m_followError.isEmpty() ? QStringLiteral("validation") : QStringLiteral("follow")), initError);
        return;
    }
    if (!m_followReady || !m_validationReady) {
        finishUnknown([] { return tr("正在准备自动记录，请稍候…"); }); return;
    }
    if (c.contains(QStringLiteral("npcap_installed")) && !c.value(QStringLiteral("npcap_installed")).toBool()) {
        block(session + QStringLiteral("npcap"), [] { return tr("尚未安装 Npcap 驱动，暂时不能自动记录。请按「捕获诊断」中的说明安装。"); }); return;
    }
    const QString error = c.value(QStringLiteral("last_error_code")).toString();
    const QString reason = silentReason(c);
    if (c.value(QStringLiteral("midstream_suspected")).toBool()
        || reason == QLatin1String("MIDSTREAM")) {
        block(session + QStringLiteral("capture:midstream"), [c, reason] { return silentMessage(c, reason); }); return;
    }
    if (!error.isEmpty() && error != QLatin1String("NONE")) {
        // A player cannot act on "ERR_DB_BUSY". The sentence is the whole
        // explanation; the raw token only ever trails it, and only for a code
        // this build has no wording for.
        block(session + QStringLiteral("capture:") + error, [error] {
            const QString detail = Formatters::captureErrorKnown(error)
                ? Formatters::captureErrorLabel(error)
                : tr("%1（代码 %2）").arg(Formatters::captureErrorLabel(error), error);
            return tr("暂时不能自动记录。%1 请查看「捕获诊断」。").arg(detail);
        }); return;
    }
    if (c.value(QStringLiteral("state")).toString() != QLatin1String("RUNNING")) {
        finishUnknown([] { return tr("正在启动自动记录服务，请稍候…"); }); return;
    }
    if (!reason.isEmpty()) {
        // RUNNING is a fact about a socket, not about the game: a capture that
        // decodes nothing must not be shown as a green 「自动监听中」.
        clearIncident();
        setState(QStringLiteral("listening_silent"), [c, reason] { return silentMessage(c, reason); }); return;
    }
    clearIncident();
    // A profile this machine calibrated is a VERIFIED profile like any other,
    // but the user should be able to see where it came from without opening
    // the diagnostics page.
    const QString origin = c.value(QStringLiteral("profile_origin")).toString();
    if (origin == QLatin1String("LOCAL_CALIBRATION")) {
        setState(QStringLiteral("listening"),
                 [] { return tr("本机校准已完成，自动记录已开启。"); }); return;
    }
    if (origin == QLatin1String("SHARED_CALIBRATION")) {
        setState(QStringLiteral("listening"),
                 [] { return tr("共享校准已启用，自动记录已开启。"); }); return;
    }
    setState(QStringLiteral("listening"), [] { return tr("自动记录已开启，正在读取游戏数据。完成后的记录可在「历史记录」中查看。"); });
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
        clearIncident();
        setState(QStringLiteral("calibrating"), [error] {
            const QString detail = (!error.isEmpty() && error != QLatin1String("NONE"))
                ? tr("暂时读不到游戏数据（%1），正在重试。请查看「捕获诊断」。").arg(Formatters::captureErrorLabel(error))
                : tr("正在连接游戏，暂时不能自动记录。");
            return tr("游戏更新了，需要重新校准。%1").arg(detail);
        });
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
                     [] { return tr("共享校准已通过检查。请在「捕获诊断」的「共享校准」中阅读说明，点「同意，开始记录」。"); });
            return true;
        }
        // The four progress ticks live on the capture page's calibration card. This
        // sentence also appears in the dashboard's fixed-height 当前导随 card, so it
        // carries no progress suffix.
        setState(QStringLiteral("calibrating"), [] {
            return tr("游戏更新了，正在重新校准。"
                      "请照常完成随机任务，再按提示核对。完成前暂时不能自动记录。");
        });
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
                 [required] { return tr("还差最后一步：核对 %1 件事，就能开始自动记录。").arg(required); });
        return true;
    }
    if (state == QLatin1String("BLOCKED")) {
        // The Collector's blockers are already written for the player
        // (section 2.2); repeating the first one verbatim is the whole message.
        const auto blockers = calibration.value(QStringLiteral("blockers")).toList();
        const QString first = blockers.isEmpty() ? QString() : blockers.first().toString();
        // blocker_refs parallels blockers; a list of another length is ignored as a
        // whole (contracts/collector-texts.md §3).
        const auto refs = calibration.value(QStringLiteral("blocker_refs")).toList();
        const QVariant firstRef = refs.size() == blockers.size() && !refs.isEmpty() ? refs.first() : QVariant();
        clearIncident();
        setState(QStringLiteral("calibration_blocked"), [first, firstRef] {
            return first.isEmpty() ? tr("校准遇到了问题，暂时不能自动记录。请查看「捕获诊断」。")
                                   : tr("%1 请查看「捕获诊断」。").arg(collector_text::render(firstRef, first));
        });
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
    // hint_ref renders it in the interface language (contracts/collector-texts.md).
    const QString hint = c.value(QStringLiteral("hint")).toString();
    if (!hint.isEmpty())
        return collector_text::render(c.value(QStringLiteral("hint_ref")), hint);
    if (reason == QLatin1String("MIDSTREAM"))
        return tr("软件开始读取时游戏已经登录，暂时读不到游戏数据。"
                  "请回到标题画面重新登录，或先开本软件再开游戏。");
    if (reason == QLatin1String("NO_PACKETS_ON_ADAPTER"))
        return tr("所选网卡上没有游戏流量（可能在用加速器/VPN），"
                  "请在「捕获诊断」点「重新检测网卡」，再按网卡提示操作。");
    return tr("正在监听，但还没有收到可识别的游戏数据。");
}
}
