#pragma once

#include <QJsonArray>
#include <QJsonObject>
#include <QObject>
#include <QPointer>
#include <QQmlEngine>
#include <QString>
#include <QSet>
#include <QTemporaryDir>
#include <QVariantList>
#include <QVariantMap>

#include <memory>

namespace mr {
class IBackend;
class OfflineOcrEngine;

/// Desktop input/evidence and editable preview of Collector-owned import work.
/// OCR creates candidates only. Every edit invalidates commit eligibility until
/// the Collector validates it again; callbacks from a cancelled session are ignored.
class ImportRecordsController final : public QObject
{
    Q_OBJECT
    QML_ANONYMOUS
    Q_PROPERTY(bool busy READ busy NOTIFY changed)
    Q_PROPERTY(bool committing READ committing NOTIFY changed)
    Q_PROPERTY(bool pendingCommitConfirmation READ pendingCommitConfirmation NOTIFY changed)
    Q_PROPERTY(QString phase READ phase NOTIFY changed)
    Q_PROPERTY(QString statusText READ statusText NOTIFY changed)
    Q_PROPERTY(QString errorText READ errorText NOTIFY changed)
    Q_PROPERTY(QString sourceLabel READ sourceLabel NOTIFY changed)
    Q_PROPERTY(QString sourceKind READ sourceKind NOTIFY changed)
    Q_PROPERTY(int sourceImageCount READ sourceImageCount NOTIFY changed)
    Q_PROPERTY(QVariantList rows READ rows NOTIFY changed)
    Q_PROPERTY(QVariantMap summary READ summary NOTIFY changed)
    Q_PROPERTY(bool previewValid READ previewValid NOTIFY changed)
    Q_PROPERTY(bool ownRecordsConfirmed READ ownRecordsConfirmed WRITE setOwnRecordsConfirmed NOTIFY changed)
    Q_PROPERTY(int selectedCount READ selectedCount NOTIFY changed)
    Q_PROPERTY(bool canCommit READ canCommit NOTIFY changed)
    Q_PROPERTY(int currentRow READ currentRow WRITE setCurrentRow NOTIFY changed)
    Q_PROPERTY(QVariantMap currentCandidate READ currentCandidate NOTIFY changed)
    Q_PROPERTY(QVariantMap currentEvidence READ currentEvidence NOTIFY changed)
    Q_PROPERTY(QString timeZone READ timeZone WRITE setTimeZone NOTIFY changed)
    Q_PROPERTY(QVariantMap columnMapping READ columnMapping WRITE setColumnMapping NOTIFY changed)
    Q_PROPERTY(QVariantMap commitResult READ commitResult NOTIFY changed)
    Q_PROPERTY(QVariantList jobChoices READ jobChoices CONSTANT)

public:
    explicit ImportRecordsController(QObject *parent = nullptr);
    ~ImportRecordsController() override;
    void setBackend(IBackend *backend);

    bool busy() const;
    bool committing() const { return m_phase == QLatin1String("commit"); }
    bool pendingCommitConfirmation() const { return !m_pendingCommitPayload.isEmpty(); }
    QString phase() const { return m_phase; }
    QString statusText() const;
    QString errorText() const { return m_error; }
    QString sourceLabel() const { return m_sourceName; }
    QString sourceKind() const { return m_sourceKind; }
    int sourceImageCount() const { return m_sourceImages.size(); }
    QVariantList rows() const { return m_rows; }
    QVariantMap summary() const { return m_summary; }
    bool previewValid() const { return !m_previewId.isEmpty(); }
    bool ownRecordsConfirmed() const { return m_ownConfirmed; }
    void setOwnRecordsConfirmed(bool confirmed);
    int selectedCount() const { return m_selected.size(); }
    bool canCommit() const { return !busy() && previewValid() && m_ownConfirmed && !m_selected.isEmpty(); }
    int currentRow() const { return m_current; }
    void setCurrentRow(int row);
    /// 当前编辑候选；类别可由 preview.run 补充显示，原始 wire 字段保持独立。
    QVariantMap currentCandidate() const;
    QVariantMap currentEvidence() const;
    QString timeZone() const { return m_timeZone; }
    void setTimeZone(const QString &zone);
    QVariantMap columnMapping() const { return m_columnMapping; }
    void setColumnMapping(const QVariantMap &mapping);
    QVariantMap commitResult() const { return m_commitResult; }
    QVariantList jobChoices() const;

    Q_INVOKABLE void reset();
    Q_INVOKABLE void chooseFiles();
    Q_INVOKABLE void importFiles(const QVariantList &pathsOrUrls);
    Q_INVOKABLE void pasteClipboard();
    Q_INVOKABLE void importText(const QString &text);
    Q_INVOKABLE void revalidate();
    Q_INVOKABLE void updateCandidate(int row, const QVariantMap &changes);
    /// 修改调用前已勾选的记录；只接受职业、结果和打本心情，并校验一次。
    Q_INVOKABLE void batchUpdateSelected(const QVariantMap &changes);
    Q_INVOKABLE void setRowSelected(int row, bool selected);
    Q_INVOKABLE void setAllImportableSelected(bool selected);
    Q_INVOKABLE void commit();
    /// Stops owned OCR or invalidates read-only requests. An active commit is
    /// awaited because cancelling its UI cannot undo a Collector transaction.
    Q_INVOKABLE void cancel();

    /// Test seam for a packaged OCR layout in an isolated directory.
    void setOcrApplicationDirectoryForTesting(const QString &directory);
    static QString sourceKindForPath(const QString &path);
    static QString localPath(const QVariant &pathOrUrl);

Q_SIGNALS:
    void changed();
    void imported(const QVariantMap &result);

private:
    void beginInput(const QString &kind, const QString &name);
    void invalidatePreview(bool rememberSelection = true);
    void acceptPreview(const QVariantMap &payload);
    void discardAppend(const QString &message);
    QString screenshotSourceName(const QStringList &paths) const;
    void fail(const QString &message);
    void recognizeImage(const QString &path, const QByteArray &tsv);
    QJsonObject previewRequest() const;

    QPointer<IBackend> m_backend;
    OfflineOcrEngine *m_ocr;
    std::unique_ptr<QTemporaryDir> m_clipboardFiles;
    QJsonArray m_candidates;
    QVariantList m_evidence;
    QVariantList m_rows;
    QVariantMap m_summary;
    QVariantMap m_columnMapping;
    QVariantMap m_commitResult;
    QJsonObject m_pendingCommitPayload;
    QSet<int> m_selected;
    QSet<int> m_restoreSelection;
    QStringList m_sourceImages;
    // 新截图在 OCR 和 Collector 校验全部成功前不替换现有批次。
    QStringList m_appendImages;
    QJsonArray m_appendCandidates;
    QVariantList m_appendEvidence;
    QString m_appendPreviousPhase;
    QString m_sourceKind;
    QString m_sourceName;
    QString m_filePath;
    QString m_text;
    QString m_timeZone = QStringLiteral("+08:00");
    QString m_previewId;
    QString m_commitRequestId;
    QString m_phase = QStringLiteral("empty");
    QString m_error;
    int m_current = -1;
    quint64 m_generation = 0;
    bool m_ownConfirmed = false;
    bool m_hasAcceptedPreview = false;
    bool m_batchCommitted = false;
};

} // namespace mr
