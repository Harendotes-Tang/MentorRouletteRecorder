#include "TestCollectorGuard.h"
#include "AppController.h"
#include "CandidateReviewController.h"
#include "CaptureValidationController.h"
#include "IBackend.h"
#include "MockBackend.h"
#include "TtsService.h"

#include <QDateTime>
#include <QDir>
#include <QFile>
#include <QFontDatabase>
#include <QGuiApplication>
#include <QImage>
#include <QJsonArray>
#include <QJsonDocument>
#include <QPointer>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QSet>
#include <QSignalSpy>
#include <QStandardPaths>
#include <QTest>
#include <QTimer>
#include <algorithm>
#include <memory>

namespace {
class TestBackend final : public mr::IBackend
{
public:
    struct Call { QString type; QJsonObject payload; QPointer<mr::BackendReply> reply; };
    QList<Call> calls;
    QJsonArray rows;
    bool automatic = true;
    bool inconsistentIndex = false;
    QString backendName() const override { return QStringLiteral("mock"); }
    bool isConnected() const override { return true; }
    mr::BackendReply *request(const QString &type, const QJsonObject &payload = {}) override
    {
        auto *reply = new mr::BackendReply(QString::number(calls.size()), type, this);
        calls.append({type, payload, reply});
        if (automatic) QTimer::singleShot(0, reply, [this, reply, type, payload] {
            if (type == QLatin1String("QueryCandidateObservations")) reply->succeed(query(payload));
            else if (type == QLatin1String("GetCaptureSettings"))
                reply->succeed({{QStringLiteral("candidate_validation_enabled"), false},
                                {QStringLiteral("research_payload_opcodes"), QJsonArray{}}});
            else reply->succeed({});
        });
        return reply;
    }
    QJsonObject query(const QJsonObject &payload) const
    {
        QJsonArray selected;
        const auto session = payload.value(QStringLiteral("session_id")).toString();
        for (const auto &value : rows) {
            const auto row = value.toObject();
            if (session.isEmpty() || row.value(QStringLiteral("capture_session_id")).toString() == session)
                selected.append(row);
        }
        const int page = payload.value(QStringLiteral("page")).toInt(1);
        const int size = payload.value(QStringLiteral("page_size")).toInt(50);
        QJsonArray items;
        for (int index = (page - 1) * size; index < qMin(page * size, int(selected.size())); ++index)
            items.append(selected[index]);
        const int total = int(selected.size()) + (inconsistentIndex && size == 200 && page > 1 ? 1 : 0);
        return {{QStringLiteral("items"), items},
                {QStringLiteral("page_info"), QJsonObject{{QStringLiteral("page"), page},
                    {QStringLiteral("page_size"), size}, {QStringLiteral("total"), total}}}};
    }
    int count(const char *type) const
    {
        return std::count_if(calls.begin(), calls.end(), [type](const Call &call) { return call.type == QLatin1String(type); });
    }
    void event() { Q_EMIT liveEvent({{QStringLiteral("kind"), QStringLiteral("candidate_observed")},
                                   {QStringLiteral("event_type"), QStringLiteral("CandidateObserved")}}); }
};

class CaptureHost final : public mr::CaptureValidationController::Host
{
public:
    bool candidate = true;
    bool running = false;
    bool formalCapturing() const override { return running; }
    bool candidateValidationEnabled() const override { return candidate; }
    QString captureProfileStatus() const override { return QStringLiteral("UNSUPPORTED_BUILD"); }
    void applyFormalCaptureStatus(const QVariantMap &status) override
    { running = status.value(QStringLiteral("state")).toString() == QLatin1String("RUNNING"); }
    void refreshStatus() override {}
    void showToast(const QString &) override {}
};

QJsonObject row(int id, const QString &session = QStringLiteral("session-a"),
                const QString &connection = QStringLiteral("connection-a"), bool anchor = false)
{
    return {{QStringLiteral("observation_id"), QString::number(id)},
            {QStringLiteral("capture_session_id"), session},
            {QStringLiteral("connection_tag"), connection},
            {QStringLiteral("t_ms"), 20000 - id},
            {QStringLiteral("observed_at_utc"), QDateTime::fromString(QStringLiteral("2026-09-04T00:00:00.000Z"), Qt::ISODateWithMs)
                .addSecs(-id).toString(Qt::ISODateWithMs)},
            {QStringLiteral("hypothesis_name"), anchor ? QStringLiteral("ZONE_LOAD") : QStringLiteral("QUEUE_ACK_B0")},
            {QStringLiteral("direction"), anchor ? QStringLiteral("NONE") : QStringLiteral("S2C")},
            {QStringLiteral("review_note"), QStringLiteral("原备注")},
            {QStringLiteral("review_verdict"), QJsonValue::Null}};
}

// One event row at a capture time, for timeline tests.
QJsonObject eventAt(int id, const QString &name, qint64 tMs, const QString &connection = QStringLiteral("c1"))
{
    auto value = row(id, QStringLiteral("s1"), connection, name == QLatin1String("ZONE_LOAD"));
    value[QStringLiteral("hypothesis_name")] = name;
    value[QStringLiteral("direction")] = name == QLatin1String("ZONE_LOAD") ? QStringLiteral("NONE")
        : name == QLatin1String("FINDER_STATE_NOTIFICATION") ? QStringLiteral("S2C") : QStringLiteral("C2S");
    value[QStringLiteral("t_ms")] = tMs;
    value[QStringLiteral("observed_at_utc")] = QDateTime::fromString(QStringLiteral("2026-09-06T01:49:00.000Z"), Qt::ISODateWithMs)
        .addMSecs(tMs).toString(Qt::ISODateWithMs);
    return value;
}

QStringList kinds(const QVariantList &timeline)
{
    QStringList result;
    for (const auto &value : timeline) result.append(value.toMap().value(QStringLiteral("kind")).toString());
    return result;
}

QJsonObject completedMockPayload(mr::BackendReply *reply)
{
    QSignalSpy done(reply, &mr::BackendReply::done);
    if (!done.wait(1000) || !done.first()[0].toBool()) {
        QTest::qFail("Mock snapshot did not complete successfully", __FILE__, __LINE__);
        return {};
    }
    return QJsonObject::fromVariantMap(done.first()[1].toMap());
}

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
} // namespace

