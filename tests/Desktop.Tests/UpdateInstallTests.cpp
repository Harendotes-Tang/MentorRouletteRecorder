// ---------------------------------------------------------------------------
// tst_updateinstall - 下载并安装 (docs/privacy-boundary.md §8.6), desktop half.
//
// What it pins:
//   * a Collector without update.download, or one that refuses
//     StartUpdateDownload as unknown, leaves 下载新版本 to the browser;
//   * 下载并安装 sends StartUpdateDownload with {} and the maintainer tools'
//     重新下载最新正式版 sends {reinstall: true}; 取消 sends CancelUpdateDownload;
//   * while the download is DOWNLOADING or VERIFYING the status is re-read through
//     the application's own status read, and that stops when the state moves on,
//     when the pipe drops and when the application quits;
//   * the progress reads as a percentage when the size is known, in megabytes
//     otherwise; a failure shows the Collector's sentence, 重试 and the browser;
//   * 立即安装 starts the file only after this process checked it - where it is,
//     that it is a regular file, its SHA-256 - and each failed check starts
//     nothing; it is refused while a run is in progress or not known; it quits
//     through the injected exit only after the start succeeded, and stays when
//     the start was declined;
//   * the hash is read in slices, visibly (正在校验…), and quitting stops it;
//   * the installer is held from its checksum to its start: while it is read
//     and while the shell is asked to start it, nobody can write to it, rename
//     it, delete it or rename its folder; every attempt that is given up lets
//     go of it, and a started one is let go of when the application quits;
//   * a shipping (IPC) build hands the file to the shell, a mock run starts
//     nothing - both as the library composes them, not as a test replaces them;
//   * a 下载并安装 / 重试 the Collector answers with the download unchanged
//     (it starts at most one every few seconds) says so, without an error;
//   * a refusal shows a player its sentence only and a maintainer the code too;
//   * the mock backend simulates the whole download offline, and the three
//     places - 总览 banner, 设置 · 通用, 设置 · 关于 - show and drive it.
//
// No test ever starts a program: every controller gets a launcher that records
// the path, and an exit that records the call - except the one test of the
// shipping launcher, where QDesktopServices hands the file to a stand-in.
// ---------------------------------------------------------------------------

#include "TestCollectorGuard.h"
#include "AppController.h"
#include "AppSettings.h"
#include "Formatters.h"
#include "IBackend.h"
#include "IpcBackend.h"
#include "JobCatalog.h"
#include "MockBackend.h"
#include "RoleCatalog.h"
#include "UpdateController.h"

#include <QCryptographicHash>
#include <QDesktopServices>
#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QFont>
#include <QFontDatabase>
#include <QGuiApplication>
#include <QJsonObject>
#include <QJsonValue>
#include <QProcess>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickStyle>
#include <QRegularExpression>
#include <QScopeGuard>
#include <QSignalSpy>
#include <QStandardPaths>
#include <QTemporaryDir>
#include <QTest>
#include <QTimer>
#include <QUrl>
#include <QUuid>

#include <functional>
#include <memory>
#include <optional>

// Last: it brings <windows.h>.
#include "InstallerTampering.h"

namespace {

constexpr auto kVersion = "9.9.9";
constexpr auto kReleaseUrl =
    "https://github.com/Harendotes-Tang/MentorRouletteRecorder/releases/latest";
constexpr auto kInstallerUrl =
    "https://github.com/Harendotes-Tang/MentorRouletteRecorder/releases/download/v9.9.9/"
    "MentorRecorder-9.9.9-setup.exe";
constexpr auto kMockInstallerUrl =
    "https://github.com/Harendotes-Tang/MentorRouletteRecorder/releases/download/v99.9.9/"
    "MentorRecorder-99.9.9-setup.exe";
constexpr qint64 kMiB = 1024 * 1024;

QQuickItem *findVisualItem(QQuickItem *root, const QString &name)
{
    if (!root)
        return nullptr;
    if (root->objectName() == name)
        return root;
    for (auto *child : root->childItems()) {
        if (auto *found = findVisualItem(child, name))
            return found;
    }
    return nullptr;
}

/// $defs/UpdateDownload.
QJsonObject download(const QString &state, const QJsonObject &extra = {})
{
    QJsonObject object{{QStringLiteral("state"), state}};
    if (state != QLatin1String("IDLE"))
        object.insert(QStringLiteral("version"), QLatin1String(kVersion));
    for (auto it = extra.begin(); it != extra.end(); ++it)
        object.insert(it.key(), it.value());
    return object;
}

/// $defs/UpdateStatus with a newer version on offer, and \a downloadObject when
/// it is not empty - a Collector that knows the feature always sends one.
QJsonObject updateStatus(const QJsonObject &downloadObject)
{
    QJsonObject status{{QStringLiteral("enabled"), true},
                       {QStringLiteral("update_available"), true},
                       {QStringLiteral("latest_version"), QLatin1String(kVersion)},
                       {QStringLiteral("release_url"), QLatin1String(kReleaseUrl)},
                       {QStringLiteral("installer_url"), QLatin1String(kInstallerUrl)},
                       {QStringLiteral("last_checked_at_utc"), QStringLiteral("2026-10-04T02:00:00.000Z")},
                       {QStringLiteral("last_outcome"), QStringLiteral("OK")}};
    if (!downloadObject.isEmpty())
        status.insert(QStringLiteral("download"), downloadObject);
    return status;
}

/// Answers GetStatus with `update`, StartUpdateDownload / CancelUpdateDownload
/// with the next `update` the test names, and GetCurrentRun with `currentRun`.
/// Every answer arrives on the next turn of the event loop, as the pipe's do.
class DownloadBackend final : public mr::IBackend
{
public:
    QJsonObject update;
    /// What StartUpdateDownload answers with; also becomes `update`.
    QJsonObject startAnswer;
    QJsonObject cancelAnswer;
    /// Refuse StartUpdateDownload with this code (a Collector that does not
    /// know the message answers ERR_BAD_REQUEST).
    QString refuseStart;
    QJsonObject currentRun{{QStringLiteral("state"), QStringLiteral("IDLE")},
                           {QStringLiteral("run"), QJsonValue::Null},
                           {QStringLiteral("elapsed_ms"), QJsonValue::Null}};
    bool failCurrentRun = false;
    bool connected = true;
    int statusReads = 0;
    struct Sent {
        QString type;
        QJsonObject payload;
    };
    QList<Sent> sent;

    QString backendName() const override { return QStringLiteral("scripted"); }
    bool isConnected() const override { return connected; }

    void setConnected(bool value)
    {
        connected = value;
        Q_EMIT connectionChanged();
    }

    int countOf(const QString &type) const
    {
        int count = 0;
        for (const Sent &entry : sent)
            count += entry.type == type ? 1 : 0;
        return count;
    }

