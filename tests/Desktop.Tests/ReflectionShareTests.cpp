#include "ReflectionShareController.h"
#include "BundledFonts.h"
#include "AppSettings.h"
#include "Formatters.h"
#include "JobCatalog.h"
#include "GameNames.h"
#include "LanguageManager.h"

#include <QDir>
#include <QDate>
#include <QFileInfo>
#include <QFile>
#include <QGuiApplication>
#include <QFont>
#include <QFontDatabase>
#include <QFontInfo>
#include <QFontMetricsF>
#include <QImage>
#include <QPainterPath>
#include <QLocale>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickWindow>
#include <QRawFont>
#include <QQuickStyle>
#include <QSignalSpy>
#include <QStandardPaths>
#include <QTemporaryDir>
#include <QTest>
#include <QWheelEvent>
#include <QScopeGuard>
#include <QtMath>

#include <memory>
#include <utility>

namespace {
QQuickItem *findVisual(QQuickItem *at, const QString &name)
{
    if (!at)
        return nullptr;
    if (at->objectName() == name)
        return at;
    for (auto *child : at->childItems())
        if (auto *found = findVisual(child, name))
            return found;
    return nullptr;
}

QList<QQuickItem *> completeCards(QQuickItem *at)
{
    QList<QQuickItem *> cards;
    if (at->objectName() == QStringLiteral("reflectionShareCard"))
        cards.append(at);
    for (auto *child : at->childItems())
        cards.append(completeCards(child));
    return cards;
}

QList<QQuickItem *> visibleEntries(QQuickItem *at)
{
    QList<QQuickItem *> entries;
    if (at->objectName() == QStringLiteral("reflectionShareEntry") && at->isVisible())
        entries.append(at);
    for (auto *child : at->childItems())
        entries.append(visibleEntries(child));
    return entries;
}

bool paperJoinsAreContinuous(const QImage &png, QQuickItem *card, qreal ratio)
{
    // 长图宽度未必是模板的整数比例；顶片、底片交界都不得出现整行黑色或透明空隙。
    for (const auto &name : {QStringLiteral("reflectionShareHeader"), QStringLiteral("reflectionShareFooter")}) {
        auto *cap = findVisual(card, name);
        if (!cap)
            return false;
        const QRectF bounds = cap->mapRectToItem(card, cap->boundingRect());
        const qreal join = (name.endsWith(QStringLiteral("Header")) ? bounds.bottom() : bounds.top()) * ratio;
        for (int y = qFloor(join) - 2; y <= qCeil(join) + 2; ++y) {
            bool paperPixel = false;
            for (int x = png.width() / 3; x < png.width() * 2 / 3; ++x) {
                const QColor pixel = png.pixelColor(x, y);
                if (pixel.alpha() == 255 && pixel.red() > 140 && pixel.green() > 120 && pixel.blue() > 100) {
                    paperPixel = true;
                    break;
                }
            }
            if (!paperPixel) {
                qWarning() << "paper join" << name << "row" << y << "pixel" << png.pixelColor(png.width() / 2, y);
                return false;
            }
        }
    }
    return true;
}
bool finalTextHasInk(const QImage &png, QQuickItem *body, QQuickItem *capture, qreal ratio)
{
    // 纹理纸面也有深浅变化，只统计接近正文墨色的像素；范围含基准画布的缩放。
    const QRectF bounds = body->mapRectToItem(capture, body->boundingRect());
    const QColor ink = body->property("color").value<QColor>();
    const int endY = qMin(png.height(), qCeil(bounds.bottom() * ratio));
    int glyphPixels = 0;
    for (int y = qMax(0, endY - qRound(40 * ratio)); y < endY; ++y)
        for (int x = qMax(0, qFloor(bounds.left() * ratio));
             x < qMin(png.width(), qCeil(bounds.right() * ratio)); ++x) {
            const QColor pixel = png.pixelColor(x, y);
            if (pixel.alpha() > 200 && qAbs(pixel.red() - ink.red())
                + qAbs(pixel.green() - ink.green()) + qAbs(pixel.blue() - ink.blue()) < 60)
                ++glyphPixels;
        }
    return glyphPixels > 20;
}

QString headingInkAlignmentError(const QImage &png, QQuickItem *card, qreal ratio)
{
    // 检查实际 PNG 墨迹，避免“基线相同”掩盖字号、字重不同造成的视觉偏移。
    auto *heading = findVisual(card, card->property("single").toBool()
        ? QStringLiteral("reflectionShareSingleHeading") : QStringLiteral("reflectionShareCollectionMeta"));
    if (!heading) return QStringLiteral("缺少可验证题头");
    const qreal targetCenter = heading->property("firstLineCenter").toReal();
    qreal topCenter = png.height();
    qreal bottomCenter = 0;
    QStringList centers;
    for (const char *name : {"reflectionShareEyebrow", "reflectionShareCharacterName",
                             "reflectionShareServerName", "reflectionShareCount", "reflectionShareTimeText"}) {
        auto *label = findVisual(card, QString::fromLatin1(name));
        if (!label || !label->isVisible()) continue;
        // 数学布局严格对齐；独立于下方抗锯齿栅格化的像素容差。
        const QFontMetricsF metrics(label->property("font").value<QFont>());
        QString firstLine = label->property("text").toString().section(QLatin1Char('\n'), 0, 0);
        if (label->objectName() == QStringLiteral("reflectionShareCharacterName"))
            firstLine = metrics.elidedText(firstLine, Qt::ElideRight, label->width());
        const QRectF glyphs = metrics.tightBoundingRect(firstLine);
        const qreal layoutCenter = label->mapToItem(heading,
            QPointF(0, label->baselineOffset() + glyphs.center().y())).y();
        if (qAbs(layoutCenter - targetCenter) > 0.02)
            return QStringLiteral("题头轮廓中心未对齐：%1，偏差 %2").arg(QString::fromLatin1(name)).arg(layoutCenter - targetCenter);
        const QRectF mapped = label->mapRectToItem(card, label->boundingRect());
        const QRect region = QRectF(mapped.x() * ratio, mapped.y() * ratio,
                                     mapped.width() * ratio, mapped.height() * ratio).toAlignedRect()
                                 .intersected(png.rect());
        const QColor ink = label->property("color").value<QColor>();
        QRect inkBounds;
        for (int y = region.top(); y <= region.bottom(); ++y)
            for (int x = region.left(); x <= region.right(); ++x) {
                const QColor pixel = png.pixelColor(x, y);
                if (pixel.alpha() > 200 && qAbs(pixel.red() - ink.red())
                    + qAbs(pixel.green() - ink.green()) + qAbs(pixel.blue() - ink.blue()) < 50)
                    inkBounds |= QRect(x, y, 1, 1);
            }
        if (inkBounds.isEmpty()) return QStringLiteral("题头文字没有可验证墨迹：%1").arg(QString::fromLatin1(name));
        const qreal center = (inkBounds.top() + inkBounds.bottom()) / 2.0;
        topCenter = qMin(topCenter, center);
        bottomCenter = qMax(bottomCenter, center);
        centers.append(QStringLiteral("%1=%2").arg(QString::fromLatin1(name)).arg(center / ratio));
    }
    if (centers.size() < 3) return QStringLiteral("题头有效文字段不足");
    qInfo().noquote() << "heading ink centers:" << centers.join(QStringLiteral(", "));
    // 色差阈值会排除细笔画的抗锯齿端点；允许两个物理像素或一个逻辑像素的栅格偏差。
    // 这是 Windows / FreeType、1x–2x 验证矩阵的经验容差，不替代上方严格的轮廓中心断言。
    return bottomCenter - topCenter <= qMax(qreal(2), ratio) ? QString() : centers.join(QStringLiteral(", "));
}
}

class ShareThemeState : public QObject
{
    Q_OBJECT
    Q_PROPERTY(bool dark MEMBER dark NOTIFY changed)
public:
    bool dark = false;
Q_SIGNALS:
    void changed();
};

class ReflectionShareTests : public QObject
{
    Q_OBJECT
private:
    struct IdentityStage
    {
        IdentityStage(mr::ReflectionShareController &testController, const mr::AppSettings &testSettings,
                      const QVariantMap &sourceRun, const QString &sourceBaseName, const QString &evidencePath,
                      const QTemporaryDir &outputDirectory, const QSignalSpy &savedSignal)
            : controller(testController), settings(testSettings), run(sourceRun), baseName(sourceBaseName),
              evidence(evidencePath), output(outputDirectory), saved(savedSignal)
        {
        }

        mr::ReflectionShareController &controller;
        const mr::AppSettings &settings;
        QQuickWindow *window = nullptr;
        QObject *dialog = nullptr;
        const QVariantMap &run;
        const QString &baseName;
        const QString &evidence;
        const QTemporaryDir &output;
        const QSignalSpy &saved;
        QQuickItem *input = nullptr;
        QQuickItem *option = nullptr;
        QQuickItem *mode = nullptr;
        QQuickItem *individual = nullptr;
        QQuickItem *identity = nullptr;
    };

    static bool openShareRuns(QObject *dialog, const QVariantList &runs)
    {
        return QMetaObject::invokeMethod(dialog, "openForRuns", Q_ARG(QVariant, QVariant(runs)));
    }

    static void verifyReferenceCollection(const QVariantMap &run, mr::ReflectionShareController &controller,
                                          QQuickItem *card, QQuickWindow *window, const QTemporaryDir &output,
                                          const QSignalSpy &saved, const QString &evidence)
    {
        const auto save = [&](const QString &name) {
            controller.saveTo(card, output.filePath(name));
        };
        const auto keep = [&](const QString &name) {
            const QImage png(output.filePath(name));
            return !png.isNull() && (evidence.isEmpty() || png.save(QDir(evidence).filePath(name)));
        };
        const QStringList names{QStringLiteral("伊弗利特讨伐战"), QStringLiteral("神兵要塞帝国南方堡"), QStringLiteral("险峻峡谷塞尔法特尔溪谷")};
        const QStringList notes{QStringLiteral("三剑一豆芽，全明星团队亲情奉献！（美美+1）"), QStringLiteral("有本打就好；；"), QStringLiteral("排了好久才排到…我们猪区还有救吗")};
        QVariantList runs;
        for (int i = 0; i < names.size(); ++i) {
            QVariantMap item = run;
            item.insert(QStringLiteral("duty_name"), names[i]);
            item.insert(QStringLiteral("job_id"), 34);
            item.insert(QStringLiteral("job_name"), QStringLiteral("武士"));
            item.insert(QStringLiteral("matched_at_utc"), QStringLiteral("2026-10-%1T06:40:00Z").arg(12 - i));
            item.insert(QStringLiteral("reflection"), QVariantMap{{QStringLiteral("text"), notes[i]}, {QStringLiteral("mood"), QStringLiteral("good")}});
            runs.append(item);
        }
        card->setProperty("runsData", runs);
        QTRY_COMPARE(visibleEntries(card).size(), 3);
        const auto collectionEntries = visibleEntries(card);
        for (auto *entry : collectionEntries) {
            QVERIFY(findVisual(entry, QStringLiteral("reflectionShareResultText")));
            QVERIFY(findVisual(entry, QStringLiteral("reflectionShareMoodTag")));
        }
        const auto referenceHeightForFont = [&] {
            qreal height = 1054.0;
            for (auto *entry : collectionEntries) {
                auto *metadata = findVisual(entry, QStringLiteral("reflectionShareResultText"));
                auto *mood = findVisual(entry, QStringLiteral("reflectionShareMoodTag"));
                const qreal bottom = qMax(metadata->y() + metadata->height(),
                                           mood->isVisible() ? mood->y() + mood->height() : 0.0);
                height += qMax(0.0, bottom - 77.0);
            }
            return height;
        };
        QTRY_COMPARE(card->height(), referenceHeightForFont());
        save(QStringLiteral("reference-collection.png"));
        QTRY_COMPARE(saved.count(), 4);
        QVERIFY(keep(QStringLiteral("reference-collection.png")));
        const QString collectionAlignment = headingInkAlignmentError(QImage(output.filePath(QStringLiteral("reference-collection.png"))),
                                                                     card, window->effectiveDevicePixelRatio());
        QVERIFY2(collectionAlignment.isEmpty(), qPrintable(collectionAlignment));
        QCOMPARE(card->property("runsData").toList(), runs);
    }

