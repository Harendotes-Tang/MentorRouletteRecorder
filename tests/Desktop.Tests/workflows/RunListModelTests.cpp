#include "DesktopTestRunner.h"
#include "IBackend.h"
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
};
} // namespace
MR_DESKTOP_QTEST_SUITE(RunListModel, RunListModelTests, QCoreApplication)
#include "RunListModelTests.moc"
