// ---------------------------------------------------------------------------
// tst_sharedcalibrationcard - the 共享校准 section of the calibration card and
// its two dialogs, in shipping QML.
//
// What it pins:
//   * each --mock-shared state shows its sentence and exactly its buttons;
//   * no visible text on the card carries maintainer vocabulary, a wire token,
//     a candidate's short id or the code itself;
//   * a shared profile that records replaces the "正在重新校准" headline, the
//     progress rows and 清空校准进度 - unless it is provisional, which keeps
//     the provisional wording;
//   * 导入校准码 shows the Collector's refusal and closes on success;
//   * 改为本机校准 asks first, and the consent button accepts;
//   * a narrow card wraps the buttons instead of pushing them outside it.
// ---------------------------------------------------------------------------

#include "DesktopTestRunner.h"
#include "CalibrationCardTestSupport.h"
#include "SharedCalibrationTestSupport.h"
#include "QmlComponentTestSupport.h"
#include "AppController.h"
#include "CalibrationController.h"
#include "MockBackend.h"
#include "SharedCalibrationController.h"

#include <QDir>
#include <QGuiApplication>
#include <QJsonArray>
#include <QJsonObject>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickWindow>
#include <QTest>

#include <memory>

namespace {

using mrtest::kVectorCode;

const QStringList &kButtons()
{
    static const QStringList value{QStringLiteral("sharedShareButton"), QStringLiteral("sharedCheckButton"),
                           QStringLiteral("sharedImportButton"), QStringLiteral("sharedRejectButton"),
                           QStringLiteral("sharedAcceptButton"), QStringLiteral("sharedOtherWaysButton")};
    return value;
}

using mrtest::findVisualItem;

/// A shared profile that records while calibration stays OBSERVING beside it (plan §13): the
/// state in which 改为本机校准 is offered during the very duties the shared profile records.
QJsonObject captureWithSharedInUse()
{
    QJsonObject shared = sharedNone();
    shared.insert(QStringLiteral("phase"), QStringLiteral("VERIFIED"));
    shared.insert(QStringLiteral("last_fetch_status"), QStringLiteral("OK"));
    shared.insert(QStringLiteral("profile_id"), QStringLiteral("cn.2026.09.01.0000.0000.shared"));
    return {{QStringLiteral("ffxiv_running"), true},
            {QStringLiteral("state"), QStringLiteral("RUNNING")},
            {QStringLiteral("profile_status"), QStringLiteral("VERIFIED")},
            {QStringLiteral("profile_origin"), QStringLiteral("SHARED_CALIBRATION")},
            {QStringLiteral("calibration"), QJsonObject{
                 {QStringLiteral("state"), QStringLiteral("OBSERVING")},
                 {QStringLiteral("game_build"), QStringLiteral("2026.09.01.0000.0000")},
                 {QStringLiteral("local_profile_id"), QStringLiteral("cn.2026.09.01.0000.0000.shared")},
                 {QStringLiteral("blockers"), QJsonArray{}},
                 {QStringLiteral("events"), QJsonArray{}},
                 {QStringLiteral("shared"), shared}}}};
}

/// One card on a backend, torn down in dependency order.
struct CardScene
{
    std::unique_ptr<mr::MockBackend> backend = std::make_unique<mr::MockBackend>();
    std::unique_ptr<mr::IBackend> other;
    std::unique_ptr<mr::AppController> app;
    std::unique_ptr<QQmlEngine> engine;
    std::unique_ptr<QObject> root;
    QQuickItem *card = nullptr;

    ~CardScene()
    {
        root.reset();
        engine.reset();
        app.reset();
    }

    /// The backend openOn() will be given instead of the mock.
    template <typename Fake>
    Fake *fake()
    {
        return installBackend<Fake>(other);
    }

    bool open(const QString &fixture, int width)
    {
        backend->setSharedCalibrationFixture(fixture);
        return openOn(backend.get(), width);
    }

    bool openOn(mr::IBackend *source, int width)
    {
        app = std::make_unique<mr::AppController>(source, nullptr);
        engine = std::make_unique<QQmlEngine>();
        engine->rootContext()->setContextProperty(QStringLiteral("App"), app.get());
        engine->rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QQmlComponent component(engine.get());
        component.setData(QStringLiteral(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: %1; height: 900; visible: true
    color: Theme.surface
    CalibrationCard { x: 16; y: 16; width: parent.width - 32; height: implicitHeight }
})").arg(width).toUtf8(), QUrl());
        root.reset(component.create());
        if (!root) {
            qWarning("%s", qPrintable(component.errorString()));
            return false;
        }
        card = qobject_cast<QQuickItem *>(root->findChild<QObject *>(QStringLiteral("calibrationCard")));
        return card != nullptr;
    }

