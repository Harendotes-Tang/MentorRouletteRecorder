#include "HistoryController.h"
#include "IBackend.h"
#include "DutyCatalog.h"
#include "Formatters.h"
#include <QCoreApplication>
#include <QDateTime>
#include <QJsonArray>
#include <QJsonDocument>
#include <QTimeZone>
#include <QUuid>
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
    // 历史表日期与日期筛选共享 actual/source 投影；心得模型保持自己的排序。
    m_runs->sortBy(QStringLiteral("history_date"));
    m_historyFilter.insert(QStringLiteral("date_field"), QStringLiteral("history_date"));
    m_runs->setBackend(backend);
    // The existing model borrows a raw backend pointer. Keep that borrow within
    // this workflow's backend lifetime, including independently owned models.
    connect(backend, &QObject::destroyed, this, [this] { m_runs->setBackend(nullptr); });
    connect(m_runs, &QAbstractItemModel::modelReset, this, &HistoryController::checkedRunsChanged);
    connect(m_runs, &RunListModel::loadingChanged, this, &HistoryController::checkedRunsChanged);
    connect(backend, &IBackend::connectionChanged, this, [this] {
        if (m_backend && m_backend->isConnected())
            refreshRetentionSettings();
    });
}

void HistoryController::refreshPendingReviewRun()
{
    if (!m_backend)
        return;
    QJsonObject filter;
    filter.insert(QStringLiteral("pending_review"), true);
    filter.insert(QStringLiteral("source"), QJsonArray{QStringLiteral("AUTO_NETWORK"), QStringLiteral("MANUAL")});
    QJsonObject sort;
    sort.insert(QStringLiteral("field"), QStringLiteral("matched_at_utc"));
    sort.insert(QStringLiteral("direction"), QStringLiteral("desc"));
    m_backend->queryRuns(filter, 1, 1, sort)
        ->whenDone(this, [this](bool ok, const QVariantMap &payload, const QString &,
                                const QString &) {
            // A failed read is not evidence that the run was reviewed: keep
            // the banner on the run it showed (review OI-1).
            if (!ok)
                return;
            const QJsonArray items =
                QJsonObject::fromVariantMap(payload).value(QStringLiteral("items")).toArray();
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

    bool revisionAdvanced = false;
    const auto merge = [&run, &runId, revision, &revisionAdvanced](QJsonObject &current) {
        if (current.value(QStringLiteral("run_id")).toString() != runId
            || current.value(QStringLiteral("revision")).toInt() > revision)
            return false;
        QJsonObject next = current;
        for (auto field = run.begin(); field != run.end(); ++field)
            next.insert(field.key(), field.value());
        if (next == current)
            return false;
        revisionAdvanced |= current.value(QStringLiteral("revision")).toInt() < revision;
        current = next;
        return true;
    };
    bool changed = false;
    if (merge(m_selectedRun)) {
        Q_EMIT selectionChanged();
        changed = true;
        // A newer revision of the open record has a longer chain: re-read it, or
        // 修正历史 stops at the old row and 撤销 is withheld (review S2-6).
        if (revisionAdvanced)
            loadRevisionsForSelection();
    }
    if (merge(m_pendingReviewRun)) {
        Q_EMIT pendingReviewRunChanged();
        changed = true;
    }
    if (changed && revisionAdvanced)
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
    QVariantMap latest;
    for (const QVariant &value : m_selectedRunRevisions) {
        const QVariantMap revision = value.toMap();
        if (revision.value(QStringLiteral("revision")).toInt() > newest) {
            newest = revision.value(QStringLiteral("revision")).toInt();
            latest = revision;
        }
    }
    if (latest.value(QStringLiteral("actor")).toString() == QLatin1String("SYSTEM")) {
        if (latest.value(QStringLiteral("reason")).toString()
            == QStringLiteral("依据导入来源审计维护历史身份；缺游戏时间保留待补充，不作为采集结果待复核。"))
            return false;
        for (const QVariant &change : latest.value(QStringLiteral("changes")).toList())
            if (change.toMap().value(QStringLiteral("field")).toString() == QLatin1String("source"))
                return false; // 导入来源维护不能通过普通撤销还原成自动采集身份。
    }
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
    loadRevisionPage(runId, ++m_revisionLoadGeneration, 1, {});
}

void HistoryController::loadRevisionPage(const QString &runId, quint64 generation, int page,
                                         QVariantList collected)
{
    if (!m_backend)
        return;
    // The chain is paged oldest-first, 50 rows unless asked otherwise. Every page
    // is read, so the newest revision - the one 撤销 acts on - is always in the
    // list however long the chain grew (review OI-5).
    m_backend->getRunRevisions(runId, page, kRevisionPageSize)->whenDone(
        this, [this, runId, generation, page, collected = std::move(collected)](
                  bool ok, const QVariantMap &payload, const QString &, const QString &) mutable {
            if (!ok || generation != m_revisionLoadGeneration
                || m_selectedRun.value(QStringLiteral("run_id")).toString() != runId)
                return;
            const QVariantList items = payload.value(QStringLiteral("items")).toList();
            collected.append(items);
            const int total = payload.value(QStringLiteral("page_info")).toMap()
                                  .value(QStringLiteral("total"), int(collected.size())).toInt();
            if (!items.isEmpty() && collected.size() < total && page < kMaxRevisionPages) {
                loadRevisionPage(runId, generation, page + 1, std::move(collected));
                return;
            }
            m_selectedRunRevisions = collected;
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
                    // No machine code: ordinary users never see one, and this
                    // controller does not know whether the maintainer tools are open.
                    m_runEventsMessage =
                        QCoreApplication::translate("mr::AppController", "当前 Collector 不提供逐事件摘要。");
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
    QJsonObject next = QJsonObject::fromVariantMap(filter);
    next.insert(QStringLiteral("date_field"), QStringLiteral("history_date"));
    if (next == m_historyFilter)
        return;
    if (m_batchRunning) {
        Q_EMIT toastRequested(tr("批量操作尚未完成，请稍后调整筛选。"));
        Q_EMIT historyFilterChanged();
        return;
    }
    if (!m_checkedRuns.isEmpty()) {
        m_proposedFilter = next;
        m_filterConfirmationPending = true;
        Q_EMIT filterConfirmationChanged();
        return;
    }
    m_historyFilter = next;
    m_runs->setFilter(next.toVariantMap());
    Q_EMIT historyFilterChanged();
}

void HistoryController::resetHistoryFilter()
{
    setHistoryFilter({});
}

void HistoryController::confirmHistoryFilterChange(bool apply)
{
    if (!m_filterConfirmationPending)
        return;
    const QJsonObject next = m_proposedFilter;
    m_filterConfirmationPending = false;
    m_proposedFilter = {};
    Q_EMIT filterConfirmationChanged();
    if (apply && !m_batchRunning) {
        clearCheckedRuns();
        setHistoryFilter(next.toVariantMap());
    } else {
        // 取消后把还未生效的输入恢复为实际查询条件。
        Q_EMIT historyFilterChanged();
    }
}

int HistoryController::checkedDeletedCount() const
{
    int count = 0;
    for (const QJsonObject &run : m_checkedRuns)
        count += run.value(QStringLiteral("soft_deleted")).toBool() ? 1 : 0;
    return count;
}

bool HistoryController::allCurrentPageChecked() const
{
    if (m_runs->rowCount() == 0 || m_runs->isLoading() || !m_runs->loadError().isEmpty())
        return false;
    for (int row = 0; row < m_runs->rowCount(); ++row) {
        if (!m_checkedRuns.contains(m_runs->runAt(row).value(QStringLiteral("run_id")).toString()))
            return false;
    }
    return true;
}

void HistoryController::setRunChecked(const QVariantMap &run, bool checked)
{
    if (m_batchRunning)
        return;
    const QString id = run.value(QStringLiteral("run_id")).toString();
    if (id.isEmpty() || run.value(QStringLiteral("revision")).toInt() < 1)
        return;
    if (checked) {
        if (m_checkedRuns.contains(id))
            return;
        if (m_checkedRuns.size() >= 2000) {
            Q_EMIT toastRequested(tr("一次最多勾选 2000 条记录，请分批操作。"));
            return;
        }
        m_checkedRuns.insert(id, QJsonObject::fromVariantMap(run));
        m_checkedOrder.append(id);
    } else {
        if (!m_checkedRuns.remove(id))
            return;
        m_checkedOrder.removeAll(id);
    }
    Q_EMIT checkedRunsChanged();
}

void HistoryController::setCurrentPageChecked(bool checked)
{
    if (m_runs->isLoading() || !m_runs->loadError().isEmpty())
        return;
    for (int row = 0; row < m_runs->rowCount(); ++row)
        setRunChecked(m_runs->runAt(row), checked);
}

void HistoryController::clearCheckedRuns()
{
    if (m_batchRunning || m_checkedRuns.isEmpty())
        return;
    m_checkedRuns.clear();
    m_checkedOrder.clear();
    Q_EMIT checkedRunsChanged();
}

void HistoryController::mutateCheckedRuns(const QString &action, const QString &reason)
{
    if (!m_backend || m_batchRunning || m_checkedRuns.isEmpty())
        return;
    const bool deleting = action == QLatin1String("soft_delete");
    if ((!deleting && action != QLatin1String("restore") && action != QLatin1String("purge"))
        || reason.trimmed().isEmpty() || reason.trimmed().size() > 500) {
        m_batchFeedback = tr("请选择有效操作并填写 1–500 字原因。");
        Q_EMIT batchChanged();
        return;
    }
    if ((deleting && checkedDeletedCount() != 0)
        || (!deleting && checkedDeletedCount() != checkedRunCount())) {
        m_batchFeedback = deleting ? tr("请只勾选未删除记录后移入回收站。")
                                  : tr("恢复和永久删除只能操作已在回收站的记录。");
        Q_EMIT batchChanged();
        return;
    }
    QJsonArray rows;
    const QStringList ids = m_checkedOrder;
    for (const QString &id : ids)
        rows.append(QJsonObject{{QStringLiteral("run_id"), id},
                                {QStringLiteral("expected_revision"), m_checkedRuns.value(id).value(QStringLiteral("revision"))}});
    const QJsonObject payload{{QStringLiteral("action"), action}, {QStringLiteral("runs"), rows},
                              {QStringLiteral("reason"), reason.trimmed()}};
    const QString requestId = requestIdFor(m_unansweredBatch, QJsonDocument(payload).toJson(QJsonDocument::Compact));
    m_batchRunning = true;
    m_batchFeedback.clear();
    Q_EMIT batchChanged();
    m_backend->requestWithId(QStringLiteral("BatchMutateRuns"), payload, requestId)
        ->whenDone(this, [this, action, ids, requestId](bool ok, const QVariantMap &answer,
                                                       const QString &code, const QString &message) {
            settle(m_unansweredBatch, requestId, ok, code);
            m_batchRunning = false;
            if (!ok) {
                const bool uncertain = code == QLatin1String("ERR_PIPE_CLOSED") || code == QLatin1String("ERR_TIMEOUT")
                    || code == QLatin1String("ERR_DISCONNECTED");
                m_batchFeedback = (message.isEmpty() ? code : message) + (uncertain
                    ? tr(" 请求结果尚未确认。请保留勾选并重试，同一请求不会重复执行。")
                    : tr(" 请刷新列表，取消并重新勾选发生变化的记录后重试；本批没有部分删除。"));
                Q_EMIT batchChanged();
                return;
            }
            for (const QString &id : ids) {
                m_checkedRuns.remove(id);
                m_checkedOrder.removeAll(id);
            }
            if (ids.contains(m_selectedRun.value(QStringLiteral("run_id")).toString()))
                clearSelection();
            const QString verb = action == QLatin1String("restore") ? tr("恢复")
                : action == QLatin1String("purge") ? tr("永久删除") : tr("移入回收站");
            m_batchFeedback = tr("已%1 %2 条记录。").arg(verb).arg(answer.value(QStringLiteral("changed_count")).toInt());
            if (action == QLatin1String("purge"))
                m_batchFeedback += tr(" 关联图片清理已排队，失败会保留任务重试。已有备份和导出文件保留。");
            Q_EMIT checkedRunsChanged();
            Q_EMIT batchChanged();
            Q_EMIT batchSucceeded(action, ids);
            Q_EMIT refreshRequested();
        });
}

void HistoryController::refreshRetentionSettings()
{
    if (!m_backend || !m_backend->isConnected() || m_retentionSaving)
        return;
    const quint64 generation = ++m_retentionGeneration;
    m_backend->request(QStringLiteral("GetHistoryRetentionSettings"))
        ->whenDone(this, [this, generation](bool ok, const QVariantMap &payload,
                                            const QString &, const QString &message) {
            if (generation != m_retentionGeneration)
                return;
            m_retentionLoaded = true;
            if (ok) {
                m_retentionDays = payload.value(QStringLiteral("retention_days")).toInt();
                m_retentionFeedback.clear();
            } else {
                m_retentionDays = -1;
                m_retentionFeedback = message.isEmpty() ? tr("无法读取回收站保留期，请重试。") : message;
            }
            Q_EMIT retentionChanged();
        });
}

void HistoryController::updateRetentionSettings(int days)
{
    if (!m_backend || !m_backend->isConnected() || m_retentionSaving)
        return;
    if (days < 0 || days > 36500) {
        m_retentionFeedback = tr("请输入 1–36500 天，或选择永不自动清理。");
        Q_EMIT retentionChanged();
        return;
    }
    ++m_retentionGeneration;
    m_retentionSaving = true;
    m_retentionFeedback.clear();
    Q_EMIT retentionChanged();
    m_backend->request(QStringLiteral("UpdateHistoryRetentionSettings"), {{QStringLiteral("retention_days"), days}})
        ->whenDone(this, [this, days](bool ok, const QVariantMap &payload,
                                     const QString &code, const QString &message) {
            m_retentionSaving = false;
            if (ok) {
                m_retentionDays = payload.value(QStringLiteral("retention_days"), days).toInt();
                m_retentionLoaded = true;
                m_retentionFeedback = tr("回收站保留期已保存。");
            } else {
                m_retentionFeedback = message.isEmpty() ? code : message;
            }
            Q_EMIT retentionChanged();
        });
}

void HistoryController::sendRunMutation(const QString &kind, const QString &runId,
                                        BackendReply *reply, bool keepSelection)
{
    if (!reply)
        return;
    // whenDone() runs before the reply is deleted, so the guard is still set there.
    const QPointer<BackendReply> sent(reply);
    reply->whenDone(this, [this, kind, runId, keepSelection, sent](bool ok, const QVariantMap &payload,
                                                                   const QString &code,
                                                                   const QString &message) {
        if (!ok) {
            Q_EMIT mutationFailed(code, message, kind, runId, sent && sent->neverSent());
            Q_EMIT toastRequested(message.isEmpty() ? code : message);
            return;
        }
        adoptRunMutation(kind, payload, keepSelection);
    });
}

void HistoryController::adoptRunMutation(const QString &kind, const QVariantMap &payload,
                                     bool keepSelection)
{
    const bool stillSelected = !m_selectedRun.isEmpty()
        && m_selectedRun.value(QStringLiteral("run_id")).toString()
            == payload.value(QStringLiteral("run_id")).toString();
    if (keepSelection && stillSelected
        && payload.value(QStringLiteral("revision")).toInt()
            >= m_selectedRun.value(QStringLiteral("revision")).toInt()) {
        m_selectedRun = QJsonObject::fromVariantMap(payload.value(QStringLiteral("run")).toMap());
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
    } else if (kind == QLatin1String("supplement_job")) {
        Q_EMIT toastRequested(QString::fromUtf8("已补录职业 · 修订 %1")
                      .arg(payload.value(QStringLiteral("revision")).toInt()));
    }

    if (!keepSelection && stillSelected)
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
                              QString::fromUtf8("必须填写新增原因。"), QStringLiteral("create"),
                              QString());
        return;
    }

    const QJsonObject run = contractRunFields(QJsonObject::fromVariantMap(fields));
    const QString requestId = requestIdFor(
        m_unansweredCreate,
        QJsonDocument(QJsonObject{{QStringLiteral("run"), run},
                                  {QStringLiteral("reason"), reason}})
            .toJson(QJsonDocument::Compact));
    BackendReply *reply = m_backend->createManualRun(run, reason, requestId);
    // whenDone() runs before the reply is deleted, so the guard is still set there.
    const QPointer<BackendReply> sent(reply);
    reply
        ->whenDone(this, [this, requestId, sent](bool ok, const QVariantMap &payload,
                                                 const QString &code, const QString &message) {
            settle(m_unansweredCreate, requestId, ok, code);
            if (!ok) {
                Q_EMIT mutationFailed(code, message, QStringLiteral("create"), QString(),
                                      sent && sent->neverSent());
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
                                           const QString &runId, int expectedRevision)
{
    if (!m_backend)
        return;
    const QString targetRunId = m_selectedRun.value(QStringLiteral("run_id")).toString();
    // The form names the record it edits; its refusal goes back to that form.
    const QString formRunId = runId.isEmpty() ? targetRunId : runId;
    if (m_selectedRun.isEmpty()
        || (!runId.isEmpty() && runId != targetRunId)) {
        Q_EMIT mutationFailed(QStringLiteral("ERR_SELECTION_CHANGED"),
                              QString::fromUtf8("选中的记录已经变了，未保存任何修改。请关闭后重新打开要修正的记录。"),
                              QStringLiteral("correct"), formRunId);
        return;
    }
    if (reason.trimmed().isEmpty()) {
        Q_EMIT mutationFailed(QStringLiteral("ERR_REASON_REQUIRED"),
                              QString::fromUtf8("必须填写修正原因。"), QStringLiteral("correct"),
                              formRunId);
        return;
    }

    const int revision = expectedRevision >= 0
                             ? expectedRevision
                             : m_selectedRun.value(QStringLiteral("revision")).toInt();
    const QJsonObject contractChanges = contractRunFields(QJsonObject::fromVariantMap(changes));
    const QString requestId = requestIdFor(
        m_unansweredCorrection,
        QJsonDocument(QJsonObject{{QStringLiteral("run_id"), targetRunId},
                                  {QStringLiteral("expected_revision"), revision},
                                  {QStringLiteral("changes"), contractChanges},
                                  {QStringLiteral("reason"), reason}})
            .toJson(QJsonDocument::Compact));
    BackendReply *reply =
        m_backend->correctRun(targetRunId, revision, contractChanges, reason, requestId);
    if (reply) {
        reply->whenDone(this, [this, requestId](bool ok, const QVariantMap &, const QString &code,
                                                const QString &) {
            settle(m_unansweredCorrection, requestId, ok, code);
        });
    }
    sendRunMutation(QStringLiteral("correct"), targetRunId, reply, true);
}

QString HistoryController::requestIdFor(UnansweredMutation &slot, const QByteArray &content)
{
    if (slot.requestId.isEmpty() || slot.inFlight || slot.content != content) {
        slot.requestId = QUuid::createUuid().toString(QUuid::WithoutBraces);
        slot.content = content;
    }
    slot.inFlight = true;
    return slot.requestId;
}

void HistoryController::settle(UnansweredMutation &slot, const QString &requestId, bool ok,
                               const QString &code)
{
    if (slot.requestId != requestId)
        return;
    slot.inFlight = false;
    if (ok || code == QLatin1String("ERR_IDEMPOTENCY_CONFLICT"))
        slot = UnansweredMutation();
}

void HistoryController::resolveRunResult(const QString &runId, int revision,
                                    const QString &result, const QString &reason, int jobId)
{
    if (!m_backend || runId.isEmpty() || result.isEmpty())
        return;
    if (reason.trimmed().isEmpty()) {
        Q_EMIT mutationFailed(QStringLiteral("ERR_REASON_REQUIRED"),
                              QString::fromUtf8("必须填写确认原因。"), QStringLiteral("review"),
                              runId);
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
            QString::fromUtf8("找不到这条记录的版本号，请在历史记录中修正。"),
            QStringLiteral("review"), runId);
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

void HistoryController::supplementRunJob(const QString &runId, int revision, int jobId,
                                         const QString &reason)
{
    if (!m_backend)
        return;
    if (runId.isEmpty() || revision < 1 || jobId <= 0 || reason.trimmed().isEmpty()) {
        Q_EMIT mutationFailed(QStringLiteral("ERR_BAD_REQUEST"),
                              QString::fromUtf8("记录、职业或修订信息不完整，请重新打开记录后补录。"),
                              QStringLiteral("supplement_job"), runId);
        return;
    }
    // No result field: supplying even its unchanged value would acknowledge
    // the pending outcome review, which choosing a job does not do.
    sendResultCorrection(runId, revision, {}, reason, jobId, /*allowRetry=*/true);
}

void HistoryController::sendResultCorrection(const QString &runId, int expectedRevision,
                                         const QString &result, const QString &reason,
                                         int jobId, bool allowRetry)
{
    if (!m_backend)
        return;
    QJsonObject changes;
    if (!result.isEmpty())
        changes.insert(QStringLiteral("result"), result);
    if (jobId > 0)
        changes.insert(QStringLiteral("job_id"), jobId);
    const bool keepSelection =
        m_selectedRun.value(QStringLiteral("run_id")).toString() == runId;
    const QString kind = result.isEmpty() ? QStringLiteral("supplement_job") : QStringLiteral("review");
    BackendReply *reply = m_backend->correctRun(runId, expectedRevision, changes, reason);
    if (!allowRetry) {
        sendRunMutation(kind, runId, reply, keepSelection);
        return;
    }
    if (!reply)
        return;

    reply->whenDone(this, [this, runId, expectedRevision, result, reason, jobId, keepSelection, kind](
                              bool ok, const QVariantMap &payload, const QString &code,
                              const QString &message) {
        if (ok) {
            adoptRunMutation(kind, payload, keepSelection);
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
        Q_EMIT mutationFailed(code, message, kind, runId);
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
    const QString kind = result.isEmpty() ? QStringLiteral("supplement_job") : QStringLiteral("review");
    m_backend->getRunRevisions(runId, page, kRevisionPageSize)->whenDone(
        this, [this, runId, staleRevision, result, reason, jobId, page, kind](
                  bool ok, const QVariantMap &payload, const QString &code,
                  const QString &message) {
            if (!ok) {
                Q_EMIT mutationFailed(code, message, kind, runId);
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
                if (number > staleRevision && revisionTouchesResult(revision, jobId > 0, !result.isEmpty()))
                    overlaps = true;
            }
            if (newest <= 0) {
                Q_EMIT mutationFailed(QStringLiteral("ERR_REVISION_CONFLICT"), reopen, kind, runId);
                return;
            }
            if (newest > page * kRevisionPageSize) {
                // More than a page of revisions landed since the dialog opened.
                // What they changed is unknown here, so nothing is retried.
                Q_EMIT runRevisionChanged(runId, newest);
                Q_EMIT mutationFailed(QStringLiteral("ERR_REVISION_CONFLICT"), reopen, kind, runId);
                Q_EMIT toastRequested(reopen);
                return;
            }
            // Publish the observed revision for read-only projections. Forms
            // keep their baseline; only the checked retry below advances it.
            Q_EMIT runRevisionChanged(runId, newest);
            if (overlaps) {
                // Another place has already answered for this run. Whoever
                // wrote first wins (docs/manual-correction.md section 3); the
                // user sees the record as it is now and decides again.
                const QString text = result.isEmpty()
                    ? QString::fromUtf8("这条记录的职业刚刚已在别处修改过，本次没有改动。请重新打开记录后再决定。")
                    : QString::fromUtf8("这条记录的结果刚刚已在别处确认过，本次没有改动。请查看当前记录后再决定。");
                Q_EMIT mutationFailed(QStringLiteral("ERR_REVISION_CONFLICT"), text, kind, runId);
                Q_EMIT toastRequested(text);
                return;
            }
            sendResultCorrection(runId, newest, result, reason, jobId, /*allowRetry=*/false);
        });
}

bool HistoryController::revisionTouchesResult(const QVariantMap &revision, bool withJob, bool withResult)
{
    // A result correction writes `result`, implicitly clears `pending_review`
    // and may set `job_id`; a revision that changed any of those already made
    // the decision this dialog is about to make.
    const QVariantList changes = revision.value(QStringLiteral("changes")).toList();
    for (const QVariant &value : changes) {
        const QString field = value.toMap().value(QStringLiteral("field")).toString();
        if (withResult && (field == QLatin1String("result") || field == QLatin1String("pending_review")))
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
    const QString runId = m_selectedRun.value(QStringLiteral("run_id")).toString();
    if (reason.trimmed().isEmpty()) {
        Q_EMIT mutationFailed(QStringLiteral("ERR_REASON_REQUIRED"),
                              QString::fromUtf8("必须填写确认原因。"), QStringLiteral("review"),
                              runId);
        return;
    }
    if (!m_selectedRun.value(QStringLiteral("pending_review")).toBool(false)) {
        Q_EMIT mutationFailed(QStringLiteral("ERR_BAD_REQUEST"),
                              QString::fromUtf8("该记录不在待复核状态。"), QStringLiteral("review"),
                              runId);
        return;
    }

    // Cleared through CorrectRun rather than a dedicated message: the
    // acknowledgement is a human decision about a run whose end was inferred,
    // and it belongs in the same append-only audit trail as every other
    // correction.
    QJsonObject changes;
    changes.insert(QStringLiteral("pending_review"), false);
    sendRunMutation(QStringLiteral("review"), runId,
                    m_backend->correctRun(
                        runId, m_selectedRun.value(QStringLiteral("revision")).toInt(), changes,
                        reason),
                    true);
}

void HistoryController::undoSelectedRunRevision(const QString &reason)
{
    if (!m_backend || m_selectedRun.isEmpty())
        return;
    const QString runId = m_selectedRun.value(QStringLiteral("run_id")).toString();
    if (reason.trimmed().isEmpty()) {
        Q_EMIT mutationFailed(QStringLiteral("ERR_REASON_REQUIRED"),
                              QString::fromUtf8("必须填写撤销原因。"), QStringLiteral("undo"),
                              runId);
        return;
    }
    if (!selectedRunCanUndo()) {
        Q_EMIT mutationFailed(QStringLiteral("ERR_UNDO_NOT_ALLOWED"),
                              QString::fromUtf8("只能撤销最新一次修正，且首个修订"
                                                "不可撤销。"),
                              QStringLiteral("undo"), runId);
        return;
    }

    sendRunMutation(QStringLiteral("undo"), runId,
                    m_backend->undoRevision(
                        runId, m_selectedRun.value(QStringLiteral("revision")).toInt(), reason),
                    true);
}

void HistoryController::softDeleteSelectedRun(const QString &reason)
{
    if (!m_backend || m_selectedRun.isEmpty())
        return;
    const QString runId = m_selectedRun.value(QStringLiteral("run_id")).toString();
    sendRunMutation(QStringLiteral("delete"), runId,
                    m_backend->softDeleteRun(
                        runId, m_selectedRun.value(QStringLiteral("revision")).toInt(), reason),
                    false);
}

void HistoryController::restoreSelectedRun(const QString &reason)
{
    if (!m_backend || m_selectedRun.isEmpty())
        return;
    const QString runId = m_selectedRun.value(QStringLiteral("run_id")).toString();
    sendRunMutation(QStringLiteral("restore"), runId,
                    m_backend->restoreRun(
                        runId, m_selectedRun.value(QStringLiteral("revision")).toInt(), reason),
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
