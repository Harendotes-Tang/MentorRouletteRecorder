#include "ScreenshotOcrText.h"

#include <QCoreApplication>
#include <QDate>
#include <QMap>
#include <QRegularExpression>
#include <QTime>

#include <algorithm>
#include <array>
#include <cmath>

namespace mr::screenshot {

bool isHan(const QChar character)
{
    const auto script = character.script();
    return script == QChar::Script_Han || script == QChar::Script_Hiragana
        || script == QChar::Script_Katakana;
}

} // namespace mr::screenshot

namespace {

using namespace mr::screenshot;

bool needsWordSpace(const Word &left, const Word &right)
{
    if (left.text.isEmpty() || right.text.isEmpty())
        return false;
    const QChar last = left.text.back();
    const QChar first = right.text.front();
    if (isHan(last) || isHan(first) || last.isSpace() || first.isSpace())
        return false;
    if (QStringLiteral(".,:;!?，。：；！？)]}）】」》").contains(first)
        || QStringLiteral("([{（【「《").contains(last))
        return false;
    // 日期和时刻是两个完整结构字段，检测框即使相邻或重叠也须保留分隔。
    // 只接受整块日期 + 整块时刻，不能借时间格式给普通数字正文补空格。
    static const QRegularExpression datePart(QStringLiteral(R"(^\d{4}\s*[-－]\s*\d{2}\s*[-－]\s*\d{2}$)"));
    static const QRegularExpression timePart(QStringLiteral(R"(^\d{2}\s*[:：]\s*\d{2}\s*[:：]\s*\d{2}$)"));
    if (datePart.match(left.text).hasMatch() && timePart.match(right.text).hasMatch())
        return true;
    // 稀疏检测可能把同一个 ASCII 单词或颜文字中的数字拆成相邻框。
    // 只有原图框间存在明显词间距时才补空格，不能仅凭两个字母/数字相遇就插入。
    const int gap = right.rect.left() - left.rect.right() - 1;
    const int spacing = std::max(2, std::min(left.rect.height(), right.rect.height()) / 5);
    return last.isLetterOrNumber() && first.isLetterOrNumber() && gap > spacing;
}

void finishLine(Line &line)
{
    std::ranges::sort(line.words, [](const Word &a, const Word &b) {
        return a.rect.left() < b.rect.left();
    });
    line.rect = {};
    for (const Word &word : std::as_const(line.words))
        line.rect = line.rect.united(word.rect);
    line.text = joinWords(line.words);
}

/** @brief 按纵坐标排序 OCR 行片段，并把纵向大半重叠的片段合并为同一物理行。 */
QList<Line> mergeFragments(QList<Line> fragments)
{
    std::ranges::sort(fragments, [](const Line &a, const Line &b) {
        if (a.rect.center().y() != b.rect.center().y())
            return a.rect.center().y() < b.rect.center().y();
        return a.rect.left() < b.rect.left();
    });
    QList<Line> lines;
    for (const Line &fragment : std::as_const(fragments)) {
        if (!lines.isEmpty()) {
            Line &previous = lines.back();
            const int overlap = std::min(previous.rect.bottom(), fragment.rect.bottom())
                - std::max(previous.rect.top(), fragment.rect.top()) + 1;
            if (overlap * 2 >= std::min(previous.rect.height(), fragment.rect.height())) {
                previous.words.append(fragment.words);
                previous.rect = previous.rect.united(fragment.rect);
                continue;
            }
        }
        lines.append(fragment);
    }
    for (Line &line : lines)
        finishLine(line);
    return lines;
}

} // namespace

