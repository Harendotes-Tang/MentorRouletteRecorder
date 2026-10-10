#include "AppSettings.h"
#include <QCoreApplication>
#include <QSignalSpy>
#include <QTemporaryDir>
#include <QTest>

class AppSettingsIsolationTests final : public QObject
{
    Q_OBJECT
private slots:
    void harnessCannotEnableOrDisableRealAutostart_data()
    {
        QTest::addColumn<bool>("screenshot");
        QTest::addColumn<QString>("backend");
        QTest::newRow("mock") << false << QStringLiteral("mock");
        QTest::newRow("screenshot") << true << QStringLiteral("ipc");
    }
    void harnessCannotEnableOrDisableRealAutostart()
    {
        QFETCH(bool, screenshot);
        QFETCH(QString, backend);
        QTemporaryDir directory;
        QVERIFY(directory.isValid());
        settingsAuditRoot = directory.path();
        nativeSettingsAccesses = {};
        QStandardPaths::setTestModeEnabled(mr::AppSettings::isHarnessRun(screenshot, backend));
        mr::AppSettings settings;
        QSignalSpy changed(&settings, &mr::AppSettings::generalChanged);
        // Exercise both directions even when the harness INI holds a previous value.
        settings.setAutostart(true);
        QVERIFY2(nativeSettingsAccesses.isEmpty(), "Harness attempted to open/write the real Run key");
        QVERIFY(!settings.autostart());
        settings.setValue(QStringLiteral("general/autostart"), true);
        settings.setAutostart(false);
        QVERIFY(nativeSettingsAccesses.isEmpty());
        QVERIFY(settings.autostart());
        QVERIFY(!mr::AppSettings::autostartWritable());
        QCOMPARE(changed.size(), 0);
    }
    void normalRunStillRegistersAndRemovesOnlyItsOwnValue()
    {
        QTemporaryDir directory;
        QVERIFY(directory.isValid());
        settingsAuditRoot = directory.path();
        nativeSettingsAccesses = {};
        QStandardPaths::setTestModeEnabled(false);
        mr::AppSettings settings;
#ifdef Q_OS_WIN
        QVERIFY(mr::AppSettings::autostartWritable());
        settings.setAutostart(true);
        QCOMPARE(nativeSettingsAccesses.size(), 2);
        const auto write = nativeSettingsAccesses[1].toObject();
        QCOMPARE(write.value("operation").toString(), QStringLiteral("set"));
        QCOMPARE(write.value("key").toString(), QStringLiteral("MentorRecorder"));
        QCOMPARE(write.value("target").toString(), QStringLiteral("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Run"));
        QCOMPARE(write.value("value").toString(), QStringLiteral("\"%1\"").arg(QDir::toNativeSeparators(QCoreApplication::applicationFilePath())));
        settings.setAutostart(false);
        QCOMPARE(nativeSettingsAccesses.size(), 4);
        QCOMPARE(nativeSettingsAccesses[3].toObject().value("operation").toString(), QStringLiteral("remove"));
        QCOMPARE(nativeSettingsAccesses[3].toObject().value("key").toString(), QStringLiteral("MentorRecorder"));
#else
        QVERIFY(!mr::AppSettings::autostartWritable());
        settings.setAutostart(true);
        QVERIFY(nativeSettingsAccesses.isEmpty());
#endif
        QStandardPaths::setTestModeEnabled(true);
    }
};
QTEST_GUILESS_MAIN(AppSettingsIsolationTests)
#include "AppSettingsIsolationTests.moc"
