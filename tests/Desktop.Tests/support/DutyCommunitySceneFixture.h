#pragma once

#include "StatisticsSceneFixture.h"

#include <QJSValue>

namespace {
/// The visible Text under `parent` that shows exactly `line`.
inline QQuickItem *visibleText(QQuickItem *parent, const QString &line)
{
    if (!parent) return nullptr;
    if (parent->inherits("QQuickText") && parent->isVisible() && parent->property("text").toString() == line)
        return parent;
    for (auto *child : parent->childItems())
        if (auto *match = visibleText(child, line)) return match;
    return nullptr;
}

inline QVariantMap qmlMap(const QVariant &value)
{
    return value.metaType() == QMetaType::fromType<QJSValue>()
        ? value.value<QJSValue>().toVariant().toMap() : value.toMap();
}

/// The entry ids of a list of { content } rows.
inline QStringList rowIds(const QVariant &value)
{
    QStringList out;
    for (const QVariant &row : qmlList(value))
        out << row.toMap().value(QStringLiteral("content")).toMap().value(QStringLiteral("id")).toString();
    return out;
}

/// A published community entry, as the public library hands it to the dialog.
inline QVariantMap publicEntry(int number, const char *type, const char *body, const char *role, int contentId)
{
    const auto id = QStringLiteral("00000000-0000-4000-8000-%1").arg(number, 12, 10, QLatin1Char('0'));
    return {{QStringLiteral("content"), QVariantMap{
        {QStringLiteral("schema_version"), 1}, {QStringLiteral("id"), id},
        {QStringLiteral("type"), QString::fromLatin1(type)}, {QStringLiteral("content_id"), contentId},
        {QStringLiteral("region"), QStringLiteral("ALL")}, {QStringLiteral("language"), QStringLiteral("zh-Hans")},
        {QStringLiteral("title"), QStringLiteral("Entry %1").arg(number)}, {QStringLiteral("body"), QString::fromUtf8(body)},
        {QStringLiteral("boss"), QString()}, {QStringLiteral("phase"), QString()},
        {QStringLiteral("role"), QString::fromLatin1(role)}, {QStringLiteral("tags"), QVariantList()},
        {QStringLiteral("game_version"), QString()}, {QStringLiteral("links"), QVariantList()},
        {QStringLiteral("source"), QString()}, {QStringLiteral("status"), QStringLiteral("community")},
        {QStringLiteral("license"), QStringLiteral("CC-BY-4.0")}, {QStringLiteral("revision"), 1},
        {QStringLiteral("issue"), number}, {QStringLiteral("updated_at"), QStringLiteral("2026-11-23T10:00:00Z")},
        {QStringLiteral("author"), QVariantMap{{QStringLiteral("login"), QStringLiteral("first-author")},
                                               {QStringLiteral("id"), 300}}}}}};
}

/// The community dialog opened from the dashboard with its mock entries loaded.
inline QObject *openCommunityDialog(Scene &scene)
{
    if (!scene.create(QStringLiteral("harendotes"), true, true)) return nullptr;
    if (!QTest::qWaitForWindowExposed(scene.mainWindow())) return nullptr;
    if (!QTest::qWaitFor([&] { return scene.item(QStringLiteral("dashboardDutyCommunity"), true) != nullptr; }))
        return nullptr;
    if (!scene.click(QStringLiteral("dashboardDutyCommunity"))) return nullptr;
    auto *dialog = scene.root->findChild<QObject *>(QStringLiteral("dutyCommunityDialog"));
    // Both lists answered, so neither can overwrite rows a test sets afterwards.
    if (!dialog || !QTest::qWaitFor([&] { return dialog->property("visible").toBool()
                                                 && rowIds(dialog->property("localRows")).size() >= 5
                                                 && !dialog->property("libraryStatus").toString().isEmpty(); }))
        return nullptr;
    if (QObject *alert = scene.root->findChild<QObject *>(QStringLiteral("automaticRecordingAlert")))
        QMetaObject::invokeMethod(alert, "close");
    QMetaObject::invokeMethod(dialog, "resetFilters");
    QMetaObject::invokeMethod(dialog, "pickScope", Q_ARG(QVariant, QStringLiteral("all")));
    return dialog;
}
} // namespace
