#include "SpeechAudioFile.h"

#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QRegularExpression>
#include <QtEndian>

namespace mr {
namespace {

/// Windows paths compare without regard to case.
bool samePath(const QString &left, const QString &right)
{
    return !left.isEmpty()
           && QString::compare(QDir::cleanPath(left), QDir::cleanPath(right), Qt::CaseInsensitive) == 0;
}

/// A symbolic link, a shortcut or a junction: anything that makes the path as
/// written point somewhere else. An NTFS hard link is not detectable this way
/// and is accepted: only something that can already write into the data
/// directory - the Collector's side of the trust boundary - can create one,
/// and the file must still pass the name, size and RIFF/WAVE 16-bit PCM checks
/// before it is played, locally, and never read for anything else.
bool isLink(const QFileInfo &info)
{
    return info.isSymLink() || info.isJunction();
}

bool hasParentSegment(const QString &path)
{
    const QStringList segments = path.split(QLatin1Char('/'));
    for (const QString &segment : segments) {
        if (segment == QLatin1String(".."))
            return true;
    }
    return false;
}

SpeechAudioCheck refuse(const QString &reason)
{
    SpeechAudioCheck check;
    check.reason = reason;
    return check;
}

quint16 u16(const QByteArray &bytes, qsizetype offset)
{
    return qFromLittleEndian<quint16>(bytes.constData() + offset);
}

quint32 u32(const QByteArray &bytes, qsizetype offset)
{
    return qFromLittleEndian<quint32>(bytes.constData() + offset);
}

} // namespace

QString speechCacheDirectory(const QString &databasePath)
{
    const QString database = QDir::fromNativeSeparators(databasePath.trimmed());
    if (database.isEmpty() || !QDir::isAbsolutePath(database) || hasParentSegment(database))
        return {};
    const QString directory = QFileInfo(QDir::cleanPath(database)).absolutePath();
    return QDir::cleanPath(directory + QStringLiteral("/tts-cache"));
}

SpeechAudioCheck checkSpeechAudioFile(const QString &audioPath, const QString &databasePath)
{
    const QString cacheDir = speechCacheDirectory(databasePath);
    if (cacheDir.isEmpty())
        return refuse(QStringLiteral("no absolute database_path to locate tts-cache"));

    // \\?\ and \\.\ paths bypass the normalisation every check below relies
    // on. Checked on the text as received: on Windows fromNativeSeparators()
    // quietly drops a \\?\ prefix.
    const QString received = audioPath.trimmed();
    for (const char *prefix : {"\\\\?\\", "\\\\.\\", "//?/", "//./", "\\??\\"}) {
        if (received.startsWith(QLatin1String(prefix)))
            return refuse(QStringLiteral("device path"));
    }
    const QString raw = QDir::fromNativeSeparators(received);
    if (raw.isEmpty())
        return refuse(QStringLiteral("empty audio_path"));
    if (!QDir::isAbsolutePath(raw))
        return refuse(QStringLiteral("relative path"));
    if (hasParentSegment(raw))
        return refuse(QStringLiteral("parent segment"));
    // A colon past the drive letter is an alternate data stream.
    if (raw.indexOf(QLatin1Char(':'), 2) >= 0)
        return refuse(QStringLiteral("stream or second drive in path"));

    const QString cleaned = QDir::cleanPath(raw);
    QFileInfo file(cleaned);
    static const QRegularExpression kName(QStringLiteral("^[0-9a-fA-F]{64}\\.[wW][aA][vV]$"));
    if (!kName.match(file.fileName()).hasMatch())
        return refuse(QStringLiteral("not a cache file name"));
    if (!samePath(file.absolutePath(), cacheDir))
        return refuse(QStringLiteral("outside tts-cache"));

    const QFileInfo folder(cacheDir);
    if (!folder.exists() || !folder.isDir() || isLink(folder))
        return refuse(QStringLiteral("tts-cache missing or a link"));
    if (!file.exists() || !file.isFile() || isLink(file))
        return refuse(QStringLiteral("missing, not a file, or a link"));
    if (file.size() <= 44 || file.size() > kMaxSpeechAudioBytes)
        return refuse(QStringLiteral("implausible size"));

    // Resolved: whatever the path says, the bytes must live in the resolved
    // cache folder too. The player opens the path again afterwards; that gap
    // is accepted for the same reason hard links are (see isLink()).
    const QString canonicalFile = file.canonicalFilePath();
    const QString canonicalDir = folder.canonicalFilePath();
    if (canonicalFile.isEmpty() || canonicalDir.isEmpty()
        || !samePath(QFileInfo(canonicalFile).absolutePath(), canonicalDir)) {
        return refuse(QStringLiteral("resolves outside tts-cache"));
    }

    SpeechAudioCheck check;
    QString reason;
    if (!readPcmWaveHeader(canonicalFile, &check.durationMs, &reason))
        return refuse(reason);
    check.ok = true;
    check.path = canonicalFile;
    return check;
}

bool readPcmWaveHeader(const QString &path, int *durationMs, QString *reason)
{
    const auto fail = [reason](const char *why) {
        if (reason)
            *reason = QString::fromLatin1(why);
        return false;
    };

    QFile file(path);
    if (!file.open(QIODevice::ReadOnly))
        return fail("cannot open");
    // The Collector writes a canonical 44-byte header; a few extra chunks
    // before "data" are tolerated, a header larger than this is not.
    const QByteArray head = file.read(4096);
    if (head.size() < 44 || !head.startsWith("RIFF") || head.mid(8, 4) != "WAVE")
        return fail("not RIFF/WAVE");

    quint32 byteRate = 0;
    bool pcm16 = false;
    bool sawFormat = false;
    qsizetype offset = 12;
    while (offset + 8 <= head.size()) {
        const QByteArray id = head.mid(offset, 4);
        const quint32 size = u32(head, offset + 4);
        const qsizetype body = offset + 8;
        if (id == "fmt ") {
            if (size < 16 || body + 16 > head.size())
                return fail("bad fmt chunk");
            const quint16 format = u16(head, body);
            const quint16 bits = u16(head, body + 14);
            byteRate = u32(head, body + 8);
            // 1 = PCM; 0xFFFE = WAVE_FORMAT_EXTENSIBLE, whose sub-format GUID
            // starts with the same 1.
            const bool isPcm = format == 1
                               || (format == 0xFFFE && size >= 40 && body + 26 <= head.size()
                                   && u16(head, body + 24) == 1);
            pcm16 = isPcm && bits == 16;
            sawFormat = true;
        } else if (id == "data") {
            if (!sawFormat)
                return fail("data before fmt");
            if (!pcm16)
                return fail("not 16-bit PCM");
            if (byteRate == 0 || size == 0)
                return fail("empty data");
            if (durationMs)
                *durationMs = int(qMin<qint64>(qint64(size) * 1000 / byteRate, 10 * 60 * 1000));
            return true;
        }
        const qint64 next = qint64(body) + qint64(size) + (size & 1);
        if (next > head.size())
            break;
        offset = qsizetype(next);
    }
    return fail(sawFormat && !pcm16 ? "not 16-bit PCM" : "no data chunk");
}

} // namespace mr
