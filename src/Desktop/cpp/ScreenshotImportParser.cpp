#include "ScreenshotImportParser.h"
#include "ScreenshotOcrText.h"
#include "ScreenshotRowLayout.h"
#include "JobCatalog.h"
#include "JobIconClassifier.h"

#include <QDate>
#include <QFile>
#include <QImageReader>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QMap>
#include <QRect>
#include <QRegularExpression>
#include <QSet>
#include <QTime>
#include <QUrl>
#include <QVariantMap>

#include <algorithm>
#include <array>
#include <cmath>
#include <limits>

namespace mr::screenshot {

int colorDistance(QRgb a, QRgb b)
{
    return std::max({std::abs(qRed(a) - qRed(b)), std::abs(qGreen(a) - qGreen(b)),
                     std::abs(qBlue(a) - qBlue(b))});
}

} // namespace mr::screenshot

namespace {

using namespace mr::screenshot;

struct Header {
    int line = -1;
    int level = 0;
    QString duty;
    int left = 0;
    QRect detectedCard;
    bool recoveredLevelPrefix = false;
    bool anchoredDutyTitle = false;
};

/** @brief 一条候选的全部识别证据；手机卡片与网页单行记录共用同一组候选字段和提示。 */
struct Evidence {
    QRect rect;
    QRect iconArea;
    bool measuredIconLeft = false; ///< iconArea 的左边已实测越过行圆角（网页单行记录）。
    int textLeft = 0;
    QString duty;
    int level = 0;
    bool anchoredDutyTitle = false;
    QString recordedAt;
    bool timeValid = false;
    QStringList body;
    double confidence = 0;
    bool deletionOverlaps = false;
    bool pagingOverlaps = false;
    bool nearEdge = false;
};

struct DutyNameTemplate {
    int level = 0;
    QString name;
    QString normalizedName;
};

struct JobTemplate {
    int id = 0;
    QString name;
    QImage mask;
    double aspect = 0;
};

struct IconMatch {
    int id = 0;
    int candidateId = 0;
    QString candidateName;
    double confidence = 0;
    int runnerUpId = 0;
    QString runnerUpName;
    double runnerUpConfidence = 0;
    bool classifier = false;
};

QString normalizedDutyName(const QString &name)
{
    QString normalized;
    normalized.reserve(name.size());
    for (const QChar character : name) {
        if (character.isLetterOrNumber())
            normalized.append(character.toLower());
    }
    return normalized;
}

QList<DutyNameTemplate> loadDutyNameTemplates()
{
    // 仅读现有完整中文目录作为候选文字依据，不扩展 DutyCatalog 的展示职责，
    // 不把内容编号、类型或目录等级写成 OCR 已确认事实。
    QFile file(QStringLiteral(":/data/duties/cn.2026-09-04.json"));
    if (!file.open(QIODevice::ReadOnly) || file.size() > 1024 * 1024)
        return {};
    const QJsonArray rows = QJsonDocument::fromJson(file.readAll()).object()
                                .value(QStringLiteral("duties")).toArray();
    if (rows.size() > 5000)
        return {};
    QList<DutyNameTemplate> templates;
    QSet<QString> distinctNames;
    for (const QJsonValue &value : rows) {
        const QJsonObject row = value.toObject();
        const int level = row.value(QStringLiteral("level")).toInt();
        const QString name = row.value(QStringLiteral("localized_name")).toString();
        const QString normalized = normalizedDutyName(name);
        const QString key = QString::number(level) + QLatin1Char('\n') + normalized;
        if (level <= 0 || name.isEmpty() || normalized.isEmpty() || distinctNames.contains(key))
            continue;
        distinctNames.insert(key);
        templates.append({level, name, normalized});
    }
    return templates;
}

/** @brief 随包副本目录中的最高等级，供网页单行记录检查形近误读的等级；读不到目录时为 0（不检查）。 */
int catalogueMaxLevel()
{
    static const int maxLevel = [] {
        int top = 0;
        for (const DutyNameTemplate &candidate : loadDutyNameTemplates())
            top = std::max(top, candidate.level);
        return top;
    }();
    return maxLevel;
}

QString matchedDutyName(const QString &ocrName, int level, const QList<DutyNameTemplate> &templates)
{
    const QString normalized = normalizedDutyName(ocrName);
    const auto chineseLetters = std::count_if(normalized.cbegin(), normalized.cend(), [](QChar character) {
        return character.script() == QChar::Script_Han;
    });
    if (level <= 0 || normalized.size() < 6 || chineseLetters < 4)
        return {};
    QString bestName;
    int bestDistance = std::numeric_limits<int>::max();
    int runnerUpDistance = std::numeric_limits<int>::max();
    for (const DutyNameTemplate &candidate : templates) {
        if (candidate.level != level || candidate.normalizedName.size() != normalized.size())
            continue;
        int distance = 0;
        bool chineseOnlyErrors = true;
        for (qsizetype i = 0; i < normalized.size(); ++i) {
            if (normalized[i] == candidate.normalizedName[i])
                continue;
            if (normalized[i].script() != QChar::Script_Han
                || candidate.normalizedName[i].script() != QChar::Script_Han) {
                chineseOnlyErrors = false;
                break;
            }
            ++distance;
        }
        if (!chineseOnlyErrors)
            continue;
        if (distance < bestDistance) {
            runnerUpDistance = bestDistance;
            bestDistance = distance;
            bestName = candidate.name;
        } else if (distance < runnerUpDistance) {
            runnerUpDistance = distance;
        }
    }
    // 最多替换两个中文误字。非零距离必须比次候选至少少两个误字；
    // 同等或接近的候选不自动预填，数字、字母和缺字也不靠目录补造。
    if (bestDistance > 2 || (bestDistance > 0 && runnerUpDistance - bestDistance < 2))
        return {};
    return bestName;
}

bool isDeleteWord(const Word &word, int imageWidth)
{
    const QString text = word.text.trimmed();
    return (text == QLatin1String("X") || text == QLatin1String("x")
            || text == QStringLiteral("×") || text == QStringLiteral("✕"))
        && word.rect.left() >= imageWidth * 0.83;
}

/** @brief 排除被 OCR 当作字母的蓝色副本图标，保留图标右侧真正的副本名称文字。 */
bool isBlueIconWord(const Word &word, const QImage &image)
{
    int blue = 0;
    int count = 0;
    const int stepX = std::max(1, word.rect.width() / 16);
    const int stepY = std::max(1, word.rect.height() / 16);
    for (int y = word.rect.top(); y <= word.rect.bottom(); y += stepY) {
        for (int x = word.rect.left(); x <= word.rect.right(); x += stepX) {
            const QRgb pixel = image.pixel(x, y);
            blue += qBlue(pixel) > 110 && qGreen(pixel) > 70
                && qBlue(pixel) > qRed(pixel) * 1.25 && qGreen(pixel) > qRed(pixel) * 1.2;
            ++count;
        }
    }
    return count > 0 && static_cast<double>(blue) / count > 0.12;
}

constexpr std::array<double, 9> kSurfaceSamples{0.08, 0.16, 0.24, 0.34, 0.46,
                                               0.58, 0.70, 0.82, 0.92};

/** @brief 从标题所在行的多数同色背景寻找卡片上下边框，失败时保留文字锚点范围。 */
QRect cardRect(const QImage &image, const Line &title, int lowerLimit, int upperLimit,
               int fallbackBottom, bool &hasSurface)
{
    const int width = image.width();
    const int y = title.rect.center().y();
    QRgb surface = 0;
    int bestCount = 0;
    for (double xFraction : kSurfaceSamples) {
        const QRgb sample = image.pixel(qBound(0, qRound(width * xFraction), width - 1), y);
        // White text on a dark card must not become the sampled card surface.
        if (std::max({qRed(sample), qGreen(sample), qBlue(sample)}) > 210)
            continue;
        int count = 0;
        for (double other : kSurfaceSamples)
            count += colorDistance(sample, image.pixel(qBound(0, qRound(width * other), width - 1), y)) < 45;
        if (count > bestCount) {
            surface = sample;
            bestCount = count;
        }
    }
    hasSurface = false;
    int top = std::max(lowerLimit, title.rect.top() - title.rect.height());
    int bottom = std::min(upperLimit, fallbackBottom);
    if (bestCount >= 5) {
        const auto matches = [&](int row) {
            int count = 0;
            for (double fraction : kSurfaceSamples)
                count += colorDistance(surface, image.pixel(qBound(0, qRound(width * fraction), width - 1), row)) < 45;
            return count >= 3;
        };
        int above = y;
        while (above > lowerLimit && matches(above - 1))
            --above;
        int below = y;
        while (below < upperLimit && matches(below + 1))
            ++below;
        // A uniform full screenshot has no detected card boundary.
        if (above > lowerLimit && below < upperLimit
            && below - above > title.rect.height() * 2) {
            top = above;
            bottom = below;
            hasSurface = true;
        } else if (above > lowerLimit && below == upperLimit
                   && below - above > title.rect.height() * 2) {
            top = above;
            bottom = below;
            hasSurface = true;
        }
    }
    return QRect(qRound(width * 0.03), top, std::max(1, qRound(width * 0.94)),
                 std::max(1, bottom - top + 1)).intersected(image.rect());
}

/** @brief 从透明模板或卡片左侧提取金色图形；不把模板样式不同的素材强行归类。 */
QImage glyphMask(const QImage &image, double &aspect)
{
    if (image.isNull())
        return {};
    QImage binary(image.size(), QImage::Format_Grayscale8);
    binary.fill(0);
    QRect bounds;
    int foreground = 0;
    for (int y = 0; y < image.height(); ++y) {
        uchar *row = binary.scanLine(y);
        for (int x = 0; x < image.width(); ++x) {
            const QRgb pixel = image.pixel(x, y);
            const bool gold = qAlpha(pixel) > 100 && qRed(pixel) > 110
                && qGreen(pixel) > 85 && qRed(pixel) >= qGreen(pixel) * 0.9
                && qGreen(pixel) > qBlue(pixel) * 1.12;
            if (gold) {
                row[x] = 255;
                bounds = bounds.united(QRect(x, y, 1, 1));
                ++foreground;
            }
        }
    }
    if (foreground < 12 || bounds.width() < 3 || bounds.height() < 3)
        return {};
    aspect = static_cast<double>(bounds.width()) / bounds.height();
    return binary.copy(bounds).scaled(32, 32, Qt::IgnoreAspectRatio, Qt::FastTransformation);
}

QList<JobTemplate> loadJobTemplates()
{
    const mr::JobCatalog catalogue;
    const QVariantList jobs = catalogue.battleJobs();
    QList<JobTemplate> out;
    for (const QVariant &value : jobs) {
        const QVariantMap job = value.toMap();
        const int id = job.value(QStringLiteral("job_id")).toInt();
        const QUrl url(catalogue.plainIcon(id));
        const QString path = url.isLocalFile() ? url.toLocalFile()
            : (url.scheme() == QLatin1String("qrc") ? QLatin1Char(':') + url.path() : QString());
        if (path.isEmpty())
            continue;
        QImageReader reader(path);
        const QSize size = reader.size();
        if (!size.isValid() || size.width() > 1024 || size.height() > 1024)
            continue;
        JobTemplate candidate;
        candidate.id = id;
        candidate.name = job.value(QStringLiteral("job_name")).toString();
        candidate.mask = glyphMask(reader.read(), candidate.aspect);
        if (!candidate.mask.isNull())
            out.append(std::move(candidate));
    }
    return out;
}

IconMatch matchIcon(const QImage &image, const QRect &card, int textLeft,
                    const QList<JobTemplate> &templates, bool measuredLeft = false)
{
    const int iconRight = std::min(card.right(), measuredLeft ? textLeft - 2
                                                              : textLeft - std::max(2, image.width() / 80));
    // Exclude the blurred page background beyond the card's rounded left edge.
    // A web row's area already starts past its measured rounded corner, and
    // its icon lies left of 8 % at every window width.
    const int iconLeft = measuredLeft ? card.left() : std::max(card.left(), qRound(image.width() * 0.08));
    const int verticalInset = std::max(1, card.height() / 20);
    if (iconRight <= iconLeft || card.height() <= verticalInset * 2)
        return {};
    const QImage crop = image.copy(QRect(iconLeft, card.top() + verticalInset,
                                         iconRight - iconLeft, card.height() - verticalInset * 2));
    static const mr::JobIconClassifier classifier;
    const mr::JobIconClassifier::Result result = classifier.classify(crop);
    if (result.available) {
        // 模型拒识时不再尝试把同一未知图形靠模板变成可靠职业。
        const mr::JobCatalog catalogue;
        IconMatch match;
        match.id = result.id;
        match.candidateId = result.candidateId;
        match.candidateName = result.candidateId > 0 ? catalogue.jobName(result.candidateId) : QString();
        match.confidence = result.confidence;
        match.runnerUpId = result.runnerUpId;
        match.runnerUpName = result.runnerUpId > 0 ? catalogue.jobName(result.runnerUpId) : QString();
        match.runnerUpConfidence = result.runnerUpConfidence;
        match.classifier = true;
        return match;
    }
    if (templates.isEmpty())
        return {};
    double aspect = 0;
    const QImage mask = glyphMask(crop, aspect);
    if (mask.isNull())
        return {};
    IconMatch best;
    for (const JobTemplate &candidate : templates) {
        int intersection = 0;
        int unionCount = 0;
        for (int y = 0; y < 32; ++y) {
            const uchar *source = mask.constScanLine(y);
            const uchar *reference = candidate.mask.constScanLine(y);
            for (int x = 0; x < 32; ++x) {
                const bool a = source[x] > 127;
                const bool b = reference[x] > 127;
                intersection += a && b;
                unionCount += a || b;
            }
        }
        const double ratio = std::min(aspect, candidate.aspect) / std::max(aspect, candidate.aspect);
        const double score = unionCount == 0 ? 0 : static_cast<double>(intersection) / unionCount * ratio;
        if (score > best.confidence) {
            best.runnerUpId = best.candidateId;
            best.runnerUpName = best.candidateName;
            best.runnerUpConfidence = best.confidence;
            best.candidateId = candidate.id;
            best.candidateName = candidate.name;
            best.confidence = score;
        } else if (score > best.runnerUpConfidence) {
            best.runnerUpId = candidate.id;
            best.runnerUpName = candidate.name;
            best.runnerUpConfidence = score;
        }
    }
    // Heuristic evidence, intentionally conservative and always user-confirmed.
    if (best.confidence >= 0.82 && best.confidence - best.runnerUpConfidence >= 0.12)
        best.id = best.candidateId;
    return best;
}

void addWarning(QStringList &warnings, const QString &warning)
{
    if (!warnings.contains(warning))
        warnings.append(warning);
}

void addEvidenceWarnings(QStringList &warnings, const Evidence &evidence, const IconMatch &icon,
                         bool dutyPending)
{
    if (dutyPending)
        addWarning(warnings, QStringLiteral("副本名称已按同等级目录预填候选，请与原图核对；原识别文字已保留。"));
    if (icon.id == 0)
        addWarning(warnings, QStringLiteral("职业图标未可靠匹配，请核对候选职业或保留未知。"));
    if (evidence.recordedAt.isEmpty())
        addWarning(warnings, QStringLiteral("来源记录时间缺失，卡片可能被裁切或遮挡。"));
    else if (!evidence.timeValid)
        addWarning(warnings, QStringLiteral("来源记录时间无效，请对照原图修正。"));
    else
        addWarning(warnings, QStringLiteral("来源记录时间的时区需在预览中确认，不代表实际游戏时间。"));
    if (evidence.duty.isEmpty())
        addWarning(warnings, QStringLiteral("副本名称未识别完整，请对照原图补充。"));
    if (evidence.level == 0)
        addWarning(warnings, QStringLiteral("卡片标题识别失败，已按来源时间和边框单独保留，请修正副本信息。"));
    if (evidence.body.isEmpty())
        addWarning(warnings, QStringLiteral("未识别到心得正文，请检查原图是否被裁切或遮挡。"));
    if (evidence.confidence < 0.75)
        addWarning(warnings, QStringLiteral("文字识别把握较低，请逐项对照原图。"));
    if (evidence.deletionOverlaps)
        addWarning(warnings, QStringLiteral("删除按钮可能遮挡正文，请对照原图。"));
    if (evidence.pagingOverlaps)
        addWarning(warnings, QStringLiteral("分页或浏览器控件覆盖卡片区域，请对照原图确认未识别内容。"));
    if (evidence.nearEdge)
        addWarning(warnings, QStringLiteral("卡片接近截图边缘，可能存在未拍到的内容。"));
}

QVariantMap candidateMap(const QImage &image, const Evidence &evidence, QStringList warnings,
                         const QList<JobTemplate> &templates, const QList<DutyNameTemplate> &dutyTemplates,
                         const QString &sourceImagePath)
{
    const IconMatch icon = matchIcon(image, evidence.iconArea, evidence.textLeft, templates,
                                     evidence.measuredIconLeft);
    const QString dutyCandidate = evidence.anchoredDutyTitle
        ? matchedDutyName(evidence.duty, evidence.level, dutyTemplates) : QString();
    const bool dutyPending = !dutyCandidate.isEmpty() && dutyCandidate != evidence.duty;
    addEvidenceWarnings(warnings, evidence, icon, dutyPending);

    const QRect &rect = evidence.rect;
    QVariantMap row;
    row.insert(QStringLiteral("duty_name"), dutyPending ? dutyCandidate : evidence.duty);
    row.insert(QStringLiteral("ocr_duty_name"), evidence.duty);
    row.insert(QStringLiteral("duty_candidate_name"), dutyCandidate);
    row.insert(QStringLiteral("duty_candidate_pending"), dutyPending);
    row.insert(QStringLiteral("duty_level"), evidence.level > 0 ? QVariant(evidence.level) : QVariant());
    row.insert(QStringLiteral("reflection_text"), evidence.body.join(QLatin1Char('\n')));
    row.insert(QStringLiteral("source_recorded_at"), evidence.recordedAt);
    row.insert(QStringLiteral("job_id"), icon.id > 0 ? QVariant(icon.id) : QVariant());
    row.insert(QStringLiteral("source_type"), QStringLiteral("screenshot"));
    row.insert(QStringLiteral("source_image"), sourceImagePath);
    row.insert(QStringLiteral("source_rect"), QVariantMap{
        {QStringLiteral("x"), rect.x()}, {QStringLiteral("y"), rect.y()},
        {QStringLiteral("width"), rect.width()}, {QStringLiteral("height"), rect.height()}});
    row.insert(QStringLiteral("ocr_confidence"), evidence.confidence);
    row.insert(QStringLiteral("icon_confidence"), icon.confidence);
    row.insert(QStringLiteral("icon_evidence_type"), icon.classifier ? QStringLiteral("classifier") : QStringLiteral("template"));
    row.insert(QStringLiteral("job_candidate_id"), icon.candidateId > 0 ? QVariant(icon.candidateId) : QVariant());
    row.insert(QStringLiteral("job_candidate_name"), icon.candidateName);
    row.insert(QStringLiteral("icon_runner_up_id"), icon.runnerUpId > 0 ? QVariant(icon.runnerUpId) : QVariant());
    row.insert(QStringLiteral("icon_runner_up_name"), icon.runnerUpName);
    row.insert(QStringLiteral("icon_runner_up_confidence"), icon.runnerUpConfidence);
    row.insert(QStringLiteral("icon_margin"), icon.confidence - icon.runnerUpConfidence);
    row.insert(QStringLiteral("needs_review"), true);
    row.insert(QStringLiteral("warnings"), warnings);
    return row;
}

QVariantMap rowCandidate(const QImage &image, const RowRecord &row, const QList<JobTemplate> &templates,
                         const QList<DutyNameTemplate> &dutyTemplates, const QString &sourceImagePath)
{
    QStringList warnings;
    if (row.recoveredLevelPrefix)
        addWarning(warnings, QStringLiteral("等级前缀识别不完整，已依据所在行和日期保留，请核对副本标题。"));
    if (row.misreadLevelDigits)
        addWarning(warnings, QStringLiteral("等级数字识别不清，已按字形相近的数字读取，请核对等级。"));
    Evidence evidence;
    evidence.rect = row.rect;
    evidence.iconArea = row.iconArea;
    evidence.measuredIconLeft = true;
    evidence.textLeft = row.levelRect.left();
    evidence.duty = joinWords(row.duty);
    evidence.level = row.level;
    // 行底色已按等级旁的留白找到，副本名称列由等级与日期列夹定，不依赖图标颜色。
    evidence.anchoredDutyTitle = true;
    evidence.recordedAt = row.recordedAt;
    evidence.timeValid = row.timeValid;
    evidence.body = row.note;
    evidence.confidence = row.confidence;
    evidence.deletionOverlaps = row.deletionOverlaps;
    evidence.pagingOverlaps = row.pagingOverlaps;
    evidence.nearEdge = row.rect.top() == 0 || row.rect.bottom() >= image.height() - 1;
    return candidateMap(image, evidence, warnings, templates, dutyTemplates, sourceImagePath);
}

/**
 * @brief 校验输入，读出供手机卡片使用的物理行，并按全部单词找出网页单行记录（其行内文字不再作为
 * 手机卡片标题或时间锚点）。成功时返回空字符串，否则返回中文原因。
 */
QString readInput(const QImage &image, const QByteArray &tsv, QList<Line> &lines, QList<RowRecord> &rows)
{
    if (image.isNull() || static_cast<qint64>(image.width()) * image.height() > mr::ScreenshotImportParser::MaxImagePixels)
        return QStringLiteral("截图为空或超过 5000 万像素上限。");
    if (tsv.isEmpty() || tsv.size() > mr::ScreenshotImportParser::MaxTsvBytes)
        return QStringLiteral("识别输出为空或超过 8 MiB 上限。");
    QList<Line> allLines;
    QString error;
    if (!readLines(tsv, image.size(), lines, error, &allLines))
        return error;
    rows = rowRecords(image, allLines, catalogueMaxLevel());
    if (rows.size() > mr::ScreenshotImportParser::MaxCandidates)
        return QStringLiteral("截图中的记录候选超过 1000 条，请分批导入。");
    return {};
}

} // namespace

