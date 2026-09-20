#pragma once

// ---------------------------------------------------------------------------
// Display formatting helpers, exposed to QML as the `Fmt` singleton.
//
// Two rules from the specs are enforced here and nowhere else:
//   * an undefined statistic (null) always renders as an em dash, never 0
//     (docs/statistics-definitions.md 14);
//   * timestamps travel as UTC ISO-8601 and are displayed in local time
//     (docs/architecture.md 4.6).
// ---------------------------------------------------------------------------

#include <QObject>
#include <QQmlEngine>
#include <QString>
#include <QVariant>

namespace mr {

class Formatters : public QObject
{
    Q_OBJECT
    QML_NAMED_ELEMENT(Fmt)
    QML_SINGLETON

public:
    explicit Formatters(QObject *parent = nullptr);

    /// The placeholder shown for every undefined value.
    Q_INVOKABLE static QString dash() { return QStringLiteral("—"); }

    // -- time ---------------------------------------------------------------
    /// "HH:mm:ss" in local time, or an em dash.
    Q_INVOKABLE static QString localTime(const QVariant &utcIso);
    /// "yyyy-MM-dd" in local time, or an em dash.
    Q_INVOKABLE static QString localDate(const QVariant &utcIso);
    /// "yyyy-MM-dd HH:mm:ss" in local time, or an em dash.
    Q_INVOKABLE static QString localDateTime(const QVariant &utcIso);
    /// "yyyy-MM-dd" plus the Chinese weekday, for page headers.
    Q_INVOKABLE static QString dateWithWeekday(const QVariant &utcIso);

    // -- numbers ------------------------------------------------------------
    /// "mm:ss", or "hh:mm:ss" past an hour. Negative and null give an em dash.
    Q_INVOKABLE static QString duration(const QVariant &milliseconds);
    /// A [0,1] ratio as a percentage with one decimal, or an em dash.
    Q_INVOKABLE static QString percent(const QVariant &ratio, int decimals = 1);
    /// An integer, or an em dash when null.
    Q_INVOKABLE static QString count(const QVariant &value);

    // -- game version -------------------------------------------------------
    /// The client version as a player reads it: the date part of a game build
    /// (2026.09.01.0000.0000 -> 2026.09.01). The trailing revision groups are
    /// wire detail no player needs. Null and empty give an em dash; a value
    /// this build cannot split is returned unchanged rather than hidden.
    Q_INVOKABLE static QString gameVersionLabel(const QVariant &build);

    // -- enums --------------------------------------------------------------
    /// Chinese label for a RunResult code.
    Q_INVOKABLE static QString resultLabel(const QString &code);
    /// True for an automatic run that has not ended: UNKNOWN, no end time, not pending review -
    /// the test docs/statistics-definitions.md section 0 uses. Such a run has no result yet.
    Q_INVOKABLE static bool runInProgress(const QVariantMap &run);
    /// The result column of a run: 进行中 while it is in flight, its RunResult label after.
    Q_INVOKABLE static QString runResultLabel(const QVariantMap &run);
    /// Palette token name for a RunResult code, resolved by Theme.token().
    Q_INVOKABLE static QString resultColorToken(const QString &code);
    /// Tag variant name for a RunResult code (ink / accent / outline / neutral).
    Q_INVOKABLE static QString resultTagVariant(const QString &code);
    /// Chinese label for a RunSource code.
    Q_INVOKABLE static QString sourceLabel(const QString &code);
    /// Chinese label for a RunState code.
    Q_INVOKABLE static QString stateLabel(const QString &code);
    /// Chinese label for a DetectionConfidence code (高 / 中 / 低 / 无).
    Q_INVOKABLE static QString confidenceLabel(const QString &code);
    /// confidenceLabel for a run, except that a run still in progress has not been graded yet.
    Q_INVOKABLE static QString runConfidenceLabel(const QVariantMap &run);
    /// Chinese label for a Role code.
    Q_INVOKABLE static QString roleLabel(const QString &code);
    /// Palette token for a role: TANK/HEALER/DPS/UNKNOWN.
    Q_INVOKABLE static QString roleColorToken(const QString &code);

    /// Plain-Chinese explanation of a $defs/CaptureStatus.last_error_code.
    /// Players are never shown the raw ERR_ token as the explanation; an
    /// unknown code falls back to a generic sentence and the caller decides
    /// whether to trail the raw token for a maintainer.
    Q_INVOKABLE static QString captureErrorLabel(const QString &code);
    /// True when \a code is one this build can explain in Chinese.
    Q_INVOKABLE static bool captureErrorKnown(const QString &code);

    /// Chinese name of a $defs/CaptureStatus.last_valid_event_kind
    /// (DUTY_RESULT = 副本结算). Empty for null and for a token this build does
    /// not know: the contract says to read an unknown kind as "some event", so
    /// the capture page then shows the time alone.
    Q_INVOKABLE static QString eventKindLabel(const QString &code);
    /// Plain-Chinese sentence for a $defs/ParserErrorEntry.code. It never
    /// repeats the Collector's own message, which names opcodes in hex; an
    /// unknown code gets a generic sentence.
    Q_INVOKABLE static QString parserErrorLabel(const QString &code);

    /// The six RunResult codes, in the fixed presentation order. Buckets are
    /// never merged (docs/statistics-definitions.md 9).
    Q_INVOKABLE static QStringList resultCodes();

    // -- paths and audit ----------------------------------------------------
    /// The same folding the Collector applies before a path reaches a log file
    /// (docs/privacy-boundary.md section 5): the user's profile directory
    /// becomes %USERPROFILE%. Applied to every path this UI *renders*; the
    /// unfolded value is kept only for QDesktopServices::openUrl.
    Q_INVOKABLE static QString foldUserPath(const QString &path);
    /// Chinese label for a $defs/RunRevisionChange.field name, or the raw
    /// field when it is one this build does not know.
    Q_INVOKABLE static QString fieldLabel(const QString &field);
    /// One old_value / new_value as text. JSON null renders as 空, never as an
    /// empty cell that could be mistaken for "unchanged".
    Q_INVOKABLE static QString revisionValue(const QVariant &value);

private:
    static QDateTime parse(const QVariant &utcIso);
};

} // namespace mr
