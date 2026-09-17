#pragma once

// ---------------------------------------------------------------------------
// Validation and before/after diffing for the manual-correction dialog.
//
// The rules are the ones the prototype spells out (DOC/.../submit()) and the
// error codes are the ones contracts/ipc-v1.schema.json defines, so the dialog
// refuses locally with exactly the code the Collector would return:
//
//   ERR_REASON_REQUIRED    - no reason text
//   ERR_TIME_ORDER         - entered < matched, or ended < matched with no entry
//   ERR_NEGATIVE_DURATION  - ended < entered
//   ERR_NO_CHANGES         - a correction that changes nothing
//
// Two rules have no dedicated code because they are shape rules, not ordering
// rules: a non-CANCELLED_BEFORE_ENTRY result needs an entry time, and a
// COMPLETED result needs an end time. Both report ERR_BAD_REQUEST.
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

    /// Validate one edit form.
    ///
    /// \a form carries: reason, reason_label, date ("yyyy-MM-dd"),
    /// entered_date / ended_date ("yyyy-MM-dd", legacy callers omit these
    /// to use date), matched / entered / ended ("HH:mm:ss" or
    /// "HH:mm:ss.zzz", empty when unset), result,
    /// duty_name, job_name, contributes (bool), note, edit_mode (bool).
    /// \a before is the same shape for the record being corrected; it is only
    /// consulted when edit_mode is true, to detect "nothing changed".
    ///
    /// Returns { ok, code, message }. \a code is empty when ok is true.
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
    Q_INVOKABLE static bool isValidDate(const QString &text);
};

} // namespace mr
