#include "OfflineOcrEngine.h"
#include "ScreenshotImportParser.h"

#include <QCoreApplication>
#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QGuiApplication>
#include <QImage>
#include <QPainter>
#include <QProcess>
#include <QSignalSpy>
#include <QTemporaryDir>
#include <QTest>
#include <QThread>

#include <algorithm>

namespace {

constexpr auto kHeader = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n";

QByteArray word(int line, int x, int y, int width, int height, const QString &text)
{
    return QStringLiteral("5\t1\t%1\t1\t1\t1\t%2\t%3\t%4\t%5\t95\t%6\n")
        .arg(line).arg(x).arg(y).arg(width).arg(height).arg(text).toUtf8();
}

bool writeFile(const QString &path, const QByteArray &bytes)
{
    QFile file(path);
    return file.open(QIODevice::WriteOnly) && file.write(bytes) == bytes.size();
}

QString installWorker(QTemporaryDir &directory)
{
    const QString app = directory.filePath(QStringLiteral("app"));
    const QString ocr = QDir(app).filePath(QStringLiteral("ocr"));
    if (!QDir().mkpath(QDir(ocr).filePath(QStringLiteral("tessdata")))
        || !QFile::copy(QCoreApplication::applicationFilePath(), QDir(ocr).filePath(QStringLiteral("tesseract.exe")))
        || !writeFile(QDir(ocr).filePath(QStringLiteral("tessdata/chi_sim.traineddata")), "synthetic")
        || !writeFile(QDir(ocr).filePath(QStringLiteral("tessdata/eng.traineddata")), "synthetic"))
        return {};
    return app;
}

QString createScreenshot(QTemporaryDir &directory)
{
    QImage image(1000, qEnvironmentVariableIsSet("MR_TEST_OCR_EXTRA_CARD") ? 900 : 600, QImage::Format_RGB32);
    image.fill(QColor(155, 202, 227));
    QPainter painter(&image);
    // Actual card is narrower than the parser's broad evidence rectangle. A
    // refined title/body must exclude the bright webpage stripe beside it.
    painter.fillRect(QRect(50, 190, 900, 220), QColor(51, 81, 32));
    painter.fillRect(QRect(340, 215, 46, 46), QColor(30, 180, 230));
    painter.setPen(QPen(Qt::white, 3));
    painter.drawLine(874, 289, 896, 311);
    painter.drawLine(874, 311, 896, 289);
    if (qEnvironmentVariableIsSet("MR_TEST_OCR_NON_BUTTON_X")) {
        painter.drawLine(832, 289, 854, 311);
        painter.drawLine(832, 311, 854, 289);
    }
    if (qEnvironmentVariableIsSet("MR_TEST_OCR_EXTRA_CARD")) {
        painter.fillRect(QRect(50, 450, 900, 220), QColor(51, 81, 32));
        painter.fillRect(QRect(340, 475, 46, 46), QColor(30, 180, 230));
    }
    if (qEnvironmentVariableIsSet("MR_TEST_OCR_PAGING_OVERLAY")) {
        const QString overlay = qEnvironmentVariable("MR_TEST_OCR_PAGING_OVERLAY");
        // A translucent-looking dark bar still lies within the surface colour
        // tolerance. Its pixel edge is above the text's coarse OCR bounding box.
        const int top = overlay == QLatin1String("visible") ? 335 : overlay == QLatin1String("edge") ? 287 : 283;
        painter.fillRect(QRect(150, top, 750, 410 - top), QColor(75, 110, 70));
    }
    painter.end();
    const QString path = directory.filePath(QStringLiteral("screenshot.png"));
    return image.save(path) ? path : QString();
}

int worker(int argc, char **argv)
{
    QCoreApplication application(argc, argv);
    const QString input = QString::fromLocal8Bit(argv[1]);
    const QString kind = QFileInfo(input).baseName().section(QLatin1Char('-'), 0, 0);
    if (kind == QLatin1String("title")) {
        const QString marker = qEnvironmentVariable("MR_TEST_OCR_FIELD_MARKER");
        if (!marker.isEmpty())
            writeFile(marker, "started");
        if (qEnvironmentVariableIsSet("MR_TEST_OCR_FIELD_SLEEP"))
            QThread::msleep(10000);
    }
    const int delay = qEnvironmentVariableIntValue("MR_TEST_OCR_EVERY_PASS_DELAY");
    if (delay > 0)
        QThread::msleep(static_cast<unsigned long>(delay));
    QByteArray tsv(kHeader);
    if (kind == QLatin1String("input")) {
        tsv += word(1, 250, 220, 80, 32, QStringLiteral("Lv.50"));
        tsv += word(2, 400, 226, 200, 30, QStringLiteral("BRERA"));
        if (qEnvironmentVariable("MR_TEST_OCR_PAGING_OVERLAY") == QLatin1String("edge"))
            tsv += word(3, 250, 271, 70, 16, QStringLiteral("VISIBLE")); // Bottom equals overlay top minus one.
        else
            tsv += word(3, 250, 290, 160, 32, QStringLiteral("ASAE"));
        tsv += word(4, 866, 292, 25, 25, QStringLiteral("闪"));
        if (qEnvironmentVariableIsSet("MR_TEST_OCR_PAGING_OVERLAY"))
            tsv += word(5, 350, 345, 90, 20, QStringLiteral("10/page"));
        else {
            tsv += word(5, 690, 355, 150, 18, QStringLiteral("2024-09-20"));
            tsv += word(6, 850, 355, 90, 18, QStringLiteral("12:56:16"));
        }
    } else if (kind == QLatin1String("level")) {
        const bool second = QFileInfo(input).baseName().section(QLatin1Char('-'), 1, 1).toInt() >= 4;
        if (qEnvironmentVariableIsSet("MR_TEST_OCR_SPLIT_LEVEL")) {
            tsv += word(1, 18, 34, 15, 30, QStringLiteral("L"));
            tsv += word(1, 38, 34, 50, 30, second ? QStringLiteral("v.60") : QStringLiteral("v.50"));
        } else
            tsv += word(1, 18, 34, 80, 30, second ? QStringLiteral("Lv.60") : QStringLiteral("Lv.50"));
    } else if (kind == QLatin1String("title")) {
        const QImage padded(input);
        for (int y = 0; y < padded.height(); ++y)
            for (int x = 0; x < padded.width(); ++x) {
                const QColor color = padded.pixelColor(x, y);
                if (color.blue() > color.red() * 1.25 && color.green() > color.red() * 1.2)
                    return 10;
            }
        tsv += word(1, 24, 31, 200, 30, QStringLiteral("合成副本"));
    } else if (kind == QLatin1String("body")) {
        const QImage padded(input);
        if (qEnvironmentVariableIsSet("MR_TEST_OCR_PAGING_OVERLAY")
            && qEnvironmentVariable("MR_TEST_OCR_PAGING_OVERLAY") != QLatin1String("visible"))
            return 11; // The remaining area is too thin for a full body line.
        // Original screenshot cross must be removed from the OCR copy while
        // its original pixel evidence is returned as a deletion warning.
        int whiteCount = 0;
        for (int y = 0; y < padded.height(); ++y)
            for (int x = 0; x < padded.width(); ++x) {
                const QColor color = padded.pixelColor(x, y);
                if (qEnvironmentVariableIsSet("MR_TEST_OCR_PAGING_OVERLAY")
                    && color == QColor(75, 110, 70))
                    return 12; // Pagination pixels must never reach body OCR.
                if (std::min({color.red(), color.green(), color.blue()}) > 190)
                    ++whiteCount;
            }
        if ((whiteCount > 0) != qEnvironmentVariableIsSet("MR_TEST_OCR_NON_BUTTON_X"))
            return 9;
        if (!qEnvironmentVariableIsSet("MR_TEST_OCR_EMPTY_BODY"))
            tsv += word(1, 18, 34, 160, 32, QStringLiteral("无事发生"));
        if (qEnvironmentVariableIsSet("MR_TEST_OCR_NON_BUTTON_X"))
            tsv += word(1, 600, 34, 48, 32, QStringLiteral("XD"));
    } else if (kind == QLatin1String("time")) {
        if (!qEnvironmentVariableIsSet("MR_TEST_OCR_PAGING_OVERLAY")) {
            tsv += word(1, 22, 22, 150, 18, QStringLiteral("2024-09-20"));
            tsv += word(1, 182, 22, 90, 18, QStringLiteral("12:56:16"));
        }
    } else {
        return 8;
    }
    return writeFile(QString::fromLocal8Bit(argv[2]) + QStringLiteral(".tsv"), tsv) ? 0 : 1;
}

} // namespace

class OfflineOcrEngineTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void cleanup()
    {
        for (const auto &name : {"MR_TEST_OCR_FIELD_MARKER", "MR_TEST_OCR_FIELD_SLEEP",
                                 "MR_TEST_OCR_EVERY_PASS_DELAY", "MR_TEST_OCR_EMPTY_BODY"})
            qunsetenv(name);
        qunsetenv("MR_TEST_OCR_NON_BUTTON_X");
        qunsetenv("MR_TEST_OCR_EXTRA_CARD");
        qunsetenv("MR_TEST_OCR_SPLIT_LEVEL");
        qunsetenv("MR_TEST_OCR_PAGING_OVERLAY");
    }

    void fieldPassesPreserveOriginalCoordinatesAndDeleteEvidence()
    {
        QTemporaryDir directory;
        QVERIFY(directory.isValid());
        const QString app = installWorker(directory);
        const QString path = createScreenshot(directory);
        QVERIFY(!app.isEmpty() && !path.isEmpty());
        mr::OfflineOcrEngine engine;
        engine.setApplicationDirectoryForTesting(app);
        QSignalSpy recognized(&engine, &mr::OfflineOcrEngine::imageRecognized);
        QSignalSpy finished(&engine, &mr::OfflineOcrEngine::finished);
        QSignalSpy failures(&engine, &mr::OfflineOcrEngine::failed);
        QVERIFY(engine.start({path}));
        QTRY_COMPARE_WITH_TIMEOUT(finished.size(), 1, 10000);
        QVERIFY2(failures.isEmpty(), failures.isEmpty() ? "" : qPrintable(failures[0][0].toString()));
        QVERIFY(finished[0][0].toBool());
        QCOMPARE(recognized.size(), 1);
        QCOMPARE(recognized[0][0].toString(), path);
        const QByteArray tsv = recognized[0][1].toByteArray();
        QVERIFY(tsv.contains(QStringLiteral("合成副本").toUtf8()));
        QVERIFY(tsv.contains(QStringLiteral("无事发生").toUtf8()));
        QVERIFY(!tsv.contains("BRERA") && !tsv.contains("ASAE"));
        QVERIFY(!tsv.contains(QStringLiteral("闪").toUtf8()));
        QVERIFY(tsv.contains("\t408\t225\t200\t30\t"));
        const auto rows = mr::ScreenshotImportParser::parse(QImage(path), tsv, path);
        QCOMPARE(rows.size(), 1);
        const auto candidate = rows[0].toMap();
        QCOMPARE(candidate.value("duty_name").toString(), QStringLiteral("合成副本"));
        QCOMPARE(candidate.value("reflection_text").toString(), QStringLiteral("无事发生"));
        QCOMPARE(candidate.value("source_recorded_at").toString(), QStringLiteral("2024-09-20 12:56:16"));
        QVERIFY(candidate.value("warnings").toStringList().join(QLatin1Char(' ')).contains(QStringLiteral("删除按钮")));
        const auto processes = engine.findChildren<QProcess *>();
        QVERIFY(std::all_of(processes.begin(), processes.end(),
                            [](QProcess *process) { return process->state() == QProcess::NotRunning; }));
    }

    void emptyRefinementKeepsCoarseText()
    {
        QTemporaryDir directory;
        const QString app = installWorker(directory);
        const QString path = createScreenshot(directory);
        QVERIFY(!app.isEmpty() && !path.isEmpty());
        qputenv("MR_TEST_OCR_EMPTY_BODY", "1");
        mr::OfflineOcrEngine engine;
        engine.setApplicationDirectoryForTesting(app);
        QSignalSpy recognized(&engine, &mr::OfflineOcrEngine::imageRecognized);
        QSignalSpy finished(&engine, &mr::OfflineOcrEngine::finished);
        QVERIFY(engine.start({path}));
        QTRY_COMPARE_WITH_TIMEOUT(finished.size(), 1, 10000);
        QVERIFY(finished[0][0].toBool());
        QVERIFY(recognized[0][1].toByteArray().contains("ASAE"));
    }

    void diagonalLetterXOutsideButtonPositionRemainsBodyText()
    {
        QTemporaryDir directory;
        qputenv("MR_TEST_OCR_NON_BUTTON_X", "1");
        const QString app = installWorker(directory);
        const QString path = createScreenshot(directory);
        QVERIFY(!app.isEmpty() && !path.isEmpty());
        mr::OfflineOcrEngine engine;
        engine.setApplicationDirectoryForTesting(app);
        QSignalSpy recognized(&engine, &mr::OfflineOcrEngine::imageRecognized);
        QSignalSpy finished(&engine, &mr::OfflineOcrEngine::finished);
        QVERIFY(engine.start({path}));
        QTRY_COMPARE_WITH_TIMEOUT(finished.size(), 1, 10000);
        QVERIFY(finished[0][0].toBool());
        QCOMPARE(recognized.size(), 1);
        const auto rows = mr::ScreenshotImportParser::parse(QImage(path), recognized[0][1].toByteArray(), path);
        QCOMPARE(rows.size(), 1);
        QCOMPARE(rows[0].toMap().value("reflection_text").toString(), QStringLiteral("无事发生XD"));
    }

    void missingCoarseHeaderIsRecoveredFromIndependentImageAnchor()
    {
        QTemporaryDir directory;
        qputenv("MR_TEST_OCR_EXTRA_CARD", "1");
        const QString app = installWorker(directory);
        const QString path = createScreenshot(directory);
        QVERIFY(!app.isEmpty() && !path.isEmpty());
        mr::OfflineOcrEngine engine;
        engine.setApplicationDirectoryForTesting(app);
        QSignalSpy recognized(&engine, &mr::OfflineOcrEngine::imageRecognized);
        QSignalSpy finished(&engine, &mr::OfflineOcrEngine::finished);
        QSignalSpy failures(&engine, &mr::OfflineOcrEngine::failed);
        QVERIFY(engine.start({path}));
        QTRY_COMPARE_WITH_TIMEOUT(finished.size(), 1, 10000);
        QVERIFY2(failures.isEmpty(), failures.isEmpty() ? "" : qPrintable(failures[0][0].toString()));
        QVERIFY(finished[0][0].toBool());
        QCOMPARE(recognized.size(), 1);
        const auto rows = mr::ScreenshotImportParser::parse(QImage(path), recognized[0][1].toByteArray(), path);
        QCOMPARE(rows.size(), 2);
        QCOMPARE(rows[0].toMap().value("duty_level").toInt(), 50);
        QCOMPARE(rows[1].toMap().value("duty_level").toInt(), 60);
        QCOMPARE(rows[1].toMap().value("duty_name").toString(), QStringLiteral("合成副本"));
    }

    void splitLevelWordsRecoverMissingCoarseHeader()
    {
        QTemporaryDir directory;
        qputenv("MR_TEST_OCR_EXTRA_CARD", "1");
        qputenv("MR_TEST_OCR_SPLIT_LEVEL", "1");
        const QString app = installWorker(directory);
        const QString path = createScreenshot(directory);
        QVERIFY(!app.isEmpty() && !path.isEmpty());
        mr::OfflineOcrEngine engine;
        engine.setApplicationDirectoryForTesting(app);
        QSignalSpy recognized(&engine, &mr::OfflineOcrEngine::imageRecognized);
        QSignalSpy finished(&engine, &mr::OfflineOcrEngine::finished);
        QSignalSpy failures(&engine, &mr::OfflineOcrEngine::failed);
        QVERIFY(engine.start({path}));
        QTRY_COMPARE_WITH_TIMEOUT(finished.size(), 1, 10000);
        QVERIFY2(failures.isEmpty(), failures.isEmpty() ? "" : qPrintable(failures[0][0].toString()));
        QVERIFY(finished[0][0].toBool());
        const auto rows = mr::ScreenshotImportParser::parse(QImage(path), recognized[0][1].toByteArray(), path);
        QCOMPARE(rows.size(), 2);
        QCOMPARE(rows[0].toMap().value("duty_level").toInt(), 50);
        QCOMPARE(rows[1].toMap().value("duty_level").toInt(), 60);
    }

    void paginationPixelsAndCoveredCoarseNoteNeverBecomeReflection_data()
    {
        QTest::addColumn<QByteArray>("overlay");
        QTest::addColumn<QString>("expectedNote");
        QTest::newRow("fully-covered") << QByteArray("hidden") << QString();
        QTest::newRow("visible-note-without-date") << QByteArray("visible") << QStringLiteral("无事发生");
        QTest::newRow("thin-visible-note-touches-overlay") << QByteArray("edge") << QStringLiteral("VISIBLE");
    }

    void paginationPixelsAndCoveredCoarseNoteNeverBecomeReflection()
    {
        QFETCH(QByteArray, overlay);
        QFETCH(QString, expectedNote);
        QTemporaryDir directory;
        qputenv("MR_TEST_OCR_PAGING_OVERLAY", overlay);
        const QString app = installWorker(directory);
        const QString path = createScreenshot(directory);
        QVERIFY(!app.isEmpty() && !path.isEmpty());
        mr::OfflineOcrEngine engine;
        engine.setApplicationDirectoryForTesting(app);
        QSignalSpy recognized(&engine, &mr::OfflineOcrEngine::imageRecognized);
        QSignalSpy finished(&engine, &mr::OfflineOcrEngine::finished);
        QSignalSpy failures(&engine, &mr::OfflineOcrEngine::failed);
        QVERIFY(engine.start({path}));
        QTRY_COMPARE_WITH_TIMEOUT(finished.size(), 1, 10000);
        QVERIFY2(failures.isEmpty(), failures.isEmpty() ? "" : qPrintable(failures[0][0].toString()));
        QVERIFY(finished[0][0].toBool());
        QCOMPARE(recognized.size(), 1);
        const QByteArray tsv = recognized[0][1].toByteArray();
        QVERIFY(!tsv.contains("ASAE"));
        const auto rows = mr::ScreenshotImportParser::parse(QImage(path), tsv, path);
        QCOMPARE(rows.size(), 1);
        QCOMPARE(rows[0].toMap().value("duty_name").toString(), QStringLiteral("合成副本"));
        QCOMPARE(rows[0].toMap().value("reflection_text").toString(), expectedNote);
        QVERIFY(rows[0].toMap().value("source_recorded_at").toString().isEmpty());
        QVERIFY(rows[0].toMap().value("warnings").toStringList().join(QLatin1Char(' ')).contains(QStringLiteral("分页")));
    }

    void cancellationDuringFieldPassEmitsNoPartialImage()
    {
        QTemporaryDir directory;
        const QString app = installWorker(directory);
        const QString path = createScreenshot(directory);
        QVERIFY(!app.isEmpty() && !path.isEmpty());
        const QString marker = directory.filePath(QStringLiteral("field-started"));
        qputenv("MR_TEST_OCR_FIELD_MARKER", marker.toUtf8());
        qputenv("MR_TEST_OCR_FIELD_SLEEP", "1");
        mr::OfflineOcrEngine engine;
        engine.setApplicationDirectoryForTesting(app);
        QSignalSpy recognized(&engine, &mr::OfflineOcrEngine::imageRecognized);
        QSignalSpy finished(&engine, &mr::OfflineOcrEngine::finished);
        QSignalSpy failures(&engine, &mr::OfflineOcrEngine::failed);
        QVERIFY(engine.start({path}));
        QTRY_VERIFY_WITH_TIMEOUT(QFileInfo::exists(marker), 5000);
        engine.cancel();
        QTRY_COMPARE_WITH_TIMEOUT(finished.size(), 1, 5000);
        QVERIFY(!finished[0][0].toBool());
        QVERIFY(recognized.isEmpty() && failures.isEmpty());
        QVERIFY(!engine.busy());
        qunsetenv("MR_TEST_OCR_FIELD_SLEEP");
        QVERIFY(engine.start({path}));
        QTRY_COMPARE_WITH_TIMEOUT(finished.size(), 2, 10000);
        QVERIFY(finished[1][0].toBool());
        QCOMPARE(recognized.size(), 1);
    }

    void allFieldPassesShareOneImageDeadline()
    {
        QTemporaryDir directory;
        const QString app = installWorker(directory);
        const QString path = createScreenshot(directory);
        QVERIFY(!app.isEmpty() && !path.isEmpty());
        qputenv("MR_TEST_OCR_EVERY_PASS_DELAY", "200");
        mr::OfflineOcrEngine engine;
        engine.setApplicationDirectoryForTesting(app);
        engine.setTimeoutForTesting(600);
        QSignalSpy recognized(&engine, &mr::OfflineOcrEngine::imageRecognized);
        QSignalSpy finished(&engine, &mr::OfflineOcrEngine::finished);
        QSignalSpy failures(&engine, &mr::OfflineOcrEngine::failed);
        QVERIFY(engine.start({path}));
        QTRY_COMPARE_WITH_TIMEOUT(finished.size(), 1, 5000);
        QVERIFY(!finished[0][0].toBool());
        QVERIFY(recognized.isEmpty());
        QCOMPARE(failures.size(), 1);
        QVERIFY(failures[0][0].toString().contains(QStringLiteral("超时")));
    }

    void failedCallbackMayStartNewBatchWithoutOldTerminalSignal()
    {
        QTemporaryDir directory;
        const QString app = installWorker(directory);
        const QString path = createScreenshot(directory);
        QVERIFY(!app.isEmpty() && !path.isEmpty());
        qputenv("MR_TEST_OCR_FIELD_SLEEP", "1");
        mr::OfflineOcrEngine engine;
        engine.setApplicationDirectoryForTesting(app);
        engine.setTimeoutForTesting(350);
        QSignalSpy recognized(&engine, &mr::OfflineOcrEngine::imageRecognized);
        QSignalSpy finished(&engine, &mr::OfflineOcrEngine::finished);
        QSignalSpy failures(&engine, &mr::OfflineOcrEngine::failed);
        bool restarted = false;
        connect(&engine, &mr::OfflineOcrEngine::failed, &engine, [&](const QString &) {
            qunsetenv("MR_TEST_OCR_FIELD_SLEEP");
            engine.setTimeoutForTesting(5000);
            restarted = engine.start({path});
        });
        QVERIFY(engine.start({path}));
        QTRY_VERIFY_WITH_TIMEOUT(restarted, 5000);
        QTRY_COMPARE_WITH_TIMEOUT(finished.size(), 1, 10000);
        QCOMPARE(failures.size(), 1);
        QVERIFY(finished[0][0].toBool());
        QCOMPARE(recognized.size(), 1);
        QVERIFY(!engine.busy());
    }
};

int main(int argc, char **argv)
{
    if (argc >= 3 && QFileInfo(QString::fromLocal8Bit(argv[0])).baseName() == QLatin1String("tesseract"))
        return worker(argc, argv);
    QGuiApplication application(argc, argv);
    OfflineOcrEngineTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "OfflineOcrEngineTests.moc"