namespace mr {

QVariantList ScreenshotImportParser::parse(const QImage &image, const QByteArray &tsv,
                                           const QString &sourceImagePath, QString *error)
{
    if (error)
        error->clear();
    const auto fail = [&](const QString &message) {
        if (error)
            *error = message;
        return QVariantList{};
    };
    QList<Line> lines;
    QList<RowRecord> rows;
    const QString inputError = readInput(image, tsv, lines, rows);
    if (!inputError.isEmpty())
        return fail(inputError);

    static const QRegularExpression titlePattern(QStringLiteral(
        R"(^\s*[Ll]\s*[Vv]\s*[.。．:]?\s*(\d{1,3})\s*(.*)$)"));
    static const QRegularExpression missingLevelPrefix(QStringLiteral(
        R"(^\s*[Vv]\s*[.。．]\s*(\d{1,3})\s*(.*)$)"));
    const auto inRow = [&rows](const Line &line) {
        return std::any_of(rows.cbegin(), rows.cend(), [&line](const RowRecord &row) {
            return row.rect.contains(line.rect.center());
        });
    };
    QList<Header> headers;
    for (qsizetype i = 0; i < lines.size(); ++i) {
        const Line &line = lines[i];
        if (inRow(line))
            continue;
        QList<Word> words;
        QRect blueIcon;
        for (const Word &word : line.words) {
            if (word.rect.left() < image.width() * 0.16 || isDeleteWord(word, image.width()))
                continue;
            if (isBlueIconWord(word, image)) {
                if (word.rect.width() >= word.rect.height() * 0.6
                    && word.rect.width() <= word.rect.height() * 1.5)
                    blueIcon = word.rect;
            } else {
                words.append(word);
            }
        }
        QRegularExpressionMatch match = titlePattern.match(joinWords(words));
        QRect detectedCard;
        bool recoveredLevelPrefix = false;
        bool anchoredDutyTitle = false;
        if (!match.hasMatch()) {
            match = missingLevelPrefix.match(joinWords(words));
            if (!match.hasMatch() || words.isEmpty() || !blueIcon.isValid()
                || blueIcon.left() <= words.front().rect.left())
                continue;
            bool surfaceFound = false;
            detectedCard = cardRect(image, line, 0, image.height() - 1,
                                    line.rect.bottom(), surfaceFound);
            // 仅修复卡片标题中的缺 L：必须同时看到独立卡片底色边框、
            // 等级右侧蓝色类型图标，且该行接近卡片上沿。正文 v. 不作标题。
            const int titleInset = std::max(words.front().rect.height() * 2, detectedCard.height() / 5);
            if (!surfaceFound || line.rect.top() - detectedCard.top() > titleInset)
                continue;
            recoveredLevelPrefix = true;
            anchoredDutyTitle = true;
        }
        if (!match.hasMatch())
            continue;
        if (!anchoredDutyTitle && !words.isEmpty() && blueIcon.isValid()
            && blueIcon.left() > words.front().rect.left()) {
            bool surfaceFound = false;
            const QRect surface = cardRect(image, line, 0, image.height() - 1,
                                           line.rect.bottom(), surfaceFound);
            const int titleInset = std::max(words.front().rect.height() * 2, surface.height() / 5);
            if (surfaceFound && line.rect.top() - surface.top() <= titleInset)
                anchoredDutyTitle = true;
        }
        QString duty = match.captured(2).trimmed();
        duty.remove(sourceTimePattern());
        duty = duty.trimmed();
        headers.append({static_cast<int>(i), match.captured(1).toInt(), duty,
                        words.isEmpty() ? line.rect.left() : words.front().rect.left(), detectedCard,
                        recoveredLevelPrefix, anchoredDutyTitle});
        if (rows.size() + headers.size() > MaxCandidates)
            return fail(QStringLiteral("截图中的记录候选超过 1000 条，请分批导入。"));
    }
    // Sparse OCR may miss a visible Lv. header entirely. A full source timestamp
    // inside a separately bounded card keeps that record separate for correction.
    // The missing duty/title is not inferred from neighbouring cards.
    QList<int> timeAnchors;
    for (qsizetype i = 0; i < lines.size(); ++i) {
        if (!sourceTime(lines[i].text).isEmpty())
            timeAnchors.append(static_cast<int>(i));
        if (timeAnchors.size() > MaxCandidates)
            return fail(QStringLiteral("截图中的来源时间超过 1000 个，请分批导入。"));
    }
    for (qsizetype anchor = 0; anchor < timeAnchors.size(); ++anchor) {
        const int i = timeAnchors[anchor];
        if (inRow(lines[i]))
            continue;
        const int lower = anchor == 0 ? 0 : lines[timeAnchors[anchor - 1]].rect.bottom() + 1;
        const int upper = anchor + 1 == timeAnchors.size() ? image.height() - 1
            : lines[timeAnchors[anchor + 1]].rect.top() - 1;
        bool surfaceFound = false;
        const QRect surface = cardRect(image, lines[i], lower, upper,
                                       lines[i].rect.bottom(), surfaceFound);
        if (!surfaceFound)
            continue;
        bool alreadyRepresented = false;
        for (Header &header : headers) {
            if (surface.contains(lines[header.line].rect.center())) {
                header.detectedCard = surface;
                alreadyRepresented = true;
                break;
            }
        }
        if (alreadyRepresented)
            continue;
        int first = static_cast<int>(i);
        for (int j = 0; j < i; ++j) {
            if (surface.contains(lines[j].rect.center()) && !isChromeOrPaging(lines[j].text)) {
                first = j;
                break;
            }
        }
        int left = qRound(image.width() * 0.25);
        for (const Word &word : lines[first].words) {
            if (word.rect.left() >= image.width() * 0.16) {
                left = word.rect.left();
                break;
            }
        }
        headers.append({first, 0, {}, left, surface});
        if (rows.size() + headers.size() > MaxCandidates)
            return fail(QStringLiteral("截图中的记录候选超过 1000 条，请分批导入。"));
    }
    std::sort(headers.begin(), headers.end(), [](const Header &a, const Header &b) {
        return a.line < b.line;
    });
    if (headers.isEmpty() && rows.isEmpty())
        return fail(QStringLiteral("未找到带 Lv. 副本标题或完整来源时间的记录卡片，请检查截图或改用文件导入。"));

    const QList<JobTemplate> templates = loadJobTemplates();
    const QList<DutyNameTemplate> dutyTemplates = loadDutyNameTemplates();
    QVariantList candidates;
    candidates.reserve(headers.size());
    for (qsizetype index = 0; index < headers.size(); ++index) {
        const Header &header = headers[index];
        const Line &title = lines[header.line];
        const int endLine = index + 1 < headers.size() ? headers[index + 1].line : static_cast<int>(lines.size());
        int timeLine = -1;
        QString recordedAt;
        bool timeValid = false;
        QStringList warnings;
        if (header.recoveredLevelPrefix)
            addWarning(warnings, QStringLiteral("等级前缀识别不完整，已依据卡片和类型图标保留，请核对副本标题。"));
        for (int i = header.line; i < endLine; ++i) {
            bool valid = false;
            const QString time = sourceTime(lines[i].text, &valid);
            if (!time.isEmpty()) {
                if (timeLine >= 0)
                    addWarning(warnings, QStringLiteral("卡片内识别到多个时间，请对照原图确认来源记录时间。"));
                timeLine = i;
                recordedAt = time;
                timeValid = valid;
            }
        }
        const int lower = index == 0 ? 0 : lines[headers[index - 1].line].rect.bottom() + 1;
        const int upper = index + 1 < headers.size() ? lines[headers[index + 1].line].rect.top() - 1
                                                    : image.height() - 1;
        const int fallbackBottom = timeLine >= 0 ? lines[timeLine].rect.bottom() + title.rect.height()
                                                : upper;
        bool surfaceFound = false;
        const QRect rect = header.detectedCard.isValid() ? header.detectedCard
            : cardRect(image, title, lower, upper, fallbackBottom, surfaceFound);
        QStringList body;
        double confidenceTotal = 0;
        int confidenceWeight = 0;
        const int textLeft = header.left - std::max(2, title.rect.height() / 2);
        bool deletionOverlaps = false;
        bool pagingOverlaps = false;
        for (int i = header.line; i < endLine; ++i) {
            const Line &line = lines[i];
            if (!rect.intersects(line.rect))
                continue;
            if (isChromeOrPaging(line.text)) {
                pagingOverlaps = true;
                continue;
            }
            QList<Word> retained;
            bool hasDelete = false;
            for (const Word &word : line.words) {
                if (isDeleteWord(word, image.width())) {
                    hasDelete = true;
                    continue;
                }
                if (word.rect.left() >= textLeft) {
                    retained.append(word);
                    const int weight = std::max(1, static_cast<int>(word.text.size()));
                    confidenceTotal += word.confidence * weight;
                    confidenceWeight += weight;
                }
            }
            if (i == header.line || i == timeLine || retained.isEmpty())
                continue;
            if (timeLine >= 0 && line.rect.top() >= lines[timeLine].rect.top())
                continue;
            const QString text = joinWords(retained);
            if (!text.isEmpty()) {
                body.append(text);
                deletionOverlaps |= hasDelete;
            }
        }
        Evidence evidence;
        evidence.rect = rect;
        evidence.iconArea = rect;
        evidence.textLeft = header.left;
        evidence.duty = header.duty;
        evidence.level = header.level;
        evidence.anchoredDutyTitle = header.anchoredDutyTitle;
        evidence.recordedAt = recordedAt;
        evidence.timeValid = timeValid;
        evidence.body = body;
        evidence.confidence = confidenceWeight == 0 ? 0 : confidenceTotal / confidenceWeight;
        evidence.deletionOverlaps = deletionOverlaps;
        evidence.pagingOverlaps = pagingOverlaps;
        evidence.nearEdge = rect.bottom() >= image.height() - 1 || (surfaceFound && rect.top() == 0);
        candidates.append(candidateMap(image, evidence, warnings, templates, dutyTemplates, sourceImagePath));
    }
    if (rows.isEmpty())
        return candidates;
    for (const RowRecord &row : rows)
        candidates.append(rowCandidate(image, row, templates, dutyTemplates, sourceImagePath));
    // Web rows and any card found outside them, top to bottom.
    std::stable_sort(candidates.begin(), candidates.end(), [](const QVariant &a, const QVariant &b) {
        return a.toMap().value(QStringLiteral("source_rect")).toMap().value(QStringLiteral("y")).toInt()
            < b.toMap().value(QStringLiteral("source_rect")).toMap().value(QStringLiteral("y")).toInt();
    });
    return candidates;
}

QList<ScreenshotImportParser::RowGeometry> ScreenshotImportParser::rowFields(const QImage &image,
                                                                               const QByteArray &tsv)
{
    QList<Line> lines;
    QList<RowRecord> rows;
    if (!readInput(image, tsv, lines, rows).isEmpty())
        return {};
    QList<RowGeometry> result;
    result.reserve(rows.size());
    for (const RowRecord &row : rows)
        result.append(rowGeometry(row));
    return result;
}

} // namespace mr
