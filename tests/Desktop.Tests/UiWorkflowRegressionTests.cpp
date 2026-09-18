#include "TestCollectorGuard.h"
#include "AppController.h"
#include "AppSettings.h"
#include "Formatters.h"
#include "IBackend.h"
#include "JobCatalog.h"
#include "MockBackend.h"
#include "RoleCatalog.h"
#include "RunFormValidator.h"
#include "RunListModel.h"
#include "StatsModels.h"

#include <QDateTime>
#include <QDir>
#include <QGuiApplication>
#include <QJsonArray>
#include <QJsonDocument>
#include <QPointer>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QScopeGuard>
#include <QSignalSpy>
#include <QStandardPaths>
#include <QTest>
#include <QTimer>

#include <algorithm>
#include <functional>
#include <memory>

namespace {

// Keep mutation replies under the test's control. Read requests finish normally,
// and the mock name prevents AppController from launching a real Collector.
class WorkflowBackend final : public mr::IBackend
{
public:
    struct Pending {
        QString type;
        QJsonObject payload;
        QPointer<mr::BackendReply> reply;
    };

    QString backendName() const override { return QStringLiteral("mock"); }
    bool isConnected() const override { return true; }

    mr::BackendReply *request(const QString &type, const QJsonObject &payload = {}) override
    {
        ++counts[type];
        lastPayloads.insert(type, payload);
        auto *reply = new mr::BackendReply(QString::number(++serial), type, this);
        if (type == QLatin1String("SetRunReflection") || type == QLatin1String("CorrectRun")) {
            pending.append({type, payload, reply});
        } else if (failures.contains(type)) {
            const QString code = failures.value(type);
            QTimer::singleShot(0, reply, [reply, code] {
                reply->fail(code, QStringLiteral("fixture refusal"));
            });
        } else {
            const QJsonObject answer = answers.value(type);
            QTimer::singleShot(0, reply, [reply, answer] { reply->succeed(answer); });
        }
        return reply;
    }

    bool finish(const QString &type, bool success)
    {
        for (int index = 0; index < pending.size(); ++index) {
            if (pending[index].type != type || !pending[index].reply)
                continue;
            const Pending request = pending.takeAt(index);
            if (!success) {
                request.reply->fail(QStringLiteral("ERR_BAD_REQUEST"),
                                    QStringLiteral("fixture refusal"));
                return true;
            }
            const QString id = request.payload.value(QStringLiteral("run_id")).toString();
            QJsonObject run{{QStringLiteral("run_id"), id},
                            {QStringLiteral("duty_name"), QStringLiteral("fixture duty")},
                            {QStringLiteral("revision"), 2}};
            if (type == QLatin1String("SetRunReflection")) {
                run.insert(QStringLiteral("reflection"), QJsonObject{
                    {QStringLiteral("mood"), request.payload.value(QStringLiteral("mood"))},
                    {QStringLiteral("text"), request.payload.value(QStringLiteral("text"))}});
            }
            request.reply->succeed({{QStringLiteral("run_id"), id},
                                   {QStringLiteral("run"), run},
                                   {QStringLiteral("revision"), 2},
                                   {QStringLiteral("audit_event_id"), QStringLiteral("fixture audit")}});
            return true;
        }
        return false;
    }

    QHash<QString, int> counts;
    QHash<QString, QJsonObject> lastPayloads;
    /// What an ordinary (non-pending) request answers with; {} by default.
    QHash<QString, QJsonObject> answers;
    /// Message types that are refused, with the error code.
    QHash<QString, QString> failures;
    QList<Pending> pending;
    int serial = 0;
};

// IpcClient fails before returning from request() while disconnected. Also
// answer synchronously on success so the complete reply contract is exercised.
class ImmediateBackend final : public mr::IBackend
{
public:
    QString backendName() const override { return QStringLiteral("mock"); }
    bool isConnected() const override { return !fail; }
    mr::BackendReply *request(const QString &type, const QJsonObject & = {}) override
    {
        auto *reply = new mr::BackendReply(QStringLiteral("immediate"), type, this);
        if (fail) {
            reply->fail(QStringLiteral("ERR_INTERNAL"), QStringLiteral("Collector disconnected"));
        } else {
            reply->succeed({{QStringLiteral("items"), QJsonArray{QJsonObject{
                                {QStringLiteral("run_id"), QStringLiteral("fixture")}}}},
                            {QStringLiteral("page_info"), QJsonObject{
                                {QStringLiteral("total"), 1}, {QStringLiteral("page"), 1}}}});
        }
        return reply;
    }
    bool fail = false;
};

// SettingsPage reads Tts.available / statusText / voices / voiceId and calls
// setVoice and preview; a silent engine is enough.
class TtsStub final : public QObject
{
    Q_OBJECT
    Q_PROPERTY(bool available MEMBER m_available NOTIFY changed)
    Q_PROPERTY(QString statusText MEMBER m_statusText NOTIFY changed)
    Q_PROPERTY(QVariantList voices MEMBER m_voices NOTIFY changed)
    Q_PROPERTY(QString voiceId MEMBER m_voiceId NOTIFY changed)

public:
    Q_INVOKABLE void setVoice(const QString &id)
    {
        requestedVoices.append(id);
        m_voiceId = id;
        Q_EMIT changed();
    }
    Q_INVOKABLE void preview(const QString &kind) { previews.append(kind); }

    void setVoices(const QVariantList &voices, const QString &current)
    {
        m_available = true;
        m_voices = voices;
        m_voiceId = current;
        Q_EMIT changed();
    }

    QStringList requestedVoices;
    QStringList previews;

Q_SIGNALS:
    void changed();

private:
    bool m_available = false;
    QString m_statusText = QStringLiteral("stub engine");
    QVariantList m_voices;
    QString m_voiceId;
};

QVariantMap voiceRow(const QString &name, const QString &label, const QString &locale)
{
    return {{QStringLiteral("id"), QStringLiteral("local:") + name},
            {QStringLiteral("name"), name},
            {QStringLiteral("label"), label},
            {QStringLiteral("locale"), locale},
            {QStringLiteral("provider"), QStringLiteral("local")},
            {QStringLiteral("group"), QStringLiteral("local")}};
}

QVariantMap onlineRow(const QString &id, const QString &label)
{
    return {{QStringLiteral("id"), id},
            {QStringLiteral("name"), id.section(QLatin1Char(':'), 1)},
            {QStringLiteral("label"), label},
            {QStringLiteral("locale"), QStringLiteral("zh-CN")},
            {QStringLiteral("provider"), id.section(QLatin1Char(':'), 0, 0)},
            {QStringLiteral("group"), QStringLiteral("online")}};
}

QJsonObject speechSettingsAnswer(bool hasKey)
{
    return {{QStringLiteral("provider"), QStringLiteral("azure")},
            {QStringLiteral("azure_region"), QStringLiteral("eastasia")},
            {QStringLiteral("openai_base_url"), QStringLiteral("https://api.example.com/v1")},
            {QStringLiteral("openai_model"), QStringLiteral("gpt-4o-mini-tts")},
            {QStringLiteral("voice"), QStringLiteral("zh-CN-XiaoxiaoNeural")},
            {QStringLiteral("has_key"), hasKey},
            {QStringLiteral("configured"), hasKey},
            {QStringLiteral("target_host"), QStringLiteral("eastasia.azure-speech.invalid")},
            {QStringLiteral("azure_voices"), QJsonArray{QJsonObject{
                 {QStringLiteral("name"), QStringLiteral("zh-CN-XiaoxiaoNeural")},
                 {QStringLiteral("label"), QString::fromUtf8("晓晓（女声）")}}}},
            {QStringLiteral("openai_voices"), QJsonArray{QJsonObject{
                 {QStringLiteral("name"), QStringLiteral("alloy")},
                 {QStringLiteral("label"), QStringLiteral("alloy")}}}}};
}

QVariantList speechVoiceRows()
{
    return {voiceRow(QStringLiteral("Microsoft Huihui Desktop"),
                     QString::fromUtf8("Microsoft Huihui Desktop（中文 · 女声）"), QStringLiteral("zh-CN")),
            onlineRow(QStringLiteral("azure:zh-CN-XiaoxiaoNeural"),
                      QString::fromUtf8("晓晓（女声） · 在线（Azure）")),
            onlineRow(QStringLiteral("openai:alloy"), QString::fromUtf8("alloy · 在线（OpenAI 兼容）"))};
}

// The whole settings page with its real sub-pages, a real AppController on a
// scripted backend, persisted settings and a Tts stub.
struct SettingsFixture {
    mr::AppSettings settings;
    TtsStub tts;
    WorkflowBackend backend;
    mr::AppController controller{&backend, nullptr};
    mr::Formatters formatters;
    // An open combo box popup reads them (StyledComboBox's job icons).
    mr::JobCatalog jobs;
    mr::RoleCatalog roles;
    QQmlEngine engine;
    std::unique_ptr<QObject> root;
    QString errors;

