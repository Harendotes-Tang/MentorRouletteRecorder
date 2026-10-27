#pragma once

#include <QByteArray>
#include <QDir>
#include <QQmlComponent>
#include <QQmlEngine>
#include <QQuickWindow>
#include <QString>
#include <QUrl>
#include <memory>

namespace mrtest {

inline QString qmlSourceRoot()
{
    return QString::fromUtf8(MR_DESKTOP_QML_DIR);
}

// Called explicitly by each suite before it creates an engine. Registration stays
// local to that suite's process; this does not change application startup.
inline void registerSourceQmlComponents()
{
    const QString root = qmlSourceRoot();
    qmlRegisterSingletonType(QUrl::fromLocalFile(root + QStringLiteral("/Theme.qml")),
                             "MentorRecorder", 1, 0, "Theme");
    const auto files = QDir(root + QStringLiteral("/components")).entryList({QStringLiteral("*.qml")}, QDir::Files);
    for (const auto &file : files) {
        const QByteArray name = file.chopped(4).toUtf8();
        qmlRegisterType(QUrl::fromLocalFile(root + QStringLiteral("/components/") + file),
                        "MentorRecorder", 1, 0, name.constData());
    }
}

// Owns one inline-QML root and its engine. Member order destroys the root first,
// so QML objects never outlive the engine that created them. Suites with model or
// backend dependencies keep their own scene and its explicit destruction order.
struct QmlComponentScene
{
    QQmlEngine engine;
    std::unique_ptr<QObject> root;
    QString errors;

    bool create(const QByteArray &qml, const QString &sourcePath)
    {
        QQmlComponent component(&engine);
        component.setData(qml, QUrl::fromLocalFile(qmlSourceRoot() + sourcePath));
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
};

} // namespace mrtest
