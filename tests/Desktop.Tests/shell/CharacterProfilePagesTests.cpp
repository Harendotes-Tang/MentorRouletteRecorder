// ---------------------------------------------------------------------------
// 角色档案 on the pages, in the shipping Main.qml against the offline MockBackend.
//
// A player with one character sees the overview, the history and 设置 › 成就 as they
// were, apart from one 添加角色 button. With a second character the overview card names
// the current one and switches it, 设置 › 成就 edits each character's own numbers, and
// the history shows whose each record is, filters by it and moves checked records.
// ---------------------------------------------------------------------------

#include "DesktopTestRunner.h"
#include "TestCollectorGuard.h"
#include "AppController.h"
#include "AppSettings.h"
#include "Formatters.h"
#include "JobCatalog.h"
#include "MockBackend.h"
#include "RoleCatalog.h"
#include "RunFormValidator.h"
#include "RunListModel.h"

#include <QDir>
#include <QFontDatabase>
#include <QGuiApplication>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QStandardPaths>
#include <QTest>
#include <QUrl>
#include <QtQml/qqml.h>
#include <memory>

namespace {

QQuickItem *findItem(QQuickItem *from, const QString &name, bool visibleOnly)
{
    if (!from || (visibleOnly && !from->isVisible()))
        return nullptr;
    if (from->objectName() == name)
        return from;
    for (QQuickItem *child : from->childItems()) {
        if (QQuickItem *match = findItem(child, name, visibleOnly))
            return match;
    }
    return nullptr;
}

/// The shipping window on the mock backend, onboarding suppressed.
struct Pages {
    mr::AppSettings settings;
    mr::MockBackend backend;
    std::unique_ptr<mr::AppController> app;
    mr::Formatters formatters;
    mr::JobCatalog jobs;
    mr::RoleCatalog roles;
    mr::RunFormValidator validator;
    std::unique_ptr<QQmlEngine> engine;
    std::unique_ptr<QObject> root;
    QString errors;

    ~Pages()
    {
        root.reset();
        engine.reset();
        app.reset();
    }

    bool create()
    {
        app = std::make_unique<mr::AppController>(&backend, nullptr);
        engine = std::make_unique<QQmlEngine>();
        QQmlContext *context = engine->rootContext();
        context->setContextProperty(QStringLiteral("App"), app.get());
        context->setContextProperty(QStringLiteral("Fmt"), &formatters);
        context->setContextProperty(QStringLiteral("Jobs"), &jobs);
        context->setContextProperty(QStringLiteral("Roles"), &roles);
        context->setContextProperty(QStringLiteral("RunForm"), &validator);
        context->setContextProperty(QStringLiteral("Settings"), &settings);
        context->setContextProperty(QStringLiteral("SuppressReleaseNotes"), true);
        context->setContextProperty(QStringLiteral("SuppressOnboarding"), true);
        context->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QQmlComponent component(engine.get(), QUrl::fromLocalFile(
            QString::fromUtf8(MR_DESKTOP_QML_DIR) + QStringLiteral("/Main.qml")));
        root.reset(component.create());
        for (const auto &error : component.errors())
            errors += error.toString() + QLatin1Char('\n');
        if (!root)
            return false;
        app->refreshAll();
        if (!QTest::qWaitFor([this] {
                return !app->characterProfiles().isEmpty() && app->achievementSettingsLoaded()
                    && !app->runs()->isLoading();
            }, 5000) || !QTest::qWaitForWindowExposed(window()))
            return false;
        // The mock's game build has no support file, so the window raises its modal 无法自动记录
        // notice once the status is read; acknowledged here, it leaves the pages to the test.
        QTest::qWaitFor([this] { return shown(QStringLiteral("automaticRecordingAcknowledge")) != nullptr; }, 3000);
        return dismissRecordingNotice();
    }

    bool dismissRecordingNotice() const
    {
        QQuickItem *acknowledge = shown(QStringLiteral("automaticRecordingAcknowledge"));
        if (!acknowledge)
            return true;
        QTest::qWait(50);
        const QPointF centre = acknowledge->mapToScene(QPointF(acknowledge->width() / 2, acknowledge->height() / 2));
        QTest::mouseClick(window(), Qt::LeftButton, Qt::NoModifier, centre.toPoint());
        return QTest::qWaitFor([this] { return shown(QStringLiteral("automaticRecordingAcknowledge")) == nullptr; }, 3000);
    }

    QQuickWindow *window() const { return qobject_cast<QQuickWindow *>(root.get()); }
    /// A popup, or anything else that is an object rather than an item.
    QObject *object(const QString &name) const { return root->findChild<QObject *>(name); }
    /// The item on show under this name; popups' contents live in the window's overlay.
    QQuickItem *shown(const QString &name) const { return findItem(window()->contentItem(), name, true); }
    QQuickItem *any(const QString &name) const { return findItem(window()->contentItem(), name, false); }

    bool click(const QString &name) const
    {
        // Let a popup that just opened, or a page that just came up, finish its layout first.
        QTest::qWait(50);
        QQuickItem *button = shown(name);
        if (!button || !button->isEnabled())
            return false;
        const QPointF centre = button->mapToScene(QPointF(button->width() / 2, button->height() / 2));
        QTest::mouseClick(window(), Qt::LeftButton, Qt::NoModifier, centre.toPoint());
        return true;
    }

    /// 设置 › 成就.
    bool openAchievementSettings() const
    {
        app->navigate(5);
        return QTest::qWaitFor([this] { return shown(QStringLiteral("settingsTab_goal")) != nullptr; }, 3000)
            && click(QStringLiteral("settingsTab_goal"))
            && QTest::qWaitFor([this] { return shown(QStringLiteral("addCharacterButton")) != nullptr; }, 3000);
    }

