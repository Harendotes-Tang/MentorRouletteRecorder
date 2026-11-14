#include "DesktopTestRunner.h"
#include "TestCollectorGuard.h"
#include "AppController.h"
#include "IBackend.h"
#include "TtsService.h"
#include <QGuiApplication>
#include <QHash>
#include <QTest>

namespace {
class EventBackend final : public mr::IBackend
{
public:
    QHash<QString, int> calls;
    bool connected = true;
    QString backendName() const override { return QStringLiteral("mock"); }
    bool isConnected() const override { return connected; }
    mr::BackendReply *request(const QString &type, const QJsonObject & = {}) override
    {
        ++calls[type];
        // Pending replies isolate event routing from asynchronous reply effects.
        return new mr::BackendReply(QString::number(calls[type]), type, this);
    }
    void send(qint64 sequence, const QString &id = {})
    {
        Q_EMIT liveEvent({{"kind", "heartbeat"}, {"event_type", "Heartbeat"},
                         {"sequence", sequence}, {"event_id", id.isEmpty() ? QString::number(sequence) : id}});
    }
    void connection(bool value)
    {
        connected = value;
        Q_EMIT connectionChanged();
    }
};

class AppLiveEventTests final : public QObject
{
    Q_OBJECT
private Q_SLOTS:
    void contiguousHeartbeatsDoNotRefresh()
    {
        EventBackend backend;
        mr::AppController app(&backend, nullptr, nullptr, nullptr, mr::TtsService::EngineMode::None);
        const int before = backend.calls.value("GetDashboardStats");
        backend.send(20); // A late subscriber starts at an arbitrary sequence.
        backend.send(21);
        backend.send(22);
        QCoreApplication::processEvents();
        QCOMPARE(backend.calls.value("GetDashboardStats"), before);
    }

    void heartbeatGapResynchronizesLostStateAndCoalescesBursts()
    {
        EventBackend backend;
        mr::AppController app(&backend, nullptr, nullptr, nullptr, mr::TtsService::EngineMode::None);
        const auto before = backend.calls;
        backend.send(1);
        backend.send(4);
        backend.send(9);
        QCOMPARE(backend.calls.value("GetDashboardStats"), before.value("GetDashboardStats"));
        QTRY_COMPARE(backend.calls.value("GetDashboardStats"), before.value("GetDashboardStats") + 1);
        QVERIFY(backend.calls.value("GetCurrentRun") > before.value("GetCurrentRun"));
        QVERIFY(backend.calls.value("QueryRuns") > before.value("QueryRuns"));
        QVERIFY(backend.calls.value("GetDungeonStats") > before.value("GetDungeonStats"));
        QVERIFY(backend.calls.value("GetJobStats") > before.value("GetJobStats"));
        backend.send(12);
        QTRY_COMPARE(backend.calls.value("GetDashboardStats"), before.value("GetDashboardStats") + 2);
    }

    void regularEventGapAlsoResynchronizes()
    {
        EventBackend backend;
        mr::AppController app(&backend, nullptr, nullptr, nullptr, mr::TtsService::EngineMode::None);
        const int before = backend.calls.value("GetDashboardStats");
        backend.send(10);
        Q_EMIT backend.liveEvent({{"kind", "calibration_changed"}, {"event_id", "calibration"}, {"sequence", 13}});
        QTRY_COMPARE(backend.calls.value("GetDashboardStats"), before + 1);
    }

    void replayDoesNotRewindWatermarkOrCauseFalseGap()
    {
        EventBackend backend;
        mr::AppController app(&backend, nullptr, nullptr, nullptr, mr::TtsService::EngineMode::None);
        const int before = backend.calls.value("GetDashboardStats");
        const QVariantMap event{{"kind", "calibration_changed"}, {"event_id", "replayed"}, {"sequence", 10}};
        Q_EMIT backend.liveEvent(event);
        backend.send(11);
        backend.send(12);
        Q_EMIT backend.liveEvent(event);
        backend.send(13);
        QCoreApplication::processEvents();
        QCOMPARE(backend.calls.value("GetDashboardStats"), before);
    }

    void idleHeartbeatsDoNotEvictReplayableEventIdentities()
    {
        EventBackend backend;
        mr::AppController app(&backend, nullptr, nullptr, nullptr, mr::TtsService::EngineMode::None);
        const QVariantMap event{{"kind", "calibration_changed"}, {"event_id", "retained"}, {"sequence", 1}};
        Q_EMIT backend.liveEvent(event);
        for (int sequence = 2; sequence <= 300; ++sequence)
            backend.send(sequence);
        QCoreApplication::processEvents();
        const auto before = backend.calls;
        Q_EMIT backend.liveEvent(event);
        backend.send(301);
        QCoreApplication::processEvents();
        QCOMPARE(backend.calls, before);
    }

    void restartedCollectorStartsANewSequence()
    {
        EventBackend backend;
        mr::AppController app(&backend, nullptr, nullptr, nullptr, mr::TtsService::EngineMode::None);
        const int before = backend.calls.value("GetDashboardStats");
        backend.send(100, "old-process");
        backend.send(1, "new-process-1");
        backend.send(2, "new-process-2");
        QCoreApplication::processEvents();
        QCOMPARE(backend.calls.value("GetDashboardStats"), before);
        backend.send(4, "new-process-4");
        QTRY_COMPARE(backend.calls.value("GetDashboardStats"), before + 1);
    }

    void reconnectSupersedesQueuedResynchronization()
    {
        EventBackend backend;
        mr::AppController app(&backend, nullptr, nullptr, nullptr, mr::TtsService::EngineMode::None);
        backend.send(1);
        backend.send(4);
        backend.connection(false);
        backend.connection(true);
        const int afterReconnect = backend.calls.value("GetDashboardStats");
        QCoreApplication::processEvents();
        QCOMPARE(backend.calls.value("GetDashboardStats"), afterReconnect);
    }
};
} // namespace

MR_DESKTOP_TEST_SUITE(AppLiveEvent)
{
    mrtest::disableCollectorLaunch();
    QGuiApplication app(argc, argv);
    AppLiveEventTests tests;
    return QTest::qExec(&tests, argc, argv);
}
#include "AppLiveEventTests.moc"
