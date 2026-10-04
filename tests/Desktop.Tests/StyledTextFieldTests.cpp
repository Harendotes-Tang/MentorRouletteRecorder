// components/StyledTextField.qml: a reason or note field whose placeholder is an
// example takes that example on Tab while it is empty (owner's request B3-2).
// Every other Tab - on a field that holds text, Shift+Tab, a field without an
// example, a Tab while an input method is composing - moves focus as before.
// Real QML from src/Desktop/qml, in a Window, driven by real key events.
#include <QCoreApplication>
#include <QDir>
#include <QGuiApplication>
#include <QInputMethodEvent>
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

const char kExample[] = "补录安装前的历史完成数";

struct Scene
{
    QQmlEngine engine;
    std::unique_ptr<QObject> root;
    QString errors;

    bool create()
    {
        QQmlComponent component(&engine);
        // The middle field is shaped like the call sites: its text is bound to a
        // property the page owns, and its onTextChanged writes back to it.
        component.setData(R"(import QtQuick
import QtQuick.Window
import MentorRecorder
Window {
    width: 480; height: 360; visible: true
    property string reason: ""
    property int reasonWrites: 0
    Column {
        x: 20; y: 20; width: 300; spacing: 8
        StyledTextField { objectName: "before"; width: 300 }
        StyledTextField {
            objectName: "example"
            width: 300
            exampleText: "补录安装前的历史完成数"
            placeholderText: "例如：补录安装前的历史完成数" + exampleHint
            text: reason
            onTextChanged: { reason = text; reasonWrites += 1 }
        }
        StyledTextField { objectName: "after"; width: 300 }
    }
})", QUrl::fromLocalFile(qmlRoot() + QStringLiteral("/components/StyledTextFieldProbe.qml")));
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
    QQuickItem *field(const char *name) const
    {
        return root->findChild<QQuickItem *>(QString::fromLatin1(name));
    }

    /// Focuses \a name the way Tab would and waits until it holds focus.
    bool focus(const char *name) const
    {
        if (!QTest::qWaitForWindowExposed(window()))
            return false;
        QQuickItem *item = field(name);
        if (!item)
            return false;
        item->forceActiveFocus(Qt::TabFocusReason);
        return QTest::qWaitFor([item] { return item->hasActiveFocus(); }, 2000);
    }
};

} // namespace

class StyledTextFieldTests final : public QObject
{
    Q_OBJECT

private slots:
    void initTestCase();

    void tabOnAnEmptyFieldEntersItsExampleAsIfTyped();
    void tabOnAFieldThatHoldsTextMovesFocus();
    void shiftTabOnAnEmptyFieldMovesFocusBack();
    void tabOnAFieldWithoutAnExampleMovesFocus();
    void tabWhileAnInputMethodComposesIsNotSwallowed();
    void thePlaceholderEndsWithTheHint();
};

void StyledTextFieldTests::initTestCase()
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

void StyledTextFieldTests::tabOnAnEmptyFieldEntersItsExampleAsIfTyped()
{
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    QVERIFY(scene.focus("example"));
    QQuickItem *field = scene.field("example");

    QTest::keyClick(scene.window(), Qt::Key_Tab);
    const QString example = QString::fromUtf8(kExample);
    QCOMPARE(field->property("text").toString(), example);
    QCOMPARE(field->property("cursorPosition").toInt(), example.size());
    QVERIFY(field->hasActiveFocus());
    QVERIFY(!scene.field("after")->hasActiveFocus());
    // What reacts to the text changing reacted once, as to typing.
    QCOMPARE(scene.root->property("reason").toString(), example);
    QCOMPARE(scene.root->property("reasonWrites").toInt(), 1);

    // Entered the way typing enters text: the page's binding still drives the field.
    scene.root->setProperty("reason", QString());
    QCOMPARE(field->property("text").toString(), QString());
    scene.root->setProperty("reason", QString::fromUtf8("核对"));
    QCOMPARE(field->property("text").toString(), QString::fromUtf8("核对"));
}

void StyledTextFieldTests::tabOnAFieldThatHoldsTextMovesFocus()
{
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    QVERIFY(scene.focus("example"));
    QTest::keyClick(scene.window(), Qt::Key_A);
    QQuickItem *field = scene.field("example");
    QCOMPARE(field->property("text").toString(), QStringLiteral("a"));

    QTest::keyClick(scene.window(), Qt::Key_Tab);
    QTRY_VERIFY(scene.field("after")->hasActiveFocus());
    QCOMPARE(field->property("text").toString(), QStringLiteral("a"));
}

void StyledTextFieldTests::shiftTabOnAnEmptyFieldMovesFocusBack()
{
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    QQuickItem *field = scene.field("example");

    // Windows reports Shift+Tab as Backtab; a test harness may send Tab with Shift.
    QVERIFY(scene.focus("example"));
    QTest::keyClick(scene.window(), Qt::Key_Backtab, Qt::ShiftModifier);
    QTRY_VERIFY(scene.field("before")->hasActiveFocus());
    QCOMPARE(field->property("text").toString(), QString());

    QVERIFY(scene.focus("example"));
    QTest::keyClick(scene.window(), Qt::Key_Tab, Qt::ShiftModifier);
    QTRY_VERIFY(scene.field("before")->hasActiveFocus());
    QCOMPARE(field->property("text").toString(), QString());
    QCOMPARE(scene.root->property("reasonWrites").toInt(), 0);
}

void StyledTextFieldTests::tabOnAFieldWithoutAnExampleMovesFocus()
{
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    QVERIFY(scene.focus("before"));
    QTest::keyClick(scene.window(), Qt::Key_Tab);
    QTRY_VERIFY(scene.field("example")->hasActiveFocus());
    QCOMPARE(scene.field("before")->property("text").toString(), QString());
    // One Tab, one move: the field it lands on is not filled by the same key.
    QCOMPARE(scene.field("example")->property("text").toString(), QString());
}

void StyledTextFieldTests::tabWhileAnInputMethodComposesIsNotSwallowed()
{
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    QVERIFY(scene.focus("example"));
    QQuickItem *field = scene.field("example");

    // Pinyin being composed: the field is still empty, but it is not idle.
    QInputMethodEvent composing(QStringLiteral("bu"), {});
    QCoreApplication::sendEvent(field, &composing);
    QCOMPARE(field->property("preeditText").toString(), QStringLiteral("bu"));
    QCOMPARE(field->property("length").toInt(), 0);

    QTest::keyClick(scene.window(), Qt::Key_Tab);
    QVERIFY(field->property("text").toString() != QString::fromUtf8(kExample));
    QTRY_VERIFY(scene.field("after")->hasActiveFocus());
}

void StyledTextFieldTests::thePlaceholderEndsWithTheHint()
{
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    QCOMPARE(scene.field("example")->property("exampleHint").toString(),
             QString::fromUtf8("（按 Tab 填入）"));
    QCOMPARE(scene.field("example")->property("placeholderText").toString(),
             QString::fromUtf8("例如：补录安装前的历史完成数（按 Tab 填入）"));
}

int main(int argc, char *argv[])
{
    qputenv("QT_QPA_PLATFORM", "offscreen");
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication app(argc, argv);
    StyledTextFieldTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "StyledTextFieldTests.moc"
