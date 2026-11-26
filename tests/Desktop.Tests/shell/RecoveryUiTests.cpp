#include "DesktopTestRunner.h"
#include "QmlComponentTestSupport.h"
#include "ShippingWindowFixture.h"
#include "TestCollectorGuard.h"
#include "AppController.h"
#include "BundledFonts.h"
#include "RecoveryController.h"
#include "StatisticsOverlayController.h"

#include <QApplication>
#include <bit>

#include <QDir>
#include <QFileInfo>
#include <QFont>
#include <QImage>
#include <QJsonArray>
#include <QLockFile>
#include <QQuickItem>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QScreen>
#include <QSignalSpy>
#include <QStandardPaths>
#include <QTemporaryDir>
#include <QTest>
#ifdef Q_OS_WIN
#include <qt_windows.h>
#endif

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
    void aJobOnlyFinishLeavesNothingPending();
    void aPageRequestOvertakenByAnAnswerLeavesTheNoteEditable();
    void theGrowingPopupStaysOnTheScreen();
    void aRefusedNoteAfterTheAnswerStillCollapses();
    void aDeletedRecordsReminderCanBeRemoved();
    void reviewFromTheNoteEditorOpensTheReasonDialogForThatRecord();
    void theMainWindowNoteEditorKeepsItsOwnLook();
    void escapeInTheMainWindowEditorKeepsAnUnsavedDraft();
    void aPendingRecordWithAKnownResultCanBeReviewed();
    void floatingModeOpensThePopupBesideAVisibleFloat();
    void aNoteEditorLeftInTheHiddenWindowDoesNotHoldPopupsBack();
    void aNoteEditorParkedOnTheRecordPageDoesNotHoldPopupsBack();
    void theInboxOpensBesideAParkedForm();
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
    QVERIFY(mrtest::registerShippingQml());
}

