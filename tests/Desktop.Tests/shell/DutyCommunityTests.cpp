// 副本提醒与导随宏: the community dialog opened from 总览 on the shipping main window - its
// blank states, the example macros, the editor, the ticked entries, the submission and the
// directory - and the text rules it shares with the publishing workflow. The shared library's
// list is DutyCommunityLibraryTests.cpp.
//
// The scene is the statistics float's (StatisticsSceneFixture.h), opened on Main.qml.

#include "DesktopTestRunner.h"
#include "DutyCommunitySceneFixture.h"
#include "TestCollectorGuard.h"
#include "BundledFonts.h"

#include <QApplication>
#include <QClipboard>
#include <QDir>
#include <QFile>
#include <QFont>
#include <QJSEngine>
#include <QJSValue>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QQuickItem>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QSignalSpy>
#include <QStandardPaths>
#include <QTest>

namespace {
class DutyCommunityTests : public QObject
{
    Q_OBJECT
private Q_SLOTS:
    void initTestCase();
    void dashboardOpensTheCommunityLibrary();
    void communityBlankStatesAreCentred();
    void communityExamplesStartNamedMacros();
    void communityEditorFollowsTheDesign();
    void communityEditorSaysWhyASaveIsRefused();
    void communityLateRepliesKeepThePlayersPlace();
    void communityAuthorizationIgnoresSupersededReplies();
    void withdrawalRequiresOnePublishingAccount();
    void communityDirectorySetsTheRangeAndGroupsTheList();
    void communityTextRulesMatchThePublisher();
    void overlayGuideTipSaysWhereItGoes();
public:
    Q_INVOKABLE void rememberUrl(const QUrl &url) { opened << url; }
    QList<QUrl> opened;
};
} // namespace

void DutyCommunityTests::initTestCase()
{
    QVERIFY(QDir().mkpath(QString::fromUtf8(MR_SOURCE_DIR) + kEvidence()));
    registerSceneQmlTypes();
}

void DutyCommunityTests::withdrawalRequiresOnePublishingAccount()
{
    Scene scene;
    auto *dialog = openCommunityDialog(scene);
    QVERIFY2(dialog, qPrintable(scene.errors));
    QVariantList rows = qmlList(dialog->property("localRows")), published;
    QStringList ids;
    for (int i = 0; i < 2; ++i) {
        QVariantMap row = qmlMap(rows.at(i));
        row.insert(QStringLiteral("upstream"), QVariant());
        rows[i] = row;
        ids << qmlMap(row.value(QStringLiteral("content"))).value(QStringLiteral("id")).toString();
        auto item = publicEntry(i + 1, "reminder", "Withdraw", "", 4).value(QStringLiteral("content")).toMap();
        item.insert(QStringLiteral("id"), ids.last());
        item.insert(QStringLiteral("author"), QVariantMap{{QStringLiteral("id"), 150 + i},
            {QStringLiteral("login"), i == 0 ? QStringLiteral("one") : QStringLiteral("two")}});
        published << QVariantMap{{QStringLiteral("content"), item}};
    }
    dialog->setProperty("localRows", rows);
    dialog->setProperty("publicRows", published);
    QVERIFY(QMetaObject::invokeMethod(dialog, "withdrawRows", Q_ARG(QVariant, QVariant(ids))));
    auto *consent = scene.item(QStringLiteral("dutyCommunityConsent"), true);
    QVERIFY(consent && !consent->isEnabled());
    consent->setProperty("checked", true);
    auto *send = scene.item(QStringLiteral("dutyCommunitySubmitBrowser"), true);
    QVERIFY(send && !send->isEnabled());
    QVERIFY(visibleText(scene.item(QStringLiteral("dutyCommunitySubmission"), true),
        QStringLiteral("所选条目由不同的 GitHub 账号发布（@one、@two）。请返回列表，按发布账号分别撤回。")));
}

