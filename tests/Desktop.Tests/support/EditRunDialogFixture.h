// EditRunDialog, the three-step record wizard, alone in a window over a real AppController on
// the MockBackend: the scene EditRunDialogBindingTests drives the wizard on and NoteImageStoreTests
// its 备注图片 on. Everything is in an anonymous namespace: each suite gets its own copy inside
// the shared test executable.
#pragma once

#include "AppController.h"
#include "Formatters.h"
#include "JobCatalog.h"
#include "MockBackend.h"
#include "NoteImageStore.h"
#include "QmlComponentTestSupport.h"
#include "RoleCatalog.h"
#include "RunFormValidator.h"

#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickWindow>
#include <QSignalSpy>
#include <QTest>
#include <QVariantList>
#include <QVariantMap>

#include <memory>

namespace {

inline QVariantMap duty(int contentId, const QString &name)
{
    return {{QStringLiteral("content_id"), contentId}, {QStringLiteral("duty_name"), name}};
}

inline QVariantMap job(int jobId, const QString &name)
{
    return {{QStringLiteral("job_id"), jobId}, {QStringLiteral("job_name"), name}};
}

/// EditRunDialog alone in a 900 x 800 window over a real AppController on the MockBackend,
/// offering three duties (70, 71, 72) and three jobs (19, 21, 24) after 未知.
struct EditRunDialogFixture {
    mr::MockBackend backend;
    mr::AppController controller{&backend, nullptr};
    mr::Formatters formatters;
    mr::JobCatalog jobs;
    mr::RoleCatalog roles;
    mr::RunFormValidator validator;
    mr::NoteImageStore *imageStore = nullptr;
    QQmlEngine engine;
    std::unique_ptr<QObject> root;
    QString errors;
    /// Puts a history-row stand-in (a TapHandler) under the dialog and counts its taps.
    bool backgroundTapProbe = false;

