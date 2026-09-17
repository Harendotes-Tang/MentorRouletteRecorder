// ---------------------------------------------------------------------------
// TtsService, online route.
//
// One sentence at a time:
//
//   Synthesizing   SynthesizeSpeech is on the wire; m_replyTimer bounds it.
//   Playing        the Collector's WAV, checked by checkSpeechAudioFile, is
//                  playing through the SpeechPlayer.
//   SpeakingLocal  something failed; the engine says the same sentence with
//                  the local default voice. The next sentence waits for the
//                  engine to fall idle again (or m_localTimer).
//
// Every sentence ends in exactly one spokeVia(); none is dropped. A 试听 or a
// free-form line interrupts the sentence in progress, as it does on the local
// route, and queued run announcements are still spoken after it.
// ---------------------------------------------------------------------------

#include "TtsService.h"

#include "AppSettings.h"
#include "IBackend.h"
#include "SpeechAudioFile.h"
#include "SpeechController.h"
#include "SpeechPlayer.h"

#include <QLoggingCategory>
#include <QTextToSpeech>

#include <utility>

namespace {

Q_LOGGING_CATEGORY(lcTtsOnline, "mr.tts.online")

/// SynthesizeSpeechRequest.text: at most 200 code points.
constexpr qsizetype kMaxOnlineTextLength = 200;

/// How long a fallback sentence may take before the queue moves on anyway:
/// generous for a slow voice, bounded for an engine that never reports back.
int localSpeechBudgetMs(const QString &text)
{
    return qBound(3000, 2000 + int(text.size()) * 450, 60000);
}

/// The codes a toast is keyed by. Everything the reason text cannot tell
/// apart (a disconnected pipe, an internal error, no settings yet) shares one.
QString toastKey(const QString &code)
{
    static const QStringList kKnown{
        QStringLiteral("ERR_SPEECH_DISABLED"),      QStringLiteral("ERR_SPEECH_NOT_CONFIGURED"),
        QStringLiteral("ERR_SPEECH_AUTH"),          QStringLiteral("ERR_SPEECH_QUOTA"),
        QStringLiteral("ERR_SPEECH_NETWORK"),       QStringLiteral("ERR_SPEECH_TIMEOUT"),
        QStringLiteral("ERR_SPEECH_FORMAT"),        QStringLiteral("ERR_UNKNOWN_MESSAGE"),
        QStringLiteral("DESKTOP_AUDIO_REJECTED"),   QStringLiteral("DESKTOP_PLAYBACK_FAILED"),
        QStringLiteral("DESKTOP_TEXT_TOO_LONG"),    QStringLiteral("DESKTOP_PROVIDER_MISMATCH"),
    };
    return kKnown.contains(code) ? code : QStringLiteral("OTHER");
}

} // namespace

