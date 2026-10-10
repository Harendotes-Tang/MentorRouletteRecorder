#include "OfflineOcrEngine.h"

#include <QCoreApplication>
#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QGuiApplication>
#include <QImage>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QProcess>
#include <QSignalSpy>
#include <QTemporaryDir>
#include <QTest>
#include <QThread>

#include <memory>

namespace {

constexpr auto kHeader = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n";
const QStringList kModels{QStringLiteral("PP-OCRv6_det_small.onnx"),
                          QStringLiteral("PP-OCRv6_rec_small.onnx"),
                          QStringLiteral("ch_ppocr_mobile_v2.0_cls_mobile.onnx")};

bool writeFile(const QString &path, const QByteArray &bytes)
{
    QFile file(path);
    return file.open(QIODevice::WriteOnly) && file.write(bytes) == bytes.size();
}

QString installWorker(QTemporaryDir &directory)
{
    const QString app = directory.filePath(QStringLiteral("app with spaces"));
    const QString ocr = QDir(app).filePath(QStringLiteral("ocr"));
    const QString models = QDir(ocr).filePath(QStringLiteral("models"));
    if (!QDir().mkpath(models)
        || !QFile::copy(QCoreApplication::applicationFilePath(), QDir(ocr).filePath(QStringLiteral("local-ai-ocr.exe"))))
        return {};
    for (const auto &name : kModels)
        if (!writeFile(QDir(models).filePath(name), "synthetic-model"))
            return {};
    return app;
}

QString createScreenshot(QTemporaryDir &directory, const QString &name = QStringLiteral("截图 with spaces.png"))
{
    QImage image(1851, 1082, QImage::Format_RGB32);
    image.fill(QColor(45, 69, 38));
    const QString path = directory.filePath(name);
    return image.save(path) ? path : QString();
}

QByteArray wholeImageTsv()
{
    QByteArray tsv(kHeader);
    // These are whole detected text rectangles, including a combined timestamp
    // and mixed Han/Latin/punctuation note. No per-character boxes are guessed.
    for (int row = 0; row < 10; ++row) {
        const int y = row * 108 + 28;
        tsv += QStringLiteral("5\t1\t%1\t1\t1\t1\t142\t%2\t50\t27\t98.5\tLv.90\n").arg(row * 4 + 1).arg(y).toUtf8();
        tsv += QStringLiteral("5\t1\t%1\t1\t1\t1\t284\t%2\t294\t27\t99.1\t近东秘宝阿尔扎达尔海底遗迹群\n").arg(row * 4 + 2).arg(y).toUtf8();
        tsv += QStringLiteral("5\t1\t%1\t1\t1\t1\t610\t%2\t190\t27\t99.7\t2024-09-22 23:04:51\n").arg(row * 4 + 3).arg(y).toUtf8();
        tsv += QStringLiteral("5\t1\t%1\t1\t1\t1\t826\t%2\t510\t27\t99.8\t无事发生.jpg（第 %3 条；啵啵030!）\n").arg(row * 4 + 4).arg(y).arg(row + 1).toUtf8();
    }
    return tsv;
}

int worker(int argc, char **argv)
{
    QCoreApplication application(argc, argv);
    const QStringList args = application.arguments();
    if (args.size() != 7 || args[1] != QLatin1String("--input") || args[3] != QLatin1String("--output")
        || args[5] != QLatin1String("--models") || !QFileInfo(args[2]).isAbsolute()
        || !QFileInfo(args[4]).isAbsolute() || !QFileInfo(args[6]).isAbsolute())
        return 12;
    const QImage image(args[2]);
    if (image.isNull() || image.size() != QSize(1851, 1082))
        return 13;
    const QString log = qEnvironmentVariable("MR_TEST_AI_OCR_LOG");
    if (!log.isEmpty()) {
        QJsonObject record{{QStringLiteral("args"), QJsonArray::fromStringList(args)},
                           {QStringLiteral("cwd"), QDir::currentPath()},
                           {QStringLiteral("pythonpath_present"), qEnvironmentVariableIsSet("PYTHONPATH")},
                           {QStringLiteral("omp_threads"), qEnvironmentVariable("OMP_NUM_THREADS")}};
        QFile file(log);
        if (!file.open(QIODevice::Append))
            return 14;
        file.write(QJsonDocument(record).toJson(QJsonDocument::Compact) + '\n');
    }
    const QString ready = qEnvironmentVariable("MR_TEST_AI_OCR_READY");
    if (!ready.isEmpty())
        writeFile(ready, QDir::currentPath().toUtf8());
    const QString mode = qEnvironmentVariable("MR_TEST_AI_OCR_MODE");
    if (mode == QLatin1String("sleep"))
        QThread::msleep(15000);
    if (mode == QLatin1String("fail"))
        return 7;
    if (mode == QLatin1String("no-output"))
        return 0;
    if (mode == QLatin1String("oversize") || mode == QLatin1String("oversize-partial")) {
        QFile output(args[4] + (mode.endsWith(QLatin1String("partial")) ? QStringLiteral(".partial") : QString()));
        if (!output.open(QIODevice::WriteOnly) || !output.resize(mr::OfflineOcrEngine::MaxOutputBytes + 1))
            return 15;
        output.close();
        QThread::msleep(5000);
        return 0;
    }
    if (mode == QLatin1String("noisy")) {
        QFile out, err;
        if (!out.open(stdout, QIODevice::WriteOnly) || !err.open(stderr, QIODevice::WriteOnly))
            return 17;
        out.write(QByteArray(64 * 1024, 'o'));
        err.write(QByteArray(64 * 1024, 'e'));
        out.flush();
        err.flush();
    }
    QByteArray tsv = wholeImageTsv();
    if (mode == QLatin1String("malformed"))
        tsv = "unexpected output";
    if (mode == QLatin1String("bad-coordinate"))
        tsv = QByteArray(kHeader) + "5\t1\t1\t1\t1\t1\t1850\t20\t100\t27\t99\toutside\n";
    if (mode == QLatin1String("empty"))
        tsv = kHeader;
    return writeFile(args[4], tsv) ? 0 : 16;
}

} // namespace

