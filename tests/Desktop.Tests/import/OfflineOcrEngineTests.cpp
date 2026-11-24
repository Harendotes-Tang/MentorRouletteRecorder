#include "DesktopTestRunner.h"
#include "OcrStubSupport.h"
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

#include <iterator>
#include <memory>

#ifdef Q_OS_WIN
#include <qt_windows.h>
#endif

namespace {

using mrtest::installOcrStub;
using mrtest::ocrModelFiles;
using mrtest::ocrTsvHeader;

bool writeFile(const QString &path, const QByteArray &bytes)
{
    QFile file(path);
    return file.open(QIODevice::WriteOnly) && file.write(bytes) == bytes.size();
}

QString createScreenshot(QTemporaryDir &directory, const QString &name = QStringLiteral("截图 with spaces.png"))
{
    QImage image(1851, 1082, QImage::Format_RGB32);
    image.fill(QColor(45, 69, 38));
    const QString path = directory.filePath(name);
    return image.save(path) ? path : QString();
}

/// The engine over the stub worker installed in a folder of its own, with spies on its signals.
struct EngineBench
{
    QTemporaryDir directory;
    QString app = installOcrStub(directory);
    mr::OfflineOcrEngine engine;
    QSignalSpy recognized{&engine, &mr::OfflineOcrEngine::imageRecognized};
    QSignalSpy finished{&engine, &mr::OfflineOcrEngine::finished};
    QSignalSpy failures{&engine, &mr::OfflineOcrEngine::failed};

    EngineBench() { engine.setApplicationDirectoryForTesting(app); }
    /// A screenshot of the size the stub worker accepts, in the bench's folder.
    QString screenshot(const QString &name = QStringLiteral("截图 with spaces.png")) { return createScreenshot(directory, name); }
};

/// The work folder the stub worker wrote into its ready marker; empty when the marker cannot be read.
QString workDirectoryOf(const QString &readyMarker)
{
    QFile marker(readyMarker);
    return marker.open(QIODevice::ReadOnly) ? QString::fromUtf8(marker.readAll()) : QString();
}

QByteArray wholeImageTsv()
{
    QByteArray tsv(ocrTsvHeader());
    // These are whole detected text rectangles, including a combined timestamp
    // and mixed Han/Latin/punctuation note. No per-character boxes are guessed.
    for (int row = 0; row < 10; ++row) {
        const int y = (row * 108) + 28;
        tsv += QStringLiteral("5\t1\t%1\t1\t1\t1\t142\t%2\t50\t27\t98.5\tLv.90\n").arg((row * 4) + 1).arg(y).toUtf8();
        tsv += QStringLiteral("5\t1\t%1\t1\t1\t1\t284\t%2\t294\t27\t99.1\t近东秘宝阿尔扎达尔海底遗迹群\n").arg((row * 4) + 2).arg(y).toUtf8();
        tsv += QStringLiteral("5\t1\t%1\t1\t1\t1\t610\t%2\t190\t27\t99.7\t2024-09-22 23:04:51\n").arg((row * 4) + 3).arg(y).toUtf8();
        tsv += QStringLiteral("5\t1\t%1\t1\t1\t1\t826\t%2\t510\t27\t99.8\t无事发生.jpg（第 %3 条；啵啵030!）\n").arg((row * 4) + 4).arg(y).arg(row + 1).toUtf8();
    }
    return tsv;
}

int worker(int argc, char **argv)
{
    QCoreApplication application(argc, argv);
    const QStringList args = QCoreApplication::arguments();
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
#ifdef Q_OS_WIN
        wchar_t modulePath[32768];
        const DWORD modulePathLength = GetModuleFileNameW(nullptr, modulePath, static_cast<DWORD>(std::size(modulePath)));
        if (modulePathLength == 0 || modulePathLength >= std::size(modulePath))
            return 18;
        record.insert(QStringLiteral("module_path"), QString::fromWCharArray(modulePath, static_cast<int>(modulePathLength)));
#endif
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
        QFile out;
        QFile err;
        if (!out.open(stdout, QIODevice::WriteOnly) || !err.open(stderr, QIODevice::WriteOnly))
            return 17;
        out.write(QByteArray(qsizetype{64} * 1024, 'o'));
        err.write(QByteArray(qsizetype{64} * 1024, 'e'));
        out.flush();
        err.flush();
    }
    QByteArray tsv = wholeImageTsv();
    if (mode == QLatin1String("malformed"))
        tsv = "unexpected output";
    if (mode == QLatin1String("bad-coordinate"))
        tsv = ocrTsvHeader() + "5\t1\t1\t1\t1\t1\t1850\t20\t100\t27\t99\toutside\n";
    if (mode == QLatin1String("empty"))
        tsv = ocrTsvHeader();
    return writeFile(args[4], tsv) ? 0 : 16;
}

} // namespace

