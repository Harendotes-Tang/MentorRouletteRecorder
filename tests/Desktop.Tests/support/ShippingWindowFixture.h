// The shipping window (Main.qml) as the application starts it: a real AppController on a
// MockBackend whose first GetStatus has verified an isolated database, with a desktop.ini of
// its own. RecoveryUiTests drives the note popup and 待处理 on it, RecordWorkspaceTests the
// record page. Everything is in an anonymous namespace: each suite gets its own copy inside
// the shared test executable.
#pragma once

#include "AppController.h"
#include "AppSettings.h"
#include "Formatters.h"
#include "GameNames.h"
#include "JobCatalog.h"
#include "LanguageManager.h"
#include "MockBackend.h"
#include "RecoveryController.h"
#include "RoleCatalog.h"
#include "RunFormValidator.h"

#include <QApplication>
#include <QClipboard>
#include <QDateTime>
#include <QDir>
#include <QFileInfo>
#include <QJsonArray>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickWindow>
#include <QTemporaryDir>
#include <QTest>
#include <QTimer>
#include <QUuid>

#include <memory>

namespace {

/// Where the scenes keep their desktop.ini and the suites leave their screenshots.
inline QString evidenceDirectory()
{
    return QString::fromUtf8(MR_SOURCE_DIR) + QStringLiteral("/artifacts/recoverable-prompts-20261020");
}

/// A captured run that ended a second ago after twenty minutes: cleared when \a completed,
/// otherwise ended without a clear signal and kept for review. \a sequence tells runs apart.
inline QVariantMap finishedRun(bool completed = false, int sequence = 1)
{
    const QString now = QDateTime::currentDateTimeUtc().addSecs(1).toString(Qt::ISODateWithMs);
    const QString earlier = QDateTime::currentDateTimeUtc().addSecs(-1200).toString(Qt::ISODateWithMs);
    const QString state = completed ? QStringLiteral("COMPLETED") : QStringLiteral("UNKNOWN_FINAL_STATE");
    const QString result = completed ? QStringLiteral("COMPLETED") : QStringLiteral("UNKNOWN");
    return {{QStringLiteral("run_id"), QStringLiteral("4e9bb2ac-2222-4333-8444-%1").arg(sequence, 12, 10, QLatin1Char('0'))},
            {QStringLiteral("revision"), 1}, {QStringLiteral("source"), QStringLiteral("AUTO_NETWORK")},
            {QStringLiteral("state"), state}, {QStringLiteral("result"), result},
            {QStringLiteral("mentor_roulette_id"), 9},
            {QStringLiteral("mentor_confirmed"), true}, {QStringLiteral("is_mentor"), true},
            {QStringLiteral("duty_name"), QStringLiteral("测试副本：森林中的重逢")},
            {QStringLiteral("duty_category"), QStringLiteral("迷宫挑战")},
            {QStringLiteral("job_id"), 24}, {QStringLiteral("job_name"), QStringLiteral("白魔法师")},
            {QStringLiteral("created_at_utc"), earlier}, {QStringLiteral("matched_at_utc"), earlier},
            {QStringLiteral("entered_at_utc"), earlier}, {QStringLiteral("ended_at_utc"), now},
            {QStringLiteral("duration_ms"), 1200000}, {QStringLiteral("pending_review"), !completed},
            {QStringLiteral("contributes_to_goal"), true}, {QStringLiteral("soft_deleted"), false}};
}

/// The MockBackend, listing every request type it is sent; it refuses the next note, the
/// fresh reads of one run or the reads of 待复核 when a test asks it to.
struct ObservedBackend : mr::MockBackend {
    QStringList commands;
    int freshReads = 0;
    bool refuseNextReflection = false;
    bool refuseFreshRead = false;
    bool refusePendingReads = false;
    bool connected = true;

    bool isConnected() const override { return connected; }

