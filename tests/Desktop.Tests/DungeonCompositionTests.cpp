#include "TestCollectorGuard.h"
#include "AppController.h"
#include "DutyCatalog.h"
#include "Formatters.h"
#include "IBackend.h"
#include "JobCatalog.h"
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
#include <limits>
#include <memory>

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
    bool fail = false;
    int requests = 0;

    QString backendName() const override { return QStringLiteral("composition-test"); }
    bool isConnected() const override { return true; }
    mr::BackendReply *request(const QString &type, const QJsonObject &payload = {}) override
    {
        auto *reply = new mr::BackendReply(QString::number(++m_serial), type, this);
        QJsonObject answer;
        const bool rejected = fail && type == QLatin1String("GetDungeonStats");
        if (type == QLatin1String("GetDungeonStats")) {
            ++requests;
            const int page = payload.value(QStringLiteral("page")).toInt(1);
            const int pageSize = payload.value(QStringLiteral("page_size")).toInt(200);
            QJsonArray items;
            for (int i = (page - 1) * pageSize; i < qMin(page * pageSize, int(rows.size())); ++i)
                items.append(rows.at(i));
            answer = {{QStringLiteral("items"), items},
                      {QStringLiteral("distinct_count"), rows.size()},
                      {QStringLiteral("page_info"), QJsonObject{
                          {QStringLiteral("page"), page}, {QStringLiteral("page_size"), pageSize},
                          {QStringLiteral("total"), rows.size()}}}};
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

struct Scene {
    StatisticsBackend backend;
    mr::AppController controller{&backend, nullptr};
    mr::Formatters formatters;
    mr::JobCatalog jobs;
    QQmlEngine engine;
    std::unique_ptr<QObject> root;
    QString errors;
    QStringList warnings;

    bool create(const QString &theme, const QString &style, int width, bool graphs)
    {
        controller.setThemeMode(theme);
        engine.rootContext()->setContextProperty(QStringLiteral("App"), &controller);
        engine.rootContext()->setContextProperty(QStringLiteral("Fmt"), &formatters);
        engine.rootContext()->setContextProperty(QStringLiteral("Jobs"), &jobs);
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
    DungeonsPage { objectName: "dungeonsPage"; anchors.fill: parent; anchors.margins: 24 }
})");
        QQmlComponent component(&engine);
        component.setData(qml, QUrl());
        root.reset(component.create());
        for (const auto &error : component.errors())
            errors += error.toString() + QLatin1Char('\n');
        controller.dungeons()->reload();
        return root != nullptr;
    }

    QQuickWindow *window() const { return qobject_cast<QQuickWindow *>(root.get()); }
    QQuickItem *item(const QString &name) const { return findItem(window()->contentItem(), name); }
};

} // namespace

class DungeonCompositionTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void initTestCase()
    {
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
        QVERIFY(!bucket(model.categoryBreakdown(), QString::fromUtf8("未识别")).isEmpty());
        for (const auto &group : {model.categoryBreakdown(), model.specialDutyBreakdown()}) {
            for (const QVariant &value : group) {
                QCOMPARE(value.toMap().value("completed_count").toLongLong(), qint64(0));
                QVERIFY(value.toMap().value("share").isNull());
            }
        }
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
        QCOMPARE(before.size(), 4);
        QVERIFY(page->setProperty("topLimit", 0));
        QCOMPARE(qmlList(special->property("buckets")), before);
        QVERIFY(page->setProperty("topLimit", 20));
        QCOMPARE(qmlList(special->property("buckets")), before);
        // Both cards and every legend cell must fit the page/card at minimum width.
        for (QQuickItem *chart : {category, special}) {
            QTRY_VERIFY(chart->width() > 0 && chart->height() > 0);
            const QPointF position = chart->mapToItem(page, QPointF());
            QVERIFY(position.x() >= -0.5);
            QVERIFY(position.x() + chart->width() <= page->width() + 0.5);
            auto *legend = findItem(chart, QStringLiteral("completionPieLegend"));
            QVERIFY(legend);
            for (int index = 0; index < qmlList(chart->property("buckets")).size(); ++index) {
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
