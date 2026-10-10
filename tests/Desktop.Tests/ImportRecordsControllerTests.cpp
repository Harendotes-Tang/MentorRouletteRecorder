#include "IBackend.h"
#include "ImportRecordsController.h"
#include "JobCatalog.h"
#include "OfflineOcrEngine.h"

#include <QCoreApplication>
#include <QClipboard>
#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QGuiApplication>
#include <QHash>
#include <QImage>
#include <QJsonArray>
#include <QPainter>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQmlProperty>
#include <QQuickItem>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QPointer>
#include <QProcess>
#include <QSignalSpy>
#include <QScopeGuard>
#include <QSet>
#include <QTemporaryDir>
#include <QTest>
#include <QThread>
#include <QtMath>

class ImportThemeState : public QObject
{
    Q_OBJECT
    Q_PROPERTY(bool dark MEMBER dark NOTIFY changed)
public:
    bool dark = false;
Q_SIGNALS:
    void changed();
};

namespace {

class ImportBackend final : public mr::IBackend
{
public:
    struct Call { QString type; QJsonObject payload; QString requestId; QPointer<mr::BackendReply> reply; };
    QList<Call> calls;
    bool connected = true;
    bool synchronous = false;
    QJsonObject synchronousAnswer;

    QString backendName() const override { return QStringLiteral("import-test"); }
    bool isConnected() const override { return connected; }
    mr::BackendReply *request(const QString &type, const QJsonObject &payload) override
    {
        auto *reply = new mr::BackendReply(QString::number(calls.size()), type, this);
        calls.append({type, payload, {}, reply});
        if (synchronous)
            reply->succeed(synchronousAnswer);
        return reply;
    }
    mr::BackendReply *requestWithId(const QString &type, const QJsonObject &payload, const QString &id) override
    {
        auto *reply = request(type, payload);
        calls.last().requestId = id;
        return reply;
    }
};

QJsonObject preview(const QString &id = QStringLiteral("test-preview"))
{
    QJsonArray rows;
    for (int i = 1; i <= 3; ++i) {
        rows.append(QJsonObject{{"row_number", i}, {"status", i == 1 ? "new" : (i == 2 ? "duplicate" : "conflict")},
                                {"can_import", i == 1}, {"incomplete", true},
                                {"candidate", QJsonObject{{"duty_name", QStringLiteral("合成副本")}, {"reflection_text", QStringLiteral("合成正文")},
                                                          {"source_recorded_at", QStringLiteral("2026-10-08 18:00:00")}}},
                                {"errors", QJsonArray{}}, {"warnings", QJsonArray{}}});
    }
    return {{"preview_id", id}, {"rows", rows}, {"summary", QJsonObject{{"total", 3}, {"new", 1}, {"duplicate", 1}, {"conflict", 1}}}};
}

QJsonObject singleRowPreview(const QJsonObject &candidate, const QJsonObject &run = {},
                             const QString &id = QStringLiteral("single-preview"))
{
    return {{"preview_id", id},
            {"rows", QJsonArray{QJsonObject{{"row_number", 1}, {"status", "new"}, {"can_import", true},
                                            {"incomplete", true}, {"candidate", candidate}, {"run", run},
                                            {"errors", QJsonArray{}}, {"warnings", QJsonArray{}}}}},
            {"summary", QJsonObject{{"total", 1}, {"new", 1}, {"incomplete", 1}}}};
}

const QByteArray kTsvHeader = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n";

QByteArray syntheticOcrCard()
{
    // Synthetic source text follows the production mobile card coordinates;
    // no personal record or real screenshot is embedded in a test fixture.
    return kTsvHeader
        + QStringLiteral("5\t1\t1\t1\t1\t1\t150\t100\t65\t20\t96\tLv.15\n"
                         "5\t1\t1\t1\t1\t2\t225\t98\t30\t25\t40\t(A)\n"
                         "5\t1\t1\t1\t1\t3\t270\t100\t280\t20\t96\t天然要寒沙斯塔夏溶洞\n"
                         "5\t1\t1\t1\t2\t1\t150\t140\t260\t20\t96\t合成心得第一行\n"
                         "5\t1\t1\t1\t3\t1\t150\t165\t260\t20\t96\t合成心得第二行\n"
                         "5\t1\t1\t1\t4\t1\t380\t200\t190\t15\t96\t2026-10-01 12:34:56\n").toUtf8();
}

void typeAscii(QWindow *window, const QByteArray &text)
{
    // QTest::keyClicks only accepts QWidget; its QWindow char overload sends
    // each supplied character as event text, preserving case and punctuation.
    for (char character : text)
        QTest::keyClick(window, character);
}

void createFile(const QString &path, const QByteArray &content)
{
    QFile file(path);
    QVERIFY(file.open(QIODevice::WriteOnly));
    QCOMPARE(file.write(content), content.size());
}

QString installTestWorker(QTemporaryDir &directory)
{
    const QString app = directory.filePath(QStringLiteral("test-app"));
    const QString ocr = QDir(app).filePath(QStringLiteral("ocr"));
    if (!QDir().mkpath(QDir(ocr).filePath(QStringLiteral("tessdata"))))
        return {};
    if (!QFile::copy(QCoreApplication::applicationFilePath(), QDir(ocr).filePath(QStringLiteral("tesseract.exe"))))
        return {};
    createFile(QDir(ocr).filePath(QStringLiteral("tessdata/chi_sim.traineddata")), "test-model");
    createFile(QDir(ocr).filePath(QStringLiteral("tessdata/eng.traineddata")), "test-model");
    return app;
}

class ScopedImportOcrEnvironment
{
public:
    ScopedImportOcrEnvironment()
    {
        for (const QByteArray &name : {QByteArray("LOCALAPPDATA"), QByteArray("MR_TEST_OCR_TSV_FILE"), QByteArray("MR_TEST_OCR_SLEEP")}) {
            values.insert(name, qgetenv(name.constData()));
            if (qEnvironmentVariableIsSet(name.constData()))
                present.insert(name);
        }
        qunsetenv("MR_TEST_OCR_SLEEP");
    }
    ~ScopedImportOcrEnvironment()
    {
        for (auto it = values.cbegin(); it != values.cend(); ++it) {
            if (present.contains(it.key()))
                qputenv(it.key().constData(), it.value());
            else
                qunsetenv(it.key().constData());
        }
    }
private:
    QHash<QByteArray, QByteArray> values;
    QSet<QByteArray> present;
};

QImage syntheticImportImage()
{
    QImage image(600, 800, QImage::Format_ARGB32);
    image.fill(Qt::white);
    QPainter painter(&image);
    painter.fillRect(QRect(30, 70, 540, 180), QColor(85, 45, 45));
    painter.fillRect(QRect(50, 110, 72, 72), QColor(220, 185, 75));
    painter.fillRect(QRect(225, 98, 30, 25), QColor(15, 180, 230));
    return image;
}

QJsonObject previewCandidates(const QJsonArray &candidates, const QString &id = QStringLiteral("candidate-preview"),
                              const QList<int> &rowNumbers = {})
{
    QJsonArray rows;
    for (int i = 0; i < candidates.size(); ++i)
        rows.append(QJsonObject{{"row_number", rowNumbers.isEmpty() ? i + 1 : rowNumbers[i]}, {"status", "new"},
            {"can_import", true}, {"incomplete", true}, {"candidate", candidates[i]},
            {"errors", QJsonArray{}}, {"warnings", QJsonArray{}}});
    return {{"preview_id", id}, {"rows", rows}, {"summary", QJsonObject{{"total", rows.size()}, {"new", rows.size()}}}};
}

int matchingRenderedPixels(const QImage &image, const QRectF &sceneRect, qreal ratio, const QColor &target)
{
    const QRect pixels(qFloor(sceneRect.x() * ratio), qFloor(sceneRect.y() * ratio),
                       qCeil(sceneRect.width() * ratio), qCeil(sceneRect.height() * ratio));
    const QRect clipped = pixels.intersected(image.rect());
    int matched = 0;
    for (int y = clipped.top(); y <= clipped.bottom(); ++y)
        for (int x = clipped.left(); x <= clipped.right(); ++x) {
            const QColor color = image.pixelColor(x, y);
            matched += qAbs(color.red() - target.red()) + qAbs(color.green() - target.green())
                + qAbs(color.blue() - target.blue()) < 24;
        }
    return matched;
}

} // namespace

class ImportRecordsControllerTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void initTestCase()
    {
        const QString root = QString::fromUtf8(MR_DESKTOP_QML_DIR);
        qmlRegisterSingletonType(QUrl::fromLocalFile(root + QStringLiteral("/Theme.qml")),
                                 "MentorRecorder", 1, 0, "Theme");
        for (const auto &directory : {QStringLiteral("/components"), QStringLiteral("/dialogs")})
            for (const auto &file : QDir(root + directory).entryList({QStringLiteral("*.qml")}, QDir::Files)) {
                const QByteArray name = file.chopped(4).toUtf8();
                qmlRegisterType(QUrl::fromLocalFile(root + directory + QLatin1Char('/') + file),
                                "MentorRecorder", 1, 0, name.constData());
            }
    }

    void realMessageNamesAndOwnRecordConfirmation()
    {
        ImportBackend backend;
        mr::ImportRecordsController controller;
        controller.setBackend(&backend);
        controller.importText(QStringLiteral("职业\t副本\t评论\n舞者\t合成副本\t合成正文"));
        QCOMPARE(backend.calls.last().type, QStringLiteral("PreviewRunImport"));
        QCOMPARE(backend.calls.last().payload.value("source_kind").toString(), QStringLiteral("PASTE"));
        QVERIFY(controller.busy());
        backend.calls.last().reply->succeed(preview());
        QVERIFY(!controller.busy());
        QCOMPARE(controller.selectedCount(), 1);
        QVERIFY(!controller.canCommit());
        controller.commit();
        QCOMPARE(backend.calls.size(), 1);
        controller.setOwnRecordsConfirmed(true);
        QVERIFY(controller.canCommit());
        controller.commit();
        QCOMPARE(backend.calls.last().type, QStringLiteral("CommitRunImport"));
        QCOMPARE(backend.calls.last().payload.value("row_numbers").toArray(), QJsonArray{1});
        QVERIFY(backend.calls.last().payload.value("confirm_own_records").toBool());
        QVERIFY(!backend.calls.last().requestId.isEmpty());
        controller.cancel();
        QVERIFY(controller.committing());
        backend.calls.last().reply->succeed({{"imported_count", 1}, {"duplicate_count", 0}, {"conflict_count", 0}});
        QCOMPARE(controller.phase(), QStringLiteral("complete"));
        QVERIFY(!controller.canCommit());
    }

    void editedRowsMustBeRevalidatedWithOriginalSourceKind()
    {
        ImportBackend backend;
        mr::ImportRecordsController controller;
        controller.setBackend(&backend);
        controller.importText(QStringLiteral("副本\n合成副本"));
        QJsonObject response = preview();
        QJsonArray rows = response.value("rows").toArray();
        QJsonObject first = rows[0].toObject();
        QJsonObject original = first.value("candidate").toObject();
        original.insert("source_recorded_at_utc", "2026-10-08T10:00:00Z");
        original.insert("import_metadata", QJsonObject{{"source_recorded_at", "2026-10-08 18:00:00"},
                                                      {"source_recorded_at_utc", "2026-10-08T10:00:00Z"}, {"source_kind", "PASTE"}});
        first.insert("candidate", original);
        rows[0] = first;
        response.insert("rows", rows);
        backend.calls.last().reply->succeed(response);
        controller.setOwnRecordsConfirmed(true);
        controller.updateCandidate(0, {{"duty_name", QStringLiteral("修改后副本")}, {"source_recorded_at", "2026-10-08 19:00:00"}});
        QVERIFY(!controller.previewValid());
        QVERIFY(!controller.canCommit());
        controller.revalidate();
        const QJsonObject request = backend.calls.last().payload;
        QCOMPARE(request.value("source_kind").toString(), QStringLiteral("PASTE"));
        QVERIFY(!request.contains("text"));
        QVERIFY(request.contains("rows"));
        const QJsonObject row = request.value("rows").toArray().first().toObject();
        QCOMPARE(row.value("duty_name").toString(), QStringLiteral("修改后副本"));
        QVERIFY(!row.contains("matched_at_utc"));
        QCOMPARE(row.value("source_recorded_at").toString(), QStringLiteral("2026-10-08 19:00:00"));
        QVERIFY(!row.contains("source_recorded_at_utc"));
        const QJsonObject metadata = row.value("import_metadata").toObject();
        QVERIFY(!metadata.contains("source_recorded_at"));
        QVERIFY(!metadata.contains("source_recorded_at_utc"));
        QCOMPARE(metadata.value("source_kind").toString(), QStringLiteral("PASTE"));
    }

    void noOpEditorBlurKeepsPreviewAndConflictCannotBeSelected()
    {
        ImportBackend backend;
        mr::ImportRecordsController controller;
        controller.setBackend(&backend);
        controller.importText(QStringLiteral("副本\n合成副本"));
        backend.calls.last().reply->succeed(preview());
        controller.updateCandidate(0, {{"duty_name", QStringLiteral("合成副本")}});
        controller.updateCandidate(0, {{"duty_category", QVariant()}});
        QVERIFY(controller.previewValid());
        controller.setRowSelected(2, true);
        controller.setRowSelected(1, true);
        QCOMPARE(controller.selectedCount(), 1);
        controller.setAllImportableSelected(false);
        QCOMPARE(controller.selectedCount(), 0);
        controller.setAllImportableSelected(true);
        QCOMPARE(controller.selectedCount(), 1);
    }

    void jobChoicesOnlyContainEligibleBattleJobs()
    {
        mr::ImportRecordsController controller;
        const QVariantList choices = controller.jobChoices();
        QVERIFY(choices.size() > 1);
        QVERIFY(choices.first().toMap().value("job_id").isNull());
        QSet<int> selectable;
        for (int i = 1; i < choices.size(); ++i) {
            const QVariantMap job = choices[i].toMap();
            const int id = job.value("job_id").toInt();
            QVERIFY(id > 0);
            QVERIFY(!selectable.contains(id));
            QVERIFY(!job.value("job_name").toString().isEmpty());
            QVERIFY(job.value("role_group").toString() != QStringLiteral("其他"));
            selectable.insert(id);
        }
        const QSet<int> baseClasses{1, 2, 3, 4, 5, 6, 7, 26, 29};
        const QSet<int> limitedJobs{36, 43};
        const mr::JobCatalog catalog;
        const QVariantList all = catalog.allJobs();
        QVERIFY(!all.isEmpty());
        for (const QVariant &value : all) {
            const QVariantMap job = value.toMap();
            const int id = job.value("job_id").toInt();
            const bool excluded = baseClasses.contains(id) || limitedJobs.contains(id)
                || job.value("role_group").toString() == QStringLiteral("其他");
            QCOMPARE(selectable.contains(id), !excluded);
        }
        for (int id : {19, 24, 34, 42})
            QVERIFY2(selectable.contains(id), qPrintable(QString::number(id)));
    }

    void revalidationRestoresOnlyPreviouslySelectedImportableRowNumbers()
    {
        ImportBackend backend;
        mr::ImportRecordsController controller;
        controller.setBackend(&backend);
        controller.importText(QStringLiteral("副本\n合成副本"));
        const QJsonArray candidates{QJsonObject{{"duty_name", "one"}}, QJsonObject{{"duty_name", "two"}},
            QJsonObject{{"duty_name", "three"}}};
        backend.calls.last().reply->succeed(previewCandidates(candidates, "first", {10, 20, 30}));
        QCOMPARE(controller.selectedCount(), 3); // Initial tables keep the established default.
        controller.setAllImportableSelected(false);
        controller.setRowSelected(0, true);
        controller.setRowSelected(2, true);
        controller.setOwnRecordsConfirmed(true);
        controller.updateCandidate(0, {{"reflection_text", "edited"}});
        QCOMPARE(controller.selectedCount(), 0);
        QVERIFY(!controller.canCommit());
        controller.revalidate();
        const QJsonArray edited = backend.calls.last().payload.value("rows").toArray();
        backend.calls.last().reply->succeed(previewCandidates(edited, "second", {10, 20, 30}));
        QCOMPARE(controller.selectedCount(), 2);
        QVERIFY(controller.rows()[0].toMap().value("selected").toBool());
        QVERIFY(!controller.rows()[1].toMap().value("selected").toBool());
        QVERIFY(controller.rows()[2].toMap().value("selected").toBool());
        QVERIFY(controller.canCommit());
        controller.revalidate();
        QJsonObject response = previewCandidates(edited, "third", {10, 20, 30});
        QJsonArray rows = response.value("rows").toArray();
        for (int index : {0, 2}) {
            QJsonObject row = rows[index].toObject();
            row.insert("status", index == 0 ? "invalid" : "conflict");
            row.insert("can_import", false);
            rows[index] = row;
        }
        QJsonObject middle = rows[1].toObject();
        middle.insert("status", "possible_duplicate");
        rows[1] = middle;
        response.insert("rows", rows);
        backend.calls.last().reply->succeed(response);
        QCOMPARE(controller.selectedCount(), 0); // Revalidation must not newly choose row 20.
        QVERIFY(!controller.canCommit());
        controller.setRowSelected(1, true);
        controller.revalidate();
        backend.calls.last().reply->succeed(response);
        QCOMPARE(controller.selectedCount(), 1); // Importable suspected duplicate remains explicitly chosen.
        controller.importText(QStringLiteral("副本\n另一个来源"));
        backend.calls.last().reply->succeed(previewCandidates(candidates));
        QCOMPARE(controller.selectedCount(), 3);
        QVERIFY(!controller.ownRecordsConfirmed());
    }

    void batchUpdateFreezesSelectedIndicesAndValidatesOnce()
    {
        ImportBackend backend;
        mr::ImportRecordsController controller;
        controller.setBackend(&backend);
        controller.importText(QStringLiteral("副本\n合成副本"));
        const QJsonArray candidates{QJsonObject{{"duty_name", "one"}, {"job_id", 19}, {"job_name", QStringLiteral("骑士")}},
            QJsonObject{{"duty_name", "two"}, {"result", "COMPLETED"}},
            QJsonObject{{"duty_name", "three"}, {"job_id", 19}, {"job_name", QStringLiteral("骑士")}}};
        backend.calls.last().reply->succeed(previewCandidates(candidates, "batch-first", {12, 24, 36}));
        controller.setRowSelected(1, false);
        const QVariantList originalRows = controller.rows();
        for (const QVariantMap &bad : {QVariantMap{{"reflection_text", "cannot bulk edit notes"}},
            QVariantMap{{"job_id", 8}}, QVariantMap{{"result", "invalid"}}, QVariantMap{{"reflection_mood", "invalid"}}}) {
            controller.batchUpdateSelected(bad);
            QCOMPARE(backend.calls.size(), 1);
            QCOMPARE(controller.rows(), originalRows);
            QVERIFY(controller.previewValid());
        }
        controller.batchUpdateSelected({{"job_id", QVariant()}, {"result", "UNKNOWN"}, {"reflection_mood", "bad"}});
        QCOMPARE(backend.calls.size(), 2);
        QVERIFY(controller.busy());
        QVERIFY(!controller.previewValid());
        const QJsonArray modified = backend.calls.last().payload.value("rows").toArray();
        QCOMPARE(modified[1], candidates[1]); // Middle unselected candidate is byte-for-byte unchanged.
        for (int index : {0, 2}) {
            const QJsonObject candidate = modified[index].toObject();
            QVERIFY(candidate.value("job_id").isNull());
            QVERIFY(candidate.value("job_name").isNull());
            QCOMPARE(candidate.value("result").toString(), QStringLiteral("UNKNOWN"));
            QCOMPARE(candidate.value("reflection_mood").toString(), QStringLiteral("bad"));
            QCOMPARE(candidate.value("duty_name"), candidates[index].toObject().value("duty_name"));
        }
        controller.batchUpdateSelected({{"result", "COMPLETED"}}); // Preview lock prevents a second batch operation.
        QCOMPARE(backend.calls.size(), 2);
        backend.calls.last().reply->fail("ERR_BAD_REQUEST", "synthetic preview failure");
        QVERIFY(!controller.canCommit());
        QCOMPARE(controller.selectedCount(), 0);
        controller.revalidate();
        backend.calls.last().reply->succeed(previewCandidates(modified, "batch-restored", {12, 24, 36}));
        QCOMPARE(controller.selectedCount(), 2);
        controller.batchUpdateSelected({{"job_id", 24}, {"job_name", "ignored noncanonical name"}});
        QCOMPARE(backend.calls.size(), 4);
        const QJsonArray withJob = backend.calls.last().payload.value("rows").toArray();
        QCOMPARE(withJob[1], candidates[1]);
        for (int index : {0, 2})
            QCOMPARE(withJob[index].toObject().value("job_name").toString(), mr::JobCatalog().jobName(24));
        backend.calls.last().reply->succeed(previewCandidates(withJob, "batch-job", {12, 24, 36}));
        controller.setAllImportableSelected(false);
        controller.batchUpdateSelected({{"result", "COMPLETED"}});
        QCOMPARE(backend.calls.size(), 4);
    }

    void screenshotAppendPreservesEditsAndRollsBackFailureOrCancellation()
    {
        ScopedImportOcrEnvironment environment;
        QTemporaryDir directory;
        QVERIFY(directory.isValid());
        qputenv("LOCALAPPDATA", directory.path().toUtf8());
        const QString tsvPath = directory.filePath(QStringLiteral("synthetic.tsv"));
        createFile(tsvPath, syntheticOcrCard());
        qputenv("MR_TEST_OCR_TSV_FILE", tsvPath.toUtf8());
        const QString app = installTestWorker(directory);
        QVERIFY(!app.isEmpty());
        const QString firstImage = directory.filePath(QStringLiteral("first.png"));
        const QString secondImage = directory.filePath(QStringLiteral("second.png"));
        QVERIFY(syntheticImportImage().save(firstImage));
        QVERIFY(syntheticImportImage().save(secondImage));
        ImportBackend backend;
        mr::ImportRecordsController controller;
        controller.setBackend(&backend);
        controller.setOcrApplicationDirectoryForTesting(app);
        controller.importFiles({firstImage});
        QTRY_COMPARE_WITH_TIMEOUT(backend.calls.size(), 1, 10000);
        backend.calls.last().reply->succeed(previewCandidates(backend.calls.last().payload.value("rows").toArray(), "initial-image"));
        QCOMPARE(controller.sourceImageCount(), 1);
        QCOMPARE(controller.selectedCount(), 0);
        controller.setRowSelected(0, true);
        controller.setOwnRecordsConfirmed(true);
        controller.updateCandidate(0, {{"reflection_text", "preserved edit"}});
        controller.revalidate();
        backend.calls.last().reply->succeed(previewCandidates(backend.calls.last().payload.value("rows").toArray(), "edited-image"));
        QCOMPARE(controller.selectedCount(), 1);
        const QVariantList original = controller.rows();
        const QVariantMap originalSummary = controller.summary();
        const QString sourceName = controller.sourceLabel();
        const QString badImage = directory.filePath(QStringLiteral("bad.png"));
        createFile(badImage, "not an image");
        controller.importFiles({badImage});
        QVERIFY(controller.previewValid());
        QCOMPARE(controller.rows(), original);
        QCOMPARE(controller.sourceImageCount(), 1);
        QCOMPARE(controller.selectedCount(), 1);
        controller.importFiles({secondImage});
        QVERIFY(controller.busy());
        controller.importText("副本\nlocked");
        QCOMPARE(controller.sourceKind(), QStringLiteral("SCREENSHOT"));
        QTRY_COMPARE_WITH_TIMEOUT(backend.calls.size(), 3, 10000);
        QCOMPARE(backend.calls.last().payload.value("rows").toArray().size(), 2);
        backend.calls.last().reply->fail("ERR_INTERNAL", "append preview failed");
        QVERIFY(controller.previewValid());
        QCOMPARE(controller.rows(), original);
        QCOMPARE(controller.summary(), originalSummary);
        QCOMPARE(controller.sourceLabel(), sourceName);
        QCOMPARE(controller.currentRow(), 0);
        QCOMPARE(controller.selectedCount(), 1);
        QCOMPARE(controller.sourceImageCount(), 1);
        QVERIFY(controller.ownRecordsConfirmed());
        controller.importFiles({secondImage});
        QTRY_COMPARE_WITH_TIMEOUT(backend.calls.size(), 4, 10000);
        const auto cancelledReply = backend.calls.last().reply;
        const QJsonObject cancelledResponse = previewCandidates(backend.calls.last().payload.value("rows").toArray(), "cancelled-append");
        controller.cancel();
        cancelledReply->succeed(cancelledResponse);
        QCOMPARE(controller.rows(), original);
        QVERIFY(controller.previewValid());
        QCOMPARE(controller.sourceImageCount(), 1);
        qputenv("MR_TEST_OCR_SLEEP", "1");
        controller.importFiles({secondImage});
        QTest::qWait(100);
        controller.cancel();
        QTRY_VERIFY_WITH_TIMEOUT(!controller.busy(), 5000);
        QCOMPARE(backend.calls.size(), 4);
        QCOMPARE(controller.rows(), original);
        QVERIFY(controller.previewValid());
        QCOMPARE(controller.sourceImageCount(), 1);
        qunsetenv("MR_TEST_OCR_SLEEP");
        controller.importFiles({secondImage});
        QTRY_COMPARE_WITH_TIMEOUT(backend.calls.size(), 5, 10000);
        const QJsonArray appended = backend.calls.last().payload.value("rows").toArray();
        QCOMPARE(appended.first().toObject().value("reflection_text").toString(), QStringLiteral("preserved edit"));
        backend.calls.last().reply->succeed(previewCandidates(appended, "appended"));
        QCOMPARE(controller.sourceImageCount(), 2);
        QCOMPARE(controller.rows().size(), 2);
        QCOMPARE(controller.selectedCount(), 1);
        QCOMPARE(controller.rows()[0].toMap().value("evidence").toMap().value("source_image").toString(), firstImage);
        QCOMPARE(controller.rows()[1].toMap().value("evidence").toMap().value("source_image").toString(), secondImage);
        QVERIFY(!controller.rows()[1].toMap().value("selected").toBool());
        controller.importText("副本\nnew table");
        QCOMPARE(controller.sourceImageCount(), 0);
        QCOMPARE(controller.sourceKind(), QStringLiteral("PASTE"));
        QVERIFY(controller.rows().isEmpty());
        QVERIFY(!controller.ownRecordsConfirmed());
        backend.calls.last().reply->succeed(singleRowPreview({{"duty_name", "new table"}}));
        QCOMPARE(controller.selectedCount(), 1);
        QVERIFY(controller.currentEvidence().isEmpty());
    }

    void screenshotAppendCountsImagesAndRetainsClipboardEvidenceFiles()
    {
        ScopedImportOcrEnvironment environment;
        QTemporaryDir directory;
        QVERIFY(directory.isValid());
        qputenv("LOCALAPPDATA", directory.path().toUtf8());
        const QString tsvPath = directory.filePath(QStringLiteral("synthetic.tsv"));
        createFile(tsvPath, syntheticOcrCard());
        qputenv("MR_TEST_OCR_TSV_FILE", tsvPath.toUtf8());
        const QString app = installTestWorker(directory);
        QVERIFY(!app.isEmpty());
        ImportBackend backend;
        mr::ImportRecordsController controller;
        controller.setBackend(&backend);
        controller.setOcrApplicationDirectoryForTesting(app);
        QGuiApplication::clipboard()->setImage(syntheticImportImage());
        controller.pasteClipboard();
        QTRY_COMPARE_WITH_TIMEOUT(backend.calls.size(), 1, 10000);
        backend.calls.last().reply->succeed(previewCandidates(backend.calls.last().payload.value("rows").toArray(), "clipboard-one"));
        const QString clipboardPath = controller.currentEvidence().value("source_image").toString();
        QVERIFY(QFileInfo::exists(clipboardPath));
        const QDir clipboardDirectory(QFileInfo(clipboardPath).absolutePath());
        const QStringList clipboardCopies = clipboardDirectory.entryList(QDir::Files);
        QVariantList images;
        for (int i = 0; i < 19; ++i) {
            const QString path = directory.filePath(QStringLiteral("append-%1.png").arg(i));
            QVERIFY(syntheticImportImage().save(path));
            images.append(path);
        }
        controller.importFiles(images);
        QTRY_COMPARE_WITH_TIMEOUT(backend.calls.size(), 2, 60000);
        const QJsonArray appended = backend.calls.last().payload.value("rows").toArray();
        QCOMPARE(appended.size(), 20);
        backend.calls.last().reply->succeed(previewCandidates(appended, "twenty-images"));
        QCOMPARE(controller.sourceImageCount(), 20);
        QVERIFY(QFileInfo::exists(clipboardPath));
        controller.setRowSelected(0, true);
        const QVariantList original = controller.rows();
        QGuiApplication::clipboard()->setImage(syntheticImportImage());
        controller.pasteClipboard();
        QCOMPARE(backend.calls.size(), 2);
        QCOMPARE(controller.sourceImageCount(), 20);
        QCOMPARE(controller.rows(), original);
        QVERIFY(controller.previewValid());
        QCOMPARE(controller.selectedCount(), 1);
        QVERIFY(controller.errorText().contains(QStringLiteral("20")));
        QCOMPARE(clipboardDirectory.entryList(QDir::Files), clipboardCopies); // Rejected pastes do not accumulate image copies.
        controller.reset();
        QVERIFY(!QFileInfo::exists(clipboardPath));
    }

    void committedScreenshotBatchStartsFreshAfterAnInputError()
    {
        ScopedImportOcrEnvironment environment;
        QTemporaryDir directory;
        QVERIFY(directory.isValid());
        qputenv("LOCALAPPDATA", directory.path().toUtf8());
        const QString tsvPath = directory.filePath(QStringLiteral("synthetic.tsv"));
        createFile(tsvPath, syntheticOcrCard());
        qputenv("MR_TEST_OCR_TSV_FILE", tsvPath.toUtf8());
        const QString app = installTestWorker(directory);
        QVERIFY(!app.isEmpty());
        const QString firstImage = directory.filePath(QStringLiteral("committed.png"));
        const QString nextImage = directory.filePath(QStringLiteral("new-batch.png"));
        QVERIFY(syntheticImportImage().save(firstImage));
        QVERIFY(syntheticImportImage().save(nextImage));
        ImportBackend backend;
        mr::ImportRecordsController controller;
        controller.setBackend(&backend);
        controller.setOcrApplicationDirectoryForTesting(app);
        controller.importFiles({firstImage});
        QTRY_COMPARE_WITH_TIMEOUT(backend.calls.size(), 1, 10000);
        backend.calls.last().reply->succeed(previewCandidates(backend.calls.last().payload.value("rows").toArray(), "to-commit"));
        controller.setRowSelected(0, true);
        controller.updateCandidate(0, {{"reflection_text", "already stored old note"}});
        controller.revalidate();
        backend.calls.last().reply->succeed(previewCandidates(backend.calls.last().payload.value("rows").toArray(), "edited-to-commit"));
        controller.setOwnRecordsConfirmed(true);
        controller.commit();
        QCOMPARE(backend.calls.size(), 3);
        backend.calls.last().reply->succeed({{"imported_count", 1}});
        QCOMPARE(controller.phase(), QStringLiteral("complete"));
        QCOMPARE(controller.sourceImageCount(), 1);
        controller.importFiles({directory.filePath(QStringLiteral("missing.png"))});
        QVERIFY(!controller.errorText().isEmpty());
        QCOMPARE(controller.sourceImageCount(), 1);
        controller.cancel(); // A display phase change must not reopen a committed batch for append.
        controller.importFiles({nextImage});
        QVERIFY(!controller.ownRecordsConfirmed());
        QVERIFY(controller.rows().isEmpty());
        QTRY_COMPARE_WITH_TIMEOUT(backend.calls.size(), 4, 10000);
        const QJsonArray nextCandidates = backend.calls.last().payload.value("rows").toArray();
        QCOMPARE(nextCandidates.size(), 1);
        QVERIFY(nextCandidates.first().toObject().value("reflection_text").toString() != QStringLiteral("already stored old note"));
        backend.calls.last().reply->succeed(previewCandidates(nextCandidates, "fresh-batch"));
        QCOMPARE(controller.sourceImageCount(), 1);
        QCOMPARE(controller.selectedCount(), 0);
        QCOMPARE(controller.currentEvidence().value("source_image").toString(), nextImage);
    }

    void screenshotJobPrefillRequiresExplicitReview_data()
    {
        QTest::addColumn<bool>("changeJob");
        QTest::newRow("confirm-same-job") << false;
        QTest::newRow("manually-change-job") << true;
    }

    void screenshotJobPrefillRequiresExplicitReview()
    {
        QFETCH(bool, changeJob);
        QTemporaryDir directory;
        QVERIFY(directory.isValid());
        const QList<QByteArray> variableNames{"LOCALAPPDATA", "MR_TEST_OCR_TSV_FILE", "MR_TEST_OCR_SLEEP"};
        QHash<QByteArray, QByteArray> previousValues;
        QSet<QByteArray> previouslySet;
        for (const QByteArray &name : variableNames) {
            previousValues.insert(name, qgetenv(name.constData()));
            if (qEnvironmentVariableIsSet(name.constData()))
                previouslySet.insert(name);
        }
        const auto restoreEnvironment = qScopeGuard([&] {
            for (const QByteArray &name : variableNames) {
                if (previouslySet.contains(name))
                    qputenv(name.constData(), previousValues.value(name));
                else
                    qunsetenv(name.constData());
            }
        });
        qputenv("LOCALAPPDATA", directory.path().toUtf8());
        qunsetenv("MR_TEST_OCR_SLEEP");
        const QString tsvPath = directory.filePath(QStringLiteral("synthetic.tsv"));
        createFile(tsvPath, syntheticOcrCard());
        qputenv("MR_TEST_OCR_TSV_FILE", tsvPath.toUtf8());
        const QString app = installTestWorker(directory);
        QVERIFY(!app.isEmpty());

        // A local synthetic glyph makes the candidate path testable even in
        // builds that intentionally do not bundle game artwork. The screenshot
        // is a solid gold block, so a best match must still remain uncertain.
        const QString jobsDirectory = directory.filePath(QStringLiteral("MentorRecorder/icons/jobs"));
        QVERIFY(QDir().mkpath(jobsDirectory));
        QImage glyph(56, 56, QImage::Format_ARGB32);
        glyph.fill(Qt::transparent);
        {
            QPainter painter(&glyph);
            painter.fillRect(QRect(5, 5, 8, 45), QColor(220, 185, 75));
            painter.fillRect(QRect(5, 5, 45, 8), QColor(220, 185, 75));
        }
        QVERIFY(glyph.save(QDir(jobsDirectory).filePath(QStringLiteral("34.png"))));
        QImage image(600, 800, QImage::Format_ARGB32);
        image.fill(Qt::white);
        {
            QPainter painter(&image);
            painter.fillRect(QRect(30, 70, 540, 180), QColor(85, 45, 45));
            painter.fillRect(QRect(50, 110, 72, 72), QColor(220, 185, 75));
            painter.fillRect(QRect(225, 98, 30, 25), QColor(15, 180, 230));
        }
        const QString imagePath = directory.filePath(QStringLiteral("synthetic.png"));
        QVERIFY(image.save(imagePath));
        ImportBackend backend;
        mr::ImportRecordsController controller;
        controller.setBackend(&backend);
        controller.setOcrApplicationDirectoryForTesting(app);
        controller.importFiles({imagePath});
        QTRY_COMPARE_WITH_TIMEOUT(backend.calls.size(), 1, 10000);
        QCOMPARE(backend.calls.last().type, QStringLiteral("PreviewRunImport"));
        QCOMPARE(backend.calls.last().payload.value("source_kind").toString(), QStringLiteral("SCREENSHOT"));
        const QJsonArray candidates = backend.calls.last().payload.value("rows").toArray();
        QCOMPARE(candidates.size(), 1);
        const QJsonObject candidate = candidates.first().toObject();
        const int originalJob = candidate.value("job_id").toInt();
        QVERIFY(originalJob > 0);
        QVERIFY(!candidate.value("job_name").toString().isEmpty());
        const QString recognizedDuty = QStringLiteral("天然要寒沙斯塔夏溶洞");
        const QString canonicalDuty = QStringLiteral("天然要害沙斯塔夏溶洞");
        QCOMPARE(candidate.value("duty_name").toString(), canonicalDuty);
        QCOMPARE(candidate.value("reflection_text").toString(), QStringLiteral("合成心得第一行\n合成心得第二行"));
        for (const QString &key : {QStringLiteral("job_candidate_pending"), QStringLiteral("job_candidate_id"),
                                  QStringLiteral("icon_confidence"), QStringLiteral("source_image"),
                                  QStringLiteral("source_rect"), QStringLiteral("evidence"), QStringLiteral("needs_review"),
                                  QStringLiteral("ocr_duty_name"), QStringLiteral("duty_candidate_name"),
                                  QStringLiteral("duty_candidate_pending")})
            QVERIFY2(!candidate.contains(key), qPrintable(key));
        backend.calls.last().reply->succeed(singleRowPreview(candidate));
        QVERIFY(controller.previewValid());
        QCOMPARE(controller.rows().size(), 1);
        QCOMPARE(controller.selectedCount(), 0);
        QVERIFY(!controller.rows().first().toMap().value("selected").toBool());
        QVariantMap evidence = controller.currentEvidence();
        QCOMPARE(evidence.value("job_candidate_id").toInt(), originalJob);
        QVERIFY(evidence.value("job_candidate_pending").toBool());
        QVERIFY(evidence.value("icon_confidence").toDouble() < 0.82);
        QVERIFY(evidence.value("needs_review").toBool());
        QCOMPARE(evidence.value("source_image").toString(), imagePath);
        QCOMPARE(evidence.value("ocr_duty_name").toString(), recognizedDuty);
        QCOMPARE(evidence.value("duty_candidate_name").toString(), canonicalDuty);
        QVERIFY(evidence.value("duty_candidate_pending").toBool());
        const mr::JobCatalog catalog;
        QSet<int> eligible;
        for (const QVariant &job : catalog.battleJobs())
            eligible.insert(job.toMap().value("job_id").toInt());
        QVERIFY(eligible.contains(originalJob));
        controller.setOwnRecordsConfirmed(true);
        QVERIFY(!controller.canCommit());
        controller.setRowSelected(0, true); // Screenshot selection is always explicit.
        QCOMPARE(controller.selectedCount(), 1);
        QSignalSpy changes(&controller, &mr::ImportRecordsController::changed);
        const QString reviewedDuty = changeJob ? QStringLiteral("手动合成副本") : canonicalDuty;
        controller.updateCandidate(0, {{"duty_name", reviewedDuty}});
        QVERIFY(!controller.currentEvidence().value("duty_candidate_pending").toBool());
        QVERIFY(!controller.rows().first().toMap().value("evidence").toMap().value("duty_candidate_pending").toBool());
        QVERIFY(controller.currentEvidence().value("job_candidate_pending").toBool());
        QCOMPARE(controller.currentCandidate().value("duty_name").toString(), reviewedDuty);
        QCOMPARE(controller.previewValid(), !changeJob);
        QCOMPARE(controller.selectedCount(), changeJob ? 0 : 1);
        const int reviewedJob = changeJob ? (originalJob == 19 ? 24 : 19) : originalJob;
        controller.updateCandidate(0, {{"job_id", reviewedJob}, {"job_name", catalog.jobName(reviewedJob)}});
        QVERIFY(!changes.isEmpty());
        QVERIFY(!controller.currentEvidence().value("job_candidate_pending").toBool());
        QVERIFY(!controller.rows().first().toMap().value("evidence").toMap().value("job_candidate_pending").toBool());
        QCOMPARE(controller.currentCandidate().value("job_id").toInt(), reviewedJob);
        QCOMPARE(backend.calls.size(), 1); // Confirmation/edit never sends an implicit commit or preview.
        if (!changeJob) {
            QVERIFY(controller.previewValid());
            QCOMPARE(controller.selectedCount(), 1);
            QVERIFY(controller.canCommit());
        } else {
            QVERIFY(!controller.previewValid());
            QCOMPARE(controller.selectedCount(), 0);
            QVERIFY(!controller.canCommit());
            controller.revalidate();
            QCOMPARE(backend.calls.size(), 2);
            QCOMPARE(backend.calls.last().payload.value("source_kind").toString(), QStringLiteral("SCREENSHOT"));
            const QJsonObject edited = backend.calls.last().payload.value("rows").toArray().first().toObject();
            QCOMPARE(edited.value("job_id").toInt(), reviewedJob);
            QCOMPARE(edited.value("job_name").toString(), catalog.jobName(reviewedJob));
            QCOMPARE(edited.value("duty_name").toString(), reviewedDuty);
            QVERIFY(!edited.contains("job_candidate_pending"));
            QVERIFY(!edited.contains("ocr_duty_name"));
            QVERIFY(!edited.contains("duty_candidate_name"));
            QVERIFY(!edited.contains("duty_candidate_pending"));
            backend.calls.last().reply->succeed(singleRowPreview(edited, {}, QStringLiteral("manual-job-preview")));
            QVERIFY(controller.previewValid());
            QCOMPARE(controller.currentCandidate().value("job_id").toInt(), reviewedJob);
            QVERIFY(!controller.currentEvidence().value("job_candidate_pending").toBool());
            QCOMPARE(controller.currentEvidence().value("job_candidate_id").toInt(), originalJob);
            QCOMPARE(controller.currentCandidate().value("duty_name").toString(), reviewedDuty);
            QVERIFY(!controller.currentEvidence().value("duty_candidate_pending").toBool());
            QCOMPARE(controller.currentEvidence().value("ocr_duty_name").toString(), recognizedDuty);
            QCOMPARE(controller.selectedCount(), 1); // The explicitly chosen screenshot survives a valid re-preview.
        }
        for (const auto &call : backend.calls)
            QCOMPARE(call.type, QStringLiteral("PreviewRunImport"));
    }

    void derivedDutyCategoryIsOnlyADisplayProjection()
    {
        ImportBackend backend;
        mr::ImportRecordsController controller;
        controller.setBackend(&backend);
        controller.importText(QStringLiteral("副本\n合成副本"));
        const QJsonObject candidate{{"duty_name", QStringLiteral("合成副本")}, {"source_key", "synthetic-source"}};
        const QJsonObject run{{"duty_name", QStringLiteral("合成副本")}, {"duty_category", QStringLiteral("四人迷宫")}};
        backend.calls.last().reply->succeed(singleRowPreview(candidate, run));
        QCOMPARE(controller.currentCandidate().value("duty_category").toString(), QStringLiteral("四人迷宫"));
        QVERIFY(!controller.rows().first().toMap().value("candidate").toMap().contains("duty_category"));
        controller.revalidate();
        const QJsonObject sent = backend.calls.last().payload.value("rows").toArray().first().toObject();
        QCOMPARE(sent, candidate);
        QVERIFY(!sent.contains("duty_category"));
        backend.calls.last().reply->succeed(singleRowPreview(sent, run));
        QCOMPARE(controller.currentCandidate().value("duty_category").toString(), QStringLiteral("四人迷宫"));
        QVERIFY(!controller.rows().first().toMap().value("candidate").toMap().contains("duty_category"));
    }

    void explicitDutyCategoryEditClearsDerivedDisplay_data()
    {
        QTest::addColumn<bool>("clearCategory");
        QTest::newRow("edit-derived-category") << false;
        QTest::newRow("clear-derived-category") << true;
    }

    void explicitDutyCategoryEditClearsDerivedDisplay()
    {
        QFETCH(bool, clearCategory);
        ImportBackend backend;
        mr::ImportRecordsController controller;
        controller.setBackend(&backend);
        controller.importText(QStringLiteral("副本\n合成副本"));
        const QJsonObject candidate{{"duty_name", QStringLiteral("合成副本")}};
        const QJsonObject run{{"duty_category", QStringLiteral("四人迷宫")}};
        backend.calls.last().reply->succeed(singleRowPreview(candidate, run));
        QCOMPARE(controller.currentCandidate().value("duty_category").toString(), QStringLiteral("四人迷宫"));
        const QVariant category = clearCategory ? QVariant() : QVariant(QStringLiteral("InvalidCategory"));
        controller.updateCandidate(0, {{"duty_category", category}});
        QVERIFY(!controller.previewValid());
        QCOMPARE(controller.selectedCount(), 0);
        const QVariantMap edited = controller.currentCandidate();
        QVERIFY(edited.contains("duty_category"));
        if (clearCategory)
            QVERIFY(edited.value("duty_category").isNull());
        else
            QCOMPARE(edited.value("duty_category").toString(), QStringLiteral("InvalidCategory"));
        QVERIFY(!controller.rows().first().toMap().value("run").toMap().contains("duty_category"));
        controller.revalidate();
        const QJsonObject sent = backend.calls.last().payload.value("rows").toArray().first().toObject();
        QVERIFY(sent.contains("duty_category"));
        if (clearCategory)
            QVERIFY(sent.value("duty_category").isNull());
        else
            QCOMPARE(sent.value("duty_category").toString(), QStringLiteral("InvalidCategory"));
        backend.calls.last().reply->succeed(singleRowPreview(sent));
        QCOMPARE(controller.currentCandidate().value("duty_category").toString(),
                 clearCategory ? QString() : QStringLiteral("InvalidCategory"));
    }

    void renamingDutyClearsPreviousIdentityAndCategory()
    {
        ImportBackend backend;
        mr::ImportRecordsController controller;
        controller.setBackend(&backend);
        controller.importText(QStringLiteral("副本\n旧合成副本"));
        const QJsonObject original{{"duty_name", QStringLiteral("旧合成副本")}, {"content_id", 100},
                                   {"territory_id", 200}, {"duty_category", QStringLiteral("四人迷宫")},
                                   {"duty_source", "catalog"}, {"source_key", "synthetic-source"},
                                   {"reflection_text", QStringLiteral("保留合成心得")}};
        backend.calls.last().reply->succeed(singleRowPreview(original, original));
        controller.updateCandidate(0, {{"duty_name", QStringLiteral("新合成副本")}});
        QVERIFY(!controller.previewValid());
        QCOMPARE(controller.selectedCount(), 0);
        const QVariantMap displayRun = controller.rows().first().toMap().value("run").toMap();
        for (const QString &key : {QStringLiteral("content_id"), QStringLiteral("territory_id"),
                                  QStringLiteral("duty_category"), QStringLiteral("duty_source")}) {
            QVERIFY2(!controller.currentCandidate().contains(key), qPrintable(key));
            QVERIFY2(!displayRun.contains(key), qPrintable(key));
        }
        controller.revalidate();
        const QJsonObject edited = backend.calls.last().payload.value("rows").toArray().first().toObject();
        QCOMPARE(edited.value("duty_name").toString(), QStringLiteral("新合成副本"));
        QCOMPARE(edited.value("source_key").toString(), QStringLiteral("synthetic-source"));
        QCOMPARE(edited.value("reflection_text").toString(), QStringLiteral("保留合成心得"));
        for (const QString &key : {QStringLiteral("content_id"), QStringLiteral("territory_id"),
                                  QStringLiteral("duty_category"), QStringLiteral("duty_source")})
            QVERIFY2(!edited.contains(key), qPrintable(key));
        const QJsonObject resolved{{"duty_name", QStringLiteral("新合成副本")}, {"content_id", 300},
                                   {"territory_id", 400}, {"duty_category", QStringLiteral("讨伐歼灭战")},
                                   {"duty_source", "catalog"}};
        backend.calls.last().reply->succeed(singleRowPreview(edited, resolved, QStringLiteral("new-duty-preview")));
        QVERIFY(controller.previewValid());
        QCOMPARE(controller.currentCandidate().value("duty_category").toString(), QStringLiteral("讨伐歼灭战"));
        QVERIFY(!controller.rows().first().toMap().value("candidate").toMap().contains("duty_category"));
        controller.revalidate();
        QVERIFY(!backend.calls.last().payload.value("rows").toArray().first().toObject().contains("duty_category"));
        backend.calls.last().reply->succeed(singleRowPreview(edited, resolved));

        // An explicit replacement supplied in the same edit is new evidence,
        // so clearing the previous identity must not discard these values.
        controller.updateCandidate(0, {{"duty_name", QStringLiteral("另一个合成副本")}, {"content_id", 500},
                                       {"territory_id", 600}, {"duty_category", QStringLiteral("大型任务")},
                                       {"duty_source", "manual"}});
        QVERIFY(!controller.previewValid());
        controller.revalidate();
        const QJsonObject replacement = backend.calls.last().payload.value("rows").toArray().first().toObject();
        QCOMPARE(replacement.value("content_id").toInt(), 500);
        QCOMPARE(replacement.value("territory_id").toInt(), 600);
        QCOMPARE(replacement.value("duty_category").toString(), QStringLiteral("大型任务"));
        QCOMPARE(replacement.value("duty_source").toString(), QStringLiteral("manual"));
    }

    void explicitTimeZoneEditInvalidatesOnlyLocalSourceUtc()
    {
        QTemporaryDir directory;
        QVERIFY(directory.isValid());
        const QString source = directory.filePath(QStringLiteral("native.json"));
        createFile(source, "[]");
        ImportBackend backend;
        mr::ImportRecordsController controller;
        controller.setBackend(&backend);
        controller.importFiles({source});
        const QString local = QStringLiteral("2026-10-08 18:00:00");
        const QString utc = QStringLiteral("2026-10-08T10:00:00Z");
        const QString offset = QStringLiteral("2026-10-08T18:00:00+08:00");
        QJsonArray rows;
        for (int i = 0; i < 5; ++i) {
            QJsonObject metadata{{"source_kind", "JSON"}, {"source_name", "native-origin"},
                                 {"source_recorded_at_utc", utc}};
            QJsonObject candidate{{"duty_name", QStringLiteral("合成副本")}, {"source_key", QString::number(i)},
                                  {"source_recorded_at_utc", utc}};
            if (i != 4) {
                const QString raw = i == 2 ? QStringLiteral("2026-10-08") : (i == 3 ? offset : local);
                metadata.insert("source_recorded_at", raw);
                if (i != 1) candidate.insert("source_recorded_at", raw); // Nested-only native provenance.
            }
            if (i == 2) { // A date has no synthesized midnight or gameplay time.
                candidate.insert("source_recorded_at_utc", QJsonValue::Null);
                metadata.insert("source_recorded_at_utc", QJsonValue::Null);
            }
            candidate.insert("import_metadata", metadata);
            rows.append(QJsonObject{{"row_number", i + 1}, {"status", "new"}, {"can_import", true},
                                    {"incomplete", true}, {"candidate", candidate},
                                    {"run", QJsonObject{{"pending_review", true}, {"matched_at_utc", QJsonValue::Null}}},
                                    {"errors", QJsonArray{}}, {"warnings", QJsonArray{}}});
        }
        const QJsonObject response{{"preview_id", "time-zone-preview"}, {"rows", rows},
                                   {"summary", QJsonObject{{"total", 5}, {"incomplete", 5}}}};
        backend.calls.last().reply->succeed(response);
        controller.revalidate(); // Merely reviewing a native export retains its known UTC.
        const QJsonArray unchanged = backend.calls.last().payload.value("rows").toArray();
        QCOMPARE(unchanged[0].toObject().value("source_recorded_at_utc").toString(), utc);
        QCOMPARE(unchanged[0].toObject().value("import_metadata").toObject().value("source_recorded_at_utc").toString(), utc);
        backend.calls.last().reply->succeed(response);
        controller.setTimeZone(QStringLiteral("+09:00"));
        QVERIFY(!controller.previewValid());
        QCOMPARE(controller.selectedCount(), 0);
        QVERIFY(!controller.currentCandidate().contains("source_recorded_at_utc"));
        controller.revalidate();
        const QJsonObject request = backend.calls.last().payload;
        QCOMPARE(request.value("source_kind").toString(), QStringLiteral("JSON"));
        QCOMPARE(request.value("time_zone").toString(), QStringLiteral("+09:00"));
        const QJsonArray edited = request.value("rows").toArray();
        QCOMPARE(edited.size(), 5);
        for (int i = 0; i < 3; ++i) {
            const QJsonObject candidate = edited[i].toObject();
            const QJsonObject metadata = candidate.value("import_metadata").toObject();
            QVERIFY(!candidate.contains("source_recorded_at_utc"));
            QVERIFY(!metadata.contains("source_recorded_at_utc"));
            QCOMPARE(metadata.value("source_recorded_at").toString(), i == 2 ? QStringLiteral("2026-10-08") : local);
            QCOMPARE(metadata.value("source_name").toString(), QStringLiteral("native-origin"));
            QCOMPARE(candidate.value("source_key").toString(), QString::number(i));
            QVERIFY(!candidate.contains("matched_at_utc"));
        }
        QCOMPARE(edited[0].toObject().value("source_recorded_at").toString(), local);
        QCOMPARE(edited[2].toObject().value("source_recorded_at").toString(), QStringLiteral("2026-10-08"));
        QVERIFY(controller.rows()[2].toMap().value("incomplete").toBool());
        QVERIFY(controller.rows()[2].toMap().value("run").toMap().value("pending_review").toBool());
        for (int i : {3, 4}) {
            const QJsonObject candidate = edited[i].toObject();
            QCOMPARE(candidate.value("source_recorded_at_utc").toString(), utc);
            QCOMPARE(candidate.value("import_metadata").toObject().value("source_recorded_at_utc").toString(), utc);
        }
        QCOMPARE(edited[3].toObject().value("source_recorded_at").toString(), offset);
        QVERIFY(!edited[4].toObject().contains("source_recorded_at"));
    }

    void cancelledPreviewCannotRestoreCommitEligibility()
    {
        ImportBackend backend;
        mr::ImportRecordsController controller;
        controller.setBackend(&backend);
        controller.importText(QStringLiteral("副本\n合成副本"));
        auto reply = backend.calls.last().reply;
        controller.cancel();
        QVERIFY(!controller.busy());
        reply->succeed(preview());
        QVERIFY(!controller.previewValid());
        QVERIFY(controller.rows().isEmpty());
    }

    void timedOutCommitRetriesSameRequestId()
    {
        ImportBackend backend;
        mr::ImportRecordsController controller;
        controller.setBackend(&backend);
        controller.importText(QStringLiteral("副本\n合成副本"));
        backend.calls.last().reply->succeed(preview());
        controller.setOwnRecordsConfirmed(true);
        controller.commit();
        const QString id = backend.calls.last().requestId;
        const QJsonObject request = backend.calls.last().payload;
        backend.calls.last().reply->fail(QStringLiteral("ERR_TIMEOUT"), QStringLiteral("尚未确认"));
        QVERIFY(controller.pendingCommitConfirmation());
        controller.updateCandidate(0, {{"duty_name", QStringLiteral("不得改变未知结果的批次")}});
        controller.setAllImportableSelected(false);
        controller.cancel();
        controller.reset();
        QVERIFY(controller.pendingCommitConfirmation());
        QVERIFY(controller.previewValid());
        QCOMPARE(controller.selectedCount(), 1);
        QCOMPARE(controller.currentCandidate().value("duty_name").toString(), QStringLiteral("合成副本"));
        QVERIFY(controller.canCommit());
        controller.commit();
        QCOMPARE(backend.calls.last().requestId, id);
        QCOMPARE(backend.calls.last().payload, request);
    }

    void destroyedControllerAndSynchronousReply()
    {
        ImportBackend backend;
        auto *controller = new mr::ImportRecordsController;
        controller->setBackend(&backend);
        controller->importText(QStringLiteral("副本\n合成副本"));
        auto reply = backend.calls.last().reply;
        delete controller;
        reply->succeed(preview());
        backend.synchronous = true;
        backend.synchronousAnswer = preview();
        mr::ImportRecordsController sync;
        sync.setBackend(&backend);
        sync.importText(QStringLiteral("副本\n合成副本"));
        QVERIFY(sync.previewValid());
        QVERIFY(!sync.busy());
    }

    // The baseline question: offered only when a baseline is stored and selected rows would add
    // to the progress; defaults by source kind; travels with the commit only when answered yes.
    void baselineDeductionFollowsSourceKindSelectionAndAnswer()
    {
        ImportBackend backend;
        mr::ImportRecordsController controller;
        controller.setBackend(&backend);
        controller.setBaselineCount(1500);
        controller.importText(QStringLiteral("副本\n合成副本"));
        QJsonObject response = preview();
        QJsonArray rows = response.value("rows").toArray();
        QJsonObject first = rows[0].toObject();
        first.insert("incomplete", false);
        first.insert("run", QJsonObject{{"result", "COMPLETED"}, {"contributes_to_goal", true}, {"soft_deleted", false},
                                        {"import_metadata", QJsonObject{{"mentor_confirmed", true}}}});
        rows[0] = first;
        response.insert("rows", rows);
        backend.calls.last().reply->succeed(response);
        QCOMPARE(controller.contributingSelectedCount(), 1);
        QVERIFY(controller.baselineChoiceOffered());
        QVERIFY(controller.deductFromBaseline());
        QCOMPARE(controller.baselineDeductionPreview(), 1);
        controller.setRowSelected(0, false);
        QCOMPARE(controller.contributingSelectedCount(), 0);
        QVERIFY(!controller.baselineChoiceOffered());
        controller.setRowSelected(0, true);
        controller.setOwnRecordsConfirmed(true);
        controller.commit();
        QCOMPARE(backend.calls.last().type, QStringLiteral("CommitRunImport"));
        QVERIFY(backend.calls.last().payload.value("deduct_from_baseline").toBool());
        controller.setDeductFromBaseline(false); // locked while the commit is unanswered
        QVERIFY(controller.deductFromBaseline());
        backend.calls.last().reply->succeed({{"imported_count", 1}, {"duplicate_count", 0}, {"conflict_count", 0},
                                            {"baseline_deducted_count", 1}, {"baseline_completed_count", 1499}});
        QCOMPARE(controller.phase(), QStringLiteral("complete"));
        QVERIFY2(controller.statusText().contains(QStringLiteral("1500 改为 1499")), qPrintable(controller.statusText()));

        // Answered no: nothing travels, and the answer survives a revalidation of the same batch.
        controller.importText(QStringLiteral("副本\n合成副本"));
        backend.calls.last().reply->succeed(response);
        QVERIFY(controller.deductFromBaseline());
        controller.setDeductFromBaseline(false);
        controller.updateCandidate(0, {{"duty_name", QStringLiteral("改名副本")}});
        controller.revalidate();
        backend.calls.last().reply->succeed(response);
        QVERIFY(!controller.deductFromBaseline());
        controller.setOwnRecordsConfirmed(true);
        controller.commit();
        QVERIFY(!backend.calls.last().payload.contains("deduct_from_baseline"));
        backend.calls.last().reply->succeed({{"imported_count", 1}, {"duplicate_count", 0}, {"conflict_count", 0},
                                            {"baseline_deducted_count", 0}, {"baseline_completed_count", 1500}});
        QVERIFY(!controller.statusText().contains(QStringLiteral("基数")));

        // No stored baseline: the question is not asked and the default answer does not travel.
        controller.setBaselineCount(0);
        controller.importText(QStringLiteral("副本\n合成副本"));
        backend.calls.last().reply->succeed(response);
        QVERIFY(!controller.baselineChoiceOffered());
        controller.setOwnRecordsConfirmed(true);
        controller.commit();
        QVERIFY(!backend.calls.last().payload.contains("deduct_from_baseline"));
        backend.calls.last().reply->succeed({{"imported_count", 1}, {"duplicate_count", 0}, {"conflict_count", 0}});

        // Incomplete, excluded or unconfirmed rows never count towards the question.
        controller.setBaselineCount(10);
        controller.importText(QStringLiteral("副本\n合成副本"));
        QJsonObject excluded = response;
        QJsonArray excludedRows = excluded.value("rows").toArray();
        QJsonObject row = excludedRows[0].toObject();
        QJsonObject run = row.value("run").toObject();
        run.insert("contributes_to_goal", false);
        row.insert("run", run);
        excludedRows[0] = row;
        excluded.insert("rows", excludedRows);
        backend.calls.last().reply->succeed(excluded);
        QCOMPARE(controller.contributingSelectedCount(), 0);
        QVERIFY(!controller.baselineChoiceOffered());
    }

    void backupAndNativeJsonDefaultToKeepingTheBaseline()
    {
        for (const QString &kind : {QStringLiteral("BACKUP"), QStringLiteral("JSON"), QStringLiteral("XLSX")}) {
            ImportBackend backend;
            mr::ImportRecordsController controller;
            controller.setBackend(&backend);
            controller.setBaselineCount(1500);
            QTemporaryDir dir;
            const QString extension = kind == QLatin1String("BACKUP") ? QStringLiteral("db")
                : kind == QLatin1String("JSON") ? QStringLiteral("json") : QStringLiteral("xlsx");
            const QString path = dir.filePath(QStringLiteral("history.") + extension);
            QFile file(path);
            QVERIFY(file.open(QIODevice::WriteOnly));
            file.write("x");
            file.close();
            controller.importFiles({path});
            QCOMPARE(backend.calls.last().type, QStringLiteral("PreviewRunImport"));
            QCOMPARE(backend.calls.last().payload.value("source_kind").toString(), kind);
            QJsonObject response = preview();
            QJsonArray rows = response.value("rows").toArray();
            QJsonObject first = rows[0].toObject();
            first.insert("incomplete", false);
            first.insert("run", QJsonObject{{"result", "COMPLETED"}, {"contributes_to_goal", true}});
            rows[0] = first;
            response.insert("rows", rows);
            backend.calls.last().reply->succeed(response);
            QVERIFY2(controller.baselineChoiceOffered(), qPrintable(kind));
            QCOMPARE(controller.deductFromBaseline(), kind == QLatin1String("XLSX"));
        }
    }

    void localPathAndFormatBoundary()
    {
        QCOMPARE(mr::ImportRecordsController::sourceKindForPath("C:/records.XLSX"), QStringLiteral("XLSX"));
        QCOMPARE(mr::ImportRecordsController::sourceKindForPath("C:/mentor.sqlite3"), QStringLiteral("BACKUP"));
        QVERIFY(mr::ImportRecordsController::localPath(QUrl("https://example.invalid/a.png")).isEmpty());
        QVERIFY(mr::ImportRecordsController::localPath("relative.csv").isEmpty());
        QVERIFY(mr::ImportRecordsController::localPath("\\\\host\\share\\image.png").isEmpty());
    }

    void unavailableEngineDoesNotWriteHistory()
    {
        QTemporaryDir directory;
        QVERIFY(directory.isValid());
        const QString imagePath = directory.filePath("sample.png");
        QImage image(100, 100, QImage::Format_RGB32);
        image.fill(Qt::white);
        QVERIFY(image.save(imagePath));
        ImportBackend backend;
        mr::ImportRecordsController controller;
        controller.setBackend(&backend);
        controller.setOcrApplicationDirectoryForTesting(directory.path());
        controller.importFiles({imagePath});
        QVERIFY(!controller.busy());
        QVERIFY(controller.errorText().contains(QStringLiteral("缺少")));
        QVERIFY(backend.calls.isEmpty());
    }

    void productionDialogLoadsAndActionsRemainVisibleAtMinimumSize_data()
    {
        QTest::addColumn<bool>("dark");
        QTest::newRow("dark") << true;
        QTest::newRow("light") << false;
    }

    void productionDialogLoadsAndActionsRemainVisibleAtMinimumSize()
    {
        QFETCH(bool, dark);
        ImportBackend backend;
        mr::ImportRecordsController controller;
        controller.setBackend(&backend);
        controller.importText(QStringLiteral("副本\n合成副本"));
        QJsonObject initialPreview = preview();
        QJsonArray initialRows = initialPreview.value("rows").toArray();
        QJsonObject initialRow = initialRows.first().toObject();
        QJsonObject initialCandidate = initialRow.value("candidate").toObject();
        initialCandidate.insert("content_id", 100);
        initialCandidate.insert("territory_id", 200);
        initialCandidate.insert("duty_source", "catalog");
        initialRow.insert("candidate", initialCandidate);
        initialRow.insert("run", QJsonObject{{"content_id", 100}, {"territory_id", 200},
                                             {"duty_category", QStringLiteral("四人迷宫")}, {"duty_source", "catalog"}});
        initialRows[0] = initialRow;
        initialPreview.insert("rows", initialRows);
        backend.calls.last().reply->succeed(initialPreview);
        ImportThemeState theme;
        theme.dark = dark;
        QQmlEngine engine;
        engine.rootContext()->setContextProperty(QStringLiteral("ImportRecords"), &controller);
        engine.rootContext()->setContextProperty(QStringLiteral("App"), &theme);
        engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QStringList warnings;
        connect(&engine, &QQmlEngine::warnings, this, [&warnings](const QList<QQmlError> &errors) {
            for (const QQmlError &error : errors)
                warnings.append(error.toString());
        });
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 980; height: 640; visible: true
    property color expectedLabelColor: Theme.textPrimary
    property color expectedHintColor: Theme.textMuted
    property color expectedIndicatorColor: Theme.insetBackground
    property color expectedFocusColor: Theme.accent
    ImportRecordsDialog { id: importDialog; Component.onCompleted: open() }
})", QUrl::fromLocalFile(QString::fromUtf8(MR_DESKTOP_QML_DIR) + QStringLiteral("/dialogs/ImportDialogProbe.qml")));
        QTRY_VERIFY_WITH_TIMEOUT(component.status() != QQmlComponent::Loading, 5000);
        QVERIFY2(!component.isError(), qPrintable(component.errorString()));
        std::unique_ptr<QObject> root(component.create());
        QVERIFY2(root != nullptr, qPrintable(component.errorString()));
        auto *window = qobject_cast<QQuickWindow *>(root.get());
        QVERIFY(window);
        QVERIFY(QTest::qWaitForWindowExposed(window));
        QTest::qWait(100);
        auto *commit = root->findChild<QQuickItem *>(QStringLiteral("importCommit"));
        auto *revalidate = root->findChild<QQuickItem *>(QStringLiteral("importRevalidate"));
        auto *cancel = root->findChild<QQuickItem *>(QStringLiteral("importCancel"));
        auto *duty = root->findChild<QQuickItem *>(QStringLiteral("importDutyName"));
        auto *category = root->findChild<QQuickItem *>(QStringLiteral("importDutyCategory"));
        auto *jobWarning = root->findChild<QQuickItem *>(QStringLiteral("importJobCandidateWarning"));
        auto *job = root->findChild<QQuickItem *>(QStringLiteral("importJob"));
        auto *result = root->findChild<QQuickItem *>(QStringLiteral("importResult"));
        auto *own = root->findChild<QQuickItem *>(QStringLiteral("importOwnRecordsConfirmed"));
        auto *gameFacts = root->findChild<QQuickItem *>(QStringLiteral("importShowGameFacts"));
        auto *paste = root->findChild<QQuickItem *>(QStringLiteral("importPastedText"));
        auto *reflection = root->findChild<QQuickItem *>(QStringLiteral("importReflectionText"));
        auto *choose = root->findChild<QQuickItem *>(QStringLiteral("importChooseFiles"));
        auto *content = root->findChild<QQuickItem *>(QStringLiteral("importContent"));
        auto *zonePreset = root->findChild<QQuickItem *>(QStringLiteral("importTimeZonePreset"));
        auto *customZone = root->findChild<QQuickItem *>(QStringLiteral("importTimeZone"));
        auto *recordList = root->findChild<QQuickItem *>(QStringLiteral("importCandidates"));
        auto *manualPasteOpen = root->findChild<QQuickItem *>(QStringLiteral("importManualPasteOpen"));
        auto *manualPasteDialog = root->findChild<QObject *>(QStringLiteral("importManualPasteDialog"));
        auto *dialog = root->findChild<QObject *>(QStringLiteral("importRecordsDialog"));
        QVERIFY(commit && revalidate && cancel && duty && category && jobWarning && job && result && own && gameFacts && paste && reflection
                && choose && content && zonePreset && customZone && recordList && manualPasteOpen && manualPasteDialog && dialog);
        QVERIFY(!category->property("readOnly").toBool());
        QCOMPARE(category->property("text").toString(), QStringLiteral("四人迷宫"));
        QVERIFY(!jobWarning->isVisible()); // A native table has no screenshot candidate evidence.
        QTRY_VERIFY(content->hasActiveFocus());
        QVERIFY(!choose->hasActiveFocus());
        QVERIFY(!choose->property("keyboardFocusVisible").toBool());
        auto *chooseBackground = choose->property("background").value<QQuickItem *>();
        QVERIFY(chooseBackground);
        QCOMPARE(QQmlProperty(chooseBackground, "border.width").read().toInt(), 1);
        // Opening has neutral focus, but real keyboard traversal keeps the
        // existing AppButton marker. Never invoke the native file picker here.
        QTest::keyClick(window, Qt::Key_Tab);
        QTRY_VERIFY(choose->hasActiveFocus());
        QVERIFY(choose->property("keyboardFocusVisible").toBool());
        QCOMPARE(QQmlProperty(chooseBackground, "border.width").read().toInt(), 3);
        QTest::keyClick(window, Qt::Key_Tab, Qt::ShiftModifier);
        auto *previous = window->activeFocusItem();
        QVERIFY(previous && previous != choose && previous->hasActiveFocus());
        QVERIFY(previous->property("keyboardFocusVisible").toBool());
        QTest::keyClick(window, Qt::Key_Tab);
        QTRY_VERIFY(choose->hasActiveFocus());

        const auto center = [](QQuickItem *item) {
            return item->mapToScene(QPointF(item->width() / 2, item->height() / 2)).toPoint();
        };
        // The list retains focus so two ordinary Space presses can select and
        // deselect the same row without another click between them.
        const int beforeListKeys = backend.calls.size();
        QCOMPARE(controller.selectedCount(), 1);
        recordList->forceActiveFocus(Qt::TabFocusReason);
        QTest::keyClick(window, Qt::Key_Space);
        QTRY_COMPARE(controller.selectedCount(), 0);
        QTRY_VERIFY(recordList->hasActiveFocus());
        QTest::keyClick(window, Qt::Key_Space);
        QTRY_COMPARE(controller.selectedCount(), 1);
        QTRY_VERIFY(recordList->hasActiveFocus());
        QCOMPARE(controller.currentRow(), 0);
        QCOMPARE(backend.calls.size(), beforeListKeys);

        // Ctrl+V and ordinary editing keys inside the reflection editor keep
        // native text behavior rather than importing a new source or moving rows.
        const QString originalReflectionText = reflection->property("text").toString();
        reflection->forceActiveFocus(Qt::MouseFocusReason);
        QGuiApplication::clipboard()->setText(QStringLiteral("Native clipboard text"));
        QTest::keyClick(window, Qt::Key_A, Qt::ControlModifier);
        QTest::keyClick(window, Qt::Key_V, Qt::ControlModifier);
        QTRY_COMPARE(reflection->property("text").toString(), QStringLiteral("Native clipboard text"));
        QTest::keyClick(window, Qt::Key_Return);
        QTest::keyClick(window, Qt::Key_Space);
        QTRY_COMPARE(reflection->property("text").toString(), QStringLiteral("Native clipboard text\n "));
        QCOMPARE(controller.currentRow(), 0);
        QCOMPARE(backend.calls.size(), beforeListKeys);
        QVERIFY(controller.previewValid());
        QVERIFY(reflection->hasActiveFocus());
        QGuiApplication::clipboard()->setText(originalReflectionText);
        QTest::keyClick(window, Qt::Key_A, Qt::ControlModifier);
        QTest::keyClick(window, Qt::Key_V, Qt::ControlModifier);
        QTRY_COMPARE(reflection->property("text").toString(), originalReflectionText);
        const QRect windowBounds(0, 0, window->width(), window->height());
        QVERIFY(windowBounds.contains(center(duty)));
        QVERIFY(windowBounds.contains(center(category)));
        QVERIFY(windowBounds.contains(center(revalidate)));
        QVERIFY(windowBounds.contains(center(commit)));
        QTest::mouseClick(window, Qt::LeftButton, Qt::NoModifier, center(category));
        QTRY_VERIFY(category->hasActiveFocus());
        QVERIFY(controller.previewValid());
        const int beforeCategoryBlur = backend.calls.size();
        QTest::mouseClick(window, Qt::LeftButton, Qt::NoModifier, center(revalidate));
        QTRY_COMPARE(backend.calls.size(), beforeCategoryBlur + 1);
        const QJsonObject categoryUnchanged = backend.calls.last().payload.value("rows").toArray().first().toObject();
        QVERIFY(!categoryUnchanged.contains("duty_category"));
        QCOMPARE(categoryUnchanged.value("content_id").toInt(), 100);
        QCOMPARE(categoryUnchanged.value("territory_id").toInt(), 200);
        backend.calls.last().reply->succeed(initialPreview);
        QTest::mouseClick(window, Qt::LeftButton, Qt::NoModifier, center(duty));
        QTRY_VERIFY(duty->hasActiveFocus());
        QTest::keyClick(window, Qt::Key_A, Qt::ControlModifier);
        typeAscii(window, "MouseEditedDuty");
        QCOMPARE(duty->property("text").toString(), QStringLiteral("MouseEditedDuty"));
        QCOMPARE(controller.currentCandidate().value("duty_name").toString(), QStringLiteral("合成副本"));
        QVERIFY(controller.previewValid()); // Text is still in the active editor.
        const int beforeMousePreview = backend.calls.size();
        QTest::mouseClick(window, Qt::LeftButton, Qt::NoModifier, center(revalidate));
        QTRY_COMPARE(backend.calls.size(), beforeMousePreview + 1);
        QCOMPARE(backend.calls.last().type, QStringLiteral("PreviewRunImport"));
        const QJsonObject mouseEdited = backend.calls.last().payload.value("rows").toArray().first().toObject();
        QCOMPARE(mouseEdited.value("duty_name").toString(), QStringLiteral("MouseEditedDuty"));
        for (const QString &key : {QStringLiteral("content_id"), QStringLiteral("territory_id"),
                                  QStringLiteral("duty_category"), QStringLiteral("duty_source")})
            QVERIFY2(!mouseEdited.contains(key), qPrintable(key));
        QVERIFY(!duty->hasActiveFocus());
        backend.calls.last().reply->succeed(singleRowPreview(mouseEdited));
        controller.setOwnRecordsConfirmed(true);
        QTRY_VERIFY(commit->isEnabled());
        QTest::mouseClick(window, Qt::LeftButton, Qt::NoModifier, center(duty));
        QTRY_VERIFY(duty->hasActiveFocus());
        QTest::keyClick(window, Qt::Key_A, Qt::ControlModifier);
        typeAscii(window, "MouseUnvalidatedDuty");
        QCOMPARE(duty->property("text").toString(), QStringLiteral("MouseUnvalidatedDuty"));
        QCOMPARE(controller.currentCandidate().value("duty_name").toString(), QStringLiteral("MouseEditedDuty"));
        QVERIFY(controller.previewValid());
        QVERIFY(commit->isEnabled());
        const int beforeMouseCommit = backend.calls.size();
        QTest::mouseClick(window, Qt::LeftButton, Qt::NoModifier, center(commit));
        QCOMPARE(backend.calls.size(), beforeMouseCommit); // Editor blur must block the stale commit.
        QVERIFY(!controller.previewValid());
        QCOMPARE(controller.selectedCount(), 0);
        QCOMPARE(controller.currentCandidate().value("duty_name").toString(), QStringLiteral("MouseUnvalidatedDuty"));
        QVERIFY(!duty->hasActiveFocus());
        QVERIFY(!controller.committing());
        controller.setOwnRecordsConfirmed(false);
        QTest::mouseClick(window, Qt::LeftButton, Qt::NoModifier, center(revalidate));
        QTRY_COMPARE(backend.calls.size(), beforeMouseCommit + 1);
        const QJsonObject unvalidatedDuty = backend.calls.last().payload.value("rows").toArray().first().toObject();
        QCOMPARE(unvalidatedDuty.value("duty_name").toString(), QStringLiteral("MouseUnvalidatedDuty"));
        backend.calls.last().reply->succeed(singleRowPreview(unvalidatedDuty,
            QJsonObject{{"duty_category", QStringLiteral("四人迷宫")}}));
        QTest::mouseClick(window, Qt::LeftButton, Qt::NoModifier, center(category));
        QTRY_VERIFY(category->hasActiveFocus());
        QTest::keyClick(window, Qt::Key_A, Qt::ControlModifier);
        typeAscii(window, "InvalidCategory");
        const int beforeCategoryEdit = backend.calls.size();
        QTest::mouseClick(window, Qt::LeftButton, Qt::NoModifier, center(revalidate));
        QTRY_COMPARE(backend.calls.size(), beforeCategoryEdit + 1);
        const QJsonObject explicitCategory = backend.calls.last().payload.value("rows").toArray().first().toObject();
        QCOMPARE(explicitCategory.value("duty_category").toString(), QStringLiteral("InvalidCategory"));
        QVERIFY(!controller.rows().first().toMap().value("run").toMap().contains("duty_category"));
        backend.calls.last().reply->succeed(singleRowPreview(explicitCategory,
            QJsonObject{{"duty_category", QStringLiteral("四人迷宫")}}));
        QCOMPARE(category->property("text").toString(), QStringLiteral("InvalidCategory"));
        QTest::mouseClick(window, Qt::LeftButton, Qt::NoModifier, center(category));
        QTRY_VERIFY(category->hasActiveFocus());
        QTest::keyClick(window, Qt::Key_A, Qt::ControlModifier);
        QTest::keyClick(window, Qt::Key_Backspace);
        const int beforeCategoryClear = backend.calls.size();
        QTest::mouseClick(window, Qt::LeftButton, Qt::NoModifier, center(revalidate));
        QTRY_COMPARE(backend.calls.size(), beforeCategoryClear + 1);
        const QJsonObject clearedCategory = backend.calls.last().payload.value("rows").toArray().first().toObject();
        QVERIFY(clearedCategory.contains("duty_category"));
        QVERIFY(clearedCategory.value("duty_category").isNull());
        QVERIFY(!controller.rows().first().toMap().value("run").toMap().contains("duty_category"));
        QVERIFY(controller.currentCandidate().value("duty_category").toString().isEmpty());
        backend.calls.last().reply->succeed(singleRowPreview(clearedCategory));
        QCOMPARE(category->property("text").toString(), QString());

        QCOMPARE(controller.timeZone(), QStringLiteral("+08:00"));
        QVERIFY(zonePreset->property("currentText").toString().contains(QStringLiteral("北京时间")));
        QVERIFY(!customZone->isVisible());
        const int beforeZoneSelection = backend.calls.size();
        zonePreset->forceActiveFocus(Qt::TabFocusReason);
        QTest::keyClick(window, Qt::Key_Down);
        QTRY_COMPARE(controller.timeZone(), QStringLiteral("+09:00"));
        QVERIFY(zonePreset->property("currentText").toString().contains(QStringLiteral("日本")));
        QVERIFY(!controller.previewValid());
        QCOMPARE(controller.selectedCount(), 0);
        QCOMPARE(backend.calls.size(), beforeZoneSelection);
        controller.revalidate();
        QCOMPARE(backend.calls.last().payload.value("time_zone").toString(), QStringLiteral("+09:00"));
        backend.calls.last().reply->succeed(preview(QStringLiteral("japan-preview")));

        // Loading an existing non-preset offset reveals it without replacing it.
        controller.setTimeZone(QStringLiteral("+05:30"));
        QTRY_VERIFY(customZone->isVisible());
        QCOMPARE(customZone->property("text").toString(), QStringLiteral("+05:30"));
        QCOMPARE(controller.timeZone(), QStringLiteral("+05:30"));
        QVERIFY(zonePreset->property("currentText").toString().contains(QStringLiteral("自定义")));
        QTest::qWait(50); // Let the additional custom row participate in layout.
        const QPointF customFooterPosition = commit->mapToScene(QPointF());
        QVERIFY(customFooterPosition.x() + commit->width() <= window->width() + 1);
        QVERIFY(customFooterPosition.y() + commit->height() <= window->height() + 1);
        controller.setTimeZone(QStringLiteral("+09:00"));
        QTRY_VERIFY(!customZone->isVisible());
        zonePreset->forceActiveFocus(Qt::TabFocusReason);
        QTest::keyClick(window, Qt::Key_Down); // UTC preset.
        QTRY_COMPARE(controller.timeZone(), QStringLiteral("+00:00"));
        QTest::keyClick(window, Qt::Key_Down); // Custom is an editing mode, not an empty zone.
        QTRY_VERIFY(customZone->isVisible());
        QCOMPARE(controller.timeZone(), QStringLiteral("+00:00"));
        customZone->forceActiveFocus(Qt::TabFocusReason);
        QTest::keyClick(window, Qt::Key_A, Qt::ControlModifier);
        typeAscii(window, "+05:30");
        QCOMPARE(controller.timeZone(), QStringLiteral("+00:00"));
        QVERIFY(customZone->hasActiveFocus());
        const int beforeCustomZonePreview = backend.calls.size();
        QTest::mouseClick(window, Qt::LeftButton, Qt::NoModifier, center(revalidate));
        QTRY_COMPARE(backend.calls.size(), beforeCustomZonePreview + 1);
        QTRY_COMPARE(controller.timeZone(), QStringLiteral("+05:30"));
        QCOMPARE(customZone->property("text").toString(), QStringLiteral("+05:30"));
        QCOMPARE(backend.calls.last().type, QStringLiteral("PreviewRunImport"));
        QCOMPARE(backend.calls.last().payload.value("time_zone").toString(), QStringLiteral("+05:30"));
        QVERIFY(!customZone->hasActiveFocus());
        backend.calls.last().reply->succeed(preview(QStringLiteral("custom-zone-mouse-preview")));
        zonePreset->forceActiveFocus(Qt::TabFocusReason);
        for (int i = 0; i < 3; ++i)
            QTest::keyClick(window, Qt::Key_Up);
        QTRY_COMPARE(controller.timeZone(), QStringLiteral("+08:00"));
        QTRY_VERIFY(!customZone->isVisible());
        controller.revalidate();
        backend.calls.last().reply->succeed(preview(QStringLiteral("restored-preview")));

        reflection->forceActiveFocus(Qt::MouseFocusReason);
        reflection->setProperty("text", QStringLiteral("逐行切换前编辑的合成全文"));
        QVERIFY(QMetaObject::invokeMethod(dialog, "selectRow", Q_ARG(QVariant, QVariant(1))));
        QTRY_VERIFY(content->hasActiveFocus());
        QVERIFY(!choose->hasActiveFocus());
        QVERIFY(!choose->property("keyboardFocusVisible").toBool());
        QCOMPARE(controller.rows()[0].toMap().value("candidate").toMap().value("reflection_text").toString(),
                 QStringLiteral("逐行切换前编辑的合成全文"));
        QCOMPARE(controller.rows()[1].toMap().value("candidate").toMap().value("reflection_text").toString(),
                 QStringLiteral("合成正文"));
        controller.revalidate();
        QJsonObject editedPreview = preview(QStringLiteral("row-edited-preview"));
        QJsonArray editedRows = editedPreview.value("rows").toArray();
        const QJsonArray editedCandidates = backend.calls.last().payload.value("rows").toArray();
        for (int i = 0; i < editedRows.size(); ++i) {
            QJsonObject row = editedRows[i].toObject();
            row.insert("candidate", editedCandidates[i]);
            editedRows[i] = row;
        }
        editedPreview.insert("rows", editedRows);
        backend.calls.last().reply->succeed(editedPreview);
        controller.setCurrentRow(0);
        for (auto *check : {own, gameFacts}) {
            auto *label = check->property("contentItem").value<QQuickItem *>();
            auto *indicator = check->property("indicator").value<QQuickItem *>();
            QVERIFY(label && indicator);
            QCOMPARE(label->property("color").value<QColor>(), root->property("expectedLabelColor").value<QColor>());
            QCOMPARE(indicator->property("color").value<QColor>(), root->property("expectedIndicatorColor").value<QColor>());
            check->forceActiveFocus(Qt::TabFocusReason);
            QTRY_VERIFY(check->property("visualFocus").toBool());
            QCOMPARE(QQmlProperty(indicator, "border.color").read().value<QColor>(), root->property("expectedFocusColor").value<QColor>());
            QCOMPARE(QQmlProperty(indicator, "border.width").read().toInt(), 2);
        }
        QCOMPARE(paste->property("placeholderTextColor").value<QColor>(), root->property("expectedHintColor").value<QColor>());
        QCOMPARE(reflection->property("placeholderTextColor").value<QColor>(), root->property("expectedHintColor").value<QColor>());
        own->forceActiveFocus(Qt::TabFocusReason);
        QTest::qWait(50);
        const QImage checkedFrame = window->grabWindow();
        QVERIFY(!checkedFrame.isNull());
        const qreal pixelRatio = qreal(checkedFrame.width()) / window->width();
        auto *ownLabel = own->property("contentItem").value<QQuickItem *>();
        auto *ownIndicator = own->property("indicator").value<QQuickItem *>();
        const qreal labelPadding = ownLabel->property("leftPadding").toReal();
        const QRectF labelRect(ownLabel->mapToScene(QPointF(labelPadding, 0)),
                              QSizeF(ownLabel->width() - labelPadding, ownLabel->height()));
        QVERIFY2(matchingRenderedPixels(checkedFrame, labelRect, pixelRatio,
                                        root->property("expectedLabelColor").value<QColor>()) > 10,
                 "Rendered own-records confirmation label must contain the theme foreground, not a black style fallback");
        const QPointF indicatorCenter = ownIndicator->mapToScene(QPointF(ownIndicator->width() / 2, ownIndicator->height() / 2));
        QCOMPARE(checkedFrame.pixelColor(qRound(indicatorCenter.x() * pixelRatio), qRound(indicatorCenter.y() * pixelRatio)),
                 root->property("expectedIndicatorColor").value<QColor>());
        QCOMPARE(result->property("currentIndex").toInt(), 1); // User-approved default COMPLETED.
        const QPointF position = commit->mapToScene(QPointF());
        QVERIFY(position.x() >= 0);
        QVERIFY(position.y() >= 0);
        QVERIFY(position.x() + commit->width() <= window->width() + 1);
        QVERIFY(position.y() + commit->height() <= window->height() + 1);
        QVERIFY(!commit->isEnabled());
        controller.setOwnRecordsConfirmed(true);
        QTRY_VERIFY(commit->isEnabled());
        controller.commit();
        const QString commitId = backend.calls.last().requestId;
        const QJsonObject commitPayload = backend.calls.last().payload;
        backend.calls.last().reply->fail(QStringLiteral("ERR_TIMEOUT"), QStringLiteral("尚未确认"));
        QTRY_VERIFY(commit->isEnabled());
        QVERIFY(!cancel->isEnabled());
        QTest::keyClick(window, Qt::Key_Escape);
        QVERIFY(dialog->property("visible").toBool());
        // An external owner may close the popup: reopening must retain the
        // uncertain operation even though the normal close action is disabled.
        QVERIFY(QMetaObject::invokeMethod(dialog, "close"));
        QVERIFY(QMetaObject::invokeMethod(dialog, "openDialog"));
        QVERIFY(controller.pendingCommitConfirmation());
        controller.commit();
        QCOMPARE(backend.calls.last().requestId, commitId);
        QCOMPARE(backend.calls.last().payload, commitPayload);
        backend.calls.last().reply->succeed({{"imported_count", 1}});
        controller.importText(QStringLiteral("副本\n合成副本"));
        backend.calls.last().reply->succeed(preview());
        controller.updateCandidate(0, {{"duty_name", QStringLiteral("已编辑合成副本")}});
        QTRY_VERIFY(!commit->isEnabled());
        controller.reset();
        QTest::qWait(50);
        QTRY_VERIFY(manualPasteOpen->isVisible());
        QTest::mouseClick(window, Qt::LeftButton, Qt::NoModifier, center(manualPasteOpen));
        QTRY_VERIFY(manualPasteDialog->property("visible").toBool());
        auto *pasteHint = root->findChild<QQuickItem *>(QStringLiteral("importPastePlaceholder"));
        QTRY_VERIFY(pasteHint && pasteHint->isVisible());
        QTest::qWait(50); // Let the popup and text glyphs render before the pixel assertion.
        const QImage emptyFrame = window->grabWindow();
        QVERIFY(!emptyFrame.isNull());
        const QRectF hintRect(pasteHint->mapToScene(QPointF()), QSizeF(pasteHint->width(), pasteHint->height()));
        QVERIFY2(matchingRenderedPixels(emptyFrame, hintRect, pixelRatio,
                                        root->property("expectedHintColor").value<QColor>()) > 10,
                 "Rendered paste hint must contain the theme muted foreground, not a black style fallback");
        QTest::keyClick(window, Qt::Key_Escape);
        QTRY_VERIFY(!manualPasteDialog->property("visible").toBool());
        QVERIFY(dialog->property("visible").toBool());
        QTemporaryDir externalSource;
        QVERIFY(externalSource.isValid());
        const QString externalJson = externalSource.filePath(QStringLiteral("external-jobs.json"));
        createFile(externalJson, "[]");
        controller.importFiles({externalJson});
        QCOMPARE(backend.calls.last().payload.value("source_kind").toString(), QStringLiteral("JSON"));
        const QJsonObject externalJob{{"duty_name", QStringLiteral("合成副本")}, {"job_id", 8},
                                      {"job_name", QStringLiteral("刻木匠")}};
        backend.calls.last().reply->succeed(singleRowPreview(externalJob));
        QTRY_COMPARE(job->property("currentIndex").toInt(), -1);
        QCOMPARE(job->property("displayText").toString(), QStringLiteral("刻木匠（来源职业）"));
        QTRY_VERIFY(jobWarning->isVisible());
        QVERIFY(jobWarning->property("text").toString().contains(QStringLiteral("请核对")));
        for (const QVariant &choice : controller.jobChoices())
            QVERIFY(choice.toMap().value("job_id").toInt() != 8);
        const int beforeExternalJobPreview = backend.calls.size();
        QTest::mouseClick(window, Qt::LeftButton, Qt::NoModifier, center(revalidate));
        QTRY_COMPARE(backend.calls.size(), beforeExternalJobPreview + 1);
        const QJsonObject preservedJob = backend.calls.last().payload.value("rows").toArray().first().toObject();
        QCOMPARE(preservedJob.value("job_id").toInt(), 8);
        QCOMPARE(preservedJob.value("job_name").toString(), QStringLiteral("刻木匠"));
        backend.calls.last().reply->succeed(singleRowPreview(preservedJob));
        QTest::keyClick(window, Qt::Key_Escape);
        QTRY_VERIFY(!dialog->property("visible").toBool());
        QVERIFY2(warnings.isEmpty(), qPrintable(warnings.join(QLatin1Char('\n'))));
    }

    void columnMappingRecoversUnrecognizedHeadersBeforeRowsExist_data()
    {
        QTest::addColumn<bool>("dark");
        QTest::addColumn<bool>("csvSource");
        QTest::newRow("dark-paste") << true << false;
        QTest::newRow("light-paste") << false << false;
        QTest::newRow("dark-csv") << true << true;
        QTest::newRow("light-csv") << false << true;
    }

    void columnMappingRecoversUnrecognizedHeadersBeforeRowsExist()
    {
        QFETCH(bool, dark);
        QFETCH(bool, csvSource);
        QTemporaryDir directory;
        QVERIFY(directory.isValid());
        ImportBackend backend;
        mr::ImportRecordsController controller;
        controller.setBackend(&backend);
        const QString pastedSource = QStringLiteral("活动\t说明\n第一副本\t合成正文\n第二副本\t另一行");
        const QString fileSource = directory.filePath(QStringLiteral("unrecognized-headers.csv"));
        if (csvSource) {
            createFile(fileSource, QStringLiteral("活动,说明\n第一副本,合成正文\n第二副本,另一行").toUtf8());
            controller.importFiles({fileSource});
        } else {
            controller.importText(pastedSource);
        }
        QCOMPARE(backend.calls.size(), 1);
        backend.calls.last().reply->fail("ERR_VALIDATION", QStringLiteral("表格没有可识别的记录列，请提供列映射。"));
        QVERIFY(controller.rows().isEmpty());
        QVERIFY(!controller.previewValid());
        QCOMPARE(controller.sourceKind(), csvSource ? QStringLiteral("CSV") : QStringLiteral("PASTE"));

        ImportThemeState theme;
        theme.dark = dark;
        QQmlEngine engine;
        engine.rootContext()->setContextProperty(QStringLiteral("ImportRecords"), &controller);
        engine.rootContext()->setContextProperty(QStringLiteral("App"), &theme);
        engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QStringList warnings;
        connect(&engine, &QQmlEngine::warnings, this, [&warnings](const QList<QQmlError> &errors) {
            for (const QQmlError &error : errors)
                warnings.append(error.toString());
        });
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 980; height: 640; visible: true
    ImportRecordsDialog { Component.onCompleted: open() }
})", QUrl::fromLocalFile(QString::fromUtf8(MR_DESKTOP_QML_DIR) + QStringLiteral("/dialogs/ImportMappingProbe.qml")));
        QTRY_VERIFY_WITH_TIMEOUT(component.status() != QQmlComponent::Loading, 5000);
        QVERIFY2(!component.isError(), qPrintable(component.errorString()));
        std::unique_ptr<QObject> root(component.create());
        QVERIFY2(root != nullptr, qPrintable(component.errorString()));
        auto *window = qobject_cast<QQuickWindow *>(root.get());
        QVERIFY(window);
        QVERIFY(QTest::qWaitForWindowExposed(window));
        auto *mappingOpen = root->findChild<QQuickItem *>(QStringLiteral("importMappingOpen"));
        auto *mappingDialog = root->findChild<QObject *>(QStringLiteral("importMappingDialog"));
        auto *mapDuty = root->findChild<QQuickItem *>(QStringLiteral("importMapDuty"));
        auto *mappingApply = root->findChild<QQuickItem *>(QStringLiteral("importMappingApply"));
        auto *recordList = root->findChild<QQuickItem *>(QStringLiteral("importCandidates"));
        auto *duty = root->findChild<QQuickItem *>(QStringLiteral("importDutyName"));
        QVERIFY(mappingOpen && mappingDialog && mapDuty && mappingApply && recordList && duty);
        QTRY_VERIFY(mappingOpen->isVisible() && mappingOpen->isEnabled());
        QVERIFY(!mappingDialog->property("visible").toBool());
        const auto center = [](QQuickItem *item) {
            return item->mapToScene(QPointF(item->width() / 2, item->height() / 2)).toPoint();
        };
        QVERIFY(QRect(0, 0, window->width(), window->height()).contains(center(mappingOpen)));
        QTest::mouseClick(window, Qt::LeftButton, Qt::NoModifier, center(mappingOpen));
        QTRY_VERIFY(mappingDialog->property("visible").toBool());
        QTRY_VERIFY(mapDuty->isVisible() && mapDuty->isEnabled());
        QTest::mouseClick(window, Qt::LeftButton, Qt::NoModifier, center(mapDuty));
        QTRY_VERIFY(mapDuty->hasActiveFocus());
        QGuiApplication::clipboard()->setText(QStringLiteral("活动"));
        QTest::keyClick(window, Qt::Key_A, Qt::ControlModifier);
        QTest::keyClick(window, Qt::Key_V, Qt::ControlModifier);
        QTRY_COMPARE(mapDuty->property("text").toString(), QStringLiteral("活动"));
        QTest::mouseClick(window, Qt::LeftButton, Qt::NoModifier, center(mappingApply));
        QTRY_COMPARE(backend.calls.size(), 2);
        QCOMPARE(backend.calls.last().type, QStringLiteral("PreviewRunImport"));
        const QJsonObject request = backend.calls.last().payload;
        const QJsonObject expectedMapping{{QStringLiteral("活动"), "duty_name"}};
        QCOMPARE(request.value("column_mapping").toObject(), expectedMapping);
        QCOMPARE(request.value("source_kind").toString(), csvSource ? QStringLiteral("CSV") : QStringLiteral("PASTE"));
        if (csvSource)
            QCOMPARE(request.value("file_path").toString(), fileSource);
        else
            QCOMPARE(request.value("text").toString(), pastedSource);
        QVERIFY(!request.contains("rows"));
        backend.calls.last().reply->succeed(previewCandidates(QJsonArray{
            QJsonObject{{"duty_name", QStringLiteral("第一副本")}},
            QJsonObject{{"duty_name", QStringLiteral("第二副本")}}}, "mapping-recovered"));
        QTRY_VERIFY(!mappingDialog->property("visible").toBool());
        QTRY_VERIFY(recordList->isVisible());
        QCOMPARE(controller.rows().size(), 2);
        QCOMPARE(controller.rows()[0].toMap().value("row_number").toInt(), 1);
        QCOMPARE(controller.rows()[1].toMap().value("row_number").toInt(), 2);
        QQuickItem *secondRow = nullptr;
        const auto findVisualRow = [&](auto &&self, QQuickItem *item) -> QQuickItem * {
            if (item->objectName() == QLatin1String("importRecordRow1"))
                return item;
            for (QQuickItem *child : item->childItems())
                if (QQuickItem *found = self(self, child))
                    return found;
            return nullptr;
        };
        QTRY_VERIFY((secondRow = findVisualRow(findVisualRow, recordList)) != nullptr);
        QTest::mouseClick(window, Qt::LeftButton, Qt::NoModifier, center(secondRow));
        QTRY_COMPARE(controller.currentRow(), 1);
        QCOMPARE(controller.currentCandidate().value("duty_name").toString(), QStringLiteral("第二副本"));
        QCOMPARE(duty->property("text").toString(), QStringLiteral("第二副本"));
        QCOMPARE(controller.rows()[0].toMap().value("candidate").toMap().value("duty_name").toString(), QStringLiteral("第一副本"));
        QVERIFY2(warnings.isEmpty(), qPrintable(warnings.join(QLatin1Char('\n'))));
    }

    void ownedProcessTimeoutAndCancellation()
    {
        QTemporaryDir directory;
        QVERIFY(directory.isValid());
        const QString app = installTestWorker(directory);
        QVERIFY(!app.isEmpty());
        const QString imagePath = directory.filePath("sample.png");
        QImage image(100, 100, QImage::Format_RGB32);
        image.fill(Qt::white);
        QVERIFY(image.save(imagePath));
        qputenv("MR_TEST_OCR_SLEEP", "1");
        mr::OfflineOcrEngine engine;
        engine.setApplicationDirectoryForTesting(app);
        engine.setTimeoutForTesting(250);
        QSignalSpy failures(&engine, &mr::OfflineOcrEngine::failed);
        QSignalSpy finished(&engine, &mr::OfflineOcrEngine::finished);
        QVERIFY(engine.start({imagePath}));
        QTRY_COMPARE_WITH_TIMEOUT(finished.size(), 1, 5000);
        QVERIFY(!engine.busy());
        QCOMPARE(finished[0][0].toBool(), false);
        QVERIFY(failures[0][0].toString().contains(QStringLiteral("超时")));
        failures.clear();
        finished.clear();
        engine.setTimeoutForTesting(5000);
        QVERIFY(engine.start({imagePath}));
        QTest::qWait(100);
        engine.cancel();
        QTRY_COMPARE_WITH_TIMEOUT(finished.size(), 1, 5000);
        QCOMPARE(finished[0][0].toBool(), false);
        QVERIFY(failures.isEmpty());
        qunsetenv("MR_TEST_OCR_SLEEP");
    }

    void rapidCancelRestartOwnsOnlyOneProcess()
    {
        QTemporaryDir directory;
        QVERIFY(directory.isValid());
        const QString app = installTestWorker(directory);
        QVERIFY(!app.isEmpty());
        const QString imagePath = directory.filePath("sample.png");
        QImage image(100, 100, QImage::Format_RGB32);
        image.fill(Qt::white);
        QVERIFY(image.save(imagePath));
        qputenv("MR_TEST_OCR_SLEEP", "1");
        mr::OfflineOcrEngine engine;
        engine.setApplicationDirectoryForTesting(app);
        engine.setTimeoutForTesting(5000);
        QSignalSpy finished(&engine, &mr::OfflineOcrEngine::finished);
        QVERIFY(engine.start({imagePath}));
        engine.cancel();
        QVERIFY(!engine.busy());
        QVERIFY(engine.start({imagePath})); // Before either queued start ran.
        QTest::qWait(100);
        auto activeProcesses = [&engine] {
            int count = 0;
            for (auto *process : engine.findChildren<QProcess *>())
                if (process->state() != QProcess::NotRunning)
                    ++count;
            return count;
        };
        QCOMPARE(activeProcesses(), 1);
        engine.cancel();
        QTRY_VERIFY_WITH_TIMEOUT(!engine.busy(), 5000);
        QTRY_COMPARE_WITH_TIMEOUT(activeProcesses(), 0, 5000);
        QCOMPARE(finished.size(), 2);
        qunsetenv("MR_TEST_OCR_SLEEP");
    }
};

int main(int argc, char **argv)
{
    if (argc >= 3 && QFileInfo(QString::fromLocal8Bit(argv[0])).baseName() == QLatin1String("tesseract")) {
        QCoreApplication application(argc, argv);
        if (qEnvironmentVariableIsSet("MR_TEST_OCR_SLEEP"))
            QThread::msleep(10000);
        QByteArray tsv = kTsvHeader;
        if (qEnvironmentVariableIsSet("MR_TEST_OCR_TSV_FILE")) {
            QFile input(qEnvironmentVariable("MR_TEST_OCR_TSV_FILE"));
            if (!input.open(QIODevice::ReadOnly))
                return 2;
            tsv = input.read(8 * 1024 * 1024 + 1);
            if (tsv.size() > 8 * 1024 * 1024)
                return 2;
        }
        QFile output(application.arguments()[2] + QStringLiteral(".tsv"));
        return output.open(QIODevice::WriteOnly) && output.write(tsv) == tsv.size() ? 0 : 1;
    }
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication application(argc, argv);
    ImportRecordsControllerTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "ImportRecordsControllerTests.moc"
