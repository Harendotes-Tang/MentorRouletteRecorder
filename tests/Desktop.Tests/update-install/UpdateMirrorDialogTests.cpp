#include "AppSettings.h"
#include "DesktopTestRunner.h"
#include "Formatters.h"
#include "JobCatalog.h"
#include "MockBackend.h"
#include "MotionSceneFixture.h"
#include "RoleCatalog.h"
#include "RunFormValidator.h"
#include "TestCollectorGuard.h"
#include "UpdateController.h"

#include <QPointer>
#include <QQuickStyle>
#include <QStandardPaths>

namespace {

class MirrorChoiceBackend final : public mr::IBackend
{
public:
    QJsonObject status;
    QList<QJsonObject> choices;
    QPointer<mr::BackendReply> pending;
    bool connected = true;

    QString backendName() const override { return QStringLiteral("mock"); }
    bool isConnected() const override { return connected; }

    mr::BackendReply *request(const QString &type, const QJsonObject &payload = {}) override
    {
        auto *reply = new mr::BackendReply(type, type, this);
        if (type == QLatin1String("ResolveUpdateMirrorOffer")) {
            choices.append(payload);
            pending = reply;
        } else {
            QTimer::singleShot(0, reply, [this, reply, type] {
                reply->succeed(type == QLatin1String("GetStatus") ? status : QJsonObject());
            });
        }
        return reply;
    }
};

struct MirrorScene
{
    MirrorChoiceBackend backend;
    mr::AppController controller{&backend, nullptr};
    mr::AppSettings settings;
    mr::Formatters formatters;
    mr::JobCatalog jobs;
    mr::RoleCatalog roles;
    mr::RunFormValidator validator;
    QQmlEngine engine;
    std::unique_ptr<QObject> root;
    QString errors;

    bool create()
    {
        auto *context = engine.rootContext();
        context->setContextProperty(QStringLiteral("App"), &controller);
        context->setContextProperty(QStringLiteral("Settings"), &settings);
        context->setContextProperty(QStringLiteral("Fmt"), &formatters);
        context->setContextProperty(QStringLiteral("Jobs"), &jobs);
        context->setContextProperty(QStringLiteral("Roles"), &roles);
        context->setContextProperty(QStringLiteral("RunForm"), &validator);
        context->setContextProperty(QStringLiteral("SuppressOnboarding"), true);
        context->setContextProperty(QStringLiteral("SuppressReleaseNotes"), true);
        context->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QQmlComponent component(&engine, QUrl::fromLocalFile(
            QString::fromUtf8(MR_DESKTOP_QML_DIR) + QStringLiteral("/Main.qml")));
        root.reset(component.create());
        errors = component.errorString();
        return root != nullptr;
    }

    QQuickWindow *window() const { return qobject_cast<QQuickWindow *>(root.get()); }
    QObject *named(const QString &name) const { return root->findChild<QObject *>(name); }
    QQuickItem *item(const QString &name) const { return Scene::find(window()->contentItem(), name); }

    bool click(const QString &name) const
    {
        auto *button = item(name);
        if (!button || !button->isVisible() || !button->isEnabled())
            return false;
        const auto center = button->mapToScene(QPointF(button->width() / 2, button->height() / 2));
        QTest::mouseClick(window(), Qt::LeftButton, Qt::NoModifier, center.toPoint());
        return true;
    }
};

class UpdateMirrorDialogTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void initTestCase() { QVERIFY(registerShippingQml()); }

    void mockResolvesOnlyValidMirrorChoices_data()
    {
        QTest::addColumn<QJsonObject>("request");
        QTest::addColumn<QString>("errorField");
        const QString id = QStringLiteral("AAAAAAAA-BBBB-4CCC-8DDD-EEEEEEEEEEEE");
        const QJsonObject accept{{"offer_id", id}, {"use_gitcode", true}};
        QTest::newRow("accept stale") << accept << QString();
        QTest::newRow("decline stale") << QJsonObject{{"offer_id", id}, {"use_gitcode", false}} << QString();
        QTest::newRow("missing id") << QJsonObject{{"use_gitcode", true}} << QStringLiteral("payload.offer_id");
        QTest::newRow("invalid id") << QJsonObject{{"offer_id", "invalid"}, {"use_gitcode", true}}
                                   << QStringLiteral("payload.offer_id");
        QTest::newRow("signed id") << QJsonObject{{"offer_id", "+0000001-0000-0000-0000-000000000000"},
                                                  {"use_gitcode", true}} << QStringLiteral("payload.offer_id");
        QTest::newRow("trailing newline") << QJsonObject{{"offer_id", id + QLatin1Char('\n')},
                                                        {"use_gitcode", true}} << QStringLiteral("payload.offer_id");
        QTest::newRow("missing choice") << QJsonObject{{"offer_id", id}} << QStringLiteral("payload.use_gitcode");
        QTest::newRow("invalid choice") << QJsonObject{{"offer_id", id}, {"use_gitcode", 1}}
                                       << QStringLiteral("payload.use_gitcode");
        QJsonObject extra = accept;
        extra.insert(QStringLiteral("other"), false);
        QTest::newRow("extra property") << extra << QStringLiteral("payload.other");
    }

