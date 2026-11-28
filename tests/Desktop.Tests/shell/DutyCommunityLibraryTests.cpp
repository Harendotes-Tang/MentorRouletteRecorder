// 副本提醒与导随宏's shared library tab: entries that read alike folded under the first one,
// the job filter, each entry's 👍 on GitHub, and the 收藏 star of the local tab.
//
// The scene is the statistics float's (StatisticsSceneFixture.h), opened on Main.qml.

#include "DesktopTestRunner.h"
#include "DutyCommunitySceneFixture.h"
#include "MockBackendTestSupport.h"
#include "TestCollectorGuard.h"
#include "BundledFonts.h"

#include <QApplication>
#include <QClipboard>
#include <QColor>
#include <QDesktopServices>
#include <QDir>
#include <QFont>
#include <QJsonArray>
#include <QQuickItem>
#include <QQuickStyle>
#include <QScopeGuard>
#include <QStandardPaths>
#include <QTest>

namespace {
class DutyCommunityLibraryTests : public QObject
{
    Q_OBJECT
private Q_SLOTS:
    void initTestCase();
    void communityPublicLibraryFoldsAndFilters();
    void sharedLibraryShowsLikesAndSortsByThem();
    void emptySnapshotMarksInstalledSourceRemoved();
    void reopeningKeepsTheActiveRefresh();
    void changedLocalPageIsReadAgainFromTheStart();
    void localPageAcceptsOnlyValidOffsets();
    void favoriteStarIsLucideAndFilledWhenChosen();
    void floatingNotesFollowTheEnteredDutyAndItsPhases();
    void floatingNotesEmptyStateOpensTheCommunityDialog();
    void floatingNotesInterleaveTrashAndBosses();
    void floatingNotesApplyAutomaticStageWithoutHidingStatistics();
public:
    Q_INVOKABLE void rememberUrl(const QUrl &url) { opened << url; }
    QList<QUrl> opened;
};

QString mockId(int number)
{
    return QStringLiteral("00000000-0000-4000-8000-%1").arg(number, 12, 10, QLatin1Char('0'));
}

QString bodyName(int number)
{
    return QStringLiteral("floatingDutyNotesBody_") + mockId(number);
}

QString phaseText(const Scene &scene)
{
    const auto *label = scene.item(QStringLiteral("floatingDutyNotesPhase"), true);
    return label ? label->property("text").toString() : QString();
}

QVariantMap overlayEntry(int number, const char *type, const char *body, int duty, const char *phase,
                         bool favorite = false, bool fromLibrary = false, const char *region = "CN")
{
    auto row = publicEntry(number, type, body, "", duty);
    auto content = row.value(QStringLiteral("content")).toMap();
    if (fromLibrary) row.insert(QStringLiteral("upstream"), content);
    content.insert(QStringLiteral("boss"), QString::fromUtf8(phase));
    content.insert(QStringLiteral("region"), QString::fromLatin1(region));
    if (number == 302) content.insert(QStringLiteral("title"), QStringLiteral("进本打招呼"));
    row.insert(QStringLiteral("content"), content);
    row.insert(QStringLiteral("favorite"), favorite);
    return row;
}

void refreshOverlayRun(Scene &scene, const char *state, int duty, int run, const char *phase = nullptr)
{
    scene.backend.currentRunOverride = {
        {QStringLiteral("state"), QString::fromLatin1(state)},
        {QStringLiteral("elapsed_ms"), 134000},
        {QStringLiteral("run"), QJsonObject{{QStringLiteral("run_id"), mockId(run)},
                                          {QStringLiteral("region"), QStringLiteral("CN")},
                                          {QStringLiteral("content_id"), duty},
                                          {QStringLiteral("duty_category"), QStringLiteral("四人迷宫")},
                                          {QStringLiteral("job_id"), 24},
                                          {QStringLiteral("entered_at_utc"), QStringLiteral("2026-11-28T00:01:00Z")}}}
    };
    if (phase)
        scene.backend.currentRunOverride.insert(QStringLiteral("duty_phase"), QString::fromLatin1(phase));
    scene.app->refreshAll();
}

/// The SVG an Image shows from a Lucide.js data: URL.
QString svgOf(const QQuickItem *image)
{
    const QString source = image->property("source").toUrl().toString();
    return QUrl::fromPercentEncoding(source.mid(source.indexOf(QLatin1Char(',')) + 1).toUtf8());
}

/// The visible items called `name` in the visual tree under `root` (list delegates are not
/// QObject children of the list), for rows the list's delegates repeat.
QList<QQuickItem *> visibleNamed(QQuickItem *root, const QString &name)
{
    QList<QQuickItem *> out;
    if (!root || !root->isVisible()) return out;
    if (root->objectName() == name) out << root;
    for (QQuickItem *child : root->childItems())
        out << visibleNamed(child, name);
    return out;
}
} // namespace

void DutyCommunityLibraryTests::initTestCase()
{
    QVERIFY(QDir().mkpath(QString::fromUtf8(MR_SOURCE_DIR) + kEvidence()));
    registerSceneQmlTypes();
}

