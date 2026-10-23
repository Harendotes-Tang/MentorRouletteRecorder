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
//   * which share is offered: this machine's own calibration ("local"), or
//     another player's that proved itself here ("verified", §20.6), and the
//     toast that follows the basis the Collector names;
//   * 导入校准码 shows the Collector's own message and never sends empty text;
//   * 立即检查 / consent / 不用共享的 send empty requests and re-read the status;
//   * shared_calibration_enabled passes the AppController's settings whitelist;
//   * the mock's --mock-shared fixtures project to the states they are named for.
// The card itself, in shipping QML, is SharedCalibrationCardTests.cpp.
// ---------------------------------------------------------------------------

#include "DesktopTestRunner.h"
#include "TestCollectorGuard.h"
#include "AppController.h"
#include "AutomaticRecordingController.h"
#include "CalibrationController.h"
#include "CollectorText.h"
#include "CollectorTextTestSupport.h"
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
/// popGate adds the match criterion with the gate the Collector judges it by
/// (criteria[].gate); empty leaves every gate out, as a Collector before 1.1.0
/// does and as a candidate reads before it was first judged.
QJsonObject candidate(const QString &source, const QString &status = QStringLiteral("VERIFYING"),
                      const QString &provenance = QString(), bool auditPending = false,
                      const QString &popGate = QString())
{
    QJsonObject zone{{QStringLiteral("message"), QStringLiteral("ZONE_INITIALIZATION")},
                     {QStringLiteral("verdict"), QStringLiteral("WAIT")},
                     {QStringLiteral("reason"), QString::fromUtf8("还没有见到登录时的换区。")},
                     {QStringLiteral("contradicting_sessions"), 0}};
    QJsonArray criteria{zone};
    if (!popGate.isEmpty()) {
        zone.insert(QStringLiteral("gate"), QStringLiteral("REQUIRED"));
        criteria = QJsonArray{zone, QJsonObject{
            {QStringLiteral("message"), QStringLiteral("CONTENT_FINDER_POP")},
            {QStringLiteral("verdict"), QStringLiteral("WAIT")},
            {QStringLiteral("reason"), QString::fromUtf8("等待识别这条匹配成功跟在排本之后出现；排一次随机任务就能核实。")},
            {QStringLiteral("contradicting_sessions"), 0},
            {QStringLiteral("gate"), popGate}}};
    }
    QJsonObject row{{QStringLiteral("sha12"), QStringLiteral("67ef1bb97e65")},
                    {QStringLiteral("source"), source},
                    {QStringLiteral("match_source"), QStringLiteral("REPLY_STATE")},
                    {QStringLiteral("status"), status},
                    {QStringLiteral("verdict"), QStringLiteral("WAIT")},
                    {QStringLiteral("criteria"), criteria},
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
                        const QString &origin = QString(), const QString &profileStatus = QString())
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
    if (!profileStatus.isEmpty())
        capture.insert(QStringLiteral("profile_status"), profileStatus);
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
        QStringLiteral("IMPORTED"), QStringLiteral("audit_pending"),
        // The share-code basis and the candidate marks (plans/shared-calibration.md §20).
        QStringLiteral("PROVEN"), QStringLiteral("CALIBRATED"), QStringLiteral("basis"),
        QStringLiteral("shape_prior"), QStringLiteral("disputed"), QStringLiteral("preferred"),
        QStringLiteral("CONTENT_FINDER_POP"), QStringLiteral("ZONE_INITIALIZATION"),
        QStringLiteral("ZONE_TERRITORY"), QStringLiteral("PLAYER_JOB"), QStringLiteral("IN_USE"),
        QStringLiteral("SHARED_CALIBRATION"), QStringLiteral("LOCAL_CALIBRATION"),
        QStringLiteral("GetCalibrationShareCode"), QStringLiteral("SHARED_NOT_PROVEN")};
    for (const QString &word : words) {
        QVERIFY2(!text.contains(word, Qt::CaseInsensitive),
                 qPrintable(QStringLiteral("player copy leaks \"%1\": %2").arg(word, text)));
    }
}

/// The profile in use as a Collector reports it once it recorded one complete duty and
/// nothing is left to audit (plans/shared-calibration.md §20.6), with the §20.5 marks the
/// desktop never shows.  marks false leaves those out, as every Collector before §20 does
/// - one that already reports PROVEN but still refuses to hand the code out.
QJsonObject provenCandidate(bool auditPending = false,
                            const QString &provenance = QStringLiteral("PUBLISHED"), bool marks = true)
{
    QJsonObject row = candidate(QStringLiteral("DOWNLOADED"), QStringLiteral("PROVEN"), provenance,
                                auditPending, QStringLiteral("AUDIT"));
    if (marks) {
        row.insert(QStringLiteral("disputed"), QJsonArray{QStringLiteral("CONTENT_FINDER_POP")});
        row.insert(QStringLiteral("preferred"), true);
        row.insert(QStringLiteral("shape_prior"), true);
    }
    return row;
}