    static void verifyIdentityVisibilityAndWrapping(IdentityStage &stage)
    {
        auto &controller = stage.controller;
        auto &window = stage.window;
        const auto &evidence = stage.evidence;
        const auto &output = stage.output;
        const auto &saved = stage.saved;
        auto &input = stage.input;
        auto &option = stage.option;
        auto &individual = stage.individual;
        auto &identity = stage.identity;
        controller.saveTo(individual, output.filePath(QStringLiteral("identity.png")));
        QTRY_COMPARE(saved.count(), 1);
        if (!evidence.isEmpty()) QVERIFY(QImage(output.filePath(QStringLiteral("identity.png"))).save(QDir(evidence).filePath(QStringLiteral("identity-single.png"))));
        const qreal withIdentityHeight = individual->height();
        option->setProperty("checked", false);
        QTRY_VERIFY(!identity->isVisible());
        QCOMPARE(individual->height(), withIdentityHeight);
        controller.saveTo(individual, output.filePath(QStringLiteral("hidden.png")));
        QTRY_COMPARE(saved.count(), 2);
        const QImage hidden(output.filePath(QStringLiteral("hidden.png")));
        QVERIFY(!hidden.isNull());
        option->setProperty("checked", true);
        input->setProperty("text", QString());
        QTRY_VERIFY(!identity->isVisible());
        QCOMPARE(individual->height(), hidden.height() / window->effectiveDevicePixelRatio());
        input->setProperty("text", QStringLiteral("很长的冒险者名字").repeated(4) + QStringLiteral("@白银乡"));
        QTRY_VERIFY(identity->isVisible());
        auto *characterLabel = findVisual(identity, QStringLiteral("reflectionShareCharacterName"));
        auto *server = findVisual(identity, QStringLiteral("reflectionShareServerName"));
        auto *heading = findVisual(individual, QStringLiteral("reflectionShareSingleHeading"));
        QVERIFY(characterLabel && server && heading);
        QTRY_VERIFY(characterLabel->property("truncated").toBool());
        QCOMPARE(server->property("text").toString(), QStringLiteral("@白银乡"));
        QVERIFY(!heading->property("dateLabel").toString().contains(QStringLiteral("2026")));
        QVERIFY(server->mapRectToItem(heading, server->boundingRect()).right() <= heading->width() + 1);
        QCOMPARE(individual->height(), withIdentityHeight);
        controller.saveTo(individual, output.filePath(QStringLiteral("long.png")));
        QTRY_COMPARE(saved.count(), 3);
        if (!evidence.isEmpty()) QVERIFY(QImage(output.filePath(QStringLiteral("long.png"))).save(QDir(evidence).filePath(QStringLiteral("identity-long.png"))));
    }

    static void verifyIdentityCombinedAndKeyboard(IdentityStage &stage)
    {
        auto &controller = stage.controller;
        const auto &settings = stage.settings;
        auto &window = stage.window;
        auto &dialog = stage.dialog;
        const auto &run = stage.run;
        const auto &evidence = stage.evidence;
        const auto &output = stage.output;
        const auto &saved = stage.saved;
        auto &input = stage.input;
        auto &mode = stage.mode;
        mode->setProperty("currentIndex", 1);
        QQuickItem *combined = findVisual(window->contentItem(), QStringLiteral("reflectionShareCombinedCard"));
        QTRY_VERIFY(combined->isVisible());
        QCOMPARE(controller.suggestedFileName(combined), QStringLiteral("导随心得合集_%1_2条.png").arg(QDate::currentDate().toString(Qt::ISODate)));
        const auto entries = visibleEntries(combined);
        QCOMPARE(entries.size(), 2);
        auto *title = findVisual(entries.first(), QStringLiteral("reflectionShareDutyTitle"));
        auto *metadata = findVisual(entries.first(), QStringLiteral("reflectionShareResultText"));
        QCOMPARE(title->property("text").toString(), QStringLiteral("休养胜地布雷福洛克斯\n野营地"));
        QVERIFY(title->y() + title->height() <= metadata->y());
        input->setProperty("text", QStringLiteral("测试冒险者@白银乡"));
        controller.saveTo(combined, output.filePath(QStringLiteral("phrases.png")));
        QTRY_COMPARE(saved.count(), 4);
        if (!evidence.isEmpty()) {
            QVERIFY(QImage(output.filePath(QStringLiteral("phrases.png"))).save(QDir(evidence).filePath(QStringLiteral("phrase-collection.png"))));
            QVERIFY(window->grabWindow().save(QDir(evidence).filePath(QStringLiteral("identity-dialog.png"))));
        }
        QVERIFY(QMetaObject::invokeMethod(dialog, "close"));
        QTRY_VERIFY(!dialog->property("visible").toBool());
        QCOMPARE(settings.shareGameIdentity(), QStringLiteral("测试冒险者@白银乡"));
        QVERIFY(openShareRuns(dialog, {run}));
        QTRY_VERIFY(dialog->property("visible").toBool());
        QCOMPARE(input->property("text").toString(), settings.shareGameIdentity());
        QCOMPARE(dialog->property("runData").toMap(), run);
        window->resize(720, 560);
        input->setProperty("text", QStringLiteral("缺少服务器"));
        auto *saveButton = findVisual(window->contentItem(), QStringLiteral("saveReflectionImageButton"));
        QTRY_VERIFY(saveButton && !saveButton->isEnabled());
        QTest::qWait(80);
        auto *dialogContent = qvariant_cast<QQuickItem *>(dialog->property("contentItem"));
        QVERIFY(dialogContent);
        QVERIFY(saveButton->mapRectToItem(dialogContent, saveButton->boundingRect()).bottom() <= dialogContent->height());
        // 使用真实按键完成 ID 编辑、焦点切换和关闭。
        input->forceActiveFocus();
        QTest::keyClick(window, Qt::Key_A, Qt::ControlModifier);
        for (char character : QByteArrayLiteral("Adventurer@World")) QTest::keyClick(window, character);
        QTest::keyClick(window, Qt::Key_Tab);
        QTRY_VERIFY(saveButton->isEnabled());
        QCOMPARE(settings.shareGameIdentity(), QStringLiteral("Adventurer@World"));
        QTest::keyClick(window, Qt::Key_Tab, Qt::ShiftModifier);
        QTRY_VERIFY(input->hasActiveFocus());
        QTest::keyClick(window, Qt::Key_Escape);
        QTRY_VERIFY(!dialog->property("visible").toBool());
        QVERIFY(openShareRuns(dialog, {run}));
        QTRY_VERIFY(dialog->property("visible").toBool());
    }

    static void verifyReadableFileNamesAndCollisionSuffixes(IdentityStage &stage)
    {
        auto &controller = stage.controller;
        auto &window = stage.window;
        const auto &baseName = stage.baseName;
        const auto &output = stage.output;
        auto &individual = stage.individual;
        // 无真实游玩时间时不得把导出日期伪装成游玩时间；过滤路径字符。
        QObject simple;
        simple.setProperty("runData", QVariantMap{{QStringLiteral("duty_name"), QStringLiteral("副本/名称:*? ")}});
        QCOMPARE(controller.suggestedFileName(&simple), QStringLiteral("导随心得_时间未知_副本_名称___.png"));
        simple.setProperty("runData", QVariantMap{{QStringLiteral("duty_name"), QStringLiteral("副本/名称:*? ")},
                                                  {QStringLiteral("matched_at_utc"), QStringLiteral("invalid-time")}});
        QCOMPARE(controller.suggestedFileName(&simple), QStringLiteral("导随心得_时间未知_副本_名称___.png"));
        // 实际批量写入相同名称，验证递增后缀及保留旧文件字节。
        individual = nullptr;
        for (auto *card : completeCards(window->contentItem())) if (card->isVisible()) { individual = card; break; }
        QVERIFY(individual);
        const QVariantMap batch{{QStringLiteral("run_id"), QStringLiteral("batch-a")}, {QStringLiteral("item"), QVariant::fromValue<QObject *>(individual)}};
        controller.saveBatchTo({batch}, output.path());
        QTRY_COMPARE(controller.successfulCount(), 1);
        QTRY_VERIFY(!controller.busy());
        QVERIFY(QFileInfo::exists(output.filePath(baseName)));
        QImage sentinel(23, 17, QImage::Format_RGB32);
        sentinel.fill(Qt::magenta);
        sentinel.setText(QStringLiteral("existing"), QStringLiteral("never overwrite this file"));
        QVERIFY(sentinel.save(output.filePath(baseName)));
        QFile existing(output.filePath(baseName));
        QVERIFY(existing.open(QIODevice::ReadOnly));
        const QByteArray originalBytes = existing.readAll();
        existing.close();
        controller.saveBatchTo({batch}, output.path());
        QTRY_COMPARE(controller.successfulCount(), 1);
        QTRY_VERIFY(!controller.busy());
        QVERIFY(QFileInfo::exists(output.filePath(baseName.chopped(4) + QStringLiteral(" (2).png"))));
        QVERIFY(!QImage(output.filePath(baseName.chopped(4) + QStringLiteral(" (2).png"))).isNull());
        controller.saveBatchTo({batch}, output.path());
        QTRY_COMPARE(controller.successfulCount(), 1);
        QTRY_VERIFY(!controller.busy());
        QVERIFY(QFileInfo::exists(output.filePath(baseName.chopped(4) + QStringLiteral(" (3).png"))));
        QVERIFY(existing.open(QIODevice::ReadOnly));
        QCOMPARE(existing.readAll(), originalBytes);
    }

    static void verifyActualDialogClosing(QQuickWindow *window, QObject *dialog, QQuickItem *closeButton,
                                          QQuickItem *attribution, const QVariantMap &run)
    {
        // 从当前保存按钮经真实 Tab 遍历到右上角关闭，再用 Enter 激活。
        QTest::keyClick(window, Qt::Key_Tab);
        for (int step = 0; step < 20 && !closeButton->hasActiveFocus(); ++step)
            QTest::keyClick(window, Qt::Key_Tab);
        QTRY_VERIFY(closeButton->hasActiveFocus());
        QTest::keyClick(window, Qt::Key_Return);
        QTRY_VERIFY(!dialog->property("visible").toBool());
        QVERIFY(QMetaObject::invokeMethod(dialog, "openForRun", Q_ARG(QVariant, QVariant(run))));
        QTRY_VERIFY(dialog->property("visible").toBool());
        QTRY_VERIFY(attribution->property("checked").toBool());
        QTRY_VERIFY(findVisual(window->contentItem(), QStringLiteral("reflectionShareCard"))->property("showAttribution").toBool());
        QTest::keyClick(window, Qt::Key_Escape);
        QTRY_VERIFY(!dialog->property("visible").toBool());
        QVERIFY(QMetaObject::invokeMethod(dialog, "openForRun", Q_ARG(QVariant, QVariant(run))));
        QTRY_VERIFY(dialog->property("visible").toBool());
        QTest::mouseClick(window, Qt::LeftButton, Qt::NoModifier,
                          closeButton->mapToScene(closeButton->boundingRect().center()).toPoint());
        QTRY_VERIFY(!dialog->property("visible").toBool());
    }

    static void verifyBatchPreviewScrolling(QQuickWindow *window, QQuickItem *preview, QQuickItem *individuals,
                                            QQuickItem *save, QQuickItem *mode, QObject *flickable,
                                            const QList<QQuickItem *> &cards)
    {
        QTRY_COMPARE(flickable->property("contentHeight").toReal(), individuals->height());
        QVERIFY(flickable->property("contentHeight").toReal() > preview->height());
        const QPointF savePosition = save->mapToScene(QPointF());
        const QPointF modePosition = mode->mapToScene(QPointF());
        const QPointF wheelPosition = preview->mapToScene(QPointF(preview->width() / 2, preview->height() / 2));
        QWheelEvent wheel(wheelPosition, window->mapToGlobal(wheelPosition.toPoint()), QPoint(), QPoint(0, -120),
                          Qt::NoButton, Qt::NoModifier, Qt::NoScrollPhase, false);
        QCoreApplication::sendEvent(window, &wheel);
        QTRY_VERIFY(flickable->property("contentY").toReal() > 0);
        // 到达末尾后仍能看到最后一张卡片，顶部输出方式和底部保存按钮保持固定。
        flickable->setProperty("contentY", individuals->height() - preview->height());
        const QPointF lastBottom = cards.last()->mapToItem(preview, QPointF(0, cards.last()->height()));
        QVERIFY(qAbs(lastBottom.y() - preview->height()) < 2);
        QCOMPARE(save->mapToScene(QPointF()), savePosition);
        QCOMPARE(mode->mapToScene(QPointF()), modePosition);
        flickable->setProperty("contentY", 0);
    }

