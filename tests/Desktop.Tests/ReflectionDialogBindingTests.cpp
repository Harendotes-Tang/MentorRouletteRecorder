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

#include "TestCollectorGuard.h"
#include "AppController.h"
#include "Formatters.h"
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
#include <QTest>
#include <QTemporaryDir>
#include <QTimer>
#include <QVariantMap>

#include <memory>

namespace {

QVariantMap finishedRun(const QString &id, const QString &dutyName, int revision)
{
    return {
        {QStringLiteral("run_id"), id},
        {QStringLiteral("revision"), revision},
        {QStringLiteral("duty_name"), dutyName},
        {QStringLiteral("job_name"), QStringLiteral("白魔法师")},
        {QStringLiteral("result"), QStringLiteral("UNKNOWN_FINAL_STATE")},
        {QStringLiteral("duration_ms"), 1180422},
        {QStringLiteral("pending_review"), true},
        // Ended just now, so the controller's "not a replay from before this
        // session" guard lets the question through.
        {QStringLiteral("ended_at_utc"),
         QDateTime::currentDateTimeUtc().addSecs(1).toString(Qt::ISODateWithMs)},
    };
}

int g_sequence = 0;

/// A run_finished envelope, the shape the live bus publishes.
QVariantMap runFinishedEvent(const QVariantMap &run)
{
    ++g_sequence;
    return {
        {QStringLiteral("event_id"),
         QStringLiteral("31111111-2222-4333-8444-%1").arg(g_sequence, 12, 10, QLatin1Char('0'))},
        {QStringLiteral("event_type"), QStringLiteral("RunFinished")},
        {QStringLiteral("kind"), QStringLiteral("run_finished")},
        {QStringLiteral("emitted_at_utc"), QStringLiteral("2026-09-08T11:00:00.000Z")},
        {QStringLiteral("sequence"), g_sequence},
        {QStringLiteral("state"), QStringLiteral("UNKNOWN_FINAL_STATE")},
        {QStringLiteral("run"), run},
    };
}

struct ObservedBackend : mr::MockBackend {
    QStringList commands;
    bool syntheticReflection = false;
    bool refuseReflectionOnce = false;

    mr::BackendReply *request(const QString &type, const QJsonObject &payload = {}) override
    {
        commands.append(type);
        if (!syntheticReflection || type != QStringLiteral("SetRunReflection"))
            return mr::MockBackend::request(type, payload);
        auto *reply = new mr::BackendReply(QStringLiteral("synthetic-note"), type, this);
        const bool refuse = refuseReflectionOnce;
        refuseReflectionOnce = false;
        QTimer::singleShot(0, reply, [reply, payload, refuse] {
            if (refuse) {
                reply->fail(QStringLiteral("ERR_BAD_REQUEST"), QStringLiteral("synthetic save refusal"));
                return;
            }
            reply->succeed({{QStringLiteral("run_id"), payload.value(QStringLiteral("run_id"))},
                           {QStringLiteral("reflection"), QJsonObject{
                                {QStringLiteral("mood"), payload.value(QStringLiteral("mood"))},
                                {QStringLiteral("text"), payload.value(QStringLiteral("text"))}}}});
        });
        return reply;
    }
};

QVariantList asList(const QVariant &value)
{
    return value.canConvert<QJSValue>() ? value.value<QJSValue>().toVariant().toList() : value.toList();
}

const QString kImageRunId = QStringLiteral("c32d8f24-e263-4245-9428-c8e66c538e21");

struct DialogFixture {
    ObservedBackend backend;
    mr::AppController controller{&backend, nullptr};
    mr::Formatters formatters;
    QTemporaryDir imageRoot;
    mr::NoteImageStore images{imageRoot.path()};
    QQmlEngine engine;
    std::unique_ptr<QObject> root;
    QString errors;

