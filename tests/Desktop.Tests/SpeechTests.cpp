// ---------------------------------------------------------------------------
// tst_speech - 在线语音, desktop half.
//
//   * SpeechController against MockBackend: load, 保存 / 清除 / 测试, the key
//     binding rule, an older Collector, the Chinese reasons.
//   * TtsService routing with a scripted backend and a fake player: files play
//     in order, failures and timeouts fall back to the local voice with one
//     toast per code, only WAVs inside tts-cache\ are played, and no
//     announcement is ever dropped.
//   * checkSpeechAudioFile on its own.
//
// Nothing here makes a sound or a network request: the engine is off
// (TtsService::EngineMode::None) and the player is a recorder.
// ---------------------------------------------------------------------------

#include "TestCollectorGuard.h"
#include "AppSettings.h"
#include "IBackend.h"
#include "MockBackend.h"
#include "SpeechAudioFile.h"
#include "SpeechController.h"
#include "SpeechPlayer.h"
#include "TtsService.h"

#include <QDir>
#include <QFile>
#include <QGuiApplication>
#include <QJsonArray>
#include <QJsonDocument>
#include <QPointer>
#include <QProcess>
#include <QRegularExpression>
#include <QScopeGuard>
#include <QSignalSpy>
#include <QStandardPaths>
#include <QTemporaryDir>
#include <QTest>
#include <QTimer>
#include <QtEndian>

#include <functional>

namespace {

const QString kHash64 = QStringLiteral("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

QByteArray waveBytes(quint16 bits = 16, quint16 format = 1, quint32 dataBytes = 4800)
{
    QByteArray bytes(44 + int(dataBytes), '\0');
    char *d = bytes.data();
    memcpy(d, "RIFF", 4);
    qToLittleEndian<quint32>(36 + dataBytes, d + 4);
    memcpy(d + 8, "WAVEfmt ", 8);
    qToLittleEndian<quint32>(16, d + 16);
    qToLittleEndian<quint16>(format, d + 20);
    qToLittleEndian<quint16>(1, d + 22);
    qToLittleEndian<quint32>(24000, d + 24);
    qToLittleEndian<quint32>(24000 * (bits / 8), d + 28);
    qToLittleEndian<quint16>(bits / 8, d + 32);
    qToLittleEndian<quint16>(bits, d + 34);
    memcpy(d + 36, "data", 4);
    qToLittleEndian<quint32>(dataBytes, d + 40);
    return bytes;
}

bool writeFile(const QString &path, const QByteArray &bytes)
{
    QDir().mkpath(QFileInfo(path).absolutePath());
    QFile file(path);
    return file.open(QIODevice::WriteOnly | QIODevice::Truncate) && file.write(bytes) == bytes.size();
}

QJsonObject azureSettings(bool configured = true)
{
    return {
        {QStringLiteral("provider"), QStringLiteral("azure")},
        {QStringLiteral("azure_region"), QStringLiteral("eastasia")},
        {QStringLiteral("openai_base_url"), QJsonValue::Null},
        {QStringLiteral("openai_model"), QJsonValue::Null},
        {QStringLiteral("voice"), QStringLiteral("zh-CN-XiaoxiaoNeural")},
        {QStringLiteral("has_key"), configured},
        {QStringLiteral("configured"), configured},
        {QStringLiteral("target_host"), QStringLiteral("eastasia.azure-speech.invalid")},
        {QStringLiteral("azure_voices"), QJsonArray{
             QJsonObject{{QStringLiteral("name"), QStringLiteral("zh-CN-XiaoxiaoNeural")},
                         {QStringLiteral("label"), QString::fromUtf8("晓晓（女声）")}},
             QJsonObject{{QStringLiteral("name"), QStringLiteral("zh-CN-YunxiNeural")},
                         {QStringLiteral("label"), QString::fromUtf8("云希（男声）")}}}},
        {QStringLiteral("openai_voices"), QJsonArray{
             QJsonObject{{QStringLiteral("name"), QStringLiteral("alloy")},
                         {QStringLiteral("label"), QStringLiteral("alloy")}}}},
    };
}

/// Answers GetSpeechSettings with \ref settings and SynthesizeSpeech from a
/// script; every request is recorded.
class ScriptedSpeechBackend final : public mr::IBackend
{
public:
    struct Answer {
        enum Kind { File, Error, Hang } kind = File;
        QString value; // audio_path or error code
    };

    QString backendName() const override { return QStringLiteral("mock"); }
    bool isConnected() const override { return connected; }

    mr::BackendReply *request(const QString &type, const QJsonObject &payload = {}) override
    {
        requests.append({type, payload});
        auto *reply = new mr::BackendReply(QString::number(requests.size()), type, this);
        if (type == QLatin1String("GetSpeechSettings") || type == QLatin1String("UpdateSpeechSettings")) {
            if (!settingsError.isEmpty()) {
                const QString code = settingsError;
                QTimer::singleShot(0, reply, [reply, code] { reply->fail(code, QStringLiteral("refused")); });
            } else {
                const QJsonObject answer = settings;
                QTimer::singleShot(0, reply, [reply, answer] { reply->succeed(answer); });
            }
            return reply;
        }
        if (type != QLatin1String("SynthesizeSpeech"))
            return reply; // never answered: nothing else is under test
        const Answer answer = script.isEmpty() ? fallback : script.takeFirst();
        switch (answer.kind) {
        case Answer::File:
            QTimer::singleShot(delayMs, reply, [reply, answer] {
                reply->succeed({{QStringLiteral("audio_path"), answer.value},
                                {QStringLiteral("from_cache"), false},
                                {QStringLiteral("provider"), QStringLiteral("azure")}});
            });
            break;
        case Answer::Error:
            QTimer::singleShot(delayMs, reply, [reply, answer] {
                reply->fail(answer.value, QStringLiteral("scripted"));
            });
            break;
        case Answer::Hang:
            hung.append(reply);
            break;
        }
        return reply;
    }

    QList<QJsonObject> payloadsOf(const QString &type) const
    {
        QList<QJsonObject> result;
        for (const auto &request : requests) {
            if (request.first == type)
                result.append(request.second);
        }
        return result;
    }
    int countOf(const QString &type) const { return int(payloadsOf(type).size()); }

    bool connected = true;
    int delayMs = 0;
    QJsonObject settings = azureSettings();
    QString settingsError;
    QList<Answer> script;
    Answer fallback;
    QList<QPair<QString, QJsonObject>> requests;
    QList<QPointer<mr::BackendReply>> hung;
};

/// Records what it is asked to play; finishes on request (or at once).
class FakePlayer final : public mr::SpeechPlayer
{
public:
    void play(const QString &path, double volume, int expectedDurationMs) override
    {
        played.append(path);
        volumes.append(volume);
        durations.append(expectedDurationMs);
        playing = true;
        if (autoFinish)
            QTimer::singleShot(0, this, [this] { finish(true); });
    }
    void stop() override
    {
        ++stops;
        playing = false;
    }
    void finish(bool ok, const QString &error = {})
    {
        if (!playing)
            return;
        playing = false;
        Q_EMIT finished(ok, error);
    }

    QStringList played;
    QList<double> volumes;
    QList<int> durations;
    bool playing = false;
    bool autoFinish = true;
    bool failNext = false;
    int stops = 0;
};

/// A throw-away data directory with a database path and a tts-cache folder.
struct CacheFixture {
    QTemporaryDir root;
    QString databasePath() const { return QDir::toNativeSeparators(root.filePath(QStringLiteral("mentor_recorder.db"))); }
    QString cacheDir() const { return root.filePath(QStringLiteral("tts-cache")); }
    QString wav(int index, const QByteArray &bytes = waveBytes()) const
    {
        const QString name = QStringLiteral("%1%2.wav").arg(kHash64.left(62)).arg(index, 2, 10, QLatin1Char('0'));
        const QString path = cacheDir() + QLatin1Char('/') + name;
        writeFile(path, bytes);
        return QDir::toNativeSeparators(path);
    }
};

/// Settings the tests change, put back afterwards (the INI is shared).
struct SettingsGuard {
    mr::AppSettings &settings;
    QString voice = settings.ttsVoice();
    bool enabled = settings.ttsEnabled();
    int rate = settings.ttsRate();
    int volume = settings.ttsVolume();
    bool confirmed = settings.ttsOnlineConfirmed();
    ~SettingsGuard()
    {
        settings.setTtsVoice(voice);
        settings.setTtsEnabled(enabled);
        settings.setTtsRate(rate);
        settings.setTtsVolume(volume);
        settings.setTtsOnlineConfirmed(confirmed);
    }
};

/// A TtsService on a scripted backend with a fake player and no engine.
struct RoutingFixture {
    mr::AppSettings settings;
    SettingsGuard guard{settings};
    ScriptedSpeechBackend backend;
    mr::SpeechController speech;
    mr::TtsService tts{&settings, nullptr, mr::TtsService::EngineMode::None};
    FakePlayer *player = new FakePlayer;
    CacheFixture cache;
    QStringList routes;   // "kind|text|route"
    QStringList toasts;
    QStringList fallbacks;

    RoutingFixture()
    {
        settings.setTtsEnabled(true);
        settings.setTtsRate(100);
        settings.setTtsVolume(80);
        settings.setTtsVoice(QStringLiteral("azure:zh-CN-XiaoxiaoNeural"));
        speech.setBackend(&backend);
        tts.setBackend(&backend);
        tts.setSpeech(&speech);
        tts.setPlayer(player);
        tts.setCollectorDatabasePath(cache.databasePath());
        QObject::connect(&tts, &mr::TtsService::spokeVia, &tts,
                         [this](const QString &kind, const QString &text, const QString &route) {
            routes.append(kind + QLatin1Char('|') + text + QLatin1Char('|') + route);
        });
        QObject::connect(&tts, &mr::TtsService::toastRequested, &tts,
                         [this](const QString &message) { toasts.append(message); });
        QObject::connect(&tts, &mr::TtsService::onlineFallback, &tts,
                         [this](const QString &code) { fallbacks.append(code); });
    }

    bool loaded() { return QTest::qWaitFor([this] { return speech.loaded(); }, 2000); }
    void say(const QString &text) { tts.announceText(QStringLiteral("entered"), text); }
    static ScriptedSpeechBackend::Answer file(const QString &path) { return {ScriptedSpeechBackend::Answer::File, path}; }
    static ScriptedSpeechBackend::Answer error(const QString &code) { return {ScriptedSpeechBackend::Answer::Error, code}; }
    static ScriptedSpeechBackend::Answer hang() { return {ScriptedSpeechBackend::Answer::Hang, {}}; }
};

QString route(const QString &text, const QString &how)
{
    return QStringLiteral("entered|") + text + QLatin1Char('|') + how;
}

/// SpeechController + TtsService on the shipping MockBackend.
struct MockFixture {
    mr::AppSettings settings;
    SettingsGuard guard{settings};
    QTemporaryDir data;
    mr::MockBackend backend;
    mr::SpeechController speech;
    mr::TtsService tts{&settings, nullptr, mr::TtsService::EngineMode::None};
    FakePlayer *player = new FakePlayer;

    explicit MockFixture(const QString &fixture)
    {
        backend.setDataDirectory(data.path());
        if (!fixture.isEmpty())
            backend.setSpeechFixture(fixture);
        settings.setTtsVoice(mr::MockBackend::speechFixtureVoiceId(fixture.isEmpty() ? QStringLiteral("azure") : fixture));
        speech.setBackend(&backend);
        tts.setBackend(&backend);
        tts.setSpeech(&speech);
        tts.setPlayer(player);
        tts.setCollectorDatabasePath(QDir::toNativeSeparators(data.filePath(QStringLiteral("mentor_recorder.db"))));
    }
    bool loaded() { return QTest::qWaitFor([this] { return speech.loaded(); }, 2000); }
    bool idle() { return QTest::qWaitFor([this] { return !speech.busy(); }, 3000); }
};

} // namespace

class SpeechTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    // -- IBackend wrappers -----------------------------------------------------
    void updateSpeechSettings_sendsOnlyTheKeysGiven();
    void synthesizeSpeech_clampsTheRate();

