#include "SpeechPlayer.h"

#include "SpeechAudioFile.h"

#include <QAudioDevice>
#include <QCoreApplication>
#include <QElapsedTimer>
#include <QEventLoop>
#include <QFileInfo>
#include <QLoggingCategory>
#include <QMediaDevices>
#include <QSoundEffect>
#include <QUrl>

#include <cstdio>

namespace {

Q_LOGGING_CATEGORY(lcSpeechPlayer, "mr.tts.player")

/// Slack past the announced length before a sentence is considered over.
constexpr int kWatchdogSlackMs = 4000;
/// A sentence whose header announces nothing still gets this long.
constexpr int kMinimumWatchdogMs = 6000;

} // namespace

namespace mr {

SpeechPlayer::~SpeechPlayer() = default;

// ---------------------------------------------------------------------------
// PlaybackEndTracker
// ---------------------------------------------------------------------------

PlaybackEndTracker::PlaybackEndTracker(QObject *parent)
    : QObject(parent)
{
    m_settle.setSingleShot(true);
    m_settle.setInterval(kSettleMs);
    connect(&m_settle, &QTimer::timeout, this, &PlaybackEndTracker::end);
}

void PlaybackEndTracker::start(int expectedDurationMs)
{
    m_settle.stop();
    m_expectedMs = qMax(0, expectedDurationMs);
    m_active = true;
    m_started = false;
    m_clock.invalidate();
}

void PlaybackEndTracker::notePlaying(bool playing)
{
    if (!m_active)
        return;
    if (playing) {
        // A stop that turned out to be a pause while buffering.
        m_settle.stop();
        if (!m_started) {
            m_started = true;
            m_clock.start();
        }
        return;
    }
    if (!m_started)
        return;
    if (m_clock.elapsed() >= qint64(m_expectedMs) - kEndMarginMs) {
        end();
        return;
    }
    // Early: only a stop that lasts is the end (the player's watchdog still
    // bounds the whole sentence).
    if (!m_settle.isActive())
        m_settle.start();
}

void PlaybackEndTracker::cancel()
{
    m_active = false;
    m_settle.stop();
}

void PlaybackEndTracker::end()
{
    if (!m_active || !m_started)
        return;
    m_active = false;
    m_settle.stop();
    Q_EMIT ended();
}

// ---------------------------------------------------------------------------
// SoundEffectPlayer
// ---------------------------------------------------------------------------

SoundEffectPlayer::SoundEffectPlayer(QObject *parent)
    : SpeechPlayer(parent)
{
    connect(&m_tracker, &PlaybackEndTracker::ended, this, [this] { finish(true, {}); });
    m_watchdog.setSingleShot(true);
    connect(&m_watchdog, &QTimer::timeout, this, [this] {
        if (!m_active)
            return;
        // Some audio stacks never report the end of a short effect. A sentence
        // that did start has been heard; one that never started has not.
        if (m_tracker.started()) {
            qCDebug(lcSpeechPlayer) << "no end-of-playback signal; treating the sentence as played";
            finish(true, {});
        } else {
            qCWarning(lcSpeechPlayer) << "playback never started";
            finish(false, QString::fromUtf8("声音没有开始播放"));
        }
    });
}

SoundEffectPlayer::~SoundEffectPlayer()
{
    release();
}

void SoundEffectPlayer::play(const QString &path, double volume, int expectedDurationMs)
{
    release();
    m_active = true;
    m_tracker.start(expectedDurationMs);

    if (QMediaDevices::defaultAudioOutput().isNull()) {
        // Queued so the caller always observes finished() after play() returns.
        QMetaObject::invokeMethod(this, [this] {
            finish(false, QString::fromUtf8("没有可用的音频输出设备"));
        }, Qt::QueuedConnection);
        return;
    }

    m_effect = new QSoundEffect(this);
    connect(m_effect, &QSoundEffect::statusChanged, this, &SoundEffectPlayer::onStatusChanged);
    connect(m_effect, &QSoundEffect::playingChanged, this, &SoundEffectPlayer::onPlayingChanged);
    m_effect->setVolume(float(qBound(0.0, volume, 1.0)));
    m_effect->setLoopCount(1);
    m_watchdog.start(qMax(kMinimumWatchdogMs, expectedDurationMs + kWatchdogSlackMs));
    // Opened by path again here, after checkSpeechAudioFile() looked at it. The
    // gap is accepted: whoever could swap the file in between can already
    // write to the data directory, i.e. is on the Collector's side of the
    // trust boundary, and what is opened is only ever played locally.
    m_effect->setSource(QUrl::fromLocalFile(path));
    // A source that was already loaded does not announce Ready again.
    if (m_effect->status() == QSoundEffect::Ready || m_effect->status() == QSoundEffect::Error)
        QMetaObject::invokeMethod(this, &SoundEffectPlayer::onStatusChanged, Qt::QueuedConnection);
}

void SoundEffectPlayer::stop()
{
    release();
}

void SoundEffectPlayer::onStatusChanged()
{
    if (!m_active || !m_effect)
        return;
    switch (m_effect->status()) {
    case QSoundEffect::Ready:
        if (!m_effect->isPlaying() && !m_tracker.started())
            m_effect->play();
        break;
    case QSoundEffect::Error:
        finish(false, QString::fromUtf8("无法读取声音文件"));
        break;
    default:
        break;
    }
}

void SoundEffectPlayer::onPlayingChanged()
{
    if (!m_active || !m_effect)
        return;
    m_tracker.notePlaying(m_effect->isPlaying());
}

void SoundEffectPlayer::finish(bool ok, const QString &error)
{
    if (!m_active)
        return;
    release();
    Q_EMIT finished(ok, error);
}

void SoundEffectPlayer::release()
{
    m_active = false;
    m_watchdog.stop();
    m_tracker.cancel();
    if (m_effect) {
        m_effect->disconnect(this);
        m_effect->stop();
        m_effect->deleteLater();
        m_effect = nullptr;
    }
}

int runSpeechSelfTest(const QString &path)
{
    const QFileInfo info(path);
    if (!info.isFile()) {
        std::fprintf(stderr, "speech selftest: no such file: %s\n", qPrintable(path));
        return 3;
    }
    int durationMs = 0;
    QString reason;
    if (!readPcmWaveHeader(info.absoluteFilePath(), &durationMs, &reason)) {
        std::fprintf(stderr, "speech selftest: not a 16-bit PCM WAV (%s): %s\n",
                     qPrintable(reason), qPrintable(path));
        return 3;
    }

    const QAudioDevice device = QMediaDevices::defaultAudioOutput();
    std::fprintf(stdout, "speech selftest: QT_MEDIA_BACKEND=%s, default output=%s\n",
                 qEnvironmentVariable("QT_MEDIA_BACKEND", QStringLiteral("<unset>")).toUtf8().constData(),
                 device.isNull() ? "<none>" : device.description().toUtf8().constData());
    std::fflush(stdout);
    if (device.isNull()) {
        // Says nothing about the package: this machine has nowhere to play to.
        std::fprintf(stderr, "speech selftest: no audio output device on this machine\n");
        return 6;
    }

    SoundEffectPlayer player;
    bool done = false;
    bool ok = false;
    QString error;
    QObject::connect(&player, &SpeechPlayer::finished, &player,
                     [&](bool success, const QString &message) {
        done = true;
        ok = success;
        error = message;
    });

    QElapsedTimer clock;
    clock.start();
    player.play(info.absoluteFilePath(), 0.0, durationMs);
    while (!done && clock.elapsed() < durationMs + 15000) {
        QEventLoop loop;
        QTimer::singleShot(20, &loop, &QEventLoop::quit);
        loop.exec();
    }

    if (!done) {
        std::fprintf(stderr, "speech selftest: no answer from the player\n");
        return 5;
    }
    if (!ok || !player.started()) {
        std::fprintf(stderr, "speech selftest: playback failed: %s\n",
                     error.isEmpty() ? "never started" : error.toUtf8().constData());
        return 4;
    }
    std::fprintf(stdout, "speech selftest: played %d ms of audio in %lld ms: ok\n", durationMs,
                 static_cast<long long>(clock.elapsed()));
    return 0;
}

} // namespace mr
