#pragma once

#include <QByteArray>
#include <QImage>
#include <QList>
#include <QRect>
#include <QString>
#include <QVariantList>

namespace mr {

/**
 * @brief 将本地 OCR 的标准 TSV 和 dlog 记录截图图像证据投影为待确认候选。
 *
 * 支持手机卡片布局与网页单行列表布局，按画面几何区分，不需要用户选择。
 *
 * 本类不启动 OCR、不联网、不写入历史，也不将来源记录时间解释为实际游戏时间。
 * 职业使用本地小型分类器；模型不可用时回退到保守模板。所有输出仍需用户核对。
 */
class ScreenshotImportParser final
{
public:
    static constexpr qsizetype MaxTsvBytes = 8 * 1024 * 1024;
    static constexpr qint64 MaxImagePixels = 50'000'000;
    static constexpr int MaxCandidates = 1000;

    /**
     * @brief 解析同一原图坐标系中的 TSV 单词行，返回按画面从上到下排列的候选。
     * @param image 已解码的原始截图；不得为空或超过 5000 万像素。
     * @param tsv 标准 12 列 TSV；最多 8 MiB，坐标必须对应 image。
     * @param sourceImagePath 供预览显示的原始本地图片路径；本函数不打开此路径。
     * @param error 可为空；输入非法、超限或无可识别卡片时写入中文原因，成功时清空。
     * @return 候选 QVariantMap 列表，最多 1000 条；失败时为空且不返回部分批次。
     *
     * 候选包含 duty_name、duty_level、reflection_text、source_recorded_at、job_id
     * （未知为 null）、source_type、source_image、source_rect（x/y/width/height）、
     * ocr_confidence、icon_confidence、icon_evidence_type、job_candidate_id/name、icon_runner_up_id/name、
     * icon_runner_up_confidence、icon_margin、needs_review 和 warnings。未确认的职业
     * 候选仅为预览证据，不能代替 job_id。来源时间不含时区；
     * 分数是 OCR/图标识别证据，不代表正确率，不能写入网络采集 confidence。
     * icon_evidence_type 区分模型分数与模板相似度。所有 needs_review 均为 true。
     */
    static QVariantList parse(const QImage &image, const QByteArray &tsv,
                              const QString &sourceImagePath, QString *error = nullptr);

    /**
     * @brief dlog 网页记录列表中一条单行记录的列范围，坐标均属原图坐标系。
     *
     * 等级、日期、时间和删除标记把行分成列：标题列在类型图标（或等级）之后、日期列之前；
     * 心得列在时间列之后、删除标记或行尾之前。title 与 body 覆盖整行文字带，可含多行。
     */
    struct RowGeometry {
        QRect row;                ///< 行底色的实际范围。
        QRect level;              ///< 等级单词框。
        QRect title;              ///< 副本名称列；没有空间时为空矩形。
        QRect time;               ///< 日期与时间单词的并集；未识别到时间时向日期右方与下方放宽。
        QRect body;               ///< 心得列；没有空间时为空矩形。
        QRect deleteMark;         ///< 按像素认出的行尾删除叉号；没有时为空矩形。分区识别时擦除它。
        bool stackedTime = false; ///< 时间列跨两行（时间在日期的下一行），分区识别按多行处理。
    };

    /**
     * @brief 从与 parse() 相同的输入中找出网页单行记录，供本地分区识别裁切各列。
     * @return 按画面从上到下排列的行几何；输入无效或画面不是单行记录布局时为空。
     *
     * 不改变 parse() 的候选字段，也不读写文件。
     */
    static QList<RowGeometry> rowFields(const QImage &image, const QByteArray &tsv);
};

} // namespace mr
