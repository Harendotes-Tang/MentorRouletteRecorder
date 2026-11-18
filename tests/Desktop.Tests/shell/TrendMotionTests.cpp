// 动效 (docs/ui-design.md): the completion-trend chart. Every change of series is one
// continuous motion from the picture on screen to the picture Qt Graphs shows next -
// 日 / 周 / 月, 7 天 / 30 天, a refresh, and a change that arrives mid-way.
//
// The chart is real QML from src/Desktop/qml (MotionSceneFixture.h).

#include "DesktopTestRunner.h"
#include "MotionSceneFixture.h"
#include "TestCollectorGuard.h"

#include <QApplication>
#include <QDateTime>
#include <QQuickItem>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QStandardPaths>
#include <QTest>
#include <QTimeZone>

#include <algorithm>
#include <functional>

namespace {

const QByteArray &kTrendScene()
{
    static const QByteArray value = R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 720; height: 260; visible: true
    GraphsTrendChart { objectName: "chart"; anchors.fill: parent; anchors.margins: 10 }
})";
    return value;
}

/// The day the trend series of these tests end on (a Friday).
QDate trendToday() { return {2026, 9, 4}; }

/// Trend buckets like the Collector's: `count` per slot, `start_utc` per slot.
QVariantList buckets(const QString &granularity, const QList<int> &counts)
{
    const QDate today = trendToday();
    QList<QDateTime> starts;
    const auto n = static_cast<int>(counts.size());
    for (int i = n - 1; i >= 0; --i) {
        QDate day;
        if (granularity == QLatin1String("week"))
            day = today.addDays(-(today.dayOfWeek() - 1)).addDays(-7LL * i);
        else if (granularity == QLatin1String("month"))
            day = QDate(today.year(), today.month(), 1).addMonths(-i);
        else
            day = today.addDays(-i);
        starts.append(QDateTime(day, QTime(0, 0), QTimeZone::UTC));
    }
    QVariantList out;
    for (int i = 0; i < n; ++i) {
        out.append(QVariantMap{
            {QStringLiteral("label"), starts.at(i).toString(QStringLiteral("MM-dd"))},
            {QStringLiteral("count"), counts.at(i)},
            {QStringLiteral("start_utc"),
             starts.at(i).toString(QStringLiteral("yyyy-MM-ddTHH:mm:ss.zzzZ"))}});
    }
    return out;
}

QList<int> dayCounts()
{
    return {1, 0, 1, 1, 3, 0, 1, 0, 2, 2, 2, 0, 0, 4, 0, 1, 0, 1, 0, 4, 2, 0, 1, 1, 1, 0, 2, 2, 2, 2};
}

QList<int> weekCounts()
{
    return {0, 0, 0, 1, 2, 5, 6, 3, 4, 2, 3, 3};
}

/// The bars Qt Graphs itself drew (QQuickRectangle children of its renderer),
/// in scene coordinates, left to right.
QList<QRectF> graphsBars(QQuickItem *chart)
{
    QList<QRectF> out;
    const QQuickItem *layer = chart->property("motionLayer").value<QQuickItem *>();
    std::function<void(QQuickItem *)> walk = [&](QQuickItem *item) {
        if (item == layer)
            return;
        if (QByteArray(item->metaObject()->className()) == "QQuickRectangle"
            && item->parentItem()
            && QByteArray(item->parentItem()->metaObject()->className()).contains("BarsRenderer"))
            out.append(item->mapRectToScene(QRectF(0, 0, item->width(), item->height())));
        const auto children = item->childItems();
        for (QQuickItem *child : children)
            walk(child);
    };
    const auto children = chart->childItems();
    for (QQuickItem *child : children) {
        if (QByteArray(child->metaObject()->className()).contains("GraphsView"))
            walk(child);
    }
    std::ranges::sort(out, [](const QRectF &a, const QRectF &b) { return a.x() < b.x(); });
    return out;
}

/// The motion layer's bar items, left to right.
QList<QQuickItem *> layerBars(QQuickItem *chart)
{
    auto *layer = chart->property("motionLayer").value<QQuickItem *>();
    QList<QQuickItem *> out;
    const auto children = layer->childItems();
    for (QQuickItem *child : children) {
        if (child->property("entry").isValid())
            out.append(child);
    }
    std::ranges::sort(out, [](QQuickItem *a, QQuickItem *b) { return a->x() < b->x(); });
    return out;
}

