// ---------------------------------------------------------------------------
// 显示时区 (设置 › 通用): the zone record times are shown and typed in.
//
// What it pins:
//   * with a zone chosen, a UTC instant is shown as that zone's wall clock,
//     whatever the machine's own zone; switching the setting re-formats, and
//     an id Qt does not know falls back to the system and is stored as "";
//   * the setting persists, and an unknown id in the file is dropped on load;
//   * across a daylight-saving change (America/New_York) both sides show the
//     right wall clock and offset;
//   * the offered list comes from one place, keeps to ids this machine knows
//     and is ordered by offset;
//   * the settings row lists 跟随系统 first, stores the id chosen, names the
//     zone in force under the row, and a binding that formats through
//     Runtime.fmt re-renders without a restart;
//   * the Collector is told the zone (SetDisplayTimeZone) on every connection
//     and on every change, a Collector older than the message is ignored, and
//     once it acknowledges a change the history list and its calendar of days
//     are read again;
//   * a date range already applied on the history page keeps its picked days
//     and takes the new zone's bounds for them.
// The machine's zone is never changed; every test leaves DisplayTimeZone on
// the system (the suites of one executable share the process).
// ---------------------------------------------------------------------------

#include "DesktopTestRunner.h"
#include "QmlComponentTestSupport.h"
#include "TestCollectorGuard.h"
#include "AppController.h"
#include "AppSettings.h"
#include "DisplayTimeZone.h"
#include "Formatters.h"
#include "IBackend.h"
#include "JobCatalog.h"
#include "MockBackend.h"
#include "RoleCatalog.h"
#include "RunFormValidator.h"
#include "RunListModel.h"
#include "TtsService.h"
#include "workflows/DisplayTimeZoneSync.h"

#include <QGuiApplication>
#include <QHash>
#include <QJsonObject>
#include <QJsonValue>
#include <QList>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QSettings>
#include <QSignalSpy>
#include <QStandardPaths>
#include <QTemporaryDir>
#include <QTimer>
#include <QTest>
#include <QTimeZone>

#include <algorithm>
#include <limits>
#include <memory>

namespace {

constexpr auto kInstant = "2026-11-20T16:30:00.000Z";

using mrtest::findVisualItem;

QString systemWallClock(const char *utc)
{
    return QDateTime::fromString(QString::fromLatin1(utc), Qt::ISODateWithMs)
        .toLocalTime().toString(QStringLiteral("yyyy-MM-dd HH:mm:ss"));
}

/// 设置 › 通用 on the deterministic backend, with a probe that formats one
/// instant through Runtime.fmt the way every page does.
struct SettingsScene
{
    QTemporaryDir directory;
    std::unique_ptr<mr::MockBackend> backend = std::make_unique<mr::MockBackend>();
    std::unique_ptr<mr::AppSettings> settings;
    std::unique_ptr<mr::AppController> app;
    std::unique_ptr<mr::Formatters> formatters;
    std::unique_ptr<mr::JobCatalog> jobs;
    std::unique_ptr<mr::RoleCatalog> roles;
    std::unique_ptr<QQmlEngine> engine;
    std::unique_ptr<QObject> root;

    ~SettingsScene()
    {
        root.reset();
        engine.reset();
        app.reset();
    }

