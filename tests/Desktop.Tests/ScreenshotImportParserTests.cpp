#include "ScreenshotImportParser.h"
#include "JobCatalog.h"
#include "JobIconClassifier.h"

#include <QColor>
#include <QDir>
#include <QFile>
#include <QGuiApplication>
#include <QPainter>
#include <QScopeGuard>
#include <QSet>
#include <QTemporaryDir>
#include <QTest>
#include <QVariantMap>

namespace {

const QByteArray kTsvHeader = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n";

/** @brief 创建不含真实用户正文的 TSV；block 用于模拟 sparse OCR 把同一行切成多块。 */
QByteArray word(int line, int left, int top, int width, int height, const QString &text,
                double confidence = 96, int block = 1, int position = 1)
{
    return QByteArray("5\t1\t") + QByteArray::number(block) + "\t1\t"
        + QByteArray::number(line) + '\t' + QByteArray::number(position) + '\t'
        + QByteArray::number(left) + '\t' + QByteArray::number(top) + '\t'
        + QByteArray::number(width) + '\t' + QByteArray::number(height) + '\t'
        + QByteArray::number(confidence) + '\t' + text.toUtf8() + '\n';
}

QImage screenshot(int height = 800)
{
    QImage image(600, height, QImage::Format_ARGB32);
    image.fill(Qt::white);
    return image;
}

QByteArray card(int top, int line = 1, const QString &time = QStringLiteral("2026-10-01 12:34:56"))
{
    return word(line, 150, top, 280, 20, QStringLiteral("Lv.50 合成副本"))
        + word(line + 1, 150, top + 40, 260, 20, QStringLiteral("合成心得第一行"))
        + word(line + 2, 150, top + 65, 260, 20, QStringLiteral("合成心得第二行"))
        + word(line + 3, 380, top + 100, 190, 15, time);
}

bool hasWarning(const QVariantMap &row, const QString &part)
{
    for (const QString &warning : row.value(QStringLiteral("warnings")).toStringList()) {
        if (warning.contains(part))
            return true;
    }
    return false;
}

/**
 * @brief dlog 网页记录列表的合成画面：白底上的深绿圆角行，行可以伸出截图右缘。
 * antialiased 时另画一圈浅绿阴影，圆角混色与真实页面一样会通过金色笔画判定。
 */
QImage webPage(int width, int height, const QList<QRect> &rows, bool antialiased = false)
{
    QImage image(width, height, QImage::Format_ARGB32);
    image.fill(Qt::white);
    QPainter painter(&image);
    painter.setRenderHint(QPainter::Antialiasing, antialiased);
    painter.setPen(Qt::NoPen);
    if (antialiased) {
        painter.setBrush(QColor(215, 228, 208));
        for (const QRect &row : rows)
            painter.drawRoundedRect(QRectF(row).adjusted(-2.5, -2.5, 2.5, 2.5), 16, 16);
    }
    painter.setBrush(QColor(45, 69, 38));
    for (const QRect &row : rows)
        painter.drawRoundedRect(row, 14, 14);
    return image;
}

/** @brief 把文字框画成实心浅色字块（最不利于底色取样的情形），并返回对应 TSV 单词。 */
QByteArray inked(QImage &image, const QRect &box, const QString &text, int block, int position = 1)
{
    QPainter painter(&image);
    painter.fillRect(box, QColor(225, 232, 224));
    return word(1, box.x(), box.y(), box.width(), box.height(), text, 96, block, position);
}

/** @brief 网页删除按钮：两条白色细对角线，与样例截图一样只有笔画中心是纯白。 */
void paintDeleteMark(QImage &image, const QRect &box, double pen = 1.6)
{
    QPainter painter(&image);
    painter.setRenderHint(QPainter::Antialiasing);
    painter.setPen(QPen(QColor(235, 240, 235), pen));
    painter.drawLine(box.topLeft(), box.bottomRight());
    painter.drawLine(box.bottomLeft(), box.topRight());
}

/** @brief 副本类型图标：彩色方块中间带深色图案，与网页上的蓝、青、红色图标同类。 */
void paintTypeIcon(QImage &image, const QRect &box, const QColor &colour)
{
    QPainter painter(&image);
    painter.fillRect(box, colour);
    painter.fillRect(box.adjusted(box.width() / 3, box.height() / 3, -box.width() / 3, -box.height() / 3),
                     QColor(30, 40, 60));
}

} // namespace

class ScreenshotImportParserTests final : public QObject
{
    Q_OBJECT

private slots:
    void separatesCardsAndPreservesLineOrder();
    void sparseBlocksOnTheSameRowFormOneTitleAndTimestamp();
    void mobileAdjacentDateAndTimeBoxesKeepTheSourceTimestamp_data();
    void mobileAdjacentDateAndTimeBoxesKeepTheSourceTimestamp();
    void timestampAndCardBorderKeepAMissedTitleSeparate();
    void blueDutyIconIsNotTitleText();
    void ignoresDeleteAndPagingAndDoesNotInventRunFacts();
    void clippedCardStaysVisibleForCorrection();
    void invalidDateAndLowConfidenceRequireCorrection();
    void boundedInputsRejectWholeBatch();
    void candidateLimitRejectsWholeBatch();
    void localTemplateMatchRemainsReviewable();
    void differentGoldArtworkRemainsUnknown();
    void jobTemplatesExcludeBaseCraftingAndLimitedJobs();
    void missingLevelPrefixRequiresCardAndBlueIconAtTheTop();
    void dutyCandidatesRequireUniqueSameLevelChineseMatches();
    void webRowsSplitLevelDutyTimeAndNoteIntoColumns();
    void webRowColumnsSpanWrappedLines();
    void webRowAutoWrapDoesNotBecomeANoteLineBreak_data();
    void webRowAutoWrapDoesNotBecomeANoteLineBreak();
    void webRowAutoWrapKeepsPhysicalOrderAcrossFragments();
    void webRowColumnsFollowVisualLinesNotOcrLineNumbers();
    void webRowDeleteMarkIsNeverNoteText();
    void webRowJobIconLeftOfLevelIsMatched_data();
    void webRowJobIconLeftOfLevelIsMatched();
    void rowFieldsBoundEachColumnOfAWebRow();
    void mobileCardWithCloseFooterIsNotAWebRow();
    void webRowsTouchingTheTopOrBottomEdgeAreKept();
    void webRowWithMisreadDateOrLevelIsKept();
    void webRowTypeIconIgnoresColourNoise();
    void webRowIconWordIsExcludedByComponentOrByRatio();
    void webRowDeleteMarkFusedWithTheLastGlyphIsStripped();
    void webRowDeleteWordNearTheRowEndOnANarrowPage();
    void rowFieldsLeaveColumnsWithoutRoomEmpty();
    void webRowNoteLinesStaySeparateUnderATallOcrBox();
    void mobileCardWithFooterRightUnderTheTitleIsNotAWebRow();
    void mobileBodyLineStartingWithLevelAndDateIsNotAWebRow();
    void mobileWatermarkLookingLikeALevelIsNotText();
    void webRowWithANoteCoveringEverySampleColumnIsKept();
    void webRowDutyLeftOfTheWatermarkCutOffIsKept();
    void webRowDeleteMarkIsFoundByPixelsEvenWhenTouchingTheNote();
    void webRowCrossReadAsAGlyphAtTheRowEndIsNotNoteText();
    void webRowEndGlyphThatIsNotACrossStaysNoteText();
    void webRowThinAntialiasedDeleteMarkIsFound();
    void webRowLatinXIsStrippedOnlyWithAButtonBehindIt();
    void webRowLiteralCrossBesideAPixelLocatedButtonIsPreserved_data();
    void webRowLiteralCrossBesideAPixelLocatedButtonIsPreserved();
    void fragmentedAsciiAndPunctuationKeepSourceSpacing_data();
    void fragmentedAsciiAndPunctuationKeepSourceSpacing();
    void webRowExtentIgnoresANoteLineAboveTheLevel();
    void webRowCroppedThroughTheJobIconUsesTheStripRightOfTheLevel();
    void webRowLevelTokenToleratesFurtherMisreads();
    void neuralTextBoxesAndElevatedTypeIconDoNotSplitWebRows();
    void unrecognisedWideGoldStrokeDoesNotTrimWebRow();
};

void ScreenshotImportParserTests::separatesCardsAndPreservesLineOrder()
{
    QString error;
    // TSV order intentionally differs from screen order.
    const QByteArray tsv = kTsvHeader + card(350, 10, QStringLiteral("2026-10-01 12:30:00"))
        + card(100);
    const QVariantList rows = mr::ScreenshotImportParser::parse(screenshot(), tsv,
                                                               QStringLiteral("C:/synthetic.png"), &error);
    QVERIFY2(error.isEmpty(), qPrintable(error));
    QCOMPARE(rows.size(), 2);
    const QVariantMap first = rows[0].toMap();
    const QVariantMap second = rows[1].toMap();
    QCOMPARE(first.value(QStringLiteral("duty_name")).toString(), QStringLiteral("合成副本"));
    QCOMPARE(first.value(QStringLiteral("duty_level")).toInt(), 50);
    QCOMPARE(first.value(QStringLiteral("reflection_text")).toString(), QStringLiteral("合成心得第一行\n合成心得第二行"));
    QCOMPARE(first.value(QStringLiteral("source_recorded_at")).toString(), QStringLiteral("2026-10-01 12:34:56"));
    QCOMPARE(second.value(QStringLiteral("source_recorded_at")).toString(), QStringLiteral("2026-10-01 12:30:00"));
    QCOMPARE(first.value(QStringLiteral("source_image")).toString(), QStringLiteral("C:/synthetic.png"));
    QVERIFY(first.value(QStringLiteral("needs_review")).toBool());
    const QVariantMap rect = first.value(QStringLiteral("source_rect")).toMap();
    QVERIFY(rect.value(QStringLiteral("y")).toInt() <= 100);
    QVERIFY(rect.value(QStringLiteral("y")).toInt() + rect.value(QStringLiteral("height")).toInt() > 215);
    QVERIFY(rect.value(QStringLiteral("y")).toInt() + rect.value(QStringLiteral("height")).toInt() < 350);
}

void ScreenshotImportParserTests::sparseBlocksOnTheSameRowFormOneTitleAndTimestamp()
{
    const QByteArray tsv = kTsvHeader
        + word(1, 150, 100, 35, 20, QStringLiteral("Lv."), 95, 1)
        + word(1, 185, 102, 30, 20, QStringLiteral("50"), 95, 2)
        + word(1, 250, 101, 220, 20, QStringLiteral("合成 副本"), 95, 3)
        + word(1, 150, 140, 200, 20, QStringLiteral("合成正文"), 95, 4)
        + word(1, 350, 200, 105, 15, QStringLiteral("2026-10-01"), 95, 5)
        + word(1, 462, 201, 90, 15, QStringLiteral("12:34:56"), 95, 6);
    const QVariantList rows = mr::ScreenshotImportParser::parse(screenshot(), tsv, {});
    QCOMPARE(rows.size(), 1);
    QCOMPARE(rows[0].toMap().value(QStringLiteral("duty_name")).toString(), QStringLiteral("合成 副本"));
    QCOMPARE(rows[0].toMap().value(QStringLiteral("source_recorded_at")).toString(), QStringLiteral("2026-10-01 12:34:56"));
}

void ScreenshotImportParserTests::mobileAdjacentDateAndTimeBoxesKeepTheSourceTimestamp_data()
{
    QTest::addColumn<int>("gap");
    QTest::addColumn<QString>("date");
    QTest::addColumn<QString>("time");
    QTest::newRow("touching-boxes") << 0 << QStringLiteral("2026-10-01") << QStringLiteral("12:34:56");
    QTest::newRow("overlapping-boxes") << -4 << QStringLiteral("2026-10-01") << QStringLiteral("12:34:56");
    QTest::newRow("small-gap-boxes") << 2 << QStringLiteral("2026-10-01") << QStringLiteral("12:34:56");
    QTest::newRow("fullwidth-timestamp-separators") << 0 << QStringLiteral("2026－10－01") << QStringLiteral("12：34：56");
}

void ScreenshotImportParserTests::mobileAdjacentDateAndTimeBoxesKeepTheSourceTimestamp()
{
    QFETCH(int, gap);
    QFETCH(QString, date);
    QFETCH(QString, time);
    // 检测框的外扩可能吞掉原图中的日期/时间间距；完整字段仍须保留结构分隔。
    // 同张卡片里拆成相邻框的数字颜文字则必须继续合成 030，不能普遍补空格。
    const QByteArray tsv = kTsvHeader
        + word(1, 150, 100, 280, 20, QStringLiteral("Lv.50 合成副本"), 96, 1)
        + word(2, 150, 140, 10, 20, QStringLiteral("0"), 96, 2)
        + word(2, 160, 140, 20, 20, QStringLiteral("30"), 96, 3)
        + word(3, 350, 200, 105, 15, date, 96, 4)
        + word(3, 455 + gap, 201, 90, 15, time, 96, 5);
    const QVariantList rows = mr::ScreenshotImportParser::parse(screenshot(300), tsv, {});
    QCOMPARE(rows.size(), 1);
    const QVariantMap row = rows[0].toMap();
    QCOMPARE(row.value(QStringLiteral("source_recorded_at")).toString(), QStringLiteral("2026-10-01 12:34:56"));
    QCOMPARE(row.value(QStringLiteral("reflection_text")).toString(), QStringLiteral("030"));
}

void ScreenshotImportParserTests::ignoresDeleteAndPagingAndDoesNotInventRunFacts()
{
    const QByteArray tsv = kTsvHeader
        + word(1, 60, 30, 180, 20, QStringLiteral("返回首页"))
        + word(2, 150, 100, 280, 20, QStringLiteral("Lv.50 合成副本"))
        + word(3, 150, 140, 340, 20, QStringLiteral("合成正文"))
        + word(3, 530, 140, 20, 20, QStringLiteral("X"), 99, 1, 2)
        + word(4, 380, 200, 190, 15, QStringLiteral("2026-10-01 12:34:56"))
        + word(5, 60, 240, 350, 20, QStringLiteral("共719条记录 10/page 1 of 72"))
        + word(6, 180, 740, 350, 20, QStringLiteral("dlog.luyulight.cn"));
    const QVariantList rows = mr::ScreenshotImportParser::parse(screenshot(), tsv, {});
    QCOMPARE(rows.size(), 1);
    const QVariantMap row = rows[0].toMap();
    QCOMPARE(row.value(QStringLiteral("reflection_text")).toString(), QStringLiteral("合成正文"));
    QVERIFY(!row.contains(QStringLiteral("result")));
    QVERIFY(!row.contains(QStringLiteral("duration_seconds")));
    QVERIFY(!row.contains(QStringLiteral("entered_at_utc")));
    QVERIFY(!row.contains(QStringLiteral("ended_at_utc")));
    QVERIFY(!row.contains(QStringLiteral("confidence")));
    QVERIFY(row.value(QStringLiteral("job_id")).isNull());
    QVERIFY(hasWarning(row, QStringLiteral("删除按钮")));
    QVERIFY(hasWarning(row, QStringLiteral("时区")));
}

