// Game names in the interface language (GameNames, Theme's name helpers).
//
// Duties, jobs, roulettes, duty categories and expansions are recorded in the
// Chinese client's names and stay so in every value; only what is shown follows
// the interface language. These tests pin the per-language lookups, the fallbacks
// to the recorded name, the QML helpers' re-evaluation on a switch, and that the
// record wizard shows translated names while sending Chinese ones.

#include "DesktopTestRunner.h"
#include "TestCollectorGuard.h"
#include "AppController.h"
#include "DutyCatalog.h"
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
#include <QJsonArray>
#include <QJsonObject>
#include <QJSValue>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickStyle>
#include <QSet>
#include <QSignalSpy>
#include <QStandardPaths>
#include <QTest>

#include <memory>

namespace {

const QString &kSastashaCn()
{
    static const QString value = QString::fromUtf8("天然要害沙斯塔夏溶洞");
    return value;
}
const QString &kTotoRakCn()
{
    static const QString value = QString::fromUtf8("监狱废墟托托·拉克千狱");
    return value;
}
const QString &kPaladinCn()
{
    static const QString value = QString::fromUtf8("骑士");
    return value;
}
const QString &kWhiteMageCn()
{
    static const QString value = QString::fromUtf8("白魔法师");
    return value;
}
const QString &kDungeonsCn()
{
    static const QString value = QString::fromUtf8("四人迷宫");
    return value;
}

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
            {QStringLiteral("duty_category"), kDungeonsCn()},
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
        {QStringLiteral("duty_name"), kSastashaCn()},
        {QStringLiteral("duty_category"), kDungeonsCn()},
        {QStringLiteral("job_id"), 19},
        {QStringLiteral("job_name"), kPaladinCn()},
        {QStringLiteral("result"), QStringLiteral("COMPLETED")},
        {QStringLiteral("contributes_to_goal"), true},
        {QStringLiteral("note"), QString()},
    };
}

/// The run ids MockBackend lists for \a filter, as the Collector would.
QStringList listedRuns(mr::IBackend &backend, const QVariantMap &filter)
{
    bool done = false;
    QStringList ids;
    const QJsonObject payload{{QStringLiteral("filter"), QJsonObject::fromVariantMap(filter)},
                              {QStringLiteral("page"), 1},
                              {QStringLiteral("page_size"), 200}};
    backend.request(QStringLiteral("QueryRuns"), payload)->whenDone(&backend,
        [&done, &ids](bool ok, const QVariantMap &result, const QString &, const QString &) {
            done = true;
            if (!ok)
                return;
            for (const QVariant &item : result.value(QStringLiteral("items")).toList())
                ids.append(item.toMap().value(QStringLiteral("run_id")).toString());
        });
    if (!QTest::qWaitFor([&done] { return done; }, 3000))
        return {QStringLiteral("TEST_NO_REPLY")};
    ids.sort();
    return ids;
}

} // namespace

namespace {
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

    /// HistoryPage or ReflectionsPage on MockBackend, wired as the app wires it.
    struct SearchPage {
        mr::MockBackend backend;
        mr::AppController controller{&backend, nullptr};
        mr::ReflectionLibraryController library{&backend};
        mr::Formatters formatters;
        mr::JobCatalog jobs;
        mr::RoleCatalog roles;
        mr::RunFormValidator validator;
        QQmlEngine engine;
        const QString pageName;
        const Attachment attached;
        std::unique_ptr<QObject> page;
        QObject *search = nullptr;

