#include "DesktopTestRunner.h"
#include "TestCollectorGuard.h"
#include "AppController.h"
#include "AppSettings.h"
#include "BundledFonts.h"
#include "DutyCatalog.h"
#include "Formatters.h"
#include "GameNames.h"
#include "JobCatalog.h"
#include "LanguageManager.h"
#include "MockBackend.h"
#include "RecoveryController.h"
#include "RoleCatalog.h"
#include "RunFormValidator.h"
#include "StatsModels.h"
#include "StatisticsOverlayController.h"
#include "TrayController.h"

#include <QAction>
#include <QApplication>
#include <QAnimationDriver>
#include <QColor>
#include <QDateTime>
#include <QDir>
#include <QElapsedTimer>
#include <QEventLoop>
#include <QFile>
#include <QFont>
#include <QGuiApplication>
#include <QImage>
#include <QJsonArray>
#include <QJsonObject>
#include <QJSValue>
#include <QMenu>
#include <QMutex>
#include <QMutexLocker>
#include <QPainter>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QPointer>
#include <QSettings>
#include <QScreen>
#include <QScopeGuard>
#include <QSignalSpy>
#include <QStandardPaths>
#include <QTemporaryDir>
#include <QTest>
#include <QTimer>
#include <QTimeZone>
#include <QVariantAnimation>
#include <QtTest/qtestwheel.h>
#include <QSystemTrayIcon>
#include <algorithm>
#include <bit>
#include <limits>
#include <memory>
#ifdef Q_OS_WIN
#include <qt_windows.h>
#endif

namespace {
// Deterministic image evidence advances the same Qt animation timeline without
// counting screenshot readback time as animation time. Native cadence is measured
// separately, with this driver absent and without frame captures.
class FormEvidenceClock final : public QAnimationDriver
{
public:
    FormEvidenceClock()
    {
        QObject::connect(&m_realTime, &QTimer::timeout, &m_realTime, [this] {
            const qint64 now = m_wall.elapsed();
            m_elapsed += now - m_wallSeen;
            m_wallSeen = now;
            advance();
        });
        install();
        // Keep the unified timer alive across the frozen interval and image
        // readbacks; otherwise its restart can fall back to wall-clock time.
        m_sentinel.setStartValue(0.0);
        m_sentinel.setEndValue(1.0);
        m_sentinel.setDuration(60000);
        m_sentinel.setLoopCount(-1);
        m_sentinel.start();
        followRealTime(true);
    }
    ~FormEvidenceClock() override { m_sentinel.stop(); uninstall(); }
    qint64 elapsed() const override { return m_elapsed; }
    void step(int milliseconds)
    {
        for (int remaining = milliseconds; remaining > 0;) {
            const int delta = std::min(4, remaining);
            m_elapsed += delta;
            advance();
            QCoreApplication::processEvents(QEventLoop::AllEvents, 5);
            remaining -= delta;
        }
    }
    void followRealTime(bool follow)
    {
        if (!follow) { m_realTime.stop(); return; }
        m_wall.start();
        m_wallSeen = 0;
        m_realTime.start(8);
    }
protected:
    void start() override { m_elapsed = 0; QAnimationDriver::start(); }
private:
    qint64 m_elapsed = 0;
    QTimer m_realTime;
    QElapsedTimer m_wall;
    qint64 m_wallSeen = 0;
    QVariantAnimation m_sentinel;
};
FormEvidenceClock *formEvidenceClock = nullptr;

const QString &kEvidence()
{
    static const QString value = QStringLiteral("/artifacts/statistics-float-20261020");
    return value;
}

const QString &kAttemptEvidence()
{
    static const QString value = QStringLiteral("/artifacts/overlay-attempt-periods-20261021");
    return value;
}

QVariantList qmlList(const QVariant &value)
{
    return value.metaType() == QMetaType::fromType<QJSValue>()
        ? value.value<QJSValue>().toVariant().toList() : value.toList();
}

// Hold only dashboard reads explicitly armed after startup, so the actual
// window can exercise pending/failure/retry/zero without mocking its bindings.
class OverlayReplyBackend : public mr::MockBackend
{
public:
    bool holdDashboard = false;
    bool holdRows = false;
    QList<QPointer<mr::BackendReply>> pending;
    QList<QPointer<mr::BackendReply>> pendingRows;

    mr::BackendReply *request(const QString &messageType, const QJsonObject &payload = {}) override
    {
        if (holdRows && (messageType == QLatin1String("GetDungeonStats") || messageType == QLatin1String("GetJobStats"))) {
            auto *reply = new mr::BackendReply(QString::number(pendingRows.size()), messageType, this);
            pendingRows.append(reply);
            return reply;
        }
        if (!holdDashboard || messageType != QLatin1String("GetDashboardStats"))
            return mr::MockBackend::request(messageType, payload);
        auto *reply = new mr::BackendReply(QString::number(pending.size()), messageType, this);
        pending.append(reply);
        return reply;
    }
};

QVariantMap unknownResultRun(int sequence = 1)
{
    const QString now = QDateTime::currentDateTimeUtc().addSecs(1).toString(Qt::ISODateWithMs);
    const QString earlier = QDateTime::currentDateTimeUtc().addSecs(-1200).toString(Qt::ISODateWithMs);
    return {{QStringLiteral("run_id"), QStringLiteral("5f0cc3bd-3333-4444-8555-%1").arg(sequence, 12, 10, QLatin1Char('0'))},
            {QStringLiteral("revision"), 1}, {QStringLiteral("source"), QStringLiteral("AUTO_NETWORK")},
            {QStringLiteral("state"), QStringLiteral("UNKNOWN_FINAL_STATE")}, {QStringLiteral("result"), QStringLiteral("UNKNOWN")},
            {QStringLiteral("mentor_roulette_id"), 9}, {QStringLiteral("mentor_confirmed"), true}, {QStringLiteral("is_mentor"), true},
            {QStringLiteral("duty_name"), QStringLiteral("天狼星灯塔")}, {QStringLiteral("duty_category"), QStringLiteral("四人迷宫")},
            {QStringLiteral("job_id"), 24}, {QStringLiteral("job_name"), QStringLiteral("白魔法师")},
            {QStringLiteral("created_at_utc"), earlier}, {QStringLiteral("matched_at_utc"), earlier},
            {QStringLiteral("entered_at_utc"), earlier}, {QStringLiteral("ended_at_utc"), now},
            {QStringLiteral("duration_ms"), 965000}, {QStringLiteral("pending_review"), true},
            {QStringLiteral("soft_deleted"), false}};
}

/// A cleared run that only waits for a note: its result is known.
QVariantMap completedRun(int sequence = 1)
{
    QVariantMap run = unknownResultRun(sequence);
    run.insert(QStringLiteral("state"), QStringLiteral("COMPLETED"));
    run.insert(QStringLiteral("result"), QStringLiteral("COMPLETED"));
    run.insert(QStringLiteral("pending_review"), false);
    return run;
}

/// Four entered, ended attempts spanning the selector's four calendar ranges.
QJsonArray datedAttemptRuns()
{
    QJsonArray runs;
    int sequence = 1;
    const QStringList categories{QStringLiteral("四人迷宫"), QStringLiteral("讨伐歼灭战"),
                                 QStringLiteral("大型任务"), QStringLiteral("行会令")};
    const QList<int> jobs{19, 24, 20, 23};
    for (const int daysAgo : {0, 3, 14, 45}) {
        QVariantMap run = completedRun(sequence++);
        run.insert(QStringLiteral("duty_name"), QStringLiteral("时段验证副本 %1").arg(sequence - 1));
        run.insert(QStringLiteral("duty_category"), categories.at(sequence - 2));
        run.insert(QStringLiteral("job_id"), jobs.at(sequence - 2));
        // Statistics group by observed identity; free-text names alone are one unknown duty.
        for (const auto &entry : mr::DutyCatalog::shared()->allDuties()) {
            const QVariantMap duty = entry.toMap();
            if (duty.value(QStringLiteral("duty_category")).toString() == categories.at(sequence - 2)) {
                run.insert(QStringLiteral("content_id"), duty.value(QStringLiteral("content_id")));
                break;
            }
        }
        const QString timestamp = QDateTime(QDate::currentDate().addDays(-daysAgo), QTime(12, 0),
                                            QTimeZone::systemTimeZone()).toUTC().toString(Qt::ISODateWithMs);
        for (const auto &field : {QStringLiteral("created_at_utc"), QStringLiteral("matched_at_utc"),
                                  QStringLiteral("entered_at_utc"), QStringLiteral("ended_at_utc")})
            run.insert(field, timestamp);
        runs.append(QJsonObject::fromVariantMap(run));
    }
    return runs;
}

struct Scene {
    QTemporaryDir directory{QString::fromUtf8(MR_SOURCE_DIR) + kEvidence() + QStringLiteral("/test-settings-XXXXXX")};
    mr::AppSettings settings{directory.filePath(QStringLiteral("desktop.ini")), nullptr};
    mr::LanguageManager language{&settings};
    OverlayReplyBackend backend;
    std::unique_ptr<mr::AppController> app;
    mr::Formatters formatters;
    mr::JobCatalog jobs;
    mr::RoleCatalog roles;
    mr::RunFormValidator runForm;
    QQmlEngine engine;
    std::unique_ptr<QObject> root;
    QString errors;

    bool create(const QString &style = QStringLiteral("harendotes"), bool dark = true,
                bool mainWindow = false, bool reduceMotion = true)
    {
        language.initialize(QStringLiteral("zh-CN"));
        language.attachEngine(&engine);
        app = std::make_unique<mr::AppController>(&backend, &settings);
        app->setThemeMode(dark ? QStringLiteral("dark") : QStringLiteral("light"));
        auto *context = engine.rootContext();
        context->setContextProperty(QStringLiteral("App"), app.get());
        context->setContextProperty(QStringLiteral("Fmt"), &formatters);
        context->setContextProperty(QStringLiteral("Names"), mr::GameNames::shared());
        context->setContextProperty(QStringLiteral("Jobs"), &jobs);
        context->setContextProperty(QStringLiteral("Roles"), &roles);
        context->setContextProperty(QStringLiteral("RunForm"), &runForm);
        context->setContextProperty(QStringLiteral("Settings"), &settings);
        context->setContextProperty(QStringLiteral("Lang"), &language);
        context->setContextProperty(QStringLiteral("SuppressOnboarding"), true);
        context->setContextProperty(QStringLiteral("ReduceMotion"), reduceMotion);
        context->setContextProperty(QStringLiteral("ForceUiStyle"), style);
        QObject::connect(&engine, &QQmlEngine::warnings, &engine, [this](const QList<QQmlError> &warnings) {
            for (const auto &warning : warnings) errors += warning.toString() + QLatin1Char('\n');
        });
        QQmlComponent component(&engine);
        if (mainWindow)
            component.loadUrl(QUrl::fromLocalFile(QString::fromUtf8(MR_DESKTOP_QML_DIR) + QStringLiteral("/Main.qml")));
        else
            component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 1100; height: 760; visible: true
    StatisticsFloatingWindow { }
})", QUrl());
        root.reset(component.create());
        for (const auto &error : component.errors()) errors += error.toString() + QLatin1Char('\n');
        return root != nullptr;
    }
    QQuickWindow *mainWindow() const { return qobject_cast<QQuickWindow *>(root.get()); }
    QQuickWindow *floating() const { return root->findChild<QQuickWindow *>(QStringLiteral("statisticsFloatingWindow")); }
    mr::StatisticsOverlayController *overlay() const { return app->statisticsOverlay(); }
    QQuickItem *item(const QString &name, bool requireVisible = false) const
    {
        // Repeater and Popup items may be visually parented outside their QObject subtree.
        const auto find = [&](auto &&self, QQuickItem *parent) -> QQuickItem * {
            if (parent->objectName() == name && (!requireVisible || parent->isVisible())) return parent;
            for (auto *child : parent->childItems())
                if (auto *match = self(self, child)) return match;
            return nullptr;
        };
        if (auto *match = find(find, floating()->contentItem())) return match;
        return find(find, mainWindow()->contentItem());
    }
    bool click(const QString &name) const
    {
        QQuickItem *target = nullptr;
        QPoint point;
        // Module changes rebuild the Repeater before its new rows are laid out.
        // Polish their visual ancestry before mapping the actual mouse target.
        if (!QTest::qWaitFor([&] {
                target = item(name, true);
                if (!target || !target->isEnabled() || !target->window()) return false;
                QList<QQuickItem *> ancestors;
                for (auto *parent = target; parent; parent = parent->parentItem()) ancestors.prepend(parent);
                for (auto *parent : ancestors) parent->ensurePolished();
                if (target->width() <= 0 || target->height() <= 0) return false;
                point = target->mapToScene(QPointF(target->width() / 2, target->height() / 2)).toPoint();
                return QRect(QPoint(), target->window()->size()).contains(point);
            }, 1500)) return false;
        QTest::mouseClick(target->window(), Qt::LeftButton, Qt::NoModifier, point);
        return true;
    }
    /// Reach the customization footer through the same wheel input a user sends.
    bool revealCustomizationFooter() const
    {
        auto *panel = item(QStringLiteral("floatingCustomizePanel"), true);
        auto *done = item(QStringLiteral("floatingCustomizationDone"), true);
        if (!panel || !done) return false;
        QList<QQuickItem *> ancestors;
        for (auto *parent = done; parent; parent = parent->parentItem()) ancestors.prepend(parent);
        for (auto *parent : ancestors) parent->ensurePolished();
        const QRectF viewport(panel->mapToScene(QPointF()), panel->size());
        for (int step = 0; step < 12; ++step) {
            const QRectF footer(done->mapToScene(QPointF()), done->size());
            if (viewport.contains(footer)) {
                const auto stationary = [&] {
                    return std::ranges::none_of(ancestors, [](QQuickItem *parent) {
                        return parent->property("moving").toBool() || parent->property("flicking").toBool();
                    });
                };
                if (!QTest::qWaitFor(stationary, 1500)) return false;
                return viewport.contains(QRectF(done->mapToScene(QPointF()), done->size()));
            }
            QTest::wheelEvent(floating(), panel->mapToScene(QPointF(panel->width() / 3, panel->height() / 2)), QPoint(0, -120));
            QTest::qWait(30);
        }
        return false;
    }
    QStringList texts() const
    {
        QStringList result;
        const auto collect = [&](auto &&self, QQuickItem *parent) -> void {
            const QString text = parent->property("text").toString();
            if (!text.isEmpty() && parent->isVisible()) result.append(text);
            for (auto *child : parent->childItems()) self(self, child);
        };
        collect(collect, floating()->contentItem());
        return result;
    }
    /// The float's endless animations (the 进行中 / 已匹配 pulse, the wait stripes).
    QList<QObject *> endlessAnimations() const
    {
        QList<QObject *> result;
        const auto collect = [&](auto &&self, QQuickItem *parent) -> void {
            for (QObject *child : parent->children())
                if (child->inherits("QQuickAbstractAnimation") && child->property("loops").toInt() < 0)
                    result.append(child);
            for (auto *item : parent->childItems()) self(self, item);
        };
        collect(collect, floating()->contentItem());
        return result;
    }
    int runningEndlessAnimations() const
    {
        int running = 0;
        for (QObject *animation : endlessAnimations())
            running += animation->property("running").toBool() ? 1 : 0;
        return running;
    }
    /// The tooltips the float's items carry, opened or not, whose text contains \a part.
    QList<QObject *> tipsContaining(const QString &part) const
    {
        QList<QObject *> result;
        const auto collect = [&](auto &&self, QQuickItem *parent) -> void {
            for (QObject *child : parent->children())
                if (child->inherits("QQuickToolTip") && child->property("text").toString().contains(part))
                    result.append(child);
            for (auto *item : parent->childItems()) self(self, item);
        };
        collect(collect, floating()->contentItem());
        return result;
    }
    /// The visible text items whose text contains \a part.
    QList<QQuickItem *> textItemsContaining(const QString &part) const
    {
        QList<QQuickItem *> result;
        const auto collect = [&](auto &&self, QQuickItem *parent) -> void {
            if (parent->inherits("QQuickText") && parent->isVisible() && parent->property("text").toString().contains(part))
                result.append(parent);
            for (auto *item : parent->childItems()) self(self, item);
        };
        collect(collect, floating()->contentItem());
        return result;
    }
    /// The float's OverlayData: what its modules show.
    QObject *overlayData() const
    {
        for (QObject *object : root->findChildren<QObject *>())
            if (QByteArray(object->metaObject()->className()).startsWith("OverlayData"))
                return object;
        return nullptr;
    }
    bool grab(const QString &name) const
    {
        QTest::qWait(80);
        return floating()->grabWindow().save(QString::fromUtf8(MR_SOURCE_DIR) + kEvidence() + QLatin1Char('/') + name + QStringLiteral(".png"));
    }
    bool grabAttempts(const QString &name) const
    {
        QTest::qWait(80);
        return floating()->grabWindow().save(QString::fromUtf8(MR_SOURCE_DIR) + kAttemptEvidence()
                                             + QLatin1Char('/') + name + QStringLiteral(".png"));
    }
};
}

