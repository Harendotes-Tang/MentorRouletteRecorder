// Game names in the interface language (GameNames, Theme's name helpers).
//
// Duties, jobs, roulettes, duty categories and expansions are recorded in the
// Chinese client's names and stay so in every value; only what is shown follows
// the interface language. These tests pin the per-language lookups, the fallbacks
// to the recorded name, the QML helpers' re-evaluation on a switch, and that the
// record wizard shows translated names while sending Chinese ones.

#include "TestCollectorGuard.h"
#include "AppController.h"
#include "Formatters.h"
#include "GameNames.h"
#include "JobCatalog.h"
#include "LanguageManager.h"
#include "MockBackend.h"
#include "RoleCatalog.h"
#include "ReflectionLibraryController.h"
#include "RunFormValidator.h"

#include <QDir>
#include <QGuiApplication>
#include <QJSValue>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickStyle>
#include <QSignalSpy>
#include <QStandardPaths>
#include <QTest>

#include <memory>

namespace {

const QString kSastashaCn = QString::fromUtf8("天然要害沙斯塔夏溶洞");
const QString kTotoRakCn = QString::fromUtf8("监狱废墟托托·拉克千狱");
const QString kPaladinCn = QString::fromUtf8("骑士");
const QString kWhiteMageCn = QString::fromUtf8("白魔法师");
const QString kDungeonsCn = QString::fromUtf8("四人迷宫");

QVariantMap asMap(const QVariant &value)
{
    return value.canConvert<QJSValue>() ? value.value<QJSValue>().toVariant().toMap()
                                        : value.toMap();
}

QVariantList asList(const QVariant &value)
{
    return value.canConvert<QJSValue>() ? value.value<QJSValue>().toVariant().toList()
                                        : value.toList();
}

QVariantMap catalogueDuty(int contentId, const QString &name, int level)
{
    return {{QStringLiteral("content_id"), contentId},
            {QStringLiteral("duty_name"), name},
            {QStringLiteral("duty_category"), kDungeonsCn},
            {QStringLiteral("duty_level"), level},
            {QStringLiteral("duty_expansion"), QStringLiteral("A Realm Reborn")},
            {QStringLiteral("party_size"), 4},
            {QStringLiteral("difficulty"), QString::fromUtf8("普通")},
            {QStringLiteral("version"), QStringLiteral("2.x")}};
}

QVariantMap sastashaRun()
{
    return {
        {QStringLiteral("run_id"), QStringLiteral("names-run")},
        {QStringLiteral("revision"), 1},
        {QStringLiteral("matched_at_utc"), QStringLiteral("2026-09-04T12:39:05.125Z")},
        {QStringLiteral("entered_at_utc"), QStringLiteral("2026-09-04T12:41:00.000Z")},
        {QStringLiteral("ended_at_utc"), QStringLiteral("2026-09-04T12:59:40.422Z")},
        {QStringLiteral("content_id"), 4},
        {QStringLiteral("duty_name"), kSastashaCn},
        {QStringLiteral("duty_category"), kDungeonsCn},
        {QStringLiteral("job_id"), 19},
        {QStringLiteral("job_name"), kPaladinCn},
        {QStringLiteral("result"), QStringLiteral("COMPLETED")},
        {QStringLiteral("contributes_to_goal"), true},
        {QStringLiteral("note"), QString()},
    };
}

} // namespace

class GameNamesTests : public QObject
{
    Q_OBJECT

    std::unique_ptr<mr::LanguageManager> m_language;
    mr::GameNames *names() const { return mr::GameNames::shared(); }

    /// Attaches  engine to the language for as long as it lives, so a failed
    /// check cannot leave the manager holding a deleted engine.
    struct Attachment {
        mr::LanguageManager *language;
        Attachment(mr::LanguageManager *manager, QQmlEngine *engine) : language(manager)
        {
            language->attachEngine(engine);
        }
        ~Attachment() { language->attachEngine(nullptr); }
        Attachment(const Attachment &) = delete;
        Attachment &operator=(const Attachment &) = delete;
    };

