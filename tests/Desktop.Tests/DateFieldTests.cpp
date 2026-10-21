// components/DateField.qml: the prototype's <input type="date"> for the history
// filter - a typed yyyy-MM-dd, or a day picked from the calendar behind the icon.
//
// Real QML from src/Desktop/qml, loaded from source the way IconTests does; the
// popup needs a Window, so the scene is one.
#include <QDate>
#include <QDir>
#include <QGuiApplication>
#include <QFontDatabase>
#include <QQmlComponent>
#include <QQmlEngine>
#include <QQmlContext>
#include <QWheelEvent>
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

// The history page's case: the calendar opens over the table, whose rows listen
// with a TapHandler (HistoryPage's row selection).
const QByteArray kSceneOverRows = R"(import QtQuick
import QtQuick.Window
import MentorRecorder
Window {
    width: 480; height: 360; visible: true
    property int rowTaps: 0
    Rectangle {
        anchors.fill: parent
        TapHandler { onTapped: rowTaps += 1 }
    }
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
    void onlyTheChosenDayOfItsOwnMonthIsHighlighted();
    void aClickInTheCalendarNeverReachesTheRowBeneathIt();
    void rangeSelectionAndCancellation();
    void monthYearWheelAndKeyboard();
    void recordAwareRangeDisablesEmptyDatesButSingleDateDoesNot();
    void rangeCalendarScreenshots_data();
    void rangeCalendarScreenshots();
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

// 审查 OL-9：选中格只比较了日与年，翻到同一年的其他月份，同一个日号也被高亮。
void DateFieldTests::onlyTheChosenDayOfItsOwnMonthIsHighlighted()
{
    Scene scene;
    QVERIFY2(scene.create(kScene), qPrintable(scene.errors));
    QQuickItem *field = scene.field();
    field->setProperty("text", QStringLiteral("2026-09-17"));
    QVERIFY(QMetaObject::invokeMethod(field, "openCalendar"));
    QTRY_VERIFY(field->property("calendarOpen").toBool());
    QVERIFY(dayCell(field, 17)->property("selected").toBool());

    QVERIFY(QMetaObject::invokeMethod(field, "shiftMonth", Q_ARG(QVariant, 1)));
    QTest::qWait(30);
    QTRY_VERIFY(dayCell(field, 17));
    QVERIFY(!dayCell(field, 17)->property("selected").toBool());
    QVERIFY(QMetaObject::invokeMethod(field, "shiftMonth", Q_ARG(QVariant, -2)));
    QTest::qWait(30);
    QTRY_VERIFY(dayCell(field, 17));
    QVERIFY(!dayCell(field, 17)->property("selected").toBool());
    QVERIFY(QMetaObject::invokeMethod(field, "shiftMonth", Q_ARG(QVariant, 1)));
    QTRY_VERIFY(dayCell(field, 17) && dayCell(field, 17)->property("selected").toBool());
}

// 嫌疑 K-s1：日历是非模态弹层，日格只用被动抓取的 TapHandler，背景也不接收按下，
// 一次点选可能同时落到下面历史表格的行上（与 a1d5c06 修过的对话框同一机理）。
void DateFieldTests::aClickInTheCalendarNeverReachesTheRowBeneathIt()
{
    Scene scene;
    QVERIFY2(scene.create(kSceneOverRows), qPrintable(scene.errors));
    QQuickWindow *window = scene.window();
    QVERIFY(QTest::qWaitForWindowExposed(window));
    QQuickItem *field = scene.field();
    field->setProperty("text", QStringLiteral("2026-09-17"));
    QVERIFY(QMetaObject::invokeMethod(field, "openCalendar"));
    QTRY_VERIFY(field->property("calendarOpen").toBool());

    // The month label: a gap in the calendar with nothing of its own to press.
    QQuickItem *label = field->findChild<QQuickItem *>(QStringLiteral("calendarMonthLabel"));
    QVERIFY(label);
    QTRY_VERIFY(label->width() > 0 && label->isVisible());
    clickItem(window, label);
    QTest::qWait(50);
    QVERIFY(field->property("calendarOpen").toBool());
    QCOMPARE(scene.root->property("rowTaps").toInt(), 0);

    // The title now opens month selection; choosing September returns to days.
    QVERIFY(field->property("monthView").toBool());
    auto *september = findItem(window->contentItem(), QStringLiteral("calendarMonth9"));
    QVERIFY(september);
    clickItem(window, september);
    QTRY_VERIFY(!field->property("monthView").toBool());
    // Popup and day-grid geometry settle on the next polish after switching views.
    QTest::qWait(50);
    // A day: it is picked, and nothing underneath is.
    QQuickItem *day3 = dayCell(field, 3);
    QVERIFY(day3);
    clickItem(window, day3);
    QTRY_COMPARE(field->property("text").toString(), QStringLiteral("2026-09-03"));
    QTest::qWait(50);
    QCOMPARE(scene.root->property("rowTaps").toInt(), 0);
}

void DateFieldTests::rangeSelectionAndCancellation()
{
    Scene scene;
    QByteArray qml = kScene;
    qml.replace("height: 360", "height: 580");
    qml.replace("width: 118", "width: 238; rangeMode: true");
    QVERIFY2(scene.create(qml), qPrintable(scene.errors));
    auto *field = scene.field();
    auto *window = scene.window();
    QVERIFY(QTest::qWaitForWindowExposed(window));
    field->setProperty("startText", QStringLiteral("2026-09-17"));
    field->setProperty("endText", QStringLiteral("2026-09-19"));
    QVERIFY(QMetaObject::invokeMethod(field, "openCalendar"));
    clickItem(window, dayCell(field, 28));
    QVERIFY(field->property("selectionPending").toBool());
    QVERIFY(field->property("calendarOpen").toBool());
    QCOMPARE(field->property("startText").toString(), QStringLiteral("2026-09-28"));
    QCOMPARE(field->property("endText").toString(), QString());
    QVERIFY(QMetaObject::invokeMethod(field, "shiftMonth", Q_ARG(QVariant, 1)));
    QTRY_VERIFY(dayCell(field, 3));
    clickItem(window, dayCell(field, 3));
    QTRY_VERIFY(!field->property("calendarOpen").toBool());
    QCOMPARE(field->property("endText").toString(), QStringLiteral("2026-10-03"));
    QVERIFY(!field->property("selectionPending").toBool());

    QVERIFY(QMetaObject::invokeMethod(field, "openCalendar"));
    clickItem(window, dayCell(field, 20));
    QTest::keyClick(window, Qt::Key_Escape);
    QTRY_VERIFY(!field->property("calendarOpen").toBool());
    QCOMPARE(field->property("startText").toString(), QStringLiteral("2026-09-28"));
    QCOMPARE(field->property("endText").toString(), QStringLiteral("2026-10-03"));
    QVERIFY(field->hasActiveFocus());

    QVERIFY(QMetaObject::invokeMethod(field, "openCalendar"));
    clickItem(window, dayCell(field, 20));
    QTest::mouseClick(window, Qt::LeftButton, Qt::NoModifier, QPoint(450, 550));
    QTRY_VERIFY(!field->property("calendarOpen").toBool());
    QCOMPARE(field->property("startText").toString(), QStringLiteral("2026-09-28"));
    QCOMPARE(field->property("endText").toString(), QStringLiteral("2026-10-03"));

    // Reverse order is normalized; same-day endpoints are also valid.
    QVERIFY(QMetaObject::invokeMethod(field, "openCalendar"));
    clickItem(window, dayCell(field, 20));
    clickItem(window, dayCell(field, 3));
    QTRY_VERIFY(!field->property("calendarOpen").toBool());
    QCOMPARE(field->property("startText").toString(), QStringLiteral("2026-09-03"));
    QCOMPARE(field->property("endText").toString(), QStringLiteral("2026-09-20"));
    QVERIFY(QMetaObject::invokeMethod(field, "openCalendar"));
    clickItem(window, dayCell(field, 8));
    clickItem(window, dayCell(field, 8));
    QCOMPARE(field->property("endText").toString(), QStringLiteral("2026-09-08"));
    QVERIFY(!field->findChild<QObject *>(QStringLiteral("calendarMultiSelect")));
    QVERIFY(!field->property("calendarOpen").toBool());
}

void DateFieldTests::recordAwareRangeDisablesEmptyDatesButSingleDateDoesNot()
{
    Scene scene;
    QByteArray qml = kScene;
    qml.replace("height: 360", "height: 580");
    qml.replace("width: 118", R"(width: 238; rangeMode: true; restrictToRecords: true; availableDays: ["2026-09-07", "2026-09-17"]; knownMonths: ["2026-09"])");
    QVERIFY2(scene.create(qml), qPrintable(scene.errors));
    auto *field = scene.field();
    auto *window = scene.window();
    QVERIFY(QTest::qWaitForWindowExposed(window));
    field->setProperty("startText", QStringLiteral("2026-09-07"));
    field->setProperty("endText", QStringLiteral("2026-09-17"));
    QVERIFY(QMetaObject::invokeMethod(field, "openCalendar"));
    QTest::qWait(50);
    QVERIFY(dayCell(field, 7)->isEnabled());
    QVERIFY(!dayCell(field, 8)->isEnabled());
    clickItem(window, dayCell(field, 8));
    QVERIFY(!field->property("selectionPending").toBool());
    QCOMPARE(field->property("startText").toString(), QStringLiteral("2026-09-07"));
    clickItem(window, dayCell(field, 7));
    QVERIFY(field->property("calendarOpen").toBool());
    QVERIFY(field->property("selectionPending").toBool());
    clickItem(window, dayCell(field, 7));
    QTRY_VERIFY(!field->property("calendarOpen").toBool());
    QCOMPARE(field->property("endText").toString(), QStringLiteral("2026-09-07"));
    // Record days only limit what the calendar offers: a whole date already in
    // the field stays valid and is queried (review K1-1). Only a malformed one is not.
    field->setProperty("endText", QStringLiteral("2026-09-08"));
    QVERIFY(field->property("valid").toBool());
    QVERIFY(!dayCell(field, 8)->isEnabled());
    field->setProperty("endText", QStringLiteral("2026-09-31"));
    QVERIFY(!field->property("valid").toBool());

    // The same component in the new-record wizard permits a record-free date
    // and commits it after ONE click, regardless of range availability inputs.
    Scene single;
    QVERIFY2(single.create(kScene), qPrintable(single.errors));
    auto *singleField = single.field();
    QVERIFY(!singleField->property("rangeMode").toBool());
    QVERIFY(!singleField->property("restrictToRecords").toBool());
    singleField->setProperty("availableDays", QStringList{});
    singleField->setProperty("text", QStringLiteral("2026-09-07"));
    QVERIFY(QTest::qWaitForWindowExposed(single.window()));
    QVERIFY(QMetaObject::invokeMethod(singleField, "openCalendar"));
    QTest::qWait(50);
    QVERIFY(dayCell(singleField, 8)->isEnabled());
    clickItem(single.window(), dayCell(singleField, 8));
    QTRY_VERIFY(!singleField->property("calendarOpen").toBool());
    QCOMPARE(singleField->property("text").toString(), QStringLiteral("2026-09-08"));
}

void DateFieldTests::monthYearWheelAndKeyboard()
{
    Scene scene;
    QVERIFY2(scene.create(kScene), qPrintable(scene.errors));
    auto *window = scene.window();
    QVERIFY(QTest::qWaitForWindowExposed(window));
    auto *field = scene.field();
    field->setProperty("text", QStringLiteral("2026-09-17"));
    window->contentItem()->forceActiveFocus();
    QTest::keyClick(window, Qt::Key_Tab);
    QTRY_VERIFY(field->hasActiveFocus());
    QTest::keyClick(window, Qt::Key_Down, Qt::AltModifier);
    QTRY_VERIFY(field->property("calendarOpen").toBool());
    auto *title = field->findChild<QQuickItem *>(QStringLiteral("calendarMonthLabel"));
    QVERIFY(title->hasActiveFocus());
    QTest::keyClick(window, Qt::Key_Space);
    QTRY_VERIFY(field->property("monthView").toBool());
    auto *year = field->findChild<QQuickItem *>(QStringLiteral("calendarYearSelect"));
    QVERIFY(year->hasActiveFocus());
    QTest::keyClick(window, Qt::Key_Tab, Qt::ShiftModifier);
    QTRY_VERIFY(field->findChild<QQuickItem *>(QStringLiteral("calendarPrevMonth"))->hasActiveFocus());
    QTest::keyClick(window, Qt::Key_Tab);
    QTRY_VERIFY(year->hasActiveFocus());
    QTest::keyClick(window, Qt::Key_Space);
    auto *yearPopup = year->property("popup").value<QObject *>();
    QVERIFY(yearPopup);
    QTRY_VERIFY(yearPopup->property("visible").toBool());
    QTest::keyClick(window, Qt::Key_Up);
    QTest::keyClick(window, Qt::Key_Return);
    auto *grid = field->findChild<QQuickItem *>(QStringLiteral("calendarGrid"));
    QTRY_COMPARE(grid->property("year").toInt(), 2025);
    auto *month = findItem(window->contentItem(), QStringLiteral("calendarMonth12"));
    QVERIFY(month);
    const QPointF monthPos = month->mapToScene(QPointF(month->width()/2, month->height()/2));
    QWheelEvent yearWheel(monthPos, window->mapToGlobal(monthPos.toPoint()), QPoint(), QPoint(0, -120),
                          Qt::NoButton, Qt::NoModifier, Qt::NoScrollPhase, false);
    QCoreApplication::sendEvent(window, &yearWheel);
    QTRY_COMPARE(grid->property("year").toInt(), 2026);
    clickItem(window, month);
    QTRY_COMPARE(grid->property("month").toInt(), 11);
    QVERIFY(!field->property("monthView").toBool());
    const QPointF dayPos = dayCell(field, 3)->mapToScene(QPointF(10, 10));
    QWheelEvent monthWheel(dayPos, window->mapToGlobal(dayPos.toPoint()), QPoint(), QPoint(0, -120),
                           Qt::NoButton, Qt::NoModifier, Qt::NoScrollPhase, false);
    QCoreApplication::sendEvent(window, &monthWheel);
    QTRY_COMPARE(grid->property("year").toInt(), 2027);
    QCOMPARE(grid->property("month").toInt(), 0);
    QCOMPARE(field->property("text").toString(), QStringLiteral("2026-09-17"));
    QTest::keyClick(window, Qt::Key_Escape);
    QTRY_VERIFY(!field->property("calendarOpen").toBool());
    QVERIFY(field->hasActiveFocus());
}

void DateFieldTests::rangeCalendarScreenshots_data()
{
    QTest::addColumn<QString>("style");
    QTest::addColumn<bool>("dark");
    QTest::newRow("classic-light") << QStringLiteral("classic") << false;
    QTest::newRow("classic-dark") << QStringLiteral("classic") << true;
    QTest::newRow("harendotes-light") << QStringLiteral("harendotes") << false;
    QTest::newRow("harendotes-dark") << QStringLiteral("harendotes") << true;
}
void DateFieldTests::rangeCalendarScreenshots()
{
    QFETCH(QString, style);
    QFETCH(bool, dark);
    Scene scene;
    QStringList warnings;
    connect(&scene.engine, &QQmlEngine::warnings, this, [&warnings](const QList<QQmlError> &errors) {
        for (const auto &error : errors) warnings.append(error.toString());
    });
    scene.engine.rootContext()->setContextProperty(QStringLiteral("ForceUiStyle"), style);
    scene.engine.rootContext()->setContextProperty(QStringLiteral("App"), QVariantMap{{QStringLiteral("dark"), dark}});
    QByteArray qml = kScene;
    qml.replace("height: 360", "height: 580; color: Theme.windowBackground");
    qml.replace("width: 118", "width: 238; rangeMode: true");
    QVERIFY2(scene.create(qml), qPrintable(scene.errors));
    auto *field = scene.field();
    auto *window = scene.window();
    QVERIFY(QTest::qWaitForWindowExposed(window));
    field->setProperty("startText", QStringLiteral("2026-09-07"));
    field->setProperty("endText", QStringLiteral("2026-09-17"));
    field->setProperty("restrictToRecords", true);
    field->setProperty("availableDays", QStringList{QStringLiteral("2026-09-07"), QStringLiteral("2026-09-17"), QStringLiteral("2026-09-21")});
    field->setProperty("knownMonths", QStringList{QStringLiteral("2026-09")});
    QVERIFY(QMetaObject::invokeMethod(field, "openCalendar"));
    QTRY_VERIFY(dayCell(field, 7)->property("selected").toBool());
    QVERIFY(dayCell(field, 10)->property("inRange").toBool());
    const QString dir = qEnvironmentVariable("MR_HISTORY_REVIEW_EVIDENCE_DIR");
    if (!dir.isEmpty()) {
        QVERIFY(QDir().mkpath(dir));
        QTest::qWait(100);
        QVERIFY(window->grabWindow().save(dir + QStringLiteral("/date-range-%1-%2.png").arg(style, dark ? QStringLiteral("dark") : QStringLiteral("light"))));
        clickItem(window, field->findChild<QQuickItem *>(QStringLiteral("calendarMonthLabel")));
        QTest::qWait(100);
        QVERIFY(window->grabWindow().save(dir + QStringLiteral("/date-months-%1-%2.png").arg(style, dark ? QStringLiteral("dark") : QStringLiteral("light"))));
    }
    QVERIFY2(warnings.isEmpty(), qPrintable(warnings.join('\n')));
}

int main(int argc, char *argv[])
{
    qputenv("QT_QPA_PLATFORM", "offscreen");
    QQuickStyle::setStyle(QStringLiteral("Basic"));
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
    DateFieldTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "DateFieldTests.moc"