        SearchPage(mr::LanguageManager *language, const QString &name)
            : pageName(name), attached(language, &engine)
        {
            const auto context = engine.rootContext();
            context->setContextProperty(QStringLiteral("App"), &controller);
            context->setContextProperty(QStringLiteral("Reflections"), &library);
            context->setContextProperty(QStringLiteral("Fmt"), &formatters);
            context->setContextProperty(QStringLiteral("Jobs"), &jobs);
            context->setContextProperty(QStringLiteral("Roles"), &roles);
            context->setContextProperty(QStringLiteral("RunForm"), &validator);
            context->setContextProperty(QStringLiteral("ReduceMotion"), true);
            context->setContextProperty(QStringLiteral("Lang"), language);
            context->setContextProperty(QStringLiteral("Names"), mr::GameNames::shared());
            QQmlComponent component(&engine, QUrl::fromLocalFile(
                QString::fromUtf8(MR_DESKTOP_QML_DIR) + QStringLiteral("/pages/") + pageName + QStringLiteral(".qml")));
            page.reset(component.create());
            if (!page)
                qWarning() << component.errorString();
            else
                search = page->findChild<QObject *>(pageName == QLatin1String("HistoryPage")
                    ? QStringLiteral("historySearchField") : QStringLiteral("reflectionSearchField"));
        }

        /// Types \a text into the search field and returns the filter the page hands over.
        QVariantMap searchFor(const QString &text)
        {
            search->setProperty("text", text);
            if (!QMetaObject::invokeMethod(page.get(), "applyFilter"))
                return {};
            return pageName == QLatin1String("HistoryPage") ? controller.historyFilter() : library.filter();
        }
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
        QCOMPARE(names()->dutyIn(QStringLiteral("zh-TW"), 4, kSastashaCn()),
                 QString::fromUtf8("天然要害沙斯塔夏溶洞"));
        QCOMPARE(names()->dutyIn(QStringLiteral("en"), 4, kSastashaCn()), QStringLiteral("Sastasha"));
        QCOMPARE(names()->dutyIn(QStringLiteral("ja"), 4, kSastashaCn()),
                 QString::fromUtf8("天然要害 サスタシャ浸食洞"));
        QCOMPARE(names()->jobIn(QStringLiteral("zh-TW"), 19, kPaladinCn()), QString::fromUtf8("騎士"));
        QCOMPARE(names()->jobIn(QStringLiteral("en"), 19, kPaladinCn()), QStringLiteral("Paladin"));
        QCOMPARE(names()->jobIn(QStringLiteral("ja"), 19, kPaladinCn()), QString::fromUtf8("ナイト"));

        // The invokables follow the language on screen.
        switchTo("en");
        QCOMPARE(names()->duty(4, kSastashaCn()), QStringLiteral("Sastasha"));
        QCOMPARE(names()->job(19, kPaladinCn()), QStringLiteral("Paladin"));
        QCOMPARE(names()->roulette(9, QString::fromUtf8("指导者任务")), QStringLiteral("Mentor"));
        switchTo("ja");
        QCOMPARE(names()->duty(4, kSastashaCn()), QString::fromUtf8("天然要害 サスタシャ浸食洞"));
        QCOMPARE(names()->rouletteByName(QString::fromUtf8("指导者任务")), QString::fromUtf8("メンター"));
        switchTo("zh-TW");
        QCOMPARE(names()->job(19, kPaladinCn()), QString::fromUtf8("騎士"));
    }

    void simplifiedChineseShowsTheRecordUnchanged()
    {
        QCOMPARE(mr::LanguageManager::currentCode(), QStringLiteral("zh-CN"));
        // Even a name that differs from the catalogue's is the record's to keep.
        const QString recorded = QString::fromUtf8("沙斯塔夏（旧称）");
        QCOMPARE(names()->duty(4, recorded), recorded);
        QCOMPARE(names()->job(19, QString::fromUtf8("剑术师转职")), QString::fromUtf8("剑术师转职"));
        QCOMPARE(names()->category(kDungeonsCn()), kDungeonsCn());
        QCOMPARE(names()->rouletteByName(QString::fromUtf8("指导者任务")), QString::fromUtf8("指导者任务"));
        // Without a recorded name the catalogue's Chinese name stands in.
        QCOMPARE(names()->duty(4, QString()), kSastashaCn());
    }

