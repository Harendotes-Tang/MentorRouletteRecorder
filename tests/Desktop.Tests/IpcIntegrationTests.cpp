// ---------------------------------------------------------------------------
// Live round trip against the real Collector.
//
// The Collector is started as a child process on a temporary database, so this
// test exercises the shipping IpcBackend / IpcClient against the shipping
// server rather than against a stub of it. Everything it asserts is a fact of
// contracts/ipc-v1.schema.json, not of this test's fixture data.
//
// It skips - it does not fail - when MentorRecorder.Collector.exe is not next
// to the Desktop binary, so a Qt-only checkout still runs the suite.
//
// Exactly one Collector may exist while this runs, and it is the one below.
// Two slots construct an AppController, whose constructor supervises a child
// Collector of its own whenever the backend calls itself "ipc" - with no --db
// and no --pipe, i.e. against the user's real database. MR_COLLECTOR_PATH is
// authoritative and exclusive in CollectorProcess::resolveDefaultExecutable, so
// tests/Desktop.Tests/CMakeLists.txt points it at a path that cannot exist.
// main() below repeats that, for a run started by hand rather than by CTest.
// ---------------------------------------------------------------------------

#include "TestCollectorGuard.h"
#include "AppController.h"
#include "AppSettings.h"
#include "IBackend.h"
#include "IpcBackend.h"
#include "IpcClient.h"
#include "PipeName.h"
#include "SpeechController.h"

#include <QCoreApplication>
#include <QCryptographicHash>
#include <QDir>
#include <QElapsedTimer>
#include <QFile>
#include <QGuiApplication>
#include <QJsonArray>
#include <QJsonObject>
#include <QJsonDocument>
#include <QProcess>
#include <QProcessEnvironment>
#include <QRegularExpression>
#include <QSignalSpy>
#include <QTemporaryDir>
#include <QUuid>
#include <QTest>

namespace {

/// Absolute path of the Collector next to the test binary, or an empty string.
QString collectorPath()
{
    for (const QString &directory :
         {QCoreApplication::applicationDirPath(),
          QDir(QCoreApplication::applicationDirPath()).absoluteFilePath(
              QStringLiteral("../../src/Desktop"))}) {
        const QString candidate =
            QDir(directory).absoluteFilePath(QStringLiteral("MentorRecorder.Collector.exe"));
        if (QFileInfo::exists(candidate))
            return QDir::cleanPath(candidate);
    }
    return {};
}

/// Runs one request to completion and returns the reply's outcome.
struct Outcome {
    bool finished = false;
    bool ok = false;
    QVariantMap payload;
    QString code;
    QString message;
};

Outcome await(mr::BackendReply *reply, int timeoutMs = 15000)
{
    Outcome outcome;
    // Destroy the subscription before outcome leaves scope, including on timeout.
    QObject subscription;
    reply->whenDone(&subscription,
                     [&outcome](bool ok, const QVariantMap &payload, const QString &code,
                                const QString &message) {
                         outcome.finished = true;
                         outcome.ok = ok;
                         outcome.payload = payload;
                         outcome.code = code;
                         outcome.message = message;
                     });
    // QTRY_VERIFY cannot be used here: it returns from the enclosing function,
    // which is not void. The loop does the same job and lets the caller assert.
    QElapsedTimer elapsed;
    elapsed.start();
    while (!outcome.finished && elapsed.elapsed() < timeoutMs)
        QCoreApplication::processEvents(QEventLoop::AllEvents, 25);
    return outcome;
}

} // namespace

class IpcIntegrationTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void initTestCase();
    void cleanupTestCase();

    void awaitDisconnectsAfterTimeout();
    void awaitReadsAlreadyFinishedReply();

    void getVersionMatchesTheContract();
    void queryRunsHonoursFilterAndPaging();
    void correctRunSurfacesTypedRefusals();
    void createManualRunEmitsLiveEvents();
    void backupDatabaseUsesTheManagedFolder();
    void dashboardStatsCarryTheServerSideTrend();
    void exportDiagnosticsReportWritesASanitizedFile();
    void appControllerSendsOnlyContractFields();
    void candidateEndpointsRemainIndependentAndExportChecksummedEvidence();
    void normalStartupRestoresFollowWithoutChangingCandidateSettings();
    void onlineSpeechKeepsTheKeyAndSendsNothing();

private:
    QTemporaryDir m_databaseDirectory;
    QProcess m_collector;
    mr::IpcBackend *m_backend = nullptr;
    QString m_seededRunId;
    int m_seededRevision = 0;
};

