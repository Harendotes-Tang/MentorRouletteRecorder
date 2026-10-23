#include "AppSettings.h"
#include "ReleaseNotesController.h"
#include <QCryptographicHash>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>

#include <QGuiApplication>
#include <QFile>
#include <QSettings>
#include <QStandardPaths>
#include <QTest>

namespace {
class ReleaseNotesTests : public QObject
{
    Q_OBJECT
private Q_SLOTS:
    void init()
    {
        QSettings settings(mr::AppSettings::filePath(), QSettings::IniFormat);
        settings.remove(QStringLiteral("ui/release_notes_seen_version"));
        settings.sync();
    }

    void versionPrecedence_data()
    {
        QTest::addColumn<QString>("current");
        QTest::addColumn<QString>("seen");
        QTest::addColumn<bool>("newer");
        QTest::newRow("first") << "1.7.2" << "" << true;
        QTest::newRow("same") << "1.7.2" << "1.7.2" << false;
        QTest::newRow("upgrade") << "1.7.3" << "1.7.2" << true;
        QTest::newRow("downgrade") << "1.7.1" << "1.7.2" << false;
        QTest::newRow("numeric") << "1.10.0" << "1.9.9" << true;
        QTest::newRow("beta") << "1.8.0-beta.10" << "1.8.0-beta.9" << true;
        QTest::newRow("release") << "1.8.0" << "1.8.0-rc.1" << true;
        QTest::newRow("pre-downgrade") << "1.8.0-beta.1" << "1.8.0" << false;
        QTest::newRow("build") << "1.8.0+new" << "1.8.0+old" << false;
        QTest::newRow("bad-current") << "garbage" << "1.7.2" << false;
        QTest::newRow("bad-seen") << "1.7.2" << "broken" << true;
        QTest::newRow("leading-zero") << "1.8.0-beta.01" << "1.7.2" << false;
    }

    void versionPrecedence()
    {
        QFETCH(QString, current);
        QFETCH(QString, seen);
        QFETCH(bool, newer);
        QCOMPARE(mr::ReleaseNotesController::isNewerVersion(current, seen), newer);
    }

    void exactCurrentSectionOnly()
    {
        const QString changelog = QStringLiteral(
            "# Log\r\n## [Unreleased]\r\nFuture\r\n## [1.7.2] - 2026-10-14\r\n"
            "### Fixes\r\n- Current `fix`\r\n## [1.7.1]\r\nOld\r\n");
        const QString notes = mr::ReleaseNotesController::extractNotes(changelog, "1.7.2");
        QVERIFY(notes.contains("Current fix"));
        QVERIFY(notes.contains("Fixes"));
        QVERIFY(!notes.contains("Future"));
        QVERIFY(!notes.contains("Old"));
        QVERIFY(!notes.contains("###"));
        QVERIFY(mr::ReleaseNotesController::extractNotes(changelog, "1.7.20").isEmpty());
        QVERIFY(mr::ReleaseNotesController::extractNotes(changelog, "1.7.2-beta.1").isEmpty());
        QVERIFY(mr::ReleaseNotesController::extractNotes("## [1.7.2]\n\n## [1.7.1]\nOld", "1.7.2").isEmpty());
    }

    void persistsOnlyOnCloseAndRetainsHighestVersion()
    {
        const QString changelog = QStringLiteral("## [1.7.2]\n- Fix\n## [1.7.3]\n- Next\n");
        mr::AppSettings settings;
        {
            mr::ReleaseNotesController abandoned(&settings, "1.7.2", changelog);
            QVERIFY(abandoned.pending());
        }
        QVERIFY(settings.releaseNotesSeenVersion().isEmpty());
        mr::ReleaseNotesController first(&settings, "1.7.2", changelog);
        first.acknowledge();
        QVERIFY(!first.pending());
        mr::AppSettings reloaded;
        QCOMPARE(reloaded.releaseNotesSeenVersion(), QStringLiteral("1.7.2"));
        mr::ReleaseNotesController same(&reloaded, "1.7.2", changelog);
        QVERIFY(!same.pending());
        mr::ReleaseNotesController upgrade(&reloaded, "1.7.3", changelog);
        QVERIFY(upgrade.pending());
        upgrade.acknowledge();
        mr::ReleaseNotesController downgrade(&reloaded, "1.7.2", changelog);
        QVERIFY(!downgrade.pending());
        downgrade.acknowledge();
        QCOMPARE(reloaded.releaseNotesSeenVersion(), QStringLiteral("1.7.3"));
    }

