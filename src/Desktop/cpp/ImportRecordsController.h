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

#include "LiveText.h"

namespace mr {
class IBackend;
class OfflineOcrEngine;
struct RunImportSource;

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
    Q_PROPERTY(QString templateStatusText READ templateStatusText NOTIFY changed)
    Q_PROPERTY(QString templateErrorText READ templateErrorText NOTIFY changed)
    Q_PROPERTY(QString templateInstructions READ templateInstructions CONSTANT)
    Q_PROPERTY(QString sourceLabel READ sourceLabel NOTIFY changed)
    Q_PROPERTY(QString sourceKind READ sourceKind NOTIFY changed)
    Q_PROPERTY(int sourceImageCount READ sourceImageCount NOTIFY changed)
    Q_PROPERTY(QVariantList rows READ rows NOTIFY changed)
    Q_PROPERTY(QVariantMap summary READ summary NOTIFY changed)
    Q_PROPERTY(bool previewValid READ previewValid NOTIFY changed)
    Q_PROPERTY(bool ownRecordsConfirmed READ ownRecordsConfirmed WRITE setOwnRecordsConfirmed NOTIFY changed)
    Q_PROPERTY(int selectedCount READ selectedCount NOTIFY changed)
    /// Selected screenshot rows whose job is still the icon guess the user has not confirmed.
    Q_PROPERTY(int pendingJobSelectedCount READ pendingJobSelectedCount NOTIFY changed)
    Q_PROPERTY(bool canCommit READ canCommit NOTIFY changed)
    Q_PROPERTY(int currentRow READ currentRow WRITE setCurrentRow NOTIFY changed)
    Q_PROPERTY(QVariantMap currentCandidate READ currentCandidate NOTIFY changed)
    Q_PROPERTY(QVariantMap currentEvidence READ currentEvidence NOTIFY changed)
    Q_PROPERTY(QString timeZone READ timeZone WRITE setTimeZone NOTIFY changed)
    Q_PROPERTY(QVariantMap columnMapping READ columnMapping WRITE setColumnMapping NOTIFY changed)
    Q_PROPERTY(QVariantMap commitResult READ commitResult NOTIFY changed)
    /// Re-read only after a language switch (choicesChanged), so an open combo box
    /// keeps its state while the import is edited.
    Q_PROPERTY(QVariantList jobChoices READ jobChoices NOTIFY choicesChanged)
    Q_PROPERTY(QVariantList dutyChoices READ dutyChoices NOTIFY choicesChanged)
    /// The stored achievement baseline (安装前已完成次数), fed by the owner from the dashboard.
    Q_PROPERTY(int baselineCount READ baselineCount WRITE setBaselineCount NOTIFY changed)
    /// The owner has read the stored baseline on this connection; before that baselineCount is a default.
    Q_PROPERTY(bool baselineLoaded READ baselineLoaded WRITE setBaselineLoaded NOTIFY changed)
    /// Selected rows add to the progress but the baseline is still unread: 起始次数尚未读取.
    Q_PROPERTY(bool waitingForBaseline READ waitingForBaseline NOTIFY changed)
    /// Selected reviewed mentor completions; unknown game times do not prevent contribution.
    Q_PROPERTY(int contributingSelectedCount READ contributingSelectedCount NOTIFY changed)
    /// True when the batch could count twice: a baseline is stored and selected rows add to the progress.
    Q_PROPERTY(bool baselineChoiceOffered READ baselineChoiceOffered NOTIFY changed)
    /// The user's answer to "already counted in the baseline?"; defaults by source kind until chosen.
    Q_PROPERTY(bool deductFromBaseline READ deductFromBaseline WRITE setDeductFromBaseline NOTIFY changed)
    /// How many the Collector would deduct: the contributing rows, capped by the stored baseline.
    Q_PROPERTY(int baselineDeductionPreview READ baselineDeductionPreview NOTIFY changed)

public:
    explicit ImportRecordsController(QObject *parent = nullptr);
    ~ImportRecordsController() override;
    void setBackend(IBackend *backend);

