// The protocol profile card on the capture page: sharing across restarts and local profile
// recalibration or rollback, including disabled actions while a duty is in flight.
#include "CalibrationCardTestSupport.h"
#include "DesktopTestRunner.h"
#include "SharedCalibrationTestSupport.h"

namespace {

using mrtest::kVectorCode;

/// The whole 捕获诊断 page, so that 协议档案 - the one card that is never hidden -
/// can be read exactly as a player sees it.
using PageScene = CapturePageScene;

/// One --mock-calibration state beside one --mock-shared state, as the screenshot
/// targets take them; an empty state leaves the mock's own.
bool openWith(PageScene &scene, const QString &calibration, const QString &shared)
{
    if (!calibration.isEmpty())
        scene.mock->setCalibrationFixture(calibration);
    if (!shared.isEmpty())
        scene.mock->setSharedCalibrationFixture(shared);
    return scene.open();
}

class ProtocolProfileCardTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void initTestCase()
    {
        QVERIFY(mrtest::registerSourceQml({"components", "pages"}));
    }

    // -- 协议档案 card: sharing once the calibration card is gone --------------

    void theProtocolCardOffersSharingAfterARestart()
    {
        // The normal state of the very player whose calibration is worth sharing: the app
        // was started again, the local profile records, calibration is IDLE and its card -
        // the other place 分享给其他玩家 appears - is not on the page at all.
        PageScene scene;
        QVERIFY(openWith(scene, QStringLiteral("idle"), QStringLiteral("share")));
        QTRY_VERIFY(scene.shared()->canShare());
        QTRY_VERIFY(scene.shows(QStringLiteral("protocolShareButton")));
        QVERIFY(scene.shows(QStringLiteral("protocolShareHint")));
        QVERIFY(!scene.shows(QStringLiteral("calibrationCard")));
        QVERIFY(!scene.shows(QStringLiteral("sharedShareButton")));

        verifyPlayerCopyOf(scene.item(QStringLiteral("protocolProfileCard")));
    }

    void onlyOneShareButtonIsEverOnScreen()
    {
        // While the calibration card is up it is the card explaining the calibration that
        // just finished, so it keeps the button; the protocol card steps aside rather than
        // putting a second identical button on the same screen.
        PageScene scene;
        QVERIFY(openWith(scene, QStringLiteral("done"), QStringLiteral("share")));
        QTRY_VERIFY(scene.shows(QStringLiteral("calibrationCard")));
        QTRY_VERIFY(scene.shows(QStringLiteral("sharedShareButton")));
        QVERIFY(!scene.shows(QStringLiteral("protocolShareButton")));
        QVERIFY(!scene.shows(QStringLiteral("protocolShareHint")));
    }

    void bothShareButtonsTakeTheSamePath()
    {
        PageScene restarted;
        QVERIFY(openWith(restarted, QStringLiteral("idle"), QStringLiteral("share")));
        QTRY_VERIFY(restarted.shows(QStringLiteral("protocolShareButton")));
        QVERIFY(QMetaObject::invokeMethod(restarted.item(QStringLiteral("protocolShareButton")), "clicked"));
        QTRY_COMPARE(restarted.opened.size(), 1);

        PageScene calibrated;
        QVERIFY(openWith(calibrated, QStringLiteral("done"), QStringLiteral("share")));
        QTRY_VERIFY(calibrated.shows(QStringLiteral("sharedShareButton")));
        QVERIFY(QMetaObject::invokeMethod(calibrated.item(QStringLiteral("sharedShareButton")), "clicked"));
        QTRY_COMPARE(calibrated.opened.size(), 1);

        // Same request, same clipboard, same address, same sentence: one code path.
        QCOMPARE(restarted.opened, calibrated.opened);
        QCOMPARE(restarted.copied, calibrated.copied);
        QVERIFY(!restarted.copied.isEmpty());
        QVERIFY(restarted.opened.constFirst().startsWith(QStringLiteral("https://github.com/")));
        QTRY_VERIFY(!restarted.app->toastMessage().isEmpty());
        QTRY_COMPARE(restarted.app->toastMessage(), calibrated.app->toastMessage());
        QVERIFY(restarted.app->toastMessage().contains(QString::fromUtf8("已打开 GitHub 分享页面")));
    }

    // -- 这份校准好用，告诉其他玩家 (plans/shared-calibration.md §20.6) ----------

