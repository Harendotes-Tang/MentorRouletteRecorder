// ---------------------------------------------------------------------------
// tst_updatenotice - 检查新版本 (notify only), desktop half, in shipping QML.
//
// What it pins:
//   * the version shown on screen is the whole version from Directory.Build.props,
//     prerelease suffix included, so a test build cannot look like the release;
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
//   * 检查更新 sends CheckUpdateNow, adopts the `update` object the answer
//     carries and says exactly one sentence per outcome - never a wire token -
//     while `checking` holds the buttons down for the one request in flight;
//   * the settings panel and the 关于 page offer that button under the same
//     rule, and turn it into 打开下载页 once there is something to download;
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
#include <QFile>
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
#include <QPointer>
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

/// $defs/UpdateStatus with an explicit last_outcome and 开关 value, for the
/// answers a check can come back with.
QJsonObject updateStatus(bool updateAvailable, const QString &latest,
                         const QString &lastOutcome, bool enabled)
{
    QJsonObject status = updateStatus(updateAvailable, latest);
    status.insert(QStringLiteral("last_outcome"), lastOutcome);
    status.insert(QStringLiteral("enabled"), enabled);
    return status;
}

/// Answers GetStatus with one hand-written collector status and CheckUpdateNow
/// with one hand-written answer; everything else is empty, so nothing but the
/// update projection is under test.
class StatusBackend final : public mr::IBackend
{
public:
    QJsonObject status;
    /// The CheckUpdateNow answer, verbatim.
    QJsonObject checkAnswer;
    /// Answer CheckUpdateNow with an error envelope instead.
    bool checkFails = false;
    /// Hold the answer back until releaseCheck() runs, so a test can observe
    /// the request while it is still out.
    bool holdCheck = false;
    /// CheckUpdateNow requests this backend was asked for.
    int checkCount = 0;

    QString backendName() const override { return QStringLiteral("mock"); }
    bool isConnected() const override { return true; }

    mr::BackendReply *request(const QString &type, const QJsonObject & = {}) override
    {
        auto *reply = new mr::BackendReply(type, type, this);
        if (type == QLatin1String("GetStatus")) {
            reply->succeed(status);
        } else if (type == QLatin1String("CheckUpdateNow")) {
            ++checkCount;
            if (holdCheck)
                m_held = reply;
            else
                answerCheck(reply);
        } else {
            reply->succeed({});
        }
        return reply;
    }

    void releaseCheck()
    {
        if (m_held)
            answerCheck(m_held);
        m_held = nullptr;
    }

private:
    void answerCheck(mr::BackendReply *reply)
    {
        if (checkFails)
            reply->fail(QStringLiteral("ERR_UNKNOWN_MESSAGE"),
                        QString::fromUtf8("当前采集器不支持该消息。"));
        else
            reply->succeed(checkAnswer);
    }

    QPointer<mr::BackendReply> m_held;
};

