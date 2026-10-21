#include "EvidenceSubmissionController.h"
#include "CaptureValidationController.h"
#include "AutomaticRecordingController.h"
#include "IBackend.h"
#include "AppController.h"
#include "Formatters.h"
#include "MockBackend.h"
#include "TestCollectorGuard.h"

#include <QDir>
#include <QGuiApplication>
#include <QFontDatabase>
#include <QJsonArray>
#include <QJsonDocument>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QSignalSpy>
#include <QStandardPaths>
#include <QTest>
#include <QUrlQuery>
#include <memory>

class EvidenceTheme final : public QObject {
    Q_OBJECT
    Q_PROPERTY(bool dark MEMBER dark CONSTANT)
public:
    bool dark = true;
};

namespace {
class EvidenceBackend final : public mr::IBackend {
public:
    struct Call { QString type; QJsonObject payload; QPointer<mr::BackendReply> reply; };
    QList<Call> calls;
    bool connected = true;
    QString backendName() const override { return QStringLiteral("scripted"); }
    bool isConnected() const override { return connected; }
    void setConnected(bool value) { connected = value; Q_EMIT connectionChanged(); }
    mr::BackendReply *request(const QString &type, const QJsonObject &payload = {}) override {
        auto *reply = new mr::BackendReply(QString::number(calls.size()), type, this);
        calls.append({type, payload, reply});
        return reply;
    }
};

QString jsonFor(const QString &kind = QStringLiteral("CODE_VERIFICATION"), const QString &hash = QString(64, 'a'))
{
    return QString::fromUtf8(QJsonDocument(QJsonObject{
        {QStringLiteral("format"), QStringLiteral("MentorRecorder.CalibrationEvidence")},
        {QStringLiteral("schema_version"), 1}, {QStringLiteral("kind"), kind},
        {QStringLiteral("evidence_id"), QStringLiteral("11111111-1111-4111-8111-111111111111")},
        {QStringLiteral("producer"), QJsonObject{{QStringLiteral("app_version"), QStringLiteral("1.7.2")},
            {QStringLiteral("rules_version"), QStringLiteral("calibration-evidence-v1")}}},
        {QStringLiteral("target"), QJsonObject{{QStringLiteral("region"), QStringLiteral("GLOBAL")},
            {QStringLiteral("game_build"), QStringLiteral("2026.09.10.0000.0000")},
            {QStringLiteral("template_profile_id"), QStringLiteral("test-profile")},
            {QStringLiteral("template_sha256"), QString(64, 'b')},
            {QStringLiteral("code_sha256"), hash}, {QStringLiteral("quarantine_epoch"), 0}}},
        {QStringLiteral("sessions"), QJsonArray{}}, {QStringLiteral("observations"), QJsonArray{}}
    }).toJson());
}

QUrl issueUrl(const QString &json, const QString &kind = QStringLiteral("CODE_VERIFICATION"), bool prefill = true)
{
    QUrl url(QStringLiteral("https://github.com/Harendotes-Tang/MentorRecorder-Calibrations/issues/new"));
    QUrlQuery query;
    query.addQueryItem(QStringLiteral("template"), kind == QLatin1String("CODE_VERIFICATION")
                       ? QStringLiteral("evidence-calibration.yml") : QStringLiteral("template-observation.yml"));
    if (prefill) query.addQueryItem(QStringLiteral("evidence"), json);
    url.setQuery(query);
    return url;
}

QJsonObject answer(const QString &json, const QString &kind = QStringLiteral("CODE_VERIFICATION"), bool prefill = true)
{
    return {{QStringLiteral("available"), true}, {QStringLiteral("kind"), kind},
            {QStringLiteral("evidence_json"), json},
            {QStringLiteral("issue_url"), issueUrl(json, kind, prefill).toString(QUrl::FullyEncoded)},
            {QStringLiteral("evidence_in_url"), prefill}, {QStringLiteral("reason"), QJsonValue::Null}};
}

QQuickItem *visual(QQuickItem *root, const QString &name)
{
    if (!root) return nullptr;
    if (root->objectName() == name) return root;
    for (auto *child : root->childItems()) if (auto *found = visual(child, name)) return found;
    return nullptr;
}

class CaptureHost final : public mr::CaptureValidationController::Host {
public:
    bool formal = false;
    void setFormalCapturing(bool running) { formal = running; }
    bool candidate = true;
    QString profile = QStringLiteral("UNSUPPORTED_BUILD");
    bool formalCapturing() const override { return formal; }
    bool candidateValidationEnabled() const override { return candidate; }
    QString captureProfileStatus() const override { return profile; }
    void applyFormalCaptureStatus(const QVariantMap &) override { formal = false; }
    void refreshStatus() override {}
    void showToast(const QString &) override {}
};
}

