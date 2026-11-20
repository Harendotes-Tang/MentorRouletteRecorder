// 第二轮采证 (evidence/EvidenceCandidateRound.qml on the 新区服采证 page): the player's way into the
// candidate validation lane without the maintainer tools. It appears only for a client build the
// Collector has a candidate profile for and no profile that records; 开始 waits for the disclosure
// to be accepted and then whitelists every eligible hypothesis of every group in one request; the
// round and a 新区服采证 round exclude each other; 结束并导出 lists the two files to send.
#include "CandidateReviewController.h"
#include "DesktopTestRunner.h"
#include "EvidenceSceneFixture.h"
#include "QmlComponentTestSupport.h"
#include "TestCollectorGuard.h"

#include <QGuiApplication>
#include <QJsonArray>
#include <QQuickStyle>
#include <QSet>
#include <QStandardPaths>
#include <QTemporaryDir>

namespace {

QQuickItem *roundCard(const EvidenceScene &scene)
{
    return scene.item(QStringLiteral("candidateRoundCard"));
}

bool roundVisible(const EvidenceScene &scene)
{
    auto *card = roundCard(scene);
    return card && card->isVisible();
}

/// The research-eligible opcodes of the fixture's candidate, every group.
QStringList eligibleOpcodes(QSet<QString> *groups)
{
    QStringList opcodes;
    for (const auto &value : mr::MockBackend::candidateRoundHypotheses()) {
        const QJsonObject hypothesis = value.toObject();
        if (!hypothesis.value(QStringLiteral("research_eligible")).toBool())
            continue;
        opcodes.append(hypothesis.value(QStringLiteral("opcode")).toString());
        groups->insert(hypothesis.value(QStringLiteral("group")).toString());
    }
    return opcodes;
}

QStringList strings(const QJsonArray &array)
{
    QStringList values;
    for (const auto &value : array)
        values.append(value.toString());
    return values;
}

class EvidenceCandidateRoundTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void initTestCase()
    {
        mrtest::registerSourceQmlComponents();
    }

    // The ordinary mock reports a CN build with a profile in force: nothing to show. A region
    // nothing records and no candidate describes (TC here) shows nothing either.
    void theRoundIsHiddenWithoutACandidateProfileForTheBuild()
    {
        EvidenceScene scene;
        QVERIFY(scene.open(QStringLiteral("IDLE")));
        QTRY_VERIFY(scene.app->captureSettingsLoaded());
        QTRY_VERIFY(!scene.app->captureStatus().value(QStringLiteral("game_build")).toString().isEmpty());
        QVERIFY(roundCard(scene));
        QVERIFY(!roundVisible(scene));

        EvidenceScene unsupported;
        unsupported.backend.setRecordingFixture(QStringLiteral("waiting"));
        QVERIFY(unsupported.open(QStringLiteral("IDLE")));
        QTRY_VERIFY(unsupported.app->captureSettingsLoaded());
        QVERIFY(!roundVisible(unsupported));
        QCOMPARE(scene.warnings->size(), 0);
    }

    // A GLOBAL client on the candidate's exact build, no maintainer tools: the round is offered,
    // and 开始 stays disabled until the disclosure is accepted.
    void theRoundIsOfferedWithoutMaintainerToolsAndWaitsForConsent()
    {
        EvidenceScene scene;
        scene.backend.setCandidateRoundFixture(false);
        QVERIFY(scene.open(QStringLiteral("IDLE")));
        QVERIFY(!scene.app->maintainerToolsVisible());
        QTRY_VERIFY(roundVisible(scene));
        QVERIFY(scene.item(QStringLiteral("candidateRoundDisclosure"))->isVisible());
        auto *start = scene.item(QStringLiteral("candidateRoundStartButton"));
        QVERIFY(start && start->isVisible());
        QVERIFY(!start->isEnabled());
        QVERIFY(scene.tap(QStringLiteral("candidateRoundConsent")));
        QTRY_VERIFY(roundCard(scene)->property("consent").toBool());
        QTRY_VERIFY(start->isEnabled());
        QCOMPARE(scene.warnings->size(), 0);
    }