    void switchTo(const char *code)
    {
        m_language->setLanguage(QString::fromLatin1(code));
        QCOMPARE(mr::LanguageManager::currentCode(), QString::fromLatin1(code));
    }

private Q_SLOTS:
    void initTestCase()
    {
        // Pinned: the switches below are never written to desktop.ini.
        m_language = std::make_unique<mr::LanguageManager>(nullptr);
        m_language->initialize(QStringLiteral("zh-CN"));
        QVERIFY(m_language->pinned());
        QVERIFY(!names()->dataVersion().isEmpty());

        const QString qmlRoot = QString::fromUtf8(MR_DESKTOP_QML_DIR);
        QVERIFY(QDir(qmlRoot).exists());
        qmlRegisterSingletonType(QUrl::fromLocalFile(qmlRoot + QStringLiteral("/Theme.qml")),
                                 "MentorRecorder", 1, 0, "Theme");
        for (const auto &directory : {QStringLiteral("/components"), QStringLiteral("/dialogs"),
                                     QStringLiteral("/pages")}) {
            for (const auto &file :
                 QDir(qmlRoot + directory).entryList({QStringLiteral("*.qml")}, QDir::Files)) {
                const QByteArray name = file.chopped(4).toUtf8();
                qmlRegisterType(QUrl::fromLocalFile(qmlRoot + directory + QLatin1Char('/') + file),
                                "MentorRecorder", 1, 0, name.constData());
            }
        }
    }

    void cleanup()
    {
        m_language->setLanguage(QStringLiteral("zh-CN"));
    }

    void cleanupTestCase()
    {
        m_language.reset();
    }

    void dutiesAndJobsInEachLanguage()
    {
        QCOMPARE(names()->dutyIn(QStringLiteral("zh-TW"), 4, kSastashaCn),
                 QString::fromUtf8("天然要害沙斯塔夏溶洞"));
        QCOMPARE(names()->dutyIn(QStringLiteral("en"), 4, kSastashaCn), QStringLiteral("Sastasha"));
        QCOMPARE(names()->dutyIn(QStringLiteral("ja"), 4, kSastashaCn),
                 QString::fromUtf8("天然要害 サスタシャ浸食洞"));
        QCOMPARE(names()->jobIn(QStringLiteral("zh-TW"), 19, kPaladinCn), QString::fromUtf8("騎士"));
        QCOMPARE(names()->jobIn(QStringLiteral("en"), 19, kPaladinCn), QStringLiteral("Paladin"));
        QCOMPARE(names()->jobIn(QStringLiteral("ja"), 19, kPaladinCn), QString::fromUtf8("ナイト"));

        // The invokables follow the language on screen.
        switchTo("en");
        QCOMPARE(names()->duty(4, kSastashaCn), QStringLiteral("Sastasha"));
        QCOMPARE(names()->job(19, kPaladinCn), QStringLiteral("Paladin"));
        QCOMPARE(names()->roulette(9, QString::fromUtf8("指导者任务")), QStringLiteral("Mentor"));
        switchTo("ja");
        QCOMPARE(names()->duty(4, kSastashaCn), QString::fromUtf8("天然要害 サスタシャ浸食洞"));
        QCOMPARE(names()->rouletteByName(QString::fromUtf8("指导者任务")), QString::fromUtf8("メンター"));
        switchTo("zh-TW");
        QCOMPARE(names()->job(19, kPaladinCn), QString::fromUtf8("騎士"));
    }

    void simplifiedChineseShowsTheRecordUnchanged()
    {
        QCOMPARE(mr::LanguageManager::currentCode(), QStringLiteral("zh-CN"));
        // Even a name that differs from the catalogue's is the record's to keep.
        const QString recorded = QString::fromUtf8("沙斯塔夏（旧称）");
        QCOMPARE(names()->duty(4, recorded), recorded);
        QCOMPARE(names()->job(19, QString::fromUtf8("剑术师转职")), QString::fromUtf8("剑术师转职"));
        QCOMPARE(names()->category(kDungeonsCn), kDungeonsCn);
        QCOMPARE(names()->rouletteByName(QString::fromUtf8("指导者任务")), QString::fromUtf8("指导者任务"));
        // Without a recorded name the catalogue's Chinese name stands in.
        QCOMPARE(names()->duty(4, QString()), kSastashaCn);
    }

