// 副本提醒与导随宏's shared library tab: entries that read alike folded under the first one,
// the job filter, each entry's 👍 on GitHub, and the 收藏 star of the local tab.
//
// The scene is the statistics float's (StatisticsSceneFixture.h), opened on Main.qml.

#include "DesktopTestRunner.h"
#include "DutyCommunitySceneFixture.h"
#include "TestCollectorGuard.h"
#include "BundledFonts.h"

#include <QApplication>
#include <QColor>
#include <QDesktopServices>
#include <QDir>
#include <QFont>
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
    void favoriteStarIsLucideAndFilledWhenChosen();
public:
    Q_INVOKABLE void rememberUrl(const QUrl &url) { opened << url; }
    QList<QUrl> opened;
};

QString mockId(int number)
{
    return QStringLiteral("00000000-0000-4000-8000-%1").arg(number, 12, 10, QLatin1Char('0'));
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