/// The motion layer's bar items in the order the transition listed them.
QList<QQuickItem *> layerBarsInOrder(QQuickItem *chart)
{
    QList<QQuickItem *> out = layerBars(chart);
    std::ranges::sort(out, [](QQuickItem *a, QQuickItem *b) {
        return a->property("index").toInt() < b->property("index").toInt();
    });
    return out;
}

QRectF sceneRect(const QQuickItem *item)
{
    return item->mapRectToScene(QRectF(0, 0, item->width(), item->height()));
}

/// Bars are compared by what is drawn: the left edge, the width, the baseline and the height.
bool sameBar(const QRectF &a, const QRectF &b)
{
    return fuzzyEqual(a.x(), b.x()) && fuzzyEqual(a.width(), b.width())
           && fuzzyEqual(a.bottom(), b.bottom()) && fuzzyEqual(a.height(), b.height());
}

QString barText(const QRectF &bar)
{
    return QStringLiteral("(%1,%2 %3x%4)").arg(bar.x()).arg(bar.y()).arg(bar.width()).arg(bar.height());
}

/// The bar \a share of the way from \a from to \a to, both standing on the same baseline.
QRectF barBetween(const QRectF &from, const QRectF &to, qreal share)
{
    const qreal height = from.height() + ((to.height() - from.height()) * share);
    const qreal bottom = from.bottom() + ((to.bottom() - from.bottom()) * share);
    return {from.x() + ((to.x() - from.x()) * share), bottom - height,
            from.width() + ((to.width() - from.width()) * share), height};
}

/// The week slot (of \a weekCount ending this week) the day slot \a dayIndex starts in.
int weekSlotOfDay(int dayIndex, int dayCount, int weekCount)
{
    const auto monday = [](QDate day) { return day.addDays(-(day.dayOfWeek() - 1)); };
    const QDate day = trendToday().addDays(-(dayCount - 1 - dayIndex));
    return weekCount - 1 - static_cast<int>(monday(day).daysTo(monday(trendToday())) / 7);
}

/// The slots of \a counts that draw a bar, in order.
QList<int> filledSlots(const QList<int> &counts)
{
    QList<int> out;
    for (int i = 0; i < counts.size(); ++i) {
        if (counts.at(i) > 0)
            out.append(i);
    }
    return out;
}

/// The bars of \a bars that show something inside \a plot.
QList<QRectF> shapesIn(const QList<QRectF> &bars, const QRectF &plot)
{
    QList<QRectF> out;
    for (const QRectF &bar : bars) {
        if (bar.height() > 0.01 && bar.width() > 0.01 && bar.right() > plot.left() + 0.01
            && bar.left() < plot.right() - 0.01)
            out.append(bar);
    }
    return out;
}

/// What the motion layer draws inside the plot in this frame.
QList<QRectF> layerShapes(QQuickItem *chart)
{
    QList<QRectF> bars;
    for (const QQuickItem *bar : layerBars(chart))
        bars.append(sceneRect(bar));
    return shapesIn(bars, sceneRect(chart->property("motionLayer").value<QQuickItem *>()));
}

/// What the motion layer draws at \a progress of its running motion. The clock
/// is left alone: its next tick sets its own value again.
QList<QRectF> layerShapesAt(QQuickItem *chart, qreal progress)
{
    auto *layer = chart->property("motionLayer").value<QQuickItem *>();
    const QVariant now = layer->property("progress");
    layer->setProperty("progress", progress);
    const QList<QRectF> shapes = layerShapes(chart);
    layer->setProperty("progress", now);
    return shapes;
}

/// Whether two pictures are the same: every bar of one lies on a bar of the
/// other. Bars drawn on top of each other count once.
bool samePicture(const QList<QRectF> &a, const QList<QRectF> &b)
{
    const auto within = [](const QList<QRectF> &bars, const QList<QRectF> &others) {
        return std::ranges::all_of(bars, [&others](const QRectF &bar) {
            return std::ranges::any_of(others,
                                       [&bar](const QRectF &other) { return sameBar(bar, other); });
        });
    };
    return within(a, b) && within(b, a);
}

QString pictureText(const QList<QRectF> &bars)
{
    QStringList out;
    for (const QRectF &bar : bars)
        out.append(barText(bar));
    return out.join(QLatin1Char(' '));
}