    QVariantMap profile(int profileId) const { return app->characterProfile(profileId); }
    int currentOf(const QString &region) const
    {
        return app->achievementForRegion(region).value(QStringLiteral("profile_id")).toInt();
    }
    /// What a character picker emits when the player picks a row.
    static bool pick(QQuickItem *selector, int profileId)
    {
        return selector && QMetaObject::invokeMethod(selector, "profilePicked", Q_ARG(int, profileId));
    }
};

class CharacterProfilePagesTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void initTestCase()
    {
        const QString qmlRoot = QString::fromUtf8(MR_DESKTOP_QML_DIR);
        QVERIFY(QDir(qmlRoot).exists());
        qmlRegisterSingletonType(QUrl::fromLocalFile(qmlRoot + QStringLiteral("/Theme.qml")),
                                 "MentorRecorder", 1, 0, "Theme");
        for (const auto &directory : {QStringLiteral("/charts"), QStringLiteral("/components"),
                                      QStringLiteral("/dialogs"), QStringLiteral("/pages")}) {
            for (const auto &file :
                 QDir(qmlRoot + directory).entryList({QStringLiteral("*.qml")}, QDir::Files)) {
                const QByteArray name = file.chopped(4).toUtf8();
                qmlRegisterType(QUrl::fromLocalFile(qmlRoot + directory + QLatin1Char('/') + file),
                                "MentorRecorder", 1, 0, name.constData());
            }
        }
    }

    void historyMigrationRequiresExplicitOverlapAndMovesTheSelectedRecord()
    {
        Pages pages;
        QVERIFY2(pages.create(), qPrintable(pages.errors));
        pages.window()->resize(760, 720);
        pages.app->createCharacterProfile(QStringLiteral("GLOBAL"), QStringLiteral("目标"), 2000, 1444, true);
        int target = 0;
        auto targetProfile = [&] {
            for (const auto &value : pages.app->characterProfilesForRegion(QStringLiteral("GLOBAL"))) {
                const auto profile = value.toMap();
                if (profile.value(QStringLiteral("display_name")).toString() == QStringLiteral("目标"))
                    return profile.value(QStringLiteral("profile_id")).toInt();
            }
            return 0;
        };
        QTRY_VERIFY((target = targetProfile()) > 0);
        const QString note = QStringLiteral("迁移验证笔记");
        pages.app->createManualRun({{QStringLiteral("region"), QStringLiteral("CN")},
                                    {QStringLiteral("result"), QStringLiteral("COMPLETED")},
                                    {QStringLiteral("matched_at_utc"), QStringLiteral("2026-11-18T07:50:00.000Z")},
                                    {QStringLiteral("contributes_to_goal"), true},
                                    {QStringLiteral("note"), note}}, QStringLiteral("迁移验证"));
        pages.app->navigate(1);
        QVariantMap run;
        auto findRecord = [&] {
            for (int index = 0; index < pages.app->runs()->rowCount(); ++index) {
                const auto candidate = pages.app->runs()->runAt(index);
                if (candidate.value(QStringLiteral("note")).toString() == note)
                    return candidate;
            }
            return QVariantMap();
        };
        QTRY_VERIFY(!(run = findRecord()).isEmpty());
        pages.app->setHistoryRunChecked(run, true);
        QTRY_VERIFY(pages.shown(QStringLiteral("batchMoveRegionButton")));
        QVERIFY(pages.click(QStringLiteral("batchMoveRegionButton")));
        auto *region = pages.shown(QStringLiteral("historyRegionMigrationRegion"));
        QVERIFY(region && region->setProperty("currentIndex", 2));
        QVERIFY(Pages::pick(pages.shown(QStringLiteral("historyRegionMigrationProfile")), target));
        QVERIFY(pages.click(QStringLiteral("historyRegionMigrationPreview")));
        QTRY_VERIFY(pages.shown(QStringLiteral("historyRegionMigrationConfirm")));
        auto *confirm = pages.shown(QStringLiteral("historyRegionMigrationConfirm"));
        QVERIFY(!confirm->isEnabled());
        auto *overlap = pages.shown(QStringLiteral("historyRegionMigrationOverlap"));
        auto *reason = pages.shown(QStringLiteral("historyRegionMigrationReason"));
        QVERIFY(overlap && reason);
        QVERIFY(reason->setProperty("text", QStringLiteral("迁移验证")));
        QVERIFY(overlap->setProperty("text", QStringLiteral("2")));
        QVERIFY(!confirm->isEnabled());
        QVERIFY(overlap->setProperty("text", QStringLiteral("1")));
        QTRY_VERIFY(confirm->isEnabled());
        QVERIFY(pages.window()->grabWindow().save(QDir::current().filePath(QStringLiteral("history-region-migration.png"))));
        QVERIFY(pages.click(QStringLiteral("historyRegionMigrationConfirm")));
        QTRY_COMPARE(pages.app->checkedHistoryRunCount(), 0);
        QTRY_COMPARE(pages.profile(target).value(QStringLiteral("baseline_completed_count")).toInt(), 1443);
        QTRY_COMPARE(findRecord().value(QStringLiteral("region")).toString(), QStringLiteral("GLOBAL"));
        QCOMPARE(findRecord().value(QStringLiteral("run_id")), run.value(QStringLiteral("run_id")));
    }