    void theProtocolCardOffersTellingOtherPlayersAfterARestart()
    {
        // After a restart calibration is IDLE and its card is gone; the profile in force is the
        // proven shared one, and the 协议档案 card carries the button with its own words.
        PageScene scene;
        QVERIFY(openWith(scene, QStringLiteral("idle"), QStringLiteral("verified-proven")));
        QTRY_COMPARE(scene.shared()->shareKind(), QStringLiteral("verified"));
        QTRY_VERIFY(scene.shows(QStringLiteral("protocolShareButton")));
        QVERIFY(!scene.shows(QStringLiteral("calibrationCard")));
        QVERIFY(!scene.shows(QStringLiteral("sharedShareButton")));
        QCOMPARE(scene.item(QStringLiteral("protocolShareButton"))->property("text").toString(),
                 QString::fromUtf8("这份校准好用，告诉其他玩家"));
        QVERIFY(scene.shows(QStringLiteral("protocolShareHint")));
        QCOMPARE(scene.item(QStringLiteral("protocolShareHint"))->property("text").toString(),
                 mr::SharedCalibrationController::verifiedShareHint());
        // A shared profile is stopped through 改为本机校准, never through 重新校准.
        QVERIFY(!scene.shows(QStringLiteral("protocolRecalibrateButton")));
        verifyPlayerCopyOf(scene.item(QStringLiteral("protocolProfileCard")));

        QVERIFY(QMetaObject::invokeMethod(scene.item(QStringLiteral("protocolShareButton")), "clicked"));
        QTRY_COMPARE(scene.opened.size(), 1);
        QVERIFY(scene.opened.constFirst().startsWith(QStringLiteral("https://github.com/")));
        QVERIFY(scene.opened.constFirst().contains(QStringLiteral("basis=")));
        QCOMPARE(scene.copied, QLatin1String(kVectorCode));
        QTRY_VERIFY(scene.app->toastMessage().contains(QString::fromUtf8("多一个核实通过的账号")));
    }

    void onlyOneVerifiedShareButtonIsEverOnScreen()
    {
        // The calibration card is up (calibration finished on the shared profile): it carries the
        // button, and the protocol card steps aside, exactly as for this machine's own calibration.
        PageScene scene;
        QVERIFY(openWith(scene, QString(), QStringLiteral("verified-proven")));
        QTRY_VERIFY(scene.shows(QStringLiteral("calibrationCard")));
        QTRY_VERIFY(scene.shows(QStringLiteral("sharedShareButton")));
        QVERIFY(!scene.shows(QStringLiteral("protocolShareButton")));
        QVERIFY(!scene.shows(QStringLiteral("protocolShareHint")));
    }

    // -- 协议档案 card: 重新校准 ---------------------------------------------

    void theProtocolCardOffersRecalibrationForALocalProfile()
    {
        // The player whose own machine calibrated the wrong message. Once the local profile
        // binds, the calibration card is gone and with it 清空校准进度 and 导入校准码,
        // so until now the only way back was renaming a file in Explorer.
        PageScene scene;
        // No duty in flight: the mock's own default is one, and retiring the profile would
        // close it (recalibrationWaitsForTheDutyToEnd pins that case).
        scene.mock->setLiveMode(mr::MockBackend::LiveMode::None);
        QVERIFY(openWith(scene, QStringLiteral("idle"), QStringLiteral("share")));
        QTRY_VERIFY(scene.shows(QStringLiteral("protocolRecalibrateButton")));
        QVERIFY(!scene.shows(QStringLiteral("calibrationCard")));
        QTRY_COMPARE(scene.app->currentRunState(), QStringLiteral("IDLE"));
        QVERIFY(scene.item(QStringLiteral("protocolRecalibrateButton"))->property("enabled").toBool());
        QVERIFY(!scene.shows(QStringLiteral("protocolRecalibrateHint")));

        auto *dialog = scene.root->findChild<QObject *>(QStringLiteral("protocolRecalibrateDialog"));
        QVERIFY(dialog);
        QVERIFY(!dialog->property("visible").toBool());
        QCOMPARE(scene.mock->discardCalibrationCount(), 0);

        // Opening it sends nothing: the player is told what stopping the profile costs before
        // anything at all happens to it.
        QVERIFY(QMetaObject::invokeMethod(scene.item(QStringLiteral("protocolRecalibrateButton")), "clicked"));
        QTRY_VERIFY(dialog->property("visible").toBool());
        QCOMPARE(scene.mock->discardCalibrationCount(), 0);

        auto *confirm = dialog->findChild<QQuickItem *>(QStringLiteral("protocolRecalibrateConfirm"));
        QVERIFY(confirm);
        QVERIFY(QMetaObject::invokeMethod(confirm, "clicked"));

        QTRY_COMPARE(scene.mock->discardCalibrationCount(), 1);
        QVERIFY(scene.mock->lastDiscardCalibration()
                    .value(QStringLiteral("retire_local_profile")).toBool());
    }

