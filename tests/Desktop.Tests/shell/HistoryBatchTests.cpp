#include "DesktopTestRunner.h"
#include "TestFonts.h"
#include "QmlComponentTestSupport.h"
#include "TestCollectorGuard.h"
#include "AppController.h"
#include "HistoryController.h"
#include "HistoryBatchTestSupport.h"
#include "MockBackend.h"
#include "MockBackendTestSupport.h"
#include "Formatters.h"
#include "JobCatalog.h"
#include "RoleCatalog.h"
#include "RunFormValidator.h"

#include <QDir>
#include <QGuiApplication>
#include <QJsonArray>
#include <QTest>
#include <QTimeZone>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickWindow>
#include <QQuickStyle>
#include <memory>

namespace {
using mrtest::findVisualItem;
void clickItem(QQuickWindow *window, QQuickItem *item) {
    const QPoint point = item->mapToScene(QPointF(item->width() / 2, item->height() / 2)).toPoint();
    QTest::mouseClick(window, Qt::LeftButton, Qt::NoModifier, point);
}
}

namespace {
class HistoryBatchTests final : public QObject {
    Q_OBJECT
private:
    template <typename Reveal>
    static void exerciseFilterConfirmationAndDelete(QQuickWindow *window, mr::AppController &app,
                                                    QQuickItem *page, QQuickItem *mark, QQuickItem *pending,
                                                    const Reveal &reveal, const QStringList &warnings)
    {
        const QString directory = qEnvironmentVariable("MR_HISTORY_UI_EVIDENCE_DIR", QDir::currentPath());
        QVERIFY(QDir().mkpath(directory));
        QTest::qWait(50);
        QVERIFY(window->grabWindow().save(directory + QStringLiteral("/history-") + QString::fromLatin1(QTest::currentDataTag()) + QStringLiteral(".png")));
        QVERIFY(reveal(mark));
        clickItem(window, mark);
        QTRY_VERIFY(pending->isVisible());
        clickItem(window, pending);
        QTRY_VERIFY(app.historyFilterConfirmationPending());
        auto *keep = findVisualItem(window->contentItem(), QStringLiteral("historyKeepSelection"));
        QTRY_VERIFY(keep && keep->isVisible());
        QVERIFY(keep->hasActiveFocus());
        QTest::keyClick(window, Qt::Key_Escape);
        QTRY_VERIFY(!app.historyFilterConfirmationPending());
        QCOMPARE(app.checkedHistoryRunCount(), 1);
        QVERIFY(!app.historyFilter().value(QStringLiteral("pending_review")).toBool());
        QVERIFY(reveal(mark));
        clickItem(window, mark);
        QTRY_VERIFY(pending->isVisible());
        clickItem(window, pending);
        QTRY_VERIFY(app.historyFilterConfirmationPending());
        auto *accept = findVisualItem(window->contentItem(), QStringLiteral("historyConfirmFilter"));
        QVERIFY(accept);
        clickItem(window, accept);
        QTRY_COMPARE(app.checkedHistoryRunCount(), 0);
        QTRY_VERIFY(app.historyFilter().value(QStringLiteral("pending_review")).toBool());
        app.resetHistoryFilter();
        QTRY_VERIFY(!app.runs()->isLoading() && app.runs()->rowCount() > 0);
        app.setHistoryRunChecked(app.runs()->runAt(0), true);
        QVERIFY(QMetaObject::invokeMethod(page, "requestBatch", Q_ARG(QVariant, QStringLiteral("soft_delete"))));
        auto *confirm = findVisualItem(window->contentItem(), QStringLiteral("historyConfirmBatch"));
        QTRY_VERIFY(confirm && confirm->isVisible());
        QVERIFY(window->grabWindow().save(directory + QStringLiteral("/history-confirm-") + QString::fromLatin1(QTest::currentDataTag()) + QStringLiteral(".png")));
        clickItem(window, confirm);
        QTRY_VERIFY(!app.historyBatchRunning() && app.checkedHistoryRunCount() == 0);
        QVERIFY2(warnings.isEmpty(), qPrintable(warnings.join(QLatin1Char('\n'))));
    }

