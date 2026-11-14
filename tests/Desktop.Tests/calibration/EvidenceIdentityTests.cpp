// 角色识别采证 (evidence/EvidencePage.qml with EvidenceSession.purpose "identity"): the page
// follows the Collector's identity_probe, lists the five steps of the round instead of the
// events of a run, asks again for a step that named nobody, and shows and reports the verdict.
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

        // Confirming the second login first leaves the first one skipped, not 漏按.
        QVERIFY(scene.click(QStringLiteral("templateObservationMarker_character_b")));
        auto *session = scene.session();
        QVERIFY(session);
        QTRY_COMPARE(session->property("markedCount").toInt(), 1);
        const QVariantList marks = session->property("marks").toList();
        QCOMPARE(marks.at(0).toString(), QStringLiteral("skip"));
        QVERIFY(!session->property("chips").toMap().value(QStringLiteral("miss")).toBool());
        QTRY_COMPARE(scene.backend.lastValidationMarker(), QStringLiteral("character_b"));
        QTRY_VERIFY(scene.item(QStringLiteral("templateObservationMarker_character_a_again"))->isEnabled());
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

        scene.backend.validation = identityStatus(QStringLiteral("RECORDING"), QStringLiteral("INCOMPLETE"),
                                                  {step("character_a", true), step("character_b", true)});
        QVERIFY(scene.click(second));
        QTRY_VERIFY(!scene.item(second)->isVisible());
        QCOMPARE(scene.backend.counts.value(QStringLiteral("AddCaptureValidationMarker")), 3);
        QCOMPARE(session->property("markedCount").toInt(), 2);
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

        const QJsonArray read{step("character_a", true), step("character_b", false), step("character_a_again", true)};
        scene.backend.validation = identityStatus(QStringLiteral("RECORDING"), QStringLiteral("INCOMPLETE"), read);
        for (const char *marker : {"character_a", "character_b", "character_a_again"}) {
            const QString name = QStringLiteral("templateObservationMarker_") + QLatin1String(marker);
            QTRY_VERIFY(scene.item(name)->isEnabled());
            QVERIFY(scene.click(name));
            QTRY_VERIFY(scene.app->validationMarkerEnabled());
        }
        QTRY_VERIFY(verdict->isVisible());
        QVERIFY2(scene.text(QStringLiteral("evidenceIdentityVerdictText")).startsWith(QString::fromUtf8("还不能判断")),
                 qPrintable(scene.text(QStringLiteral("evidenceIdentityVerdictText"))));

        // The second login is confirmed again and read: the verdict replaces the advice.
        scene.backend.validation = identityStatus(
            QStringLiteral("RECORDING"), QStringLiteral("DISTINGUISHED"),
            {step("character_a", true), step("character_b", true), step("character_a_again", true)});
        QVERIFY(scene.click(QStringLiteral("templateObservationMarker_character_b")));
        QTRY_VERIFY(scene.text(QStringLiteral("evidenceIdentityVerdictText")).startsWith(QString::fromUtf8("可以区分")));
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