/// Where Qt Graphs draws the \a bars of the series it shows, once it has laid them out.
QList<QRectF> settledGraphsBars(QQuickItem *chart, int bars)
{
    if (!QTest::qWaitFor([&] { return graphsBars(chart).size() == bars; }, 3000))
        return {};
    QTest::qWait(100);
    return graphsBars(chart);
}

} // namespace

namespace {
class TrendMotionTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void initTestCase();
    void trendSwitchLeavesEveryBarAtRest();
    void trendMergeIsOneContinuousMotion();
    void trendSplitStartsOnTheBarsOnScreen();
    void interruptedTrendSwitchGoesOnFromTheFrameOnScreen();
    void interruptedTrendSwitchEndsClean();
    void reducedMotionTrendSwitchIsInstant();
    void trendHoverMatchesRenderedBars_data();
    void trendHoverMatchesRenderedBars();

private:
    static void verifyTrendAtRest(QQuickItem *chart, int bars);
    static void switchAndVerifyEnds(QQuickItem *chart, const QVariantList &series,
                                    const QString &phase, int movingBars);
};
} // namespace

void TrendMotionTests::initTestCase()
{
    QVERIFY(registerShippingQml());
}

void TrendMotionTests::trendHoverMatchesRenderedBars_data()
{
    QTest::addColumn<bool>("useGraphs");
    QTest::newRow("qt-graphs") << true;
    QTest::newRow("fallback") << false;
}

void TrendMotionTests::trendHoverMatchesRenderedBars()
{
    QFETCH(bool, useGraphs);
    Scene scene;
    scene.engine.rootContext()->setContextProperty(QStringLiteral("GraphsAvailable"), useGraphs);
    QByteArray qml = kTrendScene();
    qml.replace("GraphsTrendChart {", "TrendChart {");
    QVERIFY2(scene.create(qml, true), qPrintable(scene.errors));
    QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
    auto *chart = scene.item(QStringLiteral("chart"));
    auto *bars = scene.item(useGraphs ? QStringLiteral("completionTrendChart") : QStringLiteral("completionTrendBars"));
    auto *tip = scene.item(QStringLiteral("trendHoverTip"));
    QVERIFY(chart && bars && tip);
    QCOMPARE(tip->window(), scene.window());
    auto drawnBars = [&] {
        if (useGraphs)
            return graphsBars(bars);
        QList<QRectF> result;
        auto *row = Scene::find(bars, QStringLiteral("trendFallbackBars"));
        if (row) {
            const auto delegates = row->childItems();
            for (auto *bucket : delegates) {
                if (const auto *bar = Scene::find(bucket, QStringLiteral("trendFallbackBar")))
                    result.append(sceneRect(bar));
            }
        }
        std::ranges::sort(result, [](const QRectF &left, const QRectF &right) { return left.x() < right.x(); });
        return result;
    };
    for (const int width : {720, 1100}) {
        scene.window()->setWidth(width);
        QTRY_COMPARE(chart->width(), qreal(width - 20));
        for (const int count : {7, 30}) {
            QList<int> counts(count, 1);
            counts[count / 2] = 4;
            const auto data = buckets(QStringLiteral("day"), counts);
            QVERIFY(chart->setProperty("buckets", data));
            QTRY_COMPARE(drawnBars().size(), count);
            QTRY_VERIFY(!bars->property("transitionRunning").toBool());
            QTRY_VERIFY(drawnBars().last().x() > drawnBars().first().x());
            const auto drawn = drawnBars();
            for (int i = 0; i < count; ++i) {
                QTest::mouseMove(scene.window(), drawn.at(i).center().toPoint());
                QTRY_COMPARE(bars->property("hoveredIndex").toInt(), i);
                QVERIFY(tip->isVisible());
                const auto day = data.at(i).toMap().value(QStringLiteral("label")).toString();
                QCOMPARE(tip->property("text").toString(), QStringLiteral("%1 · %2 次").arg(day).arg(counts.at(i)));
                if (i == count / 2 - 1 || i == count / 2) {
                    const auto tipRect = sceneRect(tip);
                    QVERIFY(qAbs(tipRect.center().x() - drawn.at(i).center().x()) <= 2);
                    QVERIFY(qAbs(drawn.at(i).top() - tipRect.bottom() - 6) <= 2);
                }
            }
            // Sweeping through an empty day clears the label without an exit frame.
            const int empty = count / 2;
            counts[empty] = 0;
            QVERIFY(chart->setProperty("buckets", buckets(QStringLiteral("day"), counts)));
            for (int pass = 0; pass < 3; ++pass) {
                for (const int i : {empty - 1, empty, empty + 1}) {
                    QTest::mouseMove(scene.window(), drawn.at(i).center().toPoint());
                    QTRY_COMPARE(bars->property("hoveredIndex").toInt(), i);
                    QCOMPARE(tip->isVisible(), i != empty);
                }
            }
            QTest::mouseMove(scene.window(), QPoint(1, 1));
            QTRY_COMPARE(bars->property("hoveredIndex").toInt(), -1);
            QVERIFY(!tip->isVisible());
        }
    }
}

