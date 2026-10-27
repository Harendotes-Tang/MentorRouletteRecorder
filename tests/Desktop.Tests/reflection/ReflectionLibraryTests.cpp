#include "DesktopTestRunner.h"
#include "IBackend.h"
#include "ReflectionLibraryController.h"
#include "RunListModel.h"
#include "Formatters.h"
#include "JobCatalog.h"

#include <QGuiApplication>
#include <QFont>
#include <QFontDatabase>
#include <QDir>
#include <QJsonArray>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QSignalSpy>
#include <QTest>
#include <QTimer>
#include <memory>

namespace {
class QueryBackend final : public mr::IBackend
{
public:
    QString backendName() const override { return QStringLiteral("test"); }
    bool isConnected() const override { return true; }
    QList<QJsonObject> queries;
    bool reject = false;
    mr::BackendReply *request(const QString &type, const QJsonObject &payload) override {
        queries.append(payload);
        auto *reply = new mr::BackendReply(QStringLiteral("test"), type, this);
        const bool rejected = reject;
        const int page = payload.value(QStringLiteral("page")).toInt(1);
        QTimer::singleShot(0, reply, [reply, rejected, page] {
            if (rejected) {
                reply->fail(QStringLiteral("ERR_QUERY"), QStringLiteral("数据库暂不可读"));
                return;
            }
            QJsonArray items;
            items.append(QJsonObject{{QStringLiteral("run_id"), QStringLiteral("page-%1").arg(page)},
                {QStringLiteral("duty_name"), QStringLiteral("长中文副本名称用于选择验证")},
                {QStringLiteral("job_id"), 24}, {QStringLiteral("job_name"), QStringLiteral("白魔法师")},
                {QStringLiteral("result"), QStringLiteral("COMPLETED")},
                {QStringLiteral("reflection"), QJsonObject{{QStringLiteral("text"), QStringLiteral("完整心得")}}}});
            reply->succeed({{QStringLiteral("items"), items}, {QStringLiteral("page_info"),
                QJsonObject{{QStringLiteral("page"), page}, {QStringLiteral("total"), 31}}}});
        });
        return reply;
    }
};
/// Runs kept in memory and queried the way the Collector does: soft-deleted runs
/// and runs without reflection text are left out, run_id narrows, pages slice.
class StoreBackend final : public mr::IBackend
{
public:
    QString backendName() const override { return QStringLiteral("store"); }
    bool isConnected() const override { return true; }
    QList<QJsonObject> runs;
    bool reject = false;
    void add(const QString &id, const QString &text) {
        runs.append({{QStringLiteral("run_id"), id}, {QStringLiteral("duty_name"), QStringLiteral("心得副本")},
                     {QStringLiteral("result"), QStringLiteral("COMPLETED")},
                     {QStringLiteral("reflection"), QJsonObject{{QStringLiteral("text"), text}}}});
    }
    void update(const QString &id, const QString &key, const QJsonValue &value) {
        for (QJsonObject &run : runs)
            if (run.value(QStringLiteral("run_id")).toString() == id)
                run.insert(key, value);
    }
    mr::BackendReply *request(const QString &type, const QJsonObject &payload) override {
        auto *reply = new mr::BackendReply(QStringLiteral("store"), type, this);
        const QJsonObject filter = payload.value(QStringLiteral("filter")).toObject();
        QStringList ids;
        for (const auto &id : filter.value(QStringLiteral("run_id")).toArray())
            ids.append(id.toString());
        QJsonArray matching;
        for (const QJsonObject &run : runs) {
            if (run.value(QStringLiteral("soft_deleted")).toBool())
                continue;
            if (filter.value(QStringLiteral("with_reflection")).toBool()
                && run.value(QStringLiteral("reflection")).toObject().value(QStringLiteral("text")).toString().isEmpty())
                continue;
            if (filter.contains(QStringLiteral("run_id")) && !ids.contains(run.value(QStringLiteral("run_id")).toString()))
                continue;
            matching.append(run);
        }
        const int page = payload.value(QStringLiteral("page")).toInt(1);
        const int size = payload.value(QStringLiteral("page_size")).toInt(10);
        QJsonArray items;
        for (int index = (page - 1) * size; index < qMin(static_cast<int>(matching.size()), page * size); ++index)
            items.append(matching.at(index));
        const int total = static_cast<int>(matching.size());
        const bool rejected = reject;
        QTimer::singleShot(0, reply, [reply, rejected, items, page, total] {
            if (rejected) {
                reply->fail(QStringLiteral("ERR_QUERY"), QStringLiteral("数据库暂不可读"));
                return;
            }
            reply->succeed({{QStringLiteral("items"), items}, {QStringLiteral("page_info"),
                QJsonObject{{QStringLiteral("page"), page}, {QStringLiteral("total"), total}}}});
        });
        return reply;
    }
};
QQuickItem *findVisual(QQuickItem *at, const QString &name) {
    if (at->objectName() == name) return at;
    for (auto *child : at->childItems())
        if (auto *found = findVisual(child, name)) return found;
    return nullptr;
}
}

