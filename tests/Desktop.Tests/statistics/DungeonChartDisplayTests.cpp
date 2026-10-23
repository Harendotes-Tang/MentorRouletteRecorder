#include "DesktopTestRunner.h"
#include "TestCollectorGuard.h"
#include "AppController.h"
#include "AppSettings.h"
#include "Formatters.h"
#include "GameNames.h"
#include "IBackend.h"
#include "JobCatalog.h"
#include "LanguageManager.h"
#include "RoleCatalog.h"
#include "StatsModels.h"

#include <QDir>
#include <QFontDatabase>
#include <QGuiApplication>
#include <QJSValue>
#include <QJsonArray>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QSignalSpy>
#include <QSet>
#include <QStandardPaths>
#include <QTemporaryDir>
#include <QTest>
#include <QTimer>
#include <memory>

namespace {

class ChartBackend final : public mr::IBackend
{
public:
    QJsonArray rows{
        QJsonObject{{"content_id", 15}, {"duty_category", QString::fromUtf8("四人迷宫")}, {"completed_count", 60}, {"attempt_count", 60}},
        QJsonObject{{"content_id", 42}, {"duty_category", QString::fromUtf8("行会令")}, {"completed_count", 30}, {"attempt_count", 30}},
        QJsonObject{{"content_id", 1}, {"duty_category", QString::fromUtf8("四人迷宫")}, {"completed_count", 10}, {"attempt_count", 10}}};
    QString backendName() const override { return QStringLiteral("chart-test"); }
    bool isConnected() const override { return true; }
    mr::BackendReply *request(const QString &type, const QJsonObject & = {}) override
    {
        auto *reply = new mr::BackendReply(QString::number(++serial), type, this);
        const QJsonObject result{{"items", rows}, {"distinct_count", rows.size()},
            {"page_info", QJsonObject{{"page", 1}, {"page_size", 200}, {"total", rows.size()}}}};
        QTimer::singleShot(0, reply, [reply, result] { reply->succeed(result); });
        return reply;
    }
private:
    int serial = 0;
};

QVariantList list(const QVariant &value)
{
    return value.metaType() == QMetaType::fromType<QJSValue>()
        ? value.value<QJSValue>().toVariant().toList() : value.toList();
}

QQuickItem *item(QQuickItem *root, const QString &name)
{
    if (root->objectName() == name)
        return root;
    for (QQuickItem *child : root->childItems()) {
        if (auto *found = item(child, name))
            return found;
    }
    return nullptr;
}

struct ChartScene {
    QTemporaryDir directory;
    ChartBackend backend;
    mr::AppController controller{&backend, nullptr};
    mr::AppSettings settings{directory.filePath(QStringLiteral("desktop.ini")), nullptr};
    mr::Formatters formatters;
    mr::JobCatalog jobs;
    mr::RoleCatalog roles;
    QString errors;
    QStringList warnings;
    QQmlEngine engine;
    std::unique_ptr<QObject> root;