    // -- SpeechController on MockBackend --------------------------------------
    void controller_loadsTheMockSettingsWithoutAKey();
    void controller_saveStoresOnlyHasKey();
    void controller_changingTheServiceWithoutAKeyForgetsTheKey();
    void controller_clearKeyAndRefusals();
    void controller_testReportsSuccessAndFailure();
    void controller_olderCollectorHidesOnlineSpeech();
    void controller_errorReasons_data();
    void controller_errorReasons();
    void controller_syncsTheVoiceButNeverTheService();
    void controller_retriesAVoiceSyncTheCollectorCouldNotTake();
    void controller_describesTargetsWithoutGuessing();
    void mock_writesASilentWaveIntoTtsCache();

    // -- TtsService routing ---------------------------------------------------
    void routing_playsReturnedFilesOneAfterAnother();
    void routing_failureFallsBackWithOneToastPerCode();
    void routing_refusesFilesOutsideTheCache();
    void routing_refusesALinkedCacheFolder();
    void routing_timeoutFallsBackAndIgnoresTheLateAnswer();
    void routing_neverDropsAnAnnouncement();
    void routing_playbackFailureFallsBack();
    void routing_localVoiceNeverAsksTheCollector();
    void routing_mismatchedServiceIsNotSent();
    void routing_sendsRateTestFlagAndVolume();
    void routing_tooLongSentenceStaysLocal();
    void routing_previewInterruptsButKeepsUnheardAnnouncements();
    void voices_mergeLocalAndOnlineRows();

    // -- playback end detection ----------------------------------------------
    void playbackEnd_ignoresAMomentaryStop();
    void playbackEnd_endsOnceAtTheAnnouncedLength();

    // -- audio file checks ----------------------------------------------------
    void audioFile_acceptsOnlyCacheWaves();
    void audioFile_readsPcmHeaders();
};

// ---------------------------------------------------------------------------
// IBackend wrappers
// ---------------------------------------------------------------------------

void SpeechTests::updateSpeechSettings_sendsOnlyTheKeysGiven()
{
    ScriptedSpeechBackend backend;
    backend.updateSpeechSettings({{QStringLiteral("voice"), QStringLiteral("zh-CN-YunxiNeural")},
                                  {QStringLiteral("bogus"), 1}});
    QCOMPARE(backend.payloadsOf(QStringLiteral("UpdateSpeechSettings")).constLast(),
             QJsonObject({{QStringLiteral("voice"), QStringLiteral("zh-CN-YunxiNeural")}}));

    // "" deletes the key; a null field is sent as null (clear); a null
    // provider is never sent (it is an enum without null).
    backend.updateSpeechSettings({{QStringLiteral("api_key"), QString()},
                                  {QStringLiteral("azure_region"), QVariant()},
                                  {QStringLiteral("provider"), QVariant()}});
    const QJsonObject cleared = backend.payloadsOf(QStringLiteral("UpdateSpeechSettings")).constLast();
    QCOMPARE(cleared.value(QStringLiteral("api_key")), QJsonValue(QString()));
    QVERIFY(cleared.value(QStringLiteral("azure_region")).isNull());
    QVERIFY(cleared.contains(QStringLiteral("azure_region")));
    QVERIFY(!cleared.contains(QStringLiteral("provider")));
    QCOMPARE(cleared.size(), 2);
}

void SpeechTests::synthesizeSpeech_clampsTheRate()
{
    ScriptedSpeechBackend backend;
    backend.synthesizeSpeech(QStringLiteral("x"), 10, true);
    backend.synthesizeSpeech(QStringLiteral("y"), 900, false);
    const auto sent = backend.payloadsOf(QStringLiteral("SynthesizeSpeech"));
    QCOMPARE(sent.at(0), QJsonObject({{QStringLiteral("text"), QStringLiteral("x")},
                                      {QStringLiteral("rate_percent"), 50},
                                      {QStringLiteral("test"), true}}));
    QCOMPARE(sent.at(1).value(QStringLiteral("rate_percent")).toInt(), 200);
    QCOMPARE(sent.at(1).value(QStringLiteral("test")).toBool(), false);
}

// ---------------------------------------------------------------------------
// SpeechController on MockBackend
// ---------------------------------------------------------------------------