void TrendMotionTests::verifyTrendAtRest(QQuickItem *chart, int bars)
{
    QVERIFY(!chart->property("transitionRunning").toBool());
    auto *layer = chart->property("motionLayer").value<QQuickItem *>();
    QVERIFY(layer);
    QVERIFY(!layer->isVisible());
    QVERIFY(fuzzyEqual(layer->property("progress").toReal(), 1.0));
    const QList<QQuickItem *> items = layerBars(chart);
    QCOMPARE(items.size(), bars);
    for (int i = 0; i < items.size(); ++i) {
        const QQuickItem *bar = items.at(i);
        const QString where = QStringLiteral("bar %1").arg(i);
        QVERIFY2(fuzzyEqual(bar->opacity(), 1.0), qPrintable(where));
        QVERIFY2(fuzzyEqual(bar->scale(), 1.0), qPrintable(where));
    }
    // Qt Graphs is back on screen, and the layer's resting bars sit exactly on
    // its pixels - the hand-over at the end of every transition is seamless.
    const QQuickItem *view = nullptr;
    const auto children = chart->childItems();
    for (const QQuickItem *child : children) {
        if (QByteArray(child->metaObject()->className()).contains("GraphsView"))
            view = child;
    }
    if (!view) {
        QTest::qFail("The chart has no GraphsView child", __FILE__, __LINE__);
        return;
    }
    QVERIFY(fuzzyEqual(view->opacity(), 1.0));
    QTRY_COMPARE(graphsBars(chart).size(), bars);
    const QList<QRectF> drawn = graphsBars(chart);
    for (int i = 0; i < items.size(); ++i) {
        const QRectF mine = items.at(i)->mapRectToScene(
            QRectF(0, 0, items.at(i)->width(), items.at(i)->height()));
        const QRectF theirs = drawn.at(i);
        const QString detail = QStringLiteral("bar %1: layer (%2,%3 %4x%5) graphs (%6,%7 %8x%9)")
                                   .arg(i).arg(mine.x()).arg(mine.y()).arg(mine.width())
                                   .arg(mine.height()).arg(theirs.x()).arg(theirs.y())
                                   .arg(theirs.width()).arg(theirs.height());
        QVERIFY2(fuzzyEqual(mine.x(), theirs.x()) && fuzzyEqual(mine.width(), theirs.width())
                     && fuzzyEqual(mine.bottom(), theirs.bottom())
                     && fuzzyEqual(mine.height(), theirs.height()),
                 qPrintable(detail));
    }
}

void TrendMotionTests::switchAndVerifyEnds(QQuickItem *chart, const QVariantList &series,
                                      const QString &phase, int movingBars)
{
    auto *layer = chart->property("motionLayer").value<QQuickItem *>();
    QVERIFY(layer);
    const QRectF plot = sceneRect(layer);
    // The picture on screen, once Qt Graphs has laid it out.
    QTest::qWait(100);
    const QList<QRectF> before = shapesIn(graphsBars(chart), plot);
    QVERIFY2(!before.isEmpty(), qPrintable(phase));

    chart->setProperty("buckets", series);
    QVERIFY(chart->property("transitionRunning").toBool());
    QVERIFY2(layer->property("phase").toString() == phase,
             qPrintable(QStringLiteral("expected %1, the layer runs %2")
                            .arg(phase, layer->property("phase").toString())));
    QCOMPARE(layer->property("progress").toDouble(), 0.0);
    QVERIFY2(layerBars(chart).size() == movingBars,
             qPrintable(QStringLiteral("%1 moves %2 bars, expected %3")
                            .arg(phase).arg(layerBars(chart).size()).arg(movingBars)));

    // Frame 0 is the picture that was on screen ...
    const QList<QRectF> first = layerShapes(chart);
    QVERIFY2(samePicture(first, before),
             qPrintable(QStringLiteral("%1 starts as %2, on screen was %3")
                            .arg(phase, pictureText(first), pictureText(before))));
    const QList<QRectF> last = layerShapesAt(chart, 1.0);

    // ... and the last frame is the picture Qt Graphs shows once the layer is gone.
    QTRY_VERIFY_WITH_TIMEOUT(!chart->property("transitionRunning").toBool(), 3000);
    const QList<QRectF> after =
        shapesIn(settledGraphsBars(chart, static_cast<int>(series.size())), plot);
    QVERIFY2(!after.isEmpty(), qPrintable(phase));
    QVERIFY2(samePicture(last, after),
             qPrintable(QStringLiteral("%1 ends as %2, Qt Graphs then shows %3")
                            .arg(phase, pictureText(last), pictureText(after))));
}

