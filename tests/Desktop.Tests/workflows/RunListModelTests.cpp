#include "DesktopTestRunner.h"
#include "IBackend.h"
#include "MockBackend.h"
#include "RunListModel.h"

#include <QJsonArray>
#include <QPointer>
#include <QTest>

namespace {
class CalendarBackend final : public mr::IBackend {
public:
    struct Call { QString type; QJsonObject payload; QPointer<mr::BackendReply> reply; };
    QList<Call> calls;
    QString backendName() const override { return QStringLiteral("calendar-test"); }
    bool isConnected() const override { return true; }
    mr::BackendReply *request(const QString &type, const QJsonObject &payload = {}) override {
        auto *reply = new mr::BackendReply(QString::number(calls.size()), type, this);
        calls.append({.type=type, .payload=payload, .reply=reply});
        return reply;
    }
    mr::BackendReply *requestWithId(const QString &type, const QJsonObject &payload,
                                   const QString &) override { return request(type, payload); }
    QList<Call> calendarCalls() const {
        QList<Call> result;
        for (const Call &call : calls)
            if (call.type == QStringLiteral("QueryRunCalendarDays")) result.append(call);
        return result;
    }
    void answer(int index, const QStringList &days) const {
        const Call call = calendarCalls().at(index);
        QJsonArray array;
        for (const QString &day : days) array.append(day);
        call.reply->succeed({{QStringLiteral("month"), call.payload.value(QStringLiteral("month"))},
                             {QStringLiteral("days"), array}});
    }
    /// Answers the oldest QueryRuns still waiting: no rows, page \a page of \a total.
    void answerNextQuery(int page, int total) const {
        QPointer<mr::BackendReply> next;
        for (const Call &call : calls) {
            if (call.type == QStringLiteral("QueryRuns") && call.reply && !call.reply->isFinished()) {
                next = call.reply;
                break;
            }
        }
        QVERIFY2(next, "no QueryRuns is waiting for an answer");
        // The answer may send the next QueryRuns, which appends to calls.
        next->succeed({{QStringLiteral("items"), QJsonArray{}},
                       {QStringLiteral("page_info"),
                        QJsonObject{{QStringLiteral("page"), page}, {QStringLiteral("total"), total}}}});
    }
};
}