namespace {
class LibraryThemeState : public QObject {
    Q_OBJECT
    Q_PROPERTY(bool dark MEMBER dark NOTIFY changed)
    Q_PROPERTY(QVariantList battleJobOptions MEMBER jobs CONSTANT)
    Q_PROPERTY(QVariantList categoryOptions MEMBER categories CONSTANT)
public:
    bool dark = false;
    QVariantList jobs, categories;
Q_SIGNALS:
    void changed();
};
} // namespace

namespace {
class ReflectionLibraryTests : public QObject
{
    Q_OBJECT
private:
    static void cancelLibraryFilterAndClearSelection(QQuickWindow *window, QObject *root, const mr::ReflectionLibraryController &library, QQuickItem *search, const QStringList &warnings)
    {
        search->forceActiveFocus();
        QTest::keyClick(window, Qt::Key_N);
        QTest::keyClick(window, Qt::Key_Return);
        QObject *confirmation = root->findChild<QObject *>(QStringLiteral("reflectionFilterConfirmation"));
        QVERIFY(confirmation);
        QTRY_VERIFY(confirmation->property("visible").toBool());
        QCOMPARE(library.selectedCount(), 2);
        QVERIFY(!library.filter().contains(QStringLiteral("text")));
        QTest::keyClick(window, Qt::Key_Escape);
        QTRY_VERIFY(!confirmation->property("visible").toBool());
        QTRY_COMPARE(search->property("text").toString(), QString());
        QCOMPARE(library.selectedCount(), 2);
        auto *clear = findVisual(window->contentItem(), QStringLiteral("clearReflectionSelection"));
        QVERIFY(clear);
        clear->forceActiveFocus();
        QTest::keyClick(window, Qt::Key_Space);
        QTRY_COMPARE(library.selectedCount(), 0);
        QVERIFY2(warnings.isEmpty(), qPrintable(warnings.join(QLatin1Char('\n'))));
    }

private Q_SLOTS:
    void initTestCase() {
        const QString root = QString::fromUtf8(MR_DESKTOP_QML_DIR);
        qmlRegisterSingletonType(QUrl::fromLocalFile(root + QStringLiteral("/Theme.qml")),
                                 "MentorRecorder", 1, 0, "Theme");
        qmlRegisterUncreatableType<mr::RunListModel>("MentorRecorder", 1, 0, "RunListModel", "Provided by controller");
        for (const auto &directory : {QStringLiteral("/components"), QStringLiteral("/pages")})
            for (const auto &file : QDir(root + directory).entryList({QStringLiteral("*.qml")}, QDir::Files)) {
                const QByteArray name = file.chopped(4).toUtf8();
                qmlRegisterType(QUrl::fromLocalFile(root + directory + QLatin1Char('/') + file),
                                "MentorRecorder", 1, 0, name.constData());
            }
    }
    void independentFiltersAndPaging() {
        QueryBackend backend;
        mr::RunListModel history;
        history.setBackend(&backend);
        history.setFilter({{QStringLiteral("text"), QStringLiteral("历史筛选")}});
        QTRY_VERIFY(!history.isLoading());
        mr::ReflectionLibraryController library(&backend);
        QCOMPARE(backend.queries.size(), 1);
        library.setActive(true);
        QTRY_VERIFY(!library.runs()->isLoading());
        QVERIFY(backend.queries.last().value(QStringLiteral("filter")).toObject().value(QStringLiteral("with_reflection")).toBool());
        library.runs()->nextPage();
        QTRY_VERIFY(!library.runs()->isLoading());
        QCOMPARE(library.runs()->page(), 2);
        QCOMPARE(history.page(), 1);
        QCOMPARE(history.runAt(0).value(QStringLiteral("run_id")).toString(), QStringLiteral("page-1"));
        library.setFilter({{QStringLiteral("text"), QStringLiteral("  心得副本  ")},
            {QStringLiteral("include_deleted"), true}, {QStringLiteral("with_reflection"), false}});
        QTRY_VERIFY(!library.runs()->isLoading());
        QCOMPARE(library.runs()->page(), 1);
        const auto filter = backend.queries.last().value(QStringLiteral("filter")).toObject();
        QCOMPARE(filter.value(QStringLiteral("text")).toString(), QStringLiteral("心得副本"));
        QVERIFY(filter.value(QStringLiteral("with_reflection")).toBool());
        QVERIFY(!filter.contains(QStringLiteral("include_deleted")));
        history.reload();
        QTRY_VERIFY(!history.isLoading());
        QCOMPARE(backend.queries.last().value(QStringLiteral("filter")).toObject().value(QStringLiteral("text")).toString(), QStringLiteral("历史筛选"));
    }
    void failureIsNotAnEmptyResultAndRetryWorks() {
        QueryBackend backend;
        backend.reject = true;
        mr::ReflectionLibraryController library(&backend);
        library.setActive(true);
        QTRY_VERIFY(!library.runs()->isLoading());
        QCOMPARE(library.runs()->rowCount(), 0);
        QVERIFY(!library.runs()->loadError().isEmpty());
        backend.reject = false;
        library.reload();
        QTRY_VERIFY(!library.runs()->isLoading());
        QCOMPARE(library.runs()->total(), 31);
        QVERIFY(library.runs()->loadError().isEmpty());
    }
    void stableSelectionAcrossPagesAndFilterConfirmation() {
        QueryBackend backend;
        mr::ReflectionLibraryController library(&backend);
        QSignalSpy confirm(&library, &mr::ReflectionLibraryController::filterChangeNeedsConfirmation);
        library.setActive(true);
        QTRY_VERIFY(!library.runs()->isLoading());
        library.setPageSelected(true);
        QCOMPARE(library.selectedCount(), 1);
        QVERIFY(library.currentPageSelected());
        library.runs()->nextPage();
        QTRY_VERIFY(!library.runs()->isLoading());
        QVERIFY(!library.currentPageSelected());
        library.setPageSelected(true);
        QCOMPARE(library.selectedIds(), QStringList({QStringLiteral("page-1"), QStringLiteral("page-2")}));
        QCOMPARE(library.selectedRuns().at(0).toMap().value(QStringLiteral("reflection")).toMap()
                    .value(QStringLiteral("text")).toString(), QStringLiteral("完整心得"));
        library.runs()->previousPage();
        QTRY_VERIFY(!library.runs()->isLoading());
        QVERIFY(library.currentPageSelected());
        library.setPageSelected(false);
        QCOMPARE(library.selectedIds(), QStringList({QStringLiteral("page-2")}));
        library.setFilter({{QStringLiteral("text"), QStringLiteral("新条件")}});
        QCOMPARE(confirm.count(), 1);
        QCOMPARE(library.selectedCount(), 1);
        QVERIFY(!library.filter().contains(QStringLiteral("text")));
        library.cancelFilterChange();
        library.confirmFilterChange();
        QCOMPARE(library.selectedCount(), 1);
        QVERIFY(!library.filter().contains(QStringLiteral("text")));
        library.setFilter({{QStringLiteral("text"), QStringLiteral("新条件")}});
        library.confirmFilterChange();
        QTRY_VERIFY(!library.runs()->isLoading());
        QCOMPARE(library.selectedCount(), 0);
        QCOMPARE(library.runs()->page(), 1);
        QCOMPARE(library.filter().value(QStringLiteral("text")).toString(), QStringLiteral("新条件"));
    }
    void realLibraryKeyboardSelectionAndFilterCancel_data() {
        QTest::addColumn<QSize>("size");
        QTest::addColumn<bool>("dark");
        QTest::addColumn<QString>("style");
        QTest::newRow("classic-small-light") << QSize(720, 560) << false << QStringLiteral("classic");
        QTest::newRow("classic-large-dark") << QSize(1100, 900) << true << QStringLiteral("classic");
        QTest::newRow("harendotes-small-light") << QSize(720, 560) << false << QStringLiteral("harendotes");
        QTest::newRow("harendotes-large-dark") << QSize(1100, 900) << true << QStringLiteral("harendotes");
    }
    void realLibraryKeyboardSelectionAndFilterCancel() {
        QFETCH(QSize, size);
        QFETCH(bool, dark);
        QFETCH(QString, style);
        QueryBackend backend;
        mr::ReflectionLibraryController library(&backend);
        LibraryThemeState theme;
        theme.dark = dark;
        mr::Formatters formatters;
        mr::JobCatalog jobs;
        QQmlEngine engine;
        QStringList warnings;
        connect(&engine, &QQmlEngine::warnings, this, [&warnings](const QList<QQmlError> &errors) {
            for (const auto &error : errors) warnings.append(error.toString());
        });
        engine.rootContext()->setContextProperty(QStringLiteral("ForceUiStyle"), style);
        engine.rootContext()->setContextProperty(QStringLiteral("App"), &theme);
        engine.rootContext()->setContextProperty(QStringLiteral("Fmt"), &formatters);
        engine.rootContext()->setContextProperty(QStringLiteral("Jobs"), &jobs);
        engine.rootContext()->setContextProperty(QStringLiteral("Reflections"), &library);
        engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    visible: true; width: 720; height: 560
    color: Theme.contentBackground
    ReflectionsPage { objectName: "page"; anchors.fill: parent; anchors.margins: 24 }
})", QUrl());
        std::unique_ptr<QObject> root(component.create());
        QVERIFY2(root, qPrintable(component.errorString()));
        auto *window = qobject_cast<QQuickWindow *>(root.get());
        window->resize(size);
        QVERIFY(QTest::qWaitForWindowExposed(window));
        library.setActive(true);
        QTRY_VERIFY(!library.runs()->isLoading());
        auto *select = findVisual(window->contentItem(), QStringLiteral("selectCurrentReflectionPage"));
        QVERIFY(select);
        auto *toolbar = findVisual(window->contentItem(), QStringLiteral("reflectionToolbar"));
        QVERIFY(toolbar);
        auto *search = findVisual(window->contentItem(), QStringLiteral("reflectionSearchField"));
        QVERIFY(search);
        QCOMPARE(select->parentItem(), toolbar);
        QCOMPARE(select->property("text").toString(), QString());
        QVERIFY(!findVisual(window->contentItem(), QStringLiteral("reflectionSelectionHeader")));
        QVERIFY(!findVisual(window->contentItem(), QStringLiteral("reflectionSelectionHint")));
        QTRY_VERIFY(qAbs(select->mapToScene(QPointF(0, select->height() / 2)).y()
                          - search->mapToScene(QPointF(0, search->height() / 2)).y()) < 1);
        QCOMPARE(select->x(), qreal(0));
        QVERIFY(select->x() + select->width() <= search->x());
        // 宽窗口所有操作同排，窄窗口完整控件换行且不越过工具栏边界。
        for (auto *control : toolbar->childItems()) {
            QVERIFY(control->x() >= 0);
            QVERIFY(control->x() + control->width() <= toolbar->width() + 1);
            if (size.width() >= 1100)
                QCOMPARE(control->y(), qreal(0));
        }
        QObject *page = root->findChild<QObject *>(QStringLiteral("page"));
        QSignalSpy detail(page, SIGNAL(openRunRequested(QVariant)));
        QVERIFY(detail.isValid());
        select->forceActiveFocus();
        QTest::keyClick(window, Qt::Key_Tab);
        QTRY_VERIFY(search->hasActiveFocus());
        QTest::keyClick(window, Qt::Key_Backtab);
        QTRY_VERIFY(select->hasActiveFocus());
        QTest::mouseClick(window, Qt::LeftButton, Qt::NoModifier,
                          select->mapToScene(QPointF(select->width() / 2, select->height() / 2)).toPoint());
        QTRY_COMPARE(library.selectedCount(), 1);
        QVERIFY(select->property("checked").toBool());
        QCOMPARE(detail.count(), 0);
        select->forceActiveFocus();
        QTest::keyClick(window, Qt::Key_Space);
        QTRY_COMPARE(library.selectedCount(), 0);
        QTest::keyClick(window, Qt::Key_Space);
        QTRY_COMPARE(library.selectedCount(), 1);
        auto *next = findVisual(window->contentItem(), QStringLiteral("reflectionNextPage"));
        QVERIFY(next);
        next->forceActiveFocus();
        QTest::keyClick(window, Qt::Key_Space);
        QTRY_COMPARE(library.runs()->page(), 2);
        QTRY_VERIFY(!library.runs()->isLoading());
        QTRY_VERIFY(!select->property("checked").toBool());
        select->forceActiveFocus();
        QTest::keyClick(window, Qt::Key_Space);
        QTRY_COMPARE(library.selectedCount(), 2);
        auto *jump = findVisual(window->contentItem(), QStringLiteral("reflectionPageJump"));
        QVERIFY(jump);
        auto *number = findVisual(jump, QStringLiteral("pageNumberField"));
        auto *jumpButton = findVisual(jump, QStringLiteral("pageNumberJumpButton"));
        QVERIFY(number && jumpButton);
        const auto filterBeforeJump = library.filter();
        number->forceActiveFocus();
        QTest::keyClick(window, Qt::Key_A, Qt::ControlModifier);
        QTest::keyClick(window, Qt::Key_1);
        QTest::keyClick(window, Qt::Key_Return);
        QTRY_COMPARE(library.runs()->page(), 1);
        QTRY_VERIFY(!library.runs()->isLoading());
        QCOMPARE(library.selectedCount(), 2);
        QCOMPARE(library.filter(), filterBeforeJump);
        number->forceActiveFocus();
        QTest::keyClick(window, Qt::Key_A, Qt::ControlModifier);
        QTest::keyClick(window, Qt::Key_2);
        QTest::mouseClick(window, Qt::LeftButton, Qt::NoModifier,
                          jumpButton->mapToScene(QPointF(jumpButton->width() / 2, jumpButton->height() / 2)).toPoint());
        QTRY_COMPARE(library.runs()->page(), 2);
        QTRY_VERIFY(!library.runs()->isLoading());
        QCOMPARE(library.selectedCount(), 2);
        QSignalSpy share(page, SIGNAL(shareBatchRequested(QVariant)));
        QVERIFY(share.isValid());
        auto *generate = findVisual(window->contentItem(), QStringLiteral("shareSelectedReflections"));
        QVERIFY(generate && generate->isEnabled());
        generate->forceActiveFocus();
        QTest::keyClick(window, Qt::Key_Space);
        QTRY_COMPARE(share.count(), 1);
        const QString evidence = qEnvironmentVariable("MR_REFLECTION_SHARE_SCREENSHOT_DIR");
        if (!evidence.isEmpty()) {
            QVERIFY(QDir().mkpath(evidence));
            QTest::qWait(80);
            QVERIFY(window->grabWindow().save(QDir(evidence).filePath(QStringLiteral("library-%1-%2-%3.png")
                .arg(style, dark ? QStringLiteral("dark") : QStringLiteral("light"))
                .arg(size.width()))));
        }
        cancelLibraryFilterAndClearSelection(window, root.get(), library, search, warnings);
        QVERIFY(!QTest::currentTestFailed());
    }
    // 审查 I4-1：勾选保存的是记录的副本。心得被清空、记录被删除，或在别的页上改过
    // 心得之后，勾选必须跟着记录走：删掉的不再交给「生成分享图片」，改过的用新文字；
    // 读取失败时什么也不动。
    void selectionFollowsTheRecordsItWasTakenFrom() {
        StoreBackend backend;
        backend.add(QStringLiteral("a"), QStringLiteral("甲的心得"));
        backend.add(QStringLiteral("b"), QStringLiteral("乙的心得"));
        backend.add(QStringLiteral("c"), QStringLiteral("丙的心得"));
        backend.add(QStringLiteral("d"), QStringLiteral("丁的心得"));
        mr::ReflectionLibraryController library(&backend);
        library.runs()->setPageSize(1);
        library.setActive(true);
        QTRY_VERIFY(!library.runs()->isLoading());
        for (int page = 1; page <= 3; ++page) {
            QTRY_COMPARE(library.runs()->page(), page);
            QTRY_VERIFY(!library.runs()->isLoading());
            library.setPageSelected(true);
            if (page < 3)
                library.runs()->nextPage();
        }
        QCOMPARE(library.selectedIds(), QStringList({QStringLiteral("a"), QStringLiteral("b"), QStringLiteral("c")}));

        // 甲's reflection cleared, 乙's edited while it sits on another page, 丙's run
        // deleted in a batch - which only says "re-query".
        backend.update(QStringLiteral("a"), QStringLiteral("reflection"), QJsonObject{{QStringLiteral("text"), QString()}});
        backend.update(QStringLiteral("b"), QStringLiteral("reflection"),
                       QJsonObject{{QStringLiteral("text"), QStringLiteral("乙改过的心得")}});
        backend.update(QStringLiteral("c"), QStringLiteral("soft_deleted"), true);
        Q_EMIT backend.liveEvent({{QStringLiteral("kind"), QStringLiteral("stats_invalidated")}});
        QTRY_COMPARE(library.selectedIds(), QStringList({QStringLiteral("b")}));
        QTRY_VERIFY(!library.runs()->isLoading());
        QVERIFY(library.runs()->runAt(0).value(QStringLiteral("run_id")).toString() != QStringLiteral("b"));
        QCOMPARE(library.selectedCount(), 1);
        QTRY_COMPARE(library.selectedRuns().at(0).toMap().value(QStringLiteral("reflection")).toMap()
                         .value(QStringLiteral("text")).toString(), QStringLiteral("乙改过的心得"));

        // A failed read is not a deletion.
        backend.reject = true;
        backend.update(QStringLiteral("b"), QStringLiteral("soft_deleted"), true);
        Q_EMIT backend.liveEvent({{QStringLiteral("kind"), QStringLiteral("run_updated")}});
        QTRY_VERIFY(!library.runs()->loadError().isEmpty());
        QTest::qWait(50);
        QCOMPARE(library.selectedIds(), QStringList({QStringLiteral("b")}));
    }
    void inactiveRefreshAndBackendLifetime() {
        auto *backend = new QueryBackend;
        mr::ReflectionLibraryController library(backend);
        library.setActive(true);
        QTRY_VERIFY(!library.runs()->isLoading());
        library.runs()->nextPage();
        QTRY_VERIFY(!library.runs()->isLoading());
        library.setActive(false);
        const qsizetype before = backend->queries.size();
        Q_EMIT backend->liveEvent({{QStringLiteral("kind"), QStringLiteral("run_updated")}});
        QCOMPARE(backend->queries.size(), before);
        library.setActive(true);
        QTRY_VERIFY(!library.runs()->isLoading());
        QCOMPARE(backend->queries.size(), before + 1);
        QCOMPARE(library.runs()->page(), 2);
        delete backend;
        library.reload();
        library.runs()->reload();
        QVERIFY(!library.runs()->isLoading());
    }
};
} // namespace