    /// Single-character settings expose the name and binding without extra history filters.
    void aPlayerWithOneCharacterCanSeeTheBindingAndRename()
    {
        Pages pages;
        QVERIFY2(pages.create(), qPrintable(pages.errors));
        QVERIFY(!pages.app->characterFeaturesVisible(QString()));

        pages.app->navigate(0);
        QTRY_VERIFY(pages.shown(QStringLiteral("achievementCard")));
        QVERIFY(!pages.shown(QStringLiteral("achievementCharacterSwitch_CN")));
        QVERIFY(!pages.shown(QStringLiteral("achievementUnassigned_CN")));

        pages.app->navigate(1);
        QTRY_VERIFY(pages.shown(QStringLiteral("historyBatchBar")) || pages.any(QStringLiteral("historyBatchBar")));
        QTRY_VERIFY(pages.app->runs()->rowCount() > 0);
        QVERIFY(!pages.shown(QStringLiteral("historyCharacterFilter")));
        QVERIFY(!pages.shown(QStringLiteral("historyCharacterCell")));
        auto *selectAll = pages.shown(QStringLiteral("selectCurrentHistoryPage"));
        QVERIFY(selectAll);
        // HistoryPage applies its measured row capacity on a debounced timer.
        // Compare the settled layout before and after selecting the page.
        auto *historyPage = selectAll->parentItem();
        while (historyPage && !historyPage->property("rowsThatFit").isValid())
            historyPage = historyPage->parentItem();
        QVERIFY(historyPage);
        QTRY_COMPARE(pages.app->runs()->pageSize(), historyPage->property("rowsThatFit").toInt());
        QTRY_VERIFY(!pages.app->runs()->isLoading());
        const int rowCount = pages.app->runs()->rowCount();
        const int pageSize = pages.app->runs()->pageSize();
        auto *header = pages.shown(QStringLiteral("historyTableHeader"));
        QVERIFY(header);
        const qreal headerTop = header->mapToScene(QPointF{}).y();
        QVERIFY(pages.click(QStringLiteral("selectCurrentHistoryPage")));
        QTRY_VERIFY(pages.shown(QStringLiteral("batchSoftDeleteButton")));
        QTRY_COMPARE(pages.app->checkedHistoryRunCount(), rowCount);
        auto *batchBar = pages.shown(QStringLiteral("historyBatchBar"));
        auto *clearFilters = pages.shown(QStringLiteral("historyClearFilters"));
        QVERIFY(batchBar && clearFilters);
        QTRY_VERIFY(batchBar->mapToScene(QPointF{}).x() >
                    clearFilters->mapToScene(QPointF(clearFilters->width(), 0)).x());
        QCOMPARE(batchBar->mapToScene(QPointF{}).y(), clearFilters->mapToScene(QPointF{}).y());
        QCOMPARE(header->mapToScene(QPointF{}).y(), headerTop);
        QCOMPARE(historyPage->property("rowsThatFit").toInt(), pageSize);
        QCOMPARE(pages.app->runs()->pageSize(), pageSize);
        QVERIFY(!pages.shown(QStringLiteral("batchAssignCharacterButton")));
        pages.app->clearCheckedHistoryRuns();

        QVERIFY(pages.openAchievementSettings());
        QVERIFY(!pages.shown(QStringLiteral("achievementCharacterSelector")));
        QVERIFY(pages.shown(QStringLiteral("renameCharacterButton")));
        QVERIFY(pages.shown(QStringLiteral("characterBindingRow")));
        QVERIFY(!pages.shown(QStringLiteral("forgetIdentitiesButton")));
        QVERIFY(pages.shown(QStringLiteral("goalField")));
    }

    // Owner decision 2026-11-19 (audit K1-1): a character that is already bound is only
    // offered a name, in its own words, and the offer can be put off.
    void aBoundCharactersNamingOfferCanBePutOff()
    {
        Pages pages;
        pages.backend.addCharacterIdentity(QStringLiteral("CN"), QStringLiteral("BOUND"), 0, true);
        QVERIFY2(pages.create(), qPrintable(pages.errors));
        pages.app->navigate(0);
        QTRY_VERIFY(pages.shown(QStringLiteral("characterIdentityPrompt_CN")));
        QCOMPARE(pages.shown(QStringLiteral("characterIdentityHeading_CN"))->property("text").toString(),
                 QString::fromUtf8("给「原有记录」起个名字"));
        QVERIFY(!pages.shown(QStringLiteral("characterIdentityNew_CN")));
        QVERIFY(pages.click(QStringLiteral("characterIdentityLater_CN")));
        QTRY_VERIFY(!pages.shown(QStringLiteral("characterIdentityPrompt_CN")));
        QVERIFY(pages.app->pendingCharacterIdentities(QStringLiteral("CN")).isEmpty());
        // The Collector still offers it: the profile can be named from 设置 › 成就.
        QCOMPARE(pages.app->characterIdentities().size(), 1);
    }

    void registrationNamesTheOriginalProfileAndShowsItsBinding_data()
    {
        QTest::addColumn<QString>("state");
        QTest::newRow("first-login") << QStringLiteral("PENDING");
        QTest::newRow("previously-bound") << QStringLiteral("BOUND");
    }

