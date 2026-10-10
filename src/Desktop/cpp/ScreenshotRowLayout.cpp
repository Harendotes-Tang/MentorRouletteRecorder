#include "ScreenshotRowLayout.h"

#include <QRegularExpression>

#include <algorithm>
#include <array>
#include <cmath>

namespace mr::screenshot {

QList<Component> components(std::vector<uchar> mask, int width, int height, bool eightConnected)
{
    QList<Component> result;
    std::vector<int> pending;
    for (int start = 0; start < width * height; ++start) {
        if (!mask[size_t(start)])
            continue;
        mask[size_t(start)] = 0;
        pending.assign(1, start);
        QRect bounds(start % width, start / width, 1, 1);
        for (size_t next = 0; next < pending.size(); ++next) {
            const int px = pending[next] % width;
            const int py = pending[next] / width;
            bounds = bounds.united(QRect(px, py, 1, 1));
            // Edge neighbours first, in the scan order OfflineOcrEngine has always used.
            static const std::array<QPoint, 8> offsets{QPoint(-1, 0), QPoint(1, 0), QPoint(0, -1), QPoint(0, 1),
                                                       QPoint(-1, -1), QPoint(1, -1), QPoint(-1, 1), QPoint(1, 1)};
            for (int k = 0; k < (eightConnected ? 8 : 4); ++k) {
                const QPoint offset = offsets[size_t(k)];
                const int nx = px + offset.x();
                const int ny = py + offset.y();
                if (nx >= 0 && ny >= 0 && nx < width && ny < height && mask[size_t(ny * width + nx)]) {
                    mask[size_t(ny * width + nx)] = 0;
                    pending.push_back(ny * width + nx);
                }
            }
        }
        result.append({bounds, pending});
    }
    return result;
}

bool isDiagonalCross(const std::vector<int> &points, int width, const QRect &bounds, double tolerance)
{
    int diagonal = 0;
    std::array<int, 4> corners{};
    for (const int position : points) {
        const double dx = double(position % width - bounds.left()) / (bounds.width() - 1);
        const double dy = double(position / width - bounds.top()) / (bounds.height() - 1);
        diagonal += std::abs(dx - dy) <= tolerance || std::abs(dx + dy - 1.0) <= tolerance;
        if (std::abs(dx - 0.5) > 0.25 && std::abs(dy - 0.5) > 0.25)
            ++corners[size_t((dy > 0.5 ? 2 : 0) + (dx > 0.5 ? 1 : 0))];
    }
    return diagonal >= points.size() * 0.85
        && std::all_of(corners.begin(), corners.end(), [](int count) { return count >= 3; });
}

} // namespace mr::screenshot