void IpcIntegrationTests::awaitDisconnectsAfterTimeout()
{
    class ObservableReply : public mr::BackendReply {
    public:
        using mr::BackendReply::BackendReply;
        int doneReceivers() const
        {
            return receivers(SIGNAL(done(bool,QVariantMap,QString,QString)));
        }
    };
    QObject owner;
    auto *reply = new ObservableReply(QStringLiteral("late"), QStringLiteral("BackupDatabase"), &owner);
    const Outcome timedOut = await(reply, 0);
    QVERIFY(!timedOut.finished);
    QCOMPARE(reply->doneReceivers(), 0);
    // A late response or stop() may still finish this request after await returns.
    reply->fail(QStringLiteral("ERR_INTERNAL"), QStringLiteral("late completion"));
    QVERIFY(reply->isFinished());
}

void IpcIntegrationTests::awaitReadsAlreadyFinishedReply()
{
    QObject owner;
    auto *reply = new mr::BackendReply(QStringLiteral("early"), QStringLiteral("GetStatus"), &owner);
    reply->fail(QStringLiteral("ERR_INTERNAL"), QStringLiteral("disconnected"));
    const Outcome result = await(reply, 0);
    QVERIFY(result.finished);
    QVERIFY(!result.ok);
    QCOMPARE(result.code, QStringLiteral("ERR_INTERNAL"));
    QCOMPARE(result.message, QStringLiteral("disconnected"));
}

void IpcIntegrationTests::normalStartupRestoresFollowWithoutChangingCandidateSettings()
{
    const Outcome before = await(m_backend->getCaptureSettings());
    QVERIFY(before.ok);
    const Outcome disabled = await(m_backend->updateCaptureSettings({{QStringLiteral("follow_game"), false}}));
    QVERIFY(disabled.ok);
    QCOMPARE(disabled.payload.value(QStringLiteral("follow_game")).toBool(), false);
    {
        mr::AppController controller(m_backend, nullptr);
        QTRY_VERIFY_WITH_TIMEOUT(controller.captureSettingsLoaded()
            && controller.captureSettings().value(QStringLiteral("follow_game")).toBool(), 15000);
        const Outcome confirmed = await(m_backend->getCaptureSettings());
        QVERIFY(confirmed.ok);
        QVERIFY(confirmed.payload.value(QStringLiteral("follow_game")).toBool());
        QCOMPARE(confirmed.payload.value(QStringLiteral("candidate_validation_enabled")),
                 before.payload.value(QStringLiteral("candidate_validation_enabled")));
        QCOMPARE(confirmed.payload.value(QStringLiteral("research_payload_opcodes")),
                 before.payload.value(QStringLiteral("research_payload_opcodes")));
    }
    const Outcome restored = await(m_backend->updateCaptureSettings({
        {QStringLiteral("follow_game"), QJsonValue::fromVariant(before.payload.value(QStringLiteral("follow_game")))}}));
    QVERIFY(restored.ok);
}

// 在线语音 through the shipping wrappers: the key goes in and never comes back,
// and with the kill switch set a fully configured service still sends nothing.
void IpcIntegrationTests::onlineSpeechKeepsTheKeyAndSendsNothing()
{
    const Outcome initial = await(m_backend->getSpeechSettings());
    if (!initial.ok && (initial.code == QLatin1String("ERR_UNKNOWN_MESSAGE")
                        || initial.code == QLatin1String("ERR_BAD_REQUEST"))) {
        // The settings page must then hide online speech altogether.
        mr::SpeechController speech;
        speech.setBackend(m_backend);
        QTRY_VERIFY_WITH_TIMEOUT(speech.loaded(), 15000);
        QVERIFY(!speech.supported());
        QSKIP("The staged Collector predates online speech.");
    }
    QVERIFY2(initial.ok, qPrintable(initial.code + QLatin1Char(' ') + initial.message));
    QCOMPARE(initial.payload.value(QStringLiteral("provider")).toString(), QStringLiteral("none"));
    QCOMPARE(initial.payload.value(QStringLiteral("has_key")).toBool(), false);
    QCOMPARE(initial.payload.value(QStringLiteral("configured")).toBool(), false);
    QVERIFY(!initial.payload.value(QStringLiteral("azure_voices")).toList().isEmpty());
    QVERIFY(!initial.payload.value(QStringLiteral("openai_voices")).toList().isEmpty());

    const QString key = QStringLiteral("test-key-0000");
    const Outcome saved = await(m_backend->updateSpeechSettings({
        {QStringLiteral("provider"), QStringLiteral("azure")},
        {QStringLiteral("azure_region"), QStringLiteral("eastasia")},
        {QStringLiteral("voice"), QStringLiteral("zh-CN-XiaoxiaoNeural")},
        {QStringLiteral("api_key"), key}}));
    QVERIFY2(saved.ok, qPrintable(saved.code + QLatin1Char(' ') + saved.message));
    QCOMPARE(saved.payload.value(QStringLiteral("has_key")).toBool(), true);
    QCOMPARE(saved.payload.value(QStringLiteral("configured")).toBool(), true);
    QVERIFY(saved.payload.value(QStringLiteral("target_host")).toString().startsWith(QStringLiteral("eastasia.")));
    QVERIFY(!QJsonDocument(QJsonObject::fromVariantMap(saved.payload)).toJson().contains(key.toUtf8()));

    // The controller the settings page binds to reads the same state.
    {
        mr::SpeechController speech;
        speech.setBackend(m_backend);
        QTRY_VERIFY_WITH_TIMEOUT(speech.loaded(), 15000);
        QVERIFY(speech.supported());
        QVERIFY(speech.configuredFor(QStringLiteral("azure:zh-CN-YunxiNeural")));
    }

    const Outcome spoken = await(m_backend->synthesizeSpeech(QString::fromUtf8("集成测试"), 120, true), 30000);
    QVERIFY(spoken.finished);
    QVERIFY(!spoken.ok);
    QCOMPARE(spoken.code, QStringLiteral("ERR_SPEECH_DISABLED"));

    const Outcome cleared = await(m_backend->updateSpeechSettings({
        {QStringLiteral("api_key"), QString()}, {QStringLiteral("provider"), QStringLiteral("none")}}));
    QVERIFY2(cleared.ok, qPrintable(cleared.code + QLatin1Char(' ') + cleared.message));
    QCOMPARE(cleared.payload.value(QStringLiteral("has_key")).toBool(), false);
    QCOMPARE(cleared.payload.value(QStringLiteral("provider")).toString(), QStringLiteral("none"));
}

