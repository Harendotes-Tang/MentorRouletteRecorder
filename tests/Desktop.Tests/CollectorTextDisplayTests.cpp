#include "TestCollectorGuard.h"
#include "AppController.h"
#include "AppSettings.h"
#include "CalibrationController.h"
#include "CollectorText.h"
#include "CollectorTextTestSupport.h"
#include "GameNames.h"
#include "IBackend.h"
#include "LanguageManager.h"
#include "MockBackend.h"
#include "SpeechController.h"
#include "UpdateController.h"

#include <QGuiApplication>
#include <QJsonArray>
#include <QJsonObject>
#include <QSettings>
#include <QSignalSpy>
#include <QStandardPaths>
#include <QTest>

// The Collector's sentences where the Desktop shows them (contracts/collector-texts.md
// §3): rendered from their keys in the interface language, the Collector's Chinese when
// there is no key or one this build does not know, and rendered again - with the
// owner's NOTIFY - after a language switch. "Another language" is MarkingTranslator
// (CollectorTextTestSupport.h): the library these tests link carries no catalogue.
namespace {

QString chineseOf(const QVariantMap &ref)
{
    return mr::collector_text::chinese(ref);
}

QJsonObject json(const QVariantMap &ref)
{
    return QJsonObject::fromVariantMap(ref);
}

/// Answers GetSpeechSettings with \ref speechSettings and refuses everything else
/// with \ref refusalCode / \ref refusalMessage / \ref refusalRef, synchronously.
class RefusingBackend final : public mr::IBackend
{
public:
    QString backendName() const override { return QStringLiteral("mock"); }
    bool isConnected() const override { return true; }

    mr::BackendReply *request(const QString &type, const QJsonObject &payload = {}) override
    {
        Q_UNUSED(payload)
        auto *reply = new mr::BackendReply(QString::number(++m_count), type, this);
        if (type == QLatin1String("GetSpeechSettings"))
            reply->succeed(speechSettings);
        else
            reply->fail(refusalCode, refusalMessage, {}, refusalRef);
        return reply;
    }

    QJsonObject speechSettings;
    QString refusalCode = QStringLiteral("ERR_BAD_REQUEST");
    QString refusalMessage;
    QJsonObject refusalRef;

private:
    int m_count = 0;
};

} // namespace

class CollectorTextDisplayTests : public QObject
{
    Q_OBJECT

private:
    // Built before every controller, so each one connects to its languageChanged().
    std::unique_ptr<mr::AppSettings> m_settings;
    std::unique_ptr<mr::LanguageManager> m_language;

private Q_SLOTS:
    void init()
    {
        QSettings raw(mr::AppSettings::filePath(), QSettings::IniFormat);
        raw.remove(QStringLiteral("ui/language"));
        raw.sync();
        m_settings = std::make_unique<mr::AppSettings>();
        m_language = std::make_unique<mr::LanguageManager>(m_settings.get());
        m_language->initialize();
        QCOMPARE(mr::LanguageManager::instance(), m_language.get());
    }

    void cleanup()
    {
        m_language->setLanguage(QStringLiteral("zh-CN"));
        m_language.reset();
        m_settings.reset();
    }

