// components/DateField.qml: the prototype's <input type="date"> for the history
// filter - a typed yyyy-MM-dd, or a day picked from the calendar behind the icon.
//
// Real QML from src/Desktop/qml, loaded from source the way IconTests does; the
// popup needs a Window, so the scene is one.
#include <QDate>
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
        component.setData(qml, QUrl::fromLocalFile(qmlRoot() + QStringLiteral("/components/DateProbe.qml")));
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

const QByteArray kScene = R"(import QtQuick
import QtQuick.Window
import MentorRecorder
Window {
    width: 480; height: 360; visible: true
    DateField { objectName: "field"; x: 20; y: 20; width: 118 }
})";

/// Repeater delegates (the month grid's day cells) hang off the visual tree
/// only, so findChild() over QObject children cannot see them.
QQuickItem *findItem(QQuickItem *root, const QString &name)
{
    if (!root)
        return nullptr;
    if (root->objectName() == name)
        return root;
    for (QQuickItem *child : root->childItems()) {
        if (QQuickItem *found = findItem(child, name))
            return found;
    }
    return nullptr;
}

QQuickItem *dayCell(QQuickItem *field, int day)
{
    QQuickItem *grid = field->findChild<QQuickItem *>(QStringLiteral("calendarGrid"));
    return grid ? findItem(grid, QStringLiteral("calendarDay") + QString::number(day)) : nullptr;
}

void clickItem(QQuickWindow *window, QQuickItem *item)
{
    const QPointF center = item->mapToScene(QPointF(item->width() / 2, item->height() / 2));
    QTest::mouseClick(window, Qt::LeftButton, Qt::NoModifier, center.toPoint());
}

} // namespace

class DateFieldTests final : public QObject
{
    Q_OBJECT

private slots:
    void initTestCase();

    void emptyFieldShowsThePlaceholderAndIsValid();
    void aTypedDateIsAcceptedAndNonsenseTurnsRed();
    void theCalendarOpensOnTheFieldsOwnMonth();
    void pickingADayWritesItAsText();
    void theArrowsTurnThePagesAcrossAYearEnd();
};

void DateFieldTests::initTestCase()
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

void DateFieldTests::emptyFieldShowsThePlaceholderAndIsValid()
{
    Scene scene;
    QVERIFY2(scene.create(kScene), qPrintable(scene.errors));
    QQuickItem *field = scene.field();
    QVERIFY(field);
    QCOMPARE(field->property("text").toString(), QString());
    QCOMPARE(field->property("placeholderText").toString(), QString::fromUtf8("年/月/日"));
    QVERIFY(field->property("valid").toBool());
    QVERIFY(!field->property("calendarOpen").toBool());
    QQuickItem *icon = field->findChild<QQuickItem *>(QStringLiteral("calendarIcon"));
    QVERIFY(icon);
    QTRY_COMPARE_WITH_TIMEOUT(icon->property("status").toInt(), 1 /* Image.Ready */, 5000);
}

void DateFieldTests::aTypedDateIsAcceptedAndNonsenseTurnsRed()
{
    Scene scene;
    QVERIFY2(scene.create(kScene), qPrintable(scene.errors));
    QQuickItem *field = scene.field();
    field->setProperty("text", QStringLiteral("2026-09-17"));
    QVERIFY(field->property("valid").toBool());
    const QColor good = field->property("color").value<QColor>();

    for (const char *bad : {"2026-13-01", "2026-02-30", "17/09/2026", "abc"}) {
        field->setProperty("text", QString::fromLatin1(bad));
        QVERIFY2(!field->property("valid").toBool(), bad);
        QVERIFY2(field->property("color").value<QColor>() != good, bad);
    }
    field->setProperty("text", QStringLiteral(" 2026-09-17 "));
    QVERIFY(field->property("valid").toBool());
}

