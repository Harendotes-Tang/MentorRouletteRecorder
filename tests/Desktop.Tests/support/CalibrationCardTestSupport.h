#pragma once

// Capture responses and application setup shared by the calibration and protocol profile card suites.
#include "CapturePageScene.h"
#include "PlayerVocabulary.h"
#include "TestCollectorGuard.h"
#include "TestFonts.h"

#include <QJsonArray>
#include <QJsonObject>
#include <QQuickStyle>
#include <QRegularExpression>
#include <QStandardPaths>
#include <QTest>

namespace {

void verifyPlayerCopy(const QStringList &texts)
{
    // The index outcomes, a contradicted candidate and a shared profile's id suffix.
    static const QStringList words = mrtest::sharedCalibrationWords() + QStringList{
        QStringLiteral("CONTRADICTED"), QStringLiteral("INDEX_UNAVAILABLE"), QStringLiteral(".shared")};
    // Any code's short id, not only the mock's: twelve hex digits in a row.
    static const QRegularExpression shortId(QStringLiteral("[0-9A-Fa-f]{12}"));
    for (const QString &text : texts) {
        for (const QString &word : words) {
            QVERIFY2(!text.contains(word, Qt::CaseInsensitive),
                     qPrintable(QStringLiteral("card leaks \"%1\": %2").arg(word, text)));
        }
        QVERIFY2(!shortId.match(text).hasMatch(),
                 qPrintable(QStringLiteral("card leaks a short id: %1").arg(text)));
    }
}

/// verifyPlayerCopy() on every text shown under \a root.
void verifyPlayerCopyOf(QQuickItem *root)
{
    QStringList texts;
    collectVisibleText(root, texts);
    verifyPlayerCopy(texts);
}

/// $defs/SharedCalibrationStatus as a Collector reports it when nothing is going on:
/// present (so the five requests exist), with no candidate and no refusal.
QJsonObject sharedNone()
{
    return {{QStringLiteral("phase"), QStringLiteral("NONE")},
            {QStringLiteral("candidates"), QJsonArray{}},
            {QStringLiteral("last_fetch_status"), QJsonValue::Null},
            {QStringLiteral("last_index_attempts"), QJsonArray{}},
            {QStringLiteral("profile_id"), QJsonValue::Null},
            {QStringLiteral("bound_at_utc"), QJsonValue::Null},
            {QStringLiteral("last_refusal"), QJsonValue::Null},
            {QStringLiteral("rejected_candidates"), 0},
            {QStringLiteral("user_rejected"), false}};
}

/// Answers the two status reads with one hand-written capture status; everything else is empty.
/// DiscardCalibration is recorded, and can be made to fail, so the rollback's refusal path can
/// be read exactly as the player would read it.
class CaptureBackend final : public mr::IBackend
{
public:
    QJsonObject capture;
    /// When set, what the capture status says once a DiscardCalibration has been answered.
    QJsonObject captureAfterDiscard;
    QJsonObject currentRun;
    QList<QJsonObject> discards;
    QString discardErrorCode;
    QString discardErrorMessage;
    int captureStatusReads = 0;
    /// Every request type in the order sent, so a test can prove one was never sent.
    QStringList requestTypes;

    QString backendName() const override { return QStringLiteral("mock"); }
    bool isConnected() const override { return true; }

    mr::BackendReply *request(const QString &type, const QJsonObject &payload = {}) override
    {
        requestTypes.append(type);
        auto *reply = new mr::BackendReply(type, type, this);
        if (type == QLatin1String("GetStatus")) {
            reply->succeed({{QStringLiteral("capture"), capture}});
        } else if (type == QLatin1String("GetCaptureStatus")) {
            ++captureStatusReads;
            reply->succeed(capture);
        } else if (type == QLatin1String("GetCurrentRun")) {
            reply->succeed(currentRun);
        } else if (type == QLatin1String("DiscardCalibration")) {
            discards.append(payload);
            if (!captureAfterDiscard.isEmpty())
                capture = captureAfterDiscard;
            if (discardErrorCode.isEmpty())
                reply->succeed({{QStringLiteral("state"), QStringLiteral("IDLE")}});
            else
                reply->fail(discardErrorCode, discardErrorMessage);
        } else {
            reply->succeed({});
}
        return reply;
    }
};

/// A capture status with a calibration object, so the rollback's three inputs - the profile in
/// force, whether a retired one waits, and whether a duty is in flight - can each be set alone.
/// \a origin empty means no profile is in force, which is what retiring leaves behind.
QJsonObject captureWithRollback(const QString &origin, bool retiredAvailable,
                                const QString &calibrationState = QStringLiteral("OBSERVING"))
{
    QJsonObject calibration{{QStringLiteral("state"), calibrationState},
                            {QStringLiteral("blockers"), QJsonArray{}},
                            {QStringLiteral("events"), QJsonArray{}},
                            {QStringLiteral("retired_local_profile_available"), retiredAvailable},
                            {QStringLiteral("shared"), sharedNone()}};
    QJsonObject capture{{QStringLiteral("ffxiv_running"), true},
                        {QStringLiteral("state"), QStringLiteral("RUNNING")},
                        {QStringLiteral("game_build"), QStringLiteral("2026.09.01.0000.0000")},
                        {QStringLiteral("region"), QStringLiteral("CN")},
                        {QStringLiteral("profile_status"),
                         origin.isEmpty() ? QStringLiteral("UNSUPPORTED_BUILD")
                                          : QStringLiteral("VERIFIED")},
                        {QStringLiteral("calibration"), calibration}};
    if (!origin.isEmpty())
        capture.insert(QStringLiteral("profile_origin"), origin);
    return capture;
}

template <typename Suite>
int runCalibrationCardTests(int argc, char **argv)
{
    // Never run against the user's own Collector, database or serve lease - see
    // TestCollectorGuard.h. Must precede any construction.
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication app(argc, argv);
#ifdef Q_OS_WIN
    // Wrapping is measured in real glyphs; the offscreen platform finds no system fonts.
    if (mrtest::useWindowsFonts({"Fonts/msyh.ttc", "Fonts/simhei.ttf"}).isEmpty()) {
        qCritical("Calibration card tests require a loadable Windows CJK font (msyh.ttc or simhei.ttf).");
        return 6;
    }
#endif
    Suite tests;
    return QTest::qExec(&tests, argc, argv);
}

} // namespace
