#include "DesktopTestRunner.h"
#include "AppController.h"
#include "BundledFonts.h"
#include "Formatters.h"
#include "IBackend.h"
#include "JobCatalog.h"
#include "RoleCatalog.h"
#include "StatisticsController.h"
#include "TestCollectorGuard.h"

#include <QDir>
#include <QElapsedTimer>
#include <QFile>
#include <QGuiApplication>
#include <QImage>
#include <QJsonArray>
#include <QPointer>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QSignalSpy>
#include <QStandardPaths>
#include <QTest>
#include <QTextStream>
#include <QTimer>
#include <memory>

namespace {
/// Dashboard answers are completed explicitly, including old-connection and out-of-order replies.
class ProgressBackend final : public mr::IBackend
{
public:
    QString backendName() const override { return QStringLiteral("mock"); }
    bool isConnected() const override { return true; }
    mr::BackendReply *request(const QString &type, const QJsonObject &payload = {}) override
    {
        auto *reply = new mr::BackendReply(QString::number(++serial), type, this);
        if (type == QLatin1String("GetDashboardStats")) {
            dashboards.append(reply);
            lastDashboardPayload = payload;
            dashboardPayloadOf.insert(reply, payload);
        }
        else if (type == QLatin1String("UpdateAchievementBaseline")) {
            settingsReply = reply;
            settingsPayload = payload;
        }
        else {
            QTimer::singleShot(0, reply, [reply] { reply->succeed({}); });
        }
        return reply;
    }

    static QJsonObject snapshot(int progress, int goal = 2000, int baseline = 0)
    {
        return {{QStringLiteral("achievement_progress"), progress},
                {QStringLiteral("goal_count"), goal},
                {QStringLiteral("baseline_completed_count"), baseline},
                {QStringLiteral("remaining"), qMax(0, goal - progress)}};
    }
    void read(mr::StatisticsController &stats, int progress, int goal = 2000, int baseline = 0)
    {
        stats.refreshDashboard();
        dashboards.takeLast()->succeed(snapshot(progress, goal, baseline));
    }

    QList<QPointer<mr::BackendReply>> dashboards;
    QPointer<mr::BackendReply> settingsReply;
    QJsonObject settingsPayload;
    QJsonObject lastDashboardPayload;
    QHash<const mr::BackendReply *, QJsonObject> dashboardPayloadOf;
    int serial = 0;
};

struct Scene {
    ProgressBackend backend;
    mr::AppController app{&backend, nullptr};
    mr::Formatters formatters;
    mr::JobCatalog jobs;
    mr::RoleCatalog roles;
    QQmlEngine engine;
    std::unique_ptr<QObject> root;
    QString errors;

    bool create(bool reduceMotion, const QString &style = QStringLiteral("harendotes"),
                bool dark = true, bool dashboard = false)
    {
        app.setThemeMode(dark ? QStringLiteral("dark") : QStringLiteral("light"));
        auto *context = engine.rootContext();
        context->setContextProperty(QStringLiteral("App"), &app);
        context->setContextProperty(QStringLiteral("Fmt"), &formatters);
        context->setContextProperty(QStringLiteral("Jobs"), &jobs);
        context->setContextProperty(QStringLiteral("Roles"), &roles);
        context->setContextProperty(QStringLiteral("ReduceMotion"), reduceMotion);
        context->setContextProperty(QStringLiteral("ForceUiStyle"), style);
        QObject::connect(&engine, &QQmlEngine::warnings, &engine, [this](const QList<QQmlError> &warnings) {
            for (const auto &warning : warnings)
                errors += warning.toString() + QLatin1Char('\n');
        });
        QQmlComponent component(&engine);
        component.setData(dashboard ? QByteArray(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 1100; height: 760; visible: true; color: Theme.contentBackground
    DashboardPage { objectName: "dashboard"; anchors.fill: parent }
})") : QByteArray(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    id: shell
    width: 420; height: 340; visible: true; color: Theme.contentBackground
    property int clicks: 0
    MouseArea { anchors.fill: parent; onClicked: shell.clicks += 1 }
    ProgressRing {
        id: ring; objectName: "ring"; width: 200; height: 200; anchors.centerIn: parent
        maximum: 2000; value: 1980; label: String(value)
        detail: Fmt.percent(value / maximum, 1)
    }
    AchievementConfetti { objectName: "confetti"; anchors.fill: parent }
})"), QUrl());
        root.reset(component.create());
        for (const auto &error : component.errors())
            errors += error.toString() + QLatin1Char('\n');
        return root != nullptr;
    }

    QQuickWindow *window() const { return qobject_cast<QQuickWindow *>(root.get()); }
    QObject *find(const char *name) const
    {
        if (auto *owned = root->findChild<QObject *>(QLatin1String(name))) return owned;
        // Repeater delegates belong to their creation context, while their visual
        // parent is the page. Inspect the rendered item tree as well.
        std::function<QQuickItem *(QQuickItem *)> visit = [&](QQuickItem *item) -> QQuickItem * {
            if (item->objectName() == QLatin1String(name)) return item;
            for (auto *child : item->childItems()) if (auto *match = visit(child)) return match;
            return nullptr;
        };
        return window() ? visit(window()->contentItem()) : nullptr;
    }
};

