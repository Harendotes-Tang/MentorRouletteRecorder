#include "DesktopTestRunner.h"
#include "BundledFonts.h"
#include "Formatters.h"
#include "JobCatalog.h"
#include "QmlComponentTestSupport.h"
#include "ReflectionLibraryController.h"
#include "ReflectionShareController.h"

#include <QDir>
#include <QFileInfo>
#include <QGuiApplication>
#include <QQmlContext>
#include <QQmlListReference>
#include <QQuickItem>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QTemporaryDir>

#include <algorithm>

namespace {

using mrtest::findVisualItem;

QStringList runIds(QObject *dialog)
{
    const auto runs = mrtest::qmlList(dialog->property("runsData"));
    QStringList ids;
    for (const auto &run : runs)
        ids.append(run.toMap().value(QStringLiteral("run_id")).toString());
    return ids;
}

QPoint centerInWindow(QQuickItem *item)
{
    return item->mapToScene(item->boundingRect().center()).toPoint();
}

QRectF sceneRect(const QQuickItem *item)
{
    return item->mapRectToScene(item->boundingRect());
}

QRect framePixels(const QRectF &rect, const QImage &image, const QQuickWindow *window)
{
    const qreal scale = static_cast<qreal>(image.width()) / window->width();
    return QRectF(rect.topLeft() * scale, rect.size() * scale).toAlignedRect().intersected(image.rect());
}

int changedPixels(const QImage &visible, const QImage &hidden, const QRect &region)
{
    int changed = 0;
    for (int y = region.top(); y <= region.bottom(); ++y)
        for (int x = region.left(); x <= region.right(); ++x) {
            const auto a = visible.pixelColor(x, y);
            const auto b = hidden.pixelColor(x, y);
            if (qAbs(a.red() - b.red()) + qAbs(a.green() - b.green()) + qAbs(a.blue() - b.blue()) > 12)
                ++changed;
        }
    return changed;
}

int textInk(const QImage &image, const QRect &region)
{
    int ink = 0;
    for (int y = region.top(); y <= region.bottom(); ++y)
        for (int x = region.left(); x <= region.right(); ++x) {
            const auto pixel = image.pixelColor(x, y);
            if (pixel.red() < 110 && pixel.green() < 100 && pixel.blue() < 95)
                ++ink;
        }
    return ink;
}

/// The share dialog alone in an 820x940 window, where its rows are reordered.
constexpr auto kOrderDialogQml = R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    visible: true; width: 820; height: 940
    ReflectionShareDialog { objectName: "dialog"; anchors.centerIn: Overlay.overlay }
})";

/// The objects the share dialog reads from its context, and the scene it is created in.
struct ShareScene
{
    mr::Formatters formatters;
    mr::JobCatalog jobs;
    mr::ReflectionLibraryController library{nullptr};
    mr::ReflectionShareController controller;
    mrtest::QmlComponentScene scene;

    ShareScene()
    {
        auto *context = scene.engine.rootContext();
        context->setContextProperty(QStringLiteral("Fmt"), &formatters);
        context->setContextProperty(QStringLiteral("Jobs"), &jobs);
        context->setContextProperty(QStringLiteral("Reflections"), &library);
        context->setContextProperty(QStringLiteral("ReflectionShare"), &controller);
        context->setContextProperty(QStringLiteral("ReduceMotion"), true);
    }

    static QVariantList runs()
    {
        QVariantList runs;
        for (int i = 1; i <= 3; ++i) {
            runs.append(QVariantMap{{QStringLiteral("run_id"), QString::number(i)},
                {QStringLiteral("duty_name"), QStringLiteral("副本 %1").arg(i)},
                {QStringLiteral("matched_at_utc"), QStringLiteral("2026-11-0%1T08:00:00Z").arg(i)},
                {QStringLiteral("reflection"), QVariantMap{{QStringLiteral("text"), QStringLiteral("心得 %1").arg(i)}}}});
        }
        return runs;
    }
};

