#pragma once

#include <QObject>
#include <QPointer>
#include <QQmlEngine>
#include <QUrl>
#include <QVariantMap>
#include <functional>

namespace mr {
class IBackend;

/// 只读生成公开摘要；只有 copyAndOpen() 会修改剪贴板、打开固定 GitHub 表单。
/// 请求代次绑定当前候选及后端连接，关闭预览、切换候选或失联均废弃迟到响应。
class EvidenceSubmissionController final : public QObject
{
    Q_OBJECT
    QML_ANONYMOUS
    Q_PROPERTY(bool visible READ visible NOTIFY changed)
    Q_PROPERTY(bool busy READ busy NOTIFY changed)
    Q_PROPERTY(bool canSubmit READ canSubmit NOTIFY changed)
    Q_PROPERTY(QString evidenceJson READ evidenceJson NOTIFY changed)
    Q_PROPERTY(QString kind READ kind NOTIFY changed)
    Q_PROPERTY(QString reason READ reason NOTIFY changed)
    Q_PROPERTY(bool evidenceInUrl READ evidenceInUrl NOTIFY changed)
    Q_PROPERTY(QVariantList candidateChoices READ candidateChoices NOTIFY changed)
    Q_PROPERTY(QString selectedCode READ selectedCode NOTIFY changed)
public:
    using UrlOpener = std::function<bool(const QUrl &)>;
    using ClipboardWriter = std::function<void(const QString &)>;
    explicit EvidenceSubmissionController(IBackend *backend, QObject *parent = nullptr);
    void setUrlOpener(UrlOpener opener);
    void setClipboardWriter(ClipboardWriter writer);
    bool visible() const { return m_visible; }
    bool busy() const { return m_busy; }
    bool canSubmit() const;
    QString evidenceJson() const { return m_json; }
    QString kind() const { return m_kind; }
    QString reason() const;
    bool evidenceInUrl() const { return m_inUrl; }
    QVariantList candidateChoices() const;
    QString selectedCode() const { return m_selectedCode; }
    /// 固定仓库、固定模板、无额外查询字段；预填时必须携带逐字一致的完整摘要。
    static bool isSubmissionUrl(const QUrl &url, const QString &kind,
                                const QString &json, bool inUrl);
public Q_SLOTS:
    void previewCalibration(const QString &codeSha256 = {});
    void previewTemplateObservation();
    void dismiss();
    void copyAndOpen();
    void invalidate();
    void refreshFromCaptureStatus(const QVariantMap &capture);
    void refreshFromValidationStatus(const QVariantMap &status);
Q_SIGNALS:
    void changed();
    void notice(const QString &message);
private:
    void preview(const QString &kind, const QString &codeSha256 = {});
    void clearResult();
    QPointer<IBackend> m_backend;
    UrlOpener m_openUrl;
    ClipboardWriter m_copy;
    quint64 m_generation = 0;
    QByteArray m_captureIdentity;
    QByteArray m_validationIdentity;
    QString m_json;
    QString m_kind;
    QString m_reason;
    QVariantList m_candidates;
    QString m_selectedCode;
    QUrl m_url;
    bool m_visible = false;
    bool m_busy = false;
    bool m_inUrl = false;
};
} // namespace mr