    mr::BackendReply *request(const QString &type, const QJsonObject &payload = {}) override
    {
        auto *reply = new mr::BackendReply(type, type, this);
        if (!connected) {
            reply->failUnsent(QStringLiteral("ERR_INTERNAL"), QString::fromUtf8("未连接。"));
            return reply;
        }
        bool ok = true;
        QString code;
        QJsonObject answer;
        if (type == QLatin1String("GetStatus")) {
            ++statusReads;
            answer = {{QStringLiteral("database_ready"), true}};
            if (!update.isEmpty())
                answer.insert(QStringLiteral("update"), update);
        } else if (type == QLatin1String("GetCurrentRun")) {
            ok = !failCurrentRun;
            code = QStringLiteral("ERR_INTERNAL");
            answer = currentRun;
        } else if (type == QLatin1String("StartUpdateDownload")) {
            sent.append({type, payload});
            if (!refuseStart.isEmpty()) {
                ok = false;
                code = refuseStart;
            } else {
                if (!startAnswer.isEmpty())
                    update = startAnswer;
                answer = {{QStringLiteral("update"), update}};
            }
        } else if (type == QLatin1String("CancelUpdateDownload")) {
            sent.append({type, payload});
            if (!cancelAnswer.isEmpty())
                update = cancelAnswer;
            answer = {{QStringLiteral("update"), update}};
        }
        QTimer::singleShot(0, reply, [reply, ok, code, answer] {
            if (ok)
                reply->succeed(answer);
            else
                reply->fail(code, QString::fromUtf8("当前采集器不支持该消息。"));
        });
        return reply;
    }
};

/// A file standing in for the installer, and its SHA-256.
struct InstallerFile
{
    QString path;
    QString sha256;
};

InstallerFile writeInstaller(const QString &path, qint64 size = 64 * 1024)
{
    QDir().mkpath(QFileInfo(path).absolutePath());
    QByteArray bytes;
    bytes.reserve(int(size));
    for (qint64 index = 0; index < size; ++index)
        bytes.append(char('a' + index % 23));
    QFile file(path);
    if (!file.open(QIODevice::WriteOnly | QIODevice::Truncate))
        return {};
    file.write(bytes);
    file.close();
    return {path, QString::fromLatin1(
                      QCryptographicHash::hash(bytes, QCryptographicHash::Sha256).toHex())};
}

/// One AppController on the scripted backend; the update controller's launcher
/// and exit only record what they were asked to do.
struct Scene
{
    std::unique_ptr<DownloadBackend> backend = std::make_unique<DownloadBackend>();
    std::unique_ptr<mr::AppController> app;
    QTemporaryDir dataDirectory;
    QStringList opened;
    QStringList events;
    QStringList launched;
    bool launchSucceeds = true;
    /// Runs inside the launcher, while the installer is being started.
    std::function<void(const QString &path)> duringLaunch;

    ~Scene() { app.reset(); }

    mr::UpdateController *update() const { return app->update(); }

    void open(const QJsonObject &updateObject)
    {
        backend->update = updateObject;
        app = std::make_unique<mr::AppController>(backend.get(), nullptr);
        auto *controller = update();
        controller->setUrlOpener([this](const QUrl &url) {
            opened.append(url.toString());
            return true;
        });
        controller->setInstallerLauncher([this](const QString &path) {
            if (duringLaunch)
                duringLaunch(path);
            launched.append(path);
            events.append(QStringLiteral("launch"));
            return launchSucceeds;
        });
        controller->setQuitter([this] { events.append(QStringLiteral("quit")); });
        const QString directory = dataDirectory.path();
        controller->setDataDirectoryProvider([directory] { return directory; });
        controller->setDownloadPollMs(20);
    }

    /// The installer where the Collector puts it.
    QString installerPath() const
    {
        return dataDirectory.filePath(QStringLiteral("updates/MentorRecorder-9.9.9-setup.exe"));
    }

    /// READY, reporting \a path (native separators, as the Collector writes it)
    /// and \a sha256.
    static QJsonObject ready(const QString &path, const QString &sha256)
    {
        return updateStatus(download(QStringLiteral("READY"),
            {{QStringLiteral("received_bytes"), 64 * 1024},
             {QStringLiteral("total_bytes"), 64 * 1024},
             {QStringLiteral("file_path"), QDir::toNativeSeparators(path)},
             {QStringLiteral("sha256"), sha256}}));
    }
};

/// One shipping QML page on the mock backend, with nothing ever started.
struct PageScene
{
    std::unique_ptr<mr::MockBackend> backend = std::make_unique<mr::MockBackend>();
    std::unique_ptr<mr::AppSettings> settings;
    std::unique_ptr<mr::Formatters> formatters;
    std::unique_ptr<mr::JobCatalog> jobs;
    std::unique_ptr<mr::RoleCatalog> roles;
    std::unique_ptr<QQmlEngine> engine;
    std::unique_ptr<mr::AppController> app;
    std::unique_ptr<QObject> root;
    QTemporaryDir dataDirectory;
    QQuickItem *page = nullptr;
    QStringList opened;
    QStringList launched;
    int quits = 0;

    ~PageScene()
    {
        root.reset();
        engine.reset();
        app.reset();
    }

    bool open(const QString &element, const QString &fixture = QString(), bool maintainer = false)
    {
        backend->setLiveMode(mr::MockBackend::LiveMode::None);
        backend->setDataDirectory(dataDirectory.path());
        backend->setUpdateDownloadStepMs(10);
        backend->setUpdateAvailable(true);
        if (!fixture.isEmpty())
            backend->setUpdateDownloadFixture(fixture);
        settings = std::make_unique<mr::AppSettings>();
        settings->setDismissedUpdateVersion(QString());
        app = std::make_unique<mr::AppController>(backend.get(), settings.get());
        app->setMaintainerToolsVisible(maintainer);
        auto *update = app->update();
        update->setUrlOpener([this](const QUrl &url) {
            opened.append(url.toString());
            return true;
        });
        update->setInstallerLauncher([this](const QString &path) {
            launched.append(path);
            return true;
        });
        update->setQuitter([this] { ++quits; });
        const QString directory = dataDirectory.path();
        update->setDataDirectoryProvider([directory] { return directory; });
        update->setDownloadPollMs(20);
        formatters = std::make_unique<mr::Formatters>();
        jobs = std::make_unique<mr::JobCatalog>();
        roles = std::make_unique<mr::RoleCatalog>();
        engine = std::make_unique<QQmlEngine>();
        auto *context = engine->rootContext();
        context->setContextProperty(QStringLiteral("App"), app.get());
        context->setContextProperty(QStringLiteral("Fmt"), formatters.get());
        context->setContextProperty(QStringLiteral("Jobs"), jobs.get());
        context->setContextProperty(QStringLiteral("Roles"), roles.get());
        context->setContextProperty(QStringLiteral("Settings"), settings.get());
        context->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QQmlComponent component(engine.get());
        component.setData(QStringLiteral(R"(import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder
ApplicationWindow {
    width: 1100; height: 900; visible: true
    color: Theme.surface
    ColumnLayout {
        anchors.fill: parent
        anchors.margins: 16
        %1 { objectName: "scenePage"; Layout.fillWidth: true; Layout.fillHeight: true }
    }
})").arg(element).toUtf8(), QUrl());
        root.reset(component.create());
        if (!root) {
            qWarning("%s", qPrintable(component.errorString()));
            return false;
        }
        page = qobject_cast<QQuickItem *>(root->findChild<QObject *>(QStringLiteral("scenePage")));
        return page != nullptr;
    }

    QQuickItem *item(const QString &name) const { return findVisualItem(page, name); }
    bool shows(const QString &name) const
    {
        auto *found = item(name);
        return found && found->isVisible();
    }
    QString textOf(const QString &name) const
    {
        auto *found = item(name);
        return found ? found->property("text").toString() : QString();
    }
    bool press(const QString &name) const
    {
        auto *button = item(name);
        return button && button->isVisible() && button->isEnabled()
               && QMetaObject::invokeMethod(button, "clicked");
    }
};

/// Everything a player reads in \a text once the version numbers it quotes and
/// the megabyte unit are taken out: it must hold no Latin letter at all.
bool readsWithoutLatin(QString text)
{
    // This build's own version carries a Latin prerelease label on a test build.
    for (const QString &allowed : {mr::UpdateController::currentVersion(), QStringLiteral("99.9.9"),
                                   QString::fromLatin1(kVersion), QStringLiteral("MB"),
                                   QStringLiteral("Windows")}) {
        text.remove(allowed);
    }
    static const QRegularExpression latin(QStringLiteral("[A-Za-z]"));
    return !text.contains(latin);
}

void emitAboutToQuit()
{
    QVERIFY(QMetaObject::invokeMethod(QCoreApplication::instance(), "aboutToQuit",
                                      Qt::DirectConnection));
}

QString sha256Of(const QString &path)
{
    QFile file(path);
    if (!file.open(QIODevice::ReadOnly))
        return {};
    return QString::fromLatin1(QCryptographicHash::hash(file.readAll(), QCryptographicHash::Sha256).toHex());
}

/// What an AppController with no Collector behind it cannot know, stood in for:
/// where the Collector keeps its data, that no run is in progress, and the
/// exit. The launcher is left as the composition made it.
void standInForTheCollector(mr::UpdateController *update, const QString &dataDirectory, int *quits)
{
    update->setDataDirectoryProvider([dataDirectory] { return dataDirectory; });
    update->setRunProbe([] { return std::optional<bool>(false); });
    update->setQuitter([quits] { ++*quits; });
}

} // namespace

/// Stands in for the system shell: QDesktopServices hands it every file:
/// address instead of opening it (QDesktopServices::setUrlHandler), so the
/// launcher a shipping build uses can be called without starting anything.
class ShellStandIn final : public QObject
{
    Q_OBJECT

public:
    QList<QUrl> opened;

public Q_SLOTS:
    void open(const QUrl &url) { opened.append(url); }
};

class UpdateInstallTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void initTestCase()
    {
        const QString qmlRoot = QString::fromUtf8(MR_DESKTOP_QML_DIR);
        QVERIFY(QDir(qmlRoot).exists());
        qmlRegisterSingletonType(QUrl::fromLocalFile(qmlRoot + QStringLiteral("/Theme.qml")),
                                 "MentorRecorder", 1, 0, "Theme");
        for (const auto &directory : {QStringLiteral("/components"), QStringLiteral("/dialogs"),
                                      QStringLiteral("/pages"), QStringLiteral("/charts")}) {
            for (const auto &file :
                 QDir(qmlRoot + directory).entryList({QStringLiteral("*.qml")}, QDir::Files)) {
                const QByteArray name = file.chopped(4).toUtf8();
                qmlRegisterType(QUrl::fromLocalFile(qmlRoot + directory + QLatin1Char('/') + file),
                                "MentorRecorder", 1, 0, name.constData());
            }
        }
    }