namespace mr::screenshot {

QString joinWords(const QList<Word> &words)
{
    QString out;
    const Word *previous = nullptr;
    for (const Word &word : words) {
        if (previous && needsWordSpace(*previous, word))
            out += QLatin1Char(' ');
        out += word.text;
        previous = &word;
    }
    return out.trimmed();
}

const QRegularExpression &sourceTimePattern()
{
    static const QRegularExpression expression(QStringLiteral(
        R"((?<!\d)(\d{4})\s*[-－]\s*(\d{2})\s*[-－]\s*(\d{2})\s+(\d{2})\s*[:：]\s*(\d{2})\s*[:：]\s*(\d{2})(?!\d))"));
    return expression;
}

QString sourceTime(const QString &text, bool *valid)
{
    const QRegularExpressionMatch match = sourceTimePattern().match(text);
    if (!match.hasMatch()) {
        if (valid)
            *valid = false;
        return {};
    }
    const QDate date(match.captured(1).toInt(), match.captured(2).toInt(), match.captured(3).toInt());
    const QTime time(match.captured(4).toInt(), match.captured(5).toInt(), match.captured(6).toInt());
    if (valid)
        *valid = date.isValid() && time.isValid();
    return match.captured(1) + QLatin1Char('-') + match.captured(2) + QLatin1Char('-')
        + match.captured(3) + QLatin1Char(' ') + match.captured(4) + QLatin1Char(':')
        + match.captured(5) + QLatin1Char(':') + match.captured(6);
}

bool isChromeOrPaging(const QString &text)
{
    static const QRegularExpression controls(QStringLiteral(
        R"((?:dlog\s*\.\s*luyulight\s*\.\s*cn|共\s*\d+\s*条记录|\d+\s*/\s*page|\bof\s*\d+|ICP\s*备|^\s*[<>‹›〈〉]+\s*$))"),
        QRegularExpression::CaseInsensitiveOption);
    return controls.match(text).hasMatch();
}

/**
 * @brief 校验 TSV 数值和坐标，按 OCR 行分组后合并同一物理行的稀疏文字块。
 * lines 供手机卡片使用，舍弃左侧水印区的单词；allLines（可为空）保留全部单词，
 * 供网页单行记录使用——其等级、类型图标与副本名称按像素固定，可能落在比例截止线左侧。
 */
bool readLines(const QByteArray &tsv, const QSize &imageSize, QList<Line> &lines, QString &error,
               QList<Line> *allLines)
{
    QMap<QString, Line> grouped;
    const QList<QByteArray> rows = tsv.split('\n');
    if (rows.isEmpty() || !rows.front().startsWith("level\tpage_num\t")) {
        error = QCoreApplication::translate("mr::ScreenshotOcrText", "图片识别结果格式不正确，请重新识别。");
        return false;
    }

    int wordCount = 0;
    for (qsizetype rowIndex = 1; rowIndex < rows.size(); ++rowIndex) {
        QByteArray row = rows[rowIndex];
        if (row.endsWith('\r'))
            row.chop(1);
        if (row.trimmed().isEmpty())
            continue;
        const QList<QByteArray> columns = row.split('\t');
        if (columns.size() != 12) {
            error = QCoreApplication::translate("mr::ScreenshotOcrText", "图片识别结果内容不完整，请重新识别。");
            return false;
        }
        bool levelOk = false;
        const int level = columns[0].toInt(&levelOk);
        if (!levelOk || level < 1 || level > 5) {
            error = QCoreApplication::translate("mr::ScreenshotOcrText", "图片识别结果中的文字分组异常，请重新识别。");
            return false;
        }
        if (level != 5)
            continue;

        std::array<int, 4> geometry{};
        for (int i = 0; i < 4; ++i) {
            bool ok = false;
            geometry[i] = columns[6 + i].toInt(&ok);
            if (!ok || geometry[i] < 0) {
                error = QCoreApplication::translate("mr::ScreenshotOcrText", "图片识别结果中的文字位置异常，请重新识别。");
                return false;
            }
        }
        if (geometry[2] == 0 || geometry[3] == 0)
            continue;
        if (static_cast<qint64>(geometry[0]) + geometry[2] > imageSize.width()
            || static_cast<qint64>(geometry[1]) + geometry[3] > imageSize.height()) {
            error = QCoreApplication::translate("mr::ScreenshotOcrText", "识别出的文字位置超出原图范围，请重新识别。");
            return false;
        }
        bool confidenceOk = false;
        const double rawConfidence = columns[10].toDouble(&confidenceOk);
        if (!confidenceOk || !std::isfinite(rawConfidence)
            || rawConfidence < -1 || rawConfidence > 100) {
            error = QCoreApplication::translate("mr::ScreenshotOcrText", "图片识别结果中的文字评分异常，请重新识别。");
            return false;
        }
        const QString text = QString::fromUtf8(columns[11]).trimmed();
        if (text.isEmpty())
            continue;
        // A bounded number of words also bounds sorting and image matching work.
        if (++wordCount > 100'000) {
            error = QCoreApplication::translate("mr::ScreenshotOcrText", "截图中的文字过多，请裁切图片，只保留记录区域后重试。");
            return false;
        }
        const QString key = QString::fromLatin1(columns[1] + '/' + columns[2] + '/'
                                               + columns[3] + '/' + columns[4]);
        grouped[key].words.append({.rect = QRect(geometry[0], geometry[1], geometry[2], geometry[3]),
                                   .text = text, .confidence = std::max(0.0, rawConfidence) / 100.0});
    }

    QList<Line> fragments;
    QList<Line> allFragments;
    fragments.reserve(grouped.size());
    for (const Line &group : std::as_const(grouped)) {
        if (allLines) {
            Line all = group;
            finishLine(all);
            allFragments.append(std::move(all));
        }
        // Left watermark glyphs are image evidence, not text. Keeping their large
        // OCR boxes out of physical-row merging prevents them joining body lines.
        Line line;
        for (const Word &word : group.words) {
            if (word.rect.left() >= imageSize.width() * 0.16)
                line.words.append(word);
        }
        if (line.words.isEmpty())
            continue;
        finishLine(line);
        fragments.append(std::move(line));
    }
    lines = mergeFragments(std::move(fragments));
    if (allLines)
        *allLines = mergeFragments(std::move(allFragments));
    return true;
}

} // namespace mr::screenshot
