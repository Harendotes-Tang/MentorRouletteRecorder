#include "JobIconClassifier.h"
#include "JobCatalog.h"

#include <QFile>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QSet>
#include <QVariantMap>

#include <algorithm>
#include <array>
#include <cmath>
#include <limits>
#include <optional>
#include <vector>

static void initializeClassifierResource()
{
#ifdef MR_BUNDLE_JOB_CLASSIFIER
    static const bool initialized = [] {
        Q_INIT_RESOURCE(mr_job_classifier);
        return true;
    }();
    (void)initialized;
#endif
}

namespace {

constexpr int kFeatureCount = 257;
constexpr int kHiddenCount = 64;
constexpr int kGrid = 16;
constexpr qint64 kMaximumModelBytes = 1024 * 1024;

/** @brief 与训练脚本相同的金色前景和面积采样；排除触边背景碎片并保留离散笔画。 */
std::optional<std::array<float, kFeatureCount>> features(const QImage &input)
{
    if (input.isNull() || input.width() > 2048 || input.height() > 2048
        || static_cast<qint64>(input.width()) * input.height() > 1024 * 1024)
        return std::nullopt;
    const QImage image = input.convertToFormat(QImage::Format_ARGB32);
    const int width = image.width();
    const int height = image.height();
    std::vector<uchar> mask(static_cast<size_t>(width) * height, 0);
    std::vector<uchar> selected(mask.size(), 0);
    for (int y = 0; y < height; ++y) {
        const auto *pixels = reinterpret_cast<const QRgb *>(image.constScanLine(y));
        for (int x = 0; x < width; ++x) {
            const QRgb pixel = pixels[x];
            const int r = qRed(pixel), g = qGreen(pixel), b = qBlue(pixel);
            mask[static_cast<size_t>(y) * width + x] = qAlpha(pixel) > 100 && r > 100 && g > 75
                && r * 10 >= g * 9 && r - b > 25 && g - b > 20;
        }
    }

    int left = width, top = height, right = -1, bottom = -1, foreground = 0;
    std::vector<int> component;
    for (int index = 0; index < static_cast<int>(mask.size()); ++index) {
        if (mask[index] != 1)
            continue;
        component.clear();
        component.push_back(index);
        mask[index] = 2;
        int cl = width, ct = height, cr = -1, cb = -1;
        for (size_t position = 0; position < component.size(); ++position) {
            const int value = component[position];
            const int y = value / width, x = value % width;
            cl = std::min(cl, x); ct = std::min(ct, y);
            cr = std::max(cr, x); cb = std::max(cb, y);
            for (int dy = -1; dy <= 1; ++dy) {
                for (int dx = -1; dx <= 1; ++dx) {
                    const int nx = x + dx, ny = y + dy;
                    if ((dx == 0 && dy == 0) || nx < 0 || ny < 0 || nx >= width || ny >= height)
                        continue;
                    const int neighbour = ny * width + nx;
                    if (mask[neighbour] == 1) {
                        mask[neighbour] = 2;
                        component.push_back(neighbour);
                    }
                }
            }
        }
        if (component.size() < 3 || cl == 0 || ct == 0 || cr == width - 1 || cb == height - 1)
            continue;
        for (int value : component)
            selected[value] = 1;
        foreground += static_cast<int>(component.size());
        left = std::min(left, cl); top = std::min(top, ct);
        right = std::max(right, cr); bottom = std::max(bottom, cb);
    }
    const int glyphWidth = right - left + 1, glyphHeight = bottom - top + 1;
    if (foreground < 12 || glyphWidth < 3 || glyphHeight < 3)
        return std::nullopt;
    const double aspect = static_cast<double>(glyphWidth) / glyphHeight;
    if (aspect < 0.25 || aspect > 4)
        return std::nullopt;
    std::array<float, kFeatureCount> result{};
    for (int y = 0; y < kGrid; ++y) {
        const int yl = y * glyphHeight, yh = (y + 1) * glyphHeight;
        for (int x = 0; x < kGrid; ++x) {
            const int xl = x * glyphWidth, xh = (x + 1) * glyphWidth;
            double total = 0;
            for (int sy = yl / kGrid; sy < (yh + kGrid - 1) / kGrid; ++sy) {
                const int wy = std::min(yh, (sy + 1) * kGrid) - std::max(yl, sy * kGrid);
                for (int sx = xl / kGrid; sx < (xh + kGrid - 1) / kGrid; ++sx) {
                    const int wx = std::min(xh, (sx + 1) * kGrid) - std::max(xl, sx * kGrid);
                    total += selected[static_cast<size_t>(top + sy) * width + left + sx] * wx * wy;
                }
            }
            result[y * kGrid + x] = static_cast<float>(total / (glyphWidth * glyphHeight));
        }
    }
    result.back() = static_cast<float>(std::log(aspect));
    return result;
}

bool readWeights(const QJsonObject &weights, const QString &key, int count, std::vector<float> &output)
{
    const QJsonArray values = weights.value(key).toArray();
    if (values.size() != count)
        return false;
    output.reserve(count);
    for (const QJsonValue &value : values) {
        if (!value.isDouble())
            return false;
        const double number = value.toDouble();
        if (!std::isfinite(number) || std::abs(number) > 1000)
            return false;
        output.push_back(static_cast<float>(number));
    }
    return true;
}

bool boundedThreshold(const QJsonObject &object, const QString &key, double minimum, double maximum,
                      double &result)
{
    const QJsonValue value = object.value(key);
    if (!value.isDouble())
        return false;
    result = value.toDouble();
    return std::isfinite(result) && result >= minimum && result <= maximum;
}

} // namespace