    void expandOtherWays() const
    {
        auto *button = item(QStringLiteral("sharedOtherWaysButton"));
        QTRY_VERIFY(button && button->isVisible());
        QTest::qWait(30); // Wait for the newly visible Flow to finish its layout before the click.
        if (!button->property("checked").toBool()) {
            QTest::mouseClick(button->window(), Qt::LeftButton, Qt::NoModifier,
                             button->mapToScene(QPointF(button->width() / 2, button->height() / 2)).toPoint());
        }
        QTRY_VERIFY(button->property("checked").toBool());
    }

    mr::SharedCalibrationController *shared() const { return app->calibration()->shared(); }
    QQuickItem *item(const QString &name) const { return findVisualItem(card, name); }
    QObject *section() const { return root->findChild<QObject *>(QStringLiteral("calibrationSharedSection")); }
    QObject *dialog(const char *name) const { return section()->property(name).value<QObject *>(); }
};

} // namespace

namespace {
class SharedCalibrationCardTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void initTestCase()
    {
        QVERIFY(mrtest::registerSourceQml({"components", "pages"}));
    }

    void eachStateShowsItsSentenceAndOnlyItsButtons_data()
    {
        QTest::addColumn<QString>("fixture");
        QTest::addColumn<QString>("view");
        QTest::addColumn<QString>("sentenceItem");
        QTest::addColumn<QString>("sentence");
        QTest::addColumn<QStringList>("buttons");

        const auto u = [](const char *text) { return QString::fromUtf8(text); };
        const QString headline = QStringLiteral("sharedCalibrationHeadline");
        QTest::newRow("fetching") << "fetching" << "fetching" << headline << u("正在查找其他玩家分享的校准")
                                  << QStringList{QStringLiteral("sharedOtherWaysButton")};
        QTest::newRow("verifying") << "verifying" << "verifying" << headline << u("已找到共享校准，等待本机验证。")
            << QStringList{QStringLiteral("sharedOtherWaysButton")};
        QTest::newRow("consent") << "consent" << "consent" << QStringLiteral("sharedConsentText")
            << u("匹配时间使用你申请排队的时间")
            << QStringList{QStringLiteral("sharedOtherWaysButton"), QStringLiteral("sharedAcceptButton")};
        QTest::newRow("verified") << "verified" << "verified" << QStringLiteral("calibrationHeadline")
            << u("正在用其他玩家分享的校准自动记录") << QStringList{QStringLiteral("sharedOtherWaysButton")};
        // Gates graded by provenance (plans/shared-calibration.md 18.3): a code an index
        // lists is a matter of logging in, a pasted code no index knows waits for one queue
        // and one duty as well. Both are VERIFYING, and the buttons are the same.
        QTest::newRow("imported-published") << "imported-published" << "verifying" << headline
            << u("已找到共享校准，等待本机验证。")
            << QStringList{QStringLiteral("sharedOtherWaysButton")};
        QTest::newRow("imported-unpublished") << "imported-unpublished" << "verifying" << headline
            << u("校准码已导入。登录后申请随机任务并进入副本，通过检查后才能开始记录。")
            << QStringList{QStringLiteral("sharedOtherWaysButton")};
        // Recording after the login burst, with the match and the duty entry still audited
        // (18.4): the card's own headline says which of the two it is, and the grey line
        // below it is the only place the audit is explained.
        QTest::newRow("verified-auditing") << "verified-auditing" << "verified"
            << QStringLiteral("calibrationHeadline")
            << u("正在用其他玩家分享的校准记录，排本和进本还在核对。")
            << QStringList{QStringLiteral("sharedOtherWaysButton")};
        QTest::newRow("rejected") << "rejected" << "rejected" << headline << u("这份共享校准不适用于你的游戏")
            << QStringList{QStringLiteral("sharedCheckButton"), QStringLiteral("sharedOtherWaysButton")};
        QTest::newRow("unavailable") << "unavailable" << "unavailable" << headline << u("暂时没能下载共享校准")
            << QStringList{QStringLiteral("sharedCheckButton"), QStringLiteral("sharedOtherWaysButton")};
        QTest::newRow("user-rejected") << "user-rejected" << "user_rejected" << QStringLiteral("sharedCalibrationDetail")
            << u("清空校准进度") << QStringList{};
        QTest::newRow("none-for-build") << "none-for-build" << "none_for_build" << headline
            << u("暂时没有当前游戏版本的共享校准")
            << QStringList{QStringLiteral("sharedCheckButton"), QStringLiteral("sharedOtherWaysButton")};
        QTest::newRow("share") << "share" << "none" << QStringLiteral("sharedShareHint") << u("软件不会自动上传")
                               << QStringList{QStringLiteral("sharedShareButton")};
        // §20.6: another player's calibration that recorded one complete duty here. Everything a
        // shared profile in use offers stays, and the share button tells other players it works.
        QTest::newRow("verified-proven") << "verified-proven" << "verified" << QStringLiteral("sharedShareHint")
            << u("会打开 GitHub 页面，把这份校准记为你也核实过，让其他玩家的软件更优先使用它。")
            << QStringList{QStringLiteral("sharedShareButton"), QStringLiteral("sharedOtherWaysButton")};
    }