    // -- the download ---------------------------------------------------------

    void aCollectorWithoutTheFeatureKeepsTheBrowserDownload()
    {
        Scene scene;
        scene.open(updateStatus({}));
        auto *update = scene.update();
        QTRY_VERIFY(update->updateAvailable());
        QVERIFY(!update->downloadSupported());
        QCOMPARE(update->downloadPhase(), QStringLiteral("browser"));
        QCOMPARE(update->downloadActionText(), QString::fromUtf8("下载新版本"));
        update->downloadAction();
        QCOMPARE(scene.opened, QStringList{QLatin1String(kInstallerUrl)});
        QTest::qWait(30);
        QCOMPARE(scene.backend->countOf(QStringLiteral("StartUpdateDownload")), 0);
    }

    void downloadAndInstallAsksTheCollectorWithAnEmptyRequest()
    {
        Scene scene;
        scene.open(updateStatus(download(QStringLiteral("IDLE"))));
        auto *update = scene.update();
        QTRY_VERIFY(update->downloadSupported());
        QCOMPARE(update->downloadPhase(), QStringLiteral("offer"));
        QCOMPARE(update->downloadActionText(), QString::fromUtf8("下载并安装"));
        QVERIFY(update->downloadActionEnabled());

        scene.backend->startAnswer = updateStatus(download(QStringLiteral("DOWNLOADING"),
            {{QStringLiteral("received_bytes"), 0}, {QStringLiteral("total_bytes"), 60 * kMiB}}));
        update->downloadAction();
        // Held down for the one request in flight.
        QVERIFY(!update->downloadActionEnabled());
        QTRY_COMPARE(update->downloadPhase(), QStringLiteral("downloading"));
        QCOMPARE(scene.backend->countOf(QStringLiteral("StartUpdateDownload")), 1);
        QCOMPARE(scene.backend->sent.constFirst().payload, QJsonObject());
        QCOMPARE(update->downloadActionText(), QString::fromUtf8("取消"));
        QVERIFY(update->downloadActionEnabled());
        // The download is the Collector's; the browser is not involved.
        QVERIFY(scene.opened.isEmpty());
        QVERIFY(scene.launched.isEmpty());
    }

    void reinstallSendsTheReinstallFlag()
    {
        Scene scene;
        scene.open(updateStatus(download(QStringLiteral("IDLE"))));
        auto *update = scene.update();
        QTRY_VERIFY(update->canReinstall());
        update->reinstallLatest();
        QTRY_COMPARE(scene.backend->countOf(QStringLiteral("StartUpdateDownload")), 1);
        QCOMPARE(scene.backend->sent.constFirst().payload,
                 (QJsonObject{{QStringLiteral("reinstall"), true}}));
    }

    void aCollectorThatRefusesTheMessageFallsBackToTheBrowser()
    {
        Scene scene;
        scene.open(updateStatus(download(QStringLiteral("IDLE"))));
        auto *update = scene.update();
        QTRY_VERIFY(update->downloadSupported());
        scene.backend->refuseStart = QStringLiteral("ERR_BAD_REQUEST");
        update->downloadAction();
        // The click asked for the installer: the browser downloads it.
        QTRY_COMPARE(scene.opened, QStringList{QLatin1String(kInstallerUrl)});
        QVERIFY(!update->downloadSupported());
        QCOMPARE(update->downloadPhase(), QStringLiteral("browser"));
        QCOMPARE(update->downloadActionText(), QString::fromUtf8("下载新版本"));
    }

    void theStatusIsReReadWhileDownloadingAndNoLongerOnceItIsReady()
    {
        Scene scene;
        scene.open(updateStatus(download(QStringLiteral("IDLE"))));
        auto *update = scene.update();
        QTRY_VERIFY(update->downloadSupported());
        QVERIFY(!update->polling());

        scene.backend->startAnswer = updateStatus(download(QStringLiteral("DOWNLOADING"),
            {{QStringLiteral("received_bytes"), 0}, {QStringLiteral("total_bytes"), 60 * kMiB}}));
        const int before = scene.backend->statusReads;
        update->startDownload();
        QTRY_VERIFY(update->polling());
        QTRY_VERIFY(scene.backend->statusReads >= before + 3);

        // Progress arrives through those reads.
        scene.backend->update = updateStatus(download(QStringLiteral("DOWNLOADING"),
            {{QStringLiteral("received_bytes"), 30 * kMiB}, {QStringLiteral("total_bytes"), 60 * kMiB}}));
        QTRY_VERIFY(update->downloadStatusText().contains(QStringLiteral("50%")));
        scene.backend->update = updateStatus(download(QStringLiteral("VERIFYING"),
            {{QStringLiteral("received_bytes"), 60 * kMiB}, {QStringLiteral("total_bytes"), 60 * kMiB}}));
        QTRY_COMPARE(update->downloadPhase(), QStringLiteral("verifying"));
        QVERIFY(update->polling());

        const InstallerFile file = writeInstaller(scene.installerPath());
        scene.backend->update = Scene::ready(file.path, file.sha256);
        QTRY_COMPARE(update->downloadPhase(), QStringLiteral("ready"));
        QVERIFY(!update->polling());
        const int settled = scene.backend->statusReads;
        QTest::qWait(150);
        QCOMPARE(scene.backend->statusReads, settled);
    }

    void theReReadStopsWhenThePipeDrops()
    {
        Scene scene;
        scene.open(updateStatus(download(QStringLiteral("DOWNLOADING"),
            {{QStringLiteral("received_bytes"), kMiB}})));
        auto *update = scene.update();
        QTRY_VERIFY(update->polling());
        scene.backend->setConnected(false);
        QVERIFY(!update->polling());
        const int settled = scene.backend->statusReads;
        QTest::qWait(150);
        QCOMPARE(scene.backend->statusReads, settled);
    }

    void aFailedReadKeepsReReadingWhileThePipeIsUp()
    {
        // A read that times out is not the end of the download: the projection
        // goes blank for a moment and the next read brings it back.
        Scene scene;
        scene.open(updateStatus(download(QStringLiteral("DOWNLOADING"),
            {{QStringLiteral("received_bytes"), kMiB}})));
        auto *update = scene.update();
        QTRY_VERIFY(update->polling());
        scene.backend->update = QJsonObject();
        QTRY_VERIFY(!update->available());
        QVERIFY(update->polling());
        scene.backend->update = updateStatus(download(QStringLiteral("DOWNLOADING"),
            {{QStringLiteral("received_bytes"), 2 * kMiB}}));
        QTRY_VERIFY(update->available());
        QVERIFY(update->polling());
    }

