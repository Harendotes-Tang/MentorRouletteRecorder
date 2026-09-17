// ---------------------------------------------------------------------------
// tst_capturepage - the 捕获诊断 page in shipping QML
// (docs/ui-design.md §4.4).
//
// What it pins:
//   * the title row: a player reads the listening state in words and never a
//     start / stop button; a maintainer gets the button and the 维护者工具 block;
//   * 链路: the summary line and the four columns when everything is up, while
//     the game is not running, and while Npcap is missing (with the 降级模式 panel);
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
#include <QJsonArray>
#include <QJsonObject>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickStyle>
#include <QRegularExpression>
#include <QStandardPaths>
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

    QString backendName() const override { return QStringLiteral("mock"); }
    bool isConnected() const override { return true; }

    mr::BackendReply *request(const QString &type, const QJsonObject & = {}) override
    {
        auto *reply = new mr::BackendReply(type, type, this);
        if (type == QLatin1String("GetStatus"))
            reply->succeed({{QStringLiteral("capture"), capture}});
        else if (type == QLatin1String("GetCaptureStatus"))
            reply->succeed(capture);
        else
            reply->succeed({});
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

} // namespace

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
                     QString::fromUtf8("FF14 → Npcap → 适配器 → 协议档案 全部就绪"));
        QCOMPARE(chainValue(scene, "game"), QStringLiteral("ffxiv_dx11.exe"));
        QCOMPARE(chainSub(scene, "game"), QStringLiteral("PID 18244"));
        QCOMPARE(chainValue(scene, "npcap"), QStringLiteral("v1.79"));
        QCOMPARE(chainSub(scene, "npcap"), QString::fromUtf8("WinPcap 兼容模式"));
        QTRY_COMPARE(chainValue(scene, "adapter"), QStringLiteral("Ethernet"));
        QCOMPARE(chainSub(scene, "adapter"), QString::fromUtf8("自动选择（有 FF14 连接）"));
        // A player never reads the profile id; the sidebar's words instead.
        QCOMPARE(chainValue(scene, "profile"), QString::fromUtf8("档案匹配"));
        QCOMPARE(chainSub(scene, "profile"), QString::fromUtf8("与游戏版本匹配"));
        QVERIFY(!scene.shows(QStringLiteral("captureNpcapPanel")));
        QVERIFY(!scene.shows(QStringLiteral("protocolProfileCard")));
        QVERIFY(!scene.shows(QStringLiteral("captureSilentNotice")));
    }

    void theChainWaitsWhileTheGameIsNotRunning()
    {
        PageScene scene;
        scene.mock->setRecordingFixture(QStringLiteral("waiting"));
        QVERIFY(scene.open());
        QTRY_COMPARE(scene.text(QStringLiteral("captureChainSummary")),
                     QString::fromUtf8("等待游戏启动 · 档案按版本匹配，游戏启动后才知道能否记录"));
        QCOMPARE(chainValue(scene, "game"), QString::fromUtf8("未运行"));
        QCOMPARE(chainSub(scene, "game"), QString::fromUtf8("启动游戏后自动检测"));
        QCOMPARE(chainValue(scene, "npcap"), QStringLiteral("v1.79"));
        QCOMPARE(chainValue(scene, "profile"), QString::fromUtf8("待游戏启动"));
        QCOMPARE(chainSub(scene, "profile"), QString::fromUtf8("待游戏启动后校验"));
        QVERIFY(!scene.shows(QStringLiteral("captureNpcapPanel")));
        // Nothing is wrong yet, so no 协议档案 notice either.
        QVERIFY(!scene.shows(QStringLiteral("protocolProfileCard")));
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
        QVERIFY(scene.shows(QStringLiteral("captureNpcapPanel")));
        QCOMPARE(scene.text(QStringLiteral("captureNpcapHeadline")), QString::fromUtf8("未安装 Npcap"));
        QVERIFY(scene.text(QStringLiteral("captureNpcapExplanation"))
                    .contains(QString::fromUtf8("出于许可证限制需自行安装")));
        QVERIFY(scene.shows(QStringLiteral("openNpcapButton")));
        QVERIFY(scene.shows(QStringLiteral("redetectNpcapButton")));
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

    void aPlayersPageCarriesNoWireVocabulary()
    {
        PageScene scene;
        scene.mock->setRecordingFixture(QStringLiteral("listening"));
        QVERIFY(scene.open());
        QTRY_VERIFY(!scene.app->parserErrors().isEmpty());
        QTRY_VERIFY(scene.item(QStringLiteral("captureParseFailureText")));
        const QStringList texts = scene.visibleTexts();
        QVERIFY(texts.contains(QString::fromUtf8("报文长度与档案不符，已忽略")));
        static const QStringList words{
            QString::fromUtf8("开始捕获"), QString::fromUtf8("停止捕获"), QString::fromUtf8("开始验证"),
            QString::fromUtf8("维护者工具"), QString::fromUtf8("对照核对"), QStringLiteral("opcode"),
            QStringLiteral("0x"), QStringLiteral("ERR_"), QStringLiteral("VERIFIED"),
            QStringLiteral("S2C"), QStringLiteral("profile"), QStringLiteral("cn/2026"),
            QStringLiteral("LIVE_CAPTURE_STATUS"), QStringLiteral("ExportDiagnosticsReport")};
        // The refusal code itself is shown in mono (plan §4: 错误码用等宽字体);
        // E_UNKNOWN_OPCODE is a code, not an opcode, so the code cell is exempt.
        static const QRegularExpression refusalCode(QStringLiteral("^E_[A-Z_]+$"));
        for (const QString &text : texts) {
            if (refusalCode.match(text).hasMatch())
                continue;
            for (const QString &word : words) {
                QVERIFY2(!text.contains(word, Qt::CaseInsensitive),
                         qPrintable(QStringLiteral("player page leaks \"%1\": %2").arg(word, text)));
            }
        }
        QVERIFY(texts.contains(QStringLiteral("E_LEN_MISMATCH")));
    }

    void maintainersReadTheRawRefusal()
    {
        PageScene scene;
        scene.mock->setRecordingFixture(QStringLiteral("listening"));
        QVERIFY(scene.open(true));
        QTRY_VERIFY(!scene.app->parserErrors().isEmpty());
        QTRY_VERIFY(scene.visibleTexts().join(QLatin1Char('\n')).contains(QStringLiteral("S2C 0x01A3")));
        QVERIFY(scene.visibleTexts().join(QLatin1Char('\n')).contains(QStringLiteral("LIVE_CAPTURE_STATUS")));
    }
};

int main(int argc, char **argv)
{
    // Never run against the user's own Collector, database or serve lease - see
    // TestCollectorGuard.h. Must precede any construction.
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication app(argc, argv);
    CapturePageTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "CapturePageTests.moc"
