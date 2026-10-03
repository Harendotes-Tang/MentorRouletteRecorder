// ---------------------------------------------------------------------------
// One Desktop per user (review OH-1).
//
// Every test uses a key of its own, never SingleInstanceGuard::defaultKey():
// that one is the guard a Desktop the developer has open is holding, and a test
// that took it - or signalled it - would bring that window forward.
// ---------------------------------------------------------------------------

#include "TestCollectorGuard.h"
#include "PipeName.h"
#include "SingleInstanceGuard.h"

#include <QCoreApplication>
#include <QSignalSpy>
#include <QTest>

namespace {

QString uniqueKey()
{
    static int counter = 0;
    return QStringLiteral("MentorRecorderTest.%1.%2.desktop")
        .arg(QCoreApplication::applicationPid())
        .arg(++counter);
}

} // namespace

class SingleInstanceTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    /// The second launch is told it is the second, before it has created a
    /// supervisor, a tray icon or an announcer.
    void aSecondLaunchIsNotTheFirstInstance()
    {
#ifndef Q_OS_WIN
        QSKIP("The guard is a pair of Win32 kernel objects.");
#else
        const QString key = uniqueKey();
        mr::SingleInstanceGuard first(key);
        QVERIFY(first.isPrimary());
        mr::SingleInstanceGuard second(key);
        QVERIFY(!second.isPrimary());
#endif
    }

    /// Instead of opening a window of its own, the second launch brings the
    /// first one forward.
    void aSecondLaunchAsksTheFirstToShowItsWindow()
    {
#ifndef Q_OS_WIN
        QSKIP("The guard is a pair of Win32 kernel objects.");
#else
        const QString key = uniqueKey();
        mr::SingleInstanceGuard first(key);
        QSignalSpy activations(&first, &mr::SingleInstanceGuard::activationRequested);
        mr::SingleInstanceGuard second(key);
        QVERIFY(second.signalPrimary());
        QVERIFY(activations.wait(3000));
        QCOMPARE(activations.count(), 1);

        // And again for the next launch: the request is not used up.
        mr::SingleInstanceGuard third(key);
        QVERIFY(third.signalPrimary());
        QTRY_COMPARE_WITH_TIMEOUT(activations.count(), 2, 3000);
#endif
    }

    /// Once the first instance is gone - quit, or crashed - the next launch is
    /// the first again.
    void theNextLaunchIsTheFirstOnceTheFirstHasGone()
    {
#ifndef Q_OS_WIN
        QSKIP("The guard is a pair of Win32 kernel objects.");
#else
        const QString key = uniqueKey();
        {
            mr::SingleInstanceGuard first(key);
            QVERIFY(first.isPrimary());
        }
        mr::SingleInstanceGuard next(key);
        QVERIFY(next.isPrimary());
        // The first instance never signals anybody.
        QVERIFY(!next.signalPrimary());
#endif
    }

    /// Per user, like the pipe: another account's Desktop is not this one.
    void theProductionKeyIsTheUsersOwn()
    {
        const QString pipe = mr::ipc::currentUserPipeName();
        if (pipe.isEmpty())
            QSKIP("The user's SID is not readable here.");
        QCOMPARE(mr::SingleInstanceGuard::defaultKey(), pipe + QStringLiteral(".desktop"));
        // Without a key there is no guard, and nothing is ever refused.
        mr::SingleInstanceGuard unguarded{QString()};
        mr::SingleInstanceGuard alsoUnguarded{QString()};
        QVERIFY(unguarded.isPrimary());
        QVERIFY(alsoUnguarded.isPrimary());
    }
};

int main(int argc, char *argv[])
{
    // Never run against the user's own Collector, database or serve lease - see
    // TestCollectorGuard.h. Must precede any construction.
    mrtest::disableCollectorLaunch();
    QCoreApplication app(argc, argv);
    SingleInstanceTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "SingleInstanceTests.moc"