void TrendMotionTests::trendSwitchLeavesEveryBarAtRest()
{
    Scene scene;
    QVERIFY2(scene.create(kTrendScene(), false), qPrintable(scene.errors));
    QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
    QQuickItem *chart = scene.item(QStringLiteral("chart"));
    QVERIFY(chart);
    const QVariantList days = buckets(QStringLiteral("day"), dayCounts());
    QList<int> changed = dayCounts();
    changed[29] = 6;
    const QVariantList changedDays = buckets(QStringLiteral("day"), changed);

    // The first series is simply shown.
    chart->setProperty("buckets", days);
    QVERIFY(!chart->property("transitionRunning").toBool());
    verifyTrendAtRest(chart, 30);
    if (QTest::currentTestFailed())
        return;

    struct Change {
        QVariantList series;
        QString phase;
        int movingBars;
    };
    const QList<Change> changes{
        // 日 -> 周: the twenty days that have a bar, and the four such weeks the
        // day window did not reach, which come in from the left.
        {buckets(QStringLiteral("week"), weekCounts()), QStringLiteral("merge"), 24},
        // 周 -> 日: the same twenty days, and the four weeks that leave.
        {days, QStringLiteral("split"), 24},
        // 30 天 -> 7 天 keeps the slot length: the last seven days widen, the
        // rest leave. Six of the seven and fourteen of the rest have a bar.
        {buckets(QStringLiteral("day"), dayCounts().mid(23)), QStringLiteral("zoom"), 20},
        // 7 天 -> 30 天: the seven narrow, the others come in.
        {days, QStringLiteral("zoom"), 20},
        // Same slots, new counts and a new axis maximum: only the heights move.
        {changedDays, QStringLiteral("height"), 20},
    };
    for (const Change &change : changes) {
        switchAndVerifyEnds(chart, change.series, change.phase, change.movingBars);
        if (QTest::currentTestFailed())
            return;
        verifyTrendAtRest(chart, static_cast<int>(change.series.size()));
        if (QTest::currentTestFailed())
            return;
    }

    // The same answer again (a dashboard refresh) moves nothing.
    chart->setProperty("buckets", changedDays);
    QVERIFY(!chart->property("transitionRunning").toBool());
}

