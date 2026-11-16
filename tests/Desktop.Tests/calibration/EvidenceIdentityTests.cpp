// 角色识别采证 (evidence/EvidencePage.qml with EvidenceSession.purpose "identity"): the page
// follows the Collector's identity_probe, lists the five steps of the round instead of the
// events of a run and takes them in turn, asks again for a step that named nobody, shows and
// reports the verdict, and has no file to tick off once the round is over.
#include "DesktopTestRunner.h"
#include "EvidenceSceneFixture.h"
#include "QmlComponentTestSupport.h"
#include "TestCollectorGuard.h"

#include <QGuiApplication>
#include <QJsonArray>
#include <QQuickStyle>
#include <QStandardPaths>

namespace {

/// A scripted round that is a guided identity round, with the steps the Collector has read.
QJsonObject identityStatus(const QString &state, const QString &result, const QJsonArray &steps,
                           const QString &trace = {})
{
    QJsonObject status = roundStatus(state, QStringLiteral("identity-round"), 1842, 120, trace);
    status.insert(QStringLiteral("identity_probe"), true);
    status.insert(QStringLiteral("identity_result"), result);
    status.insert(QStringLiteral("identity_steps"), steps);
    return status;
}

QJsonObject step(const char *marker, bool named)
{
    return {{QStringLiteral("marker"), QLatin1String(marker)}, {QStringLiteral("named"), named}};
}

class EvidenceIdentityTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void initTestCase()
    {
        mrtest::registerSourceQmlComponents();
    }

    void anIdentityRoundShowsItsOwnStepsAndSendsItsOwnMarkers()
    {
        EvidenceScene scene;
        scene.backend.setValidationIdentityFixture(false);
        QVERIFY(scene.open(QStringLiteral("RECORDING")));
        QTRY_VERIFY(scene.app->validationMarkerEnabled());
        QTRY_VERIFY(scene.sessionValue("identity").toBool());
        QTRY_COMPARE(scene.stepStatus(3), QStringLiteral("cur"));
        QTRY_VERIFY(scene.item(QStringLiteral("evidenceStep_3_identity"))->isVisible());
        // The steps of this round stand where the events of a run would.
        auto *events = scene.item(QStringLiteral("evidenceMarkerTimeline"));
        auto *steps = scene.item(QStringLiteral("evidenceIdentitySteps"));
        QVERIFY(events && !events->isVisible());
        QVERIFY(steps && steps->isVisible());
        QVERIFY(scene.item(QStringLiteral("templateObservationMarker_character_a"))->isEnabled());
        auto *verdict = scene.item(QStringLiteral("evidenceIdentityVerdict"));
        QVERIFY(verdict && !verdict->isVisible());

        // The logins are compared with each other, so none can be left out: a later
        // step is shown but waits for the ones before it.
        for (const char *later : {"character_b", "character_a_again", "after_teleport", "after_duty"}) {
            auto *button = scene.item(QStringLiteral("templateObservationMarker_") + QLatin1String(later));
            QVERIFY2(button && button->isVisible() && !button->isEnabled(), later);
        }
        auto *session = scene.session();
        QVERIFY(session);
        QVERIFY(QMetaObject::invokeMethod(session, "mark", Q_ARG(QVariant, 1)));
        QVERIFY(scene.marksAreEmpty());

        int confirmed = 0;
        for (const char *marker : {"character_a", "character_b", "character_a_again"}) {
            const QString name = QStringLiteral("templateObservationMarker_") + QLatin1String(marker);
            QTRY_VERIFY(scene.item(name)->isEnabled());
            QVERIFY(scene.click(name));
            QTRY_COMPARE(scene.backend.lastValidationMarker(), QLatin1String(marker));
            ++confirmed;
            QTRY_COMPARE(session->property("markedCount").toInt(), confirmed);
            QTRY_VERIFY(scene.app->validationMarkerEnabled());
        }

        // Of the two optional steps the first may be left out: skipped, not 漏按.
        const QString last = QStringLiteral("templateObservationMarker_after_duty");
        QTRY_VERIFY(scene.item(last)->isEnabled());
        QVERIFY(scene.click(last));
        QTRY_COMPARE(scene.backend.lastValidationMarker(), QStringLiteral("after_duty"));
        const QVariantList marks = session->property("marks").toList();
        QCOMPARE(marks.at(3).toString(), QStringLiteral("skip"));
        QVERIFY(!session->property("chips").toMap().value(QStringLiteral("miss")).toBool());
        QCOMPARE(scene.warnings->size(), 0);
    }

