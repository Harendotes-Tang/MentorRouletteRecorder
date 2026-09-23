#include "NoteImageStore.h"

#include <QCoreApplication>
#include <QDateTime>
#include <QDir>
#include <QFile>
#include <QFileDialog>
#include <QFileInfo>
#include <QImageReader>
#include <QRegularExpression>
#include <QStandardPaths>
#include <QUrl>

namespace mr {

namespace {

QString chinese(const char *utf8)
{
    return QString::fromUtf8(utf8);
}

QString nativePath(const QString &path)
{
    return QDir::toNativeSeparators(path);
}

/// True when \a path is \a directory itself or lies below it. Both sides are
/// cleaned absolute paths; the comparison is case-insensitive because Windows is.
bool isInside(const QString &directory, const QString &path)
{
    const QString base = QDir::cleanPath(directory);
    const QString candidate = QDir::cleanPath(path);
    if (candidate.compare(base, Qt::CaseInsensitive) == 0)
        return true;
    return candidate.startsWith(base + QLatin1Char('/'), Qt::CaseInsensitive);
}

} // namespace

// ---------------------------------------------------------------- 构造 --

NoteImageStore::NoteImageStore(QObject *parent)
    : NoteImageStore(resolveRoot(qEnvironmentVariable(kRootVariable),
                                 QCoreApplication::applicationDirPath()),
                     parent)
{
}

NoteImageStore::NoteImageStore(const QString &rootDirectory, QObject *parent)
    : QObject(parent)
    , m_root(QDir::cleanPath(QDir(rootDirectory).absolutePath()))
{
}

QString NoteImageStore::resolveRoot(const QString &overrideDirectory,
                                    const QString &applicationDir)
{
    const QString chosen = overrideDirectory.trimmed();
    if (!chosen.isEmpty())
        return QDir::cleanPath(QDir(chosen).absolutePath());
    return QDir::cleanPath(QDir(applicationDir).absoluteFilePath(QLatin1String(kFolderName)));
}

bool NoteImageStore::isValidRunId(const QString &runId)
{
    static const QRegularExpression uuid(
        QStringLiteral("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$"));
    return uuid.match(runId).hasMatch();
}

QStringList NoteImageStore::supportedSuffixes()
{
    return {QStringLiteral("png"), QStringLiteral("jpg"), QStringLiteral("jpeg"),
            QStringLiteral("gif"), QStringLiteral("bmp"), QStringLiteral("webp")};
}

QString NoteImageStore::nameFilter()
{
    QStringList globs;
    for (const QString &suffix : supportedSuffixes())
        globs.append(QStringLiteral("*.") + suffix);
    return chinese("图片文件 (") + globs.join(QLatin1Char(' ')) + QLatin1Char(')');
}

QString NoteImageStore::rootDirectoryNative() const
{
    return nativePath(m_root);
}

QString NoteImageStore::runDirectory(const QString &runId) const
{
    if (!isValidRunId(runId))
        return {};
    return m_root + QLatin1Char('/') + runId.toLower();
}

// ---------------------------------------------------------------- 读取 --

QVariantList NoteImageStore::imagesFor(const QString &runId) const
{
    QVariantList rows;
    const QString directory = runDirectory(runId);
    if (directory.isEmpty())
        return rows;

    QStringList globs;
    for (const QString &suffix : supportedSuffixes())
        globs.append(QStringLiteral("*.") + suffix);

    const auto entries = QDir(directory).entryInfoList(globs, QDir::Files | QDir::Readable,
                                                       QDir::Name | QDir::IgnoreCase);
    for (const QFileInfo &info : entries) {
        QVariantMap row;
        row.insert(QStringLiteral("path"), info.absoluteFilePath());
        row.insert(QStringLiteral("url"), urlFor(info.absoluteFilePath()));
        row.insert(QStringLiteral("name"), info.fileName());
        row.insert(QStringLiteral("byte_count"), info.size());
        rows.append(row);
    }
    return rows;
}

QString NoteImageStore::urlFor(const QString &path)
{
    if (path.isEmpty())
        return {};
    return QUrl::fromLocalFile(path).toString();
}

// ---------------------------------------------------------------- 检查 --

QString NoteImageStore::suffixForFormat(const QByteArray &format)
{
    const QString lower = QString::fromLatin1(format).toLower();
    if (lower == QLatin1String("jpeg"))
        return QStringLiteral("jpg");
    return lower;
}

NoteImageStore::Inspection NoteImageStore::inspectFile(const QString &sourcePath)
{
    Inspection result;
    const QString trimmed = sourcePath.trimmed();
    if (trimmed.isEmpty()) {
        result.error = chinese("没有选择图片。");
        return result;
    }

    const QFileInfo info(trimmed);
    if (!info.exists() || !info.isFile()) {
        result.error = chinese("找不到这张图片：%1").arg(nativePath(trimmed));
        return result;
    }
    if (info.size() <= 0) {
        result.error = chinese("这张图片是空文件：%1").arg(info.fileName());
        return result;
    }
    if (info.size() > kMaxImageBytes) {
        result.error = chinese("图片超过 %1 MB，请先压缩：%2")
                           .arg(kMaxImageBytes / (1024 * 1024))
                           .arg(info.fileName());
        return result;
    }

    QImageReader reader(info.absoluteFilePath());
    const QString suffix = reader.canRead() ? suffixForFormat(reader.format()) : QString();
    if (suffix.isEmpty() || !supportedSuffixes().contains(suffix)) {
        result.error = chinese("不是支持的图片格式（支持 PNG、JPG、GIF、BMP、WebP）：%1")
                           .arg(info.fileName());
        return result;
    }

    result.ok = true;
    result.absolutePath = info.absoluteFilePath();
    result.suffix = suffix;
    result.byteCount = info.size();
    return result;
}

QVariantMap NoteImageStore::inspect(const QString &sourcePath) const
{
    const Inspection inspection = inspectFile(sourcePath);
    QVariantMap row;
    row.insert(QStringLiteral("ok"), inspection.ok);
    row.insert(QStringLiteral("error"), inspection.error);
    if (!inspection.ok)
        return row;
    row.insert(QStringLiteral("path"), inspection.absolutePath);
    row.insert(QStringLiteral("url"), urlFor(inspection.absolutePath));
    row.insert(QStringLiteral("name"), QFileInfo(inspection.absolutePath).fileName());
    row.insert(QStringLiteral("byte_count"), inspection.byteCount);
    row.insert(QStringLiteral("format"), inspection.suffix);
    return row;
}

QString NoteImageStore::pickImage()
{
    const QString pictures =
        QStandardPaths::writableLocation(QStandardPaths::PicturesLocation);
    return QFileDialog::getOpenFileName(nullptr, chinese("选择图片"), pictures, nameFilter());
}

// ---------------------------------------------------------------- 写入 --

void NoteImageStore::pruneEmptyFolder(const QString &directory)
{
    QDir folder(directory);
    if (!folder.exists())
        return;
    if (folder.entryList(QDir::AllEntries | QDir::NoDotAndDotDot | QDir::Hidden | QDir::System)
            .isEmpty()) {
        folder.removeRecursively();
    }
}

QVariantMap NoteImageStore::commit(const QString &runId, const QStringList &adds,
                                   const QStringList &removes)
{
    QVariantMap result;
    QStringList added;
    QStringList removed;
    auto fail = [&](const QString &error) {
        result.insert(QStringLiteral("ok"), false);
        result.insert(QStringLiteral("error"), error);
        result.insert(QStringLiteral("added"), added);
        result.insert(QStringLiteral("removed"), removed);
        return result;
    };

    const QString directory = runDirectory(runId);
    if (directory.isEmpty())
        return fail(chinese("这条记录还没有可用的编号，无法保存图片。"));

    // Every source is checked before anything is copied, so a bad file in the
    // middle of the batch does not leave half of it in the folder.
    QList<Inspection> inspections;
    for (const QString &source : adds) {
        const Inspection inspection = inspectFile(source);
        if (!inspection.ok)
            return fail(inspection.error);
        inspections.append(inspection);
    }

    // Cap on the folder after this commit; a removal that is queued frees a slot.
    if (!inspections.isEmpty()) {
        int remaining = 0;
        for (const QVariant &row : imagesFor(runId)) {
            const QString path = row.toMap().value(QStringLiteral("path")).toString();
            if (!removes.contains(path, Qt::CaseInsensitive))
                ++remaining;
        }
        if (remaining + inspections.size() > kMaxImagesPerRun) {
            return fail(chinese("一条记录最多保存 %1 张图片。").arg(kMaxImagesPerRun));
        }

        if (!QDir().mkpath(directory)) {
            return fail(chinese("无法在安装目录创建图片文件夹：%1（请检查该目录是否可写）")
                            .arg(nativePath(directory)));
        }
    }

    // Copies. One timestamp per commit, a running number per file, and a bump
    // past anything that already exists so two commits within a millisecond
    // cannot collide.
    const QString stamp =
        QDateTime::currentDateTime().toString(QStringLiteral("yyyyMMdd-HHmmss-zzz"));
    int sequence = 0;
    QStringList copied;
    for (int i = 0; i < inspections.size(); ++i) {
        const Inspection &inspection = inspections.at(i);
        QString target;
        do {
            ++sequence;
            target = QStringLiteral("%1/%2-%3.%4")
                         .arg(directory, stamp)
                         .arg(sequence, 2, 10, QLatin1Char('0'))
                         .arg(inspection.suffix);
        } while (QFileInfo::exists(target));

        if (!QFile::copy(inspection.absolutePath, target)) {
            for (const QString &partial : copied)
                QFile::remove(partial);
            pruneEmptyFolder(directory);
            return fail(chinese("无法把图片复制到安装目录：%1（请检查该目录是否可写）")
                            .arg(nativePath(directory)));
        }
        // QFile::copy keeps a read-only source's attribute; the copy is ours and
        // must stay removable.
        QFile::setPermissions(target, QFileDevice::ReadOwner | QFileDevice::WriteOwner
                                          | QFileDevice::ReadUser | QFileDevice::WriteUser);
        copied.append(target);
        added.append(adds.at(i));
    }

    // Removals. Only paths inside this run's folder are touched; anything else
    // is a programming error on the caller's side and is refused, not deleted.
    QStringList failures;
    for (const QString &path : removes) {
        const QString absolute = QFileInfo(path).absoluteFilePath();
        if (!isInside(directory, absolute) || isInside(absolute, directory)) {
            failures.append(chinese("不在这条记录的图片文件夹内：%1").arg(nativePath(path)));
            continue;
        }
        if (!QFileInfo::exists(absolute)) {
            removed.append(path);
            continue;
        }
        // A sync client or the player may have flagged the copy read-only since;
        // that must not make it undeletable from the UI.
        QFile::setPermissions(absolute, QFileDevice::ReadOwner | QFileDevice::WriteOwner
                                            | QFileDevice::ReadUser | QFileDevice::WriteUser);
        if (QFile::remove(absolute)) {
            removed.append(path);
            continue;
        }
        failures.append(chinese("无法删除图片：%1").arg(nativePath(absolute)));
    }
    pruneEmptyFolder(directory);

    if (!added.isEmpty() || !removed.isEmpty())
        Q_EMIT imagesChanged(runId);

    if (!failures.isEmpty())
        return fail(failures.join(QLatin1Char('\n')));

    result.insert(QStringLiteral("ok"), true);
    result.insert(QStringLiteral("error"), QString());
    result.insert(QStringLiteral("added"), added);
    result.insert(QStringLiteral("removed"), removed);
    return result;
}

} // namespace mr