void DutyCommunityLibraryTests::floatingNotesFollowTheEnteredDutyAndItsPhases()
{
    Scene scene;
    const auto macro = QStringLiteral("/p 初次见面，请多关照！<se.1>\n/p 第一次来的话可以先看动画。");
    const auto longBody = QStringLiteral("沿左侧走。\n等队友到齐。\n准备减伤。\n下一波怪先集火。\n看到范围就散开。");
    scene.backend.communityListOverride = QJsonArray::fromVariantList({
        overlayEntry(301, "reminder", "等转阶段后再开爆发。", 17, "P2"),
        overlayEntry(302, "macro", macro.toUtf8().constData(), 17, "opening", true, true),
        overlayEntry(303, "reminder", longBody.toUtf8().constData(), 17, "老二"),
        overlayEntry(304, "reminder", "留意前方扇形范围。", 17, "2ボス"),
        overlayEntry(305, "reminder", "随时留意队友的标记。", 17, "全程"),
        overlayEntry(306, "reminder", "沿路的宝箱可自行拾取。", 17, ""),
        overlayEntry(307, "reminder", "按需要调整站位。", 17, "场边提示"),
        overlayEntry(308, "reminder", "副本结束后可先确认掉落。", 0, "ending"),
        overlayEntry(309, "reminder", "Other duty", 4, "BOSS 1"),
        overlayEntry(310, "reminder", "Not starred", 17, "start", false, true),
        overlayEntry(311, "reminder", "Wrong region", 17, "start", false, false, "GLOBAL"),
        overlayEntry(312, "reminder", "开场记得确认队伍进度。", 17, "开场"),
        overlayEntry(313, "reminder", "也可以向队友确认机制。", 17, "toString")
    });
    scene.backend.setLiveMode(mr::MockBackend::LiveMode::None);
    QVERIFY2(scene.create(QStringLiteral("harendotes"), true, true), qPrintable(scene.errors));
    auto *dialog = scene.root->findChild<QObject *>(QStringLiteral("dutyCommunityDialog"));
    QVERIFY(dialog);
    QTRY_COMPARE(rowIds(dialog->property("localRows")).size(), 13);
    dialog->setProperty("recent", QVariantMap{{mockId(304), QStringLiteral("2026-11-28T01:30:00Z")}});

    const auto module = QStringLiteral("floatingDutyNotes");
    const auto next = QStringLiteral("floatingDutyNotesNext");
    const auto previous = QStringLiteral("floatingDutyNotesPrevious");
    const auto groups = [&] { return qmlList(scene.item(module)->property("groups")); };
    scene.overlay()->setGeometry(QRect(100, 80, 340, 680));
    scene.overlay()->setModules({"current_duty", "elapsed"});
    QVERIFY(scene.showFloating());
    QVERIFY(scene.click(QStringLiteral("floatingCustomizeButton")));
    QVERIFY(scene.click(QStringLiteral("floatingModuleToggle_duty_notes")));
    QVERIFY(scene.revealCustomizationFooter());
    QVERIFY(scene.click(QStringLiteral("floatingCustomizationDone")));
    scene.overlay()->moveModuleTo(QStringLiteral("duty_notes"), 2);
    QCOMPARE(scene.overlay()->modules(), QStringList({"current_duty", "elapsed", "duty_notes"}));
    QVERIFY(!scene.item(module, true));

    refreshOverlayRun(scene, "MENTOR_MATCHED", 17, 701);
    QTRY_COMPARE(scene.app->currentRunState(), QStringLiteral("MENTOR_MATCHED"));
    QVERIFY(!scene.item(module, true));
    refreshOverlayRun(scene, "ENTERED_DUTY", 17, 701);
    QTRY_COMPARE(phaseText(scene), QStringLiteral("开场"));
    QStringList order;
    for (const auto &group : groups()) order << group.toMap().value(QStringLiteral("key")).toString();
    QCOMPARE(order.mid(0, 4), QStringList({"p:open", "p:all", "p:b2", "p:p2"}));
    QVERIFY(order.mid(4, 2).contains(QStringLiteral("p:x:tostring")));
    QVERIFY(order.mid(4, 2).contains(QStringLiteral("p:x:场边提示")));
    QCOMPARE(order.mid(6), QStringList({"p:none", "gen"}));
    const auto rowsIn = [&](int index) {
        QStringList ids;
        const auto items = groups().at(index).toMap().value(QStringLiteral("items")).toList();
        for (const auto &item : items)
            ids << item.toMap().value(QStringLiteral("row")).toMap().value(QStringLiteral("content"))
                       .toMap().value(QStringLiteral("id")).toString();
        return ids;
    };
    QCOMPARE(rowsIn(0), QStringList({mockId(302), mockId(312)}));
    QCOMPARE(rowsIn(2), QStringList({mockId(304), mockId(303)}));
    const auto bossGroup = QStringLiteral("floatingDutyNotesGroup_p:b2");
    QVERIFY(!scene.item(bossGroup, true));
    QVERIFY(!scene.item(previous, true)->isEnabled());
    for (int id : {305, 306, 307, 308, 313}) QVERIFY(scene.item(bodyName(id), true));
    for (int id : {301, 303, 304, 309, 310, 311}) QVERIFY(!scene.item(bodyName(id), true));
    QVERIFY(scene.grab(QStringLiteral("duty-notes-entered")));

    auto *clipboard = QGuiApplication::clipboard();
    const QString savedText = clipboard->text();
    const auto restoreClipboard = qScopeGuard([&] { clipboard->setText(savedText); });
    QVERIFY(scene.click(QStringLiteral("floatingDutyNotesCopy_") + mockId(302)));
    QTRY_COMPARE(scene.backend.lastCommunityCopy.value(QStringLiteral("mode")).toString(), QStringLiteral("macro"));
    QCOMPARE(scene.backend.lastCommunityCopy.value(QStringLiteral("body")).toString(), macro);
    QTRY_COMPARE(clipboard->text().replace(QStringLiteral("\r\n"), QStringLiteral("\n")), macro);
    QTRY_VERIFY(qmlMap(dialog->property("recent")).contains(mockId(302)));

    QVERIFY(scene.click(next));
    QTRY_COMPARE(phaseText(scene), QStringLiteral("BOSS 2"));
    QVERIFY(scene.item(bossGroup, true));
    QVERIFY(!scene.item(QStringLiteral("floatingDutyNotesGroup_p:open"), true));
    QVERIFY(!scene.item(bodyName(302), true));
    const auto expand = QStringLiteral("floatingDutyNotesExpand_") + mockId(303);
    QTRY_VERIFY(scene.item(expand, true));
    auto *body = scene.item(bodyName(303), true);
    QVERIFY(body);
    QCOMPARE(body->property("lineCount").toInt(), 3);
    QVERIFY(scene.click(expand));
    QTRY_COMPARE(body->property("lineCount").toInt(), 5);
    scene.app->refreshAll();
    QTRY_COMPARE(phaseText(scene), QStringLiteral("BOSS 2"));
    QVERIFY(scene.click(next));
    QTRY_COMPARE(phaseText(scene), QStringLiteral("阶段 2"));
    QVERIFY(!scene.item(next, true)->isEnabled());
    for (int id : {305, 306, 307, 308}) QVERIFY(scene.item(bodyName(id), true));
    QVERIFY(scene.click(previous));
    QTRY_COMPARE(phaseText(scene), QStringLiteral("BOSS 2"));

    refreshOverlayRun(scene, "ENTERED_DUTY", 4, 701);
    QTRY_COMPARE(phaseText(scene), QStringLiteral("BOSS 1"));
    QVERIFY(!scene.item(bodyName(303), true));
    QVERIFY(scene.item(bodyName(309), true));
    refreshOverlayRun(scene, "ENTERED_DUTY", 17, 702);
    QTRY_COMPARE(phaseText(scene), QStringLiteral("开场"));
    QVERIFY(scene.click(next));
    scene.backend.currentRunOverride = {};
    scene.app->refreshAll();
    QTRY_VERIFY(!scene.item(module, true));
    refreshOverlayRun(scene, "ENTERED_DUTY", 17, 703);
    QTRY_COMPARE(phaseText(scene), QStringLiteral("开场"));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void DutyCommunityLibraryTests::floatingNotesEmptyStateOpensTheCommunityDialog()
{
    Scene scene;
    scene.backend.communityListOverride = QJsonArray{};
    scene.backend.setLiveMode(mr::MockBackend::LiveMode::Entered);
    QVERIFY2(scene.create(QStringLiteral("harendotes"), true, true), qPrintable(scene.errors));
    scene.overlay()->setModules({"duty_notes"});
    scene.overlay()->setGeometry(QRect(100, 80, 240, 500));
    QVERIFY(scene.showFloating());
    QTRY_VERIFY(scene.item(QStringLiteral("floatingDutyNotesEmpty"), true));
    QVERIFY(!scene.item(QStringLiteral("floatingDutyNotesPrevious"), true));
    QVERIFY(!scene.item(QStringLiteral("floatingDutyNotesNext"), true));
    const auto manage = QStringLiteral("floatingDutyNotesManage");
    const struct { const char *locale; const char *label; } languages[] = {
        {"zh-CN", "打开提醒与宏"},
        {"zh-TW", "開啟提醒與巨集"},
        {"en", "Open reminders & macros"},
        {"ja", "リマインダーとマクロを開く"}
    };
    const auto manageText = [&] {
        const auto *button = scene.item(manage, true);
        return button ? button->property("text").toString() : QString();
    };
    for (const auto &language : languages) {
        scene.language.setLanguage(QString::fromLatin1(language.locale));
        QTRY_COMPARE(manageText(), QString::fromUtf8(language.label));
        const auto *button = scene.item(manage, true);
        QTRY_VERIFY(button->width() > 0 && button->width() <= button->parentItem()->width());
    }
    auto *dialog = scene.root->findChild<QObject *>(QStringLiteral("dutyCommunityDialog"));
    QVERIFY(dialog);
    QVERIFY(!dialog->property("visible").toBool());
    scene.mainWindow()->hide();
    QVERIFY(scene.click(manage));
    QTRY_VERIFY(scene.mainWindow()->isVisible());
    QTRY_VERIFY(dialog->property("visible").toBool());
    QCOMPARE(dialog->property("activeScope").toString(), QStringLiteral("cur"));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void DutyCommunityLibraryTests::floatingNotesInterleaveTrashAndBosses()
{
    Scene scene;
    scene.backend.communityListOverride = QJsonArray::fromVariantList({
        overlayEntry(327, "reminder", "尾王读条时留意场地。", 17, "BOSS 3"),
        overlayEntry(324, "reminder", "先打会施法的小怪，留一次打断。", 17, "小怪二"),
        overlayEntry(323, "reminder", "站到侧面避开正面攻击。", 17, "BOSS 1"),
        overlayEntry(328, "macro", "/p 这段请把两组小怪拉到一起。", 17, "Trash 2"),
        overlayEntry(326, "reminder", "先清门前巡逻的那一只。", 17, "道中雑魚 3"),
        overlayEntry(321, "reminder", "沿途遇到小怪时注意队友位置。", 17, "道中"),
        overlayEntry(325, "reminder", "提前散开，留意头顶标记。", 17, "BOSS 2"),
        overlayEntry(322, "reminder", "第一组小怪沿左墙集中。", 17, "道中 1")
    });
    scene.backend.setLiveMode(mr::MockBackend::LiveMode::Entered);
    QVERIFY2(scene.create(QStringLiteral("harendotes"), true, true), qPrintable(scene.errors));
    scene.overlay()->setGeometry(QRect(100, 80, 340, 760));
    const auto originalModules = scene.overlay()->modules();
    const auto notes = QStringLiteral("duty_notes");
    scene.overlay()->toggleModule(notes);
    scene.overlay()->moveModuleTo(notes, originalModules.indexOf(QStringLiteral("elapsed")) + 1);
    auto enabled = scene.overlay()->modules();
    QVERIFY(enabled.removeOne(notes));
    QCOMPARE(enabled, originalModules);
    QVERIFY(scene.showFloating());
    QTRY_COMPARE(phaseText(scene), QStringLiteral("小怪"));

    const auto *dialog = scene.root->findChild<QObject *>(QStringLiteral("dutyCommunityDialog"));
    QVERIFY(dialog);
    QStringList headers;
    for (const auto &entry : qmlList(dialog->property("listEntries"))) {
        const auto line = entry.toMap();
        if (line.value(QStringLiteral("kind")).toString() == QStringLiteral("head"))
            headers << line.value(QStringLiteral("label")).toString();
    }
    const QStringList expected{"小怪", "小怪 1", "BOSS 1", "小怪 2", "BOSS 2", "小怪 3", "BOSS 3"};
    QCOMPARE(headers, expected);
    const auto next = QStringLiteral("floatingDutyNotesNext");
    const auto previous = QStringLiteral("floatingDutyNotesPrevious");
    for (qsizetype index = 0; index < expected.size(); ++index) {
        if (index > 0) QVERIFY(scene.click(next));
        QTRY_COMPARE(phaseText(scene), expected[index]);
        QVERIFY(scene.item(bodyName(321 + index), true));
        if (index > 0) QVERIFY(!scene.item(bodyName(320 + index), true));
        if (index == 3) {
            QVERIFY(scene.item(bodyName(328), true));
            scene.language.setLanguage(QStringLiteral("en"));
            QTRY_COMPARE(phaseText(scene), QStringLiteral("Trash 2"));
            scene.language.setLanguage(QStringLiteral("zh-CN"));
            QTRY_COMPARE(phaseText(scene), QStringLiteral("小怪 2"));
            for (const auto *module : {"attempts", "progress", "current_duty", "elapsed", "dungeons", "jobs"})
                QVERIFY(scene.item(QStringLiteral("floatingModule_") + QString::fromLatin1(module), true));
            QTest::mouseMove(scene.floating(), QPoint(8, 18));
            QVERIFY(scene.grab(QStringLiteral("duty-notes-interleaved")));
        } else {
            QVERIFY(!scene.item(bodyName(328), true));
        }
    }
    QVERIFY(!scene.item(next, true)->isEnabled());
    QVERIFY(scene.click(previous));
    QTRY_COMPARE(phaseText(scene), QStringLiteral("小怪 3"));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void DutyCommunityLibraryTests::floatingNotesApplyAutomaticStageWithoutHidingStatistics()
{
    Scene scene;
    scene.backend.communityListOverride = QJsonArray::fromVariantList({
        overlayEntry(481, "reminder", "第一段小怪。", 676, "小怪 1"),
        overlayEntry(482, "reminder", "第一个 BOSS。", 676, "BOSS 1"),
        overlayEntry(483, "reminder", "第二段小怪。", 676, "小怪 2"),
        overlayEntry(484, "macro", "/p 泰丝琳点名时散开。", 676, "BOSS 2"),
        overlayEntry(485, "reminder", "全程留意队友位置。", 676, "全程"),
        overlayEntry(486, "reminder", "未标阶段的提醒。", 676, ""),
        overlayEntry(487, "reminder", "道中通用提醒。", 676, "道中"),
        overlayEntry(488, "reminder", "开场提醒。", 676, "开场"),
        overlayEntry(489, "reminder", "尾王留意点名。", 676, "尾王"),
        overlayEntry(490, "macro", "/p 注意魔导炮艇的火炎放射。", 32, "BOSS 2"),
        overlayEntry(491, "reminder", "先打龙之幻象。", 32, "BOSS 3")
    });
    scene.backend.setLiveMode(mr::MockBackend::LiveMode::Entered);
    QVERIFY2(scene.create(QStringLiteral("harendotes"), true, true), qPrintable(scene.errors));
    scene.overlay()->setGeometry(QRect(100, 80, 340, 690));
    const auto notes = QStringLiteral("duty_notes");
    const auto originalModules = scene.overlay()->modules();
    scene.overlay()->toggleModule(notes);
    scene.overlay()->moveModuleTo(notes, static_cast<int>(originalModules.indexOf(QStringLiteral("elapsed")) + 1));
    auto enabled = scene.overlay()->modules();
    QVERIFY(enabled.removeOne(notes));
    QCOMPARE(enabled, originalModules);
    refreshOverlayRun(scene, "ENTERED_DUTY", 676, 801, "unknown");
    QVERIFY(scene.showFloating());

    const auto applyPhase = [&](const char *phase) {
        scene.backend.currentRunOverride.insert(QStringLiteral("duty_phase"), QString::fromLatin1(phase));
        Q_EMIT scene.backend.liveEvent({{QStringLiteral("event_type"), QStringLiteral("DiagnosticsMessage")},
                                      {QStringLiteral("kind"), QStringLiteral("run_phase_changed")}});
    };
    QTRY_COMPARE(phaseText(scene), QStringLiteral("当前阶段待识别"));
    QVERIFY(!scene.item(bodyName(481), true));
    QVERIFY(scene.item(bodyName(485), true));
    QVERIFY(scene.item(bodyName(486), true));
    QVERIFY(!scene.item(QStringLiteral("floatingDutyNotesPrevious"), true));
    QVERIFY(!scene.item(QStringLiteral("floatingDutyNotesNext"), true));

    applyPhase("trash1");
    QTRY_COMPARE(phaseText(scene), QStringLiteral("小怪 1"));
    QVERIFY(scene.item(bodyName(481), true));
    QVERIFY(scene.item(bodyName(487), true));
    QVERIFY(scene.item(bodyName(488), true));
    applyPhase("boss1");
    QTRY_COMPARE(phaseText(scene), QStringLiteral("BOSS 1 · 得到宽恕的失调"));
    QVERIFY(!scene.item(bodyName(481), true));
    QVERIFY(!scene.item(bodyName(487), true));
    QVERIFY(!scene.item(bodyName(488), true));
    QVERIFY(scene.item(bodyName(482), true));
    applyPhase("trash2");
    QTRY_COMPARE(phaseText(scene), QStringLiteral("小怪 2"));
    QVERIFY(scene.item(bodyName(483), true));
    QVERIFY(!scene.item(bodyName(482), true));
    applyPhase("boss2");
    QTRY_COMPARE(phaseText(scene), QStringLiteral("BOSS 2 · 得到宽恕的泰丝琳"));
    QVERIFY(scene.item(bodyName(484), true));
    QVERIFY(!scene.item(bodyName(483), true));
    QVERIFY(scene.item(bodyName(485), true));
    for (const auto *module : {"attempts", "progress", "current_duty", "elapsed", "dungeons", "jobs"})
        QVERIFY(scene.item(QStringLiteral("floatingModule_") + QString::fromLatin1(module), true));
    QTRY_VERIFY(scene.item(QStringLiteral("floatingCategorySegment"), true));
    auto *summary = scene.item(QStringLiteral("floatingDungeonState"), true);
    QVERIFY(summary);
    QTRY_COMPARE(summary->property("text").toString(), QStringLiteral("71/88"));
    QVERIFY(scene.textItemsContaining(QStringLiteral("沙斯塔夏溶洞")).isEmpty());
    QVERIFY(scene.textItemsContaining(QStringLiteral("铜铃铜山")).isEmpty());
    QTest::mouseMove(scene.floating(), QPoint(8, 18));
    QVERIFY(scene.grab(QStringLiteral("duty-notes-holminster-automatic")));

    scene.language.setLanguage(QStringLiteral("en"));
    QTRY_COMPARE(phaseText(scene), QStringLiteral("Boss 2 · Tesleen, the Forgiven"));
    scene.language.setLanguage(QStringLiteral("ja"));
    QTRY_COMPARE(phaseText(scene), QStringLiteral("ボス 2 · フォーギヴン・テスリーン"));
    scene.language.setLanguage(QStringLiteral("zh-TW"));
    QTRY_COMPARE(phaseText(scene), QStringLiteral("BOSS 2 · 得到寬恕的泰絲琳"));
    scene.language.setLanguage(QStringLiteral("zh-CN"));
    QTRY_COMPARE(phaseText(scene), QStringLiteral("BOSS 2 · 得到宽恕的泰丝琳"));

    applyPhase("trash3");
    QTRY_COMPARE(phaseText(scene), QStringLiteral("小怪 3"));
    QVERIFY(scene.item(bodyName(487), true));
    applyPhase("boss3");
    QTRY_COMPARE(phaseText(scene), QStringLiteral("BOSS 3 · 斐利亚"));
    QVERIFY(scene.item(bodyName(489), true));
    QVERIFY(!scene.item(bodyName(484), true));
    QVERIFY(!scene.item(bodyName(487), true));
    applyPhase("boss4");
    QTRY_COMPARE(phaseText(scene), QStringLiteral("开场"));
    QVERIFY(scene.item(QStringLiteral("floatingDutyNotesNext"), true));
    refreshOverlayRun(scene, "ENTERED_DUTY", 676, 802, "unknown");
    QTRY_COMPARE(phaseText(scene), QStringLiteral("当前阶段待识别"));
    QVERIFY(!scene.item(bodyName(482), true));

    // The Keeper of the Lake trace replay supplies this order. The mock transports
    // its phase values through the same event and snapshot used by supported duties.
    refreshOverlayRun(scene, "ENTERED_DUTY", 32, 803, "trash1");
    QTRY_COMPARE(phaseText(scene), QStringLiteral("小怪 1"));
    applyPhase("boss1");
    QTRY_COMPARE(phaseText(scene), QStringLiteral("BOSS 1 · 独爪妖禽"));
    applyPhase("trash2");
    QTRY_COMPARE(phaseText(scene), QStringLiteral("小怪 2"));
    applyPhase("boss2");
    QTRY_COMPARE(phaseText(scene), QStringLiteral("BOSS 2 · 魔导炮艇"));
    QVERIFY(scene.item(bodyName(490), true));
    QVERIFY(!scene.item(bodyName(491), true));
    QVERIFY(scene.grab(QStringLiteral("duty-notes-keeper-replay")));
    applyPhase("trash3");
    QTRY_COMPARE(phaseText(scene), QStringLiteral("小怪 3"));
    applyPhase("boss3");
    QTRY_COMPARE(phaseText(scene), QStringLiteral("BOSS 3 · 尘世幻龙"));
    QVERIFY(scene.item(bodyName(491), true));
    QVERIFY(!scene.item(bodyName(490), true));
    refreshOverlayRun(scene, "IDLE", 0, 804);
    QTRY_VERIFY(!scene.item(QStringLiteral("floatingDutyNotes"), true));

    refreshOverlayRun(scene, "ENTERED_DUTY", 32, 805);
    QTRY_COMPARE(phaseText(scene), QStringLiteral("BOSS 2 · 魔导炮艇"));
    const auto next = QStringLiteral("floatingDutyNotesNext");
    QVERIFY(scene.click(next));
    QTRY_COMPARE(phaseText(scene), QStringLiteral("BOSS 3 · 尘世幻龙"));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

// The public library folds entries that read alike under the first one (「另有 N 条相似」) and
// can be narrowed to a job (owner, 2026-11-23). The two text pairs are the
// publisher's own test cases: 0.96 alike folds, 0.8 does not.
void DutyCommunityLibraryTests::communityPublicLibraryFoldsAndFilters()
{
    const auto s = [](const char *text) { return QString::fromUtf8(text); };
    Scene scene;
    QObject *dialog = openCommunityDialog(scene);
    QVERIFY2(dialog, qPrintable(scene.errors));
    const auto id = [](int number) { return QStringLiteral("00000000-0000-4000-8000-%1").arg(number, 12, 10, QLatin1Char('0')); };
    dialog->setProperty("tab", QStringLiteral("public"));
    dialog->setProperty("publicRows", QVariantList{
        publicEntry(201, "macro", "/p 辛苦了嗷♡ <se.2>\n/p 下次见", "", 4),
        publicEntry(202, "macro", "/p 辛苦了嗷♡ <se.4>\n/p 下次见！", "TANK", 4),
        publicEntry(203, "macro", "/p辛苦了!", "WHM", 0),
        publicEntry(204, "macro", "/p辛苦了~", "PLD,BRD", 4),
        publicEntry(205, "reminder", "/p 辛苦了嗷♡ <se.2>\n/p 下次见", "", 0)});
    const auto listed = [&] {
        QStringList out;
        for (const QVariant &entry : qmlList(dialog->property("listRows")))
            out << entry.toMap().value(QStringLiteral("row")).toMap().value(QStringLiteral("content")).toMap()
                       .value(QStringLiteral("id")).toString();
        return out;
    };
    QTRY_COMPARE(listed(), (QStringList{id(201), id(203), id(204), id(205)}));
    QTRY_VERIFY(scene.item(QStringLiteral("dutyCommunitySimilarToggle"), true));
    QCOMPARE(scene.item(QStringLiteral("dutyCommunitySimilarToggle"), true)->property("text").toString(), s("另有 1 条相似"));
    QVERIFY(scene.click(QStringLiteral("dutyCommunitySimilarToggle")));
    QTRY_COMPARE(listed(), (QStringList{id(201), id(202), id(203), id(204), id(205)}));
    QCOMPARE(scene.item(QStringLiteral("dutyCommunitySimilarToggle"), true)->property("text").toString(), s("收起相似"));

    // The open entry is always a row on screen: choosing a folded one opens its group, closing
    // the group moves to the entry that stays, and a ticked entry hidden by folding is not acted on.
    QVERIFY(scene.click(QStringLiteral("dutyCommunitySimilarToggle")));
    QTRY_COMPARE(listed().size(), 4);
    QVERIFY(QMetaObject::invokeMethod(dialog, "select", Q_ARG(QVariant, id(202))));
    QTRY_COMPARE(listed().size(), 5);
    QCOMPARE(dialog->property("currentId").toString(), id(202));
    dialog->setProperty("basket", QVariantList{id(202)});
    QCOMPARE(qmlList(dialog->property("checkedIds")).size(), 1);
    QVERIFY(QMetaObject::invokeMethod(dialog, "toggleGroup", Q_ARG(QVariant, id(201))));
    QTRY_COMPARE(listed().size(), 4);
    QCOMPARE(dialog->property("currentId").toString(), id(201));
    QCOMPARE(qmlList(dialog->property("checkedIds")).size(), 0);
    dialog->setProperty("basket", QVariantList());

    // 职业: a 职能 covers its jobs and a job its 职能; 不限 is the entries without one.
    QVERIFY(scene.item(QStringLiteral("dutyCommunityJobFilter"), true));
    const auto setFilter = [&](const char *key, const QVariantList &choice) {
        QVERIFY(QMetaObject::invokeMethod(dialog, "setFilter", Q_ARG(QVariant, QString::fromLatin1(key)),
                                          Q_ARG(QVariant, QVariant(choice))));
    };
    const auto withJob = [&](const QString &choice) {
        setFilter("job", {choice});
        QStringList out = rowIds(dialog->property("filtered"));
        out.sort();
        return out;
    };
    QCOMPARE(withJob(QStringLiteral("PLD")), (QStringList{id(202), id(204)}));
    QCOMPARE(withJob(QStringLiteral("TANK")), (QStringList{id(202), id(204)}));
    QCOMPARE(withJob(QStringLiteral("HEALER")), QStringList{id(203)});
    QCOMPARE(withJob(QStringLiteral("ANY")), (QStringList{id(201), id(205)}));
    // 不限 and the five 职能 lead, so a long list of jobs never pushes them out of the panel.
    const QVariantList jobOptions = qmlList(dialog->property("jobOptions"));
    QStringList leading;
    for (int i = 0; i < 6 && i < jobOptions.size(); ++i)
        leading << jobOptions.at(i).toMap().value(QStringLiteral("value")).toString();
    QCOMPARE(leading, (QStringList{QStringLiteral("ANY"), QStringLiteral("TANK"), QStringLiteral("HEALER"),
                                   QStringLiteral("MELEE"), QStringLiteral("RANGED"), QStringLiteral("CASTER")}));
    QVERIFY(dialog->property("filterActive").toBool());

    // The detail names the licence by its code; what it allows is said on hover.
    setFilter("job", {});
    QVERIFY(QMetaObject::invokeMethod(dialog, "select", Q_ARG(QVariant, id(201))));
    QTRY_VERIFY(visibleText(scene.mainWindow()->contentItem(), s("CC BY 4.0")));
    QTRY_VERIFY(visibleText(scene.mainWindow()->contentItem(), s("社区投稿 · 未核验")));
    QTRY_VERIFY(visibleText(scene.mainWindow()->contentItem(), QStringLiteral("2026-11-23")));

    // Each tab keeps its own filters: a choice left on the public tab does not narrow the local list.
    setFilter("job", {QStringLiteral("PLD")});
    dialog->setProperty("tab", QStringLiteral("local"));
    QTRY_COMPARE(rowIds(dialog->property("filtered")).size(), rowIds(dialog->property("localRows")).size());
    QVERIFY(!dialog->property("filterActive").toBool());
    dialog->setProperty("tab", QStringLiteral("public"));
    setFilter("job", {});

    // The panel lists a dozen rows before a search: a chosen option past them keeps its row, and
    // the rest are counted for the hint that a search finds them (audit 2026-11-26 K3-3).
    QQuickItem *jobFilter = scene.item(QStringLiteral("dutyCommunityJobFilter"), true);
    QVERIFY(jobFilter);
    QVariantList many;
    for (int number = 0; number < 15; ++number)
        many << QVariantMap{{QStringLiteral("value"), QStringLiteral("J%1").arg(number)},
                            {QStringLiteral("label"), QStringLiteral("Job %1").arg(number)},
                            {QStringLiteral("sub"), QString()}, {QStringLiteral("count"), 1}};
    jobFilter->setProperty("options", many);
    jobFilter->setProperty("selection", QVariantList{QStringLiteral("J14")});
    QVariant shown;
    QVERIFY(QMetaObject::invokeMethod(jobFilter, "listed", Q_RETURN_ARG(QVariant, shown), Q_ARG(QVariant, QString())));
    QStringList values;
    for (const QVariant &option : qmlMap(shown).value(QStringLiteral("rows")).toList())
        values << option.toMap().value(QStringLiteral("value")).toString();
    QCOMPARE(values.size(), 13);
    QCOMPARE(values.last(), QStringLiteral("J14"));
    QCOMPARE(qmlMap(shown).value(QStringLiteral("hidden")).toInt(), 2);
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void DutyCommunityLibraryTests::reopeningKeepsTheActiveRefresh()
{
    Scene scene;
    QObject *dialog = openCommunityDialog(scene);
    QVERIFY2(dialog, qPrintable(scene.errors));
    QObject *service = dialog->property("service").value<QObject *>();
    QVERIFY(service);
    scene.backend.setReplyDelay(10000, QStringLiteral("DutyCommunity"));
    dialog->setProperty("online", true);
    dialog->setProperty("tab", QStringLiteral("public"));
    QCOMPARE(dialog->property("publicAction").toString(), QStringLiteral("refresh"));
    const int ticket = dialog->property("serial").toInt();
    const int generation = dialog->property("publicRequest").toInt();
    QVERIFY(QMetaObject::invokeMethod(dialog, "close"));
    QTRY_VERIFY(!dialog->property("visible").toBool());
    QVERIFY(QMetaObject::invokeMethod(dialog, "openFor"));
    QTRY_VERIFY(dialog->property("opened").toBool());
    dialog->setProperty("tab", QStringLiteral("public"));
    QCOMPARE(dialog->property("publicRequest").toInt(), generation);

    const auto row = publicEntry(48, "macro", "/p ready", "", 0).value(QStringLiteral("content")).toMap();
    const QVariantMap snapshot{{QStringLiteral("status"), QStringLiteral("ready")}, {QStringLiteral("online"), true},
                               {QStringLiteral("cached"), true}, {QStringLiteral("entries"), QVariantList{row}}};
    const auto reply = [&](int id, const QVariantMap &result) {
        return QMetaObject::invokeMethod(service, "answered", Q_ARG(int, id), Q_ARG(QVariantMap, result),
                                         Q_ARG(QString, QString()));
    };
    QVERIFY(reply(ticket, snapshot));
    QTRY_COMPARE(rowIds(dialog->property("publicRows")), QStringList{row.value(QStringLiteral("id")).toString()});
    QVERIFY(!dialog->property("refreshing").toBool());

    // An explicit switch-off supersedes another network refresh, even if that refresh answers last.
    QVERIFY(QMetaObject::invokeMethod(dialog, "refreshPublic"));
    const int superseded = dialog->property("serial").toInt();
    const QVariantMap disable{{QStringLiteral("enabled"), false}};
    QVERIFY(QMetaObject::invokeMethod(dialog, "loadPublic", Q_ARG(QVariant, QStringLiteral("online")),
                                      Q_ARG(QVariant, disable)));
    auto offline = snapshot;
    offline.insert(QStringLiteral("online"), false);
    offline.insert(QStringLiteral("status"), QStringLiteral("offline"));
    QVERIFY(reply(dialog->property("serial").toInt(), offline));
    QVERIFY(reply(superseded, snapshot));
    QVERIFY(!dialog->property("online").toBool());
    QCOMPARE(dialog->property("libraryStatus").toString(), QStringLiteral("offline"));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void DutyCommunityLibraryTests::emptySnapshotMarksInstalledSourceRemoved()
{
    Scene scene;
    QObject *dialog = openCommunityDialog(scene);
    QVERIFY2(dialog, qPrintable(scene.errors));
    auto installed = publicEntry(42, "macro", "/p ready", "", 0);
    installed.insert(QStringLiteral("upstream"), installed.value(QStringLiteral("content")));
    dialog->setProperty("localRows", QVariantList{installed});
    const auto removed = [&] {
        return visibleText(scene.item(QStringLiteral("dutyCommunityDetail"), true), QStringLiteral("已移除"));
    };
    QTRY_COMPARE(dialog->property("currentId").toString(),
                 installed.value(QStringLiteral("content")).toMap().value(QStringLiteral("id")).toString());
    // No snapshot was fetched at startup. The same empty list from a valid cache is authoritative.
    QVERIFY(!removed());
    scene.backend.holdCommunityBrowse = true;
    QVERIFY(QMetaObject::invokeMethod(dialog, "loadPublic", Q_ARG(QVariant, QStringLiteral("browse")),
                                      Q_ARG(QVariant, QVariantMap{})));
    QTRY_COMPARE(scene.backend.pendingCommunityBrowse.size(), 1);
    scene.backend.pendingCommunityBrowse.takeFirst()->succeed({
        {QStringLiteral("status"), QStringLiteral("offline")}, {QStringLiteral("online"), false},
        {QStringLiteral("cached"), true}, {QStringLiteral("entries"), QJsonArray()}});
    QTRY_VERIFY(removed());
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

// 共享库 shows each entry's 👍 on GitHub (owner, 2026-11-27): the count on its row and on the
// detail's 点赞 button, which opens the comment the workflow posted for the entry; 点赞最多 puts
// the most liked first. An entry the workflow has not reached yet shows neither. The counts come
// from the mock library, as CommunityLibrary hands them over.
void DutyCommunityLibraryTests::sharedLibraryShowsLikesAndSortsByThem()
{
    const auto s = [](const char *text) { return QString::fromUtf8(text); };
    Scene scene;
    QObject *dialog = openCommunityDialog(scene);
    QVERIFY2(dialog, qPrintable(scene.errors));
    QDesktopServices::setUrlHandler(QStringLiteral("https"), this, "rememberUrl");
    const auto urlGuard = qScopeGuard([] { QDesktopServices::unsetUrlHandler(QStringLiteral("https")); });
    opened.clear();
    dialog->setProperty("tab", QStringLiteral("public"));
    const QVariantMap online{{QStringLiteral("enabled"), true}};
    QVERIFY(QMetaObject::invokeMethod(dialog, "loadPublic", Q_ARG(QVariant, QStringLiteral("online")),
                                      Q_ARG(QVariant, online)));
    QTRY_COMPARE(rowIds(dialog->property("publicRows")).size(), 6);
    QTRY_COMPARE(qmlMap(dialog->property("publicLikes")).size(), 5);
    QVERIFY(QMetaObject::invokeMethod(dialog, "showEveryPlace"));

    // A row shows its count; 104 has no like comment yet.
    QQuickItem *content = scene.mainWindow()->contentItem();
    QTRY_VERIFY(!visibleNamed(content, QStringLiteral("dutyCommunityRowLikes")).isEmpty());
    QStringList counts;
    for (QQuickItem *likes : visibleNamed(content, QStringLiteral("dutyCommunityRowLikes")))
        for (QQuickItem *child : likes->childItems())
            if (child->inherits("QQuickText")) counts << child->property("text").toString();
    QVERIFY2(counts.contains(QStringLiteral("27")) && counts.contains(QStringLiteral("0")), qPrintable(counts.join(u',')));
    QCOMPARE(visibleNamed(content, QStringLiteral("dutyCommunityRowLikes")).size(),
             qmlList(dialog->property("listRows")).size() - 1);

    // 点赞最多: 105 (27), 101 (12), 102 (3) in that order, the entry without a count last.
    QVERIFY(QMetaObject::invokeMethod(dialog, "setSort", Q_ARG(QVariant, QStringLiteral("likes"))));
    QStringList order;
    for (const QVariant &entry : qmlList(dialog->property("listRows")))
        order << entry.toMap().value(QStringLiteral("row")).toMap().value(QStringLiteral("content")).toMap()
                     .value(QStringLiteral("id")).toString();
    QVERIFY2(order.indexOf(mockId(105)) == 0 && order.indexOf(mockId(101)) < order.indexOf(mockId(102))
                 && order.last() == mockId(104), qPrintable(order.join(u',')));
    QVERIFY(QMetaObject::invokeMethod(dialog, "setSort", Q_ARG(QVariant, QStringLiteral("updated"))));

    // The detail's 点赞 opens the like comment on GitHub; the count is on the button.
    QVERIFY(QMetaObject::invokeMethod(dialog, "select", Q_ARG(QVariant, mockId(105))));
    QTRY_VERIFY(scene.item(QStringLiteral("dutyCommunityDetailLike"), true));
    QCOMPARE(scene.item(QStringLiteral("dutyCommunityDetailLike"), true)->property("text").toString(), s("点赞 27"));
    QVERIFY(scene.click(QStringLiteral("dutyCommunityDetailLike")));
    QCOMPARE(opened, (QList<QUrl>{QUrl(QStringLiteral(
        "https://github.com/Harendotes-Tang/MentorRecorder-Calibrations/issues/132#issuecomment-2400000105"))}));
    QVERIFY(QMetaObject::invokeMethod(dialog, "select", Q_ARG(QVariant, mockId(104))));
    QTRY_COMPARE(dialog->property("currentId").toString(), mockId(104));
    QVERIFY(!scene.item(QStringLiteral("dutyCommunityDetailLike"), true));

    // Local entries have no 👍; the star is theirs.
    dialog->setProperty("tab", QStringLiteral("local"));
    QTRY_VERIFY(scene.item(QStringLiteral("dutyCommunityDetailFavorite"), true));
    QVERIFY(!scene.item(QStringLiteral("dutyCommunityDetailLike"), true));
    QVERIFY(visibleNamed(content, QStringLiteral("dutyCommunityRowLikes")).isEmpty());
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void DutyCommunityLibraryTests::localPageAcceptsOnlyValidOffsets()
{
    mr::MockBackend backend;
    const auto list = [&](QJsonValue offset) {
        return mrtest::ask(backend, QStringLiteral("DutyCommunity"),
                           {{QStringLiteral("action"), QStringLiteral("list")}, {QStringLiteral("offset"), offset}}).payload;
    };
    const auto first = list(QJsonValue::Undefined);
    QCOMPARE(first.value(QStringLiteral("status")).toString(), QStringLiteral("ready"));
    QCOMPARE(list(QJsonValue::Null), first);
    QCOMPARE(list(0), first);
    const auto items = QStringLiteral("items");
    auto rows = first.value(items).toArray();
    QVERIFY(rows.size() > 1);
    rows.removeFirst();
    QCOMPARE(list(1).value(items), QJsonValue(rows));
    QCOMPARE(list(400).value(items), QJsonValue(QJsonArray()));
    for (const auto &invalid : QJsonArray{QStringLiteral("1"), true, 0.5, 2147483648LL, -1, 401})
        QCOMPARE(list(invalid).value(QStringLiteral("status")).toString(), QStringLiteral("invalid_offset"));
}

void DutyCommunityLibraryTests::changedLocalPageIsReadAgainFromTheStart()
{
    Scene scene;
    QObject *dialog = openCommunityDialog(scene);
    QVERIFY(dialog);
    const auto page = [](int item, int generation, QJsonValue next) {
        const QJsonObject row = QJsonObject::fromVariantMap(publicEntry(item, "macro", "/p Test", "", 0));
        return QJsonObject{{QStringLiteral("status"), QStringLiteral("ready")},
                           {QStringLiteral("items"), QJsonArray{row}}, {QStringLiteral("next"), next},
                           {QStringLiteral("generation"), generation}};
    };
    scene.backend.communityListPages = {page(1, 10, 1), page(2, 11, QJsonValue::Null),
                                        page(3, 11, 1), page(4, 11, QJsonValue::Null)};
    QVERIFY(QMetaObject::invokeMethod(dialog, "loadLocal"));
    QTRY_COMPARE(rowIds(dialog->property("localRows")), (QStringList{mockId(3), mockId(4)}));
    QCOMPARE(scene.backend.requestedCommunityOffsets, (QList<int>{0, 1, 0, 1}));
}

// 收藏 uses Lucide's star (owner, 2026-11-27), outlined until the entry is collected and then
// filled in yellow, on the row and on the detail's button, which says 收藏 / 已收藏.
void DutyCommunityLibraryTests::favoriteStarIsLucideAndFilledWhenChosen()
{
    const auto s = [](const char *text) { return QString::fromUtf8(text); };
    Scene scene;
    QObject *dialog = openCommunityDialog(scene);
    QVERIFY2(dialog, qPrintable(scene.errors));
    QVariantMap first;
    for (const QVariant &row : qmlList(dialog->property("localRows")))
        if (first.isEmpty() && !row.toMap().value(QStringLiteral("favorite")).toBool()) first = row.toMap();
    QVERIFY(!first.isEmpty());
    const QString id = qmlMap(first.value(QStringLiteral("content"))).value(QStringLiteral("id")).toString();
    QVERIFY(QMetaObject::invokeMethod(dialog, "select", Q_ARG(QVariant, id)));
    QTRY_VERIFY(scene.item(QStringLiteral("dutyCommunityDetailFavorite"), true));
    QQuickItem *button = scene.item(QStringLiteral("dutyCommunityDetailFavorite"), true);
    const auto icon = [&] { return button->findChild<QQuickItem *>(QStringLiteral("buttonIcon")); };
    QCOMPARE(button->property("iconName").toString(), QStringLiteral("star"));
    QCOMPARE(button->property("text").toString(), s("收藏"));
    QVERIFY2(svgOf(icon()).contains(QStringLiteral("fill=\"none\"")), qPrintable(svgOf(icon())));
    // The row's star shows on hover, on the open row and once collected. The rows are rebuilt
    // when the list answers, so they are looked up again each time.
    const auto stars = [&](bool filled) {
        int n = 0;
        for (QQuickItem *star : visibleNamed(scene.mainWindow()->contentItem(), QStringLiteral("dutyCommunityRowStar"))) {
            const QString svg = svgOf(star);
            n += svg.contains(QStringLiteral("M11.525 2.295")) && svg.contains(QStringLiteral("fill=\"none\"")) != filled;
        }
        return n;
    };
    QTRY_VERIFY(stars(false) >= 1);
    const int filledBefore = stars(true);

    QVERIFY(QMetaObject::invokeMethod(dialog, "toggleFavorite", Q_ARG(QVariant, first)));
    QTRY_COMPARE(button->property("text").toString(), s("已收藏"));
    QVERIFY(button->property("iconFilled").toBool());
    const QString yellow = button->property("iconColor").value<QColor>().name(QColor::HexRgb);
    QTRY_VERIFY2(svgOf(icon()).contains(QStringLiteral("fill=\"") + yellow + u'"'), qPrintable(svgOf(icon())));
    QTRY_COMPARE(stars(true), filledBefore + 1);
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

MR_DESKTOP_TEST_SUITE(DutyCommunityLibrary)
{
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QApplication app(argc, argv);
    if (!mr::registerBundledFonts()) return 2;
    QApplication::setFont(QFont(QStringLiteral("Noto Sans SC")));
    DutyCommunityLibraryTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "DutyCommunityLibraryTests.moc"
