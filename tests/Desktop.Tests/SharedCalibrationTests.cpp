// ---------------------------------------------------------------------------
// tst_sharedcalibration - 共享校准 (§5.1,
// §7.2), desktop half: the controller, its AppController wiring and the mock.
//
// What it pins:
//   * every $defs/SharedCalibrationPhase - and user_rejected before it - read
//     in the player's own words: no short id, refusal token, hex or enum;
//   * which buttons a calibration state offers;
//   * 分享给其他玩家: the clipboard always gets the code, the browser gets the
//     issue address only when the code is in it and it is a github.com address,
//     and each ERR_SHARE_CODE_UNAVAILABLE reason has its own sentence;
//   * 导入校准码 shows the Collector's own message and never sends empty text;
//   * 立即检查 / consent / 不用共享的 send empty requests and re-read the status;
//   * shared_calibration_enabled passes the AppController's settings whitelist;
//   * the mock's --mock-shared fixtures project to the states they are named for.
// The card itself, in shipping QML, is SharedCalibrationCardTests.cpp.
// ---------------------------------------------------------------------------

#include "TestCollectorGuard.h"
#include "AppController.h"
#include "AutomaticRecordingController.h"
#include "CalibrationController.h"
#include "IBackend.h"
#include "MockBackend.h"
#include "SharedCalibrationController.h"

#include <QGuiApplication>
#include <QJsonArray>
#include <QJsonObject>
#include <QPointer>
#include <QSignalSpy>
#include <QStandardPaths>
#include <QTest>

#include <algorithm>

namespace {

constexpr auto kVectorCode =
    "MRC1.XY_LasMwEEX_ZdbG6G3LuxKyKyG02ZRShCSPExXbMrIdaEP-vWoKpeksZjH3wZkLHO2Axq2hb6EBRpgqiS4JLUme24IC3qMzcfKxRWgEZUwWMNjFn8wc1-TzEZ62-8cX83x4OGyzf4oTNBf4jWgqRQEz9uiXmMzZ9ivO0Lzyt2sBCY8hjrljs8vRBYeptwuaKcUu9GjCN5cfyx-0uiTyr2s-WSZVdvBOeYrEaVu1zAuUXU00tcxxL1qJqqtITTWz3AkvW4XVP_1WmlLIfB93v2bwMzS0gM844p3Cr18";

///  provenance empty leaves the field out, as a Collector before 1.1.0 does.
QJsonObject candidate(const QString &source, const QString &status = QStringLiteral("VERIFYING"),
                      const QString &provenance = QString(), bool auditPending = false)
{
    QJsonObject row{{QStringLiteral("sha12"), QStringLiteral("67ef1bb97e65")},
                    {QStringLiteral("source"), source},
                    {QStringLiteral("match_source"), QStringLiteral("REPLY_STATE")},
                    {QStringLiteral("status"), status},
                    {QStringLiteral("verdict"), QStringLiteral("WAIT")},
                    {QStringLiteral("criteria"), QJsonArray{QJsonObject{
                         {QStringLiteral("message"), QStringLiteral("ZONE_INITIALIZATION")},
                         {QStringLiteral("verdict"), QStringLiteral("WAIT")},
                         {QStringLiteral("reason"), QString::fromUtf8("还没有见到登录时的换区。")},
                         {QStringLiteral("contradicting_sessions"), 0}}}},
                    {QStringLiteral("staging_overflowed"), false}};
    if (!provenance.isEmpty()) {
        row.insert(QStringLiteral("provenance"), provenance);
        row.insert(QStringLiteral("audit_pending"), auditPending);
    }
    return row;
}

QJsonObject sharedStatus(const QString &phase, bool userRejected = false,
                         const QString &lastFetch = QString(), const QJsonArray &candidates = {},
                         bool auditPending = false)
{
    QJsonObject status{
        {QStringLiteral("phase"), phase},
        {QStringLiteral("candidates"), candidates},
        {QStringLiteral("last_fetch_status"),
         lastFetch.isEmpty() ? QJsonValue(QJsonValue::Null) : QJsonValue(lastFetch)},
        {QStringLiteral("last_index_attempts"), QJsonArray{QJsonObject{
             {QStringLiteral("source"), QStringLiteral("GITHUB_RAW")},
             {QStringLiteral("outcome"), QStringLiteral("TIMEOUT")}}}},
        {QStringLiteral("profile_id"), QJsonValue::Null},
        {QStringLiteral("bound_at_utc"), QJsonValue::Null},
        // Diagnostics only: nothing a player reads may ever repeat it.
        {QStringLiteral("last_refusal"), QStringLiteral("WRITE_FAILED")},
        {QStringLiteral("rejected_candidates"), 0},
        {QStringLiteral("user_rejected"), userRejected}};
    // Optional since 1.1.0: left out entirely unless asked for, so the "not reported"
    // path an older Collector produces is what every other row exercises.
    if (auditPending)
        status.insert(QStringLiteral("audit_pending"), true);
    return status;
}

QVariantMap captureWith(const QString &calibrationState, const QJsonObject &shared,
                        const QString &origin = QString())
{
    QJsonObject calibration{{QStringLiteral("state"), calibrationState},
                            {QStringLiteral("game_build"), QStringLiteral("2026.09.01.0000.0000")},
                            {QStringLiteral("blockers"), QJsonArray{}},
                            {QStringLiteral("events"), QJsonArray{}}};
    if (!shared.isEmpty())
        calibration.insert(QStringLiteral("shared"), shared);
    QJsonObject capture{{QStringLiteral("calibration"), calibration}};
    if (!origin.isEmpty())
        capture.insert(QStringLiteral("profile_origin"), origin);
    return capture.toVariantMap();
}

/// Maintainer words, wire tokens, the candidate's short id and the code itself.
void verifyPlayerCopy(const QString &text, bool mayBeEmpty = false)
{
    if (!mayBeEmpty)
        QVERIFY2(!text.isEmpty(), "no sentence at all");
    static const QStringList words{
        QString::fromUtf8("开始捕获"), QString::fromUtf8("开始验证"), QString::fromUtf8("停止捕获"),
        QString::fromUtf8("维护者工具"), QString::fromUtf8("对照核对"), QStringLiteral("opcode"),
        QStringLiteral("0x"), QStringLiteral("67ef1bb97e65"), QStringLiteral("sha"),
        QStringLiteral("WRITE_FAILED"), QStringLiteral("REPLY_STATE"), QStringLiteral("QUEUE_REQUEST"),
        QStringLiteral("GITHUB_RAW"), QStringLiteral("TIMEOUT"), QStringLiteral("FETCHING"),
        QStringLiteral("VERIFYING"), QStringLiteral("AWAITING_CONSENT"), QStringLiteral("VERIFIED"),
        QStringLiteral("REJECTED"), QStringLiteral("UNAVAILABLE"), QStringLiteral("NONE_FOR_BUILD"),
        QStringLiteral("ERR_"), QStringLiteral("MRC1"), QStringLiteral("PUBLISHED"),
        QStringLiteral("IMPORTED"), QStringLiteral("audit_pending")};
    for (const QString &word : words) {
        QVERIFY2(!text.contains(word, Qt::CaseInsensitive),
                 qPrintable(QStringLiteral("player copy leaks \"%1\": %2").arg(word, text)));
    }
}

/// Answers every message synchronously and records what was sent.
class SharedBackend final : public mr::IBackend
{
public:
    QStringList calls;
    QList<QJsonObject> payloads;
    QJsonObject capture{{QStringLiteral("ffxiv_running"), true},
                        {QStringLiteral("ffxiv_process_id"), 4242},
                        {QStringLiteral("profile_status"), QStringLiteral("UNSUPPORTED_BUILD")},
                        {QStringLiteral("state"), QStringLiteral("RUNNING")},
                        {QStringLiteral("npcap_installed"), true},
                        {QStringLiteral("silent_reason"), QStringLiteral("NONE")}};
    QJsonObject shareResult;
    QString failType;
    /// A message whose reply is left unanswered, so the controller stays busy.
    QString holdType;
    QString errorCode;
    QString errorMessage;
    QJsonObject errorDetails;
    QString outcome = QStringLiteral("STARTED");
    QJsonObject importResult;
    QJsonObject rejectResult{{QStringLiteral("withdrawn_profile_id"), QJsonValue::Null},
                             {QStringLiteral("dropped_candidates"), 1}};

