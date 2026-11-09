#include "DesktopTestRunner.h"
#include "HistoryController.h"
#include "HistoryFilterFacets.h"
#include "IBackend.h"
#include "MockBackend.h"

#include <QDateTime>
#include <QJsonArray>
#include <QPointer>
#include <QTest>
#include <QTimeZone>

#include <functional>
#include <memory>

namespace {
class FacetBackend final : public mr::IBackend
{
public:
    struct Call { QJsonObject payload; QPointer<mr::BackendReply> reply; };
    QList<Call> calls;
    bool connected = true;
    bool synchronous = false;
    std::function<void()> onQuery;
    QString backendName() const override { return QStringLiteral("facet-test"); }
    bool isConnected() const override { return connected; }
    mr::BackendReply *request(const QString &type, const QJsonObject &payload = {}) override
    {
        auto *reply = new mr::BackendReply(QString::number(calls.size()), type, this);
        if (type == QStringLiteral("QueryRuns")) {
            calls.append({.payload = payload, .reply = reply});
            if (onQuery) onQuery();
            if (synchronous) reply->succeed(answer(17));
        }
        return reply;
    }
    mr::BackendReply *requestWithId(const QString &type, const QJsonObject &payload,
                                   const QString &) override { return request(type, payload); }
    static QJsonObject answer(int total)
    {
        return {{QStringLiteral("items"), QJsonArray{}},
                {QStringLiteral("page_info"), QJsonObject{{QStringLiteral("total"), total}}}};
    }
    void completeBatch(int first, int total) const
    {
        for (int i = first; i < first + 9; ++i)
            if (calls.at(i).reply && !calls.at(i).reply->isFinished())
                calls.at(i).reply->succeed(answer(total + i - first));
    }
};

class HistoryFilterFacetsTests final : public QObject
{
    Q_OBJECT
private Q_SLOTS:
    void deletionDuringQueryDispatchStopsTheBatch()
    {
        FacetBackend backend;
        auto facets = std::make_unique<mr::HistoryFilterFacets>(&backend);
        backend.onQuery = [&facets] { facets.reset(); };
        facets->setActive(true);
        QTRY_VERIFY(!facets);
        QCOMPARE(backend.calls.size(), 1);
    }

    void refreshDuringQueryDispatchStartsOnlyTheNewBatch()
    {
        FacetBackend backend;
        mr::HistoryFilterFacets facets(&backend);
        bool firstQuery = true;
        backend.onQuery = [&] {
            if (firstQuery) {
                firstQuery = false;
                facets.refresh();
            }
        };
        facets.setActive(true);
        QTRY_COMPARE(backend.calls.size(), 10);
        backend.completeBatch(1, 30);
        QTRY_VERIFY(!facets.loading());
        QCOMPARE(facets.dateCounts().value("all").toInt(), 30);
        backend.calls.first().reply->succeed(FacetBackend::answer(999));
        QCOMPARE(facets.dateCounts().value("all").toInt(), 30);
    }

    void defaultsAndAcceptedFiltersUseMatchTime()
    {
        FacetBackend backend;
        mr::HistoryController history(&backend);
        QCOMPARE(history.historyFilter(), (QVariantMap{{"date_field", "matched_at_utc"}}));
        QCOMPARE(history.runs()->sortField(), QStringLiteral("history_date"));
        history.setHistoryFilter({{"date_field", "history_date"}, {"history_from_day", "2026-09-01"},
                                  {"history_to_day", "2026-09-03"}, {"from_utc", "2026-09-01T00:00:00Z"}});
        const QJsonObject filter = backend.calls.last().payload.value("filter").toObject();
        QCOMPARE(filter.value("date_field").toString(), QStringLiteral("matched_at_utc"));
        QVERIFY(!filter.contains("history_from_day"));
        QVERIFY(!filter.contains("history_to_day"));
        QVERIFY(filter.contains("from_utc"));
        history.resetHistoryFilter();
        QCOMPARE(history.historyFilter(), (QVariantMap{{"date_field", "matched_at_utc"}}));
    }