    void aMissingIdIsFoundByTheChineseName()
    {
        switchTo("en");
        QCOMPARE(names()->duty(QVariant(), kSastashaCn()), QStringLiteral("Sastasha"));
        QCOMPARE(names()->duty(QVariant::fromValue(nullptr), kTotoRakCn()),
                 QStringLiteral("The Thousand Maws of Toto–Rak"));
        QCOMPARE(names()->job(QVariant(), kPaladinCn()), QStringLiteral("Paladin"));
        QCOMPARE(names()->dutyIn(QStringLiteral("ja"), QVariant(), kSastashaCn()),
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
        QCOMPARE(names()->duty(0, kSastashaCn()), QStringLiteral("Sastasha"));
    }

    void ambiguousNamesDoNotAcquireTheFirstIdentity()
    {
        const QString ambiguous = QString::fromUtf8("天上福地云冠群岛");
        for (const auto &code : {QStringLiteral("en"), QStringLiteral("ja"), QStringLiteral("zh-TW")}) {
            QCOMPARE(names()->dutyIn(code, QVariant(), ambiguous), ambiguous);
            const QString asura = QString::fromUtf8("阿修罗歼灭战");
            QCOMPARE(names()->dutyIn(code, QVariant(), asura), names()->dutyIn(code, 944, asura));
        }
    }

    void everySameNameChoiceKeepsADistinctLabelInEachLanguage()
    {
        QQmlEngine engine;
        const Attachment attached(m_language.get(), &engine);
        engine.rootContext()->setContextProperty(QStringLiteral("Lang"), m_language.get());
        engine.rootContext()->setContextProperty(QStringLiteral("Names"), names());
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import MentorRecorder
QtObject {
    function label(row) { return Theme.dutyChoiceLabel(row) }
    function plain(row) { return Theme.dutyName(row) }
})", QUrl());
        std::unique_ptr<QObject> probe(component.create());
        QVERIFY2(probe, qPrintable(component.errorString()));
        for (const char *code : {"zh-CN", "zh-TW", "en", "ja"}) {
            switchTo(code);
            QSet<QString> labels;
            int count = 0;
            for (const auto &value : mr::DutyCatalog::shared()->allDuties()) {
                QVariantMap row = value.toMap();
                if (!row.value("name_ambiguous").toBool()) continue;
                ++count;
                // Statistics rows omit the marker: Names must provide the same distinction.
                row.remove(QStringLiteral("name_ambiguous"));
                QVariant label;
                QVariant plain;
                QVERIFY(QMetaObject::invokeMethod(probe.get(), "label", Q_RETURN_ARG(QVariant, label), Q_ARG(QVariant, row)));
                QVERIFY(QMetaObject::invokeMethod(probe.get(), "plain", Q_RETURN_ARG(QVariant, plain), Q_ARG(QVariant, row)));
                QVERIFY(label.toString().startsWith(plain.toString()));
                QVERIFY(label.toString().endsWith(row.value("content_id").toString()));
                labels.insert(label.toString());
            }
            QCOMPARE(labels.size(), count);
            QCOMPARE(count, 170);
        }
    }

    // A localized name gives the recorded names it names; the page separately
    // preserves the original words for notes.
    void searchUsesTheRecordedNames()
    {
        const QString sastashaHard = QString::fromUtf8("逆转要害沙斯塔夏溶洞");
        QCOMPARE(names()->recordedSearchNamesIn(QStringLiteral("en"), QStringLiteral("sastasha")),
                 (QStringList{kSastashaCn(), sastashaHard}));
        QCOMPARE(names()->recordedSearchNamesIn(QStringLiteral("en"), QStringLiteral("Paladin")),
                 QStringList{kPaladinCn()});
        QCOMPARE(names()->recordedSearchNamesIn(QStringLiteral("ja"), QString::fromUtf8("ナイト")),
                 QStringList{kPaladinCn()});
        // Pharos Sirius and its hard mode share no Chinese name; both are named.
        QCOMPARE(names()->recordedSearchNamesIn(QStringLiteral("en"), QStringLiteral("Pharos")),
                 (QStringList{QString::fromUtf8("地脉灵灯天狼星灯塔"), QString::fromUtf8("领航明灯天狼星灯塔")}));
        // Chinese typed as is, a note's words, an empty query: no names.
        QVERIFY(names()->recordedSearchNamesIn(QStringLiteral("en"), kSastashaCn()).isEmpty());
        QVERIFY(names()->recordedSearchNamesIn(QStringLiteral("en"), QStringLiteral("wiped twice")).isEmpty());
        QVERIFY(names()->recordedSearchNamesIn(QStringLiteral("en"), QStringLiteral("  ")).isEmpty());
        // A broad word names every duty that has it.
        const QStringList the = names()->recordedSearchNamesIn(QStringLiteral("en"), QStringLiteral("the"));
        QVERIFY2(the.size() > 100 && the.contains(QString::fromUtf8("运河宝物库")), qPrintable(QString::number(the.size())));
        // 简体中文 never adds names.
        QVERIFY(names()->recordedSearchNamesIn(QStringLiteral("zh-CN"), QStringLiteral("Sastasha")).isEmpty());
    }