namespace {
class RunListModelTests : public QObject {
    Q_OBJECT
private Q_SLOTS:
    void dateBoundsNeverRestrictCalendar() {
        CalendarBackend backend; mr::RunListModel model; model.setBackend(&backend);
        QVariantMap filter{{"job", "SCH"}, {"deleted_only", true}, {"from_utc", "2026-09-07T00:00:00Z"},
                           {"to_utc", "2026-09-08T00:00:00Z"}, {"history_from_day", "2026-09-07"},
                           {"history_to_day", "2026-09-07"}};
        model.setFilter(filter); model.requestCalendarDays("2026-09");
        const auto call = backend.calendarCalls().last();
        QCOMPARE(call.payload.value("month").toString(), QStringLiteral("2026-09"));
        QCOMPARE(call.payload.value("filter").toObject(), (QJsonObject{{"job", "SCH"}, {"deleted_only", true}}));
        QVERIFY(!call.payload.contains("page")); QVERIFY(!call.payload.contains("page_size"));
        QVERIFY(model.calendarLoading()); backend.answer(0, {"2026-09-01", "2026-09-30"});
        QVERIFY(!model.calendarLoading()); QVERIFY(model.calendarDayAvailable("2026-09-30"));
        filter["history_from_day"] = "2026-09-30";
        model.setFilter(filter); model.sortBy("duty_name"); model.setPageSize(20);
        QCOMPARE(backend.calendarCalls().size(), 1);
        QVERIFY(model.calendarDayAvailable("2026-09-01"));
    }
    void filterChangeDiscardsLateReplies() {
        CalendarBackend backend; mr::RunListModel model; model.setBackend(&backend);
        model.setCalendarFilter({{"job", "SCH"}}); model.requestCalendarDays("2026-09");
        model.setCalendarFilter({{"job", "WHM"}});
        QCOMPARE(backend.calendarCalls().size(), 2);
        backend.answer(0, {"2026-09-07"});
        QVERIFY(model.calendarLoading()); QVERIFY(!model.calendarMonthKnown("2026-09"));
        backend.answer(1, {"2026-09-17"});
        QVERIFY(!model.calendarDayAvailable("2026-09-07"));
        QVERIFY(model.calendarDayAvailable("2026-09-17"));
    }
    void monthNavigationAndEndpointQueriesRemainIndependent() {
        CalendarBackend backend; mr::RunListModel model; model.setBackend(&backend);
        model.requestCalendarDays("2026-09"); model.requestCalendarDays("2026-10");
        backend.answer(0, {"2026-09-07"});
        QCOMPARE(model.calendarMonth(), QStringLiteral("2026-10")); QVERIFY(model.calendarLoading());
        model.ensureCalendarDay("2026-08-31"); QCOMPARE(backend.calendarCalls().size(), 3);
        backend.answer(2, {"2026-08-31"}); backend.answer(1, {"2026-10-01"});
        QCOMPARE(model.calendarAvailableDays(), (QStringList{"2026-08-31", "2026-09-07", "2026-10-01"}));
        QCOMPARE(model.calendarKnownMonths(), (QStringList{"2026-08", "2026-09", "2026-10"}));
        model.requestCalendarDays("2026-09"); QCOMPARE(backend.calendarCalls().size(), 3);
    }
    void malformedAnswersAreErrors_data() {
        QTest::addColumn<QJsonObject>("answer");
        QTest::newRow("missing-days") << QJsonObject{{"month", "2026-09"}};
        QTest::newRow("wrong-month") << QJsonObject{{"month", "2026-08"}, {"days", QJsonArray{}}};
        QTest::newRow("invalid-date") << QJsonObject{{"month", "2026-09"}, {"days", QJsonArray{"2026-09-31"}}};
        QTest::newRow("other-month-day") << QJsonObject{{"month", "2026-09"}, {"days", QJsonArray{"2026-10-01"}}};
        QTest::newRow("duplicates") << QJsonObject{{"month", "2026-09"}, {"days", QJsonArray{"2026-09-07", "2026-09-07"}}};
        QTest::newRow("unordered") << QJsonObject{{"month", "2026-09"}, {"days", QJsonArray{"2026-09-17", "2026-09-07"}}};
    }
    void malformedAnswersAreErrors() {
        QFETCH(QJsonObject, answer);
        CalendarBackend backend; mr::RunListModel model; model.setBackend(&backend);
        model.requestCalendarDays("2026-09"); backend.calendarCalls().last().reply->succeed(answer);
        QVERIFY(!model.calendarLoading()); QVERIFY(!model.calendarError().isEmpty());
        QVERIFY(!model.calendarMonthKnown("2026-09")); QVERIFY(model.calendarAvailableDays().isEmpty());
    }
    void emptyAnswerFailureAndRetryAreDistinct() {
        CalendarBackend backend; mr::RunListModel model; model.setBackend(&backend);
        model.requestCalendarDays("2026-09"); backend.answer(0, {});
        QVERIFY(model.calendarMonthKnown("2026-09")); QVERIFY(model.calendarError().isEmpty());
        model.ensureCalendarDay("2026-08-01");
        backend.calendarCalls().last().reply->fail("ERR_TIMEOUT", "calendar timeout");
        QVERIFY(!model.calendarMonthKnown("2026-08"));
        QCOMPARE(model.calendarDayError("2026-08-01"), QStringLiteral("calendar timeout"));
        model.ensureCalendarDay("2026-08-01"); QCOMPARE(backend.calendarCalls().size(), 2);
        model.requestCalendarDays("2026-08"); QCOMPARE(backend.calendarCalls().size(), 3);
        QVERIFY(model.calendarLoading()); QVERIFY(model.calendarError().isEmpty());
        backend.answer(2, {"2026-08-01"}); QVERIFY(model.calendarDayAvailable("2026-08-01"));
    }
    void explicitReloadInvalidatesAllMonthsAndRejectsStaleAnswers() {
        CalendarBackend backend; mr::RunListModel model; model.setBackend(&backend);
        model.requestCalendarDays("2026-09"); backend.answer(0, {"2026-09-07"});
        model.ensureCalendarDay("2026-08-01"); model.reload();
        QCOMPARE(backend.calendarCalls().size(), 3); QVERIFY(model.calendarAvailableDays().isEmpty());
        backend.answer(1, {"2026-08-01"}); QVERIFY(!model.calendarMonthKnown("2026-08"));
        backend.answer(2, {"2026-09-17"});
        QVERIFY(model.calendarDayAvailable("2026-09-17")); QVERIFY(!model.calendarDayAvailable("2026-09-07"));
    }
    void invalidDatesNeverQueryBackend() {
        CalendarBackend backend; mr::RunListModel model; model.setBackend(&backend);
        model.ensureCalendarDay("2026-09-31"); model.ensureCalendarDay("2026-9-01");
        model.requestCalendarDays("2026-13"); model.requestCalendarDays("2026-9");
        QVERIFY(backend.calendarCalls().isEmpty()); QVERIFY(!model.calendarError().isEmpty());
    }

