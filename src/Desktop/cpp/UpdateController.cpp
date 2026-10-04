#include "UpdateController.h"

#include "AppSettings.h"
#include "CollectorProcess.h"
#include "IBackend.h"
#include "TrayController.h"

#include <QCoreApplication>
#include <QDesktopServices>
#include <QDir>
#include <QMetaType>

#include <utility>

namespace mr {
namespace {

// $defs/UpdateDownload.state; anything else (IDLE, or a state a later Collector
// adds) reads as nothing under way.
const QString kIdle = QStringLiteral("IDLE");
const QString kDownloading = QStringLiteral("DOWNLOADING");
const QString kVerifying = QStringLiteral("VERIFYING");
const QString kReady = QStringLiteral("READY");
const QString kFailed = QStringLiteral("FAILED");

/// A Collector that does not know the message answers one of these.
bool isUnknownMessage(const QString &code)
{
    return code == QLatin1String("ERR_BAD_REQUEST") || code == QLatin1String("ERR_UNKNOWN_MESSAGE");
}

QString megabytes(qint64 bytes)
{
    return QString::number(double(bytes) / (1024.0 * 1024.0), 'f', 1);
}

} // namespace

UpdateController::UpdateController(QObject *parent)
    : QObject(parent)
    , m_openUrl([](const QUrl &url) { return QDesktopServices::openUrl(url); })
    // The shell opens the file, as it opens an address: the installer's own
    // manifest asks Windows for elevation, and a declined prompt is a failed
    // start (false), never a crash of this process.
    , m_launchInstaller([](const QString &path) {
        return QDesktopServices::openUrl(QUrl::fromLocalFile(path));
    })
    , m_quit([] { TrayController::quitApplication(); })
    , m_errorText([](const QString &message, const QString &) { return message; })
    , m_dataDirectory([] { return CollectorProcess::collectorDataDirectory(); })
{
    m_pollTimer.setSingleShot(true);
    m_pollTimer.setInterval(kDownloadPollMs);
    connect(&m_pollTimer, &QTimer::timeout, this, &UpdateController::statusRefreshRequested);
    // Nothing is re-read and nothing is hashed once the application is leaving.
    if (auto *application = QCoreApplication::instance())
        connect(application, &QCoreApplication::aboutToQuit, this, &UpdateController::stopForQuit);
}

UpdateController::~UpdateController() = default;

void UpdateController::setSettings(AppSettings *settings)
{
    m_settings = settings;
    Q_EMIT changed();
}

void UpdateController::setBackend(IBackend *backend)
{
    if (m_backend)
        disconnect(m_backend, nullptr, this, nullptr);
    m_backend = backend;
    if (m_backend) {
        connect(m_backend, &IBackend::connectionChanged, this, [this] {
            if (!m_backend)
                return;
            if (!m_backend->isConnected()) {
                // A dropped pipe ends the re-read; the next connection reads
                // the status again and resumes it if the download still runs.
                stopPolling();
                return;
            }
            // A Collector that comes back may be a newer one.
            m_downloadRefused = false;
        });
    }
    Q_EMIT changed();
}

void UpdateController::setUrlOpener(UrlOpener opener)
{
    if (opener)
        m_openUrl = std::move(opener);
}

void UpdateController::setInstallerLauncher(InstallerLauncher launcher)
{
    if (launcher)
        m_launchInstaller = std::move(launcher);
}

void UpdateController::setQuitter(Quitter quitter)
{
    if (quitter)
        m_quit = std::move(quitter);
}

void UpdateController::setRunProbe(RunProbe probe)
{
    m_runProbe = std::move(probe);
}

void UpdateController::setErrorFormatter(ErrorFormatter formatter)
{
    if (formatter)
        m_errorText = std::move(formatter);
}

void UpdateController::setDataDirectoryProvider(DirectoryProvider provider)
{
    if (provider)
        m_dataDirectory = std::move(provider);
}

void UpdateController::setDownloadPollMs(int milliseconds)
{
    m_pollTimer.setInterval(qMax(1, milliseconds));
}

void UpdateController::setHashSliceBytes(qint64 bytes)
{
    m_hashSliceBytes = qMax<qint64>(1, bytes);
}

QString UpdateController::currentVersion()
{
#ifdef MR_APP_VERSION
    return QStringLiteral(MR_APP_VERSION);
#else
    return {};
#endif
}

QString UpdateController::headline() const
{
    if (!updateAvailable() || m_state.latestVersion.isEmpty())
        return {};
    return tr("有新版本 %1").arg(m_state.latestVersion);
}

QString UpdateController::detail() const
{
    if (!downloadSupported()) {
        return tr("当前 %1。更新需要用户自行下载安装，本软件不会自动下载或替换任何文件。")
            .arg(currentVersion());
    }
    return tr("当前 %1。只有在你点「下载并安装」之后才会下载，下载完成后还要再点「立即安装」才会安装；"
              "本软件不会自行下载或安装。")
        .arg(currentVersion());
}

bool UpdateController::dismissed() const
{
    if (m_state.latestVersion.isEmpty())
        return false;
    const QString remembered =
        m_settings ? m_settings->dismissedUpdateVersion() : m_dismissedVersion;
    return remembered == m_state.latestVersion;
}

bool UpdateController::isReleaseUrl(const QUrl &url)
{
    if (!(url.isValid() && url.scheme() == QLatin1String("https")
          && url.host() == QLatin1String("github.com") && url.userInfo().isEmpty()
          && url.port() == -1)) {
        return false;
    }
    // github.com serves everybody's releases; only this project's may be
    // offered (review OH-4). Judged on the fully decoded path, segment by
    // segment, so neither "..", an encoded one, nor a look-alike repository
    // name can walk out of it.
    static const QStringList kProject{QStringLiteral("Harendotes-Tang"),
                                      QStringLiteral("MentorRouletteRecorder")};
    const QStringList segments = url.path(QUrl::FullyDecoded).split(QLatin1Char('/'));
    if (segments.size() < 3 || !segments.first().isEmpty())
        return false;
    for (int index = 0; index < kProject.size(); ++index) {
        if (segments.at(index + 1).compare(kProject.at(index), Qt::CaseInsensitive) != 0)
            return false;
    }
    for (const QString &segment : segments) {
        if (segment == QLatin1String(".") || segment == QLatin1String("..")
            || segment.contains(QLatin1Char('\\')))
            return false;
    }
    return true;
}

bool UpdateController::isInstallerUrl(const QUrl &url)
{
    if (!isReleaseUrl(url) || url.hasQuery() || url.hasFragment())
        return false;
    // "", owner, repository, "releases", "download", tag, file - counted on the
    // decoded path, as isReleaseUrl judges it, so an encoded "/" is one more
    // segment and not a way into another directory.
    static const QLatin1String kInstallerSuffix("-setup.exe");
    const QStringList segments = url.path(QUrl::FullyDecoded).split(QLatin1Char('/'));
    return segments.size() == 7 && segments.at(3) == QLatin1String("releases")
           && segments.at(4) == QLatin1String("download") && !segments.at(5).isEmpty()
           && segments.at(6).size() > kInstallerSuffix.size()
           && segments.at(6).endsWith(kInstallerSuffix, Qt::CaseInsensitive);
}

UpdateController::Download UpdateController::downloadFrom(const QVariant &raw)
{
    Download download;
    if (raw.typeId() != QMetaType::QVariantMap)
        return download;
    const QVariantMap object = raw.toMap();
    download.present = true;
    download.state = object.value(QStringLiteral("state")).toString();
    download.version = object.value(QStringLiteral("version")).toString();
    download.filePath = object.value(QStringLiteral("file_path")).toString();
    download.sha256 = object.value(QStringLiteral("sha256")).toString();
    download.failure = object.value(QStringLiteral("failure")).toString();
    download.message = object.value(QStringLiteral("message")).toString();
    download.receivedBytes = qMax<qint64>(0, object.value(QStringLiteral("received_bytes")).toLongLong());
    const QVariant total = object.value(QStringLiteral("total_bytes"));
    download.totalBytes = total.isValid() && !total.isNull() ? qMax<qint64>(0, total.toLongLong()) : -1;
    return download;
}

void UpdateController::refreshFromStatus(const QVariantMap &status)
{
    const QVariant raw = status.value(QStringLiteral("update"));
    const bool available = raw.typeId() == QMetaType::QVariantMap;
    const QVariantMap update = available ? raw.toMap() : QVariantMap();

    State next;
    next.available = available;
    next.enabled = update.value(QStringLiteral("enabled")).toBool();
    // The Collector's own verdict, adopted verbatim: this process owns no
    // version comparison of any kind.
    next.updateAvailable = update.value(QStringLiteral("update_available")).toBool();
    next.latestVersion = update.value(QStringLiteral("latest_version")).toString();
    next.lastCheckedAtUtc = update.value(QStringLiteral("last_checked_at_utc")).toString();
    // The address is kept only if this process would be willing to open it, so
    // no view can offer a button that leads somewhere else.
    const QString url = update.value(QStringLiteral("release_url")).toString();
    if (isReleaseUrl(QUrl(url)))
        next.releaseUrl = url;
    const QString installer = update.value(QStringLiteral("installer_url")).toString();
    if (isInstallerUrl(QUrl(installer)))
        next.installerUrl = installer;
    // The download as the Collector reports it. Nothing here is decided by
    // this process: the file is checked only when 立即安装 is pressed.
    next.download = downloadFrom(update.value(QStringLiteral("download")));

    const bool sameDownload = next.download == m_state.download;
    const bool changedState = !(next == m_state);
    m_state = next;
    updatePolling(available);
    // A sentence about the last 立即安装 is about that one READY file only.
    const bool problemStale = !m_installProblem.isEmpty()
                              && (!sameDownload || m_state.download.state != kReady);
    if (problemStale)
        m_installProblem.clear();
    if (changedState || problemStale)
        Q_EMIT changed();
}

bool UpdateController::downloadRunning() const
{
    return m_state.download.state == kDownloading || m_state.download.state == kVerifying;
}

void UpdateController::updatePolling(bool projectionAvailable)
{
    // A read that failed leaves the projection blank for a moment; it does not
    // end the download, so the re-read carries on while the pipe is up. Every
    // read that names a state decides afresh.
    if (projectionAvailable)
        m_polling = downloadRunning();
    if (m_quitting || (m_polling && m_backend && !m_backend->isConnected()))
        m_polling = false;
    // At most one read per interval: the single-shot timer is armed again by
    // the next status adopted after it fired, normally that read's answer.
    if (!m_polling)
        m_pollTimer.stop();
    else if (!m_pollTimer.isActive())
        m_pollTimer.start();
}

void UpdateController::stopPolling()
{
    m_polling = false;
    m_pollTimer.stop();
}

void UpdateController::stopForQuit()
{
    m_quitting = true;
    stopPolling();
    if (m_hasher) {
        m_hasher->cancel();
        m_hasher.reset();
        Q_EMIT changed();
    }
    // An installer being checked is given up; one that was started was held
    // until now, while its start turned into this exit.
    m_heldInstaller.release();
}

void UpdateController::showReleasePage(const QString &opened)
{
    const QUrl url(m_state.releaseUrl);
    if (!isReleaseUrl(url)) {
        Q_EMIT toastRequested(tr("采集器没有给出可用的发布页地址，没有打开浏览器。"));
        return;
    }
    if (!m_openUrl(url)) {
        Q_EMIT toastRequested(tr("无法调用系统浏览器，请自行到项目的发布页下载新版本。"));
        return;
    }
    Q_EMIT toastRequested(opened);
}

void UpdateController::openReleasePage()
{
    showReleasePage(tr("已在系统浏览器中打开发布页（由你手动触发）。"
                       "是否下载安装由你决定，本软件不会自动下载或替换任何文件。"));
}

void UpdateController::openInstallerDownload()
{
    const QUrl url(m_state.installerUrl);
    if (!isInstallerUrl(url)) {
        // A Collector that names no installer, or none this process would open:
        // the release page, where the player downloads it by hand.
        showReleasePage(tr("没有可用的安装程序地址，已改为在系统浏览器中打开发布页"
                           "（由你手动触发），请在页面中下载新版本。"));
        return;
    }
    if (!m_openUrl(url)) {
        Q_EMIT toastRequested(tr("无法调用系统浏览器，请自行到项目的发布页下载新版本。"));
        return;
    }
    // The browser downloads; this process fetched nothing and starts nothing.
    const QString version = m_state.latestVersion.isEmpty()
                                ? tr("新版本") : tr("%1 版").arg(m_state.latestVersion);
    Q_EMIT toastRequested(tr("已请系统浏览器下载 %1的安装程序（由你手动触发）。"
                             "这次由浏览器下载，安装需要你自己运行它。")
                              .arg(version));
}

QString UpdateController::checkSentence(const QVariantMap &payload) const
{
    const QString outcome = payload.value(QStringLiteral("outcome")).toString();
    if (outcome == QLatin1String("DISABLED"))
        return tr("「检查新版本并提示」已关闭，打开后才能检查。");
    if (outcome == QLatin1String("BLOCKED"))
        return tr("本机已通过环境变量禁用更新检查。");

    // CHECKED: a check ran, and how it ended is the answer's own
    // update.last_outcome. The verdict is read back from the state the answer
    // was just adopted into, so the sentence and the version row cannot
    // disagree.
    if (updateAvailable() && !m_state.latestVersion.isEmpty() && downloadSupported())
        return tr("有新版本 %1，可以点「下载并安装」。").arg(m_state.latestVersion);
    if (updateAvailable() && !m_state.latestVersion.isEmpty())
        return tr("有新版本 %1，可以点「下载新版本」下载。").arg(m_state.latestVersion);
    const QVariantMap update = payload.value(QStringLiteral("update")).toMap();
    if (!updateAvailable()
        && update.value(QStringLiteral("last_outcome")).toString() == QLatin1String("OK")) {
        return tr("已是最新版本（%1）。").arg(currentVersion());
    }
    // NOT_FOUND, TIMEOUT, DNS_OR_CONNECT and anything a later Collector adds:
    // one sentence for all of them, and never the token itself.
    return tr("没有检查成功（网络不通或发布页暂时不可用），稍后再试。");
}

void UpdateController::checkNow()
{
    // The same predicate the buttons are enabled by: a Collector that sends no
    // update projection is also too old to answer this message, one that has
    // the setting off would only answer DISABLED, and a request already out is
    // waited for rather than sent twice.
    if (!canCheck())
        return;
    m_checking = true;
    Q_EMIT changed();
    m_backend->checkUpdateNow()->whenDone(this,
        [this](bool ok, const QVariantMap &payload, const QString &, const QString &) {
        m_checking = false;
        if (!ok) {
            // No reply, a refusal from a Collector too old for the message, or
            // a deadline: one sentence, and nothing about the state changes.
            Q_EMIT changed();
            Q_EMIT toastRequested(tr("检查失败，请稍后再试。"));
            return;
        }
        // The answer carries `update` under exactly the key a status does, so
        // the one projection this class has adopts it unchanged.
        refreshFromStatus(payload);
        Q_EMIT changed();
        Q_EMIT toastRequested(checkSentence(payload));
    });
}

// ---------------------------------------------------------------------------
// 下载并安装
// ---------------------------------------------------------------------------

QString UpdateController::downloadPhase() const
{
    if (!m_state.available)
        return {};
    if (!downloadSupported())
        return updateAvailable() ? QStringLiteral("browser") : QString();
    const QString &state = m_state.download.state;
    if (state == kDownloading)
        return QStringLiteral("downloading");
    if (state == kVerifying)
        return QStringLiteral("verifying");
    if (state == kReady)
        return QStringLiteral("ready");
    // A failure is offered a 重试 only while there is something to download;
    // otherwise (重新下载最新正式版 with nothing newer known) it was said once.
    if (state == kFailed)
        return updateAvailable() ? QStringLiteral("failed") : QString();
    // IDLE, or a state a later Collector adds: what there is to offer.
    return updateAvailable() ? QStringLiteral("offer") : QString();
}

QString UpdateController::downloadActionText() const
{
    const QString phase = downloadPhase();
    if (phase == QLatin1String("browser"))
        return tr("下载新版本");
    if (phase == QLatin1String("offer"))
        return tr("下载并安装");
    if (phase == QLatin1String("downloading") || phase == QLatin1String("verifying"))
        return tr("取消");
    if (phase == QLatin1String("ready"))
        return verifyingInstaller() ? tr("正在校验…") : tr("立即安装");
    if (phase == QLatin1String("failed"))
        return tr("重试");
    return {};
}

bool UpdateController::downloadActionEnabled() const
{
    const QString phase = downloadPhase();
    if (phase.isEmpty())
        return false;
    if (phase == QLatin1String("browser"))
        return true;
    return !m_downloadRequestOut && !verifyingInstaller();
}

QString UpdateController::installerName() const
{
    const QString &version = m_state.download.version;
    return version.isEmpty() ? tr("新版本的安装程序") : tr("%1 版的安装程序").arg(version);
}

QString UpdateController::downloadStatusText() const
{
    const QString phase = downloadPhase();
    const Download &download = m_state.download;
    if (phase == QLatin1String("downloading")) {
        if (download.totalBytes > 0) {
            const qint64 percent = qBound(qint64(0), download.receivedBytes * 100 / download.totalBytes,
                                          qint64(100));
            return tr("%1：已下载 %2%（%3 / %4 MB）")
                .arg(installerName())
                .arg(percent)
                .arg(megabytes(download.receivedBytes), megabytes(download.totalBytes));
        }
        return tr("%1：已下载 %2 MB").arg(installerName(), megabytes(download.receivedBytes));
    }
    if (phase == QLatin1String("verifying"))
        return tr("%1：已下载完毕，正在核对发布时公布的校验值…").arg(installerName());
    if (phase == QLatin1String("ready")) {
        if (verifyingInstaller())
            return tr("%1：正在校验，通过后启动安装程序并关闭本软件。").arg(installerName());
        return tr("%1：已下载并通过校验。点「立即安装」后本软件会关闭，由安装程序完成更新；"
                  "Windows 会请你批准它以管理员身份运行。")
            .arg(installerName());
    }
    if (phase == QLatin1String("failed")) {
        // The Collector's own sentence; never the failure token.
        if (!download.message.isEmpty())
            return download.message;
        return tr("下载没有完成。可以点「重试」，或点「在浏览器中下载」改用浏览器下载。");
    }
    return {};
}

qreal UpdateController::downloadProgress() const
{
    const Download &download = m_state.download;
    if (download.totalBytes <= 0)
        return -1;
    return qBound(0.0, double(download.receivedBytes) / double(download.totalBytes), 1.0);
}

bool UpdateController::browserFallbackOffered() const
{
    const QString phase = downloadPhase();
    return phase == QLatin1String("failed")
           || (phase == QLatin1String("ready") && !m_installProblem.isEmpty());
}

bool UpdateController::downloadEngaged() const
{
    const QString phase = downloadPhase();
    return phase == QLatin1String("downloading") || phase == QLatin1String("verifying")
           || phase == QLatin1String("ready");
}

bool UpdateController::canReinstall() const
{
    return downloadSupported() && !downloadRunning() && !m_downloadRequestOut
           && !verifyingInstaller();
}

void UpdateController::downloadAction()
{
    const QString phase = downloadPhase();
    if (phase == QLatin1String("browser"))
        openInstallerDownload();
    else if (phase == QLatin1String("offer") || phase == QLatin1String("failed"))
        startDownload();
    else if (phase == QLatin1String("downloading") || phase == QLatin1String("verifying"))
        cancelDownload();
    else if (phase == QLatin1String("ready"))
        install();
}

void UpdateController::startDownload()
{
    const QString phase = downloadPhase();
    if (phase != QLatin1String("offer") && phase != QLatin1String("failed"))
        return;
    sendStart(false);
}

void UpdateController::reinstallLatest()
{
    if (!canReinstall())
        return;
    sendStart(true);
}

void UpdateController::sendStart(bool reinstall)
{
    if (!m_backend || m_downloadRequestOut)
        return;
    m_downloadRequestOut = true;
    m_installProblem.clear();
    Q_EMIT changed();
    const Download before = m_state.download;
    m_backend->startUpdateDownload(reinstall)->whenDone(this,
        [this, before](bool ok, const QVariantMap &payload, const QString &code, const QString &) {
        m_downloadRequestOut = false;
        if (ok) {
            adoptDownloadAnswer(payload);
            const Download &after = m_state.download;
            // No place shows a failure while nothing newer is on offer.
            if (after.state == kFailed && !updateAvailable()) {
                Q_EMIT toastRequested(after.message.isEmpty()
                                          ? tr("没有开始下载，请稍后再试。")
                                          : after.message);
            } else if (after.state == kIdle
                       || (after.state == kFailed && before.state == kFailed
                           && after.failure == before.failure)) {
                // The Collector starts at most one download every few seconds,
                // and none while a cancelled one is still finishing; it then
                // answers with the download as it was. Said once, the way a
                // refusal is, and nothing about the state is called failed.
                Q_EMIT toastRequested(tr("没有开始下载，请稍候几秒再试。"));
            }
            return;
        }
        Q_EMIT changed();
        if (isUnknownMessage(code)) {
            // A Collector without the feature: the click asked for the
            // installer, and the browser is how this build got it before.
            m_downloadRefused = true;
            Q_EMIT changed();
            openInstallerDownload();
            return;
        }
        Q_EMIT toastRequested(m_errorText(tr("没有开始下载，请稍后再试。"), code));
    });
}

void UpdateController::cancelDownload()
{
    if (!m_backend || m_downloadRequestOut || !downloadRunning())
        return;
    m_downloadRequestOut = true;
    Q_EMIT changed();
    m_backend->cancelUpdateDownload()->whenDone(this,
        [this](bool ok, const QVariantMap &payload, const QString &code, const QString &) {
        m_downloadRequestOut = false;
        if (ok) {
            adoptDownloadAnswer(payload);
            return;
        }
        Q_EMIT changed();
        Q_EMIT toastRequested(m_errorText(tr("没有取消下载，请稍后再试。"), code));
    });
}

void UpdateController::adoptDownloadAnswer(const QVariantMap &payload)
{
    // The answer carries `update` under the key a status does. The holder of
    // the status adopts it too, so the next status change cannot bring back
    // the state from before the click.
    const QVariant update = payload.value(QStringLiteral("update"));
    if (update.typeId() == QMetaType::QVariantMap)
        Q_EMIT updateAnswered(update.toMap());
    refreshFromStatus(payload);
    Q_EMIT changed();
}

// ---------------------------------------------------------------------------
// 立即安装
// ---------------------------------------------------------------------------

QString UpdateController::runRefusal(QString *code) const
{
    const std::optional<bool> inProgress = m_runProbe ? m_runProbe() : std::nullopt;
    if (!inProgress.has_value()) {
        *code = QStringLiteral("RUN_STATE_UNKNOWN");
        return tr("还没有读到当前是否在导随中，暂时不能安装，请稍后再试。");
    }
    if (*inProgress) {
        *code = QStringLiteral("RUN_IN_PROGRESS");
        return tr("正在导随（已匹配或已在副本中），现在安装会中断这次记录。"
                  "请在这次导随结束后再点「立即安装」。");
    }
    return {};
}

void UpdateController::refuseInstall(const QString &sentence, const QString &code)
{
    // Nothing is started from this attempt: the file is let go of, so the
    // Collector, or the user, may replace or delete it again.
    m_heldInstaller.release();
    m_installProblem = m_errorText(sentence, code);
    Q_EMIT changed();
    Q_EMIT toastRequested(m_installProblem);
}

void UpdateController::install()
{
    if (m_quitting || downloadPhase() != QLatin1String("ready") || m_hasher)
        return;
    m_installProblem.clear();
    QString code;
    const QString refusal = runRefusal(&code);
    if (!refusal.isEmpty()) {
        refuseInstall(refusal, code);
        return;
    }

    // Snapshot what was reported when the button was pressed: that file is
    // the one checked, and the one started.
    const Download &download = m_state.download;
    m_pendingInstaller = {download.filePath, download.sha256, download.version};
    if (!installer::isInUpdatesDirectory(download.filePath, m_dataDirectory(), download.version)) {
        refuseInstall(tr("下载的安装程序不在本软件的数据目录中，为安全起见没有启动它。"
                         "可以改为在浏览器中下载。"),
                      QStringLiteral("INSTALLER_LOCATION"));
        return;
    }
    if (!installer::isRegularFile(download.filePath)) {
        refuseInstall(tr("下载的安装程序不是一个普通文件，为安全起见没有启动它。"
                         "可以改为在浏览器中下载。"),
                      QStringLiteral("INSTALLER_NOT_A_FILE"));
        return;
    }

    // From here the file is held, and others may only read it: the checks just
    // made are made again on what the handle refers to, the hash is read
    // through it, and the shell starts it while it is still held - nothing can
    // be swapped in between. Every give-up releases it (refuseInstall), and so
    // does the application's exit (stopForQuit).
    if (!m_heldInstaller.open(download.filePath)) {
        refuseInstall(tr("无法读取下载的安装程序，没有启动它。可以改为在浏览器中下载。"),
                      QStringLiteral("INSTALLER_UNREADABLE"));
        return;
    }
    if (!m_heldInstaller.isRegularFile()) {
        refuseInstall(tr("下载的安装程序不是一个普通文件，为安全起见没有启动它。"
                         "可以改为在浏览器中下载。"),
                      QStringLiteral("INSTALLER_NOT_A_FILE"));
        return;
    }
    if (!m_heldInstaller.isAt(download.filePath)) {
        // A data directory reached through a link, a SUBST drive or a mapped
        // drive ends here: the path would not be bound to the file that is held.
        refuseInstall(tr("无法确认下载的安装程序位于本软件的数据目录中（数据目录可能经由链接或映射的盘符到达），"
                         "为安全起见没有启动它。可以改为在浏览器中下载。"),
                      QStringLiteral("INSTALLER_LOCATION"));
        return;
    }

    // The SHA-256 is computed now, a slice per turn of the event loop, while
    // 正在校验… shows; quitting the application stops it (stopForQuit).
    auto hasher = std::make_unique<InstallerHasher>();
    connect(hasher.get(), &InstallerHasher::finished, this, &UpdateController::onInstallerHashed);
    if (!hasher->start(&m_heldInstaller, m_hashSliceBytes)) {
        refuseInstall(tr("无法读取下载的安装程序，没有启动它。可以改为在浏览器中下载。"),
                      QStringLiteral("INSTALLER_UNREADABLE"));
        return;
    }
    m_hasher = std::move(hasher);
    Q_EMIT changed();
}

void UpdateController::onInstallerHashed(bool ok, const QString &sha256)
{
    // Deleted later: this runs inside the hasher's own signal.
    if (m_hasher)
        m_hasher.release()->deleteLater();
    Q_EMIT changed();
    const Installer installer = m_pendingInstaller;
    m_pendingInstaller = {};
    if (!ok) {
        refuseInstall(tr("无法读取下载的安装程序，没有启动它。可以改为在浏览器中下载。"),
                      QStringLiteral("INSTALLER_UNREADABLE"));
        return;
    }
    if (!installer::isSha256(installer.sha256) || sha256 != installer.sha256) {
        refuseInstall(tr("下载的安装程序与发布时公布的校验值不一致，为安全起见没有启动它。"
                         "可以改为在浏览器中下载。"),
                      QStringLiteral("INSTALLER_CHECKSUM_MISMATCH"));
        return;
    }
    // A run may have begun while the file was read: asked again right before
    // the start. The file itself cannot have changed - it is still held - and
    // its path still names what is held.
    QString code;
    const QString refusal = runRefusal(&code);
    if (!refusal.isEmpty()) {
        refuseInstall(refusal, code);
        return;
    }
    if (!m_heldInstaller.isRegularFile() || !m_heldInstaller.isAt(installer.path)) {
        refuseInstall(tr("下载的安装程序不是一个普通文件，为安全起见没有启动它。"
                         "可以改为在浏览器中下载。"),
                      QStringLiteral("INSTALLER_NOT_A_FILE"));
        return;
    }
    if (!m_launchInstaller(QDir::toNativeSeparators(installer.path))) {
        refuseInstall(tr("安装程序没有启动（可能是在 Windows 的确认窗口中选择了「否」）。"
                         "本软件继续运行，可以再点「立即安装」，或改为在浏览器中下载。"),
                      QStringLiteral("INSTALLER_NOT_STARTED"));
        return;
    }
    // Only a started installer ends this process, through its normal exit:
    // the Collector is asked to stop politely and the files can be replaced.
    // The installer stays held until then (stopForQuit), a moment after the
    // shell has started it.
    m_quit();
}

void UpdateController::dismiss()
{
    if (m_state.latestVersion.isEmpty())
        return;
    m_dismissedVersion = m_state.latestVersion;
    if (m_settings)
        m_settings->setDismissedUpdateVersion(m_state.latestVersion);
    Q_EMIT changed();
}

} // namespace mr
