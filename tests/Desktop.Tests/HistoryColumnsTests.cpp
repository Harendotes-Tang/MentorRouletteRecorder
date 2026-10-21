// The history table's columns (docs/ui-design.md §4.2): HistoryColumnsController
// on its own and on the page. The page tests load HistoryPage.qml from the
// source tree on the mock backend with a temporary desktop.ini, drive the header
// with synthetic mouse events and keep the offscreen window's frames under
// artifacts/history-columns-20261021/ as evidence.
#include "TestCollectorGuard.h"
#include "AppController.h"
#include "AppSettings.h"
#include "Formatters.h"
#include "HistoryColumnsController.h"
#include "JobCatalog.h"
#include "MockBackend.h"
#include "RoleCatalog.h"
#include "RunFormValidator.h"
#include "TtsService.h"

#include <QDir>
#include <QFile>
#include <QFontDatabase>
#include <QGuiApplication>
#include <QImage>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QSettings>
#include <QSignalSpy>
#include <QTemporaryDir>
#include <QTest>
#include <functional>
#include <memory>

namespace {
const QString &kEvidence()
{
    static const QString value = QString::fromUtf8(MR_SOURCE_DIR)
                                 + QStringLiteral("/artifacts/history-columns-20261021");
    return value;
}

QStringList defaults()
{
    return mr::HistoryColumnsController::defaultOrder();
}

QQuickItem *findVisual(QQuickItem *item, const QString &name)
{
    if (!item)
        return nullptr;
    if (item->objectName() == name)
        return item;
    for (auto *child : item->childItems())
        if (auto *found = findVisual(child, name))
            return found;
    return nullptr;
}

QPoint centreOf(QQuickItem *item)
{
    return item->mapToScene(QPointF(item->width() / 2, item->height() / 2)).toPoint();
}

QPointF sceneX(QQuickItem *item)
{
    return item->mapToScene(QPointF());
}

/// A press at \a from, a run of moves past the drag threshold and a release at
/// \a to; \a atPeak runs with the button still down and the pointer at \a to.
void drag(QQuickWindow *window, const QPoint &from, const QPoint &to,
          const std::function<void()> &atPeak = {})
{
    QTest::mousePress(window, Qt::LeftButton, Qt::NoModifier, from);
    constexpr int steps = 10;
    for (int step = 1; step <= steps; ++step) {
        QTest::mouseMove(window, from + (to - from) * step / steps);
        QTest::qWait(5);
    }
    if (atPeak)
        atPeak();
    QTest::mouseRelease(window, Qt::LeftButton, Qt::NoModifier, to);
    QTest::qWait(30);
}

bool closeColour(const QColor &actual, const QColor &expected)
{
    return qAbs(actual.red() - expected.red()) <= 8 && qAbs(actual.green() - expected.green()) <= 8
           && qAbs(actual.blue() - expected.blue()) <= 8;
}

// The history page on the mock backend, with a real AppController whose
// settings live in a temporary INI, so what the header stores can be read back.
struct PageFixture {
    QTemporaryDir directory;
    mr::MockBackend backend;
    std::unique_ptr<mr::AppSettings> settings;
    std::unique_ptr<mr::AppController> app;
    mr::Formatters formatters;
    mr::JobCatalog jobs;
    mr::RoleCatalog roles;
    mr::RunFormValidator validator;
    QQmlEngine engine;
    std::unique_ptr<QObject> root;
    QQuickWindow *window = nullptr;
    QQuickItem *page = nullptr;
    QStringList warnings;
    QString error;

    QString iniPath() const { return directory.filePath(QStringLiteral("columns.ini")); }

    bool create(QObject *owner, int width = 1280, int height = 800)
    {
        if (!directory.isValid()) {
            error = QStringLiteral("no temporary directory");
            return false;
        }
        settings = std::make_unique<mr::AppSettings>(iniPath(), nullptr);
        app = std::make_unique<mr::AppController>(&backend, settings.get(), nullptr, nullptr,
                                                  mr::TtsService::EngineMode::None);
        auto *const context = engine.rootContext();
        context->setContextProperty(QStringLiteral("App"), app.get());
        context->setContextProperty(QStringLiteral("Fmt"), &formatters);
        context->setContextProperty(QStringLiteral("Jobs"), &jobs);
        context->setContextProperty(QStringLiteral("Roles"), &roles);
        context->setContextProperty(QStringLiteral("RunForm"), &validator);
        context->setContextProperty(QStringLiteral("Settings"), settings.get());
        context->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QObject::connect(&engine, &QQmlEngine::warnings, owner, [this](const QList<QQmlError> &errors) {
            for (const auto &problem : errors)
                warnings.append(problem.toString());
        });
        QQmlComponent component(&engine);
        component.setData(QStringLiteral(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    visible: true; width: %1; height: %2; color: Theme.windowBackground
    HistoryPage { objectName: "historyUnderTest"; anchors.fill: parent; anchors.margins: 24 }
})").arg(width).arg(height).toUtf8(), QUrl());
        root.reset(component.create());
        if (!root) {
            error = component.errorString();
            return false;
        }
        window = qobject_cast<QQuickWindow *>(root.get());
        if (!window) {
            error = QStringLiteral("the root is not a window");
            return false;
        }
        page = findVisual(window->contentItem(), QStringLiteral("historyUnderTest"));
        if (!page) {
            error = QStringLiteral("no history page");
            return false;
        }
        return true;
    }