    // The whole catalogue fits in one name_exact list, so no search is ever cut
    // short by the contract's cap. A larger catalogue needs the cap raised.
    void everyRecordedNameFitsInOneSearch()
    {
        QSet<QString> all;
        for (const QVariant &row : mr::DutyCatalog::shared()->allDuties())
            all.insert(row.toMap().value(QStringLiteral("duty_name")).toString());
        for (const QVariant &row : mr::JobCatalog().allJobs())
            all.insert(row.toMap().value(QStringLiteral("job_name")).toString());
        QVERIFY2(all.size() <= mr::GameNames::kMaxSearchNames, qPrintable(QString::number(all.size())));
        // A single letter names most of the catalogue, and all of it is sent.
        const QStringList e = names()->recordedSearchNamesIn(QStringLiteral("en"), QStringLiteral("e"));
        QVERIFY2(e.size() > 300, qPrintable(QString::number(e.size())));
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
        SearchPage page(m_language.get(), pageName);
        QVERIFY(page.page);
        QVERIFY(page.search);
        QCOMPARE(page.search->objectName(), searchName);
        const QVariantMap filter = page.searchFor(QStringLiteral("Paladin"));
        // A White Mage's note such as "helped a Paladin" must remain searchable,
        // even though a Paladin's recorded job name is Chinese.
        QCOMPARE(filter.value(QStringLiteral("text")).toString(), QStringLiteral("Paladin"));
        QCOMPARE(filter.value(QStringLiteral("name_exact")).toStringList(), QStringList{kPaladinCn()});
        QVERIFY(!filter.contains(QStringLiteral("name_text")));
        QCOMPARE(page.search->property("text").toString(), QStringLiteral("Paladin"));

        // A later free-text query must not retain the previous names.
        const QVariantMap plain = page.searchFor(QStringLiteral("wiped twice"));
        QCOMPARE(plain.value(QStringLiteral("text")).toString(), QStringLiteral("wiped twice"));
        QVERIFY(!plain.contains(QStringLiteral("name_exact")));
    }