namespace {

using namespace mr::screenshot;

/**
 * @brief 网页单行记录的等级标记：L 可能漏识别，分隔符可能识别为点、全角点、冒号或逗号；
 * 数字中的 O、l、I、| 是 OCR 常见的形近误读。至少须有一位真数字，除非 L、v 与分隔符都在。
 * maxLevel 为副本目录的最高等级（0 表示不检查）：超出它且以形近字母开头时去掉这个字母，
 * 如「Lvl90」读作 90。用到形近读取时 misreadDigits 为真。
 */
bool levelToken(const QString &text, int maxLevel = 0, int *level = nullptr, bool *misreadDigits = nullptr)
{
    static const QRegularExpression expression(QStringLiteral(
        R"(^([Ll]\s*)?[Vv]\s*([.。．:,，])?\s*([\dOoIl|]{1,3})$)"));
    const QRegularExpressionMatch match = expression.match(text);
    if (!match.hasMatch())
        return false;
    const QString original = match.captured(3);
    const auto isDigit = [](QChar character) { return character.isDigit(); };
    const bool fullPrefix = !match.captured(1).isEmpty() && !match.captured(2).isEmpty();
    if (!fullPrefix && std::none_of(original.cbegin(), original.cend(), isDigit))
        return false;
    const auto value = [](QString digits) {
        digits.replace(QLatin1Char('O'), QLatin1Char('0')).replace(QLatin1Char('o'), QLatin1Char('0'));
        digits.replace(QLatin1Char('I'), QLatin1Char('1')).replace(QLatin1Char('l'), QLatin1Char('1'))
            .replace(QLatin1Char('|'), QLatin1Char('1'));
        return digits.toInt();
    };
    int result = value(original);
    if (maxLevel > 0 && result > maxLevel && original.size() > 1 && !original.front().isDigit())
        result = value(original.mid(1));
    if (level)
        *level = result;
    if (misreadDigits)
        *misreadDigits = !std::all_of(original.cbegin(), original.cend(), isDigit);
    return true;
}

/** @brief 自 start 起取 1–3 个紧邻单词组成等级标记，返回所用单词数；不是等级时返回 0。 */
qsizetype levelHead(const QList<Word> &words, qsizetype start = 0)
{
    qsizetype adjacent = 1;
    while (adjacent < 3 && start + adjacent < words.size()
           && words[start + adjacent].rect.left() - words[start + adjacent - 1].rect.right()
               <= std::max(2, words[start + adjacent - 1].rect.height() / 2))
        ++adjacent;
    for (qsizetype count = std::min(adjacent, words.size() - start); count >= 1; --count) {
        if (levelToken(joinWords(words.mid(start, count))))
            return count;
    }
    return 0;
}

/** @brief 网页单行记录的日期列：只要求年份与第一个连字符，月日误读交给时间校验与分区重读。 */
const QRegularExpression &datePrefixPattern()
{
    static const QRegularExpression expression(QStringLiteral(R"(^\d{4}\s*[-－])"));
    return expression;
}

/** @brief 自 index 向上下扫描，返回中心落在 [top, bottom] 内的行号；行已按中心纵坐标排序。 */
QList<int> linesBetween(const QList<Line> &lines, int index, int top, int bottom)
{
    int first = index;
    while (first > 0 && lines[first - 1].rect.center().y() >= top)
        --first;
    int last = index;
    while (last + 1 < lines.size() && lines[last + 1].rect.center().y() <= bottom)
        ++last;
    QList<int> result;
    for (int i = first; i <= last; ++i) {
        if (lines[i].rect.center().y() >= top && lines[i].rect.center().y() <= bottom)
            result.append(i);
    }
    return result;
}

/**
 * @brief 等级右侧、与等级同处一条文字带（上下各放宽一个字高）的最左日期单词。
 * 日期还须与等级或某个副本名称单词同在一行文字上：手机卡片的页脚日期即使紧贴标题，
 * 也只与时间同行，不与等级或副本名称同行。
 */
const Word *rowDate(const QList<Line> &lines, const QList<int> &nearby, const QRect &level)
{
    const Word *date = nullptr;
    const int reach = level.height();
    for (int i : nearby) {
        for (const Word &word : lines[i].words) {
            if (word.rect.left() <= level.right() || !datePrefixPattern().match(word.text).hasMatch()
                || word.rect.top() > level.bottom() + reach || word.rect.bottom() < level.top() - reach
                || (date && word.rect.left() >= date->rect.left()))
                continue;
            bool sharesLine = overlapsVertically(word.rect, level);
            for (int j : nearby) {
                for (const Word &duty : lines[j].words) {
                    sharesLine |= duty.rect.center().x() > level.right() && duty.rect.center().x() < word.rect.left()
                        && overlapsVertically(word.rect, duty.rect);
                }
            }
            if (sharesLine)
                date = &word;
        }
    }
    return date;
}

/** @brief 沿两列留白上下扫描行底色；两列须是同一种深色，且行高超过两个字高并容下等级与日期。 */
QRect surfaceAlong(const QImage &image, const std::array<int, 2> &columns, const QRect &level, const QRect &date)
{
    const int h = level.height();
    const int y = level.center().y();
    const QRgb surface = image.pixel(columns[0], y);
    // White text on a dark row must not become the row surface.
    if (std::max({qRed(surface), qGreen(surface), qBlue(surface)}) > 210)
        return {};
    const auto matches = [&](int row) {
        return std::all_of(columns.cbegin(), columns.cend(), [&](int x) {
            return colorDistance(image.pixel(x, row), surface) < 45;
        });
    };
    if (!matches(y))
        return {};
    int top = y;
    while (top > 0 && matches(top - 1))
        --top;
    int bottom = y;
    while (bottom + 1 < image.height() && matches(bottom + 1))
        ++bottom;
    if ((top == 0 && bottom == image.height() - 1) || bottom - top <= h * 2
        || top > std::min(level.top(), date.top()) || bottom < std::max(level.bottom(), date.bottom()))
        return {};
    return QRect(0, top, image.width(), bottom - top + 1);
}

/**
 * @brief 等级与日期所在的行底色：在等级左侧的留白（职业图标与等级之间）取两列上下扫描；
 * 截图从职业图标中间裁起、左侧留白不足或被占用时，改用等级右侧（与类型图标之间）的留白。
 * 两列都不能落在任何识别单词的框内。不在固定比例的位置取样——长心得与长副本名可能盖住所有比例位置。
 * 截图可能从某行中间开始或结束，行底色可以贴着上缘或下缘，但不能同时贴着两者。
 */
QRect rowSurface(const QImage &image, const QRect &level, const QRect &date, const QList<Word> &nearby)
{
    const int h = level.height();
    const int inner = std::max(3, h / 2);
    const std::array<std::array<int, 2>, 2> strips{{{level.left() - inner, level.left() - h},
                                                    {level.right() + inner, level.right() + h}}};
    for (const std::array<int, 2> &columns : strips) {
        const bool covered = std::any_of(nearby.cbegin(), nearby.cend(), [&columns](const Word &word) {
            return std::any_of(columns.cbegin(), columns.cend(), [&word](int x) {
                return word.rect.left() <= x && x <= word.rect.right();
            });
        });
        if (std::min(columns[0], columns[1]) < 0 || std::max(columns[0], columns[1]) >= image.width() || covered)
            continue;
        const QRect surface = surfaceAlong(image, columns, level, date);
        if (surface.isValid())
            return surface;
    }
    return {};
}

/**
 * @brief 在行上沿与文字带之间的空白处量出行底色的实际左右边界；行可伸出截图右缘。
 * 扫描线取两者中点，空白够高时至少在行上沿之下一个字高处，避开圆角对左右两端的裁减。
 */
QRect rowExtent(const QImage &image, const QRect &surface, const QRect &level, int textTop, QRgb &colour)
{
    const int middle = surface.top() + std::max(1, (textTop - surface.top()) / 2);
    const int belowCorners = std::min(textTop - 1, surface.top() + level.height());
    const int y = std::min(surface.bottom(), std::max(middle, belowCorners));
    const int anchor = level.center().x();
    colour = image.pixel(anchor, y);
    int left = 0;
    while (left < anchor && colorDistance(image.pixel(left, y), colour) >= 45)
        ++left;
    // Compare with a pixel some way back so a gradient surface is followed but
    // an anti-aliased or shadowed edge into the page background is not.
    int right = anchor;
    while (right + 1 < image.width()
           && colorDistance(image.pixel(right + 1, y), image.pixel(std::max(anchor, right - 15), y)) < 45)
        ++right;
    return QRect(QPoint(left, surface.top()), QPoint(right, surface.bottom()));
}

/** @brief 职业图标取样起点：越过行左端圆角的抗锯齿像素，它们会被当成金色笔画。 */
QRect rowIconArea(const QImage &image, const QRect &row, const QRect &level, QRgb surface)
{
    // matchIcon trims the same vertical inset; the rounded corner is widest there.
    const int y = row.top() + std::max(1, row.height() / 20);
    int left = row.left();
    while (left < level.left() && colorDistance(image.pixel(left, y), surface) >= 45)
        ++left;
    return QRect(QPoint(left + 1, row.top()), row.bottomRight());
}

/** @brief 类型图标像素：饱和彩色且与行底色明显不同；白字、灰字和底色都不是图标。 */
bool isTypeIconPixel(QRgb pixel, QRgb surface)
{
    const int high = std::max({qRed(pixel), qGreen(pixel), qBlue(pixel)});
    const int low = std::min({qRed(pixel), qGreen(pixel), qBlue(pixel)});
    return high - low >= 60 && high >= 100 && colorDistance(pixel, surface) > 60;
}

/**
 * @brief 等级与日期列之间的彩色类型图标（蓝、青、红等任意色相）：离等级最近、大致方形、
 * 边长在半个到三个字高之间的彩色连通块，并入与之相接的图标内部图案。
 * 次像素彩边、零散色点与日期的抗锯齿边缘形状不符，不会把图标范围拉到日期。
 */
QRect typeIconRect(const QImage &image, const QRect &area, QRgb surface, int glyph)
{
    const QRect search = area.isValid() ? area.intersected(image.rect()) : QRect();
    if (search.isEmpty())
        return {};
    std::vector<uchar> mask(size_t(search.width()) * size_t(search.height()), 0);
    for (int y = 0; y < search.height(); ++y) {
        for (int x = 0; x < search.width(); ++x)
            mask[size_t(y) * search.width() + x] = isTypeIconPixel(image.pixel(search.left() + x, search.top() + y), surface);
    }
    const QList<Component> parts = components(std::move(mask), search.width(), search.height(), false);
    const auto iconShaped = [glyph](const QRect &rect) {
        const int side = std::max(rect.width(), rect.height());
        return rect.width() * 2 >= rect.height() && rect.height() * 2 >= rect.width()
            && side >= std::max(6, glyph / 2) && side <= glyph * 3;
    };
    QRect icon;
    for (const Component &part : parts) {
        if (part.points.size() >= 12 && iconShaped(part.bounds) && (!icon.isValid() || part.bounds.left() < icon.left()))
            icon = part.bounds;
    }
    for (bool grown = icon.isValid(); grown;) {
        grown = false;
        for (const Component &part : parts) {
            const QRect united = icon.united(part.bounds);
            if (united != icon && part.bounds.intersects(icon.adjusted(-2, -2, 2, 2)) && iconShaped(united)) {
                icon = united;
                grown = true;
            }
        }
    }
    return icon.isValid() ? icon.translated(search.topLeft()) : QRect();
}

/** @brief OCR 把类型图标读成方形单词、且框内大半是图标彩色时排除；彩色描边的汉字不在此列。 */
bool isTypeIconWord(const Word &word, const QImage &image, QRgb surface)
{
    if (word.rect.width() < word.rect.height() * 0.6 || word.rect.width() > word.rect.height() * 1.5)
        return false;
    int colored = 0;
    int count = 0;
    const int stepX = std::max(1, word.rect.width() / 16);
    const int stepY = std::max(1, word.rect.height() / 16);
    for (int y = word.rect.top(); y <= word.rect.bottom(); y += stepY) {
        for (int x = word.rect.left(); x <= word.rect.right(); x += stepX) {
            colored += isTypeIconPixel(image.pixel(x, y), surface);
            ++count;
        }
    }
    return count > 0 && static_cast<double>(colored) / count >= 0.4;
}

/** @brief 日期右侧紧邻的单词（同一行）或日期正下方的单词（换行）组成时间；删除叉号不属于时间。 */
QList<Word> rowTimeWords(const QList<Word> &words, const Word &date, bool &stacked)
{
    stacked = false;
    if (!sourceTime(date.text).isEmpty())
        return {};
    QList<Word> sorted;
    for (const Word &word : words) {
        if (!isDeleteText(word.text.trimmed()))
            sorted.append(word);
    }
    std::sort(sorted.begin(), sorted.end(), [](const Word &a, const Word &b) {
        return a.rect.left() < b.rect.left();
    });
    // Columns are separated by much more than the space between date and time.
    const int gap = std::max(4, date.rect.height());
    const auto extend = [&](QList<Word> chain, QRect reach) {
        for (const Word &word : std::as_const(sorted)) {
            if (chain.size() >= 2)
                break;
            if (word.rect.top() <= reach.bottom() && word.rect.bottom() >= reach.top()
                && word.rect.center().x() > reach.right() && word.rect.left() - reach.right() <= gap) {
                chain.append(word);
                reach = word.rect;
            }
        }
        return chain;
    };
    const QList<Word> sameLine = extend({}, date.rect);
    if (!sameLine.isEmpty())
        return sameLine;
    const Word *below = nullptr;
    for (const Word &word : std::as_const(sorted)) {
        if (word.rect.top() > date.rect.center().y() && word.rect.top() - date.rect.bottom() <= gap * 2
            && word.rect.left() <= date.rect.right() && word.rect.right() >= date.rect.left()
            && (!below || word.rect.top() < below->rect.top()))
            below = &word;
    }
    if (!below)
        return {};
    stacked = true;
    return extend({*below}, below->rect);
}

/**
 * @brief 按纵向重叠把同一列的单词分成画面上的文字行（不依赖 OCR 行号），行内自左向右。
 * 每个单词只与该行第一个单词比较，一个上下伸出的高识别框不会把相邻两行连起来。
 */
QList<QList<Word>> visualLines(QList<Word> words)
{
    std::sort(words.begin(), words.end(), [](const Word &a, const Word &b) {
        return a.rect.center().y() < b.rect.center().y();
    });
    QList<QList<Word>> lines;
    QRect seed;
    for (const Word &word : std::as_const(words)) {
        const int overlap = std::min(seed.bottom(), word.rect.bottom())
            - std::max(seed.top(), word.rect.top()) + 1;
        if (!lines.isEmpty() && overlap * 2 >= std::min(seed.height(), word.rect.height())) {
            lines.back().append(word);
        } else {
            lines.append({word});
            seed = word.rect;
        }
    }
    for (QList<Word> &line : lines) {
        std::sort(line.begin(), line.end(), [](const Word &a, const Word &b) {
            return a.rect.left() < b.rect.left();
        });
    }
    return lines;
}

/** @brief 行内的删除叉号单词：在时间列右侧、紧靠行的实际右缘（约四个字高内），与截图宽度无关。 */
bool isRowDeleteWord(const Word &word, const RowRecord &row)
{
    return word.rect.center().x() > row.timeColumn.right() && isDeleteText(word.text.trimmed())
        && row.rect.right() - word.rect.right() <= row.levelRect.height() * 4;
}

/**
 * @brief 行尾删除叉号的像素证据：亮色细笔画组成的叉，距行的实际右缘不超过四个字高。
 * 笔画抗锯齿、只有中心是纯白，按三个通道都不低于 128 取亮色；细笔画只在对角方向相连，按八连通取块。
 * 叉号可能与左侧末字相连，所以只从连通块的右缘定位：最右几列里笔画的上下两端给出方框，
 * 方框两条对角线上的采样点合计至少八成五附近有笔画，上、下、右三边的中段须为空
 * （叉号在这些位置没有笔画，「义」「叉」与实心块都有）。
 */
QRect rowDeleteMarkPixels(const QImage &image, const RowRecord &row)
{
    const int glyph = row.levelRect.height();
    const int middle = row.levelRect.center().y();
    const QRect search = QRect(QPoint(row.rect.right() - glyph * 5, middle - glyph * 5 / 2),
                               QPoint(row.rect.right(), middle + glyph * 5 / 2))
                             .intersected(row.rect).intersected(image.rect());
    if (glyph <= 0 || search.isEmpty())
        return {};
    const int width = search.width();
    const int height = search.height();
    std::vector<uchar> mask(size_t(width) * size_t(height), 0);
    for (int y = 0; y < height; ++y) {
        for (int x = 0; x < width; ++x) {
            const QRgb pixel = image.pixel(search.left() + x, search.top() + y);
            mask[size_t(y) * width + x] = isBrightStroke(pixel);
        }
    }
    const auto light = [&](int x, int y) {
        return x >= 0 && y >= 0 && x < width && y < height && mask[size_t(y) * width + x];
    };
    const int smallest = std::max(8, glyph / 2);
    QRect best;
    for (const Component &part : components(mask, width, height, true)) {
        // The stroke ends in the rightmost columns are the mark's top and bottom.
        const int right = part.bounds.right();
        int top = height;
        int bottom = -1;
        for (const int position : part.points) {
            if (position % width >= right - smallest / 2) {
                top = std::min(top, position / width);
                bottom = std::max(bottom, position / width);
            }
        }
        const int side = bottom - top + 1;
        if (side < smallest || side > glyph * 5 / 2 || right - side + 1 < 0)
            continue;
        const QRect box(right - side + 1, top, side, side);
        const CrossEvidence cross = crossEvidence(box, light);
        if ((cross.first + cross.second) * 20 < side * 2 * 17 || !cross.sidesEmpty)
            continue;
        const QRect mark = box.translated(search.topLeft());
        if (row.rect.right() - mark.right() <= glyph * 4 && (!best.isValid() || mark.left() > best.left()))
            best = mark;
    }
    return best;
}

/**
 * @brief 叉号位置上的识别单词不是正文：中心或大半落在叉号上的去掉（框伸出叉号较多时它也盖住了心得字，
 * 提示可能遮挡）；只是末字压到叉号的，若末字是叉号的常见误读则去掉末字并提示。
 */
QList<Word> withoutDeleteMark(const QList<Word> &words, const QRect &mark, RowRecord &row)
{
    if (!mark.isValid())
        return words;
    const int h = row.levelRect.height();
    QList<Word> kept;
    for (Word word : words) {
        const QRect overlap = word.rect.intersected(mark);
        if (overlap.isEmpty()) {
            kept.append(word);
            continue;
        }
        if (mark.adjusted(-1, -1, 1, 1).contains(word.rect.center())
            || qint64(overlap.width()) * overlap.height() * 2 >= qint64(word.rect.width()) * word.rect.height()) {
            row.deletionOverlaps |= word.rect.left() < mark.left() - h / 2 || word.rect.right() > mark.right() + h / 2;
            continue;
        }
        // A word overlapping the button touches it, whatever its last glyph.
        const QString text = word.text.trimmed();
        if (text.size() > 1 && isCrossLookalike(text.back()))
            word.text = text.chopped(1).trimmed();
        row.deletionOverlaps = true;
        kept.append(word);
    }
    return kept;
}

/** @brief 日期与紧随其后（同一行或下一行）的时间组成时间列，并读取来源记录时间。 */
QList<Word> assignTimeColumn(const QList<Word> &all, const Word &date, RowRecord &row)
{
    QList<Word> timeWords = rowTimeWords(all, date, row.stackedTime);
    row.recordedAt = sourceTime(timeWords.isEmpty() ? date.text : date.text + QLatin1Char(' ') + joinWords(timeWords),
                                &row.timeValid);
    timeWords.prepend(date);
    row.timeColumn = {};
    for (const Word &word : std::as_const(timeWords))
        row.timeColumn = row.timeColumn.united(word.rect);
    return timeWords;
}

/** @brief 方框内画着叉号：两条对角线各至少六成附近有亮色笔画，三边中段为空。义、久、二等真字都不满足。 */
bool drawsCross(const QImage &image, const QRect &box)
{
    const CrossEvidence cross = crossEvidence(box, [&image](int x, int y) {
        return image.rect().contains(x, y) && isBrightStroke(image.pixel(x, y));
    });
    return cross.first * 5 >= cross.samples * 3 && cross.second * 5 >= cross.samples * 3 && cross.sidesEmpty;
}

/**
 * @brief 整体叉号检测没认出时的兜底（认出时不再调用），只看行尾最后一个心得单词（距行右缘约四个字高内）：
 * 单独一个方形、约一个字高的 ×、✕、X、x 整个去掉；义、乂、叉、久、二本身是常用字，只有字框里画着叉号时才去掉。
 * 紧贴前一个字时提示遮挡。叉号被并进末字时，末尾的 ×、✕ 直接去掉；末尾的 X、x 也可能是字母（如「thx」），
 * 只有单词最右一个字高见方的范围里画着叉号时才去掉。去掉末字时提示遮挡。行内其余位置的同形字不受影响。
 */
void stripFusedDeleteMark(const QImage &image, QList<Word> &notes, RowRecord &row)
{
    if (notes.isEmpty())
        return;
    const int h = row.levelRect.height();
    const auto last = std::max_element(notes.begin(), notes.end(), [](const Word &a, const Word &b) {
        return a.rect.right() < b.rect.right();
    });
    if (row.rect.right() - last->rect.right() > h * 4)
        return;
    const QString text = last->text.trimmed();
    const QRect box = last->rect;
    if (text.size() == 1 && isCrossLookalike(text.front()) && box.width() * 5 >= box.height() * 3
        && box.height() * 5 >= box.width() * 3 && std::max(box.width(), box.height()) * 2 <= h * 3
        && (isDeleteText(text) || drawsCross(image, box))) {
        const bool touching = std::any_of(notes.cbegin(), notes.cend(), [&](const Word &word) {
            return word.rect != box && overlapsVertically(word.rect, box) && word.rect.right() < box.left()
                && box.left() - word.rect.right() < h / 2;
        });
        notes.erase(last);
        row.deletionOverlaps |= touching;
        return;
    }
    if (text.size() < 2 || !isDeleteText(text.right(1)))
        return;
    const QChar end = text.back();
    if ((end == QLatin1Char('X') || end == QLatin1Char('x'))
        && !drawsCross(image, QRect(QPoint(std::max(box.left(), box.right() - h + 1), box.top()), box.bottomRight())))
        return;
    last->text = text.left(text.size() - 1).trimmed();
    row.deletionOverlaps = true;
}

/** @brief 粗识别漏掉时间时，时间分区向日期右方（到下一个单词之前）和整行文字带下方放宽。 */
void widenMissingTime(const QList<Word> &all, const Word &date, RowRecord &row)
{
    row.timeCrop = row.timeColumn;
    if (row.timeColumn != date.rect || !sourceTime(date.text).isEmpty())
        return;
    // Two gaps before the next word: the field pass pads this crop by about one more.
    const int gap = std::max(3, date.rect.height() / 3);
    int right = date.rect.right() + date.rect.width() * 85 / 100;
    for (const Word &word : all) {
        if (word.rect.left() > date.rect.right() && overlapsVertically(word.rect, date.rect))
            right = std::min(right, word.rect.left() - gap * 2);
    }
    row.timeCrop = QRect(QPoint(date.rect.left(), date.rect.top()),
                         QPoint(std::max(date.rect.right(), right), std::max(date.rect.bottom(), row.textBand.bottom())));
    row.stackedTime = row.timeCrop.height() > date.rect.height() * 8 / 5;
}

/** @brief 时间列右侧最左的删除叉号单词。 */
QRect rowDeleteRect(const QList<Word> &all, const RowRecord &row)
{
    QRect result;
    for (const Word &word : all) {
        if (isRowDeleteWord(word, row) && (!result.isValid() || word.rect.left() < result.left()))
            result = word.rect;
    }
    return result;
}

/** @brief 心得列逐行成文；分页文字只作覆盖提示，贴近删除叉号的心得提示可能遮挡。返回采用的单词。 */
QList<Word> assignNoteLines(const QList<Word> &notes, RowRecord &row)
{
    QList<Word> used;
    const int reach = std::max(row.deleteRect.width(), row.levelRect.height());
    for (const QList<Word> &note : visualLines(notes)) {
        const QString text = joinWords(note);
        if (text.isEmpty())
            continue;
        if (isChromeOrPaging(text)) {
            row.pagingOverlaps = true;
            continue;
        }
        row.note.append(text);
        for (const Word &word : note) {
            used.append(word);
            // Within reach of the button on either side (a note may run past it).
            const int apart = std::max({row.deleteRect.left() - word.rect.right(), word.rect.left() - row.deleteRect.right(), 0});
            row.deletionOverlaps |= row.deleteRect.isValid() && apart <= reach
                && word.rect.top() <= row.deleteRect.bottom() + reach / 2
                && word.rect.bottom() >= row.deleteRect.top() - reach / 2;
        }
    }
    return used;
}

/** @brief 采用单词按字数加权的平均置信度与整行文字带。 */
void summariseRowText(const QList<Word> &used, RowRecord &row)
{
    double total = 0;
    int weight = 0;
    for (const Word &word : used) {
        const int size = std::max(1, static_cast<int>(word.text.size()));
        total += word.confidence * size;
        weight += size;
        row.textBand = row.textBand.united(word.rect);
    }
    row.confidence = weight == 0 ? 0 : total / weight;
}

/** @brief 把行内单词按列归入等级、副本名称、时间和心得；删除标记和类型图标不是文字。 */
void assignRowColumns(const QImage &image, const QList<Line> &lines, const QList<int> &rowLines,
                      const Word &date, QRgb surface, RowRecord &row)
{
    QList<Word> all;
    for (int i : rowLines)
        all.append(lines[i].words);
    QList<Word> used = assignTimeColumn(all, date, row);
    const int h = row.levelRect.height();
    const QRect iconSearch(QPoint(row.levelRect.right() + 1, row.levelRect.center().y() - 2 * h),
                           QPoint(row.timeColumn.left() - 1, row.levelRect.center().y() + 2 * h));
    row.iconRect = typeIconRect(image, iconSearch.isValid() ? iconSearch.intersected(row.rect) : QRect(), surface, h);
    row.deleteMark = rowDeleteMarkPixels(image, row);
    all = withoutDeleteMark(all, row.deleteMark, row);
    row.deleteRect = row.deleteMark.isValid() ? row.deleteMark : rowDeleteRect(all, row);
    const qsizetype timeCount = used.size();
    QList<Word> duty;
    QList<Word> notes;
    for (const Word &word : std::as_const(all)) {
        const int x = word.rect.center().x();
        const bool timeWord = std::any_of(used.cbegin(), used.cbegin() + timeCount, [&](const Word &time) {
            return time.rect == word.rect && time.text == word.text;
        });
        if (isRowDeleteWord(word, row) || x < row.levelRect.left() || timeWord)
            continue;
        if (row.levelRect.contains(word.rect.center()))
            used.append(word);
        else if (x > row.levelRect.right() && x < row.timeColumn.left())
            duty.append(word);
        else if (x > row.timeColumn.right())
            notes.append(word);
    }
    duty.erase(std::remove_if(duty.begin(), duty.end(), [&](const Word &word) {
        return row.iconRect.adjusted(-2, -2, 2, 2).contains(word.rect.center()) || isTypeIconWord(word, image, surface);
    }), duty.end());
    for (const QList<Word> &line : visualLines(duty))
        row.duty.append(line);
    used.append(row.duty);
    if (!row.deleteMark.isValid())
        stripFusedDeleteMark(image, notes, row);
    used.append(assignNoteLines(notes, row));
    summariseRowText(used, row);
    widenMissingTime(all, date, row);
}

/**
 * @brief 物理行中的等级标记：自左数第一组能组成等级的单词。它左侧只允许职业图标被读出的杂字——
 * 与等级相隔至少一个字高，且不含汉字或字框明显高于等级；正文中途出现的「Lv.70」不是行首等级。
 * @return 等级起始下标，count 为所用单词数；不是网页单行记录的等级行时返回 -1。
 */
qsizetype levelRunStart(const QList<Word> &words, qsizetype &count)
{
    for (qsizetype start = 0; start < words.size(); ++start) {
        count = levelHead(words, start);
        if (count == 0)
            continue;
        const QRect run = words[start].rect;
        const bool onlyIconsBefore = std::all_of(words.cbegin(), words.cbegin() + start, [&run](const Word &word) {
            const bool han = std::any_of(word.text.cbegin(), word.text.cend(), isHan);
            return run.left() - word.rect.right() >= run.height()
                && (!han || word.rect.height() * 2 > run.height() * 3);
        });
        return onlyIconsBefore ? start : -1;
    }
    return -1;
}

/** @brief 行首等级右侧、同一行底色内有日期时，该行是网页单行记录；返回 line 为 -1 表示不是。 */
RowRecord rowRecord(const QImage &image, const QList<Line> &lines, int index, int maxLevel)
{
    const QList<Word> &words = lines[index].words;
    qsizetype head = 0;
    const qsizetype start = levelRunStart(words, head);
    if (start < 0)
        return {};
    RowRecord row;
    for (qsizetype i = start; i < start + head; ++i)
        row.levelRect = row.levelRect.united(words[i].rect);
    const int h = row.levelRect.height();
    const QList<int> nearby = linesBetween(lines, index, row.levelRect.top() - 3 * h,
                                           row.levelRect.bottom() + 3 * h);
    const Word *date = rowDate(lines, nearby, row.levelRect);
    // A watermark glyph read as a level token is far taller than the text.
    if (!date || h > date->rect.height() * 2)
        return {};
    QList<Word> nearbyWords;
    for (int i : nearby)
        nearbyWords.append(lines[i].words);
    const QRect surface = rowSurface(image, row.levelRect, date->rect, nearbyWords);
    if (!surface.isValid())
        return {};
    const QList<int> rowLines = linesBetween(lines, index, surface.top(), surface.bottom());
    // A web row has one level. Another Lv word left of the date column on
    // another text line means a mobile card whose body starts with a level.
    static const QRegularExpression levelPrefix(QStringLiteral(R"(^\s*[Ll]\s*[Vv]\s*[.。．:]?\s*\d)"));
    // The extent is measured above all of the row's text: a centred row's
    // note may start higher than its level and date.
    int textTop = std::min(row.levelRect.top(), date->rect.top());
    for (int i : rowLines) {
        for (const Word &word : lines[i].words) {
            if (word.rect.left() < date->rect.left() && !overlapsVertically(word.rect, row.levelRect)
                && levelPrefix.match(word.text).hasMatch())
                return {};
            if (word.rect.left() >= row.levelRect.left())
                textTop = std::min(textTop, word.rect.top());
        }
    }
    QRgb colour = 0;
    row.rect = rowExtent(image, surface, row.levelRect, textTop, colour);
    row.iconArea = rowIconArea(image, row.rect, row.levelRect, colour);
    const QString token = joinWords(words.mid(start, head));
    row.line = index;
    levelToken(token, maxLevel, &row.level, &row.misreadLevelDigits);
    row.recoveredLevelPrefix = !token.startsWith(QLatin1Char('L'), Qt::CaseInsensitive);
    assignRowColumns(image, lines, rowLines, *date, colour, row);
    return row;
}

} // namespace