    void aMissingIdIsFoundByTheChineseName()
    {
        switchTo("en");
        QCOMPARE(names()->duty(QVariant(), kSastashaCn), QStringLiteral("Sastasha"));
        QCOMPARE(names()->duty(QVariant::fromValue(nullptr), kTotoRakCn),
                 QStringLiteral("The Thousand Maws of Toto–Rak"));
        QCOMPARE(names()->job(QVariant(), kPaladinCn), QStringLiteral("Paladin"));
        QCOMPARE(names()->dutyIn(QStringLiteral("ja"), QVariant(), kSastashaCn),
                 QString::fromUtf8("天然要害 サスタシャ浸食洞"));
    }

    void anUnknownNameFallsBackToTheRecord()
    {
        switchTo("en");
        const QString homebrew = QString::fromUtf8("自定义副本");
        QCOMPARE(names()->duty(999999, homebrew), homebrew);
        QCOMPARE(names()->duty(QVariant(), homebrew), homebrew);
        QCOMPARE(names()->duty(999999, QString()), QString());
        QCOMPARE(names()->job(999, QString::fromUtf8("旧职业")), QString::fromUtf8("旧职业"));
        QCOMPARE(names()->category(QString::fromUtf8("不存在的类别")), QString::fromUtf8("不存在的类别"));
        QCOMPARE(names()->expansion(QStringLiteral("Unknown Expansion")), QStringLiteral("Unknown Expansion"));
        QCOMPARE(names()->roleGroup(QString::fromUtf8("不存在")), QString::fromUtf8("不存在"));
        // An id of 0 is no id: the recorded name finds the duty.
        QCOMPARE(names()->duty(0, kSastashaCn), QStringLiteral("Sastasha"));
    }

    // A localized name supplies a recorded-name alias; the page separately
    // preserves the original words for notes.
    void searchUsesTheRecordedNames()
    {
        QCOMPARE(names()->recordedSearchTextIn(QStringLiteral("en"), QStringLiteral("sastasha")),
                 kSastashaCn);
        QCOMPARE(names()->recordedSearchTextIn(QStringLiteral("en"), QStringLiteral("Paladin")),
                 kPaladinCn);
        QCOMPARE(names()->recordedSearchTextIn(QStringLiteral("ja"), QString::fromUtf8("ナイト")),
                 kPaladinCn);
        // Chinese typed as is, a note's words, an empty query: unchanged.
        QCOMPARE(names()->recordedSearchTextIn(QStringLiteral("en"), kSastashaCn), kSastashaCn);
        QCOMPARE(names()->recordedSearchTextIn(QStringLiteral("en"), QStringLiteral("wiped twice")),
                 QStringLiteral("wiped twice"));
        QCOMPARE(names()->recordedSearchTextIn(QStringLiteral("en"), QString()), QString());
        // Too broad to name one duty: sent as typed.
        QCOMPARE(names()->recordedSearchTextIn(QStringLiteral("en"), QStringLiteral("the")),
                 QStringLiteral("the"));
        // 简体中文 never rewrites.
        QCOMPARE(names()->recordedSearchTextIn(QStringLiteral("zh-CN"), QStringLiteral("Sastasha")),
                 QStringLiteral("Sastasha"));
    }

    void translatedSearchKeepsTheOriginalNoteQuery_data()
    {
        QTest::addColumn<QString>("pageName");
        QTest::addColumn<QString>("searchName");
        QTest::newRow("history") << QStringLiteral("HistoryPage") << QStringLiteral("historySearchField");
        QTest::newRow("reflections") << QStringLiteral("ReflectionsPage") << QStringLiteral("reflectionSearchField");
    }