    static void exerciseSelectionAndFilterConfirmation(QQuickWindow *window, mr::AppController &app, QQuickItem *page, QQuickItem *checkbox, QQuickItem *selectAll, QQuickItem *mark, QQuickItem *pending, const QStringList &warnings)
    {
        clickItem(window, checkbox);
        QTRY_COMPARE(app.checkedHistoryRunCount(), 1);
        QVERIFY(!app.hasSelection()); // 勾选不应打开详情遮住批量操作。
        clickItem(window, selectAll);
        QTRY_COMPARE(app.checkedHistoryRunCount(), app.runs()->rowCount());
        QVERIFY(app.checkedHistoryRunCount() < app.runs()->total());
        clickItem(window, selectAll);
        QTRY_COMPARE(app.checkedHistoryRunCount(), 0);
        clickItem(window, checkbox);
        QTRY_COMPARE(app.checkedHistoryRunCount(), 1);
        const auto filterBeforeJump = app.historyFilter();
        auto *jump = findVisualItem(window->contentItem(), QStringLiteral("historyPageJump"));
        QVERIFY(jump);
        auto *number = findVisualItem(jump, QStringLiteral("pageNumberField"));
        QVERIFY(number);
        auto *viewport = jump->parentItem();
        while (viewport && viewport->metaObject()->indexOfProperty("contentY") < 0)
            viewport = viewport->parentItem();
        QVERIFY(viewport);
        // A page of rows is sized to the window (review K3-1): the jump at the bottom and the
        // filter bar at the top are both on screen without scrolling, once a change in the
        // bar above the table (the batch bar appearing) has resized the page.
        const auto reveal = [&](QQuickItem *item) {
            return QTest::qWaitFor([&] {
                const QRectF area(viewport->mapToScene(QPointF()), viewport->size());
                return !app.runs()->isLoading() && !viewport->property("moving").toBool()
                    && qFuzzyIsNull(viewport->property("contentY").toReal())
                    && area.contains(QRectF(item->mapToScene(QPointF()), item->size()));
            }, 3000);
        };
        QVERIFY(reveal(jump));
        number->forceActiveFocus();
        QTest::keyClick(window, Qt::Key_A, Qt::ControlModifier);
        QTest::keyClick(window, Qt::Key_2);
        QTest::keyClick(window, Qt::Key_Return);
        QTRY_COMPARE(app.runs()->page(), 2);
        QTRY_VERIFY(!app.runs()->isLoading());
        QCOMPARE(app.checkedHistoryRunCount(), 1);
        QCOMPARE(app.historyFilter(), filterBeforeJump);
        number->forceActiveFocus();
        QTest::keyClick(window, Qt::Key_A, Qt::ControlModifier);
        QTest::keyClick(window, Qt::Key_1);
        QTest::keyClick(window, Qt::Key_Return);
        QTRY_COMPARE(app.runs()->page(), 1);
        QTRY_VERIFY(!app.runs()->isLoading());
        QCOMPARE(app.checkedHistoryRunCount(), 1);
        QVERIFY(reveal(jump));
        exerciseFilterConfirmationAndDelete(window, app, page, mark, pending, reveal, warnings);
        QVERIFY(!QTest::currentTestFailed());
    }

private Q_SLOTS:
    void regionMoveRetryKeepsPreviewAndRequestAfterReconnect()
    {
        ControlledBackend backend;
        mr::HistoryController history(&backend);
        QVariantMap selected = row(firstId(), 3);
        selected.insert(QStringLiteral("source"), QStringLiteral("IMPORT"));
        selected.insert(QStringLiteral("region"), QStringLiteral("CN"));
        history.setRunChecked(selected, true);
        history.previewRegionMigration(QStringLiteral("GLOBAL"), 4);
        const auto preview = backend.last(QStringLiteral("PreviewRunRegionMigration"));
        QCOMPARE(preview.payload.value(QStringLiteral("profile_id")).toInt(), 4);
        preview.reply->succeed({{QStringLiteral("target_baseline"), 1444},
                                {QStringLiteral("contributing_count"), 1}});
        QTRY_VERIFY(!history.regionMigrationPreview().isEmpty());
        const QString reason = QStringLiteral("正确区服");
        history.movePreviewedRuns(1, reason);
        const auto first = backend.last(QStringLiteral("BatchMutateRuns"));
        QCOMPARE(first.payload.value(QStringLiteral("action")).toString(), QStringLiteral("move_region"));
        QCOMPARE(first.payload.value(QStringLiteral("baseline_overlap")).toInt(), 1);
        QCOMPARE(first.payload.value(QStringLiteral("expected_baseline")).toInt(), 1444);
        first.reply->fail(QStringLiteral("ERR_PIPE_CLOSED"), QStringLiteral("disconnected"));
        QTRY_VERIFY(!history.batchRunning());
        Q_EMIT backend.connectionChanged();
        history.movePreviewedRuns(1, reason);
        const auto retry = backend.last(QStringLiteral("BatchMutateRuns"));
        QVERIFY(retry.reply != first.reply);
        QCOMPARE(retry.id, first.id);
        QCOMPARE(retry.payload, first.payload);
        retry.reply->succeed({{QStringLiteral("changed_count"), 1}});
        QTRY_VERIFY(history.checkedRunIds().isEmpty());
    }

    void initTestCase() {
        QVERIFY(mrtest::registerSourceQml({"components", "dialogs"}));
        qmlRegisterType(QUrl::fromLocalFile(mrtest::qmlSourceRoot() + QStringLiteral("/pages/HistoryPage.qml")), "MentorRecorder", 1, 0, "HistoryPage");
    }
    void crossPageSelectionKeepsOriginalRevisionsAndConflictKeepsSelection() {
        ControlledBackend backend;
        mr::HistoryController history(&backend);
        history.runs()->setPageSize(1);
        QCOMPARE(backend.last(QStringLiteral("QueryRuns")).payload.value(QStringLiteral("sort")).toObject().value(QStringLiteral("field")).toString(), QStringLiteral("history_date"));
        backend.completePage(row(firstId(), 4));
        history.setCurrentPageChecked(true);
        QVERIFY(history.allCurrentPageChecked());
        history.runs()->nextPage();
        backend.completePage(row(secondId(), 7), 2);
        history.setCurrentPageChecked(true);
        QCOMPARE(history.checkedRunIds(), QStringList({firstId(), secondId()}));
        history.adoptRunRevisionFromEvent(QJsonObject::fromVariantMap(row(firstId(), 9)));
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
    // 勾选全部 reads every matching record instead of a tick per page. A row checked
    // before keeps the revision it was checked with; a failed or abandoned read
    // changes nothing.
    void checkAllMatchingReadsTheWholeFilterAndKeepsEarlierRevisions() {
        ControlledBackend backend;
        mr::HistoryController history(&backend);
        history.runs()->setPageSize(1);
        backend.completePage(row(firstId(), 4));
        history.setCurrentPageChecked(true);
        const QJsonObject everything{
            {QStringLiteral("selection_id"), QStringLiteral("8367ddf3-2401-477f-b450-fb98a296bdb5")},
            {QStringLiteral("items"), QJsonArray{QJsonObject::fromVariantMap(row(firstId(), 9)),
                                                 QJsonObject::fromVariantMap(row(secondId(), 7))}},
            {QStringLiteral("page_info"), QJsonObject{{QStringLiteral("page"), 1}, {QStringLiteral("page_size"), 200},
                                                        {QStringLiteral("total"), 2}}}};

        history.checkAllMatchingRuns();
        QVERIFY(history.checkAllLoading());
        QCOMPARE(backend.last(QStringLiteral("QueryHistorySelection")).payload.value(QStringLiteral("page")).toInt(), 1);
        QCOMPARE(backend.last(QStringLiteral("QueryHistorySelection")).payload.value(QStringLiteral("filter")).toObject(),
                 QJsonObject::fromVariantMap(history.historyFilter()));
        backend.last(QStringLiteral("QueryHistorySelection")).reply->fail(QStringLiteral("ERR_INTERNAL"), QStringLiteral("读取失败"));
        QVERIFY(!history.checkAllLoading());
        QCOMPARE(history.checkedRunIds(), QStringList({firstId()}));

        // 取消勾选 while the read is on its way: its late answer checks nothing.
        history.checkAllMatchingRuns();
        const auto abandoned = backend.last(QStringLiteral("QueryHistorySelection"));
        history.clearCheckedRuns();
        QVERIFY(!history.checkAllLoading());
        abandoned.reply->succeed(everything);
        QCOMPARE(history.checkedRunCount(), 0);

        // Unchecking one row, or a batch sent meanwhile, drops the read as well: its rows
        // would come back checked, with the revisions from before the batch.
        history.setCurrentPageChecked(true);
        history.checkAllMatchingRuns();
        const auto afterUncheck = backend.last(QStringLiteral("QueryHistorySelection"));
        history.setRunChecked(row(firstId(), 4), false);
        QVERIFY(!history.checkAllLoading());
        afterUncheck.reply->succeed(everything);
        QCOMPARE(history.checkedRunCount(), 0);
        history.setCurrentPageChecked(true);
        history.checkAllMatchingRuns();
        const auto duringBatch = backend.last(QStringLiteral("QueryHistorySelection"));
        history.mutateCheckedRuns(QStringLiteral("soft_delete"), QStringLiteral("清理记录"));
        QVERIFY(!history.checkAllLoading());
        backend.last(QStringLiteral("BatchMutateRuns")).reply->fail(QStringLiteral("ERR_REVISION_CONFLICT"), QStringLiteral("记录已更新"));
        duringBatch.reply->succeed(everything);
        QCOMPARE(history.checkedRunIds(), QStringList({firstId()}));
        history.clearCheckedRuns();

        history.setCurrentPageChecked(true);
        history.checkAllMatchingRuns();
        backend.last(QStringLiteral("QueryHistorySelection")).reply->succeed(everything);
        QVERIFY(!history.checkAllLoading());
        QCOMPARE(history.checkedRunIds(), QStringList({firstId(), secondId()}));
        history.mutateCheckedRuns(QStringLiteral("soft_delete"), QStringLiteral("清理记录"));
        const auto targets = backend.last(QStringLiteral("BatchMutateRuns")).payload.value(QStringLiteral("runs")).toArray();
        QCOMPARE(targets.size(), 2);
        QCOMPARE(targets[0].toObject().value(QStringLiteral("expected_revision")).toInt(), 4);
        QCOMPARE(targets[1].toObject().value(QStringLiteral("expected_revision")).toInt(), 7);
    }
    void filterChangesRequireExplicitConfirmation() {
        ControlledBackend backend;
        mr::HistoryController history(&backend);
        history.setRunChecked(row(firstId(), 2), true);
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
        QCOMPARE(history.historyFilter().value(QStringLiteral("date_field")).toString(), QStringLiteral("matched_at_utc"));
        QVERIFY(history.historyFilter().value(QStringLiteral("pending_review")).toBool());
        QVERIFY(backend.last(QStringLiteral("QueryRuns")).reply);
    }
    void purgeAndRestoreRefuseLiveOrMixedSelectionBeforeSending() {
        ControlledBackend backend;
        mr::HistoryController history(&backend);
        history.setRunChecked(row(firstId(), 1), true);
        history.setRunChecked(row(secondId(), 2, true), true);
        history.mutateCheckedRuns(QStringLiteral("purge"), QStringLiteral("原因"));
        history.mutateCheckedRuns(QStringLiteral("restore"), QStringLiteral("原因"));
        history.mutateCheckedRuns(QStringLiteral("soft_delete"), QStringLiteral("原因"));
        QVERIFY(backend.calls.isEmpty());
        history.setRunChecked(row(firstId(), 1), false);
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
    void sourceCalendarDayFilterKeepsAllRawDateFormatsInNegativeTimezone() {
        mr::MockBackend backend;
        QJsonArray fixtures;
        for (const QString &raw : {QStringLiteral("2024-09-24"), QStringLiteral("2024/9/24"), QStringLiteral("2024.9.24")}) {
            QString id = firstId();
            if (raw.contains(QLatin1Char('.'))) id = QStringLiteral("00000000-0000-0000-0000-000000000004");
            else if (raw.contains(QLatin1Char('/'))) id = secondId();
            QJsonObject run = QJsonObject::fromVariantMap(row(id, 1));
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
        const auto all = mrtest::ask(backend, QStringLiteral("QueryRuns"), {{QStringLiteral("filter"), filter}});
        QVERIFY(all.ok);
        QCOMPARE(all.payload.value(QStringLiteral("items")).toArray().size(), 3);
        filter.insert(QStringLiteral("run_id"), QJsonArray{secondId()});
        const auto one = mrtest::ask(backend, QStringLiteral("QueryRuns"), {{QStringLiteral("filter"), filter}});
        const QJsonArray items = one.payload.value(QStringLiteral("items")).toArray();
        QCOMPARE(items.size(), 1);
        QCOMPARE(items.first().toObject().value(QStringLiteral("run_id")).toString(), secondId());
    }
    void importedMaintenanceRevisionCannotUndoEvenWithOnlyPendingFlagChange() {
        ControlledBackend backend;
        mr::HistoryController history(&backend);
        history.selectRun(row(firstId(), 2));
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
        QJsonObject source = QJsonObject::fromVariantMap(row(firstId(), 1));
        source.insert(QStringLiteral("source"), QStringLiteral("IMPORT"));
        source.insert(QStringLiteral("import_metadata"), QJsonObject{{QStringLiteral("source_recorded_at"), QStringLiteral("2024/9/24")}});
        QJsonObject actual = QJsonObject::fromVariantMap(row(secondId(), 1));
        actual.insert(QStringLiteral("entered_at_utc"), QDateTime(sourceDay.addDays(dayOffset), QTime(hour, 0),
            QTimeZone::systemTimeZone()).toUTC().toString(Qt::ISODateWithMs));
        backend.resetRuns({actual, source});
        for (bool ascending : {true, false}) {
            const QJsonObject sort{{QStringLiteral("field"), QStringLiteral("history_date")},
                {QStringLiteral("direction"), ascending ? QStringLiteral("asc") : QStringLiteral("desc")}};
            const auto sorted = mrtest::ask(backend, QStringLiteral("QueryRuns"), {{QStringLiteral("sort"), sort}});
            QVERIFY(sorted.ok);
            const QJsonArray rows = sorted.payload.value(QStringLiteral("items")).toArray();
            QCOMPARE(rows.size(), 2);
            QCOMPARE(rows.first().toObject().value(QStringLiteral("run_id")).toString(), sourceFirst == ascending ? firstId() : secondId());
        }
        // The default instant filter remains UTC-day based; only the comparison
        // key used by ORDER BY treats a raw calendar day as local midnight.
        const QJsonObject filter{{QStringLiteral("date_field"), QStringLiteral("history_date")},
            {QStringLiteral("run_id"), QJsonArray{firstId()}},
            {QStringLiteral("from_utc"), QStringLiteral("2024-09-24T00:00:00.000Z")},
            {QStringLiteral("to_utc"), QStringLiteral("2024-09-24T00:00:00.000Z")}};
        const auto filtered = mrtest::ask(backend, QStringLiteral("QueryRuns"), {{QStringLiteral("filter"), filter}});
        QVERIFY(filtered.ok);
        QCOMPARE(filtered.payload.value(QStringLiteral("items")).toArray().size(), 1);
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
        window->resize(narrow ? QSize(720, 760) : QSize(1140, 790));
        QVERIFY(QTest::qWaitForWindowExposed(window));
        QTRY_VERIFY(!app.runs()->isLoading() && app.runs()->rowCount() > 0);
        auto *page = findVisualItem(window->contentItem(), QStringLiteral("historyUnderTest"));
        QVERIFY(page);
        auto *detail = findVisualItem(window->contentItem(), QStringLiteral("detailFacts"));
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
            for (const QVariant &field : mrtest::qmlList(detail->property("infoFields")))
                hasSupplement |= field.toMap().value(QStringLiteral("v")).toString() == QStringLiteral("待补充");
            QCOMPARE(hasSupplement, source == QLatin1String("IMPORT"));
        }
        // 标记: every word of the column comes with the sentence that explains it (a tester
        // did not know what 待补充 and 已确认 meant, 2026-11-20).
        {
            const auto marksOf = [page](const QVariantMap &run) {
                QVariant returned;
                QMetaObject::invokeMethod(page, "runMarks", Q_RETURN_ARG(QVariant, returned), Q_ARG(QVariant, QVariant(run)));
                QStringList labels;
                for (const QVariant &mark : mrtest::qmlList(returned)) {
                    const QVariantMap entry = mark.toMap();
                    if (entry.value(QStringLiteral("tip")).toString().size() < 6) return QStringList{QStringLiteral("(no tip)")};
                    labels.append(entry.value(QStringLiteral("label")).toString());
                }
                return labels;
            };
            QCOMPARE(marksOf({{QStringLiteral("source"), QStringLiteral("IMPORT")}, {QStringLiteral("result"), QStringLiteral("UNKNOWN")}}),
                     QStringList{QString::fromUtf8("待补充")});
            // Answered or filled in without overruling anything: 已确认, only when the
            // Collector says so (review_confirmed). Overruled: 已修正, never both.
            QCOMPARE(marksOf({{QStringLiteral("source"), QStringLiteral("AUTO_NETWORK")}, {QStringLiteral("revision"), 2},
                              {QStringLiteral("review_confirmed"), true}}),
                     QStringList{QString::fromUtf8("已确认")});
            // A later revision that confirmed nothing - moved to another character or
            // region, deleted and restored - and a Collector that does not send the
            // field show no mark (audit 2026-11-26, K3-1).
            QVERIFY(marksOf({{QStringLiteral("source"), QStringLiteral("AUTO_NETWORK")}, {QStringLiteral("revision"), 3},
                             {QStringLiteral("review_confirmed"), false}}).isEmpty());
            QVERIFY(marksOf({{QStringLiteral("source"), QStringLiteral("AUTO_NETWORK")}, {QStringLiteral("revision"), 2}}).isEmpty());
            QCOMPARE(marksOf({{QStringLiteral("source"), QStringLiteral("AUTO_NETWORK")}, {QStringLiteral("revision"), 3},
                              {QStringLiteral("manually_corrected"), true}, {QStringLiteral("reflection"), QVariantMap{{QStringLiteral("text"), QStringLiteral("x")}}}}),
                     (QStringList{QString::fromUtf8("已修正"), QString::fromUtf8("有笔记")}));
            QCOMPARE(marksOf({{QStringLiteral("source"), QStringLiteral("AUTO_NETWORK")}, {QStringLiteral("pending_review"), true},
                              {QStringLiteral("revision"), 2}, {QStringLiteral("joined_in_progress"), true}}),
                     (QStringList{QString::fromUtf8("待复核"), QString::fromUtf8("中途加入")}));
            QVERIFY(marksOf({{QStringLiteral("source"), QStringLiteral("AUTO_NETWORK")}}).isEmpty());
        }
        for (const QString &raw : {QStringLiteral("2024-09-24"), QStringLiteral("2024/9/24"), QStringLiteral("2024.9.24")}) {
            const QVariantMap source{{QStringLiteral("source_recorded_at"), raw}};
            const QVariant input = QVariantMap{{QStringLiteral("import_metadata"), source}};
            QVariant returned;
            QVERIFY(QMetaObject::invokeMethod(page, "dateLabel", Q_RETURN_ARG(QVariant, returned), Q_ARG(QVariant, input)));
            QCOMPARE(returned.toString(), QStringLiteral("原站\n2024-09-24"));
        }
        auto *more = findVisualItem(window->contentItem(), QStringLiteral("historyMoreFiltersButton"));
        auto *pending = root->findChild<QQuickItem *>(QStringLiteral("pendingReviewChip"));
        QVERIFY(more && pending);
        QCOMPARE(more->isVisible(), narrow);
        auto *mark = findVisualItem(window->contentItem(), QStringLiteral("historyMarkFilter"));
        auto *selectAll = findVisualItem(window->contentItem(), QStringLiteral("selectCurrentHistoryPage"));
        auto *count = findVisualItem(window->contentItem(), QStringLiteral("historyCheckedCount"));
        auto *previous = findVisualItem(window->contentItem(), QStringLiteral("historyPreviousPage"));
        QVERIFY(mark && selectAll && count && previous);
        QTRY_VERIFY(mark->isVisible() && selectAll->isVisible());
        auto *tableHeader = root->findChild<QQuickItem *>(QStringLiteral("historyTableHeader"));
        QVERIFY(tableHeader);
        QCOMPARE(selectAll->parentItem()->parentItem(), tableHeader);
        QVERIFY(selectAll->parentItem() != mark->parentItem());
        QCOMPARE(count->parentItem(), previous->parentItem());
        QVERIFY(count->x() < previous->x());
        QVERIFY(!pending->isVisible());
        const QVariantMap run = app.runs()->runAt(0);
        auto *checkbox = findVisualItem(window->contentItem(), QStringLiteral("historyRunCheck_") + run.value(QStringLiteral("run_id")).toString());
        QVERIFY(checkbox);
        QCOMPARE(selectAll->mapToScene(QPointF()).x(), checkbox->mapToScene(QPointF()).x());
        exerciseSelectionAndFilterConfirmation(window, app, page, checkbox, selectAll, mark, pending, warnings);
        QVERIFY(!QTest::currentTestFailed());
    }
};
} // namespace
MR_DESKTOP_TEST_SUITE(HistoryBatch) {
    mrtest::disableCollectorLaunch();
    if (qEnvironmentVariableIsEmpty("QT_QPA_FONTDIR"))
        qputenv("QT_QPA_FONTDIR", "C:/Windows/Fonts");
    QGuiApplication app(argc, argv);
    mrtest::useScreenshotFontStack();
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    HistoryBatchTests tests;
    return QTest::qExec(&tests, argc, argv);
}
#include "HistoryBatchTests.moc"
