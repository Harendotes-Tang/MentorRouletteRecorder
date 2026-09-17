#include "ExportController.h"

#include "Formatters.h"
#include "IBackend.h"

#include <QDateTime>
#include <QDesktopServices>
#include <QDir>
#include <QFileDialog>
#include <QFileInfo>
#include <QStandardPaths>
#include <QUrl>

namespace {

/// A human size for a backup or an export.
QString byteSize(double bytes)
{
    if (bytes < 1024)
        return QStringLiteral("%1 B").arg(qint64(bytes));
    if (bytes < 1024.0 * 1024.0)
        return QStringLiteral("%1 KB").arg(bytes / 1024.0, 0, 'f', 1);
    return QStringLiteral("%1 MB").arg(bytes / (1024.0 * 1024.0), 0, 'f', 2);
}

QString stamp(const QString &format)
{
    return QDateTime::currentDateTime().toString(format);
}

} // namespace

namespace mr {

ExportController::ExportController(QObject *parent) : QObject(parent) {}

void ExportController::setBackend(IBackend *backend)
{
    m_backend = backend;
}

void ExportController::setTargetOverride(const QString &directory)
{
    m_targetOverride = directory;
}

QString ExportController::chooseExportPath(const QString &suggestedName,
                                           const QString &filter)
{
    // The Collector refuses any target outside the user's own profile
    // (ERR_EXPORT_FAILED), so the dialog starts in 文档 and the non-interactive
    // fallback stays there too.
    const QString documents =
        QStandardPaths::writableLocation(QStandardPaths::DocumentsLocation);

    if (!m_targetOverride.isEmpty()) {
        QDir().mkpath(m_targetOverride);
        return QDir(m_targetOverride).absoluteFilePath(suggestedName);
    }

    const QString suggested = QDir(documents).absoluteFilePath(suggestedName);
    return QFileDialog::getSaveFileName(nullptr, QString::fromUtf8("选择保存位置"),
                                        suggested, filter);
}

void ExportController::exportCsv()
{
    if (!m_backend)
        return;
    const QString path = chooseExportPath(
        QStringLiteral("mentor_runs_") + stamp(QStringLiteral("yyyy-MM-dd"))
            + QStringLiteral(".csv"),
        QString::fromUtf8("CSV (*.csv)"));
    if (path.isEmpty())
        return;

    m_backend->exportCsv(path, m_historyFilter)
        ->whenDone(this, [this](bool ok, const QVariantMap &payload, const QString &code,
                                const QString &message) {
            if (!ok) {
                Q_EMIT toastRequested(QString::fromUtf8("导出失败：")
                                      + (message.isEmpty() ? code : message));
                return;
            }
            Q_EMIT toastRequested(
                QString::fromUtf8("已导出 %1 · %2 行 · %3")
                    .arg(QDir::toNativeSeparators(
                             payload.value(QStringLiteral("target_path")).toString()))
                    .arg(payload.value(QStringLiteral("row_count")).toInt())
                    .arg(byteSize(payload.value(QStringLiteral("byte_count")).toDouble())));
        });
}

void ExportController::exportJson()
{
    if (!m_backend)
        return;
    const QString path = chooseExportPath(
        QStringLiteral("mentor_runs_") + stamp(QStringLiteral("yyyy-MM-dd"))
            + QStringLiteral(".json"),
        QString::fromUtf8("JSON (*.json)"));
    if (path.isEmpty())
        return;

    m_backend->exportJson(path, m_historyFilter)
        ->whenDone(this, [this](bool ok, const QVariantMap &payload, const QString &code,
                                const QString &message) {
            if (!ok) {
                Q_EMIT toastRequested(QString::fromUtf8("导出失败：")
                                      + (message.isEmpty() ? code : message));
                return;
            }
            Q_EMIT toastRequested(
                QString::fromUtf8("已导出 %1 · %2 条 · %3")
                    .arg(QDir::toNativeSeparators(
                             payload.value(QStringLiteral("target_path")).toString()))
                    .arg(payload.value(QStringLiteral("row_count")).toInt())
                    .arg(byteSize(payload.value(QStringLiteral("byte_count")).toDouble())));
        });
}

void ExportController::backupDatabase()
{
    if (!m_backend)
        return;
    // No target_path: that selects the Collector's managed backups folder next
    // to the database, which is also the folder it prunes to 14 copies. A path
    // of our own would defeat both.
    m_backend->backupDatabase()->whenDone(
        this, [this](bool ok, const QVariantMap &payload, const QString &code,
                     const QString &message) {
            if (!ok) {
                Q_EMIT toastRequested(QString::fromUtf8("备份失败：")
                                      + (message.isEmpty() ? code : message));
                return;
            }
            if (!payload.value(QStringLiteral("integrity_check_passed")).toBool()) {
                Q_EMIT toastRequested(QString::fromUtf8("备份失败：完整性校验未通过。"));
                return;
            }
            Q_EMIT backupSucceeded();
            Q_EMIT toastRequested(
                QString::fromUtf8("已备份 → %1 · %2 · 完整性校验 %3 · 清理 %4 份")
                    .arg(QDir::toNativeSeparators(
                             payload.value(QStringLiteral("target_path")).toString()),
                         byteSize(payload.value(QStringLiteral("byte_count")).toDouble()),
                         payload.value(QStringLiteral("integrity_check_passed")).toBool()
                             ? QString::fromUtf8("通过")
                             : QString::fromUtf8("未通过"))
                    .arg(payload.value(QStringLiteral("pruned_count")).toInt()));
        });
}

void ExportController::exportDiagnosticsReport()
{
    // The Collector writes the report itself, through the ExportDiagnosticsReport
    // message, so the counters and the parser errors are those of the process
    // that has been capturing.
    if (!m_backend) {
        Q_EMIT toastRequested(QString::fromUtf8("未连接 Collector，无法生成诊断报告。"));
        return;
    }

    const QString target = chooseExportPath(
        QStringLiteral("mentor_diagnostics_")
            + stamp(QStringLiteral("yyyy-MM-dd_HHmmss")) + QStringLiteral(".json"),
        QString::fromUtf8("脱敏诊断报告 (*.json)"));

    // Empty means the user closed the dialog. The contract would read an
    // omitted target_path as "the managed folder", which is not what somebody
    // who just pressed 取消 asked for.
    if (target.isEmpty())
        return;

    m_backend->exportDiagnosticsReport(target)
        ->whenDone(this, [this](bool ok, const QVariantMap &payload, const QString &code,
                                const QString &message) {
            if (!ok) {
                Q_EMIT toastRequested(message.isEmpty()
                                          ? QString::fromUtf8("诊断报告导出失败：") + code
                                          : message);
                return;
            }
            Q_EMIT toastRequested(
                QString::fromUtf8("已导出脱敏诊断报告 → %1（%2，不含 payload / IP / 路径）")
                    .arg(QDir::toNativeSeparators(
                             payload.value(QStringLiteral("target_path")).toString()),
                         byteSize(double(
                             payload.value(QStringLiteral("byte_count")).toLongLong()))));
        });
}

QVariantMap ExportController::describeIntegrityResult(bool ok, const QVariantMap &payload,
                                                      const QString &errorCode,
                                                      const QString &errorMessage)
{
    QVariantMap result;
    if (!ok) {
        // A Collector older than the message answers ERR_UNKNOWN_MESSAGE; the
        // mock and some builds say ERR_BAD_REQUEST / ERR_UNSUPPORTED for the
        // same thing. That is a missing feature, not a damaged database.
        const bool unsupported = errorCode == QLatin1String("ERR_UNKNOWN_MESSAGE")
                                 || errorCode == QLatin1String("ERR_UNSUPPORTED")
                                 || errorCode == QLatin1String("ERR_BAD_REQUEST");
        result.insert(QStringLiteral("state"),
                      unsupported ? QStringLiteral("unsupported") : QStringLiteral("error"));
        result.insert(QStringLiteral("error"), true);
        result.insert(QStringLiteral("text"),
                      unsupported
                          ? QString::fromUtf8("当前采集器不支持完整性校验")
                          : QString::fromUtf8("完整性校验没有完成：%1")
                                .arg(errorMessage.isEmpty() ? errorCode : errorMessage));
        return result;
    }

    const bool passed = payload.value(QStringLiteral("passed")).toBool();
    const QString detail = payload.value(QStringLiteral("detail")).toString();
    const QString checkedAt = payload.value(QStringLiteral("checked_at_utc")).toString();
    result.insert(QStringLiteral("state"),
                  passed ? QStringLiteral("passed") : QStringLiteral("failed"));
    result.insert(QStringLiteral("passed"), passed);
    result.insert(QStringLiteral("detail"), detail);
    result.insert(QStringLiteral("checked_at_utc"), checkedAt);
    result.insert(QStringLiteral("error"), !passed);
    result.insert(QStringLiteral("text"),
                  passed ? QString::fromUtf8("完整性校验通过 · %1")
                               .arg(Formatters::localTime(checkedAt))
                         : QString::fromUtf8("完整性校验未通过：%1")
                               .arg(detail.isEmpty() ? QString::fromUtf8("未说明原因")
                                                     : detail));
    return result;
}

void ExportController::checkDatabaseIntegrity()
{
    if (!m_backend || m_integrityRunning)
        return;
    m_integrityRunning = true;
    Q_EMIT integrityCheckChanged();

    // whenDone() also covers a request that failed before it was sent (pipe
    // down): the button must come back either way.
    m_backend->checkDatabaseIntegrity()->whenDone(
        this, [this](bool ok, const QVariantMap &payload, const QString &code,
                     const QString &message) {
            m_integrityRunning = false;
            m_integrityResult = describeIntegrityResult(ok, payload, code, message);
            Q_EMIT integrityCheckChanged();
        });
}

void ExportController::openDatabaseFolder(const QString &databasePath)
{
    QString folder = QFileInfo(databasePath).absolutePath();
    if (databasePath.isEmpty() || !QDir(folder).exists()) {
        folder = QDir(QStandardPaths::writableLocation(QStandardPaths::GenericDataLocation))
                     .absoluteFilePath(QStringLiteral("MentorRecorder"));
        QDir().mkpath(folder);
    }
    // A shell hand-off to a local folder, never to a URL derived from backend
    // data. AppController::openCaptureValidationFolder() is the other one.
    QDesktopServices::openUrl(QUrl::fromLocalFile(folder));
    Q_EMIT toastRequested(QString::fromUtf8("已打开 ")
                          + QDir::toNativeSeparators(folder));
}

} // namespace mr