constexpr int kCelebrationFrames = 40;
constexpr int kCelebrationFrameMs = 80;

/// Frames of the real dashboard paced on the wall clock, so a GIF built from
/// celebration-frames.txt plays at the speed the window showed. Its first line
/// is the achievement card's rectangle in device pixels, the preview crop.
bool captureCelebration(const Scene &scene, const QString &evidence)
{
    auto *card = qobject_cast<QQuickItem *>(scene.find("achievementCard"));
    if (!card)
        return false;
    const qreal dpr = scene.window()->devicePixelRatio();
    const QRectF rect = card->mapRectToScene(QRectF(0, 0, card->width(), card->height()));
    QFile index(evidence + QStringLiteral("/celebration-frames.txt"));
    if (!index.open(QIODevice::WriteOnly | QIODevice::Text))
        return false;
    QTextStream out(&index);
    out << "card " << qRound(rect.x() * dpr) << ' ' << qRound(rect.y() * dpr) << ' '
        << qRound(rect.width() * dpr) << ' ' << qRound(rect.height() * dpr) << '\n';
    QElapsedTimer clock;
    clock.start();
    for (int frame = 0; frame < kCelebrationFrames; ++frame) {
        while (clock.elapsed() < static_cast<qint64>(frame) * kCelebrationFrameMs)
            QTest::qWait(5);
        const qint64 at = clock.elapsed();
        const QImage image = scene.window()->grabWindow();
        const QString name = QStringLiteral("celebration-%1.png").arg(frame, 2, 10, QLatin1Char('0'));
        if (image.isNull() || !image.save(evidence + QLatin1Char('/') + name))
            return false;
        if (frame == 8 && !image.save(evidence + QStringLiteral("/dashboard-celebration.png")))
            return false;
        out << name << ' ' << at << '\n';
    }
    return true;
}
} // namespace

namespace {
class AchievementProgressTests : public QObject
{
    Q_OBJECT
private Q_SLOTS:
    void initTestCase();
    void milestonesAreObservedOnce();
    void settingsAndReconnectDoNotCelebrate();
    void staleRepliesDoNotAdvanceMilestones();
    void savingSettingsDoesNotCelebrate();
    void laps_data();
    void laps();
    void crossingAnimatesForward();
    void hiddenRingSettlesImmediately();
    void confettiRespectsMotionVisibilityAndInput();
    void dashboardUsesUncappedPercentAndMilestoneSignal();
    void dashboardTopRowGrowsForAThirdRowOfStatCards();
    void renderedEvidence();
    void regionalMilestonesDoNotReplayAcrossScopes();
    void regionalBaselineEditsOwnTheirRegion();
    void regionalDashboardRendersSeparateProgress();
    void regionalReadsProtectSettingsAndListingScope();
    void failedScopeReadNeverRetainsAnotherRegionsCounts();
    void regionalGoalReadUsesTheRunsRegion();
    void targetedReadsRejectOlderDashboardMilestones();
    void unusedRegionsReadTheirOwnAuthoritativeDefaults();
    void reconnectReadsEveryRegionTheScopeReadLeftOut();
};
} // namespace

void AchievementProgressTests::initTestCase()
{
    const QString base = QString::fromUtf8(MR_DESKTOP_QML_DIR);
    qmlRegisterSingletonType(QUrl::fromLocalFile(base + QStringLiteral("/Theme.qml")),
                             "MentorRecorder", 1, 0, "Theme");
    for (const auto &directory : {QStringLiteral("/components"), QStringLiteral("/pages"),
                                  QStringLiteral("/dialogs"), QStringLiteral("/charts")}) {
        for (const auto &file : QDir(base + directory).entryList({QStringLiteral("*.qml")}, QDir::Files)) {
            const QByteArray name = file.chopped(4).toUtf8();
            qmlRegisterType(QUrl::fromLocalFile(base + directory + QLatin1Char('/') + file),
                            "MentorRecorder", 1, 0, name.constData());
        }
    }
}