class EvidenceSubmissionTests final : public QObject {
    Q_OBJECT
private Q_SLOTS:
    void initTestCase()
    {
        const QString qmlRoot = QString::fromUtf8(MR_DESKTOP_QML_DIR);
        qmlRegisterSingletonType(QUrl::fromLocalFile(qmlRoot + "/Theme.qml"), "MentorRecorder", 1, 0, "Theme");
        for (const auto &directory : {QStringLiteral("/components"), QStringLiteral("/dialogs"), QStringLiteral("/pages")}) {
            for (const auto &file : QDir(qmlRoot + directory).entryList({"*.qml"}, QDir::Files)) {
                const auto name = file.chopped(4).toUtf8();
                qmlRegisterType(QUrl::fromLocalFile(qmlRoot + directory + '/' + file), "MentorRecorder", 1, 0, name.constData());
            }
        }
    }

    void previewAndCancelHaveNoSideEffects()
    {
        EvidenceBackend backend;
        mr::EvidenceSubmissionController controller(&backend);
        int copied = 0;
        int opened = 0;
        controller.setClipboardWriter([&](const QString &) { ++copied; });
        controller.setUrlOpener([&](const QUrl &) { ++opened; return true; });
        controller.previewCalibration();
        QCOMPARE(backend.calls.last().type, QStringLiteral("GetCalibrationEvidence"));
        QVERIFY(backend.calls.last().payload.isEmpty());
        QVERIFY(controller.busy());
        QVERIFY(!controller.canSubmit());
        const auto json = jsonFor();
        backend.calls.last().reply->succeed(answer(json));
        QCOMPARE(controller.evidenceJson(), json);
        QVERIFY(controller.canSubmit());
        QCOMPARE(copied, 0); QCOMPARE(opened, 0);
        controller.dismiss();
        QVERIFY(!controller.canSubmit());
        QVERIFY(controller.evidenceJson().isEmpty());
        controller.copyAndOpen();
        QCOMPARE(copied, 0); QCOMPARE(opened, 0);
    }

    void explicitActionCopiesExactJsonAndLongFallback()
    {
        EvidenceBackend backend;
        mr::EvidenceSubmissionController controller(&backend);
        QString copied;
        QUrl opened;
        controller.setClipboardWriter([&](const QString &text) { copied = text; });
        controller.setUrlOpener([&](const QUrl &url) { opened = url; return true; });
        QString json = jsonFor();
        json += QString(qsizetype{48} * 1024 - json.toUtf8().size(), ' ');
        QCOMPARE(json.toUtf8().size(), 48 * 1024);
        controller.previewCalibration();
        backend.calls.last().reply->succeed(answer(json, "CODE_VERIFICATION", false));
        QVERIFY(controller.canSubmit());
        QVERIFY(!controller.evidenceInUrl());
        controller.copyAndOpen();
        QCOMPARE(copied, json);
        QVERIFY(!QUrlQuery(opened).hasQueryItem(QStringLiteral("evidence")));
        controller.previewCalibration();
        backend.calls.last().reply->succeed(answer(json + " ", "CODE_VERIFICATION", false));
        QVERIFY(!controller.canSubmit());
    }

