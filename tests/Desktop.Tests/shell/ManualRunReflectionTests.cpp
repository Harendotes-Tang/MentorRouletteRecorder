#include "DesktopTestRunner.h"
#include "TestCollectorGuard.h"
#include "AppController.h"
#include "Formatters.h"
#include "JobCatalog.h"
#include "MockBackend.h"
#include "RoleCatalog.h"
#include "RunFormValidator.h"

#include <QDir>
#include <QGuiApplication>
#include <QImage>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickStyle>
#include <QQuickItem>
#include <QQuickWindow>
#include <QSignalSpy>
#include <QTest>
#include <QTimer>
#include <memory>
#include <utility>

namespace {
class ObservedBackend final : public mr::MockBackend
{
public:
    int creations = 0;
    bool refuseNote = false;
    bool holdNote = false;
    mr::BackendReply *heldNote = nullptr;
    QJsonObject heldPayload;
    QList<QJsonObject> notes;

    void releaseNote()
    {
        auto *pending = std::exchange(heldNote, nullptr);
        if (!pending) return;
        mr::MockBackend::request(QStringLiteral("SetRunReflection"), heldPayload)
            ->whenDone(pending, [pending](bool ok, const QVariantMap &payload,
                                          const QString &code, const QString &message) {
                if (ok) pending->succeed(QJsonObject::fromVariantMap(payload));
                else pending->fail(code, message);
            });
    }

    mr::BackendReply *request(const QString &type, const QJsonObject &payload = {}) override
    {
        if (type == QLatin1String("CreateManualRun")) ++creations;
        if (type == QLatin1String("SetRunReflection")) {
            notes.append(payload);
            if (holdNote) {
                heldPayload = payload;
                heldNote = new mr::BackendReply(QStringLiteral("held-note"), type, this);
                return heldNote;
            }
            if (refuseNote) {
                refuseNote = false;
                auto *reply = new mr::BackendReply(QStringLiteral("refused-note"), type, this);
                QTimer::singleShot(0, reply, [reply] {
                    reply->fail(QStringLiteral("ERR_INTERNAL"), QStringLiteral("note refused"));
                });
                return reply;
            }
        }
        return mr::MockBackend::request(type, payload);
    }
};

struct Scene
{
    ObservedBackend backend;
    mr::AppController controller{&backend, nullptr};
    mr::Formatters fmt;
    mr::JobCatalog jobs;
    mr::RoleCatalog roles;
    mr::RunFormValidator validator;
    QQmlEngine engine;
    std::unique_ptr<QObject> root;
    QString errors;

    bool create()
    {
        engine.rootContext()->setContextProperty(QStringLiteral("App"), &controller);
        engine.rootContext()->setContextProperty(QStringLiteral("Fmt"), &fmt);
        engine.rootContext()->setContextProperty(QStringLiteral("Jobs"), &jobs);
        engine.rootContext()->setContextProperty(QStringLiteral("Roles"), &roles);
        engine.rootContext()->setContextProperty(QStringLiteral("RunForm"), &validator);
        engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 900; height: 800; visible: true
    EditRunDialog {
        id: edit; objectName: "edit"
        onCreateRequested: function(fields, reason) { App.createManualRun(fields, reason) }
    }
    ReflectionDialog { objectName: "reflection"; standalone: true; anchors.centerIn: Overlay.overlay }
    Connections {
        target: App
        function onMutationSucceeded(kind, runId, revision, auditEventId) {
            if (kind === "create") edit.acceptSubmission(runId)
        }
    }
})", QUrl());
        root.reset(component.create());
        errors = component.errorString();
        if (!root) return false;
        // Wait for the same confirmed database identity the visible app gets
        // during startup, rather than changing scope midway through creation.
        if (!QTest::qWaitFor([this] { return !controller.recovery()->databaseKey().isEmpty(); }, 3000))
            return false;
        QMetaObject::invokeMethod(dialog(), "openForCreate");
        dialog()->setProperty("matchedTime", "12:00:00");
        dialog()->setProperty("enteredTime", "12:01:00");
        dialog()->setProperty("endedTime", "12:30:00");
        return true;
    }
    QObject *dialog() const { return root->findChild<QObject *>(QStringLiteral("edit")); }
};

