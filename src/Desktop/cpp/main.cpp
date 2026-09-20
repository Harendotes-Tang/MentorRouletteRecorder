// ---------------------------------------------------------------------------
// MentorRecorder.Desktop entry point.
//
// Besides starting the UI this binary supports a headless screenshot mode,
// used to review the layout without a human at the keyboard:
//
//   MentorRecorder.Desktop --screenshot out.png --page 1 --theme dark
//                          [--mock-npcap-missing] [--mock-live entered]
//                          [--mock-first-run] [--mock-open-detail]
//                          [--mock-open-edit] [--show-disclosure]
//                          [--mock-open-create] [--mock-wizard-step 1|2|3]
//                          [--mock-detail-tab refl] [--mock-open-reflection]
//                          [--mock-ui-style classic] [--settings-tab tts]
//                          [--mock-calibration done] [--mock-shared consent]
//                          [--mock-update-available]
//                          [--mock-recording-state waiting-verified]
//                          [--export-target DIR] [--open-detail] [--open-edit]
//                          [--screenshot-size WxH] [--mock-speech azure]
//                          [--mock-open-speech-confirm] [--mock-speech-preview]
//
//   MentorRecorder.Desktop --speech-selftest file.wav   (hidden; exit 0 = played)
//   MentorRecorder.Desktop --screenshot out.png --motion-probe theme
//                          (hidden; writes out-t0.png ... out-t600.png with the
//                          animations on, see Motion.h)
//
// --show-disclosure, --export-target, --open-detail, --open-edit,
// --settings-tab and --screenshot-size work with either backend. --export-target
// replaces the interactive file chooser with a fixed directory, so an unattended
// run never blocks on a modal dialog.
//
// It never opens a listening socket and never performs a network request.
// ---------------------------------------------------------------------------

#include "AppController.h"
#include "CollectorProcess.h"
#include <memory>
#include "AppSettings.h"
#include "Formatters.h"
#include "IpcBackend.h"
#include "JobCatalog.h"
#include "MockBackend.h"
#include "Motion.h"
#include "RoleCatalog.h"
#include "RunFormValidator.h"
#include "SpeechPlayer.h"
#include "TrayController.h"
#include "TtsService.h"

#include <QApplication>
#include <QCommandLineParser>
#include <QDir>
#include <QFileInfo>
#include <QElapsedTimer>
#include <QEventLoop>
#include <QFont>
#include <QFontDatabase>
#include <QKeyEvent>
#include <QQmlApplicationEngine>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQuickItem>
#include <QQuickStyle>
#include <QStandardPaths>
#include <QQuickWindow>
#include <QScopeGuard>
#ifdef Q_OS_WIN
#include <dwmapi.h>
#include <windows.h>
#endif
#include <QTimer>

#include <cstdlib>
#include <cstdio>