    bool create(const QString &style = QString())
    {
        const auto context = engine.rootContext();
        context->setContextProperty(QStringLiteral("App"), &controller);
        context->setContextProperty(QStringLiteral("Jobs"), &jobs);
        context->setContextProperty(QStringLiteral("Roles"), &roles);
        context->setContextProperty(QStringLiteral("Fmt"), &formatters);
        context->setContextProperty(QStringLiteral("Settings"), &settings);
        context->setContextProperty(QStringLiteral("Tts"), &tts);
        context->setContextProperty(QStringLiteral("ForceUiStyle"), style);
        context->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 1280; height: 800; visible: true
    SettingsPage { objectName: "settingsPage"; anchors.fill: parent }
})", QUrl());
        root.reset(component.create());
        for (const auto &error : component.errors())
            errors += error.toString() + QLatin1Char('\n');
        return root != nullptr;
    }

    QObject *page() const { return item(QStringLiteral("settingsPage")); }
    // Repeater delegates are not QObject children of the window, so the visual
    // tree is searched, the same way main.cpp's self-check does.
    static QQuickItem *find(QQuickItem *from, const QString &name)
    {
        if (!from)
            return nullptr;
        if (from->objectName() == name)
            return from;
        for (QQuickItem *child : from->childItems()) {
            if (QQuickItem *match = find(child, name))
                return match;
        }
        return nullptr;
    }
    QQuickItem *item(const QString &name) const
    {
        const auto *window = qobject_cast<QQuickWindow *>(root.get());
        return window ? find(window->contentItem(), name) : nullptr;
    }
    bool selectTab(const QString &id) const
    {
        QObject *nav = item(QStringLiteral("settingsTab_") + id);
        return nav && QMetaObject::invokeMethod(nav, "clicked");
    }
};

QVariantMap run(const QString &id = QStringLiteral("fixture-run"))
{
    return {{QStringLiteral("run_id"), id},
            {QStringLiteral("revision"), 1},
            {QStringLiteral("duty_name"), QStringLiteral("fixture duty")}};
}

struct UiFixture {
    WorkflowBackend backend;
    mr::AppController controller{&backend, nullptr};
    mr::Formatters formatters;
    mr::JobCatalog jobs;
    mr::RoleCatalog roles;
    mr::RunFormValidator validator;
    QQmlEngine engine;
    std::unique_ptr<QObject> root;
    QString errors;

    bool create()
    {
        engine.rootContext()->setContextProperty(QStringLiteral("App"), &controller);
        engine.rootContext()->setContextProperty(QStringLiteral("Fmt"), &formatters);
        engine.rootContext()->setContextProperty(QStringLiteral("Jobs"), &jobs);
        engine.rootContext()->setContextProperty(QStringLiteral("Roles"), &roles);
        engine.rootContext()->setContextProperty(QStringLiteral("RunForm"), &validator);
        engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 1100; height: 720; visible: true
    HistoryPage { objectName: "history"; anchors.fill: parent }
    ReflectionDialog { objectName: "reflection" }
})", QUrl());
        root.reset(component.create());
        for (const auto &error : component.errors())
            errors += error.toString() + QLatin1Char('\n');
        return root != nullptr;
    }

    QObject *dialog() const { return root->findChild<QObject *>(QStringLiteral("reflection")); }
    QObject *textArea() const { return dialog()->findChild<QObject *>(QStringLiteral("reflectionTextArea")); }
    QObject *history() const { return root->findChild<QObject *>(QStringLiteral("history")); }
    bool openResult(const QVariantMap &value = run())
    {
        return QMetaObject::invokeMethod(dialog(), "openForResult", Q_ARG(QVariant, QVariant(value)));
    }
    bool resolve()
    {
        return QMetaObject::invokeMethod(dialog(), "resolveWith",
                                        Q_ARG(QVariant, QVariant(QStringLiteral("COMPLETED"))),
                                        Q_ARG(QVariant, QVariant(QStringLiteral("confirmed"))));
    }
};

} // namespace

class UiWorkflowRegressionTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void initTestCase();
    void immediateModelFailureClearsLoadingAndOldRows();
    void reflectionFailurePreservesTextAndDoesNotConfirmResult();
    void resultFailureAfterSavedReflectionCanBeRetried();
    void resultWithoutTextNeedsNoReflectionWrite();
    void missingJobIsSavedWithTheResultAndRetainedAfterFailure();
    void completedRunCanSupplementJobWithoutAReflection();
    void supplementingJobAlsoKeepsAnExplicitReflectionClear();
    void pendingReflectionLocksEscapeAndRunSelection();
    void ordinaryReflectionSaveStillWaitsForItsReply();
    void unrelatedReflectionReplyDoesNotCloseAnIdleDialog();
    void historyMirrorsEveryFilterAndClearsStaleControls();
    void externalHistoryFilterCancelsPendingDebounce();
    void mockCorrectionAcknowledgesOnlyExplicitOutcome_data();
    void mockCorrectionAcknowledgesOnlyExplicitOutcome();
    void hoverFadeRestsOnTheHoverColour_data();
    void hoverFadeRestsOnTheHoverColour();
    void styleSwitchShowsTheStyleOnScreen_data();
    void styleSwitchShowsTheStyleOnScreen();
    void settingsTabsShowTheirOwnControls_data();
    void settingsTabsShowTheirOwnControls();
    void integrityButtonShowsTheCollectorsAnswer_data();
    void integrityButtonShowsTheCollectorsAnswer();
    void voiceComboListsTheVoicesAndSelectsOne();
    void onlinePanelAppearsOnlyForOnlineVoices();
    void onlineKeyFieldIsNeverFilledAndEmptiesOnSave();
    void onlineConfirmationIsAskedOnceAndCancelKeepsTheVoice();
};

void UiWorkflowRegressionTests::initTestCase()
{
    const QString qmlRoot = QString::fromUtf8(MR_DESKTOP_QML_DIR);
    QVERIFY(QDir(qmlRoot).exists());
    qmlRegisterSingletonType(QUrl::fromLocalFile(qmlRoot + QStringLiteral("/Theme.qml")),
                             "MentorRecorder", 1, 0, "Theme");
    for (const auto &directory : {QStringLiteral("/components"), QStringLiteral("/dialogs"),
                                  QStringLiteral("/pages")}) {
        for (const auto &file : QDir(qmlRoot + directory).entryList({QStringLiteral("*.qml")}, QDir::Files)) {
            const QByteArray name = file.chopped(4).toUtf8();
            qmlRegisterType(QUrl::fromLocalFile(qmlRoot + directory + QLatin1Char('/') + file),
                            "MentorRecorder", 1, 0, name.constData());
        }
    }
}

void UiWorkflowRegressionTests::immediateModelFailureClearsLoadingAndOldRows()
{
    ImmediateBackend backend;
    mr::RunListModel runs;
    mr::DungeonStatsModel dungeons;
    mr::JobStatsModel jobs;
    runs.setBackend(&backend);
    dungeons.setBackend(&backend);
    jobs.setBackend(&backend);
    QSignalSpy runFailures(&runs, &mr::RunListModel::loadFailed);
    QSignalSpy dungeonFailures(&dungeons, &mr::StatsRowsModel::loadFailed);
    QSignalSpy jobFailures(&jobs, &mr::StatsRowsModel::loadFailed);
    runs.reload();
    dungeons.reload();
    jobs.reload();
    QCOMPARE(runs.rowCount(), 1);
    QCOMPARE(dungeons.rowCount(), 1);
    QCOMPARE(jobs.rowCount(), 1);
    backend.fail = true;
    runs.reload();
    dungeons.reload();
    jobs.reload();
    QVERIFY(!runs.isLoading());
    QVERIFY(!dungeons.isLoading());
    QVERIFY(!jobs.isLoading());
    QCOMPARE(runs.rowCount(), 0);
    QCOMPARE(dungeons.rowCount(), 0);
    QCOMPARE(jobs.rowCount(), 0);
    QCOMPARE(runFailures.count(), 1);
    QCOMPARE(dungeonFailures.count(), 1);
    QCOMPARE(jobFailures.count(), 1);
    QCoreApplication::processEvents();
    QCOMPARE(runFailures.count(), 1);
    QCOMPARE(dungeonFailures.count(), 1);
    QCOMPARE(jobFailures.count(), 1);
}