/// GetCalibrationShareCode as the Collector answers it, with the basis it names.
QJsonObject shareAnswer(const QString &basis, bool codeInUrl = true,
                        const QString &host = QStringLiteral("https://github.com/"))
{
    QString url = host + QStringLiteral("Harendotes-Tang/MentorRecorder-Calibrations/issues/new"
                                        "?template=share-calibration.yml");
    if (codeInUrl)
        url += QStringLiteral("&basis=") + basis + QStringLiteral("&code=") + QLatin1String(kVectorCode);
    QJsonObject answer{{QStringLiteral("code"), QLatin1String(kVectorCode)},
                       {QStringLiteral("code_sha256"), QString(64, QLatin1Char('a'))},
                       {QStringLiteral("issue_url"), url},
                       {QStringLiteral("code_in_url"), codeInUrl}};
    if (!basis.isEmpty())
        answer.insert(QStringLiteral("basis"), basis);
    return answer;
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
    void setShareResult(const QJsonObject &result) { shareResult = result; }
    void setCheckExtra(const QJsonObject &extra) { checkExtra = extra; }
    QJsonObject shareResult;
    QString failType;
    /// A message whose reply is left unanswered, so the controller stays busy.
    QString holdType;
    QString errorCode;
    QString errorMessage;
    QJsonObject errorDetails;
    QString outcome = QStringLiteral("STARTED");
    /// More fields of the CheckSharedCalibration answer (its message / message_ref).
    QJsonObject checkExtra;
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
        return static_cast<int>(std::count(calls.begin(), calls.end(), QLatin1String(type)));
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
        if (type == QLatin1String("CheckSharedCalibration")) {
            QJsonObject result = checkExtra;
            result.insert(QStringLiteral("outcome"), outcome);
            return result;
        }
        if (type == QLatin1String("AcceptSharedQueueInference"))
            return {{QStringLiteral("outcome"), outcome}};
        return {};
    }
};

struct Seams
{
    QList<QUrl> opened;
    QStringList copied;
    bool openResult = true;
    void setOpenResult(bool result) { openResult = result; }

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

namespace {
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
                                  << QString::fromUtf8("正在查找其他玩家分享的校准");
        QTest::newRow("verifying") << "VERIFYING" << "OK" << false << "verifying"
                                   << QString::fromUtf8("已找到共享校准，等待本机验证");
        QTest::newRow("consent") << "AWAITING_CONSENT" << "OK" << false << "consent"
                                 << QString::fromUtf8("确认是否使用");
        QTest::newRow("verified") << "VERIFIED" << "OK" << false << "verified"
                                  << QString::fromUtf8("正在使用共享校准，已通过本机检查");
        QTest::newRow("rejected") << "REJECTED" << "OK" << false << "rejected"
                                  << QString::fromUtf8("这份共享校准不适用于你的游戏，已改为本机校准");
        QTest::newRow("index-unavailable") << "UNAVAILABLE" << "INDEX_UNAVAILABLE" << false << "unavailable"
                                           << QString::fromUtf8("暂时没能下载共享校准，将继续本机校准");
        QTest::newRow("codes-unavailable") << "UNAVAILABLE" << "CODES_UNAVAILABLE" << false << "unavailable"
                                           << QString::fromUtf8("暂时没能下载共享校准，将继续本机校准");
        QTest::newRow("none-for-build") << "NONE" << "NONE_FOR_BUILD" << false << "none_for_build"
                                        << QString::fromUtf8("暂时没有当前游戏版本的共享校准，将继续本机校准");
        QTest::newRow("disabled") << "NONE" << "DISABLED" << false << "disabled"
                                  << QString::fromUtf8("已关闭");
        // A refusal also reads REJECTED; user_rejected has to be read first.
        QTest::newRow("user-rejected") << "REJECTED" << "OK" << true << "user_rejected"
                                       << QString::fromUtf8("已改为本机校准");
        QTest::newRow("user-rejected-first") << "VERIFYING" << "OK" << true << "user_rejected"
                                             << QString::fromUtf8("已改为本机校准");
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
        QVERIFY(controller.detail().contains(QString::fromUtf8("清空校准进度")));
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
        QVERIFY(controller.headline().contains(QString::fromUtf8("校准码已导入")));

        controller.refreshFromCaptureStatus(captureWith(
            QStringLiteral("OBSERVING"),
            sharedStatus(QStringLiteral("VERIFYING"), false, QStringLiteral("OK"),
                         {candidate(QStringLiteral("MANUAL")), candidate(QStringLiteral("DOWNLOADED"))})));
        QVERIFY(controller.headline().contains(QString::fromUtf8("已找到共享校准")));
    }

    // -- 18.3 / 18.4: gates graded by where the code came from ---------------

