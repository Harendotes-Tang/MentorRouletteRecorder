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
#include "StatisticsOverlayController.h"

#include <QApplication>
#include <QClipboard>
#include <QDateTime>
#include <bit>

#include <QDir>
#include <QEvent>
#include <QFileInfo>
#include <QFont>
#include <QImage>
#include <QJsonArray>
#include <QLockFile>
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
#ifdef Q_OS_WIN
#include <qt_windows.h>
#endif

namespace {
QString evidenceDirectory()
{
    return QString::fromUtf8(MR_SOURCE_DIR) + QStringLiteral("/artifacts/recoverable-prompts-20261020");
}

QVariantMap finishedRun(bool completed = false, int sequence = 1)
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

struct Scene {
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

    explicit Scene(const QString &sharedDirectory = {})
        : settings(QDir(sharedDirectory.isEmpty() ? directory.path() : sharedDirectory)
                       .filePath(QStringLiteral("desktop.ini")), nullptr)
    {
        backend.setDataDirectory(QFileInfo(settings.instanceFilePath()).absolutePath() + QStringLiteral("/data"));
        backend.setLiveMode(mr::MockBackend::LiveMode::None);
        backend.setRecordingFixture(QStringLiteral("listening"));
        backend.resetRuns({});
        settings.setPromptPresentation(QStringLiteral("window"));
    }

    bool create(bool dark = true)
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
        context->setContextProperty(QStringLiteral("ForceUiStyle"), QStringLiteral("harendotes"));
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
        const QString expectedDatabase = QDir::cleanPath(QDir(QFileInfo(settings.instanceFilePath()).absolutePath())
            .filePath(QStringLiteral("data/mentor_recorder.db")));
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

    QQuickWindow *mainWindow() const { return qobject_cast<QQuickWindow *>(root.get()); }
    QQuickWindow *host() const { return root->findChild<QQuickWindow *>(QStringLiteral("recoveryReminderWindow")); }
    QQuickWindow *floating() const { return root->findChild<QQuickWindow *>(QStringLiteral("statisticsFloatingWindow")); }
    QObject *editor() const { return root->findChild<QObject *>(QStringLiteral("reflectionDialog")); }
    QObject *inbox() const { return root->findChild<QObject *>(QStringLiteral("recoveryInbox")); }
    QObject *note() const { return editor()->findChild<QObject *>(QStringLiteral("reflectionTextArea")); }

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
        host()->requestActivate();
        if (!QTest::qWaitForWindowActive(host())) return false;
        if (!click(QStringLiteral("reflectionTextArea"))) return false;
        const QString previous = QApplication::clipboard()->text();
        QApplication::clipboard()->setText(text);
        QTest::keyClick(host(), Qt::Key_A, Qt::ControlModifier);
        QTest::keyClick(host(), Qt::Key_V, Qt::ControlModifier);
        QApplication::clipboard()->setText(previous);
        return note()->property("text").toString() == text;
    }