void UiWorkflowRegressionTests::reflectionFailurePreservesTextAndDoesNotConfirmResult()
{
    UiFixture fixture;
    QVERIFY2(fixture.create(), qPrintable(fixture.errors));
    QVERIFY(fixture.openResult());
    QTRY_VERIFY(fixture.dialog()->property("visible").toBool());
    const QString overLimit(2100, QLatin1Char('x'));
    fixture.textArea()->setProperty("text", overLimit);
    QVERIFY(fixture.resolve());
    QCOMPARE(fixture.backend.counts.value(QStringLiteral("SetRunReflection")), 1);
    QCOMPARE(fixture.backend.counts.value(QStringLiteral("CorrectRun")), 0);
    QVERIFY(fixture.backend.finish(QStringLiteral("SetRunReflection"), false));
    QVERIFY(fixture.dialog()->property("visible").toBool());
    QVERIFY(!fixture.dialog()->property("busy").toBool());
    QVERIFY(!fixture.dialog()->property("errorText").toString().isEmpty());
    QCOMPARE(fixture.textArea()->property("text").toString(), overLimit);
    QCOMPARE(fixture.backend.counts.value(QStringLiteral("CorrectRun")), 0);
    fixture.textArea()->setProperty("text", QStringLiteral("shortened reflection"));
    QVERIFY(fixture.resolve());
    QVERIFY(fixture.backend.finish(QStringLiteral("SetRunReflection"), true));
    QCOMPARE(fixture.backend.counts.value(QStringLiteral("CorrectRun")), 1);
    QVERIFY(fixture.backend.finish(QStringLiteral("CorrectRun"), true));
    QTRY_VERIFY(!fixture.dialog()->property("visible").toBool());
}

void UiWorkflowRegressionTests::resultFailureAfterSavedReflectionCanBeRetried()
{
    UiFixture fixture;
    QVERIFY2(fixture.create(), qPrintable(fixture.errors));
    QVERIFY(fixture.openResult());
    QTRY_VERIFY(fixture.dialog()->property("visible").toBool());
    fixture.textArea()->setProperty("text", QStringLiteral("keep this reflection"));
    QVERIFY(fixture.resolve());
    QVERIFY(fixture.backend.finish(QStringLiteral("SetRunReflection"), true));
    QCOMPARE(fixture.backend.counts.value(QStringLiteral("CorrectRun")), 1);
    QVERIFY(fixture.dialog()->property("visible").toBool());
    QVERIFY(fixture.backend.finish(QStringLiteral("CorrectRun"), false));
    QVERIFY(fixture.dialog()->property("visible").toBool());
    QVERIFY(!fixture.dialog()->property("busy").toBool());
    QCOMPARE(fixture.textArea()->property("text").toString(), QStringLiteral("keep this reflection"));
    QVERIFY(fixture.resolve());
    QCOMPARE(fixture.backend.counts.value(QStringLiteral("CorrectRun")), 1);
    QVERIFY(fixture.backend.finish(QStringLiteral("SetRunReflection"), true));
    QCOMPARE(fixture.backend.counts.value(QStringLiteral("CorrectRun")), 2);
    QVERIFY(fixture.backend.finish(QStringLiteral("CorrectRun"), true));
    QTRY_VERIFY(!fixture.dialog()->property("visible").toBool());
}

void UiWorkflowRegressionTests::missingJobIsSavedWithTheResultAndRetainedAfterFailure()
{
    UiFixture fixture;
    QVERIFY2(fixture.create(), qPrintable(fixture.errors));
    QVERIFY(fixture.openResult());
    QVERIFY(fixture.dialog()->property("needsJob").toBool());
    fixture.dialog()->setProperty("jobIndex", 1);
    const int jobId = fixture.dialog()->property("selectedJobId").toInt();
    QVERIFY(jobId > 0);
    QVERIFY(fixture.resolve());
    QCOMPARE(fixture.backend.counts.value(QStringLiteral("SetRunReflection")), 0);
    QCOMPARE(fixture.backend.pending.last().payload.value(QStringLiteral("changes")).toObject(),
             (QJsonObject{{QStringLiteral("result"), QStringLiteral("COMPLETED")},
                          {QStringLiteral("job_id"), jobId}}));
    QVERIFY(fixture.backend.finish(QStringLiteral("CorrectRun"), false));
    QVERIFY(fixture.dialog()->property("visible").toBool());
    QCOMPARE(fixture.dialog()->property("selectedJobId").toInt(), jobId);
    QVERIFY(fixture.resolve());
    QCOMPARE(fixture.backend.pending.last().payload.value(QStringLiteral("changes")).toObject()
                 .value(QStringLiteral("job_id")).toInt(), jobId);
    QVERIFY(fixture.backend.finish(QStringLiteral("CorrectRun"), true));
    QTRY_VERIFY(!fixture.dialog()->property("visible").toBool());

    auto known = run(QStringLiteral("known-job"));
    known.insert(QStringLiteral("job_id"), 24);
    QVERIFY(fixture.openResult(known));
    QVERIFY(!fixture.dialog()->property("needsJob").toBool());
    QCOMPARE(fixture.dialog()->property("selectedJobId").toInt(), 0);
    QVERIFY(fixture.openResult(run(QStringLiteral("another-run"))));
    QCOMPARE(fixture.dialog()->property("jobIndex").toInt(), 0);
}

void UiWorkflowRegressionTests::completedRunCanSupplementJobWithoutAReflection()
{
    UiFixture fixture;
    QVERIFY2(fixture.create(), qPrintable(fixture.errors));
    auto value = run();
    value.insert(QStringLiteral("result"), QStringLiteral("COMPLETED"));
    QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "openForRun",
                                     Q_ARG(QVariant, QVariant(value)),
                                     Q_ARG(QVariant, QVariant(QStringLiteral("刚刚完成")))));
    fixture.dialog()->setProperty("jobIndex", 1);
    auto *save = fixture.dialog()->findChild<QObject *>(QStringLiteral("saveReflectionButton"));
    QVERIFY(save);
    QVERIFY(QMetaObject::invokeMethod(save, "clicked"));
    QCOMPARE(fixture.backend.counts.value(QStringLiteral("SetRunReflection")), 0);
    QCOMPARE(fixture.backend.counts.value(QStringLiteral("CorrectRun")), 1);
    QVERIFY(fixture.backend.pending.last().payload.value(QStringLiteral("changes")).toObject()
                .value(QStringLiteral("job_id")).toInt() > 0);
    QVERIFY(fixture.backend.finish(QStringLiteral("CorrectRun"), true));
    QTRY_VERIFY(!fixture.dialog()->property("visible").toBool());
}

void UiWorkflowRegressionTests::supplementingJobAlsoKeepsAnExplicitReflectionClear()
{
    UiFixture fixture;
    QVERIFY2(fixture.create(), qPrintable(fixture.errors));
    auto value = run();
    value.insert(QStringLiteral("result"), QStringLiteral("COMPLETED"));
    value.insert(QStringLiteral("reflection"), QVariantMap{{QStringLiteral("text"), QStringLiteral("old note")}});
    QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "openForRun",
                                     Q_ARG(QVariant, QVariant(value)), Q_ARG(QVariant, QVariant(QString()))));
    fixture.dialog()->setProperty("jobIndex", 1);
    fixture.textArea()->setProperty("text", QString());
    auto *save = fixture.dialog()->findChild<QObject *>(QStringLiteral("saveReflectionButton"));
    QVERIFY(save);
    QVERIFY(QMetaObject::invokeMethod(save, "clicked"));
    QCOMPARE(fixture.backend.counts.value(QStringLiteral("SetRunReflection")), 1);
    QCOMPARE(fixture.backend.counts.value(QStringLiteral("CorrectRun")), 0);
    QVERIFY(fixture.backend.pending.last().payload.value(QStringLiteral("text")).toString().isEmpty());
    QVERIFY(fixture.backend.finish(QStringLiteral("SetRunReflection"), true));
    QCOMPARE(fixture.backend.counts.value(QStringLiteral("CorrectRun")), 1);
    QVERIFY(fixture.backend.finish(QStringLiteral("CorrectRun"), true));
    QTRY_VERIFY(!fixture.dialog()->property("visible").toBool());
}

void UiWorkflowRegressionTests::resultWithoutTextNeedsNoReflectionWrite()
{
    UiFixture fixture;
    QVERIFY2(fixture.create(), qPrintable(fixture.errors));
    QVERIFY(fixture.openResult());
    QTRY_VERIFY(fixture.dialog()->property("visible").toBool());
    QVERIFY(fixture.resolve());
    QCOMPARE(fixture.backend.counts.value(QStringLiteral("SetRunReflection")), 0);
    QCOMPARE(fixture.backend.counts.value(QStringLiteral("CorrectRun")), 1);
    QVERIFY(fixture.backend.finish(QStringLiteral("CorrectRun"), true));
    QTRY_VERIFY(!fixture.dialog()->property("visible").toBool());
}