    static void verifyIndividualPngs(const mr::ReflectionShareController &controller,
                                     const QList<QQuickItem *> &cards, QQuickItem *firstBody, QQuickItem *secondBody,
                                     qreal ratio, const QString &evidence, const QString &themeName)
    {
        QStringList paths;
        for (int index = 0; index < cards.size(); ++index) {
            const QString path = controller.batchResults().at(index).toMap().value(QStringLiteral("path")).toString();
            paths.append(path);
            const QImage png(path);
            QVERIFY(!png.isNull());
            if (!evidence.isEmpty())
                QVERIFY(png.save(QDir(evidence).filePath(QStringLiteral("individual-%1-%2.png").arg(index + 1).arg(themeName))));
            QVERIFY2(paperJoinsAreContinuous(png, cards.at(index), ratio), "长图顶底拼接处出现整行黑线或透明空隙");
            QCOMPARE(png.pixelColor(0, 0).alpha(), 255);
            QCOMPARE(png.pixelColor(png.width() - 1, png.height() - 1).alpha(), 255);
            QCOMPARE(png.size(), QSize(qCeil(cards.at(index)->width()), qCeil(cards.at(index)->height())) * ratio);
            QVERIFY(finalTextHasInk(png, index == 0 ? firstBody : secondBody, cards.at(index), ratio));

        }
        QVERIFY(paths[0] != paths[1]);
        QCOMPARE(QFileInfo(paths[0]).fileName(), QStringLiteral("导随心得_时间未知_副本 first.png"));
    }

private Q_SLOTS:
    void bundledFontsUseOriginalStaticGlyphs_data() {
        QTest::addColumn<QString>("family");
        QTest::addColumn<QString>("resource");
        QTest::addColumn<bool>("bold");
        QTest::newRow("serif-regular") << QStringLiteral("MR Reflection Serif")
            << QStringLiteral(":/resources/fonts/NotoSerifSC-Regular.otf") << false;
        QTest::newRow("serif-bold") << QStringLiteral("MR Reflection Serif")
            << QStringLiteral(":/resources/fonts/NotoSerifSC-Bold.otf") << true;
        QTest::newRow("sans-regular") << QStringLiteral("Noto Sans SC")
            << QStringLiteral(":/resources/fonts/NotoSansSC-Regular.otf") << false;
    }

    void bundledFontsUseOriginalStaticGlyphs() {
        QFETCH(QString, family);
        QFETCH(QString, resource);
        QFETCH(bool, bold);
        QFont requested(family);
        requested.setPixelSize(40);
        requested.setBold(bold);
        QCOMPARE(QFontInfo(requested).family(), family);
        const QRawFont actual = QRawFont::fromFont(requested);
        const QRawFont expected(resource, 40);
        QVERIFY(actual.isValid());
        QVERIFY(expected.isValid());
        // 比较实际字体轮廓表，既防止系统字体替换，也防止变量字体或伪粗体冒充静态字重。
        const QByteArray outlines = expected.fontTable("CFF ");
        QVERIFY(!outlines.isEmpty());
        QCOMPARE(actual.fontTable("CFF "), outlines);
        for (QChar character : QStringLiteral("末日暗影亚马乌罗提赤色熊喵龙巢神殿导随记录心得"))
            QVERIFY2(actual.supportsCharacter(character), qPrintable(QString(character)));
        if (resource.contains(QStringLiteral("NotoSerifSC"))) {
            // Curve 的字形缓存会把字体复制到64px；Windows按同名字体重新选脸时，
            // cmap/CFF读对也可能取错轮廓，所以必须检查缩放后的实际路径。
            QRawFont resized = actual;
            resized.setPixelSize(64);
            const QRawFont expected64(resource, 64);
            for (QChar character : QStringLiteral("日马提ヴォルカニック・ハートABC")) {
                const QString sample(character);
                const auto glyphs = resized.glyphIndexesForString(sample);
                const auto expectedGlyphs = expected64.glyphIndexesForString(sample);
                QCOMPARE(glyphs, expectedGlyphs);
                QVERIFY(!glyphs.isEmpty() && glyphs.first() != 0);
                const QPainterPath path = resized.pathForGlyph(glyphs.first());
                const QPainterPath reference = expected64.pathForGlyph(expectedGlyphs.first());
                QVERIFY2(!path.isEmpty() && !reference.isEmpty(), qPrintable(sample));
                QCOMPARE(path, reference);
            }
        }
    }

