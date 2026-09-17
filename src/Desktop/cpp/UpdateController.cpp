#include "UpdateController.h"

#include "AppSettings.h"

#include <QDesktopServices>
#include <QMetaType>

#include <utility>

namespace mr {

UpdateController::UpdateController(QObject *parent)
    : QObject(parent)
    , m_openUrl([](const QUrl &url) { return QDesktopServices::openUrl(url); })
{
}

void UpdateController::setSettings(AppSettings *settings)
{
    m_settings = settings;
    Q_EMIT changed();
}

void UpdateController::setUrlOpener(UrlOpener opener)
{
    if (opener)
        m_openUrl = std::move(opener);
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
    return tr("当前 %1。更新需要用户自行下载安装，本软件不会自动下载或替换任何文件。")
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
    return url.isValid() && url.scheme() == QLatin1String("https")
        && url.host() == QLatin1String("github.com") && url.userInfo().isEmpty()
        && url.port() == -1;
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

    if (next == m_state)
        return;
    m_state = next;
    Q_EMIT changed();
}

void UpdateController::openReleasePage()
{
    const QUrl url(m_state.releaseUrl);
    if (!isReleaseUrl(url)) {
        Q_EMIT toastRequested(tr("采集器没有给出可用的下载页地址，没有打开浏览器。"));
        return;
    }
    if (!m_openUrl(url)) {
        Q_EMIT toastRequested(tr("无法调用系统浏览器，请自行到项目的发布页下载新版本。"));
        return;
    }
    Q_EMIT toastRequested(tr("已在系统浏览器中打开下载页（由你手动触发）。"
                             "是否下载安装由你决定，本软件不会自动下载或替换任何文件。"));
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
