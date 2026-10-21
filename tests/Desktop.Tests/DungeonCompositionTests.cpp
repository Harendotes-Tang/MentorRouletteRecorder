#include "TestCollectorGuard.h"
#include "AppController.h"
#include "AppSettings.h"
#include "DutyCatalog.h"
#include "Formatters.h"
#include "GameNames.h"
#include "IBackend.h"
#include "JobCatalog.h"
#include "LanguageManager.h"
#include "RoleCatalog.h"
#include "StatsModels.h"

#include <QDir>
#include <QFile>
#include <QFont>
#include <QFontDatabase>
#include <QGuiApplication>
#include <QImage>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJSValue>
#include <QPointer>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QSignalSpy>
#include <QStandardPaths>
#include <QTest>
#include <QTimer>
#include <algorithm>
#include <limits>
#include <memory>
#include <tuple>

namespace {

QJsonObject duty(const QJsonValue &id, const QJsonValue &category, int completions,
                 int attempts = 100, const QString &name = QStringLiteral("recorded duty"))
{
    return {{QStringLiteral("content_id"), id}, {QStringLiteral("duty_category"), category},
            {QStringLiteral("duty_name"), name}, {QStringLiteral("completed_count"), completions},
            {QStringLiteral("attempt_count"), attempts}};
}

QJsonArray compositionRows()
{
    return {duty(15, QString::fromUtf8("四人迷宫"), 3),
            duty(16, QString::fromUtf8("四人迷宫"), 2),
            duty(830, QString::fromUtf8("讨伐歼灭战"), 5),
            duty(42, QString::fromUtf8("行会令"), 5),
            // A main-scenario name never turns a different content identity into a member.
            duty(1, QString::fromUtf8("四人迷宫"), 10, 100,
                 QString::fromUtf8("最终决战天幕魔导城")),
            duty(QJsonValue::Null, QString::fromUtf8("四人迷宫"), 7),
            duty(999999, QString::fromUtf8("讨伐歼灭战"), 4),
            duty(QJsonValue::Null, QJsonValue::Null, 6)};
}

class StatisticsBackend final : public mr::IBackend
{
public:
    QJsonArray rows = compositionRows();
    QJsonArray jobRows;
    QJsonObject dashboard;
    bool fail = false;
    int requests = 0;

    QString backendName() const override { return QStringLiteral("composition-test"); }
    bool isConnected() const override { return true; }
    mr::BackendReply *request(const QString &type, const QJsonObject &payload = {}) override
    {
        auto *reply = new mr::BackendReply(QString::number(++m_serial), type, this);
        QJsonObject answer;
        const bool statsRows = type == QLatin1String("GetDungeonStats") || type == QLatin1String("GetJobStats");
        const bool rejected = fail && statsRows;
        if (statsRows) {
            ++requests;
            const QJsonArray source = type == QLatin1String("GetDungeonStats") ? rows : jobRows;
            const int page = payload.value(QStringLiteral("page")).toInt(1);
            const int pageSize = payload.value(QStringLiteral("page_size")).toInt(200);
            QJsonArray items;
            for (int i = (page - 1) * pageSize; i < qMin(page * pageSize, int(source.size())); ++i)
                items.append(source.at(i));
            answer = {{QStringLiteral("items"), items},
                      {QStringLiteral("distinct_count"), source.size()},
                      {QStringLiteral("page_info"), QJsonObject{
                          {QStringLiteral("page"), page}, {QStringLiteral("page_size"), pageSize},
                          {QStringLiteral("total"), source.size()}}}};
        } else if (type == QLatin1String("GetDashboardStats")) {
            answer = dashboard;
        }
        QTimer::singleShot(0, reply, [reply, answer, rejected] {
            if (rejected)
                reply->fail(QStringLiteral("ERR_INTERNAL"), QString::fromUtf8("读取失败测试"));
            else
                reply->succeed(answer);
        });
        return reply;
    }

private:
    int m_serial = 0;
};

QVariantMap bucket(const QVariantList &buckets, const QString &label)
{
    for (const QVariant &value : buckets) {
        if (value.toMap().value(QStringLiteral("label")).toString() == label)
            return value.toMap();
    }
    return {};
}

qint64 count(const QVariantList &buckets, const char *label)
{
    return bucket(buckets, QString::fromUtf8(label)).value(QStringLiteral("completed_count")).toLongLong();
}

qint64 total(const QVariantList &buckets)
{
    qint64 result = 0;
    for (const QVariant &value : buckets)
        result += value.toMap().value(QStringLiteral("completed_count")).toLongLong();
    return result;
}

QVariantList qmlList(const QVariant &value)
{
    return value.metaType() == QMetaType::fromType<QJSValue>()
           ? value.value<QJSValue>().toVariant().toList() : value.toList();
}

QQuickItem *findItem(QQuickItem *from, const QString &name)
{
    if (!from)
        return nullptr;
    if (from->objectName() == name)
        return from;
    for (QQuickItem *child : from->childItems()) {
        if (auto *match = findItem(child, name))
            return match;
    }
    return nullptr;
}

// Every item of that name in creation order, which for Repeater delegates is
// the model's order.
void collectItems(QQuickItem *from, const QString &name, QList<QQuickItem *> &out)
{
    if (!from)
        return;
    if (from->objectName() == name)
        out.append(from);
    for (QQuickItem *child : from->childItems())
        collectItems(child, name, out);
}

QList<QQuickItem *> findItems(QQuickItem *from, const QString &name)
{
    QList<QQuickItem *> out;
    collectItems(from, name, out);
    return out;
}

qreal sceneLeft(QQuickItem *item)
{
    return item->mapToScene(QPointF(0, 0)).x();
}

// The right edge of the widest child, relative to the layout: more than the
// layout's own width means a column spilled past the card's margin.
qreal childrenRight(QQuickItem *layout)
{
    qreal right = 0;
    for (QQuickItem *child : layout->childItems())
        right = qMax(right, child->x() + child->width());
    return right;
}

struct Scene {
    StatisticsBackend backend;
    mr::AppController controller{&backend, nullptr};
    mr::Formatters formatters;
    mr::JobCatalog jobs;
    mr::RoleCatalog roles;
    mr::AppSettings settings;
    QQmlEngine engine;
    std::unique_ptr<QObject> root;
    QString errors;
    QStringList warnings;

