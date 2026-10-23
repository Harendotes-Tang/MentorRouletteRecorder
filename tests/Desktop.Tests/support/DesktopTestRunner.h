#pragma once

// An area test executable links several Desktop test suites and runs exactly one of them per
// process: the one its first argument names (DesktopTestMain.cpp). Each suite keeps its own
// application object, environment, fonts and exit codes, so CTest still registers, logs and
// times every suite on its own. Qt's batch-test support would do the same, but it is compiled
// out of the Qt this project builds against.

#include <QtTest/QTest>

// The entry function a suite's former main() becomes; the body stays as it was.
#define MR_DESKTOP_TEST_SUITE(Name) int mrDesktopSuite_##Name(int argc, char **argv)

// QTEST_MAIN / QTEST_GUILESS_MAIN as a suite entry, step for step. The application class is
// named here because an area executable that links Qt Widgets would otherwise turn
// QTEST_MAIN's QGuiApplication into a QApplication.
#define MR_DESKTOP_QTEST_SUITE(Name, TestObject, AppClass) \
    MR_DESKTOP_TEST_SUITE(Name) \
    { \
        QT_PREPEND_NAMESPACE(QTest::Internal::callInitMain)<TestObject>(); \
        AppClass app(argc, argv); \
        app.setAttribute(Qt::AA_Use96Dpi, true); \
        TestObject tc; \
        QTEST_SET_MAIN_SOURCE_PATH \
        return QTest::qExec(&tc, argc, argv); \
    }

namespace mrtest {

struct SuiteEntry
{
    const char *name;
    int (*run)(int argc, char **argv);
};

} // namespace mrtest