    void fixedUrlAndQueryDefence()
    {
        const auto json = jsonFor();
        const auto good = issueUrl(json);
        QVERIFY(mr::EvidenceSubmissionController::isSubmissionUrl(good, "CODE_VERIFICATION", json, true));
        for (const QString &replacement : {
                 QStringLiteral("https://github.com.evil.invalid/Harendotes-Tang/MentorRecorder-Calibrations/issues/new?template=evidence-calibration.yml"),
                 QStringLiteral("https://github.com/other/MentorRecorder-Calibrations/issues/new?template=evidence-calibration.yml"),
                 QStringLiteral("https://github.com/Harendotes-Tang/MentorRecorder-Calibrations/issues/new?template=template-observation.yml"),
                 QStringLiteral("https://user@github.com/Harendotes-Tang/MentorRecorder-Calibrations/issues/new?template=evidence-calibration.yml"),
                 QStringLiteral("https://github.com:443/Harendotes-Tang/MentorRecorder-Calibrations/issues/new?template=evidence-calibration.yml"),
                 QStringLiteral("https://github.com/Harendotes-Tang/MentorRecorder-Calibrations/issues/new?template=evidence-calibration.yml&title=injected"),
                 QStringLiteral("https://github.com/Harendotes-Tang/MentorRecorder-Calibrations/issues/new?template=evidence-calibration.yml&template=evidence-calibration.yml"),
                 QStringLiteral("https://github.com/Harendotes-Tang/MentorRecorder-Calibrations/issues/new?template=evidence-calibration.yml#fragment")}) {
            QVERIFY2(!mr::EvidenceSubmissionController::isSubmissionUrl(QUrl(replacement), "CODE_VERIFICATION", json, false), qPrintable(replacement));
        }
        QVERIFY(!mr::EvidenceSubmissionController::isSubmissionUrl(good, "CODE_VERIFICATION", json + " ", true));
        QVERIFY(!mr::EvidenceSubmissionController::isSubmissionUrl(good, "TEMPLATE_OBSERVATION", json, true));
        auto extra = good; QUrlQuery query(extra); query.addQueryItem("evidence", json); extra.setQuery(query);
        QVERIFY(!mr::EvidenceSubmissionController::isSubmissionUrl(extra, "CODE_VERIFICATION", json, true));
    }

    void lateRepliesDisconnectAndOldBackend()
    {
        EvidenceBackend backend;
        mr::EvidenceSubmissionController controller(&backend);
        controller.previewCalibration(QString(64, 'a'));
        auto first = backend.calls.last().reply;
        QCOMPARE(backend.calls.last().payload.value("code_sha256").toString(), QString(64, 'a'));
        controller.previewCalibration(QString(64, 'c'));
        first->succeed(answer(jsonFor()));
        QVERIFY(controller.busy());
        QVERIFY(controller.evidenceJson().isEmpty());
        backend.calls.last().reply->succeed(answer(jsonFor("CODE_VERIFICATION", QString(64, 'c'))));
        QVERIFY(controller.canSubmit());
        backend.setConnected(false);
        QVERIFY(!controller.canSubmit());
        QVERIFY(controller.evidenceJson().isEmpty());
        backend.setConnected(true);
        controller.previewCalibration();
        auto late = backend.calls.last().reply;
        controller.dismiss();
        late->succeed(answer(jsonFor()));
        QVERIFY(!controller.visible()); QVERIFY(!controller.canSubmit());
        controller.previewCalibration();
        backend.calls.last().reply->fail("ERR_UNKNOWN_MESSAGE", "unknown");
        QVERIFY(!controller.busy()); QVERIFY(!controller.canSubmit());
        QVERIFY(controller.reason().contains(QString::fromUtf8("尚不支持")));
        controller.previewCalibration();
        backend.calls.last().reply->succeed({{"available", false}, {"reason", "NO_HEALTHY_SESSIONS"}});
        QVERIFY(!controller.canSubmit()); QVERIFY(!controller.reason().isEmpty());
    }

    void changedTargetOrTraceInvalidatesPendingAndReadyResults()
    {
        EvidenceBackend backend;
        mr::EvidenceSubmissionController controller(&backend);
        const QVariantMap global{{"region", "GLOBAL"}, {"game_build", "2026.09.10.0000.0000"}};
        controller.refreshFromCaptureStatus(global);
        controller.previewCalibration();
        auto old = backend.calls.last().reply;
        controller.refreshFromCaptureStatus({{"region", "CN"}});
        old->succeed(answer(jsonFor()));
        QVERIFY(!controller.canSubmit()); QVERIFY(controller.evidenceJson().isEmpty());
        controller.refreshFromValidationStatus({{"state", "COMPLETED"}, {"sha256", "first"}});
        controller.previewTemplateObservation();
        backend.calls.last().reply->succeed(answer(jsonFor("TEMPLATE_OBSERVATION"), "TEMPLATE_OBSERVATION"));
        QVERIFY(controller.canSubmit());
        controller.refreshFromValidationStatus({{"state", "RECORDING"}});
        QVERIFY(!controller.canSubmit());
        controller.previewCalibration(QString(64, 'a'));
        backend.calls.last().reply->succeed(answer(jsonFor("CODE_VERIFICATION", QString(64, 'b'))));
        QVERIFY(!controller.canSubmit());
        const qsizetype calls = backend.calls.size();
        controller.previewCalibration("not-a-hash");
        QCOMPARE(backend.calls.size(), calls);
    }

