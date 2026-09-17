#include "TestCollectorGuard.h"
#include "Formatters.h"
#include "JobCatalog.h"
#include "RoleCatalog.h"
#include "RunFormValidator.h"

#include <QDateTime>
#include <QDir>
#include <QGuiApplication>
#include <QJSValue>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQmlPropertyMap>
#include <QSignalSpy>
#include <QTest>
#include <memory>

namespace {

// Keep the stack-owned fixture compatible with Qt 6.8 while using the
// constructor supported by Qt 6.11 and newer.
class TestAppProperties final : public QQmlPropertyMap
{
    Q_OBJECT
public:
    TestAppProperties() : QQmlPropertyMap(this, nullptr) {}
};

QVariantMap crossMidnightForm()
{
    return {{"reason", "correct dates"}, {"date", "2026-09-07"},
            {"matched", "23:50:10.123"}, {"entered_date", "2026-09-07"},
            {"entered", "23:55:12.456"}, {"ended_date", "2026-09-08"},
            {"ended", "00:20:14.789"}, {"result", "COMPLETED"}};
}

QString utcOn(const QDate &date, const QTime &time)
{
    return QDateTime(date, time).toUTC().toString(Qt::ISODateWithMs);
}

QVariantMap crossMidnightRun()
{
    const QDate date(2026, 9, 7);
    const QVariant null = QVariant::fromValue(nullptr);
    return {{"run_id", "cross-midnight"}, {"revision", 2},
            {"matched_at_utc", utcOn(date, QTime(23, 50, 10, 123))},
            {"entered_at_utc", utcOn(date, QTime(23, 55, 12, 456))},
            {"ended_at_utc", utcOn(date.addDays(1), QTime(0, 20, 14, 789))},
            // A separately corrected duration must survive a note-only edit.
            {"duration_ms", 30000}, {"result", "COMPLETED"},
            {"contributes_to_goal", true}, {"note", "old note"},
            {"job_name", QString::fromUtf8("未知")}, {"role", "UNKNOWN"},
            {"content_id", null}, {"territory_id", null},
            {"duty_name", null}, {"duty_category", null},
            {"duty_level", null}, {"duty_expansion", null},
            {"job_id", null}};
}

QVariantMap asMap(const QVariant &value)
{
    return value.metaType() == QMetaType::fromType<QJSValue>()
        ? value.value<QJSValue>().toVariant().toMap() : value.toMap();
}

} // namespace

class RunFormValidatorTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void initTestCase()
    {
        const QString qmlRoot = QString::fromUtf8(MR_SOURCE_DIR) + "/src/Desktop/qml";
        qmlRegisterSingletonType(QUrl::fromLocalFile(qmlRoot + "/Theme.qml"),
                                 "MentorRecorder", 1, 0, "Theme");
        for (const QString &directory : {QStringLiteral("components"), QStringLiteral("dialogs")}) {
            const QDir folder(qmlRoot + "/" + directory);
            for (const QString &file : folder.entryList({"*.qml"}, QDir::Files)) {
                const QByteArray name = file.chopped(4).toUtf8();
                qmlRegisterType(QUrl::fromLocalFile(folder.filePath(file)),
                                "MentorRecorder", 1, 0, name.constData());
            }
        }
    }

    void init()
    {
        m_app.insert("dark", true);
        m_app.insert("goalCount", 2000);
        m_engine = std::make_unique<QQmlEngine>();
        auto *context = m_engine->rootContext();
        context->setContextProperty("App", &m_app);
        context->setContextProperty("Fmt", &m_formatters);
        context->setContextProperty("RunForm", &m_validator);
        context->setContextProperty("Jobs", &m_jobs);
        context->setContextProperty("Roles", &m_roles);
        context->setContextProperty("ReduceMotion", true);
        QQmlComponent component(m_engine.get());
        component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 900; height: 740; visible: true
    EditRunDialog { objectName: "editDialog" }
})", QUrl());
        m_root.reset(component.create());
        QVERIFY2(m_root, qPrintable(component.errorString()));
        m_dialog = m_root->findChild<QObject *>("editDialog");
        QVERIFY(m_dialog);
    }

    void cleanup()
    {
        m_dialog = nullptr;
        m_root.reset();
        m_engine.reset();
    }

    void validatesExplicitDatesAndMilliseconds()
    {
        const auto verdict = mr::RunFormValidator::validate(crossMidnightForm());
        QVERIFY2(verdict.value("ok").toBool(), qPrintable(verdict.value("message").toString()));
        const auto entered = mr::RunFormValidator::toDateTime("2026-09-07", "23:55:12.456").toDateTime();
        const auto ended = mr::RunFormValidator::toDateTime("2026-09-08", "00:20:14.789").toDateTime();
        QCOMPARE(entered.msecsTo(ended), 1502333);
    }

    void rejectsInvalidExplicitDateInsteadOfGuessingNextDay()
    {
        auto form = crossMidnightForm();
        form["ended_date"] = "2026-09-07";
        QCOMPARE(mr::RunFormValidator::validate(form).value("code").toString(), "ERR_NEGATIVE_DURATION");
        form["ended_date"] = "2026-02-30";
        QCOMPARE(mr::RunFormValidator::validate(form).value("code").toString(), "ERR_BAD_REQUEST");
        form["ended_date"] = "2026-09-08";
        form["entered_date"] = "";
        QCOMPARE(mr::RunFormValidator::validate(form).value("code").toString(), "ERR_BAD_REQUEST");
    }

    void dateChangesAppearInDiffAndPermitCorrection()
    {
        const auto before = crossMidnightForm();
        auto after = before;
        after["date"] = "2026-09-08";
        after["entered_date"] = "2026-09-08";
        after["ended_date"] = "2026-09-09";
        after["edit_mode"] = true;
        QVERIFY(mr::RunFormValidator::validate(after, before).value("ok").toBool());
        const auto rows = mr::RunFormValidator::diff(before, after);
        QCOMPARE(rows.size(), 3);
        QCOMPARE(rows[0].toMap().value("k").toString(), QString::fromUtf8("匹配日期"));
        QCOMPARE(rows[1].toMap().value("k").toString(), QString::fromUtf8("进本日期"));
        QCOMPARE(rows[2].toMap().value("k").toString(), QString::fromUtf8("结束日期"));
        QCOMPARE(rows[2].toMap().value("a").toString(), "2026-09-08");
        QCOMPARE(rows[2].toMap().value("b").toString(), "2026-09-09");
    }

    void noteOnlySubmissionKeepsDatesMillisecondsAndDuration()
    {
        auto run = crossMidnightRun();
        // Preserve Collector's finer-than-millisecond ISO text too when untouched.
        run["matched_at_utc"] = run["matched_at_utc"].toString().replace(".123Z", ".1234567Z");
        QVERIFY(QMetaObject::invokeMethod(m_dialog, "openForRun", Q_ARG(QVariant, QVariant(run))));
        QCOMPARE(m_dialog->property("enteredDate").toString(), "2026-09-07");
        QCOMPARE(m_dialog->property("endedDate").toString(), "2026-09-08");
        QCOMPARE(m_dialog->property("enteredTime").toString(), "23:55:12.456");
        m_dialog->setProperty("reasonText", "correct note");
        m_dialog->setProperty("noteText", "new note");
        QSignalSpy corrected(m_dialog, SIGNAL(correctRequested(QVariant,QString)));
        QVERIFY(corrected.isValid());
        QVERIFY(QMetaObject::invokeMethod(m_dialog, "submit"));
        QCOMPARE(m_dialog->property("errorCode").toString(), "");
        QCOMPARE(corrected.count(), 1);
        QCOMPARE(asMap(corrected.first().first()), QVariantMap({{"note", "new note"}}));
    }

    void dateOnlySubmissionChangesActualUtcField()
    {
        const auto run = crossMidnightRun();
        QVERIFY(QMetaObject::invokeMethod(m_dialog, "openForRun", Q_ARG(QVariant, QVariant(run))));
        m_dialog->setProperty("reasonText", "correct end date");
        m_dialog->setProperty("endedDate", "2026-09-09");
        QSignalSpy corrected(m_dialog, SIGNAL(correctRequested(QVariant,QString)));
        QVERIFY(QMetaObject::invokeMethod(m_dialog, "submit"));
        QCOMPARE(m_dialog->property("errorCode").toString(), "");
        QCOMPARE(corrected.count(), 1);
        const auto changes = asMap(corrected.first().first());
        QCOMPARE(changes.size(), 2);
        QCOMPARE(changes.value("ended_at_utc").toString(), utcOn(QDate(2026, 9, 9), QTime(0, 20, 14, 789)));
        QCOMPARE(changes.value("duration_ms").toLongLong(), 87902333);
        const auto rows = mr::RunFormValidator::diff(asMap(m_dialog->property("beforeState")),
                                                    asMap(m_dialog->property("formState")));
        QCOMPARE(rows.size(), 1);
        QCOMPARE(rows.first().toMap().value("k").toString(), QString::fromUtf8("结束日期"));
    }

    void createSubmissionUsesEachExplicitDate()
    {
        QVERIFY(QMetaObject::invokeMethod(m_dialog, "openForCreate"));
        m_dialog->setProperty("matchedDate", "2026-09-07");
        m_dialog->setProperty("matchedTime", "23:50:10.123");
        m_dialog->setProperty("enteredDate", "2026-09-07");
        m_dialog->setProperty("enteredTime", "23:55:12.456");
        m_dialog->setProperty("endedDate", "2026-09-08");
        m_dialog->setProperty("endedTime", "00:20:14.789");
        m_dialog->setProperty("reasonText", "missed while offline");
        QSignalSpy created(m_dialog, SIGNAL(createRequested(QVariant,QString)));
        QVERIFY(QMetaObject::invokeMethod(m_dialog, "submit"));
        QCOMPARE(m_dialog->property("errorCode").toString(), "");
        QCOMPARE(created.count(), 1);
        const auto fields = asMap(created.first().first());
        const auto expected = crossMidnightRun();
        for (const auto *key : {"matched_at_utc", "entered_at_utc", "ended_at_utc"})
            QCOMPARE(fields.value(key), expected.value(key));
        QCOMPARE(fields.value("duration_ms").toLongLong(), 1502333);
    }

private:
    TestAppProperties m_app;
    mr::Formatters m_formatters;
    mr::RunFormValidator m_validator;
    mr::JobCatalog m_jobs;
    mr::RoleCatalog m_roles;
    std::unique_ptr<QQmlEngine> m_engine;
    std::unique_ptr<QObject> m_root;
    QObject *m_dialog = nullptr;
};

int main(int argc, char **argv)
{
    // Never run against the user's own Collector, database or serve lease - see
    // TestCollectorGuard.h. Must precede any construction.
    mrtest::disableCollectorLaunch();
    QGuiApplication app(argc, argv);
    Q_INIT_RESOURCE(mr_icons);
    RunFormValidatorTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "RunFormValidatorTests.moc"
