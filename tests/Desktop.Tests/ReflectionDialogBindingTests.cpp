// ReflectionDialog's 本次导随结果 half, driven the way the controller drives it.
//
// The controller records a finished run as "asked" only once the dialog reports
// App.resultConfirmationShown(runId), and re-offers whatever the dialog could
// not show once App.resultConfirmationClosed() arrives. The same run therefore
// reaches openForResult twice, and the second call must not clear a 心得 the
// user is in the middle of typing.
//
// These tests open for a run, type, let the same run arrive again and assert the
// note survives; a different run must start empty. They also pin the
// acknowledgement pair: forgetting the closed half leaves the controller
// believing a dialog is still on screen, silencing every further question for
// the rest of the session.

#include "TestCollectorGuard.h"
#include "AppController.h"
#include "Formatters.h"
#include "MockBackend.h"

#include <QDateTime>
#include <QDir>
#include <QGuiApplication>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQmlExpression>
#include <QQuickStyle>
#include <QSignalSpy>
#include <QTest>
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

struct DialogFixture {
    mr::MockBackend backend;
    mr::AppController controller{&backend, nullptr};
    mr::Formatters formatters;
    QQmlEngine engine;
    std::unique_ptr<QObject> root;
    QString errors;

    bool create()
    {
        engine.rootContext()->setContextProperty(QStringLiteral("App"), &controller);
        engine.rootContext()->setContextProperty(QStringLiteral("Fmt"), &formatters);
        engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 900; height: 800; visible: true
    ReflectionDialog { objectName: "reflection" }
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

    bool openForRun(const QVariantMap &run, const QString &kicker)
    {
        return QMetaObject::invokeMethod(dialog(), "openForRun", Q_ARG(QVariant, QVariant(run)),
                                         Q_ARG(QVariant, QVariant(kicker)));
    }

    /// Publish a finished run the way the Collector's live bus does, so the
    /// controller's own queue - not the test - decides what is asked and when.
    void publishFinished(const QVariantMap &run) { Q_EMIT backend.liveEvent(runFinishedEvent(run)); }
};

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

        // A different run is a different diary entry and starts empty.
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
    void aQuestionDroppedByABusyDialogIsAskedAgainWhenItCloses()
    {
        DialogFixture fixture;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));
        QSignalSpy asked(&fixture.controller,
                         &mr::AppController::resultConfirmationRequested);

        // The user is in the middle of writing up an earlier run.
        QVERIFY(fixture.openForRun(finishedRun(QStringLiteral("run-old"),
                                               QStringLiteral("邪龙坠巢"), 2),
                                   QStringLiteral("补录笔记")));
        fixture.type(QStringLiteral("路上讲了机制。"));
        fixture.dialog()->setProperty("submitting", true);
        QVERIFY(fixture.dialog()->property("busy").toBool());

        // A duty ends. The controller asks; the busy window cannot show it.
        fixture.publishFinished(finishedRun(QStringLiteral("run-a"),
                                            QStringLiteral("水晶塔"), 3));
        QTRY_COMPARE_WITH_TIMEOUT(asked.count(), 1, 3000);
        QCOMPARE(fixture.dialog()->property("runId").toString(), QStringLiteral("run-old"));
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

    void aNewRevisionFromTheControllerReachesTheDialog()
    {
        DialogFixture fixture;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));

        QVERIFY(fixture.openForResult(finishedRun(QStringLiteral("run-a"),
                                                  QStringLiteral("水晶塔"), 3)));
        QCOMPARE(fixture.dialog()->property("runRevision").toInt(), 3);

        // A live run_updated, or the controller's own retry after
        // ERR_REVISION_CONFLICT. The next correction must carry the new number.
        Q_EMIT fixture.controller.runRevisionChanged(QStringLiteral("run-a"), 7);
        QCOMPARE(fixture.dialog()->property("runRevision").toInt(), 7);

        // Somebody else's run must not move this one.
        Q_EMIT fixture.controller.runRevisionChanged(QStringLiteral("run-b"), 99);
        QCOMPARE(fixture.dialog()->property("runRevision").toInt(), 7);
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
