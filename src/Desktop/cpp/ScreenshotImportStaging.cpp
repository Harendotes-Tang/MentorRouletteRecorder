#include "ScreenshotImportStaging.h"

#include <QDir>
#include <QFileInfo>
#include <QJsonObject>
#include <utility>

namespace mr {
namespace {
QString imageIdentity(const QString &path)
{
    const QString canonical = QFileInfo(path).canonicalFilePath();
    const QString identity = canonical.isEmpty() ? QDir::cleanPath(path) : canonical;
#ifdef Q_OS_WIN
    return identity.toCaseFolded();
#else
    return identity;
#endif
}
}

ScreenshotSelectionResult ScreenshotImportStaging::validateSelection(
    const QStringList &chosen, const QStringList &accepted, int maxImages)
{
    QSet<QString> inBatch;
    for (const QString &path : accepted)
        inBatch.insert(imageIdentity(path));
    QSet<QString> selected;
    for (const QString &path : chosen) {
        const QString identity = imageIdentity(path);
        if (inBatch.contains(identity))
            return {.failure = ScreenshotSelectionFailure::AlreadyAccepted, .fileName = QFileInfo(path).fileName()};
        if (selected.contains(identity))
            return {.failure = ScreenshotSelectionFailure::RepeatedSelection, .fileName = QFileInfo(path).fileName()};
        selected.insert(identity);
    }
    if (chosen.size() + accepted.size() > maxImages)
        return {.failure = ScreenshotSelectionFailure::TooMany};
    return {};
}

ScreenshotImportBatch ScreenshotImportStaging::project(const QVariantList &recognized, const QSet<int> &eligibleJobs)
{
    ScreenshotImportBatch result;
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
        const bool eligible = eligibleJobs.contains(jobCandidate);
        evidence.insert(QStringLiteral("job_candidate_pending"), eligible);
        if (eligible) {
            row.insert(QStringLiteral("job_id"), jobCandidate);
            row.insert(QStringLiteral("job_name"), evidence.value(QStringLiteral("job_candidate_name")));
        }
        result.candidates.append(QJsonObject::fromVariantMap(row));
        result.evidence.append(evidence);
    }
    return result;
}

void ScreenshotImportStaging::begin(QStringList images, QString previousPhase)
{
    m_pending = {};
    m_pending.images = std::move(images);
    m_previousPhase = std::move(previousPhase);
}

void ScreenshotImportStaging::append(const QJsonArray &candidates, const QVariantList &evidence)
{
    for (const auto &candidate : candidates)
        m_pending.candidates.append(candidate);
    m_pending.evidence.append(evidence);
}

QJsonArray ScreenshotImportStaging::mergedCandidates(const QJsonArray &accepted) const
{
    QJsonArray result = accepted;
    for (const auto &candidate : m_pending.candidates)
        result.append(candidate);
    return result;
}

ScreenshotImportBatch ScreenshotImportStaging::take()
{
    auto result = std::move(m_pending);
    clear();
    return result;
}

QString ScreenshotImportStaging::discard()
{
    const QString phase = m_previousPhase.isEmpty() ? QStringLiteral("editing") : m_previousPhase;
    clear();
    return phase;
}

void ScreenshotImportStaging::clear()
{
    m_pending = {};
    m_previousPhase.clear();
}

} // namespace mr
