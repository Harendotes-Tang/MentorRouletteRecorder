// ReflectionDialog's 本次导随结果 half, driven the way the controller drives it.
//
// The controller records a finished run as "asked" only once the dialog reports
// App.resultConfirmationShown(runId), and re-offers whatever the dialog could
// not show once App.resultConfirmationClosed() arrives. The same run therefore
// reaches openForResult twice, and the second call must not clear a 心得 the
// user is in the middle of typing.
//
// These tests open for a run, type, let the same run arrive again and assert the
// note survives; a different run must wait until the draft closes. They also pin the
// acknowledgement pair: forgetting the closed half leaves the controller
// believing a dialog is still on screen, silencing every further question for
// the rest of the session.

#include "DesktopTestRunner.h"
#include "TestCollectorGuard.h"
#include "ReflectionDialogFixture.h"
#include "AppController.h"
#include "Formatters.h"
#include "JobCatalog.h"
#include "MockBackend.h"
#include "NoteImageStore.h"

#include <QDateTime>
#include <QClipboard>
#include <QColor>
#include <QDir>
#include <QFileInfo>
#include <QGuiApplication>
#include <QImage>
#include <QJSValue>
#include <QMimeData>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQmlExpression>
#include <QQuickItem>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QSignalSpy>
#include <QStandardPaths>
#include <QTest>
#include <QTemporaryDir>
#include <QTimer>
#include <QVariantMap>

#include <memory>

namespace {

using namespace mrtest::reflectiondialog;

QVariantList asList(const QVariant &value)
{
    return value.canConvert<QJSValue>() ? value.value<QJSValue>().toVariant().toList() : value.toList();
}

const QString &kImageRunId()
{
    static const QString value = QStringLiteral("c32d8f24-e263-4245-9428-c8e66c538e21");
    return value;
}

/// The text of the first item under \a from, visible or not, that starts with
/// \a prefix; empty when there is none.
QString textStartingWith(QQuickItem *from, const QString &prefix)
{
    if (!from)
        return {};
    QString text = from->property("text").toString();
    if (text.startsWith(prefix))
        return text;
    for (QQuickItem *child : from->childItems()) {
        const QString match = textStartingWith(child, prefix);
        if (!match.isEmpty())
            return match;
    }
    return {};
}

void verifyCompactImageLimitsAndScrolling(DialogFixture &fixture, bool askingResult)
{
    fixture.dialog()->setProperty("pendingSwitchId", QStringLiteral("another-run"));
    QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "pasteNoteImage"));
    QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "removeNoteImage",
        Q_ARG(QVariant, QVariant(fixture.images.imagesFor(kImageRunId()).first().toMap()))));
    QCOMPARE(fixture.images.imagesFor(kImageRunId()).size(), 3);
    auto *strip = fixture.dialog()->findChild<QObject *>(QStringLiteral("reflectionNoteImagesCompact"));
    QVERIFY(strip);
    QVERIFY(!strip->property("editable").toBool());
    fixture.dialog()->setProperty("pendingSwitchId", QString());

    for (int index = 3; index < mr::NoteImageStore::kMaxImagesPerRun; ++index)
        QVERIFY(fixture.images.addClipboard(kImageRunId()).value(QStringLiteral("ok")).toBool());
    auto *viewport = fixture.dialog()->findChild<QQuickItem *>(QStringLiteral("reflectionImageViewportCompact"));
    QVERIFY(viewport);
    QTRY_COMPARE(strip->property("count").toInt(), mr::NoteImageStore::kMaxImagesPerRun);
    QTRY_VERIFY(strip->property("implicitHeight").toDouble() > 144);
    QTRY_COMPARE(viewport->height(), 144.0);
    viewport->ensurePolished();
    QTest::qWait(40);
    // A real reminder is bounded by its native window. Long text and
    // a full image strip must still let the user scroll to the actions.
    const QString longNote = QStringLiteral("这次记住提前提醒机制，保留全部心得文字。\n").repeated(24);
    fixture.textArea()->setProperty("text", longNote);
    fixture.textArea()->setProperty("cursorPosition", longNote.size());
    fixture.dialog()->setProperty("managedPresentation", true);
    QQmlExpression limitHeight(qmlContext(fixture.window()), fixture.window(), QStringLiteral("height = 360"));
    limitHeight.evaluate();
    QVERIFY2(!limitHeight.hasError(), qPrintable(limitHeight.error().toString()));
    QTRY_COMPARE(fixture.window()->height(), 360);
    auto *outer = fixture.dialog()->findChild<QQuickItem *>(QStringLiteral("reflectionCompactViewport"));
    QVERIFY(outer);
    auto *flickable = outer->property("contentItem").value<QQuickItem *>();
    QVERIFY(flickable);
    QTRY_VERIFY2(flickable->property("contentHeight").toReal() > flickable->height(),
        qPrintable(QStringLiteral("content=%1 viewport=%2 dialog=%3 host=%4")
            .arg(flickable->property("contentHeight").toReal()).arg(flickable->height())
            .arg(fixture.dialog()->property("height").toReal()).arg(fixture.window()->height())));
    flickable->setProperty("contentY", flickable->property("contentHeight").toReal() - flickable->height());
    QCOMPARE(fixture.note(), longNote);
    if (!askingResult) {
        auto *save = fixture.dialog()->findChild<QQuickItem *>(QStringLiteral("saveReflectionButton"));
        QVERIFY(save);
        QTRY_VERIFY(QRectF(QPointF(), fixture.window()->size()).contains(
            save->mapRectToScene(save->boundingRect())));
    }
}

} // namespace

