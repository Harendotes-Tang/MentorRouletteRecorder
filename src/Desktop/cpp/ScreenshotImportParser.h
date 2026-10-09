#pragma once

#include <QByteArray>
#include <QImage>
#include <QString>
#include <QVariantList>

namespace mr {

/**
 * @brief 将本地 Tesseract TSV 的 dlog 手机卡片文字与图像证据投影为待确认候选。
 *
 * 本类不启动 OCR、不联网、不写入历史，也不将来源记录时间解释为实际游戏时间。
 * 职业仅在本地模板形状匹配足够明确时提供候选；所有输出仍需用户核对。
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
     * @param tsv Tesseract 的标准 12 列 TSV；最多 8 MiB，坐标必须对应 image。
     * @param sourceImagePath 供预览显示的原始本地图片路径；本函数不打开此路径。
     * @param error 可为空；输入非法、超限或无可识别卡片时写入中文原因，成功时清空。
     * @return 候选 QVariantMap 列表，最多 1000 条；失败时为空且不返回部分批次。
     *
     * 候选包含 duty_name、duty_level、reflection_text、source_recorded_at、job_id
     * （未知为 null）、source_type、source_image、source_rect（x/y/width/height）、
     * ocr_confidence、icon_confidence、job_candidate_id/name、icon_runner_up_id/name、
     * icon_runner_up_confidence、icon_margin、needs_review 和 warnings。未确认的职业
     * 候选仅为预览证据，不能代替 job_id。来源时间不含时区；
     * 分数是 OCR/图标识别证据，不能写入网络采集 confidence。所有 needs_review 均为 true。
     */
    static QVariantList parse(const QImage &image, const QByteArray &tsv,
                              const QString &sourceImagePath, QString *error = nullptr);
};

} // namespace mr
