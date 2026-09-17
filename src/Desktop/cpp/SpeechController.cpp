#include "SpeechController.h"

#include "IBackend.h"

#include <QJsonArray>
#include <QLoggingCategory>
#include <QUrl>

namespace {

Q_LOGGING_CATEGORY(lcSpeech, "mr.speech")

const QString kAzure = QStringLiteral("azure");
const QString kOpenAi = QStringLiteral("openai_compatible");

QVariantList voiceList(const QJsonValue &value)
{
    QVariantList rows;
    const QJsonArray array = value.toArray();
    for (const QJsonValue &entry : array) {
        const QJsonObject object = entry.toObject();
        const QString name = object.value(QStringLiteral("name")).toString().trimmed();
        if (name.isEmpty())
            continue;
        const QString label = object.value(QStringLiteral("label")).toString().trimmed();
        rows.append(QVariantMap{{QStringLiteral("name"), name},
                                {QStringLiteral("label"), label.isEmpty() ? name : label}});
    }
    return rows;
}

QString normalizedUrl(const QString &url)
{
    QString text = url.trimmed();
    while (text.endsWith(QLatin1Char('/')))
        text.chop(1);
    return text;
}

QVariantMap voiceRow(const QString &provider, const QString &name, const QString &label)
{
    const bool azure = provider == kAzure;
    const QString suffix = azure ? QString::fromUtf8(" · 在线（Azure）")
                                 : QString::fromUtf8(" · 在线（OpenAI 兼容）");
    return {{QStringLiteral("id"), mr::SpeechController::voiceIdFor(provider, name)},
            {QStringLiteral("name"), name},
            {QStringLiteral("label"), (label.isEmpty() ? name : label) + suffix},
            {QStringLiteral("locale"), azure ? QStringLiteral("zh-CN") : QString()},
            {QStringLiteral("provider"), azure ? QStringLiteral("azure") : QStringLiteral("openai")},
            {QStringLiteral("group"), QStringLiteral("online")}};
}

} // namespace