void DutyCommunityTests::dashboardOpensTheCommunityLibrary()
{
    Scene scene;
    QVERIFY2(scene.create(QStringLiteral("harendotes"), true, true), qPrintable(scene.errors));
    QVERIFY(QTest::qWaitForWindowExposed(scene.mainWindow()));
    QTRY_VERIFY(scene.item(QStringLiteral("dashboardDutyCommunity"), true));
    QVERIFY(scene.click(QStringLiteral("dashboardDutyCommunity")));
    auto *dialog = scene.root->findChild<QObject *>(QStringLiteral("dutyCommunityDialog"));
    QVERIFY(dialog);
    QTRY_VERIFY(dialog->property("visible").toBool());
    QCOMPARE(dialog->property("tab").toString(), QStringLiteral("local"));
    QCOMPARE(dialog->property("mode").toString(), QStringLiteral("view"));

    // Leaving the tab drops an unsaved form; opening 公共共享库 starts a refresh once it may go online.
    QVERIFY(QMetaObject::invokeMethod(dialog, "editNew", Q_ARG(QVariant, QStringLiteral("macro")),
                                      Q_ARG(QVariant, QVariant())));
    QCOMPARE(dialog->property("mode").toString(), QStringLiteral("edit"));
    dialog->setProperty("online", true);
    dialog->setProperty("tab", QStringLiteral("public"));
    QCOMPARE(dialog->property("mode").toString(), QStringLiteral("view"));
    QVERIFY(dialog->property("remote").toBool() && dialog->property("refreshing").toBool());
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

namespace {
/// The community dialog opened from the dashboard, its mock entries loaded and then
/// set aside so that the onboarding shows.
QObject *openEmptyCommunityDialog(Scene &scene)
{
    if (!scene.create(QStringLiteral("harendotes"), true, true)) return nullptr;
    if (!QTest::qWaitForWindowExposed(scene.mainWindow())) return nullptr;
    if (!QTest::qWaitFor([&] { return scene.item(QStringLiteral("dashboardDutyCommunity"), true) != nullptr; }))
        return nullptr;
    if (!scene.click(QStringLiteral("dashboardDutyCommunity"))) return nullptr;
    auto *dialog = scene.root->findChild<QObject *>(QStringLiteral("dutyCommunityDialog"));
    if (!dialog || !QTest::qWaitFor([&] { return dialog->property("visible").toBool()
                                                 && !dialog->property("onboarding").toBool(); }))
        return nullptr;
    dialog->setProperty("localRows", QVariantList());
    // The mock leaves the automatic-recording alert open on top, holding the keyboard.
    if (QObject *alert = scene.root->findChild<QObject *>(QStringLiteral("automaticRecordingAlert")))
        QMetaObject::invokeMethod(alert, "close");
    return dialog;
}

} // namespace

// 还没有提醒或宏 and 公共共享库还没有内容 sat at the left edge of their column: the
// blank state was only as wide as its widest line, so the column could not
// stretch it (owner's screenshots, 2026-11-22). The test measures where each
// line is drawn, not where its box is.
void DutyCommunityTests::communityBlankStatesAreCentred()
{
    Scene scene;
    QObject *dialog = openEmptyCommunityDialog(scene);
    QVERIFY2(dialog, qPrintable(scene.errors));

    // How far the drawn line sits from the middle of its column; huge when it is not shown.
    const auto offCentre = [&](const QString &column, const QString &line) -> qreal {
        constexpr qreal missing = 1e6;
        QQuickItem *frame = scene.item(column, true);
        QQuickItem *text = visibleText(frame, line);
        if (!text) return missing;
        const qreal drawn = text->property("contentWidth").toReal();
        const int align = text->property("effectiveHorizontalAlignment").toInt();
        const qreal left = (align & Qt::AlignHCenter) ? (text->width() - drawn) / 2
                         : (align & Qt::AlignRight) ? text->width() - drawn : 0;
        return qAbs(text->mapToItem(frame, QPointF(left + drawn / 2, 0)).x() - frame->width() / 2);
    };
    const auto onboarding = QStringLiteral("dutyCommunityOnboarding");
    const auto onboardingHeading = QString::fromUtf8("还没有提醒或宏");
    const auto onboardingBody = QString::fromUtf8("把进本要说的话、要注意的机制存在这里，进了副本一键复制。");
    QTRY_VERIFY2(offCentre(onboarding, onboardingHeading) < 1.5,
                 qPrintable(QString::number(offCentre(onboarding, onboardingHeading))));
    QVERIFY2(offCentre(onboarding, onboardingBody) < 1.5, qPrintable(QString::number(offCentre(onboarding, onboardingBody))));

    // A short sentence under a longer heading: the case in the second screenshot.
    dialog->setProperty("tab", QStringLiteral("public"));
    const auto blank = QStringLiteral("dutyCommunityPublicBlank");
    QTRY_VERIFY(scene.item(blank, true));
    QTRY_VERIFY(!dialog->property("refreshing").toBool());
    dialog->setProperty("online", true);
    dialog->setProperty("libraryStatus", QStringLiteral("not_deployed"));
    const auto heading = QString::fromUtf8("公共共享库还没有内容");
    const auto body = QString::fromUtf8("公共共享库尚未部署。");
    QTRY_VERIFY2(offCentre(blank, body) < 1.5, qPrintable(QString::number(offCentre(blank, body))));
    QVERIFY2(offCentre(blank, heading) < 1.5, qPrintable(QString::number(offCentre(blank, heading))));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

// The examples are the owner's macros (2026-11-22): the card shows the name and the first
// line, and picking one opens the editor with that name and the whole macro. Every example
// must pass the Collector's macro rules, or saving it would be refused.
void DutyCommunityTests::communityExamplesStartNamedMacros()
{
    const auto s = [](const char *text) { return QString::fromUtf8(text); };
    Scene scene;
    QObject *dialog = openEmptyCommunityDialog(scene);
    QVERIFY2(dialog, qPrintable(scene.errors));

    const QVariantList examples = qmlList(dialog->property("examples"));
    QStringList titles;
    for (const QVariant &entry : examples) {
        const QVariantMap example = qmlMap(entry);
        titles << example.value(QStringLiteral("title")).toString();
        const QStringList lines = example.value(QStringLiteral("body")).toString().split(QLatin1Char('\n'));
        QVERIFY2(lines.size() <= 15, qPrintable(titles.last()));
        for (const QString &line : lines)
            QVERIFY2(line.toUtf8().size() <= 180, qPrintable(titles.last() + QStringLiteral(": ") + line));
    }
    QCOMPARE(titles, (QStringList{s("实盾姿"), s("单点攻击"), s("无敌提醒宏"), s("OT宏"), s("禁止攻击宏"), s("辛苦宏")}));

    // A new entry starts in the selected record region, 全部区服 while none is selected (owner, 2026-11-24).
    const auto blankRegion = [&] {
        QVariant item;
        QMetaObject::invokeMethod(dialog, "blankItem", Q_RETURN_ARG(QVariant, item), Q_ARG(QVariant, QStringLiteral("macro")));
        return qmlMap(item).value(QStringLiteral("region")).toString();
    };
    scene.app->setRecordRegion(QStringLiteral("GLOBAL"));
    QCOMPARE(blankRegion(), QStringLiteral("GLOBAL"));
    scene.app->setRecordRegion(QString());
    QCOMPARE(blankRegion(), QStringLiteral("ALL"));

    const auto onboarding = QStringLiteral("dutyCommunityOnboarding");
    QTRY_VERIFY(visibleText(scene.item(onboarding, true), s("实盾姿")));
    // A card shows the macro's first line, as typed: its tags are text, not markup.
    QQuickItem *first = visibleText(scene.item(onboarding, true), s("/marking 攻击1 <t>"));
    QVERIFY(first);
    QCOMPARE(first->property("textFormat").toInt(), int(Qt::PlainText));

    QQuickItem *card = visibleText(scene.item(onboarding, true), s("实盾姿"));
    QTest::mouseClick(scene.mainWindow(), Qt::LeftButton, {},
                      card->mapToScene(QPointF(card->width() / 2, card->height() / 2)).toPoint());
    QTRY_COMPARE(dialog->property("mode").toString(), QStringLiteral("edit"));
    QTRY_VERIFY(scene.item(QStringLiteral("dutyCommunityEditorTitle"), true));
    QCOMPARE(scene.item(QStringLiteral("dutyCommunityEditorTitle"), true)->property("text").toString(), s("实盾姿"));
    QCOMPARE(scene.item(QStringLiteral("dutyCommunityEditorBody"), true)->property("text").toString(),
             s("/micon 守护\n/p 请开启盾姿：钢铁信念（剑术师/骑士）守护（斧术师/战士）深恶痛绝（暗黑骑士）王室亲卫（绝枪战士）<se.2>"));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

// The editor as drawn in the Design artifact (新建宏, owner 2026-11-22): it takes the whole dialog,
// the title and the body on the left, 适用范围 on the right where nothing is required. One 副本
// list with a search box, one BOSS / 阶段 field, 职业 by 职能, a game version range from two lists.
// Whatever the form no longer shows, or shows unchanged, is saved as it was.
void DutyCommunityTests::communityEditorFollowsTheDesign()
{
    const auto s = [](const char *text) { return QString::fromUtf8(text); };
    Scene scene;
    QObject *dialog = openEmptyCommunityDialog(scene);
    QVERIFY2(dialog, qPrintable(scene.errors));
    const auto id = QStringLiteral("6f1c2b4e-0000-4000-8000-000000000001");
    QVariantMap content{
        {QStringLiteral("schema_version"), 1}, {QStringLiteral("id"), id},
        {QStringLiteral("type"), QStringLiteral("macro")}, {QStringLiteral("content_id"), 0},
        {QStringLiteral("region"), QStringLiteral("CN")}, {QStringLiteral("language"), QStringLiteral("zh-Hans")},
        {QStringLiteral("title"), s("开场")}, {QStringLiteral("body"), s("/p 大家好")},
        {QStringLiteral("boss"), QStringLiteral("BOSS 1")}, {QStringLiteral("phase"), s("转场")},
        {QStringLiteral("role"), QStringLiteral("MT")}, {QStringLiteral("tags"), QVariantList{s("开场")}},
        {QStringLiteral("game_version"), QStringLiteral("6.0 - 7.3")}, {QStringLiteral("links"), QVariantList{}},
        {QStringLiteral("source"), s("群友")}};
    // The revision the entry has in the local list, 0 before it is saved: the Collector (and the
    // mock) refuses a save made from another one.
    const auto revisionNow = [&] {
        for (const QVariant &entry : qmlList(dialog->property("localRows"))) {
            const QVariantMap row = qmlMap(entry);
            if (qmlMap(row.value(QStringLiteral("content"))).value(QStringLiteral("id")) == id)
                return row.value(QStringLiteral("revision")).toInt();
        }
        return 0;
    };
    const auto rowOf = [&](const QVariantMap &item) {
        return QVariantMap{{QStringLiteral("content"), item}, {QStringLiteral("revision"), revisionNow()},
                           {QStringLiteral("position"), 3}, {QStringLiteral("favorite"), true}};
    };
    // The entry exists already, as one opened for editing does.
    const QVariant firstSave = rowOf(content);
    QVERIFY(QMetaObject::invokeMethod(dialog, "call", Q_ARG(QVariant, QStringLiteral("save")), Q_ARG(QVariant, firstSave),
                                      Q_ARG(QVariant, QVariant()), Q_ARG(QVariant, QVariant())));
    QVERIFY(QMetaObject::invokeMethod(dialog, "loadLocal"));
    QTRY_COMPARE(revisionNow(), 1);
    const auto field = [&](const char *name) { return scene.item(QString::fromLatin1(name), true); };
    // The mock reopens its modal automatic-recording alert after a save; a modal popup on top
    // takes the keyboard and blocks the dialog's shortcuts, as it would for a player.
    const auto keysToEditor = [&] {
        QObject *alert = scene.root->findChild<QObject *>(QStringLiteral("automaticRecordingAlert"));
        if (alert && alert->property("visible").toBool())
            QMetaObject::invokeMethod(alert, "close");
        scene.mainWindow()->requestActivate();
        if (!QTest::qWaitForWindowActive(scene.mainWindow()) || (alert && !QTest::qWaitFor([&] {
                return !alert->property("visible").toBool(); })))
            return false;
        QQuickItem *title = scene.item(QStringLiteral("dutyCommunityEditorTitle"), true);
        title->forceActiveFocus();
        return QTest::qWaitFor([&] { return title->hasActiveFocus(); });
    };
    // A version list is set the way a click sets it.
    const auto pickVersion = [&](const char *box, const QString &version) {
        QQuickItem *list = field(box);
        int index = -1;
        QMetaObject::invokeMethod(list, "indexOfValue", Q_RETURN_ARG(int, index), Q_ARG(QVariant, version));
        list->setProperty("currentIndex", index);
        return QMetaObject::invokeMethod(list, "activated", Q_ARG(int, index));
    };
    QVERIFY(QMetaObject::invokeMethod(dialog, "startEdit", Q_ARG(QVariant, rowOf(content))));
    QTRY_VERIFY(field("dutyCommunityEditor"));
    QQuickItem *editor = field("dutyCommunityEditor");

    // The form takes the dialog: no tabs, filters, list or 投稿进度 beside it.
    for (const char *aside : {"dutyCommunityTabs", "dutyCommunitySearch", "dutyCommunityIssue"})
        QVERIFY2(!field(aside), aside);
    QVERIFY(visibleText(field("dutyCommunityBack"), s("副本提醒与导随宏")));
    QCOMPARE(field("dutyCommunityEditorHeading")->property("text").toString(), s("编辑宏"));
    QQuickItem *scope = field("dutyCommunityEditorScope");
    QVERIFY(visibleText(scope, s("适用范围")));
    QVERIFY(visibleText(scope, s("都可以不填，不填就是都适用。")));
    for (const char *gone : {"副本（可不填）", "BOSS / 阶段（可不填）", "角色", "显示顺序", "收藏",
                             "标签，以逗号分隔", "来源", "改为通用"})
        QVERIFY2(!visibleText(editor, s(gone)), gone);

    QQuickItem *duty = field("dutyCommunityEditorDuty");
    QVERIFY(duty && duty->inherits("QQuickComboBox"));
    QCOMPARE(duty->property("displayText").toString(), s("通用"));
    QCOMPARE(field("dutyCommunityEditorStage")->property("text").toString(), s("BOSS 1 · 转场"));
    // 游戏版本 is two lists; "6.0 - 7.3" opens as 6.0 → 7.3.
    QTRY_COMPARE(field("dutyCommunityEditorVersionFrom")->property("currentValue").toString(), QStringLiteral("6.0"));
    QCOMPARE(field("dutyCommunityEditorVersionTo")->property("currentValue").toString(), QStringLiteral("7.3"));
    // "MT" was written by hand, so no job is chosen and the text is shown under the field.
    QCOMPARE(field("dutyCommunityEditorJobs")->property("displayText").toString(), s("不限"));
    QVERIFY(visibleText(field("dutyCommunityEditorRoleByHand"), s("原来写的是「MT」")));
    // A macro has room for 15 numbered lines at once.
    QVERIFY(visibleText(field("dutyCommunityEditorBodyEdit"), QStringLiteral("15")));

    // Some 850 duties: the list opens with a search box, and a found duty is picked from it.
    QVERIFY(scene.click(QStringLiteral("dutyCommunityEditorDuty")));
    QTRY_VERIFY(field("dutyCommunityEditorDutySearch"));
    field("dutyCommunityEditorDutySearch")->setProperty("text", s("天狼星灯塔"));
    QTRY_VERIFY(field("dutyCommunityEditorDutyRow"));
    QVERIFY(scene.click(QStringLiteral("dutyCommunityEditorDutyRow")));
    const int chosen = duty->property("value").toInt();
    QVERIFY(chosen > 0);
    QVERIFY2(duty->property("displayText").toString().contains(s("天狼星灯塔")),
             qPrintable(duty->property("displayText").toString()));
    QTRY_VERIFY(!field("dutyCommunityEditorDutySearch"));
    // It stays chosen when the server changes the list.
    field("dutyCommunityEditorRegion")->setProperty("currentIndex", 2); // 繁中服 TC
    QCOMPARE(duty->property("value").toInt(), chosen);
    QVERIFY(!duty->property("displayText").toString().isEmpty());

    QSignalSpy saved(editor, SIGNAL(saved(QVariant)));
    QVERIFY(scene.click(QStringLiteral("dutyCommunityEditorSave")));
    QCOMPARE(saved.count(), 1);
    QVariantMap value = qmlMap(saved.takeFirst().at(0));
    QVariantMap item = value.value(QStringLiteral("content")).toMap();
    QCOMPARE(item.value(QStringLiteral("content_id")).toInt(), chosen);
    QCOMPARE(item.value(QStringLiteral("region")).toString(), QStringLiteral("TC"));
    QCOMPARE(item.value(QStringLiteral("boss")).toString(), QStringLiteral("BOSS 1"));
    QCOMPARE(item.value(QStringLiteral("phase")).toString(), s("转场"));
    QCOMPARE(item.value(QStringLiteral("role")).toString(), QStringLiteral("MT"));
    // An untouched range keeps how it was written.
    QCOMPARE(item.value(QStringLiteral("game_version")).toString(), QStringLiteral("6.0 - 7.3"));
    QCOMPARE(item.value(QStringLiteral("source")).toString(), s("群友"));
    QCOMPARE(item.value(QStringLiteral("tags")).toList(), QVariantList{s("开场")});
    QCOMPARE(value.value(QStringLiteral("position")).toInt(), 3);
    QCOMPARE(value.value(QStringLiteral("favorite")).toBool(), true);
    // The save goes to the Collector (here the mock); it lands before the entry opens again.
    QTRY_COMPARE(revisionNow(), 2);

    // A changed BOSS / 阶段 is one value now: it becomes the boss. Jobs are saved as their
    // abbreviations in the catalogue's order.
    QVERIFY(QMetaObject::invokeMethod(dialog, "startEdit", Q_ARG(QVariant, rowOf(content))));
    QTRY_VERIFY(field("dutyCommunityEditorStage"));
    field("dutyCommunityEditorStage")->setProperty("text", s("尾王"));
    QQuickItem *jobs = field("dutyCommunityEditorJobs");
    QVERIFY(QMetaObject::invokeMethod(jobs, "toggle", Q_ARG(QVariant, QStringLiteral("WAR"))));
    QVERIFY(QMetaObject::invokeMethod(jobs, "toggle", Q_ARG(QVariant, QStringLiteral("PLD"))));
    QCOMPARE(jobs->property("displayText").toString(), s("骑士、战士"));
    QVERIFY(!field("dutyCommunityEditorRoleByHand"));
    // A start after the end is told at once, and refused.
    QVERIFY(pickVersion("dutyCommunityEditorVersionFrom", QStringLiteral("7.3")));
    QVERIFY(pickVersion("dutyCommunityEditorVersionTo", QStringLiteral("6.0")));
    QTRY_VERIFY(field("dutyCommunityEditorVersionProblem"));
    QVERIFY(scene.click(QStringLiteral("dutyCommunityEditorSave")));
    QCOMPARE(saved.count(), 0);
    // 7.2 → 不限.
    QVERIFY(pickVersion("dutyCommunityEditorVersionTo", QString()));
    QVERIFY(pickVersion("dutyCommunityEditorVersionFrom", QStringLiteral("7.2")));
    QVERIFY(!field("dutyCommunityEditorVersionProblem"));
    // Ctrl + S saves (Design: Ctrl + S 保存): the dialog's own shortcut, live while editing only.
    // The mock keeps modal alerts open over the dialog, and a modal popup on top rightly keeps
    // shortcuts from the dialog, so the shortcut is fired here rather than typed.
    QObject *saveShortcut = dialog->findChild<QObject *>(QStringLiteral("dutyCommunitySaveShortcut"));
    QVERIFY(saveShortcut && saveShortcut->property("enabled").toBool());
    QVERIFY(QMetaObject::invokeMethod(saveShortcut, "activated"));
    QTRY_COMPARE(saved.count(), 1);
    item = qmlMap(saved.takeFirst().at(0)).value(QStringLiteral("content")).toMap();
    QCOMPARE(item.value(QStringLiteral("boss")).toString(), s("尾王"));
    QCOMPARE(item.value(QStringLiteral("phase")).toString(), QString());
    QCOMPARE(item.value(QStringLiteral("game_version")).toString(), QStringLiteral("7.2-"));
    QCOMPARE(item.value(QStringLiteral("role")).toString(), QStringLiteral("PLD,WAR"));
    QTRY_VERIFY(dialog->property("mode").toString() == QStringLiteral("view"));
    QTRY_COMPARE(revisionNow(), 3);
    QTest::qWait(50);

    // A version written by hand is shown under the range and can be cleared, as can 职业. An
    // edited BOSS / 阶段 longer than the boss may be is refused under the field.
    content.insert(QStringLiteral("game_version"), s("7.x 以后"));
    content.insert(QStringLiteral("boss"), QString(45, QLatin1Char('B')));
    content.insert(QStringLiteral("phase"), QString(40, QLatin1Char('P')));
    QVERIFY(QMetaObject::invokeMethod(dialog, "startEdit", Q_ARG(QVariant, rowOf(content))));
    QTRY_VERIFY(field("dutyCommunityEditorVersionByHand"));
    QVERIFY(visibleText(field("dutyCommunityEditorVersionByHand"), s("原来写的是「7.x 以后」")));
    QTRY_COMPARE(field("dutyCommunityEditorVersionFrom")->property("currentValue").toString(), QString());
    for (const char *byHand : {"dutyCommunityEditorVersionByHand", "dutyCommunityEditorRoleByHand"}) {
        // Clearing the first row moves the second up. Under load the layout can still be settling
        // when the click is aimed (the 1.10.0-beta.2 package run missed it), so wait until 清除
        // stays put between two polls before clicking where it is.
        QPointF aim(-1, -1);
        QVERIFY2(QTest::qWaitFor([&] {
            QQuickItem *clear = visibleText(field(byHand), s("清除"));
            if (!clear) return false;
            const QPointF now = clear->mapToScene(QPointF(clear->width() / 2, clear->height() / 2));
            const bool settled = now == aim;
            aim = now;
            return settled;
        }), byHand);
        QTest::mouseClick(scene.mainWindow(), Qt::LeftButton, {}, aim.toPoint());
        QTRY_VERIFY2(!field(byHand), byHand);
    }
    QQuickItem *stage = field("dutyCommunityEditorStage");
    const QString joined = stage->property("text").toString();
    stage->setProperty("text", joined.left(joined.size() - 1) + QLatin1Char('!'));
    QVERIFY(scene.click(QStringLiteral("dutyCommunityEditorSave")));
    QCOMPARE(saved.count(), 0);
    QVERIFY(field("dutyCommunityEditorStageProblem"));
    stage->setProperty("text", s("尾王"));
    QVERIFY(scene.click(QStringLiteral("dutyCommunityEditorSave")));
    QCOMPARE(saved.count(), 1);
    item = qmlMap(saved.takeFirst().at(0)).value(QStringLiteral("content")).toMap();
    QCOMPARE(item.value(QStringLiteral("game_version")).toString(), QString());
    QCOMPARE(item.value(QStringLiteral("role")).toString(), QString());
    QTRY_VERIFY(dialog->property("mode").toString() == QStringLiteral("view"));
    QTRY_COMPARE(revisionNow(), 4);
    QTest::qWait(50);

    // A 职能 chosen whole is kept as the group, so later jobs count too (Design: 职业怎么存). A
    // backwards range already stored does not stop other edits.
    content.insert(QStringLiteral("game_version"), QStringLiteral("7.3-6.0"));
    QVERIFY(QMetaObject::invokeMethod(dialog, "startEdit", Q_ARG(QVariant, rowOf(content))));
    QTRY_VERIFY(field("dutyCommunityEditorJobs"));
    jobs = field("dutyCommunityEditorJobs");
    for (const char *tank : {"PLD", "WAR", "DRK", "GNB"})
        QVERIFY(QMetaObject::invokeMethod(jobs, "toggle", Q_ARG(QVariant, QString::fromLatin1(tank))));
    QVERIFY(QMetaObject::invokeMethod(jobs, "toggle", Q_ARG(QVariant, QStringLiteral("HEALER"))));
    QCOMPARE(jobs->property("displayText").toString(), s("坦克、治疗"));
    QVERIFY(QMetaObject::invokeMethod(jobs, "toggle", Q_ARG(QVariant, QStringLiteral("WHM"))));
    QVERIFY(!field("dutyCommunityEditorVersionProblem"));

    // 插入: a command starts a line or takes an empty one, a placeholder goes at the cursor.
    QQuickItem *bodyEdit = field("dutyCommunityEditorBodyEdit");
    field("dutyCommunityEditorBody")->setProperty("text", QString());
    QVERIFY(QMetaObject::invokeMethod(bodyEdit, "insert", Q_ARG(QVariant, QStringLiteral("/p")), Q_ARG(QVariant, true)));
    QVERIFY(QMetaObject::invokeMethod(bodyEdit, "insert", Q_ARG(QVariant, QStringLiteral("<se.1>")), Q_ARG(QVariant, false)));
    QVERIFY(QMetaObject::invokeMethod(bodyEdit, "insert", Q_ARG(QVariant, QStringLiteral("/e")), Q_ARG(QVariant, true)));
    QCOMPARE(field("dutyCommunityEditorBody")->property("text").toString(), QStringLiteral("/p <se.1>\n/e "));

    QVERIFY(scene.click(QStringLiteral("dutyCommunityEditorSave")));
    QCOMPARE(saved.count(), 1);
    item = qmlMap(saved.takeFirst().at(0)).value(QStringLiteral("content")).toMap();
    QCOMPARE(item.value(QStringLiteral("role")).toString(), QStringLiteral("TANK,SCH,AST,SGE"));
    QCOMPARE(item.value(QStringLiteral("game_version")).toString(), QStringLiteral("7.3-6.0"));
    QTRY_VERIFY(dialog->property("mode").toString() == QStringLiteral("view"));
    QTRY_COMPARE(revisionNow(), 5);
    QTest::qWait(50);

    // Esc goes back to the list without saving (Design: Esc 或左上角回到列表).
    QVERIFY(QMetaObject::invokeMethod(dialog, "startEdit", Q_ARG(QVariant, rowOf(content))));
    QTRY_VERIFY(field("dutyCommunityEditorTitle"));
    QVERIFY(keysToEditor());
    QTest::keyClick(scene.mainWindow(), Qt::Key_Escape);
    QTRY_COMPARE(dialog->property("mode").toString(), QStringLiteral("view"));
    QCOMPARE(saved.count(), 0);
    QVERIFY(dialog->property("visible").toBool());
    QVERIFY(!saveShortcut->property("enabled").toBool());

    // The owner's three cases, as stored and as the detail shows them.
    const auto call = [&](const char *function, const QVariant &first, const QVariant &second = {}) {
        QVariant result;
        const bool called = second.isValid()
            ? QMetaObject::invokeMethod(dialog, function, Q_RETURN_ARG(QVariant, result), Q_ARG(QVariant, first),
                                        Q_ARG(QVariant, second))
            : QMetaObject::invokeMethod(dialog, function, Q_RETURN_ARG(QVariant, result), Q_ARG(QVariant, first));
        return called ? result.toString() : QStringLiteral("<not called>");
    };
    QCOMPARE(call("versionValue", QString(), QStringLiteral("6.0")), QStringLiteral("-6.0"));
    QCOMPARE(call("versionValue", QStringLiteral("6.0"), QStringLiteral("7.3")), QStringLiteral("6.0-7.3"));
    QCOMPARE(call("versionValue", QStringLiteral("7.3"), QString()), QStringLiteral("7.3-"));
    QCOMPARE(call("versionLabel", QStringLiteral("-6.0")), s("到 6.0"));
    QCOMPARE(call("versionLabel", QStringLiteral("6.0-7.3")), s("6.0 → 7.3"));
    QCOMPARE(call("versionLabel", QStringLiteral("7.3-")), s("7.3 → 最新"));
    QCOMPARE(call("versionLabel", s("7.x 以后")), s("7.x 以后"));
    QCOMPARE(call("jobsLabel", QStringLiteral("PLD,WAR")), s("骑士、战士"));
    QCOMPARE(call("jobsLabel", QStringLiteral("TANK,BRD")), s("坦克、吟游诗人"));
    QCOMPARE(call("jobsLabel", QStringLiteral("MT")), QStringLiteral("MT"));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

// A save the Collector refuses comes back into the form with the reason in it, not in the footer
// the form hides. What the form can tell before sending - a macro line over 180 UTF-8 bytes, a
// body over 1600 characters, a hidden direction mark in the title - is said under the field and
// not sent (audit 2026-11-26 R2-1, R1-4).
void DutyCommunityTests::communityEditorSaysWhyASaveIsRefused()
{
    const auto s = [](const char *text) { return QString::fromUtf8(text); };
    Scene scene;
    QObject *dialog = openCommunityDialog(scene);
    QVERIFY2(dialog, qPrintable(scene.errors));
    const auto field = [&](const char *name) { return scene.item(QString::fromLatin1(name), true); };
    // Read at another revision than the one the Collector holds: it answers local_conflict.
    QVariantMap row = qmlList(dialog->property("localRows")).constFirst().toMap();
    row.insert(QStringLiteral("revision"), row.value(QStringLiteral("revision")).toInt() + 1);
    QVERIFY(QMetaObject::invokeMethod(dialog, "startEdit", Q_ARG(QVariant, row)));
    QTRY_VERIFY(field("dutyCommunityEditorSave"));
    QVERIFY(!field("dutyCommunityEditorRefusal"));
    QVERIFY(scene.click(QStringLiteral("dutyCommunityEditorSave")));
    QTRY_VERIFY(field("dutyCommunityEditorRefusal"));
    QCOMPARE(field("dutyCommunityEditorRefusal")->property("text").toString(), s("本地有同一条目或更新的修改，请重新加载。"));
    QCOMPARE(dialog->property("mode").toString(), QStringLiteral("edit"));

    QQuickItem *editor = field("dutyCommunityEditor");
    QSignalSpy saved(editor, SIGNAL(saved(QVariant)));
    QQuickItem *body = field("dutyCommunityEditorBody");
    const auto problem = [&](const char *name) {
        QQuickItem *shown = field(name);
        return shown ? shown->property("text").toString() : QStringLiteral("<not shown>");
    };
    // "/p " and 70 CJK characters: 213 UTF-8 bytes, more than a macro line may hold.
    body->setProperty("text", QStringLiteral("/p ") + QString(70, QChar(0x6C49)));
    QVERIFY(scene.click(QStringLiteral("dutyCommunityEditorSave")));
    QCOMPARE(saved.count(), 0);
    QCOMPARE(problem("dutyCommunityEditorBodyProblem"), s("第 1 行太长：游戏里一行宏最多 180 个字节，大约 60 个汉字"));
    QVERIFY(!field("dutyCommunityEditorRefusal"));
    // Sixteen short lines of 101 characters: 1616 in all.
    QString longBody;
    for (int line = 0; line < 16; ++line)
        longBody += QStringLiteral("/p ") + QString(97, QLatin1Char('a')) + QLatin1Char('\n');
    body->setProperty("text", longBody);
    QVERIFY(scene.click(QStringLiteral("dutyCommunityEditorSave")));
    QCOMPARE(saved.count(), 0);
    QCOMPARE(problem("dutyCommunityEditorBodyProblem"), s("正文最多 1600 字，现在有 1616 字"));
    // A right-to-left override would show the title in another order than it is stored.
    body->setProperty("text", QStringLiteral("/p ok"));
    field("dutyCommunityEditorTitle")->setProperty("text", QStringLiteral("ok") + QChar(0x202E) + QStringLiteral("cba"));
    QVERIFY(scene.click(QStringLiteral("dutyCommunityEditorSave")));
    QCOMPARE(saved.count(), 0);
    QCOMPARE(problem("dutyCommunityEditorTitleProblem"), s("标题里有看不见的特殊字符，请删掉后重新输入"));
    QVERIFY(!field("dutyCommunityEditorBodyProblem"));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

// Answers that arrive after the player moved on leave the new place alone: 投稿进度's answer for
// a number the field no longer shows is dropped, and a save answered after a new form was opened
// does not close that form (audit 2026-11-26 R2-3, R2-5).
void DutyCommunityTests::communityLateRepliesKeepThePlayersPlace()
{
    Scene scene;
    QObject *dialog = openCommunityDialog(scene);
    QVERIFY2(dialog, qPrintable(scene.errors));
    QObject *service = dialog->property("service").value<QObject *>();
    QVERIFY(service);
    dialog->setProperty("issueText", QStringLiteral("128"));
    QVERIFY(QMetaObject::invokeMethod(dialog, "checkIssue"));
    const int asked = dialog->property("serial").toInt();
    // The player types another number, as onTextEdited does, before #128 is answered.
    dialog->setProperty("issueText", QStringLiteral("129"));
    dialog->setProperty("issueState", QString());
    const QVariantMap published{{QStringLiteral("status"), QStringLiteral("published")}};
    QVERIFY(QMetaObject::invokeMethod(service, "answered", Q_ARG(int, asked), Q_ARG(QVariantMap, published),
                                      Q_ARG(QString, QString())));
    QCOMPARE(dialog->property("issueState").toString(), QString());

    // Entry A saved, then 新建宏 before the save is answered: the new form stays open.
    const QVariantMap row = qmlList(dialog->property("localRows")).constFirst().toMap();
    const int revision = row.value(QStringLiteral("revision")).toInt();
    const auto firstRevision = [&] {
        return qmlList(dialog->property("localRows")).constFirst().toMap().value(QStringLiteral("revision")).toInt();
    };
    QVERIFY(QMetaObject::invokeMethod(dialog, "startEdit", Q_ARG(QVariant, row)));
    QTRY_VERIFY(scene.item(QStringLiteral("dutyCommunityEditorSave"), true));
    QVERIFY(scene.click(QStringLiteral("dutyCommunityEditorSave")));
    QCOMPARE(dialog->property("mode").toString(), QStringLiteral("view"));
    QVERIFY(QMetaObject::invokeMethod(dialog, "editNew", Q_ARG(QVariant, QStringLiteral("macro")),
                                      Q_ARG(QVariant, QVariant())));
    QCOMPARE(dialog->property("mode").toString(), QStringLiteral("edit"));
    QTRY_COMPARE(firstRevision(), revision + 1);
    QTest::qWait(50);
    QCOMPARE(dialog->property("mode").toString(), QStringLiteral("edit"));
    QCOMPARE(scene.item(QStringLiteral("dutyCommunityEditorHeading"), true)->property("text").toString(),
             QString::fromUtf8("新建宏"));

    // The save answered before 新建提醒 opened, the list after it (audit 2026-11-26 RE-9): the
    // saved entry is not revealed over the new form.
    dialog->setProperty("mode", QStringLiteral("view"));
    dialog->setProperty("revealId", qmlMap(row.value(QStringLiteral("content"))).value(QStringLiteral("id")));
    QVERIFY(QMetaObject::invokeMethod(dialog, "loadLocal"));
    QVERIFY(QMetaObject::invokeMethod(dialog, "editNew", Q_ARG(QVariant, QStringLiteral("reminder")),
                                      Q_ARG(QVariant, QVariant())));
    QTRY_COMPARE(dialog->property("revealId").toString(), QString());
    QCOMPARE(dialog->property("mode").toString(), QStringLiteral("edit"));
    QCOMPARE(scene.item(QStringLiteral("dutyCommunityEditorHeading"), true)->property("text").toString(),
             QString::fromUtf8("新建提醒"));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void DutyCommunityTests::communityAuthorizationIgnoresSupersededReplies()
{
    Scene scene;
    QObject *dialog = openCommunityDialog(scene);
    QVERIFY2(dialog, qPrintable(scene.errors));
    QObject *submission = dialog->findChild<QObject *>(QStringLiteral("dutyCommunitySubmission"));
    QVERIFY(submission);
    QObject *service = dialog->property("service").value<QObject *>();
    QVERIFY(service);
    dialog->setProperty("mode", QStringLiteral("submit"));
    QTRY_VERIFY(submission->property("visible").toBool());
    const auto ticket = [&] { return dialog->property("serial").toInt(); };
    const auto request = [&](const QString &action) {
        return QMetaObject::invokeMethod(submission, "auth", Q_ARG(QVariant, action));
    };
    const auto reply = [&](int id, const QString &status, QVariantMap result = {}) {
        result.insert(QStringLiteral("status"), status);
        return QMetaObject::invokeMethod(service, "answered", Q_ARG(int, id), Q_ARG(QVariantMap, result),
                                         Q_ARG(QString, QString()));
    };
    const QVariantMap obsoleteAccount{{QStringLiteral("account"), QVariantMap{
        {QStringLiteral("login"), QStringLiteral("obsolete")}, {QStringLiteral("id"), 42}}}};
    const QVariantMap newGrant{{QStringLiteral("user_code"), QStringLiteral("USER-2")}, {QStringLiteral("interval"), 5}};

    QVERIFY(request(QStringLiteral("sign_in")));
    const int firstSignIn = ticket();
    QVERIFY(submission->property("authorizing").toBool());
    QVERIFY(QMetaObject::invokeMethod(submission, "cancelAuth"));
    const int firstCancel = ticket();
    const auto beforeLateReply = dialog->property("note");
    QVERIFY(reply(firstSignIn, QStringLiteral("signed_in"), obsoleteAccount));
    QVERIFY(qmlMap(submission->property("account")).isEmpty());
    QCOMPARE(dialog->property("note"), beforeLateReply);

    QVERIFY(request(QStringLiteral("sign_in")));
    QVERIFY(reply(ticket(), QStringLiteral("authorizing"), newGrant));
    QCOMPARE(submission->property("code").toString(), QStringLiteral("USER-2"));
    const auto authorizingNote = dialog->property("note");
    QVERIFY(reply(firstCancel, QStringLiteral("cancelled")));
    QCOMPARE(dialog->property("note"), authorizingNote);
    QCOMPARE(submission->property("code").toString(), QStringLiteral("USER-2"));

    QVERIFY(request(QStringLiteral("poll")));
    QVERIFY(reply(ticket(), QStringLiteral("temporarily_unavailable")));
    QVERIFY(submission->property("authorizing").toBool());
    QCOMPARE(submission->property("code").toString(), QStringLiteral("USER-2"));

    QVERIFY(request(QStringLiteral("poll")));
    const int poll = ticket();
    QVERIFY(request(QStringLiteral("sign_out")));
    const int signOut = ticket();
    QVERIFY(reply(poll, QStringLiteral("signed_in"), obsoleteAccount));
    QVERIFY(reply(signOut, QStringLiteral("signed_out")));
    QVERIFY(qmlMap(submission->property("account")).isEmpty());
    QVERIFY(submission->property("code").toString().isEmpty());

    QVERIFY(request(QStringLiteral("sign_in")));
    const int hiddenSignIn = ticket();
    dialog->setProperty("mode", QStringLiteral("view"));
    QVERIFY(!submission->property("authorizing").toBool());
    QCOMPARE(ticket(), hiddenSignIn + 1);
    QVERIFY(reply(hiddenSignIn, QStringLiteral("authorizing"), newGrant));
    QVERIFY(submission->property("code").toString().isEmpty());
    QCOMPARE(ticket(), hiddenSignIn + 1);
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

// 条目很多时 (Design artifact): the directory on the left sets the range and its numbers follow
// the filters; inside a duty the list is grouped by what BOSS / 阶段 means, however it was
// spelt; the public library opens on the player's server and says what that hides; copying a
// local entry puts it under 最近用过; the order chosen is remembered.
void DutyCommunityTests::communityDirectorySetsTheRangeAndGroupsTheList()
{
    const auto s = [](const char *text) { return QString::fromUtf8(text); };
    Scene scene;
    QObject *dialog = openCommunityDialog(scene);
    QVERIFY2(dialog, qPrintable(scene.errors));
    const auto id = [](int number) { return QStringLiteral("00000000-0000-4000-8000-%1").arg(number, 12, 10, QLatin1Char('0')); };
    const auto entry = [](int number, int duty, const char *stage, const char *region = "ALL") {
        QVariantMap row = publicEntry(number, "macro", QByteArray("/p ") + QByteArray::number(number), "", duty);
        QVariantMap content = row.value(QStringLiteral("content")).toMap();
        content.insert(QStringLiteral("boss"), QString::fromUtf8(stage));
        content.insert(QStringLiteral("region"), QString::fromLatin1(region));
        row.insert(QStringLiteral("content"), content);
        return row;
    };
    const auto call = [&](const char *method, const QVariant &argument) {
        return QMetaObject::invokeMethod(dialog, method, Q_ARG(QVariant, argument));
    };
    // What the list draws, as "kind label-or-id".
    const auto drawn = [&] {
        QStringList out;
        for (const QVariant &value : qmlList(dialog->property("listEntries"))) {
            const QVariantMap line = value.toMap();
            const QString kind = line.value(QStringLiteral("kind")).toString();
            out << kind + QLatin1Char(' ') + (kind == QLatin1String("head")
                ? line.value(QStringLiteral("label")).toString() + QLatin1Char(' ') + line.value(QStringLiteral("count")).toString()
                : line.value(QStringLiteral("row")).toMap().value(QStringLiteral("content")).toMap()
                      .value(QStringLiteral("id")).toString().right(3));
        }
        return out;
    };
    dialog->setProperty("tab", QStringLiteral("public"));
    dialog->setProperty("publicRows", QVariantList{
        entry(301, 4, "BOSS 2"), entry(302, 4, "\xE8\x80\x81\xE4\xBA\x8C"), entry(303, 4, ""), entry(304, 0, ""),
        entry(305, 4, "\xE6\xBD\xAE\xE6\xB1\x90\xE6\xB3\xA2"), entry(306, 4, "\xE5\xBC\x80\xE5\x9C\xBA", "GLOBAL")});
    QVERIFY(QMetaObject::invokeMethod(dialog, "showEveryPlace"));

    // 全部 is one flat list; the directory counts each duty.
    QTRY_COMPARE(drawn().size(), 6);
    QVERIFY(!dialog->property("listGrouped").toBool());
    const QVariantMap counts = dialog->property("scopeCounts").toMap().value(QStringLiteral("duty")).toMap();
    QCOMPARE(counts.value(QStringLiteral("4")).toInt(), 5);
    QCOMPARE(counts.value(QStringLiteral("0")).toInt(), 1);
    QVERIFY(scene.item(QStringLiteral("dutyCommunityRail"), true));

    // Inside the duty: 开场, then BOSS 2 with both spellings, the spelling nobody knows as
    // written, 未标阶段 last.
    QVERIFY(call("pickScope", QStringLiteral("d:4")));
    QTRY_COMPARE(drawn(), (QStringList{s("head 开场 1"), s("row 306"), s("head BOSS 2 2"), s("row 301"), s("row 302"),
                                       s("head 潮汐波 1"), s("row 305"), s("head 未标阶段 1"), s("row 303")}));
    QTRY_VERIFY(scene.item(QStringLiteral("dutyCommunityGroupHead"), true));
    QVERIFY(call("toggleListGroup", QStringLiteral("d:4/p:b2")));
    QTRY_COMPARE(drawn().size(), 7);
    // 全选 still takes the folded group's rows.
    QVERIFY(scene.click(QStringLiteral("dutyCommunitySelectAll")));
    QTRY_COMPARE(qmlList(dialog->property("checkedIds")).size(), 5);
    QVERIFY(QMetaObject::invokeMethod(dialog, "foldAll"));
    QTRY_COMPARE(drawn().size(), 4);
    QVERIFY(QMetaObject::invokeMethod(dialog, "foldAll"));
    QTRY_COMPARE(drawn().size(), 9);

    // A region filter hides the other server's entry and the list says so.
    QVERIFY(QMetaObject::invokeMethod(dialog, "setFilter", Q_ARG(QVariant, QStringLiteral("region")),
                                      Q_ARG(QVariant, QVariant(QVariantList{QStringLiteral("CN")}))));
    QTRY_COMPARE(dialog->property("hiddenByPlace").toInt(), 1);
    QTRY_VERIFY(scene.item(QStringLiteral("dutyCommunityShowEveryPlace"), true));
    QVERIFY(scene.click(QStringLiteral("dutyCommunityShowEveryPlace")));
    QTRY_COMPARE(dialog->property("hiddenByPlace").toInt(), 0);

    // A search marks its hit in the title and the directory's numbers become hit counts.
    dialog->setProperty("query", QStringLiteral("Entry 301"));
    QTRY_COMPARE(dialog->property("scopeCounts").toMap().value(QStringLiteral("all")).toInt(), 1);
    QTRY_VERIFY(scene.item(QStringLiteral("dutyCommunityRowTitle"), true));
    QVERIFY(scene.item(QStringLiteral("dutyCommunityRowTitle"), true)->property("text").toString().contains(QStringLiteral("<b>Entry 301</b>")));
    dialog->setProperty("query", QString());

    // 清除筛选 drops the search and 类型 but leaves the public library's 区服 where the player
    // put it (owner, 2026-11-25); with nothing else set the button has nothing to do.
    QVERIFY(QMetaObject::invokeMethod(dialog, "setFilter", Q_ARG(QVariant, QStringLiteral("region")),
                                      Q_ARG(QVariant, QVariant(QVariantList{QStringLiteral("CN")}))));
    QVERIFY(!dialog->property("resettable").toBool());
    QTRY_VERIFY(!scene.item(QStringLiteral("dutyCommunityResetFilters"), true));
    dialog->setProperty("query", QStringLiteral("Entry"));
    QVERIFY(dialog->property("resettable").toBool());
    QVERIFY(QMetaObject::invokeMethod(dialog, "resetFilters"));
    QCOMPARE(dialog->property("query").toString(), QString());
    QCOMPARE(dialog->property("hiddenByPlace").toInt(), 1);

    // The order and the 区服 choice are preferences: both are still there after the saved view
    // is read again.
    QVERIFY(call("setSort", QStringLiteral("title")));
    dialog->setProperty("sortPublic", QStringLiteral("updated"));
    dialog->setProperty("publicPreset", true);
    QVERIFY(QMetaObject::invokeMethod(dialog, "loadView"));
    QCOMPARE(dialog->property("sortPublic").toString(), QStringLiteral("title"));
    QCOMPARE(dialog->property("hiddenByPlace").toInt(), 1);
    QVERIFY(call("setSort", QStringLiteral("updated")));
    QVERIFY(QMetaObject::invokeMethod(dialog, "showEveryPlace"));

    // Folding a group leaves a long list where it was instead of back at the top.
    QVariantList many{entry(400, 4, "BOSS 1")};
    for (int number = 401; number <= 440; ++number)
        many << entry(number, 4, "");
    dialog->setProperty("publicRows", many);
    QTRY_COMPARE(drawn().size(), 43);
    auto *rows = scene.item(QStringLiteral("dutyCommunityRows"), true);
    QVERIFY(rows);
    const qreal top = rows->property("originY").toReal();
    rows->setProperty("contentY", top + 400);
    QVERIFY(call("toggleListGroup", QStringLiteral("d:4/p:b1")));
    QTRY_COMPARE(drawn().size(), 42);
    QTRY_VERIFY(rows->property("contentY").toReal() > rows->property("originY").toReal() + 200);

    // 最近用过 holds what was copied, on the local tab only.
    dialog->setProperty("tab", QStringLiteral("local"));
    const QString local = rowIds(dialog->property("localRows")).constFirst();
    QVERIFY(call("pickScope", QStringLiteral("recent")));
    QTRY_COMPARE(qmlList(dialog->property("listRows")).size(), 0);
    QTRY_VERIFY(visibleText(scene.mainWindow()->contentItem(), s("复制过的内容会出现在这里。")));
    QVERIFY(call("markUsed", local));
    QTRY_COMPARE(rowIds(dialog->property("scopeRows")), QStringList{local});
    dialog->setProperty("recent", QVariantMap());
    QVERIFY(QMetaObject::invokeMethod(dialog, "saveView"));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

// The app and the publishing workflow must call the same text the same, or the app would hold
// back what the workflow publishes. Both run tests/Fixtures/shared-calibration/
// community-text-vectors.json (the publisher in test_community_publish.py), here through the
// QML engine's own JavaScript, so the engine's string functions are what is tested.
void DutyCommunityTests::communityTextRulesMatchThePublisher()
{
    QFile script(QStringLiteral(MR_DESKTOP_QML_DIR "/dialogs/DutyCommunityMatch.js"));
    QVERIFY(script.open(QIODevice::ReadOnly));
    QString source = QString::fromUtf8(script.readAll());
    source.replace(QStringLiteral(".pragma library"), QString());
    QJSEngine engine;
    const QJSValue loaded = engine.evaluate(source);
    QVERIFY2(!loaded.isError(), qPrintable(loaded.toString()));
    QFile file(QStringLiteral(MR_SOURCE_DIR "/tests/Fixtures/shared-calibration/community-text-vectors.json"));
    QVERIFY(file.open(QIODevice::ReadOnly));
    const QJsonObject vectors = QJsonDocument::fromJson(file.readAll()).object();
    const auto call = [&](const char *name, const QJSValueList &args) {
        const QJSValue result = engine.globalObject().property(QString::fromLatin1(name)).call(args);
        return result;
    };
    for (const QJsonValue &pair : vectors.value(QStringLiteral("exact_text")).toArray()) {
        const QJsonArray item = pair.toArray();
        QCOMPARE(call("exactText", {item.at(0).toString()}).toString(), item.at(1).toString());
    }
    for (const QJsonValue &pair : vectors.value(QStringLiteral("loose_text")).toArray()) {
        const QJsonArray item = pair.toArray();
        QCOMPARE(call("looseText", {item.at(0).toString()}).toString(), item.at(1).toString());
    }
    for (const QJsonValue &triple : vectors.value(QStringLiteral("similarity")).toArray()) {
        const QJsonArray item = triple.toArray();
        const QJSValue first = call("looseText", {item.at(0).toString()});
        const QJSValue second = call("looseText", {item.at(1).toString()});
        const double value = call("similarity", {first, second}).toNumber();
        QVERIFY2(qAbs(value - item.at(2).toDouble()) < 1e-6,
                 qPrintable(QStringLiteral("%1 / %2: %3").arg(item.at(0).toString(), item.at(1).toString())
                                .arg(value)));
    }
    // What a submission leaves out as a repeat (audit 2026-11-26 R2-10) and what a text field may
    // hold (R1-4): the publisher and the Collector run the same cases.
    const QJsonArray repeats = vectors.value(QStringLiteral("repeats")).toArray();
    QVERIFY(!repeats.isEmpty());
    for (const QJsonValue &value : repeats) {
        const QJsonObject testCase = value.toObject();
        QVariantMap library;
        for (const QJsonValue &entry : testCase.value(QStringLiteral("library")).toArray())
            library.insert(entry.toObject().value(QStringLiteral("id")).toString(), entry.toObject().toVariantMap());
        const QJSValue drafts = engine.toScriptValue(testCase.value(QStringLiteral("drafts")).toArray().toVariantList());
        const QVariantMap found = call("repeats", {drafts, engine.toScriptValue(library)}).toVariant().toMap();
        QVariantMap twins;
        for (auto twin = found.cbegin(); twin != found.cend(); ++twin)
            twins.insert(twin.key(), twin.value().toMap().value(QStringLiteral("id")));
        const QString name = testCase.value(QStringLiteral("name")).toString();
        QVERIFY2(twins == testCase.value(QStringLiteral("expected")).toObject().toVariantMap(), qPrintable(name));
    }
    const QJsonArray fields = vectors.value(QStringLiteral("field_text")).toArray();
    QVERIFY(!fields.isEmpty());
    for (const QJsonValue &value : fields) {
        const QJsonArray item = value.toArray();
        const bool accepted = call("validText", {item.at(0).toString(), item.at(1).toBool()}).toBool();
        QVERIFY2(accepted == item.at(2).toBool(),
                 qPrintable(item.at(0).toString() + (item.at(1).toBool() ? QStringLiteral(" (multi-line)")
                                                                          : QStringLiteral(" (single line)"))));
    }
}

// The float's 攻略 button is an icon alone, so its tooltip is the sentence that says where it
// goes, not the caption with an arrow a text button shows (audit 2026-11-26 L3-1).
void DutyCommunityTests::overlayGuideTipSaysWhereItGoes()
{
    Scene scene;
    scene.backend.setLiveMode(mr::MockBackend::LiveMode::None);
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    scene.overlay()->toggleModule(QStringLiteral("current_duty"));
    QVERIFY(scene.showFloating());
    QTRY_VERIFY(scene.item(QStringLiteral("floatingDutyGuide"), true));
    QCOMPARE(scene.item(QStringLiteral("floatingDutyGuide"), true)->property("tip").toString(),
             QString::fromUtf8("在浏览器打开「新大陆见闻录」的攻略目录"));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

MR_DESKTOP_TEST_SUITE(DutyCommunity)
{
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QApplication app(argc, argv);
    if (!mr::registerBundledFonts()) return 2;
    QApplication::setFont(QFont(QStringLiteral("Noto Sans SC")));
    DutyCommunityTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "DutyCommunityTests.moc"