namespace mr {

void TtsService::setBackend(IBackend *backend)
{
    m_backend = backend;
}

void TtsService::setSpeech(SpeechController *speech)
{
    if (m_speech == speech)
        return;
    if (m_speech)
        m_speech->disconnect(this);
    m_speech = speech;
    if (m_speech) {
        connect(m_speech, &SpeechController::changed, this, [this] {
            refreshAllVoices();
            // voiceId depends on whether online speech is offered.
            Q_EMIT availabilityChanged();
        });
        connect(m_speech, &SpeechController::testRequested, this, &TtsService::testOnline);
        if (m_settings)
            m_speech->setSelectedVoice(m_settings->ttsVoice());
    }
    refreshAllVoices();
    Q_EMIT availabilityChanged();
}

void TtsService::setPlayer(SpeechPlayer *player)
{
    if (m_player == player)
        return;
    if (m_player) {
        m_player->stop();
        m_player->disconnect(this);
        m_player->deleteLater();
    }
    m_player = player;
    if (m_player) {
        m_player->setParent(this);
        connect(m_player, &SpeechPlayer::finished, this, &TtsService::handlePlaybackFinished);
    }
}

SpeechPlayer *TtsService::player()
{
    if (!m_player)
        setPlayer(new SoundEffectPlayer(this));
    return m_player;
}

void TtsService::setCollectorDatabasePath(const QString &path)
{
    m_databasePath = path;
}

int TtsService::pendingOnlineCount() const
{
    return int(m_onlineQueue.size()) + (m_current ? 1 : 0);
}

QString TtsService::fallbackToast(const QString &code)
{
    return QString::fromUtf8("在线语音暂不可用（%1），本次改用本机语音")
        .arg(SpeechController::errorReason(code));
}

void TtsService::enqueueOnline(const Utterance &utterance, bool replace)
{
    if (replace) {
        // A click answers now: what is playing stops (as on the local route),
        // earlier clicks are superseded, and run announcements stay in line -
        // including one that was still waiting for its audio and so has not
        // been heard at all.
        std::optional<Utterance> unheard;
        if (m_current && !m_current->test && m_phase == OnlinePhase::Synthesizing)
            unheard = *m_current;
        cancelOnline(QStringLiteral("DESKTOP_CANCELLED"));
        m_onlineQueue.prepend(utterance);
        if (unheard)
            m_onlineQueue.insert(1, *unheard);
    } else {
        m_onlineQueue.append(utterance);
    }
    pumpOnline();
}

void TtsService::cancelOnline(const QString &code)
{
    QList<Utterance> kept;
    for (const Utterance &queued : std::as_const(m_onlineQueue)) {
        if (queued.test) {
            if (queued.reportsTest && m_speech)
                m_speech->noteTestResult(false, code, false);
            continue;
        }
        kept.append(queued);
    }
    m_onlineQueue = kept;

    if (!m_current)
        return;
    const bool wasLocal = m_phase == OnlinePhase::SpeakingLocal;
    if (m_current->reportsTest && m_speech)
        m_speech->noteTestResult(false, code, false);
    ++m_onlineGeneration;
    m_replyTimer.stop();
    m_localTimer.stop();
    if (m_player)
        m_player->stop();
    if (wasLocal && m_engine)
        m_engine->stop();
    m_current.reset();
    m_phase = OnlinePhase::Idle;
}

void TtsService::schedulePump()
{
    if (m_pumpScheduled)
        return;
    m_pumpScheduled = true;
    QMetaObject::invokeMethod(this, [this] {
        m_pumpScheduled = false;
        pumpOnline();
    }, Qt::QueuedConnection);
}

QString TtsService::onlinePrecondition() const
{
    if (!m_backend || !m_speech || !m_speech->loaded())
        return QStringLiteral("DESKTOP_NOT_READY");
    if (!m_speech->supported())
        return QStringLiteral("ERR_UNKNOWN_MESSAGE");
    // The Collector speaks with the service it has in force. A voice of the
    // other service must not come out in that one's voice.
    const QString selected = m_settings ? m_settings->ttsVoice() : QString();
    if (SpeechController::providerOfVoiceId(selected) != m_speech->provider())
        return QStringLiteral("DESKTOP_PROVIDER_MISMATCH");
    return {};
}

void TtsService::pumpOnline()
{
    if (m_phase != OnlinePhase::Idle || m_current || m_onlineQueue.isEmpty())
        return;
    m_current = m_onlineQueue.takeFirst();
    m_phase = OnlinePhase::Synthesizing;
    const quint64 generation = ++m_onlineGeneration;

    QString refusal = onlinePrecondition();
    const QString text = m_current->text.trimmed();
    if (refusal.isEmpty() && text.toUcs4().size() > kMaxOnlineTextLength)
        refusal = QStringLiteral("DESKTOP_TEXT_TOO_LONG");
    if (!refusal.isEmpty()) {
        fallBackToLocal(refusal);
        return;
    }

    const int rate = m_settings ? m_settings->ttsRate() : 100;
    m_replyTimer.start(m_onlineReplyTimeoutMs);
    // whenDone may answer before this returns (a pipe that is already down).
    m_backend->synthesizeSpeech(text, rate, m_current->test)->whenDone(this,
        [this, generation](bool ok, const QVariantMap &payload, const QString &code, const QString &) {
            handleSynthesis(generation, ok, payload, code);
        });
}

void TtsService::handleSynthesis(quint64 generation, bool ok, const QVariantMap &payload,
                                 const QString &code)
{
    if (generation != m_onlineGeneration || m_phase != OnlinePhase::Synthesizing || !m_current)
        return;
    m_replyTimer.stop();
    if (!ok) {
        fallBackToLocal(code.isEmpty() ? QStringLiteral("ERR_INTERNAL") : code);
        return;
    }

    const SpeechAudioCheck check = checkSpeechAudioFile(
        payload.value(QStringLiteral("audio_path")).toString(), m_databasePath);
    if (!check.ok) {
        // The reason, never the path: it names the user's profile directory.
        qCWarning(lcTtsOnline) << "refused the returned audio file:" << check.reason;
        fallBackToLocal(QStringLiteral("DESKTOP_AUDIO_REJECTED"));
        return;
    }

    m_phase = OnlinePhase::Playing;
    qCDebug(lcTtsOnline) << "playing the online sentence," << check.durationMs << "ms";
    Q_EMIT spokeVia(m_current->kind, m_current->text, QStringLiteral("online"));
    const int volume = m_settings ? m_settings->ttsVolume() : 80;
    player()->play(check.path, volumeFromPercent(volume), check.durationMs);
}

void TtsService::handlePlaybackFinished(bool ok, const QString &error)
{
    if (m_phase != OnlinePhase::Playing || !m_current)
        return;
    if (ok) {
        finishCurrent(true);
        return;
    }
    qCWarning(lcTtsOnline) << "playback failed:" << error;
    fallBackToLocal(QStringLiteral("DESKTOP_PLAYBACK_FAILED"));
}

void TtsService::noteFallback(const QString &code)
{
    Q_EMIT onlineFallback(code);
    const QString key = toastKey(code);
    if (m_toastedCodes.contains(key))
        return;
    m_toastedCodes.insert(key);
    Q_EMIT toastRequested(fallbackToast(code));
}

void TtsService::fallBackToLocal(const QString &code)
{
    if (!m_current)
        return;
    const Utterance utterance = *m_current;
    qCInfo(lcTtsOnline) << "speaking locally instead:" << code;
    noteFallback(code);
    if (utterance.reportsTest && m_speech)
        m_speech->noteTestResult(false, code, false);

    m_phase = OnlinePhase::SpeakingLocal;
    m_localStarted = false;
    Q_EMIT spokeVia(utterance.kind, utterance.text, QStringLiteral("local"));
    if (!m_engine || !isAvailable()) {
        finishCurrent(false);
        return;
    }
    m_localTimer.start(localSpeechBudgetMs(utterance.text));
    m_engine->say(utterance.text);
    noteLocalEngineState(int(m_engine->state()));
}

void TtsService::noteLocalEngineState(int state)
{
    if (m_phase != OnlinePhase::SpeakingLocal)
        return;
    if (state == int(QTextToSpeech::Speaking) || state == int(QTextToSpeech::Synthesizing)) {
        m_localStarted = true;
        return;
    }
    if (state == int(QTextToSpeech::Error)
        || (state == int(QTextToSpeech::Ready) && m_localStarted)) {
        finishCurrent(false);
    }
}

void TtsService::finishCurrent(bool spokenOnline)
{
    if (!m_current)
        return;
    if (spokenOnline && m_current->reportsTest && m_speech)
        m_speech->noteTestResult(true, QString(), true);
    m_current.reset();
    m_phase = OnlinePhase::Idle;
    m_localStarted = false;
    m_replyTimer.stop();
    m_localTimer.stop();
    schedulePump();
}

} // namespace mr
