#include "EvidenceSubmissionController.h"
#include "IBackend.h"
#include "LiveText.h"

#include <QClipboard>
#include <QDesktopServices>
#include <QGuiApplication>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QRegularExpression>
#include <QUrlQuery>
#include <utility>

namespace mr {
namespace {
constexpr auto codeKind = "CODE_VERIFICATION";
constexpr auto templateKind = "TEMPLATE_OBSERVATION";
QString formFor(const QString &kind)
{
    return kind == QLatin1String(codeKind) ? QStringLiteral("evidence-calibration.yml")
         : kind == QLatin1String(templateKind) ? QStringLiteral("template-observation.yml") : QString();
}
}

EvidenceSubmissionController::EvidenceSubmissionController(IBackend *backend, QObject *parent)
    : QObject(parent), m_backend(backend)
    , m_openUrl([](const QUrl &url) { return QDesktopServices::openUrl(url); })
    , m_copy([](const QString &json) {
        if (auto *clipboard = QGuiApplication::clipboard()) clipboard->setText(json);
    })
{
    if (backend) {
        connect(backend, &IBackend::connectionChanged, this, &EvidenceSubmissionController::invalidate);
        connect(backend, &QObject::destroyed, this, &EvidenceSubmissionController::invalidate);
    }
    onLanguageChanged(this, [this] { Q_EMIT changed(); });
}

void EvidenceSubmissionController::setUrlOpener(UrlOpener opener)
{
    if (opener) m_openUrl = std::move(opener);
}
void EvidenceSubmissionController::setClipboardWriter(ClipboardWriter writer)
{
    if (writer) m_copy = std::move(writer);
}

bool EvidenceSubmissionController::isSubmissionUrl(const QUrl &url, const QString &kind,
                                                  const QString &json, bool inUrl)
{
    if (!url.isValid() || url.scheme() != QLatin1String("https")
        || url.host() != QLatin1String("github.com") || !url.userInfo().isEmpty()
        || url.port(-1) != -1 || !url.fragment().isEmpty()
        || url.path() != QLatin1String("/Harendotes-Tang/MentorRecorder-Calibrations/issues/new")
        || formFor(kind).isEmpty()) return false;
    const auto items = QUrlQuery(url).queryItems(QUrl::FullyDecoded);
    bool templateSeen = false;
    bool evidenceSeen = false;
    for (const auto &[key, value] : items) {
        if (key == QLatin1String("template") && !templateSeen && value == formFor(kind))
            templateSeen = true;
        else if (key == QLatin1String("evidence") && !evidenceSeen && inUrl && value == json)
            evidenceSeen = true;
        else return false;
    }
    return templateSeen && evidenceSeen == inUrl;
}

bool EvidenceSubmissionController::canSubmit() const
{
    return m_visible && !m_busy && m_backend && m_backend->isConnected() && !m_json.isEmpty()
        && isSubmissionUrl(m_url, m_kind, m_json, m_inUrl);
}

QString EvidenceSubmissionController::reason() const
{
    if (m_reason.isEmpty()) return {};
    if (m_reason == QLatin1String("DISCONNECTED")) return tr("后台服务未连接，请连接后重新预览。");
    if (m_reason == QLatin1String("STALE")) return tr("区服、版本或校准候选已变化，请重新预览当前摘要。");
    if (m_reason == QLatin1String("ERR_UNKNOWN_MESSAGE")) return tr("当前后台服务尚不支持证据摘要，请更新并重启软件。");
    if (m_reason == QLatin1String("INVALID_RESPONSE")) return tr("后台返回的摘要或提交地址无效，未启用提交。请重新预览。");
    if (m_reason == QLatin1String("INVALID_CANDIDATE")) return tr("校准候选标识无效，请重新选择候选。");
    if (m_reason == QLatin1String("NO_CURRENT_GAME_BUILD")) return tr("尚未确认区服和游戏版本，请先启动并选择游戏窗口。");
    if (m_reason == QLatin1String("PIPELINE_UNAVAILABLE")) return tr("当前尚无可用的协议观测，请检查捕获诊断中的监听条件。");
    if (m_reason == QLatin1String("EVIDENCE_STORE_UNAVAILABLE")) return tr("本机证据存储当前不可用，请检查本地存储状态后重试。");
    if (m_reason == QLatin1String("NO_CURRENT_CANDIDATE")) return tr("尚无可核实的校准候选，请先检查或导入共享校准。");
    if (m_reason == QLatin1String("SELECT_CANDIDATE")) return m_candidates.isEmpty()
        ? tr("存在多个候选，但当前后台未提供完整候选标识。请更新并重启后台服务后重试。")
        : tr("存在多个候选，请选择本次要提交证据的校准候选。");
    if (m_reason == QLatin1String("CANDIDATE_NOT_OBSERVED")) return tr("所选候选尚未登记观测，请重新预览当前候选。");
    if (m_reason == QLatin1String("NO_COMPLETED_HEALTHY_SESSION")) return tr("尚无登记候选后完成的健康会话。请回到标题画面重新登录，完成本机核实后再预览。");
    if (m_reason == QLatin1String("NO_OBSERVATION")) return tr("尚无已完成的元数据采证，请先按采证步骤开始、标记并停止。");
    if (m_reason == QLatin1String("OBSERVATION_ACTIVE")) return tr("采证仍在进行或排空中，请停止并等待保存完成后再预览。");
    if (m_reason == QLatin1String("OBSERVATION_FAILED")) return tr("本次采证未成功完成，请查看采证面板的具体错误后重新采集。");
    if (m_reason == QLatin1String("OBSERVATION_CHANGED")) return tr("采证会话已变化，请重新预览最新候选摘要。");
    if (m_reason == QLatin1String("OBSERVATION_INCOMPLETE")) return tr("本次采证不完整或发生截断，不能作为公开候选摘要，请重新采集。");
    if (m_reason == QLatin1String("OBSERVATION_NO_EVENTS")) return tr("本次尚无可关联的报文元数据和操作标记，请按步骤重新采集。");
    if (m_reason == QLatin1String("OBSERVATION_IDENTITY_UNKNOWN")) return tr("尚未准确识别区服和游戏版本，请确认游戏窗口及区服后重新采证。");
    if (m_reason == QLatin1String("OBSERVATION_LIMIT")) return tr("本次采证超过摘要处理上限，请缩短采证范围后重试。");
    if (m_reason == QLatin1String("OBSERVATION_HASH_MISMATCH") || m_reason == QLatin1String("OBSERVATION_INVALID")
        || m_reason == QLatin1String("OBSERVATION_UNREADABLE")) return tr("本机采证文件无法通过完整性检查或读取，请重新采证。");
    if (m_reason == QLatin1String("CONTROLLER_DISPOSED")) return tr("采证服务已停止，请重启软件后重试。");
    return tr("暂时无法生成公开摘要：%1").arg(m_reason);
}

QVariantList EvidenceSubmissionController::candidateChoices() const
{
    QVariantList choices;
    for (const auto &candidate : m_candidates) {
        auto row = candidate.toMap();
        row.insert(QStringLiteral("label"), tr("候选 %1 · %2").arg(choices.size() + 1)
                                                .arg(row.value(QStringLiteral("sha12")).toString()));
        choices.append(row);
    }
    return choices;
}

void EvidenceSubmissionController::clearResult()
{
    m_json.clear();
    m_url.clear();
    m_inUrl = false;
}

void EvidenceSubmissionController::invalidate()
{
    ++m_generation;
    clearResult();
    m_busy = false;
    if (m_visible) m_reason = m_backend && m_backend->isConnected() ? QStringLiteral("STALE")
                                                                            : QStringLiteral("DISCONNECTED");
    Q_EMIT changed();
}

void EvidenceSubmissionController::refreshFromCaptureStatus(const QVariantMap &capture)
{
    const auto calibration = capture.value(QStringLiteral("calibration")).toMap();
    QJsonArray candidates;
    QVariantList choices;
    for (const auto &item : calibration.value(QStringLiteral("shared")).toMap()
                                 .value(QStringLiteral("candidates")).toList()) {
        const auto row = item.toMap();
        candidates.append(QJsonObject{{QStringLiteral("sha12"), row.value(QStringLiteral("sha12")).toString()},
                                      {QStringLiteral("code_sha256"), row.value(QStringLiteral("code_sha256")).toString()}});
        const auto hash = row.value(QStringLiteral("code_sha256")).toString();
        static const QRegularExpression fullHash(QStringLiteral("^[0-9a-f]{64}$"));
        if (fullHash.match(hash).hasMatch()) choices.append(QVariantMap{
            {QStringLiteral("sha12"), hash.first(12)}, {QStringLiteral("code_sha256"), hash}});
    }
    const QByteArray identity = QJsonDocument(QJsonObject{
        {QStringLiteral("region"), capture.value(QStringLiteral("region")).toString()},
        {QStringLiteral("game_build"), capture.value(QStringLiteral("game_build")).toString()},
        {QStringLiteral("template_profile_id"), calibration.value(QStringLiteral("template_profile_id")).toString()},
        {QStringLiteral("local_profile_id"), calibration.value(QStringLiteral("local_profile_id")).toString()},
        {QStringLiteral("shared_profile_id"), calibration.value(QStringLiteral("shared")).toMap().value(QStringLiteral("profile_id")).toString()},
        {QStringLiteral("candidates"), candidates}}).toJson(QJsonDocument::Compact);
    if (identity == m_captureIdentity) return;
    const bool hadIdentity = !m_captureIdentity.isEmpty();
    m_captureIdentity = identity;
    m_candidates = choices;
    if (hadIdentity || m_visible) invalidate();
    else Q_EMIT changed();
}

void EvidenceSubmissionController::previewCalibration(const QString &codeSha256)
{
    preview(QString::fromLatin1(codeKind), codeSha256);
}
void EvidenceSubmissionController::refreshFromValidationStatus(const QVariantMap &status)
{
    const QByteArray identity = QJsonDocument(QJsonObject{
        {QStringLiteral("state"), status.value(QStringLiteral("state")).toString()},
        {QStringLiteral("trace_path"), status.value(QStringLiteral("trace_path")).toString()},
        {QStringLiteral("sha256"), status.value(QStringLiteral("sha256")).toString()}}).toJson(QJsonDocument::Compact);
    if (identity == m_validationIdentity) return;
    m_validationIdentity = identity;
    if (m_visible && m_kind == QLatin1String(templateKind)) invalidate();
}
void EvidenceSubmissionController::previewTemplateObservation()
{
    preview(QString::fromLatin1(templateKind));
}

void EvidenceSubmissionController::preview(const QString &kind, const QString &codeSha256)
{
    const quint64 generation = ++m_generation;
    m_visible = true;
    m_busy = false;
    clearResult();
    m_reason.clear();
    m_kind = kind;
    m_selectedCode = codeSha256;
    if (!m_backend || !m_backend->isConnected()) {
        m_reason = QStringLiteral("DISCONNECTED");
        Q_EMIT changed();
        return;
    }
    static const QRegularExpression hash(QStringLiteral("^[0-9a-f]{64}$"));
    if (!codeSha256.isEmpty() && !hash.match(codeSha256).hasMatch()) {
        m_reason = QStringLiteral("INVALID_CANDIDATE");
        Q_EMIT changed();
        return;
    }
    m_busy = true;
    Q_EMIT changed();
    QJsonObject request;
    if (!codeSha256.isEmpty()) request.insert(QStringLiteral("code_sha256"), codeSha256);
    auto *reply = m_backend->request(kind == QLatin1String(codeKind) ? QStringLiteral("GetCalibrationEvidence")
                                                                   : QStringLiteral("GetTemplateObservation"), request);
    if (!reply) {
        m_busy = false;
        m_reason = QStringLiteral("INVALID_RESPONSE");
        Q_EMIT changed();
        return;
    }
    reply->whenDone(this, [this, generation, kind, codeSha256](bool ok, const QVariantMap &payload,
                                                             const QString &code, const QString &message) {
        if (generation != m_generation || !m_visible || !m_backend || !m_backend->isConnected()) return;
        m_busy = false;
        if (!ok) {
            m_reason = code == QLatin1String("ERR_UNKNOWN_MESSAGE") ? code : message.isEmpty() ? code : message;
        } else if (!payload.value(QStringLiteral("available")).toBool()) {
            m_reason = payload.value(QStringLiteral("reason")).toString();
            if (m_reason.isEmpty()) m_reason = QStringLiteral("INVALID_RESPONSE");
        } else {
            const QString json = payload.value(QStringLiteral("evidence_json")).toString();
            const QUrl url(payload.value(QStringLiteral("issue_url")).toString(), QUrl::StrictMode);
            const bool inUrl = payload.value(QStringLiteral("evidence_in_url")).toBool();
            QJsonParseError error;
            const QJsonDocument doc = QJsonDocument::fromJson(json.toUtf8(), &error);
            const auto root = doc.object();
            if (json.toUtf8().size() > 48 * 1024 || error.error != QJsonParseError::NoError || !doc.isObject()
                || root.value(QStringLiteral("format")).toString() != QLatin1String("MentorRecorder.CalibrationEvidence")
                || root.value(QStringLiteral("schema_version")).toInt() != 1
                || root.value(QStringLiteral("kind")).toString() != kind
                || payload.value(QStringLiteral("kind")).toString() != kind
                || (!codeSha256.isEmpty() && root.value(QStringLiteral("target")).toObject()
                         .value(QStringLiteral("code_sha256")).toString() != codeSha256)
                || !isSubmissionUrl(url, kind, json, inUrl)) {
                m_reason = QStringLiteral("INVALID_RESPONSE");
            } else {
                m_json = json; // 保留生产者的精确 JSON，不重新格式化、补字段或截断。
                m_url = url;
                m_inUrl = inUrl;
            }
        }
        Q_EMIT changed();
    });
}

void EvidenceSubmissionController::dismiss()
{
    ++m_generation;
    m_visible = false;
    m_busy = false;
    clearResult();
    m_reason.clear();
    Q_EMIT changed();
}

void EvidenceSubmissionController::copyAndOpen()
{
    if (!canSubmit()) return;
    const quint64 generation = m_generation;
    const QUrl url = m_url;
    const QString json = m_json;
    const bool inUrl = m_inUrl;
    const QPointer<EvidenceSubmissionController> guard(this);
    // 再次检查的目标就是当前已预览内容；复制全量摘要，即使预填 URL 未携带摘要。
    m_copy(json);
    // 剪贴板信号可同步进入其他界面逻辑；取消、切换目标或销毁后不再打开旧表单。
    if (!guard || generation != m_generation || !canSubmit()) return;
    if (!m_openUrl(url)) {
        if (!guard) return;
        Q_EMIT notice(tr("完整摘要已复制，但未能打开浏览器。请稍后重试。"));
        return;
    }
    if (!guard) return;
    Q_EMIT notice(inUrl ? tr("已复制摘要并打开 GitHub，请检查后自行提交。")
                         : tr("完整摘要已复制，请在 GitHub 的证据摘要字段粘贴后自行提交。"));
}
} // namespace mr