    void eachStateShowsItsSentenceAndOnlyItsButtons()
    {
        QFETCH(QString, fixture);
        QFETCH(QString, view);
        QFETCH(QString, sentenceItem);
        QFETCH(QString, sentence);
        QFETCH(QStringList, buttons);

        CardScene scene;
        QVERIFY(scene.open(fixture, 760));
        QTRY_COMPARE(scene.shared()->view(), view);
        QTRY_VERIFY(scene.item(QStringLiteral("calibrationSharedSection"))
                    && scene.item(QStringLiteral("calibrationSharedSection"))->isVisible());

        auto *text = scene.item(sentenceItem);
        QVERIFY2(text, qPrintable(sentenceItem));
        QTRY_VERIFY2(text->isVisible(), qPrintable(sentenceItem));
        QVERIFY2(text->property("text").toString().contains(sentence),
                 qPrintable(text->property("text").toString()));
        for (const QString &name : kButtons()) {
            auto *button = scene.item(name);
            QVERIFY2(button, qPrintable(name));
            QCOMPARE(button->isVisible(), buttons.contains(name));
        }
        verifyPlayerCopyOf(scene.card);
    }

    void theConsentViewStillOffersImporting()
    {
        // A real machine: the player retired her local profile so she could import the code a
        // friend had sent her. The client downloaded the published queue-inferred code first,
        // it passed verification, and the card went to the consent view - where the import
        // button was hidden. Consenting binds that weaker code and hides the button for good;
        // refusing blocks every import until 重新观察, after which the same code arrives again.
        // There was no way in, and the Collector had never refused the import - only this card.
        CardScene scene;
        QVERIFY(scene.open(QStringLiteral("consent"), 760));
        QTRY_COMPARE(scene.shared()->view(), QStringLiteral("consent"));
        QVERIFY(scene.shared()->canImport());

        QVERIFY(!scene.item(QStringLiteral("sharedImportButton"))->isVisible());
        scene.expandOtherWays();
        auto *button = scene.item(QStringLiteral("sharedImportButton"));
        QVERIFY(button);
        QTRY_VERIFY(button->isVisible());
        QVERIFY(button->property("enabled").toBool());
        // The same fallback entry remains available before consent is accepted.

        QVERIFY(scene.item(QStringLiteral("sharedConsentText"))->property("text").toString().contains(
            QString::fromUtf8("导入校准码")));

        auto *dialog = scene.dialog("importDialog");
        QVERIFY(dialog);
        QVERIFY(!dialog->property("visible").toBool());
        QVERIFY(QMetaObject::invokeMethod(button, "clicked"));
        QTRY_VERIFY(dialog->property("visible").toBool());
    }

    /// The consent box's own buttons must wrap inside a narrow card like every other row.
    void theConsentBoxWrapsInsteadOfOverflowingANarrowCard()
    {
        CardScene scene;
        QVERIFY(scene.open(QStringLiteral("consent"), 340));
        scene.expandOtherWays();
        QTRY_VERIFY(scene.item(QStringLiteral("sharedImportButton"))->isVisible());
        QTest::qWait(50);
        for (const QString &name : {QStringLiteral("sharedAcceptButton"),
                                    QStringLiteral("sharedImportButton")}) {
            auto *button = scene.item(name);
            QVERIFY2(button && button->isVisible(), qPrintable(name));
            const QPointF at = button->mapToItem(scene.card, QPointF(0, 0));
            QVERIFY2(at.x() >= 0 && at.x() + button->width() <= scene.card->width() + 0.5,
                     qPrintable(QStringLiteral("%1 overflows: x=%2 w=%3 card=%4")
                                    .arg(name).arg(at.x()).arg(button->width()).arg(scene.card->width())));
        }
    }

    void aRecordingSharedProfileReplacesTheCalibratingCard()
    {
        CardScene scene;
        QVERIFY(scene.open(QStringLiteral("verified"), 760));
        QTRY_VERIFY(scene.shared()->inUse());
        QTRY_VERIFY(!scene.item(QStringLiteral("calibrationHeadline"))->property("text").toString()
                         .contains(QString::fromUtf8("正在重新校准")));
        QVERIFY(!scene.item(QStringLiteral("calibrationProgress_pop_seen"))->isVisible());
        QVERIFY(!scene.item(QStringLiteral("calibrationDiscardButton"))->isVisible());
        QVERIFY(!scene.item(QStringLiteral("sharedCalibrationHeadline"))->isVisible());
    }