    mr::HistoryColumnsController *columns() const { return app->historyColumns(); }
    QQuickItem *item(const QString &name) const { return findVisual(window->contentItem(), name); }
    QQuickItem *title(const QString &key) const { return item(QStringLiteral("historyColumnHeader_") + key); }
    QQuickItem *cell(const QString &key) const { return item(QStringLiteral("historyCell_") + key); }
    QQuickItem *handle(const QString &key) const { return item(QStringLiteral("historyColumnResize_") + key); }

    bool save(const QString &name) const
    {
        QDir().mkpath(kEvidence());
        const QImage frame = window->grabWindow();
        return !frame.isNull() && frame.save(kEvidence() + QLatin1Char('/') + name + QStringLiteral(".png"));
    }
};
} // namespace

namespace {
class HistoryColumnsTests final : public QObject
{
    Q_OBJECT
private:
    static bool waitForRows(PageFixture &fixture)
    {
        for (int attempt = 0; attempt < 200; ++attempt) {
            if (!fixture.app->runs()->isLoading() && fixture.app->runs()->rowCount() > 0
                && fixture.title(QStringLiteral("date")) && fixture.cell(QStringLiteral("date")))
                return true;
            QTest::qWait(25);
        }
        return false;
    }

private Q_SLOTS:
    void initTestCase()
    {
        const QString root = QString::fromUtf8(MR_DESKTOP_QML_DIR);
        qmlRegisterSingletonType(QUrl::fromLocalFile(root + QStringLiteral("/Theme.qml")), "MentorRecorder", 1, 0, "Theme");
        for (const QString &folder : {QStringLiteral("/components"), QStringLiteral("/dialogs")})
            for (const QString &file : QDir(root + folder).entryList({QStringLiteral("*.qml")}, QDir::Files))
                qmlRegisterType(QUrl::fromLocalFile(root + folder + QLatin1Char('/') + file), "MentorRecorder", 1, 0,
                                file.chopped(4).toUtf8().constData());
        qmlRegisterType(QUrl::fromLocalFile(root + QStringLiteral("/pages/HistoryPage.qml")), "MentorRecorder", 1, 0, "HistoryPage");
    }

    // ------------------------------------------------------- controller --
    void defaultsAndBounds()
    {
        mr::HistoryColumnsController columns(nullptr);
        QSignalSpy changed(&columns, &mr::HistoryColumnsController::layoutChanged);
        QCOMPARE(columns.order(), defaults());
        QCOMPARE(columns.visible(), defaults());
        QVERIFY(columns.widths().isEmpty());
        QVERIFY(!columns.customised());
        QCOMPARE(columns.width(QStringLiteral("date")), 0);
        QVERIFY(columns.canHide(QStringLiteral("date")));
        QVERIFY(!columns.canHide(QStringLiteral("bogus")));

        columns.setVisible(QStringLiteral("date"), false);
        QVERIFY(!columns.isVisible(QStringLiteral("date")));
        QCOMPARE(columns.visible().size(), defaults().size() - 1);
        QCOMPARE(columns.order(), defaults()); // 隐藏不改变顺序。
        QVERIFY(columns.customised());
        QCOMPARE(changed.size(), 1);
        columns.setVisible(QStringLiteral("date"), false); // 已隐藏：无变化。
        QCOMPARE(changed.size(), 1);
        columns.setVisible(QStringLiteral("bogus"), false);
        QCOMPARE(changed.size(), 1);

        // The last shown column cannot go.
        for (const QString &key : defaults())
            columns.setVisible(key, false);
        QCOMPARE(columns.visible(), QStringList{QStringLiteral("marks")});
        QVERIFY(!columns.canHide(QStringLiteral("marks")));
        columns.setVisible(QStringLiteral("marks"), false);
        QCOMPARE(columns.visible(), QStringList{QStringLiteral("marks")});
        columns.setVisible(QStringLiteral("date"), true);
        QCOMPARE(columns.visible(), QStringList({QStringLiteral("date"), QStringLiteral("marks")}));

        // Widths are clamped; 0 returns the column to the page's default.
        columns.setWidth(QStringLiteral("date"), 10);
        QCOMPARE(columns.width(QStringLiteral("date")), mr::HistoryColumnsController::kMinimumWidth);
        columns.setWidth(QStringLiteral("date"), 5000);
        QCOMPARE(columns.width(QStringLiteral("date")), mr::HistoryColumnsController::kMaximumWidth);
        columns.setWidth(QStringLiteral("date"), 140);
        QCOMPARE(columns.widths().value(QStringLiteral("date")).toInt(), 140);
        const qsizetype before = changed.size();
        columns.setWidth(QStringLiteral("date"), 140);
        columns.setWidth(QStringLiteral("bogus"), 140);
        QCOMPARE(changed.size(), before);
        columns.setWidth(QStringLiteral("date"), 0);
        QVERIFY(columns.widths().isEmpty());

        columns.reset();
        QCOMPARE(columns.order(), defaults());
        QCOMPARE(columns.visible(), defaults());
        QVERIFY(!columns.customised());
        const qsizetype afterReset = changed.size();
        columns.reset(); // 已是默认：无变化。
        QCOMPARE(changed.size(), afterReset);
    }