void UiWorkflowRegressionTests::pendingReflectionLocksEscapeAndRunSelection()
{
    UiFixture fixture;
    QVERIFY2(fixture.create(), qPrintable(fixture.errors));
    QVERIFY(fixture.openResult());
    QTRY_VERIFY(fixture.dialog()->property("opened").toBool());
    fixture.textArea()->setProperty("text", QStringLiteral("original input"));
    QVERIFY(fixture.resolve());
    QVERIFY(fixture.textArea()->property("readOnly").toBool());
    auto *window = qobject_cast<QQuickWindow *>(fixture.root.get());
    QVERIFY(window);
    QTest::keyClick(window, Qt::Key_Escape);
    QVERIFY(fixture.dialog()->property("visible").toBool());
    QVERIFY(fixture.openResult(run(QStringLiteral("other-run"))));
    QCOMPARE(fixture.dialog()->property("runId").toString(), QStringLiteral("fixture-run"));
    QCOMPARE(fixture.textArea()->property("text").toString(), QStringLiteral("original input"));
    QVERIFY(fixture.backend.finish(QStringLiteral("SetRunReflection"), false));
    QVERIFY(!fixture.textArea()->property("readOnly").toBool());
    QTest::keyClick(window, Qt::Key_Escape);
    QTRY_VERIFY(!fixture.dialog()->property("visible").toBool());
    QCOMPARE(fixture.backend.counts.value(QStringLiteral("CorrectRun")), 0);
}

void UiWorkflowRegressionTests::ordinaryReflectionSaveStillWaitsForItsReply()
{
    UiFixture fixture;
    QVERIFY2(fixture.create(), qPrintable(fixture.errors));
    QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "openForRun",
                                     Q_ARG(QVariant, QVariant(run())), Q_ARG(QVariant, QVariant(QString()))));
    QTRY_VERIFY(fixture.dialog()->property("visible").toBool());
    fixture.textArea()->setProperty("text", QStringLiteral("ordinary reflection"));
    auto *button = fixture.dialog()->findChild<QObject *>(QStringLiteral("saveReflectionButton"));
    QVERIFY(button);
    QVERIFY(QMetaObject::invokeMethod(button, "clicked"));
    QVERIFY(fixture.dialog()->property("submitting").toBool());
    QVERIFY(fixture.backend.finish(QStringLiteral("SetRunReflection"), false));
    QVERIFY(fixture.dialog()->property("visible").toBool());
    QCOMPARE(fixture.textArea()->property("text").toString(), QStringLiteral("ordinary reflection"));
    QVERIFY(QMetaObject::invokeMethod(button, "clicked"));
    QVERIFY(fixture.backend.finish(QStringLiteral("SetRunReflection"), true));
    QTRY_VERIFY(!fixture.dialog()->property("visible").toBool());
    QCOMPARE(fixture.backend.counts.value(QStringLiteral("CorrectRun")), 0);
}

void UiWorkflowRegressionTests::unrelatedReflectionReplyDoesNotCloseAnIdleDialog()
{
    UiFixture fixture;
    QVERIFY2(fixture.create(), qPrintable(fixture.errors));
    QVERIFY(fixture.openResult());
    QTRY_VERIFY(fixture.dialog()->property("visible").toBool());
    fixture.textArea()->setProperty("text", QStringLiteral("unsent draft"));
    fixture.controller.reflectionSaved(QStringLiteral("fixture-run"), false);
    fixture.controller.reflectionFailed(QStringLiteral("ERR_BUSY"), QStringLiteral("unrelated refusal"));
    QVERIFY(fixture.dialog()->property("visible").toBool());
    QVERIFY(fixture.dialog()->property("errorText").toString().isEmpty());
    QCOMPARE(fixture.textArea()->property("text").toString(), QStringLiteral("unsent draft"));
}

void UiWorkflowRegressionTests::historyMirrorsEveryFilterAndClearsStaleControls()
{
    UiFixture fixture;
    QVERIFY2(fixture.create(), qPrintable(fixture.errors));
    const QString category = QString::fromUtf8("四人迷宫");
    QVariantMap filter{{QStringLiteral("job_id"), QVariantList{19}},
                       {QStringLiteral("duty_category"), QVariantList{category}},
                       {QStringLiteral("result"), QVariantList{QStringLiteral("COMPLETED")}},
                       {QStringLiteral("source"), QVariantList{QStringLiteral("MANUAL")}},
                       {QStringLiteral("content_id"), QVariantList{1}},
                       {QStringLiteral("text"), QStringLiteral("fixture")},
                       {QStringLiteral("corrected_only"), true},
                       {QStringLiteral("pending_review"), true},
                       {QStringLiteral("with_reflection"), true},
                       {QStringLiteral("include_deleted"), true}};
    filter.insert(QStringLiteral("from_utc"),
                  QDateTime(QDate(2026, 9, 7), QTime(0, 0)).toUTC().toString(Qt::ISODateWithMs));
    filter.insert(QStringLiteral("to_utc"),
                  QDateTime(QDate(2026, 9, 8), QTime(23, 59, 59, 999)).toUTC().toString(Qt::ISODateWithMs));
    fixture.controller.setHistoryFilter(filter);
    QVERIFY(QMetaObject::invokeMethod(fixture.history(), "applyFilter"));
    const auto actual = fixture.controller.historyFilter();
    for (auto it = filter.cbegin(); it != filter.cend(); ++it) {
        // IPC normalises numeric array entries to qlonglong; compare their
        // JSON values, which are the filter contract rather than C++ widths.
        const auto got = QJsonValue::fromVariant(actual.value(it.key()));
        const auto expected = QJsonValue::fromVariant(it.value());
        QVERIFY2(got == expected, qPrintable(QStringLiteral("Filter mismatch: %1; actual=%2; expected=%3")
            .arg(it.key(), QString::fromUtf8(QJsonDocument(QJsonArray{got}).toJson(QJsonDocument::Compact)),
                 QString::fromUtf8(QJsonDocument(QJsonArray{expected}).toJson(QJsonDocument::Compact)))));
    }
    fixture.controller.showHistoryForJob(21);
    QVERIFY(QMetaObject::invokeMethod(fixture.history(), "applyFilter"));
    QCOMPARE(fixture.controller.historyFilter().value(QStringLiteral("job_id")).toList().first().toInt(), 21);
    QCOMPARE(fixture.controller.historyFilter().size(), 2); // job_id plus date_field
    fixture.controller.showPendingReview();
    QVERIFY(QMetaObject::invokeMethod(fixture.history(), "applyFilter"));
    QCOMPARE(fixture.controller.historyFilter().value(QStringLiteral("pending_review")).toBool(), true);
    QCOMPARE(fixture.controller.historyFilter().size(), 2); // pending_review plus date_field
}

void UiWorkflowRegressionTests::externalHistoryFilterCancelsPendingDebounce()
{
    UiFixture fixture;
    QVERIFY2(fixture.create(), qPrintable(fixture.errors));
    QVERIFY(QMetaObject::invokeMethod(fixture.history(), "scheduleFilter"));
    QSignalSpy changes(&fixture.controller, &mr::AppController::historyFilterChanged);
    fixture.controller.showHistoryForJob(19);
    QCOMPARE(changes.count(), 1);
    QTest::qWait(360);
    QCOMPARE(changes.count(), 1);
    QCOMPARE(fixture.controller.historyFilter().value(QStringLiteral("job_id")).toList().first().toInt(), 19);
}

void UiWorkflowRegressionTests::mockCorrectionAcknowledgesOnlyExplicitOutcome_data()
{
    QTest::addColumn<QJsonObject>("changes");
    QTest::addColumn<bool>("initialPending");
    QTest::addColumn<bool>("succeeds");
    QTest::addColumn<bool>("finalPending");
    QTest::addColumn<QString>("errorCode");
    QTest::newRow("note-only-keeps-review")
        << QJsonObject{{QStringLiteral("note"), QStringLiteral("new note")}}
        << true << true << true << QString();
    QTest::newRow("explicit-false-acknowledges")
        << QJsonObject{{QStringLiteral("pending_review"), false}}
        << true << true << false << QString();
    QTest::newRow("same-result-acknowledges")
        << QJsonObject{{QStringLiteral("result"), QStringLiteral("UNKNOWN")}}
        << true << true << false << QString();
    QTest::newRow("changed-result-acknowledges")
        << QJsonObject{{QStringLiteral("result"), QStringLiteral("COMPLETED")}}
        << true << true << false << QString();
    QTest::newRow("true-is-refused-without-partial-note-change")
        << QJsonObject{{QStringLiteral("pending_review"), true},
                       {QStringLiteral("note"), QStringLiteral("must not be saved")}}
        << true << false << true << QStringLiteral("ERR_BAD_REQUEST");
    QTest::newRow("already-acknowledged-false-is-not-a-change")
        << QJsonObject{{QStringLiteral("pending_review"), false}}
        << false << false << false << QStringLiteral("ERR_NO_CHANGES");
    QTest::newRow("same-result-without-review-is-not-a-change")
        << QJsonObject{{QStringLiteral("result"), QStringLiteral("UNKNOWN")}}
        << false << false << false << QStringLiteral("ERR_NO_CHANGES");
}

