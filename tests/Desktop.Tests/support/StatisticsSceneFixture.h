// The scene the statistics float and the 副本提醒与导随宏 suites share: shipping QML from
// src/Desktop/qml over a real AppController, on a MockBackend whose dashboard and table reads a
// test can hold back - either the float alone or the whole main window (Main.qml). Everything
// is in an anonymous namespace: each suite gets its own copy inside its test executable.
#pragma once

#include "AppController.h"
#include "AppSettings.h"
#include "Formatters.h"
#include "GameNames.h"
#include "JobCatalog.h"
#include "LanguageManager.h"
#include "MockBackend.h"
#include "QmlComponentTestSupport.h"
#include "RoleCatalog.h"
#include "RunFormValidator.h"
#include "StatisticsOverlayController.h"

#include <QByteArray>
#include <QJsonObject>
#include <QList>
#include <QPointer>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickWindow>
#include <QRect>
#include <QTemporaryDir>
#include <QTest>
#include <QTimer>
#include <QUrl>
#include <QVariant>
#include <QtTest/qtestwheel.h>

#include <algorithm>
#include <memory>

namespace {

/// Registers the shipping QML the way `import MentorRecorder` finds it in the application
/// (mrtest::registerShippingQml); the name the scene's suites call.
void registerSceneQmlTypes()
{
    mrtest::registerShippingQml();
}

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

using mrtest::qmlList;

// Hold only dashboard reads explicitly armed after startup, so the actual
// window can exercise pending/failure/retry/zero without mocking its bindings.
class OverlayReplyBackend : public mr::MockBackend
{
public:
    bool holdDashboard = false;
    bool holdRows = false;
    /// A region whose own dashboard reads stay unanswered.
    QString holdRegion;
    QList<QPointer<mr::BackendReply>> pending;
    QList<QPointer<mr::BackendReply>> pendingRows;
    QJsonObject currentRunOverride;
    QJsonObject lastCommunitySubmission;
    bool holdCommunitySubmission = false;
    bool holdCommunityBrowse = false;
    bool failCommunityList = false;
    QList<QPointer<mr::BackendReply>> pendingCommunitySubmissions;
    QList<QPointer<mr::BackendReply>> pendingCommunityBrowse;

    mr::BackendReply *request(const QString &messageType, const QJsonObject &payload = {}) override
    {
        if (messageType == QLatin1String("DutyCommunity")) {
            const QString action = payload.value(QStringLiteral("action")).toString();
            if (action == QLatin1String("submit") || action == QLatin1String("prepare_issue")
                || action == QLatin1String("prepare_withdrawal")) {
                lastCommunitySubmission = payload;
                if (holdCommunitySubmission) {
                    auto *reply = new mr::BackendReply(QStringLiteral("fixture-community-submission"), messageType, this);
                    pendingCommunitySubmissions.append(reply);
                    return reply;
                }
            }
            if (holdCommunityBrowse && action == QLatin1String("browse")) {
                auto *reply = new mr::BackendReply(QStringLiteral("fixture-community-browse"), messageType, this);
                pendingCommunityBrowse.append(reply);
                return reply;
            }
            if (failCommunityList && action == QLatin1String("list")) {
                auto *reply = new mr::BackendReply(QStringLiteral("fixture-community-list"), messageType, this);
                QTimer::singleShot(0, reply, [reply] {
                    reply->fail(QStringLiteral("test_list"), QStringLiteral("Fixture list failure"));
                });
                return reply;
            }
        }
        if (!currentRunOverride.isEmpty() && messageType == QLatin1String("GetCurrentRun")) {
            auto *reply = new mr::BackendReply(QStringLiteral("fixture-current-run"), messageType, this);
            QTimer::singleShot(0, reply, [reply, snapshot = currentRunOverride] { reply->succeed(snapshot); });
            return reply;
        }
        if (!holdRegion.isEmpty() && messageType == QLatin1String("GetDashboardStats")
            && payload.value(QStringLiteral("filter")).toObject().value(QStringLiteral("region")).toString() == holdRegion) {
            auto *reply = new mr::BackendReply(QString::number(pending.size()), messageType, this);
            pending.append(reply);
            return reply;
        }
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
    /// Shows the float and waits until its window is exposed.
    bool showFloating() const
    {
        overlay()->show();
        return QTest::qWaitForWindowExposed(floating());
    }
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

} // namespace