    void moveKeepsHiddenColumnsInPlace()
    {
        mr::HistoryColumnsController columns(nullptr);
        columns.setVisible(QStringLiteral("entered"), false);
        columns.moveTo(QStringLiteral("duty"), 0);
        QCOMPARE(columns.visible().first(), QStringLiteral("duty"));
        // 进本 keeps its slot between 日期 and 结束; the shown columns take the new order.
        QStringList expected{QStringLiteral("duty"), QStringLiteral("entered"), QStringLiteral("date"),
                             QStringLiteral("ended"), QStringLiteral("category"), QStringLiteral("job"),
                             QStringLiteral("result"), QStringLiteral("duration"), QStringLiteral("source"),
                             QStringLiteral("marks")};
        QCOMPARE(columns.order(), expected);
        columns.moveTo(QStringLiteral("duty"), 999); // 越界：落到最后。
        QCOMPARE(columns.visible().last(), QStringLiteral("duty"));
        QCOMPARE(columns.order().indexOf(QStringLiteral("entered")), 1);
        columns.moveTo(QStringLiteral("entered"), 0); // 隐藏的列不能拖。
        QCOMPARE(columns.order().indexOf(QStringLiteral("entered")), 1);
        columns.moveTo(QStringLiteral("bogus"), 0);
        const QStringList unchanged = columns.order();
        columns.moveTo(QStringLiteral("duty"), columns.visible().size() - 1); // 原位：无变化。
        QCOMPARE(columns.order(), unchanged);
    }

    void persistsByKeyAcrossRestart()
    {
        QTemporaryDir directory;
        QVERIFY(directory.isValid());
        const QString path = directory.filePath(QStringLiteral("columns.ini"));
        {
            mr::AppSettings settings(path, nullptr);
            mr::HistoryColumnsController columns(&settings);
            columns.setVisible(QStringLiteral("source"), false);
            columns.moveTo(QStringLiteral("marks"), 0);
            columns.setWidth(QStringLiteral("date"), 150);
            columns.setWidth(QStringLiteral("job"), 90);
            QCOMPARE(settings.historyHiddenColumns(), QStringList{QStringLiteral("source")});
            QCOMPARE(settings.historyColumnOrder().first(), QStringLiteral("marks"));
            QCOMPARE(settings.historyColumnWidths().value(QStringLiteral("date")).toInt(), 150);
        }
        {
            mr::AppSettings settings(path, nullptr);
            mr::HistoryColumnsController columns(&settings);
            QVERIFY(columns.customised());
            QVERIFY(!columns.isVisible(QStringLiteral("source")));
            QCOMPARE(columns.visible().first(), QStringLiteral("marks"));
            QCOMPARE(columns.order().size(), defaults().size());
            QCOMPARE(columns.width(QStringLiteral("date")), 150);
            QCOMPARE(columns.width(QStringLiteral("job")), 90);
            QCOMPARE(columns.width(QStringLiteral("duty")), 0);
        }
        // Keys, never titles: the interface language has no say in the file.
        QFile file(path);
        QVERIFY(file.open(QIODevice::ReadOnly));
        const QString text = QString::fromUtf8(file.readAll());
        QVERIFY(text.contains(QStringLiteral("column_order=")));
        QVERIFY(text.contains(QStringLiteral("hidden_columns=")));
        QVERIFY(text.contains(QStringLiteral("column_width")));
        QVERIFY(!text.contains(QString::fromUtf8("日期")));
        QVERIFY(!text.contains(QString::fromUtf8("标记")));
        {
            mr::AppSettings settings(path, nullptr);
            mr::HistoryColumnsController columns(&settings);
            columns.reset();
            QVERIFY(settings.historyColumnOrder().isEmpty());
            QVERIFY(settings.historyHiddenColumns().isEmpty());
            QVERIFY(settings.historyColumnWidths().isEmpty());
        }
        mr::AppSettings settings(path, nullptr);
        mr::HistoryColumnsController columns(&settings);
        QVERIFY(!columns.customised());
    }