    void aFinishedIdentityRoundShowsItsVerdictAndReportsIt()
    {
        EvidenceScene scene;
        scene.backend.setValidationIdentityFixture(true);
        QVERIFY(scene.open(QStringLiteral("COMPLETED")));
        QTRY_COMPARE(scene.stepStatus(4), QStringLiteral("cur"));
        auto *verdict = scene.item(QStringLiteral("evidenceSaveVerdict"));
        QTRY_VERIFY(verdict && verdict->isVisible());
        QVERIFY2(scene.text(QStringLiteral("evidenceSaveVerdictText")).startsWith(QString::fromUtf8("可以区分")),
                 qPrintable(scene.text(QStringLiteral("evidenceSaveVerdictText"))));
        QCOMPARE(scene.sessionValue("identityResult").toString(), QStringLiteral("DISTINGUISHED"));

        QVERIFY(scene.click(QStringLiteral("evidenceToReportButton")));
        QTRY_COMPARE(scene.stepStatus(5), QStringLiteral("cur"));
        const QString report = scene.text(QStringLiteral("evidenceReportText"));
        QVERIFY2(report.startsWith(QString::fromUtf8("【导随记录器 · 角色识别采证回报】")), qPrintable(report));
        QVERIFY2(report.contains(QString::fromUtf8("结论：可以区分")), qPrintable(report));
        QVERIFY2(!report.contains(QString::fromUtf8("随机类型")), qPrintable(report));
        // The round hands over its trace only: no list of files, ticked or missing.
        QVERIFY2(!report.contains(QString::fromUtf8("缺失")) && !report.contains(QString::fromUtf8("附件")),
                 qPrintable(report));
        // The candidate summary is made from an event round only.
        QVERIFY(!scene.item(QStringLiteral("evidenceOptionalToggle"))->isVisible());
        QCOMPARE(scene.warnings->size(), 0);
    }

    void thePurposeOfARoundIsSentWithItsStart()
    {
        EvidenceScene scene;
        QVERIFY(scene.open(QStringLiteral("IDLE")));
        QTRY_VERIFY(scene.app->templateObservationActionEnabled());
        auto *session = scene.session();
        QVERIFY(session);
        QVERIFY(!scene.sessionValue("identity").toBool());
        // Chosen while no round is under way, the purpose is the page's own.
        auto *purpose = scene.item(QStringLiteral("evidencePurposeControl"));
        QVERIFY(purpose && purpose->isEnabled());
        QVERIFY(session->setProperty("purpose", QStringLiteral("identity")));
        QVERIFY(scene.sessionValue("identity").toBool());
        QCOMPARE(purpose->property("currentValue").toString(), QStringLiteral("identity"));

        // The start asks for what the page shows.
        scene.app->setTemplateObservationIdentity(scene.sessionValue("identity").toBool());
        scene.app->toggleTemplateObservation();
        QTRY_COMPARE(scene.app->validationState(), QStringLiteral("WAITING"));
        QVERIFY(scene.backend.validationIdentityProbe());
        // Under way, the round is what the Collector says it is, whatever the page chose.
        QTRY_VERIFY(!purpose->isEnabled());
        QVERIFY(session->setProperty("purpose", QStringLiteral("region")));
        QVERIFY(scene.sessionValue("identity").toBool());
        QCOMPARE(scene.warnings->size(), 0);
    }

