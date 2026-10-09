#include "ReflectionShareController.h"

#include <QFileDialog>
#include <QFileInfo>
#include <QImageWriter>
#include <QQuickItem>
#include <QQuickItemGrabResult>
#include <QQuickWindow>
#include <QSaveFile>
#include <QStandardPaths>
#include <QTimer>
#include <QUrl>
#include <cmath>

namespace mr {
ReflectionShareController::ReflectionShareController(QObject *parent) : QObject(parent) {}

void ReflectionShareController::resetFeedback()
{
    if (m_busy)
        return;
    m_feedback.clear();
    m_savedPath.clear();
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
    if (path.trimmed().isEmpty()) {
        finish(tr("已取消保存。"));
        return;
    }
    auto *item = qobject_cast<QQuickItem *>(object);
    if (!item || !item->window() || !item->window()->isVisible() || !item->isVisible()
        || item->width() <= 0 || item->height() <= 0) {
        finish(tr("分享预览尚未显示，请打开预览后重试。"));
        return;
    }
    // 不允许渲染器缩小或裁切完整卡片；超限时保留内容并如实报错。
    const QSize size(int(std::ceil(item->width())), int(std::ceil(item->height())));
    // Qt 6.11 的抓图纹理按窗口 DPR 扩大；限制和完整性核验均使用实际像素。
    const QSize pixelSize = size * item->window()->effectiveDevicePixelRatio();
    if (pixelSize.width() > 16384 || pixelSize.height() > 16384
        || qint64(pixelSize.width()) * pixelSize.height() > 50 * 1000 * 1000) {
        finish(tr("心得图片尺寸过大，无法完整保存。请减少连续空行后重试。"));
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
        finish(tr("请使用 .png 文件名保存分享图片。"));
        return;
    }
    m_busy = true;
    m_feedback = tr("正在生成完整图片…");
    m_savedPath.clear();
    const quint64 generation = ++m_generation;
    Q_EMIT changed();
    m_grab = item->grabToImage(size);
    if (!m_grab) {
        finish(tr("无法生成图片，请重新打开预览后重试。"));
        return;
    }
    connect(m_grab.data(), &QQuickItemGrabResult::ready, this, [this, target, pixelSize, generation] {
        if (!m_busy || generation != m_generation)
            return;
        const QImage image = m_grab->image();
        if (image.isNull() || image.size() != pixelSize) {
            finish(tr("生成的图片不完整，未保存。请重试。"));
            return;
        }
        QString error;
        if (!writePng(image, target, &error)) {
            finish(error);
            return;
        }
        finish(tr("图片已保存：%1").arg(target), QFileInfo(target).absoluteFilePath());
    });
    QTimer::singleShot(15000, this, [this, generation] {
        if (m_busy && generation == m_generation) {
            ++m_generation;
            finish(tr("生成图片超时，未保存。请重试。"));
        }
    });
}
}
