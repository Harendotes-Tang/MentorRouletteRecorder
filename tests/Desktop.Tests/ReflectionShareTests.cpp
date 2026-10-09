#include "ReflectionShareController.h"
#include "Formatters.h"
#include "JobCatalog.h"

#include <QDir>
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
        auto *card = dialog->findChild<QQuickItem *>(QStringLiteral("reflectionShareCard"));
        auto *body = dialog->findChild<QQuickItem *>(QStringLiteral("reflectionShareFullText"));
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