    void recalibrationWaitsForTheDutyToEnd()
    {
        // Retiring the profile closes the run in flight the way a stopped capture does, which
        // would cost the player the duty they are sitting in. The button says so and waits.
        PageScene scene;
        scene.mock->setLiveMode(mr::MockBackend::LiveMode::Matched);
        QVERIFY(openWith(scene, QStringLiteral("idle"), QStringLiteral("share")));
        QTRY_COMPARE(scene.app->currentRunState(), QStringLiteral("MENTOR_MATCHED"));
        QTRY_VERIFY(scene.shows(QStringLiteral("protocolRecalibrateButton")));

        QVERIFY(!scene.item(QStringLiteral("protocolRecalibrateButton"))->property("enabled").toBool());
        QTRY_VERIFY(scene.shows(QStringLiteral("protocolRecalibrateHint")));
        QCOMPARE(scene.item(QStringLiteral("protocolRecalibrateHint"))->property("text").toString(),
                 QString::fromUtf8("副本进行中，结束后再试"));

        verifyPlayerCopyOf(scene.item(QStringLiteral("protocolProfileCard")));
    }

    // -- 协议档案 card: 恢复上一份本机校准 ------------------------------------

    void theRollbackIsOfferedOnlyWhenARetiredProfileIsWaiting_data()
    {
        QTest::addColumn<QString>("origin");
        QTest::addColumn<bool>("retiredAvailable");
        QTest::addColumn<bool>("rollback");
        QTest::addColumn<bool>("recalibrate");

        // Straight after 重新校准: nothing records, the retired file waits.
        QTest::newRow("retired") << QString() << true << true << false;
        // Her own calibration is back in force: the rollback is spent, 重新校准 returns.
        QTest::newRow("local-in-force") << "LOCAL_CALIBRATION" << false << false << true;
        // Nothing was ever retired, so there is nothing to put back.
        QTest::newRow("nothing-retired") << QString() << false << false << false;
        // A shared profile records after the retirement; the way back is still open.
        QTest::newRow("shared-took-over") << "SHARED_CALIBRATION" << true << true << false;
    }

    void theRollbackIsOfferedOnlyWhenARetiredProfileIsWaiting()
    {
        QFETCH(QString, origin);
        QFETCH(bool, retiredAvailable);
        QFETCH(bool, rollback);
        QFETCH(bool, recalibrate);

        PageScene scene;
        auto *source = scene.fake<CaptureBackend>();
        source->capture = captureWithRollback(origin, retiredAvailable);
        QVERIFY(scene.open());
        QTRY_COMPARE(scene.app->calibration()->retiredLocalProfileAvailable(), retiredAvailable);

        QCOMPARE(scene.shows(QStringLiteral("protocolRestoreButton")), rollback);
        QCOMPARE(scene.shows(QStringLiteral("protocolRecalibrateButton")), recalibrate);
        // The two are opposites and must never share a screen.
        QVERIFY(!(rollback && recalibrate));

        if (rollback)
            verifyPlayerCopyOf(scene.item(QStringLiteral("protocolProfileCard")));
    }