namespace {
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
        QVERIFY(failures[0][0].toString().contains(QStringLiteral("缺少本机图片识别组件")));
        QVERIFY(finished.isEmpty() && !engine.busy());
        QVERIFY(engine.findChildren<QProcess *>().isEmpty());
    }

    void missingOrEmptyModelIsRejectedBeforeLaunch_data()
    {
        QTest::addColumn<QString>("name");
        QTest::addColumn<bool>("empty");
        for (const auto &name : ocrModelFiles()) {
            QTest::newRow(qPrintable(name + QStringLiteral("-missing"))) << name << false;
            QTest::newRow(qPrintable(name + QStringLiteral("-empty"))) << name << true;
        }
    }

    void missingOrEmptyModelIsRejectedBeforeLaunch()
    {
        QFETCH(QString, name);
        QFETCH(bool, empty);
        EngineBench bench;
        const QString model = QDir(mr::OfflineOcrEngine::modelsPath(bench.app)).filePath(name);
        QVERIFY(empty ? writeFile(model, {}) : QFile::remove(model));
        QVERIFY(!bench.engine.start({bench.screenshot()}));
        QCOMPARE(bench.failures.size(), 1);
        QVERIFY(bench.failures[0][0].toString().contains(QStringLiteral("本机图片识别文件不完整")));
        QVERIFY(bench.engine.findChildren<QProcess *>().isEmpty());
    }

    void validatesEntireBatchBeforeLaunching()
    {
        EngineBench bench;
        const QString valid = bench.screenshot();
        QVERIFY(!bench.engine.start({valid, bench.directory.filePath(QStringLiteral("missing.png"))}));
        QVERIFY(!bench.engine.start({QFileInfo(valid).fileName()}));
        QFile oversized(valid);
        QVERIFY(oversized.open(QIODevice::ReadWrite) && oversized.resize(mr::OfflineOcrEngine::MaxInputBytes + 1));
        oversized.close();
        QVERIFY(!bench.engine.start({valid}));
        QVERIFY(!bench.engine.start({}));
        QVERIFY(!bench.engine.start(QStringList(mr::OfflineOcrEngine::MaxImages + 1, valid)));
        QCOMPARE(bench.failures.size(), 5);
        QVERIFY(!bench.engine.busy() && bench.engine.findChildren<QProcess *>().isEmpty());
    }

    void oversizedPixelHeaderIsRejectedWithoutDecoding()
    {
        EngineBench bench;
        QByteArray bmp(54, '\0');
        bmp[0] = 'B';
        bmp[1] = 'M';
        const auto put = [&bmp](int offset, quint32 value) {
            for (int i = 0; i < 4; ++i)
                bmp[offset + i] = static_cast<char>((value >> (i * 8)) & 255);
        };
        put(2, 300000054);
        put(10, 54);
        put(14, 40);
        put(18, 10000);
        put(22, 10000);
        bmp[26] = 1;
        bmp[28] = 24;
        const QString path = bench.directory.filePath(QStringLiteral("oversize.bmp"));
        QVERIFY(writeFile(path, bmp));
        QVERIFY(!bench.engine.start({path}));
        QCOMPARE(bench.failures.size(), 1);
        QVERIFY(bench.failures[0][0].toString().contains(QStringLiteral("5000 万像素")));
        QVERIFY(bench.engine.findChildren<QProcess *>().isEmpty());
    }

    void damagedPackagedExecutableSettlesWithChineseFailure()
    {
        EngineBench bench;
        QVERIFY(writeFile(mr::OfflineOcrEngine::enginePath(bench.app), "damaged executable"));
        QVERIFY(bench.engine.start({bench.screenshot()}));
        QTRY_COMPARE_WITH_TIMEOUT(bench.finished.size(), 1, 5000);
        QCOMPARE(bench.failures.size(), 1);
        QVERIFY(bench.failures[0][0].toString().contains(QStringLiteral("无法启动")));
        QVERIFY(!bench.finished[0][0].toBool() && bench.recognized.isEmpty() && !bench.engine.busy());
    }

    void oneWholeImagePassPreservesAllTenNotesAndCoordinates()
    {
        EngineBench bench;
        const QString path = bench.screenshot();
        const QString log = bench.directory.filePath(QStringLiteral("worker-log.jsonl"));
        qputenv("MR_TEST_AI_OCR_LOG", log.toUtf8());
        QVERIFY(bench.engine.start({path}));
        QVERIFY(!bench.engine.start({path}));
        QTRY_COMPARE_WITH_TIMEOUT(bench.finished.size(), 1, 10000);
        QVERIFY2(bench.failures.isEmpty(), bench.failures.isEmpty() ? "" : qPrintable(bench.failures[0][0].toString()));
        QVERIFY(bench.finished[0][0].toBool());
        QCOMPARE(bench.recognized.size(), 1);
        QCOMPARE(bench.recognized[0][0].toString(), path);
        QCOMPARE(bench.recognized[0][1].toByteArray(), wholeImageTsv());
        QCOMPARE(bench.recognized[0][1].toByteArray().count(QStringLiteral("无事发生.jpg").toUtf8()), 10);
        QFile file(log);
        QVERIFY(file.open(QIODevice::ReadOnly));
        const auto records = file.readAll().trimmed().split('\n');
        QCOMPARE(records.size(), 1);
        const auto record = QJsonDocument::fromJson(records[0]).object();
        const auto args = record.value(QStringLiteral("args")).toArray();
        QCOMPARE(QDir::cleanPath(args[0].toString()), QDir::cleanPath(mr::OfflineOcrEngine::enginePath(bench.app)));
        QCOMPARE(args[1].toString(), QStringLiteral("--input"));
        QVERIFY(args[2].toString() != path && args[2].toString().endsWith(QStringLiteral("/input.png")));
        QCOMPARE(args[3].toString(), QStringLiteral("--output"));
        QCOMPARE(args[5].toString(), QStringLiteral("--models"));
        QCOMPARE(QDir::cleanPath(args[6].toString()), QDir::cleanPath(mr::OfflineOcrEngine::modelsPath(bench.app)));
        QCOMPARE(record.value(QStringLiteral("omp_threads")).toString(), QStringLiteral("2"));
        QVERIFY(!record.value(QStringLiteral("pythonpath_present")).toBool());
        QVERIFY(!QFileInfo::exists(record.value(QStringLiteral("cwd")).toString()));
        QCOMPARE(bench.engine.completedImages(), 1);
        QVERIFY(!bench.engine.busy());
    }