void SpeechTests::controller_loadsTheMockSettingsWithoutAKey()
{
    MockFixture fixture(QStringLiteral("azure"));
    QVERIFY(fixture.loaded());
    const mr::SpeechController &speech = fixture.speech;
    QVERIFY(speech.supported());
    QCOMPARE(speech.provider(), QStringLiteral("azure"));
    QCOMPARE(speech.azureRegion(), QStringLiteral("eastasia"));
    QCOMPARE(speech.voice(), QStringLiteral("zh-CN-XiaoxiaoNeural"));
    QVERIFY(speech.hasKey());
    QVERIFY(speech.configured());
    QCOMPARE(speech.targetHost(), QStringLiteral("eastasia.azure-speech.invalid"));
    QCOMPARE(speech.azureVoices().size(), 6);
    QCOMPARE(speech.openaiVoices().size(), 6);
    QCOMPARE(speech.azureVoices().constFirst().toMap().value(QStringLiteral("label")).toString(),
             QString::fromUtf8("晓晓（女声）"));
    QVERIFY(speech.configuredFor(QStringLiteral("azure:zh-CN-YunxiNeural")));
    QVERIFY(!speech.configuredFor(QStringLiteral("openai:alloy")));

    MockFixture openai(QStringLiteral("openai"));
    QVERIFY(openai.loaded());
    QCOMPARE(openai.speech.provider(), QStringLiteral("openai_compatible"));
    QCOMPARE(openai.speech.targetHost(), QStringLiteral("api.example.com"));
    QCOMPARE(openai.speech.openaiModel(), QStringLiteral("gpt-4o-mini-tts"));

    MockFixture none(QStringLiteral("unconfigured"));
    QVERIFY(none.loaded());
    QCOMPARE(none.speech.provider(), QStringLiteral("none"));
    QVERIFY(!none.speech.configured());
    QVERIFY(none.speech.targetHost().isEmpty());
}

void SpeechTests::controller_saveStoresOnlyHasKey()
{
    MockFixture fixture(QStringLiteral("unconfigured"));
    QVERIFY(fixture.loaded());
    QSignalSpy finished(&fixture.speech, &mr::SpeechController::saveFinished);
    const QString secret = QStringLiteral("secret-key-4242");
    fixture.speech.save({{QStringLiteral("provider"), QStringLiteral("azure")},
                         {QStringLiteral("azure_region"), QStringLiteral(" EastAsia ")},
                         {QStringLiteral("voice"), QStringLiteral("zh-CN-YunxiNeural")},
                         {QStringLiteral("api_key"), secret}});
    QVERIFY(fixture.speech.busy());
    QVERIFY(fixture.idle());
    QCOMPARE(finished.size(), 1);
    QCOMPARE(finished.constFirst().constFirst().toBool(), true);
    QVERIFY(fixture.speech.hasKey());
    QVERIFY(fixture.speech.configured());
    QCOMPARE(fixture.speech.azureRegion(), QStringLiteral("eastasia"));
    QCOMPARE(fixture.speech.resultState(), QStringLiteral("ok"));
    QCOMPARE(fixture.speech.resultText(), QString::fromUtf8("已保存"));
    QVERIFY(fixture.speech.lastError().isEmpty());

    // The key is write-only: nothing the backend answers carries it.
    QByteArray settings;
    fixture.backend.getSpeechSettings()->whenDone(&fixture.backend,
        [&settings](bool, const QVariantMap &payload, const QString &, const QString &) {
            settings = QJsonDocument(QJsonObject::fromVariantMap(payload)).toJson();
        });
    QTRY_VERIFY(!settings.isEmpty());
    QVERIFY(settings.contains("has_key"));
    QVERIFY(!settings.contains(secret.toUtf8()));

    // An empty key field means "keep": no api_key goes out.
    fixture.speech.save({{QStringLiteral("voice"), QStringLiteral("zh-CN-XiaoyiNeural")},
                         {QStringLiteral("api_key"), QStringLiteral("   ")}});
    QVERIFY(fixture.idle());
    QVERIFY(fixture.speech.hasKey());
    QCOMPARE(fixture.speech.voice(), QStringLiteral("zh-CN-XiaoyiNeural"));
}

void SpeechTests::controller_changingTheServiceWithoutAKeyForgetsTheKey()
{
    MockFixture fixture(QStringLiteral("azure"));
    QVERIFY(fixture.loaded());
    QVERIFY(fixture.speech.hasKey());
    fixture.speech.save({{QStringLiteral("provider"), QStringLiteral("openai_compatible")},
                         {QStringLiteral("openai_base_url"), QStringLiteral("https://api.example.com/v1/")},
                         {QStringLiteral("openai_model"), QStringLiteral("gpt-4o-mini-tts")},
                         {QStringLiteral("voice"), QStringLiteral("nova")}});
    QVERIFY(fixture.idle());
    QCOMPARE(fixture.speech.provider(), QStringLiteral("openai_compatible"));
    QCOMPARE(fixture.speech.openaiBaseUrl(), QStringLiteral("https://api.example.com/v1"));
    QVERIFY(!fixture.speech.hasKey());
    QVERIFY(!fixture.speech.configured());
    QCOMPARE(fixture.speech.targetHost(), QStringLiteral("api.example.com"));
}

void SpeechTests::controller_clearKeyAndRefusals()
{
    MockFixture fixture(QStringLiteral("openai"));
    QVERIFY(fixture.loaded());
    fixture.speech.clearKey();
    QVERIFY(fixture.idle());
    QVERIFY(!fixture.speech.hasKey());
    QCOMPARE(fixture.speech.resultText(), QString::fromUtf8("已清除密钥"));

    QSignalSpy finished(&fixture.speech, &mr::SpeechController::saveFinished);
    fixture.speech.save({{QStringLiteral("openai_base_url"), QStringLiteral("http://api.example.com/v1")}});
    QVERIFY(fixture.idle());
    QCOMPARE(finished.size(), 1);
    QCOMPARE(finished.constFirst().constFirst().toBool(), false);
    QCOMPARE(fixture.speech.resultState(), QStringLiteral("error"));
    QVERIFY2(fixture.speech.lastError().startsWith(QString::fromUtf8("保存没有成功：接口地址必须是 https://")),
             qPrintable(fixture.speech.lastError()));
    // Nothing was written.
    QCOMPARE(fixture.speech.openaiBaseUrl(), QStringLiteral("https://api.example.com/v1"));

    // A key with no target is refused.
    MockFixture none(QStringLiteral("unconfigured"));
    QVERIFY(none.loaded());
    none.speech.save({{QStringLiteral("api_key"), QStringLiteral("k")}});
    QVERIFY(none.idle());
    QVERIFY(none.speech.lastError().contains(QString::fromUtf8("请先选择在线语音服务")));
    QVERIFY(!none.speech.hasKey());
}

void SpeechTests::controller_testReportsSuccessAndFailure()
{
    MockFixture fixture(QStringLiteral("azure"));
    QVERIFY(fixture.loaded());
    QTRY_VERIFY(fixture.speech.available());
    fixture.speech.test();
    QVERIFY(fixture.speech.busy());
    QCOMPARE(fixture.speech.resultText(), QString::fromUtf8("正在测试…"));
    QVERIFY(fixture.idle());
    QCOMPARE(fixture.speech.resultState(), QStringLiteral("ok"));
    QCOMPARE(fixture.player->played.size(), 1);
    QVERIFY(fixture.player->played.constFirst().contains(QStringLiteral("tts-cache")));
    QCOMPARE(fixture.backend.speechSynthesisCount(), 1);

    MockFixture failing(QStringLiteral("fail"));
    QVERIFY(failing.loaded());
    QSignalSpy toasts(&failing.tts, &mr::TtsService::toastRequested);
    failing.speech.test();
    QVERIFY(failing.idle());
    QCOMPARE(failing.speech.resultState(), QStringLiteral("error"));
    QCOMPARE(failing.speech.lastError(),
             QString::fromUtf8("测试没有成功（网络不可用），示例播报改用本机语音播放。"));
    QVERIFY(failing.player->played.isEmpty());
    QCOMPARE(toasts.size(), 1);
    QCOMPARE(toasts.constFirst().constFirst().toString(),
             QString::fromUtf8("在线语音暂不可用（网络不可用），本次改用本机语音"));

    // Nobody listening: the test ends at once instead of spinning forever.
    ScriptedSpeechBackend backend;
    mr::SpeechController lonely;
    lonely.setBackend(&backend);
    QTRY_VERIFY(lonely.loaded());
    lonely.test();
    QVERIFY(!lonely.busy());
    QCOMPARE(lonely.resultState(), QStringLiteral("error"));
}