void IpcIntegrationTests::initTestCase()
{
    const QString executable = collectorPath();
    if (executable.isEmpty())
        QSKIP("MentorRecorder.Collector.exe is not present next to the test binary.");
    QVERIFY(m_databaseDirectory.isValid());

    const QString database =
        QDir(m_databaseDirectory.path()).absoluteFilePath(QStringLiteral("ipc-tests.db"));

    // The Collector under test serves a pipe of its own. On the per-user pipe
    // the client would reach the Collector the user's Desktop launched, and
    // every fixture below would land in the user's real records.
    const QString pipeName =
        QStringLiteral("MentorRecorder.test-%1.v1")
            .arg(QUuid::createUuid().toString(QUuid::WithoutBraces));

    // --log-dir keeps this run's rotated diagnostics - and its retention sweep -
    // inside the same throw-away directory as the database. MR_DATA_DIR moves
    // the managed root as a whole, so even a code path that resolves a folder
    // without consulting the command line cannot reach the user's own data.
    const QString logDirectory =
        QDir(m_databaseDirectory.path()).absoluteFilePath(QStringLiteral("logs"));
    QProcessEnvironment environment = QProcessEnvironment::systemEnvironment();
    environment.insert(QStringLiteral("MR_DATA_DIR"), m_databaseDirectory.path());
    // The online-speech kill switch (docs/privacy-boundary.md §8.3): whatever a
    // test configures, this Collector refuses to send a sentence anywhere.
    environment.insert(QStringLiteral("MR_DISABLE_ONLINE_SPEECH"), QStringLiteral("1"));
    m_collector.setProcessEnvironment(environment);

    m_collector.setProgram(executable);

    // --log-dir is newer than the Collector some checkouts stage next to the
    // test binary, and an unknown argument makes that one print its usage and
    // exit. The fixture therefore asks for the flag and falls back to the form
    // every build understands; MR_DATA_DIR above already moves the managed root,
    // so the fallback costs tidiness, never isolation.
    const QStringList base{QStringLiteral("--serve"), QStringLiteral("--db"), database,
                           QStringLiteral("--pipe"), pipeName};
    const QList<QStringList> attempts{
        base + QStringList{QStringLiteral("--log-dir"), logDirectory}, base};
    QString refusal;
    for (const QStringList &arguments : attempts) {
        m_collector.setArguments(arguments);
        m_collector.start();
        if (!m_collector.waitForStarted(10000))
            QSKIP("The Collector could not be started on this machine.");
        // A serving Collector never returns; only a refused argument does.
        if (!m_collector.waitForFinished(1500))
            break;
        refusal = QString::fromLocal8Bit(m_collector.readAllStandardOutput()
                                         + m_collector.readAllStandardError());
    }
    if (m_collector.state() != QProcess::Running)
        QSKIP(qPrintable(QStringLiteral("The Collector refused to serve: %1").arg(refusal)));

    m_backend = new mr::IpcBackend(this, mr::ipc::localSocketServerName(pipeName));
    QTRY_VERIFY_WITH_TIMEOUT(m_backend->isConnected(), 20000);
    QCOMPARE(m_backend->client()->serverName(), mr::ipc::localSocketServerName(pipeName));

    // One manual run, so the query and mutation tests have a row to work with
    // and never depend on data another test left behind.
    QJsonObject run;
    run.insert(QStringLiteral("duty_name"), QString::fromUtf8("集成测试副本"));
    run.insert(QStringLiteral("result"), QStringLiteral("COMPLETED"));
    run.insert(QStringLiteral("matched_at_utc"), QStringLiteral("2026-09-04T01:00:00.000Z"));
    run.insert(QStringLiteral("entered_at_utc"), QStringLiteral("2026-09-04T01:00:20.000Z"));
    run.insert(QStringLiteral("ended_at_utc"), QStringLiteral("2026-09-04T01:12:20.000Z"));

    const Outcome created =
        await(m_backend->createManualRun(run, QString::fromUtf8("集成测试种子数据")));
    QVERIFY2(created.ok, qPrintable(created.code + QLatin1Char(' ') + created.message));
    m_seededRunId = created.payload.value(QStringLiteral("run_id")).toString();
    m_seededRevision = created.payload.value(QStringLiteral("revision")).toInt();
    QVERIFY(!m_seededRunId.isEmpty());
    QCOMPARE(m_seededRevision, 1);
    // MutationResult carries the audit id the correction toast quotes.
    QVERIFY(!created.payload.value(QStringLiteral("audit_event_id")).toString().isEmpty());
    QCOMPARE(created.payload.value(QStringLiteral("run"))
                 .toMap()
                 .value(QStringLiteral("source"))
                 .toString(),
             QStringLiteral("MANUAL"));
}