    void registrationNamesTheOriginalProfileAndShowsItsBinding()
    {
        QFETCH(QString, state);
        int invalidations = 0;
        Pages pages;
        pages.backend.addCharacterIdentity(QStringLiteral("CN"), state, 0, true);
        QVERIFY2(pages.create(), qPrintable(pages.errors));
        const int original = pages.currentOf(QStringLiteral("CN"));
        connect(&pages.backend, &mr::IBackend::liveEvent, this, [&invalidations](const QVariantMap &event) {
            if (event.value(QStringLiteral("kind")).toString() == QLatin1String("stats_invalidated"))
                ++invalidations;
        });
        pages.app->navigate(0);
        QTRY_VERIFY(pages.shown(QStringLiteral("characterIdentityPrompt_CN")));
        QCOMPARE(pages.shown(QStringLiteral("characterIdentityNew_CN")) != nullptr, state == QLatin1String("PENDING"));
        QCOMPARE(pages.shown(QStringLiteral("characterIdentityIgnore_CN")) != nullptr, state == QLatin1String("PENDING"));
        QVERIFY(pages.click(QStringLiteral("characterIdentityRegister_CN")));
        QTRY_VERIFY(pages.shown(QStringLiteral("characterNameField")));
        QVERIFY(pages.shown(QStringLiteral("characterNameField"))->setProperty("text", QString::fromUtf8("白露")));
        QVERIFY(pages.click(QStringLiteral("characterProfileSave")));
        QTRY_VERIFY(!pages.shown(QStringLiteral("characterIdentityPrompt_CN")));
        QTRY_VERIFY(pages.shown(QStringLiteral("achievementCharacterName_CN")));
        QCOMPARE(pages.shown(QStringLiteral("achievementCharacterName_CN"))->property("text").toString(),
                 QString::fromUtf8("白露"));
        QCOMPARE(pages.currentOf(QStringLiteral("CN")), original);
        QTRY_COMPARE(invalidations, 1);

        QVERIFY(pages.openAchievementSettings());
        QVERIFY(pages.shown(QStringLiteral("forgetIdentitiesButton")));
        QVERIFY(pages.click(QStringLiteral("renameCharacterButton")));
        QTRY_VERIFY(pages.shown(QStringLiteral("characterNameField")));
        QVERIFY(pages.shown(QStringLiteral("characterNameField"))->setProperty("text", QString::fromUtf8("新名字")));
        QVERIFY(pages.click(QStringLiteral("characterProfileSave")));
        QTRY_COMPARE(pages.profile(original).value(QStringLiteral("display_name")).toString(), QString::fromUtf8("新名字"));
        QCOMPARE(pages.profile(original).value(QStringLiteral("identity_count")).toInt(), 1);
        QTRY_COMPARE(invalidations, 2);
    }

    void decliningRegistrationOffersTheNextCharacter_data()
    {
        QTest::addColumn<QString>("choice");
        QTest::newRow("ignore") << QStringLiteral("ignore");
        QTest::newRow("create") << QStringLiteral("create");
        QTest::newRow("other-profile") << QStringLiteral("bind");
        QTest::newRow("release-previous-binding") << QStringLiteral("forget");
    }

    void decliningRegistrationOffersTheNextCharacter()
    {
        QFETCH(QString, choice);
        Pages pages;
        const QString region = QStringLiteral("CN");
        const bool wasBound = choice == QLatin1String("forget");
        const int declined = pages.backend.addCharacterIdentity(region, wasBound ? QStringLiteral("BOUND")
                                                                                : QStringLiteral("PENDING"), 1, true);
        const int next = pages.backend.addCharacterIdentity(region, QStringLiteral("PENDING"), 1);
        const int other = choice == QLatin1String("bind")
            ? pages.backend.addCharacterProfile(region, QString::fromUtf8("小号"), 2000, 0, false) : 0;
        QVERIFY2(pages.create(), qPrintable(pages.errors));
        const int original = pages.currentOf(region);
        pages.app->navigate(0);
        QTRY_VERIFY(pages.shown(QStringLiteral("characterIdentityRegister_CN")));
        QCOMPARE(pages.shown(QStringLiteral("characterIdentityPrompt_CN"))->property("identityId").toInt(), declined);

        if (wasBound) {
            QVERIFY(pages.openAchievementSettings());
            QVERIFY(pages.click(QStringLiteral("forgetIdentitiesButton")));
            QTRY_COMPARE(pages.profile(original).value(QStringLiteral("identity_count")).toInt(), 0);
            pages.app->navigate(0);
        } else if (choice == QLatin1String("create")) {
            QVERIFY(pages.click(QStringLiteral("characterIdentityNew_CN")));
            QTRY_VERIFY(pages.shown(QStringLiteral("characterNameField")));
            QVERIFY(pages.shown(QStringLiteral("characterNameField"))->setProperty("text", QString::fromUtf8("小号")));
            QVERIFY(pages.click(QStringLiteral("characterProfileSave")));
            QTRY_COMPARE(pages.app->characterProfilesForRegion(region).size(), 2);
        } else if (choice == QLatin1String("bind")) {
            QVERIFY(Pages::pick(pages.shown(QStringLiteral("characterIdentityExisting_CN")), other));
            QVERIFY(pages.click(QStringLiteral("characterIdentityBind_CN")));
            QTRY_COMPARE(pages.profile(other).value(QStringLiteral("identity_count")).toInt(), 1);
        } else {
            QVERIFY(pages.click(QStringLiteral("characterIdentityIgnore_CN")));
            QTRY_COMPARE(pages.app->ignoredCharacterIdentityCount(region), 1);
        }
        QTRY_VERIFY(pages.shown(QStringLiteral("characterIdentityRegister_CN")));
        QTRY_COMPARE(pages.shown(QStringLiteral("characterIdentityPrompt_CN"))->property("identityId").toInt(), next);
        QCOMPARE(pages.shown(QStringLiteral("characterIdentityPrompt_CN"))->property("registrationProfileId").toInt(), original);
        QVERIFY(pages.profile(original).value(QStringLiteral("display_name")).toString().isEmpty());
        QCOMPARE(pages.profile(original).value(QStringLiteral("identity_count")).toInt(), 0);
    }