namespace {

/// Send Qt/QML diagnostics to stderr even for the Windows GUI subsystem.
/// This keeps headless screenshot and CI failures observable when stdout and
/// stderr are redirected by a build script.
void stderrMessageHandler(QtMsgType type, const QMessageLogContext &, const QString &message)
{
    const char *const level = [type] {
        switch (type) {
        case QtDebugMsg: return "debug";
        case QtInfoMsg: return "info";
        case QtWarningMsg: return "warning";
        case QtCriticalMsg: return "critical";
        case QtFatalMsg: return "fatal";
        }
        return "info";
    }();

    const QByteArray encoded = message.toUtf8();
    std::fprintf(stderr, "[%s] %s\n", level, encoded.constData());
    std::fflush(stderr);
}

/// True when \a name appears in the raw argument vector.
bool hasRawArgument(int argc, char **argv, const char *name)
{
    for (int i = 1; i < argc; ++i) {
        if (qstrcmp(argv[i], name) == 0)
            return true;
    }
    return false;
}

/// Application font stack.
///
/// Normal runs ask for the prototype's stack; Qt falls through the list until
/// a family exists on the machine. Under the offscreen platform Qt discovers no
/// system fonts at all, so one installed CJK file is registered as a fallback -
/// it stays a Windows system asset and is never redistributed.
void installApplicationFont(bool screenshotMode)
{
    const QStringList families0{
        QStringLiteral("Noto Sans SC"),
        QStringLiteral("Microsoft YaHei UI"),
        QStringLiteral("Microsoft YaHei"),
        QStringLiteral("Segoe UI"),
    };
    QStringList families = families0;

    // Cinzel (SIL OFL 1.1, resources/fonts/OFL.txt) carries the eorzea style's
    // Latin headings and figures; IBM Plex Mono (SIL OFL 1.1,
    // resources/fonts/IBMPlexMono-OFL.txt) the classic style's figures. Both ship
    // inside the binary, so neither depends on what the machine happens to have.
    for (const char *font : {":/resources/fonts/Cinzel-Regular.ttf",
                             ":/resources/fonts/Cinzel-Bold.ttf",
                             ":/resources/fonts/IBMPlexMono-Medium.ttf",
                             ":/resources/fonts/IBMPlexMono-SemiBold.ttf"}) {
        if (QFontDatabase::addApplicationFont(QString::fromLatin1(font)) < 0)
            qWarning("Cannot register bundled font %s", font);
    }

    // Chinese headings ask for "Noto Serif SC"; on a machine without it Qt
    // falls through to another serif CJK face instead of silently dropping
    // back to the sans body font.
    QFont::insertSubstitutions(QStringLiteral("Noto Serif SC"),
                               {QStringLiteral("Noto Serif CJK SC"),
                                QStringLiteral("Songti SC"),
                                QStringLiteral("SimSun"),
                                QStringLiteral("NSimSun")});

#ifdef Q_OS_WIN
    if (screenshotMode) {
        bool loadedCjkFont = false;
        const QString windowsDir = qEnvironmentVariable("WINDIR", QStringLiteral("C:/Windows"));
        for (const char *file : {"Fonts/msyh.ttc", "Fonts/simhei.ttf"}) {
            const QString path = QDir(windowsDir).filePath(QString::fromLatin1(file));
            const int id = QFontDatabase::addApplicationFont(path);
            const QStringList registered = QFontDatabase::applicationFontFamilies(id);
            loadedCjkFont = loadedCjkFont || (id >= 0 && !registered.isEmpty());
            for (const QString &family : registered) {
                if (!families.contains(family))
                    families.append(family);
            }
        }
        if (!loadedCjkFont) {
            qCritical("Cannot load a system CJK font for screenshot verification.");
            std::exit(6);
        }
        // Theme.monoFamily picks Consolas in the eorzea style; without it the
        // offscreen "monospace" fallback lands on Cinzel and paths render in
        // capitals. Optional: a machine without the file keeps the fallback.
        QFontDatabase::addApplicationFont(
            QDir(windowsDir).filePath(QStringLiteral("Fonts/consola.ttf")));
    }
#else
    Q_UNUSED(screenshotMode)
#endif

    // Put a family that really exists first: QML reads
    // `Qt.application.font.family` (a single name) for the classic style's
    // headings, and a name the font database cannot resolve would fall back to
    // a serif face instead of the body font.
    QStringList ordered;
    for (const QString &family : families) {
        if (QFontDatabase::hasFamily(family)) {
            ordered.append(family);
            break;
        }
    }
    for (const QString &family : families) {
        if (!ordered.contains(family))
            ordered.append(family);
    }

    QFont font;
    font.setFamilies(ordered);
    QApplication::setFont(font);
}

/// Probe whether Qt Graphs can actually be instantiated in this environment.
/// The dashboard falls back to a hand-drawn bar row when it cannot.
bool probeGraphs(QQmlEngine *engine)
{
    QQmlComponent component(engine);
    component.setData(QByteArrayLiteral(
                          "import QtQuick\nimport QtGraphs\nGraphsView { }\n"),
                      QUrl(QStringLiteral("qrc:/mr/graphs-probe.qml")));
    if (component.isError()) {
        qWarning("Qt Graphs unavailable, falling back to the plain bar chart: %s",
                 qPrintable(component.errorString()));
        return false;
    }
    return true;
}

/// Asks Windows to round the corners of a frameless window.
///
/// Windows 11 rounds the window itself, drop shadow and snap preview included; a rounded
/// Rectangle drawn inside a transparent surface would round the paint and nothing else.
/// Older Windows refuses the attribute and keeps square corners.
void roundWindowCorners(QQuickWindow *window)
{
#ifdef Q_OS_WIN
    if (!window)
        return;
    window->create();
    const auto handle = reinterpret_cast<HWND>(window->winId());
    if (!handle)
        return;
    // DWMWA_WINDOW_CORNER_PREFERENCE / DWMWCP_ROUND, by value so the build does not need a
    // Windows 11 SDK to compile.
    const DWORD preference = 2;
    DwmSetWindowAttribute(handle, 33, &preference, sizeof(preference));
#else
    Q_UNUSED(window);
#endif
}

bool sendKey(QQuickWindow *window, int key,
             Qt::KeyboardModifiers modifiers = Qt::NoModifier)
{
    QKeyEvent press(QEvent::KeyPress, key, modifiers);
    QKeyEvent release(QEvent::KeyRelease, key, modifiers);
    const bool accepted = QCoreApplication::sendEvent(window, &press);
    QCoreApplication::sendEvent(window, &release);
    QCoreApplication::processEvents();
    return accepted;
}

QQuickItem *findVisualItem(QQuickItem *root, const QString &objectName)
{
    if (!root)
        return nullptr;
    if (root->objectName() == objectName)
        return root;
    for (QQuickItem *child : root->childItems()) {
        if (QQuickItem *match = findVisualItem(child, objectName))
            return match;
    }
    return nullptr;
}

/// True when some *visible* item in the scene carries \a needle in its text.
/// The screenshot tests use it to assert what the page actually says, rather
/// than only that a frame could be grabbed at all: a fixture rendering "已保存"
/// on a FAILED session would otherwise pass.
bool sceneShowsText(QQuickItem *root, const QString &needle)
{
    if (!root)
        return false;
    if (root->isVisible()) {
        const QVariant text = root->property("text");
        if (text.isValid() && text.toString().contains(needle))
            return true;
    }
    for (QQuickItem *child : root->childItems()) {
        if (sceneShowsText(child, needle))
            return true;
    }
    return false;
}

// Compiled-QML acceptance: exercises the real visibility handlers and button wiring.
// Notification delivery by Windows is deliberately outside this offscreen check.
bool verifyRecordingAlertFlow(QQuickWindow *window, mr::MockBackend *backend, mr::AppController *app)
{
    auto *alert = window->findChild<QObject *>(QStringLiteral("automaticRecordingAlert"));
    if (!alert) { qCritical("recording alert object missing"); return false; }
    const auto waitFor = [](const std::function<bool()> &condition) {
        QElapsedTimer timer; timer.start();
        while (!condition() && timer.elapsed() < 2500) {
            QEventLoop loop;
            QTimer::singleShot(20, &loop, &QEventLoop::quit);
            loop.exec();
        }
        return condition();
    };
    const auto isOpen = [alert] { return alert->property("visible").toBool(); };
    const auto observe = [&](const QString &fixture, const QString &state) {
        backend->setRecordingFixture(fixture);
        app->recording()->refresh();
        return waitFor([&] { return app->recording()->state() == state; });
    };
    if (!observe(QStringLiteral("waiting"), QStringLiteral("waiting")) || isOpen()) return false;
    window->hide();
    if (!observe(QStringLiteral("blocked"), QStringLiteral("blocked"))
        || window->isVisible() || isOpen() || !app->recording()->pendingAlert()) {
        qCritical("hidden blocker restored window or opened popup"); return false;
    }
    window->showNormal();
    if (!waitFor(isOpen)) { qCritical("reopened window did not show deferred alert"); return false; }
    auto *ack = findVisualItem(window->contentItem(), QStringLiteral("automaticRecordingAcknowledge"));
    if (!ack) return false;
    ack->forceActiveFocus(Qt::TabFocusReason);
    sendKey(window, Qt::Key_Space);
    if (!waitFor([&] { return !isOpen() && !app->recording()->pendingAlert(); })) {
        qCritical("acknowledgement button did not close alert"); return false;
    }
    auto *banner = findVisualItem(window->contentItem(), QStringLiteral("automaticRecordingBanner"));
    if (!banner || !banner->isVisible()) { qCritical("acknowledgement removed persistent banner"); return false; }
    app->recording()->refresh();
    QEventLoop poll; QTimer::singleShot(250, &poll, &QEventLoop::quit); poll.exec();
    if (isOpen() || !app->recording()->blocked()) { qCritical("same fault repeated popup"); return false; }
    if (!observe(QStringLiteral("listening"), QStringLiteral("listening"))) return false;
    window->showMinimized();
    if (!observe(QStringLiteral("blocked"), QStringLiteral("blocked")) || isOpen()
        || window->visibility() != QWindow::Minimized) {
        qCritical("minimized blocker restored window or opened popup"); return false;
    }
    window->showNormal();
    if (!waitFor(isOpen)) return false;
    // Recovery while hidden must close even an existing modal and drop deferred state.
    window->hide();
    if (!observe(QStringLiteral("listening"), QStringLiteral("listening"))) return false;
    window->showNormal();
    if (!waitFor([&] { return !isOpen(); }) || app->recording()->pendingAlert()) return false;
    window->hide();
    if (!observe(QStringLiteral("blocked"), QStringLiteral("blocked")) || isOpen()) return false;
    if (!observe(QStringLiteral("listening"), QStringLiteral("listening"))) return false;
    window->showNormal();
    QCoreApplication::processEvents();
    if (isOpen() || app->recording()->pendingAlert()) { qCritical("recovered deferred fault showed stale popup"); return false; }
    for (int page = 0; page < 6; ++page) {
        app->navigate(page);
        QCoreApplication::processEvents();
        if (page == 5) {
            QEventLoop layout; QTimer::singleShot(80, &layout, &QEventLoop::quit); layout.exec();
            // 界面风格 lives in the 通用 tab's 外观 panel (docs/ui-design.md §4.5).
            auto *card = findVisualItem(window->contentItem(), QStringLiteral("appearanceSettingsCard"));
            auto *style = findVisualItem(window->contentItem(), QStringLiteral("uiStyleSettingControl"));
            if (!card || !style) { qCritical("settings appearance panel or style control missing"); return false; }
            const QPointF relative = style->mapToItem(card, QPointF(0, 0));
            if (relative.x() < 0 || relative.x() + style->width() > card->width() - 18) {
                qCritical("settings style control overflows its card: x=%g width=%g card=%g",
                          relative.x(), style->width(), card->width()); return false;
            }
        }
        for (const auto &forbidden : {QString::fromUtf8("开始捕获"), QString::fromUtf8("开始验证"),
                 QString::fromUtf8("停止捕获"), QString::fromUtf8("维护者工具"), QString::fromUtf8("对照核对")}) {
            if (sceneShowsText(window->contentItem(), forbidden)) {
                qCritical("normal page %d exposes maintenance text: %s", page, qPrintable(forbidden)); return false;
            }
        }
    }
    app->navigate(0);
    std::fputs("recording alert flow verified: hidden, minimized, deferred reopen, keyboard acknowledgement, persistent banner, no repeated popup, recovery, normal navigation controls\n", stdout);
    return true;
}

/// Exercise the actual Qt Quick focus chain and activation path used by the
/// validation marker controls. This runs only behind a mock screenshot flag.
bool verifyValidationMarkerKeyboard(QQuickWindow *window, const mr::MockBackend *backend)
{
    const QStringList codes{QStringLiteral("queued"), QStringLiteral("pop"),
                            QStringLiteral("entered"), QStringLiteral("victory"),
                            QStringLiteral("left")};
    QList<QQuickItem *> buttons;
    for (const QString &code : codes) {
        auto *button = findVisualItem(window->contentItem(),
                                      QStringLiteral("validationMarker_") + code);
        if (!button) {
            qCritical("cannot find marker focus item %s", qPrintable(code));
            return false;
        }
        buttons.append(button);
    }

    buttons.first()->forceActiveFocus(Qt::TabFocusReason);
    QCoreApplication::processEvents();
    if (window->activeFocusItem() != buttons.first()) {
        qCritical("queued marker did not accept keyboard focus");
        return false;
    }
    for (qsizetype index = 1; index < buttons.size(); ++index) {
        sendKey(window, Qt::Key_Tab);
        if (window->activeFocusItem() != buttons.at(index)) {
            qCritical("Tab focus mismatch: expected marker %s",
                      qPrintable(codes.at(index)));
            return false;
        }
    }

    sendKey(window, Qt::Key_Backtab, Qt::ShiftModifier);
    if (window->activeFocusItem() != buttons.at(3)) {
        qCritical("Shift+Tab focus mismatch: expected marker victory");
        return false;
    }
    sendKey(window, Qt::Key_Tab);
    if (window->activeFocusItem() != buttons.last()) {
        qCritical("Tab focus mismatch after Shift+Tab: expected marker left");
        return false;
    }

    sendKey(window, Qt::Key_Space);
    QCoreApplication::processEvents();
    if (!backend || backend->lastValidationMarker() != QLatin1String("left")) {
        qCritical("Space marker payload mismatch: expected left, got %s",
                  backend ? qPrintable(backend->lastValidationMarker()) : "<no mock>");
        return false;
    }
    if (window->activeFocusItem() != buttons.last()) {
        qCritical("Space activation moved focus away from marker left");
        return false;
    }
    if (!buttons.last()->property("keyboardFocusVisible").toBool()) {
        qCritical("focused marker left has no visible keyboard focus indicator");
        return false;
    }

    window->update();
    QCoreApplication::processEvents();
    std::fputs("verified validation marker keyboard focus: "
               "queued -> pop -> entered -> victory -> left; "
               "Shift+Tab -> victory; Tab/Space -> left\n",
               stdout);
    return true;
}

} // namespace

