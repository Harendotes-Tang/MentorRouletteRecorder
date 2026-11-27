#include "DesktopTestRunner.h"
#include "QmlComponentTestSupport.h"
#include <QGuiApplication>
#include <QJSValue>
#include <QQuickItem>
#include <QQuickStyle>
#include <QSignalSpy>
#include <QTest>

namespace {
struct Scene : mrtest::QmlComponentScene
{
    bool create()
    {
        return QmlComponentScene::create(R"(import QtQuick
import QtQuick.Window
import MentorRecorder
Window {
    width: 480; height: 360; visible: true
    property string today: "2026-11-30"
    property var applied: []
    QtObject {
        id: calendar
        property bool loading: false
        property string error: ""
        property var dateCounts: ({})
        function dateRange(key) {
            if (key === "all") return { start: "", end: "" }
            const parts = today.split("-").map(Number)
            const date = new Date(parts[0], parts[1] - 1, parts[2])
            if (key === "last7") date.setDate(date.getDate() - 6)
            if (key === "last30") date.setDate(date.getDate() - 29)
            if (key === "month") date.setDate(1)
            return { start: Qt.formatDate(date, "yyyy-MM-dd"), end: today }
        }
    }
    HistoryDateFilter {
        objectName: "filter"; x: 20; y: 20
        facets: calendar
        onRelativeRangeChanged: applied = [startText, endText, preset, updating]
    }
})", QStringLiteral("/components/HistoryDateProbe.qml"));
    }
    QObject *filter() const { return root->findChild<QObject *>(QStringLiteral("filter")); }
    bool choose(const QString &key)
    {
        return QMetaObject::invokeMethod(filter(), "choose", Q_ARG(QVariant, key));
    }
    bool refresh() { return QMetaObject::invokeMethod(filter(), "refreshRelativeRange"); }
};

class HistoryDateFilterTests final : public QObject
{
    Q_OBJECT
private Q_SLOTS:
    void initTestCase() { mrtest::registerSourceQmlComponents(); }

    // Dates that were not applied explain why on hover, in the application's bubble; a valid
    // range keeps the button quiet (the tip's own condition, kept in the move off Qt's
    // attached ToolTip, 2026-11-27).
    void onlyAnUnappliedRangeExplainsItselfOnHover_data()
    {
        QTest::addColumn<QString>("end");
        QTest::addColumn<bool>("invalid");
        QTest::newRow("in order") << QStringLiteral("2026-11-30") << false;
        QTest::newRow("reversed") << QStringLiteral("2026-11-01") << true;
    }
    void onlyAnUnappliedRangeExplainsItselfOnHover()
    {
        QFETCH(QString, end);
        QFETCH(bool, invalid);
        Scene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
        auto *filter = qobject_cast<QQuickItem *>(scene.filter());
        QVERIFY(filter);
        // Offscreen has no hover-effects hint, so a control keeping Qt's default never hovers.
        filter->setProperty("hoverEnabled", true);
        filter->setProperty("startText", QStringLiteral("2026-11-10"));
        filter->setProperty("endText", end);
        QCOMPARE(filter->property("invalid").toBool(), invalid);
        const QString why = QString::fromUtf8("日期尚未应用，请检查格式与起止顺序");
        QObject *tip = nullptr;
        for (QObject *child : filter->findChildren<QObject *>())
            if (child->inherits("QQuickToolTip") && child->property("text").toString() == why)
                tip = child;
        QVERIFY(tip);

        QTest::mouseMove(scene.window(), QPoint(470, 350));
        QTest::mouseMove(scene.window(), filter->mapToScene(QPointF(filter->width() / 2, filter->height() / 2)).toPoint());
        QTRY_VERIFY(filter->property("hovered").toBool());
        if (invalid) {
            QTRY_VERIFY(tip->property("opened").toBool());
        } else {
            QTest::qWait(300);
            QVERIFY(!tip->property("visible").toBool());
        }
    }

