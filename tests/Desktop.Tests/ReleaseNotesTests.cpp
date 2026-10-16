#include "AppSettings.h"
#include "ReleaseNotesController.h"

#include <QGuiApplication>
#include <QFile>
#include <QSettings>
#include <QStandardPaths>
#include <QTest>

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

int main(int argc, char **argv)
{
    QStandardPaths::setTestModeEnabled(true);
    QGuiApplication app(argc, argv);
    ReleaseNotesTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "ReleaseNotesTests.moc"
