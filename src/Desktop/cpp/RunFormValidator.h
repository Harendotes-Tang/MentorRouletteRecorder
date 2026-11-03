#pragma once

// ---------------------------------------------------------------------------
// Validation and before/after diffing for the manual-correction dialog.
//
// The rules are the ones the prototype spells out (DOC/.../submit()), with the
// error codes contracts/ipc-v1.schema.json defines:
//
//   ERR_REASON_REQUIRED    - no reason text
//   ERR_TIME_ORDER         - entered < matched, or ended < matched with no entry
//   ERR_NEGATIVE_DURATION  - ended < entered
//   ERR_NO_CHANGES         - a correction that changes nothing
//
// One of them is not the Collector's own: it answers ended < entered with
// ERR_TIME_ORDER (RunMutationValidation, EnteredAfterEnded); an end before the
// match is ERR_TIME_ORDER on both sides (MatchedAfterEnded). A time that is not a
// time, a reason over 500 characters and a note over 1000 (the Collector's
// limits) report ERR_BAD_REQUEST.
//
// The entry and end times may both be left empty whatever the result: the record
// keeps its outcome and its duration stays unknown (docs/manual-correction.md
// §7). The match date and time are required, except in an edit that keeps an
// imported record's missing facts (incomplete_import_context). Every time that
// is supplied is still checked for format and order.
//
// This lives in C++ (not in the QML dialog) so the unit tests can drive every
// branch without a scene graph.
// ---------------------------------------------------------------------------

#include <QObject>
#include <QQmlEngine>
#include <QString>
#include <QVariantList>
#include <QVariantMap>

namespace mr {

class RunFormValidator : public QObject
{
    Q_OBJECT
    QML_NAMED_ELEMENT(RunForm)
    QML_SINGLETON

public:
    explicit RunFormValidator(QObject *parent = nullptr);

    /// The Collector's limits: MutationText.MaxReasonLength and the CreateManualRun /
    /// CorrectRun note (RequestParsers). Both count UTF-16 code units, as QString does.
    static constexpr int kMaxReasonLength = 500;
    static constexpr int kMaxNoteLength = 1000;

    /// Validate one edit form.
    ///
    /// \a form carries: reason, reason_label, date ("yyyy-MM-dd"),
    /// entered_date / ended_date ("yyyy-MM-dd", legacy callers omit these
    /// to use date), matched / entered / ended ("HH:mm:ss" or
    /// "HH:mm:ss.zzz", empty when unset), result,
    /// duty_name, job_name, contributes (bool), note, edit_mode (bool), and
    /// optionally duty_label / job_label: the same names as shown (the
    /// interface language), which diff() displays. Recorded names and the
    /// optional content_id decide whether the duty changed; a different ID can
    /// select another duty with the same name. Job comparison uses job_name.
    /// incomplete_import_context (bool) is a trusted Desktop editing context:
    /// EditRunDialog sets it for every existing imported run it edits (source
    /// IMPORT or import_metadata present), confirming an imported result
    /// included. It is false for creation and for runs the software recorded.
    /// It permits absent endpoints, never malformed or reversed supplied times.
    /// \a before is the same shape for the record being corrected; it is only
    /// consulted when edit_mode is true, to detect "nothing changed".
    ///
    /// Returns { ok, code, message }. \a code is empty when ok is true. \a message is
    /// the sentence alone; the dialog adds the code for a maintainer (AppController::errorText).
    Q_INVOKABLE static QVariantMap validate(const QVariantMap &form,
                                            const QVariantMap &before = {});

    /// The rows of the "修改前后对比" table: { k, a, b } per changed field, in
    /// the prototype's fixed order. Empty when nothing changed.
    Q_INVOKABLE static QVariantList diff(const QVariantMap &before,
                                         const QVariantMap &form);

    /// Combine \a date and \a time into a local QDateTime; invalid when either
    /// part is missing or malformed.
    Q_INVOKABLE static QVariant toDateTime(const QString &date, const QString &time);

    /// True when \a text is a calendar-valid "yyyy-MM-dd" date.
    Q_INVOKABLE static bool isValidDate(const QString &value);
};

} // namespace mr
