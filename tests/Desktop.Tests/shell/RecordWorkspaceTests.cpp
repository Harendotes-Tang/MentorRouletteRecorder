#include "DesktopTestRunner.h"
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

#include <QApplication>
#include <QDateTime>
#include <QDir>
#include <QFileInfo>
#include <QFont>
#include <QJsonArray>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QSignalSpy>
#include <QStandardPaths>
#include <QTemporaryDir>
#include <QTest>
#include <QTimer>
#include <QUuid>

#include <memory>

namespace {

QVariantMap finishedRun(bool completed)
{
    const QString end = QDateTime::currentDateTimeUtc().addSecs(1).toString(Qt::ISODateWithMs);
    const QString start = QDateTime::currentDateTimeUtc().addSecs(-1200).toString(Qt::ISODateWithMs);
    return {{"run_id", QStringLiteral("a32cb20c-9925-4656-bd91-02122658dc63")},
            {"revision", 1}, {"source", QStringLiteral("AUTO_NETWORK")},
            {"state", completed ? QStringLiteral("COMPLETED") : QStringLiteral("UNKNOWN_FINAL_STATE")},
            {"result", completed ? QStringLiteral("COMPLETED") : QStringLiteral("UNKNOWN")},
            {"mentor_roulette_id", 9}, {"mentor_confirmed", true}, {"is_mentor", true},
            {"duty_name", QStringLiteral("测试副本：森林中的重逢")},
            {"duty_category", QStringLiteral("迷宫挑战")},
            {"job_id", 24}, {"job_name", QStringLiteral("白魔法师")},
            {"created_at_utc", start}, {"matched_at_utc", start},
            {"entered_at_utc", start}, {"ended_at_utc", end},
            {"duration_ms", 1200000}, {"pending_review", !completed},
            {"contributes_to_goal", true}, {"soft_deleted", false}};
}

class WorkspaceBackend final : public mr::MockBackend
{
public:
    QStringList commands;
    bool refuseNextNote = false;

    mr::BackendReply *request(const QString &type, const QJsonObject &payload = {}) override
    {
        commands.append(type);
        if (type == QLatin1String("SetRunReflection") && refuseNextNote) {
            refuseNextNote = false;
            auto *reply = new mr::BackendReply(QStringLiteral("workspace-note-refusal"), type, this);
            QTimer::singleShot(0, reply, [reply] {
                reply->fail(QStringLiteral("ERR_INTERNAL"), QStringLiteral("测试：笔记暂时无法保存"));
            });
            return reply;
        }
        return mr::MockBackend::request(type, payload);
    }
};

struct WorkspaceScene
{
    QTemporaryDir directory;
    mr::AppSettings settings{QDir(directory.path()).filePath(QStringLiteral("desktop.ini")), nullptr};
    mr::LanguageManager language{&settings};
    WorkspaceBackend backend;
    std::unique_ptr<mr::AppController> app;
    mr::Formatters formatters;
    mr::JobCatalog jobs;
    mr::RoleCatalog roles;
    mr::RunFormValidator validator;
    QString errors;
    QQmlEngine engine;
    std::unique_ptr<QObject> root;

    bool create()
    {
        if (!directory.isValid()) return false;
        backend.setDataDirectory(QDir(directory.path()).filePath(QStringLiteral("data")));
        backend.setLiveMode(mr::MockBackend::LiveMode::None);
        backend.setRecordingFixture(QStringLiteral("listening"));
        backend.resetRuns({});
        settings.setPromptPresentation(QStringLiteral("window"));
        language.initialize(QStringLiteral("zh-CN"));
        language.attachEngine(&engine);
        app = std::make_unique<mr::AppController>(&backend, &settings);
        auto *context = engine.rootContext();
        context->setContextProperty(QStringLiteral("App"), app.get());
        context->setContextProperty(QStringLiteral("Fmt"), &formatters);
        context->setContextProperty(QStringLiteral("Names"), mr::GameNames::shared());
        context->setContextProperty(QStringLiteral("Jobs"), &jobs);
        context->setContextProperty(QStringLiteral("Roles"), &roles);
        context->setContextProperty(QStringLiteral("RunForm"), &validator);
        context->setContextProperty(QStringLiteral("Settings"), &settings);
        context->setContextProperty(QStringLiteral("Lang"), &language);
        context->setContextProperty(QStringLiteral("SuppressOnboarding"), true);
        context->setContextProperty(QStringLiteral("ReduceMotion"), true);
        context->setContextProperty(QStringLiteral("ForceUiStyle"), QStringLiteral("classic"));
        QObject::connect(&engine, &QQmlEngine::warnings, &engine, [this](const QList<QQmlError> &warnings) {
            for (const auto &warning : warnings) errors += warning.toString() + QLatin1Char('\n');
        });
        QQmlComponent component(&engine, QUrl::fromLocalFile(
            QString::fromUtf8(MR_DESKTOP_QML_DIR) + QStringLiteral("/Main.qml")));
        root.reset(component.create());
        for (const auto &error : component.errors()) errors += error.toString() + QLatin1Char('\n');
        if (!root || !QTest::qWaitForWindowExposed(window())) return false;
        const QString expectedDatabase = QDir::cleanPath(QDir(directory.path())
            .filePath(QStringLiteral("data/mentor_recorder.db")));
        return QTest::qWaitFor([&] {
            const QString actualDatabase = QDir::cleanPath(QDir::fromNativeSeparators(
                app->collectorStatus().value(QStringLiteral("database_path")).toString()));
            return backend.commands.contains(QStringLiteral("GetStatus"))
                && app->recovery()->identityVerified() && !app->recovery()->busy()
                && actualDatabase == expectedDatabase;
        });
    }