    QString backendName() const override { return QStringLiteral("mock"); }
    bool isConnected() const override { return true; }

    mr::BackendReply *request(const QString &type, const QJsonObject &payload = {}) override
    {
        calls.append(type);
        payloads.append(payload);
        auto *reply = new mr::BackendReply(QString::number(calls.size()), type, this);
        if (type == holdType)
            return reply;
        if (type == failType)
            reply->fail(errorCode, errorMessage, errorDetails);
        else
            reply->succeed(answer(type));
        return reply;
    }

    int count(const char *type) const
    {
        return int(std::count(calls.begin(), calls.end(), QLatin1String(type)));
    }

private:
    QJsonObject answer(const QString &type) const
    {
        if (type == QLatin1String("GetStatus"))
            return {{QStringLiteral("capture"), capture}};
        if (type == QLatin1String("GetCaptureStatus"))
            return capture;
        if (type == QLatin1String("GetCaptureSettings"))
            return {{QStringLiteral("follow_game"), true},
                    {QStringLiteral("shared_calibration_enabled"), true}};
        if (type == QLatin1String("GetCaptureValidationStatus"))
            return {{QStringLiteral("active"), false}, {QStringLiteral("state"), QStringLiteral("IDLE")}};
        if (type == QLatin1String("GetCalibrationShareCode"))
            return shareResult;
        if (type == QLatin1String("ImportCalibrationCode"))
            return importResult;
        if (type == QLatin1String("RejectSharedCalibration"))
            return rejectResult;
        if (type == QLatin1String("CheckSharedCalibration")
            || type == QLatin1String("AcceptSharedQueueInference"))
            return {{QStringLiteral("outcome"), outcome}};
        return {};
    }
};

struct Seams
{
    QList<QUrl> opened;
    QStringList copied;
    bool openResult = true;

    void attach(mr::SharedCalibrationController &controller)
    {
        controller.setUrlOpener([this](const QUrl &url) { opened.append(url); return openResult; });
        controller.setClipboardWriter([this](const QString &text) { copied.append(text); });
    }
};

QString lastNotice(const QSignalSpy &spy)
{
    return spy.isEmpty() ? QString() : spy.last().at(0).toString();
}

} // namespace

class SharedCalibrationTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    // -- projection ---------------------------------------------------------

    void projectsEachPhaseInPlayerWords_data()
    {
        QTest::addColumn<QString>("phase");
        QTest::addColumn<QString>("lastFetch");
        QTest::addColumn<bool>("userRejected");
        QTest::addColumn<QString>("view");
        QTest::addColumn<QString>("needle");

        QTest::newRow("fetching") << "FETCHING" << "" << false << "fetching"
                                  << QString::fromUtf8("正在获取其他玩家的共享校准");
        QTest::newRow("verifying") << "VERIFYING" << "OK" << false << "verifying"
                                   << QString::fromUtf8("找到共享校准，登录或排本时自动核实");
        QTest::newRow("consent") << "AWAITING_CONSENT" << "OK" << false << "consent"
                                 << QString::fromUtf8("同意一次");
        QTest::newRow("verified") << "VERIFIED" << "OK" << false << "verified"
                                  << QString::fromUtf8("已使用其他玩家分享的校准（本机已核实）");
        QTest::newRow("rejected") << "REJECTED" << "OK" << false << "rejected"
                                  << QString::fromUtf8("共享校准与本机流量对不上，已改为本机校准");
        QTest::newRow("index-unavailable") << "UNAVAILABLE" << "INDEX_UNAVAILABLE" << false << "unavailable"
                                           << QString::fromUtf8("没取到共享校准（网络不通），继续本机校准");
        QTest::newRow("codes-unavailable") << "UNAVAILABLE" << "CODES_UNAVAILABLE" << false << "unavailable"
                                           << QString::fromUtf8("没取到共享校准（网络不通），继续本机校准");
        QTest::newRow("none-for-build") << "NONE" << "NONE_FOR_BUILD" << false << "none_for_build"
                                        << QString::fromUtf8("还没有人分享这个版本的校准，继续本机校准");
        QTest::newRow("disabled") << "NONE" << "DISABLED" << false << "disabled"
                                  << QString::fromUtf8("已关闭");
        // A refusal also reads REJECTED; user_rejected has to be read first.
        QTest::newRow("user-rejected") << "REJECTED" << "OK" << true << "user_rejected"
                                       << QString::fromUtf8("已按你的选择改为本机校准");
        QTest::newRow("user-rejected-first") << "VERIFYING" << "OK" << true << "user_rejected"
                                             << QString::fromUtf8("已按你的选择改为本机校准");
    }

    void projectsEachPhaseInPlayerWords()
    {
        QFETCH(QString, phase);
        QFETCH(QString, lastFetch);
        QFETCH(bool, userRejected);
        QFETCH(QString, view);
        QFETCH(QString, needle);

        mr::SharedCalibrationController controller;
        const QJsonArray candidates{candidate(QStringLiteral("DOWNLOADED"))};
        controller.refreshFromCaptureStatus(captureWith(
            QStringLiteral("OBSERVING"), sharedStatus(phase, userRejected, lastFetch, candidates)));

        QVERIFY(controller.available());
        QCOMPARE(controller.view(), view);
        QVERIFY2(controller.headline().contains(needle), qPrintable(controller.headline()));
        verifyPlayerCopy(controller.headline());
        verifyPlayerCopy(controller.detail(), true);
    }

    void aRefusalSaysHowToUndoIt()
    {
        mr::SharedCalibrationController controller;
        controller.refreshFromCaptureStatus(captureWith(
            QStringLiteral("OBSERVING"), sharedStatus(QStringLiteral("REJECTED"), true)));
        QVERIFY(controller.userRejected());
        QVERIFY(controller.detail().contains(QString::fromUtf8("清空进度并重新观察")));
    }

    void anOlderCollectorWithoutSharedOffersNothing()
    {
        mr::SharedCalibrationController controller;
        controller.refreshFromCaptureStatus(captureWith(QStringLiteral("OBSERVING"), {}));
        QVERIFY(!controller.available());
        QCOMPARE(controller.view(), QStringLiteral("none"));
        QVERIFY(controller.headline().isEmpty());
        QVERIFY(!controller.canCheck());
        QVERIFY(!controller.canImport());
        QVERIFY(!controller.canReject());
    }

    void importedCodesSayImportedRatherThanFound()
    {
        mr::SharedCalibrationController controller;
        controller.refreshFromCaptureStatus(captureWith(
            QStringLiteral("OBSERVING"),
            sharedStatus(QStringLiteral("VERIFYING"), false, QString(),
                         {candidate(QStringLiteral("MANUAL"))})));
        QVERIFY(controller.headline().contains(QString::fromUtf8("已导入校准码")));

        controller.refreshFromCaptureStatus(captureWith(
            QStringLiteral("OBSERVING"),
            sharedStatus(QStringLiteral("VERIFYING"), false, QStringLiteral("OK"),
                         {candidate(QStringLiteral("MANUAL")), candidate(QStringLiteral("DOWNLOADED"))})));
        QVERIFY(controller.headline().contains(QString::fromUtf8("找到共享校准")));
    }

    // -- 18.3 / 18.4: gates graded by where the code came from ---------------

    void theVerifyingSentenceFollowsTheProvenance_data()
    {
        QTest::addColumn<QJsonArray>("candidates");
        QTest::addColumn<QString>("provenance");
        QTest::addColumn<QString>("headline");

        const QString published = QString::fromUtf8("找到共享校准，登录时自动核实，通过就开始记录。");
        const QString imported = QString::fromUtf8("已导入校准码，登录并排一次本、核实通过后启用。");
        // A pasted code the last index read lists is judged like a downloaded one (18.5),
        // so "已导入" is not what decides the sentence any more - the provenance is.
        QTest::newRow("downloaded-published")
            << QJsonArray{candidate(QStringLiteral("DOWNLOADED"), QStringLiteral("VERIFYING"),
                                    QStringLiteral("PUBLISHED"))}
            << "published" << published;
        QTest::newRow("imported-and-listed")
            << QJsonArray{candidate(QStringLiteral("MANUAL"), QStringLiteral("VERIFYING"),
                                    QStringLiteral("PUBLISHED"))}
            << "published" << published;
        QTest::newRow("imported-unpublished")
            << QJsonArray{candidate(QStringLiteral("MANUAL"), QStringLiteral("VERIFYING"),
                                    QStringLiteral("IMPORTED"))}
            << "imported" << imported;
        QTest::newRow("one-published-among-them")
            << QJsonArray{candidate(QStringLiteral("MANUAL"), QStringLiteral("VERIFYING"),
                                    QStringLiteral("IMPORTED")),
                          candidate(QStringLiteral("DOWNLOADED"), QStringLiteral("VERIFYING"),
                                    QStringLiteral("PUBLISHED"))}
            << "published" << published;
        // A rejected candidate decides nothing any more, so the one still in the running does.
        QTest::newRow("rejected-published-is-ignored")
            << QJsonArray{candidate(QStringLiteral("DOWNLOADED"), QStringLiteral("REJECTED"),
                                    QStringLiteral("PUBLISHED")),
                          candidate(QStringLiteral("MANUAL"), QStringLiteral("VERIFYING"),
                                    QStringLiteral("IMPORTED"))}
            << "imported" << imported;
        // An older Collector reports no provenance: neither promise can be made, and the
        // wording from before the split stands unchanged.
        QTest::newRow("not-reported-downloaded")
            << QJsonArray{candidate(QStringLiteral("DOWNLOADED"))} << QString()
            << QString::fromUtf8("找到共享校准，登录或排本时自动核实。");
        QTest::newRow("not-reported-manual")
            << QJsonArray{candidate(QStringLiteral("MANUAL"))} << QString()
            << QString::fromUtf8("已导入校准码，登录或排本时自动核实。");
        // One candidate without a provenance is not "all imported" either.
        QTest::newRow("half-reported")
            << QJsonArray{candidate(QStringLiteral("MANUAL"), QStringLiteral("VERIFYING"),
                                    QStringLiteral("IMPORTED")),
                          candidate(QStringLiteral("MANUAL"))}
            << QString()
            << QString::fromUtf8("已导入校准码，登录或排本时自动核实。");
    }

    void theVerifyingSentenceFollowsTheProvenance()
    {
        QFETCH(QJsonArray, candidates);
        QFETCH(QString, provenance);
        QFETCH(QString, headline);

        mr::SharedCalibrationController c;
        c.refreshFromCaptureStatus(captureWith(
            QStringLiteral("OBSERVING"),
            sharedStatus(QStringLiteral("VERIFYING"), false, QStringLiteral("OK"), candidates)));
        QCOMPARE(c.view(), QStringLiteral("verifying"));
        QCOMPARE(c.candidateProvenance(), provenance);
        QVERIFY(!c.auditPending());
        QCOMPARE(c.headline(), headline);
        verifyPlayerCopy(c.headline());
        verifyPlayerCopy(c.detail(), true);
    }

    void aRecordingProfileUnderAuditSaysSoInGrey()
    {
        mr::SharedCalibrationController c;
        const QJsonArray audited{candidate(QStringLiteral("DOWNLOADED"), QStringLiteral("IN_USE"),
                                           QStringLiteral("PUBLISHED"), true)};
        c.refreshFromCaptureStatus(captureWith(
            QStringLiteral("OBSERVING"),
            sharedStatus(QStringLiteral("VERIFIED"), false, QStringLiteral("OK"), audited, true),
            QStringLiteral("SHARED_CALIBRATION")));

        QCOMPARE(c.view(), QStringLiteral("verified"));
        QVERIFY(c.inUse());
        QVERIFY(c.auditPending());
        // It records because the login burst matched; it must not claim more than that.
        QCOMPARE(c.headline(),
                 QString::fromUtf8("已使用其他玩家分享的校准（登录时已在本机核实）。"));
        QCOMPARE(c.detail(),
                 QString::fromUtf8("排本和进本还在核对中，照常游戏即可；万一对不上，会自动改回本机校准，这期间生成的记录会标记待复核。"));
        verifyPlayerCopy(c.headline());
        verifyPlayerCopy(c.detail());
        // The audit changes nothing a player can do about it.
        QVERIFY(c.canReject() && !c.canImport() && !c.canCheck() && !c.canShare());

        // Once every audited criterion has passed - or on a Collector that never reports
        // the field - the plain sentence returns.
        c.refreshFromCaptureStatus(captureWith(
            QStringLiteral("OBSERVING"),
            sharedStatus(QStringLiteral("VERIFIED"), false, QStringLiteral("OK"),
                         {candidate(QStringLiteral("DOWNLOADED"), QStringLiteral("IN_USE"),
                                    QStringLiteral("PUBLISHED"))}),
            QStringLiteral("SHARED_CALIBRATION")));
        QVERIFY(!c.auditPending());
        QCOMPARE(c.headline(),
                 QString::fromUtf8("已使用其他玩家分享的校准（本机已核实）。"));
        QVERIFY(c.detail().contains(QString::fromUtf8("这台电脑的流量里核实过")));
    }

    void actionsFollowTheCalibrationState()
    {
        mr::SharedCalibrationController c;
        c.refreshFromCaptureStatus(captureWith(QStringLiteral("OBSERVING"), sharedStatus(QStringLiteral("NONE"))));
        QVERIFY(c.canCheck() && c.canImport() && !c.canReject() && !c.canShare());

        c.refreshFromCaptureStatus(captureWith(QStringLiteral("OBSERVING"), sharedStatus(QStringLiteral("VERIFYING"))));
        QVERIFY(!c.canCheck() && c.canImport() && c.canReject());

        // Awaiting consent is the phase importing matters most in, not least: the two answers
        // on offer are "bind this queue-inferred code" and "refuse every shared code until
        // 重新观察", and a player holding a friend's better code needs a third.
        c.refreshFromCaptureStatus(captureWith(QStringLiteral("OBSERVING"), sharedStatus(QStringLiteral("AWAITING_CONSENT"))));
        QVERIFY(c.canImport() && c.canReject());

        c.refreshFromCaptureStatus(captureWith(QStringLiteral("OBSERVING"), sharedStatus(QStringLiteral("REJECTED"), true)));
        QVERIFY(!c.canCheck() && !c.canImport() && !c.canReject());

        c.refreshFromCaptureStatus(captureWith(QStringLiteral("DONE"), sharedStatus(QStringLiteral("NONE")),
                                               QStringLiteral("LOCAL_CALIBRATION")));
        QVERIFY(c.canShare() && !c.canCheck() && !c.canImport());

        // Retention: the shared profile records while calibration stays armed beside it.
        c.refreshFromCaptureStatus(captureWith(QStringLiteral("OBSERVING"), sharedStatus(QStringLiteral("VERIFIED")),
                                               QStringLiteral("SHARED_CALIBRATION")));
        QVERIFY(c.inUse() && c.canReject() && !c.canShare() && !c.canImport() && !c.canCheck());
        QCOMPARE(c.view(), QStringLiteral("verified"));

        c.refreshFromCaptureStatus(captureWith(QStringLiteral("DONE"), sharedStatus(QStringLiteral("VERIFIED")),
                                               QStringLiteral("SHARED_CALIBRATION")));
        QVERIFY(!c.canShare());

        // 分享给其他玩家 follows the profile in force, not the calibration: after a restart
        // calibration is IDLE and its card is gone, and that is exactly the player whose
        // calibration is worth sharing - gating on DONE would offer nothing here.
        const auto restarted = [&c](const QString &origin, const QJsonObject &shared) {
            c.refreshFromCaptureStatus(captureWith(QStringLiteral("IDLE"), shared, origin));
            return c.canShare();
        };
        QVERIFY(restarted(QStringLiteral("LOCAL_CALIBRATION"), sharedStatus(QStringLiteral("NONE"))));
        QVERIFY(!c.canCheck() && !c.canImport() && !c.canReject());
        // Never someone else's, never the shipped one, never without a profile, never on an old Collector.
        QVERIFY(!restarted(QStringLiteral("SHARED_CALIBRATION"), sharedStatus(QStringLiteral("VERIFIED"))) && c.inUse());
        QVERIFY(!restarted(QStringLiteral("SHIPPED"), sharedStatus(QStringLiteral("NONE"))));
        QVERIFY(!restarted(QString(), sharedStatus(QStringLiteral("NONE"))));
        QVERIFY(!restarted(QStringLiteral("LOCAL_CALIBRATION"), {}) && !c.available());
    }

    void changedFiresOnlyWhenSomethingChanged()
    {
        mr::SharedCalibrationController c;
        QSignalSpy changed(&c, &mr::SharedCalibrationController::changed);
        const auto capture = captureWith(QStringLiteral("OBSERVING"), sharedStatus(QStringLiteral("FETCHING")));
        c.refreshFromCaptureStatus(capture);
        c.refreshFromCaptureStatus(capture);
        QCOMPARE(changed.count(), 1);
    }

    // -- requests -----------------------------------------------------------

    void checkNowToastsEveryOutcome_data()
    {
        QTest::addColumn<QString>("outcome");
        QTest::addColumn<QString>("needle");
        QTest::newRow("started") << "STARTED" << QString::fromUtf8("正在获取");
        QTest::newRow("already") << "ALREADY_FETCHING" << QString::fromUtf8("已经在获取");
        QTest::newRow("disabled") << "DISABLED" << QString::fromUtf8("设置");
        QTest::newRow("not-needed") << "NOT_NEEDED" << QString::fromUtf8("不需要");
    }

    void checkNowToastsEveryOutcome()
    {
        QFETCH(QString, outcome);
        QFETCH(QString, needle);
        SharedBackend backend;
        backend.outcome = outcome;
        mr::SharedCalibrationController c;
        c.setBackend(&backend);
        QSignalSpy notices(&c, &mr::SharedCalibrationController::notice);
        QSignalSpy rereads(&c, &mr::SharedCalibrationController::refreshRequested);

        c.checkNow();
        QCOMPARE(backend.calls, QStringList{QStringLiteral("CheckSharedCalibration")});
        QVERIFY(backend.payloads.first().isEmpty());
        QVERIFY2(lastNotice(notices).contains(needle), qPrintable(lastNotice(notices)));
        verifyPlayerCopy(lastNotice(notices));
        QCOMPARE(rereads.count(), 1);
        QVERIFY(!c.busy());
    }

    void shareOpensTheIssueAndCopiesTheCode()
    {
        SharedBackend backend;
        const QString url = QStringLiteral(
            "https://github.com/Harendotes-Tang/MentorRecorder-Calibrations/issues/new?template=share-calibration.yml&code=")
            + QLatin1String(kVectorCode);
        backend.shareResult = {{QStringLiteral("code"), QLatin1String(kVectorCode)},
                               {QStringLiteral("code_sha256"), QString(64, QLatin1Char('a'))},
                               {QStringLiteral("issue_url"), url},
                               {QStringLiteral("code_in_url"), true}};
        mr::SharedCalibrationController c;
        c.setBackend(&backend);
        Seams seams;
        seams.attach(c);
        QSignalSpy notices(&c, &mr::SharedCalibrationController::notice);

        c.share();
        QCOMPARE(backend.calls, QStringList{QStringLiteral("GetCalibrationShareCode")});
        QVERIFY(backend.payloads.first().isEmpty());
        QCOMPARE(seams.copied, QStringList{QLatin1String(kVectorCode)});
        QCOMPARE(seams.opened.size(), 1);
        QCOMPARE(seams.opened.first(), QUrl(url));
        QVERIFY(lastNotice(notices).contains(QString::fromUtf8("浏览器")));
        QVERIFY(lastNotice(notices).contains(QString::fromUtf8("剪贴板")));
        verifyPlayerCopy(lastNotice(notices));

        // No browser at all: the code is still on the clipboard, and the toast says so.
        seams.openResult = false;
        c.share();
        QCOMPARE(seams.copied.size(), 2);
        QVERIFY(lastNotice(notices).contains(QString::fromUtf8("无法调用系统浏览器")));
    }

    void shareOnlyCopiesWhenTheCodeDidNotFitTheUrl()
    {
        SharedBackend backend;
        backend.shareResult = {{QStringLiteral("code"), QLatin1String(kVectorCode)},
                               {QStringLiteral("code_sha256"), QString(64, QLatin1Char('a'))},
                               {QStringLiteral("issue_url"),
                                QStringLiteral("https://github.com/x/y/issues/new?template=share-calibration.yml")},
                               {QStringLiteral("code_in_url"), false}};
        mr::SharedCalibrationController c;
        c.setBackend(&backend);
        Seams seams;
        seams.attach(c);
        QSignalSpy notices(&c, &mr::SharedCalibrationController::notice);

        c.share();
        QVERIFY(seams.opened.isEmpty());
        QCOMPARE(seams.copied, QStringList{QLatin1String(kVectorCode)});
        QVERIFY(lastNotice(notices).contains(QString::fromUtf8("剪贴板")));
        QVERIFY(lastNotice(notices).contains(QString::fromUtf8("没有打开浏览器")));
        verifyPlayerCopy(lastNotice(notices));
    }

    void shareNeverOpensAnAddressOutsideGithub()
    {
        QVERIFY(mr::SharedCalibrationController::isShareIssueUrl(QUrl(QStringLiteral("https://github.com/a/b/issues/new"))));
        for (const char *bad : {"http://github.com/a", "https://github.com.example.com/a", "https://gist.github.com/a",
                                "https://user@github.com/a", "https://github.com:8443/a", "file:///C:/x", ""}) {
            QVERIFY2(!mr::SharedCalibrationController::isShareIssueUrl(QUrl(QString::fromLatin1(bad))), bad);
        }

        SharedBackend backend;
        backend.shareResult = {{QStringLiteral("code"), QLatin1String(kVectorCode)},
                               {QStringLiteral("issue_url"), QStringLiteral("https://example.com/issues/new")},
                               {QStringLiteral("code_in_url"), true}};
        mr::SharedCalibrationController c;
        c.setBackend(&backend);
        Seams seams;
        seams.attach(c);
        QSignalSpy notices(&c, &mr::SharedCalibrationController::notice);
        c.share();
        QVERIFY(seams.opened.isEmpty());
        QCOMPARE(seams.copied.size(), 1);
        QVERIFY(lastNotice(notices).contains(QString::fromUtf8("没有打开浏览器")));
    }

    void shareRefusalIsExplainedByReason_data()
    {
        QTest::addColumn<QString>("reason");
        QTest::addColumn<QString>("needle");
        QTest::newRow("no-profile") << "NO_PROFILE" << QString::fromUtf8("校准完成之后");
        QTest::newRow("not-local") << "NOT_LOCAL" << QString::fromUtf8("随软件附带");
        QTest::newRow("shared") << "SHARED" << QString::fromUtf8("不能再转手");
        QTest::newRow("not-shareable") << "NOT_SHAREABLE" << QString::fromUtf8("暂时不能分享");
        QTest::newRow("missing") << "" << QString::fromUtf8("暂时不能分享");
    }

    void shareRefusalIsExplainedByReason()
    {
        QFETCH(QString, reason);
        QFETCH(QString, needle);
        SharedBackend backend;
        backend.failType = QStringLiteral("GetCalibrationShareCode");
        backend.errorCode = QStringLiteral("ERR_SHARE_CODE_UNAVAILABLE");
        backend.errorMessage = QStringLiteral("profile file drifted: D:/data/cn.local.json");
        if (!reason.isEmpty())
            backend.errorDetails = {{QStringLiteral("reason"), reason}};
        mr::SharedCalibrationController c;
        c.setBackend(&backend);
        Seams seams;
        seams.attach(c);
        QSignalSpy notices(&c, &mr::SharedCalibrationController::notice);

        c.share();
        QCOMPARE(lastNotice(notices), mr::SharedCalibrationController::shareRefusalMessage(reason));
        QVERIFY2(lastNotice(notices).contains(needle), qPrintable(lastNotice(notices)));
        verifyPlayerCopy(lastNotice(notices));
        QVERIFY(seams.opened.isEmpty());
        QVERIFY(seams.copied.isEmpty());
    }

    void importShowsTheCollectorsMessageAsIs_data()
    {
        QTest::addColumn<QString>("outcome");
        QTest::addColumn<QString>("message");
        QTest::addColumn<bool>("applied");
        QTest::addColumn<QString>("reason");
        QTest::addColumn<QString>("provenance");
        QTest::newRow("applied") << "APPLIED"
            << QString::fromUtf8("校准码已导入，登录或排本时会在本机流量里自动核实。") << true << "" << "";
        QTest::newRow("not-applicable") << "NOT_APPLICABLE"
            << QString::fromUtf8("这份校准码适用于客户端版本 2026.09.02.0000.0000，当前客户端版本是 2026.09.01.0000.0000，不能通用。")
            << false << "OTHER_BUILD" << "";
        QTest::newRow("malformed") << "MALFORMED"
            << QString::fromUtf8("这不是一份能识别的校准码：内容为空。") << false
            << "E_SHARE_CODE_EMPTY" << "";
        // §18.5: the Collector's own sentence is the one that distinguishes a code the
        // index lists from one it does not, and a revoked one from a malformed one. The
        // desktop adds nothing to it and drops nothing from it.
        QTest::newRow("applied-published") << "APPLIED"
            << QString::fromUtf8("校准码已导入：它与公开仓库里 3 人提交的校准一致，下次登录核实通过就开始记录。")
            << true << "" << "PUBLISHED";
        QTest::newRow("applied-unpublished") << "APPLIED"
            << QString::fromUtf8("校准码已导入：它没有在公开仓库里发布过，要登录并排一次本、核实通过后才启用。")
            << true << "" << "IMPORTED";
        QTest::newRow("revoked") << "NOT_APPLICABLE"
            << QString::fromUtf8("这份校准码已被撤回，不能再用。")
            << false << "REVOKED" << "";
    }

    void importShowsTheCollectorsMessageAsIs()
    {
        QFETCH(QString, outcome);
        QFETCH(QString, message);
        QFETCH(bool, applied);
        QFETCH(QString, reason);
        QFETCH(QString, provenance);
        SharedBackend backend;
        backend.importResult = {{QStringLiteral("outcome"), outcome},
                                {QStringLiteral("reason"), reason.isEmpty() ? QJsonValue(QJsonValue::Null)
                                                                            : QJsonValue(reason)},
                                {QStringLiteral("message"), message},
                                {QStringLiteral("code_sha256"), QJsonValue::Null}};
        if (!provenance.isEmpty())
            backend.importResult.insert(QStringLiteral("provenance"), provenance);
        mr::SharedCalibrationController c;
        c.setBackend(&backend);
        QSignalSpy finished(&c, &mr::SharedCalibrationController::importFinished);
        QSignalSpy notices(&c, &mr::SharedCalibrationController::notice);
        QSignalSpy rereads(&c, &mr::SharedCalibrationController::refreshRequested);

        c.importCode(QStringLiteral("  \n") + QLatin1String(kVectorCode) + QStringLiteral("\n"));
        QCOMPARE(backend.calls, QStringList{QStringLiteral("ImportCalibrationCode")});
        QCOMPARE(backend.payloads.first(), (QJsonObject{{QStringLiteral("code"), QLatin1String(kVectorCode)}}));
        QCOMPARE(finished.count(), 1);
        QCOMPARE(finished.first().at(0).toBool(), applied);
        QCOMPARE(finished.first().at(1).toString(), message);
        QCOMPARE(notices.count(), applied ? 1 : 0);
        QCOMPARE(rereads.count(), applied ? 1 : 0);
        if (applied)
            QCOMPARE(lastNotice(notices), message);
    }

    void onlyAnEmptyImportMessageFallsBackToOurOwnSentence()
    {
        SharedBackend backend;
        backend.importResult = {{QStringLiteral("outcome"), QStringLiteral("APPLIED")},
                                {QStringLiteral("reason"), QJsonValue::Null},
                                {QStringLiteral("message"), QString()},
                                {QStringLiteral("code_sha256"), QJsonValue::Null}};
        mr::SharedCalibrationController c;
        c.setBackend(&backend);
        QSignalSpy finished(&c, &mr::SharedCalibrationController::importFinished);
        c.importCode(QLatin1String(kVectorCode));
        QCOMPARE(finished.count(), 1);
        QVERIFY(finished.first().at(0).toBool());
        QVERIFY(!finished.first().at(1).toString().isEmpty());
        verifyPlayerCopy(finished.first().at(1).toString());
    }

    void anEmptyImportIsNeverSent()
    {
        SharedBackend backend;
        mr::SharedCalibrationController c;
        c.setBackend(&backend);
        QSignalSpy finished(&c, &mr::SharedCalibrationController::importFinished);
        c.importCode(QStringLiteral(" \t\n"));
        QVERIFY(backend.calls.isEmpty());
        QCOMPARE(finished.count(), 1);
        QVERIFY(!finished.first().at(0).toBool());
        QVERIFY(finished.first().at(1).toString().contains(QString::fromUtf8("粘贴")));
    }

    void aSecondImportWhileTheFirstIsPendingIsAnswered()
    {
        SharedBackend backend;
        backend.holdType = QStringLiteral("ImportCalibrationCode");
        mr::SharedCalibrationController c;
        c.setBackend(&backend);
        QSignalSpy finished(&c, &mr::SharedCalibrationController::importFinished);
        c.importCode(QLatin1String(kVectorCode));
        QVERIFY(c.busy());
        QCOMPARE(finished.count(), 0);

        // The dialog waits for importFinished; a refused second send must still answer it.
        c.importCode(QLatin1String(kVectorCode));
        QCOMPARE(backend.count("ImportCalibrationCode"), 1);
        QCOMPARE(finished.count(), 1);
        QVERIFY(!finished.first().at(0).toBool());
        verifyPlayerCopy(finished.first().at(1).toString());
    }

    void acceptSendsAnEmptyRequestAndRereadsStatus()
    {
        SharedBackend backend;
        backend.outcome = QStringLiteral("ACCEPTED");
        mr::SharedCalibrationController c;
        c.setBackend(&backend);
        QSignalSpy notices(&c, &mr::SharedCalibrationController::notice);
        QSignalSpy rereads(&c, &mr::SharedCalibrationController::refreshRequested);
        c.acceptQueueInference();
        QCOMPARE(backend.calls, QStringList{QStringLiteral("AcceptSharedQueueInference")});
        QVERIFY(backend.payloads.first().isEmpty());
        QCOMPARE(rereads.count(), 1);
        verifyPlayerCopy(lastNotice(notices));
    }

    void rejectSendsAnEmptyRequestAndRereadsStatus_data()
    {
        QTest::addColumn<QString>("withdrawn");
        QTest::addColumn<int>("dropped");
        QTest::addColumn<QString>("needle");
        QTest::newRow("withdrawn") << "cn.2026.09.01.0000.0000.shared" << 0 << QString::fromUtf8("已停用");
        QTest::newRow("dropped") << "" << 2 << QString::fromUtf8("已丢弃");
        QTest::newRow("nothing") << "" << 0 << QString::fromUtf8("已记下你的选择");
    }

    void rejectSendsAnEmptyRequestAndRereadsStatus()
    {
        QFETCH(QString, withdrawn);
        QFETCH(int, dropped);
        QFETCH(QString, needle);
        SharedBackend backend;
        backend.rejectResult = {{QStringLiteral("withdrawn_profile_id"),
                                 withdrawn.isEmpty() ? QJsonValue(QJsonValue::Null) : QJsonValue(withdrawn)},
                                {QStringLiteral("dropped_candidates"), dropped}};
        mr::SharedCalibrationController c;
        c.setBackend(&backend);
        QSignalSpy notices(&c, &mr::SharedCalibrationController::notice);
        QSignalSpy rereads(&c, &mr::SharedCalibrationController::refreshRequested);
        c.reject();
        QCOMPARE(backend.calls, QStringList{QStringLiteral("RejectSharedCalibration")});
        QVERIFY(backend.payloads.first().isEmpty());
        QVERIFY2(lastNotice(notices).contains(needle), qPrintable(lastNotice(notices)));
        // The withdrawn profile id is diagnostics, not a sentence.
        verifyPlayerCopy(lastNotice(notices));
        QCOMPARE(rereads.count(), 1);
    }

    void errorDetailsReachAHandlerOnBothDeliveryPaths()
    {
        QObject owner;
        const QJsonObject details{{QStringLiteral("reason"), QStringLiteral("SHARED")}};

        auto *later = new mr::BackendReply(QStringLiteral("1"), QStringLiteral("GetCalibrationShareCode"), &owner);
        QPointer<mr::BackendReply> laterGuard(later);
        QString seenLater;
        later->whenDone(&owner, [&](bool, const QVariantMap &, const QString &, const QString &) {
            seenLater = laterGuard ? laterGuard->errorDetails().value(QStringLiteral("reason")).toString() : QString();
        });
        later->fail(QStringLiteral("ERR_SHARE_CODE_UNAVAILABLE"), QStringLiteral("x"), details);
        QCOMPARE(seenLater, QStringLiteral("SHARED"));

        auto *already = new mr::BackendReply(QStringLiteral("2"), QStringLiteral("GetCalibrationShareCode"), &owner);
        QPointer<mr::BackendReply> alreadyGuard(already);
        already->fail(QStringLiteral("ERR_SHARE_CODE_UNAVAILABLE"), QStringLiteral("x"), details);
        QString seenAlready;
        already->whenDone(&owner, [&](bool, const QVariantMap &, const QString &, const QString &) {
            seenAlready = alreadyGuard ? alreadyGuard->errorDetails().value(QStringLiteral("reason")).toString() : QString();
        });
        QCOMPARE(seenAlready, QStringLiteral("SHARED"));
    }

    // -- application wiring -------------------------------------------------

    void theSettingsSwitchReachesTheCollector()
    {
        mr::MockBackend backend;
        mr::AppController app(&backend, nullptr);
        QTRY_VERIFY(app.captureSettingsLoaded());
        QVERIFY(app.captureSettings().value(QStringLiteral("shared_calibration_enabled")).toBool());

        // Without the whitelist entry the write is dropped before the debounce.
        app.updateCaptureSetting(QStringLiteral("shared_calibration_enabled"), false);
        QVERIFY(!app.captureSettings().value(QStringLiteral("shared_calibration_enabled")).toBool());
        QTest::qWait(1200);
        QVERIFY(!app.captureSettings().value(QStringLiteral("shared_calibration_enabled")).toBool());
        QVERIFY(app.captureSettingsError().isEmpty());

        app.updateCaptureSetting(QStringLiteral("shared_calibration_enabled"), true);
        QTest::qWait(1200);
        QVERIFY(app.captureSettings().value(QStringLiteral("shared_calibration_enabled")).toBool());
        QVERIFY(app.captureSettingsError().isEmpty());
    }

    void outcomesBecomeToastsAndTheStatusIsReread()
    {
        SharedBackend backend;
        mr::AppController app(&backend, nullptr);
        QTest::qWait(80);
        backend.calls.clear();
        app.calibration()->shared()->reject();
        QTRY_COMPARE(backend.count("GetCaptureStatus"), 1);
        QVERIFY(app.toastMessage().contains(QString::fromUtf8("已丢弃")));
        QCOMPARE(backend.count("QueryRuns"), 0);
    }

    void aSharedProfileListensUnderItsOwnName()
    {
        SharedBackend backend;
        backend.capture.insert(QStringLiteral("profile_status"), QStringLiteral("VERIFIED"));
        backend.capture.insert(QStringLiteral("profile_origin"), QStringLiteral("SHARED_CALIBRATION"));
        backend.capture.insert(QStringLiteral("calibration"),
                               QJsonObject::fromVariantMap(captureWith(
                                   QStringLiteral("OBSERVING"), sharedStatus(QStringLiteral("VERIFIED")))
                                   .value(QStringLiteral("calibration")).toMap()));
        mr::AutomaticRecordingController recording(&backend);
        recording.refresh();
        QTRY_COMPARE(recording.state(), QStringLiteral("listening"));
        QVERIFY(!recording.attention());
        QVERIFY(recording.message().contains(QString::fromUtf8("其他玩家分享")));
        verifyPlayerCopy(recording.message());
    }

    void consentIsAskedForOnTheBanner()
    {
        SharedBackend backend;
        backend.capture.insert(QStringLiteral("calibration"),
                               QJsonObject::fromVariantMap(captureWith(
                                   QStringLiteral("OBSERVING"), sharedStatus(QStringLiteral("AWAITING_CONSENT")))
                                   .value(QStringLiteral("calibration")).toMap()));
        mr::AutomaticRecordingController recording(&backend);
        recording.refresh();
        QTRY_COMPARE(recording.state(), QStringLiteral("calibrating"));
        QVERIFY(recording.attention());
        QVERIFY(!recording.pendingAlert());
        QVERIFY(recording.message().contains(QString::fromUtf8("同意")));
        verifyPlayerCopy(recording.message());
    }

    // -- mock fixtures ------------------------------------------------------

    void everyMockStateProjects_data()
    {
        QTest::addColumn<QString>("fixture");
        QTest::addColumn<QString>("view");
        for (const auto &[fixture, view] : std::initializer_list<std::pair<const char *, const char *>>{
                 {"fetching", "fetching"}, {"verifying", "verifying"}, {"consent", "consent"},
                 {"verified", "verified"}, {"verified-auditing", "verified"},
                 {"imported-published", "verifying"}, {"imported-unpublished", "verifying"},
                 {"rejected", "rejected"}, {"unavailable", "unavailable"},
                 {"user-rejected", "user_rejected"}, {"none-for-build", "none_for_build"}, {"share", "none"}}) {
            QTest::newRow(fixture) << QString::fromLatin1(fixture) << QString::fromLatin1(view);
        }
    }

    void everyMockStateProjects()
    {
        QFETCH(QString, fixture);
        QFETCH(QString, view);
        mr::MockBackend backend;
        backend.setSharedCalibrationFixture(fixture);
        mr::AppController app(&backend, nullptr);
        QTRY_VERIFY(app.calibration()->shared()->available());
        QCOMPARE(app.calibration()->shared()->view(), view);
        QCOMPARE(app.calibration()->shared()->canShare(), fixture == QLatin1String("share"));
        QCOMPARE(app.calibration()->shared()->inUse(),
                 fixture.startsWith(QStringLiteral("verified")));
        verifyPlayerCopy(app.calibration()->shared()->headline(), fixture == QLatin1String("share"));
        verifyPlayerCopy(app.calibration()->shared()->detail(), true);
    }

    void theThreeGradedMockStatesProjectTheirOwnSentences_data()
    {
        QTest::addColumn<QString>("fixture");
        QTest::addColumn<QString>("provenance");
        QTest::addColumn<bool>("auditPending");
        QTest::addColumn<QString>("headline");
        QTest::addColumn<QString>("detail");

        QTest::newRow("verified-auditing")
            << "verified-auditing" << "published" << true
            << QString::fromUtf8("已使用其他玩家分享的校准（登录时已在本机核实）。")
            << QString::fromUtf8("排本和进本还在核对中");
        QTest::newRow("imported-published")
            << "imported-published" << "published" << false
            << QString::fromUtf8("找到共享校准，登录时自动核实，通过就开始记录。")
            << QString::fromUtf8("核实通过才会开始记录");
        QTest::newRow("imported-unpublished")
            << "imported-unpublished" << "imported" << false
            << QString::fromUtf8("已导入校准码，登录并排一次本、核实通过后启用。")
            << QString::fromUtf8("核实通过才会开始记录");
    }

    void theThreeGradedMockStatesProjectTheirOwnSentences()
    {
        QFETCH(QString, fixture);
        QFETCH(QString, provenance);
        QFETCH(bool, auditPending);
        QFETCH(QString, headline);
        QFETCH(QString, detail);

        mr::MockBackend backend;
        backend.setSharedCalibrationFixture(fixture);
        mr::AppController app(&backend, nullptr);
        auto *shared = app.calibration()->shared();
        QTRY_VERIFY(shared->available());
        QTRY_COMPARE(shared->candidateProvenance(), provenance);
        QCOMPARE(shared->auditPending(), auditPending);
        QCOMPARE(shared->headline(), headline);
        QVERIFY2(shared->detail().contains(detail), qPrintable(shared->detail()));
        verifyPlayerCopy(shared->headline());
        verifyPlayerCopy(shared->detail());
    }

    void theMockSharesALocalCalibrationAndRefusesASharedOne()
    {
        mr::MockBackend local;
        local.setSharedCalibrationFixture(QStringLiteral("share"));
        mr::AppController app(&local, nullptr);
        QTRY_VERIFY(app.calibration()->shared()->canShare());
        Seams seams;
        seams.attach(*app.calibration()->shared());
        app.calibration()->shared()->share();
        QTRY_COMPARE(seams.copied.size(), 1);
        QVERIFY(seams.copied.first().startsWith(QStringLiteral("MRC1.")));
        QCOMPARE(seams.opened.size(), 1);
        QVERIFY(mr::SharedCalibrationController::isShareIssueUrl(seams.opened.first()));

        mr::MockBackend shared;
        shared.setSharedCalibrationFixture(QStringLiteral("verified"));
        mr::AppController other(&shared, nullptr);
        QTRY_VERIFY(other.calibration()->shared()->inUse());
        Seams otherSeams;
        otherSeams.attach(*other.calibration()->shared());
        other.calibration()->shared()->share();
        QTRY_COMPARE(other.toastMessage(), mr::SharedCalibrationController::shareRefusalMessage(QStringLiteral("SHARED")));
        QVERIFY(otherSeams.copied.isEmpty());
    }

    void theMockImportsRejectsAndForgetsTheRefusal()
    {
        mr::MockBackend backend;
        backend.setSharedCalibrationFixture(QStringLiteral("unavailable"));
        mr::AppController app(&backend, nullptr);
        auto *shared = app.calibration()->shared();
        QTRY_COMPARE(shared->view(), QStringLiteral("unavailable"));

        QSignalSpy finished(shared, &mr::SharedCalibrationController::importFinished);
        shared->importCode(QStringLiteral("not a code"));
        QTRY_COMPARE(finished.count(), 1);
        QVERIFY(!finished.first().at(0).toBool());
        verifyPlayerCopy(finished.first().at(1).toString());

        shared->importCode(QLatin1String(kVectorCode));
        QTRY_COMPARE(finished.count(), 2);
        QVERIFY(finished.last().at(0).toBool());
        QTRY_COMPARE(shared->view(), QStringLiteral("verifying"));
        QVERIFY(shared->headline().contains(QString::fromUtf8("已导入校准码")));

        shared->reject();
        QTRY_COMPARE(shared->view(), QStringLiteral("user_rejected"));
        app.calibration()->discard();
        QTRY_VERIFY(shared->view() != QLatin1String("user_rejected"));
    }
};

int main(int argc, char **argv)
{
    // Never run against the user's own Collector, database or serve lease - see
    // TestCollectorGuard.h. Must precede any construction.
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QGuiApplication app(argc, argv);
    SharedCalibrationTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "SharedCalibrationTests.moc"
