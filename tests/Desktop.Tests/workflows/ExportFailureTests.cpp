#include "DesktopTestRunner.h"
#include "TestCollectorGuard.h"
#include "ExportController.h"
#include "Formatters.h"
#include "MockBackend.h"

#include <QDesktopServices>
#include <QPointer>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickStyle>
#include <QScopeGuard>
#include <QSignalSpy>
#include <QTemporaryDir>
#include <QtTest>

#include <memory>

namespace {

/// Stands in for the shell: records the folders that would have been opened.
class OpenedFolders final : public QObject
{
    Q_OBJECT
public:
    QList<QUrl> urls;
public Q_SLOTS:
    void open(const QUrl &url) { urls.append(url); }
};

class BackupReplyBackend final : public mr::MockBackend
{
public:
    QJsonObject result;

    mr::BackendReply *request(const QString &messageType,
                              const QJsonObject &payload = {}) override
    {
        if (messageType != QLatin1String("BackupDatabase"))
            return MockBackend::request(messageType, payload);
        auto *reply = new mr::BackendReply(QStringLiteral("backup-test"), messageType, this);
        reply->succeed(result);
        return reply;
    }
};

/// Keeps every export and backup request, and leaves the backup unanswered until told.
class RecordingExportBackend final : public mr::MockBackend
{
public:
    QHash<QString, QJsonObject> exports;
    int backups = 0;
    QPointer<mr::BackendReply> pendingBackup;

    mr::BackendReply *request(const QString &messageType,
                              const QJsonObject &payload = {}) override
    {
        if (messageType == QLatin1String("BackupDatabase")) {
            ++backups;
            pendingBackup = new mr::BackendReply(QStringLiteral("backup-pending"), messageType, this);
            return pendingBackup;
        }
        if (messageType.startsWith(QLatin1String("Export")))
            exports.insert(messageType, payload);
        return MockBackend::request(messageType, payload);
    }
};

/// 设置 · 数据 on its own, with a stand-in for the application object.
constexpr auto kDataTabApp = R"(import QtQuick
QtObject {
    property bool dark: false
    property var collectorStatus: ({})
    property bool backupRunning: false
    property bool integrityCheckRunning: false
    property var integrityCheckResult: ({})
    property bool captureSettingsSupported: true
    property bool captureSettingsLoaded: true
    property var captureSettings: ({ log_retention_days: 14 })
    property bool historyRetentionLoaded: true
    property int historyRetentionDays: 30
    property bool historyRetentionSaving: false
    property string historyRetentionFeedback: ""
    property string historyImageCleanupFeedback: ""
    property bool historyImageCleanupRunning: false
    property bool backendConnected: true
    property int backupRequests: 0
    function refreshHistoryRetentionSettings() {}
    function backupDatabase() { ++backupRequests }
})";

} // namespace

namespace {
class ExportFailureTests final : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void unverifiedBackupDoesNotSignalSuccess_data()
    {
        QTest::addColumn<QJsonObject>("result");
        QTest::newRow("failed-integrity")
            << QJsonObject{{QStringLiteral("integrity_check_passed"), false}};
        QTest::newRow("missing-integrity") << QJsonObject{};
    }

    void unverifiedBackupDoesNotSignalSuccess()
    {
        QFETCH(QJsonObject, result);
        BackupReplyBackend backend;
        backend.result = result;
        mr::ExportController controller;
        controller.setBackend(&backend);
        QSignalSpy succeeded(&controller, &mr::ExportController::backupSucceeded);
        QSignalSpy toast(&controller, &mr::ExportController::toastRequested);

        controller.backupDatabase();

        QCOMPARE(succeeded.count(), 0);
        QCOMPARE(toast.count(), 1);
        QVERIFY(toast.first().first().toString().contains(QString::fromUtf8("备份的数据检查未通过")));
    }

    void verifiedBackupSignalsSuccess()
    {
        BackupReplyBackend backend;
        backend.result = {{QStringLiteral("integrity_check_passed"), true}};
        mr::ExportController controller;
        controller.setBackend(&backend);
        QSignalSpy succeeded(&controller, &mr::ExportController::backupSucceeded);

        controller.backupDatabase();

        QCOMPARE(succeeded.count(), 1);
    }

