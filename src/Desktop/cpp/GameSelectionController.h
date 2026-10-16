#pragma once

#include "IBackend.h"
#include "LiveText.h"
#include <QElapsedTimer>
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
    QString message() const { return m_message.text(); }
    QString selectionMessage() const { return m_selectionMessage.text(); }
    QString currentLabel() const { return m_currentLabel; }
    void observe(const QVariantMap &capture);
    Q_INVOKABLE void beginPick();
    Q_INVOKABLE void cancelPick();
    Q_INVOKABLE void select(int index);
    /// Shared by the native timer and deterministic tests. Ignores other applications.
    void observeForegroundProcess(int processId);
    /// How long after the Collector confirmed a choice a snapshot that still
    /// contradicts it is taken to predate it. A status event published while the
    /// switch was under way reaches the pipe within milliseconds of the reply;
    /// two seconds is one ordinary poll.
    static constexpr int kSelectionSettleMs = 2000;
Q_SIGNALS:
    void changed();
    void choicesChanged();
    void selected(const QVariantMap &capture);
private:
    void choose(const QVariantMap &choice);
    /// "游戏 N · HH:mm:ss 启动" for the \a number th listed client, in the language on screen.
    static QString choiceLabel(const QVariantMap &choice, int number);
    /// Rebuild every choice label and the current one after a language switch.
    void relabel();
    /// True for a snapshot that predates the choice the Collector just confirmed:
    /// it still lists the chosen client but does not show it locked.
    bool predatesConfirmedChoice(const QVariantMap &capture) const;
    QPointer<IBackend> m_backend;
    QTimer m_picker;
    QVariantList m_choices, m_pickChoices;
    bool m_required = false, m_busy = false;
    // Recipes, so a language switch re-renders them (LiveText.h).
    LiveText m_message, m_selectionMessage;
    QString m_currentLabel;
    int m_ticks = 0;
    /// The locked process of the last adopted snapshot; a change retires m_message.
    int m_observedPid = 0;
    quint64 m_generation = 0;
    int m_confirmedPid = 0;
    QString m_confirmedToken;
    QElapsedTimer m_confirmedAt;
};
}
