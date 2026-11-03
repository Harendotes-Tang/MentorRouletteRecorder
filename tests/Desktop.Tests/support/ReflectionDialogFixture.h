#pragma once

// The ReflectionDialog scene the reflection suites share: a main window with the
// dialog, the popup's own window beside it, and an observed mock backend.
// ReflectionDialogBindingTests drives the 本次导随结果 half and the images,
// ReflectionDialogMoodNoteTests the mood-only notes.

#include "AppController.h"
#include "Formatters.h"
#include "JobCatalog.h"
#include "MockBackend.h"
#include "NoteImageStore.h"

#include <QDateTime>
#include <QJsonObject>
#include <QList>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQmlExpression>
#include <QQuickItem>
#include <QQuickWindow>
#include <QStringList>
#include <QTemporaryDir>
#include <QTest>
#include <QTimer>
#include <QVariantMap>

#include <memory>

namespace mrtest::reflectiondialog {

inline QVariantMap finishedRun(const QString &id, const QString &dutyName, int revision)
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

/// A run_finished envelope, the shape the live bus publishes.
inline QVariantMap runFinishedEvent(const QVariantMap &run)
{
    static int sequence = 0;
    ++sequence;
    return {
        {QStringLiteral("event_id"),
         QStringLiteral("31111111-2222-4333-8444-%1").arg(sequence, 12, 10, QLatin1Char('0'))},
        {QStringLiteral("event_type"), QStringLiteral("RunFinished")},
        {QStringLiteral("kind"), QStringLiteral("run_finished")},
        {QStringLiteral("emitted_at_utc"), QStringLiteral("2026-09-08T11:00:00.000Z")},
        {QStringLiteral("sequence"), sequence},
        {QStringLiteral("state"), QStringLiteral("UNKNOWN_FINAL_STATE")},
        {QStringLiteral("run"), run},
    };
}

struct ObservedBackend : mr::MockBackend {
    QStringList commands;
    /// Every SetRunReflection payload, in order.
    QList<QJsonObject> reflections;
    bool syntheticReflection = false;
    bool refuseReflectionOnce = false;

    mr::BackendReply *request(const QString &type, const QJsonObject &payload = {}) override
    {
        commands.append(type);
        if (type == QStringLiteral("SetRunReflection"))
            reflections.append(payload);
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

struct DialogFixture {
    ObservedBackend backend;
    mr::AppController controller{&backend, nullptr};
    mr::Formatters formatters;
    mr::JobCatalog jobs;
    QTemporaryDir imageRoot{QString::fromUtf8(MR_SOURCE_DIR)
                           + QStringLiteral("/artifacts/recoverable-prompts-20261020/reflection-images-XXXXXX")};
    mr::NoteImageStore images{imageRoot.path()};
    QQmlEngine engine;
    std::unique_ptr<QObject> root;
    QString errors;

    bool create()
    {
        engine.rootContext()->setContextProperty(QStringLiteral("App"), &controller);
        engine.rootContext()->setContextProperty(QStringLiteral("Fmt"), &formatters);
        engine.rootContext()->setContextProperty(QStringLiteral("Jobs"), &jobs);
        engine.rootContext()->setContextProperty(QStringLiteral("NoteImages"), &images);
        engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    id: mainWindow
    width: 900; height: 800; visible: true
    RecoveryReminderWindow {
        id: compactHost
        objectName: "compactHost"
        editor: reflection
        visible: reflection.standalone && reflection.visible
    }
    ReflectionDialog {
        id: reflection
        objectName: "reflection"
        parent: standalone ? compactHost.contentItem : Overlay.overlay
    }
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

    bool openForResult(const QVariantMap &run) const
    {
        return QMetaObject::invokeMethod(dialog(), "openForResult",
                                         Q_ARG(QVariant, QVariant(run)));
    }

    // Typing, at the level the dialog sees it: a QML-side write to the text
    // area, exactly as TextArea does for a keystroke.
    void type(const QString &note) const
    {
        auto *area = textArea();
        QQmlExpression assign(qmlContext(area), area,
                              QStringLiteral("text = \"%1\"").arg(note));
        assign.evaluate();
        QVERIFY2(!assign.hasError(), qPrintable(assign.error().toString()));
    }

    QString note() const { return textArea()->property("text").toString(); }

    QQuickWindow *window() const
    {
        return dialog()->property("standalone").toBool()
            ? root->findChild<QQuickWindow *>(QStringLiteral("compactHost"))
            : qobject_cast<QQuickWindow *>(root.get());
    }

    bool click(const QString &name) const
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

    void pasteShortcut() const
    {
        auto *area = qobject_cast<QQuickItem *>(textArea());
        QVERIFY(area);
        area->forceActiveFocus();
        QTest::keyClick(window(), Qt::Key_V, Qt::ControlModifier);
    }

    bool openForRun(const QVariantMap &run, const QString &kicker) const
    {
        return QMetaObject::invokeMethod(dialog(), "openForRun", Q_ARG(QVariant, QVariant(run)),
                                         Q_ARG(QVariant, QVariant(kicker)));
    }

    /// Publish a finished run the way the Collector's live bus does, so the
    /// controller's own queue - not the test - decides what is asked and when.
    void publishFinished(const QVariantMap &run) { Q_EMIT backend.liveEvent(runFinishedEvent(run)); }
};

} // namespace mrtest::reflectiondialog
