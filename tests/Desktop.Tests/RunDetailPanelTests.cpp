// The history detail panel (dialogs/RunDetailPanel.qml) on the history page, and the
// Formatters it reads its words from.
//
// What it pins:
//   * 修正历史 and 事件摘要 speak to a player in Chinese: change kinds, actors,
//     results, roles, durations, event names - and the identifiers the Collector
//     writes into a revision (run_id, protocol_profile_id, content_id …) or an
//     event's parsed fields only appear under 维护者工具 (standing rule 4);
//   * a normally parsed event carries no warning tag;
//   * a click on the panel - a tab, the header, a gap - stays in the panel: the
//     page's click-outside area and the history row beneath it never see it.
//
// The revision and event fixtures are Collector-shaped: every field
// SemanticEventProcessor.DescribeCreation and RunMutationService.InitialChanges
// write at creation, the fields a correction's diff writes, and the event types
// of Domain/Events/SemanticEvent.cs.

#include "TestCollectorGuard.h"
#include "AppController.h"
#include "Formatters.h"
#include "IBackend.h"
#include "ImportRecordsController.h"
#include "JobCatalog.h"
#include "RoleCatalog.h"
#include "RunFormValidator.h"

#include <QDir>
#include <QGuiApplication>
#include <QJsonArray>
#include <QJsonObject>
#include <QMetaEnum>
#include <QMetaProperty>
#include <QNetworkAccessManager> // BOUNDARY-ALLOW(NET-006): the test manager overrides requests and never contacts a network.
#include <QNetworkReply>
#include <QNetworkRequest>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQmlNetworkAccessManagerFactory>
#include <QQuickItem>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QRegularExpression>
#include <QStandardPaths>
#include <QTest>
#include <QTimer>

#include <memory>
#include <atomic>