void SpeechTests::controller_olderCollectorHidesOnlineSpeech()
{
    mr::AppSettings settings;
    SettingsGuard guard{settings};
    settings.setTtsVoice(QStringLiteral("azure:zh-CN-XiaoxiaoNeural"));
    ScriptedSpeechBackend backend;
    backend.settingsError = QStringLiteral("ERR_UNKNOWN_MESSAGE");
    mr::SpeechController speech;
    mr::TtsService tts(&settings, nullptr, mr::TtsService::EngineMode::None);
    auto *player = new FakePlayer;
    tts.setPlayer(player);
    tts.setBackend(&backend);
    tts.setSpeech(&speech);
    speech.setBackend(&backend);
    QTRY_VERIFY(speech.loaded());
    QVERIFY(!speech.supported());
    QVERIFY(!speech.available());
    QVERIFY(speech.voiceRows(settings.ttsVoice()).isEmpty());
    for (const QVariant &row : tts.voices())
        QVERIFY(row.toMap().value(QStringLiteral("group")).toString() != QLatin1String("online"));
    QVERIFY(!tts.voiceId().startsWith(QStringLiteral("azure:")));

    // The saved choice still speaks - locally, with one explanation.
    QSignalSpy toasts(&tts, &mr::TtsService::toastRequested);
    QSignalSpy via(&tts, &mr::TtsService::spokeVia);
    tts.announceText(QStringLiteral("entered"), QStringLiteral("a"));
    tts.announceText(QStringLiteral("entered"), QStringLiteral("b"));
    QTRY_COMPARE(via.size(), 2);
    QCOMPARE(via.at(1).at(2).toString(), QStringLiteral("local"));
    QCOMPARE(backend.countOf(QStringLiteral("SynthesizeSpeech")), 0);
    QCOMPARE(toasts.size(), 1);
    QVERIFY(toasts.constFirst().constFirst().toString().contains(QString::fromUtf8("采集器版本不支持")));

    // A reconnect asks again. The shipping Collector refuses an unknown type
    // with ERR_BAD_REQUEST, which reads the same for this empty request.
    backend.settingsError = QStringLiteral("ERR_BAD_REQUEST");
    backend.connected = false;
    Q_EMIT backend.connectionChanged();
    backend.connected = true;
    Q_EMIT backend.connectionChanged();
    QTRY_VERIFY(speech.loaded());
    QVERIFY(!speech.supported());
    backend.settingsError.clear();
    backend.connected = false;
    Q_EMIT backend.connectionChanged();
    QVERIFY(!speech.loaded());
    backend.connected = true;
    Q_EMIT backend.connectionChanged();
    QTRY_VERIFY(speech.available());
}

void SpeechTests::controller_errorReasons_data()
{
    QTest::addColumn<QString>("code");
    QTest::addColumn<QString>("reason");
    QTest::newRow("not configured") << "ERR_SPEECH_NOT_CONFIGURED" << QString::fromUtf8("未配置");
    QTest::newRow("auth") << "ERR_SPEECH_AUTH" << QString::fromUtf8("密钥无效");
    QTest::newRow("quota") << "ERR_SPEECH_QUOTA" << QString::fromUtf8("额度用完");
    QTest::newRow("network") << "ERR_SPEECH_NETWORK" << QString::fromUtf8("网络不可用");
    QTest::newRow("timeout") << "ERR_SPEECH_TIMEOUT" << QString::fromUtf8("超时");
    QTest::newRow("format") << "ERR_SPEECH_FORMAT" << QString::fromUtf8("返回的不是可播放的声音");
    QTest::newRow("disabled") << "ERR_SPEECH_DISABLED" << QString::fromUtf8("在线语音已被禁用");
    QTest::newRow("refused path") << "DESKTOP_AUDIO_REJECTED" << QString::fromUtf8("返回的不是可播放的声音");
    QTest::newRow("older collector") << "ERR_UNKNOWN_MESSAGE" << QString::fromUtf8("采集器版本不支持");
    QTest::newRow("anything else") << "ERR_INTERNAL" << QString::fromUtf8("采集器没有响应");
}

void SpeechTests::controller_errorReasons()
{
    QFETCH(QString, code);
    QFETCH(QString, reason);
    QCOMPARE(mr::SpeechController::errorReason(code), reason);
    QCOMPARE(mr::TtsService::fallbackToast(code),
             QString::fromUtf8("在线语音暂不可用（%1），本次改用本机语音").arg(reason));
}

void SpeechTests::controller_syncsTheVoiceButNeverTheService()
{
    ScriptedSpeechBackend backend;
    mr::SpeechController speech;
    speech.setBackend(&backend);
    QTRY_VERIFY(speech.available());

    // Same service, other voice: the voice alone is sent, once.
    speech.setSelectedVoice(QStringLiteral("azure:zh-CN-YunxiNeural"));
    const QList<QJsonObject> expected{
        QJsonObject{{QStringLiteral("voice"), QStringLiteral("zh-CN-YunxiNeural")}}};
    QCOMPARE(backend.payloadsOf(QStringLiteral("UpdateSpeechSettings")), expected);
    QTRY_VERIFY(!speech.busy());
    // The scripted Collector still says Xiaoxiao: no second attempt.
    QCOMPARE(backend.countOf(QStringLiteral("UpdateSpeechSettings")), 1);
    QVERIFY(speech.resultText().isEmpty());

    // Another service: nothing is sent (that would delete the stored key).
    speech.setSelectedVoice(QStringLiteral("openai:alloy"));
    speech.setSelectedVoice(QStringLiteral("local:Microsoft Huihui Desktop"));
    speech.setSelectedVoice(QStringLiteral("azure:zh-CN-XiaoxiaoNeural"));
    QTest::qWait(20);
    QCOMPARE(backend.countOf(QStringLiteral("UpdateSpeechSettings")), 1);
}

void SpeechTests::controller_retriesAVoiceSyncTheCollectorCouldNotTake()
{
    ScriptedSpeechBackend backend;
    mr::SpeechController speech;
    speech.setBackend(&backend);
    QTRY_VERIFY(speech.available());
    const QString update = QStringLiteral("UpdateSpeechSettings");

    // The Collector was busy: not a verdict on the voice, so the next reload tries again.
    backend.settingsError = QStringLiteral("ERR_BUSY");
    speech.setSelectedVoice(QStringLiteral("azure:zh-CN-YunxiNeural"));
    QTRY_VERIFY(!speech.busy());
    QCOMPARE(backend.countOf(update), 1);
    backend.settingsError.clear();
    speech.refresh();
    QTRY_COMPARE(backend.countOf(update), 2);
    QTRY_VERIFY(!speech.busy());
    const QJsonObject yunxi{{QStringLiteral("voice"), QStringLiteral("zh-CN-YunxiNeural")}};
    QCOMPARE(backend.payloadsOf(update).last(), yunxi);
    // Synced now: a further reload sends nothing.
    speech.refresh();
    QTRY_VERIFY(!speech.busy());
    QTest::qWait(20);
    QCOMPARE(backend.countOf(update), 2);

    // A refusal is a verdict: the same voice is not offered again.
    backend.settingsError = QStringLiteral("ERR_BAD_REQUEST");
    speech.setSelectedVoice(QStringLiteral("azure:zh-CN-XiaoyiNeural"));
    QTRY_VERIFY(!speech.busy());
    QCOMPARE(backend.countOf(update), 3);
    backend.settingsError.clear();
    speech.refresh();
    QTRY_VERIFY(!speech.busy());
    QTest::qWait(20);
    QCOMPARE(backend.countOf(update), 3);
    QVERIFY(speech.resultText().isEmpty());
}

void SpeechTests::controller_describesTargetsWithoutGuessing()
{
    ScriptedSpeechBackend backend;
    mr::SpeechController speech;
    speech.setBackend(&backend);
    QTRY_VERIFY(speech.available());
    const QString azure = QStringLiteral("azure");
    const QString openai = QStringLiteral("openai_compatible");
    // The saved target: the Collector's own host name.
    QCOMPARE(speech.describeTarget(azure, QStringLiteral(" EASTASIA "), {}),
             QStringLiteral("eastasia.azure-speech.invalid"));
    // A draft: never a host name made up on this side.
    QCOMPARE(speech.describeTarget(azure, QStringLiteral("westus"), {}),
             QString::fromUtf8("微软 Azure 语音（westus 区域）"));
    QCOMPARE(speech.describeTarget(azure, {}, {}), QString::fromUtf8("微软 Azure 语音服务"));
    QCOMPARE(speech.describeTarget(openai, {}, QStringLiteral("https://tts.example.org/v1/")),
             QStringLiteral("tts.example.org"));
    QCOMPARE(speech.describeTarget(openai, {}, QStringLiteral("not a url")),
             QString::fromUtf8("你填写的地址"));
    QCOMPARE(mr::SpeechController::hostOfUrl(QStringLiteral("ftp://x.example/")), QString());
    QCOMPARE(mr::SpeechController::providerOfVoiceId(QStringLiteral("openai:nova")), openai);
    QCOMPARE(mr::SpeechController::voiceIdFor(openai, QStringLiteral(" nova ")), QStringLiteral("openai:nova"));
    QCOMPARE(mr::SpeechController::voiceIdFor(QStringLiteral("none"), QStringLiteral("x")), QString());
}