    /// 添加角色 asks for a name and the character's own numbers; the new character then has
    /// its own row to edit, to make current, to rename and - while nothing belongs to it - to delete.
    void aCharacterIsAddedEditedSwitchedRenamedAndDeletedInAchievementSettings()
    {
        Pages pages;
        QVERIFY2(pages.create(), qPrintable(pages.errors));
        const int original = pages.currentOf(QStringLiteral("CN"));
        const int originalBaseline = pages.app->baselineCount();
        QVERIFY(pages.openAchievementSettings());

        // A name is required before anything is sent.
        QVERIFY(pages.click(QStringLiteral("addCharacterButton")));
        QObject *dialog = pages.object(QStringLiteral("characterProfileDialog"));
        QVERIFY(dialog);
        QTRY_VERIFY(dialog->property("opened").toBool());
        QVERIFY(pages.click(QStringLiteral("characterProfileSave")));
        QTRY_VERIFY(pages.shown(QStringLiteral("characterProfileError")));
        QCOMPARE(pages.app->characterProfilesForRegion(QStringLiteral("CN")).size(), 1);

        QVERIFY(pages.shown(QStringLiteral("characterNameField"))->setProperty("text", QString::fromUtf8("小号")));
        QVERIFY(pages.shown(QStringLiteral("characterBaselineField"))->setProperty("text", QStringLiteral("120")));
        QVERIFY(pages.click(QStringLiteral("characterProfileSave")));
        QTRY_COMPARE(pages.app->characterProfilesForRegion(QStringLiteral("CN")).size(), 2);
        QTRY_VERIFY(!dialog->property("visible").toBool());
        const int added = pages.app->characterProfilesForRegion(QStringLiteral("CN")).last().toMap()
                              .value(QStringLiteral("profile_id")).toInt();
        QCOMPARE(pages.profile(added).value(QStringLiteral("display_name")).toString(), QString::fromUtf8("小号"));
        // Not switched to: records keep going to the character that was current.
        QCOMPARE(pages.currentOf(QStringLiteral("CN")), original);

        // The fields now edit the new character, and say so.
        QTRY_COMPARE(pages.app->baselineProfileId(), added);
        QTRY_VERIFY(pages.shown(QStringLiteral("achievementCharacterSelector")));
        QTRY_COMPARE(pages.shown(QStringLiteral("baselineField"))->property("text").toString(), QStringLiteral("120"));
        QVERIFY(pages.shown(QStringLiteral("achievementCharacterActions")));

        // 设为当前角色.
        QVERIFY(pages.click(QStringLiteral("selectCharacterButton")));
        QTRY_COMPARE(pages.currentOf(QStringLiteral("CN")), added);
        QTRY_VERIFY(!pages.shown(QStringLiteral("selectCharacterButton")));
        QVERIFY(!pages.shown(QStringLiteral("deleteCharacterButton")));

        // 改名.
        QVERIFY(pages.click(QStringLiteral("renameCharacterButton")));
        QTRY_VERIFY(dialog->property("opened").toBool());
        QCOMPARE(pages.shown(QStringLiteral("characterNameField"))->property("text").toString(), QString::fromUtf8("小号"));
        QVERIFY(!pages.shown(QStringLiteral("characterBaselineField")));
        QVERIFY(pages.shown(QStringLiteral("characterNameField"))->setProperty("text", QString::fromUtf8("白露")));
        QVERIFY(pages.click(QStringLiteral("characterProfileSave")));
        QTRY_COMPARE(pages.profile(added).value(QStringLiteral("display_name")).toString(), QString::fromUtf8("白露"));
        QTRY_VERIFY(!dialog->property("visible").toBool());

        // The other character's own numbers are untouched; it still has records, so it stays.
        QVERIFY(Pages::pick(pages.shown(QStringLiteral("achievementCharacterSelector")), original));
        QTRY_COMPARE(pages.app->baselineProfileId(), original);
        QTRY_COMPARE(pages.shown(QStringLiteral("baselineField"))->property("text").toString(),
                     QString::number(originalBaseline));
        QVERIFY(pages.click(QStringLiteral("deleteCharacterButton")));
        QTRY_COMPARE(pages.shown(QStringLiteral("deleteCharacterButton"))->property("text").toString(),
                     QString::fromUtf8("确认删除"));
        QCOMPARE(pages.app->characterProfilesForRegion(QStringLiteral("CN")).size(), 2);
        QVERIFY(pages.click(QStringLiteral("deleteCharacterButton")));
        QTRY_VERIFY(pages.shown(QStringLiteral("characterErrorText")));
        QCOMPARE(pages.app->characterProfilesForRegion(QStringLiteral("CN")).size(), 2);

        // A character nothing belongs to goes at the second press.
        const int empty = pages.backend.addCharacterProfile(QStringLiteral("CN"), QString::fromUtf8("空号"), 2000, 0, false);
        pages.app->refreshDashboard();
        QTRY_COMPARE(pages.app->characterProfilesForRegion(QStringLiteral("CN")).size(), 3);
        QVERIFY(Pages::pick(pages.shown(QStringLiteral("achievementCharacterSelector")), empty));
        QTRY_COMPARE(pages.app->baselineProfileId(), empty);
        QTRY_VERIFY(!pages.shown(QStringLiteral("characterErrorText")));
        QVERIFY(pages.click(QStringLiteral("deleteCharacterButton")));
        QVERIFY(pages.click(QStringLiteral("deleteCharacterButton")));
        QTRY_COMPARE(pages.app->characterProfilesForRegion(QStringLiteral("CN")).size(), 2);
        QVERIFY(pages.profile(empty).isEmpty());
    }