    void theVerifyingSentenceFollowsTheProvenance_data()
    {
        QTest::addColumn<QJsonArray>("candidates");
        QTest::addColumn<QString>("provenance");
        QTest::addColumn<QString>("headline");

        const QString published = QString::fromUtf8("已找到共享校准，等待本机验证。");
        const QString queue = QString::fromUtf8("已找到共享校准，等待本机验证。");
        const QString imported = QString::fromUtf8("校准码已导入。登录后申请随机任务并进入副本，通过检查后才能开始记录。");
        const QString audit = QStringLiteral("AUDIT");
        const QString required = QStringLiteral("REQUIRED");
        // A pasted code the last index read lists is judged like a downloaded one (18.5),
        // so "已导入" is not what decides the sentence any more - the gate is.
        QTest::newRow("downloaded-published")
            << QJsonArray{candidate(QStringLiteral("DOWNLOADED"), QStringLiteral("VERIFYING"),
                                    QStringLiteral("PUBLISHED"), false, audit)}
            << "published" << published;
        QTest::newRow("imported-and-listed")
            << QJsonArray{candidate(QStringLiteral("MANUAL"), QStringLiteral("VERIFYING"),
                                    QStringLiteral("PUBLISHED"), false, audit)}
            << "published" << published;
        QTest::newRow("imported-unpublished")
            << QJsonArray{candidate(QStringLiteral("MANUAL"), QStringLiteral("VERIFYING"),
                                    QStringLiteral("IMPORTED"))}
            << "imported" << imported;
        QTest::newRow("one-published-among-them")
            << QJsonArray{candidate(QStringLiteral("MANUAL"), QStringLiteral("VERIFYING"),
                                    QStringLiteral("IMPORTED"), false, required),
                          candidate(QStringLiteral("DOWNLOADED"), QStringLiteral("VERIFYING"),
                                    QStringLiteral("PUBLISHED"), false, audit)}
            << "published" << published;
        // Audit 2026-10-03 (ON1-1): a published code is used on the login check alone only
        // when nothing usable records, it carries no conflict mark and no other code has more
        // submitters. Otherwise its match is REQUIRED like a pasted code's, and promising
        // "通过就开始记录" at login would be false: one queue on this machine comes first.
        QTest::newRow("published-under-the-full-gate")
            << QJsonArray{candidate(QStringLiteral("DOWNLOADED"), QStringLiteral("VERIFYING"),
                                    QStringLiteral("PUBLISHED"), false, required)}
            << "published" << queue;
        QTest::newRow("listed-and-pasted-under-the-full-gate")
            << QJsonArray{candidate(QStringLiteral("MANUAL"), QStringLiteral("VERIFYING"),
                                    QStringLiteral("PUBLISHED"), false, required)}
            << "published" << queue;
        QTest::newRow("published-and-unpublished-both-under-the-full-gate")
            << QJsonArray{candidate(QStringLiteral("MANUAL"), QStringLiteral("VERIFYING"),
                                    QStringLiteral("IMPORTED"), false, required),
                          candidate(QStringLiteral("DOWNLOADED"), QStringLiteral("VERIFYING"),
                                    QStringLiteral("PUBLISHED"), false, required)}
            << "published" << queue;
        // One code that may be used at login is enough for that promise to hold.
        QTest::newRow("one-at-login-beside-one-under-the-full-gate")
            << QJsonArray{candidate(QStringLiteral("DOWNLOADED"), QStringLiteral("VERIFYING"),
                                    QStringLiteral("PUBLISHED"), false, required),
                          candidate(QStringLiteral("DOWNLOADED"), QStringLiteral("VERIFYING"),
                                    QStringLiteral("PUBLISHED"), false, audit)}
            << "published" << published;
        // A published code not judged yet reports no gate: neither promise can be made.
        QTest::newRow("published-not-judged-yet")
            << QJsonArray{candidate(QStringLiteral("DOWNLOADED"), QStringLiteral("VERIFYING"),
                                    QStringLiteral("PUBLISHED"))}
            << "published" << QString::fromUtf8("已找到共享校准，等待本机验证。");
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
            << QString::fromUtf8("已找到共享校准，等待本机验证。");
        QTest::newRow("not-reported-manual")
            << QJsonArray{candidate(QStringLiteral("MANUAL"))} << QString()
            << QString::fromUtf8("校准码已导入。登录或排本时会自动检查是否可用。");
        // One candidate without a provenance is not "all imported" either.
        QTest::newRow("half-reported")
            << QJsonArray{candidate(QStringLiteral("MANUAL"), QStringLiteral("VERIFYING"),
                                    QStringLiteral("IMPORTED")),
                          candidate(QStringLiteral("MANUAL"))}
            << QString()
            << QString::fromUtf8("校准码已导入。登录或排本时会自动检查是否可用。");
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

    // -- a better code checked underneath a profile that already records -----

    void aCandidateCheckedUnderARecordingProfileNeverSaysRecordingHasNotStarted_data()
    {
        QTest::addColumn<QString>("origin");
        QTest::addColumn<QString>("provenance");
        QTest::addColumn<QString>("headline");

        const QString better = QString::fromUtf8("找到了更完整的共享校准，正在检查是否可用。自动记录照常进行。");
        const QString pasted = QString::fromUtf8("校准码已导入，正在检查是否可用。自动记录照常进行。");
        // The machine's own queue-inferred calibration records; a code that reads the server's own
        // match is being checked underneath it.
        QTest::newRow("under a local queue-inferred profile")
            << "LOCAL_CALIBRATION" << "PUBLISHED" << better;
        QTest::newRow("under a local queue-inferred profile, pasted code")
            << "LOCAL_CALIBRATION" << "IMPORTED" << pasted;
    }

    void aCandidateCheckedUnderARecordingProfileNeverSaysRecordingHasNotStarted()
    {
        QFETCH(QString, origin);
        QFETCH(QString, provenance);
        QFETCH(QString, headline);

        mr::SharedCalibrationController c;
        const QJsonArray checking{candidate(QStringLiteral("DOWNLOADED"), QStringLiteral("VERIFYING"), provenance)};
        c.refreshFromCaptureStatus(captureWith(
            QStringLiteral("OBSERVING"),
            sharedStatus(QStringLiteral("VERIFYING"), false, QStringLiteral("OK"), checking), origin,
            QStringLiteral("VERIFIED")));

        QCOMPARE(c.view(), QStringLiteral("verifying"));
        QCOMPARE(c.headline(), headline);
        QCOMPARE(c.detail(),
                 QString::fromUtf8("自动记录照常进行。检查通过后会自动换用新的共享校准，已保存的记录不会改动。"));
        // The sentences from before 1.3.2 promised that nothing records yet; both would be false here.
        QVERIFY(!c.headline().contains(QString::fromUtf8("通过就开始记录")));
        QVERIFY(!c.detail().contains(QString::fromUtf8("检查通过后才能开始自动记录")));
        verifyPlayerCopy(c.headline());
        verifyPlayerCopy(c.detail());
    }

    /// With nothing in force the candidate explains the remaining login check.
    void aCandidateCheckedWithNothingRecordingExplainsTheNextStep()
    {
        mr::SharedCalibrationController c;
        const QJsonArray checking{candidate(QStringLiteral("DOWNLOADED"), QStringLiteral("VERIFYING"),
                                            QStringLiteral("PUBLISHED"), false,
                                            QStringLiteral("AUDIT"))};
        c.refreshFromCaptureStatus(captureWith(
            QStringLiteral("OBSERVING"),
            sharedStatus(QStringLiteral("VERIFYING"), false, QStringLiteral("OK"), checking), QString(),
            QStringLiteral("UNSUPPORTED_BUILD")));

        QCOMPARE(c.headline(), QString::fromUtf8("已找到共享校准，等待本机验证。"));
        QCOMPARE(c.detail(),
                 QString::fromUtf8("无需手动导入。登录游戏时会自动检查，检查通过后才能开始自动记录。如果不能使用，会继续本机校准，已有进度会保留。"));
    }

    void aRetrievedCandidateExplainsItsVerificationGate_data()
    {
        QTest::addColumn<QString>("gate");
        QTest::addColumn<QString>("nextStep");
        QTest::newRow("login") << "AUDIT" << QString::fromUtf8("登录游戏时会自动检查");
        QTest::newRow("queue") << "REQUIRED" << QString::fromUtf8("请申请随机任务并进入副本");
        QTest::newRow("unknown") << "" << QString::fromUtf8("登录或排本时会自动检查");
    }

    void aRetrievedCandidateExplainsItsVerificationGate()
    {
        QFETCH(QString, gate);
        QFETCH(QString, nextStep);
        mr::SharedCalibrationController controller;
        const QJsonArray checking{candidate(QStringLiteral("DOWNLOADED"), QStringLiteral("VERIFYING"),
                                            QStringLiteral("PUBLISHED"), false, gate)};
        controller.refreshFromCaptureStatus(captureWith(QStringLiteral("OBSERVING"),
            sharedStatus(QStringLiteral("VERIFYING"), false, QStringLiteral("OK"), checking)));
        QVERIFY(controller.detail().startsWith(QString::fromUtf8("无需手动导入。")));
        QVERIFY(controller.detail().contains(nextStep));
        QVERIFY(controller.detail().contains(QString::fromUtf8("检查通过后才能开始自动记录")));
        if (gate == QLatin1String("AUDIT"))
            QVERIFY(!controller.detail().contains(QString::fromUtf8("申请随机任务")));
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
                 QString::fromUtf8("正在使用共享校准，登录检查已通过。"));
        QCOMPARE(c.detail(),
                 QString::fromUtf8("排本和进本还在核对中，记录照常进行。如果之后发现不匹配，会停止使用这份校准并继续本机校准，期间生成的记录会标为“待复核”，请检查并补正。"));
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
                 QString::fromUtf8("正在使用共享校准，已通过本机检查。"));
        QVERIFY(c.detail().contains(QString::fromUtf8("能在你的游戏中使用")));
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
        // Never someone else's that has not proven itself here (no candidate says PROVEN), never
        // the shipped one, never without a profile, never on an old Collector.
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