    // 开始 turns validation on and whitelists every eligible hypothesis of every group in one
    // request; the round then runs, with its checklist and counts, and the 新区服采证 round
    // cannot start beside it.
    void startingSelectsEveryGroupAndBlocksTheMetadataRound()
    {
        EvidenceScene scene;
        scene.backend.setCandidateRoundFixture(false);
        QVERIFY(scene.open(QStringLiteral("IDLE")));
        QTRY_VERIFY(roundVisible(scene));
        QVERIFY(!scene.sessionValue("candidateRoundRunning").toBool());
        QVERIFY(scene.tap(QStringLiteral("candidateRoundConsent")));
        QTRY_VERIFY(scene.item(QStringLiteral("candidateRoundStartButton"))->isEnabled());
        QVERIFY(scene.click(QStringLiteral("candidateRoundStartButton")));

        QTRY_VERIFY(scene.app->captureSettings().value(QStringLiteral("candidate_validation_enabled")).toBool());
        const QJsonObject request = scene.backend.lastCaptureSettingsUpdate();
        QVERIFY(request.value(QStringLiteral("candidate_validation_enabled")).toBool());
        QSet<QString> groups;
        const QStringList expected = eligibleOpcodes(&groups);
        QCOMPARE(strings(request.value(QStringLiteral("research_payload_opcodes")).toArray()), expected);
        const QSet<QString> everyGroup{QStringLiteral("queue"), QStringLiteral("finder"),
                                       QStringLiteral("identity"), QStringLiteral("zone_load")};
        QVERIFY(groups == everyGroup);

        QTRY_COMPARE(roundCard(scene)->property("phase").toString(), QStringLiteral("running"));
        for (int task = 1; task <= 4; ++task)
            QVERIFY(scene.item(QStringLiteral("candidateRoundTask_%1").arg(task))->isVisible());
        QVERIFY(!scene.item(QStringLiteral("candidateRoundStartButton"))->isVisible());
        // Seen hypotheses are counted by name, the round's own only.
        scene.app->candidates()->observeLiveEvent({{QStringLiteral("name"), QStringLiteral("QUEUE_REGISTRATION")}});
        scene.app->candidates()->observeLiveEvent({{QStringLiteral("name"), QStringLiteral("QUEUE_REGISTRATION")}});
        scene.app->candidates()->observeLiveEvent({{QStringLiteral("name"), QStringLiteral("ZONE_LOAD")}});
        QTRY_COMPARE(roundCard(scene)->property("seenCount").toInt(), 1);
        QVERIFY(scene.text(QStringLiteral("candidateRoundCounts")).startsWith(QString::fromUtf8("已看到 1 / 19 种报文")));

        // The metadata round waits, and says why.
        QTRY_VERIFY(scene.sessionValue("candidateRoundRunning").toBool());
        QVERIFY(!scene.sessionValue("canStart").toBool());
        QVERIFY(!scene.item(QStringLiteral("templateObservationActionButton"))->isEnabled());
        QVERIFY(scene.item(QStringLiteral("evidenceCandidateRoundBlocks"))->isVisible());
        QCOMPARE(scene.warnings->size(), 0);
    }

    // While a 新区服采证 round records, the second round cannot start, and the line says so.
    void theRoundWaitsWhileAMetadataRoundRuns()
    {
        EvidenceScene scene;
        scene.backend.setCandidateRoundFixture(false);
        QVERIFY(scene.open(QStringLiteral("RECORDING")));
        QTRY_VERIFY(scene.app->validationActive());
        QTRY_VERIFY(roundVisible(scene));
        QVERIFY(scene.tap(QStringLiteral("candidateRoundConsent")));
        QTRY_VERIFY(roundCard(scene)->property("consent").toBool());
        QVERIFY(!scene.item(QStringLiteral("candidateRoundStartButton"))->isEnabled());
        auto *reason = scene.item(QStringLiteral("candidateRoundBlockedReason"));
        QVERIFY(reason && reason->isVisible());
        QVERIFY(scene.text(QStringLiteral("candidateRoundBlockedReason")).contains(QString::fromUtf8("新区服采证正在进行")));
        QCOMPARE(scene.warnings->size(), 0);
    }