    /// The overview card names the current character and switches it.
    void theOverviewCardSwitchesTheCharacterNewRecordsGoTo()
    {
        Pages pages;
        const int alt = pages.backend.addCharacterProfile(QStringLiteral("CN"), QString::fromUtf8("小号"), 1000, 5, false);
        QVERIFY2(pages.create(), qPrintable(pages.errors));
        const int original = pages.currentOf(QStringLiteral("CN"));
        QVERIFY(original != alt);
        pages.app->navigate(0);

        QTRY_VERIFY(pages.shown(QStringLiteral("achievementCharacterSwitch_CN")));
        QQuickItem *switcher = pages.shown(QStringLiteral("achievementCharacterSwitch_CN"));
        QCOMPARE(switcher->property("selection").toInt(), original);
        // Another region with one character keeps its plain card.
        QVERIFY(!pages.shown(QStringLiteral("achievementCharacterSwitch_TC")));

        // A switch the Collector refuses says so, and the card goes on naming the character in force.
        QVERIFY(Pages::pick(switcher, 9999));
        QTRY_VERIFY2(pages.app->toastMessage().contains(QString::fromUtf8("没有切换成功")),
                     qPrintable(pages.app->toastMessage()));
        QCOMPARE(pages.currentOf(QStringLiteral("CN")), original);
        QTRY_VERIFY(switcher->isEnabled());
        QCOMPARE(switcher->property("currentValue").toInt(), original);

        QVERIFY(Pages::pick(switcher, alt));
        QTRY_COMPARE(pages.currentOf(QStringLiteral("CN")), alt);
        QTRY_COMPARE(pages.shown(QStringLiteral("achievementCharacterSwitch_CN"))->property("selection").toInt(), alt);
        // The card now shows the other character's own goal and progress.
        QCOMPARE(pages.app->achievementForRegion(QStringLiteral("CN")).value(QStringLiteral("goal_count")).toInt(), 1000);
        QCOMPARE(pages.app->achievementForRegion(QStringLiteral("CN")).value(QStringLiteral("achievement_progress")).toInt(), 5);
    }

    /// The history names the character of each record, filters by it, and moves checked records.
    void theHistoryShowsFiltersAndMovesRecordsByCharacter()
    {
        Pages pages;
        const int alt = pages.backend.addCharacterProfile(QStringLiteral("CN"), QString::fromUtf8("小号"), 1000, 0, false);
        QVERIFY2(pages.create(), qPrintable(pages.errors));
        pages.app->navigate(1);
        QTRY_VERIFY(pages.app->runs()->rowCount() > 0);
        QTRY_VERIFY(pages.shown(QStringLiteral("historyCharacterCell")));
        QTRY_VERIFY(pages.shown(QStringLiteral("historyCharacterFilter")));
        const int everyRecord = pages.app->runs()->total();

        // 小号 has no record yet; 未分配 has none either.
        QQuickItem *filter = pages.shown(QStringLiteral("historyCharacterFilter"));
        QVERIFY(Pages::pick(filter, alt));
        QTRY_COMPARE(pages.app->historyFilter().value(QStringLiteral("profile_id")).toInt(), alt);
        QTRY_VERIFY(!pages.app->runs()->isLoading() && pages.app->runs()->total() == 0);
        QVERIFY(Pages::pick(filter, -1));
        QTRY_VERIFY(pages.app->historyFilter().value(QStringLiteral("profile_unassigned")).toBool());
        QVERIFY(!pages.app->historyFilter().contains(QStringLiteral("profile_id")));
        QVERIFY(Pages::pick(filter, 0));
        QTRY_VERIFY(!pages.app->historyFilter().contains(QStringLiteral("profile_unassigned")));
        QTRY_VERIFY(!pages.app->runs()->isLoading() && pages.app->runs()->total() == everyRecord);
        // A character belongs to one region: picking another region drops the character filter
        // instead of asking that region for somebody else's character.
        QVERIFY(Pages::pick(filter, alt));
        QTRY_COMPARE(pages.app->historyFilter().value(QStringLiteral("profile_id")).toInt(), alt);
        QQuickItem *regions = pages.shown(QStringLiteral("historyRegionFilter"));
        QVERIFY(regions && QMetaObject::invokeMethod(regions, "regionPicked", Q_ARG(QString, QStringLiteral("TC"))));
        QTRY_COMPARE(pages.app->historyFilter().value(QStringLiteral("region")).toString(), QStringLiteral("TC"));
        QVERIFY(!pages.app->historyFilter().contains(QStringLiteral("profile_id")));
        // 繁中服 has one character: the picker is gone and nothing of it stays in force.
        QTRY_VERIFY(!pages.shown(QStringLiteral("historyCharacterFilter")));
        QVERIFY(QMetaObject::invokeMethod(regions, "regionPicked", Q_ARG(QString, QString())));
        QTRY_VERIFY(!pages.app->historyFilter().contains(QStringLiteral("region")));
        QTRY_VERIFY(!pages.app->runs()->isLoading() && pages.app->runs()->total() == everyRecord);
        QTRY_VERIFY(pages.shown(QStringLiteral("historyCharacterFilter")));

        // 改角色: nothing is chosen for the player, and the checked record moves once one is.
        pages.app->setHistoryRunChecked(pages.app->runs()->runAt(0), true);
        QTRY_VERIFY(pages.shown(QStringLiteral("batchAssignCharacterButton")));
        QVERIFY(pages.click(QStringLiteral("batchAssignCharacterButton")));
        QObject *dialog = pages.object(QStringLiteral("historyAssignCharacterDialog"));
        QVERIFY(dialog);
        QTRY_VERIFY(dialog->property("opened").toBool());
        // The list follows the ticked record's region, not the page's 全部区服 (audit K3-3).
        QVERIFY(!pages.app->historyFilter().contains(QStringLiteral("region")));
        QCOMPARE(dialog->property("region").toString(),
                 pages.app->runs()->runAt(0).value(QStringLiteral("region")).toString());
        QVERIFY(!dialog->property("mixedRegions").toBool());
        QTRY_VERIFY(pages.shown(QStringLiteral("historyAssignCharacterConfirm")));
        QVERIFY(!pages.shown(QStringLiteral("historyAssignCharacterConfirm"))->isEnabled());
        QVERIFY(Pages::pick(pages.shown(QStringLiteral("historyAssignCharacterTarget")), alt));
        QTRY_VERIFY(pages.shown(QStringLiteral("historyAssignCharacterConfirm"))->isEnabled());
        QVERIFY(pages.click(QStringLiteral("historyAssignCharacterConfirm")));
        QTRY_COMPARE(pages.app->checkedHistoryRunCount(), 0);
        QTRY_VERIFY(!dialog->property("visible").toBool());
        QTRY_COMPARE(pages.profile(alt).value(QStringLiteral("run_count")).toInt(), 1);
    }