namespace {
class ReflectionDialogBindingTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void initTestCase()
    {
        const QString qmlRoot = QString::fromUtf8(MR_DESKTOP_QML_DIR);
        QVERIFY(QDir(qmlRoot).exists());
        qmlRegisterSingletonType(QUrl::fromLocalFile(qmlRoot + QStringLiteral("/Theme.qml")),
                                 "MentorRecorder", 1, 0, "Theme");
        for (const auto &directory : {QStringLiteral("/components"), QStringLiteral("/dialogs")}) {
            for (const auto &file :
                 QDir(qmlRoot + directory).entryList({QStringLiteral("*.qml")}, QDir::Files)) {
                const QByteArray name = file.chopped(4).toUtf8();
                qmlRegisterType(QUrl::fromLocalFile(qmlRoot + directory + QLatin1Char('/') + file),
                                "MentorRecorder", 1, 0, name.constData());
            }
        }
    }

    void cleanup()
    {
        QGuiApplication::clipboard()->clear();
    }

    void editingSessionKeepsNoteSemanticsWithoutVisualControls()
    {
        QQmlEngine engine;
        QQmlComponent component(&engine);
        component.setData("import MentorRecorder 1.0\nReflectionEditSession {}", QUrl());
        std::unique_ptr<QObject> session(component.create());
        QVERIFY2(session, qPrintable(component.errorString()));
        QVERIFY(!qobject_cast<QQuickItem *>(session.get()));
        QString expressionError;
        const auto evaluate = [&](const QString &source) {
            QQmlExpression expression(qmlContext(session.get()), session.get(), source);
            const QVariant value = expression.evaluate();
            if (expression.hasError())
                expressionError = expression.error().toString();
            return value;
        };

        QCOMPARE(evaluate(QStringLiteral("noteToSave(false)")).toString(), QString());
        QCOMPARE(evaluate(QStringLiteral("noteToSave(true)")).toString(), QStringLiteral("顺利"));
        session->setProperty("baselineText", QStringLiteral("顺利"));
        session->setProperty("baselineMood", QStringLiteral("good"));
        QVERIFY(QMetaObject::invokeMethod(session.get(), "showNote",
                                         Q_ARG(QVariant, QVariant(QStringLiteral("顺利"))),
                                         Q_ARG(QVariant, QVariant(QStringLiteral("Good")))));
        QCOMPARE(session->property("text").toString(), QStringLiteral("Good"));
        QCOMPARE(evaluate(QStringLiteral("noteToSave(false)")).toString(), QStringLiteral("顺利"));
        // The label captured when the note was shown remains authoritative even
        // after the interface language changes; changing mood updates its source word.
        session->setProperty("mood", QStringLiteral("bad"));
        QCOMPARE(evaluate(QStringLiteral("noteToSave(false)")).toString(), QStringLiteral("糟心"));
        session->setProperty("text", QString());
        QCOMPARE(evaluate(QStringLiteral("noteToSave(true)")).toString(), QString());
        session->setProperty("text", QStringLiteral("  player text  "));
        QCOMPARE(evaluate(QStringLiteral("noteToSave(false)")).toString(), QStringLiteral("player text"));
        session->setProperty("submitting", true);
        session->setProperty("submittedDraftToken", QStringLiteral("draft-token"));
        session->setProperty("submittedNote", QStringLiteral("player text"));
        QVERIFY(session->property("busy").toBool());
        QCOMPARE(session->property("submittedDraftToken").toString(), QStringLiteral("draft-token"));
        QCOMPARE(session->property("submittedNote").toString(), QStringLiteral("player text"));
        QVERIFY2(expressionError.isEmpty(), qPrintable(expressionError));
    }

