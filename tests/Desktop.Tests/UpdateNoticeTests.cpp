// ---------------------------------------------------------------------------
// tst_updatenotice - 检查新版本 (notify only), desktop half, in shipping QML.
//
// What it pins:
//   * the `update` object on the collector status is adopted as it arrives, and
//     a Collector that sends none leaves the controller unavailable;
//   * the Desktop never compares two versions: the Collector's verdict is what
//     decides, even when it disagrees with a plain string comparison;
//   * only an https://github.com/ address without credentials and on the
//     default port is ever handed to the browser;
//   * 忽略此版本 is remembered for exactly one version, and a newer one raises
//     the banner again;
//   * no sentence on the banner carries an address or a wire token;
//   * the banner is on 总览 only while there is an update to show, and it offers
//     both buttons;
//   * 检查新版本并提示 writes update_check_enabled through UpdateCaptureSettings;
//   * the first-run disclosure names three kinds of network access.
// ---------------------------------------------------------------------------

#include "TestCollectorGuard.h"
#include "AppController.h"
#include "AppSettings.h"
#include "Formatters.h"
#include "IBackend.h"
#include "JobCatalog.h"
#include "MockBackend.h"
#include "RoleCatalog.h"
#include "UpdateController.h"

#include <QDir>
#include <QFont>
#include <QFontDatabase>
#include <QGuiApplication>
#include <QJsonObject>
#include <QJsonValue>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickStyle>
#include <QRegularExpression>
#include <QStandardPaths>
#include <QTest>
#include <QUrl>

#include <memory>

namespace {

constexpr auto kReleaseUrl =
    "https://github.com/Harendotes-Tang/MentorRouletteRecorder/releases/latest";
constexpr auto kCheckedAt = "2026-09-17T02:00:00.000Z";

QQuickItem *findVisualItem(QQuickItem *root, const QString &name)
{
    if (!root)
        return nullptr;
    if (root->objectName() == name)
        return root;
    for (auto *child : root->childItems()) {
        if (auto *found = findVisualItem(child, name))
            return found;
    }
    return nullptr;
}

void collectText(QQuickItem *item, QStringList &texts)
{
    if (!item)
        return;
    const QVariant text = item->property("text");
    if (text.isValid() && !text.toString().isEmpty())
        texts.append(text.toString());
    for (auto *child : item->childItems())
        collectText(child, texts);
}

/// $defs/UpdateStatus as the Collector reports it. \a latest empty means the
/// check ran and found nothing newer.
QJsonObject updateStatus(bool updateAvailable, const QString &latest,
                         const QString &url = QLatin1String(kReleaseUrl))
{
    return {{QStringLiteral("enabled"), true},
            {QStringLiteral("update_available"), updateAvailable},
            {QStringLiteral("latest_version"),
             latest.isEmpty() ? QJsonValue(QJsonValue::Null) : QJsonValue(latest)},
            {QStringLiteral("release_url"),
             url.isEmpty() ? QJsonValue(QJsonValue::Null) : QJsonValue(url)},
            {QStringLiteral("last_checked_at_utc"), QLatin1String(kCheckedAt)},
            {QStringLiteral("last_outcome"), QStringLiteral("OK")}};
}

/// Answers GetStatus with one hand-written collector status; everything else is
/// empty, so nothing but the update projection is under test.
class StatusBackend final : public mr::IBackend
{
public:
    QJsonObject status;

    QString backendName() const override { return QStringLiteral("mock"); }
    bool isConnected() const override { return true; }

    mr::BackendReply *request(const QString &type, const QJsonObject & = {}) override
    {
        auto *reply = new mr::BackendReply(type, type, this);
        if (type == QLatin1String("GetStatus"))
            reply->succeed(status);
        else
            reply->succeed({});
        return reply;
    }
};

/// One controller on a hand-written status, torn down in dependency order.
struct ControllerScene
{
    std::unique_ptr<StatusBackend> backend = std::make_unique<StatusBackend>();
    std::unique_ptr<mr::AppSettings> settings;
    std::unique_ptr<mr::AppController> app;
    QStringList opened;
    bool openerSucceeds = true;

