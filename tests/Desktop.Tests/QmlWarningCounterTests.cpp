// ---------------------------------------------------------------------------
// tst_qmlwarningcounter - the screenshot harness counts QML/JS runtime
// warnings itself (review DT4-X5).
//
// A screenshot run carries on to the frame after a binding threw, so before
// this only CTest's FAIL_REGULAR_EXPRESSION noticed; the screenshot that
// scripts/package.ps1 -Verify takes of the unpacked build had nothing watching
// it. What it pins:
//   * a warning the engine reports is counted once, although it arrives both
//     through QQmlEngine::warnings and through the message handler;
//   * the engine's signal alone suffices when it prints nothing;
//   * a console.warn / console.error from QML counts, console.log does not;
//   * the two environment notices every run prints (no font directory, no
//     Direct3D 12) and the Qt Graphs fallback notice name no QML location and
//     never count.
// ---------------------------------------------------------------------------

#include "TestCollectorGuard.h"
#include "QmlWarningCounter.h"

#include <QGuiApplication>
#include <QQmlComponent>
#include <QQmlEngine>
#include <QRegularExpression>
#include <QStandardPaths>
#include <QTest>

#include <memory>

namespace {

mr::QmlWarningCounter *s_counter = nullptr;
QtMessageHandler s_previous = nullptr;

void forward(QtMsgType type, const QMessageLogContext &context, const QString &message)
{
    if (s_counter)
        s_counter->noteMessage(type, context, message);
    if (s_previous)
        s_previous(type, context, message);
}

/// Routes the message handler to \a counter for as long as it lives.
struct Routed
{
    explicit Routed(mr::QmlWarningCounter *counter)
    {
        s_counter = counter;
        s_previous = qInstallMessageHandler(forward);
    }
    ~Routed()
    {
        qInstallMessageHandler(s_previous);
        s_counter = nullptr;
        s_previous = nullptr;
    }
};

/// Creates \a qml as a component at a qrc: address, the way the shipping QML is loaded.
std::unique_ptr<QObject> create(QQmlEngine &engine, const char *qml)
{
    QQmlComponent component(&engine);
    component.setData(QByteArray(qml), QUrl(QStringLiteral("qrc:/mr-test/Probe.qml")));
    return std::unique_ptr<QObject>(component.create());
}

} // namespace

class QmlWarningCounterTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void aBindingThatThrowsIsCountedOnce()
    {
        mr::QmlWarningCounter counter;
        Routed routed(&counter);
        QQmlEngine engine;
        counter.watch(&engine);
        QTest::ignoreMessage(QtWarningMsg, QRegularExpression(QStringLiteral("ReferenceError")));
        const auto object = create(engine, "import QtQuick\nItem { property int probe: noSuchName.length }\n");
        QVERIFY(object);
        QCOMPARE(counter.count(), 1);
    }

    void anEngineThatPrintsNothingIsStillCounted()
    {
        mr::QmlWarningCounter counter;
        Routed routed(&counter);
        QQmlEngine engine;
        engine.setOutputWarningsToStandardError(false);
        counter.watch(&engine);
        const auto object = create(engine, "import QtQuick\nItem { property int probe: noSuchName.length }\n");
        QVERIFY(object);
        QCOMPARE(counter.count(), 1);
    }

    void aConsoleWarningFromQmlCountsAndALogLineDoesNot()
    {
        mr::QmlWarningCounter counter;
        Routed routed(&counter);
        QQmlEngine engine;
        counter.watch(&engine);
        QTest::ignoreMessage(QtDebugMsg, "fixture log line");
        auto object = create(engine, "import QtQuick\nItem { Component.onCompleted: console.log(\"fixture log line\") }\n");
        QVERIFY(object);
        QCOMPARE(counter.count(), 0);

        QTest::ignoreMessage(QtWarningMsg, "fixture warning");
        QTest::ignoreMessage(QtCriticalMsg, "fixture error");
        object = create(engine, "import QtQuick\nItem { Component.onCompleted: { console.warn(\"fixture warning\");"
                                " console.error(\"fixture error\") } }\n");
        QVERIFY(object);
        QCOMPARE(counter.count(), 2);
    }

    void aMessageCountsOnlyWhenItNamesAQmlLocation_data()
    {
        QTest::addColumn<int>("type");
        QTest::addColumn<QByteArray>("file");
        QTest::addColumn<QString>("message");
        QTest::addColumn<bool>("counted");

        QTest::newRow("qrc warning") << int(QtWarningMsg) << QByteArray()
            << QStringLiteral("qrc:/qt/qml/MentorRecorder/pages/DashboardPage.qml:80: TypeError: Cannot read property 'x' of null")
            << true;
        QTest::newRow("file warning") << int(QtWarningMsg) << QByteArray()
            << QStringLiteral("file:///D:/PRJ/src/Desktop/qml/Main.qml:12:5: Unable to assign [undefined] to int")
            << true;
        QTest::newRow("qrc critical") << int(QtCriticalMsg) << QByteArray()
            << QStringLiteral("qrc:/qt/qml/MentorRecorder/Main.qml:1: fixture") << true;
        QTest::newRow("console warning located by its context") << int(QtWarningMsg)
            << QByteArray("qrc:/qt/qml/MentorRecorder/Main.qml") << QStringLiteral("fixture") << true;
        QTest::newRow("qrc info") << int(QtInfoMsg) << QByteArray()
            << QStringLiteral("qrc:/qt/qml/MentorRecorder/Main.qml:1: fixture") << false;
        QTest::newRow("qrc debug") << int(QtDebugMsg) << QByteArray()
            << QStringLiteral("qrc:/qt/qml/MentorRecorder/Main.qml:1: fixture") << false;
        // The two notices every screenshot run prints, word for word.
        QTest::newRow("no font directory") << int(QtWarningMsg) << QByteArray()
            << QStringLiteral("QFontDatabase: Cannot find font directory D:/APPS/Qt/6.11.2/mingw_64/lib/fonts.\n"
                              "Note that Qt no longer ships fonts. Deploy some (from https://dejavu-fonts.github.io/ "
                              "for example) or switch to fontconfig.")
            << false;
        QTest::newRow("no Direct3D 12") << int(QtWarningMsg) << QByteArray()
            << QStringLiteral("Qt was built without Direct3D 12 support. This is likely due to having ancient SDK "
                              "headers (such as d3d12.h) in the Qt build environment.")
            << false;
        // It names the probe's address inside the text, not as the location of the message.
        QTest::newRow("graphs fallback") << int(QtWarningMsg) << QByteArray()
            << QStringLiteral("Qt Graphs unavailable, falling back to the plain bar chart: "
                              "qrc:/mr/graphs-probe.qml:2 module \"QtGraphs\" is not installed")
            << false;
        QTest::newRow("C++ source location") << int(QtWarningMsg)
            << QByteArray("D:/PRJ/src/Desktop/cpp/main.cpp") << QStringLiteral("fixture") << false;
    }

    void aMessageCountsOnlyWhenItNamesAQmlLocation()
    {
        QFETCH(int, type);
        QFETCH(QByteArray, file);
        QFETCH(QString, message);
        QFETCH(bool, counted);

        const QMessageLogContext context(file.isEmpty() ? nullptr : file.constData(), 1, nullptr, nullptr);
        QCOMPARE(mr::QmlWarningCounter::namesQmlLocation(QtMsgType(type), context, message), counted);
        mr::QmlWarningCounter counter;
        counter.noteMessage(QtMsgType(type), context, message);
        QCOMPARE(counter.count(), counted ? 1 : 0);
    }
};

int main(int argc, char **argv)
{
    // Never run against the user's own Collector, database or serve lease - see
    // TestCollectorGuard.h. Must precede any construction.
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QGuiApplication app(argc, argv);
    QmlWarningCounterTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "QmlWarningCounterTests.moc"