    bool open()
    {
        settings = std::make_unique<mr::AppSettings>(directory.filePath(QStringLiteral("desktop.ini")), nullptr);
        app = std::make_unique<mr::AppController>(backend.get(), settings.get());
        formatters = std::make_unique<mr::Formatters>();
        jobs = std::make_unique<mr::JobCatalog>();
        roles = std::make_unique<mr::RoleCatalog>();
        engine = std::make_unique<QQmlEngine>();
        auto *context = engine->rootContext();
        context->setContextProperty(QStringLiteral("App"), app.get());
        context->setContextProperty(QStringLiteral("Fmt"), formatters.get());
        context->setContextProperty(QStringLiteral("Jobs"), jobs.get());
        context->setContextProperty(QStringLiteral("Roles"), roles.get());
        context->setContextProperty(QStringLiteral("Settings"), settings.get());
        context->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QQmlComponent component(engine.get());
        component.setData(QStringLiteral(R"(import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder
ApplicationWindow {
    width: 1100; height: 900; visible: true
    color: Theme.surface
    ColumnLayout {
        anchors.fill: parent
        anchors.margins: 16
        Text { objectName: "timeProbe"; text: Runtime.fmt.localDateTime("%1") }
        SettingsGeneralTab { objectName: "scenePage"; Layout.fillWidth: true; Layout.fillHeight: true }
    }
})").arg(QLatin1String(kInstant)).toUtf8(), QUrl());
        root.reset(component.create());
        if (!root) {
            qWarning("%s", qPrintable(component.errorString()));
            return false;
        }
        return item(QStringLiteral("scenePage")) != nullptr;
    }

    QQuickItem *item(const QString &name) const
    {
        auto *window = qobject_cast<QQuickWindow *>(root.get());
        return window ? findVisualItem(window->contentItem(), name) : nullptr;
    }
};

/// A Collector connection the test opens and closes. SetDisplayTimeZone is
/// answered on the next turn of the event loop with the zone it was sent, or -
/// as a Collector older than the message does - refused with
/// ipc.unknown_message_type. Nothing else is ever answered.
class ZoneBackend final : public mr::IBackend
{
public:
    bool connected = false;
    bool olderCollector = false;
    QList<QJsonValue> sent;

    QString backendName() const override { return QStringLiteral("zone-backend"); }
    bool isConnected() const override { return connected; }
    void setConnected(bool value)
    {
        connected = value;
        Q_EMIT connectionChanged();
    }
    mr::BackendReply *request(const QString &messageType, const QJsonObject &payload) override
    {
        auto *reply = new mr::BackendReply(QStringLiteral("zone"), messageType, this);
        if (messageType != QLatin1String("SetDisplayTimeZone"))
            return reply;
        sent.append(payload.value(QStringLiteral("time_zone")));
        const QJsonValue zone = payload.value(QStringLiteral("time_zone"));
        const bool refuse = olderCollector;
        QTimer::singleShot(0, reply, [reply, zone, refuse] {
            if (refuse) {
                reply->fail(QStringLiteral("ERR_BAD_REQUEST"), QStringLiteral("unknown"), {},
                            QJsonObject{{QStringLiteral("key"), QStringLiteral("ipc.unknown_message_type")}});
            } else {
                reply->succeed({{QStringLiteral("time_zone"), zone},
                                {QStringLiteral("utc_offset_minutes"), 0}});
            }
        });
        return reply;
    }
};

/// The offline backend, counting what the Desktop asks for.
class CountingBackend final : public mr::MockBackend
{
public:
    QHash<QString, int> counts;
    mr::BackendReply *request(const QString &type, const QJsonObject &payload = {}) override
    {
        counts[type] += 1;
        return MockBackend::request(type, payload);
    }
};

class DisplayTimeZoneTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void initTestCase()
    {
        QVERIFY(mrtest::registerShippingQml());
        for (const char *zone : {"Asia/Taipei", "Asia/Tokyo", "America/New_York"})
            QVERIFY2(QTimeZone(zone).isValid(), zone);
    }

    void cleanup()
    {
        mr::DisplayTimeZone::set(QString());
        QVERIFY(mr::DisplayTimeZone::followsSystem());
    }

