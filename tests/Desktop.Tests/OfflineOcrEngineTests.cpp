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

// 网页单行记录：每列字块用一种独有的浅色，分区副本中出现哪些颜色即可判断裁切是否越列。
const QColor kRowLevelInk(240, 240, 200);
const QColor kRowDutyInk(231, 232, 233);
const QColor kRowDateInk(232, 231, 233);
const QColor kRowTimeInk(233, 232, 231);
const QColor kRowNoteInk(231, 233, 232);
const QColor kRowOverflowInk(232, 233, 231);

constexpr int kRowPitch = 160;

int rowCount()
{
    return std::max(1, qEnvironmentVariableIntValue("MR_TEST_OCR_ROW_COUNT"));
}

/**
 * @brief 两行高的网页记录：副本名、日期/时间、心得各占两行，红色类型图标，行尾白色删除叉号靠近心得。
 * 环境变量可改为蓝色图标、抗锯齿粗笔叉号、与心得相连的叉号，或重复多行。
 */
QString createRowScreenshot(QTemporaryDir &directory)
{
    QImage image(1400, 80 + rowCount() * kRowPitch, QImage::Format_RGB32);
    image.fill(Qt::white);
    QPainter painter(&image);
    for (int row = 0; row < rowCount(); ++row) {
        painter.save();
        painter.translate(0, row * kRowPitch);
        painter.fillRect(QRect(7, 40, 1360, 151), QColor(45, 69, 38));
        if (qEnvironmentVariableIsSet("MR_TEST_OCR_ROW_BLUE_ICON"))
            painter.fillRect(QRect(290, 95, 30, 30), QColor(30, 180, 230)); // Inside the card branch's 20–62 % band.
        else
            painter.fillRect(QRect(280, 95, 30, 30), QColor(220, 70, 50));
        painter.fillRect(QRect(145, 103, 44, 15), kRowLevelInk);
        painter.fillRect(QRect(330, 80, 126, 20), kRowDutyInk);
        painter.fillRect(QRect(330, 120, 42, 20), kRowDutyInk);
        painter.fillRect(QRect(600, 82, 106, 15), kRowDateInk);
        painter.fillRect(QRect(600, 122, 74, 15), kRowTimeInk);
        painter.fillRect(QRect(760, 80, qEnvironmentVariableIsSet("MR_TEST_OCR_ROW_FUSED_X") ? 561 : 552, 20), kRowNoteInk);
        painter.fillRect(QRect(760, 120, 100, 20), kRowNoteInk);
        if (qEnvironmentVariableIsSet("MR_TEST_OCR_ROW_OVERFLOW"))
            painter.fillRect(QRect(1350, 80, 12, 20), kRowOverflowInk); // A long note continues past the button.
        const bool thick = qEnvironmentVariableIsSet("MR_TEST_OCR_ROW_AA_X");
        painter.setRenderHint(QPainter::Antialiasing, thick);
        painter.setPen(QPen(Qt::white, thick ? 7 : 3));
        painter.drawLine(1320, 100, 1342, 122);
        painter.drawLine(1320, 122, 1342, 100);
        painter.restore();
    }
    painter.end();
    const QString path = directory.filePath(QStringLiteral("rows.png"));
    return image.save(path) ? path : QString();
}

/** @brief 分区副本中某种字块颜色按上下分组后的外接框（副本坐标）。 */
QList<QRect> inkBands(const QImage &image, const QColor &ink)
{
    QList<QRect> bands;
    for (int y = 0; y < image.height(); ++y) {
        QRect line;
        for (int x = 0; x < image.width(); ++x) {
            if (image.pixelColor(x, y) == ink)
                line = line.united(QRect(x, y, 1, 1));
        }
        if (!line.isValid())
            continue;
        if (!bands.isEmpty() && bands.back().bottom() >= y - 1)
            bands.back() = bands.back().united(line);
        else
            bands.append(line);
    }
    return bands;
}

/** @brief 每个字块一行，与 Tesseract 分块模式为每行文字编不同 line_num 的输出一致。 */
QByteArray bandWords(const QList<QRect> &bands, const QStringList &texts)
{
    QByteArray tsv;
    for (int i = 0; i < bands.size() && i < texts.size(); ++i)
        tsv += QStringLiteral("5\t1\t1\t1\t%1\t1\t%2\t%3\t%4\t%5\t95\t%6\n")
                   .arg(i + 1).arg(bands[i].x()).arg(bands[i].y()).arg(bands[i].width()).arg(bands[i].height())
                   .arg(texts[i]).toUtf8();
    return tsv;
}