    void cancelStopsTheDownload()
    {
        Scene scene;
        scene.open(updateStatus(download(QStringLiteral("DOWNLOADING"),
            {{QStringLiteral("received_bytes"), kMiB}})));
        auto *update = scene.update();
        QTRY_COMPARE(update->downloadPhase(), QStringLiteral("downloading"));
        scene.backend->cancelAnswer = updateStatus(download(QStringLiteral("IDLE")));
        update->downloadAction();
        QTRY_COMPARE(update->downloadPhase(), QStringLiteral("offer"));
        QCOMPARE(scene.backend->countOf(QStringLiteral("CancelUpdateDownload")), 1);
        QVERIFY(!update->polling());
    }

    void theProgressIsAPercentageOrMegabytes()
    {
        Scene scene;
        scene.open(updateStatus(download(QStringLiteral("DOWNLOADING"),
            {{QStringLiteral("received_bytes"), qint64(0.45 * 60 * kMiB)},
             {QStringLiteral("total_bytes"), 60 * kMiB}})));
        auto *update = scene.update();
        QTRY_COMPARE(update->downloadPhase(), QStringLiteral("downloading"));
        QCOMPARE(update->downloadStatusText(),
                 QString::fromUtf8("9.9.9 版的安装程序：已下载 45%（27.0 / 60.0 MB）"));
        QVERIFY(qAbs(update->downloadProgress() - 0.45) < 0.001);

        // No declared size: megabytes received, and no percentage invented.
        scene.backend->update = updateStatus(download(QStringLiteral("DOWNLOADING"),
            {{QStringLiteral("received_bytes"), 27 * kMiB}}));
        QTRY_COMPARE(update->downloadStatusText(),
                     QString::fromUtf8("9.9.9 版的安装程序：已下载 27.0 MB"));
        QCOMPARE(update->downloadProgress(), qreal(-1));
    }

    void aFailureShowsTheCollectorsSentenceRetryAndTheBrowser()
    {
        Scene scene;
        const QString sentence =
            QString::fromUtf8("下载安装程序超时，没有保存任何文件。可以点「重试」，或改用浏览器下载。");
        scene.open(updateStatus(download(QStringLiteral("FAILED"),
            {{QStringLiteral("failure"), QStringLiteral("TIMEOUT")},
             {QStringLiteral("message"), sentence}})));
        auto *update = scene.update();
        QTRY_COMPARE(update->downloadPhase(), QStringLiteral("failed"));
        QCOMPARE(update->downloadStatusText(), sentence);
        QVERIFY(!update->downloadStatusText().contains(QStringLiteral("TIMEOUT")));
        QCOMPARE(update->downloadActionText(), QString::fromUtf8("重试"));
        QVERIFY(update->browserFallbackOffered());

        scene.backend->startAnswer = updateStatus(download(QStringLiteral("DOWNLOADING")));
        update->downloadAction();
        QTRY_COMPARE(update->downloadPhase(), QStringLiteral("downloading"));

        // A failure without a sentence still says what to do.
        scene.backend->update = updateStatus(download(QStringLiteral("FAILED"),
            {{QStringLiteral("failure"), QStringLiteral("SOMETHING_NEW")}}));
        QTRY_COMPARE(update->downloadPhase(), QStringLiteral("failed"));
        QCOMPARE(update->downloadStatusText(),
                 QString::fromUtf8("下载没有完成。可以点「重试」，或点「在浏览器中下载」改用浏览器下载。"));
    }

    void aStartThatStartsNothingSaysSo()
    {
        // The Collector starts at most one download every few seconds, and none
        // while a cancelled one is still finishing: it then answers with the
        // download as it was - IDLE, or the earlier FAILED. The click must not
        // look ignored, and nothing failed either.
        const QString sentence = QString::fromUtf8("没有开始下载，请稍候几秒再试。");
        const QString timedOut =
            QString::fromUtf8("下载安装程序超时，没有保存任何文件。可以点「重试」，或改用浏览器下载。");
        const QJsonObject idle = updateStatus(download(QStringLiteral("IDLE")));
        const QJsonObject failed = updateStatus(download(QStringLiteral("FAILED"),
            {{QStringLiteral("failure"), QStringLiteral("TIMEOUT")}, {QStringLiteral("message"), timedOut}}));
        for (const QJsonObject &unchanged : {idle, failed}) {
            Scene scene;
            scene.open(unchanged);
            auto *update = scene.update();
            QTRY_VERIFY(update->downloadSupported());
            const QString phase = update->downloadPhase();
            const QString status = update->downloadStatusText();
            QSignalSpy toasts(update, &mr::UpdateController::toastRequested);
            update->downloadAction();
            QTRY_COMPARE(toasts.count(), 1);
            QCOMPARE(toasts.constFirst().constFirst().toString(), sentence);
            QCOMPARE(scene.backend->countOf(QStringLiteral("StartUpdateDownload")), 1);
            QCOMPARE(update->downloadPhase(), phase);
            QCOMPARE(update->downloadStatusText(), status);
            QVERIFY(update->installProblem().isEmpty());
            QVERIFY(update->downloadActionEnabled());
        }
        QVERIFY(readsWithoutLatin(sentence));

        // A download that started, or a failure that is a new one, is no such case.
        Scene scene;
        scene.open(failed);
        auto *update = scene.update();
        QTRY_COMPARE(update->downloadPhase(), QStringLiteral("failed"));
        QSignalSpy toasts(update, &mr::UpdateController::toastRequested);
        scene.backend->startAnswer = updateStatus(download(QStringLiteral("FAILED"),
            {{QStringLiteral("failure"), QStringLiteral("NOT_FOUND")},
             {QStringLiteral("message"), QString::fromUtf8("发布页面上暂时找不到该版本的安装程序。")}}));
        update->downloadAction();
        QTRY_VERIFY(update->downloadStatusText().contains(QString::fromUtf8("找不到")));
        QTest::qWait(30);
        QVERIFY(toasts.isEmpty());
        scene.backend->startAnswer = updateStatus(download(QStringLiteral("DOWNLOADING")));
        update->downloadAction();
        QTRY_COMPARE(update->downloadPhase(), QStringLiteral("downloading"));
        QTest::qWait(30);
        QVERIFY(toasts.isEmpty());
    }

    // -- 立即安装 -------------------------------------------------------------

    void installStartsTheVerifiedInstallerAndThenQuits()
    {
        Scene scene;
        const InstallerFile file = writeInstaller(scene.installerPath());
        QVERIFY(!file.sha256.isEmpty());
        scene.open(Scene::ready(file.path, file.sha256));
        auto *update = scene.update();
        QTRY_COMPARE(update->downloadPhase(), QStringLiteral("ready"));
        QCOMPARE(update->downloadActionText(), QString::fromUtf8("立即安装"));
        QVERIFY(update->downloadStatusText().contains(QString::fromUtf8("会关闭")));
        QVERIFY(update->downloadStatusText().contains(QString::fromUtf8("管理员")));

        update->downloadAction();
        QTRY_COMPARE(scene.events, (QStringList{QStringLiteral("launch"), QStringLiteral("quit")}));
        QCOMPARE(QDir::fromNativeSeparators(scene.launched.constFirst()), file.path);
        QVERIFY(update->installProblem().isEmpty());
    }

    void theInstallerPathMayDifferOnlyInLetterCase()
    {
        // Windows compares paths without regard to case; the file name itself
        // is the Collector's and must match exactly.
        Scene scene;
        const InstallerFile file = writeInstaller(scene.installerPath());
        QString reported = file.path;
        const QString name = QFileInfo(reported).fileName();
        reported = QFileInfo(reported).absolutePath().toUpper() + QLatin1Char('/') + name;
        scene.open(Scene::ready(reported, file.sha256));
        QTRY_COMPARE(scene.update()->downloadPhase(), QStringLiteral("ready"));
        scene.update()->install();
        QTRY_COMPARE(scene.launched.size(), 1);
    }