    void formattersFollowTheChosenZone()
    {
        QTemporaryDir directory;
        mr::AppSettings settings(directory.filePath(QStringLiteral("desktop.ini")), nullptr);
        QSignalSpy changed(mr::DisplayTimeZone::notifier(), &mr::DisplayTimeZone::changed);
        QSignalSpy settingChanged(&settings, &mr::AppSettings::displayTimeZoneChanged);

        QCOMPARE(mr::Formatters::localDateTime(QLatin1String(kInstant)), systemWallClock(kInstant));
        settings.setDisplayTimeZone(QStringLiteral("Asia/Taipei"));
        QCOMPARE(settings.displayTimeZone(), QStringLiteral("Asia/Taipei"));
        QCOMPARE(mr::DisplayTimeZone::id(), QStringLiteral("Asia/Taipei"));
        QCOMPARE(changed.count(), 1);
        QCOMPARE(settingChanged.count(), 1);
        QCOMPARE(mr::Formatters::localDateTime(QLatin1String(kInstant)), QStringLiteral("2026-11-21 00:30:00"));
        QCOMPARE(mr::Formatters::localDate(QLatin1String(kInstant)), QStringLiteral("2026-11-21"));
        QCOMPARE(mr::Formatters::localTime(QLatin1String(kInstant)), QStringLiteral("00:30:00"));
        QCOMPARE(mr::Formatters::localFormat(QLatin1String(kInstant), QStringLiteral("MM-dd HH:mm")),
                 QStringLiteral("11-21 00:30"));
        QCOMPARE(mr::Formatters::localFormat(QStringLiteral("not a time"), QStringLiteral("HH:mm")), QString());
        QCOMPARE(mr::Formatters::localUtcOffset(QLatin1String(kInstant)), QStringLiteral("UTC+08:00"));
        QCOMPARE(mr::Formatters::dateWithWeekday(QLatin1String(kInstant)).left(10), QStringLiteral("2026-11-21"));

        settings.setDisplayTimeZone(QStringLiteral("America/New_York"));
        QCOMPARE(changed.count(), 2);
        QCOMPARE(mr::Formatters::localDateTime(QLatin1String(kInstant)), QStringLiteral("2026-11-20 11:30:00"));

        // Not a zone Qt knows: the system's, and nothing kept.
        settings.setDisplayTimeZone(QStringLiteral("Mars/Olympus_Mons"));
        QCOMPARE(settings.displayTimeZone(), QString());
        QVERIFY(mr::DisplayTimeZone::followsSystem());
        QCOMPARE(mr::Formatters::localDateTime(QLatin1String(kInstant)), systemWallClock(kInstant));
        QVERIFY(!QSettings(settings.instanceFilePath(), QSettings::IniFormat)
                     .contains(QStringLiteral("general/displayTimeZone")));
        mr::DisplayTimeZone::set(QStringLiteral("Mars/Olympus_Mons"));
        QVERIFY(mr::DisplayTimeZone::followsSystem());
        QCOMPARE(mr::DisplayTimeZone::normalized(QStringLiteral("  Asia/Tokyo ")), QStringLiteral("Asia/Tokyo"));
    }

    void theChoiceIsKeptAndAnUnknownStoredIdIsDropped()
    {
        QTemporaryDir directory;
        const QString path = directory.filePath(QStringLiteral("desktop.ini"));
        {
            mr::AppSettings settings(path, nullptr);
            QCOMPARE(settings.displayTimeZone(), QString());
            settings.setDisplayTimeZone(QStringLiteral("Asia/Tokyo"));
        }
        mr::DisplayTimeZone::set(QString());
        {
            // Reading the file never puts the zone in force: main() does that once.
            mr::AppSettings reopened(path, nullptr);
            QCOMPARE(reopened.displayTimeZone(), QStringLiteral("Asia/Tokyo"));
            QVERIFY(mr::DisplayTimeZone::followsSystem());
        }
        {
            QSettings raw(path, QSettings::IniFormat);
            raw.setValue(QStringLiteral("general/displayTimeZone"), QStringLiteral("Mars/Olympus_Mons"));
        }
        mr::AppSettings edited(path, nullptr);
        QCOMPARE(edited.displayTimeZone(), QString());
        QVERIFY(!QSettings(path, QSettings::IniFormat).contains(QStringLiteral("general/displayTimeZone")));
    }

    void daylightSavingChangesKeepTheWallClockRight()
    {
        mr::DisplayTimeZone::set(QStringLiteral("America/New_York"));
        // 2026-03-08 02:00 EST springs forward to 03:00 EDT.
        QCOMPARE(mr::Formatters::localDateTime(QStringLiteral("2026-03-08T06:59:00Z")),
                 QStringLiteral("2026-03-08 01:59:00"));
        QCOMPARE(mr::Formatters::localUtcOffset(QStringLiteral("2026-03-08T06:59:00Z")), QStringLiteral("UTC-05:00"));
        QCOMPARE(mr::Formatters::localDateTime(QStringLiteral("2026-03-08T07:00:00Z")),
                 QStringLiteral("2026-03-08 03:00:00"));
        QCOMPARE(mr::Formatters::localUtcOffset(QStringLiteral("2026-03-08T07:00:00Z")), QStringLiteral("UTC-04:00"));
        // 2026-11-01 02:00 EDT falls back to 01:00 EST.
        QCOMPARE(mr::Formatters::localTime(QStringLiteral("2026-11-01T05:59:00Z")), QStringLiteral("01:59:00"));
        QCOMPARE(mr::Formatters::localTime(QStringLiteral("2026-11-01T06:00:00Z")), QStringLiteral("01:00:00"));
        // A typed time on either side is the instant of that side.
        QCOMPARE(mr::Formatters::dayBoundaryUtc(QStringLiteral("2026-03-08"), false),
                 QStringLiteral("2026-03-08T05:00:00.000Z"));
        QCOMPARE(mr::Formatters::dayBoundaryUtc(QStringLiteral("2026-03-08"), true),
                 QStringLiteral("2026-03-09T03:59:59.999Z"));
        QCOMPARE(mr::DisplayTimeZone::fromWallClock(QDate(2026, 7, 1), QTime(12, 0)).toUTC(),
                 QDateTime(QDate(2026, 7, 1), QTime(16, 0), QTimeZone::UTC));
    }

