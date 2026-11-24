// CaptureValidationController (cpp/CaptureValidationController.h): which command the 捕获诊断
// action button and the 新区服采证 metadata button send, and what the controller keeps across
// a failed start, a reconnect, a draining round and a profile query that keeps failing. The
// replies are given by hand; the application side is a stub host.
#include "DesktopTestRunner.h"
#include "RecordingBackend.h"
#include "TestCollectorGuard.h"
#include "AutomaticRecordingController.h"
#include "CaptureValidationController.h"

#include <QGuiApplication>
#include <QJsonObject>
#include <QSignalSpy>
#include <QStandardPaths>
#include <QTest>

#include <algorithm>

namespace {

using mrtest::RecordingBackend;

/// What the controller asks the application: whether a formal capture runs (it does once a
/// start or stop reply says RUNNING), whether candidate validation is on, the profile status.
class CaptureHost final : public mr::CaptureValidationController::Host
{
public:
    bool formal = false;
    bool candidate = true;
    QString profile = QStringLiteral("UNSUPPORTED_BUILD");
    void setFormalCapturing(bool running) { formal = running; }
    void setCandidateEnabled(bool enabled) { candidate = enabled; }
    bool formalCapturing() const override { return formal; }
    bool candidateValidationEnabled() const override { return candidate; }
    quint64 formalCaptureRevision() const override { return 0; }
    QString captureProfileStatus() const override { return profile; }
    void applyFormalCaptureStatus(const QVariantMap &status) override
    {
        formal = status.value(QStringLiteral("state")).toString() == QLatin1String("RUNNING");
    }
    void refreshStatus() override {}
    void showToast(const QString &) override {}
};

qsizetype count(const RecordingBackend &backend, const char *type)
{
    return std::count_if(backend.calls.begin(), backend.calls.end(),
                         [type](const RecordingBackend::Call &call) { return call.type == QLatin1String(type); });
}

class CaptureValidationControllerTests : public QObject
{
    Q_OBJECT
private Q_SLOTS:
    // -- the capture page's action button ---------------------------------

    void candidateButtonStartsPassiveLedgerAndNeverFallsBackToLegacyTrace()
    {
        RecordingBackend backend;
        CaptureHost host;
        mr::CaptureValidationController controller(&host);
        controller.setBackend(&backend);
        controller.refreshProfile();
        backend.calls.last().reply->succeed({{QStringLiteral("status"), QStringLiteral("UNSUPPORTED_BUILD")}});
        controller.refreshStatusSnapshot();
        backend.calls.last().reply->succeed({{QStringLiteral("state"), QStringLiteral("IDLE")}});
        QVERIFY(controller.actionEnabled());
        QCOMPARE(controller.actionLabel(), QStringLiteral("开始候选捕获"));
        controller.toggle();
        QCOMPARE(backend.calls.last().type, QStringLiteral("StartCapture"));
        host.setCandidateEnabled(false); // A changed setting cannot change the in-flight command's meaning.
        backend.calls.last().reply->fail(QStringLiteral("ERR_PROFILE_UNSUPPORTED"), QStringLiteral("候选启动失败"));
        QCOMPARE(count(backend, "StartCaptureValidation"), 0);
        QCOMPARE(controller.error(), QStringLiteral("候选启动失败"));
        QVERIFY(!controller.commandBusy());
        host.setCandidateEnabled(true);
        controller.toggle();
        backend.calls.last().reply->succeed({{QStringLiteral("state"), QStringLiteral("RUNNING")}});
        QCOMPARE(controller.actionLabel(), QStringLiteral("停止捕获"));
        QCOMPARE(controller.modeStatusText(), QStringLiteral("候选观测中；VERIFIED 解析可独立生成正式记录"));
        QCOMPARE(controller.modeCompactText(), QStringLiteral("候选观测中"));
        host.setCandidateEnabled(false);
        QCOMPARE(controller.modeStatusText(), QStringLiteral("被动监听（不自动记录）"));
        QCOMPARE(controller.modeCompactText(), QStringLiteral("被动监听中"));
        controller.toggle();
        QCOMPARE(backend.calls.last().type, QStringLiteral("StopCapture"));
    }

    // 审查 OI-7：协议档案查询一直失败、验证状态却一直有回应时，退避也必须用尽。
    // 状态的回应不能把档案查询的失败次数清零，否则会以基础间隔永远轮询下去。
    void aFailingProfileQueryStopsRetryingWhileTheStatusAnswers()
    {
        RecordingBackend backend;
        CaptureHost host;
        host.setCandidateEnabled(false);
        mr::CaptureValidationController controller(&host);
        controller.setBackend(&backend);
        // The poll timer's own order: profile first, then status.
        for (int round = 0; round < 8; ++round) {
            controller.refreshProfile();
            const auto profile = backend.calls.last();
            QCOMPARE(profile.type, QStringLiteral("GetProtocolProfileStatus"));
            controller.refreshStatusSnapshot();
            const auto status = backend.calls.last();
            QCOMPARE(status.type, QStringLiteral("GetCaptureValidationStatus"));
            profile.reply->fail(QStringLiteral("ERR_INTERNAL"), QStringLiteral("timeout"));
            status.reply->succeed({{QStringLiteral("state"), QStringLiteral("IDLE")}});
        }
        QCOMPARE(controller.actionLabel(), QStringLiteral("状态未确认"));

        // One answer from the profile query is a recovery.
        controller.refreshProfile();
        backend.calls.last().reply->succeed({{QStringLiteral("status"), QStringLiteral("VERIFIED")}});
        QVERIFY(controller.actionLabel() != QStringLiteral("状态未确认"));
    }

