// The main() of every area test executable: runs the one suite its first argument names.
// See DesktopTestRunner.h. The suite table is generated per executable by CMake from the same
// list that registers the CTest entries, so a listed suite without an entry fails to link.

#include "DesktopTestRunner.h"
#include "DesktopTestSuites.g.h"

#include <QByteArray>
#include <QtGlobal>

#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

namespace {

constexpr int UsageExitCode = 2;
constexpr const char *SuiteVariable = "MR_TEST_SUITE";

const mrtest::SuiteEntry *findSuite(const char *name)
{
    for (const auto &suite : mrtest::Suites) {
        if (std::strcmp(suite.name, name) == 0)
            return &suite;
    }
    return nullptr;
}

std::string fileName(const char *program)
{
    std::string name(program);
    const auto slash = name.find_last_of("/\\");
    if (slash != std::string::npos)
        name.erase(0, slash + 1);
    return name;
}

// The import suites copy this executable to ocr/local-ai-ocr.exe and start it as the offline
// OCR engine, with the engine's own arguments. Same test as the suites use: QFileInfo::baseName().
bool startedAsOcrEngine(const char *program)
{
    std::string name = fileName(program);
    const auto dot = name.find('.');
    if (dot != std::string::npos)
        name.erase(dot);
    return name == "local-ai-ocr";
}

int usage(const char *program)
{
    const std::string name = fileName(program);
    std::fprintf(stderr, "usage: %s <suite> [QtTest options]\n       %s --list\nsuites:", name.c_str(), name.c_str());
    for (const auto &suite : mrtest::Suites)
        std::fprintf(stderr, " %s", suite.name);
    std::fprintf(stderr, "\n");
    return UsageExitCode;
}

} // namespace

int main(int argc, char **argv)
{
    if (argc > 0 && startedAsOcrEngine(argv[0])) {
        // The engine copy inherits the variable the parent suite set below; it decides which
        // suite's engine stand-in answers. Its command line is the engine's and stays untouched.
        const QByteArray name = qgetenv(SuiteVariable);
        const auto *suite = findSuite(name.constData());
        if (!suite) {
            std::fprintf(stderr, "local-ai-ocr: %s names no suite of this executable\n", SuiteVariable);
            return UsageExitCode;
        }
        return suite->run(argc, argv);
    }

    if (argc < 2)
        return usage(argv[0]);
    if (std::strcmp(argv[1], "--list") == 0) {
        for (const auto &suite : mrtest::Suites)
            std::printf("%s\n", suite.name);
        return 0;
    }
    const auto *suite = findSuite(argv[1]);
    if (!suite) {
        std::fprintf(stderr, "unknown suite '%s'\n", argv[1]);
        return usage(argv[0]);
    }

    qputenv(SuiteVariable, suite->name);
    // The suite sees its usual command line: the program, then QtTest's options. The vector
    // outlives the suite's application object, which keeps a reference to argc and argv.
    std::vector<char *> arguments;
    arguments.reserve(static_cast<std::size_t>(argc));
    arguments.push_back(argv[0]);
    for (int index = 2; index < argc; ++index)
        arguments.push_back(argv[index]);
    arguments.push_back(nullptr);
    return suite->run(argc - 1, arguments.data());
}