int main(int argc, char *argv[])
{
    qInstallMessageHandler(stderrMessageHandler);

    // Online speech only plays WAV files, which the Windows Media Foundation
    // backend does without FFmpeg; the package ships that backend alone
    // (scripts/package.ps1). An explicit setting still wins.
    if (qEnvironmentVariableIsEmpty("QT_MEDIA_BACKEND"))
        qputenv("QT_MEDIA_BACKEND", "windows");

    const bool screenshotMode = hasRawArgument(argc, argv, "--screenshot");
    if (hasRawArgument(argc, argv, "--motion-probe")) {
        // The probe steps the animation clock itself; the basic render loop is
        // the one that leaves the clock alone (the threaded loop installs its own).
        if (qEnvironmentVariableIsEmpty("QSG_RENDER_LOOP"))
            qputenv("QSG_RENDER_LOOP", "basic");
        // Offscreen windows default to the software renderer, which draws no
        // shader effects - the theme reveal's mask among them. The probe renders
        // through the GPU like an interactive window does.
        if (qEnvironmentVariableIsEmpty("QT_QUICK_BACKEND"))
            qputenv("QT_QUICK_BACKEND", "rhi");
    }

    // A screenshot run must not pop a window on the user's desktop.
    if (screenshotMode && qEnvironmentVariableIsEmpty("QT_QPA_PLATFORM"))
        qputenv("QT_QPA_PLATFORM", "offscreen");

    // Screenshot fixtures must never read or write the interactive user's
    // desktop.ini; test mode resolves GenericConfigLocation below
    // AppData/Local/qttest.
    if (screenshotMode)
        QStandardPaths::setTestModeEnabled(true);

    // Custom styling only works on a style that does not impose its own look.
    qputenv("QT_QUICK_CONTROLS_STYLE", "Basic");

    // Persisted UI scale has to reach Qt before the application exists.
    const int scale = mr::readPersistedUiScale();
    if (scale != 100 && qEnvironmentVariableIsEmpty("QT_SCALE_FACTOR"))
        qputenv("QT_SCALE_FACTOR", QByteArray::number(scale / 100.0));

    QGuiApplication::setHighDpiScaleFactorRoundingPolicy(
        Qt::HighDpiScaleFactorRoundingPolicy::PassThrough);

    // QApplication (not QGuiApplication) because the notification-area icon is
    // QSystemTrayIcon, which lives in Qt Widgets.
    QApplication app(argc, argv);
    QApplication::setApplicationName(QStringLiteral("MentorRecorder.Desktop"));
    QApplication::setOrganizationName(QStringLiteral("MentorRecorder"));
    QApplication::setApplicationVersion(QStringLiteral(MR_APP_VERSION));
    QApplication::setQuitOnLastWindowClosed(false);
    QQuickStyle::setStyle(QStringLiteral("Basic"));

    installApplicationFont(screenshotMode);

    QCommandLineParser parser;
    parser.setApplicationDescription(
        QStringLiteral("FF14 mentor roulette recorder - desktop shell"));
    parser.addHelpOption();
    parser.addVersionOption();

    QCommandLineOption backendOption(
        QStringLiteral("backend"),
        QStringLiteral("Backend to use: mock or ipc. Default: ipc for interactive runs, mock for --screenshot."),
        QStringLiteral("mock|ipc"));
    QCommandLineOption screenshotOption(
        QStringLiteral("screenshot"),
        QStringLiteral("Render one frame offscreen into <file> and exit."),
        QStringLiteral("file"));
    QCommandLineOption pageOption(
        QStringLiteral("page"), QStringLiteral("Page 1-7 to show (screenshot mode)."),
        QStringLiteral("n"), QStringLiteral("1"));
    QCommandLineOption themeOption(
        QStringLiteral("theme"), QStringLiteral("dark, light or system."),
        QStringLiteral("mode"), QStringLiteral("dark"));
    QCommandLineOption npcapOption(
        QStringLiteral("mock-npcap-missing"),
        QStringLiteral("Pretend the Npcap driver is not installed."));
    QCommandLineOption candidatesOption(
        QStringLiteral("mock-candidates"),
        QStringLiteral("Use deterministic candidate review fixtures; research payload remains disabled."));
    QCommandLineOption calibrationOption(
        QStringLiteral("mock-calibration"),
        QStringLiteral("Synthetic 本机校准 state: observing, ready, blocked, done or idle "
                       "(idle = calibrated in an earlier run, local profile recording, no "
                       "calibration card). Player-facing; it never opens maintainer tools."),
        QStringLiteral("state"));
    QCommandLineOption sharedOption(
        QStringLiteral("mock-shared"),
        QStringLiteral("Synthetic 共享校准 state: fetching, verifying, consent, verified, "
                       "verified-auditing, imported-published, imported-unpublished, rejected, "
                       "unavailable, user-rejected, none-for-build or share. Arms a matching "
                       "--mock-calibration state when none is given; nothing is downloaded."),
        QStringLiteral("state"));
    QCommandLineOption updateOption(
        QStringLiteral("mock-update-available"),
        QStringLiteral("Pretend the Collector's update check found a newer release. "
                       "The version and the page it names are synthetic; nothing is fetched."));
    QCommandLineOption liveOption(
        QStringLiteral("mock-live"),
        QStringLiteral("Live run state: none, matched or entered."),
        QStringLiteral("state"), QStringLiteral("entered"));
    QCommandLineOption alertFlowOption(QStringLiteral("mock-recording-alert-flow"),
        QStringLiteral("Verify hidden/minimized/deferred recording alerts and normal controls in compiled QML."));
    QCommandLineOption recordingOption(QStringLiteral("mock-recording-state"),
        QStringLiteral("Synthetic automatic recording UI state: waiting, waiting-verified, "
                       "waiting-calibrating, checking, listening or blocked. The three waiting "
                       "states are the game closed: with no remembered install directory "
                       "(waiting), and with the installed version read off disk and a profile "
                       "for it that is usable (waiting-verified) or not yet (waiting-calibrating)."),
        QStringLiteral("state"));
    QCommandLineOption maintainerOption(QStringLiteral("maintainer-tools"),
        QStringLiteral("Enable manual capture, validation and candidate maintenance tools."));
    QCommandLineOption validationOption(
        QStringLiteral("mock-validation-state"),
        QStringLiteral("Validation screenshot state: waiting, recording, stopping, completed or failed."),
        QStringLiteral("state"));
    QCommandLineOption formalRunningOption(
        QStringLiteral("mock-formal-running"),
        QStringLiteral("Keep a formal capture RUNNING while validation evidence is historical."));
    QCommandLineOption validationKeyboardOption(
        QStringLiteral("mock-validation-keyboard"),
        QStringLiteral("Verify marker Tab/Shift+Tab/Space behavior before a mock screenshot."));
    QCommandLineOption firstRunOption(
        QStringLiteral("mock-first-run"),
        QStringLiteral("Show the first-run baseline dialog."));
    QCommandLineOption openDetailOption(
        QStringLiteral("mock-open-detail"),
        QStringLiteral("Open the history detail panel on the first row."));
    QCommandLineOption openEditOption(
        QStringLiteral("mock-open-edit"),
        QStringLiteral("Open the manual-correction dialog on the first row."));
    QCommandLineOption delayOption(
        QStringLiteral("screenshot-delay"),
        QStringLiteral("Milliseconds to settle before grabbing (screenshot mode)."),
        QStringLiteral("ms"), QStringLiteral("900"));
    QCommandLineOption openDetailOption2(
        QStringLiteral("open-detail"),
        QStringLiteral("Open the history detail panel on the first row. Works with either "
                       "backend; --mock-open-detail is the mock-only spelling."));
    QCommandLineOption openEditOption2(
        QStringLiteral("open-edit"),
        QStringLiteral("Open the manual-correction dialog on the first row. Works with "
                       "either backend."));
    QCommandLineOption detailTabOption(
        QStringLiteral("mock-detail-tab"),
        QStringLiteral("Detail panel tab to open on the first row: info, events, revs or refl. "
                       "Works with either backend."),
        QStringLiteral("tab"), QString());
    QCommandLineOption openReflectionOption(
        QStringLiteral("mock-open-reflection"),
        QStringLiteral("Open the reflection (导随笔记) dialog on the first history row."));
    QCommandLineOption uiStyleOption(
        QStringLiteral("mock-ui-style"),
        QStringLiteral("Pin the UI style for this run without touching desktop.ini: classic, "
                       "eorzea or harendotes. Works with either backend."),
        QStringLiteral("style"), QString());
    QCommandLineOption settingsTabOption(
        QStringLiteral("settings-tab"),
        QStringLiteral("Settings page tab to open: general, tts, goal, data or about. "
                       "Works with either backend."),
        QStringLiteral("tab"), QString());
    QCommandLineOption sizeOption(
        QStringLiteral("screenshot-size"),
        QStringLiteral("Window size as WxH for the screenshot run. Default 1280x800."),
        QStringLiteral("WxH"), QStringLiteral("1280x800"));
    QCommandLineOption disclosureOption(
        QStringLiteral("show-disclosure"),
        QStringLiteral("Open the first-run disclosure page regardless of the stored "
                       "acknowledgement. Works with either backend."));
    QCommandLineOption verifyTextOption(
        QStringLiteral("verify-text"),
        QStringLiteral("Before grabbing, assert that some visible item shows <text>. "
                       "Repeatable. Exits 8 when one is missing."),
        QStringLiteral("text"));
    QCommandLineOption midstreamOption(
        QStringLiteral("mock-midstream"),
        QStringLiteral("Simulate a capture started after the client logged in: "
                       "midstream_suspected, zero decoded messages, rising decode errors."));
    QCommandLineOption speechOption(
        QStringLiteral("mock-speech"),
        QStringLiteral("Synthetic online speech state: azure, openai, unconfigured or fail. "
                       "Selects the matching online voice for this run only; nothing is sent."),
        QStringLiteral("state"));
    QCommandLineOption speechConfirmOption(
        QStringLiteral("mock-open-speech-confirm"),
        QStringLiteral("Open the settings page's online-speech confirmation for this run."));
    QCommandLineOption speechPreviewOption(
        QStringLiteral("mock-speech-preview"),
        QStringLiteral("Play the settings-page preview sentence shortly after start (online route when an online "
                       "voice is selected)."));
    QCommandLineOption speechSelfTestOption(
        QStringLiteral("speech-selftest"),
        QStringLiteral("Play <wav> through the online-speech player and exit (0 = played)."),
        QStringLiteral("wav"));
    speechSelfTestOption.setFlags(QCommandLineOption::HiddenFromHelp);
    QCommandLineOption motionProbeOption(
        QStringLiteral("motion-probe"),
        QStringLiteral("With --screenshot: keep the animations on, trigger <scenario> (theme, "
                       "trend-week, trend-month, trend-day or history-rows) and write frames "
                       "at 0-600 ms beside the screenshot."),
        QStringLiteral("scenario"));
    motionProbeOption.setFlags(QCommandLineOption::HiddenFromHelp);
    QCommandLineOption exportTargetOption(
        QStringLiteral("export-target"),
        QStringLiteral("Write exports and diagnostics reports into <dir> instead of "
                       "opening a file chooser. For tests and unattended runs."),
        QStringLiteral("dir"));

    parser.addOption(backendOption);
    parser.addOption(screenshotOption);
    parser.addOption(pageOption);
    parser.addOption(themeOption);
    parser.addOption(npcapOption);
    parser.addOption(candidatesOption);
    parser.addOption(calibrationOption);
    parser.addOption(sharedOption);
    parser.addOption(updateOption);
    parser.addOption(liveOption);
    parser.addOption(validationOption);
    parser.addOption(maintainerOption);
    parser.addOption(recordingOption);
    parser.addOption(alertFlowOption);
    parser.addOption(formalRunningOption);
    parser.addOption(validationKeyboardOption);
    parser.addOption(firstRunOption);
    parser.addOption(openDetailOption);
    parser.addOption(openEditOption);
    parser.addOption(delayOption);
    parser.addOption(openDetailOption2);
    parser.addOption(openEditOption2);
    parser.addOption(detailTabOption);
    parser.addOption(openReflectionOption);
    parser.addOption(uiStyleOption);
    parser.addOption(settingsTabOption);
    parser.addOption(sizeOption);
    parser.addOption(disclosureOption);
    parser.addOption(verifyTextOption);
    parser.addOption(midstreamOption);
    parser.addOption(exportTargetOption);
    parser.addOption(speechOption);
    parser.addOption(speechConfirmOption);
    parser.addOption(speechPreviewOption);
    parser.addOption(speechSelfTestOption);
    parser.addOption(motionProbeOption);
    // -- record wizard screenshots --
    QCommandLineOption openCreateOption(
        QStringLiteral("mock-open-create"),
        QStringLiteral("Open the 新增遗漏记录 wizard. Works with either backend."));
    QCommandLineOption wizardStepOption(
        QStringLiteral("mock-wizard-step"),
        QStringLiteral("Record wizard step (1, 2 or 3) to show with --mock-open-create or "
                       "--mock-open-edit."),
        QStringLiteral("step"), QStringLiteral("1"));
    parser.addOption(openCreateOption);
    parser.addOption(wizardStepOption);
    parser.process(app);
    const int wizardStep = parser.value(wizardStepOption).toInt();
    if (wizardStep < 1 || wizardStep > 3) {
        std::fputs("invalid --mock-wizard-step value (expected 1, 2 or 3)\n", stderr);
        return 2;
    }

    const bool motionProbe = parser.isSet(motionProbeOption);
    if (motionProbe && (!screenshotMode
                        || !mr::motion::isProbeScenario(parser.value(motionProbeOption)))) {
        std::fprintf(stderr, "--motion-probe needs --screenshot and one of: %s\n",
                     qPrintable(mr::motion::probeScenarios().join(QStringLiteral(", "))));
        return 2;
    }
    if (motionProbe)
        mr::motion::installProbeClock();

    // The same player an online sentence goes through, without a window, a
    // backend or a settings file: proves the deployed multimedia plugin works.
    if (parser.isSet(speechSelfTestOption))
        return mr::runSpeechSelfTest(parser.value(speechSelfTestOption));

    // 设置先于控制器和 QML 引擎创建，确保它们销毁时设置对象仍然存活。
    mr::AppSettings settings;

    const QString selectedBackend = parser.isSet(backendOption)
                                        ? parser.value(backendOption)
                                        : (screenshotMode ? QStringLiteral("mock")
                                                          : QStringLiteral("ipc"));

    if (selectedBackend != QLatin1String("ipc")
        && selectedBackend != QLatin1String("mock")) {
        std::fprintf(stderr,
                     "invalid --backend value: %s (expected mock or ipc)\n",
                     qPrintable(selectedBackend));
        return 2;
    }

    const QString mockLiveMode = parser.value(liveOption);
    if (selectedBackend == QLatin1String("mock")
        && mockLiveMode != QLatin1String("none")
        && mockLiveMode != QLatin1String("matched")
        && mockLiveMode != QLatin1String("entered")) {
        std::fprintf(stderr,
                     "invalid --mock-live value: %s (expected none, matched or entered)\n",
                     qPrintable(mockLiveMode));
        return 2;
    }
    const QString mockValidationState = parser.value(validationOption).toUpper();
    if (selectedBackend == QLatin1String("mock") && parser.isSet(validationOption)
        && !QStringList{QStringLiteral("WAITING"), QStringLiteral("RECORDING"),
                        QStringLiteral("STOPPING"), QStringLiteral("COMPLETED"),
                        QStringLiteral("FAILED")}.contains(mockValidationState)) {
        std::fprintf(stderr,
                     "invalid --mock-validation-state value: %s (expected waiting, recording, stopping, completed or failed)\n",
                     qPrintable(parser.value(validationOption)));
        return 2;
    }
    if (selectedBackend != QLatin1String("mock")
        && (parser.isSet(npcapOption)
            || parser.isSet(candidatesOption)
            || parser.isSet(calibrationOption)
            || parser.isSet(sharedOption)
            || parser.isSet(updateOption)
            || parser.isSet(recordingOption)
            || parser.isSet(alertFlowOption)
            || parser.isSet(liveOption)
            || parser.isSet(validationOption)
            || parser.isSet(formalRunningOption)
            || parser.isSet(validationKeyboardOption)
            || parser.isSet(firstRunOption)
            || parser.isSet(openDetailOption)
            || parser.isSet(openEditOption)
            || parser.isSet(midstreamOption)
            || parser.isSet(openReflectionOption)
            || parser.isSet(speechOption)
            || parser.isSet(speechConfirmOption)
            || parser.isSet(speechPreviewOption))) {
        std::fputs("--mock-* options require --backend mock or implicit mock via --screenshot\n",
                   stderr);
        return 2;
    }
    if (parser.isSet(validationKeyboardOption)
        && (!screenshotMode || mockValidationState != QLatin1String("RECORDING"))) {
        std::fputs("--mock-validation-keyboard requires a screenshot with "
                   "--mock-validation-state recording\n",
                   stderr);
        return 2;
    }

    if (parser.isSet(recordingOption) && !QStringList{QStringLiteral("waiting"),
            QStringLiteral("waiting-verified"), QStringLiteral("waiting-calibrating"),
            QStringLiteral("checking"),
            QStringLiteral("listening"), QStringLiteral("blocked")}.contains(parser.value(recordingOption))) {
        std::fputs("invalid --mock-recording-state value\n", stderr);
        return 2;
    }
    if (parser.isSet(calibrationOption) && !QStringList{QStringLiteral("observing"),
            QStringLiteral("ready"), QStringLiteral("blocked"), QStringLiteral("done"),
            QStringLiteral("idle")}.contains(parser.value(calibrationOption))) {
        std::fputs("invalid --mock-calibration value\n", stderr);
        return 2;
    }
    if (parser.isSet(sharedOption) && !QStringList{QStringLiteral("fetching"),
            QStringLiteral("verifying"), QStringLiteral("consent"), QStringLiteral("verified"),
            QStringLiteral("verified-auditing"), QStringLiteral("imported-published"),
            QStringLiteral("imported-unpublished"),
            QStringLiteral("rejected"), QStringLiteral("unavailable"), QStringLiteral("user-rejected"),
            QStringLiteral("none-for-build"), QStringLiteral("share")}.contains(parser.value(sharedOption))) {
        std::fputs("invalid --mock-shared value\n", stderr);
        return 2;
    }
    const QString mockSpeech = parser.value(speechOption);
    if (parser.isSet(speechOption) && !mr::MockBackend::isSpeechFixture(mockSpeech)) {
        std::fprintf(stderr,
                     "invalid --mock-speech value: %s (expected azure, openai, unconfigured or fail)\n",
                     qPrintable(mockSpeech));
        return 2;
    }
    if (parser.isSet(alertFlowOption) && (!screenshotMode || parser.isSet(maintainerOption)
            || parser.isSet(validationOption) || parser.isSet(candidatesOption))) {
        std::fputs("--mock-recording-alert-flow requires a normal mock screenshot\n", stderr);
        return 2;
    }
    // Only drives navigation, so either backend accepts it; checked before a
    // backend or a Collector exists.
    const QString settingsTab = parser.value(settingsTabOption);
    if (!settingsTab.isEmpty()
        && !QStringList{QStringLiteral("general"), QStringLiteral("tts"), QStringLiteral("goal"),
                        QStringLiteral("data"), QStringLiteral("about")}.contains(settingsTab)) {
        std::fprintf(stderr, "invalid --settings-tab value: %s (expected general, tts, goal, data or about)\n",
                     qPrintable(settingsTab));
        return 2;
    }
    mr::IBackend *backend = nullptr;
    mr::MockBackend *mockBackend = nullptr;
    if (selectedBackend == QLatin1String("ipc")) {
        backend = new mr::IpcBackend(&app);
    } else {
        auto *mock = new mr::MockBackend(&app);
        mockBackend = mock;
        mock->setNpcapMissing(parser.isSet(npcapOption));
        const QString live = mockLiveMode;
        mock->setLiveMode(live == QLatin1String("none")
                              ? mr::MockBackend::LiveMode::None
                              : live == QLatin1String("matched")
                                    ? mr::MockBackend::LiveMode::Matched
                                    : mr::MockBackend::LiveMode::Entered);
        if (parser.isSet(validationOption))
            mock->setValidationFixture(mockValidationState);
        if (parser.isSet(formalRunningOption))
            mock->setFormalCaptureRunning(true);
        if (parser.isSet(midstreamOption))
            mock->setMidstreamSuspected(true);
        if (parser.isSet(candidatesOption))
            mock->setCandidateFixture();
        if (parser.isSet(calibrationOption))
            mock->setCalibrationFixture(parser.value(calibrationOption));
        if (parser.isSet(sharedOption))
            mock->setSharedCalibrationFixture(parser.value(sharedOption));
        if (parser.isSet(updateOption))
            mock->setUpdateAvailable(true);
        if (parser.isSet(speechOption))
            mock->setSpeechFixture(mockSpeech);
        if (parser.isSet(recordingOption)) mock->setRecordingFixture(parser.value(recordingOption));
        if (parser.isSet(alertFlowOption)) mock->setRecordingFixture(QStringLiteral("waiting"));
        backend = mock;
    }

    // A speech fixture selects its online voice for this run only: the choice
    // and the confirmation are put back however the process ends normally.
    const bool pinSpeech = mockBackend
                           && (parser.isSet(speechOption) || parser.isSet(speechConfirmOption));
    const QString savedVoice = settings.ttsVoice();
    const bool savedOnlineConfirmed = settings.ttsOnlineConfirmed();
    const auto restoreSpeech = qScopeGuard([&settings, pinSpeech, savedVoice, savedOnlineConfirmed] {
        if (!pinSpeech)
            return;
        settings.setTtsVoice(savedVoice);
        settings.setTtsOnlineConfirmed(savedOnlineConfirmed);
    });
    QString forceSpeechConfirm;
    if (pinSpeech && parser.isSet(speechConfirmOption)) {
        // The question is asked while a local voice is in use.
        settings.setTtsVoice(QString());
        settings.setTtsOnlineConfirmed(false);
        forceSpeechConfirm = mr::MockBackend::speechFixtureVoiceId(
            parser.isSet(speechOption) ? mockSpeech : QStringLiteral("azure"));
    } else if (pinSpeech) {
        settings.setTtsVoice(mr::MockBackend::speechFixtureVoiceId(mockSpeech));
        settings.setTtsOnlineConfirmed(true);
    }

    // Only the production IPC composition has authority over a local process.
    // Declaration order destroys the controller before its borrowed supervisor.
    std::unique_ptr<mr::CollectorProcess> collector;
    if (selectedBackend == QLatin1String("ipc"))
        collector = std::make_unique<mr::CollectorProcess>();
    mr::AppController controller(backend, &settings, nullptr, collector.get());
    // The validation and candidate scenarios exercise maintainer tools that a
    // player never sees by default, so those screenshots start with them open.
    if (parser.isSet(maintainerOption) || parser.isSet(validationOption) || parser.isSet(validationKeyboardOption)
        || parser.isSet(candidatesOption))
        controller.setMaintainerToolsVisible(true);
    if (parser.isSet(exportTargetOption))
        controller.setExportTargetOverride(parser.value(exportTargetOption));
    else if (screenshotMode) {
        // A screenshot run must never block on a modal file chooser.
        controller.setExportTargetOverride(
            QDir::temp().absoluteFilePath(QStringLiteral("MentorRecorder-screenshot-exports")));
    }
    auto *formatters = new mr::Formatters(&app);
    auto *jobs = new mr::JobCatalog(&app);
    auto *roles = new mr::RoleCatalog(&app);
    auto *validator = new mr::RunFormValidator(&app);
    if (parser.isSet(themeOption))
        controller.setThemeMode(parser.value(themeOption));

    QQmlApplicationEngine engine;
    engine.rootContext()->setContextProperty(QStringLiteral("App"), &controller);
    engine.rootContext()->setContextProperty(QStringLiteral("Fmt"), formatters);
    engine.rootContext()->setContextProperty(QStringLiteral("Jobs"), jobs);
    engine.rootContext()->setContextProperty(QStringLiteral("Roles"), roles);
    engine.rootContext()->setContextProperty(QStringLiteral("RunForm"), validator);
    engine.rootContext()->setContextProperty(QStringLiteral("Settings"), &settings);
    engine.rootContext()->setContextProperty(QStringLiteral("Tts"), controller.tts());
    engine.rootContext()->setContextProperty(QStringLiteral("GraphsAvailable"),
                                             probeGraphs(&engine));
    engine.rootContext()->setContextProperty(
        QStringLiteral("SuppressOnboarding"),
        screenshotMode && !parser.isSet(firstRunOption));
    engine.rootContext()->setContextProperty(QStringLiteral("ForceBaselineDialog"),
                                             parser.isSet(firstRunOption));
    engine.rootContext()->setContextProperty(QStringLiteral("ForceDisclosure"),
                                             parser.isSet(disclosureOption));
    const QString uiStyle = parser.value(uiStyleOption);
    if (!uiStyle.isEmpty() && uiStyle != QLatin1String("eorzea")
        && uiStyle != QLatin1String("classic") && uiStyle != QLatin1String("harendotes")) {
        std::fprintf(stderr, "invalid --mock-ui-style value: %s (expected classic, eorzea or harendotes)\n",
                     qPrintable(uiStyle));
        return 2;
    }
    const QString detailTab = parser.value(detailTabOption);
    if (!detailTab.isEmpty()
        && !QStringList{QStringLiteral("info"), QStringLiteral("events"),
                        QStringLiteral("revs"), QStringLiteral("refl")}.contains(detailTab)) {
        std::fprintf(stderr, "invalid --mock-detail-tab value: %s (expected info, events, revs or refl)\n",
                     qPrintable(detailTab));
        return 2;
    }
    engine.rootContext()->setContextProperty(QStringLiteral("ForceUiStyle"), uiStyle);
    engine.rootContext()->setContextProperty(QStringLiteral("ForceSettingsTab"), settingsTab);
    engine.rootContext()->setContextProperty(QStringLiteral("ForceSpeechConfirm"), forceSpeechConfirm);
    // Screenshot runs must be deterministic frames: no fades, no sweeps. The
    // same holds when Windows' 显示动画 is off. A motion probe is the exception:
    // its whole point is the motion.
    const bool reduceMotion = !motionProbe
                              && (screenshotMode || mr::motion::systemReducesMotion());
    engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), reduceMotion);
    engine.rootContext()->setContextProperty(QStringLiteral("ForceDetailTab"), detailTab);
    engine.rootContext()->setContextProperty(QStringLiteral("ForceOpenReflection"),
                                             parser.isSet(openReflectionOption));
    const bool openDetail = parser.isSet(openDetailOption)
                            || parser.isSet(openEditOption)
                            || parser.isSet(openReflectionOption)
                            || !detailTab.isEmpty()
                            || parser.isSet(openDetailOption2)
                            || parser.isSet(openEditOption2);
    const bool openEdit = parser.isSet(openEditOption) || parser.isSet(openEditOption2);
    engine.rootContext()->setContextProperty(QStringLiteral("ForceOpenDetail"), openDetail);
    engine.rootContext()->setContextProperty(QStringLiteral("ForceOpenEdit"), openEdit);
    engine.rootContext()->setContextProperty(QStringLiteral("ForceOpenCreate"),
                                             parser.isSet(openCreateOption));
    engine.rootContext()->setContextProperty(QStringLiteral("ForceWizardStep"), wizardStep);

    QObject::connect(&engine, &QQmlApplicationEngine::objectCreationFailed, &app,
                     [] { QCoreApplication::exit(2); }, Qt::QueuedConnection);
    engine.loadFromModule("MentorRecorder", "Main");

    if (engine.rootObjects().isEmpty())
        return 2;

    auto *window = qobject_cast<QQuickWindow *>(engine.rootObjects().constFirst());
    if (!window) {
        std::fputs("root object is not a window\n", stderr);
        return 3;
    }

    roundWindowCorners(window);

    if (parser.isSet(speechPreviewOption)) {
        // After the mock's speech settings and status have arrived.
        QTimer::singleShot(400, controller.tts(), [&controller] {
            controller.tts()->preview(QStringLiteral("finished"));
        });
    }

    if (!parser.isSet(screenshotOption)) {
        // 事件循环结束后先移除托盘及窗口过滤器，再销毁 QML 窗口。
        mr::TrayController tray(&controller, &settings, window);
        // Without a tray there is nowhere to minimise to, so closing the last
        // window must still end the process.
        QApplication::setQuitOnLastWindowClosed(!tray.isActive());
        controller.runDailyBackupIfDue();
        return app.exec();
    }

    // ---------------------------------------------------------------- shot --
    controller.navigate(parser.value(pageOption).toInt() - 1);
    {
        const QStringList size = parser.value(sizeOption).split(QLatin1Char('x'));
        const int width = size.size() == 2 ? qBound(640, size.at(0).toInt(), 4096) : 1280;
        const int height = size.size() == 2 ? qBound(480, size.at(1).toInt(), 4096) : 800;
        window->resize(width, height);
    }
    window->show();

    const QString target = parser.value(screenshotOption);
    const int delay = qMax(50, parser.value(delayOption).toInt());
    int exitCode = 0;

    QTimer::singleShot(delay, &app, [&] {
        if (motionProbe) {
            exitCode = mr::motion::runProbe(window, &controller,
                                            parser.value(motionProbeOption), target);
            QCoreApplication::quit();
            return;
        }
        if (parser.isSet(alertFlowOption) && !verifyRecordingAlertFlow(window, mockBackend, &controller)) {
            exitCode = 9;
            QCoreApplication::quit();
            return;
        }
        if (parser.isSet(validationKeyboardOption)
            && !verifyValidationMarkerKeyboard(window, mockBackend)) {
            exitCode = 7;
            QCoreApplication::quit();
            return;
        }
        for (const QString &needle : parser.values(verifyTextOption)) {
            if (needle.isEmpty())
                continue;
            // Rendering a heavy theme can postpone the mock backend's queued replies
            // beyond the screenshot delay. Wait for the asserted state, not a fixed
            // scheduling gap; genuinely missing text still fails within a bounded time.
            QElapsedTimer textDeadline;
            textDeadline.start();
            while (!sceneShowsText(window->contentItem(), needle)
                   && textDeadline.elapsed() < 5000) {
                QEventLoop pending;
                QTimer::singleShot(20, &pending, &QEventLoop::quit);
                pending.exec();
            }
            if (!sceneShowsText(window->contentItem(), needle)) {
                std::fprintf(stderr, "expected on-screen text not found: %s\n",
                             qPrintable(needle));
                exitCode = 8;
                QCoreApplication::quit();
                return;
            }
            std::fprintf(stdout, "verified on-screen text: %s\n", qPrintable(needle));
        }
        const QImage frame = window->grabWindow();
        if (frame.isNull()) {
            std::fputs("grabWindow returned a null image\n", stderr);
            exitCode = 4;
        } else {
            QDir().mkpath(QFileInfo(target).absolutePath());
            if (!frame.save(target)) {
                std::fprintf(stderr, "cannot write %s\n", qPrintable(target));
                exitCode = 5;
            } else {
                std::fprintf(stdout, "wrote %s (%dx%d)\n", qPrintable(target),
                             frame.width(), frame.height());
            }
        }
        QCoreApplication::quit();
    });

    const int loopResult = app.exec();
    return exitCode != 0 ? exitCode : loopResult;
}
