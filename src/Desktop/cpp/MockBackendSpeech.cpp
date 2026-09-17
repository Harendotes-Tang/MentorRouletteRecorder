// ---------------------------------------------------------------------------
// 在线语音 fixtures.
//
// The three speech messages against in-memory settings, with the Collector's
// validation rules and sentences (src/Collector/Speech/). Nothing is ever sent
// anywhere: this backend has no network client. SynthesizeSpeech writes a
// short silent 16-bit PCM WAV into <data dir>/tts-cache/<sha256>.wav, so the
// Desktop's real playback path - path check, QSoundEffect - runs offline.
// Only has_key is kept, never the key.
//
// The Azure target host is a reserved .invalid name on purpose: the real
// speech host may appear in exactly one file of the repository (NET-007).
// ---------------------------------------------------------------------------

#include "MockBackend.h"

#include <QCryptographicHash>
#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QJsonArray>
#include <QRegularExpression>
#include <QSaveFile>
#include <QUrl>
#include <QtEndian>

#include <cstring>
#include <iterator>

namespace mr {
namespace {

const QString kAzure = QStringLiteral("azure");
const QString kOpenAi = QStringLiteral("openai_compatible");
const QString kNone = QStringLiteral("none");

struct VoiceOption {
    const char *name;
    const char *label;
};

// The Collector's curated lists (src/Collector/Speech/OnlineSpeechService.cs).
constexpr VoiceOption kAzureVoices[] = {
    {"zh-CN-XiaoxiaoNeural", "晓晓（女声）"},
    {"zh-CN-YunxiNeural", "云希（男声）"},
    {"zh-CN-XiaoyiNeural", "晓伊（女声）"},
    {"zh-CN-YunjianNeural", "云健（男声）"},
    {"zh-CN-XiaochenNeural", "晓辰（女声）"},
    {"zh-CN-YunyangNeural", "云扬（男声，新闻）"},
};

constexpr VoiceOption kOpenAiVoices[] = {
    {"alloy", "alloy"}, {"echo", "echo"}, {"fable", "fable"},
    {"onyx", "onyx"},   {"nova", "nova"}, {"shimmer", "shimmer"},
};

QJsonArray voiceArray(const VoiceOption *first, const VoiceOption *last)
{
    QJsonArray rows;
    for (const VoiceOption *option = first; option != last; ++option) {
        rows.append(QJsonObject{{QStringLiteral("name"), QString::fromUtf8(option->name)},
                                {QStringLiteral("label"), QString::fromUtf8(option->label)}});
    }
    return rows;
}

bool matches(const char *pattern, const QString &text)
{
    const QRegularExpression expression(QString::fromLatin1(pattern));
    return expression.match(text).hasMatch();
}

bool isRegion(const QString &region)
{
    return matches("^[a-z0-9]{2,32}$", region);
}

bool isName(const QString &name)
{
    return matches("^[A-Za-z0-9._:/-]{1,128}$", name);
}

bool isVoice(const QString &provider, const QString &voice)
{
    if (provider == kAzure)
        return matches("^[A-Za-z0-9-]{3,64}$", voice);
    return provider == kOpenAi && isName(voice);
}

/// SpeechValidation.TryNormalizeBaseUrl: https, or http for loopback only; no
/// user, query or fragment; trailing slashes removed.
QString normalizeBaseUrl(const QString &raw)
{
    const QString text = raw.trimmed();
    if (text.isEmpty() || text.size() > 2048 || text.contains(QLatin1Char('?'))
        || text.contains(QLatin1Char('#')) || text.contains(QLatin1Char('\\'))
        || text.contains(QRegularExpression(QStringLiteral("\\s")))) {
        return {};
    }
    const QUrl url(text, QUrl::StrictMode);
    if (!url.isValid() || url.isRelative() || url.host().isEmpty() || !url.userInfo().isEmpty())
        return {};
    const QString host = url.host().toLower();
    const bool loopback = host == QLatin1String("localhost") || host == QLatin1String("127.0.0.1")
                          || host == QLatin1String("::1");
    if (url.scheme() != QLatin1String("https") && !(url.scheme() == QLatin1String("http") && loopback))
        return {};
    QString normalized = text;
    while (normalized.endsWith(QLatin1Char('/')))
        normalized.chop(1);
    return normalized;
}

QString stringField(const QJsonObject &object, const char *name)
{
    return object.value(QLatin1String(name)).toString();
}

/// OnlineSpeechClient.KeyBinding: what a stored key belongs to.
QString keyBinding(const QJsonObject &settings)
{
    const QString provider = stringField(settings, "provider");
    if (provider == kAzure && isRegion(stringField(settings, "azure_region")))
        return QStringLiteral("azure:") + stringField(settings, "azure_region");
    if (provider == kOpenAi) {
        const QString url = normalizeBaseUrl(stringField(settings, "openai_base_url"));
        if (!url.isEmpty())
            return QStringLiteral("openai_compatible:") + url;
    }
    return {};
}

bool isComplete(const QJsonObject &settings)
{
    const QString provider = stringField(settings, "provider");
    const QString voice = stringField(settings, "voice");
    if (provider == kAzure)
        return isRegion(stringField(settings, "azure_region")) && isVoice(provider, voice);
    if (provider == kOpenAi) {
        return !normalizeBaseUrl(stringField(settings, "openai_base_url")).isEmpty()
               && isName(stringField(settings, "openai_model")) && isVoice(provider, voice);
    }
    return false;
}

QString targetHost(const QJsonObject &settings)
{
    const QString provider = stringField(settings, "provider");
    if (provider == kAzure && isRegion(stringField(settings, "azure_region")))
        return stringField(settings, "azure_region") + QStringLiteral(".azure-speech.invalid");
    if (provider == kOpenAi) {
        const QString url = normalizeBaseUrl(stringField(settings, "openai_base_url"));
        if (!url.isEmpty())
            return QUrl(url).host();
    }
    return {};
}

QJsonValue nullable(const QString &text)
{
    return text.isEmpty() ? QJsonValue(QJsonValue::Null) : QJsonValue(text);
}

/// A canonical 44-byte header and 0.3 s of 24 kHz mono silence.
QByteArray silentWave()
{
    constexpr quint32 kSampleRate = 24000;
    constexpr quint32 kDataBytes = kSampleRate * 2 * 3 / 10;
    QByteArray bytes(44 + int(kDataBytes), '\0');
    char *data = bytes.data();
    const auto put32 = [data](int offset, quint32 value) { qToLittleEndian(value, data + offset); };
    const auto put16 = [data](int offset, quint16 value) { qToLittleEndian(value, data + offset); };
    memcpy(data, "RIFF", 4);
    put32(4, 36 + kDataBytes);
    memcpy(data + 8, "WAVEfmt ", 8);
    put32(16, 16);
    put16(20, 1);                // PCM
    put16(22, 1);                // mono
    put32(24, kSampleRate);
    put32(28, kSampleRate * 2);  // byte rate
    put16(32, 2);                // block align
    put16(34, 16);               // bits per sample
    memcpy(data + 36, "data", 4);
    put32(40, kDataBytes);
    return bytes;
}

} // namespace

bool MockBackend::isSpeechFixture(const QString &state)
{
    return state == QLatin1String("azure") || state == QLatin1String("openai")
           || state == QLatin1String("unconfigured") || state == QLatin1String("fail");
}

QString MockBackend::speechFixtureVoiceId(const QString &state)
{
    if (state == QLatin1String("openai"))
        return QStringLiteral("openai:alloy");
    if (isSpeechFixture(state))
        return QStringLiteral("azure:zh-CN-XiaoxiaoNeural");
    return {};
}

void MockBackend::setSpeechFixture(const QString &state)
{
    m_speechState = state;
    m_speech = QJsonObject();
    m_speechHasKey = false;
    if (state == QLatin1String("azure") || state == QLatin1String("fail")) {
        m_speech = {{QStringLiteral("provider"), kAzure},
                    {QStringLiteral("azure_region"), QStringLiteral("eastasia")},
                    {QStringLiteral("voice"), QStringLiteral("zh-CN-XiaoxiaoNeural")}};
        m_speechHasKey = true;
    } else if (state == QLatin1String("openai")) {
        m_speech = {{QStringLiteral("provider"), kOpenAi},
                    {QStringLiteral("openai_base_url"), QStringLiteral("https://api.example.com/v1")},
                    {QStringLiteral("openai_model"), QStringLiteral("gpt-4o-mini-tts")},
                    {QStringLiteral("voice"), QStringLiteral("alloy")}};
        m_speechHasKey = true;
    }
}

QString MockBackend::dataDirectory() const
{
    return m_dataDirectory.isEmpty() ? QDir::tempPath() + QStringLiteral("/MentorRecorder-mock")
                                     : m_dataDirectory;
}

bool MockBackend::isSpeechMessage(const QString &messageType)
{
    return messageType == QLatin1String("GetSpeechSettings")
           || messageType == QLatin1String("UpdateSpeechSettings")
           || messageType == QLatin1String("SynthesizeSpeech");
}

QJsonObject MockBackend::applySpeech(const QString &messageType, const QJsonObject &payload,
                                     QString *errorCode, QString *errorMessage,
                                     QJsonObject *errorDetails)
{
    if (messageType == QLatin1String("GetSpeechSettings"))
        return speechSettings();
    if (messageType == QLatin1String("UpdateSpeechSettings"))
        return updateSpeechPayload(payload, errorCode, errorMessage);
    return synthesizePayload(payload, errorCode, errorMessage, errorDetails);
}

QJsonObject MockBackend::speechSettings() const
{
    const QString provider = stringField(m_speech, "provider");
    const bool hasKey = m_speechHasKey && !keyBinding(m_speech).isEmpty();
    return {
        {QStringLiteral("provider"), provider.isEmpty() ? kNone : provider},
        {QStringLiteral("azure_region"), nullable(stringField(m_speech, "azure_region"))},
        {QStringLiteral("openai_base_url"), nullable(stringField(m_speech, "openai_base_url"))},
        {QStringLiteral("openai_model"), nullable(stringField(m_speech, "openai_model"))},
        {QStringLiteral("voice"), nullable(stringField(m_speech, "voice"))},
        {QStringLiteral("has_key"), hasKey},
        {QStringLiteral("configured"), hasKey && isComplete(m_speech)},
        {QStringLiteral("target_host"), nullable(targetHost(m_speech))},
        {QStringLiteral("azure_voices"), voiceArray(std::begin(kAzureVoices), std::end(kAzureVoices))},
        {QStringLiteral("openai_voices"), voiceArray(std::begin(kOpenAiVoices), std::end(kOpenAiVoices))},
    };
}

QJsonObject MockBackend::updateSpeechPayload(const QJsonObject &payload, QString *errorCode,
                                             QString *errorMessage)
{
    const auto refuse = [errorCode, errorMessage](const char *message) {
        *errorCode = QStringLiteral("ERR_BAD_REQUEST");
        *errorMessage = QString::fromUtf8(message);
        return QJsonObject();
    };

    // Everything is validated before anything is written.
    QJsonObject next = m_speech;
    if (payload.contains(QStringLiteral("provider"))) {
        const QString provider = payload.value(QStringLiteral("provider")).toString();
        if (provider != kNone && provider != kAzure && provider != kOpenAi)
            return refuse("provider 只能是 none、azure 或 openai_compatible。");
        next.insert(QStringLiteral("provider"), provider);
    }
    if (payload.contains(QStringLiteral("azure_region"))) {
        const QString region = payload.value(QStringLiteral("azure_region")).toString().trimmed().toLower();
        if (!region.isEmpty() && !isRegion(region))
            return refuse("区域只能是 2–32 个小写字母或数字，例如 eastasia。");
        next.insert(QStringLiteral("azure_region"), region);
    }
    if (payload.contains(QStringLiteral("openai_base_url"))) {
        const QString raw = payload.value(QStringLiteral("openai_base_url")).toString().trimmed();
        const QString url = normalizeBaseUrl(raw);
        if (!raw.isEmpty() && url.isEmpty())
            return refuse("接口地址必须是 https:// 开头的完整地址（本机回环地址可以用 http://），不能带用户名、查询串或片段。");
        next.insert(QStringLiteral("openai_base_url"), url);
    }
    if (payload.contains(QStringLiteral("openai_model"))) {
        const QString model = payload.value(QStringLiteral("openai_model")).toString().trimmed();
        if (!model.isEmpty() && !isName(model))
            return refuse("模型名只能包含英文字母、数字和 . _ : / -，最多 128 个字符。");
        next.insert(QStringLiteral("openai_model"), model);
    }
    if (payload.contains(QStringLiteral("voice"))) {
        const QString voice = payload.value(QStringLiteral("voice")).toString().trimmed();
        next.insert(QStringLiteral("voice"), voice);
    }
    const QString provider = stringField(next, "provider");
    const QString voice = stringField(next, "voice");
    if (!voice.isEmpty() && (provider == kAzure || provider == kOpenAi) && !isVoice(provider, voice))
        return refuse("音色名不合法。");

    const QString oldBinding = keyBinding(m_speech);
    const QString newBinding = keyBinding(next);
    const bool keyGiven = payload.contains(QStringLiteral("api_key"));
    const QString key = payload.value(QStringLiteral("api_key")).toString().trimmed();
    if (keyGiven && !key.isEmpty()) {
        if (key.size() > 512 || !matches("^[!-~]+$", key))
            return refuse("密钥只能包含可见的英文字符，不能有空格，最多 512 个字符。");
        if (newBinding.isEmpty())
            return refuse("请先选择在线语音服务并填写区域或接口地址，再填写密钥。");
    }

    m_speech = next;
    if (keyGiven)
        m_speechHasKey = !key.isEmpty();  // the key itself is dropped here
    else if (oldBinding != newBinding)
        m_speechHasKey = false;           // never sent where it was not entered for
    return speechSettings();
}

QJsonObject MockBackend::synthesizePayload(const QJsonObject &payload, QString *errorCode,
                                           QString *errorMessage, QJsonObject *errorDetails)
{
    ++m_speechSynthesisCount;
    QString text = payload.value(QStringLiteral("text")).toString();
    text.replace(QLatin1Char('\t'), QLatin1Char(' '))
        .replace(QLatin1Char('\r'), QLatin1Char(' '))
        .replace(QLatin1Char('\n'), QLatin1Char(' '));
    text = text.trimmed();
    const int rate = payload.value(QStringLiteral("rate_percent")).toInt(100);
    if (text.isEmpty() || text.toUcs4().size() > 200 || rate < 50 || rate > 200) {
        *errorCode = QStringLiteral("ERR_BAD_REQUEST");
        *errorMessage = QString::fromUtf8("播报文字必须是 1–200 个字，语速必须在 50–200 之间。");
        return {};
    }
    if (m_speechState == QLatin1String("fail")) {
        *errorCode = QStringLiteral("ERR_SPEECH_NETWORK");
        *errorMessage = QString::fromUtf8("连不上语音服务，这一句改用本机语音。");
        *errorDetails = {{QStringLiteral("reason"), QStringLiteral("CONNECTION_ERROR")}};
        return {};
    }
    const QJsonObject settings = speechSettings();
    if (!settings.value(QStringLiteral("configured")).toBool()) {
        *errorCode = QStringLiteral("ERR_SPEECH_NOT_CONFIGURED");
        *errorMessage = QString::fromUtf8("在线语音还没有配置好：请选择服务，填写区域或接口地址、音色和密钥。");
        return {};
    }

    const QString provider = stringField(m_speech, "provider");
    const QByteArray key = QCryptographicHash::hash(
        QStringLiteral("%1|%2|%3|%4").arg(provider, stringField(m_speech, "voice")).arg(rate).arg(text).toUtf8(),
        QCryptographicHash::Sha256).toHex();
    const QString folder = dataDirectory() + QStringLiteral("/tts-cache");
    const QString path = folder + QLatin1Char('/') + QString::fromLatin1(key) + QStringLiteral(".wav");
    const bool cached = QFileInfo::exists(path) && !payload.value(QStringLiteral("test")).toBool();
    if (!cached) {
        QSaveFile file(path);
        if (!QDir().mkpath(folder) || !file.open(QIODevice::WriteOnly)
            || file.write(silentWave()) < 0 || !file.commit()) {
            *errorCode = QStringLiteral("ERR_SPEECH_FORMAT");
            *errorMessage = QString::fromUtf8("语音服务返回的不是可播放的 WAV 音频。");
            *errorDetails = {{QStringLiteral("reason"), QStringLiteral("CACHE_WRITE")}};
            return {};
        }
    }
    return {{QStringLiteral("audio_path"), QDir::toNativeSeparators(QFileInfo(path).absoluteFilePath())},
            {QStringLiteral("from_cache"), cached},
            {QStringLiteral("provider"), provider}};
}

} // namespace mr
