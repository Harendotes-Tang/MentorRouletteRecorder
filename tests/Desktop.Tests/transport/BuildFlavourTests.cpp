// ---------------------------------------------------------------------------
// Development build versus distribution build (BuildFlavour.h).
//
// Every rule takes the flavour as a parameter, so both flavours are asserted
// here in one binary whichever way this test was compiled: the data root, the
// pipe name, the child's command line and environment, the version label. The
// last slots then pin this build's own answers to the CMake definition it was
// compiled with, so a packaging guard is never the only thing that knows.
// ---------------------------------------------------------------------------

#include "DesktopTestRunner.h"
#include "TestCollectorGuard.h"
#include "AppSettings.h"
#include "BuildFlavour.h"
#include "CollectorProcess.h"
#include "PipeName.h"
#include "SingleInstanceGuard.h"
#include "UpdateController.h"

#include <QCoreApplication>
#include <QDir>
#include <QProcessEnvironment>
#include <QRegularExpression>
#include <QStandardPaths>
#include <QTest>

#include <cstring>

using mr::build::Flavour;

class BuildFlavourTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void theDataFolderIsTheInstalledOneOnlyForADistributionBuild()
    {
        QCOMPARE(mr::build::dataFolderName(Flavour::Distribution),
                 QStringLiteral("MentorRecorder"));
        QCOMPARE(mr::build::dataFolderName(Flavour::Development),
                 QStringLiteral("MentorRecorder-dev"));
        QCOMPARE(mr::build::dataFolderName(), mr::build::dataFolderName(mr::build::thisBuild()));
    }

    /// The Collector's data root: MR_DATA_DIR wins in both flavours; without it
    /// each flavour has its own folder under %LOCALAPPDATA%.
    void theDataRootFollowsTheFlavourUnlessMrDataDirIsSet()
    {
        const QString local = QDir::cleanPath(QDir::tempPath());
        QCOMPARE(mr::CollectorProcess::resolveDataDirectory(QString(), local,
                                                            Flavour::Distribution),
                 QDir(local).absoluteFilePath(QStringLiteral("MentorRecorder")));
        QCOMPARE(mr::CollectorProcess::resolveDataDirectory(QString(), local,
                                                            Flavour::Development),
                 QDir(local).absoluteFilePath(QStringLiteral("MentorRecorder-dev")));

        const QString elsewhere = QDir::cleanPath(QDir::homePath()) + QStringLiteral("/mr-data");
        for (const Flavour flavour : {Flavour::Distribution, Flavour::Development}) {
            // An explicit override wins, trailing separator and all.
            QCOMPARE(mr::CollectorProcess::resolveDataDirectory(elsewhere + QLatin1Char('/'),
                                                                local, flavour),
                     elsewhere);
            // Blank means unset, as in the Collector's own DatabasePaths.ResolveRoot.
            QCOMPARE(mr::CollectorProcess::resolveDataDirectory(QStringLiteral("   "), local,
                                                                flavour),
                     QDir(local).absoluteFilePath(mr::build::dataFolderName(flavour)));
            // Nothing to build the path from: no answer rather than a relative guess.
            QVERIFY(mr::CollectorProcess::resolveDataDirectory(QString(), QString(), flavour)
                        .isEmpty());
        }
    }

    /// What a build adds to the fixed "--serve --parent-pid ..." command line and
    /// to the child's environment: a development build names its pipe and hands
    /// over its data root, a distribution build adds nothing at all.
    void theChildIsIsolatedOnlyByADevelopmentBuild()
    {
        const QString pipe = QStringLiteral("MentorRecorder.0123.dev.v1");
        QCOMPARE(mr::CollectorProcess::isolationArguments(Flavour::Development, pipe),
                 QStringList({QStringLiteral("--pipe"), pipe}));
        QVERIFY(mr::CollectorProcess::isolationArguments(Flavour::Distribution, pipe).isEmpty());
        // Never "--pipe" with nothing behind it: the Collector refuses that command line.
        QVERIFY(mr::CollectorProcess::isolationArguments(Flavour::Development, QString())
                    .isEmpty());

        QProcessEnvironment inherited;
        inherited.insert(QStringLiteral("MR_SOMETHING_ELSE"), QStringLiteral("kept"));
        const QString root =
            QDir::cleanPath(QDir::tempPath()) + QStringLiteral("/MentorRecorder-dev");

        const QProcessEnvironment development =
            mr::CollectorProcess::childEnvironment(Flavour::Development, root, inherited);
        QCOMPARE(development.value(QStringLiteral("MR_DATA_DIR")),
                 QDir::toNativeSeparators(root));
        QCOMPARE(development.value(QStringLiteral("MR_SOMETHING_ELSE")), QStringLiteral("kept"));

        const QProcessEnvironment distribution =
            mr::CollectorProcess::childEnvironment(Flavour::Distribution, root, inherited);
        QVERIFY(!distribution.contains(QStringLiteral("MR_DATA_DIR")));
        QCOMPARE(distribution.toStringList(), inherited.toStringList());

        // Nothing to hand over means nothing is overridden; the child then applies
        // its own rule rather than an empty MR_DATA_DIR.
        QVERIFY(!mr::CollectorProcess::childEnvironment(Flavour::Development, QString(),
                                                        inherited)
                     .contains(QStringLiteral("MR_DATA_DIR")));
    }

    void thePipeNameCarriesADevSegmentOnlyForADevelopmentBuild()
    {
        const QString sid = QStringLiteral("S-1-5-21-1111111111-2222222222-3333333333-1001");
        const QString distribution = mr::ipc::pipeNameForSid(sid, Flavour::Distribution);
        const QString development = mr::ipc::pipeNameForSid(sid, Flavour::Development);

        // The distribution name is the contract with the Collector's PipeNaming.ForSid.
        QCOMPARE(distribution, mr::ipc::pipeNameForSid(sid));

        const QRegularExpression distributionShape(
            QStringLiteral("^MentorRecorder\\.([0-9a-f]{32})\\.v1$"));
        const QRegularExpression developmentShape(
            QStringLiteral("^MentorRecorder\\.([0-9a-f]{32})\\.dev\\.v1$"));
        const QRegularExpressionMatch distributionMatch = distributionShape.match(distribution);
        const QRegularExpressionMatch developmentMatch = developmentShape.match(development);
        QVERIFY2(distributionMatch.hasMatch(), qPrintable(distribution));
        QVERIFY2(developmentMatch.hasMatch(), qPrintable(development));
        // Same user, same hash: only the segment differs.
        QCOMPARE(developmentMatch.captured(1), distributionMatch.captured(1));
        QVERIFY(development != distribution);

        // Still a name the Collector's --pipe accepts: no separators, no
        // whitespace, at most 200 characters (CommandLineOptions.ParsePipeName).
        QVERIFY(development.size() <= 200);
        QVERIFY(!development.contains(QRegularExpression(QStringLiteral("[\\\\/\\s]"))));

        // An unreadable SID yields no name in either flavour, never one with an empty hash.
        QVERIFY(mr::ipc::pipeNameForSid(QString(), Flavour::Distribution).isEmpty());
        QVERIFY(mr::ipc::pipeNameForSid(QString(), Flavour::Development).isEmpty());
    }

    void theVersionLabelEndsInDevOnlyForADevelopmentBuild()
    {
        QCOMPARE(mr::build::versionLabel(QStringLiteral("1.7.3"), Flavour::Distribution),
                 QStringLiteral("1.7.3"));
        QCOMPARE(mr::build::versionLabel(QStringLiteral("1.7.3"), Flavour::Development),
                 QStringLiteral("1.7.3 dev"));
        QCOMPARE(mr::build::versionLabel(QStringLiteral("1.8.0-beta.1"), Flavour::Development),
                 QStringLiteral("1.8.0-beta.1 dev"));
        // The label is MR_APP_VERSION - what the update check compares - plus
        // the marker, and nothing else.
        QVERIFY(!mr::UpdateController::currentVersion().isEmpty());
        QCOMPARE(mr::build::versionLabel(),
                 mr::build::versionLabel(mr::UpdateController::currentVersion(),
                                         mr::build::thisBuild()));
    }

    // -- this build ----------------------------------------------------------

    void thisBuildIsTheFlavourItWasCompiledAs()
    {
#ifdef MR_DEV_BUILD
        QCOMPARE(int(mr::build::thisBuild()), int(Flavour::Development));
        QVERIFY(mr::build::isDevelopmentBuild());
        QVERIFY2(mr::build::versionLabel().endsWith(QStringLiteral(" dev")),
                 qPrintable(mr::build::versionLabel()));
#else
        QCOMPARE(int(mr::build::thisBuild()), int(Flavour::Distribution));
        QVERIFY(!mr::build::isDevelopmentBuild());
        QVERIFY2(!mr::build::versionLabel().endsWith(QStringLiteral(" dev")),
                 qPrintable(mr::build::versionLabel()));
#endif
    }

    /// scripts/package.ps1 reads these bytes out of the executable; they must
    /// say what the code does.
    void theFlavourStampSaysWhatThisBuildDoes()
    {
        const char *stamp = mr::build::flavourStamp();
        QVERIFY(stamp != nullptr);
        const QByteArray text(stamp);
        QVERIFY(text.startsWith("MR_BUILD_FLAVOUR="));
        QCOMPARE(text, mr::build::isDevelopmentBuild()
                           ? QByteArrayLiteral("MR_BUILD_FLAVOUR=development")
                           : QByteArrayLiteral("MR_BUILD_FLAVOUR=distribution"));
        // The scan looks for the text followed by NUL: the whole string, nothing appended.
        QCOMPARE(std::strlen(stamp), std::size_t(text.size()));
    }

    /// Everything derived from the pipe name follows this build's flavour, and so
    /// does the settings file.
    void thisBuildsPipeLeaseAndSettingsFollowItsFlavour()
    {
        const QString sid = mr::ipc::currentUserSidString();
        if (sid.isEmpty())
            QSKIP("The user's SID is not readable here.");
        const QString pipe = mr::ipc::currentUserPipeName();
        QCOMPARE(pipe, mr::ipc::pipeNameForSid(sid, mr::build::thisBuild()));
        QCOMPARE(pipe.endsWith(QStringLiteral(".dev.v1")), mr::build::isDevelopmentBuild());
        QCOMPARE(mr::ipc::currentUserServerName(), mr::ipc::localSocketServerName(pipe));
        QCOMPARE(mr::SingleInstanceGuard::defaultKey(), pipe + QStringLiteral(".desktop"));
        QCOMPARE(mr::CollectorProcess::currentStopEventName(),
                 mr::CollectorProcess::stopEventName(pipe));

        // desktop.ini sits in the flavour's own folder (under QStandardPaths'
        // test root here), never in the other flavour's.
        QVERIFY(QStandardPaths::isTestModeEnabled());
        const QString settings = QDir::fromNativeSeparators(mr::AppSettings::filePath());
        QVERIFY2(settings.endsWith(QLatin1Char('/') + mr::build::dataFolderName()
                                   + QStringLiteral("/desktop.ini")),
                 qPrintable(settings));
        const Flavour other =
            mr::build::isDevelopmentBuild() ? Flavour::Distribution : Flavour::Development;
        QVERIFY(!settings.endsWith(QLatin1Char('/') + mr::build::dataFolderName(other)
                                   + QStringLiteral("/desktop.ini")));
    }
};

MR_DESKTOP_TEST_SUITE(BuildFlavour)
{
    // Never run against the user's own Collector, database or serve lease - see
    // TestCollectorGuard.h. Must precede any construction.
    mrtest::disableCollectorLaunch();
    QCoreApplication app(argc, argv);
    // AppSettings::filePath() must resolve under QStandardPaths' test root, not
    // the user's real %LOCALAPPDATA%.
    QStandardPaths::setTestModeEnabled(true);
    BuildFlavourTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "BuildFlavourTests.moc"
