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
// ---------------------------------------------------------------------------

#include <QObject>
#include <QPointer>
#include <QQmlEngine>
#include <QString>
#include <QUrl>
#include <QVariantMap>

#include <functional>

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
    /// The page the Collector named. Empty unless it passes \ref isReleaseUrl.
    Q_PROPERTY(QString releaseUrl READ releaseUrl NOTIFY changed)
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

public:
    using UrlOpener = std::function<bool(const QUrl &)>;

    explicit UpdateController(QObject *parent = nullptr);

    /// Where 忽略此版本 is remembered. Without one the dismissal lasts only for
    /// this session.
    void setSettings(AppSettings *settings);
    /// Where checkNow() sends CheckUpdateNow. Without one the button can never
    /// be pressed (canCheck stays false).
    void setBackend(IBackend *backend);
    /// Test seam. By default the address goes to QDesktopServices.
    void setUrlOpener(UrlOpener opener);

    bool available() const { return m_state.available; }
    bool enabled() const { return m_state.enabled; }
    bool updateAvailable() const { return m_state.available && m_state.updateAvailable; }
    QString latestVersion() const { return m_state.latestVersion; }
    static QString currentVersion();
    QString releaseUrl() const { return m_state.releaseUrl; }
    QString lastCheckedAtUtc() const { return m_state.lastCheckedAtUtc; }
    QString headline() const;
    QString detail() const;
    bool dismissed() const;
    bool checking() const { return m_checking; }
    bool canCheck() const
    {
        return m_backend && m_state.available && m_state.enabled && !m_checking;
    }

    /// True only for an https://github.com/ address without credentials and on
    /// the default port: the one kind of address this process opens.
    static bool isReleaseUrl(const QUrl &url);

public Q_SLOTS:
    /// Adopt one GetStatus payload. A payload without `update` resets this
    /// controller to unavailable.
    void refreshFromStatus(const QVariantMap &status);
    /// 打开下载页. Nothing is downloaded here; the browser takes over.
    void openReleasePage();
    /// 忽略此版本, for the version currently on offer only.
    void dismiss();
    /// 检查更新. Sends CheckUpdateNow, adopts the `update` object it answers
    /// with, and asks for one toast sentence. A second press while the first
    /// request is still out does nothing.
    void checkNow();

Q_SIGNALS:
    void changed();
    /// One sentence for the toast.
    void toastRequested(const QString &message);

private:
    /// Everything the Collector decided, and nothing this process decided.
    struct State
    {
        QString latestVersion;
        QString releaseUrl;
        QString lastCheckedAtUtc;
        bool available = false;
        bool enabled = false;
        bool updateAvailable = false;

        bool operator==(const State &) const = default;
    };

    /// The one sentence a CheckUpdateNow answer deserves. Read from the answer
    /// and from the state it was just adopted into, never from a token.
    QString checkSentence(const QVariantMap &payload) const;

    QPointer<AppSettings> m_settings;
    QPointer<IBackend> m_backend;
    UrlOpener m_openUrl;
    State m_state;
    /// Remembers 忽略此版本 while no AppSettings is attached.
    QString m_dismissedVersion;
    bool m_checking = false;
};

} // namespace mr
