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
#include <QKeyEvent>
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

/// Consent, 开始, and the round running.
bool startRound(EvidenceScene &scene)
{
    if (!QTest::qWaitFor([&scene] { return roundVisible(scene); }) || !scene.tap(QStringLiteral("candidateRoundConsent")))
        return false;
    if (!QTest::qWaitFor([&scene] { return scene.item(QStringLiteral("candidateRoundStartButton"))->isEnabled(); })
        || !scene.click(QStringLiteral("candidateRoundStartButton")))
        return false;
    return QTest::qWaitFor([&scene] { return roundCard(scene)->property("phase").toString() == QLatin1String("running"); });
}

QString answer(const EvidenceScene &scene, const char *key)
{
    return scene.text(QStringLiteral("candidateRoundAnswer_") + QLatin1String(key));
}

bool edited(const EvidenceScene &scene, const char *key)
{
    auto *tag = scene.item(QStringLiteral("candidateRoundEdited_") + QLatin1String(key));
    return tag && tag->isVisible();
}

/// Typed into the field as a tester would: focus, select what is there, type over it.
bool typeAnswer(EvidenceScene &scene, const char *key, const QString &text)
{
    auto *field = scene.item(QStringLiteral("candidateRoundAnswer_") + QLatin1String(key));
    if (!field || !field->isVisible())
        return false;
    scene.window->requestActivate();
    if (!QTest::qWaitForWindowActive(scene.window))
        return false;
    field->forceActiveFocus();
    if (!field->hasActiveFocus() || !QMetaObject::invokeMethod(field, "selectAll"))
        return false;
    // QTest::keyClicks takes a QWidget only, and keyClick(QWindow *) a Latin-1 key: send the
    // key events a keyboard with an input method would, one character each.
    for (const QChar character : text) {
        QKeyEvent press(QEvent::KeyPress, Qt::Key_unknown, Qt::NoModifier, QString(character));
        QKeyEvent release(QEvent::KeyRelease, Qt::Key_unknown, Qt::NoModifier, QString(character));
        QCoreApplication::sendEvent(scene.window, &press);
        QCoreApplication::sendEvent(scene.window, &release);
    }
    return field->property("text").toString() == text;
}

