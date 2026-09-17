#pragma once

// ---------------------------------------------------------------------------
// 动效 support for main(): the reduced-motion switch and the hidden
// `--motion-probe <scenario>` evidence run (docs/ui-design.md 动效).
//
// A probe run keeps the QML animations on, but drives them from a manual clock:
// it triggers one scenario after the page has settled and writes frames at
// fixed points of that clock, so the frames show the motion itself and do not
// depend on how long a grab takes.
// ---------------------------------------------------------------------------

#include <QString>
#include <QStringList>

class QQuickWindow;

namespace mr {

class AppController;

namespace motion {

/// True when Windows' "在 Windows 中显示动画" is off
/// (SystemParametersInfo SPI_GETCLIENTAREAANIMATION). Always false elsewhere.
bool systemReducesMotion();

/// theme, trend-week, trend-month, trend-day, history-rows.
QStringList probeScenarios();
bool isProbeScenario(const QString &name);

/// Milliseconds after the trigger at which a probe writes a frame.
QList<int> probeFrameTimes();

/// `<dir>/<name>-t<ms>.png` for a --screenshot target `<dir>/<name>.png`.
QString probeFramePath(const QString &screenshotTarget, int milliseconds);

/// Replaces the animation clock with a manual one. Until runProbe() starts,
/// the clock follows real time so the start-up entrances finish normally.
/// Call once, after the application object exists.
void installProbeClock();

/// Triggers `scenario` on the settled window, writes the frames and returns
/// the process exit code (0 on success).
int runProbe(QQuickWindow *window, AppController *controller, const QString &scenario,
             const QString &screenshotTarget);

} // namespace motion
} // namespace mr
