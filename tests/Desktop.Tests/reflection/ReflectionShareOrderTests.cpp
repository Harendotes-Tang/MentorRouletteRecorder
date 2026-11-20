#include "DesktopTestRunner.h"
#include "BundledFonts.h"
#include "Formatters.h"
#include "JobCatalog.h"
#include "QmlComponentTestSupport.h"
#include "ReflectionLibraryController.h"
#include "ReflectionShareController.h"

#include <QFileInfo>
#include <QGuiApplication>
#include <QJSValue>
#include <QQmlContext>
#include <QQmlListReference>
#include <QQuickItem>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QTemporaryDir>

namespace {

QQuickItem *findVisual(QQuickItem *root, const QString &name)
{
    if (root->objectName() == name)
        return root;
    for (auto *child : root->childItems())
        if (auto *found = findVisual(child, name))
            return found;
    return nullptr;
}

QStringList runIds(QObject *dialog)
{
    const QVariant value = dialog->property("runsData");
    const auto runs = value.canConvert<QJSValue>() ? value.value<QJSValue>().toVariant().toList() : value.toList();
    QStringList ids;
    for (const auto &run : runs)
        ids.append(run.toMap().value(QStringLiteral("run_id")).toString());
    return ids;
}

QPoint centerInWindow(QQuickItem *item)
{
    return item->mapToScene(item->boundingRect().center()).toPoint();
}

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
        auto *stack = findVisual(host->contentItem(), QStringLiteral("reflectionShareIndividualCards"));
        auto *preview = findVisual(host->contentItem(), QStringLiteral("reflectionSharePreview"));
        QVERIFY(stack && preview);
        // The dialog fills the window; the cards stop at their limit and sit in the middle.
        QTRY_COMPARE(dialog->property("width").toReal(), 1500.0);
        QTRY_COMPARE(stack->width(), dialog->property("cardLimit").toReal());
        QVERIFY(preview->width() > 1400);
        QVERIFY(qAbs(stack->mapToItem(preview, QPointF(stack->width() / 2, 0)).x() - preview->width() / 2) < 12);
        QVERIFY(!findVisual(host->contentItem(), QStringLiteral("reflectionShareCloseButton"))->isVisible());

        const QStringList newest{QStringLiteral("3"), QStringLiteral("2"), QStringLiteral("1")};
        QCOMPARE(runIds(dialog), newest);
        const auto tap = [host](const QString &name) {
            auto *button = findVisual(host->contentItem(), name);
            if (!button || !button->isVisible())
                return false;
            QTest::mouseClick(host, Qt::LeftButton, Qt::NoModifier, centerInWindow(button));
            return true;
        };
        // The first row has nowhere further up to go, the last nowhere further down.
        QTRY_VERIFY(findVisual(host->contentItem(), QStringLiteral("reflectionShareMoveLast_0")));
        QVERIFY(!findVisual(host->contentItem(), QStringLiteral("reflectionShareMoveFirst_0"))->isVisible());
        QVERIFY(!findVisual(host->contentItem(), QStringLiteral("reflectionShareMoveLast_2"))->isVisible());
        QVERIFY(tap(QStringLiteral("reflectionShareMoveLast_0")));
        QTRY_COMPARE(runIds(dialog), (QStringList{QStringLiteral("2"), QStringLiteral("1"), QStringLiteral("3")}));
        QTRY_VERIFY(findVisual(host->contentItem(), QStringLiteral("reflectionShareMoveFirst_2")));
        // The rows are rebuilt in the new order, and the last one lies below the window: bring it in.
        QTest::qWait(200);
        auto *flick = qvariant_cast<QObject *>(preview->property("contentItem"));
        QVERIFY(flick);
        flick->setProperty("contentY", flick->property("contentHeight").toReal() - preview->height());
        QTest::qWait(100);
        QVERIFY(tap(QStringLiteral("reflectionShareMoveFirst_2")));
        QTRY_COMPARE(runIds(dialog), newest);
        QCOMPARE(findVisual(host->contentItem(), QStringLiteral("reflectionImageOrder"))->property("currentIndex").toInt(), 2);

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
        QVERIFY2(scene.create(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    visible: true; width: 820; height: 940
    ReflectionShareDialog { objectName: "dialog"; anchors.centerIn: Overlay.overlay }
})", QStringLiteral("/ReflectionShareOrderTest.qml")), qPrintable(scene.errors));

        auto *window = scene.window();
        QVERIFY(QTest::qWaitForWindowExposed(window));
        auto *dialog = scene.root->findChild<QObject *>(QStringLiteral("dialog"));
        QVERIFY(dialog);
        const QVariantList runs = ShareScene::runs();
        const auto open = [dialog, &runs] {
            return QMetaObject::invokeMethod(dialog, "openForRuns", Q_ARG(QVariant, QVariant(runs)));
        };
        const auto visual = [window](const QString &name) { return findVisual(window->contentItem(), name); };
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
        QVERIFY(!findVisual(stack, QStringLiteral("reflectionShareDragGrip_0")));

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
        QVERIFY(!findVisual(combined, QStringLiteral("reflectionShareDragGrip_0")));
        const QQmlListReference timeline(combined, "collectionEntries");
        QCOMPARE(timeline.count(), 3);
        auto *firstEntry = qobject_cast<QQuickItem *>(timeline.at(0));
        QVERIFY(firstEntry);
        auto *firstNote = findVisual(firstEntry, QStringLiteral("reflectionShareFullText"));
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

    // Audit 2026-11-20 L2-1: ↑/↓ on a focused grip during another grip's mouse drag rebuilt
    // the rows under the drag's own row indexes. The order waits for the drag to end.
    void aKeyMoveDuringAMouseDragLeavesTheOrderAlone()
    {
        ShareScene share;
        auto &scene = share.scene;
        QVERIFY2(scene.create(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    visible: true; width: 820; height: 940
    ReflectionShareDialog { objectName: "dialog"; anchors.centerIn: Overlay.overlay }
})", QStringLiteral("/ReflectionShareOrderTest.qml")), qPrintable(scene.errors));
        auto *window = scene.window();
        QVERIFY(QTest::qWaitForWindowExposed(window));
        auto *dialog = scene.root->findChild<QObject *>(QStringLiteral("dialog"));
        QVERIFY(dialog);
        QVERIFY(QMetaObject::invokeMethod(dialog, "openForRuns", Q_ARG(QVariant, QVariant(ShareScene::runs()))));
        QTRY_VERIFY(dialog->property("opened").toBool());
        const auto grip = [window](int i) {
            return findVisual(window->contentItem(), QStringLiteral("reflectionShareDragGrip_%1").arg(i));
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
