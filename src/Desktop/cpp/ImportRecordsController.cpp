#include "ImportRecordsController.h"

#include "IBackend.h"
#include "DutyCatalog.h"
#include "JobCatalog.h"
#include "OfflineOcrEngine.h"
#include "ScreenshotImportParser.h"

#include <QClipboard>
#include <QDir>
#include <QFileDialog>
#include <QFileInfo>
#include <QGuiApplication>
#include <QImageReader>
#include <QJsonDocument>
#include <QMimeData>
#include <QRegularExpression>
#include <QUrl>
#include <QUuid>

#include <algorithm>

namespace mr {

ImportRecordsController::ImportRecordsController(QObject *parent)
    : QObject(parent), m_ocr(new OfflineOcrEngine(this))
{
    connect(m_ocr, &OfflineOcrEngine::changed, this, &ImportRecordsController::changed);
    connect(m_ocr, &OfflineOcrEngine::imageRecognized, this, &ImportRecordsController::recognizeImage);
    connect(m_ocr, &OfflineOcrEngine::failed, this, [this](const QString &message) { m_error = message; });
    connect(m_ocr, &OfflineOcrEngine::finished, this, [this](bool complete) {
        if (m_phase != QLatin1String("ocr"))
            return;
        m_phase = QStringLiteral("editing");
        if (!m_appendImages.isEmpty()) {
            if (complete && !m_appendCandidates.isEmpty() && m_error.isEmpty())
                revalidate();
            else
                discardAppend(m_error.isEmpty()
                    ? (complete ? tr("新截图没有识别到完整记录，原批次已保留。") : tr("追加识别已取消，原批次已保留。"))
                    : m_error);
        } else if (complete && !m_candidates.isEmpty() && m_error.isEmpty())
            revalidate();
        else if (m_error.isEmpty())
            fail(complete ? tr("没有识别到完整记录卡片。请裁切到记录列表或粘贴表格后重试。")
                          : tr("识别已取消；历史记录没有变化。"));
        Q_EMIT changed();
    });
}

ImportRecordsController::~ImportRecordsController() = default;

bool ImportRecordsController::busy() const
{
    return m_ocr->busy() || m_phase == QLatin1String("ocr") || m_phase == QLatin1String("preview") || committing();
}

void ImportRecordsController::setBackend(IBackend *backend)
{
    if (m_backend == backend)
        return;
    if (busy() && !committing())
        cancel();
    m_backend = backend;
}

void ImportRecordsController::setOcrApplicationDirectoryForTesting(const QString &directory)
{
    m_ocr->setApplicationDirectoryForTesting(directory);
}

QString ImportRecordsController::statusText() const
{
    if (m_phase == QLatin1String("ocr"))
        return tr("正在本地识别 %1 / %2：%3").arg(m_ocr->completedImages() + 1).arg(m_ocr->totalImages()).arg(m_ocr->currentFile());
    if (m_phase == QLatin1String("preview"))
        return tr("正在解析和核对重复记录，尚未写入历史。");
    if (m_phase == QLatin1String("commit"))
        return tr("正在保存选中的本人记录，请等待保存结果。");
    if (m_phase == QLatin1String("complete")) {
        const QString summary = tr("已导入 %1 条；重复 %2 条；冲突 %3 条保留本地。").arg(m_commitResult.value(QStringLiteral("imported_count")).toInt())
            .arg(m_commitResult.value(QStringLiteral("duplicate_count")).toInt())
            .arg(m_commitResult.value(QStringLiteral("conflict_count")).toInt());
        const int deducted = m_commitResult.value(QStringLiteral("baseline_deducted_count")).toInt();
        if (deducted <= 0)
            return summary;
        const int baseline = m_commitResult.value(QStringLiteral("baseline_completed_count")).toInt();
        return summary + tr("成就基数已从 %1 改为 %2（扣除 %3 次已导入的通关），进度不变。").arg(baseline + deducted).arg(baseline).arg(deducted);
    }
    if (pendingCommitConfirmation())
        return tr("上次保存结果尚未确认。请重试确认同一批次；输入和选择暂时锁定，避免重复保存。");
    if (!m_rows.isEmpty() && !previewValid())
        return tr("内容已修改。请重新校验；仍可导入的原勾选会保留。");
    if (previewValid())
        return m_rows.isEmpty() ? tr("此来源没有记录可预览。请检查文件内容或列映射。")
                               : tr("请核对本人导随与结果。已确认通关正常计数；未知游戏时间保留待补充，未知结果不计通关。");
    return tr("选择文件、拖入图片，或粘贴截图和表格。缺失结果默认通关，可在预览中修改；不会补造游戏时间。");
}

QString ImportRecordsController::sourceKindForPath(const QString &path)
{
    const QString suffix = QFileInfo(path).suffix().toLower();
    if (suffix == QLatin1String("csv")) return QStringLiteral("CSV");
    if (suffix == QLatin1String("xlsx")) return QStringLiteral("XLSX");
    if (suffix == QLatin1String("json")) return QStringLiteral("JSON");
    if (suffix == QLatin1String("db") || suffix == QLatin1String("sqlite") || suffix == QLatin1String("sqlite3")) return QStringLiteral("BACKUP");
    if (suffix == QLatin1String("png") || suffix == QLatin1String("jpg") || suffix == QLatin1String("jpeg")
        || suffix == QLatin1String("webp") || suffix == QLatin1String("bmp")) return QStringLiteral("SCREENSHOT");
    return {};
}

QString ImportRecordsController::localPath(const QVariant &pathOrUrl)
{
    QString path;
    const QUrl url = pathOrUrl.toUrl();
    if (url.isLocalFile()) {
        if (!url.host().isEmpty())
            return {};
        path = url.toLocalFile();
    } else {
        const QString text = pathOrUrl.toString();
        if (text.contains(QStringLiteral("://")))
            return {};
        path = text;
    }
    path = QDir::fromNativeSeparators(path);
    if (path.startsWith(QStringLiteral("//")) || !QFileInfo(path).isAbsolute())
        return {};
    return QDir::cleanPath(path);
}

void ImportRecordsController::beginInput(const QString &kind, const QString &name)
{
    ++m_generation;
    m_sourceKind = kind;
    m_sourceName = name;
    m_filePath.clear();
    m_text.clear();
    m_candidates = {};
    m_sourceImages.clear();
    m_appendImages.clear();
    m_appendCandidates = {};
    m_appendEvidence.clear();
    m_appendPreviousPhase.clear();
    m_restoreSelection.clear();
    m_hasAcceptedPreview = false;
    m_batchCommitted = false;
    m_evidence.clear();
    m_rows.clear();
    m_summary.clear();
    m_commitResult.clear();
    m_pendingCommitPayload = {};
    m_columnMapping.clear();
    m_error.clear();
    m_current = -1;
    m_ownConfirmed = false;
    m_deductChosen = false;
    m_deduct = false;
    m_phase = QStringLiteral("editing");
    invalidatePreview(false);
}

QString ImportRecordsController::screenshotSourceName(const QStringList &paths) const
{
    if (paths.isEmpty())
        return {};
    const QString first = QFileInfo(paths.first()).fileName();
    return (paths.size() == 1 ? first : tr("%1 等 %2 张截图").arg(first).arg(paths.size())).left(500);
}

void ImportRecordsController::discardAppend(const QString &message)
{
    m_appendImages.clear();
    m_appendCandidates = {};
    m_appendEvidence.clear();
    m_phase = m_appendPreviousPhase.isEmpty() ? QStringLiteral("editing") : m_appendPreviousPhase;
    m_appendPreviousPhase.clear();
    m_error = message;
    Q_EMIT changed();
}

void ImportRecordsController::reset()
{
    if (committing() || pendingCommitConfirmation())
        return;
    cancel();
    beginInput({}, {});
    m_columnMapping.clear();
    m_clipboardFiles.reset();
    m_phase = QStringLiteral("empty");
    Q_EMIT changed();
}

void ImportRecordsController::chooseFiles()
{
    if (busy() || pendingCommitConfirmation())
        return;
    const QStringList files = QFileDialog::getOpenFileNames(nullptr, tr("选择导入记录或截图"), {},
        tr("记录和图片 (*.csv *.xlsx *.json *.db *.sqlite *.sqlite3 *.png *.jpg *.jpeg *.webp *.bmp);;记录文件 (*.csv *.xlsx *.json *.db *.sqlite *.sqlite3);;截图 (*.png *.jpg *.jpeg *.webp *.bmp)"));
    if (files.isEmpty())
        return;
    QVariantList paths;
    for (const QString &file : files)
        paths.append(file);
    importFiles(paths);
}

void ImportRecordsController::importFiles(const QVariantList &pathsOrUrls)
{
    if (busy() || pendingCommitConfirmation() || pathsOrUrls.isEmpty())
        return;
    QStringList files;
    QString kind;
    for (const QVariant &value : pathsOrUrls) {
        const QString path = localPath(value);
        const QString nextKind = sourceKindForPath(path);
        if (path.isEmpty() || nextKind.isEmpty() || !QFileInfo(path).isFile()) {
            fail(tr("请选择可读取的本地 CSV、XLSX、JSON、数据库或截图文件。"));
            return;
        }
        if (!kind.isEmpty() && kind != nextKind) {
            fail(tr("截图可以一次选择多张；表格和数据库请按来源逐个导入。"));
            return;
        }
        kind = nextKind;
        files.append(path);
    }
    if (files.size() > 1 && kind != QLatin1String("SCREENSHOT")) {
        fail(tr("请选择一个记录文件。多张截图可以同时识别。"));
        return;
    }
    if (kind == QLatin1String("SCREENSHOT")) {
        const bool append = m_sourceKind == QLatin1String("SCREENSHOT")
            && !m_sourceImages.isEmpty() && !m_candidates.isEmpty() && !m_batchCommitted;
        if (files.size() + (append ? m_sourceImages.size() : 0) > OfflineOcrEngine::MaxImages) {
            fail(tr("同一批次最多 %1 张截图；原批次已保留。").arg(OfflineOcrEngine::MaxImages));
            return;
        }
        if (append) {
            m_appendImages = files;
            m_appendCandidates = {};
            m_appendEvidence.clear();
            m_appendPreviousPhase = m_phase;
            m_error.clear();
            if (previewValid())
                m_restoreSelection = m_selected;
        } else {
            beginInput(kind, screenshotSourceName(files));
            m_sourceImages = files;
        }
        m_phase = QStringLiteral("ocr");
        if (!m_ocr->start(files)) {
            if (append)
                discardAppend(m_error);
            else {
                m_sourceImages.clear();
                m_phase = QStringLiteral("editing");
            }
            Q_EMIT changed();
        }
    } else {
        beginInput(kind, QFileInfo(files.first()).fileName().left(500));
        m_filePath = files.first();
        revalidate();
    }
    Q_EMIT changed();
}

void ImportRecordsController::pasteClipboard()
{
    if (busy() || pendingCommitConfirmation())
        return;
    QClipboard *clipboard = QGuiApplication::clipboard();
    const QMimeData *mime = clipboard ? clipboard->mimeData() : nullptr;
    if (!mime) {
        fail(tr("剪贴板没有可导入的内容。"));
        return;
    }
    if (mime->hasImage()) {
        if (m_sourceKind == QLatin1String("SCREENSHOT") && !m_batchCommitted
            && !m_candidates.isEmpty() && m_sourceImages.size() >= OfflineOcrEngine::MaxImages) {
            fail(tr("同一批次最多 %1 张截图；原批次已保留。").arg(OfflineOcrEngine::MaxImages));
            return;
        }
        const QImage image = clipboard->image();
        if (image.isNull() || qint64(image.width()) * image.height() > OfflineOcrEngine::MaxPixels) {
            fail(tr("剪贴板图片无法读取或超过 5000 万像素。"));
            return;
        }
        if (!m_clipboardFiles)
            m_clipboardFiles = std::make_unique<QTemporaryDir>(QDir::tempPath() + QStringLiteral("/mentor-import-clipboard-XXXXXX"));
        if (!m_clipboardFiles->isValid()) {
            fail(tr("无法创建剪贴板图片副本，请检查本地磁盘权限。"));
            return;
        }
        const QString path = m_clipboardFiles->filePath(QUuid::createUuid().toString(QUuid::WithoutBraces) + QStringLiteral(".png"));
        if (!image.save(path, "PNG")) {
            fail(tr("无法保存剪贴板图片副本。"));
            return;
        }
        importFiles({path});
        return;
    }
    if (mime->hasUrls()) {
        QVariantList urls;
        for (const QUrl &url : mime->urls())
            urls.append(url);
        importFiles(urls);
        return;
    }
    if (mime->hasText()) {
        importText(mime->text());
        return;
    }
    fail(tr("剪贴板中没有截图、文件或表格文字。"));
}

void ImportRecordsController::importText(const QString &text)
{
    if (busy() || pendingCommitConfirmation())
        return;
    if (text.trimmed().isEmpty() || text.size() > 1024 * 1024) {
        fail(tr("请粘贴非空表格文字，大小不超过 1 MiB。"));
        return;
    }
    beginInput(QStringLiteral("PASTE"), tr("粘贴的表格"));
    m_text = text;
    revalidate();
}

void ImportRecordsController::recognizeImage(const QString &path, const QByteArray &tsv)
{
    if (m_phase != QLatin1String("ocr"))
        return;
    QImageReader reader(path);
    reader.setAutoTransform(true);
    const QImage image = reader.read();
    QString error;
    const QVariantList recognized = ScreenshotImportParser::parse(image, tsv, path, &error);
    if (!error.isEmpty()) {
        m_error = error;
        return;
    }
    QSet<int> eligibleJobs;
    for (const QVariant &value : JobCatalog().battleJobs())
        eligibleJobs.insert(value.toMap().value(QStringLiteral("job_id")).toInt());
    for (const QVariant &value : recognized) {
        QVariantMap row = value.toMap();
        QVariantMap evidence;
        for (const QString &key : {QStringLiteral("source_rect"), QStringLiteral("source_image"), QStringLiteral("ocr_confidence"),
                                  QStringLiteral("icon_confidence"), QStringLiteral("icon_evidence_type"),
                                  QStringLiteral("needs_review"), QStringLiteral("warnings"),
                                  QStringLiteral("duty_level"), QStringLiteral("source_type"), QStringLiteral("job_candidate_id"),
                                  QStringLiteral("ocr_duty_name"), QStringLiteral("duty_candidate_name"),
                                  QStringLiteral("duty_candidate_pending"),
                                  QStringLiteral("job_candidate_name"), QStringLiteral("icon_runner_up_id"), QStringLiteral("icon_runner_up_name"),
                                  QStringLiteral("icon_runner_up_confidence"), QStringLiteral("icon_margin")}) {
            if (row.contains(key))
                evidence.insert(key, row.take(key));
        }
        const int jobCandidate = evidence.value(QStringLiteral("job_candidate_id")).toInt();
        const bool hasEligibleCandidate = eligibleJobs.contains(jobCandidate);
        // 预填最接近的战斗职业，分数和待复核标记留在 Desktop 证据中。
        // 截图行仍须用户核对并勾选；此标记不成为 Collector 的记录字段。
        evidence.insert(QStringLiteral("job_candidate_pending"), hasEligibleCandidate);
        if (hasEligibleCandidate) {
            row.insert(QStringLiteral("job_id"), jobCandidate);
            row.insert(QStringLiteral("job_name"), evidence.value(QStringLiteral("job_candidate_name")));
        }
        // The screenshot's time is evidence of a source record, not a game
        // event. No matched/entered/ended time or game result is fabricated.
        if (m_appendImages.isEmpty()) {
            m_candidates.append(QJsonObject::fromVariantMap(row));
            m_evidence.append(evidence);
        } else {
            m_appendCandidates.append(QJsonObject::fromVariantMap(row));
            m_appendEvidence.append(evidence);
        }
    }
    if (m_candidates.size() + m_appendCandidates.size() > 1000) {
        m_error = tr("识别候选超过 1000 条，请分批导入。");
        m_ocr->cancel();
    }
}

QJsonObject ImportRecordsController::previewRequest() const
{
    QJsonArray candidates = m_candidates;
    for (const auto &candidate : m_appendCandidates)
        candidates.append(candidate);
    const QString sourceName = m_appendImages.isEmpty() ? m_sourceName
        : screenshotSourceName(m_sourceImages + m_appendImages);
    QJsonObject request{{QStringLiteral("source_kind"), m_sourceKind}, {QStringLiteral("source_name"), sourceName},
                        {QStringLiteral("time_zone"), m_timeZone}};
    if (!candidates.isEmpty())
        request.insert(QStringLiteral("rows"), candidates);
    else if (!m_filePath.isEmpty())
        request.insert(QStringLiteral("file_path"), m_filePath);
    else if (!m_text.isEmpty())
        request.insert(QStringLiteral("text"), m_text);
    if (!m_columnMapping.isEmpty())
        request.insert(QStringLiteral("column_mapping"), QJsonObject::fromVariantMap(m_columnMapping));
    return request;
}

void ImportRecordsController::revalidate()
{
    if (busy())
        return;
    if (pendingCommitConfirmation()) {
        fail(tr("请先重试确认上一批保存结果，再开始新的预览。"));
        return;
    }
    if (m_sourceKind.isEmpty() || (m_candidates.isEmpty() && m_filePath.isEmpty() && m_text.isEmpty())) {
        fail(tr("请先选择导入来源。"));
        return;
    }
    if (!m_backend || !m_backend->isConnected()) {
        if (!m_appendImages.isEmpty())
            discardAppend(tr("记录服务尚未连接；追加未生效，原批次已保留。"));
        else
            fail(tr("记录服务尚未连接；输入已保留，连接后可重新校验。"));
        return;
    }
    if (m_appendImages.isEmpty())
        invalidatePreview();
    m_error.clear();
    m_phase = QStringLiteral("preview");
    const quint64 generation = ++m_generation;
    Q_EMIT changed();
    m_backend->request(QStringLiteral("PreviewRunImport"), previewRequest())
        ->whenDone(this, [this, generation](bool ok, const QVariantMap &payload, const QString &code, const QString &message) {
            if (generation != m_generation)
                return;
            m_phase = QStringLiteral("editing");
            if (!ok) {
                const QString error = code == QLatin1String("ERR_UNKNOWN_MESSAGE") ? tr("当前记录服务不支持导入，请更新应用后重试。")
                    : (message.isEmpty() ? tr("预览未完成，输入已保留。") : message);
                if (!m_appendImages.isEmpty())
                    discardAppend(error);
                else
                    fail(error);
                return;
            }
            acceptPreview(payload);
        });
}

void ImportRecordsController::acceptPreview(const QVariantMap &payload)
{
    const QString previewId = payload.value(QStringLiteral("preview_id")).toString();
    if (previewId.isEmpty()) {
        const QString error = tr("预览响应不完整，无法确认保存。请更新应用或重试。");
        if (!m_appendImages.isEmpty())
            discardAppend(error);
        else
            fail(error);
        return;
    }
    if (!m_appendImages.isEmpty()
        && payload.value(QStringLiteral("rows")).toList().size() != m_candidates.size() + m_appendCandidates.size()) {
        discardAppend(tr("追加预览响应不完整，原批次已保留。请重新校验来源后重试。"));
        return;
    }
    if (!m_appendImages.isEmpty()) {
        m_sourceImages.append(m_appendImages);
        m_sourceName = screenshotSourceName(m_sourceImages);
        m_evidence.append(m_appendEvidence);
        m_appendImages.clear();
        m_appendCandidates = {};
        m_appendEvidence.clear();
        m_appendPreviousPhase.clear();
    }
    m_previewId = previewId;
    m_rows = payload.value(QStringLiteral("rows")).toList();
    m_summary = payload.value(QStringLiteral("summary")).toMap();
    m_candidates = {};
    m_selected.clear();
    for (int i = 0; i < m_rows.size(); ++i) {
        QVariantMap row = m_rows[i].toMap();
        const QVariantMap candidate = row.value(QStringLiteral("candidate")).toMap();
        m_candidates.append(QJsonObject::fromVariantMap(candidate));
        // Preserve Desktop-only OCR warnings and source coordinates separately.
        if (i < m_evidence.size())
            row.insert(QStringLiteral("evidence"), m_evidence[i]);
        const bool selectable = row.value(QStringLiteral("can_import")).toBool();
        // Screenshot text always requires explicit row selection after checking
        // the source. Duplicate/conflict/invalid rows never become selected.
        const int number = row.value(QStringLiteral("row_number")).toInt();
        const bool selected = selectable && (m_hasAcceptedPreview ? m_restoreSelection.contains(number)
            : m_sourceKind != QLatin1String("SCREENSHOT"));
        row.insert(QStringLiteral("selected"), selected);
        if (selected)
            m_selected.insert(row.value(QStringLiteral("row_number")).toInt());
        m_rows[i] = row;
    }
    m_hasAcceptedPreview = true;
    m_restoreSelection.clear();
    m_current = m_rows.isEmpty() ? -1 : qBound(0, m_current, int(m_rows.size()) - 1);
    m_commitRequestId = QUuid::createUuid().toString(QUuid::WithoutBraces);
    Q_EMIT changed();
}

void ImportRecordsController::invalidatePreview(bool rememberSelection)
{
    if (rememberSelection && previewValid())
        m_restoreSelection = m_selected;
    else if (!rememberSelection)
        m_restoreSelection.clear();
    m_previewId.clear();
    m_commitRequestId.clear();
    m_selected.clear();
    for (QVariant &value : m_rows) {
        QVariantMap row = value.toMap();
        row.insert(QStringLiteral("selected"), false);
        value = row;
    }
}

void ImportRecordsController::updateCandidate(int row, const QVariantMap &changes)
{
    if (busy() || pendingCommitConfirmation() || row < 0 || row >= m_candidates.size())
        return;
    QJsonObject candidate = m_candidates[row].toObject();
    bool candidateChanged = false;
    bool recordedTimeChanged = false;
    bool dutyNameChanged = false;
    bool dutyCategoryChanged = false;
    for (auto it = changes.cbegin(); it != changes.cend(); ++it)
    {
        const QJsonValue value = QJsonValue::fromVariant(it.value());
        const bool clearingProjectedCategory = it.key() == QLatin1String("duty_category")
            && (value.isNull() || (value.isString() && value.toString().isEmpty()))
            && candidate.value(QStringLiteral("duty_category")).toString().isEmpty()
            && row < m_rows.size()
            && !m_rows[row].toMap().value(QStringLiteral("run")).toMap()
                    .value(QStringLiteral("duty_category")).toString().isEmpty();
        if (!candidate.contains(it.key()) && (value.isNull() || (value.isString() && value.toString().isEmpty()))
            && !clearingProjectedCategory)
            continue;
        if (candidate.value(it.key()) != value || clearingProjectedCategory) {
            candidate.insert(it.key(), value);
            candidateChanged = true;
            recordedTimeChanged |= it.key() == QLatin1String("source_recorded_at");
            dutyNameChanged |= it.key() == QLatin1String("duty_name");
            dutyCategoryChanged |= it.key() == QLatin1String("duty_category");
        }
    }
    bool evidenceReviewed = false;
    if ((changes.contains(QStringLiteral("job_id")) || changes.contains(QStringLiteral("duty_name")))
        && row < m_evidence.size()) {
        QVariantMap evidence = m_evidence[row].toMap();
        if (changes.contains(QStringLiteral("job_id"))
            && evidence.value(QStringLiteral("job_candidate_pending")).toBool()) {
            evidence.insert(QStringLiteral("job_candidate_pending"), false);
            evidenceReviewed = true;
        }
        if (changes.contains(QStringLiteral("duty_name"))
            && evidence.value(QStringLiteral("duty_candidate_pending")).toBool()) {
            evidence.insert(QStringLiteral("duty_candidate_pending"), false);
            evidenceReviewed = true;
        }
        if (evidenceReviewed) {
            m_evidence[row] = evidence;
            if (row < m_rows.size()) {
                QVariantMap display = m_rows[row].toMap();
                display.insert(QStringLiteral("evidence"), evidence);
                m_rows[row] = display;
            }
        }
    }
    if (!candidateChanged) {
        if (evidenceReviewed)
            Q_EMIT changed();
        return;
    }
    if (dutyNameChanged) {
        // 旧副本身份不能跟随新名称。显式一次提供的新身份/类别仍保留；
        // 其他字段由下一次 Collector 预览按新名称重新推导。
        for (const QString &key : {QStringLiteral("content_id"), QStringLiteral("territory_id"),
                                  QStringLiteral("duty_category"), QStringLiteral("duty_source")}) {
            if (!changes.contains(key))
                candidate.remove(key);
        }
    }
    if (recordedTimeChanged) {
        candidate.remove(QStringLiteral("source_recorded_at_utc"));
        if (candidate.value(QStringLiteral("import_metadata")).isObject()) {
            QJsonObject metadata = candidate.value(QStringLiteral("import_metadata")).toObject();
            metadata.remove(QStringLiteral("source_recorded_at"));
            metadata.remove(QStringLiteral("source_recorded_at_utc"));
            candidate.insert(QStringLiteral("import_metadata"), metadata);
        }
    }
    m_candidates[row] = candidate;
    if (row < m_rows.size()) {
        QVariantMap display = m_rows[row].toMap();
        display.insert(QStringLiteral("candidate"), candidate.toVariantMap());
        if (dutyNameChanged) {
            QVariantMap run = display.value(QStringLiteral("run")).toMap();
            for (const QString &key : {QStringLiteral("content_id"), QStringLiteral("territory_id"),
                                      QStringLiteral("duty_category"), QStringLiteral("duty_source")})
                run.remove(key);
            display.insert(QStringLiteral("run"), run);
        } else if (dutyCategoryChanged) {
            QVariantMap run = display.value(QStringLiteral("run")).toMap();
            run.remove(QStringLiteral("duty_category"));
            display.insert(QStringLiteral("run"), run);
        }
        m_rows[row] = display;
    }
    invalidatePreview();
    m_commitResult.clear();
    m_phase = QStringLiteral("editing");
    Q_EMIT changed();
}

void ImportRecordsController::batchUpdateSelected(const QVariantMap &changes)
{
    if (busy() || pendingCommitConfirmation() || !previewValid() || m_selected.isEmpty() || changes.isEmpty())
        return;
    const QSet<QString> allowed{QStringLiteral("job_id"), QStringLiteral("job_name"),
                               QStringLiteral("result"), QStringLiteral("reflection_mood")};
    for (auto it = changes.cbegin(); it != changes.cend(); ++it) {
        if (!allowed.contains(it.key())) {
            fail(tr("批量设置只支持职业、通关结果和打本心情。"));
            return;
        }
    }
    QVariantMap normalized = changes;
    if (changes.contains(QStringLiteral("job_name")) && !changes.contains(QStringLiteral("job_id"))) {
        fail(tr("请选择批量设置的职业。"));
        return;
    }
    if (changes.contains(QStringLiteral("job_id"))) {
        const QVariant id = changes.value(QStringLiteral("job_id"));
        QString name;
        bool eligible = id.isNull();
        for (const QVariant &choice : jobChoices()) {
            const QVariantMap job = choice.toMap();
            if (!job.value(QStringLiteral("job_id")).isNull() && job.value(QStringLiteral("job_id")) == id) {
                eligible = true;
                name = job.value(QStringLiteral("job_name")).toString();
                break;
            }
        }
        if (!eligible) {
            fail(tr("请选择适用于导随的战斗职业或未知职业。"));
            return;
        }
        normalized.insert(QStringLiteral("job_name"), name.isEmpty() ? QVariant() : QVariant(name));
    }
    if (changes.contains(QStringLiteral("result"))) {
        const QSet<QString> results{QStringLiteral("UNKNOWN"), QStringLiteral("COMPLETED"), QStringLiteral("LEFT_OR_ABANDONED"),
            QStringLiteral("CANCELLED_BEFORE_ENTRY"), QStringLiteral("DISCONNECTED"), QStringLiteral("INTERRUPTED")};
        if (!results.contains(changes.value(QStringLiteral("result")).toString())) {
            fail(tr("请选择有效的通关结果。"));
            return;
        }
    }
    if (changes.contains(QStringLiteral("reflection_mood"))) {
        const QSet<QString> moods{QStringLiteral("unknown"), QStringLiteral("good"), QStringLiteral("ok"), QStringLiteral("bad")};
        if (!moods.contains(changes.value(QStringLiteral("reflection_mood")).toString())) {
            fail(tr("请选择有效的打本心情。"));
            return;
        }
    }
    // 先冻结真实数组下标。第一条修改使校验失效并暂时清空显示勾选，
    // 后续目标不能再从这个变化中的集合获取。
    QList<int> targets;
    for (int i = 0; i < m_rows.size(); ++i) {
        const QVariantMap row = m_rows[i].toMap();
        if (row.value(QStringLiteral("can_import")).toBool()
            && m_selected.contains(row.value(QStringLiteral("row_number")).toInt()))
            targets.append(i);
    }
    for (int index : targets)
        updateCandidate(index, normalized);
    if (!targets.isEmpty())
        revalidate();
}

void ImportRecordsController::setRowSelected(int row, bool selected)
{
    if (busy() || pendingCommitConfirmation() || !previewValid() || row < 0 || row >= m_rows.size())
        return;
    QVariantMap display = m_rows[row].toMap();
    if (!display.value(QStringLiteral("can_import")).toBool())
        return;
    const int number = display.value(QStringLiteral("row_number")).toInt();
    if (selected)
        m_selected.insert(number);
    else
        m_selected.remove(number);
    display.insert(QStringLiteral("selected"), selected);
    m_rows[row] = display;
    Q_EMIT changed();
}

void ImportRecordsController::setAllImportableSelected(bool selected)
{
    if (busy() || pendingCommitConfirmation() || !previewValid())
        return;
    for (int i = 0; i < m_rows.size(); ++i) {
        QVariantMap display = m_rows[i].toMap();
        if (!display.value(QStringLiteral("can_import")).toBool())
            continue;
        const int number = display.value(QStringLiteral("row_number")).toInt();
        if (selected) m_selected.insert(number); else m_selected.remove(number);
        display.insert(QStringLiteral("selected"), selected);
        m_rows[i] = display;
    }
    Q_EMIT changed();
}

void ImportRecordsController::commit()
{
    if (!canCommit() || !m_backend)
        return;
    QJsonArray numbers;
    QList<int> sorted = m_selected.values();
    std::sort(sorted.begin(), sorted.end());
    for (int number : sorted)
        numbers.append(number);
    QJsonObject request = pendingCommitConfirmation() ? m_pendingCommitPayload
        : QJsonObject{{QStringLiteral("preview_id"), m_previewId}, {QStringLiteral("row_numbers"), numbers},
                      {QStringLiteral("confirm_own_records"), true}};
    // Only an answered question travels: an older Collector rejects fields it does not know,
    // and with no baseline or no contributing row there is nothing to deduct.
    if (!pendingCommitConfirmation() && baselineChoiceOffered() && deductFromBaseline())
        request.insert(QStringLiteral("deduct_from_baseline"), true);
    const quint64 generation = m_generation;
    m_phase = QStringLiteral("commit");
    m_error.clear();
    Q_EMIT changed();
    QPointer<BackendReply> reply = m_backend->requestWithId(QStringLiteral("CommitRunImport"), request, m_commitRequestId);
    reply->whenDone(this, [this, generation, request, reply](bool ok, const QVariantMap &payload, const QString &code, const QString &message) {
            if (generation != m_generation)
                return;
            m_phase = QStringLiteral("editing");
            if (!ok) {
                if ((!reply || !reply->neverSent()) && (code == QLatin1String("ERR_INTERNAL") || code == QLatin1String("ERR_TIMEOUT")))
                    m_pendingCommitPayload = request;
                fail(message.isEmpty() ? tr("尚未确认导入结果。输入已保留，可安全重试同一批次。") : message);
                return;
            }
            m_pendingCommitPayload = {};
            m_commitResult = payload;
            m_batchCommitted = true;
            invalidatePreview(false);
            m_phase = QStringLiteral("complete");
            Q_EMIT changed();
            Q_EMIT imported(payload);
        });
}

void ImportRecordsController::cancel()
{
    if (committing() || pendingCommitConfirmation())
        return;
    ++m_generation;
    if (!m_appendImages.isEmpty()) {
        // 先退出 OCR/preview 阶段，使进程的迟到终结信号不会触碰旧批次。
        discardAppend(tr("追加已取消，原批次已保留。"));
        if (m_ocr->busy())
            m_ocr->cancel();
        return;
    }
    if (m_ocr->busy())
        m_ocr->cancel();
    invalidatePreview(false);
    m_phase = m_rows.isEmpty() ? QStringLiteral("empty") : QStringLiteral("editing");
    Q_EMIT changed();
}

void ImportRecordsController::fail(const QString &message)
{
    m_error = message;
    if (!busy())
        m_phase = QStringLiteral("editing");
    Q_EMIT changed();
}

void ImportRecordsController::setOwnRecordsConfirmed(bool confirmed)
{
    if (committing() || pendingCommitConfirmation() || m_ownConfirmed == confirmed)
        return;
    m_ownConfirmed = confirmed;
    Q_EMIT changed();
}

void ImportRecordsController::setBaselineCount(int count)
{
    count = qMax(0, count);
    if (m_baselineCount == count)
        return;
    m_baselineCount = count;
    Q_EMIT changed();
}

int ImportRecordsController::contributingSelectedCount() const
{
    // Mirrors docs/statistics-definitions.md §4 on the preview the Collector rendered: the
    // Collector decides at commit; this count only sizes the question and the preview.
    int count = 0;
    for (const QVariant &value : m_rows) {
        const QVariantMap row = value.toMap();
        if (!row.value(QStringLiteral("selected")).toBool())
            continue;
        const QVariantMap run = row.value(QStringLiteral("run")).toMap();
        if (run.isEmpty() || run.value(QStringLiteral("result")).toString() != QLatin1String("COMPLETED"))
            continue;
        if (run.contains(QStringLiteral("contributes_to_goal")) && !run.value(QStringLiteral("contributes_to_goal")).toBool())
            continue;
        if (run.value(QStringLiteral("soft_deleted")).toBool() || run.value(QStringLiteral("pending_review")).toBool())
            continue;
        const QVariantMap metadata = run.value(QStringLiteral("import_metadata")).toMap();
        const QVariant roulette = run.value(QStringLiteral("mentor_roulette_id"));
        const bool confirmed = metadata.value(QStringLiteral("mentor_confirmed")).toBool()
            || (roulette.isValid() && !roulette.isNull());
        if (confirmed)
            ++count;
    }
    return count;
}

bool ImportRecordsController::deductFromBaseline() const
{
    if (m_deductChosen)
        return m_deduct;
    // A backup or this software's own JSON export holds runs recorded after installation, which
    // the baseline never included. Spreadsheets, pasted tables and screenshots usually carry the
    // history the game's achievement panel - and so the baseline - already counted.
    return m_sourceKind != QLatin1String("BACKUP") && m_sourceKind != QLatin1String("JSON");
}

void ImportRecordsController::setDeductFromBaseline(bool deduct)
{
    if (committing() || pendingCommitConfirmation() || (m_deductChosen && m_deduct == deduct))
        return;
    m_deductChosen = true;
    m_deduct = deduct;
    Q_EMIT changed();
}

void ImportRecordsController::setCurrentRow(int row)
{
    if (row < -1 || row >= m_rows.size() || row == m_current)
        return;
    m_current = row;
    Q_EMIT changed();
}

QVariantMap ImportRecordsController::currentCandidate() const
{
    if (m_current < 0 || m_current >= m_candidates.size())
        return {};
    QVariantMap candidate = m_candidates[m_current].toObject().toVariantMap();
    if (m_current < m_rows.size() && candidate.value(QStringLiteral("duty_category")).toString().isEmpty()) {
        const QVariantMap run = m_rows[m_current].toMap().value(QStringLiteral("run")).toMap();
        const QString category = run.value(QStringLiteral("duty_category")).toString();
        // 展示 Collector 已推导的类别；保持 wire candidate 省略字段语义，
        // 避免把展示投影误作来源明确提供的字段参与重复/冲突比较。
        if (!category.isEmpty())
            candidate.insert(QStringLiteral("duty_category"), category);
    }
    return candidate;
}

QVariantMap ImportRecordsController::currentEvidence() const
{
    QVariantMap evidence = m_current >= 0 && m_current < m_evidence.size() ? m_evidence[m_current].toMap() : QVariantMap();
    const QString path = evidence.value(QStringLiteral("source_image")).toString();
    if (!path.isEmpty())
        evidence.insert(QStringLiteral("source_url"), QUrl::fromLocalFile(path));
    return evidence;
}

void ImportRecordsController::setTimeZone(const QString &zone)
{
    if (busy() || pendingCommitConfirmation() || m_timeZone == zone)
        return;
    m_timeZone = zone;
    static const QRegularExpression explicitOffset(QStringLiteral(R"((?:Z|[+-]\d{2}:\d{2})$)"));
    for (int i = 0; i < m_candidates.size(); ++i) {
        QJsonObject candidate = m_candidates[i].toObject();
        QJsonObject metadata = candidate.value(QStringLiteral("import_metadata")).toObject();
        const QJsonValue raw = candidate.contains(QStringLiteral("source_recorded_at"))
            ? candidate.value(QStringLiteral("source_recorded_at")) : metadata.value(QStringLiteral("source_recorded_at"));
        const QString recorded = raw.toString().trimmed();
        // Only an explicit zone change invalidates local-time derivatives.
        // Native UTC-only provenance and offsets embedded in the raw value
        // retain their authority; date-only text stays raw and remains pending.
        if (recorded.isEmpty() || explicitOffset.match(recorded).hasMatch())
            continue;
        candidate.remove(QStringLiteral("source_recorded_at_utc"));
        if (candidate.value(QStringLiteral("import_metadata")).isObject()) {
            metadata.remove(QStringLiteral("source_recorded_at_utc"));
            candidate.insert(QStringLiteral("import_metadata"), metadata);
        }
        m_candidates[i] = candidate;
        if (i < m_rows.size()) {
            QVariantMap display = m_rows[i].toMap();
            display.insert(QStringLiteral("candidate"), candidate.toVariantMap());
            m_rows[i] = display;
        }
    }
    invalidatePreview();
    Q_EMIT changed();
}

void ImportRecordsController::setColumnMapping(const QVariantMap &mapping)
{
    if (busy() || pendingCommitConfirmation() || m_columnMapping == mapping)
        return;
    m_columnMapping = mapping;
    if (!m_filePath.isEmpty() || !m_text.isEmpty()) {
        m_candidates = {};
        m_rows.clear();
        m_current = -1;
    }
    invalidatePreview();
    Q_EMIT changed();
}

QVariantList ImportRecordsController::jobChoices() const
{
    JobCatalog jobs;
    QVariantList choices{{QVariantMap{{QStringLiteral("job_id"), QVariant()}, {QStringLiteral("job_name"), tr("职业未知 / 待补充")}}}};
    choices.append(jobs.battleJobs());
    return choices;
}

QVariantList ImportRecordsController::dutyChoices() const
{
    return DutyCatalog::shared()->allDuties();
}

} // namespace mr
