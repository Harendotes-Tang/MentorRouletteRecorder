#pragma once

#include <QByteArray>
#include <QtGlobal>

#include <optional>

namespace mrtest {

/// Gives an environment variable a value for one scope and puts back what was there before -
/// the old value, or no variable at all - when the scope ends, a failed QVERIFY or a QSKIP
/// included. Declare it after the directory its value names and before the objects that read
/// the variable, so they are gone before it is restored.
class ScopedEnvironmentVariable
{
public:
    /// Sets \a name to \a value; std::nullopt unsets it.
    ScopedEnvironmentVariable(const char *name, const std::optional<QByteArray> &value)
        : m_name(name)
    {
        if (qEnvironmentVariableIsSet(name))
            m_previous = qgetenv(name);
        if (value)
            qputenv(name, *value);
        else
            qunsetenv(name);
    }

    ~ScopedEnvironmentVariable()
    {
        if (m_previous)
            qputenv(m_name.constData(), *m_previous);
        else
            qunsetenv(m_name.constData());
    }

    ScopedEnvironmentVariable(const ScopedEnvironmentVariable &) = delete;
    ScopedEnvironmentVariable &operator=(const ScopedEnvironmentVariable &) = delete;

private:
    QByteArray m_name;
    std::optional<QByteArray> m_previous;
};

} // namespace mrtest
