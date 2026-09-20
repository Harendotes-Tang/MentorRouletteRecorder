#include "SharedCalibrationController.h"

#include "CalibrationController.h"
#include "IBackend.h"

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

/// Which gate set the candidates still in the running are judged by, as one word
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
    next.auditPending = shared.value(QStringLiteral("audit_pending")).toBool();
    next.phase = shared.value(QStringLiteral("phase")).toString();
    next.lastFetchStatus = shared.value(QStringLiteral("last_fetch_status")).toString();
    const QString state = calibration.value(QStringLiteral("state")).toString();
    next.calibrationState = state.isEmpty() ? QStringLiteral("IDLE") : state;
    next.profileOrigin = capture.value(QStringLiteral("profile_origin")).toString();

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
        return tr("正在获取其他玩家的共享校准，本机校准照常进行。");
    if (current == QLatin1String("verifying")) {
        // §18.3: a code an index lists binds at the login burst, so it is a matter of
        // logging in; a pasted code no index knows waits for one queue and one duty as
        // well. Without a provenance (an older Collector) neither promise can be made,
        // and the sentence from before the split stands.
        if (m_inputs.provenance == QLatin1String("imported"))
            return tr("已导入校准码，登录并排一次本、核实通过后启用。");
        if (m_inputs.provenance == QLatin1String("published"))
            return tr("找到共享校准，登录时自动核实，通过就开始记录。");
        return m_inputs.manualOnly ? tr("已导入校准码，登录或排本时自动核实。")
                                   : tr("找到共享校准，登录或排本时自动核实。");
    }
    if (current == QLatin1String("consent"))
        return tr("共享校准核实通过了，还需要你同意一次才能开始记录。");
    if (current == QLatin1String("verified")) {
        // §18.4: it records because the login burst matched; saying "本机已核实" while the
        // match and the duty entry are still being audited would promise more than that.
        return m_inputs.auditPending ? tr("已使用其他玩家分享的校准（登录时已在本机核实）。")
                                     : tr("已使用其他玩家分享的校准（本机已核实）。");
    }
    if (current == QLatin1String("rejected"))
        return tr("共享校准与本机流量对不上，已改为本机校准。");
    if (current == QLatin1String("unavailable"))
        return tr("没取到共享校准（网络不通），继续本机校准。");
    if (current == QLatin1String("none_for_build"))
        return tr("还没有人分享这个版本的校准，继续本机校准。");
    if (current == QLatin1String("disabled"))
        return tr("获取共享校准已关闭，继续本机校准。");
    if (current == QLatin1String("user_rejected"))
        return tr("已按你的选择改为本机校准，这个游戏版本不再使用共享校准。");
    return {};
}

QString SharedCalibrationController::detail() const
{
    const QString current = view();
    if (current == QLatin1String("verifying"))
        return tr("核实通过才会开始记录，期间照常游戏即可；对不上就继续本机校准，已经攒下的进度不受影响。");
    if (current == QLatin1String("verified")) {
        // Grey, not orange: nothing is wrong, and the player has nothing to do about it.
        return m_inputs.auditPending
            ? tr("排本和进本还在核对中，照常游戏即可；万一对不上，会自动改回本机校准，"
                 "这期间生成的记录会标记待复核。")
            : tr("这份校准在这台电脑的流量里核实过，和随软件附带的档案一样用于记录。");
    }
    if (current == QLatin1String("rejected"))
        return tr("本机校准一直在进行，进度没有丢，照常游戏即可。");
    if (current == QLatin1String("unavailable"))
        return tr("可以稍后点「立即检查」再试；也可以请已经校准好的玩家把校准码发给你，用「导入校准码」导入。");
    if (current == QLatin1String("none_for_build"))
        return tr("你校准完成后，可以点「分享给其他玩家」，帮到后面更新游戏的人。");
    if (current == QLatin1String("disabled"))
        return tr("可以在设置里打开「游戏更新后获取其他玩家的共享校准」；手动导入校准码不受影响。");
    if (current == QLatin1String("user_rejected") && calibrating())
        return tr("想撤销这个选择，点「清空进度并重新观察」（本机校准的进度会一起清空）。");
    if (current == QLatin1String("none") && canImport())
        return tr("有其他玩家发来的校准码的话，可以点「导入校准码」，同样先在本机核实再用。");
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
    // The profile in force decides, not whether calibration happens to be running:
    // GetCalibrationShareCode answers for whatever records right now, and requiring
    // calibration.state == DONE as well would take the entry away after a restart.
    // LOCAL_CALIBRATION alone also keeps a shared profile (SHARED_CALIBRATION) and the
    // shipped one out: neither is ours to pass on.
    return m_inputs.available && m_inputs.profileOrigin == QLatin1String("LOCAL_CALIBRATION");
}

QString SharedCalibrationController::consentText()
{
    // The last sentence is the third answer. Without it the box reads as a choice between
    // accepting this code and giving up on shared calibration for the build, which is what
    // left a player with a better code in hand stuck between the two.
    return tr("这份校准没有认出服务器发出的「匹配成功」报文，所以会%1：记录里的匹配时间是你申请排本的时间，"
              "取消排队不会留下记录。本机校准遇到同样的情况也是这样记录的。同意一次，这个游戏版本就不再问。"
              "手上有其他玩家发来的校准码的话，也可以先点「导入校准码」。")
        .arg(CalibrationController::queueInferenceText());
}

QString SharedCalibrationController::shareHint()
{
    return tr("这份校准可以分享给同一游戏版本的其他玩家：会用浏览器打开 GitHub 上预填好的分享页面，"
              "并把校准码复制到剪贴板。提交与否由你在浏览器里决定，本软件自己不上传任何东西。");
}

