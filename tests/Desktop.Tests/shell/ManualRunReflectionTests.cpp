#include "DesktopTestRunner.h"
#include "TestCollectorGuard.h"
#include "AppController.h"
#include "BundledFonts.h"
#include "Formatters.h"
#include "JobCatalog.h"
#include "LanguageManager.h"
#include "MockBackend.h"
#include "RoleCatalog.h"
#include "RunFormValidator.h"

#include <QDir>
#include <QFontDatabase>
#include <QFontInfo>
#include <QGuiApplication>
#include <QImage>
#include <QJSValue>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickStyle>
#include <QQuickItem>
#include <QQuickWindow>
#include <QSignalSpy>
#include <QScopeGuard>
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

    bool create(bool inlinePresentation = false)
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
        inlineContent: workspace.manualContent
        onCreateRequested: function(fields, reason) { App.createManualRun(fields, reason) }
    }
    ReflectionDialog { id: reflection; objectName: "reflection"; standalone: true; anchors.centerIn: Overlay.overlay }
    RecordWorkspacePage {
        id: workspace; anchors.fill: parent
        manualEditor: edit; reflectionEditor: reflection
        visible: edit.inlineActive
    }
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
        dialog()->setProperty("inlinePresentation", inlinePresentation);
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
        for (const auto *directory : {"/components", "/dialogs", "/pages"}) {
            const auto folder = path + QString::fromLatin1(directory);
            for (const auto &file : QDir(folder).entryList({QStringLiteral("*.qml")}, QDir::Files)) {
                const auto name = file.chopped(4).toUtf8();
                qmlRegisterType(QUrl::fromLocalFile(folder + QLatin1Char('/') + file),
                                "MentorRecorder", 1, 0, name.constData());
            }
        }
    }

    // No Popup, TextArea or AppController: request identity and partial-save
    // behavior belong to the submission coordinator itself.
    void coordinatorKeepsUnknownSubmissionIdentity()
    {
        QQmlEngine engine;
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import MentorRecorder
ManualRunSubmission { sessionActive: true }
)", QUrl());
        std::unique_ptr<QObject> session(component.create());
        QVERIFY2(session, qPrintable(component.errorString()));
        QSignalSpy creates(session.get(), SIGNAL(createRequested(QVariant,QString)));
        QSignalSpy completed(session.get(), SIGNAL(completed()));
        QVERIFY(creates.isValid() && completed.isValid());
        QVERIFY(QMetaObject::invokeMethod(session.get(), "resetSession"));
        const QVariantMap fields{{"note", "original"}};
        QVERIFY(QMetaObject::invokeMethod(session.get(), "begin",
                                         Q_ARG(QVariant, true), Q_ARG(QVariant, fields), Q_ARG(QVariant, "reason")));
        const auto ticket = session->property("submissionTicket").toString();
        QVERIFY(!ticket.isEmpty());
        QVERIFY(QMetaObject::invokeMethod(session.get(), "rejectSubmission",
                                         Q_ARG(QVariant, ""), Q_ARG(QVariant, "ERR_INTERNAL"),
                                         Q_ARG(QVariant, "timeout"), Q_ARG(QVariant, false)));
        QVERIFY(session->property("awaitingRetry").toBool());
        QVERIFY(QMetaObject::invokeMethod(session.get(), "resume"));
        QCOMPARE(creates.size(), 2);
        const auto payload = [](const QVariant &value) {
            return value.canConvert<QJSValue>() ? value.value<QJSValue>().toVariant().toMap() : value.toMap();
        };
        QCOMPARE(payload(creates.at(0).at(0)), fields);
        QCOMPARE(payload(creates.at(1).at(0)), fields);
        QCOMPARE(creates.at(0).at(1).toString(), creates.at(1).at(1).toString());
        QCOMPARE(session->property("submissionTicket").toString(), ticket);
        // A failed transport retry cannot erase uncertainty from the first send.
        QVERIFY(QMetaObject::invokeMethod(session.get(), "rejectSubmission",
                                         Q_ARG(QVariant, ""), Q_ARG(QVariant, "ERR_INTERNAL"),
                                         Q_ARG(QVariant, "offline"), Q_ARG(QVariant, true)));
        QVERIFY(session->property("awaitingRetry").toBool());
        QVERIFY(QMetaObject::invokeMethod(session.get(), "resetSession"));
        QVERIFY(QMetaObject::invokeMethod(session.get(), "acceptSubmission", Q_ARG(QVariant, "late-run")));
        QVERIFY(completed.isEmpty());
        QVERIFY(!session->property("recordSaved").toBool());
    }

    void coordinatorRetriesOnlyUnfinishedStages()
    {
        QQmlEngine engine;
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import MentorRecorder
ManualRunSubmission {
    id: session
    sessionActive: true
    hasManualReflection: true
    reflectionMood: "ok"
    reflectionText: "note"
    databaseKey: "db-a"
    identityVerified: true
    imageStore: fakeImages
    property QtObject fakeImages: QtObject {
        property int calls: 0
        property var sent: []
        function commit(runId, adds, removes) {
            sent = sent.concat([adds])
            ++calls
            return calls === 1
                ? {ok: false, added: ["a.png"], removed: [], error: "disk full"}
                : {ok: true, added: adds, removed: [], error: ""}
        }
    }
    function stageImages() { pendingImageAdds = [{path: "a.png"}, {path: "b.png"}] }
    function lastImageCount() { return fakeImages.sent[fakeImages.sent.length - 1].length }
}
)", QUrl());
        std::unique_ptr<QObject> session(component.create());
        QVERIFY2(session, qPrintable(component.errorString()));
        QSignalSpy creates(session.get(), SIGNAL(createRequested(QVariant,QString)));
        QSignalSpy reflections(session.get(), SIGNAL(reflectionRequested(QString,QString,QString)));
        QSignalSpy completed(session.get(), SIGNAL(completed()));
        QVERIFY(creates.isValid() && reflections.isValid() && completed.isValid());
        QVERIFY(QMetaObject::invokeMethod(session.get(), "stageImages"));
        QVERIFY(QMetaObject::invokeMethod(session.get(), "begin", Q_ARG(QVariant, true),
                                         Q_ARG(QVariant, QVariantMap()), Q_ARG(QVariant, "reason")));
        QVERIFY(QMetaObject::invokeMethod(session.get(), "acceptSubmission", Q_ARG(QVariant, "saved-run")));
        QCOMPARE(reflections.size(), 1);
        QVERIFY(QMetaObject::invokeMethod(session.get(), "reflectionFailed",
                                         Q_ARG(QVariant, "ERR_INTERNAL"), Q_ARG(QVariant, "note failed")));
        QVERIFY(QMetaObject::invokeMethod(session.get(), "resume"));
        QCOMPARE(reflections.size(), 2);
        QCOMPARE(creates.size(), 1);
        QVERIFY(QMetaObject::invokeMethod(session.get(), "reflectionSaved", Q_ARG(QVariant, "saved-run")));
        QVERIFY(session->property("recordSaved").toBool());
        QVERIFY(!session->property("reflectionPending").toBool());
        QVERIFY(completed.isEmpty());
        QVERIFY(QMetaObject::invokeMethod(session.get(), "resume"));
        QCOMPARE(creates.size(), 1);
        QCOMPARE(reflections.size(), 2);
        QCOMPARE(completed.size(), 1);
        QVariant lastCount;
        QVERIFY(QMetaObject::invokeMethod(session.get(), "lastImageCount", Q_RETURN_ARG(QVariant, lastCount)));
        QCOMPARE(lastCount.toInt(), 1); // The successful first image is never re-sent.
    }

    void optionalReflection_data()
    {
        QTest::addColumn<QString>("mood");
        QTest::addColumn<QString>("text");
        QTest::addColumn<QString>("expected");
        QTest::newRow("good-only") << QStringLiteral("good") << QString() << QString::fromUtf8("顺利");
        QTest::newRow("ok-only") << QStringLiteral("ok") << QString() << QString::fromUtf8("一般");
        QTest::newRow("bad-only") << QStringLiteral("bad") << QString() << QString::fromUtf8("糟心");
        QTest::newRow("both") << QStringLiteral("bad") << QStringLiteral("  my note  ") << QStringLiteral("my note");
        QTest::newRow("neither") << QString() << QStringLiteral("  ") << QString();
    }

    void repeatedLanguageSwitchesKeepManualEditorReadable_data()
    {
        QTest::addColumn<bool>("inlinePresentation");
        QTest::newRow("dialog") << false;
        QTest::newRow("page") << true;
    }
    void repeatedLanguageSwitchesKeepManualEditorReadable()
    {
        QFETCH(bool, inlinePresentation);
        QVERIFY(mr::registerBundledFonts());
        const QFont savedFont = QGuiApplication::font();
        const QLocale savedLocale;
        const auto savedRenderType = QQuickWindow::textRenderType();
        mr::LanguageManager language(nullptr);
        const auto restore = qScopeGuard([&] {
            language.setLanguage("zh-CN");
            QGuiApplication::setFont(savedFont);
            QLocale::setDefault(savedLocale);
            QQuickWindow::setTextRenderType(savedRenderType);
        });
        QQuickWindow::setTextRenderType(QQuickWindow::NativeTextRendering);
        language.initialize("zh-CN");
        Scene scene;
        scene.engine.rootContext()->setContextProperty("Lang", &language);
        language.attachEngine(&scene.engine);
        QVERIFY2(scene.create(inlinePresentation), qPrintable(scene.errors));
        auto *window = qobject_cast<QQuickWindow *>(scene.root.get());
        QVERIFY(QTest::qWaitForWindowExposed(window));
        QTRY_VERIFY(scene.dialog()->property("editingActive").toBool());
        scene.dialog()->setProperty("reflectionMood", QStringLiteral("ok"));
        const QString note = QStringLiteral("保留这次输入的心得：日本語 / English 123");
        scene.dialog()->setProperty("reflectionText", note);
        scene.dialog()->setProperty("currentStep", 3);
        auto *form = scene.dialog()->property(inlinePresentation ? "inlineContent" : "contentItem").value<QQuickItem *>();
        QVERIFY(form);
        auto *input = form->findChild<QQuickItem *>(QStringLiteral("manualReflectionText"));
        QVERIFY(input && input->isVisible());

        QQuickItem *title = nullptr;
        for (auto *item : scene.root->findChildren<QQuickItem *>())
            if (item->isVisible() && item->property("text").toString() == QStringLiteral("新增遗漏记录"))
                title = item;
        QVERIFY(title);
        const auto titleImage = [&] {
            const qreal dpr = window->devicePixelRatio();
            const QRectF rect = title->mapRectToScene(title->boundingRect());
            return window->grabWindow().copy(QRect(qFloor(rect.x() * dpr), qFloor(rect.y() * dpr),
                                                   qCeil(rect.width() * dpr), qCeil(rect.height() * dpr)));
        };
        QTest::qWait(100);
        const QImage initial = titleImage();
        QVERIFY(!initial.isNull());
        QSignalSpy databaseChanged(qGuiApp, &QGuiApplication::fontDatabaseChanged);
        for (int round = 0; round < 4; ++round) {
            for (const QString &code : {QStringLiteral("zh-TW"), QStringLiteral("ja"),
                                       QStringLiteral("en"), QStringLiteral("zh-CN")}) {
                QSignalSpy frames(window, &QQuickWindow::frameSwapped);
                language.setLanguage(code);
                window->requestUpdate();
                QVERIFY(QTest::qWaitFor([&] { return !frames.isEmpty(); }));
                QTRY_COMPARE(scene.dialog()->property("font").value<QFont>().family(),
                             QGuiApplication::font().family());
                QTRY_COMPARE(input->property("font").value<QFont>().family(),
                             QGuiApplication::font().family());
                QCOMPARE(input->property("text").toString(), note);
                QCOMPARE(title->property("text").toString(),
                         QCoreApplication::translate("EditRunDialog", "新增遗漏记录"));
                const QImage rendered = titleImage();
                QVERIFY(!rendered.isNull());
                bool hasInk = false;
                const QColor background = rendered.pixelColor(0, 0);
                for (int y = 0; y < rendered.height() && !hasInk; ++y)
                    for (int x = 0; x < rendered.width(); ++x)
                        hasInk |= rendered.pixelColor(x, y) != background;
                QVERIFY2(hasInk, qPrintable(code));
                QCOMPARE(scene.dialog()->property("reflectionText").toString(), note);
                QCOMPARE(scene.dialog()->property("reflectionMood").toString(), QStringLiteral("ok"));
                QVERIFY(scene.dialog()->property("editingActive").toBool());
                QCOMPARE(scene.dialog()->property("visible").toBool(), !inlinePresentation);
            }
            QTRY_COMPARE(titleImage(), initial);
        }
        QCOMPARE(databaseChanged.count(), 0);
        QCOMPARE(scene.backend.creations, 0);
        const QString screenshot = qEnvironmentVariable("MR_MANUAL_LANGUAGE_SCREENSHOT");
        if (!screenshot.isEmpty())
            QVERIFY(window->grabWindow().save(screenshot + (inlinePresentation ? "-page.png" : "-dialog.png")));
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
            QCOMPARE(scene.backend.notes.last().value(QStringLiteral("mood")).toString(), mood);
        }
    }

    void inlineWorkspaceKeepsInputAcrossNavigation()
    {
        QVERIFY(mr::registerBundledFonts());
        Scene scene;
        QVERIFY2(scene.create(true), qPrintable(scene.errors));
        auto *page = scene.root->findChild<QQuickItem *>(QStringLiteral("recordWorkspacePage"));
        QVERIFY(page);
        QVERIFY(page->isVisible());
        QVERIFY(!scene.dialog()->property("visible").toBool());
        QVERIFY(scene.dialog()->property("inlineActive").toBool());
        auto *body = findItem(page, QStringLiteral("manualRecordPageContent"));
        // The body is in the page's visual ancestry, not in Overlay.overlay.
        if (!body) body = page->findChild<QQuickItem *>(QStringLiteral("manualRecordPageContent"));
        QVERIFY(body);
        bool belongsToPage = false;
        for (auto *parent = body->parentItem(); parent; parent = parent->parentItem())
            belongsToPage |= parent == page;
        QVERIFY(belongsToPage);
        scene.dialog()->setProperty("reflectionMood", QStringLiteral("good"));
        const QString text = QStringLiteral("这次配合很顺利，下一次提前提醒机制。");
        scene.dialog()->setProperty("reflectionText", text);
        scene.dialog()->setProperty("currentStep", 3);
        page->setVisible(false);
        QCoreApplication::processEvents();
        page->setVisible(true);
        QCOMPARE(scene.dialog()->property("reflectionText").toString(), text);
        QCOMPARE(scene.dialog()->property("reflectionMood").toString(), QStringLiteral("good"));
        QCOMPARE(scene.dialog()->property("currentStep").toInt(), 3);
        QCOMPARE(scene.backend.creations, 0);
        auto *window = qobject_cast<QQuickWindow *>(scene.root.get());
        window->resize(1100, 720);
        QVERIFY(QTest::qWaitForWindowExposed(window));
        QTest::qWait(100);
        const QString screenshot = qEnvironmentVariable("MR_MANUAL_WORKSPACE_SCREENSHOT");
        if (!screenshot.isEmpty()) QVERIFY(window->grabWindow().save(screenshot));
        QVERIFY(QMetaObject::invokeMethod(scene.dialog(), "closeEditor"));
        QVERIFY(!scene.dialog()->property("editingActive").toBool());
        QCOMPARE(scene.backend.creations, 0);
    }

    // Audit 2026-11-03 L1-4: a 心得 written with the mood left at 「不填写」 was
    // stored as 顺利, a mood nobody chose. A manual record cannot store a 心得
    // without a mood (only imported ones may), so the wizard asks for one
    // before anything is created.
    void textWithoutAMoodAsksForOneBeforeCreating()
    {
        Scene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        scene.dialog()->setProperty("reflectionMood", QString());
        scene.dialog()->setProperty("reflectionText", QStringLiteral("  note  "));
        QVERIFY(QMetaObject::invokeMethod(scene.dialog(), "submit"));
        QCOMPARE(scene.backend.creations, 0);
        QVERIFY(scene.backend.notes.isEmpty());
        QVERIFY(scene.dialog()->property("visible").toBool());
        QVERIFY(scene.dialog()->property("errorText").toString().contains(QString::fromUtf8("这次感觉")));

        scene.dialog()->setProperty("reflectionMood", QStringLiteral("ok"));
        QVERIFY(QMetaObject::invokeMethod(scene.dialog(), "submit"));
        QTRY_VERIFY2(!scene.dialog()->property("visible").toBool(),
                     qPrintable(scene.dialog()->property("errorText").toString()));
        QCOMPARE(scene.backend.creations, 1);
        QCOMPARE(scene.backend.notes.size(), 1);
        QCOMPARE(scene.backend.notes.last().value(QStringLiteral("mood")).toString(), QStringLiteral("ok"));
        QCOMPARE(scene.backend.notes.last().value(QStringLiteral("text")).toString(), QStringLiteral("note"));
    }

    void failedReflectionRetriesTheSameRun_data()
    {
        QTest::addColumn<bool>("inlinePresentation");
        QTest::newRow("dialog") << false;
        QTest::newRow("page") << true;
    }
    void failedReflectionRetriesTheSameRun()
    {
        QFETCH(bool, inlinePresentation);
        Scene scene;
        QVERIFY2(scene.create(inlinePresentation), qPrintable(scene.errors));
        scene.backend.refuseNote = true;
        scene.dialog()->setProperty("reflectionMood", "ok");
        QVERIFY(QMetaObject::invokeMethod(scene.dialog(), "submit"));
        QTRY_VERIFY(scene.dialog()->property("recordSaved").toBool());
        QTRY_VERIFY(!scene.dialog()->property("submitting").toBool());
        QVERIFY(scene.dialog()->property("editingActive").toBool());
        QCOMPARE(scene.dialog()->property("visible").toBool(), !inlinePresentation);
        QVERIFY(scene.dialog()->property("reflectionPending").toBool());
        QVERIFY(!scene.dialog()->property("errorText").toString().isEmpty());
        const auto runId = scene.dialog()->property("savedRunId").toString();
        QVERIFY(!runId.isEmpty());
        QVERIFY(QMetaObject::invokeMethod(scene.dialog(), "submit"));
        QTRY_VERIFY(!scene.dialog()->property("editingActive").toBool());
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