class OfflineOcrEngineTests final : public QObject
{
    Q_OBJECT
private Q_SLOTS:
    void cleanup()
    {
        for (const auto &name : {"MR_TEST_AI_OCR_LOG", "MR_TEST_AI_OCR_READY", "MR_TEST_AI_OCR_MODE"})
            qunsetenv(name);
    }

    void fixedPackagedPathsNeverFindHostOcr()
    {
        QTemporaryDir directory;
        const QString app = directory.filePath(QStringLiteral("app"));
        QDir().mkpath(app);
        writeFile(QDir(app).filePath(QStringLiteral("tesseract.exe")), "host-ocr");
        QCOMPARE(mr::OfflineOcrEngine::enginePath(app), QDir(app).filePath(QStringLiteral("ocr/local-ai-ocr.exe")));
        QCOMPARE(mr::OfflineOcrEngine::modelsPath(app), QDir(app).filePath(QStringLiteral("ocr/models")));
        mr::OfflineOcrEngine engine;
        engine.setApplicationDirectoryForTesting(app);
        QSignalSpy failures(&engine, &mr::OfflineOcrEngine::failed);
        QSignalSpy finished(&engine, &mr::OfflineOcrEngine::finished);
        QVERIFY(!engine.start({createScreenshot(directory)}));
        QCOMPARE(failures.size(), 1);
        QVERIFY(failures[0][0].toString().contains(QStringLiteral("本地 AI")));
        QVERIFY(finished.isEmpty() && !engine.busy());
        QVERIFY(engine.findChildren<QProcess *>().isEmpty());
    }

    void missingOrEmptyModelIsRejectedBeforeLaunch_data()
    {
        QTest::addColumn<QString>("name");
        QTest::addColumn<bool>("empty");
        for (const auto &name : kModels) {
            QTest::newRow(qPrintable(name + QStringLiteral("-missing"))) << name << false;
            QTest::newRow(qPrintable(name + QStringLiteral("-empty"))) << name << true;
        }
    }