void IpcIntegrationTests::cleanupTestCase()
{
    if (m_backend)
        m_backend->client()->stop();
    if (m_collector.state() != QProcess::NotRunning) {
        m_collector.terminate();
        if (!m_collector.waitForFinished(5000))
            m_collector.kill();
        m_collector.waitForFinished(5000);
    }
    // No orphan may survive this test.
    QCOMPARE(m_collector.state(), QProcess::NotRunning);
}

void IpcIntegrationTests::getVersionMatchesTheContract()
{
    const Outcome version = await(m_backend->getVersion());
    QVERIFY(version.ok);
    QCOMPARE(version.payload.value(QStringLiteral("protocol_version")).toInt(), 1);
    QVERIFY(!version.payload.value(QStringLiteral("collector_version")).toString().isEmpty());

    const Outcome status = await(m_backend->getStatus());
    QVERIFY(status.ok);
    QVERIFY(status.payload.value(QStringLiteral("database_ready")).toBool());
    // The two DEC-OODLE-01 disclosures the first-run page renders.
    QVERIFY(status.payload.contains(QStringLiteral("oodle_mode")));
    QVERIFY(status.payload.contains(QStringLiteral("reads_game_executable")));
    // Boundary constants, reported so the user can verify them at runtime.
    const QVariantMap capture = status.payload.value(QStringLiteral("capture")).toMap();
    QCOMPARE(capture.value(QStringLiteral("monitor_type")).toString(),
             QStringLiteral("WinPCap"));
    QCOMPARE(capture.value(QStringLiteral("injected_hook_enabled")).toBool(), false);
}

void IpcIntegrationTests::queryRunsHonoursFilterAndPaging()
{
    QJsonObject sort;
    sort.insert(QStringLiteral("field"), QStringLiteral("matched_at_utc"));
    sort.insert(QStringLiteral("direction"), QStringLiteral("desc"));

    const Outcome all = await(m_backend->queryRuns({}, 1, 10, sort));
    QVERIFY(all.ok);
    QVERIFY(all.payload.value(QStringLiteral("items")).toList().size() >= 1);
    QCOMPARE(all.payload.value(QStringLiteral("page_info")).toMap()
                 .value(QStringLiteral("page_size")).toInt(),
             10);

    // A filter the history page assembles: one result bucket only.
    QJsonObject filter;
    filter.insert(QStringLiteral("result"), QJsonArray{QStringLiteral("LEFT_OR_ABANDONED")});
    const Outcome left = await(m_backend->queryRuns(filter, 1, 10, sort));
    QVERIFY(left.ok);
    for (const QVariant &row : left.payload.value(QStringLiteral("items")).toList()) {
        QCOMPARE(row.toMap().value(QStringLiteral("result")).toString(),
                 QStringLiteral("LEFT_OR_ABANDONED"));
    }

    // A filter that matches nothing must come back empty, never unfiltered.
    QJsonObject textFilter;
    textFilter.insert(QStringLiteral("text"), QStringLiteral("zzz-no-such-duty"));
    const Outcome none = await(m_backend->queryRuns(textFilter, 1, 10, sort));
    QVERIFY(none.ok);
    QCOMPARE(none.payload.value(QStringLiteral("items")).toList().size(), 0);
}

