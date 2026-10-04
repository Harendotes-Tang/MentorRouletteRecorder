#pragma once

// ---------------------------------------------------------------------------
// 检查新版本 / update check (notify only), desktop half.
//
// Projects the optional `update` object the Collector puts on its status into
// the banner on 总览 and the version row in 设置 · 关于. Every decision belongs
// to the Collector: this class never compares two versions, never builds an
// address and never fetches anything. It shows what arrived and, on an explicit
// click, hands the address the Collector named to the system browser - the same
// path AppController::openNpcapWebsite takes, behind the same kind of predicate
// SharedCalibrationController::isShareIssueUrl applies.
//
// A Collector that sends no `update` object leaves this controller unavailable
// and nothing about updates is shown.
//
// 检查更新 (checkNow) is the one thing this class asks for rather than waits
// for: CheckUpdateNow makes the Collector check once, outside its daily
// throttle. The answer carries the same `update` object a status does and is
// adopted through the same projection; the outcome only decides which single
// sentence the toast gets. Nothing is downloaded and no version is compared
// here either.
//
// 忽略此版本 is remembered per version in AppSettings, so a later release raises
// the banner again without the user having to undo anything.
//
// 下载并安装 (docs/privacy-boundary.md §8.6) is two clicks, one request each.
// The first sends StartUpdateDownload: the Collector downloads the installer and
// its published SHA-256 and reports the download in `update.download`, which is
// adopted through the same projection; while it is DOWNLOADING or VERIFYING the
// status is re-read about once a second through the application's own status
// read. The second, 立即安装, is the only place that starts a program: only in
// READY, not while a run is in progress, and only after this process checked
// the file itself (InstallerVerifier.h), through the one handle it then holds
// until the start, so what is started is what was checked. The installer is
// started through the shell - it asks for elevation itself - and the application
// then quits through its normal exit path, which stops the Collector politely
// and lets go of the file. A Collector without
// the feature, or one that refuses the message, leaves 下载新版本 to the browser
// as before.
// ---------------------------------------------------------------------------

#include "InstallerVerifier.h"

#include <QObject>
#include <QPointer>
#include <QQmlEngine>
#include <QString>
#include <QTimer>
#include <QUrl>
#include <QVariantMap>

#include <functional>
#include <memory>
#include <optional>

namespace mr {

class AppSettings;
class IBackend;

class UpdateController final : public QObject
{
    Q_OBJECT
    QML_ANONYMOUS

    /// The Collector sent an `update` object. An older one never does, and then
    /// neither the banner nor the version row says anything about updates.
    Q_PROPERTY(bool available READ available NOTIFY changed)
    /// 检查新版本并提示 as the Collector currently has it.
    Q_PROPERTY(bool enabled READ enabled NOTIFY changed)
    /// The Collector's own verdict, adopted verbatim.
    Q_PROPERTY(bool updateAvailable READ updateAvailable NOTIFY changed)
    Q_PROPERTY(QString latestVersion READ latestVersion NOTIFY changed)
    /// This build's version, for the sentence that names both.
    Q_PROPERTY(QString currentVersion READ currentVersion CONSTANT)
    /// The page the Collector named. Empty unless it passes \ref isReleaseUrl,
    /// i.e. unless it is a page of this project's GitHub repository.
    Q_PROPERTY(QString releaseUrl READ releaseUrl NOTIFY changed)
    /// The installer of latestVersion the Collector named. Empty unless it passes
    /// \ref isInstallerUrl - and empty from a Collector that names none.
    Q_PROPERTY(QString installerUrl READ installerUrl NOTIFY changed)
    Q_PROPERTY(QString lastCheckedAtUtc READ lastCheckedAtUtc NOTIFY changed)
    /// The banner's two sentences. Empty headline while there is nothing to say.
    Q_PROPERTY(QString headline READ headline NOTIFY changed)
    Q_PROPERTY(QString detail READ detail NOTIFY changed)
    /// 忽略此版本 was pressed for exactly the version now on offer.
    Q_PROPERTY(bool dismissed READ dismissed NOTIFY changed)
    /// A CheckUpdateNow request is out. The buttons that would send another
    /// one are disabled while it is true.
    Q_PROPERTY(bool checking READ checking NOTIFY changed)
    /// 检查更新 can be pressed: the Collector reports an update projection, it
    /// has 检查新版本并提示 on, and nothing is in flight. A Collector that is too
    /// old to carry the projection is also too old to carry the message.
    Q_PROPERTY(bool canCheck READ canCheck NOTIFY changed)

