// ---------------------------------------------------------------------------
// tst_sharedcalibrationcard - the 共享校准 section of the calibration card and
// its two dialogs, in shipping QML.
//
// What it pins:
//   * each --mock-shared state shows its sentence and exactly its buttons;
//   * no visible text on the card carries maintainer vocabulary, a wire token,
//     a candidate's short id or the code itself;
//   * a shared profile that records replaces the "正在重新校准" headline, the
//     progress rows and 清空进度并重新观察 - unless it is provisional, which keeps
//     the provisional wording;
//   * 导入校准码 shows the Collector's refusal and closes on success;
//   * 不用共享的，我自己校准 asks first, and the consent button accepts;
//   * a narrow card wraps the buttons instead of pushing them outside it;
//   * the always-visible 协议档案 card on the capture page carries 分享给其他玩家
//     after a restart, when the calibration card is gone, and steps aside while
//     the calibration card offers the very same button.
// ---------------------------------------------------------------------------

#include "TestCollectorGuard.h"
#include "AppController.h"
#include "CalibrationController.h"
#include "Formatters.h"
#include "MockBackend.h"
#include "SharedCalibrationController.h"

#include <QDir>
#include <QFont>
#include <QFontDatabase>
#include <QGuiApplication>
#include <QJsonArray>
#include <QJsonObject>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickStyle>
#include <QStandardPaths>
#include <QTest>

#include <memory>

namespace {

constexpr auto kVectorCode =
    "MRC1.XY_LasMwEEX_ZdbG6G3LuxKyKyG02ZRShCSPExXbMrIdaEP-vWoKpeksZjH3wZkLHO2Axq2hb6EBRpgqiS4JLUme24IC3qMzcfKxRWgEZUwWMNjFn8wc1-TzEZ62-8cX83x4OGyzf4oTNBf4jWgqRQEz9uiXmMzZ9ivO0Lzyt2sBCY8hjrljs8vRBYeptwuaKcUu9GjCN5cfyx-0uiTyr2s-WSZVdvBOeYrEaVu1zAuUXU00tcxxL1qJqqtITTWz3AkvW4XVP_1WmlLIfB93v2bwMzS0gM844p3Cr18";

const QStringList kButtons{QStringLiteral("sharedShareButton"), QStringLiteral("sharedCheckButton"),
                           QStringLiteral("sharedImportButton"), QStringLiteral("sharedRejectButton"),
                           QStringLiteral("sharedAcceptButton")};

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

void collectVisibleText(QQuickItem *item, QStringList &texts)
{
    if (!item || !item->isVisible())
        return;
    const QVariant text = item->property("text");
    if (text.isValid() && !text.toString().isEmpty())
        texts.append(text.toString());
    for (auto *child : item->childItems())
        collectVisibleText(child, texts);
}

void verifyPlayerCopy(const QStringList &texts)
{
    static const QStringList words{
        QString::fromUtf8("开始捕获"), QString::fromUtf8("开始验证"), QString::fromUtf8("停止捕获"),
        QString::fromUtf8("维护者工具"), QString::fromUtf8("对照核对"), QStringLiteral("opcode"),
        QStringLiteral("0x"), QStringLiteral("67ef1bb97e65"), QStringLiteral("REPLY_STATE"),
        QStringLiteral("QUEUE_REQUEST"), QStringLiteral("CONTRADICTED"), QStringLiteral("GITHUB_RAW"),
        QStringLiteral("INDEX_UNAVAILABLE"), QStringLiteral("AWAITING_CONSENT"), QStringLiteral("VERIFIED"),
        QStringLiteral("ERR_"), QStringLiteral("MRC1"), QStringLiteral(".shared"),
        QStringLiteral("PUBLISHED"), QStringLiteral("IMPORTED"), QStringLiteral("audit_pending")};
    for (const QString &text : texts) {
        for (const QString &word : words) {
            QVERIFY2(!text.contains(word, Qt::CaseInsensitive),
                     qPrintable(QStringLiteral("card leaks \"%1\": %2").arg(word, text)));
        }
    }
}

/// $defs/SharedCalibrationStatus as a Collector reports it when nothing is going on:
/// present (so the five requests exist), with no candidate and no refusal.
QJsonObject sharedNone()
{
    return {{QStringLiteral("phase"), QStringLiteral("NONE")},
            {QStringLiteral("candidates"), QJsonArray{}},
            {QStringLiteral("last_fetch_status"), QJsonValue::Null},
            {QStringLiteral("last_index_attempts"), QJsonArray{}},
            {QStringLiteral("profile_id"), QJsonValue::Null},
            {QStringLiteral("bound_at_utc"), QJsonValue::Null},
            {QStringLiteral("last_refusal"), QJsonValue::Null},
            {QStringLiteral("rejected_candidates"), 0},
            {QStringLiteral("user_rejected"), false}};
}

/// Answers the two status reads with one hand-written capture status; everything else is empty.
/// DiscardCalibration is recorded, and can be made to fail, so the rollback's refusal path can
/// be read exactly as the player would read it.
class CaptureBackend final : public mr::IBackend
{
public:
    QJsonObject capture;
    QJsonObject currentRun;
    QList<QJsonObject> discards;
    QString discardErrorCode;
    QString discardErrorMessage;