void SpeechTests::mock_writesASilentWaveIntoTtsCache()
{
    QTemporaryDir data;
    mr::MockBackend backend;
    backend.setDataDirectory(data.path());
    backend.setSpeechFixture(QStringLiteral("azure"));

    QVariantMap first;
    backend.synthesizeSpeech(QString::fromUtf8("匹配成功"), 100, false)->whenDone(&backend,
        [&first](bool ok, const QVariantMap &payload, const QString &, const QString &) {
            QVERIFY(ok);
            first = payload;
        });
    QTRY_VERIFY(!first.isEmpty());
    const QString path = first.value(QStringLiteral("audio_path")).toString();
    QVERIFY2(QFileInfo(path).fileName().contains(QRegularExpression(QStringLiteral("^[0-9a-f]{64}\\.wav$"))),
             qPrintable(path));
    QCOMPARE(first.value(QStringLiteral("from_cache")).toBool(), false);
    QCOMPARE(first.value(QStringLiteral("provider")).toString(), QStringLiteral("azure"));
    const mr::SpeechAudioCheck check = mr::checkSpeechAudioFile(
        path, data.filePath(QStringLiteral("mentor_recorder.db")));
    QVERIFY2(check.ok, qPrintable(check.reason));
    QCOMPARE(check.durationMs, 300);

    // The status the Desktop reads names the same directory.
    QVariantMap status;
    backend.getStatus()->whenDone(&backend, [&status](bool, const QVariantMap &payload, const QString &, const QString &) {
        status = payload;
    });
    QTRY_VERIFY(!status.isEmpty());
    QCOMPARE(QDir::cleanPath(QFileInfo(QDir::fromNativeSeparators(
                 status.value(QStringLiteral("database_path")).toString())).absolutePath()),
             QDir::cleanPath(data.path()));

    // Same sentence again: from the cache. The failing fixture refuses.
    QVariantMap second;
    backend.synthesizeSpeech(QString::fromUtf8("匹配成功"), 100, false)->whenDone(&backend,
        [&second](bool, const QVariantMap &payload, const QString &, const QString &) { second = payload; });
    QTRY_VERIFY(!second.isEmpty());
    QCOMPARE(second.value(QStringLiteral("from_cache")).toBool(), true);

    backend.setSpeechFixture(QStringLiteral("fail"));
    QString code;
    backend.synthesizeSpeech(QStringLiteral("x"), 100, false)->whenDone(&backend,
        [&code](bool, const QVariantMap &, const QString &error, const QString &) { code = error; });
    QTRY_COMPARE(code, QStringLiteral("ERR_SPEECH_NETWORK"));

    backend.setSpeechFixture(QStringLiteral("unconfigured"));
    code.clear();
    backend.synthesizeSpeech(QStringLiteral("x"), 100, false)->whenDone(&backend,
        [&code](bool, const QVariantMap &, const QString &error, const QString &) { code = error; });
    QTRY_COMPARE(code, QStringLiteral("ERR_SPEECH_NOT_CONFIGURED"));
}

// ---------------------------------------------------------------------------
// TtsService routing
// ---------------------------------------------------------------------------

void SpeechTests::routing_playsReturnedFilesOneAfterAnother()
{
    RoutingFixture f;
    QVERIFY(f.loaded());
    f.player->autoFinish = false;
    const QString a = f.cache.wav(1), b = f.cache.wav(2), c = f.cache.wav(3);
    f.backend.script = {RoutingFixture::file(a), RoutingFixture::file(b), RoutingFixture::file(c)};

    f.say(QStringLiteral("one"));
    f.say(QStringLiteral("two"));
    f.say(QStringLiteral("three"));
    // One request at a time.
    QTRY_COMPARE(f.player->played.size(), 1);
    QCOMPARE(f.backend.countOf(QStringLiteral("SynthesizeSpeech")), 1);
    QCOMPARE(f.backend.payloadsOf(QStringLiteral("SynthesizeSpeech")).constFirst()
                 .value(QStringLiteral("text")).toString(), QStringLiteral("one"));
    QCOMPARE(f.tts.pendingOnlineCount(), 3);
    QTest::qWait(30);
    QCOMPARE(f.backend.countOf(QStringLiteral("SynthesizeSpeech")), 1);

    f.player->finish(true);
    QTRY_COMPARE(f.player->played.size(), 2);
    QCOMPARE(f.backend.countOf(QStringLiteral("SynthesizeSpeech")), 2);
    f.player->finish(true);
    QTRY_COMPARE(f.player->played.size(), 3);
    f.player->finish(true);
    QTRY_COMPARE(f.tts.pendingOnlineCount(), 0);

    QCOMPARE(f.player->played, (QStringList{QFileInfo(a).canonicalFilePath(), QFileInfo(b).canonicalFilePath(),
                                           QFileInfo(c).canonicalFilePath()}));
    QCOMPARE(f.routes, (QStringList{route(QStringLiteral("one"), QStringLiteral("online")),
                                    route(QStringLiteral("two"), QStringLiteral("online")),
                                    route(QStringLiteral("three"), QStringLiteral("online"))}));
    QVERIFY(f.toasts.isEmpty());
    QCOMPARE(f.player->durations.constFirst(), 100);
}

void SpeechTests::routing_failureFallsBackWithOneToastPerCode()
{
    RoutingFixture f;
    QVERIFY(f.loaded());
    f.backend.script = {RoutingFixture::error(QStringLiteral("ERR_SPEECH_NETWORK")),
                        RoutingFixture::error(QStringLiteral("ERR_SPEECH_NETWORK")),
                        RoutingFixture::error(QStringLiteral("ERR_SPEECH_NETWORK")),
                        RoutingFixture::error(QStringLiteral("ERR_SPEECH_AUTH")),
                        RoutingFixture::error(QStringLiteral("ERR_SPEECH_AUTH"))};
    for (const char *text : {"a", "b", "c", "d", "e"})
        f.say(QString::fromLatin1(text));
    QTRY_COMPARE(f.routes.size(), 5);
    for (const QString &line : f.routes)
        QVERIFY2(line.endsWith(QStringLiteral("|local")), qPrintable(line));
    QCOMPARE(f.fallbacks, (QStringList{QStringLiteral("ERR_SPEECH_NETWORK"), QStringLiteral("ERR_SPEECH_NETWORK"),
                                       QStringLiteral("ERR_SPEECH_NETWORK"), QStringLiteral("ERR_SPEECH_AUTH"),
                                       QStringLiteral("ERR_SPEECH_AUTH")}));
    QCOMPARE(f.toasts, (QStringList{QString::fromUtf8("在线语音暂不可用（网络不可用），本次改用本机语音"),
                                    QString::fromUtf8("在线语音暂不可用（密钥无效），本次改用本机语音")}));
    QVERIFY(f.player->played.isEmpty());
    // Each sentence was still asked for: a failure is not a switch-off.
    QCOMPARE(f.backend.countOf(QStringLiteral("SynthesizeSpeech")), 5);
}

