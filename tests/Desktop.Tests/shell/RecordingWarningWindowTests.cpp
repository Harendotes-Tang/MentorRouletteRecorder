#include "DesktopTestRunner.h"
#include "TestCollectorGuard.h"
#include "AppController.h"
#include "BundledFonts.h"
#include "AppSettings.h"
#include "Formatters.h"
#include "JobCatalog.h"
#include "MockBackend.h"
#include "RoleCatalog.h"
#include "RunFormValidator.h"

#include <QDir>
#include <QGuiApplication>
#include <QImage>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QScreen>
#include <QSignalSpy>
#include <QStandardPaths>
#include <QTemporaryDir>
#include <QTest>
#include <memory>

#ifdef Q_OS_WIN
#  ifndef NOMINMAX
#    define NOMINMAX
#  endif
#  ifndef WIN32_LEAN_AND_MEAN
#    define WIN32_LEAN_AND_MEAN
#  endif
#  include <windows.h>
#endif

namespace {

class WarningBackend final : public mr::MockBackend
{
public:
    QJsonObject capture{{"ffxiv_running", true}, {"ffxiv_process_id", 123},
                        {"profile_status", "VERIFIED"}, {"state", "RUNNING"},
                        {"silent_reason", "NONE"}, {"last_error_code", "NONE"},
                        {"npcap_installed", true}};

    WarningBackend() { setRecordingFixture(QStringLiteral("listening")); }

    mr::BackendReply *request(const QString &type, const QJsonObject &payload = {}) override
    {
        auto *original = mr::MockBackend::request(type, payload);
        if (type != QLatin1String("GetStatus") && type != QLatin1String("GetCaptureStatus"))
            return original;
        auto *reply = new mr::BackendReply(QStringLiteral("warning-status"), type, this);
        original->whenDone(reply, [this, reply, type](bool ok, const QVariantMap &body,
                                                     const QString &code, const QString &message) {
            if (!ok) { reply->fail(code, message); return; }
            auto result = QJsonObject::fromVariantMap(body);
            if (type == QLatin1String("GetStatus")) result.insert(QStringLiteral("capture"), capture);
            else result = capture;
            reply->succeed(result);
        });
        return reply;
    }
};

struct Scene
{
    QTemporaryDir scratch;
    mr::AppSettings settings{scratch.filePath(QStringLiteral("desktop.ini")), nullptr};
    WarningBackend backend;
    mr::AppController app{&backend, &settings};
    mr::Formatters formatters;
    mr::JobCatalog jobs;
    mr::RoleCatalog roles;
    mr::RunFormValidator validator;
    QQmlEngine engine;
    std::unique_ptr<QObject> root;
    QString errors;