    void missingNotesNeverAcknowledge()
    {
        mr::AppSettings settings;
        mr::ReleaseNotesController missing(&settings, "1.7.2", "## [Unreleased]\nFuture");
        QVERIFY(!missing.pending());
        missing.acknowledge();
        QVERIFY(settings.releaseNotesSeenVersion().isEmpty());
    }

    void applicationExitDoesNotAcknowledge()
    {
        mr::AppSettings settings;
        mr::ReleaseNotesController notes(&settings, "1.7.2", "## [1.7.2]\nFix");
        QVERIFY(QMetaObject::invokeMethod(qApp, "aboutToQuit", Qt::DirectConnection));
        notes.acknowledge();
        QVERIFY(settings.releaseNotesSeenVersion().isEmpty());
    }

    void structuredEntriesPreserveSectionsContinuationsAndPlainText()
    {
        const QString log = QString::fromUtf8(
            "## [Unreleased]\n- Future\n## [1.7.3-beta.1]\n"
            "### 新增\n- 界面语言：四种语言。`details` <img src='https://invalid'>\n"
            "  第二行保留。\n\n### 修复\n- 搜索：保留原始输入。\n"
            "### 自定义\n非列表说明也不能丢失。\n## [1.7.2]\n- Old\n");
        const auto entries = mr::ReleaseNotesController::parseEntries(log, "1.7.3-beta.1");
        QCOMPARE(entries.size(), 3);
        QCOMPARE(entries[0].toMap().value("category").toString(), QStringLiteral("added"));
        QVERIFY(entries[0].toMap().value("detail").toString().contains("第二行保留。"));
        QVERIFY(entries[0].toMap().value("detail").toString().contains("<img src='https://invalid'>"));
        QVERIFY(!entries[0].toMap().value("detail").toString().contains('`'));
        QCOMPARE(entries[1].toMap().value("category").toString(), QStringLiteral("fixed"));
        QCOMPARE(entries[2].toMap().value("section").toString(), QString::fromUtf8("自定义"));
        QVERIFY(mr::ReleaseNotesController::parseEntries(log, "1.7.3").isEmpty());
    }

    void annotationsRequireExactVersionAndBodyDigest()
    {
        const QString body = QString::fromUtf8("界面语言：真实完整正文。");
        const QString log = QString::fromUtf8("## [1.7.3-beta.1]\n### 新增\n- ") + body;
        QJsonObject annotation{{"bodySha256", QString::fromLatin1(QCryptographicHash::hash(
                                    body.toUtf8(), QCryptographicHash::Sha256).toHex())},
                               {"title", "Curated title"}, {"summary", "Short summary"},
                               {"tag", "Language"}, {"highlight", true},
                               {"action", "language-settings"}};
        QJsonObject presentation{{"version", "1.7.3-beta.1"}, {"entries", QJsonArray{annotation}}};
        const auto parse = [&] (const QString &source) {
            return mr::ReleaseNotesController::parseEntries(source, "1.7.3-beta.1",
                         QJsonDocument(presentation).toJson()).first().toMap();
        };
        auto projected = parse(log);
        QCOMPARE(projected.value("title").toString(), QStringLiteral("Curated title"));
        QCOMPARE(projected.value("detail").toString(), body);
        QVERIFY(projected.value("highlight").toBool());
        QCOMPARE(projected.value("action").toString(), QStringLiteral("language-settings"));
        projected = parse(log + QString::fromUtf8("已修改。"));
        QVERIFY(!projected.value("highlight").toBool());
        QVERIFY(projected.value("title").toString() != QStringLiteral("Curated title"));
        QVERIFY(projected.value("detail").toString().endsWith(QString::fromUtf8("已修改。")));
        presentation["version"] = "1.7.2";
        QVERIFY(!parse(log).value("highlight").toBool());
        presentation["version"] = "1.7.3-beta.1";
        annotation["action"] = "https://example.invalid/";
        presentation["entries"] = QJsonArray{annotation};
        QVERIFY(parse(log).value("action").toString().isEmpty());
    }

