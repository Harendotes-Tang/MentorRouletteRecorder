#pragma once

#include "IBackend.h"
#include <QPointer>
#include <QQmlEngine>
#include <QTimer>

namespace mr {
/// Explicit, time-limited foreground-window selection; never installs a hook.
class GameSelectionController : public QObject
{
    Q_OBJECT
    QML_ANONYMOUS
    Q_PROPERTY(QVariantList choices READ choices NOTIFY choicesChanged)
    Q_PROPERTY(bool selectionRequired READ required NOTIFY changed)
    Q_PROPERTY(bool picking READ picking NOTIFY changed)
    Q_PROPERTY(bool busy READ busy NOTIFY changed)
    Q_PROPERTY(QString message READ message NOTIFY changed)
    Q_PROPERTY(QString selectionMessage READ selectionMessage NOTIFY changed)
    Q_PROPERTY(QString currentLabel READ currentLabel NOTIFY changed)
public:
    explicit GameSelectionController(IBackend *backend, QObject *parent = nullptr);
    QVariantList choices() const { return m_choices; }
    bool required() const { return m_required; }
    bool picking() const { return m_picker.isActive(); }
    bool busy() const { return m_busy; }
    QString message() const { return m_message; }
    QString selectionMessage() const { return m_selectionMessage; }
    QString currentLabel() const { return m_currentLabel; }
    void observe(const QVariantMap &capture);
    Q_INVOKABLE void beginPick();
    Q_INVOKABLE void cancelPick();
    Q_INVOKABLE void select(int index);
    /// Shared by the native timer and deterministic tests. Ignores other applications.
    void observeForegroundProcess(int processId);
Q_SIGNALS:
    void changed();
    void choicesChanged();
    void selected(const QVariantMap &capture);
private:
    void choose(const QVariantMap &choice);
    QPointer<IBackend> m_backend;
    QTimer m_picker;
    QVariantList m_choices, m_pickChoices;
    bool m_required = false, m_busy = false;
    QString m_message, m_selectionMessage, m_currentLabel;
    int m_ticks = 0;
    quint64 m_generation = 0;
};
}
