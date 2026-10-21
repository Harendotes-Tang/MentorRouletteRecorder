// ---------------------------------------------------------------------------
// tst_capturepage - the 捕获诊断 page in shipping QML
// (docs/ui-design.md §4.4).
//
// What it pins:
//   * the title row: a player reads the listening state in words and never a
//     start / stop button; a maintainer gets the button and the 维护者工具 block;
//   * 链路: the summary line and the four columns when everything is up, while
//     the game is not running, and while Npcap is missing (with the 降级模式 panel);
//   * 链路 with the game closed but the installed version known: the profile is
//     answered for that version instead of being deferred to the game's launch,
//     and nothing tells the player to go play a duty while the game is shut;
//   * 最近有效事件: time plus the Chinese name of a known kind, the time alone
//     for a kind this build does not know;
//   * a player's page carries no opcode, hex or ERR_ token and no maintainer text.
//
// The page is loaded with App, Fmt and ReduceMotion only - the same context
// SharedCalibrationCardTests uses - so it must not need Settings.
// ---------------------------------------------------------------------------

#include "TestCollectorGuard.h"
#include "AppController.h"
#include "Formatters.h"
#include "IBackend.h"
#include "MockBackend.h"

#include <QDir>
#include <QGuiApplication>
#include <QFontDatabase>
#include <QHash>
#include <QJsonArray>
#include <QJsonObject>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickWindow>
#include <QQuickStyle>
#include <QRegularExpression>
#include <QStandardPaths>
#include <QSignalSpy>
#include <QTest>

#include <memory>

namespace {

QQuickItem *findVisualItem(QQuickItem *root, const QString &name)
{
    if (!root)
        return nullptr;
    if (root->objectName() == name)
        return root;
    for (auto *child : root->childItems()) {
        if (auto *found = findVisualItem(child, name))
            return found;
    }
    return nullptr;
}

void collectVisibleText(QQuickItem *item, QStringList &texts)
{
    if (!item || !item->isVisible())
        return;
    const QVariant text = item->property("text");
    if (text.isValid() && !text.toString().isEmpty())
        texts.append(text.toString());
    for (auto *child : item->childItems())
        collectVisibleText(child, texts);
}

/// Answers GetStatus / GetCaptureStatus with one hand-written capture status;
/// everything else succeeds empty.
class CaptureBackend final : public mr::IBackend
{
public:
    QJsonObject capture;
    /// GetCaptureValidationStatus; no session by default.
    QJsonObject validation{{QStringLiteral("active"), false}};
    /// ListCaptureAdapters when set.
    QJsonObject adapters;
    QHash<QString, int> counts;

    QString backendName() const override { return QStringLiteral("mock"); }
    bool isConnected() const override { return true; }

    mr::BackendReply *request(const QString &type, const QJsonObject & = {}) override
    {
        ++counts[type];
        auto *reply = new mr::BackendReply(type, type, this);
        if (type == QLatin1String("GetStatus"))
            reply->succeed({{QStringLiteral("capture"), capture}});
        else if (type == QLatin1String("GetCaptureStatus"))
            reply->succeed(capture);
        // Follow-the-game on and no validation session: the recording controller
        // can then reach its capture verdicts instead of waiting for a setting.
        else if (type == QLatin1String("GetCaptureSettings"))
            reply->succeed({{QStringLiteral("follow_game"), true}});
        else if (type == QLatin1String("GetCaptureValidationStatus"))
            reply->succeed(validation);
        else if (type == QLatin1String("ListCaptureAdapters") && !adapters.isEmpty())
            reply->succeed(adapters);
        else
            reply->succeed({});
        return reply;
    }
};

/// A Collector the Desktop cannot read (review K2-1): no pipe at all - not started yet,
/// refused to start, exited - or a pipe on which GetStatus fails. Either way nothing is
/// known about Npcap, and the page must not say it is missing.
class UnreadableBackend final : public mr::IBackend
{
public:
    bool connected = false;
    bool statusReadable = false;
    QJsonObject capture;

    QString backendName() const override { return QStringLiteral("ipc"); }
    bool isConnected() const override { return connected; }

    void setConnected(bool value)
    {
        connected = value;
        Q_EMIT connectionChanged();
    }

    mr::BackendReply *request(const QString &type, const QJsonObject & = {}) override
    {
        auto *reply = new mr::BackendReply(type, type, this);
        if (!connected) {
            reply->failUnsent(QStringLiteral("ERR_INTERNAL"), QStringLiteral("not connected"));
        } else if (type == QLatin1String("GetStatus") || type == QLatin1String("GetCaptureStatus")) {
            if (!statusReadable)
                reply->failTransport(QStringLiteral("ERR_TIMEOUT"), QStringLiteral("timed out"));
            else if (type == QLatin1String("GetStatus"))
                reply->succeed({{QStringLiteral("capture"), capture}});
            else
                reply->succeed(capture);
        } else if (type == QLatin1String("GetCaptureSettings")) {
            reply->succeed({{QStringLiteral("follow_game"), true}});
        } else if (type == QLatin1String("GetCaptureValidationStatus")) {
            reply->succeed({{QStringLiteral("active"), false}});
        } else {
            reply->succeed({});
}
        return reply;
    }
};

/// The whole page on one backend, torn down in dependency order.
struct PageScene
{
    std::unique_ptr<mr::MockBackend> mock = std::make_unique<mr::MockBackend>();
    std::unique_ptr<mr::IBackend> other;
    std::unique_ptr<mr::AppController> app;
    std::unique_ptr<mr::Formatters> formatters;
    std::unique_ptr<QQmlEngine> engine;
    std::unique_ptr<QObject> root;
    QQuickItem *page = nullptr;

    ~PageScene()
    {
        root.reset();
        engine.reset();
        app.reset();
    }

    bool open(bool maintainer = false) { return openOn(other ? other.get() : mock.get(), maintainer); }

    bool openOn(mr::IBackend *source, bool maintainer)
    {
        app = std::make_unique<mr::AppController>(source, nullptr);
        app->setMaintainerToolsVisible(maintainer);
        formatters = std::make_unique<mr::Formatters>();
        engine = std::make_unique<QQmlEngine>();
        engine->rootContext()->setContextProperty(QStringLiteral("App"), app.get());
        engine->rootContext()->setContextProperty(QStringLiteral("Fmt"), formatters.get());
        engine->rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QQmlComponent component(engine.get());
        component.setData(QByteArrayLiteral(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 1180; height: 900; visible: true
    color: Theme.surface
    CapturePage { objectName: "capturePage"; anchors.fill: parent; anchors.margins: 16 }
})"), QUrl());
        root.reset(component.create());
        if (!root) {
            qWarning("%s", qPrintable(component.errorString()));
            return false;
        }
        page = qobject_cast<QQuickItem *>(root->findChild<QObject *>(QStringLiteral("capturePage")));
        return page != nullptr;
    }