QQuickItem *findItem(QObject *popup, const QString &name)
{
    QList<QQuickItem *> pending{popup->property("contentItem").value<QQuickItem *>()};
    while (!pending.isEmpty()) {
        auto *item = pending.takeFirst();
        if (!item) continue;
        if (item->objectName() == name) return item;
        pending.append(item->childItems());
    }
    return nullptr;
}

class ManualRunReflectionTests final : public QObject
{
    Q_OBJECT
private slots:
    void initTestCase()
    {
        const auto path = QString::fromUtf8(MR_DESKTOP_QML_DIR);
        qmlRegisterSingletonType(QUrl::fromLocalFile(path + QStringLiteral("/Theme.qml")),
                                 "MentorRecorder", 1, 0, "Theme");
        for (const auto *directory : {"/components", "/dialogs"}) {
            const auto folder = path + QString::fromLatin1(directory);
            for (const auto &file : QDir(folder).entryList({QStringLiteral("*.qml")}, QDir::Files)) {
                const auto name = file.chopped(4).toUtf8();
                qmlRegisterType(QUrl::fromLocalFile(folder + QLatin1Char('/') + file),
                                "MentorRecorder", 1, 0, name.constData());
            }
        }
    }

    void optionalReflection_data()
    {
        QTest::addColumn<QString>("mood");
        QTest::addColumn<QString>("text");
        QTest::addColumn<QString>("expected");
        QTest::newRow("good-only") << QStringLiteral("good") << QString() << QString::fromUtf8("顺利");
        QTest::newRow("ok-only") << QStringLiteral("ok") << QString() << QString::fromUtf8("一般");
        QTest::newRow("bad-only") << QStringLiteral("bad") << QString() << QString::fromUtf8("糟心");
        QTest::newRow("text-only") << QString() << QStringLiteral("  note  ") << QStringLiteral("note");
        QTest::newRow("both") << QStringLiteral("bad") << QStringLiteral("my note") << QStringLiteral("my note");
        QTest::newRow("neither") << QString() << QStringLiteral("  ") << QString();
    }
    void optionalReflection()
    {
        QFETCH(QString, mood);
        QFETCH(QString, text);
        QFETCH(QString, expected);
        Scene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QSignalSpy saved(&scene.controller, &mr::AppController::reflectionSaved);
        scene.dialog()->setProperty("reflectionMood", mood);
        scene.dialog()->setProperty("reflectionText", text);
        QVERIFY(QMetaObject::invokeMethod(scene.dialog(), "submit"));
        QTRY_VERIFY2(!scene.dialog()->property("visible").toBool(),
                     qPrintable(scene.dialog()->property("errorText").toString()));
        QCOMPARE(scene.backend.creations, 1);
        QCOMPARE(scene.backend.notes.size(), expected.isEmpty() ? 0 : 1);
        if (!expected.isEmpty()) {
            QCOMPARE(saved.count(), 1);
            QCOMPARE(scene.backend.notes.last().value(QStringLiteral("text")).toString(), expected);
            QCOMPARE(scene.backend.notes.last().value(QStringLiteral("mood")).toString(),
                     mood.isEmpty() ? QStringLiteral("good") : mood);
        }
    }

    void failedReflectionRetriesTheSameRun()
    {
        Scene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        scene.backend.refuseNote = true;
        scene.dialog()->setProperty("reflectionMood", "ok");
        QVERIFY(QMetaObject::invokeMethod(scene.dialog(), "submit"));
        QTRY_VERIFY(scene.dialog()->property("recordSaved").toBool());
        QTRY_VERIFY(!scene.dialog()->property("submitting").toBool());
        QVERIFY(scene.dialog()->property("visible").toBool());
        QVERIFY(scene.dialog()->property("reflectionPending").toBool());
        QVERIFY(!scene.dialog()->property("errorText").toString().isEmpty());
        const auto runId = scene.dialog()->property("savedRunId").toString();
        QVERIFY(!runId.isEmpty());
        QVERIFY(QMetaObject::invokeMethod(scene.dialog(), "submit"));
        QTRY_VERIFY(!scene.dialog()->property("visible").toBool());
        QCOMPARE(scene.backend.creations, 1);
        QCOMPARE(scene.backend.notes.size(), 2);
        for (const auto &note : std::as_const(scene.backend.notes))
            QCOMPARE(note.value(QStringLiteral("run_id")).toString(), runId);
    }