    void mockResolvesOnlyValidMirrorChoices()
    {
        QFETCH(QJsonObject, request);
        QFETCH(QString, errorField);
        mr::MockBackend backend;
        backend.setUpdateDownloadFixture(QStringLiteral("downloading"));
        auto *reply = backend.request(QStringLiteral("ResolveUpdateMirrorOffer"), request);
        bool finished = false;
        bool ok = false;
        QVariantMap answer;
        QString code;
        QString field;
        reply->whenDone(this, [&](bool succeeded, const QVariantMap &payload, const QString &error, const QString &) {
            ok = succeeded;
            answer = payload;
            code = error;
            field = reply->errorDetails().value(QStringLiteral("field")).toString();
            finished = true;
        });
        QTRY_VERIFY(finished);
        QCOMPARE(ok, errorField.isEmpty());
        QCOMPARE(field, errorField);
        if (ok) {
            const auto download = answer.value("update").toMap().value("download").toMap();
            QCOMPARE(download.value("state").toString(), QStringLiteral("DOWNLOADING"));
            QVERIFY(download.value("received_bytes").toLongLong() > 0);
            QVERIFY(!download.contains("mirror_offer_id"));
        } else {
            QCOMPARE(code, QStringLiteral("ERR_BAD_REQUEST"));
        }
        QCOMPARE(backend.startUpdateDownloadCount(), 0);
        QCOMPARE(backend.cancelUpdateDownloadCount(), 0);
    }

    void mirrorDownloadChoice_data()
    {
        QTest::addColumn<QString>("action");
        QTest::newRow("switch") << QStringLiteral("switch");
        QTest::newRow("retry on reconnect") << QStringLiteral("reconnect");
        QTest::newRow("retry after timeout") << QStringLiteral("timeout");
        QTest::newRow("timeout after Collector applied the choice") << QStringLiteral("applied");
        QTest::newRow("keep without acknowledgement") << QStringLiteral("keep failed");
        QTest::newRow("keep") << QStringLiteral("keep");
        QTest::newRow("escape") << QStringLiteral("escape");
        QTest::newRow("finished") << QStringLiteral("finished");
    }