    // A translated name finds the runs whose name, as shown, contains it, and no
    // others. Recorded names hold one another (骑士 in 暗黑骑士 and 龙骑士, 万魔殿
    // 边境之狱4 in its Savage mode), so one Chinese substring finds too much.
    void aTranslatedNameFindsOnlyTheRunsItNames_data()
    {
        QTest::addColumn<QString>("pageName");
        QTest::addColumn<QString>("code");
        QTest::addColumn<QString>("query");
        QTest::addColumn<QStringList>("expected");
        const QString history = QStringLiteral("HistoryPage");
        // "note" is a White Mage run whose note says "helped a Paladin".
        QTest::newRow("en Paladin") << history << QStringLiteral("en") << QStringLiteral("Paladin")
                                    << QStringList{QStringLiteral("note"), QStringLiteral("paladin")};
        QTest::newRow("en Paladin, reflections")
            << QStringLiteral("ReflectionsPage") << QStringLiteral("en") << QStringLiteral("Paladin")
            << QStringList{QStringLiteral("note"), QStringLiteral("paladin")};
        QTest::newRow("en Warrior") << history << QStringLiteral("en") << QStringLiteral("Warrior")
                                    << QStringList{QStringLiteral("warrior")};
        QTest::newRow("en Dragoon") << history << QStringLiteral("en") << QStringLiteral("Dragoon")
                                    << QStringList{QStringLiteral("dragoon")};
        QTest::newRow("en Dark Knight") << history << QStringLiteral("en") << QStringLiteral("Dark Knight")
                                        << QStringList{QStringLiteral("dark-knight")};
        QTest::newRow("ja ナイト") << history << QStringLiteral("ja") << QString::fromUtf8("ナイト")
                                 << QStringList{QStringLiteral("paladin")};
        QTest::newRow("ja 戦士") << history << QStringLiteral("ja") << QString::fromUtf8("戦士")
                               << QStringList{QStringLiteral("warrior")};
        QTest::newRow("en Lost Canals") << history << QStringLiteral("en")
                                        << QStringLiteral("The Lost Canals of Uznair")
                                        << QStringList{QStringLiteral("canals")};
        QTest::newRow("ja 辺獄編4") << history << QStringLiteral("ja")
                                  << QString::fromUtf8("万魔殿パンデモニウム：辺獄編4")
                                  << QStringList{QStringLiteral("p4")};
        // A shown name that holds the query still finds its run: the Savage mode's
        // English name contains the normal one's.
        QTest::newRow("en Fourth Circle") << history << QStringLiteral("en")
                                          << QStringLiteral("Asphodelos: The Fourth Circle")
                                          << QStringList{QStringLiteral("p4"), QStringLiteral("p4s")};
        // ... and so do names whose Chinese names hold no common name.
        QTest::newRow("en Pharos") << history << QStringLiteral("en") << QStringLiteral("Pharos")
                                   << QStringList{QStringLiteral("pharos"), QStringLiteral("pharos-hard")};
        QTest::newRow("ja シリウス") << history << QStringLiteral("ja") << QString::fromUtf8("シリウス")
                                  << QStringList{QStringLiteral("pharos"), QStringLiteral("pharos-hard")};
        QTest::newRow("en Sastasha (Hard)") << history << QStringLiteral("en") << QStringLiteral("Sastasha (Hard)")
                                            << QStringList{QStringLiteral("sastasha-hard")};
    }