    // 本机校准: blocker_refs and label_ref on the card's lines and the timeline.
    void calibrationBlockersAndLabelsFollowTheLanguage()
    {
        const QVariantMap finishDuty = mrtest::textRef("calibration.blocker.finish_duty");
        const QVariantMap pop = mrtest::textRef(
            "calibration.event.pop", {{QStringLiteral("roulette"), QString::fromUtf8("练级迷宫")}});
        const QString older = QString::fromUtf8("旧版采集服务的句子。");
        const QString newer = QString::fromUtf8("更新的采集服务的句子。");
        QJsonObject calibration{
            {QStringLiteral("state"), QStringLiteral("BLOCKED")},
            {QStringLiteral("blockers"), QJsonArray{chineseOf(finishDuty), older, newer}},
            // No reference for the second line; a key this build does not know for the third.
            {QStringLiteral("blocker_refs"),
             QJsonArray{json(finishDuty), QJsonValue::Null,
                        QJsonObject{{QStringLiteral("key"), QStringLiteral("calibration.blocker.brand_new")}}}},
            {QStringLiteral("events"),
             QJsonArray{QJsonObject{{QStringLiteral("event_id"), QStringLiteral("pop-1")},
                                    {QStringLiteral("kind"), QStringLiteral("pop")},
                                    {QStringLiteral("at_utc"), QStringLiteral("2026-10-15T12:00:00.000Z")},
                                    {QStringLiteral("t_ms"), 1000},
                                    {QStringLiteral("label"), chineseOf(pop)},
                                    {QStringLiteral("label_ref"), json(pop)},
                                    {QStringLiteral("requires_confirmation"), true}}}}};
        const QVariantMap capture{{QStringLiteral("calibration"), calibration.toVariantMap()}};

        mr::CalibrationController controller;
        controller.refreshFromCaptureStatus(capture);
        QCOMPARE(controller.blockers(), (QStringList{chineseOf(finishDuty), older, newer}));
        QCOMPARE(controller.blockerKeys(),
                 (QStringList{QStringLiteral("calibration.blocker.finish_duty"), QString(),
                              QStringLiteral("calibration.blocker.brand_new")}));
        QCOMPARE(controller.events().constFirst().toMap().value(QStringLiteral("label")).toString(),
                 QString::fromUtf8("匹配弹窗：练级迷宫"));

        QSignalSpy changed(&controller, &mr::CalibrationController::changed);
        const mrtest::MarkedLanguage language;
        m_language->setLanguage(QStringLiteral("en"));
        QVERIFY(changed.count() >= 1);
        QCOMPARE(controller.blockers(),
                 (QStringList{mrtest::marked("calibration.blocker.finish_duty", chineseOf(finishDuty)),
                              older, newer}));
        // The roulette argument reads as the official name in the interface language.
        const QString roulette = mr::GameNames::shared()
                                     ? mr::GameNames::shared()->rouletteByName(QString::fromUtf8("练级迷宫"))
                                     : QString::fromUtf8("练级迷宫");
        QVERIFY(roulette != QString::fromUtf8("练级迷宫"));
        QCOMPARE(controller.events().constFirst().toMap().value(QStringLiteral("label")).toString(),
                 mrtest::marked("calibration.event.pop", QString::fromUtf8("匹配弹窗：") + roulette));
        // The keys read the same in every language: the card recognises a sentence by them.
        QCOMPARE(controller.blockerKeys().constFirst(), QStringLiteral("calibration.blocker.finish_duty"));

        // A refs list of another length is ignored as a whole.
        calibration.insert(QStringLiteral("blocker_refs"), QJsonArray{json(finishDuty)});
        controller.refreshFromCaptureStatus({{QStringLiteral("calibration"), calibration.toVariantMap()}});
        QCOMPARE(controller.blockers(), (QStringList{chineseOf(finishDuty), older, newer}));
        QCOMPARE(controller.blockerKeys(), (QStringList{QString(), QString(), QString()}));
    }