QString SharedCalibrationController::shareRefusalMessage(const QString &reason)
{
    if (reason == QLatin1String("NO_PROFILE"))
        return tr("现在没有正在使用的档案，本机校准完成之后才能分享。");
    if (reason == QLatin1String("NOT_LOCAL"))
        return tr("现在用的是随软件附带的档案，其他玩家也有，不需要分享。");
    if (reason == QLatin1String("SHARED"))
        return tr("现在用的是其他玩家分享的校准，不能再转手分享。");
    return tr("这份本机校准的档案文件读不出来或被改动过，暂时不能分享。");
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
            Q_EMIT notice(message.isEmpty() ? tr("检查失败，请稍后再试。") : message);
            return;
        }
        const QString outcome = outcomeOf(payload);
        if (outcome == QLatin1String("STARTED"))
            Q_EMIT notice(tr("正在获取其他玩家分享的校准，结果会显示在校准卡片上。"));
        else if (outcome == QLatin1String("ALREADY_FETCHING"))
            Q_EMIT notice(tr("已经在获取了，结果会显示在校准卡片上。"));
        else if (outcome == QLatin1String("DISABLED"))
            Q_EMIT notice(tr("「游戏更新后获取其他玩家的共享校准」没有打开，可以在设置里打开；手动导入校准码不受影响。"));
        else
            Q_EMIT notice(tr("现在不需要获取：已经有可用的档案，或者这个游戏版本你选择了自己校准。"));
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
        Q_EMIT notice(tr("没有拿到校准码，请稍后再试。"));
        return;
    }
    // The clipboard first: whatever happens with the browser, the player can still
    // hand the code to someone else.
    m_copy(code);
    if (!payload.value(QStringLiteral("code_in_url")).toBool()) {
        Q_EMIT notice(tr("校准码已复制到剪贴板。它太长，放不进 GitHub 分享页面的地址，所以这次没有打开浏览器："
                         "可以直接发给其他玩家（比如贴到群里），对方在校准卡片上点「导入校准码」粘贴即可。"));
        return;
    }
    const QUrl url(payload.value(QStringLiteral("issue_url")).toString(), QUrl::StrictMode);
    if (!isShareIssueUrl(url)) {
        Q_EMIT notice(tr("校准码已复制到剪贴板，但分享页面的地址不对，没有打开浏览器；可以直接把校准码发给其他玩家。"));
        return;
    }
    if (!m_openUrl(url)) {
        Q_EMIT notice(tr("无法调用系统浏览器。校准码已复制到剪贴板，可以直接发给其他玩家。"));
        return;
    }
    Q_EMIT notice(tr("已在系统浏览器中打开 GitHub 上的分享页面，校准码也复制到了剪贴板。"
                     "是否提交由你在浏览器里决定，本软件不会上传任何东西。"));
}

void SharedCalibrationController::explainShareFailure(const QString &code, const QString &message,
                                                      const QVariantMap &details)
{
    if (code == QLatin1String("ERR_SHARE_CODE_UNAVAILABLE")) {
        // The Collector's message can name a file; the reason token is all the player needs.
        Q_EMIT notice(shareRefusalMessage(details.value(QStringLiteral("reason")).toString()));
        return;
    }
    Q_EMIT notice(message.isEmpty() ? tr("没能生成校准码，请稍后再试。") : message);
}

void SharedCalibrationController::importCode(const QString &text)
{
    const QString code = text.trimmed();
    if (code.isEmpty()) {
        Q_EMIT importFinished(false, tr("先把校准码粘贴进来。"));
        return;
    }
    if (!begin()) {
        // The dialog waits for an answer; never leave it without one.
        Q_EMIT importFinished(false, tr("现在无法导入，请稍后再试。"));
        return;
    }
    m_backend->importCalibrationCode(code)->whenDone(this,
        [this](bool ok, const QVariantMap &payload, const QString &, const QString &message) {
        end();
        if (!ok) {
            Q_EMIT importFinished(false, message.isEmpty() ? tr("导入失败，请稍后再试。") : message);
            return;
        }
        const bool applied = outcomeOf(payload) == QLatin1String("APPLIED");
        QString sentence = payload.value(QStringLiteral("message")).toString();
        if (sentence.isEmpty())
            sentence = applied ? tr("校准码已导入，登录或排本时会自动核实。") : tr("这份校准码用不了。");
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
            Q_EMIT notice(message.isEmpty() ? tr("没能记下你的同意，请稍后再试。") : message);
            return;
        }
        Q_EMIT notice(outcomeOf(payload) == QLatin1String("ACCEPTED")
                          ? tr("已同意。这份共享校准绑定后就开始记录，照常游戏即可。")
                          : tr("这份共享校准现在不需要同意了。"));
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
            Q_EMIT notice(message.isEmpty() ? tr("没能改为本机校准，请稍后再试。") : message);
            return;
        }
        // The withdrawn profile id and the count are diagnostics; the sentence only says
        // which of the three things happened.
        const QVariant withdrawn = payload.value(QStringLiteral("withdrawn_profile_id"));
        if (withdrawn.isValid() && !withdrawn.isNull() && !withdrawn.toString().isEmpty())
            Q_EMIT notice(tr("已停用其他玩家分享的校准，改为本机校准；已经生成的记录不受影响。"));
        else if (payload.value(QStringLiteral("dropped_candidates")).toInt() > 0)
            Q_EMIT notice(tr("已丢弃正在核实的共享校准，改为本机校准。"));
        else
            Q_EMIT notice(tr("已记下你的选择：这个游戏版本不用共享校准，继续本机校准。"));
        Q_EMIT refreshRequested();
    });
}

} // namespace mr