    void aStepThatNamedNobodyIsAskedForAgain()
    {
        EvidenceScene scene;
        QVERIFY(scene.open(QStringLiteral("RECORDING")));
        scene.report(identityStatus(QStringLiteral("RECORDING"), QStringLiteral("INCOMPLETE"), {}));
        QTRY_VERIFY(scene.sessionValue("identity").toBool());
        QTRY_VERIFY(scene.app->validationMarkerEnabled());
        const QString first = QStringLiteral("templateObservationMarker_character_a");
        QTRY_VERIFY(scene.item(first) && scene.item(first)->isEnabled());

        // Confirmed, and the Collector read someone: the step is done and its button gone.
        scene.backend.validation = identityStatus(QStringLiteral("RECORDING"), QStringLiteral("INCOMPLETE"),
                                                  {step("character_a", true)});
        QVERIFY(scene.click(first));
        auto *session = scene.session();
        QTRY_COMPARE(session->property("markedCount").toInt(), 1);
        QTRY_VERIFY(!scene.item(first)->isVisible());
        QCOMPARE(scene.backend.counts.value(QStringLiteral("AddCaptureValidationMarker")), 1);

        // Confirmed too early: the Collector read nobody, and the same step can be pressed again.
        const QString second = QStringLiteral("templateObservationMarker_character_b");
        scene.backend.validation = identityStatus(QStringLiteral("RECORDING"), QStringLiteral("INCOMPLETE"),
                                                  {step("character_a", true), step("character_b", false)});
        QTRY_VERIFY(scene.item(second)->isEnabled());
        QVERIFY(scene.click(second));
        QTRY_COMPARE(session->property("markedCount").toInt(), 2);
        QTRY_VERIFY(scene.item(second)->isVisible() && scene.item(second)->isEnabled());
        QCOMPARE(scene.item(second)->property("text").toString(), QString::fromUtf8("重新确认"));
        // The login after it waits: once the character is left, it cannot be read again.
        const QString third = QStringLiteral("templateObservationMarker_character_a_again");
        QVERIFY(!scene.item(third)->isEnabled());

        scene.backend.validation = identityStatus(QStringLiteral("RECORDING"), QStringLiteral("INCOMPLETE"),
                                                  {step("character_a", true), step("character_b", true)});
        QVERIFY(scene.click(second));
        QTRY_VERIFY(!scene.item(second)->isVisible());
        QCOMPARE(scene.backend.counts.value(QStringLiteral("AddCaptureValidationMarker")), 3);
        QCOMPARE(session->property("markedCount").toInt(), 2);
        QTRY_VERIFY(scene.item(third)->isEnabled());
        QCOMPARE(scene.warnings->size(), 0);
    }

    void threeLoginsWithoutAVerdictSayWhatToDo()
    {
        EvidenceScene scene;
        QVERIFY(scene.open(QStringLiteral("RECORDING")));
        scene.report(identityStatus(QStringLiteral("RECORDING"), QStringLiteral("INCOMPLETE"), {}));
        QTRY_VERIFY(scene.sessionValue("identity").toBool());
        QTRY_VERIFY(scene.app->validationMarkerEnabled());
        auto *verdict = scene.item(QStringLiteral("evidenceIdentityVerdict"));
        QVERIFY(verdict && !verdict->isVisible());

        // The first two logins are read, the third is confirmed too early.
        QJsonArray read{step("character_a", true), step("character_b", true)};
        scene.backend.validation = identityStatus(QStringLiteral("RECORDING"), QStringLiteral("INCOMPLETE"), read);
        for (const char *marker : {"character_a", "character_b"}) {
            const QString name = QStringLiteral("templateObservationMarker_") + QLatin1String(marker);
            QTRY_VERIFY(scene.item(name)->isEnabled());
            QVERIFY(scene.click(name));
            QTRY_VERIFY(scene.app->validationMarkerEnabled());
        }
        QVERIFY(!verdict->isVisible());
        const QString third = QStringLiteral("templateObservationMarker_character_a_again");
        read.append(step("character_a_again", false));
        scene.backend.validation = identityStatus(QStringLiteral("RECORDING"), QStringLiteral("INCOMPLETE"), read);
        QTRY_VERIFY(scene.item(third)->isEnabled());
        QVERIFY(scene.click(third));
        QTRY_VERIFY(verdict->isVisible());
        QVERIFY2(scene.text(QStringLiteral("evidenceIdentityVerdictText")).startsWith(QString::fromUtf8("还不能判断")),
                 qPrintable(scene.text(QStringLiteral("evidenceIdentityVerdictText"))));

        // The third login is confirmed again and read: the verdict replaces the advice.
        scene.backend.validation = identityStatus(
            QStringLiteral("RECORDING"), QStringLiteral("DISTINGUISHED"),
            {step("character_a", true), step("character_b", true), step("character_a_again", true)});
        QTRY_VERIFY(scene.item(third)->isEnabled());
        QVERIFY(scene.click(third));
        QTRY_VERIFY(scene.text(QStringLiteral("evidenceIdentityVerdictText")).startsWith(QString::fromUtf8("可以区分")));
        QCOMPARE(scene.warnings->size(), 0);
    }

