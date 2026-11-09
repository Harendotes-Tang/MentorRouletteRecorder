#include "DesktopTestRunner.h"
#include "workflows/DailyBackupScheduler.h"

#include <QCoreApplication>
#include <QSignalSpy>
#include <QTest>

#include <memory>

namespace {
class DailyBackupSchedulerTests final : public QObject
{
    Q_OBJECT
private Q_SLOTS:
    void inactiveObservationDoesNotEnableBackgroundWrites()
    {
        mr::DailyBackupScheduler scheduler([] { return mr::DailyBackupScheduler::Context{.enabled = true, .connected = true, .runKnown = true}; });
        QSignalSpy requests(&scheduler, &mr::DailyBackupScheduler::backupRequested);
        scheduler.checkIfActive();
        QVERIFY(!scheduler.active());
        QCOMPARE(requests.count(), 0);
    }

    void waitsForConnectionAndKnownRunWithoutBurningAttempt()
    {
        mr::DailyBackupScheduler::Context state{.enabled = true};
        mr::DailyBackupScheduler scheduler([&state] { return state; });
        QSignalSpy requests(&scheduler, &mr::DailyBackupScheduler::backupRequested);
        scheduler.runIfDue();
        QCOMPARE(requests.count(), 0);
        state.connected = true;
        scheduler.checkIfActive();
        QCOMPARE(requests.count(), 0);
        state.runKnown = true;
        scheduler.checkIfActive();
        QCOMPARE(requests.count(), 1);
        scheduler.runIfDue();
        QCOMPARE(requests.count(), 1);
    }

    void failedAttemptRetriesNextDayAndSuccessStampsCurrentDay()
    {
        QDate day(2026, 11, 9);
        mr::DailyBackupScheduler scheduler([] { return mr::DailyBackupScheduler::Context{.enabled = true, .connected = true, .runKnown = true}; });
        scheduler.setTodayForTest([&day] { return day; });
        QSignalSpy requests(&scheduler, &mr::DailyBackupScheduler::backupRequested);
        QSignalSpy recorded(&scheduler, &mr::DailyBackupScheduler::backupRecorded);
        scheduler.runIfDue();
        scheduler.runIfDue();
        QCOMPARE(requests.count(), 1);
        QCOMPARE(recorded.count(), 0);
        day = day.addDays(1);
        scheduler.runIfDue();
        QCOMPARE(requests.count(), 2);
        scheduler.recordSuccess();
        QCOMPARE(recorded.count(), 1);
        QCOMPARE(recorded.first().first().toString(), QStringLiteral("2026-11-10"));
    }

    void persistedSuccessAndDisabledSettingPreventAttempt()
    {
        mr::DailyBackupScheduler::Context state{.connected = true, .runKnown = true};
        mr::DailyBackupScheduler scheduler([&state] { return state; });
        scheduler.setTodayForTest([] { return QDate(2026, 11, 9); });
        QSignalSpy requests(&scheduler, &mr::DailyBackupScheduler::backupRequested);
        scheduler.runIfDue();
        state.enabled = true;
        state.lastBackupDate = QStringLiteral("2026-11-09");
        scheduler.checkIfActive();
        QCOMPARE(requests.count(), 0);
        state.lastBackupDate.clear();
        scheduler.checkIfActive();
        QCOMPARE(requests.count(), 1);
    }

    void nextDutyRestartsPostDutyDeferral()
    {
        mr::DailyBackupScheduler::Context state{.enabled = true, .connected = true, .runKnown = true, .dutyInProgress = true};
        mr::DailyBackupScheduler scheduler([&state] { return state; });
        scheduler.setAfterDutyIntervalForTest(120);
        QSignalSpy requests(&scheduler, &mr::DailyBackupScheduler::backupRequested);
        scheduler.runIfDue();
        state.dutyInProgress = false;
        scheduler.checkIfActive();
        QTest::qWait(30);
        QCOMPARE(requests.count(), 0);
        state.dutyInProgress = true;
        scheduler.checkIfActive();
        QTest::qWait(150);
        QCOMPARE(requests.count(), 0);
        state.dutyInProgress = false;
        scheduler.checkIfActive();
        QTest::qWait(30);
        QCOMPARE(requests.count(), 0);
        QTRY_COMPARE(requests.count(), 1);
    }

    void destructionCancelsDeferredChecks()
    {
        mr::DailyBackupScheduler::Context state{.enabled = true, .connected = true, .runKnown = true, .dutyInProgress = true};
        int requests = 0;
        auto scheduler = std::make_unique<mr::DailyBackupScheduler>([&state] { return state; });
        connect(scheduler.get(), &mr::DailyBackupScheduler::backupRequested, this, [&requests] { ++requests; });
        scheduler->setAfterDutyIntervalForTest(50);
        scheduler->runIfDue();
        state.dutyInProgress = false;
        scheduler->checkIfActive();
        scheduler.reset();
        QTest::qWait(80);
        QCOMPARE(requests, 0);
    }
};
}

MR_DESKTOP_QTEST_SUITE(DailyBackupScheduler, DailyBackupSchedulerTests, QCoreApplication)
#include "DailyBackupSchedulerTests.moc"
