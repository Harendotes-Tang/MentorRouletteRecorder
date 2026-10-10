#include "TestCollectorGuard.h"
#include "AppController.h"
#include "ExportController.h"
#include "HistoryController.h"
#include "IBackend.h"
#include "MockBackend.h"
#include "NoteImageStore.h"
#include "Formatters.h"
#include "JobCatalog.h"
#include "RoleCatalog.h"
#include "RunFormValidator.h"

#include <QDir>
#include <QFile>
#include <QGuiApplication>
#include <QJsonArray>
#include <QJSValue>
#include <QPointer>
#include <QProcess>
#include <QScopeGuard>
#include <QSignalSpy>
#include <QTemporaryDir>
#include <QTest>
#include <QTimer>
#include <QTimeZone>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickWindow>
#include <QQuickStyle>
#include <memory>

namespace {
const QString firstId = QStringLiteral("00000000-0000-0000-0000-000000000001");
const QString secondId = QStringLiteral("00000000-0000-0000-0000-000000000002");
QVariantMap row(const QString &id, int revision, bool deleted = false)
{
    return {{QStringLiteral("run_id"), id}, {QStringLiteral("revision"), revision},
            {QStringLiteral("soft_deleted"), deleted}, {QStringLiteral("duty_name"), QStringLiteral("测试副本")}};
}
class ControlledBackend final : public mr::IBackend {
public:
    struct Call { QString type; QJsonObject payload; QString id; QPointer<mr::BackendReply> reply; };
    QList<Call> calls;
    QString backendName() const override { return QStringLiteral("controlled"); }
    bool isConnected() const override { return true; }
    mr::BackendReply *request(const QString &type, const QJsonObject &payload = {}) override {
        auto *reply = new mr::BackendReply(QString::number(calls.size()), type, this);
        calls.append({type, payload, {}, reply});
        return reply;
    }
    mr::BackendReply *requestWithId(const QString &type, const QJsonObject &payload, const QString &id) override {
        auto *reply = request(type, payload); calls.last().id = id; return reply;
    }
    Call last(const QString &type) const {
        for (auto i = calls.crbegin(); i != calls.crend(); ++i) if (i->type == type) return *i;
        return {};
    }
    void completePage(const QVariantMap &run, int page = 1, int total = 2) {
        last(QStringLiteral("QueryRuns")).reply->succeed({
            {QStringLiteral("items"), QJsonArray{QJsonObject::fromVariantMap(run)}},
            {QStringLiteral("page_info"), QJsonObject{{QStringLiteral("page"), page}, {QStringLiteral("total"), total}}}});
    }
};
class CleanupBackend final : public mr::MockBackend {
public:
    QStringList pending;
    QList<QStringList> acknowledgements;
    bool failAck = false;
    mr::BackendReply *request(const QString &type, const QJsonObject &payload = {}) override {
        if (type != QLatin1String("GetPendingImageCleanup") && type != QLatin1String("AcknowledgeImageCleanup"))
            return MockBackend::request(type, payload);
        auto *reply = new mr::BackendReply(QStringLiteral("cleanup"), type, this);
        if (type == QLatin1String("GetPendingImageCleanup")) {
            QJsonArray ids; for (const QString &id : pending.mid(0, 2000)) ids.append(id);
            reply->succeed({{QStringLiteral("run_ids"), ids}});
        } else {
            QStringList ids;
            for (const auto &id : payload.value(QStringLiteral("run_ids")).toArray()) ids.append(id.toString());
            acknowledgements.append(ids);
            if (failAck) reply->fail(QStringLiteral("ERR_PIPE_CLOSED"), QStringLiteral("连接中断"));
            else {
                for (const QString &id : ids) pending.removeAll(id);
                reply->succeed({{QStringLiteral("acknowledged_count"), ids.size()}});
            }
        }
        return reply;
    }
};
bool putFile(const QString &path) {
    QFile file(path); return file.open(QIODevice::WriteOnly) && file.write("fixture") == 7;
}
QQuickItem *findVisual(QQuickItem *item, const QString &name) {
    if (item->objectName() == name) return item;
    for (auto *child : item->childItems()) if (auto *found = findVisual(child, name)) return found;
    return nullptr;
}
void clickItem(QQuickWindow *window, QQuickItem *item) {
    const QPoint point = item->mapToScene(QPointF(item->width() / 2, item->height() / 2)).toPoint();
    QTest::mouseClick(window, Qt::LeftButton, Qt::NoModifier, point);
}
}