void ScreenshotImportParserTests::timestampAndCardBorderKeepAMissedTitleSeparate()
{
    QImage image = screenshot();
    {
        QPainter painter(&image);
        painter.fillRect(QRect(30, 70, 540, 180), QColor(85, 45, 45));
        painter.fillRect(QRect(30, 320, 540, 180), QColor(85, 45, 45));
    }
    const QByteArray tsv = kTsvHeader + card(100)
        + word(10, 150, 350, 300, 20, QStringLiteral("unrecognised header"), 0)
        + word(11, 150, 390, 260, 20, QStringLiteral("合成第二卡正文"))
        + word(12, 380, 450, 190, 15, QStringLiteral("2026-10-01 12:30:00"));
    const QVariantList rows = mr::ScreenshotImportParser::parse(image, tsv, {});
    QCOMPARE(rows.size(), 2);
    QCOMPARE(rows[0].toMap().value(QStringLiteral("source_recorded_at")).toString(), QStringLiteral("2026-10-01 12:34:56"));
    const QVariantMap missed = rows[1].toMap();
    QVERIFY(missed.value(QStringLiteral("duty_name")).toString().isEmpty());
    QVERIFY(missed.value(QStringLiteral("duty_level")).isNull());
    QCOMPARE(missed.value(QStringLiteral("reflection_text")).toString(), QStringLiteral("合成第二卡正文"));
    QCOMPARE(missed.value(QStringLiteral("source_recorded_at")).toString(), QStringLiteral("2026-10-01 12:30:00"));
    QVERIFY(hasWarning(missed, QStringLiteral("标题识别失败")));
}

void ScreenshotImportParserTests::blueDutyIconIsNotTitleText()
{
    QImage image = screenshot();
    {
        QPainter painter(&image);
        painter.fillRect(QRect(225, 98, 30, 25), QColor(15, 180, 230));
    }
    const QByteArray tsv = kTsvHeader
        + word(1, 150, 100, 65, 20, QStringLiteral("Lv.50"))
        + word(1, 225, 100, 30, 20, QStringLiteral("(A)"), 40, 1, 2)
        + word(1, 270, 100, 200, 20, QStringLiteral("合成副本"), 96, 1, 3)
        + word(2, 150, 140, 260, 20, QStringLiteral("合成正文"))
        + word(3, 380, 200, 190, 15, QStringLiteral("2026-10-01 12:34:56"));
    const QVariantList rows = mr::ScreenshotImportParser::parse(image, tsv, {});
    QCOMPARE(rows.size(), 1);
    QCOMPARE(rows[0].toMap().value(QStringLiteral("duty_name")).toString(), QStringLiteral("合成副本"));
}

void ScreenshotImportParserTests::clippedCardStaysVisibleForCorrection()
{
    const QByteArray tsv = kTsvHeader + card(100)
        + word(10, 150, 640, 280, 20, QStringLiteral("Lv.59 合成裁切副本"))
        + word(11, 150, 690, 350, 20, QStringLiteral("10/page 1 of 72"))
        + word(12, 180, 740, 350, 20, QStringLiteral("dlog.luyulight.cn"));
    const QVariantList rows = mr::ScreenshotImportParser::parse(screenshot(), tsv, {});
    QCOMPARE(rows.size(), 2);
    const QVariantMap row = rows[1].toMap();
    QCOMPARE(row.value(QStringLiteral("duty_name")).toString(), QStringLiteral("合成裁切副本"));
    QVERIFY(row.value(QStringLiteral("reflection_text")).toString().isEmpty());
    QVERIFY(row.value(QStringLiteral("source_recorded_at")).toString().isEmpty());
    QVERIFY(row.value(QStringLiteral("needs_review")).toBool());
    QVERIFY(hasWarning(row, QStringLiteral("裁切")));
}

void ScreenshotImportParserTests::invalidDateAndLowConfidenceRequireCorrection()
{
    const QByteArray tsv = kTsvHeader
        + word(1, 150, 100, 280, 20, QStringLiteral("Lv.50 合成副本"), 30)
        + word(2, 150, 140, 260, 20, QStringLiteral("合成正文"), 20)
        + word(3, 380, 200, 190, 15, QStringLiteral("2026-02-30 25:61:00"), 20);
    const QVariantList rows = mr::ScreenshotImportParser::parse(screenshot(), tsv, {});
    QCOMPARE(rows.size(), 1);
    const QVariantMap row = rows[0].toMap();
    QCOMPARE(row.value(QStringLiteral("source_recorded_at")).toString(), QStringLiteral("2026-02-30 25:61:00"));
    QVERIFY(hasWarning(row, QStringLiteral("时间无效")));
    QVERIFY(hasWarning(row, QStringLiteral("把握较低")));
    QVERIFY(row.value(QStringLiteral("ocr_confidence")).toDouble() < 0.3);
}

