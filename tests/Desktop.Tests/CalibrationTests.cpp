// ---------------------------------------------------------------------------
// tst_calibration - 本机校准, desktop half.
//
// What it pins:
//   * the four projections a player reads out of $defs/CalibrationStatus, in
//     their own words - and that none of them uses maintainer vocabulary;
//   * the dialog's rule that every event needing a verdict has to have one
//     before 核对并启用 can be pressed;
//   * that an all-CORRECT confirmation sends exactly the payload shape of
//     tests/Fixtures/ipc-requests/ConfirmCalibration.json;
//   * that one WRONG shows the "再打一把" sentence instead of an error code;
//   * that a calibration_changed live event costs exactly one GetCaptureStatus
//     and never a run query, a statistics refresh or an announcement.
// ---------------------------------------------------------------------------

#include "TestCollectorGuard.h"
#include "AppController.h"
#include "AutomaticRecordingController.h"
#include "CalibrationController.h"
#include "IBackend.h"
#include "MockBackend.h"

#include <QDateTime>
#include <QDir>
#include <QFont>
#include <QFontDatabase>
#include <QGuiApplication>
#include <QImage>
#include <QJsonArray>
#include <QJsonObject>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QSignalSpy>
#include <QStandardPaths>
#include <QTest>
#include <QTimer>

#include <algorithm>
#include <memory>

namespace {

/// One synthetic timeline event.
QJsonObject event(const QString &kind, qint64 tMs, const QString &label, bool asks)
{
    return {{QStringLiteral("event_id"), kind + QLatin1Char('-') + QString::number(tMs)},
            {QStringLiteral("kind"), kind},
            {QStringLiteral("at_utc"), QStringLiteral("2026-09-10T12:00:00.000Z")},
            {QStringLiteral("t_ms"), double(tMs)},
            {QStringLiteral("label"), label},
            {QStringLiteral("requires_confirmation"), asks}};
}

QJsonObject readyTimeline(bool asks = true)
{
    return {{QStringLiteral("state"), asks ? QStringLiteral("READY") : QStringLiteral("DONE")},
            {QStringLiteral("game_build"), QStringLiteral("2026.09.01.0000.0000")},
            {QStringLiteral("blockers"), QJsonArray{}},
            {QStringLiteral("progress"),
             QJsonObject{{QStringLiteral("finder_request_seen"), true},
                         {QStringLiteral("pop_seen"), true},
                         {QStringLiteral("zone_clusters"), 3},
                         {QStringLiteral("duty_entry_seen"), true},
                         {QStringLiteral("duty_exit_seen"), true}}},
            {QStringLiteral("events"),
             QJsonArray{event(QStringLiteral("login"), 0, QString::fromUtf8("登录进入游戏"), false),
                        event(QStringLiteral("finder_request"), 60000,
                              QString::fromUtf8("排本：练级迷宫"), asks),
                        event(QStringLiteral("pop"), 120000,
                              QString::fromUtf8("匹配弹窗：练级迷宫"), asks),
                        event(QStringLiteral("duty_enter"), 124000,
                              QString::fromUtf8("进入副本：沙斯塔夏溶洞"), asks),
                        event(QStringLiteral("duty_exit"), 214000,
                              QString::fromUtf8("离开副本"), asks)}}};
}

QJsonObject observingCalibration()
{
    return {{QStringLiteral("state"), QStringLiteral("OBSERVING")},
            {QStringLiteral("game_build"), QStringLiteral("2026.09.01.0000.0000")},
            {QStringLiteral("blockers"), QJsonArray{}},
            {QStringLiteral("progress"),
             QJsonObject{{QStringLiteral("finder_request_seen"), true},
                         {QStringLiteral("pop_seen"), false},
                         {QStringLiteral("zone_clusters"), 1},
                         {QStringLiteral("duty_entry_seen"), false},
                         {QStringLiteral("duty_exit_seen"), false}}},
            {QStringLiteral("events"), QJsonArray{}}};
}

/// A backend that answers every message synchronously and records the order.
class CalibrationBackend final : public mr::IBackend
{
public:
    QStringList calls;
    QList<QJsonObject> payloads;
    QJsonObject capture{{QStringLiteral("ffxiv_running"), true},
                        {QStringLiteral("ffxiv_process_id"), 4242},
                        {QStringLiteral("profile_status"), QStringLiteral("UNSUPPORTED_BUILD")},
                        {QStringLiteral("state"), QStringLiteral("RUNNING")},
                        {QStringLiteral("npcap_installed"), true},
                        {QStringLiteral("silent_reason"), QStringLiteral("NONE")}};
    QString confirmErrorCode;
    QJsonObject confirmResult{{QStringLiteral("profile_id"), QStringLiteral("cn.2026.09.01.local")},
                              {QStringLiteral("profile_path"), QStringLiteral("D:/x.json")},
                              {QStringLiteral("bound_in_session"), true}};

    QString backendName() const override { return QStringLiteral("mock"); }
    bool isConnected() const override { return true; }

    mr::BackendReply *request(const QString &type, const QJsonObject &payload = {}) override
    {
        calls.append(type);
        payloads.append(payload);
        auto *reply = new mr::BackendReply(QString::number(calls.size()), type, this);
        if (type == QLatin1String("GetStatus")) {
            reply->succeed({{QStringLiteral("capture"), capture}});
        } else if (type == QLatin1String("GetCaptureStatus")) {
            reply->succeed(capture);
        } else if (type == QLatin1String("GetCaptureSettings")) {
            reply->succeed({{QStringLiteral("follow_game"), true},
                            {QStringLiteral("auto_calibration_enabled"), true}});
        } else if (type == QLatin1String("GetCaptureValidationStatus")) {
            reply->succeed({{QStringLiteral("active"), false}, {QStringLiteral("state"), QStringLiteral("IDLE")}});
        } else if (type == QLatin1String("ConfirmCalibration")) {
            if (confirmErrorCode.isEmpty())
                reply->succeed(confirmResult);
            else
                reply->fail(confirmErrorCode, QStringLiteral("draft voided"));
        } else if (type == QLatin1String("DiscardCalibration")) {
            reply->succeed({{QStringLiteral("state"), QStringLiteral("OBSERVING")}});
        } else {
            reply->succeed({});
        }
        return reply;
    }

    int count(const char *type) const
    {
        return int(std::count(calls.begin(), calls.end(), QLatin1String(type)));
    }
    void emitCalibrationChanged()
    {
        Q_EMIT liveEvent({{QStringLiteral("kind"), QStringLiteral("calibration_changed")},
                          {QStringLiteral("event_type"), QStringLiteral("CalibrationChanged")},
                          {QStringLiteral("event_id"), QStringLiteral("calib-1")},
                          {QStringLiteral("sequence"), 1},
                          {QStringLiteral("calibration_state"), QStringLiteral("READY")},
                          {QStringLiteral("ready"), true}});
    }
};

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

/// The words the screenshot guard in main.cpp forbids on a player page, plus
/// the raw contract tokens no player-facing string may leak.
const QStringList &maintainerVocabulary()
{
    static const QStringList words{
        QString::fromUtf8("开始捕获"), QString::fromUtf8("开始验证"),
        QString::fromUtf8("停止捕获"), QString::fromUtf8("维护者工具"),
        QString::fromUtf8("对照核对"), QStringLiteral("opcode"), QStringLiteral("0x"),
        QStringLiteral("UNSUPPORTED_BUILD"), QStringLiteral("VERIFIED"),
        QStringLiteral("OBSERVING"), QStringLiteral("BLOCKED"),
        QStringLiteral("ERR_")};
    return words;
}

void verifyPlayerCopy(const QString &text)
{
    QVERIFY2(!text.isEmpty(), "projection produced no sentence at all");
    for (const QString &word : maintainerVocabulary()) {
        QVERIFY2(!text.contains(word, Qt::CaseInsensitive),
                 qPrintable(QStringLiteral("player copy leaks \"") + word
                            + QStringLiteral("\": ") + text));
    }
}

} // namespace

class CalibrationTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void initTestCase()
    {
        const QString qmlRoot = QString::fromUtf8(MR_DESKTOP_QML_DIR);
        QVERIFY(QDir(qmlRoot).exists());
        qmlRegisterSingletonType(QUrl::fromLocalFile(qmlRoot + QStringLiteral("/Theme.qml")),
                                 "MentorRecorder", 1, 0, "Theme");
        for (const auto &file : QDir(qmlRoot + QStringLiteral("/components"))
                                    .entryList({QStringLiteral("*.qml")}, QDir::Files)) {
            const QByteArray name = file.chopped(4).toUtf8();
            qmlRegisterType(QUrl::fromLocalFile(qmlRoot + QStringLiteral("/components/") + file),
                            "MentorRecorder", 1, 0, name.constData());
        }
    }

    // -- projections --------------------------------------------------------

    void observingExplainsWhatToPlayAndShowsProgress()
    {
        CalibrationBackend backend;
        backend.capture.insert(QStringLiteral("calibration"), observingCalibration());
        mr::AutomaticRecordingController recording(&backend);
        QSignalSpy alerts(&recording, &mr::AutomaticRecordingController::incidentRaised);
        recording.refresh();

        QCOMPARE(recording.state(), QStringLiteral("calibrating"));
        QVERIFY(recording.attention());
        // Calibration is not an incident: no modal, nothing to acknowledge.
        QVERIFY(!recording.blocked());
        QVERIFY(!recording.pendingAlert());
        QCOMPARE(alerts.count(), 0);
        QVERIFY(recording.message().contains(QString::fromUtf8("正在重新校准")));
        QVERIFY(recording.message().contains(QString::fromUtf8("打一把随机任务")));
        QVERIFY(recording.message().contains(QString::fromUtf8("期间不会生成记录")));
        // Progress ticks belong to the calibration card, not to a sentence that is
        // also squeezed into the dashboard's fixed-height card.
        QVERIFY(!recording.message().contains(QString::fromUtf8("已看到")));
        verifyPlayerCopy(recording.message());
    }

    void readyAsksForTheNumberOfEventsThatNeedAVerdict()
    {
        CalibrationBackend backend;
        backend.capture.insert(QStringLiteral("calibration"), readyTimeline());
        mr::AutomaticRecordingController recording(&backend);
        recording.refresh();

        QCOMPARE(recording.state(), QStringLiteral("calibration_ready"));
        QVERIFY(recording.attention());
        QVERIFY(!recording.blocked());
        QCOMPARE(recording.message(),
                 QString::fromUtf8("校准完成，核对 4 件事就能开始自动记录。"));
        verifyPlayerCopy(recording.message());
    }

    void blockedRepeatsTheCollectorsOwnSentence()
    {
        const QString blocker = QString::fromUtf8(
            "这次更新改了报文的结构，本机校准做不了，需要维护者出一份新档案。");
        CalibrationBackend backend;
        QJsonObject calibration = readyTimeline();
        calibration.insert(QStringLiteral("state"), QStringLiteral("BLOCKED"));
        calibration.insert(QStringLiteral("blockers"), QJsonArray{blocker,
            QString::fromUtf8("打完这把副本，离开后再回来。")});
        backend.capture.insert(QStringLiteral("calibration"), calibration);
        mr::AutomaticRecordingController recording(&backend);
        recording.refresh();

        QCOMPARE(recording.state(), QStringLiteral("calibration_blocked"));
        QVERIFY(recording.message().startsWith(blocker));
        QVERIFY(recording.message().contains(QString::fromUtf8("查看诊断")));
        // Only the first blocker; the rest belong on the card.
        QVERIFY(!recording.message().contains(QString::fromUtf8("打完这把副本")));
        verifyPlayerCopy(recording.message());
    }

    void doneListensAndSaysTheProfileWasCalibratedHere()
    {
        CalibrationBackend backend;
        QJsonObject calibration = readyTimeline(false);
        backend.capture.insert(QStringLiteral("calibration"), calibration);
        backend.capture.insert(QStringLiteral("profile_status"), QStringLiteral("VERIFIED"));
        backend.capture.insert(QStringLiteral("profile_origin"), QStringLiteral("LOCAL_CALIBRATION"));
        mr::AutomaticRecordingController recording(&backend);
        recording.refresh();

        QCOMPARE(recording.state(), QStringLiteral("listening"));
        QVERIFY(!recording.attention());
        QVERIFY(recording.message().contains(QString::fromUtf8("自动监听中")));
        QVERIFY(recording.message().contains(QString::fromUtf8("本机校准")));
        verifyPlayerCopy(recording.message());
    }

    void anUnreadableInstallPathStillWinsOverCalibration()
    {
        CalibrationBackend backend;
        backend.capture.insert(QStringLiteral("calibration"), observingCalibration());
        backend.capture.insert(QStringLiteral("install_path_readable"), false);
        mr::AutomaticRecordingController recording(&backend);
        recording.refresh();

        QVERIFY(recording.blocked());
        QVERIFY(recording.message().contains(QString::fromUtf8("无法读取游戏安装路径")));
    }

    void aMissingNpcapStillWinsOverCalibration()
    {
        CalibrationBackend backend;
        backend.capture.insert(QStringLiteral("calibration"), observingCalibration());
        backend.capture.insert(QStringLiteral("npcap_installed"), false);
        mr::AutomaticRecordingController recording(&backend);
        recording.refresh();

        // Whatever the block says (the profile mismatch outranks Npcap in the
        // existing precedence), it must be a block, never the calibration copy.
        QVERIFY(recording.blocked());
        QVERIFY(!recording.calibrating());
        QVERIFY(!recording.message().contains(QString::fromUtf8("校准")));
    }

    void theRecordingPollHandsEveryCaptureSnapshotToTheCard()
    {
        // A progress tick does not change calibration.state, and nothing else refreshes
        // the capture status between events, so the two-second poll must feed the card
        // itself; otherwise a roulette that was seen still shows as a dash.
        CalibrationBackend backend;
        backend.capture.insert(QStringLiteral("calibration"), observingCalibration());
        mr::AutomaticRecordingController recording(&backend);
        mr::CalibrationController calibration;
        calibration.setBackend(&backend);
        QObject::connect(&recording, &mr::AutomaticRecordingController::captureObserved,
                         &calibration, [&calibration](const QVariantMap &capture) {
            calibration.refreshFromCaptureStatus(capture);
        });

        QCOMPARE(calibration.state(), QStringLiteral("IDLE"));
        recording.refresh();

        QCOMPARE(calibration.state(), QStringLiteral("OBSERVING"));
        QVERIFY(calibration.progress().value(QStringLiteral("finder_request_seen")).toBool());
    }

    // -- controller ---------------------------------------------------------

    void controllerProjectsTheTimelineInTimeOrderWithLocalClock()
    {
        CalibrationBackend backend;
        mr::CalibrationController controller;
        controller.setBackend(&backend);
        QJsonObject calibration = readyTimeline();
        // Deliberately out of order on the wire.
        QJsonArray shuffled{calibration.value(QStringLiteral("events")).toArray().at(3),
                            calibration.value(QStringLiteral("events")).toArray().at(0),
                            calibration.value(QStringLiteral("events")).toArray().at(4),
                            calibration.value(QStringLiteral("events")).toArray().at(1),
                            calibration.value(QStringLiteral("events")).toArray().at(2)};
        calibration.insert(QStringLiteral("events"), shuffled);
        controller.refreshFromCaptureStatus(
            QJsonObject{{QStringLiteral("calibration"), calibration}}.toVariantMap());

        QCOMPARE(controller.state(), QStringLiteral("READY"));
        QCOMPARE(controller.confirmCount(), 4);
        QCOMPARE(controller.events().size(), 5);
        QStringList ids;
        for (const auto &value : controller.events()) {
            ids.append(value.toMap().value(QStringLiteral("event_id")).toString());
            QVERIFY(!value.toMap().value(QStringLiteral("time_text")).toString().isEmpty());
        }
        QCOMPARE(ids, (QStringList{QStringLiteral("login-0"), QStringLiteral("finder_request-60000"),
                                   QStringLiteral("pop-120000"), QStringLiteral("duty_enter-124000"),
                                   QStringLiteral("duty_exit-214000")}));

        // A capture status with no calibration object is IDLE, not stale.
        controller.refreshFromCaptureStatus(QVariantMap{});
        QCOMPARE(controller.state(), QStringLiteral("IDLE"));
        QCOMPARE(controller.confirmCount(), 0);
        QVERIFY(controller.events().isEmpty());
    }

    void allCorrectSendsTheCommittedPayloadShape()
    {
        CalibrationBackend backend;
        mr::CalibrationController controller;
        controller.setBackend(&backend);
        controller.refreshFromCaptureStatus(
            QJsonObject{{QStringLiteral("calibration"), readyTimeline()}}.toVariantMap());

        QSignalSpy confirmed(&controller, &mr::CalibrationController::confirmed);
        QVariantMap verdicts;
        for (const auto &value : controller.events()) {
            const auto entry = value.toMap();
            if (entry.value(QStringLiteral("requires_confirmation")).toBool())
                verdicts.insert(entry.value(QStringLiteral("event_id")).toString(),
                                QStringLiteral("CORRECT"));
        }
        controller.confirm(verdicts);
        QCOMPARE(confirmed.size(), 1);
        QCOMPARE(confirmed.first().at(0).toString(), QStringLiteral("cn.2026.09.01.local"));
        QVERIFY(confirmed.first().at(1).toBool());
        QVERIFY(controller.error().isEmpty());

        QCOMPARE(backend.count("ConfirmCalibration"), 1);
        const QJsonObject sent = backend.payloads.last();
        QCOMPARE(sent.keys(), QStringList{QStringLiteral("verdicts")});
        const QJsonArray rows = sent.value(QStringLiteral("verdicts")).toArray();
        QCOMPARE(rows.size(), 4);
        const QStringList expected{QStringLiteral("finder_request-60000"), QStringLiteral("pop-120000"),
                                   QStringLiteral("duty_enter-124000"), QStringLiteral("duty_exit-214000")};
        for (int index = 0; index < rows.size(); ++index) {
            const QJsonObject row = rows.at(index).toObject();
            QCOMPARE(row.keys(), (QStringList{QStringLiteral("event_id"), QStringLiteral("verdict")}));
            QCOMPARE(row.value(QStringLiteral("event_id")).toString(), expected.at(index));
            QCOMPARE(row.value(QStringLiteral("verdict")).toString(), QStringLiteral("CORRECT"));
        }
    }

    void anIncompleteVerdictSetIsNeverSent()
    {
        CalibrationBackend backend;
        mr::CalibrationController controller;
        controller.setBackend(&backend);
        controller.refreshFromCaptureStatus(
            QJsonObject{{QStringLiteral("calibration"), readyTimeline()}}.toVariantMap());

        controller.confirm({{QStringLiteral("pop-120000"), QStringLiteral("CORRECT")}});
        QCOMPARE(backend.count("ConfirmCalibration"), 0);
        QVERIFY(!controller.error().isEmpty());
        verifyPlayerCopy(controller.error());
    }

    void aRejectedDraftIsExplainedInPlainWords()
    {
        CalibrationBackend backend;
        backend.confirmErrorCode = QStringLiteral("ERR_CALIBRATION_REJECTED");
        mr::CalibrationController controller;
        controller.setBackend(&backend);
        controller.refreshFromCaptureStatus(
            QJsonObject{{QStringLiteral("calibration"), readyTimeline()}}.toVariantMap());

        QSignalSpy rejected(&controller, &mr::CalibrationController::rejected);
        QVariantMap verdicts{{QStringLiteral("finder_request-60000"), QStringLiteral("CORRECT")},
                             {QStringLiteral("pop-120000"), QStringLiteral("WRONG")},
                             {QStringLiteral("duty_enter-124000"), QStringLiteral("CORRECT")},
                             {QStringLiteral("duty_exit-214000"), QStringLiteral("CORRECT")}};
        controller.confirm(verdicts);
        QCOMPARE(rejected.size(), 1);
        QCOMPARE(controller.error(),
                 QString::fromUtf8("有事件被标为不对，这次校准作废；再打一把随机任务后会重新核对。"));
        verifyPlayerCopy(controller.error());
        QVERIFY(!controller.busy());
        // The WRONG verdict travelled verbatim.
        const QJsonArray rows = backend.payloads.last().value(QStringLiteral("verdicts")).toArray();
        QCOMPARE(rows.at(1).toObject().value(QStringLiteral("verdict")).toString(),
                 QStringLiteral("WRONG"));
    }

    void discardAsksToObserveAgain()
    {
        CalibrationBackend backend;
        mr::CalibrationController controller;
        controller.setBackend(&backend);
        controller.refreshFromCaptureStatus(
            QJsonObject{{QStringLiteral("calibration"), readyTimeline()}}.toVariantMap());
        QVERIFY(controller.confirmCount() > 0);
        controller.discard();
        QCOMPARE(backend.count("DiscardCalibration"), 1);
        QCOMPARE(backend.payloads.last(), QJsonObject{});
        QCOMPARE(controller.state(), QStringLiteral("OBSERVING"));
        // Nothing of the discarded draft may linger on the card.
        QVERIFY(controller.events().isEmpty());
        QVERIFY(controller.blockers().isEmpty());
        QVERIFY(controller.progress().isEmpty());
        QCOMPARE(controller.confirmCount(), 0);
    }

    // -- live event ---------------------------------------------------------

    void calibrationChangedCostsExactlyOneCaptureStatusRead()
    {
        CalibrationBackend backend;
        backend.capture.insert(QStringLiteral("calibration"), observingCalibration());
        mr::AppController app(&backend, nullptr);
        // Let the constructor's own refreshAll and the automatic-recording
        // controller's first observation drain before anything is counted.
        QTest::qWait(80);
        backend.calls.clear();
        backend.payloads.clear();
        backend.capture.insert(QStringLiteral("calibration"), readyTimeline());

        backend.emitCalibrationChanged();
        QTRY_COMPARE(backend.count("GetCaptureStatus"), 1);
        QCoreApplication::processEvents();

        QCOMPARE(backend.count("GetCaptureStatus"), 1);
        QCOMPARE(backend.count("QueryRuns"), 0);
        QCOMPARE(backend.count("GetDashboardStats"), 0);
        QCOMPARE(backend.count("GetCurrentRun"), 0);
        QCOMPARE(backend.count("GetStatus"), 0);
        QCOMPARE(app.calibration()->state(), QStringLiteral("READY"));
        QCOMPARE(app.calibration()->confirmCount(), 4);
    }

    void calibratedProfilesAreLabelledInTheSidebar_data()
    {
        QTest::addColumn<bool>("shared");
        QTest::addColumn<QString>("origin");
        QTest::addColumn<QString>("label");
        QTest::newRow("local") << false << "LOCAL_CALIBRATION" << QString::fromUtf8("本机校准");
        QTest::newRow("shared") << true << "SHARED_CALIBRATION" << QString::fromUtf8("共享校准");
    }

    void calibratedProfilesAreLabelledInTheSidebar()
    {
        QFETCH(bool, shared);
        QFETCH(QString, origin);
        QFETCH(QString, label);
        mr::MockBackend backend;
        if (shared)
            backend.setSharedCalibrationFixture(QStringLiteral("verified"));
        else
            backend.setCalibrationFixture(QStringLiteral("done"));
        mr::AppController app(&backend, nullptr);
        QTRY_COMPARE(app.captureStatus().value(QStringLiteral("profile_origin")).toString(), origin);

        QQmlEngine engine;
        engine.rootContext()->setContextProperty(QStringLiteral("App"), &app);
        engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import MentorRecorder
Item {
    // The listening branch of the capture page's protocol wording.
    readonly property string label:
        App.captureStatus.profile_origin === "LOCAL_CALIBRATION" ? qsTr("本机校准")
        : App.captureStatus.profile_origin === "SHARED_CALIBRATION" ? qsTr("共享校准")
        : qsTr("档案匹配")
})", QUrl());
        std::unique_ptr<QObject> root(component.create());
        QVERIFY2(root != nullptr, qPrintable(component.errorString()));
        QCOMPARE(root->property("label").toString(), label);
    }

    // -- QML ----------------------------------------------------------------

    void aLongTimelineScrollsAndKeepsTheConfirmButtonInsideTheWindow()
    {
        // Real machine, 1.2.3: a morning of logins, zone changes and five queues made a timeline
        // taller than the window, and the dialog grew with it - 以后再说 / 核对并启用 ended up
        // below the bottom edge, so the calibration could be neither confirmed nor postponed.
        CalibrationBackend backend;
        QJsonObject calibration = readyTimeline();
        QJsonArray events = calibration.value(QStringLiteral("events")).toArray();
        for (int i = 0; i < 40; ++i)
            events.append(::event(QStringLiteral("zone"), 300000 + i * 1000, QString::fromUtf8("换区"), false));
        calibration.insert(QStringLiteral("events"), events);
        backend.capture.insert(QStringLiteral("calibration"), calibration);
        mr::AppController app(&backend, nullptr);
        QTRY_COMPARE(app.calibration()->state(), QStringLiteral("READY"));

        QQmlEngine engine;
        engine.rootContext()->setContextProperty(QStringLiteral("App"), &app);
        engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 720; height: 640; visible: true
    CalibrationDialog { id: inner; anchors.centerIn: parent }
})", QUrl());
        std::unique_ptr<QObject> root(component.create());
        QVERIFY2(root != nullptr, qPrintable(component.errorString()));
        auto *dialog = root->findChild<QObject *>(QStringLiteral("calibrationDialog"));
        QVERIFY(dialog);
        QVERIFY(QMetaObject::invokeMethod(dialog, "openDialog"));
        QTRY_VERIFY(dialog->property("visible").toBool());

        auto *confirm = dialog->findChild<QQuickItem *>(QStringLiteral("calibrationDialogConfirm"));
        QVERIFY(confirm);
        QTRY_VERIFY(confirm->height() > 0);
        // The popup lays its content out after it opens, so the position is waited for.
        QTRY_VERIFY2(confirm->mapToScene(QPointF(0, confirm->height())).y() <= 640.0,
                     qPrintable(QStringLiteral("confirm button bottom at %1, dialog height %2")
                                    .arg(confirm->mapToScene(QPointF(0, confirm->height())).y())
                                    .arg(dialog->property("height").toReal())));
        QVERIFY(dialog->property("height").toReal() <= 640.0);

        auto *timeline = dialog->findChild<QQuickItem *>(QStringLiteral("calibrationDialogTimelineView"));
        QVERIFY(timeline);
        QVERIFY(timeline->property("contentHeight").toReal() > timeline->height());
    }

    void dialogEnablesConfirmOnlyAfterEveryVerdictAndSendsThemAll()
    {
        mr::MockBackend backend;
        backend.setCalibrationFixture(QStringLiteral("ready"));
        mr::AppController app(&backend, nullptr);
        QTRY_COMPARE(app.calibration()->confirmCount(), 4);

        QQmlEngine engine;
        engine.rootContext()->setContextProperty(QStringLiteral("App"), &app);
        engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 720; height: 640; visible: true
    property alias dialog: inner
    CalibrationDialog { id: inner }
})", QUrl());
        std::unique_ptr<QObject> root(component.create());
        QVERIFY2(root != nullptr, qPrintable(component.errorString()));
        auto *dialog = root->findChild<QObject *>(QStringLiteral("calibrationDialog"));
        QVERIFY(dialog);
        QVERIFY(QMetaObject::invokeMethod(dialog, "openDialog"));
        QTRY_VERIFY(dialog->property("visible").toBool());

        auto *confirm = dialog->findChild<QObject *>(QStringLiteral("calibrationDialogConfirm"));
        QVERIFY(confirm);
        QVERIFY(!confirm->property("enabled").toBool());

        // The login row is a time reference only: no buttons, and no verdict.
        auto *content = qobject_cast<QQuickItem *>(dialog->property("contentItem").value<QObject *>());
        QVERIFY(content);
        QVERIFY(findVisualItem(content, QStringLiteral("calibrationEvent_login-0")));
        QVERIFY(!findVisualItem(content, QStringLiteral("calibrationVerdict_login-0")));

        const QStringList required{QStringLiteral("finder_request-60000"), QStringLiteral("pop-120000"),
                                   QStringLiteral("duty_enter-124000"), QStringLiteral("duty_exit-214000")};
        for (int index = 0; index < required.size(); ++index) {
            QVERIFY2(findVisualItem(content, QStringLiteral("calibrationVerdict_") + required.at(index)),
                     qPrintable(required.at(index)));
            // The third argument is the name a relabel carries; CORRECT never has one.
            QVERIFY(QMetaObject::invokeMethod(dialog, "setVerdict",
                                              Q_ARG(QVariant, QVariant(required.at(index))),
                                              Q_ARG(QVariant, QVariant(QStringLiteral("CORRECT"))),
                                              Q_ARG(QVariant, QVariant(QString()))));
            // Enabled only once the last one is answered.
            QCOMPARE(confirm->property("enabled").toBool(), index == required.size() - 1);
        }

        QSignalSpy confirmedSpy(app.calibration(), &mr::CalibrationController::confirmed);
        QVERIFY(QMetaObject::invokeMethod(dialog, "submit"));
        QTRY_COMPARE(confirmedSpy.size(), 1);
        QTRY_VERIFY(!dialog->property("visible").toBool());
        QCOMPARE(backend.calibrationFixture(), QStringLiteral("done"));
        QTRY_COMPARE(app.captureStatus().value(QStringLiteral("profile_status")).toString(),
                     QStringLiteral("VERIFIED"));
    }

    void dialogShowsThePlainRejectionSentenceAndCloses()
    {
        mr::MockBackend backend;
        backend.setCalibrationFixture(QStringLiteral("ready"));
        mr::AppController app(&backend, nullptr);
        QTRY_COMPARE(app.calibration()->confirmCount(), 4);

        QQmlEngine engine;
        engine.rootContext()->setContextProperty(QStringLiteral("App"), &app);
        engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 720; height: 640; visible: true
    CalibrationDialog { id: inner }
})", QUrl());
        std::unique_ptr<QObject> root(component.create());
        QVERIFY2(root != nullptr, qPrintable(component.errorString()));
        auto *dialog = root->findChild<QObject *>(QStringLiteral("calibrationDialog"));
        QVERIFY(dialog);
        QVERIFY(QMetaObject::invokeMethod(dialog, "openDialog"));
        QTRY_VERIFY(dialog->property("visible").toBool());

        for (const QString &id : {QStringLiteral("finder_request-60000"), QStringLiteral("pop-120000"),
                                  QStringLiteral("duty_enter-124000"), QStringLiteral("duty_exit-214000")}) {
            QVERIFY(QMetaObject::invokeMethod(dialog, "setVerdict", Q_ARG(QVariant, QVariant(id)),
                Q_ARG(QVariant, QVariant(id == QLatin1String("duty_enter-124000")
                                         ? QStringLiteral("WRONG") : QStringLiteral("CORRECT"))),
                Q_ARG(QVariant, QVariant(QString()))));
        }
        QSignalSpy rejected(app.calibration(), &mr::CalibrationController::rejected);
        QVERIFY(QMetaObject::invokeMethod(dialog, "submit"));
        QTRY_COMPARE(rejected.size(), 1);

        const QString expected = QString::fromUtf8(
            "有事件被标为不对，这次校准作废；再打一把随机任务后会重新核对。");
        QCOMPARE(dialog->property("noticeText").toString(), expected);
        QTRY_VERIFY(!dialog->property("visible").toBool());
        // Voided draft: the Collector observes again, so the user can play on.
        QCOMPARE(backend.calibrationFixture(), QStringLiteral("observing"));
    }

    void cardShowsProgressBlockersAndPlayerWording()
    {
        mr::MockBackend backend;
        backend.setCalibrationFixture(QStringLiteral("observing"));
        mr::AppController app(&backend, nullptr);
        QTRY_COMPARE(app.calibration()->state(), QStringLiteral("OBSERVING"));

        QQmlEngine engine;
        engine.rootContext()->setContextProperty(QStringLiteral("App"), &app);
        engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 720; height: card.implicitHeight + 32; visible: true
    color: Theme.surface
    CalibrationCard { id: card; x: 16; y: 16; width: parent.width - 32; height: implicitHeight }
})", QUrl());
        std::unique_ptr<QObject> root(component.create());
        QVERIFY2(root != nullptr, qPrintable(component.errorString()));
        auto *card = root->findChild<QObject *>(QStringLiteral("calibrationCard"));
        QVERIFY(card);
        auto *visualCard = qobject_cast<QQuickItem *>(card);
        QVERIFY(visualCard);

        auto *headline = card->findChild<QObject *>(QStringLiteral("calibrationHeadline"));
        QVERIFY(headline);
        verifyPlayerCopy(headline->property("text").toString());
        QVERIFY(headline->property("text").toString().contains(QString::fromUtf8("正在重新校准")));

        for (const QString &key : {QStringLiteral("finder_request_seen"), QStringLiteral("pop_seen"),
                                   QStringLiteral("duty_entry_seen"), QStringLiteral("duty_exit_seen")}) {
            QVERIFY2(findVisualItem(visualCard, QStringLiteral("calibrationProgress_") + key),
                     qPrintable(key));
        }
        // 核对并启用 only exists once the draft is ready.
        auto *confirm = findVisualItem(visualCard, QStringLiteral("calibrationConfirmButton"));
        QVERIFY(confirm);
        QVERIFY(!confirm->isVisible());
        auto *discard = findVisualItem(visualCard, QStringLiteral("calibrationDiscardButton"));
        QVERIFY(discard);
        QVERIFY(discard->isVisible());

        backend.setCalibrationFixture(QStringLiteral("blocked"));
        app.refreshStatus();
        QTRY_COMPARE(app.calibration()->state(), QStringLiteral("BLOCKED"));
        QCOMPARE(app.calibration()->blockers().size(), 1);
        verifyPlayerCopy(app.calibration()->blockers().first());
        QTRY_VERIFY(findVisualItem(visualCard, QStringLiteral("calibrationBlockers")));
    }

    void retainedProgressExplainsReloginWithoutClearingEvidence()
    {
        CalibrationBackend backend;
        QJsonObject calibration = observingCalibration();
        auto progress = calibration.value(QStringLiteral("progress")).toObject();
        progress.insert(QStringLiteral("pop_seen"), true);
        progress.insert(QStringLiteral("duty_entry_seen"), true);
        calibration.insert(QStringLiteral("progress"), progress);
        const QString legacyBlocker = QString::fromUtf8("打完这把副本，离开后再回来。");
        calibration.insert(QStringLiteral("blockers"), QJsonArray{legacyBlocker});

        auto entry = ::event(QStringLiteral("duty_enter"), 5574991, QString::fromUtf8("进入副本"), true);
        entry.insert(QStringLiteral("at_utc"), QStringLiteral("2026-09-13T12:53:59.449Z"));
        auto login = ::event(QStringLiteral("login"), 1831870, QString::fromUtf8("登录进入游戏"), false);
        login.insert(QStringLiteral("at_utc"), QStringLiteral("2026-09-14T00:17:28.451Z"));
        auto olderLogin = ::event(QStringLiteral("login"), 5449216, QString::fromUtf8("登录进入游戏"), false);
        olderLogin.insert(QStringLiteral("at_utc"), QStringLiteral("2026-09-13T12:51:53.674Z"));
        auto invalidLogin = ::event(QStringLiteral("login"), 9999999, QString::fromUtf8("登录进入游戏"), false);
        invalidLogin.insert(QStringLiteral("at_utc"), QStringLiteral("invalid"));
        // Deliberately unordered, across midnight, with the session clock reset.
        calibration.insert(QStringLiteral("events"), QJsonArray{login, invalidLogin, entry, olderLogin});
        backend.capture.insert(QStringLiteral("calibration"), calibration);
        mr::AppController app(&backend, nullptr);
        QTRY_COMPARE(app.calibration()->state(), QStringLiteral("OBSERVING"));

        QQmlEngine engine;
        engine.rootContext()->setContextProperty(QStringLiteral("App"), &app);
        engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 720; height: card.implicitHeight + 32; visible: true
    color: Theme.surface
    CalibrationCard { id: card; x: 16; y: 16; width: parent.width - 32; height: implicitHeight }
})", QUrl());
        std::unique_ptr<QObject> root(component.create());
        QVERIFY2(root != nullptr, qPrintable(component.errorString()));
        auto *card = qobject_cast<QQuickItem *>(root->findChild<QObject *>(QStringLiteral("calibrationCard")));
        QVERIFY(card);
        auto *context = findVisualItem(card, QStringLiteral("calibrationProgressContext"));
        auto *times = findVisualItem(card, QStringLiteral("calibrationEvidenceTimes"));
        auto *blocker = findVisualItem(card, QStringLiteral("calibrationBlockerText"));
        QVERIFY(context);
        QVERIFY(times);
        QVERIFY(blocker);
        QVERIFY(context->property("text").toString().contains(QString::fromUtf8("累计校准进度")));
        QVERIFY(context->property("text").toString().contains(QString::fromUtf8("不代表你当前仍在副本中")));
        QTRY_VERIFY(card->property("reloggedSinceEntry").toBool());
        auto localStamp = [](const QJsonObject &value) {
            return QDateTime::fromString(value.value(QStringLiteral("at_utc")).toString(), Qt::ISODateWithMs)
                .toLocalTime().toString(QStringLiteral("yyyy-MM-dd HH:mm:ss"));
        };
        QCOMPARE(times->property("text").toString(),
            QString::fromUtf8("最近识别登录：%1\n已采到的进本：%2").arg(localStamp(login), localStamp(entry)));
        QVERIFY(blocker->property("text").toString().contains(QString::fromUtf8("已识别重新登录")));
        QVERIFY(!blocker->property("text").toString().contains(QString::fromUtf8("打完这把副本")));
        verifyPlayerCopy(blocker->property("text").toString());
        for (const QString &key : {QStringLiteral("finder_request_seen"), QStringLiteral("pop_seen"),
                                  QStringLiteral("duty_entry_seen")}) {
            auto *row = findVisualItem(card, QStringLiteral("calibrationProgressText_") + key);
            QVERIFY(row);
            QVERIFY(row->property("text").toString().endsWith(QString::fromUtf8(" · 已看到")));
        }
        QCOMPARE(app.calibration()->blockers(), QStringList{legacyBlocker});
        QCOMPARE(backend.count("DiscardCalibration"), 0);

        auto *window = qobject_cast<QQuickWindow *>(root.get());
        QVERIFY(window);
        QTest::qWait(100);
        const QImage frame = window->grabWindow();
        QVERIFY(!frame.isNull());
        QVERIFY(frame.save(QDir::current().absoluteFilePath(QStringLiteral("calibration-retained-progress.png"))));

        // A later entry starts a fresh incomplete chain; a previous login must not
        // make it read as a relogin after that new entry.
        entry.insert(QStringLiteral("at_utc"), QStringLiteral("2026-09-14T00:30:00.000Z"));
        calibration.insert(QStringLiteral("events"), QJsonArray{entry, login});
        backend.capture.insert(QStringLiteral("calibration"), calibration);
        app.refreshStatus();
        QTRY_VERIFY(!card->property("reloggedSinceEntry").toBool());
        QTRY_VERIFY(times->property("text").toString().contains(localStamp(entry)));
        QVERIFY(findVisualItem(card, QStringLiteral("calibrationBlockerText"))
            ->property("text").toString().contains(QString::fromUtf8("如果你已经离开副本")));

        // Older or empty snapshots do not leave the previous session's times on screen.
        calibration.insert(QStringLiteral("events"), QJsonArray{});
        backend.capture.insert(QStringLiteral("calibration"), calibration);
        app.refreshStatus();
        QTRY_VERIFY(times->property("text").toString().isEmpty());
        QVERIFY(!times->isVisible());

        // Do not reinterpret an unrelated Collector failure as a missing exit.
        const QString otherBlocker = QString::fromUtf8("先登录进入游戏。");
        calibration.insert(QStringLiteral("blockers"), QJsonArray{otherBlocker});
        backend.capture.insert(QStringLiteral("calibration"), calibration);
        app.refreshStatus();
        QTRY_COMPARE(findVisualItem(card, QStringLiteral("calibrationBlockerText"))
            ->property("text").toString(), otherBlocker);

        // Capture faults still take precedence over instructions to play another duty.
        // A provisional profile can capture while calibration continues in the background.
        backend.capture.insert(QStringLiteral("profile_status"), QStringLiteral("VERIFIED"));
        backend.capture.insert(QStringLiteral("silent_reason"), QStringLiteral("MIDSTREAM"));
        backend.capture.insert(QStringLiteral("midstream_suspected"), true);
        app.refreshStatus();
        app.recording()->refresh();
        QTRY_VERIFY(card->property("captureSilent").toBool());
        QVERIFY(findVisualItem(card, QStringLiteral("calibrationBlockerText"))
            ->property("text").toString().contains(QString::fromUtf8("现在抓包一条报文都解不出来")));
        QCOMPARE(backend.count("DiscardCalibration"), 0);
    }

    void aRecordingProfileReadsAsProvisionalWhileTheGameIsClosed()
    {
        // Real machine, 1.2.1: after 清空进度并重新观察 with the game closed the card said
        // "正在重新校准…期间不会生成记录" above a line saying recording works. WAITING is the
        // same calibration with no capture session under it; a named profile is still in force.
        CalibrationBackend backend;
        QJsonObject calibration = observingCalibration();
        calibration.insert(QStringLiteral("state"), QStringLiteral("WAITING"));
        calibration.insert(QStringLiteral("local_profile_id"), QStringLiteral("cn.2026.09.15.0000.0000.local"));
        backend.capture.insert(QStringLiteral("calibration"), calibration);
        mr::AppController app(&backend, nullptr);
        QTRY_COMPARE(app.calibration()->state(), QStringLiteral("WAITING"));
        QVERIFY(app.calibration()->provisional());

        calibration.remove(QStringLiteral("local_profile_id"));
        backend.capture.insert(QStringLiteral("calibration"), calibration);
        app.refreshStatus();
        QTRY_VERIFY(!app.calibration()->provisional());
    }

    void aPopShapeStaysUnconfirmedUntilDutyEntryAndWrapsAtNarrowWidth()
    {
        // A shape match is only diagnostic evidence. Until a duty entry supports
        // it, the card must neither claim a pop nor hide what verification remains.
        mr::MockBackend backend;
        backend.setCalibrationFixture(QStringLiteral("observing"));
        mr::AppController app(&backend, nullptr);
        QTRY_COMPARE(app.calibration()->state(), QStringLiteral("OBSERVING"));
        QVERIFY(!app.calibration()->progress().value(QStringLiteral("pop_seen")).toBool());
        QVERIFY(app.calibration()->progress().value(QStringLiteral("pop_shape_seen")).toBool());
        QVERIFY(!app.calibration()->progress().value(QStringLiteral("duty_entry_seen")).toBool());

        QQmlEngine engine;
        engine.rootContext()->setContextProperty(QStringLiteral("App"), &app);
        engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 320; height: card.implicitHeight + 32; visible: true
    color: Theme.surface
    CalibrationCard { id: card; x: 16; y: 16; width: parent.width - 32; height: implicitHeight }
})", QUrl());
        std::unique_ptr<QObject> root(component.create());
        QVERIFY2(root != nullptr, qPrintable(component.errorString()));
        auto *visualCard = qobject_cast<QQuickItem *>(
            root->findChild<QObject *>(QStringLiteral("calibrationCard")));
        QVERIFY(visualCard);

        auto *label = findVisualItem(
            visualCard, QStringLiteral("calibrationProgressText_pop_seen"));
        QVERIFY(label);
        const QString expected = QString::fromUtf8("匹配弹窗 · 尚未确认，等待进本核对");
        QCOMPARE(label->property("text").toString(), expected);
        verifyPlayerCopy(expected);

        auto *window = qobject_cast<QQuickWindow *>(root.get());
        QVERIFY(window);
        QTRY_VERIFY(window->isVisible());
        auto *discard = findVisualItem(
            visualCard, QStringLiteral("calibrationDiscardButton"));
        QVERIFY(discard);
        for (const int width : {320, 240}) {
            window->setWidth(width);
            QTRY_COMPARE(visualCard->width(), qreal(width - 32));
            QTest::qWait(100); // Let the resized layout polish before capturing failure evidence.
            const QImage frame = window->grabWindow();
            QVERIFY2(!frame.isNull(), "narrow calibration card screenshot is null");
            const QString screenshotPath = QDir::current().absoluteFilePath(width == 320
                ? QStringLiteral("calibration-pop-shape-narrow.png")
                : QStringLiteral("calibration-pop-shape-wrapped.png"));
            QVERIFY2(frame.save(screenshotPath), qPrintable(screenshotPath));
            QCOMPARE(frame.width(), qRound(width * window->devicePixelRatio()));

            // The real font fits on one line at 320px; 240px exercises wrapping.
            QTRY_VERIFY(label->property("width").toReal() > 0.0);
            if (width == 240)
                QTRY_VERIFY(label->property("lineCount").toInt() >= 2);
            QVERIFY(label->property("contentWidth").toReal()
                    <= label->property("width").toReal() + 0.5);
            QVERIFY(label->property("height").toReal()
                    >= label->property("contentHeight").toReal() - 0.5);
            QVERIFY(discard->isEnabled());
            QVERIFY(discard->isVisible());
            const QRectF discardInCard = discard->mapRectToItem(visualCard, discard->boundingRect());
            QVERIFY2(visualCard->boundingRect().contains(discardInCard),
                     "calibration discard button exceeds the card bounds");
            const QRectF discardInWindow = discard->mapRectToItem(
                window->contentItem(), discard->boundingRect());
            const QRectF windowBounds(0.0, 0.0, window->contentItem()->width(),
                                      window->contentItem()->height());
            QVERIFY2(windowBounds.contains(discardInWindow),
                     "calibration discard button exceeds the narrow window bounds");
        }
    }

    void aMissingPopShapeFieldKeepsTheOldCollectorFallback()
    {
        CalibrationBackend backend;
        // observingCalibration deliberately omits the optional pop_shape_seen
        // field, matching a Collector from before the diagnostic was added.
        backend.capture.insert(QStringLiteral("calibration"), observingCalibration());
        mr::AppController app(&backend, nullptr);
        QTRY_COMPARE(app.calibration()->state(), QStringLiteral("OBSERVING"));
        QVERIFY(!app.calibration()->progress().contains(QStringLiteral("pop_shape_seen")));

        QQmlEngine engine;
        engine.rootContext()->setContextProperty(QStringLiteral("App"), &app);
        engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 320; height: card.implicitHeight + 32; visible: true
    color: Theme.surface
    CalibrationCard { id: card; x: 16; y: 16; width: parent.width - 32; height: implicitHeight }
})", QUrl());
        std::unique_ptr<QObject> root(component.create());
        QVERIFY2(root != nullptr, qPrintable(component.errorString()));
        auto *visualCard = qobject_cast<QQuickItem *>(
            root->findChild<QObject *>(QStringLiteral("calibrationCard")));
        QVERIFY(visualCard);
        auto *label = findVisualItem(
            visualCard, QStringLiteral("calibrationProgressText_pop_seen"));
        QVERIFY(label);
        QCOMPARE(label->property("text").toString(),
                 QString::fromUtf8("匹配弹窗 · 还没见到"));
    }

    void theJobRowSaysRecognisedOrNotRatherThanSeen()
    {
        // A job message that never meets the calibration rule writes every record as
        // 职业未知, so the card must say so. "还没见到" is wrong here: the message was
        // on the wire, it just was not recognised.
        CalibrationBackend backend;
        backend.capture.insert(QStringLiteral("calibration"), observingCalibration());
        mr::AppController app(&backend, nullptr);
        QTRY_COMPARE(app.calibration()->state(), QStringLiteral("OBSERVING"));
        QVERIFY(!app.calibration()->progress().contains(QStringLiteral("job_seen")));

        QQmlEngine engine;
        engine.rootContext()->setContextProperty(QStringLiteral("App"), &app);
        engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 320; height: card.implicitHeight + 32; visible: true
    color: Theme.surface
    CalibrationCard { id: card; x: 16; y: 16; width: parent.width - 32; height: implicitHeight }
})", QUrl());
        std::unique_ptr<QObject> root(component.create());
        QVERIFY2(root != nullptr, qPrintable(component.errorString()));
        auto *visualCard = qobject_cast<QQuickItem *>(
            root->findChild<QObject *>(QStringLiteral("calibrationCard")));
        QVERIFY(visualCard);

        auto *job = findVisualItem(visualCard, QStringLiteral("calibrationProgressText_job_seen"));
        QVERIFY(job);
        const QString unseen = QString::fromUtf8("职业 · 还没认出来，先记为未知");
        QCOMPARE(job->property("text").toString(), unseen);
        verifyPlayerCopy(unseen);

        // The other rows keep their wording.
        auto *exit = findVisualItem(visualCard, QStringLiteral("calibrationProgressText_duty_exit_seen"));
        QVERIFY(exit);
        QCOMPARE(exit->property("text").toString(), QString::fromUtf8("出本 · 还没见到"));

        // Once the Collector reports it, the row says so in the same words the rule uses.
        CalibrationBackend recognised;
        QJsonObject calibration = observingCalibration();
        QJsonObject progress = calibration.value(QStringLiteral("progress")).toObject();
        progress.insert(QStringLiteral("job_seen"), true);
        calibration.insert(QStringLiteral("progress"), progress);
        recognised.capture.insert(QStringLiteral("calibration"), calibration);
        mr::AppController seen(&recognised, nullptr);
        QTRY_VERIFY(seen.calibration()->progress().value(QStringLiteral("job_seen")).toBool());

        QQmlEngine seenEngine;
        seenEngine.rootContext()->setContextProperty(QStringLiteral("App"), &seen);
        seenEngine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QQmlComponent seenComponent(&seenEngine);
        seenComponent.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 320; height: card.implicitHeight + 32; visible: true
    color: Theme.surface
    CalibrationCard { id: card; x: 16; y: 16; width: parent.width - 32; height: implicitHeight }
})", QUrl());
        std::unique_ptr<QObject> seenRoot(seenComponent.create());
        QVERIFY2(seenRoot != nullptr, qPrintable(seenComponent.errorString()));
        auto *seenCard = qobject_cast<QQuickItem *>(
            seenRoot->findChild<QObject *>(QStringLiteral("calibrationCard")));
        QVERIFY(seenCard);
        auto *seenJob = findVisualItem(seenCard, QStringLiteral("calibrationProgressText_job_seen"));
        QVERIFY(seenJob);
        QCOMPARE(seenJob->property("text").toString(), QString::fromUtf8("职业 · 已认出"));
    }

    void aPlayedDutyReadsAsSeenEvenBeforeItSupportsTheMatch()
    {
        // After a finished roulette every row saying 还没见到 reads as software that
        // noticed nothing. duty_entry_seen is the stricter claim; duty_zone_seen is
        // what the player actually did.
        CalibrationBackend backend;
        QJsonObject calibration = observingCalibration();
        QJsonObject progress = calibration.value(QStringLiteral("progress")).toObject();
        progress.insert(QStringLiteral("duty_zone_seen"), true);
        calibration.insert(QStringLiteral("progress"), progress);
        backend.capture.insert(QStringLiteral("calibration"), calibration);
        mr::AppController app(&backend, nullptr);
        QTRY_COMPARE(app.calibration()->state(), QStringLiteral("OBSERVING"));
        QVERIFY(!app.calibration()->progress().value(QStringLiteral("duty_entry_seen")).toBool());

        QQmlEngine engine;
        engine.rootContext()->setContextProperty(QStringLiteral("App"), &app);
        engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 320; height: card.implicitHeight + 32; visible: true
    color: Theme.surface
    CalibrationCard { id: card; x: 16; y: 16; width: parent.width - 32; height: implicitHeight }
})", QUrl());
        std::unique_ptr<QObject> root(component.create());
        QVERIFY2(root != nullptr, qPrintable(component.errorString()));
        auto *visualCard = qobject_cast<QQuickItem *>(
            root->findChild<QObject *>(QStringLiteral("calibrationCard")));
        QVERIFY(visualCard);

        auto *entry = findVisualItem(
            visualCard, QStringLiteral("calibrationProgressText_duty_entry_seen"));
        QVERIFY(entry);
        const QString expected = QString::fromUtf8("进本 · 已看到，还没能和这次排本对上");
        QCOMPARE(entry->property("text").toString(), expected);
        verifyPlayerCopy(expected);

        // 出本 has no near evidence of its own, so it must keep the plain wording.
        auto *exit = findVisualItem(
            visualCard, QStringLiteral("calibrationProgressText_duty_exit_seen"));
        QVERIFY(exit);
        QCOMPARE(exit->property("text").toString(), QString::fromUtf8("出本 · 还没见到"));
    }

    void aMissingDutyZoneFieldKeepsTheOldCollectorFallback()
    {
        CalibrationBackend backend;
        // observingCalibration omits duty_zone_seen, matching a Collector from
        // before the field existed.
        backend.capture.insert(QStringLiteral("calibration"), observingCalibration());
        mr::AppController app(&backend, nullptr);
        QTRY_COMPARE(app.calibration()->state(), QStringLiteral("OBSERVING"));
        QVERIFY(!app.calibration()->progress().contains(QStringLiteral("duty_zone_seen")));

        QQmlEngine engine;
        engine.rootContext()->setContextProperty(QStringLiteral("App"), &app);
        engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 320; height: card.implicitHeight + 32; visible: true
    color: Theme.surface
    CalibrationCard { id: card; x: 16; y: 16; width: parent.width - 32; height: implicitHeight }
})", QUrl());
        std::unique_ptr<QObject> root(component.create());
        QVERIFY2(root != nullptr, qPrintable(component.errorString()));
        auto *visualCard = qobject_cast<QQuickItem *>(
            root->findChild<QObject *>(QStringLiteral("calibrationCard")));
        QVERIFY(visualCard);
        auto *entry = findVisualItem(
            visualCard, QStringLiteral("calibrationProgressText_duty_entry_seen"));
        QVERIFY(entry);
        QCOMPARE(entry->property("text").toString(), QString::fromUtf8("进本 · 还没见到"));
    }

    void settingsToggleWritesTheCollectorOwnedSwitch()
    {
        mr::MockBackend backend;
        mr::AppController app(&backend, nullptr);
        QTRY_VERIFY(app.captureSettingsLoaded());
        // Default on: off means fail-closed silence on every patch day.
        QVERIFY(app.captureSettings().value(QStringLiteral("auto_calibration_enabled")).toBool());

        // The whitelist in AppController has to know the key, or the write is
        // dropped before it reaches the debounce.
        app.updateCaptureSetting(QStringLiteral("auto_calibration_enabled"), false);
        QVERIFY(!app.captureSettings().value(QStringLiteral("auto_calibration_enabled")).toBool());
        // And the Collector's confirmed answer has to agree once the debounce
        // has flushed, not just the optimistic local copy.
        QTest::qWait(1200);
        QVERIFY(!app.captureSettings().value(QStringLiteral("auto_calibration_enabled")).toBool());
        QVERIFY(app.captureSettingsError().isEmpty());

        app.updateCaptureSetting(QStringLiteral("auto_calibration_enabled"), true);
        QTest::qWait(1200);
        QVERIFY(app.captureSettings().value(QStringLiteral("auto_calibration_enabled")).toBool());
        QVERIFY(app.captureSettingsError().isEmpty());
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
    QStringList cjkFamilies;
    const QString windowsDir = qEnvironmentVariable("WINDIR", QStringLiteral("C:/Windows"));
    for (const char *file : {"Fonts/msyh.ttc", "Fonts/simhei.ttf"}) {
        const QString path = QDir(windowsDir).filePath(QString::fromLatin1(file));
        const int id = QFontDatabase::addApplicationFont(path);
        const QStringList registered = QFontDatabase::applicationFontFamilies(id);
        if (id >= 0 && !registered.isEmpty()) {
            for (const QString &family : registered) {
                if (!cjkFamilies.contains(family))
                    cjkFamilies.append(family);
            }
        }
    }
    if (cjkFamilies.isEmpty()) {
        qCritical("CalibrationTests requires a loadable Windows CJK font (msyh.ttc or simhei.ttf).");
        return 6;
    }
    qInfo() << "calibration CJK families" << cjkFamilies;
    QFont testFont;
    testFont.setFamilies(cjkFamilies);
    app.setFont(testFont);
#endif
    CalibrationTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "CalibrationTests.moc"