    void filtersSortsAndPages()
    {
        mr::MockBackend backend;
        mr::RunListModel model;
        model.setBackend(&backend);

        // QML selects the map through the integer model role, which exceeds one byte.
        QCOMPARE(static_cast<int>(mr::RunListModel::RunRole), Qt::UserRole + 1);
        QCOMPARE(model.roleNames().value(Qt::UserRole + 1), QByteArray("run"));
        model.reload();
        QTRY_VERIFY_WITH_TIMEOUT(model.rowCount() > 0, 3000);
        QCOMPARE(model.data(model.index(0), Qt::UserRole + 1).toMap(), model.runAt(0));
        QVERIFY(!model.data(model.index(0), Qt::UserRole).isValid());
        QCOMPARE(model.page(), 1);
        QCOMPARE(model.pageSize(), 10);
        QVERIFY(model.total() >= model.rowCount());

        model.sortBy(QStringLiteral("duty_name"));
        QTRY_COMPARE_WITH_TIMEOUT(model.sortField(), QStringLiteral("duty_name"), 3000);
        QCOMPARE(model.sortAscending(), false);

        model.sortBy(QStringLiteral("duty_name"));
        QTRY_VERIFY_WITH_TIMEOUT(model.sortAscending(), 3000);

        QVariantMap correctedOnly;
        correctedOnly.insert(QStringLiteral("corrected_only"), true);
        model.setFilter(correctedOnly);
        QTRY_COMPARE_WITH_TIMEOUT(model.total(), 1, 3000);
        QCOMPARE(model.rowCount(), 1);
        QVERIFY(model.runAt(0).value(QStringLiteral("manually_corrected")).toBool());

        QVariantMap includeDeleted;
        includeDeleted.insert(QStringLiteral("include_deleted"), true);
        model.setFilter(includeDeleted);
        QTRY_VERIFY_WITH_TIMEOUT(model.total() > 90, 3000);
        QVERIFY(model.runAt(0).contains(QStringLiteral("run_id")));

        // The page size follows the window (HistoryPage.rowsThatFit). Growing or
        // shrinking it keeps the row at the top of the current page in view rather
        // than jumping back to page 1: page 3 of 10 starts at row 20, which sits on
        // page 2 of 15 (rows 15-29); the original anchor, row 20, then heads
        // page 5 of 5, without accumulating rounding loss across resizes.
        // reload() marks the model loading before the request leaves, so waiting
        // for that flag to clear is waiting for the page that was just asked for.
        model.goToPage(3);
        QCOMPARE(model.page(), 3);
        QTRY_VERIFY_WITH_TIMEOUT(!model.isLoading(), 3000);
        const QString topRow = model.runAt(0).value(QStringLiteral("run_id")).toString();
        QVERIFY(!topRow.isEmpty());
        model.setPageSize(15);
        QCOMPARE(model.pageSize(), 15);
        QCOMPARE(model.page(), 2);
        QTRY_VERIFY_WITH_TIMEOUT(!model.isLoading(), 3000);
        QCOMPARE(model.rowCount(), 15);
        QCOMPARE(model.runAt(5).value(QStringLiteral("run_id")).toString(), topRow);
        model.setPageSize(5);
        QCOMPARE(model.page(), 5);
        QTRY_VERIFY_WITH_TIMEOUT(!model.isLoading(), 3000);
        QCOMPARE(model.rowCount(), 5);
        QCOMPARE(model.runAt(0).value(QStringLiteral("run_id")).toString(), topRow);
    }