    void toleratesACorruptFile()
    {
        QTemporaryDir directory;
        QVERIFY(directory.isValid());
        const QString path = directory.filePath(QStringLiteral("columns.ini"));
        {
            QSettings raw(path, QSettings::IniFormat);
            raw.setValue(QStringLiteral("history/column_order"),
                         QStringList({QStringLiteral("bogus"), QStringLiteral("marks"), QStringLiteral("marks"), QStringLiteral("date")}));
            raw.setValue(QStringLiteral("history/hidden_columns"), defaults() << QStringLiteral("bogus"));
            raw.setValue(QStringLiteral("history/column_width/job"), 12);
            raw.setValue(QStringLiteral("history/column_width/bogus"), 200);
            raw.setValue(QStringLiteral("history/column_width/date"), QStringLiteral("wide"));
            raw.setValue(QStringLiteral("history/column_width/duty"), -5);
        }
        mr::AppSettings settings(path, nullptr);
        mr::HistoryColumnsController columns(&settings);
        // Unknown keys go, repeats collapse, the rest follow in the design order.
        QStringList expected{QStringLiteral("marks"), QStringLiteral("date")};
        for (const QString &key : defaults())
            if (!expected.contains(key))
                expected.append(key);
        QCOMPARE(columns.order(), expected);
        // Everything hidden shows everything.
        QCOMPARE(columns.visible(), expected);
        QCOMPARE(columns.width(QStringLiteral("job")), mr::HistoryColumnsController::kMinimumWidth);
        QCOMPARE(columns.width(QStringLiteral("bogus")), 0);
        QCOMPARE(columns.width(QStringLiteral("date")), 0);
        QCOMPARE(columns.width(QStringLiteral("duty")), 0);
        QCOMPARE(columns.widths().size(), 1);
    }

    // ------------------------------------------------------------- page --
    void pageFollowsTheController()
    {
        PageFixture fixture;
        QVERIFY2(fixture.create(this), qPrintable(fixture.error));
        QVERIFY(QTest::qWaitForWindowExposed(fixture.window));
        QVERIFY(waitForRows(fixture));
        auto *columns = fixture.columns();
        QVERIFY(columns);

        // Every column: a title and, under it at the same x and width, a cell.
        qreal lastX = -1;
        for (const QString &key : defaults()) {
            QQuickItem *title = fixture.title(key);
            QQuickItem *cell = fixture.cell(key);
            QVERIFY2(title && cell, qPrintable(key));
            QVERIFY(title->isVisible());
            QVERIFY(cell->isVisible());
            QVERIFY(title->width() > 0);
            QCOMPARE(sceneX(title->parentItem()).x(), sceneX(cell).x());
            QCOMPARE(title->parentItem()->width(), cell->width());
            QVERIFY(sceneX(cell).x() > lastX);
            lastX = sceneX(cell).x();
        }
        // 副本 takes what the others leave: wider than its 228 px default.
        QVERIFY(fixture.cell(QStringLiteral("duty"))->width() > 228);
        auto *selectAll = fixture.item(QStringLiteral("selectCurrentHistoryPage"));
        auto *header = fixture.item(QStringLiteral("historyTableHeader"));
        QVERIFY(selectAll && header);
        QCOMPARE(selectAll->parentItem()->parentItem(), header);
        QVERIFY(fixture.save(QStringLiteral("page-default")));

        columns->setVisible(QStringLiteral("source"), false);
        QTRY_VERIFY(!fixture.title(QStringLiteral("source")));
        QTRY_VERIFY(!fixture.cell(QStringLiteral("source"))->isVisible());
        QVERIFY(fixture.cell(QStringLiteral("marks"))->isVisible());

        columns->setWidth(QStringLiteral("date"), 140);
        QTRY_COMPARE(fixture.title(QStringLiteral("date"))->parentItem()->width(), 140.0);
        QCOMPARE(fixture.cell(QStringLiteral("date"))->width(), 140.0);
        QCOMPARE(sceneX(fixture.cell(QStringLiteral("entered"))).x(), sceneX(fixture.cell(QStringLiteral("date"))).x() + 140);

        columns->moveTo(QStringLiteral("marks"), 0);
        QTRY_VERIFY(sceneX(fixture.cell(QStringLiteral("marks"))).x() < sceneX(fixture.cell(QStringLiteral("date"))).x());
        QVERIFY(sceneX(fixture.title(QStringLiteral("marks"))->parentItem()).x()
                < sceneX(fixture.title(QStringLiteral("date"))->parentItem()).x());
        QVERIFY(fixture.save(QStringLiteral("page-customised")));

        // A dragged-out width on 副本 fixes it: the table's right end is left free.
        columns->setWidth(QStringLiteral("duty"), 200);
        QTRY_COMPARE(fixture.cell(QStringLiteral("duty"))->width(), 200.0);

        columns->reset();
        QTRY_VERIFY(fixture.title(QStringLiteral("source")));
        QTRY_VERIFY(fixture.cell(QStringLiteral("source"))->isVisible());
        QVERIFY(fixture.cell(QStringLiteral("duty"))->width() > 228);
        QVERIFY(sceneX(fixture.cell(QStringLiteral("marks"))).x() > sceneX(fixture.cell(QStringLiteral("date"))).x());
        QVERIFY2(fixture.warnings.isEmpty(), qPrintable(fixture.warnings.join(QLatin1Char('\n'))));
    }