    bool create()
    {
        auto *context = engine.rootContext();
        context->setContextProperty(QStringLiteral("App"), &app);
        context->setContextProperty(QStringLiteral("Fmt"), &formatters);
        context->setContextProperty(QStringLiteral("Jobs"), &jobs);
        context->setContextProperty(QStringLiteral("Roles"), &roles);
        context->setContextProperty(QStringLiteral("RunForm"), &validator);
        context->setContextProperty(QStringLiteral("Settings"), &settings);
        context->setContextProperty(QStringLiteral("SuppressReleaseNotes"), true);
        context->setContextProperty(QStringLiteral("SuppressOnboarding"), true);
        context->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QObject::connect(&engine, &QQmlEngine::warnings, &engine,
                         [this](const QList<QQmlError> &warnings) {
            for (const auto &warning : warnings) errors += warning.toString() + QLatin1Char('\n');
        });
        QQmlComponent component(&engine, QUrl::fromLocalFile(
            QString::fromUtf8(MR_DESKTOP_QML_DIR) + QStringLiteral("/Main.qml")));
        root.reset(component.create());
        errors += component.errorString();
        return root != nullptr;
    }
    QQuickWindow *window() const { return qobject_cast<QQuickWindow *>(root.get()); }
    QQuickWindow *warning() const
    {
        return root->findChild<QQuickWindow *>(QStringLiteral("recordingWarningWindow"));
    }
    void recovering()
    {
        backend.capture.insert(QStringLiteral("silent_reason"), QStringLiteral("MIDSTREAM"));
        backend.capture.insert(QStringLiteral("path_recovery"), QJsonObject{{"state", "RECOVERING"}});
        app.recording()->observeCapture(backend.capture.toVariantMap());
    }
    void failed()
    {
        backend.capture.insert(QStringLiteral("path_recovery"), QJsonObject{{"state", "FAILED"}});
        app.recording()->observeCapture(backend.capture.toVariantMap());
    }
    void healthy()
    {
        backend.capture.insert(QStringLiteral("silent_reason"), QStringLiteral("NONE"));
        backend.capture.insert(QStringLiteral("path_recovery"), QJsonObject{{"state", "NONE"}});
        app.recording()->observeCapture(backend.capture.toVariantMap());
    }
    bool invokeButton(const QString &name) const
    {
        auto *button = warning()->findChild<QQuickItem *>(name);
        return button && QMetaObject::invokeMethod(button, "clicked");
    }
};

class RecordingWarningWindowTests final : public QObject
{
    Q_OBJECT
private slots:
    void initTestCase()
    {
        const auto path = QString::fromUtf8(MR_DESKTOP_QML_DIR);
        qmlRegisterSingletonType(QUrl::fromLocalFile(path + QStringLiteral("/Theme.qml")),
                                 "MentorRecorder", 1, 0, "Theme");
        for (const auto *directory : {"/charts", "/components", "/dialogs", "/pages"}) {
            const auto folder = path + QString::fromLatin1(directory);
            for (const auto &file : QDir(folder).entryList({QStringLiteral("*.qml")}, QDir::Files)) {
                const auto name = file.chopped(4).toUtf8();
                qmlRegisterType(QUrl::fromLocalFile(folder + QLatin1Char('/') + file),
                                "MentorRecorder", 1, 0, name.constData());
            }
        }
    }

    void hiddenMainReceivesEarlyWarning()
    {
        Scene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QTRY_COMPARE(scene.app.recording()->state(), QStringLiteral("listening"));
        QVERIFY(scene.warning());
        scene.window()->hide();
        scene.recovering();
        QTRY_VERIFY(scene.warning()->isVisible());
        QVERIFY(!scene.window()->isVisible());
        QVERIFY(scene.app.recording()->recovering());
        QVERIFY(!scene.app.recording()->blocked());
        QVERIFY(scene.warning()->flags().testFlag(Qt::WindowStaysOnTopHint));
        QVERIFY(scene.warning()->flags().testFlag(Qt::WindowDoesNotAcceptFocus));
        QCOMPARE(scene.warning()->modality(), Qt::NonModal);
        QCOMPARE(scene.warning()->transientParent(), nullptr);
        QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
    }

    void minimizedMainStaysMinimized()
    {
        Scene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QTRY_COMPARE(scene.app.recording()->state(), QStringLiteral("listening"));
        scene.window()->showMinimized();
        scene.recovering();
        QTRY_VERIFY(scene.warning()->isVisible());
        QCOMPARE(scene.window()->visibility(), QWindow::Minimized);
        QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
    }

    void acknowledgementSurvivesFailureAndRecoveryClosesTheWindow()
    {
        Scene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QTRY_COMPARE(scene.app.recording()->state(), QStringLiteral("listening"));
        scene.window()->hide();
        scene.recovering();
        QTRY_VERIFY(scene.warning()->isVisible());
        const auto serial = scene.app.recording()->warningSerial();
        QVERIFY(scene.invokeButton(QStringLiteral("recordingWarningAcknowledge")));
        QTRY_VERIFY(!scene.warning()->isVisible());
        QVERIFY(!scene.app.recording()->warningPending());
        scene.failed();
        QTest::qWait(30);
        QVERIFY(!scene.warning()->isVisible());
        QCOMPARE(scene.app.recording()->warningSerial(), serial);
        scene.healthy();
        QTRY_VERIFY(!scene.app.recording()->warningActive());
        scene.recovering();
        QTRY_VERIFY(scene.warning()->isVisible());
        QVERIFY(scene.app.recording()->warningSerial() > serial);
        scene.healthy();
        QTRY_VERIFY(!scene.warning()->isVisible());
        QVERIFY(!scene.window()->isVisible());
        QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
    }