    // -- which share is offered (plans/shared-calibration.md §20.6) ----------

    void shareKindFollowsTheProfileInForce_data()
    {
        QTest::addColumn<QString>("calibrationState");
        QTest::addColumn<QString>("origin");
        QTest::addColumn<QJsonObject>("shared");
        QTest::addColumn<QString>("kind");

        const QJsonObject none = sharedStatus(QStringLiteral("NONE"));
        const auto verified = [](const QJsonArray &candidates, bool auditPending = false) {
            return sharedStatus(QStringLiteral("VERIFIED"), false, QStringLiteral("OK"), candidates, auditPending);
        };
        const QJsonObject inUse = candidate(QStringLiteral("DOWNLOADED"), QStringLiteral("IN_USE"),
                                            QStringLiteral("PUBLISHED"), false, QStringLiteral("AUDIT"));

        // This machine's own calibration: today's 分享给其他玩家, whatever calibration is doing.
        QTest::newRow("local-done") << "DONE" << "LOCAL_CALIBRATION" << none << "local";
        QTest::newRow("local-restarted") << "IDLE" << "LOCAL_CALIBRATION" << none << "local";
        // Another player's calibration that recorded one complete duty here and has nothing
        // left to audit: the player can say it works.
        QTest::newRow("shared-proven") << "DONE" << "SHARED_CALIBRATION" << verified({provenCandidate()})
                                       << "verified";
        QTest::newRow("shared-proven-restarted") << "IDLE" << "SHARED_CALIBRATION"
                                                 << verified({provenCandidate()}) << "verified";
        // Still being watched, or still audited: nothing has been proven yet.
        QTest::newRow("shared-in-use") << "OBSERVING" << "SHARED_CALIBRATION" << verified({inUse}) << "";
        QTest::newRow("shared-candidate-audited") << "OBSERVING" << "SHARED_CALIBRATION"
                                                  << verified({provenCandidate(true)}) << "";
        QTest::newRow("shared-snapshot-audited") << "OBSERVING" << "SHARED_CALIBRATION"
                                                 << verified({provenCandidate()}, true) << "";
        // A pasted code no index lists can prove itself too, but the Collector keeps no text for
        // it and refuses every click (SHARED): only a published code is offered.
        QTest::newRow("proven-imported") << "DONE" << "SHARED_CALIBRATION"
                                         << verified({provenCandidate(false, QStringLiteral("IMPORTED"))}) << "";
        // Collectors 1.1.0-1.7.2 already report PROVEN for a bound shared profile but refuse
        // GetCalibrationShareCode for it; only a §20 Collector sends the candidate marks.
        QTest::newRow("proven-old-collector") << "DONE" << "SHARED_CALIBRATION"
            << verified({provenCandidate(false, QStringLiteral("PUBLISHED"), false)}) << "";
        // Only the profile in use - the first candidate - counts, never one behind it.
        QTest::newRow("proven-not-first") << "OBSERVING" << "SHARED_CALIBRATION"
                                          << verified({inUse, provenCandidate()}) << "";
        // A profile restored from disk whose code was not recovered lists no candidate.
        QTest::newRow("shared-no-candidate") << "IDLE" << "SHARED_CALIBRATION" << verified({}) << "";
        // A proven candidate never turns the shipped profile, or no profile, into a share.
        QTest::newRow("shipped") << "IDLE" << "SHIPPED" << verified({provenCandidate()}) << "";
        QTest::newRow("no-profile") << "OBSERVING" << "" << none << "";
        // An older Collector that sends no calibration.shared offers nothing to send the request with.
        QTest::newRow("old-collector-local") << "DONE" << "LOCAL_CALIBRATION" << QJsonObject() << "";
    }