    void lazyQueriesReplaceOnlyTheirOwnDimensionAndPublishAtomically()
    {
        FacetBackend backend;
        mr::HistoryFilterFacets facets(&backend);
        const QJsonObject filter{{"region", "TC"}, {"from_utc", "2026-01-01T00:00:00Z"},
            {"to_utc", "2026-02-01T00:00:00Z"}, {"text", "Scholar"}, {"deleted_only", true}};
        facets.setFilter(filter);
        QCoreApplication::processEvents();
        QVERIFY(backend.calls.isEmpty());
        facets.setActive(true);
        QTRY_COMPARE(backend.calls.size(), 9);
        QVERIFY(facets.loading());
        for (int i = 0; i < 9; ++i) {
            const QJsonObject payload = backend.calls.at(i).payload;
            QCOMPARE(payload.value("page_size").toInt(), 1);
            QCOMPARE(payload.value("page").toInt(), 1);
            const QJsonObject queried = payload.value("filter").toObject();
            QCOMPARE(queried.value("text"), filter.value("text"));
            QCOMPARE(queried.value("deleted_only"), filter.value("deleted_only"));
            QCOMPARE(queried.value("date_field").toString(), QStringLiteral("matched_at_utc"));
            if (i < 5) QCOMPARE(queried.value("region").toString(), QStringLiteral("TC"));
            else QCOMPARE(queried.value("from_utc"), filter.value("from_utc"));
        }
        QVERIFY(!backend.calls.at(0).payload.value("filter").toObject().contains("from_utc"));
        QVERIFY(!backend.calls.at(5).payload.value("filter").toObject().contains("region"));
        for (int i = 0; i < 8; ++i) backend.calls.at(i).reply->succeed(FacetBackend::answer(100 + i));
        QVERIFY(facets.dateCounts().isEmpty());
        QVERIFY(facets.regionCounts().isEmpty());
        backend.calls.at(8).reply->succeed(FacetBackend::answer(108));
        QVERIFY(!facets.loading());
        QCOMPARE(facets.dateCounts().value("all").toInt(), 100);
        QCOMPARE(facets.regionCounts().value("all").toInt(), 105);
        QCOMPARE(facets.regionCounts().value("GLOBAL").toInt(), 108);
        QVERIFY(!facets.regionCounts().contains("UNKNOWN"));
    }

    void lateRepliesAndHiddenRequestsCannotReplaceCurrentCounts()
    {
        FacetBackend backend;
        mr::HistoryFilterFacets facets(&backend);
        facets.setActive(true);
        QTRY_COMPARE(backend.calls.size(), 9);
        facets.setFilter({{"text", "new"}});
        QTRY_COMPARE(backend.calls.size(), 18);
        backend.completeBatch(0, 1000);
        QVERIFY(facets.loading());
        QVERIFY(facets.dateCounts().isEmpty());
        backend.completeBatch(9, 10);
        QCOMPARE(facets.dateCounts().value("all").toInt(), 10);
        facets.refresh();
        QTRY_COMPARE(backend.calls.size(), 27);
        facets.setActive(false);
        backend.completeBatch(18, 2000);
        QVERIFY(!facets.loading());
        QVERIFY(facets.dateCounts().isEmpty());
        facets.setFilter({{"text", "hidden"}});
        facets.refresh();
        QCoreApplication::processEvents();
        QCOMPARE(backend.calls.size(), 27);
        facets.setActive(true);
        QTRY_COMPARE(backend.calls.size(), 36);
    }

    void errorsAndMalformedTotalsAreNotZero()
    {
        FacetBackend backend;
        mr::HistoryFilterFacets facets(&backend);
        facets.setActive(true);
        QTRY_COMPARE(backend.calls.size(), 9);
        backend.calls.at(0).reply->fail("ERR_TIMEOUT", "timeout");
        backend.completeBatch(0, 0);
        QVERIFY(!facets.loading());
        QVERIFY(!facets.error().isEmpty());
        QVERIFY(facets.dateCounts().isEmpty());
        facets.refresh();
        QTRY_COMPARE(backend.calls.size(), 18);
        backend.calls.at(9).reply->succeed({{"page_info", QJsonObject{{"total", -1}}}});
        backend.completeBatch(9, 0);
        QVERIFY(!facets.error().isEmpty());
        QVERIFY(facets.regionCounts().isEmpty());
        facets.refresh();
        QTRY_COMPARE(backend.calls.size(), 27);
        for (int i = 18; i < 27; ++i) backend.calls.at(i).reply->succeed(FacetBackend::answer(0));
        QVERIFY(facets.error().isEmpty());
        QCOMPARE(facets.dateCounts().value("all").toInt(), 0);
    }

    void synchronousRepliesAndBackendLifetimeAreSafe()
    {
        auto *backend = new FacetBackend;
        backend->synchronous = true;
        mr::HistoryFilterFacets facets(backend);
        facets.setActive(true);
        QTRY_COMPARE(facets.dateCounts().value("all").toInt(), 17);
        QVERIFY(!facets.loading());
        delete backend;
        QCoreApplication::processEvents();
        QVERIFY(!facets.loading());
        QVERIFY(facets.dateCounts().isEmpty());
        QVERIFY(!facets.error().isEmpty());
        facets.refresh();
        QCoreApplication::processEvents();
        QVERIFY(!facets.loading());
    }