    bool create(const QString &theme, const QString &style, int width, bool graphs,
                const QString &pageType = QStringLiteral("DungeonsPage"))
    {
        controller.setThemeMode(theme);
        engine.rootContext()->setContextProperty(QStringLiteral("App"), &controller);
        engine.rootContext()->setContextProperty(QStringLiteral("Fmt"), &formatters);
        engine.rootContext()->setContextProperty(QStringLiteral("Jobs"), &jobs);
        engine.rootContext()->setContextProperty(QStringLiteral("Roles"), &roles);
        engine.rootContext()->setContextProperty(QStringLiteral("Settings"), &settings);
        engine.rootContext()->setContextProperty(QStringLiteral("Names"), mr::GameNames::shared());
        if (auto *language = mr::LanguageManager::instance()) {
            engine.rootContext()->setContextProperty(QStringLiteral("Lang"), language);
            language->attachEngine(&engine);
        }
        engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
        engine.rootContext()->setContextProperty(QStringLiteral("ForceUiStyle"), style);
        engine.rootContext()->setContextProperty(QStringLiteral("GraphsAvailable"), graphs);
        QObject::connect(&engine, &QQmlEngine::warnings, &engine,
                         [this](const QList<QQmlError> &list) {
            for (const auto &warning : list)
                warnings.append(warning.toString());
        });
        const QByteArray qml = QByteArrayLiteral(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: )") + QByteArray::number(width) + QByteArrayLiteral(R"(; height: 920; visible: true
    color: Theme.windowBackground
    )") + pageType.toUtf8() + QByteArrayLiteral(R"( { objectName: "dungeonsPage"; anchors.fill: parent; anchors.margins: 24 }
})");
        QQmlComponent component(&engine);
        component.setData(qml, QUrl());
        root.reset(component.create());
        for (const auto &error : component.errors())
            errors += error.toString() + QLatin1Char('\n');
        controller.dungeons()->reload();
        if (pageType == QLatin1String("JobsPage"))
            controller.jobs()->reload();
        if (pageType == QLatin1String("DashboardPage"))
            controller.refreshDashboard();
        return root != nullptr;
    }

    QQuickWindow *window() const { return qobject_cast<QQuickWindow *>(root.get()); }
    QQuickItem *item(const QString &name) const { return findItem(window()->contentItem(), name); }

    bool capture(const QString &name) const
    {
        const QString evidence = qEnvironmentVariable("MR_COMPOSITION_SCREENSHOT_DIR");
        if (evidence.isEmpty()) return true;
        QTest::qWait(80);
        return QDir().mkpath(evidence) && window()->grabWindow().save(QDir(evidence).filePath(name));
    }
};

} // namespace

class DungeonCompositionTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void initTestCase()
    {
        QVERIFY(QFile::exists(QStringLiteral(":/i18n/mentor_recorder_en.qm")));
        const QString qmlRoot = QString::fromUtf8(MR_DESKTOP_QML_DIR);
        qmlRegisterSingletonType(QUrl::fromLocalFile(qmlRoot + QStringLiteral("/Theme.qml")),
                                 "MentorRecorder", 1, 0, "Theme");
        for (const QString &directory : {QStringLiteral("/components"), QStringLiteral("/charts"),
                                         QStringLiteral("/pages")}) {
            for (const QString &file : QDir(qmlRoot + directory).entryList(
                     {QStringLiteral("*.qml")}, QDir::Files)) {
                const QByteArray name = file.chopped(4).toUtf8();
                qmlRegisterType(QUrl::fromLocalFile(qmlRoot + directory + QLatin1Char('/') + file),
                                "MentorRecorder", 1, 0, name.constData());
            }
        }
    }

    void installedMainScenarioResourceMatchesBothCatalogues()
    {
        QFile resource(QStringLiteral(":/resources/statistics/main-scenario-membership.json"));
        QVERIFY(resource.open(QIODevice::ReadOnly));
        const QJsonObject membership = QJsonDocument::fromJson(resource.readAll()).object();
        QCOMPARE(membership.value(QStringLiteral("scope")).toString(), QStringLiteral("MENTOR_COMPLETIONS"));
        const auto members = membership.value(QStringLiteral("duties")).toArray();
        QCOMPARE(members.size(), 3);
        for (const char *region : {"cn", "global"}) {
            QFile source(QString::fromUtf8(MR_SOURCE_DIR) + QStringLiteral("/data/duties/")
                         + QString::fromLatin1(region) + QStringLiteral(".2026-09-04.json"));
            QVERIFY(source.open(QIODevice::ReadOnly));
            const auto catalogue = QJsonDocument::fromJson(source.readAll()).object()
                                       .value(QStringLiteral("duties")).toArray();
            for (const QJsonValue &member : members) {
                const QJsonObject record = member.toObject();
                const int id = record.value(QStringLiteral("content_id")).toInt();
                QJsonObject found;
                for (const QJsonValue &value : catalogue) {
                    if (value.toObject().value(QStringLiteral("content_id")).toInt() == id)
                        found = value.toObject();
                }
                QVERIFY(!found.isEmpty());
                const QString expected = record.value(QString::fromLatin1(qstrcmp(region, "cn") == 0
                                      ? "local_name" : "official_name")).toString();
                // The GLOBAL export uses sentence-case names; content IDs are the join key.
                QCOMPARE(found.value(QStringLiteral("localized_name")).toString().compare(expected, Qt::CaseInsensitive), 0);
            }
        }
    }

    void compositionsUseCompletionsAndPreserveUnknownIdentities()
    {
        StatisticsBackend backend;
        mr::DungeonStatsModel model;
        model.setBackend(&backend);
        model.reload();
        QTRY_COMPARE(model.rowCount(), 8);
        QCOMPARE(model.totalCompletedCount(), qint64(42));
        QVERIFY(model.totalAttemptCount() > model.totalCompletedCount());
        const auto categories = model.categoryBreakdown();
        const auto special = model.specialDutyBreakdown();
        QCOMPARE(total(categories), qint64(42));
        QCOMPARE(total(special), qint64(42));
        QCOMPARE(count(categories, "四人迷宫"), qint64(22));
        QCOMPARE(count(categories, "讨伐歼灭战"), qint64(9));
        QCOMPARE(count(categories, "行会令"), qint64(5));
        QCOMPARE(count(categories, "未识别"), qint64(6));
        QCOMPARE(count(special, "主线副本"), qint64(10));
        QCOMPARE(count(special, "行会令"), qint64(5));
        QCOMPARE(count(special, "其他副本"), qint64(10));
        QCOMPARE(count(special, "未识别"), qint64(17));
        QCOMPARE(bucket(special, QString::fromUtf8("主线副本")).value("share").toDouble(), 10.0 / 42.0);
        QCOMPARE(bucket(categories, QString::fromUtf8("大型任务")).value("completed_count").toLongLong(), qint64(0));
        QCOMPARE(bucket(categories, QString::fromUtf8("大型任务")).value("share").toDouble(), 0.0);
    }

    void specialClassificationUsesCatalogueRatherThanMutableNamesOrRouletteIds()
    {
        StatisticsBackend backend;
        auto spoof = duty(1, QString::fromUtf8("行会令"), 7, 20,
                          QString::fromUtf8("究极神兵破坏作战"));
        spoof.insert(QStringLiteral("mentor_roulette_id"), 3);
        backend.rows = {spoof, duty(15, QJsonValue::Null, 5, 20, QStringLiteral("renamed"))};
        mr::DungeonStatsModel model;
        model.setBackend(&backend);
        model.reload();
        QTRY_COMPARE(model.rowCount(), 2);
        QCOMPARE(count(model.specialDutyBreakdown(), "主线副本"), qint64(5));
        QCOMPARE(count(model.specialDutyBreakdown(), "行会令"), qint64(0));
        QCOMPARE(count(model.specialDutyBreakdown(), "其他副本"), qint64(7));
        // Category composition preserves the backend's corrected category, while
        // a missing category falls back to the installed catalogue.
        QCOMPARE(count(model.categoryBreakdown(), "四人迷宫"), qint64(5));
        QCOMPARE(count(model.categoryBreakdown(), "行会令"), qint64(7));
    }

    void extremeTrialsUseBundledIdentityWithoutChangingDifficultySelection()
    {
        const auto *catalogue = mr::DutyCatalog::shared();
        QVERIFY(!catalogue->isExtremeTrial(56)); // 伊弗利特讨伐战
        QVERIFY(catalogue->isExtremeTrial(63)); // 伊弗利特歼殛战
        QVERIFY(catalogue->isExtremeTrial(91)); // 圆桌骑士幻想歼灭战
        QVERIFY(catalogue->isExtremeTrial(846)); // 终极之战
        QVERIFY(!catalogue->isExtremeTrial(1118)); // 神龙幻巧战
        QVERIFY(!catalogue->isExtremeTrial(32)); // A known dungeon is not a trial.
        QVERIFY(!catalogue->isExtremeTrial(999999));
        QVERIFY(!catalogue->isExtremeTrial(QVariant()));
        QVERIFY(!catalogue->isExtremeTrial(QJsonValue(QJsonValue::Null).toVariant()));
        QVERIFY(!catalogue->isExtremeTrial(QString::fromUtf8("伊弗利特歼殛战")));
        QVERIFY(!catalogue->isExtremeTrial(QStringLiteral("63")));
        QVERIFY(!catalogue->isExtremeTrial(63.25));
        // Unreal remains in the existing 极 difficulty selector. Only the new
        // completion display classification excludes it.
        QCOMPARE(mr::DutyCatalog::difficultyForName(QString::fromUtf8("神龙幻巧战")), QString::fromUtf8("极"));
        QCOMPARE(catalogue->lookup(1118).value("difficulty").toString(), QString::fromUtf8("极"));
    }

    void extremeCompositionCarvesOnlyTrialRowsAndPreservesRecordedCategories()
    {
        StatisticsBackend backend;
        const QString trials = QString::fromUtf8("讨伐歼灭战");
        const QString misleading = QString::fromUtf8("伊弗利特歼殛战");
        backend.rows = {duty(56, trials, 2), duty(63, trials, 3),
                        duty(91, QJsonValue::Null, 4), duty(1118, trials, 5),
                        duty(56, trials, 6, 100, misleading),
                        duty(999999, trials, 7, 100, misleading),
                        duty(QJsonValue::Null, trials, 8, 100, misleading),
                        duty(QJsonValue::Null, QJsonValue::Null, 9, 100, misleading),
                        duty(32, trials, 10, 100, misleading),
                        // A supplied category remains authoritative: only
                        // completions already in the trial category are carved out.
                        duty(846, QString::fromUtf8("四人迷宫"), 11)};
        mr::DungeonStatsModel model;
        model.setBackend(&backend);
        model.reload();
        QTRY_COMPARE(model.rowCount(), 10);
        const auto categories = model.categoryBreakdown();
        QCOMPARE(model.totalCompletedCount(), qint64(65));
        QCOMPARE(total(categories), model.totalCompletedCount());
        QCOMPARE(count(categories, "极神"), qint64(7));
        QCOMPARE(count(categories, "讨伐歼灭战"), qint64(38));
        QCOMPARE(count(categories, "四人迷宫"), qint64(11));
        QCOMPARE(count(categories, "未识别"), qint64(9));
        const auto extreme = bucket(categories, QString::fromUtf8("极神"));
        QCOMPARE(extreme.value("share").toDouble(), 7.0 / 65.0);
        QVERIFY(!extreme.value("is_unknown").toBool());
        QCOMPARE(extreme.value("color_token").toString(), QStringLiteral("yellow"));
        const QStringList ordinary{QString::fromUtf8("四人迷宫"), trials, QString::fromUtf8("大型任务"),
                                   QString::fromUtf8("团队任务"), QString::fromUtf8("行会令")};
        const QStringList tokens{QStringLiteral("blue"), QStringLiteral("orange"), QStringLiteral("teal"),
                                 QStringLiteral("purple"), QStringLiteral("green")};
        for (qsizetype i = 0; i < ordinary.size(); ++i)
            QCOMPARE(bucket(categories, ordinary.at(i)).value("color_token").toString(), tokens.at(i));
        const auto published = model.topRows(0);
        for (qsizetype i = 0; i < published.size(); ++i) {
            const auto source = backend.rows.at(i).toObject();
            const auto row = published.at(i).toMap();
            QCOMPARE(row.value("content_id"), source.value("content_id").toVariant());
            QCOMPARE(row.value("duty_category"), source.value("duty_category").toVariant());
            QCOMPARE(row.value("duty_name").toString(), source.value("duty_name").toString());
        }
    }

    void extremeCompositionSurvivesTopNAndTranslationWithoutAnotherRead()
    {
        mr::LanguageManager language(nullptr);
        language.initialize(QStringLiteral("zh-CN"));
        StatisticsBackend backend;
        backend.rows = {duty(56, QString::fromUtf8("讨伐歼灭战"), 2),
                        duty(63, QString::fromUtf8("讨伐歼灭战"), 3),
                        duty(1118, QString::fromUtf8("讨伐歼灭战"), 5),
                        duty(QJsonValue::Null, QJsonValue::Null, 7)};
        mr::DungeonStatsModel model;
        model.setBackend(&backend);
        model.reload();
        QTRY_COMPARE(model.rowCount(), 4);
        const auto chinese = model.categoryBreakdown();
        QCOMPARE(count(chinese, "极神"), qint64(3));
        for (const int limit : {1, 5, 0}) {
            QCOMPARE(model.topRows(limit).size(), limit == 1 ? 1 : 4);
            QCOMPARE(model.categoryBreakdown(), chinese);
        }
        const int requests = backend.requests;
        language.setLanguage(QStringLiteral("en"));
        const auto english = model.categoryBreakdown();
        QCOMPARE(count(english, "Extreme Trials"), qint64(3));
        QCOMPARE(total(english), qint64(17));
        QCOMPARE(english.size(), chinese.size());
        for (qsizetype i = 0; i < chinese.size(); ++i) {
            auto before = chinese.at(i).toMap();
            auto after = english.at(i).toMap();
            before.remove(QStringLiteral("label"));
            after.remove(QStringLiteral("label"));
            QCOMPARE(after, before);
        }
        QCOMPARE(backend.requests, requests);
        language.setLanguage(QStringLiteral("zh-CN"));
        QCOMPARE(model.categoryBreakdown(), chinese);
    }

    void allPagesAndTailUnknownCompletionsSurviveTopN()
    {
        StatisticsBackend backend;
        backend.rows = {};
        for (int i = 0; i < 401; ++i)
            backend.rows.append(duty(5000 + i, QString::fromUtf8("四人迷宫"), 1));
        backend.rows.append(duty(830, QString::fromUtf8("讨伐歼灭战"), 99));
        backend.rows.append(duty(QJsonValue::Null, QJsonValue::Null, 30));
        mr::DungeonStatsModel model;
        model.setBackend(&backend);
        QSignalSpy published(&model, &mr::StatsRowsModel::countChanged);
        model.reload();
        QTRY_COMPARE(model.rowCount(), 403);
        QCOMPARE(backend.requests, 3);
        QCOMPARE(published.count(), 1);
        QCOMPARE(model.topRows(10).size(), 10);
        QCOMPARE(model.topRows(20).size(), 20);
        QCOMPARE(model.topRows(0).size(), 403);
        QCOMPARE(model.totalCompletedCount(), qint64(530));
        QCOMPARE(total(model.categoryBreakdown()), qint64(530));
        QCOMPARE(count(model.categoryBreakdown(), "未识别"), qint64(30));
        QCOMPARE(count(model.specialDutyBreakdown(), "主线副本"), qint64(99));
    }

    void emptyTotalsKeepZeroBucketsAndUndefinedShares()
    {
        StatisticsBackend backend;
        backend.rows = {};
        mr::DungeonStatsModel model;
        model.setBackend(&backend);
        model.reload();
        QTRY_VERIFY(!model.isLoading());
        QCOMPARE(model.totalCompletedCount(), qint64(0));
        QCOMPARE(model.specialDutyBreakdown().size(), 4);
        QCOMPARE(model.categoryBreakdown().size(), 7);
        QVERIFY(!bucket(model.categoryBreakdown(), QString::fromUtf8("极神")).isEmpty());
        QVERIFY(!bucket(model.categoryBreakdown(), QString::fromUtf8("未识别")).isEmpty());
        for (const auto &group : {model.categoryBreakdown(), model.specialDutyBreakdown()}) {
            for (const QVariant &value : group) {
                QCOMPARE(value.toMap().value("completed_count").toLongLong(), qint64(0));
                QVERIFY(value.toMap().value("share").isNull());
            }
        }
        backend.rows = {duty(63, QString::fromUtf8("讨伐歼灭战"), 0),
                        duty(56, QString::fromUtf8("讨伐歼灭战"), 1)};
        model.reload();
        QTRY_COMPARE(model.totalCompletedCount(), qint64(1));
        const auto extreme = bucket(model.categoryBreakdown(), QString::fromUtf8("极神"));
        QCOMPARE(extreme.value("completed_count").toLongLong(), qint64(0));
        QCOMPARE(extreme.value("share").toDouble(), 0.0);
        QVERIFY(!extreme.value("is_unknown").toBool());
    }

    void aggregateDoesNotOverflowIndividualInt32Counts()
    {
        StatisticsBackend backend;
        backend.rows = {duty(15, QString::fromUtf8("四人迷宫"), std::numeric_limits<int>::max()),
                        duty(16, QString::fromUtf8("四人迷宫"), std::numeric_limits<int>::max())};
        mr::DungeonStatsModel model;
        model.setBackend(&backend);
        model.reload();
        QTRY_COMPARE(model.rowCount(), 2);
        const qint64 expected = qint64(std::numeric_limits<int>::max()) * 2;
        QCOMPARE(model.totalCompletedCount(), expected);
        QCOMPARE(count(model.categoryBreakdown(), "四人迷宫"), expected);
        QCOMPARE(count(model.specialDutyBreakdown(), "主线副本"), expected);
    }

    void realPageIsReadableAndTopNIndependent_data()
    {
        QTest::addColumn<QString>("theme");
        QTest::addColumn<QString>("style");
        QTest::addColumn<int>("width");
        QTest::addColumn<bool>("graphs");
        for (const QString &theme : {QStringLiteral("light"), QStringLiteral("dark")}) {
            for (const QString &style : {QStringLiteral("classic"), QStringLiteral("eorzea"), QStringLiteral("harendotes")}) {
                for (const int width : {1280, 720}) {
                    for (const bool graphs : {true, false}) {
                        const QByteArray name = (theme + QLatin1Char('-') + style + QLatin1Char('-')
                                                 + QString::number(width) + (graphs ? "-graphs" : "-fallback")).toUtf8();
                        QTest::newRow(name.constData()) << theme << style << width << graphs;
                    }
                }
            }
        }
    }

    void realPageIsReadableAndTopNIndependent()
    {
        QFETCH(QString, theme);
        QFETCH(QString, style);
        QFETCH(int, width);
        QFETCH(bool, graphs);
        Scene scene;
        scene.backend.rows[2] = duty(63, QString::fromUtf8("讨伐歼灭战"), 5);
        QVERIFY2(scene.create(theme, style, width, graphs), qPrintable(scene.errors));
        QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
        QTRY_COMPARE(scene.controller.dungeons()->totalCompletedCount(), qint64(42));
        auto *page = scene.item(QStringLiteral("dungeonsPage"));
        auto *category = scene.item(QStringLiteral("dungeonCategoryPie"));
        auto *special = scene.item(QStringLiteral("dungeonSpecialPie"));
        auto *grid = scene.item(QStringLiteral("dungeonCompositionGrid"));
        QVERIFY(page && category && special && grid);
        QTRY_COMPARE(category->property("completedTotal").toDouble(), 42.0);
        QCOMPARE(special->property("completedTotal").toDouble(), 42.0);
        QCOMPARE(grid->property("columns").toInt(), width >= 1000 ? 2 : 1);
        auto *charts = scene.item(QStringLiteral("dungeonChartsColumn"));
        auto *attempts = scene.item(QStringLiteral("dungeonAttemptsCard"));
        auto *table = scene.item(QStringLiteral("dungeonTableCard"));
        QVERIFY(charts && attempts && table);
        QCOMPARE(category->parentItem(), charts);
        QCOMPARE(special->parentItem(), charts);
        QCOMPARE(attempts->parentItem(), charts);
        QTRY_VERIFY(special->y() >= category->y() + category->height());
        QTRY_VERIFY(attempts->y() >= special->y() + special->height());
        if (grid->property("columns").toInt() == 2) {
            QTRY_VERIFY(table->mapToItem(page, QPointF()).x()
                        >= charts->mapToItem(page, QPointF()).x() + charts->width());
            QVERIFY(qAbs(charts->mapToItem(page, QPointF()).y()
                         - table->mapToItem(page, QPointF()).y()) <= 0.5);
        } else {
            QTRY_VERIFY(table->mapToItem(page, QPointF()).y()
                        >= charts->mapToItem(page, QPointF()).y() + charts->height());
        }
        const QVariantList before = qmlList(special->property("buckets"));
        const QVariantList categoriesBefore = qmlList(category->property("buckets"));
        QCOMPARE(count(categoriesBefore, "极神"), qint64(5));
        QCOMPARE(before.size(), 4);
        QVERIFY(page->setProperty("topLimit", 0));
        QCOMPARE(qmlList(special->property("buckets")), before);
        QCOMPARE(qmlList(category->property("buckets")), categoriesBefore);
        QVERIFY(page->setProperty("topLimit", 20));
        QCOMPARE(qmlList(special->property("buckets")), before);
        QCOMPARE(qmlList(category->property("buckets")), categoriesBefore);
        // Both cards and every legend cell must fit the page/card at minimum width.
        for (QQuickItem *chart : {category, special}) {
            QTRY_VERIFY(chart->width() > 0 && chart->height() > 0);
            const QPointF position = chart->mapToItem(page, QPointF());
            QVERIFY(position.x() >= -0.5);
            QVERIFY(position.x() + chart->width() <= page->width() + 0.5);
            auto *legend = findItem(chart, QStringLiteral("completionPieLegend"));
            QVERIFY(legend);
            for (int index = 0; index < qmlList(chart->property("visibleBuckets")).size(); ++index) {
                auto *count = findItem(chart, QStringLiteral("completionPieCount") + QString::number(index));
                auto *share = findItem(chart, QStringLiteral("completionPieShare") + QString::number(index));
                QVERIFY(count && share);
                QTRY_VERIFY(count->width() > 0 && share->width() > 0);
                const QPointF countEnd = count->mapToItem(chart, QPointF(count->width(), 0));
                const QPointF shareStart = share->mapToItem(chart, QPointF());
                QVERIFY2(shareStart.x() - countEnd.x() <= 12.0,
                         "Completion count and percentage must stay together, not at opposite card edges");
            }
            for (QQuickItem *row : legend->childItems()) {
                if (!row->objectName().startsWith(QLatin1String("completionPieLegendRow")))
                    continue;
                for (QQuickItem *cell : row->childItems()) {
                    const QPointF cellPosition = cell->mapToItem(chart, QPointF());
                    QVERIFY2(cellPosition.x() >= -0.5
                             && cellPosition.x() + cell->width() <= chart->width() + 0.5,
                             qPrintable(cell->objectName()));
                }
            }
        }
        QTest::qWait(80); // Allow the actual Canvas scene graph to paint, then inspect the PNG.
        const QImage image = scene.window()->grabWindow();
        QVERIFY(!image.isNull());
        const QString evidence = qEnvironmentVariable("MR_COMPOSITION_SCREENSHOT_DIR");
        if (!evidence.isEmpty()) {
            QVERIFY(QDir().mkpath(evidence));
            QVERIFY(image.save(QDir(evidence).absoluteFilePath(
                QString::fromLatin1(QTest::currentDataTag()) + QStringLiteral(".png"))));
        }
        QVERIFY2(scene.warnings.isEmpty(), qPrintable(scene.warnings.join(QLatin1Char('\n'))));
    }

    void zeroUnknownDutyIsHiddenWithoutChangingStatistics()
    {
        Scene scene;
        scene.backend.rows = {
            duty(15, QString::fromUtf8("四人迷宫"), 1, 1),
            duty(16, QString::fromUtf8("四人迷宫"), 0, 0),
            duty(QJsonValue::Null, QJsonValue::Null, 0, 0, QString::fromUtf8("未知副本")),
            duty(QJsonValue::Null, QJsonValue::Null, 0, 0, QString::fromUtf8("已识别区域名称")),
            duty(999999, QJsonValue::Null, 0, 0, QString::fromUtf8("未知副本")),
            duty(QJsonValue::Null, QJsonValue::Null, 0, 0, QString())};
        QVERIFY2(scene.create(QStringLiteral("light"), QStringLiteral("classic"), 1280, false), qPrintable(scene.errors));
        QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
        QTRY_COMPARE(scene.controller.dungeons()->rowCount(), 6);
        auto *page = scene.item(QStringLiteral("dungeonsPage"));
        QVERIFY(page);
        QTRY_COMPARE(qmlList(page->property("rowsCache")).size(), 3);
        const auto shown = qmlList(page->property("rowsCache"));
        QCOMPARE(shown[1].toMap().value(QStringLiteral("content_id")).toInt(), 16);
        QCOMPARE(shown[2].toMap().value(QStringLiteral("duty_name")).toString(), QString::fromUtf8("已识别区域名称"));
        QCOMPARE(scene.controller.dungeons()->topRows(0).size(), 6);
        QCOMPARE(scene.controller.dungeons()->totalAttemptCount(), 1);

        const QString evidence = qEnvironmentVariable("MR_COMPOSITION_SCREENSHOT_DIR");
        const auto capture = [&](const QString &name) {
            if (evidence.isEmpty()) return true;
            QTest::qWait(80);
            return QDir().mkpath(evidence) && scene.window()->grabWindow().save(QDir(evidence).filePath(name));
        };
        QVERIFY(capture(QStringLiteral("unknown-zero-hidden.png")));

        scene.backend.rows[2] = duty(QJsonValue::Null, QJsonValue::Null, 0, 2, QString::fromUtf8("未知副本"));
        scene.controller.dungeons()->reload();
        QTRY_COMPARE(scene.controller.dungeons()->totalAttemptCount(), 3);
        QTRY_COMPARE(qmlList(page->property("rowsCache")).size(), 4);
        QVERIFY(capture(QStringLiteral("unknown-nonzero-shown.png")));

        scene.backend.rows[2] = duty(QJsonValue::Null, QJsonValue::Null, 0, 0, QString::fromUtf8("未知副本"));
        scene.controller.dungeons()->reload();
        QTRY_COMPARE(scene.controller.dungeons()->totalAttemptCount(), 1);
        QTRY_COMPARE(qmlList(page->property("rowsCache")).size(), 3);
        QVERIFY2(scene.warnings.isEmpty(), qPrintable(scene.warnings.join(QLatin1Char('\n'))));
    }

    void recognizedJobNamesResolveRolesWithoutReplacingIds()
    {
        mr::JobCatalog catalogue;
        for (const QVariant &value : catalogue.allJobs()) {
            const auto job = value.toMap();
            const auto id = job.value("job_id");
            const auto expected = catalogue.roleGroup(id);
            for (const char *code : {"zh-CN", "zh-TW", "en", "ja"}) {
                const auto name = mr::GameNames::shared()->jobIn(QString::fromLatin1(code), id, job.value("job_name").toString());
                QVERIFY(!name.isEmpty());
                QCOMPARE(catalogue.roleGroup(QVariant(), name), expected);
                QCOMPARE(mr::JobStatsModel::roleGroupOf({{"job_id", QJsonValue::Null}, {"job_name", name},
                                                       {"role_group", QString::fromUtf8("未知")}}), expected);
            }
        }
        QCOMPARE(catalogue.roleGroup(QVariant(), QStringLiteral("  WHITE MAGE  ")), QString::fromUtf8("治疗"));
        QCOMPARE(catalogue.roleGroup(999999, QStringLiteral("Samurai")), QString::fromUtf8("近战"));
        QCOMPARE(catalogue.roleGroup(19, QStringLiteral("White Mage")), QString::fromUtf8("坦克"));
        QCOMPARE(catalogue.roleGroup(QVariant(), QString::fromUtf8("白魔")), QString::fromUtf8("未知"));
        QCOMPARE(catalogue.roleGroup(QVariant(), QString::fromUtf8("未收录职业名称")), QString::fromUtf8("未知"));
        QCOMPARE(catalogue.roleGroup(QVariant(), QString()), QString::fromUtf8("未知"));

        StatisticsBackend backend;
        backend.jobRows = {QJsonObject{{"job_id", QJsonValue::Null}, {"job_name", "Carpenter"}, {"attempt_count", 2}}};
        mr::JobStatsModel model;
        model.setBackend(&backend);
        model.reload();
        QTRY_COMPARE(model.totalAttemptCount(), 2);
        auto roles = model.roleBreakdown();
        QCOMPARE(roles.size(), 7);
        QCOMPARE(roles[5].toMap().value("role_group").toString(), QString::fromUtf8("其他"));
        QCOMPARE(roles[5].toMap().value("attempt_count").toInt(), 2);
        int sum = 0;
        for (const QVariant &role : roles) sum += role.toMap().value("attempt_count").toInt();
        QCOMPARE(sum, model.totalAttemptCount());
        QVERIFY(model.topRows(0).first().toMap().value("job_id").isNull());
        backend.jobRows[0] = QJsonObject{{"job_id", QJsonValue::Null}, {"job_name", "Carpenter"}, {"attempt_count", 0}};
        model.reload();
        QTRY_COMPARE(model.totalAttemptCount(), 0);
        QCOMPARE(model.roleBreakdown().size(), 7); // Known other category retains zero.
    }

    void statisticsHideOnlyZeroPlaceholders_data()
    {
        QTest::addColumn<QString>("pageType");
        QTest::addColumn<QString>("languageCode");
        for (const QString &page : {QStringLiteral("DungeonsPage"), QStringLiteral("JobsPage"), QStringLiteral("DashboardPage")}) {
            for (const QString &language : {QStringLiteral("zh-CN"), QStringLiteral("en")})
                QTest::newRow((page + QLatin1Char('-') + language).toUtf8().constData()) << page << language;
        }
    }

    void statisticsHideOnlyZeroPlaceholders()
    {
        QFETCH(QString, pageType);
        QFETCH(QString, languageCode);
        mr::LanguageManager language(nullptr);
        language.initialize(languageCode);
        Scene scene;
        scene.backend.rows = {duty(15, QString::fromUtf8("四人迷宫"), 1, 1),
                              duty(16, QString::fromUtf8("四人迷宫"), 0, 0),
                              duty(QJsonValue::Null, QJsonValue::Null, 0, 0, QString::fromUtf8("未知副本"))};
        const auto job = [](const QJsonValue &id, const QString &name, int attempts) {
            return QJsonObject{{"job_id", id}, {"job_name", name}, {"attempt_count", attempts}, {"completed_count", 0}};
        };
        scene.backend.jobRows = {job(19, QString::fromUtf8("骑士"), 1), job(21, QString::fromUtf8("战士"), 0),
                                job(QJsonValue::Null, QString::fromUtf8("未知"), 0),
                                job(QJsonValue::Null, QString::fromUtf8("未收录职业名称"), 0),
                                job(999999, QString::fromUtf8("未知"), 0), job(QJsonValue::Null, QString(), 0),
                                job(QJsonValue::Null, QString::fromUtf8("白魔法师"), 3),
                                job(QJsonValue::Null, QStringLiteral("Carpenter"), 2)};
        const auto dashboard = [](int unknown) {
            QJsonArray buckets;
            for (const char *code : {"COMPLETED", "LEFT_OR_ABANDONED", "CANCELLED_BEFORE_ENTRY", "DISCONNECTED", "INTERRUPTED", "UNKNOWN"}) {
                const int count = QByteArray(code) == "COMPLETED" ? 1 : (QByteArray(code) == "UNKNOWN" ? unknown : 0);
                buckets.append(QJsonObject{{"result", QString::fromLatin1(code)}, {"count", count},
                                           {"share", double(count) / (1 + unknown)}});
            }
            return QJsonObject{{"attempt_count", 1 + unknown}, {"completed_count", 1},
                               {"result_breakdown", QJsonObject{{"buckets", buckets}}}};
        };
        scene.backend.dashboard = dashboard(0);
        QVERIFY2(scene.create(QStringLiteral("light"), QStringLiteral("classic"), 1280, false, pageType), qPrintable(scene.errors));
        QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
        auto *page = scene.item(QStringLiteral("dungeonsPage"));
        QVERIFY(page);
        const auto repeaterCount = [&](const char *name) {
            auto *repeater = scene.root->findChild<QObject *>(QString::fromLatin1(name));
            return repeater ? repeater->property("count").toInt() : -1;
        };
        int phase = 0;
        for (const int unknown : {0, 2, 0}) {
            // Check the same fixture before and after translation without another read.
            for (int translated = 0; translated < 2; ++translated) {
                if (translated) language.setLanguage(language.code() == QLatin1String("en") ? QStringLiteral("zh-CN") : QStringLiteral("en"));
                if (pageType == QLatin1String("DungeonsPage")) {
                    if (!translated) {
                        scene.backend.rows[2] = duty(QJsonValue::Null, QJsonValue::Null, unknown, unknown, QString::fromUtf8("未知副本"));
                        scene.controller.dungeons()->reload();
                    }
                    QTRY_COMPARE(scene.controller.dungeons()->totalCompletedCount(), qint64(1 + unknown));
                    QTRY_COMPARE(qmlList(page->property("rowsCache")).size(), unknown ? 3 : 2);
                    QCOMPARE(scene.controller.dungeons()->topRows(0).size(), 3);
                    for (const QString &chartName : {QStringLiteral("dungeonCategoryPie"), QStringLiteral("dungeonSpecialPie")}) {
                        auto *chart = scene.item(chartName);
                        QVERIFY(chart);
                        const int rawSize = chartName == QLatin1String("dungeonCategoryPie") ? 7 : 4;
                        QTRY_COMPARE(qmlList(chart->property("visibleBuckets")).size(), rawSize - (unknown == 0 ? 1 : 0));
                        const auto raw = qmlList(chart->property("buckets"));
                        QCOMPARE(raw.size(), rawSize);
                        QCOMPARE(raw.last().toMap().value("is_unknown").toBool(), true);
                        QCOMPARE(raw.last().toMap().value("completed_count").toInt(), unknown);
                        QCOMPARE(raw.last().toMap().value("label").toString(), language.code() == QLatin1String("en")
                                 ? QStringLiteral("Unrecognized") : QString::fromUtf8("未识别"));
                        QCOMPARE(total(raw), qint64(1 + unknown));
                        for (qsizetype i = 0; i < raw.size() - 1; ++i)
                            QVERIFY(!raw[i].toMap().value("is_unknown").toBool());
                        const int shown = rawSize - (unknown == 0 ? 1 : 0);
                        QVERIFY(findItem(chart, QStringLiteral("completionPieLegendRow") + QString::number(shown - 1)));
                        QVERIFY(!findItem(chart, QStringLiteral("completionPieLegendRow") + QString::number(shown)));
                    }
                } else if (pageType == QLatin1String("JobsPage")) {
                    if (!translated) {
                        scene.backend.jobRows[2] = job(QJsonValue::Null, QString::fromUtf8("未知"), unknown);
                        scene.controller.jobs()->reload();
                    }
                    QTRY_COMPARE(scene.controller.jobs()->totalAttemptCount(), 6 + unknown);
                    QTRY_COMPARE(qmlList(page->property("rowsCache")).size(), unknown ? 6 : 5);
                    QTRY_COMPARE(repeaterCount("jobsTableRows"), unknown ? 6 : 5);
                    QTRY_COMPARE(repeaterCount("jobsRoleLegend"), unknown ? 7 : 6);
                    QCOMPARE(scene.controller.jobs()->topRows(0).size(), 8);
                    const auto rawRoles = scene.controller.jobs()->roleBreakdown();
                    QCOMPARE(rawRoles.size(), 7);
                    QCOMPARE(rawRoles.last().toMap().value("attempt_count").toInt(), unknown);
                    QCOMPARE(rawRoles[1].toMap().value("attempt_count").toInt(), 3);
                    QCOMPARE(rawRoles[5].toMap().value("attempt_count").toInt(), 2);
                    int roleTotal = 0;
                    for (const QVariant &role : rawRoles) roleTotal += role.toMap().value("attempt_count").toInt();
                    QCOMPARE(roleTotal, scene.controller.jobs()->totalAttemptCount());
                    const auto shown = qmlList(page->property("rowsCache"));
                    QVERIFY(std::any_of(shown.cbegin(), shown.cend(), [](const QVariant &row) {
                        return row.toMap().value("job_name").toString() == QString::fromUtf8("未收录职业名称");
                    }));
                    const auto nameOnly = shown[shown.size() - 2].toMap();
                    QCOMPARE(nameOnly.value("job_name").toString(), QString::fromUtf8("白魔法师"));
                    QVERIFY(nameOnly.value("job_id").isNull());
                    QCOMPARE(nameOnly.value("role_group").toString(), QString::fromUtf8("治疗"));
                    auto *tableRow = scene.item(QStringLiteral("jobsTableRow") + QString::number(shown.size() - 2));
                    QVERIFY(tableRow);
                    auto *role = findItem(tableRow, QStringLiteral("jobRowRole"));
                    QVERIFY(role);
                    QCOMPARE(role->property("role").toString(), QString::fromUtf8("治疗"));
                    auto *header = scene.item(QStringLiteral("jobsHeader"));
                    QVERIFY(header);
                    QCOMPARE(header->property("subtitle").toString().isEmpty(), unknown == 0);
                    if (unknown)
                        QCOMPARE(header->property("subtitle").toString(), language.code() == QLatin1String("en")
                                 ? QStringLiteral("Unknown job: 2 runs") : QString::fromUtf8("未知职业 2 次"));
                    QCOMPARE(page->property("unknownCount").toInt(), unknown);
                } else {
                    if (!translated) {
                        scene.backend.dashboard = dashboard(unknown);
                        scene.controller.refreshDashboard();
                    }
                    QTRY_COMPARE(page->property("resultTotal").toInt(), 1 + unknown);
                    QTRY_COMPARE(repeaterCount("dashboardResultLegend"), unknown ? 6 : 5);
                    QTRY_COMPARE(repeaterCount("dashboardStatCards"), unknown ? 8 : 7);
                    const auto raw = scene.controller.resultBuckets();
                    QCOMPARE(raw.size(), 6);
                    QCOMPARE(raw.last().toMap().value("result").toString(), QStringLiteral("UNKNOWN"));
                    QCOMPARE(raw.last().toMap().value("count").toInt(), unknown);
                    QCOMPARE(scene.controller.dashboard().value("attempt_count").toInt(), 1 + unknown);
                    if (unknown) {
                        auto *card = scene.item(QStringLiteral("dashboardStatCard7"));
                        QVERIFY(card);
                        QCOMPARE(card->property("title").toString(), language.code() == QLatin1String("en")
                                 ? QStringLiteral("Unknown result") : QString::fromUtf8("未知结果"));
                    }
                }
            }
            QVERIFY(scene.capture(pageType + QLatin1Char('-') + language.code() + QStringLiteral("-%1.png").arg(phase++)));
        }
        if (pageType == QLatin1String("JobsPage")) {
            scene.backend.jobRows = {};
            scene.controller.jobs()->reload();
            QTRY_COMPARE(scene.controller.jobs()->rowCount(), 0);
            QTRY_COMPARE(repeaterCount("jobsRoleLegend"), 5);
            scene.backend.fail = true;
            scene.controller.jobs()->reload();
            QTRY_VERIFY(!scene.controller.jobs()->loadError().isEmpty());
            QVERIFY(scene.item(QStringLiteral("jobsLoadError"))->isVisible());
            QVERIFY(!scene.item(QStringLiteral("jobsHeader"))->property("subtitle").toString().isEmpty());
        }
        QVERIFY2(scene.warnings.isEmpty(), qPrintable(scene.warnings.join(QLatin1Char('\n'))));
    }

    void joinedInProgressColumnReadsCountAndStaysAligned_data()
    {
        QTest::addColumn<QString>("pageType");
        QTest::addColumn<QString>("languageCode");
        QTest::addColumn<int>("width");
        // This scene has no sidebar: 900 px leaves the tables about the width
        // they get in the real window at its 1100 px minimum; 1280 is the default.
        for (const char *page : {"DungeonsPage", "JobsPage"})
            for (const char *language : {"zh-CN", "en"})
                for (const int width : {900, 1280})
                    QTest::addRow("%s-%s-%d", page, language, width)
                        << QString::fromLatin1(page) << QString::fromLatin1(language) << width;
    }

    // The 中途加入 column (docs/ui-design.md §4.3): the count as sent, 0 when an
    // older Collector sends no joined_in_progress_count, a header that is never
    // narrower than its own title, and header and cells on one vertical line
    // at every width — including the jobs table, whose 次数 bar gives way before
    // the job name does, so the fixed columns never spill past the card.
    void joinedInProgressColumnReadsCountAndStaysAligned()
    {
        QFETCH(QString, pageType);
        QFETCH(QString, languageCode);
        QFETCH(int, width);
        mr::LanguageManager language(nullptr);
        language.initialize(languageCode);
        Scene scene;
        QJsonObject joinedDuty = duty(15, QString::fromUtf8("四人迷宫"), 11, 12);
        joinedDuty.insert(QStringLiteral("joined_in_progress_count"), 3);
        scene.backend.rows = {joinedDuty, duty(16, QString::fromUtf8("四人迷宫"), 7, 7)};
        scene.backend.jobRows = {
            QJsonObject{{"job_id", 19}, {"job_name", QString::fromUtf8("骑士")}, {"attempt_count", 13},
                        {"completed_count", 11}, {"joined_in_progress_count", 4}},
            QJsonObject{{"job_id", 21}, {"job_name", QString::fromUtf8("战士")}, {"attempt_count", 4},
                        {"completed_count", 4}}};
        const bool jobs = pageType == QLatin1String("JobsPage");
        QVERIFY2(scene.create(QStringLiteral("dark"), QStringLiteral("eorzea"), width, false, pageType),
                 qPrintable(scene.errors));
        QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
        auto *page = scene.item(QStringLiteral("dungeonsPage"));
        QVERIFY(page);
        QTRY_COMPARE(qmlList(page->property("rowsCache")).size(), 2);

        const QString cellName = jobs ? QStringLiteral("jobRowJoinedInProgress")
                                      : QStringLiteral("dungeonRowJoinedInProgress");
        QList<QQuickItem *> cells;
        QTRY_COMPARE((cells = findItems(scene.window()->contentItem(), cellName)).size(), 2);
        QCOMPARE(cells[0]->property("text").toString(), jobs ? QStringLiteral("4") : QStringLiteral("3"));
        QCOMPARE(cells[1]->property("text").toString(), QStringLiteral("0"));

        auto *header = scene.item(jobs ? QStringLiteral("jobsTableHeaderJoined")
                                       : QStringLiteral("dungeonTableHeader3"));
        QVERIFY(header);
        QCOMPARE(header->property("text").toString(), languageCode == QLatin1String("en")
                 ? QStringLiteral("Joined late") : QString::fromUtf8("中途加入"));
        const int columnWidth = page->property("joinedColumnWidth").toInt();
        QVERIFY2(columnWidth >= 58, qPrintable(QString::number(columnWidth)));
        QTRY_VERIFY(header->width() > 0 && cells[0]->width() > 0);
        QVERIFY2(header->implicitWidth() <= header->width() + 0.5,
                 qPrintable(QStringLiteral("title %1 px in a %2 px column")
                            .arg(header->implicitWidth()).arg(header->width())));

        const auto aligned = [](QQuickItem *a, QQuickItem *b) {
            return qAbs(sceneLeft(a) - sceneLeft(b)) < 1.0 && qAbs(a->width() - b->width()) < 1.0;
        };
        QTRY_VERIFY(aligned(header, cells[0]));
        QVERIFY(aligned(header, cells[1]));
        QVERIFY2(childrenRight(header->parentItem()) <= header->parentItem()->width() + 0.5,
                 qPrintable(QStringLiteral("header row %1 px wide holds %2 px")
                            .arg(header->parentItem()->width()).arg(childrenRight(header->parentItem()))));
        for (QQuickItem *cell : cells) {
            QVERIFY2(childrenRight(cell->parentItem()) <= cell->parentItem()->width() + 0.5,
                     qPrintable(QStringLiteral("row %1 px wide holds %2 px")
                                .arg(cell->parentItem()->width()).arg(childrenRight(cell->parentItem()))));
        }
        if (jobs) {
            auto *row = scene.item(QStringLiteral("jobsTableRow0"));
            QVERIFY(row);
            // 84 px keeps a four-character job name beside its icon; 48 px keeps
            // the count beside a sliver of bar.
            for (const auto &[headerName, rowCellName, minimum]
                 : {std::tuple{"jobsTableHeaderJob", "jobRowJob", 84},
                    std::tuple{"jobsTableHeaderRuns", "jobRowRuns", 48}}) {
                auto *columnHeader = scene.item(QString::fromLatin1(headerName));
                auto *cell = findItem(row, QString::fromLatin1(rowCellName));
                QVERIFY(columnHeader);
                QVERIFY(cell);
                QVERIFY2(aligned(columnHeader, cell),
                         qPrintable(QStringLiteral("%1: header x %2 w %3, cell x %4 w %5")
                                    .arg(QLatin1String(rowCellName)).arg(sceneLeft(columnHeader))
                                    .arg(columnHeader->width()).arg(sceneLeft(cell)).arg(cell->width())));
                QVERIFY2(cell->width() >= minimum - 0.5,
                         qPrintable(QStringLiteral("%1 is %2 px").arg(QLatin1String(rowCellName)).arg(cell->width())));
            }
            auto *runs = findItem(row, QStringLiteral("jobRowRuns"));
            QCOMPARE(qRound(runs->width()), page->property("runsColumnWidth").toInt());
            QCOMPARE(page->property("runsColumnWidth").toInt(), width == 1280 ? 180 : 48);
        }
        QVERIFY2(scene.warnings.isEmpty(), qPrintable(scene.warnings.join(QLatin1Char('\n'))));
    }

    void pageDistinguishesEmptyFromReadFailure()
    {
        Scene scene;
        scene.backend.rows = {};
        QVERIFY2(scene.create(QStringLiteral("light"), QStringLiteral("classic"), 720, false), qPrintable(scene.errors));
        QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
        QTRY_VERIFY(!scene.controller.dungeons()->isLoading());
        auto *chart = scene.item(QStringLiteral("dungeonSpecialPie"));
        QVERIFY(chart);
        QTRY_COMPARE(chart->property("completedTotal").toDouble(), 0.0);
        auto *empty = findItem(chart, QStringLiteral("completionPieEmpty"));
        QVERIFY(empty && empty->isVisible());
        auto *share = findItem(chart, QStringLiteral("completionPieShare0"));
        QVERIFY(share);
        QCOMPARE(share->property("text").toString(), QString::fromUtf8("—"));
        QCOMPARE(qmlList(chart->property("buckets")).size(), 4);
        QCOMPARE(qmlList(chart->property("visibleBuckets")).size(), 3);
        scene.backend.fail = true;
        scene.controller.dungeons()->reload();
        QTRY_VERIFY(!scene.controller.dungeons()->loadError().isEmpty());
        QVERIFY(!chart->isVisible());
        QVERIFY(scene.item(QStringLiteral("dungeonsLoadError"))->isVisible());
        QVERIFY2(scene.warnings.isEmpty(), qPrintable(scene.warnings.join(QLatin1Char('\n'))));
    }
};