void IpcIntegrationTests::correctRunSurfacesTypedRefusals()
{
    QJsonObject changes;
    changes.insert(QStringLiteral("note"), QString::fromUtf8("集成测试备注"));

    // 1. A stale expected_revision is a conflict, not a silent overwrite.
    const Outcome conflict = await(m_backend->correctRun(
        m_seededRunId, m_seededRevision + 99, changes, QString::fromUtf8("冲突用例")));
    QVERIFY(!conflict.ok);
    QCOMPARE(conflict.code, QStringLiteral("ERR_REVISION_CONFLICT"));
    QVERIFY(!conflict.message.isEmpty());

    // 2. An empty reason is refused by the Collector, with a readable message.
    const Outcome noReason =
        await(m_backend->correctRun(m_seededRunId, m_seededRevision, changes, QString()));
    QVERIFY(!noReason.ok);
    QCOMPARE(noReason.code, QStringLiteral("ERR_REASON_REQUIRED"));
    QVERIFY(!noReason.message.isEmpty());

    // 3. The happy path bumps the revision and returns an audit id.
    const Outcome saved = await(m_backend->correctRun(
        m_seededRunId, m_seededRevision, changes, QString::fromUtf8("集成测试修正")));
    QVERIFY2(saved.ok, qPrintable(saved.code + QLatin1Char(' ') + saved.message));
    QCOMPARE(saved.payload.value(QStringLiteral("revision")).toInt(), m_seededRevision + 1);
    QVERIFY(!saved.payload.value(QStringLiteral("audit_event_id")).toString().isEmpty());
    m_seededRevision = saved.payload.value(QStringLiteral("revision")).toInt();

    // 4. The revision list the detail panel refreshes with really grew.
    const Outcome revisions = await(m_backend->getRunRevisions(m_seededRunId));
    QVERIFY(revisions.ok);
    QVERIFY(revisions.payload.value(QStringLiteral("items")).toList().size() >= 2);
}

void IpcIntegrationTests::createManualRunEmitsLiveEvents()
{
    const Outcome subscribed = await(m_backend->subscribeLiveEvents());
    QVERIFY(subscribed.ok);
    QVERIFY(!subscribed.payload.value(QStringLiteral("subscription_id")).toString().isEmpty());

    QSignalSpy events(m_backend, &mr::IBackend::liveEvent);

    QJsonObject run;
    run.insert(QStringLiteral("duty_name"), QString::fromUtf8("事件用例"));
    run.insert(QStringLiteral("result"), QStringLiteral("COMPLETED"));
    run.insert(QStringLiteral("matched_at_utc"), QStringLiteral("2026-09-04T02:00:00.000Z"));
    run.insert(QStringLiteral("entered_at_utc"), QStringLiteral("2026-09-04T02:00:20.000Z"));
    run.insert(QStringLiteral("ended_at_utc"), QStringLiteral("2026-09-04T02:09:20.000Z"));

    const Outcome created =
        await(m_backend->createManualRun(run, QString::fromUtf8("事件用例")));
    QVERIFY2(created.ok, qPrintable(created.code + QLatin1Char(' ') + created.message));

    // Waiting for the two kinds, not for a count. A new subscription is served
    // the bus's replay buffer first (LiveEventBus.ReplayCapacity), so events an
    // earlier test caused can satisfy "at least two have arrived" long before
    // this run's own creation does.
    const auto kindsSoFar = [&events] {
        QStringList kinds;
        for (const QList<QVariant> &signal : events)
            kinds.append(signal.at(0).toMap().value(QStringLiteral("kind")).toString());
        return kinds;
    };

    // The two kinds the shell keys on: one refreshes the list, the other the
    // statistics (AppController::handleLiveEvent).
    QTRY_VERIFY_WITH_TIMEOUT(
        kindsSoFar().contains(QStringLiteral("run_created"))
            && kindsSoFar().contains(QStringLiteral("stats_invalidated")),
        10000);
}

void IpcIntegrationTests::backupDatabaseUsesTheManagedFolder()
{
    // No target_path: the Collector picks its managed backups folder and prunes
    // it, which is exactly what the settings page's 立即备份 asks for.
    const Outcome backup = await(m_backend->backupDatabase());
    QVERIFY2(backup.ok, qPrintable(backup.code + QLatin1Char(' ') + backup.message));

    const QString target = backup.payload.value(QStringLiteral("target_path")).toString();
    QVERIFY(!target.isEmpty());
    QVERIFY(QFileInfo::exists(target));
    QVERIFY(backup.payload.value(QStringLiteral("byte_count")).toLongLong() > 0);
    // The field the settings page shows instead of a fake integrity-check button.
    QVERIFY(backup.payload.value(QStringLiteral("integrity_check_passed")).toBool());
    QVERIFY(backup.payload.contains(QStringLiteral("pruned_count")));

    // Export into this test's private directory inside the user's profile.
    // Documents may be redirected or protected; using a shared fixed filename
    // there would also let concurrent runs delete each other's export.
    const QString csv =
        QDir(m_databaseDirectory.path()).absoluteFilePath(QStringLiteral("runs.csv"));

    const Outcome exported = await(m_backend->exportCsv(csv));
    QVERIFY2(exported.ok, qPrintable(exported.code + QLatin1Char(' ') + exported.message));
    QVERIFY(QFileInfo::exists(csv));
    QVERIFY(exported.payload.value(QStringLiteral("row_count")).toInt() > 0);
    QVERIFY(exported.payload.value(QStringLiteral("byte_count")).toLongLong() > 0);
    QVERIFY(QFile::remove(csv));

    // Network exports are refused before attempting to access the destination.
    const Outcome refused = await(m_backend->exportCsv(
        QStringLiteral("//mentor-test.invalid/reports/mentor.csv")));
    QVERIFY(!refused.ok);
    QVERIFY(!refused.message.isEmpty());
    QCOMPARE(refused.code, QStringLiteral("ERR_EXPORT_FAILED"));
}