    void changedDatabaseBlocksTheOldRetry()
    {
        Scene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        scene.controller.recovery()->setDatabasePath(QStringLiteral("artifacts/manual-original.db"));
        scene.backend.refuseNote = true;
        scene.dialog()->setProperty("reflectionMood", "bad");
        QVERIFY(QMetaObject::invokeMethod(scene.dialog(), "submit"));
        QTRY_VERIFY(scene.dialog()->property("recordSaved").toBool());
        QTRY_VERIFY(!scene.dialog()->property("submitting").toBool());
        scene.controller.recovery()->setDatabasePath(QStringLiteral("artifacts/manual-other.db"));
        QVERIFY(QMetaObject::invokeMethod(scene.dialog(), "submit"));
        QCOMPARE(scene.backend.notes.size(), 1);
        QCOMPARE(scene.backend.creations, 1);
        QCOMPARE(scene.dialog()->property("errorCode").toString(), QStringLiteral("ERR_RECOVERY_SCOPE_CHANGED"));
        QCOMPARE(scene.dialog()->property("reflectionMood").toString(), QStringLiteral("bad"));
    }

    void anotherEditorsBusyErrorDoesNotReleaseThisSave()
    {
        Scene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        scene.backend.holdNote = true;
        scene.dialog()->setProperty("reflectionMood", "ok");
        QVERIFY(QMetaObject::invokeMethod(scene.dialog(), "submit"));
        QTRY_VERIFY(scene.dialog()->property("reflectionSubmitting").toBool());
        QVERIFY(scene.controller.reflectionSaving());
        scene.controller.saveReflection(QStringLiteral("another-run"), QStringLiteral("good"), QStringLiteral("note"));
        QVERIFY(scene.dialog()->property("submitting").toBool());
        QVERIFY(scene.dialog()->property("reflectionSubmitting").toBool());
        scene.backend.releaseNote();
        QTRY_VERIFY(!scene.dialog()->property("visible").toBool());
        QCOMPARE(scene.backend.creations, 1);
        QCOMPARE(scene.backend.notes.size(), 1);
    }

    void reopeningResetsOptionalReflection()
    {
        Scene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        scene.dialog()->setProperty("reflectionMood", "bad");
        scene.dialog()->setProperty("reflectionText", "unsaved");
        QVERIFY(QMetaObject::invokeMethod(scene.dialog(), "close"));
        QVERIFY(QMetaObject::invokeMethod(scene.dialog(), "openForCreate"));
        QCOMPARE(scene.dialog()->property("reflectionMood").toString(), QString());
        QCOMPARE(scene.dialog()->property("reflectionText").toString(), QString());
    }

    void oversizedReflectionDoesNotCreateARun()
    {
        Scene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        scene.dialog()->setProperty("reflectionText", QString(2001, QLatin1Char('x')));
        QVERIFY(QMetaObject::invokeMethod(scene.dialog(), "submit"));
        QCOMPARE(scene.backend.creations, 0);
        QVERIFY(!scene.dialog()->property("errorText").toString().isEmpty());
    }