void ScreenshotImportParserTests::boundedInputsRejectWholeBatch()
{
    QString error;
    const QImage image = screenshot();
    QVERIFY(mr::ScreenshotImportParser::parse({}, kTsvHeader + card(100), {}, &error).isEmpty());
    QVERIFY(!error.isEmpty());
    // Mono storage keeps this 50,010,000-pixel limit test small in memory.
    const QImage tooLarge(10'000, 5001, QImage::Format_Mono);
    QVERIFY(!tooLarge.isNull());
    QVERIFY(mr::ScreenshotImportParser::parse(tooLarge, kTsvHeader + card(100), {}, &error).isEmpty());
    QVERIFY(error.contains(QStringLiteral("5000")));
    QVERIFY(mr::ScreenshotImportParser::parse(image, QByteArray(mr::ScreenshotImportParser::MaxTsvBytes + 1, 'x'), {}, &error).isEmpty());
    QVERIFY(!error.isEmpty());
    QVERIFY(mr::ScreenshotImportParser::parse(image, QByteArray("not-tsv"), {}, &error).isEmpty());
    QVERIFY(!error.isEmpty());
    QVERIFY(mr::ScreenshotImportParser::parse(image, kTsvHeader + card(100)
                                               + word(10, 590, 600, 40, 20, QStringLiteral("越界")), {}, &error).isEmpty());
    QVERIFY(error.contains(QStringLiteral("坐标")));
    QVERIFY(mr::ScreenshotImportParser::parse(image, kTsvHeader + card(100)
                                               + word(10, 150, 600, 40, 20, QStringLiteral("非法分值"), 101), {}, &error).isEmpty());
    QVERIFY(error.contains(QStringLiteral("置信度")));
    QVERIFY(mr::ScreenshotImportParser::parse(image, kTsvHeader + word(1, 150, 100, 200, 20, QStringLiteral("无卡片正文")), {}, &error).isEmpty());
    QVERIFY(error.contains(QStringLiteral("未找到")));
}

void ScreenshotImportParserTests::candidateLimitRejectsWholeBatch()
{
    QImage image(600, 50'000, QImage::Format_RGB32);
    image.fill(Qt::white);
    QByteArray tsv = kTsvHeader;
    for (int i = 0; i <= mr::ScreenshotImportParser::MaxCandidates; ++i)
        tsv += word(i + 1, 150, 10 + i * 40, 280, 20, QStringLiteral("Lv.50 合成副本"));
    QString error;
    QVERIFY(mr::ScreenshotImportParser::parse(image, tsv, {}, &error).isEmpty());
    QVERIFY(error.contains(QStringLiteral("1000")));
}

void ScreenshotImportParserTests::localTemplateMatchRemainsReviewable()
{
    // Public builds exercise local template fallback with a synthetic glyph;
    // personal classifier builds reuse the corresponding existing icon resource.
    if (!QFile::exists(QStringLiteral(":/resources/icons/manifest.json")))
        QSKIP("The host test target must link the existing mr_icons resource.");
    QTemporaryDir localData;
    QVERIFY(localData.isValid());
    const QByteArray previousLocalData = qgetenv("LOCALAPPDATA");
    const bool wasSet = qEnvironmentVariableIsSet("LOCALAPPDATA");
    const auto restore = qScopeGuard([&] {
        if (wasSet)
            qputenv("LOCALAPPDATA", previousLocalData);
        else
            qunsetenv("LOCALAPPDATA");
    });
    qputenv("LOCALAPPDATA", localData.path().toUtf8());
    QVERIFY(QDir().mkpath(localData.path() + QStringLiteral("/MentorRecorder/icons/jobs")));
    QImage icon(56, 56, QImage::Format_ARGB32);
    icon.fill(Qt::transparent);
    {
        QPainter painter(&icon);
        painter.fillRect(QRect(8, 8, 8, 40), QColor(220, 185, 75));
        painter.fillRect(QRect(8, 40, 40, 8), QColor(220, 185, 75));
        painter.fillRect(QRect(40, 20, 8, 28), QColor(220, 185, 75));
        painter.fillRect(QRect(24, 8, 8, 12), QColor(220, 185, 75));
    }
    if (mr::JobIconClassifier().isAvailable()) {
        icon = QImage(QStringLiteral(":/resources/icons/jobs/19.png"));
        QVERIFY(!icon.isNull());
    }
    QVERIFY(icon.save(localData.path() + QStringLiteral("/MentorRecorder/icons/jobs/19.png")));
    QImage image = screenshot();
    {
        QPainter painter(&image);
        painter.fillRect(QRect(30, 70, 540, 180), QColor(85, 45, 45));
        painter.drawImage(QRect(50, 110, 72, 72), icon);
    }
    const QVariantList rows = mr::ScreenshotImportParser::parse(image, kTsvHeader + card(100), {});
    QCOMPARE(rows.size(), 1);
    const QVariantMap row = rows[0].toMap();
    QCOMPARE(row.value(QStringLiteral("job_id")).toInt(), 19);
    QCOMPARE(row.value(QStringLiteral("job_candidate_id")).toInt(), 19);
    QVERIFY(!row.value(QStringLiteral("job_candidate_name")).toString().isEmpty());
    QVERIFY(row.value(QStringLiteral("icon_confidence")).toDouble() >= 0.82);
    QVERIFY(row.value(QStringLiteral("needs_review")).toBool());
    const QVariantMap rect = row.value(QStringLiteral("source_rect")).toMap();
    QVERIFY(rect.value(QStringLiteral("y")).toInt() >= 65);
    QVERIFY(rect.value(QStringLiteral("y")).toInt() <= 75);
    QVERIFY(rect.value(QStringLiteral("height")).toInt() >= 170);
}

void ScreenshotImportParserTests::differentGoldArtworkRemainsUnknown()
{
    QImage image = screenshot();
    {
        QPainter painter(&image);
        painter.fillRect(QRect(30, 70, 540, 180), QColor(85, 45, 45));
        // A solid rectangle is intentionally unlike a job glyph.
        painter.fillRect(QRect(50, 110, 72, 72), QColor(220, 185, 75));
    }
    const QVariantList rows = mr::ScreenshotImportParser::parse(image, kTsvHeader + card(100), {});
    QCOMPARE(rows.size(), 1);
    QVERIFY(rows[0].toMap().value(QStringLiteral("job_id")).isNull());
    // 已知输出低分时可以给出待核对候选，但不能变成可靠职业。
    // 无候选（包括未打包游戏图标的构建）保留未知，不强制制造建议。
    if (!rows[0].toMap().value(QStringLiteral("job_candidate_id")).isNull()) {
        QVERIFY(rows[0].toMap().value(QStringLiteral("needs_review")).toBool());
        QVERIFY(!rows[0].toMap().value(QStringLiteral("job_candidate_name")).toString().isEmpty());
    } else {
        QVERIFY(rows[0].toMap().value(QStringLiteral("job_candidate_name")).toString().isEmpty());
    }
    QVERIFY(rows[0].toMap().value(QStringLiteral("icon_runner_up_confidence")).toDouble()
            <= rows[0].toMap().value(QStringLiteral("icon_confidence")).toDouble());
    QVERIFY(hasWarning(rows[0].toMap(), QStringLiteral("职业图标")));
}

void ScreenshotImportParserTests::jobTemplatesExcludeBaseCraftingAndLimitedJobs()
{
    if (!QFile::exists(QStringLiteral(":/resources/icons/manifest.json")))
        QSKIP("The host test target must link the existing mr_icons resource.");
    QTemporaryDir localData;
    QVERIFY(localData.isValid());
    const QByteArray previousLocalData = qgetenv("LOCALAPPDATA");
    const bool wasSet = qEnvironmentVariableIsSet("LOCALAPPDATA");
    const auto restore = qScopeGuard([&] {
        if (wasSet)
            qputenv("LOCALAPPDATA", previousLocalData);
        else
            qunsetenv("LOCALAPPDATA");
    });
    qputenv("LOCALAPPDATA", localData.path().toUtf8());
    QVERIFY(QDir().mkpath(localData.path() + QStringLiteral("/MentorRecorder/icons/jobs")));
    QImage icon(56, 56, QImage::Format_ARGB32);
    icon.fill(Qt::transparent);
    {
        QPainter painter(&icon);
        painter.fillRect(QRect(5, 5, 8, 45), QColor(220, 185, 75));
        painter.fillRect(QRect(5, 5, 45, 8), QColor(220, 185, 75));
        painter.fillRect(QRect(20, 25, 30, 8), QColor(220, 185, 75));
        painter.fillRect(QRect(42, 25, 8, 25), QColor(220, 185, 75));
    }
    // 即使非导随职业的本地图标与截图完全一致，也不能进入候选排名。
    for (int excluded : {1, 8, 36, 43})
        QVERIFY(icon.save(localData.path() + QStringLiteral("/MentorRecorder/icons/jobs/%1.png").arg(excluded)));
    QImage image = screenshot();
    {
        QPainter painter(&image);
        painter.fillRect(QRect(30, 70, 540, 180), QColor(85, 45, 45));
        painter.drawImage(QRect(50, 110, 72, 72), icon);
    }
    const QVariantList rows = mr::ScreenshotImportParser::parse(image, kTsvHeader + card(100), {});
    QCOMPARE(rows.size(), 1);
    QSet<int> eligible;
    for (const QVariant &job : mr::JobCatalog().battleJobs())
        eligible.insert(job.toMap().value(QStringLiteral("job_id")).toInt());
    const QVariantMap row = rows[0].toMap();
    for (const QString &key : {QStringLiteral("job_id"), QStringLiteral("job_candidate_id"), QStringLiteral("icon_runner_up_id")}) {
        const int id = row.value(key).toInt();
        QVERIFY2(id == 0 || eligible.contains(id), qPrintable(key));
        QVERIFY(id != 1 && id != 8 && id != 36 && id != 43);
    }
}

void ScreenshotImportParserTests::missingLevelPrefixRequiresCardAndBlueIconAtTheTop()
{
    const QByteArray missingTitle = kTsvHeader
        + word(1, 150, 100, 65, 20, QStringLiteral("v.55"))
        + word(1, 225, 98, 30, 25, QStringLiteral("(A)"), 40, 1, 2)
        + word(1, 270, 100, 200, 20, QStringLiteral("合成副本"), 96, 1, 3)
        + word(2, 150, 140, 260, 20, QStringLiteral("裁切正文"));
    QImage bounded = screenshot();
    {
        QPainter painter(&bounded);
        painter.fillRect(QRect(30, 70, 540, 180), QColor(85, 45, 45));
        painter.fillRect(QRect(225, 98, 30, 25), QColor(15, 180, 230));
    }
    const QVariantList rows = mr::ScreenshotImportParser::parse(bounded, missingTitle, {});
    QCOMPARE(rows.size(), 1);
    QCOMPARE(rows[0].toMap().value(QStringLiteral("duty_level")).toInt(), 55);
    QCOMPARE(rows[0].toMap().value(QStringLiteral("duty_name")).toString(), QStringLiteral("合成副本"));
    QVERIFY(rows[0].toMap().value(QStringLiteral("needs_review")).toBool());
    QVERIFY(hasWarning(rows[0].toMap(), QStringLiteral("等级前缀")));
    QVERIFY(rows[0].toMap().value(QStringLiteral("source_recorded_at")).toString().isEmpty());

    QImage noCard = screenshot();
    {
        QPainter painter(&noCard);
        painter.fillRect(QRect(225, 98, 30, 25), QColor(15, 180, 230));
    }
    QVERIFY(mr::ScreenshotImportParser::parse(noCard, missingTitle, {}).isEmpty());
    QImage noBlue = screenshot();
    {
        QPainter painter(&noBlue);
        painter.fillRect(QRect(30, 70, 540, 180), QColor(85, 45, 45));
    }
    QVERIFY(mr::ScreenshotImportParser::parse(noBlue, missingTitle, {}).isEmpty());

    // 正文即使有类似前缀和蓝色图形，远离卡片上沿也不能分裂出新记录。
    QImage bodyImage = screenshot();
    {
        QPainter painter(&bodyImage);
        painter.fillRect(QRect(30, 70, 540, 250), QColor(85, 45, 45));
        painter.fillRect(QRect(225, 228, 30, 25), QColor(15, 180, 230));
    }
    const QByteArray bodyPrefix = kTsvHeader
        + word(1, 150, 100, 280, 20, QStringLiteral("Lv.55 合成副本"))
        + word(2, 150, 230, 65, 20, QStringLiteral("v.90"))
        + word(2, 225, 228, 30, 25, QStringLiteral("(A)"), 40, 1, 2)
        + word(2, 270, 230, 200, 20, QStringLiteral("正文版本"), 96, 1, 3);
    const QVariantList bodyRows = mr::ScreenshotImportParser::parse(bodyImage, bodyPrefix, {});
    QCOMPARE(bodyRows.size(), 1);
    QCOMPARE(bodyRows[0].toMap().value(QStringLiteral("duty_level")).toInt(), 55);
    QVERIFY(bodyRows[0].toMap().value(QStringLiteral("reflection_text")).toString().contains(QStringLiteral("v.90")));
}

void ScreenshotImportParserTests::dutyCandidatesRequireUniqueSameLevelChineseMatches()
{
    QVERIFY(QFile::exists(QStringLiteral(":/data/duties/cn.2026-09-04.json")));
    const auto parseTitle = [](const QString &name, int level, bool hasBlue = true, bool hasCard = true) {
        QImage image = screenshot();
        {
            QPainter painter(&image);
            if (hasCard)
                painter.fillRect(QRect(30, 70, 540, 180), QColor(85, 45, 45));
            if (hasBlue)
                painter.fillRect(QRect(225, 98, 30, 25), QColor(15, 180, 230));
        }
        const QByteArray tsv = kTsvHeader
            + word(1, 150, 100, 65, 20, QStringLiteral("Lv.%1").arg(level))
            + word(1, 225, 98, 30, 25, QStringLiteral("(A)"), 40, 1, 2)
            + word(1, 270, 100, 280, 20, name, 96, 1, 3)
            + word(2, 150, 140, 260, 20, QStringLiteral("天然要寒沙斯塔夏溶洞"));
        const QVariantList rows = mr::ScreenshotImportParser::parse(image, tsv, {});
        return rows.isEmpty() ? QVariantMap() : rows[0].toMap();
    };
    const QString canonical = QStringLiteral("天然要害沙斯塔夏溶洞");
    const QString misspelled = QStringLiteral("天然要寒沙斯塔夏溶洞");
    const QVariantMap unique = parseTitle(misspelled, 15);
    QCOMPARE(unique.value(QStringLiteral("duty_name")).toString(), canonical);
    QCOMPARE(unique.value(QStringLiteral("duty_candidate_name")).toString(), canonical);
    QCOMPARE(unique.value(QStringLiteral("ocr_duty_name")).toString(), misspelled);
    QVERIFY(unique.value(QStringLiteral("duty_candidate_pending")).toBool());
    QVERIFY(unique.value(QStringLiteral("needs_review")).toBool());
    QVERIFY(!unique.contains(QStringLiteral("content_id")));
    QVERIFY(!unique.contains(QStringLiteral("duty_category")));
    QVERIFY(hasWarning(unique, QStringLiteral("目录预填候选")));
    QCOMPARE(parseTitle(QStringLiteral("天然 要寒沙斯塔夏·溶同"), 15)
                 .value(QStringLiteral("duty_candidate_name")).toString(), canonical);

    const auto noCandidate = [&](const QString &name, int level, bool blue = true, bool surface = true) {
        const QVariantMap row = parseTitle(name, level, blue, surface);
        QVERIFY(!row.isEmpty());
        QVERIFY(row.value(QStringLiteral("duty_candidate_name")).toString().isEmpty());
        QVERIFY(!row.value(QStringLiteral("duty_candidate_pending")).toBool());
        // 无蓝图标时图标 OCR 文字仍按旧规则属于标题原文，不能被猜成目录事实。
        if (blue)
            QCOMPARE(row.value(QStringLiteral("duty_name")).toString(), name);
    };
    noCandidate(misspelled, 50); // 相似的逆转要害是 50 级，不能跨等级套用。
    noCandidate(QStringLiteral("天然要寒沙斯塔夏熔同"), 15); // 三个误字越界。
    noCandidate(QStringLiteral("伊弗利特歼某战"), 50); // 歼灭/歼殛两个同距候选。
    noCandidate(QStringLiteral("伊弗利特歼灭某"), 50); // 最近/次近仅差一个误字。
    noCandidate(QStringLiteral("泰坦歼某战"), 50); // 短文字不靠目录补齐。
    noCandidate(QStringLiteral("abcdefg"), 15); // 非中文 OCR 不近似改名。
    noCandidate(misspelled, 15, false);
    noCandidate(misspelled, 15, true, false);
    noCandidate(QStringLiteral("合成副本"), 15); // 正文内的相似副本字串不能改变标题。
}

void ScreenshotImportParserTests::webRowsSplitLevelDutyTimeAndNoteIntoColumns()
{
    // 1428 宽网页：等级在左侧 10%，类型图标为蓝、青、红三色或缺失，日期列随副本名长度移动。
    // 字块实心铺满文字框，行中线的底色取样不足时须改在文字带上方取样。
    QImage image = webPage(1428, 440, {QRect(7, 7, 1500, 93), QRect(7, 115, 1500, 93),
                                        QRect(7, 223, 1500, 93), QRect(7, 331, 1500, 93)});
    paintTypeIcon(image, QRect(222, 39, 30, 30), QColor(136, 245, 251));
    paintTypeIcon(image, QRect(222, 147, 30, 30), QColor(70, 172, 176));
    paintTypeIcon(image, QRect(222, 255, 30, 30), QColor(220, 70, 50));
    QByteArray tsv = kTsvHeader;
    tsv += inked(image, QRect(145, 47, 44, 15), QStringLiteral("Lv.90"), 1);
    tsv += inked(image, QRect(286, 44, 171, 20), QStringLiteral("最终幻想未世终迹"), 2);
    tsv += inked(image, QRect(484, 46, 103, 15), QStringLiteral("2024-09-21"), 3);
    tsv += inked(image, QRect(598, 46, 72, 15), QStringLiteral("13:18:11"), 4);
    tsv += inked(image, QRect(704, 43, 80, 20), QStringLiteral("无事发生"), 5);
    tsv += inked(image, QRect(790, 43, 190, 20), QStringLiteral("（除了中间导师发信息"), 6);
    // 等级被切成两个单词；青色图标被识别成一个方形汉字。
    tsv += inked(image, QRect(145, 155, 25, 15), QStringLiteral("Lv."), 7, 1);
    tsv += inked(image, QRect(171, 155, 18, 15), QStringLiteral("55"), 7, 2);
    tsv += word(1, 220, 145, 33, 34, QStringLiteral("包"), 69, 8);
    tsv += inked(image, QRect(286, 152, 167, 20), QStringLiteral("那龙王座龙巢神殿"), 9);
    tsv += inked(image, QRect(484, 154, 189, 15), QStringLiteral("2024-09-20 12:56:16"), 10);
    tsv += inked(image, QRect(704, 151, 220, 20), QStringLiteral("无事发生（除了t不认路"), 11);
    // 等级缺 L；红色图标的行同样按行底色锚定副本名称。
    tsv += inked(image, QRect(145, 263, 40, 15), QStringLiteral("v.24"), 12);
    tsv += inked(image, QRect(286, 260, 213, 20), QStringLiteral("监狱废墟托托·拉克干狱"), 13);
    tsv += inked(image, QRect(531, 262, 106, 15), QStringLiteral("2024-09-20"), 14);
    tsv += inked(image, QRect(645, 262, 74, 15), QStringLiteral("12:20:55"), 15);
    tsv += inked(image, QRect(750, 259, 100, 20), QStringLiteral("给我打困了"), 16);
    // 无类型图标、无心得的行。
    tsv += inked(image, QRect(145, 371, 44, 15), QStringLiteral("Lv.10"), 17);
    tsv += inked(image, QRect(286, 368, 140, 20), QStringLiteral("讨伐彷徨死灵！"), 18);
    tsv += inked(image, QRect(463, 370, 106, 15), QStringLiteral("2024-09-20"), 19);
    tsv += inked(image, QRect(577, 370, 75, 15), QStringLiteral("11:49:22"), 20);

    QString error;
    const QVariantList rows = mr::ScreenshotImportParser::parse(image, tsv, QStringLiteral("C:/web.png"), &error);
    QVERIFY2(error.isEmpty(), qPrintable(error));
    QCOMPARE(rows.size(), 4);
    const QList<int> tops{7, 115, 223, 331};
    for (int i = 0; i < rows.size(); ++i) {
        const QVariantMap row = rows[i].toMap();
        QVERIFY(row.value(QStringLiteral("needs_review")).toBool());
        QVERIFY(!hasWarning(row, QStringLiteral("标题识别失败")));
        QVERIFY(!hasWarning(row, QStringLiteral("副本名称未识别完整")));
        QCOMPARE(row.value(QStringLiteral("source_image")).toString(), QStringLiteral("C:/web.png"));
        const QVariantMap rect = row.value(QStringLiteral("source_rect")).toMap();
        QCOMPARE(rect.value(QStringLiteral("y")).toInt(), tops[i]);
        QCOMPARE(rect.value(QStringLiteral("height")).toInt(), 93);
        QVERIFY(rect.value(QStringLiteral("x")).toInt() <= 8);
        QVERIFY(rect.value(QStringLiteral("x")).toInt() + rect.value(QStringLiteral("width")).toInt() >= 1427);
        if (i < 3)
            QVERIFY(!hasWarning(row, QStringLiteral("未识别到心得正文")));
    }
    const QVariantMap first = rows[0].toMap();
    QCOMPARE(first.value(QStringLiteral("duty_level")).toInt(), 90);
    QCOMPARE(first.value(QStringLiteral("ocr_duty_name")).toString(), QStringLiteral("最终幻想未世终迹"));
    QCOMPARE(first.value(QStringLiteral("duty_name")).toString(), QStringLiteral("最终幻想末世终迹"));
    QVERIFY(first.value(QStringLiteral("duty_candidate_pending")).toBool());
    QCOMPARE(first.value(QStringLiteral("source_recorded_at")).toString(), QStringLiteral("2024-09-21 13:18:11"));
    QCOMPARE(first.value(QStringLiteral("reflection_text")).toString(), QStringLiteral("无事发生（除了中间导师发信息"));
    QVERIFY(first.value(QStringLiteral("ocr_confidence")).toDouble() > 0.9);

    const QVariantMap second = rows[1].toMap();
    QCOMPARE(second.value(QStringLiteral("duty_level")).toInt(), 55);
    QCOMPARE(second.value(QStringLiteral("ocr_duty_name")).toString(), QStringLiteral("那龙王座龙巢神殿"));
    QCOMPARE(second.value(QStringLiteral("duty_name")).toString(), QStringLiteral("邪龙王座龙巢神殿"));
    QCOMPARE(second.value(QStringLiteral("source_recorded_at")).toString(), QStringLiteral("2024-09-20 12:56:16"));
    QCOMPARE(second.value(QStringLiteral("reflection_text")).toString(), QStringLiteral("无事发生（除了t不认路"));

    const QVariantMap third = rows[2].toMap();
    QCOMPARE(third.value(QStringLiteral("duty_level")).toInt(), 24);
    QVERIFY(hasWarning(third, QStringLiteral("等级前缀")));
    QCOMPARE(third.value(QStringLiteral("duty_name")).toString(), QStringLiteral("监狱废墟托托·拉克千狱"));
    QVERIFY(third.value(QStringLiteral("duty_candidate_pending")).toBool());
    QCOMPARE(third.value(QStringLiteral("source_recorded_at")).toString(), QStringLiteral("2024-09-20 12:20:55"));
    QCOMPARE(third.value(QStringLiteral("reflection_text")).toString(), QStringLiteral("给我打困了"));

    const QVariantMap fourth = rows[3].toMap();
    QCOMPARE(fourth.value(QStringLiteral("duty_level")).toInt(), 10);
    QCOMPARE(fourth.value(QStringLiteral("duty_name")).toString(), QStringLiteral("讨伐彷徨死灵！"));
    QCOMPARE(fourth.value(QStringLiteral("source_recorded_at")).toString(), QStringLiteral("2024-09-20 11:49:22"));
    QVERIFY(fourth.value(QStringLiteral("reflection_text")).toString().isEmpty());
    QVERIFY(hasWarning(fourth, QStringLiteral("未识别到心得正文")));
}

void ScreenshotImportParserTests::webRowColumnsSpanWrappedLines()
{
    // 1848 宽网页：各列独立换行。第一行的副本名、日期/时间和心得各占两行，
    // 等级位于两行之间；第二行只有心得换行，等级、名称和时间居中。
    QImage image = webPage(1848, 330, {QRect(7, 20, 1900, 151), QRect(7, 190, 1900, 131)});
    paintTypeIcon(image, QRect(280, 75, 30, 30), QColor(220, 70, 50));
    paintTypeIcon(image, QRect(280, 240, 30, 30), QColor(136, 245, 251));
    const QByteArray tsv = kTsvHeader
        + word(1, 145, 83, 44, 15, QStringLiteral("Lv.50"), 96, 1)
        + word(1, 330, 60, 126, 20, QStringLiteral("究极神兵假想"), 96, 2)
        + word(1, 330, 100, 42, 20, QStringLiteral("作战"), 96, 3)
        + word(1, 600, 62, 106, 15, QStringLiteral("2024-09-11"), 96, 4)
        + word(1, 600, 102, 74, 15, QStringLiteral("20:22:36"), 96, 5)
        + word(1, 760, 60, 100, 20, QStringLiteral("第一行心得"), 96, 6)
        + word(1, 760, 100, 100, 20, QStringLiteral("第二行心得"), 96, 7)
        + word(1, 145, 248, 44, 15, QStringLiteral("Lv.83"), 96, 8)
        + word(1, 330, 245, 147, 20, QStringLiteral("魔导神门巴别塔"), 96, 9)
        + word(1, 520, 248, 106, 15, QStringLiteral("2024-09-19"), 96, 10)
        + word(1, 634, 248, 75, 15, QStringLiteral("19:54:54"), 96, 11)
        + word(1, 760, 222, 80, 20, QStringLiteral("又是这本"), 96, 12)
        + word(1, 760, 268, 60, 20, QStringLiteral("第二行"), 96, 13);
    QString error;
    const QVariantList rows = mr::ScreenshotImportParser::parse(image, tsv, {}, &error);
    QVERIFY2(error.isEmpty(), qPrintable(error));
    QCOMPARE(rows.size(), 2);
    const QVariantMap wrapped = rows[0].toMap();
    QCOMPARE(wrapped.value(QStringLiteral("duty_level")).toInt(), 50);
    QCOMPARE(wrapped.value(QStringLiteral("duty_name")).toString(), QStringLiteral("究极神兵假想作战"));
    QCOMPARE(wrapped.value(QStringLiteral("source_recorded_at")).toString(), QStringLiteral("2024-09-11 20:22:36"));
    QCOMPARE(wrapped.value(QStringLiteral("reflection_text")).toString(), QStringLiteral("第一行心得\n第二行心得"));
    QCOMPARE(wrapped.value(QStringLiteral("source_rect")).toMap().value(QStringLiteral("y")).toInt(), 20);
    QCOMPARE(wrapped.value(QStringLiteral("source_rect")).toMap().value(QStringLiteral("height")).toInt(), 151);
    const QVariantMap noteWraps = rows[1].toMap();
    QCOMPARE(noteWraps.value(QStringLiteral("duty_level")).toInt(), 83);
    QCOMPARE(noteWraps.value(QStringLiteral("duty_name")).toString(), QStringLiteral("魔导神门巴别塔"));
    QCOMPARE(noteWraps.value(QStringLiteral("source_recorded_at")).toString(), QStringLiteral("2024-09-19 19:54:54"));
    QCOMPARE(noteWraps.value(QStringLiteral("reflection_text")).toString(), QStringLiteral("又是这本\n第二行"));
    for (const QVariant &value : rows) {
        QVERIFY(!hasWarning(value.toMap(), QStringLiteral("未识别到心得正文")));
        QVERIFY(!hasWarning(value.toMap(), QStringLiteral("标题识别失败")));
        QVERIFY(!hasWarning(value.toMap(), QStringLiteral("多个时间")));
    }
}

void ScreenshotImportParserTests::webRowAutoWrapDoesNotBecomeANoteLineBreak_data()
{
    QTest::addColumn<QString>("first");
    QTest::addColumn<QString>("second");
    QTest::addColumn<int>("firstRight");
    QTest::addColumn<int>("secondLeft");
    QTest::addColumn<int>("secondTop");
    QTest::addColumn<QString>("expected");
    QTest::addColumn<bool>("joined");
    QTest::newRow("continuous-chinese")
        << QStringLiteral("合成心得这次记录已") << QStringLiteral("经完整填写。")
        << 1408 << 534 << 55 << QStringLiteral("合成心得这次记录已经完整填写。") << true;
    QTest::newRow("punctuation-and-kaomoji")
        << QStringLiteral("合成心得(°") << QStringLiteral("▽°)!!!")
        << 1408 << 534 << 55 << QStringLiteral("合成心得(°▽°)!!!") << true;
    QTest::newRow("short-line-stays-separate")
        << QStringLiteral("第一段合成心得。") << QStringLiteral("第二段合成心得。")
        << 900 << 534 << 55 << QStringLiteral("第一段合成心得。\n第二段合成心得。") << false;
    QTest::newRow("paragraph-gap-stays-separate")
        << QStringLiteral("第一段合成心得。") << QStringLiteral("第二段合成心得。")
        << 1408 << 534 << 91 << QStringLiteral("第一段合成心得。\n第二段合成心得。") << false;
    QTest::newRow("indented-paragraph-stays-separate")
        << QStringLiteral("第一段合成心得。") << QStringLiteral("第二段合成心得。")
        << 1408 << 582 << 55 << QStringLiteral("第一段合成心得。\n第二段合成心得。") << false;
    // 截图无法提供跨行英文/数字之间的原始空格证据，不自动连词或补空格。
    QTest::newRow("ascii-boundary-stays-reviewable")
        << QStringLiteral("synthetic") << QStringLiteral("sample")
        << 1408 << 534 << 55 << QStringLiteral("synthetic\nsample") << false;
    QTest::newRow("number-boundary-stays-reviewable")
        << QStringLiteral("合成编号123") << QStringLiteral("456")
        << 1408 << 534 << 55 << QStringLiteral("合成编号123\n456") << false;
}

void ScreenshotImportParserTests::webRowAutoWrapDoesNotBecomeANoteLineBreak()
{
    QFETCH(QString, first);
    QFETCH(QString, second);
    QFETCH(int, firstRight);
    QFETCH(int, secondLeft);
    QFETCH(int, secondTop);
    QFETCH(QString, expected);
    QFETCH(bool, joined);
    // 标题和时间各占两条物理行；心得沿列右缘折行，三个字段不能相互拼接。
    QImage image = webPage(1422, 160, {QRect(4, 15, 1414, 134)});
    const QByteArray tsv = kTsvHeader
        + word(1, 217, 28, 135, 21, QStringLiteral("最终决战天幕魔导"), 96, 1)
        + word(1, 375, 30, 88, 19, QStringLiteral("2026-10-01"), 96, 2)
        + word(1, 532, 28, firstRight - 532 + 1, 24, first, 96, 3)
        + word(1, 105, 42, 45, 21, QStringLiteral("Lv.50"), 96, 4)
        + word(1, 216, 55, 24, 24, QStringLiteral("城"), 96, 5)
        + word(1, 375, 56, 66, 19, QStringLiteral("12:34:56"), 96, 6)
        + word(1, secondLeft, secondTop, 86, 21, second, 96, 7);
    QString error;
    const QVariantList rows = mr::ScreenshotImportParser::parse(image, tsv, {}, &error);
    QVERIFY2(error.isEmpty(), qPrintable(error));
    QCOMPARE(rows.size(), 1);
    const QVariantMap row = rows[0].toMap();
    QCOMPARE(row.value(QStringLiteral("reflection_text")).toString(), expected);
    QCOMPARE(row.value(QStringLiteral("duty_name")).toString(), QStringLiteral("最终决战天幕魔导城"));
    QCOMPARE(row.value(QStringLiteral("source_recorded_at")).toString(), QStringLiteral("2026-10-01 12:34:56"));
    QCOMPARE(hasWarning(row, QStringLiteral("自动折行")), joined);
    QVERIFY(row.value(QStringLiteral("needs_review")).toBool());
}

void ScreenshotImportParserTests::webRowAutoWrapKeepsPhysicalOrderAcrossFragments()
{
    QImage image = webPage(1422, 160, {QRect(4, 15, 1414, 134)});
    // TSV 顺序与画面相反，第二物理行也被分成两个框；连续折行不能按框号或横坐标串错。
    const QByteArray tsv = kTsvHeader
        + word(1, 105, 42, 45, 21, QStringLiteral("Lv.50"), 96, 1)
        + word(1, 217, 40, 135, 21, QStringLiteral("合成副本标题"), 96, 2)
        + word(1, 375, 30, 88, 19, QStringLiteral("2026-10-01"), 96, 3)
        + word(1, 375, 56, 66, 19, QStringLiteral("12:34:56"), 96, 4)
        + word(1, 534, 82, 86, 21, QStringLiteral("第四段。"), 96, 5)
        + word(1, 608, 55, 801, 21, QStringLiteral("第三段"), 96, 6)
        + word(1, 534, 55, 65, 21, QStringLiteral("第二段"), 96, 7)
        + word(1, 532, 28, 877, 24, QStringLiteral("第一段"), 96, 8);
    const QVariantList rows = mr::ScreenshotImportParser::parse(image, tsv, {});
    QCOMPARE(rows.size(), 1);
    const QVariantMap row = rows[0].toMap();
    QCOMPARE(row.value(QStringLiteral("reflection_text")).toString(), QStringLiteral("第一段第二段第三段第四段。"));
    QVERIFY(hasWarning(row, QStringLiteral("自动折行")));
}

void ScreenshotImportParserTests::webRowColumnsFollowVisualLinesNotOcrLineNumbers()
{
    // 分区识别可能把两行文字或居中的删除叉号编进同一 OCR 行；列内顺序仍按画面上下行排列。
    QImage image = webPage(1848, 200, {QRect(7, 20, 1823, 151)});
    paintTypeIcon(image, QRect(280, 75, 30, 30), QColor(220, 70, 50));
    const QByteArray tsv = kTsvHeader
        + word(1, 145, 83, 44, 15, QStringLiteral("Lv.50"), 96, 1, 1)
        + word(1, 330, 100, 42, 20, QStringLiteral("作战"), 96, 1, 2)
        + word(1, 330, 60, 126, 20, QStringLiteral("究极神兵假想"), 96, 1, 3)
        + word(1, 600, 62, 106, 15, QStringLiteral("2024-09-11"), 96, 1, 4)
        + word(1, 600, 102, 74, 15, QStringLiteral("20:22:36"), 96, 1, 5)
        + word(1, 760, 100, 100, 20, QStringLiteral("第二行心得"), 96, 1, 6)
        + word(1, 760, 60, 1000, 20, QStringLiteral("第一行心得一直写到删除按钮旁边"), 96, 1, 7)
        + word(1, 1766, 70, 40, 40, QStringLiteral("×"), 90, 1, 8);
    const QVariantList rows = mr::ScreenshotImportParser::parse(image, tsv, {});
    QCOMPARE(rows.size(), 1);
    const QVariantMap row = rows[0].toMap();
    QCOMPARE(row.value(QStringLiteral("duty_level")).toInt(), 50);
    QCOMPARE(row.value(QStringLiteral("duty_name")).toString(), QStringLiteral("究极神兵假想作战"));
    QCOMPARE(row.value(QStringLiteral("source_recorded_at")).toString(), QStringLiteral("2024-09-11 20:22:36"));
    QCOMPARE(row.value(QStringLiteral("reflection_text")).toString(),
             QStringLiteral("第一行心得一直写到删除按钮旁边\n第二行心得"));
    QVERIFY(hasWarning(row, QStringLiteral("删除按钮")));
}

void ScreenshotImportParserTests::webRowDeleteMarkIsNeverNoteText()
{
    // 行尾白色叉号是删除按钮。心得紧贴叉号时提示遮挡；短心得不提示，叉号都不进入正文。
    QImage image = webPage(1848, 230, {QRect(7, 10, 1823, 93), QRect(7, 120, 1823, 93)});
    const QByteArray tsv = kTsvHeader
        + word(1, 145, 50, 44, 15, QStringLiteral("Lv.90"), 96, 1)
        + word(1, 330, 48, 140, 20, QStringLiteral("间歇灵泉哈姆岛"), 96, 2)
        + word(1, 520, 50, 106, 15, QStringLiteral("2024-09-19"), 96, 3)
        + word(1, 634, 50, 76, 15, QStringLiteral("23:05:12"), 96, 4)
        + word(1, 760, 48, 1020, 20, QStringLiteral("很长的心得一直写到删除按钮旁边我是天才"), 96, 5)
        + word(1, 1786, 48, 20, 20, QStringLiteral("×"), 90, 6)
        + word(1, 145, 160, 44, 15, QStringLiteral("Lv.90"), 96, 7)
        + word(1, 330, 158, 160, 20, QStringLiteral("雪山奥窟冥魂石洞"), 96, 8)
        + word(1, 520, 160, 106, 15, QStringLiteral("2024-09-20"), 96, 9)
        + word(1, 634, 160, 74, 15, QStringLiteral("12:40:33"), 96, 10)
        + word(1, 760, 158, 80, 20, QStringLiteral("无事发生"), 96, 11)
        + word(1, 1786, 158, 20, 20, QStringLiteral("×"), 90, 12);
    const QVariantList rows = mr::ScreenshotImportParser::parse(image, tsv, {});
    QCOMPARE(rows.size(), 2);
    const QVariantMap touching = rows[0].toMap();
    QCOMPARE(touching.value(QStringLiteral("reflection_text")).toString(),
             QStringLiteral("很长的心得一直写到删除按钮旁边我是天才"));
    QVERIFY(hasWarning(touching, QStringLiteral("删除按钮")));
    const QVariantMap apart = rows[1].toMap();
    QCOMPARE(apart.value(QStringLiteral("reflection_text")).toString(), QStringLiteral("无事发生"));
    QVERIFY(!hasWarning(apart, QStringLiteral("删除按钮")));
    for (const QVariant &value : rows) {
        QVERIFY(!value.toMap().value(QStringLiteral("reflection_text")).toString().contains(QStringLiteral("×")));
        QVERIFY(!hasWarning(value.toMap(), QStringLiteral("未识别到心得正文")));
        QCOMPARE(value.toMap().value(QStringLiteral("duty_level")).toInt(), 90);
    }
}

void ScreenshotImportParserTests::webRowJobIconLeftOfLevelIsMatched_data()
{
    QTest::addColumn<int>("width");
    QTest::newRow("1000") << 1000;
    QTest::newRow("1428") << 1428;
    QTest::newRow("1848") << 1848;
}

void ScreenshotImportParserTests::webRowJobIconLeftOfLevelIsMatched()
{
    // 金色职业图标位于行左端、等级左侧；圆角抗锯齿边缘的浅色像素不能并入图标。
    QFETCH(int, width);
    if (!QFile::exists(QStringLiteral(":/resources/icons/manifest.json")))
        QSKIP("The host test target must link the existing mr_icons resource.");
    QTemporaryDir localData;
    QVERIFY(localData.isValid());
    const QByteArray previousLocalData = qgetenv("LOCALAPPDATA");
    const bool wasSet = qEnvironmentVariableIsSet("LOCALAPPDATA");
    const auto restore = qScopeGuard([&] {
        if (wasSet)
            qputenv("LOCALAPPDATA", previousLocalData);
        else
            qunsetenv("LOCALAPPDATA");
    });
    qputenv("LOCALAPPDATA", localData.path().toUtf8());
    QVERIFY(QDir().mkpath(localData.path() + QStringLiteral("/MentorRecorder/icons/jobs")));
    QImage icon(56, 56, QImage::Format_ARGB32);
    icon.fill(Qt::transparent);
    {
        QPainter painter(&icon);
        painter.fillRect(QRect(8, 8, 8, 40), QColor(220, 185, 75));
        painter.fillRect(QRect(8, 40, 40, 8), QColor(220, 185, 75));
        painter.fillRect(QRect(40, 20, 8, 28), QColor(220, 185, 75));
        painter.fillRect(QRect(24, 8, 8, 12), QColor(220, 185, 75));
    }
    if (mr::JobIconClassifier().isAvailable()) {
        icon = QImage(QStringLiteral(":/resources/icons/jobs/19.png"));
        QVERIFY(!icon.isNull());
    }
    QVERIFY(icon.save(localData.path() + QStringLiteral("/MentorRecorder/icons/jobs/19.png")));
    QImage image = webPage(width, 120, {QRect(7, 7, width + 50, 93)}, true);
    {
        QPainter painter(&image);
        painter.drawImage(QRect(32, 18, 72, 72), icon);
    }
    const QByteArray tsv = kTsvHeader
        + word(1, 145, 47, 44, 15, QStringLiteral("Lv.90"), 96, 1)
        + word(1, 286, 44, 140, 20, QStringLiteral("间歇灵泉哈姆岛"), 96, 2)
        + word(1, 484, 46, 103, 15, QStringLiteral("2024-09-19"), 96, 3)
        + word(1, 598, 46, 72, 15, QStringLiteral("23:05:12"), 96, 4)
        + word(1, 704, 43, 80, 20, QStringLiteral("无事发生"), 96, 5);
    const QVariantList rows = mr::ScreenshotImportParser::parse(image, tsv, {});
    QCOMPARE(rows.size(), 1);
    const QVariantMap row = rows[0].toMap();
    QCOMPARE(row.value(QStringLiteral("duty_level")).toInt(), 90);
    QCOMPARE(row.value(QStringLiteral("job_candidate_id")).toInt(), 19);
    QCOMPARE(row.value(QStringLiteral("job_id")).toInt(), 19);
    QVERIFY(row.value(QStringLiteral("icon_confidence")).toDouble() >= 0.82);
    QVERIFY(row.value(QStringLiteral("needs_review")).toBool());
}

void ScreenshotImportParserTests::rowFieldsBoundEachColumnOfAWebRow()
{
    QImage image = webPage(1848, 200, {QRect(7, 20, 1823, 151)});
    paintTypeIcon(image, QRect(280, 75, 30, 30), QColor(220, 70, 50));
    const QByteArray tsv = kTsvHeader
        + word(1, 145, 83, 44, 15, QStringLiteral("Lv.50"), 96, 1)
        + word(1, 330, 60, 126, 20, QStringLiteral("究极神兵假想"), 96, 2)
        + word(1, 330, 100, 42, 20, QStringLiteral("作战"), 96, 3)
        + word(1, 600, 62, 106, 15, QStringLiteral("2024-09-11"), 96, 4)
        + word(1, 600, 102, 74, 15, QStringLiteral("20:22:36"), 96, 5)
        + word(1, 760, 60, 100, 20, QStringLiteral("第一行心得"), 96, 6)
        + word(1, 760, 100, 100, 20, QStringLiteral("第二行心得"), 96, 7)
        + word(1, 1790, 85, 20, 20, QStringLiteral("×"), 90, 8);
    const QList<mr::ScreenshotImportParser::RowGeometry> rows = mr::ScreenshotImportParser::rowFields(image, tsv);
    QCOMPARE(rows.size(), 1);
    const mr::ScreenshotImportParser::RowGeometry &row = rows[0];
    QCOMPARE(row.row.top(), 20);
    QCOMPARE(row.row.bottom(), 170);
    QVERIFY(row.row.left() <= 8);
    QCOMPARE(row.row.right(), 1829);
    QCOMPARE(row.level, QRect(145, 83, 44, 15));
    // 标题列：红色图标之后、日期列之前，覆盖两行副本名称。
    QVERIFY2(row.title.left() > 309 && row.title.left() <= 330, qPrintable(QString::number(row.title.left())));
    QVERIFY2(row.title.right() < 600, qPrintable(QString::number(row.title.right())));
    QVERIFY(row.title.top() <= 60 && row.title.bottom() >= 119);
    // 时间列只含日期和下一行的时间。
    QCOMPARE(row.time, QRect(600, 62, 106, 15).united(QRect(600, 102, 74, 15)));
    QVERIFY(row.stackedTime);
    // 心得列：时间列之后、删除叉号之前，覆盖两行心得。
    QVERIFY2(row.body.left() > 705 && row.body.left() <= 760, qPrintable(QString::number(row.body.left())));
    QVERIFY2(row.body.right() >= 859 && row.body.right() < 1790, qPrintable(QString::number(row.body.right())));
    QVERIFY(row.body.top() <= 60 && row.body.bottom() >= 119);

    QVERIFY(mr::ScreenshotImportParser::rowFields(screenshot(), kTsvHeader + card(100)).isEmpty());
    QVERIFY(mr::ScreenshotImportParser::rowFields(image, QByteArray("not-tsv")).isEmpty());
    QVERIFY(mr::ScreenshotImportParser::rowFields({}, tsv).isEmpty());
}

void ScreenshotImportParserTests::mobileCardWithCloseFooterIsNotAWebRow()
{
    // 手机卡片即使正文为空、页脚日期紧接标题，也不按网页单行记录拆列。
    QImage image = screenshot();
    {
        QPainter painter(&image);
        painter.fillRect(QRect(30, 70, 540, 120), QColor(85, 45, 45));
    }
    const QByteArray tsv = kTsvHeader
        + word(1, 150, 100, 65, 20, QStringLiteral("Lv.50"))
        + word(1, 225, 100, 200, 20, QStringLiteral("合成副本"), 96, 1, 2)
        + word(2, 380, 150, 190, 15, QStringLiteral("2026-10-01 12:34:56"));
    const QVariantList rows = mr::ScreenshotImportParser::parse(image, tsv, {});
    QCOMPARE(rows.size(), 1);
    QCOMPARE(rows[0].toMap().value(QStringLiteral("duty_name")).toString(), QStringLiteral("合成副本"));
    QCOMPARE(rows[0].toMap().value(QStringLiteral("source_recorded_at")).toString(), QStringLiteral("2026-10-01 12:34:56"));
    QVERIFY(rows[0].toMap().value(QStringLiteral("reflection_text")).toString().isEmpty());
    QVERIFY(mr::ScreenshotImportParser::rowFields(image, tsv).isEmpty());
}

void ScreenshotImportParserTests::webRowsTouchingTheTopOrBottomEdgeAreKept()
{
    // 截图从某一行中间开始、在另一行中间结束：两行都保留，并提示接近截图边缘。
    QImage image = webPage(1428, 200, {QRect(7, 0, 1500, 80), QRect(7, 108, 1500, 92)});
    const QByteArray tsv = kTsvHeader
        + word(1, 145, 33, 44, 15, QStringLiteral("Lv.90"), 96, 1)
        + word(1, 286, 30, 140, 20, QStringLiteral("间歇灵泉哈姆岛"), 96, 2)
        + word(1, 484, 32, 103, 15, QStringLiteral("2024-09-19"), 96, 3)
        + word(1, 598, 32, 72, 15, QStringLiteral("23:05:12"), 96, 4)
        + word(1, 704, 30, 80, 20, QStringLiteral("无事发生"), 96, 5)
        + word(1, 145, 147, 44, 15, QStringLiteral("Lv.80"), 96, 6)
        + word(1, 286, 144, 200, 20, QStringLiteral("魔术工房玛托雅工作室"), 96, 7)
        + word(1, 526, 146, 106, 15, QStringLiteral("2024-09-19"), 96, 8)
        + word(1, 639, 146, 76, 15, QStringLiteral("22:16:48"), 96, 9)
        + word(1, 746, 144, 80, 20, QStringLiteral("装备要炸了"), 96, 10);
    const QVariantList rows = mr::ScreenshotImportParser::parse(image, tsv, {});
    QCOMPARE(rows.size(), 2);
    QCOMPARE(rows[0].toMap().value(QStringLiteral("duty_level")).toInt(), 90);
    QCOMPARE(rows[0].toMap().value(QStringLiteral("reflection_text")).toString(), QStringLiteral("无事发生"));
    QCOMPARE(rows[0].toMap().value(QStringLiteral("source_rect")).toMap().value(QStringLiteral("y")).toInt(), 0);
    QCOMPARE(rows[1].toMap().value(QStringLiteral("duty_level")).toInt(), 80);
    QCOMPARE(rows[1].toMap().value(QStringLiteral("reflection_text")).toString(), QStringLiteral("装备要炸了"));
    for (const QVariant &value : rows)
        QVERIFY(hasWarning(value.toMap(), QStringLiteral("接近截图边缘")));
    QCOMPARE(mr::ScreenshotImportParser::rowFields(image, tsv).size(), 2);
}

void ScreenshotImportParserTests::webRowWithMisreadDateOrLevelIsKept()
{
    // 日期或等级数字被读错一个字形时仍按单行记录保留，交给提示与分区重读修正，不能整行丢失。
    QImage image = webPage(1428, 230, {QRect(7, 7, 1500, 93), QRect(7, 115, 1500, 93)});
    const QByteArray tsv = kTsvHeader
        + word(1, 145, 47, 44, 15, QStringLiteral("Lv.9O"), 96, 1)
        + word(1, 286, 44, 140, 20, QStringLiteral("间歇灵泉哈姆岛"), 96, 2)
        + word(1, 484, 46, 103, 15, QStringLiteral("2024-09-2l"), 96, 3)
        + word(1, 598, 46, 72, 15, QStringLiteral("13:18:11"), 96, 4)
        + word(1, 704, 43, 80, 20, QStringLiteral("无事发生"), 96, 5)
        + word(1, 145, 155, 44, 15, QStringLiteral("Lv.l0"), 96, 6)
        + word(1, 286, 152, 140, 20, QStringLiteral("讨伐彷徨死灵！"), 96, 7)
        + word(1, 463, 154, 106, 15, QStringLiteral("2024-09-20"), 96, 8)
        + word(1, 577, 154, 75, 15, QStringLiteral("11:49:22"), 96, 9)
        + word(1, 684, 151, 80, 20, QStringLiteral("啵啵"), 96, 10);
    const QVariantList rows = mr::ScreenshotImportParser::parse(image, tsv, {});
    QCOMPARE(rows.size(), 2);
    const QVariantMap misreadDate = rows[0].toMap();
    QCOMPARE(misreadDate.value(QStringLiteral("duty_level")).toInt(), 90);
    QVERIFY(hasWarning(misreadDate, QStringLiteral("等级数字")));
    QCOMPARE(misreadDate.value(QStringLiteral("duty_name")).toString(), QStringLiteral("间歇灵泉哈姆岛"));
    QVERIFY(misreadDate.value(QStringLiteral("source_recorded_at")).toString().isEmpty());
    QVERIFY(hasWarning(misreadDate, QStringLiteral("来源记录时间缺失")));
    // 时间仍属时间列，不会流入心得。
    QCOMPARE(misreadDate.value(QStringLiteral("reflection_text")).toString(), QStringLiteral("无事发生"));
    const QVariantMap misreadLevel = rows[1].toMap();
    QCOMPARE(misreadLevel.value(QStringLiteral("duty_level")).toInt(), 10);
    QVERIFY(hasWarning(misreadLevel, QStringLiteral("等级数字")));
    QCOMPARE(misreadLevel.value(QStringLiteral("source_recorded_at")).toString(), QStringLiteral("2024-09-20 11:49:22"));
    QCOMPARE(misreadLevel.value(QStringLiteral("reflection_text")).toString(), QStringLiteral("啵啵"));
}

void ScreenshotImportParserTests::webRowTypeIconIgnoresColourNoise()
{
    // 次像素抗锯齿的彩色边缘与零散色点不是类型图标：单字方形单词和贴近日期的色点都不能清空副本名称。
    QImage image = webPage(1428, 120, {QRect(7, 7, 1500, 93)});
    paintTypeIcon(image, QRect(222, 39, 30, 30), QColor(136, 245, 251));
    const QString name = QStringLiteral("最终幻想末世终迹");
    QByteArray tsv = kTsvHeader + word(1, 145, 47, 44, 15, QStringLiteral("Lv.90"), 96, 1);
    {
        QPainter painter(&image);
        for (int i = 0; i < name.size(); ++i) {
            const int left = 286 + 22 * i;
            for (const int offset : {3, 10, 16})
                painter.fillRect(QRect(left + offset, 44, 1, 20), offset == 10 ? QColor(60, 60, 230) : QColor(230, 60, 60));
            tsv += word(1, left, 44, 20, 20, name.mid(i, 1), 96, 2 + i);
        }
        painter.fillRect(QRect(483, 46, 1, 15), QColor(230, 60, 60));
        painter.fillRect(QRect(470, 50, 2, 2), QColor(60, 200, 230));
    }
    tsv += word(1, 484, 46, 103, 15, QStringLiteral("2024-09-21"), 96, 20)
        + word(1, 598, 46, 72, 15, QStringLiteral("13:18:11"), 96, 21)
        + word(1, 704, 43, 80, 20, QStringLiteral("无事发生"), 96, 22);
    const QVariantList rows = mr::ScreenshotImportParser::parse(image, tsv, {});
    QCOMPARE(rows.size(), 1);
    QCOMPARE(rows[0].toMap().value(QStringLiteral("ocr_duty_name")).toString(), name);
    const QList<mr::ScreenshotImportParser::RowGeometry> geometry = mr::ScreenshotImportParser::rowFields(image, tsv);
    QCOMPARE(geometry.size(), 1);
    QVERIFY2(geometry[0].title.left() > 251 && geometry[0].title.left() <= 286,
             qPrintable(QString::number(geometry[0].title.left())));
    QVERIFY(geometry[0].title.right() < 484);
}

void ScreenshotImportParserTests::webRowIconWordIsExcludedByComponentOrByRatio()
{
    // 两条独立证据各自排除图标单词：描边图标靠图标连通块，超大实心图标靠单词框内的彩色比例。
    // 1000 宽时图标单词位于左侧 16% 之外，确实进入列划分，而不是先被水印规则舍弃。
    QImage image = webPage(1000, 230, {QRect(7, 7, 1100, 93), QRect(7, 115, 1100, 93)});
    {
        QPainter painter(&image);
        painter.fillRect(QRect(222, 39, 30, 30), QColor(70, 172, 176));
        painter.fillRect(QRect(224, 41, 26, 26), QColor(45, 69, 38));
        painter.fillRect(QRect(205, 131, 64, 64), QColor(220, 70, 50));
    }
    const QByteArray tsv = kTsvHeader
        + word(1, 145, 47, 44, 15, QStringLiteral("Lv.90"), 96, 1)
        + word(1, 220, 37, 34, 34, QStringLiteral("@"), 60, 2)
        + word(1, 286, 44, 140, 20, QStringLiteral("间歇灵泉哈姆岛"), 96, 3)
        + word(1, 484, 46, 103, 15, QStringLiteral("2024-09-19"), 96, 4)
        + word(1, 598, 46, 72, 15, QStringLiteral("23:05:12"), 96, 5)
        + word(1, 704, 43, 80, 20, QStringLiteral("无事发生"), 96, 6)
        + word(1, 145, 155, 44, 15, QStringLiteral("Lv.50"), 96, 7)
        + word(1, 205, 131, 64, 64, QStringLiteral("包"), 60, 8)
        + word(1, 286, 152, 180, 20, QStringLiteral("最终决战天幕魔导城"), 96, 9)
        + word(1, 505, 154, 106, 15, QStringLiteral("2024-09-19"), 96, 10)
        + word(1, 618, 154, 76, 15, QStringLiteral("20:08:34"), 96, 11)
        + word(1, 725, 151, 40, 20, QStringLiteral("我恨"), 96, 12);
    const QVariantList rows = mr::ScreenshotImportParser::parse(image, tsv, {});
    QCOMPARE(rows.size(), 2);
    QCOMPARE(rows[0].toMap().value(QStringLiteral("ocr_duty_name")).toString(), QStringLiteral("间歇灵泉哈姆岛"));
    QCOMPARE(rows[1].toMap().value(QStringLiteral("ocr_duty_name")).toString(), QStringLiteral("最终决战天幕魔导城"));
}

void ScreenshotImportParserTests::webRowDeleteMarkFusedWithTheLastGlyphIsStripped()
{
    // 删除叉号与心得末字连成一个识别单词时，行尾的叉号仍不是正文，并提示可能遮挡。
    // × 本身不会是字母；末尾的 X 只有其后确有叉号笔画时才去掉（第二行画出了按钮）。
    QImage image = webPage(1848, 230, {QRect(7, 10, 1823, 93), QRect(7, 120, 1823, 93)});
    paintDeleteMark(image, QRect(1775, 159, 17, 17));
    const QByteArray tsv = kTsvHeader
        + word(1, 145, 50, 44, 15, QStringLiteral("Lv.90"), 96, 1)
        + word(1, 330, 48, 140, 20, QStringLiteral("间歇灵泉哈姆岛"), 96, 2)
        + word(1, 520, 50, 106, 15, QStringLiteral("2024-09-19"), 96, 3)
        + word(1, 634, 50, 76, 15, QStringLiteral("23:05:12"), 96, 4)
        + word(1, 760, 48, 1030, 20, QStringLiteral("很长的心得一直写到删除按钮旁边我是天才×"), 96, 5)
        + word(1, 145, 160, 44, 15, QStringLiteral("Lv.90"), 96, 6)
        + word(1, 330, 158, 160, 20, QStringLiteral("雪山奥窟冥魂石洞"), 96, 7)
        + word(1, 520, 160, 106, 15, QStringLiteral("2024-09-20"), 96, 8)
        + word(1, 634, 160, 74, 15, QStringLiteral("12:40:33"), 96, 9)
        + word(1, 760, 158, 1032, 20, QStringLiteral("另一条写满整行的心得最后一个字母X"), 96, 10);
    const QVariantList rows = mr::ScreenshotImportParser::parse(image, tsv, {});
    QCOMPARE(rows.size(), 2);
    QCOMPARE(rows[0].toMap().value(QStringLiteral("reflection_text")).toString(),
             QStringLiteral("很长的心得一直写到删除按钮旁边我是天才"));
    QCOMPARE(rows[1].toMap().value(QStringLiteral("reflection_text")).toString(),
             QStringLiteral("另一条写满整行的心得最后一个字母"));
    for (const QVariant &value : rows)
        QVERIFY(hasWarning(value.toMap(), QStringLiteral("删除按钮")));
}

void ScreenshotImportParserTests::webRowDeleteWordNearTheRowEndOnANarrowPage()
{
    // 网页内容区较窄时行尾不在截图右侧 83% 之外；行尾的叉号仍按删除按钮处理。
    QImage image = webPage(1848, 120, {QRect(7, 10, 1093, 93)});
    const QByteArray tsv = kTsvHeader
        + word(1, 145, 50, 44, 15, QStringLiteral("Lv.90"), 96, 1)
        + word(1, 330, 48, 140, 20, QStringLiteral("间歇灵泉哈姆岛"), 96, 2)
        + word(1, 520, 50, 106, 15, QStringLiteral("2024-09-19"), 96, 3)
        + word(1, 634, 50, 76, 15, QStringLiteral("23:05:12"), 96, 4)
        + word(1, 760, 48, 80, 20, QStringLiteral("无事发生"), 96, 5)
        + word(1, 1066, 48, 20, 20, QStringLiteral("×"), 90, 6);
    const QVariantList rows = mr::ScreenshotImportParser::parse(image, tsv, {});
    QCOMPARE(rows.size(), 1);
    QCOMPARE(rows[0].toMap().value(QStringLiteral("reflection_text")).toString(), QStringLiteral("无事发生"));
    QVERIFY(!hasWarning(rows[0].toMap(), QStringLiteral("删除按钮")));
    const QList<mr::ScreenshotImportParser::RowGeometry> geometry = mr::ScreenshotImportParser::rowFields(image, tsv);
    QCOMPARE(geometry.size(), 1);
    QVERIFY(geometry[0].body.right() < 1066);
}

void ScreenshotImportParserTests::rowFieldsLeaveColumnsWithoutRoomEmpty()
{
    // 日期紧跟类型图标、叉号紧跟时间时，标题列与心得列没有空间，不能被翻转成一条窄条。
    QImage image = webPage(600, 120, {QRect(7, 7, 450, 93)});
    paintTypeIcon(image, QRect(200, 39, 30, 30), QColor(220, 70, 50));
    const QByteArray tsv = kTsvHeader
        + word(1, 145, 47, 44, 15, QStringLiteral("Lv.90"), 96, 1)
        + word(1, 236, 46, 103, 15, QStringLiteral("2024-09-21"), 96, 2)
        + word(1, 350, 46, 72, 15, QStringLiteral("13:18:11"), 96, 3)
        + word(1, 424, 46, 20, 20, QStringLiteral("×"), 90, 4);
    const QList<mr::ScreenshotImportParser::RowGeometry> rows = mr::ScreenshotImportParser::rowFields(image, tsv);
    QCOMPARE(rows.size(), 1);
    QVERIFY(rows[0].title.isNull());
    QVERIFY(rows[0].body.isNull());
    QVERIFY(rows[0].time.isValid());
}

void ScreenshotImportParserTests::webRowNoteLinesStaySeparateUnderATallOcrBox()
{
    // 一个上下伸出的高识别框不能把两行心得并成一行。
    QImage image = webPage(1848, 200, {QRect(7, 20, 1823, 151)});
    const QByteArray tsv = kTsvHeader
        + word(1, 145, 83, 44, 15, QStringLiteral("Lv.50"), 96, 1)
        + word(1, 330, 60, 126, 20, QStringLiteral("究极神兵假想"), 96, 2)
        + word(1, 330, 100, 42, 20, QStringLiteral("作战"), 96, 3)
        + word(1, 600, 62, 106, 15, QStringLiteral("2024-09-11"), 96, 4)
        + word(1, 600, 102, 74, 15, QStringLiteral("20:22:36"), 96, 5)
        + word(1, 760, 60, 100, 20, QStringLiteral("第一行"), 96, 6)
        + word(1, 870, 55, 40, 55, QStringLiteral("心得"), 96, 7)
        + word(1, 760, 100, 100, 20, QStringLiteral("第二行"), 96, 8);
    const QVariantList rows = mr::ScreenshotImportParser::parse(image, tsv, {});
    QCOMPARE(rows.size(), 1);
    QCOMPARE(rows[0].toMap().value(QStringLiteral("reflection_text")).toString(), QStringLiteral("第一行心得\n第二行"));
}

void ScreenshotImportParserTests::mobileCardWithFooterRightUnderTheTitleIsNotAWebRow()
{
    // 手机卡片正文为空、页脚日期距标题不到一个等级字高：日期不与等级或副本名称同行，仍是卡片。
    QImage image = screenshot();
    {
        QPainter painter(&image);
        painter.fillRect(QRect(30, 70, 540, 100), QColor(85, 45, 45));
    }
    const QByteArray tsv = kTsvHeader
        + word(1, 150, 100, 65, 20, QStringLiteral("Lv.50"))
        + word(1, 225, 100, 200, 20, QStringLiteral("合成副本"), 96, 1, 2)
        + word(2, 380, 125, 190, 15, QStringLiteral("2026-10-01 12:34:56"));
    QVERIFY(mr::ScreenshotImportParser::rowFields(image, tsv).isEmpty());
    const QVariantList rows = mr::ScreenshotImportParser::parse(image, tsv, {});
    QCOMPARE(rows.size(), 1);
    QCOMPARE(rows[0].toMap().value(QStringLiteral("duty_name")).toString(), QStringLiteral("合成副本"));
    QCOMPARE(rows[0].toMap().value(QStringLiteral("source_recorded_at")).toString(), QStringLiteral("2026-10-01 12:34:56"));
}

void ScreenshotImportParserTests::mobileBodyLineStartingWithLevelAndDateIsNotAWebRow()
{
    // 手机卡片正文恰好以「Lv.70 … 日期」开头：同一卡片底色内已有标题等级，这一行仍是正文。
    QImage image = screenshot();
    {
        QPainter painter(&image);
        painter.fillRect(QRect(30, 70, 540, 180), QColor(85, 45, 45));
    }
    const QByteArray tsv = kTsvHeader
        + word(1, 150, 100, 280, 20, QStringLiteral("Lv.50 合成副本"))
        + word(2, 150, 140, 65, 20, QStringLiteral("Lv.70"))
        + word(2, 225, 140, 60, 20, QStringLiteral("打完"), 96, 1, 2)
        + word(2, 300, 142, 105, 15, QStringLiteral("2024-09-01"), 96, 1, 3)
        + word(3, 150, 165, 260, 20, QStringLiteral("合成心得第二行"))
        + word(4, 380, 200, 190, 15, QStringLiteral("2026-10-01 12:34:56"));
    QVERIFY(mr::ScreenshotImportParser::rowFields(image, tsv).isEmpty());
    // 卡片标题不被并入一条「Lv.70」单行记录；正文以 Lv 开头时另起一张卡片是手机解析原有的行为。
    const QVariantList rows = mr::ScreenshotImportParser::parse(image, tsv, {});
    QVERIFY(!rows.isEmpty());
    QCOMPARE(rows[0].toMap().value(QStringLiteral("duty_level")).toInt(), 50);
    QCOMPARE(rows[0].toMap().value(QStringLiteral("duty_name")).toString(), QStringLiteral("合成副本"));
}

void ScreenshotImportParserTests::mobileWatermarkLookingLikeALevelIsNotText()
{
    // 卡片左侧水印被识别成「V8」这类等级字样时仍是图像证据，不能把两行正文并成一行。
    QImage image = screenshot();
    {
        QPainter painter(&image);
        painter.fillRect(QRect(30, 70, 540, 180), QColor(85, 45, 45));
    }
    const QByteArray tsv = kTsvHeader + card(100) + word(9, 40, 130, 60, 80, QStringLiteral("V8"), 60, 9);
    const QVariantList rows = mr::ScreenshotImportParser::parse(image, tsv, {});
    QCOMPARE(rows.size(), 1);
    QCOMPARE(rows[0].toMap().value(QStringLiteral("reflection_text")).toString(),
             QStringLiteral("合成心得第一行\n合成心得第二行"));
}

void ScreenshotImportParserTests::webRowWithANoteCoveringEverySampleColumnIsKept()
{
    // 样例二 Lv.87 一行：等级、副本名、时间与长心得盖住行中线上每个固定比例取样点，行底色仍须找到。
    QImage image = webPage(1845, 120, {QRect(2, 10, 1829, 92)});
    QByteArray tsv = kTsvHeader;
    tsv += inked(image, QRect(140, 50, 44, 15), QStringLiteral("Lv.87"), 1);
    tsv += inked(image, QRect(281, 47, 192, 20), QStringLiteral("创造环境极北造物院"), 2);
    tsv += inked(image, QRect(500, 49, 105, 15), QStringLiteral("2024-09-13"), 3);
    tsv += inked(image, QRect(614, 49, 75, 15), QStringLiteral("17:15:14"), 4);
    tsv += inked(image, QRect(720, 46, 980, 20), QStringLiteral("战士好神秘的减伤安排但是收获了三个赞"), 5);
    QString error;
    const QVariantList rows = mr::ScreenshotImportParser::parse(image, tsv, {}, &error);
    QVERIFY2(error.isEmpty(), qPrintable(error));
    QCOMPARE(rows.size(), 1);
    const QVariantMap row = rows[0].toMap();
    QCOMPARE(row.value(QStringLiteral("duty_level")).toInt(), 87);
    QCOMPARE(row.value(QStringLiteral("ocr_duty_name")).toString(), QStringLiteral("创造环境极北造物院"));
    QCOMPARE(row.value(QStringLiteral("source_recorded_at")).toString(), QStringLiteral("2024-09-13 17:15:14"));
    QCOMPARE(row.value(QStringLiteral("reflection_text")).toString(), QStringLiteral("战士好神秘的减伤安排但是收获了三个赞"));
    QCOMPARE(row.value(QStringLiteral("source_rect")).toMap().value(QStringLiteral("y")).toInt(), 10);
    QCOMPARE(row.value(QStringLiteral("source_rect")).toMap().value(QStringLiteral("height")).toInt(), 92);
}

void ScreenshotImportParserTests::webRowDutyLeftOfTheWatermarkCutOffIsKept()
{
    // 1845 宽网页：等级与副本名称按像素固定在 x=140 与 x=280，比例截止线（16%）在 x=295。
    // 副本名称的首字、换到第二行的「作战」和类型图标都在截止线左侧，前两者属于副本名称，图标识别字不属于。
    QImage image = webPage(1845, 240, {QRect(2, 7, 1829, 92), QRect(2, 130, 1829, 92)});
    paintTypeIcon(image, QRect(215, 39, 33, 33), QColor(70, 172, 176));
    paintTypeIcon(image, QRect(215, 160, 33, 33), QColor(220, 70, 50));
    const QByteArray tsv = kTsvHeader
        + word(1, 140, 47, 44, 15, QStringLiteral("Lv.50"), 92, 1)
        + word(1, 215, 37, 33, 34, QStringLiteral("a"), 85, 2)
        + word(1, 281, 44, 31, 20, QStringLiteral("剑"), 93, 3, 1)
        + word(1, 323, 44, 11, 20, QStringLiteral("斗"), 92, 3, 2)
        + word(1, 333, 40, 43, 35, QStringLiteral("领域"), 95, 3, 3)
        + word(1, 386, 45, 12, 19, QStringLiteral("日"), 90, 3, 4)
        + word(1, 407, 44, 7, 16, QStringLiteral("影"), 92, 3, 5)
        + word(1, 417, 44, 17, 20, QStringLiteral("地"), 97, 3, 6)
        + word(1, 434, 40, 45, 35, QStringLiteral("修炼"), 96, 3, 7)
        + word(1, 478, 45, 12, 19, QStringLiteral("所"), 96, 3, 8)
        + word(1, 521, 46, 105, 15, QStringLiteral("2024-09-13"), 97, 4, 1)
        + word(1, 635, 46, 72, 15, QStringLiteral("17:45:11"), 97, 4, 2)
        + word(1, 741, 43, 80, 20, QStringLiteral("活死人"), 93, 5)
        + word(1, 140, 170, 44, 15, QStringLiteral("Lv.50"), 92, 6)
        + word(1, 215, 160, 33, 34, QStringLiteral("四"), 59, 7)
        + word(1, 280, 149, 20, 20, QStringLiteral("究"), 93, 8, 1)
        + word(1, 308, 149, 12, 20, QStringLiteral("极"), 93, 8, 2)
        + word(1, 330, 149, 23, 20, QStringLiteral("神"), 93, 8, 3)
        + word(1, 364, 149, 11, 20, QStringLiteral("兵"), 93, 8, 4)
        + word(1, 374, 145, 30, 35, QStringLiteral("假想"), 97, 8, 5)
        + word(1, 453, 153, 103, 15, QStringLiteral("2024-09-11"), 90, 9)
        + word(1, 658, 150, 200, 20, QStringLiteral("两个人撞球啊"), 96, 10)
        + word(1, 280, 185, 41, 20, QStringLiteral("作战"), 96, 11)
        + word(1, 453, 186, 76, 15, QStringLiteral("20:22:36"), 96, 12)
        + word(1, 645, 183, 150, 20, QStringLiteral("唤你也补药鼠啊"), 93, 13);
    QString error;
    const QVariantList rows = mr::ScreenshotImportParser::parse(image, tsv, {}, &error);
    QVERIFY2(error.isEmpty(), qPrintable(error));
    QCOMPARE(rows.size(), 2);
    QCOMPARE(rows[0].toMap().value(QStringLiteral("ocr_duty_name")).toString(), QStringLiteral("剑斗领域日影地修炼所"));
    QCOMPARE(rows[0].toMap().value(QStringLiteral("reflection_text")).toString(), QStringLiteral("活死人"));
    const QVariantMap wrapped = rows[1].toMap();
    QCOMPARE(wrapped.value(QStringLiteral("ocr_duty_name")).toString(), QStringLiteral("究极神兵假想作战"));
    QCOMPARE(wrapped.value(QStringLiteral("source_recorded_at")).toString(), QStringLiteral("2024-09-11 20:22:36"));
    QCOMPARE(wrapped.value(QStringLiteral("reflection_text")).toString(), QStringLiteral("两个人撞球啊\n唤你也补药鼠啊"));
    const QList<mr::ScreenshotImportParser::RowGeometry> geometry = mr::ScreenshotImportParser::rowFields(image, tsv);
    QCOMPARE(geometry.size(), 2);
    for (const auto &row : geometry) {
        QVERIFY2(row.title.left() > 248 && row.title.left() <= 280, qPrintable(QString::number(row.title.left())));
        QVERIFY(row.row.left() <= 4);
    }
}

void ScreenshotImportParserTests::webRowDeleteMarkIsFoundByPixelsEvenWhenTouchingTheNote()
{
    // 行尾删除叉号按像素认出（细笔画、抗锯齿），与心得末字相连时同样认出。
    // 叉号位置上的识别单词（这里被读成「Y」）不进入心得；紧贴心得时提示可能遮挡，分开时不提示。
    QImage image = webPage(1845, 230, {QRect(2, 7, 1829, 92), QRect(2, 115, 1829, 92)});
    {
        QPainter painter(&image);
        painter.fillRect(QRect(1765, 44, 15, 18), QColor(225, 232, 224)); // 「才」，右缘碰到叉号笔画。
    }
    paintDeleteMark(image, QRect(1779, 45, 17, 17));
    paintDeleteMark(image, QRect(1781, 153, 17, 17));
    const QByteArray tsv = kTsvHeader
        + word(1, 140, 47, 44, 15, QStringLiteral("Lv.50"), 92, 1)
        + word(1, 281, 44, 209, 20, QStringLiteral("剑斗领域日影地修炼所"), 93, 2)
        + word(1, 521, 46, 105, 15, QStringLiteral("2024-09-13"), 97, 3)
        + word(1, 635, 46, 72, 15, QStringLiteral("17:45:11"), 97, 4)
        + word(1, 1700, 43, 60, 20, QStringLiteral("我是天"), 97, 5, 1)
        + word(1, 1765, 43, 14, 20, QStringLiteral("才"), 93, 5, 2)
        + word(1, 1781, 45, 17, 17, QStringLiteral("Y"), 83, 5, 3)
        + word(1, 140, 155, 44, 15, QStringLiteral("Lv.44"), 92, 6)
        + word(1, 281, 152, 188, 20, QStringLiteral("山中战线泽梅尔要塞"), 93, 7)
        + word(1, 500, 154, 103, 15, QStringLiteral("2024-09-11"), 91, 8)
        + word(1, 613, 154, 76, 15, QStringLiteral("19:54:56"), 90, 9)
        + word(1, 720, 151, 40, 20, QStringLiteral("伤心"), 93, 10, 1)
        + word(1, 1781, 153, 17, 17, QStringLiteral("Y"), 83, 10, 2);
    const QVariantList rows = mr::ScreenshotImportParser::parse(image, tsv, {});
    QCOMPARE(rows.size(), 2);
    QCOMPARE(rows[0].toMap().value(QStringLiteral("reflection_text")).toString(), QStringLiteral("我是天才"));
    QVERIFY(hasWarning(rows[0].toMap(), QStringLiteral("删除按钮")));
    QCOMPARE(rows[1].toMap().value(QStringLiteral("reflection_text")).toString(), QStringLiteral("伤心"));
    QVERIFY(!hasWarning(rows[1].toMap(), QStringLiteral("删除按钮")));
    // 分区识别用到的叉号是像素证据，心得列可越过它（擦除后读叉号右侧溢出的文字）。
    const QList<mr::ScreenshotImportParser::RowGeometry> geometry = mr::ScreenshotImportParser::rowFields(image, tsv);
    QCOMPARE(geometry.size(), 2);
    QVERIFY(geometry[0].deleteMark.contains(QPoint(1788, 53)));
    QVERIFY(geometry[1].deleteMark.contains(QPoint(1789, 161)));
}

void ScreenshotImportParserTests::webRowCrossReadAsAGlyphAtTheRowEndIsNotNoteText()
{
    // 行尾单独一个「义」「久」「二」之类的字，只有字框里确有叉号笔画（两条对角线）时才是被误读的删除按钮。
    // 第一行的叉号中心偏淡，整体叉号检测认不出，由字框核对去掉；真正的「二」和没有叉号笔画的「久」保留。
    QImage image = webPage(1845, 440, {QRect(2, 7, 1829, 92), QRect(2, 115, 1829, 92),
                                        QRect(2, 223, 1829, 92), QRect(2, 331, 1829, 92)});
    {
        QPainter painter(&image);
        painter.setRenderHint(QPainter::Antialiasing);
        painter.setPen(QPen(QColor(235, 240, 235), 2.0));
        // 叉号的四段笔画，中心一段未画出。
        painter.drawLine(QPointF(1781, 45), QPointF(1787, 51));
        painter.drawLine(QPointF(1791, 55), QPointF(1797, 61));
        painter.drawLine(QPointF(1781, 61), QPointF(1787, 55));
        painter.drawLine(QPointF(1791, 51), QPointF(1797, 45));
        // 真正的「二」：两条横笔。
        painter.setRenderHint(QPainter::Antialiasing, false);
        painter.fillRect(QRect(1783, 157, 13, 2), QColor(235, 240, 235));
        painter.fillRect(QRect(1781, 166, 17, 2), QColor(235, 240, 235));
    }
    QByteArray tsv = kTsvHeader;
    for (int i = 0; i < 4; ++i) {
        const int top = 7 + 108 * i;
        tsv += word(1, 140, top + 40, 44, 15, QStringLiteral("Lv.44"), 92, 10 * i + 1)
            + word(1, 281, top + 37, 188, 20, QStringLiteral("山中战线泽梅尔要塞"), 93, 10 * i + 2)
            + word(1, 500, top + 39, 103, 15, QStringLiteral("2024-09-11"), 91, 10 * i + 3)
            + word(1, 613, top + 39, 76, 15, QStringLiteral("19:54:56"), 90, 10 * i + 4);
    }
    tsv += word(1, 720, 44, 40, 20, QStringLiteral("伤心"), 93, 5, 1) + word(1, 1781, 45, 17, 17, QStringLiteral("义"), 83, 5, 2);
    tsv += word(1, 720, 152, 20, 20, QStringLiteral("第"), 93, 15, 1) + word(1, 1781, 153, 17, 17, QStringLiteral("二"), 93, 15, 2);
    tsv += word(1, 720, 260, 20, 20, QStringLiteral("好"), 93, 25, 1) + word(1, 1781, 261, 17, 17, QStringLiteral("久"), 93, 25, 2);
    tsv += word(1, 720, 368, 40, 20, QStringLiteral("很有"), 93, 35, 1) + word(1, 1745, 368, 36, 20, QStringLiteral("意义"), 93, 35, 2);
    const QVariantList rows = mr::ScreenshotImportParser::parse(image, tsv, {});
    QCOMPARE(rows.size(), 4);
    QVERIFY(mr::ScreenshotImportParser::rowFields(image, tsv).value(0).deleteMark.isNull());
    QCOMPARE(rows[0].toMap().value(QStringLiteral("reflection_text")).toString(), QStringLiteral("伤心"));
    QCOMPARE(rows[1].toMap().value(QStringLiteral("reflection_text")).toString(), QStringLiteral("第二"));
    QCOMPARE(rows[2].toMap().value(QStringLiteral("reflection_text")).toString(), QStringLiteral("好久"));
    QCOMPARE(rows[3].toMap().value(QStringLiteral("reflection_text")).toString(), QStringLiteral("很有意义"));
}

void ScreenshotImportParserTests::webRowEndGlyphThatIsNotACrossStaysNoteText()
{
    // 行尾一个实心方块字的两条对角线也被笔画覆盖，但上、下、右三边中段不空，它不是删除叉号。
    QImage image = webPage(1845, 120, {QRect(2, 7, 1829, 92)});
    {
        QPainter painter(&image);
        painter.fillRect(QRect(1781, 45, 17, 17), QColor(225, 232, 224));
    }
    const QByteArray tsv = kTsvHeader
        + word(1, 140, 47, 44, 15, QStringLiteral("Lv.44"), 92, 1)
        + word(1, 281, 44, 188, 20, QStringLiteral("山中战线泽梅尔要塞"), 93, 2)
        + word(1, 500, 46, 103, 15, QStringLiteral("2024-09-11"), 91, 3)
        + word(1, 613, 46, 76, 15, QStringLiteral("19:54:56"), 90, 4)
        + word(1, 720, 43, 40, 20, QStringLiteral("伤心"), 93, 5, 1)
        + word(1, 1781, 45, 17, 17, QStringLiteral("口"), 90, 5, 2);
    const QVariantList rows = mr::ScreenshotImportParser::parse(image, tsv, {});
    QCOMPARE(rows.size(), 1);
    QCOMPARE(rows[0].toMap().value(QStringLiteral("reflection_text")).toString(), QStringLiteral("伤心口"));
    QVERIFY(!hasWarning(rows[0].toMap(), QStringLiteral("删除按钮")));
    QVERIFY(mr::ScreenshotImportParser::rowFields(image, tsv).value(0).deleteMark.isNull());
}

namespace {

/** @brief 1845 宽网页的一条单行记录（行 2..1830 × top..top+91），返回等级、副本、日期与时间单词。 */
QByteArray rowHead(int top, const QString &level, int block)
{
    return word(1, 140, top + 40, 44, 15, level, 92, block)
        + word(1, 281, top + 37, 188, 20, QStringLiteral("山中战线泽梅尔要塞"), 93, block + 1)
        + word(1, 500, top + 39, 103, 15, QStringLiteral("2024-09-11"), 91, block + 2)
        + word(1, 613, top + 39, 76, 15, QStringLiteral("19:54:56"), 90, block + 3);
}

} // namespace

void ScreenshotImportParserTests::webRowThinAntialiasedDeleteMarkIsFound()
{
    // 浏览器缩小或截图缩放后叉号笔画只有约 1.2 像素：亮色笔画只在对角方向相连，仍须认出整个叉号。
    QImage image = webPage(1845, 120, {QRect(2, 7, 1829, 92)});
    paintDeleteMark(image, QRect(1781, 45, 17, 17), 1.2);
    const QByteArray tsv = kTsvHeader + rowHead(7, QStringLiteral("Lv.44"), 1)
        + word(1, 720, 44, 40, 20, QStringLiteral("伤心"), 93, 10, 1)
        + word(1, 1781, 45, 17, 17, QStringLiteral("Y"), 83, 10, 2);
    const QVariantList rows = mr::ScreenshotImportParser::parse(image, tsv, {});
    QCOMPARE(rows.size(), 1);
    QCOMPARE(rows[0].toMap().value(QStringLiteral("reflection_text")).toString(), QStringLiteral("伤心"));
    QVERIFY(mr::ScreenshotImportParser::rowFields(image, tsv).value(0).deleteMark.contains(QPoint(1789, 53)));
}

void ScreenshotImportParserTests::webRowLatinXIsStrippedOnlyWithAButtonBehindIt()
{
    // 行尾心得以「thx」结尾、行尾没有删除按钮：x 是正文，不提示遮挡。
    // 有删除按钮且识别成「thx×」时只去掉 ×，不再把 x 也当成叉号去掉。
    QImage image = webPage(1845, 230, {QRect(2, 7, 1829, 92), QRect(2, 115, 1829, 92)});
    paintDeleteMark(image, QRect(1781, 153, 17, 17));
    const QByteArray tsv = kTsvHeader + rowHead(7, QStringLiteral("Lv.44"), 1)
        + word(1, 720, 44, 40, 20, QStringLiteral("很好"), 93, 10, 1)
        + word(1, 1745, 45, 40, 17, QStringLiteral("thx"), 90, 10, 2)
        + rowHead(115, QStringLiteral("Lv.44"), 20)
        + word(1, 720, 152, 40, 20, QStringLiteral("很好"), 93, 30, 1)
        + word(1, 1745, 153, 52, 17, QStringLiteral("thx×"), 90, 30, 2);
    const QVariantList rows = mr::ScreenshotImportParser::parse(image, tsv, {});
    QCOMPARE(rows.size(), 2);
    QCOMPARE(rows[0].toMap().value(QStringLiteral("reflection_text")).toString(), QStringLiteral("很好thx"));
    QVERIFY(!hasWarning(rows[0].toMap(), QStringLiteral("删除按钮")));
    QCOMPARE(rows[1].toMap().value(QStringLiteral("reflection_text")).toString(), QStringLiteral("很好thx"));
    QVERIFY(hasWarning(rows[1].toMap(), QStringLiteral("删除按钮")));
}

void ScreenshotImportParserTests::webRowLiteralCrossBesideAPixelLocatedButtonIsPreserved_data()
{
    QTest::addColumn<QString>("literal");
    QTest::newRow("lowercase-x") << QStringLiteral("x");
    QTest::newRow("uppercase-x") << QStringLiteral("X");
    QTest::newRow("multiplication-sign") << QStringLiteral("×");
    QTest::newRow("cross-sign") << QStringLiteral("✕");
}

void ScreenshotImportParserTests::webRowLiteralCrossBesideAPixelLocatedButtonIsPreserved()
{
    QFETCH(QString, literal);
    // 正文里的单独 x/× 靠近行尾，但与更右侧、已按像素确认的按钮不相交。
    // 既要保留正文符号，也要剔除真正的按钮，不能只看右缘位置或字符形状。
    QImage image = webPage(1845, 120, {QRect(2, 7, 1829, 92)});
    paintDeleteMark(image, QRect(1810, 45, 17, 17));
    const QByteArray tsv = kTsvHeader + rowHead(7, QStringLiteral("Lv.44"), 1)
        + word(1, 720, 44, 40, 20, QStringLiteral("正文"), 93, 10)
        + word(1, 1770, 45, 17, 17, literal, 93, 11)
        + word(1, 1810, 45, 17, 17, QStringLiteral("X"), 90, 12);
    const auto geometry = mr::ScreenshotImportParser::rowFields(image, tsv);
    QCOMPARE(geometry.size(), 1);
    QVERIFY(geometry[0].deleteMark.contains(QPoint(1818, 53)));
    QVERIFY(!geometry[0].deleteMark.intersects(QRect(1770, 45, 17, 17)));
    const QVariantList rows = mr::ScreenshotImportParser::parse(image, tsv, {});
    QCOMPARE(rows.size(), 1);
    QCOMPARE(rows[0].toMap().value(QStringLiteral("reflection_text")).toString(), QStringLiteral("正文") + literal);
}

void ScreenshotImportParserTests::fragmentedAsciiAndPunctuationKeepSourceSpacing_data()
{
    QTest::addColumn<QStringList>("parts");
    QTest::addColumn<int>("gap");
    QTest::addColumn<QString>("expected");
    QTest::newRow("adjacent-numeric-face") << QStringList{QStringLiteral("0"), QStringLiteral("30")}
        << 1 << QStringLiteral("030");
    QTest::newRow("adjacent-latin-word") << QStringList{QStringLiteral("th"), QStringLiteral("x")}
        << 1 << QStringLiteral("thx");
    QTest::newRow("separated-english-words") << QStringList{QStringLiteral("hello"), QStringLiteral("world")}
        << 12 << QStringLiteral("hello world");
    QTest::newRow("source-space-inside-a-box") << QStringList{QStringLiteral("hello world")}
        << 1 << QStringLiteral("hello world");
    QTest::newRow("leading-fullwidth-dots") << QStringList{QStringLiteral("。"), QStringLiteral("。"), QStringLiteral("。"), QStringLiteral("正文")}
        << 1 << QStringLiteral("。。。正文");
    QTest::newRow("filename-dot") << QStringList{QStringLiteral("回家了"), QStringLiteral("."), QStringLiteral("jpg")}
        << 1 << QStringLiteral("回家了.jpg");
    QTest::newRow("split-kaomoji") << QStringList{QStringLiteral("(;"), QStringLiteral("´～`"), QStringLiteral(")")}
        << 1 << QStringLiteral("(;´～`)");
    QTest::newRow("fullwidth-punctuation-stays-fullwidth") << QStringList{QStringLiteral("啵啵030"), QStringLiteral("！")}
        << 1 << QStringLiteral("啵啵030！");
    QTest::newRow("halfwidth-punctuation-stays-halfwidth") << QStringList{QStringLiteral("啵啵030"), QStringLiteral("!")}
        << 1 << QStringLiteral("啵啵030!");
}

void ScreenshotImportParserTests::fragmentedAsciiAndPunctuationKeepSourceSpacing()
{
    QFETCH(QStringList, parts);
    QFETCH(int, gap);
    QFETCH(QString, expected);
    // 同一正文在网页行和手机卡片两种布局里都不应因 OCR 分块而改变内容。
    for (const bool web : {true, false}) {
        QImage image = web ? webPage(1845, 120, {QRect(2, 7, 1829, 92)}) : screenshot(300);
        QByteArray tsv = kTsvHeader;
        if (web) {
            tsv += rowHead(7, QStringLiteral("Lv.44"), 1);
        } else {
            tsv += word(1, 150, 100, 280, 20, QStringLiteral("Lv.50 合成副本"), 96, 1)
                + word(3, 380, 200, 190, 15, QStringLiteral("2026-10-01 12:34:56"), 96, 2);
        }
        int x = web ? 720 : 150;
        for (qsizetype i = 0; i < parts.size(); ++i) {
            const int width = std::max(10, static_cast<int>(parts[i].size()) * 10);
            tsv += word(web ? 1 : 2, x, web ? 44 : 140, width, 20, parts[i], 96, 10 + static_cast<int>(i));
            x += width + gap;
        }
        const QVariantList rows = mr::ScreenshotImportParser::parse(image, tsv, {});
        QCOMPARE(rows.size(), 1);
        QCOMPARE(rows[0].toMap().value(QStringLiteral("reflection_text")).toString(), expected);
        QCOMPARE(rows[0].toMap().value(QStringLiteral("source_recorded_at")).toString(),
                 web ? QStringLiteral("2024-09-11 19:54:56") : QStringLiteral("2026-10-01 12:34:56"));
    }
}

void ScreenshotImportParserTests::webRowExtentIgnoresANoteLineAboveTheLevel()
{
    // 两行心得居中、第一行高于等级和日期：量行右缘的扫描线须在全部文字之上，否则停在心得上，
    // 行尾的删除叉号就落在行外。
    QImage image = webPage(1845, 120, {QRect(2, 10, 1829, 92)});
    paintDeleteMark(image, QRect(1781, 48, 17, 17));
    QByteArray tsv = kTsvHeader + rowHead(10, QStringLiteral("Lv.44"), 1);
    tsv += inked(image, QRect(720, 22, 200, 20), QStringLiteral("第一行心得"), 10);
    tsv += word(1, 720, 60, 120, 20, QStringLiteral("第二行"), 93, 11);
    tsv += word(1, 1781, 48, 17, 17, QStringLiteral("Y"), 83, 12);
    const QVariantList rows = mr::ScreenshotImportParser::parse(image, tsv, {});
    QCOMPARE(rows.size(), 1);
    const QVariantMap rect = rows[0].toMap().value(QStringLiteral("source_rect")).toMap();
    // The row ends at 1830; its rounded corner may trim a few pixels at the scan line.
    QVERIFY2(rect.value(QStringLiteral("x")).toInt() + rect.value(QStringLiteral("width")).toInt() >= 1820,
             qPrintable(QString::number(rect.value(QStringLiteral("width")).toInt())));
    QCOMPARE(rows[0].toMap().value(QStringLiteral("reflection_text")).toString(), QStringLiteral("第一行心得\n第二行"));
}

void ScreenshotImportParserTests::webRowCroppedThroughTheJobIconUsesTheStripRightOfTheLevel()
{
    // 截图从职业图标中间裁起：等级离左缘不足一个字高，改在等级右侧（与类型图标之间）的留白取行底色。
    QImage image = webPage(1300, 120, {QRect(0, 7, 1400, 93)});
    paintTypeIcon(image, QRect(85, 39, 30, 30), QColor(136, 245, 251));
    const QByteArray tsv = kTsvHeader
        + word(1, 8, 47, 44, 15, QStringLiteral("Lv.90"), 92, 1)
        + word(1, 150, 44, 140, 20, QStringLiteral("间歇灵泉哈姆岛"), 93, 2)
        + word(1, 350, 46, 103, 15, QStringLiteral("2024-09-19"), 91, 3)
        + word(1, 465, 46, 72, 15, QStringLiteral("23:05:12"), 90, 4)
        + word(1, 570, 43, 80, 20, QStringLiteral("无事发生"), 93, 5);
    const QVariantList rows = mr::ScreenshotImportParser::parse(image, tsv, {});
    QCOMPARE(rows.size(), 1);
    QCOMPARE(rows[0].toMap().value(QStringLiteral("duty_level")).toInt(), 90);
    QCOMPARE(rows[0].toMap().value(QStringLiteral("ocr_duty_name")).toString(), QStringLiteral("间歇灵泉哈姆岛"));
    QCOMPARE(rows[0].toMap().value(QStringLiteral("reflection_text")).toString(), QStringLiteral("无事发生"));
    QCOMPARE(rows[0].toMap().value(QStringLiteral("source_rect")).toMap().value(QStringLiteral("height")).toInt(), 93);
}

void ScreenshotImportParserTests::webRowLevelTokenToleratesFurtherMisreads()
{
    // 前缀「Lv」与分隔符齐全时，两位数字都被读成形近字母（Lv.lO）也按等级读取；逗号分隔符照常；
    // 超出副本目录最高等级的「Lvl90」去掉开头的形近字母读作 90。形近读取都提示核对等级。
    QImage image = webPage(1845, 340, {QRect(2, 7, 1829, 92), QRect(2, 115, 1829, 92), QRect(2, 223, 1829, 92)});
    const QByteArray tsv = kTsvHeader
        + rowHead(7, QStringLiteral("Lv.lO"), 1) + word(1, 720, 44, 40, 20, QStringLiteral("一"), 93, 10)
        + rowHead(115, QStringLiteral("Lv,90"), 20) + word(1, 720, 152, 40, 20, QStringLiteral("二"), 93, 30)
        + rowHead(223, QStringLiteral("Lvl90"), 40) + word(1, 720, 260, 40, 20, QStringLiteral("三"), 93, 50);
    const QVariantList rows = mr::ScreenshotImportParser::parse(image, tsv, {});
    QCOMPARE(rows.size(), 3);
    QCOMPARE(rows[0].toMap().value(QStringLiteral("duty_level")).toInt(), 10);
    QVERIFY(hasWarning(rows[0].toMap(), QStringLiteral("等级数字")));
    QCOMPARE(rows[1].toMap().value(QStringLiteral("duty_level")).toInt(), 90);
    QVERIFY(!hasWarning(rows[1].toMap(), QStringLiteral("等级")));
    QCOMPARE(rows[2].toMap().value(QStringLiteral("duty_level")).toInt(), 90);
    QVERIFY(hasWarning(rows[2].toMap(), QStringLiteral("等级数字")));
}

void ScreenshotImportParserTests::unrecognisedWideGoldStrokeDoesNotTrimWebRow()
{
    // 大职业图标的稀疏上沿跨过一组留白取样列，但未被文字 OCR 返回。
    // 第一组扫描仍符合最小行高；必须比较其他留白，保留整行及完整图标。
    QImage image = webPage(1851, 120, {QRect(7, 7, 1829, 92)}, true);
    {
        QPainter painter(&image);
        painter.fillRect(QRect(117, 17, 3, 10), QColor(220, 185, 75));
    }
    paintTypeIcon(image, QRect(219, 32, 34, 34), QColor(15, 180, 230));
    const QByteArray tsv = kTsvHeader
        + inked(image, QRect(144, 40, 50, 27), QStringLiteral("Lv.90"), 1)
        + inked(image, QRect(282, 40, 177, 27), QStringLiteral("合成副本名称"), 2)
        + inked(image, QRect(479, 41, 198, 23), QStringLiteral("2026-10-01 13:10:22"), 3)
        + inked(image, QRect(700, 40, 402, 26), QStringLiteral("保留完整图标"), 4);
    const QVariantList rows = mr::ScreenshotImportParser::parse(image, tsv, {});
    QCOMPARE(rows.size(), 1);
    const QVariantMap rect = rows[0].toMap().value(QStringLiteral("source_rect")).toMap();
    QCOMPARE(rect.value(QStringLiteral("y")).toInt(), 7);
    QCOMPARE(rect.value(QStringLiteral("height")).toInt(), 92);
}

void ScreenshotImportParserTests::neuralTextBoxesAndElevatedTypeIconDoNotSplitWebRows()
{
    // 整段 OCR 框较高，类型图标的顶端高于文字；旧扫描线碰到类型图标后
    // 把行截成短条，来源时间又被手机卡片兜底重复保留，并串到了下一条时间。
    QImage image = webPage(1851, 225, {QRect(7, 7, 1829, 92), QRect(7, 115, 1829, 92)}, true);
    QByteArray tsv = kTsvHeader;
    for (int i = 0; i < 2; ++i) {
        const int top = 7 + i * 108;
        paintTypeIcon(image, QRect(219, top + 25, 34, 34), QColor(15, 180, 230));
        tsv += word(1, 23, top + 19, 87, 53, QStringLiteral("20"), 82, i * 10 + 6);
        tsv += inked(image, QRect(137, top + 32, 57, 30), QStringLiteral("Lv.90"), i * 10 + 1);
        tsv += inked(image, QRect(282, top + 33, 177, 27), QStringLiteral("合成整段副本名称"), i * 10 + 2);
        tsv += inked(image, QRect(479, top + 34, 198, 23),
                     i == 0 ? QStringLiteral("2026-10-01 13:10:22") : QStringLiteral("2026-10-02 22:08:21"), i * 10 + 3);
        tsv += inked(image, QRect(700, top + 33, 402, 26),
                     i == 0 ? QStringLiteral("第一条整段心得") : QStringLiteral("第二条整段心得"), i * 10 + 4);
        paintDeleteMark(image, QRect(1785, top + 39, 15, 15));
        tsv += word(1, 1780, top + 33, 29, 26, QStringLiteral("X"), 82, i * 10 + 5);
    }
    QString error;
    const QVariantList rows = mr::ScreenshotImportParser::parse(image, tsv, {}, &error);
    QVERIFY2(error.isEmpty(), qPrintable(error));
    QCOMPARE(rows.size(), 2);
    for (int i = 0; i < 2; ++i) {
        const QVariantMap row = rows[i].toMap();
        const QVariantMap rect = row.value(QStringLiteral("source_rect")).toMap();
        QVERIFY(rect.value(QStringLiteral("width")).toInt() > 1800);
        QCOMPARE(row.value(QStringLiteral("ocr_duty_name")).toString(), QStringLiteral("合成整段副本名称"));
        QCOMPARE(row.value(QStringLiteral("source_recorded_at")).toString(),
                 i == 0 ? QStringLiteral("2026-10-01 13:10:22") : QStringLiteral("2026-10-02 22:08:21"));
        QCOMPARE(row.value(QStringLiteral("reflection_text")).toString(),
                 i == 0 ? QStringLiteral("第一条整段心得") : QStringLiteral("第二条整段心得"));
    }
}

int main(int argc, char *argv[])
{
    qputenv("QT_QPA_PLATFORM", "offscreen");
    QGuiApplication app(argc, argv);
    ScreenshotImportParserTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "ScreenshotImportParserTests.moc"
