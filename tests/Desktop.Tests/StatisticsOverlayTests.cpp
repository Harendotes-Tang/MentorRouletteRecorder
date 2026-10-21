#include "TestCollectorGuard.h"
#include "AppController.h"
#include "AppSettings.h"
#include "BundledFonts.h"
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
#include <QColor>
#include <QDateTime>
#include <QDir>
#include <QFont>
#include <QGuiApplication>
#include <QImage>
#include <QJsonArray>
#include <QJsonObject>
#include <QMenu>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QPointer>
#include <QSettings>
#include <QScreen>
#include <QSignalSpy>
#include <QStandardPaths>
#include <QTemporaryDir>
#include <QTest>
#include <QTimeZone>
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

// Hold only dashboard reads explicitly armed after startup, so the actual
// window can exercise pending/failure/retry/zero without mocking its bindings.
class OverlayReplyBackend : public mr::MockBackend
{
public:
    bool holdDashboard = false;
    QList<QPointer<mr::BackendReply>> pending;

    mr::BackendReply *request(const QString &messageType, const QJsonObject &payload = {}) override
    {
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
    for (const int daysAgo : {0, 3, 14, 45}) {
        QVariantMap run = completedRun(sequence++);
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
    void realWindowCanCustomizeAndRecoverEmptyContent();
    void customizePanelTogglesFromTheTitleBar_data();
    void customizePanelTogglesFromTheTitleBar();
    void customizationFooterRemainsReachableWithSevenModules();
    void miniBarSharesThePositionAndKeepsTheSize();
    void miniBarStaysWhereItWasDocked();
    void miniBarTipsOpenOutsideTheBar();
    void pendingRowOpensTheFirstRecord();
    void hiddenFloatKeepsItsEndlessAnimationsStill();
    void importedTextIsShownAsWritten();
    void plusPillCountsCompletionsNotBaselineEdits();
    void entryButtonFollowsPageTheme_data();
    void entryButtonFollowsPageTheme();
    void mainWindowCanUnlockAndMinimizeIndependently();
    void sharedSnapshotsRefreshWithoutOpeningTheMainPage();
    void attemptCountRefreshesWithDashboardChanges();
    void attemptPeriodsUsePointerKeyboardAndKeepOtherStatistics();
    void attemptCountDistinguishesLoadingFailureAndZero();
    void attemptRangeRenderedEvidence_data();
    void attemptRangeRenderedEvidence();
    void nativeInputStylesAndTrayRecovery();
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
    QTRY_COMPARE(scene.floating()->width(), 280);
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
    QTRY_COMPARE(scene.floating()->width(), 280);
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
    QTRY_COMPARE(scene.floating()->width(), 280);
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
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    scene.overlay()->show();
    QTRY_VERIFY(scene.app->dungeons()->rowCountProperty() > 0 && scene.app->jobs()->rowCountProperty() > 0);
    auto *state = scene.item(QStringLiteral("floatingDungeonState"));
    QVERIFY(state);
    QTRY_VERIFY(state->property("text").toString().contains(QString::number(scene.app->dungeons()->totalAttemptCount())));
    QVERIFY(state->property("text").toString().contains(QString::number(scene.app->dungeons()->totalCompletedCount())));
    QSignalSpy refreshed(scene.app->dungeons(), &mr::StatsRowsModel::countChanged);
    scene.app->dungeons()->reload();
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
    QVERIFY2(scene.create(), qPrintable(scene.errors));
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
    scene.overlay()->setModules({QStringLiteral("attempts"), QStringLiteral("progress")});
    QTRY_VERIFY(scene.item(QStringLiteral("floatingAttemptPeriods"), true));
    scene.overlay()->setForm(QStringLiteral("mini"));
    QTRY_VERIFY(!scene.item(QStringLiteral("floatingAttemptPeriods"), true));
    QVERIFY(scene.item(QStringLiteral("floatingMiniProgress"), true));
    QCOMPARE(scene.app->dashboard(), dashboard);
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
    QTRY_COMPARE(scene.floating()->width(), 280);
    QVERIFY(scene.grab(prefix + QStringLiteral("-mini")));
    scene.overlay()->setLocked(true);
    QVERIFY(scene.grab(prefix + QStringLiteral("-mini-locked")));
    scene.overlay()->unlock();
    scene.overlay()->setForm(QStringLiteral("full"));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

int main(int argc, char **argv)
{
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QApplication app(argc, argv);
    if (!mr::registerBundledFonts()) return 2;
    QApplication::setFont(QFont(QStringLiteral("Noto Sans SC")));
    StatisticsOverlayTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "StatisticsOverlayTests.moc"