    void relativePresetsRollOverAtomically_data()
    {
        QTest::addColumn<QString>("key");
        QTest::addColumn<QString>("start");
        QTest::newRow("today") << QStringLiteral("today") << QStringLiteral("2026-12-01");
        QTest::newRow("week") << QStringLiteral("last7") << QStringLiteral("2026-11-25");
        QTest::newRow("month-window") << QStringLiteral("last30") << QStringLiteral("2026-11-02");
        QTest::newRow("calendar-month") << QStringLiteral("month") << QStringLiteral("2026-12-01");
    }
    void relativePresetsRollOverAtomically()
    {
        QFETCH(QString, key);
        QFETCH(QString, start);
        Scene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QVERIFY(scene.choose(key));
        QSignalSpy applied(scene.filter(), SIGNAL(relativeRangeChanged()));
        scene.root->setProperty("today", "2026-12-01");
        QVERIFY(scene.refresh());
        QCOMPARE(applied.count(), 1);
        QCOMPARE(scene.filter()->property("startText").toString(), start);
        QCOMPARE(scene.filter()->property("endText").toString(), QStringLiteral("2026-12-01"));
        QCOMPARE(scene.filter()->property("preset").toString(), key);
        const auto snapshot = scene.root->property("applied").value<QJSValue>().toVariant().toList();
        QCOMPARE(snapshot, QVariantList({start, QStringLiteral("2026-12-01"), key, false}));
        QVERIFY(scene.refresh());
        QCOMPARE(applied.count(), 1); // No periodic query when the day did not change.
    }

    void customAndAllRangesDoNotRollOver()
    {
        Scene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QVERIFY(scene.choose("today"));
        QVERIFY(scene.choose("custom"));
        scene.filter()->setProperty("startText", "2026-11-02");
        QSignalSpy applied(scene.filter(), SIGNAL(relativeRangeChanged()));
        scene.root->setProperty("today", "2026-12-01");
        QVERIFY(scene.refresh());
        QCOMPARE(scene.filter()->property("startText").toString(), QStringLiteral("2026-11-02"));
        QCOMPARE(scene.filter()->property("endText").toString(), QStringLiteral("2026-11-30"));
        QCOMPARE(applied.count(), 0);
        QVERIFY(scene.choose("all"));
        QVERIFY(scene.refresh());
        QVERIFY(scene.filter()->property("startText").toString().isEmpty());
        QVERIFY(scene.filter()->property("endText").toString().isEmpty());
        QCOMPARE(applied.count(), 0);
    }

    void timerRollsOverWhilePageIsHidden()
    {
        Scene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QVERIFY(scene.choose("today"));
        scene.filter()->setProperty("visible", false);
        scene.root->setProperty("today", "2026-12-01");
        QTRY_COMPARE_WITH_TIMEOUT(scene.filter()->property("endText").toString(), QStringLiteral("2026-12-01"), 2500);
    }

    void showingPageRefreshesImmediatelyAfterClockChange()
    {
        Scene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QVERIFY(scene.choose("today"));
        scene.filter()->setProperty("visible", false);
        scene.root->setProperty("today", "2026-12-01");
        scene.filter()->setProperty("visible", true);
        QCOMPARE(scene.filter()->property("endText").toString(), QStringLiteral("2026-12-01"));
    }

    void restoringWindowRefreshesImmediately()
    {
        Scene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QVERIFY(scene.choose("today"));
        scene.window()->hide();
        scene.root->setProperty("today", "2026-12-01");
        scene.window()->show();
        QCOMPARE(scene.filter()->property("endText").toString(), QStringLiteral("2026-12-01"));
    }