void AchievementProgressTests::milestonesAreObservedOnce()
{
    ProgressBackend backend;
    mr::StatisticsController stats(&backend);
    QSignalSpy spy(&stats, SIGNAL(achievementMilestoneReached(int)));
    QVERIFY(spy.isValid());
    backend.read(stats, 1999);
    QCOMPARE(spy.count(), 0);
    backend.read(stats, 2000);
    QCOMPARE(spy.count(), 1);
    QCOMPARE(spy.last().first().toInt(), 1);
    backend.read(stats, 2000);
    backend.read(stats, 1990);
    backend.read(stats, 2000);
    QCOMPARE(spy.count(), 1);
    backend.read(stats, 3999);
    backend.read(stats, 4000);
    QCOMPARE(spy.count(), 2);
    QCOMPARE(spy.last().first().toInt(), 2);
    backend.read(stats, 8001); // A batch is bounded to one burst, at the newest milestone.
    QCOMPARE(spy.count(), 3);
    QCOMPARE(spy.last().first().toInt(), 4);
}

void AchievementProgressTests::settingsAndReconnectDoNotCelebrate()
{
    ProgressBackend backend;
    mr::StatisticsController stats(&backend);
    QSignalSpy spy(&stats, SIGNAL(achievementMilestoneReached(int)));
    backend.read(stats, 1999);
    backend.read(stats, 2100, 2000, 100);
    backend.read(stats, 2100, 1000, 100);
    QCOMPARE(spy.count(), 0);
    stats.forgetAchievementSettings();
    backend.read(stats, 3000, 1000, 100);
    QCOMPARE(spy.count(), 0);
    backend.read(stats, 4000, 1000, 100);
    QCOMPARE(spy.count(), 1);
    stats.forgetAchievementSettings();
    backend.read(stats, 3900, 1000, 100);
    backend.read(stats, 4000, 1000, 100);
    QCOMPARE(spy.count(), 1);
}

void AchievementProgressTests::staleRepliesDoNotAdvanceMilestones()
{
    ProgressBackend backend;
    mr::StatisticsController stats(&backend);
    QSignalSpy spy(&stats, SIGNAL(achievementMilestoneReached(int)));
    backend.read(stats, 1999);
    stats.refreshDashboard();
    const auto older = backend.dashboards.takeLast();
    backend.read(stats, 3999);
    QCOMPARE(spy.count(), 1);
    older->succeed(ProgressBackend::snapshot(4000));
    QCOMPARE(spy.count(), 1);
    backend.read(stats, 4000);
    QCOMPARE(spy.count(), 2);

    stats.refreshDashboard();
    const auto oldConnection = backend.dashboards.takeLast();
    stats.forgetAchievementSettings();
    backend.read(stats, 5999);
    oldConnection->succeed(ProgressBackend::snapshot(6000));
    QCOMPARE(spy.count(), 2);
    backend.read(stats, 6000);
    QCOMPARE(spy.count(), 3);
}

void AchievementProgressTests::savingSettingsDoesNotCelebrate()
{
    ProgressBackend backend;
    mr::StatisticsController stats(&backend);
    QSignalSpy spy(&stats, SIGNAL(achievementMilestoneReached(int)));
    backend.read(stats, 1999);
    stats.updateAchievementBaseline(2000, 100, QStringLiteral("test baseline"));
    QVERIFY(backend.settingsReply);
    backend.read(stats, 2100, 2000, 100);
    QCOMPARE(spy.count(), 0);
    backend.settingsReply->succeed({{QStringLiteral("goal_count"), 2000},
                                    {QStringLiteral("baseline_completed_count"), 100}});
    backend.dashboards.takeLast()->succeed(ProgressBackend::snapshot(2100, 2000, 100));
    QCOMPARE(spy.count(), 0);
    backend.read(stats, 4000, 2000, 100);
    QCOMPARE(spy.count(), 1);
}

void AchievementProgressTests::laps_data()
{
    QTest::addColumn<int>("count");
    QTest::addColumn<double>("ratio");
    QTest::addColumn<double>("fraction");
    for (const int count : {0, 1980, 2000, 2200, 3000, 4000, 5000}) {
        const double ratio = static_cast<double>(count) / 2000;
        const double fraction = count > 0 && count % 2000 == 0 ? 1.0 : static_cast<double>(count % 2000) / 2000;
        QTest::newRow(qPrintable(QString::number(count))) << count << ratio << fraction;
    }
}

void AchievementProgressTests::laps()
{
    QFETCH(int, count);
    QFETCH(double, ratio);
    QFETCH(double, fraction);
    Scene scene;
    QVERIFY2(scene.create(true), qPrintable(scene.errors));
    auto *ring = scene.find("ring");
    QVERIFY(ring);
    ring->setProperty("value", count);
    QVERIFY(qAbs(ring->property("progress").toDouble() - ratio) < 0.000001);
    QVERIFY(qAbs(ring->property("shown").toDouble() - ratio) < 0.000001);
    QVERIFY(qAbs(ring->property("lapProgress").toDouble() - fraction) < 0.000001);
}