/** @brief 网页单行记录的假识别进程：粗识别给出错字，分区副本只在裁切正确时返回正确文字。 */
int rowWorker(const QString &kind, const QString &input, const QStringList &arguments, QByteArray &tsv)
{
    if (kind == QLatin1String("input")) {
        const QString level = qEnvironmentVariable("MR_TEST_OCR_ROW_COARSE_LEVEL", QStringLiteral("Lv.50"));
        const QString date = qEnvironmentVariable("MR_TEST_OCR_ROW_COARSE_DATE", QStringLiteral("2024-09-11"));
        for (int row = 0; row < rowCount(); ++row) {
            const int y = row * kRowPitch;
            const int block = row * 10;
            tsv += word(block + 1, 145, y + 103, 44, 15, level);
            tsv += word(block + 2, 330, y + 80, 126, 20, QStringLiteral("BRERA"));
            tsv += word(block + 3, 330, y + 120, 42, 20, QStringLiteral("作占"));
            tsv += word(block + 4, 600, y + 82, 106, 15, date);
            if (!qEnvironmentVariableIsSet("MR_TEST_OCR_ROW_COARSE_NO_TIME"))
                tsv += word(block + 5, 600, y + 122, 74, 15, QStringLiteral("20:22:36"));
            if (!qEnvironmentVariableIsSet("MR_TEST_OCR_ROW_COARSE_NO_NOTE")) {
                tsv += word(block + 6, 760, y + 80, 552, 20, QStringLiteral("ASAE"));
                tsv += word(block + 7, 760, y + 120, 100, 20, QStringLiteral("QQ"));
            }
        }
        return 0;
    }
    const QImage crop(input);
    const auto has = [&crop](const QColor &ink) { return !inkBands(crop, ink).isEmpty(); };
    bool saturated = false;
    bool buttonWhite = false;
    for (int y = 0; y < crop.height(); ++y)
        for (int x = 0; x < crop.width(); ++x) {
            const QColor color = crop.pixelColor(x, y);
            saturated |= std::max({color.red(), color.green(), color.blue()})
                - std::min({color.red(), color.green(), color.blue()}) >= 60;
            buttonWhite |= color == QColor(Qt::white);
        }
    const QString psm = arguments.value(arguments.indexOf(QStringLiteral("--psm")) + 1);
    if (kind == QLatin1String("level")) {
        if (!has(kRowLevelInk) || has(kRowDutyInk) || saturated)
            return 20;
        tsv += bandWords(inkBands(crop, kRowLevelInk),
                         {qEnvironmentVariable("MR_TEST_OCR_ROW_FIELD_LEVEL", QStringLiteral("Lv.50"))});
    } else if (kind == QLatin1String("title")) {
        // 标题列：类型图标之后、日期列之前，两行副本名称都在内。
        if (!has(kRowDutyInk) || has(kRowDateInk) || has(kRowTimeInk) || has(kRowLevelInk) || has(kRowNoteInk)
            || saturated || psm != QLatin1String("6"))
            return 21;
        tsv += bandWords(inkBands(crop, kRowDutyInk), {QStringLiteral("究极神兵假想"), QStringLiteral("作战")});
    } else if (kind == QLatin1String("time")) {
        // 时间列只含日期和换到下一行的时间。
        if (!has(kRowDateInk) || !has(kRowTimeInk) || has(kRowDutyInk) || has(kRowNoteInk) || has(kRowLevelInk)
            || psm != QLatin1String("6"))
            return 22;
        tsv += bandWords(inkBands(crop, kRowDateInk) + inkBands(crop, kRowTimeInk),
                         {QStringLiteral("2024-09-11"), QStringLiteral("20:22:36")});
    } else if (kind == QLatin1String("body")) {
        // 心得列：时间列之后、删除叉号之前，两行心得都在内。叉号与心得相连时无法从像素分开，
        // 分区识别会把它读成心得末字。
        const bool fused = qEnvironmentVariableIsSet("MR_TEST_OCR_ROW_FUSED_X");
        if (!has(kRowNoteInk) || has(kRowTimeInk) || has(kRowDateInk) || (buttonWhite && !fused)
            || psm != QLatin1String("6"))
            return 23;
        tsv += bandWords(inkBands(crop, kRowNoteInk), {fused ? QStringLiteral("第一行心得×") : QStringLiteral("第一行心得"),
                                                       QStringLiteral("第二行心得")});
        tsv += bandWords(inkBands(crop, kRowOverflowInk), {QStringLiteral("召")});
    } else {
        return 24;
    }
    return 0;
}

