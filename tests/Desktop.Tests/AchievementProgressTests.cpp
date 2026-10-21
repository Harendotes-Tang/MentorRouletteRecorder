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
    mr::BackendReply *request(const QString &type, const QJsonObject & = {}) override
    {
        auto *reply = new mr::BackendReply(QString::number(++serial), type, this);
        if (type == QLatin1String("GetDashboardStats"))
            dashboards.append(reply);
        else if (type == QLatin1String("UpdateAchievementBaseline"))
            settingsReply = reply;
        else
            QTimer::singleShot(0, reply, [reply] { reply->succeed({}); });
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
    QObject *find(const char *name) const { return root->findChild<QObject *>(QLatin1String(name)); }
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
        while (clock.elapsed() < qint64(frame) * kCelebrationFrameMs)
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
    void renderedEvidence();
};

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
        const double ratio = double(count) / 2000;
        const double fraction = count > 0 && count % 2000 == 0 ? 1.0 : double(count % 2000) / 2000;
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
                QTRY_VERIFY(qAbs(ring->property("shown").toDouble() - double(progress) / 2000) < 0.000001);
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

int main(int argc, char **argv)
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