    // The game must be closed before the round starts: the capture has to see the game's
    // connections from their beginning.
    void theRoundWaitsForTheGameToBeClosed()
    {
        EvidenceScene scene;
        scene.backend.setCandidateRoundFixture(true);
        QVERIFY(scene.open(QStringLiteral("IDLE")));
        QTRY_VERIFY(roundVisible(scene));
        QTRY_VERIFY(scene.app->ffxivRunning());
        QVERIFY(scene.tap(QStringLiteral("candidateRoundConsent")));
        QTRY_VERIFY(roundCard(scene)->property("consent").toBool());
        QVERIFY(!scene.item(QStringLiteral("candidateRoundStartButton"))->isEnabled());
        QVERIFY(scene.text(QStringLiteral("candidateRoundBlockedReason")).contains(QString::fromUtf8("先完全退出游戏")));
        QCOMPARE(scene.warnings->size(), 0);
    }

    // 结束并导出 switches validation off and clears the whitelist, then exports; the page lists
    // the evidence file and its .sha256, and a folder that does not exist is never handed to the shell.
    void finishingExportsAndListsTheFilesToSend()
    {
        QTemporaryDir target;
        QVERIFY(target.isValid());
        EvidenceScene scene;
        scene.backend.setCandidateRoundFixture(false);
        QVERIFY(scene.open(QStringLiteral("IDLE")));
        scene.app->candidates()->setTargetOverride(target.path());
        int opened = 0;
        scene.app->candidates()->setOpenUrl([&opened](const QUrl &) { ++opened; return true; });
        QTRY_VERIFY(roundVisible(scene));
        QVERIFY(scene.tap(QStringLiteral("candidateRoundConsent")));
        QTRY_VERIFY(scene.item(QStringLiteral("candidateRoundStartButton"))->isEnabled());
        QVERIFY(scene.click(QStringLiteral("candidateRoundStartButton")));
        QTRY_COMPARE(roundCard(scene)->property("phase").toString(), QStringLiteral("running"));

        // QTRY_* evaluates its expression once more after it holds: wait first, click once.
        QTRY_VERIFY(scene.item(QStringLiteral("candidateRoundFinishButton"))->isEnabled());
        QVERIFY(scene.click(QStringLiteral("candidateRoundFinishButton")));
        QTRY_COMPARE(roundCard(scene)->property("phase").toString(), QStringLiteral("exported"));
        QVERIFY(!scene.app->captureSettings().value(QStringLiteral("candidate_validation_enabled")).toBool());
        QVERIFY(scene.app->captureSettings().value(QStringLiteral("research_payload_opcodes")).toList().isEmpty());

        const QString evidence = scene.text(QStringLiteral("candidateRoundFile_0"));
        QVERIFY2(evidence.startsWith(QStringLiteral("candidate-evidence-")) && evidence.endsWith(QStringLiteral(".json")),
                 qPrintable(evidence));
        QCOMPARE(scene.text(QStringLiteral("candidateRoundFile_1")), evidence + QStringLiteral(".sha256"));
        QVERIFY(scene.item(QStringLiteral("candidateRoundFile_1"))->isVisible());
        QVERIFY(scene.item(QStringLiteral("candidateRoundOpenFolderButton"))->isVisible());

        // The mock names a folder that does not exist: the page says so instead of opening it.
        QVERIFY(scene.click(QStringLiteral("candidateRoundOpenFolderButton")));
        QCOMPARE(opened, 0);
        QTRY_VERIFY(!scene.app->candidates()->exportError().isEmpty());
        QCOMPARE(scene.warnings->size(), 0);
    }
};

} // namespace

MR_DESKTOP_TEST_SUITE(EvidenceCandidateRound)
{
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication app(argc, argv);
    EvidenceCandidateRoundTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "EvidenceCandidateRoundTests.moc"
