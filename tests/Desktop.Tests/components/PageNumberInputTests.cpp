#include "DesktopTestRunner.h"
#include "QmlComponentTestSupport.h"
#include "IBackend.h"
#include "RunListModel.h"

#include <QGuiApplication>
#include <QJsonArray>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QTest>
#include <QTimer>
#include <memory>

namespace {
class QueryBackend final : public mr::IBackend
{
public:
    int total = 240;
    bool hold = false;
    QList<QJsonObject> queries;
    QString backendName() const override { return QStringLiteral("page-probe"); }
    bool isConnected() const override { return true; }
    mr::BackendReply *request(const QString &type, const QJsonObject &payload) override
    {
        queries.append(payload);
        auto *reply = new mr::BackendReply(QStringLiteral("page-query"), type, this);
        if (hold) return reply;
        const int count = total;
        QTimer::singleShot(0, reply, [reply, count] {
            reply->succeed({{QStringLiteral("items"), QJsonArray{}},
                            {QStringLiteral("page_info"), QJsonObject{{QStringLiteral("total"), count}}}});
        });
        return reply;
    }
};

struct Scene
{
    QueryBackend backend;
    mr::RunListModel model;
    QQmlEngine engine;
    std::unique_ptr<QObject> root;
    QString errors;

    bool create()
    {
        model.setBackend(&backend);
        model.setPageSize(20);
        model.setFilter({{QStringLiteral("text"), QStringLiteral("keep-filter")}});
        engine.rootContext()->setContextProperty(QStringLiteral("PagedModel"), &model);
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import MentorRecorder
Window {
    width: 500; height: 180; visible: true
    PageNumberInput { objectName: "pager"; x: 20; y: 20; paginationModel: PagedModel }
})", QUrl::fromLocalFile(QString::fromUtf8(MR_DESKTOP_QML_DIR) + QStringLiteral("/components/PageProbe.qml")));
        root.reset(component.create());
        errors = component.errorString();
        return root != nullptr;
    }
    QQuickWindow *window() const { return qobject_cast<QQuickWindow *>(root.get()); }
    QQuickItem *pager() const { return root->findChild<QQuickItem *>(QStringLiteral("pager")); }
    QQuickItem *field() const { return root->findChild<QQuickItem *>(QStringLiteral("pageNumberField")); }
    QQuickItem *button() const { return root->findChild<QQuickItem *>(QStringLiteral("pageNumberJumpButton")); }
    void type(const QString &text)
    {
        field()->forceActiveFocus();
        QTest::keyClick(window(), Qt::Key_A, Qt::ControlModifier);
        for (const auto ch : text) QTest::keyClick(window(), ch.toLatin1());
    }
};

class PageNumberInputTests final : public QObject
{
    Q_OBJECT
private slots:
    void initTestCase()
    {
        mrtest::registerSourceQmlComponents();
    }
    void enterAndClickKeepTheFilter()
    {
        Scene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
        QTRY_VERIFY(!scene.model.isLoading());
        QCOMPARE(scene.model.pageCount(), 12);
        scene.type(QStringLiteral("9"));
        QTest::keyClick(scene.window(), Qt::Key_Return);
        QTRY_COMPARE(scene.model.page(), 9);
        QTRY_VERIFY(!scene.model.isLoading());
        QCOMPARE(scene.backend.queries.last().value(QStringLiteral("filter")).toObject().value(QStringLiteral("text")).toString(), QStringLiteral("keep-filter"));
        scene.type(QStringLiteral("3"));
        const auto pos = scene.button()->mapToScene(QPointF(scene.button()->width() / 2, scene.button()->height() / 2)).toPoint();
        QTest::mouseClick(scene.window(), Qt::LeftButton, Qt::NoModifier, pos);
        QTRY_COMPARE(scene.model.page(), 3);
        QTRY_VERIFY(!scene.model.isLoading());
        QCOMPARE(scene.field()->property("text").toString(), QStringLiteral("3"));
    }
    void invalidDraftDoesNotQuery_data()
    {
        QTest::addColumn<QString>("draft");
        for (const auto *value : {"", "0", "13", "-1", "1.5", "x", "999999999999999999999"})
            QTest::newRow(value[0] ? value : "empty") << QString::fromLatin1(value);
    }
    void invalidDraftDoesNotQuery()
    {
        QFETCH(QString, draft);
        Scene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QTRY_VERIFY(!scene.model.isLoading());
        const auto count = scene.backend.queries.size();
        // Bypass the input validator to exercise the final submit guard too.
        scene.field()->setProperty("text", draft);
        QVERIFY(!scene.button()->isEnabled());
        QVERIFY(QMetaObject::invokeMethod(scene.pager(), "jump"));
        QCOMPARE(scene.backend.queries.size(), count);
        QCOMPARE(scene.model.page(), 1);
    }
    void loadingAndAbsentModelDisableNavigation()
    {
        Scene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QTRY_VERIFY(!scene.model.isLoading());
        scene.backend.hold = true;
        scene.model.goToPage(2);
        QVERIFY(scene.model.isLoading());
        QVERIFY(!scene.field()->isEnabled());
        QVERIFY(!scene.button()->isEnabled());
        scene.field()->setProperty("text", "5");
        const auto count = scene.backend.queries.size();
        QVERIFY(QMetaObject::invokeMethod(scene.pager(), "jump"));
        QCOMPARE(scene.backend.queries.size(), count);
        scene.pager()->setProperty("paginationModel", QVariant::fromValue(static_cast<QObject *>(nullptr)));
        QVERIFY(!scene.pager()->isEnabled());
        QCOMPARE(scene.field()->property("text").toString(), QStringLiteral("1"));
    }
    void filterResetAndLastPageReductionFollowTheModel()
    {
        Scene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QTRY_VERIFY(!scene.model.isLoading());
        scene.model.goToPage(10);
        QTRY_VERIFY(!scene.model.isLoading());
        QCOMPARE(scene.field()->property("text").toString(), QStringLiteral("10"));
        scene.backend.total = 3;
        scene.model.reload();
        QTRY_VERIFY(!scene.model.isLoading());
        QCOMPARE(scene.model.page(), 1);
        QCOMPARE(scene.pager()->property("maximumPage").toInt(), 1);
        QCOMPARE(scene.field()->property("text").toString(), QStringLiteral("1"));
        scene.backend.total = 240;
        scene.model.setFilter({});
        QTRY_VERIFY(!scene.model.isLoading());
        QCOMPARE(scene.pager()->property("maximumPage").toInt(), 12);
        QCOMPARE(scene.field()->property("text").toString(), QStringLiteral("1"));
        scene.field()->setProperty("text", "8");
        scene.model.setFilter({{QStringLiteral("text"), QStringLiteral("another-filter")}});
        QTRY_VERIFY(!scene.model.isLoading());
        QCOMPARE(scene.field()->property("text").toString(), QStringLiteral("1"));
    }
    void emptyResultKeepsOnePageWithoutAnotherQuery()
    {
        Scene scene;
        scene.backend.total = 0;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QTRY_VERIFY(!scene.model.isLoading());
        QCOMPARE(scene.pager()->property("maximumPage").toInt(), 1);
        const auto count = scene.backend.queries.size();
        QVERIFY(QMetaObject::invokeMethod(scene.pager(), "jump"));
        QCOMPARE(scene.backend.queries.size(), count);
    }
};
} // namespace

MR_DESKTOP_TEST_SUITE(PageNumberInput)
{
    qputenv("QT_QPA_PLATFORM", "offscreen");
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication app(argc, argv);
    PageNumberInputTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "PageNumberInputTests.moc"