    void theRollbackDialogAsksFirstAndThenSendsTheFlag()
    {
        PageScene scene;
        auto *source = scene.fake<CaptureBackend>();
        source->capture = captureWithRollback(QString(), true);
        QVERIFY(scene.open());
        QTRY_VERIFY(scene.shows(QStringLiteral("protocolRestoreButton")));
        QVERIFY(scene.item(QStringLiteral("protocolRestoreButton"))->property("enabled").toBool());

        auto *dialog = scene.root->findChild<QObject *>(QStringLiteral("protocolRestoreDialog"));
        QVERIFY(dialog);
        QVERIFY(!dialog->property("visible").toBool());
        QVERIFY(QMetaObject::invokeMethod(scene.item(QStringLiteral("protocolRestoreButton")), "clicked"));
        QTRY_VERIFY(dialog->property("visible").toBool());
        QVERIFY(source->discards.isEmpty());

        auto *confirm = dialog->findChild<QQuickItem *>(QStringLiteral("protocolRestoreConfirm"));
        QVERIFY(confirm);
        QVERIFY(QMetaObject::invokeMethod(confirm, "clicked"));

        QTRY_COMPARE(source->discards.size(), 1);
        const QJsonObject sent = source->discards.constFirst();
        QVERIFY(sent.value(QStringLiteral("restore_local_profile")).toBool());
        // Never both: the Collector answers ERR_BAD_REQUEST for a request carrying the pair.
        QVERIFY(!sent.contains(QStringLiteral("retire_local_profile")));
    }

    void aRefusedRollbackShowsTheCollectorsOwnSentence()
    {
        PageScene scene;
        auto *source = scene.fake<CaptureBackend>();
        source->capture = captureWithRollback(QString(), true);
        source->discardErrorCode = QStringLiteral("ERR_CALIBRATION_NOT_READY");
        source->discardErrorMessage = QString::fromUtf8("上一份本机校准已经无法使用，请重新校准。");
        QVERIFY(scene.open());
        QTRY_VERIFY(scene.shows(QStringLiteral("protocolRestoreButton")));

        scene.app->calibration()->restoreLocalProfile();

        QTRY_VERIFY(scene.shows(QStringLiteral("protocolCalibrationError")));
        QCOMPARE(scene.item(QStringLiteral("protocolCalibrationError"))->property("text").toString(),
                 source->discardErrorMessage);
        // The error token itself never reaches the page.
        verifyPlayerCopyOf(scene.item(QStringLiteral("protocolProfileCard")));
    }

    // CS8-D1：恢复的那份本机校准放回来了，但这一版游戏另有排在它前面的档案，所以采集服务
    // 继续用那一份记录，并以 ERR_CALIBRATION_NOT_READY 说明。这不是失败：文件已经放回，
    // 退路随之没了。页面此前把这句话当错误显示，而且拒绝之后不重读状态，「恢复上一份
    // 本机校准」一直挂着。
    void anOutrankedRollbackIsReportedAsInformationAndTheOfferGoes()
    {
        const QString answer = QString::fromUtf8(
            "上一份本机校准已经放回，但这一版游戏已有随软件附带的档案，所以继续用它记录。");
        PageScene scene;
        auto *source = scene.fake<CaptureBackend>();
        // The game is closed (the installed build is known), so the recording poll is slow
        // and cannot stand in for the re-read the answer must cause.
        source->capture = captureWithRollback(QStringLiteral("SHIPPED"), true, QStringLiteral("IDLE"));
        source->capture.insert(QStringLiteral("ffxiv_running"), false);
        source->captureAfterDiscard =
              captureWithRollback(QStringLiteral("SHIPPED"), false, QStringLiteral("IDLE"));
        source->captureAfterDiscard.insert(QStringLiteral("ffxiv_running"), false);
        source->discardErrorCode = QStringLiteral("ERR_CALIBRATION_NOT_READY");
        source->discardErrorMessage = answer;
        QVERIFY(scene.open());
        QTRY_VERIFY(scene.shows(QStringLiteral("protocolRestoreButton")));
        const int reads = source->captureStatusReads;

        scene.app->calibration()->restoreLocalProfile();
        QCOMPARE(source->discards.size(), 1);

        // The status is read again after the answer, and the spent offer goes.
        QTRY_VERIFY(source->captureStatusReads > reads);
        QTRY_VERIFY(!scene.shows(QStringLiteral("protocolRestoreButton")));
        // The answer stays on the page as information, in the Collector's own words.
        QVERIFY(scene.app->calibration()->error().isEmpty());
        QVERIFY(!scene.shows(QStringLiteral("protocolCalibrationError")));
        QVERIFY(scene.shows(QStringLiteral("protocolProfileCard")));
        QVERIFY(scene.shows(QStringLiteral("protocolCalibrationNotice")));
        auto *notice = scene.item(QStringLiteral("protocolCalibrationNotice"));
        QCOMPARE(notice->property("text").toString(), answer);
        QCOMPARE(notice->property("color"),
                 scene.item(QStringLiteral("protocolProfileExplanation"))->property("color"));
        verifyPlayerCopyOf(scene.item(QStringLiteral("protocolProfileCard")));

        // It speaks of the profile in force on this build; once another build is in force
        // it would no longer be true, and it goes.
        QJsonObject patched = source->capture;
        patched.insert(QStringLiteral("game_build"), QStringLiteral("2026.10.01.0000.0000"));
        source->capture = patched;
        scene.app->rereadCaptureStatus();
        QTRY_VERIFY(!scene.shows(QStringLiteral("protocolCalibrationNotice")));
    }

