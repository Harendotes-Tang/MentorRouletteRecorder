// components/TimeField.qml: the wizard's 匹配 / 进本 / 结束 time - digits typed
// without punctuation become HH:mm, and hour and minute can be picked from the
// wheels behind the clock icon. Real QML from src/Desktop/qml, in a Window.
#include "DesktopTestRunner.h"
#include "QmlComponentTestSupport.h"
#include <QDir>
#include <QGuiApplication>
#include <QQuickItem>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QWheelEvent>
#include <QTest>
#include <QTime>

namespace {

struct Scene : mrtest::QmlComponentScene
{
    bool create()
    {
        return QmlComponentScene::create(R"(import QtQuick
import QtQuick.Window
import MentorRecorder
Window {
    width: 480; height: 360; visible: true
    TimeField { objectName: "field"; x: 20; y: 20; width: 140 }
})", QStringLiteral("/components/TimeProbe.qml"));
    }

    QQuickItem *field() const { return root->findChild<QQuickItem *>(QStringLiteral("field")); }
};

/// Types \a text into the focused field the way a player does, key by key.
void typeText(QQuickWindow *window, const QString &text)
{
    for (const QChar &character : text)
        QTest::keyClick(window, character.toLatin1());
}

bool wheelSettled(QQuickItem *wheel)
{
    QList<QQuickItem *> pending{wheel};
    const QString name = wheel->objectName() + QStringLiteral("Value")
        + QString::number(wheel->property("currentIndex").toInt());
    while (!pending.isEmpty()) {
        auto *item = pending.takeFirst();
        pending.append(item->childItems());
        if (item->objectName() == name) {
            const auto center = item->mapToItem(wheel, QPointF(item->width() / 2, item->height() / 2));
            return qAbs(center.y() - wheel->height() / 2) < 0.5;
        }
    }
    return false;
}

} // namespace

namespace {
class TimeFieldTests final : public QObject
{
    Q_OBJECT

private slots:
    void initTestCase();

    void bareDigitsBecomeATimeAsTheyAreTyped();
    void backspaceGetsPastAnAddedColon();
    void leavingTheFieldWritesTheCanonicalForm();
    void nonsenseStaysAsTypedAndTurnsRed();
    void theWheelsOpenOnTheFieldsOwnTimeAndWriteBack();
    void visibleNumbersCanBeClickedAndWheeled();
    void draggingTheHourColumnStillWorks();
    void nowWritesTheClockWithSeconds();
};
} // namespace