    void theOfferedZonesAreOneOrderedList()
    {
        const QStringList ids = mr::DisplayTimeZone::curatedIds();
        QVERIFY(ids.size() >= 30);
        for (const char *required : {"Asia/Shanghai", "Asia/Taipei", "Asia/Hong_Kong", "Asia/Tokyo", "Asia/Seoul",
                                     "Asia/Singapore", "Europe/London", "America/New_York", "America/Los_Angeles",
                                     "Australia/Sydney", "UTC"}) {
            QVERIFY2(ids.contains(QString::fromLatin1(required)), required);
        }
        const QDateTime now = QDateTime::currentDateTimeUtc();
        int previous = std::numeric_limits<int>::min();
        for (const QString &id : ids) {
            QVERIFY2(QTimeZone(id.toUtf8()).isValid(), qPrintable(id));
            const int offset = now.toTimeZone(QTimeZone(id.toUtf8())).offsetFromUtc();
            QVERIFY2(offset >= previous, qPrintable(id));
            previous = offset;
        }
        const QVariantList options = mr::Formatters::displayTimeZoneOptions();
        QCOMPARE(options.size(), ids.size());
        const auto taipei = std::ranges::find_if(options, [](const QVariant &option) {
            return option.toMap().value(QStringLiteral("value")).toString() == QLatin1String("Asia/Taipei");
        });
        QVERIFY(taipei != options.end());
        QCOMPARE(taipei->toMap().value(QStringLiteral("text")).toString(), QStringLiteral("UTC+08:00 · Asia/Taipei"));
        QCOMPARE(mr::DisplayTimeZone::offsetText(QTimeZone("Asia/Kolkata")), QStringLiteral("+05:30"));
    }

    void theSettingsRowListsTheSystemFirstAndStoresTheChoice()
    {
        SettingsScene scene;
        QVERIFY(scene.open());
        auto *box = scene.item(QStringLiteral("displayTimeZoneBox"));
        auto *hint = scene.item(QStringLiteral("displayTimeZoneHint"));
        auto *probe = scene.item(QStringLiteral("timeProbe"));
        QVERIFY(box && hint && probe);
        QVERIFY(box->isVisible());
        QTRY_COMPARE(box->property("currentIndex").toInt(), 0);
        QString first;
        QVERIFY(QMetaObject::invokeMethod(box, "textAt", Q_RETURN_ARG(QString, first), Q_ARG(int, 0)));
        QCOMPARE(first, QString::fromUtf8("跟随系统（%1）").arg(mr::Formatters::systemUtcOffset()));
        QVERIFY(!hint->isVisible());
        QCOMPARE(probe->property("text").toString(), systemWallClock(kInstant));

        int taipei = -1;
        QVERIFY(QMetaObject::invokeMethod(box, "indexOfValue", Q_RETURN_ARG(int, taipei),
                                          Q_ARG(QVariant, QVariant(QStringLiteral("Asia/Taipei")))));
        QVERIFY(taipei > 0);
        QString label;
        QVERIFY(QMetaObject::invokeMethod(box, "textAt", Q_RETURN_ARG(QString, label), Q_ARG(int, taipei)));
        QCOMPARE(label, QStringLiteral("UTC+08:00 · Asia/Taipei"));
        QVERIFY(QMetaObject::invokeMethod(box, "activated", Q_ARG(int, taipei)));
        QCOMPARE(scene.settings->displayTimeZone(), QStringLiteral("Asia/Taipei"));
        QCOMPARE(mr::DisplayTimeZone::id(), QStringLiteral("Asia/Taipei"));
        QTRY_COMPARE(box->property("currentIndex").toInt(), taipei);
        QTRY_VERIFY(hint->isVisible());
        QCOMPARE(hint->property("text").toString(), QString::fromUtf8("当前按 UTC+08:00 显示"));
        // Re-rendered in place: no restart, no reload of the page.
        QTRY_COMPARE(probe->property("text").toString(), QStringLiteral("2026-11-21 00:30:00"));

        scene.settings->setDisplayTimeZone(QString());
        QTRY_COMPARE(box->property("currentIndex").toInt(), 0);
        QTRY_VERIFY(!hint->isVisible());
        QTRY_COMPARE(probe->property("text").toString(), systemWallClock(kInstant));
    }