void SpeechTests::routing_refusesFilesOutsideTheCache()
{
    RoutingFixture f;
    QVERIFY(f.loaded());
    QTemporaryDir elsewhere;
    const QString outside = QDir::toNativeSeparators(elsewhere.filePath(kHash64 + QStringLiteral(".wav")));
    QVERIFY(writeFile(outside, waveBytes()));
    const QString inCache = f.cache.wav(7);
    const QString sneaky = QDir::toNativeSeparators(f.cache.cacheDir() + QStringLiteral("/../")
                                                    + QFileInfo(inCache).fileName());
    const QString missing = QDir::toNativeSeparators(f.cache.cacheDir() + QLatin1Char('/') + kHash64 + QStringLiteral(".wav"));
    const QString notWav = f.cache.cacheDir() + QLatin1Char('/') + kHash64 + QStringLiteral(".mp3");
    QVERIFY(writeFile(notWav, waveBytes()));
    const QString badName = f.cache.cacheDir() + QStringLiteral("/speech.wav");
    QVERIFY(writeFile(badName, waveBytes()));
    const QString notPcm16 = f.cache.wav(8, waveBytes(8));
    const QString notRiff = f.cache.wav(9, QByteArray(200, 'x'));
    const QString relative = QStringLiteral("tts-cache/") + QFileInfo(inCache).fileName();
    const QString device = QStringLiteral("\\\\?\\") + inCache;
    const QString deviceDot = QStringLiteral("\\\\.\\") + inCache;
    const QString stream = inCache + QStringLiteral(":evil");

    const QStringList refused{outside, sneaky, missing, notWav, badName, notPcm16, notRiff, relative, device, deviceDot, stream,
                              QString()};
    for (const QString &path : refused)
        f.backend.script.append(RoutingFixture::file(path));
    for (int index = 0; index < refused.size(); ++index)
        f.say(QString::number(index));
    QTRY_COMPARE(f.routes.size(), refused.size());
    for (const QString &line : f.routes)
        QVERIFY2(line.endsWith(QStringLiteral("|local")), qPrintable(line));
    QVERIFY(f.player->played.isEmpty());
    QCOMPARE(f.fallbacks.size(), refused.size());
    QCOMPARE(f.fallbacks.constFirst(), QStringLiteral("DESKTOP_AUDIO_REJECTED"));
    QCOMPARE(f.toasts, QStringList{QString::fromUtf8("在线语音暂不可用（返回的不是可播放的声音），本次改用本机语音")});

    // The same file named properly plays; without a database path nothing does.
    f.backend.script = {RoutingFixture::file(inCache)};
    f.say(QStringLiteral("ok"));
    QTRY_COMPARE(f.player->played.size(), 1);
    f.tts.setCollectorDatabasePath(QString());
    f.backend.script = {RoutingFixture::file(inCache)};
    f.say(QStringLiteral("no dir"));
    QTRY_COMPARE(f.routes.constLast(), route(QStringLiteral("no dir"), QStringLiteral("local")));
    f.tts.setCollectorDatabasePath(QStringLiteral("mentor_recorder.db"));
    f.backend.script = {RoutingFixture::file(inCache)};
    f.say(QStringLiteral("relative db"));
    QTRY_COMPARE(f.routes.constLast(), route(QStringLiteral("relative db"), QStringLiteral("local")));
    QCOMPARE(f.player->played.size(), 1);
}

void SpeechTests::routing_refusesALinkedCacheFolder()
{
    // A junction needs no privilege on Windows (a symbolic link does).
    RoutingFixture f;
    QVERIFY(f.loaded());
    QTemporaryDir target;
    QTemporaryDir data;
    const QString file = target.filePath(kHash64 + QStringLiteral(".wav"));
    QVERIFY(writeFile(file, waveBytes()));
    const QString link = QDir::toNativeSeparators(data.filePath(QStringLiteral("tts-cache")));
    const int exit = QProcess::execute(QStringLiteral("cmd.exe"),
                                       {QStringLiteral("/c"), QStringLiteral("mklink"), QStringLiteral("/J"),
                                        link, QDir::toNativeSeparators(target.path())});
    // Removed before the temporary directories, so their cleanup never
    // walks through the link.
    const auto unlink = qScopeGuard([link] { QDir().rmdir(link); });
    if (exit != 0 || !QFileInfo(link).isJunction())
        QSKIP("cannot create a junction on this machine");
    const QString viaLink = link + QLatin1Char('\\') + kHash64 + QStringLiteral(".wav");
    QVERIFY(QFileInfo::exists(viaLink));
    const mr::SpeechAudioCheck check = mr::checkSpeechAudioFile(
        viaLink, data.filePath(QStringLiteral("mentor_recorder.db")));
    QVERIFY(!check.ok);

    f.tts.setCollectorDatabasePath(data.filePath(QStringLiteral("mentor_recorder.db")));
    f.backend.script = {RoutingFixture::file(viaLink)};
    f.say(QStringLiteral("linked"));
    QTRY_COMPARE(f.routes.size(), 1);
    QCOMPARE(f.routes.constFirst(), route(QStringLiteral("linked"), QStringLiteral("local")));
    QVERIFY(f.player->played.isEmpty());
}

void SpeechTests::routing_timeoutFallsBackAndIgnoresTheLateAnswer()
{
    RoutingFixture f;
    QVERIFY(f.loaded());
    QCOMPARE(mr::TtsService::kDefaultOnlineReplyTimeoutMs >= 20000, true);
    f.tts.setOnlineReplyTimeoutMs(80);
    f.backend.script = {RoutingFixture::hang(), RoutingFixture::file(f.cache.wav(4))};
    f.say(QStringLiteral("slow"));
    f.say(QStringLiteral("next"));
    QTRY_COMPARE(f.routes.size(), 2);
    QCOMPARE(f.routes.at(0), route(QStringLiteral("slow"), QStringLiteral("local")));
    QCOMPARE(f.routes.at(1), route(QStringLiteral("next"), QStringLiteral("online")));
    QCOMPARE(f.fallbacks, QStringList{QStringLiteral("ERR_SPEECH_TIMEOUT")});
    QCOMPARE(f.toasts, QStringList{QString::fromUtf8("在线语音暂不可用（超时），本次改用本机语音")});
    QTRY_COMPARE(f.tts.pendingOnlineCount(), 0);

    // The answer that finally arrives is for a sentence already spoken.
    QVERIFY(!f.backend.hung.isEmpty() && f.backend.hung.constFirst());
    f.backend.hung.constFirst()->succeed({{QStringLiteral("audio_path"), f.cache.wav(5)},
                                          {QStringLiteral("from_cache"), false},
                                          {QStringLiteral("provider"), QStringLiteral("azure")}});
    QTest::qWait(30);
    QCOMPARE(f.player->played.size(), 1);
    QCOMPARE(f.routes.size(), 2);
}

void SpeechTests::routing_neverDropsAnAnnouncement()
{
    RoutingFixture f;
    QVERIFY(f.loaded());
    f.tts.setOnlineReplyTimeoutMs(60);
    f.backend.delayMs = 5;
    f.backend.script = {RoutingFixture::file(f.cache.wav(10)),
                        RoutingFixture::error(QStringLiteral("ERR_SPEECH_TIMEOUT")),
                        RoutingFixture::hang(),
                        RoutingFixture::file(QStringLiteral("C:/Windows/win.ini")),
                        RoutingFixture::error(QStringLiteral("ERR_SPEECH_QUOTA")),
                        RoutingFixture::file(f.cache.wav(11))};
    QStringList expected;
    for (int index = 0; index < 6; ++index) {
        const QString text = QStringLiteral("line %1").arg(index);
        f.say(text);
        expected.append(text);
    }
    QTRY_COMPARE_WITH_TIMEOUT(f.routes.size(), 6, 5000);
    QStringList spoken;
    for (const QString &line : f.routes)
        spoken.append(line.section(QLatin1Char('|'), 1, 1));
    QCOMPARE(spoken, expected);
    QCOMPARE(f.routes.constFirst().section(QLatin1Char('|'), 2), QStringLiteral("online"));
    QCOMPARE(f.routes.constLast().section(QLatin1Char('|'), 2), QStringLiteral("online"));
    QCOMPARE(f.player->played.size(), 2);
    QTRY_COMPARE(f.tts.pendingOnlineCount(), 0);
}

void SpeechTests::routing_playbackFailureFallsBack()
{
    RoutingFixture f;
    QVERIFY(f.loaded());
    f.player->autoFinish = false;
    f.backend.script = {RoutingFixture::file(f.cache.wav(12)), RoutingFixture::file(f.cache.wav(13))};
    f.say(QStringLiteral("broken"));
    f.say(QStringLiteral("fine"));
    QTRY_COMPARE(f.player->played.size(), 1);
    f.player->finish(false, QStringLiteral("device gone"));
    QTRY_COMPARE(f.player->played.size(), 2);
    f.player->finish(true);
    QTRY_COMPARE(f.tts.pendingOnlineCount(), 0);
    // The first sentence was announced online when it started, then again
    // locally when the playback failed; the second one online.
    QCOMPARE(f.routes, (QStringList{route(QStringLiteral("broken"), QStringLiteral("online")),
                                    route(QStringLiteral("broken"), QStringLiteral("local")),
                                    route(QStringLiteral("fine"), QStringLiteral("online"))}));
    QCOMPARE(f.fallbacks, QStringList{QStringLiteral("DESKTOP_PLAYBACK_FAILED")});
    QCOMPARE(f.toasts.size(), 1);
}

