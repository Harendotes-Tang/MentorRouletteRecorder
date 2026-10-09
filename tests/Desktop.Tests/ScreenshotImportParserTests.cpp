#include "ScreenshotImportParser.h"
#include "JobCatalog.h"

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

} // namespace

class ScreenshotImportParserTests final : public QObject
{
    Q_OBJECT

private slots:
    void separatesCardsAndPreservesLineOrder();
    void sparseBlocksOnTheSameRowFormOneTitleAndTimestamp();
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
    // Use a synthetic, distinctive glyph as a local replacement for catalogue id 19.
    // No game artwork or user screenshot becomes a source fixture.
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
    QVERIFY(!rows[0].toMap().value(QStringLiteral("job_candidate_id")).isNull());
    QVERIFY(!rows[0].toMap().value(QStringLiteral("job_candidate_name")).toString().isEmpty());
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

int main(int argc, char *argv[])
{
    qputenv("QT_QPA_PLATFORM", "offscreen");
    QGuiApplication app(argc, argv);
    ScreenshotImportParserTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "ScreenshotImportParserTests.moc"
