// 新区服采证 (evidence/EvidencePage.qml): the five steps follow the Collector's
// $defs/CaptureValidationStatus, a later marker records the earlier ones as 漏按,
// the 回传说明 is composed from the round, and 开始下一轮 returns to step 1.
#include "DesktopTestRunner.h"
#include "AppController.h"
#include "EvidenceSubmissionController.h"
#include "Formatters.h"
#include "MockBackend.h"
#include "QmlComponentTestSupport.h"
#include "TestCollectorGuard.h"

#include <QGuiApplication>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QSignalSpy>
#include <QStandardPaths>
#include <QTest>

#include <memory>

namespace {

QQuickItem *findItem(QQuickItem *root, const QString &name)
{
    if (!root) return nullptr;
    if (root->objectName() == name) return root;
    for (auto *child : root->childItems())
        if (auto *found = findItem(child, name)) return found;
    return nullptr;
}

struct EvidenceScene
{
    mr::MockBackend backend;
    std::unique_ptr<mr::AppController> app;
    mr::Formatters formatters;
    std::unique_ptr<mr::EvidenceSubmissionController> evidence;
    std::unique_ptr<QQmlEngine> engine;
    std::unique_ptr<QSignalSpy> warnings;
    std::unique_ptr<QObject> root;
    QQuickWindow *window = nullptr;
    QQuickItem *page = nullptr;

    ~EvidenceScene()
    {
        root.reset();
        engine.reset();
        evidence.reset();
        app.reset();
    }

    bool open(const QString &validationState)
    {
        backend.setValidationFixture(validationState);
        app = std::make_unique<mr::AppController>(&backend, nullptr);
        app->recording()->setUserObservation(true);
        evidence = std::make_unique<mr::EvidenceSubmissionController>(&backend);
        engine = std::make_unique<QQmlEngine>();
        engine->rootContext()->setContextProperty(QStringLiteral("App"), app.get());
        engine->rootContext()->setContextProperty(QStringLiteral("Fmt"), &formatters);
        engine->rootContext()->setContextProperty(QStringLiteral("CalibrationEvidence"), evidence.get());
        engine->rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
        warnings = std::make_unique<QSignalSpy>(engine.get(), &QQmlEngine::warnings);
        QQmlComponent component(engine.get());
        component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 1180; height: 900; visible: true; color: Theme.contentBackground
    ScrollView { anchors.fill: parent
        contentWidth: availableWidth
        EvidencePage { objectName: "evidencePage"; width: parent.width }
    }
})", QUrl());
        root.reset(component.create());
        if (!root) {
            qWarning("%s", qPrintable(component.errorString()));
            return false;
        }
        window = qobject_cast<QQuickWindow *>(root.get());
        page = window ? findItem(window->contentItem(), QStringLiteral("evidencePage")) : nullptr;
        return page != nullptr;
    }

    QQuickItem *item(const QString &name) const { return findItem(page, name); }
    QString text(const QString &name) const
    {
        auto *found = item(name);
        return found ? found->property("text").toString() : QString();
    }
    QString stepStatus(int number) const
    {
        auto *step = item(QStringLiteral("evidenceStep_%1").arg(number));
        return step ? step->property("status").toString() : QString();
    }
    bool stepOpen(int number) const
    {
        auto *step = item(QStringLiteral("evidenceStep_%1").arg(number));
        return step && step->property("open").toBool();
    }
    QObject *session() const { return page ? page->property("session").value<QObject *>() : nullptr; }
    bool click(const QString &name) const
    {
        auto *button = item(name);
        return button && button->isVisible() && button->isEnabled()
               && QMetaObject::invokeMethod(button, "clicked");
    }
};

class EvidencePageTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void initTestCase()
    {
        // The page is built from the shared controls; the evidence folder itself is
        // registered by QmlRuntimeTestRegistration.cpp like every feature folder.
        mrtest::registerSourceQmlComponents();
    }

    void stepsFollowTheCollectorsState_data()
    {
        QTest::addColumn<QString>("state");
        QTest::addColumn<int>("step");
        QTest::addColumn<QString>("badge");
        QTest::newRow("idle") << QStringLiteral("IDLE") << 1 << QString::fromUtf8("尚未开始");
        QTest::newRow("failed") << QStringLiteral("FAILED") << 1 << QString::fromUtf8("失败");
        QTest::newRow("waiting") << QStringLiteral("WAITING") << 2 << QString::fromUtf8("等待游戏启动");
        QTest::newRow("recording") << QStringLiteral("RECORDING") << 3 << QString::fromUtf8("正在取证");
        QTest::newRow("stopping") << QStringLiteral("STOPPING") << 4 << QString::fromUtf8("正在保存");
        QTest::newRow("completed") << QStringLiteral("COMPLETED") << 4 << QString::fromUtf8("已保存");
        // 取消等待: STOPPING with nothing counted stays at step 2, then the Collector
        // reports COMPLETED with reason CANCELLED and no trace.
        QTest::newRow("cancelling") << QStringLiteral("CANCELLING") << 2 << QString::fromUtf8("正在取消等待");
        QTest::newRow("cancelled") << QStringLiteral("CANCELLED") << 1 << QString::fromUtf8("尚未开始");
    }

    void stepsFollowTheCollectorsState()
    {
        QFETCH(QString, state);
        QFETCH(int, step);
        QFETCH(QString, badge);
        EvidenceScene scene;
        QVERIFY(scene.open(state));
        const QString reported = state == QStringLiteral("CANCELLED") ? QStringLiteral("COMPLETED")
                               : state == QStringLiteral("CANCELLING") ? QStringLiteral("STOPPING") : state;
        QTRY_COMPARE(scene.app->validationState(), reported);
        QTRY_COMPARE(scene.stepStatus(step), QStringLiteral("cur"));
        for (int number = 1; number <= 5; ++number) {
            QCOMPARE(scene.stepStatus(number), number < step ? QStringLiteral("done")
                                               : number == step ? QStringLiteral("cur") : QStringLiteral("todo"));
            // Only the current step is open until a done one is reopened.
            QCOMPARE(scene.stepOpen(number), number == step);
        }
        QCOMPARE(scene.text(QStringLiteral("evidenceStatusBadgeText")), badge);
        QCOMPARE(scene.text(QStringLiteral("evidenceStatusHeadline")), badge);
        // The one action button of the round: 开始采证 before, 取消等待 / 停止采证 while it
        // runs; while the Collector saves, and once it has, there is nothing to press.
        auto *action = scene.item(QStringLiteral("templateObservationActionButton"));
        if (state == QStringLiteral("STOPPING") || state == QStringLiteral("CANCELLING")
            || state == QStringLiteral("COMPLETED")) {
            QVERIFY(!action);
        } else {
            QVERIFY(action);
            QVERIFY(action->isVisible());
        }
        QCOMPARE(scene.warnings->size(), 0);
    }

    void cancellingTheWaitReturnsToStepOne()
    {
        EvidenceScene scene;
        QVERIFY(scene.open(QStringLiteral("WAITING")));
        QTRY_COMPARE(scene.stepStatus(2), QStringLiteral("cur"));
        QVERIFY(scene.click(QStringLiteral("templateObservationActionButton")));
        // Nothing was recorded: step 1 again, with the Collector's sentence and no
        // round to show in the sidebar - not 已结束，文件不完整 at step 4.
        QTRY_COMPARE(scene.stepStatus(1), QStringLiteral("cur"));
        QCOMPARE(scene.app->validationState(), QStringLiteral("COMPLETED"));
        for (int number = 2; number <= 5; ++number)
            QCOMPARE(scene.stepStatus(number), QStringLiteral("todo"));
        QCOMPARE(scene.text(QStringLiteral("evidenceStatusBadgeText")), QString::fromUtf8("尚未开始"));
        auto *cancelled = scene.item(QStringLiteral("evidenceCancelledText"));
        QVERIFY(cancelled && cancelled->isVisible());
        QCOMPARE(cancelled->property("text").toString(), QString::fromUtf8("已取消等待，未创建取证文件。"));
        QCOMPARE(scene.text(QStringLiteral("evidenceCounter_message_count")), mr::Formatters::dash());
        QVERIFY(scene.item(QStringLiteral("templateObservationActionButton")));
        QCOMPARE(scene.warnings->size(), 0);
    }

    void aDoneStepReopensFromItsHeaderAndKeepsItsSummary()
    {
        EvidenceScene scene;
        QVERIFY(scene.open(QStringLiteral("RECORDING")));
        QTRY_COMPARE(scene.stepStatus(3), QStringLiteral("cur"));
        QVERIFY(!scene.stepOpen(1));
        auto *step1 = scene.item(QStringLiteral("evidenceStep_1"));
        QVERIFY(step1);
        QVERIFY(QMetaObject::invokeMethod(step1, "toggleRequested"));
        QTRY_VERIFY(scene.stepOpen(1));
        QVERIFY(scene.stepOpen(3));
        const QString summary = scene.text(QStringLiteral("evidenceStepSummary_1"));
        QVERIFY2(summary.contains(QString::fromUtf8("开始于")), qPrintable(summary));
        // A step still to come stays locked.
        auto *step5 = scene.item(QStringLiteral("evidenceStep_5"));
        QVERIFY(step5);
        QVERIFY(QMetaObject::invokeMethod(step5, "toggleRequested"));
        QVERIFY(!scene.stepOpen(5));
        QCOMPARE(scene.warnings->size(), 0);
    }

    void aLaterMarkerRecordsTheEarlierOnesAsSkipped()
    {
        EvidenceScene scene;
        QVERIFY(scene.open(QStringLiteral("RECORDING")));
        QTRY_VERIFY(scene.app->validationMarkerEnabled());
        QTRY_COMPARE(scene.stepStatus(3), QStringLiteral("cur"));
        const QString markersBefore = scene.text(QStringLiteral("evidenceCounter_marker_count"));
        QVERIFY(scene.click(QStringLiteral("templateObservationMarker_entered")));
        auto *session = scene.session();
        QVERIFY(session);
        QTRY_COMPARE(session->property("markedCount").toInt(), 1);
        const QVariantList marks = session->property("marks").toList();
        QCOMPARE(marks.size(), 5);
        QCOMPARE(marks.at(0).toString(), QStringLiteral("skip"));
        QCOMPARE(marks.at(1).toString(), QStringLiteral("skip"));
        QVERIFY(!marks.at(2).toMap().value(QStringLiteral("at")).toString().isEmpty());
        QVERIFY(marks.at(3).isNull());
        QVERIFY(session->property("chips").toMap().value(QStringLiteral("miss")).toBool());
        // Skipped and done markers are spent; the next one is the only one left to press.
        QVERIFY(!scene.item(QStringLiteral("templateObservationMarker_queued"))->isEnabled());
        QVERIFY(!scene.item(QStringLiteral("templateObservationMarker_entered"))->isEnabled());
        QTRY_VERIFY(scene.item(QStringLiteral("templateObservationMarker_victory"))->isEnabled());
        // The Collector received exactly the marker that was pressed.
        QTRY_VERIFY(scene.text(QStringLiteral("evidenceCounter_marker_count")) != markersBefore);
        QTRY_VERIFY(!scene.app->validationFeedback().isEmpty());
        QCOMPARE(scene.warnings->size(), 0);
    }

    void theReportIsComposedFromTheRound()
    {
        EvidenceScene scene;
        QVERIFY(scene.open(QStringLiteral("COMPLETED")));
        QTRY_COMPARE(scene.stepStatus(4), QStringLiteral("cur"));
        QVERIFY(scene.text(QStringLiteral("evidenceSavedText")).startsWith(QString::fromUtf8("已保存")));
        // Every required file is still to be copied.
        QVERIFY(scene.item(QStringLiteral("evidenceToReportButton"))->isEnabled());
        QVERIFY(scene.click(QStringLiteral("evidenceToReportButton")));
        QTRY_COMPARE(scene.stepStatus(5), QStringLiteral("cur"));
        QCOMPARE(scene.stepStatus(4), QStringLiteral("done"));
        QString report = scene.text(QStringLiteral("evidenceReportText"));
        QVERIFY2(report.startsWith(QString::fromUtf8("【导随记录器 · 新区服采证回报】")), qPrintable(report));
        QVERIFY2(report.contains(QString::fromUtf8("软件版本：v") + scene.app->appVersion()), qPrintable(report));
        QVERIFY2(report.contains(QString::fromUtf8("计数：报文 ") + mr::Formatters::count(1842)), qPrintable(report));
        QVERIFY2(report.contains(QString::fromUtf8("随机类型：指导者随机")), qPrintable(report));
        QVERIFY2(report.contains(QString::fromUtf8("缺失：状态截图、trace.jsonl、trace.jsonl.sha256、诊断报告 JSON")),
                 qPrintable(report));
        QVERIFY2(!report.contains(QStringLiteral("mock-only")), qPrintable(report));

        // Ticking a file moves it from 缺失 to 附件.
        auto *session = scene.session();
        QVERIFY(session);
        QVERIFY(QMetaObject::invokeMethod(session, "toggleFile", Q_ARG(QVariant, QStringLiteral("trace"))));
        QTRY_VERIFY(scene.text(QStringLiteral("evidenceReportText")).contains(QString::fromUtf8("附件：trace.jsonl")));
        report = scene.text(QStringLiteral("evidenceReportText"));
        QVERIFY2(report.contains(QString::fromUtf8("缺失：状态截图、trace.jsonl.sha256、诊断报告 JSON")), qPrintable(report));
        QCOMPARE(scene.text(QStringLiteral("evidenceStepSummary_4")), QString::fromUtf8("已保存 · 文件 1 / 4"));

        // 开始下一轮: the Collector still reports the finished round, the page is at step 1.
        QVERIFY(scene.click(QStringLiteral("evidenceNextRoundButton")));
        QTRY_COMPARE(scene.stepStatus(1), QStringLiteral("cur"));
        QCOMPARE(scene.text(QStringLiteral("evidenceStatusBadgeText")), QString::fromUtf8("尚未开始"));
        QCOMPARE(session->property("files").toMap().size(), 0);
        QCOMPARE(scene.warnings->size(), 0);
    }

    void theBackLinkAsksTheCapturePageToReturn()
    {
        EvidenceScene scene;
        QVERIFY(scene.open(QStringLiteral("IDLE")));
        QSignalSpy back(scene.page, SIGNAL(backRequested()));
        QVERIFY(back.isValid());
        QVERIFY(scene.click(QStringLiteral("evidenceBackButton")));
        QCOMPARE(back.count(), 1);
        QCOMPARE(scene.warnings->size(), 0);
    }
};

} // namespace

MR_DESKTOP_TEST_SUITE(EvidencePage)
{
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication app(argc, argv);
    EvidencePageTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "EvidencePageTests.moc"