void UiWorkflowRegressionTests::mockCorrectionAcknowledgesOnlyExplicitOutcome()
{
    QFETCH(QJsonObject, changes);
    QFETCH(bool, initialPending);
    QFETCH(bool, succeeds);
    QFETCH(bool, finalPending);
    QFETCH(QString, errorCode);
    mr::MockBackend backend;
    QJsonObject initial{{QStringLiteral("run_id"), QStringLiteral("fixture-run")},
                        {QStringLiteral("revision"), 1},
                        {QStringLiteral("result"), QStringLiteral("UNKNOWN")},
                        {QStringLiteral("pending_review"), initialPending},
                        {QStringLiteral("note"), QStringLiteral("original note")},
                        {QStringLiteral("matched_at_utc"), QStringLiteral("2026-09-08T01:00:00Z")},
                        {QStringLiteral("entered_at_utc"), QStringLiteral("2026-09-08T01:01:00Z")},
                        {QStringLiteral("ended_at_utc"), QStringLiteral("2026-09-08T01:30:00Z")},
                        {QStringLiteral("duration_ms"), 1740000}};
    backend.resetRuns(QJsonArray{initial});
    bool done = false;
    bool ok = false;
    QString code;
    backend.correctRun(QStringLiteral("fixture-run"), 1, changes, QStringLiteral("review regression"))
        ->whenDone(this, [&](bool success, const QVariantMap &, const QString &failure, const QString &) {
            ok = success;
            code = failure;
            done = true;
        });
    QTRY_VERIFY(done);
    QCOMPARE(ok, succeeds);
    QCOMPARE(code, errorCode);
    done = false;
    QVariantMap actual;
    backend.queryRuns({}, 1, 10)->whenDone(this,
        [&](bool success, const QVariantMap &payload, const QString &, const QString &) {
            ok = success;
            const auto items = payload.value(QStringLiteral("items")).toList();
            if (!items.isEmpty()) actual = items.first().toMap();
            done = true;
        });
    QTRY_VERIFY(done);
    QVERIFY(ok);
    QCOMPARE(actual.value(QStringLiteral("pending_review")).toBool(), finalPending);
    QCOMPARE(actual.value(QStringLiteral("revision")).toInt(), succeeds ? 2 : 1);
    if (!succeeds)
        QCOMPARE(actual.value(QStringLiteral("note")).toString(), QStringLiteral("original note"));
    done = false;
    QVariantList revisions;
    backend.getRunRevisions(QStringLiteral("fixture-run"))->whenDone(this,
        [&](bool success, const QVariantMap &payload, const QString &, const QString &) {
            ok = success;
            revisions = payload.value(QStringLiteral("items")).toList();
            done = true;
        });
    QTRY_VERIFY(done);
    QVERIFY(ok);
    QCOMPARE(revisions.size(), succeeds ? 1 : 0);
    if (succeeds) {
        int acknowledgements = 0;
        for (const auto &change : revisions.first().toMap().value(QStringLiteral("changes")).toList()) {
            if (change.toMap().value(QStringLiteral("field")).toString() == QLatin1String("pending_review"))
                ++acknowledgements;
        }
        QCOMPARE(acknowledgements, initialPending && !finalPending ? 1 : 0);
    }
}

// ColorAnimation interpolates straight RGBA, and "transparent" is #00000000: a fade from it
// to the classic light fill (#f2f2f7) passes through half-opaque grey, so every sidebar
// item the pointer crosses flashes a dark plate. A control at rest must hold its hover
// colour at zero alpha, so the fade only ever changes alpha. Each control declares that
// colour as `hoverColor` (a classic ghost button hovers to accent-100, everything else to
// the fill).
void UiWorkflowRegressionTests::hoverFadeRestsOnTheHoverColour_data()
{
    QTest::addColumn<QString>("style");
    QTest::addColumn<QString>("theme");
    for (const auto &style : {QStringLiteral("classic"), QStringLiteral("eorzea")}) {
        for (const auto &theme : {QStringLiteral("light"), QStringLiteral("dark")})
            QTest::newRow(qPrintable(style + QLatin1Char('-') + theme)) << style << theme;
    }
}

void UiWorkflowRegressionTests::hoverFadeRestsOnTheHoverColour()
{
    QFETCH(QString, style);
    QFETCH(QString, theme);
    WorkflowBackend backend;
    mr::AppController controller{&backend, nullptr};
    controller.setThemeMode(theme);
    QQmlEngine engine;
    engine.rootContext()->setContextProperty(QStringLiteral("App"), &controller);
    engine.rootContext()->setContextProperty(QStringLiteral("ForceUiStyle"), style);
    engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
    QQmlComponent component(&engine);
    component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 400; height: 200; visible: true
    Column {
        NavItem { objectName: "nav"; width: 180; number: "01"; label: "nav" }
        AppButton { objectName: "ghost"; variant: "ghost"; text: "ghost" }
        AppButton { objectName: "primary"; variant: "primary"; text: "primary" }
    }
})", QUrl());
    const std::unique_ptr<QObject> root(component.create());
    QString errors;
    for (const auto &error : component.errors())
        errors += error.toString() + QLatin1Char('\n');
    QVERIFY2(root, qPrintable(errors));

    for (const auto &name : {QStringLiteral("nav"), QStringLiteral("ghost")}) {
        const QObject *control = root->findChild<QObject *>(name);
        QVERIFY2(control, qPrintable(name));
        QVERIFY2(!control->property("hovered").toBool(), qPrintable(name));
        const QVariant hoverValue = control->property("hoverColor");
        QVERIFY2(hoverValue.isValid(), qPrintable(name + QStringLiteral(" declares no hoverColor")));
        const QColor hover = hoverValue.value<QColor>();
        QVERIFY2(hover.alpha() > 0, qPrintable(name));
        const auto *background = control->property("background").value<QObject *>();
        QVERIFY2(background, qPrintable(name));
        const QColor rest = background->property("color").value<QColor>();
        const QString detail = QStringLiteral("%1 rests on %2, hover is %3")
                                   .arg(name, rest.name(QColor::HexArgb), hover.name(QColor::HexArgb));
        QVERIFY2(rest.alpha() == 0, qPrintable(detail));
        QVERIFY2(rest.rgb() == hover.rgb(), qPrintable(detail));
    }

    // In eorzea the primary button's base sits under the gold gradient, which hides at
    // once when the style flips to classic; a base resting on transparent black then
    // fades up to the accent through black.
    const QObject *primary = root->findChild<QObject *>(QStringLiteral("primary"));
    QVERIFY(primary);
    const auto *primaryBackground = primary->property("background").value<QObject *>();
    QVERIFY(primaryBackground);
    const QColor primaryRest = primaryBackground->property("color").value<QColor>();
    QVERIFY2(primaryRest.rgba() != qRgba(0, 0, 0, 0),
             qPrintable(QStringLiteral("primary rests on %1").arg(primaryRest.name(QColor::HexArgb))));
}

// The prototype drives both the skin and the 界面风格 segment from one value (curStyle()).
// --mock-ui-style pins the skin without touching desktop.ini, so the segment has to follow
// that override rather than the persisted setting.
void UiWorkflowRegressionTests::styleSwitchShowsTheStyleOnScreen_data()
{
    QTest::addColumn<QString>("forced");
    QTest::addColumn<QString>("persisted");
    QTest::addColumn<QString>("expected");
    QTest::newRow("forced-classic") << QStringLiteral("classic") << QStringLiteral("eorzea") << QStringLiteral("classic");
    QTest::newRow("forced-eorzea") << QStringLiteral("eorzea") << QStringLiteral("classic") << QStringLiteral("eorzea");
    QTest::newRow("persisted-classic") << QString() << QStringLiteral("classic") << QStringLiteral("classic");
    QTest::newRow("persisted-eorzea") << QString() << QStringLiteral("eorzea") << QStringLiteral("eorzea");
    QTest::newRow("forced-harendotes") << QStringLiteral("harendotes") << QStringLiteral("classic") << QStringLiteral("harendotes");
    QTest::newRow("persisted-harendotes") << QString() << QStringLiteral("harendotes") << QStringLiteral("harendotes");
}

