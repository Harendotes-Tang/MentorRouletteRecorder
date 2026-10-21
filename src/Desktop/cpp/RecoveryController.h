#pragma once

#include <QObject>
#include <QHash>
#include <QPointer>
#include <QVariantList>
#include <QVariantMap>
#include <QVector>

namespace mr {
class AppSettings;
class IBackend;

/// Local reminder/draft journal. It reads Collector records but never mutates a Run.
/// The owner forwards the verified database identity; callbacks from an older identity
/// or refresh generation cannot replace this controller's current projection.
class RecoveryController : public QObject
{
    Q_OBJECT
    Q_PROPERTY(QVariantList items READ items NOTIFY changed)
    Q_PROPERTY(int count READ count NOTIFY changed)
    Q_PROPERTY(bool busy READ busy NOTIFY changed)
    Q_PROPERTY(QString error READ error NOTIFY changed)
    Q_PROPERTY(QString databaseKey READ databaseKey NOTIFY scopeChanged)
    Q_PROPERTY(bool identityVerified READ identityVerified NOTIFY changed)
public:
    explicit RecoveryController(IBackend *backend, AppSettings *settings, QObject *parent = nullptr);
    QVariantList items() const;
    int count() const { return static_cast<int>(items().size()); }
    bool busy() const { return m_busy; }
    QString error() const { return m_error; }
    QString databaseKey() const { return m_databaseKey; }
    bool identityVerified() const { return m_identityVerified; }
    Q_INVOKABLE void invalidateIdentity();
    /// Empty paths leave the last verified database identity intact.
    Q_INVOKABLE void setDatabasePath(const QString &path);
    /// Register only an already eligible live prompt, before its dialog is queued.
    Q_INVOKABLE void offer(const QVariantMap &run, const QString &kind);
    Q_INVOKABLE void registerPrompt(const QVariantMap &run, const QString &kind) { offer(run, kind); }
    /// Fields: text, mood, jobId. resetBaseline=true records the user's explicit
    /// choice to edit the latest record using a conflicting recovered draft.
    /// Failure keeps input in memory and reports error; it is never a Run write.
    Q_INVOKABLE bool saveDraft(const QVariantMap &run, const QVariantMap &fields);
    Q_INVOKABLE QVariantMap draft(const QVariantMap &run) const;
    /// Only return a token whose persisted draft fields match the submitted input.
    /// A merge may have loaded another editor's draft since this editor last typed.
    Q_INVOKABLE QString draftTokenForFields(const QString &runId, const QVariantMap &fields) const;
    /// Token matching protects edits made while a previous save was in flight.
    Q_INVOKABLE void clearDraft(const QString &runId, const QString &token = QString());
    Q_INVOKABLE void markReflectionSaved(const QString &runId, const QString &draftToken = QString());
    Q_INVOKABLE void markResultSaved(const QString &runId);
    /// 移除提醒: drop an unavailable item's reminders and its draft from the
    /// journal. Only for an item whose record could not be opened; it never
    /// touches the record itself (review I2-1).
    Q_INVOKABLE void dismiss(const QString &runId);
    Q_INVOKABLE void requestOpen(const QString &runId);
    Q_INVOKABLE void requestInbox();
    Q_INVOKABLE void refresh();
Q_SIGNALS:
    void changed();
    void openRequested(const QVariantMap &run, const QString &kind);
    void inboxRequested();
    /// Buffered live offers become eligible for the owner's normal queue only
    /// after this connection's verified database has received their journal work.
    void promptReady(const QVariantMap &run, const QString &kind);
    /// Emitted synchronously on application quit so the editor can capture its last keystroke.
    void flushRequested();
    /// The old scope remains active for synchronous capture of the open editor.
    void scopeChanging();
    void scopeChanged();
private:
    struct Edit { QString id; QVariantMap fields; QString clearToken; bool clearDraft = false; };
    struct Scope { QHash<QString, QVariantMap> entries; QVector<Edit> edits; QString journalError; };
    bool persist(const Edit &edit);
    bool flushJournal();
    void flushOnQuit();
    void adoptProvisionalOffers();
    bool readJournal(QHash<QString, QVariantMap> &entries);
    void updateError(const QString &message);
    void updateJournalError(const QString &message);
    void fetchPage(quint64 generation, int page, QHash<QString, QVariantMap> collected);
    QVariantMap mergedRun(const QString &id) const;
    void publishOpen(const QVariantMap &run, bool unavailable);
    QPointer<IBackend> m_backend;
    QPointer<AppSettings> m_settings;
    QHash<QString, QVariantMap> m_entries;
    QHash<QString, QVariantMap> m_pending;
    QVector<Edit> m_edits;
    QHash<QString, Scope> m_scopes;
    /// Live events received before GetStatus verifies this connection's database.
    QHash<QString, QVariantMap> m_provisionalOffers;
    QString m_databaseKey;
    QString m_journalPath;
    QString m_error;
    QString m_journalError;
    bool m_busy = false;
    bool m_refreshAgain = false;
    bool m_identityVerified = false;
    quint64 m_generation = 0;
    quint64 m_scopeGeneration = 0;
    quint64 m_openGeneration = 0;
};
} // namespace mr
