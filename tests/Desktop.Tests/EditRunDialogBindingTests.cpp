// EditRunDialog, the three-step record wizard.
//
// Every picker must keep following the dialog's own state after the user has picked
// something in it. A picker that loses its inline `currentIndex` binding on the first pick
// leaves the sequence
//
//     edit run A -> change 副本 -> 取消 -> edit run B
//
// naming run A's duty while dutyIndex, the diff table and the correction that would be sent
// all describe run B. The wizard's cards, rows and chips are pure views (PickSurface /
// PickChip never write `selected` / `checked` themselves), so these tests click them the way
// a player does - a real mouse click on the item in the scene.
//
// dutyIndex / jobIndex are indices into currentDutyOptions() / currentJobOptions(), where 0
// is 未知. The step 2 filters never change those lists.

#include "TestCollectorGuard.h"
#include "AppController.h"
#include "DutyCatalog.h"
#include "Formatters.h"
#include "JobCatalog.h"
#include "MockBackend.h"
#include "RoleCatalog.h"
#include "RunFormValidator.h"

#include <QDir>
#include <QDateTime>
#include <QGuiApplication>
#include <QJSValue>
#include <QJsonArray>
#include <QJsonObject>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QSet>
#include <QSignalSpy>
#include <QTest>
#include <QVariantList>
#include <QVariantMap>

#include <memory>

namespace {

QVariantMap duty(int contentId, const QString &name)
{
    return {{QStringLiteral("content_id"), contentId}, {QStringLiteral("duty_name"), name}};
}

/// A catalogue-shaped row, as DutyCatalog::allDuties() hands them out.
QVariantMap catalogueDuty(int contentId, const char *name, const char *category, int level,
                          int partySize, const char *difficulty, const char *version)
{
    return {{QStringLiteral("content_id"), contentId},
            {QStringLiteral("duty_name"), QString::fromUtf8(name)},
            {QStringLiteral("duty_category"), QString::fromUtf8(category)},
            {QStringLiteral("duty_level"), level},
            {QStringLiteral("party_size"), partySize},
            {QStringLiteral("difficulty"), QString::fromUtf8(difficulty)},
            {QStringLiteral("version"), QString::fromUtf8(version)}};
}

QVariantMap job(int jobId, const QString &name)
{
    return {{QStringLiteral("job_id"), jobId}, {QStringLiteral("job_name"), name}};
}

QVariantMap run(const QString &id, int contentId, int jobId, const QString &result)
{
    return {
        {QStringLiteral("run_id"), id},
        {QStringLiteral("revision"), 1},
        {QStringLiteral("matched_at_utc"), QStringLiteral("2026-09-04T12:39:05.125Z")},
        {QStringLiteral("entered_at_utc"), QStringLiteral("2026-09-04T12:41:00.000Z")},
        {QStringLiteral("ended_at_utc"), QStringLiteral("2026-09-04T12:59:40.422Z")},
        {QStringLiteral("content_id"), contentId},
        {QStringLiteral("job_id"), jobId},
        {QStringLiteral("result"), result},
        {QStringLiteral("contributes_to_goal"), true},
        {QStringLiteral("note"), QString()},
    };
}

QVariantMap asMap(const QVariant &value)
{
    return value.canConvert<QJSValue>() ? value.value<QJSValue>().toVariant().toMap()
                                        : value.toMap();
}

QVariantList asList(const QVariant &value)
{
    return value.canConvert<QJSValue>() ? value.value<QJSValue>().toVariant().toList()
                                        : value.toList();
}

QJsonObject runRow(const QJsonValue &contentId, bool softDeleted = false)
{
    QJsonObject row;
    row.insert(QStringLiteral("content_id"), contentId);
    row.insert(QStringLiteral("soft_deleted"), softDeleted);
    return row;
}

struct DialogFixture {
    mr::MockBackend backend;
    mr::AppController controller{&backend, nullptr};
    mr::Formatters formatters;
    mr::JobCatalog jobs;
    mr::RoleCatalog roles;
    mr::RunFormValidator validator;
    QQmlEngine engine;
    std::unique_ptr<QObject> root;
    QString errors;

    bool create(bool reduceMotion = true)
    {
        engine.rootContext()->setContextProperty(QStringLiteral("App"), &controller);
        engine.rootContext()->setContextProperty(QStringLiteral("Fmt"), &formatters);
        engine.rootContext()->setContextProperty(QStringLiteral("Jobs"), &jobs);
        engine.rootContext()->setContextProperty(QStringLiteral("Roles"), &roles);
        engine.rootContext()->setContextProperty(QStringLiteral("RunForm"), &validator);
        engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), reduceMotion);
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 900; height: 800; visible: true
    EditRunDialog { objectName: "edit" }
})", QUrl());
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

    bool openForRun(const QVariantMap &value)
    {
        return QMetaObject::invokeMethod(dialog(), "openForRun", Q_ARG(QVariant, QVariant(value)));
    }

    bool goToStep(int step)
    {
        return QMetaObject::invokeMethod(dialog(), "goToStep", Q_ARG(QVariant, QVariant(step)));
    }

    int step() const { return dialog()->property("currentStep").toInt(); }

    /// Waits for the popup to be fully laid out, then clicks the centre of \a name the way a
    /// player does. Fails the test if the item is missing, hidden or outside the window.
    bool click(const QString &name)
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
        for (const QVariant &row : asList(dialog()->property("visibleDuties")))
            ids.append(asMap(row).value(QStringLiteral("content_id")).toInt());
        return ids;
    }

    QString selectedDutyName() const
    {
        QVariant selected;
        QMetaObject::invokeMethod(dialog(), "selectedDutyName", Q_RETURN_ARG(QVariant, selected));
        return selected.toString();
    }
};

QVariantList ids(std::initializer_list<int> values)
{
    QVariantList out;
    for (int value : values)
        out.append(value);
    return out;
}

} // namespace

class EditRunDialogBindingTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void initTestCase()
    {
        const QString qmlRoot = QString::fromUtf8(MR_DESKTOP_QML_DIR);
        QVERIFY(QDir(qmlRoot).exists());
        qmlRegisterSingletonType(QUrl::fromLocalFile(qmlRoot + QStringLiteral("/Theme.qml")),
                                 "MentorRecorder", 1, 0, "Theme");
        for (const auto &directory : {QStringLiteral("/components"), QStringLiteral("/dialogs")}) {
            for (const auto &file :
                 QDir(qmlRoot + directory).entryList({QStringLiteral("*.qml")}, QDir::Files)) {
                const QByteArray name = file.chopped(4).toUtf8();
                qmlRegisterType(QUrl::fromLocalFile(qmlRoot + directory + QLatin1Char('/') + file),
                                "MentorRecorder", 1, 0, name.constData());
            }
        }
    }

    void everyPickerFollowsTheRunTheDialogWasReopenedFor()
    {
        DialogFixture fixture;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));

        // Index 0 of the duty and job lists is the injected "unknown" row, so content_id 70
        // and job_id 19 are both index 1.
        QVERIFY(fixture.openForRun(run(QStringLiteral("run-a"), 70, 19,
                                       QStringLiteral("CANCELLED_BEFORE_ENTRY"))));
        QCOMPARE(fixture.step(), 1);
        QCOMPARE(fixture.dialog()->property("dutyIndex").toInt(), 1);
        QCOMPARE(fixture.dialog()->property("jobIndex").toInt(), 1);
        QVERIFY(fixture.selected(QStringLiteral("resultPick_CANCELLED_BEFORE_ENTRY")));
        QVERIFY(!fixture.selected(QStringLiteral("resultPick_COMPLETED")));

        // The user changes all three, then cancels: nothing is sent, and the dialog's own
        // state is thrown away when it is opened for the next run. Each picked value differs
        // both from the run being edited and from the run opened next, so a picker that kept
        // the picked value cannot accidentally agree with the assertions below.
        QVERIFY(fixture.click(QStringLiteral("resultPick_INTERRUPTED")));
        QVERIFY(fixture.click(QStringLiteral("nextStepButton")));
        QCOMPARE(fixture.step(), 2);
        QTRY_VERIFY(fixture.chosenRow(QStringLiteral("dutyRow_70")));
        QVERIFY(fixture.selected(QStringLiteral("jobPick_19")));
        QVERIFY(fixture.click(QStringLiteral("dutyRow_71")));
        QVERIFY(fixture.click(QStringLiteral("jobPick_21")));
        QCOMPARE(fixture.dialog()->property("dutyIndex").toInt(), 2);
        QCOMPARE(fixture.dialog()->property("jobIndex").toInt(), 2);
        QCOMPARE(fixture.dialog()->property("resultCode").toString(),
                 QStringLiteral("INTERRUPTED"));
        QVERIFY(fixture.chosenRow(QStringLiteral("dutyRow_71")));
        QVERIFY(!fixture.chosenRow(QStringLiteral("dutyRow_70")));
        QVERIFY(fixture.selected(QStringLiteral("jobPick_21")));
        QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "close"));

        // Reopened for a different run: every picker shows run B, on step 1 again.
        QVERIFY(fixture.openForRun(run(QStringLiteral("run-b"), 72, 24,
                                       QStringLiteral("COMPLETED"))));
        QCOMPARE(fixture.step(), 1);
        QCOMPARE(fixture.dialog()->property("dutyIndex").toInt(), 3);
        QCOMPARE(fixture.dialog()->property("jobIndex").toInt(), 3);
        QCOMPARE(fixture.dialog()->property("resultCode").toString(), QStringLiteral("COMPLETED"));
        QVERIFY(fixture.selected(QStringLiteral("resultPick_COMPLETED")));
        QVERIFY(!fixture.selected(QStringLiteral("resultPick_INTERRUPTED")));
        QVERIFY(fixture.goToStep(2));
        QTRY_VERIFY(fixture.chosenRow(QStringLiteral("dutyRow_72")));
        QVERIFY(!fixture.chosenRow(QStringLiteral("dutyRow_71")));
        QVERIFY(fixture.selected(QStringLiteral("jobPick_24")));
        QVERIFY(!fixture.selected(QStringLiteral("jobPick_21")));
        QVERIFY(!fixture.item(QStringLiteral("jobUnknownChip"))->property("checked").toBool());

        // 未知 is index 0 for both lists.
        QVERIFY(fixture.click(QStringLiteral("jobUnknownChip")));
        QCOMPARE(fixture.dialog()->property("jobIndex").toInt(), 0);
        QVERIFY(fixture.item(QStringLiteral("jobUnknownChip"))->property("checked").toBool());
        QVERIFY(fixture.click(QStringLiteral("changeDutyButton")));
        QCOMPARE(fixture.dialog()->property("dutyIndex").toInt(), 0);
        QCOMPARE(fixture.selectedDutyName(), QStringLiteral("未知副本"));
    }

    void aPickedValueStillReachesTheFormTheDialogWillSubmit()
    {
        // The pickers must not fight the user either: after a pick, the row stays chosen and
        // the dialog reports the matching name to the validator.
        DialogFixture fixture;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));
        QVERIFY(fixture.openForRun(run(QStringLiteral("run-a"), 70, 19,
                                       QStringLiteral("CANCELLED_BEFORE_ENTRY"))));
        QVERIFY(fixture.goToStep(2));

        QVERIFY(fixture.click(QStringLiteral("dutyRow_71")));
        QVERIFY(fixture.chosenRow(QStringLiteral("dutyRow_71")));
        QCOMPARE(fixture.dialog()->property("dutyIndex").toInt(), 2);
        QCOMPARE(fixture.selectedDutyName(), QStringLiteral("石卫塔"));
        QCOMPARE(asMap(fixture.dialog()->property("formState")).value(QStringLiteral("duty_name")).toString(),
                 QStringLiteral("石卫塔"));
        QVERIFY(fixture.item(QStringLiteral("selectedDutyTag"))->isVisible());
    }

    void resultCorrectionPreservesAnIdentifiedDutyWithoutACatalogueMatch_data()
    {
        QTest::addColumn<QVariant>("contentId");
        QTest::newRow("territory only") << QVariant();
        QTest::newRow("retired content") << QVariant(999999);
    }

    void resultCorrectionPreservesAnIdentifiedDutyWithoutACatalogueMatch()
    {
        QFETCH(QVariant, contentId);
        DialogFixture fixture;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));
        auto value = run(QStringLiteral("territory-run"), 70, 19, QStringLiteral("UNKNOWN"));
        value.insert(QStringLiteral("content_id"), contentId);
        value.insert(QStringLiteral("territory_id"), 123);
        value.insert(QStringLiteral("duty_name"), QStringLiteral("区域识别的副本"));
        value.insert(QStringLiteral("duty_category"), QStringLiteral("四人迷宫"));
        QVERIFY(fixture.openForRun(value));
        // The run's own duty is option 1, listed first and chosen.
        QCOMPARE(fixture.dialog()->property("dutyIndex").toInt(), 1);
        QCOMPARE(fixture.selectedDutyName(), QStringLiteral("区域识别的副本"));
        QVERIFY(fixture.goToStep(2));
        QTRY_VERIFY(fixture.chosenRow(QStringLiteral("dutyRow_run")));
        QCOMPARE(fixture.visibleIds().size(), 4);
        QSignalSpy corrections(fixture.dialog(), SIGNAL(correctRequested(QVariant,QString)));
        QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "markCompleted"));
        QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "submit"));
        QCOMPARE(corrections.count(), 1);
        const auto changes = asMap(corrections.at(0).at(0));
        QCOMPARE(changes.value(QStringLiteral("result")).toString(), QStringLiteral("COMPLETED"));
        for (const auto *key : {"content_id", "territory_id", "duty_name", "duty_category"})
            QVERIFY2(!changes.contains(QString::fromLatin1(key)), key);

        // Choosing a different duty still submits its actual id and name.
        QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "acceptSubmission"));
        QVERIFY(fixture.openForRun(value));
        QVERIFY(fixture.goToStep(2));
        QVERIFY(fixture.click(QStringLiteral("dutyRow_71")));
        QCOMPARE(fixture.dialog()->property("dutyIndex").toInt(), 3);
        QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "submit"));
        QCOMPARE(corrections.count(), 2);
        const auto replacement = asMap(corrections.at(1).at(0));
        QCOMPARE(replacement.value(QStringLiteral("content_id")).toInt(), 71);
        QCOMPARE(replacement.value(QStringLiteral("duty_name")).toString(), QStringLiteral("石卫塔"));
    }

    void progressPreviewCountsOnlyCompletedContributingRuns()
    {
        DialogFixture fixture;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));
        QVERIFY(fixture.openForRun(run(QStringLiteral("cancelled"), 70, 19,
                                       QStringLiteral("CANCELLED_BEFORE_ENTRY"))));
        QCOMPARE(fixture.dialog()->property("progressDelta").toInt(), 0);
        QVERIFY(fixture.dialog()->property("progressExplanation").toString().contains(QStringLiteral("进本前取消")));
        QVERIFY(fixture.item(QStringLiteral("markCompletedButton"))->isVisible());
        QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "markCompleted"));
        QCOMPARE(fixture.dialog()->property("progressDelta").toInt(), 1);
        QVERIFY(!fixture.item(QStringLiteral("markCompletedButton"))->isVisible());
        QCOMPARE(fixture.item(QStringLiteral("progressPreview"))->property("text").toString(),
                 QStringLiteral("保存后，成就进度 +1"));
        // The goal switch is the player's way to take it back.
        QVERIFY(fixture.click(QStringLiteral("goalToggle")));
        QVERIFY(!fixture.dialog()->property("contributesToGoal").toBool());
        QCOMPARE(fixture.dialog()->property("progressDelta").toInt(), 0);

        QVERIFY(fixture.openForRun(run(QStringLiteral("completed"), 70, 19,
                                       QStringLiteral("COMPLETED"))));
        QCOMPARE(fixture.dialog()->property("progressDelta").toInt(), 0);
        fixture.dialog()->setProperty("contributesToGoal", false);
        QCOMPARE(fixture.dialog()->property("progressDelta").toInt(), -1);

        auto deleted = run(QStringLiteral("deleted"), 70, 19, QStringLiteral("COMPLETED"));
        deleted.insert(QStringLiteral("soft_deleted"), true);
        QVERIFY(fixture.openForRun(deleted));
        QCOMPARE(fixture.dialog()->property("progressDelta").toInt(), 0);
        QVERIFY(fixture.dialog()->property("progressExplanation").toString().contains(QStringLiteral("先恢复")));
    }

    void completingAMissingEntryRequiresAnExplicitEstimateAndKeepsDurationUnknown()
    {
        DialogFixture fixture;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));
        auto value = run(QStringLiteral("missing-entry"), 70, 19,
                         QStringLiteral("CANCELLED_BEFORE_ENTRY"));
        value.insert(QStringLiteral("entered_at_utc"), QVariant());
        value.insert(QStringLiteral("duration_ms"), QVariant());
        value.insert(QStringLiteral("note"), QStringLiteral("保留原来的备注"));
        QVERIFY(fixture.openForRun(value));
        QSignalSpy corrections(fixture.dialog(), SIGNAL(correctRequested(QVariant,QString)));
        QVERIFY(corrections.isValid());
        QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "markCompleted"));
        QVERIFY(fixture.dialog()->property("enteredTime").toString().isEmpty());
        QVERIFY(fixture.dialog()->property("showTimeDetails").toBool());
        QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "submit"));
        QCOMPARE(corrections.count(), 0);
        QCOMPARE(fixture.dialog()->property("errorCode").toString(), QStringLiteral("ERR_BAD_REQUEST"));
        // The refusal is about a time, which is on step 3; so is the estimate.
        QCOMPARE(fixture.step(), 3);
        QTRY_VERIFY(fixture.item(QStringLiteral("errorBanner"))->isVisible());

        QVERIFY(fixture.click(QStringLiteral("estimateEntryButton")));
        QVERIFY(fixture.dialog()->property("estimatedEntry").toBool());
        QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "submit"));
        QCOMPARE(corrections.count(), 1);
        const auto changes = asMap(corrections.at(0).at(0));
        QCOMPARE(changes.value(QStringLiteral("result")).toString(), QStringLiteral("COMPLETED"));
        QCOMPARE(changes.value(QStringLiteral("entered_at_utc")).toString(),
                 value.value(QStringLiteral("matched_at_utc")).toString());
        QVERIFY(changes.contains(QStringLiteral("duration_ms")));
        QVERIFY(changes.value(QStringLiteral("duration_ms")).isNull());
        QVERIFY(changes.value(QStringLiteral("note")).toString().contains(QStringLiteral("保留原来的备注")));
        QVERIFY(changes.value(QStringLiteral("note")).toString().contains(QStringLiteral("估算")));
        QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "submit"));
        QCOMPARE(corrections.count(), 1);

        QVERIFY(fixture.openForRun(value));
        QVERIFY(!fixture.dialog()->property("estimatedEntry").toBool());
        QVERIFY(fixture.dialog()->property("enteredTime").toString().isEmpty());
        QCOMPARE(fixture.step(), 1);
    }

    void creatingAnEstimatedRunSendsAnExplicitUnknownDuration()
    {
        DialogFixture fixture;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));
        QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "openForCreate"));
        fixture.dialog()->setProperty("matchedDate", "2026-09-13");
        fixture.dialog()->setProperty("matchedTime", "20:00:00");
        fixture.dialog()->setProperty("endedDate", "2026-09-13");
        fixture.dialog()->setProperty("endedTime", "20:30:00");
        QSignalSpy creations(fixture.dialog(), SIGNAL(createRequested(QVariant,QString)));
        QVERIFY(creations.isValid());
        QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "estimateEntryFromMatch"));
        QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "submit"));
        QCOMPARE(creations.count(), 1);
        const auto fields = asMap(creations.at(0).at(0));
        QVERIFY(fields.contains(QStringLiteral("duration_ms")));
        QVERIFY(fields.value(QStringLiteral("duration_ms")).isNull());
        QCOMPARE(fields.value(QStringLiteral("entered_at_utc")), fields.value(QStringLiteral("matched_at_utc")));
    }

    void reopeningAnUnknownDurationPreservesItUntilTheEntryIsReplaced_data()
    {
        QTest::addColumn<bool>("replaceEntry");
        QTest::newRow("only correct the end") << false;
        QTest::newRow("supply the actual entry") << true;
    }

    void reopeningAnUnknownDurationPreservesItUntilTheEntryIsReplaced()
    {
        QFETCH(bool, replaceEntry);
        DialogFixture fixture;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));
        auto value = run(QStringLiteral("estimated"), 70, 19, QStringLiteral("COMPLETED"));
        value.insert(QStringLiteral("entered_at_utc"), value.value(QStringLiteral("matched_at_utc")));
        value.insert(QStringLiteral("duration_ms"), QVariant());
        // Unknown duration is a stored fact; its preservation must not depend on editable note text.
        value.insert(QStringLiteral("note"), QStringLiteral("备注已修改"));
        QVERIFY(fixture.openForRun(value));
        const auto originalEntry = QDateTime::fromString(value.value(QStringLiteral("entered_at_utc")).toString(), Qt::ISODateWithMs);
        const auto end = QDateTime::fromString(value.value(QStringLiteral("ended_at_utc")).toString(), Qt::ISODateWithMs).addSecs(60);
        fixture.dialog()->setProperty("endedDate", end.toLocalTime().toString(QStringLiteral("yyyy-MM-dd")));
        fixture.dialog()->setProperty("endedTime", end.toLocalTime().toString(QStringLiteral("HH:mm:ss.zzz")));
        const auto actualEntry = originalEntry.addSecs(120);
        if (replaceEntry) {
            fixture.dialog()->setProperty("enteredDate", actualEntry.toLocalTime().toString(QStringLiteral("yyyy-MM-dd")));
            fixture.dialog()->setProperty("enteredTime", actualEntry.toLocalTime().toString(QStringLiteral("HH:mm:ss.zzz")));
        }
        QSignalSpy corrections(fixture.dialog(), SIGNAL(correctRequested(QVariant,QString)));
        QVERIFY(corrections.isValid());
        QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "submit"));
        QCOMPARE(corrections.count(), 1);
        const auto changes = asMap(corrections.at(0).at(0));
        QVERIFY(changes.contains(QStringLiteral("duration_ms")));
        if (replaceEntry)
            QCOMPARE(changes.value(QStringLiteral("duration_ms")).toLongLong(), actualEntry.msecsTo(end));
        else
            QVERIFY(changes.value(QStringLiteral("duration_ms")).isNull());
    }

    void theWizardFitsASmallWindowWithItsButtonsInView()
    {
        // Plan §5: min(720, window - 40) x min(680, window - 32), and the action row stays
        // visible whatever the step body holds - the body scrolls instead.
        DialogFixture fixture;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));
        fixture.root->setProperty("width", 700);
        fixture.root->setProperty("height", 600);
        auto value = run(QStringLiteral("small-window"), 70, 19,
                         QStringLiteral("CANCELLED_BEFORE_ENTRY"));
        value.insert(QStringLiteral("entered_at_utc"), QVariant());
        QVERIFY(fixture.openForRun(value));
        QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "markCompleted"));
        QVERIFY(fixture.goToStep(3));
        QTest::qWait(50);

        const qreal width = fixture.dialog()->property("width").toReal();
        const qreal height = fixture.dialog()->property("height").toReal();
        QCOMPARE(width, 660.0);
        QCOMPARE(height, 568.0);
        const qreal x = fixture.dialog()->property("x").toReal();
        const qreal y = fixture.dialog()->property("y").toReal();
        QVERIFY(x >= 0 && y >= 0);
        QVERIFY(x + width <= 700 && y + height <= 600);

        for (const auto *name : {"saveRunButton", "prevStepButton", "estimateEntryButton"}) {
            auto *button = fixture.item(QString::fromLatin1(name));
            if (!button)
                QFAIL(name);
            QVERIFY2(button->isVisible(), name);
            const QRectF rect = button->mapRectToScene(QRectF(0, 0, button->width(), button->height()));
            QVERIFY2(rect.left() >= x && rect.right() <= x + width, name);
            QVERIFY2(rect.top() >= y && rect.bottom() <= y + height, name);
            QVERIFY2(rect.right() <= 700 && rect.bottom() <= 600, name);
        }
        QVERIFY(!fixture.item(QStringLiteral("nextStepButton"))->isVisible());

        // Step 2 is the tallest body; the buttons are still where they were.
        QVERIFY(fixture.goToStep(2));
        QTest::qWait(50);
        auto *next = fixture.item(QStringLiteral("nextStepButton"));
        QVERIFY(next->isVisible());
        const QRectF rect = next->mapRectToScene(QRectF(0, 0, next->width(), next->height()));
        QVERIFY(rect.bottom() <= y + height && rect.bottom() <= 600);
    }

    void stepsNavigateAndARefusalBringsUpTheTimes()
    {
        DialogFixture fixture;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));
        QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "openForCreate"));
        QCOMPARE(fixture.step(), 1);
        QVERIFY(fixture.item(QStringLiteral("wizardStepBar")));
        QVERIFY(fixture.item(QStringLiteral("nextStepButton"))->isVisible());
        QVERIFY(!fixture.item(QStringLiteral("prevStepButton"))->isVisible());
        QVERIFY(!fixture.item(QStringLiteral("saveRunButton"))->isVisible());
        QVERIFY(fixture.item(QStringLiteral("wizardStep1"))->isVisible());

        QVERIFY(fixture.click(QStringLiteral("nextStepButton")));
        QCOMPARE(fixture.step(), 2);
        QVERIFY(fixture.item(QStringLiteral("wizardStep2"))->isVisible());
        QVERIFY(!fixture.item(QStringLiteral("wizardStep1"))->isVisible());
        QVERIFY(fixture.item(QStringLiteral("prevStepButton"))->isVisible());
        QVERIFY(fixture.click(QStringLiteral("nextStepButton")));
        QCOMPARE(fixture.step(), 3);
        QVERIFY(!fixture.item(QStringLiteral("nextStepButton"))->isVisible());
        QVERIFY(fixture.item(QStringLiteral("saveRunButton"))->isVisible());
        QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "nextStep"));
        QCOMPARE(fixture.step(), 3);
        QVERIFY(fixture.click(QStringLiteral("prevStepButton")));
        QCOMPARE(fixture.step(), 2);
        // The step bar is clickable too.
        QVERIFY(fixture.click(QStringLiteral("wizardStep_1")));
        QCOMPARE(fixture.step(), 1);
        QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "prevStep"));
        QCOMPARE(fixture.step(), 1);
        QVERIFY(fixture.item(QStringLiteral("wizardStepLabel"))->property("text").toString()
                    .contains(QStringLiteral("1 / 3")));

        // Submitting from step 1 with a missing match time: the field is on step 3.
        QSignalSpy creations(fixture.dialog(), SIGNAL(createRequested(QVariant,QString)));
        QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "submit"));
        QCOMPARE(creations.count(), 0);
        QCOMPARE(fixture.dialog()->property("errorCode").toString(), QStringLiteral("ERR_BAD_REQUEST"));
        QCOMPARE(fixture.step(), 3);
        QTRY_VERIFY(fixture.item(QStringLiteral("errorBanner"))->isVisible());

        // So is the reason.
        QVERIFY(fixture.goToStep(2));
        fixture.dialog()->setProperty("matchedTime", "20:00:00");
        fixture.dialog()->setProperty("enteredTime", "20:01:00");
        fixture.dialog()->setProperty("endedTime", "20:20:00");
        fixture.dialog()->setProperty("reasonText", "");
        QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "submit"));
        QCOMPARE(fixture.dialog()->property("errorCode").toString(),
                 QStringLiteral("ERR_REASON_REQUIRED"));
        QCOMPARE(fixture.step(), 3);

        // A refusal from the Collector lands on the same banner.
        fixture.dialog()->setProperty("reasonText", "missed while offline");
        QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "submit"));
        QCOMPARE(creations.count(), 1);
        QVERIFY(fixture.dialog()->property("submitting").toBool());
        QVERIFY(fixture.goToStep(1));
        fixture.dialog()->setProperty("externalErrorText", "ERR_TIME_ORDER (ERR_TIME_ORDER)");
        QCOMPARE(fixture.step(), 3);
        QVERIFY(!fixture.dialog()->property("submitting").toBool());
        QCOMPARE(fixture.dialog()->property("errorText").toString(),
                 QStringLiteral("ERR_TIME_ORDER (ERR_TIME_ORDER)"));
    }

    void changingTheDayMovesOnlyTheTimesStillOnIt()
    {
        DialogFixture fixture;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));
        QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "openForCreate"));
        fixture.dialog()->setProperty("matchedDate", "2026-09-07");
        fixture.dialog()->setProperty("enteredDate", "2026-09-07");
        fixture.dialog()->setProperty("endedDate", "2026-09-08");
        QVERIFY(fixture.dialog()->property("dayFieldsVisible").toBool());
        QVERIFY(fixture.goToStep(3));
        auto *field = fixture.item(QStringLiteral("matchedDateField"));
        QVERIFY(field);
        field->setProperty("text", "2026-09-06");
        QCOMPARE(fixture.dialog()->property("matchedDate").toString(), QStringLiteral("2026-09-06"));
        QCOMPARE(fixture.dialog()->property("enteredDate").toString(), QStringLiteral("2026-09-06"));
        QCOMPARE(fixture.dialog()->property("endedDate").toString(), QStringLiteral("2026-09-08"));
        QVERIFY(fixture.item(QStringLiteral("endedDateField"))->isVisible());

        // An edit of a run without an entry time starts that entry on the match day.
        auto value = run(QStringLiteral("no-entry"), 70, 19, QStringLiteral("CANCELLED_BEFORE_ENTRY"));
        value.insert(QStringLiteral("duty_name"), QStringLiteral("伊库拉尔堡垒"));
        value.insert(QStringLiteral("job_name"), QStringLiteral("骑士"));
        value.insert(QStringLiteral("entered_at_utc"), QVariant());
        value.insert(QStringLiteral("ended_at_utc"), QVariant());
        QVERIFY(fixture.openForRun(value));
        const QString day = fixture.dialog()->property("matchedDate").toString();
        QCOMPARE(fixture.dialog()->property("enteredDate").toString(), day);
        QCOMPARE(fixture.dialog()->property("endedDate").toString(), day);
        QVERIFY(!fixture.dialog()->property("dayFieldsVisible").toBool());
        QVERIFY(asList(fixture.dialog()->property("diffRows")).isEmpty());
    }

    void dutyFiltersNarrowByPartyLevelDifficultyAndSearch()
    {
        DialogFixture fixture;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));
        fixture.dialog()->setProperty("dutyOptions", QVariantList{
            catalogueDuty(1, "天然要害沙斯塔夏溶洞", "四人迷宫", 15, 4, "普通", "2.x"),
            catalogueDuty(2, "伊弗利特歼殛战", "讨伐歼灭战", 50, 8, "极", "2.x"),
            catalogueDuty(3, "阿卡狄亚零式登天斗技场 轻量级1", "大型任务", 100, 8, "零式", "7.x"),
            catalogueDuty(4, "朱诺：第一巡行", "大型任务", 100, 24, "普通", "7.x"),
            catalogueDuty(5, "完成集团战训练！", "行会令", 10, 0, "普通", "2.x"),
            // A category a roulette never hands out: never listed.
            catalogueDuty(6, "金碟游乐场", "金碟游乐场", 15, 0, "普通", "2.x"),
            // No metadata at all: listed while every filter is 全部, and still selectable.
            duty(7, QStringLiteral("裸行")),
        });
        QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "openForCreate"));
        QVERIFY(fixture.goToStep(2));
        QCOMPARE(fixture.visibleIds(), ids({5, 1, 2, 3, 4, 7}));
        QTRY_COMPARE(fixture.item(QStringLiteral("dutyCount"))->property("text").toString(),
                     QStringLiteral("共 6 个副本"));

        // 人数, through the segmented control itself.
        auto *party = fixture.item(QStringLiteral("partyFilter"));
        QVERIFY(party);
        const QList<QPair<QString, QVariantList>> parties = {
            {QStringLiteral("4"), ids({1})},
            {QStringLiteral("8"), ids({2, 3})},
            {QStringLiteral("24"), ids({4})},
            {QStringLiteral("0"), ids({5})},
        };
        for (const auto &[value, expected] : parties) {
            QVERIFY(QMetaObject::invokeMethod(party, "activated", Q_ARG(QVariant, QVariant(value))));
            QCOMPARE(fixture.dialog()->property("partyFilter").toString(), value);
            QCOMPARE(fixture.visibleIds(), expected);
        }

        // 难度 only shows for 全部 / 8 / 24, and a hidden row does not filter.
        fixture.dialog()->setProperty("difficultyFilter", QString::fromUtf8("极"));
        QVERIFY(!fixture.dialog()->property("showDifficultyFilter").toBool());
        QCOMPARE(fixture.visibleIds(), ids({5}));
        QTRY_VERIFY(!fixture.item(QStringLiteral("difficultyChip_all")));
        QVERIFY(QMetaObject::invokeMethod(party, "activated", Q_ARG(QVariant, QVariant(QString()))));
        QVERIFY(fixture.dialog()->property("showDifficultyFilter").toBool());
        QCOMPARE(fixture.visibleIds(), ids({2}));
        QVERIFY(fixture.click(QStringLiteral(u"difficultyChip_零式")));
        QCOMPARE(fixture.visibleIds(), ids({3}));
        QVERIFY(fixture.click(QStringLiteral(u"difficultyChip_普通")));
        QCOMPARE(fixture.visibleIds(), ids({5, 1, 4}));
        QVERIFY(fixture.click(QStringLiteral("difficultyChip_all")));
        QCOMPARE(fixture.visibleIds(), ids({5, 1, 2, 3, 4, 7}));

        // 等级.
        QVERIFY(fixture.click(QStringLiteral("levelChip_91-100")));
        QCOMPARE(fixture.dialog()->property("levelFilter").toString(), QStringLiteral("91-100"));
        QCOMPARE(fixture.visibleIds(), ids({3, 4}));
        QVERIFY(fixture.item(QStringLiteral("levelChip_91-100"))->property("checked").toBool());
        QVERIFY(!fixture.item(QStringLiteral("levelChip_all"))->property("checked").toBool());
        QVERIFY(fixture.click(QStringLiteral("levelChip_1-50")));
        QCOMPARE(fixture.visibleIds(), ids({5, 1, 2}));
        QVERIFY(fixture.click(QStringLiteral("levelChip_all")));

        // 搜索: name, exact level, version, expansion name.
        const QList<QPair<QString, QVariantList>> searches = {
            {QStringLiteral("朱诺"), ids({4})},
            {QStringLiteral("100"), ids({3, 4})},
            {QStringLiteral("7.2"), ids({3, 4})},
            {QStringLiteral("7.x"), ids({3, 4})},
            {QStringLiteral("2"), ids({5, 1, 2})},
            {QStringLiteral("金曦"), ids({3, 4})},
            {QStringLiteral("讨伐"), ids({2})},
            {QStringLiteral("裸"), ids({7})},
            {QStringLiteral("  沙斯塔夏 "), ids({1})},
        };
        auto *search = fixture.item(QStringLiteral("dutySearchField"));
        QVERIFY(search);
        for (const auto &[query, expected] : searches) {
            search->setProperty("text", query);
            QCOMPARE(fixture.dialog()->property("dutyQuery").toString(), query);
            QCOMPARE(fixture.visibleIds(), expected);
        }
        search->setProperty("text", QStringLiteral("90"));
        QVERIFY(fixture.visibleIds().isEmpty());
        QTRY_VERIFY(fixture.item(QStringLiteral("dutyListEmpty"))->isVisible());
        QCOMPARE(fixture.item(QStringLiteral("dutyCount"))->property("text").toString(),
                 QStringLiteral("共 0 个副本"));

        // The bare row is selectable and keeps its option index whatever the filters do.
        search->setProperty("text", QStringLiteral("裸"));
        QVERIFY(fixture.click(QStringLiteral("dutyRow_7")));
        const int bareIndex = fixture.dialog()->property("dutyIndex").toInt();
        QCOMPARE(bareIndex, 6);
        QCOMPARE(fixture.selectedDutyName(), QStringLiteral("裸行"));
        search->setProperty("text", QString());
        QVERIFY(QMetaObject::invokeMethod(party, "activated", Q_ARG(QVariant, QVariant(QStringLiteral("24")))));
        QCOMPARE(fixture.dialog()->property("dutyIndex").toInt(), bareIndex);
        QCOMPARE(fixture.selectedDutyName(), QStringLiteral("裸行"));

        // Reopening resets the filters.
        QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "openForCreate"));
        QCOMPARE(fixture.dialog()->property("partyFilter").toString(), QString());
        QCOMPARE(fixture.dialog()->property("dutyQuery").toString(), QString());
        QCOMPARE(fixture.dialog()->property("dutyIndex").toInt(), 0);
        QCOMPARE(fixture.visibleIds().size(), 6);
    }

    void jobsAreGroupedByRoleInLegendOrder()
    {
        DialogFixture fixture;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));
        QVariantMap scholar = job(28, QStringLiteral("学者"));
        scholar.insert(QStringLiteral("role_group"), QStringLiteral("治疗"));
        scholar.insert(QStringLiteral("abbreviation"), QStringLiteral("SCH"));
        fixture.dialog()->setProperty("jobOptions", QVariantList{
            job(24, QStringLiteral("白魔法师")), job(19, QStringLiteral("骑士")), scholar,
            job(99999, QStringLiteral("没有职能的职业"))});
        const auto groups = asList(fixture.dialog()->property("jobGroups"));
        QStringList roles;
        for (const QVariant &group : groups)
            roles.append(asMap(group).value(QStringLiteral("role")).toString());
        QCOMPARE(roles, QStringList({QStringLiteral("坦克"), QStringLiteral("治疗"),
                                     QStringLiteral("其他")}));
        const auto healers = asList(asMap(groups.at(1)).value(QStringLiteral("jobs")));
        QCOMPARE(healers.size(), 2);
        QCOMPARE(asMap(healers.at(0)).value(QStringLiteral("option_index")).toInt(), 1);
        QCOMPARE(asMap(healers.at(1)).value(QStringLiteral("abbreviation")).toString(),
                 QStringLiteral("SCH"));
        QCOMPARE(asMap(groups.at(0)).value(QStringLiteral("token")).toString(),
                 mr::JobCatalog::tokenForRoleGroup(QStringLiteral("坦克")));

        QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "openForCreate"));
        QVERIFY(fixture.goToStep(2));
        QVERIFY(fixture.click(QStringLiteral("jobPick_28")));
        QCOMPARE(fixture.dialog()->property("jobIndex").toInt(), 3);
        QVERIFY(fixture.click(QStringLiteral("jobPick_99999")));
        QCOMPARE(fixture.dialog()->property("jobIndex").toInt(), 4);
        QVariant name;
        QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "selectedJobName",
                                          Q_RETURN_ARG(QVariant, name)));
        QCOMPARE(name.toString(), QStringLiteral("没有职能的职业"));
    }

    void recentDutiesAreDistinctCatalogueRowsNewestFirst()
    {
        QJsonArray runs;
        runs.append(runRow(17));
        runs.append(runRow(17));
        runs.append(runRow(QJsonValue(QJsonValue::Null)));
        runs.append(runRow(999999));
        runs.append(runRow(56, true));
        for (int id : {4, 2, 3, 11, 15, 239})
            runs.append(runRow(id));
        const QVariantList recent = mr::AppController::recentDutiesFromRuns(runs, 5);
        QVariantList got;
        for (const QVariant &row : recent)
            got.append(row.toMap().value(QStringLiteral("content_id")).toInt());
        QCOMPARE(got, ids({17, 4, 2, 3, 11}));
        const QVariantMap first = recent.first().toMap();
        QCOMPARE(first.value(QStringLiteral("duty_name")).toString(),
                 mr::DutyCatalog::shared()->lookup(17).value(QStringLiteral("duty_name")).toString());
        QCOMPARE(first.value(QStringLiteral("party_size")).toInt(), 4);
        QVERIFY(mr::AppController::recentDutiesFromRuns(runs, 2).size() == 2);
        QVERIFY(mr::AppController::recentDutiesFromRuns(QJsonArray(), 5).isEmpty());

        // Through the mock backend and into the dialog's chips.
        DialogFixture fixture;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));
        fixture.dialog()->setProperty("dutyOptions", fixture.controller.dutyCatalogOptions());
        QSignalSpy changed(&fixture.controller, &mr::AppController::recentDutiesChanged);
        QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "openForCreate"));
        QTRY_COMPARE_WITH_TIMEOUT(changed.count(), 1, 5000);
        const QVariantList fromMock = fixture.controller.recentDuties();
        QCOMPARE(fromMock.size(), 5);
        QSet<qint64> distinct;
        for (const QVariant &row : fromMock)
            distinct.insert(row.toMap().value(QStringLiteral("content_id")).toLongLong());
        QCOMPARE(distinct.size(), 5);
        // The newest mock run is the alliance raid placed on it.
        QCOMPARE(fromMock.first().toMap().value(QStringLiteral("content_id")).toInt(), 1015);
        QCOMPARE(fromMock.first().toMap().value(QStringLiteral("party_size")).toInt(), 24);

        const auto chips = asList(fixture.dialog()->property("recentDutyOptions"));
        QCOMPARE(chips.size(), 5);
        QVERIFY(fixture.goToStep(2));
        QVERIFY(fixture.click(QStringLiteral("recentDuty_1015")));
        QCOMPARE(fixture.selectedDutyName(), QStringLiteral("朱诺：第一巡行"));
        QVERIFY(fixture.item(QStringLiteral("recentDuty_1015"))->property("checked").toBool());
        // Hidden while searching.
        QVERIFY(fixture.item(QStringLiteral("recentDuties"))->isVisible());
        fixture.dialog()->setProperty("dutyQuery", QStringLiteral("巡行"));
        QVERIFY(!fixture.item(QStringLiteral("recentDuties"))->isVisible());

        // A refreshed list that did not change does not notify again.
        QVERIFY(QMetaObject::invokeMethod(&fixture.controller, "refreshRecentDuties"));
        QTest::qWait(300);
        QCOMPARE(changed.count(), 1);
    }

    void dutyCatalogDerivesPartySizeDifficultyAndVersion()
    {
        using mr::DutyCatalog;
        const auto u = [](const char *text) { return QString::fromUtf8(text); };
        QCOMPARE(DutyCatalog::partySizeGroup(u("四人迷宫"), QVariant()), 4);
        QCOMPARE(DutyCatalog::partySizeGroup(u("讨伐歼灭战"), 4), 8);
        QCOMPARE(DutyCatalog::partySizeGroup(u("团队任务"), QVariant()), 24);
        QCOMPARE(DutyCatalog::partySizeGroup(u("大型任务"), 24), 24);
        QCOMPARE(DutyCatalog::partySizeGroup(u("大型任务"), 8), 8);
        QCOMPARE(DutyCatalog::partySizeGroup(u("大型任务"), QVariant()), 8);
        QCOMPARE(DutyCatalog::partySizeGroup(u("大型任务"), QVariant::fromValue(nullptr)), 8);
        QCOMPARE(DutyCatalog::partySizeGroup(u("行会令"), 4), 0);
        QCOMPARE(DutyCatalog::partySizeGroup(u("PVP"), 8), 0);
        QCOMPARE(DutyCatalog::partySizeGroup(QString(), 4), 0);

        for (const char *name : {"伊弗利特歼殛战", "佐拉加歼殛战", "永恒女王忆想歼灭战", "终极之战",
                                 "神龙幻巧战", "究极神兵假想作战", "白虎诗魂战", "火龙上位狩猎战",
                                 "圆桌骑士幻想歼灭战", "尼德霍格传奇征龙战", "红宝石神兵狂想作战",
                                 "博兹雅堡垒追忆战", "永远之暗悲惶歼灭战"})
            QVERIFY2(DutyCatalog::difficultyForName(u(name)) == u("极"), name);
        for (const char *name : {"伊弗利特讨伐战", "佐拉加歼灭战", "究极神兵破坏作战", "终结之战",
                                 "白虎镇魂战", "火龙狩猎战", "尼德霍格征龙战", "天然要害沙斯塔夏溶洞",
                                 "朱诺：第一巡行", ""})
            QVERIFY2(DutyCatalog::difficultyForName(u(name)) == u("普通"), name);
        QCOMPARE(DutyCatalog::difficultyForName(u("阿卡狄亚零式登天斗技场 轻量级1")), u("零式"));
        QCOMPARE(DutyCatalog::difficultyForName(u("欧米茄零式时空狭缝 德尔塔幻境1")), u("零式"));

        QCOMPARE(DutyCatalog::versionForExpansion(QStringLiteral("A Realm Reborn")), QStringLiteral("2.x"));
        QCOMPARE(DutyCatalog::versionForExpansion(QStringLiteral("Heavensward")), QStringLiteral("3.x"));
        QCOMPARE(DutyCatalog::versionForExpansion(QStringLiteral("Stormblood")), QStringLiteral("4.x"));
        QCOMPARE(DutyCatalog::versionForExpansion(QStringLiteral("Shadowbringers")), QStringLiteral("5.x"));
        QCOMPARE(DutyCatalog::versionForExpansion(QStringLiteral("Endwalker")), QStringLiteral("6.x"));
        QCOMPARE(DutyCatalog::versionForExpansion(QStringLiteral("Dawntrail")), QStringLiteral("7.x"));
        QCOMPARE(DutyCatalog::versionForExpansion(QStringLiteral("UNKNOWN")), QString());

        // The bundled table, joined with the data file's party_size.
        const DutyCatalog *catalog = DutyCatalog::shared();
        const QVariantMap alliance = catalog->lookup(1015);
        QCOMPARE(alliance.value(QStringLiteral("duty_category")).toString(), u("大型任务"));
        QCOMPARE(alliance.value(QStringLiteral("party_size")).toInt(), 24);
        QCOMPARE(alliance.value(QStringLiteral("difficulty")).toString(), u("普通"));
        QCOMPARE(alliance.value(QStringLiteral("version")).toString(), QStringLiteral("7.x"));
        QCOMPARE(catalog->lookup(986).value(QStringLiteral("party_size")).toInt(), 8);
        QCOMPARE(catalog->lookup(986).value(QStringLiteral("difficulty")).toString(), u("零式"));
        QCOMPARE(catalog->lookup(1017).value(QStringLiteral("difficulty")).toString(), u("极"));
        QCOMPARE(catalog->lookup(56).value(QStringLiteral("party_size")).toInt(), 8);
        QCOMPARE(catalog->lookup(4).value(QStringLiteral("party_size")).toInt(), 4);
        QCOMPARE(catalog->lookup(4).value(QStringLiteral("version")).toString(), QStringLiteral("2.x"));

        QHash<int, int> parties;
        QHash<QString, int> difficulties;
        const QStringList roulette = {u("四人迷宫"), u("讨伐歼灭战"), u("大型任务"), u("团队任务"),
                                      u("行会令")};
        for (const QVariant &value : catalog->allDuties()) {
            const QVariantMap row = value.toMap();
            if (!roulette.contains(row.value(QStringLiteral("duty_category")).toString()))
                continue;
            ++parties[row.value(QStringLiteral("party_size")).toInt()];
            ++difficulties[row.value(QStringLiteral("difficulty")).toString()];
        }
        QCOMPARE(parties.value(4), 103);
        QCOMPARE(parties.value(8), 244);
        QCOMPARE(parties.value(24), 19);
        QCOMPARE(parties.value(0), 14);
        QCOMPARE(difficulties.value(u("极")), 47);
        QCOMPARE(difficulties.value(u("零式")), 64);
        QCOMPARE(difficulties.value(u("普通")), 269);
    }

    // 动效: the incoming step slides in from the side it comes from; with the
    // system's animations off it lands at once.
    void stepSwitchSlidesTheIncomingStep()
    {
        DialogFixture fixture;
        QVERIFY2(fixture.create(false), qPrintable(fixture.errors));
        QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "openForCreate"));
        QVERIFY(fixture.dialog()->property("stepAnimates").toBool());
        QCOMPARE(fixture.dialog()->property("stepProgress").toReal(), 1.0);

        QVERIFY(fixture.goToStep(2));
        QCOMPARE(fixture.dialog()->property("stepDirection").toInt(), 1);
        QVERIFY(fixture.dialog()->property("stepProgress").toReal() < 1.0);
        QQuickItem *step2 = fixture.item(QStringLiteral("wizardStep2"));
        QVERIFY(step2);
        QVERIFY(step2->isVisible());
        QVERIFY(step2->opacity() < 1.0);
        QTRY_COMPARE(fixture.dialog()->property("stepProgress").toReal(), 1.0);
        QCOMPARE(step2->opacity(), 1.0);

        QVERIFY(fixture.goToStep(1));
        QCOMPARE(fixture.dialog()->property("stepDirection").toInt(), -1);
        QTRY_COMPARE(fixture.dialog()->property("stepProgress").toReal(), 1.0);

        // Reduced motion: no in-between frame, ever.
        DialogFixture still;
        QVERIFY2(still.create(true), qPrintable(still.errors));
        QVERIFY(QMetaObject::invokeMethod(still.dialog(), "openForCreate"));
        QVERIFY(!still.dialog()->property("stepAnimates").toBool());
        QVERIFY(still.goToStep(3));
        QCOMPARE(still.dialog()->property("stepProgress").toReal(), 1.0);
        QCOMPARE(still.item(QStringLiteral("wizardStep3"))->opacity(), 1.0);
    }
};

int main(int argc, char *argv[])
{
    // Never run against the user's own Collector, database or serve lease - see
    // TestCollectorGuard.h. Must precede any construction.
    mrtest::disableCollectorLaunch();
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication app(argc, argv);
    EditRunDialogBindingTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "EditRunDialogBindingTests.moc"