    void anAuditedSharedProfileExplainsTheAuditInGrey()
    {
        // plan 18.4: it records because the login burst matched, while the match and the
        // duty entry are still being checked. The card must not claim "本机已核实" outright,
        // and the one line that explains the audit has to be on screen even though the
        // card - not the shared section - carries the headline.
        CardScene scene;
        QVERIFY(scene.open(QStringLiteral("verified-auditing"), 760));
        QTRY_VERIFY(scene.shared()->inUse());
        QTRY_VERIFY(scene.shared()->auditPending());

        auto *headline = scene.item(QStringLiteral("calibrationHeadline"));
        QTRY_VERIFY(headline->property("text").toString().contains(
            QString::fromUtf8("排本和进本还在核对")));
        QVERIFY(!headline->property("text").toString().contains(
            QString::fromUtf8("已通过本机检查")));
        QVERIFY(!scene.item(QStringLiteral("sharedCalibrationHeadline"))->isVisible());

        auto *detail = scene.item(QStringLiteral("sharedCalibrationDetail"));
        QTRY_VERIFY(detail->isVisible());
        const QString text = detail->property("text").toString();
        QVERIFY2(text.contains(QString::fromUtf8("排本和进本还在核对中")), qPrintable(text));
        QVERIFY2(text.contains(QString::fromUtf8("标为“待复核”")), qPrintable(text));
        // The audit takes nothing away from the recording state the plain VERIFIED shows.
        QVERIFY(!scene.item(QStringLiteral("calibrationProgress_pop_seen"))->isVisible());
        QVERIFY(!scene.item(QStringLiteral("calibrationDiscardButton"))->isVisible());
        QVERIFY(!scene.item(QStringLiteral("sharedRejectButton"))->isVisible());
        scene.expandOtherWays();
        QVERIFY(scene.item(QStringLiteral("sharedRejectButton"))->isVisible());

        verifyPlayerCopyOf(scene.card);
    }

    void aVerifiedSharedProfileWithoutAnAuditKeepsThePlainSentence()
    {
        // A Collector before 1.1.0 never reports audit_pending, and the wording from
        // before the graded gates is what it gets.
        CardScene scene;
        QVERIFY(scene.open(QStringLiteral("verified"), 760));
        QTRY_VERIFY(scene.shared()->inUse());
        QVERIFY(!scene.shared()->auditPending());
        QTRY_VERIFY(scene.item(QStringLiteral("calibrationHeadline"))->property("text").toString()
                        .contains(QString::fromUtf8("正在用其他玩家分享的校准自动记录")));
        QVERIFY(!scene.item(QStringLiteral("sharedCalibrationDetail"))->isVisible());
    }

    void aProvisionalSharedProfileKeepsTheProvisionalWording()
    {
        // Plan §13: a queue-inferred shared profile that recorded its first duty keeps calibration
        // looking for the real match message underneath it, so the Collector reports OBSERVING
        // with that profile's id as local_profile_id. The card says it records and is still
        // improving (the provisional wording), and the shared section names where it came from.
        CardScene scene;
        auto *source = scene.fake<CaptureBackend>();
        source->capture = captureWithSharedInUse();
        QVERIFY(scene.openOn(source, 760));
        QTRY_VERIFY(scene.app->calibration()->provisional());
        QTRY_VERIFY(scene.shared()->inUse());

        QTRY_VERIFY(scene.item(QStringLiteral("calibrationHeadline"))->property("text").toString()
                        .contains(QString::fromUtf8("已经可以自动记录")));
        auto *explanation = scene.item(QStringLiteral("calibrationExplanation"));
        QVERIFY(explanation->isVisible());
        QVERIFY(explanation->property("text").toString().contains(QString::fromUtf8("你申请的随机任务")));
        auto *sharedHeadline = scene.item(QStringLiteral("sharedCalibrationHeadline"));
        QTRY_VERIFY(sharedHeadline->isVisible());
        QVERIFY(sharedHeadline->property("text").toString().contains(QString::fromUtf8("正在使用共享校准")));
        QVERIFY(!scene.item(QStringLiteral("sharedRejectButton"))->isVisible());
        scene.expandOtherWays();
        QVERIFY(scene.item(QStringLiteral("sharedRejectButton"))->isVisible());
        verifyPlayerCopyOf(scene.card);
    }