    void shareKindFollowsTheProfileInForce()
    {
        QFETCH(QString, calibrationState);
        QFETCH(QString, origin);
        QFETCH(QJsonObject, shared);
        QFETCH(QString, kind);

        mr::SharedCalibrationController c;
        c.refreshFromCaptureStatus(captureWith(calibrationState, shared, origin, QStringLiteral("VERIFIED")));
        QCOMPARE(c.shareKind(), kind);
        QCOMPARE(c.canShare(), !kind.isEmpty());

        if (kind == QLatin1String("local")) {
            QCOMPARE(c.shareButtonText(), QString::fromUtf8("分享给其他玩家"));
            QCOMPARE(c.shareHintText(), mr::SharedCalibrationController::shareHint());
        } else if (kind == QLatin1String("verified")) {
            QCOMPARE(c.shareButtonText(), QString::fromUtf8("这份校准好用，告诉其他玩家"));
            QCOMPARE(c.shareHintText(), mr::SharedCalibrationController::verifiedShareHint());
            // Proven is not a different view: the card still says the shared profile is in use.
            QCOMPARE(c.view(), QStringLiteral("verified"));
            QVERIFY(c.inUse() && c.canReject());
        } else {
            QVERIFY(c.shareHintText().isEmpty());
        }
        verifyPlayerCopy(c.shareButtonText(), kind.isEmpty());
        verifyPlayerCopy(c.shareHintText(), kind.isEmpty());
        verifyPlayerCopy(c.headline(), true);
        verifyPlayerCopy(c.detail(), true);
    }

    void theVerifiedHintSaysWhatTheClickDoes()
    {
        QCOMPARE(mr::SharedCalibrationController::verifiedShareHint(),
                 QString::fromUtf8("会打开 GitHub 页面，把这份校准记为你也核实过，让其他玩家的软件更优先使用它。"));
        verifyPlayerCopy(mr::SharedCalibrationController::verifiedShareHint());
        // The local sentence is unchanged, and the two never read alike.
        QVERIFY(mr::SharedCalibrationController::shareHint().contains(QString::fromUtf8("本机校准")));
        QVERIFY(mr::SharedCalibrationController::shareHint()
                != mr::SharedCalibrationController::verifiedShareHint());
    }

    void theShareTextsChangeWithTheProfile()
    {
        // One controller across a restart of the evidence: the button and its hint must
        // follow, and changed() must say so, or a bound QML text would keep the old words.
        mr::SharedCalibrationController c;
        QSignalSpy changed(&c, &mr::SharedCalibrationController::changed);
        c.refreshFromCaptureStatus(captureWith(
            QStringLiteral("OBSERVING"),
            sharedStatus(QStringLiteral("VERIFIED"), false, QStringLiteral("OK"),
                         {candidate(QStringLiteral("DOWNLOADED"), QStringLiteral("IN_USE"),
                                    QStringLiteral("PUBLISHED"), false, QStringLiteral("AUDIT"))}),
            QStringLiteral("SHARED_CALIBRATION"), QStringLiteral("VERIFIED")));
        QCOMPARE(c.shareKind(), QString());
        changed.clear();

        c.refreshFromCaptureStatus(captureWith(
            QStringLiteral("DONE"),
            sharedStatus(QStringLiteral("VERIFIED"), false, QStringLiteral("OK"), {provenCandidate()}),
            QStringLiteral("SHARED_CALIBRATION"), QStringLiteral("VERIFIED")));
        QCOMPARE(c.shareKind(), QStringLiteral("verified"));
        QVERIFY(!changed.isEmpty());
    }

    // -- requests -----------------------------------------------------------