    void aTranslatedNameFindsOnlyTheRunsItNames()
    {
        QFETCH(QString, pageName);
        QFETCH(QString, code);
        QFETCH(QString, query);
        QFETCH(QStringList, expected);
        switchTo(code.toLatin1().constData());
        SearchPage page(m_language.get(), pageName);
        QVERIFY(page.page);
        QVERIFY(page.search);

        const QString whiteMage = kWhiteMageCn();
        const auto run = [](const QString &id, int jobId, const QString &job, int contentId,
                            const QString &duty, const QString &note = QString()) {
            QJsonObject row = QJsonObject::fromVariantMap(sastashaRun());
            row.insert(QStringLiteral("run_id"), id);
            row.insert(QStringLiteral("job_id"), jobId);
            row.insert(QStringLiteral("job_name"), job);
            row.insert(QStringLiteral("content_id"), contentId);
            row.insert(QStringLiteral("duty_name"), duty);
            row.insert(QStringLiteral("note"), note);
            row.insert(QStringLiteral("reflection"),
                       QJsonObject{{QStringLiteral("mood"), QStringLiteral("good")},
                                   {QStringLiteral("text"), QStringLiteral("reflection")}});
            return row;
        };
        page.backend.resetRuns(QJsonArray{
            run(QStringLiteral("paladin"), 19, kPaladinCn(), 4, kSastashaCn()),
            run(QStringLiteral("dark-knight"), 32, QString::fromUtf8("暗黑骑士"), 4, kSastashaCn()),
            run(QStringLiteral("dragoon"), 22, QString::fromUtf8("龙骑士"), 4, kSastashaCn()),
            run(QStringLiteral("warrior"), 21, QString::fromUtf8("战士"), 4, kSastashaCn()),
            run(QStringLiteral("gunbreaker"), 37, QString::fromUtf8("绝枪战士"), 4, kSastashaCn()),
            run(QStringLiteral("knights"), 24, whiteMage, 90, QString::fromUtf8("圆桌骑士歼灭战")),
            run(QStringLiteral("dragonsong"), 24, whiteMage, 273, QString::fromUtf8("红血之龙骑士")),
            run(QStringLiteral("canals"), 24, whiteMage, 268, QString::fromUtf8("运河宝物库")),
            run(QStringLiteral("canals-deep"), 24, whiteMage, 276, QString::fromUtf8("运河宝物库深层")),
            run(QStringLiteral("p4"), 24, whiteMage, 800, QString::fromUtf8("万魔殿 边境之狱4")),
            run(QStringLiteral("p4s"), 24, whiteMage, 801, QString::fromUtf8("零式万魔殿 边境之狱4")),
            run(QStringLiteral("pharos"), 24, whiteMage, 17, QString::fromUtf8("领航明灯天狼星灯塔")),
            run(QStringLiteral("pharos-hard"), 24, whiteMage, 40, QString::fromUtf8("地脉灵灯天狼星灯塔")),
            run(QStringLiteral("sastasha-hard"), 24, whiteMage, 28, QString::fromUtf8("逆转要害沙斯塔夏溶洞")),
            run(QStringLiteral("note"), 24, whiteMage, 4, kSastashaCn(), QStringLiteral("helped a Paladin")),
        });

        const QVariantMap filter = page.searchFor(query);
        QCOMPARE(filter.value(QStringLiteral("text")).toString(), query);
        expected.sort();
        QCOMPARE(listedRuns(page.backend, filter), expected);
    }

    void categoriesAndExpansions()
    {
        switchTo("en");
        QCOMPARE(names()->category(kDungeonsCn()), QStringLiteral("Dungeons"));
        QCOMPARE(names()->expansion(QStringLiteral("A Realm Reborn")), QStringLiteral("A Realm Reborn"));
        switchTo("ja");
        QCOMPARE(names()->category(kDungeonsCn()), QString::fromUtf8("ダンジョン"));
        QCOMPARE(names()->expansion(QStringLiteral("Dawntrail")), QString::fromUtf8("黄金のレガシー"));
        switchTo("zh-TW");
        QCOMPARE(names()->category(kDungeonsCn()), QString::fromUtf8("迷宮挑戰"));
        switchTo("zh-CN");
        QCOMPARE(names()->expansion(QStringLiteral("A Realm Reborn")), QString::fromUtf8("重生之境"));
    }

    void revisionLogShowsRecordedNamesInTheInterfaceLanguage()
    {
        QCOMPARE(mr::Formatters::revisionFieldValue(QStringLiteral("duty_name"), kSastashaCn()), kSastashaCn());
        switchTo("en");
        QCOMPARE(mr::Formatters::revisionFieldValue(QStringLiteral("duty_name"), kSastashaCn()),
                 QStringLiteral("Sastasha"));
        QCOMPARE(mr::Formatters::revisionFieldValue(QStringLiteral("job_name"), kPaladinCn()),
                 QStringLiteral("Paladin"));
        QCOMPARE(mr::Formatters::revisionFieldValue(QStringLiteral("duty_category"), kDungeonsCn()),
                 QStringLiteral("Dungeons"));
        QCOMPARE(mr::Formatters::revisionFieldValue(QStringLiteral("note"), kSastashaCn()), kSastashaCn());
    }