    QQuickWindow *window() const { return qobject_cast<QQuickWindow *>(root.get()); }
    QObject *manual() const { return root->findChild<QObject *>(QStringLiteral("editRunDialog")); }
    QObject *reflection() const { return root->findChild<QObject *>(QStringLiteral("reflectionDialog")); }
    QQuickWindow *reminder() const { return root->findChild<QQuickWindow *>(QStringLiteral("recoveryReminderWindow")); }
    QQuickItem *workspace() const { return root->findChild<QQuickItem *>(QStringLiteral("recordWorkspacePage")); }

    bool inlineSession(QObject *editor) const
    {
        return editor->property("inlineActive").toBool() && !editor->property("visible").toBool()
            && app->currentPage() == 8 && window()->isVisible() && workspace()->isVisible()
            && !reminder()->isVisible();
    }

    bool clickNewRecord() const
    {
        QQuickItem *button = nullptr;
        const auto find = [&](auto &&self, QQuickItem *parent) -> QQuickItem * {
            if (!parent->isVisible()) return nullptr;
            if (parent->property("text").toString() == QStringLiteral("新增记录")
                && parent->metaObject()->indexOfSignal("clicked()") >= 0)
                return parent;
            for (auto *child : parent->childItems())
                if (auto *found = self(self, child)) return found;
            return nullptr;
        };
        if (!QTest::qWaitFor([&] {
                button = find(find, window()->contentItem());
                return button && button->isEnabled() && button->width() > 0 && button->height() > 0;
            })) return false;
        const QPoint center = button->mapToScene(QPointF(button->width() / 2, button->height() / 2)).toPoint();
        if (!QRect(QPoint(), window()->size()).contains(center)) return false;
        QTest::mouseClick(window(), Qt::LeftButton, Qt::NoModifier, center);
        return true;
    }

    void publish(const QVariantMap &run)
    {
        backend.resetRuns({QJsonObject::fromVariantMap(run)});
        Q_EMIT backend.liveEvent({{"event_id", QUuid::createUuid().toString(QUuid::WithoutBraces)},
            {"event_type", QStringLiteral("RunFinished")}, {"kind", QStringLiteral("run_finished")},
            {"emitted_at_utc", QDateTime::currentDateTimeUtc().addSecs(1).toString(Qt::ISODateWithMs)},
            {"state", run.value(QStringLiteral("state"))}, {"run", run}});
    }
};

class RecordWorkspaceTests final : public QObject
{
    Q_OBJECT
private Q_SLOTS:
    void initTestCase()
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

    void overviewAndHistoryCreateInsideTheWorkspace_data()
    {
        QTest::addColumn<int>("sourcePage");
        QTest::newRow("overview") << 0;
        QTest::newRow("history") << 1;
    }

    void overviewAndHistoryCreateInsideTheWorkspace()
    {
        QFETCH(int, sourcePage);
        WorkspaceScene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        scene.app->navigate(sourcePage);
        QVERIFY(scene.clickNewRecord());
        QTRY_VERIFY(scene.inlineSession(scene.manual()));
        auto *body = scene.workspace()->findChild<QQuickItem *>(QStringLiteral("manualRecordPageContent"));
        QVERIFY(body && body->isVisible());
        bool belongsToPage = false;
        for (auto *parent = body->parentItem(); parent; parent = parent->parentItem())
            belongsToPage |= parent == scene.workspace();
        QVERIFY(belongsToPage);
        QCOMPARE(scene.backend.commands.count(QStringLiteral("CreateManualRun")), 0);
        QVERIFY(QMetaObject::invokeMethod(scene.manual(), "closeEditor"));
        QTRY_COMPARE(scene.app->currentPage(), sourcePage);
        QVERIFY(!scene.manual()->property("editingActive").toBool());
        QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
    }