void TrendMotionTests::trendMergeIsOneContinuousMotion()
{
    Scene scene;
    QVERIFY2(scene.create(kTrendScene(), false), qPrintable(scene.errors));
    QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
    auto *chart = scene.item(QStringLiteral("chart"));
    QVERIFY(chart);
    auto *layer = chart->property("motionLayer").value<QQuickItem *>();
    QVERIFY(layer);
    const QVariantList days = buckets(QStringLiteral("day"), dayCounts());
    const QVariantList weeks = buckets(QStringLiteral("week"), weekCounts());
    // The bars that move: the days that have one (every week of the day window
    // has such a day, so its empty days are left out), then the weeks before
    // the day window that have one.
    const QList<int> shownDays = filledSlots(dayCounts());
    const QList<int> arriving = filledSlots(weekCounts().mid(0, 7));
    const auto dayBarCount = static_cast<int>(shownDays.size());
    const auto movingCount = static_cast<int>(shownDays.size() + arriving.size());
    QCOMPARE(dayBarCount, 20);
    QCOMPARE(movingCount, 24);

    // Where Qt Graphs draws the week series at rest.
    chart->setProperty("buckets", weeks);
    const QList<QRectF> weekBars = settledGraphsBars(chart, 12);
    QCOMPARE(weekBars.size(), 12);

    // On a loaded machine one late frame can skip the whole motion; sample again then.
    bool sampled = false;
    for (int attempt = 0; attempt < 5 && !sampled; ++attempt) {
        chart->setProperty("buckets", days);
        QTRY_VERIFY_WITH_TIMEOUT(!chart->property("transitionRunning").toBool(), 3000);
        const QList<QRectF> dayBars = settledGraphsBars(chart, 30);
        QCOMPARE(dayBars.size(), 30);

        chart->setProperty("buckets", weeks);
        QCOMPARE(layer->property("phase").toString(), QStringLiteral("merge"));
        QCOMPARE(layer->property("progress").toDouble(), 0.0);
        const QList<QQuickItem *> moving = layerBarsInOrder(chart);
        QCOMPARE(moving.size(), movingCount);
        const QRectF plot = sceneRect(layer);

        // The first frame is the picture Qt Graphs just showed: every day bar on
        // its own pixels, and the weeks still to come outside the plot.
        for (int k = 0; k < dayBarCount; ++k) {
            const int day = shownDays.at(k);
            const QRectF bar = sceneRect(moving.at(k));
            QVERIFY2(sameBar(bar, dayBars.at(day)),
                     qPrintable(QStringLiteral("day %1 starts at %2, Qt Graphs drew %3")
                                    .arg(day).arg(barText(bar), barText(dayBars.at(day)))));
            QVERIFY(fuzzyEqual(moving.at(k)->opacity(), 1.0));
        }
        for (int k = dayBarCount; k < movingCount; ++k) {
            const QRectF bar = sceneRect(moving.at(k));
            QVERIFY2(bar.right() <= plot.left() + 0.01,
                     qPrintable(QStringLiteral("week %1 starts inside the plot at %2")
                                    .arg(arriving.at(k - dayBarCount)).arg(barText(bar))));
        }

        QTest::qWait(120);
        const double progress = layer->property("progress").toDouble();
        if (layer->property("phase").toString() != QLatin1String("merge")
            || progress <= 0.0 || progress >= 1.0)
            continue;
        sampled = true;

        // Mid-way nothing has been swapped: the same items, each day bar as far
        // along the way to its week's bar as the shared clock says, fully opaque.
        QCOMPARE(layerBarsInOrder(chart), moving);
        for (int k = 0; k < dayBarCount; ++k) {
            const int day = shownDays.at(k);
            const QRectF bar = sceneRect(moving.at(k));
            const QRectF expected =
                barBetween(dayBars.at(day), weekBars.at(weekSlotOfDay(day, 30, 12)), progress);
            QVERIFY2(sameBar(bar, expected),
                     qPrintable(QStringLiteral("day %1 at %2 is %3, expected %4")
                                    .arg(day).arg(progress).arg(barText(bar), barText(expected))));
            QVERIFY(fuzzyEqual(moving.at(k)->opacity(), 1.0));
        }
        // The weeks the day window did not reach arrive as they will stay: their
        // own width and height, still to the left of their slot.
        for (int k = dayBarCount; k < movingCount; ++k) {
            const int week = arriving.at(k - dayBarCount);
            const QRectF bar = sceneRect(moving.at(k));
            const QRectF &resting = weekBars.at(week);
            QVERIFY2(fuzzyEqual(bar.width(), resting.width()) && fuzzyEqual(bar.height(), resting.height())
                         && fuzzyEqual(bar.bottom(), resting.bottom()) && bar.x() < resting.x(),
                     qPrintable(QStringLiteral("week %1 at %2 is %3, it ends on %4")
                                    .arg(week).arg(progress).arg(barText(bar), barText(resting))));
        }
    }
    QVERIFY2(sampled, "no frame inside the merge in five attempts");

    // The last frame is the week series: every day bar has become its week's bar.
    QTRY_VERIFY_WITH_TIMEOUT(!chart->property("transitionRunning").toBool(), 3000);
    verifyTrendAtRest(chart, 12);
}