    void publish(const QVariantMap &run)
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

namespace {
class RecoveryUiTests : public QObject
{
    Q_OBJECT
private Q_SLOTS:
    void initTestCase();
    void hiddenMainReminderCanDeferAndReopen();
    void nativeCloseDefersTheDraftAndWaitsForAnActiveSave() const;
    void automaticNativeWindowDoesNotStealForeground();
    void restartDraftCanOpenFromOverviewAndSave();
    void silentModesAndDisabledPrompts_data();
    void silentModesAndDisabledPrompts();
    void failedSaveAndChangedNoteKeepRecoverableInput();
    void journalFailureKeepsEditorOpenUntilRetry();
    void oldSaveReplyCannotCloseReopenedEditorAfterScopeChanges();
    void sameRunWithoutDraftUsesLatestCommittedNote();
    void answerStaysOpenForTheNote();
    void answerWithAJobThenFinishClosesCleanly();
    void aRefusedNoteAfterTheAnswerStillCollapses();
    void aDeletedRecordsReminderCanBeRemoved();
    void reviewFromTheNoteEditorOpensTheReasonDialogForThatRecord();
    void theMainWindowNoteEditorKeepsItsOwnLook();
    void escapeInTheMainWindowEditorKeepsAnUnsavedDraft();
    void aPendingRecordWithAKnownResultCanBeReviewed();
    void floatingModeOpensThePopupBesideAVisibleFloat();
    void aNoteEditorLeftInTheHiddenWindowDoesNotHoldPopupsBack();
    void aLostConnectionPutsThePopupAwayWithANotice();
    void bringingTheWindowForwardKeepsItMaximised();
    void openingAnotherRecordBringsTheOpenEditorForward();
    void noNpcapWarningWhileNothingIsKnownAboutNpcap();
    void aQuestionAnsweredInThePagerIsNotAskedAgain();
    void aQuestionAnsweredElsewhereIsNotAskedAgain();
    void anAutomaticClearShowsTheCollectorsCountBeforeAndAfter();
    void anAnsweredClearClaimsTheCountOnlyWhenTheRunContributes_data();
    void anAnsweredClearClaimsTheCountOnlyWhenTheRunContributes();
    void overviewUsesUnifiedCountAndSharedActions();
    void overviewDraftOnlyDisappearsAfterSave();
    void overviewFailedReadKeepsRecoveryAccess();
    void overviewEvidence_data();
    void overviewEvidence();
    void renderedEvidence_data();
    void renderedEvidence();
};
} // namespace

void RecoveryUiTests::initTestCase()
{
    QVERIFY(QDir().mkpath(evidenceDirectory()));
    const QString base = QString::fromUtf8(MR_DESKTOP_QML_DIR);
    qmlRegisterSingletonType(QUrl::fromLocalFile(base + QStringLiteral("/Theme.qml")), "MentorRecorder", 1, 0, "Theme");
    for (const auto &directory : {QStringLiteral("/components"), QStringLiteral("/pages"),
                                  QStringLiteral("/dialogs"), QStringLiteral("/charts")})
        for (const auto &file : QDir(base + directory).entryList({QStringLiteral("*.qml")}, QDir::Files)) {
            const QByteArray name = file.chopped(4).toUtf8();
            qmlRegisterType(QUrl::fromLocalFile(base + directory + QLatin1Char('/') + file),
                            "MentorRecorder", 1, 0, name.constData());
        }
}

void RecoveryUiTests::hiddenMainReminderCanDeferAndReopen()
{
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    QVERIFY(scene.host() && scene.editor() && scene.inbox());
    scene.mainWindow()->hide();
    const auto run = finishedRun();
    scene.publish(run);
    QTRY_VERIFY(scene.host()->isVisible() && scene.editor()->property("opened").toBool());
    QVERIFY(!scene.mainWindow()->isVisible());
    QVERIFY(!scene.host()->transientParent());
    QCOMPARE(scene.host()->flags() & Qt::WindowType_Mask, Qt::WindowFlags(Qt::Tool));
    QVERIFY(!scene.host()->flags().testFlag(Qt::WindowDoesNotAcceptFocus));
    QVERIFY(!scene.editor()->property("modal").toBool());
    QCOMPARE(scene.host()->width(), 320);
    QVERIFY(scene.hostTexts().contains(QString::fromUtf8("通关了吗？")));
    QVERIFY(scene.hostTexts().contains(QString::fromUtf8("刚刚结束")));
    QVERIFY(scene.hostTexts().join(QLatin1Char('\n')).contains(run.value(QStringLiteral("duty_name")).toString()));
    QVERIFY(scene.enterNote(QStringLiteral("remember this run")));
    QTest::keyClick(scene.host(), Qt::Key_Escape);
    QTRY_VERIFY(!scene.editor()->property("visible").toBool() && !scene.host()->isVisible());
    QCOMPARE(scene.app->recovery()->draft(run).value(QStringLiteral("text")).toString(), QStringLiteral("remember this run"));
    QCOMPARE(scene.backend.commands.count(QStringLiteral("SetRunReflection")), 0);
    QTRY_VERIFY(scene.item(QStringLiteral("reopenDeferredButton")));
    const int reads = scene.backend.freshReads;
    QVERIFY(scene.click(QStringLiteral("reopenDeferredButton")));
    QTRY_VERIFY(scene.host()->isVisible() && scene.editor()->property("opened").toBool());
    QVERIFY(scene.backend.freshReads > reads);
    QCOMPARE(scene.note()->property("text").toString(), QStringLiteral("remember this run"));
    QCOMPARE(scene.editor()->property("runId").toString(), run.value(QStringLiteral("run_id")).toString());
    QVERIFY(scene.click(QStringLiteral("deferReflectionButton")));
    QTRY_VERIFY(!scene.host()->isVisible());
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::nativeCloseDefersTheDraftAndWaitsForAnActiveSave() const
{
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    scene.mainWindow()->hide();
    const auto run = finishedRun(true);
    scene.publish(run);
    QTRY_VERIFY(scene.host()->isVisible() && scene.editor()->property("opened").toBool());
    const QString text = QStringLiteral("keep this note when the native window closes");
    QVERIFY(scene.enterNote(text));
    QVERIFY(!scene.editor()->property("busy").toBool());
    // The close event is rejected even when defer() successfully puts the
    // editor away: only the editor owns saving its draft and hiding its host.
    QVERIFY(!scene.host()->close());
    QTRY_VERIFY(!scene.host()->isVisible() && !scene.editor()->property("visible").toBool());
    QCOMPARE(scene.app->recovery()->draft(run).value(QStringLiteral("text")).toString(), text);
    QCOMPARE(scene.backend.commands.count(QStringLiteral("SetRunReflection")), 0);

    scene.app->recovery()->requestOpen(run.value(QStringLiteral("run_id")).toString());
    QTRY_VERIFY(scene.host()->isVisible() && scene.editor()->property("opened").toBool());
    QCOMPARE(scene.note()->property("text").toString(), text);
    QSignalSpy success(scene.app.get(), &mr::AppController::reflectionSaved);
    scene.backend.setReplyDelay(1200, QStringLiteral("SetRunReflection"));
    QVERIFY(scene.click(QStringLiteral("saveReflectionButton")));
    QVERIFY(scene.editor()->property("busy").toBool());
    QVERIFY(!scene.host()->close());
    QVERIFY(scene.host()->isVisible() && scene.editor()->property("opened").toBool());
    QVERIFY(scene.editor()->property("busy").toBool());
    QCOMPARE(scene.note()->property("text").toString(), text);
    QCOMPARE(scene.app->recovery()->draft(run).value(QStringLiteral("text")).toString(), text);
    QCOMPARE(scene.backend.commands.count(QStringLiteral("SetRunReflection")), 1);
    QTRY_COMPARE(success.count(), 1);
    QTRY_VERIFY(!scene.editor()->property("busy").toBool());
    QTRY_VERIFY(!scene.host()->isVisible() && !scene.editor()->property("visible").toBool());
    QVERIFY(scene.app->recovery()->draft(run).isEmpty());
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::automaticNativeWindowDoesNotStealForeground()
{
#ifdef Q_OS_WIN
    if (QGuiApplication::platformName() != QLatin1String("windows"))
        QSKIP("Native foreground HWND verification requires the Windows platform.");
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    scene.mainWindow()->hide();
    QWindow foreground;
    foreground.setTitle(QStringLiteral("Recovery UI test foreground sentinel"));
    foreground.resize(640, 420);
    foreground.show();
    foreground.requestActivate();
    QVERIFY(QTest::qWaitForWindowActive(&foreground));
    auto *const original = std::bit_cast<HWND>(foreground.winId());
    QTRY_COMPARE(GetForegroundWindow(), original);
    scene.publish(finishedRun());
    QTRY_VERIFY(scene.host()->isVisible() && scene.editor()->property("opened").toBool());
    QTest::qWait(160);
    QCOMPARE(GetForegroundWindow(), original);
    auto *const host = std::bit_cast<HWND>(scene.host()->winId());
    QCOMPARE(GetWindow(host, GW_OWNER), HWND(nullptr));
    QVERIFY(GetWindowLongPtr(host, GWL_EXSTYLE) & WS_EX_TOOLWINDOW);
    QVERIFY(GetWindowLongPtr(host, GWL_EXSTYLE) & WS_EX_TOPMOST);
    QVERIFY(!(GetWindowLongPtr(host, GWL_EXSTYLE) & WS_EX_NOACTIVATE));
    QVERIFY(scene.enterNote(QStringLiteral("typing after explicit interaction")));
    QTRY_COMPARE(GetForegroundWindow(), host);
    QTest::keyClick(scene.host(), Qt::Key_Escape);
    QTRY_VERIFY(!scene.host()->isVisible());
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
#else
    QSKIP("Native Windows foreground verification.");
#endif
}

void RecoveryUiTests::restartDraftCanOpenFromOverviewAndSave()
{
    QTemporaryDir shared(evidenceDirectory() + QStringLiteral("/restart-XXXXXX"));
    QVERIFY(shared.isValid());
    const auto run = finishedRun(true);
    const QString note = QStringLiteral("下次注意转场时的治疗时机。");
    {
        Scene first(shared.path());
        QVERIFY2(first.create(), qPrintable(first.errors));
        first.publish(run);
        QTRY_VERIFY(first.host()->isVisible() && first.editor()->property("opened").toBool());
        QVERIFY(first.enterNote(note));
        QVERIFY(first.click(QStringLiteral("deferReflectionButton")));
        QTRY_VERIFY(!first.host()->isVisible());
        QCOMPARE(first.app->recovery()->draft(run).value(QStringLiteral("text")).toString(), note);
        QCOMPARE(first.backend.commands.count(QStringLiteral("SetRunReflection")), 0);
        QVERIFY2(first.errors.isEmpty(), qPrintable(first.errors));
    }
    Scene restored(shared.path());
    restored.backend.resetRuns({QJsonObject::fromVariantMap(run)});
    QVERIFY2(restored.create(), qPrintable(restored.errors));
    QTRY_COMPARE(restored.app->recovery()->count(), 1);
    QTRY_VERIFY(!restored.app->recovery()->busy());
    QVERIFY(!restored.host()->isVisible());
    QVERIFY(restored.click(QStringLiteral("openPendingWork_") + run.value(QStringLiteral("run_id")).toString()));
    QTRY_VERIFY(restored.host()->isVisible() && restored.editor()->property("opened").toBool());
    QCOMPARE(restored.note()->property("text").toString(), note);
    QVERIFY(restored.backend.freshReads > 0);
    QVERIFY(restored.click(QStringLiteral("saveReflectionButton")));
    QTRY_VERIFY(!restored.host()->isVisible());
    QTRY_VERIFY(restored.app->recovery()->draft(run).isEmpty());
    QTRY_COMPARE(restored.app->recovery()->count(), 0);
    QCOMPARE(restored.backend.commands.count(QStringLiteral("SetRunReflection")), 1);
    QTest::qWait(400); // A delayed draft timer must not resurrect successfully submitted input.
    QVERIFY(restored.app->recovery()->draft(run).isEmpty());
    QVERIFY2(restored.errors.isEmpty(), qPrintable(restored.errors));
}

void RecoveryUiTests::silentModesAndDisabledPrompts_data()
{
    QTest::addColumn<QString>("mode");
    QTest::addColumn<bool>("enabled");
    QTest::newRow("inbox") << QStringLiteral("inbox") << true;
    QTest::newRow("floating") << QStringLiteral("floating") << true;
    QTest::newRow("disabled") << QStringLiteral("window") << false;
}

void RecoveryUiTests::silentModesAndDisabledPrompts()
{
    QFETCH(QString, mode);
    QFETCH(bool, enabled);
    Scene scene;
    scene.settings.setPromptPresentation(mode);
    scene.settings.setReflectPrompt(enabled);
    scene.settings.setConfirmPrompt(enabled);
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    QSignalSpy reflectionOffers(scene.app.get(), &mr::AppController::reflectionPromptRequested);
    QSignalSpy resultOffers(scene.app.get(), &mr::AppController::resultConfirmationRequested);
    const auto run = finishedRun(true);
    scene.publish(run);
    QTest::qWait(180);
    QVERIFY(!scene.host()->isVisible());
    QVERIFY(!scene.editor()->property("visible").toBool());
    QCOMPARE(reflectionOffers.count(), enabled ? 1 : 0);
    QCOMPARE(scene.app->recovery()->count(), enabled ? 1 : 0);
    // A distinct eligible note proves delivery consumed the first automatic queue.
    const auto second = finishedRun(true, 2);
    scene.publish(second);
    QTest::qWait(140);
    QCOMPARE(reflectionOffers.count(), enabled ? 2 : 0);
    QVERIFY(!scene.host()->isVisible());
    if (mode == QLatin1String("floating")) {
        QVERIFY(!scene.floating()->isVisible());
        scene.app->statisticsOverlay()->setModules({QStringLiteral("pending_work")});
        scene.app->statisticsOverlay()->show();
        QVERIFY(QTest::qWaitForWindowExposed(scene.floating()));
        QTRY_VERIFY(scene.item(QStringLiteral("floatingPendingButton")));
        QCOMPARE(scene.item(QStringLiteral("floatingPendingCount"))->property("text").toString(), QStringLiteral("2"));
        scene.app->statisticsOverlay()->setLocked(true);
        QVERIFY(scene.app->statisticsOverlay()->locked());
        QVERIFY(!scene.item(QStringLiteral("floatingHeader")));
        scene.app->statisticsOverlay()->setLocked(false);
        QVERIFY(scene.click(QStringLiteral("floatingPendingButton")));
        QTRY_VERIFY(scene.host()->isVisible() && scene.editor()->property("opened").toBool());
        // Two records: the popup pages between them without closing.
        QTRY_VERIFY(scene.item(QStringLiteral("popupNextButton")));
        const QString firstShown = scene.editor()->property("runId").toString();
        QVERIFY(scene.click(QStringLiteral("popupNextButton")));
        QTRY_VERIFY(scene.editor()->property("runId").toString() != firstShown);
        QVERIFY(scene.host()->isVisible());
        QVERIFY(scene.click(QStringLiteral("popupPreviousButton")));
        QTRY_COMPARE(scene.editor()->property("runId").toString(), firstShown);
        QVERIFY(scene.click(QStringLiteral("deferReflectionButton")));
        QTRY_VERIFY(!scene.host()->isVisible());
        // 浮窗提示 opens the popup beside a visible float (review L2-8); the
        // questions below are asked with the float closed again.
        scene.app->statisticsOverlay()->hide();
        QTRY_VERIFY(!scene.floating()->isVisible());
    }
    if (!enabled) {
        const auto unknown = finishedRun(false, 3);
        scene.publish(unknown);
        QTRY_COMPARE(scene.app->recovery()->count(), 1); // Existing pending_review remains a manual entry.
        QCOMPARE(resultOffers.count(), 0);
        QVERIFY(!scene.host()->isVisible());
    } else {
        scene.publish(finishedRun(false, 3));
        QTRY_COMPARE(resultOffers.count(), 1);
        scene.publish(finishedRun(false, 4));
        QTRY_COMPARE(resultOffers.count(), 2);
        QVERIFY(!scene.host()->isVisible());
    }
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::failedSaveAndChangedNoteKeepRecoverableInput()
{
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    auto run = finishedRun(true);
    scene.publish(run);
    QTRY_VERIFY(scene.host()->isVisible() && scene.editor()->property("opened").toBool());
    QVERIFY(scene.enterNote(QStringLiteral("unsaved draft")));
    scene.backend.refuseNextReflection = true;
    QVERIFY(scene.click(QStringLiteral("saveReflectionButton")));
    QTRY_VERIFY(!scene.editor()->property("busy").toBool());
    QTRY_VERIFY(!scene.editor()->property("errorText").toString().isEmpty());
    QVERIFY(scene.host()->isVisible());
    QCOMPARE(scene.note()->property("text").toString(), QStringLiteral("unsaved draft"));
    QVERIFY(scene.click(QStringLiteral("deferReflectionButton")));
    QTRY_VERIFY(!scene.host()->isVisible());
    run.insert(QStringLiteral("reflection"), QVariantMap{{QStringLiteral("text"), QStringLiteral("new note saved elsewhere")},
               {QStringLiteral("mood"), QStringLiteral("ok")}});
    scene.backend.resetRuns({QJsonObject::fromVariantMap(run)});
    scene.app->recovery()->requestOpen(run.value(QStringLiteral("run_id")).toString());
    QTRY_VERIFY(scene.host()->isVisible() && scene.editor()->property("opened").toBool());
    QVERIFY(scene.editor()->property("conflictUnresolved").toBool());
    QCOMPARE(scene.note()->property("text").toString(), QStringLiteral("unsaved draft"));
    QCOMPARE(scene.editor()->property("baselineText").toString(), QStringLiteral("new note saved elsewhere"));
    auto *save = scene.item(QStringLiteral("saveReflectionButton"));
    QVERIFY(save);
    QVERIFY(!save->isEnabled());
    QVERIFY(scene.enterNote(QStringLiteral("edited conflict draft")));
    QTest::qWait(350);
    QCOMPARE(scene.app->recovery()->draft(run).value(QStringLiteral("text")).toString(), QStringLiteral("edited conflict draft"));
    QVERIFY(scene.app->recovery()->draft(run).value(QStringLiteral("conflict")).toBool());
    QVERIFY(scene.click(QStringLiteral("keepRecoveryDraftButton")));
    QVERIFY(!scene.editor()->property("conflictUnresolved").toBool());
    QVERIFY(scene.click(QStringLiteral("saveReflectionButton")));
    QTRY_VERIFY(!scene.host()->isVisible());
    QTRY_VERIFY(scene.app->recovery()->draft(run).isEmpty());
    QCOMPARE(scene.backend.commands.count(QStringLiteral("SetRunReflection")), 2);
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::journalFailureKeepsEditorOpenUntilRetry()
{
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    const auto run = finishedRun(true);
    scene.publish(run);
    QTRY_VERIFY(scene.host()->isVisible() && scene.editor()->property("opened").toBool());
    const QString journal = QDir(QFileInfo(scene.settings.instanceFilePath()).absolutePath())
        .filePath(QStringLiteral("recovery/") + scene.app->recovery()->databaseKey() + QStringLiteral(".json"));
    QLockFile lock(journal + QStringLiteral(".lock"));
    QVERIFY(lock.tryLock(0));
    QVERIFY(scene.enterNote(QStringLiteral("retain input while disk is busy")));
    QVERIFY(scene.click(QStringLiteral("deferReflectionButton")));
    QTRY_VERIFY(!scene.editor()->property("errorText").toString().isEmpty());
    QVERIFY(scene.host()->isVisible() && scene.editor()->property("visible").toBool());
    QCOMPARE(scene.note()->property("text").toString(), QStringLiteral("retain input while disk is busy"));
    QCOMPARE(scene.backend.commands.count(QStringLiteral("SetRunReflection")), 0);
    lock.unlock();
    QVERIFY(scene.click(QStringLiteral("deferReflectionButton")));
    QTRY_VERIFY(!scene.host()->isVisible());
    QVERIFY(QFileInfo::exists(journal));
    scene.app->recovery()->requestOpen(run.value(QStringLiteral("run_id")).toString());
    QTRY_VERIFY(scene.host()->isVisible() && scene.editor()->property("opened").toBool());
    QCOMPARE(scene.note()->property("text").toString(), QStringLiteral("retain input while disk is busy"));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::oldSaveReplyCannotCloseReopenedEditorAfterScopeChanges()
{
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    const auto run = finishedRun(true);
    scene.publish(run);
    QTRY_VERIFY(scene.host()->isVisible() && scene.editor()->property("opened").toBool());
    QVERIFY(scene.enterNote(QStringLiteral("old submitted note")));
    QSignalSpy success(scene.app.get(), &mr::AppController::reflectionSaved);
    QSignalSpy failure(scene.app.get(), &mr::AppController::reflectionFailed);
    scene.backend.setReplyDelay(1200, QStringLiteral("SetRunReflection"));
    QVERIFY(scene.click(QStringLiteral("saveReflectionButton")));
    QVERIFY(scene.editor()->property("busy").toBool());
    const QString original = QDir(QFileInfo(scene.settings.instanceFilePath()).absolutePath())
        .filePath(QStringLiteral("data/mentor_recorder.db"));
    scene.app->recovery()->setDatabasePath(original + QStringLiteral(".other"));
    QTRY_VERIFY(!scene.host()->isVisible());
    scene.app->recovery()->setDatabasePath(original);
    scene.app->recovery()->requestOpen(run.value(QStringLiteral("run_id")).toString());
    QTRY_VERIFY(scene.host()->isVisible() && scene.editor()->property("opened").toBool());
    QVERIFY(scene.enterNote(QStringLiteral("new editor input after reconnect")));
    QTRY_COMPARE(failure.count(), 1);
    QCOMPARE(success.count(), 0);
    QVERIFY(scene.host()->isVisible() && scene.editor()->property("visible").toBool());
    QCOMPARE(scene.note()->property("text").toString(), QStringLiteral("new editor input after reconnect"));
    QTRY_COMPARE(scene.app->recovery()->draft(run).value(QStringLiteral("text")).toString(),
                 QStringLiteral("new editor input after reconnect"));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::sameRunWithoutDraftUsesLatestCommittedNote()
{
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    auto run = finishedRun();
    scene.publish(run);
    QTRY_VERIFY(scene.host()->isVisible() && scene.editor()->property("opened").toBool());
    QVERIFY(scene.note()->property("text").toString().isEmpty());
    QVERIFY(scene.click(QStringLiteral("deferReflectionButton")));
    QTRY_VERIFY(!scene.host()->isVisible());
    QVERIFY(scene.app->recovery()->draft(run).isEmpty());
    run.insert(QStringLiteral("reflection"), QVariantMap{{QStringLiteral("text"), QStringLiteral("latest note from another editor")},
               {QStringLiteral("mood"), QStringLiteral("bad")}});
    scene.backend.resetRuns({QJsonObject::fromVariantMap(run)});
    scene.app->recovery()->requestOpen(run.value(QStringLiteral("run_id")).toString());
    QTRY_VERIFY(scene.host()->isVisible() && scene.editor()->property("opened").toBool());
    QCOMPARE(scene.note()->property("text").toString(), QStringLiteral("latest note from another editor"));
    QCOMPARE(scene.editor()->property("mood").toString(), QStringLiteral("bad"));
    QVERIFY(!scene.editor()->property("conflictUnresolved").toBool());
    QCOMPARE(scene.backend.commands.count(QStringLiteral("SetRunReflection")), 0);
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::answerStaysOpenForTheNote()
{
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    scene.mainWindow()->hide();
    const auto run = finishedRun();
    scene.publish(run);
    QTRY_VERIFY(scene.host()->isVisible() && scene.editor()->property("opened").toBool());
    QVERIFY(scene.editor()->property("askingResult").toBool());
    QVERIFY(scene.item(QStringLiteral("confirmCompletedButton")));
    QVERIFY(!scene.item(QStringLiteral("saveReflectionButton")));
    QVERIFY(scene.click(QStringLiteral("confirmCompletedButton")));
    QTRY_COMPARE(scene.backend.commands.count(QStringLiteral("CorrectRun")), 1);
    QTRY_VERIFY(scene.editor()->property("answered").toBool());
    QVERIFY(scene.host()->isVisible());
    QVERIFY(!scene.editor()->property("askingResult").toBool());
    QVERIFY(scene.item(QStringLiteral("popupResultBox")));
    QVERIFY(!scene.item(QStringLiteral("confirmCompletedButton")));
    QCOMPARE(scene.backend.commands.count(QStringLiteral("SetRunReflection")), 0);
    QVERIFY(scene.enterNote(QStringLiteral("answered first, noted after")));
    QVERIFY(scene.click(QStringLiteral("popupMood_ok")));
    QCOMPARE(scene.editor()->property("mood").toString(), QStringLiteral("ok"));
    QVERIFY(scene.click(QStringLiteral("saveReflectionButton")));
    QTRY_VERIFY(!scene.host()->isVisible());
    QCOMPARE(scene.backend.commands.count(QStringLiteral("SetRunReflection")), 1);
    QTRY_VERIFY(scene.app->recovery()->draft(run).isEmpty());
    // No reminder pill: the record was finished, not put aside.
    QVERIFY(!scene.item(QStringLiteral("reopenDeferredButton")));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::answerWithAJobThenFinishClosesCleanly()
{
    // A run without a job: the job picked in the popup goes with the answer,
    // and 完成 afterwards neither resends it nor gets stuck on ERR_NO_CHANGES.
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    scene.mainWindow()->hide();
    auto run = finishedRun();
    run.insert(QStringLiteral("job_id"), 0);
    run.insert(QStringLiteral("job_name"), QString::fromUtf8("未知"));
    scene.publish(run);
    QTRY_VERIFY(scene.host()->isVisible() && scene.editor()->property("opened").toBool());
    QVERIFY(scene.editor()->property("needsJob").toBool());
    QVERIFY(scene.item(QStringLiteral("completedJobComboCompact")));
    scene.editor()->setProperty("jobIndex", 1);
    QVERIFY(scene.editor()->property("selectedJobId").toInt() > 0);
    QVERIFY(scene.click(QStringLiteral("confirmCompletedButton")));
    QTRY_COMPARE(scene.backend.commands.count(QStringLiteral("CorrectRun")), 1);
    QTRY_VERIFY(scene.editor()->property("answered").toBool());
    QVERIFY(!scene.editor()->property("needsJob").toBool());
    QCOMPARE(scene.editor()->property("selectedJobId").toInt(), 0);
    QVERIFY(scene.click(QStringLiteral("saveReflectionButton")));
    QTRY_VERIFY(!scene.host()->isVisible());
    QCOMPARE(scene.backend.commands.count(QStringLiteral("CorrectRun")), 1);
    QCOMPARE(scene.backend.commands.count(QStringLiteral("SetRunReflection")), 0);
    QVERIFY(scene.editor()->property("errorText").toString().isEmpty());
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::aRefusedNoteAfterTheAnswerStillCollapses()
{
    // 收起 after an answer saves like 完成; when that save is refused, the next
    // 收起 closes the ordinary way and the draft is kept.
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    scene.mainWindow()->hide();
    const auto run = finishedRun();
    scene.publish(run);
    QTRY_VERIFY(scene.host()->isVisible() && scene.editor()->property("opened").toBool());
    QVERIFY(scene.click(QStringLiteral("confirmLeftButton")));
    QTRY_VERIFY(scene.editor()->property("answered").toBool());
    QVERIFY(scene.enterNote(QStringLiteral("kept after a refusal")));
    scene.backend.refuseNextReflection = true;
    QVERIFY(scene.click(QStringLiteral("deferReflectionButton")));
    QTRY_VERIFY(!scene.editor()->property("errorText").toString().isEmpty());
    QVERIFY(scene.host()->isVisible());
    QCOMPARE(scene.backend.commands.count(QStringLiteral("SetRunReflection")), 1);
    QVERIFY(scene.click(QStringLiteral("deferReflectionButton")));
    QTRY_VERIFY(!scene.host()->isVisible());
    QCOMPARE(scene.backend.commands.count(QStringLiteral("SetRunReflection")), 1);
    QCOMPARE(scene.app->recovery()->draft(run).value(QStringLiteral("text")).toString(), QStringLiteral("kept after a refusal"));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::aDeletedRecordsReminderCanBeRemoved()
{
    // Review I2-1 / P-2: a reminder whose record was purged stayed in 待处理 for
    // good. Opening it ends the reminder; the kept draft can be copied and then
    // removed with 移除提醒, which asks once more because it deletes typed text.
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    scene.settings.setPromptPresentation(QStringLiteral("inbox"));
    const auto run = finishedRun(true, 71);
    const QString id = run.value(QStringLiteral("run_id")).toString();
    scene.publish(run);
    QTRY_COMPARE(scene.app->recovery()->count(), 1);
    QVERIFY(scene.app->recovery()->saveDraft(run, {{QStringLiteral("text"), QStringLiteral("copy me first")},
        {QStringLiteral("mood"), QStringLiteral("good")}, {QStringLiteral("jobId"), 24}}));
    scene.publishedRuns = QJsonArray();
    scene.backend.resetRuns({}); // Purged by retention.
    QVERIFY(!scene.item(QStringLiteral("dismissPendingWork_") + id));
    QVERIFY(scene.click(QStringLiteral("openPendingWork_") + id));
    QTRY_VERIFY(scene.host()->isVisible() && scene.editor()->property("opened").toBool());
    QVERIFY(scene.editor()->property("unavailable").toBool());
    QCOMPARE(scene.note()->property("text").toString(), QStringLiteral("copy me first"));
    QVERIFY(scene.click(QStringLiteral("deferReflectionButton")));
    QTRY_VERIFY(!scene.host()->isVisible());
    QTRY_COMPARE(scene.app->recovery()->count(), 1);
    // The draft is still in 待处理, so the notice offers it again.
    auto *notice = scene.root->findChild<QQuickWindow *>(QStringLiteral("recoveryNoticeWindow"));
    QVERIFY(notice);
    QTRY_VERIFY(notice->isVisible());
    notice->hide();
    QVERIFY(scene.click(QStringLiteral("dismissPendingWork_") + id));
    QTest::qWait(50);
    QCOMPARE(scene.app->recovery()->count(), 1);
    QVERIFY(scene.click(QStringLiteral("dismissPendingWork_") + id));
    QTRY_COMPARE(scene.app->recovery()->count(), 0);
    QVERIFY(scene.app->recovery()->draft(run).isEmpty());
    QCOMPARE(scene.backend.commands.count(QStringLiteral("SetRunReflection")), 0);

    // Review RV3-3: without a draft, opening it ends the reminder and keeps nothing. Putting
    // that popup away must not announce 「已收起到待处理」 with a 重新打开 that opens nothing.
    const auto bare = finishedRun(true, 72);
    const QString bareId = bare.value(QStringLiteral("run_id")).toString();
    scene.publish(bare);
    QTRY_COMPARE(scene.app->recovery()->count(), 1);
    scene.publishedRuns = QJsonArray();
    scene.backend.resetRuns({});
    QVERIFY(scene.click(QStringLiteral("openPendingWork_") + bareId));
    QTRY_VERIFY(scene.host()->isVisible() && scene.editor()->property("opened").toBool());
    QVERIFY(scene.editor()->property("unavailable").toBool());
    QTRY_COMPARE(scene.app->recovery()->count(), 0);
    QVERIFY(scene.click(QStringLiteral("deferReflectionButton")));
    QTRY_VERIFY(!scene.host()->isVisible());
    QTest::qWait(100);
    QVERIFY(!notice->isVisible());
    QVERIFY(!scene.item(QStringLiteral("reopenDeferredButton")));
    QCOMPARE(scene.app->recovery()->count(), 0);
    QCOMPARE(scene.backend.commands.count(QStringLiteral("SetRunReflection")), 0);
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::reviewFromTheNoteEditorOpensTheReasonDialogForThatRecord()
{
    // Review K1-3 / L1-2: 在历史记录中复核 for the record already selected in the
    // history page toggled the selection off, and nothing opened.
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    auto run = finishedRun(true, 91);
    run.insert(QStringLiteral("pending_review"), true); // Kept after a calibration withdrawal.
    scene.publishedRuns.append(QJsonObject::fromVariantMap(run));
    scene.backend.resetRuns(scene.publishedRuns);
    scene.app->navigate(1);
    scene.app->selectRun(run);
    QVERIFY(scene.app->hasSelection());
    QVERIFY(QMetaObject::invokeMethod(scene.root.get(), "openReflection",
                                      Q_ARG(QVariant, QVariant(run)), Q_ARG(QVariant, QVariant(QString()))));
    QTRY_VERIFY(scene.editor()->property("opened").toBool());
    QVERIFY(scene.click(QStringLiteral("reviewRecoveredRecordButton")));
    auto *reason = scene.root->findChild<QObject *>(QStringLiteral("reasonDialog"));
    QVERIFY(reason);
    QTRY_VERIFY(reason->property("visible").toBool());
    QCOMPARE(scene.root->property("reasonAction").toString(), QStringLiteral("review"));
    QCOMPARE(scene.root->property("reasonRunId").toString(), run.value(QStringLiteral("run_id")).toString());
    QCOMPARE(scene.root->property("reasonRevision").toInt(), 1);
    QCOMPARE(scene.app->selectedRun().value(QStringLiteral("run_id")).toString(),
             run.value(QStringLiteral("run_id")).toString());
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::theMainWindowNoteEditorKeepsItsOwnLook()
{
    // Review K1-4 / L2-5 / P-7: editing a note from the history page showed the
    // reminder's wording (本次笔记待补录, 收起, 关闭只会收起提醒…) and every close
    // put up the 「已收起到待处理」 pill. Only the popup is a reminder.
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    auto run = finishedRun(true, 92);
    run.insert(QStringLiteral("reflection"), QVariantMap{{QStringLiteral("text"), QStringLiteral("an old note")},
                                                         {QStringLiteral("mood"), QStringLiteral("ok")}});
    scene.publishedRuns.append(QJsonObject::fromVariantMap(run));
    scene.backend.resetRuns(scene.publishedRuns);
    QVERIFY(QMetaObject::invokeMethod(scene.root.get(), "openReflection",
                                      Q_ARG(QVariant, QVariant(run)), Q_ARG(QVariant, QVariant(QString()))));
    QTRY_VERIFY(scene.editor()->property("opened").toBool());
    QStringList texts;
    const auto collect = [&](auto &&self, QQuickItem *parent) -> void {
        const QString text = parent->property("text").toString();
        if (!text.isEmpty() && parent->isVisible()) texts.append(text);
        for (auto *child : parent->childItems()) self(self, child);
    };
    collect(collect, scene.editor()->property("contentItem").value<QQuickItem *>());
    QVERIFY2(texts.contains(QString::fromUtf8("导随笔记")), qPrintable(texts.join(QLatin1Char('|'))));
    QVERIFY2(texts.contains(QString::fromUtf8("编辑笔记")), qPrintable(texts.join(QLatin1Char('|'))));
    QVERIFY2(!texts.contains(QString::fromUtf8("本次笔记待补录")), qPrintable(texts.join(QLatin1Char('|'))));
    QVERIFY2(!texts.join(QLatin1Char('|')).contains(QString::fromUtf8("关闭只会收起提醒")),
             qPrintable(texts.join(QLatin1Char('|'))));
    QVERIFY(!scene.item(QStringLiteral("deferReflectionButton")));
    scene.mainWindow()->requestActivate();
    QVERIFY(QTest::qWaitForWindowActive(scene.mainWindow()));
    QTest::keyClick(scene.mainWindow(), Qt::Key_Escape);
    QTRY_VERIFY(!scene.editor()->property("visible").toBool());
    QTest::qWait(100);
    auto *notice = scene.root->findChild<QQuickWindow *>(QStringLiteral("recoveryNoticeWindow"));
    QVERIFY(notice && !notice->isVisible());
    QCOMPARE(scene.backend.commands.count(QStringLiteral("SetRunReflection")), 0);
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::escapeInTheMainWindowEditorKeepsAnUnsavedDraft()
{
    // Review RV3-4: K1-4 gave the main-window note editor CloseOnEscape, so with
    // the recovery journal unwritable Esc closed it and the typed text was gone.
    // Every managed editor puts itself away through defer(), which stays open
    // with the draft error until the draft is kept; only the chrome differs.
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    const auto run = finishedRun(true, 74);
    scene.publishedRuns.append(QJsonObject::fromVariantMap(run));
    scene.backend.resetRuns(scene.publishedRuns);
    QVERIFY(QMetaObject::invokeMethod(scene.root.get(), "openReflection",
                                      Q_ARG(QVariant, QVariant(run)), Q_ARG(QVariant, QVariant(QString()))));
    QTRY_VERIFY(scene.editor()->property("opened").toBool());
    QVERIFY(!scene.editor()->property("standalone").toBool());
    const QString journal = QDir(QFileInfo(scene.settings.instanceFilePath()).absolutePath())
        .filePath(QStringLiteral("recovery/") + scene.app->recovery()->databaseKey() + QStringLiteral(".json"));
    QLockFile lock(journal + QStringLiteral(".lock"));
    QVERIFY(lock.tryLock(0));
    QVERIFY(scene.note()->setProperty("text", QStringLiteral("typed while the disk is busy")));
    scene.mainWindow()->requestActivate();
    QVERIFY(QTest::qWaitForWindowActive(scene.mainWindow()));
    QTest::keyClick(scene.mainWindow(), Qt::Key_Escape);
    QTRY_VERIFY(!scene.editor()->property("errorText").toString().isEmpty());
    QVERIFY(scene.editor()->property("visible").toBool());
    QCOMPARE(scene.note()->property("text").toString(), QStringLiteral("typed while the disk is busy"));
    lock.unlock();
    QTest::keyClick(scene.mainWindow(), Qt::Key_Escape);
    QTRY_VERIFY(!scene.editor()->property("visible").toBool());
    QCOMPARE(scene.app->recovery()->draft(run).value(QStringLiteral("text")).toString(),
             QStringLiteral("typed while the disk is busy"));
    QTest::qWait(100);
    auto *notice = scene.root->findChild<QQuickWindow *>(QStringLiteral("recoveryNoticeWindow"));
    QVERIFY(notice && !notice->isVisible());
    QCOMPARE(scene.backend.commands.count(QStringLiteral("SetRunReflection")), 0);
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::aPendingRecordWithAKnownResultCanBeReviewed()
{
    // Review K1-2: a run kept for review with its result already recorded (crash
    // recovery, a withdrawn calibration) opened as a note popup with no way to
    // settle it, so it stayed in 待处理 for good.
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    auto run = finishedRun(true, 93);
    run.insert(QStringLiteral("result"), QStringLiteral("INTERRUPTED"));
    run.insert(QStringLiteral("pending_review"), true);
    const QString id = run.value(QStringLiteral("run_id")).toString();
    scene.publishedRuns.append(QJsonObject::fromVariantMap(run));
    scene.backend.resetRuns(scene.publishedRuns);
    scene.app->recovery()->refresh();
    QTRY_COMPARE(scene.app->recovery()->count(), 1);
    QTRY_VERIFY(!scene.app->recovery()->busy());
    QVERIFY(scene.click(QStringLiteral("openPendingWork_") + id));
    QTRY_VERIFY(scene.host()->isVisible() && scene.editor()->property("opened").toBool());
    QVERIFY(!scene.editor()->property("askingResult").toBool());
    QVERIFY(scene.click(QStringLiteral("reviewRecoveredRecordButtonCompact")));
    QTRY_VERIFY(!scene.host()->isVisible());
    auto *reason = scene.root->findChild<QObject *>(QStringLiteral("reasonDialog"));
    QVERIFY(reason);
    QTRY_VERIFY(reason->property("visible").toBool());
    QCOMPARE(scene.root->property("reasonAction").toString(), QStringLiteral("review"));
    QCOMPARE(scene.app->selectedRun().value(QStringLiteral("run_id")).toString(), id);
    QCOMPARE(scene.app->currentPage(), 1);
    QVERIFY(!scene.item(QStringLiteral("reopenDeferredButton")));
    scene.item(QStringLiteral("reasonField"))->setProperty("text", QString::fromUtf8("核对过了"));
    QVERIFY(scene.click(QStringLiteral("reasonConfirmButton")));
    QTRY_VERIFY(!reason->property("visible").toBool());
    QTRY_COMPARE(scene.app->recovery()->count(), 0);
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::floatingModeOpensThePopupBesideAVisibleFloat()
{
    // Review L2-8 / K1-5 / P-6: 浮窗提示 behaved exactly like 仅保留待办. As the
    // setting says, it opens the popup while the float is up; with the float
    // closed the prompt only waits in 待处理 (silentModesAndDisabledPrompts).
    Scene scene;
    scene.settings.setPromptPresentation(QStringLiteral("floating"));
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    scene.app->statisticsOverlay()->show();
    QVERIFY(QTest::qWaitForWindowExposed(scene.floating()));
    const auto run = finishedRun(true, 94);
    scene.publish(run);
    QTRY_VERIFY(scene.host()->isVisible() && scene.editor()->property("opened").toBool());
    QCOMPARE(scene.editor()->property("runId").toString(), run.value(QStringLiteral("run_id")).toString());
    QVERIFY(scene.click(QStringLiteral("deferReflectionButton")));
    QTRY_VERIFY(!scene.host()->isVisible());
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::aNoteEditorLeftInTheHiddenWindowDoesNotHoldPopupsBack()
{
    // Review L2-10: a note editor left open in the main window when it went to
    // the tray stayed "visible", and every automatic popup was dropped until the
    // window came back. Hiding the window puts that editor away, its draft kept.
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    const auto old = finishedRun(true, 95);
    scene.publishedRuns.append(QJsonObject::fromVariantMap(old));
    scene.backend.resetRuns(scene.publishedRuns);
    QVERIFY(QMetaObject::invokeMethod(scene.root.get(), "openReflection",
                                      Q_ARG(QVariant, QVariant(old)), Q_ARG(QVariant, QVariant(QString()))));
    QTRY_VERIFY(scene.editor()->property("opened").toBool());
    scene.note()->setProperty("text", QStringLiteral("half a note"));
    scene.mainWindow()->hide();
    QTRY_VERIFY(!scene.editor()->property("visible").toBool());
    QCOMPARE(scene.app->recovery()->draft(old).value(QStringLiteral("text")).toString(), QStringLiteral("half a note"));
    const auto run = finishedRun(true, 96);
    scene.publish(run);
    QTRY_VERIFY(scene.host()->isVisible() && scene.editor()->property("opened").toBool());
    QCOMPARE(scene.editor()->property("runId").toString(), run.value(QStringLiteral("run_id")).toString());
    QVERIFY(scene.click(QStringLiteral("deferReflectionButton")));
    QTRY_VERIFY(!scene.host()->isVisible());
    QCOMPARE(scene.backend.commands.count(QStringLiteral("SetRunReflection")), 0);
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::aLostConnectionPutsThePopupAwayWithANotice()
{
    // Review L2-6: a Collector disconnect closed the popup with no word, and the
    // run was never offered again. It is put away like 收起: the draft is kept
    // and the 「已收起到待处理」 notice says where it went.
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    scene.mainWindow()->hide();
    const auto run = finishedRun(true, 97);
    scene.publish(run);
    QTRY_VERIFY(scene.host()->isVisible() && scene.editor()->property("opened").toBool());
    QVERIFY(scene.enterNote(QStringLiteral("typed before the drop")));
    scene.app->recovery()->invalidateIdentity(); // What a reconnect does first.
    QTRY_VERIFY(!scene.host()->isVisible());
    QTRY_VERIFY(scene.item(QStringLiteral("reopenDeferredButton")));
    QCOMPARE(scene.app->recovery()->draft(run).value(QStringLiteral("text")).toString(),
             QStringLiteral("typed before the drop"));
    QCOMPARE(scene.app->recovery()->count(), 1);
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::bringingTheWindowForwardKeepsItMaximised()
{
    // Review K1-6: 查看全部待处理 called showNormal() and un-maximised the window.
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    scene.mainWindow()->showMaximized();
    if (!QTest::qWaitFor([&] { return scene.mainWindow()->visibility() == QWindow::Maximized; }))
        QSKIP("This platform plugin does not report a maximised window.");
    scene.app->recovery()->requestInbox();
    QTRY_VERIFY(scene.inbox()->property("opened").toBool());
    QTest::qWait(50);
    QCOMPARE(scene.mainWindow()->visibility(), QWindow::Maximized);
    QVERIFY(QMetaObject::invokeMethod(scene.inbox(), "close"));
    QTRY_VERIFY(!scene.inbox()->property("visible").toBool());
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::openingAnotherRecordBringsTheOpenEditorForward()
{
    // Review K1-7: 继续处理 or 重新打开 while the editor was open for another
    // record did nothing at all; the open editor's window is brought forward.
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    const auto open = finishedRun(true, 98);
    const auto other = finishedRun(true, 99);
    scene.publishedRuns.append(QJsonObject::fromVariantMap(open));
    scene.publishedRuns.append(QJsonObject::fromVariantMap(other));
    scene.backend.resetRuns(scene.publishedRuns);
    scene.app->recovery()->offer(other, QStringLiteral("reflection"));
    QVERIFY(QMetaObject::invokeMethod(scene.root.get(), "openReflection",
                                      Q_ARG(QVariant, QVariant(open)), Q_ARG(QVariant, QVariant(QString()))));
    QTRY_VERIFY(scene.editor()->property("opened").toBool());
    scene.mainWindow()->showMinimized();
    if (!QTest::qWaitFor([&] { return scene.mainWindow()->visibility() == QWindow::Minimized; }))
        QSKIP("This platform plugin does not report a minimised window.");
    scene.app->recovery()->requestOpen(other.value(QStringLiteral("run_id")).toString());
    QTRY_VERIFY(scene.mainWindow()->visibility() != QWindow::Minimized);
    QCOMPARE(scene.editor()->property("runId").toString(), open.value(QStringLiteral("run_id")).toString());
    QVERIFY(scene.editor()->property("visible").toBool());
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::noNpcapWarningWhileNothingIsKnownAboutNpcap()
{
    // Review K2-1 (the 总览 banner and the side panel's Npcap row, reported by
    // Y5b): without a capture status - no pipe - nothing is known about Npcap,
    // and 「未检测到 Npcap」 / 「未安装」 would be a false claim.
    Scene scene;
    scene.backend.setNpcapMissing(true);
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    const auto shown = [&](const QString &text) {
        bool found = false;
        const auto visit = [&](auto &&self, QQuickItem *parent) -> void {
            if (parent->isVisible() && parent->property("text").toString() == text) found = true;
            for (auto *child : parent->childItems()) self(self, child);
        };
        visit(visit, scene.mainWindow()->contentItem());
        return found;
    };
    // A real answer that Npcap is missing is shown, as before.
    QTRY_VERIFY(scene.item(QStringLiteral("npcapMissingBanner")));
    QVERIFY(shown(QString::fromUtf8("未安装")));
    scene.backend.connected = false;
    Q_EMIT scene.backend.connectionChanged();
    QTRY_VERIFY(!scene.app->backendConnected());
    QTRY_VERIFY(!scene.item(QStringLiteral("npcapMissingBanner")));
    QVERIFY(!shown(QString::fromUtf8("未安装")));
    QVERIFY(!shown(QString::fromUtf8("就绪")));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

namespace {
/// A note reminder opened by hand from 待处理, then a duty that ends without a
/// clear signal while it is on screen: the question is queued, not shown.
struct QueuedQuestion {
    QVariantMap note = finishedRun(true, 81);
    QVariantMap question = finishedRun(false, 82);
};

bool openNoteThenQueueQuestion(Scene &scene, const QueuedQuestion &runs, QSignalSpy &offers)
{
    scene.settings.setPromptPresentation(QStringLiteral("inbox"));
    scene.publish(runs.note);
    if (!QTest::qWaitFor([&] { return scene.app->recovery()->count() == 1; })) return false;
    scene.settings.setPromptPresentation(QStringLiteral("window"));
    scene.app->recovery()->requestOpen(runs.note.value(QStringLiteral("run_id")).toString());
    if (!QTest::qWaitFor([&] { return scene.host()->isVisible() && scene.editor()->property("opened").toBool(); }))
        return false;
    scene.publish(runs.question);
    if (!QTest::qWaitFor([&] { return offers.count() == 1 && scene.app->recovery()->count() == 2; })) return false;
    return scene.editor()->property("runId").toString() == runs.note.value(QStringLiteral("run_id")).toString();
}
} // namespace

void RecoveryUiTests::aQuestionAnsweredInThePagerIsNotAskedAgain()
{
    // Review L2-2: paging to a queued question and answering it there left it in
    // the automatic queue, so closing the popup asked 「通关了吗？」 again.
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    QSignalSpy offers(scene.app.get(), &mr::AppController::resultConfirmationRequested);
    const QueuedQuestion runs;
    QVERIFY(openNoteThenQueueQuestion(scene, runs, offers));
    const QString questionId = runs.question.value(QStringLiteral("run_id")).toString();
    QTRY_VERIFY(scene.item(QStringLiteral("popupNextButton")));
    const bool questionFirst = scene.editor()->property("queueIndex").toInt() == 1;
    QVERIFY(scene.click(questionFirst ? QStringLiteral("popupPreviousButton") : QStringLiteral("popupNextButton")));
    QTRY_COMPARE(scene.editor()->property("runId").toString(), questionId);
    QVERIFY(scene.editor()->property("askingResult").toBool());
    QVERIFY(scene.click(QStringLiteral("confirmCompletedButton")));
    QTRY_VERIFY(scene.editor()->property("answered").toBool());
    QVERIFY(scene.click(QStringLiteral("saveReflectionButton")));
    QTRY_VERIFY(!scene.host()->isVisible());
    QTest::qWait(300);
    QVERIFY(!scene.host()->isVisible());
    QCOMPARE(offers.count(), 1);
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::aQuestionAnsweredElsewhereIsNotAskedAgain()
{
    // Review L2-2: a queued question answered somewhere else (here: directly,
    // as the history page would) is dropped from the automatic queue.
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    QSignalSpy offers(scene.app.get(), &mr::AppController::resultConfirmationRequested);
    QSignalSpy reviewed(scene.app.get(), &mr::AppController::mutationSucceeded);
    const QueuedQuestion runs;
    QVERIFY(openNoteThenQueueQuestion(scene, runs, offers));
    scene.app->resolveRunResult(runs.question.value(QStringLiteral("run_id")).toString(), 1,
                                QStringLiteral("COMPLETED"), QStringLiteral("answered on the history page"));
    QTRY_COMPARE(reviewed.count(), 1);
    QVERIFY(scene.click(QStringLiteral("deferReflectionButton")));
    QTRY_VERIFY(!scene.host()->isVisible());
    QTest::qWait(300);
    QVERIFY(!scene.host()->isVisible());
    QCOMPARE(offers.count(), 1);
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::anAutomaticClearShowsTheCollectorsCountBeforeAndAfter()
{
    // Review L2-1 / I2-2 / P-3: the prompt opens on the run's own events, before
    // the dashboard has read the run; the popup shows the Collector's count from
    // before the run and the one read after it - never a number computed here.
    Scene scene;
    scene.backend.setAchievement(2000, 10);
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    QTRY_COMPARE(scene.app->dashboard().value(QStringLiteral("achievement_progress")).toInt(), 10);
    scene.mainWindow()->hide();
    scene.publish(finishedRun(true));
    QTRY_VERIFY(scene.host()->isVisible() && scene.editor()->property("opened").toBool());
    QTRY_COMPARE(scene.editor()->property("progressTo").toInt(), 11);
    QCOMPARE(scene.editor()->property("progressFrom").toInt(), 10);
    const QStringList texts = scene.hostTexts();
    QVERIFY2(texts.contains(QString::fromUtf8("已计入导随次数")), qPrintable(texts.join(QLatin1Char('|'))));
    QVERIFY2(texts.contains(QString::fromUtf8("10 →")), qPrintable(texts.join(QLatin1Char('|'))));
    QVERIFY2(texts.contains(QStringLiteral("11")), qPrintable(texts.join(QLatin1Char('|'))));
    QVERIFY(scene.click(QStringLiteral("deferReflectionButton")));
    QTRY_VERIFY(!scene.host()->isVisible());
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::anAnsweredClearClaimsTheCountOnlyWhenTheRunContributes_data()
{
    QTest::addColumn<bool>("contributes");
    QTest::newRow("contributes") << true;
    QTest::newRow("excluded from the goal") << false;
}

void RecoveryUiTests::anAnsweredClearClaimsTheCountOnlyWhenTheRunContributes()
{
    // Review L2-1 second path: answering 通关 must not print 「已计入导随次数」 and
    // "N → N+1" for a run the Collector does not count towards the goal.
    QFETCH(bool, contributes);
    Scene scene;
    scene.backend.setAchievement(2000, 10);
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    QTRY_COMPARE(scene.app->dashboard().value(QStringLiteral("achievement_progress")).toInt(), 10);
    scene.mainWindow()->hide();
    auto run = finishedRun();
    run.insert(QStringLiteral("contributes_to_goal"), contributes);
    scene.publish(run);
    QTRY_VERIFY(scene.host()->isVisible() && scene.editor()->property("opened").toBool());
    QVERIFY(scene.click(QStringLiteral("confirmCompletedButton")));
    QTRY_VERIFY(scene.editor()->property("answered").toBool());
    if (contributes) {
        QTRY_COMPARE(scene.editor()->property("progressTo").toInt(), 11);
        QCOMPARE(scene.editor()->property("progressFrom").toInt(), 10);
    } else {
        QTest::qWait(300);
        QTRY_COMPARE(scene.app->dashboard().value(QStringLiteral("achievement_progress")).toInt(), 10);
    }
    const QStringList texts = scene.hostTexts();
    QCOMPARE(texts.contains(QString::fromUtf8("已计入导随次数")), contributes);
    QCOMPARE(texts.contains(QString::fromUtf8("10 →")), contributes);
    QCOMPARE(texts.contains(QStringLiteral("11")), contributes);
    QVERIFY(scene.click(QStringLiteral("saveReflectionButton")));
    QTRY_VERIFY(!scene.host()->isVisible());
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::overviewUsesUnifiedCountAndSharedActions()
{
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    scene.settings.setPromptPresentation(QStringLiteral("inbox"));
    const auto result = finishedRun(false, 31);
    const auto note = finishedRun(true, 32);
    const auto draft = finishedRun(true, 33);
    scene.publish(result);
    scene.publish(note);
    scene.publishedRuns.append(QJsonObject::fromVariantMap(draft));
    scene.backend.resetRuns(scene.publishedRuns);
    QVERIFY(scene.app->recovery()->saveDraft(result, {{QStringLiteral("text"), QStringLiteral("result note draft")},
        {QStringLiteral("mood"), QStringLiteral("good")}, {QStringLiteral("jobId"), 24}}));
    QVERIFY(scene.app->recovery()->saveDraft(draft, {{QStringLiteral("text"), QStringLiteral("another draft")},
        {QStringLiteral("mood"), QStringLiteral("ok")}, {QStringLiteral("jobId"), 24}}));
    scene.app->recovery()->refresh();
    QTRY_COMPARE(scene.app->recovery()->count(), 3);
    QTRY_COMPARE(scene.app->pendingReviewCount(), 1);
    QTRY_VERIFY(!scene.app->recovery()->busy());
    auto *card = scene.item(QStringLiteral("dashboardPendingWorkCard"));
    QVERIFY(card);
    auto *title = card->findChild<QObject *>(QStringLiteral("pendingWorkTitle"));
    QVERIFY(title);
    QTRY_COMPARE(title->property("text").toString(), QString::fromUtf8("待处理 · 3"));
    QVERIFY(!scene.root->findChild<QObject *>(QStringLiteral("pendingWorkButton")));
    QVERIFY(!scene.item(QStringLiteral("pendingReviewCompletedButton")));
    QVERIFY(!scene.host()->isVisible());

    QVERIFY(scene.click(QStringLiteral("pendingWorkAllButton")));
    QTRY_VERIFY(scene.inbox()->property("opened").toBool());
    QTRY_VERIFY(!scene.app->recovery()->busy());
    QVERIFY(scene.click(QStringLiteral("openPendingWork_") + result.value(QStringLiteral("run_id")).toString()));
    QTRY_VERIFY(scene.editor()->property("opened").toBool());
    QCOMPARE(scene.editor()->property("runId").toString(), result.value(QStringLiteral("run_id")).toString());
    QCOMPARE(scene.note()->property("text").toString(), QStringLiteral("result note draft"));
    QCOMPARE(scene.backend.commands.count(QStringLiteral("CorrectRun")), 0);
    QCOMPARE(scene.backend.commands.count(QStringLiteral("SetRunReflection")), 0);
    QVERIFY(scene.click(QStringLiteral("deferReflectionButton")));
    QTRY_VERIFY(!scene.host()->isVisible());

    const QString firstId = scene.app->recovery()->items().first().toMap().value(QStringLiteral("runId")).toString();
    QVERIFY(scene.click(QStringLiteral("openPendingWork_") + firstId));
    QTRY_VERIFY(scene.editor()->property("opened").toBool());
    QCOMPARE(scene.editor()->property("runId").toString(), firstId);
    QCOMPARE(scene.backend.commands.count(QStringLiteral("CorrectRun")), 0);
    QCOMPARE(scene.backend.commands.count(QStringLiteral("SetRunReflection")), 0);
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::overviewDraftOnlyDisappearsAfterSave()
{
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    scene.settings.setReflectPrompt(false);
    const auto draft = finishedRun(true, 41);
    scene.backend.resetRuns({QJsonObject::fromVariantMap(draft)});
    QVERIFY(scene.app->recovery()->saveDraft(draft, {{QStringLiteral("text"), QStringLiteral("draft only")},
        {QStringLiteral("mood"), QStringLiteral("good")}, {QStringLiteral("jobId"), 24}}));
    scene.app->refreshDashboard();
    QTRY_COMPARE(scene.app->pendingReviewCount(), 0);
    QTRY_COMPARE(scene.app->recovery()->count(), 1);
    auto *card = scene.item(QStringLiteral("dashboardPendingWorkCard"));
    QVERIFY(card);
    QVERIFY(scene.click(QStringLiteral("openPendingWork_") + draft.value(QStringLiteral("run_id")).toString()));
    QTRY_VERIFY(scene.editor()->property("opened").toBool());
    QCOMPARE(scene.note()->property("text").toString(), QStringLiteral("draft only"));
    QVERIFY(scene.click(QStringLiteral("saveReflectionButton")));
    QTRY_COMPARE(scene.app->recovery()->count(), 0);
    QTRY_VERIFY(!card->isVisible());
    QVERIFY(!scene.root->findChild<QObject *>(QStringLiteral("pendingWorkButton")));
    QCOMPARE(scene.backend.commands.count(QStringLiteral("SetRunReflection")), 1);
    QCOMPARE(scene.backend.commands.count(QStringLiteral("CorrectRun")), 0);
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::overviewFailedReadKeepsRecoveryAccess()
{
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    auto *card = scene.root->findChild<QQuickItem *>(QStringLiteral("dashboardPendingWorkCard"));
    QVERIFY(card && !card->isVisible());
    scene.backend.refusePendingReads = true;
    scene.app->recovery()->refresh();
    QTRY_VERIFY(!scene.app->recovery()->error().isEmpty());
    QTRY_VERIFY(card->isVisible());
    QCOMPARE(scene.app->recovery()->count(), 0);
    for (auto *item : card->findChildren<QQuickItem *>())
        QVERIFY(!item->isVisible() || !item->property("text").toString().startsWith(QString::fromUtf8("没有待处理")));
    QVERIFY(scene.click(QStringLiteral("pendingWorkAllButton")));
    QTRY_VERIFY(scene.inbox()->property("opened").toBool());
    scene.backend.refusePendingReads = false;
    QVERIFY(scene.click(QStringLiteral("refreshPendingWorkButton")));
    QTRY_VERIFY(!scene.app->recovery()->busy());
    QTRY_VERIFY(scene.app->recovery()->error().isEmpty());
    QTRY_VERIFY(!card->isVisible());
    QVERIFY(QMetaObject::invokeMethod(scene.inbox(), "close"));
    QTRY_VERIFY(!scene.inbox()->property("visible").toBool());
    const auto task = finishedRun(true, 51);
    scene.app->recovery()->offer(task, QStringLiteral("reflection"));
    QTRY_COMPARE(scene.app->recovery()->count(), 1);
    scene.backend.refusePendingReads = true;
    scene.app->recovery()->refresh();
    QTRY_VERIFY(!scene.app->recovery()->error().isEmpty());
    QCOMPARE(scene.app->recovery()->count(), 1);
    QVERIFY(card->isVisible());
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::overviewEvidence_data()
{
    QTest::addColumn<bool>("dark");
    QTest::addColumn<bool>("compact");
    QTest::newRow("dark") << true << false;
    QTest::newRow("light") << false << false;
    QTest::newRow("compact-dark") << true << true;
    QTest::newRow("compact-light") << false << true;
}

void RecoveryUiTests::overviewEvidence()
{
    QFETCH(bool, dark);
    QFETCH(bool, compact);
    Scene scene;
    QVERIFY2(scene.create(dark), qPrintable(scene.errors));
    scene.settings.setPromptPresentation(QStringLiteral("inbox"));
    auto task = finishedRun(false, 61);
    task.insert(QStringLiteral("duty_name"), QString::fromUtf8("测试副本：森林中的重逢与旅途中尚未写完的故事"));
    scene.publish(task);
    auto note = finishedRun(true, 62);
    note.insert(QStringLiteral("duty_name"), task.value(QStringLiteral("duty_name")));
    scene.publish(note);
    scene.app->recovery()->saveDraft(task, {{QStringLiteral("text"), QStringLiteral("recoverable input")},
        {QStringLiteral("mood"), QStringLiteral("good")}, {QStringLiteral("jobId"), 24}});
    QTRY_COMPARE(scene.app->recovery()->count(), 2);
    QTRY_VERIFY(!scene.app->recovery()->busy());
    if (compact) scene.mainWindow()->resize(1100, 720);
    QTest::qWait(100);
    auto *card = scene.item(QStringLiteral("dashboardPendingWorkCard"));
    QVERIFY(card && card->isVisible());
    auto *button = scene.item(QStringLiteral("pendingWorkAllButton"));
    QVERIFY(button && button->isVisible());
    const QPointF center = button->mapToScene(QPointF(button->width() / 2, button->height() / 2));
    QVERIFY(QRectF(QPointF(), scene.mainWindow()->size()).contains(center));
    QString output = QString::fromUtf8(MR_SOURCE_DIR) + QStringLiteral("/artifacts/unified-pending-work-20261020");
    if (qEnvironmentVariable("MR_RECOVERY_EVIDENCE_SUBDIR") == QLatin1String("scale150"))
        output += QStringLiteral("/scale150");
    QVERIFY(QDir().mkpath(output));
    QVERIFY(scene.mainWindow()->grabWindow().save(output + QStringLiteral("/overview-")
        + (dark ? QStringLiteral("dark") : QStringLiteral("light"))
        + (compact ? QStringLiteral("-compact") : QString()) + QStringLiteral(".png")));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::renderedEvidence_data()
{
    QTest::addColumn<bool>("dark");
    QTest::addColumn<bool>("answered");
    QTest::newRow("ask-dark") << true << false;
    QTest::newRow("ask-light") << false << false;
    QTest::newRow("answered-dark") << true << true;
    QTest::newRow("answered-light") << false << true;
}

void RecoveryUiTests::renderedEvidence()
{
    QFETCH(bool, dark);
    QFETCH(bool, answered);
    Scene scene;
    QVERIFY2(scene.create(dark), qPrintable(scene.errors));
    scene.mainWindow()->hide();
    scene.publish(finishedRun());
    QTRY_VERIFY(scene.host()->isVisible() && scene.editor()->property("opened").toBool());
    if (answered) {
        QVERIFY(scene.click(QStringLiteral("confirmCompletedButton")));
        QTRY_VERIFY(scene.editor()->property("answered").toBool());
    }
    QVERIFY(scene.enterNote(QStringLiteral("这次配合很顺利，下次记得提前提醒机制。")));
    QTest::qWait(100);
    QVERIFY(scene.editor()->property("width").toReal() <= scene.host()->width() + 1);
    auto *defer = scene.item(QStringLiteral("deferReflectionButton"));
    QVERIFY(defer && defer->isEnabled());
    const QPointF point = defer->mapToScene(QPointF(defer->width() / 2, defer->height() / 2));
    QVERIFY(QRectF(QPointF(), scene.host()->size()).contains(point));
    QString directory = evidenceDirectory();
    if (qEnvironmentVariable("MR_RECOVERY_EVIDENCE_SUBDIR") == QLatin1String("scale150"))
        directory += QStringLiteral("/scale150");
    QVERIFY(QDir().mkpath(directory));
    const QString path = directory + QStringLiteral("/reminder-") + (dark ? QStringLiteral("dark") : QStringLiteral("light"))
        + (answered ? QStringLiteral("-answered") : QString()) + QStringLiteral(".png");
    QVERIFY(scene.host()->grabWindow().save(path));
    QVERIFY(scene.click(QStringLiteral("deferReflectionButton")));
    QTRY_VERIFY(!scene.host()->isVisible());
    scene.app->statisticsOverlay()->setModules({QStringLiteral("pending_work")});
    scene.app->statisticsOverlay()->show();
    QVERIFY(QTest::qWaitForWindowExposed(scene.floating()));
    QTest::qWait(100);
    QVERIFY(scene.floating()->grabWindow().save(directory + QStringLiteral("/pending-float-")
        + (dark ? QStringLiteral("dark") : QStringLiteral("light"))
        + (answered ? QStringLiteral("-answered") : QString()) + QStringLiteral(".png")));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

MR_DESKTOP_TEST_SUITE(RecoveryUi)
{
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QApplication app(argc, argv);
    if (!mr::registerBundledFonts()) return 2;
    QApplication::setFont(QFont(QStringLiteral("Noto Sans SC")));
    RecoveryUiTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "RecoveryUiTests.moc"