void RecoveryUiTests::hiddenMainReminderCanDeferAndReopen()
{
    ShippingWindowScene scene;
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
    QTRY_VERIFY(scene.workspaceActive());
    QVERIFY(scene.backend.freshReads > reads);
    QCOMPARE(scene.note()->property("text").toString(), QStringLiteral("remember this run"));
    QCOMPARE(scene.editor()->property("runId").toString(), run.value(QStringLiteral("run_id")).toString());
    QVERIFY(scene.click(QStringLiteral("deferReflectionButton")));
    QTRY_VERIFY(!scene.editor()->property("editingActive").toBool() && !scene.host()->isVisible());
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::nativeCloseDefersTheDraftAndWaitsForAnActiveSave() const
{
    ShippingWindowScene scene;
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
    QTRY_VERIFY(scene.workspaceActive());
    QCOMPARE(scene.note()->property("text").toString(), text);
    QSignalSpy success(scene.app.get(), &mr::AppController::reflectionSaved);
    scene.backend.setReplyDelay(1200, QStringLiteral("SetRunReflection"));
    QVERIFY(scene.click(QStringLiteral("saveReflectionButton")));
    QVERIFY(scene.editor()->property("busy").toBool());
    QVERIFY(QMetaObject::invokeMethod(scene.editor(), "defer"));
    QVERIFY(scene.workspaceActive());
    QVERIFY(!scene.item(QStringLiteral("recordWorkspaceBackButton"))->isEnabled());
    QVERIFY(scene.editor()->property("busy").toBool());
    QCOMPARE(scene.note()->property("text").toString(), text);
    QCOMPARE(scene.app->recovery()->draft(run).value(QStringLiteral("text")).toString(), text);
    QCOMPARE(scene.backend.commands.count(QStringLiteral("SetRunReflection")), 1);
    QTRY_COMPARE(success.count(), 1);
    QTRY_VERIFY(!scene.editor()->property("busy").toBool());
    QTRY_VERIFY(!scene.editor()->property("editingActive").toBool() && !scene.host()->isVisible());
    QVERIFY(scene.app->recovery()->draft(run).isEmpty());
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::automaticNativeWindowDoesNotStealForeground()
{
#ifdef Q_OS_WIN
    if (QGuiApplication::platformName() != QLatin1String("windows"))
        QSKIP("Native foreground HWND verification requires the Windows platform.");
    ShippingWindowScene scene;
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
        ShippingWindowScene first(shared.path());
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
    ShippingWindowScene restored(shared.path());
    restored.backend.resetRuns({QJsonObject::fromVariantMap(run)});
    QVERIFY2(restored.create(), qPrintable(restored.errors));
    QTRY_COMPARE(restored.app->recovery()->count(), 1);
    QTRY_VERIFY(!restored.app->recovery()->busy());
    QVERIFY(!restored.host()->isVisible());
    QVERIFY(restored.click(QStringLiteral("openPendingWork_") + run.value(QStringLiteral("run_id")).toString()));
    QTRY_VERIFY(restored.workspaceActive());
    QCOMPARE(restored.note()->property("text").toString(), note);
    QVERIFY(restored.backend.freshReads > 0);
    QVERIFY(restored.click(QStringLiteral("saveReflectionButton")));
    QTRY_VERIFY(!restored.editor()->property("editingActive").toBool() && !restored.host()->isVisible());
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
    ShippingWindowScene scene;
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
        QTRY_VERIFY(scene.workspaceActive());
        QVERIFY(!scene.item(QStringLiteral("popupNextButton")));
        const QString firstShown = scene.editor()->property("runId").toString();
        QVERIFY(scene.click(QStringLiteral("deferReflectionButton")));
        QTRY_VERIFY(!scene.editor()->property("editingActive").toBool());
        const QString otherId = firstShown == run.value(QStringLiteral("run_id")).toString()
            ? second.value(QStringLiteral("run_id")).toString() : run.value(QStringLiteral("run_id")).toString();
        scene.app->recovery()->requestOpen(otherId);
        QTRY_VERIFY(scene.workspaceActive());
        QCOMPARE(scene.editor()->property("runId").toString(), otherId);
        QVERIFY(scene.click(QStringLiteral("deferReflectionButton")));
        QTRY_VERIFY(!scene.editor()->property("editingActive").toBool());
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
    ShippingWindowScene scene;
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
    QTRY_VERIFY(scene.workspaceActive());
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
    QTRY_VERIFY(!scene.editor()->property("editingActive").toBool() && !scene.host()->isVisible());
    QTRY_VERIFY(scene.app->recovery()->draft(run).isEmpty());
    QCOMPARE(scene.backend.commands.count(QStringLiteral("SetRunReflection")), 2);
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::journalFailureKeepsEditorOpenUntilRetry()
{
    ShippingWindowScene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    const auto run = finishedRun(true);
    scene.publish(run);
    QTRY_VERIFY(scene.host()->isVisible() && scene.editor()->property("opened").toBool());
    const QString journal = scene.journalPath();
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
    QTRY_VERIFY(scene.workspaceActive());
    QCOMPARE(scene.note()->property("text").toString(), QStringLiteral("retain input while disk is busy"));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::oldSaveReplyCannotCloseReopenedEditorAfterScopeChanges()
{
    ShippingWindowScene scene;
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
    const QString original = scene.databasePath();
    scene.app->recovery()->setDatabasePath(original + QStringLiteral(".other"));
    QTRY_VERIFY(!scene.host()->isVisible());
    scene.app->recovery()->setDatabasePath(original);
    scene.app->recovery()->requestOpen(run.value(QStringLiteral("run_id")).toString());
    QTRY_VERIFY(scene.workspaceActive());
    QVERIFY(scene.enterNote(QStringLiteral("new editor input after reconnect")));
    QTRY_COMPARE(failure.count(), 1);
    QCOMPARE(success.count(), 0);
    QVERIFY(scene.workspaceActive());
    QCOMPARE(scene.note()->property("text").toString(), QStringLiteral("new editor input after reconnect"));
    QTRY_COMPARE(scene.app->recovery()->draft(run).value(QStringLiteral("text")).toString(),
                 QStringLiteral("new editor input after reconnect"));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::sameRunWithoutDraftUsesLatestCommittedNote()
{
    ShippingWindowScene scene;
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
    QTRY_VERIFY(scene.workspaceActive());
    QCOMPARE(scene.note()->property("text").toString(), QStringLiteral("latest note from another editor"));
    QCOMPARE(scene.editor()->property("mood").toString(), QStringLiteral("bad"));
    QVERIFY(!scene.editor()->property("conflictUnresolved").toBool());
    QCOMPARE(scene.backend.commands.count(QStringLiteral("SetRunReflection")), 0);
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::answerStaysOpenForTheNote()
{
    ShippingWindowScene scene;
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
    ShippingWindowScene scene;
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

void RecoveryUiTests::aJobOnlyFinishLeavesNothingPending()
{
    // Audit 2026-11-03 L2-2: a cleared run without a job, the job picked in
    // the note popup and 完成 pressed without a note. The job was saved, but the
    // draft holding it and the 待补笔记 reminder stayed, so the run was still
    // listed as pending with a draft.
    ShippingWindowScene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    scene.mainWindow()->hide();
    auto run = finishedRun(true);
    run.insert(QStringLiteral("job_id"), 0);
    run.insert(QStringLiteral("job_name"), QString::fromUtf8("未知"));
    scene.publish(run);
    QTRY_VERIFY(scene.host()->isVisible() && scene.editor()->property("opened").toBool());
    QVERIFY(!scene.editor()->property("askingResult").toBool());
    QVERIFY(scene.editor()->property("needsJob").toBool());
    scene.editor()->setProperty("jobIndex", 1);
    QVERIFY(scene.editor()->property("selectedJobId").toInt() > 0);
    // The 250 ms draft timer has written the job down.
    QTRY_VERIFY(!scene.app->recovery()->draft(run).isEmpty());
    QVERIFY(scene.click(QStringLiteral("saveReflectionButton")));
    QTRY_VERIFY(!scene.host()->isVisible());
    QCOMPARE(scene.backend.commands.count(QStringLiteral("CorrectRun")), 1);
    QCOMPARE(scene.backend.commands.count(QStringLiteral("SetRunReflection")), 0);
    QTRY_VERIFY(scene.app->recovery()->draft(run).isEmpty());
    QTRY_COMPARE(scene.app->recovery()->count(), 0);
    QTest::qWait(400); // No late draft brings the entry back.
    QCOMPARE(scene.app->recovery()->count(), 0);
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::aPageRequestOvertakenByAnAnswerLeavesTheNoteEditable()
{
    // Audit 2026-11-03 L2-4: 「下一条」 and then 「通关 +1」 before the page read
    // came back. The busy popup dropped the page, but kept waiting for it: the
    // note field stayed read-only and the image controls locked.
    ShippingWindowScene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    scene.mainWindow()->hide();
    scene.settings.setPromptPresentation(QStringLiteral("inbox"));
    const auto first = finishedRun(false, 91);
    const auto second = finishedRun(false, 92);
    scene.publish(first);
    scene.publish(second);
    QTRY_COMPARE(scene.app->recovery()->count(), 2);
    scene.settings.setPromptPresentation(QStringLiteral("window"));
    // Exercise the retained compact editor API directly; explicit recovery uses a page.
    QVERIFY(QMetaObject::invokeMethod(scene.root.get(), "prepareReflection",
        Q_ARG(QVariant, QVariant(true)), Q_ARG(QVariant, QVariant(false))));
    QVERIFY(QMetaObject::invokeMethod(scene.editor(), "openForResult", Q_ARG(QVariant, QVariant(first))));
    QTRY_VERIFY(scene.host()->isVisible() && scene.editor()->property("opened").toBool());
    QTRY_VERIFY(scene.editor()->property("askingResult").toBool());
    QTRY_VERIFY(scene.item(QStringLiteral("popupNextButton")) || scene.item(QStringLiteral("popupPreviousButton")));
    const QString shown = scene.editor()->property("runId").toString();
    const int direction = scene.editor()->property("queueIndex").toInt() == 0 ? 1 : -1;
    // Both before the event loop runs again: the page read is still out when
    // the answer makes the popup busy.
    QVERIFY(QMetaObject::invokeMethod(scene.editor(), "pageQueue", Q_ARG(QVariant, QVariant(direction))));
    QVERIFY(QMetaObject::invokeMethod(scene.editor(), "resolveWith",
                                      Q_ARG(QVariant, QVariant(QStringLiteral("COMPLETED"))),
                                      Q_ARG(QVariant, QVariant(QStringLiteral("用户确认通关")))));
    QTRY_VERIFY(scene.editor()->property("answered").toBool());
    QTest::qWait(300); // The page read has been answered meanwhile.
    QCOMPARE(scene.editor()->property("runId").toString(), shown);
    QCOMPARE(scene.editor()->property("pendingSwitchId").toString(), QString());
    QVERIFY(!scene.note()->property("readOnly").toBool());
    QVERIFY(scene.enterNote(QStringLiteral("still writable")));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::theGrowingPopupStaysOnTheScreen()
{
    // Audit 2026-11-03 K1-6: the popup was placed once. Docked at the bottom
    // of the screen, the note field growing while writing pushed the field and
    // 完成 below the screen edge.
    ShippingWindowScene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    scene.mainWindow()->hide();
    scene.publish(finishedRun());
    QTRY_VERIFY(scene.host()->isVisible() && scene.editor()->property("opened").toBool());
    QScreen *screen = scene.host()->screen();
    QVERIFY(screen);
    const QRect bounds = screen->geometry();
    // The window follows the editor's height; let the first layout settle.
    QTRY_COMPARE(scene.host()->height(), qRound(scene.editor()->property("implicitHeight").toReal()));
    QTest::qWait(200);
    const int before = scene.host()->height();
    scene.host()->setY(bounds.y() + bounds.height() - before - 8);
    QTRY_COMPARE(scene.host()->y(), bounds.y() + bounds.height() - before - 8);
    QVERIFY(scene.enterNote(QStringLiteral("一\n二\n三\n四\n五\n六")));
    QTRY_VERIFY_WITH_TIMEOUT(scene.host()->height() > before, 10000);
    QTRY_VERIFY2(scene.host()->y() + scene.host()->height() <= bounds.y() + bounds.height() - 8,
                 qPrintable(QStringLiteral("%1 + %2 > %3").arg(scene.host()->y()).arg(scene.host()->height())
                                .arg(bounds.y() + bounds.height() - 8)));
    QVERIFY(scene.host()->y() >= bounds.y() + 8);
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::aRefusedNoteAfterTheAnswerStillCollapses()
{
    // 收起 after an answer saves like 完成; when that save is refused, the next
    // 收起 closes the ordinary way and the draft is kept.
    ShippingWindowScene scene;
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
    ShippingWindowScene scene;
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
    QTRY_VERIFY(scene.workspaceActive());
    QVERIFY(scene.editor()->property("unavailable").toBool());
    QCOMPARE(scene.note()->property("text").toString(), QStringLiteral("copy me first"));
    QVERIFY(scene.click(QStringLiteral("deferReflectionButton")));
    QTRY_VERIFY(!scene.editor()->property("editingActive").toBool() && !scene.host()->isVisible());
    QTRY_COMPARE(scene.app->recovery()->count(), 1);
    // Inline recovery returns to the overview; its retained draft remains listed.
    auto *notice = scene.root->findChild<QQuickWindow *>(QStringLiteral("recoveryNoticeWindow"));
    QVERIFY(notice);
    QVERIFY(!notice->isVisible());
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
    QTRY_VERIFY(scene.workspaceActive());
    QVERIFY(scene.editor()->property("unavailable").toBool());
    QTRY_COMPARE(scene.app->recovery()->count(), 0);
    QVERIFY(scene.click(QStringLiteral("deferReflectionButton")));
    QTRY_VERIFY(!scene.editor()->property("editingActive").toBool() && !scene.host()->isVisible());
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
    ShippingWindowScene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    auto run = finishedRun(true, 91);
    run.insert(QStringLiteral("pending_review"), true); // Kept after a calibration withdrawal.
    scene.store(run);
    scene.app->navigate(1);
    scene.app->selectRun(run);
    QVERIFY(scene.app->hasSelection());
    QVERIFY(scene.openReflection(run));
    QTRY_VERIFY(scene.workspaceActive());
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
    ShippingWindowScene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    auto run = finishedRun(true, 92);
    run.insert(QStringLiteral("reflection"), QVariantMap{{QStringLiteral("text"), QStringLiteral("an old note")},
                                                         {QStringLiteral("mood"), QStringLiteral("ok")}});
    scene.store(run);
    QVERIFY(scene.openReflection(run));
    QTRY_VERIFY(scene.workspaceActive());
    QStringList texts;
    const auto collect = [&](auto &&self, QQuickItem *parent) -> void {
        const QString text = parent->property("text").toString();
        if (!text.isEmpty() && parent->isVisible()) texts.append(text);
        for (auto *child : parent->childItems()) self(self, child);
    };
    collect(collect, scene.editor()->property("inlineContent").value<QQuickItem *>());
    QVERIFY2(texts.contains(QString::fromUtf8("导随笔记")), qPrintable(texts.join(QLatin1Char('|'))));
    QVERIFY2(texts.contains(QString::fromUtf8("编辑笔记")), qPrintable(texts.join(QLatin1Char('|'))));
    QVERIFY2(!texts.contains(QString::fromUtf8("本次笔记待补录")), qPrintable(texts.join(QLatin1Char('|'))));
    QVERIFY2(!texts.join(QLatin1Char('|')).contains(QString::fromUtf8("关闭只会收起提醒")),
             qPrintable(texts.join(QLatin1Char('|'))));
    QVERIFY(scene.item(QStringLiteral("recordWorkspaceBackButton")));
    scene.mainWindow()->requestActivate();
    QVERIFY(QTest::qWaitForWindowActive(scene.mainWindow()));
    QTest::keyClick(scene.mainWindow(), Qt::Key_Escape);
    QTRY_VERIFY(!scene.editor()->property("editingActive").toBool());
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
    ShippingWindowScene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    const auto run = finishedRun(true, 74);
    scene.store(run);
    QVERIFY(scene.openReflection(run));
    QTRY_VERIFY(scene.workspaceActive());
    QVERIFY(!scene.editor()->property("standalone").toBool());
    const QString journal = scene.journalPath();
    QLockFile lock(journal + QStringLiteral(".lock"));
    QVERIFY(lock.tryLock(0));
    QVERIFY(scene.note()->setProperty("text", QStringLiteral("typed while the disk is busy")));
    scene.mainWindow()->requestActivate();
    QVERIFY(QTest::qWaitForWindowActive(scene.mainWindow()));
    QTest::keyClick(scene.mainWindow(), Qt::Key_Escape);
    QTRY_VERIFY(!scene.editor()->property("errorText").toString().isEmpty());
    QVERIFY(scene.workspaceActive());
    QCOMPARE(scene.note()->property("text").toString(), QStringLiteral("typed while the disk is busy"));
    lock.unlock();
    QTest::keyClick(scene.mainWindow(), Qt::Key_Escape);
    QTRY_VERIFY(!scene.editor()->property("editingActive").toBool());
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
    ShippingWindowScene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    auto run = finishedRun(true, 93);
    run.insert(QStringLiteral("result"), QStringLiteral("INTERRUPTED"));
    run.insert(QStringLiteral("pending_review"), true);
    const QString id = run.value(QStringLiteral("run_id")).toString();
    scene.store(run);
    scene.app->recovery()->refresh();
    QTRY_COMPARE(scene.app->recovery()->count(), 1);
    QTRY_VERIFY(!scene.app->recovery()->busy());
    QVERIFY(scene.click(QStringLiteral("openPendingWork_") + id));
    QTRY_VERIFY(scene.workspaceActive());
    QVERIFY(!scene.editor()->property("askingResult").toBool());
    QVERIFY(scene.click(QStringLiteral("reviewRecoveredRecordButton")));
    QTRY_VERIFY(!scene.editor()->property("editingActive").toBool() && !scene.host()->isVisible());
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
    ShippingWindowScene scene;
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
    ShippingWindowScene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    const auto old = finishedRun(true, 95);
    scene.store(old);
    QVERIFY(scene.openReflection(old));
    QTRY_VERIFY(scene.workspaceActive());
    scene.note()->setProperty("text", QStringLiteral("half a note"));
    scene.mainWindow()->hide();
    QTRY_VERIFY(!scene.editor()->property("editingActive").toBool());
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

void RecoveryUiTests::aNoteEditorParkedOnTheRecordPageDoesNotHoldPopupsBack()
{
    // Audit 2026-11-26 L2-2: a note editor left on the record page with 返回 held every
    // automatic popup, and 本次更新, until it was found again. It is put away like the one
    // left in a hidden window, its draft kept, and the popup opens.
    ShippingWindowScene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    const auto old = finishedRun(true, 120);
    scene.store(old);
    QVERIFY(scene.openReflection(old));
    QTRY_VERIFY(scene.workspaceActive());
    scene.note()->setProperty("text", QStringLiteral("half a parked note"));
    QVERIFY(scene.click(QStringLiteral("recordWorkspaceBackButton")));
    QTRY_VERIFY(scene.app->currentPage() != 8);
    QVERIFY(scene.editor()->property("editingActive").toBool());
    QVERIFY(!scene.root->property("recordEditorOnScreen").toBool());
    const auto run = finishedRun(true, 121);
    scene.publish(run);
    QTRY_VERIFY(scene.host()->isVisible() && scene.editor()->property("opened").toBool());
    QCOMPARE(scene.editor()->property("runId").toString(), run.value(QStringLiteral("run_id")).toString());
    QCOMPARE(scene.app->recovery()->draft(old).value(QStringLiteral("text")).toString(),
             QStringLiteral("half a parked note"));
    QVERIFY(scene.click(QStringLiteral("deferReflectionButton")));
    QTRY_VERIFY(!scene.host()->isVisible());
    QCOMPARE(scene.backend.commands.count(QStringLiteral("SetRunReflection")), 0);
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::theInboxOpensBesideAParkedForm()
{
    // Audit 2026-11-26 K1-2: with a 新增记录 form parked by 「返回」, 待处理 showed the form
    // and never opened the inbox, which is only a list and needs no editor.
    ShippingWindowScene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    QVERIFY(QMetaObject::invokeMethod(scene.root.get(), "openCreateDialog"));
    QTRY_VERIFY(scene.inlineSession(scene.manual()));
    QVERIFY(scene.click(QStringLiteral("recordWorkspaceBackButton")));
    QTRY_VERIFY(scene.app->currentPage() != 8);
    QVERIFY(scene.manual()->property("inlineActive").toBool());
    scene.app->recovery()->requestInbox();
    QTRY_VERIFY(scene.inbox()->property("opened").toBool());
    QVERIFY(scene.app->currentPage() != 8);
    QVERIFY(scene.manual()->property("inlineActive").toBool());
    QVERIFY(QMetaObject::invokeMethod(scene.inbox(), "close"));
    QTRY_VERIFY(!scene.inbox()->property("visible").toBool());
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::aLostConnectionPutsThePopupAwayWithANotice()
{
    // Review L2-6: a Collector disconnect closed the popup with no word, and the
    // run was never offered again. It is put away like 收起: the draft is kept
    // and the 「已收起到待处理」 notice says where it went.
    ShippingWindowScene scene;
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
    ShippingWindowScene scene;
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
    ShippingWindowScene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    const auto open = finishedRun(true, 98);
    const auto other = finishedRun(true, 99);
    scene.store(open);
    scene.store(other);
    scene.app->recovery()->offer(other, QStringLiteral("reflection"));
    QVERIFY(scene.openReflection(open));
    QTRY_VERIFY(scene.workspaceActive());
    scene.mainWindow()->showMinimized();
    if (!QTest::qWaitFor([&] { return scene.mainWindow()->visibility() == QWindow::Minimized; }))
        QSKIP("This platform plugin does not report a minimised window.");
    scene.app->recovery()->requestOpen(other.value(QStringLiteral("run_id")).toString());
    QTRY_VERIFY(scene.mainWindow()->visibility() != QWindow::Minimized);
    QCOMPARE(scene.editor()->property("runId").toString(), open.value(QStringLiteral("run_id")).toString());
    QVERIFY(scene.workspaceActive());

    // Audit 2026-11-03 K1-5: 总览「补录笔记」, 心得页「编辑心得」 and 「写心得」
    // (openReflection) still did nothing at all in that case.
    scene.mainWindow()->showMinimized();
    QTRY_VERIFY(scene.mainWindow()->visibility() == QWindow::Minimized);
    QVERIFY(scene.openReflection(other));
    QTRY_VERIFY(scene.mainWindow()->visibility() != QWindow::Minimized);
    QCOMPARE(scene.editor()->property("runId").toString(), open.value(QStringLiteral("run_id")).toString());
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::noNpcapWarningWhileNothingIsKnownAboutNpcap()
{
    // Review K2-1 (the 总览 banner and the side panel's Npcap row, reported by
    // Y5b): without a capture status - no pipe - nothing is known about Npcap,
    // and 「未检测到 Npcap」 / 「未安装」 would be a false claim.
    ShippingWindowScene scene;
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

bool openNoteThenQueueQuestion(ShippingWindowScene &scene, const QueuedQuestion &runs, QSignalSpy &offers, bool compact)
{
    scene.settings.setPromptPresentation(QStringLiteral("inbox"));
    scene.publish(runs.note);
    if (!QTest::qWaitFor([&] { return scene.app->recovery()->count() == 1; })) return false;
    scene.settings.setPromptPresentation(QStringLiteral("window"));
    if (compact) {
        if (!QMetaObject::invokeMethod(scene.root.get(), "prepareReflection",
                Q_ARG(QVariant, QVariant(true)), Q_ARG(QVariant, QVariant(false)))) return false;
        if (!QMetaObject::invokeMethod(scene.editor(), "openForRun", Q_ARG(QVariant, QVariant(runs.note)),
                Q_ARG(QVariant, QVariant(QString())))) return false;
        if (!QTest::qWaitFor([&] { return scene.host()->isVisible() && scene.editor()->property("opened").toBool(); }))
            return false;
    } else {
        scene.app->recovery()->requestOpen(runs.note.value(QStringLiteral("run_id")).toString());
        if (!QTest::qWaitFor([&] { return scene.workspaceActive(); })) return false;
    }
    scene.publish(runs.question);
    if (!QTest::qWaitFor([&] { return offers.count() == 1 && scene.app->recovery()->count() == 2; })) return false;
    return scene.editor()->property("runId").toString() == runs.note.value(QStringLiteral("run_id")).toString();
}
} // namespace

void RecoveryUiTests::aQuestionAnsweredInThePagerIsNotAskedAgain()
{
    // Review L2-2: paging to a queued question and answering it there left it in
    // the automatic queue, so closing the popup asked 「通关了吗？」 again.
    ShippingWindowScene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    QSignalSpy offers(scene.app.get(), &mr::AppController::resultConfirmationRequested);
    const QueuedQuestion runs;
    QVERIFY(openNoteThenQueueQuestion(scene, runs, offers, true));
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
    ShippingWindowScene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    QSignalSpy offers(scene.app.get(), &mr::AppController::resultConfirmationRequested);
    QSignalSpy reviewed(scene.app.get(), &mr::AppController::mutationSucceeded);
    const QueuedQuestion runs;
    QVERIFY(openNoteThenQueueQuestion(scene, runs, offers, false));
    scene.app->resolveRunResult(runs.question.value(QStringLiteral("run_id")).toString(), 1,
                                QStringLiteral("COMPLETED"), QStringLiteral("answered on the history page"));
    QTRY_COMPARE(reviewed.count(), 1);
    QVERIFY(scene.click(QStringLiteral("deferReflectionButton")));
    QTRY_VERIFY(!scene.editor()->property("editingActive").toBool() && !scene.host()->isVisible());
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
    ShippingWindowScene scene;
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
    QVERIFY2(texts.contains(QStringLiteral("10")), qPrintable(texts.join(QLatin1Char('|'))));
    QVERIFY2(texts.contains(QStringLiteral("11")), qPrintable(texts.join(QLatin1Char('|'))));
    // The two counts are one face, size and weight; only the colour differs.
    auto *from = scene.item(QStringLiteral("popupProgressFrom"));
    auto *to = scene.item(QStringLiteral("popupProgressTo"));
    QVERIFY(from && to);
    QCOMPARE(from->property("font").value<QFont>(), to->property("font").value<QFont>());
    QVERIFY(from->property("color") != to->property("color"));
    QVERIFY(scene.click(QStringLiteral("deferReflectionButton")));
    QTRY_VERIFY(!scene.host()->isVisible());
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::anAnsweredClearClaimsTheCountOnlyWhenTheRunContributes_data()
{
    QTest::addColumn<bool>("contributes");
    QTest::addColumn<bool>("unassigned");
    QTest::newRow("contributes") << true << false;
    QTest::newRow("excluded from the goal") << false << false;
    // Audit 2026-11-20 L1-2: a record assigned to no character counts in no one's progress.
    QTest::newRow("assigned to no character") << true << true;
}

void RecoveryUiTests::anAnsweredClearClaimsTheCountOnlyWhenTheRunContributes()
{
    // Review L2-1 second path: answering 通关 must not print 「已计入导随次数」 and
    // "N → N+1" for a run the Collector does not count towards the goal.
    QFETCH(bool, contributes);
    QFETCH(bool, unassigned);
    const bool counted = contributes && !unassigned;
    ShippingWindowScene scene;
    scene.backend.setAchievement(2000, 10);
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    QTRY_COMPARE(scene.app->dashboard().value(QStringLiteral("achievement_progress")).toInt(), 10);
    scene.mainWindow()->hide();
    auto run = finishedRun();
    run.insert(QStringLiteral("contributes_to_goal"), contributes);
    if (unassigned) run.insert(QStringLiteral("profile_id"), QVariant::fromValue(nullptr));
    scene.publish(run);
    QTRY_VERIFY(scene.host()->isVisible() && scene.editor()->property("opened").toBool());
    auto *confirm = scene.item(QStringLiteral("confirmCompletedButton"));
    QVERIFY(confirm);
    QCOMPARE(confirm->property("text").toString(),
             unassigned ? QString::fromUtf8("通关") : QString::fromUtf8("通关 +1"));
    QVERIFY(scene.click(QStringLiteral("confirmCompletedButton")));
    QTRY_VERIFY(scene.editor()->property("answered").toBool());
    if (unassigned) {
        auto *resultText = scene.item(QStringLiteral("popupResultText"));
        QVERIFY(resultText);
        QTRY_COMPARE(resultText->property("text").toString(),
                     QString::fromUtf8("已记为通关；还没有分配角色，暂不计入进度"));
    }
    if (counted) {
        QTRY_COMPARE(scene.editor()->property("progressTo").toInt(), 11);
        QCOMPARE(scene.editor()->property("progressFrom").toInt(), 10);
    } else {
        QTest::qWait(300);
        QTRY_COMPARE(scene.app->dashboard().value(QStringLiteral("achievement_progress")).toInt(), 10);
    }
    const QStringList texts = scene.hostTexts();
    QCOMPARE(texts.contains(QString::fromUtf8("已计入导随次数")), counted);
    QCOMPARE(texts.contains(QStringLiteral("10")), counted);
    QCOMPARE(texts.contains(QStringLiteral("11")), counted);
    QVERIFY(scene.click(QStringLiteral("saveReflectionButton")));
    QTRY_VERIFY(!scene.host()->isVisible());
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::overviewUsesUnifiedCountAndSharedActions()
{
    ShippingWindowScene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    scene.settings.setPromptPresentation(QStringLiteral("inbox"));
    const auto result = finishedRun(false, 31);
    const auto note = finishedRun(true, 32);
    const auto draft = finishedRun(true, 33);
    scene.publish(result);
    scene.publish(note);
    scene.store(draft);
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
    QTRY_VERIFY(scene.workspaceActive());
    QCOMPARE(scene.editor()->property("runId").toString(), result.value(QStringLiteral("run_id")).toString());
    QCOMPARE(scene.note()->property("text").toString(), QStringLiteral("result note draft"));
    QCOMPARE(scene.backend.commands.count(QStringLiteral("CorrectRun")), 0);
    QCOMPARE(scene.backend.commands.count(QStringLiteral("SetRunReflection")), 0);
    QVERIFY(scene.click(QStringLiteral("deferReflectionButton")));
    QTRY_VERIFY(!scene.editor()->property("editingActive").toBool() && !scene.host()->isVisible());

    const QString firstId = scene.app->recovery()->items().first().toMap().value(QStringLiteral("runId")).toString();
    QVERIFY(scene.click(QStringLiteral("openPendingWork_") + firstId));
    QTRY_VERIFY(scene.workspaceActive());
    QCOMPARE(scene.editor()->property("runId").toString(), firstId);
    QCOMPARE(scene.backend.commands.count(QStringLiteral("CorrectRun")), 0);
    QCOMPARE(scene.backend.commands.count(QStringLiteral("SetRunReflection")), 0);
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void RecoveryUiTests::overviewDraftOnlyDisappearsAfterSave()
{
    ShippingWindowScene scene;
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
    QTRY_VERIFY(scene.workspaceActive());
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
    ShippingWindowScene scene;
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
    ShippingWindowScene scene;
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
    ShippingWindowScene scene;
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
