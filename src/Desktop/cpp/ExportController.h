#pragma once

// ---------------------------------------------------------------------------
// CSV / JSON export, database backup and the sanitized diagnostics report.
//
// The Collector does every write; this object only picks the target path and
// reports what came back - including when nothing came back at all, which is
// what a synchronous pipe failure looks like.
// ---------------------------------------------------------------------------

#include <QJsonObject>
#include <QObject>
#include <QString>
#include <QVariantMap>

namespace mr {

class IBackend;

class ExportController : public QObject
{
    Q_OBJECT

public:
    explicit ExportController(QObject *parent = nullptr);

    void setBackend(IBackend *backend);

    /// Where the next export goes when the interactive file chooser is
    /// suppressed (--export-target), so a test or screenshot run never blocks
    /// on a modal dialog.
    void setTargetOverride(const QString &directory);
    QString targetOverride() const { return m_targetOverride; }

    /// The filter QueryRuns is currently showing, so an export covers exactly
    /// the rows the user is looking at.
    void setHistoryFilter(const QJsonObject &filter) { m_historyFilter = filter; }

    void exportCsv();
    void exportJson();
    void backupDatabase();
    void exportDiagnosticsReport();
    /// Open the folder holding the database in the system file manager.
    /// \a databasePath is GetStatus.database_path; empty falls back to the
    /// per-user data directory.
    void openDatabaseFolder(const QString &databasePath);

    /// 完整性校验: CheckDatabaseIntegrity. Ignored while one is running.
    void checkDatabaseIntegrity();
    bool integrityCheckRunning() const { return m_integrityRunning; }
    /// The last outcome, empty before the first check:
    ///   state          "passed" | "failed" | "unsupported" | "error"
    ///   passed, detail, checked_at_utc   (from the reply, when there was one)
    ///   text           the line the settings page shows
    ///   error          true when that line is a problem (red / orange)
    QVariantMap integrityCheckResult() const { return m_integrityResult; }

    /// The settings-page sentence for one CheckDatabaseIntegrity outcome.
    /// Pure, so the wording is pinned without a backend.
    static QVariantMap describeIntegrityResult(bool ok, const QVariantMap &payload,
                                               const QString &errorCode,
                                               const QString &errorMessage);

Q_SIGNALS:
    void toastRequested(const QString &message);
    /// A BackupDatabase request completed and the Collector confirmed the copy.
    /// The daily auto-backup stamps its date from this, never from the attempt.
    void backupSucceeded();
    void integrityCheckChanged();

private:
    QString chooseExportPath(const QString &suggestedName, const QString &filter);

    IBackend *m_backend = nullptr;
    QJsonObject m_historyFilter;
    QString m_targetOverride;
    bool m_integrityRunning = false;
    QVariantMap m_integrityResult;
};

} // namespace mr