    void headerDragsReorderAndResizeButOnlyClicksSort()
    {
        PageFixture fixture;
        QVERIFY2(fixture.create(this), qPrintable(fixture.error));
        QVERIFY(QTest::qWaitForWindowExposed(fixture.window));
        QVERIFY(waitForRows(fixture));
        auto *columns = fixture.columns();
        auto *runs = fixture.app->runs();
        QCOMPARE(runs->sortField(), QStringLiteral("history_date"));

        // Carry 职业 to the left of 类型: the drop guide shows while it is held.
        QQuickItem *job = fixture.title(QStringLiteral("job"));
        QQuickItem *category = fixture.title(QStringLiteral("category"));
        QVERIFY(job && category);
        const QPoint target(static_cast<int>(sceneX(category->parentItem()).x()) + 4, centreOf(category).y());
        bool guideSeen = false;
        drag(fixture.window, centreOf(job), target, [&] {
            auto *guide = fixture.item(QStringLiteral("historyColumnDropGuide"));
            if (!guide || !guide->isVisible())
                return;
            const QImage frame = fixture.window->grabWindow();
            const QPoint probe = guide->mapToScene(QPointF(1, 8)).toPoint();
            guideSeen = closeColour(frame.pixelColor(probe), guide->property("color").value<QColor>());
            QDir().mkpath(kEvidence());
            frame.save(kEvidence() + QStringLiteral("/header-reorder-drag.png"));
        });
        QVERIFY2(guideSeen, "the drop guide was not drawn where the title would land");
        QTRY_VERIFY(columns->visible().indexOf(QStringLiteral("job")) < columns->visible().indexOf(QStringLiteral("category")));
        QCOMPARE(columns->visible().indexOf(QStringLiteral("job")), columns->visible().indexOf(QStringLiteral("duty")) + 1);
        QCOMPARE(runs->sortField(), QStringLiteral("history_date")); // 拖动不排序。
        QVERIFY(!runs->sortAscending());

        // A sortable title carried and dropped: still no sort, in either direction.
        QQuickItem *ended = fixture.title(QStringLiteral("ended"));
        QQuickItem *entered = fixture.title(QStringLiteral("entered"));
        QVERIFY(ended && entered);
        drag(fixture.window, centreOf(ended), QPoint(static_cast<int>(sceneX(entered->parentItem()).x()) + 4, centreOf(entered).y()));
        QTRY_VERIFY(columns->visible().indexOf(QStringLiteral("ended")) < columns->visible().indexOf(QStringLiteral("entered")));
        QCOMPARE(runs->sortField(), QStringLiteral("history_date"));
        QVERIFY(!runs->sortAscending());
        // Carried and dropped back where it was: nothing changes, nothing sorts.
        const QStringList orderBefore = columns->visible();
        QQuickItem *marks = fixture.title(QStringLiteral("marks"));
        QVERIFY(marks);
        drag(fixture.window, centreOf(marks), centreOf(marks) + QPoint(14, 0));
        QTest::qWait(60);
        QCOMPARE(columns->visible(), orderBefore);
        QCOMPARE(runs->sortField(), QStringLiteral("history_date"));
        QVERIFY(!runs->sortAscending());

        // A click still sorts; so does a press that never leaves the threshold.
        QTest::mouseClick(fixture.window, Qt::LeftButton, Qt::NoModifier, centreOf(fixture.title(QStringLiteral("duty"))));
        QTRY_COMPARE(runs->sortField(), QStringLiteral("duty_name"));
        QVERIFY(!runs->sortAscending());
        const QPoint datePoint = centreOf(fixture.title(QStringLiteral("date")));
        QTest::mousePress(fixture.window, Qt::LeftButton, Qt::NoModifier, datePoint);
        QTest::mouseMove(fixture.window, datePoint + QPoint(2, 0));
        QTest::mouseRelease(fixture.window, Qt::LeftButton, Qt::NoModifier, datePoint + QPoint(2, 0));
        QTRY_COMPARE(runs->sortField(), QStringLiteral("history_date"));
        QCOMPARE(columns->visible().indexOf(QStringLiteral("date")), 0); // 没有越过阈值：不算拖动。

        // The width handle: 日期 grows by what the pointer travelled, rows following.
        QQuickItem *handle = fixture.handle(QStringLiteral("date"));
        QVERIFY(handle);
        const int before = qRound(fixture.cell(QStringLiteral("date"))->width());
        QVERIFY(!runs->sortAscending());
        bool edgeSeen = false;
        drag(fixture.window, centreOf(handle), centreOf(handle) + QPoint(40, 0), [&] {
            auto *guide = fixture.item(QStringLiteral("historyColumnResizeGuide"));
            if (!guide || !guide->isVisible())
                return;
            // The rows already show the new width while the button is down.
            if (qRound(fixture.cell(QStringLiteral("date"))->width()) != before + 40)
                return;
            const QImage frame = fixture.window->grabWindow();
            const QPoint probe = guide->mapToScene(QPointF(1, 8)).toPoint();
            edgeSeen = closeColour(frame.pixelColor(probe), guide->property("color").value<QColor>());
            frame.save(kEvidence() + QStringLiteral("/header-resize-drag.png"));
        });
        QVERIFY2(edgeSeen, "the width guide was not drawn at the new edge");
        QTRY_COMPARE(columns->width(QStringLiteral("date")), before + 40);
        QCOMPARE(qRound(fixture.cell(QStringLiteral("date"))->width()), before + 40);
        QCOMPARE(qRound(fixture.title(QStringLiteral("date"))->parentItem()->width()), before + 40);
        QCOMPARE(runs->sortField(), QStringLiteral("history_date")); // 拖边缘也不排序。
        QVERIFY(!runs->sortAscending());

        // The edge of a sortable column that is not the sort column: no sort either.
        QQuickItem *durationHandle = fixture.handle(QStringLiteral("duration"));
        QVERIFY(durationHandle);
        const int durationBefore = qRound(fixture.cell(QStringLiteral("duration"))->width());
        drag(fixture.window, centreOf(durationHandle), centreOf(durationHandle) + QPoint(20, 0));
        QTRY_COMPARE(columns->width(QStringLiteral("duration")), durationBefore + 20);
        QCOMPARE(runs->sortField(), QStringLiteral("history_date"));
        QVERIFY(!runs->sortAscending());
        // A click on the edge sorts nothing; a double click puts the default width back.
        QTest::mouseClick(fixture.window, Qt::LeftButton, Qt::NoModifier, centreOf(durationHandle));
        QTest::qWait(60);
        QCOMPARE(runs->sortField(), QStringLiteral("history_date"));
        QVERIFY(!runs->sortAscending());
        QTest::mouseDClick(fixture.window, Qt::LeftButton, Qt::NoModifier, centreOf(durationHandle));
        QTRY_COMPARE(columns->width(QStringLiteral("duration")), 0);
        QCOMPARE(qRound(fixture.cell(QStringLiteral("duration"))->width()), durationBefore);
        QCOMPARE(runs->sortField(), QStringLiteral("history_date"));
        QVERIFY(!runs->sortAscending());
        // An edge drag that comes back and lets go where it began stores nothing.
        const QPoint origin = centreOf(durationHandle);
        QTest::mousePress(fixture.window, Qt::LeftButton, Qt::NoModifier, origin);
        for (int step = 1; step <= 6; ++step) {
            QTest::mouseMove(fixture.window, origin + QPoint(5 * step, 0));
            QTest::qWait(5);
        }
        QTRY_COMPARE(qRound(fixture.cell(QStringLiteral("duration"))->width()), durationBefore + 30);
        QTest::mouseMove(fixture.window, origin);
        QTest::qWait(5);
        QTest::mouseRelease(fixture.window, Qt::LeftButton, Qt::NoModifier, origin);
        QTest::qWait(60);
        QCOMPARE(columns->width(QStringLiteral("duration")), 0);
        QCOMPARE(qRound(fixture.cell(QStringLiteral("duration"))->width()), durationBefore);

        // Narrower than its narrowest is refused; the handle stays usable.
        drag(fixture.window, centreOf(handle), centreOf(handle) - QPoint(600, 0));
        QTRY_VERIFY(columns->width(QStringLiteral("date")) >= mr::HistoryColumnsController::kMinimumWidth);
        QVERIFY(columns->width(QStringLiteral("date")) < before);

        // Everything reached the file.
        mr::AppSettings reopened(fixture.iniPath(), nullptr);
        QCOMPARE(reopened.historyColumnWidths().value(QStringLiteral("date")).toInt(), columns->width(QStringLiteral("date")));
        const QStringList order = reopened.historyColumnOrder();
        QVERIFY(order.indexOf(QStringLiteral("job")) < order.indexOf(QStringLiteral("category")));
        QVERIFY2(fixture.warnings.isEmpty(), qPrintable(fixture.warnings.join(QLatin1Char('\n'))));
    }