    // -- 下载并安装 -----------------------------------------------------------
    /// The Collector reports `update.download` and has not refused
    /// StartUpdateDownload as a message it does not know. Otherwise the browser
    /// downloads the installer, as before.
    Q_PROPERTY(bool downloadSupported READ downloadSupported NOTIFY changed)
    /// What the three update places show: "" (nothing to download), "browser"
    /// (下载新版本 in the browser), "offer" (下载并安装), "downloading",
    /// "verifying", "ready" (立即安装) or "failed".
    Q_PROPERTY(QString downloadPhase READ downloadPhase NOTIFY changed)
    /// The label of the one button that acts on the phase; empty with none.
    Q_PROPERTY(QString downloadActionText READ downloadActionText NOTIFY changed)
    Q_PROPERTY(bool downloadActionEnabled READ downloadActionEnabled NOTIFY changed)
    /// One sentence on the download: progress, verdict or the Collector's own
    /// explanation of a failure. Empty while there is nothing to say.
    Q_PROPERTY(QString downloadStatusText READ downloadStatusText NOTIFY changed)
    /// 0..1 while the size is known, -1 when the server declared none.
    Q_PROPERTY(qreal downloadProgress READ downloadProgress NOTIFY changed)
    /// The sentence of the last 立即安装 that started nothing; empty otherwise.
    Q_PROPERTY(QString installProblem READ installProblem NOTIFY changed)
    /// This process is computing the installer's SHA-256 before starting it.
    Q_PROPERTY(bool verifyingInstaller READ verifyingInstaller NOTIFY changed)
    /// 在浏览器中下载 is offered beside the in-app download: after a failure, or
    /// after a 立即安装 that started nothing.
    Q_PROPERTY(bool browserFallbackOffered READ browserFallbackOffered NOTIFY changed)
    /// A download the user started is under way or done; the 总览 banner stays
    /// for it even after 忽略此版本.
    Q_PROPERTY(bool downloadEngaged READ downloadEngaged NOTIFY changed)
    /// 重新下载最新正式版 (maintainer tools) can be pressed.
    Q_PROPERTY(bool canReinstall READ canReinstall NOTIFY changed)

public:
    using UrlOpener = std::function<bool(const QUrl &)>;
    /// Starts the installer at the given path; false when it did not start.
    using InstallerLauncher = std::function<bool(const QString &path)>;
    /// The application's orderly exit.
    using Quitter = std::function<void()>;
    /// Whether a run is in progress (matched or in the duty); no value while
    /// that is not known.
    using RunProbe = std::function<std::optional<bool>()>;
    /// A refusal as a reader is shown it (AppController::errorText).
    using ErrorFormatter = std::function<QString(const QString &message, const QString &code)>;
    /// The Collector's data directory (CollectorProcess::collectorDataDirectory).
    using DirectoryProvider = std::function<QString()>;

    /// How often the status is re-read while a download is running.
    static constexpr int kDownloadPollMs = 1000;

    explicit UpdateController(QObject *parent = nullptr);
    ~UpdateController() override;

