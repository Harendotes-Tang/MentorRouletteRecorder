#include "DesktopTestRunner.h"
#include "TestCollectorGuard.h"
#include "AppController.h"
#include "ExportController.h"
#include "MockBackend.h"
#include <QGuiApplication>
#include <QTemporaryDir>
#include <QTest>

namespace {
/// Keeps the payload of every export request and answers it like the mock.
class RecordingExportBackend final : public mr::MockBackend {
public:
    QHash<QString, QJsonObject> exports;
    mr::BackendReply *request(const QString &type, const QJsonObject &payload = {}) override {
        if (type.startsWith(QLatin1String("Export"))) exports.insert(type, payload);
        return MockBackend::request(type, payload);
    }
};

struct Answer {
    bool done = false;
    bool ok = false;
    QJsonObject payload;
    QString code;
    QString message;
};

/// One request, waited for.
Answer ask(mr::IBackend &backend, const QString &type, const QJsonObject &payload = {})
{
    Answer answer;
    backend.request(type, payload)->whenDone(&backend,
        [&answer](bool ok, const QVariantMap &result, const QString &code, const QString &message) {
            answer.done = true;
            answer.ok = ok;
            answer.payload = QJsonObject::fromVariantMap(result);
            answer.code = code;
            answer.message = message;
        });
    if (!QTest::qWaitFor([&answer] { return answer.done; }, 3000))
        answer.code = QStringLiteral("TEST_NO_REPLY");
    return answer;
}

class ExportOrderingTests final : public QObject {
    Q_OBJECT
private Q_SLOTS:
    // The sort the 「日期」 column shows reaches the export through the application
    // object: flipping the list to 最早在前 flips the next export's sort.
    void theApplicationSendsTheListsSortWithAnExport() {
        RecordingExportBackend backend;
        mr::AppController app(&backend, nullptr);
        QTemporaryDir dir;
        app.setExportTargetOverride(dir.path());
        app.exportCsv();
        QCOMPARE(backend.exports.value(QStringLiteral("ExportCsv")).value(QStringLiteral("filter")).toObject(),
                 QJsonObject({{QStringLiteral("date_field"), QStringLiteral("matched_at_utc")}}));
        QCOMPARE(backend.exports.value(QStringLiteral("ExportCsv")).value(QStringLiteral("sort")).toObject(),
                 QJsonObject({{QStringLiteral("field"), QStringLiteral("history_date")},
                              {QStringLiteral("direction"), QStringLiteral("desc")}}));
        app.runs()->sortBy(QStringLiteral("history_date"));
        QVERIFY(app.runs()->sortAscending());
        app.exportJson();
        QCOMPARE(backend.exports.value(QStringLiteral("ExportJson")).value(QStringLiteral("sort")).toObject(),
                 QJsonObject({{QStringLiteral("field"), QStringLiteral("history_date")},
                              {QStringLiteral("direction"), QStringLiteral("asc")}}));
    }
    // contracts/CHANGELOG.md 2026-11-03: an export carries the sort the history list is showing,
    // whether the whole filter or the checked rows are exported; until the list's sort is known
    // nothing is sent, and the Collector keeps newest entered first.
    void exportsCarryTheHistoryListsCurrentSort() {
        RecordingExportBackend backend;
        QTemporaryDir dir;
        mr::ExportController exporter;
        exporter.setBackend(&backend);
        exporter.setTargetOverride(dir.path());
        exporter.exportCsv();
        QVERIFY(!backend.exports.value(QStringLiteral("ExportCsv")).contains(QStringLiteral("sort")));
        exporter.setHistorySort(QStringLiteral("history_date"), true);
        exporter.exportCsv();
        const QJsonObject earliestFirst{{QStringLiteral("field"), QStringLiteral("history_date")},
                                        {QStringLiteral("direction"), QStringLiteral("asc")}};
        QCOMPARE(backend.exports.value(QStringLiteral("ExportCsv")).value(QStringLiteral("sort")).toObject(), earliestFirst);
        exporter.setCheckedRunIds({QStringLiteral("00000000-0000-4000-8000-000000000001")});
        exporter.exportJson();
        QCOMPARE(backend.exports.value(QStringLiteral("ExportJson")).value(QStringLiteral("sort")).toObject(), earliestFirst);
        exporter.setHistorySort(QStringLiteral("duty_name"), false);
        exporter.exportJson();
        QCOMPARE(backend.exports.value(QStringLiteral("ExportJson")).value(QStringLiteral("sort")).toObject(),
                 QJsonObject({{QStringLiteral("field"), QStringLiteral("duty_name")},
                              {QStringLiteral("direction"), QStringLiteral("desc")}}));
    }
    // RequestParsers.Sort and RunExporter.LoadPages (contracts/CHANGELOG.md 2026-11-03): an
    // export takes the RunSort QueryRuns takes, counts the rows QueryRuns lists, and refuses
    // the sorts QueryRuns refuses; an omitted sort is still accepted.
    void anExportTakesTheSortQueryRunsTakesAndRefusesWhatItRefuses()
    {
        mr::MockBackend backend;
        const QJsonObject earliestFirst{{QStringLiteral("field"), QStringLiteral("history_date")},
                                        {QStringLiteral("direction"), QStringLiteral("asc")}};
        const QString target = QStringLiteral("D:/exports/mentor.out");
        const Answer page = ask(backend, QStringLiteral("QueryRuns"),
                                {{QStringLiteral("filter"), QJsonObject()},
                                 {QStringLiteral("page"), 1}, {QStringLiteral("page_size"), 200},
                                 {QStringLiteral("sort"), earliestFirst}});
        QVERIFY(page.ok);
        const int total = page.payload.value(QStringLiteral("page_info")).toObject()
                              .value(QStringLiteral("total")).toInt();
        QVERIFY(total > 1);
        for (const QString &type : {QStringLiteral("ExportCsv"), QStringLiteral("ExportJson")}) {
            const Answer exported = ask(backend, type,
                                        {{QStringLiteral("target_path"), target},
                                         {QStringLiteral("filter"), QJsonObject()},
                                         {QStringLiteral("sort"), earliestFirst}});
            QVERIFY2(exported.ok, qPrintable(type + QLatin1String(": ") + exported.code));
            QCOMPARE(exported.payload.value(QStringLiteral("row_count")).toInt(), total);
        }
        const Answer omitted = ask(backend, QStringLiteral("ExportCsv"),
                                   {{QStringLiteral("target_path"), target},
                                    {QStringLiteral("filter"), QJsonObject()}});
        QVERIFY(omitted.ok);
        QCOMPARE(omitted.payload.value(QStringLiteral("row_count")).toInt(), total);
        for (const QJsonObject &bad : {
                 QJsonObject{{QStringLiteral("field"), QStringLiteral("history_date")},
                             {QStringLiteral("direction"), QStringLiteral("sideways")}},
                 QJsonObject{{QStringLiteral("field"), QStringLiteral("run_id")}},
                 QJsonObject{{QStringLiteral("field"), QStringLiteral("history_date")},
                             {QStringLiteral("page"), 1}}}) {
            const Answer refused = ask(backend, QStringLiteral("ExportJson"),
                                       {{QStringLiteral("target_path"), target},
                                        {QStringLiteral("filter"), QJsonObject()},
                                        {QStringLiteral("sort"), bad}});
            QCOMPARE(refused.code, QStringLiteral("ERR_BAD_REQUEST"));
            QVERIFY(!refused.payload.contains(QStringLiteral("row_count")));
            QCOMPARE(ask(backend, QStringLiteral("QueryRuns"),
                         {{QStringLiteral("filter"), QJsonObject()}, {QStringLiteral("sort"), bad}}).code,
                     QStringLiteral("ERR_BAD_REQUEST"));
        }
    }

};
}
MR_DESKTOP_TEST_SUITE(ExportOrdering)
{
    mrtest::disableCollectorLaunch();
    QGuiApplication app(argc, argv);
    ExportOrderingTests tests;
    return QTest::qExec(&tests, argc, argv);
}
#include "ExportOrderingTests.moc"