#ifdef Q_OS_WIN
    void deepWindowsWorkerLoadsThroughExtendedPath_data()
    {
        QTest::addColumn<bool>("alreadyExtended");
        QTest::newRow("absolute-drive-path") << false;
        QTest::newRow("already-extended-drive-path") << true;
    }

    void deepWindowsWorkerLoadsThroughExtendedPath()
    {
        QFETCH(bool, alreadyExtended);
        QTemporaryDir directory;
        QVERIFY(directory.isValid());
        QString relativeApp = QStringLiteral("深目录 with spaces");
        while (mr::OfflineOcrEngine::enginePath(directory.filePath(relativeApp)).size() < 220)
            relativeApp += QStringLiteral("/nested app files");
        const QString app = installOcrStub(directory, relativeApp);
        QVERIFY(!app.isEmpty());
        const QString log = directory.filePath(QStringLiteral("deep-worker.jsonl"));
        qputenv("MR_TEST_AI_OCR_LOG", log.toUtf8());
        const QString nativeApp = QDir::toNativeSeparators(app);
        mr::OfflineOcrEngine engine;
        engine.setApplicationDirectoryForTesting(alreadyExtended ? QStringLiteral("\\\\?\\") + nativeApp : app);
        QSignalSpy recognized(&engine, &mr::OfflineOcrEngine::imageRecognized);
        QSignalSpy finished(&engine, &mr::OfflineOcrEngine::finished);
        QSignalSpy failures(&engine, &mr::OfflineOcrEngine::failed);
        QVERIFY(engine.start({createScreenshot(directory)}));
        QTRY_COMPARE_WITH_TIMEOUT(finished.size(), 1, 10000);
        QVERIFY2(failures.isEmpty(), failures.isEmpty() ? "" : qPrintable(failures[0][0].toString()));
        QVERIFY(finished[0][0].toBool());
        QCOMPARE(recognized.size(), 1);
        QCOMPARE(recognized[0][1].toByteArray(), wholeImageTsv());
        QFile file(log);
        QVERIFY(file.open(QIODevice::ReadOnly));
        const auto record = QJsonDocument::fromJson(file.readAll().trimmed()).object();
        // Verify the path Windows actually used to load the child, beyond the
        // QProcess property: PyInstaller adds dependency paths below this root.
        QCOMPARE(record.value(QStringLiteral("module_path")).toString(),
                 QStringLiteral("\\\\?\\") + QDir::toNativeSeparators(mr::OfflineOcrEngine::enginePath(app)));
    }
