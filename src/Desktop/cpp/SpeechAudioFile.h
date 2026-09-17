#pragma once

// ---------------------------------------------------------------------------
// Which files the Desktop is willing to play for online speech.
//
// The Collector answers SynthesizeSpeech with audio_path, an absolute path of
// <data dir>\tts-cache\<64 hex>.wav ($defs/SynthesizeSpeech). The Desktop plays
// nothing else (docs/privacy-boundary.md §8.3): the data directory is the
// directory of GetStatus.database_path - the same Path.GetDirectoryName the
// Collector builds tts-cache\ from (src/Collector/Ipc/CollectorHost.cs) - and
// the file must lie directly in its tts-cache folder, both as written and
// after every link is resolved, and be a real RIFF/WAVE 16-bit PCM file.
// ---------------------------------------------------------------------------

#include <QString>

namespace mr {

struct SpeechAudioCheck
{
    bool ok = false;
    /// The canonical path to hand to the player.
    QString path;
    /// Length the WAV header announces, in milliseconds.
    int durationMs = 0;
    /// Why the path was refused; for the log only, never shown with the path.
    QString reason;
};

/// Longest file accepted: the Collector refuses responses over 5 MB.
constexpr qint64 kMaxSpeechAudioBytes = 5 * 1024 * 1024 + 1024;

/// <dir of databasePath>/tts-cache, cleaned, or empty when \a databasePath is
/// empty or not absolute (a relative path would resolve against this
/// process's working directory, not the Collector's).
QString speechCacheDirectory(const QString &databasePath);

/// Validates \a audioPath against the cache directory of \a databasePath.
/// Refuses: an empty, relative, device (\\?\ \\.\) or ".." path; a name that
/// is not <64 hex>.wav; a file outside the cache folder as written or once
/// resolved (other drive, junction, symbolic link); a cache folder that is
/// itself a link; anything that is not an existing regular file of sane size;
/// a header that is not RIFF/WAVE PCM 16-bit.
SpeechAudioCheck checkSpeechAudioFile(const QString &audioPath, const QString &databasePath);

/// Reads the RIFF/WAVE header of \a path. Returns false with \a reason set
/// when it is not 16-bit PCM; \a durationMs is the data chunk's length.
bool readPcmWaveHeader(const QString &path, int *durationMs, QString *reason);

} // namespace mr