    QQuickItem *item(const QString &name) const { return findVisualItem(page, name); }
    bool shows(const QString &name) const
    {
        auto *found = item(name);
        return found && found->isVisible();
    }
    QString text(const QString &name) const
    {
        auto *found = item(name);
        return found ? found->property("text").toString() : QString();
    }
    QStringList visibleTexts() const
    {
        QStringList texts;
        collectVisibleText(page, texts);
        return texts;
    }
};

QString chainValue(const PageScene &scene, const char *key)
{
    return scene.text(QStringLiteral("captureChainValue_") + QLatin1String(key));
}

QString chainSub(const PageScene &scene, const char *key)
{
    return scene.text(QStringLiteral("captureChainSub_") + QLatin1String(key));
}

/// The 链路 summary is drawn in Theme.textSecondary while it is only waiting and
/// in Theme.orangeText when something is wrong. The sub lines of the four
/// columns are always Theme.textSecondary, so one of them names the wait tone
/// without this test having to reach into Theme.
bool summaryReadsAsWaiting(const PageScene &scene)
{
    auto *summary = scene.item(QStringLiteral("captureChainSummary"));
    auto *sub = scene.item(QStringLiteral("captureChainSub_game"));
    return summary && sub && summary->property("color") == sub->property("color");
}

/// Review K2-1: what the page says while it knows nothing about Npcap.
void expectNothingClaimedAboutNpcap(const PageScene &scene, const QString &headline)
{
    QTRY_COMPARE(scene.text(QStringLiteral("captureHeroHeadline")), headline);
    QVERIFY(!scene.shows(QStringLiteral("captureNpcapPanel")));
    QVERIFY(!scene.shows(QStringLiteral("openNpcapButton")));
    QVERIFY(!scene.shows(QStringLiteral("redetectNpcapButton")));
    // The sidebar says 检查中 for the same state, and so does the page.
    QCOMPARE(scene.text(QStringLiteral("captureHeaderStatusText")), QString::fromUtf8("检查中"));
    QCOMPARE(scene.text(QStringLiteral("captureChainSummary")), headline);
    QVERIFY(summaryReadsAsWaiting(scene));
    // A field the Collector did not send is a dash, never a verdict.
    QCOMPARE(chainValue(scene, "npcap"), mr::Formatters::dash());
    QCOMPARE(chainSub(scene, "npcap"), QString::fromUtf8("尚未读取"));
    const QString page = scene.visibleTexts().join(QLatin1Char('\n'));
    QVERIFY2(!page.contains(QString::fromUtf8("未安装")), qPrintable(page));
    QVERIFY2(!page.contains(QString::fromUtf8("驱动缺失")), qPrintable(page));
    QVERIFY2(!page.contains(QString::fromUtf8("需手动记录")), qPrintable(page));
    QVERIFY2(!page.contains(QStringLiteral("npcap.com")), qPrintable(page));
}

} // namespace

namespace {
class CapturePageTests : public QObject
{
    Q_OBJECT

private slots:
    void initTestCase()
    {
        // The shipping QML straight from the source tree, as the other page suites load it.
        const QString qmlRoot = QString::fromUtf8(MR_DESKTOP_QML_DIR);
        QVERIFY(QDir(qmlRoot).exists());
        qmlRegisterSingletonType(QUrl::fromLocalFile(qmlRoot + QStringLiteral("/Theme.qml")),
                                 "MentorRecorder", 1, 0, "Theme");
        for (const auto &directory : {QStringLiteral("/components"), QStringLiteral("/pages")}) {
            for (const auto &file :
                 QDir(qmlRoot + directory).entryList({QStringLiteral("*.qml")}, QDir::Files)) {
                const QByteArray name = file.chopped(4).toUtf8();
                qmlRegisterType(QUrl::fromLocalFile(qmlRoot + directory + QLatin1Char('/') + file),
                                "MentorRecorder", 1, 0, name.constData());
            }
        }
    }

    // -- 标题行 -------------------------------------------------------------

    void playersReadTheListeningStateInsteadOfAButton()
    {
        PageScene scene;
        scene.mock->setRecordingFixture(QStringLiteral("listening"));
        QVERIFY(scene.open());
        QTRY_COMPARE(scene.app->recording()->state(), QStringLiteral("listening"));
        QVERIFY(scene.shows(QStringLiteral("captureHeaderStatus")));
        QTRY_COMPARE(scene.text(QStringLiteral("captureHeaderStatusText")), QString::fromUtf8("自动监听中"));
        QVERIFY(!scene.shows(QStringLiteral("captureHeaderAction")));
        QVERIFY(!scene.shows(QStringLiteral("captureMaintainerSection")));
        QVERIFY(!scene.shows(QStringLiteral("validationEvidenceCard")));
        QVERIFY(!scene.shows(QStringLiteral("candidateValidationCard")));
        QVERIFY(!scene.shows(QStringLiteral("liveCaptureStatusBox")));
        QVERIFY(!scene.shows(QStringLiteral("offlineReplayRow")));
        QVERIFY(scene.shows(QStringLiteral("exportDiagnosticsButton")));
    }

    void playersWaitingForTheGameAreToldSo()
    {
        PageScene scene;
        scene.mock->setRecordingFixture(QStringLiteral("waiting"));
        QVERIFY(scene.open());
        QTRY_COMPARE(scene.text(QStringLiteral("captureHeaderStatusText")), QString::fromUtf8("等待游戏启动"));
        QVERIFY(!scene.shows(QStringLiteral("captureHeaderAction")));
    }

    void unknownRegionOffersOrdinarySettings_data()
    {
        QTest::addColumn<bool>("running");
        QTest::addColumn<QString>("region");
        QTest::addColumn<bool>("readable");
        QTest::addColumn<bool>("expected");
        QTest::newRow("unknown running region") << true << QStringLiteral("UNKNOWN") << true << true;
        QTest::newRow("closed game") << false << QStringLiteral("UNKNOWN") << true << false;
        QTest::newRow("known CN") << true << QStringLiteral("CN") << true << false;
        QTest::newRow("known GLOBAL") << true << QStringLiteral("GLOBAL") << true << false;
        QTest::newRow("unreadable installation") << true << QStringLiteral("UNKNOWN") << false << false;
    }