QJsonObject guess(int id, const char *name)
{
    const QJsonValue null(QJsonValue::Null);
    return {{QStringLiteral("id"), id},
            {QStringLiteral("name"), name ? QJsonValue(QString::fromUtf8(name)) : null},
            {QStringLiteral("content_id"), null}};
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

    // 软件识别到的情况: each field shows the software's guess by name (the job by the bundled table
    // when the Collector gave none, the number when nothing names it) and follows the guess until
    // the tester edits it; 结束并导出 sends the four texts, 重新导出 sends them again, 开始 clears them.
    void theGuessesAreShownFollowedUntilEditedAndSent()
    {
        QTemporaryDir target;
        QVERIFY(target.isValid());
        EvidenceScene scene;
        scene.backend.setCandidateRoundFixture(false);
        QVERIFY(scene.open(QStringLiteral("IDLE")));
        scene.app->candidates()->setTargetOverride(target.path());
        QVERIFY(startRound(scene));

        auto *box = scene.item(QStringLiteral("candidateRoundGuesses"));
        QVERIFY(box && box->isVisible());
        QTRY_COMPARE(answer(scene, "first_roulette"), QString::fromUtf8("练级迷宫"));
        QCOMPARE(answer(scene, "job_before"), QString::fromUtf8("骑士"));
        QCOMPARE(answer(scene, "job_after"), QString::fromUtf8("白魔法师"));
        QCOMPARE(answer(scene, "duty"), QString());
        QVERIFY(!scene.item(QStringLiteral("candidateRoundAnswer_duty"))->property("placeholderText").toString().isEmpty());
        for (const char *key : {"first_roulette", "job_before", "job_after", "duty"})
            QVERIFY2(!edited(scene, key), key);

        QVERIFY(typeAnswer(scene, "job_after", QString::fromUtf8("学者")));
        QTRY_VERIFY(edited(scene, "job_after"));
        QVERIFY(!edited(scene, "job_before"));

        // The round goes on: unedited fields follow, the edited one keeps the tester's text.
        const QJsonValue null(QJsonValue::Null);
        scene.backend.setCandidateRoundGuesses({{QStringLiteral("started_at_utc"), QStringLiteral("2026-11-21T08:00:00.000Z")},
                                                {QStringLiteral("first_roulette"), guess(999, nullptr)},
                                                {QStringLiteral("job_before"), guess(21, "战士")},
                                                {QStringLiteral("job_after"), guess(28, "学者")},
                                                {QStringLiteral("duty"), guess(1043, nullptr)}});
        QTRY_COMPARE(answer(scene, "job_before"), QString::fromUtf8("战士"));
        QCOMPARE(answer(scene, "first_roulette"), QString::fromUtf8("编号 999"));
        QCOMPARE(answer(scene, "duty"), QString::fromUtf8("编号 1043"));
        scene.backend.setCandidateRoundGuesses({{QStringLiteral("started_at_utc"), QStringLiteral("2026-11-21T08:00:00.000Z")},
                                                {QStringLiteral("first_roulette"), guess(999, nullptr)},
                                                {QStringLiteral("job_before"), guess(21, "战士")},
                                                {QStringLiteral("job_after"), guess(33, "占星术士")},
                                                {QStringLiteral("duty"), guess(1043, nullptr)}});
        QTRY_VERIFY(scene.app->captureStatus().value(QStringLiteral("candidate_round_guesses")).toMap()
                        .value(QStringLiteral("job_after")).toMap().value(QStringLiteral("id")).toInt() == 33);
        QCOMPARE(answer(scene, "job_after"), QString::fromUtf8("学者"));
        QVERIFY(edited(scene, "job_after"));
        QVERIFY(typeAnswer(scene, "duty", QString::fromUtf8("  天然要害沙斯塔夏溶洞  ")));

        QTRY_VERIFY(scene.item(QStringLiteral("candidateRoundFinishButton"))->isEnabled());
        QVERIFY(scene.click(QStringLiteral("candidateRoundFinishButton")));
        QTRY_COMPARE(roundCard(scene)->property("phase").toString(), QStringLiteral("exported"));
        const QJsonObject sent{{QStringLiteral("first_roulette"), QString::fromUtf8("编号 999")},
                               {QStringLiteral("job_before"), QString::fromUtf8("战士")},
                               {QStringLiteral("job_after"), QString::fromUtf8("学者")},
                               {QStringLiteral("duty"), QString::fromUtf8("天然要害沙斯塔夏溶洞")}};
        QCOMPARE(scene.backend.lastCandidateExportRequest().value(QStringLiteral("round_answers")).toObject(), sent);

        // 重新导出 writes the file again with the same answers.
        scene.app->candidates()->exportEvidence();
        QTRY_VERIFY(!scene.app->candidates()->exporting());
        QCOMPARE(scene.backend.lastCandidateExportRequest().value(QStringLiteral("round_answers")).toObject(), sent);

        // A new round starts from the software's guesses again. The exported card offers no
        // 开始 button, so its start() is called the way the button calls it.
        QVERIFY(roundCard(scene)->property("canStart").toBool());
        QVERIFY(QMetaObject::invokeMethod(roundCard(scene), "start"));
        QTRY_COMPARE(roundCard(scene)->property("phase").toString(), QStringLiteral("running"));
        QVERIFY(scene.app->candidates()->roundAnswers().isEmpty());
        QVERIFY(!edited(scene, "job_after"));
        QTRY_COMPARE(answer(scene, "job_after"), QString::fromUtf8("占星术士"));
        QCOMPARE(scene.warnings->size(), 0);
    }

    // Nothing guessed and nothing written (or an older Collector that reports no guesses): the
    // fields are empty and the export request carries no round_answers at all.
    void anEmptyRoundSendsNoAnswers()
    {
        QTemporaryDir target;
        QVERIFY(target.isValid());
        EvidenceScene scene;
        scene.backend.setCandidateRoundFixture(false);
        QVERIFY(scene.open(QStringLiteral("IDLE")));
        scene.backend.setCandidateRoundGuesses({});
        scene.app->candidates()->setTargetOverride(target.path());
        QVERIFY(startRound(scene));
        QVERIFY(scene.item(QStringLiteral("candidateRoundGuesses"))->isVisible());
        for (const char *key : {"first_roulette", "job_before", "job_after", "duty"})
            QVERIFY2(answer(scene, key).isEmpty(), key);

        QTRY_VERIFY(scene.item(QStringLiteral("candidateRoundFinishButton"))->isEnabled());
        QVERIFY(scene.click(QStringLiteral("candidateRoundFinishButton")));
        QTRY_COMPARE(roundCard(scene)->property("phase").toString(), QStringLiteral("exported"));
        const QJsonObject request = scene.backend.lastCandidateExportRequest();
        QCOMPARE(request.keys(), QStringList{QStringLiteral("target_path")});
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
