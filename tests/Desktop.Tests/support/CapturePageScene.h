// The 捕获诊断 page in shipping QML on one backend, as CapturePageTests and SharedCalibrationCardTests
// load it, and the build's translation catalogue for the length of one test. Everything is in an
// anonymous namespace: each suite gets its own copy inside the shared test executable.
#pragma once

#include "AppController.h"
#include "CalibrationController.h"
#include "Formatters.h"
#include "IBackend.h"
#include "JobCatalog.h"
#include "MockBackend.h"
#include "QmlComponentTestSupport.h"
#include "RoleCatalog.h"
#include "SharedCalibrationController.h"

#include <QCoreApplication>
#include <QDir>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QStringList>
#include <QTranslator>
#include <QUrl>

#include <memory>

namespace {

/// Every non-empty text under \a item, depth first; an invisible item hides its whole subtree.
[[maybe_unused]] void collectVisibleText(QQuickItem *item, QStringList &texts)
{
    if (!item || !item->isVisible())
        return;
    const QVariant text = item->property("text");
    if (text.isValid() && !text.toString().isEmpty())
        texts.append(text.toString());
    for (auto *child : item->childItems())
        collectVisibleText(child, texts);
}

/// Puts a new \a Fake backend into \a slot and returns it, so that a test can script it after
/// the scene that owns the slot exists.
template <typename Fake>
Fake *installBackend(std::unique_ptr<mr::IBackend> &slot)
{
    auto owned = std::make_unique<Fake>();
    Fake *fake = owned.get();
    slot = std::move(owned);
    return fake;
}

/// The whole page on one backend - the mock unless a fake was installed - torn down in
/// dependency order. The browser and the clipboard are replaced as soon as the page is up:
/// a test never opens github.com and never overwrites the developer's own clipboard.
struct CapturePageScene
{
    std::unique_ptr<mr::MockBackend> mock = std::make_unique<mr::MockBackend>();
    std::unique_ptr<mr::IBackend> other;
    std::unique_ptr<mr::AppController> app;
    std::unique_ptr<mr::Formatters> formatters;
    mr::JobCatalog jobs;
    mr::RoleCatalog roles;
    std::unique_ptr<QQmlEngine> engine;
    std::unique_ptr<QObject> root;
    QQuickItem *page = nullptr;
    QString pageType = QStringLiteral("CapturePage");
    /// What the page asked the browser to open and last put on the clipboard.
    QStringList opened;
    QString copied;

    ~CapturePageScene()
    {
        root.reset();
        engine.reset();
        app.reset();
    }

    /// The backend the page will run on instead of the mock; call before open().
    template <typename Fake>
    Fake *fake()
    {
        return installBackend<Fake>(other);
    }

    bool open(bool maintainer = false)
    {
        app = std::make_unique<mr::AppController>(other ? other.get() : mock.get(), nullptr);
        app->setMaintainerToolsVisible(maintainer);
        formatters = std::make_unique<mr::Formatters>();
        engine = std::make_unique<QQmlEngine>();
        engine->rootContext()->setContextProperty(QStringLiteral("App"), app.get());
        engine->rootContext()->setContextProperty(QStringLiteral("Fmt"), formatters.get());
        engine->rootContext()->setContextProperty(QStringLiteral("Jobs"), &jobs);
        engine->rootContext()->setContextProperty(QStringLiteral("Roles"), &roles);
        engine->rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QQmlComponent component(engine.get());
        component.setData(QStringLiteral(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 1180; height: 900; visible: true
    color: Theme.surface
    %1 { objectName: "capturePage"; anchors.fill: parent; anchors.margins: 16 }
})").arg(pageType).toUtf8(), QUrl());
        root.reset(component.create());
        if (!root) {
            qWarning("%s", qPrintable(component.errorString()));
            return false;
        }
        page = qobject_cast<QQuickItem *>(root->findChild<QObject *>(QStringLiteral("capturePage")));
        if (!page)
            return false;
        shared()->setUrlOpener([this](const QUrl &url) {
            opened.append(url.toString());
            return true;
        });
        shared()->setClipboardWriter([this](const QString &text) { copied = text; });
        return true;
    }

    mr::SharedCalibrationController *shared() const { return app->calibration()->shared(); }
    QQuickItem *item(const QString &name) const { return mrtest::findVisualItem(page, name); }
    bool shows(const QString &name) const
    {
        auto *found = item(name);
        return found && found->isVisible();
    }
    QString text(const QString &name) const
    {
        auto *found = item(name);
        return found ? found->property("text").toString() : QString();
    }
    QStringList visibleTexts() const
    {
        QStringList texts;
        collectVisibleText(page, texts);
        return texts;
    }
};

/// The build's catalogue for \a language (src/Desktop/mentor_recorder_<language>.qm of this
/// build tree), installed for as long as this lives. zh_CN is the source language: nothing to load.
class ScopedCatalogue
{
public:
    explicit ScopedCatalogue(const QString &language)
    {
        if (language == QLatin1String("zh_CN"))
            return;
        m_path = QDir(QCoreApplication::applicationDirPath())
                     .filePath(QStringLiteral("../../src/Desktop/mentor_recorder_%1.qm").arg(language));
        m_loaded = m_translator.load(m_path);
        if (m_loaded)
            QCoreApplication::installTranslator(&m_translator);
    }
    ~ScopedCatalogue() { QCoreApplication::removeTranslator(&m_translator); }
    ScopedCatalogue(const ScopedCatalogue &) = delete;
    ScopedCatalogue &operator=(const ScopedCatalogue &) = delete;

    /// True for the source language and for a catalogue that loaded.
    bool loaded() const { return m_path.isEmpty() || m_loaded; }
    QString path() const { return m_path; }

private:
    QTranslator m_translator;
    QString m_path;
    bool m_loaded = false;
};

} // namespace
