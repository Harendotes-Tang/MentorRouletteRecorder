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
#include <QStringList>

#include <functional>

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
    /// Test seam: stands in for the save dialog, which has confirmed replacing whatever path
    /// it returns (\a suggestedPath, \a filter as the dialog gets them; empty is 取消).
    void setSaveDialogForTesting(std::function<QString(const QString &suggestedPath, const QString &filter)> dialog)
    {
        m_saveDialog = std::move(dialog);
    }

    /// The filter QueryRuns is currently showing, so an export covers exactly
    /// the rows the user is looking at.
    void setHistoryFilter(const QJsonObject &filter) { m_historyFilter = filter; }
    void setCheckedRunIds(const QStringList &ids) { m_checkedRunIds = ids; }
    /// 勾选时只按精确 ID 导出，包括明确选中的回收站记录。
    QJsonObject effectiveHistoryFilter() const;

    void exportCsv();
    void exportJson();
    /// 立即备份 and the daily backup. Ignored while one runs: each would write another copy
    /// and push the oldest kept one out of the managed folder (audit 2026-11-03 K1-8).
    void backupDatabase();
    bool backupRunning() const { return m_backupRunning; }
    void exportDiagnosticsReport();
    /// Open the folder holding the database in the system file manager.
    /// \a databasePath is GetStatus.database_path; empty falls back to the
    /// per-user data directory.
    void openDatabaseFolder(const QString &databasePath);
    /// Open the Collector's `logs` folder (collector-*.log) under the data
    /// directory; the 新区服采证 file list points a player there.
    void openLogsFolder();

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
    void backupRunningChanged();
    void integrityCheckChanged();

private:
    /// The path to write, and in \a confirmedReplace whether the user already agreed to replace a
    /// file there: the save dialog asks about an existing one; the --export-target folder never did.
    QString chooseExportPath(const QString &suggestedName, const QString &filter, bool *confirmedReplace);

    std::function<QString(const QString &, const QString &)> m_saveDialog;
    bool m_backupRunning = false;

    IBackend *m_backend = nullptr;
    QJsonObject m_historyFilter;
    QStringList m_checkedRunIds;
    QString m_targetOverride;
    bool m_integrityRunning = false;
    QVariantMap m_integrityResult;
    /// The answer m_integrityResult was described from, so a language switch can
    /// describe it again; empty until a check has answered.
    std::function<QVariantMap()> m_describeIntegrity;
};

} // namespace mr