    void explicitDiagnosisShowsTheMainWindow()
    {
        Scene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QTRY_COMPARE(scene.app.recording()->state(), QStringLiteral("listening"));
        scene.window()->hide();
        scene.recovering();
        QTRY_VERIFY(scene.warning()->isVisible());
        QVERIFY(scene.invokeButton(QStringLiteral("recordingWarningDiagnose")));
        QTRY_VERIFY(scene.window()->isVisible());
        QTRY_COMPARE(scene.app.currentPage(), 4);
        QTRY_VERIFY(!scene.warning()->isVisible());
        QVERIFY(!scene.app.recording()->warningPending());
        QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
    }

    void nativeWarningKeepsTheOtherWindowForeground_data()
    {
        QTest::addColumn<int>("mainVisibility");
        QTest::newRow("visible-behind-game") << int(QWindow::Windowed);
        QTest::newRow("hidden") << int(QWindow::Hidden);
        QTest::newRow("minimized") << int(QWindow::Minimized);
    }

    void nativeWarningKeepsTheOtherWindowForeground()
    {
#ifdef Q_OS_WIN
        QFETCH(int, mainVisibility);
        if (QGuiApplication::platformName() == QLatin1String("offscreen"))
            QSKIP("Requires the Windows platform; functional coverage runs offscreen.");
        Scene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QTRY_COMPARE(scene.app.recording()->state(), QStringLiteral("listening"));
        QQuickWindow other;
        other.resize(320, 200);
        other.show();
        other.requestActivate();
        QVERIFY(QTest::qWaitForWindowActive(&other));
        QTRY_COMPARE(::GetForegroundWindow(), reinterpret_cast<HWND>(other.winId()));
        const auto foreground = ::GetForegroundWindow();
        // Establish the game-like foreground before hiding/minimizing the
        // background main window; otherwise Windows may revoke this test
        // process's permission to establish a new foreground sentinel.
        if (mainVisibility == int(QWindow::Hidden)) scene.window()->hide();
        else if (mainVisibility == int(QWindow::Minimized)) {
            scene.window()->showMinimized();
            QTRY_VERIFY(::IsIconic(reinterpret_cast<HWND>(scene.window()->winId())));
        }
        QTRY_COMPARE(::GetForegroundWindow(), foreground);
        scene.recovering();
        QTRY_VERIFY(scene.warning()->isVisible());
        const auto recoveringSerial = scene.app.recording()->warningSerial();
        const auto handle = reinterpret_cast<HWND>(scene.warning()->winId());
        const auto style = ::GetWindowLongPtrW(handle, GWL_EXSTYLE);
        QVERIFY(style & WS_EX_NOACTIVATE);
        QVERIFY(style & WS_EX_TOPMOST);
        QVERIFY(style & WS_EX_TOOLWINDOW);
        QCOMPARE(::GetWindow(handle, GW_OWNER), HWND(nullptr));
        QCOMPARE(::GetForegroundWindow(), foreground);
        scene.failed();
        QTRY_VERIFY(scene.app.recording()->blocked());
        QCOMPARE(scene.app.recording()->warningSerial(), recoveringSerial);
        QTest::qWait(160);
        QCOMPARE(::GetForegroundWindow(), foreground);
        QVERIFY(scene.warning()->isVisible());
        scene.healthy();
        QTRY_VERIFY(!scene.warning()->isVisible());
        // A direct blocker uses the legacy Dialog path as well; it must stay
        // deferred while this separate native window has the foreground.
        scene.failed();
        QTRY_VERIFY(scene.app.recording()->blocked());
        QTRY_VERIFY(scene.warning()->isVisible());
        QTest::qWait(160);
        QCOMPARE(::GetForegroundWindow(), foreground);
        QCOMPARE(int(scene.window()->visibility()), mainVisibility);
        QVERIFY(!scene.window()->isActive());
        QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
#else
        QSKIP("Windows-only native focus and style assertions.");
#endif
    }

