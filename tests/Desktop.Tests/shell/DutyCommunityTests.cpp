// 副本提醒与导随宏: the community dialog opened from 总览 on the shipping main window - its
// blank states, the example macros, the editor, the ticked entries, the submission and the
// public library - and the text rules it shares with the publishing workflow.
//
// The scene is the statistics float's (StatisticsSceneFixture.h), opened on Main.qml.

#include "DesktopTestRunner.h"
#include "StatisticsSceneFixture.h"
#include "TestCollectorGuard.h"
#include "BundledFonts.h"

#include <QApplication>
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
    void communityDetailActionsTakeTheTickedEntries();
    void communitySubmissionLeavesOutWhatIsAlreadyPublic();
    void communityPublicLibraryFoldsAndFilters();
    void communityTextRulesMatchThePublisher();
};
} // namespace

void DutyCommunityTests::initTestCase()
{
    QVERIFY(QDir().mkpath(QString::fromUtf8(MR_SOURCE_DIR) + kEvidence()));
    registerSceneQmlTypes();
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

    // Creating happens in the right pane; leaving the tab drops the unsaved form.
    QVERIFY(QMetaObject::invokeMethod(dialog, "editNew", Q_ARG(QVariant, QStringLiteral("macro")),
                                      Q_ARG(QVariant, QVariant())));
    QCOMPARE(dialog->property("mode").toString(), QStringLiteral("edit"));
    dialog->setProperty("tab", QStringLiteral("public"));
    QCOMPARE(dialog->property("mode").toString(), QStringLiteral("view"));
    QVERIFY(dialog->property("remote").toBool());
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

namespace {
/// The visible Text under `parent` that shows exactly `line`.
QQuickItem *visibleText(QQuickItem *parent, const QString &line)
{
    if (!parent) return nullptr;
    if (parent->inherits("QQuickText") && parent->isVisible() && parent->property("text").toString() == line)
        return parent;
    for (auto *child : parent->childItems())
        if (auto *match = visibleText(child, line)) return match;
    return nullptr;
}

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

QVariantMap qmlMap(const QVariant &value)
{
    return value.metaType() == QMetaType::fromType<QJSValue>()
        ? value.value<QJSValue>().toVariant().toMap() : value.toMap();
}

/// The entry ids of a list of { content } rows.
QStringList rowIds(const QVariant &value)
{
    QStringList out;
    for (const QVariant &row : qmlList(value))
        out << row.toMap().value(QStringLiteral("content")).toMap().value(QStringLiteral("id")).toString();
    return out;
}

/// A published community entry, as the public library hands it to the dialog.
QVariantMap publicEntry(int number, const char *type, const char *body, const char *role, int contentId)
{
    const auto id = QStringLiteral("00000000-0000-4000-8000-%1").arg(number, 12, 10, QLatin1Char('0'));
    return {{QStringLiteral("content"), QVariantMap{
        {QStringLiteral("schema_version"), 1}, {QStringLiteral("id"), id},
        {QStringLiteral("type"), QString::fromLatin1(type)}, {QStringLiteral("content_id"), contentId},
        {QStringLiteral("region"), QStringLiteral("ALL")}, {QStringLiteral("language"), QStringLiteral("zh-Hans")},
        {QStringLiteral("title"), QStringLiteral("Entry %1").arg(number)}, {QStringLiteral("body"), QString::fromUtf8(body)},
        {QStringLiteral("boss"), QString()}, {QStringLiteral("phase"), QString()},
        {QStringLiteral("role"), QString::fromLatin1(role)}, {QStringLiteral("tags"), QVariantList()},
        {QStringLiteral("game_version"), QString()}, {QStringLiteral("links"), QVariantList()},
        {QStringLiteral("source"), QString()}, {QStringLiteral("status"), QStringLiteral("community")},
        {QStringLiteral("license"), QStringLiteral("CC-BY-4.0")}, {QStringLiteral("revision"), 1},
        {QStringLiteral("issue"), number}, {QStringLiteral("updated_at"), QStringLiteral("2026-11-23T10:00:00Z")},
        {QStringLiteral("author"), QVariantMap{{QStringLiteral("login"), QStringLiteral("first-author")},
                                               {QStringLiteral("id"), 300}}}}}};
}

/// The community dialog opened from the dashboard with its mock entries loaded.
QObject *openCommunityDialog(Scene &scene)
{
    if (!scene.create(QStringLiteral("harendotes"), true, true)) return nullptr;
    if (!QTest::qWaitForWindowExposed(scene.mainWindow())) return nullptr;
    if (!QTest::qWaitFor([&] { return scene.item(QStringLiteral("dashboardDutyCommunity"), true) != nullptr; }))
        return nullptr;
    if (!scene.click(QStringLiteral("dashboardDutyCommunity"))) return nullptr;
    auto *dialog = scene.root->findChild<QObject *>(QStringLiteral("dutyCommunityDialog"));
    // Both lists answered, so neither can overwrite rows a test sets afterwards.
    if (!dialog || !QTest::qWaitFor([&] { return dialog->property("visible").toBool()
                                                 && rowIds(dialog->property("localRows")).size() >= 5
                                                 && !dialog->property("libraryStatus").toString().isEmpty(); }))
        return nullptr;
    if (QObject *alert = scene.root->findChild<QObject *>(QStringLiteral("automaticRecordingAlert")))
        QMetaObject::invokeMethod(alert, "close");
    QMetaObject::invokeMethod(dialog, "resetFilters");
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
    const auto rowOf = [](const QVariantMap &item) {
        return QVariantMap{{QStringLiteral("content"), item}, {QStringLiteral("revision"), 2},
                           {QStringLiteral("position"), 3}, {QStringLiteral("favorite"), true}};
    };
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
    const auto landed = [&] {
        for (const QVariant &entry : qmlList(dialog->property("localRows")))
            if (qmlMap(qmlMap(entry).value(QStringLiteral("content"))).value(QStringLiteral("id")) == id)
                return true;
        return false;
    };
    QTRY_VERIFY(landed());

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

// 「我勾选 2 个点投稿只给了一个」(owner, 2026-11-23): with two entries ticked, the 投稿 under the
// open entry sent that entry alone. While entries are ticked, 导出 and 投稿 there take the
// ticked entries and say how many; with none ticked they take the open entry.
void DutyCommunityTests::communityDetailActionsTakeTheTickedEntries()
{
    const auto s = [](const char *text) { return QString::fromUtf8(text); };
    const auto ids = [](const QVariant &value) { return rowIds(value); };
    Scene scene;
    QObject *dialog = openCommunityDialog(scene);
    QVERIFY2(dialog, qPrintable(scene.errors));
    const QStringList shown = ids(dialog->property("filtered"));
    QObject *submission = dialog->findChild<QObject *>(QStringLiteral("dutyCommunitySubmission"));
    QVERIFY(submission);
    const auto submitButton = [&] { return scene.item(QStringLiteral("dutyCommunitySubmit"), true); };
    const auto exportButton = [&] { return scene.item(QStringLiteral("dutyCommunityExport"), true); };

    // Nothing ticked: the open entry alone.
    QVERIFY(QMetaObject::invokeMethod(dialog, "select", Q_ARG(QVariant, shown.at(0))));
    QTRY_VERIFY(submitButton());
    QCOMPARE(submitButton()->property("text").toString(), s("投稿"));
    QCOMPARE(exportButton()->property("text").toString(), s("导出"));
    QVERIFY(scene.click(QStringLiteral("dutyCommunitySubmit")));
    QTRY_COMPARE(dialog->property("mode").toString(), QStringLiteral("submit"));
    QCOMPARE(ids(submission->property("selected")), QStringList{shown.at(0)});
    QVERIFY(scene.click(QStringLiteral("dutyCommunitySubmissionCancel")));
    QTRY_COMPARE(dialog->property("mode").toString(), QStringLiteral("view"));

    // Two others ticked while the first is open: both of them, and only them.
    QVERIFY(QMetaObject::invokeMethod(dialog, "toggleChecked", Q_ARG(QVariant, shown.at(1))));
    QVERIFY(QMetaObject::invokeMethod(dialog, "toggleChecked", Q_ARG(QVariant, shown.at(2))));
    QCOMPARE(dialog->property("currentId").toString(), shown.at(0));
    QTRY_COMPARE(submitButton()->property("text").toString(), s("投稿 2 项"));
    QCOMPARE(exportButton()->property("text").toString(), s("导出 2 项"));
    QVERIFY(scene.click(QStringLiteral("dutyCommunitySubmit")));
    QTRY_COMPARE(dialog->property("mode").toString(), QStringLiteral("submit"));
    QCOMPARE(ids(submission->property("selected")), (QStringList{shown.at(1), shown.at(2)}));
    QTRY_VERIFY(scene.item(QStringLiteral("dutyCommunitySubmitBrowser"), true));
    QCOMPARE(scene.item(QStringLiteral("dutyCommunitySubmitBrowser"), true)->property("text").toString(),
             s("复制投稿（2 项）并打开浏览器 ↗"));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

// 「如果宏的内容一致……告诉提交者」(owner, 2026-11-23): what the public library already holds is
// left out before anything is sent, said why, and a link to it points at the public entry. The
// licence reads as what others may do, not as a code.
void DutyCommunityTests::communitySubmissionLeavesOutWhatIsAlreadyPublic()
{
    const auto s = [](const char *text) { return QString::fromUtf8(text); };
    Scene scene;
    QObject *dialog = openCommunityDialog(scene);
    QVERIFY2(dialog, qPrintable(scene.errors));
    QVariantList local = qmlList(dialog->property("localRows"));
    const auto contentOf = [&](int index) { return local.at(index).toMap().value(QStringLiteral("content")).toMap(); };
    const auto idOf = [&](int index) { return contentOf(index).value(QStringLiteral("id")).toString(); };
    // Row 1 (a reminder) links row 2 (a macro); row 4 is a macro that is itself published.
    QVariantMap reminder = local.at(1).toMap();
    QVariantMap reminderContent = contentOf(1);
    reminderContent.insert(QStringLiteral("links"), QVariantList{idOf(2)});
    reminder.insert(QStringLiteral("content"), reminderContent);
    local.replace(1, reminder);
    dialog->setProperty("localRows", local);

    QVariantMap sameText = publicEntry(900, "macro", "", "", 0);
    QVariantMap sameTextContent = sameText.value(QStringLiteral("content")).toMap();
    sameTextContent.insert(QStringLiteral("body"), contentOf(2).value(QStringLiteral("body")).toString() + QStringLiteral("  \n"));
    sameText.insert(QStringLiteral("content"), sameTextContent);
    // A local entry as the player's own published copy of it.
    const auto asPublished = [](QVariantMap content, int issue) {
        content.insert(QStringLiteral("author"), QVariantMap{{QStringLiteral("login"), QStringLiteral("player")},
                                                             {QStringLiteral("id"), 75}});
        content.insert(QStringLiteral("status"), QStringLiteral("community"));
        content.insert(QStringLiteral("license"), QStringLiteral("CC-BY-4.0"));
        content.insert(QStringLiteral("revision"), 1);
        content.insert(QStringLiteral("issue"), issue);
        content.insert(QStringLiteral("updated_at"), QStringLiteral("2026-11-23T10:00:00Z"));
        return QVariantMap{{QStringLiteral("content"), content}};
    };
    dialog->setProperty("publicRows", QVariantList{sameText, asPublished(contentOf(4), 901)});

    const QVariant chosen = QStringList{idOf(1), idOf(4)};
    QVERIFY(QMetaObject::invokeMethod(dialog, "submitRows", Q_ARG(QVariant, chosen)));
    QTRY_COMPARE(dialog->property("mode").toString(), QStringLiteral("submit"));
    QObject *submission = dialog->findChild<QObject *>(QStringLiteral("dutyCommunitySubmission"));
    QVERIFY(submission);
    QCOMPARE(rowIds(submission->property("selected")), (QStringList{idOf(1), idOf(4), idOf(2)}));
    QCOMPARE(rowIds(submission->property("submittable")), QStringList{idOf(1)});
    const QVariantMap twins = qmlMap(submission->property("twins"));
    QCOMPARE(twins.value(idOf(2)).toMap().value(QStringLiteral("kind")).toString(), QStringLiteral("same_text"));
    QCOMPARE(twins.value(idOf(4)).toMap().value(QStringLiteral("kind")).toString(), QStringLiteral("unchanged"));
    QVariant draft;
    QVERIFY(QMetaObject::invokeMethod(submission, "draftFor", Q_RETURN_ARG(QVariant, draft),
                                      Q_ARG(QVariant, qmlList(submission->property("submittable")).at(0))));
    QCOMPARE(qmlMap(draft).value(QStringLiteral("links")).toStringList(),
             QStringList{sameTextContent.value(QStringLiteral("id")).toString()});
    QTRY_VERIFY(scene.item(QStringLiteral("dutyCommunitySubmitBrowser"), true));
    QCOMPARE(scene.item(QStringLiteral("dutyCommunitySubmitBrowser"), true)->property("text").toString(),
             s("复制投稿（1 项）并打开浏览器 ↗"));
    QVERIFY(visibleText(scene.item(QStringLiteral("dutyCommunitySubmission"), true),
                        s("公共库已有相同内容「Entry 900」（first-author），不再投稿")));

    // The licence names what others may do; the code it sends stays the same.
    QQuickItem *license = scene.item(QStringLiteral("dutyCommunityLicense"), true);
    QVERIFY(license);
    QCOMPARE(license->property("displayText").toString(), s("注明作者即可转载、改编（CC BY 4.0）"));
    QCOMPARE(license->property("currentValue").toString(), QStringLiteral("CC-BY-4.0"));

    // Only repeats chosen: nothing to send.
    QVERIFY(scene.click(QStringLiteral("dutyCommunitySubmissionCancel")));
    QTRY_COMPARE(dialog->property("mode").toString(), QStringLiteral("view"));
    QVERIFY(QMetaObject::invokeMethod(dialog, "submitRows", Q_ARG(QVariant, QStringList{idOf(4)})));
    QTRY_COMPARE(dialog->property("mode").toString(), QStringLiteral("submit"));
    scene.item(QStringLiteral("dutyCommunityConsent"), true)->setProperty("checked", true);
    QTRY_VERIFY(!scene.item(QStringLiteral("dutyCommunitySubmitBrowser"), true)->property("enabled").toBool());

    // An edit of one's own published entry that keeps its text goes out even when an older
    // entry shares that text - the publisher takes it as a title change, not a repeat.
    QVERIFY(scene.click(QStringLiteral("dutyCommunitySubmissionCancel")));
    QTRY_COMPARE(dialog->property("mode").toString(), QStringLiteral("view"));
    QVariantMap oldTitle = contentOf(0);
    oldTitle.insert(QStringLiteral("title"), QStringLiteral("Old title"));
    QVariantMap olderTwin = publicEntry(902, "macro", "", "", 0);
    QVariantMap olderTwinContent = olderTwin.value(QStringLiteral("content")).toMap();
    olderTwinContent.insert(QStringLiteral("body"), contentOf(0).value(QStringLiteral("body")));
    olderTwin.insert(QStringLiteral("content"), olderTwinContent);
    dialog->setProperty("publicRows", QVariantList{asPublished(oldTitle, 903), olderTwin});
    QVERIFY(QMetaObject::invokeMethod(dialog, "submitRows", Q_ARG(QVariant, QVariant(QStringList{idOf(0)}))));
    QTRY_COMPARE(dialog->property("mode").toString(), QStringLiteral("submit"));
    QCOMPARE(rowIds(submission->property("submittable")), QStringList{idOf(0)});
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

// The public library folds entries that read alike under the first one (「另有 N 条相似」) and
// can be narrowed to a duty and to a job (owner, 2026-11-23). The two text pairs are the
// publisher's own test cases: 0.96 alike folds, 0.8 does not.
void DutyCommunityTests::communityPublicLibraryFoldsAndFilters()
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
    QVERIFY(scene.item(QStringLiteral("dutyCommunityDutyFilter"), true));
    const auto filteredWith = [&](const char *property, const QVariantList &choice) {
        dialog->setProperty("jobFilter", QVariantList());
        dialog->setProperty("dutyFilter", QVariantList());
        dialog->setProperty(property, choice);
        QStringList out = rowIds(dialog->property("filtered"));
        out.sort();
        return out;
    };
    QCOMPARE(filteredWith("jobFilter", {QStringLiteral("PLD")}), (QStringList{id(202), id(204)}));
    QCOMPARE(filteredWith("jobFilter", {QStringLiteral("TANK")}), (QStringList{id(202), id(204)}));
    QCOMPARE(filteredWith("jobFilter", {QStringLiteral("HEALER")}), QStringList{id(203)});
    QCOMPARE(filteredWith("jobFilter", {QStringLiteral("ANY")}), (QStringList{id(201), id(205)}));
    QCOMPARE(filteredWith("dutyFilter", QVariantList{QVariant(0)}), (QStringList{id(203), id(205)}));
    // 不限 and the five 职能 lead, so a long list of jobs never pushes them out of the panel.
    const QVariantList jobOptions = qmlList(dialog->property("jobOptions"));
    QStringList leading;
    for (int i = 0; i < 6 && i < jobOptions.size(); ++i)
        leading << jobOptions.at(i).toMap().value(QStringLiteral("value")).toString();
    QCOMPARE(leading, (QStringList{QStringLiteral("ANY"), QStringLiteral("TANK"), QStringLiteral("HEALER"),
                                   QStringLiteral("MELEE"), QStringLiteral("RANGED"), QStringLiteral("CASTER")}));
    QVERIFY(dialog->property("filterActive").toBool());

    // The detail names the licence in words too.
    dialog->setProperty("dutyFilter", QVariantList());
    QVERIFY(QMetaObject::invokeMethod(dialog, "select", Q_ARG(QVariant, id(201))));
    QTRY_VERIFY(visibleText(scene.mainWindow()->contentItem(), s("注明作者即可转载、改编（CC BY 4.0）")));

    // The local list has neither filter, and a choice left on the public tab does not narrow it.
    dialog->setProperty("jobFilter", QVariantList{QStringLiteral("PLD")});
    dialog->setProperty("tab", QStringLiteral("local"));
    QTRY_VERIFY(!scene.item(QStringLiteral("dutyCommunityJobFilter"), true));
    QCOMPARE(rowIds(dialog->property("filtered")).size(), rowIds(dialog->property("localRows")).size());
    QVERIFY(!dialog->property("filterActive").toBool());
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