namespace mr {

struct JobIconClassifier::Model {
    std::vector<int> ids;
    std::vector<float> inputHidden, hiddenBias, hiddenOutput, outputBias, centers;
    double confidenceThreshold = 1, marginThreshold = 1, maxDistance = 0;
};

JobIconClassifier::JobIconClassifier(const QString &modelPath)
{
    initializeClassifierResource();
    QFile file(modelPath);
    if (!file.open(QIODevice::ReadOnly) || file.size() <= 0 || file.size() > kMaximumModelBytes)
        return;
    const QByteArray payload = file.read(kMaximumModelBytes + 1);
    if (payload.size() > kMaximumModelBytes || file.error() != QFileDevice::NoError)
        return;
    QJsonParseError error{};
    const QJsonObject json = QJsonDocument::fromJson(payload, &error).object();
    if (error.error != QJsonParseError::NoError
        || json.value(QStringLiteral("format")).toString() != QLatin1String("mr-job-icon-mlp-v1")
        || json.value(QStringLiteral("feature_format")).toString() != QLatin1String("gold-area16-v1")
        || json.value(QStringLiteral("input_count")).toInt() != kFeatureCount
        || json.value(QStringLiteral("hidden_count")).toInt() != kHiddenCount)
        return;
    const QJsonArray classIds = json.value(QStringLiteral("class_ids")).toArray();
    if (classIds.size() < 2 || classIds.size() > 64)
        return;
    QSet<int> eligible;
    const JobCatalog catalogue;
    for (const QVariant &job : catalogue.battleJobs())
        eligible.insert(job.toMap().value(QStringLiteral("job_id")).toInt());
    auto model = std::make_shared<Model>();
    QSet<int> labels;
    for (const QJsonValue &value : classIds) {
        const double number = value.toDouble(-1);
        if (!value.isDouble() || !std::isfinite(number) || number < 0 || number > 10000
            || number != std::floor(number) || labels.contains(static_cast<int>(number)))
            return;
        const int id = static_cast<int>(number);
        if (id != 0 && !eligible.contains(id))
            return;
        labels.insert(id);
        model->ids.push_back(id);
    }
    labels.remove(0);
    if (model->ids.front() != 0 || labels != eligible)
        return;
    if (!boundedThreshold(json, QStringLiteral("confidence_threshold"), 0.97, 1, model->confidenceThreshold)
        || !boundedThreshold(json, QStringLiteral("margin_threshold"), 0.1, 1, model->marginThreshold)
        || !boundedThreshold(json, QStringLiteral("max_feature_distance"), 0.001, 0.25, model->maxDistance))
        return;
    const int classes = static_cast<int>(model->ids.size());
    const QJsonObject weights = json.value(QStringLiteral("weights")).toObject();
    if (!readWeights(weights, QStringLiteral("input_hidden"), kFeatureCount * kHiddenCount, model->inputHidden)
        || !readWeights(weights, QStringLiteral("hidden_bias"), kHiddenCount, model->hiddenBias)
        || !readWeights(weights, QStringLiteral("hidden_output"), kHiddenCount * classes, model->hiddenOutput)
        || !readWeights(weights, QStringLiteral("output_bias"), classes, model->outputBias)
        || !readWeights(weights, QStringLiteral("feature_centers"), kFeatureCount * classes, model->centers))
        return;
    m_model = std::move(model);
}

JobIconClassifier::Result JobIconClassifier::classify(const QImage &crop) const
{
    Result result;
    result.available = isAvailable();
    if (!m_model)
        return result;
    const auto input = features(crop);
    if (!input)
        return result;
    const Model &model = *m_model;
    std::array<double, kHiddenCount> hidden{};
    for (int h = 0; h < kHiddenCount; ++h) {
        double value = model.hiddenBias[h];
        for (int f = 0; f < kFeatureCount; ++f)
            value += (*input)[f] * model.inputHidden[f * kHiddenCount + h];
        hidden[h] = std::max(0.0, value);
    }
    const int classes = static_cast<int>(model.ids.size());
    std::vector<double> scores(classes, 0);
    for (int c = 0; c < classes; ++c) {
        scores[c] = model.outputBias[c];
        for (int h = 0; h < kHiddenCount; ++h)
            scores[c] += hidden[h] * model.hiddenOutput[h * classes + c];
    }
    const double maximum = *std::max_element(scores.cbegin(), scores.cend());
    double total = 0;
    for (double &value : scores) {
        value = std::exp(value - maximum);
        total += value;
    }
    if (!std::isfinite(total) || total <= 0)
        return result;
    for (double &value : scores)
        value /= total;
    result.unknownConfidence = scores.front();
    std::vector<int> order(classes);
    for (int c = 0; c < classes; ++c)
        order[c] = c;
    std::stable_sort(order.begin(), order.end(), [&](int a, int b) { return scores[a] > scores[b]; });
    const int best = order[0], runner = order[1];
    if (best == 0)
        return result;
    result.candidateId = model.ids[best];
    result.confidence = scores[best];
    result.runnerUpId = model.ids[runner];
    result.runnerUpConfidence = scores[runner];
    for (int f = 0; f < kFeatureCount; ++f) {
        const double difference = (*input)[f] - model.centers[best * kFeatureCount + f];
        result.featureDistance += difference * difference;
    }
    result.featureDistance /= kFeatureCount;
    if (result.confidence >= model.confidenceThreshold
        && result.confidence - result.runnerUpConfidence >= model.marginThreshold
        && result.featureDistance <= model.maxDistance)
        result.id = result.candidateId;
    return result;
}

} // namespace mr
