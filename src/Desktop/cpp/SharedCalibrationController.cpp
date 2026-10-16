#include "SharedCalibrationController.h"

#include "CalibrationController.h"
#include "CollectorText.h"
#include "IBackend.h"
#include "LiveText.h"

#include <QClipboard>
#include <QDesktopServices>
#include <QGuiApplication>
#include <QVariantList>

#include <utility>

namespace mr {
namespace {

bool allManual(const QVariantList &candidates)
{
    if (candidates.isEmpty())
        return false;
    for (const auto &value : candidates) {
        if (value.toMap().value(QStringLiteral("source")).toString() != QLatin1String("MANUAL"))
            return false;
    }
    return true;
}

/// Where the candidates still in the running came from, as one word
/// (plans/shared-calibration.md §18.3). Rejected candidates are left out: they no
/// longer decide anything the player is told. Empty means no candidate reported a
/// provenance - a Collector before 1.1.0, or a profile restored from disk whose code
/// was not recovered - and then the wording from before this field stands unchanged.
QString provenanceOf(const QVariantList &candidates)
{
    int considered = 0;
    int imported = 0;
    for (const auto &value : candidates) {
        const QVariantMap candidate = value.toMap();
        if (candidate.value(QStringLiteral("status")).toString() == QLatin1String("REJECTED"))
            continue;
        ++considered;
        const QString provenance = candidate.value(QStringLiteral("provenance")).toString();
        if (provenance == QLatin1String("PUBLISHED"))
            return QStringLiteral("published");
        if (provenance == QLatin1String("IMPORTED"))
            ++imported;
    }
    return considered > 0 && imported == considered ? QStringLiteral("imported") : QString();
}

/// Which gate the candidates still in the running are judged by now, as one word
/// (plans/shared-calibration.md §18.3; audit 2026-10-03, ON1-1). The gate, not the
/// provenance: a published code is used on the login check alone only while nothing
/// usable records, it carries no conflict mark and no other code has more submitters;
/// otherwise the Collector reports its match criterion as REQUIRED, exactly as for a
/// pasted code no index knows. "login" when one candidate may be used on the login
/// check (its match criterion is AUDITed), "queue" when every one must first see a
/// queue on this machine, empty while that cannot be told - a Collector before 1.1.0
/// reports no gate, and a candidate not judged yet has no criteria.
QString gateOf(const QVariantList &candidates)
{
    int considered = 0;
    int queue = 0;
    for (const auto &value : candidates) {
        const QVariantMap candidate = value.toMap();
        if (candidate.value(QStringLiteral("status")).toString() == QLatin1String("REJECTED"))
            continue;
        ++considered;
        QString gate;
        for (const auto &criterion : candidate.value(QStringLiteral("criteria")).toList()) {
            const QVariantMap row = criterion.toMap();
            if (row.value(QStringLiteral("message")).toString() == QLatin1String("CONTENT_FINDER_POP"))
                gate = row.value(QStringLiteral("gate")).toString();
        }
        if (gate == QLatin1String("AUDIT"))
            return QStringLiteral("login");
        // A pasted code no index knows is never judged any other way.
        if (gate == QLatin1String("REQUIRED")
            || candidate.value(QStringLiteral("provenance")).toString() == QLatin1String("IMPORTED"))
            ++queue;
    }
    return considered > 0 && queue == considered ? QStringLiteral("queue") : QString();
}

/// The profile in use - listed first (SharedCalibrationStatus.candidates) - is one the
/// Collector will hand out for 这份校准好用，告诉其他玩家 (plans/shared-calibration.md §20.6):
///   * status PROVEN with its own audit_pending false: it recorded one complete duty and
///     nothing about it is still audited;
///   * provenance PUBLISHED: a pasted code no index lists can prove itself too, but the
///     Collector keeps no text for it and refuses every click (SHARED);
///   * the candidate carries `preferred`, which only a §20 Collector sends. Collectors
///     1.1.0-1.7.2 already report PROVEN for a bound shared profile and still refuse
///     GetCalibrationShareCode for it, so without this the button would only ever fail.
/// Only that candidate counts; a later one is at most being checked, and the values of the
/// disputed / preferred / shape_prior marks (§20.5) decide nothing a player is offered.
bool provenInUse(const QVariantList &candidates)
{
    if (candidates.isEmpty())
        return false;
    const QVariantMap first = candidates.constFirst().toMap();
    return first.value(QStringLiteral("status")).toString() == QLatin1String("PROVEN")
        && !first.value(QStringLiteral("audit_pending")).toBool()
        && first.value(QStringLiteral("provenance")).toString() == QLatin1String("PUBLISHED")
        && first.contains(QStringLiteral("preferred"));
}

QString outcomeOf(const QVariantMap &payload)
{
    return payload.value(QStringLiteral("outcome")).toString();
}

} // namespace

SharedCalibrationController::SharedCalibrationController(QObject *parent)
    : QObject(parent)
    , m_openUrl([](const QUrl &url) { return QDesktopServices::openUrl(url); })
    , m_copy([](const QString &text) {
        if (auto *clipboard = QGuiApplication::clipboard())
            clipboard->setText(text);
    })
{
    // Every sentence here is built from m_inputs when it is read.
    onLanguageChanged(this, [this] { Q_EMIT changed(); });
}

void SharedCalibrationController::setBackend(IBackend *backend)
{
    m_backend = backend;
}

void SharedCalibrationController::setUrlOpener(UrlOpener opener)
{
    if (opener)
        m_openUrl = std::move(opener);
}

void SharedCalibrationController::setClipboardWriter(ClipboardWriter writer)
{
    if (writer)
        m_copy = std::move(writer);
}

// -- projection --------------------------------------------------------------

void SharedCalibrationController::refreshFromCaptureStatus(const QVariantMap &capture)
{
    const QVariantMap calibration = capture.value(QStringLiteral("calibration")).toMap();
    const QVariant raw = calibration.value(QStringLiteral("shared"));
    const QVariantMap shared = raw.toMap();

    Inputs next;
    next.available = raw.isValid() && !raw.isNull() && !shared.isEmpty();
    next.userRejected = shared.value(QStringLiteral("user_rejected")).toBool();
    const QVariantList candidates = shared.value(QStringLiteral("candidates")).toList();
    next.manualOnly = allManual(candidates);
    next.provenance = provenanceOf(candidates);
    next.gate = gateOf(candidates);
    next.auditPending = shared.value(QStringLiteral("audit_pending")).toBool();
    next.inUseProven = provenInUse(candidates);
    next.phase = shared.value(QStringLiteral("phase")).toString();
    next.lastFetchStatus = shared.value(QStringLiteral("last_fetch_status")).toString();
    const QString state = calibration.value(QStringLiteral("state")).toString();
    next.calibrationState = state.isEmpty() ? QStringLiteral("IDLE") : state;
    next.profileOrigin = capture.value(QStringLiteral("profile_origin")).toString();
    next.profileStatus = capture.value(QStringLiteral("profile_status")).toString();

    if (next == m_inputs)
        return;
    m_inputs = next;
    Q_EMIT changed();
}

bool SharedCalibrationController::calibrating() const
{
    return m_inputs.calibrationState == QLatin1String("OBSERVING")
        || m_inputs.calibrationState == QLatin1String("WAITING");
}

bool SharedCalibrationController::inUse() const
{
    return m_inputs.profileOrigin == QLatin1String("SHARED_CALIBRATION");
}

bool SharedCalibrationController::recordingAlready() const
{
    // Keyed on the profile status, like the banner and the chain panel: whatever wrote the profile in
    // force, a VERIFIED one records, and a candidate being checked underneath it would only replace it.
    return m_inputs.profileStatus == QLatin1String("VERIFIED");
}

QString SharedCalibrationController::view() const
{
    // A refusal also reports REJECTED, so the player's own choice is read first: the
    // card must say "as you chose", never "it did not match".
    if (m_inputs.available && m_inputs.userRejected)
        return QStringLiteral("user_rejected");
    if (inUse())
        return QStringLiteral("verified");
    if (!m_inputs.available)
        return QStringLiteral("none");
    static const QList<std::pair<QLatin1String, QLatin1String>> phases{
        {QLatin1String("FETCHING"), QLatin1String("fetching")},
        {QLatin1String("VERIFYING"), QLatin1String("verifying")},
        {QLatin1String("AWAITING_CONSENT"), QLatin1String("consent")},
        {QLatin1String("VERIFIED"), QLatin1String("verified")},
        {QLatin1String("REJECTED"), QLatin1String("rejected")},
        {QLatin1String("UNAVAILABLE"), QLatin1String("unavailable")}};
    for (const auto &[phase, view] : phases) {
        if (m_inputs.phase == phase)
            return view;
    }
    // NONE: nobody shared this build, the download is switched off, or nothing ran yet.
    if (m_inputs.lastFetchStatus == QLatin1String("NONE_FOR_BUILD"))
        return QStringLiteral("none_for_build");
    if (m_inputs.lastFetchStatus == QLatin1String("DISABLED"))
        return QStringLiteral("disabled");
    return QStringLiteral("none");
}

QString SharedCalibrationController::headline() const
{
    const QString current = view();
    if (current == QLatin1String("fetching"))
        return tr("正在查找其他玩家分享的校准，本机校准也会继续。");
    if (current == QLatin1String("verifying")) {
        // Something records already - this machine's own queue-inferred calibration, or a shared one -
        // and the candidate would replace it rather than start recording. Since 1.4.0 the index is read
        // in exactly that situation, so the promise below would be false here.
        if (recordingAlready()) {
            return m_inputs.provenance == QLatin1String("imported")
                ? tr("校准码已导入，正在检查是否可用。自动记录照常进行。")
                : tr("找到了更完整的共享校准，正在检查是否可用。自动记录照常进行。");
        }
        // §18.3: a code bound at the login burst is a matter of logging in; any other -
        // a pasted code no index knows, or since audit 2026-10-03 (ON1-1) a published
        // one that is outranked, marked as conflicting or would displace a usable
        // calibration - waits for one queue and one duty as well. The gate the
        // Collector reports decides, not where the code came from. Without one (an
        // older Collector, or a code not judged yet) neither promise can be made, and
        // the sentence from before the split stands.
        if (m_inputs.provenance == QLatin1String("imported"))
            return tr("校准码已导入。登录后申请随机任务并进入副本，通过检查后才能开始记录。");
        if (m_inputs.gate == QLatin1String("login"))
            return tr("找到了共享校准，登录时会自动检查，通过后就能开始记录。");
        if (m_inputs.gate == QLatin1String("queue"))
            return tr("找到了共享校准。申请随机任务并进入副本，通过检查后才能开始记录。");
        return m_inputs.manualOnly ? tr("校准码已导入。登录或排本时会自动检查是否可用。")
                                   : tr("找到了共享校准，登录或排本时会自动检查是否可用。");
    }
    if (current == QLatin1String("consent"))
        return tr("共享校准已通过检查，请确认是否使用。");
    if (current == QLatin1String("verified")) {
        // §18.4: it records because the login burst matched; saying "本机已核实" while the
        // match and the duty entry are still being audited would promise more than that.
        return m_inputs.auditPending ? tr("正在使用共享校准，登录检查已通过。")
                                     : tr("正在使用共享校准，已通过本机检查。");
    }
    if (current == QLatin1String("rejected"))
        return tr("这份共享校准不适用于你的游戏，已改为本机校准。");
    if (current == QLatin1String("unavailable"))
        return tr("暂时没能下载共享校准，将继续本机校准。");
    if (current == QLatin1String("none_for_build"))
        return tr("暂时没有当前游戏版本的共享校准，将继续本机校准。");
    if (current == QLatin1String("disabled"))
        return tr("自动获取共享校准已关闭，将继续本机校准。");
    if (current == QLatin1String("user_rejected"))
        return tr("已改为本机校准，当前游戏版本不再使用共享校准。");
    return {};
}

QString SharedCalibrationController::detail() const
{
    const QString current = view();
    if (current == QLatin1String("verifying")) {
        if (recordingAlready())
            return tr("自动记录照常进行。检查通过后会自动换用新的共享校准，已保存的记录不会改动。");
        return tr("检查通过后才能开始自动记录。照常游戏即可；如果不能使用，会继续本机校准，已有进度会保留。");
    }
    if (current == QLatin1String("verified")) {
        // Grey, not orange: nothing is wrong, and the player has nothing to do about it.
        return m_inputs.auditPending
            ? tr("排本和进本还在核对中，记录照常进行。如果之后发现不匹配，会停止使用这份校准并继续本机校准，"
                 "期间生成的记录会标为“待复核”，请检查并补正。")
            : tr("已确认这份共享校准能在你的游戏中使用，照常游戏即可。");
    }
    if (current == QLatin1String("rejected"))
        return tr("本机校准会继续，已有进度会保留，照常游戏即可。");
    if (current == QLatin1String("unavailable"))
        return tr("稍后可点「立即检查」重试。也可以向已完成校准的玩家索取校准码，再点「导入校准码」。");
    if (current == QLatin1String("none_for_build"))
        return tr("本机校准完成后，可以点「分享给其他玩家」，让同一游戏版本的玩家也能使用。");
    if (current == QLatin1String("disabled"))
        return tr("想自动查找共享校准，可在设置中开启「游戏更新后获取其他玩家的共享校准」。也可以直接导入校准码。");
    if (current == QLatin1String("user_rejected") && calibrating())
        return tr("想重新使用共享校准，点「清空校准进度」。本机尚未完成的校准进度也会一起清空。");
    if (current == QLatin1String("none") && canImport())
        return tr("有其他玩家发来的校准码，可以点「导入校准码」。通过检查后才会使用。");
    return {};
}

bool SharedCalibrationController::canCheck() const
{
    static const QStringList views{QStringLiteral("none"), QStringLiteral("disabled"),
                                   QStringLiteral("unavailable"), QStringLiteral("none_for_build"),
                                   QStringLiteral("rejected")};
    return m_inputs.available && calibrating() && !m_inputs.userRejected && !inUse()
        && views.contains(view());
}

bool SharedCalibrationController::canImport() const
{
    // The consent view is where importing matters most, not least. A downloaded
    // queue-inferred code that passed verification parks the card there, and the two
    // answers on offer are "bind this weaker code" and "refuse every shared code until
    // 重新观察" - after which the same code is downloaded again. A player holding a
    // friend's better code had no way in at all. The Collector never refused an import
    // in this phase (SharedCalibrationSession.Import); only this card did.
    return m_inputs.available && calibrating() && !m_inputs.userRejected && !inUse();
}

bool SharedCalibrationController::canReject() const
{
    static const QStringList views{QStringLiteral("verifying"), QStringLiteral("consent"),
                                   QStringLiteral("verified")};
    return m_inputs.available && !m_inputs.userRejected && views.contains(view());
}

bool SharedCalibrationController::canShare() const
{
    return !shareKind().isEmpty();
}

QString SharedCalibrationController::shareKind() const
{
    // The profile in force decides, not whether calibration happens to be running:
    // GetCalibrationShareCode answers for whatever records right now, and requiring
    // calibration.state == DONE as well would take the entry away after a restart.
    if (!m_inputs.available)
        return {};
    if (m_inputs.profileOrigin == QLatin1String("LOCAL_CALIBRATION"))
        return QStringLiteral("local");
    // Another player's calibration is offered only once it has proven itself here (§20.6):
    // the Collector answers with its code then, and refuses with SHARED_NOT_PROVEN before.
    // The shipped profile is never ours to pass on.
    if (inUse() && m_inputs.inUseProven && !m_inputs.auditPending)
        return QStringLiteral("verified");
    return {};
}

QString SharedCalibrationController::shareButtonText() const
{
    const QString kind = shareKind();
    if (kind == QLatin1String("local"))
        return tr("分享给其他玩家");
    if (kind == QLatin1String("verified"))
        return tr("这份校准好用，告诉其他玩家");
    return {};
}

QString SharedCalibrationController::shareHintText() const
{
    const QString kind = shareKind();
    if (kind == QLatin1String("local"))
        return shareHint();
    if (kind == QLatin1String("verified"))
        return verifiedShareHint();
    return {};
}

QString SharedCalibrationController::consentText()
{
    // The last sentence is the third answer. Without it the box reads as a choice between
    // accepting this code and giving up on shared calibration for the build, which is what
    // left a player with a better code in hand stuck between the two.
    // One whole sentence for the translators: the clause is the one
    // CalibrationController::queueInferenceText() names on its own.
    return tr("这份校准会根据你申请的随机任务和实际进入的副本来记录，记录中的匹配时间使用你申请排队的时间。"
              "取消排队不会生成记录。这是目前可用的基础记录方式。同意后，当前游戏版本不会再询问。"
              "如果你有其他玩家发来的校准码，也可以先点「导入校准码」。");
}

QString SharedCalibrationController::shareHint()
{
    return tr("可以把本机校准分享给同一游戏版本的玩家。点击后会打开 GitHub 分享页面，"
              "并复制校准码。是否提交由你决定，软件不会自动上传。");
}

QString SharedCalibrationController::verifiedShareHint()
{
    return tr("会打开 GitHub 页面，把这份校准记为你也核实过，让其他玩家的软件更优先使用它。");
}

QString SharedCalibrationController::shareRefusalMessage(const QString &reason)
{
    if (reason == QLatin1String("NO_PROFILE"))
        return tr("还没有可分享的本机校准，请先完成校准。");
    if (reason == QLatin1String("NOT_LOCAL"))
        return tr("当前使用的是软件自带的校准，其他玩家也已附带，无需分享。");
    // §20.5: a shared profile in use is shared once it is proven; before that it is refused
    // as not proven yet, and SHARED is left for a code whose text is not on this machine.
    if (reason == QLatin1String("SHARED_NOT_PROVEN"))
        return tr("这份共享校准还在核对中，等它记录过一次完整的随机任务后再试。");
    if (reason == QLatin1String("SHARED"))
        return tr("这份共享校准在本机找不到对应的校准码，暂时无法分享。");
    return tr("本机校准文件无法读取或已被修改，暂时不能分享。");
}

bool SharedCalibrationController::isShareIssueUrl(const QUrl &url)
{
    return url.isValid() && url.scheme() == QLatin1String("https")
        && url.host() == QLatin1String("github.com") && url.userInfo().isEmpty()
        && url.port() == -1;
}

// -- requests ----------------------------------------------------------------

bool SharedCalibrationController::begin()
{
    if (m_busy || !m_backend)
        return false;
    m_busy = true;
    Q_EMIT changed();
    return true;
}

void SharedCalibrationController::end()
{
    m_busy = false;
    Q_EMIT changed();
}

void SharedCalibrationController::checkNow()
{
    if (!begin())
        return;
    m_backend->checkSharedCalibration()->whenDone(this,
        [this](bool ok, const QVariantMap &payload, const QString &, const QString &message) {
        end();
        if (!ok) {
            Q_EMIT notice(message.isEmpty() ? tr("没能检查共享校准，请稍后重试。") : message);
            return;
        }
        const QString outcome = outcomeOf(payload);
        if (outcome == QLatin1String("STARTED"))
            Q_EMIT notice(tr("正在查找共享校准，结果会显示在「共享校准」中。"));
        else if (outcome == QLatin1String("ALREADY_FETCHING"))
            Q_EMIT notice(tr("正在查找，请稍等。结果会显示在「共享校准」中。"));
        // 刚查过：此时并没有下载在途，所以不能说「结果会显示在卡片上」——那个结果
        // 上一次就已经出来了（审查 2026-09-21 第 13 条）。
        else if (outcome == QLatin1String("RECENTLY_CHECKED"))
            Q_EMIT notice(tr("刚刚检查过，请稍后重试。上次结果已显示在「共享校准」中。"));
        else if (outcome == QLatin1String("DISABLED"))
            Q_EMIT notice(tr("请先在设置中开启「游戏更新后获取其他玩家的共享校准」。也可以直接导入校准码。"));
        else
            Q_EMIT notice(tr("暂时不需要查找：已有可用校准，或你已选择当前游戏版本只用本机校准。"));
        Q_EMIT refreshRequested();
    });
}

void SharedCalibrationController::share()
{
    if (!begin())
        return;
    BackendReply *reply = m_backend->getCalibrationShareCode();
    const QPointer<BackendReply> guard(reply);
    reply->whenDone(this,
        [this, guard](bool ok, const QVariantMap &payload, const QString &code, const QString &message) {
        // whenDone runs before the reply deletes itself, so its details are still there.
        const QVariantMap details = guard ? guard->errorDetails() : QVariantMap();
        end();
        if (ok)
            deliverShareCode(payload);
        else
            explainShareFailure(code, message, details);
    });
}

void SharedCalibrationController::deliverShareCode(const QVariantMap &payload)
{
    const QString code = payload.value(QStringLiteral("code")).toString();
    if (code.isEmpty()) {
        Q_EMIT notice(tr("没能生成校准码，请稍后重试。"));
        return;
    }
    // VERIFIED: another player's code this machine proved (§20.6). Submitting it adds an
    // account that checked it rather than a new code, and the sentences say so. CALIBRATED,
    // or no basis at all from a Collector before §20, keeps the sharing wording.
    const bool verified = payload.value(QStringLiteral("basis")).toString() == QLatin1String("VERIFIED");
    // The clipboard first: whatever happens with the browser, the player can still
    // hand the code to someone else.
    m_copy(code);
    if (!payload.value(QStringLiteral("code_in_url")).toBool()) {
        Q_EMIT notice(verified
            ? tr("校准码已复制到剪贴板。由于内容较长，无法自动打开 GitHub 页面。"
                 "你可以把这份校准直接发给其他玩家，让对方点「导入校准码」并粘贴。")
            : tr("校准码已复制到剪贴板。由于内容较长，无法自动打开 GitHub 分享页面。"
                 "你可以直接把它发给其他玩家，让对方点「导入校准码」并粘贴。"));
        return;
    }
    const QUrl url(payload.value(QStringLiteral("issue_url")).toString(), QUrl::StrictMode);
    if (!isShareIssueUrl(url)) {
        Q_EMIT notice(verified
            ? tr("校准码已复制到剪贴板。GitHub 页面地址有误，未打开浏览器；可以把这份校准直接发给其他玩家。")
            : tr("校准码已复制到剪贴板。分享页面地址有误，未打开浏览器；可以直接把校准码发给其他玩家。"));
        return;
    }
    if (!m_openUrl(url)) {
        Q_EMIT notice(tr("没能打开浏览器。校准码已复制，可以直接发给其他玩家。"));
        return;
    }
    Q_EMIT notice(verified
        ? tr("已打开 GitHub 页面，校准码也已复制到剪贴板。"
             "提交后这份共享校准会多一个核实通过的账号，软件不会自动上传。")
        : tr("已打开 GitHub 分享页面，校准码也已复制到剪贴板。"
             "请在浏览器中确认是否提交，软件不会自动上传。"));
}

void SharedCalibrationController::explainShareFailure(const QString &code, const QString &message,
                                                      const QVariantMap &details)
{
    if (code == QLatin1String("ERR_SHARE_CODE_UNAVAILABLE")) {
        // The Collector's message can name a file; the reason token is all the player needs.
        Q_EMIT notice(shareRefusalMessage(details.value(QStringLiteral("reason")).toString()));
        return;
    }
    Q_EMIT notice(message.isEmpty() ? tr("没能生成校准码，请稍后重试。") : message);
}

void SharedCalibrationController::importCode(const QString &text)
{
    const QString code = text.trimmed();
    if (code.isEmpty()) {
        Q_EMIT importFinished(false, tr("请先粘贴完整的校准码。"));
        return;
    }
    if (!begin()) {
        // The dialog waits for an answer; never leave it without one.
        Q_EMIT importFinished(false, tr("暂时无法导入，请稍后重试。"));
        return;
    }
    m_backend->importCalibrationCode(code)->whenDone(this,
        [this](bool ok, const QVariantMap &payload, const QString &, const QString &message) {
        end();
        if (!ok) {
            Q_EMIT importFinished(false, message.isEmpty() ? tr("没能导入校准码，请稍后重试。") : message);
            return;
        }
        const bool applied = outcomeOf(payload) == QLatin1String("APPLIED");
        // The Collector's sentence, in the interface language where message_ref allows
        // (contracts/collector-texts.md). The dialog and the toast show it at once.
        QString sentence = collector_text::render(payload.value(QStringLiteral("message_ref")),
                                                  payload.value(QStringLiteral("message")).toString());
        if (sentence.isEmpty())
            sentence = applied ? tr("校准码已导入。登录或排本时会自动检查是否可用。") : tr("这份校准码无法使用，请换一个校准码重试。");
        Q_EMIT importFinished(applied, sentence);
        if (applied) {
            Q_EMIT notice(sentence);
            Q_EMIT refreshRequested();
        }
    });
}

void SharedCalibrationController::acceptQueueInference()
{
    if (!begin())
        return;
    m_backend->acceptSharedQueueInference()->whenDone(this,
        [this](bool ok, const QVariantMap &payload, const QString &, const QString &message) {
        end();
        if (!ok) {
            Q_EMIT notice(message.isEmpty() ? tr("没能保存你的选择，请稍后重试。") : message);
            return;
        }
        Q_EMIT notice(outcomeOf(payload) == QLatin1String("ACCEPTED")
                          ? tr("已同意使用。启用这份共享校准后会开始自动记录，照常游戏即可。")
                          : tr("共享校准的状态已变化，请查看「共享校准」中的最新提示。"));
        Q_EMIT refreshRequested();
    });
}

void SharedCalibrationController::reject()
{
    if (!begin())
        return;
    m_backend->rejectSharedCalibration()->whenDone(this,
        [this](bool ok, const QVariantMap &payload, const QString &, const QString &message) {
        end();
        if (!ok) {
            Q_EMIT notice(message.isEmpty() ? tr("没能改为本机校准，请稍后重试。") : message);
            return;
        }
        // The withdrawn profile id and the count are diagnostics; the sentence only says
        // which of the three things happened.
        const QVariant withdrawn = payload.value(QStringLiteral("withdrawn_profile_id"));
        if (withdrawn.isValid() && !withdrawn.isNull() && !withdrawn.toString().isEmpty())
            Q_EMIT notice(tr("已停止使用共享校准，正在进行本机校准。已保存的记录不受影响。"));
        else if (payload.value(QStringLiteral("dropped_candidates")).toInt() > 0)
            Q_EMIT notice(tr("已取消共享校准的检查，正在继续本机校准。"));
        else
            Q_EMIT notice(tr("已改为本机校准，当前游戏版本不再使用共享校准。"));
        Q_EMIT refreshRequested();
    });
}

} // namespace mr