    void theCollectorIsToldOnEveryConnectionAndEveryChange()
    {
        ZoneBackend backend;
        mr::DisplayTimeZoneSync sync(&backend);
        QSignalSpy applied(&sync, &mr::DisplayTimeZoneSync::applied);
        QVERIFY(backend.sent.isEmpty());

        // Connected while following the system: null, and nothing to read again.
        backend.setConnected(true);
        QCOMPARE(backend.sent.size(), 1);
        QVERIFY(backend.sent.constLast().isNull());
        QTRY_VERIFY(sync.acknowledged());
        QCOMPARE(sync.acknowledgedZone(), QString());
        QCOMPARE(applied.count(), 0);

        // A zone chosen while connected is sent and, acknowledged, read again.
        mr::DisplayTimeZone::set(QStringLiteral("Asia/Tokyo"));
        QCOMPARE(backend.sent.size(), 2);
        QCOMPARE(backend.sent.constLast().toString(), QStringLiteral("Asia/Tokyo"));
        QTRY_COMPARE(applied.count(), 1);
        QCOMPARE(sync.acknowledgedZone(), QStringLiteral("Asia/Tokyo"));

        // Disconnected: nothing travels; a change meanwhile goes with the next connection.
        backend.setConnected(false);
        QVERIFY(!sync.acknowledged());
        mr::DisplayTimeZone::set(QStringLiteral("America/New_York"));
        QCOMPARE(backend.sent.size(), 2);
        backend.setConnected(true);
        QCOMPARE(backend.sent.size(), 3);
        QCOMPARE(backend.sent.constLast().toString(), QStringLiteral("America/New_York"));
        QTRY_COMPARE(applied.count(), 2);

        // A Collector older than the message: refused quietly, nothing read again.
        backend.setConnected(false);
        backend.olderCollector = true;
        backend.setConnected(true);
        QCOMPARE(backend.sent.size(), 4);
        mr::DisplayTimeZone::set(QString());
        QCOMPARE(backend.sent.size(), 5);
        QVERIFY(backend.sent.constLast().isNull());
        QTest::qWait(50);
        QCOMPARE(applied.count(), 2);
        QVERIFY(!sync.acknowledged());
    }

    void theHistoryAndItsCalendarAreReadAgainInTheNewZone()
    {
        QTemporaryDir directory;
        CountingBackend backend;
        mr::AppSettings settings(directory.filePath(QStringLiteral("desktop.ini")), nullptr);
        mr::AppController app(&backend, &settings, nullptr, nullptr, mr::TtsService::EngineMode::None);
        QSignalSpy applied(&app, &mr::AppController::displayTimeZoneApplied);
        QSignalSpy changed(&app, &mr::AppController::displayTimeZoneChanged);
        // The offline backend is up from the start: told at once, following the system.
        QTRY_VERIFY(backend.counts.value(QStringLiteral("SetDisplayTimeZone")) >= 1);
        const int toldBefore = backend.counts.value(QStringLiteral("SetDisplayTimeZone"));

        mr::RunListModel *runs = app.runs();
        QVERIFY(runs != nullptr);
        runs->requestCalendarDays(QStringLiteral("2026-09"));
        QTRY_VERIFY(runs->calendarMonthKnown(QStringLiteral("2026-09")));
        QTest::qWait(50);
        const int calendarBefore = backend.counts.value(QStringLiteral("QueryRunCalendarDays"));
        const int queriesBefore = backend.counts.value(QStringLiteral("QueryRuns"));
        QCOMPARE(applied.count(), 0);

        mr::DisplayTimeZone::set(QStringLiteral("Asia/Tokyo"));
        QCOMPARE(changed.count(), 1);
        QCOMPARE(backend.counts.value(QStringLiteral("SetDisplayTimeZone")), toldBefore + 1);
        QTRY_COMPARE(applied.count(), 1);
        // The cached days were dropped and the month on screen asked for again,
        // with the list itself: date-only records moved in the history order.
        QTRY_VERIFY(backend.counts.value(QStringLiteral("QueryRunCalendarDays")) > calendarBefore);
        QTRY_VERIFY(runs->calendarMonthKnown(QStringLiteral("2026-09")));
        QVERIFY(backend.counts.value(QStringLiteral("QueryRuns")) > queriesBefore);
    }