    void translatedSearchKeepsTheOriginalNoteQuery()
    {
        QFETCH(QString, pageName);
        QFETCH(QString, searchName);
        switchTo("en");
        mr::MockBackend backend;
        mr::AppController controller{&backend, nullptr};
        mr::ReflectionLibraryController library{&backend};
        mr::Formatters formatters;
        mr::JobCatalog jobs;
        mr::RoleCatalog roles;
        mr::RunFormValidator validator;
        QQmlEngine engine;
        const Attachment attached(m_language.get(), &engine);
        auto *const context = engine.rootContext();
        context->setContextProperty(QStringLiteral("App"), &controller);
        context->setContextProperty(QStringLiteral("Reflections"), &library);
        context->setContextProperty(QStringLiteral("Fmt"), &formatters);
        context->setContextProperty(QStringLiteral("Jobs"), &jobs);
        context->setContextProperty(QStringLiteral("Roles"), &roles);
        context->setContextProperty(QStringLiteral("RunForm"), &validator);
        context->setContextProperty(QStringLiteral("ReduceMotion"), true);
        context->setContextProperty(QStringLiteral("Lang"), m_language.get());
        context->setContextProperty(QStringLiteral("Names"), names());
        QQmlComponent component(&engine, QUrl::fromLocalFile(
            QString::fromUtf8(MR_DESKTOP_QML_DIR) + QStringLiteral("/pages/") + pageName + QStringLiteral(".qml")));
        std::unique_ptr<QObject> page(component.create());
        QVERIFY2(page, qPrintable(component.errorString()));
        auto *search = page->findChild<QObject *>(searchName);
        QVERIFY(search);
        search->setProperty("text", QStringLiteral("Paladin"));
        QVERIFY(QMetaObject::invokeMethod(page.get(), "applyFilter"));
        const QVariantMap filter = pageName == QLatin1String("HistoryPage")
            ? controller.historyFilter() : library.filter();
        // A White Mage's note such as "helped a Paladin" must remain searchable,
        // even though a Paladin's recorded job name is Chinese.
        QCOMPARE(filter.value(QStringLiteral("text")).toString(), QStringLiteral("Paladin"));
        QCOMPARE(filter.value(QStringLiteral("name_text")).toString(), kPaladinCn);
        QCOMPARE(search->property("text").toString(), QStringLiteral("Paladin"));

        // A later free-text query must not retain the previous name alias.
        search->setProperty("text", QStringLiteral("wiped twice"));
        QVERIFY(QMetaObject::invokeMethod(page.get(), "applyFilter"));
        const QVariantMap plain = pageName == QLatin1String("HistoryPage")
            ? controller.historyFilter() : library.filter();
        QCOMPARE(plain.value(QStringLiteral("text")).toString(), QStringLiteral("wiped twice"));
        QVERIFY(!plain.contains(QStringLiteral("name_text")));
    }

    void categoriesAndExpansions()
    {
        switchTo("en");
        QCOMPARE(names()->category(kDungeonsCn), QStringLiteral("Dungeons"));
        QCOMPARE(names()->expansion(QStringLiteral("A Realm Reborn")), QStringLiteral("A Realm Reborn"));
        switchTo("ja");
        QCOMPARE(names()->category(kDungeonsCn), QString::fromUtf8("ダンジョン"));
        QCOMPARE(names()->expansion(QStringLiteral("Dawntrail")), QString::fromUtf8("黄金のレガシー"));
        switchTo("zh-TW");
        QCOMPARE(names()->category(kDungeonsCn), QString::fromUtf8("迷宮挑戰"));
        switchTo("zh-CN");
        QCOMPARE(names()->expansion(QStringLiteral("A Realm Reborn")), QString::fromUtf8("重生之境"));
    }

    void revisionLogShowsRecordedNamesInTheInterfaceLanguage()
    {
        QCOMPARE(mr::Formatters::revisionFieldValue(QStringLiteral("duty_name"), kSastashaCn), kSastashaCn);
        switchTo("en");
        QCOMPARE(mr::Formatters::revisionFieldValue(QStringLiteral("duty_name"), kSastashaCn),
                 QStringLiteral("Sastasha"));
        QCOMPARE(mr::Formatters::revisionFieldValue(QStringLiteral("job_name"), kPaladinCn),
                 QStringLiteral("Paladin"));
        QCOMPARE(mr::Formatters::revisionFieldValue(QStringLiteral("duty_category"), kDungeonsCn),
                 QStringLiteral("Dungeons"));
        QCOMPARE(mr::Formatters::revisionFieldValue(QStringLiteral("note"), kSastashaCn), kSastashaCn);
    }