void TrendMotionTests::trendSplitStartsOnTheBarsOnScreen()
{
    Scene scene;
    QVERIFY2(scene.create(kTrendScene(), false), qPrintable(scene.errors));
    QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
    auto *chart = scene.item(QStringLiteral("chart"));
    QVERIFY(chart);
    auto *layer = chart->property("motionLayer").value<QQuickItem *>();
    QVERIFY(layer);
    chart->setProperty("buckets", buckets(QStringLiteral("week"), weekCounts()));
    const QList<QRectF> weekBars = settledGraphsBars(chart, 12);
    QCOMPARE(weekBars.size(), 12);

    chart->setProperty("buckets", buckets(QStringLiteral("day"), dayCounts()));
    QCOMPARE(layer->property("phase").toString(), QStringLiteral("split"));
    QCOMPARE(layer->property("progress").toDouble(), 0.0);
    // The bars that move: the days that have one, then the weeks before the
    // day window that have one.
    const QList<int> shownDays = filledSlots(dayCounts());
    const QList<int> leaving = filledSlots(weekCounts().mid(0, 7));
    const auto dayBarCount = static_cast<int>(shownDays.size());
    const QList<QQuickItem *> moving = layerBarsInOrder(chart);
    QCOMPARE(moving.size(), shownDays.size() + leaving.size());

    // Frame 0 is the week chart: every day bar starts as the week it was part
    // of, and the weeks the day window drops are still where they were. No
    // week of the week chart is left without a bar on it.
    QList<int> covered;
    for (int k = 0; k < moving.size(); ++k) {
        const int week = k < dayBarCount ? weekSlotOfDay(shownDays.at(k), 30, 12)
                                         : leaving.at(k - dayBarCount);
        const QRectF bar = sceneRect(moving.at(k));
        QVERIFY2(sameBar(bar, weekBars.at(week)),
                 qPrintable(QStringLiteral("bar %1 starts at %2, week %3 was drawn at %4")
                                .arg(k).arg(barText(bar)).arg(week).arg(barText(weekBars.at(week)))));
        QVERIFY(fuzzyEqual(moving.at(k)->opacity(), 1.0));
        if (!covered.contains(week))
            covered.append(week);
    }
    std::ranges::sort(covered);
    QCOMPARE(covered, filledSlots(weekCounts()));

    QTRY_VERIFY_WITH_TIMEOUT(!chart->property("transitionRunning").toBool(), 3000);
    verifyTrendAtRest(chart, 30);
}

void TrendMotionTests::interruptedTrendSwitchGoesOnFromTheFrameOnScreen()
{
    Scene scene;
    QVERIFY2(scene.create(kTrendScene(), false), qPrintable(scene.errors));
    QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
    QQuickItem *chart = scene.item(QStringLiteral("chart"));
    QVERIFY(chart);
    auto *layer = chart->property("motionLayer").value<QQuickItem *>();
    QVERIFY(layer);
    const QVariantList days = buckets(QStringLiteral("day"), dayCounts());
    const QVariantList weeks = buckets(QStringLiteral("week"), weekCounts());
    const QVariantList months = buckets(QStringLiteral("month"), {3, 4, 9, 12, 8, 2});

    struct Case {
        const char *what;
        QVariantList from;
        QVariantList via;
        QVariantList to;
    };
    const QList<Case> cases{
        {"日 -> 周, back to 日 mid-merge", days, weeks, days},
        {"日 -> 周, on to 月 mid-merge", days, weeks, months},
        {"周 -> 日, back to 周 mid-split", weeks, days, weeks},
        // The bars on screen still stand for weeks: each splits into its days.
        {"月 -> 周, on to 日 mid-split", months, weeks, days},
    };
    for (const Case &item : cases) {
        // On a loaded machine one late frame can skip the first motion; try again then.
        bool sampled = false;
        for (int attempt = 0; attempt < 5 && !sampled; ++attempt) {
            chart->setProperty("buckets", item.from);
            QTRY_VERIFY_WITH_TIMEOUT(!chart->property("transitionRunning").toBool(), 3000);
            chart->setProperty("buckets", item.via);
            QTest::qWait(120);
            const double progress = layer->property("progress").toDouble();
            if (!chart->property("transitionRunning").toBool() || progress <= 0.0 || progress >= 1.0)
                continue;
            sampled = true;
            const QList<QRectF> onScreen = layerShapes(chart);
            QVERIFY2(!onScreen.isEmpty(), item.what);

            // The next motion starts on the frame the first one was cut at ...
            chart->setProperty("buckets", item.to);
            QVERIFY2(chart->property("transitionRunning").toBool(), item.what);
            QCOMPARE(layer->property("progress").toDouble(), 0.0);
            const QList<QRectF> first = layerShapes(chart);
            QVERIFY2(samePicture(first, onScreen),
                     qPrintable(QStringLiteral("%1 (cut at %2): starts as %3, on screen was %4")
                                    .arg(QString::fromUtf8(item.what)).arg(progress)
                                    .arg(pictureText(first), pictureText(onScreen))));
            const QList<QRectF> last = layerShapesAt(chart, 1.0);

            // ... and ends on the series asked for last.
            QTRY_VERIFY_WITH_TIMEOUT(!chart->property("transitionRunning").toBool(), 3000);
            const auto bars = static_cast<int>(item.to.size());
            const QList<QRectF> after = shapesIn(settledGraphsBars(chart, bars), sceneRect(layer));
            QVERIFY2(samePicture(last, after),
                     qPrintable(QStringLiteral("%1: ends as %2, Qt Graphs then shows %3")
                                    .arg(QString::fromUtf8(item.what), pictureText(last),
                                         pictureText(after))));
            verifyTrendAtRest(chart, bars);
            if (QTest::currentTestFailed())
                return;
        }
        QVERIFY2(sampled, item.what);
    }
}