    void importShowsTheRefusalAndClosesOnSuccess()
    {
        CardScene scene;
        QVERIFY(scene.open(QStringLiteral("unavailable"), 760));
        scene.expandOtherWays();
        QTRY_VERIFY(scene.item(QStringLiteral("sharedImportButton"))->isVisible());
        QVERIFY(QMetaObject::invokeMethod(scene.item(QStringLiteral("sharedImportButton")), "clicked"));
        QObject *dialog = scene.dialog("importDialog");
        QVERIFY(dialog);
        QTRY_VERIFY(dialog->property("visible").toBool());

        dialog->setProperty("code", QStringLiteral("not a code"));
        QVERIFY(QMetaObject::invokeMethod(dialog, "submit"));
        QTRY_VERIFY(!dialog->property("messageText").toString().isEmpty());
        // The Collector's sentence for a code it cannot read (sharing.import.not_imported).
        QVERIFY(dialog->property("messageText").toString().contains(QString::fromUtf8("无法识别这份校准码")));
        QVERIFY(dialog->property("visible").toBool());

        dialog->setProperty("code", QLatin1String(kVectorCode));
        QVERIFY(QMetaObject::invokeMethod(dialog, "submit"));
        QTRY_VERIFY(!dialog->property("visible").toBool());
        QTRY_COMPARE(scene.shared()->view(), QStringLiteral("verifying"));
        QVERIFY(scene.app->toastMessage().contains(QString::fromUtf8("校准码已导入")));
    }

    void rejectAsksBeforeSending()
    {
        CardScene scene;
        QVERIFY(scene.open(QStringLiteral("verifying"), 760));
        scene.expandOtherWays();
        QTRY_VERIFY(scene.item(QStringLiteral("sharedRejectButton"))->isVisible());
        QVERIFY(QMetaObject::invokeMethod(scene.item(QStringLiteral("sharedRejectButton")), "clicked"));
        QObject *dialog = scene.dialog("rejectDialog");
        QVERIFY(dialog);
        QTRY_VERIFY(dialog->property("visible").toBool());
        QTest::qWait(50);
        QCOMPARE(scene.backend->sharedCalibrationFixture(), QStringLiteral("verifying"));

        auto *content = dialog->property("contentItem").value<QQuickItem *>();
        auto *confirm = findVisualItem(content, QStringLiteral("sharedRejectConfirm"));
        QVERIFY(confirm);
        QVERIFY(QMetaObject::invokeMethod(confirm, "clicked"));
        QTRY_VERIFY(!dialog->property("visible").toBool());
        QTRY_COMPARE(scene.shared()->view(), QStringLiteral("user_rejected"));
    }

    void theConsentSentenceSaysWhereTheLocalCalibrationChoiceIs()
    {
        // Review K2-5: 改为本机校准 moved into the folded 其他方式, and the sentence pointing at it
        // has to say where it went.
        CardScene scene;
        QVERIFY(scene.open(QStringLiteral("consent"), 760));
        QTRY_COMPARE(scene.shared()->view(), QStringLiteral("consent"));
        auto *explanation = scene.item(QStringLiteral("calibrationExplanation"));
        QVERIFY(explanation);
        QTRY_VERIFY(explanation->isVisible());
        QTRY_VERIFY(explanation->property("text").toString().contains(QString::fromUtf8("同意，开始记录")));
        const QString text = explanation->property("text").toString();
        QVERIFY2(text.contains(QString::fromUtf8("在「其他方式」中选择「改为本机校准」")), qPrintable(text));
    }

    void anUnsavedRefusalOffersADirectRetryUntilTheCollectorConfirmsIt()
    {
        CardScene scene;
        auto *source = scene.fake<CaptureBackend>();
        source->capture = captureWithRollback({}, false);
        auto calibration = source->capture.value(QStringLiteral("calibration")).toObject();
        auto shared = sharedNone();
        shared.insert(QStringLiteral("phase"), QStringLiteral("REJECTED"));
        shared.insert(QStringLiteral("user_rejected"), true);
        shared.insert(QStringLiteral("last_refusal"), QStringLiteral("USER_REJECTION_NOT_SAVED"));
        calibration.insert(QStringLiteral("shared"), shared);
        source->capture.insert(QStringLiteral("calibration"), calibration);
        QVERIFY(scene.openOn(source, 760));
        auto *retry = scene.item(QStringLiteral("sharedRetryRejectionButton"));
        QTRY_VERIFY(retry && retry->isVisible() && retry->isEnabled());
        QVERIFY(scene.shared()->headline().contains(QString::fromUtf8("尚未保存")));
        QVERIFY(!scene.shared()->canImport());

        shared.insert(QStringLiteral("last_refusal"), QStringLiteral("USER_REJECTED"));
        calibration.insert(QStringLiteral("shared"), shared);
        source->capture.insert(QStringLiteral("calibration"), calibration);
        QVERIFY(QMetaObject::invokeMethod(retry, "clicked"));
        QTRY_VERIFY(source->requestTypes.contains(QStringLiteral("RejectSharedCalibration")));
        QTRY_VERIFY(!retry->isVisible());
        QVERIFY(scene.shared()->userRejected());
    }

