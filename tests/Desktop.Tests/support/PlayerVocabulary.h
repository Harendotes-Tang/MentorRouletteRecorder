// Words no text a player reads may carry, shared by the calibration suites' guards. Each suite
// adds the tokens of its own subject and keeps its own failure message.
#pragma once

#include <QString>
#include <QStringList>

namespace mrtest {

/// What main.cpp's screenshot guard forbids on a player page - the maintainer buttons and
/// tools, opcodes and hex - plus error codes and the VERIFIED status.
inline const QStringList &maintainerWords()
{
    static const QStringList words{
        QString::fromUtf8("开始捕获"), QString::fromUtf8("开始验证"), QString::fromUtf8("停止捕获"),
        QString::fromUtf8("维护者工具"), QString::fromUtf8("对照核对"), QStringLiteral("opcode"),
        QStringLiteral("0x"), QStringLiteral("ERR_"), QStringLiteral("VERIFIED")};
    return words;
}

/// The maintainer words, and a shared calibration's own wire tokens: the fixtures' candidate
/// short id, match sources, index source, phases and provenances, the share-code basis and the
/// candidate marks (plans/shared-calibration.md §20), the profile origins and the request name.
inline const QStringList &sharedCalibrationWords()
{
    static const QStringList words = maintainerWords() + QStringList{
        QStringLiteral("67ef1bb97e65"), QStringLiteral("sha"), QStringLiteral("REPLY_STATE"),
        QStringLiteral("QUEUE_REQUEST"), QStringLiteral("GITHUB_RAW"), QStringLiteral("AWAITING_CONSENT"),
        QStringLiteral("MRC1"), QStringLiteral("PUBLISHED"), QStringLiteral("IMPORTED"),
        QStringLiteral("audit_pending"), QStringLiteral("PROVEN"), QStringLiteral("CALIBRATED"),
        QStringLiteral("basis"), QStringLiteral("shape_prior"), QStringLiteral("disputed"),
        QStringLiteral("preferred"), QStringLiteral("CONTENT_FINDER_POP"), QStringLiteral("ZONE_INITIALIZATION"),
        QStringLiteral("ZONE_TERRITORY"), QStringLiteral("PLAYER_JOB"), QStringLiteral("IN_USE"),
        QStringLiteral("SHARED_CALIBRATION"), QStringLiteral("LOCAL_CALIBRATION"),
        QStringLiteral("GetCalibrationShareCode"), QStringLiteral("SHARED_NOT_PROVEN")};
    return words;
}

} // namespace mrtest
