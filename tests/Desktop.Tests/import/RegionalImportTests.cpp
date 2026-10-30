#include "DesktopTestRunner.h"
#include "DutyCatalog.h"
#include "GameNames.h"
#include "ImportRecordsController.h"
#include "IBackend.h"

#include <QApplication>
#include <QJsonArray>
#include <QtTest>

namespace {
class RegionalImportBackend final : public mr::IBackend
{
public:
    using IBackend::IBackend;
    mr::BackendReply *pending = nullptr;
    QJsonObject payload;
    int requestCount = 0;
    QString backendName() const override { return QStringLiteral("regional-import-test"); }
    bool isConnected() const override { return true; }
    mr::BackendReply *request(const QString &type, const QJsonObject &requestPayload) override
    {
        ++requestCount;
        payload = requestPayload;
        pending = new mr::BackendReply(QStringLiteral("request"), type, this);
        return pending;
    }
};

QJsonObject previewRow(int number, const QString &region, int content = 5)
{
    const QJsonObject candidate{{"region", region}, {"content_id", content}, {"territory_id", 1331},
        {"duty_name", QStringLiteral("合成旧名称")}, {"duty_category", QStringLiteral("四人迷宫")}};
    const QJsonObject run{{"region", region}, {"content_id", content}, {"result", "COMPLETED"},
        {"contributes_to_goal", true}, {"mentor_roulette_id", 9}};
    return {{"row_number", number}, {"status", "new"}, {"can_import", true},
        {"candidate", candidate}, {"run", run}, {"errors", QJsonArray{}}, {"warnings", QJsonArray{}}};
}

void answerPreview(RegionalImportBackend &backend, const QJsonArray &rows)
{
    backend.pending->succeed({{"preview_id", "regional-preview"}, {"rows", rows},
        {"summary", QJsonObject{{"total", rows.size()}, {"new", rows.size()}}}});
}

class RegionalImportTests final : public QObject
{
    Q_OBJECT
private Q_SLOTS:
    void embeddedRealCataloguesExcludeSyntheticSamples()
    {
        const auto *catalogue = mr::DutyCatalog::shared();
        QVERIFY(catalogue->allDuties(QStringLiteral("CN")).size() > 800);
        QVERIFY(!catalogue->lookup(5, QStringLiteral("CN")).isEmpty());
        QVERIFY(catalogue->lookup(900001, QStringLiteral("CN")).isEmpty());
        QVERIFY(!catalogue->dataVersion().isEmpty());
        QCOMPARE(catalogue->allDuties(QStringLiteral("TC")).size(), 782);
    }
    void tcTerritoryAndNameUseTheirOwnCatalogue()
    {
        const auto *catalogue = mr::DutyCatalog::shared();
        const QList<QPair<int, int>> territories{{5, 172}, {13, 171}, {36, 434}, {235, 616}};
        for (const auto &[content, territory] : territories) {
            const QVariantMap duty = catalogue->lookup(content, QStringLiteral("TC"));
            QVERIFY(!duty.isEmpty());
            QCOMPARE(duty.value("territory_id").toInt(), territory);
            QCOMPARE(duty.value("region").toString(), QStringLiteral("TC"));
            const QVariantMap enriched = catalogue->enrich({{"region", "TC"}, {"content_id", content}});
            QCOMPARE(enriched.value("duty_name"), duty.value("duty_name"));
            QVERIFY(!catalogue->findByName(duty.value("duty_name").toString(), QStringLiteral("TC")).isEmpty());
            QVERIFY(catalogue->lookup(content, QStringLiteral("CN")).value("territory_id").toInt() != territory);
        }
    }

    void tcAbsenceAndAmbiguityNeverAcquireCnIdentity()
    {
        const auto *catalogue = mr::DutyCatalog::shared();
        QVERIFY(catalogue->lookup(1011, QStringLiteral("TC")).isEmpty());
        QVERIFY(!catalogue->lookup(1011, QStringLiteral("CN")).isEmpty());
        const QString name = catalogue->lookup(69, QStringLiteral("TC")).value("duty_name").toString();
        QVERIFY(!name.isEmpty());
        QCOMPARE(catalogue->lookup(944, QStringLiteral("TC")).value("duty_name").toString(), name);
        QVERIFY(catalogue->findByName(name, QStringLiteral("TC")).isEmpty());
        const QVariantMap raw{{"region", "TC"}, {"content_id", 1011}};
        QCOMPARE(catalogue->enrich(raw), raw);
        QVERIFY(catalogue->allDuties(QStringLiteral("INVALID")).isEmpty());
        QVERIFY(!catalogue->lookup(1029, QStringLiteral("TC")).isEmpty());
        QCOMPARE(catalogue->lookup(63, QStringLiteral("TC")).value("difficulty").toString(), QStringLiteral("极"));
        QVERIFY(catalogue->isExtremeTrial(63, QStringLiteral("TC")));
        QVERIFY(!catalogue->isExtremeTrial(1029, QStringLiteral("TC")));
    }

