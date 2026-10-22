#include "QmlRuntime.h"

#include <QCoreApplication>
#include <QQmlEngine>
#include <QtQml/qqml.h>

namespace {

QObject *createTestRuntime(QQmlEngine *engine, QJSEngine *)
{
    // Source-QML suites retain their existing context-property fixtures. The
    // adapter belongs to this engine; no service or singleton crosses suites.
    auto *runtime = mr::QmlRuntime::forEngine(engine);
    runtime->enableLegacyContextAdapter();
    return runtime;
}

void registerTestRuntime()
{
    qmlRegisterSingletonType<mr::QmlRuntime>(
        "MentorRecorder", 1, 0, "Runtime", createTestRuntime);
}

} // namespace

// Linked only into test executables. Production uses the generated module
// registration and explicitly supplies its services before loading QML.
Q_COREAPP_STARTUP_FUNCTION(registerTestRuntime)