    void unknownRegionOffersOrdinarySettings()
    {
        QFETCH(bool, running);
        QFETCH(QString, region);
        QFETCH(bool, readable);
        QFETCH(bool, expected);
        auto fake = std::make_unique<CaptureBackend>();
        auto *backend = fake.get();
        fake->capture = {{QStringLiteral("state"), QStringLiteral("STOPPED")},
                         {QStringLiteral("ffxiv_running"), running},
                         {QStringLiteral("npcap_installed"), true},
                         {QStringLiteral("profile_status"), QStringLiteral("NONE")},
                         {QStringLiteral("region"), region},
                         {QStringLiteral("game_build"), QStringLiteral("2026.10.20.0000.0000")},
                         {QStringLiteral("install_path_readable"), readable}};
        PageScene scene;
        scene.other = std::move(fake);
        QVERIFY(scene.open());
        QTRY_COMPARE(scene.app->ffxivRunning(), running);
        QTRY_COMPARE(scene.shows(QStringLiteral("openRegionSettingsButton")), expected);
        if (!expected)
            return;

        QCOMPARE(scene.text(QStringLiteral("protocolProfileHeadline")), QString::fromUtf8("尚未识别游戏区服"));
        const auto visible = scene.visibleTexts().join(QLatin1Char('\n'));
        QVERIFY(visible.contains(QString::fromUtf8("未识别区服")));
        QVERIFY(!visible.contains(QString::fromUtf8("未识别版本")));
        QVERIFY(scene.text(QStringLiteral("protocolProfileExplanation")).contains(QString::fromUtf8("设置 → 通用")));
        QSignalSpy requested(scene.page, SIGNAL(openRegionSettingsRequested()));
        QVERIFY(requested.isValid());
        auto *button = scene.item(QStringLiteral("openRegionSettingsButton"));
        QVERIFY(button && button->isEnabled());
        QVERIFY(QMetaObject::invokeMethod(button, "clicked"));
        QCOMPARE(requested.count(), 1);
        QVERIFY(!scene.page->property("observationGuideExpanded").toBool());
        QCOMPARE(backend->counts.value(QStringLiteral("StartCaptureValidation")), 0);
        QCOMPARE(backend->counts.value(QStringLiteral("StartCapture")), 0);
        const auto evidence = qEnvironmentVariable("MR_CAPTURE_RECOVERY_EVIDENCE_DIR");
        if (!evidence.isEmpty()) {
            QVERIFY(QDir().mkpath(evidence));
            auto *window = qobject_cast<QQuickWindow *>(scene.root.get());
            QVERIFY(window);
            for (const bool dark : {true, false}) {
                if (scene.app->isDark() != dark) scene.app->toggleTheme();
                QTest::qWait(150);
                QVERIFY(window->grabWindow().save(QDir(evidence).filePath(
                    QStringLiteral("capture-region-unknown-%1.png")
                        .arg(dark ? QStringLiteral("dark") : QStringLiteral("light")))));
            }
        }
    }

    void maintainersKeepTheCaptureButtonAndTheirTools()
    {
        PageScene scene;
        scene.mock->setRecordingFixture(QStringLiteral("listening"));
        QVERIFY(scene.open(true));
        QTRY_VERIFY(scene.shows(QStringLiteral("captureHeaderAction")));
        QTRY_COMPARE(scene.text(QStringLiteral("captureHeaderAction")), scene.app->captureActionLabel());
        QVERIFY(!scene.app->captureActionLabel().isEmpty());
        QVERIFY(!scene.shows(QStringLiteral("captureHeaderStatus")));
        QVERIFY(scene.shows(QStringLiteral("captureMaintainerSection")));
        QVERIFY(scene.shows(QStringLiteral("validationEvidenceCard")));
        QVERIFY(scene.shows(QStringLiteral("candidateValidationCard")));
        QVERIFY(scene.shows(QStringLiteral("captureAdapterTable")));
        QVERIFY(scene.shows(QStringLiteral("captureMetricGrid")));
        QVERIFY(scene.shows(QStringLiteral("liveCaptureStatusBox")));
        QVERIFY(scene.shows(QStringLiteral("offlineReplayRow")));
        QVERIFY(scene.shows(QStringLiteral("validationMarker_pop")));
    }

    // -- 链路 ---------------------------------------------------------------

    void theChainIsWholeWhenEverythingIsUp()
    {
        PageScene scene;
        scene.mock->setRecordingFixture(QStringLiteral("listening"));
        QVERIFY(scene.open());
        QTRY_COMPARE(scene.app->recording()->state(), QStringLiteral("listening"));
        QTRY_COMPARE(scene.text(QStringLiteral("captureChainSummary")),
                     QString::fromUtf8("游戏、驱动、网卡和版本支持均已就绪"));
        QCOMPARE(chainValue(scene, "game"), QStringLiteral("ffxiv_dx11.exe"));
        QCOMPARE(chainSub(scene, "game"), QStringLiteral("PID 18244"));
        QCOMPARE(chainValue(scene, "npcap"), QStringLiteral("v1.79"));
        QCOMPARE(chainSub(scene, "npcap"), QString::fromUtf8("WinPcap 兼容模式"));
        QTRY_COMPARE(chainValue(scene, "adapter"), QStringLiteral("Ethernet"));
        QCOMPARE(chainSub(scene, "adapter"), QString::fromUtf8("自动选择（有 FF14 连接）"));
        // A player never reads the profile id; the sidebar's words instead.
        QCOMPARE(chainValue(scene, "profile"), QString::fromUtf8("版本已支持"));
        QCOMPARE(chainSub(scene, "profile"), QString::fromUtf8("与游戏版本匹配"));
        QVERIFY(!scene.shows(QStringLiteral("captureNpcapPanel")));
        QVERIFY(!scene.shows(QStringLiteral("protocolProfileCard")));
        QCOMPARE(scene.text(QStringLiteral("captureHeroHeadline")), QString::fromUtf8("自动记录已开启，正在读取游戏数据"));
    }

    void maintainerSeesSelectionReasonWithoutAutomaticRecording_data()
    {
        QTest::addColumn<QString>("fixture");
        QTest::addColumn<QString>("expected");
        QTest::newRow("multiple") << QStringLiteral("multiple") << QStringLiteral("多个游戏客户端");
        QTest::newRow("exited") << QStringLiteral("multiple-exited") << QStringLiteral("所选游戏已退出");
    }

    void maintainerSeesSelectionReasonWithoutAutomaticRecording()
    {
        QFETCH(QString, fixture);
        QFETCH(QString, expected);
        PageScene scene;
        scene.mock->setRecordingFixture(fixture);
        QVERIFY(scene.open(true));
        QTRY_VERIFY(scene.app->gameSelection()->required());
        QTRY_VERIFY(scene.text(QStringLiteral("captureChainSummary")).contains(expected));
        QVERIFY(scene.visibleTexts().join(QLatin1Char('\n')).contains(expected));
        QVERIFY(!scene.app->recording()->message().contains(expected));
    }