    void missingOrEmptyModelIsRejectedBeforeLaunch()
    {
        QFETCH(QString, name);
        QFETCH(bool, empty);
        QTemporaryDir directory;
        const QString app = installWorker(directory);
        const QString model = QDir(mr::OfflineOcrEngine::modelsPath(app)).filePath(name);
        QVERIFY(empty ? writeFile(model, {}) : QFile::remove(model));
        mr::OfflineOcrEngine engine;
        engine.setApplicationDirectoryForTesting(app);
        QSignalSpy failures(&engine, &mr::OfflineOcrEngine::failed);
        QVERIFY(!engine.start({createScreenshot(directory)}));
        QCOMPARE(failures.size(), 1);
        QVERIFY(failures[0][0].toString().contains(QStringLiteral("模型不完整")));
        QVERIFY(engine.findChildren<QProcess *>().isEmpty());
    }

    void validatesEntireBatchBeforeLaunching()
    {
        QTemporaryDir directory;
        const QString app = installWorker(directory);
        mr::OfflineOcrEngine engine;
        engine.setApplicationDirectoryForTesting(app);
        const QString valid = createScreenshot(directory);
        QSignalSpy failures(&engine, &mr::OfflineOcrEngine::failed);
        QVERIFY(!engine.start({valid, directory.filePath(QStringLiteral("missing.png"))}));
        QVERIFY(!engine.start({QFileInfo(valid).fileName()}));
        QFile oversized(valid);
        QVERIFY(oversized.open(QIODevice::ReadWrite) && oversized.resize(mr::OfflineOcrEngine::MaxInputBytes + 1));
        oversized.close();
        QVERIFY(!engine.start({valid}));
        QVERIFY(!engine.start({}));
        QVERIFY(!engine.start(QStringList(mr::OfflineOcrEngine::MaxImages + 1, valid)));
        QCOMPARE(failures.size(), 5);
        QVERIFY(!engine.busy() && engine.findChildren<QProcess *>().isEmpty());
    }

    void oversizedPixelHeaderIsRejectedWithoutDecoding()
    {
        QTemporaryDir directory;
        mr::OfflineOcrEngine engine;
        engine.setApplicationDirectoryForTesting(installWorker(directory));
        QByteArray bmp(54, '\0');
        bmp[0] = 'B';
        bmp[1] = 'M';
        const auto put = [&bmp](int offset, quint32 value) {
            for (int i = 0; i < 4; ++i)
                bmp[offset + i] = char((value >> (i * 8)) & 255);
        };
        put(2, 300000054);
        put(10, 54);
        put(14, 40);
        put(18, 10000);
        put(22, 10000);
        bmp[26] = 1;
        bmp[28] = 24;
        const QString path = directory.filePath(QStringLiteral("oversize.bmp"));
        QVERIFY(writeFile(path, bmp));
        QSignalSpy failures(&engine, &mr::OfflineOcrEngine::failed);
        QVERIFY(!engine.start({path}));
        QCOMPARE(failures.size(), 1);
        QVERIFY(failures[0][0].toString().contains(QStringLiteral("5000 万像素")));
        QVERIFY(engine.findChildren<QProcess *>().isEmpty());
    }

    void damagedPackagedExecutableSettlesWithChineseFailure()
    {
        QTemporaryDir directory;
        const QString app = installWorker(directory);
        QVERIFY(writeFile(mr::OfflineOcrEngine::enginePath(app), "damaged executable"));
        mr::OfflineOcrEngine engine;
        engine.setApplicationDirectoryForTesting(app);
        QSignalSpy recognized(&engine, &mr::OfflineOcrEngine::imageRecognized);
        QSignalSpy finished(&engine, &mr::OfflineOcrEngine::finished);
        QSignalSpy failures(&engine, &mr::OfflineOcrEngine::failed);
        QVERIFY(engine.start({createScreenshot(directory)}));
        QTRY_COMPARE_WITH_TIMEOUT(finished.size(), 1, 5000);
        QCOMPARE(failures.size(), 1);
        QVERIFY(failures[0][0].toString().contains(QStringLiteral("无法启动")));
        QVERIFY(!finished[0][0].toBool() && recognized.isEmpty() && !engine.busy());
    }