    void installStartsNothingOutsideTheUpdatesDirectory_data()
    {
        QTest::addColumn<QString>("relative");
        QTest::addColumn<QString>("version");
        QTest::addColumn<bool>("absolute");
        QTest::newRow("another-directory")
            << "elsewhere/MentorRecorder-9.9.9-setup.exe" << "9.9.9" << true;
        QTest::newRow("the-data-directory-itself")
            << "MentorRecorder-9.9.9-setup.exe" << "9.9.9" << true;
        QTest::newRow("another-versions-name")
            << "updates/MentorRecorder-9.9.8-setup.exe" << "9.9.9" << true;
        QTest::newRow("another-name") << "updates/setup.exe" << "9.9.9" << true;
        QTest::newRow("dot-segments")
            << "updates/../updates/MentorRecorder-9.9.9-setup.exe" << "9.9.9" << true;
        QTest::newRow("relative") << "updates/MentorRecorder-9.9.9-setup.exe" << "9.9.9" << false;
        QTest::newRow("not-a-version")
            << "updates/MentorRecorder-..-setup.exe" << ".." << true;
    }

    void installStartsNothingOutsideTheUpdatesDirectory()
    {
        QFETCH(QString, relative);
        QFETCH(QString, version);
        QFETCH(bool, absolute);
        Scene scene;
        // The file is really there and its checksum is right: only where it is
        // can stop it.
        const InstallerFile file =
            writeInstaller(QDir::cleanPath(scene.dataDirectory.filePath(relative)));
        QVERIFY(!file.sha256.isEmpty());
        QString reported = absolute ? scene.dataDirectory.path() + QLatin1Char('/') + relative
                                    : relative;
        QJsonObject status = Scene::ready(reported, file.sha256);
        QJsonObject object = status.value(QStringLiteral("download")).toObject();
        object.insert(QStringLiteral("version"), version);
        status.insert(QStringLiteral("download"), object);
        scene.open(status);
        auto *update = scene.update();
        QTRY_COMPARE(update->downloadPhase(), QStringLiteral("ready"));
        update->install();
        QTRY_VERIFY(!update->installProblem().isEmpty());
        QTest::qWait(50);
        QVERIFY2(scene.launched.isEmpty(), qPrintable(reported));
        QVERIFY(scene.events.isEmpty());
        QVERIFY(update->installProblem().contains(QString::fromUtf8("没有启动")));
        // The browser download remains.
        QVERIFY(update->browserFallbackOffered());
        QVERIFY(scene.app->toastMessage().contains(QString::fromUtf8("没有启动")));
    }

    void installStartsNothingThatIsNotARegularFile()
    {
        Scene scene;
        // A directory where the installer should be.
        QVERIFY(QDir().mkpath(scene.installerPath()));
        scene.open(Scene::ready(scene.installerPath(), QString(64, QLatin1Char('a'))));
        auto *update = scene.update();
        QTRY_COMPARE(update->downloadPhase(), QStringLiteral("ready"));
        update->install();
        QTRY_VERIFY(!update->installProblem().isEmpty());
        QTest::qWait(50);
        QVERIFY(scene.launched.isEmpty());
        QVERIFY(scene.events.isEmpty());

        // Nothing there at all.
        QVERIFY(QDir(scene.installerPath()).removeRecursively());
        update->install();
        QTest::qWait(50);
        QVERIFY(scene.launched.isEmpty());
        QVERIFY(scene.events.isEmpty());
    }

    void installStartsNothingThroughALinkedUpdatesDirectory()
    {
        Scene scene;
        QTemporaryDir elsewhere;
        const InstallerFile file =
            writeInstaller(elsewhere.filePath(QStringLiteral("MentorRecorder-9.9.9-setup.exe")));
        const QString link = scene.dataDirectory.filePath(QStringLiteral("updates"));
        // A junction needs no privilege; a test machine that cannot make one
        // still runs every other check.
        const int made = QProcess::execute(QStringLiteral("cmd.exe"),
            {QStringLiteral("/c"), QStringLiteral("mklink"), QStringLiteral("/J"),
             QDir::toNativeSeparators(link), QDir::toNativeSeparators(elsewhere.path())});
        if (made != 0 || !QFileInfo::exists(scene.installerPath()))
            QSKIP("this machine cannot create a directory junction");
        scene.open(Scene::ready(scene.installerPath(), file.sha256));
        auto *update = scene.update();
        QTRY_COMPARE(update->downloadPhase(), QStringLiteral("ready"));
        update->install();
        QTRY_VERIFY(!update->installProblem().isEmpty());
        QTest::qWait(50);
        QVERIFY(scene.launched.isEmpty());
        QVERIFY(scene.events.isEmpty());
        QDir().rmdir(link);
    }

    void installStartsNothingWhoseChecksumDiffers()
    {
        Scene scene;
        const InstallerFile file = writeInstaller(scene.installerPath());
        QString other = file.sha256;
        other[0] = other.at(0) == QLatin1Char('0') ? QLatin1Char('1') : QLatin1Char('0');
        scene.open(Scene::ready(file.path, other));
        auto *update = scene.update();
        QTRY_COMPARE(update->downloadPhase(), QStringLiteral("ready"));
        update->install();
        QTRY_VERIFY(!update->installProblem().isEmpty());
        QVERIFY(!update->verifyingInstaller());
        QTest::qWait(50);
        QVERIFY(scene.launched.isEmpty());
        QVERIFY(scene.events.isEmpty());
        QVERIFY(update->installProblem().contains(QString::fromUtf8("校验值")));
        // Given up: the file is let go of, and a clean-up can delete it.
        QVERIFY(QFile::remove(file.path));
    }

    void theChecksumIsReadInSlicesAndShownAsVerifying()
    {
        Scene scene;
        const InstallerFile file = writeInstaller(scene.installerPath(), 512 * 1024);
        scene.open(Scene::ready(file.path, file.sha256));
        auto *update = scene.update();
        update->setHashSliceBytes(4096);
        QTRY_COMPARE(update->downloadPhase(), QStringLiteral("ready"));
        update->install();
        // install() returned with the file unread: the window keeps painting.
        QVERIFY(update->verifyingInstaller());
        QCOMPARE(update->downloadActionText(), QString::fromUtf8("正在校验…"));
        QVERIFY(!update->downloadActionEnabled());
        QVERIFY(scene.launched.isEmpty());
        QTRY_COMPARE(scene.launched.size(), 1);
        QVERIFY(!update->verifyingInstaller());
    }

    void installWaitsForTheRunToEnd_data()
    {
        QTest::addColumn<QString>("state");
        QTest::newRow("matched") << "MENTOR_MATCHED";
        QTest::newRow("in-the-duty") << "ENTERED_DUTY";
    }

    void installWaitsForTheRunToEnd()
    {
        QFETCH(QString, state);
        Scene scene;
        const InstallerFile file = writeInstaller(scene.installerPath());
        scene.backend->currentRun.insert(QStringLiteral("state"), state);
        scene.open(Scene::ready(file.path, file.sha256));
        auto *update = scene.update();
        QTRY_COMPARE(update->downloadPhase(), QStringLiteral("ready"));
        QTRY_COMPARE(scene.app->currentRunState(), state);
        update->install();
        QTRY_VERIFY(!update->installProblem().isEmpty());
        QVERIFY(update->installProblem().contains(QString::fromUtf8("导随")));
        QVERIFY(!update->verifyingInstaller());
        QTest::qWait(50);
        QVERIFY(scene.launched.isEmpty());
        QVERIFY(scene.events.isEmpty());
    }

