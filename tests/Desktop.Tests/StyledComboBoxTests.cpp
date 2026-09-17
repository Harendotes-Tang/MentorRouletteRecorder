// components/StyledComboBox.qml: the popup lists each option once (a box
// without a sectionRole gets no section headers) and is wide enough for the
// longest option, however narrow the closed field is - an 86 px filter box must
// neither truncate an option to 讨伐歼… nor give every row a heading of its own.
#include <QDir>
#include <QGuiApplication>
#include <QQmlComponent>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QTest>
#include <QUrl>
#include <memory>

namespace {

QString qmlRoot()
{
    return QString::fromUtf8(MR_DESKTOP_QML_DIR);
}

struct Scene
{
    QQmlEngine engine;
    std::unique_ptr<QObject> root;
    QString errors;

    bool create(const QByteArray &qml)
    {
        QQmlComponent component(&engine);
        component.setData(qml, QUrl::fromLocalFile(qmlRoot() + QStringLiteral("/components/ComboProbe.qml")));
        if (component.isError()) {
            errors = component.errorString();
            return false;
        }
        root.reset(component.create());
        if (!root) {
            errors = component.errorString();
            return false;
        }
        return true;
    }

    QQuickWindow *window() const { return qobject_cast<QQuickWindow *>(root.get()); }
};

int countItems(QQuickItem *root, const QString &name)
{
    int count = root && root->objectName() == name ? 1 : 0;
    if (root) {
        for (QQuickItem *child : root->childItems())
            count += countItems(child, name);
    }
    return count;
}

} // namespace

class StyledComboBoxTests final : public QObject
{
    Q_OBJECT

private slots:
    void initTestCase();

    void aBoxWithoutSectionsListsEachOptionOnce();
    void thePopupGrowsToTheLongestOption();
    void aBoxWithASectionRoleStillGetsItsHeaders();
};

void StyledComboBoxTests::initTestCase()
{
    const QString root = qmlRoot();
    QVERIFY(QDir(root).exists());
    qmlRegisterSingletonType(QUrl::fromLocalFile(root + QStringLiteral("/Theme.qml")),
                             "MentorRecorder", 1, 0, "Theme");
    const auto files = QDir(root + QStringLiteral("/components")).entryList({QStringLiteral("*.qml")}, QDir::Files);
    for (const auto &file : files) {
        const QByteArray name = file.chopped(4).toUtf8();
        qmlRegisterType(QUrl::fromLocalFile(root + QStringLiteral("/components/") + file),
                        "MentorRecorder", 1, 0, name.constData());
    }
}

void StyledComboBoxTests::aBoxWithoutSectionsListsEachOptionOnce()
{
    Scene scene;
    QVERIFY2(scene.create(R"(import QtQuick
import QtQuick.Window
import MentorRecorder
Window {
    width: 480; height: 400; visible: true
    StyledComboBox { objectName: "box"; x: 20; y: 20; width: 86
                     model: ["类型", "四人迷宫", "讨伐歼灭战", "大型任务", "行会令"] }
})"), qPrintable(scene.errors));
    QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
    QQuickItem *box = scene.root->findChild<QQuickItem *>(QStringLiteral("box"));
    QVERIFY(box);
    QCOMPARE(box->property("sectionRole").toString(), QString());

    QVERIFY(QMetaObject::invokeMethod(box->property("popup").value<QObject *>(), "open"));
    QTRY_VERIFY(box->property("popup").value<QObject *>()->property("visible").toBool());
    QObject *popup = box->property("popup").value<QObject *>();
    QQuickItem *list = popup->property("contentItem").value<QQuickItem *>();
    QVERIFY(list);
    // Five rows, and no section header anywhere in the list.
    QTRY_COMPARE(list->property("count").toInt(), 5);
    QCOMPARE(countItems(list, QStringLiteral("comboSectionHeader")), 0);
    QVERIFY(list->property("section").value<QObject *>()->property("delegate").isNull()
            || !list->property("section").value<QObject *>()->property("delegate").value<QObject *>());
}

void StyledComboBoxTests::thePopupGrowsToTheLongestOption()
{
    Scene scene;
    QVERIFY2(scene.create(R"(import QtQuick
import QtQuick.Window
import MentorRecorder
Window {
    width: 480; height: 400; visible: true
    StyledComboBox { objectName: "box"; x: 20; y: 20; width: 86
                     model: ["类型", "一个特别特别长的副本种类名称"] }
})"), qPrintable(scene.errors));
    QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
    QQuickItem *box = scene.root->findChild<QQuickItem *>(QStringLiteral("box"));
    QVERIFY(box);
    QVERIFY(QMetaObject::invokeMethod(box->property("popup").value<QObject *>(), "open"));
    QObject *popup = box->property("popup").value<QObject *>();
    QTRY_VERIFY(popup->property("visible").toBool());
    // Wider than the 86 px field, and at least as wide as the text needs.
    QVERIFY(popup->property("width").toReal() > 86);
    QVERIFY(popup->property("widestOption").toReal() > 86);
    QCOMPARE(popup->property("width").toReal(), popup->property("widestOption").toReal());
}

void StyledComboBoxTests::aBoxWithASectionRoleStillGetsItsHeaders()
{
    Scene scene;
    QVERIFY2(scene.create(R"(import QtQuick
import QtQuick.Window
import MentorRecorder
Window {
    width: 480; height: 400; visible: true
    StyledComboBox { objectName: "box"; x: 20; y: 20; width: 160
                     textRole: "name"; sectionRole: "group"
                     model: [{ name: "骑士", group: "坦克" }, { name: "战士", group: "坦克" },
                             { name: "白魔法师", group: "治疗" }] }
})"), qPrintable(scene.errors));
    QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
    QQuickItem *box = scene.root->findChild<QQuickItem *>(QStringLiteral("box"));
    QVERIFY(box);
    QVERIFY(QMetaObject::invokeMethod(box->property("popup").value<QObject *>(), "open"));
    QObject *popup = box->property("popup").value<QObject *>();
    QTRY_VERIFY(popup->property("visible").toBool());
    QQuickItem *list = popup->property("contentItem").value<QQuickItem *>();
    QVERIFY(list);
    QTRY_COMPARE(list->property("count").toInt(), 3);
    // Two groups, two headers.
    QTRY_COMPARE(countItems(list, QStringLiteral("comboSectionHeader")), 2);
}

int main(int argc, char *argv[])
{
    qputenv("QT_QPA_PLATFORM", "offscreen");
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication app(argc, argv);
    StyledComboBoxTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "StyledComboBoxTests.moc"