class HistoryBatchTests final : public QObject {
    Q_OBJECT
private Q_SLOTS:
    void initTestCase() {
        const QString root = QString::fromUtf8(MR_DESKTOP_QML_DIR);
        qmlRegisterSingletonType(QUrl::fromLocalFile(root + QStringLiteral("/Theme.qml")), "MentorRecorder", 1, 0, "Theme");
        for (const QString &folder : {QStringLiteral("/components"), QStringLiteral("/dialogs")})
            for (const QString &file : QDir(root + folder).entryList({QStringLiteral("*.qml")}, QDir::Files))
                qmlRegisterType(QUrl::fromLocalFile(root + folder + QLatin1Char('/') + file), "MentorRecorder", 1, 0, file.chopped(4).toUtf8().constData());
        qmlRegisterType(QUrl::fromLocalFile(root + QStringLiteral("/pages/HistoryPage.qml")), "MentorRecorder", 1, 0, "HistoryPage");
    }
    void crossPageSelectionKeepsOriginalRevisionsAndConflictKeepsSelection() {
        ControlledBackend backend;
        mr::HistoryController history(&backend);
        history.runs()->setPageSize(1);
        QCOMPARE(backend.last(QStringLiteral("QueryRuns")).payload.value(QStringLiteral("sort")).toObject().value(QStringLiteral("field")).toString(), QStringLiteral("history_date"));
        backend.completePage(row(firstId, 4));
        history.setCurrentPageChecked(true);
        QVERIFY(history.allCurrentPageChecked());
        history.runs()->nextPage();
        backend.completePage(row(secondId, 7), 2);
        history.setCurrentPageChecked(true);
        QCOMPARE(history.checkedRunIds(), QStringList({firstId, secondId}));
        history.adoptRunRevisionFromEvent(QJsonObject::fromVariantMap(row(firstId, 9)));
        history.mutateCheckedRuns(QStringLiteral("soft_delete"), QStringLiteral("清理记录"));
        const auto sent = backend.last(QStringLiteral("BatchMutateRuns"));
        const auto targets = sent.payload.value(QStringLiteral("runs")).toArray();
        QCOMPARE(targets.size(), 2);
        QCOMPARE(targets[0].toObject().value(QStringLiteral("expected_revision")).toInt(), 4);
        QCOMPARE(targets[1].toObject().value(QStringLiteral("expected_revision")).toInt(), 7);
        history.clearCheckedRuns();
        QCOMPARE(history.checkedRunCount(), 2); // 请求进行中不允许选择变化。
        sent.reply->fail(QStringLiteral("ERR_REVISION_CONFLICT"), QStringLiteral("记录已更新"));
        QVERIFY(!history.batchRunning());
        QCOMPARE(history.checkedRunCount(), 2);
        QVERIFY(history.batchFeedback().contains(QStringLiteral("重新勾选")));
    }
    void uncertainBatchRetryKeepsRequestIdentity() {
        ControlledBackend backend;
        mr::HistoryController history(&backend);
        history.setRunChecked(row(firstId, 3), true);
        history.mutateCheckedRuns(QStringLiteral("soft_delete"), QStringLiteral("原因"));
        const auto first = backend.last(QStringLiteral("BatchMutateRuns"));
        first.reply->fail(QStringLiteral("ERR_PIPE_CLOSED"), QStringLiteral("结果未知"));
        history.mutateCheckedRuns(QStringLiteral("soft_delete"), QStringLiteral("原因"));
        const auto retried = backend.last(QStringLiteral("BatchMutateRuns"));
        QVERIFY(!first.id.isEmpty());
        QCOMPARE(retried.id, first.id);
        retried.reply->succeed({{QStringLiteral("changed_count"), 1}});
        QCOMPARE(history.checkedRunCount(), 0);
    }
    void filterChangesRequireExplicitConfirmation() {
        ControlledBackend backend;
        mr::HistoryController history(&backend);
        history.setRunChecked(row(firstId, 2), true);
        history.setHistoryFilter({{QStringLiteral("pending_review"), true}});
        QVERIFY(history.filterConfirmationPending());
        QCOMPARE(history.checkedRunCount(), 1);
        QVERIFY(backend.calls.isEmpty());
        history.confirmHistoryFilterChange(false);
        QVERIFY(!history.historyFilter().contains(QStringLiteral("pending_review")));
        QCOMPARE(history.checkedRunCount(), 1);
        history.setHistoryFilter({{QStringLiteral("pending_review"), true}});
        history.confirmHistoryFilterChange(true);
        QCOMPARE(history.checkedRunCount(), 0);
        QCOMPARE(history.historyFilter().value(QStringLiteral("date_field")).toString(), QStringLiteral("history_date"));
        QVERIFY(history.historyFilter().value(QStringLiteral("pending_review")).toBool());
        QVERIFY(backend.last(QStringLiteral("QueryRuns")).reply);
    }
    void exactExportIdsIgnoreOtherFiltersAndIncludeExplicitlySelectedDeletedRows() {
        ControlledBackend backend;
        QTemporaryDir dir;
        mr::ExportController exporter;
        exporter.setBackend(&backend);
        exporter.setTargetOverride(dir.path());
        exporter.setHistoryFilter({{QStringLiteral("text"), QStringLiteral("原筛选")}});
        exporter.setCheckedRunIds({firstId, secondId});
        exporter.exportCsv();
        const auto filter = backend.last(QStringLiteral("ExportCsv")).payload.value(QStringLiteral("filter")).toObject();
        QCOMPARE(filter.value(QStringLiteral("run_id")).toArray(), QJsonArray({firstId, secondId}));
        QVERIFY(filter.value(QStringLiteral("include_deleted")).toBool());
        QVERIFY(!filter.contains(QStringLiteral("text")));
        exporter.setCheckedRunIds({});
        exporter.exportJson();
        QCOMPARE(backend.last(QStringLiteral("ExportJson")).payload.value(QStringLiteral("filter")).toObject().value(QStringLiteral("text")).toString(), QStringLiteral("原筛选"));
    }
    void purgeAndRestoreRefuseLiveOrMixedSelectionBeforeSending() {
        ControlledBackend backend;
        mr::HistoryController history(&backend);
        history.setRunChecked(row(firstId, 1), true);
        history.setRunChecked(row(secondId, 2, true), true);
        history.mutateCheckedRuns(QStringLiteral("purge"), QStringLiteral("原因"));
        history.mutateCheckedRuns(QStringLiteral("restore"), QStringLiteral("原因"));
        history.mutateCheckedRuns(QStringLiteral("soft_delete"), QStringLiteral("原因"));
        QVERIFY(backend.calls.isEmpty());
        history.setRunChecked(row(firstId, 1), false);
        history.mutateCheckedRuns(QStringLiteral("purge"), QStringLiteral("原因"));
        QVERIFY(backend.last(QStringLiteral("BatchMutateRuns")).reply);
    }
    void retentionOnlyChangesAfterCollectorConfirmation() {
        ControlledBackend backend;
        mr::HistoryController history(&backend);
        history.refreshRetentionSettings();
        backend.last(QStringLiteral("GetHistoryRetentionSettings")).reply->succeed({{QStringLiteral("retention_days"), 30}});
        QCOMPARE(history.retentionDays(), 30);
        history.updateRetentionSettings(0);
        QCOMPARE(history.retentionDays(), 30);
        QVERIFY(history.retentionSaving());
        backend.last(QStringLiteral("UpdateHistoryRetentionSettings")).reply->succeed({{QStringLiteral("retention_days"), 0}});
        QCOMPARE(history.retentionDays(), 0);
        QVERIFY(!history.retentionSaving());
    }
    void sourceCalendarDayFilterKeepsBothRawDateFormatsInNegativeTimezone() {
        mr::MockBackend backend;
        QJsonArray fixtures;
        for (const QString &raw : {QStringLiteral("2024-09-24"), QStringLiteral("2024/9/24")}) {
            QJsonObject run = QJsonObject::fromVariantMap(row(raw.contains(QLatin1Char('/')) ? secondId : firstId, 1));
            run.insert(QStringLiteral("source"), QStringLiteral("IMPORT"));
            run.insert(QStringLiteral("import_metadata"), QJsonObject{{QStringLiteral("source_recorded_at"), raw}});
            fixtures.append(run);
        }
        QJsonObject instant = QJsonObject::fromVariantMap(row(QStringLiteral("00000000-0000-0000-0000-000000000003"), 1));
        instant.insert(QStringLiteral("matched_at_utc"), QStringLiteral("2024-09-24T02:00:00.000Z")); // UTC−4: Sep 23.
        fixtures.append(instant);
        backend.resetRuns(fixtures);
        QJsonObject filter{{QStringLiteral("date_field"), QStringLiteral("history_date")},
            {QStringLiteral("from_utc"), QStringLiteral("2024-09-24T04:00:00.000Z")},
            {QStringLiteral("to_utc"), QStringLiteral("2024-09-25T03:59:59.999Z")},
            {QStringLiteral("history_from_day"), QStringLiteral("2024-09-24")},
            {QStringLiteral("history_to_day"), QStringLiteral("2024-09-24")}};
        bool finished = false;
        bool success = false;
        QVariantMap result;
        auto collect = [&](bool ok, const QVariantMap &payload, const QString &, const QString &) {
            finished = true; success = ok; result = payload;
        };
        backend.request(QStringLiteral("QueryRuns"), {{QStringLiteral("filter"), filter}})->whenDone(this, collect);
        QTRY_VERIFY(finished);
        QVERIFY(success);
        QCOMPARE(result.value(QStringLiteral("items")).toList().size(), 2);
        filter.insert(QStringLiteral("run_id"), QJsonArray{secondId});
        finished = false;
        backend.request(QStringLiteral("QueryRuns"), {{QStringLiteral("filter"), filter}})->whenDone(this, collect);
        QTRY_VERIFY(finished);
        QCOMPARE(result.value(QStringLiteral("items")).toList().size(), 1);
        QCOMPARE(result.value(QStringLiteral("items")).toList().first().toMap().value(QStringLiteral("run_id")).toString(), secondId);
    }
    void importedMaintenanceRevisionCannotUndoEvenWithOnlyPendingFlagChange() {
        ControlledBackend backend;
        mr::HistoryController history(&backend);
        history.selectRun(row(firstId, 2));
        const QJsonObject revision{{QStringLiteral("revision"), 2}, {QStringLiteral("actor"), QStringLiteral("SYSTEM")},
            {QStringLiteral("reason"), QStringLiteral("依据导入来源审计维护历史身份；缺游戏时间保留待补充，不作为采集结果待复核。")},
            {QStringLiteral("changes"), QJsonArray{QJsonObject{{QStringLiteral("field"), QStringLiteral("pending_review")}}}}};
        backend.last(QStringLiteral("GetRunRevisions")).reply->succeed({{QStringLiteral("items"), QJsonArray{revision}}});
        QVERIFY(!history.selectedRunCanUndo());
    }
    void sourceCalendarDaySortUsesLocalMidnightWithoutChangingFilters_data() {
        QTest::addColumn<int>("dayOffset");
        QTest::addColumn<int>("hour");
        QTest::addColumn<bool>("sourceFirst");
        QTest::newRow("same-day-two-am") << 0 << 2 << true;
        QTest::newRow("previous-day-eleven-pm") << -1 << 23 << false;
        QTest::newRow("following-day-one-am") << 1 << 1 << true;
    }
    void sourceCalendarDaySortUsesLocalMidnightWithoutChangingFilters() {
        QFETCH(int, dayOffset);
        QFETCH(int, hour);
        QFETCH(bool, sourceFirst);
        mr::MockBackend backend;
        const QDate sourceDay(2024, 9, 24);
        QJsonObject source = QJsonObject::fromVariantMap(row(firstId, 1));
        source.insert(QStringLiteral("source"), QStringLiteral("IMPORT"));
        source.insert(QStringLiteral("import_metadata"), QJsonObject{{QStringLiteral("source_recorded_at"), QStringLiteral("2024/9/24")}});
        QJsonObject actual = QJsonObject::fromVariantMap(row(secondId, 1));
        actual.insert(QStringLiteral("entered_at_utc"), QDateTime(sourceDay.addDays(dayOffset), QTime(hour, 0),
            QTimeZone::systemTimeZone()).toUTC().toString(Qt::ISODateWithMs));
        backend.resetRuns({actual, source});
        bool finished = false, success = false;
        QVariantMap result;
        auto collect = [&](bool ok, const QVariantMap &payload, const QString &, const QString &) {
            finished = true; success = ok; result = payload;
        };
        for (bool ascending : {true, false}) {
            finished = false;
            const QJsonObject sort{{QStringLiteral("field"), QStringLiteral("history_date")},
                {QStringLiteral("direction"), ascending ? QStringLiteral("asc") : QStringLiteral("desc")}};
            backend.request(QStringLiteral("QueryRuns"), {{QStringLiteral("sort"), sort}})->whenDone(this, collect);
            QTRY_VERIFY(finished);
            QVERIFY(success);
            const QVariantList rows = result.value(QStringLiteral("items")).toList();
            QCOMPARE(rows.size(), 2);
            QCOMPARE(rows.first().toMap().value(QStringLiteral("run_id")).toString(), sourceFirst == ascending ? firstId : secondId);
        }
        // The default instant filter remains UTC-day based; only the comparison
        // key used by ORDER BY treats a raw calendar day as local midnight.
        finished = false;
        const QJsonObject filter{{QStringLiteral("date_field"), QStringLiteral("history_date")},
            {QStringLiteral("run_id"), QJsonArray{firstId}},
            {QStringLiteral("from_utc"), QStringLiteral("2024-09-24T00:00:00.000Z")},
            {QStringLiteral("to_utc"), QStringLiteral("2024-09-24T00:00:00.000Z")}};
        backend.request(QStringLiteral("QueryRuns"), {{QStringLiteral("filter"), filter}})->whenDone(this, collect);
        QTRY_VERIFY(finished);
        QVERIFY(success);
        QCOMPARE(result.value(QStringLiteral("items")).toList().size(), 1);
    }
    void cleanupRejectsTraversalAndUnexpectedDirectories() {
        QTemporaryDir dir;
        mr::NoteImageStore store(dir.filePath(QStringLiteral("images")));
        QVERIFY(QDir().mkpath(store.runDirectory(firstId) + QStringLiteral("/nested")));
        QVERIFY(putFile(store.runDirectory(firstId) + QStringLiteral("/kept.png")));
        QVERIFY(!store.cleanupRun(QStringLiteral("../escape")).value(QStringLiteral("ok")).toBool());
        QVERIFY(!store.cleanupRun(firstId).value(QStringLiteral("ok")).toBool());
        QVERIFY(QFileInfo::exists(store.runDirectory(firstId) + QStringLiteral("/kept.png")));
        QVERIFY(QDir(store.runDirectory(firstId)).rmdir(QStringLiteral("nested")));
        QVERIFY(store.cleanupRun(firstId).value(QStringLiteral("ok")).toBool());
        QVERIFY(QDir(store.rootDirectory()).exists());
        QVERIFY(!QDir(store.runDirectory(firstId)).exists());
        QVERIFY(store.cleanupRun(firstId).value(QStringLiteral("ok")).toBool());
    }
    void cleanupRejectsJunctionWithoutTouchingItsTarget() {
#ifdef Q_OS_WIN
        QTemporaryDir dir;
        mr::NoteImageStore store(dir.filePath(QStringLiteral("images")));
        const QString outside = dir.filePath(QStringLiteral("outside"));
        QVERIFY(QDir().mkpath(store.rootDirectory()));
        QVERIFY(QDir().mkpath(outside));
        QVERIFY(putFile(outside + QStringLiteral("/keep.png")));
        QProcess link;
        link.start(QStringLiteral("cmd.exe"), {QStringLiteral("/c"), QStringLiteral("mklink"), QStringLiteral("/J"), QDir::toNativeSeparators(store.runDirectory(firstId)), QDir::toNativeSeparators(outside)});
        if (!link.waitForFinished(10000) || link.exitCode() != 0) QSKIP("mklink /J unavailable");
        auto unlink = qScopeGuard([&] { QDir(store.rootDirectory()).rmdir(firstId); });
        QVERIFY(!store.cleanupRun(firstId).value(QStringLiteral("ok")).toBool());
        QVERIFY(QFileInfo::exists(outside + QStringLiteral("/keep.png")));
#else
        QSKIP("Windows junction regression");
#endif
    }
    void cleanupAcknowledgesOnlySuccessAndRetriesFailedAcknowledgement() {
        QTemporaryDir dir;
        mr::NoteImageStore store(dir.filePath(QStringLiteral("images")));
        QVERIFY(QDir().mkpath(store.runDirectory(firstId)));
        QVERIFY(QDir().mkpath(store.runDirectory(secondId) + QStringLiteral("/nested")));
        QVERIFY(putFile(store.runDirectory(firstId) + QStringLiteral("/note.png")));
        CleanupBackend backend;
        backend.pending = {firstId, secondId};
        backend.failAck = true;
        mr::AppController app(&backend, nullptr);
        app.setNoteImageStore(&store);
        QTRY_VERIFY(!app.historyImageCleanupRunning());
        QCOMPARE(backend.acknowledgements.size(), 1);
        QCOMPARE(backend.acknowledgements.first(), QStringList({firstId}));
        QCOMPARE(backend.pending.size(), 2);
        QVERIFY(app.historyImageCleanupFeedback().contains(QStringLiteral("确认状态未保存")));
        backend.failAck = false;
        QVERIFY(QDir(store.runDirectory(secondId)).rmdir(QStringLiteral("nested")));
        app.retryHistoryImageCleanup();
        QTRY_VERIFY(!app.historyImageCleanupRunning());
        QVERIFY(backend.pending.isEmpty());
        QVERIFY(app.historyImageCleanupFeedback().isEmpty());
    }
    void cleanupDrainsMoreThanOneFullQueuePage() {
        QTemporaryDir dir;
        mr::NoteImageStore store(dir.filePath(QStringLiteral("images")));
        CleanupBackend backend;
        for (int i = 1; i <= 2001; ++i)
            backend.pending.append(QStringLiteral("00000000-0000-0000-0000-%1").arg(i, 12, 16, QLatin1Char('0')));
        mr::AppController app(&backend, nullptr);
        app.setNoteImageStore(&store);
        QTRY_VERIFY_WITH_TIMEOUT(backend.pending.isEmpty(), 10000);
        QCOMPARE(backend.acknowledgements.size(), 2);
        QCOMPARE(backend.acknowledgements.first().size(), 2000);
        QCOMPARE(backend.acknowledgements.last().size(), 1);
        QVERIFY(!app.historyImageCleanupRunning());
    }
    void nativeFiltersConfirmationSelectionAndDateProjection_data() {
        QTest::addColumn<bool>("dark"); QTest::addColumn<bool>("narrow");
        QTest::newRow("normal-light") << false << false;
        QTest::newRow("normal-dark") << true << false;
        QTest::newRow("narrow-light") << false << true;
        QTest::newRow("narrow-dark") << true << true;
    }
    void nativeFiltersConfirmationSelectionAndDateProjection() {
        QFETCH(bool, dark); QFETCH(bool, narrow);
        mr::MockBackend backend;
        mr::AppController app(&backend, nullptr);
        app.setThemeMode(dark ? QStringLiteral("dark") : QStringLiteral("light"));
        mr::Formatters formatters; mr::JobCatalog jobs; mr::RoleCatalog roles; mr::RunFormValidator validator;
        QQmlEngine engine;
        engine.rootContext()->setContextProperty(QStringLiteral("App"), &app);
        engine.rootContext()->setContextProperty(QStringLiteral("Fmt"), &formatters);
        engine.rootContext()->setContextProperty(QStringLiteral("Jobs"), &jobs);
        engine.rootContext()->setContextProperty(QStringLiteral("Roles"), &roles);
        engine.rootContext()->setContextProperty(QStringLiteral("RunForm"), &validator);
        engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QStringList warnings;
        connect(&engine, &QQmlEngine::warnings, this, [&warnings](const QList<QQmlError> &errors) { for (const auto &error : errors) warnings.append(error.toString()); });
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    visible: true; width: 1140; height: 790; color: Theme.windowBackground
    HistoryPage { objectName: "historyUnderTest"; anchors.fill: parent; anchors.margins: 24 }
    RunDetailPanel { objectName: "detailFacts"; visible: false; width: 380; height: 600 }
})", QUrl());
        std::unique_ptr<QObject> root(component.create());
        QVERIFY2(root, qPrintable(component.errorString()));
        auto *window = qobject_cast<QQuickWindow *>(root.get());
        window->resize(narrow ? QSize(920, 760) : QSize(1140, 790));
        QVERIFY(QTest::qWaitForWindowExposed(window));
        QTRY_VERIFY(!app.runs()->isLoading() && app.runs()->rowCount() > 0);
        auto *page = findVisual(window->contentItem(), QStringLiteral("historyUnderTest"));
        QVERIFY(page);
        auto *detail = findVisual(window->contentItem(), QStringLiteral("detailFacts"));
        QVERIFY(detail);
        for (const QString &source : {QStringLiteral("IMPORT"), QStringLiteral("AUTO_NETWORK")}) {
            const QVariantMap missing{{QStringLiteral("source"), source}, {QStringLiteral("result"), QStringLiteral("COMPLETED")}};
            QVariant returned;
            const QVariant input = missing;
            QVERIFY(QMetaObject::invokeMethod(page, "importIncomplete", Q_RETURN_ARG(QVariant, returned), Q_ARG(QVariant, input)));
            QCOMPARE(returned.toBool(), source == QLatin1String("IMPORT"));
            detail->setProperty("runData", missing);
            QCOMPARE(detail->property("importIncomplete").toBool(), source == QLatin1String("IMPORT"));
            bool hasSupplement = false;
            QVariant fields = detail->property("infoFields");
            if (fields.metaType() == QMetaType::fromType<QJSValue>()) fields = fields.value<QJSValue>().toVariant();
            for (const QVariant &field : fields.toList())
                hasSupplement |= field.toMap().value(QStringLiteral("v")).toString() == QStringLiteral("待补充");
            QCOMPARE(hasSupplement, source == QLatin1String("IMPORT"));
        }
        for (const QString &raw : {QStringLiteral("2024-09-24"), QStringLiteral("2024/9/24")}) {
            const QVariantMap source{{QStringLiteral("source_recorded_at"), raw}};
            const QVariant input = QVariantMap{{QStringLiteral("import_metadata"), source}};
            QVariant returned;
            QVERIFY(QMetaObject::invokeMethod(page, "dateLabel", Q_RETURN_ARG(QVariant, returned), Q_ARG(QVariant, input)));
            QCOMPARE(returned.toString(), QStringLiteral("原站\n2024-09-24"));
        }
        auto *more = findVisual(window->contentItem(), QStringLiteral("historyMoreFiltersButton"));
        auto *pending = root->findChild<QQuickItem *>(QStringLiteral("pendingReviewChip"));
        QVERIFY(more && pending);
        QCOMPARE(more->isVisible(), narrow);
        auto *mark = findVisual(window->contentItem(), QStringLiteral("historyMarkFilter"));
        auto *selectAll = findVisual(window->contentItem(), QStringLiteral("selectCurrentHistoryPage"));
        auto *count = findVisual(window->contentItem(), QStringLiteral("historyCheckedCount"));
        auto *previous = findVisual(window->contentItem(), QStringLiteral("historyPreviousPage"));
        QVERIFY(mark && selectAll && count && previous);
        QTRY_VERIFY(mark->isVisible() && selectAll->isVisible());
        QCOMPARE(selectAll->parentItem(), mark->parentItem());
        QTRY_COMPARE(selectAll->y(), 0.0);
        QCOMPARE(count->parentItem(), previous->parentItem());
        QVERIFY(count->x() < previous->x());
        QVERIFY(!pending->isVisible());
        const QVariantMap run = app.runs()->runAt(0);
        auto *checkbox = findVisual(window->contentItem(), QStringLiteral("historyRunCheck_") + run.value(QStringLiteral("run_id")).toString());
        QVERIFY(checkbox);
        clickItem(window, checkbox);
        QTRY_COMPARE(app.checkedHistoryRunCount(), 1);
        QVERIFY(!app.hasSelection()); // 勾选不应打开详情遮住批量操作。
        const QString directory = qEnvironmentVariable("MR_HISTORY_UI_EVIDENCE_DIR", QDir::currentPath());
        QVERIFY(QDir().mkpath(directory));
        QTest::qWait(50);
        QVERIFY(window->grabWindow().save(directory + QStringLiteral("/history-") + QString::fromLatin1(QTest::currentDataTag()) + QStringLiteral(".png")));
        clickItem(window, mark);
        QTRY_VERIFY(pending->isVisible());
        clickItem(window, pending);
        QTRY_VERIFY(app.historyFilterConfirmationPending());
        auto *keep = findVisual(window->contentItem(), QStringLiteral("historyKeepSelection"));
        QTRY_VERIFY(keep && keep->isVisible());
        QVERIFY(keep->hasActiveFocus());
        QTest::keyClick(window, Qt::Key_Escape);
        QTRY_VERIFY(!app.historyFilterConfirmationPending());
        QCOMPARE(app.checkedHistoryRunCount(), 1);
        QVERIFY(!app.historyFilter().value(QStringLiteral("pending_review")).toBool());
        clickItem(window, mark);
        QTRY_VERIFY(pending->isVisible());
        clickItem(window, pending);
        QTRY_VERIFY(app.historyFilterConfirmationPending());
        auto *accept = findVisual(window->contentItem(), QStringLiteral("historyConfirmFilter"));
        QVERIFY(accept);
        clickItem(window, accept);
        QTRY_COMPARE(app.checkedHistoryRunCount(), 0);
        QTRY_VERIFY(app.historyFilter().value(QStringLiteral("pending_review")).toBool());
        app.resetHistoryFilter();
        QTRY_VERIFY(!app.runs()->isLoading() && app.runs()->rowCount() > 0);
        app.setHistoryRunChecked(app.runs()->runAt(0), true);
        QVERIFY(QMetaObject::invokeMethod(page, "requestBatch", Q_ARG(QVariant, QStringLiteral("soft_delete"))));
        auto *confirm = findVisual(window->contentItem(), QStringLiteral("historyConfirmBatch"));
        QTRY_VERIFY(confirm && confirm->isVisible());
        QVERIFY(window->grabWindow().save(directory + QStringLiteral("/history-confirm-") + QString::fromLatin1(QTest::currentDataTag()) + QStringLiteral(".png")));
        clickItem(window, confirm);
        QTRY_VERIFY(!app.historyBatchRunning() && app.checkedHistoryRunCount() == 0);
        QVERIFY2(warnings.isEmpty(), qPrintable(warnings.join(QLatin1Char('\n'))));
    }
};
int main(int argc, char **argv) {
    mrtest::disableCollectorLaunch();
    if (qEnvironmentVariableIsEmpty("QT_QPA_FONTDIR"))
        qputenv("QT_QPA_FONTDIR", "C:/Windows/Fonts");
    QGuiApplication app(argc, argv);
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    HistoryBatchTests tests;
    return QTest::qExec(&tests, argc, argv);
}
#include "HistoryBatchTests.moc"