    void oneWholeImagePassPreservesAllTenNotesAndCoordinates()
    {
        QTemporaryDir directory;
        const QString app = installWorker(directory);
        const QString path = createScreenshot(directory);
        const QString log = directory.filePath(QStringLiteral("worker-log.jsonl"));
        qputenv("MR_TEST_AI_OCR_LOG", log.toUtf8());
        mr::OfflineOcrEngine engine;
        engine.setApplicationDirectoryForTesting(app);
        QSignalSpy recognized(&engine, &mr::OfflineOcrEngine::imageRecognized);
        QSignalSpy finished(&engine, &mr::OfflineOcrEngine::finished);
        QSignalSpy failures(&engine, &mr::OfflineOcrEngine::failed);
        QVERIFY(engine.start({path}));
        QVERIFY(!engine.start({path}));
        QTRY_COMPARE_WITH_TIMEOUT(finished.size(), 1, 10000);
        QVERIFY2(failures.isEmpty(), failures.isEmpty() ? "" : qPrintable(failures[0][0].toString()));
        QVERIFY(finished[0][0].toBool());
        QCOMPARE(recognized.size(), 1);
        QCOMPARE(recognized[0][0].toString(), path);
        QCOMPARE(recognized[0][1].toByteArray(), wholeImageTsv());
        QCOMPARE(recognized[0][1].toByteArray().count(QStringLiteral("无事发生.jpg").toUtf8()), 10);
        QFile file(log);
        QVERIFY(file.open(QIODevice::ReadOnly));
        const auto records = file.readAll().trimmed().split('\n');
        QCOMPARE(records.size(), 1);
        const auto record = QJsonDocument::fromJson(records[0]).object();
        const auto args = record.value(QStringLiteral("args")).toArray();
        QCOMPARE(QDir::cleanPath(args[0].toString()), QDir::cleanPath(mr::OfflineOcrEngine::enginePath(app)));
        QCOMPARE(args[1].toString(), QStringLiteral("--input"));
        QVERIFY(args[2].toString() != path && args[2].toString().endsWith(QStringLiteral("/input.png")));
        QCOMPARE(args[3].toString(), QStringLiteral("--output"));
        QCOMPARE(args[5].toString(), QStringLiteral("--models"));
        QCOMPARE(QDir::cleanPath(args[6].toString()), QDir::cleanPath(mr::OfflineOcrEngine::modelsPath(app)));
        QCOMPARE(record.value(QStringLiteral("omp_threads")).toString(), QStringLiteral("2"));
        QVERIFY(!record.value(QStringLiteral("pythonpath_present")).toBool());
        QVERIFY(!QFileInfo::exists(record.value(QStringLiteral("cwd")).toString()));
        QCOMPARE(engine.completedImages(), 1);
        QVERIFY(!engine.busy());
    }

    void emptyWholeImageResultRemainsEmpty()
    {
        QTemporaryDir directory;
        mr::OfflineOcrEngine engine;
        engine.setApplicationDirectoryForTesting(installWorker(directory));
        qputenv("MR_TEST_AI_OCR_MODE", "empty");
        QSignalSpy recognized(&engine, &mr::OfflineOcrEngine::imageRecognized);
        QSignalSpy finished(&engine, &mr::OfflineOcrEngine::finished);
        QVERIFY(engine.start({createScreenshot(directory)}));
        QTRY_COMPARE_WITH_TIMEOUT(finished.size(), 1, 5000);
        QVERIFY(finished[0][0].toBool());
        QCOMPARE(recognized[0][1].toByteArray(), QByteArray(kHeader));
    }

    void failuresNeverEmitPartialImage_data()
    {
        QTest::addColumn<QByteArray>("mode");
        for (const auto &mode : {"fail", "no-output", "malformed", "bad-coordinate", "oversize", "oversize-partial"})
            QTest::newRow(mode) << QByteArray(mode);
    }