namespace {
class StatisticsOverlayTests : public QObject
{
    Q_OBJECT
private:
    static void verifyInterruptedFormTransitions(const Scene &scene, const QSize &fullSize)
    {
        scene.overlay()->setForm(QStringLiteral("mini"));
        QTest::qWait(35);
        QVERIFY(scene.floating()->property("transitioning").toBool());
        scene.overlay()->setForm(QStringLiteral("full"));
        QTRY_VERIFY(!scene.floating()->property("transitioning").toBool());
        QCOMPARE(scene.floating()->size(), fullSize);
        QCOMPARE(scene.overlay()->geometry().size(), fullSize);

        // At the bottom edge, expansion must move upwards with the same height
        // transition and keep the original full size inside the available screen.
        scene.overlay()->setForm(QStringLiteral("mini"));
        QTRY_VERIFY(!scene.floating()->property("transitioning").toBool());
        const QRect available = scene.floating()->screen()->availableGeometry();
        scene.floating()->setPosition(available.x() + 100,
                                      available.y() + available.height() - scene.floating()->height());
        QTRY_COMPARE(scene.overlay()->geometry().topLeft(), scene.floating()->position());
        scene.overlay()->setForm(QStringLiteral("full"));
        QVERIFY(scene.floating()->property("transitioning").toBool());
        QTRY_VERIFY(!scene.floating()->property("transitioning").toBool());
        QCOMPARE(scene.floating()->size(), fullSize);
        QVERIFY(available.contains(scene.floating()->geometry()));

        scene.overlay()->setForm(QStringLiteral("mini"));
        QTest::qWait(35);
        QVERIFY(scene.floating()->property("transitioning").toBool());
        scene.overlay()->hide();
        QVERIFY(!scene.floating()->property("transitioning").toBool());
        QCOMPARE(scene.overlay()->geometry().size(), fullSize);
        scene.overlay()->show();
        QTRY_VERIFY(scene.floating()->property("positionReady").toBool());
        QVERIFY(scene.floating()->height() < 80);
        QCOMPARE(scene.floating()->width(), 340);
        scene.overlay()->setForm(QStringLiteral("full"));
        QVERIFY(scene.floating()->property("transitioning").toBool());
        scene.engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QTRY_VERIFY(!scene.floating()->property("transitioning").toBool());
        QCOMPARE(scene.floating()->size(), fullSize);
        QCOMPARE(scene.overlay()->geometry().size(), fullSize);
    }

    static void verifyAttemptPeriodStatistics(const Scene &scene, int count)
    {
        QTRY_COMPARE(scene.overlay()->dungeons()->totalAttemptCount(), count);
        QTRY_COMPARE(scene.overlay()->dungeons()->totalCompletedCount(), count);
        QTRY_COMPARE(scene.overlay()->jobs()->totalAttemptCount(), count);
        QTRY_COMPARE(scene.item(QStringLiteral("floatingDungeonState"))->property("text").toString(),
                     QStringLiteral("%1/%1").arg(count));
        QTRY_VERIFY(!qmlList(scene.item(QStringLiteral("floatingDungeonStatistics"))->property("rows")).isEmpty());
        QTRY_VERIFY(!qmlList(scene.item(QStringLiteral("floatingJobStatistics"))->property("columns")).isEmpty());
        const QVariantList buckets = qmlList(scene.item(QStringLiteral("floatingDungeonStatistics"))->property("buckets"));
        const QVariantList columns = qmlList(scene.item(QStringLiteral("floatingJobStatistics"))->property("columns"));
        int clears = 0;
        int attempts = 0;
        for (const auto &bucket : buckets) clears += bucket.toMap().value(QStringLiteral("completed_count")).toInt();
        for (const auto &column : columns) attempts += column.toMap().value(QStringLiteral("count")).toInt();
        QCOMPARE(clears, count);
        QCOMPARE(attempts, count);
        QCOMPARE(qmlList(scene.item(QStringLiteral("floatingDungeonStatistics"))->property("rows")).size(), qMin(3, count));
    }

    static void verifyAttemptPeriodKeyboard(const Scene &scene)
    {
        scene.floating()->requestActivate();
        QVERIFY(QTest::qWaitForWindowActive(scene.floating()));
        auto *all = scene.item(QStringLiteral("floatingAttemptPeriod_all"), true);
        QVERIFY(all);
        all->forceActiveFocus(Qt::TabFocusReason);
        QTest::keyClick(scene.floating(), Qt::Key_Left);
        QTRY_COMPARE(scene.overlay()->attemptPeriod(), QStringLiteral("last30"));
        QTRY_VERIFY(scene.item(QStringLiteral("floatingAttemptPeriod_last30"))->hasActiveFocus());
        QTest::keyClick(scene.floating(), Qt::Key_Right);
        QTRY_COMPARE(scene.overlay()->attemptPeriod(), QStringLiteral("all"));
        auto *today = scene.item(QStringLiteral("floatingAttemptPeriod_today"));
        QVERIFY(today);
        today->forceActiveFocus(Qt::TabFocusReason);
        QTest::keyClick(scene.floating(), Qt::Key_Space);
        QTRY_COMPARE(scene.overlay()->attemptPeriod(), QStringLiteral("today"));
        QTest::keyClick(scene.floating(), Qt::Key_Space);
        QVERIFY(today->property("checked").toBool());
    }

    static void customizeOverlayModules(const Scene &scene)
    {
        QVERIFY(scene.click(QStringLiteral("floatingCustomizeButton")));
        QTRY_VERIFY(scene.item(QStringLiteral("floatingCustomizePanel"), true));
        QVERIFY(!scene.item(QStringLiteral("floatingStatisticsScroll"), true));
        QVERIFY(scene.click(QStringLiteral("floatingModuleToggle_current_duty")));
        QTRY_VERIFY(!scene.overlay()->modules().contains(QStringLiteral("current_duty")));
        QVERIFY(scene.click(QStringLiteral("floatingModuleToggle_current_duty")));
        QTRY_COMPARE(scene.overlay()->modules().indexOf(QStringLiteral("current_duty")), 2);
        QVERIFY(scene.click(QStringLiteral("floatingMoveUp_current_duty")));
        QTRY_COMPARE(scene.overlay()->modules().indexOf(QStringLiteral("current_duty")), 1);
        scene.overlay()->setModules({});
        QTRY_VERIFY(!scene.item(QStringLiteral("floatingModule_progress")));
        QTest::qWait(50);
        QVERIFY(scene.click(QStringLiteral("floatingModuleToggle_jobs")));
        QTRY_COMPARE(scene.overlay()->modules(), QStringList({"jobs"}));
        // 副本排行: choosing 前 5 changes the ranking (audit 2026-10-20, L3-6 / I2-5).
        auto *rowLimit = scene.item(QStringLiteral("floatingRowLimit"), true);
        if (!rowLimit) {
            QTest::qFail("floatingRowLimit was not found", __FILE__, __LINE__);
            return;
        }
        QQuickItem *topFive = nullptr;
        const auto findOption = [&](auto &&self, QQuickItem *parent) -> void {
            if (!topFive && parent->inherits("QQuickAbstractButton") && parent->property("text").toString() == QString::fromUtf8("前 5"))
                topFive = parent;
            for (auto *child : parent->childItems()) self(self, child);
        };
        findOption(findOption, rowLimit);
        if (!topFive) {
            QTest::qFail("The top-five row limit option was not found", __FILE__, __LINE__);
            return;
        }
        QVERIFY(topFive->isVisible());
        QCOMPARE(scene.overlay()->rowLimit(), 3);
        QTest::mouseClick(topFive->window(), Qt::LeftButton, Qt::NoModifier,
                          topFive->mapToScene(QPointF(topFive->width() / 2, topFive->height() / 2)).toPoint());
        QTRY_COMPARE(scene.overlay()->rowLimit(), 5);
        QVERIFY(scene.revealCustomizationFooter());
        QVERIFY(scene.click(QStringLiteral("floatingCustomizationDone")));
        QTRY_VERIFY(!scene.item(QStringLiteral("floatingCustomizePanel"), true));
        QVERIFY(scene.item(QStringLiteral("floatingModule_jobs"), true));
    }

    static void moveMiniBarNearScreenEdge(Scene &scene)
    {
        // Near the bottom edge the bar's own size decides what fits, not the full form's.
        const QRect available = scene.floating()->screen()->availableGeometry();
        const int lowY = available.bottom() - scene.floating()->height() - 10;
        scene.floating()->setPosition(160, lowY);
        QTRY_COMPARE(scene.overlay()->geometry().y(), lowY);
        QCOMPARE(scene.overlay()->geometry().size(), QSize(340, 520));
        QVERIFY(scene.click(QStringLiteral("floatingExpandButton")));
        QTRY_COMPARE(scene.overlay()->form(), QStringLiteral("full"));
        QTRY_COMPARE(scene.floating()->size(), QSize(340, 520));
        QTRY_VERIFY(scene.floating()->y() + scene.floating()->height() <= available.bottom() + 1);
        QVERIFY(scene.click(QStringLiteral("floatingMiniButton")));
        QTRY_COMPARE(scene.overlay()->form(), QStringLiteral("mini"));
        scene.floating()->setPosition(160, 140);
        QTRY_COMPARE(scene.overlay()->geometry().topLeft(), QPoint(160, 140));
    }

    static QObject *visibleTip(Scene &scene, const QString &name)
    {
        for (auto *candidate : scene.root->findChildren<QObject *>(name)) {
            auto *owner = candidate->property("parent").value<QQuickItem *>();
            if (owner && owner->isVisible()) return candidate;
        }
        return nullptr;
    }

    static QPointF centreOf(QQuickItem *item)
    {
        return item->mapToScene(QPointF(item->width() / 2, item->height() / 2));
    }

    static void verifyFullOverlayTips(Scene &scene, QObject *&headerLockTip)
    {
        // The full form's title bar has the same shape: the drag tip of the bar
        // underneath stays closed while a button's tip is up.
        auto *headerTip = visibleTip(scene, QStringLiteral("floatingHeaderTip"));
        QVERIFY(headerTip);
        auto *headerLock = scene.item(QStringLiteral("floatingLockButton"), true);
        QVERIFY(headerLock);
        headerLockTip = visibleTip(scene, QStringLiteral("floatingLockButtonTip"));
        QVERIFY(headerLockTip);
        QTest::mouseMove(scene.floating(), centreOf(headerLock).toPoint());
        QTRY_VERIFY(headerLockTip->property("opened").toBool());
        QVERIFY(!headerTip->property("visible").toBool());
        QTest::mouseMove(scene.floating(), QPoint(40, 15));
        QTRY_VERIFY(headerTip->property("opened").toBool());
        QTRY_VERIFY(!headerLockTip->property("visible").toBool());
        QTest::mouseMove(scene.floating(), QPoint(150, 300));
        QTRY_VERIFY(!headerTip->property("visible").toBool());
    }

    static void verifyMiniTipAtScreenTop(Scene &scene, QObject *lockTip, QQuickWindow *tipWindow, const QPointF &lockCentre)
    {
        // Docked at the top of the screen there is no room above the bar: the tip
        // moves below it instead of lying on it, and stays on the screen.
        QTest::mouseMove(scene.floating(), QPoint(150, 300));
        QTRY_VERIFY(!lockTip->property("visible").toBool());
        const QRect screen = scene.floating()->screen()->availableGeometry();
        scene.floating()->setPosition(screen.x() + 100, screen.y());
        QTRY_COMPARE(scene.floating()->y(), screen.y());
        QTest::mouseMove(scene.floating(), lockCentre.toPoint());
        QTRY_VERIFY(lockTip->property("opened").toBool());
        {
            const QRect topBar = scene.floating()->geometry();
            const QRect topTip = tipWindow->geometry();
            QVERIFY2(!topTip.intersects(topBar), qPrintable(QStringLiteral("tip %1,%2 %3x%4 overlaps bar %5,%6 %7x%8 at the screen top")
                .arg(topTip.x()).arg(topTip.y()).arg(topTip.width()).arg(topTip.height())
                .arg(topBar.x()).arg(topBar.y()).arg(topBar.width()).arg(topBar.height())));
            QVERIFY2(topTip.top() >= screen.top(), qPrintable(QStringLiteral("tip top %1 above screen top %2").arg(topTip.top()).arg(screen.top())));
            QVERIFY(tipWindow->grabWindow().save(QString::fromUtf8(MR_SOURCE_DIR) + kEvidence() + QStringLiteral("/mini-lock-tip-screen-top.png")));
        }
        QTest::mouseMove(scene.floating(), QPoint(150, 300));
        QTRY_VERIFY(!lockTip->property("visible").toBool());
        scene.floating()->setPosition(200, 300);
        QTRY_COMPARE(scene.floating()->y(), 300);
    }

private Q_SLOTS:
    void initTestCase();
    void preferencesPersistButVisibilityAndLockDoNot();
    void moduleOrderSurvivesSwitchingOff();
    void invalidPreferencesAreBounded();
    void geometryHandlesMissingAndNegativeScreens();
    void nativeCloseSavesGeometryBeforeTheDebounce() const;
    void realWindowCanCustomizeAndRecoverEmptyContent();
    void customizePanelTogglesFromTheTitleBar_data();
    void customizePanelTogglesFromTheTitleBar();
    void customizationFooterRemainsReachableWithSevenModules();
    void miniBarSharesThePositionAndKeepsTheSize();
    void formControlsLeadRightActionGroup_data();
    void formControlsLeadRightActionGroup();
    void formTransitionKeepsStableGeometry() const;
    void miniBarStaysWhereItWasDocked();
    void miniBarTipsOpenOutsideTheBar();
    void pendingRowOpensTheFirstRecord();
    void hiddenFloatKeepsItsEndlessAnimationsStill();
    void paceBarScalesByTheDutyThenItsCategory_data();
    void paceBarScalesByTheDutyThenItsCategory();
    void stateMarksStayInsideTheFloat();
    void runRowsShowTheCategoryIcon();
    void miniBarDrawsNothingBelowTheRunRow();
    void importedTextIsShownAsWritten();
    void plusPillCountsCompletionsNotBaselineEdits();
    void entryButtonFollowsPageTheme_data();
    void entryButtonFollowsPageTheme();
    void mainWindowCanUnlockAndMinimizeIndependently();
    void sharedSnapshotsRefreshWithoutOpeningTheMainPage();
    void attemptCountRefreshesWithDashboardChanges();
    void attemptPeriodsUsePointerKeyboardAndKeepOtherStatistics();
    void attemptCountDistinguishesLoadingFailureAndZero();
    void periodStatisticsHideOldChartsWhileLoadingAndOnFailure();
    void attemptRangeRenderedEvidence_data();
    void attemptRangeRenderedEvidence();
    void nativeInputStylesAndTrayRecovery();
    void trayRestoresWithoutLosingMaximizedState();
    void renderedEvidence_data();
    void renderedEvidence();
};
} // namespace