    mr::BackendReply *request(const QString &type, const QJsonObject &payload = {}) override
    {
        commands.append(type);
        const bool freshRead = type == QLatin1String("QueryRuns")
            && !payload.value(QStringLiteral("filter")).toObject().value(QStringLiteral("run_id")).toArray().isEmpty();
        if (freshRead) ++freshReads;
        const bool refuseNote = type == QLatin1String("SetRunReflection") && refuseNextReflection;
        const bool refusePending = refusePendingReads && type == QLatin1String("QueryRuns")
            && payload.value(QStringLiteral("filter")).toObject().value(QStringLiteral("pending_review")).toBool();
        if (!refuseNote && !refusePending && !(freshRead && refuseFreshRead))
            return mr::MockBackend::request(type, payload);
        refuseNextReflection = false;
        auto *reply = new mr::BackendReply(QUuid::createUuid().toString(), type, this);
        QTimer::singleShot(0, reply, [reply] {
            reply->fail(QStringLiteral("ERR_TEST_READ_OR_SAVE"), QStringLiteral("测试：暂时无法保存或读取，请稍后重试。"));
        });
        return reply;
    }
};

struct ShippingWindowScene {
    QTemporaryDir directory{evidenceDirectory() + QStringLiteral("/ui-settings-XXXXXX")};
    mr::AppSettings settings;
    mr::LanguageManager language{&settings};
    ObservedBackend backend;
    QJsonArray publishedRuns;
    std::unique_ptr<mr::AppController> app;
    mr::Formatters formatters;
    mr::JobCatalog jobs;
    mr::RoleCatalog roles;
    mr::RunFormValidator runForm;
    // The warning callback remains connected throughout QQmlEngine teardown.
    QString errors;
    QQmlEngine engine;
    std::unique_ptr<QObject> root;

    /// A scene whose desktop.ini (and data) is in \a sharedDirectory, as a restart finds it.
    explicit ShippingWindowScene(const QString &sharedDirectory = {})
        : settings(QDir(sharedDirectory.isEmpty() ? directory.path() : sharedDirectory)
                       .filePath(QStringLiteral("desktop.ini")), nullptr)
    {
        backend.setDataDirectory(settingsDirectory() + QStringLiteral("/data"));
        backend.setLiveMode(mr::MockBackend::LiveMode::None);
        backend.setRecordingFixture(QStringLiteral("listening"));
        backend.resetRuns({});
        settings.setPromptPresentation(QStringLiteral("window"));
    }

    bool create(bool dark = true, const QString &style = QStringLiteral("harendotes"))
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
        context->setContextProperty(QStringLiteral("ReduceMotion"), true);
        context->setContextProperty(QStringLiteral("ForceUiStyle"), style);
        QObject::connect(&engine, &QQmlEngine::warnings, &engine, [this](const QList<QQmlError> &warnings) {
            for (const auto &warning : warnings) errors += warning.toString() + QLatin1Char('\n');
        });
        QQmlComponent component(&engine, QUrl::fromLocalFile(QString::fromUtf8(MR_DESKTOP_QML_DIR) + QStringLiteral("/Main.qml")));
        root.reset(component.create());
        for (const auto &error : component.errors()) errors += error.toString() + QLatin1Char('\n');
        if (!root) return false;
        // Exercise the real initial connection/GetStatus handshake. Manually
        // assigning the scope here can race the queued startup connection and
        // make that later callback close an editor the fixture already opened.
        if (!QTest::qWaitForWindowExposed(mainWindow())) {
            errors += QStringLiteral("Main window did not become exposed.\n");
            return false;
        }
        const QString expectedDatabase = QDir::cleanPath(databasePath());
        if (!QTest::qWaitFor([&] {
                const QString actualDatabase = QDir::cleanPath(QDir::fromNativeSeparators(
                    app->collectorStatus().value(QStringLiteral("database_path")).toString()));
                return backend.commands.contains(QStringLiteral("GetStatus"))
                    && app->recovery()->identityVerified() && !app->recovery()->busy()
                    && actualDatabase == expectedDatabase;
            })) {
            errors += QStringLiteral("Initial GetStatus did not verify the isolated database.\n");
            return false;
        }
        return true;
    }

    /// The folder of desktop.ini, which also holds the data and the recovery journals.
    QString settingsDirectory() const { return QFileInfo(settings.instanceFilePath()).absolutePath(); }
    QString databasePath() const { return QDir(settingsDirectory()).filePath(QStringLiteral("data/mentor_recorder.db")); }
    /// The recovery journal of the database in use.
    QString journalPath() const
    {
        return QDir(settingsDirectory()).filePath(QStringLiteral("recovery/") + app->recovery()->databaseKey()
                                                  + QStringLiteral(".json"));
    }

    QQuickWindow *mainWindow() const { return qobject_cast<QQuickWindow *>(root.get()); }
    QQuickWindow *host() const { return root->findChild<QQuickWindow *>(QStringLiteral("recoveryReminderWindow")); }
    QQuickWindow *floating() const { return root->findChild<QQuickWindow *>(QStringLiteral("statisticsFloatingWindow")); }
    QObject *editor() const { return root->findChild<QObject *>(QStringLiteral("reflectionDialog")); }
    QObject *manual() const { return root->findChild<QObject *>(QStringLiteral("editRunDialog")); }
    QObject *inbox() const { return root->findChild<QObject *>(QStringLiteral("recoveryInbox")); }
    QQuickItem *workspace() const { return root->findChild<QQuickItem *>(QStringLiteral("recordWorkspacePage")); }
    QObject *note() const { return editor()->property("noteEditor").value<QObject *>(); }
    QQuickWindow *editingWindow() const { return editor()->property("inlineActive").toBool() ? mainWindow() : host(); }