    // Audit 2026-10-20 I3-7: before the Collector has named its database, 打开目录 opens the
    // folder the Collector uses - MR_DATA_DIR or %LOCALAPPDATA%\MentorRecorder - and never
    // creates one of its own.
    void databaseFolderFallbackIsTheCollectorDataDirectory()
    {
        QTemporaryDir root;
        QVERIFY(root.isValid());
        const QString dataDirectory = QDir(root.path()).filePath(QStringLiteral("collector-data"));
        const QByteArray previous = qgetenv("MR_DATA_DIR");
        const bool hadPrevious = qEnvironmentVariableIsSet("MR_DATA_DIR");
        qputenv("MR_DATA_DIR", dataDirectory.toUtf8());
        OpenedFolders opened;
        QDesktopServices::setUrlHandler(QStringLiteral("file"), &opened, "open");
        const auto restore = qScopeGuard([&] {
            QDesktopServices::unsetUrlHandler(QStringLiteral("file"));
            if (hadPrevious)
                qputenv("MR_DATA_DIR", previous);
            else
                qunsetenv("MR_DATA_DIR");
        });
        mr::ExportController controller;
        QSignalSpy toast(&controller, &mr::ExportController::toastRequested);

        controller.openDatabaseFolder({});
        QVERIFY(!QFileInfo::exists(dataDirectory));
        QVERIFY(opened.urls.isEmpty());
        QCOMPARE(toast.count(), 1);
        QVERIFY(toast.last().first().toString().contains(QDir::toNativeSeparators(dataDirectory)));

        QVERIFY(QDir().mkpath(dataDirectory));
        controller.openDatabaseFolder(QDir(root.path()).filePath(QStringLiteral("gone/mentor.db")));
        QCOMPARE(opened.urls, QList<QUrl>{QUrl::fromLocalFile(QDir::cleanPath(dataDirectory))});
    }

    // Audit 2026-11-03 G-2: the save dialog has already asked 「是否替换」 about an existing
    // file, so the export it confirmed must not be refused as 「目标文件已存在」. A target
    // nobody confirmed (--export-target) still never replaces anything.
    void confirmedSaveTargetIsReplacedAndAnUnconfirmedOneIsNot()
    {
        QTemporaryDir directory;
        QVERIFY(directory.isValid());
        RecordingExportBackend backend;
        mr::ExportController controller;
        controller.setBackend(&backend);
        QStringList suggested;
        controller.setSaveDialogForTesting([&](const QString &name, const QString &) {
            suggested.append(QFileInfo(name).fileName());
            const QString path = directory.filePath(QFileInfo(name).fileName());
            QFile existing(path); // The file the dialog asked about.
            return existing.open(QIODevice::WriteOnly) ? path : QString();
        });
        controller.exportCsv();
        controller.exportJson();
        controller.exportDiagnosticsReport();
        QCOMPARE(suggested.size(), 3);
        for (const QString &type : {QStringLiteral("ExportCsv"), QStringLiteral("ExportJson"),
                                   QStringLiteral("ExportDiagnosticsReport")}) {
            QVERIFY2(backend.exports.contains(type), qPrintable(type));
            QCOMPARE(backend.exports.value(type).value(QStringLiteral("overwrite")), QJsonValue(true));
        }

        backend.exports.clear();
        controller.setTargetOverride(directory.path());
        controller.exportCsv();
        controller.exportJson();
        controller.exportDiagnosticsReport();
        QCOMPARE(suggested.size(), 3); // No dialog for the harness target.
        for (const QString &type : {QStringLiteral("ExportCsv"), QStringLiteral("ExportJson"),
                                   QStringLiteral("ExportDiagnosticsReport")}) {
            QVERIFY2(backend.exports.contains(type), qPrintable(type));
            QVERIFY2(!backend.exports.value(type).contains(QStringLiteral("overwrite")), qPrintable(type));
        }
    }