int main(int argc, char **argv)
{
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication app(argc, argv);
#ifdef Q_OS_WIN
    // Offscreen Qt does not enumerate the Windows CJK fallback fonts reliably.
    // Register the real system fonts, as the production screenshot harness does.
    const QString windowsDir = qEnvironmentVariable("WINDIR", QStringLiteral("C:/Windows"));
    QStringList cjkFamilies;
    for (const char *file : {"Fonts/msyh.ttc", "Fonts/simhei.ttf", "Fonts/simsun.ttc", "Fonts/consola.ttf"}) {
        const int fontId = QFontDatabase::addApplicationFont(
            QDir(windowsDir).filePath(QString::fromLatin1(file)));
        if (fontId >= 0 && cjkFamilies.isEmpty() && QByteArray(file).contains("msyh"))
            cjkFamilies = QFontDatabase::applicationFontFamilies(fontId);
    }
    QFont::insertSubstitutions(QStringLiteral("Noto Serif SC"),
                              {QStringLiteral("SimSun"), QStringLiteral("NSimSun")});
    if (!cjkFamilies.isEmpty()) {
        QFont font = app.font();
        font.setFamilies(cjkFamilies);
        app.setFont(font);
    }
#endif
    DungeonCompositionTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "DungeonCompositionTests.moc"