    void installWaitsWhileTheRunIsNotKnown()
    {
        Scene scene;
        const InstallerFile file = writeInstaller(scene.installerPath());
        scene.backend->failCurrentRun = true;
        scene.open(Scene::ready(file.path, file.sha256));
        auto *update = scene.update();
        QTRY_COMPARE(update->downloadPhase(), QStringLiteral("ready"));
        update->install();
        QTRY_VERIFY(!update->installProblem().isEmpty());
        QTest::qWait(50);
        QVERIFY(scene.launched.isEmpty());
    }

    void aDeclinedStartKeepsTheApplicationRunning()
    {
        Scene scene;
        const InstallerFile file = writeInstaller(scene.installerPath());
        scene.launchSucceeds = false;
        scene.open(Scene::ready(file.path, file.sha256));
        auto *update = scene.update();
        QTRY_COMPARE(update->downloadPhase(), QStringLiteral("ready"));
        update->install();
        QTRY_COMPARE(scene.launched.size(), 1);
        QTRY_VERIFY(!update->installProblem().isEmpty());
        QCOMPARE(scene.events, QStringList{QStringLiteral("launch")});
        QCOMPARE(update->downloadPhase(), QStringLiteral("ready"));
        QVERIFY(update->installProblem().contains(QString::fromUtf8("继续运行")));
        QVERIFY(update->browserFallbackOffered());
        // Given up: the file is let go of, and a clean-up can delete it.
        QVERIFY(QFile::remove(file.path));
    }

    void nothingCanReplaceTheInstallerFromItsChecksumToItsStart()
    {
        Scene scene;
        const InstallerFile file = writeInstaller(scene.installerPath(), 512 * 1024);
        mrtest::Tampering atStart;
        scene.duringLaunch = [&atStart](const QString &path) { atStart = mrtest::tryToReplace(path); };
        scene.open(Scene::ready(file.path, file.sha256));
        auto *update = scene.update();
        update->setHashSliceBytes(4096);
        QTRY_COMPARE(update->downloadPhase(), QStringLiteral("ready"));
        update->install();
        QVERIFY(update->verifyingInstaller());
        // While it is read for its checksum...
        const mrtest::Tampering whileChecked = mrtest::tryToReplace(file.path);
        QVERIFY2(!whileChecked.any(), qPrintable(whileChecked.describe()));
        // ...and while the shell is asked to start it.
        QTRY_COMPARE(scene.events, (QStringList{QStringLiteral("launch"), QStringLiteral("quit")}));
        QVERIFY2(!atStart.any(), qPrintable(atStart.describe()));
        QCOMPARE(sha256Of(file.path), file.sha256);
        // A started installer stays held until the application has gone, which
        // the recorded exit never makes it do here; then a clean-up can delete it.
        QVERIFY(!QFile::remove(file.path));
        scene.app.reset();
        QVERIFY(QFile::remove(file.path));
    }

    void aRunThatBeginsDuringTheChecksumStartsNothing()
    {
        bool running = false;
        Scene scene;
        const InstallerFile file = writeInstaller(scene.installerPath(), 512 * 1024);
        scene.open(Scene::ready(file.path, file.sha256));
        auto *update = scene.update();
        update->setRunProbe([&running] { return std::optional<bool>(running); });
        update->setHashSliceBytes(4096);
        QTRY_COMPARE(update->downloadPhase(), QStringLiteral("ready"));
        update->install();
        QVERIFY(update->verifyingInstaller());
        running = true;
        QTRY_VERIFY(!update->installProblem().isEmpty());
        QVERIFY(update->installProblem().contains(QString::fromUtf8("导随")));
        QVERIFY(!update->verifyingInstaller());
        QTest::qWait(50);
        QVERIFY(scene.launched.isEmpty());
        QVERIFY(scene.events.isEmpty());
        QVERIFY(QFile::remove(file.path));
    }

    void aPipeDropDuringTheChecksumStartsNothing()
    {
        Scene scene;
        const InstallerFile file = writeInstaller(scene.installerPath(), 512 * 1024);
        scene.open(Scene::ready(file.path, file.sha256));
        QSignalSpy runRead(scene.app.get(), &mr::AppController::currentRunChanged);
        auto *update = scene.update();
        update->setHashSliceBytes(4096);
        QTRY_COMPARE(update->downloadPhase(), QStringLiteral("ready"));
        // The current run has been read, so without the drop this would start
        // (until it is read, 立即安装 is refused before anything is hashed).
        QTRY_VERIFY(!runRead.isEmpty());
        update->install();
        QVERIFY(update->verifyingInstaller());
        // Reading a local file needs no pipe; what may start is asked again
        // after it, and with the Collector gone the run is no longer known.
        scene.backend->setConnected(false);
        QTRY_VERIFY(!update->installProblem().isEmpty());
        QVERIFY(!update->verifyingInstaller());
        QTest::qWait(50);
        QVERIFY(scene.launched.isEmpty());
        QVERIFY(scene.events.isEmpty());
        QVERIFY(QFile::remove(file.path));
    }

    // -- what starts the installer in a shipping build ------------------------

    void aShippingBuildAsksTheShellAndAMockRunStartsNothing()
    {
        // The launcher the composition installs is called here. QDesktopServices
        // hands every file: address to the stand-in instead of the shell, and on
        // the offscreen platform nothing could be opened even without it.
        if (QGuiApplication::platformName() != QLatin1String("offscreen"))
            QSKIP("calls the shipping launcher: runs on the offscreen platform only");
        ShellStandIn shell;
        QDesktopServices::setUrlHandler(QStringLiteral("file"), &shell, "open");
        const auto restore = qScopeGuard([] { QDesktopServices::unsetUrlHandler(QStringLiteral("file")); });

        // An IPC build, on a pipe no Collector serves; only what needs one is
        // stood in for, never the launcher.
        QTemporaryDir directory;
        const InstallerFile file =
            writeInstaller(directory.filePath(QStringLiteral("updates/MentorRecorder-9.9.9-setup.exe")));
        QVERIFY(!file.sha256.isEmpty());
        mr::IpcBackend ipc(nullptr, QStringLiteral("MentorRecorder-test-%1")
                                        .arg(QUuid::createUuid().toString(QUuid::WithoutBraces)));
        mr::AppController shipping(&ipc, nullptr);
        int quits = 0;
        standInForTheCollector(shipping.update(), directory.path(), &quits);
        shipping.update()->refreshFromStatus(
            {{QStringLiteral("update"), Scene::ready(file.path, file.sha256).toVariantMap()}});
        QCOMPARE(shipping.update()->downloadPhase(), QStringLiteral("ready"));
        shipping.update()->install();
        QTRY_COMPARE(quits, 1);
        QCOMPARE(shell.opened.size(), 1);
        QVERIFY(shell.opened.constFirst().isLocalFile());
        QCOMPARE(QDir::fromNativeSeparators(shell.opened.constFirst().toLocalFile()), file.path);

        // A mock run: its "installer" is a text file it wrote itself, and the
        // composition gives it a launcher that starts nothing.
        mr::MockBackend mock;
        QTemporaryDir mockDirectory;
        mock.setDataDirectory(mockDirectory.path());
        mock.setLiveMode(mr::MockBackend::LiveMode::None);
        mock.setUpdateDownloadFixture(QStringLiteral("ready"));
        mr::AppController mockRun(&mock, nullptr);
        int mockQuits = 0;
        standInForTheCollector(mockRun.update(), mockDirectory.path(), &mockQuits);
        QTRY_COMPARE(mockRun.update()->downloadPhase(), QStringLiteral("ready"));
        mockRun.update()->install();
        QTRY_VERIFY(!mockRun.update()->installProblem().isEmpty());
        QVERIFY(mockRun.update()->installProblem().contains(QString::fromUtf8("没有启动")));
        QTest::qWait(50);
        QCOMPARE(mockQuits, 0);
        QCOMPARE(shell.opened.size(), 1);
    }