    void mirrorDownloadChoice()
    {
        QFETCH(QString, action);
        const QString offerId = QStringLiteral("11111111-2222-4333-8444-666666666706");
        QJsonObject download{{QStringLiteral("state"), QStringLiteral("DOWNLOADING")},
                             {QStringLiteral("version"), QStringLiteral("99.9.9")},
                             {QStringLiteral("received_bytes"), 1024},
                             {QStringLiteral("total_bytes"), 1000000},
                             {QStringLiteral("mirror_offer_id"), offerId}};
        QJsonObject update{{QStringLiteral("enabled"), true},
                           {QStringLiteral("update_available"), true},
                           {QStringLiteral("latest_version"), QStringLiteral("99.9.9")},
                           {QStringLiteral("download"), download}};
        const QJsonObject pendingStatus{{QStringLiteral("update"), update}};
        MirrorScene shell;
        shell.backend.status = pendingStatus;
        QVERIFY2(shell.create(), qPrintable(shell.errors));
        QVERIFY(QTest::qWaitForWindowExposed(shell.window()));
        shell.controller.update()->setDownloadPollMs(20);

        auto *dialog = shell.named(QStringLiteral("updateMirrorDialog"));
        QVERIFY(dialog);
        QTRY_VERIFY(dialog->property("visible").toBool());
        auto *keep = shell.item(QStringLiteral("keepGitHubDownloadButton"));
        QVERIFY(keep);
        QTRY_VERIFY(keep->hasActiveFocus());

        // A new progress sample for the same offer leaves the same dialog open.
        download.insert(QStringLiteral("received_bytes"), 8192);
        update.insert(QStringLiteral("download"), download);
        shell.backend.status = {{QStringLiteral("update"), update}};
        QTRY_VERIFY(shell.controller.update()->downloadProgress() > 0.008);
        QVERIFY(dialog->property("visible").toBool());

        const bool retry = action == QLatin1String("reconnect") || action == QLatin1String("timeout");
        const bool switching = retry || action == QLatin1String("switch") || action == QLatin1String("applied");
        if (switching) {
            QVERIFY(shell.click(QStringLiteral("switchToGitCodeButton")));
        } else if (action == QLatin1String("keep") || action == QLatin1String("keep failed")) {
            QVERIFY(shell.click(QStringLiteral("keepGitHubDownloadButton")));
        } else if (action == QLatin1String("escape")) {
            QTest::keyClick(shell.window(), Qt::Key_Escape);
        }

        if (action != QLatin1String("finished")) {
            QTRY_VERIFY(!dialog->property("visible").toBool());
            const auto calls = shell.backend.choices;
            QCOMPARE(calls.size(), 1);
            QCOMPARE(calls.constFirst().value(QStringLiteral("offer_id")).toString(), offerId);
            QCOMPARE(calls.constFirst().value(QStringLiteral("use_gitcode")).toBool(),
                     switching);
        }

        if (retry) {
            // While the choice is outstanding, an old status for the same ID cannot prompt again.
            shell.controller.update()->refreshFromStatus(pendingStatus.toVariantMap());
            QVERIFY(shell.controller.update()->mirrorOfferId().isEmpty());
            QVERIFY(shell.backend.pending);
            if (action == QLatin1String("reconnect")) {
                shell.backend.connected = false;
                shell.backend.pending->failUnsent(QStringLiteral("ERR_INTERNAL"), QStringLiteral("disconnected"));
                Q_EMIT shell.backend.connectionChanged();
                QVERIFY(shell.controller.update()->mirrorOfferId().isEmpty());
                shell.backend.connected = true;
                Q_EMIT shell.backend.connectionChanged();
            } else {
                shell.backend.pending->failTransport(QStringLiteral("ERR_INTERNAL"), QStringLiteral("timeout"));
            }
            // No automatic re-send. A fresh GetStatus offers the still-pending choice to the user.
            QTRY_VERIFY(dialog->property("visible").toBool());
            QCOMPARE(shell.backend.choices.size(), 1);
            QVERIFY(shell.click(QStringLiteral("switchToGitCodeButton")));
            QTRY_COMPARE(shell.backend.choices.size(), 2);
        }

        if (action == QLatin1String("applied") || action == QLatin1String("keep failed")) {
            // A lost reply can follow an applied switch. Keeping GitHub needs no
            // confirmation even if its offer remains in subsequent status reads.
            if (action == QLatin1String("applied"))
                download.remove(QStringLiteral("mirror_offer_id"));
            download.insert(QStringLiteral("received_bytes"), 16384);
            update.insert(QStringLiteral("download"), download);
            shell.backend.status = {{QStringLiteral("update"), update}};
            QVERIFY(shell.backend.pending);
            shell.backend.pending->failTransport(QStringLiteral("ERR_INTERNAL"), QStringLiteral("timeout"));
            QTRY_VERIFY(shell.controller.update()->downloadProgress() > 0.016);
            QVERIFY(shell.controller.update()->mirrorOfferId().isEmpty());
            QVERIFY(!dialog->property("visible").toBool());
            QCOMPARE(shell.backend.choices.size(), 1);
        }

        download.insert(QStringLiteral("state"), QStringLiteral("VERIFYING"));
        download.remove(QStringLiteral("mirror_offer_id"));
        update.insert(QStringLiteral("download"), download);
        shell.backend.status = {{QStringLiteral("update"), update}};
        QTRY_COMPARE(shell.controller.update()->downloadPhase(), QStringLiteral("verifying"));
        QTRY_VERIFY(!dialog->property("visible").toBool());
        if (auto *late = shell.backend.pending.data())
            late->succeed(pendingStatus);
        QCoreApplication::processEvents();
        QCOMPARE(shell.controller.update()->downloadPhase(), QStringLiteral("verifying"));
        const int expectedChoices = retry ? 2 : (action == QLatin1String("finished") ? 0 : 1);
        QCOMPARE(shell.backend.choices.size(), expectedChoices);
    }
};
} // namespace

MR_DESKTOP_TEST_SUITE(UpdateMirrorDialog)
{
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication app(argc, argv);
    UpdateMirrorDialogTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "UpdateMirrorDialogTests.moc"