namespace {

// QML's image loader is intercepted before any connection can be made. A rich-text
// control below proves this hook sees resource loads; production record views must
// never call it, including for markup copied from a third-party export.
class BlockedResourceReply final : public QNetworkReply
{
public:
    BlockedResourceReply(QNetworkAccessManager::Operation operation, const QNetworkRequest &request, QObject *parent) // BOUNDARY-ALLOW(NET-006): preserves the intercepted operation in a local error reply, without sending it.
        : QNetworkReply(parent)
    {
        setOperation(operation);
        setRequest(request);
        setUrl(request.url());
        open(QIODevice::ReadOnly);
        setError(OperationCanceledError, QStringLiteral("Resource requests are blocked by the test"));
        QTimer::singleShot(0, this, [this] {
            setFinished(true);
            Q_EMIT errorOccurred(error());
            Q_EMIT finished();
        });
    }
    void abort() override {}
protected:
    qint64 readData(char *, qint64) override { return -1; }
};

class ResourceRequestManager final : public QNetworkAccessManager // BOUNDARY-ALLOW(NET-006): fake-only resource loader; createRequest never delegates to the network implementation.
{
public:
    ResourceRequestManager(QObject *parent, std::atomic<int> &requests)
        : QNetworkAccessManager(parent), m_requests(requests) // BOUNDARY-ALLOW(NET-006): constructing the intercepting test manager does not initiate a request.
    {}
protected:
    QNetworkReply *createRequest(Operation operation, const QNetworkRequest &request, QIODevice *) override
    {
        // Inline SVG icons may use the same loader. Count only resources which
        // could leave the process, while still refusing every request locally.
        const auto scheme = request.url().scheme();
        if (!request.url().isLocalFile() && scheme != QLatin1String("data")
            && scheme != QLatin1String("qrc"))
            ++m_requests;
        return new BlockedResourceReply(operation, request, this);
    }
private:
    std::atomic<int> &m_requests;
};

class ResourceRequestFactory final : public QQmlNetworkAccessManagerFactory
{
public:
    std::atomic<int> requests{0};
    QNetworkAccessManager *create(QObject *parent) override // BOUNDARY-ALLOW(NET-006): supplies only the fake manager which locally rejects every QML resource request.
    {
        return new ResourceRequestManager(parent, requests);
    }
};

const QString kRunId = QStringLiteral("6f1c2d3e-4a5b-4c6d-8e7f-0123456789ab");

/// Every field the Collector writes into revision 1 of an automatic run
/// (SemanticEventProcessor.DescribeCreation), with a value of the wire shape.
QJsonArray creationChanges()
{
    const QList<QPair<const char *, QJsonValue>> fields{
        {"run_id", kRunId},
        {"revision", 1},
        {"capture_session_id", QStringLiteral("0d9e8f7a-6b5c-4d3e-8f2a-1b0c9d8e7f6a")},
        {"region", QStringLiteral("CN")},
        {"protocol_profile_id", QStringLiteral("cn-2026.09.01.0000.0000-shared")},
        {"mentor_roulette_id", 9},
        {"content_id", 1036},
        {"territory_id", 1036},
        {"duty_name", QString::fromUtf8("天然要害沙斯塔夏溶洞")},
        {"duty_category", QString::fromUtf8("四人迷宫")},
        {"job_id", 24},
        {"job_name", QString::fromUtf8("白魔法师")},
        {"role", QStringLiteral("HEALER")},
        {"matched_at_utc", QStringLiteral("2026-09-04T12:39:05.125Z")},
        {"entered_at_utc", QJsonValue::Null},
        {"ended_at_utc", QJsonValue::Null},
        {"duration_ms", QJsonValue::Null},
        {"result", QStringLiteral("UNKNOWN")},
        {"detection_confidence", QStringLiteral("MEDIUM")},
        {"source", QStringLiteral("AUTO_NETWORK")},
        {"contributes_to_goal", true},
        {"manually_created", false},
        {"manually_corrected", false},
        {"soft_deleted", false},
        {"created_at_utc", QStringLiteral("2026-09-04T12:39:05.125Z")},
        {"updated_at_utc", QStringLiteral("2026-09-04T12:39:05.125Z")},
    };
    QJsonArray out;
    for (const auto &field : fields) {
        out.append(QJsonObject{{QStringLiteral("field"), QString::fromLatin1(field.first)},
                               {QStringLiteral("old_value"), QJsonValue::Null},
                               {QStringLiteral("new_value"), field.second}});
    }
    return out;
}

QJsonObject change(const char *field, const QJsonValue &before, const QJsonValue &after)
{
    return {{QStringLiteral("field"), QString::fromLatin1(field)},
            {QStringLiteral("old_value"), before},
            {QStringLiteral("new_value"), after}};
}

QJsonArray revisions()
{
    return {
        QJsonObject{{QStringLiteral("revision_id"), QStringLiteral("11111111-1111-4111-8111-111111111111")},
                    {QStringLiteral("run_id"), kRunId},
                    {QStringLiteral("revision"), 1},
                    {QStringLiteral("changed_at_utc"), QStringLiteral("2026-09-04T12:39:05.125Z")},
                    {QStringLiteral("change_kind"), QStringLiteral("CREATE_AUTO")},
                    {QStringLiteral("actor"), QStringLiteral("SYSTEM")},
                    {QStringLiteral("reason"), QJsonValue::Null},
                    {QStringLiteral("changes"), creationChanges()}},
        // 用户确认通关, through CorrectRun: the diff RunMutationRules.Diff writes.
        QJsonObject{{QStringLiteral("revision_id"), QStringLiteral("22222222-2222-4222-8222-222222222222")},
                    {QStringLiteral("run_id"), kRunId},
                    {QStringLiteral("revision"), 2},
                    {QStringLiteral("changed_at_utc"), QStringLiteral("2026-09-04T13:10:00.000Z")},
                    {QStringLiteral("change_kind"), QStringLiteral("CORRECT")},
                    {QStringLiteral("actor"), QStringLiteral("USER")},
                    {QStringLiteral("reason"), QString::fromUtf8("用户确认通关")},
                    {QStringLiteral("changes"),
                     QJsonArray{change("content_id", 1036, 1037),
                                change("job_id", 24, 19),
                                change("job_name", QString::fromUtf8("白魔法师"), QString::fromUtf8("骑士")),
                                change("role", QStringLiteral("HEALER"), QStringLiteral("TANK")),
                                change("entered_at_utc", QJsonValue::Null,
                                       QStringLiteral("2026-09-04T12:41:00.000Z")),
                                change("ended_at_utc", QJsonValue::Null,
                                       QStringLiteral("2026-09-04T13:11:34.000Z")),
                                change("duration_ms", QJsonValue::Null, 1834000),
                                change("result", QStringLiteral("UNKNOWN"), QStringLiteral("COMPLETED")),
                                change("pending_review", true, false),
                                change("manually_corrected", false, true)}}},
        // Crash recovery's own revision.
        QJsonObject{{QStringLiteral("revision_id"), QStringLiteral("33333333-3333-4333-8333-333333333333")},
                    {QStringLiteral("run_id"), kRunId},
                    {QStringLiteral("revision"), 3},
                    {QStringLiteral("changed_at_utc"), QStringLiteral("2026-09-05T08:00:00.000Z")},
                    {QStringLiteral("change_kind"), QStringLiteral("CORRECT")},
                    {QStringLiteral("actor"), QStringLiteral("SYSTEM")},
                    {QStringLiteral("reason"), QString::fromUtf8("程序重启后收尾")},
                    {QStringLiteral("changes"),
                     QJsonArray{change("detection_confidence", QStringLiteral("MEDIUM"),
                                       QStringLiteral("LOW"))}}},
    };
}

QJsonObject event(const char *type, const char *status, const QJsonValue &parsed, int ordinal)
{
    return {{QStringLiteral("event_id"),
             QStringLiteral("44444444-4444-4444-8444-%1").arg(ordinal, 12, 10, QLatin1Char('0'))},
            {QStringLiteral("event_type"), QString::fromLatin1(type)},
            {QStringLiteral("observed_at_utc"), QStringLiteral("2026-09-04T12:39:05.125Z")},
            {QStringLiteral("direction"), QStringLiteral("S2C")},
            {QStringLiteral("opcode"), QStringLiteral("0x02F5")},
            {QStringLiteral("payload_hash"), QStringLiteral("0123456789ab")},
            {QStringLiteral("parser_status"), QString::fromLatin1(status)},
            {QStringLiteral("protocol_profile_id"), QStringLiteral("cn-2026.09.01.0000.0000-shared")},
            {QStringLiteral("parsed"), parsed}};
}

QJsonArray events()
{
    return {
        event("CONTENT_FINDER_POP", "PARSED",
              QJsonObject{{QStringLiteral("roulette_id"), 9}, {QStringLiteral("content_id"), QJsonValue::Null}}, 1),
        event("ZONE_INITIALIZATION", "PARSED",
              QJsonObject{{QStringLiteral("content_id"), 1036}, {QStringLiteral("territory_id"), 1036}}, 2),
        event("PLAYER_JOB", "PARSED", QJsonObject{{QStringLiteral("job_id"), 24}}, 3),
        event("DUTY_RESULT", "PARSED", QJsonValue::Null, 4),
        event("PROCESS_RESTART", "SYNTHETIC", QJsonValue::Null, 5),
    };
}

QJsonObject run()
{
    return {{QStringLiteral("run_id"), kRunId},
            {QStringLiteral("revision"), 3},
            {QStringLiteral("matched_at_utc"), QStringLiteral("2026-09-04T12:39:05.125Z")},
            {QStringLiteral("entered_at_utc"), QStringLiteral("2026-09-04T12:41:00.000Z")},
            {QStringLiteral("ended_at_utc"), QStringLiteral("2026-09-04T13:11:34.000Z")},
            {QStringLiteral("duration_ms"), 1834000},
            {QStringLiteral("content_id"), 1037},
            {QStringLiteral("duty_name"), QString::fromUtf8("天然要害沙斯塔夏溶洞")},
            {QStringLiteral("job_id"), 19},
            {QStringLiteral("job_name"), QString::fromUtf8("骑士")},
            {QStringLiteral("role"), QStringLiteral("TANK")},
            {QStringLiteral("result"), QStringLiteral("COMPLETED")},
            {QStringLiteral("source"), QStringLiteral("AUTO_NETWORK")},
            {QStringLiteral("detection_confidence"), QStringLiteral("LOW")},
            {QStringLiteral("contributes_to_goal"), true},
            {QStringLiteral("pending_review"), false},
            {QStringLiteral("soft_deleted"), false}};
}

/// Answers the reads the history page makes with the fixtures above.
class PanelBackend final : public mr::IBackend
{
public:
    QString backendName() const override { return QStringLiteral("mock"); }
    bool isConnected() const override { return true; }

