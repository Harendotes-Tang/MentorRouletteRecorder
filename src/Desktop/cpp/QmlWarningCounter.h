#pragma once

// ---------------------------------------------------------------------------
// QML/JS runtime warnings, counted for the screenshot harness (review OJ-7,
// DT4-X5).
//
// A binding that throws, a failed property assignment or an unresolved name
// leaves the page half drawn but still produces a frame, so a screenshot run
// that only looked at the frame would pass. main.cpp counts every such
// diagnostic and exits non-zero after a screenshot run that had one - CTest's
// FAIL_REGULAR_EXPRESSION and the screenshot scripts/package.ps1 -Verify takes
// of the unpacked build alike. Two sources feed it: QQmlEngine::warnings, and
// the message handler for warnings that name a QML or JavaScript location
// (console.warn / console.error, and what other code reports against a QML
// file). A warning the engine reports also reaches the message handler, with
// the same text; it is counted once. Messages that name no QML location - the
// environment notices every offscreen run prints - never count.
// ---------------------------------------------------------------------------

#include <QList>
#include <QMutex>
#include <QQmlError>
#include <QString>
#include <QStringList>
#include <QtGlobal>

class QQmlEngine;

namespace mr {

class QmlWarningCounter
{
public:
    /// True for a warning, critical or fatal message whose text, or whose log
    /// context, starts with a qrc: or file: address - a QML or JavaScript location.
    static bool namesQmlLocation(QtMsgType type, const QMessageLogContext &context,
                                 const QString &message);

    /// Counts what \a engine reports through QQmlEngine::warnings.
    void watch(QQmlEngine *engine);
    /// One message from the message handler; safe from any thread.
    void noteMessage(QtMsgType type, const QMessageLogContext &context, const QString &message);
    /// Diagnostics counted so far.
    int count() const;

private:
    void noteEngineWarnings(const QList<QQmlError> &warnings, bool printed);

    mutable QMutex m_mutex;
    int m_count = 0;
    /// Engine warnings already counted that are still to arrive through the message handler.
    QStringList m_printedByEngine;
};

} // namespace mr
