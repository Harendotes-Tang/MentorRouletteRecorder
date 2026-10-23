#include "AppSettings.h"
#include "IBackend.h"
#include "StatisticsOverlayController.h"

#include <QDateTime>
#include <QJsonObject>
#include <QPointer>
#include <QSettings>
#include <QTemporaryDir>
#include <QTest>
#include <QTimer>
#include <QTimeZone>
#include <algorithm>
#include <memory>

namespace {
class RangeBackend final : public mr::IBackend
{
public:
    QString backendName() const override { return QStringLiteral("range-fixture"); }
    bool isConnected() const override { return connected; }
    mr::BackendReply *request(const QString &type, const QJsonObject &payload) override
    {
        auto *reply = new mr::BackendReply(QString::number(requests.size()), type, this);
        requests.append(payload);
        types.append(type);
        replies.append(reply);
        return reply;
    }
    void setConnected(bool value) { connected = value; Q_EMIT connectionChanged(); }
    bool connected = true;
    QList<QJsonObject> requests;
    QStringList types;
    QList<QPointer<mr::BackendReply>> replies;
};

QStringList legacyModules()
{
    return {QStringLiteral("progress"), QStringLiteral("current_duty"), QStringLiteral("elapsed"),
            QStringLiteral("dungeons"), QStringLiteral("jobs"), QStringLiteral("pending_work")};
}

void tick(mr::StatisticsOverlayController &overlay)
{
    auto *timer = overlay.findChild<QTimer *>(QStringLiteral("statisticsOverlayCalendarTimer"));
    QVERIFY(timer);
    QVERIFY(QMetaObject::invokeMethod(timer, "timeout", Qt::DirectConnection));
}
class StatisticsOverlayRangeTests : public QObject
{
    Q_OBJECT
private Q_SLOTS:
    void periodPreferencesAndReset()
    {
        QTemporaryDir directory;
        QVERIFY(directory.isValid());
        const QString path = directory.filePath(QStringLiteral("overlay.ini"));
        {
            mr::AppSettings settings(path, nullptr);
            mr::StatisticsOverlayController overlay(&settings);
            QCOMPARE(overlay.attemptPeriod(), QStringLiteral("all"));
            QCOMPARE(overlay.attemptPeriodOptions().size(), 4);
            overlay.setAttemptPeriod(QStringLiteral("last7"));
            QCOMPARE(settings.statisticsOverlayAttemptPeriod(), QStringLiteral("last7"));
        }
        {
            mr::AppSettings settings(path, nullptr);
            mr::StatisticsOverlayController overlay(&settings);
            QCOMPARE(overlay.attemptPeriod(), QStringLiteral("last7"));
            overlay.reset();
            QCOMPARE(overlay.attemptPeriod(), QStringLiteral("all"));
            settings.setStatisticsOverlayAttemptPeriod(QStringLiteral("corrupt"));
        }
        mr::AppSettings settings(path, nullptr);
        mr::StatisticsOverlayController invalid(&settings);
        QCOMPARE(invalid.attemptPeriod(), QStringLiteral("all"));
    }

    void legacySelectionMigration_data()
    {
        QTest::addColumn<QStringList>("enabled");
        QTest::addColumn<bool>("gainsAttempts");
        QTest::newRow("complete") << legacyModules() << true;
        QTest::newRow("subset") << QStringList{QStringLiteral("jobs"), QStringLiteral("progress")} << false;
        QTest::newRow("empty") << QStringList{} << false;
    }
    void legacySelectionMigration()
    {
        QFETCH(QStringList, enabled);
        QFETCH(bool, gainsAttempts);
        QTemporaryDir directory;
        QVERIFY(directory.isValid());
        mr::AppSettings settings(directory.filePath(QStringLiteral("overlay.ini")), nullptr);
        QStringList order = legacyModules();
        std::ranges::reverse(order);
        settings.setStatisticsOverlayModules(enabled);
        settings.setStatisticsOverlayModuleOrder(order);
        {
            mr::StatisticsOverlayController overlay(&settings);
            QCOMPARE(overlay.modules().contains(QStringLiteral("attempts")), gainsAttempts);
            const QVariantList rows = overlay.moduleRows();
            QCOMPARE(rows.first().toMap().value(QStringLiteral("key")).toString(), QStringLiteral("attempts"));
            QStringList actual;
            for (const QVariant &row : rows.mid(1))
                actual.append(row.toMap().value(QStringLiteral("key")).toString());
            QCOMPARE(actual, order);
            if (gainsAttempts)
                overlay.toggleModule(QStringLiteral("attempts"));
        }
        // Once migrated, explicitly disabling only the new module stays disabled.
        mr::StatisticsOverlayController reopened(&settings);
        QVERIFY(!reopened.modules().contains(QStringLiteral("attempts")));
    }

