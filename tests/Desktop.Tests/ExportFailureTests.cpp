#include "TestCollectorGuard.h"
#include "ExportController.h"
#include "MockBackend.h"

#include <QDesktopServices>
#include <QScopeGuard>
#include <QSignalSpy>
#include <QTemporaryDir>
#include <QtTest>

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

} // namespace

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
};

int main(int argc, char **argv)
{
    // Never run against the user's own Collector, database or serve lease - see
    // TestCollectorGuard.h. Must precede any construction.
    mrtest::disableCollectorLaunch();
    QGuiApplication app(argc, argv);
    ExportFailureTests tests;
    return QTest::qExec(&tests, argc, argv);
}
#include "ExportFailureTests.moc"
