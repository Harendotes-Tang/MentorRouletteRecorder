// Button icons (components/Lucide.js, docs/third-party-licenses.md section 3.4):
// the JavaScript library turns a bundled Lucide icon and a colour into a data:
// URL, and AppButton draws that beside its label in the label's own colour.
//
// Real QML from src/Desktop/qml, loaded from source the way MotionTests does.
#include <QDir>
#include <QGuiApplication>
#include <QPointer>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickStyle>
#include <QSignalSpy>
#include <QTest>
#include <QUrl>
#include <memory>

namespace {

QString qmlRoot()
{
    return QString::fromUtf8(MR_DESKTOP_QML_DIR);
}

/// One engine, one root object created from inline QML placed inside the
/// components directory so `import "Lucide.js"` resolves like AppButton's does.
struct Scene
{
    QQmlEngine engine;
    std::unique_ptr<QObject> root;
    QString errors;

    bool create(const QByteArray &qml)
    {
        QQmlComponent component(&engine);
        component.setData(qml, QUrl::fromLocalFile(qmlRoot() + QStringLiteral("/components/IconProbe.qml")));
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
};

QQuickItem *childNamed(QObject *root, const QString &name)
{
    return root->findChild<QQuickItem *>(name);
}

} // namespace

class IconTests final : public QObject
{
    Q_OBJECT

private slots:
    void initTestCase();