    void localCalendarBoundaries_data()
    {
        QTest::addColumn<QString>("period");
        QTest::addColumn<QDate>("today");
        QTest::addColumn<QByteArray>("zoneId");
        QTest::addColumn<QString>("first");
        QTest::addColumn<QString>("from");
        QTest::addColumn<QString>("to");
        QTest::newRow("today") << QStringLiteral("today") << QDate(2026, 10, 21) << QByteArray("Asia/Singapore")
            << QStringLiteral("2026-10-21") << QStringLiteral("2026-10-20T16:00:00.000Z") << QStringLiteral("2026-10-21T15:59:59.999Z");
        QTest::newRow("spring-23-hours") << QStringLiteral("today") << QDate(2026, 3, 8) << QByteArray("America/New_York")
            << QStringLiteral("2026-03-08") << QStringLiteral("2026-03-08T05:00:00.000Z") << QStringLiteral("2026-03-09T03:59:59.999Z");
        QTest::newRow("autumn-25-hours") << QStringLiteral("today") << QDate(2026, 11, 1) << QByteArray("America/New_York")
            << QStringLiteral("2026-11-01") << QStringLiteral("2026-11-01T04:00:00.000Z") << QStringLiteral("2026-11-02T04:59:59.999Z");
        QTest::newRow("seven-cross-month") << QStringLiteral("last7") << QDate(2026, 3, 3) << QByteArray("Asia/Singapore")
            << QStringLiteral("2026-02-25") << QStringLiteral("2026-02-24T16:00:00.000Z") << QStringLiteral("2026-03-03T15:59:59.999Z");
        QTest::newRow("thirty-cross-year") << QStringLiteral("last30") << QDate(2026, 1, 2) << QByteArray("Asia/Singapore")
            << QStringLiteral("2025-12-04") << QStringLiteral("2025-12-03T16:00:00.000Z") << QStringLiteral("2026-01-02T15:59:59.999Z");
    }
    void localCalendarBoundaries()
    {
        QFETCH(QString, period);
        QFETCH(QDate, today);
        QFETCH(QByteArray, zoneId);
        QFETCH(QString, first);
        QFETCH(QString, from);
        QFETCH(QString, to);
        const QTimeZone zone(zoneId);
        QVERIFY(zone.isValid());
        const auto filter = mr::StatisticsOverlayController::attemptFilter(period, today, zone);
        QCOMPARE(filter.value(QStringLiteral("date_field")).toString(), QStringLiteral("history_date"));
        QCOMPARE(filter.value(QStringLiteral("from_utc")).toString(), from);
        QCOMPARE(filter.value(QStringLiteral("to_utc")).toString(), to);
        // The paired day literals keep source-date-only imports on their own history day.
        QCOMPARE(filter.value(QStringLiteral("history_from_day")).toString(), first);
        QCOMPARE(filter.value(QStringLiteral("history_to_day")).toString(), today.toString(Qt::ISODate));
        QVERIFY(mr::StatisticsOverlayController::attemptFilter(QStringLiteral("all"), today, zone).isEmpty());
    }

