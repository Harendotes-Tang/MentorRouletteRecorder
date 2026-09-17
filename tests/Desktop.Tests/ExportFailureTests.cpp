#include "TestCollectorGuard.h"
#include "ExportController.h"
#include "MockBackend.h"

#include <QSignalSpy>
#include <QtTest>

namespace {

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
        QVERIFY(toast.first().first().toString().contains(QString::fromUtf8("备份失败")));
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