    void theConsentButtonAccepts()
    {
        CardScene scene;
        QVERIFY(scene.open(QStringLiteral("consent"), 760));
        QTRY_VERIFY(scene.item(QStringLiteral("sharedAcceptButton"))->isVisible());
        QVERIFY(QMetaObject::invokeMethod(scene.item(QStringLiteral("sharedAcceptButton")), "clicked"));
        QTRY_COMPARE(scene.shared()->view(), QStringLiteral("verified"));
    }

    void fallbackActionsWorkWithTheKeyboard()
    {
        CardScene scene;
        QVERIFY(scene.open(QStringLiteral("verifying"), 760));
        auto *disclosure = scene.item(QStringLiteral("sharedOtherWaysButton"));
        QTRY_VERIFY(disclosure && disclosure->isVisible());
        auto *window = disclosure->window();
        // Traverse the actual tab chain from the window, including the local-calibration action.
        for (int tries = 0; tries < 12 && window->activeFocusItem() != disclosure; ++tries)
            QTest::keyClick(window, Qt::Key_Tab);
        QTRY_COMPARE(window->activeFocusItem(), disclosure);
        QTest::keyClick(window, Qt::Key_Space);
        QTRY_VERIFY(scene.item(QStringLiteral("sharedImportButton"))->isVisible());
        QTest::keyClick(window, Qt::Key_Tab);
        QTRY_COMPARE(window->activeFocusItem(), scene.item(QStringLiteral("sharedImportButton")));
        QTest::keyClick(window, Qt::Key_Tab);
        QTRY_COMPARE(window->activeFocusItem(), scene.item(QStringLiteral("sharedRejectButton")));
        QTest::keyClick(window, Qt::Key_Backtab, Qt::ShiftModifier);
        QTRY_COMPARE(window->activeFocusItem(), scene.item(QStringLiteral("sharedImportButton")));
        QTest::keyClick(window, Qt::Key_Return);
        QTRY_VERIFY(scene.dialog("importDialog")->property("visible").toBool());
        QTest::keyClick(window, Qt::Key_Escape);
        QTRY_VERIFY(!scene.dialog("importDialog")->property("visible").toBool());
        QTRY_COMPARE(window->activeFocusItem(), scene.item(QStringLiteral("sharedImportButton")));

        // A phase change collapses alternatives while preserving a usable focus target.
        scene.backend->setSharedCalibrationFixture(QStringLiteral("verified"));
        scene.app->refreshStatus();
        QTRY_COMPARE(scene.shared()->view(), QStringLiteral("verified"));
        QTRY_VERIFY(!scene.item(QStringLiteral("sharedRejectButton"))->isVisible());
        QTRY_COMPARE(window->activeFocusItem(), disclosure);
    }

    void localizedFallbacksFitTheSharedPanel_data()
    {
        QTest::addColumn<QString>("language");
        QTest::addColumn<int>("width");
        QTest::addColumn<bool>("dark");
        for (const auto &language : {"zh_CN", "zh_TW", "en", "ja"}) {
            for (int width : {340, 760}) {
                for (bool dark : {false, true}) {
                    const QByteArray name = QStringLiteral("%1-%2-%3").arg(QLatin1String(language))
                        .arg(width).arg(dark ? "dark" : "light").toUtf8();
                    QTest::newRow(name.constData()) << QString::fromLatin1(language) << width << dark;
                }
            }
        }
    }

    void localizedFallbacksFitTheSharedPanel()
    {
        QFETCH(QString, language);
        QFETCH(int, width);
        QFETCH(bool, dark);
        const ScopedCatalogue catalogue(language);
        QVERIFY2(catalogue.loaded(), qPrintable(catalogue.path()));
        CardScene scene;
        QVERIFY(scene.open(QStringLiteral("verifying"), width));
        if (scene.app->isDark() != dark)
            scene.app->toggleTheme();
        auto *section = scene.item(QStringLiteral("calibrationSharedSection"));
        QTRY_VERIFY(section && section->isVisible());
        const QString output = qEnvironmentVariable("MR_SHARED_SCREENSHOT_DIR");
        const auto save = [&](const QString &state) {
            if (!output.isEmpty()) {
                QDir().mkpath(output);
                const QString path = QDir(output).filePath(QStringLiteral("%1-%2.png")
                    .arg(QString::fromLatin1(QTest::currentDataTag()), state));
                QVERIFY2(section->window()->grabWindow().save(path), qPrintable(path));
            }
        };
        QTest::qWait(50);
        save(QStringLiteral("collapsed"));
        scene.expandOtherWays();
        QTest::qWait(50);
        for (const auto &name : {"sharedOtherWaysButton", "sharedImportButton", "sharedRejectButton"}) {
            auto *button = scene.item(QString::fromLatin1(name));
            QVERIFY(button && button->isVisible());
            const QPointF at = button->mapToItem(section, QPointF(0, 0));
            QVERIFY2(at.x() >= section->property("horizontalPadding").toReal()
                         && at.x() + button->width() <= section->width()
                             - section->property("horizontalPadding").toReal() + 0.5,
                     qPrintable(QStringLiteral("%1 overflows shared panel: x=%2 width=%3 panel=%4")
                         .arg(QLatin1String(name)).arg(at.x()).arg(button->width()).arg(section->width())));
        }
        save(QStringLiteral("expanded"));
        if (width == 760) {
            QVERIFY(QMetaObject::invokeMethod(scene.item(QStringLiteral("sharedImportButton")), "clicked"));
            auto *dialog = scene.dialog("importDialog");
            QTRY_VERIFY(dialog->property("visible").toBool());
            auto *help = findVisualItem(dialog->property("contentItem").value<QQuickItem *>(),
                                        QStringLiteral("sharedImportHelp"));
            QVERIFY(help && !help->property("text").toString().isEmpty());
            if (language != QLatin1String("zh_CN"))
                QVERIFY(!help->property("text").toString().contains(QString::fromUtf8("自动获取不可用")));
            QTest::qWait(50);
            save(QStringLiteral("import"));
        }
    }