    void failuresNeverEmitPartialImage()
    {
        QFETCH(QByteArray, mode);
        QTemporaryDir directory;
        const QString ready = directory.filePath(QStringLiteral("ready"));
        qputenv("MR_TEST_AI_OCR_READY", ready.toUtf8());
        qputenv("MR_TEST_AI_OCR_MODE", mode);
        mr::OfflineOcrEngine engine;
        engine.setApplicationDirectoryForTesting(installWorker(directory));
        QSignalSpy recognized(&engine, &mr::OfflineOcrEngine::imageRecognized);
        QSignalSpy finished(&engine, &mr::OfflineOcrEngine::finished);
        QSignalSpy failures(&engine, &mr::OfflineOcrEngine::failed);
        QVERIFY(engine.start({createScreenshot(directory)}));
        QTRY_COMPARE_WITH_TIMEOUT(finished.size(), 1, 5000);
        QVERIFY(!finished[0][0].toBool());
        QCOMPARE(failures.size(), 1);
        QVERIFY(recognized.isEmpty() && !engine.busy());
        QFile marker(ready);
        QVERIFY(marker.open(QIODevice::ReadOnly));
        QVERIFY(!QFileInfo::exists(QString::fromUtf8(marker.readAll())));
    }

    void cancelOrTimeoutKillsOwnedChild_data()
    {
        QTest::addColumn<bool>("timeout");
        QTest::newRow("cancel") << false;
        QTest::newRow("timeout") << true;
    }

    void cancelOrTimeoutKillsOwnedChild()
    {
        QFETCH(bool, timeout);
        QTemporaryDir directory;
        const QString app = installWorker(directory);
        const QString path = createScreenshot(directory);
        const QString ready = directory.filePath(QStringLiteral("ready"));
        qputenv("MR_TEST_AI_OCR_READY", ready.toUtf8());
        qputenv("MR_TEST_AI_OCR_MODE", "sleep");
        mr::OfflineOcrEngine engine;
        engine.setApplicationDirectoryForTesting(app);
        // Leave cold process startup time before testing expiry. The worker must
        // outlive this deadline so a normal exit cannot masquerade as a timeout.
        engine.setTimeoutForTesting(5000);
        QSignalSpy recognized(&engine, &mr::OfflineOcrEngine::imageRecognized);
        QSignalSpy finished(&engine, &mr::OfflineOcrEngine::finished);
        QSignalSpy failures(&engine, &mr::OfflineOcrEngine::failed);
        QVERIFY(engine.start({path}));
        QTRY_VERIFY_WITH_TIMEOUT(QFileInfo::exists(ready), 3000);
        QFile marker(ready);
        QVERIFY(marker.open(QIODevice::ReadOnly));
        const QString work = QString::fromUtf8(marker.readAll());
        marker.close();
        if (!timeout)
            engine.cancel();
        QTRY_COMPARE_WITH_TIMEOUT(finished.size(), 1, 5000);
        QVERIFY(!finished[0][0].toBool());
        QCOMPARE(failures.size(), timeout ? 1 : 0);
        if (timeout)
            QVERIFY(failures[0][0].toString().contains(QStringLiteral("超时")));
        QVERIFY(recognized.isEmpty() && !QFileInfo::exists(work) && !engine.busy());
        qunsetenv("MR_TEST_AI_OCR_MODE");
        engine.setTimeoutForTesting(5000);
        QVERIFY(engine.start({path}));
        QTRY_COMPARE_WITH_TIMEOUT(finished.size(), 2, 5000);
        QVERIFY(finished[1][0].toBool());
        QCOMPARE(recognized.size(), 1);
    }

    void destructionKillsChildAndRemovesOwnedImage()
    {
        QTemporaryDir directory;
        const QString ready = directory.filePath(QStringLiteral("ready"));
        qputenv("MR_TEST_AI_OCR_READY", ready.toUtf8());
        qputenv("MR_TEST_AI_OCR_MODE", "sleep");
        auto engine = std::make_unique<mr::OfflineOcrEngine>();
        engine->setApplicationDirectoryForTesting(installWorker(directory));
        QVERIFY(engine->start({createScreenshot(directory)}));
        QTRY_VERIFY_WITH_TIMEOUT(QFileInfo::exists(ready), 3000);
        QFile marker(ready);
        QVERIFY(marker.open(QIODevice::ReadOnly));
        const QString work = QString::fromUtf8(marker.readAll());
        marker.close();
        QVERIFY(QFileInfo::exists(work));
        engine.reset();
        QVERIFY(!QFileInfo::exists(work));
    }