/// One CheckUpdateNow answer:  outcome plus the `update` object it carries.
QJsonObject checkAnswer(const QString &outcome, const QJsonObject &update)
{
    return {{QStringLiteral("outcome"), outcome}, {QStringLiteral("update"), update}};
}

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

    /// What CheckUpdateNow will answer on the next 检查更新.
    void willAnswer(const QString &outcome, const QJsonObject &update)
    {
        backend->checkAnswer = checkAnswer(outcome, update);
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

    // The version the title bar ("v" + App.appVersion) and the 关于 page show is
    // MR_APP_VERSION, stamped by CMake from Directory.Build.props. A test build is
    // X.Y.Z-beta.N and has to be recognisable as one on screen, so the suffix must
    // survive that route; the numeric-only shapes (VERSIONINFO, AssemblyVersion) are
    // checked by scripts/package.ps1 against the built binaries. Read from the props
    // file rather than from the same CMake variable, which would agree with itself
    // even if the suffix had been dropped on the way here.
    void theVersionOnScreenIsTheWholeVersionIncludingItsPrereleaseSuffix()
    {
        QFile props(QString::fromUtf8(MR_SOURCE_DIR) + QStringLiteral("/Directory.Build.props"));
        QVERIFY2(props.open(QIODevice::ReadOnly | QIODevice::Text), qPrintable(props.fileName()));
        const QString text = QString::fromUtf8(props.readAll());

        const QRegularExpressionMatch prefix =
            QRegularExpression(QStringLiteral("<VersionPrefix>([^<]*)</VersionPrefix>"))
                .match(text);
        QVERIFY2(prefix.hasMatch(), "Directory.Build.props carries no <VersionPrefix>");
        const QRegularExpressionMatch suffix =
            QRegularExpression(QStringLiteral("<VersionSuffix>([^<]*)</VersionSuffix>"))
                .match(text);

        QString expected = prefix.captured(1).trimmed();
        QVERIFY2(QRegularExpression(QStringLiteral("^\\d+\\.\\d+\\.\\d+$"))
                     .match(expected).hasMatch(),
                 qPrintable(expected));
        if (suffix.hasMatch() && !suffix.captured(1).trimmed().isEmpty())
            expected += QLatin1Char('-') + suffix.captured(1).trimmed();

        QCOMPARE(mr::UpdateController::currentVersion(), expected);
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
        // Player copy: no address, no wire token, no Latin word anywhere - except
        // inside the two version numbers the sentences quote, which the reader is
        // meant to see and which carry a Latin prerelease label on a test build
        // (1.4.0-beta.1). They are removed first, so everything else still has to
        // be free of Latin: an address, a hex string or an outcome token would be
        // caught exactly as before.
        static const QRegularExpression latin(QStringLiteral("[A-Za-z]"));
        for (const QString &text : {scene.controller()->headline(), scene.controller()->detail()}) {
            QString rest = text;
            for (const QString &version :
                 {scene.controller()->currentVersion(), scene.controller()->latestVersion()}) {
                if (!version.isEmpty())
                    rest.remove(version);
            }
            QVERIFY2(!rest.contains(latin),
                     qPrintable(QStringLiteral("update copy leaks a Latin token: ") + text));
        }
    }

    // -- 检查更新 -------------------------------------------------------

    void checkNowAdoptsTheAnswerAndNamesTheNewVersion()
    {
        ControllerScene scene;
        scene.open(updateStatus(false, QString()));
        QTRY_VERIFY(scene.controller()->available());
        QVERIFY(scene.controller()->canCheck());
        QVERIFY(!scene.controller()->checking());

        scene.willAnswer(QStringLiteral("CHECKED"),
                         updateStatus(true, QStringLiteral("9.9.9")));
        scene.controller()->checkNow();
        QTRY_COMPARE(scene.backend->checkCount, 1);
        // The answer's `update` object is adopted exactly as a status would be.
        QTRY_VERIFY(scene.controller()->updateAvailable());
        QCOMPARE(scene.controller()->latestVersion(), QStringLiteral("9.9.9"));
        QCOMPARE(scene.controller()->releaseUrl(), QLatin1String(kReleaseUrl));
        QCOMPARE(scene.controller()->lastCheckedAtUtc(), QLatin1String(kCheckedAt));
        QVERIFY(!scene.controller()->checking());
        QTRY_VERIFY(!scene.app->toastMessage().isEmpty());
        QCOMPARE(scene.app->toastMessage(),
                 QString::fromUtf8("有新版本 9.9.9，可以点「打开下载页」下载。"));
        // Nothing was opened: the check only checks.
        QVERIFY(scene.opened.isEmpty());
    }

    void checkNowSaysThisIsTheNewestVersionWhenTheCheckFoundNothing()
    {
        ControllerScene scene;
        scene.open(updateStatus(false, QString()));
        QTRY_VERIFY(scene.controller()->canCheck());

        scene.willAnswer(QStringLiteral("CHECKED"),
                         updateStatus(false, QString(), QStringLiteral("OK"), true));
        scene.controller()->checkNow();
        QTRY_VERIFY(!scene.app->toastMessage().isEmpty());
        QCOMPARE(scene.app->toastMessage(),
                 QString::fromUtf8("已是最新版本（%1）。")
                     .arg(scene.controller()->currentVersion()));
        QVERIFY(!scene.controller()->updateAvailable());
    }

    void checkNowSaysItDidNotSucceedForEveryOtherOutcome_data()
    {
        QTest::addColumn<QString>("lastOutcome");
        QTest::newRow("not-found") << "NOT_FOUND";
        QTest::newRow("timeout") << "TIMEOUT";
        QTest::newRow("dns-or-connect") << "DNS_OR_CONNECT";
        QTest::newRow("http-error") << "HTTP_ERROR";
        // A token a later Collector may add reads the same way here.
        QTest::newRow("unknown-to-this-build") << "SOMETHING_NEW";
    }

    void checkNowSaysItDidNotSucceedForEveryOtherOutcome()
    {
        QFETCH(QString, lastOutcome);
        ControllerScene scene;
        scene.open(updateStatus(false, QString()));
        QTRY_VERIFY(scene.controller()->canCheck());

        scene.willAnswer(QStringLiteral("CHECKED"),
                         updateStatus(false, QString(), lastOutcome, true));
        scene.controller()->checkNow();
        QTRY_VERIFY(!scene.app->toastMessage().isEmpty());
        const QString toast = scene.app->toastMessage();
        QCOMPARE(toast,
                 QString::fromUtf8("没有检查成功（网络不通或发布页暂时不可用），稍后再试。"));
        // Never the wire token, and never a Latin word of any kind.
        QVERIFY(!toast.contains(lastOutcome));
        static const QRegularExpression latin(QStringLiteral("[A-Za-z]"));
        QVERIFY(!toast.contains(latin));
    }

    void checkNowSaysTheSettingIsOff()
    {
        ControllerScene scene;
        scene.open(updateStatus(false, QString()));
        QTRY_VERIFY(scene.controller()->canCheck());

        scene.willAnswer(QStringLiteral("DISABLED"),
                         updateStatus(false, QString(), QStringLiteral("OK"), false));
        scene.controller()->checkNow();
        QTRY_VERIFY(!scene.app->toastMessage().isEmpty());
        QCOMPARE(scene.app->toastMessage(),
                 QString::fromUtf8("「检查新版本并提示」已关闭，打开后才能检查。"));
        // The answer said the switch is off, so the button follows it down.
        QTRY_VERIFY(!scene.controller()->enabled());
        QVERIFY(!scene.controller()->canCheck());
    }

    void checkNowSaysTheMachineDisabledTheCheck()
    {
        ControllerScene scene;
        scene.open(updateStatus(false, QString()));
        QTRY_VERIFY(scene.controller()->canCheck());

        scene.willAnswer(QStringLiteral("BLOCKED"),
                         updateStatus(false, QString(), QStringLiteral("OK"), true));
        scene.controller()->checkNow();
        QTRY_VERIFY(!scene.app->toastMessage().isEmpty());
        QCOMPARE(scene.app->toastMessage(),
                 QString::fromUtf8("本机已通过环境变量禁用更新检查。"));
    }

    void aFailedCheckRequestSaysSoAndChangesNothing()
    {
        ControllerScene scene;
        scene.open(updateStatus(false, QString()));
        QTRY_VERIFY(scene.controller()->canCheck());

        scene.backend->checkFails = true;
        scene.controller()->checkNow();
        QTRY_VERIFY(!scene.app->toastMessage().isEmpty());
        QCOMPARE(scene.app->toastMessage(),
                 QString::fromUtf8("检查失败，请稍后再试。"));
        // A refusal is not a verdict: the projection is left exactly as it was.
        QVERIFY(!scene.controller()->checking());
        QVERIFY(scene.controller()->available());
        QVERIFY(!scene.controller()->updateAvailable());
        QVERIFY(scene.controller()->canCheck());
    }

    void checkingHoldsTheButtonDownForTheOneRequestInFlight()
    {
        ControllerScene scene;
        scene.open(updateStatus(false, QString()));
        QTRY_VERIFY(scene.controller()->canCheck());

        scene.backend->holdCheck = true;
        scene.willAnswer(QStringLiteral("CHECKED"),
                         updateStatus(false, QString(), QStringLiteral("OK"), true));
        scene.controller()->checkNow();
        QVERIFY(scene.controller()->checking());
        QVERIFY(!scene.controller()->canCheck());
        QCOMPARE(scene.backend->checkCount, 1);

        // A second press while the first is out sends nothing.
        scene.controller()->checkNow();
        QCOMPARE(scene.backend->checkCount, 1);
        QVERIFY(scene.app->toastMessage().isEmpty());

        scene.backend->releaseCheck();
        QTRY_VERIFY(!scene.controller()->checking());
        QVERIFY(scene.controller()->canCheck());
        QTRY_VERIFY(!scene.app->toastMessage().isEmpty());
    }

    void withoutAnUpdateProjectionTheCheckButtonCannotBePressed()
    {
        // An older Collector sends no `update` object, and is also too old to
        // answer CheckUpdateNow: the button stays down and sends nothing.
        ControllerScene scene;
        scene.openWithoutUpdate();
        QTest::qWait(50);
        QVERIFY(!scene.controller()->canCheck());
        scene.controller()->checkNow();
        QTest::qWait(50);
        QCOMPARE(scene.backend->checkCount, 0);
    }

    void theSettingsPanelOffersCheckNowAndTurnsItIntoTheDownloadButton()
    {
        PageScene quiet;
        QVERIFY(quiet.open(QStringLiteral("SettingsGeneralTab"), false));
        QTRY_VERIFY(quiet.app->update()->available());
        auto *button = quiet.item(QStringLiteral("checkUpdateNowButton"));
        QVERIFY(button);
        QTRY_VERIFY(button->isVisible());
        QCOMPARE(button->property("text").toString(), QString::fromUtf8("检查更新"));
        QTRY_VERIFY(button->property("enabled").toBool());

        // One press asks the deterministic backend, which answers CHECKED with
        // the very `update` object its status carries.
        QVERIFY(QMetaObject::invokeMethod(button, "clicked"));
        QTRY_VERIFY(!quiet.app->toastMessage().isEmpty());
        QCOMPARE(quiet.app->toastMessage(),
                 QString::fromUtf8("已是最新版本（%1）。")
                     .arg(quiet.app->update()->currentVersion()));
        // The check never opens a browser.
        QVERIFY(quiet.opened.isEmpty());

        // With something to download, the same place becomes the download button.
        PageScene raised;
        QVERIFY(raised.open(QStringLiteral("SettingsGeneralTab"), true));
        QTRY_VERIFY(raised.app->update()->updateAvailable());
        auto *download = raised.item(QStringLiteral("checkUpdateNowButton"));
        QVERIFY(download);
        QTRY_COMPARE(download->property("text").toString(),
                     QString::fromUtf8("打开下载页"));
        QVERIFY(QMetaObject::invokeMethod(download, "clicked"));
        QTRY_COMPARE(raised.opened.size(), 1);
        QVERIFY(raised.opened.constFirst().startsWith(QStringLiteral("https://github.com/")));
        // And the 最近检查 line is there as soon as the Collector named a time.
        QVERIFY(raised.shows(QStringLiteral("updateCheckStatusText")));
    }

    void theSettingsCheckButtonIsDisabledWhileTheCheckIsOut()
    {
        PageScene scene;
        QVERIFY(scene.open(QStringLiteral("SettingsGeneralTab"), false));
        QTRY_VERIFY(scene.app->update()->canCheck());
        auto *button = scene.item(QStringLiteral("checkUpdateNowButton"));
        QVERIFY(button);
        QTRY_VERIFY(button->property("enabled").toBool());

        QVERIFY(QMetaObject::invokeMethod(button, "clicked"));
        // The mock answers on the next event-loop turn, so the disabled state
        // is asserted before the loop is given back.
        QVERIFY(scene.app->update()->checking());
        QVERIFY(!button->property("enabled").toBool());
        QTRY_VERIFY(!scene.app->update()->checking());
        QTRY_VERIFY(button->property("enabled").toBool());
    }

    void theAboutPageOffersCheckNowOnlyWhileThereIsNothingToDownload()
    {
        PageScene quiet;
        QVERIFY(quiet.open(QStringLiteral("SettingsAboutTab"), false));
        QTRY_VERIFY(quiet.shows(QStringLiteral("aboutCheckUpdateButton")));
        QVERIFY(!quiet.shows(QStringLiteral("aboutOpenReleasePageButton")));
        auto *button = quiet.item(QStringLiteral("aboutCheckUpdateButton"));
        QCOMPARE(button->property("text").toString(), QString::fromUtf8("检查更新"));
        QTRY_VERIFY(button->property("enabled").toBool());
        QVERIFY(QMetaObject::invokeMethod(button, "clicked"));
        QTRY_VERIFY(!quiet.app->toastMessage().isEmpty());
        QVERIFY(quiet.opened.isEmpty());

        PageScene raised;
        QVERIFY(raised.open(QStringLiteral("SettingsAboutTab"), true));
        QTRY_VERIFY(raised.shows(QStringLiteral("aboutOpenReleasePageButton")));
        QVERIFY(!raised.shows(QStringLiteral("aboutCheckUpdateButton")));
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
        // The recheck of a shared or queue-inferred profile is a network request
        // the version 4 text ruled out ("已经有可用档案时不会联网").
        QVERIFY(all.contains(QString::fromUtf8("登录后还会再读一次同一份公开列表")));
        QVERIFY(!all.contains(QString::fromUtf8("通过才用来记录；已经有可用档案时不会联网")));
        QVERIFY(mr::AppSettings::kDisclosureVersion >= 5);
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