    void formDiffComparesRecordedNamesAndShowsLabels()
    {
        const QVariantMap before{{QStringLiteral("duty_name"), kSastashaCn},
                                 {QStringLiteral("duty_label"), QStringLiteral("Sastasha")},
                                 {QStringLiteral("job_name"), kPaladinCn},
                                 {QStringLiteral("job_label"), QStringLiteral("Paladin")}};
        // Same records: no row, whatever the labels say.
        QVariantMap same = before;
        same.insert(QStringLiteral("duty_label"), QStringLiteral("Something else"));
        QVERIFY(mr::RunFormValidator::diff(before, same).isEmpty());

        QVariantMap other = before;
        other.insert(QStringLiteral("duty_name"), kTotoRakCn);
        other.insert(QStringLiteral("duty_label"), QStringLiteral("The Thousand Maws of Toto–Rak"));
        const QVariantList rows = mr::RunFormValidator::diff(before, other);
        QCOMPARE(rows.size(), 1);
        QCOMPARE(rows.first().toMap().value(QStringLiteral("a")).toString(), QStringLiteral("Sastasha"));
        QCOMPARE(rows.first().toMap().value(QStringLiteral("b")).toString(),
                 QStringLiteral("The Thousand Maws of Toto–Rak"));

        // A form without labels shows the names themselves.
        const QVariantList bare = mr::RunFormValidator::diff(
            {{QStringLiteral("job_name"), kPaladinCn}}, {{QStringLiteral("job_name"), kWhiteMageCn}});
        QCOMPARE(bare.size(), 1);
        QCOMPARE(bare.first().toMap().value(QStringLiteral("b")).toString(), kWhiteMageCn);
    }

    void themeHelpersFollowTheLanguage()
    {
        QQmlEngine engine;
        const Attachment attached(m_language.get(), &engine);
        engine.rootContext()->setContextProperty(QStringLiteral("Lang"), m_language.get());
        engine.rootContext()->setContextProperty(QStringLiteral("Names"), names());
        mr::JobCatalog jobs;
        engine.rootContext()->setContextProperty(QStringLiteral("Jobs"), &jobs);
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import MentorRecorder
QtObject {
    readonly property string duty: Theme.dutyName({ content_id: 4, duty_name: "天然要害沙斯塔夏溶洞" })
    readonly property string dutyByName: Theme.dutyName({ duty_name: "天然要害沙斯塔夏溶洞" })
    readonly property string noDuty: Theme.dutyName(null)
    readonly property string job: Theme.jobName({ job_id: 19, job_name: "骑士" })
    readonly property string catalogJob: Theme.catalogJobName(19)
    readonly property string unmappedJob: Theme.catalogJobName(99999)
    readonly property string category: Theme.categoryName("四人迷宫")
    readonly property string expansion: Theme.expansionName("Dawntrail")
    readonly property string roulette: Theme.rouletteName("指导者任务")
    readonly property string unknownRole: Theme.roleGroupName("未知")
    readonly property var jobs: Theme.labelledJobs([{ job_id: 19, job_name: "骑士" }])
    readonly property var categories: Theme.labelledCategories(["四人迷宫"])
    function matches(query) {
        return Theme.nameMatches(["天然要害沙斯塔夏溶洞",
                                  Theme.dutyName({ content_id: 4, duty_name: "天然要害沙斯塔夏溶洞" })], query)
    }
})", QUrl());
        std::unique_ptr<QObject> probe(component.create());
        QVERIFY2(probe, qPrintable(component.errorString()));

        QCOMPARE(probe->property("duty").toString(), kSastashaCn);
        QCOMPARE(probe->property("noDuty").toString(), QString());
        QCOMPARE(probe->property("catalogJob").toString(), kPaladinCn);
        QCOMPARE(probe->property("category").toString(), kDungeonsCn);

