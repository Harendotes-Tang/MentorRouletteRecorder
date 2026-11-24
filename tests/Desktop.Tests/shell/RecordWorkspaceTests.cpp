#include "DesktopTestRunner.h"
#include "QmlComponentTestSupport.h"
#include "ShippingWindowFixture.h"
#include "TestCollectorGuard.h"
#include "AppController.h"
#include "BundledFonts.h"
#include "RecoveryController.h"

#include <QApplication>
#include <QClipboard>
#include <QDesktopServices>
#include <QFont>
#include <QQuickItem>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QScopeGuard>
#include <QSignalSpy>
#include <QStandardPaths>
#include <QTest>
#include <qpa/qwindowsysteminterface.h>

namespace {

/// The shipping window as this suite starts it: its desktop.ini and data in the system temp
/// folder, the classic style, and the system's theme.
struct WorkspaceScene : ShippingWindowScene
{
    WorkspaceScene() : ShippingWindowScene({}, Folder::SystemTemp) {}
    bool create() { return ShippingWindowScene::create(std::nullopt, QStringLiteral("classic")); }
};

/// Clicks the 「新增记录」 button of the page on screen once it can take the click.
bool clickNewRecord(const ShippingWindowScene &scene)
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
    QQuickWindow *window = scene.mainWindow();
    if (!QTest::qWaitFor([&] {
            button = find(find, window->contentItem());
            return button && button->isEnabled() && button->width() > 0 && button->height() > 0;
        })) return false;
    const QPoint center = button->mapToScene(QPointF(button->width() / 2, button->height() / 2)).toPoint();
    if (!QRect(QPoint(), window->size()).contains(center)) return false;
    QTest::mouseClick(window, Qt::LeftButton, Qt::NoModifier, center);
    return true;
}

class FeedbackUrlReceiver final : public QObject
{
    Q_OBJECT
public:
    QList<QUrl> urls;
    Q_SLOT void open(const QUrl &url) { urls.append(url); }
};

class RecordWorkspaceTests final : public QObject
{
    Q_OBJECT
private Q_SLOTS:
    void initTestCase() { QVERIFY(mrtest::registerShippingQml()); }

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
        QVERIFY(clickNewRecord(scene));
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

    void feedbackOpensTheSelectedChannelWithoutLeavingTheWorkspace_data()
    {
        QTest::addColumn<QString>("actionName");
        QTest::addColumn<QUrl>("expectedUrl");
        QTest::newRow("github") << QStringLiteral("feedbackGithubButton") << QUrl(QStringLiteral(
            "https://github.com/Harendotes-Tang/MentorRouletteRecorder/issues/new/choose"));
        QTest::newRow("email") << QStringLiteral("feedbackEmailButton") << QUrl(QStringLiteral(
            "mailto:mentorrecorder@gmail.com?subject=导随记录器意见反馈"));
    }