    void boldLeadInIsNeitherShownAsMarkupNorRepeated()
    {
        const QString body = QString::fromUtf8("**成就圆环可以跑满多圈。**进度超过目标后，圆环从顶端开始下一圈，**每圈**放彩带。");
        const QString log = QString::fromUtf8("## [1.8.0-beta.1]\n### 新增\n- ") + body
            + QString::fromUtf8("\n### 修复\n- **抓包：**序号回绕后照常记录。\n- **只有粗体的一句。**\n");
        const auto entries = mr::ReleaseNotesController::parseEntries(log, "1.8.0-beta.1");
        QCOMPARE(entries.size(), 3);
        const auto first = entries[0].toMap();
        QCOMPARE(first.value("title").toString(), QString::fromUtf8("成就圆环可以跑满多圈"));
        QCOMPARE(first.value("summary").toString(), QString::fromUtf8("成就圆环可以跑满多圈。"));
        QCOMPARE(first.value("detail").toString(),
                 QString::fromUtf8("进度超过目标后，圆环从顶端开始下一圈，每圈放彩带。"));
        QCOMPARE(entries[1].toMap().value("detail").toString(), QString::fromUtf8("序号回绕后照常记录。"));
        // A bullet that is only its bold lead-in keeps that sentence as the detail.
        QCOMPARE(entries[2].toMap().value("detail").toString(), QString::fromUtf8("只有粗体的一句。"));
        for (const auto &value : entries) {
            const auto entry = value.toMap();
            for (const auto &key : {"title", "summary", "detail"})
                QVERIFY2(!entry.value(QLatin1String(key)).toString().contains(QStringLiteral("**")), key);
        }

        // Bold that is not a whole lead-in, or says more than the summary, stays in the detail.
        const auto kept = mr::ReleaseNotesController::parseEntries(QString::fromUtf8(
            "## [1.8.0-beta.1]\n### 修复\n- **统计浮窗**与结果弹窗合并。\n- **抓包**：序号回绕。\n"
            "- **甲。乙。**丙。\n"), "1.8.0-beta.1");
        QCOMPARE(kept.size(), 3);
        QCOMPARE(kept[0].toMap().value("detail").toString(), QString::fromUtf8("统计浮窗与结果弹窗合并。"));
        QCOMPARE(kept[1].toMap().value("detail").toString(), QString::fromUtf8("抓包：序号回绕。"));
        QCOMPARE(kept[2].toMap().value("detail").toString(), QString::fromUtf8("甲。乙。丙。"));

        // presentation.json keeps addressing the CHANGELOG text as written, markers included.
        QJsonObject annotation{{"bodySha256", QString::fromLatin1(QCryptographicHash::hash(
                                    body.toUtf8(), QCryptographicHash::Sha256).toHex())},
                               {"title", "Curated"}};
        const QJsonObject presentation{{"version", "1.8.0-beta.1"}, {"entries", QJsonArray{annotation}}};
        const auto curated = mr::ReleaseNotesController::parseEntries(
            log, "1.8.0-beta.1", QJsonDocument(presentation).toJson());
        QVERIFY(curated[0].toMap().value("curated").toBool());
        QCOMPARE(curated[0].toMap().value("title").toString(), QStringLiteral("Curated"));
        QVERIFY(!curated[1].toMap().value("curated").toBool());
    }

    void hiddenAnnotationsStayListedButAreNeverHighlighted()
    {
        const QString body = QString::fromUtf8("打包期间源码变化即停止。");
        const QString log = QString::fromUtf8("## [1.8.0-beta.1]\n### 构建与发布\n- ") + body
            + QString::fromUtf8("\n### 新增\n- 浮窗。\n");
        const auto digestOf = [](const QString &text) {
            return QString::fromLatin1(QCryptographicHash::hash(text.toUtf8(), QCryptographicHash::Sha256).toHex());
        };
        const QJsonObject hiddenAnnotation{{"bodySha256", digestOf(body)}, {"title", "Hidden"},
                                           {"hidden", true}, {"highlight", true}};
        const QJsonObject shownAnnotation{{"bodySha256", digestOf(QString::fromUtf8("浮窗。"))},
                                          {"title", "Shown"}, {"highlight", true}};
        const QJsonObject presentation{{"version", "1.8.0-beta.1"},
                                       {"entries", QJsonArray{hiddenAnnotation, shownAnnotation}}};
        const auto entries = mr::ReleaseNotesController::parseEntries(
            log, "1.8.0-beta.1", QJsonDocument(presentation).toJson());
        QCOMPARE(entries.size(), 2);
        QVERIFY(entries[0].toMap().value("hidden").toBool());
        QVERIFY(!entries[0].toMap().value("highlight").toBool());
        QVERIFY(!entries[1].toMap().value("hidden").toBool());
        // The hidden entry does not use up the single highlight.
        QVERIFY(entries[1].toMap().value("highlight").toBool());
    }

