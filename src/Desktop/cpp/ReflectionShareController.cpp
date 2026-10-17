#include "ReflectionShareController.h"
#include "Formatters.h"

#include <QDir>
#include <QDate>
#include <QFileDialog>
#include <QFileInfo>
#include <QImageWriter>
#include <QJSValue>
#include <QPainter>
#include <QQuickItem>
#include <QQuickItemGrabResult>
#include <QQuickWindow>
#include <QRegularExpression>
#include <QSaveFile>
#include <QSet>
#include <QStandardPaths>
#include <QTimer>
#include <QUrl>
#include <cmath>
#include <memory>

namespace {
QVariant shareProperty(QObject *object, const char *name)
{
    QVariant value = object ? object->property(name) : QVariant();
    return value.metaType() == QMetaType::fromType<QJSValue>()
        ? value.value<QJSValue>().toVariant() : value;
}

QString availablePngPath(const QString &directory, const QString &fileName)
{
    const QString stem = fileName.chopped(4);
    QString path = QDir(directory).filePath(fileName);
    for (int number = 2; QFileInfo::exists(path); ++number)
        path = QDir(directory).filePath(stem + QStringLiteral(" (%1).png").arg(number));
    return path;
}

QQuickItem *findShareVisual(QQuickItem *item, const QString &name)
{
    if (item->objectName() == name)
        return item;
    for (auto *child : item->childItems())
        if (auto *found = findShareVisual(child, name))
            return found;
    return nullptr;
}

// Qt 软件抓图会将混有不透明正文片的组合输出为 RGB，角外透明区域随之变黑。
// 只在 alpha 已丢失时恢复：从实际顶底素材和已渲染几何取 alpha，不给正常 RGBA 重乘蒙版。
bool restorePaperAlpha(QImage &image, QQuickItem *capture)
{
    // 新卷轴以不透明纸纹覆盖整幅；不能对它套用旧卡片的圆角透明蒙版。
    if (image.hasAlphaChannel() || capture->property("opaquePaper").toBool())
        return true;
    QList<QQuickItem *> cards;
    const auto collect = [&cards](auto &&self, QQuickItem *item) -> void {
        if (item->objectName() == QStringLiteral("reflectionShareCard")) {
            cards.append(item);
            return;
        }
        for (auto *child : item->childItems())
            self(self, child);
    };
    collect(collect, capture);
    if (cards.isEmpty())
        return true;

    const qreal ratio = image.devicePixelRatio();
    const auto pixels = [capture, ratio](QQuickItem *item) {
        const QRectF bounds = item->mapRectToItem(capture, item->boundingRect());
        return QRectF(bounds.topLeft() * ratio, bounds.size() * ratio);
    };
    QImage restored(image.size(), QImage::Format_ARGB32_Premultiplied);
    if (restored.isNull())
        return false;
    restored.fill(Qt::transparent);
    {
        QPainter painter(&restored);
        for (auto *card : cards) {
            painter.setClipRect(pixels(card));
            // 显式源/目标像素矩形，避免 QImage 的 DPR 再次缩放；组合卡片之间保留透明空隙。
            painter.drawImage(QRectF(restored.rect()), image, QRectF(image.rect()));
        }
    }
    for (auto *card : cards) {
        auto *bodyPaper = findShareVisual(card, QStringLiteral("reflectionSharePaperBody"));
        if (!bodyPaper)
            return false;
        for (const auto &name : {QStringLiteral("reflectionShareHeader"), QStringLiteral("reflectionShareFooter")}) {
            auto *cap = findShareVisual(card, name);
            if (!cap)
                return false;
            const QUrl source = cap->property("source").toUrl();
            if (source.scheme() != QLatin1String("qrc"))
                return false;
            const QImage original(QLatin1Char(':') + source.path());
            if (original.isNull())
                return false;
            const QRectF bounds = pixels(cap);
            const QRect region = bounds.toAlignedRect().intersected(restored.rect());
            QImage mask(region.size(), QImage::Format_ARGB32_Premultiplied);
            if (mask.isNull())
                return false;
            mask.fill(Qt::transparent);
            {
                QPainter painter(&mask);
                painter.setRenderHint(QPainter::SmoothPixmapTransform);
                // 底片的亚像素首行仍由下方正文覆盖，不能将此拼接重叠误清为透明。
                painter.fillRect(pixels(bodyPaper).translated(-region.topLeft()), Qt::white);
                painter.drawImage(bounds.translated(-region.topLeft()), original);
            }
            for (int y = 0; y < region.height(); ++y) {
                auto *target = reinterpret_cast<QRgb *>(restored.scanLine(region.top() + y)) + region.left();
                const auto *alpha = reinterpret_cast<const QRgb *>(mask.constScanLine(y));
                for (int x = 0; x < region.width(); ++x) {
                    const int a = qAlpha(alpha[x]);
                    // RGB 抓图中的边缘已经与透明黑合成；保留预乘色，恢复对应 alpha。
                    if (a < 255)
                        target[x] = qRgba(qMin(qRed(target[x]), a), qMin(qGreen(target[x]), a),
                                          qMin(qBlue(target[x]), a), a);
                }
            }
        }
    }
    restored.setDevicePixelRatio(ratio);
    image = restored;
    return true;
}
}

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
    const QString directory = QStandardPaths::writableLocation(QStandardPaths::PicturesLocation);
    QFileDialog chooser(nullptr, tr("保存心得分享图片"), directory, tr("PNG 图片 (*.png)"));
    chooser.setAcceptMode(QFileDialog::AcceptSave);
    chooser.setDefaultSuffix(QStringLiteral("png"));
    chooser.selectFile(availablePngPath(directory, suggestedFileName(item)));
    const QString path = chooser.exec() == QDialog::Accepted
        ? chooser.selectedFiles().value(0) : QString();
    saveTo(item, path);
}