    void feedbackOpensTheSelectedChannelWithoutLeavingTheWorkspace()
    {
        QFETCH(QString, actionName);
        QFETCH(QUrl, expectedUrl);
        FeedbackUrlReceiver externalApp;
        QDesktopServices::setUrlHandler(QStringLiteral("https"), &externalApp, "open");
        QDesktopServices::setUrlHandler(QStringLiteral("mailto"), &externalApp, "open");
        const auto restore = qScopeGuard([] {
            QDesktopServices::unsetUrlHandler(QStringLiteral("https"));
            QDesktopServices::unsetUrlHandler(QStringLiteral("mailto"));
        });

        WorkspaceScene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QVERIFY(clickNewRecord(scene));
        QTRY_VERIFY(scene.inlineSession(scene.manual()));
        const QString draft = QStringLiteral("填写到一半的心得");
        QVERIFY(scene.manual()->setProperty("reflectionText", draft));
        QVERIFY(externalApp.urls.isEmpty());

        QVERIFY(scene.click(QStringLiteral("feedbackEntry")));
        const auto *dialog = scene.root->findChild<QObject *>(QStringLiteral("feedbackDialog"));
        QVERIFY(dialog);
        QTRY_VERIFY(dialog->property("opened").toBool());
        QVERIFY(externalApp.urls.isEmpty());

        if (expectedUrl.scheme() == QStringLiteral("mailto")) {
            QVERIFY(scene.click(QStringLiteral("feedbackCopyEmailButton")));
            QCOMPARE(QApplication::clipboard()->text(), expectedUrl.path());
            QVERIFY(externalApp.urls.isEmpty());
        }

        QVERIFY(scene.click(actionName));
        QCOMPARE(externalApp.urls, QList<QUrl>{expectedUrl});
        QTRY_VERIFY(!dialog->property("visible").toBool());
        QVERIFY(scene.inlineSession(scene.manual()));
        QCOMPARE(scene.manual()->property("reflectionText").toString(), draft);
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
        scene.backend.refuseNextReflection = true;
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
        QTRY_VERIFY(scene.inlineSession(scene.editor()));
        auto *note = scene.editor()->property("noteEditor").value<QObject *>();
        QVERIFY(note);
        const QString text = QStringLiteral("切换页面后仍保留的心得草稿");
        note->setProperty("text", text);
        scene.app->navigate(1);
        QTRY_VERIFY(!scene.workspace()->isVisible());
        QTRY_VERIFY(!scene.editor()->property("editorOnScreen").toBool());
        scene.mainWindow()->requestActivate();
        QVERIFY(QTest::qWaitForWindowActive(scene.mainWindow()));
        QTest::keyClick(scene.mainWindow(), Qt::Key_Escape);
        QTest::qWait(100);
        QCOMPARE(scene.app->currentPage(), 1);
        QVERIFY(scene.editor()->property("inlineActive").toBool());
        QVERIFY(!scene.editor()->property("visible").toBool());
        QCOMPARE(note->property("text").toString(), text);
        QCOMPARE(scene.backend.commands.count(QStringLiteral("SetRunReflection")), 0);
        scene.app->navigate(8);
        QTRY_VERIFY(scene.inlineSession(scene.editor()));
        QCOMPARE(note->property("text").toString(), text);
        QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
    }

    void closingManualEntryReoffersTheAutomaticPromptItBlocked()
    {
        WorkspaceScene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QVERIFY(QMetaObject::invokeMethod(scene.root.get(), "openCreateDialog"));
        QTRY_VERIFY(scene.inlineSession(scene.manual()));
        // Only a form in the active window is on show (audit 2026-11-20 K1-1).
        scene.mainWindow()->requestActivate();
        QVERIFY(QTest::qWaitForWindowActive(scene.mainWindow()));
        QSignalSpy offers(scene.app.get(), &mr::AppController::resultConfirmationRequested);
        scene.publish(finishedRun(false));
        QTRY_COMPARE(offers.count(), 1);
        QTRY_COMPARE(scene.app->recovery()->count(), 1);
        QVERIFY(!scene.editor()->property("editingActive").toBool());
        QVERIFY(!scene.host()->isVisible());
        QVERIFY(QMetaObject::invokeMethod(scene.manual(), "closeEditor"));
        QTRY_VERIFY(scene.host()->isVisible() && scene.editor()->property("opened").toBool());
        QVERIFY(scene.editor()->property("standalone").toBool());
        QVERIFY(scene.editor()->property("automaticOpen").toBool());
        QVERIFY(!scene.editor()->property("inlineActive").toBool());
        QVERIFY(scene.editor()->property("askingResult").toBool());
        QCOMPARE(scene.app->currentPage(), 0);
        QCOMPARE(scene.backend.commands.count(QStringLiteral("CreateManualRun")), 0);
        QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
    }