    void multipleClientsWaitForChoiceAndTheSelectedClientReachesThePage()
    {
        PageScene scene;
        scene.mock->setRecordingFixture(QStringLiteral("multiple"));
        QVERIFY(scene.open());
        QTRY_COMPARE(scene.app->recording()->state(), QStringLiteral("choosing_game"));
        QVERIFY(scene.shows(QStringLiteral("gameSelectionPanel")));
        QCOMPARE(chainValue(scene, "game"), QString::fromUtf8("待选择"));
        QCOMPARE(chainSub(scene, "game"), QString::fromUtf8("检测到 2 个客户端"));
        QCOMPARE(chainValue(scene, "profile"), QString::fromUtf8("等待选择游戏"));
        scene.app->gameSelection()->select(1);
        QTRY_VERIFY(!scene.app->gameSelection()->required());
        QTRY_COMPARE(scene.app->captureStatus().value(QStringLiteral("ffxiv_process_id")).toInt(), 18245);
        QVERIFY(!scene.app->gameSelection()->currentLabel().isEmpty());
    }

    // 业主反馈（2026-10-05）：只有一个游戏在运行且已锁定时没有可选的对象，「记录对象」
    // 卡片不出现；列出第二个客户端时才出现，选定之后仍留着，以便更换记录对象。
    void theRecordingTargetCardIsHiddenWhileTheOnlyClientIsLocked()
    {
        PageScene scene;
        QVERIFY(scene.open());
        QTRY_COMPARE(scene.app->gameSelection()->choices().size(), 1);
        QVERIFY(!scene.app->gameSelection()->required());
        QVERIFY(!scene.app->gameSelection()->currentLabel().isEmpty());
        QVERIFY(!scene.shows(QStringLiteral("gameSelectionPanel")));
        const QString page = scene.visibleTexts().join(QLatin1Char('\n'));
        QVERIFY2(!page.contains(QString::fromUtf8("记录对象")), qPrintable(page));
        QVERIFY(!page.contains(QString::fromUtf8("点选游戏窗口")));
    }

    void theRecordingTargetCardStaysWhileSeveralClientsAreListed()
    {
        PageScene scene;
        scene.mock->setRecordingFixture(QStringLiteral("multiple"));
        QVERIFY(scene.open());
        QTRY_VERIFY(scene.app->gameSelection()->required());
        QVERIFY(scene.shows(QStringLiteral("gameSelectionPanel")));
        scene.app->gameSelection()->select(1);
        QTRY_VERIFY(!scene.app->gameSelection()->required());
        QTRY_VERIFY(!scene.app->gameSelection()->busy());
        QCOMPARE(scene.app->gameSelection()->choices().size(), 2);
        QVERIFY(scene.shows(QStringLiteral("gameSelectionPanel")));
        QVERIFY(scene.text(QStringLiteral("gameSelectionStatusText")).startsWith(QString::fromUtf8("已锁定：")));
    }

    // 只列出一个客户端、但采集服务仍要求选择（所选游戏已退出而另一个还开着）时，
    // 卡片照常出现：这时有事要用户决定。
    void theRecordingTargetCardAppearsWhenTheOnlyListedClientNeedsChoosing()
    {
        PageScene scene;
        scene.mock->setRecordingFixture(QStringLiteral("multiple-exited"));
        QVERIFY(scene.open());
        QTRY_VERIFY(scene.app->gameSelection()->required());
        QCOMPARE(scene.app->gameSelection()->choices().size(), 1);
        QVERIFY(scene.shows(QStringLiteral("gameSelectionPanel")));
    }

    // 审查 OD-1 / OK-2：唯一的客户端关掉之后（或几个都关掉之后），采集服务可能仍
    // 报 game_selection_required，但列表是空的。没有可选的游戏，页面就是普通的
    // 「等待游戏启动」：不出「记录对象」卡片，链路不变橙，档案不写「等待选择游戏」。
    void aClosedGameWithNothingToChooseIsOrdinaryWaiting_data()
    {
        QTest::addColumn<QString>("reason");
        QTest::newRow("only client closed") << QStringLiteral("EXITED");
        QTest::newRow("every client closed") << QStringLiteral("MULTIPLE");
    }

    void aClosedGameWithNothingToChooseIsOrdinaryWaiting()
    {
        QFETCH(QString, reason);
        auto fake = std::make_unique<CaptureBackend>();
        fake->capture = {{QStringLiteral("state"), QStringLiteral("STOPPED")},
                         {QStringLiteral("ffxiv_running"), false},
                         {QStringLiteral("ffxiv_process_id"), QJsonValue::Null},
                         {QStringLiteral("npcap_installed"), true},
                         {QStringLiteral("profile_status"), QStringLiteral("NONE")},
                         {QStringLiteral("region"), QStringLiteral("UNKNOWN")},
                         {QStringLiteral("game_selection_required"), true},
                         {QStringLiteral("game_selection_reason"), reason},
                         {QStringLiteral("game_processes"), QJsonArray()}};
        PageScene scene;
        scene.other = std::move(fake);
        QVERIFY(scene.open());
        QTRY_COMPARE(scene.app->recording()->state(), QStringLiteral("waiting"));
        QTRY_COMPARE(scene.text(QStringLiteral("captureHeaderStatusText")), QString::fromUtf8("等待游戏启动"));
        QTRY_COMPARE(scene.text(QStringLiteral("captureChainSummary")),
                     QString::fromUtf8("等待游戏启动 · 启动后会检查能否自动记录"));
        QVERIFY(summaryReadsAsWaiting(scene));
        QVERIFY(!scene.shows(QStringLiteral("gameSelectionPanel")));
        QCOMPARE(chainValue(scene, "game"), QString::fromUtf8("未运行"));
        QCOMPARE(chainValue(scene, "profile"), QString::fromUtf8("待游戏启动"));
        const QString page = scene.visibleTexts().join(QLatin1Char('\n'));
        QVERIFY2(!page.contains(QString::fromUtf8("所选游戏已退出")), qPrintable(page));
        QVERIFY(!page.contains(QString::fromUtf8("多个游戏客户端")));
        QVERIFY(!page.contains(QString::fromUtf8("等待选择游戏")));
    }

    void validationRestartWaitDoesNotTellTheUserToChangeClients()
    {
        PageScene scene;
        scene.mock->setRecordingFixture(QStringLiteral("multiple-exited"));
        scene.mock->setValidationFixture(QStringLiteral("WAITING"));
        QVERIFY(scene.open(true));
        QTRY_VERIFY(scene.app->validationActive());
        QTRY_COMPARE(scene.text(QStringLiteral("gameSelectionStatusText")), QStringLiteral("验证正在等待所选游戏重启"));
        QCOMPARE(scene.text(QStringLiteral("captureChainSummary")), QStringLiteral("验证正在等待所选游戏重启"));
        QVERIFY(!scene.item(QStringLiteral("pickGameWindowButton"))->isEnabled());
    }