void TrendMotionTests::interruptedTrendSwitchEndsClean()
{
    Scene scene;
    QVERIFY2(scene.create(kTrendScene(), false), qPrintable(scene.errors));
    QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
    QQuickItem *chart = scene.item(QStringLiteral("chart"));
    QVERIFY(chart);
    auto *layer = chart->property("motionLayer").value<QQuickItem *>();
    chart->setProperty("buckets", buckets(QStringLiteral("day"), dayCounts()));
    QTRY_COMPARE(graphsBars(chart).size(), 30);

    // Back to 日 in the middle of the merge.
    chart->setProperty("buckets", buckets(QStringLiteral("week"), weekCounts()));
    QTest::qWait(120);
    QCOMPARE(layer->property("phase").toString(), QStringLiteral("merge"));
    chart->setProperty("buckets", buckets(QStringLiteral("day"), dayCounts()));
    QCOMPARE(layer->property("phase").toString(), QStringLiteral("split"));
    QTRY_VERIFY_WITH_TIMEOUT(!chart->property("transitionRunning").toBool(), 3000);
    verifyTrendAtRest(chart, 30);
    if (QTest::currentTestFailed())
        return;

    // On to 月 while the merge into 周 is still moving, back to 周 (a split) and
    // the same 周 answer again mid-split: every change replaces the one before
    // it or is left alone as it should be, and nothing is left behind.
    chart->setProperty("buckets", buckets(QStringLiteral("week"), weekCounts()));
    QTest::qWait(60);
    chart->setProperty("buckets", buckets(QStringLiteral("month"), {3, 4, 9, 12, 8, 2}));
    QCOMPARE(layer->property("phase").toString(), QStringLiteral("merge"));
    QTest::qWait(60);
    chart->setProperty("buckets", buckets(QStringLiteral("week"), weekCounts()));
    QCOMPARE(layer->property("phase").toString(), QStringLiteral("split"));
    QTest::qWait(60);
    chart->setProperty("buckets", buckets(QStringLiteral("week"), weekCounts()));
    QTRY_VERIFY_WITH_TIMEOUT(!chart->property("transitionRunning").toBool(), 3000);
    // Nothing a replaced motion left behind may start the bars again.
    QTest::qWait(600);
    QVERIFY(!chart->property("transitionRunning").toBool());
    verifyTrendAtRest(chart, 12);
    if (QTest::currentTestFailed())
        return;
}

void TrendMotionTests::reducedMotionTrendSwitchIsInstant()
{
    Scene scene;
    QVERIFY2(scene.create(kTrendScene(), true), qPrintable(scene.errors));
    QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
    QQuickItem *chart = scene.item(QStringLiteral("chart"));
    QVERIFY(chart);
    chart->setProperty("buckets", buckets(QStringLiteral("day"), dayCounts()));
    chart->setProperty("buckets", buckets(QStringLiteral("week"), weekCounts()));
    QVERIFY(!chart->property("transitionRunning").toBool());
    verifyTrendAtRest(chart, 12);
    if (QTest::currentTestFailed())
        return;
}

MR_DESKTOP_TEST_SUITE(TrendMotion)
{
    // Never run against the user's own Collector, database or serve lease - see
    // TestCollectorGuard.h. Must precede any construction.
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QApplication app(argc, argv);
    TrendMotionTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "TrendMotionTests.moc"