    mr::BackendReply *request(const QString &type, const QJsonObject & = {}) override
    {
        auto *reply = new mr::BackendReply(type, type, this);
        QJsonObject answer;
        if (type == QLatin1String("QueryRuns")) {
            answer = {{QStringLiteral("items"), QJsonArray{runItem}},
                      {QStringLiteral("page_info"),
                       QJsonObject{{QStringLiteral("page"), 1}, {QStringLiteral("total"), 1}}}};
        } else if (type == QLatin1String("GetRunRevisions")) {
            answer = {{QStringLiteral("items"), revisionItems},
                      {QStringLiteral("page_info"),
                       QJsonObject{{QStringLiteral("page"), 1},
                                   {QStringLiteral("total"), int(revisionItems.size())}}}};
        } else if (type == QLatin1String("GetRunEvents")) {
            answer = {{QStringLiteral("run_id"), kRunId}, {QStringLiteral("events"), events()}};
        } else if (type == QLatin1String("PreviewRunImport")) {
            if (!importErrorMessage.isEmpty()) {
                QTimer::singleShot(0, reply, [reply, message = importErrorMessage] {
                    reply->fail(QStringLiteral("ERR_BAD_REQUEST"), message);
                });
                return reply;
            }
            answer = {{QStringLiteral("preview_id"), QStringLiteral("literal-text-preview")},
                      {QStringLiteral("rows"), QJsonArray{QJsonObject{
                          {QStringLiteral("row_number"), 1}, {QStringLiteral("status"), QStringLiteral("new")},
                          {QStringLiteral("can_import"), true}, {QStringLiteral("incomplete"), true},
                          {QStringLiteral("candidate"), importCandidate},
                          {QStringLiteral("errors"), QJsonArray{}}, {QStringLiteral("warnings"), QJsonArray{}}}}},
                      {QStringLiteral("summary"), QJsonObject{{QStringLiteral("total"), 1},
                          {QStringLiteral("new"), 1}, {QStringLiteral("incomplete"), 1}}}};
        }
        QTimer::singleShot(0, reply, [reply, answer] { reply->succeed(answer); });
        return reply;
    }

    /// What GetRunRevisions answers with.
    QJsonArray revisionItems = revisions();
    QJsonObject runItem = run();
    QJsonObject importCandidate;
    QString importErrorMessage;
};

QQuickItem *findItem(QQuickItem *from, const QString &name)
{
    if (!from)
        return nullptr;
    if (from->objectName() == name)
        return from;
    for (QQuickItem *child : from->childItems()) {
        if (QQuickItem *match = findItem(child, name))
            return match;
    }
    return nullptr;
}

QQuickItem *findTextItem(QQuickItem *from, const QString &text)
{
    if (!from)
        return nullptr;
    if (from->property("text").toString() == text && from->property("textFormat").isValid())
        return from;
    for (QQuickItem *child : from->childItems()) {
        if (auto *match = findTextItem(child, text))
            return match;
    }
    return nullptr;
}

void collectVisibleText(QQuickItem *item, QStringList &texts)
{
    if (!item || !item->isVisible())
        return;
    const QVariant text = item->property("text");
    if (text.isValid() && !text.toString().isEmpty())
        texts.append(text.toString());
    for (QQuickItem *child : item->childItems())
        collectVisibleText(child, texts);
}

struct HistoryScene {
    PanelBackend backend;
    std::unique_ptr<mr::AppController> controller;
    mr::Formatters formatters;
    mr::JobCatalog jobs;
    mr::RoleCatalog roles;
    mr::RunFormValidator validator;
    std::unique_ptr<QQmlEngine> engine;
    std::unique_ptr<QObject> root;
    QString errors;

    ~HistoryScene()
    {
        root.reset();
        engine.reset();
        controller.reset();
    }

