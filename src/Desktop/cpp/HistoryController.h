#pragma once

#include <functional>
#include <QJsonObject>
#include <QObject>
#include <QPointer>
#include <QVariantList>
#include <QVariantMap>
#include <QMap>
#include <QStringList>
#include "RunListModel.h"

namespace mr {
class IBackend;
class BackendReply;

/// Owns the history model, selected/pending records and revision/event request handling.
/// The backend and its reply objects are borrowed; this QObject supplies the callback
/// context, while QPointer and backend-destruction handlers guard the borrowed backend.
class HistoryController : public QObject
{
    Q_OBJECT
public:
    explicit HistoryController(IBackend *backend, QObject *parent = nullptr);
    RunListModel *runs() const { return m_runs; }
    QVariantMap historyFilter() const { return m_historyFilter.toVariantMap(); }
    QVariantMap pendingReviewRun() const { return m_pendingReviewRun.toVariantMap(); }
    bool hasSelection() const { return !m_selectedRun.isEmpty(); }
    QVariantList selectedRunRevisions() const { return m_selectedRunRevisions; }
    QVariantList selectedRunEvents() const { return m_selectedRunEvents; }
    QString runEventsState() const { return m_runEventsState; }
    QString runEventsMessage() const { return m_runEventsMessage; }
    void openRun(const QVariantMap &run);
    void adoptReflection(const QString &runId, const QJsonValue &reflection);
    void updatePendingReviewCount(int count);

    void refreshPendingReviewRun();
    void adoptRunRevisionFromEvent(const QJsonObject &run);
    QVariantMap selectedRun() const;
    bool selectedRunCanUndo() const;
    void selectRun(const QVariantMap &run);
    void clearSelection();
    void refreshRunEvents();
    void setHistoryFilter(const QVariantMap &filter);
    void resetHistoryFilter();
    void createManualRun(const QVariantMap &fields, const QString &reason);
    /// The dialog supplies both the run id and the revision it actually displayed.
    /// A live update must not silently advance that form's conflict-check baseline.
    void correctSelectedRun(const QVariantMap &changes, const QString &reason,
                            const QString &runId = QString(), int expectedRevision = -1);
    void resolveRunResult(const QString &runId, int revision,
                                    const QString &result, const QString &reason, int jobId = 0);
    void supplementRunJob(const QString &runId, int revision, int jobId, const QString &reason);
    void confirmSelectedRunReview(const QString &reason);
    void undoSelectedRunRevision(const QString &reason);
    void softDeleteSelectedRun(const QString &reason);
    void restoreSelectedRun(const QString &reason);
    /// 勾选保存当时的修订，不随翻页或后台刷新偷偷推进冲突基线。
    QStringList checkedRunIds() const { return m_checkedOrder; }
    int checkedRunCount() const { return m_checkedOrder.size(); }
    int checkedDeletedCount() const;
    bool allCurrentPageChecked() const;
    bool batchRunning() const { return m_batchRunning; }
    QString batchFeedback() const { return m_batchFeedback; }
    void setRunChecked(const QVariantMap &run, bool checked);
    void setCurrentPageChecked(bool checked);
    void clearCheckedRuns();
    void mutateCheckedRuns(const QString &action, const QString &reason);
    bool filterConfirmationPending() const { return m_filterConfirmationPending; }
    void confirmHistoryFilterChange(bool apply);
    int retentionDays() const { return m_retentionDays; }
    bool retentionLoaded() const { return m_retentionLoaded; }
    bool retentionSaving() const { return m_retentionSaving; }
    QString retentionFeedback() const { return m_retentionFeedback; }
    void refreshRetentionSettings();
    void updateRetentionSettings(int days);

Q_SIGNALS:
    void selectionChanged();
    void runEventsChanged();
    void historyFilterChanged();
    void pendingReviewRunChanged();
    void runRevisionChanged(const QString &runId, int revision);
    /// \a kind and \a runId name the request that was refused, as
    /// mutationSucceeded names the one that was accepted; \a runId is empty for
    /// a creation, whose record does not exist yet. \a neverSent is
    /// BackendReply::neverSent(): the request never reached the Collector.
    void mutationFailed(const QString &code, const QString &message, const QString &kind,
                        const QString &runId, bool neverSent = false);
    void mutationSucceeded(const QString &kind, const QString &runId, int revision,
                           const QString &auditEventId);
    void toastRequested(const QString &message);
    /// Accepted mutations invalidate other views as well as the history list.
    void refreshRequested();
    void checkedRunsChanged();
    void batchChanged();
    void batchSucceeded(const QString &action, const QStringList &runIds);
    void filterConfirmationChanged();
    void retentionChanged();

private:
    void loadRevisionsForSelection();
    void loadRevisionPage(const QString &runId, quint64 generation, int page,
                          QVariantList collected);
    void sendRunMutation(const QString &kind, const QString &runId, BackendReply *reply,
                                    bool keepSelection);
    void adoptRunMutation(const QString &kind, const QVariantMap &payload,
                                     bool keepSelection);
    void sendResultCorrection(const QString &runId, int expectedRevision,
                                         const QString &result, const QString &reason,
                                         int jobId, bool allowRetry);
    void retryResultCorrectionWithFreshRevision(const QString &runId, int staleRevision,
                                                           const QString &result,
                                                           const QString &reason, int jobId);
    static bool revisionTouchesResult(const QVariantMap &revision, bool withJob, bool withResult);