    bool create()
    {
        engine.rootContext()->setContextProperty(QStringLiteral("App"), &controller);
        engine.rootContext()->setContextProperty(QStringLiteral("Fmt"), &formatters);
        engine.rootContext()->setContextProperty(QStringLiteral("NoteImages"), &images);
        engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 900; height: 800; visible: true
    ReflectionDialog { id: reflection; objectName: "reflection" }
    Connections {
        target: App
        function onReflectionPromptRequested(run) { reflection.openForPrompt(run, "刚刚完成") }
    }
})",
                          QUrl());
        root.reset(component.create());
        for (const auto &error : component.errors())
            errors += error.toString() + QLatin1Char('\n');
        return root != nullptr;
    }

    QObject *dialog() const { return root->findChild<QObject *>(QStringLiteral("reflection")); }
    QObject *textArea() const
    {
        return dialog()->findChild<QObject *>(QStringLiteral("reflectionTextArea"));
    }

    bool openForResult(const QVariantMap &run)
    {
        return QMetaObject::invokeMethod(dialog(), "openForResult",
                                         Q_ARG(QVariant, QVariant(run)));
    }

    // Typing, at the level the dialog sees it: a QML-side write to the text
    // area, exactly as TextArea does for a keystroke.
    void type(const QString &note)
    {
        auto *area = textArea();
        QQmlExpression assign(qmlContext(area), area,
                              QStringLiteral("text = \"%1\"").arg(note));
        assign.evaluate();
        QVERIFY2(!assign.hasError(), qPrintable(assign.error().toString()));
    }

    QString note() const { return textArea()->property("text").toString(); }

    QQuickWindow *window() const { return qobject_cast<QQuickWindow *>(root.get()); }

    bool click(const QString &name)
    {
        auto *item = dialog()->findChild<QQuickItem *>(name);
        if (!item || !QTest::qWaitFor([&] { return item->isVisible() && item->width() > 0; }, 1500))
            return false;
        QTest::qWait(40);
        const QPoint centre = item->mapToScene(QPointF(item->width() / 2, item->height() / 2)).toPoint();
        if (!QRect(QPoint(0, 0), window()->size()).contains(centre))
            return false;
        QTest::mouseClick(window(), Qt::LeftButton, Qt::NoModifier, centre);
        return true;
    }

    void pasteShortcut()
    {
        auto *area = qobject_cast<QQuickItem *>(textArea());
        QVERIFY(area);
        area->forceActiveFocus();
        QTest::keyClick(window(), Qt::Key_V, Qt::ControlModifier);
    }

    bool openForRun(const QVariantMap &run, const QString &kicker)
    {
        return QMetaObject::invokeMethod(dialog(), "openForRun", Q_ARG(QVariant, QVariant(run)),
                                         Q_ARG(QVariant, QVariant(kicker)));
    }

    /// Publish a finished run the way the Collector's live bus does, so the
    /// controller's own queue - not the test - decides what is asked and when.
    void publishFinished(const QVariantMap &run) { Q_EMIT backend.liveEvent(runFinishedEvent(run)); }
};