void UiWorkflowRegressionTests::styleSwitchShowsTheStyleOnScreen()
{
    QFETCH(QString, forced);
    QFETCH(QString, persisted);
    QFETCH(QString, expected);
    // Declared before the fixture so its engine goes first.
    mr::AppSettings settings;
    settings.setUiStyle(persisted);
    TtsStub tts;
    UiFixture fixture;
    const auto context = fixture.engine.rootContext();
    context->setContextProperty(QStringLiteral("App"), &fixture.controller);
    context->setContextProperty(QStringLiteral("Fmt"), &fixture.formatters);
    context->setContextProperty(QStringLiteral("Settings"), &settings);
    context->setContextProperty(QStringLiteral("Tts"), &tts);
    context->setContextProperty(QStringLiteral("ForceUiStyle"), forced);
    context->setContextProperty(QStringLiteral("ReduceMotion"), true);
    QQmlComponent component(&fixture.engine);
    component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 1100; height: 720; visible: true
    readonly property bool eorzea: Theme.eorzea
    readonly property string uiStyle: Theme.uiStyle
    SettingsPage { anchors.fill: parent }
})", QUrl());
    const std::unique_ptr<QObject> root(component.create());
    QString errors;
    for (const auto &error : component.errors())
        errors += error.toString() + QLatin1Char('\n');
    QVERIFY2(root, qPrintable(errors));

    // Both gilded styles share eorzea's layout; uiStyle names the palette.
    QCOMPARE(root->property("eorzea").toBool(), expected != QLatin1String("classic"));
    QCOMPARE(root->property("uiStyle").toString(), expected);
    const QObject *control = root->findChild<QObject *>(QStringLiteral("uiStyleSettingControl"));
    QVERIFY(control);
    QCOMPARE(control->property("currentValue").toString(), expected);
}

// 设置 is a sub-navigation over five tabs (docs/ui-design.md §4.5). Every tab must be
// reachable from its nav entry, show its own controls and hide the others'; the
// objectNames here are the ones main.cpp's self-check and the docs rely on.
void UiWorkflowRegressionTests::settingsTabsShowTheirOwnControls_data()
{
    QTest::addColumn<QString>("tab");
    QTest::addColumn<QStringList>("controls");
    QTest::newRow("general") << QStringLiteral("general")
        << QStringList{QStringLiteral("generalSettingsCard"), QStringLiteral("appearanceSettingsCard"),
                       QStringLiteral("uiStyleSettingControl"), QStringLiteral("autoCalibrationToggle"),
                       QStringLiteral("sharedCalibrationToggle")};
    QTest::newRow("tts") << QStringLiteral("tts")
        << QStringList{QStringLiteral("ttsVoiceCombo"), QStringLiteral("ttsPreviewButton"),
                       QStringLiteral("speechTemplatesCard")};
    QTest::newRow("goal") << QStringLiteral("goal")
        << QStringList{QStringLiteral("baselineReasonField"), QStringLiteral("progressFormula"),
                       QStringLiteral("saveAchievementButton")};
    QTest::newRow("data") << QStringLiteral("data")
        << QStringList{QStringLiteral("logRetentionField"), QStringLiteral("integrityCheckButton"),
                       QStringLiteral("databasePathField")};
    QTest::newRow("about") << QStringLiteral("about")
        << QStringList{QStringLiteral("copyrightCard"), QStringLiteral("readsGameExecutableTile"),
                       QStringLiteral("disclosureTile")};
}

void UiWorkflowRegressionTests::settingsTabsShowTheirOwnControls()
{
    QFETCH(QString, tab);
    QFETCH(QStringList, controls);
    SettingsFixture fixture;
    QVERIFY2(fixture.create(), qPrintable(fixture.errors));
    QCOMPARE(fixture.page()->property("currentTab").toString(), QStringLiteral("general"));

    const QStringList tabs{QStringLiteral("general"), QStringLiteral("tts"), QStringLiteral("goal"),
                           QStringLiteral("data"), QStringLiteral("about")};
    // Start from another tab so the click is a real change.
    const QString other = tab == tabs.first() ? tabs.last() : tabs.first();
    QVERIFY(fixture.selectTab(other));
    QVERIFY(fixture.selectTab(tab));
    QCOMPARE(fixture.page()->property("currentTab").toString(), tab);
    QVERIFY(fixture.item(QStringLiteral("settingsTab_") + tab)->property("current").toBool());
    QVERIFY(!fixture.item(QStringLiteral("settingsTab_") + other)->property("current").toBool());

    for (const QString &name : controls) {
        QQuickItem *control = fixture.item(name);
        QVERIFY2(control, qPrintable(name));
        QVERIFY2(control->isVisible(), qPrintable(name + QStringLiteral(" hidden on ") + tab));
    }
    // Another tab's key control exists but is not on screen.
    const QString foreign = tab == QLatin1String("data") ? QStringLiteral("copyrightCard")
                                                         : QStringLiteral("logRetentionField");
    QVERIFY(fixture.item(foreign));
    QVERIFY2(!fixture.item(foreign)->isVisible(), qPrintable(foreign));

    // Maintainer-only and not-yet-implemented items keep their place, hidden.
    QVERIFY(fixture.item(QStringLiteral("followGameToggle")));
    QVERIFY(!fixture.item(QStringLiteral("followGameToggle"))->isVisible());
    QQuickItem *slot = fixture.item(QStringLiteral("onlineSpeechSlot"));
    QVERIFY(slot);
    QVERIFY(!slot->isVisible());
}

void UiWorkflowRegressionTests::integrityButtonShowsTheCollectorsAnswer_data()
{
    const QString stamp = QStringLiteral("2026-09-16T12:34:56.000Z");
    QTest::addColumn<QJsonObject>("answer");
    QTest::addColumn<QString>("failure");
    QTest::addColumn<QString>("expected");
    QTest::addColumn<QString>("state");
    QTest::newRow("passed")
        << QJsonObject{{QStringLiteral("passed"), true}, {QStringLiteral("detail"), QStringLiteral("ok")},
                       {QStringLiteral("checked_at_utc"), stamp}}
        << QString()
        << QString::fromUtf8("完整性校验通过 · ") + mr::Formatters::localTime(stamp)
        << QStringLiteral("passed");
    QTest::newRow("failed")
        << QJsonObject{{QStringLiteral("passed"), false},
                       {QStringLiteral("detail"), QStringLiteral("row 3 missing from index idx_runs")},
                       {QStringLiteral("checked_at_utc"), stamp}}
        << QString()
        << QString::fromUtf8("完整性校验未通过：row 3 missing from index idx_runs")
        << QStringLiteral("failed");
    QTest::newRow("older-collector")
        << QJsonObject() << QStringLiteral("ERR_UNKNOWN_MESSAGE")
        << QString::fromUtf8("当前采集器不支持完整性校验") << QStringLiteral("unsupported");
    QTest::newRow("busy")
        << QJsonObject() << QStringLiteral("ERR_DB_BUSY")
        << QString::fromUtf8("完整性校验没有完成：fixture refusal") << QStringLiteral("error");
}

void UiWorkflowRegressionTests::integrityButtonShowsTheCollectorsAnswer()
{
    QFETCH(QJsonObject, answer);
    QFETCH(QString, failure);
    QFETCH(QString, expected);
    QFETCH(QString, state);
    SettingsFixture fixture;
    const QString type = QStringLiteral("CheckDatabaseIntegrity");
    if (failure.isEmpty())
        fixture.backend.answers.insert(type, answer);
    else
        fixture.backend.failures.insert(type, failure);
    QVERIFY2(fixture.create(), qPrintable(fixture.errors));
    QVERIFY(fixture.selectTab(QStringLiteral("data")));

    QQuickItem *button = fixture.item(QStringLiteral("integrityCheckButton"));
    QQuickItem *line = fixture.item(QStringLiteral("integrityCheckResultText"));
    QVERIFY(button);
    QVERIFY(line);
    QVERIFY(button->isEnabled());
    QVERIFY(!line->isVisible());

    QVERIFY(QMetaObject::invokeMethod(button, "clicked"));
    QCOMPARE(fixture.backend.counts.value(type), 1);
    QCOMPARE(fixture.backend.lastPayloads.value(type), QJsonObject());
    // The reply is queued: the button waits for it and cannot start a second check.
    QVERIFY(fixture.controller.integrityCheckRunning());
    QVERIFY(!button->isEnabled());
    fixture.controller.checkDatabaseIntegrity();
    QCOMPARE(fixture.backend.counts.value(type), 1);

    QTRY_VERIFY(!fixture.controller.integrityCheckRunning());
    QVERIFY(button->isEnabled());
    QVERIFY(line->isVisible());
    QCOMPARE(line->property("text").toString(), expected);
    QCOMPARE(fixture.controller.integrityCheckResult().value(QStringLiteral("state")).toString(), state);
    const QColor colour = line->property("color").value<QColor>();
    const QColor red = fixture.item(QStringLiteral("baselineErrorText"))->property("color").value<QColor>();
    QCOMPARE(colour == red, state == QLatin1String("failed"));
}