    void drainsNoisyPipesWithoutChangingRecognition()
    {
        QTemporaryDir directory;
        mr::OfflineOcrEngine engine;
        engine.setApplicationDirectoryForTesting(installWorker(directory));
        qputenv("MR_TEST_AI_OCR_MODE", "noisy");
        QSignalSpy recognized(&engine, &mr::OfflineOcrEngine::imageRecognized);
        QSignalSpy finished(&engine, &mr::OfflineOcrEngine::finished);
        QVERIFY(engine.start({createScreenshot(directory)}));
        QTRY_COMPARE_WITH_TIMEOUT(finished.size(), 1, 5000);
        QVERIFY(finished[0][0].toBool());
        QCOMPARE(recognized[0][1].toByteArray(), wholeImageTsv());
    }

    void failedCallbackMayStartNewBatchWithoutOldTerminalSignal()
    {
        QTemporaryDir directory;
        mr::OfflineOcrEngine engine;
        engine.setApplicationDirectoryForTesting(installWorker(directory));
        const QString path = createScreenshot(directory);
        qputenv("MR_TEST_AI_OCR_MODE", "fail");
        QSignalSpy recognized(&engine, &mr::OfflineOcrEngine::imageRecognized);
        QSignalSpy finished(&engine, &mr::OfflineOcrEngine::finished);
        QSignalSpy failures(&engine, &mr::OfflineOcrEngine::failed);
        bool restarted = false;
        connect(&engine, &mr::OfflineOcrEngine::failed, &engine, [&](const QString &) {
            qunsetenv("MR_TEST_AI_OCR_MODE");
            restarted = engine.start({path});
        });
        QVERIFY(engine.start({path}));
        QTRY_VERIFY_WITH_TIMEOUT(restarted, 5000);
        QTRY_COMPARE_WITH_TIMEOUT(finished.size(), 1, 5000);
        QCOMPARE(failures.size(), 1);
        QCOMPARE(recognized.size(), 1);
        QVERIFY(finished[0][0].toBool() && !engine.busy());
    }

    void cancellationBeforeQueuedLaunchStartsNoWorker()
    {
        QTemporaryDir directory;
        mr::OfflineOcrEngine engine;
        engine.setApplicationDirectoryForTesting(installWorker(directory));
        QSignalSpy recognized(&engine, &mr::OfflineOcrEngine::imageRecognized);
        QSignalSpy finished(&engine, &mr::OfflineOcrEngine::finished);
        QVERIFY(engine.start({createScreenshot(directory)}));
        engine.cancel();
        QTRY_COMPARE_WITH_TIMEOUT(finished.size(), 1, 1000);
        QVERIFY(!finished[0][0].toBool());
        QVERIFY(recognized.isEmpty() && engine.findChildren<QProcess *>().isEmpty());
    }

    void imagesRunSeriallyAndSettleOnce()
    {
        QTemporaryDir directory;
        mr::OfflineOcrEngine engine;
        engine.setApplicationDirectoryForTesting(installWorker(directory));
        const QString first = createScreenshot(directory);
        const QString second = createScreenshot(directory, QStringLiteral("第二张.png"));
        QSignalSpy recognized(&engine, &mr::OfflineOcrEngine::imageRecognized);
        QSignalSpy finished(&engine, &mr::OfflineOcrEngine::finished);
        QVERIFY(engine.start({first, second}));
        QTRY_COMPARE_WITH_TIMEOUT(finished.size(), 1, 5000);
        QCOMPARE(recognized.size(), 2);
        QCOMPARE(recognized[0][0].toString(), first);
        QCOMPARE(recognized[1][0].toString(), second);
        QCOMPARE(engine.completedImages(), 2);
        QVERIFY(finished[0][0].toBool());
    }
};

int main(int argc, char **argv)
{
    if (argc >= 3 && QFileInfo(QString::fromLocal8Bit(argv[0])).baseName() == QLatin1String("local-ai-ocr"))
        return worker(argc, argv);
    QGuiApplication application(argc, argv);
    OfflineOcrEngineTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "OfflineOcrEngineTests.moc"