void AchievementProgressTests::crossingAnimatesForward()
{
    Scene scene;
    QVERIFY2(scene.create(false), qPrintable(scene.errors));
    auto *ring = scene.find("ring");
    QTRY_VERIFY(qAbs(ring->property("shown").toDouble() - 0.99) < 0.000001);
    ring->setProperty("value", 2020);
    QTRY_VERIFY(ring->property("shown").toDouble() > 0.99);
    QVERIFY(ring->property("shown").toDouble() <= 1.01);
    QTRY_VERIFY(qAbs(ring->property("shown").toDouble() - 1.01) < 0.000001);
}

void AchievementProgressTests::hiddenRingSettlesImmediately()
{
    Scene scene;
    QVERIFY2(scene.create(false), qPrintable(scene.errors));
    QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
    auto *ring = scene.find("ring");
    ring->setProperty("value", 2200);
    scene.window()->hide();
    QCOMPARE(ring->property("shown").toDouble(), 1.1);
    ring->setProperty("value", 3000);
    QCOMPARE(ring->property("shown").toDouble(), 1.5);
    scene.window()->show();
    QCOMPARE(ring->property("shown").toDouble(), 1.5);
}

void AchievementProgressTests::confettiRespectsMotionVisibilityAndInput()
{
    for (const bool reduce : {true, false}) {
        Scene scene;
        QVERIFY2(scene.create(reduce), qPrintable(scene.errors));
        QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
        auto *confetti = scene.find("confetti");
        QVERIFY(confetti);
        QVERIFY(QMetaObject::invokeMethod(confetti, "burst"));
        QCOMPARE(confetti->property("running").toBool(), !reduce);
        QTest::mouseClick(scene.window(), Qt::LeftButton, Qt::NoModifier, QPoint(210, 170));
        QTRY_COMPARE_WITH_TIMEOUT(scene.root->property("clicks").toInt(), 1, 200);
        if (!reduce) {
            scene.window()->hide();
            QTRY_VERIFY(!confetti->property("running").toBool());
        }
    }
}