    void library_buildsADataUrlOnlyForBundledIcons();
    void library_writesTranslucentColoursAsStrokeOpacity();
    void appButton_drawsTheIconInTheLabelColour();
    void appButton_withoutAnIconIsTheOldTextButton();
    void appButton_ignoresAnUnknownIconName();
    void navItem_showsThePageIconInsteadOfTheNumber();
};

void IconTests::initTestCase()
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

void IconTests::library_buildsADataUrlOnlyForBundledIcons()
{
    Scene scene;
    QVERIFY2(scene.create(R"(import QtQuick
import "Lucide.js" as Lucide
QtObject {
    readonly property string plus: Lucide.source("plus", "#ffffff")
    readonly property string unknown: Lucide.source("no-such-icon", "#ffffff")
    readonly property string traversal: Lucide.source("../fonts/OFL", "#ffffff")
    readonly property bool hasPlus: Lucide.has("plus")
    readonly property bool hasProto: Lucide.has("constructor")
    readonly property int count: Object.keys(Lucide.icons).length
    readonly property string version: Lucide.version
})"), qPrintable(scene.errors));

    const QString plus = scene.root->property("plus").toString();
    QVERIFY2(plus.startsWith(QStringLiteral("data:image/svg+xml;utf8,")), qPrintable(plus));
    const QString svg = QUrl::fromPercentEncoding(plus.mid(plus.indexOf(u',') + 1).toUtf8());
    QVERIFY2(svg.contains(QStringLiteral("stroke=\"#ffffff\"")), qPrintable(svg));
    QVERIFY2(svg.contains(QStringLiteral("<path d=\"M5 12h14\"/>")), qPrintable(svg));
    QVERIFY2(!svg.contains(QStringLiteral("currentColor")), qPrintable(svg));
    QVERIFY2(!svg.contains(QStringLiteral("stroke-opacity")), qPrintable(svg));

    QCOMPARE(scene.root->property("unknown").toString(), QString());
    QCOMPARE(scene.root->property("traversal").toString(), QString());
    QVERIFY(scene.root->property("hasPlus").toBool());
    // Object.prototype members are not icons.
    QVERIFY(!scene.root->property("hasProto").toBool());
    QVERIFY(scene.root->property("count").toInt() >= 9);
    QVERIFY(!scene.root->property("version").toString().isEmpty());
}

void IconTests::library_writesTranslucentColoursAsStrokeOpacity()
{
    Scene scene;
    QVERIFY2(scene.create(R"(import QtQuick
import "Lucide.js" as Lucide
QtObject {
    // A QML color, not a string: the way AppButton hands its foreground over.
    readonly property color translucent: Qt.rgba(1, 0, 0, 0.5)
    readonly property string source: Lucide.source("x", translucent)
})"), qPrintable(scene.errors));
    const QString source = scene.root->property("source").toString();
    const QString svg = QUrl::fromPercentEncoding(source.mid(source.indexOf(u',') + 1).toUtf8());
    QVERIFY2(svg.contains(QStringLiteral("stroke=\"#ff0000\"")), qPrintable(svg));
    QVERIFY2(svg.contains(QStringLiteral("stroke-opacity=\"0.502\"")), qPrintable(svg));
    // Never a nine-digit colour the SVG renderer would not understand.
    QVERIFY2(!svg.contains(QStringLiteral("#80ff0000")), qPrintable(svg));
}

void IconTests::appButton_drawsTheIconInTheLabelColour()
{
    Scene scene;
    QVERIFY2(scene.create(R"(import QtQuick
import MentorRecorder
Item {
    width: 400; height: 200
    AppButton { id: primary; objectName: "primary"; text: "新增记录"; iconName: "plus"; variant: "primary" }
    AppButton { id: plain; objectName: "plain"; text: "下一页"; iconName: "chevron-right"; iconAfterText: true; y: 60 }
})"), qPrintable(scene.errors));

    for (const char *name : {"primary", "plain"}) {
        QQuickItem *button = childNamed(scene.root.get(), QString::fromLatin1(name));
        QVERIFY2(button, name);
        QVERIFY(button->property("hasIcon").toBool());
        QQuickItem *icon = button->findChild<QQuickItem *>(QStringLiteral("buttonIcon"));
        QQuickItem *label = button->findChild<QQuickItem *>(QStringLiteral("buttonLabel"));
        QVERIFY2(icon && label, name);
        QVERIFY(icon->isVisible());
        QCOMPARE(icon->width(), 14.0);

        // The icon really loads (data: URL through the image pipeline)...
        QTRY_COMPARE_WITH_TIMEOUT(icon->property("status").toInt(), 1 /* Image.Ready */, 5000);
        QVERIFY(icon->property("sourceSize").toSize().width() >= 14);

        // ...and its stroke is the label's colour.
        const QColor foreground = label->property("color").value<QColor>();
        const QString source = icon->property("source").toUrl().toString();
        const QString svg = QUrl::fromPercentEncoding(source.mid(source.indexOf(u',') + 1).toUtf8());
        QVERIFY2(svg.contains(QStringLiteral("stroke=\"") + foreground.name(QColor::HexRgb) + u'"'),
                 qPrintable(QString::fromLatin1(name) + u' ' + svg));

        // The button is wide enough for both, with the gap between them.
        QVERIFY(button->implicitWidth() >= icon->width() + 6 + label->implicitWidth() + 28);
    }

    // The primary button is white-on-accent in every style; the icon follows.
    QQuickItem *primary = childNamed(scene.root.get(), QStringLiteral("primary"));
    QQuickItem *primaryIcon = primary->findChild<QQuickItem *>(QStringLiteral("buttonIcon"));
    QVERIFY(primaryIcon->property("source").toUrl().toString().contains(QStringLiteral("%23ffffff")));

    // iconAfterText: the icon's x is to the right of the label's.
    QQuickItem *plain = childNamed(scene.root.get(), QStringLiteral("plain"));
    QQuickItem *plainIcon = plain->findChild<QQuickItem *>(QStringLiteral("buttonIcon"));
    QQuickItem *plainLabel = plain->findChild<QQuickItem *>(QStringLiteral("buttonLabel"));
    QTRY_VERIFY(plainIcon->x() > plainLabel->x());
    QQuickItem *primaryLabel = primary->findChild<QQuickItem *>(QStringLiteral("buttonLabel"));
    QTRY_VERIFY(primaryIcon->x() < primaryLabel->x());
}

void IconTests::appButton_withoutAnIconIsTheOldTextButton()
{
    Scene scene;
    QVERIFY2(scene.create(R"(import QtQuick
import MentorRecorder
AppButton { text: "导出 CSV" })"), qPrintable(scene.errors));
    QQuickItem *button = qobject_cast<QQuickItem *>(scene.root.get());
    QVERIFY(button);
    QVERIFY(!button->property("hasIcon").toBool());
    QQuickItem *icon = button->findChild<QQuickItem *>(QStringLiteral("buttonIcon"));
    QQuickItem *label = button->findChild<QQuickItem *>(QStringLiteral("buttonLabel"));
    QVERIFY(icon && label);
    QVERIFY(!icon->isVisible());
    QCOMPARE(icon->property("source").toUrl(), QUrl());
    QVERIFY(label->isVisible());
    // Same width rule as before: text plus 28 px, at least 80.
    QCOMPARE(button->implicitWidth(), std::max(80.0, label->implicitWidth() + 28));
}

void IconTests::appButton_ignoresAnUnknownIconName()
{
    Scene scene;
    QVERIFY2(scene.create(R"(import QtQuick
import MentorRecorder
AppButton { text: "清除"; iconName: "no-such-icon" })"), qPrintable(scene.errors));
    QQuickItem *button = qobject_cast<QQuickItem *>(scene.root.get());
    QVERIFY(button);
    QVERIFY(!button->property("hasIcon").toBool());
    QQuickItem *icon = button->findChild<QQuickItem *>(QStringLiteral("buttonIcon"));
    QVERIFY(icon);
    QVERIFY(!icon->isVisible());
    QCOMPARE(icon->property("source").toUrl(), QUrl());
}

void IconTests::navItem_showsThePageIconInsteadOfTheNumber()
{
    Scene scene;
    QVERIFY2(scene.create(R"(import QtQuick
import MentorRecorder
Item {
    width: 200; height: 120
    NavItem { objectName: "withIcon"; width: 180; iconName: "history"; number: "02"; label: "历史记录"; current: true }
    NavItem { objectName: "numbered"; width: 180; y: 50; number: "07"; label: "对照核对" }
})"), qPrintable(scene.errors));

    QQuickItem *withIcon = childNamed(scene.root.get(), QStringLiteral("withIcon"));
    QVERIFY(withIcon);
    QVERIFY(withIcon->property("hasIcon").toBool());
    QQuickItem *icon = withIcon->findChild<QQuickItem *>(QStringLiteral("navIcon"));
    QQuickItem *number = withIcon->findChild<QQuickItem *>(QStringLiteral("navNumber"));
    QVERIFY(icon);
    QVERIFY(number);
    QVERIFY(icon->isVisible());
    QVERIFY(!number->isVisible());
    QTRY_COMPARE_WITH_TIMEOUT(icon->property("status").toInt(), 1 /* Image.Ready */, 5000);
    // The current page's icon is drawn in the label's colour.
    QQuickItem *label = nullptr;
    for (QQuickItem *child : withIcon->findChildren<QQuickItem *>()) {
        if (child->property("text").toString() == QStringLiteral("历史记录")) {
            label = child;
            break;
        }
    }
    if (!label)
        QFAIL("no navigation label with the text 历史记录 under withIcon");
    const QColor labelColor = label->property("color").value<QColor>();
    QVERIFY(icon->property("source").toUrl().toString().contains(
        QStringLiteral("stroke%3D%22") + QUrl::toPercentEncoding(labelColor.name(QColor::HexRgb))));

    // A caller that only has a number (the settings sub-navigation) still gets it.
    QQuickItem *numbered = childNamed(scene.root.get(), QStringLiteral("numbered"));
    QVERIFY(numbered);
    QVERIFY(!numbered->property("hasIcon").toBool());
    QVERIFY(!numbered->findChild<QQuickItem *>(QStringLiteral("navIcon"))->isVisible());
    QVERIFY(numbered->findChild<QQuickItem *>(QStringLiteral("navNumber"))->isVisible());
}

int main(int argc, char *argv[])
{
    qputenv("QT_QPA_PLATFORM", "offscreen");
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication app(argc, argv);
    IconTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "IconTests.moc"
