#include "PopupMotion.h"

#include <QPointer>
#include <QThread>
#include <QtQuickTemplates2/private/qquickpopup_p.h>
#include <QtQuickTemplates2/private/qquickpopup_p_p.h>

namespace mr {

void PopupMotion::finish(QObject *popup)
{
    // No cross-thread access to Qt Quick objects, including their private state.
    if (!popup || popup->thread() != QThread::currentThread()
        || thread() != QThread::currentThread())
        return;
    QPointer<QQuickPopup> guardedPopup = qobject_cast<QQuickPopup *>(popup);
    if (!guardedPopup)
        return;
    auto *state = QQuickPopupPrivate::get(guardedPopup);
    const auto target = state->transitionState;
    if (target == QQuickPopupPrivate::NoTransition || !state->transitionManager.isRunning())
        return;

    // This component uses finite NumberAnimation transitions. Unlike a smoothed
    // continuing job, their cancellation does not finish the new transition
    // reentrantly. Qt's manager releases the current animation before finalizing.
    state->transitionManager.cancel();
    const auto sameTarget = [&guardedPopup, target]() {
        if (!guardedPopup)
            return false;
        const auto *current = QQuickPopupPrivate::get(guardedPopup);
        return current->transitionState == target && !current->transitionManager.isRunning();
    };
    if (!sameTarget())
        return;
    if (target == QQuickPopupPrivate::EnterTransition) {
        guardedPopup->setScale(1.0);
        if (!sameTarget())
            return;
        guardedPopup->setOpacity(1.0);
        if (!sameTarget())
            return;
        QQuickPopupPrivate::get(guardedPopup)->finalizeEnterTransition();
    } else {
        // Qt owns dimmer removal, saved focus restoration, visibility and its
        // original opacity/scale. No access follows the user-visible closed signal.
        QQuickPopupPrivate::get(guardedPopup)->finalizeExitTransition();
    }
}
}