void SpeechTests::routing_localVoiceNeverAsksTheCollector()
{
    RoutingFixture f;
    QVERIFY(f.loaded());
    f.settings.setTtsVoice(QStringLiteral("local:Microsoft Huihui Desktop"));
    f.say(QStringLiteral("local"));
    f.tts.preview(QStringLiteral("finished"));
    QCOMPARE(f.routes.size(), 2);
    QVERIFY(f.routes.at(0).endsWith(QStringLiteral("|local")));
    QVERIFY(f.routes.at(1).endsWith(QStringLiteral("|local")));
    QTest::qWait(20);
    QCOMPARE(f.backend.countOf(QStringLiteral("SynthesizeSpeech")), 0);
    QVERIFY(f.toasts.isEmpty());

    // The master switch still silences announcements on the online route.
    f.settings.setTtsVoice(QStringLiteral("azure:zh-CN-XiaoxiaoNeural"));
    f.settings.setTtsEnabled(false);
    f.say(QStringLiteral("muted"));
    QTest::qWait(20);
    QCOMPARE(f.backend.countOf(QStringLiteral("SynthesizeSpeech")), 0);
    QCOMPARE(f.routes.size(), 2);
}

void SpeechTests::routing_mismatchedServiceIsNotSent()
{
    RoutingFixture f;
    QVERIFY(f.loaded());
    // The Collector has Azure in force; an OpenAI voice must not come out in Azure's voice.
    f.settings.setTtsVoice(QStringLiteral("openai:alloy"));
    f.say(QStringLiteral("other service"));
    QTRY_COMPARE(f.routes.size(), 1);
    QCOMPARE(f.routes.constFirst(), route(QStringLiteral("other service"), QStringLiteral("local")));
    QCOMPARE(f.backend.countOf(QStringLiteral("SynthesizeSpeech")), 0);
    QCOMPARE(f.backend.countOf(QStringLiteral("UpdateSpeechSettings")), 0);
    QCOMPARE(f.toasts, QStringList{QString::fromUtf8("在线语音暂不可用（未配置），本次改用本机语音")});

    // Before any settings arrived (no Collector), nothing is sent either.
    mr::AppSettings settings;
    SettingsGuard guard{settings};
    settings.setTtsVoice(QStringLiteral("azure:zh-CN-XiaoxiaoNeural"));
    ScriptedSpeechBackend offline;
    offline.connected = false;
    mr::SpeechController speech;
    speech.setBackend(&offline);
    mr::TtsService tts(&settings, nullptr, mr::TtsService::EngineMode::None);
    tts.setBackend(&offline);
    tts.setSpeech(&speech);
    QSignalSpy via(&tts, &mr::TtsService::spokeVia);
    tts.announceText(QStringLiteral("entered"), QStringLiteral("offline"));
    QTRY_COMPARE(via.size(), 1);
    QCOMPARE(via.constFirst().at(2).toString(), QStringLiteral("local"));
    QCOMPARE(offline.countOf(QStringLiteral("SynthesizeSpeech")), 0);
}

void SpeechTests::routing_sendsRateTestFlagAndVolume()
{
    RoutingFixture f;
    QVERIFY(f.loaded());
    f.settings.setTtsRate(150);
    f.settings.setTtsVolume(40);
    f.backend.fallback = RoutingFixture::file(f.cache.wav(20));
    f.say(QStringLiteral("announcement"));
    QTRY_COMPARE(f.player->played.size(), 1);
    f.tts.preview(QStringLiteral("finished"));
    QTRY_COMPARE(f.player->played.size(), 2);
    f.speech.test();
    QTRY_COMPARE(f.player->played.size(), 3);
    QTRY_VERIFY(!f.speech.busy());
    QCOMPARE(f.speech.resultState(), QStringLiteral("ok"));

    const auto sent = f.backend.payloadsOf(QStringLiteral("SynthesizeSpeech"));
    QCOMPARE(sent.size(), 3);
    QCOMPARE(sent.at(0).value(QStringLiteral("rate_percent")).toInt(), 150);
    QCOMPARE(sent.at(0).value(QStringLiteral("test")).toBool(), false);
    QCOMPARE(sent.at(1).value(QStringLiteral("test")).toBool(), true);
    QCOMPARE(sent.at(2).value(QStringLiteral("test")).toBool(), true);
    QCOMPARE(sent.at(1).value(QStringLiteral("text")).toString(), f.settings.templateFinished()
                 .replace(QStringLiteral("{progress}"), QStringLiteral("0"))
                 .replace(QStringLiteral("{remaining}"), QStringLiteral("0"))
                 .replace(QStringLiteral("{duty}"), QString::fromUtf8("未知副本")));
    QCOMPARE(f.player->volumes.constFirst(), 0.4);
}

void SpeechTests::routing_tooLongSentenceStaysLocal()
{
    RoutingFixture f;
    QVERIFY(f.loaded());
    const QString longText(201, QChar(0x5B57));
    f.say(longText);
    QTRY_COMPARE(f.routes.size(), 1);
    QVERIFY(f.routes.constFirst().endsWith(QStringLiteral("|local")));
    QCOMPARE(f.backend.countOf(QStringLiteral("SynthesizeSpeech")), 0);
    QCOMPARE(f.toasts, QStringList{QString::fromUtf8("在线语音暂不可用（这句话超过 200 字），本次改用本机语音")});

    // 200 characters are fine.
    f.backend.fallback = RoutingFixture::file(f.cache.wav(21));
    f.say(QString(200, QChar(0x5B57)));
    QTRY_COMPARE(f.player->played.size(), 1);
}

void SpeechTests::routing_previewInterruptsButKeepsUnheardAnnouncements()
{
    RoutingFixture f;
    QVERIFY(f.loaded());
    f.player->autoFinish = false;
    f.backend.script = {RoutingFixture::hang(),                       // "waiting" - never heard
                        RoutingFixture::file(f.cache.wav(30)),        // the preview
                        RoutingFixture::file(f.cache.wav(31)),        // "waiting" again
                        RoutingFixture::file(f.cache.wav(32))};       // "queued"
    f.say(QStringLiteral("waiting"));
    f.say(QStringLiteral("queued"));
    QTRY_COMPARE(f.backend.countOf(QStringLiteral("SynthesizeSpeech")), 1);
    f.tts.preview(QStringLiteral("finished"));
    QTRY_COMPARE(f.player->played.size(), 1);
    QCOMPARE(f.backend.payloadsOf(QStringLiteral("SynthesizeSpeech")).at(1).value(QStringLiteral("test")).toBool(), true);
    f.player->finish(true);
    QTRY_COMPARE(f.player->played.size(), 2);
    f.player->finish(true);
    QTRY_COMPARE(f.player->played.size(), 3);
    f.player->finish(true);
    QTRY_COMPARE(f.tts.pendingOnlineCount(), 0);
    const auto sent = f.backend.payloadsOf(QStringLiteral("SynthesizeSpeech"));
    QCOMPARE(sent.at(2).value(QStringLiteral("text")).toString(), QStringLiteral("waiting"));
    QCOMPARE(sent.at(3).value(QStringLiteral("text")).toString(), QStringLiteral("queued"));
    QVERIFY(f.toasts.isEmpty());

    // Something already playing is cut off, as on the local route.
    f.backend.script = {RoutingFixture::file(f.cache.wav(33)), RoutingFixture::file(f.cache.wav(34))};
    f.say(QStringLiteral("playing"));
    QTRY_COMPARE(f.player->played.size(), 4);
    const int stops = f.player->stops;
    f.tts.preview(QStringLiteral("finished"));
    QTRY_COMPARE(f.player->played.size(), 5);
    QVERIFY(f.player->stops > stops);
}

