#include "UpdateController.h"

#include "AppSettings.h"
#include "IBackend.h"

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

void UpdateController::setBackend(IBackend *backend)
{
    m_backend = backend;
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

    if (next == m_state)
        return;
    m_state = next;
    Q_EMIT changed();
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
                             "安装需要你自己运行它；本软件自身不下载、也不替换任何文件。")
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