void AchievementProgressTests::dashboardUsesUncappedPercentAndMilestoneSignal()
{
    Scene scene;
    QVERIFY2(scene.create(false, QStringLiteral("harendotes"), true, true), qPrintable(scene.errors));
    QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
    scene.app.refreshDashboard();
    scene.backend.dashboards.takeLast()->succeed(ProgressBackend::snapshot(2200));
    auto *ring = scene.find("achievementRing");
    auto *confetti = scene.find("achievementConfetti");
    QVERIFY(ring && confetti);
    QCOMPARE(ring->property("label").toString(), QStringLiteral("2200"));
    QVERIFY(ring->property("detail").toString().startsWith(QStringLiteral("110")));
    QVERIFY(!confetti->property("running").toBool());
    const QString evidence = qEnvironmentVariable("MR_ACHIEVEMENT_EVIDENCE_DIR");
    if (!evidence.isEmpty()) {
        QVERIFY(QDir().mkpath(evidence));
        QTRY_VERIFY(qAbs(ring->property("shown").toDouble() - 1.1) < 0.000001);
        QTest::qWait(35);
        QVERIFY(scene.window()->grabWindow().save(evidence + QStringLiteral("/dashboard-110.png")));
    }
    scene.app.refreshDashboard();
    scene.backend.dashboards.takeLast()->succeed(ProgressBackend::snapshot(4000));
    QVERIFY(confetti->property("running").toBool());
    if (!evidence.isEmpty())
        QVERIFY(captureCelebration(scene, evidence));
    scene.find("dashboard")->setProperty("visible", false);
    QTRY_VERIFY(!confetti->property("running").toBool());
    scene.find("dashboard")->setProperty("visible", true);
    QVERIFY(!confetti->property("running").toBool());
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

// The achievement row holds the stat grid and 当前导随. A ninth card (中途加入 or 未知结果)
// adds a third grid row; the row must grow with it instead of staying 340 px and letting
// 当前导随 slide under 完成趋势 (found in the 1.8.0 candidate, 2026-10-27).
void AchievementProgressTests::dashboardTopRowGrowsForAThirdRowOfStatCards()
{
    Scene scene;
    QVERIFY2(scene.create(true, QStringLiteral("classic"), true, true), qPrintable(scene.errors));
    QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
    auto *row = qobject_cast<QQuickItem *>(scene.find("dashboardTopRow"));
    auto *current = qobject_cast<QQuickItem *>(scene.find("dashboardCurrentRunCard"));
    auto *trend = qobject_cast<QQuickItem *>(scene.find("dashboardTrendCard"));
    QVERIFY(row && current && trend);
    const auto top = [](const QQuickItem *item) { return item->mapToScene(QPointF(0, 0)).y(); };
    const auto bottom = [](const QQuickItem *item) { return item->mapToScene(QPointF(0, item->height())).y(); };

    qreal twoRowColumn = 0; // the stat grid's two rows plus 当前导随, as the column wants them
    for (const bool ninth : {false, true}) {
        QJsonObject snapshot = ProgressBackend::snapshot(1445);
        if (ninth) {
            snapshot.insert(QStringLiteral("joined_in_progress_count"), 9);
            snapshot.insert(QStringLiteral("joined_in_progress_rate"), 0.15);
            snapshot.insert(QStringLiteral("result_breakdown"), QJsonObject{
                {QStringLiteral("buckets"), QJsonArray{QJsonObject{{QStringLiteral("result"), QStringLiteral("UNKNOWN")},
                                                                   {QStringLiteral("count"), 4}}}}});
        }
        scene.app.refreshDashboard();
        scene.backend.dashboards.takeLast()->succeed(snapshot);
        QTRY_COMPARE(scene.app.statCards().size(), ninth ? 9 : 7);
        // Never below the 340 it always had; the ninth card adds one stat row (96 + 12).
        // The all-regions view stacks one achievement card per region beside the stats,
        // and that column may be the taller one: the row follows whichever is tallest.
        QTRY_VERIFY2(row->height() >= 340.0, qPrintable(QString::number(row->height())));
        if (ninth) {
            QTRY_VERIFY(scene.find("achievementCard"));
            const QQuickItem *achievementColumn = qobject_cast<QQuickItem *>(scene.find("achievementCard"))->parentItem();
            QTRY_COMPARE(row->height(), qMax(qMax(340.0, twoRowColumn + 108.0), achievementColumn->implicitHeight()));
        } else {
            twoRowColumn = current->parentItem()->implicitHeight();
        }
        const auto where = [&] {
            const QQuickItem *column = current->parentItem();
            const QQuickItem *grid = column->childItems().value(0);
            return QStringLiteral("%1 cards: row %2 (implicit %6), column %7 (implicit %8), grid %9, card %10 (implicit %11); "
                                  "card ends at %3, row at %4, trend starts at %5")
                .arg(scene.app.statCards().size()).arg(row->height())
                .arg(bottom(current)).arg(bottom(row)).arg(top(trend)).arg(row->implicitHeight())
                .arg(column->height()).arg(column->implicitHeight()).arg(grid ? grid->height() : -1)
                .arg(current->height()).arg(current->implicitHeight());
        };
        QTRY_VERIFY2(bottom(current) <= bottom(row) + 0.5, qPrintable(where()));
        QVERIFY2(bottom(current) <= top(trend) + 0.5, qPrintable(where()));
    }
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

namespace {
QJsonObject regionalSnapshot(int cn, int tc)
{
    QJsonObject snapshot = ProgressBackend::snapshot(cn);
    QJsonObject cnEntry = ProgressBackend::snapshot(cn);
    cnEntry.insert(QStringLiteral("region"), QStringLiteral("CN"));
    QJsonObject tcEntry = ProgressBackend::snapshot(tc, 100, 0);
    tcEntry.insert(QStringLiteral("region"), QStringLiteral("TC"));
    snapshot.insert(QStringLiteral("achievements"), QJsonArray{cnEntry, tcEntry});
    return snapshot;
}
}

void AchievementProgressTests::regionalMilestonesDoNotReplayAcrossScopes()
{
    ProgressBackend backend;
    mr::StatisticsController stats(&backend);
    QSignalSpy reached(&stats, &mr::StatisticsController::regionalAchievementMilestoneReached);
    auto read = [&](int cn, int tc) {
        stats.refreshDashboard();
        backend.dashboards.takeLast()->succeed(regionalSnapshot(cn, tc));
    };
    read(1999, 99);
    read(1999, 100);
    QCOMPARE(reached.size(), 1);
    QCOMPARE(reached.last().at(0).toString(), QStringLiteral("TC"));
    stats.setRecordRegion(QStringLiteral("TC"));
    backend.dashboards.takeLast()->succeed(regionalSnapshot(1999, 200));
    QCOMPARE(reached.size(), 1); // Adopting an existing milestone after switching is a read.
    read(1999, 300);
    QCOMPARE(reached.size(), 2);
    stats.setRecordRegion(QString());
    backend.dashboards.takeLast()->succeed(regionalSnapshot(2000, 300));
    QCOMPARE(reached.size(), 2);
    read(4000, 300);
    QCOMPARE(reached.size(), 3);
    QCOMPARE(reached.last().at(0).toString(), QStringLiteral("CN"));
    stats.forgetAchievementSettings();
    read(6000, 500);
    QCOMPARE(reached.size(), 3);
}

void AchievementProgressTests::regionalBaselineEditsOwnTheirRegion()
{
    ProgressBackend backend;
    mr::StatisticsController stats(&backend);
    stats.refreshDashboard();
    backend.dashboards.takeLast()->succeed(regionalSnapshot(1500, 7));
    stats.setBaselineRegion(QStringLiteral("TC"));
    QCOMPARE(stats.goalCount(), 100);
    QCOMPARE(stats.baselineCount(), 0);
    QVERIFY(stats.achievementSettingsLoaded());
    stats.updateAchievementBaseline(200, 12, QStringLiteral("TC baseline"));
    QCOMPARE(backend.settingsPayload.value(QStringLiteral("region")).toString(), QStringLiteral("TC"));
    QCOMPARE(backend.settingsPayload.value(QStringLiteral("baseline_completed_count")).toInt(), 12);
    QCOMPARE(stats.achievementForRegion(QStringLiteral("CN")).value(QStringLiteral("achievement_progress")).toInt(), 1500);
}

void AchievementProgressTests::regionalDashboardRendersSeparateProgress()
{
    Scene scene;
    QVERIFY2(scene.create(true, QStringLiteral("classic"), false, true), qPrintable(scene.errors));
    QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
    scene.app.refreshDashboard();
    scene.backend.dashboards.takeLast()->succeed(regionalSnapshot(1500, 7));
    QTRY_VERIFY(scene.find("achievementRing") && scene.find("achievementRing_TC"));
    QCOMPARE(scene.find("achievementRing")->property("value").toInt(), 1500);
    QCOMPARE(scene.find("achievementRing_TC")->property("value").toInt(), 7);
    QCOMPARE(scene.find("achievementRing_TC")->property("maximum").toInt(), 100);
    const QString output = qEnvironmentVariable("MR_TC_UI_EVIDENCE_DIR");
    if (!output.isEmpty()) {
        QVERIFY(QDir().mkpath(output));
        QTest::qWait(150);
        QVERIFY(scene.window()->grabWindow().save(output + QStringLiteral("/regional-dashboard-classic-light.png")));
    }
    // Shrink and regrow the real Repeater while its outgoing delegates still
    // receive bindings; no removed row may become an undefined achievement.
    for (const QString &region : {QStringLiteral("TC"), QStringLiteral("CN"), QString()}) {
        scene.app.setRecordRegion(region);
        scene.backend.dashboards.takeLast()->succeed(regionalSnapshot(1500, 7));
        QTest::qWait(30);
        QVERIFY(scene.find(region == QLatin1String("TC") ? "achievementRing_TC" : "achievementRing"));
    }
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void AchievementProgressTests::regionalReadsProtectSettingsAndListingScope()
{
    ProgressBackend backend;
    mr::StatisticsController stats(&backend);
    stats.refreshDashboard();
    backend.dashboards.takeLast()->succeed(regionalSnapshot(1500, 7));
    stats.requestRegionalAchievement(QStringLiteral("TC"));
    const auto oldRead = backend.dashboards.takeLast();
    stats.setBaselineRegion(QStringLiteral("TC"));
    stats.updateAchievementBaseline(200, 12, QStringLiteral("TC baseline"));
    backend.settingsReply->succeed({{"region", "TC"}, {"goal_count", 200}, {"baseline_completed_count", 12}});
    QJsonObject fresh = ProgressBackend::snapshot(19, 200, 12);
    fresh.insert(QStringLiteral("region"), QStringLiteral("TC"));
    backend.dashboards.takeLast()->succeed({{"achievements", QJsonArray{fresh}}});
    oldRead->succeed(regionalSnapshot(1500, 7));
    QCOMPARE(stats.goalCount(), 200);
    QCOMPARE(stats.baselineCount(), 12);
    QCOMPARE(stats.achievementForRegion(QStringLiteral("TC")).value(QStringLiteral("achievement_progress")).toInt(), 19);
    QCOMPARE(stats.dashboardSnapshot().value(QStringLiteral("achievement_progress")).toInt(), 1500);
}

void AchievementProgressTests::failedScopeReadNeverRetainsAnotherRegionsCounts()
{
    ProgressBackend backend;
    mr::StatisticsController stats(&backend);
    stats.refreshDashboard();
    QJsonObject cn = regionalSnapshot(1500, 7);
    cn.insert(QStringLiteral("attempt_count"), 82);
    backend.dashboards.takeLast()->succeed(cn);
    stats.setRecordRegion(QStringLiteral("TC"));
    backend.dashboards.takeLast()->fail(QStringLiteral("ERR_INTERNAL"), QStringLiteral("read failed"));
    QVERIFY(stats.dashboardSnapshot().isEmpty());
    QCOMPARE(stats.completedLast7Days(), 0);
    QVERIFY(stats.trendBuckets().isEmpty());
    QCOMPARE(stats.achievements().size(), 1);
    QCOMPARE(stats.achievements().first().toMap().value(QStringLiteral("region")).toString(), QStringLiteral("TC"));
}

void AchievementProgressTests::regionalGoalReadUsesTheRunsRegion()
{
    ProgressBackend backend;
    mr::AppController app(&backend, nullptr);
    app.setRecordRegion(QStringLiteral("CN"));
    backend.dashboards.takeLast()->succeed(regionalSnapshot(1500, 7));
    QSignalSpy read(&app, &mr::AppController::goalProgressRead);
    app.readGoalProgress(QStringLiteral("tc-run"), QStringLiteral("TC"));
    QCOMPARE(backend.lastDashboardPayload.value(QStringLiteral("filter")).toObject()
        .value(QStringLiteral("region")).toString(), QStringLiteral("TC"));
    QJsonObject tc = ProgressBackend::snapshot(8, 100, 0);
    tc.insert(QStringLiteral("region"), QStringLiteral("TC"));
    backend.dashboards.takeLast()->succeed({{"achievements", QJsonArray{tc}}});
    QCOMPARE(read.size(), 1);
    QVERIFY(read.first().at(1).toBool());
    QCOMPARE(app.recordRegion(), QStringLiteral("CN"));
    QCOMPARE(app.dashboard().value(QStringLiteral("achievement_progress")).toInt(), 1500);
    QCOMPARE(app.achievementForRegion(QStringLiteral("TC")).value(QStringLiteral("achievement_progress")).toInt(), 8);
}

void AchievementProgressTests::targetedReadsRejectOlderDashboardMilestones()
{
    ProgressBackend backend;
    mr::StatisticsController stats(&backend);
    backend.read(stats, 1999);
    QSignalSpy reached(&stats, &mr::StatisticsController::regionalAchievementMilestoneReached);
    stats.refreshDashboard();
    const auto oldRead = backend.dashboards.takeLast();
    stats.requestRegionalAchievement(QStringLiteral("CN"));
    QJsonObject current = ProgressBackend::snapshot(1900);
    current.insert(QStringLiteral("region"), QStringLiteral("CN"));
    backend.dashboards.takeLast()->succeed({{"achievements", QJsonArray{current}}});
    oldRead->succeed(ProgressBackend::snapshot(2000));
    QCOMPARE(reached.size(), 0);
    QCOMPARE(stats.achievementForRegion(QStringLiteral("CN")).value(QStringLiteral("achievement_progress")).toInt(), 1900);
    QCOMPARE(stats.dashboardSnapshot().value(QStringLiteral("achievement_progress")).toInt(), 1900);
}

void AchievementProgressTests::unusedRegionsReadTheirOwnAuthoritativeDefaults()
{
    ProgressBackend backend;
    mr::StatisticsController stats(&backend);
    stats.refreshAchievementRegions();
    backend.dashboards.takeLast()->succeed(regionalSnapshot(0, 0));
    QCOMPARE(stats.allAchievements().size(), 2);
    QVERIFY(stats.achievementForRegion(QStringLiteral("UNKNOWN")).isEmpty());
    QCOMPARE(backend.dashboards.size(), 2);
    for (const QString &region : {QStringLiteral("GLOBAL"), QStringLiteral("UNKNOWN")}) {
        QJsonObject empty = ProgressBackend::snapshot(0);
        empty.insert(QStringLiteral("region"), region);
        backend.dashboards.takeFirst()->succeed({{"achievements", QJsonArray{empty}}});
    }
    QCOMPARE(stats.allAchievements().size(), 4);
    QCOMPARE(stats.achievementForRegion(QStringLiteral("UNKNOWN")).value(QStringLiteral("baseline_completed_count")).toInt(), 0);
    stats.forgetAchievementSettings();
    stats.refreshAchievementRegions();
    backend.dashboards.takeLast()->succeed(ProgressBackend::snapshot(0));
    for (const auto &reply : std::as_const(backend.dashboards)) reply->succeed(ProgressBackend::snapshot(0));
    QCOMPARE(stats.allAchievements().size(), 1); // An old scalar cannot authorize other regions.
}

// A disconnect forgets every region's settings; the reconnect's scope read brings
// back only the regions it covers. The 成就 editor's region is read once after
// that first read, and so are GLOBAL and UNKNOWN once the import dialog has asked
// for every region, so neither stays 「正在读取…」 for good (review I3-2 / K1-3).
void AchievementProgressTests::reconnectReadsEveryRegionTheScopeReadLeftOut()
{
    ProgressBackend backend;
    mr::StatisticsController stats(&backend);
    const auto regionOf = [&backend](const mr::BackendReply *reply) {
        return backend.dashboardPayloadOf.value(reply).value(QStringLiteral("filter")).toObject()
            .value(QStringLiteral("region")).toString();
    };
    const auto answer = [&regionOf](mr::BackendReply *reply) {
        const QString asked = regionOf(reply);
        QJsonArray entries;
        for (const QString &region : {QStringLiteral("CN"), QStringLiteral("TC"),
                                      QStringLiteral("GLOBAL"), QStringLiteral("UNKNOWN")}) {
            if (asked.isEmpty() ? region == QLatin1String("CN") || region == QLatin1String("TC") : region == asked) {
                const int figure = region == QLatin1String("TC") ? 70 : 640;
                QJsonObject entry = ProgressBackend::snapshot(figure, 2000, figure);
                entry.insert(QStringLiteral("region"), region);
                entries.append(entry);
            }
        }
        reply->succeed({{QStringLiteral("achievements"), entries}});
    };
    const auto drain = [&backend, &answer] {
        while (!backend.dashboards.isEmpty()) {
            const QPointer<mr::BackendReply> reply = backend.dashboards.takeFirst();
            if (reply && !reply->isFinished())
                answer(reply);
        }
    };
    // The pipe drops and comes back; the reconnect's scope read is for CN only.
    // Returns the regions asked for once that read has answered.
    const auto reconnect = [&] {
        stats.forgetAchievementSettings();
        stats.refreshDashboard();
        QStringList asked;
        if (backend.dashboards.size() != 1 || regionOf(backend.dashboards.first()) != QLatin1String("CN"))
            return QStringList{QStringLiteral("unexpected scope read")};
        answer(backend.dashboards.takeFirst());
        for (const auto &reply : std::as_const(backend.dashboards))
            asked.append(regionOf(reply));
        asked.sort();
        return asked;
    };
    stats.setRecordRegion(QStringLiteral("CN"));
    stats.setBaselineRegion(QStringLiteral("TC"));
    drain();
    QVERIFY(stats.achievementSettingsLoaded());

    QCOMPARE(reconnect(), QStringList{QStringLiteral("TC")});
    QVERIFY(!stats.achievementSettingsLoaded());
    drain();
    QVERIFY(stats.achievementSettingsLoaded());
    QCOMPARE(stats.baselineCount(), 70);

    // The import dialog asks for every region.
    stats.refreshAchievementRegions();
    drain();
    QCOMPARE(stats.allAchievements().size(), 4);
    QCOMPARE(reconnect(), (QStringList{QStringLiteral("GLOBAL"), QStringLiteral("TC"), QStringLiteral("UNKNOWN")}));
    drain();
    QVERIFY(stats.achievementSettingsLoaded());
    QCOMPARE(stats.allAchievements().size(), 4);
    // Once per connection: later reads ask for nothing more.
    stats.refreshDashboard();
    QCOMPARE(backend.dashboards.size(), 1);
    answer(backend.dashboards.takeFirst());
    QVERIFY(backend.dashboards.isEmpty());
}

void AchievementProgressTests::renderedEvidence()
{
    const QString output = qEnvironmentVariable("MR_ACHIEVEMENT_EVIDENCE_DIR");
    if (output.isEmpty())
        return;
    QVERIFY(QDir().mkpath(output));
    for (const auto &style : {QStringLiteral("classic"), QStringLiteral("eorzea"), QStringLiteral("harendotes")}) {
        for (const bool dark : {false, true}) {
            Scene scene;
            QVERIFY2(scene.create(false, style, dark), qPrintable(scene.errors));
            QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
            auto *ring = scene.find("ring");
            for (const int progress : {1980, 2000, 2200, 3000, 4000, 5000}) {
                ring->setProperty("value", progress);
                QTRY_VERIFY(qAbs(ring->property("shown").toDouble() - (static_cast<double>(progress) / 2000)) < 0.000001);
                QTest::qWait(35);
                const QImage image = scene.window()->grabWindow();
                QVERIFY(!image.isNull());
                QVERIFY(image.save(output + QStringLiteral("/%1-%2-%3.png")
                                   .arg(style, dark ? QStringLiteral("dark") : QStringLiteral("light"))
                                   .arg(progress)));
            }
            auto *confetti = scene.find("confetti");
            QVERIFY(QMetaObject::invokeMethod(confetti, "burst"));
            QTest::qWait(600);
            QVERIFY(confetti->property("running").toBool());
            QVERIFY(scene.window()->grabWindow().save(output + QStringLiteral("/%1-%2-confetti.png")
                        .arg(style, dark ? QStringLiteral("dark") : QStringLiteral("light"))));
            QTRY_VERIFY_WITH_TIMEOUT(!confetti->property("running").toBool(), 4000);
        }
    }
}

MR_DESKTOP_TEST_SUITE(AchievementProgress)
{
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication app(argc, argv);
    if (!mr::registerBundledFonts())
        return 2;
    AchievementProgressTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "AchievementProgressTests.moc"