void DateFieldTests::theCalendarOpensOnTheFieldsOwnMonth()
{
    Scene scene;
    QVERIFY2(scene.create(kScene), qPrintable(scene.errors));
    QQuickItem *field = scene.field();
    field->setProperty("text", QStringLiteral("2025-02-14"));
    QVERIFY(QMetaObject::invokeMethod(field, "openCalendar"));
    QTRY_VERIFY(field->property("calendarOpen").toBool());
    QQuickItem *grid = field->findChild<QQuickItem *>(QStringLiteral("calendarGrid"));
    QVERIFY(grid);
    QCOMPARE(grid->property("year").toInt(), 2025);
    QCOMPARE(grid->property("month").toInt(), 1);
    // The 14th is the selected cell; the 1st is not.
    QQuickItem *day14 = dayCell(field, 14);
    QQuickItem *day1 = dayCell(field, 1);
    QVERIFY(day14);
    QVERIFY(day1);
    QVERIFY(day14->property("selected").toBool());
    QVERIFY(!day1->property("selected").toBool());

    // Empty field: today's month.
    field->setProperty("text", QString());
    QVERIFY(QMetaObject::invokeMethod(field, "openCalendar"));
    const QDate today = QDate::currentDate();
    QCOMPARE(grid->property("year").toInt(), today.year());
    QCOMPARE(grid->property("month").toInt(), today.month() - 1);
}

void DateFieldTests::pickingADayWritesItAsText()
{
    Scene scene;
    QVERIFY2(scene.create(kScene), qPrintable(scene.errors));
    QQuickWindow *window = scene.window();
    QVERIFY(window);
    QVERIFY(QTest::qWaitForWindowExposed(window));
    QQuickItem *field = scene.field();
    field->setProperty("text", QStringLiteral("2026-09-17"));
    QVERIFY(QMetaObject::invokeMethod(field, "openCalendar"));
    QTRY_VERIFY(field->property("calendarOpen").toBool());

    QQuickItem *day3 = dayCell(field, 3);
    QVERIFY(day3);
    QTRY_VERIFY(day3->width() > 0 && day3->isVisible());
    clickItem(window, day3);

    QTRY_COMPARE(field->property("text").toString(), QStringLiteral("2026-09-03"));
    QTRY_VERIFY(!field->property("calendarOpen").toBool());
    QVERIFY(field->property("valid").toBool());
}

void DateFieldTests::theArrowsTurnThePagesAcrossAYearEnd()
{
    Scene scene;
    QVERIFY2(scene.create(kScene), qPrintable(scene.errors));
    QQuickItem *field = scene.field();
    field->setProperty("text", QStringLiteral("2026-12-05"));
    QVERIFY(QMetaObject::invokeMethod(field, "openCalendar"));
    QQuickItem *grid = field->findChild<QQuickItem *>(QStringLiteral("calendarGrid"));
    QVERIFY(grid);

    QVERIFY(QMetaObject::invokeMethod(field, "shiftMonth", Q_ARG(QVariant, 1)));
    QCOMPARE(grid->property("year").toInt(), 2027);
    QCOMPARE(grid->property("month").toInt(), 0);
    QVERIFY(QMetaObject::invokeMethod(field, "shiftMonth", Q_ARG(QVariant, -2)));
    QCOMPARE(grid->property("year").toInt(), 2026);
    QCOMPARE(grid->property("month").toInt(), 10);
    // Turning pages never touches the field's own text.
    QCOMPARE(field->property("text").toString(), QStringLiteral("2026-12-05"));
    QQuickItem *label = field->findChild<QQuickItem *>(QStringLiteral("calendarMonthLabel"));
    QVERIFY(label);
    QCOMPARE(label->property("text").toString(), QString::fromUtf8("2026 年 11 月"));
}

int main(int argc, char *argv[])
{
    qputenv("QT_QPA_PLATFORM", "offscreen");
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication app(argc, argv);
    DateFieldTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "DateFieldTests.moc"