    void inlineFieldsRenderAndFollowUserInput()
    {
        Scene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QVERIFY(QMetaObject::invokeMethod(scene.dialog(), "goToStep", Q_ARG(QVariant, 3)));
        auto *window = qobject_cast<QQuickWindow *>(scene.root.get());
        QVERIFY(QTest::qWaitForWindowExposed(window));
        auto *picker = findItem(scene.dialog(), QStringLiteral("manualMoodPicker"));
        auto *editor = findItem(scene.dialog(), QStringLiteral("manualReflectionText"));
        auto *scroll = findItem(scene.dialog(), QStringLiteral("wizardBody"));
        QVERIFY(picker && editor && scroll);
        QTRY_VERIFY(editor->height() > 0 && picker->width() > 0);
        scroll->setProperty("contentY", scroll->property("contentHeight").toReal() - scroll->height());
        QTest::qWait(50);
        QList<QQuickItem *> items{picker};
        QQuickItem *ok = nullptr;
        while (!items.isEmpty()) {
            auto *item = items.takeFirst();
            if (item->property("text").toString() == QString::fromUtf8("一般")
                && item->metaObject()->indexOfSignal("clicked()") >= 0) {
                ok = item;
                break;
            }
            items.append(item->childItems());
        }
        QVERIFY(ok);
        const auto pos = ok->mapToScene(QPointF(ok->width() / 2, ok->height() / 2)).toPoint();
        QVERIFY(QRect(QPoint(), window->size()).contains(pos));
        QTest::mouseClick(window, Qt::LeftButton, Qt::NoModifier, pos);
        QCOMPARE(scene.dialog()->property("reflectionMood").toString(), QStringLiteral("ok"));
        editor->forceActiveFocus();
        for (const auto &character : QStringLiteral("my note"))
            QTest::keyClick(window, character.toLatin1());
        QCOMPARE(scene.dialog()->property("reflectionText").toString(), QStringLiteral("my note"));
        QVERIFY(window->grabWindow().save(QString::fromUtf8(MR_SOURCE_DIR)
                                          + QStringLiteral("/artifacts/manual-entry-form-inline.png")));
    }

    void reminderUsesExistingCategoryArt_data()
    {
        QTest::addColumn<QString>("category");
        for (const auto *category : {"四人迷宫", "讨伐歼灭战", "大型任务", "团队任务", "行会令"})
            QTest::newRow(category) << QString::fromUtf8(category);
    }
    void reminderUsesExistingCategoryArt()
    {
        QFETCH(QString, category);
        Scene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QVERIFY(QMetaObject::invokeMethod(scene.dialog(), "close"));
        auto *popup = scene.root->findChild<QObject *>(QStringLiteral("reflection"));
        const QVariantMap run{{QStringLiteral("run_id"), QStringLiteral("11111111-2222-4333-8444-555555555555")},
                              {QStringLiteral("duty_name"), QString::fromUtf8("究极神兵破坏作战")},
                              {QStringLiteral("duty_category"), category},
                              {QStringLiteral("job_id"), 19},
                              {QStringLiteral("result"), QStringLiteral("COMPLETED")},
                              {QStringLiteral("duration_ms"), 900000}};
        QVERIFY(QMetaObject::invokeMethod(popup, "openForRun", Q_ARG(QVariant, run), Q_ARG(QVariant, QString())));
        auto *icon = findItem(popup, QStringLiteral("reminderCategoryIcon"));
        QVERIFY(icon);
        QTRY_VERIFY(icon->isVisible());
        QCOMPARE(icon->property("category").toString(), category);
        const QString source = scene.jobs.categoryIcon(category);
        QVERIFY(!source.isEmpty());
        QQuickItem *image = nullptr;
        for (auto *item : icon->childItems())
            if (item->property("source").toUrl().toString() == source) image = item;
        QVERIFY(image);
        QTRY_COMPARE(image->property("status").toInt(), 1); // Image.Ready
        QVERIFY(image->isVisible());
        if (category == QString::fromUtf8("讨伐歼灭战")) {
            auto *window = qobject_cast<QQuickWindow *>(scene.root.get());
            QVERIFY(QTest::qWaitForWindowExposed(window));
            QTest::qWait(100);
            QVERIFY(window->grabWindow().save(QString::fromUtf8(MR_SOURCE_DIR)
                                              + QStringLiteral("/artifacts/manual-entry-reminder.png")));
        }
    }
};
} // namespace

MR_DESKTOP_TEST_SUITE(ManualRunReflection)
{
    mrtest::disableCollectorLaunch();
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication app(argc, argv);
    ManualRunReflectionTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "ManualRunReflectionTests.moc"
