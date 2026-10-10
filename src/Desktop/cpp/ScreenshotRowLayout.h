#pragma once

#include "ScreenshotImportParser.h"
#include "ScreenshotOcrText.h"

#include <QImage>
#include <QList>
#include <QRect>
#include <QString>
#include <QStringList>

#include <algorithm>
#include <vector>

/**
 * 截图导入的内部接口，仅供 ScreenshotImportParser 与 OfflineOcrEngine 使用：
 * 网页单行记录的识别，以及与手机卡片、OfflineOcrEngine 共用的像素工具。
 * OCR 单词与行模型及文字工具见 ScreenshotOcrText.h。
 */
namespace mr::screenshot {

/** @brief 网页单行记录：等级、日期、时间与删除标记划分出的各列及其文字证据。 */
struct RowRecord {
    int line = -1;
    int level = 0;
    bool recoveredLevelPrefix = false;
    bool misreadLevelDigits = false;
    QRect rect;      ///< 行底色的实际范围。
    QRect iconArea;  ///< 职业图标取样范围，已避开圆角边缘。
    QRect levelRect;
    QRect iconRect;
    QRect timeColumn;
    QRect timeCrop;  ///< 时间分区范围；粗识别漏掉时间时向日期右方与下方放宽。
    bool stackedTime = false;
    QRect deleteRect;
    QRect deleteMark; ///< 按像素认出的删除叉号。
    QRect textBand;
    QList<Word> duty;
    QStringList note;
    QString recordedAt;
    bool timeValid = false;
    double confidence = 0;
    bool deletionOverlaps = false;
    bool pagingOverlaps = false;
};

/** @brief 掩码中的一个连通块：外接框与按扫描、广度优先顺序排列的像素下标（掩码坐标）。 */
struct Component {
    QRect bounds;
    std::vector<int> points;
};

// 像素颜色差，定义于 ScreenshotImportParser.cpp。
int colorDistance(QRgb a, QRgb b);

// 像素工具，定义于 ScreenshotRowLayout.cpp；OfflineOcrEngine 的手机卡片删除叉号检测同用。
/** @brief 按扫描顺序取出掩码中的连通块：eightConnected 为假时只按上下左右相连。 */
QList<Component> components(std::vector<uchar> mask, int width, int height, bool eightConnected);
/** @brief 连通块是否由两条对角线组成；tolerance 是归一化坐标中离对角线的容许偏差。 */
bool isDiagonalCross(const std::vector<int> &points, int width, const QRect &bounds, double tolerance);

// 网页单行记录的小工具与叉号判定，ScreenshotRowLayout.cpp 使用。
/** @brief 两个矩形在纵向上有重叠。 */
inline bool overlapsVertically(const QRect &a, const QRect &b)
{
    return a.top() <= b.bottom() && a.bottom() >= b.top();
}

/** @brief 删除按钮常被识别成的单个字符。 */
inline bool isDeleteText(const QString &text)
{
    return text == QLatin1String("X") || text == QLatin1String("x")
        || text == QStringLiteral("×") || text == QStringLiteral("✕");
}

/** @brief OCR 常把细笔画的删除叉号读成的字形。 */
inline bool isCrossLookalike(QChar character)
{
    return QStringLiteral("×✕Xx义乂叉久二").contains(character);
}

/** @brief 删除叉号的亮色笔画：抗锯齿细笔画只有中心是纯白，三个通道都不低于 128 即算。 */
inline bool isBrightStroke(QRgb pixel)
{
    return std::min({qRed(pixel), qGreen(pixel), qBlue(pixel)}) >= 128;
}

/** @brief 方框两条对角线上各有多少采样点（共 samples 个）附近有亮色笔画，以及上、下、右三边中段是否几乎为空。 */
struct CrossEvidence {
    int samples = 0;
    int first = 0;  ///< 左上到右下。
    int second = 0; ///< 左下到右上。
    bool sidesEmpty = false;
};

/** @brief light(x, y) 判断单个像素是否为亮色笔画（越界返回 false）。叉号的三边中段没有笔画。 */
template<typename Light>
CrossEvidence crossEvidence(const QRect &box, Light light)
{
    CrossEvidence evidence;
    const int width = box.width();
    const int height = box.height();
    if (width < 2 || height < 2)
        return evidence;
    const auto lightNear = [&light](int x, int y) {
        for (int dy = -1; dy <= 1; ++dy) {
            for (int dx = -1; dx <= 1; ++dx) {
                if (light(x + dx, y + dy))
                    return true;
            }
        }
        return false;
    };
    evidence.samples = std::max(width, height);
    for (int i = 0; i < evidence.samples; ++i) {
        const int x = box.left() + i * (width - 1) / (evidence.samples - 1);
        const int y = i * (height - 1) / (evidence.samples - 1);
        evidence.first += lightNear(x, box.top() + y);
        evidence.second += lightNear(x, box.bottom() - y);
    }
    const auto emptyEnough = [&light](const QRect &area) {
        int bright = 0;
        for (int y = area.top(); y <= area.bottom(); ++y) {
            for (int x = area.left(); x <= area.right(); ++x)
                bright += light(x, y);
        }
        return bright * 5 <= area.width() * area.height();
    };
    const int band = std::max(2, std::min(width, height) / 5);
    evidence.sidesEmpty = emptyEnough(QRect(box.left() + width / 3, box.top(), width / 3, band))
        && emptyEnough(QRect(box.left() + width / 3, box.bottom() - band + 1, width / 3, band))
        && emptyEnough(QRect(box.right() - band + 1, box.top() + height / 3, band, height / 3));
    return evidence;
}

// 网页单行记录，定义于 ScreenshotRowLayout.cpp。
/**
 * @brief 自上而下找出全部网页单行记录；lines 须含左侧水印区的单词。
 * maxLevel 为副本目录的最高等级，用于纠正形近误读的等级（0 表示不检查）。
 */
QList<RowRecord> rowRecords(const QImage &image, const QList<Line> &lines, int maxLevel);
/** @brief 一条记录各列在原图中的范围，供分区识别裁切。 */
ScreenshotImportParser::RowGeometry rowGeometry(const RowRecord &row);

} // namespace mr::screenshot