void UiWorkflowRegressionTests::voiceComboListsTheVoicesAndSelectsOne()
{
    SettingsFixture fixture;
    const QVariantList voices{
        voiceRow(QStringLiteral("Microsoft Huihui Desktop"),
                 QString::fromUtf8("Microsoft Huihui Desktop（中文 · 女声）"), QStringLiteral("zh-CN")),
        voiceRow(QStringLiteral("Microsoft Zira Desktop"),
                 QString::fromUtf8("Microsoft Zira Desktop（英语 · 女声）"), QStringLiteral("en-US"))};
    fixture.tts.setVoices(voices, QStringLiteral("local:Microsoft Zira Desktop"));
    QVERIFY2(fixture.create(), qPrintable(fixture.errors));
    QVERIFY(fixture.selectTab(QStringLiteral("tts")));

    QQuickItem *combo = fixture.item(QStringLiteral("ttsVoiceCombo"));
    QVERIFY(combo);
    QVERIFY(combo->isVisible());
    QVERIFY(combo->isEnabled());
    QCOMPARE(combo->property("count").toInt(), 2);
    QCOMPARE(combo->property("currentIndex").toInt(), 1);
    QCOMPARE(combo->property("displayText").toString(),
             QString::fromUtf8("Microsoft Zira Desktop（英语 · 女声）"));

    QVERIFY(QMetaObject::invokeMethod(combo, "activated", Q_ARG(int, 0)));
    QCOMPARE(fixture.tts.requestedVoices,
             QStringList{QStringLiteral("local:Microsoft Huihui Desktop")});
    // The box follows what the service reports, including a change made elsewhere.
    QCOMPARE(combo->property("currentIndex").toInt(), 0);
    fixture.tts.setVoices(voices, QStringLiteral("local:Microsoft Zira Desktop"));
    QCOMPARE(combo->property("currentIndex").toInt(), 1);

    // 试听 still plays the 结束待确认 line.
    QVERIFY(QMetaObject::invokeMethod(fixture.item(QStringLiteral("ttsPreviewButton")), "clicked"));
    QCOMPARE(fixture.tts.previews, QStringList{QStringLiteral("finished")});
}

// 在线语音 (docs/ui-design.md §4.5): the panel exists only while an online
// voice is selected, and the header says where the sentence goes.
void UiWorkflowRegressionTests::onlinePanelAppearsOnlyForOnlineVoices()
{
    SettingsFixture fixture;
    fixture.backend.answers.insert(QStringLiteral("GetSpeechSettings"), speechSettingsAnswer(true));
    // The controller already asked once while the fixture was being built.
    fixture.controller.speech()->refresh();
    fixture.tts.setVoices(speechVoiceRows(), QStringLiteral("local:Microsoft Huihui Desktop"));
    QVERIFY2(fixture.create(), qPrintable(fixture.errors));
    QVERIFY(fixture.selectTab(QStringLiteral("tts")));
    QTRY_COMPARE(fixture.controller.speech()->provider(), QStringLiteral("azure"));

    QQuickItem *slot = fixture.item(QStringLiteral("onlineSpeechSlot"));
    QQuickItem *header = fixture.item(QStringLiteral("speechHeaderRow"));
    QVERIFY(slot);
    QVERIFY(header);
    QVERIFY(!slot->isVisible());
    QVERIFY(!fixture.item(QStringLiteral("onlineSpeechCard")));
    QCOMPARE(header->property("description").toString(), QString::fromUtf8("使用系统语音引擎，不联网"));
    QCOMPARE(header->property("label").toString(), QString::fromUtf8("本地语音播报"));

    fixture.tts.setVoices(speechVoiceRows(), QStringLiteral("azure:zh-CN-XiaoxiaoNeural"));
    QTRY_VERIFY(slot->isVisible());
    QQuickItem *card = fixture.item(QStringLiteral("onlineSpeechCard"));
    QVERIFY(card);
    QVERIFY(card->isVisible());
    QVERIFY(card->height() > 100);
    QCOMPARE(header->property("description").toString(),
             QString::fromUtf8("播报文字会发送到 eastasia.azure-speech.invalid"));
    QCOMPARE(header->property("label").toString(), QString::fromUtf8("语音播报"));
    QVERIFY(fixture.item(QStringLiteral("onlineSpeechRegionField"))->isVisible());
    QVERIFY(!fixture.item(QStringLiteral("onlineSpeechUrlField"))->isVisible());
    QCOMPARE(fixture.item(QStringLiteral("onlineSpeechRegionField"))->property("text").toString(),
             QStringLiteral("eastasia"));
    QCOMPARE(fixture.item(QStringLiteral("onlineSpeechNote"))->property("text").toString(),
             QString::fromUtf8("开启后播报文字会发送到 eastasia.azure-speech.invalid；密钥只保存在本机，并由 Windows 加密。"));
    QVERIFY(fixture.item(QStringLiteral("onlineSpeechTestButton"))->isEnabled());

    // 测试 goes through TtsService, which answers for the button.
    QVERIFY(QMetaObject::invokeMethod(fixture.item(QStringLiteral("onlineSpeechTestButton")), "clicked"));
    QTRY_VERIFY(!fixture.controller.speech()->busy());
    QVERIFY(!fixture.item(QStringLiteral("onlineSpeechResultText"))->property("text").toString().isEmpty());

    // An OpenAI voice while the Collector has Azure: a draft, not configured.
    fixture.tts.setVoices(speechVoiceRows(), QStringLiteral("openai:alloy"));
    QTRY_VERIFY(fixture.item(QStringLiteral("onlineSpeechUrlField"))->isVisible());
    QVERIFY(!fixture.item(QStringLiteral("onlineSpeechRegionField"))->isVisible());
    QVERIFY(fixture.item(QStringLiteral("onlineSpeechModelField"))->isVisible());
    QCOMPARE(fixture.item(QStringLiteral("onlineSpeechVoiceField"))->property("text").toString(),
             QStringLiteral("alloy"));
    QCOMPARE(header->property("description").toString(), QString::fromUtf8("在线语音 · 尚未配置"));
    QVERIFY(!fixture.item(QStringLiteral("onlineSpeechTestButton"))->isEnabled());
    QVERIFY(fixture.item(QStringLiteral("onlineSpeechRebindHint"))->isVisible());

    // Back to a local voice: the panel goes away again.
    fixture.tts.setVoices(speechVoiceRows(), QStringLiteral("local:Microsoft Huihui Desktop"));
    QTRY_VERIFY(!slot->isVisible());
    QCOMPARE(header->property("description").toString(), QString::fromUtf8("使用系统语音引擎，不联网"));

    // An older Collector: no online panel even for an online id.
    SettingsFixture older;
    older.backend.failures.insert(QStringLiteral("GetSpeechSettings"), QStringLiteral("ERR_UNKNOWN_MESSAGE"));
    older.controller.speech()->refresh();
    older.tts.setVoices(speechVoiceRows(), QStringLiteral("azure:zh-CN-XiaoxiaoNeural"));
    QVERIFY2(older.create(), qPrintable(older.errors));
    QVERIFY(older.selectTab(QStringLiteral("tts")));
    QTRY_VERIFY(older.controller.speech()->loaded());
    QVERIFY(!older.controller.speech()->supported());
    QVERIFY(!older.item(QStringLiteral("onlineSpeechSlot"))->isVisible());
}