    // Audit 2026-11-10 K1-1: a form left behind on another page is not being looked at, so the
    // popup after a duty opens all the same and the draft stays where it was.
    void aManualEntryLeftOnAnotherPageDoesNotHoldTheAutomaticPromptBack()
    {
        WorkspaceScene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QVERIFY(QMetaObject::invokeMethod(scene.root.get(), "openCreateDialog"));
        QTRY_VERIFY(scene.inlineSession(scene.manual()));
        scene.app->navigate(1);
        QTRY_COMPARE(scene.app->currentPage(), 1);
        scene.publish(finishedRun(false));
        QTRY_VERIFY(scene.host()->isVisible() && scene.editor()->property("opened").toBool());
        QVERIFY(scene.editor()->property("standalone").toBool());
        QVERIFY(scene.editor()->property("askingResult").toBool());
        QVERIFY(scene.manual()->property("inlineActive").toBool());
        QCOMPARE(scene.app->currentPage(), 1);
        // Finishing the parked form does not answer the popup that is still open.
        QVERIFY(QMetaObject::invokeMethod(scene.manual(), "closeEditor"));
        QTRY_VERIFY(!scene.manual()->property("inlineActive").toBool());
        QVERIFY(scene.host()->isVisible() && scene.editor()->property("opened").toBool());
        QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
    }

    // Audit 2026-11-20 K1-1 (b): a popup held back while the form was looked at is offered
    // again as soon as the form is parked, not only once it is finished.
    void aPopupHeldByTheFormOpensOnceTheFormIsParked()
    {
        WorkspaceScene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QVERIFY(QMetaObject::invokeMethod(scene.root.get(), "openCreateDialog"));
        QTRY_VERIFY(scene.inlineSession(scene.manual()));
        scene.mainWindow()->requestActivate();
        QVERIFY(QTest::qWaitForWindowActive(scene.mainWindow()));
        QSignalSpy offers(scene.app.get(), &mr::AppController::resultConfirmationRequested);
        scene.publish(finishedRun(false));
        QTRY_COMPARE(offers.count(), 1);
        QTRY_COMPARE(scene.app->recovery()->count(), 1);
        QTest::qWait(100);
        QVERIFY(!scene.host()->isVisible());
        QVERIFY(!scene.editor()->property("editingActive").toBool());
        scene.app->navigate(1);
        QTRY_VERIFY(scene.host()->isVisible() && scene.editor()->property("opened").toBool());
        QVERIFY(scene.editor()->property("standalone").toBool());
        QVERIFY(scene.editor()->property("askingResult").toBool());
        QVERIFY(scene.manual()->property("inlineActive").toBool());
        QCOMPARE(scene.backend.commands.count(QStringLiteral("CreateManualRun")), 0);
        QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
    }

    // Re-review 2026-11-20 R4-1: 添加图片 opens a file chooser, a window of this application
    // that takes the focus from the main window while the player is still in the form. The
    // popup stays held back.
    void aFileChooserOpenedFromTheFormKeepsThePopupHeld()
    {
        WorkspaceScene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QVERIFY(QMetaObject::invokeMethod(scene.root.get(), "openCreateDialog"));
        QTRY_VERIFY(scene.inlineSession(scene.manual()));
        scene.mainWindow()->requestActivate();
        QVERIFY(QTest::qWaitForWindowActive(scene.mainWindow()));
        QWindow chooser;
        chooser.resize(320, 240);
        chooser.show();
        chooser.requestActivate();
        QVERIFY(QTest::qWaitForWindowActive(&chooser));
        QTRY_VERIFY(!scene.mainWindow()->isActive());
        QCOMPARE(QGuiApplication::applicationState(), Qt::ApplicationActive);
        QSignalSpy offers(scene.app.get(), &mr::AppController::resultConfirmationRequested);
        scene.publish(finishedRun(false));
        QTRY_COMPARE(offers.count(), 1);
        QTest::qWait(100);
        QVERIFY(!scene.host()->isVisible());
        QVERIFY(!scene.editor()->property("editingActive").toBool());
        QVERIFY(scene.root->property("popupHeldByManualForm").toBool());
        QVERIFY(scene.manual()->property("inlineActive").toBool());
        QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
    }