        switchTo("en");
        QCOMPARE(probe->property("duty").toString(), QStringLiteral("Sastasha"));
        QCOMPARE(probe->property("dutyByName").toString(), QStringLiteral("Sastasha"));
        QCOMPARE(probe->property("job").toString(), QStringLiteral("Paladin"));
        QCOMPARE(probe->property("catalogJob").toString(), QStringLiteral("Paladin"));
        // The placeholder goes through qsTr(); this binary has no catalogue, so it
        // reads as its source text here (the screenshot tests load the real one).
        QCOMPARE(probe->property("unmappedJob").toString(), QString::fromUtf8("未知"));
        QCOMPARE(probe->property("category").toString(), QStringLiteral("Dungeons"));
        QCOMPARE(probe->property("expansion").toString(), QStringLiteral("Dawntrail"));
        QCOMPARE(probe->property("roulette").toString(), QStringLiteral("Mentor"));
        QCOMPARE(probe->property("unknownRole").toString(), QString::fromUtf8("未知"));
        // Options keep the Chinese value and carry the label.
        const QVariantMap job = asMap(asList(probe->property("jobs")).value(0));
        QCOMPARE(job.value(QStringLiteral("job_name")).toString(), kPaladinCn);
        QCOMPARE(job.value(QStringLiteral("label")).toString(), QStringLiteral("Paladin"));
        const QVariantMap category = asMap(asList(probe->property("categories")).value(0));
        QCOMPARE(category.value(QStringLiteral("value")).toString(), kDungeonsCn);
        QCOMPARE(category.value(QStringLiteral("label")).toString(), QStringLiteral("Dungeons"));
        // A search finds the duty by either name.
        for (const QString &query : {QStringLiteral("sasta"), QString::fromUtf8("沙斯塔")}) {
            QVariant found;
            QVERIFY(QMetaObject::invokeMethod(probe.get(), "matches", Q_RETURN_ARG(QVariant, found),
                                              Q_ARG(QVariant, query)));
            QVERIFY2(found.toBool(), qPrintable(query));
        }