namespace mr {

SpeechController::SpeechController(QObject *parent)
    : QObject(parent)
{
}

void SpeechController::setBackend(IBackend *backend)
{
    if (m_backend == backend)
        return;
    if (m_backend)
        m_backend->disconnect(this);
    m_backend = backend;
    if (!m_backend)
        return;
    connect(m_backend, &IBackend::connectionChanged, this, [this] {
        if (m_backend && m_backend->isConnected()) {
            refresh();
            return;
        }
        // A Collector that went away may come back as a different build; what
        // it supports is asked again on the next connect. Replies still on
        // the wire are dropped by the generation check.
        ++m_generation;
        const bool wasVisible = m_loaded || m_busy;
        m_loaded = false;
        m_supported = true;
        m_busy = false;
        m_testing = false;
        m_syncedVoice.clear();
        if (wasVisible)
            Q_EMIT changed();
    });
    if (m_backend->isConnected())
        refresh();
}

// ---------------------------------------------------------------------------
// Voice ids
// ---------------------------------------------------------------------------

QString SpeechController::providerOfVoiceId(const QString &id)
{
    if (id.startsWith(azurePrefix()) && id.size() > azurePrefix().size())
        return kAzure;
    if (id.startsWith(openaiPrefix()) && id.size() > openaiPrefix().size())
        return kOpenAi;
    return {};
}

QString SpeechController::voiceNameOf(const QString &id)
{
    if (id.startsWith(azurePrefix()))
        return id.mid(azurePrefix().size()).trimmed();
    if (id.startsWith(openaiPrefix()))
        return id.mid(openaiPrefix().size()).trimmed();
    return {};
}

QString SpeechController::voiceIdFor(const QString &provider, const QString &voice)
{
    const QString name = voice.trimmed();
    if (name.isEmpty())
        return {};
    if (provider == kAzure)
        return azurePrefix() + name;
    if (provider == kOpenAi)
        return openaiPrefix() + name;
    return {};
}

bool SpeechController::isOnlineVoiceId(const QString &id)
{
    return !providerOfVoiceId(id).isEmpty();
}

bool SpeechController::configuredFor(const QString &id) const
{
    return available() && configured() && !m_provider.isEmpty()
           && providerOfVoiceId(id) == m_provider;
}

QVariantList SpeechController::voiceRows(const QString &current) const
{
    QVariantList rows;
    if (!available())
        return rows;
    QStringList ids;
    const auto add = [&rows, &ids](const QVariantMap &row) {
        const QString id = row.value(QStringLiteral("id")).toString();
        if (id.isEmpty() || ids.contains(id))
            return;
        ids.append(id);
        rows.append(row);
    };
    for (const QVariant &entry : m_azureVoices) {
        const QVariantMap voice = entry.toMap();
        add(voiceRow(kAzure, voice.value(QStringLiteral("name")).toString(),
                     voice.value(QStringLiteral("label")).toString()));
    }
    for (const QVariant &entry : m_openaiVoices) {
        const QVariantMap voice = entry.toMap();
        add(voiceRow(kOpenAi, voice.value(QStringLiteral("name")).toString(),
                     voice.value(QStringLiteral("label")).toString()));
    }
    // A name typed on the panel (any OpenAI-compatible service names its
    // voices its own way), and whatever the Collector has in force.
    if (m_provider == kAzure || m_provider == kOpenAi)
        add(voiceRow(m_provider, voice(), QString()));
    const QString selectedProvider = providerOfVoiceId(current);
    if (!selectedProvider.isEmpty())
        add(voiceRow(selectedProvider, voiceNameOf(current), QString()));
    return rows;
}

// ---------------------------------------------------------------------------
// Sentences
// ---------------------------------------------------------------------------

QString SpeechController::errorReason(const QString &code)
{
    if (code == QLatin1String("ERR_SPEECH_NOT_CONFIGURED")
        || code == QLatin1String("DESKTOP_PROVIDER_MISMATCH"))
        return QString::fromUtf8("未配置");
    if (code == QLatin1String("ERR_SPEECH_AUTH"))
        return QString::fromUtf8("密钥无效");
    if (code == QLatin1String("ERR_SPEECH_QUOTA"))
        return QString::fromUtf8("额度用完");
    if (code == QLatin1String("ERR_SPEECH_NETWORK"))
        return QString::fromUtf8("网络不可用");
    if (code == QLatin1String("ERR_SPEECH_TIMEOUT"))
        return QString::fromUtf8("超时");
    if (code == QLatin1String("ERR_SPEECH_FORMAT")
        || code == QLatin1String("DESKTOP_AUDIO_REJECTED"))
        return QString::fromUtf8("返回的不是可播放的声音");
    if (code == QLatin1String("ERR_SPEECH_DISABLED"))
        return QString::fromUtf8("在线语音已被禁用");
    if (code == QLatin1String("ERR_UNKNOWN_MESSAGE"))
        return QString::fromUtf8("采集器版本不支持");
    if (code == QLatin1String("DESKTOP_PLAYBACK_FAILED"))
        return QString::fromUtf8("无法播放返回的声音");
    if (code == QLatin1String("DESKTOP_TEXT_TOO_LONG"))
        return QString::fromUtf8("这句话超过 200 字");
    if (code == QLatin1String("DESKTOP_CANCELLED"))
        return QString::fromUtf8("被新的播报打断");
    return QString::fromUtf8("采集器没有响应");
}

QString SpeechController::describeTarget(const QString &provider, const QString &region,
                                         const QString &baseUrl) const
{
    const QString host = targetHost();
    if (available() && provider == m_provider && !host.isEmpty()) {
        if (provider == kAzure && region.trimmed().toLower() == azureRegion())
            return host;
        if (provider == kOpenAi && normalizedUrl(baseUrl) == openaiBaseUrl())
            return host;
    }
    if (provider == kOpenAi) {
        const QString typed = hostOfUrl(baseUrl);
        return typed.isEmpty() ? QString::fromUtf8("你填写的地址") : typed;
    }
    const QString typedRegion = region.trimmed().toLower();
    return typedRegion.isEmpty()
               ? QString::fromUtf8("微软 Azure 语音服务")
               : QString::fromUtf8("微软 Azure 语音（%1 区域）").arg(typedRegion);
}

QString SpeechController::hostOfUrl(const QString &url)
{
    // Parsing only: QUrl never resolves or connects.
    const QUrl parsed(url.trimmed(), QUrl::StrictMode);
    if (!parsed.isValid() || parsed.isRelative())
        return {};
    const QString scheme = parsed.scheme().toLower();
    if (scheme != QLatin1String("https") && scheme != QLatin1String("http"))
        return {};
    return parsed.host();
}

// ---------------------------------------------------------------------------
// Requests
// ---------------------------------------------------------------------------

void SpeechController::refresh()
{
    if (!m_backend || !m_backend->isConnected())
        return;
    const int generation = m_generation;
    m_backend->getSpeechSettings()->whenDone(this,
        [this, generation](bool ok, const QVariantMap &payload, const QString &code, const QString &) {
            if (generation != m_generation)
                return;
            if (ok) {
                adopt(QJsonObject::fromVariantMap(payload));
                return;
            }
            // A Collector older than the message refuses its type: the shipping
            // ones with ERR_BAD_REQUEST (field message_type), others with
            // ERR_UNKNOWN_MESSAGE / ERR_UNSUPPORTED. The payload is empty, so a
            // bad request can only be about the type (same rule as the
            // integrity check, ExportController).
            if (code == QLatin1String("ERR_UNKNOWN_MESSAGE")
                || code == QLatin1String("ERR_UNSUPPORTED")
                || code == QLatin1String("ERR_BAD_REQUEST")) {
                m_loaded = true;
                m_supported = false;
                Q_EMIT changed();
                return;
            }
            qCInfo(lcSpeech) << "GetSpeechSettings failed:" << code;
        });
}

void SpeechController::adopt(const QJsonObject &settings)
{
    m_settings = settings;
    const QString provider = settings.value(QStringLiteral("provider")).toString();
    m_provider = (provider == kAzure || provider == kOpenAi) ? provider : QStringLiteral("none");
    m_azureVoices = voiceList(settings.value(QStringLiteral("azure_voices")));
    m_openaiVoices = voiceList(settings.value(QStringLiteral("openai_voices")));
    m_loaded = true;
    m_supported = true;
    Q_EMIT changed();
    Q_EMIT settingsReplaced();
    maybeSyncVoice();
}

void SpeechController::save(const QVariantMap &fields)
{
    QVariantMap outgoing;
    for (const char *name : {"provider", "azure_region", "openai_base_url", "openai_model", "voice"}) {
        const QString key = QLatin1String(name);
        if (fields.contains(key))
            outgoing.insert(key, fields.value(key).toString().trimmed());
    }
    // Only a key the user actually typed; an empty field means "keep".
    const QString key = fields.value(QStringLiteral("api_key")).toString().trimmed();
    if (!key.isEmpty())
        outgoing.insert(QStringLiteral("api_key"), key);
    send(outgoing, QString::fromUtf8("已保存"), QString::fromUtf8("保存没有成功"));
}

void SpeechController::clearKey()
{
    send({{QStringLiteral("api_key"), QString()}}, QString::fromUtf8("已清除密钥"),
         QString::fromUtf8("清除密钥没有成功"));
}

void SpeechController::send(const QVariantMap &fields, const QString &successText,
                            const QString &failurePrefix)
{
    if (!m_backend || m_busy || fields.isEmpty())
        return;
    const bool quiet = successText.isEmpty();
    m_busy = true;
    if (!quiet)
        setResult(QString(), QString());
    Q_EMIT changed();

    const int generation = m_generation;
    m_backend->updateSpeechSettings(fields)->whenDone(this,
        [this, generation, quiet, successText, failurePrefix](
            bool ok, const QVariantMap &payload, const QString &code, const QString &message) {
            if (generation != m_generation)
                return;
            m_busy = false;
            if (ok) {
                if (!quiet)
                    setResult(QStringLiteral("ok"), successText);
                adopt(QJsonObject::fromVariantMap(payload));
                if (!quiet)
                    Q_EMIT saveFinished(true);
                return;
            }
            if (code == QLatin1String("ERR_UNKNOWN_MESSAGE"))
                m_supported = false;
            // ERR_BAD_REQUEST carries the Collector's own sentence naming the
            // field; anything else is explained by its code.
            const QString reason = code == QLatin1String("ERR_BAD_REQUEST") && !message.trimmed().isEmpty()
                                       ? message.trimmed()
                                       : errorReason(code);
            if (quiet) {
                qCInfo(lcSpeech) << "voice sync failed:" << code;
                // A refusal stands: the same voice would be refused again. Anything
                // else - the Collector busy, the connection dropped - gets one more
                // try the next time the settings are reloaded or the user acts.
                if (!isRefusal(code))
                    m_syncedVoice.clear();
            } else {
                setResult(QStringLiteral("error"), failurePrefix + QString::fromUtf8("：") + reason);
            }
            Q_EMIT changed();
            if (!quiet)
                Q_EMIT saveFinished(false);
        });
}

void SpeechController::test()
{
    if (m_busy)
        return;
    m_busy = true;
    m_testing = true;
    setResult(QString(), QString::fromUtf8("正在测试…"));
    Q_EMIT changed();
    if (!isSignalConnected(QMetaMethod::fromSignal(&SpeechController::testRequested))) {
        noteTestResult(false, QStringLiteral("ERR_INTERNAL"), false);
        return;
    }
    Q_EMIT testRequested();
}

void SpeechController::noteTestResult(bool ok, const QString &code, bool spokenOnline)
{
    if (!m_testing)
        return;
    m_testing = false;
    m_busy = false;
    if (ok && spokenOnline) {
        setResult(QStringLiteral("ok"), QString::fromUtf8("测试成功：示例播报已用在线语音播放。"));
    } else {
        setResult(QStringLiteral("error"),
                  QString::fromUtf8("测试没有成功（%1），示例播报改用本机语音播放。")
                      .arg(errorReason(code)));
    }
    Q_EMIT changed();
    maybeSyncVoice();
}

void SpeechController::setResult(const QString &state, const QString &text)
{
    m_resultState = state;
    m_resultText = text;
}

void SpeechController::setSelectedVoice(const QString &id)
{
    if (m_selectedVoice == id)
        return;
    m_selectedVoice = id;
    maybeSyncVoice();
}

bool SpeechController::isRefusal(const QString &code)
{
    return code == QLatin1String("ERR_BAD_REQUEST") || code == QLatin1String("ERR_UNKNOWN_MESSAGE")
           || code == QLatin1String("ERR_UNSUPPORTED")
           || code == QLatin1String("ERR_SPEECH_NOT_CONFIGURED");
}

void SpeechController::maybeSyncVoice()
{
    if (!available() || m_busy || !m_backend)
        return;
    const QString provider = providerOfVoiceId(m_selectedVoice);
    if (provider.isEmpty() || provider != m_provider)
        return;
    const QString name = voiceNameOf(m_selectedVoice);
    if (name == voice() || m_syncedVoice == m_selectedVoice)
        return;
    // Tried once per selection: a refusal must not turn into a request loop.
    m_syncedVoice = m_selectedVoice;
    send({{QStringLiteral("voice"), name}}, QString(), QString());
}

} // namespace mr
