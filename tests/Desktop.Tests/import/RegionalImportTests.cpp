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
        // The shared name resolves to the row the TC data prefers, as on CN (audit 2026-11-03
        // H2-6), and to a TC row, never to the CN identity.
        const QVariantMap byName = catalogue->findByName(name, QStringLiteral("TC"));
        QCOMPARE(byName.value("content_id").toInt(), 944);
        QCOMPARE(byName.value("region").toString(), QStringLiteral("TC"));
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

    // Audit 2026-11-03 I3-3 / L2-5: the result line names each region as the preview did
    // (国服 / 繁中服 / 未知区服), never by its wire code.
    void completedImportNamesRegionsAsThePreviewDid()
    {
        RegionalImportBackend backend;
        mr::ImportRecordsController controller;
        controller.setBackend(&backend);
        controller.setBaselineLoaded(true);
        controller.setAchievements({QVariantMap{{"region", "CN"}, {"baseline_completed_count", 100}},
            QVariantMap{{"region", "TC"}, {"baseline_completed_count", 1}},
            QVariantMap{{"region", "UNKNOWN"}, {"baseline_completed_count", 3}}});
        controller.importText(QStringLiteral("副本\n合成副本"));
        answerPreview(backend, {previewRow(1, "CN"), previewRow(2, "TC"), previewRow(3, "UNKNOWN")});
        QTRY_VERIFY(controller.previewValid());
        QStringList shown;
        for (const QVariant &value : controller.baselinePreviews())
            shown.append(value.toMap().value("label").toString());
        QCOMPARE(shown, (QStringList{QStringLiteral("国服"), QStringLiteral("繁中服"), QStringLiteral("未知区服")}));
        controller.setOwnRecordsConfirmed(true);
        controller.setDeductFromBaseline(true);
        controller.commit();
        backend.pending->succeed({{"imported_count", 3}, {"duplicate_count", 0}, {"conflict_count", 0},
            {"baseline_deducted_count", 3},
            {"baseline_adjustments", QJsonArray{
                QJsonObject{{"region", "CN"}, {"baseline_deducted_count", 1}, {"baseline_completed_count", 99}},
                QJsonObject{{"region", "TC"}, {"baseline_deducted_count", 1}, {"baseline_completed_count", 0}},
                QJsonObject{{"region", "UNKNOWN"}, {"baseline_deducted_count", 1}, {"baseline_completed_count", 2}}}}});
        QCOMPARE(controller.phase(), QStringLiteral("complete"));
        const QString status = controller.statusText();
        QVERIFY2(status.contains(QStringLiteral("国服：扣除 1 次，起始次数改为 99")), qPrintable(status));
        QVERIFY2(status.contains(QStringLiteral("繁中服：扣除 1 次，起始次数改为 0")), qPrintable(status));
        QVERIFY2(status.contains(QStringLiteral("未知区服：扣除 1 次，起始次数改为 2")), qPrintable(status));
        for (const QString &code : {QStringLiteral("CN"), QStringLiteral("TC"), QStringLiteral("UNKNOWN")})
            QVERIFY2(!status.contains(code), qPrintable(status));
    }

    // Audit 2026-11-03 H1-1 (decision): a row whose source names no region - no 区服 column, a blank
    // cell (read back as 未知区服) - takes the region the dialog was given, the dashboard's else 国服.
    // It is set on the previewed candidate the way the batch region pick sets it, so the identity the
    // first reading gave the row (source_key) is kept and an earlier import of it is still found; the
    // source itself is read unchanged. A stated region, and a record of this software, are kept.
    void regionlessRowsTakeTheDefaultRegionAsTheBatchPickSetsIt()
    {
        RegionalImportBackend backend;
        mr::ImportRecordsController controller;
        controller.setBackend(&backend);
        controller.setDefaultRegion(QStringLiteral("TC"));
        QCOMPARE(controller.defaultRegion(), QStringLiteral("TC"));
        controller.importText(QStringLiteral("副本\n合成副本"));
        QCOMPARE(backend.requestCount, 1);
        QVERIFY(!backend.payload.contains("rows")); // The first reading is the source as it is.
        const auto readRow = [](int number, const QJsonValue &region, const QString &runId = {}) {
            QJsonObject row = previewRow(number, "UNKNOWN");
            QJsonObject candidate = row.value("candidate").toObject();
            candidate.remove("region");
            if (!region.isUndefined())
                candidate.insert("region", region);
            candidate.insert("source_key", QStringLiteral("source-key-%1").arg(number));
            if (!runId.isEmpty())
                candidate.insert("run_id", runId);
            row.insert("candidate", candidate);
            return row;
        };
        answerPreview(backend, {readRow(1, QJsonValue::Undefined), readRow(2, QStringLiteral("")),
                                readRow(3, QStringLiteral("UNKNOWN")),
                                readRow(4, QStringLiteral("UNKNOWN"), QStringLiteral("6f1d2c3b-4a5e-4f60-8b7c-9d0e1f2a3b4c")),
                                previewRow(5, "GLOBAL")});
        QCOMPARE(backend.requestCount, 2); // Read again at once, as after a batch pick.
        QVERIFY(controller.busy());
        const QJsonArray sent = backend.payload.value("rows").toArray();
        QCOMPARE(sent.size(), 5);
        for (int i = 0; i < 3; ++i) {
            QCOMPARE(sent[i].toObject().value("region").toString(), QStringLiteral("TC"));
            QCOMPARE(sent[i].toObject().value("source_key").toString(), QStringLiteral("source-key-%1").arg(i + 1));
            // Only the region changes (re-review RV3-1): what the source stated about the duty stays.
            QCOMPARE(sent[i].toObject().value("content_id").toInt(), 5);
            QCOMPARE(sent[i].toObject().value("territory_id").toInt(), 1331);
            QCOMPARE(sent[i].toObject().value("duty_category").toString(), QStringLiteral("四人迷宫"));
        }
        QCOMPARE(sent[3].toObject().value("region").toString(), QStringLiteral("UNKNOWN"));
        QCOMPARE(sent[4].toObject().value("region").toString(), QStringLiteral("GLOBAL"));
        QJsonObject possible = previewRow(2, "TC");
        possible.insert("status", "possible_duplicate");
        answerPreview(backend, {previewRow(1, "TC"), possible, previewRow(3, "TC"), previewRow(4, "UNKNOWN"),
                                previewRow(5, "GLOBAL")});
        QTRY_VERIFY(controller.previewValid());
        QCOMPARE(backend.requestCount, 2); // Once.
        QCOMPARE(controller.currentCandidate().value("region").toString(), QStringLiteral("TC"));
        // The second reading decides what is preselected, as a first preview: a possible duplicate never is.
        QVERIFY(controller.rows()[0].toMap().value("selected").toBool());
        QVERIFY(!controller.rows()[1].toMap().value("selected").toBool());
        QVERIFY(controller.rows()[4].toMap().value("selected").toBool());

        // Only a named region is a default; without one the rows stay as the Collector read them.
        for (const QString &none : {QString(), QStringLiteral("UNKNOWN"), QStringLiteral("all")}) {
            RegionalImportBackend plain;
            mr::ImportRecordsController unset;
            unset.setBackend(&plain);
            unset.setDefaultRegion(none);
            QCOMPARE(unset.defaultRegion(), QString());
            unset.importText(QStringLiteral("副本\n合成副本"));
            answerPreview(plain, {readRow(1, QJsonValue::Undefined)});
            QTRY_VERIFY(unset.previewValid());
            QCOMPARE(plain.requestCount, 1);
        }
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