#endif

    void emptyWholeImageResultRemainsEmpty()
    {
        EngineBench bench;
        qputenv("MR_TEST_AI_OCR_MODE", "empty");
        QVERIFY(bench.engine.start({bench.screenshot()}));
        QTRY_COMPARE_WITH_TIMEOUT(bench.finished.size(), 1, 5000);
        QVERIFY(bench.finished[0][0].toBool());
        QCOMPARE(bench.recognized[0][1].toByteArray(), ocrTsvHeader());
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
        EngineBench bench;
        const QString ready = bench.directory.filePath(QStringLiteral("ready"));
        qputenv("MR_TEST_AI_OCR_READY", ready.toUtf8());
        qputenv("MR_TEST_AI_OCR_MODE", mode);
        QVERIFY(bench.engine.start({bench.screenshot()}));
        QTRY_COMPARE_WITH_TIMEOUT(bench.finished.size(), 1, 5000);
        QVERIFY(!bench.finished[0][0].toBool());
        QCOMPARE(bench.failures.size(), 1);
        QVERIFY(bench.recognized.isEmpty() && !bench.engine.busy());
        const QString work = workDirectoryOf(ready);
        QVERIFY(!work.isEmpty());
        QVERIFY(!QFileInfo::exists(work));
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
        EngineBench bench;
        const QString path = bench.screenshot();
        const QString ready = bench.directory.filePath(QStringLiteral("ready"));
        qputenv("MR_TEST_AI_OCR_READY", ready.toUtf8());
        qputenv("MR_TEST_AI_OCR_MODE", "sleep");
        // Leave cold process startup time before testing expiry. The worker must
        // outlive this deadline so a normal exit cannot masquerade as a timeout.
        bench.engine.setTimeoutForTesting(5000);
        QVERIFY(bench.engine.start({path}));
        QTRY_VERIFY_WITH_TIMEOUT(QFileInfo::exists(ready), 3000);
        const QString work = workDirectoryOf(ready);
        QVERIFY(!work.isEmpty());
        if (!timeout)
            bench.engine.cancel();
        QTRY_COMPARE_WITH_TIMEOUT(bench.finished.size(), 1, 5000);
        QVERIFY(!bench.finished[0][0].toBool());
        QCOMPARE(bench.failures.size(), timeout ? 1 : 0);
        if (timeout)
            QVERIFY(bench.failures[0][0].toString().contains(QStringLiteral("图片识别超时")));
        QVERIFY(bench.recognized.isEmpty() && !QFileInfo::exists(work) && !bench.engine.busy());
        qunsetenv("MR_TEST_AI_OCR_MODE");
        bench.engine.setTimeoutForTesting(5000);
        QVERIFY(bench.engine.start({path}));
        QTRY_COMPARE_WITH_TIMEOUT(bench.finished.size(), 2, 5000);
        QVERIFY(bench.finished[1][0].toBool());
        QCOMPARE(bench.recognized.size(), 1);
    }

    void destructionKillsChildAndRemovesOwnedImage()
    {
        QTemporaryDir directory;
        const QString ready = directory.filePath(QStringLiteral("ready"));
        qputenv("MR_TEST_AI_OCR_READY", ready.toUtf8());
        qputenv("MR_TEST_AI_OCR_MODE", "sleep");
        auto engine = std::make_unique<mr::OfflineOcrEngine>();
        engine->setApplicationDirectoryForTesting(installOcrStub(directory));
        QVERIFY(engine->start({createScreenshot(directory)}));
        QTRY_VERIFY_WITH_TIMEOUT(QFileInfo::exists(ready), 3000);
        const QString work = workDirectoryOf(ready);
        QVERIFY(!work.isEmpty());
        QVERIFY(QFileInfo::exists(work));
        engine.reset();
        QVERIFY(!QFileInfo::exists(work));
    }

    void drainsNoisyPipesWithoutChangingRecognition()
    {
        EngineBench bench;
        qputenv("MR_TEST_AI_OCR_MODE", "noisy");
        QVERIFY(bench.engine.start({bench.screenshot()}));
        QTRY_COMPARE_WITH_TIMEOUT(bench.finished.size(), 1, 5000);
        QVERIFY(bench.finished[0][0].toBool());
        QCOMPARE(bench.recognized[0][1].toByteArray(), wholeImageTsv());
    }

    void failedCallbackMayStartNewBatchWithoutOldTerminalSignal()
    {
        EngineBench bench;
        const QString path = bench.screenshot();
        qputenv("MR_TEST_AI_OCR_MODE", "fail");
        bool restarted = false;
        connect(&bench.engine, &mr::OfflineOcrEngine::failed, &bench.engine, [&](const QString &) {
            qunsetenv("MR_TEST_AI_OCR_MODE");
            restarted = bench.engine.start({path});
        });
        QVERIFY(bench.engine.start({path}));
        QTRY_VERIFY_WITH_TIMEOUT(restarted, 5000);
        QTRY_COMPARE_WITH_TIMEOUT(bench.finished.size(), 1, 5000);
        QCOMPARE(bench.failures.size(), 1);
        QCOMPARE(bench.recognized.size(), 1);
        QVERIFY(bench.finished[0][0].toBool() && !bench.engine.busy());
    }

    void cancellationBeforeQueuedLaunchStartsNoWorker()
    {
        EngineBench bench;
        QVERIFY(bench.engine.start({bench.screenshot()}));
        bench.engine.cancel();
        QTRY_COMPARE_WITH_TIMEOUT(bench.finished.size(), 1, 1000);
        QVERIFY(!bench.finished[0][0].toBool());
        QVERIFY(bench.recognized.isEmpty() && bench.engine.findChildren<QProcess *>().isEmpty());
    }

    // Start, cancel and start again before either queued launch ran: the engine owns exactly one
    // running worker, and each start settles once.
    void rapidCancelRestartOwnsOnlyOneProcess()
    {
        EngineBench bench;
        const QString path = bench.screenshot();
        qputenv("MR_TEST_AI_OCR_MODE", "sleep");
        bench.engine.setTimeoutForTesting(5000);
        QVERIFY(bench.engine.start({path}));
        bench.engine.cancel();
        QVERIFY(!bench.engine.busy());
        QVERIFY(bench.engine.start({path})); // Before either queued start ran.
        QTest::qWait(100);
        const auto activeProcesses = [&bench] {
            int count = 0;
            for (auto *process : bench.engine.findChildren<QProcess *>())
                if (process->state() != QProcess::NotRunning)
                    ++count;
            return count;
        };
        QCOMPARE(activeProcesses(), 1);
        bench.engine.cancel();
        QTRY_VERIFY_WITH_TIMEOUT(!bench.engine.busy(), 5000);
        QTRY_COMPARE_WITH_TIMEOUT(activeProcesses(), 0, 5000);
        QCOMPARE(bench.finished.size(), 2);
    }

    void imagesRunSeriallyAndSettleOnce()
    {
        EngineBench bench;
        const QString first = bench.screenshot();
        const QString second = bench.screenshot(QStringLiteral("第二张.png"));
        QVERIFY(bench.engine.start({first, second}));
        QTRY_COMPARE_WITH_TIMEOUT(bench.finished.size(), 1, 5000);
        QCOMPARE(bench.recognized.size(), 2);
        QCOMPARE(bench.recognized[0][0].toString(), first);
        QCOMPARE(bench.recognized[1][0].toString(), second);
        QCOMPARE(bench.engine.completedImages(), 2);
        QVERIFY(bench.finished[0][0].toBool());
    }
};
} // namespace

MR_DESKTOP_TEST_SUITE(OfflineOcrEngine)
{
    if (argc >= 3 && QFileInfo(QString::fromLocal8Bit(argv[0])).baseName() == QLatin1String("local-ai-ocr"))
        return worker(argc, argv);
    QGuiApplication application(argc, argv);
    OfflineOcrEngineTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "OfflineOcrEngineTests.moc"
