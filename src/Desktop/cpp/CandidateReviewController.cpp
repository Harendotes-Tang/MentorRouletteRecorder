#include "CandidateReviewController.h"
#include "IBackend.h"

#include <QDateTime>
#include <QDir>
#include <QJsonArray>
#include <QUuid>
#include <algorithm>

namespace {
constexpr int kIndexPageSize = 200;
constexpr int kMaxObservations = 20000;

QString failure(const QString &code, const QString &message)
{
    return message.isEmpty() ? code : message;
}

// Hypotheses that become timeline events. Everything else (queue acks, zone-load
// cluster members) is packet-level detail for the maintainer's list only.
bool isEventRow(const QVariantMap &row)
{
    const auto name = row.value(QStringLiteral("hypothesis_name")).toString();
    if (name == QLatin1String("ZONE_LOAD"))
        return row.value(QStringLiteral("direction")).toString() == QLatin1String("NONE");
    return name == QLatin1String("QUEUE_REGISTRATION") || name == QLatin1String("FINDER_ACTION")
        || name == QLatin1String("QUEUE_CANCELLATION") // pre-2026-09-06 name of FINDER_ACTION
        || name == QLatin1String("FINDER_STATE_NOTIFICATION");
}

// Finder notifications arrive in a short burst (two 0x0323 in the live trace); they are
// one event to the user.
constexpr qint64 kFinderBurstMs = 2000;
// A duty entry has to follow the finder state update within this window (confirm -> loading
// screen took 24 s live), or the update is treated as stale (it also follows a cancel).
constexpr qint64 kPopToEntryMs = 3 * 60 * 1000;

bool before(const QVariant &left, const QVariant &right)
{
    const auto a = left.toMap();
    const auto b = right.toMap();
    // The monotonic clock defines capture order even if Windows wall time is corrected.
    const auto at = a.value(QStringLiteral("t_ms")).toLongLong();
    const auto bt = b.value(QStringLiteral("t_ms")).toLongLong();
    return at == bt ? a.value(QStringLiteral("observation_id")).toString() <
                         b.value(QStringLiteral("observation_id")).toString() : at < bt;
}
} // namespace

