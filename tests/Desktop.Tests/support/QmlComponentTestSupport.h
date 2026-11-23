#pragma once

#include <QByteArray>
#include <QDir>
#include <QJSValue>
#include <QList>
#include <QMetaType>
#include <QQmlComponent>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickWindow>
#include <QString>
#include <QUrl>
#include <QVariant>
#include <initializer_list>
#include <memory>

namespace mrtest {

inline QString qmlSourceRoot()
{
    return QString::fromUtf8(MR_DESKTOP_QML_DIR);
}

/// Registers Theme and every *.qml of \a folders (folders of src/Desktop/qml, such as
/// "components" or "dialogs") under MentorRecorder 1.0, from the source tree, the way
/// `import MentorRecorder` finds the shipping QML in the application. Each suite names the
/// folders it needs. Called explicitly by each suite before it creates an engine;
/// registration stays local to that suite's process and does not change application
/// startup. False when the source tree is not where the build said.
inline bool registerSourceQml(std::initializer_list<const char *> folders)
{
    const QString root = qmlSourceRoot();
    if (!QDir(root).exists())
        return false;
    qmlRegisterSingletonType(QUrl::fromLocalFile(root + QStringLiteral("/Theme.qml")),
                             "MentorRecorder", 1, 0, "Theme");
    for (const char *folder : folders) {
        const QDir directory(root + QLatin1Char('/') + QLatin1String(folder));
        const auto files = directory.entryList({QStringLiteral("*.qml")}, QDir::Files);
        for (const auto &file : files) {
            const QByteArray name = file.chopped(4).toUtf8();
            qmlRegisterType(QUrl::fromLocalFile(directory.filePath(file)),
                            "MentorRecorder", 1, 0, name.constData());
        }
    }
    return true;
}

/// Theme and the shared controls (components/) only.
inline bool registerSourceQmlComponents()
{
    return registerSourceQml({"components"});
}

/// Theme and every component, dialog, page and chart of the shipping QML.
inline bool registerShippingQml()
{
    return registerSourceQml({"components", "dialogs", "pages", "charts"});
}

/// The first item named \a name in the visual tree under \a root (\a root included),
/// depth first. Repeater delegates and Popup content hang off the visual tree, not
/// necessarily the QObject tree, so QObject::findChild() cannot see them. Null when
/// \a root is null or nothing matches.
inline QQuickItem *findVisualItem(QQuickItem *root, const QString &name)
{
    if (!root)
        return nullptr;
    if (root->objectName() == name)
        return root;
    const auto children = root->childItems();
    for (QQuickItem *child : children) {
        if (QQuickItem *found = findVisualItem(child, name))
            return found;
    }
    return nullptr;
}

/// Like findVisualItem(), but an invisible item hides its whole subtree: only an item
/// the player can see is found.
inline QQuickItem *findShownVisualItem(QQuickItem *root, const QString &name)
{
    if (!root || !root->isVisible())
        return nullptr;
    if (root->objectName() == name)
        return root;
    const auto children = root->childItems();
    for (QQuickItem *child : children) {
        if (QQuickItem *found = findShownVisualItem(child, name))
            return found;
    }
    return nullptr;
}

/// Every item named \a name in the visual tree under \a root, in creation order, which
/// for Repeater delegates is the model's order.
inline QList<QQuickItem *> findVisualItems(QQuickItem *root, const QString &name)
{
    QList<QQuickItem *> found;
    if (!root)
        return found;
    if (root->objectName() == name)
        found.append(root);
    const auto children = root->childItems();
    for (QQuickItem *child : children)
        found.append(findVisualItems(child, name));
    return found;
}

/// A QML `var` read through QObject::property() arrives either as a QJSValue or as a
/// plain list; either way, the list.
inline QVariantList qmlList(const QVariant &value)
{
    return value.metaType() == QMetaType::fromType<QJSValue>()
        ? value.value<QJSValue>().toVariant().toList() : value.toList();
}

/// A QML `var` read through QObject::property() arrives either as a QJSValue or as a
/// plain map; either way, the map.
inline QVariantMap qmlMap(const QVariant &value)
{
    return value.metaType() == QMetaType::fromType<QJSValue>()
        ? value.value<QJSValue>().toVariant().toMap() : value.toMap();
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