    void independentRepliesFailureAndLifecycle()
    {
        RangeBackend backend;
        mr::StatisticsOverlayController overlay(nullptr);
        overlay.setModules({QStringLiteral("attempts")});
        overlay.setBackend(&backend);
        overlay.refreshAttempts();
        QCOMPARE(backend.requests.size(), 0);
        overlay.show();
        QCOMPARE(backend.requests.size(), 1);
        QVERIFY(backend.requests.first().value(QStringLiteral("filter")).toObject().isEmpty());
        QVERIFY(overlay.attemptsLoading());
        QVERIFY(!overlay.attemptsKnown());
        overlay.setAttemptPeriod(QStringLiteral("today"));
        QCOMPARE(backend.requests.size(), 2);
        backend.replies.at(1)->succeed({{QStringLiteral("attempt_count"), 0}, {QStringLiteral("achievement_progress"), 987}});
        QVERIFY(overlay.attemptsKnown());
        QCOMPARE(overlay.attemptCount(), 0);
        QVERIFY(!overlay.attemptsLoading());
        backend.replies.at(0)->succeed({{QStringLiteral("attempt_count"), 100}});
        QCOMPARE(overlay.attemptCount(), 0);
        overlay.refreshAttempts();
        backend.replies.last()->fail(QStringLiteral("ERR_TEST"), QStringLiteral("fixture"));
        QVERIFY(!overlay.attemptsKnown());
        QVERIFY(!overlay.attemptsLoading());
        QVERIFY(!overlay.attemptsError().isEmpty());
        overlay.refreshAttempts();
        QVERIFY(overlay.attemptsError().isEmpty());
        backend.replies.last()->succeed({{QStringLiteral("attempt_count"), 7}});
        QCOMPARE(overlay.attemptCount(), 7);
        overlay.refreshAttempts();
        auto *late = backend.replies.last().data();
        overlay.hide();
        late->succeed({{QStringLiteral("attempt_count"), 999}});
        QVERIFY(!overlay.attemptsKnown());
        const qsizetype hiddenRequests = backend.requests.size();
        overlay.refreshAttempts();
        QCOMPARE(backend.requests.size(), hiddenRequests);
        overlay.show();
        overlay.setForm(QStringLiteral("mini"));
        const qsizetype miniRequests = backend.requests.size();
        overlay.refreshAttempts();
        QCOMPARE(backend.requests.size(), miniRequests);
        overlay.setForm(QStringLiteral("full"));
        QCOMPARE(backend.requests.size(), miniRequests + 1);
        overlay.toggleModule(QStringLiteral("attempts"));
        const qsizetype disabledRequests = backend.requests.size();
        overlay.refreshAttempts();
        QCOMPARE(backend.requests.size(), disabledRequests);
        overlay.toggleModule(QStringLiteral("attempts"));
        QCOMPARE(backend.requests.size(), disabledRequests + 1);
    }

    void malformedBackendAndDestruction()
    {
        auto backend = std::make_unique<RangeBackend>();
        mr::StatisticsOverlayController overlay(nullptr);
        overlay.setModules({QStringLiteral("attempts")});
        overlay.setBackend(backend.get());
        overlay.show();
        backend->replies.last()->succeed({});
        QVERIFY(!overlay.attemptsKnown());
        QVERIFY(!overlay.attemptsError().isEmpty());
        overlay.refreshAttempts();
        backend->replies.last()->succeed({{QStringLiteral("attempt_count"), -1}});
        QVERIFY(!overlay.attemptsKnown());
        overlay.refreshAttempts();
        backend->replies.last()->succeed({{QStringLiteral("attempt_count"), 1.25}});
        QVERIFY(!overlay.attemptsKnown());
        backend->setConnected(false);
        QVERIFY(!overlay.attemptsLoading());
        backend->setConnected(true);
        QVERIFY(overlay.attemptsLoading());
        backend.reset();
        QVERIFY(!overlay.attemptsLoading());
        QVERIFY(!overlay.attemptsKnown());
        QVERIFY(!overlay.attemptsError().isEmpty());
        overlay.refreshAttempts();
    }

    void dateAndTimeZoneRefreshOnlyWhileApplicable()
    {
        QDateTime now(QDate(2026, 10, 21), QTime(23, 59), QTimeZone("Asia/Singapore"));
        RangeBackend backend;
        mr::StatisticsOverlayController overlay(nullptr, nullptr, [&now] { return now; });
        overlay.setModules({QStringLiteral("attempts")});
        overlay.setBackend(&backend);
        overlay.setAttemptPeriod(QStringLiteral("today"));
        overlay.show();
        QCOMPARE(backend.requests.size(), 1);
        tick(overlay);
        QCOMPARE(backend.requests.size(), 1);
        now = now.addSecs(120);
        tick(overlay);
        QCOMPARE(backend.requests.size(), 2);
        QCOMPARE(backend.requests.last().value(QStringLiteral("filter")).toObject()
                     .value(QStringLiteral("history_from_day")).toString(), QStringLiteral("2026-10-22"));
        now = now.toTimeZone(QTimeZone("America/New_York"));
        tick(overlay);
        QCOMPARE(backend.requests.size(), 3);
        overlay.hide();
        now = now.addDays(1);
        tick(overlay);
        QCOMPARE(backend.requests.size(), 3);
        overlay.show();
        QCOMPARE(backend.requests.size(), 4);
    }