    void aNarrowCardWrapsItsButtons()
    {
        CardScene scene;
        QVERIFY(scene.open(QStringLiteral("rejected"), 340));
        QTRY_VERIFY(scene.item(QStringLiteral("sharedCheckButton"))->isVisible());
        QCOMPARE(scene.item(QStringLiteral("sharedCheckButton"))->property("variant").toString(), QStringLiteral("primary"));
        scene.expandOtherWays();
        QTest::qWait(50);
        for (const QString &name : kButtons()) {
            auto *button = scene.item(name);
            if (!button->isVisible())
                continue;
            const QPointF at = button->mapToItem(scene.card, QPointF(0, 0));
            QVERIFY2(at.x() >= 0 && at.x() + button->width() <= scene.card->width() + 0.5,
                     qPrintable(QStringLiteral("%1 overflows: x=%2 w=%3 card=%4")
                                    .arg(name).arg(at.x()).arg(button->width()).arg(scene.card->width())));
        }
    }

    void theShareButtonSaysWhichShareItIs_data()
    {
        QTest::addColumn<QString>("fixture");
        QTest::addColumn<QString>("button");
        QTest::addColumn<QString>("hint");
        QTest::newRow("local") << "share" << QString::fromUtf8("分享给其他玩家")
                               << mr::SharedCalibrationController::shareHint();
        QTest::newRow("verified") << "verified-proven" << QString::fromUtf8("这份校准好用，告诉其他玩家")
                                  << mr::SharedCalibrationController::verifiedShareHint();
    }

    void theShareButtonSaysWhichShareItIs()
    {
        QFETCH(QString, fixture);
        QFETCH(QString, button);
        QFETCH(QString, hint);

        CardScene scene;
        QVERIFY(scene.open(fixture, 760));
        QTRY_VERIFY(scene.item(QStringLiteral("sharedShareButton"))->isVisible());
        QCOMPARE(scene.item(QStringLiteral("sharedShareButton"))->property("text").toString(), button);
        QCOMPARE(scene.item(QStringLiteral("sharedShareHint"))->property("text").toString(), hint);
        verifyPlayerCopyOf(scene.card);
    }

    void aProvenSharedProfileStillReadsAsTheProfileInUse()
    {
        // Proven changes what the player may do, not what the card says: the shared profile
        // records, the progress rows and 清空校准进度 stay hidden, and no audit line appears.
        CardScene scene;
        QVERIFY(scene.open(QStringLiteral("verified-proven"), 760));
        QTRY_VERIFY(scene.shared()->inUse());
        QTRY_COMPARE(scene.shared()->shareKind(), QStringLiteral("verified"));
        QVERIFY(!scene.shared()->auditPending());
        QTRY_VERIFY(scene.item(QStringLiteral("calibrationHeadline"))->property("text").toString()
                        .contains(QString::fromUtf8("正在用其他玩家分享的校准自动记录")));
        QVERIFY(!scene.item(QStringLiteral("calibrationProgress_pop_seen"))->isVisible());
        QVERIFY(!scene.item(QStringLiteral("calibrationDiscardButton"))->isVisible());
        QVERIFY(!scene.item(QStringLiteral("sharedCalibrationDetail"))->isVisible());
    }