    /// The request_id of a create or a correction whose fate is unknown - the
    /// client stopped waiting, or the pipe dropped - and what it carried. The
    /// Collector may have applied it all the same, so pressing 提交 again with
    /// the same content resends it under the same id and its idempotency
    /// answers with the first result rather than writing a second record
    /// (review OH-2). Other content gets an id of its own: reusing this one
    /// would be refused as ERR_IDEMPOTENCY_CONFLICT.
    struct UnansweredMutation {
        QString requestId;
        QByteArray content;
        bool inFlight = false;
    };
    /// The id to send \a content under, reusing \a slot's when it carried the
    /// same content and is no longer on the wire.
    static QString requestIdFor(UnansweredMutation &slot, const QByteArray &content);
    /// Forget \a slot once the Collector has answered the request \a requestId:
    /// accepted, or refused as an idempotency conflict.
    static void settle(UnansweredMutation &slot, const QString &requestId, bool ok,
                       const QString &code);
    UnansweredMutation m_unansweredCreate;
    UnansweredMutation m_unansweredCorrection;
    UnansweredMutation m_unansweredBatch;
    QMap<QString, QJsonObject> m_checkedRuns;
    QStringList m_checkedOrder;
    QJsonObject m_proposedFilter;
    bool m_filterConfirmationPending = false;
    bool m_batchRunning = false;
    QString m_batchFeedback;
    int m_retentionDays = -1;
    bool m_retentionLoaded = false;
    bool m_retentionSaving = false;
    quint64 m_retentionGeneration = 0;
    QString m_retentionFeedback;
    /// Contract cap for GetRunRevisions page_size (contracts/ipc-protocol: 200).
    static constexpr int kRevisionPageSize = 200;
    /// Upper bound on pages read for one chain (10 000 revisions); a guard
    /// against a Collector whose total never stops growing, not a real limit.
    static constexpr int kMaxRevisionPages = 50;
    QPointer<IBackend> m_backend;
    /// Bumped by every revision-list load; replies of an older load are dropped.
    quint64 m_revisionLoadGeneration = 0;
    RunListModel *m_runs;
    QJsonObject m_selectedRun;
    QJsonObject m_historyFilter;
    QJsonObject m_pendingReviewRun;
    QVariantList m_selectedRunRevisions;
    QVariantList m_selectedRunEvents;
    QString m_runEventsState = QStringLiteral("idle");
    QString m_runEventsMessage;
    QString m_runEventsRunId;
};

} // namespace mr