    bool busy() const;
    bool committing() const { return m_phase == QLatin1String("commit"); }
    bool pendingCommitConfirmation() const { return !m_pendingCommitPayload.isEmpty(); }
    QString phase() const { return m_phase; }
    QString statusText() const;
    QString errorText() const { return m_error.text(); }
    QString templateStatusText() const { return m_templateStatus.text(); }
    QString templateErrorText() const { return m_templateError.text(); }
    QString templateInstructions() const;
    /// The source as shown, in the interface language; what travels is m_sourceName.
    QString sourceLabel() const;
    QString sourceKind() const { return m_sourceKind; }
    int sourceImageCount() const { return static_cast<int>(m_sourceImages.size()); }
    QVariantList rows() const { return m_rows; }
    QVariantMap summary() const { return m_summary; }
    bool previewValid() const { return !m_previewId.isEmpty(); }
    bool ownRecordsConfirmed() const { return m_ownConfirmed; }
    void setOwnRecordsConfirmed(bool confirmed);
    int selectedCount() const { return static_cast<int>(m_selected.size()); }
    int pendingJobSelectedCount() const;
    /// A guessed job never travels unconfirmed (owner rule: job icons stay manual confirmation),
    /// and a batch that adds to the progress waits for the stored baseline (audit L2-9).
    bool canCommit() const
    {
        // 重试确认保存 resends what was already asked, so it never waits for the baseline.
        return !busy() && previewValid() && m_ownConfirmed && !m_selected.isEmpty()
            && pendingJobSelectedCount() == 0 && (pendingCommitConfirmation() || !waitingForBaseline());
    }
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
    QVariantList dutyChoices() const;
    int baselineCount() const { return m_baselineCount; }
    void setBaselineCount(int count);
    bool baselineLoaded() const { return m_baselineLoaded; }
    void setBaselineLoaded(bool loaded);
    bool waitingForBaseline() const { return previewValid() && !m_baselineLoaded && contributingSelectedCount() > 0; }
    int contributingSelectedCount() const;
    bool baselineChoiceOffered() const { return previewValid() && m_baselineCount > 0 && contributingSelectedCount() > 0; }
    bool deductFromBaseline() const;
    void setDeductFromBaseline(bool deduct);
    int baselineDeductionPreview() const { return qMin(contributingSelectedCount(), m_baselineCount); }

    Q_INVOKABLE void reset();
    Q_INVOKABLE void chooseFiles();
    /// 另存内嵌 XLSX/JSON 空白模板；取消、成功和失败均不修改当前导入候选。
    Q_INVOKABLE void saveTemplate(const QString &format);
    /// 与交互另存共享的原子写入入口；调用者负责既有文件替换确认，供本地验证使用。
    bool saveTemplateToPath(const QString &format, const QString &path);
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
    /// The interface language changed: jobChoices and dutyChoices have new names.
    void choicesChanged();
    void imported(const QVariantMap &result);

private:
    struct PreviewPages;
    void beginInput(const QString &kind, const QString &name);
    void rememberSelectedIndices();
    void invalidatePreview(bool rememberSelection = true);
    void acceptPreview(const QVariantMap &payload);
    /// Collect one frozen preview before exposing it; generation discards cancelled replies.
    /// Page 1 sends \a source, every later page asks for the frozen preview by id.
    void requestPreviewPage(const RunImportSource &source, quint64 generation, const std::shared_ptr<PreviewPages> &pages);
    /// Puts \c error_texts / \c warning_texts on a preview row: its errors and
    /// warnings in the interface language (error_refs / warning_refs).
    static void renderCollectorTexts(QVariantMap &row);
    /// Row \a index still carries the icon's job guess, not yet confirmed by the user.
    bool jobCandidatePending(int index) const;
    void discardAppend(const LiveText &message);
    void removeImportedRows(const QVariantMap &receipt);
    /// The batch's source name: the zh-CN wording stored as provenance, or with
    /// \a forDisplay the same name in the interface language (audit H2-2).
    QString screenshotSourceName(const QStringList &paths, bool forDisplay = false) const;
    void fail(const LiveText &message);
    void recognizeImage(const QString &path, const QByteArray &tsv);
    RunImportSource previewSource() const;
    QJsonObject previewRequest() const;
    /// The re-check of this batch, with \a candidate in place of row \a row (none when -1) and
    /// \a extraBytes more, is one the Collector accepts and still goes out as one IPC frame
    /// (audit H2-3, RV3-1, RV5-1).
    bool revalidationFits(int row, const QJsonObject &candidate, qint64 extraBytes = 0) const;

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
    // Candidate array indices survive file-row numbers being reassigned after editing.
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
    // Recipes, so a language switch re-renders them (LiveText.h). A sentence that
    // arrived as text - the Collector's, the OCR engine's - is kept verbatim.
    LiveText m_error;
    LiveText m_templateStatus;
    LiveText m_templateError;
    int m_current = -1;
    quint64 m_generation = 0;
    bool m_ownConfirmed = false;
    bool m_hasAcceptedPreview = false;
    bool m_batchCommitted = false;
    int m_baselineCount = 0;
    bool m_baselineLoaded = false;
    // 「这些记录是否已包含在基数中」: the default follows the source kind until the user answers.
    bool m_deductChosen = false;
    bool m_deduct = false;
    // batchUpdateSelected has already checked the size of its whole batch of edits.
    bool m_batchEditing = false;
};

} // namespace mr