void SpeechTests::voices_mergeLocalAndOnlineRows()
{
    RoutingFixture f;
    QVERIFY(f.loaded());
    QTRY_VERIFY(!f.tts.voices().isEmpty());
    QStringList ids;
    QVariantMap xiaoxiao;
    for (const QVariant &row : f.tts.voices()) {
        const QVariantMap map = row.toMap();
        ids.append(map.value(QStringLiteral("id")).toString());
        if (map.value(QStringLiteral("id")) == QLatin1String("azure:zh-CN-XiaoxiaoNeural"))
            xiaoxiao = map;
    }
    QCOMPARE(ids, (QStringList{QStringLiteral("azure:zh-CN-XiaoxiaoNeural"), QStringLiteral("azure:zh-CN-YunxiNeural"),
                               QStringLiteral("openai:alloy")}));
    QCOMPARE(xiaoxiao.value(QStringLiteral("label")).toString(), QString::fromUtf8("晓晓（女声） · 在线（Azure）"));
    QCOMPARE(xiaoxiao.value(QStringLiteral("group")).toString(), QStringLiteral("online"));
    QCOMPARE(f.tts.voiceId(), QStringLiteral("azure:zh-CN-XiaoxiaoNeural"));

    // A voice typed on the panel gets a row of its own.
    QSignalSpy changed(&f.tts, &mr::TtsService::voicesChanged);
    f.tts.setVoice(QStringLiteral("openai:my-voice"));
    QCOMPARE(f.settings.ttsVoice(), QStringLiteral("openai:my-voice"));
    QVERIFY(changed.size() >= 1);
    const QVariantMap custom = f.tts.voices().constLast().toMap();
    QCOMPARE(custom.value(QStringLiteral("id")).toString(), QStringLiteral("openai:my-voice"));
    QCOMPARE(custom.value(QStringLiteral("label")).toString(), QString::fromUtf8("my-voice · 在线（OpenAI 兼容）"));
    QCOMPARE(f.tts.voiceId(), QStringLiteral("openai:my-voice"));

    // Rate changes do not rebuild the list.
    const int before = int(changed.size());
    f.settings.setTtsRate(120);
    QCOMPARE(int(changed.size()), before);

    // Unknown providers are ignored.
    f.tts.setVoice(QStringLiteral("google:x"));
    QCOMPARE(f.settings.ttsVoice(), QStringLiteral("openai:my-voice"));
    f.tts.setVoice(QStringLiteral("azure:zh-CN-YunxiNeural"));
    QCOMPARE(f.settings.ttsVoice(), QStringLiteral("azure:zh-CN-YunxiNeural"));
    QTRY_COMPARE(f.backend.countOf(QStringLiteral("UpdateSpeechSettings")), 1);
}

// ---------------------------------------------------------------------------
// Playback end detection (SoundEffectPlayer's PlaybackEndTracker)
// ---------------------------------------------------------------------------

void SpeechTests::playbackEnd_ignoresAMomentaryStop()
{
    // QSoundEffect may report playing == false while it buffers, then true again.
    mr::PlaybackEndTracker tracker;
    QSignalSpy ended(&tracker, &mr::PlaybackEndTracker::ended);
    tracker.start(60000);
    tracker.notePlaying(false);            // before anything played: nothing
    tracker.notePlaying(true);
    QVERIFY(tracker.started());
    tracker.notePlaying(false);            // the quirk, mid-sentence
    QTest::qWait(mr::PlaybackEndTracker::kSettleMs / 3);
    tracker.notePlaying(true);             // still playing after all
    QTest::qWait(mr::PlaybackEndTracker::kSettleMs * 3);
    QCOMPARE(ended.size(), 0);

    // A stop that lasts is the end, even before the announced length.
    tracker.notePlaying(false);
    QCOMPARE(ended.size(), 0);
    QTRY_COMPARE(ended.size(), 1);
    tracker.notePlaying(true);
    tracker.notePlaying(false);
    QTest::qWait(mr::PlaybackEndTracker::kSettleMs * 2);
    QCOMPARE(ended.size(), 1);

    // cancel() (stop, or the next sentence) reports nothing for this one.
    tracker.start(60000);
    tracker.notePlaying(true);
    tracker.notePlaying(false);
    tracker.cancel();
    QTest::qWait(mr::PlaybackEndTracker::kSettleMs * 2);
    QCOMPARE(ended.size(), 1);
}

void SpeechTests::playbackEnd_endsOnceAtTheAnnouncedLength()
{
    mr::PlaybackEndTracker tracker;
    QSignalSpy ended(&tracker, &mr::PlaybackEndTracker::ended);
    // Near the announced length a stop counts at once.
    tracker.start(mr::PlaybackEndTracker::kEndMarginMs);
    tracker.notePlaying(true);
    tracker.notePlaying(false);
    QCOMPARE(ended.size(), 1);
    tracker.notePlaying(false);
    QCOMPARE(ended.size(), 1);

    tracker.start(150);
    tracker.notePlaying(true);
    QTest::qWait(120);
    tracker.notePlaying(false);
    QCOMPARE(ended.size(), 2);
}

// ---------------------------------------------------------------------------
// Audio file checks
// ---------------------------------------------------------------------------

void SpeechTests::audioFile_acceptsOnlyCacheWaves()
{
    CacheFixture cache;
    const QString good = cache.wav(40);
    const QString db = cache.databasePath();
    mr::SpeechAudioCheck check = mr::checkSpeechAudioFile(good, db);
    QVERIFY2(check.ok, qPrintable(check.reason));
    QCOMPARE(QFileInfo(check.path).canonicalFilePath(), QFileInfo(good).canonicalFilePath());
    // Separators and letter case of the directory do not matter on Windows.
    QVERIFY(mr::checkSpeechAudioFile(QDir::fromNativeSeparators(good), db).ok);
    QVERIFY(mr::checkSpeechAudioFile(good, db.toUpper()).ok);

    QCOMPARE(mr::speechCacheDirectory(QString()), QString());
    QCOMPARE(mr::speechCacheDirectory(QStringLiteral("mentor_recorder.db")), QString());
    QCOMPARE(mr::speechCacheDirectory(QStringLiteral("C:/a/../b/mentor_recorder.db")), QString());
    QCOMPARE(mr::speechCacheDirectory(QStringLiteral("C:\\Data\\mentor_recorder.db")),
             QStringLiteral("C:/Data/tts-cache"));

    // A subfolder of tts-cache is not tts-cache.
    const QString nested = cache.cacheDir() + QStringLiteral("/sub/") + kHash64 + QStringLiteral(".wav");
    QVERIFY(writeFile(nested, waveBytes()));
    QVERIFY(!mr::checkSpeechAudioFile(nested, db).ok);
    // Another drive, the database folder itself.
    const QString beside = QFileInfo(db).absolutePath() + QLatin1Char('/') + kHash64 + QStringLiteral(".wav");
    QVERIFY(writeFile(beside, waveBytes()));
    QVERIFY(!mr::checkSpeechAudioFile(beside, db).ok);
    QVERIFY(!mr::checkSpeechAudioFile(QStringLiteral("Z:/tts-cache/") + kHash64 + QStringLiteral(".wav"), db).ok);
    // Too large.
    const QString huge = cache.wav(41, waveBytes(16, 1, quint32(mr::kMaxSpeechAudioBytes)));
    QVERIFY(!mr::checkSpeechAudioFile(huge, db).ok);
    // A directory with the right name.
    const QString folder = cache.cacheDir() + QLatin1Char('/') + kHash64.left(63) + QStringLiteral("f.wav");
    QVERIFY(QDir().mkpath(folder));
    QVERIFY(!mr::checkSpeechAudioFile(folder, db).ok);
}

void SpeechTests::audioFile_readsPcmHeaders()
{
    QTemporaryDir dir;
    const auto check = [&dir](const QByteArray &bytes, int *duration = nullptr) {
        const QString path = dir.filePath(QStringLiteral("probe.wav"));
        writeFile(path, bytes);
        QString reason;
        int ms = -1;
        const bool ok = mr::readPcmWaveHeader(path, &ms, &reason);
        if (duration)
            *duration = ms;
        return ok;
    };
    int duration = 0;
    QVERIFY(check(waveBytes(16, 1, 48000), &duration));
    QCOMPARE(duration, 1000);
    QVERIFY(!check(waveBytes(8)));
    QVERIFY(!check(waveBytes(16, 3)));        // float
    QVERIFY(!check(waveBytes(16, 1, 0)));     // no samples
    QVERIFY(!check(QByteArray("RIFF\0\0\0\0WAVE", 12) + QByteArray(40, '\0')));
    QVERIFY(!check(QByteArray("ID3") + QByteArray(100, '\0')));

    // An extra chunk before "data" is tolerated.
    QByteArray withList = waveBytes(16, 1, 2400);
    QByteArray list("LIST", 4);
    list.append(QByteArray::fromHex("04000000"));
    list.append("INFO");
    withList.insert(36, list);
    QVERIFY(check(withList, &duration));
    QCOMPARE(duration, 50);
}

int main(int argc, char **argv)
{
    // Never run against the user's own Collector, database or serve lease - see
    // TestCollectorGuard.h. Must precede any construction.
    mrtest::disableCollectorLaunch();
    // Keep the developer's real desktop.ini untouched.
    QStandardPaths::setTestModeEnabled(true);
    QGuiApplication app(argc, argv);
    SpeechTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "SpeechTests.moc"