    void periodModelsShareFiltersAndRejectLateReplies()
    {
        RangeBackend backend;
        QDateTime now(QDate(2026, 10, 22), QTime(12, 0), QTimeZone("Asia/Singapore"));
        mr::StatisticsOverlayController overlay(nullptr, nullptr, [&now] { return now; });
        overlay.setBackend(&backend);
        overlay.show();
        QCOMPARE(backend.requests.size(), 3);
        const auto rows = [](int count) {
            return QJsonObject::fromVariantMap({{QStringLiteral("items"), QVariantList{QVariantMap{
                {QStringLiteral("content_id"), 1}, {QStringLiteral("duty_name"), QStringLiteral("fixture")},
                {QStringLiteral("job_id"), 19}, {QStringLiteral("attempt_count"), count},
                {QStringLiteral("completed_count"), count}}}},
                {QStringLiteral("page_info"), QVariantMap{{QStringLiteral("total"), 1},
                    {QStringLiteral("page"), 1}, {QStringLiteral("page_size"), 200}}}});
        };
        const QStringList types{QStringLiteral("GetDungeonStats"), QStringLiteral("GetJobStats"), QStringLiteral("GetDashboardStats")};
        for (const auto &period : {QStringLiteral("today"), QStringLiteral("last7"), QStringLiteral("last30"), QStringLiteral("all")}) {
            const qsizetype first = backend.requests.size();
            overlay.setAttemptPeriod(period);
            QCOMPARE(backend.requests.size(), first + 3);
            const auto filter = mr::StatisticsOverlayController::attemptFilter(period, now.date(), now.timeZone());
            for (int i = 0; i < 3; ++i) {
                QCOMPARE(backend.types.at(first + i), types.at(i));
                QCOMPARE(backend.requests.at(first + i).value(QStringLiteral("filter")).toObject(), filter);
            }
        }
        const qsizetype last = backend.requests.size() - 3;
        backend.replies.at(last)->succeed(rows(8));
        backend.replies.at(last + 1)->succeed(rows(8));
        backend.replies.at(last + 2)->succeed({{QStringLiteral("attempt_count"), 8}});
        QCOMPARE(overlay.dungeons()->totalAttemptCount(), 8);
        QCOMPARE(overlay.jobs()->totalAttemptCount(), 8);
        backend.replies.at(0)->succeed(rows(99));
        backend.replies.at(1)->fail(QStringLiteral("OLD"), QStringLiteral("old"));
        QCOMPARE(overlay.dungeons()->totalAttemptCount(), 8);
        QCOMPARE(overlay.jobs()->totalAttemptCount(), 8);
        QVERIFY(overlay.jobs()->loadError().isEmpty());

        overlay.setModules({QStringLiteral("dungeons"), QStringLiteral("jobs")});
        QVERIFY(overlay.hasPeriodStatistics());
        const qsizetype pending = backend.requests.size() - 2;
        overlay.hide();
        backend.replies.at(pending)->succeed(rows(77));
        backend.replies.at(pending + 1)->succeed(rows(77));
        QCOMPARE(overlay.dungeons()->totalAttemptCount(), 8);
        QCOMPARE(overlay.jobs()->totalAttemptCount(), 8);
        const qsizetype hidden = backend.requests.size();
        tick(overlay);
        QCOMPARE(backend.requests.size(), hidden);
        overlay.show();
        QCOMPARE(backend.requests.size(), hidden + 2);
        now = now.addDays(1);
        tick(overlay);
        QCOMPARE(backend.requests.size(), hidden + 4);
        overlay.setModules({QStringLiteral("jobs")});
        const qsizetype onlyJobs = backend.requests.size();
        overlay.refreshAttempts();
        QCOMPARE(backend.requests.size(), onlyJobs + 1);
        QCOMPARE(backend.types.last(), QStringLiteral("GetJobStats"));
        backend.replies.last()->fail(QStringLiteral("ERR_TEST"), QStringLiteral("current failure"));
        QVERIFY(!overlay.jobs()->loadError().isEmpty());
        QCOMPARE(overlay.jobs()->rowCount(), 0);
        overlay.setModules({QStringLiteral("progress")});
        QVERIFY(!overlay.hasPeriodStatistics());
        const qsizetype stopped = backend.requests.size();
        overlay.refreshAttempts();
        tick(overlay);
        QCOMPARE(backend.requests.size(), stopped);
    }
};
} // namespace

QTEST_MAIN(StatisticsOverlayRangeTests)
#include "StatisticsOverlayRangeTests.moc"
