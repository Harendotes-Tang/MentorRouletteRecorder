#pragma once

#include <QImage>
#include <QString>

#include <memory>

namespace mr {

/** @brief 对截图职业区域执行本地小型神经网络推理；训练和 Python 不参与导入过程。 */
class JobIconClassifier final
{
public:
    struct Result {
        bool available = false; ///< 模型已加载且标签符合当前 battleJobs；false 时可使用旧模板回退。
        int id = 0; ///< 仅分数、候选间隔和训练分布检查全部通过时为可靠职业；仍须用户核对。
        int candidateId = 0; ///< 已知输出赢时保留候选；unknown 赢或前景无效时为 0。
        double confidence = 0; ///< softmax 模型分数，不代表校准后的正确率。
        int runnerUpId = 0;
        double runnerUpConfidence = 0;
        double unknownConfidence = 0;
        double featureDistance = 0; ///< 与训练类特征均值的均方距离，供分布外拒识。
    };

    /** @brief 从固定本地资源或明确测试路径读取模型；任何结构/标签异常均保留 unavailable。 */
    explicit JobIconClassifier(
        const QString &modelPath = QStringLiteral(":/resources/models/job-icon-classifier.json"));

    bool isAvailable() const { return static_cast<bool>(m_model); }

    /** @brief 对原始裁剪区域识别；空白、白叉、蓝图标和低分区域保留未知，不触发模板回退。 */
    Result classify(const QImage &crop) const;

private:
    struct Model;
    std::shared_ptr<const Model> m_model;
};

} // namespace mr