namespace mr {
CandidateReviewController::CandidateReviewController(QObject *parent) : QObject(parent)
{
    m_liveRefresh.setSingleShot(true);
    m_liveRefresh.setInterval(750);
    connect(&m_liveRefresh, &QTimer::timeout, this, [this] {
        // Status-only refresh is a separate signal, never AppController::refreshAll.
        Q_EMIT captureStatusRefreshRequested();
        if (m_active && !m_loading && m_reviewBusyId.isEmpty()) loadPage(m_page);
    });
}

void CandidateReviewController::setBackend(IBackend *backend)
{
    if (m_backend) disconnect(m_backend, nullptr, this, nullptr);
    disconnected();
    m_backend = backend;
    if (!backend) return;
    connect(backend, &IBackend::connectionChanged, this, [this] {
        if (!m_backend->isConnected()) disconnected();
        else if (m_active) refresh();
    });
}

void CandidateReviewController::disconnected()
{
    ++m_connectionGeneration;
    ++m_pageGeneration;
    ++m_indexGeneration;
    m_liveRefresh.stop();
    m_loading = m_indexLoading = m_indexComplete = m_exporting = m_settingsBusy = false;
    m_rows.clear();
    m_sessions.clear();
    m_pairs.clear();
    m_indexIds.clear();
    m_reviewBusyId.clear();
    m_indexMessage = tr("连接后刷新候选观测。");
    Q_EMIT changed();
}

void CandidateReviewController::setActive(bool active)
{
    m_active = active;
    if (active && !m_indexComplete && !m_indexLoading) refresh();
}

QVariantMap CandidateReviewController::pageInfo() const
{
    return {{QStringLiteral("page"), m_page}, {QStringLiteral("page_size"), pageSize()},
            {QStringLiteral("total"), m_total}};
}

QVariantList CandidateReviewController::anchorPairs() const
{
    if (m_sessionId.isEmpty()) return m_pairs;
    QVariantList selected;
    for (const auto &pair : m_pairs)
        if (pair.toMap().value(QStringLiteral("capture_session_id")).toString() == m_sessionId)
            selected.append(pair);
    return selected;
}

void CandidateReviewController::refresh()
{
    if (!m_backend || !m_backend->isConnected()) {
        m_error = tr("Collector 未连接，请连接后重试。");
        Q_EMIT changed();
        return;
    }
    m_hasNewObservations = false;
    m_liveRefresh.stop();
    loadPage(m_page);
    startIndex();
}

void CandidateReviewController::setSessionId(const QString &sessionId)
{
    if (sessionId == m_sessionId) return;
    m_sessionId = sessionId;
    m_rows.clear();
    m_total = 0;
    loadPage(1);
}

void CandidateReviewController::loadPage(int page)
{
    if (!m_backend || !m_backend->isConnected()) return;
    const int generation = ++m_pageGeneration;
    m_page = qBound(1, page, kMaxObservations / pageSize());
    m_loading = true;
    m_error.clear();
    Q_EMIT changed();
    m_backend->queryCandidateObservations(m_sessionId, {}, {}, m_page, pageSize())->whenDone(
        this, [this, generation](bool ok, const QVariantMap &payload, const QString &code, const QString &message) {
            if (generation != m_pageGeneration) return;
            m_loading = false;
            if (!ok) {
                m_error = failure(code, message);
                Q_EMIT changed();
                return;
            }
            const auto info = payload.value(QStringLiteral("page_info")).toMap();
            const auto items = payload.value(QStringLiteral("items")).toList();
            const int total = info.value(QStringLiteral("total"), -1).toInt();
            if (total < 0 || total > kMaxObservations || items.size() > pageSize()
                || info.value(QStringLiteral("page")).toInt() != m_page
                || info.value(QStringLiteral("page_size")).toInt() != pageSize()) {
                m_error = tr("候选分页响应不完整，请刷新。");
            } else {
                m_total = total;
                const int last = qMax(1, (total + pageSize() - 1) / pageSize());
                if (m_page > last) { loadPage(last); return; }
                m_rows = items;
            }
            Q_EMIT changed();
        });
}

void CandidateReviewController::startIndex(bool retry)
{
    if (!m_backend || !m_backend->isConnected()) return;
    ++m_indexGeneration;
    m_indexRetried = retry;
    m_indexChanged = false;
    m_indexLoading = true;
    m_indexComplete = false;
    m_indexExpectedTotal = -1;
    m_indexIds.clear();
    m_indexSessions.clear();
    m_indexEvents.clear();
    m_pairs.clear();
    m_timeline.clear();
    m_indexUpperUtc = QDateTime::currentDateTimeUtc().toString(Qt::ISODateWithMs);
    m_indexMessage = retry ? tr("观测发生变化，重新读取会话索引（1/1）……") : tr("正在读取完整会话与锚点索引……");
    Q_EMIT changed();
    readIndexPage(1, m_indexGeneration);
}

void CandidateReviewController::readIndexPage(int page, int generation)
{
    m_backend->queryCandidateObservations({}, {}, m_indexUpperUtc, page, kIndexPageSize)->whenDone(
        this, [this, page, generation](bool ok, const QVariantMap &payload, const QString &code, const QString &message) {
            if (generation != m_indexGeneration) return;
            if (!ok) { indexFailed(failure(code, message), false); return; }
            const auto info = payload.value(QStringLiteral("page_info")).toMap();
            const int total = info.value(QStringLiteral("total"), -1).toInt();
            const auto items = payload.value(QStringLiteral("items")).toList();
            if (m_indexExpectedTotal < 0) m_indexExpectedTotal = total;
            const int expected = qBound(0, total - (page - 1) * kIndexPageSize, kIndexPageSize);
            if (total < 0 || total > kMaxObservations || total != m_indexExpectedTotal || items.size() != expected
                || info.value(QStringLiteral("page")).toInt() != page
                || info.value(QStringLiteral("page_size")).toInt() != kIndexPageSize) {
                indexFailed(tr("观测数量发生变化，需重新刷新后查看锚点配对。"), true);
                return;
            }
            for (const auto &value : items) {
                const auto row = value.toMap();
                const auto id = row.value(QStringLiteral("observation_id")).toString();
                const auto session = row.value(QStringLiteral("capture_session_id")).toString();
                const auto stamp = row.value(QStringLiteral("observed_at_utc")).toString();
                if (id.isEmpty() || session.isEmpty() || !QDateTime::fromString(stamp, Qt::ISODateWithMs).isValid()
                    || m_indexIds.contains(id)) {
                    indexFailed(tr("分页出现重复或缺失观测，需重新刷新。"), true);
                    return;
                }
                m_indexIds.insert(id);
                auto summary = m_indexSessions.value(session);
                if (summary.isEmpty()) {
                    summary = {{QStringLiteral("session_id"), session}, {QStringLiteral("count"), 0},
                               {QStringLiteral("first_at_utc"), stamp}, {QStringLiteral("last_at_utc"), stamp}};
                }
                summary[QStringLiteral("count")] = summary.value(QStringLiteral("count")).toInt() + 1;
                if (stamp < summary.value(QStringLiteral("first_at_utc")).toString()) summary[QStringLiteral("first_at_utc")] = stamp;
                if (stamp > summary.value(QStringLiteral("last_at_utc")).toString()) summary[QStringLiteral("last_at_utc")] = stamp;
                m_indexSessions.insert(session, summary);
                if (isEventRow(row)) {
                    const auto connection = row.value(QStringLiteral("connection_tag")).toString();
                    if (!connection.isEmpty()) m_indexEvents[session + QLatin1Char('|') + connection].append(row);
                }
            }
            Q_EMIT changed();
            if (page * kIndexPageSize < total) {
                QTimer::singleShot(0, this, [this, page, generation] {
                    if (generation == m_indexGeneration) readIndexPage(page + 1, generation);
                });
            } else if (m_indexIds.size() != total || m_indexChanged) {
                indexFailed(tr("捕获期间观测发生变化，需重新刷新后查看锚点配对。"), true);
            } else finishIndex();
        });
}

void CandidateReviewController::indexFailed(const QString &message, bool retryable)
{
    if (retryable && !m_indexRetried) {
        startIndex(true);
        return;
    }
    m_indexLoading = false;
    m_indexComplete = false;
    m_pairs.clear();
    m_timeline.clear();
    m_indexMessage = message;
    Q_EMIT changed();
}

void CandidateReviewController::finishIndex()
{
    m_sessions.clear();
    for (auto summary : m_indexSessions) {
        const auto local = QDateTime::fromString(summary.value(QStringLiteral("first_at_utc")).toString(), Qt::ISODateWithMs).toLocalTime();
        summary[QStringLiteral("label")] = tr("%1 · %2 · %3 条")
            .arg(local.toString(QStringLiteral("MM-dd HH:mm")), summary.value(QStringLiteral("session_id")).toString().left(8))
            .arg(summary.value(QStringLiteral("count")).toInt());
        m_sessions.append(summary);
    }
    std::sort(m_sessions.begin(), m_sessions.end(), [](const QVariant &a, const QVariant &b) {
        return a.toMap().value(QStringLiteral("last_at_utc")).toString() > b.toMap().value(QStringLiteral("last_at_utc")).toString();
    });
    buildTimeline();
    m_indexLoading = false;
    m_indexComplete = true;
    m_indexMessage = tr("已读取全部 %1 条观测；进本／离开为推断，请对照实际经历核对。").arg(m_indexIds.size());
    if (!m_sessionId.isEmpty() && !m_indexSessions.contains(m_sessionId)) {
        m_sessionId.clear();
        loadPage(1);
    }
    // Keep only the small public summaries after validation; no second copy of event rows.
    m_indexSessions.clear();
    m_indexEvents.clear();
    Q_EMIT changed();
}

// Reads the event rows of one connection in capture order and names them.
//
// What the live session of 2026-09-06 established (docs/live-validation-guide.md
// section 7.3): every zone-load burst is one zone transition, the first burst on a
// connection is the login itself, and entering or leaving a duty does not open a new
// connection on the CN client. So a burst on its own says "zone changed", nothing more.
// The finder state update (sent after the user confirms or cancels) is what tells
// entries apart: the first burst shortly after one is the duty entry, the burst after
// that is the exit. Without one in between, bursts are shown as plain zone changes and
// nothing is paired -- a guess is not evidence.
void CandidateReviewController::buildTimeline()
{
    m_timeline.clear();
    m_pairs.clear();
    for (auto rows : m_indexEvents) {
        std::sort(rows.begin(), rows.end(), before);
        bool sawLogin = false;
        bool inDuty = false;
        qint64 popAt = -1;
        qint64 lastFinderAt = -1;
        qsizetype lastFinderIndex = -1;
        qsizetype entryIndex = -1;
        for (const auto &value : rows) {
            const auto row = value.toMap();
            const auto name = row.value(QStringLiteral("hypothesis_name")).toString();
            const auto t = row.value(QStringLiteral("t_ms")).toLongLong();
            QVariantMap event{
                {QStringLiteral("capture_session_id"), row.value(QStringLiteral("capture_session_id"))},
                {QStringLiteral("connection_tag"), row.value(QStringLiteral("connection_tag"))},
                {QStringLiteral("observation_id"), row.value(QStringLiteral("observation_id"))},
                {QStringLiteral("at_utc"), row.value(QStringLiteral("observed_at_utc"))},
                {QStringLiteral("t_ms"), t},
                {QStringLiteral("hypothesis_name"), name},
                {QStringLiteral("review_verdict"), row.value(QStringLiteral("review_verdict"))},
                {QStringLiteral("review_note"), row.value(QStringLiteral("review_note"))},
                {QStringLiteral("count"), 1},
                {QStringLiteral("inferred"), false},
                {QStringLiteral("pair_at_utc"), QVariant{}},
            };
            if (name == QLatin1String("FINDER_STATE_NOTIFICATION")) {
                if (lastFinderIndex >= 0 && t - lastFinderAt <= kFinderBurstMs) {
                    auto burst = m_timeline[lastFinderIndex].toMap();
                    burst[QStringLiteral("count")] = burst.value(QStringLiteral("count")).toInt() + 1;
                    m_timeline[lastFinderIndex] = burst;
                    lastFinderAt = t;
                    continue;
                }
                event[QStringLiteral("kind")] = QStringLiteral("finder");
                popAt = t;
                lastFinderAt = t;
                lastFinderIndex = m_timeline.size();
            } else if (name == QLatin1String("QUEUE_REGISTRATION")) {
                event[QStringLiteral("kind")] = QStringLiteral("queue");
            } else if (name == QLatin1String("FINDER_ACTION") || name == QLatin1String("QUEUE_CANCELLATION")) {
                event[QStringLiteral("kind")] = QStringLiteral("finder_action");
            } else {
                // ZONE_LOAD anchor.
                event[QStringLiteral("at_utc")] = row.value(QStringLiteral("first_observed_at_utc"), row.value(QStringLiteral("observed_at_utc")));
                event[QStringLiteral("inferred")] = true;
                if (!sawLogin) {
                    event[QStringLiteral("kind")] = QStringLiteral("login");
                    sawLogin = true;
                } else if (popAt >= 0 && t - popAt <= kPopToEntryMs && !inDuty) {
                    event[QStringLiteral("kind")] = QStringLiteral("duty_enter");
                    inDuty = true;
                    entryIndex = m_timeline.size();
                } else if (inDuty) {
                    event[QStringLiteral("kind")] = QStringLiteral("duty_leave");
                    inDuty = false;
                    auto entry = m_timeline[entryIndex].toMap();
                    entry[QStringLiteral("pair_at_utc")] = event.value(QStringLiteral("at_utc"));
                    event[QStringLiteral("pair_at_utc")] = entry.value(QStringLiteral("at_utc"));
                    m_timeline[entryIndex] = entry;
                    m_pairs.append(QVariantMap{
                        {QStringLiteral("capture_session_id"), entry.value(QStringLiteral("capture_session_id"))},
                        {QStringLiteral("connection_tag"), entry.value(QStringLiteral("connection_tag"))},
                        {QStringLiteral("first_at_utc"), entry.value(QStringLiteral("at_utc"))},
                        {QStringLiteral("last_at_utc"), event.value(QStringLiteral("at_utc"))},
                        {QStringLiteral("first_observation_id"), entry.value(QStringLiteral("observation_id"))},
                        {QStringLiteral("last_observation_id"), event.value(QStringLiteral("observation_id"))},
                        {QStringLiteral("inferred"), true}, {QStringLiteral("complete"), true}});
                    entryIndex = -1;
                } else {
                    event[QStringLiteral("kind")] = QStringLiteral("zone");
                }
                popAt = -1;
                lastFinderIndex = -1;
            }
            m_timeline.append(event);
        }
        if (inDuty && entryIndex >= 0) {
            const auto entry = m_timeline[entryIndex].toMap();
            m_pairs.append(QVariantMap{
                {QStringLiteral("capture_session_id"), entry.value(QStringLiteral("capture_session_id"))},
                {QStringLiteral("connection_tag"), entry.value(QStringLiteral("connection_tag"))},
                {QStringLiteral("first_at_utc"), entry.value(QStringLiteral("at_utc"))},
                {QStringLiteral("last_at_utc"), QVariant{}},
                {QStringLiteral("first_observation_id"), entry.value(QStringLiteral("observation_id"))},
                {QStringLiteral("last_observation_id"), QVariant{}},
                {QStringLiteral("inferred"), true}, {QStringLiteral("complete"), false}});
        }
    }
    // Newest first, like the row list; the monotonic clock orders within a connection and
    // wall time orders connections against each other.
    std::sort(m_timeline.begin(), m_timeline.end(), [](const QVariant &a, const QVariant &b) {
        const auto x = a.toMap();
        const auto y = b.toMap();
        const auto sx = x.value(QStringLiteral("capture_session_id")).toString() + x.value(QStringLiteral("connection_tag")).toString();
        const auto sy = y.value(QStringLiteral("capture_session_id")).toString() + y.value(QStringLiteral("connection_tag")).toString();
        if (sx == sy) return x.value(QStringLiteral("t_ms")).toLongLong() > y.value(QStringLiteral("t_ms")).toLongLong();
        return x.value(QStringLiteral("at_utc")).toString() > y.value(QStringLiteral("at_utc")).toString();
    });
    std::sort(m_pairs.begin(), m_pairs.end(), [](const QVariant &a, const QVariant &b) {
        return a.toMap().value(QStringLiteral("first_at_utc")).toString() > b.toMap().value(QStringLiteral("first_at_utc")).toString();
    });
}

QVariantList CandidateReviewController::timeline() const
{
    if (m_sessionId.isEmpty()) return m_timeline;
    QVariantList selected;
    for (const auto &event : m_timeline)
        if (event.toMap().value(QStringLiteral("capture_session_id")).toString() == m_sessionId)
            selected.append(event);
    return selected;
}

void CandidateReviewController::observeLiveEvent(const QVariantMap &event)
{
    Q_UNUSED(event)
    m_hasNewObservations = true;
    m_indexChanged = true;
    m_indexComplete = false;
    m_pairs.clear();
    m_timeline.clear();
    if (!m_indexLoading) m_indexMessage = tr("有新观测，刷新后重新计算完整锚点配对。");
    if (!m_liveRefresh.isActive()) m_liveRefresh.start();
    Q_EMIT changed();
}

void CandidateReviewController::review(const QString &id, const QString &verdict, const QString &note)
{
    if (!m_backend || !m_reviewBusyId.isEmpty()) return;
    if (id.isEmpty() || !QStringList{QStringLiteral("CORRECT"), QStringLiteral("WRONG"), QStringLiteral("UNSURE")}.contains(verdict)
        || note.size() > 2000) {
        m_reviewError = tr("请选择有效观测与核对结论，备注最多 2000 字符。");
        Q_EMIT changed();
        Q_EMIT reviewFinished(id, false, m_reviewError);
        return;
    }
    const int generation = m_connectionGeneration;
    m_reviewBusyId = id;
    m_reviewError.clear();
    ++m_pageGeneration; // A pending read must not replace the just-reviewed row with older data.
    m_loading = false;
    Q_EMIT changed();
    m_backend->reviewCandidateObservation(id, verdict, note)->whenDone(this,
        [this, generation, id, note](bool ok, const QVariantMap &payload, const QString &code, const QString &message) {
            if (generation != m_connectionGeneration) return;
            m_reviewBusyId.clear();
            if (!ok) m_reviewError = failure(code, message);
            else {
                const auto patch = [&](QVariantList &list) {
                    for (auto &value : list) {
                        auto row = value.toMap();
                        if (row.value(QStringLiteral("observation_id")).toString() != id) continue;
                        row[QStringLiteral("review_verdict")] = payload.value(QStringLiteral("review_verdict"));
                        row[QStringLiteral("reviewed_at_utc")] = payload.value(QStringLiteral("reviewed_at_utc"));
                        row[QStringLiteral("review_note")] = note.trimmed().isEmpty() ? QVariant{} : note.trimmed();
                        value = row;
                    }
                };
                patch(m_rows);
                patch(m_timeline);
            }
            Q_EMIT changed();
            Q_EMIT reviewFinished(id, ok, m_reviewError);
        });
}

void CandidateReviewController::writeSettings(const QJsonObject &changes)
{
    if (!m_backend || m_settingsBusy) return;
    const int generation = m_connectionGeneration;
    m_settingsBusy = true;
    m_settingsError.clear();
    Q_EMIT changed();
    m_backend->updateCaptureSettings(changes)->whenDone(this,
        [this, generation](bool ok, const QVariantMap &payload, const QString &code, const QString &message) {
            if (generation != m_connectionGeneration) return;
            m_settingsBusy = false;
            if (!ok) m_settingsError = failure(code, message);
            else {
                Q_EMIT captureSettingsApplied(payload);
                Q_EMIT captureStatusRefreshRequested();
            }
            Q_EMIT changed();
        });
}

void CandidateReviewController::setCandidateEnabled(bool enabled)
{
    writeSettings({{QStringLiteral("candidate_validation_enabled"), enabled}});
}

void CandidateReviewController::setResearchOpcodes(const QVariantList &opcodes)
{
    writeSettings({{QStringLiteral("research_payload_opcodes"), QJsonArray::fromVariantList(opcodes)}});
}

void CandidateReviewController::exportEvidence()
{
    if (!m_backend || m_exporting) return;
    const int generation = m_connectionGeneration;
    m_exporting = true;
    m_exportError.clear();
    m_exportResult.clear();
    QString path;
    if (!m_targetOverride.isEmpty()) path = QDir(m_targetOverride).absoluteFilePath(
        QStringLiteral("candidate-evidence-") + QUuid::createUuid().toString(QUuid::WithoutBraces) + QStringLiteral(".json"));
    Q_EMIT changed();
    m_backend->exportCandidateEvidence(path)->whenDone(this,
        [this, generation](bool ok, const QVariantMap &payload, const QString &code, const QString &message) {
            if (generation != m_connectionGeneration) return;
            m_exporting = false;
            if (!ok) m_exportError = failure(code, message);
            else m_exportResult = payload;
            Q_EMIT changed();
        });
}
} // namespace mr
