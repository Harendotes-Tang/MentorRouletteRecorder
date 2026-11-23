// The scene the update suites (UpdateNotice, UpdateInstall) share: one shipping QML page -
// the 总览 banner, 设置 · 通用 or 设置 · 关于 - over a real AppController on the
// deterministic MockBackend, and the release addresses both suites expect. The browser is
// replaced before anything is clicked: a test never opens github.com. Everything is in an
// anonymous namespace: each suite gets its own copy inside the shared test executable.
#pragma once

#include "AppController.h"
#include "AppSettings.h"
#include "Formatters.h"
#include "JobCatalog.h"
#include "MockBackend.h"
#include "QmlComponentTestSupport.h"
#include "RoleCatalog.h"
#include "UpdateController.h"

#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QStringList>
#include <QUrl>

#include <memory>

namespace {

constexpr auto kReleaseUrl =
    "https://github.com/Harendotes-Tang/MentorRouletteRecorder/releases/latest";
/// $defs/UpdateStatus.installer_url for 9.9.9, as the Collector builds it.
constexpr auto kInstallerUrl =
    "https://github.com/Harendotes-Tang/MentorRouletteRecorder/releases/download/v9.9.9/"
    "MentorRecorder-9.9.9-setup.exe";

struct UpdatePageScene
{
    std::unique_ptr<mr::MockBackend> backend = std::make_unique<mr::MockBackend>();
    std::unique_ptr<mr::AppSettings> settings;
    std::unique_ptr<mr::Formatters> formatters;
    std::unique_ptr<mr::JobCatalog> jobs;
    std::unique_ptr<mr::RoleCatalog> roles;
    std::unique_ptr<QQmlEngine> engine;
    std::unique_ptr<mr::AppController> app;
    std::unique_ptr<QObject> root;
    QQuickItem *page = nullptr;
    /// Every address the page asked the browser to open.
    QStringList opened;

    ~UpdatePageScene() { close(); }

    /// The application over the backend as configured so far, with 忽略此版本 cleared and
    /// the browser recorded in `opened`.
    void startApplication()
    {
        settings = std::make_unique<mr::AppSettings>();
        settings->setDismissedUpdateVersion(QString());
        app = std::make_unique<mr::AppController>(backend.get(), settings.get());
        app->update()->setUrlOpener([this](const QUrl &url) {
            opened.append(url.toString());
            return true;
        });
    }

    /// Shows the shipping \a element (DashboardPage, SettingsGeneralTab, ...) over the
    /// application; false when it could not be created.
    bool loadPage(const QString &element)
    {
        formatters = std::make_unique<mr::Formatters>();
        jobs = std::make_unique<mr::JobCatalog>();
        roles = std::make_unique<mr::RoleCatalog>();
        engine = std::make_unique<QQmlEngine>();
        auto *context = engine->rootContext();
        context->setContextProperty(QStringLiteral("App"), app.get());
        context->setContextProperty(QStringLiteral("Fmt"), formatters.get());
        context->setContextProperty(QStringLiteral("Jobs"), jobs.get());
        context->setContextProperty(QStringLiteral("Roles"), roles.get());
        context->setContextProperty(QStringLiteral("Settings"), settings.get());
        context->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QQmlComponent component(engine.get());
        component.setData(QStringLiteral(R"(import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder
ApplicationWindow {
    width: 1100; height: 900; visible: true
    color: Theme.surface
    ColumnLayout {
        anchors.fill: parent
        anchors.margins: 16
        %1 { objectName: "scenePage"; Layout.fillWidth: true; Layout.fillHeight: true }
    }
})").arg(element).toUtf8(), QUrl());
        root.reset(component.create());
        if (!root) {
            qWarning("%s", qPrintable(component.errorString()));
            return false;
        }
        page = qobject_cast<QQuickItem *>(root->findChild<QObject *>(QStringLiteral("scenePage")));
        return page != nullptr;
    }

    QQuickItem *item(const QString &name) const { return mrtest::findVisualItem(page, name); }
    bool shows(const QString &name) const
    {
        auto *found = item(name);
        return found && found->isVisible();
    }
    QString textOf(const QString &name) const
    {
        auto *found = item(name);
        return found ? found->property("text").toString() : QString();
    }
    bool press(const QString &name) const
    {
        auto *button = item(name);
        return button && button->isVisible() && button->isEnabled()
               && QMetaObject::invokeMethod(button, "clicked");
    }

protected:
    /// The page first, then its engine, then the application it binds to.
    void close()
    {
        root.reset();
        engine.reset();
        app.reset();
    }
};

} // namespace