    // Audit 2026-11-03 K1-8: 立即备份 pressed again while a backup runs writes no second copy,
    // which would also push the oldest kept backup out of the managed folder.
    void backupNowIgnoresPressesWhileOneRuns()
    {
        RecordingExportBackend backend;
        mr::ExportController controller;
        controller.setBackend(&backend);
        QSignalSpy running(&controller, &mr::ExportController::backupRunningChanged);
        QVERIFY(!controller.backupRunning());
        controller.backupDatabase();
        QVERIFY(controller.backupRunning());
        controller.backupDatabase();
        controller.backupDatabase();
        QCOMPARE(backend.backups, 1);
        QVERIFY(backend.pendingBackup);
        backend.pendingBackup->succeed({{QStringLiteral("integrity_check_passed"), true}});
        QVERIFY(!controller.backupRunning());
        QCOMPARE(running.count(), 2);
        controller.backupDatabase(); // The next press after the answer is a new backup.
        QCOMPARE(backend.backups, 2);
        backend.pendingBackup->failTransport(QStringLiteral("ERR_TIMEOUT"), QStringLiteral("后台服务暂时没有响应"));
        QVERIFY(!controller.backupRunning()); // A failure releases the button as well.
    }

    // Audit 2026-11-03 K1-8 / K1-9: 设置 · 数据 shows a backup in progress, and no invented
    // database path before the Collector has named its own.
    void dataTabShowsTheBackupInProgressAndOnlyTheRealDatabasePath()
    {
        QQuickStyle::setStyle(QStringLiteral("Basic")); // The application's own controls style.
        const QString root = QString::fromUtf8(MR_DESKTOP_QML_DIR);
        qmlRegisterSingletonType(QUrl::fromLocalFile(root + QStringLiteral("/Theme.qml")), "MentorRecorder", 1, 0, "Theme");
        for (const auto &file : QDir(root + QStringLiteral("/components")).entryList({QStringLiteral("*.qml")}, QDir::Files))
            qmlRegisterType(QUrl::fromLocalFile(root + QStringLiteral("/components/") + file),
                            "MentorRecorder", 1, 0, file.chopped(4).toUtf8().constData());
        QQmlEngine engine;
        QQmlComponent appComponent(&engine);
        appComponent.setData(kDataTabApp, QUrl());
        std::unique_ptr<QObject> app(appComponent.create());
        QVERIFY2(app, qPrintable(appComponent.errorString()));
        QQmlComponent settingsComponent(&engine);
        settingsComponent.setData("import QtQuick\nQtObject { property bool autoBackup: false }", QUrl());
        std::unique_ptr<QObject> settings(settingsComponent.create());
        QVERIFY2(settings, qPrintable(settingsComponent.errorString()));
        mr::Formatters formatters;
        engine.rootContext()->setContextProperty(QStringLiteral("App"), app.get());
        engine.rootContext()->setContextProperty(QStringLiteral("Settings"), settings.get());
        engine.rootContext()->setContextProperty(QStringLiteral("Fmt"), &formatters);
        engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QQmlComponent component(&engine, QUrl::fromLocalFile(root + QStringLiteral("/pages/SettingsDataTab.qml")));
        QTRY_VERIFY_WITH_TIMEOUT(component.status() != QQmlComponent::Loading, 5000);
        std::unique_ptr<QObject> tab(component.create());
        QVERIFY2(tab, qPrintable(component.errorString()));
        auto *path = tab->findChild<QQuickItem *>(QStringLiteral("databasePathField"));
        auto *backup = tab->findChild<QQuickItem *>(QStringLiteral("backupNowButton"));
        QVERIFY(path && backup);

        QCOMPARE(path->property("text").toString(), mr::Formatters::dash());
        const QString database = QStringLiteral("D:/data/MentorRecorder-dev/mentor_recorder.db");
        app->setProperty("collectorStatus", QVariantMap{{QStringLiteral("database_path"), database}});
        QCOMPARE(path->property("text").toString(), database);

        QVERIFY(backup->isEnabled());
        QCOMPARE(backup->property("text").toString(), QString::fromUtf8("立即备份"));
        app->setProperty("backupRunning", true);
        QVERIFY(!backup->isEnabled());
        QCOMPARE(backup->property("text").toString(), QString::fromUtf8("正在备份…"));
        app->setProperty("backupRunning", false);
        QVERIFY(backup->isEnabled());
    }
};
} // namespace

MR_DESKTOP_TEST_SUITE(ExportFailure)
{
    // Never run against the user's own Collector, database or serve lease - see
    // TestCollectorGuard.h. Must precede any construction.
    mrtest::disableCollectorLaunch();
    QGuiApplication app(argc, argv);
    ExportFailureTests tests;
    return QTest::qExec(&tests, argc, argv);
}
#include "ExportFailureTests.moc"
