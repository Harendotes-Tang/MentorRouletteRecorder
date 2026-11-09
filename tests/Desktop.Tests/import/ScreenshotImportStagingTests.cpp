#include "DesktopTestRunner.h"
#include "ScreenshotImportStaging.h"

#include <QCoreApplication>
#include <QJsonObject>
#include <QtTest>

namespace {
class ScreenshotImportStagingTests final : public QObject
{
    Q_OBJECT
private Q_SLOTS:
    void discardingAppendLeavesAcceptedInputUntouched()
    {
        const QJsonArray accepted{QJsonObject{{"note", "accepted"}}};
        mr::ScreenshotImportStaging staging;
        staging.begin({QStringLiteral("second.png")}, QStringLiteral("complete"));
        staging.append({QJsonObject{{"note", "pending"}}}, {QVariantMap{{"source_image", "second.png"}}});
        QCOMPARE(staging.mergedCandidates(accepted).size(), 2);
        QCOMPARE(staging.discard(), QStringLiteral("complete"));
        QVERIFY(!staging.active());
        QCOMPARE(staging.mergedCandidates(accepted), accepted);
        QCOMPARE(accepted.first().toObject().value("note").toString(), QStringLiteral("accepted"));
    }

    void acceptanceTransfersPendingDataOnce()
    {
        mr::ScreenshotImportStaging staging;
        staging.begin({QStringLiteral("second.png")}, QStringLiteral("editing"));
        const QJsonArray candidates{QJsonObject{{"note", "pending"}}};
        const QVariantList evidence{QVariantMap{{"source_image", "second.png"}}};
        staging.append(candidates, evidence);
        const auto accepted = staging.take();
        QCOMPARE(accepted.images, QStringList{QStringLiteral("second.png")});
        QCOMPARE(accepted.candidates, candidates);
        QCOMPARE(accepted.evidence, evidence);
        QVERIFY(!staging.active());
        QVERIFY(staging.take().candidates.isEmpty());
    }

    void separatesEvidenceAndOnlyPrefillsEligibleJobCandidates()
    {
        const QVariantList rows{
            QVariantMap{{"duty_name", "source"}, {"source_image", "a.png"}, {"source_rect", "rect"},
                        {"job_candidate_id", 19}, {"job_candidate_name", "PLD"}, {"icon_margin", 0.9}},
            QVariantMap{{"job_candidate_id", 99}, {"job_candidate_name", "ineligible"}}};
        const auto batch = mr::ScreenshotImportStaging::project(rows, {19});
        QCOMPARE(batch.candidates.size(), 2);
        const auto first = batch.candidates[0].toObject();
        QCOMPARE(first.value("job_id").toInt(), 19);
        QVERIFY(!first.contains("source_image"));
        QVERIFY(!first.contains("icon_margin"));
        QVERIFY(!first.contains("matched_at_utc"));
        QCOMPARE(batch.evidence[0].toMap().value("source_image").toString(), QStringLiteral("a.png"));
        QVERIFY(batch.evidence[0].toMap().value("job_candidate_pending").toBool());
        QVERIFY(!batch.candidates[1].toObject().contains("job_id"));
        QVERIFY(!batch.evidence[1].toMap().value("job_candidate_pending").toBool());
    }

    void validatesDuplicatesBeforeCapacityWithoutChangingStaging()
    {
        using Failure = mr::ScreenshotSelectionFailure;
        QCOMPARE(mr::ScreenshotImportStaging::validateSelection({"a.png"}, {"a.png"}, 1).failure,
                 Failure::AlreadyAccepted);
        QCOMPARE(mr::ScreenshotImportStaging::validateSelection({"b.png", "b.png"}, {}, 1).failure,
                 Failure::RepeatedSelection);
        QCOMPARE(mr::ScreenshotImportStaging::validateSelection({"b.png"}, {"a.png"}, 1).failure,
                 Failure::TooMany);
        QCOMPARE(mr::ScreenshotImportStaging::validateSelection({"b.png"}, {"a.png"}, 2).failure,
                 Failure::None);
    }
};
}

MR_DESKTOP_QTEST_SUITE(ScreenshotImportStaging, ScreenshotImportStagingTests, QCoreApplication)
#include "ScreenshotImportStagingTests.moc"