    void fullAndCompactViewsShareSessionTextAcrossCreationAndDestruction()
    {
        DialogFixture fixture;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));
        auto *session = fixture.dialog()->property("editSession").value<QObject *>();
        QVERIFY(session);
        QVERIFY(session->setProperty("text", QStringLiteral("written before any additional view")));
        QQmlComponent component(&fixture.engine);
        component.setData("import MentorRecorder 1.0\nReflectionEditorContent {}", QUrl());
        QQmlComponent compactComponent(&fixture.engine);
        compactComponent.setData("import MentorRecorder 1.0\nResultPopupContent {}", QUrl());
        const QVariantMap properties{{QStringLiteral("dialog"), QVariant::fromValue(fixture.dialog())}};
        std::unique_ptr<QObject> first(component.createWithInitialProperties(properties));
        std::unique_ptr<QObject> second(compactComponent.createWithInitialProperties(properties));
        QVERIFY2(first, qPrintable(component.errorString()));
        QVERIFY2(second, qPrintable(compactComponent.errorString()));
        auto *firstArea = first->property("noteEditor").value<QObject *>();
        auto *secondArea = second->findChild<QObject *>(QStringLiteral("reflectionTextArea"));
        QVERIFY(firstArea && secondArea);
        QCOMPARE(firstArea->property("text").toString(), session->property("text").toString());
        QCOMPARE(secondArea->property("text").toString(), session->property("text").toString());
        QVERIFY(firstArea->setProperty("text", QStringLiteral("typed in first view")));
        QTRY_COMPARE(session->property("text").toString(), QStringLiteral("typed in first view"));
        QTRY_COMPARE(secondArea->property("text").toString(), QStringLiteral("typed in first view"));
        first.reset();
        QVERIFY(secondArea->setProperty("text", QStringLiteral("kept after first view closes")));
        QTRY_COMPARE(session->property("text").toString(), QStringLiteral("kept after first view closes"));
        second.reset();
        QCOMPARE(session->property("text").toString(), QStringLiteral("kept after first view closes"));
        std::unique_ptr<QObject> reopened(compactComponent.createWithInitialProperties(properties));
        QVERIFY2(reopened, qPrintable(compactComponent.errorString()));
        auto *reopenedArea = reopened->findChild<QObject *>(QStringLiteral("reflectionTextArea"));
        QVERIFY(reopenedArea);
        QCOMPARE(reopenedArea->property("text").toString(), session->property("text").toString());
    }

    void longTextPasteAndKeyboardKeepCursorInViewport_data()
    {
        QTest::addColumn<bool>("standalone");
        QTest::newRow("main-window") << false;
        QTest::newRow("popup") << true;
    }

    void longTextPasteAndKeyboardKeepCursorInViewport()
    {
        QFETCH(bool, standalone);
        DialogFixture fixture;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));
        fixture.dialog()->setProperty("standalone", standalone);
        QVERIFY(fixture.openForRun(finishedRun(kImageRunId(), QStringLiteral("水晶塔"), 3), QString()));
        QVERIFY(QTest::qWaitForWindowExposed(fixture.window()));
        QTRY_VERIFY(fixture.dialog()->property("opened").toBool());
        if (standalone)
            fixture.root->setProperty("visible", false);
        fixture.window()->requestActivate();
        QVERIFY(QTest::qWaitForWindowActive(fixture.window()));
        auto *area = qobject_cast<QQuickItem *>(fixture.textArea());
        QVERIFY(area);
        QString text;
        for (int line = 1; line <= 30; ++line)
            text += QStringLiteral("Line %1: keep the complete reflection visible.\n").arg(line);
        QVERIFY(text.size() < 2000);
        QGuiApplication::clipboard()->setText(text);
        fixture.pasteShortcut();
        QTRY_COMPARE(fixture.note(), text);
        QTRY_COMPARE(area->property("cursorPosition").toInt(), text.size());
        const auto cursorVisible = [&] {
            const QRectF cursor = area->mapRectToScene(area->property("cursorRectangle").toRectF());
            for (auto *item = area; item; item = item->parentItem()) {
                if (!item->clip()) continue;
                const QRectF clip = item->mapRectToScene(item->boundingRect());
                if (cursor.top() < clip.top() - 1 || cursor.bottom() > clip.bottom() + 1)
                    return false;
            }
            return QRectF(QPointF(), fixture.window()->size()).contains(cursor);
        };
        QTRY_VERIFY(cursorVisible());
        QTest::keyClick(fixture.window(), Qt::Key_Home, Qt::ControlModifier);
        QTRY_COMPARE(area->property("cursorPosition").toInt(), 0);
        QTRY_VERIFY(cursorVisible());
        QTest::keyClick(fixture.window(), Qt::Key_End, Qt::ControlModifier);
        QTRY_COMPARE(area->property("cursorPosition").toInt(), text.size());
        QTRY_VERIFY(cursorVisible());
        QTest::keyClick(fixture.window(), Qt::Key_X);
        QTRY_COMPARE(fixture.note(), text + QStringLiteral("x"));
        QTRY_VERIFY(cursorVisible());
        if (standalone)
            QTRY_COMPARE(fixture.window()->height(), qRound(fixture.dialog()->property("implicitHeight").toReal()));
        if (standalone) {
            auto *finish = fixture.dialog()->findChild<QQuickItem *>(QStringLiteral("saveReflectionButton"));
            QVERIFY(finish);
            QTRY_VERIFY(QRectF(QPointF(), fixture.window()->size()).contains(
                finish->mapRectToScene(finish->boundingRect())));
        }
        const QString screenshot = qEnvironmentVariable("MR_LONG_REFLECTION_SCREENSHOT");
        if (!screenshot.isEmpty())
            QVERIFY(fixture.window()->grabWindow().save(screenshot + (standalone ? "-popup.png" : "-main.png")));
    }

    void completedDutyPopupAcceptsClipboardImagesByButtonAndKeyboard_data()
    {
        QTest::addColumn<bool>("standalone");
        QTest::addColumn<bool>("askingResult");
        QTest::addColumn<bool>("dark");
        QTest::newRow("full-note") << false << false << false;
        QTest::newRow("compact-note") << true << false << false;
        QTest::newRow("compact-result") << true << true << false;
        QTest::newRow("compact-result-dark") << true << true << true;
    }

    void completedDutyPopupAcceptsClipboardImagesByButtonAndKeyboard()
    {
        QFETCH(bool, standalone);
        QFETCH(bool, askingResult);
        QFETCH(bool, dark);
        DialogFixture fixture;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));
        fixture.controller.setThemeMode(dark ? QStringLiteral("dark") : QStringLiteral("light"));
        fixture.dialog()->setProperty("standalone", standalone);
        auto completed = finishedRun(kImageRunId(), QStringLiteral("伊库拉尔堡垒"), 1);
        completed.insert(QStringLiteral("job_id"), 24);
        completed.insert(QStringLiteral("result"), QStringLiteral("COMPLETED"));
        completed.insert(QStringLiteral("pending_review"), false);
        completed.insert(QStringLiteral("duty_category"), QStringLiteral("四人迷宫"));
        auto event = runFinishedEvent(completed);
        event.insert(QStringLiteral("state"), QStringLiteral("COMPLETED"));
        if (askingResult)
            QVERIFY(fixture.openForResult(completed));
        else
            Q_EMIT fixture.backend.liveEvent(event);
        QTRY_VERIFY(fixture.dialog()->property("visible").toBool());
        QCOMPARE(fixture.dialog()->property("runId").toString(), kImageRunId());
        if (!askingResult)
            QCOMPARE(fixture.dialog()->property("kicker").toString(), QStringLiteral("刚刚完成"));
        QVERIFY(QTest::qWaitForWindowExposed(fixture.window()));
        if (standalone)
            fixture.root->setProperty("visible", false);
        fixture.window()->requestActivate();
        QVERIFY(QTest::qWaitForWindowActive(fixture.window()));
        fixture.type(QStringLiteral("先保留正在写的心得。"));
        QImage image(36, 28, QImage::Format_ARGB32);
        image.fill(QColor(0, 80, 160, 192));
        QGuiApplication::clipboard()->setImage(image);
        fixture.backend.commands.clear();
        QVERIFY(fixture.click(standalone ? QStringLiteral("pasteReflectionImageButtonCompact")
                                        : QStringLiteral("pasteReflectionImageButton")));
        QTRY_COMPARE(asList(fixture.dialog()->property("noteImages")).size(), 1);
        QCOMPARE(fixture.note(), QStringLiteral("先保留正在写的心得。"));
        fixture.pasteShortcut();
        QTRY_COMPARE(asList(fixture.dialog()->property("noteImages")).size(), 2);
        QCOMPARE(fixture.note(), QStringLiteral("先保留正在写的心得。"));
        QCOMPARE(fixture.backend.commands.count(QStringLiteral("SetRunReflection")), 0);
        QCOMPARE(fixture.backend.commands.count(QStringLiteral("CreateManualRun")), 0);
        QCOMPARE(fixture.backend.commands.count(QStringLiteral("CorrectRun")), 0);
        const auto rows = fixture.images.imagesFor(kImageRunId());
        QCOMPARE(rows.size(), 2);
        QCOMPARE(QImage(rows.first().toMap().value(QStringLiteral("path")).toString()).pixelColor(0, 0),
                 image.pixelColor(0, 0));

        // Switching back to a text-only clipboard disables the image shortcut;
        // the focused TextArea receives its original native Ctrl+V behavior.
        QGuiApplication::clipboard()->setText(QStringLiteral("追加文字"));
        fixture.textArea()->setProperty("cursorPosition", fixture.note().size());
        fixture.pasteShortcut();
        QTRY_COMPARE(fixture.note(), QStringLiteral("先保留正在写的心得。追加文字"));
        QCOMPARE(fixture.images.imagesFor(kImageRunId()).size(), 2);
        if (standalone) {
            QGuiApplication::clipboard()->setImage(image);
            fixture.window()->contentItem()->forceActiveFocus();
            QTest::keyClick(fixture.window(), Qt::Key_V, Qt::ControlModifier);
            QTRY_COMPARE(fixture.images.imagesFor(kImageRunId()).size(), 3);
        }
        const QString screenshot = qEnvironmentVariable("MR_REFLECTION_CLIPBOARD_SCREENSHOT");
        if (!screenshot.isEmpty()) {
            QString suffix = QStringLiteral("-full-note.png");
            if (standalone)
                suffix = askingResult ? QStringLiteral("-compact-result.png") : QStringLiteral("-compact-note.png");
            QVERIFY(fixture.window()->grabWindow().save(screenshot
                + (dark ? QStringLiteral("-dark") : QStringLiteral("-light")) + suffix));
        }

        if (standalone) {
            verifyCompactImageLimitsAndScrolling(fixture, askingResult);
            QVERIFY(!QTest::currentTestFailed());
        }

        // Attachments already belong to the completed run, like chosen files.
        // Closing the note neither deletes them nor creates/corrects a record.
        QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "close"));
        QTRY_VERIFY(!fixture.dialog()->property("visible").toBool());
        QCOMPARE(fixture.images.imagesFor(kImageRunId()).size(), standalone ? mr::NoteImageStore::kMaxImagesPerRun : 2);
        QCOMPARE(fixture.backend.commands.count(QStringLiteral("SetRunReflection")), 0);
        QCOMPARE(fixture.backend.commands.count(QStringLiteral("CreateManualRun")), 0);
        QCOMPARE(fixture.backend.commands.count(QStringLiteral("CorrectRun")), 0);
    }

    void clipboardFailureAndNoteSaveRetryKeepOneAttachmentAndNeverCreateRecords_data()
    {
        QTest::addColumn<bool>("standalone");
        QTest::newRow("full-note") << false;
        QTest::newRow("compact-note") << true;
    }

    void clipboardFailureAndNoteSaveRetryKeepOneAttachmentAndNeverCreateRecords()
    {
        QFETCH(bool, standalone);
        DialogFixture fixture;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));
        fixture.dialog()->setProperty("standalone", standalone);
        const QString pasteButton = standalone ? QStringLiteral("pasteReflectionImageButtonCompact")
                                               : QStringLiteral("pasteReflectionImageButton");
        auto completed = finishedRun(kImageRunId(), QStringLiteral("伊库拉尔堡垒"), 1);
        completed.insert(QStringLiteral("job_id"), 24);
        QVERIFY(fixture.openForRun(completed, QStringLiteral("刚刚完成")));
        QVERIFY(QTest::qWaitForWindowExposed(fixture.window()));
        fixture.type(QStringLiteral("保存失败也要保留这段心得。"));
        fixture.backend.commands.clear();
        QGuiApplication::clipboard()->clear();
        QVERIFY(fixture.click(pasteButton));
        QVERIFY(fixture.dialog()->property("noteImageError").toString().contains(QStringLiteral("没有图片")));
        QCOMPARE(fixture.note(), QStringLiteral("保存失败也要保留这段心得。"));

        QImage image(24, 16, QImage::Format_RGB32);
        image.fill(Qt::magenta);
        QGuiApplication::clipboard()->setImage(image);
        fixture.images.setCopyFunctionForTesting([](const QString &, const QString &) { return false; });
        QVERIFY(fixture.click(pasteButton));
        QVERIFY(!fixture.dialog()->property("noteImageError").toString().isEmpty());
        QVERIFY(fixture.images.imagesFor(kImageRunId()).isEmpty());
        fixture.images.setCopyFunctionForTesting({});
        QVERIFY(fixture.click(pasteButton));
        QCOMPARE(fixture.images.imagesFor(kImageRunId()).size(), 1);
        QVERIFY(fixture.dialog()->property("noteImageError").toString().isEmpty());
        QCOMPARE(fixture.backend.commands.count(QStringLiteral("SetRunReflection")), 0);
        QCOMPARE(fixture.backend.commands.count(QStringLiteral("CreateManualRun")), 0);
        QCOMPARE(fixture.backend.commands.count(QStringLiteral("CorrectRun")), 0);

        fixture.backend.syntheticReflection = true;
        fixture.backend.refuseReflectionOnce = true;
        QVERIFY(fixture.click(QStringLiteral("saveReflectionButton")));
        QTRY_VERIFY(!fixture.dialog()->property("submitting").toBool());
        QVERIFY(fixture.dialog()->property("visible").toBool());
        QVERIFY(!fixture.dialog()->property("errorText").toString().isEmpty());
        QCOMPARE(fixture.note(), QStringLiteral("保存失败也要保留这段心得。"));
        QCOMPARE(fixture.images.imagesFor(kImageRunId()).size(), 1);
        QVERIFY(fixture.click(QStringLiteral("saveReflectionButton")));
        QTRY_VERIFY(!fixture.dialog()->property("visible").toBool());
        QCOMPARE(fixture.images.imagesFor(kImageRunId()).size(), 1);
        QCOMPARE(fixture.backend.commands.count(QStringLiteral("SetRunReflection")), 2);
        QCOMPARE(fixture.backend.commands.count(QStringLiteral("CreateManualRun")), 0);
        QCOMPARE(fixture.backend.commands.count(QStringLiteral("CorrectRun")), 0);
    }

    void aReAskedRunKeepsTheNoteTheUserIsTyping()
    {
        DialogFixture fixture;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));

        QVERIFY(fixture.openForResult(finishedRun(QStringLiteral("run-a"),
                                                  QStringLiteral("水晶塔"), 3)));
        QVERIFY(fixture.dialog()->property("visible").toBool());
        QVERIFY(fixture.dialog()->property("askingResult").toBool());
        fixture.type(QStringLiteral("新人第一次进，讲了三次分摊。"));

        // The same question again - the controller re-offers a run whose dialog
        // it believes was never shown. Nothing typed may be lost.
        QVERIFY(fixture.openForResult(finishedRun(QStringLiteral("run-a"),
                                                  QStringLiteral("水晶塔"), 3)));
        QCOMPARE(fixture.note(), QStringLiteral("新人第一次进，讲了三次分摊。"));

        // A different run cannot replace an open draft either.
        QVERIFY(fixture.openForResult(finishedRun(QStringLiteral("run-b"),
                                                  QStringLiteral("石卫塔"), 1)));
        QCOMPARE(fixture.note(), QStringLiteral("新人第一次进，讲了三次分摊。"));
        QCOMPARE(fixture.dialog()->property("runId").toString(), QStringLiteral("run-a"));
        QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "close"));
        QTRY_VERIFY(!fixture.dialog()->property("visible").toBool());
        QVERIFY(fixture.openForResult(finishedRun(QStringLiteral("run-b"),
                                                  QStringLiteral("石卫塔"), 1)));
        QCOMPARE(fixture.note(), QString());
        QCOMPARE(fixture.dialog()->property("runId").toString(), QStringLiteral("run-b"));
    }

    void openingAndClosingIsAcknowledgedToTheController()
    {
        DialogFixture fixture;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));

        QVERIFY(fixture.openForResult(finishedRun(QStringLiteral("run-a"),
                                                  QStringLiteral("水晶塔"), 3)));
        // resultConfirmationShown() was reachable and was called.
        QVERIFY(fixture.dialog()->property("resultAcknowledged").toBool());

        // 稍后再说 / Escape / a click outside all end in close(); onClosed is the
        // single place the controller is told, so any of them clears the flag.
        QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "close"));
        QTRY_VERIFY(!fixture.dialog()->property("visible").toBool());
        QVERIFY(!fixture.dialog()->property("resultAcknowledged").toBool());
    }

    /// A question dropped because the window was busy saving another run's 心得
    /// is asked again as soon as that window closes.
    ///
    /// The controller keeps such a run queued - it only marks a run asked when
    /// the dialog acknowledges it - and resultConfirmationClosed() is the one
    /// thing that re-offers it. Every close must send it, not only the close of
    /// an acknowledged 结果 question: otherwise a 心得 window closing tells the
    /// controller nothing and the dropped question waits for the *next* finished
    /// run, which in a session with no next run never comes.
    void aQuestionDroppedByABusyDialogIsAskedAgainWhenItCloses_data()
    {
        QTest::addColumn<bool>("saving");
        QTest::newRow("typing-a-note") << false;
        QTest::newRow("saving-a-note") << true;
    }

    void aQuestionDroppedByABusyDialogIsAskedAgainWhenItCloses()
    {
        QFETCH(bool, saving);
        DialogFixture fixture;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));
        QSignalSpy asked(&fixture.controller,
                         &mr::AppController::resultConfirmationRequested);

        // The user is in the middle of writing up an earlier run.
        QVERIFY(fixture.openForRun(finishedRun(QStringLiteral("run-old"),
                                               QStringLiteral("邪龙坠巢"), 2),
                                   QStringLiteral("补录笔记")));
        fixture.type(QStringLiteral("路上讲了机制。"));
        fixture.dialog()->setProperty("submitting", saving);
        QCOMPARE(fixture.dialog()->property("busy").toBool(), saving);

        // A duty ends. The controller asks; the busy window cannot show it.
        fixture.publishFinished(finishedRun(QStringLiteral("run-a"),
                                            QStringLiteral("水晶塔"), 3));
        QTRY_COMPARE_WITH_TIMEOUT(asked.count(), 1, 3000);
        QCOMPARE(fixture.dialog()->property("runId").toString(), QStringLiteral("run-old"));
        QCOMPARE(fixture.note(), QStringLiteral("路上讲了机制。"));
        QVERIFY(!fixture.dialog()->property("askingResult").toBool());

        // The 心得 is saved and the window closes. This is the moment the
        // controller has to hear about.
        fixture.dialog()->setProperty("submitting", false);
        QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "close"));

        QTRY_COMPARE_WITH_TIMEOUT(asked.count(), 2, 3000);
        QCOMPARE(asked.at(1).at(0).toMap().value(QStringLiteral("run_id")).toString(),
                 QStringLiteral("run-a"));
        // And this time it is on screen, as the 结果 question.
        QTRY_VERIFY(fixture.dialog()->property("visible").toBool());
        QCOMPARE(fixture.dialog()->property("runId").toString(), QStringLiteral("run-a"));
        QVERIFY(fixture.dialog()->property("askingResult").toBool());
        QVERIFY(fixture.dialog()->property("resultAcknowledged").toBool());
    }

    void aCompletionPromptCannotOverwriteAnOpenNote_data()
    {
        QTest::addColumn<bool>("sameRun");
        QTest::newRow("same-run") << true;
        QTest::newRow("another-run") << false;
    }

    void aCompletionPromptCannotOverwriteAnOpenNote()
    {
        QFETCH(bool, sameRun);
        DialogFixture fixture;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));
        QVERIFY(fixture.openForRun(finishedRun(QStringLiteral("run-old"),
                                               QStringLiteral("邪龙坠巢"), 2),
                                   QStringLiteral("补录笔记")));
        fixture.type(QStringLiteral("这段笔记还没有保存。"));
        auto completed = finishedRun(sameRun ? QStringLiteral("run-old") : QStringLiteral("run-new"),
                                     QStringLiteral("水晶塔"), 3);
        completed.insert(QStringLiteral("result"), QStringLiteral("COMPLETED"));
        completed.insert(QStringLiteral("pending_review"), false);
        auto event = runFinishedEvent(completed);
        event.insert(QStringLiteral("state"), QStringLiteral("COMPLETED"));
        QSignalSpy prompts(&fixture.controller, &mr::AppController::reflectionPromptRequested);
        Q_EMIT fixture.backend.liveEvent(event);
        QTRY_COMPARE(prompts.count(), 1);
        QCOMPARE(fixture.dialog()->property("runId").toString(), QStringLiteral("run-old"));
        QCOMPARE(fixture.dialog()->property("runRevision").toInt(), 2);
        QCOMPARE(fixture.note(), QStringLiteral("这段笔记还没有保存。"));
    }

    /// A 心得 prompt the busy window could not show is offered again as soon as
    /// that window closes, through the same hand-shake the result question has
    /// (review OH-6 / S2-5). Before, the run was marked as offered the moment
    /// the prompt was raised, so the dropped prompt never came back.
    void aCompletionPromptDroppedByABusyDialogIsOfferedWhenItCloses()
    {
        DialogFixture fixture;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));
        QSignalSpy prompts(&fixture.controller, &mr::AppController::reflectionPromptRequested);

        // The user is writing up an earlier run.
        QVERIFY(fixture.openForRun(finishedRun(QStringLiteral("run-old"),
                                               QStringLiteral("邪龙坠巢"), 2),
                                   QStringLiteral("补录笔记")));
        fixture.type(QStringLiteral("路上讲了机制。"));

        // Another run is cleared meanwhile.
        auto completed = finishedRun(QStringLiteral("run-b"), QStringLiteral("水晶塔"), 3);
        completed.insert(QStringLiteral("result"), QStringLiteral("COMPLETED"));
        completed.insert(QStringLiteral("pending_review"), false);
        auto event = runFinishedEvent(completed);
        event.insert(QStringLiteral("state"), QStringLiteral("COMPLETED"));
        Q_EMIT fixture.backend.liveEvent(event);
        QTRY_COMPARE(prompts.count(), 1);
        QCOMPARE(fixture.dialog()->property("runId").toString(), QStringLiteral("run-old"));

        // The earlier note closes; now the prompt is offered, and shown.
        QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "close"));
        QTRY_COMPARE_WITH_TIMEOUT(prompts.count(), 2, 3000);
        QTRY_VERIFY(fixture.dialog()->property("visible").toBool());
        QCOMPARE(fixture.dialog()->property("runId").toString(), QStringLiteral("run-b"));
        QCOMPARE(fixture.dialog()->property("kicker").toString(), QStringLiteral("刚刚完成"));
        QCOMPARE(fixture.note(), QString());

        // Shown once is asked: closing it does not raise it again.
        QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "close"));
        QTRY_VERIFY(!fixture.dialog()->property("visible").toBool());
        QTest::qWait(150);
        QCOMPARE(prompts.count(), 2);
    }

    // 审查 OD-5：结果问题的窗口复位了错误提示，却没复位图片错误，于是乙的「本次导随
    // 结果」里显示着甲那次添加图片失败的原因。
    void aResultQuestionStartsWithoutTheLastRunsImageError()
    {
        DialogFixture fixture;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));
        QVERIFY(fixture.openForRun(finishedRun(QStringLiteral("run-a"), QStringLiteral("水晶塔"), 3),
                                   QStringLiteral("补录笔记")));
        fixture.dialog()->setProperty("noteImageError", QString::fromUtf8("图片太大，没有添加。"));
        QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "close"));
        QTRY_VERIFY(!fixture.dialog()->property("visible").toBool());

        QVERIFY(fixture.openForResult(finishedRun(QStringLiteral("run-b"), QStringLiteral("石卫塔"), 1)));
        QTRY_VERIFY(fixture.dialog()->property("visible").toBool());
        QVERIFY(fixture.dialog()->property("noteImageError").toString().isEmpty());
    }

    void liveRevisionsDoNotAdvanceTheOpenResultFormsBaseline()
    {
        DialogFixture fixture;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));

        QVERIFY(fixture.openForResult(finishedRun(QStringLiteral("run-a"),
                                                  QStringLiteral("水晶塔"), 3)));
        QCOMPARE(fixture.dialog()->property("runRevision").toInt(), 3);

        // A newer result/job may be a conflicting human decision. Only the
        // controller's revision-chain check can authorize an automatic retry.
        Q_EMIT fixture.controller.runRevisionChanged(QStringLiteral("run-a"), 7);
        QCOMPARE(fixture.dialog()->property("runRevision").toInt(), 3);

        // Somebody else's run must not move this one.
        Q_EMIT fixture.controller.runRevisionChanged(QStringLiteral("run-b"), 99);
        QCOMPARE(fixture.dialog()->property("runRevision").toInt(), 3);
        QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "close"));
        QVERIFY(fixture.openForResult(finishedRun(QStringLiteral("run-a"), QStringLiteral("水晶塔"), 7)));
        QCOMPARE(fixture.dialog()->property("runRevision").toInt(), 7);
    }

    /// A CN duty cleared in the duty is recorded as 通关 by itself
    /// (docs/protocol-profile-format.md §12), so the question is only asked when
    /// no clear arrived before the duty was left - and it says so.
    void theResultQuestionSaysNoClearWasSeen()
    {
        DialogFixture fixture;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));
        QVERIFY(fixture.openForResult(finishedRun(QStringLiteral("run-a"), QStringLiteral("水晶塔"), 3)));
        QVERIFY(fixture.dialog()->property("askingResult").toBool());

        auto *content = fixture.dialog()->property("contentItem").value<QQuickItem *>();
        QVERIFY(content);
        QCOMPARE(textStartingWith(content, QString::fromUtf8("《水晶塔》通关了吗？")),
                 QString::fromUtf8("《水晶塔》通关了吗？软件尚未确认本次结果，"
                                   "只有你确认“通关”后这一次才会计入导随次数。"));
    }
};
} // namespace

MR_DESKTOP_TEST_SUITE(ReflectionDialogBinding)
{
    // Never run against the user's own Collector, database or serve lease - see
    // TestCollectorGuard.h. Must precede any construction.
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication app(argc, argv);
    ReflectionDialogBindingTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "ReflectionDialogBindingTests.moc"