    void destroyedControllerAndBackendAreSafe()
    {
        auto backend = std::make_unique<EvidenceBackend>();
        auto controller = std::make_unique<mr::EvidenceSubmissionController>(backend.get());
        controller->previewCalibration();
        auto reply = backend->calls.last().reply;
        controller.reset();
        reply->succeed(answer(jsonFor()));
        controller = std::make_unique<mr::EvidenceSubmissionController>(backend.get());
        controller->previewCalibration();
        backend.reset();
        QVERIFY(!controller->canSubmit());
    }

    void candidateChoicesRequireFullIdentityAndClipboardReentryCannotOpenStaleForm()
    {
        EvidenceBackend backend;
        mr::EvidenceSubmissionController controller(&backend);
        controller.refreshFromCaptureStatus({{"region", "GLOBAL"}, {"calibration", QVariantMap{
            {"shared", QVariantMap{{"candidates", QVariantList{
                QVariantMap{{"sha12", "aaaaaaaaaaaa"}, {"code_sha256", QString(64, 'a')}},
                QVariantMap{{"sha12", "bbbbbbbbbbbb"}, {"code_sha256", QString(64, 'b')}},
                QVariantMap{{"sha12", "cccccccccccc"}}}}}}}}});
        QCOMPARE(controller.candidateChoices().size(), 2);
        controller.previewCalibration();
        backend.calls.last().reply->succeed({{"available", false}, {"reason", "SELECT_CANDIDATE"}});
        QVERIFY(!controller.canSubmit());
        controller.previewCalibration(controller.candidateChoices().at(1).toMap().value("code_sha256").toString());
        QCOMPARE(backend.calls.last().payload.value("code_sha256").toString(), QString(64, 'b'));
        backend.calls.last().reply->succeed(answer(jsonFor("CODE_VERIFICATION", QString(64, 'b'))));
        QVERIFY(controller.canSubmit());
        int opened = 0;
        controller.setUrlOpener([&](const QUrl &) { ++opened; return true; });
        controller.setClipboardWriter([&](const QString &) { controller.dismiss(); });
        controller.copyAndOpen();
        QCOMPARE(opened, 0);
    }

    void metadataButtonNeverStartsFormalCapture()
    {
        EvidenceBackend backend;
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
        EvidenceBackend backend;
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
        EvidenceBackend backend;
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
        int stopCalls = 0;
        for (const auto &call : backend.calls) stopCalls += call.type == QLatin1String("StopCaptureValidation");
        QCOMPARE(stopCalls, 1); // 唯一的停止来自玩家按钮，自动记录没有争抢。
    }

    void longDialogKeyboardAndThemes_data()
    {
        QTest::addColumn<QString>("style"); QTest::addColumn<bool>("dark");
        for (const auto &style : {QStringLiteral("classic"), QStringLiteral("eorzea")}) {
            QTest::newRow(qPrintable(style + "-dark")) << style << true;
            QTest::newRow(qPrintable(style + "-light")) << style << false;
        }
    }