    void theRollbackWaitsForTheDutyToEnd()
    {
        // Putting the old profile back closes the run in flight, exactly as retiring does.
        PageScene scene;
        auto *source = scene.fake<CaptureBackend>();
        source->capture = captureWithRollback(QString(), true);
        source->currentRun = QJsonObject{{QStringLiteral("state"), QStringLiteral("ENTERED_DUTY")}};
        QVERIFY(scene.open());
        QTRY_COMPARE(scene.app->currentRunState(), QStringLiteral("ENTERED_DUTY"));
        QTRY_VERIFY(scene.shows(QStringLiteral("protocolRestoreButton")));

        QVERIFY(!scene.item(QStringLiteral("protocolRestoreButton"))->property("enabled").toBool());
        QTRY_VERIFY(scene.shows(QStringLiteral("protocolRecalibrateHint")));
        QCOMPARE(scene.item(QStringLiteral("protocolRecalibrateHint"))->property("text").toString(),
                 QString::fromUtf8("副本进行中，结束后再试"));
    }

    // -- Review K2-2: nothing that stops the profile in force may cut a duty short ------

    void anOpenProfileDialogClosesWhenADutyStarts_data()
    {
        QTest::addColumn<QString>("origin");
        QTest::addColumn<bool>("retiredAvailable");
        QTest::addColumn<QString>("button");
        QTest::addColumn<QString>("dialog");
        QTest::addColumn<QString>("confirm");
        QTest::addColumn<QString>("runState");
        for (const char *state : {"MENTOR_MATCHED", "ENTERED_DUTY"}) {
            QTest::newRow(QByteArray("recalibrate-").append(state).constData())
                << QStringLiteral("LOCAL_CALIBRATION") << false << QStringLiteral("protocolRecalibrateButton")
                << QStringLiteral("protocolRecalibrateDialog") << QStringLiteral("protocolRecalibrateConfirm")
                << QString::fromLatin1(state);
            QTest::newRow(QByteArray("restore-").append(state).constData())
                << QString() << true << QStringLiteral("protocolRestoreButton")
                << QStringLiteral("protocolRestoreDialog") << QStringLiteral("protocolRestoreConfirm")
                << QString::fromLatin1(state);
        }
    }

    void anOpenProfileDialogClosesWhenADutyStarts()
    {
        // Trigger B: the dialog is opened while queued - nothing in flight, so the button is
        // enabled - the duty pops while it is open, and its confirm button would still retire
        // or swap the profile in force, closing the run the player is sitting in.
        QFETCH(QString, origin);
        QFETCH(bool, retiredAvailable);
        QFETCH(QString, button);
        QFETCH(QString, dialog);
        QFETCH(QString, confirm);
        QFETCH(QString, runState);

        PageScene scene;
        auto *source = scene.fake<CaptureBackend>();
        source->capture = captureWithRollback(origin, retiredAvailable);
        QVERIFY(scene.open());
        QTRY_VERIFY(scene.shows(button));
        QTRY_COMPARE(scene.app->currentRunState(), QStringLiteral("IDLE"));
        QTRY_VERIFY(scene.item(button)->property("enabled").toBool());

        auto *dialogObject = scene.root->findChild<QObject *>(dialog);
        QVERIFY(dialogObject);
        QVERIFY(QMetaObject::invokeMethod(scene.item(button), "clicked"));
        QTRY_VERIFY(dialogObject->property("visible").toBool());
        auto *confirmItem = dialogObject->findChild<QQuickItem *>(confirm);
        QVERIFY(confirmItem);
        QVERIFY(confirmItem->property("enabled").toBool());

        source->currentRun = QJsonObject{{QStringLiteral("state"), runState}};
        scene.app->refreshStatus();
        QTRY_COMPARE(scene.app->currentRunState(), runState);

        QTRY_VERIFY(!dialogObject->property("visible").toBool());
        QVERIFY(!confirmItem->property("enabled").toBool());
        QVERIFY(source->discards.isEmpty());
    }

