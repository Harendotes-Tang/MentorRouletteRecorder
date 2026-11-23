#include "DesktopTestRunner.h"
#include "QmlComponentTestSupport.h"
#include "TestCollectorGuard.h"
#include "DisplayTimeZone.h"
#include "Formatters.h"
#include "JobCatalog.h"
#include "RoleCatalog.h"
#include "RunFormValidator.h"

#include <QDateTime>
#include <QGuiApplication>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQmlPropertyMap>
#include <QScopeGuard>
#include <QSignalSpy>
#include <QTest>
#include <QTimeZone>
#include <memory>

namespace {

// Keep the stack-owned fixture compatible with Qt 6.8 while using the
// constructor supported by Qt 6.11 and newer.
class TestAppProperties final : public QQmlPropertyMap
{
    Q_OBJECT
public:
    TestAppProperties() : QQmlPropertyMap(this, nullptr) {}

    Q_INVOKABLE QString errorText(const QString &message, const QString &) const { return message; }
Q_SIGNALS:
    void scopeChanging();
    void reflectionSaved(const QString & /*runId*/, bool /*cleared*/);
    void reflectionFailed(const QString & /*code*/, const QString & /*message*/);
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

using mrtest::qmlMap;

} // namespace

namespace {
class RunFormValidatorTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void initTestCase()
    {
        QVERIFY(mrtest::registerSourceQml({"components", "dialogs"}));
    }