    void aRefusalShowsAPlayerItsSentenceAndAMaintainerTheCodeToo()
    {
        for (const bool maintainer : {false, true}) {
            Scene scene;
            const InstallerFile file = writeInstaller(scene.installerPath());
            QString other = file.sha256;
            other[0] = other.at(0) == QLatin1Char('0') ? QLatin1Char('1') : QLatin1Char('0');
            scene.open(Scene::ready(file.path, other));
            scene.app->setMaintainerToolsVisible(maintainer);
            auto *update = scene.update();
            QTRY_COMPARE(update->downloadPhase(), QStringLiteral("ready"));
            update->install();
            QTRY_VERIFY(!update->installProblem().isEmpty());
            const QString text = update->installProblem();
            static const QRegularExpression code(QStringLiteral("\\([A-Z_]+\\)$"));
            QCOMPARE(text.contains(code), maintainer);
            if (!maintainer)
                QVERIFY2(readsWithoutLatin(text), qPrintable(text));
        }
    }

    void everySentenceReadsWithoutInternalNames()
    {
        Scene scene;
        const InstallerFile file = writeInstaller(scene.installerPath());
        const QList<QJsonObject> states{
            updateStatus(download(QStringLiteral("IDLE"))),
            updateStatus(download(QStringLiteral("DOWNLOADING"),
                {{QStringLiteral("received_bytes"), kMiB}, {QStringLiteral("total_bytes"), 2 * kMiB}})),
            updateStatus(download(QStringLiteral("DOWNLOADING"), {{QStringLiteral("received_bytes"), kMiB}})),
            updateStatus(download(QStringLiteral("VERIFYING"), {{QStringLiteral("received_bytes"), kMiB}})),
            Scene::ready(file.path, file.sha256),
            updateStatus(download(QStringLiteral("FAILED"), {{QStringLiteral("failure"), QStringLiteral("TIMEOUT")}})),
        };
        scene.open(states.first());
        auto *update = scene.update();
        for (const QJsonObject &state : states) {
            scene.backend->update = state;
            scene.app->refreshStatus();
            const QString wanted = state.value(QStringLiteral("download")).toObject()
                                       .value(QStringLiteral("state")).toString();
            QTRY_VERIFY(update->downloadPhase().length() > 0
                        && (wanted != QLatin1String("READY") || update->downloadPhase() == QLatin1String("ready")));
            for (const QString &text : {update->downloadStatusText(), update->downloadActionText(),
                                        update->detail()}) {
                QVERIFY2(readsWithoutLatin(text), qPrintable(text));
            }
        }
    }

    // -- the mock -------------------------------------------------------------

    void theMockSimulatesTheWholeDownloadOffline()
    {
        mr::MockBackend backend;
        QTemporaryDir directory;
        backend.setDataDirectory(directory.path());
        backend.setUpdateAvailable(true);
        backend.setUpdateDownloadStepMs(10);
        backend.setLiveMode(mr::MockBackend::LiveMode::None);
        mr::AppController app(&backend, nullptr);
        auto *update = app.update();
        update->setDownloadPollMs(20);
        QTRY_COMPARE(update->downloadPhase(), QStringLiteral("offer"));
        update->startDownload();
        QTRY_COMPARE(update->downloadPhase(), QStringLiteral("downloading"));
        QTRY_COMPARE(update->downloadPhase(), QStringLiteral("ready"));
        QVERIFY(!update->polling());
        QCOMPARE(backend.startUpdateDownloadCount(), 1);
        const QJsonObject reported = app.collectorStatus().value(QStringLiteral("update")).toMap()
                                         .value(QStringLiteral("download")).toJsonObject();
        const QString path = reported.value(QStringLiteral("file_path")).toString();
        QFile file(path);
        QVERIFY2(file.open(QIODevice::ReadOnly), qPrintable(path));
        QCOMPARE(QString::fromLatin1(QCryptographicHash::hash(file.readAll(),
                                                              QCryptographicHash::Sha256).toHex()),
                 reported.value(QStringLiteral("sha256")).toString());
        QVERIFY(QDir::fromNativeSeparators(path).startsWith(
            QDir::fromNativeSeparators(directory.path()) + QStringLiteral("/updates/")));
    }

    void theMockCancelsAndRefusesLikeTheCollector()
    {
        mr::MockBackend backend;
        QTemporaryDir directory;
        backend.setDataDirectory(directory.path());
        backend.setUpdateAvailable(true);
        backend.setUpdateDownloadFixture(QStringLiteral("downloading"));
        backend.setLiveMode(mr::MockBackend::LiveMode::None);
        mr::AppController app(&backend, nullptr);
        auto *update = app.update();
        QTRY_COMPARE(update->downloadPhase(), QStringLiteral("downloading"));
        update->cancelDownload();
        QTRY_COMPARE(update->downloadPhase(), QStringLiteral("offer"));
        QCOMPARE(backend.cancelUpdateDownloadCount(), 1);

        // Nothing newer known: 下载并安装 is not offered, and the maintainer's
        // reinstall is answered NO_UPDATE, with a sentence for the player.
        backend.setUpdateAvailable(false);
        app.refreshStatus();
        QTRY_VERIFY(!update->updateAvailable());
        QCOMPARE(update->downloadPhase(), QString());
        update->startDownload();
        QTest::qWait(30);
        QCOMPARE(backend.startUpdateDownloadCount(), 0);
        update->reinstallLatest();
        QTRY_COMPARE(backend.startUpdateDownloadCount(), 1);
        QCOMPARE(backend.lastStartUpdateDownload(), (QJsonObject{{QStringLiteral("reinstall"), true}}));
        // With no newer version on offer the refusal is said once; the update
        // places keep 检查更新 rather than a 重试 that cannot succeed.
        QTRY_COMPARE(app.toastMessage(), QString::fromUtf8("没有比当前版本更新的版本可以下载。"));
        QCOMPARE(update->downloadPhase(), QString());
        QVERIFY(update->canCheck());
        QVERIFY(readsWithoutLatin(app.toastMessage()));

        // An old Collector: no download object and the messages refused.
        mr::MockBackend old;
        old.setUpdateAvailable(true);
        old.setUpdateDownloadSupported(false);
        mr::AppController oldApp(&old, nullptr);
        QTRY_COMPARE(oldApp.update()->downloadPhase(), QStringLiteral("browser"));
    }

    // -- the three places -----------------------------------------------------

    void theBannerDownloadsThenInstalls()
    {
        PageScene scene;
        QVERIFY(scene.open(QStringLiteral("DashboardPage")));
        QTRY_VERIFY(scene.shows(QStringLiteral("updateNotice")));
        QTRY_COMPARE(scene.textOf(QStringLiteral("downloadInstallerButton")),
                     QString::fromUtf8("下载并安装"));
        QVERIFY(scene.press(QStringLiteral("downloadInstallerButton")));
        QTRY_COMPARE(scene.textOf(QStringLiteral("downloadInstallerButton")),
                     QString::fromUtf8("立即安装"));
        QVERIFY(scene.opened.isEmpty());
        QVERIFY(scene.shows(QStringLiteral("updateNoticeDownloadStatusText")));
        QVERIFY(scene.textOf(QStringLiteral("updateNoticeDownloadStatusText"))
                    .contains(QString::fromUtf8("管理员")));
        // Once there is something to install, it is no longer one to ignore.
        QVERIFY(!scene.shows(QStringLiteral("dismissUpdateButton")));
        QVERIFY(scene.shows(QStringLiteral("openReleasePageButton")));

        QVERIFY(scene.press(QStringLiteral("downloadInstallerButton")));
        QTRY_COMPARE(scene.quits, 1);
        QCOMPARE(scene.launched.size(), 1);
        QVERIFY(QDir::fromNativeSeparators(scene.launched.constFirst())
                    .endsWith(QStringLiteral("/updates/MentorRecorder-99.9.9-setup.exe")));
    }

    void theBannerStaysForADownloadAfterIgnore()
    {
        PageScene scene;
        QVERIFY(scene.open(QStringLiteral("DashboardPage"), QStringLiteral("downloading")));
        QTRY_VERIFY(scene.shows(QStringLiteral("updateNotice")));
        scene.app->update()->dismiss();
        QTest::qWait(50);
        QVERIFY(scene.shows(QStringLiteral("updateNotice")));
        scene.settings->setDismissedUpdateVersion(QString());
    }