    void checkNowToastsEveryOutcome_data()
    {
        QTest::addColumn<QString>("outcome");
        QTest::addColumn<QString>("needle");
        QTest::newRow("started") << "STARTED" << QString::fromUtf8("正在查找");
        QTest::newRow("already") << "ALREADY_FETCHING" << QString::fromUtf8("正在查找，请稍等");
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

    // Audit RV5-5 (X6): with the refusal state unreadable the Collector still answers NOT_NEEDED
    // but says why. That sentence is shown - rendered from the catalogue in the interface
    // language - instead of claiming a usable calibration or a choice of local calibration.
    void checkNowShowsTheCollectorsReasonForNotLooking()
    {
        const QVariantMap ref = mrtest::textRef("sharing.check.state_unreadable");
        const QString unreadable = mr::collector_text::chinese(ref);
        QVERIFY(!unreadable.isEmpty());
        SharedBackend backend;
        backend.outcome = QStringLiteral("NOT_NEEDED");
        backend.checkExtra = {{QStringLiteral("message"), unreadable},
                              {QStringLiteral("message_ref"), QJsonObject::fromVariantMap(ref)}};
        mr::SharedCalibrationController c;
        c.setBackend(&backend);
        QSignalSpy notices(&c, &mr::SharedCalibrationController::notice);
        QSignalSpy rereads(&c, &mr::SharedCalibrationController::refreshRequested);
        c.checkNow();
        QCOMPARE(lastNotice(notices), unreadable);
        verifyPlayerCopy(lastNotice(notices));
        QCOMPARE(rereads.count(), 1);
        {
            const mrtest::MarkedLanguage language;
            c.checkNow();
            QCOMPARE(lastNotice(notices), mrtest::marked("sharing.check.state_unreadable", unreadable));
        }
        // A sentence without a catalogue reference is shown as the Collector wrote it.
        backend.checkExtra = {{QStringLiteral("message"), unreadable}};
        c.checkNow();
        QCOMPARE(lastNotice(notices), unreadable);
        // Without either, NOT_NEEDED keeps its own line.
        backend.setCheckExtra({});
        c.checkNow();
        QVERIFY2(lastNotice(notices).contains(QString::fromUtf8("暂时不需要查找")), qPrintable(lastNotice(notices)));
        QCOMPARE(rereads.count(), 4);
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
        seams.setOpenResult(false);
        c.share();
        QCOMPARE(seams.copied.size(), 2);
        QVERIFY(lastNotice(notices).contains(QString::fromUtf8("没能打开浏览器")));
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
        QVERIFY(lastNotice(notices).contains(QString::fromUtf8("无法自动打开 GitHub 分享页面")));
        // Review K2-5: the import button is 手动导入校准码 inside the folded 其他方式 now; the
        // sentence the other player follows names it where it is.
        QCOMPARE(lastNotice(notices),
                 QString::fromUtf8("校准码已复制到剪贴板。由于内容较长，无法自动打开 GitHub 分享页面。"
                                   "你可以直接把它发给其他玩家，让对方在「其他方式」中点「手动导入校准码」并粘贴。"));
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
        QVERIFY(lastNotice(notices).contains(QString::fromUtf8("分享页面地址有误")));
    }

    void theNoticeFollowsTheBasisOfTheCode_data()
    {
        QTest::addColumn<QString>("basis");
        QTest::addColumn<QString>("notice");
        // VERIFIED: another player's calibration this machine proved; the submission adds an account
        // that checked it rather than a new code.
        QTest::newRow("verified") << "VERIFIED"
            << QString::fromUtf8("已打开 GitHub 页面，校准码也已复制到剪贴板。"
                                 "提交后这份共享校准会多一个核实通过的账号，软件不会自动上传。");
        // CALIBRATED, and a Collector before §20 that names no basis at all: today's sentence.
        QTest::newRow("calibrated") << "CALIBRATED"
            << QString::fromUtf8("已打开 GitHub 分享页面，校准码也已复制到剪贴板。"
                                 "请在浏览器中确认是否提交，软件不会自动上传。");
        QTest::newRow("not-reported") << ""
            << QString::fromUtf8("已打开 GitHub 分享页面，校准码也已复制到剪贴板。"
                                 "请在浏览器中确认是否提交，软件不会自动上传。");
    }

    void theNoticeFollowsTheBasisOfTheCode()
    {
        QFETCH(QString, basis);
        QFETCH(QString, notice);
        SharedBackend backend;
        backend.setShareResult(shareAnswer(basis));
        mr::SharedCalibrationController c;
        c.setBackend(&backend);
        Seams seams;
        seams.attach(c);
        QSignalSpy notices(&c, &mr::SharedCalibrationController::notice);

        c.share();
        QCOMPARE(seams.copied, QStringList{QLatin1String(kVectorCode)});
        QCOMPARE(seams.opened.size(), 1);
        QCOMPARE(lastNotice(notices), notice);
        verifyPlayerCopy(lastNotice(notices));
    }

    void aVerifiedCodeThatCannotOpenThePageStillReadsRight()
    {
        // The code is the same length whoever shares it, so the long-code and wrong-address
        // fallbacks can meet a verified answer too. They keep their substance but talk about
        // "这份校准" and a GitHub page, not a sharing page the player did not ask for.
        SharedBackend backend;
        mr::SharedCalibrationController c;
        c.setBackend(&backend);
        Seams seams;
        seams.attach(c);
        QSignalSpy notices(&c, &mr::SharedCalibrationController::notice);

        backend.setShareResult(shareAnswer(QStringLiteral("VERIFIED"), false));
        c.share();
        QVERIFY(seams.opened.isEmpty());
        QCOMPARE(lastNotice(notices),
                 QString::fromUtf8("校准码已复制到剪贴板。由于内容较长，无法自动打开 GitHub 页面。"
                                   "你可以把这份校准直接发给其他玩家，让对方在「其他方式」中点「手动导入校准码」并粘贴。"));
        verifyPlayerCopy(lastNotice(notices));

        backend.setShareResult(shareAnswer(QStringLiteral("VERIFIED"), true, QStringLiteral("https://example.com/")));
        c.share();
        QVERIFY(seams.opened.isEmpty());
        QCOMPARE(lastNotice(notices),
                 QString::fromUtf8("校准码已复制到剪贴板。GitHub 页面地址有误，未打开浏览器；"
                                   "可以把这份校准直接发给其他玩家。"));
        verifyPlayerCopy(lastNotice(notices));

        // No browser at all reads the same either way.
        backend.setShareResult(shareAnswer(QStringLiteral("VERIFIED")));
        seams.setOpenResult(false);
        c.share();
        QCOMPARE(seams.opened.size(), 1);
        QCOMPARE(lastNotice(notices), QString::fromUtf8("没能打开浏览器。校准码已复制，可以直接发给其他玩家。"));
        QCOMPARE(seams.copied.size(), 3);
    }

    void shareRefusalIsExplainedByReason_data()
    {
        QTest::addColumn<QString>("reason");
        QTest::addColumn<QString>("needle");
        QTest::newRow("no-profile") << "NO_PROFILE" << QString::fromUtf8("请先完成校准");
        QTest::newRow("not-local") << "NOT_LOCAL" << QString::fromUtf8("软件自带");
        // §20.5: a shared profile in use is shared once it is proven; SHARED now only means the
        // code text is not on this machine.
        QTest::newRow("shared-not-proven") << "SHARED_NOT_PROVEN"
            << QString::fromUtf8("这份共享校准还在核对中，等它记录过一次完整的随机任务后再试。");
        QTest::newRow("shared") << "SHARED"
            << QString::fromUtf8("这份共享校准在本机找不到对应的校准码，暂时无法分享。");
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
            << QString::fromUtf8("校准码已导入。登录或排本时会自动检查是否可用。") << true << "" << "";
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

    // ImportCalibrationCode.message_ref (contracts/collector-texts.md): the answer is
    // shown in the interface language; a key this build does not know shows the
    // Collector's Chinese, as before the catalogue.
    void importShowsTheMessageByItsKey()
    {
        const QVariantMap why = mrtest::textRef("sharing.code.not_a_code");
        const QVariantMap ref = mrtest::textRef("sharing.import.not_imported", {{QStringLiteral("reason"), why}});
        const QString chinese = mr::collector_text::chinese(ref);
        QCOMPARE(chinese, QString::fromUtf8("校准码未导入：") + mr::collector_text::chinese(why));
        for (const bool known : {true, false}) {
            SharedBackend backend;
            backend.importResult = {{QStringLiteral("outcome"), QStringLiteral("MALFORMED")},
                                    {QStringLiteral("reason"), QStringLiteral("E_SHARE_CODE_NOT_A_CODE")},
                                    {QStringLiteral("message"), chinese},
                                    {QStringLiteral("message_ref"),
                                     known ? QJsonObject::fromVariantMap(ref)
                                           : QJsonObject{{QStringLiteral("key"), QStringLiteral("sharing.import.brand_new")}}},
                                    {QStringLiteral("code_sha256"), QJsonValue::Null}};
            mr::SharedCalibrationController c;
            c.setBackend(&backend);
            QSignalSpy finished(&c, &mr::SharedCalibrationController::importFinished);
            const mrtest::MarkedLanguage language;
            c.importCode(QLatin1String(kVectorCode));
            QCOMPARE(finished.count(), 1);
            // Nested sentences are rendered too: the reason reads in the same language.
            QCOMPARE(finished.first().at(1).toString(),
                     known ? QStringLiteral("[sharing.import.not_imported] ")
                                 + QString::fromUtf8("校准码未导入：")
                                 + mrtest::marked("sharing.code.not_a_code", mr::collector_text::chinese(why))
                           : chinese);
        }
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
        QTest::newRow("withdrawn") << "cn.2026.09.01.0000.0000.shared" << 0 << QString::fromUtf8("已停止使用共享校准");
        QTest::newRow("dropped") << "" << 2 << QString::fromUtf8("已取消共享校准的检查");
        QTest::newRow("nothing") << "" << 0 << QString::fromUtf8("已改为本机校准");
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
        QVERIFY(app.toastMessage().contains(QString::fromUtf8("已取消共享校准的检查")));
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
        QVERIFY(recording.message().contains(QString::fromUtf8("共享校准已启用")));
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
                 {"verified-proven", "verified"}, {"imported-published", "verifying"},{"imported-unpublished", "verifying"},
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
        QCOMPARE(app.calibration()->shared()->canShare(),
                 fixture == QLatin1String("share") || fixture == QLatin1String("verified-proven"));
        QCOMPARE(app.calibration()->shared()->shareKind(),
                 fixture == QLatin1String("share") ? QStringLiteral("local")
                 : fixture == QLatin1String("verified-proven") ? QStringLiteral("verified")
                                                               : QString());
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
            << QString::fromUtf8("正在使用共享校准，登录检查已通过。")
            << QString::fromUtf8("排本和进本还在核对中");
        QTest::newRow("imported-published")
            << "imported-published" << "published" << false
            << QString::fromUtf8("已找到共享校准，等待本机验证。")
            << QString::fromUtf8("检查通过后才能开始自动记录");
        QTest::newRow("imported-unpublished")
            << "imported-unpublished" << "imported" << false
            << QString::fromUtf8("校准码已导入。登录后申请随机任务并进入副本，通过检查后才能开始记录。")
            << QString::fromUtf8("检查通过后才能开始自动记录");
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
        const auto *shared = app.calibration()->shared();
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

        // §20.5: a shared profile still watched is refused as not proven yet - whether or not
        // its match is still audited - never as "not ours".
        for (const QString &fixture : {QStringLiteral("verified"), QStringLiteral("verified-auditing")}) {
            mr::MockBackend shared;
            shared.setSharedCalibrationFixture(fixture);
            mr::AppController other(&shared, nullptr);
            QTRY_VERIFY(other.calibration()->shared()->inUse());
            QVERIFY(!other.calibration()->shared()->canShare());
            Seams otherSeams;
            otherSeams.attach(*other.calibration()->shared());
            other.calibration()->shared()->share();
            QTRY_COMPARE(other.toastMessage(),
                         mr::SharedCalibrationController::shareRefusalMessage(QStringLiteral("SHARED_NOT_PROVEN")));
            QVERIFY(otherSeams.copied.isEmpty());
            QVERIFY(otherSeams.opened.isEmpty());
        }
    }

    void theMockSharesAProvenSharedCalibrationAsVerified()
    {
        mr::MockBackend backend;
        backend.setSharedCalibrationFixture(QStringLiteral("verified-proven"));
        mr::AppController app(&backend, nullptr);
        auto *shared = app.calibration()->shared();
        QTRY_VERIFY(shared->inUse());
        QTRY_COMPARE(shared->shareKind(), QStringLiteral("verified"));
        QVERIFY(shared->canShare() && shared->canReject());
        QVERIFY(!shared->auditPending());
        // The §20.5 marks ride along on the candidate and reach no sentence.
        verifyPlayerCopy(shared->headline());
        verifyPlayerCopy(shared->detail(), true);
        verifyPlayerCopy(shared->shareHintText());
        verifyPlayerCopy(shared->shareButtonText());

        Seams seams;
        seams.attach(*shared);
        shared->share();
        QTRY_COMPARE(seams.copied.size(), 1);
        QCOMPARE(seams.copied.first(), QLatin1String(kVectorCode));
        QCOMPARE(seams.opened.size(), 1);
        const QUrl opened = seams.opened.first();
        QVERIFY(mr::SharedCalibrationController::isShareIssueUrl(opened));
        QVERIFY2(opened.toString(QUrl::FullyEncoded).contains(QStringLiteral("basis=")),
                 qPrintable(opened.toString(QUrl::FullyEncoded)));
        QTRY_COMPARE(app.toastMessage(),
                     QString::fromUtf8("已打开 GitHub 页面，校准码也已复制到剪贴板。"
                                       "提交后这份共享校准会多一个核实通过的账号，软件不会自动上传。"));
    }

    void theMockRefusesAnUnprovenSharedProfileInTheCollectorsWords()
    {
        // The desktop explains the refusal by its reason alone; the mock's own message is the
        // Collector's sentence all the same, as this file's fixtures promise.
        mr::MockBackend backend;
        backend.setSharedCalibrationFixture(QStringLiteral("verified"));
        bool answered = false;
        bool succeeded = true;
        QString code;
        QString message;
        backend.request(QStringLiteral("GetCalibrationShareCode"))->whenDone(&backend,
            [&](bool ok, const QVariantMap &, const QString &errorCode, const QString &errorMessage) {
                answered = true;
                succeeded = ok;
                code = errorCode;
                message = errorMessage;
            });
        QTRY_VERIFY(answered);
        QVERIFY(!succeeded);
        QCOMPARE(code, QStringLiteral("ERR_SHARE_CODE_UNAVAILABLE"));
        QCOMPARE(message, QString::fromUtf8("这份共享校准还在核对中，等它完整记录过一次随机任务后再告诉其他玩家。"));
    }

    void withdrawingAProvenSharedProfileReArmsCalibration_data()
    {
        QTest::addColumn<QString>("calibration");
        // The fixture's own DONE, and the restart where calibration reads IDLE.
        QTest::newRow("done") << QString();
        QTest::newRow("restarted") << QStringLiteral("idle");
    }

    void withdrawingAProvenSharedProfileReArmsCalibration()
    {
        // 改为本机校准 on the profile that records: with no calibration of this machine's own
        // behind it, the Collector re-arms calibration. The mock must not turn the withdrawn
        // shared profile into a local one that never existed and offer 分享给其他玩家 for it.
        QFETCH(QString, calibration);
        mr::MockBackend backend;
        if (!calibration.isEmpty())
            backend.setCalibrationFixture(calibration);
        backend.setSharedCalibrationFixture(QStringLiteral("verified-proven"));
        mr::AppController app(&backend, nullptr);
        auto *shared = app.calibration()->shared();
        QTRY_COMPARE(shared->shareKind(), QStringLiteral("verified"));

        shared->reject();
        QTRY_COMPARE(shared->view(), QStringLiteral("user_rejected"));
        QTRY_COMPARE(app.calibration()->state(), QStringLiteral("OBSERVING"));
        QCOMPARE(backend.calibrationFixture(), QStringLiteral("observing"));
        QVERIFY(!shared->canShare());
        QCOMPARE(shared->shareKind(), QString());
        QVERIFY(app.captureStatus().value(QStringLiteral("profile_origin")).toString()
                != QLatin1String("LOCAL_CALIBRATION"));
    }

    void theMockNamesTheBasisOfEveryCodeItHandsOut_data()
    {
        QTest::addColumn<QString>("fixture");
        QTest::addColumn<QString>("basis");
        QTest::newRow("local") << "share" << "CALIBRATED";
        QTest::newRow("verified") << "verified-proven" << "VERIFIED";
    }

    void theMockNamesTheBasisOfEveryCodeItHandsOut()
    {
        QFETCH(QString, fixture);
        QFETCH(QString, basis);
        mr::MockBackend backend;
        backend.setSharedCalibrationFixture(fixture);
        bool answered = false;
        QVariantMap payload;
        backend.request(QStringLiteral("GetCalibrationShareCode"))->whenDone(&backend,
            [&](bool ok, const QVariantMap &answer, const QString &, const QString &) {
                answered = ok;
                payload = answer;
            });
        QTRY_VERIFY(answered);
        QCOMPARE(payload.value(QStringLiteral("basis")).toString(), basis);
        QCOMPARE(payload.value(QStringLiteral("code")).toString(), QLatin1String(kVectorCode));
        QVERIFY(payload.value(QStringLiteral("code_in_url")).toBool());
        const QUrl url(payload.value(QStringLiteral("issue_url")).toString(), QUrl::StrictMode);
        QVERIFY(mr::SharedCalibrationController::isShareIssueUrl(url));
        QVERIFY(url.toString(QUrl::FullyEncoded).contains(QStringLiteral("basis=")));
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
        // The mock answers with the Collector's sentence (sharing.code.not_a_code), which
        // names the prefix a code starts with: the one place "MRC1." belongs in player copy.
        QString refusal = finished.first().at(1).toString();
        QVERIFY2(refusal.contains(QStringLiteral("MRC1.")), qPrintable(refusal));
        verifyPlayerCopy(refusal.remove(QStringLiteral("MRC1.")));

        shared->importCode(QLatin1String(kVectorCode));
        QTRY_COMPARE(finished.count(), 2);
        QVERIFY(finished.last().at(0).toBool());
        QTRY_COMPARE(shared->view(), QStringLiteral("verifying"));
        QVERIFY(shared->headline().contains(QString::fromUtf8("校准码已导入")));

        shared->reject();
        QTRY_COMPARE(shared->view(), QStringLiteral("user_rejected"));
        app.calibration()->discard();
        QTRY_VERIFY(shared->view() != QLatin1String("user_rejected"));
    }
};
} // namespace

MR_DESKTOP_TEST_SUITE(SharedCalibration)
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