    /// A character the Collector told apart and nobody has answered for is asked about on the
    /// region's card. With one character there, one button says "it is that one"; the records
    /// made for it then count, and the card is the plain card again.
    void aCharacterNobodyAnsweredForIsAskedAboutOnTheOverviewCard()
    {
        Pages pages;
        const int identity = pages.backend.addCharacterIdentity(QStringLiteral("CN"), QStringLiteral("PENDING"), 2);
        QVERIFY(identity > 0);
        QVERIFY2(pages.create(), qPrintable(pages.errors));
        const int only = pages.currentOf(QStringLiteral("CN"));
        const int runsBefore = pages.profile(only).value(QStringLiteral("run_count")).toInt();
        pages.app->navigate(0);

        QTRY_VERIFY(pages.shown(QStringLiteral("characterIdentityPrompt_CN")));
        QVERIFY(!pages.shown(QStringLiteral("characterIdentityPrompt_TC")));
        QVERIFY(pages.shown(QStringLiteral("characterIdentityBody_CN"))->property("text").toString()
                    .contains(QString::fromUtf8("2 条记录")));
        // One character: no list to pick from, the button names it.
        QVERIFY(!pages.shown(QStringLiteral("characterIdentityExisting_CN")));
        QVERIFY(pages.shown(QStringLiteral("characterIdentityOnly_CN"))->property("text").toString()
                    .contains(QString::fromUtf8("原有记录")));

        QVERIFY(pages.click(QStringLiteral("characterIdentitySame_CN")));

        QTRY_VERIFY(!pages.shown(QStringLiteral("characterIdentityPrompt_CN")));
        QTRY_COMPARE(pages.profile(only).value(QStringLiteral("run_count")).toInt(), runsBefore + 2);
        QCOMPARE(pages.profile(only).value(QStringLiteral("identity_count")).toInt(), 1);
        QTRY_VERIFY(!pages.app->characterFeaturesVisible(QStringLiteral("CN")));
    }

    /// With several characters the player picks which one it is, or names a new one in the
    /// dialog 添加角色 uses - which then neither offers nor needs "switch to it now".
    void anUnansweredCharacterBecomesAnExistingOrANewCharacter()
    {
        Pages pages;
        const int alt = pages.backend.addCharacterProfile(QStringLiteral("CN"), QString::fromUtf8("小号"), 1000, 0, false);
        pages.backend.addCharacterIdentity(QStringLiteral("CN"), QStringLiteral("PENDING"), 1);
        pages.backend.addCharacterIdentity(QStringLiteral("CN"), QStringLiteral("PENDING"), 1);
        QVERIFY2(pages.create(), qPrintable(pages.errors));
        // One is asked about at a time, in the order the Collector lists them.
        const QVariantList waiting = pages.app->pendingCharacterIdentities(QStringLiteral("CN"));
        QCOMPARE(waiting.size(), 2);
        const int first = waiting.at(0).toMap().value(QStringLiteral("identity_id")).toInt();
        const int second = waiting.at(1).toMap().value(QStringLiteral("identity_id")).toInt();
        // The character being answered about is the one in the game, so its profile becomes current.
        pages.backend.setLoggedInCharacterIdentity(first);
        pages.app->navigate(0);

        QTRY_VERIFY(pages.shown(QStringLiteral("characterIdentityPrompt_CN")));
        QCOMPARE(pages.shown(QStringLiteral("characterIdentityPrompt_CN"))->property("identityId").toInt(), first);
        QVERIFY(!pages.shown(QStringLiteral("characterIdentitySame_CN")));
        // Nothing is bound until a character is picked.
        QVERIFY(!pages.shown(QStringLiteral("characterIdentityBind_CN")));
        QVERIFY(Pages::pick(pages.shown(QStringLiteral("characterIdentityExisting_CN")), alt));
        QTRY_VERIFY(pages.shown(QStringLiteral("characterIdentityBind_CN")));
        QVERIFY(pages.click(QStringLiteral("characterIdentityBind_CN")));

        QTRY_COMPARE(pages.profile(alt).value(QStringLiteral("identity_count")).toInt(), 1);
        QTRY_COMPARE(pages.currentOf(QStringLiteral("CN")), alt);
        // The next one is asked about, with nothing carried over from the last.
        QTRY_COMPARE(pages.shown(QStringLiteral("characterIdentityPrompt_CN"))->property("identityId").toInt(), second);
        QVERIFY(!pages.shown(QStringLiteral("characterIdentityBind_CN")));

        pages.backend.setLoggedInCharacterIdentity(second);
        QVERIFY(pages.click(QStringLiteral("characterIdentityNew_CN")));
        QTRY_VERIFY(pages.shown(QStringLiteral("characterNameField")));
        QVERIFY(pages.shown(QStringLiteral("characterIdentityHint")));
        QVERIFY(!pages.shown(QStringLiteral("characterMakeCurrentSwitch")));
        QVERIFY(pages.shown(QStringLiteral("characterNameField"))->setProperty("text", QString::fromUtf8("第三个")));
        QVERIFY(pages.click(QStringLiteral("characterProfileSave")));

        QTRY_VERIFY(!pages.shown(QStringLiteral("characterIdentityPrompt_CN")));
        // The prompt goes as soon as the answer is in; the current character follows with the next read.
        QTRY_VERIFY(pages.currentOf(QStringLiteral("CN")) != alt);
        const int created = pages.currentOf(QStringLiteral("CN"));
        QCOMPARE(pages.profile(created).value(QStringLiteral("display_name")).toString(), QString::fromUtf8("第三个"));
        QCOMPARE(pages.profile(created).value(QStringLiteral("identity_count")).toInt(), 1);
        // The card now says how the region finds a record's character.
        QTRY_VERIFY(pages.shown(QStringLiteral("achievementIdentification_CN")));
    }