    void theChainWaitsWhileTheGameIsNotRunning()
    {
        PageScene scene;
        scene.mock->setRecordingFixture(QStringLiteral("waiting"));
        QVERIFY(scene.open());
        QTRY_COMPARE(scene.text(QStringLiteral("captureChainSummary")),
                     QString::fromUtf8("等待游戏启动 · 启动后会检查能否自动记录"));
        QCOMPARE(chainValue(scene, "game"), QString::fromUtf8("未运行"));
        QCOMPARE(chainSub(scene, "game"), QString::fromUtf8("启动游戏后自动检测"));
        QCOMPARE(chainValue(scene, "npcap"), QStringLiteral("v1.79"));
        QCOMPARE(chainValue(scene, "profile"), QString::fromUtf8("待游戏启动"));
        QCOMPARE(chainSub(scene, "profile"), QString::fromUtf8("启动游戏后自动检查"));
        QVERIFY(!scene.shows(QStringLiteral("captureNpcapPanel")));
        // Nothing is wrong yet, so no 协议档案 notice either.
        QVERIFY(!scene.shows(QStringLiteral("protocolProfileCard")));
        QVERIFY(summaryReadsAsWaiting(scene));
        // No install directory has ever been remembered, so no version may be
        // named - not even a stale one.
        QVERIFY(!scene.visibleTexts().join(QLatin1Char('\n'))
                     .contains(QString::fromUtf8("已安装版本")));
    }

    // -- 游戏未运行，但已知已安装的版本 --------------------------------------

    void aClosedGameWithAUsableProfileIsReadyBeforeItStarts()
    {
        // The Collector remembers where the game is installed and reads the
        // version off disk, so 协议档案 is already answered for that version.
        PageScene scene;
        scene.mock->setRecordingFixture(QStringLiteral("waiting-verified"));
        QVERIFY(scene.open());
        QTRY_COMPARE(scene.text(QStringLiteral("captureChainSummary")),
                     QString::fromUtf8("等待游戏启动 · 已支持安装的游戏版本，启动后会自动记录"));
        // The chain is still not whole: nothing records until the game runs.
        QVERIFY(summaryReadsAsWaiting(scene));
        QCOMPARE(chainValue(scene, "game"), QString::fromUtf8("未运行"));
        QCOMPARE(chainSub(scene, "game"), QString::fromUtf8("已安装版本 2026.09.01"));
        QCOMPARE(chainValue(scene, "profile"), QString::fromUtf8("版本已支持"));
        QCOMPARE(chainSub(scene, "profile"), QString::fromUtf8("与已安装的游戏版本匹配"));
        // 待游戏启动后校验 was the answer only while the version was unknown.
        const QString page = scene.visibleTexts().join(QLatin1Char('\n'));
        QVERIFY(!page.contains(QString::fromUtf8("启动游戏后自动检查")));
        QVERIFY(!scene.shows(QStringLiteral("protocolProfileCard")));
        QVERIFY(!scene.shows(QStringLiteral("calibrationCard")));
    }

    void aClosedGameWithoutAProfileSaysSoWithoutSendingThePlayerIntoADuty()
    {
        PageScene scene;
        scene.mock->setRecordingFixture(QStringLiteral("waiting-calibrating"));
        QVERIFY(scene.open());
        QTRY_COMPARE(scene.text(QStringLiteral("captureChainSummary")),
                     QString::fromUtf8("等待游戏启动 · 当前安装的游戏版本需要重新校准"));
        QVERIFY(!summaryReadsAsWaiting(scene));
        QCOMPARE(chainValue(scene, "game"), QString::fromUtf8("未运行"));
        QCOMPARE(chainSub(scene, "game"), QString::fromUtf8("已安装版本 2026.09.01"));
        QCOMPARE(chainValue(scene, "profile"), QString::fromUtf8("待校准"));
        QCOMPARE(chainSub(scene, "profile"), QString::fromUtf8("启动游戏后重新校准"));
        // The hero card explains it over the calibration row, and while the
        // game is closed it never claims to be calibrating now.
        QTRY_VERIFY(scene.shows(QStringLiteral("calibrationCard")));
        const QString headline = scene.text(QStringLiteral("captureHeroHeadline"));
        QVERIFY2(headline.contains(QString::fromUtf8("启动游戏后")), qPrintable(headline));
        QVERIFY(!headline.contains(QString::fromUtf8("正在重新校准")));
        const QString page = scene.visibleTexts().join(QLatin1Char('\n'));
        QVERIFY(!page.contains(QString::fromUtf8("尚未识别游戏版本或区服")));
        QVERIFY(!page.contains(QString::fromUtf8("启动游戏后自动检查")));
    }

    void aMissingNpcapBlocksTheChainAndOpensTheDegradedPanel()
    {
        PageScene scene;
        scene.mock->setRecordingFixture(QStringLiteral("listening"));
        scene.mock->setNpcapMissing(true);
        QVERIFY(scene.open());
        QTRY_COMPARE(scene.text(QStringLiteral("captureChainSummary")),
                     QString::fromUtf8("Npcap 未安装 · 安装前只能手动记录"));
        QCOMPARE(chainValue(scene, "npcap"), QString::fromUtf8("未安装"));
        QCOMPARE(chainSub(scene, "npcap"), QString::fromUtf8("驱动缺失"));
        // 降级模式 is the hero card itself: the headline, the explanation, the
        // install steps and the two buttons.
        QVERIFY(scene.shows(QStringLiteral("captureNpcapPanel")));
        QCOMPARE(scene.text(QStringLiteral("captureHeroHeadline")), QString::fromUtf8("未安装 Npcap，自动记录已停用"));
        QVERIFY(scene.text(QStringLiteral("captureHeroText"))
                    .contains(QString::fromUtf8("请自行下载安装")));
        QCOMPARE(scene.text(QStringLiteral("captureHeaderStatusText")), QString::fromUtf8("需手动记录"));
        QVERIFY(scene.shows(QStringLiteral("openNpcapButton")));
        QVERIFY(scene.shows(QStringLiteral("redetectNpcapButton")));
    }

    // Review K2-1: with no status from the Collector - no pipe yet, a Collector that refused
    // to start or exited, a status read that failed - nothing is known about Npcap. The page
    // used to read the missing key as "not installed": the 降级模式 panel, 打开 npcap.com
    // and 需手动记录, sending the player to reinstall a driver for an unrelated problem.
    void aCollectorWithoutAPipeIsNotAMissingNpcap()
    {
        auto fake = std::make_unique<UnreadableBackend>();
        PageScene scene;
        scene.other = std::move(fake);
        QVERIFY(scene.open());
        QVERIFY(!scene.app->backendConnected());
        expectNothingClaimedAboutNpcap(scene, QString::fromUtf8("正在连接自动记录服务…"));
    }