    void refusedNoteAfterCreationStaysOnThePageAndRetriesTheSameRun()
    {
        WorkspaceScene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QVERIFY(QMetaObject::invokeMethod(scene.root.get(), "openCreateDialog"));
        QTRY_VERIFY(scene.inlineSession(scene.manual()));
        scene.manual()->setProperty("matchedTime", QStringLiteral("12:00:00"));
        scene.manual()->setProperty("enteredTime", QStringLiteral("12:01:00"));
        scene.manual()->setProperty("endedTime", QStringLiteral("12:30:00"));
        scene.manual()->setProperty("reflectionMood", QStringLiteral("ok"));
        const QString note = QStringLiteral("这次补录的记录已经保存，心得失败后继续重试。");
        scene.manual()->setProperty("reflectionText", note);
        scene.backend.refuseNextNote = true;
        QVERIFY(QMetaObject::invokeMethod(scene.manual(), "submit"));
        QTRY_VERIFY(scene.manual()->property("recordSaved").toBool());
        QTRY_VERIFY(!scene.manual()->property("reflectionSubmitting").toBool());
        QTRY_VERIFY(!scene.manual()->property("errorText").toString().isEmpty());
        QVERIFY(scene.inlineSession(scene.manual()));
        QVERIFY(scene.manual()->property("reflectionPending").toBool());
        const QString runId = scene.manual()->property("savedRunId").toString();
        QVERIFY(!runId.isEmpty());
        QCOMPARE(scene.manual()->property("reflectionText").toString(), note);
        QCOMPARE(scene.backend.commands.count(QStringLiteral("CreateManualRun")), 1);
        QCOMPARE(scene.backend.commands.count(QStringLiteral("SetRunReflection")), 1);
        QVERIFY(QMetaObject::invokeMethod(scene.manual(), "submit"));
        QTRY_VERIFY(!scene.manual()->property("editingActive").toBool());
        QTRY_COMPARE(scene.app->currentPage(), 0);
        QCOMPARE(scene.backend.commands.count(QStringLiteral("CreateManualRun")), 1);
        QCOMPARE(scene.backend.commands.count(QStringLiteral("SetRunReflection")), 2);
        QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
    }

    void escapeOnAnotherPageDoesNotCloseTheHiddenReflectionSession()
    {
        WorkspaceScene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        const auto run = finishedRun(true);
        scene.backend.resetRuns({QJsonObject::fromVariantMap(run)});
        scene.app->recovery()->offer(run, QStringLiteral("reflection"));
        scene.app->recovery()->requestOpen(run.value(QStringLiteral("run_id")).toString());
        QTRY_VERIFY(scene.inlineSession(scene.reflection()));
        auto *note = scene.reflection()->property("noteEditor").value<QObject *>();
        QVERIFY(note);
        const QString text = QStringLiteral("切换页面后仍保留的心得草稿");
        note->setProperty("text", text);
        scene.app->navigate(1);
        QTRY_VERIFY(!scene.workspace()->isVisible());
        QTRY_VERIFY(!scene.reflection()->property("editorOnScreen").toBool());
        scene.window()->requestActivate();
        QVERIFY(QTest::qWaitForWindowActive(scene.window()));
        QTest::keyClick(scene.window(), Qt::Key_Escape);
        QTest::qWait(100);
        QCOMPARE(scene.app->currentPage(), 1);
        QVERIFY(scene.reflection()->property("inlineActive").toBool());
        QVERIFY(!scene.reflection()->property("visible").toBool());
        QCOMPARE(note->property("text").toString(), text);
        QCOMPARE(scene.backend.commands.count(QStringLiteral("SetRunReflection")), 0);
        scene.app->navigate(8);
        QTRY_VERIFY(scene.inlineSession(scene.reflection()));
        QCOMPARE(note->property("text").toString(), text);
        QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
    }

    void closingManualEntryReoffersTheAutomaticPromptItBlocked()
    {
        WorkspaceScene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QVERIFY(QMetaObject::invokeMethod(scene.root.get(), "openCreateDialog"));
        QTRY_VERIFY(scene.inlineSession(scene.manual()));
        QSignalSpy offers(scene.app.get(), &mr::AppController::resultConfirmationRequested);
        scene.publish(finishedRun(false));
        QTRY_COMPARE(offers.count(), 1);
        QTRY_COMPARE(scene.app->recovery()->count(), 1);
        QVERIFY(!scene.reflection()->property("editingActive").toBool());
        QVERIFY(!scene.reminder()->isVisible());
        QVERIFY(QMetaObject::invokeMethod(scene.manual(), "closeEditor"));
        QTRY_VERIFY(scene.reminder()->isVisible() && scene.reflection()->property("opened").toBool());
        QVERIFY(scene.reflection()->property("standalone").toBool());
        QVERIFY(scene.reflection()->property("automaticOpen").toBool());
        QVERIFY(!scene.reflection()->property("inlineActive").toBool());
        QVERIFY(scene.reflection()->property("askingResult").toBool());
        QCOMPARE(scene.app->currentPage(), 0);
        QCOMPARE(scene.backend.commands.count(QStringLiteral("CreateManualRun")), 0);
        QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
    }
};

} // namespace

MR_DESKTOP_TEST_SUITE(RecordWorkspace)
{
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QApplication app(argc, argv);
    if (!mr::registerBundledFonts()) return 2;
    QApplication::setFont(QFont(QStringLiteral("Noto Sans SC")));
    RecordWorkspaceTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "RecordWorkspaceTests.moc"