    void regionalNamesRemainDisplayOnly()
    {
        const auto *catalogue = mr::DutyCatalog::shared();
        const QString tc = catalogue->lookup(5, QStringLiteral("TC")).value("duty_name").toString();
        const QString cn = catalogue->lookup(5, QStringLiteral("CN")).value("duty_name").toString();
        QVERIFY(!tc.isEmpty());
        QCOMPARE(mr::GameNames::shared()->dutyIn(QStringLiteral("zh-CN"), 5, tc), cn);
        QCOMPARE(catalogue->lookup(5, QStringLiteral("TC")).value("duty_name").toString(), tc);
        QCOMPARE(catalogue->lookup(5, QStringLiteral("TC")).value("territory_id").toInt(), 172);
    }

    void selectedRegionsCapTheirOwnBaseline()
    {
        RegionalImportBackend backend;
        mr::ImportRecordsController controller;
        controller.setBackend(&backend);
        controller.setBaselineCount(100);
        controller.setBaselineLoaded(true);
        controller.setAchievements({QVariantMap{{"region", "CN"}, {"baseline_completed_count", 100}},
            QVariantMap{{"region", "TC"}, {"baseline_completed_count", 1}},
            QVariantMap{{"region", "UNKNOWN"}, {"baseline_completed_count", 0}}});
        controller.importText(QStringLiteral("副本\n合成副本"));
        answerPreview(backend, {previewRow(1, "CN"), previewRow(2, "TC"), previewRow(3, "TC"), previewRow(4, "UNKNOWN")});
        QTRY_VERIFY(controller.previewValid());
        QCOMPARE(controller.contributingSelectedCount(), 4);
        QCOMPARE(controller.baselineDeductionPreview(), 2);
        QVERIFY(controller.baselineChoiceOffered());
        const QVariantList previews = controller.baselinePreviews();
        QCOMPARE(previews.size(), 3);
        for (const QVariant &value : previews) {
            const auto row = value.toMap();
            if (row.value("region").toString() == QLatin1String("TC")) {
                QCOMPARE(row.value("contributing_count").toInt(), 2);
                QCOMPARE(row.value("deducted_count").toInt(), 1);
                QCOMPARE(row.value("remaining_baseline").toInt(), 0);
            }
            if (row.value("region").toString() == QLatin1String("UNKNOWN"))
                QCOMPARE(row.value("deducted_count").toInt(), 0);
        }
        controller.setRowSelected(0, false);
        QCOMPARE(controller.baselineDeductionPreview(), 1);
    }

    void reconnectWithOnlyCnSnapshotCannotCommitTcRows()
    {
        RegionalImportBackend backend;
        mr::ImportRecordsController controller;
        controller.setBackend(&backend);
        controller.setBaselineLoaded(true);
        controller.setAchievements({QVariantMap{{"region", "TC"}, {"baseline_completed_count", 7}}});
        controller.importText(QStringLiteral("副本\n合成副本"));
        answerPreview(backend, {previewRow(1, "TC")});
        QTRY_VERIFY(controller.previewValid());
        controller.setOwnRecordsConfirmed(true);
        QVERIFY(controller.canCommit());

        // The owning app invalidates per-connection knowledge at reconnect.
        controller.setBaselineLoaded(false);
        controller.setAchievements({});
        QVERIFY(controller.waitingForBaseline());
        QVERIFY(!controller.canCommit());
        controller.setAchievements({QVariantMap{{"region", "CN"}, {"baseline_completed_count", 100}}});
        controller.setBaselineLoaded(true);
        controller.setBaselineCount(100);
        QVERIFY(controller.waitingForBaseline());
        QCOMPARE(controller.baselineDeductionPreview(), -1);
        QVERIFY(!controller.baselineChoiceOffered());
        const QVariantMap missing = controller.baselinePreviews().first().toMap();
        QVERIFY(!missing.value("baseline_known").toBool());
        QVERIFY(!missing.value("baseline_count").isValid());
        QVERIFY(!missing.value("deducted_count").isValid());
        const int before = backend.requestCount;
        controller.commit();
        QCOMPARE(backend.requestCount, before);
        QVERIFY(!controller.canCommit());

        controller.setAchievements({QVariantMap{{"region", "CN"}, {"baseline_completed_count", 100}},
            QVariantMap{{"region", "TC"}, {"baseline_completed_count", 7}}});
        QVERIFY(!controller.waitingForBaseline());
        QCOMPARE(controller.baselineDeductionPreview(), 1);
        QVERIFY(controller.canCommit());
        controller.commit();
        QCOMPARE(backend.requestCount, before + 1);
        QVERIFY(backend.payload.value("deduct_from_baseline").toBool());
    }