    void aFailedStatusReadIsNotAMissingNpcap()
    {
        auto fake = std::make_unique<UnreadableBackend>();
        fake->connected = true;
        PageScene scene;
        scene.other = std::move(fake);
        QVERIFY(scene.open());
        QVERIFY(scene.app->backendConnected());
        expectNothingClaimedAboutNpcap(scene, QString::fromUtf8("正在获取自动记录状态…"));
    }

    void aCollectorThatGoesAwayLeavesNpcapUnknown()
    {
        auto fake = std::make_unique<UnreadableBackend>();
        fake->connected = true;
        fake->statusReadable = true;
        fake->capture = {{QStringLiteral("state"), QStringLiteral("RUNNING")},
                         {QStringLiteral("ffxiv_running"), true},
                         {QStringLiteral("npcap_installed"), true},
                         {QStringLiteral("npcap_version"), QStringLiteral("1.79")},
                         {QStringLiteral("profile_status"), QStringLiteral("VERIFIED")}};
        auto *source = fake.get();
        PageScene scene;
        scene.other = std::move(fake);
        QVERIFY(scene.open());
        QTRY_COMPARE(chainValue(scene, "npcap"), QStringLiteral("v1.79"));

        source->setConnected(false);
        QVERIFY(!scene.app->backendConnected());
        expectNothingClaimedAboutNpcap(scene, QString::fromUtf8("正在连接自动记录服务…"));
    }

    void anIncidentKeptAcrossALostConnectionStaysThePagesVerdict()
    {
        // The recording controller keeps an incident when the pipe goes (a disconnect is no
        // evidence of recovery), and the sidebar keeps saying 无法自动记录. The page says the
        // same - and still claims nothing about Npcap.
        auto fake = std::make_unique<UnreadableBackend>();
        fake->connected = true;
        fake->statusReadable = true;
        fake->capture = {{QStringLiteral("state"), QStringLiteral("RUNNING")},
                         {QStringLiteral("ffxiv_running"), true},
                         {QStringLiteral("ffxiv_process_id"), 18244},
                         {QStringLiteral("npcap_installed"), true},
                         {QStringLiteral("profile_status"), QStringLiteral("VERIFIED")},
                         {QStringLiteral("last_error_code"), QStringLiteral("ERR_DB_BUSY")}};
        auto *source = fake.get();
        PageScene scene;
        scene.other = std::move(fake);
        QVERIFY(scene.open());
        QTRY_COMPARE(scene.app->recording()->state(), QStringLiteral("blocked"));
        QTRY_COMPARE(scene.text(QStringLiteral("captureHeaderStatusText")), QString::fromUtf8("无法自动记录"));

        source->setConnected(false);
        QVERIFY(!scene.app->backendConnected());
        QTRY_COMPARE(scene.text(QStringLiteral("captureChainSummary")), QString::fromUtf8("正在连接自动记录服务…"));
        QCOMPARE(scene.app->recording()->state(), QStringLiteral("blocked"));
        QCOMPARE(scene.text(QStringLiteral("captureHeaderStatusText")), QString::fromUtf8("无法自动记录"));
        QCOMPARE(scene.text(QStringLiteral("captureHeroHeadline")), QString::fromUtf8("暂时不会自动记录"));
        QVERIFY(!scene.shows(QStringLiteral("captureNpcapPanel")));
        QCOMPARE(chainValue(scene, "npcap"), mr::Formatters::dash());
    }

    // OB8-D1：只读得到 Npcap 自带 libpcap 的版本时，采集服务报「libpcap 1.10.6」；页面在
    // 前面再加「v」就成了「vlibpcap 1.10.6」。只有以数字开头的版本号才加「v」。
    void aLibpcapVersionIsNotPrefixedWithAV()
    {
        for (const bool maintainer : {false, true}) {
            auto fake = std::make_unique<CaptureBackend>();
            fake->capture = {{QStringLiteral("state"), QStringLiteral("RUNNING")},
                             {QStringLiteral("ffxiv_running"), true},
                             {QStringLiteral("npcap_installed"), true},
                             {QStringLiteral("npcap_version"), QStringLiteral("libpcap 1.10.6")},
                             {QStringLiteral("profile_status"), QStringLiteral("VERIFIED")}};
            PageScene scene;
            scene.other = std::move(fake);
            QVERIFY(scene.open(maintainer));
            QTRY_COMPARE(chainValue(scene, "npcap"), QStringLiteral("libpcap 1.10.6"));
            if (maintainer)
                QTRY_VERIFY(scene.shows(QStringLiteral("captureMetricGrid")));
            const QString page = scene.visibleTexts().join(QLatin1Char('\n'));
            QVERIFY2(!page.contains(QStringLiteral("vlibpcap")), qPrintable(page));
        }
    }

    // -- 采证步骤 (帮助适配其他区服) ------------------------------------------

    // Review K2-4: the guided card a player opens from 帮助适配其他区服 showed a raw 错误码, the
    // opaque adapter id in every adapter label, a 64-digit SHA-256, and claimed 当前正在正式记录
    // while the capture was only listening on a build it cannot record.
    void theObservationGuideSpeaksToAPlayer_data()
    {
        QTest::addColumn<bool>("maintainer");
        QTest::newRow("player") << false;
        QTest::newRow("maintainer") << true;
    }