    void theGeneralTabShowsTheProgressAndCancels()
    {
        PageScene scene;
        QVERIFY(scene.open(QStringLiteral("SettingsGeneralTab"), QStringLiteral("downloading")));
        QTRY_COMPARE(scene.textOf(QStringLiteral("checkUpdateNowButton")), QString::fromUtf8("取消"));
        QVERIFY(scene.shows(QStringLiteral("settingsUpdateDownloadProgress")));
        QVERIFY(scene.textOf(QStringLiteral("settingsUpdateDownloadStatusText"))
                    .contains(QStringLiteral("45%")));
        QVERIFY(scene.press(QStringLiteral("checkUpdateNowButton")));
        QTRY_COMPARE(scene.textOf(QStringLiteral("checkUpdateNowButton")),
                     QString::fromUtf8("下载并安装"));
        QCOMPARE(scene.backend->cancelUpdateDownloadCount(), 1);
        QVERIFY(!scene.shows(QStringLiteral("settingsUpdateDownloadProgress")));
    }

    void theAboutTabOffersRetryAndTheBrowserAfterAFailure()
    {
        PageScene scene;
        QVERIFY(scene.open(QStringLiteral("SettingsAboutTab"), QStringLiteral("failed")));
        QTRY_COMPARE(scene.textOf(QStringLiteral("aboutDownloadInstallerButton")),
                     QString::fromUtf8("重试"));
        QVERIFY(!scene.shows(QStringLiteral("aboutCheckUpdateButton")));
        QVERIFY(scene.shows(QStringLiteral("aboutDownloadStatusText")));
        QVERIFY(scene.shows(QStringLiteral("aboutBrowserDownloadButton")));
        QCOMPARE(scene.textOf(QStringLiteral("aboutBrowserDownloadButton")),
                 QString::fromUtf8("在浏览器中下载"));
        QVERIFY(scene.press(QStringLiteral("aboutBrowserDownloadButton")));
        QTRY_COMPARE(scene.opened, QStringList{QLatin1String(kMockInstallerUrl)});
        QVERIFY(scene.press(QStringLiteral("aboutDownloadInstallerButton")));
        QTRY_COMPARE(scene.backend->startUpdateDownloadCount(), 1);
    }

    void reinstallIsInTheMaintainerToolsOnly()
    {
        PageScene player;
        QVERIFY(player.open(QStringLiteral("SettingsGeneralTab")));
        QTRY_VERIFY(player.app->update()->downloadSupported());
        QVERIFY(!player.shows(QStringLiteral("reinstallLatestButton")));

        PageScene maintainer;
        QVERIFY(maintainer.open(QStringLiteral("SettingsGeneralTab"), QString(), true));
        QTRY_VERIFY(maintainer.shows(QStringLiteral("reinstallLatestButton")));
        QCOMPARE(maintainer.textOf(QStringLiteral("reinstallLatestButton")),
                 QString::fromUtf8("重新下载最新正式版"));
        QTRY_VERIFY(maintainer.item(QStringLiteral("reinstallLatestButton"))->isEnabled());
        QVERIFY(maintainer.press(QStringLiteral("reinstallLatestButton")));
        QTRY_COMPARE(maintainer.backend->startUpdateDownloadCount(), 1);
        QCOMPARE(maintainer.backend->lastStartUpdateDownload(),
                 (QJsonObject{{QStringLiteral("reinstall"), true}}));
    }

    void anOldCollectorKeepsTheBrowserButtonEverywhere()
    {
        for (const QString &element : {QStringLiteral("DashboardPage"),
                                       QStringLiteral("SettingsGeneralTab"),
                                       QStringLiteral("SettingsAboutTab")}) {
            PageScene scene;
            scene.backend->setUpdateDownloadSupported(false);
            QVERIFY(scene.open(element));
            const QString button = element == QLatin1String("DashboardPage")
                                       ? QStringLiteral("downloadInstallerButton")
                                       : element == QLatin1String("SettingsGeneralTab")
                                             ? QStringLiteral("checkUpdateNowButton")
                                             : QStringLiteral("aboutDownloadInstallerButton");
            QTRY_COMPARE(scene.textOf(button), QString::fromUtf8("下载新版本"));
            QVERIFY(scene.press(button));
            QTRY_COMPARE(scene.opened, QStringList{QLatin1String(kMockInstallerUrl)});
        }
    }

    // -- the application quits (last: they emit QCoreApplication::aboutToQuit) --

    void theReReadStopsWhenTheApplicationQuits()
    {
        Scene scene;
        scene.open(updateStatus(download(QStringLiteral("DOWNLOADING"),
            {{QStringLiteral("received_bytes"), kMiB}})));
        auto *update = scene.update();
        QTRY_VERIFY(update->polling());
        QTRY_VERIFY(scene.backend->statusReads >= 3);
        emitAboutToQuit();
        QVERIFY(!update->polling());
        // A read already out may still be answered; none is sent after it.
        QTest::qWait(50);
        const int settled = scene.backend->statusReads;
        QTest::qWait(150);
        QCOMPARE(scene.backend->statusReads, settled);
        QVERIFY(!update->polling());
    }

    void quittingStopsTheChecksum()
    {
        Scene scene;
        const InstallerFile file = writeInstaller(scene.installerPath(), 512 * 1024);
        scene.open(Scene::ready(file.path, file.sha256));
        auto *update = scene.update();
        update->setHashSliceBytes(1024);
        QTRY_COMPARE(update->downloadPhase(), QStringLiteral("ready"));
        update->install();
        QVERIFY(update->verifyingInstaller());
        emitAboutToQuit();
        QVERIFY(!update->verifyingInstaller());
        QTest::qWait(200);
        QVERIFY(scene.launched.isEmpty());
        QVERIFY(scene.events.isEmpty());
        // And 立即安装 is not taken up again on the way out.
        update->install();
        QTest::qWait(100);
        QVERIFY(scene.launched.isEmpty());
        // Given up: the file is let go of, and a clean-up can delete it.
        QVERIFY(QFile::remove(file.path));
    }

    void aStartedInstallerIsHeldUntilTheApplicationQuits()
    {
        Scene scene;
        const InstallerFile file = writeInstaller(scene.installerPath());
        scene.open(Scene::ready(file.path, file.sha256));
        auto *update = scene.update();
        QTRY_COMPARE(update->downloadPhase(), QStringLiteral("ready"));
        update->install();
        QTRY_COMPARE(scene.events, (QStringList{QStringLiteral("launch"), QStringLiteral("quit")}));
        // The exit has been asked for; until the application leaves, the file
        // the installer was started from stays as it was.
        QVERIFY(!QFile::remove(file.path));
        emitAboutToQuit();
        QVERIFY(QFile::remove(file.path));
    }
};

int main(int argc, char **argv)
{
    // Never run against the user's own Collector, database or serve lease - see
    // TestCollectorGuard.h. Must precede any construction.
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication app(argc, argv);
#ifdef Q_OS_WIN
    // The offscreen platform finds no system fonts; the copy under test is CJK.
    QStringList cjkFamilies;
    const QString windowsDir = qEnvironmentVariable("WINDIR", QStringLiteral("C:/Windows"));
    for (const char *file : {"Fonts/msyh.ttc", "Fonts/simhei.ttf"}) {
        const int id = QFontDatabase::addApplicationFont(QDir(windowsDir).filePath(QString::fromLatin1(file)));
        for (const QString &family : QFontDatabase::applicationFontFamilies(id)) {
            if (!cjkFamilies.contains(family))
                cjkFamilies.append(family);
        }
    }
    if (!cjkFamilies.isEmpty()) {
        QFont testFont;
        testFont.setFamilies(cjkFamilies);
        app.setFont(testFont);
    }
#endif
    UpdateInstallTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "UpdateInstallTests.moc"