    // Audit 2026-11-20 K1-1 (a): a main window left behind the game is not being looked at,
    // so the form on its page does not hold the popup back.
    void aFormInAnInactiveWindowDoesNotHoldThePopupBack()
    {
        WorkspaceScene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QVERIFY(QMetaObject::invokeMethod(scene.root.get(), "openCreateDialog"));
        QTRY_VERIFY(scene.inlineSession(scene.manual()));
        // The game is another process: this application as a whole stops being active. A window
        // of this process would keep it active, so the platform's report is delivered directly.
        QWindowSystemInterface::handleApplicationStateChanged<QWindowSystemInterface::SynchronousDelivery>(
            Qt::ApplicationInactive);
        const auto reactivate = qScopeGuard([] {
            QWindowSystemInterface::handleApplicationStateChanged<QWindowSystemInterface::SynchronousDelivery>(
                Qt::ApplicationActive);
        });
        QCOMPARE(QGuiApplication::applicationState(), Qt::ApplicationInactive);
        QVERIFY(scene.mainWindow()->isVisible());
        scene.publish(finishedRun(false));
        QTRY_VERIFY(scene.host()->isVisible() && scene.editor()->property("opened").toBool());
        QVERIFY(scene.editor()->property("askingResult").toBool());
        QVERIFY(scene.manual()->property("inlineActive").toBool());
        QCOMPARE(scene.app->currentPage(), 8);
        QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
    }

    // Audit 2026-11-10 L2-1: release notes waiting for the window do not open over a form
    // being written on the record page; they open once it is closed.
    void releaseNotesWaitForTheInlineEditor()
    {
        WorkspaceScene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QVERIFY(QMetaObject::invokeMethod(scene.root.get(), "openCreateDialog"));
        QTRY_VERIFY(scene.inlineSession(scene.manual()));
        auto *notes = scene.root->findChild<QObject *>(QStringLiteral("releaseNotesDialog"));
        QVERIFY(notes);
        QVERIFY(scene.root->setProperty("releaseNotesWaiting", true));
        QTest::qWait(600);
        QVERIFY(!notes->property("visible").toBool());
        QVERIFY(scene.root->property("releaseNotesWaiting").toBool());
        QVERIFY(QMetaObject::invokeMethod(scene.manual(), "closeEditor"));
        QTRY_VERIFY(notes->property("visible").toBool());
        QVERIFY(!scene.root->property("releaseNotesWaiting").toBool());
        QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
    }

    // Audit 2026-11-10 K1-3: 待处理 while 手动修正 is open as a dialog brings the main window
    // forward; the popup window the editor was last parented to stays hidden.
    void inboxDuringACorrectionDialogDoesNotShowTheEmptyPopupWindow()
    {
        WorkspaceScene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        const auto run = finishedRun(true);
        scene.backend.resetRuns({QJsonObject::fromVariantMap(run)});
        // The last answered popup leaves the editor parented to the popup window.
        QVERIFY(scene.editor()->setProperty(
            "parent", QVariant::fromValue<QQuickItem *>(scene.host()->contentItem())));
        QVERIFY(scene.manual()->setProperty("inlinePresentation", false));
        QVERIFY(QMetaObject::invokeMethod(scene.manual(), "openForRun", Q_ARG(QVariant, QVariant(run))));
        QTRY_VERIFY(scene.manual()->property("visible").toBool());
        QVERIFY(scene.root->property("recordEditorOccupied").toBool());
        scene.app->recovery()->requestInbox();
        QTest::qWait(100);
        QVERIFY(!scene.host()->isVisible());
        QVERIFY(scene.mainWindow()->isVisible());
        QTRY_VERIFY(scene.mainWindow()->isActive());
        QVERIFY(scene.manual()->property("visible").toBool());
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