    ~ControllerScene() { app.reset(); }

    void open(const QJsonObject &update, bool persistent = false)
    {
        backend->status = collectorStatus(update);
        if (persistent) {
            settings = std::make_unique<mr::AppSettings>();
            settings->setDismissedUpdateVersion(QString());
        }
        app = std::make_unique<mr::AppController>(backend.get(), settings.get());
        controller()->setUrlOpener([this](const QUrl &url) {
            opened.append(url.toString());
            return openerSucceeds;
        });
    }

    /// Re-answer GetStatus with a different `update` object.
    void resend(const QJsonObject &update)
    {
        backend->status = collectorStatus(update);
        app->refreshStatus();
    }

    /// A GetStatus payload with no `update` object at all, as an older
    /// Collector sends it.
    void openWithoutUpdate()
    {
        backend->status = collectorStatus({});
        app = std::make_unique<mr::AppController>(backend.get(), nullptr);
    }

    mr::UpdateController *controller() const { return app->update(); }

private:
    static QJsonObject collectorStatus(const QJsonObject &update)
    {
        QJsonObject status{{QStringLiteral("collector_version"), QStringLiteral("0.9.1")},
                           {QStringLiteral("database_ready"), true}};
        if (!update.isEmpty())
            status.insert(QStringLiteral("update"), update);
        return status;
    }
};

/// One shipping page on the deterministic backend. The browser is replaced
/// before anything is clicked: a test never opens github.com.
struct PageScene
{
    std::unique_ptr<mr::MockBackend> backend = std::make_unique<mr::MockBackend>();
    std::unique_ptr<mr::AppSettings> settings;
    std::unique_ptr<mr::Formatters> formatters;
    std::unique_ptr<mr::JobCatalog> jobs;
    std::unique_ptr<mr::RoleCatalog> roles;
    std::unique_ptr<QQmlEngine> engine;
    std::unique_ptr<mr::AppController> app;
    std::unique_ptr<QObject> root;
    QQuickItem *page = nullptr;
    QStringList opened;

    ~PageScene()
    {
        root.reset();
        engine.reset();
        app.reset();
    }

    bool open(const QString &element, bool updateAvailable)
    {
        backend->setUpdateAvailable(updateAvailable);
        settings = std::make_unique<mr::AppSettings>();
        settings->setDismissedUpdateVersion(QString());
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
        %1 { objectName: "scenePage"; Layout.fillWidth: true; Layout.fillHeight: true }
    }
})").arg(element).toUtf8(), QUrl());
        root.reset(component.create());
        if (!root) {
            qWarning("%s", qPrintable(component.errorString()));
            return false;
        }
        page = qobject_cast<QQuickItem *>(root->findChild<QObject *>(QStringLiteral("scenePage")));
        if (!page)
            return false;
        app->update()->setUrlOpener([this](const QUrl &url) {
            opened.append(url.toString());
            return true;
        });
        return true;
    }

    QQuickItem *item(const QString &name) const { return findVisualItem(page, name); }
    bool shows(const QString &name) const
    {
        auto *found = item(name);
        return found && found->isVisible();
    }
};

} // namespace

class UpdateNoticeTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void initTestCase()
    {
        const QString qmlRoot = QString::fromUtf8(MR_DESKTOP_QML_DIR);
        QVERIFY(QDir(qmlRoot).exists());
        qmlRegisterSingletonType(QUrl::fromLocalFile(qmlRoot + QStringLiteral("/Theme.qml")),
                                 "MentorRecorder", 1, 0, "Theme");
        for (const auto &directory : {QStringLiteral("/components"), QStringLiteral("/dialogs"),
                                      QStringLiteral("/pages"), QStringLiteral("/charts")}) {
            for (const auto &file :
                 QDir(qmlRoot + directory).entryList({QStringLiteral("*.qml")}, QDir::Files)) {
                const QByteArray name = file.chopped(4).toUtf8();
                qmlRegisterType(QUrl::fromLocalFile(qmlRoot + directory + QLatin1Char('/') + file),
                                "MentorRecorder", 1, 0, name.constData());
            }
        }
    }

    void adoptsTheUpdateObjectFromTheCollectorStatus()
    {
        ControllerScene scene;
        scene.open(updateStatus(true, QStringLiteral("9.9.9")));
        auto *update = scene.controller();
        QTRY_VERIFY(update->available());
        QVERIFY(update->enabled());
        QVERIFY(update->updateAvailable());
        QCOMPARE(update->latestVersion(), QStringLiteral("9.9.9"));
        QCOMPARE(update->releaseUrl(), QLatin1String(kReleaseUrl));
        QCOMPARE(update->lastCheckedAtUtc(), QLatin1String(kCheckedAt));
        QVERIFY(update->headline().contains(QStringLiteral("9.9.9")));
        QVERIFY(!update->currentVersion().isEmpty());
        QVERIFY(update->detail().contains(update->currentVersion()));
    }

    void aStatusWithoutAnUpdateObjectLeavesTheControllerUnavailable()
    {
        ControllerScene scene;
        scene.openWithoutUpdate();
        auto *update = scene.controller();
        QTest::qWait(50);
        QVERIFY(!update->available());
        QVERIFY(!update->updateAvailable());
        QVERIFY(!update->enabled());
        QVERIFY(update->headline().isEmpty());
        QVERIFY(update->latestVersion().isEmpty());
        QVERIFY(update->releaseUrl().isEmpty());
        QVERIFY(!update->dismissed());
    }

    void neverComparesVersionsItself()
    {
        // The Collector says there is nothing newer while naming a version that
        // sorts far above this build; the banner stays down, because comparing
        // the two is not this process's job.
        ControllerScene scene;
        scene.open(updateStatus(false, QStringLiteral("99.99.99")));
        QTRY_VERIFY(scene.controller()->available());
        QVERIFY(!scene.controller()->updateAvailable());
        QVERIFY(scene.controller()->headline().isEmpty());

        // And the other way round: a version equal to this build's raises the
        // banner all the same, because the Collector said so.
        scene.resend(updateStatus(true, scene.controller()->currentVersion()));
        QTRY_VERIFY(scene.controller()->updateAvailable());
        QVERIFY(scene.controller()->headline().contains(scene.controller()->currentVersion()));
    }

    void opensOnlyAGithubHttpsAddress()
    {
        QVERIFY(mr::UpdateController::isReleaseUrl(QUrl(QLatin1String(kReleaseUrl))));

        ControllerScene scene;
        scene.open(updateStatus(true, QStringLiteral("9.9.9")));
        QTRY_VERIFY(scene.controller()->updateAvailable());
        scene.controller()->openReleasePage();
        QCOMPARE(scene.opened, QStringList{QLatin1String(kReleaseUrl)});
        QTRY_VERIFY(!scene.app->toastMessage().isEmpty());
        QVERIFY(scene.app->toastMessage().contains(QString::fromUtf8("已在系统浏览器中打开")));
    }

    void refusesAnAddressWithUserInfoAPortOrAnotherHost_data()
    {
        QTest::addColumn<QString>("url");
        QTest::newRow("user-info")
            << "https://user@github.com/Harendotes-Tang/MentorRouletteRecorder/releases/latest";
        QTest::newRow("credentials")
            << "https://user:secret@github.com/Harendotes-Tang/MentorRouletteRecorder/releases";
        QTest::newRow("port")
            << "https://github.com:8443/Harendotes-Tang/MentorRouletteRecorder/releases/latest";
        QTest::newRow("another-host")
            << "https://github.com.example.invalid/Harendotes-Tang/MentorRouletteRecorder";
        QTest::newRow("plain-http")
            << "http://github.com/Harendotes-Tang/MentorRouletteRecorder/releases/latest";
        QTest::newRow("not-a-url") << "javascript:void(0)";
    }

    void refusesAnAddressWithUserInfoAPortOrAnotherHost()
    {
        QFETCH(QString, url);
        QVERIFY(!mr::UpdateController::isReleaseUrl(QUrl(url)));

        // A refused address is never even held, so no view can offer it, and
        // pressing 打开下载页 opens nothing.
        ControllerScene scene;
        scene.open(updateStatus(true, QStringLiteral("9.9.9"), url));
        QTRY_VERIFY(scene.controller()->available());
        QVERIFY(scene.controller()->releaseUrl().isEmpty());
        scene.controller()->openReleasePage();
        QVERIFY(scene.opened.isEmpty());
        QTRY_VERIFY(!scene.app->toastMessage().isEmpty());
    }

    void dismissIsRememberedPerVersionAndANewerVersionRaisesItAgain()
    {
        ControllerScene scene;
        scene.open(updateStatus(true, QStringLiteral("9.9.9")), true);
        auto *update = scene.controller();
        QTRY_VERIFY(update->updateAvailable());
        QVERIFY(!update->dismissed());

        update->dismiss();
        QVERIFY(update->dismissed());
        QCOMPARE(scene.settings->dismissedUpdateVersion(), QStringLiteral("9.9.9"));

        // The same version stays dismissed across a restart.
        scene.resend(updateStatus(true, QStringLiteral("9.9.9")));
        QVERIFY(update->dismissed());

        // A later release is a different question and is asked again.
        scene.resend(updateStatus(true, QStringLiteral("9.9.10")));
        QTRY_VERIFY(!update->dismissed());
        QVERIFY(update->updateAvailable());

        scene.settings->setDismissedUpdateVersion(QString());
    }

    void theHeadlineHasNoUrlAndNoHex()
    {
        ControllerScene scene;
        scene.open(updateStatus(true, QStringLiteral("9.9.9")));
        QTRY_VERIFY(scene.controller()->updateAvailable());
        // Player copy: no address, no wire token, no Latin word anywhere.
        static const QRegularExpression latin(QStringLiteral("[A-Za-z]"));
        for (const QString &text : {scene.controller()->headline(), scene.controller()->detail()}) {
            QVERIFY2(!text.contains(latin),
                     qPrintable(QStringLiteral("update copy leaks a Latin token: ") + text));
        }
    }

    void theDashboardShowsTheBannerOnlyWhenAnUpdateIsAvailable()
    {
        PageScene quiet;
        QVERIFY(quiet.open(QStringLiteral("DashboardPage"), false));
        QTRY_VERIFY(quiet.app->update()->available());
        QVERIFY(!quiet.app->update()->updateAvailable());
        QVERIFY(!quiet.shows(QStringLiteral("updateNotice")));

        PageScene raised;
        QVERIFY(raised.open(QStringLiteral("DashboardPage"), true));
        QTRY_VERIFY(raised.shows(QStringLiteral("updateNotice")));
        auto *headline = raised.item(QStringLiteral("updateNoticeHeadline"));
        QVERIFY(headline);
        QCOMPARE(headline->property("text").toString(), raised.app->update()->headline());
    }

    void theBannerOffersOpenAndIgnore()
    {
        PageScene scene;
        QVERIFY(scene.open(QStringLiteral("DashboardPage"), true));
        QTRY_VERIFY(scene.shows(QStringLiteral("openReleasePageButton")));
        QVERIFY(scene.shows(QStringLiteral("dismissUpdateButton")));

        QVERIFY(QMetaObject::invokeMethod(scene.item(QStringLiteral("openReleasePageButton")),
                                          "clicked"));
        QTRY_COMPARE(scene.opened.size(), 1);
        QVERIFY(scene.opened.constFirst().startsWith(QStringLiteral("https://github.com/")));

        QVERIFY(QMetaObject::invokeMethod(scene.item(QStringLiteral("dismissUpdateButton")),
                                          "clicked"));
        QTRY_VERIFY(!scene.shows(QStringLiteral("updateNotice")));
        scene.settings->setDismissedUpdateVersion(QString());
    }

    void theSettingsToggleWritesUpdateCheckEnabled()
    {
        PageScene scene;
        QVERIFY(scene.open(QStringLiteral("SettingsGeneralTab"), false));
        QTRY_VERIFY(scene.app->captureSettingsLoaded());
        auto *row = scene.item(QStringLiteral("updateCheckToggle"));
        QVERIFY(row);
        QVERIFY(row->isVisible());
        QVERIFY(row->property("checked").toBool());

        QVERIFY(QMetaObject::invokeMethod(row, "toggled", Q_ARG(bool, false)));
        // Debounced by AppController before it reaches the wire.
        QTRY_VERIFY_WITH_TIMEOUT(
            scene.backend->lastCaptureSettingsUpdate().contains(
                QStringLiteral("update_check_enabled")),
            3000);
        QCOMPARE(scene.backend->lastCaptureSettingsUpdate()
                     .value(QStringLiteral("update_check_enabled")),
                 QJsonValue(false));
        QTRY_VERIFY(!row->property("checked").toBool());
    }

    void theDisclosureNamesThreeKindsOfNetworkAccess()
    {
        PageScene scene;
        QVERIFY(scene.open(QStringLiteral("DashboardPage"), false));
        QQmlComponent component(scene.engine.get());
        component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 900; height: 900; visible: true
    DisclosureDialog { id: notice; objectName: "disclosure"; anchors.centerIn: parent }
    Component.onCompleted: notice.openDialog()
})", QUrl());
        const std::unique_ptr<QObject> window(component.create());
        QVERIFY2(window, qPrintable(component.errorString()));
        auto *dialog = window->findChild<QObject *>(QStringLiteral("disclosure"));
        QVERIFY(dialog);
        QTRY_VERIFY(dialog->property("visible").toBool());

        QStringList texts;
        collectText(dialog->property("contentItem").value<QQuickItem *>(), texts);
        const QString all = texts.join(QLatin1Char('\n'));
        QVERIFY(all.contains(QString::fromUtf8("只有三种联网")));
        QVERIFY(all.contains(QString::fromUtf8("联网一：")));
        QVERIFY(all.contains(QString::fromUtf8("联网二：")));
        QVERIFY(all.contains(QString::fromUtf8("联网三：检查新版本")));
        QVERIFY(all.contains(QString::fromUtf8("设置 → 通用 → 更新")));
        // The acknowledgement is only ever valid for the text it was given for.
        QVERIFY(!all.contains(QString::fromUtf8("只有两种联网")));
        QVERIFY(!all.contains(QString::fromUtf8("没有更新检查")));
        QVERIFY(mr::AppSettings::kDisclosureVersion >= 4);
    }
};

int main(int argc, char **argv)
{
    // Never run against the user's own Collector, database or serve lease - see
    // TestCollectorGuard.h. Must precede any construction.
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication app(argc, argv);
#ifdef Q_OS_WIN
    // The offscreen platform finds no system fonts; the copy under test is CJK.
    QStringList cjkFamilies;
    const QString windowsDir = qEnvironmentVariable("WINDIR", QStringLiteral("C:/Windows"));
    for (const char *file : {"Fonts/msyh.ttc", "Fonts/simhei.ttf"}) {
        const int id = QFontDatabase::addApplicationFont(QDir(windowsDir).filePath(QString::fromLatin1(file)));
        for (const QString &family : QFontDatabase::applicationFontFamilies(id)) {
            if (!cjkFamilies.contains(family))
                cjkFamilies.append(family);
        }
    }
    if (!cjkFamilies.isEmpty()) {
        QFont testFont;
        testFont.setFamilies(cjkFamilies);
        app.setFont(testFont);
    }
#endif
    UpdateNoticeTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "UpdateNoticeTests.moc"