void TimeFieldTests::initTestCase()
{
    QVERIFY(QDir(mrtest::qmlSourceRoot()).exists());
    mrtest::registerSourceQmlComponents();
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

// Review L1-8: the colon added after two digits must not come back when the
// player deletes it - only an edit that makes the text longer is punctuated.
void TimeFieldTests::backspaceGetsPastAnAddedColon()
{
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    QQuickWindow *window = scene.window();
    QVERIFY(QTest::qWaitForWindowExposed(window));
    QQuickItem *field = scene.field();
    field->forceActiveFocus();
    QTRY_VERIFY(field->hasActiveFocus());

    typeText(window, QStringLiteral("21"));
    QCOMPARE(field->property("text").toString(), QStringLiteral("21:"));
    QTest::keyClick(window, Qt::Key_Backspace);
    QCOMPARE(field->property("text").toString(), QStringLiteral("21"));
    QTest::keyClick(window, Qt::Key_Backspace);
    QCOMPARE(field->property("text").toString(), QStringLiteral("2"));

    // Typing on is punctuated as before.
    typeText(window, QStringLiteral("030"));
    QCOMPARE(field->property("text").toString(), QStringLiteral("20:30"));
    QVERIFY(field->property("valid").toBool());
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

void TimeFieldTests::visibleNumbersCanBeClickedAndWheeled()
{
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    auto *window = scene.window();
    QVERIFY(QTest::qWaitForWindowExposed(window));
    auto *field = scene.field();
    field->setProperty("text", QStringLiteral("15:58"));
    QVERIFY(QMetaObject::invokeMethod(field, "openPicker"));
    auto *hours = field->findChild<QQuickItem *>(QStringLiteral("hourWheel"));
    auto *minutes = field->findChild<QQuickItem *>(QStringLiteral("minuteWheel"));
    QVERIFY(hours && minutes);
    const auto point = [](QQuickItem *wheel, qreal rowOffset) {
        return wheel->mapToScene(QPointF(wheel->width() / 2,
                                        wheel->height() / 2 + rowOffset * wheel->height() / 5));
    };
    QTRY_VERIFY(wheelSettled(hours) && wheelSettled(minutes));
    QCOMPARE(hours->property("currentIndex").toInt(), 15);
    QCOMPARE(minutes->property("currentIndex").toInt(), 58);
    QTest::mouseClick(window, Qt::LeftButton, Qt::NoModifier, point(hours, -1).toPoint());
    QTRY_COMPARE(hours->property("currentIndex").toInt(), 14);
    QTRY_VERIFY(wheelSettled(hours));
    QTest::mouseClick(window, Qt::LeftButton, Qt::NoModifier, point(hours, -1).toPoint());
    QTRY_COMPARE(hours->property("currentIndex").toInt(), 13);
    QTRY_VERIFY(wheelSettled(hours));
    QTest::mouseClick(window, Qt::LeftButton, Qt::NoModifier, point(hours, 2).toPoint());
    QTRY_COMPARE(hours->property("currentIndex").toInt(), 15);
    QTest::mouseClick(window, Qt::LeftButton, Qt::NoModifier, point(minutes, -2).toPoint());
    QTRY_COMPARE(minutes->property("currentIndex").toInt(), 56);

    const auto wheelEvent = [&](QQuickItem *wheel, int delta) {
        const QPointF pos = point(wheel, 0);
        QWheelEvent event(pos, window->mapToGlobal(pos.toPoint()), {}, QPoint(0, delta),
                          Qt::NoButton, Qt::NoModifier, Qt::NoScrollPhase, false);
        QCoreApplication::sendEvent(window, &event);
    };
    wheelEvent(minutes, -120);
    QTRY_COMPARE(minutes->property("currentIndex").toInt(), 57);
    wheelEvent(hours, 240);
    QTRY_COMPARE(hours->property("currentIndex").toInt(), 13);
    minutes->setProperty("currentIndex", 0);
    wheelEvent(minutes, 120);
    QTRY_COMPARE(minutes->property("currentIndex").toInt(), 59);
    wheelEvent(minutes, -60);
    QCOMPARE(minutes->property("currentIndex").toInt(), 59);
    wheelEvent(minutes, -60);
    QTRY_COMPARE(minutes->property("currentIndex").toInt(), 0);
    QCOMPARE(field->property("text").toString(), QStringLiteral("15:58"));
    QVERIFY(QMetaObject::invokeMethod(field, "select", Q_ARG(QVariant, 13), Q_ARG(QVariant, 0)));
    QCOMPARE(field->property("text").toString(), QStringLiteral("13:00"));
}

void TimeFieldTests::draggingTheHourColumnStillWorks()
{
    Scene scene;
    QVERIFY2(scene.create(), qPrintable(scene.errors));
    QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
    auto *field = scene.field();
    field->setProperty("text", QStringLiteral("15:58"));
    QVERIFY(QMetaObject::invokeMethod(field, "openPicker"));
    auto *hours = field->findChild<QQuickItem *>(QStringLiteral("hourWheel"));
    auto *minutes = field->findChild<QQuickItem *>(QStringLiteral("minuteWheel"));
    QVERIFY(hours && minutes);
    QTRY_VERIFY(wheelSettled(hours) && wheelSettled(minutes));
    const QPoint start = hours->mapToScene(QPointF(hours->width() / 2, hours->height() / 2)).toPoint();
    QTest::mousePress(scene.window(), Qt::LeftButton, Qt::NoModifier, start);
    for (int step = 1; step <= 8; ++step)
        QTest::mouseMove(scene.window(), start + QPoint(0, step * 5), 20);
    QTest::mouseRelease(scene.window(), Qt::LeftButton, Qt::NoModifier, start + QPoint(0, 40));
    QTRY_VERIFY(hours->property("currentIndex").toInt() != 15);
    QCOMPARE(minutes->property("currentIndex").toInt(), 58);
    QCOMPARE(field->property("text").toString(), QStringLiteral("15:58"));
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

MR_DESKTOP_TEST_SUITE(TimeField)
{
    qputenv("QT_QPA_PLATFORM", "offscreen");
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication app(argc, argv);
    TimeFieldTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "TimeFieldTests.moc"