    QString backendName() const override { return QStringLiteral("mock"); }
    bool isConnected() const override { return true; }

    mr::BackendReply *request(const QString &type, const QJsonObject &payload = {}) override
    {
        auto *reply = new mr::BackendReply(type, type, this);
        if (type == QLatin1String("GetStatus"))
            reply->succeed({{QStringLiteral("capture"), capture}});
        else if (type == QLatin1String("GetCaptureStatus"))
            reply->succeed(capture);
        else if (type == QLatin1String("GetCurrentRun"))
            reply->succeed(currentRun);
        else if (type == QLatin1String("DiscardCalibration")) {
            discards.append(payload);
            if (discardErrorCode.isEmpty())
                reply->succeed({{QStringLiteral("state"), QStringLiteral("IDLE")}});
            else
                reply->fail(discardErrorCode, discardErrorMessage);
        } else
            reply->succeed({});
        return reply;
    }
};

/// A capture status with a calibration object, so the rollback's three inputs - the profile in
/// force, whether a retired one waits, and whether a duty is in flight - can each be set alone.
/// \a origin empty means no profile is in force, which is what retiring leaves behind.
QJsonObject captureWithRollback(const QString &origin, bool retiredAvailable,
                                const QString &calibrationState = QStringLiteral("OBSERVING"))
{
    QJsonObject calibration{{QStringLiteral("state"), calibrationState},
                            {QStringLiteral("blockers"), QJsonArray{}},
                            {QStringLiteral("events"), QJsonArray{}},
                            {QStringLiteral("retired_local_profile_available"), retiredAvailable},
                            {QStringLiteral("shared"), sharedNone()}};
    QJsonObject capture{{QStringLiteral("ffxiv_running"), true},
                        {QStringLiteral("state"), QStringLiteral("RUNNING")},
                        {QStringLiteral("game_build"), QStringLiteral("2026.09.01.0000.0000")},
                        {QStringLiteral("region"), QStringLiteral("CN")},
                        {QStringLiteral("profile_status"),
                         origin.isEmpty() ? QStringLiteral("UNSUPPORTED_BUILD")
                                          : QStringLiteral("VERIFIED")},
                        {QStringLiteral("calibration"), calibration}};
    if (!origin.isEmpty())
        capture.insert(QStringLiteral("profile_origin"), origin);
    return capture;
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

    mr::SharedCalibrationController *shared() const { return app->calibration()->shared(); }
    QQuickItem *item(const QString &name) const { return findVisualItem(card, name); }
    QObject *section() const { return root->findChild<QObject *>(QStringLiteral("calibrationSharedSection")); }
    QObject *dialog(const char *name) const { return section()->property(name).value<QObject *>(); }
};

/// The whole 捕获诊断 page, so that 协议档案 - the one card that is never hidden -
/// can be read exactly as a player sees it. The browser and the clipboard are
/// replaced before anything is clicked: a test never opens github.com and never
/// overwrites the developer's own clipboard.
struct PageScene
{
    std::unique_ptr<mr::MockBackend> backend = std::make_unique<mr::MockBackend>();
    std::unique_ptr<mr::IBackend> other;
    std::unique_ptr<mr::AppController> app;
    std::unique_ptr<mr::Formatters> formatters;
    std::unique_ptr<QQmlEngine> engine;
    std::unique_ptr<QObject> root;
    QQuickItem *page = nullptr;
    QStringList opened;
    QString copied;

    ~PageScene()
    {
        root.reset();
        engine.reset();
        app.reset();
    }

    /// One --mock-calibration state beside one --mock-shared state, as the
    /// screenshot targets take them.
    bool open(const QString &calibration, const QString &shared, int width = 1180)
    {
        if (!calibration.isEmpty())
            backend->setCalibrationFixture(calibration);
        if (!shared.isEmpty())
            backend->setSharedCalibrationFixture(shared);
        return openOn(backend.get(), width);
    }