    bool create(const QString &theme = QStringLiteral("light"), int width = 1280)
    {
        controller.setThemeMode(theme);
        auto *context = engine.rootContext();
        context->setContextProperty(QStringLiteral("App"), &controller);
        context->setContextProperty(QStringLiteral("Settings"), &settings);
        context->setContextProperty(QStringLiteral("Fmt"), &formatters);
        context->setContextProperty(QStringLiteral("Jobs"), &jobs);
        context->setContextProperty(QStringLiteral("Roles"), &roles);
        context->setContextProperty(QStringLiteral("Names"), mr::GameNames::shared());
        context->setContextProperty(QStringLiteral("ReduceMotion"), true);
        context->setContextProperty(QStringLiteral("ForceUiStyle"), QStringLiteral("classic"));
        context->setContextProperty(QStringLiteral("GraphsAvailable"), false);
        if (auto *language = mr::LanguageManager::instance()) {
            context->setContextProperty(QStringLiteral("Lang"), language);
            language->attachEngine(&engine);
        }
        QObject::connect(&engine, &QQmlEngine::warnings, &engine, [this](const QList<QQmlError> &values) {
            for (const auto &value : values)
                warnings.append(value.toString());
        });
        QQmlComponent component(&engine);
        component.setData(QByteArrayLiteral("import QtQuick\nimport QtQuick.Controls\nimport MentorRecorder\nApplicationWindow { width: ")
            + QByteArray::number(width) + QByteArrayLiteral("; height: 1100; visible: true; color: Theme.windowBackground\n"
              "DungeonsPage { objectName: \"chartPage\"; anchors.fill: parent; anchors.margins: 24 } }"), QUrl());
        root.reset(component.create());
        for (const auto &error : component.errors())
            errors += error.toString() + QLatin1Char('\n');
        controller.dungeons()->reload();
        return root != nullptr;
    }
    QQuickWindow *window() const { return qobject_cast<QQuickWindow *>(root.get()); }
    QQuickItem *find(const QString &name) const { return item(window()->contentItem(), name); }
    bool click(const QString &name, bool keyboard = false)
    {
        // Expanding the panel and changing languages rebuild its layouts. Wait
        // for a frame, then polish ancestors before mapping native coordinates.
        QTest::qWait(40);
        QQuickItem *control = nullptr;
        QPoint position;
        if (!QTest::qWaitFor([&] {
            control = find(name);
            if (!control || !control->isVisible() || !control->isEnabled())
                return false;
            QList<QQuickItem *> ancestors;
            for (auto *parent = control; parent; parent = parent->parentItem())
                ancestors.prepend(parent);
            for (auto *parent : ancestors)
                parent->ensurePolished();
            position = control->mapToScene(QPointF(control->width() / 2, control->height() / 2)).toPoint();
            return control->width() > 0 && control->height() > 0
                && QRect(QPoint(), window()->size()).contains(position);
        }, 1500))
            return false;
        if (keyboard) {
            control->forceActiveFocus(Qt::TabFocusReason);
            QTest::keyClick(window(), Qt::Key_Space);
        } else {
            QTest::mouseClick(window(), Qt::LeftButton, Qt::NoModifier, position);
        }
        QCoreApplication::processEvents();
        return true;
    }
    bool capture(const QString &name) const
    {
        const QString path = qEnvironmentVariable("MR_COMPOSITION_SCREENSHOT_DIR");
        if (path.isEmpty())
            return true;
        QTest::qWait(80);
        return QDir().mkpath(path) && window()->grabWindow().save(QDir(path).filePath(name));
    }
};

class DungeonChartDisplayTests final : public QObject
{
    Q_OBJECT
private slots:
    void initTestCase()
    {
        const QString base = QString::fromUtf8(MR_DESKTOP_QML_DIR);
        qmlRegisterSingletonType(QUrl::fromLocalFile(base + QStringLiteral("/Theme.qml")), "MentorRecorder", 1, 0, "Theme");
        for (const QString &directory : {QStringLiteral("/components"), QStringLiteral("/charts"), QStringLiteral("/pages")}) {
            for (const QString &file : QDir(base + directory).entryList({QStringLiteral("*.qml")}, QDir::Files)) {
                const QByteArray name = file.chopped(4).toUtf8();
                qmlRegisterType(QUrl::fromLocalFile(base + directory + QLatin1Char('/') + file), "MentorRecorder", 1, 0, name.constData());
            }
        }
    }
    void preferencesPersistAndResetOnlyMainPage()
    {
        QTemporaryDir directory;
        QVERIFY(directory.isValid());
        const QString path = directory.filePath(QStringLiteral("desktop.ini"));
        {
            mr::AppSettings settings(path, nullptr);
            QVERIFY(settings.dungeonCategoryChartVisible());
            QVERIFY(settings.dungeonSpecialChartVisible());
            QVERIFY(settings.dungeonCategoryHiddenBuckets().isEmpty());
            QVERIFY(settings.dungeonSpecialHiddenBuckets().isEmpty());
            settings.setDungeonCategoryChartVisible(false);
            settings.setDungeonSpecialChartVisible(false);
            settings.setDungeonCategoryHiddenBuckets({QStringLiteral("trial:extreme"), QStringLiteral("trial:extreme"), QString()});
            settings.setDungeonSpecialHiddenBuckets({QStringLiteral("main-scenario"), QStringLiteral("future-category")});
            settings.setStatisticsOverlayRowLimit(7);
        }
        mr::AppSettings restored(path, nullptr);
        QVERIFY(!restored.dungeonCategoryChartVisible());
        QVERIFY(!restored.dungeonSpecialChartVisible());
        QCOMPARE(restored.dungeonCategoryHiddenBuckets(), QStringList{QStringLiteral("trial:extreme")});
        QVERIFY(restored.dungeonSpecialHiddenBuckets().contains(QStringLiteral("future-category")));
        QSignalSpy changed(&restored, &mr::AppSettings::dungeonChartsChanged);
        restored.resetDungeonCharts();
        QCOMPARE(changed.size(), 1);
        QVERIFY(restored.dungeonCategoryChartVisible() && restored.dungeonSpecialChartVisible());
        QVERIFY(restored.dungeonCategoryHiddenBuckets().isEmpty() && restored.dungeonSpecialHiddenBuckets().isEmpty());
        QCOMPARE(restored.statisticsOverlayRowLimit(), 7);
    }
    void selectionRecalculatesWithoutChangingCanonicalSnapshot()
    {
        mr::LanguageManager language{nullptr};
        language.initialize(QStringLiteral("zh-CN"));
        ChartScene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
        QTRY_COMPARE(scene.controller.dungeons()->totalCompletedCount(), 100);
        const auto raw = scene.controller.dungeons()->specialDutyBreakdown();
        auto *chart = scene.find(QStringLiteral("dungeonSpecialPie"));
        QVERIFY(chart);
        QVERIFY(scene.click(QStringLiteral("customizeDungeonCharts")));
        QVERIFY(scene.click(QStringLiteral("specialBucket:main-scenario"), true));
        QTRY_COMPARE(chart->property("completedTotal").toDouble(), 40.0);
        const auto selected = list(chart->property("selectedBuckets"));
        QCOMPARE(selected.size(), 3);
        QCOMPARE(selected[0].toMap().value("share").toDouble(), 0.75);
        QCOMPARE(selected[1].toMap().value("share").toDouble(), 0.25);
        QCOMPARE(scene.controller.dungeons()->specialDutyBreakdown(), raw);
        QCOMPARE(list(chart->property("buckets")), raw);
        QCOMPARE(scene.find(QStringLiteral("dungeonCategoryPie"))->property("completedTotal").toDouble(), 100.0);
        QCOMPARE(chart->property("totalCaption").toString(), QString::fromUtf8("所选通关 40 次"));
        QVERIFY(scene.find(QStringLiteral("chartPage"))->setProperty("topLimit", 1));
        QCOMPARE(chart->property("completedTotal").toDouble(), 40.0);
        QCOMPARE(scene.controller.dungeons()->specialDutyBreakdown(), raw);
        auto updated = scene.backend.rows[1].toObject();
        updated[QStringLiteral("completed_count")] = 20;
        scene.backend.rows[1] = updated;
        scene.controller.dungeons()->reload();
        QTRY_COMPARE(chart->property("completedTotal").toDouble(), 30.0);
        QCOMPARE(scene.settings.dungeonSpecialHiddenBuckets(), QStringList{QStringLiteral("main-scenario")});
        QVERIFY2(scene.warnings.isEmpty(), qPrintable(scene.warnings.join(QLatin1Char('\n'))));
    }
    void controlsRemainReachableAndLabelsFollowLanguage_data()
    {
        QTest::addColumn<QString>("code");
        QTest::addColumn<QString>("theme");
        QTest::addColumn<int>("width");
        QTest::newRow("zh-light-wide") << QStringLiteral("zh-CN") << QStringLiteral("light") << 1280;
        QTest::newRow("en-dark-narrow") << QStringLiteral("en") << QStringLiteral("dark") << 720;
        QTest::newRow("ja-light-narrow") << QStringLiteral("ja") << QStringLiteral("light") << 720;
        QTest::newRow("tw-dark-wide") << QStringLiteral("zh-TW") << QStringLiteral("dark") << 1280;
    }
    void zeroSelectedCompletionsAndNewBucketsStayDefined()
    {
        ChartScene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QTRY_COMPARE(scene.controller.dungeons()->totalCompletedCount(), 100);
        const auto raw = scene.controller.dungeons()->categoryBreakdown();
        QStringList hidden;
        QSet<QString> keys;
        for (const auto &value : raw) {
            const auto bucket = value.toMap();
            const QString key = bucket.value("key").toString();
            QVERIFY(!key.isEmpty() && !keys.contains(key));
            keys.insert(key);
            if (bucket.value("completed_count").toInt() > 0)
                hidden.append(key);
        }
        scene.settings.setDungeonCategoryHiddenBuckets(hidden);
        auto *chart = scene.find(QStringLiteral("dungeonCategoryPie"));
        QVERIFY(chart);
        QTRY_COMPARE(chart->property("completedTotal").toDouble(), 0.0);
        const auto zeroSelection = list(chart->property("selectedBuckets"));
        QVERIFY(!zeroSelection.isEmpty());
        for (const auto &value : zeroSelection)
            QVERIFY(value.toMap().value("share").isNull());
        QCOMPARE(item(chart, QStringLiteral("completionPieShare0"))->property("text").toString(), QString::fromUtf8("—"));
        scene.backend.rows.append(QJsonObject{{"content_id", QJsonValue::Null},
            {"duty_category", QString::fromUtf8("新增分类")}, {"completed_count", 5}, {"attempt_count", 5}});
        scene.controller.dungeons()->reload();
        QTRY_COMPARE(chart->property("completedTotal").toDouble(), 5.0);
        QCOMPARE(scene.settings.dungeonCategoryHiddenBuckets().size(), hidden.size());
        const auto selected = list(chart->property("selectedBuckets"));
        bool foundNewBucket = false;
        for (const auto &value : selected) {
            const auto bucket = value.toMap();
            if (bucket.value("key").toString() == QString::fromUtf8("category:新增分类")) {
                foundNewBucket = true;
                QCOMPARE(bucket.value("share").toDouble(), 1.0);
            }
        }
        QVERIFY(foundNewBucket);
        QVERIFY2(scene.warnings.isEmpty(), qPrintable(scene.warnings.join(QLatin1Char('\n'))));
    }
    void controlsRemainReachableAndLabelsFollowLanguage()
    {
        QFETCH(QString, code);
        QFETCH(QString, theme);
        QFETCH(int, width);
        mr::LanguageManager language{nullptr};
        language.initialize(code);
        ChartScene scene;
        QVERIFY2(scene.create(theme, width), qPrintable(scene.errors));
        QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
        QTRY_COMPARE(scene.controller.dungeons()->totalCompletedCount(), 100);
        QVERIFY(scene.click(QStringLiteral("customizeDungeonCharts")));
        auto *chart = scene.find(QStringLiteral("dungeonSpecialPie"));
        QVERIFY(scene.click(QStringLiteral("specialBucket:main-scenario")));
        QTRY_COMPARE(chart->property("completedTotal").toDouble(), 40.0);
        const QString originalLabel = scene.find(QStringLiteral("specialBucket:main-scenario"))->property("text").toString();
        QVERIFY(!originalLabel.isEmpty());
        language.setLanguage(code == QLatin1String("en") ? QStringLiteral("ja") : QStringLiteral("en"));
        QTRY_VERIFY(scene.find(QStringLiteral("specialBucket:main-scenario"))->property("text").toString() != originalLabel);
        QVERIFY(!scene.find(QStringLiteral("specialBucket:main-scenario"))->property("checked").toBool());
        QTRY_COMPARE(chart->property("completedTotal").toDouble(), 40.0);
        language.setLanguage(code);
        QTRY_COMPARE(scene.find(QStringLiteral("specialBucket:main-scenario"))->property("text").toString(), originalLabel);
        QVERIFY(scene.capture(QString::fromLatin1(QTest::currentDataTag()) + QStringLiteral("-customized.png")));
        QVERIFY(scene.click(QStringLiteral("categoryChartVisibility")));
        QVERIFY(scene.click(QStringLiteral("customizeDungeonCharts")));
        QVERIFY(scene.capture(QString::fromLatin1(QTest::currentDataTag()) + QStringLiteral("-selected-view.png")));
        QVERIFY(scene.click(QStringLiteral("customizeDungeonCharts")));
        QVERIFY(scene.click(QStringLiteral("categoryChartVisibility")));
        for (const auto &value : scene.controller.dungeons()->specialDutyBreakdown()) {
            const QString key = value.toMap().value("key").toString();
            if (key != QLatin1String("main-scenario"))
                QVERIFY(scene.click(QStringLiteral("specialBucket:") + key));
        }
        QTRY_COMPARE(list(chart->property("selectedBuckets")).size(), 0);
        QCOMPARE(chart->property("completedTotal").toDouble(), 0.0);
        QVERIFY(item(chart, QStringLiteral("completionPieEmpty"))->isVisible());
        QVERIFY(scene.capture(QString::fromLatin1(QTest::currentDataTag()) + QStringLiteral("-empty.png")));
        QVERIFY(scene.click(QStringLiteral("categoryChartVisibility")));
        QVERIFY(scene.click(QStringLiteral("customizeDungeonCharts")));
        QVERIFY(scene.capture(QString::fromLatin1(QTest::currentDataTag()) + QStringLiteral("-empty-view.png")));
        QVERIFY(scene.click(QStringLiteral("customizeDungeonCharts")));
        QVERIFY(scene.click(QStringLiteral("categoryChartVisibility")));
        // Reset restores chart bindings after a language retranslation destroyed
        // and recreated the category delegates.
        QVERIFY(scene.click(QStringLiteral("categoryChartVisibility")));
        QVERIFY(scene.click(QStringLiteral("specialChartVisibility")));
        QVERIFY(!scene.find(QStringLiteral("dungeonCategoryPie"))->isVisible() && !chart->isVisible());
        QVERIFY(scene.find(QStringLiteral("customizeDungeonCharts"))->isVisible());
        QVERIFY(scene.find(QStringLiteral("resetDungeonCharts"))->isVisible());
        QVERIFY(scene.capture(QString::fromLatin1(QTest::currentDataTag()) + QStringLiteral("-hidden.png")));
        QVERIFY(scene.click(QStringLiteral("resetDungeonCharts"), true));
        QTRY_VERIFY(scene.find(QStringLiteral("dungeonCategoryPie"))->isVisible() && chart->isVisible());
        QTRY_COMPARE(chart->property("completedTotal").toDouble(), 100.0);
        QVERIFY(scene.settings.dungeonSpecialHiddenBuckets().isEmpty());
        QVERIFY(scene.capture(QString::fromLatin1(QTest::currentDataTag()) + QStringLiteral("-restored.png")));
        QVERIFY2(scene.warnings.isEmpty(), qPrintable(scene.warnings.join(QLatin1Char('\n'))));
    }
};
} // namespace

MR_DESKTOP_TEST_SUITE(DungeonChartDisplay)
{
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication app(argc, argv);
#ifdef Q_OS_WIN
    const int fontId = QFontDatabase::addApplicationFont(QDir(qEnvironmentVariable("WINDIR", QStringLiteral("C:/Windows"))).filePath(QStringLiteral("Fonts/msyh.ttc")));
    if (fontId >= 0) {
        QFont font = QGuiApplication::font();
        font.setFamilies(QFontDatabase::applicationFontFamilies(fontId));
        QGuiApplication::setFont(font);
    }
#endif
    DungeonChartDisplayTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "DungeonChartDisplayTests.moc"
