#pragma once

#include <functional>
#include <QJsonObject>
#include <QObject>
#include <QPointer>
#include <QVariantList>
#include <QVariantMap>
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
    ///  runId is the run the edit dialog was opened for; a selection that moved on since is
    /// refused, so a correction can never be saved onto a run the dialog did not show.
    void correctSelectedRun(const QVariantMap &changes, const QString &reason,
                            const QString &runId = QString());
    void resolveRunResult(const QString &runId, int revision,
                                    const QString &result, const QString &reason, int jobId = 0);
    void confirmSelectedRunReview(const QString &reason);
    void undoSelectedRunRevision(const QString &reason);
    void softDeleteSelectedRun(const QString &reason);
    void restoreSelectedRun(const QString &reason);

Q_SIGNALS:
    void selectionChanged();
    void runEventsChanged();
    void historyFilterChanged();
    void pendingReviewRunChanged();
    void runRevisionChanged(const QString &runId, int revision);
    void mutationFailed(const QString &code, const QString &message);
    void mutationSucceeded(const QString &kind, const QString &runId, int revision,
                           const QString &auditEventId);
    void toastRequested(const QString &message);
    /// Accepted mutations invalidate other views as well as the history list.
    void refreshRequested();

private:
    void loadRevisionsForSelection();
    void sendRunMutation(const QString &kind, BackendReply *reply,
                                    bool keepSelection);
    void adoptRunMutation(const QString &kind, const QVariantMap &payload,
                                     bool keepSelection);
    void sendResultCorrection(const QString &runId, int expectedRevision,
                                         const QString &result, const QString &reason,
                                         int jobId, bool allowRetry);
    void retryResultCorrectionWithFreshRevision(const QString &runId,
                                                           const QString &result,
                                                           const QString &reason, int jobId);
    QPointer<IBackend> m_backend;
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