    bool create(bool maintainer = false)
    {
        controller = std::make_unique<mr::AppController>(&backend, nullptr);
        controller->setMaintainerToolsVisible(maintainer);
        engine = std::make_unique<QQmlEngine>();
        QQmlContext *context = engine->rootContext();
        context->setContextProperty(QStringLiteral("App"), controller.get());
        context->setContextProperty(QStringLiteral("Fmt"), &formatters);
        context->setContextProperty(QStringLiteral("Jobs"), &jobs);
        context->setContextProperty(QStringLiteral("Roles"), &roles);
        context->setContextProperty(QStringLiteral("RunForm"), &validator);
        context->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QQmlComponent component(engine.get());
        component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 1180; height: 760; visible: true
    HistoryPage { objectName: "history"; anchors.fill: parent }
})", QUrl());
        root.reset(component.create());
        for (const auto &error : component.errors())
            errors += error.toString() + QLatin1Char('\n');
        return root != nullptr;
    }

    QQuickWindow *window() const { return qobject_cast<QQuickWindow *>(root.get()); }
    QObject *history() const { return root->findChild<QObject *>(QStringLiteral("history")); }
    QQuickItem *item(const QString &name) const { return findItem(window()->contentItem(), name); }
    QQuickItem *panel() const { return item(QStringLiteral("runDetailPanel")); }

    /// The page's first row is selected and its revisions have arrived.
    bool openRun()
    {
        if (!QTest::qWaitFor([&] { return controller->runs()->rowCount() > 0; }, 3000))
            return false;
        controller->selectRun(run().toVariantMap());
        return QTest::qWaitFor([&] { return controller->selectedRunRevisions().size() == 3; }, 3000)
               && panel() && panel()->isVisible();
    }

    bool showTab(const QString &tab)
    {
        history()->setProperty("detailTab", tab);
        QTest::qWait(50);
        return panel()->property("activeTab").toString() == tab;
    }

    QStringList panelTexts() const
    {
        QStringList texts;
        collectVisibleText(panel(), texts);
        return texts;
    }

    void click(QQuickItem *target, QPointF at = QPointF(-1, -1)) const
    {
        if (at.x() < 0)
            at = QPointF(target->width() / 2, target->height() / 2);
        QTest::mouseClick(window(), Qt::LeftButton, Qt::NoModifier, target->mapToScene(at).toPoint());
    }
};

/// Words a player must never read in the panel, with the field or token each
/// stands for. Matched as whole words, so 通关 can contain nothing of them.
const QStringList &wireVocabulary()
{
    static const QStringList words{
        QStringLiteral("CREATE_AUTO"), QStringLiteral("CREATE_MANUAL"), QStringLiteral("CORRECT"),
        QStringLiteral("USER"), QStringLiteral("SYSTEM"), QStringLiteral("UNKNOWN"),
        QStringLiteral("COMPLETED"), QStringLiteral("HEALER"), QStringLiteral("TANK"),
        QStringLiteral("AUTO_NETWORK"), QStringLiteral("MEDIUM"), QStringLiteral("LOW"),
        QStringLiteral("PARSED"), QStringLiteral("SYNTHETIC"), QStringLiteral("CN"),
        QStringLiteral("CONTENT_FINDER_POP"), QStringLiteral("ZONE_INITIALIZATION"),
        QStringLiteral("PLAYER_JOB"), QStringLiteral("DUTY_RESULT"), QStringLiteral("PROCESS_RESTART"),
        QStringLiteral("1834000"), QStringLiteral("0x02F5"), QStringLiteral("S2C")};
    return words;
}

void verifyNoWireVocabulary(const QStringList &texts)
{
    static const QRegularExpression identifier(QStringLiteral("[a-z]+_[a-z_]+"));
    for (const QString &text : texts) {
        const auto found = identifier.match(text);
        QVERIFY2(!found.hasMatch(),
                 qPrintable(QStringLiteral("field name \"%1\" in: %2").arg(found.captured(), text)));
        for (const QString &word : wireVocabulary()) {
            const QRegularExpression whole(QStringLiteral("(^|[^A-Za-z0-9_])%1($|[^A-Za-z0-9_])")
                                               .arg(QRegularExpression::escape(word)));
            QVERIFY2(!whole.match(text).hasMatch(),
                     qPrintable(QStringLiteral("token \"%1\" in: %2").arg(word, text)));
        }
        QVERIFY2(!text.contains(QStringLiteral("cn-2026")), qPrintable(text));
    }
}

} // namespace

class RunDetailPanelTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void initTestCase()
    {
        const QString qmlRoot = QString::fromUtf8(MR_DESKTOP_QML_DIR);
        QVERIFY(QDir(qmlRoot).exists());
        qmlRegisterSingletonType(QUrl::fromLocalFile(qmlRoot + QStringLiteral("/Theme.qml")),
                                 "MentorRecorder", 1, 0, "Theme");
        for (const auto &directory : {QStringLiteral("/components"), QStringLiteral("/dialogs"),
                                      QStringLiteral("/pages")}) {
            for (const auto &file :
                 QDir(qmlRoot + directory).entryList({QStringLiteral("*.qml")}, QDir::Files)) {
                const QByteArray name = file.chopped(4).toUtf8();
                qmlRegisterType(QUrl::fromLocalFile(qmlRoot + directory + QLatin1Char('/') + file),
                                "MentorRecorder", 1, 0, name.constData());
            }
        }
    }

    void richTextResourceProbeIsInterceptedWithoutNetworking()
    {
        ResourceRequestFactory factory;
        QQmlEngine engine;
        engine.setNetworkAccessManagerFactory(&factory);
        QQmlComponent component(&engine);
        component.setData("import QtQuick\nimport QtQuick.Controls\n"
            "ApplicationWindow { width: 320; height: 200; visible: true; "
            "Text { textFormat: Text.RichText; "
            "text: '<img src=\"http://127.0.0.1:9/literal-probe.png\" width=\"10\" height=\"10\">' } }", QUrl());
        std::unique_ptr<QObject> root(component.create());
        QVERIFY2(root, qPrintable(component.errorString()));
        QTRY_COMPARE(factory.requests.load(), 1);
        // The fake manager never calls the platform implementation, so the
        // positive control verifies detection without a loopback or external connection.
    }

    void storedAndImportedRecordTextStaysLiteralWithoutResourceRequests_data()
    {
        QTest::addColumn<QString>("view");
        for (const char *view : {"recent-body", "recent-duty", "detail-note", "detail-reflection",
                                 "history-duty", "import-preview", "import-error-tooltip"})
            QTest::newRow(view) << QString::fromLatin1(view);
    }

    void storedAndImportedRecordTextStaysLiteralWithoutResourceRequests()
    {
        QFETCH(QString, view);
        // A unique resource key per row avoids a prior QML image-cache entry
        // hiding a regression. Nothing receives these URLs: the factory blocks them.
        const QString markup = QStringLiteral("<img src=\"http://127.0.0.1:9/%1.png\" width=\"30\" height=\"20\"> <b>原样 & 文字</b>").arg(view);
        const QJsonObject reflection{{QStringLiteral("text"), markup}, {QStringLiteral("mood"), QStringLiteral("ok")}};
        PanelBackend backend;
        if (view == QLatin1String("import-error-tooltip"))
            backend.importErrorMessage = markup;
        backend.runItem.insert(QStringLiteral("note"), markup);
        backend.runItem.insert(QStringLiteral("reflection"), reflection);
        backend.runItem.insert(QStringLiteral("duty_name"), markup);
        backend.importCandidate = {{QStringLiteral("duty_name"), QStringLiteral("合成副本")},
                                   {QStringLiteral("job_name"), QStringLiteral("合成职业")},
                                   {QStringLiteral("reflection_text"), markup}};
        mr::AppController controller(&backend, nullptr);
        mr::ImportRecordsController importer;
        importer.setBackend(&backend);
        mr::Formatters formatters;
        mr::JobCatalog jobs;
        mr::RoleCatalog roles;
        ResourceRequestFactory factory;
        QQmlEngine engine;
        engine.setNetworkAccessManagerFactory(&factory);
        auto *context = engine.rootContext();
        context->setContextProperty(QStringLiteral("App"), &controller);
        context->setContextProperty(QStringLiteral("Fmt"), &formatters);
        context->setContextProperty(QStringLiteral("Jobs"), &jobs);
        context->setContextProperty(QStringLiteral("Roles"), &roles);
        context->setContextProperty(QStringLiteral("ImportRecords"), &importer);
        context->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QByteArray item;
        if (view.startsWith(QLatin1String("recent-")))
            item = "ReflectionCard { objectName: \"view\"; width: 900 }";
        else if (view.startsWith(QLatin1String("detail-")))
            item = "RunDetailPanel { objectName: \"view\"; width: 600; height: 650 }";
        else if (view == QLatin1String("history-duty"))
            item = "HistoryPage { objectName: \"view\"; anchors.fill: parent }";
        else
            item = "ImportRecordsDialog { objectName: \"view\" }";
        QQmlComponent component(&engine);
        component.setData("import QtQuick\nimport QtQuick.Controls\nimport MentorRecorder\n"
                          "ApplicationWindow { width: 1180; height: 760; visible: true; " + item + " }", QUrl());
        std::unique_ptr<QObject> root(component.create());
        QVERIFY2(root, qPrintable(component.errorString()));
        auto *window = qobject_cast<QQuickWindow *>(root.get());
        QVERIFY(window);
        QObject *recordView = root->findChild<QObject *>(QStringLiteral("view"));
        QVERIFY(recordView);
        if (view.startsWith(QLatin1String("recent-"))) {
            auto record = backend.runItem;
            if (view == QLatin1String("recent-body"))
                record.insert(QStringLiteral("duty_name"), QStringLiteral("合成副本"));
            const QJsonObject body = view == QLatin1String("recent-duty")
                ? QJsonObject{{QStringLiteral("text"), QStringLiteral("合成正文")}} : reflection;
            recordView->setProperty("entry", QVariantMap{{QStringLiteral("run"), record.toVariantMap()},
                {QStringLiteral("reflection"), body.toVariantMap()}});
        } else if (view.startsWith(QLatin1String("detail-"))) {
            auto record = backend.runItem;
            record.insert(QStringLiteral("duty_name"), QStringLiteral("合成副本"));
            recordView->setProperty("runData", record.toVariantMap());
            recordView->setProperty("activeTab", view == QLatin1String("detail-note")
                ? QStringLiteral("info") : QStringLiteral("refl"));
        } else if (view == QLatin1String("import-preview")) {
            importer.importText(QStringLiteral("心得\n合成正文"));
            QTRY_VERIFY(importer.previewValid());
            QVERIFY(QMetaObject::invokeMethod(recordView, "open"));
        } else if (view == QLatin1String("import-error-tooltip")) {
            importer.importText(QStringLiteral("心得\n合成正文"));
            QTRY_VERIFY(!importer.errorText().isEmpty());
            QVERIFY(QMetaObject::invokeMethod(recordView, "open"));
            QObject *tip = nullptr;
            QTRY_VERIFY((tip = recordView->findChild<QObject *>(QStringLiteral("importErrorToolTip"))) != nullptr);
            tip->setProperty("visible", true);
            QQuickItem *content = nullptr;
            QTRY_VERIFY((content = tip->property("contentItem").value<QQuickItem *>()) != nullptr);
            QCOMPARE(content->property("text").toString(), markup);
            const auto format = content->metaObject()->property(content->metaObject()->indexOfProperty("textFormat"));
            QCOMPARE(content->property("textFormat").toInt(), format.enumerator().keyToValue("PlainText"));
        }
        const QString shown = view == QLatin1String("import-preview")
            ? QStringLiteral("合成职业 · ") + markup : markup;
        QQuickItem *textItem = nullptr;
        QTRY_VERIFY((textItem = findTextItem(window->contentItem(), shown)) != nullptr);
        QVERIFY(textItem->isVisible());
        QCOMPARE(textItem->property("text").toString(), shown);
        const auto format = textItem->metaObject()->property(textItem->metaObject()->indexOfProperty("textFormat"));
        QCOMPARE(textItem->property("textFormat").toInt(), format.enumerator().keyToValue("PlainText"));
        QTest::qWait(150);
        QVERIFY(textItem->width() > 0 && textItem->height() > 0);
        QCOMPARE(factory.requests.load(), 0);
    }

    // 审查 OI-3 / OL-7：逐一走过采集服务在创建时写入修订的每个字段
    // （DescribeCreation 与 InitialChanges）和修正的差异字段：玩家能看到的字段
    // 有中文名、按字段格式化的值；其余是维护者的事。
    void everyRevisionFieldIsEitherWordedOrMaintainerOnly_data()
    {
        QTest::addColumn<QString>("field");
        QTest::addColumn<QVariant>("value");
        QTest::addColumn<bool>("visible");
        QTest::addColumn<QString>("shown");
        const QVariant noValue;
        // Identifiers and bookkeeping: maintainer material.
        for (const char *field : {"run_id", "revision", "capture_session_id", "region",
                                  "protocol_profile_id", "mentor_roulette_id", "content_id",
                                  "territory_id", "job_id", "manually_created", "manually_corrected",
                                  "created_at_utc", "updated_at_utc", "duty_identity",
                                  "a_field_from_a_newer_collector"}) {
            QTest::newRow(field) << QString::fromLatin1(field) << noValue << false << QString();
        }
        // What a player reads, in the player's words.
        QTest::newRow("result") << "result" << QVariant(QStringLiteral("COMPLETED")) << true
                                << QString::fromUtf8("通关");
        QTest::newRow("result unknown") << "result" << QVariant(QStringLiteral("UNKNOWN")) << true
                                        << QString::fromUtf8("未知");
        QTest::newRow("role") << "role" << QVariant(QStringLiteral("HEALER")) << true
                              << QString::fromUtf8("治疗");
        QTest::newRow("source") << "source" << QVariant(QStringLiteral("AUTO_NETWORK")) << true
                                << QString::fromUtf8("自动");
        QTest::newRow("detection_confidence") << "detection_confidence"
                                              << QVariant(QStringLiteral("MEDIUM")) << true
                                              << QString::fromUtf8("中");
        QTest::newRow("duration_ms") << "duration_ms" << QVariant(1834000) << true
                                     << QStringLiteral("30:34");
        QTest::newRow("duration_ms null") << "duration_ms" << noValue << true << QString::fromUtf8("空");
        QTest::newRow("contributes_to_goal") << "contributes_to_goal" << QVariant(true) << true
                                             << QString::fromUtf8("是");
        QTest::newRow("soft_deleted") << "soft_deleted" << QVariant(false) << true
                                      << QString::fromUtf8("否");
        QTest::newRow("pending_review") << "pending_review" << QVariant(true) << true
                                        << QString::fromUtf8("是");
        QTest::newRow("duty_name") << "duty_name" << QVariant(QString::fromUtf8("水晶塔")) << true
                                   << QString::fromUtf8("水晶塔");
        QTest::newRow("duty_category") << "duty_category" << QVariant(QString::fromUtf8("四人迷宫"))
                                       << true << QString::fromUtf8("四人迷宫");
        QTest::newRow("job_name") << "job_name" << QVariant(QString::fromUtf8("骑士")) << true
                                  << QString::fromUtf8("骑士");
        QTest::newRow("note") << "note" << QVariant(QString::fromUtf8("讲了三次分摊")) << true
                              << QString::fromUtf8("讲了三次分摊");
        for (const char *field : {"matched_at_utc", "entered_at_utc", "ended_at_utc"}) {
            QTest::newRow(field) << QString::fromLatin1(field)
                                 << QVariant(QStringLiteral("2026-09-04T12:39:05.125Z")) << true
                                 << mr::Formatters::localDateTime(QStringLiteral("2026-09-04T12:39:05.125Z"));
        }
    }

    void everyRevisionFieldIsEitherWordedOrMaintainerOnly()
    {
        QFETCH(QString, field);
        QFETCH(QVariant, value);
        QFETCH(bool, visible);
        QFETCH(QString, shown);
        QCOMPARE(mr::Formatters::revisionFieldVisible(field), visible);
        if (!visible)
            return;
        const QString label = mr::Formatters::fieldLabel(field);
        QVERIFY2(!label.contains(QRegularExpression(QStringLiteral("[A-Za-z_]"))), qPrintable(label));
        QCOMPARE(mr::Formatters::revisionFieldValue(field, value), shown);
    }

    void changeKindsActorsAndEventsHaveChineseNames()
    {
        for (const char *kind : {"CREATE_AUTO", "CREATE_MANUAL", "CORRECT", "SOFT_DELETE", "RESTORE",
                                 "IMPORT", "SOMETHING_NEWER"}) {
            const QString label = mr::Formatters::changeKindLabel(QString::fromLatin1(kind));
            QVERIFY2(!label.isEmpty() && !label.contains(QRegularExpression(QStringLiteral("[A-Za-z_]"))),
                     kind);
        }
        QCOMPARE(mr::Formatters::changeKindLabel(QStringLiteral("CREATE_AUTO")), QString::fromUtf8("自动记录"));
        QCOMPARE(mr::Formatters::changeKindLabel(QStringLiteral("CORRECT")), QString::fromUtf8("修正"));
        for (const char *actor : {"USER", "SYSTEM", ""}) {
            const QString label = mr::Formatters::revisionActorLabel(QString::fromLatin1(actor));
            QVERIFY2(!label.isEmpty() && !label.contains(QRegularExpression(QStringLiteral("[A-Za-z_]"))),
                     actor);
        }
        QVERIFY(mr::Formatters::revisionActorLabel(QStringLiteral("USER"))
                != mr::Formatters::revisionActorLabel(QStringLiteral("SYSTEM")));
        // Every event type Domain/Events/SemanticEvent.cs and crash recovery write.
        for (const char *type : {"CONTENT_FINDER_POP", "ZONE_INITIALIZATION", "ZONE_TERRITORY",
                                 "DUTY_RESULT", "PLAYER_JOB", "ZONE_LEFT", "INSTANCE_LEFT",
                                 "CONNECTION_LOST", "CAPTURE_STOPPED", "EVENT_SEQUENCE_GAP",
                                 "MATCH_ANNOUNCED", "MATCH_CANCELLED", "TIMEOUT_TICK",
                                 "PROCESS_RESTART", "PROFILE_LOST", "SOMETHING_NEWER"}) {
            const QString label = mr::Formatters::runEventLabel(QString::fromLatin1(type));
            QVERIFY2(!label.isEmpty() && !label.contains(QRegularExpression(QStringLiteral("[A-Za-z_]"))),
                     type);
        }
        // The capture page's names, where the two vocabularies overlap.
        QCOMPARE(mr::Formatters::runEventLabel(QStringLiteral("DUTY_RESULT")),
                 mr::Formatters::eventKindLabel(QStringLiteral("DUTY_RESULT")));
    }

    // 审查 OI-3 / OL-7：修正历史对玩家只说中文；维护者仍看原始字段与取值。
    void theRevisionTabSpeaksToPlayersInWords()
    {
        HistoryScene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QVERIFY(scene.openRun());
        QVERIFY(scene.showTab(QStringLiteral("revs")));
        QTRY_VERIFY(scene.panelTexts().join(QLatin1Char('\n')).contains(QString::fromUtf8("用户确认通关")));
        const QStringList texts = scene.panelTexts();
        verifyNoWireVocabulary(texts);
        const QString page = texts.join(QLatin1Char('\n'));
        QVERIFY2(page.contains(QString::fromUtf8("自动记录")), qPrintable(page));
        QVERIFY(page.contains(QString::fromUtf8("通关")));
        QVERIFY(page.contains(QStringLiteral("30:34")));
        QVERIFY(page.contains(QString::fromUtf8("骑士")));
    }

    void maintainersReadTheRawRevisions()
    {
        HistoryScene scene;
        QVERIFY2(scene.create(true), qPrintable(scene.errors));
        QVERIFY(scene.openRun());
        QVERIFY(scene.showTab(QStringLiteral("revs")));
        QTRY_VERIFY(scene.panelTexts().join(QLatin1Char('\n')).contains(QStringLiteral("CREATE_AUTO")));
        const QString page = scene.panelTexts().join(QLatin1Char('\n'));
        QVERIFY(page.contains(QStringLiteral("protocol_profile_id")));
        QVERIFY(page.contains(QStringLiteral("SYSTEM")));
    }

    // 审查 OL-6 / OL-7：采集服务只发 PARSED / SYNTHETIC / UNKNOWN，正常解析的事件
    // 此前每一行都挂着红色的「PARSED」；事件名与解析字段也是原样的线上词汇。
    void theEventTabNamesEventsAndFlagsNothingNormal()
    {
        HistoryScene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QVERIFY(scene.openRun());
        QVERIFY(scene.showTab(QStringLiteral("events")));
        QTRY_COMPARE(scene.controller->runEventsState(), QStringLiteral("ready"));
        QTRY_VERIFY(scene.panelTexts().join(QLatin1Char('\n')).contains(QString::fromUtf8("副本结算")));
        const QStringList texts = scene.panelTexts();
        verifyNoWireVocabulary(texts);
        const QString page = texts.join(QLatin1Char('\n'));
        QVERIFY(page.contains(QString::fromUtf8("匹配成功")));
        QVERIFY(!page.contains(QStringLiteral("roulette_id")));
    }

    // Why a run waits for review, as far as the Desktop can know it. $defs/Run carries
    // only the flag; a reason exists only on a system revision that set it (crash
    // recovery, or a withdrawn calibration's FlagRecords, which keeps the result).
    // The flag the state machine sets as a duty ends leaves no revision.
    void aPendingRunExplainsWhyItIsPending_data()
    {
        QTest::addColumn<QString>("result");
        QTest::addColumn<bool>("flaggedBySystem");
        QTest::addColumn<QString>("shown");
        // No result was observed: the duty was left without the clear signal
        // (docs/protocol-profile-format.md §12), or a restart closed it.
        QTest::newRow("unknown") << "UNKNOWN" << false
            << QString::fromUtf8("是 · 结果未经确认（崩溃恢复，或离开副本前未收到通关结算）");
        // Completed by the clear, flagged later because the calibration that recorded it
        // was withdrawn: the clear WAS received, and the system revision says why.
        QTest::newRow("completed-flagged") << "COMPLETED" << true
            << QString::fromUtf8("是 · 需要你确认这条记录（原因见修正历史）");
        // A known result flagged as the duty ended (a match lost before entry): no
        // revision names a reason, so none is guessed.
        QTest::newRow("cancelled-unexplained") << "CANCELLED_BEFORE_ENTRY" << false
            << QString::fromUtf8("是 · 需要你确认这条记录");
    }

    void aPendingRunExplainsWhyItIsPending()
    {
        QFETCH(QString, result);
        QFETCH(bool, flaggedBySystem);
        QFETCH(QString, shown);

        HistoryScene scene;
        QJsonArray trail = revisions();
        if (flaggedBySystem) {
            trail.append(QJsonObject{
                {QStringLiteral("revision_id"), QStringLiteral("44444444-4444-4444-8444-444444444444")},
                {QStringLiteral("run_id"), kRunId},
                {QStringLiteral("revision"), 4},
                {QStringLiteral("changed_at_utc"), QStringLiteral("2026-09-06T08:00:00.000Z")},
                {QStringLiteral("change_kind"), QStringLiteral("CORRECT")},
                {QStringLiteral("actor"), QStringLiteral("SYSTEM")},
                {QStringLiteral("reason"),
                 QString::fromUtf8("这条记录由其他玩家分享的校准生成，该校准已在公开仓库里被撤回，记录标记待复核。")},
                {QStringLiteral("changes"), QJsonArray{change("pending_review", false, true)}}});
        }
        scene.backend.revisionItems = trail;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QVERIFY(QTest::qWaitFor([&] { return scene.controller->runs()->rowCount() > 0; }, 3000));
        QJsonObject pending = run();
        pending.insert(QStringLiteral("result"), result);
        pending.insert(QStringLiteral("pending_review"), true);
        pending.insert(QStringLiteral("revision"), int(trail.size()));
        scene.controller->selectRun(pending.toVariantMap());
        QTRY_COMPARE(scene.controller->selectedRunRevisions().size(), int(trail.size()));
        QTRY_VERIFY(scene.panel() && scene.panel()->isVisible());
        QVERIFY(scene.panel()->property("pendingReview").toBool());

        // The 待复核 row of the 信息 tab, as the player reads it.
        const auto pendingRow = [&scene] {
            for (const QString &text : scene.panelTexts()) {
                if (text.startsWith(QString::fromUtf8("是 · ")))
                    return text;
            }
            return QString();
        };
        QTRY_COMPARE(pendingRow(), shown);
    }

    void maintainersReadTheRawEvents()
    {
        HistoryScene scene;
        QVERIFY2(scene.create(true), qPrintable(scene.errors));
        QVERIFY(scene.openRun());
        QVERIFY(scene.showTab(QStringLiteral("events")));
        QTRY_VERIFY(scene.panelTexts().join(QLatin1Char('\n')).contains(QStringLiteral("CONTENT_FINDER_POP")));
        const QString page = scene.panelTexts().join(QLatin1Char('\n'));
        QVERIFY(page.contains(QStringLiteral("roulette_id")));
        QVERIFY(page.contains(QStringLiteral("SYNTHETIC")));
    }

    // 审查 OL-4：面板本身不接收按下，页签的 TapHandler 只拿被动抓取，于是一次点击
    // 继续落到页面的「点外面关闭」区域与下面的行上：面板关掉，另外三个页签点不到。
    void aClickOnThePanelStaysInThePanel()
    {
        HistoryScene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
        QVERIFY(scene.openRun());
        QTest::qWait(100);
        for (const char *tab : {"events", "revs", "refl", "info"}) {
            QQuickItem *target = scene.item(QStringLiteral("detailTab_") + QLatin1String(tab));
            QVERIFY2(target, tab);
            scene.click(target);
            QTest::qWait(50);
            QVERIFY2(scene.controller->hasSelection(), tab);
            QCOMPARE(scene.history()->property("detailTab").toString(), QString::fromLatin1(tab));
        }
        // A gap in the panel - the header's margin, the body's text - is not "outside"
        // either, and the history row under the panel does not take it.
        for (const qreal y : {qreal(6), scene.panel()->height() * 0.4, scene.panel()->height() * 0.6}) {
            scene.click(scene.panel(), QPointF(scene.panel()->width() / 2, y));
            QTest::qWait(50);
            QVERIFY2(scene.controller->hasSelection(), qPrintable(QString::number(y)));
            QCOMPARE(scene.history()->property("detailTab").toString(), QStringLiteral("info"));
        }
    }
};

int main(int argc, char *argv[])
{
    // Never run against the user's own Collector, database or serve lease - see
    // TestCollectorGuard.h. Must precede any construction.
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication app(argc, argv);
    RunDetailPanelTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "RunDetailPanelTests.moc"
