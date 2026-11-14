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