    /// \a editor is open on the record page of the main window, not as a popup.
    bool inlineSession(QObject *editor) const
    {
        return editor->property("inlineActive").toBool() && !editor->property("visible").toBool()
            && app->currentPage() == 8 && mainWindow()->isVisible() && workspace()->isVisible()
            && !host()->isVisible();
    }
    bool workspaceActive() const { return inlineSession(editor()); }

    QQuickItem *item(const QString &name) const
    {
        const auto find = [&](auto &&self, QQuickItem *parent) -> QQuickItem * {
            if (parent->objectName() == name && parent->isVisible()) return parent;
            for (auto *child : parent->childItems())
                if (auto *match = self(self, child)) return match;
            return nullptr;
        };
        // Overview and inbox deliberately reuse the same row component. When
        // the modal list is open, mouse input must target its row, not the
        // identically named preview underneath the popup's input shield.
        if (inbox() && inbox()->property("visible").toBool()) {
            auto *popupContent = inbox()->property("contentItem").value<QQuickItem *>();
            if (popupContent)
                if (auto *match = find(find, popupContent)) return match;
        }
        auto windows = root->findChildren<QQuickWindow *>();
        windows.prepend(mainWindow());
        for (auto *window : windows)
            if (window->isVisible())
                if (auto *match = find(find, window->contentItem())) return match;
        return nullptr;
    }

    bool click(const QString &name) const
    {
        QQuickItem *target = nullptr;
        QPoint point;
        // A refreshed Repeater can expose a delegate before its parent layouts
        // have positioned it. Polish the actual visual ancestry before mapping
        // the mouse location, and keep the click inside this Scene's windows.
        if (!QTest::qWaitFor([&] {
                target = item(name);
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

    bool enterNote(const QString &text) const
    {
        auto *window = editingWindow();
        window->requestActivate();
        if (!QTest::qWaitForWindowActive(window)) return false;
        if (!click(QStringLiteral("reflectionTextArea"))) return false;
        const QString previous = QApplication::clipboard()->text();
        QApplication::clipboard()->setText(text);
        QTest::keyClick(window, Qt::Key_A, Qt::ControlModifier);
        QTest::keyClick(window, Qt::Key_V, Qt::ControlModifier);
        QApplication::clipboard()->setText(previous);
        return note()->property("text").toString() == text;
    }

    /// Opens the note editor of the main window for \a run, as 写心得 does.
    bool openReflection(const QVariantMap &run) const
    {
        return QMetaObject::invokeMethod(root.get(), "openReflection",
                                         Q_ARG(QVariant, QVariant(run)), Q_ARG(QVariant, QVariant(QString())));
    }

    /// Puts \a run among the runs the backend holds, in place of an earlier copy.
    void store(const QVariantMap &run)
    {
        const QString id = run.value(QStringLiteral("run_id")).toString();
        bool replaced = false;
        for (qsizetype index = 0; index < publishedRuns.size(); ++index) {
            if (publishedRuns.at(index).toObject().value(QStringLiteral("run_id")).toString() != id) continue;
            publishedRuns.replace(index, QJsonObject::fromVariantMap(run));
            replaced = true;
            break;
        }
        if (!replaced) publishedRuns.append(QJsonObject::fromVariantMap(run));
        backend.resetRuns(publishedRuns);
    }

    /// Stores \a run and announces that it has just finished, as the Collector does.
    void publish(const QVariantMap &run)
    {
        store(run);
        Q_EMIT backend.liveEvent({{QStringLiteral("event_id"), QUuid::createUuid().toString(QUuid::WithoutBraces)},
            {QStringLiteral("event_type"), QStringLiteral("RunFinished")},
            {QStringLiteral("kind"), QStringLiteral("run_finished")},
            {QStringLiteral("emitted_at_utc"), QDateTime::currentDateTimeUtc().addSecs(1).toString(Qt::ISODateWithMs)},
            {QStringLiteral("state"), run.value(QStringLiteral("state"))}, {QStringLiteral("run"), run}});
    }

    QStringList hostTexts() const
    {
        QStringList result;
        const auto collect = [&](auto &&self, QQuickItem *parent) -> void {
            const QString text = parent->property("text").toString();
            if (!text.isEmpty() && parent->isVisible()) result.append(text);
            for (auto *child : parent->childItems()) self(self, child);
        };
        collect(collect, host()->contentItem());
        return result;
    }
};

} // namespace