    void activeLegacyTraceRetainsItsStopPrecedence()
    {
        RecordingBackend backend;
        CaptureHost host;
        host.setFormalCapturing(true);
        mr::CaptureValidationController controller(&host);
        controller.setBackend(&backend);
        controller.refreshStatusSnapshot();
        backend.calls.last().reply->succeed({{QStringLiteral("state"), QStringLiteral("RECORDING")}});
        controller.toggle();
        QCOMPARE(backend.calls.last().type, QStringLiteral("StopCaptureValidation"));
        QCOMPARE(count(backend, "StopCapture"), 0);
    }

    // -- the 新区服采证 metadata button -------------------------------------

    void metadataButtonNeverStartsFormalCapture()
    {
        RecordingBackend backend;
        CaptureHost host;
        mr::CaptureValidationController capture(&host);
        capture.setBackend(&backend);
        capture.refreshStatusSnapshot();
        backend.calls.last().reply->succeed({{"state", "IDLE"}, {"active", false}});
        QVERIFY(capture.metadataActionEnabled());
        capture.toggleMetadataObservation();
        QCOMPARE(backend.calls.last().type, QStringLiteral("StartCaptureValidation"));
        backend.calls.last().reply->succeed({{"state", "RECORDING"}, {"active", true}});
        capture.toggleMetadataObservation();
        QCOMPARE(backend.calls.last().type, QStringLiteral("StopCaptureValidation"));
        backend.calls.last().reply->succeed({{"state", "COMPLETED"}, {"active", false}});
        host.setFormalCapturing(true);
        QVERIFY(capture.metadataActionEnabled());
        capture.toggleMetadataObservation();
        QCOMPARE(backend.calls.last().type, QStringLiteral("StartCaptureValidation"));
        QCOMPARE(backend.calls.last().payload.value("switch_from_unsupported"), QJsonValue(true));
        backend.calls.last().reply->succeed({{"state", "COMPLETED"}, {"active", false}});
        host.setFormalCapturing(true);
        host.profile = QStringLiteral("VERIFIED");
        QVERIFY(!capture.metadataActionEnabled());
    }

    void failedStartQueuesFreshStatusBehindOldGeneration()
    {
        RecordingBackend backend;
        CaptureHost host;
        mr::CaptureValidationController capture(&host);
        capture.setBackend(&backend);
        QSignalSpy ownership(&capture, &mr::CaptureValidationController::metadataOwnershipChanged);
        capture.refreshStatusSnapshot();
        backend.calls.last().reply->succeed({{"state", "IDLE"}, {"active", false}});
        capture.refreshStatusSnapshot();
        auto old = backend.calls.last().reply;
        capture.toggleMetadataObservation();
        QCOMPARE(backend.calls.last().type, QStringLiteral("StartCaptureValidation"));
        backend.calls.last().reply->fail("ERR_TEST", "start failed");
        QCOMPARE(ownership.size(), 1);
        QCOMPARE(ownership.last().at(0).toBool(), true);
        old->succeed({{"state", "IDLE"}, {"active", false}});
        QTRY_COMPARE(backend.calls.last().type, QStringLiteral("GetCaptureValidationStatus"));
        backend.calls.last().reply->succeed({{"state", "IDLE"}, {"active", false}});
        QCOMPARE(ownership.size(), 2);
        QCOMPARE(ownership.last().at(0).toBool(), false);
        QVERIFY(capture.metadataActionEnabled());
    }

    void explicitMetadataOwnershipSurvivesReconnectAndDraining()
    {
        RecordingBackend backend;
        CaptureHost host;
        mr::AutomaticRecordingController recording(&backend);
        mr::CaptureValidationController capture(&host);
        capture.setBackend(&backend);
        connect(&capture, &mr::CaptureValidationController::metadataOwnershipChanged,
                &recording, &mr::AutomaticRecordingController::setUserObservation);
        capture.refreshStatusSnapshot();
        backend.calls.last().reply->succeed({{"state", "IDLE"}, {"active", false}});
        capture.toggleMetadataObservation();
        backend.calls.last().reply->succeed({{"state", "RECORDING"}, {"active", true}});
        const qsizetype activeCalls = backend.calls.size();
        recording.refresh();
        recording.retry();
        QCOMPARE(backend.calls.size(), activeCalls);
        QCOMPARE(recording.state(), QStringLiteral("observing"));
        backend.setConnected(false);
        capture.reset();
        backend.setConnected(true);
        recording.refresh();
        QCOMPARE(backend.calls.size(), activeCalls);
        capture.refreshStatusSnapshot();
        backend.calls.last().reply->succeed({{"state", "RECORDING"}, {"active", true}});
        capture.toggleMetadataObservation();
        QCOMPARE(backend.calls.last().type, QStringLiteral("StopCaptureValidation"));
        backend.calls.last().reply->succeed({{"state", "STOPPING"}, {"active", true}});
        const qsizetype drainingCalls = backend.calls.size();
        recording.refresh();
        QCOMPARE(backend.calls.size(), drainingCalls);
        capture.refreshStatusSnapshot();
        backend.calls.last().reply->succeed({{"state", "COMPLETED"}, {"active", false}});
        QCOMPARE(backend.calls.last().type, QStringLiteral("GetCaptureSettings"));
        QCOMPARE(count(backend, "StopCaptureValidation"), 1); // 唯一的停止来自玩家按钮，自动记录没有争抢。
    }
};
} // namespace

MR_DESKTOP_TEST_SUITE(CaptureValidationController)
{
    // Never run against the user's own Collector, database or serve lease - see
    // TestCollectorGuard.h. Must precede any construction.
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QGuiApplication app(argc, argv);
    CaptureValidationControllerTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "CaptureValidationControllerTests.moc"