QString ReflectionShareController::suggestedFileName(QObject *item, const QString &fallbackLabel) const
{
    const QVariantList entries = shareProperty(item, "entries").toList();
    QString stem;
    if (entries.size() > 1) {
        stem = tr("导随心得合集_%1_%2条").arg(QDate::currentDate().toString(QStringLiteral("yyyy-MM-dd")))
            .arg(entries.size());
    } else {
        const QVariantMap run = entries.isEmpty() ? shareProperty(item, "runData").toMap() : entries.first().toMap();
        QString time = Formatters::localDateTime(run.value(QStringLiteral("matched_at_utc")));
        if (time == Formatters::dash())
            time = tr("时间未知");
        else {
            time.replace(QLatin1Char(' '), QLatin1Char('_'));
            time.replace(QLatin1Char(':'), QLatin1Char('-'));
        }
        QString label = fallbackLabel;
        if (auto *visual = qobject_cast<QQuickItem *>(item))
            if (auto *title = findShareVisual(visual, QStringLiteral("reflectionShareDutyTitle")))
                label = title->property("text").toString().remove(QLatin1Char('\n'));
        if (label.isEmpty()) label = run.value(QStringLiteral("duty_name")).toString();
        if (label.isEmpty()) label = tr("未知副本");
        stem = tr("导随心得_%1_%2").arg(time, label);
    }
    stem.replace(QRegularExpression(QStringLiteral("[\\x00-\\x1f<>:\"/\\\\|?*]")), QStringLiteral("_"));
    stem = stem.trimmed().left(120);
    if (!stem.isEmpty() && stem.back().isHighSurrogate()) stem.chop(1);
    while (stem.endsWith(QLatin1Char('.')) || stem.endsWith(QLatin1Char(' '))) stem.chop(1);
    return stem + QStringLiteral(".png");
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
        if (error) *error = tr("没有可保存的图片，或尚未选择保存位置。");
        return false;
    }
    QSaveFile file(path);
    if (!file.open(QIODevice::WriteOnly)) {
        if (error) *error = tr("无法创建图片文件：%1").arg(file.errorString());
        return false;
    }
    QImageWriter writer(&file, "png");
    if (!writer.write(image)) {
        if (error) *error = tr("无法生成 PNG 图片：%1").arg(writer.errorString());
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
            done({}, tr("图片预览未能及时准备好，尚未保存。请重试。"));
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
        done({}, tr("图片太大，尚未保存（任一边不能超过 16384 像素，总计不能超过 5000 万像素）。如果选择了“合成长图”，请改用“逐条 PNG”。"));
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
    // 原生保存窗口可能跨过午夜；每次真正抓取前刷新日期，也覆盖逐条重试。
    if (item->property("opaquePaper").toBool())
        item->setProperty("exportDate", QDate::currentDate().toString(QStringLiteral("yyyy.MM.dd")));
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
        QImage image = grab->image();
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
        if (!restorePaperAlpha(image, capturedItem)) {
            done({}, tr("无法正确生成图片的透明边缘，尚未保存。请重新打开预览后重试。"));
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
    return availablePngPath(m_batchDirectory, suggestedFileName(entry.item, entry.label));
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
        finish((failedCount() ? tr("已保存 %1 张，失败 %2 张。可仅重试失败项。")
                              : tr("已保存 %1 张，失败 %2 张。下方列出实际保存路径。"))
                   .arg(successfulCount()).arg(failedCount()));
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
