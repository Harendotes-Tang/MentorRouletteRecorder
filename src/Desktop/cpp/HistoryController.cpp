#include "HistoryController.h"
#include "IBackend.h"
#include "DutyCatalog.h"
#include "Formatters.h"
#include <QCoreApplication>
#include <QDateTime>
#include <QJsonArray>
#include <QTimeZone>
#include <utility>

namespace {
/// Keep only fields accepted by both creation and correction contracts; the
/// edit form also carries catalogue-only display fields that cannot be sent.
QJsonObject contractRunFields(const QJsonObject &fields)
{
    static const char *kAllowed[] = {
        "content_id", "duty_name",      "duty_category",       "job_id",
        "matched_at_utc", "entered_at_utc", "ended_at_utc",     "duration_ms",
        "result",     "contributes_to_goal", "note",           "pending_review",
    };

    QJsonObject out;
    for (const char *name : kAllowed) {
        const QString key = QString::fromLatin1(name);
        if (fields.contains(key))
            out.insert(key, fields.value(key));
    }
    return out;
}
} // namespace

namespace mr {

HistoryController::HistoryController(IBackend *backend, QObject *parent)
    : QObject(parent)
    , m_backend(backend)
    , m_runs(new RunListModel(this))
{
    m_runs->setBackend(backend);
    // The existing model borrows a raw backend pointer. Keep that borrow within
    // this workflow's backend lifetime, including independently owned models.
    connect(backend, &QObject::destroyed, this, [this] { m_runs->setBackend(nullptr); });
}

void HistoryController::refreshPendingReviewRun()
{
    if (!m_backend)
        return;
    QJsonObject filter;
    filter.insert(QStringLiteral("pending_review"), true);
    QJsonObject sort;
    sort.insert(QStringLiteral("field"), QStringLiteral("matched_at_utc"));
    sort.insert(QStringLiteral("direction"), QStringLiteral("desc"));
    m_backend->queryRuns(filter, 1, 1, sort)
        ->whenDone(this, [this](bool ok, const QVariantMap &payload, const QString &,
                                const QString &) {
            const QJsonArray items =
                ok ? QJsonObject::fromVariantMap(payload)
                         .value(QStringLiteral("items"))
                         .toArray()
                   : QJsonArray();
            const QJsonObject next =
                items.isEmpty() ? QJsonObject() : items.first().toObject();
            if (next == m_pendingReviewRun)
                return;
            m_pendingReviewRun = next;
            Q_EMIT pendingReviewRunChanged();
        });
}

void HistoryController::adoptRunRevisionFromEvent(const QJsonObject &run)
{
    const QString runId = run.value(QStringLiteral("run_id")).toString();
    if (runId.isEmpty() || !run.contains(QStringLiteral("revision")))
        return;
    const int revision = run.value(QStringLiteral("revision")).toInt();
    if (revision <= 0)
        return;

    bool changed = false;
    if (m_selectedRun.value(QStringLiteral("run_id")).toString() == runId
        && m_selectedRun.value(QStringLiteral("revision")).toInt() != revision) {
        m_selectedRun.insert(QStringLiteral("revision"), revision);
        Q_EMIT selectionChanged();
        changed = true;
    }
    if (m_pendingReviewRun.value(QStringLiteral("run_id")).toString() == runId
        && m_pendingReviewRun.value(QStringLiteral("revision")).toInt() != revision) {
        m_pendingReviewRun.insert(QStringLiteral("revision"), revision);
        Q_EMIT pendingReviewRunChanged();
        changed = true;
    }
    if (changed)
        Q_EMIT runRevisionChanged(runId, revision);
}

QVariantMap HistoryController::selectedRun() const
{
    // duty_level / duty_expansion are not fields $defs/Run can carry, so they
    // are joined in from the bundled catalogue for display only.
    return DutyCatalog::shared()->enrich(m_selectedRun.toVariantMap());
}

bool HistoryController::selectedRunCanUndo() const
{
    if (m_selectedRun.isEmpty() || m_selectedRunRevisions.isEmpty())
        return false;
    int newest = 0;
    for (const QVariant &value : m_selectedRunRevisions)
        newest = qMax(newest, value.toMap().value(QStringLiteral("revision")).toInt());
    // Revision 1 is the creation record; the Collector refuses to undo it
    // (ERR_UNDO_NOT_ALLOWED) and offering the button would be a lie.
    if (newest <= 1)
        return false;
    return newest == m_selectedRun.value(QStringLiteral("revision")).toInt();
}

void HistoryController::selectRun(const QVariantMap &run)
{
    const QJsonObject next = QJsonObject::fromVariantMap(run);
    if (next.value(QStringLiteral("run_id")) == m_selectedRun.value(QStringLiteral("run_id"))) {
        clearSelection();
        return;
    }
    m_selectedRun = next;
    m_selectedRunRevisions.clear();
    m_selectedRunEvents.clear();
    m_runEventsState = QStringLiteral("idle");
    m_runEventsMessage.clear();
    m_runEventsRunId.clear();
    Q_EMIT selectionChanged();
    Q_EMIT runEventsChanged();
    loadRevisionsForSelection();
}

void HistoryController::clearSelection()
{
    if (m_selectedRun.isEmpty())
        return;
    m_selectedRun = QJsonObject();
    m_selectedRunRevisions.clear();
    m_selectedRunEvents.clear();
    m_runEventsState = QStringLiteral("idle");
    m_runEventsMessage.clear();
    m_runEventsRunId.clear();
    Q_EMIT selectionChanged();
    Q_EMIT runEventsChanged();
}

void HistoryController::loadRevisionsForSelection()
{
    if (!m_backend || m_selectedRun.isEmpty())
        return;
    const QString runId = m_selectedRun.value(QStringLiteral("run_id")).toString();
    m_backend->getRunRevisions(runId)->whenDone(
        this, [this, runId](bool ok, const QVariantMap &payload, const QString &,
                            const QString &) {
            if (!ok || m_selectedRun.value(QStringLiteral("run_id")).toString() != runId)
                return;
            m_selectedRunRevisions = payload.value(QStringLiteral("items")).toList();
            Q_EMIT selectionChanged();
        });
}

void HistoryController::refreshRunEvents()
{
    if (!m_backend || m_selectedRun.isEmpty())
        return;
    const QString runId = m_selectedRun.value(QStringLiteral("run_id")).toString();
    if (runId.isEmpty())
        return;
    // Already loaded (or loading) for this very run: the tab can be reopened
    // without re-asking the Collector.
    if (m_runEventsRunId == runId
        && (m_runEventsState == QLatin1String("loading")
            || m_runEventsState == QLatin1String("ready"))) {
        return;
    }

    m_runEventsRunId = runId;
    m_runEventsState = QStringLiteral("loading");
    m_runEventsMessage.clear();
    m_selectedRunEvents.clear();
    Q_EMIT runEventsChanged();

    m_backend->getRunEvents(runId)->whenDone(
        this, [this, runId](bool ok, const QVariantMap &payload, const QString &code,
                            const QString &message) {
            if (m_runEventsRunId != runId)
                return;
            if (!ok) {
                m_selectedRunEvents.clear();
                if (code == QLatin1String("ERR_UNKNOWN_MESSAGE")
                    || code == QLatin1String("ERR_UNSUPPORTED")
                    || code == QLatin1String("ERR_BAD_REQUEST")) {
                    m_runEventsState = QStringLiteral("unsupported");
                    m_runEventsMessage =
                        QCoreApplication::translate("mr::AppController", "当前 Collector 不提供逐事件摘要（%1）。").arg(code);
                } else {
                    m_runEventsState = QStringLiteral("error");
                    m_runEventsMessage = message.isEmpty()
                                             ? QCoreApplication::translate("mr::AppController", "读取事件摘要失败：%1").arg(code)
                                             : message;
                }
                Q_EMIT runEventsChanged();
                return;
            }
            m_selectedRunEvents = payload.value(QStringLiteral("events")).toList();
            m_runEventsState = QStringLiteral("ready");
            m_runEventsMessage.clear();
            Q_EMIT runEventsChanged();
        });
}

void HistoryController::setHistoryFilter(const QVariantMap &filter)
{
    m_historyFilter = QJsonObject::fromVariantMap(filter);
    m_runs->setFilter(filter);
    Q_EMIT historyFilterChanged();
}

void HistoryController::resetHistoryFilter()
{
    setHistoryFilter({});
}

void HistoryController::sendRunMutation(const QString &kind, BackendReply *reply,
                                    bool keepSelection)
{
    if (!reply)
        return;
    reply->whenDone(this, [this, kind, keepSelection](bool ok, const QVariantMap &payload,
                                                      const QString &code,
                                                      const QString &message) {
        if (!ok) {
            Q_EMIT mutationFailed(code, message);
            Q_EMIT toastRequested(message.isEmpty() ? code : message);
            return;
        }
        adoptRunMutation(kind, payload, keepSelection);
    });
}

void HistoryController::adoptRunMutation(const QString &kind, const QVariantMap &payload,
                                     bool keepSelection)
{
    if (keepSelection) {
        m_selectedRun =
            QJsonObject::fromVariantMap(payload.value(QStringLiteral("run")).toMap());
        Q_EMIT selectionChanged();
        loadRevisionsForSelection();
    }

    Q_EMIT mutationSucceeded(kind, payload.value(QStringLiteral("run_id")).toString(),
                             payload.value(QStringLiteral("revision")).toInt(),
                             payload.value(QStringLiteral("audit_event_id")).toString());

    if (kind == QLatin1String("correct")) {
        Q_EMIT toastRequested(QString::fromUtf8("已保存 %1 修订 %2 · 统计已重算")
                      .arg(payload.value(QStringLiteral("run_id")).toString().left(8),
                           QString::number(
                               payload.value(QStringLiteral("revision")).toInt())));
    } else if (kind == QLatin1String("undo")) {
        Q_EMIT toastRequested(QString::fromUtf8("已撤销上一次修正 · 修订 %1 · "
                                    "撤销本身也是一条新修订")
                      .arg(QString::number(
                               payload.value(QStringLiteral("revision")).toInt())));
    } else if (kind == QLatin1String("delete")) {
        Q_EMIT toastRequested(QString::fromUtf8("已软删除 · 统计已重算 · 可随时恢复"));
    } else if (kind == QLatin1String("restore")) {
        Q_EMIT toastRequested(QString::fromUtf8("已恢复 · 统计已重算"));
    } else if (kind == QLatin1String("review")) {
        Q_EMIT toastRequested(QString::fromUtf8("已确认复核 · 修订 %1")
                      .arg(QString::number(
                               payload.value(QStringLiteral("revision")).toInt())));
    }

    if (!keepSelection)
        clearSelection();
    Q_EMIT refreshRequested();
}

void HistoryController::createManualRun(const QVariantMap &fields, const QString &reason)
{
    if (!m_backend)
        return;
    if (reason.trimmed().isEmpty()) {
        // Mirrors ERR_REASON_REQUIRED so the dialog can refuse before sending.
        Q_EMIT mutationFailed(QStringLiteral("ERR_REASON_REQUIRED"),
                              QString::fromUtf8("必须填写新增原因。"));
        return;
    }

    m_backend->createManualRun(contractRunFields(QJsonObject::fromVariantMap(fields)), reason)
        ->whenDone(this, [this](bool ok, const QVariantMap &payload, const QString &code,
                                const QString &message) {
            if (!ok) {
                Q_EMIT mutationFailed(code, message);
                Q_EMIT toastRequested(message.isEmpty() ? code : message);
                return;
            }
            Q_EMIT mutationSucceeded(
                QStringLiteral("create"), payload.value(QStringLiteral("run_id")).toString(),
                payload.value(QStringLiteral("revision")).toInt(),
                payload.value(QStringLiteral("audit_event_id")).toString());
            Q_EMIT toastRequested(QString::fromUtf8("已创建手动记录 %1 · 修订 %2 · 统计已重算")
                          .arg(payload.value(QStringLiteral("run_id")).toString().left(8),
                               QString::number(
                                   payload.value(QStringLiteral("revision")).toInt())));
            Q_EMIT refreshRequested();
        });
}

void HistoryController::correctSelectedRun(const QVariantMap &changes, const QString &reason,
                                           const QString &runId)
{
    if (!m_backend || m_selectedRun.isEmpty())
        return;
    if (!runId.isEmpty() && runId != m_selectedRun.value(QStringLiteral("run_id")).toString()) {
        Q_EMIT mutationFailed(QStringLiteral("ERR_SELECTION_CHANGED"),
                              QString::fromUtf8("选中的记录已经变了，未保存任何修改。请关闭后重新打开要修正的记录。"));
        return;
    }
    if (reason.trimmed().isEmpty()) {
        Q_EMIT mutationFailed(QStringLiteral("ERR_REASON_REQUIRED"),
                              QString::fromUtf8("必须填写修正原因。"));
        return;
    }

    sendRunMutation(QStringLiteral("correct"),
                    m_backend->correctRun(
                        m_selectedRun.value(QStringLiteral("run_id")).toString(),
                        m_selectedRun.value(QStringLiteral("revision")).toInt(),
                        contractRunFields(QJsonObject::fromVariantMap(changes)), reason),
                    true);
}

void HistoryController::resolveRunResult(const QString &runId, int revision,
                                    const QString &result, const QString &reason, int jobId)
{
    if (!m_backend || runId.isEmpty() || result.isEmpty())
        return;
    if (reason.trimmed().isEmpty()) {
        Q_EMIT mutationFailed(QStringLiteral("ERR_REASON_REQUIRED"),
                              QString::fromUtf8("必须填写确认原因。"));
        return;
    }

    // The revision the caller saw. -1 means "the one carried by the record this
    // process already holds", which is the dashboard banner's case; a mismatch
    // is refused by the Collector rather than overwriting another correction.
    int expected = revision;
    if (expected < 0) {
        for (const QJsonObject *candidate : {&m_pendingReviewRun, &m_selectedRun}) {
            if (candidate->value(QStringLiteral("run_id")).toString() == runId) {
                expected = candidate->value(QStringLiteral("revision")).toInt();
                break;
            }
        }
    }
    if (expected < 0) {
        Q_EMIT mutationFailed(
            QStringLiteral("ERR_BAD_REQUEST"),
            QString::fromUtf8("找不到这条记录的版本号，请在历史记录中修正。"));
        return;
    }

    // The same append-only path as every other correction: the result and the
    // acknowledgement are one audited revision, never a silent flag flip.
    //
    // Supplying `result` acknowledges review even when its value is unchanged.
    // The separate "确认已复核" action sends pending_review:false; ordinary
    // note/job/time corrections deliberately keep an unresolved review flag.
    sendResultCorrection(runId, expected, result, reason, jobId, /*allowRetry=*/true);
}

void HistoryController::sendResultCorrection(const QString &runId, int expectedRevision,
                                         const QString &result, const QString &reason,
                                         int jobId, bool allowRetry)
{
    if (!m_backend)
        return;
    QJsonObject changes;
    changes.insert(QStringLiteral("result"), result);
    if (jobId > 0)
        changes.insert(QStringLiteral("job_id"), jobId);
    const bool keepSelection =
        m_selectedRun.value(QStringLiteral("run_id")).toString() == runId;
    BackendReply *reply = m_backend->correctRun(runId, expectedRevision, changes, reason);
    if (!allowRetry) {
        sendRunMutation(QStringLiteral("review"), reply, keepSelection);
        return;
    }
    if (!reply)
        return;

    reply->whenDone(this, [this, runId, expectedRevision, result, reason, jobId, keepSelection](
                              bool ok, const QVariantMap &payload, const QString &code,
                              const QString &message) {
        if (ok) {
            adoptRunMutation(QStringLiteral("review"), payload, keepSelection);
            return;
        }
        // The revision the dialog was handed by run_finished is stale because
        // something else corrected the run first. When that correction touched
        // unrelated fields (a note, the times), the answer the user gave is
        // still the answer, so the current revision is read back and the same
        // correction is sent once more with it. When it touched the very fields
        // this dialog is about to write, retrying would overwrite a decision
        // someone already made; the contract leaves that to the user.
        if (code == QLatin1String("ERR_REVISION_CONFLICT")) {
            retryResultCorrectionWithFreshRevision(runId, expectedRevision, result, reason,
                                                   jobId);
            return;
        }
        Q_EMIT mutationFailed(code, message);
        Q_EMIT toastRequested(message.isEmpty() ? code : message);
    });
}

void HistoryController::retryResultCorrectionWithFreshRevision(const QString &runId,
                                                           int staleRevision,
                                                           const QString &result,
                                                           const QString &reason, int jobId)
{
    if (!m_backend)
        return;
    // The chain is append-only, ascending, one row per revision, and the
    // Collector pages it oldest-first (50 rows unless told otherwise). Ask for
    // the largest page that starts at or before the revision this dialog saw,
    // so the rows it never saw are the ones that come back.
    const int page = qMax(0, staleRevision) / kRevisionPageSize + 1;
    m_backend->getRunRevisions(runId, page, kRevisionPageSize)->whenDone(
        this, [this, runId, staleRevision, result, reason, jobId, page](
                  bool ok, const QVariantMap &payload, const QString &code,
                  const QString &message) {
            if (!ok) {
                Q_EMIT mutationFailed(code, message);
                Q_EMIT toastRequested(message.isEmpty() ? code : message);
                return;
            }
            const QString reopen =
                QString::fromUtf8("这条记录刚刚被改动过，请重新打开后再确认。");
            // The newest revision in the chain is the run's current revision;
            // page_info.total names it even when it lies beyond this page.
            // Along the way, look at what the revisions this dialog never saw
            // actually changed.
            int newest = payload.value(QStringLiteral("page_info")).toMap()
                             .value(QStringLiteral("total")).toInt();
            bool overlaps = false;
            const QVariantList items = payload.value(QStringLiteral("items")).toList();
            for (const QVariant &value : items) {
                const QVariantMap revision = value.toMap();
                const int number = revision.value(QStringLiteral("revision")).toInt();
                newest = qMax(newest, number);
                if (number > staleRevision && revisionTouchesResult(revision, jobId > 0))
                    overlaps = true;
            }
            if (newest <= 0) {
                Q_EMIT mutationFailed(QStringLiteral("ERR_REVISION_CONFLICT"), reopen);
                return;
            }
            if (newest > page * kRevisionPageSize) {
                // More than a page of revisions landed since the dialog opened.
                // What they changed is unknown here, so nothing is retried.
                Q_EMIT runRevisionChanged(runId, newest);
                Q_EMIT mutationFailed(QStringLiteral("ERR_REVISION_CONFLICT"), reopen);
                Q_EMIT toastRequested(reopen);
                return;
            }
            // The dialog is holding the stale number too; tell it first, so a
            // deliberate retry by the user carries the current revision.
            Q_EMIT runRevisionChanged(runId, newest);
            if (overlaps) {
                // Another place has already answered for this run. Whoever
                // wrote first wins (docs/manual-correction.md section 3); the
                // user sees the record as it is now and decides again.
                const QString text = QString::fromUtf8(
                    "这条记录的结果刚刚已在别处确认过，本次没有改动。请查看当前记录后再决定。");
                Q_EMIT mutationFailed(QStringLiteral("ERR_REVISION_CONFLICT"), text);
                Q_EMIT toastRequested(text);
                return;
            }
            sendResultCorrection(runId, newest, result, reason, jobId, /*allowRetry=*/false);
        });
}

bool HistoryController::revisionTouchesResult(const QVariantMap &revision, bool withJob)
{
    // A result correction writes `result`, implicitly clears `pending_review`
    // and may set `job_id`; a revision that changed any of those already made
    // the decision this dialog is about to make.
    const QVariantList changes = revision.value(QStringLiteral("changes")).toList();
    for (const QVariant &value : changes) {
        const QString field = value.toMap().value(QStringLiteral("field")).toString();
        if (field == QLatin1String("result") || field == QLatin1String("pending_review"))
            return true;
        if (withJob && field == QLatin1String("job_id"))
            return true;
    }
    return false;
}

void HistoryController::confirmSelectedRunReview(const QString &reason)
{
    if (!m_backend || m_selectedRun.isEmpty())
        return;
    if (reason.trimmed().isEmpty()) {
        Q_EMIT mutationFailed(QStringLiteral("ERR_REASON_REQUIRED"),
                              QString::fromUtf8("必须填写确认原因。"));
        return;
    }
    if (!m_selectedRun.value(QStringLiteral("pending_review")).toBool(false)) {
        Q_EMIT mutationFailed(QStringLiteral("ERR_BAD_REQUEST"),
                              QString::fromUtf8("该记录不在待复核状态。"));
        return;
    }

    // Cleared through CorrectRun rather than a dedicated message: the
    // acknowledgement is a human decision about a run whose end was inferred,
    // and it belongs in the same append-only audit trail as every other
    // correction.
    QJsonObject changes;
    changes.insert(QStringLiteral("pending_review"), false);
    sendRunMutation(QStringLiteral("review"),
                    m_backend->correctRun(
                        m_selectedRun.value(QStringLiteral("run_id")).toString(),
                        m_selectedRun.value(QStringLiteral("revision")).toInt(), changes,
                        reason),
                    true);
}

void HistoryController::undoSelectedRunRevision(const QString &reason)
{
    if (!m_backend || m_selectedRun.isEmpty())
        return;
    if (reason.trimmed().isEmpty()) {
        Q_EMIT mutationFailed(QStringLiteral("ERR_REASON_REQUIRED"),
                              QString::fromUtf8("必须填写撤销原因。"));
        return;
    }
    if (!selectedRunCanUndo()) {
        Q_EMIT mutationFailed(QStringLiteral("ERR_UNDO_NOT_ALLOWED"),
                              QString::fromUtf8("只能撤销最新一次修正，且首个修订"
                                                "不可撤销。"));
        return;
    }

    sendRunMutation(QStringLiteral("undo"),
                    m_backend->undoRevision(
                        m_selectedRun.value(QStringLiteral("run_id")).toString(),
                        m_selectedRun.value(QStringLiteral("revision")).toInt(), reason),
                    true);
}

void HistoryController::softDeleteSelectedRun(const QString &reason)
{
    if (!m_backend || m_selectedRun.isEmpty())
        return;
    sendRunMutation(QStringLiteral("delete"),
                    m_backend->softDeleteRun(
                        m_selectedRun.value(QStringLiteral("run_id")).toString(),
                        m_selectedRun.value(QStringLiteral("revision")).toInt(), reason),
                    false);
}

void HistoryController::restoreSelectedRun(const QString &reason)
{
    if (!m_backend || m_selectedRun.isEmpty())
        return;
    sendRunMutation(QStringLiteral("restore"),
                    m_backend->restoreRun(
                        m_selectedRun.value(QStringLiteral("run_id")).toString(),
                        m_selectedRun.value(QStringLiteral("revision")).toInt(), reason),
                    false);
}

void HistoryController::openRun(const QVariantMap &run)
{
    const QJsonObject next = QJsonObject::fromVariantMap(run);
    if (next.value(QStringLiteral("run_id")).toString().isEmpty())
        return;
    m_selectedRun = next;
    m_selectedRunRevisions.clear();
    m_selectedRunEvents.clear();
    m_runEventsState = QStringLiteral("idle");
    m_runEventsRunId.clear();
    Q_EMIT selectionChanged();
    Q_EMIT runEventsChanged();
    loadRevisionsForSelection();
}

void HistoryController::adoptReflection(const QString &runId, const QJsonValue &reflection)
{
    if (m_selectedRun.value(QStringLiteral("run_id")).toString() != runId)
        return;
    m_selectedRun.insert(QStringLiteral("reflection"), reflection);
    Q_EMIT selectionChanged();
}

void HistoryController::updatePendingReviewCount(int count)
{
    if (count > 0) {
        refreshPendingReviewRun();
    } else if (!m_pendingReviewRun.isEmpty()) {
        m_pendingReviewRun = QJsonObject();
        Q_EMIT pendingReviewRunChanged();
    }
}


} // namespace mr
