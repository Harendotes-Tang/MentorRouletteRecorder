#include "AppSettings.h"
#include "BuildFlavour.h"
#include <QCommandLineParser>
#include <QCoreApplication>
#include <QFileInfo>
#include <QProcess>
#include <QScopeGuard>
#include <QSignalSpy>
#include <QTemporaryDir>
#include <QTest>

namespace {
using mr::build::Flavour;

/// What the distribution registers for this executable: the path, then the
/// positional marker - never an option an older build's parser would refuse.
QString registeredCommandLine()
{
    return QStringLiteral("\"%1\" autostart")
        .arg(QDir::toNativeSeparators(QCoreApplication::applicationFilePath()));
}

class AppSettingsIsolationTests final : public QObject
{
    Q_OBJECT
private slots:
    void init()
    {
        nativeSettingsRefuseWrites() = false;
        nativeSettingsDropWrites() = false;
    }
    void auditForwardsTemporaryIniReadWriteAndRemove()
    {
        QString temporaryPath;
        {
            QTemporaryDir directory;
            QVERIFY(directory.isValid());
            temporaryPath = directory.path();
            settingsAuditRoot() = temporaryPath;
            nativeSettingsAccesses() = {};
            const QString requested = QStringLiteral("audit-only-native-target");
            {
                QSettings settings(requested, QSettings::NativeFormat);
                QCOMPARE(settings.fileName(), QDir(temporaryPath).filePath(QStringLiteral("native.ini")));
                QCOMPARE(settings.value(QStringLiteral("missing"), 17).toInt(), 17);
                settings.setValue(QStringLiteral("audit/key"), QStringLiteral("saved"));
                settings.sync();
                QVERIFY(settings.contains(QStringLiteral("audit/key")));
            }
            {
                QSettings settings(requested, QSettings::NativeFormat);
                QCOMPARE(settings.value(QStringLiteral("audit/key")).toString(), QStringLiteral("saved"));
                settings.remove(QStringLiteral("audit/key"));
                settings.sync();
                QVERIFY(!settings.contains(QStringLiteral("audit/key")));
            }
            QCOMPARE(nativeSettingsAccesses().size(), 4);
            QCOMPARE(nativeSettingsAccesses()[0].toObject().value("operation").toString(), QStringLiteral("open"));
            QCOMPARE(nativeSettingsAccesses()[1].toObject().value("operation").toString(), QStringLiteral("set"));
            QCOMPARE(nativeSettingsAccesses()[1].toObject().value("target").toString(), requested);
            QCOMPARE(nativeSettingsAccesses()[1].toObject().value("value").toString(), QStringLiteral("saved"));
            QCOMPARE(nativeSettingsAccesses()[2].toObject().value("operation").toString(), QStringLiteral("open"));
            QCOMPARE(nativeSettingsAccesses()[3].toObject().value("operation").toString(), QStringLiteral("remove"));
            QCOMPARE(nativeSettingsAccesses()[3].toObject().value("key").toString(), QStringLiteral("audit/key"));
            QVERIFY(QFileInfo::exists(QDir(temporaryPath).filePath(QStringLiteral("native.ini"))));
        }
        QVERIFY(!QFileInfo::exists(temporaryPath));
    }
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
        settingsAuditRoot() = directory.path();
        nativeSettingsAccesses() = {};
        QStandardPaths::setTestModeEnabled(mr::AppSettings::isHarnessRun(screenshot, backend));
        // The distribution's rules: even the flavour that may register keeps a harness run away.
        mr::AppSettings settings(mr::AppSettings::filePath(), Flavour::Distribution);
        QSignalSpy changed(&settings, &mr::AppSettings::generalChanged);
        // Exercise both directions even when the harness INI holds a previous value.
        settings.setAutostart(true);
        QVERIFY2(nativeSettingsAccesses().isEmpty(), "Harness attempted to open/write the real Run key");
        QVERIFY(!settings.autostart());
        settings.setValue(QStringLiteral("general/autostart"), true);
        settings.setAutostart(false);
        settings.refreshAutostartRegistration();
        QVERIFY(nativeSettingsAccesses().isEmpty());
        QVERIFY(settings.autostart());
        QVERIFY(!settings.autostartWritable());
#ifdef Q_OS_WIN
        QCOMPARE(settings.autostartUnavailableReason(), QStringLiteral("harness"));
#endif
        QCOMPARE(changed.size(), 0);
    }
    // Rule 9: the account's one login Run value belongs to the installed copy. A development
    // build never registers itself and never rewrites or removes that value, whatever its own
    // desktop.ini says - it does not even open the key.
    void aDevelopmentBuildNeverOpensTheRunValue()
    {
        QTemporaryDir directory;
        QVERIFY(directory.isValid());
        settingsAuditRoot() = directory.path();
        nativeSettingsAccesses() = {};
        QStandardPaths::setTestModeEnabled(false);
        const auto restore = qScopeGuard([] { QStandardPaths::setTestModeEnabled(true); });
        {
            mr::AppSettings settings(mr::AppSettings::filePath(), Flavour::Development);
            QSignalSpy changed(&settings, &mr::AppSettings::generalChanged);
            QVERIFY(!settings.autostartWritable());
#ifdef Q_OS_WIN
            QCOMPARE(settings.autostartUnavailableReason(), QStringLiteral("development"));
#endif
            settings.setAutostart(true);
            QVERIFY(!settings.autostart());
            // A development desktop.ini switched on by an earlier build changes nothing.
            settings.setValue(QStringLiteral("general/autostart"), true);
            settings.refreshAutostartRegistration();
            settings.setAutostart(false);
            QVERIFY(settings.autostart());
            QVERIFY2(nativeSettingsAccesses().isEmpty(), "a development build opened the Run key");
            QVERIFY(!settings.autostartRegistrationFailed());
            QCOMPARE(changed.size(), 0);
        }
        // This binary, in the flavour it was compiled as: a development test binary is held to
        // the same rule through the default constructor the program uses.
        mr::AppSettings thisBuild;
        thisBuild.setValue(QStringLiteral("general/autostart"), true);
        if (mr::build::isDevelopmentBuild()) {
            QVERIFY(!thisBuild.autostartWritable());
            thisBuild.refreshAutostartRegistration();
            thisBuild.setAutostart(false);
            QVERIFY2(nativeSettingsAccesses().isEmpty(), "this development binary opened the Run key");
        }
    }
    void normalRunStillRegistersAndRemovesOnlyItsOwnValue()
    {
        QTemporaryDir directory;
        QVERIFY(directory.isValid());
        settingsAuditRoot() = directory.path();
        nativeSettingsAccesses() = {};
        QStandardPaths::setTestModeEnabled(false);
        mr::AppSettings settings(mr::AppSettings::filePath(), Flavour::Distribution);
#ifdef Q_OS_WIN
        QVERIFY(settings.autostartWritable());
        QVERIFY(settings.autostartUnavailableReason().isEmpty());
        settings.setAutostart(true);
        QCOMPARE(nativeSettingsAccesses().size(), 2);
        const auto write = nativeSettingsAccesses()[1].toObject();
        QCOMPARE(write.value("operation").toString(), QStringLiteral("set"));
        QCOMPARE(write.value("key").toString(), QStringLiteral("MentorRecorder"));
        QCOMPARE(write.value("target").toString(), QStringLiteral("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Run"));
        // The switch keeps a login start in the tray (main.cpp), through a positional marker.
        QCOMPARE(write.value("value").toString(), registeredCommandLine());
        QCOMPARE(mr::AppSettings::autostartArgument(), QStringLiteral("autostart"));
        QVERIFY(settings.autostart());
        QVERIFY(!settings.autostartRegistrationFailed());
        settings.setAutostart(false);
        QCOMPARE(nativeSettingsAccesses().size(), 4);
        QCOMPARE(nativeSettingsAccesses()[3].toObject().value("operation").toString(), QStringLiteral("remove"));
        QCOMPARE(nativeSettingsAccesses()[3].toObject().value("key").toString(), QStringLiteral("MentorRecorder"));
        QVERIFY(!settings.autostart());
#else
        QVERIFY(!settings.autostartWritable());
        settings.setAutostart(true);
        QVERIFY(nativeSettingsAccesses().isEmpty());
#endif
        QStandardPaths::setTestModeEnabled(true);
    }
    void refreshRewritesTheRunValueOnlyWhileAutostartIsOn()
    {
        QTemporaryDir directory;
        QVERIFY(directory.isValid());
        settingsAuditRoot() = directory.path();
        nativeSettingsAccesses() = {};
        QStandardPaths::setTestModeEnabled(false);
        mr::AppSettings settings(mr::AppSettings::filePath(), Flavour::Distribution);
        // Off: a start-up refresh must not recreate a value the player removed.
        settings.setValue(QStringLiteral("general/autostart"), false);
        settings.refreshAutostartRegistration();
        QVERIFY2(nativeSettingsAccesses().isEmpty(), "refresh wrote the Run key while autostart is off");
#ifdef Q_OS_WIN
        // On: an installation registered before the marker existed gets it.
        {
            QSettings run(QStringLiteral("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Run"),
                          QSettings::NativeFormat);
            run.setValue(QStringLiteral("MentorRecorder"), QStringLiteral("\"%1\"").arg(
                QDir::toNativeSeparators(QCoreApplication::applicationFilePath())));
        }
        nativeSettingsAccesses() = {};
        settings.setValue(QStringLiteral("general/autostart"), true);
        settings.refreshAutostartRegistration();
        QCOMPARE(nativeSettingsAccesses().size(), 2);
        const auto write = nativeSettingsAccesses()[1].toObject();
        QCOMPARE(write.value("operation").toString(), QStringLiteral("set"));
        QCOMPARE(write.value("key").toString(), QStringLiteral("MentorRecorder"));
        QCOMPARE(write.value("value").toString(), registeredCommandLine());
        QVERIFY(settings.autostart());
        // Already this copy's command line: read, never rewritten.
        nativeSettingsAccesses() = {};
        settings.refreshAutostartRegistration();
        QCOMPARE(nativeSettingsAccesses().size(), 1);
        QCOMPARE(nativeSettingsAccesses()[0].toObject().value("operation").toString(), QStringLiteral("open"));
        // The option form unreleased builds wrote is brought to the positional one.
        {
            QSettings run(QStringLiteral("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Run"),
                          QSettings::NativeFormat);
            run.setValue(QStringLiteral("MentorRecorder"), QStringLiteral("\"%1\" --autostart").arg(
                QDir::toNativeSeparators(QCoreApplication::applicationFilePath())));
        }
        nativeSettingsAccesses() = {};
        settings.refreshAutostartRegistration();
        QCOMPARE(nativeSettingsAccesses().size(), 2);
        QCOMPARE(nativeSettingsAccesses()[1].toObject().value("value").toString(), registeredCommandLine());
#else
        settings.setValue(QStringLiteral("general/autostart"), true);
        settings.refreshAutostartRegistration();
        QVERIFY(nativeSettingsAccesses().isEmpty());
#endif
        QStandardPaths::setTestModeEnabled(true);
    }
    // A Run value Windows did not accept (security software guarding the key, a policy) is
    // never shown as a working 开机启动: the switch stays where it was and the row says why.
    void aRefusedRunValueIsReportedAndLeavesTheSwitchAsItWas()
    {
#ifndef Q_OS_WIN
        QSKIP("The Run value is a Windows registry value.");
#else
        QTemporaryDir directory;
        QVERIFY(directory.isValid());
        settingsAuditRoot() = directory.path();
        nativeSettingsAccesses() = {};
        QStandardPaths::setTestModeEnabled(false);
        const auto restore = qScopeGuard([] {
            nativeSettingsRefuseWrites() = false;
            QStandardPaths::setTestModeEnabled(true);
        });
        mr::AppSettings settings(mr::AppSettings::filePath(), Flavour::Distribution);
        QSignalSpy changed(&settings, &mr::AppSettings::generalChanged);

        nativeSettingsRefuseWrites() = true;
        settings.setAutostart(true);
        QVERIFY(!settings.autostart());
        QVERIFY(settings.autostartRegistrationFailed());
        QCOMPARE(changed.size(), 1);

        // Allowed again: the next try registers and clears the report.
        nativeSettingsRefuseWrites() = false;
        settings.setAutostart(true);
        QVERIFY(settings.autostart());
        QVERIFY(!settings.autostartRegistrationFailed());
        QCOMPARE(changed.size(), 2);

        // A refused removal leaves the value - and so the switch - on.
        nativeSettingsRefuseWrites() = true;
        settings.setAutostart(false);
        QVERIFY(settings.autostart());
        QVERIFY(settings.autostartRegistrationFailed());
        QCOMPARE(changed.size(), 3);

        // A refused start-up refresh is reported too; the player's choice is kept.
        nativeSettingsRefuseWrites() = false;
        {
            QSettings run(QStringLiteral("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Run"),
                          QSettings::NativeFormat);
            run.setValue(QStringLiteral("MentorRecorder"), QStringLiteral("\"C:\\Moved\\MentorRecorder.Desktop.exe\""));
        }
        mr::AppSettings restarted(mr::AppSettings::filePath(), Flavour::Distribution);
        QSignalSpy restartedChanged(&restarted, &mr::AppSettings::generalChanged);
        nativeSettingsRefuseWrites() = true;
        restarted.refreshAutostartRegistration();
        QVERIFY(restarted.autostart());
        QVERIFY(restarted.autostartRegistrationFailed());
        QCOMPARE(restartedChanged.size(), 1);
        nativeSettingsRefuseWrites() = false;
        // A later start that gets through clears the report.
        restarted.refreshAutostartRegistration();
        QVERIFY(!restarted.autostartRegistrationFailed());
        QCOMPARE(restartedChanged.size(), 2);
#endif
    }
    // Some guards report success and drop the change anyway; the value is read back.
    void aSilentlyDroppedRunValueIsReportedToo()
    {
#ifndef Q_OS_WIN
        QSKIP("The Run value is a Windows registry value.");
#else
        QTemporaryDir directory;
        QVERIFY(directory.isValid());
        settingsAuditRoot() = directory.path();
        nativeSettingsAccesses() = {};
        QStandardPaths::setTestModeEnabled(false);
        const auto restore = qScopeGuard([] {
            nativeSettingsDropWrites() = false;
            QStandardPaths::setTestModeEnabled(true);
        });
        mr::AppSettings settings(mr::AppSettings::filePath(), Flavour::Distribution);

        nativeSettingsDropWrites() = true;
        settings.setAutostart(true);
        QVERIFY(!settings.autostart());
        QVERIFY(settings.autostartRegistrationFailed());
        QCOMPARE(nativeSettingsAccesses().size(), 2); // the write was attempted

        nativeSettingsDropWrites() = false;
        settings.setAutostart(true);
        QVERIFY(settings.autostart());
        QVERIFY(!settings.autostartRegistrationFailed());

        nativeSettingsDropWrites() = true;
        settings.setAutostart(false);
        QVERIFY(settings.autostart());
        QVERIFY(settings.autostartRegistrationFailed());

        // The start-up refresh of a moved copy, dropped: reported, the choice kept.
        nativeSettingsDropWrites() = false;
        {
            QSettings run(QStringLiteral("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Run"),
                          QSettings::NativeFormat);
            run.setValue(QStringLiteral("MentorRecorder"), QStringLiteral("\"C:\\Moved\\MentorRecorder.Desktop.exe\""));
        }
        mr::AppSettings restarted(mr::AppSettings::filePath(), Flavour::Distribution);
        nativeSettingsDropWrites() = true;
        restarted.refreshAutostartRegistration();
        QVERIFY(restarted.autostart());
        QVERIFY(restarted.autostartRegistrationFailed());
#endif
    }
    // A player who goes back to 1.8.x keeps a working login start: that build's parser knows
    // no "autostart" option and treats an unknown option as fatal, but ignores a positional
    // argument. This build reads both the marker and the option form (review I1-3).
    void theRegisteredCommandLineIsAcceptedByOlderBuilds()
    {
        const QStringList registered = QProcess::splitCommand(registeredCommandLine());
        QCOMPARE(registered.size(), 2);
        QCOMPARE(registered.constFirst(), QDir::toNativeSeparators(QCoreApplication::applicationFilePath()));

        QCommandLineParser older; // 1.8.x: help, version and options none of which is "autostart"
        older.addHelpOption();
        older.addVersionOption();
        older.addOption(QCommandLineOption(QStringLiteral("backend"), QString(), QStringLiteral("mode")));
        QVERIFY2(older.parse(registered), qPrintable(older.errorText()));

        QCommandLineParser current;
        const QCommandLineOption autostartOption(QStringLiteral("autostart"));
        current.addOption(autostartOption);
        QVERIFY(current.parse(registered));
        QVERIFY(mr::AppSettings::isAutostartLaunch(current.positionalArguments(),
                                                   current.isSet(autostartOption)));
        QVERIFY(current.parse({QStringLiteral("exe"), QStringLiteral("--autostart")}));
        QVERIFY(mr::AppSettings::isAutostartLaunch(current.positionalArguments(),
                                                   current.isSet(autostartOption)));
        QVERIFY(current.parse({QStringLiteral("exe")}));
        QVERIFY(!mr::AppSettings::isAutostartLaunch(current.positionalArguments(),
                                                    current.isSet(autostartOption)));
    }
    // main() decides --screenshot from the raw arguments before the application exists; it
    // must agree with the parser, which also takes "--screenshot=FILE" (review I1-6).
    void theRawScanAcceptsBothSpellingsOfAValuedOption()
    {
        const QString screenshot = QStringLiteral("--screenshot");
        QVERIFY(mr::AppSettings::hasRawOption({QStringLiteral("exe"), screenshot, QStringLiteral("out.png")}, screenshot));
        QVERIFY(mr::AppSettings::hasRawOption({QStringLiteral("exe"), QStringLiteral("--screenshot=out.png")}, screenshot));
        QVERIFY(!mr::AppSettings::hasRawOption({QStringLiteral("exe"), QStringLiteral("--screenshot-size"),
                                                QStringLiteral("800x600")}, screenshot));
        QVERIFY(!mr::AppSettings::hasRawOption({QStringLiteral("exe"), QStringLiteral("--screenshot-delay=50")}, screenshot));
        // The program name is not an argument.
        QVERIFY(!mr::AppSettings::hasRawOption({screenshot}, screenshot));
        QVERIFY(!mr::AppSettings::hasRawOption({QStringLiteral("exe"), QStringLiteral("--backend"), QStringLiteral("ipc")},
                                               screenshot));
    }
    // The start-up step main() takes once the whole command line was accepted: only the
    // account's one instance of a real run brings the Run value up to date. A second copy that
    // only wakes the first, a harness run and a development build leave it alone - the last two
    // without opening the key (review I5-3, J-1, RV3-4).
    void theStartUpRefreshRunsOnlyForThePrimaryInstanceOfARealRun()
    {
#ifndef Q_OS_WIN
        QSKIP("The Run value is a Windows registry value.");
#else
        QTemporaryDir directory;
        QVERIFY(directory.isValid());
        settingsAuditRoot() = directory.path();
        QStandardPaths::setTestModeEnabled(false);
        const auto restore = qScopeGuard([] { QStandardPaths::setTestModeEnabled(true); });
        const QString moved = QStringLiteral("\"C:\\Moved\\MentorRecorder.Desktop.exe\"");
        const auto registerMoved = [&moved] {
            QSettings run(QStringLiteral("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Run"),
                          QSettings::NativeFormat);
            run.setValue(QStringLiteral("MentorRecorder"), moved);
            nativeSettingsAccesses() = {};
        };
        mr::AppSettings settings(mr::AppSettings::filePath(), Flavour::Distribution);
        settings.setValue(QStringLiteral("general/autostart"), true);

        // A second copy: the first instance owns the login start.
        registerMoved();
        QVERIFY(!settings.refreshAutostartRegistrationAtStartUp(/*primaryInstance=*/false));
        QVERIFY2(nativeSettingsAccesses().isEmpty(), "a second instance looked at the Run value");

        // The account's one instance of a real run: brought up to date.
        QVERIFY(settings.refreshAutostartRegistrationAtStartUp(/*primaryInstance=*/true));
        QCOMPARE(nativeSettingsAccesses().size(), 2);
        QCOMPARE(nativeSettingsAccesses()[1].toObject().value("operation").toString(), QStringLiteral("set"));
        QCOMPARE(nativeSettingsAccesses()[1].toObject().value("value").toString(), registeredCommandLine());

        // 开机启动 off: nothing to bring up to date, nothing opened.
        registerMoved();
        settings.setValue(QStringLiteral("general/autostart"), false);
        QVERIFY(!settings.refreshAutostartRegistrationAtStartUp(true));
        QVERIFY(nativeSettingsAccesses().isEmpty());
        settings.setValue(QStringLiteral("general/autostart"), true);

        // A development build, even as the primary instance: the key is never opened.
        mr::AppSettings development(mr::AppSettings::filePath(), Flavour::Development);
        QVERIFY(!development.refreshAutostartRegistrationAtStartUp(true));
        QVERIFY2(nativeSettingsAccesses().isEmpty(), "a development build opened the Run key");

        // A harness run (Qt test mode), whatever main() would pass.
        QStandardPaths::setTestModeEnabled(true);
        QVERIFY(!settings.refreshAutostartRegistrationAtStartUp(true));
        QVERIFY2(nativeSettingsAccesses().isEmpty(), "a harness run opened the Run key");
#endif
    }
};
} // namespace
QTEST_GUILESS_MAIN(AppSettingsIsolationTests)
#include "AppSettingsIsolationTests.moc"