    void resizeRoundTripKeepsNavigationAnchor()
    {
        mr::MockBackend backend;
        mr::RunListModel model;
        model.setBackend(&backend);
        model.setFilter({{QStringLiteral("include_deleted"), true}});
        QTRY_VERIFY_WITH_TIMEOUT(!model.isLoading(), 3000);
        QVERIFY(model.total() > 90);
        model.goToPage(10);
        QTRY_VERIFY_WITH_TIMEOUT(!model.isLoading(), 3000);
        const QString anchor = model.runAt(0).value(QStringLiteral("run_id")).toString();
        QVERIFY(!anchor.isEmpty());

        for (int cycle = 0; cycle < 3; ++cycle) {
            model.setPageSize(11);
            QCOMPARE(model.page(), 9);
            QTRY_VERIFY_WITH_TIMEOUT(!model.isLoading(), 3000);
            QCOMPARE(model.runAt(2).value(QStringLiteral("run_id")).toString(), anchor);
            model.setPageSize(10);
            QCOMPARE(model.page(), 10);
            QTRY_VERIFY_WITH_TIMEOUT(!model.isLoading(), 3000);
            QCOMPARE(model.runAt(0).value(QStringLiteral("run_id")).toString(), anchor);
        }

        model.setPageSize(11);
        QTRY_VERIFY_WITH_TIMEOUT(!model.isLoading(), 3000);
        model.goToPage(3); // Explicit navigation establishes row 22 as the new anchor.
        QTRY_VERIFY_WITH_TIMEOUT(!model.isLoading(), 3000);
        model.setPageSize(10);
        QCOMPARE(model.page(), 3);
        QTRY_VERIFY_WITH_TIMEOUT(!model.isLoading(), 3000);
        model.sortBy(QStringLiteral("duty_name"));
        QTRY_VERIFY_WITH_TIMEOUT(!model.isLoading(), 3000);
        model.setPageSize(11);
        QCOMPARE(model.page(), 1);
        QTRY_VERIFY_WITH_TIMEOUT(!model.isLoading(), 3000);
        model.goToPage(3);
        QTRY_VERIFY_WITH_TIMEOUT(!model.isLoading(), 3000);
        model.setFilter({});
        QTRY_VERIFY_WITH_TIMEOUT(!model.isLoading(), 3000);
        model.setPageSize(10);
        QCOMPARE(model.page(), 1);
        QTRY_VERIFY_WITH_TIMEOUT(!model.isLoading(), 3000);
    }

    // Review K1-8: the history list opens sorted by its 日期 column (history_date),
    // so imported rows with only an original-site date sit among the others and the
    // column shows the sort arrow; matched_at_utc put them all at the end.
    void sortsByTheDateColumnByDefault()
    {
        CalendarBackend backend;
        mr::RunListModel model;
        QCOMPARE(model.sortField(), QStringLiteral("history_date"));
        QVERIFY(!model.sortAscending());
        model.setBackend(&backend);
        model.reload();
        QCOMPARE(backend.calls.last().type, QStringLiteral("QueryRuns"));
        const QJsonObject sort = backend.calls.last().payload.value(QStringLiteral("sort")).toObject();
        QCOMPARE(sort.value(QStringLiteral("field")).toString(), QStringLiteral("history_date"));
        QCOMPARE(sort.value(QStringLiteral("direction")).toString(), QStringLiteral("desc"));
    }

    void clampsAnchorWhenTotalShrinks()
    {
        CalendarBackend backend;
        mr::RunListModel model;
        model.setBackend(&backend);
        model.reload();
        backend.answerNextQuery(1, 100);
        model.goToPage(10);
        backend.answerNextQuery(10, 100);
        model.reload();
        backend.answerNextQuery(10, 35); // The real repository retains the requested page even when now empty.
        QCOMPARE(model.page(), 4); // The model immediately requests the last surviving page.
        QVERIFY(model.isLoading());
        backend.answerNextQuery(4, 35);
        model.setPageSize(7);
        QCOMPARE(model.page(), 5); // Last surviving row (34) stays in view.
        backend.answerNextQuery(5, 35);
        model.setPageSize(10);
        QCOMPARE(model.page(), 4);
        backend.answerNextQuery(4, 35);

        model.reload();
        backend.answerNextQuery(1, 0);
        backend.answerNextQuery(1, 0); // Empty history also clamps the previous last page back to one.
        model.setPageSize(7);
        QCOMPARE(model.page(), 1);
        backend.answerNextQuery(1, 0);
    }
};
} // namespace
MR_DESKTOP_QTEST_SUITE(RunListModel, RunListModelTests, QCoreApplication)
#include "RunListModelTests.moc"