/// The text of the first item under \a from, visible or not, that starts with
/// \a prefix; empty when there is none.
QString textStartingWith(QQuickItem *from, const QString &prefix)
{
    if (!from)
        return {};
    const QString text = from->property("text").toString();
    if (text.startsWith(prefix))
        return text;
    for (QQuickItem *child : from->childItems()) {
        const QString match = textStartingWith(child, prefix);
        if (!match.isEmpty())
            return match;
    }
    return {};
}

} // namespace

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

    void completedDutyPopupAcceptsClipboardImagesByButtonAndKeyboard()
    {
        DialogFixture fixture;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));
        auto completed = finishedRun(kImageRunId, QStringLiteral("伊库拉尔堡垒"), 1);
        completed.insert(QStringLiteral("job_id"), 24);
        completed.insert(QStringLiteral("result"), QStringLiteral("COMPLETED"));
        completed.insert(QStringLiteral("pending_review"), false);
        auto event = runFinishedEvent(completed);
        event.insert(QStringLiteral("state"), QStringLiteral("COMPLETED"));
        Q_EMIT fixture.backend.liveEvent(event);
        QTRY_VERIFY(fixture.dialog()->property("visible").toBool());
        QCOMPARE(fixture.dialog()->property("runId").toString(), kImageRunId);
        QCOMPARE(fixture.dialog()->property("kicker").toString(), QStringLiteral("刚刚完成"));
        fixture.type(QStringLiteral("先保留正在写的心得。"));
        QImage image(36, 28, QImage::Format_ARGB32);
        image.fill(QColor(0, 80, 160, 192));
        QGuiApplication::clipboard()->setImage(image);
        fixture.backend.commands.clear();
        QVERIFY(fixture.click(QStringLiteral("pasteReflectionImageButton")));
        QTRY_COMPARE(asList(fixture.dialog()->property("noteImages")).size(), 1);
        QCOMPARE(fixture.note(), QStringLiteral("先保留正在写的心得。"));
        fixture.pasteShortcut();
        QTRY_COMPARE(asList(fixture.dialog()->property("noteImages")).size(), 2);
        QCOMPARE(fixture.note(), QStringLiteral("先保留正在写的心得。"));
        QCOMPARE(fixture.backend.commands.count(QStringLiteral("SetRunReflection")), 0);
        QCOMPARE(fixture.backend.commands.count(QStringLiteral("CreateManualRun")), 0);
        QCOMPARE(fixture.backend.commands.count(QStringLiteral("CorrectRun")), 0);
        const auto rows = fixture.images.imagesFor(kImageRunId);
        QCOMPARE(rows.size(), 2);
        QCOMPARE(QImage(rows.first().toMap().value(QStringLiteral("path")).toString()).pixelColor(0, 0),
                 image.pixelColor(0, 0));

        // Switching back to a text-only clipboard disables the image shortcut;
        // the focused TextArea receives its original native Ctrl+V behavior.
        QGuiApplication::clipboard()->setText(QStringLiteral("追加文字"));
        fixture.textArea()->setProperty("cursorPosition", fixture.note().size());
        fixture.pasteShortcut();
        QTRY_COMPARE(fixture.note(), QStringLiteral("先保留正在写的心得。追加文字"));
        QCOMPARE(fixture.images.imagesFor(kImageRunId).size(), 2);
        const QString screenshot = qEnvironmentVariable("MR_REFLECTION_CLIPBOARD_SCREENSHOT");
        if (!screenshot.isEmpty())
            QVERIFY(fixture.window()->grabWindow().save(screenshot));

        // Attachments already belong to the completed run, like chosen files.
        // Closing the note neither deletes them nor creates/corrects a record.
        QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "close"));
        QTRY_VERIFY(!fixture.dialog()->property("visible").toBool());
        QCOMPARE(fixture.images.imagesFor(kImageRunId).size(), 2);
        QCOMPARE(fixture.backend.commands.count(QStringLiteral("SetRunReflection")), 0);
        QCOMPARE(fixture.backend.commands.count(QStringLiteral("CreateManualRun")), 0);
        QCOMPARE(fixture.backend.commands.count(QStringLiteral("CorrectRun")), 0);
    }

    void clipboardFailureAndNoteSaveRetryKeepOneAttachmentAndNeverCreateRecords()
    {
        DialogFixture fixture;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));
        auto completed = finishedRun(kImageRunId, QStringLiteral("伊库拉尔堡垒"), 1);
        completed.insert(QStringLiteral("job_id"), 24);
        QVERIFY(fixture.openForRun(completed, QStringLiteral("刚刚完成")));
        fixture.type(QStringLiteral("保存失败也要保留这段心得。"));
        fixture.backend.commands.clear();
        QGuiApplication::clipboard()->clear();
        QVERIFY(fixture.click(QStringLiteral("pasteReflectionImageButton")));
        QVERIFY(fixture.dialog()->property("noteImageError").toString().contains(QStringLiteral("没有图片")));
        QCOMPARE(fixture.note(), QStringLiteral("保存失败也要保留这段心得。"));

        QImage image(24, 16, QImage::Format_RGB32);
        image.fill(Qt::magenta);
        QGuiApplication::clipboard()->setImage(image);
        fixture.images.setCopyFunctionForTesting([](const QString &, const QString &) { return false; });
        QVERIFY(fixture.click(QStringLiteral("pasteReflectionImageButton")));
        QVERIFY(!fixture.dialog()->property("noteImageError").toString().isEmpty());
        QVERIFY(fixture.images.imagesFor(kImageRunId).isEmpty());
        fixture.images.setCopyFunctionForTesting({});
        QVERIFY(fixture.click(QStringLiteral("pasteReflectionImageButton")));
        QCOMPARE(fixture.images.imagesFor(kImageRunId).size(), 1);
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
        QCOMPARE(fixture.images.imagesFor(kImageRunId).size(), 1);
        QVERIFY(fixture.click(QStringLiteral("saveReflectionButton")));
        QTRY_VERIFY(!fixture.dialog()->property("visible").toBool());
        QCOMPARE(fixture.images.imagesFor(kImageRunId).size(), 1);
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
        QCOMPARE(textStartingWith(content, QString::fromUtf8("《水晶塔》打完了吗？")),
                 QString::fromUtf8("《水晶塔》打完了吗？离开副本前没有收到通关结算，"
                                   "只有你确认“通关”后这一次才会计入导随次数。"));
    }
};

int main(int argc, char *argv[])
{
    // Never run against the user's own Collector, database or serve lease - see
    // TestCollectorGuard.h. Must precede any construction.
    mrtest::disableCollectorLaunch();
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication app(argc, argv);
    ReflectionDialogBindingTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "ReflectionDialogBindingTests.moc"
