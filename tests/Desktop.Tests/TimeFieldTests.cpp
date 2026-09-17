// components/TimeField.qml: the wizard's 匹配 / 进本 / 结束 time - digits typed
// without punctuation become HH:mm, and hour and minute can be picked from the
// wheels behind the clock icon. Real QML from src/Desktop/qml, in a Window.
#include <QDir>
#include <QGuiApplication>
#include <QQmlComponent>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QTest>
#include <QTime>
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

    bool create()
    {
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import QtQuick.Window
import MentorRecorder
Window {
    width: 480; height: 360; visible: true
    TimeField { objectName: "field"; x: 20; y: 20; width: 140 }
})", QUrl::fromLocalFile(qmlRoot() + QStringLiteral("/components/TimeProbe.qml")));
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
    QQuickItem *field() const { return root->findChild<QQuickItem *>(QStringLiteral("field")); }
};

/// Types \a text into the focused field the way a player does, key by key.
void typeText(QQuickWindow *window, const QString &text)
{
    for (const QChar &character : text)
        QTest::keyClick(window, character.toLatin1());
}

} // namespace

class TimeFieldTests final : public QObject
{
    Q_OBJECT

private slots:
    void initTestCase();

    void bareDigitsBecomeATimeAsTheyAreTyped();
    void leavingTheFieldWritesTheCanonicalForm();
    void nonsenseStaysAsTypedAndTurnsRed();
    void theWheelsOpenOnTheFieldsOwnTimeAndWriteBack();
    void nowWritesTheClockWithSeconds();
};

void TimeFieldTests::initTestCase()
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

void TimeFieldTests::bareDigitsBecomeATimeAsTheyAreTyped()
{
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    QQuickWindow *window = scene.window();
    QVERIFY(QTest::qWaitForWindowExposed(window));
    QQuickItem *field = scene.field();
    field->forceActiveFocus();
    QTRY_VERIFY(field->hasActiveFocus());

    typeText(window, QStringLiteral("2130"));
    QCOMPARE(field->property("text").toString(), QStringLiteral("21:30"));
    QVERIFY(field->property("valid").toBool());

    field->setProperty("text", QString());
    typeText(window, QStringLiteral("930"));
    QCOMPARE(field->property("text").toString(), QStringLiteral("09:30"));

    field->setProperty("text", QString());
    typeText(window, QStringLiteral("213045"));
    QCOMPARE(field->property("text").toString(), QStringLiteral("21:30:45"));

    // Two digits that cannot start an hour are left alone until more arrive.
    field->setProperty("text", QString());
    typeText(window, QStringLiteral("99"));
    QCOMPARE(field->property("text").toString(), QStringLiteral("99"));
    QVERIFY(!field->property("valid").toBool());
}

void TimeFieldTests::leavingTheFieldWritesTheCanonicalForm()
{
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    QQuickItem *field = scene.field();
    field->setProperty("text", QStringLiteral("9:5"));
    QVERIFY(field->property("valid").toBool());
    QVERIFY(QMetaObject::invokeMethod(field, "canonicalize"));
    QCOMPARE(field->property("text").toString(), QStringLiteral("09:05"));

    // Seconds and milliseconds survive; a zero tail is dropped.
    field->setProperty("text", QStringLiteral("21:30:07.5"));
    QVERIFY(QMetaObject::invokeMethod(field, "canonicalize"));
    QCOMPARE(field->property("text").toString(), QStringLiteral("21:30:07.500"));
    field->setProperty("text", QStringLiteral("21:30:00"));
    QVERIFY(QMetaObject::invokeMethod(field, "canonicalize"));
    QCOMPARE(field->property("text").toString(), QStringLiteral("21:30"));
}

void TimeFieldTests::nonsenseStaysAsTypedAndTurnsRed()
{
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    QQuickItem *field = scene.field();
    field->setProperty("text", QStringLiteral("21:30"));
    const QColor good = field->property("color").value<QColor>();
    for (const char *bad : {"24:00", "12:60", "12:30:61", "noon", "12"}) {
        field->setProperty("text", QString::fromLatin1(bad));
        QVERIFY2(!field->property("valid").toBool(), bad);
        QVERIFY2(field->property("color").value<QColor>() != good, bad);
        QVERIFY(QMetaObject::invokeMethod(field, "canonicalize"));
        QCOMPARE(field->property("text").toString(), QString::fromLatin1(bad));
    }
    field->setProperty("text", QString());
    QVERIFY(field->property("valid").toBool());
}

void TimeFieldTests::theWheelsOpenOnTheFieldsOwnTimeAndWriteBack()
{
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    QQuickItem *field = scene.field();
    field->setProperty("text", QStringLiteral("14:05"));
    QVERIFY(QMetaObject::invokeMethod(field, "openPicker"));
    QTRY_VERIFY(field->property("pickerOpen").toBool());
    QQuickItem *hours = field->findChild<QQuickItem *>(QStringLiteral("hourWheel"));
    QQuickItem *minutes = field->findChild<QQuickItem *>(QStringLiteral("minuteWheel"));
    QVERIFY(hours && minutes);
    QCOMPARE(hours->property("currentIndex").toInt(), 14);
    QCOMPARE(minutes->property("currentIndex").toInt(), 5);

    hours->setProperty("currentIndex", 7);
    minutes->setProperty("currentIndex", 45);
    QVERIFY(QMetaObject::invokeMethod(field, "select", Q_ARG(QVariant, 7), Q_ARG(QVariant, 45)));
    QCOMPARE(field->property("text").toString(), QStringLiteral("07:45"));
    QTRY_VERIFY(!field->property("pickerOpen").toBool());
}

void TimeFieldTests::nowWritesTheClockWithSeconds()
{
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    QQuickItem *field = scene.field();
    const QTime before = QTime::currentTime();
    QVERIFY(QMetaObject::invokeMethod(field, "selectNow"));
    const QTime after = QTime::currentTime();
    const QTime written = QTime::fromString(field->property("text").toString(), QStringLiteral("HH:mm:ss"));
    QVERIFY(written.isValid());
    QVERIFY(written >= QTime(before.hour(), before.minute(), before.second()));
    QVERIFY(written <= after);
    QVERIFY(field->property("valid").toBool());
}

int main(int argc, char *argv[])
{
    qputenv("QT_QPA_PLATFORM", "offscreen");
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication app(argc, argv);
    TimeFieldTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "TimeFieldTests.moc"