class ReflectionShareOrderTests final : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void initTestCase()
    {
        QQuickStyle::setStyle(QStringLiteral("Basic"));
        QVERIFY(mr::registerBundledFonts());
        mrtest::registerSourceQmlComponents();
        qmlRegisterType(QUrl::fromLocalFile(mrtest::qmlSourceRoot() + QStringLiteral("/dialogs/ReflectionShareDialog.qml")),
                        "MentorRecorder", 1, 0, "ReflectionShareDialog");
        qmlRegisterType(QUrl::fromLocalFile(mrtest::qmlSourceRoot() + QStringLiteral("/dialogs/ReflectionShareWindow.qml")),
                        "MentorRecorder", 1, 0, "ReflectionShareWindow");
    }

    void theWindowOfItsOwnKeepsCardsNarrowAndMovesARowToEitherEndInOneClick()
    {
        // Owner, 2026-11-19: 生成分享图片 is a window that can be maximised, and a
        // row gets to the far end of a long list without being dragged there.
        ShareScene share;
        auto &scene = share.scene;
        QVERIFY2(scene.create(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    visible: true; width: 420; height: 320
    ReflectionShareWindow { objectName: "shareWindow" }
})", QStringLiteral("/ReflectionShareWindowTest.qml")), qPrintable(scene.errors));
        QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
        auto *host = scene.root->findChild<QQuickWindow *>(QStringLiteral("shareWindow"));
        QVERIFY(host && !host->isVisible());
        QVERIFY(host->flags().testFlag(Qt::WindowMaximizeButtonHint));
        // A minimised window renders nothing, so a batch saved meanwhile would fail (audit 2026-11-20 L2-2).
        QVERIFY(!host->flags().testFlag(Qt::WindowMinimizeButtonHint));
        auto *dialog = host->property("dialog").value<QObject *>();
        QVERIFY(dialog);
        QVERIFY(QMetaObject::invokeMethod(host, "openForRuns", Q_ARG(QVariant, QVariant(ShareScene::runs()))));
        QTRY_VERIFY(host->isVisible() && dialog->property("opened").toBool());
        QVERIFY(QTest::qWaitForWindowExposed(host));

        host->resize(1500, 900);
        auto *stack = findVisualItem(host->contentItem(), QStringLiteral("reflectionShareIndividualCards"));
        auto *preview = findVisualItem(host->contentItem(), QStringLiteral("reflectionSharePreview"));
        QVERIFY(stack && preview);
        // The dialog fills the window; the cards stop at their limit and sit in the middle.
        QTRY_COMPARE(dialog->property("width").toReal(), 1500.0);
        QTRY_COMPARE(stack->width(), dialog->property("cardLimit").toReal());
        QVERIFY(preview->width() > 1400);
        QVERIFY(qAbs(stack->mapToItem(preview, QPointF(stack->width() / 2, 0)).x() - preview->width() / 2) < 12);
        QVERIFY(!findVisualItem(host->contentItem(), QStringLiteral("reflectionShareCloseButton"))->isVisible());

        const QStringList newest{QStringLiteral("3"), QStringLiteral("2"), QStringLiteral("1")};
        QCOMPARE(runIds(dialog), newest);
        const auto tap = [host](const QString &name) {
            auto *button = findVisualItem(host->contentItem(), name);
            if (!button || !button->isVisible())
                return false;
            QTest::mouseClick(host, Qt::LeftButton, Qt::NoModifier, centerInWindow(button));
            return true;
        };
        // The first row has nowhere further up to go, the last nowhere further down.
        QTRY_VERIFY(findVisualItem(host->contentItem(), QStringLiteral("reflectionShareMoveLast_0")));
        QVERIFY(!findVisualItem(host->contentItem(), QStringLiteral("reflectionShareMoveFirst_0"))->isVisible());
        QVERIFY(!findVisualItem(host->contentItem(), QStringLiteral("reflectionShareMoveLast_2"))->isVisible());
        QVERIFY(tap(QStringLiteral("reflectionShareMoveLast_0")));
        QTRY_COMPARE(runIds(dialog), (QStringList{QStringLiteral("2"), QStringLiteral("1"), QStringLiteral("3")}));
        QTRY_VERIFY(findVisualItem(host->contentItem(), QStringLiteral("reflectionShareMoveFirst_2")));
        // The rows are rebuilt in the new order, and the last one lies below the window: bring it in.
        QTest::qWait(200);
        auto *flick = qvariant_cast<QObject *>(preview->property("contentItem"));
        QVERIFY(flick);
        flick->setProperty("contentY", flick->property("contentHeight").toReal() - preview->height());
        QTest::qWait(100);
        QVERIFY(tap(QStringLiteral("reflectionShareMoveFirst_2")));
        QTRY_COMPARE(runIds(dialog), newest);
        QCOMPARE(findVisualItem(host->contentItem(), QStringLiteral("reflectionImageOrder"))->property("currentIndex").toInt(), 2);

        const QString shot = qEnvironmentVariable("MR_SHARE_WINDOW_SHOT");
        if (!shot.isEmpty())
            QVERIFY(host->grabWindow().save(shot));

        // The title bar's close button closes the dialog with the window.
        host->close();
        QTRY_VERIFY(!host->isVisible() && !dialog->property("visible").toBool());
        QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
    }

    void dragOrderFollowsBothPreviewsAndPngFiles()
    {
        ShareScene share;
        auto &controller = share.controller;
        auto &scene = share.scene;
        QVERIFY2(scene.create(kOrderDialogQml, QStringLiteral("/ReflectionShareOrderTest.qml")), qPrintable(scene.errors));

        auto *window = scene.window();
        QVERIFY(QTest::qWaitForWindowExposed(window));
        auto *dialog = scene.root->findChild<QObject *>(QStringLiteral("dialog"));
        QVERIFY(dialog);
        const QVariantList runs = ShareScene::runs();
        const auto open = [dialog, &runs] {
            return QMetaObject::invokeMethod(dialog, "openForRuns", Q_ARG(QVariant, QVariant(runs)));
        };
        const auto visual = [window](const QString &name) { return findVisualItem(window->contentItem(), name); };
        const auto grip = [visual](int i) { return visual(QStringLiteral("reflectionShareDragGrip_%1").arg(i)); };
        QVERIFY(open());
        QTRY_VERIFY(dialog->property("opened").toBool());
        auto *order = visual(QStringLiteral("reflectionImageOrder"));
        auto *mode = visual(QStringLiteral("reflectionImageOutputMode"));
        auto *preview = visual(QStringLiteral("reflectionSharePreview"));
        auto *stack = visual(QStringLiteral("reflectionShareIndividualCards"));
        auto *combined = visual(QStringLiteral("reflectionShareCombinedCard"));
        QVERIFY(order && mode && preview && stack && combined);
        auto *flick = qvariant_cast<QObject *>(preview->property("contentItem"));
        QVERIFY(flick);
        const QStringList newest{QStringLiteral("3"), QStringLiteral("2"), QStringLiteral("1")};
        const QStringList oldest{QStringLiteral("1"), QStringLiteral("2"), QStringLiteral("3")};
        const QStringList manual{QStringLiteral("2"), QStringLiteral("1"), QStringLiteral("3")};
        QCOMPARE(runIds(dialog), newest);
        QTRY_VERIFY(grip(0) && grip(1) && grip(2) && grip(1)->y() > grip(0)->y() && grip(2)->y() > grip(1)->y());

        const QPoint start = centerInWindow(grip(0));
        const QPoint edge = preview->mapToScene(QPointF(preview->width() - 24, preview->height() - 2)).toPoint();
        QTest::mousePress(window, Qt::LeftButton, Qt::NoModifier, start);
        QTest::mouseMove(window, start + QPoint(0, 30));
        QTest::mouseMove(window, edge);
        QTRY_COMPARE(dialog->property("dragFrom").toInt(), 0);
        QTRY_VERIFY(flick->property("contentY").toReal() > 0);
        QTRY_COMPARE(dialog->property("dropIndex").toInt(), 2);
        QTest::mouseRelease(window, Qt::LeftButton, Qt::NoModifier, edge);
        QTRY_COMPARE(runIds(dialog), manual);
        QCOMPARE(order->property("currentIndex").toInt(), 2);
        QVERIFY(!findVisualItem(stack, QStringLiteral("reflectionShareDragGrip_0")));

        QVariant captured;
        QVERIFY(QMetaObject::invokeMethod(dialog, "captureEntries", Q_RETURN_ARG(QVariant, captured)));
        const QVariantList entries = captured.toList();
        QCOMPARE(entries.size(), manual.size());
        QTemporaryDir target;
        QVERIFY(target.isValid());
        controller.saveBatchTo(entries, target.path(), true);
        QTRY_VERIFY_WITH_TIMEOUT(!controller.busy(), 20000);
        QCOMPARE(controller.successfulCount(), 3);
        for (qsizetype i = 0; i < manual.size(); ++i) {
            const auto result = controller.batchResults().at(i).toMap();
            QCOMPARE(result.value(QStringLiteral("run_id")).toString(), manual[i]);
            const QString path = result.value(QStringLiteral("path")).toString();
            QVERIFY(QFileInfo(path).fileName().startsWith(QStringLiteral("0%1_").arg(i + 1)));
            QVERIFY(!QImage(path).isNull());
        }

        mode->setProperty("currentIndex", 1);
        flick->setProperty("contentY", 0);
        QTRY_VERIFY(combined->isVisible() && grip(0) && grip(1) && grip(1)->y() > grip(0)->y());
        const QPoint second = centerInWindow(grip(1));
        const QPoint first = centerInWindow(grip(0));
        QTest::mousePress(window, Qt::LeftButton, Qt::NoModifier, second);
        QTest::mouseMove(window, second - QPoint(0, 30));
        QTest::mouseMove(window, first);
        QTRY_COMPARE(dialog->property("dropIndex").toInt(), 0);
        QTest::mouseRelease(window, Qt::LeftButton, Qt::NoModifier, first);
        QTRY_COMPARE(runIds(dialog), oldest);
        QVERIFY(!findVisualItem(combined, QStringLiteral("reflectionShareDragGrip_0")));
        const QQmlListReference timeline(combined, "collectionEntries");
        QCOMPARE(timeline.count(), 3);
        auto *firstEntry = qobject_cast<QQuickItem *>(timeline.at(0));
        QVERIFY(firstEntry);
        auto *firstNote = findVisualItem(firstEntry, QStringLiteral("reflectionShareFullText"));
        QVERIFY(firstNote);
        QCOMPARE(firstNote->property("text").toString(), QStringLiteral("心得 1"));
        controller.saveTo(combined, target.filePath(QStringLiteral("combined.png")));
        QTRY_VERIFY_WITH_TIMEOUT(!controller.busy(), 20000);
        QVERIFY(!QImage(target.filePath(QStringLiteral("combined.png"))).isNull());

        QTRY_VERIFY(grip(0));
        grip(0)->forceActiveFocus();
        QTest::keyClick(window, Qt::Key_Down);
        QTRY_COMPARE(runIds(dialog), manual);
        QTRY_VERIFY(grip(1) && grip(1)->hasActiveFocus());
        order->setProperty("currentIndex", 1);
        QVERIFY(QMetaObject::invokeMethod(order, "activated", Q_ARG(int, 1)));
        QTRY_COMPARE(runIds(dialog), oldest);
        order->setProperty("currentIndex", 2);
        QVERIFY(QMetaObject::invokeMethod(order, "activated", Q_ARG(int, 2)));
        QTRY_COMPARE(runIds(dialog), manual);
        QTest::keyClick(window, Qt::Key_Escape);
        QTRY_VERIFY(!dialog->property("visible").toBool());
        QVERIFY(open());
        QCOMPARE(runIds(dialog), newest);
    }

    // Minimised while saving (audit 2026-11-20 L2-2): no frame comes until the window is
    // restored, and that is no reason to give up on the image (audit 2026-11-26, L2-1).
    void aSaveWaitsWhileTheWindowIsNotExposed()
    {
        ShareScene share;
        auto &controller = share.controller;
        auto &scene = share.scene;
        QVERIFY2(scene.create(kOrderDialogQml, QStringLiteral("/ReflectionShareExposeTest.qml")), qPrintable(scene.errors));
        auto *window = scene.window();
        QVERIFY(QTest::qWaitForWindowExposed(window));
        auto *dialog = scene.root->findChild<QObject *>(QStringLiteral("dialog"));
        QVERIFY(dialog);
        QVERIFY(QMetaObject::invokeMethod(dialog, "openForRuns", Q_ARG(QVariant, QVariant(ShareScene::runs()))));
        QTRY_VERIFY(dialog->property("opened").toBool());
        auto *mode = findVisualItem(window->contentItem(), QStringLiteral("reflectionImageOutputMode"));
        auto *combined = findVisualItem(window->contentItem(), QStringLiteral("reflectionShareCombinedCard"));
        QVERIFY(mode && combined);
        mode->setProperty("currentIndex", 1);
        QTRY_VERIFY(combined->isVisible());

        window->showMinimized();
        QTest::qWait(300);
        if (window->isExposed() || !window->isVisible()) {
            window->showNormal();
            QSKIP("This platform plugin does not report a minimised window as unexposed.");
        }
        constexpr int kWaitMs = 100;
        controller.setCaptureTimeoutForTesting(kWaitMs);
        QTemporaryDir target;
        QVERIFY(target.isValid());
        const QString path = target.filePath(QStringLiteral("combined.png"));
        controller.saveTo(combined, path);
        QVERIFY(controller.busy());
        // Several waits go by: still saving, nothing failed.
        QTest::qWait(kWaitMs * 6);
        QVERIFY(controller.busy());
        QVERIFY(!QFileInfo::exists(path));
        // Back to the real wait before restoring, so the frame and the grab that follow
        // are not raced by the short one (one more short wait re-arms it).
        controller.setCaptureTimeoutForTesting(15000);
        QTest::qWait(kWaitMs * 3);
        QVERIFY(controller.busy());

        window->showNormal();
        QVERIFY(QTest::qWaitForWindowExposed(window));
        QTRY_VERIFY_WITH_TIMEOUT(!controller.busy(), 20000);
        QCOMPARE(controller.savedPath(), QFileInfo(path).absoluteFilePath());
        QVERIFY(!QImage(path).isNull());
    }


    void combinedDragCoversTheRowBelowAndKeepsItsLastLine_data()
    {
        QTest::addColumn<int>("from");
        QTest::newRow("short-down") << 0;
        QTest::newRow("long-up") << 1;
    }

    void combinedDragCoversTheRowBelowAndKeepsItsLastLine()
    {
        QFETCH(int, from);
        ShareScene share;
        auto &scene = share.scene;
        QVERIFY2(scene.create(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    visible: true; width: 920; height: 1040
    ReflectionShareDialog { objectName: "dialog"; windowed: true; anchors.centerIn: Overlay.overlay }
})", QStringLiteral("/ReflectionShareDragPixels.qml")), qPrintable(scene.errors));
        auto *window = scene.window();
        QVERIFY(QTest::qWaitForWindowExposed(window));
        auto *dialog = scene.root->findChild<QObject *>(QStringLiteral("dialog"));
        QVERIFY(dialog);

        const QString shortNote = QStringLiteral("Palette practice\nShort reflection line two\nShort reflection last line");
        const QString longNote = QStringLiteral("A longer reflection begins\n"
            "Second reflection line\nThird reflection line\nFourth reflection line\nFifth reflection line\n"
            "Sixth reflection line\nSeventh reflection line\nLong reflection last line");
        auto runs = ShareScene::runs();
        for (qsizetype i : {qsizetype(1), qsizetype(2)}) {
            auto run = runs[i].toMap();
            run[QStringLiteral("duty_name")] = i == 1 ? QStringLiteral("The Hidden Citadel") : QStringLiteral("The Amber Keep");
            run[QStringLiteral("reflection")] = QVariantMap{{QStringLiteral("text"), i == 1 ? longNote : shortNote}};
            runs[i] = run;
        }
        QVERIFY(QMetaObject::invokeMethod(dialog, "openForRuns", Q_ARG(QVariant, QVariant(runs))));
        QTRY_VERIFY(dialog->property("opened").toBool());
        const auto visible = [window](const QString &name) { return findVisualItem(window->contentItem(), name); };
        auto *mode = visible(QStringLiteral("reflectionImageOutputMode"));
        auto *preview = visible(QStringLiteral("reflectionSharePreview"));
        auto *combined = visible(QStringLiteral("reflectionShareCombinedCard"));
        auto *ghost = visible(QStringLiteral("reflectionShareDragGhost"));
        QVERIFY(mode && preview && combined && ghost);
        auto *flick = qvariant_cast<QObject *>(preview->property("contentItem"));
        QVERIFY(flick);
        mode->setProperty("currentIndex", 1);
        const QQmlListReference entries(combined, "collectionEntries");
        QTRY_COMPARE(entries.count(), 3);
        const int to = 1 - from;
        auto *source = qobject_cast<QQuickItem *>(entries.at(from));
        auto *target = qobject_cast<QQuickItem *>(entries.at(to));
        auto *first = qobject_cast<QQuickItem *>(entries.at(0));
        QVERIFY(source && target && first);
        auto *sourceText = findVisualItem(source, QStringLiteral("reflectionShareFullText"));
        auto *targetTitle = findVisualItem(target, QStringLiteral("reflectionShareDutyTitle"));
        auto *targetText = findVisualItem(target, QStringLiteral("reflectionShareFullText"));
        QVERIFY(sourceText && targetTitle && targetText);
        const auto date = target->property("playedDate").toString().mid(5).replace(QLatin1Char('-'), QLatin1Char('.'));
        const auto children = target->childItems();
        const auto dateItem = std::find_if(children.cbegin(), children.cend(), [&date](const QQuickItem *item) {
            return item->property("text").toString() == date;
        });
        QVERIFY(dateItem != children.cend());
        auto *targetDate = *dateItem;
        QCOMPARE(sourceText->property("lineCount").toInt(), from == 0 ? 3 : 8);

        // Keep both grips and the full lifted copy inside the viewport.
        flick->setProperty("contentY", qMax(0.0, sceneRect(first).top() - sceneRect(preview).top() - 100.0));
        const auto gripName = QStringLiteral("reflectionShareDragGrip_%1").arg(from);
        QTRY_VERIFY(visible(gripName));
        QTest::qWait(80);
        const QString evidence = qEnvironmentVariable("MR_SHARE_DRAG_EVIDENCE_DIR");
        const auto save = [&evidence](const QImage &image, const QString &step) {
            if (evidence.isEmpty())
                return true;
            return image.save(QDir(evidence).filePath(QString::fromLatin1(QTest::currentDataTag())
                + QLatin1Char('-') + step + QStringLiteral(".png")));
        };
        const auto before = window->grabWindow();
        QVERIFY(!before.isNull() && save(before, QStringLiteral("before")));

        const QPoint start = centerInWindow(visible(gripName));
        // Pick up the handle with a generous lower grab offset so the copy can
        // cover the neighbour's date, title and first body lines before it slides.
        const QPoint liftPoint = start + QPoint(0, 100);
        QVERIFY(sceneRect(preview).contains(liftPoint));
        QTest::mousePress(window, Qt::LeftButton, Qt::NoModifier, start);
        QTest::mouseMove(window, liftPoint);
        QTRY_COMPARE(dialog->property("dragFrom").toInt(), from);
        QTRY_VERIFY(ghost->isVisible() && ghost->height() > 0);
        QTest::qWait(40);
        const qreal pointerToTop = liftPoint.y() - sceneRect(ghost).top();
        const QPoint overlap(start.x(), qRound(sceneRect(target).top() + pointerToTop - 8));
        QVERIFY(sceneRect(preview).contains(overlap));
        QTest::mouseMove(window, overlap);
        QTRY_COMPARE(dialog->property("dropIndex").toInt(), from);
        QTest::qWait(60);
        const auto carried = sceneRect(ghost);
        const auto inner = carried.adjusted(8, 8, -8, -8).intersected(sceneRect(preview));
        const auto during = window->grabWindow();
        QVERIFY(!during.isNull() && save(during, QStringLiteral("during")));

        // The lower row stays laid out. Only its paint changes so the two grabs
        // compare the same pixels in the copy, including its transparent areas.
        const qreal originalOpacity = target->opacity();
        target->setOpacity(0);
        QTest::qWait(50);
        const auto withoutNeighbour = window->grabWindow();
        target->setOpacity(originalOpacity);
        QVERIFY(!withoutNeighbour.isNull() && save(withoutNeighbour, QStringLiteral("without-neighbour")));
        const QRect datePixels = framePixels(inner.intersected(sceneRect(targetDate)), during, window);
        const QRect titlePixels = framePixels(inner.intersected(sceneRect(targetTitle)), during, window);
        const QRect bodyPixels = framePixels(inner.intersected(sceneRect(targetText)), during, window);
        QVERIFY(datePixels.height() > 8 && titlePixels.height() > 10 && bodyPixels.height() > 12);

        const qreal line = sourceText->property("lineHeight").toReal();
        const QRectF tailInSource = sourceText->mapRectToItem(source,
            QRectF(0, sourceText->height() - line, sourceText->width(), line));
        const qreal xScale = ghost->width() / source->width();
        const qreal yScale = ghost->height() / source->height();
        const QRectF tail = ghost->mapRectToScene(QRectF(tailInSource.x() * xScale, tailInSource.y() * yScale,
            tailInSource.width() * xScale, tailInSource.height() * yScale));
        QVERIFY(sceneRect(preview).contains(tail.adjusted(2, 2, -2, -2)));
        const int tailInk = textInk(withoutNeighbour, framePixels(tail, withoutNeighbour, window));

        const qreal dropY = sceneRect(target).center().y() + (from < to ? 12 : -12);
        const QPoint drop(start.x(), qRound(dropY));
        QVERIFY(sceneRect(preview).contains(drop));
        QTest::mouseMove(window, drop);
        QTRY_COMPARE(dialog->property("dropIndex").toInt(), to);
        QTest::mouseRelease(window, Qt::LeftButton, Qt::NoModifier, drop);
        QTRY_COMPARE(runIds(dialog), (QStringList{QStringLiteral("2"), QStringLiteral("3"), QStringLiteral("1")}));
        QTRY_VERIFY(!ghost->isVisible());
        auto *moved = qobject_cast<QQuickItem *>(entries.at(to));
        QVERIFY(moved);
        auto *movedNote = findVisualItem(moved, QStringLiteral("reflectionShareFullText"));
        QVERIFY(movedNote);
        QCOMPARE(movedNote->property("text").toString(), from == 0 ? shortNote : longNote);
        const auto after = window->grabWindow();
        QVERIFY(!after.isNull() && save(after, QStringLiteral("after")));

        const int dateBleed = changedPixels(during, withoutNeighbour, datePixels);
        const int titleBleed = changedPixels(during, withoutNeighbour, titlePixels);
        const int bodyBleed = changedPixels(during, withoutNeighbour, bodyPixels);
        QVERIFY2(dateBleed == 0 && titleBleed == 0 && bodyBleed == 0,
                 qPrintable(QStringLiteral("lower row visible through copy: date %1, title %2, body %3 pixels")
                     .arg(dateBleed).arg(titleBleed).arg(bodyBleed)));
        QVERIFY2(tailInk > 24, qPrintable(QStringLiteral("lifted final line has only %1 dark pixels").arg(tailInk)));
    }

    // Audit 2026-11-20 L2-1: ↑/↓ on a focused grip during another grip's mouse drag rebuilt
    // the rows under the drag's own row indexes. The order waits for the drag to end.
    void aKeyMoveDuringAMouseDragLeavesTheOrderAlone()
    {
        ShareScene share;
        auto &scene = share.scene;
        QVERIFY2(scene.create(kOrderDialogQml, QStringLiteral("/ReflectionShareOrderTest.qml")), qPrintable(scene.errors));
        auto *window = scene.window();
        QVERIFY(QTest::qWaitForWindowExposed(window));
        auto *dialog = scene.root->findChild<QObject *>(QStringLiteral("dialog"));
        QVERIFY(dialog);
        QVERIFY(QMetaObject::invokeMethod(dialog, "openForRuns", Q_ARG(QVariant, QVariant(ShareScene::runs()))));
        QTRY_VERIFY(dialog->property("opened").toBool());
        const auto grip = [window](int i) {
            return findVisualItem(window->contentItem(), QStringLiteral("reflectionShareDragGrip_%1").arg(i));
        };
        const QStringList newest{QStringLiteral("3"), QStringLiteral("2"), QStringLiteral("1")};
        QCOMPARE(runIds(dialog), newest);
        QTRY_VERIFY(grip(0) && grip(1) && grip(2) && grip(2)->y() > grip(0)->y());

        // As dragOrderFollowsBothPreviewsAndPngFiles starts one: the first row's grip, which
        // lies inside the window, past the drag threshold and then a little further.
        const QPoint start = centerInWindow(grip(0));
        QTest::mousePress(window, Qt::LeftButton, Qt::NoModifier, start);
        QTest::mouseMove(window, start + QPoint(0, 30));
        QTest::mouseMove(window, start + QPoint(0, 40));
        QTRY_COMPARE(dialog->property("dragFrom").toInt(), 0);
        grip(2)->forceActiveFocus();
        QTest::keyClick(window, Qt::Key_Up);
        bool moved = true;
        QVERIFY(QMetaObject::invokeMethod(dialog, "moveRun", Q_RETURN_ARG(bool, moved), Q_ARG(int, 2), Q_ARG(int, 1)));
        QVERIFY(!moved);
        QCOMPARE(runIds(dialog), newest);
        QCOMPARE(dialog->property("dragFrom").toInt(), 0);

        // The drag still lands the row it picked up.
        QTest::mouseMove(window, start);
        QTest::mouseRelease(window, Qt::LeftButton, Qt::NoModifier, start);
        QTRY_COMPARE(dialog->property("dragFrom").toInt(), -1);
        QCOMPARE(runIds(dialog), newest);
    }
};

} // namespace

MR_DESKTOP_QTEST_SUITE(ReflectionShareOrder, ReflectionShareOrderTests, QGuiApplication)
#include "ReflectionShareOrderTests.moc"