    // The page at the app's own table width (a 1280 px window leaves the header
    // 1007 px: 1280 less the sidebar, the page margins and the scroll gutter),
    // where the default widths already fill the table.
    void realTableWidthKeepsDefaultsAndFollowsThePointer()
    {
        PageFixture fixture;
        // The page sits in a 24 px margin inside the window; the header is 16 px narrower than the page.
        QVERIFY2(fixture.create(this, 1007 + 48 + 16, 800), qPrintable(fixture.error));
        QVERIFY(QTest::qWaitForWindowExposed(fixture.window));
        QVERIFY(waitForRows(fixture));
        auto *columns = fixture.columns();
        auto *header = fixture.item(QStringLiteral("historyTableHeader"));
        QVERIFY(header);
        QCOMPARE(qRound(header->width()), 1007);
        const qreal available = header->width() - 8 - 32;

        // Every fixed column keeps its tuned width (classic or eorzea); 副本 takes the rest.
        const QMap<QString, QList<int>> tuned{
            {QStringLiteral("date"), {96, 86}}, {QStringLiteral("entered"), {70, 64}}, {QStringLiteral("ended"), {70, 64}},
            {QStringLiteral("category"), {84}}, {QStringLiteral("job"), {112}}, {QStringLiteral("result"), {92}},
            {QStringLiteral("duration"), {66}}, {QStringLiteral("source"), {60}}, {QStringLiteral("marks"), {92}}};
        qreal fixed = 0;
        for (auto it = tuned.cbegin(); it != tuned.cend(); ++it) {
            const int width = qRound(fixture.cell(it.key())->width());
            QVERIFY2(it.value().contains(width), qPrintable(it.key() + QStringLiteral(": ") + QString::number(width)));
            fixed += width;
        }
        QCOMPARE(qRound(fixture.cell(QStringLiteral("duty"))->width()), qRound(available - fixed));
        QVERIFY(fixture.cell(QStringLiteral("duty"))->width() >= 200);
        QVERIFY(fixture.save(QStringLiteral("page-real-width")));

        // A drag follows the pointer exactly; only 副本 gives way.
        QQuickItem *handle = fixture.handle(QStringLiteral("date"));
        QVERIFY(handle);
        const int dateBefore = qRound(fixture.cell(QStringLiteral("date"))->width());
        const int dutyBefore = qRound(fixture.cell(QStringLiteral("duty"))->width());
        drag(fixture.window, centreOf(handle), centreOf(handle) + QPoint(35, 0));
        QTRY_COMPARE(columns->width(QStringLiteral("date")), dateBefore + 35);
        QCOMPARE(qRound(fixture.cell(QStringLiteral("date"))->width()), dateBefore + 35);
        QCOMPARE(qRound(fixture.cell(QStringLiteral("duty"))->width()), dutyBefore - 35);
        QCOMPARE(qRound(fixture.cell(QStringLiteral("job"))->width()), 112);

        // Beyond what the table has: the drag stops where 副本 reaches its narrowest.
        drag(fixture.window, centreOf(handle), centreOf(handle) + QPoint(600, 0));
        QTRY_COMPARE(qRound(fixture.cell(QStringLiteral("duty"))->width()), 120);
        QCOMPARE(columns->width(QStringLiteral("date")), dateBefore + dutyBefore - 120);
        qreal total = 0;
        for (const QString &key : defaults())
            total += fixture.cell(key)->width();
        QCOMPARE(qRound(total), qRound(available));

        // A window too narrow for the columns squeezes them; a touched edge does
        // not jump, cannot widen, and still narrows by what the pointer moved.
        fixture.window->resize(900, 800);
        QTRY_COMPARE(qRound(header->width()), 900 - 64);
        QTest::qWait(60);
        const int squeezedDate = qRound(fixture.cell(QStringLiteral("date"))->width());
        QVERIFY(squeezedDate < columns->width(QStringLiteral("date")));
        const int storedDate = columns->width(QStringLiteral("date"));
        drag(fixture.window, centreOf(handle), centreOf(handle) + QPoint(30, 0));
        QTest::qWait(60);
        QCOMPARE(qRound(fixture.cell(QStringLiteral("date"))->width()), squeezedDate);
        QCOMPARE(columns->width(QStringLiteral("date")), storedDate);
        // 20 px: Qt's drag threshold is 10, and a shorter press is a click.
        drag(fixture.window, centreOf(handle), centreOf(handle) - QPoint(20, 0));
        QTRY_COMPARE(columns->width(QStringLiteral("date")), squeezedDate - 20);
        // Released, the column shares the squeeze with the others again, so it
        // shows at most what was stored and less than before the drag.
        const int shownDate = qRound(fixture.cell(QStringLiteral("date"))->width());
        QVERIFY(shownDate <= squeezedDate - 20);
        QVERIFY(shownDate >= 80);
        QVERIFY(fixture.save(QStringLiteral("page-squeezed")));
        QVERIFY2(fixture.warnings.isEmpty(), qPrintable(fixture.warnings.join(QLatin1Char('\n'))));
    }