    bool openOn(mr::IBackend *source, int width)
    {
        app = std::make_unique<mr::AppController>(source, nullptr);
        formatters = std::make_unique<mr::Formatters>();
        engine = std::make_unique<QQmlEngine>();
        engine->rootContext()->setContextProperty(QStringLiteral("App"), app.get());
        engine->rootContext()->setContextProperty(QStringLiteral("Fmt"), formatters.get());
        engine->rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QQmlComponent component(engine.get());
        component.setData(QStringLiteral(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: %1; height: 900; visible: true
    color: Theme.surface
    CapturePage { objectName: "capturePage"; anchors.fill: parent; anchors.margins: 16 }
})").arg(width).toUtf8(), QUrl());
        root.reset(component.create());
        if (!root) {
            qWarning("%s", qPrintable(component.errorString()));
            return false;
        }
        page = qobject_cast<QQuickItem *>(root->findChild<QObject *>(QStringLiteral("capturePage")));
        if (!page)
            return false;
        shared()->setUrlOpener([this](const QUrl &url) {
            opened.append(url.toString());
            return true;
        });
        shared()->setClipboardWriter([this](const QString &text) { copied = text; });
        return true;
    }

    mr::SharedCalibrationController *shared() const { return app->calibration()->shared(); }
    QQuickItem *item(const QString &name) const { return findVisualItem(page, name); }
    bool shows(const QString &name) const
    {
        auto *found = item(name);
        return found && found->isVisible();
    }
};

} // namespace

class SharedCalibrationCardTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void initTestCase()
    {
        const QString qmlRoot = QString::fromUtf8(MR_DESKTOP_QML_DIR);
        QVERIFY(QDir(qmlRoot).exists());
        qmlRegisterSingletonType(QUrl::fromLocalFile(qmlRoot + QStringLiteral("/Theme.qml")),
                                 "MentorRecorder", 1, 0, "Theme");
        for (const auto &directory : {QStringLiteral("/components"), QStringLiteral("/pages")}) {
            for (const auto &file :
                 QDir(qmlRoot + directory).entryList({QStringLiteral("*.qml")}, QDir::Files)) {
                const QByteArray name = file.chopped(4).toUtf8();
                qmlRegisterType(QUrl::fromLocalFile(qmlRoot + directory + QLatin1Char('/') + file),
                                "MentorRecorder", 1, 0, name.constData());
            }
        }
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
        QTest::newRow("fetching") << "fetching" << "fetching" << headline << u("正在获取其他玩家的共享校准")
                                  << QStringList{QStringLiteral("sharedImportButton")};
        QTest::newRow("verifying") << "verifying" << "verifying" << headline << u("找到共享校准")
            << QStringList{QStringLiteral("sharedImportButton"), QStringLiteral("sharedRejectButton")};
        QTest::newRow("consent") << "consent" << "consent" << QStringLiteral("sharedConsentText")
            << u("匹配时间是你申请排本的时间")
            << QStringList{QStringLiteral("sharedRejectButton"), QStringLiteral("sharedAcceptButton")};
        QTest::newRow("verified") << "verified" << "verified" << QStringLiteral("calibrationHeadline")
            << u("已使用其他玩家分享的校准（本机已核实）") << QStringList{QStringLiteral("sharedRejectButton")};
        // Gates graded by provenance (plans/shared-calibration.md 18.3): a code an index
        // lists is a matter of logging in, a pasted code no index knows waits for one queue
        // and one duty as well. Both are VERIFYING, and the buttons are the same.
        QTest::newRow("imported-published") << "imported-published" << "verifying" << headline
            << u("找到共享校准，登录时自动核实，通过就开始记录。")
            << QStringList{QStringLiteral("sharedImportButton"), QStringLiteral("sharedRejectButton")};
        QTest::newRow("imported-unpublished") << "imported-unpublished" << "verifying" << headline
            << u("已导入校准码，登录并排一次本、核实通过后启用。")
            << QStringList{QStringLiteral("sharedImportButton"), QStringLiteral("sharedRejectButton")};
        // Recording after the login burst, with the match and the duty entry still audited
        // (18.4): the card's own headline says which of the two it is, and the grey line
        // below it is the only place the audit is explained.
        QTest::newRow("verified-auditing") << "verified-auditing" << "verified"
            << QStringLiteral("calibrationHeadline")
            << u("已使用其他玩家分享的校准（登录时已在本机核实），正在自动记录。")
            << QStringList{QStringLiteral("sharedRejectButton")};
        QTest::newRow("rejected") << "rejected" << "rejected" << headline << u("共享校准与本机流量对不上")
            << QStringList{QStringLiteral("sharedCheckButton"), QStringLiteral("sharedImportButton")};
        QTest::newRow("unavailable") << "unavailable" << "unavailable" << headline << u("没取到共享校准（网络不通）")
            << QStringList{QStringLiteral("sharedCheckButton"), QStringLiteral("sharedImportButton")};
        QTest::newRow("user-rejected") << "user-rejected" << "user_rejected" << QStringLiteral("sharedCalibrationDetail")
            << u("清空进度并重新观察") << QStringList{};
        QTest::newRow("none-for-build") << "none-for-build" << "none_for_build" << headline
            << u("还没有人分享这个版本的校准")
            << QStringList{QStringLiteral("sharedCheckButton"), QStringLiteral("sharedImportButton")};
        QTest::newRow("share") << "share" << "none" << QStringLiteral("sharedShareHint") << u("本软件自己不上传任何东西")
                               << QStringList{QStringLiteral("sharedShareButton")};
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
        for (const QString &name : kButtons) {
            auto *button = scene.item(name);
            QVERIFY2(button, qPrintable(name));
            QCOMPARE(button->isVisible(), buttons.contains(name));
        }
        QStringList texts;
        collectVisibleText(scene.card, texts);
        verifyPlayerCopy(texts);
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

        auto *button = scene.item(QStringLiteral("sharedConsentImportButton"));
        QVERIFY(button);
        QTRY_VERIFY(button->isVisible());
        QVERIFY(button->property("enabled").toBool());
        // One import button on screen, never two: the Flow's own steps aside here.
        QVERIFY(!scene.item(QStringLiteral("sharedImportButton"))->isVisible());

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
        QTRY_VERIFY(scene.item(QStringLiteral("sharedConsentImportButton"))->isVisible());
        QTest::qWait(50);
        for (const QString &name : {QStringLiteral("sharedAcceptButton"),
                                    QStringLiteral("sharedConsentImportButton")}) {
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
            QString::fromUtf8("（登录时已在本机核实）")));
        QVERIFY(!headline->property("text").toString().contains(
            QString::fromUtf8("（本机已核实）")));
        QVERIFY(!scene.item(QStringLiteral("sharedCalibrationHeadline"))->isVisible());

        auto *detail = scene.item(QStringLiteral("sharedCalibrationDetail"));
        QTRY_VERIFY(detail->isVisible());
        const QString text = detail->property("text").toString();
        QVERIFY2(text.contains(QString::fromUtf8("排本和进本还在核对中")), qPrintable(text));
        QVERIFY2(text.contains(QString::fromUtf8("标记待复核")), qPrintable(text));
        // The audit takes nothing away from the recording state the plain VERIFIED shows.
        QVERIFY(!scene.item(QStringLiteral("calibrationProgress_pop_seen"))->isVisible());
        QVERIFY(!scene.item(QStringLiteral("calibrationDiscardButton"))->isVisible());
        QVERIFY(scene.item(QStringLiteral("sharedRejectButton"))->isVisible());

        QStringList texts;
        collectVisibleText(scene.card, texts);
        verifyPlayerCopy(texts);
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
                        .contains(QString::fromUtf8("（本机已核实）")));
        QVERIFY(!scene.item(QStringLiteral("sharedCalibrationDetail"))->isVisible());
    }

    void aProvisionalSharedProfileKeepsTheProvisionalWording()
    {
        // Plan §13: a queue-inferred shared profile that recorded its first duty keeps calibration
        // looking for the real match message underneath it, so the Collector reports OBSERVING
        // with that profile's id as local_profile_id. The card says it records and is still
        // improving (the provisional wording), and the shared section names where it came from.
        auto fake = std::make_unique<CaptureBackend>();
        const QJsonObject shared{{QStringLiteral("phase"), QStringLiteral("VERIFIED")},
                                 {QStringLiteral("candidates"), QJsonArray{}},
                                 {QStringLiteral("last_fetch_status"), QStringLiteral("OK")},
                                 {QStringLiteral("last_index_attempts"), QJsonArray{}},
                                 {QStringLiteral("profile_id"), QStringLiteral("cn.2026.09.01.0000.0000.shared")},
                                 {QStringLiteral("bound_at_utc"), QJsonValue::Null},
                                 {QStringLiteral("last_refusal"), QJsonValue::Null},
                                 {QStringLiteral("rejected_candidates"), 0},
                                 {QStringLiteral("user_rejected"), false}};
        fake->capture = {{QStringLiteral("ffxiv_running"), true},
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
        CardScene scene;
        auto *source = fake.get();
        scene.other = std::move(fake);
        QVERIFY(scene.openOn(source, 760));
        QTRY_VERIFY(scene.app->calibration()->provisional());
        QTRY_VERIFY(scene.shared()->inUse());

        QTRY_VERIFY(scene.item(QStringLiteral("calibrationHeadline"))->property("text").toString()
                        .contains(QString::fromUtf8("已经可以正常记录导随了")));
        auto *explanation = scene.item(QStringLiteral("calibrationExplanation"));
        QVERIFY(explanation->isVisible());
        QVERIFY(explanation->property("text").toString().contains(QString::fromUtf8("你申请了哪个随机任务")));
        auto *sharedHeadline = scene.item(QStringLiteral("sharedCalibrationHeadline"));
        QTRY_VERIFY(sharedHeadline->isVisible());
        QVERIFY(sharedHeadline->property("text").toString().contains(QString::fromUtf8("已使用其他玩家分享的校准")));
        QVERIFY(scene.item(QStringLiteral("sharedRejectButton"))->isVisible());
        QStringList texts;
        collectVisibleText(scene.card, texts);
        verifyPlayerCopy(texts);
    }

    void importShowsTheRefusalAndClosesOnSuccess()
    {
        CardScene scene;
        QVERIFY(scene.open(QStringLiteral("unavailable"), 760));
        QTRY_VERIFY(scene.item(QStringLiteral("sharedImportButton"))->isVisible());
        QVERIFY(QMetaObject::invokeMethod(scene.item(QStringLiteral("sharedImportButton")), "clicked"));
        QObject *dialog = scene.dialog("importDialog");
        QVERIFY(dialog);
        QTRY_VERIFY(dialog->property("visible").toBool());

        dialog->setProperty("code", QStringLiteral("not a code"));
        QVERIFY(QMetaObject::invokeMethod(dialog, "submit"));
        QTRY_VERIFY(!dialog->property("messageText").toString().isEmpty());
        QVERIFY(dialog->property("messageText").toString().contains(QString::fromUtf8("这不是一份能识别的校准码")));
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

    void theConsentButtonAccepts()
    {
        CardScene scene;
        QVERIFY(scene.open(QStringLiteral("consent"), 760));
        QTRY_VERIFY(scene.item(QStringLiteral("sharedAcceptButton"))->isVisible());
        QVERIFY(QMetaObject::invokeMethod(scene.item(QStringLiteral("sharedAcceptButton")), "clicked"));
        QTRY_COMPARE(scene.shared()->view(), QStringLiteral("verified"));
    }

    void aNarrowCardWrapsItsButtons()
    {
        CardScene scene;
        QVERIFY(scene.open(QStringLiteral("rejected"), 340));
        QTRY_VERIFY(scene.item(QStringLiteral("sharedCheckButton"))->isVisible());
        QTest::qWait(50);
        for (const QString &name : kButtons) {
            auto *button = scene.item(name);
            if (!button->isVisible())
                continue;
            const QPointF at = button->mapToItem(scene.card, QPointF(0, 0));
            QVERIFY2(at.x() >= 0 && at.x() + button->width() <= scene.card->width() + 0.5,
                     qPrintable(QStringLiteral("%1 overflows: x=%2 w=%3 card=%4")
                                    .arg(name).arg(at.x()).arg(button->width()).arg(scene.card->width())));
        }
    }

    // -- 协议档案 card: sharing once the calibration card is gone --------------

    void theProtocolCardOffersSharingAfterARestart()
    {
        // The normal state of the very player whose calibration is worth sharing: the app
        // was started again, the local profile records, calibration is IDLE and its card -
        // the other place 分享给其他玩家 appears - is not on the page at all.
        PageScene scene;
        QVERIFY(scene.open(QStringLiteral("idle"), QStringLiteral("share")));
        QTRY_VERIFY(scene.shared()->canShare());
        QTRY_VERIFY(scene.shows(QStringLiteral("protocolShareButton")));
        QVERIFY(scene.shows(QStringLiteral("protocolShareHint")));
        QVERIFY(!scene.shows(QStringLiteral("calibrationCard")));
        QVERIFY(!scene.shows(QStringLiteral("sharedShareButton")));

        QStringList texts;
        collectVisibleText(scene.item(QStringLiteral("protocolProfileCard")), texts);
        verifyPlayerCopy(texts);
    }

    void onlyOneShareButtonIsEverOnScreen()
    {
        // While the calibration card is up it is the card explaining the calibration that
        // just finished, so it keeps the button; the protocol card steps aside rather than
        // putting a second identical button on the same screen.
        PageScene scene;
        QVERIFY(scene.open(QStringLiteral("done"), QStringLiteral("share")));
        QTRY_VERIFY(scene.shows(QStringLiteral("calibrationCard")));
        QTRY_VERIFY(scene.shows(QStringLiteral("sharedShareButton")));
        QVERIFY(!scene.shows(QStringLiteral("protocolShareButton")));
        QVERIFY(!scene.shows(QStringLiteral("protocolShareHint")));
    }

    void bothShareButtonsTakeTheSamePath()
    {
        PageScene restarted;
        QVERIFY(restarted.open(QStringLiteral("idle"), QStringLiteral("share")));
        QTRY_VERIFY(restarted.shows(QStringLiteral("protocolShareButton")));
        QVERIFY(QMetaObject::invokeMethod(restarted.item(QStringLiteral("protocolShareButton")), "clicked"));
        QTRY_COMPARE(restarted.opened.size(), 1);

        PageScene calibrated;
        QVERIFY(calibrated.open(QStringLiteral("done"), QStringLiteral("share")));
        QTRY_VERIFY(calibrated.shows(QStringLiteral("sharedShareButton")));
        QVERIFY(QMetaObject::invokeMethod(calibrated.item(QStringLiteral("sharedShareButton")), "clicked"));
        QTRY_COMPARE(calibrated.opened.size(), 1);

        // Same request, same clipboard, same address, same sentence: one code path.
        QCOMPARE(restarted.opened, calibrated.opened);
        QCOMPARE(restarted.copied, calibrated.copied);
        QVERIFY(!restarted.copied.isEmpty());
        QVERIFY(restarted.opened.constFirst().startsWith(QStringLiteral("https://github.com/")));
        QTRY_VERIFY(!restarted.app->toastMessage().isEmpty());
        QTRY_COMPARE(restarted.app->toastMessage(), calibrated.app->toastMessage());
        QVERIFY(restarted.app->toastMessage().contains(QString::fromUtf8("已在系统浏览器中打开")));
    }

    // -- 协议档案 card: 重新校准 ---------------------------------------------

    void theProtocolCardOffersRecalibrationForALocalProfile()
    {
        // The player whose own machine calibrated the wrong message. Once the local profile
        // binds, the calibration card is gone and with it 清空进度并重新观察 and 导入校准码,
        // so until now the only way back was renaming a file in Explorer.
        PageScene scene;
        // No duty in flight: the mock's own default is one, and retiring the profile would
        // close it (recalibrationWaitsForTheDutyToEnd pins that case).
        scene.backend->setLiveMode(mr::MockBackend::LiveMode::None);
        QVERIFY(scene.open(QStringLiteral("idle"), QStringLiteral("share")));
        QTRY_VERIFY(scene.shows(QStringLiteral("protocolRecalibrateButton")));
        QVERIFY(!scene.shows(QStringLiteral("calibrationCard")));
        QTRY_COMPARE(scene.app->currentRunState(), QStringLiteral("IDLE"));
        QVERIFY(scene.item(QStringLiteral("protocolRecalibrateButton"))->property("enabled").toBool());
        QVERIFY(!scene.shows(QStringLiteral("protocolRecalibrateHint")));

        auto *dialog = scene.root->findChild<QObject *>(QStringLiteral("protocolRecalibrateDialog"));
        QVERIFY(dialog);
        QVERIFY(!dialog->property("visible").toBool());
        QCOMPARE(scene.backend->discardCalibrationCount(), 0);

        // Opening it sends nothing: the player is told what stopping the profile costs before
        // anything at all happens to it.
        QVERIFY(QMetaObject::invokeMethod(scene.item(QStringLiteral("protocolRecalibrateButton")), "clicked"));
        QTRY_VERIFY(dialog->property("visible").toBool());
        QCOMPARE(scene.backend->discardCalibrationCount(), 0);

        auto *confirm = dialog->findChild<QQuickItem *>(QStringLiteral("protocolRecalibrateConfirm"));
        QVERIFY(confirm);
        QVERIFY(QMetaObject::invokeMethod(confirm, "clicked"));

        QTRY_COMPARE(scene.backend->discardCalibrationCount(), 1);
        QVERIFY(scene.backend->lastDiscardCalibration()
                    .value(QStringLiteral("retire_local_profile")).toBool());
    }

    void recalibrationWaitsForTheDutyToEnd()
    {
        // Retiring the profile closes the run in flight the way a stopped capture does, which
        // would cost the player the duty they are sitting in. The button says so and waits.
        PageScene scene;
        scene.backend->setLiveMode(mr::MockBackend::LiveMode::Matched);
        QVERIFY(scene.open(QStringLiteral("idle"), QStringLiteral("share")));
        QTRY_COMPARE(scene.app->currentRunState(), QStringLiteral("MENTOR_MATCHED"));
        QTRY_VERIFY(scene.shows(QStringLiteral("protocolRecalibrateButton")));

        QVERIFY(!scene.item(QStringLiteral("protocolRecalibrateButton"))->property("enabled").toBool());
        QTRY_VERIFY(scene.shows(QStringLiteral("protocolRecalibrateHint")));
        QCOMPARE(scene.item(QStringLiteral("protocolRecalibrateHint"))->property("text").toString(),
                 QString::fromUtf8("副本进行中，结束后再试"));

        QStringList texts;
        collectVisibleText(scene.item(QStringLiteral("protocolProfileCard")), texts);
        verifyPlayerCopy(texts);
    }

    // -- 协议档案 card: 恢复上一份本机校准 ------------------------------------

    void theRollbackIsOfferedOnlyWhenARetiredProfileIsWaiting_data()
    {
        QTest::addColumn<QString>("origin");
        QTest::addColumn<bool>("retiredAvailable");
        QTest::addColumn<bool>("rollback");
        QTest::addColumn<bool>("recalibrate");

        // Straight after 重新校准: nothing records, the retired file waits.
        QTest::newRow("retired") << QString() << true << true << false;
        // Her own calibration is back in force: the rollback is spent, 重新校准 returns.
        QTest::newRow("local-in-force") << "LOCAL_CALIBRATION" << false << false << true;
        // Nothing was ever retired, so there is nothing to put back.
        QTest::newRow("nothing-retired") << QString() << false << false << false;
        // A shared profile records after the retirement; the way back is still open.
        QTest::newRow("shared-took-over") << "SHARED_CALIBRATION" << true << true << false;
    }

    void theRollbackIsOfferedOnlyWhenARetiredProfileIsWaiting()
    {
        QFETCH(QString, origin);
        QFETCH(bool, retiredAvailable);
        QFETCH(bool, rollback);
        QFETCH(bool, recalibrate);

        auto fake = std::make_unique<CaptureBackend>();
        fake->capture = captureWithRollback(origin, retiredAvailable);
        PageScene scene;
        auto *source = fake.get();
        scene.other = std::move(fake);
        QVERIFY(scene.openOn(source, 1180));
        QTRY_COMPARE(scene.app->calibration()->retiredLocalProfileAvailable(), retiredAvailable);

        QCOMPARE(scene.shows(QStringLiteral("protocolRestoreButton")), rollback);
        QCOMPARE(scene.shows(QStringLiteral("protocolRecalibrateButton")), recalibrate);
        // The two are opposites and must never share a screen.
        QVERIFY(!(rollback && recalibrate));

        if (rollback) {
            QStringList texts;
            collectVisibleText(scene.item(QStringLiteral("protocolProfileCard")), texts);
            verifyPlayerCopy(texts);
        }
    }

    void theRollbackDialogAsksFirstAndThenSendsTheFlag()
    {
        auto fake = std::make_unique<CaptureBackend>();
        fake->capture = captureWithRollback(QString(), true);
        PageScene scene;
        auto *source = fake.get();
        scene.other = std::move(fake);
        QVERIFY(scene.openOn(source, 1180));
        QTRY_VERIFY(scene.shows(QStringLiteral("protocolRestoreButton")));
        QVERIFY(scene.item(QStringLiteral("protocolRestoreButton"))->property("enabled").toBool());

        auto *dialog = scene.root->findChild<QObject *>(QStringLiteral("protocolRestoreDialog"));
        QVERIFY(dialog);
        QVERIFY(!dialog->property("visible").toBool());
        QVERIFY(QMetaObject::invokeMethod(scene.item(QStringLiteral("protocolRestoreButton")), "clicked"));
        QTRY_VERIFY(dialog->property("visible").toBool());
        QVERIFY(source->discards.isEmpty());

        auto *confirm = dialog->findChild<QQuickItem *>(QStringLiteral("protocolRestoreConfirm"));
        QVERIFY(confirm);
        QVERIFY(QMetaObject::invokeMethod(confirm, "clicked"));

        QTRY_COMPARE(source->discards.size(), 1);
        const QJsonObject sent = source->discards.constFirst();
        QVERIFY(sent.value(QStringLiteral("restore_local_profile")).toBool());
        // Never both: the Collector answers ERR_BAD_REQUEST for a request carrying the pair.
        QVERIFY(!sent.contains(QStringLiteral("retire_local_profile")));
    }

    void aRefusedRollbackShowsTheCollectorsOwnSentence()
    {
        auto fake = std::make_unique<CaptureBackend>();
        fake->capture = captureWithRollback(QString(), true);
        fake->discardErrorCode = QStringLiteral("ERR_CALIBRATION_NOT_READY");
        fake->discardErrorMessage = QString::fromUtf8("上一份本机校准已经无法使用，请重新校准。");
        PageScene scene;
        auto *source = fake.get();
        scene.other = std::move(fake);
        QVERIFY(scene.openOn(source, 1180));
        QTRY_VERIFY(scene.shows(QStringLiteral("protocolRestoreButton")));

        scene.app->calibration()->restoreLocalProfile();

        QTRY_VERIFY(scene.shows(QStringLiteral("protocolCalibrationError")));
        QCOMPARE(scene.item(QStringLiteral("protocolCalibrationError"))->property("text").toString(),
                 source->discardErrorMessage);
        // The error token itself never reaches the page.
        QStringList texts;
        collectVisibleText(scene.item(QStringLiteral("protocolProfileCard")), texts);
        verifyPlayerCopy(texts);
    }

    void theRollbackWaitsForTheDutyToEnd()
    {
        // Putting the old profile back closes the run in flight, exactly as retiring does.
        auto fake = std::make_unique<CaptureBackend>();
        fake->capture = captureWithRollback(QString(), true);
        fake->currentRun = QJsonObject{{QStringLiteral("state"), QStringLiteral("ENTERED_DUTY")}};
        PageScene scene;
        auto *source = fake.get();
        scene.other = std::move(fake);
        QVERIFY(scene.openOn(source, 1180));
        QTRY_COMPARE(scene.app->currentRunState(), QStringLiteral("ENTERED_DUTY"));
        QTRY_VERIFY(scene.shows(QStringLiteral("protocolRestoreButton")));

        QVERIFY(!scene.item(QStringLiteral("protocolRestoreButton"))->property("enabled").toBool());
        QTRY_VERIFY(scene.shows(QStringLiteral("protocolRecalibrateHint")));
        QCOMPARE(scene.item(QStringLiteral("protocolRecalibrateHint"))->property("text").toString(),
                 QString::fromUtf8("副本进行中，结束后再试"));
    }

    void onlyALocalCalibrationIsOfferedForSharing_data()
    {
        QTest::addColumn<QString>("origin");
        // Someone else's calibration is never passed on, the shipped profile needs no
        // sharing, and with no profile in force there is nothing to share yet.
        QTest::newRow("shared") << "SHARED_CALIBRATION";
        QTest::newRow("shipped") << "SHIPPED";
        QTest::newRow("no-profile") << "";
    }

    void onlyALocalCalibrationIsOfferedForSharing()
    {
        QFETCH(QString, origin);

        auto fake = std::make_unique<CaptureBackend>();
        fake->capture = {{QStringLiteral("ffxiv_running"), true},
                         {QStringLiteral("state"), QStringLiteral("RUNNING")},
                         {QStringLiteral("profile_status"),
                          origin.isEmpty() ? QStringLiteral("UNSUPPORTED_BUILD")
                                           : QStringLiteral("VERIFIED")},
                         {QStringLiteral("calibration"), QJsonObject{
                              {QStringLiteral("state"), QStringLiteral("IDLE")},
                              {QStringLiteral("blockers"), QJsonArray{}},
                              {QStringLiteral("events"), QJsonArray{}},
                              {QStringLiteral("shared"), sharedNone()}}}};
        if (!origin.isEmpty())
            fake->capture.insert(QStringLiteral("profile_origin"), origin);

        PageScene scene;
        auto *source = fake.get();
        scene.other = std::move(fake);
        QVERIFY(scene.openOn(source, 1180));
        QTRY_VERIFY(scene.shared()->available());
        QVERIFY(!scene.shared()->canShare());
        QVERIFY(!scene.shows(QStringLiteral("protocolShareButton")));
        QVERIFY(!scene.shows(QStringLiteral("protocolShareHint")));
        // 重新校准 retracts this machine's own guess. Someone else's calibration is stopped
        // through 不用共享的，我自己校准, the shipped profile is not ours to retract, and with
        // no profile in force there is nothing to stop using.
        QVERIFY(!scene.shows(QStringLiteral("protocolRecalibrateButton")));
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
    // Wrapping is measured in real glyphs; the offscreen platform finds no system fonts.
    QStringList cjkFamilies;
    const QString windowsDir = qEnvironmentVariable("WINDIR", QStringLiteral("C:/Windows"));
    for (const char *file : {"Fonts/msyh.ttc", "Fonts/simhei.ttf"}) {
        const int id = QFontDatabase::addApplicationFont(QDir(windowsDir).filePath(QString::fromLatin1(file)));
        for (const QString &family : QFontDatabase::applicationFontFamilies(id)) {
            if (!cjkFamilies.contains(family))
                cjkFamilies.append(family);
        }
    }
    if (cjkFamilies.isEmpty()) {
        qCritical("SharedCalibrationCardTests requires a loadable Windows CJK font (msyh.ttc or simhei.ttf).");
        return 6;
    }
    QFont testFont;
    testFont.setFamilies(cjkFamilies);
    app.setFont(testFont);
#endif
    SharedCalibrationCardTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "SharedCalibrationCardTests.moc"
