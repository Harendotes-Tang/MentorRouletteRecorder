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
QQuickItem *findVisual(QQuickItem *at, const QString &name) {
    if (at->objectName() == name) return at;
    for (auto *child : at->childItems())
        if (auto *found = findVisual(child, name)) return found;
    return nullptr;
}
}

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

class ReflectionLibraryTests : public QObject
{
    Q_OBJECT
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
        QTest::newRow("small-light") << QSize(720, 560) << false;
        QTest::newRow("large-dark") << QSize(1100, 900) << true;
    }
    void realLibraryKeyboardSelectionAndFilterCancel() {
        QFETCH(QSize, size);
        QFETCH(bool, dark);
        QueryBackend backend;
        mr::ReflectionLibraryController library(&backend);
        LibraryThemeState theme;
        theme.dark = dark;
        mr::Formatters formatters;
        mr::JobCatalog jobs;
        QQmlEngine engine;
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
        // 宽窗口所有操作同排，窄窗口完整控件换行且不越过工具栏边界。
        for (auto *control : toolbar->childItems()) {
            QVERIFY(control->x() >= 0);
            QVERIFY(control->x() + control->width() <= toolbar->width() + 1);
            if (size.width() >= 1100)
                QCOMPARE(control->y(), qreal(0));
        }
        select->forceActiveFocus();
        QTest::keyClick(window, Qt::Key_Space);
        QTRY_COMPARE(library.selectedCount(), 1);
        QVERIFY(select->property("checked").toBool());
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
        QObject *page = root->findChild<QObject *>(QStringLiteral("page"));
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
            QVERIFY(window->grabWindow().save(QDir(evidence).filePath(dark
                ? QStringLiteral("library-dark-large.png") : QStringLiteral("library-light-small.png"))));
        }
        auto *search = findVisual(window->contentItem(), QStringLiteral("reflectionSearchField"));
        QVERIFY(search);
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
    }
    void inactiveRefreshAndBackendLifetime() {
        auto *backend = new QueryBackend;
        mr::ReflectionLibraryController library(backend);
        library.setActive(true);
        QTRY_VERIFY(!library.runs()->isLoading());
        library.runs()->nextPage();
        QTRY_VERIFY(!library.runs()->isLoading());
        library.setActive(false);
        const int before = backend->queries.size();
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

int main(int argc, char **argv) {
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