    // A refusal kept for later holds the reply's message_ref, not the text it
    // happened to read as when it arrived.
    void aKeptRefusalFollowsTheLanguage()
    {
        const QVariantMap notReady = mrtest::textRef("calibration.not_ready");
        RefusingBackend backend;
        backend.refusalCode = QStringLiteral("ERR_CALIBRATION_BUSY");
        backend.refusalMessage = chineseOf(notReady);
        backend.refusalRef = json(notReady);
        mr::CalibrationController controller;
        controller.setBackend(&backend);
        controller.refreshFromCaptureStatus(
            {{QStringLiteral("calibration"),
              QVariantMap{{QStringLiteral("state"), QStringLiteral("OBSERVING")}}}});
        controller.discard();
        QTRY_COMPARE(controller.error(), chineseOf(notReady));

        const mrtest::MarkedLanguage language;
        m_language->setLanguage(QStringLiteral("ja"));
        QCOMPARE(controller.error(), mrtest::marked("calibration.not_ready", chineseOf(notReady)));

        // 恢复上一份本机校准 answered with ERR_CALIBRATION_NOT_READY is an answer, kept apart.
        backend.refusalCode = QStringLiteral("ERR_CALIBRATION_NOT_READY");
        controller.restoreLocalProfile();
        QTRY_VERIFY(!controller.restoreAnswer().isEmpty());
        QCOMPARE(controller.restoreAnswer(), mrtest::marked("calibration.not_ready", chineseOf(notReady)));
        m_language->setLanguage(QStringLiteral("zh-TW"));
        QCOMPARE(controller.restoreAnswer(), mrtest::marked("calibration.not_ready", chineseOf(notReady)));

        // Without a reference the Collector's message stands as it came.
        backend.refusalCode = QStringLiteral("ERR_CALIBRATION_BUSY");
        backend.refusalMessage = QString::fromUtf8("采集服务的中文原句。");
        backend.refusalRef = {};
        controller.discard();
        QTRY_COMPARE(controller.error(), QString::fromUtf8("采集服务的中文原句。"));
    }

    // GetStatus: capture.hint_ref (the hero card, the dashboard, the notice) and
    // warning_refs; update.download.message_ref - all from the offline backend,
    // which sends the Collector's keys.
    void theCaptureHintWarningsAndUpdateFailureFollowTheLanguage()
    {
        mr::MockBackend backend;
        backend.setLiveMode(mr::MockBackend::LiveMode::None);
        backend.setMidstreamSuspected(true);
        backend.setUpdateAvailable(true);
        backend.setUpdateDownloadFixture(QStringLiteral("failed"));
        mr::AppController app(&backend, nullptr);
        QTRY_VERIFY(app.captureMidstreamSuspected());
        const QString hint = chineseOf(mrtest::textRef("capture.hint.midstream"));
        const QString timeout = chineseOf(mrtest::textRef("update.download.timeout"));
        QVERIFY(!hint.isEmpty());
        QCOMPARE(app.captureHint(), hint);
        QCOMPARE(app.captureHealthText(), hint);
        QTRY_COMPARE(app.update()->downloadPhase(), QStringLiteral("failed"));
        QCOMPARE(app.update()->downloadStatusText(), timeout);
        const QString mockWarning = app.collectorWarnings().constFirst().toString();

        QSignalSpy health(&app, &mr::AppController::captureHealthTextChanged);
        QSignalSpy update(app.update(), &mr::UpdateController::changed);
        const mrtest::MarkedLanguage language;
        m_language->setLanguage(QStringLiteral("en"));
        QVERIFY(health.count() >= 1);
        QVERIFY(update.count() >= 1);
        QCOMPARE(app.captureHint(), mrtest::marked("capture.hint.midstream", hint));
        QCOMPARE(app.captureHealthText(), mrtest::marked("capture.hint.midstream", hint));
        QCOMPARE(app.update()->downloadStatusText(), mrtest::marked("update.download.timeout", timeout));
        // The mock's own warning has no catalogue entry and stays as written.
        QCOMPARE(app.collectorWarnings().constFirst().toString(), mockWarning);

        mr::MockBackend missing;
        missing.setLiveMode(mr::MockBackend::LiveMode::None);
        missing.setNpcapMissing(true);
        mr::AppController withoutNpcap(&missing, nullptr);
        QTRY_COMPARE(withoutNpcap.collectorWarnings().size(), 2);
        const QString guidance = chineseOf(mrtest::textRef("capture.npcap.not_installed"));
        QCOMPARE(withoutNpcap.collectorWarnings().at(1).toString(),
                 mrtest::marked("capture.npcap.not_installed", guidance));
        QCOMPARE(withoutNpcap.npcapInfo().value(QStringLiteral("install_hint")).toString(), guidance);
    }

