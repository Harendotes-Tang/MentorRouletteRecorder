// ---------------------------------------------------------------------------
// 下载并安装 fixtures.
//
// StartUpdateDownload and CancelUpdateDownload against a download that is only
// simulated: this backend has no network client. The rules are the Collector's
// (docs/privacy-boundary.md §8.6) - the setting turns it off, nothing newer
// means NO_UPDATE, one download at a time, a READY file is reported again - and
// so are the shapes ($defs/UpdateDownload). The "installer" written at the end
// is a few kilobytes of text under <dataDirectory()>/updates/, never a program;
// the Desktop's own check refuses it there unless a test names that folder as
// the Collector's data directory.
// ---------------------------------------------------------------------------

#include "MockBackend.h"

#include <QCryptographicHash>
#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QSaveFile>
#include <QTimer>

namespace mr {
namespace {

const QString kIdle = QStringLiteral("IDLE");
const QString kDownloading = QStringLiteral("DOWNLOADING");
const QString kVerifying = QStringLiteral("VERIFYING");
const QString kReady = QStringLiteral("READY");
const QString kFailed = QStringLiteral("FAILED");

/// The version the mock's update check reports (MockBackend::collectorStatus).
const QString kMockLatestVersion = QStringLiteral("99.9.9");

/// The simulated download arrives in this many steps.
constexpr int kMockDownloadSteps = 8;

} // namespace

bool MockBackend::isUpdateDownloadFixture(const QString &state)
{
    return state == QLatin1String("downloading") || state == QLatin1String("verifying")
           || state == QLatin1String("ready") || state == QLatin1String("failed");
}

void MockBackend::setUpdateDownloadStepMs(int milliseconds)
{
    m_updateDownloadStepMs = qMax(1, milliseconds);
    if (m_updateDownloadTimer)
        m_updateDownloadTimer->setInterval(m_updateDownloadStepMs);
}

void MockBackend::setUpdateDownloadFixture(const QString &state)
{
    if (!isUpdateDownloadFixture(state))
        return;
    m_updateAvailable = true;
    if (m_updateDownloadTimer)
        m_updateDownloadTimer->stop();
    m_updateFailure.clear();
    m_updateMessage.clear();
    m_updateMessageRef = {};
    if (state == QLatin1String("downloading")) {
        m_updateDownloadState = kDownloading;
        m_updateReceivedBytes = kMockInstallerBytes * 45 / 100;
    } else if (state == QLatin1String("verifying")) {
        m_updateDownloadState = kVerifying;
        m_updateReceivedBytes = kMockInstallerBytes;
    } else if (state == QLatin1String("ready")) {
        finishUpdateDownload();
    } else {
        failUpdateDownload(QStringLiteral("TIMEOUT"), "update.download.timeout");
    }
}

bool MockBackend::isUpdateDownloadMessage(const QString &messageType)
{
    return messageType == QLatin1String("StartUpdateDownload")
           || messageType == QLatin1String("CancelUpdateDownload");
}

QString MockBackend::mockInstallerPath() const
{
    return QDir::toNativeSeparators(
        dataDirectory() + QStringLiteral("/updates/MentorRecorder-%1-setup.exe").arg(kMockLatestVersion));
}

QJsonObject MockBackend::updateDownload() const
{
    QJsonObject download{{QStringLiteral("state"), m_updateDownloadState}};
    if (m_updateDownloadState == kIdle)
        return download;
    download.insert(QStringLiteral("version"), kMockLatestVersion);
    if (m_updateDownloadState == kFailed) {
        download.insert(QStringLiteral("failure"), m_updateFailure);
        download.insert(QStringLiteral("message"), m_updateMessage);
        download.insert(QStringLiteral("message_ref"), m_updateMessageRef);
        return download;
    }
    download.insert(QStringLiteral("received_bytes"), double(m_updateReceivedBytes));
    download.insert(QStringLiteral("total_bytes"), double(kMockInstallerBytes));
    if (m_updateDownloadState == kReady) {
        download.insert(QStringLiteral("file_path"), m_updateFilePath);
        download.insert(QStringLiteral("sha256"), m_updateSha256);
    }
    return download;
}

QJsonObject MockBackend::applyUpdateDownload(const QString &messageType, const QJsonObject &payload)
{
    const bool running = m_updateDownloadState == kDownloading || m_updateDownloadState == kVerifying;
    if (messageType == QLatin1String("CancelUpdateDownload")) {
        ++m_cancelUpdateDownloadCount;
        if (running) {
            if (m_updateDownloadTimer)
                m_updateDownloadTimer->stop();
            m_updateDownloadState = kIdle;
            m_updateReceivedBytes = 0;
        }
        return {{QStringLiteral("update"), collectorStatus().value(QStringLiteral("update"))}};
    }

    ++m_startUpdateDownloadCount;
    m_lastStartUpdateDownload = payload;
    if (!captureSettings().value(QStringLiteral("update_check_enabled")).toBool(true)) {
        // DISABLED because of the setting (UpdateDownloadMessages.SettingOff).
        failUpdateDownload(QStringLiteral("DISABLED"), "update.download.setting_off");
    } else if (!m_updateAvailable) {
        // The mock only knows 99.9.9 while it says there is an update, so even
        // reinstall has nothing to fetch otherwise.
        failUpdateDownload(QStringLiteral("NO_UPDATE"), "update.download.no_update");
    } else if (!running && !(m_updateDownloadState == kReady && QFileInfo::exists(m_updateFilePath))) {
        // Start only when no download is running and no verified installer is already present.
        m_updateDownloadState = kDownloading;
        m_updateReceivedBytes = 0;
        m_updateFailure.clear();
        m_updateMessage.clear();
        m_updateMessageRef = {};
        if (!m_updateDownloadTimer) {
            m_updateDownloadTimer = new QTimer(this);
            connect(m_updateDownloadTimer, &QTimer::timeout, this,
                    &MockBackend::advanceUpdateDownload);
        }
        m_updateDownloadTimer->setInterval(m_updateDownloadStepMs);
        m_updateDownloadTimer->start();
    }
    return {{QStringLiteral("update"), collectorStatus().value(QStringLiteral("update"))}};
}

void MockBackend::advanceUpdateDownload()
{
    if (m_updateDownloadState == kDownloading) {
        m_updateReceivedBytes =
            qMin(kMockInstallerBytes, m_updateReceivedBytes + kMockInstallerBytes / kMockDownloadSteps);
        if (m_updateReceivedBytes >= kMockInstallerBytes)
            m_updateDownloadState = kVerifying;
        return;
    }
    if (m_updateDownloadTimer)
        m_updateDownloadTimer->stop();
    if (m_updateDownloadState == kVerifying)
        finishUpdateDownload();
}

void MockBackend::finishUpdateDownload()
{
    // A recognisable text file standing in for the installer: it is hashed
    // like the real one, and it is no program anybody could start.
    QByteArray content;
    const QByteArray line =
        QByteArrayLiteral("MentorRecorder mock installer - not a program, nothing to run.\n");
    for (int index = 0; index < 1024; ++index)
        content.append(line);
    const QString path = mockInstallerPath();
    QDir().mkpath(QFileInfo(path).absolutePath());
    QSaveFile file(path);
    if (!file.open(QIODevice::WriteOnly) || file.write(content) != content.size() || !file.commit()) {
        failUpdateDownload(QStringLiteral("DISK_FAILED"), "update.download.disk_failed");
        return;
    }
    m_updateDownloadState = kReady;
    m_updateReceivedBytes = kMockInstallerBytes;
    m_updateFilePath = path;
    m_updateSha256 = QString::fromLatin1(
        QCryptographicHash::hash(content, QCryptographicHash::Sha256).toHex());
}

void MockBackend::failUpdateDownload(const QString &failure, const char *key)
{
    if (m_updateDownloadTimer)
        m_updateDownloadTimer->stop();
    m_updateDownloadState = kFailed;
    m_updateReceivedBytes = 0;
    m_updateFailure = failure;
    // The Collector's sentence for this failure (UpdateDownloadMessages) and its
    // catalogue reference.
    const CollectorSentence sentence = collectorSentence(key);
    m_updateMessage = sentence.chinese;
    m_updateMessageRef = sentence.ref;
}

} // namespace mr
