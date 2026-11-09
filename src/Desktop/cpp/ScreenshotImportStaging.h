#pragma once

#include <QJsonArray>
#include <QSet>
#include <QStringList>
#include <QVariantList>

namespace mr {

struct ScreenshotImportBatch
{
    QStringList images;
    QJsonArray candidates;
    QVariantList evidence;
};

enum class ScreenshotSelectionFailure { None, AlreadyAccepted, RepeatedSelection, TooMany };
struct ScreenshotSelectionResult
{
    ScreenshotSelectionFailure failure = ScreenshotSelectionFailure::None;
    QString fileName;
};

/// Owns only an unaccepted screenshot append. The accepted batch stays in its session owner.
/// No controller or UI dependency: merging returns copies, take transfers once, discard restores
/// only the caller's previous phase and cannot change its accepted candidates or selection.
class ScreenshotImportStaging final
{
public:
    static ScreenshotSelectionResult validateSelection(const QStringList &chosen, const QStringList &accepted, int maxImages);
    /// Separates Desktop evidence from Collector candidates without inventing game-event fields.
    static ScreenshotImportBatch project(const QVariantList &recognized, const QSet<int> &eligibleJobs);

    bool active() const { return !m_pending.images.isEmpty(); }
    qsizetype candidateCount() const { return m_pending.candidates.size(); }
    const QStringList &images() const { return m_pending.images; }
    void begin(QStringList images, QString previousPhase);
    void append(const QJsonArray &candidates, const QVariantList &evidence);
    QJsonArray mergedCandidates(const QJsonArray &accepted) const;
    ScreenshotImportBatch take();
    QString discard();
    void clear();

private:
    ScreenshotImportBatch m_pending;
    QString m_previousPhase;
};

} // namespace mr