    void legacyScalarOnlyAuthorizesCnAndExplicitZeroSnapshotIsKnown()
    {
        for (const QString &region : {QStringLiteral("CN"), QStringLiteral("TC"),
                                     QStringLiteral("GLOBAL"), QStringLiteral("UNKNOWN")}) {
            RegionalImportBackend backend;
            mr::ImportRecordsController controller;
            controller.setBackend(&backend);
            controller.setBaselineCount(100);
            controller.setBaselineLoaded(true);
            controller.importText(QStringLiteral("副本\n合成副本"));
            answerPreview(backend, {previewRow(1, region)});
            QTRY_VERIFY(controller.previewValid());
            controller.setOwnRecordsConfirmed(true);
            QCOMPARE(controller.waitingForBaseline(), region != QLatin1String("CN"));
            QCOMPARE(controller.canCommit(), region == QLatin1String("CN"));
            if (region != QLatin1String("CN")) {
                const int before = backend.requestCount;
                controller.commit();
                QCOMPARE(backend.requestCount, before);
                controller.setAchievements({QVariantMap{{"region", region}, {"baseline_completed_count", 0}}});
                QVERIFY(!controller.waitingForBaseline());
                QCOMPARE(controller.baselineDeductionPreview(), 0);
                QVERIFY(controller.canCommit());
            }
        }
    }

    void changingRegionDropsThePreviousIdentityAndChangesChoices()
    {
        RegionalImportBackend backend;
        mr::ImportRecordsController controller;
        controller.setBackend(&backend);
        controller.importText(QStringLiteral("副本\n合成副本"));
        answerPreview(backend, {previewRow(1, "CN")});
        QTRY_VERIFY(controller.previewValid());
        controller.updateCandidate(0, {{"region", "TC"}});
        const QVariantMap candidate = controller.currentCandidate();
        QCOMPARE(candidate.value("region").toString(), QStringLiteral("TC"));
        QVERIFY(!candidate.contains("content_id"));
        QVERIFY(!candidate.contains("territory_id"));
        QVERIFY(!candidate.contains("duty_category"));
        QVERIFY(!controller.previewValid());
        QVERIFY(!controller.dutyChoices().isEmpty());
        for (const QVariant &choice : controller.dutyChoices())
            QCOMPARE(choice.toMap().value("region").toString(), QStringLiteral("TC"));
    }

    void batchRegionChangesEverySelectedCandidateOnly()
    {
        RegionalImportBackend backend;
        mr::ImportRecordsController controller;
        controller.setBackend(&backend);
        controller.importText(QStringLiteral("副本\n合成副本"));
        answerPreview(backend, {previewRow(1, "CN"), previewRow(2, "GLOBAL")});
        QTRY_VERIFY(controller.previewValid());
        controller.setRowSelected(1, false);
        controller.batchUpdateSelected({{"region", "TC"}});
        const QJsonArray rows = backend.payload.value("rows").toArray();
        QCOMPARE(rows.size(), 2);
        QCOMPARE(rows[0].toObject().value("region").toString(), QStringLiteral("TC"));
        QVERIFY(!rows[0].toObject().contains("territory_id"));
        QCOMPARE(rows[1].toObject().value("region").toString(), QStringLiteral("GLOBAL"));
        QCOMPARE(rows[1].toObject().value("territory_id").toInt(), 1331);
    }
};
} // namespace

MR_DESKTOP_TEST_SUITE(RegionalImport)
{
    QApplication application(argc, argv);
    RegionalImportTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "RegionalImportTests.moc"