    void theSharedRejectWaitsForTheDutyToEnd()
    {
        // Trigger A: a shared profile records its first duty while calibration stays OBSERVING,
        // so the card - and 改为本机校准 - stays on screen during that duty. Stopping the shared
        // profile closes the run the way a stopped capture does.
        CardScene scene;
        auto *source = scene.fake<CaptureBackend>();
        source->capture = captureWithSharedInUse();
        source->currentRun = QJsonObject{{QStringLiteral("state"), QStringLiteral("ENTERED_DUTY")}};
        QVERIFY(scene.openOn(source, 760));
        QTRY_VERIFY(scene.shared()->inUse());
        QTRY_VERIFY(scene.shared()->canReject());
        QTRY_COMPARE(scene.app->currentRunState(), QStringLiteral("ENTERED_DUTY"));

        scene.expandOtherWays();
        auto *button = scene.item(QStringLiteral("sharedRejectButton"));
        QVERIFY(button);
        QTRY_VERIFY(button->isVisible());
        QVERIFY(!button->property("enabled").toBool());
        auto *hint = scene.item(QStringLiteral("sharedRejectInFlightHint"));
        QVERIFY(hint);
        QTRY_VERIFY(hint->isVisible());
        QCOMPARE(hint->property("text").toString(), QString::fromUtf8("副本进行中，结束后再试"));

        // The confirm button in the dialog refuses as well, however the dialog came to be open.
        QObject *dialog = scene.dialog("rejectDialog");
        QVERIFY(dialog);
        QVERIFY(QMetaObject::invokeMethod(dialog, "open"));
        QTRY_VERIFY(dialog->property("visible").toBool());
        auto *confirm = findVisualItem(dialog->property("contentItem").value<QQuickItem *>(),
                                       QStringLiteral("sharedRejectConfirm"));
        QVERIFY(confirm);
        QVERIFY(!confirm->property("enabled").toBool());
        QVERIFY(!source->requestTypes.contains(QStringLiteral("RejectSharedCalibration")));

        verifyPlayerCopyOf(scene.card);
    }

    void aSharedCodeThatDoesNotRecordYetCanStillBeRefusedDuringADuty()
    {
        // Refusing a code still being checked drops candidates only: the profile recording the
        // duty is another one and stays bound (SharedCalibrationSession.RejectByUser), so
        // there is no duty to protect and nothing to wait for.
        CardScene scene;
        scene.backend->setLiveMode(mr::MockBackend::LiveMode::Entered);
        QVERIFY(scene.open(QStringLiteral("verifying"), 760));
        QTRY_COMPARE(scene.app->currentRunState(), QStringLiteral("ENTERED_DUTY"));
        QTRY_VERIFY(scene.shared()->canReject());
        QVERIFY(!scene.shared()->inUse());
        scene.expandOtherWays();
        auto *button = scene.item(QStringLiteral("sharedRejectButton"));
        QTRY_VERIFY(button && button->isVisible());
        QVERIFY(button->property("enabled").toBool());
        QVERIFY(!scene.item(QStringLiteral("sharedRejectInFlightHint"))->isVisible());
    }

    void anOpenSharedRejectDialogClosesWhenADutyStarts()
    {
        CardScene scene;
        auto *source = scene.fake<CaptureBackend>();
        source->capture = captureWithSharedInUse();
        QVERIFY(scene.openOn(source, 760));
        QTRY_VERIFY(scene.shared()->canReject());
        QTRY_COMPARE(scene.app->currentRunState(), QStringLiteral("IDLE"));

        scene.expandOtherWays();
        auto *button = scene.item(QStringLiteral("sharedRejectButton"));
        QTRY_VERIFY(button && button->isVisible());
        QVERIFY(button->property("enabled").toBool());
        auto *hint = scene.item(QStringLiteral("sharedRejectInFlightHint"));
        QVERIFY(hint);
        QVERIFY(!hint->isVisible());
        QVERIFY(QMetaObject::invokeMethod(button, "clicked"));
        QObject *dialog = scene.dialog("rejectDialog");
        QTRY_VERIFY(dialog->property("visible").toBool());
        auto *confirm = findVisualItem(dialog->property("contentItem").value<QQuickItem *>(),
                                       QStringLiteral("sharedRejectConfirm"));
        QVERIFY(confirm);
        QVERIFY(confirm->property("enabled").toBool());

        source->currentRun = QJsonObject{{QStringLiteral("state"), QStringLiteral("MENTOR_MATCHED")}};
        scene.app->refreshStatus();
        QTRY_COMPARE(scene.app->currentRunState(), QStringLiteral("MENTOR_MATCHED"));

        QTRY_VERIFY(!dialog->property("visible").toBool());
        QVERIFY(!confirm->property("enabled").toBool());
        QVERIFY(!source->requestTypes.contains(QStringLiteral("RejectSharedCalibration")));
    }

};
} // namespace

MR_DESKTOP_TEST_SUITE(SharedCalibrationCard)
{
    return runCalibrationCardTests<SharedCalibrationCardTests>(argc, argv);
}

#include "SharedCalibrationCardTests.moc"