void UiWorkflowRegressionTests::onlineKeyFieldIsNeverFilledAndEmptiesOnSave()
{
    SettingsFixture fixture;
    fixture.backend.answers.insert(QStringLiteral("GetSpeechSettings"), speechSettingsAnswer(true));
    fixture.backend.answers.insert(QStringLiteral("UpdateSpeechSettings"), speechSettingsAnswer(true));
    fixture.controller.speech()->refresh();
    fixture.tts.setVoices(speechVoiceRows(), QStringLiteral("azure:zh-CN-XiaoxiaoNeural"));
    QVERIFY2(fixture.create(), qPrintable(fixture.errors));
    QVERIFY(fixture.selectTab(QStringLiteral("tts")));
    QTRY_VERIFY(fixture.item(QStringLiteral("onlineSpeechCard")));

    QQuickItem *key = fixture.item(QStringLiteral("onlineSpeechKeyField"));
    QVERIFY(key);
    // has_key is true, and still nothing is shown: the key never comes back.
    QCOMPARE(key->property("text").toString(), QString());
    QCOMPARE(key->property("echoMode").toInt(), 2); // TextInput.Password
    QTRY_VERIFY(fixture.item(QStringLiteral("onlineSpeechClearKeyButton"))->isEnabled());
    QCOMPARE(key->property("text").toString(), QString());

    key->setProperty("text", QStringLiteral("typed-secret-1"));
    fixture.item(QStringLiteral("onlineSpeechRegionField"))->setProperty("text", QStringLiteral("westus"));
    QVERIFY(QMetaObject::invokeMethod(fixture.item(QStringLiteral("onlineSpeechSaveButton")), "clicked"));
    // Emptied at once, before the Collector has answered.
    QCOMPARE(key->property("text").toString(), QString());
    const QJsonObject sent = fixture.backend.lastPayloads.value(QStringLiteral("UpdateSpeechSettings"));
    QCOMPARE(sent.value(QStringLiteral("api_key")).toString(), QStringLiteral("typed-secret-1"));
    QCOMPARE(sent.value(QStringLiteral("provider")).toString(), QStringLiteral("azure"));
    QCOMPARE(sent.value(QStringLiteral("azure_region")).toString(), QStringLiteral("westus"));
    QCOMPARE(sent.value(QStringLiteral("voice")).toString(), QStringLiteral("zh-CN-XiaoxiaoNeural"));
    QTRY_VERIFY(!fixture.controller.speech()->busy());
    QCOMPARE(fixture.tts.requestedVoices, QStringList{QStringLiteral("azure:zh-CN-XiaoxiaoNeural")});
    QCOMPARE(key->property("text").toString(), QString());
    QCOMPARE(fixture.item(QStringLiteral("onlineSpeechResultText"))->property("text").toString(),
             QString::fromUtf8("已保存"));

    // Saving without typing a key sends none.
    QVERIFY(QMetaObject::invokeMethod(fixture.item(QStringLiteral("onlineSpeechSaveButton")), "clicked"));
    QVERIFY(!fixture.backend.lastPayloads.value(QStringLiteral("UpdateSpeechSettings"))
                 .contains(QStringLiteral("api_key")));
    QTRY_VERIFY(!fixture.controller.speech()->busy());

    // 清除 sends an empty key and nothing else.
    QVERIFY(QMetaObject::invokeMethod(fixture.item(QStringLiteral("onlineSpeechClearKeyButton")), "clicked"));
    QCOMPARE(fixture.backend.lastPayloads.value(QStringLiteral("UpdateSpeechSettings")),
             QJsonObject({{QStringLiteral("api_key"), QString()}}));
    QTRY_VERIFY(!fixture.controller.speech()->busy());

    // A refused save keeps the typed fields but not the key.
    fixture.backend.failures.insert(QStringLiteral("UpdateSpeechSettings"), QStringLiteral("ERR_BAD_REQUEST"));
    key->setProperty("text", QStringLiteral("typed-secret-2"));
    fixture.item(QStringLiteral("onlineSpeechRegionField"))->setProperty("text", QStringLiteral("bad region!"));
    QVERIFY(QMetaObject::invokeMethod(fixture.item(QStringLiteral("onlineSpeechSaveButton")), "clicked"));
    QTRY_VERIFY(!fixture.controller.speech()->busy());
    QCOMPARE(key->property("text").toString(), QString());
    QCOMPARE(fixture.item(QStringLiteral("onlineSpeechRegionField"))->property("text").toString(),
             QStringLiteral("bad region!"));
    QCOMPARE(fixture.item(QStringLiteral("onlineSpeechResultText"))->property("text").toString(),
             QString::fromUtf8("保存没有成功：fixture refusal"));
}

void UiWorkflowRegressionTests::onlineConfirmationIsAskedOnceAndCancelKeepsTheVoice()
{
    SettingsFixture fixture;
    const bool confirmedBefore = fixture.settings.ttsOnlineConfirmed();
    const auto restore = qScopeGuard([&fixture, confirmedBefore] {
        fixture.settings.setTtsOnlineConfirmed(confirmedBefore);
    });
    fixture.settings.setTtsOnlineConfirmed(false);
    fixture.backend.answers.insert(QStringLiteral("GetSpeechSettings"), speechSettingsAnswer(true));
    fixture.controller.speech()->refresh();
    const QString local = QStringLiteral("local:Microsoft Huihui Desktop");
    const QString azure = QStringLiteral("azure:zh-CN-XiaoxiaoNeural");
    fixture.tts.setVoices(speechVoiceRows(), local);
    QVERIFY2(fixture.create(), qPrintable(fixture.errors));
    QVERIFY(fixture.selectTab(QStringLiteral("tts")));
    QTRY_VERIFY(fixture.controller.speech()->loaded());

    QQuickItem *combo = fixture.item(QStringLiteral("ttsVoiceCombo"));
    QObject *dialog = fixture.root->findChild<QObject *>(QStringLiteral("onlineSpeechConfirmDialog"));
    QVERIFY(combo);
    QVERIFY(dialog);
    QCOMPARE(combo->property("count").toInt(), 3);
    QCOMPARE(combo->property("currentIndex").toInt(), 0);
    QVERIFY(!dialog->property("visible").toBool());

    // The popup shows the two groups under their own headers.
    QCOMPARE(combo->property("sectionRole").toString(), QStringLiteral("group"));
    auto *popup = combo->property("popup").value<QObject *>();
    QVERIFY(popup);
    QVERIFY(popup->setProperty("visible", true));
    const auto *window = qobject_cast<QQuickWindow *>(fixture.root.get());
    QVERIFY(window);
    const std::function<bool(QQuickItem *, const QString &)> shows =
        [&shows](QQuickItem *item, const QString &text) {
            if (!item)
                return false;
            if (item->isVisible() && item->property("text").toString() == text)
                return true;
            const auto children = item->childItems();
            return std::any_of(children.begin(), children.end(),
                               [&](QQuickItem *child) { return shows(child, text); });
        };
    QQuickItem *scene = window->contentItem()->parentItem() ? window->contentItem()->parentItem()
                                                            : window->contentItem();
    QTRY_VERIFY(shows(scene, QString::fromUtf8("在线语音 · 需配置密钥")));
    QVERIFY(shows(scene, QString::fromUtf8("本机语音 · 已安装 · Windows 自带")));
    QVERIFY(popup->setProperty("visible", false));
    QTRY_VERIFY(!popup->property("visible").toBool());

    // First online choice: asked, nothing chosen yet.
    combo->setProperty("currentIndex", 1);
    QVERIFY(QMetaObject::invokeMethod(combo, "activated", Q_ARG(int, 1)));
    QTRY_VERIFY(dialog->property("visible").toBool());
    QVERIFY(fixture.tts.requestedVoices.isEmpty());
    QCOMPARE(dialog->property("voiceId").toString(), azure);

    // 取消: the box goes back to the voice in use; nothing is remembered.
    QObject *cancel = dialog->findChild<QObject *>(QStringLiteral("onlineSpeechConfirmCancel"));
    QVERIFY(cancel);
    QVERIFY(QMetaObject::invokeMethod(cancel, "clicked"));
    QTRY_VERIFY(!dialog->property("visible").toBool());
    QTRY_COMPARE(combo->property("currentIndex").toInt(), 0);
    QVERIFY(fixture.tts.requestedVoices.isEmpty());
    QVERIFY(!fixture.settings.ttsOnlineConfirmed());
    QVERIFY(!fixture.item(QStringLiteral("onlineSpeechSlot"))->isVisible());

    // Asked again, and this time confirmed.
    combo->setProperty("currentIndex", 1);
    QVERIFY(QMetaObject::invokeMethod(combo, "activated", Q_ARG(int, 1)));
    QTRY_VERIFY(dialog->property("visible").toBool());
    QObject *accept = dialog->findChild<QObject *>(QStringLiteral("onlineSpeechConfirmAccept"));
    QVERIFY(accept);
    QVERIFY(QMetaObject::invokeMethod(accept, "clicked"));
    QTRY_VERIFY(!dialog->property("visible").toBool());
    QCOMPARE(fixture.tts.requestedVoices, QStringList{azure});
    QVERIFY(fixture.settings.ttsOnlineConfirmed());
    QTRY_VERIFY(fixture.item(QStringLiteral("onlineSpeechSlot"))->isVisible());

    // Confirmed once per machine: back to local and online again, no question.
    QVERIFY(QMetaObject::invokeMethod(combo, "activated", Q_ARG(int, 0)));
    QVERIFY(QMetaObject::invokeMethod(combo, "activated", Q_ARG(int, 2)));
    QVERIFY(!dialog->property("visible").toBool());
    QCOMPARE(fixture.tts.requestedVoices,
             (QStringList{azure, local, QStringLiteral("openai:alloy")}));
}

int main(int argc, char **argv)
{
    // Never run against the user's own Collector, database or serve lease - see
    // TestCollectorGuard.h. Must precede any construction.
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication app(argc, argv);
    UiWorkflowRegressionTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "UiWorkflowRegressionTests.moc"