    /// Where 忽略此版本 is remembered. Without one the dismissal lasts only for
    /// this session.
    void setSettings(AppSettings *settings);
    /// Where checkNow() sends CheckUpdateNow. Without one the button can never
    /// be pressed (canCheck stays false).
    void setBackend(IBackend *backend);
    /// Test seam. By default the address goes to QDesktopServices.
    void setUrlOpener(UrlOpener opener);
    /// Test seam: by default the operating system's shell opens the file, the
    /// way addresses are opened, and the installer asks for elevation itself;
    /// AppController gives a mock backend's run one that starts nothing. A test
    /// passes a function that records the path and starts nothing. The file is
    /// held while it runs.
    void setInstallerLauncher(InstallerLauncher launcher);
    /// By default TrayController::quitApplication(), the path 退出 takes.
    void setQuitter(Quitter quitter);
    /// Without one the run state is unknown and 立即安装 is refused.
    void setRunProbe(RunProbe probe);
    void setErrorFormatter(ErrorFormatter formatter);
    /// By default CollectorProcess::collectorDataDirectory().
    void setDataDirectoryProvider(DirectoryProvider provider);
    /// Test seams: the re-read interval and the hashing slice.
    void setDownloadPollMs(int milliseconds);
    void setHashSliceBytes(qint64 bytes);

    bool available() const { return m_state.available; }
    bool enabled() const { return m_state.enabled; }
    bool updateAvailable() const { return m_state.available && m_state.updateAvailable; }
    QString latestVersion() const { return m_state.latestVersion; }
    static QString currentVersion();
    QString releaseUrl() const { return m_state.releaseUrl; }
    QString installerUrl() const { return m_state.installerUrl; }
    QString lastCheckedAtUtc() const { return m_state.lastCheckedAtUtc; }
    QString headline() const;
    QString detail() const;
    bool dismissed() const;
    bool checking() const { return m_checking; }
    bool canCheck() const
    {
        return m_backend && m_state.available && m_state.enabled && !m_checking;
    }

    bool downloadSupported() const
    {
        return m_backend && m_state.available && m_state.download.present && !m_downloadRefused;
    }
    QString downloadPhase() const;
    QString downloadActionText() const;
    bool downloadActionEnabled() const;
    QString downloadStatusText() const;
    qreal downloadProgress() const;
    QString installProblem() const { return m_installProblem; }
    bool verifyingInstaller() const { return m_hasher != nullptr; }
    bool browserFallbackOffered() const;
    bool downloadEngaged() const;
    bool canReinstall() const;
    /// True while the status is being re-read for a running download.
    bool polling() const { return m_polling; }

    /// True only for an https://github.com/Harendotes-Tang/MentorRouletteRecorder
    /// address without credentials and on the default port: the one kind of
    /// address this process opens.
    static bool isReleaseUrl(const QUrl &url);
    /// An \ref isReleaseUrl address that is one of this project's release
    /// downloads of an installer: /<owner>/<repo>/releases/download/<tag>/<file>,
    /// the file named *-setup.exe, with no query and no fragment.
    static bool isInstallerUrl(const QUrl &url);

public Q_SLOTS:
    /// Adopt one GetStatus payload. A payload without `update` resets this
    /// controller to unavailable.
    void refreshFromStatus(const QVariantMap &status);
    /// 查看更新说明: the release page in the system browser.
    void openReleasePage();
    /// 下载新版本: the installer's address in the system browser, which downloads
    /// it; this process fetches nothing and installs nothing. Without an installer
    /// address it would open, the release page instead.
    void openInstallerDownload();
    /// 忽略此版本, for the version currently on offer only.
    void dismiss();
    /// 检查更新. Sends CheckUpdateNow, adopts the `update` object it answers
    /// with, and asks for one toast sentence. A second press while the first
    /// request is still out does nothing.
    void checkNow();
    /// The one button of the download places: 下载新版本 (browser), 下载并安装,
    /// 取消, 立即安装 or 重试, whichever downloadPhase calls for.
    void downloadAction();
    /// 下载并安装 / 重试: StartUpdateDownload for latest_version.
    void startDownload();
    /// 重新下载最新正式版 (maintainer tools): StartUpdateDownload with reinstall.
    void reinstallLatest();
    /// 取消: CancelUpdateDownload.
    void cancelDownload();
    /// 立即安装. Only in READY and with no run in progress; checks the file -
    /// where it is, that it is a regular file, its SHA-256 - and only then starts
    /// it and quits. Any failed check starts nothing and says why.
    void install();

Q_SIGNALS:
    void changed();
    /// One sentence for the toast.
    void toastRequested(const QString &message);
    /// Re-read the Collector status now (a download is running).
    void statusRefreshRequested();
    /// A StartUpdateDownload / CancelUpdateDownload answer carried \a update, the
    /// $defs/UpdateStatus the status holder should adopt, so a later status
    /// change does not bring back the state before it.
    void updateAnswered(const QVariantMap &update);

private:
    /// $defs/UpdateDownload as the Collector reported it.
    struct Download
    {
        QString state;
        QString version;
        QString filePath;
        QString sha256;
        QString failure;
        QString message;
        qint64 receivedBytes = 0;
        /// -1 when the server declared no size.
        qint64 totalBytes = -1;
        bool present = false;