    void formDiffComparesRecordedNamesAndShowsLabels()
    {
        const QVariantMap before{{QStringLiteral("duty_name"), kSastashaCn()},
                                 {QStringLiteral("duty_label"), QStringLiteral("Sastasha")},
                                 {QStringLiteral("job_name"), kPaladinCn()},
                                 {QStringLiteral("job_label"), QStringLiteral("Paladin")}};
        // Same records: no row, whatever the labels say.
        QVariantMap same = before;
        same.insert(QStringLiteral("duty_label"), QStringLiteral("Something else"));
        QVERIFY(mr::RunFormValidator::diff(before, same).isEmpty());

        QVariantMap other = before;
        other.insert(QStringLiteral("duty_name"), kTotoRakCn());
        other.insert(QStringLiteral("duty_label"), QStringLiteral("The Thousand Maws of Toto–Rak"));
        const QVariantList rows = mr::RunFormValidator::diff(before, other);
        QCOMPARE(rows.size(), 1);
        QCOMPARE(rows.first().toMap().value(QStringLiteral("a")).toString(), QStringLiteral("Sastasha"));
        QCOMPARE(rows.first().toMap().value(QStringLiteral("b")).toString(),
                 QStringLiteral("The Thousand Maws of Toto–Rak"));

        // A form without labels shows the names themselves.
        const QVariantList bare = mr::RunFormValidator::diff(
            {{QStringLiteral("job_name"), kPaladinCn()}}, {{QStringLiteral("job_name"), kWhiteMageCn()}});
        QCOMPARE(bare.size(), 1);
        QCOMPARE(bare.first().toMap().value(QStringLiteral("b")).toString(), kWhiteMageCn());
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

        QCOMPARE(probe->property("duty").toString(), kSastashaCn());
        QCOMPARE(probe->property("noDuty").toString(), QString());
        QCOMPARE(probe->property("catalogJob").toString(), kPaladinCn());
        QCOMPARE(probe->property("category").toString(), kDungeonsCn());

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
        QCOMPARE(job.value(QStringLiteral("job_name")).toString(), kPaladinCn());
        QCOMPARE(job.value(QStringLiteral("label")).toString(), QStringLiteral("Paladin"));
        const QVariantMap category = asMap(asList(probe->property("categories")).value(0));
        QCOMPARE(category.value(QStringLiteral("value")).toString(), kDungeonsCn());
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
        dialog->setProperty("dutyOptions", QVariantList{catalogueDuty(4, kSastashaCn(), 15),
                                                        catalogueDuty(1, kTotoRakCn(), 24)});
        dialog->setProperty("jobOptions",
                            QVariantList{QVariantMap{{QStringLiteral("job_id"), 19},
                                                     {QStringLiteral("job_name"), kPaladinCn()}},
                                         QVariantMap{{QStringLiteral("job_id"), 24},
                                                     {QStringLiteral("job_name"), kWhiteMageCn()}}});
        // Every catalogue row survives the roulette-category filter in English too.
        QCOMPARE(asList(dialog->property("visibleDuties")).size(), 2);

        QVERIFY(QMetaObject::invokeMethod(dialog, "openForRun", Q_ARG(QVariant, QVariant(sastashaRun()))));
        const QVariantList steps = asList(dialog->property("stepItems"));
        QCOMPARE(asMap(steps.value(1)).value(QStringLiteral("value")).toString(),
                 QStringLiteral("Sastasha · Paladin"));
        const QVariantMap form = asMap(dialog->property("formState"));
        QCOMPARE(form.value(QStringLiteral("duty_name")).toString(), kSastashaCn());
        QCOMPARE(form.value(QStringLiteral("job_name")).toString(), kPaladinCn());
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
        QCOMPARE(paladin.value(QStringLiteral("job_name")).toString(), kPaladinCn());
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
        QCOMPARE(dutyChange.value(QStringLiteral("duty_name")).toString(), kTotoRakCn());
        // Both are 四人迷宫, so the category is no change; the job never travels by name.
        QVERIFY(!dutyChange.contains(QStringLiteral("duty_category")));
        QVERIFY(!dutyChange.contains(QStringLiteral("job_name")));
    }
};
} // namespace

MR_DESKTOP_TEST_SUITE(GameNames)
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
