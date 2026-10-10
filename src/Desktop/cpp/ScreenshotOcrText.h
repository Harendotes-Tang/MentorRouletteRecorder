#pragma once

#include <QByteArray>
#include <QList>
#include <QRect>
#include <QRegularExpression>
#include <QSize>
#include <QString>

/**
 * 截图导入的内部接口：Tesseract TSV 的单词与物理行模型，以及手机卡片与网页单行记录共用的文字工具。
 * 仅供 ScreenshotImportParser 与 ScreenshotRowLayout 使用。
 */
namespace mr::screenshot {

struct Word {
    QRect rect;
    QString text;
    double confidence = 0;
};

struct Line {
    QList<Word> words;
    QRect rect;
    QString text;
};

/** @brief 汉字、平假名与片假名；它们之间拼接时不加空格。 */
bool isHan(QChar character);
/** @brief 按阅读顺序拼接单词，只在两个字母或数字之间加空格。 */
QString joinWords(const QList<Word> &words);
/** @brief 完整来源时间（日期与时分秒）的正则表达式。 */
const QRegularExpression &sourceTimePattern();
/** @brief 文字中的完整来源时间（yyyy-MM-dd HH:mm:ss）；valid 报告日期与时间是否真实存在。没有时返回空。 */
QString sourceTime(const QString &text, bool *valid = nullptr);
/** @brief 浏览器地址、分页与备案等页面控件文字。 */
bool isChromeOrPaging(const QString &text);
/**
 * @brief 校验 TSV 数值和坐标，按 OCR 行分组后合并同一物理行的稀疏文字块。
 * lines 供手机卡片使用，舍弃左侧水印区的单词；allLines（可为空）保留全部单词，供网页单行记录使用。
 * 输入非法时返回 false 并写入中文原因。
 */
bool readLines(const QByteArray &tsv, const QSize &imageSize, QList<Line> &lines, QString &error,
               QList<Line> *allLines = nullptr);

} // namespace mr::screenshot