void StatisticsOverlayTests::initTestCase()
{
    QVERIFY(QDir().mkpath(QString::fromUtf8(MR_SOURCE_DIR) + kEvidence()));
    QVERIFY(QDir().mkpath(QString::fromUtf8(MR_SOURCE_DIR) + kAttemptEvidence()));
    const QString base = QString::fromUtf8(MR_DESKTOP_QML_DIR);
    qmlRegisterSingletonType(QUrl::fromLocalFile(base + QStringLiteral("/Theme.qml")), "MentorRecorder", 1, 0, "Theme");
    for (const auto &directory : {QStringLiteral("/components"), QStringLiteral("/pages"),
                                  QStringLiteral("/dialogs"), QStringLiteral("/charts")}) {
        for (const auto &file : QDir(base + directory).entryList({QStringLiteral("*.qml")}, QDir::Files)) {
            const QByteArray name = file.chopped(4).toUtf8();
            qmlRegisterType(QUrl::fromLocalFile(base + directory + QLatin1Char('/') + file),
                            "MentorRecorder", 1, 0, name.constData());
        }
    }
}

void StatisticsOverlayTests::preferencesPersistButVisibilityAndLockDoNot()
{
    QTemporaryDir directory(QString::fromUtf8(MR_SOURCE_DIR) + kEvidence() + QStringLiteral("/preferences-XXXXXX"));
    const QString path = directory.filePath(QStringLiteral("ui.ini"));
    const QStringList everything{"attempts", "progress", "current_duty", "elapsed", "dungeons", "jobs", "pending_work"};
    {
        mr::AppSettings settings(path, nullptr);
        mr::StatisticsOverlayController overlay(&settings);
        QVERIFY(!overlay.visible());
        QCOMPARE(overlay.modules(), everything);
        QCOMPARE(overlay.form(), QStringLiteral("full"));
        QCOMPARE(overlay.attemptPeriod(), QStringLiteral("all"));
        overlay.setAttemptPeriod(QStringLiteral("last7"));
        QVERIFY(!overlay.labels());
        QCOMPARE(overlay.rowLimit(), 3);
        QCOMPARE(overlay.opacity(), 0.85);
        overlay.setGeometry(QRect(200, 100, 420, 650));
        overlay.setOpacity(0.65);
        overlay.setModules({"jobs", "elapsed", "jobs", "invalid", "current_duty"});
        overlay.moveModule(QStringLiteral("current_duty"), -1);
        overlay.setRowLimit(10);
        overlay.setForm(QStringLiteral("mini"));
        overlay.setLabels(true);
        overlay.show();
        overlay.setLocked(true);
        QVERIFY(overlay.visible() && overlay.locked());
        overlay.hide();
        overlay.show();
        QVERIFY(!overlay.locked());
        overlay.setLocked(true);
    }
    mr::AppSettings restored(path, nullptr);
    mr::StatisticsOverlayController overlay(&restored);
    QVERIFY(!overlay.visible() && !overlay.locked());
    QCOMPARE(overlay.geometry().size(), QSize(420, 650));
    QCOMPARE(overlay.opacity(), 0.65);
    QCOMPARE(overlay.modules(), QStringList({"jobs", "current_duty", "elapsed"}));
    QCOMPARE(overlay.rowLimit(), 10);
    QCOMPARE(overlay.form(), QStringLiteral("mini"));
    QCOMPARE(overlay.attemptPeriod(), QStringLiteral("last7"));
    QVERIFY(overlay.labels());
    overlay.reset();
    QCOMPARE(overlay.modules(), everything);
    QCOMPARE(overlay.rowLimit(), 3);
    QCOMPARE(overlay.opacity(), 0.85);
    QCOMPARE(overlay.attemptPeriod(), QStringLiteral("all"));
    QVERIFY(!overlay.labels());
    QCOMPARE(overlay.form(), QStringLiteral("mini")); // 恢复默认 leaves the form alone.
    overlay.setModules({});
    mr::StatisticsOverlayController empty(&restored);
    QVERIFY(empty.modules().isEmpty());
    QCOMPARE(empty.moduleRows().size(), 7);
}

void StatisticsOverlayTests::moduleOrderSurvivesSwitchingOff()
{
    QTemporaryDir directory(QString::fromUtf8(MR_SOURCE_DIR) + kEvidence() + QStringLiteral("/order-XXXXXX"));
    const QString path = directory.filePath(QStringLiteral("ui.ini"));
    {
        mr::AppSettings settings(path, nullptr);
        mr::StatisticsOverlayController overlay(&settings);
        // Off keeps the place; on again returns there, not to the end.
        overlay.toggleModule(QStringLiteral("current_duty"));
        QCOMPARE(overlay.modules(), QStringList({"attempts", "progress", "elapsed", "dungeons", "jobs", "pending_work"}));
        QCOMPARE(overlay.moduleRows().at(2).toMap().value(QStringLiteral("key")).toString(), QStringLiteral("current_duty"));
        QVERIFY(!overlay.moduleRows().at(2).toMap().value(QStringLiteral("on")).toBool());
        QCOMPARE(overlay.moduleRows().at(2).toMap().value(QStringLiteral("hint")).toString(), QString::fromUtf8("进本后显示"));
        overlay.toggleModule(QStringLiteral("current_duty"));
        QCOMPARE(overlay.modules(), QStringList({"attempts", "progress", "current_duty", "elapsed", "dungeons", "jobs", "pending_work"}));
        // Drag: the row lands where it was dropped, counted in the full order.
        overlay.moveModuleTo(QStringLiteral("pending_work"), 0);
        QCOMPARE(overlay.modules().first(), QStringLiteral("pending_work"));
        overlay.moveModuleTo(QStringLiteral("pending_work"), 99);
        QCOMPARE(overlay.modules().last(), QStringLiteral("pending_work"));
        overlay.toggleModule(QStringLiteral("jobs"));
        overlay.moveModule(QStringLiteral("dungeons"), 1); // past the hidden row
        QCOMPARE(overlay.moduleRows().at(5).toMap().value(QStringLiteral("key")).toString(), QStringLiteral("dungeons"));
        QCOMPARE(overlay.modules(), QStringList({"attempts", "progress", "current_duty", "elapsed", "dungeons", "pending_work"}));
    }
    mr::AppSettings restored(path, nullptr);
    mr::StatisticsOverlayController overlay(&restored);
    QCOMPARE(overlay.modules(), QStringList({"attempts", "progress", "current_duty", "elapsed", "dungeons", "pending_work"}));
    QCOMPARE(overlay.moduleRows().at(4).toMap().value(QStringLiteral("key")).toString(), QStringLiteral("jobs"));
    QVERIFY(!overlay.moduleRows().at(4).toMap().value(QStringLiteral("on")).toBool());
}

void StatisticsOverlayTests::invalidPreferencesAreBounded()
{
    QTemporaryDir directory(QString::fromUtf8(MR_SOURCE_DIR) + kEvidence() + QStringLiteral("/invalid-preferences-XXXXXX"));
    const QString path = directory.filePath(QStringLiteral("ui.ini"));
    {
        QSettings raw(path, QSettings::IniFormat);
        raw.setValue(QStringLiteral("statistics_overlay/opacity"), QStringLiteral("broken"));
        raw.setValue(QStringLiteral("statistics_overlay/row_limit"), QStringLiteral("broken"));
        raw.setValue(QStringLiteral("statistics_overlay/geometry"), QRect(-2000000, 2000000, 9000, 1));
        raw.setValue(QStringLiteral("statistics_overlay/modules"), QStringList({"unknown", "jobs", "jobs"}));
        raw.setValue(QStringLiteral("statistics_overlay/module_order"), QStringList({"unknown", "dungeons"}));
        raw.setValue(QStringLiteral("statistics_overlay/form"), QStringLiteral("huge"));
    }
    mr::AppSettings settings(path, nullptr);
    mr::StatisticsOverlayController overlay(&settings);
    QCOMPARE(overlay.opacity(), 0.85);
    QCOMPARE(overlay.rowLimit(), 3);
    QCOMPARE(overlay.geometry(), QRect(-1000000, 1000000, 520, 150));
    QCOMPARE(overlay.modules(), QStringList({"jobs"}));
    QCOMPARE(overlay.moduleRows().first().toMap().value(QStringLiteral("key")).toString(), QStringLiteral("attempts"));
    QCOMPARE(overlay.moduleRows().at(1).toMap().value(QStringLiteral("key")).toString(), QStringLiteral("dungeons"));
    QCOMPARE(overlay.moduleRows().size(), 7);
    QCOMPARE(overlay.form(), QStringLiteral("full"));
    overlay.setOpacity(std::numeric_limits<double>::quiet_NaN());
    QCOMPARE(overlay.opacity(), 0.85);
    overlay.setOpacity(10);
    QCOMPARE(overlay.opacity(), 1.0);
    overlay.setOpacity(-10);
    QCOMPARE(overlay.opacity(), 0.3);
    overlay.setRowLimit(-1);
    QCOMPARE(overlay.rowLimit(), 3);
    overlay.setRowLimit(7);
    QCOMPARE(overlay.rowLimit(), 3);
    overlay.setRowLimit(overlay.allRows());
    QCOMPARE(overlay.rowLimit(), mr::StatisticsOverlayController::kAllRows);
    overlay.setForm(QStringLiteral("sideways"));
    QCOMPARE(overlay.form(), QStringLiteral("full"));
    overlay.setLocked(true);
    QVERIFY(!overlay.locked());
    overlay.moveModule(QStringLiteral("jobs"), -1);
    overlay.toggleModule(QStringLiteral("invalid"));
    QCOMPARE(overlay.modules(), QStringList({"jobs"}));
}

void StatisticsOverlayTests::geometryHandlesMissingAndNegativeScreens()
{
    using Overlay = mr::StatisticsOverlayController;
    const QRect left(-1920, 0, 1920, 1040);
    const QRect visible = Overlay::fitGeometry(QRect(-900, 200, 380, 600), left);
    QCOMPARE(visible, QRect(-900, 200, 380, 600));
    const QRect primary(0, 0, 1280, 720);
    QVERIFY(primary.contains(Overlay::fitGeometry(QRect(-1900, 1500, 800, 1200), primary)));
    const QRect tiny(0, 0, 240, 200);
    QCOMPARE(Overlay::fitGeometry(QRect(50, 50, 380, 600), tiny), tiny);
    QCOMPARE(Overlay::fitGeometry(tiny.translated(10, 10), tiny), tiny);
    QCOMPARE(Overlay::validGeometry(QRect()), QRect(80, 80, 300, 560));
    QCOMPARE(Overlay::validGeometry(QRect(0, 0, 100, 100)).size(), QSize(240, 150));
}