    void theObservationGuideSpeaksToAPlayer()
    {
        QFETCH(bool, maintainer);
        const QString adapterId = QStringLiteral("\\Device\\NPF_{0B7E3C1A-5D2F-4E6B-9A10-2C4D6E8F0A1B}");
        const QString sha = QStringLiteral("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08");
        auto fake = std::make_unique<CaptureBackend>();
        fake->capture = {{QStringLiteral("state"), QStringLiteral("RUNNING")},
                         {QStringLiteral("ffxiv_running"), true},
                         {QStringLiteral("ffxiv_process_id"), 18244},
                         {QStringLiteral("npcap_installed"), true},
                         {QStringLiteral("profile_status"), QStringLiteral("UNSUPPORTED_BUILD")},
                         {QStringLiteral("region"), QStringLiteral("GLOBAL")},
                         {QStringLiteral("game_build"), QStringLiteral("2026.09.01.0000.0000")}};
        fake->validation = {{QStringLiteral("active"), false},
                            {QStringLiteral("state"), QStringLiteral("FAILED")},
                            {QStringLiteral("reason"), QStringLiteral("START_FAILED")},
                            {QStringLiteral("message"), QString()},
                            {QStringLiteral("error_code"), QStringLiteral("ERR_CAPTURE_START_FAILED")},
                            {QStringLiteral("trace_path"), QStringLiteral("D:/MentorRecorder/data/validation/trace.jsonl")},
                            {QStringLiteral("sha256_path"), QStringLiteral("D:/MentorRecorder/data/validation/trace.jsonl.sha256")},
                            {QStringLiteral("sha256"), sha}};
        fake->adapters = {{QStringLiteral("npcap_installed"), true},
                          {QStringLiteral("adapters"), QJsonArray{QJsonObject{
                               {QStringLiteral("adapter_id"), adapterId},
                               {QStringLiteral("description"), QStringLiteral("Intel(R) Ethernet Controller")},
                               {QStringLiteral("friendly_name"), QString::fromUtf8("以太网")},
                               {QStringLiteral("is_loopback"), false},
                               {QStringLiteral("is_up"), true},
                               {QStringLiteral("recommended"), true}}}}};
        auto *source = fake.get();
        PageScene scene;
        scene.other = std::move(fake);
        QVERIFY(scene.open(maintainer));
        QTRY_VERIFY(scene.app->capturing());
        QTRY_COMPARE(scene.app->validationState(), QStringLiteral("FAILED"));
        QTRY_COMPARE(scene.app->captureAdapters().size(), 1);
        scene.page->setProperty("observationGuideExpanded", true);
        auto *card = scene.item(QStringLiteral("templateObservationGuideCard"));
        QVERIFY(card);
        QTRY_VERIFY(card->isVisible());

        QStringList texts;
        collectVisibleText(card, texts);
        for (const QVariant &choice : card->property("adapterChoices").toList())
            texts.append(choice.toMap().value(QStringLiteral("label")).toString());
        const QString all = texts.join(QLatin1Char('\n'));
        static const QRegularExpression hash(QStringLiteral("[0-9A-Fa-f]{64}"));
        // The capture listens, but this build is not recorded: nothing may say it is.
        QVERIFY2(!all.contains(QString::fromUtf8("正在正式记录")), qPrintable(all));
        if (maintainer) {
            // A maintainer keeps every detail of the trace and the adapter.
            QVERIFY2(all.contains(QStringLiteral("ERR_CAPTURE_START_FAILED")), qPrintable(all));
            QVERIFY2(all.contains(adapterId), qPrintable(all));
            QVERIFY2(all.contains(sha), qPrintable(all));
        } else {
            QVERIFY2(!all.contains(QStringLiteral("ERR_")), qPrintable(all));
            QVERIFY2(!all.contains(QStringLiteral("NPF_")), qPrintable(all));
            QVERIFY2(!hash.match(all).hasMatch(), qPrintable(all));
            QVERIFY2(!all.contains(QStringLiteral("SHA256")), qPrintable(all));
            // The Collector sent no sentence, so the player reads the generic one, never the code.
            QVERIFY2(all.contains(QString::fromUtf8("操作没有完成，原因不明。")), qPrintable(all));
            QVERIFY2(all.contains(QString::fromUtf8("以太网")), qPrintable(all));
        }

        // On a build that does record, the sentence is true again.
        QJsonObject verified = source->capture;
        verified.insert(QStringLiteral("profile_status"), QStringLiteral("VERIFIED"));
        source->capture = verified;
        scene.app->refreshStatus();
        QTRY_COMPARE(scene.app->protocolProfileStatus(), QStringLiteral("VERIFIED"));
        QTRY_VERIFY(scene.visibleTexts().join(QLatin1Char('\n'))
                        .contains(QString::fromUtf8("当前正在正式记录；以下为最近一次验证取证证据。")));
    }

    // -- 解析 ---------------------------------------------------------------

    void theLastValidEventNamesAKnownKind()
    {
        PageScene scene;
        scene.mock->setRecordingFixture(QStringLiteral("listening"));
        QVERIFY(scene.open());
        QTRY_VERIFY(scene.app->captureCounters().contains(QStringLiteral("last_valid_event_at_utc")));
        const QVariantMap counters = scene.app->captureCounters();
        QCOMPARE(counters.value(QStringLiteral("last_valid_event_kind")).toString(), QStringLiteral("DUTY_RESULT"));
        const QString expected = mr::Formatters::localTime(counters.value(QStringLiteral("last_valid_event_at_utc")))
                                 + QString::fromUtf8(" 副本结算");
        QTRY_COMPARE(scene.text(QStringLiteral("captureParseValue_lastEvent")), expected);
        QVERIFY(scene.shows(QStringLiteral("captureParseValue_lastEvent")));
        QVERIFY(!scene.shows(QStringLiteral("captureParseUnavailable")));
        QCOMPARE(scene.text(QStringLiteral("captureParseSuccessRate")), mr::Formatters::percent(12879.0 / (12879 + 52)));
        QCOMPARE(scene.text(QStringLiteral("captureParseValue_failures")), QStringLiteral("52 · 17"));
        QCOMPARE(scene.text(QStringLiteral("captureParseValue_rate")), QStringLiteral("38.2 / s"));
    }

    void anUnknownEventKindLeavesTheTimeAlone()
    {
        auto fake = std::make_unique<CaptureBackend>();
        const QString at = QStringLiteral("2026-09-17T13:38:04.000Z");
        fake->capture = {{QStringLiteral("state"), QStringLiteral("RUNNING")},
                         {QStringLiteral("ffxiv_running"), true},
                         {QStringLiteral("npcap_installed"), true},
                         {QStringLiteral("profile_status"), QStringLiteral("VERIFIED")},
                         {QStringLiteral("parse_ok_count"), 10},
                         {QStringLiteral("parse_fail_count"), 0},
                         {QStringLiteral("duplicate_count"), 0},
                         {QStringLiteral("last_valid_event_at_utc"), at},
                         {QStringLiteral("last_valid_event_kind"), QStringLiteral("BRAND_NEW_KIND")}};
        PageScene scene;
        scene.other = std::move(fake);
        QVERIFY(scene.open());
        QTRY_COMPARE(scene.text(QStringLiteral("captureParseValue_lastEvent")), mr::Formatters::localTime(at));
        QVERIFY(!scene.text(QStringLiteral("captureParseValue_lastEvent")).contains(QStringLiteral("BRAND")));
        // No rate reported, no number: an honest dash.
        QCOMPARE(scene.text(QStringLiteral("captureParseValue_rate")), mr::Formatters::dash());
    }