    void referenceHeadingFitsBeforeElidingName() {
        QQmlEngine engine;
        QList<QQmlError> warnings;
        connect(&engine, &QQmlEngine::warnings, this, [&warnings](const QList<QQmlError> &errors) { warnings.append(errors); });
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import MentorRecorder
Window { visible: true; width: 1200; height: 600
    ReflectionShareHeading { objectName: "heading"; width: 1000
        gameIdentity: "测试冒险者@白银乡"; countLabel: "共 3 条"
        dateOptions: ["2026.10.09 – 2026.10.12", "2026.10.09 – 10.12", "10.09 – 10.12"]
    }
})", QUrl());
        std::unique_ptr<QObject> root(component.create());
        QVERIFY2(root, qPrintable(component.errorString()));
        auto *window = qobject_cast<QQuickWindow *>(root.get());
        QVERIFY(QTest::qWaitForWindowExposed(window));
        auto *heading = findVisual(window->contentItem(), QStringLiteral("heading"));
        auto *name = findVisual(heading, QStringLiteral("reflectionShareCharacterName"));
        auto *server = findVisual(heading, QStringLiteral("reflectionShareServerName"));
        auto *date = findVisual(heading, QStringLiteral("reflectionShareTimeText"));
        QVERIFY(name && server && date);
        QStringList seen;
        bool elided = false;
        // 从宽到窄跨过实际字体的三个日期档位；省略角色名必须发生在最后一档之后。
        for (int width = 1000; width >= 360; width -= 8) {
            heading->setWidth(width);
            QTest::qWait(16);
            const QString selected = heading->property("dateLabel").toString();
            if (seen.isEmpty() || seen.last() != selected) seen.append(selected);
            if (name->property("truncated").toBool()) {
                QCOMPARE(selected, QStringLiteral("10.09 – 10.12"));
                elided = true;
            }
            QCOMPARE(server->property("text").toString(), QStringLiteral("@白银乡"));
            QTRY_VERIFY(server->mapRectToItem(heading, server->boundingRect()).right() <= width + 1);
            QTRY_VERIFY(date->mapRectToItem(heading, date->boundingRect()).right() <= width + 1);
        }
        QCOMPARE(seen, QStringList({QStringLiteral("2026.10.09 – 2026.10.12"),
                                    QStringLiteral("2026.10.09 – 10.12"), QStringLiteral("10.09 – 10.12")}));
        QVERIFY(elided);
        heading->setProperty("gameIdentity", QStringLiteral("OnlyName"));
        QTRY_VERIFY(!server->isVisible());
        QCOMPARE(name->property("text").toString(), QStringLiteral("OnlyName"));
        heading->setProperty("gameIdentity", QString());
        QTRY_VERIFY(!findVisual(heading, QStringLiteral("reflectionShareGameIdentity"))->isVisible());
        QVERIFY2(warnings.isEmpty(), warnings.isEmpty() ? "" : qPrintable(warnings.first().toString()));
    }
    void referencePicturesAndEmptyMood() {
        ShareThemeState theme;
        mr::Formatters formatters;
        mr::JobCatalog jobs;
        mr::ReflectionShareController controller;
        QQmlEngine engine;
        engine.rootContext()->setContextProperty(QStringLiteral("App"), &theme);
        engine.rootContext()->setContextProperty(QStringLiteral("Fmt"), &formatters);
        engine.rootContext()->setContextProperty(QStringLiteral("Jobs"), &jobs);
        QList<QQmlError> warnings;
        connect(&engine, &QQmlEngine::warnings, this, [&warnings](const QList<QQmlError> &errors) { warnings.append(errors); });
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import MentorRecorder
Window { visible: true; width: 1180; height: 900
    ReflectionShareCard { width: 1140; gameIdentity: "赤色熊喵@龙巢神殿" }
})", QUrl());
        std::unique_ptr<QObject> root(component.create());
        QVERIFY2(root, qPrintable(component.errorString()));
        auto *window = qobject_cast<QQuickWindow *>(root.get());
        QVERIFY(QTest::qWaitForWindowExposed(window));
        auto *card = findVisual(window->contentItem(), QStringLiteral("reflectionShareCard"));
        QVariantMap run{{QStringLiteral("duty_name"), QStringLiteral("末日暗影亚马乌罗提")},
            {QStringLiteral("matched_at_utc"), QStringLiteral("2026-10-13T12:33:00Z")},
            {QStringLiteral("job_id"), 40}, {QStringLiteral("job_name"), QStringLiteral("贤者")},
            {QStringLiteral("result"), QStringLiteral("COMPLETED")},
            {QStringLiteral("reflection"), QVariantMap{{QStringLiteral("text"), QStringLiteral("DPS伤害太低差点团灭")}, {QStringLiteral("mood"), QStringLiteral("good")}}}};
        card->setProperty("runData", run);
        QTRY_COMPARE(card->height(), 506.0);
        auto *date = findVisual(card, QStringLiteral("reflectionShareExportDate"));
        auto *brand = findVisual(card, QStringLiteral("reflectionShareBrand"));
        auto *footer = findVisual(card, QStringLiteral("reflectionShareFooterLine"));
        QVERIFY(date && brand && footer);
        QCOMPARE(date->mapToItem(card, QPointF(0, date->baselineOffset())).y(),
                 brand->mapToItem(card, QPointF(0, brand->baselineOffset())).y());
        QVERIFY(footer->width() <= 520);
        QTemporaryDir output;
        QSignalSpy saved(&controller, &mr::ReflectionShareController::saved);
        const QString evidence = qEnvironmentVariable("MR_REFLECTION_SHARE_SCREENSHOT_DIR");
        const auto save = [&](const QString &name) {
            controller.saveTo(card, output.filePath(name));
        };
        const auto keep = [&](const QString &name) {
            const QImage png(output.filePath(name));
            return !png.isNull() && (evidence.isEmpty() || png.save(QDir(evidence).filePath(name)));
        };
        save(QStringLiteral("reference-single.png"));
        QTRY_COMPARE(saved.count(), 1);
        QVERIFY(keep(QStringLiteral("reference-single.png")));
        const QString singleAlignment = headingInkAlignmentError(QImage(output.filePath(QStringLiteral("reference-single.png"))),
                                                                 card, window->effectiveDevicePixelRatio());
        QVERIFY2(singleAlignment.isEmpty(), qPrintable(singleAlignment));
        auto *body = findVisual(card, QStringLiteral("reflectionShareFullText"));
        auto *stamp = findVisual(card, QStringLiteral("reflectionShareResultStamp"));
        QVariantMap edge = run;
        edge.insert(QStringLiteral("duty_name"), QStringLiteral("超长副本名需要按词组换行并完整保留").repeated(5));
        const QString longBody = QStringLiteral("标点与（括号）以及 English words 12345 保持可读，正文逐行增长，不会压住卷尾印章。\n").repeated(8) + QStringLiteral("最后一行完整保留。");
        edge.insert(QStringLiteral("reflection"), QVariantMap{{QStringLiteral("text"), longBody}});
        card->setProperty("runData", edge);
        auto *title = findVisual(card, QStringLiteral("reflectionShareDutyTitle"));
        QTRY_VERIFY(title->property("lineCount").toInt() > 2);
        QVERIFY(!title->property("truncated").toBool());
        QVERIFY(!findVisual(card, QStringLiteral("reflectionShareMoodTag"))->isVisible());
        QCOMPARE(body->property("text").toString(), longBody);
        QVERIFY(stamp->y() >= body->y() + body->height() - 12);
        save(QStringLiteral("reference-single-edge.png"));
        QTRY_COMPARE(saved.count(), 2);
        QVERIFY(keep(QStringLiteral("reference-single-edge.png")));
        QVERIFY(finalTextHasInk(QImage(output.filePath(QStringLiteral("reference-single-edge.png"))), body, card, window->effectiveDevicePixelRatio()));
        QCOMPARE(card->property("runData").toMap(), edge);
        QList<int> fontStages;
        // 与实际绘制使用相同的 QML 字体回退；C++ 默认字体替代可能选到另一张字形表。
        QQmlComponent metricComponent(&engine);
        metricComponent.setData("import QtQuick\nTextMetrics {}", QUrl());
        std::unique_ptr<QObject> measuredText(metricComponent.create());
        QVERIFY2(measuredText, qPrintable(metricComponent.errorString()));
        for (int length = 1; length <= 48; ++length) {
            QVariantMap titled = run;
            const QString name = QStringLiteral("副").repeated(length);
            titled.insert(QStringLiteral("duty_name"), name);
            card->setProperty("runData", titled);
            int expectedSize = 32;
            for (const int candidate : {48, 42, 36, 32}) {
                QFont font = title->property("font").value<QFont>();
                font.setPixelSize(candidate);
                measuredText->setProperty("font", QVariant::fromValue(font));
                measuredText->setProperty("text", name);
                if (measuredText->property("advanceWidth").toReal() <= title->width()) {
                    expectedSize = candidate;
                    break;
                }
            }
            QTRY_COMPARE(title->property("font").value<QFont>().pixelSize(), expectedSize);
            if (fontStages.isEmpty() || fontStages.last() != expectedSize) fontStages.append(expectedSize);
        }
        QCOMPARE(fontStages, QList<int>({48, 42, 36, 32}));
        QVariantMap imported = run;
        imported.remove(QStringLiteral("matched_at_utc"));
        imported.insert(QStringLiteral("import_metadata"), QVariantMap{{QStringLiteral("source_recorded_at"), QStringLiteral("2026-10-13 20:33:00")}});
        card->setProperty("runData", imported);
        auto *heading = findVisual(card, QStringLiteral("reflectionShareSingleHeading"));
        auto *server = findVisual(heading, QStringLiteral("reflectionShareServerName"));
        auto *sourceTime = findVisual(heading, QStringLiteral("reflectionShareTimeText"));
        auto *job = findVisual(card, QStringLiteral("reflectionShareJobIcon"));
        QVERIFY(server && sourceTime && job);
        QTRY_VERIFY(server->width() >= server->implicitWidth() - 1);
        QVERIFY(sourceTime->property("text").toString().contains(QStringLiteral("游玩时间未知")));
        QTRY_VERIFY(sourceTime->mapRectToItem(card, sourceTime->boundingRect()).bottom()
                    <= job->mapRectToItem(card, job->boundingRect()).top());
        save(QStringLiteral("reference-source-time.png"));
        QTRY_COMPARE(saved.count(), 3);
        QVERIFY(keep(QStringLiteral("reference-source-time.png")));
        const QString hugeServer = QStringLiteral("极长服务器名字").repeated(12);
        card->setProperty("gameIdentity", QStringLiteral("冒险者@") + hugeServer);
        QTRY_VERIFY(server->width() >= 100);
        QCOMPARE(server->property("text").toString(), QStringLiteral("@") + hugeServer);
        QTRY_VERIFY(sourceTime->mapRectToItem(card, sourceTime->boundingRect()).bottom()
                    <= job->mapRectToItem(card, job->boundingRect()).top());
        QVERIFY(server->mapRectToItem(heading, server->boundingRect()).right() <= heading->width() + 1);
        card->setProperty("gameIdentity", QStringLiteral("赤色熊喵@龙巢神殿"));
        verifyReferenceCollection(run, controller, card, window, output, saved, evidence);
        if (QTest::currentTestFailed()) return;

        QVERIFY2(warnings.isEmpty(), warnings.isEmpty() ? "" : qPrintable(warnings.first().toString()));
    }
    void identityWrappingAndReadableNames() {
        ShareThemeState theme;
        mr::Formatters formatters;
        mr::JobCatalog jobs;
        mr::ReflectionShareController controller;
        mr::AppSettings settings;
        const QString oldIdentity = settings.shareGameIdentity();
        const auto restore = qScopeGuard([&] { settings.setShareGameIdentity(oldIdentity); });
        settings.setShareGameIdentity(QStringLiteral("测试冒险者@白银乡"));
        {
            mr::AppSettings reopened;
            QCOMPARE(reopened.shareGameIdentity(), QStringLiteral("测试冒险者@白银乡"));
        }
        QQmlEngine engine;
        engine.rootContext()->setContextProperty(QStringLiteral("App"), &theme);
        engine.rootContext()->setContextProperty(QStringLiteral("Fmt"), &formatters);
        engine.rootContext()->setContextProperty(QStringLiteral("Jobs"), &jobs);
        engine.rootContext()->setContextProperty(QStringLiteral("Settings"), &settings);
        engine.rootContext()->setContextProperty(QStringLiteral("ReflectionShare"), &controller);
        engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QList<QQmlError> warnings;
        connect(&engine, &QQmlEngine::warnings, this, [&warnings](const QList<QQmlError> &errors) { warnings.append(errors); });
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow { visible: true; width: 1100; height: 900
    ReflectionShareDialog { objectName: "dialog"; anchors.centerIn: Overlay.overlay }
})", QUrl());
        std::unique_ptr<QObject> root(component.create());
        QVERIFY2(root, qPrintable(component.errorString()));
        auto *window = qobject_cast<QQuickWindow *>(root.get());
        QVERIFY(QTest::qWaitForWindowExposed(window));
        auto *dialog = root->findChild<QObject *>(QStringLiteral("dialog"));
        const QVariantMap run{{QStringLiteral("run_id"), QStringLiteral("readable-a")},
            {QStringLiteral("duty_name"), QStringLiteral("休养胜地布雷福洛克斯野营地")},
            {QStringLiteral("matched_at_utc"), QStringLiteral("2026-10-16T06:28:00Z")},
            {QStringLiteral("job_id"), 21}, {QStringLiteral("job_name"), QStringLiteral("战士")},
            {QStringLiteral("result"), QStringLiteral("COMPLETED")},
            {QStringLiteral("reflection"), QVariantMap{{QStringLiteral("text"), QStringLiteral("是芽芽！还有初见！同事的铭牌也是非常可爱的！")},
                                                       {QStringLiteral("mood"), QStringLiteral("good")}}}};
        QVariantMap other = run;
        other.insert(QStringLiteral("run_id"), QStringLiteral("readable-b"));
        QVERIFY(openShareRuns(dialog, {run, other}));
        QTRY_VERIFY(dialog->property("visible").toBool());
        auto *input = findVisual(window->contentItem(), QStringLiteral("reflectionShareIdentityInput"));
        auto *option = findVisual(window->contentItem(), QStringLiteral("reflectionShareIdentityOption"));
        auto *mode = findVisual(window->contentItem(), QStringLiteral("reflectionImageOutputMode"));
        QVERIFY(input && option && mode);
        QCOMPARE(input->property("text").toString(), settings.shareGameIdentity());
        QCOMPARE(option->property("checked").toBool(), true);
        QTRY_VERIFY(mode->hasActiveFocus());
        QTest::keyClick(window, Qt::Key_Tab);
        QTRY_VERIFY(option->hasActiveFocus());
        QTest::keyClick(window, Qt::Key_Space);
        QCOMPARE(option->property("checked").toBool(), false);
        QTest::keyClick(window, Qt::Key_Space);
        QTest::keyClick(window, Qt::Key_Tab);
        QTRY_VERIFY(input->hasActiveFocus());
        const auto cards = completeCards(window->contentItem());
        QQuickItem *individual = nullptr;
        for (auto *card : cards) if (card->isVisible()) { individual = card; break; }
        QVERIFY(individual);
        const QString expectedTime = QDateTime::fromString(QStringLiteral("2026-10-16T06:28:00Z"), Qt::ISODate)
            .toLocalTime().toString(QStringLiteral("yyyy-MM-dd_HH-mm-ss"));
        const QString baseName = QStringLiteral("导随心得_%1_休养胜地布雷福洛克斯野营地.png").arg(expectedTime);
        QCOMPARE(controller.suggestedFileName(individual), baseName);
        auto *identity = findVisual(individual, QStringLiteral("reflectionShareGameIdentity"));
        QVERIFY(identity && identity->isVisible());
        const QString evidence = qEnvironmentVariable("MR_REFLECTION_SHARE_SCREENSHOT_DIR");
        QTemporaryDir output;
        QSignalSpy saved(&controller, &mr::ReflectionShareController::saved);
        IdentityStage stage{controller, settings, run, baseName, evidence, output, saved};
        stage.window = window;
        stage.dialog = dialog;
        stage.input = input;
        stage.option = option;
        stage.mode = mode;
        stage.individual = individual;
        stage.identity = identity;
        verifyIdentityVisibilityAndWrapping(stage);
        if (QTest::currentTestFailed()) return;

        verifyIdentityCombinedAndKeyboard(stage);
        if (QTest::currentTestFailed()) return;

        verifyReadableFileNamesAndCollisionSuffixes(stage);
        if (QTest::currentTestFailed()) return;

        QVERIFY2(warnings.isEmpty(), warnings.isEmpty() ? "" : qPrintable(warnings.first().toString()));
    }
    void initTestCase() {
        const QString root = QString::fromUtf8(MR_DESKTOP_QML_DIR);
        qmlRegisterSingletonType(QUrl::fromLocalFile(root + QStringLiteral("/Theme.qml")),
                                 "MentorRecorder", 1, 0, "Theme");
        for (const auto &directory : {QStringLiteral("/components"), QStringLiteral("/dialogs")})
            for (const auto &file : QDir(root + directory).entryList({QStringLiteral("*.qml")}, QDir::Files)) {
                const QByteArray name = file.chopped(4).toUtf8();
                qmlRegisterType(QUrl::fromLocalFile(root + directory + QLatin1Char('/') + file),
                                "MentorRecorder", 1, 0, name.constData());
            }
    }
    void localizedExportTitlesRemainComplete_data() {
        QTest::addColumn<QString>("language");
        QTest::addColumn<bool>("single");
        QTest::addColumn<int>("contentId");
        for (const QString &language : {QStringLiteral("en"), QStringLiteral("ja")}) {
            QTest::newRow(qPrintable(language + QStringLiteral("-single"))) << language << true << 1057;
            QTest::newRow(qPrintable(language + QStringLiteral("-collection"))) << language << false << 1057;
        }
        QTest::newRow("en-single-closing-parenthesis") << QStringLiteral("en") << true << 175;
        QTest::newRow("ja-single-closing-parenthesis") << QStringLiteral("ja") << true << 863;
        QTest::newRow("ja-collection-closing-quote") << QStringLiteral("ja") << false << 620;
    }
    void localizedExportTitlesRemainComplete() {
        QFETCH(QString, language);
        QFETCH(bool, single);
        QFETCH(int, contentId);
        const QString previousLanguage = mr::LanguageManager::currentCode();
        const QLocale previousLocale;
        const QFont previousFont = QGuiApplication::font();
        mr::LanguageManager lang(nullptr);
        mr::GameNames names;
        mr::Formatters formatters;
        mr::JobCatalog jobs;
        mr::ReflectionShareController controller;
        QQmlEngine engine;
        const auto restoreLanguage = qScopeGuard([&] {
            lang.attachEngine(nullptr);
            lang.setLanguage(previousLanguage);
            QLocale::setDefault(previousLocale);
            QGuiApplication::setFont(previousFont);
        });
        lang.initialize(language);
        QVERIFY(QCoreApplication::translate("ReflectionShareCard", "导随记录") != QStringLiteral("导随记录"));
        lang.attachEngine(&engine);
        engine.rootContext()->setContextProperty(QStringLiteral("Fmt"), &formatters);
        engine.rootContext()->setContextProperty(QStringLiteral("Jobs"), &jobs);
        engine.rootContext()->setContextProperty(QStringLiteral("Names"), &names);
        engine.rootContext()->setContextProperty(QStringLiteral("Lang"), &lang);
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import MentorRecorder
Window { visible: true; width: 1180; height: 1000
    ReflectionShareCard { width: 1140 }
})", QUrl());
        std::unique_ptr<QObject> root(component.create());
        QVERIFY2(root, qPrintable(component.errorString()));
        auto *window = qobject_cast<QQuickWindow *>(root.get());
        QVERIFY(QTest::qWaitForWindowExposed(window));
        auto *card = findVisual(window->contentItem(), QStringLiteral("reflectionShareCard"));
        QVERIFY(card);
        QVariantMap run{{QStringLiteral("content_id"), contentId},
                        {QStringLiteral("duty_name"), QStringLiteral("记录中的中文名")},
                        {QStringLiteral("job_id"), 19}, {QStringLiteral("result"), QStringLiteral("COMPLETED")},
                        {QStringLiteral("matched_at_utc"), QStringLiteral("2026-10-18T12:00:00Z")},
                        {QStringLiteral("reflection"), QVariantMap{{QStringLiteral("text"), QStringLiteral("Full exported reflection.")}}}};
        if (single) card->setProperty("runData", run);
        else card->setProperty("runsData", QVariantList{run, run});
        QTRY_COMPARE(visibleEntries(card).size(), single ? 1 : 2);
        auto *entry = visibleEntries(card).first();
        auto *title = findVisual(entry, QStringLiteral("reflectionShareDutyTitle"));
        auto *metadata = findVisual(entry, QStringLiteral("reflectionShareResultText"));
        QVERIFY(title && metadata);
        // 这一条专测行尾右括号；轮廓度量准确后，原来的640宽可容纳整行。
        // 显式收窄此测试样本，继续覆盖真实换行后的括号归属。
        if (single && language == QLatin1String("en") && contentId == 175)
            title->setWidth(320);
        const QString expected = names.duty(contentId, run.value(QStringLiteral("duty_name")).toString());
        QVERIFY(!expected.isEmpty());
        QVERIFY(expected != run.value(QStringLiteral("duty_name")).toString());
        QTRY_COMPARE(QString(title->property("text").toString()).remove(QLatin1Char('\n')), expected);
        title->ensurePolished();
        QVERIFY(!title->property("truncated").toBool());
        QVERIFY(metadata->y() >= title->y() + title->height());
        if (single) {
            QVERIFY(title->property("lineCount").toInt() >= 2);
            const QString wrapped = title->property("text").toString();
            if (contentId == 1057 && language == QLatin1String("en")) {
                QVERIFY(wrapped.section(QLatin1Char('\n'), 0, 0).contains(QStringLiteral("Custom Match")));
            } else if (contentId == 1057) {
                for (const QString &word : {QStringLiteral("ベイサイド"), QStringLiteral("バトルグラウンド"), QStringLiteral("カスタムマッチ")})
                    QVERIFY2(wrapped.contains(word), qPrintable(wrapped));
                QVERIFY(!wrapped.contains(QStringLiteral("\n・")));
            } else if (language == QLatin1String("ja") && contentId == 863) {
                QVERIFY2(wrapped.section(QLatin1Char('\n'), 0, 0).contains(QStringLiteral("ヴォルカニック・")), qPrintable(wrapped));
            }
        } else {
            auto *heading = findVisual(card, QStringLiteral("reflectionShareCollectionTitle"));
            auto *details = findVisual(card, QStringLiteral("reflectionShareCollectionMeta"));
            QVERIFY(heading && details);
            heading->ensurePolished();
            QVERIFY(heading->property("paintedWidth").toReal() <= heading->width() + 0.5);
            QVERIFY(heading->x() + heading->width() <= 706);
            QVERIFY(details->y() >= heading->y() + heading->height());
            QVERIFY(!heading->property("truncated").toBool());
        }
        const QString closingPunctuation = QStringLiteral(")）]】」』・：");
        for (const QString &line : title->property("text").toString().split(QLatin1Char('\n'))) {
            const QString trimmed = line.trimmed();
            QVERIFY2(trimmed.isEmpty() || !closingPunctuation.contains(trimmed.front()), qPrintable(title->property("text").toString()));
        }
        auto *date = findVisual(card, QStringLiteral("reflectionShareExportDate"));
        auto *brand = findVisual(card, QStringLiteral("reflectionShareBrand"));
        auto *footer = findVisual(card, QStringLiteral("reflectionShareFooterLine"));
        QVERIFY(date && brand && footer);
        QCOMPARE(brand->property("text").toString(), language == QLatin1String("en")
            ? QStringLiteral("Powered by Mentor Recorder") : QStringLiteral("Powered by メンターレコーダー"));
        QVERIFY(footer->property("stacked").toBool());
        QVERIFY(brand->y() + brand->baselineOffset() >= date->y() + date->baselineOffset() + 28);
        QVERIFY(qAbs(brand->x() + brand->width() / 2 - footer->width() / 2) < 0.5);
        QVERIFY(!date->property("truncated").toBool());
        QVERIFY(!brand->property("truncated").toBool());
        QVERIFY(brand->property("implicitWidth").toReal() <= brand->width() + 0.5);
        QVERIFY(footer->width() <= 520);
        const qreal scale = card->property("templateScale").toReal();
        const QRectF brandBounds = brand->mapRectToItem(card, brand->boundingRect());
        QVERIFY(brandBounds.bottom() <= card->height() - 36 * scale);
        QTemporaryDir output;
        QSignalSpy saved(&controller, &mr::ReflectionShareController::saved);
        const QString filename = QStringLiteral("localized-%1-%2%3.png")
            .arg(language, single ? QStringLiteral("single") : QStringLiteral("collection"),
                 contentId == 1057 ? QString() : QStringLiteral("-%1").arg(contentId));
        const QString path = output.filePath(filename);
        controller.saveTo(card, path);
        QTRY_COMPARE(saved.count(), 1);
        QVERIFY(!QImage(path).isNull());
        const QString evidence = qEnvironmentVariable("MR_REFLECTION_SHARE_SCREENSHOT_DIR");
        if (!evidence.isEmpty()) {
            QVERIFY(QDir().mkpath(evidence));
            QVERIFY(QImage(path).save(QDir(evidence).filePath(filename)));
        }
        lang.attachEngine(nullptr);
    }
    void actualShareDialogAtSmallAndLargeSizes_data() {
        QTest::addColumn<QSize>("size");
        QTest::addColumn<bool>("dark");
        QTest::newRow("small-light") << QSize(720, 560) << false;
        QTest::newRow("large-dark") << QSize(1100, 900) << true;
    }
    void actualShareDialogAtSmallAndLargeSizes() {
        QFETCH(QSize, size);
        QFETCH(bool, dark);
        ShareThemeState theme;
        theme.dark = dark;
        mr::Formatters formatters;
        mr::JobCatalog jobs;
        mr::ReflectionShareController controller;
        QQmlEngine engine;
        engine.rootContext()->setContextProperty(QStringLiteral("App"), &theme);
        engine.rootContext()->setContextProperty(QStringLiteral("Fmt"), &formatters);
        engine.rootContext()->setContextProperty(QStringLiteral("Jobs"), &jobs);
        engine.rootContext()->setContextProperty(QStringLiteral("ReflectionShare"), &controller);
        engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    visible: true; width: 720; height: 560
    ReflectionShareDialog { objectName: "dialog"; anchors.centerIn: Overlay.overlay }
})", QUrl());
        std::unique_ptr<QObject> root(component.create());
        QVERIFY2(root, qPrintable(component.errorString()));
        auto *window = qobject_cast<QQuickWindow *>(root.get());
        window->resize(size);
        QVERIFY(QTest::qWaitForWindowExposed(window));
        const QString fullText = QStringLiteral("中文心得完整段落：长内容必须全部保留。\n").repeated(75).trimmed();
        QVariantMap run{{QStringLiteral("run_id"), QStringLiteral("internal-marker-not-for-share")},
            {QStringLiteral("duty_name"), QStringLiteral("很长的中文副本名称用于测试完整换行")},
            {QStringLiteral("job_name"), QStringLiteral("白魔法师")},
            {QStringLiteral("job_id"), 24}, {QStringLiteral("result"), QStringLiteral("UNKNOWN")},
            {QStringLiteral("reflection"), QVariantMap{{QStringLiteral("text"), fullText}, {QStringLiteral("mood"), QStringLiteral("unknown")}}},
            {QStringLiteral("import_metadata"), QVariantMap{{QStringLiteral("source_recorded_at"), QStringLiteral("2026-10-08 18:44:29")}, {QStringLiteral("incomplete"), true}}}};
        QObject *dialog = root->findChild<QObject *>(QStringLiteral("dialog"));
        QVERIFY(dialog);
        QVERIFY(QMetaObject::invokeMethod(dialog, "openForRun", Q_ARG(QVariant, QVariant(run))));
        QTRY_VERIFY(dialog->property("visible").toBool());
        auto *closeButton = findVisual(window->contentItem(), QStringLiteral("reflectionShareCloseButton"));
        auto *dialogContent = dialog->property("contentItem").value<QQuickItem *>();
        QVERIFY(closeButton && dialogContent);
        QTRY_VERIFY(qAbs(closeButton->mapRectToItem(dialogContent, closeButton->boundingRect()).top()) < 1);
        QTRY_VERIFY(qAbs(closeButton->mapRectToItem(dialogContent, closeButton->boundingRect()).right()
                        - dialogContent->width()) < 1);
        QCOMPARE(closeButton->property("iconName").toString(), QStringLiteral("x"));
        auto *card = findVisual(window->contentItem(), QStringLiteral("reflectionShareCard"));
        auto *body = findVisual(window->contentItem(), QStringLiteral("reflectionShareFullText"));
        QVERIFY(card && body);
        QTRY_VERIFY(card->height() > size.height());
        QCOMPARE(body->property("text").toString(), fullText);
        QVERIFY(body->mapRectToItem(card, body->boundingRect()).bottom() <= card->height());
        QString rendered;
        const auto collectText = [&rendered](auto &&self, QQuickItem *item) -> void {
            rendered += item->property("text").toString() + QLatin1Char('\n');
            for (auto *child : item->childItems()) self(self, child);
        };
        collectText(collectText, card);
        QVERIFY(rendered.contains(QStringLiteral("未记录心情")));
        QVERIFY(rendered.contains(QStringLiteral("原站记录时间：2026-10-08 18:44:29")));
        QVERIFY(!rendered.contains(QStringLiteral("internal-marker")));
        auto *attribution = findVisual(window->contentItem(), QStringLiteral("reflectionShareAttributionOption"));
        QVERIFY(attribution);
        QVERIFY(attribution->property("checked").toBool());
        QVERIFY(card->property("showAttribution").toBool());
        attribution->forceActiveFocus();
        QTest::keyClick(window, Qt::Key_Space);
        QTRY_VERIFY(!attribution->property("checked").toBool());
        QTRY_VERIFY(!card->property("showAttribution").toBool());
        QTemporaryDir target;
        const QString path = target.filePath(QStringLiteral("actual-dialog.png"));
        QSignalSpy saved(&controller, &mr::ReflectionShareController::saved);
        controller.saveTo(card, path);
        QTRY_COMPARE(saved.count(), 1);
        const QImage png(path);
        const qreal ratio = window->effectiveDevicePixelRatio();
        QCOMPARE(png.size(), QSize(qCeil(card->width()), qCeil(card->height())) * ratio);
        // ScrollView 的视口优化也不能省略图片底部的文字节点。
        QVERIFY2(finalTextHasInk(png, body, card, ratio), "PNG 最后一段文字缺失或不可读");
        QCOMPARE(dialog->property("runData").toMap(), run);
        verifyActualDialogClosing(window, dialog, closeButton, attribution, run);
        if (QTest::currentTestFailed()) return;


    }
    void selectedBatchIndividualAndCombinedCompletePng_data() {
        actualShareDialogAtSmallAndLargeSizes_data();
    }
    void selectedBatchIndividualAndCombinedCompletePng() {
        QFETCH(QSize, size);
        QFETCH(bool, dark);
        ShareThemeState theme;
        theme.dark = dark;
        mr::Formatters formatters;
        mr::JobCatalog jobs;
        mr::ReflectionShareController controller;
        QQmlEngine engine;
        engine.rootContext()->setContextProperty(QStringLiteral("App"), &theme);
        engine.rootContext()->setContextProperty(QStringLiteral("Fmt"), &formatters);
        engine.rootContext()->setContextProperty(QStringLiteral("Jobs"), &jobs);
        engine.rootContext()->setContextProperty(QStringLiteral("ReflectionShare"), &controller);
        engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    visible: true; width: 720; height: 560
    ReflectionShareDialog { objectName: "dialog"; anchors.centerIn: Overlay.overlay }
})", QUrl());
        std::unique_ptr<QObject> root(component.create());
        QVERIFY2(root, qPrintable(component.errorString()));
        auto *window = qobject_cast<QQuickWindow *>(root.get());
        window->resize(size);
        QVERIFY(QTest::qWaitForWindowExposed(window));
        QObject *dialog = root->findChild<QObject *>(QStringLiteral("dialog"));
        QVERIFY(dialog);
        const QString firstText = QStringLiteral("第一条完整中文心得 <b>不是 HTML</b>\n").repeated(40).trimmed();
        const QString secondText = QStringLiteral("第二条长文必须保留最后一行。\n").repeated(55).trimmed();
        const auto run = [](const QString &id, const QString &text) {
            return QVariantMap{{QStringLiteral("run_id"), id},
                {QStringLiteral("duty_name"), QStringLiteral("副本 %1").arg(id)},
                {QStringLiteral("job_name"), QStringLiteral("白魔法师")}, {QStringLiteral("job_id"), 24},
                {QStringLiteral("result"), QStringLiteral("COMPLETED")},
                {QStringLiteral("reflection"), QVariantMap{{QStringLiteral("text"), text}}},
                {QStringLiteral("import_metadata"), QVariantMap{{QStringLiteral("source_recorded_at"), QStringLiteral("2024-09-24 08:00:00")}}}};
        };
        const QVariantList runs{run(QStringLiteral("first"), firstText), run(QStringLiteral("second"), secondText)};
        QVERIFY(QMetaObject::invokeMethod(dialog, "openForRuns", Q_ARG(QVariant, QVariant(runs))));
        QTRY_VERIFY(dialog->property("visible").toBool());
        auto *combined = findVisual(window->contentItem(), QStringLiteral("reflectionShareCombinedCard"));
        QVERIFY(combined);
        auto *individuals = findVisual(window->contentItem(), QStringLiteral("reflectionShareIndividualCards"));
        QVERIFY(individuals);
        QTRY_COMPARE(completeCards(individuals).size(), 2);
        const auto cards = completeCards(individuals);
        auto *firstBody = findVisual(cards.at(0), QStringLiteral("reflectionShareFullText"));
        auto *secondBody = findVisual(cards.at(1), QStringLiteral("reflectionShareFullText"));
        QVERIFY(firstBody && secondBody);
        QTRY_VERIFY(individuals->height() > size.height());
        auto *preview = findVisual(window->contentItem(), QStringLiteral("reflectionSharePreview"));
        auto *save = findVisual(window->contentItem(), QStringLiteral("saveReflectionImageButton"));
        auto *mode = findVisual(window->contentItem(), QStringLiteral("reflectionImageOutputMode"));
        QVERIFY(preview && save && mode);
        auto *flickable = qvariant_cast<QObject *>(preview->property("contentItem"));
        QVERIFY(flickable);
        verifyBatchPreviewScrolling(window, preview, individuals, save, mode, flickable, cards);
        if (QTest::currentTestFailed()) return;

        QCOMPARE(firstBody->property("text").toString(), firstText);
        QCOMPARE(secondBody->property("text").toString(), secondText);
        QVERIFY(cards.at(1)->y() >= cards.at(0)->height());
        const QString evidence = qEnvironmentVariable("MR_REFLECTION_SHARE_SCREENSHOT_DIR");
        const QString themeName = dark ? QStringLiteral("dark-large") : QStringLiteral("light-small");
        if (!evidence.isEmpty()) {
            QVERIFY(QDir().mkpath(evidence));
            QTest::qWait(180);
            QVERIFY(window->grabWindow().save(QDir(evidence).filePath(QStringLiteral("batch-preview-%1.png").arg(themeName))));
        }
        const qreal ratio = window->effectiveDevicePixelRatio();
        QTemporaryDir target;
        QSignalSpy saved(&controller, &mr::ReflectionShareController::saved);
        QVariantList entries;
        for (int index = 0; index < cards.size(); ++index)
            entries.append(QVariantMap{{QStringLiteral("run_id"), runs[index].toMap().value(QStringLiteral("run_id"))},
                {QStringLiteral("label"), QStringLiteral("同名/副本")},
                {QStringLiteral("item"), QVariant::fromValue<QObject *>(cards.at(index))}});
        auto *attribution = findVisual(window->contentItem(), QStringLiteral("reflectionShareAttributionOption"));
        QVERIFY(attribution);
        attribution->setProperty("checked", false);
        for (auto *card : cards)
            QTRY_VERIFY(!card->property("showAttribution").toBool());
        controller.saveBatchTo(entries, target.path());
        QVERIFY(!attribution->isEnabled());
        QTRY_VERIFY_WITH_TIMEOUT(!controller.busy(), 20000);
        QVERIFY(attribution->isEnabled());
        QCOMPARE(saved.count(), 2);
        QCOMPARE(controller.successfulCount(), 2);
        QCOMPARE(controller.failedCount(), 0);
        QCOMPARE(controller.batchResults().size(), 2);
        verifyIndividualPngs(controller, cards, firstBody, secondBody, ratio, evidence, themeName);
        if (QTest::currentTestFailed()) return;

        auto *dialogContent = qvariant_cast<QQuickItem *>(dialog->property("contentItem"));
        QVERIFY(dialogContent);
        QTest::qWait(80);
        QVERIFY(save->mapRectToItem(dialogContent, save->boundingRect()).bottom() <= dialogContent->height());
        if (!evidence.isEmpty()) {
            QTest::qWait(80);
            QVERIFY(window->grabWindow().save(QDir(evidence).filePath(QStringLiteral("batch-results-%1.png").arg(themeName))));
        }
        const QString combinedPath = target.filePath(QStringLiteral("combined.png"));
        mode->setProperty("currentIndex", 1);
        QTRY_VERIFY(combined->isVisible());
        QTRY_COMPARE(flickable->property("contentHeight").toReal(), combined->height());
        const auto timelineEntries = visibleEntries(combined);
        QCOMPARE(timelineEntries.size(), 2);
        auto *timelineFirstBody = findVisual(timelineEntries[0], QStringLiteral("reflectionShareFullText"));
        auto *timelineSecondBody = findVisual(timelineEntries[1], QStringLiteral("reflectionShareFullText"));
        QVERIFY(timelineFirstBody && timelineSecondBody);
        QCOMPARE(timelineFirstBody->property("text").toString(), firstText);
        QCOMPARE(timelineSecondBody->property("text").toString(), secondText);
        controller.saveTo(combined, combinedPath);
        QTRY_VERIFY_WITH_TIMEOUT(!controller.busy(), 20000);
        QCOMPARE(saved.count(), 3);
        const QImage png(combinedPath);
        QCOMPARE(png.size(), QSize(qCeil(combined->width()), qCeil(combined->height())) * ratio);
        QVERIFY(finalTextHasInk(png, timelineFirstBody, combined, ratio));
        QVERIFY(finalTextHasInk(png, timelineSecondBody, combined, ratio));
        // 长卷条目间是一张连续的纸，而非独立卡片之间的透明间隔。
        const QPointF gap = timelineEntries[1]->mapToItem(combined, QPointF(570, -38));
        QCOMPARE(png.pixelColor(qRound(gap.x() * ratio), qRound(gap.y() * ratio)).alpha(), 255);
        if (!evidence.isEmpty())
            QVERIFY(png.save(QDir(evidence).filePath(QStringLiteral("combined-%1.png").arg(themeName))));
        QCOMPARE(firstBody->property("text").toString(), firstText);
        QCOMPARE(secondBody->property("text").toString(), secondText);
        // 真正的 Qt 键盘操作：批量窗口打开先聚焦输出方式，Tab 可到关闭，Escape 关闭。
        auto *output = findVisual(window->contentItem(), QStringLiteral("reflectionImageOutputMode"));
        QVERIFY(output);
        output->forceActiveFocus();
        QTest::keyClick(window, Qt::Key_Tab);
        QVERIFY(window->activeFocusItem());
        QTest::keyClick(window, Qt::Key_Escape);
        QTRY_VERIFY(!dialog->property("visible").toBool());
    }
    void scrollMaterialsAndResultBoundaries_data() {
        QTest::addColumn<QString>("result");
        QTest::addColumn<bool>("stamped");
        for (const auto &code : {"COMPLETED", "LEFT_OR_ABANDONED", "CANCELLED_BEFORE_ENTRY",
                                "DISCONNECTED", "INTERRUPTED", "UNKNOWN", "FAILED", ""})
            QTest::newRow(*code ? code : "missing") << QString::fromLatin1(code)
                                                   << (QString::fromLatin1(code) == QStringLiteral("COMPLETED"));
    }
    void scrollMaterialsAndResultBoundaries() {
        QFETCH(QString, result);
        QFETCH(bool, stamped);
        const QString asset = QStringLiteral(":/resources/reflection-share/duty-notes-scroll-v1.4.1/");
        for (const auto &name : {"paper_tile.png", "frame_9slice.png", "header_art.png", "header_rule.png",
                                "end_ornament.png", "side_gem.png", "stamp_complete_1.png",
                                "stamp_complete_2.png", "stamp_complete_3.png", "icon_tile.png", "rail_node.png"})
            QVERIFY2(!QImage(asset + QString::fromLatin1(name)).isNull(), name);
        ShareThemeState theme;
        mr::Formatters formatters;
        mr::JobCatalog jobs;
        mr::ReflectionShareController controller;
        QQmlEngine engine;
        engine.rootContext()->setContextProperty(QStringLiteral("App"), &theme);
        engine.rootContext()->setContextProperty(QStringLiteral("Fmt"), &formatters);
        engine.rootContext()->setContextProperty(QStringLiteral("Jobs"), &jobs);
        engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
        const QVariantMap run{{QStringLiteral("run_id"), QStringLiteral("private-id-not-for-png")},
            {QStringLiteral("duty_name"), QStringLiteral("黑风海底阿尼德罗追忆馆")},
            {QStringLiteral("job_name"), QStringLiteral("白魔法师")}, {QStringLiteral("job_id"), 24},
            {QStringLiteral("result"), result},
            {QStringLiteral("reflection"), QVariantMap{{QStringLiteral("text"), QStringLiteral("心得正文 <b>保留纯文本</b>\n最后一行必须完整可见。")},
                                                       {QStringLiteral("mood"), QStringLiteral("unknown")}}},
            {QStringLiteral("import_metadata"), QVariantMap{{QStringLiteral("source_recorded_at"), QStringLiteral("2026-10-08 18:44:29")}}}};
        engine.rootContext()->setContextProperty(QStringLiteral("ShareRun"), run);
        QList<QQmlError> warnings;
        connect(&engine, &QQmlEngine::warnings, this, [&warnings](const QList<QQmlError> &errors) { warnings.append(errors); });
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import MentorRecorder
Window {
    visible: true; width: 820; height: 650
    ReflectionShareCard { width: 768; runData: ShareRun }
})", QUrl());
        std::unique_ptr<QObject> root(component.create());
        QVERIFY2(root, qPrintable(component.errorString()));
        auto *window = qobject_cast<QQuickWindow *>(root.get());
        QVERIFY(QTest::qWaitForWindowExposed(window));
        auto *card = findVisual(window->contentItem(), QStringLiteral("reflectionShareCard"));
        auto *stamp = findVisual(card, QStringLiteral("reflectionShareResultStamp"));
        auto *brand = findVisual(card, QStringLiteral("reflectionShareBrand"));
        auto *body = findVisual(card, QStringLiteral("reflectionShareFullText"));
        auto *job = findVisual(card, QStringLiteral("reflectionShareJobIcon"));
        QVERIFY(card && stamp && brand && body && job);
        QCOMPARE(stamp->isVisible(), stamped);
        QCOMPARE(stamp->opacity(), 1.0);
        QCOMPARE(stamp->rotation(), 0.0);
        QCOMPARE(job->property("jobId").toInt(), 24);
        auto *gameJob = findVisual(job, QStringLiteral("reflectionShareGameJobImage"));
        QVERIFY(gameJob);
        QCOMPARE(gameJob->property("source").toUrl().toString(), jobs.framedIcon(24));
        QCOMPARE(findVisual(card, QStringLiteral("reflectionShareEyebrow"))->property("text").toString(), QStringLiteral("导随记录"));
        auto *date = findVisual(card, QStringLiteral("reflectionShareExportDate"));
        QVERIFY(date);
        QCOMPARE(date->property("text").toString(), QStringLiteral("导出于 ") + QDate::currentDate().toString(QStringLiteral("yyyy.MM.dd")));
        QCOMPARE(findVisual(card, QStringLiteral("reflectionShareResultText"))->property("text").toString(),
                 QStringLiteral("白魔法师 · ") + formatters.resultLabel(result));
        QVERIFY(findVisual(card, QStringLiteral("reflectionShareTimeText"))->property("text").toString()
                    .contains(QStringLiteral("游玩时间未知")));
        QTemporaryDir output;
        QSignalSpy saved(&controller, &mr::ReflectionShareController::saved);
        const QString withBrand = output.filePath(QStringLiteral("brand.png"));
        // 模拟用户在跨午夜前打开了预览；实际抓图不能沿用过期日期。
        card->setProperty("exportDate", QStringLiteral("2001.01.01"));
        controller.saveTo(card, withBrand);
        QTRY_COMPARE(saved.count(), 1);
        QCOMPARE(date->property("text").toString(), QStringLiteral("导出于 ") + QDate::currentDate().toString(QStringLiteral("yyyy.MM.dd")));
        const QImage first(withBrand);
        QVERIFY(!first.isNull());
        const qreal ratio = window->effectiveDevicePixelRatio();
        for (const QPoint &corner : {QPoint(0, 0), QPoint(first.width() - 1, 0),
                                   QPoint(0, first.height() - 1), QPoint(first.width() - 1, first.height() - 1)})
            QCOMPARE(first.pixelColor(corner).alpha(), 255);
        QCOMPARE(first.pixelColor(first.width() / 2, first.height() / 2).alpha(), 255);
        // 正文留白与底片交界不能因缩放取整出现黑线；此范围远离角饰、正文和署名。
        auto *footer = findVisual(card, QStringLiteral("reflectionShareFooter"));
        QVERIFY(footer);
        const qreal footerY = footer->mapRectToItem(card, footer->boundingRect()).top() * ratio;
        for (int y = qFloor(footerY) - 2; y <= qCeil(footerY) + 2; ++y)
            for (int x = first.width() / 3; x < first.width() * 2 / 3; ++x) {
                const QColor pixel = first.pixelColor(x, y);
                QVERIFY2(pixel.alpha() == 255,
                         "正文与底片拼接处出现黑色接缝");
            }
        QVERIFY(finalTextHasInk(first, body, card, ratio));
        card->setProperty("showAttribution", false);
        QTRY_VERIFY(!brand->isVisible());
        const QString dateBefore = date->property("text").toString();
        const QString withoutBrand = output.filePath(QStringLiteral("no-brand.png"));
        controller.saveTo(card, withoutBrand);
        QTRY_COMPARE(saved.count(), 2);
        const QImage second(withoutBrand);
        QCOMPARE(date->property("text").toString(), dateBefore);
        QCOMPARE(first.size(), second.size());
        const QRectF brandBounds = brand->mapRectToItem(card, brand->boundingRect());
        const QRect brandPixels(qFloor(brandBounds.x() * ratio), qFloor(brandBounds.y() * ratio),
                               qCeil(brandBounds.width() * ratio), qCeil(brandBounds.height() * ratio));
        QVERIFY(first.copy(brandPixels) != second.copy(brandPixels));
        QCOMPARE(first.copy(0, 0, first.width(), brandPixels.top()),
                 second.copy(0, 0, second.width(), brandPixels.top()));
        const QString evidence = qEnvironmentVariable("MR_REFLECTION_SHARE_SCREENSHOT_DIR");
        if (!evidence.isEmpty()) {
            QVERIFY(QDir().mkpath(evidence));
            const QString name = result.isEmpty() ? QStringLiteral("missing") : result;
            QVERIFY(first.save(QDir(evidence).filePath(QStringLiteral("scroll-%1.png").arg(name))));
            if (stamped)
                QVERIFY(second.save(QDir(evidence).filePath(QStringLiteral("scroll-no-brand.png"))));
        }
        QVariantMap longRun = run;
        longRun.insert(QStringLiteral("duty_name"), QStringLiteral("非常长的副本名称需要完整换行").repeated(12));
        longRun.insert(QStringLiteral("job_id"), 99999);
        card->setProperty("runData", longRun);
        QTRY_COMPARE(job->property("jobId").toInt(), 99999);
        QCOMPARE(gameJob->property("source").toUrl().toString(), QString());
        QVERIFY(findVisual(job, QStringLiteral("reflectionShareJobFallback"))->isVisible());
        auto *title = findVisual(card, QStringLiteral("reflectionShareDutyTitle"));
        auto *metadata = findVisual(card, QStringLiteral("reflectionShareResultText"));
        QTRY_VERIFY(title->property("lineCount").toInt() > 2);
        QVERIFY(!title->property("truncated").toBool());
        QVERIFY(metadata->y() >= title->y() + title->height());
        const QRectF bodyBounds = body->mapRectToItem(card, body->boundingRect());
        QVERIFY(bodyBounds.bottom() + 134 * card->property("templateScale").toReal() <= card->height());
        const QString longPath = output.filePath(QStringLiteral("wrapped-title.png"));
        controller.saveTo(card, longPath);
        QTRY_COMPARE(saved.count(), 3);
        QVERIFY(finalTextHasInk(QImage(longPath), body, card, ratio));
        if (stamped && !evidence.isEmpty())
            QVERIFY(QImage(longPath).save(QDir(evidence).filePath(QStringLiteral("scroll-wrapped-title.png"))));
        QCOMPARE(card->property("runData").toMap(), longRun);
        QVERIFY2(warnings.isEmpty(), warnings.isEmpty() ? "" : qPrintable(warnings.first().toString()));
    }
    void scrollExamplesKeepOrderAndUnknownFacts() {
        ShareThemeState theme;
        mr::Formatters formatters;
        mr::JobCatalog jobs;
        mr::ReflectionShareController controller;
        QQmlEngine engine;
        engine.rootContext()->setContextProperty(QStringLiteral("App"), &theme);
        engine.rootContext()->setContextProperty(QStringLiteral("Fmt"), &formatters);
        engine.rootContext()->setContextProperty(QStringLiteral("Jobs"), &jobs);
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import MentorRecorder
Window { visible: true; width: 1180; height: 900
    ReflectionShareCard { width: 1140 }
})", QUrl());
        std::unique_ptr<QObject> root(component.create());
        QVERIFY2(root, qPrintable(component.errorString()));
        auto *window = qobject_cast<QQuickWindow *>(root.get());
        QVERIFY(QTest::qWaitForWindowExposed(window));
        auto *card = findVisual(window->contentItem(), QStringLiteral("reflectionShareCard"));
        QVERIFY(card);
        const QStringList names{QStringLiteral("伊弗利特讨伐战"), QStringLiteral("神兵要塞帝国南方堡"), QStringLiteral("险峻峡谷塞尔法特尔溪谷")};
        const QStringList notes{QStringLiteral("三剑一豆芽，全明星团队亲情奉献！（美美+1）"), QStringLiteral("有本打就好；；"), QStringLiteral("排了好久才排到…我们猪区还有救吗")};
        QVariantList runs;
        for (int i = 0; i < 3; ++i)
            runs.append(QVariantMap{{QStringLiteral("duty_name"), names[i]}, {QStringLiteral("job_id"), 34},
                {QStringLiteral("job_name"), QStringLiteral("武士")},
                {QStringLiteral("result"), QStringLiteral("COMPLETED")},
                {QStringLiteral("matched_at_utc"), QStringLiteral("2026-10-%1T06:40:14Z").arg(12 - i)},
                {QStringLiteral("reflection"), QVariantMap{{QStringLiteral("text"), notes[i]}, {QStringLiteral("mood"), QStringLiteral("good")}}}});
        card->setProperty("runsData", runs);
        QTRY_COMPARE(visibleEntries(card).size(), 3);
        const auto entries = visibleEntries(card);
        QCOMPARE(entries.first()->mapToItem(card, QPointF()).y(), 292.0);
        for (int i = 0; i < entries.size(); ++i) {
            auto *title = findVisual(entries[i], QStringLiteral("reflectionShareDutyTitle"));
            auto *body = findVisual(entries[i], QStringLiteral("reflectionShareFullText"));
            auto *stamp = findVisual(entries[i], QStringLiteral("reflectionShareResultStamp"));
            QVERIFY(title && body && stamp);
            QCOMPARE(title->property("text").toString(), names[i]);
            QCOMPARE(body->property("text").toString(), notes[i]);
            // 用户 HTML 标注的相对基线：35 / 69 / 128，正文行距 48。
            QCOMPARE(title->y() + title->baselineOffset(), 35.0);
            auto *metadata = findVisual(entries[i], QStringLiteral("reflectionShareResultText"));
            QCOMPARE(metadata->y() + metadata->baselineOffset(), 69.0);
            // 系统回退字体的属性排版框可能更高，正文必须允许向下避让。
            QVERIFY(body->y() + body->baselineOffset() >= 128.0);
            auto *mood = findVisual(entries[i], QStringLiteral("reflectionShareMoodTag"));
            QVERIFY(mood);
            const qreal metadataBottom = qMax(metadata->y() + metadata->height(),
                                               mood->isVisible() ? mood->y() + mood->height() : 0.0);
            QCOMPARE(body->y() + body->baselineOffset(), qMax(128.0, metadataBottom + 51.0));
            QVERIFY(body->y() + body->baselineOffset() - 36 >= metadata->y() + metadata->height() + 15);
            QVERIFY(title->x() + title->width() <= stamp->x());
            QVERIFY(body->y() + body->baselineOffset() - body->property("font").value<QFont>().pixelSize() >= stamp->y() + stamp->height());
        }
        const QString evidence = qEnvironmentVariable("MR_REFLECTION_SHARE_SCREENSHOT_DIR");
        QTemporaryDir output;
        QSignalSpy saved(&controller, &mr::ReflectionShareController::saved);
        const QString scroll = output.filePath(QStringLiteral("scroll.png"));
        controller.saveTo(card, scroll);
        QTRY_COMPARE(saved.count(), 1);
        QVERIFY(finalTextHasInk(QImage(scroll), findVisual(entries.last(), QStringLiteral("reflectionShareFullText")), card, window->effectiveDevicePixelRatio()));
        if (!evidence.isEmpty())
            QVERIFY(QImage(scroll).save(QDir(evidence).filePath(QStringLiteral("example-scroll.png"))));
        card->setProperty("runsData", QVariantList{runs.first()});
        QTRY_COMPARE(visibleEntries(card).size(), 1);
        auto *singleEntry = visibleEntries(card).first();
        QCOMPARE(card->height(), 506.0);
        QCOMPARE(singleEntry->property("dividerY").toReal(), 238.0);
        for (const auto &[name, baseline] : {std::pair{"reflectionShareDutyTitle", 156.0},
                                           std::pair{"reflectionShareResultText", 192.0},
                                           std::pair{"reflectionShareFullText", 304.0}}) {
            auto *text = findVisual(singleEntry, QString::fromLatin1(name));
            QVERIFY(text);
            QCOMPARE(text->y() + text->baselineOffset(), baseline);
        }
        const QString single = output.filePath(QStringLiteral("single.png"));
        controller.saveTo(card, single);
        QTRY_COMPARE(saved.count(), 2);
        if (!evidence.isEmpty())
            QVERIFY(QImage(single).save(QDir(evidence).filePath(QStringLiteral("example-single.png"))));
        QVariantMap twoLineRun = runs.first().toMap();
        twoLineRun.insert(QStringLiteral("duty_name"), QStringLiteral("很长的副本名称").repeated(3));
        card->setProperty("runsData", QVariantList{twoLineRun});
        auto *twoLineTitle = findVisual(singleEntry, QStringLiteral("reflectionShareDutyTitle"));
        QTRY_COMPARE(twoLineTitle->property("lineCount").toInt(), 2);
        QCOMPARE(twoLineTitle->y() + twoLineTitle->baselineOffset(), 138.0);
        auto *twoLineMeta = findVisual(singleEntry, QStringLiteral("reflectionShareResultText"));
        QVERIFY(twoLineMeta->y() + twoLineMeta->baselineOffset() >= 210.0);
        QVERIFY(twoLineMeta->y() >= twoLineTitle->y() + twoLineTitle->height());
        QVERIFY(singleEntry->property("dividerY").toReal() >= 238.0);
        const QString twoLine = output.filePath(QStringLiteral("two-lines.png"));
        controller.saveTo(card, twoLine);
        QTRY_COMPARE(saved.count(), 3);
        if (!evidence.isEmpty())
            QVERIFY(QImage(twoLine).save(QDir(evidence).filePath(QStringLiteral("example-single-two-lines.png"))));
        QVariantMap imported = runs.last().toMap();
        imported.remove(QStringLiteral("matched_at_utc"));
        imported.insert(QStringLiteral("result"), QStringLiteral("UNKNOWN"));
        imported.insert(QStringLiteral("import_metadata"), QVariantMap{{QStringLiteral("source_recorded_at"), QStringLiteral("1999-01-01 00:00:00")}});
        runs[2] = imported;
        card->setProperty("runsData", runs);
        QTRY_COMPARE(visibleEntries(card).size(), 3);
        const auto unknownEntries = visibleEntries(card);
        QVERIFY(!findVisual(unknownEntries.last(), QStringLiteral("reflectionShareResultStamp"))->isVisible());
        auto *meta = findVisual(card, QStringLiteral("reflectionShareCollectionMeta"));
        QVERIFY(meta);
        QVERIFY(!meta->property("dateLabel").toString().contains(QStringLiteral("1999")));
        QVERIFY(meta->property("dateLabel").toString().contains(QStringLiteral("1 条游玩时间未知")));
        QVERIFY(findVisual(unknownEntries.last(), QStringLiteral("reflectionShareTimeText"))->property("text").toString().contains(QStringLiteral("1999-01-01 00:00:00 · 游玩时间未知")));
    }
    void partialFailureRetriesOnlyFailedAndCombinedOverflowIsExplicit() {
        QQmlEngine engine;
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
Window {
    width: 600; height: 450; visible: true
    Rectangle { objectName: "first"; width: 300; height: 150; color: "magenta" }
    Rectangle { objectName: "second"; y: 160; width: 300; height: 180; color: "cyan" }
    Rectangle { objectName: "tooLarge"; x: 400; width: 100; height: 20000; color: "white" }
})", QUrl());
        std::unique_ptr<QObject> root(component.create());
        QVERIFY2(root, qPrintable(component.errorString()));
        auto *window = qobject_cast<QQuickWindow *>(root.get());
        QVERIFY(QTest::qWaitForWindowExposed(window));
        auto *first = root->findChild<QQuickItem *>(QStringLiteral("first"));
        auto *second = root->findChild<QQuickItem *>(QStringLiteral("second"));
        auto *tooLarge = root->findChild<QQuickItem *>(QStringLiteral("tooLarge"));
        QVERIFY(first && second && tooLarge);
        second->setVisible(false);
        QTemporaryDir target;
        mr::ReflectionShareController controller;
        QSignalSpy saved(&controller, &mr::ReflectionShareController::saved);
        const auto entry = [](const QString &id, QQuickItem *item) {
            return QVariantMap{{QStringLiteral("run_id"), id}, {QStringLiteral("label"), QStringLiteral("同名")},
                {QStringLiteral("item"), QVariant::fromValue<QObject *>(item)}};
        };
        controller.saveBatchTo({entry(QStringLiteral("a"), first), entry(QStringLiteral("b"), second)}, target.path());
        QTRY_VERIFY(!controller.busy());
        QCOMPARE(saved.count(), 1);
        QCOMPARE(controller.successfulCount(), 1);
        QCOMPARE(controller.failedCount(), 1);
        QCOMPARE(controller.batchResults().at(1).toMap().value(QStringLiteral("run_id")).toString(), QStringLiteral("b"));
        QVERIFY(controller.batchResults().at(1).toMap().value(QStringLiteral("error")).toString().contains(QStringLiteral("预览")));
        const QString firstPath = controller.batchResults().at(0).toMap().value(QStringLiteral("path")).toString();
        const QImage before(firstPath);
        first->setProperty("color", QColor("black"));
        second->setVisible(true);
        controller.retryFailed();
        QTRY_VERIFY(!controller.busy());
        QCOMPARE(saved.count(), 2);
        QCOMPARE(controller.successfulCount(), 2);
        QCOMPARE(controller.failedCount(), 0);
        QCOMPARE(controller.batchResults().at(0).toMap().value(QStringLiteral("path")).toString(), firstPath);
        QCOMPARE(QImage(firstPath), before);
        QCOMPARE(QDir(target.path()).entryList({QStringLiteral("*.png")}, QDir::Files).size(), 2);
        controller.retryFailed();
        QCOMPARE(saved.count(), 2);
        controller.saveTo(tooLarge, target.filePath(QStringLiteral("overflow.png")));
        QTRY_VERIFY(!controller.busy());
        QCOMPARE(saved.count(), 2);
        QVERIFY(controller.feedback().contains(QStringLiteral("逐条 PNG")));
        QVERIFY(!QFileInfo::exists(target.filePath(QStringLiteral("overflow.png"))));
        tooLarge->setWidth(8000);
        tooLarge->setHeight(8000);
        controller.saveTo(tooLarge, target.filePath(QStringLiteral("area-overflow.png")));
        QTRY_VERIFY(!controller.busy());
        QCOMPARE(saved.count(), 2);
        QVERIFY(controller.feedback().contains(QStringLiteral("5000 万像素")));
        QVERIFY(!QFileInfo::exists(target.filePath(QStringLiteral("area-overflow.png"))));
        controller.saveBatchTo({entry(QStringLiteral("a"), first)}, {});
        QCOMPARE(saved.count(), 2);
        QCOMPARE(controller.successfulCount(), 0);
        QVERIFY(controller.feedback().contains(QStringLiteral("取消")));
        controller.saveBatchTo({entry(QStringLiteral("a"), first)}, target.filePath(QStringLiteral("missing")));
        QCOMPARE(saved.count(), 2);
        QVERIFY(controller.feedback().contains(QStringLiteral("不存在")));
    }
    void actualPngContainsFullOffscreenLongText() {
        QQmlEngine engine;
        const QString text = QStringLiteral("长中文心得：这是完整文字，不应该成为首页的两行摘要。\n").repeated(55);
        engine.rootContext()->setContextProperty(QStringLiteral("LongText"), text);
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
Window {
    width: 800; height: 600; visible: true
    Rectangle {
        objectName: "card"; width: 680; height: body.height + 120; color: "white"
        Text { id: body; objectName: "body"; x: 32; y: 32; width: 616;
            text: LongText; textFormat: Text.PlainText; wrapMode: Text.Wrap; font.pixelSize: 20; color: "black" }
        Rectangle { x: 32; y: parent.height - 64; width: 30; height: 30; color: "magenta" }
    }
})", QUrl());
        std::unique_ptr<QObject> root(component.create());
        QVERIFY2(root, qPrintable(component.errorString()));
        auto *window = qobject_cast<QQuickWindow *>(root.get());
        QVERIFY(QTest::qWaitForWindowExposed(window));
        auto *card = root->findChild<QQuickItem *>(QStringLiteral("card"));
        QVERIFY(card);
        QVERIFY(card->height() > window->height());
        QCOMPARE(root->findChild<QObject *>(QStringLiteral("body"))->property("text").toString(), text);
        QTemporaryDir target;
        QVERIFY(target.isValid());
        mr::ReflectionShareController controller;
        QSignalSpy saved(&controller, &mr::ReflectionShareController::saved);
        const QString path = target.filePath(QStringLiteral("long.png"));
        controller.saveTo(card, path);
        QTRY_COMPARE(saved.count(), 1);
        QVERIFY(!controller.busy());
        QImage png(path);
        QVERIFY(!png.isNull());
        const qreal ratio = window->effectiveDevicePixelRatio();
        QCOMPARE(png.size(), QSize(qCeil(card->width()), qCeil(card->height())) * ratio);
        // 此标记在真实窗口视口之外，PNG 包含它才能证明保存了完整卡片。
        QCOMPARE(png.pixelColor(qRound(45 * ratio), png.height() - qRound(50 * ratio)), QColor("magenta"));
        QCOMPARE(controller.savedPath(), path);
        controller.saveTo(card, target.filePath(QStringLiteral("missing/failed.png")));
        QTRY_VERIFY(!controller.busy());
        QCOMPARE(saved.count(), 1);
        QVERIFY(controller.savedPath().isEmpty());
        QVERIFY(controller.feedback().contains(QStringLiteral("无法创建")));
        QCOMPARE(root->findChild<QObject *>(QStringLiteral("body"))->property("text").toString(), text);
    }
    void cancelAndWriteFailureNeverReportSuccess() {
        mr::ReflectionShareController controller;
        QSignalSpy saved(&controller, &mr::ReflectionShareController::saved);
        controller.saveTo(nullptr, {});
        QCOMPARE(saved.count(), 0);
        QVERIFY(controller.feedback().contains(QStringLiteral("取消")));
        QVERIFY(controller.savedPath().isEmpty());
        controller.saveTo(nullptr, QStringLiteral("not-captured.png"));
        QCOMPARE(saved.count(), 0);
        QVERIFY(controller.feedback().contains(QStringLiteral("预览")));
        QTemporaryDir target;
        QImage image(20, 20, QImage::Format_ARGB32);
        image.fill(Qt::white);
        QString error;
        QVERIFY(!mr::ReflectionShareController::writePng(image, target.filePath(QStringLiteral("missing/fail.png")), &error));
        QVERIFY(!error.isEmpty());
        QCOMPARE(saved.count(), 0);
        const QString path = target.filePath(QStringLiteral("valid.png"));
        QVERIFY(mr::ReflectionShareController::writePng(image, path, &error));
        QCOMPARE(QImage(path).size(), image.size());
    }
};

