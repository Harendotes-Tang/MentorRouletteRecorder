#include "QmlWarningCounter.h"

#include <QMutexLocker>
#include <QObject>
#include <QQmlEngine>

namespace mr {
namespace {

bool isQmlAddress(QStringView text)
{
    return text.startsWith(QLatin1String("qrc:")) || text.startsWith(QLatin1String("file:"));
}

bool isWarningOrWorse(QtMsgType type)
{
    return type == QtWarningMsg || type == QtCriticalMsg || type == QtFatalMsg;
}

} // namespace

bool QmlWarningCounter::namesQmlLocation(QtMsgType type, const QMessageLogContext &context,
                                         const QString &message)
{
    if (!isWarningOrWorse(type))
        return false;
    return isQmlAddress(message)
        || (context.file && isQmlAddress(QString::fromUtf8(context.file)));
}

void QmlWarningCounter::watch(QQmlEngine *engine)
{
    if (!engine)
        return;
    QObject::connect(engine, &QQmlEngine::warnings, engine,
                     [this, engine](const QList<QQmlError> &warnings) {
        noteEngineWarnings(warnings, engine->outputWarningsToStandardError());
    });
}

void QmlWarningCounter::noteEngineWarnings(const QList<QQmlError> &warnings, bool printed)
{
    const QMutexLocker lock(&m_mutex);
    for (const QQmlError &warning : warnings) {
        if (!isWarningOrWorse(warning.messageType()))
            continue;
        ++m_count;
        // The engine prints it through the message handler right after this signal, as
        // QQmlError::toString(); that copy must not count a second time.
        if (printed)
            m_printedByEngine.append(warning.toString());
    }
}

void QmlWarningCounter::noteMessage(QtMsgType type, const QMessageLogContext &context,
                                    const QString &message)
{
    if (!namesQmlLocation(type, context, message))
        return;
    const QMutexLocker lock(&m_mutex);
    if (!m_printedByEngine.removeOne(message))
        ++m_count;
}

int QmlWarningCounter::count() const
{
    const QMutexLocker lock(&m_mutex);
    return m_count;
}

} // namespace mr
