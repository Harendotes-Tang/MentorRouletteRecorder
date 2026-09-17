#pragma once

// ---------------------------------------------------------------------------
// Playback of one online-speech sentence.
//
// The Collector fetches the audio and writes it to <data dir>\tts-cache\ as a
// canonical 16-bit PCM WAV; the Desktop only plays that local file. WAV needs
// no decoder, so QSoundEffect is enough - and with QT_MEDIA_BACKEND=windows
// (main.cpp) the package ships the Windows Media Foundation backend only, not
// the FFmpeg one (scripts/package.ps1).
//
// SpeechPlayer is the seam TtsService talks to, so the routing can be tested
// with a fake player on a machine without an audio device.
// ---------------------------------------------------------------------------

#include <QElapsedTimer>
#include <QObject>
#include <QString>
#include <QTimer>

class QSoundEffect;

namespace mr {

class SpeechPlayer : public QObject
{
    Q_OBJECT

public:
    using QObject::QObject;
    ~SpeechPlayer() override;

    /// Play the local WAV at \a path with \a volume in [0, 1]. Exactly one
    /// finished() follows unless stop() comes first. \a expectedDurationMs is
    /// the length the WAV header announces; it bounds the wait for the end.
    virtual void play(const QString &path, double volume, int expectedDurationMs) = 0;
    /// Stop what is playing. No finished() is emitted for it.
    virtual void stop() = 0;

Q_SIGNALS:
    /// \a error is a short Chinese reason when \a ok is false.
    void finished(bool ok, const QString &error);
};

/// Decides from QSoundEffect's playingChanged edges when a sentence is over.
///
/// QSoundEffect can report playing == false for a moment while it is still
/// buffering, then true again; taking that first false as the end would start
/// the next sentence on top of this one. A stop counts only once the sentence
/// has run for (almost) the length its header announces, or once playback has
/// stayed stopped for kSettleMs.
class PlaybackEndTracker final : public QObject
{
    Q_OBJECT

public:
    /// How long a stop before the announced end must last to count.
    static constexpr int kSettleMs = 150;
    /// A stop this close to the announced end counts at once.
    static constexpr int kEndMarginMs = 100;

    explicit PlaybackEndTracker(QObject *parent = nullptr);

    /// A new sentence of \a expectedDurationMs begins; forgets the last one.
    void start(int expectedDurationMs);
    /// playingChanged, with the new value.
    void notePlaying(bool playing);
    /// Nothing more is reported for the current sentence.
    void cancel();
    /// Playback was seen running at least once.
    bool started() const { return m_started; }

Q_SIGNALS:
    /// Once per start(), after it had started.
    void ended();

private:
    void end();

    QElapsedTimer m_clock;
    QTimer m_settle;
    int m_expectedMs = 0;
    bool m_active = false;
    bool m_started = false;
};

/// The shipping player: one QSoundEffect per sentence.
class SoundEffectPlayer final : public SpeechPlayer
{
    Q_OBJECT

public:
    explicit SoundEffectPlayer(QObject *parent = nullptr);
    ~SoundEffectPlayer() override;

    void play(const QString &path, double volume, int expectedDurationMs) override;
    void stop() override;

    /// What the last play() went through, for --speech-selftest.
    bool started() const { return m_tracker.started(); }

private:
    void onStatusChanged();
    void onPlayingChanged();
    void finish(bool ok, const QString &error);
    void release();

    QSoundEffect *m_effect = nullptr;
    QTimer m_watchdog;
    PlaybackEndTracker m_tracker;
    bool m_active = false;
};

/// --speech-selftest <wav>: plays \a path through SoundEffectPlayer, exactly as
/// an online sentence is played, and reports on stdout. 0 when playback
/// started and finished; 3 not a 16-bit PCM WAV; 4 playback failed; 5 no
/// answer; 6 the machine has no audio output device. Needs a QCoreApplication.
int runSpeechSelfTest(const QString &path);

} // namespace mr