    void withoutParseCountersThePanelSaysSo()
    {
        auto fake = std::make_unique<CaptureBackend>();
        fake->capture = {{QStringLiteral("state"), QStringLiteral("RUNNING")},
                         {QStringLiteral("ffxiv_running"), true}};
        PageScene scene;
        scene.other = std::move(fake);
        QVERIFY(scene.open());
        QTRY_VERIFY(scene.shows(QStringLiteral("captureParseUnavailable")));
        QVERIFY(!scene.shows(QStringLiteral("captureParseSuccessRate")));
        QVERIFY(!scene.shows(QStringLiteral("captureParseValue_lastEvent")));
    }

    // -- vocabulary -----------------------------------------------------------

    // A capture error outranks a finished calibration, as it does in
    // AutomaticRecordingController::project(): the badge and the hero say
    // 无法自动记录, the calibration row says its own sentence, and the error code
    // this build has no wording for never reaches the page.
    void aCaptureErrorOutranksAFinishedCalibrationAndNamesNoCode()
    {
        auto fake = std::make_unique<CaptureBackend>();
        fake->capture = {{QStringLiteral("state"), QStringLiteral("RUNNING")},
                         {QStringLiteral("ffxiv_running"), true},
                         {QStringLiteral("ffxiv_process_id"), 18244},
                         {QStringLiteral("npcap_installed"), true},
                         {QStringLiteral("profile_status"), QStringLiteral("VERIFIED")},
                         {QStringLiteral("profile_origin"), QStringLiteral("LOCAL_CALIBRATION")},
                         {QStringLiteral("region"), QStringLiteral("CN")},
                         {QStringLiteral("game_build"), QStringLiteral("2026.09.01.0000.0000")},
                         {QStringLiteral("last_error_code"), QStringLiteral("ERR_BRAND_NEW_CODE")},
                         {QStringLiteral("calibration"), QJsonObject{
                              {QStringLiteral("state"), QStringLiteral("DONE")},
                              {QStringLiteral("game_build"), QStringLiteral("2026.09.01.0000.0000")},
                              {QStringLiteral("blockers"), QJsonArray()},
                              {QStringLiteral("events"), QJsonArray()},
                              {QStringLiteral("progress"), QJsonObject()}}}};
        PageScene scene;
        scene.other = std::move(fake);
        QVERIFY(scene.open());
        QTRY_COMPARE(scene.app->recording()->state(), QStringLiteral("blocked"));
        QTRY_COMPARE(scene.text(QStringLiteral("captureHeaderStatusText")), QString::fromUtf8("无法自动记录"));
        QCOMPARE(scene.text(QStringLiteral("captureHeroHeadline")), QString::fromUtf8("暂时不会自动记录"));
        const QString text = scene.text(QStringLiteral("captureHeroText"));
        QVERIFY2(!text.contains(QStringLiteral("ERR_")), qPrintable(text));
        QVERIFY2(!text.contains(QString::fromUtf8("查看诊断")), qPrintable(text));
        QVERIFY2(text.endsWith(QString::fromUtf8("。")), qPrintable(text));
        QVERIFY(scene.shows(QStringLiteral("calibrationCard")));
        QVERIFY(scene.shows(QStringLiteral("calibrationHeadline")));
    }

    void aPlayersPageCarriesNoWireVocabulary()
    {
        PageScene scene;
        scene.mock->setRecordingFixture(QStringLiteral("listening"));
        QVERIFY(scene.open());
        QTRY_VERIFY(!scene.app->parserErrors().isEmpty());
        QTRY_VERIFY(scene.item(QStringLiteral("captureParseFailureText")));
        const QStringList texts = scene.visibleTexts();
        QVERIFY(texts.contains(QString::fromUtf8("游戏数据的格式不符合预期，已跳过")));
        static const QStringList words{
            QString::fromUtf8("开始捕获"), QString::fromUtf8("停止捕获"), QString::fromUtf8("开始验证"),
            QString::fromUtf8("维护者工具"), QString::fromUtf8("对照核对"), QStringLiteral("opcode"),
            QStringLiteral("0x"), QStringLiteral("ERR_"), QStringLiteral("VERIFIED"),
            QStringLiteral("S2C"), QStringLiteral("profile"), QStringLiteral("cn/2026"),
            QStringLiteral("LIVE_CAPTURE_STATUS"), QStringLiteral("ExportDiagnosticsReport")};
        // Review OK-8: the refusal code (E_LEN_MISMATCH …) is a token like any other.
        // A player reads the sentence; the code column is maintainer material.
        static const QRegularExpression refusalCode(QStringLiteral("\\bE_[A-Z_]+"));
        for (const QString &text : texts) {
            QVERIFY2(!refusalCode.match(text).hasMatch(),
                     qPrintable(QStringLiteral("player page leaks a refusal code: %1").arg(text)));
            for (const QString &word : words) {
                QVERIFY2(!text.contains(word, Qt::CaseInsensitive),
                         qPrintable(QStringLiteral("player page leaks \"%1\": %2").arg(word, text)));
            }
        }
    }

    void maintainersReadTheRawRefusal()
    {
        PageScene scene;
        scene.mock->setRecordingFixture(QStringLiteral("listening"));
        QVERIFY(scene.open(true));
        QTRY_VERIFY(!scene.app->parserErrors().isEmpty());
        QTRY_VERIFY(scene.visibleTexts().join(QLatin1Char('\n')).contains(QStringLiteral("S2C 0x01A3")));
        QVERIFY(scene.visibleTexts().join(QLatin1Char('\n')).contains(QStringLiteral("LIVE_CAPTURE_STATUS")));
        // The refusal code, in its mono column.
        QVERIFY(scene.visibleTexts().contains(QStringLiteral("E_LEN_MISMATCH")));
    }
};
} // namespace

int main(int argc, char **argv)
{
    // Never run against the user's own Collector, database or serve lease - see
    // TestCollectorGuard.h. Must precede any construction.
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication app(argc, argv);
#ifdef Q_OS_WIN
    // 与实际截图入口一致：offscreen 平台不会自动加载 Windows 的中文字体。
    const int cjkFont = QFontDatabase::addApplicationFont(QStringLiteral("C:/Windows/Fonts/msyh.ttc"));
    QFontDatabase::addApplicationFont(QStringLiteral("C:/Windows/Fonts/consola.ttf"));
    if (cjkFont < 0 && !qEnvironmentVariableIsEmpty("MR_CAPTURE_RECOVERY_EVIDENCE_DIR")) {
        qCritical("Cannot load the CJK font required for capture-page evidence.");
        return 6;
    }
    QFont font;
    font.setFamilies({QStringLiteral("Microsoft YaHei UI"), QStringLiteral("Microsoft YaHei"),
                      QStringLiteral("Segoe UI")});
    QGuiApplication::setFont(font);
#endif
    CapturePageTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "CapturePageTests.moc"
