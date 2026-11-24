// Requests to a backend - usually the MockBackend - sent one at a time and waited for, and
// the run and import fixtures the MockBackend suites send through them.
#pragma once

#include "CollectorText.h"
#include "IBackend.h"
#include "MockBackend.h"

#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QJsonValue>
#include <QObject>
#include <QString>
#include <QStringList>
#include <QTest>
#include <QVariantMap>

namespace mrtest {

struct Answer {
    bool done = false;
    bool ok = false;
    QJsonObject payload;
    QString code;
    QString message;
};

/// One request, waited for. The reply handler lives in \a context, so a reply that comes
/// after the wait gave up never writes into the returned Answer.
inline Answer ask(mr::IBackend &backend, const QString &type, const QJsonObject &payload = {})
{
    Answer answer;
    QObject context;
    backend.request(type, payload)->whenDone(&context,
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

/// One request sent under \a requestId, the way a retried mutation is resent, waited for
/// as ask() waits. The reply carries that id.
inline Answer askWithId(mr::IBackend &backend, const QString &type, const QJsonObject &payload,
                        const QString &requestId)
{
    Answer answer;
    QObject context;
    mr::BackendReply *reply = backend.requestWithId(type, payload, requestId);
    if (reply->requestId() != requestId)
        answer.code = QStringLiteral("TEST_WRONG_REQUEST_ID");
    reply->whenDone(&context,
        [&answer](bool ok, const QVariantMap &result, const QString &code, const QString &message) {
            answer.done = true;
            answer.ok = ok;
            answer.payload = QJsonObject::fromVariantMap(result);
            if (answer.code != QLatin1String("TEST_WRONG_REQUEST_ID"))
                answer.code = code;
            answer.message = message;
        });
    if (!QTest::qWaitFor([&answer] { return answer.done; }, 3000))
        answer.code = QStringLiteral("TEST_NO_REPLY");
    return answer;
}

/// The Collector's Chinese for the catalogue sentence \a key.
inline QString collectorChinese(const char *key, const QVariantMap &args = {})
{
    return mr::collector_text::chinese(mr::collector_text::textRef(key, args));
}

/// How many runs the mock holds, deleted ones included.
inline int runCount(mr::MockBackend &backend)
{
    return ask(backend, QStringLiteral("QueryRuns"),
               {{QStringLiteral("filter"), QJsonObject{{QStringLiteral("include_deleted"), true}}},
                {QStringLiteral("page"), 1}, {QStringLiteral("page_size"), 200}})
        .payload.value(QStringLiteral("page_info")).toObject().value(QStringLiteral("total")).toInt();
}

/// A run the capture recorded: matched 2026-09-01 12:00 UTC, ended 12:10, and entered at
/// 12:01 unless \a entered is false.
inline QJsonObject autoRun(const QString &id, const QString &result, bool entered)
{
    return {{QStringLiteral("run_id"), id},
            {QStringLiteral("revision"), 1},
            {QStringLiteral("source"), QStringLiteral("AUTO_NETWORK")},
            {QStringLiteral("mentor_roulette_id"), 9},
            {QStringLiteral("result"), result},
            {QStringLiteral("matched_at_utc"), QStringLiteral("2026-09-01T12:00:00.000Z")},
            {QStringLiteral("entered_at_utc"),
             entered ? QJsonValue(QStringLiteral("2026-09-01T12:01:00.000Z"))
                     : QJsonValue(QJsonValue::Null)},
            {QStringLiteral("ended_at_utc"), QStringLiteral("2026-09-01T12:10:00.000Z")},
            {QStringLiteral("duration_ms"), entered ? QJsonValue(540000.0) : QJsonValue(QJsonValue::Null)},
            {QStringLiteral("contributes_to_goal"), true},
            {QStringLiteral("manually_created"), false},
            {QStringLiteral("manually_corrected"), false},
            {QStringLiteral("soft_deleted"), false},
            {QStringLiteral("pending_review"), false},
            {QStringLiteral("created_at_utc"), QStringLiteral("2026-09-01T12:00:00.000Z")},
            {QStringLiteral("updated_at_utc"), QStringLiteral("2026-09-01T12:10:00.000Z")}};
}

/// A whole (unpaged) import preview of \a rows: a JSON file's text, or the recognised
/// rows of screenshots, read in UTC+8.
inline Answer previewImport(mr::MockBackend &backend, const QString &kind, const QJsonArray &rows,
                            const QString &sourceName)
{
    QJsonObject payload{{QStringLiteral("source_kind"), kind},
                        {QStringLiteral("source_name"), sourceName},
                        {QStringLiteral("time_zone"), QStringLiteral("+08:00")}};
    if (kind == QLatin1String("JSON"))
        payload.insert(QStringLiteral("text"), QString::fromUtf8(QJsonDocument(rows).toJson(QJsonDocument::Compact)));
    else
        payload.insert(QStringLiteral("rows"), rows);
    return ask(backend, QStringLiteral("PreviewRunImport"), payload);
}

/// Each preview row's status, in row order.
inline QStringList importStatuses(const Answer &preview)
{
    QStringList statuses;
    for (const auto &row : preview.payload.value(QStringLiteral("rows")).toArray())
        statuses.append(row.toObject().value(QStringLiteral("status")).toString());
    return statuses;
}

} // namespace mrtest