        switchTo("ja");
        QCOMPARE(probe->property("duty").toString(), QString::fromUtf8("天然要害 サスタシャ浸食洞"));
        QCOMPARE(probe->property("expansion").toString(), QString::fromUtf8("黄金のレガシー"));
    }

    void recordWizardShowsTranslatedNamesAndSendsChineseOnes()
    {
        mr::MockBackend backend;
        mr::AppController controller{&backend, nullptr};
        mr::Formatters formatters;
        mr::JobCatalog jobs;
        mr::RoleCatalog roles;
        mr::RunFormValidator validator;
        QQmlEngine engine;
        const Attachment attached(m_language.get(), &engine);
        auto *const context = engine.rootContext();
        context->setContextProperty(QStringLiteral("App"), &controller);
        context->setContextProperty(QStringLiteral("Fmt"), &formatters);
        context->setContextProperty(QStringLiteral("Jobs"), &jobs);
        context->setContextProperty(QStringLiteral("Roles"), &roles);
        context->setContextProperty(QStringLiteral("RunForm"), &validator);
        context->setContextProperty(QStringLiteral("ReduceMotion"), true);
        context->setContextProperty(QStringLiteral("Lang"), m_language.get());
        context->setContextProperty(QStringLiteral("Names"), names());
        switchTo("en");

        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 900; height: 800; visible: true
    EditRunDialog { objectName: "edit" }
})", QUrl());
        std::unique_ptr<QObject> root(component.create());
        QVERIFY2(root, qPrintable(component.errorString()));
        QObject *dialog = root->findChild<QObject *>(QStringLiteral("edit"));
        QVERIFY(dialog);
        dialog->setProperty("dutyOptions", QVariantList{catalogueDuty(4, kSastashaCn, 15),
                                                        catalogueDuty(1, kTotoRakCn, 24)});
        dialog->setProperty("jobOptions",
                            QVariantList{QVariantMap{{QStringLiteral("job_id"), 19},
                                                     {QStringLiteral("job_name"), kPaladinCn}},
                                         QVariantMap{{QStringLiteral("job_id"), 24},
                                                     {QStringLiteral("job_name"), kWhiteMageCn}}});
        // Every catalogue row survives the roulette-category filter in English too.
        QCOMPARE(asList(dialog->property("visibleDuties")).size(), 2);

        QVERIFY(QMetaObject::invokeMethod(dialog, "openForRun", Q_ARG(QVariant, QVariant(sastashaRun()))));
        const QVariantList steps = asList(dialog->property("stepItems"));
        QCOMPARE(asMap(steps.value(1)).value(QStringLiteral("value")).toString(),
                 QStringLiteral("Sastasha · Paladin"));
        const QVariantMap form = asMap(dialog->property("formState"));
        QCOMPARE(form.value(QStringLiteral("duty_name")).toString(), kSastashaCn);
        QCOMPARE(form.value(QStringLiteral("job_name")).toString(), kPaladinCn);
        QCOMPARE(form.value(QStringLiteral("duty_label")).toString(), QStringLiteral("Sastasha"));
        QVERIFY(asList(dialog->property("diffRows")).isEmpty());

        // The duty search matches the name on screen and the recorded one.
        const auto visibleIds = [dialog](const QString &query) {
            dialog->setProperty("dutyQuery", query);
            QVariantList ids;
            for (const QVariant &row : asList(dialog->property("visibleDuties")))
                ids.append(asMap(row).value(QStringLiteral("content_id")).toInt());
            return ids;
        };
        QCOMPARE(visibleIds(QStringLiteral("sasta")), QVariantList{4});
        QCOMPARE(visibleIds(QString::fromUtf8("托托")), QVariantList{1});
        QCOMPARE(visibleIds(QStringLiteral("toto")), QVariantList{1});
        QCOMPARE(visibleIds(QStringLiteral("dungeons")).size(), 2);
        QCOMPARE(visibleIds(QStringLiteral("a realm")).size(), 2);
        visibleIds(QString());

        // The job pickers show the job in English and keep the Chinese name.
        const QVariantList groups = asList(dialog->property("jobGroups"));
        QVERIFY(!groups.isEmpty());
        const QVariantMap tank = asMap(groups.first());
        QCOMPARE(tank.value(QStringLiteral("role")).toString(), QString::fromUtf8("坦克"));
        const QVariantMap paladin = asMap(asList(tank.value(QStringLiteral("jobs"))).value(0));
        QCOMPARE(paladin.value(QStringLiteral("job_name")).toString(), kPaladinCn);
        QCOMPARE(paladin.value(QStringLiteral("label")).toString(), QStringLiteral("Paladin"));

        // Only the note changes: no name, translated or not, goes to the Collector.
        QSignalSpy corrections(dialog, SIGNAL(correctRequested(QVariant,QString)));
        dialog->setProperty("noteText", QStringLiteral("only the note"));
        QVERIFY(QMetaObject::invokeMethod(dialog, "submit"));
        QCOMPARE(corrections.count(), 1);
        const QVariantMap noteOnly = asMap(corrections.at(0).at(0));
        QCOMPARE(noteOnly.value(QStringLiteral("note")).toString(), QStringLiteral("only the note"));
        for (const auto *key : {"content_id", "duty_name", "duty_category", "job_id", "job_name"})
            QVERIFY2(!noteOnly.contains(QString::fromLatin1(key)), key);

        // Another duty: the diff reads in English, the correction carries Chinese.
        QVERIFY(QMetaObject::invokeMethod(dialog, "openForRun", Q_ARG(QVariant, QVariant(sastashaRun()))));
        QVERIFY(QMetaObject::invokeMethod(dialog, "pickDuty", Q_ARG(QVariant, 2)));
        const QVariantList diff = asList(dialog->property("diffRows"));
        QCOMPARE(diff.size(), 1);
        QCOMPARE(asMap(diff.first()).value(QStringLiteral("a")).toString(), QStringLiteral("Sastasha"));
        QCOMPARE(asMap(diff.first()).value(QStringLiteral("b")).toString(),
                 QStringLiteral("The Thousand Maws of Toto–Rak"));
        QVERIFY(QMetaObject::invokeMethod(dialog, "submit"));
        QCOMPARE(corrections.count(), 2);
        const QVariantMap dutyChange = asMap(corrections.at(1).at(0));
        QCOMPARE(dutyChange.value(QStringLiteral("content_id")).toInt(), 1);
        QCOMPARE(dutyChange.value(QStringLiteral("duty_name")).toString(), kTotoRakCn);
        // Both are 四人迷宫, so the category is no change; the job never travels by name.
        QVERIFY(!dutyChange.contains(QStringLiteral("duty_category")));
        QVERIFY(!dutyChange.contains(QStringLiteral("job_name")));
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
    GameNamesTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "GameNamesTests.moc"