    void init()
    {
        m_app.insert("dark", true);
        m_app.insert("goalCount", 2000);
        // The creation form snapshots the database identity before emitting
        // createRequested, even when this validator-only probe saves no note.
        m_recovery.insert("databaseKey", QStringLiteral("validator-fixture"));
        m_recovery.insert("identityVerified", true);
        m_app.insert("recovery", QVariant::fromValue(static_cast<QObject *>(&m_recovery)));
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

    void incompleteImportContextAppliesOnlyToAnEdit()
    {
        QVariantMap form{{"reason", "supplement note"}, {"note", "new note"},
                         {"date", ""}, {"matched", ""}, {"entered", ""}, {"ended", ""},
                         {"result", "UNKNOWN"}, {"incomplete_import_context", true}};
        QCOMPARE(mr::RunFormValidator::validate(form).value("code").toString(), "ERR_BAD_REQUEST");
        form.insert("edit_mode", true);
        const auto verdict = mr::RunFormValidator::validate(form, {{"note", "old note"}});
        QVERIFY2(verdict.value("ok").toBool(), qPrintable(verdict.value("message").toString()));
        form.insert("incomplete_import_context", false);
        QCOMPARE(mr::RunFormValidator::validate(form).value("code").toString(), "ERR_BAD_REQUEST");
    }

    void importedKnownEndpointsStillValidateTheirOrderWithoutAMatchTime()
    {
        QVariantMap form{{"reason", "supplement times"}, {"date", ""}, {"matched", ""},
                         {"entered_date", "2026-10-09"}, {"entered", "10:00:00"},
                         {"ended_date", "2026-10-09"}, {"ended", "09:00:00"},
                         {"result", "UNKNOWN"}, {"edit_mode", true}, {"incomplete_import_context", true}};
        QCOMPARE(mr::RunFormValidator::validate(form).value("code").toString(), "ERR_NEGATIVE_DURATION");
        form.insert("ended", "10:30:00");
        const auto verdict = mr::RunFormValidator::validate(form);
        QVERIFY2(verdict.value("ok").toBool(), qPrintable(verdict.value("message").toString()));
    }

    void validatesExplicitDatesAndMilliseconds()
    {
        const auto verdict = mr::RunFormValidator::validate(crossMidnightForm());
        QVERIFY2(verdict.value("ok").toBool(), qPrintable(verdict.value("message").toString()));
        const auto entered = mr::RunFormValidator::toDateTime("2026-09-07", "23:55:12.456").toDateTime();
        const auto ended = mr::RunFormValidator::toDateTime("2026-09-08", "00:20:14.789").toDateTime();
        QCOMPARE(entered.msecsTo(ended), 1502333);
    }

    // 显示时区: a typed date and time are a wall clock of the chosen zone, whatever the
    // machine's own. Asia/Kolkata (+05:30) differs from Asia/Tokyo and from UTC.
    void typedTimesAreReadInTheDisplayTimeZone()
    {
        const auto restore = qScopeGuard([] { mr::DisplayTimeZone::set(QString()); });
        mr::DisplayTimeZone::set(QStringLiteral("Asia/Kolkata"));
        QCOMPARE(mr::DisplayTimeZone::id(), QStringLiteral("Asia/Kolkata"));
        const QDateTime matched = mr::RunFormValidator::toDateTime("2026-11-20", "21:30:00").toDateTime();
        QCOMPARE(matched.toUTC(), QDateTime(QDate(2026, 11, 20), QTime(16, 0), QTimeZone::UTC));
        QCOMPARE(mr::RunFormValidator::toUtcIso("2026-11-21", "00:15:30.250"),
                 QStringLiteral("2026-11-20T18:45:30.250Z"));
        QCOMPARE(mr::RunFormValidator::toUtcIso("2026-11-21", "not a time"), QString());
        // Shown back as typed, so an untouched field is no edit.
        QCOMPARE(mr::Formatters::localDate(QStringLiteral("2026-11-20T18:45:30.250Z")), QStringLiteral("2026-11-21"));
        QCOMPARE(mr::Formatters::localTime(QStringLiteral("2026-11-20T18:45:30.250Z")), QStringLiteral("00:15:30"));
        mr::DisplayTimeZone::set(QStringLiteral("America/New_York"));
        QCOMPARE(mr::RunFormValidator::toUtcIso("2026-11-20", "21:30:00"), QStringLiteral("2026-11-21T02:30:00.000Z"));
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

    // 审查 OI-6：备注最多 1000 字、原因最多 500 字（采集服务的上限，按 UTF-16
    // 码元计）。超出时在本地拦下并用中文说明，而不是提交后收到
    // 「note 超过 1000 个字符的上限」。
    void refusesTheCollectorsNoteAndReasonLimitsLocally()
    {
        auto form = crossMidnightForm();
        form["note"] = QString(1000, QChar(0x5907));
        QVERIFY2(mr::RunFormValidator::validate(form).value("ok").toBool(),
                 qPrintable(mr::RunFormValidator::validate(form).value("message").toString()));
        form["note"] = QString(1001, QChar(0x5907));
        auto verdict = mr::RunFormValidator::validate(form);
        QVERIFY(!verdict.value("ok").toBool());
        QCOMPARE(verdict.value("code").toString(), QStringLiteral("ERR_BAD_REQUEST"));
        QVERIFY(verdict.value("message").toString().contains(QString::fromUtf8("备注")));
        QVERIFY(verdict.value("message").toString().contains(QStringLiteral("1000")));
        QVERIFY(!verdict.value("message").toString().contains(QStringLiteral("note")));

        form["note"] = "";
        form["reason"] = QString(500, QChar(0x7531));
        QVERIFY(mr::RunFormValidator::validate(form).value("ok").toBool());
        form["reason"] = QString(501, QChar(0x7531));
        form["reason_label"] = QString::fromUtf8("修正原因");
        verdict = mr::RunFormValidator::validate(form);
        QVERIFY(!verdict.value("ok").toBool());
        QCOMPARE(verdict.value("code").toString(), QStringLiteral("ERR_BAD_REQUEST"));
        QVERIFY(verdict.value("message").toString().contains(QString::fromUtf8("修正原因")));
        QVERIFY(verdict.value("message").toString().contains(QStringLiteral("500")));
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
        QCOMPARE(qmlMap(corrected.first().first()), QVariantMap({{"note", "new note"}}));
    }

    void fallBackCorrectionUsesTheStoredUtcInstants_data()
    {
        QTest::addColumn<QString>("endTime");
        QTest::addColumn<QString>("errorCode");
        QTest::addColumn<QVariantMap>("expectedChanges");
        QTest::newRow("note-only") << QString() << QString() << QVariantMap{{"note", "new note"}};
        QTest::newRow("edit-end") << QStringLiteral("02:10:00") << QString()
            << QVariantMap{{"note", "new note"}, {"ended_at_utc", "2026-11-01T07:10:00.000Z"},
                           {"duration_ms", 3587544}};
        QTest::newRow("reject-end-before-entry") << QStringLiteral("00:55:00")
            << QStringLiteral("ERR_NEGATIVE_DURATION") << QVariantMap{};
    }

    void fallBackCorrectionUsesTheStoredUtcInstants()
    {
        QFETCH(QString, endTime);
        QFETCH(QString, errorCode);
        QFETCH(QVariantMap, expectedChanges);
        const auto restore = qScopeGuard([zone = mr::DisplayTimeZone::id()] { mr::DisplayTimeZone::set(zone); });
        mr::DisplayTimeZone::set(QStringLiteral("America/New_York"));
        QCOMPARE(mr::DisplayTimeZone::id(), QStringLiteral("America/New_York"));

        auto run = crossMidnightRun();
        run["matched_at_utc"] = "2026-11-01T05:50:10.1234567Z";
        run["entered_at_utc"] = "2026-11-01T06:10:12.456Z";
        run["ended_at_utc"] = "2026-11-01T06:30:14.789Z";
        run["duration_ms"] = 1202333;
        QVERIFY(QMetaObject::invokeMethod(m_dialog, "openForRun", Q_ARG(QVariant, QVariant(run))));
        QCOMPARE(m_dialog->property("matchedTime").toString(), QStringLiteral("01:50:10.123"));
        QCOMPARE(m_dialog->property("enteredTime").toString(), QStringLiteral("01:10:12.456"));
        QCOMPARE(m_dialog->property("endedTime").toString(), QStringLiteral("01:30:14.789"));
        m_dialog->setProperty("noteText", "new note");
        if (!endTime.isEmpty())
            m_dialog->setProperty("endedTime", endTime);

        QSignalSpy corrected(m_dialog, SIGNAL(correctRequested(QVariant,QString)));
        QVERIFY(corrected.isValid());
        QVERIFY(QMetaObject::invokeMethod(m_dialog, "submit"));
        QCOMPARE(m_dialog->property("errorCode").toString(), errorCode);
        QCOMPARE(m_dialog->property("errorText").toString().isEmpty(), errorCode.isEmpty());
        if (errorCode.isEmpty()) {
            QCOMPARE(corrected.count(), 1);
            QCOMPARE(qmlMap(corrected.first().first()), expectedChanges);
        } else {
            QCOMPARE(corrected.count(), 0);
        }
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
        const auto changes = qmlMap(corrected.first().first());
        QCOMPARE(changes.size(), 2);
        QCOMPARE(changes.value("ended_at_utc").toString(), utcOn(QDate(2026, 9, 9), QTime(0, 20, 14, 789)));
        QCOMPARE(changes.value("duration_ms").toLongLong(), 87902333);
        const auto rows = mr::RunFormValidator::diff(qmlMap(m_dialog->property("beforeState")),
                                                    qmlMap(m_dialog->property("formState")));
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
        const auto fields = qmlMap(created.first().first());
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
    TestAppProperties m_recovery;
    std::unique_ptr<QQmlEngine> m_engine;
    std::unique_ptr<QObject> m_root;
    QObject *m_dialog = nullptr;
};
} // namespace

MR_DESKTOP_TEST_SUITE(RunFormValidator)
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