    void dialogTogglesColumnsAndRestoresDefaults()
    {
        PageFixture fixture;
        QVERIFY2(fixture.create(this), qPrintable(fixture.error));
        QVERIFY(QTest::qWaitForWindowExposed(fixture.window));
        QVERIFY(waitForRows(fixture));
        auto *columns = fixture.columns();

        auto *button = fixture.item(QStringLiteral("historyCustomizeColumns"));
        QVERIFY(button && button->isVisible());
        QTest::mouseClick(fixture.window, Qt::LeftButton, Qt::NoModifier, centreOf(button));
        QQuickItem *sourceToggle = nullptr;
        QTRY_VERIFY((sourceToggle = fixture.item(QStringLiteral("historyColumnToggle_source"))) && sourceToggle->isVisible());
        QVERIFY(sourceToggle->property("checked").toBool());
        auto *reset = fixture.item(QStringLiteral("historyColumnsReset"));
        QVERIFY(reset);
        QVERIFY(!reset->isEnabled()); // 没有自定义过：没有可恢复的。
        QVERIFY(fixture.save(QStringLiteral("dialog-default")));

        QTest::mouseClick(fixture.window, Qt::LeftButton, Qt::NoModifier, centreOf(sourceToggle));
        QTRY_VERIFY(!columns->isVisible(QStringLiteral("source")));
        QVERIFY(!sourceToggle->property("checked").toBool());
        QTRY_VERIFY(reset->isEnabled());

        // Down to one column: its box cannot be unchecked.
        for (const QString &key : defaults())
            if (key != QLatin1String("date"))
                columns->setVisible(key, false);
        auto *dateToggle = fixture.item(QStringLiteral("historyColumnToggle_date"));
        QVERIFY(dateToggle);
        QTRY_VERIFY(!dateToggle->isEnabled());
        QVERIFY(dateToggle->property("checked").toBool());
        QTest::mouseClick(fixture.window, Qt::LeftButton, Qt::NoModifier, centreOf(dateToggle));
        QCOMPARE(columns->visible(), QStringList{QStringLiteral("date")});
        QVERIFY(fixture.save(QStringLiteral("dialog-one-column")));

        QTest::mouseClick(fixture.window, Qt::LeftButton, Qt::NoModifier, centreOf(reset));
        QTRY_COMPARE(columns->visible(), defaults());
        QTRY_VERIFY(!reset->isEnabled());
        QVERIFY(sourceToggle->property("checked").toBool());
        QVERIFY(dateToggle->isEnabled());

        auto *done = fixture.item(QStringLiteral("historyColumnsDone"));
        QVERIFY(done);
        QTest::mouseClick(fixture.window, Qt::LeftButton, Qt::NoModifier, centreOf(done));
        QTRY_VERIFY(!sourceToggle->isVisible());

        // A right click on the header opens the same dialog.
        QTest::mouseClick(fixture.window, Qt::RightButton, Qt::NoModifier, centreOf(fixture.title(QStringLiteral("date"))));
        QTRY_VERIFY(sourceToggle->isVisible());
        QCOMPARE(fixture.app->runs()->sortField(), QStringLiteral("history_date")); // 右键不排序。
        QTest::keyClick(fixture.window, Qt::Key_Escape);
        QTRY_VERIFY(!sourceToggle->isVisible());
        QVERIFY2(fixture.warnings.isEmpty(), qPrintable(fixture.warnings.join(QLatin1Char('\n'))));
    }
};
} // namespace