        bool operator==(const Download &) const = default;
    };

    /// Everything the Collector decided, and nothing this process decided.
    struct State
    {
        QString latestVersion;
        QString releaseUrl;
        QString installerUrl;
        QString lastCheckedAtUtc;
        Download download;
        bool available = false;
        bool enabled = false;
        bool updateAvailable = false;

        bool operator==(const State &) const = default;
    };

    /// The file 立即安装 checks and starts, as reported when it was pressed.
    struct Installer
    {
        QString path;
        QString sha256;
        QString version;
    };

    static Download downloadFrom(const QVariant &raw);
    bool downloadRunning() const;
    /// "9.9.9 版的安装程序", or a version-less wording.
    QString installerName() const;
    void sendStart(bool reinstall);
    /// Adopt a StartUpdateDownload / CancelUpdateDownload answer.
    void adoptDownloadAnswer(const QVariantMap &payload);
    /// Arms or stops the status re-read for the state just adopted.
    void updatePolling(bool projectionAvailable);
    void stopPolling();
    /// The sentence that holds 立即安装 back, or empty when it may proceed.
    QString runRefusal(QString *code) const;
    /// 立即安装 started nothing: \a sentence for the reader, \a code for maintainers.
    /// Every attempt given up passes through here and lets go of the file.
    void refuseInstall(const QString &sentence, const QString &code);
    void onInstallerHashed(bool ok, const QString &sha256);
    /// Stops the re-read and the hashing and lets go of the installer: the
    /// application is quitting.
    void stopForQuit();

    /// The one sentence a CheckUpdateNow answer deserves. Read from the answer
    /// and from the state it was just adopted into, never from a token.
    QString checkSentence(const QVariantMap &payload) const;
    /// Opens the release page; \a opened is the toast once the browser took it.
    void showReleasePage(const QString &opened);

    QPointer<AppSettings> m_settings;
    QPointer<IBackend> m_backend;
    UrlOpener m_openUrl;
    InstallerLauncher m_launchInstaller;
    Quitter m_quit;
    RunProbe m_runProbe;
    ErrorFormatter m_errorText;
    DirectoryProvider m_dataDirectory;
    State m_state;
    /// Remembers 忽略此版本 while no AppSettings is attached.
    QString m_dismissedVersion;
    bool m_checking = false;

    /// A StartUpdateDownload or CancelUpdateDownload is out.
    bool m_downloadRequestOut = false;
    /// The Collector refused StartUpdateDownload as unknown; cleared when the
    /// pipe comes back, which may be a newer Collector.
    bool m_downloadRefused = false;
    /// The status is being re-read for a running download.
    bool m_polling = false;
    /// The application is quitting: nothing is re-read, hashed or started any more.
    bool m_quitting = false;
    QTimer m_pollTimer;
    QString m_installProblem;
    /// The installer 立即安装 checks and starts, held from before its checks
    /// until it has started and the application quits, or until the attempt is
    /// given up (refuseInstall).
    InstallerHold m_heldInstaller;
    /// Set while the installer is being hashed before it is started.
    std::unique_ptr<InstallerHasher> m_hasher;
    Installer m_pendingInstaller;
    qint64 m_hashSliceBytes = InstallerHasher::kDefaultSliceBytes;
};

} // namespace mr