class CandidateReviewTests : public QObject
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

    void mockResearchWhitelistMatchesCurrentCandidateDeclarations()
    {
        QFile profileFile(QString::fromUtf8(MR_SOURCE_DIR)
                          + QStringLiteral("/protocol-profiles/cn/cn.2026.08.05.candidate.json"));
        QVERIFY(profileFile.open(QIODevice::ReadOnly));
        QJsonParseError parseError;
        const auto profile = QJsonDocument::fromJson(profileFile.readAll(), &parseError).object();
        QCOMPARE(parseError.error, QJsonParseError::NoError);
        QCOMPARE(profile.value(QStringLiteral("compatibility_status")).toString(), QStringLiteral("CANDIDATE"));
        const auto declarations = profile.value(QStringLiteral("hypotheses")).toArray();
        QVERIFY(!declarations.isEmpty());

        mr::MockBackend backend;
        backend.setCandidateFixture();
        const auto presented = completedMockPayload(backend.getCaptureStatus())
                                   .value(QStringLiteral("candidate_hypotheses")).toArray();
        QCOMPARE(presented.size(), declarations.size());
        QSet<QString> seen;
        QSet<int> eligibleExpandedLengths;
        int rejected = 0;
        for (const auto &value : declarations) {
            const auto declared = value.toObject();
            const QString name = declared.value(QStringLiteral("name")).toString();
            const QString token = QStringLiteral("0x%1")
                .arg(declared.value(QStringLiteral("opcode")).toInt(), 4, 16, QLatin1Char('0'));
            const auto it = std::find_if(presented.begin(), presented.end(), [&name](const QJsonValue &item) {
                return item.toObject().value(QStringLiteral("name")).toString() == name;
            });
            QVERIFY2(it != presented.end(), qPrintable(name));
            QVERIFY(!seen.contains(token));
            seen.insert(token);
            const auto shown = it->toObject();
            for (const char *key : {"name", "label", "group", "expected_length"})
                QCOMPARE(shown.value(QLatin1String(key)), declared.value(QLatin1String(key)));
            QCOMPARE(shown.value(QStringLiteral("opcode")).toString(), token);
            QCOMPARE(shown.value(QStringLiteral("profile_id")), profile.value(QStringLiteral("profile_id")));
            QCOMPARE(shown.value(QStringLiteral("direction")).toString(),
                     declared.value(QStringLiteral("direction")).toString() == QLatin1String("CLIENT_TO_SERVER")
                         ? QStringLiteral("C2S") : QStringLiteral("S2C"));

            // The checked-in candidate declares fixed, unobfuscated lengths. Driving
            // eligibility from the real lengths keeps new entries from drifting away
            // from the catalogue the production QML displays.
            QVERIFY(declared.value(QStringLiteral("expected_length")).isDouble());
            const int length = declared.value(QStringLiteral("expected_length")).toInt(-1);
            const bool eligible = length >= 0 && length <= 512;
            QCOMPARE(shown.value(QStringLiteral("research_eligible")).toBool(), eligible);
            const auto before = completedMockPayload(backend.getCaptureSettings());
            QSignalSpy done(backend.updateCaptureSettings({
                {QStringLiteral("research_payload_opcodes"), QJsonArray{token.toUpper()}}}),
                &mr::BackendReply::done);
            QVERIFY(done.wait(1000));
            QCOMPARE(done.first()[0].toBool(), eligible);
            if (eligible) {
                QCOMPARE(completedMockPayload(backend.getCaptureSettings()).value(QStringLiteral("research_payload_opcodes")).toArray(),
                         QJsonArray{token});
                if (length > 256) eligibleExpandedLengths.insert(length);
            } else {
                ++rejected;
                QCOMPARE(done.first()[2].toString(), QStringLiteral("ERR_BAD_REQUEST"));
                QVERIFY(done.first()[3].toString().contains(QStringLiteral("512")));
                QCOMPARE(completedMockPayload(backend.getCaptureSettings()), before);
            }
        }
        QCOMPARE(eligibleExpandedLengths, (QSet<int>{360, 424, 448, 456}));
        QCOMPARE(rejected, 3);
        QVERIFY(seen.contains(QStringLiteral("0x028d")));
        QVERIFY(seen.contains(QStringLiteral("0x0350")));
    }

    void mockResearchWhitelistRejectsInvalidUpdatesWithoutMutation_data()
    {
        QTest::addColumn<QJsonValue>("requested");
        QTest::newRow("unknown") << QJsonValue(QJsonArray{QStringLiteral("0xffff")});
        QTest::newRow("duplicate-normalized") << QJsonValue(QJsonArray{QStringLiteral("0x014a"), QStringLiteral("0X014A")});
        QTest::newRow("malformed") << QJsonValue(QJsonArray{QStringLiteral("014a")});
        QTest::newRow("non-string") << QJsonValue(QJsonArray{330});
        QTest::newRow("non-array") << QJsonValue(QStringLiteral("0x014a"));
        QJsonArray tooMany;
        for (int i = 0; i < 33; ++i) tooMany.append(QStringLiteral("0x014a"));
        QTest::newRow("too-many") << QJsonValue(tooMany);
    }

    void mockResearchWhitelistRejectsInvalidUpdatesWithoutMutation()
    {
        QFETCH(QJsonValue, requested);
        mr::MockBackend backend;
        backend.setCandidateFixture();
        QSignalSpy seed(backend.updateCaptureSettings({
            {QStringLiteral("research_payload_opcodes"), QJsonArray{QStringLiteral("0x028d")}}}),
            &mr::BackendReply::done);
        QVERIFY(seed.wait(1000));
        QVERIFY(seed.first()[0].toBool());
        const auto before = completedMockPayload(backend.getCaptureSettings());
        QSignalSpy done(backend.updateCaptureSettings({
            {QStringLiteral("research_payload_opcodes"), requested},
            {QStringLiteral("follow_game"), !before.value(QStringLiteral("follow_game")).toBool()}}),
            &mr::BackendReply::done);
        QVERIFY(done.wait(1000));
        QVERIFY(!done.first()[0].toBool());
        QCOMPARE(done.first()[2].toString(), QStringLiteral("ERR_BAD_REQUEST"));
        QCOMPARE(completedMockPayload(backend.getCaptureSettings()), before);
    }

    void mockResearchWhitelistRequiresOptInAndCanClearWhileDisabled()
    {
        mr::MockBackend backend;
        const auto initial = completedMockPayload(backend.getCaptureSettings());
        QVERIFY(!initial.value(QStringLiteral("candidate_validation_enabled")).toBool());
        QVERIFY(initial.value(QStringLiteral("research_payload_opcodes")).toArray().isEmpty());
        QSignalSpy refused(backend.updateCaptureSettings({
            {QStringLiteral("research_payload_opcodes"), QJsonArray{QStringLiteral("0x014a")}}}),
            &mr::BackendReply::done);
        QVERIFY(refused.wait(1000));
        QVERIFY(!refused.first()[0].toBool());
        QCOMPARE(completedMockPayload(backend.getCaptureSettings()), initial);
        QSignalSpy enabled(backend.updateCaptureSettings({
            {QStringLiteral("candidate_validation_enabled"), true},
            {QStringLiteral("research_payload_opcodes"), QJsonArray{QStringLiteral("0x014a")}}}),
            &mr::BackendReply::done);
        QVERIFY(enabled.wait(1000));
        QVERIFY(enabled.first()[0].toBool());
        QSignalSpy disabled(backend.updateCaptureSettings({{QStringLiteral("candidate_validation_enabled"), false}}),
                            &mr::BackendReply::done);
        QVERIFY(disabled.wait(1000));
        QVERIFY(disabled.first()[0].toBool());
        QCOMPARE(completedMockPayload(backend.getCaptureSettings()).value(QStringLiteral("research_payload_opcodes")).toArray(),
                 QJsonArray{QStringLiteral("0x014a")});
        QSignalSpy cleared(backend.updateCaptureSettings({{QStringLiteral("research_payload_opcodes"), QJsonArray{}}}),
                           &mr::BackendReply::done);
        QVERIFY(cleared.wait(1000));
        QVERIFY(cleared.first()[0].toBool());
        const auto finalSettings = completedMockPayload(backend.getCaptureSettings());
        QVERIFY(finalSettings.value(QStringLiteral("research_payload_opcodes")).toArray().isEmpty());
        QVERIFY(!finalSettings.value(QStringLiteral("candidate_validation_enabled")).toBool());
    }

    void productionCandidateCardExplainsAndEnforcesWholePayloadLimit()
    {
        mr::MockBackend backend;
        backend.setCandidateFixture();
        mr::AppController controller(&backend, nullptr);
        QQmlEngine engine;
        engine.rootContext()->setContextProperty(QStringLiteral("App"), &controller);
        engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 1100; height: candidateCard.implicitHeight + 32; visible: true
    color: Theme.surface
    CandidateValidationCard {
        id: candidateCard
        x: 16; y: 16; width: parent.width - 32; height: implicitHeight
    }
})", QUrl());
        std::unique_ptr<QObject> root(component.create());
        QVERIFY2(root != nullptr, qPrintable(component.errorString()));
        QTRY_VERIFY(controller.captureSettingsSupported());
        auto *card = root->findChild<QObject *>(QStringLiteral("candidateValidationCard"));
        QVERIFY(card);
        QTRY_VERIFY(card->property("editable").toBool());
        auto *policy = card->findChild<QObject *>(QStringLiteral("researchPayloadPolicyText"));
        QVERIFY(policy);
        const auto policyText = policy->property("text").toString();
        QVERIFY(policyText.contains(QStringLiteral("完整负载")));
        QVERIFY(policyText.contains(QStringLiteral("512")));
        QVERIFY(policyText.contains(QStringLiteral("超出上限不保存负载")));
        QVERIFY(!policyText.contains(QStringLiteral("前 256")));
        auto *retention = card->findChild<QObject *>(QStringLiteral("researchPayloadRetentionText"));
        QVERIFY(retention);
        QVERIFY(retention->property("text").toString().contains(QStringLiteral("≤512")));
        QVERIFY(retention->property("text").toString().contains(QStringLiteral("专用导出会附带")));
        auto *eligibility = card->findChild<QObject *>(QStringLiteral("researchPayloadEligibilityText"));
        QVERIFY(eligibility);
        QVERIFY(eligibility->property("text").toString().contains(QStringLiteral("长度上限不超过 512")));
        // Repeater delegates belong to the visual scene; their QObject ownership
        // need not descend from the card, so inspect the same tree users see.
        auto *visualCard = qobject_cast<QQuickItem *>(card);
        QVERIFY(visualCard);
        QTRY_VERIFY(findVisualItem(visualCard, QStringLiteral("researchOpcode_0x014a")));
        QVERIFY(findVisualItem(visualCard, QStringLiteral("researchOpcode_0x014a"))->isVisible());
        QVERIFY(!findVisualItem(visualCard, QStringLiteral("researchOpcode_0x0077")));

        QSignalSpy applied(controller.candidates(), &mr::CandidateReviewController::captureSettingsApplied);
        QVERIFY(QMetaObject::invokeMethod(card, "selectRecommended"));
        QTRY_COMPARE(applied.size(), 1);
        const QStringList recommended{QStringLiteral("0x00b0"), QStringLiteral("0x0104"),
            QStringLiteral("0x020b"), QStringLiteral("0x028d"), QStringLiteral("0x0323"),
            QStringLiteral("0x034b"), QStringLiteral("0x0350"), QStringLiteral("0x03bb")};
        QCOMPARE(controller.captureSettings().value(QStringLiteral("research_payload_opcodes")).toStringList(), recommended);
        QVERIFY(QMetaObject::invokeMethod(card, "toggleOpcode", Q_ARG(QVariant, QVariant(QStringLiteral("0x014a")))));
        QTRY_COMPARE(applied.size(), 2);
        const auto selected = controller.captureSettings().value(QStringLiteral("research_payload_opcodes"));
        QVERIFY(selected.toStringList().contains(QStringLiteral("0x014a")));

        QVERIFY(card->setProperty("advanced", true));
        auto *input = card->findChild<QObject *>(QStringLiteral("researchOpcodeInput"));
        QVERIFY(input);
        QVERIFY(input->setProperty("text", QStringLiteral("0x0077")));
        QVERIFY(QMetaObject::invokeMethod(card, "addOpcode"));
        QTRY_VERIFY(!controller.candidates()->candidateSettingsError().isEmpty());
        QVERIFY(controller.candidates()->candidateSettingsError().contains(QStringLiteral("512")));
        QCOMPARE(applied.size(), 2);
        QCOMPARE(controller.captureSettings().value(QStringLiteral("research_payload_opcodes")), selected);
        QCOMPARE(input->property("text").toString(), QStringLiteral("0x0077"));

        // Optional evidence uses this production QML and explicitly synthetic Mock;
        // it does not stand in for a real Collector capture or storage test.
        const QString screenshotDir = qEnvironmentVariable("MR_CANDIDATE_POLICY_SCREENSHOT_DIR");
        if (!screenshotDir.isEmpty()) {
            QVERIFY(QDir().mkpath(screenshotDir));
            auto *window = qobject_cast<QQuickWindow *>(root.get());
            QVERIFY(window);
            QTest::qWait(150);
            const auto frame = window->grabWindow();
            QVERIFY(!frame.isNull());
            QVERIFY(frame.save(QDir(screenshotDir).filePath(QStringLiteral("candidate-policy-512-synthetic.png"))));
        }
    }

    void candidateButtonStartsPassiveLedgerAndNeverFallsBackToLegacyTrace()
    {
        TestBackend backend;
        backend.automatic = false;
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
        host.candidate = false; // A changed setting cannot change the in-flight command's meaning.
        backend.calls.last().reply->fail(QStringLiteral("ERR_PROFILE_UNSUPPORTED"), QStringLiteral("候选启动失败"));
        QCOMPARE(backend.count("StartCaptureValidation"), 0);
        QCOMPARE(controller.error(), QStringLiteral("候选启动失败"));
        QVERIFY(!controller.commandBusy());
        host.candidate = true;
        controller.toggle();
        backend.calls.last().reply->succeed({{QStringLiteral("state"), QStringLiteral("RUNNING")}});
        QCOMPARE(controller.actionLabel(), QStringLiteral("停止捕获"));
        QCOMPARE(controller.modeStatusText(), QStringLiteral("候选观测中；VERIFIED 解析可独立生成正式记录"));
        QCOMPARE(controller.modeCompactText(), QStringLiteral("候选观测中"));
        host.candidate = false;
        QCOMPARE(controller.modeStatusText(), QStringLiteral("被动监听（不自动记录）"));
        QCOMPARE(controller.modeCompactText(), QStringLiteral("被动监听中"));
        controller.toggle();
        QCOMPARE(backend.calls.last().type, QStringLiteral("StopCapture"));
    }

    void activeLegacyTraceRetainsItsStopPrecedence()
    {
        TestBackend backend;
        backend.automatic = false;
        CaptureHost host;
        host.running = true;
        mr::CaptureValidationController controller(&host);
        controller.setBackend(&backend);
        controller.refreshStatusSnapshot();
        backend.calls.last().reply->succeed({{QStringLiteral("state"), QStringLiteral("RECORDING")}});
        controller.toggle();
        QCOMPARE(backend.calls.last().type, QStringLiteral("StopCaptureValidation"));
        QCOMPARE(backend.count("StopCapture"), 0);
    }

    void delayedOrdinarySettingsReplyPreservesNewCandidateConsent()
    {
        TestBackend backend;
        mr::AppController controller(&backend, nullptr);
        QTRY_VERIFY(controller.captureSettingsSupported());
        QTest::qWait(100);
        backend.automatic = false;
        backend.calls.clear();
        controller.updateCaptureSetting(QStringLiteral("autostart"), true);
        QTRY_COMPARE(backend.count("UpdateCaptureSettings"), 1);
        const auto ordinary = backend.calls.last().reply;
        controller.candidates()->setCandidateEnabled(true);
        const auto candidate = backend.calls.last().reply;
        candidate->succeed({{QStringLiteral("candidate_validation_enabled"), true},
                            {QStringLiteral("research_payload_opcodes"), QJsonArray{QStringLiteral("0x03bb")}}});
        ordinary->succeed({{QStringLiteral("autostart"), true},
                           {QStringLiteral("candidate_validation_enabled"), false},
                           {QStringLiteral("research_payload_opcodes"), QJsonArray{}}});
        QVERIFY(controller.captureSettings().value(QStringLiteral("candidate_validation_enabled")).toBool());
        QCOMPARE(controller.captureSettings().value(QStringLiteral("research_payload_opcodes")).toStringList(),
                 QStringList{QStringLiteral("0x03bb")});
    }

    void traversesTheFullLedgerAndPairsAcrossPageBoundaries()
    {
        TestBackend backend;
        for (int i = 0; i < 20000; ++i) backend.rows.append(row(i, QStringLiteral("session-a"), QStringLiteral("connection-a"), i == 0 || i == 19999));
        mr::CandidateReviewController controller;
        controller.setBackend(&backend);
        controller.refresh();
        QTRY_VERIFY_WITH_TIMEOUT(controller.indexComplete(), 10000);
        QCOMPARE(controller.indexLoadedCount(), 20000);
        QCOMPARE(controller.rows().size(), 50);
        QCOMPARE(controller.total(), 20000);
        // Two zone bursts on one connection with no pop between them: the earlier one
        // (id 19999, smallest t_ms) is the login, the later one is a plain zone change.
        // Nothing is paired, because nothing says it was a duty.
        QVERIFY(controller.anchorPairs().isEmpty());
        QCOMPARE(kinds(controller.timeline()), QStringList({"zone", "login"}));
        QCOMPARE(controller.timeline().last().toMap().value(QStringLiteral("observation_id")).toString(), QStringLiteral("19999"));
        QCOMPARE(controller.timeline().first().toMap().value(QStringLiteral("observation_id")).toString(), QStringLiteral("0"));
        controller.loadPage(400);
        QTRY_VERIFY(!controller.loading());
        QCOMPARE(controller.page(), 400);
        QCOMPARE(controller.rows().last().toMap().value(QStringLiteral("observation_id")).toString(), QStringLiteral("19999"));
        for (const auto &call : backend.calls)
            if (call.type == QLatin1String("QueryCandidateObservations")) QVERIFY(call.payload.value(QStringLiteral("page_size")).toInt() <= 200);
    }

    void separatesConnectionsAndSessionsAndTreatsEachFirstBurstAsLogin()
    {
        TestBackend backend;
        backend.rows = {row(0, "s1", "c1", true), row(1, "s1", "c2", true),
                        row(2, "s2", "c1", true), row(3, "s1", "c1", true)};
        mr::CandidateReviewController controller;
        controller.setBackend(&backend);
        controller.refresh();
        QTRY_VERIFY(controller.indexComplete());
        QCOMPARE(controller.sessions().size(), 2);
        QVERIFY(controller.anchorPairs().isEmpty());
        // Three connections, three logins; s1/c1 has a second burst (id 0, later t_ms).
        QCOMPARE(controller.timeline().size(), 4);
        int logins = 0;
        for (const auto &value : controller.timeline())
            if (value.toMap().value(QStringLiteral("kind")).toString() == QLatin1String("login")) ++logins;
        QCOMPARE(logins, 3);
        controller.setSessionId(QStringLiteral("s2"));
        QTRY_VERIFY(!controller.loading());
        QCOMPARE(controller.total(), 1);
        QCOMPARE(kinds(controller.timeline()), QStringList({"login"}));
    }

    void pairsDutyEntryAndExitOnlyAfterAFinderPop()
    {
        // The live session of 2026-09-06: login 01:49:18, queue, pop burst, duty entry
        // 01:52:27, duty exit 02:01:47 -- all on one connection. Then a teleport.
        TestBackend backend;
        backend.rows = {eventAt(0, "ZONE_LOAD", 18000), eventAt(1, "QUEUE_REGISTRATION", 60000),
                        eventAt(2, "FINDER_STATE_NOTIFICATION", 180000), eventAt(3, "FINDER_STATE_NOTIFICATION", 180400),
                        eventAt(4, "ZONE_LOAD", 207000), eventAt(5, "ZONE_LOAD", 767000),
                        eventAt(6, "ZONE_LOAD", 900000)};
        mr::CandidateReviewController controller;
        controller.setBackend(&backend);
        controller.refresh();
        QTRY_VERIFY(controller.indexComplete());
        // Newest first.
        QCOMPARE(kinds(controller.timeline()), QStringList({"zone", "duty_leave", "duty_enter", "finder", "queue", "login"}));
        const auto finder = controller.timeline()[3].toMap();
        QCOMPARE(finder.value(QStringLiteral("count")).toInt(), 2);
        const auto entry = controller.timeline()[2].toMap();
        const auto leave = controller.timeline()[1].toMap();
        QCOMPARE(entry.value(QStringLiteral("pair_at_utc")).toString(), leave.value(QStringLiteral("at_utc")).toString());
        QCOMPARE(controller.anchorPairs().size(), 1);
        const auto pair = controller.anchorPairs().first().toMap();
        QVERIFY(pair.value(QStringLiteral("complete")).toBool());
        QCOMPARE(pair.value(QStringLiteral("first_observation_id")).toString(), QStringLiteral("4"));
        QCOMPARE(pair.value(QStringLiteral("last_observation_id")).toString(), QStringLiteral("5"));

        // An action with no state update after it never produces an entry (the burst right
        // after it is a plain zone change); a state update older than three minutes is stale.
        backend.rows = {eventAt(0, "ZONE_LOAD", 1000), eventAt(1, "QUEUE_REGISTRATION", 2000),
                        eventAt(2, "FINDER_ACTION", 3000), eventAt(3, "ZONE_LOAD", 4000),
                        eventAt(4, "FINDER_STATE_NOTIFICATION", 5000), eventAt(5, "ZONE_LOAD", 5000 + 4 * 60 * 1000)};
        controller.refresh();
        QTRY_VERIFY(!controller.indexLoading());
        QTRY_VERIFY(controller.indexComplete());
        QCOMPARE(kinds(controller.timeline()), QStringList({"zone", "finder", "zone", "finder_action", "queue", "login"}));
        QVERIFY(controller.anchorPairs().isEmpty());
    }

    void rejectsChangingIndexAfterOneRetry()
    {
        TestBackend backend;
        backend.inconsistentIndex = true;
        for (int i = 0; i < 260; ++i) backend.rows.append(row(i));
        mr::CandidateReviewController controller;
        controller.setBackend(&backend);
        controller.refresh();
        QTRY_VERIFY(!controller.indexLoading());
        QVERIFY(!controller.indexComplete());
        QVERIFY(controller.anchorPairs().isEmpty());
        QVERIFY(controller.indexMessage().contains(QStringLiteral("刷新")));
        QCOMPARE(backend.count("QueryCandidateObservations"), 5); // display + two index attempts.
    }

    void wallClockRollbackDoesNotReorderMonotonicAnchors()
    {
        TestBackend backend;
        for (int i = 0; i < 4; ++i) {
            auto value = row(i, "s1", "c1", true);
            value[QStringLiteral("t_ms")] = i + 1;
            value[QStringLiteral("observed_at_utc")] = QStringList{
                "2026-09-04T10:00:00.000Z", "2026-09-04T10:01:00.000Z",
                "2026-09-04T09:59:00.000Z", "2026-09-04T10:02:00.000Z"}[i];
            backend.rows.append(value);
        }
        mr::CandidateReviewController controller;
        controller.setBackend(&backend);
        controller.refresh();
        QTRY_VERIFY(controller.indexComplete());
        // Capture order (t_ms 1..4) wins over the corrected wall clock: id 0 is the login,
        // the rest follow in monotonic order, newest first.
        QStringList ids;
        for (const auto &value : controller.timeline()) ids.append(value.toMap().value("observation_id").toString());
        QCOMPARE(ids, QStringList({"3", "2", "1", "0"}));
        QCOMPARE(kinds(controller.timeline()), QStringList({"zone", "zone", "zone", "login"}));
    }

    void expiredSelectedSessionReturnsToTheUnfilteredFirstPage()
    {
        TestBackend backend;
        backend.rows = {row(0, "old"), row(1, "current")};
        mr::CandidateReviewController controller;
        controller.setBackend(&backend);
        controller.setSessionId(QStringLiteral("old"));
        controller.refresh();
        QTRY_VERIFY(controller.indexComplete());
        backend.rows = {row(1, "current")};
        controller.refresh();
        QTRY_VERIFY(controller.indexComplete() && !controller.loading());
        QVERIFY(controller.sessionId().isEmpty());
        QCOMPARE(controller.page(), 1);
        QCOMPARE(controller.total(), 1);
        QCOMPARE(controller.rows().first().toMap().value("capture_session_id").toString(), QStringLiteral("current"));
    }

    void rejectsDuplicateRowsDespiteMatchingTotal()
    {
        TestBackend backend;
        backend.rows = {row(0), row(0)};
        mr::CandidateReviewController controller;
        controller.setBackend(&backend);
        controller.refresh();
        QTRY_VERIFY(!controller.indexLoading());
        QVERIFY(!controller.indexComplete());
        QVERIFY(controller.anchorPairs().isEmpty());
        QCOMPARE(backend.count("QueryCandidateObservations"), 3);
    }

    void ignoresAReplyFromThePreviousSessionSelection()
    {
        TestBackend backend;
        backend.automatic = false;
        backend.rows = {row(0, "s1"), row(1, "s2")};
        mr::CandidateReviewController controller;
        controller.setBackend(&backend);
        controller.setSessionId(QStringLiteral("s1"));
        controller.setSessionId(QStringLiteral("s2"));
        backend.calls[1].reply->succeed(backend.query(backend.calls[1].payload));
        backend.calls[0].reply->succeed(backend.query(backend.calls[0].payload));
        QCOMPARE(controller.sessionId(), QStringLiteral("s2"));
        QCOMPARE(controller.rows().first().toMap().value(QStringLiteral("capture_session_id")).toString(), QStringLiteral("s2"));
    }

    void settingsAreSingleFlightAndFailureNeverPublishesAnOptimisticValue()
    {
        TestBackend backend;
        backend.automatic = false;
        mr::CandidateReviewController controller;
        controller.setBackend(&backend);
        QSignalSpy applied(&controller, &mr::CandidateReviewController::captureSettingsApplied);
        controller.setCandidateEnabled(true);
        QVERIFY(controller.candidateSettingsBusy());
        controller.setResearchOpcodes({QStringLiteral("0x03bb")});
        QCOMPARE(backend.count("UpdateCaptureSettings"), 1);
        backend.calls[0].reply->fail(QStringLiteral("ERR_BAD_REQUEST"), QStringLiteral("需先停止捕获"));
        QVERIFY(!controller.candidateSettingsBusy());
        QCOMPARE(applied.size(), 0);
        QCOMPARE(controller.candidateSettingsError(), QStringLiteral("需先停止捕获"));
        controller.setCandidateEnabled(true);
        backend.calls[1].reply->succeed({{QStringLiteral("candidate_validation_enabled"), true},
                                       {QStringLiteral("research_payload_opcodes"), QJsonArray{}}});
        QCOMPARE(applied.size(), 1);
        QVERIFY(controller.candidateSettingsError().isEmpty());
        controller.setResearchOpcodes({});
        QVERIFY(backend.calls.last().payload.value(QStringLiteral("research_payload_opcodes")).toArray().isEmpty());
    }

    void reviewChangesOnlyTheAcceptedRowAndKeepsDraftOnFailure()
    {
        TestBackend backend;
        backend.automatic = false;
        backend.rows = {row(0)};
        mr::CandidateReviewController controller;
        controller.setBackend(&backend);
        controller.loadPage(1);
        backend.calls[0].reply->succeed(backend.query(backend.calls[0].payload));
        QSignalSpy finished(&controller, &mr::CandidateReviewController::reviewFinished);
        controller.review(QStringLiteral("0"), QStringLiteral("CORRECT"), QStringLiteral("新备注"));
        controller.review(QStringLiteral("0"), QStringLiteral("WRONG"), QStringLiteral("重复请求"));
        QCOMPARE(backend.count("ReviewCandidateObservation"), 1);
        backend.calls[1].reply->fail(QStringLiteral("ERR_DB_BUSY"), QStringLiteral("稍后重试"));
        QCOMPARE(controller.rows().first().toMap().value(QStringLiteral("review_note")).toString(), QStringLiteral("原备注"));
        QCOMPARE(finished.last()[1].toBool(), false);
        controller.review(QStringLiteral("0"), QStringLiteral("CORRECT"), QStringLiteral("新备注"));
        backend.calls[2].reply->succeed({{QStringLiteral("observation_id"), QStringLiteral("0")},
            {QStringLiteral("review_verdict"), QStringLiteral("CORRECT")},
            {QStringLiteral("reviewed_at_utc"), QStringLiteral("2026-09-05T00:00:00.000Z")}});
        QCOMPARE(controller.rows().first().toMap().value(QStringLiteral("review_note")).toString(), QStringLiteral("新备注"));
        QCOMPARE(controller.rows().first().toMap().value(QStringLiteral("review_verdict")).toString(), QStringLiteral("CORRECT"));
        QCOMPARE(finished.last()[1].toBool(), true);
    }

    void candidateLiveEventDoesNotRefreshFormalViewsOrAnnounce()
    {
        TestBackend backend;
        mr::AppController controller(&backend, nullptr);
        QTest::qWait(100);
        backend.calls.clear();
        QSignalSpy currentRun(&controller, &mr::AppController::currentRunChanged);
        QSignalSpy dashboard(&controller, &mr::AppController::dashboardChanged);
        QSignalSpy reflection(&controller, &mr::AppController::reflectionsChanged);
        QSignalSpy speech(controller.tts(), &mr::TtsService::spoke);
        backend.event();
        QTRY_COMPARE_WITH_TIMEOUT(backend.count("GetCaptureStatus"), 1, 2000);
        QVERIFY(controller.candidates()->hasNewObservations());
        for (const char *type : {"QueryRuns", "GetDashboardStats", "GetDungeonStats", "GetJobStats", "GetReflectionSummary", "GetCurrentRun"})
            QCOMPARE(backend.count(type), 0);
        QCOMPARE(currentRun.size(), 0);
        QCOMPARE(dashboard.size(), 0);
        QCOMPARE(reflection.size(), 0);
        QCOMPARE(speech.size(), 0);
    }

    void mockProvidesIndependentMultiPageCandidateData()
    {
        mr::MockBackend backend;
        backend.setCandidateFixture();
        mr::CandidateReviewController controller;
        controller.setBackend(&backend);
        controller.refresh();
        QTRY_VERIFY(controller.indexComplete());
        QVERIFY(controller.total() > 200);
        QCOMPARE(controller.sessions().size(), 2);
        QVERIFY(!controller.anchorPairs().isEmpty());
        for (const auto &value : controller.rows()) QVERIFY(!value.toMap().contains(QStringLiteral("payload_hex")));
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
    if (qEnvironmentVariableIsSet("MR_CANDIDATE_POLICY_SCREENSHOT_DIR")) {
        const auto path = QDir(qEnvironmentVariable("WINDIR", QStringLiteral("C:/Windows")))
                              .filePath(QStringLiteral("Fonts/msyh.ttc"));
        const int fontId = QFontDatabase::addApplicationFont(path);
        auto families = QFontDatabase::applicationFontFamilies(fontId);
        if (fontId < 0 || families.isEmpty())
            return 6;
        const auto symbolPath = QDir(qEnvironmentVariable("WINDIR", QStringLiteral("C:/Windows")))
                                    .filePath(QStringLiteral("Fonts/seguisym.ttf"));
        const int symbolId = QFontDatabase::addApplicationFont(symbolPath);
        families.append(QFontDatabase::applicationFontFamilies(symbolId));
        QFont font;
        font.setFamilies(families);
        app.setFont(font);
    }
#endif
    CandidateReviewTests tests;
    return QTest::qExec(&tests, argc, argv);
}
#include "CandidateReviewTests.moc"