    void renderedLongMessage_data()
    {
        QTest::addColumn<QString>("style");
        QTest::addColumn<bool>("dark");
        for (const auto *style : {"classic", "eorzea", "harendotes"}) {
            for (bool dark : {false, true}) {
                const auto name = QByteArray(style) + (dark ? "-dark" : "-light");
                QTest::newRow(name.constData()) << QString::fromLatin1(style) << dark;
            }
        }
    }

    void renderedLongMessage()
    {
        QFETCH(QString, style);
        QFETCH(bool, dark);
        Scene scene;
        scene.settings.setUiStyle(style);
        scene.app.setThemeMode(dark ? QStringLiteral("dark") : QStringLiteral("light"));
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QTRY_COMPARE(scene.app.recording()->state(), QStringLiteral("listening"));
        scene.window()->hide();
        scene.recovering();
        QTRY_VERIFY(scene.warning()->isVisible());
        const auto shortHeight = scene.warning()->height();
        const auto sentence = dark
            ? QString::fromUtf8("ゲームに再接続しましたが、利用できるデータを読み取れません。現在の接続設定を保持し、診断レポートを確認してください。")
            : QString::fromUtf8("你已经重新连接过游戏，但软件仍未读到可用数据。请保留当前加速器设置，导出诊断报告以继续排查。");
        scene.backend.capture.insert(QStringLiteral("hint"), sentence.repeated(7));
        scene.failed();
        QTRY_VERIFY(scene.warning()->isVisible());
        auto *message = scene.warning()->findChild<QQuickItem *>(QStringLiteral("recordingWarningMessage"));
        auto *confirm = scene.warning()->findChild<QQuickItem *>(QStringLiteral("recordingWarningAcknowledge"));
        QVERIFY(message && confirm);
        QTRY_VERIFY(message->property("text").toString().contains(sentence));
        QTRY_VERIFY(scene.warning()->height() > shortHeight);
        QCOMPARE(message->property("textFormat").toInt(), int(Qt::PlainText));
        QTRY_VERIFY(message->property("paintedWidth").toReal() <= message->width() + 1);
        const auto buttonBounds = confirm->mapRectToScene(confirm->boundingRect());
        QVERIFY(buttonBounds.top() >= 0);
        QVERIFY(buttonBounds.bottom() <= scene.warning()->height());
        QTRY_VERIFY(scene.warning()->screen()->availableGeometry().contains(scene.warning()->geometry()));
        scene.warning()->requestUpdate();
        QTest::qWait(100);
        const auto image = scene.warning()->grabWindow();
        QVERIFY(!image.isNull());
        const auto directory = QString::fromUtf8(MR_SOURCE_DIR)
            + QStringLiteral("/artifacts/recording-warning-ui-20261025");
        QVERIFY(QDir().mkpath(directory));
        QVERIFY(image.save(directory + QLatin1Char('/') + style
                           + (dark ? QStringLiteral("-dark.png") : QStringLiteral("-light.png"))));
        QVERIFY(scene.invokeButton(QStringLiteral("recordingWarningAcknowledge")));
        QTRY_VERIFY(!scene.warning()->isVisible());
        QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
    }
};

} // namespace

MR_DESKTOP_TEST_SUITE(RecordingWarningWindow)
{
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication app(argc, argv);
    if (!mr::registerBundledFonts()) return 2;
    QGuiApplication::setFont(QFont(QStringLiteral("Noto Sans SC")));
    RecordingWarningWindowTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "RecordingWarningWindowTests.moc"