int main(int argc, char **argv) {
#ifdef Q_OS_WIN
    const bool bundledFontsOnly = qEnvironmentVariableIsSet("MR_REFLECTION_SHARE_BUNDLED_FONTS_ONLY");
    // 独立离屏进程从空字体目录启动，验证另一台电脑没有系统 Noto 时的真实内置资源。
    QTemporaryDir isolatedFontDirectory;
    if (bundledFontsOnly) {
        if (!isolatedFontDirectory.isValid())
            return 6;
        qputenv("QT_QPA_FONTDIR", isolatedFontDirectory.path().toUtf8());
    }
#endif
    QGuiApplication app(argc, argv);
    // 与生产 main.cpp 保持相同的字形栅格化方式，度量和 PNG 使用同一组提示信息。
    QQuickWindow::setTextRenderType(QQuickWindow::NativeTextRendering);
#ifdef Q_OS_WIN
    if (bundledFontsOnly && (QFontDatabase::hasFamily(QStringLiteral("Noto Serif SC"))
                             || QFontDatabase::hasFamily(QStringLiteral("Noto Sans SC")))) {
        qCritical("Bundled-font regression must start without system Noto fonts.");
        return 6;
    }
    // 普通模式先加载开发机的系统 Noto，再注册内置版本，覆盖同名字体已安装的情况。
    const QDir windowsDir(qEnvironmentVariable("WINDIR", QStringLiteral("C:/Windows")));
    const QDir userFonts(QStandardPaths::writableLocation(QStandardPaths::GenericDataLocation)
                         + QStringLiteral("/Microsoft/Windows/Fonts"));
    int preloadedNotoCount = 0;
    if (!bundledFontsOnly)
        for (const QDir &directory : {QDir(windowsDir.filePath(QStringLiteral("Fonts"))), userFonts})
            for (const QString &file : directory.entryList({QStringLiteral("Noto*SC*.ttf")}, QDir::Files))
                if (QFontDatabase::addApplicationFont(directory.filePath(file)) >= 0)
                    ++preloadedNotoCount;
    qInfo() << "Preloaded system Noto files:" << preloadedNotoCount;
#endif
    if (!mr::registerBundledFonts())
        return 6;
    QFont font = QGuiApplication::font();
    font.setFamilies({QStringLiteral("Noto Sans SC")});
    QGuiApplication::setFont(font);
    QStandardPaths::setTestModeEnabled(true);
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    ReflectionShareTests tests;
    return QTest::qExec(&tests, argc, argv);
}
#include "ReflectionShareTests.moc"
