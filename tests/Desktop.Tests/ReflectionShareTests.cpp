#include "ReflectionShareController.h"
#include "Formatters.h"
#include "JobCatalog.h"

#include <QDir>
#include <QFileInfo>
#include <QGuiApplication>
#include <QImage>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickWindow>
#include <QQuickStyle>
#include <QSignalSpy>
#include <QTemporaryDir>
#include <QTest>
#include <QtMath>

#include <memory>

namespace {
QQuickItem *findVisual(QQuickItem *at, const QString &name)
{
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

bool finalTextHasInk(const QImage &png, QQuickItem *body, QQuickItem *capture, qreal ratio)
{
    const QPointF bodyPoint = body->mapToItem(capture, QPointF());
    const QColor background = capture->property("color").value<QColor>();
    const int endY = qMin(png.height(), int((bodyPoint.y() + body->height()) * ratio));
    int glyphPixels = 0;
    for (int y = qMax(0, endY - qRound(40 * ratio)); y < endY; ++y)
        for (int x = int(bodyPoint.x() * ratio); x < qMin(png.width(), int((bodyPoint.x() + body->width()) * ratio)); ++x) {
            const QColor pixel = png.pixelColor(x, y);
            if (qAbs(pixel.red() - background.red()) + qAbs(pixel.green() - background.green())
                + qAbs(pixel.blue() - background.blue()) > 40)
                ++glyphPixels;
        }
    return glyphPixels > 20;
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
private Q_SLOTS:
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
        auto *card = findVisual(window->contentItem(), QStringLiteral("reflectionShareCard"));
        auto *body = findVisual(window->contentItem(), QStringLiteral("reflectionShareFullText"));
        QVERIFY(card && body);
        QTRY_VERIFY(card->height() > size.height());
        QCOMPARE(body->property("text").toString(), fullText);
        QVERIFY(body->y() + body->height() <= card->height());
        QString rendered;
        const auto collectText = [&rendered](auto &&self, QQuickItem *item) -> void {
            rendered += item->property("text").toString() + QLatin1Char('\n');
            for (auto *child : item->childItems()) self(self, child);
        };
        collectText(collectText, card);
        QVERIFY(rendered.contains(QStringLiteral("未记录心情")));
        QVERIFY(rendered.contains(QStringLiteral("原站记录时间：2026-10-08 18:44:29")));
        QVERIFY(!rendered.contains(QStringLiteral("internal-marker")));
        QTemporaryDir target;
        const QString path = target.filePath(QStringLiteral("actual-dialog.png"));
        QSignalSpy saved(&controller, &mr::ReflectionShareController::saved);
        controller.saveTo(card, path);
        QTRY_COMPARE(saved.count(), 1);
        const QImage png(path);
        const qreal ratio = window->effectiveDevicePixelRatio();
        QCOMPARE(png.size(), QSize(qCeil(card->width()), qCeil(card->height())) * ratio);
        // ScrollView 的视口优化也不能省略图片底部的文字节点。
        const QPointF bodyPoint = body->mapToItem(card, QPointF());
        const QColor background = card->property("color").value<QColor>();
        const int endY = qMin(png.height(), int((bodyPoint.y() + body->height()) * ratio));
        int glyphPixels = 0;
        for (int y = qMax(0, endY - qRound(40 * ratio)); y < endY; ++y)
            for (int x = int(bodyPoint.x() * ratio); x < qMin(png.width(), int((bodyPoint.x() + body->width()) * ratio)); ++x) {
                const QColor pixel = png.pixelColor(x, y);
                if (qAbs(pixel.red() - background.red()) + qAbs(pixel.green() - background.green())
                    + qAbs(pixel.blue() - background.blue()) > 40) ++glyphPixels;
            }
        QVERIFY2(glyphPixels > 20, "PNG 最后一段文字缺失，可能被预览视口裁切");
        QCOMPARE(dialog->property("runData").toMap(), run);
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
        QTRY_COMPARE(completeCards(combined).size(), 2);
        const auto cards = completeCards(combined);
        auto *firstBody = findVisual(cards.at(0), QStringLiteral("reflectionShareFullText"));
        auto *secondBody = findVisual(cards.at(1), QStringLiteral("reflectionShareFullText"));
        QVERIFY(firstBody && secondBody);
        QTRY_VERIFY(combined->height() > size.height());
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
        controller.saveBatchTo(entries, target.path());
        QTRY_VERIFY_WITH_TIMEOUT(!controller.busy(), 20000);
        QCOMPARE(saved.count(), 2);
        QCOMPARE(controller.successfulCount(), 2);
        QCOMPARE(controller.failedCount(), 0);
        QCOMPARE(controller.batchResults().size(), 2);
        QStringList paths;
        for (int index = 0; index < cards.size(); ++index) {
            const QString path = controller.batchResults().at(index).toMap().value(QStringLiteral("path")).toString();
            paths.append(path);
            const QImage png(path);
            QVERIFY(!png.isNull());
            QCOMPARE(png.size(), QSize(qCeil(cards.at(index)->width()), qCeil(cards.at(index)->height())) * ratio);
            QVERIFY(finalTextHasInk(png, index == 0 ? firstBody : secondBody, cards.at(index), ratio));
            if (!evidence.isEmpty())
                QVERIFY(png.save(QDir(evidence).filePath(QStringLiteral("individual-%1-%2.png").arg(index + 1).arg(themeName))));
        }
        QVERIFY(paths[0] != paths[1]);
        QVERIFY(QFileInfo(paths[0]).fileName().startsWith(QStringLiteral("同名_副本-")));
        if (!evidence.isEmpty()) {
            QTest::qWait(80);
            QVERIFY(window->grabWindow().save(QDir(evidence).filePath(QStringLiteral("batch-results-%1.png").arg(themeName))));
        }
        const QString combinedPath = target.filePath(QStringLiteral("combined.png"));
        controller.saveTo(combined, combinedPath);
        QTRY_VERIFY_WITH_TIMEOUT(!controller.busy(), 20000);
        QCOMPARE(saved.count(), 3);
        const QImage png(combinedPath);
        QCOMPARE(png.size(), QSize(qCeil(combined->width()), qCeil(combined->height())) * ratio);
        QVERIFY(finalTextHasInk(png, firstBody, combined, ratio));
        QVERIFY(finalTextHasInk(png, secondBody, combined, ratio));
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
    QGuiApplication app(argc, argv);
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    ReflectionShareTests tests;
    return QTest::qExec(&tests, argc, argv);
}
#include "ReflectionShareTests.moc"