int main(int argc, char **argv)
{
    mrtest::disableCollectorLaunch();
    if (qEnvironmentVariableIsEmpty("QT_QPA_FONTDIR"))
        qputenv("QT_QPA_FONTDIR", "C:/Windows/Fonts");
    QGuiApplication app(argc, argv);
    // Match main.cpp's screenshot font initialization; offscreen defaults may
    // otherwise choose a condensed fallback and conceal real layout overflow.
    QFontDatabase::addApplicationFont(QStringLiteral("C:/Windows/Fonts/msyh.ttc"));
    QFontDatabase::addApplicationFont(QStringLiteral("C:/Windows/Fonts/simhei.ttf"));
    QFontDatabase::addApplicationFont(QStringLiteral("C:/Windows/Fonts/consola.ttf"));
    QFontDatabase::addApplicationFont(QString::fromUtf8(MR_DESKTOP_QML_DIR) + QStringLiteral("/../resources/fonts/IBMPlexMono-SemiBold.ttf"));
    QFont font;
    font.setFamilies({QStringLiteral("Microsoft YaHei UI"), QStringLiteral("Microsoft YaHei"), QStringLiteral("Segoe UI")});
    QGuiApplication::setFont(font);
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    HistoryColumnsTests tests;
    return QTest::qExec(&tests, argc, argv);
}
#include "HistoryColumnsTests.moc"