int worker(int argc, char **argv)
{
    QCoreApplication application(argc, argv);
    const QString input = QString::fromLocal8Bit(argv[1]);
    const QString kind = QFileInfo(input).baseName().section(QLatin1Char('-'), 0, 0);
    if (qEnvironmentVariableIsSet("MR_TEST_OCR_ROW_LAYOUT")) {
        const QString log = qEnvironmentVariable("MR_TEST_OCR_FIELD_LOG");
        if (!log.isEmpty() && kind != QLatin1String("input")) {
            QFile file(log);
            if (file.open(QIODevice::Append))
                file.write(kind.toUtf8() + '\n');
        }
        QByteArray tsv(kHeader);
        const int code = rowWorker(kind, input, application.arguments(), tsv);
        if (code != 0)
            return code;
        return writeFile(QString::fromLocal8Bit(argv[2]) + QStringLiteral(".tsv"), tsv) ? 0 : 1;
    }
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

    /** @brief 用假识别进程识别网页单行记录截图，返回解析后的候选；失败原因写入 failure。 */
    QVariantList recognizeRows(QString *failure, int timeoutMs, QByteArray *tsv = nullptr)
    {
        QTemporaryDir directory;
        qputenv("MR_TEST_OCR_ROW_LAYOUT", "1");
        const QString app = installWorker(directory);
        const QString path = createRowScreenshot(directory);
        if (app.isEmpty() || path.isEmpty()) {
            *failure = QStringLiteral("fixture");
            return {};
        }
        mr::OfflineOcrEngine engine;
        engine.setApplicationDirectoryForTesting(app);
        QSignalSpy recognized(&engine, &mr::OfflineOcrEngine::imageRecognized);
        QSignalSpy finished(&engine, &mr::OfflineOcrEngine::finished);
        QSignalSpy failures(&engine, &mr::OfflineOcrEngine::failed);
        if (!engine.start({path}) || !QTest::qWaitFor([&] { return !finished.isEmpty(); }, timeoutMs)) {
            *failure = QStringLiteral("not finished");
            return {};
        }
        if (!failures.isEmpty() || recognized.size() != 1) {
            *failure = failures.isEmpty() ? QStringLiteral("no image") : failures[0][0].toString();
            return {};
        }
        if (tsv)
            *tsv = recognized[0][1].toByteArray();
        return mr::ScreenshotImportParser::parse(QImage(path), recognized[0][1].toByteArray(), path);
    }

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
        qunsetenv("MR_TEST_OCR_ROW_LAYOUT");
        qunsetenv("MR_TEST_OCR_ROW_COARSE_LEVEL");
        qunsetenv("MR_TEST_OCR_ROW_FIELD_LEVEL");
        qunsetenv("MR_TEST_OCR_ROW_OVERFLOW");
        for (const auto &name : {"MR_TEST_OCR_ROW_COUNT", "MR_TEST_OCR_ROW_BLUE_ICON", "MR_TEST_OCR_ROW_FUSED_X",
                                 "MR_TEST_OCR_ROW_AA_X", "MR_TEST_OCR_ROW_COARSE_DATE", "MR_TEST_OCR_ROW_COARSE_NO_TIME",
                                 "MR_TEST_OCR_ROW_COARSE_NO_NOTE", "MR_TEST_OCR_FIELD_LOG"})
            qunsetenv(name);
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

    void webRowFieldPassesFollowTheColumns()
    {
        QTemporaryDir directory;
        qputenv("MR_TEST_OCR_ROW_LAYOUT", "1");
        const QString app = installWorker(directory);
        const QString path = createRowScreenshot(directory);
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
        // 每列的粗识别错字都已被分区结果替换。
        for (const QByteArray &coarse : {QByteArray("BRERA"), QByteArray("ASAE"), QByteArray("QQ"),
                                         QStringLiteral("作占").toUtf8()})
            QVERIFY2(!tsv.contains(coarse), coarse.constData());
        const auto rows = mr::ScreenshotImportParser::parse(QImage(path), tsv, path);
        QCOMPARE(rows.size(), 1);
        const auto candidate = rows[0].toMap();
        QCOMPARE(candidate.value("duty_level").toInt(), 50);
        QCOMPARE(candidate.value("duty_name").toString(), QStringLiteral("究极神兵假想作战"));
        QCOMPARE(candidate.value("source_recorded_at").toString(), QStringLiteral("2024-09-11 20:22:36"));
        QCOMPARE(candidate.value("reflection_text").toString(), QStringLiteral("第一行心得\n第二行心得"));
        const QString warnings = candidate.value("warnings").toStringList().join(QLatin1Char(' '));
        QVERIFY(warnings.contains(QStringLiteral("删除按钮")));
        QVERIFY(!warnings.contains(QStringLiteral("未识别到心得正文")));
    }

    void webRowFieldBudgetNeverAbortsALongPage()
    {
        // 40 行的网页截图超出分区上限：已分配的行照常重读，其余行保留粗识别，整批不报错。
        QTemporaryDir logDirectory;
        const QString log = logDirectory.filePath(QStringLiteral("fields.log"));
        qputenv("MR_TEST_OCR_FIELD_LOG", log.toUtf8());
        qputenv("MR_TEST_OCR_ROW_COUNT", "40");
        QString failure;
        const QVariantList rows = recognizeRows(&failure, 60000);
        QVERIFY2(failure.isEmpty(), qPrintable(failure));
        QFile passes(log);
        QVERIFY(passes.open(QIODevice::ReadOnly));
        QCOMPARE(passes.readAll().count('\n'), 96);
        QCOMPARE(rows.size(), 40);
        QCOMPARE(rows.front().toMap().value("duty_name").toString(), QStringLiteral("究极神兵假想作战"));
        QCOMPARE(rows.back().toMap().value("ocr_duty_name").toString(), QStringLiteral("BRERA作占"));
        for (const QVariant &row : rows) {
            QCOMPARE(row.toMap().value("duty_level").toInt(), 50);
            QCOMPARE(row.toMap().value("source_recorded_at").toString(), QStringLiteral("2024-09-11 20:22:36"));
        }
    }

    void webRowNoteWithoutCoarseWordsIsNotReRead()
    {
        // 粗识别在心得列一个字也没读到时不为它另开分区；只重读标题与时间。
        QTemporaryDir logDirectory;
        const QString log = logDirectory.filePath(QStringLiteral("fields.log"));
        qputenv("MR_TEST_OCR_FIELD_LOG", log.toUtf8());
        qputenv("MR_TEST_OCR_ROW_COARSE_NO_NOTE", "1");
        QString failure;
        const QVariantList rows = recognizeRows(&failure, 10000);
        QVERIFY2(failure.isEmpty(), qPrintable(failure));
        QCOMPARE(rows.size(), 1);
        QFile passes(log);
        QVERIFY(passes.open(QIODevice::ReadOnly));
        const QByteArray kinds = passes.readAll();
        QVERIFY2(!kinds.contains("body"), kinds.constData());
        QVERIFY(kinds.contains("title") && kinds.contains("time"));
        QCOMPARE(rows[0].toMap().value("duty_name").toString(), QStringLiteral("究极神兵假想作战"));
    }

    void webRowFieldPassesRepairWhatTheCoarsePassMissed_data()
    {
        QTest::addColumn<QByteArray>("variable");
        QTest::addColumn<QByteArray>("value");
        QTest::addColumn<QString>("note");
        const QString note = QStringLiteral("第一行心得\n第二行心得");
        // 抗锯齿粗笔画的删除叉号仍被认出，分区识别前擦除。
        QTest::newRow("antialiased-thick-delete-mark") << QByteArray("MR_TEST_OCR_ROW_AA_X") << QByteArray("1") << note;
        // 叉号与心得末字相连：像素上仍认出叉号（留下叉号证据），分区识别读成「心得×」时叉号不进入正文。
        QTest::newRow("delete-mark-fused-with-the-note") << QByteArray("MR_TEST_OCR_ROW_FUSED_X") << QByteArray("1") << note;
        // 心得越过删除叉号继续写：擦除叉号后连同叉号右侧的文字一起重读。
        QTest::newRow("note-overflows-past-the-delete-mark") << QByteArray("MR_TEST_OCR_ROW_OVERFLOW") << QByteArray("1")
                                                             << QStringLiteral("第一行心得召\n第二行心得");
        // 粗识别漏掉时间：时间分区向日期下方放宽，重读补上。
        QTest::newRow("coarse-missed-the-time") << QByteArray("MR_TEST_OCR_ROW_COARSE_NO_TIME") << QByteArray("1") << note;
        // 粗识别把日期读错一个字形：仍按单行记录处理，时间分区重读修正。
        QTest::newRow("coarse-misread-the-date") << QByteArray("MR_TEST_OCR_ROW_COARSE_DATE") << QByteArray("2024-09-1l")
                                                 << note;
        // 粗识别把等级数字读成形近字母：等级分区重读修正，不留等级提示。
        QTest::newRow("coarse-misread-the-level") << QByteArray("MR_TEST_OCR_ROW_COARSE_LEVEL") << QByteArray("Lv.5O") << note;
        // 蓝色类型图标落在手机卡片分支的取样带内：这一行仍只按列裁切，标题不含图标。
        QTest::newRow("blue-icon-inside-a-row") << QByteArray("MR_TEST_OCR_ROW_BLUE_ICON") << QByteArray("1") << note;
    }

    void webRowFieldPassesRepairWhatTheCoarsePassMissed()
    {
        QFETCH(QByteArray, variable);
        QFETCH(QByteArray, value);
        QFETCH(QString, note);
        qputenv(variable.constData(), value);
        QString failure;
        QByteArray tsv;
        const QVariantList rows = recognizeRows(&failure, 10000, &tsv);
        QVERIFY2(failure.isEmpty(), qPrintable(failure));
        QCOMPARE(rows.size(), 1);
        const QVariantMap row = rows[0].toMap();
        const QString warnings = row.value("warnings").toStringList().join(QLatin1Char(' '));
        QCOMPARE(row.value("duty_level").toInt(), 50);
        QCOMPARE(row.value("duty_name").toString(), QStringLiteral("究极神兵假想作战"));
        QCOMPARE(row.value("source_recorded_at").toString(), QStringLiteral("2024-09-11 20:22:36"));
        QCOMPARE(row.value("reflection_text").toString(), note);
        QVERIFY(warnings.contains(QStringLiteral("删除按钮")));
        QVERIFY2(!warnings.contains(QStringLiteral("等级")), qPrintable(warnings));
        QVERIFY2(!warnings.contains(QStringLiteral("来源记录时间缺失")), qPrintable(warnings));
        // The delete mark was seen in the pixels: its evidence word replaces any
        // OCR glyph standing on it.
        QVERIFY(tsv.contains("\t99999\t"));
    }

    void webRowLevelPassNeverDropsACompleteCoarsePrefix_data()
    {
        QTest::addColumn<QByteArray>("coarse");
        QTest::addColumn<QByteArray>("field");
        // 粗识别已完整读出 Lv 时不再分区重读，小字分区可能把 L 读丢。
        QTest::newRow("complete-coarse-kept") << QByteArray("Lv.50") << QByteArray("v.50");
        // 粗识别缺 L 时仍用分区结果补全。
        QTest::newRow("missing-prefix-restored") << QByteArray("v.50") << QByteArray("Lv.50");
    }

    void webRowLevelPassNeverDropsACompleteCoarsePrefix()
    {
        QFETCH(QByteArray, coarse);
        QFETCH(QByteArray, field);
        QTemporaryDir directory;
        qputenv("MR_TEST_OCR_ROW_LAYOUT", "1");
        qputenv("MR_TEST_OCR_ROW_COARSE_LEVEL", coarse);
        qputenv("MR_TEST_OCR_ROW_FIELD_LEVEL", field);
        const QString app = installWorker(directory);
        const QString path = createRowScreenshot(directory);
        QVERIFY(!app.isEmpty() && !path.isEmpty());
        mr::OfflineOcrEngine engine;
        engine.setApplicationDirectoryForTesting(app);
        QSignalSpy recognized(&engine, &mr::OfflineOcrEngine::imageRecognized);
        QSignalSpy finished(&engine, &mr::OfflineOcrEngine::finished);
        QVERIFY(engine.start({path}));
        QTRY_COMPARE_WITH_TIMEOUT(finished.size(), 1, 10000);
        QVERIFY(finished[0][0].toBool());
        const auto rows = mr::ScreenshotImportParser::parse(QImage(path), recognized[0][1].toByteArray(), path);
        QCOMPARE(rows.size(), 1);
        QCOMPARE(rows[0].toMap().value("duty_level").toInt(), 50);
        QVERIFY(!rows[0].toMap().value("warnings").toStringList().join(QLatin1Char(' ')).contains(QStringLiteral("等级前缀")));
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
