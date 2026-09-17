#include "TtsService.h"

#include "AppSettings.h"
#include "SpeechController.h"
#include "SpeechPlayer.h"

#include <QLocale>
#include <QLoggingCategory>
#include <QStringList>
#include <QTextToSpeech>
#include <QVoice>

namespace {

Q_LOGGING_CATEGORY(lcTts, "mr.tts")

} // namespace

namespace mr {

TtsService::TtsService(AppSettings *settings, QObject *parent, EngineMode engineMode)
    : QObject(parent)
    , m_settings(settings)
{
    m_replyTimer.setSingleShot(true);
    connect(&m_replyTimer, &QTimer::timeout, this, [this] {
        if (m_phase != OnlinePhase::Synthesizing)
            return;
        // A late answer belongs to a sentence that has already been spoken.
        ++m_onlineGeneration;
        fallBackToLocal(QStringLiteral("ERR_SPEECH_TIMEOUT"));
    });
    m_localTimer.setSingleShot(true);
    connect(&m_localTimer, &QTimer::timeout, this, [this] {
        if (m_phase == OnlinePhase::SpeakingLocal)
            finishCurrent(false);
    });
    if (m_settings)
        connect(m_settings, &AppSettings::ttsChanged, this, &TtsService::onSettingsChanged);

    if (engineMode == EngineMode::None) {
        m_statusText = QString::fromUtf8("本机没有可用的语音引擎，播报已停用。");
        return;
    }
    if (QTextToSpeech::availableEngines().isEmpty()) {
        m_statusText = QString::fromUtf8("本机没有可用的语音引擎，播报已停用。");
        qCInfo(lcTts) << "no speech engine available; announcements disabled";
        return;
    }

    auto *engine = new QTextToSpeech(this);
    if (engine->state() == QTextToSpeech::Error) {
        m_statusText = QString::fromUtf8("语音引擎初始化失败：%1").arg(engine->errorString());
        qCWarning(lcTts) << "engine error" << engine->errorString();
        delete engine;
        return;
    }

    m_engine = engine;

    // SAPI enumerates its voices asynchronously: right after the constructor the
    // engine is not yet Ready and availableVoices() is empty, so the selection is
    // re-applied every time the engine reports Ready.
    connect(engine, &QTextToSpeech::stateChanged, this,
            [this](QTextToSpeech::State) { handleEngineState(); });
    connect(engine, &QTextToSpeech::errorOccurred, this,
            [this](QTextToSpeech::ErrorReason, const QString &errorString) {
                m_errorText = errorString;
                qCWarning(lcTts) << "engine error" << errorString;
                handleEngineState();
            });

    handleEngineState();
}

TtsService::~TtsService()
{
    // Nothing may call back into a half-destroyed service.
    ++m_onlineGeneration;
    m_replyTimer.stop();
    m_localTimer.stop();
    if (m_player)
        m_player->disconnect(this);
}

void TtsService::onSettingsChanged()
{
    const QString before = voiceId();
    applyVoiceAndLevels();
    if (m_speech && m_settings)
        m_speech->setSelectedVoice(m_settings->ttsVoice());
    refreshAllVoices();
    // applyVoiceAndLevels announces this itself when there is an engine.
    if (!m_engine && voiceId() != before)
        Q_EMIT availabilityChanged();
}

void TtsService::refreshAllVoices()
{
    QVariantList rows = m_voices;
    if (m_speech)
        rows.append(m_speech->voiceRows(m_settings ? m_settings->ttsVoice() : QString()));
    if (rows == m_allVoices)
        return;
    m_allVoices = rows;
    Q_EMIT voicesChanged();
}

bool TtsService::isAvailable() const
{
    return m_engine != nullptr && m_errorText.isEmpty()
           && m_engine->state() != QTextToSpeech::Error;
}

void TtsService::handleEngineState()
{
    if (!m_engine)
        return;

    const QTextToSpeech::State state = m_engine->state();
    noteLocalEngineState(int(state));
    if (state == QTextToSpeech::Ready || state == QTextToSpeech::Speaking) {
        // Ready is the first moment the voice list is trustworthy, and it is
        // the only point at which a zh-CN voice that SAPI enumerated late can
        // still be picked. A recovered engine also clears the old error text.
        m_errorText.clear();
    }
    applyVoiceAndLevels();
}

bool TtsService::voiceApplicableInState(int state)
{
    // Ready and nothing else. Speaking and Synthesizing are the states the
    // engine is in while a line is being produced, Paused holds one, and Error
    // means there is nothing to configure.
    return state == int(QTextToSpeech::Ready);
}

void TtsService::refreshVoiceList()
{
    if (!m_engine || !m_engineVoices.isEmpty())
        return;

    const QLocale before = m_engine->locale();
    const QList<QVoice> all = m_engine->findVoices();
    // findVoices() may have walked the engine through every locale.
    if (m_engine->locale() != before)
        m_engine->setLocale(before);
    if (all.isEmpty())
        return;

    QList<VoiceEntry> entries;
    entries.reserve(all.size());
    for (const QVoice &voice : all)
        entries.append({voice.name(), voice.locale(), voice.gender()});
    const QList<VoiceEntry> ordered = orderVoices(entries);

    // The same order for the QVoice objects: match each ordered entry back to
    // the first unused voice with its name and locale.
    QList<QVoice> engineOrdered;
    QList<bool> used(all.size(), false);
    for (const VoiceEntry &entry : ordered) {
        for (qsizetype index = 0; index < all.size(); ++index) {
            if (!used.at(index) && all.at(index).name() == entry.name
                && all.at(index).locale() == entry.locale) {
                used[index] = true;
                engineOrdered.append(all.at(index));
                break;
            }
        }
    }

    m_voiceEntries = ordered;
    m_engineVoices = engineOrdered;
    m_voices = describeVoices(ordered);
    refreshAllVoices();
}

void TtsService::applyDefaultVoice()
{
    // Prefer a zh-CN voice; fall back to whatever the engine offers so the
    // feature still works on an English-only install.
    const QLocale chinese(QLocale::Chinese, QLocale::China);
    if (m_engine->locale() != chinese) {
        for (const QLocale &locale : m_engine->availableLocales()) {
            if (locale.language() == QLocale::Chinese
                && locale.territory() == QLocale::China) {
                m_engine->setLocale(locale);
                break;
            }
        }
    }
    const QList<QVoice> voices = m_engine->availableVoices();
    if (!voices.isEmpty()) {
        m_engine->setVoice(voices.constFirst());
        m_voiceName = voices.constFirst().name();
    }
}

void TtsService::applyVoiceSelection()
{
    m_selecting = true;
    refreshVoiceList();

    const QString saved = m_settings ? m_settings->ttsVoice() : QString();
    const int index = savedVoiceIndex(m_voiceEntries, saved);
    if (index >= 0 && index < m_engineVoices.size()) {
        const QVoice &voice = m_engineVoices.at(index);
        m_engine->setVoice(voice);
        m_voiceName = voice.name();
    } else {
        // Nothing saved, a voice that has been uninstalled, or an online
        // voice: today's default, without complaint. It is also the voice an
        // online sentence falls back to.
        if (!saved.isEmpty())
            qCDebug(lcTts) << "saved voice not available, using the default";
        applyDefaultVoice();
    }
    m_voiceId = m_voiceName.isEmpty() ? QString() : localVoiceId(m_voiceName);
    // Only a completed selection counts: with no voice list yet the choice is
    // tried again at the next Ready.
    if (!m_voiceName.isEmpty())
        m_appliedVoiceSetting = saved;
    m_selecting = false;
}

void TtsService::applyVoiceAndLevels()
{
    if (!m_engine || m_selecting)
        return;

    // The voice and the locale are only ever changed while the engine is idle:
    // the SAPI plugin answers setVoice by declaring itself Ready and starting the
    // next queued utterance, which cuts the line being spoken off mid-word.
    const bool choiceChanged = m_settings && m_settings->ttsVoice() != m_appliedVoiceSetting;
    if (!voiceApplicableInState(int(m_engine->state()))) {
        // Remembered rather than forced: handleEngineState runs again on the
        // next state change, and the first Ready after this applies it. A new
        // choice from the settings page waits here too.
        m_voicePending = true;
    } else if (m_voicePending || m_voiceName.isEmpty() || choiceChanged) {
        applyVoiceSelection();
        m_voicePending = false;
    }

    const int ratePercent = m_settings ? m_settings->ttsRate() : 100;
    const int volumePercent = m_settings ? m_settings->ttsVolume() : 80;
    m_engine->setRate(rateFromPercent(ratePercent));
    m_engine->setVolume(volumeFromPercent(volumePercent));

    // The status text says what actually happened rather than a flat 已就绪:
    // an engine that errored, or one that has no voice to speak with, cannot
    // produce sound and the settings page must not claim otherwise.
    if (!m_errorText.isEmpty()) {
        m_statusText = QString::fromUtf8("语音引擎出错：%1").arg(m_errorText);
    } else if (m_engine->state() == QTextToSpeech::Error) {
        m_statusText = QString::fromUtf8("语音引擎出错：%1").arg(m_engine->errorString());
    } else if (m_voiceName.isEmpty()) {
        m_statusText = QString::fromUtf8("正在等待系统语音列表…");
    } else {
        m_statusText = QString::fromUtf8("语音引擎已就绪 · %1").arg(m_voiceName);
    }
    Q_EMIT availabilityChanged();
}

// ---------------------------------------------------------------------------
// Pure helpers
// ---------------------------------------------------------------------------

QString TtsService::render(const QString &templateText, const QVariantMap &values)
{
    // One pass over the template, never over the substituted text: replacing
    // in a loop lets a value that happens to contain "{duty}" be substituted
    // again by the next key, which is a template injection through a duty name.
    QString out;
    out.reserve(templateText.size());
    qsizetype index = 0;
    while (index < templateText.size()) {
        const QChar character = templateText.at(index);
        if (character != QLatin1Char('{')) {
            out.append(character);
            ++index;
            continue;
        }
        const qsizetype close = templateText.indexOf(QLatin1Char('}'), index + 1);
        if (close < 0) {
            out.append(templateText.mid(index));
            break;
        }
        const QString key = templateText.mid(index + 1, close - index - 1);
        const auto it = values.constFind(key);
        // An unknown placeholder is copied through verbatim so a typo in the
        // settings page is audible instead of silently vanishing.
        out.append(it == values.constEnd() ? templateText.mid(index, close - index + 1)
                                           : it.value().toString());
        index = close + 1;
    }
    return out;
}

double TtsService::rateFromPercent(int percent)
{
    const double rate = (double(percent) - 100.0) / 100.0;
    return qBound(-1.0, rate, 1.0);
}

double TtsService::volumeFromPercent(int percent)
{
    return qBound(0.0, double(percent) / 100.0, 1.0);
}

namespace {

bool isMainlandChinese(const QLocale &locale)
{
    return locale.language() == QLocale::Chinese && locale.territory() == QLocale::China;
}

QString languageLabel(const QLocale &locale)
{
    switch (locale.language()) {
    case QLocale::Chinese:
        return isMainlandChinese(locale)
                   ? QString::fromUtf8("中文")
                   : QString::fromUtf8("中文 · %1").arg(locale.nativeTerritoryName());
    case QLocale::English:
        return QString::fromUtf8("英语");
    case QLocale::Japanese:
        return QString::fromUtf8("日语");
    case QLocale::Korean:
        return QString::fromUtf8("韩语");
    default:
        return locale.nativeLanguageName();
    }
}

QString genderLabel(QVoice::Gender gender)
{
    switch (gender) {
    case QVoice::Male:
        return QString::fromUtf8("男声");
    case QVoice::Female:
        return QString::fromUtf8("女声");
    default:
        return {};
    }
}

} // namespace

QString TtsService::localVoicePrefix()
{
    return QStringLiteral("local:");
}

QString TtsService::localVoiceId(const QString &name)
{
    return localVoicePrefix() + name;
}

QList<TtsService::VoiceEntry> TtsService::orderVoices(const QList<VoiceEntry> &voices)
{
    QList<VoiceEntry> ordered;
    ordered.reserve(voices.size());
    for (const VoiceEntry &voice : voices) {
        if (isMainlandChinese(voice.locale))
            ordered.append(voice);
    }
    for (const VoiceEntry &voice : voices) {
        if (!isMainlandChinese(voice.locale))
            ordered.append(voice);
    }
    return ordered;
}

QString TtsService::voiceLabel(const VoiceEntry &voice)
{
    QStringList details;
    const QString language = languageLabel(voice.locale);
    if (!language.isEmpty())
        details.append(language);
    const QString gender = genderLabel(voice.gender);
    if (!gender.isEmpty())
        details.append(gender);
    if (details.isEmpty())
        return voice.name;
    return QString::fromUtf8("%1（%2）").arg(voice.name, details.join(QString::fromUtf8(" · ")));
}

QVariantList TtsService::describeVoices(const QList<VoiceEntry> &ordered)
{
    QVariantList rows;
    rows.reserve(ordered.size());
    for (const VoiceEntry &voice : ordered) {
        rows.append(QVariantMap{
            {QStringLiteral("id"), localVoiceId(voice.name)},
            {QStringLiteral("name"), voice.name},
            {QStringLiteral("label"), voiceLabel(voice)},
            {QStringLiteral("locale"), voice.locale.name(QLocale::TagSeparator::Dash)},
            {QStringLiteral("provider"), QStringLiteral("local")},
            {QStringLiteral("group"), QStringLiteral("local")},
        });
    }
    return rows;
}

int TtsService::savedVoiceIndex(const QList<VoiceEntry> &ordered, const QString &savedId)
{
    if (!savedId.startsWith(localVoicePrefix()))
        return -1;
    const QString name = savedId.mid(localVoicePrefix().size());
    if (name.isEmpty())
        return -1;
    for (qsizetype index = 0; index < ordered.size(); ++index) {
        if (ordered.at(index).name == name)
            return int(index);
    }
    return -1;
}

QString TtsService::voiceId() const
{
    // A choice that is installed but still waiting for the engine to fall
    // idle is already the answer: the settings page must not snap back to the
    // previous voice while the current line finishes.
    const QString saved = m_settings ? m_settings->ttsVoice() : QString();
    // An online voice is the answer while the Collector offers online speech;
    // without it the list has no such row and the local voice in use is shown.
    if (SpeechController::isOnlineVoiceId(saved) && m_speech && m_speech->available())
        return saved;
    if (savedVoiceIndex(m_voiceEntries, saved) >= 0)
        return saved;
    return m_voiceId;
}

bool TtsService::onlineVoiceSelected() const
{
    return m_settings && SpeechController::isOnlineVoiceId(m_settings->ttsVoice());
}

void TtsService::setVoice(const QString &id)
{
    const QString trimmed = id.trimmed();
    if (!trimmed.isEmpty() && !trimmed.startsWith(localVoicePrefix())
        && !SpeechController::isOnlineVoiceId(trimmed)) {
        qCDebug(lcTts) << "ignoring a voice id of an unknown provider";
        return;
    }
    if (trimmed == localVoicePrefix()) {
        qCDebug(lcTts) << "ignoring a local voice id without a name";
        return;
    }
    if (!m_settings)
        return;
    // AppSettings::ttsChanged -> applyVoiceAndLevels applies it, or queues it
    // until the engine is Ready.
    m_settings->setTtsVoice(trimmed);
}

// ---------------------------------------------------------------------------
// Speaking
// ---------------------------------------------------------------------------

QString TtsService::templateFor(const QString &kind) const
{
    if (!m_settings)
        return {};
    if (kind == QLatin1String("matched"))
        return m_settings->templateMatched();
    if (kind == QLatin1String("entered"))
        return m_settings->templateEntered();
    if (kind == QLatin1String("completed"))
        return m_settings->templateCompleted();
    if (kind == QLatin1String("finished"))
        return m_settings->templateFinished();
    if (kind == QLatin1String("aborted"))
        return m_settings->templateAborted();
    return {};
}

void TtsService::deliver(const QString &kind, const QString &text, bool force, bool reportsTest)
{
    if (text.trimmed().isEmpty())
        return;
    if (!force && m_settings && !m_settings->ttsEnabled())
        return;

    // The signal fires even without an engine so the UI can still show what
    // *would* have been said; only the audio output is conditional.
    Q_EMIT spoke(kind, text);

    if (reportsTest || onlineVoiceSelected()) {
        // 试听 / 测试 skip the Collector's cache so a request is really sent.
        Utterance utterance;
        utterance.kind = kind;
        utterance.text = text;
        utterance.test = force;
        utterance.reportsTest = reportsTest;
        enqueueOnline(utterance, force || kind == QLatin1String("custom"));
        return;
    }

    Q_EMIT spokeVia(kind, text, QStringLiteral("local"));
    if (!m_engine)
        return;

    // 试听 and the free-form speak() replace whatever is playing: they are a
    // direct answer to a click. Run announcements queue instead, because
    // 进本 follows 匹配 by a second or two and stopping would cut the first
    // line off mid-word.
    if (force || kind == QLatin1String("custom")) {
        m_engine->stop();
        m_engine->say(text);
        return;
    }
    m_engine->enqueue(text);
}

void TtsService::speak(const QString &text)
{
    deliver(QStringLiteral("custom"), text, false);
}

void TtsService::announce(const QString &kind, const QVariantMap &values)
{
    deliver(kind, render(templateFor(kind), values), false);
}

void TtsService::announceText(const QString &kind, const QString &text)
{
    deliver(kind, text, false);
}

void TtsService::setContextValues(const QVariantMap &values)
{
    m_contextValues = values;
}

QString TtsService::previewText(const QString &kind) const
{
    // Real context when the controller has provided one; neutral zeros
    // otherwise. Never a made-up sample that could be mistaken for progress.
    QVariantMap values = m_contextValues;
    if (!values.contains(QStringLiteral("duty")))
        values.insert(QStringLiteral("duty"), QString::fromUtf8("未知副本"));
    if (!values.contains(QStringLiteral("progress")))
        values.insert(QStringLiteral("progress"), 0);
    if (!values.contains(QStringLiteral("remaining")))
        values.insert(QStringLiteral("remaining"), 0);
    return render(templateFor(kind), values);
}

void TtsService::preview(const QString &kind)
{
    deliver(kind, previewText(kind), true);
}

void TtsService::testOnline()
{
    const QString text = previewText(QStringLiteral("finished"));
    if (text.trimmed().isEmpty()) {
        if (m_speech)
            m_speech->noteTestResult(false, QStringLiteral("DESKTOP_TEXT_EMPTY"), false);
        return;
    }
    deliver(QStringLiteral("finished"), text, true, true);
}

void TtsService::stop()
{
    cancelOnline(QStringLiteral("DESKTOP_CANCELLED"));
    m_onlineQueue.clear();
    if (m_engine)
        m_engine->stop();
}

} // namespace mr