    void aRoundThatEndedWithoutAVerdictSaysToDoItAgainAndAsksForNoTicks()
    {
        EvidenceScene scene;
        QVERIFY(scene.open(QStringLiteral("IDLE")));
        scene.report(identityStatus(QStringLiteral("COMPLETED"), QStringLiteral("INCOMPLETE"),
                                    {step("character_a", true)}, QStringLiteral("C:/mock-only/identity/trace.jsonl")));
        QTRY_COMPARE(scene.stepStatus(4), QStringLiteral("cur"));
        // Over, a step cannot be confirmed again: the sentence says to do the round anew.
        auto *verdict = scene.item(QStringLiteral("evidenceSaveVerdict"));
        QTRY_VERIFY(verdict && verdict->isVisible());
        const QString sentence = scene.text(QStringLiteral("evidenceSaveVerdictText"));
        QVERIFY2(sentence.startsWith(QString::fromUtf8("这一轮没有得出结论")), qPrintable(sentence));
        QVERIFY2(!sentence.contains(QString::fromUtf8("重新确认")), qPrintable(sentence));

        // Two files in one folder, and nothing to tick: the list of an event round is gone.
        QVERIFY(!scene.item(QStringLiteral("evidenceFileList"))->isVisible());
        QVERIFY(!scene.item(QStringLiteral("evidenceFileListLead"))->isVisible());
        auto *files = scene.item(QStringLiteral("evidenceIdentityFiles"));
        QVERIFY(files && files->isVisible());
        auto *folder = scene.item(QStringLiteral("evidenceIdentityFolderButton"));
        QVERIFY(folder && folder->isVisible());

        QVERIFY(scene.click(QStringLiteral("evidenceToReportButton")));
        QTRY_COMPARE(scene.stepStatus(5), QStringLiteral("cur"));
        const QString report = scene.text(QStringLiteral("evidenceReportText"));
        QVERIFY2(report.contains(QString::fromUtf8("结论：这一轮没有得出结论")), qPrintable(report));
        QVERIFY2(!report.contains(QString::fromUtf8("缺失")), qPrintable(report));
        QCOMPARE(scene.warnings->size(), 0);
    }

    void aRoundThatIsOverKeepsItsPurposeUntilItIsPutAway()
    {
        EvidenceScene scene;
        QVERIFY(scene.open(QStringLiteral("IDLE")));
        scene.report(identityStatus(QStringLiteral("FAILED"), QStringLiteral("INCOMPLETE"), {}));
        QTRY_COMPARE(scene.sessionValue("phase").toString(), QStringLiteral("failed"));
        QVERIFY(scene.sessionValue("identity").toBool());
        // The failed round is an identity round whatever the page had chosen, and the
        // control cannot say otherwise while it is on the page.
        auto *purpose = scene.item(QStringLiteral("evidencePurposeControl"));
        QVERIFY(purpose && !purpose->isEnabled());
        QCOMPARE(purpose->property("currentValue").toString(), QStringLiteral("identity"));
        auto *session = scene.session();
        QVERIFY(session->setProperty("purpose", QStringLiteral("region")));
        QVERIFY(scene.sessionValue("identity").toBool());

        // Put away, the page is back to the purpose it was given.
        QVERIFY(QMetaObject::invokeMethod(session, "startNextRound"));
        QTRY_VERIFY(!scene.sessionValue("identity").toBool());
        QTRY_VERIFY(purpose->isEnabled());
        QCOMPARE(scene.warnings->size(), 0);
    }
};

} // namespace

MR_DESKTOP_TEST_SUITE(EvidenceIdentity)
{
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication app(argc, argv);
    EvidenceIdentityTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "EvidenceIdentityTests.moc"