    void bundledProjectionContainsEveryCurrentEntry()
    {
        mr::AppSettings settings;
        mr::ReleaseNotesController notes(&settings);
        QFile source(QString::fromUtf8(MR_SOURCE_DIR) + QStringLiteral("/CHANGELOG.md"));
        QVERIFY(source.open(QIODevice::ReadOnly));
        const QString log = QString::fromUtf8(source.readAll());
        const auto unannotated = mr::ReleaseNotesController::parseEntries(log, notes.version());
        QCOMPARE(notes.entries().size(), unannotated.size());
        int highlights = 0;
        int hidden = 0;
        for (qsizetype i = 0; i < unannotated.size(); ++i) {
            const auto entry = notes.entries()[i].toMap();
            QCOMPARE(entry.value("detail"), unannotated[i].toMap().value("detail"));
            QVERIFY(!entry.value("title").toString().isEmpty());
            QVERIFY(!entry.value("summary").toString().isEmpty());
            for (const auto &key : {"title", "summary", "detail"})
                QVERIFY2(!entry.value(QLatin1String(key)).toString().contains(QStringLiteral("**")), key);
            if (entry.value("hidden").toBool())
                ++hidden;
            if (entry.value("highlight").toBool()) {
                ++highlights;
                QCOMPARE(entry.value("action").toString(), QStringLiteral("language-settings"));
            }
        }
        QVERIFY2(hidden < unannotated.size(), "presentation.json must leave at least one entry shown");
        if (notes.version() == QStringLiteral("1.7.3-beta.1")) {
            QCOMPARE(notes.entries().size(), 13);
            QCOMPARE(notes.counts().value("added").toInt(), 5);
            QCOMPARE(notes.counts().value("fixed").toInt(), 6);
            QCOMPARE(notes.counts().value("improved").toInt(), 2);
            QCOMPARE(highlights, 1);
            QVERIFY(notes.prerelease());
        }
        if (notes.version() == QStringLiteral("1.8.0-beta.1")) {
            // Every bullet is curated; the dialog lists 12 and counts the other 18.
            QCOMPARE(notes.entries().size(), 30);
            QCOMPARE(hidden, 18);
            QCOMPARE(notes.counts().value("added").toInt(), 5);
            QCOMPARE(notes.counts().value("improved").toInt(), 3);
            QCOMPARE(notes.counts().value("fixed").toInt(), 4);
            QCOMPARE(notes.counts().size(), 3);
            for (const auto &value : notes.entries())
                QVERIFY(value.toMap().value("curated").toBool());
        }
        if (notes.version() == QStringLiteral("1.7.3-beta.2")) {
            QCOMPARE(notes.entries().size(), 6);
            QCOMPARE(notes.counts().value("added").toInt(), 1);
            QCOMPARE(notes.counts().value("fixed").toInt(), 2);
            QCOMPARE(notes.counts().value("improved").toInt(), 3);
            QCOMPARE(highlights, 1);
            QVERIFY(notes.prerelease());
        }
    }

    void productionResourceMatchesSource()
    {
        mr::AppSettings settings;
        mr::ReleaseNotesController notes(&settings);
        QVERIFY(!notes.version().isEmpty());
        QFile source(QString::fromUtf8(MR_SOURCE_DIR) + QStringLiteral("/CHANGELOG.md"));
        QVERIFY(source.open(QIODevice::ReadOnly));
        const QString expected = mr::ReleaseNotesController::extractNotes(QString::fromUtf8(source.readAll()), notes.version());
        QCOMPARE(notes.text(), expected);
        QVERIFY(!notes.text().contains("## [Unreleased]"));
    }
};
} // namespace

int main(int argc, char **argv)
{
    QStandardPaths::setTestModeEnabled(true);
    QGuiApplication app(argc, argv);
    ReleaseNotesTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "ReleaseNotesTests.moc"