void IpcIntegrationTests::dashboardStatsCarryTheServerSideTrend()
{
    // A run matched right now, so the assertion below is about today's bucket
    // rather than about the calendar date this suite happens to run on.
    const QDateTime nowUtc = QDateTime::currentDateTimeUtc();
    QJsonObject todayRun;
    todayRun.insert(QStringLiteral("duty_name"), QString::fromUtf8("趋势用例"));
    todayRun.insert(QStringLiteral("result"), QStringLiteral("COMPLETED"));
    todayRun.insert(QStringLiteral("matched_at_utc"),
                    nowUtc.toString(QStringLiteral("yyyy-MM-ddTHH:mm:ss.zzzZ")));
    todayRun.insert(QStringLiteral("entered_at_utc"),
                    nowUtc.toString(QStringLiteral("yyyy-MM-ddTHH:mm:ss.zzzZ")));
    todayRun.insert(QStringLiteral("ended_at_utc"),
                    nowUtc.toString(QStringLiteral("yyyy-MM-ddTHH:mm:ss.zzzZ")));
    const Outcome seeded =
        await(m_backend->createManualRun(todayRun, QString::fromUtf8("趋势用例")));
    QVERIFY2(seeded.ok, qPrintable(seeded.code + QLatin1Char(' ') + seeded.message));

    // The trend is a field of the response, not something the client buckets out
    // of one page of QueryRuns - that page stops counting at 200 rows.
    struct Expectation {
        const char *granularity;
        int buckets;
        int stepDays;
    };

    for (const Expectation &expected : {Expectation{"day", 30, 1},
                                        Expectation{"week", 12, 7},
                                        Expectation{"month", 6, 0}}) {
        const Outcome stats = await(m_backend->getDashboardStats(
            {}, QString::fromLatin1(expected.granularity)));
        QVERIFY2(stats.ok, qPrintable(stats.code + QLatin1Char(' ') + stats.message));

        const QVariantMap trend = stats.payload.value(QStringLiteral("trend")).toMap();
        QCOMPARE(trend.value(QStringLiteral("granularity")).toString(),
                 QString::fromLatin1(expected.granularity));

        const QVariantList buckets = trend.value(QStringLiteral("buckets")).toList();
        QCOMPARE(int(buckets.size()), expected.buckets);

        // Gap-free, ordered, and every boundary is midnight UTC. A chart that
        // has to guess where a missing bucket went is a chart that lies.
        QDateTime previous;
        int total = 0;
        for (const QVariant &value : buckets) {
            const QVariantMap bucket = value.toMap();
            const QDateTime start = QDateTime::fromString(
                bucket.value(QStringLiteral("start_utc")).toString(), Qt::ISODateWithMs);
            QVERIFY(start.isValid());
            QCOMPARE(start.toUTC().time(), QTime(0, 0));
            QVERIFY(bucket.value(QStringLiteral("completed_count")).toInt() >= 0);
            if (previous.isValid() && expected.stepDays > 0)
                QCOMPARE(previous.addDays(expected.stepDays), start);
            previous = start;
            total += bucket.value(QStringLiteral("completed_count")).toInt();
        }

        // The run seeded above was matched a moment ago, so it is in the last
        // bucket of every granularity.
        QVERIFY(total >= 1);
        QVERIFY(buckets.last().toMap().value(QStringLiteral("completed_count")).toInt() >= 1);
    }

    // An unknown granularity is a typed refusal, not a silently defaulted one.
    QJsonObject bad;
    bad.insert(QStringLiteral("filter"), QJsonObject{});
    bad.insert(QStringLiteral("trend_granularity"), QStringLiteral("fortnight"));
    const Outcome refused = await(m_backend->request(
        QStringLiteral("GetDashboardStats"), bad));
    QVERIFY(!refused.ok);
    QCOMPARE(refused.code, QStringLiteral("ERR_BAD_REQUEST"));
}