    void onlyALocalCalibrationIsOfferedForSharing_data()
    {
        QTest::addColumn<QString>("origin");
        // Someone else's calibration is not offered until it has proven itself here (no
        // candidate says so in these rows; theProtocolCardOffersTellingOtherPlayersAfterARestart
        // covers the proven one), the shipped profile needs no sharing, and with no profile in
        // force there is nothing to share yet.
        QTest::newRow("shared") << "SHARED_CALIBRATION";
        QTest::newRow("shipped") << "SHIPPED";
        QTest::newRow("no-profile") << "";
    }

    void onlyALocalCalibrationIsOfferedForSharing()
    {
        QFETCH(QString, origin);

        PageScene scene;
        auto *source = scene.fake<CaptureBackend>();
        source->capture = {{QStringLiteral("ffxiv_running"), true},
                           {QStringLiteral("state"), QStringLiteral("RUNNING")},
                           {QStringLiteral("profile_status"),
                            origin.isEmpty() ? QStringLiteral("UNSUPPORTED_BUILD")
                                             : QStringLiteral("VERIFIED")},
                           {QStringLiteral("calibration"), QJsonObject{
                                {QStringLiteral("state"), QStringLiteral("IDLE")},
                                {QStringLiteral("blockers"), QJsonArray{}},
                                {QStringLiteral("events"), QJsonArray{}},
                                {QStringLiteral("shared"), sharedNone()}}}};
        if (!origin.isEmpty())
            source->capture.insert(QStringLiteral("profile_origin"), origin);

        QVERIFY(scene.open());
        QTRY_VERIFY(scene.shared()->available());
        QVERIFY(!scene.shared()->canShare());
        QVERIFY(!scene.shows(QStringLiteral("protocolShareButton")));
        QVERIFY(!scene.shows(QStringLiteral("protocolShareHint")));
        // 重新校准 retracts this machine's own guess. Someone else's calibration is stopped
        // through 改为本机校准, the shipped profile is not ours to retract, and with
        // no profile in force there is nothing to stop using.
        QVERIFY(!scene.shows(QStringLiteral("protocolRecalibrateButton")));
    }

    // -- 协议档案 card: what a recording profile tells the player -----------------

    void theProfileCardExplainsTheVerifiedProfile_data()
    {
        QTest::addColumn<QString>("origin");
        QTest::addColumn<QString>("explanation");
        QTest::newRow("local") << "LOCAL_CALIBRATION"
            << QString::fromUtf8("这台电脑已完成校准，可以正常自动记录。如果没有自动识别到通关，请在离开副本后打开记录补上结果。");
        QTest::newRow("shared") << "SHARED_CALIBRATION"
            << QString::fromUtf8("其他玩家分享的校准已通过检查，适用于当前游戏版本。自动记录状态见上方。");
        QTest::newRow("shipped") << "SHIPPED"
            << QString::fromUtf8("能自动记录指导者任务的匹配、进入和离开；副本内收到通关结算时自动记为通关。"
                                 "没有收到结算就离开副本时，记录会标为“待复核”，在记录里用“手动修正”填上结果。");
    }

    void theProfileCardExplainsTheVerifiedProfile()
    {
        QFETCH(QString, origin);
        QFETCH(QString, explanation);

        PageScene scene;
        auto *source = scene.fake<CaptureBackend>();
        source->capture = captureWithRollback(origin, false, QStringLiteral("IDLE"));
        QVERIFY(scene.open());
        QTRY_COMPARE(scene.app->captureStatus().value(QStringLiteral("profile_origin")).toString(), origin);

        auto *text = scene.item(QStringLiteral("protocolProfileExplanation"));
        QVERIFY(text);
        QTRY_COMPARE(text->property("text").toString(), explanation);
    }
};
} // namespace

MR_DESKTOP_TEST_SUITE(ProtocolProfileCard)
{
    return runCalibrationCardTests<ProtocolProfileCardTests>(argc, argv);
}

#include "ProtocolProfileCardTests.moc"