    void guidedMarkersWorkWithoutMaintainerMode()
    {
        mr::MockBackend backend;
        backend.setValidationFixture(QStringLiteral("RECORDING"));
        mr::AppController app(&backend, nullptr);
        app.recording()->setUserObservation(true);
        mr::Formatters formatters;
        mr::EvidenceSubmissionController evidence(&backend);
        QQmlEngine engine;
        engine.rootContext()->setContextProperty("App", &app);
        engine.rootContext()->setContextProperty("Fmt", &formatters);
        engine.rootContext()->setContextProperty("CalibrationEvidence", &evidence);
        engine.rootContext()->setContextProperty("ReduceMotion", true);
        QSignalSpy warnings(&engine, &QQmlEngine::warnings);
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 1100; height: 720; visible: true; color: Theme.contentBackground
    ScrollView { anchors.fill: parent
        contentWidth: availableWidth
        CaptureValidationEvidenceCard { width: parent.width; guidedMode: true }
    }
})", QUrl());
        std::unique_ptr<QObject> root(component.create());
        QVERIFY2(root != nullptr, qPrintable(component.errorString()));
        auto *window = qobject_cast<QQuickWindow *>(root.get());
        QVERIFY(window);
        QTRY_VERIFY(app.validationMarkerEnabled());
        QVERIFY(!app.maintainerToolsVisible());
        auto *marker = visual(window->contentItem(), "templateObservationMarker_queued");
        const auto *action = visual(window->contentItem(), "templateObservationActionButton");
        QVERIFY(marker && action);
        QVERIFY(marker->isEnabled());
        marker->forceActiveFocus(Qt::TabFocusReason);
        QTest::keyClick(window, Qt::Key_Space);
        QTRY_VERIFY(!app.validationFeedback().isEmpty());
        QVERIFY(!app.maintainerToolsVisible());
        const auto dir = qEnvironmentVariable("MR_CALIBRATION_EVIDENCE_DIR");
        if (!dir.isEmpty()) {
            QVERIFY(QDir().mkpath(dir));
            QVERIFY(window->grabWindow().save(dir + "/guided-observation.png"));
        }
        QCOMPARE(warnings.size(), 0);
    }

    void candidateSelectorKeyboardSwitchDisablesOldTarget()
    {
        EvidenceBackend backend;
        mr::EvidenceSubmissionController controller(&backend);
        const QVariantList candidates{
            QVariantMap{{"sha12", "aaaaaaaaaaaa"}, {"code_sha256", QString(64, 'a')}},
            QVariantMap{{"sha12", "bbbbbbbbbbbb"}, {"code_sha256", QString(64, 'b')}}};
        controller.refreshFromCaptureStatus({{"calibration", QVariantMap{
            {"shared", QVariantMap{{"candidates", candidates}}}}}});
        QQmlEngine engine;
        engine.rootContext()->setContextProperty("Evidence", &controller);
        engine.rootContext()->setContextProperty("ReduceMotion", true);
        QSignalSpy warnings(&engine, &QQmlEngine::warnings);
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 1100; height: 720; visible: true
    CalibrationEvidenceDialog { evidenceController: Evidence }
})", QUrl());
        std::unique_ptr<QObject> root(component.create());
        QVERIFY2(root != nullptr, qPrintable(component.errorString()));
        auto *window = qobject_cast<QQuickWindow *>(root.get());
        QVERIFY(window);
        controller.previewCalibration(QString(64, 'a'));
        backend.calls.last().reply->succeed(answer(jsonFor()));
        auto *cancel = visual(window->contentItem(), "evidenceCancelButton");
        auto *selector = visual(window->contentItem(), "evidenceCandidateSelector");
        QVERIFY(cancel && selector);
        QTRY_VERIFY(cancel->hasActiveFocus());
        QTest::keyClick(window, Qt::Key_Tab);
        QTRY_VERIFY(selector->hasActiveFocus());
        QTest::keyClick(window, Qt::Key_Down);
        QTRY_COMPARE(backend.calls.last().payload.value("code_sha256").toString(), QString(64, 'b'));
        QVERIFY(!controller.canSubmit());
        QVERIFY(controller.evidenceJson().isEmpty());
        backend.calls.last().reply->succeed(answer(jsonFor("CODE_VERIFICATION", QString(64, 'b'))));
        QVERIFY(controller.canSubmit());
        QCOMPARE(selector->property("currentIndex").toInt(), 1);
        QCOMPARE(warnings.size(), 0);
    }

    void longDialogKeyboardAndThemes()
    {
        QFETCH(QString, style); QFETCH(bool, dark);
        EvidenceBackend backend;
        mr::EvidenceSubmissionController controller(&backend);
        QString copied; int opened = 0;
        controller.setClipboardWriter([&](const QString &text) { copied = text; });
        controller.setUrlOpener([&](const QUrl &) { ++opened; return true; });
        EvidenceTheme app;
        app.dark = dark;
        QQmlEngine engine;
        engine.rootContext()->setContextProperty("App", &app);
        engine.rootContext()->setContextProperty("Evidence", &controller);
        engine.rootContext()->setContextProperty("ForceUiStyle", style);
        engine.rootContext()->setContextProperty("ReduceMotion", true);
        QSignalSpy warnings(&engine, &QQmlEngine::warnings);
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 1100; height: 720; visible: true; color: Theme.contentBackground
    CalibrationEvidenceDialog { objectName: "dialog"; evidenceController: Evidence }
})", QUrl());
        std::unique_ptr<QObject> root(component.create());
        QVERIFY2(root != nullptr, qPrintable(component.errorString()));
        auto *window = qobject_cast<QQuickWindow *>(root.get());
        QVERIFY(window);
        QString json = jsonFor();
        json += QString(qsizetype{48} * 1024 - json.toUtf8().size(), '\n');
        controller.previewCalibration();
        backend.calls.last().reply->succeed(answer(json, "CODE_VERIFICATION", false));
        auto *dialog = root->findChild<QObject *>("dialog");
        QVERIFY(dialog);
        QTRY_VERIFY(dialog->property("opened").toBool());
        QVERIFY(QTest::qWaitForWindowExposed(window));
        auto *cancel = visual(window->contentItem(), "evidenceCancelButton");
        auto *submit = visual(window->contentItem(), "evidenceCopyOpenButton");
        auto *scroll = visual(window->contentItem(), "evidenceScroll");
        auto *text = visual(window->contentItem(), "evidenceJsonText");
        QVERIFY(cancel && submit && scroll && text);
        QTRY_VERIFY(cancel->hasActiveFocus());
        QTest::keyClick(window, Qt::Key_Tab);
        QTRY_VERIFY(text->hasActiveFocus());
        QTest::keyClick(window, Qt::Key_Backtab);
        QTRY_VERIFY(cancel->hasActiveFocus());
        QTest::keyClick(window, Qt::Key_Backtab);
        QTRY_VERIFY(submit->hasActiveFocus());
        QCOMPARE(text->property("text").toString(), json);
        QVERIFY(scroll->property("contentHeight").toReal() > scroll->height());
        const auto bottom = submit->mapToScene(QPointF(submit->width(), submit->height()));
        QVERIFY(bottom.x() < window->width()); QVERIFY(bottom.y() < window->height());
        QVERIFY(submit->isVisible());
        const QString evidenceDir = qEnvironmentVariable("MR_CALIBRATION_EVIDENCE_DIR");
        if (!evidenceDir.isEmpty()) {
            QVERIFY(QDir().mkpath(evidenceDir));
            QVERIFY(window->grabWindow().save(evidenceDir + "/preview-" + style + (dark ? "-dark.png" : "-light.png")));
        }
        QTest::keyClick(window, Qt::Key_Space);
        QCOMPARE(copied, json); QCOMPARE(opened, 1);
        QTest::keyClick(window, Qt::Key_Escape);
        QTRY_VERIFY(!controller.visible());
        QCOMPARE(opened, 1);
        QCOMPARE(warnings.size(), 0);
    }
};

int main(int argc, char **argv)
{
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QGuiApplication app(argc, argv);
    QQuickStyle::setStyle(QStringLiteral("Basic"));
#ifdef Q_OS_WIN
    const auto windowsDir = qEnvironmentVariable("WINDIR", QStringLiteral("C:/Windows"));
    const int fontId = QFontDatabase::addApplicationFont(QDir(windowsDir).filePath(QStringLiteral("Fonts/msyh.ttc")));
    const auto families = QFontDatabase::applicationFontFamilies(fontId);
    if (!families.isEmpty()) { QFont font; font.setFamilies(families); QGuiApplication::setFont(font); }
    QFontDatabase::addApplicationFont(QDir(windowsDir).filePath(QStringLiteral("Fonts/consola.ttf")));
#endif
    EvidenceSubmissionTests tests;
    return QTest::qExec(&tests, argc, argv);
}
#include "EvidenceSubmissionTests.moc"
