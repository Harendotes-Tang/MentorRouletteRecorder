#pragma once

#include <QHash>
#include <QJsonObject>
#include <QObject>
#include <QQmlEngine>
#include <QSet>
#include <QTimer>
#include <QVariantList>
#include <QVariantMap>

namespace mr {
class IBackend;

/// Candidate-only read/review surface. Owns bounded paging and a complete cross-page
/// anchor index; it has no run, statistics, achievement or speech dependency.
class CandidateReviewController final : public QObject
{
    Q_OBJECT
    QML_ANONYMOUS
    Q_PROPERTY(QVariantList rows READ rows NOTIFY changed)
    Q_PROPERTY(QVariantMap pageInfo READ pageInfo NOTIFY changed)
    Q_PROPERTY(int page READ page NOTIFY changed)
    Q_PROPERTY(int pageSize READ pageSize CONSTANT)
    Q_PROPERTY(int total READ total NOTIFY changed)
    Q_PROPERTY(QString sessionId READ sessionId WRITE setSessionId NOTIFY changed)
    Q_PROPERTY(QVariantList sessions READ sessions NOTIFY changed)
    Q_PROPERTY(QVariantList anchorPairs READ anchorPairs NOTIFY changed)
    // Plain-language event timeline built from the full ledger: login, queue, pop,
    // inferred duty entry/exit, plain zone changes. One entry per event, never per packet.
    Q_PROPERTY(QVariantList timeline READ timeline NOTIFY changed)
    Q_PROPERTY(bool loading READ loading NOTIFY changed)
    Q_PROPERTY(bool indexLoading READ indexLoading NOTIFY changed)
    Q_PROPERTY(bool indexComplete READ indexComplete NOTIFY changed)
    Q_PROPERTY(int indexLoadedCount READ indexLoadedCount NOTIFY changed)
    Q_PROPERTY(QString indexMessage READ indexMessage NOTIFY changed)
    Q_PROPERTY(QString error READ error NOTIFY changed)
    Q_PROPERTY(bool hasNewObservations READ hasNewObservations NOTIFY changed)
    Q_PROPERTY(QString reviewBusyId READ reviewBusyId NOTIFY changed)
    Q_PROPERTY(QString reviewError READ reviewError NOTIFY changed)
    Q_PROPERTY(bool exporting READ exporting NOTIFY changed)
    Q_PROPERTY(QVariantMap exportResult READ exportResult NOTIFY changed)
    Q_PROPERTY(QString exportError READ exportError NOTIFY changed)
    Q_PROPERTY(bool candidateSettingsBusy READ candidateSettingsBusy NOTIFY changed)
    Q_PROPERTY(QString candidateSettingsError READ candidateSettingsError NOTIFY changed)

public:
    explicit CandidateReviewController(QObject *parent = nullptr);
    void setBackend(IBackend *backend);
    void setActive(bool active);
    void setTargetOverride(const QString &directory) { m_targetOverride = directory; }
    void observeLiveEvent(const QVariantMap &event);

    QVariantList rows() const { return m_rows; }
    QVariantMap pageInfo() const;
    int page() const { return m_page; }
    int pageSize() const { return 50; }
    int total() const { return m_total; }
    QString sessionId() const { return m_sessionId; }
    QVariantList sessions() const { return m_sessions; }
    QVariantList anchorPairs() const;
    QVariantList timeline() const;
    bool loading() const { return m_loading; }
    bool indexLoading() const { return m_indexLoading; }
    bool indexComplete() const { return m_indexComplete; }
    int indexLoadedCount() const { return m_indexIds.size(); }
    QString indexMessage() const { return m_indexMessage; }
    QString error() const { return m_error; }
    bool hasNewObservations() const { return m_hasNewObservations; }
    QString reviewBusyId() const { return m_reviewBusyId; }
    QString reviewError() const { return m_reviewError; }
    bool exporting() const { return m_exporting; }
    QVariantMap exportResult() const { return m_exportResult; }
    QString exportError() const { return m_exportError; }
    bool candidateSettingsBusy() const { return m_settingsBusy; }
    QString candidateSettingsError() const { return m_settingsError; }

public Q_SLOTS:
    void refresh();
    void loadPage(int page);
    void setSessionId(const QString &sessionId);
    void review(const QString &observationId, const QString &verdict, const QString &note);
    void exportEvidence();
    void setCandidateEnabled(bool enabled);
    void setResearchOpcodes(const QVariantList &opcodes);

Q_SIGNALS:
    void changed();
    void reviewFinished(const QString &observationId, bool ok, const QString &error);
    void captureSettingsApplied(const QVariantMap &settings);
    void captureStatusRefreshRequested();

private:
    void startIndex(bool retry = false);
    void readIndexPage(int page, int generation);
    void indexFailed(const QString &message, bool retryable);
    void finishIndex();
    void buildTimeline();
    void writeSettings(const QJsonObject &changes);
    void disconnected();

    IBackend *m_backend = nullptr;
    QVariantList m_rows;
    QVariantList m_sessions;
    QVariantList m_pairs;
    QVariantList m_timeline;
    QHash<QString, QVariantMap> m_indexSessions;
    // Event-bearing rows per session|connection: zone anchors, queue registration and
    // cancellation, finder notifications. Acks and cluster members stay out.
    QHash<QString, QVariantList> m_indexEvents;
    QSet<QString> m_indexIds;
    QTimer m_liveRefresh;
    QString m_sessionId;
    QString m_indexUpperUtc;
    QString m_indexMessage;
    QString m_error;
    QString m_reviewBusyId;
    QString m_reviewError;
    QString m_targetOverride;
    QString m_exportError;
    QString m_settingsError;
    QVariantMap m_exportResult;
    int m_page = 1;
    int m_total = 0;
    int m_pageGeneration = 0;
    int m_indexGeneration = 0;
    int m_connectionGeneration = 0;
    int m_indexExpectedTotal = -1;
    bool m_active = false;
    bool m_loading = false;
    bool m_indexLoading = false;
    bool m_indexComplete = false;
    bool m_indexRetried = false;
    bool m_indexChanged = false;
    bool m_hasNewObservations = false;
    bool m_exporting = false;
    bool m_settingsBusy = false;
};
} // namespace mr
