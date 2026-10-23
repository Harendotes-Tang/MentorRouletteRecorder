#include "QmlRuntime.h"

#include <QCoreApplication>
#include <QDir>
#include <QQmlEngine>
#include <QUrl>
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
    // Existing source fixtures register shared controls themselves. Feature
    // folders retain the same public type names after physical regrouping.
    const QString root = QString::fromUtf8(MR_DESKTOP_QML_DIR);
    for (const QString &folder : {QStringLiteral("/overlay"), QStringLiteral("/calibration")}) {
        const QDir directory(root + folder);
        if (!directory.exists())
            qFatal("Feature QML test source directory is missing: %s", qPrintable(directory.path()));
        for (const QString &file : directory.entryList({QStringLiteral("*.qml")}, QDir::Files)) {
            const QByteArray name = file.chopped(4).toUtf8();
            qmlRegisterType(QUrl::fromLocalFile(directory.filePath(file)),
                            "MentorRecorder", 1, 0, name.constData());
        }
    }
}

} // namespace

// Linked only into test executables. Production uses the generated module
// registration and explicitly supplies its services before loading QML.
Q_COREAPP_STARTUP_FUNCTION(registerTestRuntime)