    /// A held preset slides again once released, but only after the event that released it:
    /// 「取消勾选并更改」 applies the confirmed filter first (audit 2026-11-20, K3-3).
    void aHeldPresetSlidesAfterTheReleaseIsOver()
    {
        Scene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QVERIFY(scene.choose("today"));
        QObject *filter = scene.filter();
        QSignalSpy applied(filter, SIGNAL(relativeRangeChanged()));
        filter->setProperty("holdRelativeRange", true);
        scene.root->setProperty("today", "2026-12-01");
        QVERIFY(scene.refresh());
        QCOMPARE(filter->property("preset").toString(), QStringLiteral("custom"));
        QCOMPARE(filter->property("heldPreset").toString(), QStringLiteral("today"));
        QCOMPARE(filter->property("endText").toString(), QStringLiteral("2026-11-30"));
        QCOMPARE(applied.count(), 0);

        filter->setProperty("holdRelativeRange", false);
        // Deferred: nothing moves inside the change that released the hold.
        QCOMPARE(filter->property("preset").toString(), QStringLiteral("custom"));
        QCOMPARE(applied.count(), 0);
        QTRY_COMPARE(filter->property("preset").toString(), QStringLiteral("today"));
        QCOMPARE(filter->property("endText").toString(), QStringLiteral("2026-12-01"));
        QCOMPARE(filter->property("heldPreset").toString(), QString());
        QCOMPARE(applied.count(), 1);
    }

    /// A filter with other dates mirrored while the preset is held drops the held preset.
    void aMirroredFilterDropsTheHeldPreset()
    {
        Scene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QVERIFY(scene.choose("today"));
        QObject *filter = scene.filter();
        QSignalSpy applied(filter, SIGNAL(relativeRangeChanged()));
        filter->setProperty("holdRelativeRange", true);
        scene.root->setProperty("today", "2026-12-01");
        QVERIFY(scene.refresh());
        QCOMPARE(filter->property("heldPreset").toString(), QStringLiteral("today"));
        filter->setProperty("startText", "2026-11-02");
        QCOMPARE(filter->property("heldPreset").toString(), QString());
        filter->setProperty("holdRelativeRange", false);
        QTest::qWait(50);
        QCOMPARE(filter->property("preset").toString(), QStringLiteral("custom"));
        QCOMPARE(filter->property("startText").toString(), QStringLiteral("2026-11-02"));
        QCOMPARE(applied.count(), 0);
    }

    /// 自定义 left without a date filters nothing, so the button says 全部时间 again; with a
    /// date typed it stays 自定义 (audit 2026-11-20, K3-4).
    void customWithoutDatesFallsBackToAllWhenTheMenuCloses()
    {
        Scene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QObject *filter = scene.filter();
        QObject *menu = filter->property("menu").value<QObject *>();
        QVERIFY(menu);
        QVERIFY(scene.choose("custom"));
        QVERIFY(filter->property("selected").toBool());
        QVERIFY(QMetaObject::invokeMethod(menu, "open"));
        QTRY_VERIFY(menu->property("opened").toBool());
        QVERIFY(QMetaObject::invokeMethod(menu, "close"));
        QTRY_COMPARE(filter->property("preset").toString(), QStringLiteral("all"));
        QVERIFY(!filter->property("selected").toBool());

        QVERIFY(scene.choose("custom"));
        filter->setProperty("startText", "2026-11-02");
        QVERIFY(QMetaObject::invokeMethod(menu, "open"));
        QTRY_VERIFY(menu->property("opened").toBool());
        QVERIFY(QMetaObject::invokeMethod(menu, "close"));
        QTRY_VERIFY(!menu->property("visible").toBool());
        QCOMPARE(filter->property("preset").toString(), QStringLiteral("custom"));
        QVERIFY(filter->property("selected").toBool());
    }
};
} // namespace

MR_DESKTOP_TEST_SUITE(HistoryDateFilter)
{
    qputenv("QT_QPA_PLATFORM", "offscreen");
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication app(argc, argv);
    HistoryDateFilterTests tests;
    return QTest::qExec(&tests, argc, argv);
}
#include "HistoryDateFilterTests.moc"