namespace mr::screenshot {

QList<RowRecord> rowRecords(const QImage &image, const QList<Line> &lines, int maxLevel)
{
    QList<RowRecord> rows;
    for (int i = 0; i < lines.size() && rows.size() <= ScreenshotImportParser::MaxCandidates; ++i) {
        // Rows are found top to bottom and never overlap, so only the last one
        // can hold this line (for example a wrapped second line of the row).
        if (!rows.isEmpty() && rows.back().rect.contains(lines[i].rect.center()))
            continue;
        RowRecord row = rowRecord(image, lines, i, maxLevel);
        if (row.line >= 0)
            rows.append(std::move(row));
    }
    return rows;
}

ScreenshotImportParser::RowGeometry rowGeometry(const RowRecord &row)
{
    const int gap = std::max(3, row.levelRect.height() / 3);
    const QRect band = row.textBand.adjusted(0, -gap, 0, gap).intersected(row.rect);
    int titleLeft = row.levelRect.right() + gap;
    if (row.iconRect.isValid()) {
        titleLeft = std::max(titleLeft, row.iconRect.right() + gap);
    } else if (!row.duty.isEmpty()) {
        int dutyLeft = row.duty.front().rect.left();
        for (const Word &word : row.duty)
            dutyLeft = std::min(dutyLeft, word.rect.left());
        titleLeft = std::max(titleLeft, dutyLeft - gap);
    }
    // A delete mark seen in the pixels is erased before the note is re-read, so
    // the note may continue past it; an OCR-only mark ends the note column.
    const int bodyRight = row.deleteRect.isValid() && !row.deleteMark.isValid() ? row.deleteRect.left() - 1
                                                                               : row.rect.right() - gap;
    // A column without room stays empty; Qt would normalise an inverted
    // rectangle into a strip of the neighbouring columns when intersected.
    const auto column = [](const QRect &rect) { return rect.isValid() ? rect : QRect(); };
    ScreenshotImportParser::RowGeometry geometry;
    geometry.row = row.rect;
    geometry.level = row.levelRect;
    geometry.title = column(QRect(QPoint(titleLeft, band.top()), QPoint(row.timeColumn.left() - gap, band.bottom())));
    geometry.time = row.timeCrop;
    geometry.body = column(QRect(QPoint(row.timeCrop.right() + gap, band.top()), QPoint(bodyRight, band.bottom())));
    geometry.deleteMark = row.deleteMark;
    geometry.stackedTime = row.stackedTime;
    return geometry;
}

} // namespace mr::screenshot
