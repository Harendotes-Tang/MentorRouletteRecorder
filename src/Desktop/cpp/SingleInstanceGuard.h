#pragma once

// ---------------------------------------------------------------------------
// One Desktop per user.
//
// A second launch - the player double-clicks the shortcut while the first
// window sits in the tray - must not start a second Collector supervisor, a
// second tray icon and a second announcer on the same Collector, each speaking
// every line and raising every dialog (review OH-1). The first instance holds a
// named mutex for as long as it runs; a later one finds it, sets a named event
// the first instance waits on, and exits.
//
// Nothing listens: no socket, no pipe server (NET-005 in
// tools/static-boundary-check/rules.json) and no window message (AUT-001). Both
// kernel objects live in the session's Local\ namespace and are named after the
// user's pipe, so another user or another session never meets them, and
// neither touches any other process.
// ---------------------------------------------------------------------------

#include <QObject>
#include <QString>

class QWinEventNotifier;

namespace mr {

class SingleInstanceGuard : public QObject
{
    Q_OBJECT

public:
    /// \a key names the two kernel objects. An empty key disables the guard:
    /// every instance is then the first. Production passes \ref defaultKey.
    explicit SingleInstanceGuard(const QString &key, QObject *parent = nullptr);
    ~SingleInstanceGuard() override;

    /// "<the user's pipe name>.desktop", or empty when the user's SID cannot
    /// be read.
    static QString defaultKey();

    /// True for the first instance of the key. It keeps that role until it is
    /// destroyed; the kernel drops the mutex with the process, crash included.
    bool isPrimary() const { return m_primary; }

    /// From a later instance: ask the first one to show its window. Also lets
    /// it take the foreground, which Windows otherwise keeps for the process
    /// the user just started. False when the first instance could not be told.
    bool signalPrimary() const;

Q_SIGNALS:
    /// A later instance asked this one to show its window.
    void activationRequested();

private:
    void *m_mutex = nullptr;
    void *m_event = nullptr;
    QWinEventNotifier *m_notifier = nullptr;
    bool m_primary = true;
};

} // namespace mr