    void anAppliedHistoryDateRangeTakesTheNewZonesBounds()
    {
        mr::DisplayTimeZone::set(QStringLiteral("Asia/Tokyo"));
        QTemporaryDir directory;
        mr::MockBackend backend;
        mr::AppSettings settings(directory.filePath(QStringLiteral("desktop.ini")), nullptr);
        mr::AppController app(&backend, &settings, nullptr, nullptr, mr::TtsService::EngineMode::None);
        mr::Formatters formatters;
        mr::JobCatalog jobs;
        mr::RoleCatalog roles;
        mr::RunFormValidator validator;
        QQmlEngine engine;
        auto *context = engine.rootContext();
        context->setContextProperty(QStringLiteral("App"), &app);
        context->setContextProperty(QStringLiteral("Fmt"), &formatters);
        context->setContextProperty(QStringLiteral("Jobs"), &jobs);
        context->setContextProperty(QStringLiteral("Roles"), &roles);
        context->setContextProperty(QStringLiteral("RunForm"), &validator);
        context->setContextProperty(QStringLiteral("Settings"), &settings);
        context->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QQmlComponent component(&engine);
        component.setData(QByteArrayLiteral(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    visible: true; width: 1280; height: 800; color: Theme.surface
    HistoryPage { objectName: "historyUnderTest"; anchors.fill: parent }
})"), QUrl());
        std::unique_ptr<QObject> root(component.create());
        QVERIFY2(root, qPrintable(component.errorString()));
        auto *window = qobject_cast<QQuickWindow *>(root.get());
        QVERIFY(window);
        QQuickItem *page = findVisualItem(window->contentItem(), QStringLiteral("historyUnderTest"));
        QVERIFY(page);
        auto *from = page->property("fromField").value<QObject *>();
        auto *to = page->property("toField").value<QObject *>();
        QVERIFY(from && to);

        // 2026-09-15 to 2026-09-15, picked in Tokyo.
        from->setProperty("text", QStringLiteral("2026-09-15"));
        to->setProperty("text", QStringLiteral("2026-09-15"));
        QVERIFY(QMetaObject::invokeMethod(page, "applyFilter"));
        QTRY_COMPARE(app.historyFilter().value(QStringLiteral("from_utc")).toString(),
                     QStringLiteral("2026-09-14T15:00:00.000Z"));
        QCOMPARE(app.historyFilter().value(QStringLiteral("to_utc")).toString(),
                 QStringLiteral("2026-09-15T14:59:59.999Z"));

        // The same days in New York: the fields keep them, the bounds move.
        mr::DisplayTimeZone::set(QStringLiteral("America/New_York"));
        QTRY_COMPARE(app.historyFilter().value(QStringLiteral("from_utc")).toString(),
                     QStringLiteral("2026-09-15T04:00:00.000Z"));
        QCOMPARE(app.historyFilter().value(QStringLiteral("to_utc")).toString(),
                 QStringLiteral("2026-09-16T03:59:59.999Z"));
        QCOMPARE(from->property("text").toString(), QStringLiteral("2026-09-15"));
        QCOMPARE(to->property("text").toString(), QStringLiteral("2026-09-15"));
    }
};

} // namespace

MR_DESKTOP_TEST_SUITE(DisplayTimeZone)
{
    // Never run against the user's own Collector, database or serve lease - see
    // TestCollectorGuard.h. Must precede any construction.
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication app(argc, argv);
    DisplayTimeZoneTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "DisplayTimeZoneTests.moc"