void StatisticsOverlayTests::nativeCloseSavesGeometryBeforeTheDebounce() const
{
    Scene scene;
    scene.backend.setLiveMode(mr::MockBackend::LiveMode::None);
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    scene.overlay()->show();
    QVERIFY(QTest::qWaitForWindowExposed(scene.floating()));
    QTRY_VERIFY(scene.floating()->property("positionReady").toBool());
    const QRect previous = scene.overlay()->geometry();
    const QRect moved = mr::StatisticsOverlayController::fitGeometry(
        QRect(previous.x() + 24, previous.y() + 20, 420, 580), scene.floating()->screen()->availableGeometry());
    QVERIFY(moved != previous);
    scene.floating()->setGeometry(moved);
    QCOMPARE(scene.floating()->geometry(), moved);
    // No event-loop wait: closing must save the latest geometry before the
    // 180 ms position timer can copy it into the controller's preferences.
    QCOMPARE(scene.overlay()->geometry(), previous);
    QVERIFY(scene.floating()->close());
    QVERIFY(!scene.overlay()->visible());
    QCOMPARE(scene.overlay()->geometry(), moved);
    QCOMPARE(scene.settings.statisticsOverlayGeometry(), moved);
    QVERIFY(!scene.floating()->isVisible());
    scene.overlay()->show();
    QVERIFY(QTest::qWaitForWindowExposed(scene.floating()));
    QTRY_VERIFY(scene.floating()->property("positionReady").toBool());
    QCOMPARE(scene.floating()->geometry(), moved);
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void StatisticsOverlayTests::realWindowCanCustomizeAndRecoverEmptyContent()
{
    Scene scene;
    scene.backend.setLiveMode(mr::MockBackend::LiveMode::None);
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    QVERIFY(scene.floating());
    QVERIFY(!scene.floating()->isVisible());
    scene.overlay()->show();
    QVERIFY(QTest::qWaitForWindowExposed(scene.floating()));
    QTRY_VERIFY(scene.floating()->property("positionReady").toBool());
    QVERIFY(!scene.floating()->transientParent());
    QVERIFY(scene.floating()->flags().testFlag(Qt::WindowStaysOnTopHint));
    QCOMPARE(scene.floating()->width(), 300);
    QVERIFY(scene.item(QStringLiteral("floatingModule_progress"), true));
    // 当前副本 and 副本用时 wait for a run; 待处理 for a record.
    QVERIFY(!scene.item(QStringLiteral("floatingModule_current_duty"), true));
    QVERIFY(!scene.item(QStringLiteral("floatingModule_elapsed"), true));
    QVERIFY(!scene.item(QStringLiteral("floatingModule_pending_work"), true));

    customizeOverlayModules(scene);
    QVERIFY(!QTest::currentTestFailed());
    scene.floating()->setGeometry(120, 90, 460, 560);
    const QRect fitted = mr::StatisticsOverlayController::fitGeometry(
        scene.floating()->geometry(), scene.floating()->screen()->availableGeometry());
    QTRY_COMPARE(scene.overlay()->geometry(), fitted);
    scene.overlay()->setLocked(true);
    QTRY_VERIFY(scene.floating()->flags().testFlag(Qt::WindowTransparentForInput));
    QVERIFY(scene.floating()->flags().testFlag(Qt::WindowDoesNotAcceptFocus));
    QTRY_VERIFY(!scene.item(QStringLiteral("floatingHeader"), true));
    scene.overlay()->unlock();
    QTRY_VERIFY(!scene.floating()->flags().testFlag(Qt::WindowTransparentForInput));
    QTRY_VERIFY(scene.item(QStringLiteral("floatingHeader"), true));
    QVERIFY(scene.floating()->isVisible());
    scene.floating()->close();
    QTRY_VERIFY(!scene.overlay()->visible());
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void StatisticsOverlayTests::customizePanelTogglesFromTheTitleBar_data()
{
    QTest::addColumn<bool>("reduceMotion");
    QTest::newRow("reduced-motion") << true;
    QTest::newRow("animated") << false;
}

void StatisticsOverlayTests::customizePanelTogglesFromTheTitleBar()
{
    QFETCH(bool, reduceMotion);
    Scene scene;
    QVERIFY2(scene.create(QStringLiteral("harendotes"), true, false, reduceMotion), qPrintable(scene.errors));
    scene.overlay()->show();
    QVERIFY(QTest::qWaitForWindowExposed(scene.floating()));
    const auto panelOpen = [&] { return scene.floating()->property("customizing").toBool(); };
    for (int cycle = 0; cycle < 3; ++cycle) {
        QVERIFY(scene.click(QStringLiteral("floatingCustomizeButton")));
        QTRY_VERIFY(panelOpen());
        QVERIFY(scene.item(QStringLiteral("floatingCustomizePanel"), true));
        QVERIFY(scene.click(QStringLiteral("floatingCustomizeButton")));
        QTRY_VERIFY(!panelOpen());
        QTest::qWait(60);
        QVERIFY(!panelOpen());
    }
    auto *button = scene.item(QStringLiteral("floatingCustomizeButton"));
    QVERIFY(button);
    scene.floating()->requestActivate();
    QVERIFY(QTest::qWaitForWindowActive(scene.floating()));
    QVERIFY(QMetaObject::invokeMethod(button, "forceActiveFocus"));
    QTest::keyClick(scene.floating(), Qt::Key_Space);
    QTRY_VERIFY(panelOpen());
    QTest::keyClick(scene.floating(), Qt::Key_Escape);
    QTRY_VERIFY(!panelOpen());
    QVERIFY(QMetaObject::invokeMethod(button, "forceActiveFocus"));
    QTest::keyClick(scene.floating(), Qt::Key_Space);
    QTRY_VERIFY(panelOpen());
    // ↑ on a grip moves the module and keeps the keyboard on it, so ↑ again moves it further.
    auto *grip = scene.item(QStringLiteral("floatingDragHandle_jobs"), true);
    QVERIFY(grip);
    QVERIFY(QMetaObject::invokeMethod(grip, "forceActiveFocus"));
    QCOMPARE(scene.overlay()->modules().indexOf(QStringLiteral("jobs")), 5);
    QTest::keyClick(scene.floating(), Qt::Key_Up);
    QTRY_COMPARE(scene.overlay()->modules().indexOf(QStringLiteral("jobs")), 4);
    QTRY_VERIFY(scene.item(QStringLiteral("floatingDragHandle_jobs"), true)->hasActiveFocus());
    QTest::keyClick(scene.floating(), Qt::Key_Up);
    QTRY_COMPARE(scene.overlay()->modules().indexOf(QStringLiteral("jobs")), 3);
    // Locking closes the panel: the locked float shows content only.
    scene.overlay()->setLocked(true);
    QTRY_VERIFY(!panelOpen());
    scene.overlay()->unlock();
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void StatisticsOverlayTests::customizationFooterRemainsReachableWithSevenModules()
{
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    scene.overlay()->setGeometry(QRect(80, 80, 240, 560));
    scene.overlay()->setAttemptPeriod(QStringLiteral("last7"));
    scene.overlay()->setLabels(true);
    scene.overlay()->show();
    QVERIFY(QTest::qWaitForWindowExposed(scene.floating()));
    QVERIFY(scene.click(QStringLiteral("floatingCustomizeButton")));
    QTRY_VERIFY(scene.item(QStringLiteral("floatingCustomizePanel"), true));
    QCOMPARE(scene.overlay()->moduleRows().size(), 7);
    QVERIFY(scene.revealCustomizationFooter());
    QVERIFY(scene.grabAttempts(QStringLiteral("240-customization-footer")));
    QVERIFY(scene.click(QStringLiteral("floatingCustomizationReset")));
    QCOMPARE(scene.overlay()->attemptPeriod(), QStringLiteral("all"));
    QCOMPARE(scene.overlay()->modules(), mr::StatisticsOverlayController::defaultModules());
    QVERIFY(!scene.overlay()->labels());
    QVERIFY(scene.revealCustomizationFooter());
    QVERIFY(scene.click(QStringLiteral("floatingCustomizationDone")));
    QTRY_VERIFY(!scene.floating()->property("customizing").toBool());
    QCOMPARE(scene.floating()->size(), QSize(240, 560));
    QVERIFY(scene.item(QStringLiteral("floatingAttemptPeriods"), true));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void StatisticsOverlayTests::miniBarSharesThePositionAndKeepsTheSize()
{
    Scene scene;
    scene.backend.setLiveMode(mr::MockBackend::LiveMode::None);
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    scene.overlay()->setGeometry(QRect(140, 120, 340, 520));
    scene.overlay()->show();
    QVERIFY(QTest::qWaitForWindowExposed(scene.floating()));
    QTRY_VERIFY(scene.floating()->property("positionReady").toBool());
    QCOMPARE(scene.floating()->size(), QSize(340, 520));
    QVERIFY(scene.click(QStringLiteral("floatingMiniButton")));
    QTRY_COMPARE(scene.overlay()->form(), QStringLiteral("mini"));
    QTRY_COMPARE(scene.floating()->width(), 340);
    QTRY_VERIFY(scene.floating()->height() < 80);
    QVERIFY(scene.item(QStringLiteral("floatingMiniProgress"), true));
    QVERIFY(!scene.item(QStringLiteral("floatingMiniRun"), true));
    QVERIFY(!scene.item(QStringLiteral("floatingHeader"), true));
    // The full size is kept while the bar is up; moving the bar moves both.
    QCOMPARE(scene.overlay()->geometry().size(), QSize(340, 520));
    scene.floating()->setPosition(160, 140);
    QTRY_COMPARE(scene.overlay()->geometry().topLeft(), QPoint(160, 140));
    QCOMPARE(scene.overlay()->geometry().size(), QSize(340, 520));
    moveMiniBarNearScreenEdge(scene);
    QVERIFY(!QTest::currentTestFailed());
    // A run adds the second line.
    scene.backend.setLiveMode(mr::MockBackend::LiveMode::Entered);
    QTRY_VERIFY(!scene.app->currentRun().value(QStringLiteral("run")).toMap().isEmpty());
    QTRY_VERIFY(scene.item(QStringLiteral("floatingMiniRun"), true));
    QTRY_VERIFY(scene.floating()->height() > 60);
    QVERIFY(scene.click(QStringLiteral("floatingExpandButton")));
    QTRY_COMPARE(scene.overlay()->form(), QStringLiteral("full"));
    QTRY_COMPARE(scene.floating()->size(), QSize(340, 520));
    QVERIFY(scene.item(QStringLiteral("floatingModule_current_duty"), true));
    QVERIFY(scene.item(QStringLiteral("floatingModule_elapsed"), true));
    // 文字标签 puts the captions on screen.
    QVERIFY(!scene.texts().contains(QString::fromUtf8("累计进度")));
    scene.overlay()->setLabels(true);
    QTRY_VERIFY(scene.texts().contains(QString::fromUtf8("累计进度")));
    QVERIFY(scene.texts().contains(QString::fromUtf8("当前副本")));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void StatisticsOverlayTests::formControlsLeadRightActionGroup_data()
{
    QTest::addColumn<int>("width");
    QTest::addColumn<bool>("dark");
    for (bool dark : {false, true})
        for (int width : {240, 340, 520})
            QTest::newRow(qPrintable(QStringLiteral("%1-%2").arg(width).arg(dark ? "dark" : "light"))) << width << dark;
}

void StatisticsOverlayTests::formControlsLeadRightActionGroup()
{
    QFETCH(int, width);
    QFETCH(bool, dark);
    Scene scene;
    scene.backend.setLiveMode(mr::MockBackend::LiveMode::None);
    QVERIFY2(scene.create(QStringLiteral("harendotes"), dark), qPrintable(scene.errors));
    scene.overlay()->show();
    QVERIFY(QTest::qWaitForWindowExposed(scene.floating()));
    QTRY_VERIFY(scene.floating()->property("positionReady").toBool());
    QTRY_VERIFY(scene.app->achievementSettingsLoaded());
    QObject *data = scene.overlayData();
    QVERIFY(data);
    scene.app->updateAchievementBaseline(2000, scene.app->baselineCount() + 2000 - data->property("progress").toInt(),
                                         QStringLiteral("form-width-test"));
    QTRY_COMPARE(data->property("progress").toInt(), 2000);
    // Do not wait for the 180 ms geometry-save timer before collapsing.
    scene.floating()->resize(width, 520);
    QTRY_COMPARE(scene.floating()->contentItem()->width(), width);
    auto *collapse = scene.item(QStringLiteral("floatingMiniButton"), true);
    auto *fullActions = scene.item(QStringLiteral("floatingFullActions"), true);
    auto *customize = scene.item(QStringLiteral("floatingCustomizeButton"), true);
    QVERIFY(collapse && fullActions && customize);
    const auto polishAncestors = [](QQuickItem *item) {
        QList<QQuickItem *> ancestors;
        for (auto *parent = item; parent; parent = parent->parentItem()) ancestors.prepend(parent);
        for (auto *parent : ancestors) parent->ensurePolished();
    };
    polishAncestors(collapse);
    QCOMPARE(collapse->parentItem(), fullActions);
    QCOMPARE(collapse->x(), 0);
    QVERIFY(collapse->x() + collapse->width() < customize->x());
    const qreal fullRight = fullActions->mapToScene(QPointF(fullActions->width(), 0)).x();
    QVERIFY2(qAbs(width - fullRight) <= 8,
             qPrintable(QStringLiteral("window %1, right group edge %2").arg(width).arg(fullRight)));
    const QPoint click = collapse->mapToScene(QPointF(collapse->width() / 2, collapse->height() / 2)).toPoint();
    QTest::mouseClick(scene.floating(), Qt::LeftButton, Qt::NoModifier, click);
    QTRY_COMPARE(scene.overlay()->form(), QStringLiteral("mini"));
    QTRY_COMPARE(scene.floating()->width(), width);
    QCOMPARE(scene.overlay()->geometry().size(), QSize(width, 520));
    QVERIFY(!scene.floating()->property("transitioning").toBool()); // Reduced motion.
    auto *expand = scene.item(QStringLiteral("floatingExpandButton"), true);
    auto *lock = scene.item(QStringLiteral("floatingLockButton"), true);
    auto *progress = scene.item(QStringLiteral("floatingMiniProgress"), true);
    auto *miniActions = scene.item(QStringLiteral("floatingMiniActions"), true);
    QVERIFY(expand && lock && progress && miniActions);
    polishAncestors(expand);
    QCOMPARE(expand->parentItem(), miniActions);
    QCOMPARE(lock->parentItem(), miniActions);
    QCOMPARE(expand->x(), 0);
    const qreal miniRight = miniActions->mapToScene(QPointF(miniActions->width(), 0)).x();
    QCOMPARE(miniRight, fullRight);
    QCOMPARE(expand->size(), collapse->size());
    QVERIFY(expand->mapToScene(QPointF(expand->width(), 0)).x() < lock->mapToScene(QPointF()).x());
    // All direct row children, including both controls, fit the minimum width.
    progress->ensurePolished();
    for (auto *child : progress->childItems()) {
        if (!child->isVisible() || child->width() <= 0) continue;
        const QRectF bounds(child->mapToItem(progress, QPointF()), child->size());
        QVERIFY2(bounds.left() >= -0.5 && bounds.right() <= progress->width() + 0.5,
                 qPrintable(QStringLiteral("%1 exceeds %2: %3..%4")
                     .arg(QString::fromLatin1(child->metaObject()->className())).arg(width)
                     .arg(bounds.left()).arg(bounds.right())));
    }
    const QString output = qEnvironmentVariable("MR_OVERLAY_FORM_EVIDENCE", QString::fromUtf8(MR_SOURCE_DIR)
                                                + QStringLiteral("/artifacts/overlay-form-transition-20261022"));
    QVERIFY(QDir().mkpath(output));
    QTest::qWait(60); // Let the newly visible image and hover state reach the render thread.
    const QString suffix = QStringLiteral("%1-%2.png").arg(width).arg(dark ? "dark" : "light");
    QVERIFY(scene.floating()->grabWindow().save(output + QStringLiteral("/mini-") + suffix));
    // The mini group's first action expands; it never invokes its following lock.
    QVERIFY(scene.click(QStringLiteral("floatingExpandButton")));
    QTRY_COMPARE(scene.overlay()->form(), QStringLiteral("full"));
    QTRY_COMPARE(scene.floating()->size(), QSize(width, 520));
    QVERIFY(!scene.overlay()->locked());
    QTest::qWait(60);
    QVERIFY(scene.floating()->grabWindow().save(output + QStringLiteral("/full-") + suffix));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void StatisticsOverlayTests::formTransitionKeepsStableGeometry() const
{
    Scene scene;
    scene.backend.setLiveMode(mr::MockBackend::LiveMode::None);
    QVERIFY2(scene.create(QStringLiteral("harendotes"), false, false, false), qPrintable(scene.errors));
    scene.overlay()->setGeometry(QRect(100, 100, 340, 520));
    scene.overlay()->show();
    QVERIFY(QTest::qWaitForWindowExposed(scene.floating()));
    QTRY_VERIFY(scene.floating()->property("positionReady").toBool());
    const QSize fullSize(340, 520);
    const QString output = qEnvironmentVariable("MR_OVERLAY_FORM_EVIDENCE", QString::fromUtf8(MR_SOURCE_DIR)
                                                + QStringLiteral("/artifacts/overlay-form-transition-20261022"));
    QVERIFY(QDir().mkpath(output));
    int frame = 0;
    const bool captureFrames = formEvidenceClock != nullptr;
    QFile captureLog(output + QStringLiteral("/capture-timeline.csv"));
    if (captureFrames) {
        QVERIFY(captureLog.open(QIODevice::WriteOnly));
        captureLog.write("frame,driver_ms,height,blend\n");
    }
    const auto capture = [&] {
        if (!captureFrames) return true;
        captureLog.write(QStringLiteral("%1,%2,%3,%4\n").arg(frame).arg(formEvidenceClock->elapsed())
                             .arg(scene.floating()->height()).arg(scene.floating()->property("formBlend").toReal()).toUtf8());
        const QImage shot = scene.floating()->grabWindow();
        QImage canvas(shot.width(), qRound(520 * shot.devicePixelRatio()), QImage::Format_ARGB32_Premultiplied);
        canvas.setDevicePixelRatio(shot.devicePixelRatio());
        canvas.fill(QColor(QStringLiteral("#e5e7ee")));
        QPainter painter(&canvas);
        painter.drawImage(QPoint(), shot);
        painter.end();
        return canvas.save(output + QStringLiteral("/frame-%1.png").arg(frame++, 2, 10, QLatin1Char('0')));
    };
    QTest::qWait(500); // Let startup charts settle before measuring the form switch.
    QTRY_VERIFY(scene.item(QStringLiteral("floatingRoleBarFill"), true));
    QVERIFY(scene.item(QStringLiteral("floatingRoleBarTrack"), true));
    // The evidence driver is installed in main, before Qt Quick's own driver.
    // Always resume real time on an assertion's early return as well.
    struct ResumeClock {
        FormEvidenceClock *clock;
        ~ResumeClock() { if (clock) clock->followRealTime(true); }
    } resumeClock{formEvidenceClock};
    if (captureFrames) {
        QVERIFY(formEvidenceClock->isRunning());
        formEvidenceClock->followRealTime(false);
    }
    QVERIFY(capture());
    if (captureFrames) {
        // Synchronize the unified timer's last tick before starting a new
        // animation; no startup/initial-grab time belongs to its first frame.
        formEvidenceClock->advance();
        QCoreApplication::processEvents(QEventLoop::AllEvents, 5);
    }
    struct FrameTrace {
        QElapsedTimer clock;
        QMutex mutex;
        QList<qint64> times;
    };
    auto *outgoing = scene.item(QStringLiteral("floatingFullContent"));
    auto *incoming = scene.item(QStringLiteral("floatingMiniContent"));
    QVERIFY(outgoing && incoming);
    bool sawIntermediate = false;
    bool sampledFade = false;
    bool overlappingForms = false;
    bool overflowingBars = false;
    bool sawBar = false;
    int observedFrames = 0;
    const auto observeIntermediate = [&] {
        ++observedFrames;
        // The outgoing form fades out over the first half of formBlend, the
        // first ~29 ms of the 200 ms switch on its standard curve.
        const qreal blend = scene.floating()->property("formBlend").toReal();
        sampledFade |= blend > 0 && blend < 0.5;
        sawIntermediate |= scene.floating()->height() > 80 && scene.floating()->height() < 520
                           && outgoing->opacity() > 0 && outgoing->opacity() < 1;
        overlappingForms |= outgoing->opacity() > 0.001 && incoming->opacity() > 0.001;
        QList<QQuickItem *> pending{scene.floating()->contentItem()};
        while (!pending.isEmpty()) {
            auto *item = pending.takeLast();
            if (item->objectName() == QLatin1String("floatingRoleBarFill")) {
                sawBar = true;
                overflowingBars |= item->height() > item->parentItem()->height() + 0.5 || item->y() < -0.5;
            }
            pending.append(item->childItems());
        }
    };
    // Inspect after all animation bindings have settled, just before the render
    // thread synchronizes: per-property notifications can expose binding order
    // within a frame rather than a state that is actually rendered.
    const auto visualObserver = QObject::connect(scene.floating(), &QQuickWindow::afterAnimating,
                                                 scene.floating(), observeIntermediate);
    const auto disconnectVisualObserver = qScopeGuard([&] { QObject::disconnect(visualObserver); });
    QFile frameLog(output + QStringLiteral("/frame-cadence.csv"));
    if (!captureFrames) {
        QVERIFY(frameLog.open(QIODevice::WriteOnly));
        frameLog.write("attempt,frame,elapsed_ms,interval_ms\n");
    }
    // Under CPU load one late frame can step over the whole fade. Such an attempt
    // shows nothing about the transition, so switch back and sample again; a frame
    // inside the fade that shows no intermediate form still fails at once.
    const int maxAttempts = captureFrames ? 1 : 5;
    int attempts = 0;
    double longestFrameGapMs = 0;
    for (;;) {
        ++attempts;
        sawIntermediate = false;
        sampledFade = false;
        observedFrames = 0;
        const auto trace = std::make_shared<FrameTrace>();
        trace->clock.start();
        const auto frameObserver = !captureFrames ? QObject::connect(scene.floating(), &QQuickWindow::frameSwapped, scene.floating(), [trace] {
            QMutexLocker guard(&trace->mutex);
            trace->times.append(trace->clock.nsecsElapsed());
        }, Qt::DirectConnection) : QMetaObject::Connection(); // Render-thread timestamp, not queued delivery.
        // QtTest's mouse dispatch processes events before returning. Observe before
        // clicking so the outgoing form's short fade cannot finish before sampling.
        QVERIFY(scene.click(QStringLiteral("floatingMiniButton")));
        QVERIFY(scene.floating()->property("transitioning").toBool());
        QVERIFY(!outgoing->isEnabled());
        if (captureFrames) {
            // QML registers newly started jobs through queued calls. Drain those at
            // time zero, before the first manual tick, for an accurate 200 ms run.
            for (int round = 0; round < 3; ++round)
                QCoreApplication::processEvents(QEventLoop::AllEvents, 5);
            for (int step = 0; step < 10; ++step) {
                formEvidenceClock->step(20);
                QTest::qWait(20); // Native resize/render settles while the animation clock stays frozen.
                QCOMPARE(scene.floating()->width(), 340);
                QCOMPARE(scene.overlay()->geometry().size(), fullSize);
                QVERIFY(capture());
            }
        } else {
            // A normal event loop avoids QTest's polling sleeps and screenshot readbacks
            // disturbing the presentation cadence that this mode measures.
            QEventLoop eventLoop;
            QTimer::singleShot(300, &eventLoop, &QEventLoop::quit);
            eventLoop.exec();
        }
        QObject::disconnect(frameObserver);
        QList<qint64> frameTimes;
        {
            QMutexLocker guard(&trace->mutex);
            frameTimes = trace->times;
        }
        longestFrameGapMs = 0;
        if (!captureFrames) {
            for (qsizetype i = 0; i < frameTimes.size(); ++i) {
                const double elapsedMs = static_cast<double>(frameTimes[i]) / 1e6;
                const double intervalMs = i > 0 ? static_cast<double>(frameTimes[i] - frameTimes[i - 1]) / 1e6 : 0.0;
                longestFrameGapMs = std::max(longestFrameGapMs, i > 0 ? intervalMs : elapsedMs);
                frameLog.write(QStringLiteral("%1,%2,%3,%4\n").arg(attempts).arg(i).arg(elapsedMs, 0, 'f', 3)
                                   .arg(intervalMs, 0, 'f', 3).toUtf8());
            }
        }
        if (sawIntermediate || sampledFade || attempts == maxAttempts)
            break;
        QTRY_VERIFY(!scene.floating()->property("transitioning").toBool());
        scene.overlay()->setForm(QStringLiteral("full"));
        QTRY_VERIFY(!scene.floating()->property("transitioning").toBool());
        QTRY_COMPARE(scene.floating()->size(), fullSize);
    }
    frameLog.close();
    QVERIFY2(sawIntermediate, qPrintable(QStringLiteral(
        "No intermediate form sampled: attempts=%1 fadeSampled=%2 frames=%3 longestFrameGapMs=%4 "
        "height=%5 blend=%6 outgoingOpacity=%7")
        .arg(attempts).arg(sampledFade).arg(observedFrames).arg(longestFrameGapMs, 0, 'f', 1)
        .arg(scene.floating()->height())
        .arg(scene.floating()->property("formBlend").toReal()).arg(outgoing->opacity())));
    QTRY_VERIFY(!scene.floating()->property("transitioning").toBool());
    QVERIFY(scene.floating()->height() < 80);
    auto *expand = scene.item(QStringLiteral("floatingExpandButton"), true);
    QVERIFY(expand);
    const QPoint expandPoint = expand->mapToScene(QPointF(expand->width() / 2, expand->height() / 2)).toPoint();
    QVERIFY(scene.click(QStringLiteral("floatingExpandButton")));
    QVERIFY(scene.floating()->property("transitioning").toBool());
    // The full lock occupies the mini expand button's former position. A second
    // click while that lock is still transparent must not activate it.
    QTest::mouseClick(scene.floating(), Qt::LeftButton, Qt::NoModifier, expandPoint);
    QVERIFY(!scene.overlay()->locked());
    if (captureFrames)
        for (int round = 0; round < 3; ++round)
            QCoreApplication::processEvents(QEventLoop::AllEvents, 5);
    for (int step = 0; step < (captureFrames ? 10 : 8); ++step) {
        if (captureFrames) formEvidenceClock->step(20);
        QTest::qWait(captureFrames ? 20 : 30);
        QVERIFY(capture());
    }
    if (captureFrames) formEvidenceClock->followRealTime(true);
    QTRY_COMPARE(scene.floating()->size(), fullSize);
    if (captureFrames) {
        QObject::disconnect(visualObserver);
        QVERIFY(sawBar);
        QVERIFY(!overlappingForms);
        QVERIFY(!overflowingBars);
        QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
        // Interruption timing is checked by the normal threaded-render process
        // below. Image readbacks/basic rendering must not skew its 35 ms waits.
        return;
    }

    verifyInterruptedFormTransitions(scene, fullSize);
    QVERIFY(!QTest::currentTestFailed());
    QObject::disconnect(visualObserver);
    QVERIFY(sawBar);
    QVERIFY(!overlappingForms);
    QVERIFY(!overflowingBars);
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

// The bar docked at the bottom edge comes back there when the float is closed and
// opened again or the program restarts, and a run's second line grows it upwards,
// not off the screen (audit 2026-10-20, L3-1; ui-design §4.9.1 两者共用位置).
void StatisticsOverlayTests::miniBarStaysWhereItWasDocked()
{
    Scene scene;
    scene.backend.setLiveMode(mr::MockBackend::LiveMode::None);
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    scene.overlay()->setGeometry(QRect(140, 120, 340, 520));
    scene.overlay()->setForm(QStringLiteral("mini"));
    scene.overlay()->show();
    QVERIFY(QTest::qWaitForWindowExposed(scene.floating()));
    QTRY_VERIFY(scene.floating()->property("positionReady").toBool());
    QTRY_COMPARE(scene.floating()->width(), 340);
    const int barHeight = scene.floating()->height();
    QVERIFY(barHeight < 60);
    const QRect available = scene.floating()->screen()->availableGeometry();
    const int screenBottom = available.y() + available.height();
    const int dockedY = screenBottom - barHeight;
    scene.floating()->setPosition(160, dockedY);
    QTRY_COMPARE(scene.overlay()->geometry().topLeft(), QPoint(160, dockedY));

    scene.overlay()->hide();
    QTRY_VERIFY(!scene.floating()->isVisible());
    {
        // The next start reads the same place.
        mr::StatisticsOverlayController restarted(&scene.settings);
        restarted.show();
        QCOMPARE(restarted.geometry().topLeft(), QPoint(160, dockedY));
    }
    scene.overlay()->show();
    QVERIFY(QTest::qWaitForWindowExposed(scene.floating()));
    QTRY_VERIFY(scene.floating()->property("positionReady").toBool());
    QCOMPARE(scene.overlay()->geometry().topLeft(), QPoint(160, dockedY));
    QTRY_COMPARE(scene.floating()->position(), QPoint(160, dockedY));
    QCOMPARE(scene.overlay()->geometry().size(), QSize(340, 520));

    // A run adds the second line: the bar grows upwards and stays on the screen.
    scene.backend.setLiveMode(mr::MockBackend::LiveMode::Entered);
    QTRY_VERIFY(scene.item(QStringLiteral("floatingMiniRun"), true));
    QTRY_VERIFY(scene.floating()->height() > 60);
    QTRY_COMPARE(scene.floating()->y() + scene.floating()->height(), screenBottom);
    QTRY_COMPARE(scene.overlay()->geometry().topLeft(), scene.floating()->position());
    QCOMPARE(scene.overlay()->geometry().size(), QSize(340, 520));
    const int grownY = scene.floating()->y();
    scene.backend.setLiveMode(mr::MockBackend::LiveMode::None);
    QTRY_VERIFY(!scene.item(QStringLiteral("floatingMiniRun"), true));
    QTRY_COMPARE(scene.floating()->height(), barHeight);
    QCOMPARE(scene.floating()->y(), grownY);
    scene.overlay()->setForm(QStringLiteral("full"));
    QTRY_COMPARE(scene.floating()->size(), QSize(340, 520));
    QVERIFY(available.contains(scene.floating()->geometry()));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

// The mini bar's window is only as tall as the bar, so a tooltip that stays inside it
// has nowhere to go but over the bar itself - and its buttons (owner report, 2026-10-20).
// Each tip therefore opens in a window of its own, clear of the bar, and hovering a button
// shows that button's tip alone: the progress tip of the row underneath stays closed.
void StatisticsOverlayTests::miniBarTipsOpenOutsideTheBar()
{
    Scene scene;
    scene.backend.setLiveMode(mr::MockBackend::LiveMode::None);
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    scene.overlay()->setGeometry(QRect(200, 300, 340, 520));
    scene.overlay()->show();
    QVERIFY(QTest::qWaitForWindowExposed(scene.floating()));
    QTRY_VERIFY(scene.floating()->property("positionReady").toBool());
    // The title bar and the mini bar each have a lock button, so each has a tip of
    // that name; the one that counts belongs to the form on screen.
    QObject *headerLockTip = nullptr;
    verifyFullOverlayTips(scene, headerLockTip);
    QVERIFY(!QTest::currentTestFailed());
    scene.overlay()->setForm(QStringLiteral("mini"));
    QTRY_COMPARE(scene.floating()->width(), 340);
    QTRY_VERIFY(scene.floating()->property("positionReady").toBool());

    auto *lock = scene.item(QStringLiteral("floatingLockButton"), true);
    QVERIFY(lock);
    // The bar re-lays out after the window narrows; wait for the button to settle inside it.
    QTRY_VERIFY(centreOf(lock).x() < scene.floating()->width());
    auto *lockTip = visibleTip(scene, QStringLiteral("floatingLockButtonTip"));
    auto *progressTip = visibleTip(scene, QStringLiteral("floatingMiniProgressTip"));
    QVERIFY(lockTip);
    QVERIFY(lockTip != headerLockTip);
    QVERIFY(progressTip);
    // The tip uses the interface's word for the count before recording began (review L1-10).
    QVERIFY2(progressTip->property("text").toString().contains(QString::fromUtf8("含起始次数")),
             qPrintable(progressTip->property("text").toString()));
    QVERIFY(!lockTip->property("visible").toBool());
    QVERIFY(!progressTip->property("visible").toBool());

    const QPointF lockCentre = centreOf(lock);
    QTest::mouseMove(scene.floating(), lockCentre.toPoint());
    QTRY_VERIFY(lockTip->property("opened").toBool());
    QVERIFY(!progressTip->property("visible").toBool());

    // The tip lives in its own window and does not cover the bar.
    auto *tipItem = lockTip->property("contentItem").value<QQuickItem *>();
    QVERIFY(tipItem);
    QQuickWindow *tipWindow = tipItem->window();
    QVERIFY(tipWindow);
    QVERIFY2(tipWindow != scene.floating(), "the tip is drawn inside the bar's own window");
    const QRect bar = scene.floating()->geometry();
    const QRect tip = tipWindow->geometry();
    QVERIFY2(!tip.intersects(bar), qPrintable(QStringLiteral("tip %1,%2 %3x%4 overlaps bar %5,%6 %7x%8")
        .arg(tip.x()).arg(tip.y()).arg(tip.width()).arg(tip.height())
        .arg(bar.x()).arg(bar.y()).arg(bar.width()).arg(bar.height())));
    QVERIFY(tipWindow->grabWindow().save(QString::fromUtf8(MR_SOURCE_DIR) + kEvidence() + QStringLiteral("/mini-lock-tip.png")));
    QVERIFY(scene.grab(QStringLiteral("mini-lock-hovered")));

    // Leaving the button for the figures swaps the tips over.
    auto *count = scene.item(QStringLiteral("floatingMiniCount"), true);
    QVERIFY(count);
    QTest::mouseMove(scene.floating(), centreOf(count).toPoint());
    QTRY_VERIFY(progressTip->property("opened").toBool());
    QTRY_VERIFY(!lockTip->property("visible").toBool());
    auto *progressItem = progressTip->property("contentItem").value<QQuickItem *>();
    QVERIFY(progressItem && progressItem->window() && progressItem->window() != scene.floating());
    QVERIFY(!progressItem->window()->geometry().intersects(scene.floating()->geometry()));

    verifyMiniTipAtScreenTop(scene, lockTip, tipWindow, lockCentre);
    QVERIFY(!QTest::currentTestFailed());
    // The button still takes the click while its tip is up.
    QTest::mouseMove(scene.floating(), lockCentre.toPoint());
    QTRY_VERIFY(lockTip->property("opened").toBool());
    QVERIFY(scene.click(QStringLiteral("floatingLockButton")));
    QTRY_VERIFY(scene.overlay()->locked());
    QTRY_VERIFY(!lockTip->property("visible").toBool());
    scene.overlay()->unlock();
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void StatisticsOverlayTests::pendingRowOpensTheFirstRecord()
{
    Scene scene;
    scene.backend.setLiveMode(mr::MockBackend::LiveMode::None);
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    scene.overlay()->show();
    QVERIFY(QTest::qWaitForWindowExposed(scene.floating()));
    QVERIFY(!scene.item(QStringLiteral("floatingModule_pending_work"), true));
    QTRY_VERIFY(scene.app->recovery()->identityVerified());
    const auto run = unknownResultRun();
    scene.app->recovery()->offer(run, QStringLiteral("result"));
    QTRY_COMPARE(scene.app->recovery()->count(), 1);
    QTRY_VERIFY(scene.item(QStringLiteral("floatingPendingButton"), true));
    auto *count = scene.item(QStringLiteral("floatingPendingCount"), true);
    QVERIFY(count);
    QCOMPARE(count->property("text").toString(), QStringLiteral("1"));
    QSignalSpy opened(scene.app->recovery(), &mr::RecoveryController::openRequested);
    QVERIFY(scene.click(QStringLiteral("floatingPendingButton")));
    QTRY_COMPARE(opened.count(), 1);
    QCOMPARE(opened.first().first().toMap().value(QStringLiteral("run_id")).toString(), run.value(QStringLiteral("run_id")).toString());
    // A cleared run that only waits for a note joins the count, so the tip, the
    // badge's tip and the label speak of records to deal with, not of unknown
    // results (audit 2026-10-20, L3-2 / P-8).
    const QVariantMap noteOnly = completedRun(2);
    scene.app->recovery()->offer(noteOnly, QStringLiteral("reflection"));
    const auto waiting = [&] {
        return std::ranges::any_of(scene.app->recovery()->items(), [&](const QVariant &item) {
            return item.toMap().value(QStringLiteral("runId")) == noteOnly.value(QStringLiteral("run_id"));
        });
    };
    QTRY_VERIFY(waiting() && !scene.app->recovery()->busy());
    const QString shown = QString::number(scene.app->recovery()->count());
    QVERIFY(scene.app->recovery()->count() >= 2);
    QTRY_COMPARE(count->property("text").toString(), shown);
    QObject *pendingTip = nullptr;
    for (QObject *child : scene.item(QStringLiteral("floatingPendingButton"), true)->children())
        if (child->inherits("QQuickToolTip")) pendingTip = child;
    QVERIFY(pendingTip);
    for (const QString &tip : {pendingTip->property("text").toString(),
                               scene.overlayData()->property("pendingTip").toString()}) {
        QVERIFY2(tip.contains(shown), qPrintable(tip));
        QVERIFY2(!tip.contains(QString::fromUtf8("结果未知")), qPrintable(tip));
    }
    scene.overlay()->setLabels(true);
    QTRY_VERIFY(!scene.textItemsContaining(shown + QString::fromUtf8(" 条")).isEmpty());
    for (auto *label : scene.textItemsContaining(shown + QString::fromUtf8(" 条"))) {
        const QString text = label->property("text").toString();
        QVERIFY2(!text.contains(QString::fromUtf8("待确认")) && !text.contains(QString::fromUtf8("结果未知")), qPrintable(text));
    }
    scene.overlay()->setLabels(false);
    // The mini bar shows the same count as a badge on the crest.
    scene.overlay()->setForm(QStringLiteral("mini"));
    QTRY_VERIFY(scene.item(QStringLiteral("floatingMiniPendingBadge"), true));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

// A hidden float draws nothing: its endless animations - the 已匹配 / 进行中
// pulse and the wait stripes - stand still until it is shown, above all for
// players who never open it (audit 2026-10-20, L3-3).
void StatisticsOverlayTests::hiddenFloatKeepsItsEndlessAnimationsStill()
{
    Scene scene;
    scene.backend.setLiveMode(mr::MockBackend::LiveMode::None);
    QVERIFY2(scene.create(QStringLiteral("harendotes"), true, false, false), qPrintable(scene.errors));
    QVERIFY(!scene.floating()->isVisible());
    const struct { mr::MockBackend::LiveMode mode; const char *state; } runs[] = {
        {.mode=mr::MockBackend::LiveMode::Matched, .state="MENTOR_MATCHED"},
        {.mode=mr::MockBackend::LiveMode::Entered, .state="ENTERED_DUTY"}};
    for (const auto &run : runs) {
        scene.backend.setLiveMode(run.mode);
        QTRY_COMPARE(scene.app->currentRunState(), QString::fromLatin1(run.state));
        QTRY_VERIFY(scene.item(QStringLiteral("floatingCurrentDuty")));
        QVERIFY(!scene.endlessAnimations().isEmpty());
        QTest::qWait(60);
        QVERIFY2(scene.runningEndlessAnimations() == 0, run.state);
        scene.overlay()->show();
        QVERIFY(QTest::qWaitForWindowExposed(scene.floating()));
        QTRY_VERIFY2(scene.runningEndlessAnimations() > 0, run.state);
        scene.overlay()->hide();
        QTRY_VERIFY(!scene.floating()->isVisible());
        QTRY_VERIFY2(scene.runningEndlessAnimations() == 0, run.state);
    }
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void StatisticsOverlayTests::paceBarScalesByTheDutyThenItsCategory_data()
{
    QTest::addColumn<QString>("category");
    QTest::addColumn<bool>("sameDuty");
    QTest::addColumn<QString>("caption");
    // The live run is 天狼星灯塔 (content 17), a 四人迷宫; the history holds one
    // cleared run of 20:00.
    QTest::newRow("own average") << QStringLiteral("四人迷宫") << true << QStringLiteral("均 20:00");
    QTest::newRow("category average") << QStringLiteral("四人迷宫") << false << QStringLiteral("同类均 20:00");
    QTest::newRow("nothing to measure against") << QStringLiteral("讨伐歼灭战") << false << QString();
}

// 副本用时 measures the time against the duty's average, or its category's
// while the duty has none, and shows no bar with neither. The span used to be
// elapsed x 1.3 for a duty without an average, which held the bar at 77 %
// whatever the time (owner's screenshot, 2026-10-22).
void StatisticsOverlayTests::paceBarScalesByTheDutyThenItsCategory()
{
    QFETCH(QString, category);
    QFETCH(bool, sameDuty);
    QFETCH(QString, caption);
    QVariantMap history = completedRun();
    if (!sameDuty) {
        QVariantMap other;
        for (const auto &entry : mr::DutyCatalog::shared()->allDuties()) {
            const QVariantMap duty = entry.toMap();
            if (duty.value(QStringLiteral("content_id")).toLongLong() != 17
                && duty.value(QStringLiteral("duty_category")).toString() == category) {
                other = duty;
                break;
            }
        }
        QVERIFY(!other.isEmpty());
        history.insert(QStringLiteral("content_id"), other.value(QStringLiteral("content_id")));
        history.insert(QStringLiteral("duty_name"), other.value(QStringLiteral("duty_name")));
    } else {
        history.insert(QStringLiteral("content_id"), 17);
    }
    history.insert(QStringLiteral("duty_category"), category);
    history.insert(QStringLiteral("duration_ms"), 1200000);

    Scene scene;
    scene.backend.setLiveMode(mr::MockBackend::LiveMode::None);
    scene.backend.resetRuns({QJsonObject::fromVariantMap(history)});
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    scene.overlay()->show();
    QVERIFY(QTest::qWaitForWindowExposed(scene.floating()));
    auto *dungeons = scene.app->property("dungeons").value<QObject *>();
    QVERIFY(dungeons);
    QTRY_VERIFY(dungeons->property("count").toInt() > 0);
    scene.backend.setLiveMode(mr::MockBackend::LiveMode::Entered);
    QTRY_COMPARE(scene.app->currentRunState(), QStringLiteral("ENTERED_DUTY"));
    QObject *data = scene.overlayData();
    QVERIFY(data);
    QTRY_VERIFY(scene.item(QStringLiteral("floatingPaceBar")));
    QQuickItem *bar = scene.item(QStringLiteral("floatingPaceBar"));

    if (caption.isEmpty()) {
        QTRY_VERIFY(scene.item(QStringLiteral("floatingElapsed"), true));
        QCOMPARE(data->property("hasReference").toBool(), false);
        QVERIFY(!bar->isVisible());
        QVERIFY(scene.textItemsContaining(QString::fromUtf8("均 ")).isEmpty());
        QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
        return;
    }
    QTRY_VERIFY(!scene.textItemsContaining(caption).isEmpty());
    QVERIFY(bar->isVisible());
    // The bar spans 130 % of the 20:00 and fills with the time.
    const double first = bar->property("ratio").toDouble();
    const double expected = data->property("elapsedMs").toDouble() / (1.3 * 1200000);
    QVERIFY2(qAbs(first - expected) < 0.01, qPrintable(QStringLiteral("%1 vs %2").arg(first).arg(expected)));
    QVERIFY(qAbs(first - (1 / 1.3)) > 0.1);
    QVERIFY(qAbs(bar->property("markRatio").toDouble() - (1 / 1.3)) < 1e-9);
    QTRY_VERIFY_WITH_TIMEOUT(bar->property("ratio").toDouble() > first, 3000);
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

// The state mark ends its row, where the float's body clips: the pulse around
// the 已匹配 mark was cut off there (owner's screenshot, 2026-10-22). Each
// mark's box holds its pulse at the widest; the bell inside the 已匹配 ring
// shakes as each pulse sets off, and the hourglass beside the wait time runs
// its sand, turns over and runs again (owner, 2026-10-22).
void StatisticsOverlayTests::stateMarksStayInsideTheFloat()
{
    Scene scene;
    scene.backend.setLiveMode(mr::MockBackend::LiveMode::None);
    // Classic: its accent is blue, so red can only come from the mark itself.
    QVERIFY2(scene.create(QStringLiteral("classic"), true, false, false), qPrintable(scene.errors));
    scene.backend.setLiveMode(mr::MockBackend::LiveMode::Matched);
    QTRY_COMPARE(scene.app->currentRunState(), QStringLiteral("MENTOR_MATCHED"));
    scene.overlay()->show();
    QVERIFY(QTest::qWaitForWindowExposed(scene.floating()));
    // The full form's items (the mini bar's row holds a mark too, never
    // shown), found once shown: showing the float builds its modules anew.
    QTRY_VERIFY(scene.item(QStringLiteral("floatingMatchedBell"), true));
    QPointer<QQuickItem> bell = scene.item(QStringLiteral("floatingMatchedBell"), true);
    QTRY_VERIFY(scene.item(QStringLiteral("floatingWaitHourglass"), true));
    QPointer<QQuickItem> hourglass = scene.item(QStringLiteral("floatingWaitHourglass"), true);
    QTRY_VERIFY(hourglass->property("running").toBool());

    auto *scroll = scene.item(QStringLiteral("floatingStatisticsScroll"), true);
    QVERIFY(scroll);
    QPointer<QQuickItem> mark = bell->parentItem()->parentItem();
    const auto holdsThePulse = [&](double markSize) {
        if (!mark) return false;
        const double reach = markSize * 1.9 / 2;
        const QPointF centre = mark->mapToItem(scroll, QPointF(mark->width() / 2, mark->height() / 2));
        return mark->width() >= 2 * reach && centre.x() + reach <= scroll->width() + 0.5;
    };
    QTRY_VERIFY(holdsThePulse(18));

    // Over more than one beat (1.8 s) the bell shakes, and only while the
    // ring is setting off (it is at 1.58 x when the shake ends, 1.9 x at most).
    QQuickItem *pulse = mark->findChild<QQuickItem *>(QStringLiteral("statusPulse"));
    QVERIFY(pulse);
    // Red in every style: the bell, its ring and the pulse (owner, 2026-10-22).
    auto *theme = scene.engine.singletonInstance<QObject *>(qmlTypeId("MentorRecorder", 1, 0, "Theme"));
    QVERIFY(theme);
    const QColor red = theme->property("red").value<QColor>();
    QCOMPARE(bell->property("color").value<QColor>(), red);
    QCOMPARE(mark->property("callColor").value<QColor>(), red);
    bool shook = false;
    double latestShake = 0;
    QElapsedTimer watch;
    watch.start();
    while (watch.elapsed() < 2200) {
        QTest::qWait(5);
        const double swing = qAbs(bell->rotation());
        if (swing > 1) shook = true;
        if (swing > 0.5) latestShake = qMax(latestShake, pulse->scale());
    }
    QVERIFY(shook);
    QVERIFY2(latestShake < 1.65, qPrintable(QString::number(latestShake)));

    // One pour (3 s) and the rest before the turn, then the next pour.
    QTRY_VERIFY_WITH_TIMEOUT(hourglass->property("turn").toDouble() > 0, 5000);
    QTRY_VERIFY_WITH_TIMEOUT(hourglass->property("turn").toDouble() == 0
                             && hourglass->property("sand").toDouble() < 1, 2000);

    // Hidden, both stop: the bell hangs straight, the glass stands upright.
    scene.overlay()->hide();
    QTRY_VERIFY(!scene.floating()->isVisible());
    QVERIFY(bell && hourglass);
    QTRY_VERIFY(!hourglass->property("running").toBool());
    QCOMPARE(hourglass->property("turn").toDouble(), 0.0);
    QTRY_COMPARE(bell->rotation(), 0.0);

    scene.overlay()->show();
    QVERIFY(QTest::qWaitForWindowExposed(scene.floating()));
    QTRY_VERIFY(scene.item(QStringLiteral("floatingMatchedBell"), true));
    bell = scene.item(QStringLiteral("floatingMatchedBell"), true);
    mark = bell->parentItem()->parentItem();
    // Entering the duty keeps the modules: inRun must not flicker between 已匹配 and
    // 进本 (it did when derived from the four state booleans, re-evaluated one after
    // another), or the module repeater rebuilds and this mark dies with its row.
    QSignalSpy inRunSpy(scene.overlayData(), SIGNAL(inRunChanged()));
    scene.backend.setLiveMode(mr::MockBackend::LiveMode::Entered);
    QTRY_COMPARE(scene.app->currentRunState(), QStringLiteral("ENTERED_DUTY"));
    QTRY_VERIFY(holdsThePulse(8));
    QCOMPARE(inRunSpy.count(), 0);
    QVERIFY2(mark, "the 当前副本 row was rebuilt on entering the duty");
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

// The float's 当前副本 and the mini bar's run line show the duty category as the
// game's category icon, as the result popup does, not as the one-character badge
// (迷 / 讨 / …) (owner, 2026-10-27).
void StatisticsOverlayTests::runRowsShowTheCategoryIcon()
{
    Scene scene;
    scene.backend.setLiveMode(mr::MockBackend::LiveMode::Entered);
    QVERIFY2(scene.create(QStringLiteral("classic"), true, false, false), qPrintable(scene.errors));
    QTRY_VERIFY(!scene.app->currentRun().value(QStringLiteral("run")).toMap().isEmpty());
    scene.overlay()->show();
    QVERIFY(QTest::qWaitForWindowExposed(scene.floating()));
    // Empty when the row shows the icon and no badge; otherwise what is wrong.
    const auto categoryOf = [&](const QString &rowName) {
        QQuickItem *row = scene.item(rowName, true);
        if (!row)
            return QStringLiteral("%1 is not shown").arg(rowName);
        auto *icon = row->findChild<QQuickItem *>(QStringLiteral("reminderCategoryIcon"));
        if (!icon || !icon->isVisible())
            return QStringLiteral("%1 shows no category icon").arg(rowName);
        for (auto *item : row->findChildren<QQuickItem *>()) {
            if (item->isVisible() && item->property("text").toString() == QString::fromUtf8("迷"))
                return QStringLiteral("%1 still shows the badge").arg(rowName);
        }
        return QString();
    };
    QTRY_COMPARE(categoryOf(QStringLiteral("floatingCurrentDuty")), QString());
    scene.overlay()->setForm(QStringLiteral("mini"));
    QTRY_COMPARE(categoryOf(QStringLiteral("floatingMiniRun")), QString());
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

// The mini bar draws nothing under its second line and pads it like the first:
// the prototype's pace line along the bottom edge is not drawn (owner,
// 2026-10-28); the time against the average stays in the full float's 副本用时
// module. The window is exactly the bar and its 1 px frame, idle and in a duty.
void StatisticsOverlayTests::miniBarDrawsNothingBelowTheRunRow()
{
    QVariantMap history = completedRun();
    history.insert(QStringLiteral("content_id"), 17);
    history.insert(QStringLiteral("duration_ms"), 1200000);
    Scene scene;
    scene.backend.setLiveMode(mr::MockBackend::LiveMode::None);
    scene.backend.resetRuns({QJsonObject::fromVariantMap(history)});
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    scene.overlay()->show();
    QVERIFY(QTest::qWaitForWindowExposed(scene.floating()));
    scene.overlay()->setForm(QStringLiteral("mini"));
    QTRY_COMPARE(scene.overlay()->form(), QStringLiteral("mini"));
    QQuickItem *bar = scene.item(QStringLiteral("floatingMiniContent"), true);
    QVERIFY(bar);
    QTRY_VERIFY(!scene.floating()->property("transitioning").toBool());
    QTRY_COMPARE(scene.floating()->height(), qRound(bar->height()) + 2);
    scene.backend.setLiveMode(mr::MockBackend::LiveMode::Entered);
    QTRY_COMPARE(scene.app->currentRunState(), QStringLiteral("ENTERED_DUTY"));
    // The run has an average to measure against: the full float would show its pace bar.
    QTRY_VERIFY(scene.overlayData()->property("hasReference").toBool());
    QTRY_VERIFY(scene.item(QStringLiteral("floatingMiniRun"), true));
    QTRY_VERIFY(bar->height() > 60); // the second line has been laid out
    QTRY_COMPARE(scene.floating()->height(), qRound(bar->height()) + 2);
    QQuickItem *progress = scene.item(QStringLiteral("floatingMiniProgress"), true);
    QQuickItem *run = scene.item(QStringLiteral("floatingMiniRun"), true);
    QVERIFY(progress && run);
    const qreal runBottom = run->mapToItem(bar, QPointF(0, run->height())).y();
    QStringList below;
    for (auto *item : bar->findChildren<QQuickItem *>()) {
        if (!item->isVisible() || item->width() <= 0 || item->height() <= 0) continue;
        const qreal top = item->mapToItem(bar, QPointF(0, 0)).y();
        if (top >= runBottom - 0.5)
            below << QStringLiteral("%1 \"%2\" at y %3").arg(QString::fromLatin1(item->metaObject()->className()), item->objectName()).arg(top);
    }
    QVERIFY2(below.isEmpty(), qPrintable(QStringLiteral("drawn below the run row: ") + below.join(QStringLiteral("; "))));
    const qreal topPadding = progress->mapToItem(bar, QPointF(0, 0)).y();
    QCOMPARE(bar->height() - runBottom, topPadding);
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

// Duty names and categories may come from an imported file, and a toast may
// carry a duty name: the float's legend and tips and the toast show such text
// as written, never as markup that could fetch an image (audit 2026-10-20,
// L3-4 / L2-3).
void StatisticsOverlayTests::importedTextIsShownAsWritten()
{
    const QString markup = QStringLiteral("<b>x</b>");
    Scene scene;
    scene.backend.setLiveMode(mr::MockBackend::LiveMode::None);
    QVariantMap run = completedRun();
    run.insert(QStringLiteral("duty_category"), markup);
    // A duty the catalogue knows: runs without any duty identity now fall into the one
    // unnamed 未知副本 row and would not show their own text.
    run.insert(QStringLiteral("content_id"), 17);
    scene.backend.resetRuns({QJsonObject::fromVariantMap(run)});
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    scene.overlay()->setLabels(true);
    scene.overlay()->show();
    QVERIFY(QTest::qWaitForWindowExposed(scene.floating()));
    // 文字标签 puts the category's name in the legend.
    QTRY_VERIFY(!scene.textItemsContaining(markup).isEmpty());
    for (auto *text : scene.textItemsContaining(markup))
        QCOMPARE(text->property("textFormat").toInt(), int(Qt::PlainText));
    // The legend, the stacked bar and the ranking name it in their tips.
    const auto tips = scene.tipsContaining(markup);
    QVERIFY(tips.size() >= 3);
    for (QObject *tip : tips) {
        auto *label = tip->property("contentItem").value<QQuickItem *>();
        QVERIFY(label);
        QCOMPARE(label->property("textFormat").toInt(), int(Qt::PlainText));
    }

    QQmlComponent component(&scene.engine);
    component.setData(R"(import QtQuick
import MentorRecorder
Item {
    width: 600; height: 200
    Toast { objectName: "toast"; message: "<b>x</b>" }
    Text { objectName: "reference"; text: "<b>x</b>"; textFormat: Text.PlainText; font.pixelSize: Theme.fs(13) }
})", QUrl());
    std::unique_ptr<QObject> probe(component.create());
    QVERIFY2(probe, qPrintable(component.errorString()));
    auto *toast = probe->findChild<QQuickItem *>(QStringLiteral("toast"));
    auto *reference = probe->findChild<QQuickItem *>(QStringLiteral("reference"));
    QVERIFY(toast && reference);
    QQuickItem *message = nullptr;
    for (auto *child : toast->childItems())
        if (child->inherits("QQuickText")) message = child;
    QVERIFY(message);
    QCOMPARE(message->property("textFormat").toInt(), int(Qt::PlainText));
    QCOMPARE(message->property("contentWidth").toReal(), reference->property("contentWidth").toReal());
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

// The "+N" pill counts completions. A new 起始次数 moves the progress too, but
// it is no completion (audit 2026-10-20, L3-5).
void StatisticsOverlayTests::plusPillCountsCompletionsNotBaselineEdits()
{
    Scene scene;
    scene.backend.setLiveMode(mr::MockBackend::LiveMode::None);
    scene.backend.resetRuns({});
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    scene.overlay()->show();
    QVERIFY(QTest::qWaitForWindowExposed(scene.floating()));
    QTRY_VERIFY(scene.app->achievementSettingsLoaded());
    QObject *data = scene.overlayData();
    QVERIFY(data);
    const int start = data->property("progress").toInt();
    QTRY_COMPARE(data->property("observedProgress").toInt(), start);

    scene.app->updateAchievementBaseline(scene.app->goalCount(), scene.app->baselineCount() + 300,
                                         QStringLiteral("test"));
    QTRY_COMPARE(data->property("progress").toInt(), start + 300);
    QTest::qWait(60);
    QCOMPARE(data->property("plus").toInt(), 0);

    scene.backend.resetRuns({QJsonObject::fromVariantMap(completedRun())});
    scene.app->refreshDashboard();
    QTRY_COMPARE(data->property("progress").toInt(), start + 301);
    QCOMPARE(data->property("plus").toInt(), 1);
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void StatisticsOverlayTests::entryButtonFollowsPageTheme_data()
{
    QTest::addColumn<QString>("style");
    for (const auto &style : {QStringLiteral("classic"), QStringLiteral("eorzea"), QStringLiteral("harendotes")})
        QTest::newRow(qPrintable(style)) << style;
}

void StatisticsOverlayTests::entryButtonFollowsPageTheme()
{
    QFETCH(QString, style);
    Scene scene;
    scene.backend.setRecordingFixture(QStringLiteral("listening"));
    QVERIFY2(scene.create(style, true, true), qPrintable(scene.errors));
    QVERIFY(QTest::qWaitForWindowExposed(scene.mainWindow()));
    auto *button = scene.item(QStringLiteral("statisticsFloatingWindowButton"));
    QVERIFY(button);
    auto *background = button->property("background").value<QQuickItem *>();
    QVERIFY(background);
    QTest::mouseMove(scene.mainWindow(), QPoint(2, scene.mainWindow()->height() - 2));
    const QString output = QString::fromUtf8(MR_SOURCE_DIR) + kEvidence() + QStringLiteral("/entry");
    QVERIFY(QDir().mkpath(output));
    for (bool dark : {true, false}) {
        scene.app->setThemeMode(dark ? QStringLiteral("dark") : QStringLiteral("light"));
        const auto followsTheme = [&] {
            const QColor color = background->property("color").value<QColor>();
            const double luma = (0.2126 * color.redF()) + (0.7152 * color.greenF()) + (0.0722 * color.blueF());
            return dark ? luma < 0.5 : luma > 0.5;
        };
        QTRY_VERIFY2(followsTheme(), qPrintable(background->property("color").value<QColor>().name()));
        QTest::qWait(40);
        QVERIFY(scene.mainWindow()->grabWindow().save(output + QStringLiteral("/entry-%1-%2.png")
                    .arg(style, dark ? QStringLiteral("dark") : QStringLiteral("light"))));
    }
    QVERIFY(scene.click(QStringLiteral("statisticsFloatingWindowButton")));
    QTRY_VERIFY(scene.overlay()->visible());
    scene.overlay()->setLocked(true);
    QVERIFY(scene.click(QStringLiteral("statisticsFloatingWindowButton")));
    QTRY_VERIFY(!scene.overlay()->locked());
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void StatisticsOverlayTests::mainWindowCanUnlockAndMinimizeIndependently()
{
    Scene scene;
    // Automatic recording usable, as in entryButtonFollowsPageTheme: the default mock's
    // profile opens the start-up 「无法自动记录」 notice, which would take the clicks.
    scene.backend.setRecordingFixture(QStringLiteral("listening"));
    QVERIFY2(scene.create(QStringLiteral("harendotes"), true, true), qPrintable(scene.errors));
    QVERIFY(QTest::qWaitForWindowExposed(scene.mainWindow()));
    QVERIFY(scene.click(QStringLiteral("statisticsFloatingWindowButton")));
    QTRY_VERIFY(scene.floating()->isVisible());
    scene.overlay()->setLocked(true);
    QVERIFY(scene.click(QStringLiteral("statisticsFloatingWindowButton")));
    QTRY_VERIFY(!scene.overlay()->locked());
    scene.mainWindow()->showMinimized();
    QTRY_VERIFY(scene.mainWindow()->visibility() == QWindow::Minimized);
    QVERIFY(scene.floating()->isVisible());
    scene.mainWindow()->showNormal();
    scene.overlay()->hide();
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void StatisticsOverlayTests::sharedSnapshotsRefreshWithoutOpeningTheMainPage()
{
    Scene scene;
    scene.backend.setLiveMode(mr::MockBackend::LiveMode::None);
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    scene.overlay()->show();
    QTRY_VERIFY(scene.overlay()->dungeons()->rowCountProperty() > 0 && scene.overlay()->jobs()->rowCountProperty() > 0);
    auto *state = scene.item(QStringLiteral("floatingDungeonState"));
    QVERIFY(state);
    QTRY_COMPARE(state->property("text").toString(), QStringLiteral("%1/%2")
                 .arg(scene.overlay()->dungeons()->totalCompletedCount()).arg(scene.overlay()->dungeons()->totalAttemptCount()));
    QSignalSpy refreshed(scene.overlay()->dungeons(), &mr::StatsRowsModel::countChanged);
    scene.app->refreshDashboard();
    QTRY_VERIFY(refreshed.count() > 0);
    scene.overlay()->setRowLimit(0);
    scene.overlay()->setModules({"current_duty", "elapsed", "progress"});
    scene.backend.setLiveMode(mr::MockBackend::LiveMode::Entered);
    QTRY_VERIFY(!scene.app->currentRun().value(QStringLiteral("run")).toMap().isEmpty());
    QTRY_VERIFY(scene.item(QStringLiteral("floatingCurrentDuty"), true));
    auto *elapsed = scene.item(QStringLiteral("floatingElapsed"), true);
    QVERIFY(elapsed);
    QVERIFY(elapsed->property("text").toString().contains(QLatin1Char(':')));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void StatisticsOverlayTests::attemptCountRefreshesWithDashboardChanges()
{
    Scene scene;
    scene.backend.setLiveMode(mr::MockBackend::LiveMode::None);
    scene.backend.resetRuns(datedAttemptRuns());
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    scene.overlay()->setAttemptPeriod(QStringLiteral("today"));
    scene.overlay()->show();
    QTRY_VERIFY(scene.overlay()->attemptsKnown());
    QTRY_COMPARE(scene.overlay()->attemptCount(), 1);
    scene.backend.resetRuns({});
    scene.app->refreshDashboard();
    QTRY_VERIFY(scene.overlay()->attemptsKnown());
    QTRY_COMPARE(scene.overlay()->attemptCount(), 0);
    QCOMPARE(scene.overlay()->attemptPeriod(), QStringLiteral("today"));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void StatisticsOverlayTests::attemptPeriodsUsePointerKeyboardAndKeepOtherStatistics()
{
    Scene scene;
    scene.backend.setLiveMode(mr::MockBackend::LiveMode::None);
    scene.backend.resetRuns(datedAttemptRuns());
    QVERIFY2(scene.create(QStringLiteral("workbench"), false), qPrintable(scene.errors));
    scene.overlay()->show();
    QVERIFY(QTest::qWaitForWindowExposed(scene.floating()));
    QTRY_VERIFY(scene.overlay()->attemptsKnown());
    QTRY_COMPARE(scene.app->dungeons()->totalAttemptCount(), 4);
    QTRY_COMPARE(scene.app->jobs()->totalAttemptCount(), 4);
    const QVariantMap dashboard = scene.app->dashboard();
    auto *progressItem = scene.item(QStringLiteral("floatingAchievementProgress"));
    QVERIFY(progressItem);
    const auto progress = progressItem->property("text");
    const struct { const char *key; int count; } periods[] = {
        {.key="today", .count=1}, {.key="last7", .count=2}, {.key="last30", .count=3}, {.key="all", .count=4}};
    for (const auto &period : periods) {
        const QString key = QString::fromLatin1(period.key);
        QVERIFY(scene.click(QStringLiteral("floatingAttemptPeriod_") + key));
        QTRY_COMPARE(scene.overlay()->attemptPeriod(), key);
        QTRY_VERIFY(scene.overlay()->attemptsKnown());
        QTRY_COMPARE(scene.item(QStringLiteral("floatingAttemptCount"))->property("text").toString(), QString::number(period.count));
        verifyAttemptPeriodStatistics(scene, period.count);
        QVERIFY(!QTest::currentTestFailed());
        QDir().mkpath(QString::fromUtf8(MR_SOURCE_DIR) + QStringLiteral("/artifacts/overlay-statistics-periods-20261022"));
        QVERIFY(scene.floating()->grabWindow().save(QString::fromUtf8(MR_SOURCE_DIR)
                + QStringLiteral("/artifacts/overlay-statistics-periods-20261022/") + key + QStringLiteral(".png")));
        QCOMPARE(scene.app->dashboard(), dashboard);
        QCOMPARE(scene.item(QStringLiteral("floatingAchievementProgress"))->property("text"), progress);
        QCOMPARE(scene.app->dungeons()->totalAttemptCount(), 4);
        QCOMPARE(scene.app->jobs()->totalAttemptCount(), 4);
        QVERIFY(scene.item(QStringLiteral("floatingAttemptPeriod_") + key)->property("checked").toBool());
        QVERIFY(scene.click(QStringLiteral("floatingAttemptPeriod_") + key));
        QVERIFY(scene.item(QStringLiteral("floatingAttemptPeriod_") + key)->property("checked").toBool());
    }
    verifyAttemptPeriodKeyboard(scene);
    QVERIFY(!QTest::currentTestFailed());
    scene.overlay()->hide();
    scene.overlay()->show();
    QTRY_COMPARE(scene.overlay()->attemptPeriod(), QStringLiteral("today"));
    scene.overlay()->setModules({QStringLiteral("progress")});
    QTRY_VERIFY(!scene.item(QStringLiteral("floatingAttemptPeriods"), true));
    QVERIFY(!scene.item(QStringLiteral("floatingModule_attempts"), true));
    for (const auto &module : {QStringLiteral("dungeons"), QStringLiteral("jobs")}) {
        scene.overlay()->setModules({module});
        QTRY_VERIFY(scene.item(QStringLiteral("floatingAttemptPeriods"), true));
        QVERIFY(scene.click(QStringLiteral("floatingAttemptPeriod_last7")));
        if (module == QLatin1String("dungeons"))
            QTRY_COMPARE(scene.overlay()->dungeons()->totalAttemptCount(), 2);
        else
            QTRY_COMPARE(scene.overlay()->jobs()->totalAttemptCount(), 2);
        QVERIFY(scene.click(QStringLiteral("floatingAttemptPeriod_today")));
    }
    scene.overlay()->setModules({QStringLiteral("attempts"), QStringLiteral("progress")});
    QTRY_VERIFY(scene.item(QStringLiteral("floatingAttemptPeriods"), true));
    scene.overlay()->setForm(QStringLiteral("mini"));
    QTRY_VERIFY(!scene.item(QStringLiteral("floatingAttemptPeriods"), true));
    QVERIFY(scene.item(QStringLiteral("floatingMiniProgress"), true));
    QCOMPARE(scene.app->dashboard(), dashboard);
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void StatisticsOverlayTests::periodStatisticsHideOldChartsWhileLoadingAndOnFailure()
{
    Scene scene;
    scene.backend.setLiveMode(mr::MockBackend::LiveMode::None);
    scene.backend.resetRuns(datedAttemptRuns());
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    scene.overlay()->setModules({QStringLiteral("dungeons"), QStringLiteral("jobs")});
    scene.overlay()->show();
    QTRY_COMPARE(scene.item(QStringLiteral("floatingDungeonState"))->property("text").toString(), QStringLiteral("4/4"));
    scene.backend.holdRows = true;
    scene.overlay()->setAttemptPeriod(QStringLiteral("today"));
    QTRY_COMPARE(scene.backend.pendingRows.size(), 2);
    QTRY_COMPARE(scene.item(QStringLiteral("floatingDungeonState"))->property("text").toString(), QString::fromUtf8("读取中…"));
    QTRY_COMPARE(scene.item(QStringLiteral("floatingJobMessage"))->property("text").toString(), QString::fromUtf8("读取中…"));
    QVERIFY(!scene.item(QStringLiteral("floatingDungeonStatistics"))->property("ready").toBool());
    QVERIFY(!scene.item(QStringLiteral("floatingJobStatistics"))->property("ready").toBool());
    QVERIFY(qmlList(scene.item(QStringLiteral("floatingDungeonStatistics"))->property("rows")).isEmpty());
    QVERIFY(qmlList(scene.item(QStringLiteral("floatingDungeonStatistics"))->property("buckets")).isEmpty());
    QVERIFY(qmlList(scene.item(QStringLiteral("floatingJobStatistics"))->property("columns")).isEmpty());
    // A second selection invalidates both pending model requests.
    scene.overlay()->setAttemptPeriod(QStringLiteral("last7"));
    QCOMPARE(scene.backend.pendingRows.size(), 4);
    scene.backend.pendingRows.at(0)->fail(QStringLiteral("OLD"), QStringLiteral("stale failure"));
    QVERIFY(scene.overlay()->dungeons()->loadError().isEmpty());
    QVERIFY(scene.overlay()->dungeons()->isLoading());
    scene.backend.pendingRows.at(2)->fail(QStringLiteral("ERR_TEST"), QStringLiteral("current failure"));
    scene.backend.pendingRows.at(3)->fail(QStringLiteral("ERR_TEST"), QStringLiteral("current failure"));
    QTRY_COMPARE(scene.item(QStringLiteral("floatingDungeonState"))->property("text").toString(), QStringLiteral("current failure"));
    QTRY_COMPARE(scene.item(QStringLiteral("floatingJobMessage"))->property("text").toString(), QStringLiteral("current failure"));
    QVERIFY(qmlList(scene.item(QStringLiteral("floatingDungeonStatistics"))->property("rows")).isEmpty());
    QVERIFY(qmlList(scene.item(QStringLiteral("floatingDungeonStatistics"))->property("buckets")).isEmpty());
    QVERIFY(qmlList(scene.item(QStringLiteral("floatingJobStatistics"))->property("columns")).isEmpty());
    scene.backend.holdRows = false;
    scene.backend.resetRuns({});
    scene.overlay()->refreshAttempts();
    QTRY_COMPARE(scene.item(QStringLiteral("floatingDungeonState"))->property("text").toString(), QStringLiteral("0/0"));
    QTRY_COMPARE(scene.item(QStringLiteral("floatingJobMessage"))->property("text").toString(), QString::fromUtf8("暂无统计"));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void StatisticsOverlayTests::attemptCountDistinguishesLoadingFailureAndZero()
{
    Scene scene;
    scene.backend.setLiveMode(mr::MockBackend::LiveMode::None);
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    QTRY_VERIFY(scene.app->achievementSettingsLoaded());
    scene.backend.holdDashboard = true;
    scene.overlay()->show();
    QVERIFY(QTest::qWaitForWindowExposed(scene.floating()));
    QTRY_COMPARE(scene.backend.pending.size(), 1);
    QTRY_VERIFY(scene.item(QStringLiteral("floatingAttemptsLoading"), true));
    QVERIFY(!scene.overlay()->attemptsKnown());
    QVERIFY(scene.item(QStringLiteral("floatingAttemptCount"))->property("text").toString() != QLatin1String("0"));
    QVERIFY(scene.grabAttempts(QStringLiteral("loading")));
    auto pending = scene.backend.pending.takeFirst();
    QVERIFY(pending);
    pending->fail(QStringLiteral("ERR_TEST"), QStringLiteral("Test connection failed"));
    QTRY_VERIFY(scene.item(QStringLiteral("floatingAttemptsError"), true));
    QVERIFY(scene.item(QStringLiteral("floatingAttemptsRetry"), true));
    QVERIFY(!scene.item(QStringLiteral("floatingAttemptsLoading"), true));
    QVERIFY(scene.grabAttempts(QStringLiteral("loading-failure")));
    const struct { const char *language; const char *message; } failures[] = {
        {.language="en", .message="Could not load attempts. Try again."},
        {.language="ja", .message="挑戦回数を読み込めませんでした。もう一度お試しください。"},
        {.language="zh-TW", .message="排本次數讀取失敗，請重試。"},
        {.language="zh-CN", .message="排本次数读取失败，请重试"}};
    for (const auto &failure : failures) {
        scene.language.setLanguage(QString::fromLatin1(failure.language));
        QTRY_COMPARE(scene.item(QStringLiteral("floatingAttemptsError"))->property("text").toString(),
                     QString::fromUtf8(failure.message));
        QVERIFY(scene.grabAttempts(QStringLiteral("failure-") + QString::fromLatin1(failure.language)));
    }
    QVERIFY(scene.click(QStringLiteral("floatingAttemptsRetry")));
    QTRY_COMPARE(scene.backend.pending.size(), 1);
    QTRY_VERIFY(scene.item(QStringLiteral("floatingAttemptsLoading"), true));
    pending = scene.backend.pending.takeFirst();
    QVERIFY(pending);
    pending->succeed({{QStringLiteral("attempt_count"), 0}});
    QTRY_COMPARE(scene.item(QStringLiteral("floatingAttemptCount"))->property("text").toString(), QStringLiteral("0"));
    QVERIFY(scene.overlay()->attemptsKnown());
    QVERIFY(!scene.item(QStringLiteral("floatingAttemptsError"), true));
    QVERIFY(!scene.item(QStringLiteral("floatingAttemptsRetry"), true));
    QVERIFY(!scene.item(QStringLiteral("floatingAttemptsLoading"), true));
    QVERIFY(scene.grabAttempts(QStringLiteral("known-zero")));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void StatisticsOverlayTests::attemptRangeRenderedEvidence_data()
{
    QTest::addColumn<QString>("style");
    QTest::addColumn<bool>("dark");
    QTest::addColumn<QString>("language");
    for (const auto &style : {QStringLiteral("classic"), QStringLiteral("eorzea"), QStringLiteral("harendotes")})
        for (bool dark : {false, true})
            for (const auto &language : {QStringLiteral("zh-CN"), QStringLiteral("en"), QStringLiteral("ja"), QStringLiteral("zh-TW")})
                QTest::newRow(qPrintable(style + (dark ? "-dark-" : "-light-") + language)) << style << dark << language;
}

void StatisticsOverlayTests::attemptRangeRenderedEvidence()
{
    QFETCH(QString, style);
    QFETCH(bool, dark);
    QFETCH(QString, language);
    Scene scene;
    scene.backend.setLiveMode(mr::MockBackend::LiveMode::None);
    QVERIFY2(scene.create(style, dark), qPrintable(scene.errors));
    scene.language.setLanguage(language);
    scene.overlay()->setGeometry(QRect(80, 80, 240, 560));
    scene.overlay()->show();
    QVERIFY(QTest::qWaitForWindowExposed(scene.floating()));
    QTRY_COMPARE(scene.floating()->width(), 240);
    QTRY_VERIFY(scene.overlay()->attemptsKnown());
    for (const QVariant &entry : scene.overlay()->attemptPeriodOptions()) {
        const QVariantMap period = entry.toMap();
        const QString name = QStringLiteral("floatingAttemptPeriod_") + period.value(QStringLiteral("key")).toString();
        QQuickItem *button = scene.item(name, true);
        QQuickItem *label = scene.item(name + QStringLiteral("Label"), true);
        QVERIFY(button && label);
        QTRY_VERIFY(button->width() > 0 && label->height() >= label->property("contentHeight").toReal());
        QVERIFY2(label->property("contentWidth").toReal() <= label->width() + 0.5, qPrintable(name));
        const QRectF bounds(button->mapToScene(QPointF()), button->size());
        QVERIFY2(QRectF(QPointF(), scene.floating()->size()).contains(bounds), qPrintable(name));
        QCOMPARE(button->property("text").toString(), period.value(QStringLiteral("title")).toString());
    }
    const QString prefix = QStringLiteral("240-%1-%2-%3").arg(style, dark ? QStringLiteral("dark") : QStringLiteral("light"), language);
    QVERIFY(scene.grabAttempts(prefix));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void StatisticsOverlayTests::renderedEvidence_data()
{
    QTest::addColumn<QString>("style");
    QTest::addColumn<bool>("dark");
    for (const auto &style : {QStringLiteral("classic"), QStringLiteral("eorzea"), QStringLiteral("harendotes")})
        for (bool dark : {false, true})
            QTest::newRow(qPrintable(style + (dark ? "-dark" : "-light"))) << style << dark;
}

void StatisticsOverlayTests::trayRestoresWithoutLosingMaximizedState()
{
    QWindow window;
    mr::TrayController tray(nullptr, nullptr, &window);
    window.showMaximized();
    QTRY_COMPARE(window.windowState(), Qt::WindowMaximized);
    window.hide();
    tray.showWindow();
    QVERIFY(window.isVisible());
    QCOMPARE(window.windowState(), Qt::WindowMaximized);
    tray.showWindow();
    QCOMPARE(window.windowState(), Qt::WindowMaximized);
    window.showMinimized();
    QTRY_COMPARE(window.windowState(), Qt::WindowMinimized);
    tray.showWindow();
    QTRY_COMPARE(window.windowState(), Qt::WindowNoState);
}

void StatisticsOverlayTests::nativeInputStylesAndTrayRecovery()
{
#ifdef Q_OS_WIN
    if (QGuiApplication::platformName() != QLatin1String("windows"))
        QSKIP("Native Windows styles and tray require the windows platform; run this case separately.");
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    scene.overlay()->show();
    QVERIFY(QTest::qWaitForWindowExposed(scene.floating()));
    scene.overlay()->setLocked(true);
    QTest::qWait(40);
    auto nativeStyle = [&] { return GetWindowLongPtr(std::bit_cast<HWND>(scene.floating()->winId()), GWL_EXSTYLE); };
    QVERIFY(nativeStyle() & WS_EX_TRANSPARENT);
    QVERIFY(nativeStyle() & WS_EX_NOACTIVATE);
    QVERIFY(nativeStyle() & WS_EX_TOPMOST);
    QCOMPARE(GetWindow(std::bit_cast<HWND>(scene.floating()->winId()), GW_OWNER), HWND(nullptr));
    mr::TrayController tray(scene.app.get(), &scene.settings, scene.mainWindow());
    QVERIFY2(tray.isActive(), "Native tray unavailable; native overlay assertions above still ran.");
    QAction *toggle = nullptr;
    QAction *unlock = nullptr;
    QAction *pending = nullptr;
    for (auto *widget : QApplication::allWidgets()) {
        if (auto *menu = qobject_cast<QMenu *>(widget)) {
            if (auto *action = menu->findChild<QAction *>(QStringLiteral("statisticsFloatingWindowAction"))) toggle = action;
            if (auto *action = menu->findChild<QAction *>(QStringLiteral("unlockStatisticsFloatingWindowAction"))) unlock = action;
            if (auto *action = menu->findChild<QAction *>(QStringLiteral("pendingWorkAction"))) pending = action;
        }
    }
    QVERIFY(toggle && unlock && unlock->isVisible() && pending);
    unlock->trigger();
    QTRY_VERIFY(!scene.overlay()->locked());
    QVERIFY(!(nativeStyle() & WS_EX_TRANSPARENT));
    QVERIFY(!(nativeStyle() & WS_EX_NOACTIVATE));
    scene.mainWindow()->hide();
    QVERIFY(scene.floating()->isVisible());
    toggle->trigger();
    QTRY_VERIFY(!scene.floating()->isVisible());
    toggle->trigger();
    QTRY_VERIFY(scene.floating()->isVisible());
    QVERIFY(!scene.overlay()->locked());
    QSignalSpy pendingRequested(scene.app->recovery(), &mr::RecoveryController::inboxRequested);
    scene.overlay()->setLocked(true);
    scene.mainWindow()->hide();
    pending->trigger();
    QTRY_COMPARE(pendingRequested.count(), 1);
    QVERIFY(scene.mainWindow()->isVisible());
    QVERIFY(scene.overlay()->locked());
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
#else
    QSKIP("Windows-only native overlay styles.");
#endif
}

void StatisticsOverlayTests::renderedEvidence()
{
    QFETCH(QString, style);
    QFETCH(bool, dark);
    Scene scene;
    scene.backend.setLiveMode(mr::MockBackend::LiveMode::None);
    QVERIFY2(scene.create(style, dark), qPrintable(scene.errors));
    scene.overlay()->setGeometry(QRect(80, 80, 300, 560));
    scene.overlay()->show();
    QVERIFY(QTest::qWaitForWindowExposed(scene.floating()));
    QTRY_VERIFY(scene.app->dungeons()->rowCountProperty() > 0);
    const QString prefix = style + (dark ? "-dark" : "-light");
    QVERIFY(scene.grab(prefix + QStringLiteral("-idle")));
    scene.overlay()->setLabels(true);
    QVERIFY(scene.grab(prefix + QStringLiteral("-labels")));
    scene.overlay()->setLabels(false);
    QVERIFY(scene.click(QStringLiteral("floatingCustomizeButton")));
    QTest::mouseMove(scene.floating(), QPoint(2, 2));
    QTRY_VERIFY(scene.floating()->property("customizing").toBool());
    QVERIFY(scene.grab(prefix + QStringLiteral("-customize")));
    QVERIFY(scene.revealCustomizationFooter());
    QVERIFY(scene.click(QStringLiteral("floatingCustomizationDone")));
    QTRY_VERIFY(!scene.floating()->property("customizing").toBool());
    scene.language.setLanguage(QStringLiteral("en"));
    QTest::qWait(80);
    QVERIFY(scene.grab(prefix + QStringLiteral("-english")));
    const auto first = scene.app->dungeons()->topRows(1).first().toMap();
    const QString englishDuty = mr::GameNames::shared()->duty(first.value(QStringLiteral("content_id")), first.value(QStringLiteral("duty_name")).toString());
    QVERIFY2(scene.texts().contains(englishDuty), qPrintable(scene.texts().join(QLatin1Char('\n'))));
    scene.language.setLanguage(QStringLiteral("zh-CN"));
    scene.backend.setLiveMode(mr::MockBackend::LiveMode::Entered);
    QTRY_VERIFY(!scene.app->currentRun().value(QStringLiteral("run")).toMap().isEmpty());
    QTRY_VERIFY(scene.item(QStringLiteral("floatingModule_current_duty"), true));
    QVERIFY(scene.grab(prefix + QStringLiteral("-in-duty")));
    scene.overlay()->setForm(QStringLiteral("mini"));
    QTRY_COMPARE(scene.floating()->width(), 300);
    QVERIFY(scene.grab(prefix + QStringLiteral("-mini")));
    scene.overlay()->setLocked(true);
    QVERIFY(scene.grab(prefix + QStringLiteral("-mini-locked")));
    scene.overlay()->unlock();
    scene.overlay()->setForm(QStringLiteral("full"));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

MR_DESKTOP_TEST_SUITE(StatisticsOverlay)
{
    // The threaded loop owns a competing GUI animation driver. Capture-only
    // basic rendering permits our manual clock; normal validation is unchanged.
    if (qEnvironmentVariableIsSet("MR_OVERLAY_CAPTURE_FRAMES"))
        qputenv("QSG_RENDER_LOOP", "basic");
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QApplication app(argc, argv);
    // Opt-in image evidence is a separate process from native-cadence validation.
    // Qt Quick installs its own driver when its first window is created.
    std::unique_ptr<FormEvidenceClock> evidenceClock;
    if (qEnvironmentVariableIsSet("MR_OVERLAY_CAPTURE_FRAMES")) {
        evidenceClock = std::make_unique<FormEvidenceClock>();
        formEvidenceClock = evidenceClock.get();
    }
    if (!mr::registerBundledFonts()) return 2;
    QApplication::setFont(QFont(QStringLiteral("Noto Sans SC")));
    StatisticsOverlayTests tests;
    const int result = QTest::qExec(&tests, argc, argv);
    formEvidenceClock = nullptr;
    return result;
}

#include "StatisticsOverlayTests.moc"