    // 在线语音: the voices' label_ref, and the Collector's ERR_BAD_REQUEST sentence
    // inside 「保存没有成功：…」.
    void speechLabelsAndRefusalsFollowTheLanguage()
    {
        const QVariantMap label = mrtest::textRef("speech.voice.xiaoxiao");
        const QVariantMap endpoint = mrtest::textRef("speech.settings.endpoint_invalid");
        RefusingBackend backend;
        backend.speechSettings = {
            {QStringLiteral("provider"), QStringLiteral("azure")},
            {QStringLiteral("azure_region"), QStringLiteral("eastasia")},
            {QStringLiteral("voice"), QStringLiteral("zh-CN-XiaoxiaoNeural")},
            {QStringLiteral("has_key"), true},
            {QStringLiteral("configured"), true},
            {QStringLiteral("target_host"), QStringLiteral("eastasia.azure-speech.invalid")},
            {QStringLiteral("azure_voices"),
             QJsonArray{QJsonObject{{QStringLiteral("name"), QStringLiteral("zh-CN-XiaoxiaoNeural")},
                                    {QStringLiteral("label"), chineseOf(label)},
                                    {QStringLiteral("label_ref"), json(label)}},
                        QJsonObject{{QStringLiteral("name"), QStringLiteral("zh-CN-YunxiNeural")},
                                    {QStringLiteral("label"), QString::fromUtf8("云希（男声）")}}}},
            {QStringLiteral("openai_voices"), QJsonArray{}}};
        backend.refusalMessage = chineseOf(endpoint);
        backend.refusalRef = json(endpoint);
        mr::SpeechController speech;
        speech.setBackend(&backend);
        QTRY_VERIFY(speech.loaded());
        const auto labelOf = [&speech](const QString &id) {
            for (const QVariant &row : speech.voiceRows(QString())) {
                if (row.toMap().value(QStringLiteral("id")).toString() == id)
                    return row.toMap().value(QStringLiteral("label")).toString();
            }
            return QString();
        };
        QVERIFY2(labelOf(QStringLiteral("azure:zh-CN-XiaoxiaoNeural")).startsWith(chineseOf(label)),
                 qPrintable(labelOf(QStringLiteral("azure:zh-CN-XiaoxiaoNeural"))));
        speech.save({{QStringLiteral("azure_region"), QStringLiteral("eastasia")}});
        QTRY_VERIFY(!speech.lastError().isEmpty());
        QVERIFY2(speech.lastError().endsWith(chineseOf(endpoint)), qPrintable(speech.lastError()));

        QSignalSpy changed(&speech, &mr::SpeechController::changed);
        const mrtest::MarkedLanguage language;
        m_language->setLanguage(QStringLiteral("en"));
        QVERIFY(changed.count() >= 1);
        QVERIFY2(labelOf(QStringLiteral("azure:zh-CN-XiaoxiaoNeural"))
                     .startsWith(mrtest::marked("speech.voice.xiaoxiao", chineseOf(label))),
                 qPrintable(labelOf(QStringLiteral("azure:zh-CN-XiaoxiaoNeural"))));
        QVERIFY(labelOf(QStringLiteral("azure:zh-CN-YunxiNeural")).startsWith(QString::fromUtf8("云希（男声）")));
        QVERIFY2(speech.lastError().endsWith(mrtest::marked("speech.settings.endpoint_invalid", chineseOf(endpoint))),
                 qPrintable(speech.lastError()));
    }
};

int main(int argc, char **argv)
{
    // Never run against the user's own Collector, database or serve lease - see
    // TestCollectorGuard.h. Must precede any construction.
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QGuiApplication app(argc, argv);
    CollectorTextDisplayTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "CollectorTextDisplayTests.moc"