void IpcIntegrationTests::exportDiagnosticsReportWritesASanitizedFile()
{
    const QString target =
        QDir(m_databaseDirectory.path()).absoluteFilePath(QStringLiteral("diag.json"));
    QFile::remove(target);

    const Outcome report = await(m_backend->exportDiagnosticsReport(target));
    QVERIFY2(report.ok, qPrintable(report.code + QLatin1Char(' ') + report.message));

    // The Collector answers with the native form of the path it resolved, so
    // the two are compared as paths rather than as strings.
    QCOMPARE(QDir::cleanPath(QDir::fromNativeSeparators(
                 report.payload.value(QStringLiteral("target_path")).toString())),
             QDir::cleanPath(target));
    QVERIFY(report.payload.value(QStringLiteral("byte_count")).toLongLong() > 0);
    QVERIFY(!report.payload.value(QStringLiteral("completed_at_utc")).toString().isEmpty());

    QFile file(target);
    QVERIFY(file.open(QIODevice::ReadOnly));
    const QByteArray bytes = file.readAll();
    file.close();

    const QJsonObject json = QJsonDocument::fromJson(bytes).object();
    QCOMPARE(json.value(QStringLiteral("live_capture_status")).toString(),
             QStringLiteral("VERIFIED_POP_TO_EXIT"));
    QCOMPARE(json.value(QStringLiteral("public_distribution_ready")).toBool(false), true);
    QCOMPARE(json.value(QStringLiteral("ipc_protocol_version")).toInt(), 1);
    QVERIFY(!json.value(QStringLiteral("collector_version")).toString().isEmpty());
    QVERIFY(json.contains(QStringLiteral("counters")));
    QVERIFY(json.contains(QStringLiteral("recent_parser_errors")));
    QCOMPARE(json.value(QStringLiteral("boundary"))
                 .toObject()
                 .value(QStringLiteral("injected_hook_enabled"))
                 .toBool(true),
             false);

    // This is the one file the project invites a user to hand to a stranger.
    const QString text = QString::fromUtf8(bytes);
    QVERIFY(!QRegularExpression(QStringLiteral("\\b\\d{1,3}\\.\\d{1,3}\\.\\d{1,3}\\.\\d{1,3}\\b"))
                 .match(text)
                 .hasMatch());
    QVERIFY(!QRegularExpression(QStringLiteral("[A-Za-z]:\\\\")).match(text).hasMatch());
    QVERIFY(!QRegularExpression(QStringLiteral("\\bS-1-\\d+(?:-\\d+)+\\b")).match(text).hasMatch());
    QVERIFY(!QRegularExpression(QStringLiteral("[0-9a-fA-F]{16,}")).match(text).hasMatch());

    // A network destination is refused before any file operation.
    const QString forbidden =
        QStringLiteral("//mentor-test.invalid/reports/mentor-diag.json");
    const Outcome refused = await(m_backend->exportDiagnosticsReport(forbidden));
    QVERIFY(!refused.ok);
    QCOMPARE(refused.code, QStringLiteral("ERR_EXPORT_FAILED"));
    QVERIFY(!refused.message.isEmpty());
}

void IpcIntegrationTests::appControllerSendsOnlyContractFields()
{
    // The edit dialog also tracks duty_level / duty_expansion / job_name / role
    // for its own display. $defs/CreateManualRunRequest is
    // additionalProperties:false, so forwarding them verbatim is an outright
    // ERR_BAD_REQUEST from the real Collector; AppController must strip them.
    mr::AppController controller(m_backend, nullptr);

    QSignalSpy failures(&controller, &mr::AppController::mutationFailed);
    QSignalSpy successes(&controller, &mr::AppController::mutationSucceeded);

    QVariantMap fields;
    fields.insert(QStringLiteral("duty_name"), QString::fromUtf8("字段白名单用例"));
    fields.insert(QStringLiteral("duty_category"), QString::fromUtf8("迷宫挑战"));
    fields.insert(QStringLiteral("result"), QStringLiteral("COMPLETED"));
    fields.insert(QStringLiteral("matched_at_utc"),
                  QStringLiteral("2026-09-04T03:00:00.000Z"));
    fields.insert(QStringLiteral("entered_at_utc"),
                  QStringLiteral("2026-09-04T03:00:10.000Z"));
    fields.insert(QStringLiteral("ended_at_utc"),
                  QStringLiteral("2026-09-04T03:08:10.000Z"));
    fields.insert(QStringLiteral("duration_ms"), 480000);
    fields.insert(QStringLiteral("contributes_to_goal"), true);
    fields.insert(QStringLiteral("note"), QString());
    // Dialog-only fields the contract does not declare:
    fields.insert(QStringLiteral("duty_level"), 90);
    fields.insert(QStringLiteral("duty_expansion"), QString::fromUtf8("晓月"));
    fields.insert(QStringLiteral("job_name"), QString::fromUtf8("骑士"));
    fields.insert(QStringLiteral("role"), QStringLiteral("TANK"));

    controller.createManualRun(fields, QString::fromUtf8("字段白名单用例"));

    QTRY_VERIFY_WITH_TIMEOUT(successes.count() == 1 || failures.count() == 1, 15000);
    if (failures.count() > 0) {
        QFAIL(qPrintable(QStringLiteral("%1 %2")
                             .arg(failures.at(0).at(0).toString(),
                                  failures.at(0).at(1).toString())));
    }
    QCOMPARE(successes.at(0).at(0).toString(), QStringLiteral("create"));
    QVERIFY(!successes.at(0).at(1).toString().isEmpty());
}

void IpcIntegrationTests::candidateEndpointsRemainIndependentAndExportChecksummedEvidence()
{
    const auto formalBefore = await(m_backend->queryRuns({}, 1, 50));
    QVERIFY(formalBefore.ok);
    const auto settings = await(m_backend->getCaptureSettings());
    QVERIFY(settings.ok);
    QVERIFY(settings.payload.contains(QStringLiteral("candidate_validation_enabled")));
    QVERIFY(!settings.payload.value(QStringLiteral("candidate_validation_enabled")).toBool());
    QVERIFY(settings.payload.value(QStringLiteral("research_payload_opcodes")).toList().isEmpty());

    const auto capture = await(m_backend->getCaptureStatus());
    QVERIFY(capture.ok);
    QVERIFY(capture.payload.contains(QStringLiteral("candidate_observation_count")));
    QCOMPARE(capture.payload.value(QStringLiteral("candidate_observation_count")).toInt(), 0);
    const auto candidates = await(m_backend->queryCandidateObservations({}, {}, {}, 1, 200));
    QVERIFY2(candidates.ok, qPrintable(candidates.code + candidates.message));
    QVERIFY(candidates.payload.value(QStringLiteral("items")).toList().isEmpty());
    QCOMPARE(candidates.payload.value(QStringLiteral("page_info")).toMap().value(QStringLiteral("total")).toInt(), 0);
    const auto missing = await(m_backend->reviewCandidateObservation(
        QStringLiteral("00000000-0000-0000-0000-000000000001"), QStringLiteral("UNSURE")));
    QVERIFY(!missing.ok);
    QCOMPARE(missing.code, QStringLiteral("ERR_CANDIDATE_OBSERVATION_NOT_FOUND"));

    const auto target = QDir(m_databaseDirectory.path()).filePath(QStringLiteral("candidate-evidence.json"));
    const auto exported = await(m_backend->exportCandidateEvidence(target));
    QVERIFY2(exported.ok, qPrintable(exported.code + exported.message));
    QCOMPARE(exported.payload.value(QStringLiteral("observation_count")).toInt(), 0);
    QCOMPARE(exported.payload.value(QStringLiteral("review_count")).toInt(), 0);
    QFile evidence(target);
    QVERIFY(evidence.open(QIODevice::ReadOnly));
    const auto bytes = evidence.readAll();
    const auto json = QJsonDocument::fromJson(bytes).object();
    QCOMPARE(json.value(QStringLiteral("profile_status")).toString(), QStringLiteral("CANDIDATE"));
    QCOMPARE(json.value(QStringLiteral("contains_raw_payload")).toBool(true), false);
    QVERIFY(json.value(QStringLiteral("research_payload_opcodes")).toArray().isEmpty());
    const auto hash = QString::fromLatin1(QCryptographicHash::hash(bytes, QCryptographicHash::Sha256).toHex());
    QCOMPARE(exported.payload.value(QStringLiteral("sha256")).toString(), hash);
    QFile sidecar(exported.payload.value(QStringLiteral("sha256_path")).toString());
    QVERIFY(sidecar.open(QIODevice::ReadOnly));
    QVERIFY(sidecar.readAll().startsWith(hash.toLatin1()));
    const auto formalAfter = await(m_backend->queryRuns({}, 1, 50));
    QVERIFY(formalAfter.ok);
    QCOMPARE(formalAfter.payload, formalBefore.payload);
}

int main(int argc, char **argv)
{
    // The only Collector this binary may start is the fixture one in
    // initTestCase, which resolves its own path and is handed its own
    // MR_DATA_DIR in the child environment. Anything AppController launched
    // would run against the user's production database, and the serve-lease
    // takeover reads serve.pid out of the user's own data directory - so both
    // are moved somewhere disposable. See TestCollectorGuard.h. Must precede
    // any construction.
    mrtest::disableCollectorLaunch();
    QGuiApplication app(argc, argv);
    IpcIntegrationTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "IpcIntegrationTests.moc"