    bool create(bool reduceMotion = true)
    {
        engine.rootContext()->setContextProperty(QStringLiteral("App"), &controller);
        engine.rootContext()->setContextProperty(QStringLiteral("Fmt"), &formatters);
        engine.rootContext()->setContextProperty(QStringLiteral("Jobs"), &jobs);
        engine.rootContext()->setContextProperty(QStringLiteral("Roles"), &roles);
        engine.rootContext()->setContextProperty(QStringLiteral("RunForm"), &validator);
        engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), reduceMotion);
        if (imageStore)
            engine.rootContext()->setContextProperty(QStringLiteral("NoteImages"), imageStore);
        QQmlComponent component(&engine);
        QByteArray source = R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 900; height: 800; visible: true
    property int backgroundTaps: 0
    PROBE
    EditRunDialog { objectName: "edit" }
})";
        source.replace("PROBE", backgroundTapProbe
            ? "Rectangle { anchors.fill: parent; TapHandler { onTapped: backgroundTaps += 1 } }"
            : "");
        component.setData(source, QUrl());
        root.reset(component.create());
        for (const auto &error : component.errors())
            errors += error.toString() + QLatin1Char('\n');
        if (!root)
            return false;

        auto *edit = dialog();
        edit->setProperty("dutyOptions", QVariantList{duty(70, QStringLiteral("伊库拉尔堡垒")),
                                                      duty(71, QStringLiteral("石卫塔")),
                                                      duty(72, QStringLiteral("铜铃铜山"))});
        edit->setProperty("jobOptions", QVariantList{job(19, QStringLiteral("骑士")),
                                                     job(21, QStringLiteral("战士")),
                                                     job(24, QStringLiteral("白魔法师"))});
        return true;
    }

    QObject *dialog() const { return root->findChild<QObject *>(QStringLiteral("edit")); }
    QQuickWindow *window() const { return qobject_cast<QQuickWindow *>(root.get()); }
    /// Repeater and ListView delegates are not QObject children of the dialog, so the lookup
    /// walks the popup's visual tree. A visible match wins over one that is on its way out.
    QQuickItem *item(const QString &name) const
    {
        const auto *content = dialog()->property("contentItem").value<QQuickItem *>();
        QQuickItem *hidden = nullptr;
        QList<QQuickItem *> pending;
        if (content)
            pending.append(const_cast<QQuickItem *>(content));
        while (!pending.isEmpty()) {
            QQuickItem *current = pending.takeFirst();
            if (current->objectName() == name) {
                if (current->isVisible())
                    return current;
                if (!hidden)
                    hidden = current;
            }
            pending.append(current->childItems());
        }
        return hidden ? hidden : dialog()->findChild<QQuickItem *>(name);
    }

    bool openForRun(const QVariantMap &value) const
    {
        return QMetaObject::invokeMethod(dialog(), "openForRun", Q_ARG(QVariant, QVariant(value)));
    }

    bool goToStep(int step) const
    {
        return QMetaObject::invokeMethod(dialog(), "goToStep", Q_ARG(QVariant, QVariant(step)));
    }

    int step() const { return dialog()->property("currentStep").toInt(); }

    /// Waits for the popup to be fully laid out, then clicks the centre of \a name the way a
    /// player does. Fails the test if the item is missing, hidden or outside the window.
    bool click(const QString &name) const
    {
        // Delegates a filter change released are deleted later; let that happen first so a
        // stale row cannot be the one that is found.
        QTest::qWait(30);
        QQuickItem *target = nullptr;
        if (!QTest::qWaitFor([&] {
                target = item(name);
                return target && target->isVisible() && target->width() > 0;
            }, 2000)) {
            qWarning() << "not clickable:" << name;
            return false;
        }
        // The step body scrolls; bring the target into its viewport first, as a player would.
        if (auto *body = item(QStringLiteral("wizardBody"))) {
            bool inside = false;
            for (auto *parent = target->parentItem(); parent; parent = parent->parentItem())
                inside = inside || parent == body;
            if (inside) {
                const QRectF rect = target->mapRectToItem(body, QRectF(0, 0, target->width(),
                                                                       target->height()));
                qreal contentY = body->property("contentY").toReal();
                if (rect.top() < 0)
                    contentY += rect.top() - 8;
                else if (rect.bottom() > body->height())
                    contentY += rect.bottom() - body->height() + 8;
                body->setProperty("contentY", contentY);
            }
        }
        // A ListView row or a freshly shown step may still be moving into place.
        QTest::qWait(30);
        const QPointF centre = target->mapToScene(QPointF(target->width() / 2,
                                                          target->height() / 2));
        if (centre.x() < 0 || centre.y() < 0 || centre.x() > window()->width()
            || centre.y() > window()->height()) {
            qWarning() << "outside the window:" << name << centre;
            return false;
        }
        QTest::mouseClick(window(), Qt::LeftButton, Qt::NoModifier, centre.toPoint());
        return true;
    }

    bool selected(const QString &name) const
    {
        const QQuickItem *target = item(name);
        return target && target->property("selected").toBool();
    }

    bool chosenRow(const QString &name) const
    {
        const QQuickItem *target = item(name);
        return target && target->property("chosen").toBool();
    }

    QVariantList visibleIds() const
    {
        QVariantList ids;
        for (const QVariant &row : mrtest::qmlList(dialog()->property("visibleDuties")))
            ids.append(mrtest::qmlMap(row).value(QStringLiteral("content_id")).toInt());
        return ids;
    }

    QString selectedDutyName() const
    {
        QVariant selected;
        QMetaObject::invokeMethod(dialog(), "selectedDutyName", Q_RETURN_ARG(QVariant, selected));
        return selected.toString();
    }

    /// Submits the form and returns what the one correction it sends changes; empty when it
    /// sends none or several. A correction that changes nothing is never sent
    /// (ERR_NO_CHANGES), so empty means "not exactly one correction".
    QVariantMap submitCorrection() const
    {
        QSignalSpy corrections(dialog(), SIGNAL(correctRequested(QVariant,QString)));
        QMetaObject::invokeMethod(dialog(), "submit");
        if (corrections.count() != 1) {
            qWarning() << "submit sent" << corrections.count() << "corrections, errorCode"
                       << dialog()->property("errorCode").toString();
            return {};
        }
        return mrtest::qmlMap(corrections.at(0).at(0));
    }
};

} // namespace
