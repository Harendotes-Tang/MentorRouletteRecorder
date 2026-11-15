// The scene the 动效 suites share: real QML from src/Desktop/qml, loaded from source the way
// UiWorkflowRegressionTests does, over a real AppController on a backend that answers {}.
// Everything is in an anonymous namespace: each suite gets its own copy inside the shared
// test executable.
#pragma once

#include "AppController.h"
#include "IBackend.h"

#include <QDir>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickWindow>
#include <QTimer>

#include <memory>

namespace {

class QuietBackend final : public mr::IBackend
{
public:
    QString backendName() const override { return QStringLiteral("mock"); }
    bool isConnected() const override { return true; }
    mr::BackendReply *request(const QString &type, const QJsonObject & = {}) override
    {
        auto *reply = new mr::BackendReply(QString::number(++m_serial), type, this);
        QTimer::singleShot(0, reply, [reply] { reply->succeed({}); });
        return reply;
    }

private:
    int m_serial = 0;
};

/// A QML scene over a real AppController, with ReduceMotion and the style pinned.
struct Scene {
    QuietBackend backend;
    mr::AppController controller{&backend, nullptr};
    QQmlEngine engine;
    std::unique_ptr<QObject> root;
    QString errors;

    bool create(const QByteArray &qml, bool reduceMotion, const QString &style = QString())
    {
        controller.setThemeMode(QStringLiteral("light"));
        auto *const context = engine.rootContext();
        context->setContextProperty(QStringLiteral("App"), &controller);
        context->setContextProperty(QStringLiteral("ReduceMotion"), reduceMotion);
        context->setContextProperty(QStringLiteral("ForceUiStyle"), style);
        QQmlComponent component(&engine);
        component.setData(qml, QUrl());
        root.reset(component.create());
        for (const auto &error : component.errors())
            errors += error.toString() + QLatin1Char('\n');
        return root != nullptr;
    }

    QQuickWindow *window() const { return qobject_cast<QQuickWindow *>(root.get()); }

    static QQuickItem *find(QQuickItem *from, const QString &name)
    {
        if (!from)
            return nullptr;
        if (from->objectName() == name)
            return from;
        const auto children = from->childItems();
        for (QQuickItem *child : children) {
            if (QQuickItem *match = find(child, name))
                return match;
        }
        return nullptr;
    }

    QQuickItem *item(const QString &name) const
    {
        return window() ? find(window()->contentItem(), name) : nullptr;
    }
};

/// Equal as far as a scale, an opacity or a pixel position can be told apart.
[[maybe_unused]] bool fuzzyEqual(qreal a, qreal b) { return qAbs(a - b) < 0.01; }

/// Registers Theme and every component, dialog, page and chart of the shipping QML under
/// the MentorRecorder module. False when the source tree is not where the build said.
[[maybe_unused]] bool registerShippingQml()
{
    const QString qmlRoot = QString::fromUtf8(MR_DESKTOP_QML_DIR);
    if (!QDir(qmlRoot).exists())
        return false;
    qmlRegisterSingletonType(QUrl::fromLocalFile(qmlRoot + QStringLiteral("/Theme.qml")),
                             "MentorRecorder", 1, 0, "Theme");
    for (const auto &directory : {QStringLiteral("/components"), QStringLiteral("/dialogs"),
                                  QStringLiteral("/pages"), QStringLiteral("/charts")}) {
        const auto files = QDir(qmlRoot + directory).entryList({QStringLiteral("*.qml")}, QDir::Files);
        for (const auto &file : files) {
            const QByteArray name = file.chopped(4).toUtf8();
            qmlRegisterType(QUrl::fromLocalFile(qmlRoot + directory + QLatin1Char('/') + file),
                            "MentorRecorder", 1, 0, name.constData());
        }
    }
    return true;
}

} // namespace
