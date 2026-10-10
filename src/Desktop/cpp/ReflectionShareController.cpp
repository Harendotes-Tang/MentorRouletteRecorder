#include "ReflectionShareController.h"

#include <QDir>
#include <QFileDialog>
#include <QFileInfo>
#include <QImageWriter>
#include <QQuickItem>
#include <QQuickItemGrabResult>
#include <QQuickWindow>
#include <QRegularExpression>
#include <QSaveFile>
#include <QSet>
#include <QStandardPaths>
#include <QTimer>
#include <QUrl>
#include <QUuid>
#include <cmath>
#include <memory>

namespace mr {
ReflectionShareController::ReflectionShareController(QObject *parent) : QObject(parent) {}

int ReflectionShareController::successfulCount() const
{
    int count = 0;
    for (const QVariant &result : m_batchResults)
        if (result.toMap().value(QStringLiteral("status")).toString() == QStringLiteral("saved"))
            ++count;
    return count;
}

int ReflectionShareController::failedCount() const
{
    int count = 0;
    for (const QVariant &result : m_batchResults)
        if (result.toMap().value(QStringLiteral("status")).toString() == QStringLiteral("failed"))
            ++count;
    return count;
}

void ReflectionShareController::resetFeedback()
{
    if (m_busy)
        return;
    m_feedback.clear();
    m_savedPath.clear();
    m_batchEntries.clear();
    m_batchResults.clear();
    m_pendingBatch.clear();
    m_batchDirectory.clear();
    Q_EMIT changed();
}

void ReflectionShareController::savePicked(QObject *item)
{
    if (m_busy)
        return;
    QFileDialog chooser(nullptr, tr("保存心得分享图片"),
        QStandardPaths::writableLocation(QStandardPaths::PicturesLocation), tr("PNG 图片 (*.png)"));
    chooser.setAcceptMode(QFileDialog::AcceptSave);
    chooser.setDefaultSuffix(QStringLiteral("png"));
    chooser.selectFile(QStringLiteral("导随心得.png"));
    const QString path = chooser.exec() == QDialog::Accepted
        ? chooser.selectedFiles().value(0) : QString();
    saveTo(item, path);
}

void ReflectionShareController::saveBatchPicked(const QVariantList &entries)
{
    if (m_busy)
        return;
    const QString directory = QFileDialog::getExistingDirectory(nullptr, tr("选择逐条 PNG 的保存文件夹"),
        QStandardPaths::writableLocation(QStandardPaths::PicturesLocation));
    saveBatchTo(entries, directory);
}

void ReflectionShareController::finish(const QString &feedback, const QString &savedPath)
{
    m_busy = false;
    m_feedback = feedback;
    m_savedPath = savedPath;
    m_grab.reset();
    Q_EMIT changed();
    if (!savedPath.isEmpty())
        Q_EMIT saved(savedPath);
}

bool ReflectionShareController::writePng(const QImage &image, const QString &path, QString *error)
{
    if (image.isNull() || path.trimmed().isEmpty()) {
        if (error) *error = tr("图片为空或保存路径为空。");
        return false;
    }
    QSaveFile file(path);
    if (!file.open(QIODevice::WriteOnly)) {
        if (error) *error = tr("无法创建图片文件：%1").arg(file.errorString());
        return false;
    }
    QImageWriter writer(&file, "png");
    if (!writer.write(image)) {
        if (error) *error = tr("无法编码 PNG 图片：%1").arg(writer.errorString());
        file.cancelWriting();
        return false;
    }
    if (!file.commit()) {
        if (error) *error = tr("无法保存图片文件：%1").arg(file.errorString());
        return false;
    }
    return true;
}

void ReflectionShareController::saveTo(QObject *object, const QString &path)
{
    if (m_busy)
        return;
    // 单图的原生文件对话框负责覆盖确认；批量文件名另行避免冲突。
    resetFeedback();
    if (path.trimmed().isEmpty()) {
        finish(tr("已取消保存。"));
        return;
    }
    m_busy = true;
    m_feedback = tr("正在生成完整图片…");
    Q_EMIT changed();
    captureTo(object, path, false, [this](const QString &savedPath, const QString &error) {
        finish(error.isEmpty() ? tr("图片已保存：%1").arg(savedPath) : error, savedPath);
    });
}

void ReflectionShareController::captureTo(QObject *object, const QString &path,
                                         bool rejectExisting, CaptureDone done)
{
    const QPointer<QQuickItem> item = qobject_cast<QQuickItem *>(object);
    if (!item || !item->window() || !item->window()->isVisible() || !item->isVisible()) {
        done({}, tr("分享预览尚未显示，请打开预览后重试。"));
        return;
    }
    // 初次打开或反馈区域变化后，Popup/ScrollView/Repeater 的宽度尚可能等待 polish。
    // 等一帧布局真正完成再测量，避免把最终卡片缩到瞬时的旧尺寸。
    const quint64 generation = ++m_generation;
    const auto connection = std::make_shared<QMetaObject::Connection>();
    *connection = connect(item->window(), &QQuickWindow::frameSwapped, this,
        [this, item, path, rejectExisting, done, generation, connection] {
            disconnect(*connection);
            if (!m_busy || generation != m_generation)
                return;
            ++m_generation;
            capturePrepared(item, path, rejectExisting, done);
        }, Qt::QueuedConnection);
    item->window()->update();
    QTimer::singleShot(15000, this, [this, generation, done, connection] {
        if (m_busy && generation == m_generation) {
            disconnect(*connection);
            ++m_generation;
            done({}, tr("预览布局超时，未保存。请重试。"));
        }
    });
}

void ReflectionShareController::capturePrepared(QObject *object, const QString &path,
                                               bool rejectExisting, CaptureDone done)
{
    auto *item = qobject_cast<QQuickItem *>(object);
    if (!item || !item->window() || !item->window()->isVisible() || !item->isVisible()
        || item->width() <= 0 || item->height() <= 0) {
        done({}, tr("分享预览尚未显示，请打开预览后重试。"));
        return;
    }
    // 捕获完整布局；限制和完整性核验均使用窗口 DPR 后的真实像素，禁止缩放或裁切。
    const QSize size(int(std::ceil(item->width())), int(std::ceil(item->height())));
    const QSize pixelSize = size * item->window()->effectiveDevicePixelRatio();
    if (pixelSize.width() > 16384 || pixelSize.height() > 16384
        || qint64(pixelSize.width()) * pixelSize.height() > 50 * 1000 * 1000) {
        done({}, tr("图片尺寸超出完整保存上限（单边 16384 像素、总计 5000 万像素）。合成长图请改用逐条 PNG；内容未裁剪。"));
        return;
    }
    QString target = path;
    const QUrl url(path);
    if (url.isLocalFile())
        target = url.toLocalFile();
    const QString suffix = QFileInfo(target).suffix();
    if (suffix.isEmpty())
        target += QStringLiteral(".png");
    else if (suffix.compare(QStringLiteral("png"), Qt::CaseInsensitive) != 0) {
        done({}, tr("请使用 .png 文件名保存分享图片。"));
        return;
    }
    if (rejectExisting && QFileInfo::exists(target)) {
        done({}, tr("目标文件已存在，未覆盖。请重试生成新的文件名。"));
        return;
    }
    const quint64 generation = ++m_generation;
    m_grab = item->grabToImage(size);
    if (!m_grab) {
        done({}, tr("无法生成图片，请重新打开预览后重试。"));
        return;
    }
    const QPointer<QQuickItem> capturedItem = item;
    connect(m_grab.data(), &QQuickItemGrabResult::ready, this,
            [this, target, size, pixelSize, generation, rejectExisting, done, capturedItem] {
        if (!m_busy || generation != m_generation)
            return;
        // 保持发出 ready 的对象直到回调返回；done 可能立即开始下一张抓图。
        const auto grab = m_grab;
        const QImage image = grab->image();
        ++m_generation;
        m_grab.reset();
        if (!capturedItem || !capturedItem->window()
            || QSize(int(std::ceil(capturedItem->width())), int(std::ceil(capturedItem->height()))) != size
            || size * capturedItem->window()->effectiveDevicePixelRatio() != pixelSize) {
            done({}, tr("预览尺寸已变化，图片未保存。请重试。"));
            return;
        }
        if (image.isNull() || image.size() != pixelSize) {
            done({}, tr("生成的图片不完整，未保存。请重试。"));
            return;
        }
        if (rejectExisting && QFileInfo::exists(target)) {
            done({}, tr("目标文件已存在，未覆盖。请重试生成新的文件名。"));
            return;
        }
        QString error;
        if (!writePng(image, target, &error)) {
            done({}, error);
            return;
        }
        done(QFileInfo(target).absoluteFilePath(), {});
    });
    QTimer::singleShot(15000, this, [this, generation, done] {
        if (m_busy && generation == m_generation) {
            ++m_generation;
            m_grab.reset();
            done({}, tr("生成图片超时，未保存。请重试。"));
        }
    });
}

QString ReflectionShareController::uniqueBatchPath(const BatchEntry &entry) const
{
    QString label = entry.label;
    label.replace(QRegularExpression(QStringLiteral("[\\x00-\\x1f<>:\"/\\\\|?*]")), QStringLiteral("_"));
    label = label.trimmed().left(48);
    if (label.isEmpty())
        label = tr("导随心得");
    QString path;
    do {
        path = QDir(m_batchDirectory).filePath(label + QLatin1Char('-')
            + QUuid::createUuid().toString(QUuid::WithoutBraces) + QStringLiteral(".png"));
    } while (QFileInfo::exists(path));
    return path;
}

void ReflectionShareController::saveBatchTo(const QVariantList &entries, const QString &directory)
{
    if (m_busy)
        return;
    resetFeedback();
    if (directory.trimmed().isEmpty()) {
        finish(tr("已取消保存。"));
        return;
    }
    const QUrl url(directory);
    m_batchDirectory = QFileInfo(url.isLocalFile() ? url.toLocalFile() : directory).absoluteFilePath();
    if (!QFileInfo(m_batchDirectory).isDir()) {
        finish(tr("保存文件夹不存在，请重新选择。"));
        return;
    }
    QSet<QString> seen;
    QList<int> indices;
    for (const QVariant &entryValue : entries) {
        const QVariantMap entry = entryValue.toMap();
        const QString id = entry.value(QStringLiteral("run_id")).toString();
        if (id.isEmpty() || seen.contains(id))
            continue;
        seen.insert(id);
        const QString label = entry.value(QStringLiteral("label")).toString();
        auto *item = qobject_cast<QQuickItem *>(entry.value(QStringLiteral("item")).value<QObject *>());
        indices.append(m_batchEntries.size());
        m_batchEntries.append({id, label, item});
        m_batchResults.append(QVariantMap{{QStringLiteral("run_id"), id},
            {QStringLiteral("label"), label}, {QStringLiteral("status"), QStringLiteral("pending")},
            {QStringLiteral("path"), QString()}, {QStringLiteral("error"), QString()}});
    }
    if (indices.isEmpty()) {
        finish(tr("没有可生成图片的选中心得。"));
        return;
    }
    startBatch(indices);
}

void ReflectionShareController::startBatch(const QList<int> &indices)
{
    m_pendingBatch = indices;
    m_busy = true;
    m_savedPath.clear();
    m_feedback = tr("正在逐条保存完整图片…");
    for (int index : indices) {
        QVariantMap result = m_batchResults.at(index).toMap();
        result.insert(QStringLiteral("status"), QStringLiteral("pending"));
        result.insert(QStringLiteral("error"), QString());
        result.insert(QStringLiteral("path"), QString());
        m_batchResults[index] = result;
    }
    Q_EMIT changed();
    QTimer::singleShot(0, this, &ReflectionShareController::processNextBatch);
}

void ReflectionShareController::processNextBatch()
{
    if (m_pendingBatch.isEmpty()) {
        finish(tr("已保存 %1 张，失败 %2 张。%3").arg(successfulCount()).arg(failedCount())
            .arg(failedCount() ? tr("可仅重试失败项。") : tr("下方列出实际保存路径。")));
        return;
    }
    const int index = m_pendingBatch.takeFirst();
    const BatchEntry entry = m_batchEntries.at(index);
    captureTo(entry.item, uniqueBatchPath(entry), true,
              [this, index](const QString &path, const QString &error) {
        QVariantMap result = m_batchResults.at(index).toMap();
        result.insert(QStringLiteral("status"), error.isEmpty() ? QStringLiteral("saved") : QStringLiteral("failed"));
        result.insert(QStringLiteral("path"), path);
        result.insert(QStringLiteral("error"), error);
        m_batchResults[index] = result;
        Q_EMIT changed();
        if (!path.isEmpty())
            Q_EMIT saved(path);
        QTimer::singleShot(0, this, &ReflectionShareController::processNextBatch);
    });
}

void ReflectionShareController::retryFailed()
{
    if (m_busy)
        return;
    QList<int> failed;
    for (int index = 0; index < m_batchResults.size(); ++index)
        if (m_batchResults.at(index).toMap().value(QStringLiteral("status")).toString() == QStringLiteral("failed"))
            failed.append(index);
    if (!failed.isEmpty())
        startBatch(failed);
}
}