MR_DESKTOP_TEST_SUITE(ReflectionLibrary) {
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication app(argc, argv);
#ifdef Q_OS_WIN
    // offscreen 平台不枚举系统字体；测试截图显式注册中文字体，生产字体配置保持不变。
    const QDir windowsDir(qEnvironmentVariable("WINDIR", QStringLiteral("C:/Windows")));
    QStringList cjkFamilies;
    for (const char *file : {"Fonts/msyh.ttc", "Fonts/simhei.ttf", "Fonts/simsun.ttc"}) {
        const int id = QFontDatabase::addApplicationFont(windowsDir.filePath(QString::fromLatin1(file)));
        if (id >= 0)
            cjkFamilies.append(QFontDatabase::applicationFontFamilies(id));
    }
    if (cjkFamilies.isEmpty()) {
        qCritical("Cannot load a system CJK font for UI screenshot verification.");
        return 6;
    }
    QFont font = QGuiApplication::font();
    font.setFamilies(cjkFamilies);
    QGuiApplication::setFont(font);
    QFont::insertSubstitutions(QStringLiteral("Noto Serif SC"), {QStringLiteral("SimSun")});
#endif
    ReflectionLibraryTests tests;
    return QTest::qExec(&tests, argc, argv);
}
#include "ReflectionLibraryTests.moc"