    void presetsUseLocalCalendarDays()
    {
        FacetBackend backend;
        mr::HistoryFilterFacets facets(&backend);
        const QDate today = QDate::currentDate();
        QCOMPARE(facets.dateRange("all"), (QVariantMap{{"start", ""}, {"end", ""}}));
        QCOMPARE(facets.dateRange("last7").value("start").toString(), today.addDays(-6).toString(Qt::ISODate));
        QCOMPARE(facets.dateRange("last30").value("start").toString(), today.addDays(-29).toString(Qt::ISODate));
        QCOMPARE(facets.dateRange("month").value("start").toString(), QDate(today.year(), today.month(), 1).toString(Qt::ISODate));
        facets.setActive(true);
        QTRY_COMPARE(backend.calls.size(), 9);
        const auto filter = backend.calls.at(2).payload.value("filter").toObject();
        const auto start = QDateTime::fromString(filter.value("from_utc").toString(), Qt::ISODateWithMs).toLocalTime();
        const auto end = QDateTime::fromString(filter.value("to_utc").toString(), Qt::ISODateWithMs).toLocalTime();
        QCOMPARE(start.date(), today.addDays(-6));
        QCOMPARE(start.time(), QTime(0, 0));
        QCOMPARE(end.date(), today);
        QCOMPARE(end.time(), QTime(23, 59, 59, 999));
    }

    void cancelledFilterAndRecordReloadKeepTheAppliedContext()
    {
        FacetBackend backend;
        mr::HistoryController history(&backend);
        auto *facets = history.filterFacets();
        facets->setActive(true);
        QTRY_COMPARE(backend.calls.size(), 9);
        backend.completeBatch(0, 10);
        history.setRunChecked({{"run_id", "selected"}, {"revision", 1}}, true);
        history.setHistoryFilter({{"text", "pending"}});
        QVERIFY(history.filterConfirmationPending());
        QCoreApplication::processEvents();
        QCOMPARE(backend.calls.size(), 9);
        history.confirmHistoryFilterChange(false);
        QCOMPARE(facets->dateCounts().value("all").toInt(), 10);
        history.runs()->reload();
        QTRY_COMPARE(backend.calls.size(), 19); // One page read, followed by nine count reads.
        QVERIFY(facets->dateCounts().isEmpty());
        for (int i = 10; i < 19; ++i)
            QVERIFY(!backend.calls.at(i).payload.value("filter").toObject().contains("text"));
        backend.completeBatch(10, 20);
        QCOMPARE(facets->dateCounts().value("all").toInt(), 20);
        backend.connected = false;
        Q_EMIT backend.connectionChanged();
        QVERIFY(!facets->loading());
        QVERIFY(!facets->error().isEmpty());
        backend.connected = true;
        Q_EMIT backend.connectionChanged();
        QTRY_COMPARE(backend.calls.size(), 28);
    }

    void countsUseFullMockResultsRatherThanTheLoadedPage()
    {
        mr::MockBackend backend;
        mr::HistoryFilterFacets facets(&backend);
        bool completed = false;
        int expectedTotal = -1;
        qsizetype pageRows = -1;
        backend.queryRuns({{"date_field", "matched_at_utc"}}, 1, 1)->whenDone(this,
            [&](bool ok, const QVariantMap &payload, const QString &, const QString &) {
            completed = ok;
            expectedTotal = payload.value("page_info").toMap().value("total").toInt();
            pageRows = payload.value("items").toList().size();
        });
        QTRY_VERIFY(completed);
        QCOMPARE(pageRows, 1);
        QVERIFY(expectedTotal > pageRows);
        facets.setActive(true);
        QTRY_VERIFY(!facets.loading());
        QVERIFY(facets.error().isEmpty());
        QCOMPARE(facets.dateCounts().value("all").toInt(), expectedTotal);
        QCOMPARE(facets.regionCounts().value("all").toInt(), expectedTotal);
    }

    void repeatedReloadInvalidatesCountsWhilePageRequestIsStillLoading()
    {
        FacetBackend backend;
        mr::HistoryController history(&backend);
        auto *facets = history.filterFacets();
        facets->setActive(true);
        QTRY_COMPARE(backend.calls.size(), 9);
        backend.completeBatch(0, 10);
        history.runs()->reload();
        QTRY_COMPARE(backend.calls.size(), 19);
        QVERIFY(history.runs()->isLoading());
        QVERIFY(!backend.calls.at(9).reply->isFinished());
        history.runs()->reload();
        QTRY_COMPARE(backend.calls.size(), 29);
        QVERIFY(!backend.calls.at(9).reply->isFinished());
        QVERIFY(!backend.calls.at(19).reply->isFinished());
        backend.completeBatch(10, 1000);
        QVERIFY(facets->loading());
        QVERIFY(facets->dateCounts().isEmpty());
        backend.completeBatch(20, 20);
        QVERIFY(!facets->loading());
        QCOMPARE(facets->dateCounts().value("all").toInt(), 20);
    }
};
} // namespace

MR_DESKTOP_QTEST_SUITE(HistoryFilterFacets, HistoryFilterFacetsTests, QCoreApplication)
#include "HistoryFilterFacetsTests.moc"