    /// 不统计 stops the question; 设置 › 成就 says so and can ask again. The same tab switches
    /// identification off and releases what a character was recognised as.
    void ignoringForgettingAndTheSwitchLiveInAchievementSettings()
    {
        Pages pages;
        const int alt = pages.backend.addCharacterProfile(QStringLiteral("CN"), QString::fromUtf8("小号"), 1000, 0, false);
        pages.backend.addCharacterIdentity(QStringLiteral("CN"), QStringLiteral("PENDING"), 1);
        pages.backend.addCharacterIdentity(QStringLiteral("CN"), QStringLiteral("PENDING"), 0);
        QVERIFY2(pages.create(), qPrintable(pages.errors));
        const QVariantList waiting = pages.app->pendingCharacterIdentities(QStringLiteral("CN"));
        QCOMPARE(waiting.size(), 2);
        const int guest = waiting.at(0).toMap().value(QStringLiteral("identity_id")).toInt();
        const int mine = waiting.at(1).toMap().value(QStringLiteral("identity_id")).toInt();
        pages.backend.setLoggedInCharacterIdentity(mine);
        pages.app->navigate(0);
        QTRY_COMPARE(pages.shown(QStringLiteral("characterIdentityPrompt_CN"))->property("identityId").toInt(), guest);

        QVERIFY(pages.click(QStringLiteral("characterIdentityIgnore_CN")));
        QTRY_COMPARE(pages.shown(QStringLiteral("characterIdentityPrompt_CN"))->property("identityId").toInt(), mine);
        QCOMPARE(pages.app->ignoredCharacterIdentityCount(QStringLiteral("CN")), 1);
        QVERIFY(Pages::pick(pages.shown(QStringLiteral("characterIdentityExisting_CN")), alt));
        QTRY_VERIFY(pages.shown(QStringLiteral("characterIdentityBind_CN")));
        QVERIFY(pages.click(QStringLiteral("characterIdentityBind_CN")));
        QTRY_VERIFY(!pages.shown(QStringLiteral("characterIdentityPrompt_CN")));

        QVERIFY(pages.openAchievementSettings());
        // 小号 is current now, so it is the character the tab edits.
        QTRY_VERIFY(pages.shown(QStringLiteral("characterBindingRow")));
        QVERIFY(pages.shown(QStringLiteral("ignoredCharactersRow")));
        QVERIFY(pages.click(QStringLiteral("forgetIdentitiesButton")));
        QTRY_COMPARE(pages.profile(alt).value(QStringLiteral("identity_count")).toInt(), 0);
        QTRY_VERIFY(!pages.shown(QStringLiteral("forgetIdentitiesButton")));
        QVERIFY(pages.shown(QStringLiteral("characterBindingRow")));
        QVERIFY(pages.click(QStringLiteral("askAgainButton")));
        QTRY_COMPARE(pages.app->ignoredCharacterIdentityCount(QStringLiteral("CN")), 0);
        QTRY_VERIFY(!pages.shown(QStringLiteral("ignoredCharactersRow")));
        QCOMPARE(pages.app->pendingCharacterIdentities(QStringLiteral("CN")).size(), 2);

        // The switch itself is with the other switches in 设置 › 通用, where a player with one
        // character finds it too.
        QVERIFY(!pages.shown(QStringLiteral("characterIdentificationToggle")));
        QVERIFY(pages.click(QStringLiteral("settingsTab_general")));
        QTRY_VERIFY(pages.shown(QStringLiteral("characterIdentificationToggle")));
        QQuickItem *toggle = pages.shown(QStringLiteral("characterIdentificationToggle"));
        QCOMPARE(toggle->property("checked").toBool(), true);
        QVERIFY(QMetaObject::invokeMethod(toggle, "toggled", Q_ARG(bool, false)));
        QTRY_COMPARE(pages.app->characterIdentification(QStringLiteral("CN")), QStringLiteral("MANUAL_OFF"));
        QTRY_COMPARE(toggle->property("checked").toBool(), false);
    }
};

} // namespace

MR_DESKTOP_TEST_SUITE(CharacterProfilePages)
{
    // Never run against the user's own Collector, database or serve lease - see
    // TestCollectorGuard.h. Must precede any construction.
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication app(argc, argv);
#ifdef Q_OS_WIN
    // The offscreen platform lists no Windows fonts; load the system's Chinese one so a
    // captured window can be read.
    const QString windowsDir = qEnvironmentVariable("WINDIR", QStringLiteral("C:/Windows"));
    const int fontId = QFontDatabase::addApplicationFont(QDir(windowsDir).filePath(QStringLiteral("Fonts/msyh.ttc")));
    const auto families = QFontDatabase::applicationFontFamilies(fontId);
    if (!families.isEmpty()) {
        QFont font;
        font.setFamilies(families);
        QGuiApplication::setFont(font);
    }
#endif
    CharacterProfilePagesTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "CharacterProfilePagesTests.moc"
