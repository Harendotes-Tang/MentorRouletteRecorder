// Submission through the shipping duty-community dialog opened from the dashboard.
#include "DesktopTestRunner.h"
#include "DutyCommunitySceneFixture.h"
#include "TestCollectorGuard.h"
#include "BundledFonts.h"

#include <QApplication>
#include <QClipboard>
#include <QDesktopServices>
#include <QDir>
#include <QFont>
#include <QQuickStyle>
#include <QScopeGuard>
#include <QStandardPaths>
#include <QTest>

namespace {
class DutyCommunitySubmissionTests : public QObject
{
    Q_OBJECT
private Q_SLOTS:
    void initTestCase();
    void communityDetailActionsTakeTheTickedEntries();
    void communitySubmissionLeavesOutWhatIsAlreadyPublic();
    void communityBrowserSubmissionWaitsForThePaste();
    void communitySubmissionUsesAdoptedOrConfirmedRevision();
    void communitySubmissionComparesAgainstTheWholeBatch();
    void communitySubmissionRestoresRejectedRevisionText();
    void communitySubmissionWaitsForCurrentPublicSnapshot();
    void communitySubmissionIgnoresLatePreparation();
public:
    Q_INVOKABLE void rememberUrl(const QUrl &url) { opened << url; }
    QList<QUrl> opened;
};

QVariantMap asPublished(QVariantMap content, int issue, int revision = 1)
{
    content.insert(QStringLiteral("author"), QVariantMap{{QStringLiteral("login"), QStringLiteral("player")},
                                                         {QStringLiteral("id"), 75}});
    content.insert(QStringLiteral("status"), QStringLiteral("community"));
    content.insert(QStringLiteral("license"), QStringLiteral("CC-BY-4.0"));
    content.insert(QStringLiteral("revision"), revision);
    content.insert(QStringLiteral("issue"), issue);
    content.insert(QStringLiteral("updated_at"), QStringLiteral("2026-11-23T10:00:00Z"));
    return {{QStringLiteral("content"), content}};
}
} // namespace

void DutyCommunitySubmissionTests::initTestCase()
{
    QVERIFY(QDir().mkpath(QString::fromUtf8(MR_SOURCE_DIR) + kEvidence()));
    registerSceneQmlTypes();
}

// 「我勾选 2 个点投稿只给了一个」(owner, 2026-11-23): with two entries ticked, the 投稿 under the
// open entry sent that entry alone. While entries are ticked, 导出 and 投稿 there take the
// ticked entries and say how many; with none ticked they take the open entry.
void DutyCommunitySubmissionTests::communityDetailActionsTakeTheTickedEntries()
{
    const auto s = [](const char *text) { return QString::fromUtf8(text); };
    const auto ids = [](const QVariant &value) { return rowIds(value); };
    Scene scene;
    QObject *dialog = openCommunityDialog(scene);
    QVERIFY2(dialog, qPrintable(scene.errors));
    const QStringList shown = ids(dialog->property("filtered"));
    QObject *submission = dialog->findChild<QObject *>(QStringLiteral("dutyCommunitySubmission"));
    QVERIFY(submission);
    const auto submitButton = [&] { return scene.item(QStringLiteral("dutyCommunitySubmit"), true); };
    const auto exportButton = [&] { return scene.item(QStringLiteral("dutyCommunityExport"), true); };

    // Nothing ticked: the open entry alone.
    QVERIFY(QMetaObject::invokeMethod(dialog, "select", Q_ARG(QVariant, shown.at(0))));
    QTRY_VERIFY(submitButton());
    QCOMPARE(submitButton()->property("text").toString(), s("投稿"));
    QCOMPARE(exportButton()->property("text").toString(), s("导出"));
    QVERIFY(scene.click(QStringLiteral("dutyCommunitySubmit")));
    QTRY_COMPARE(dialog->property("mode").toString(), QStringLiteral("submit"));
    QCOMPARE(ids(submission->property("selected")), QStringList{shown.at(0)});
    QVERIFY(scene.click(QStringLiteral("dutyCommunitySubmissionCancel")));
    QTRY_COMPARE(dialog->property("mode").toString(), QStringLiteral("view"));

    // Two others ticked while the first is open: both of them, and only them.
    QVERIFY(QMetaObject::invokeMethod(dialog, "toggleChecked", Q_ARG(QVariant, shown.at(1))));
    QVERIFY(QMetaObject::invokeMethod(dialog, "toggleChecked", Q_ARG(QVariant, shown.at(2))));
    QCOMPARE(dialog->property("currentId").toString(), shown.at(0));
    QTRY_COMPARE(submitButton()->property("text").toString(), s("投稿 2 项"));
    QCOMPARE(exportButton()->property("text").toString(), s("导出 2 项"));
    QVERIFY(scene.click(QStringLiteral("dutyCommunitySubmit")));
    QTRY_COMPARE(dialog->property("mode").toString(), QStringLiteral("submit"));
    QCOMPARE(ids(submission->property("selected")), (QStringList{shown.at(1), shown.at(2)}));
    QTRY_VERIFY(scene.item(QStringLiteral("dutyCommunitySubmitBrowser"), true));
    QCOMPARE(scene.item(QStringLiteral("dutyCommunitySubmitBrowser"), true)->property("text").toString(),
             s("点此复制（2 项）并粘贴到 GitHub 当中"));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

// 「如果宏的内容一致……告诉提交者」(owner, 2026-11-23): what the public library already holds is
// left out before anything is sent, said why, and a link to it points at the public entry. The
// licence reads as what others may do, not as a code.
void DutyCommunitySubmissionTests::communitySubmissionLeavesOutWhatIsAlreadyPublic()
{
    const auto s = [](const char *text) { return QString::fromUtf8(text); };
    Scene scene;
    QObject *dialog = openCommunityDialog(scene);
    QVERIFY2(dialog, qPrintable(scene.errors));
    QVariantList local = qmlList(dialog->property("localRows"));
    const auto contentOf = [&](int index) { return local.at(index).toMap().value(QStringLiteral("content")).toMap(); };
    const auto idOf = [&](int index) { return contentOf(index).value(QStringLiteral("id")).toString(); };
    // Row 1 (a reminder) links row 2 (a macro); row 4 is a macro that is itself published.
    QVariantMap reminder = local.at(1).toMap();
    QVariantMap reminderContent = contentOf(1);
    reminderContent.insert(QStringLiteral("links"), QVariantList{idOf(2)});
    reminder.insert(QStringLiteral("content"), reminderContent);
    local.replace(1, reminder);
    dialog->setProperty("localRows", local);

    QVariantMap sameText = publicEntry(900, "macro", "", "", 0);
    QVariantMap sameTextContent = sameText.value(QStringLiteral("content")).toMap();
    sameTextContent.insert(QStringLiteral("body"), contentOf(2).value(QStringLiteral("body")).toString() + QStringLiteral("  \n"));
    sameText.insert(QStringLiteral("content"), sameTextContent);
    dialog->setProperty("publicRows", QVariantList{sameText, asPublished(contentOf(4), 901)});

    const QVariant chosen = QStringList{idOf(1), idOf(4)};
    QVERIFY(QMetaObject::invokeMethod(dialog, "submitRows", Q_ARG(QVariant, chosen)));
    QTRY_COMPARE(dialog->property("mode").toString(), QStringLiteral("submit"));
    QObject *submission = dialog->findChild<QObject *>(QStringLiteral("dutyCommunitySubmission"));
    QVERIFY(submission);
    QCOMPARE(rowIds(submission->property("selected")), (QStringList{idOf(1), idOf(4), idOf(2)}));
    QCOMPARE(rowIds(submission->property("submittable")), QStringList{idOf(1)});
    const QVariantMap twins = qmlMap(submission->property("twins"));
    QCOMPARE(twins.value(idOf(2)).toMap().value(QStringLiteral("kind")).toString(), QStringLiteral("same_text"));
    QCOMPARE(twins.value(idOf(4)).toMap().value(QStringLiteral("kind")).toString(), QStringLiteral("unchanged"));
    const auto drafts = qmlMap(submission->property("plan")).value(QStringLiteral("items")).toList();
    QCOMPARE(drafts.size(), 1);
    QCOMPARE(drafts.at(0).toMap().value(QStringLiteral("links")).toStringList(),
             QStringList{sameTextContent.value(QStringLiteral("id")).toString()});
    QTRY_VERIFY(scene.item(QStringLiteral("dutyCommunitySubmitBrowser"), true));
    QCOMPARE(scene.item(QStringLiteral("dutyCommunitySubmitBrowser"), true)->property("text").toString(),
             s("点此复制（1 项）并粘贴到 GitHub 当中"));
    QVERIFY(visibleText(scene.item(QStringLiteral("dutyCommunitySubmission"), true),
                        s("公共库已有相同内容「Entry 900」（first-author），不再投稿")));

    // The licence names what others may do; the code it sends stays the same.
    QQuickItem *license = scene.item(QStringLiteral("dutyCommunityLicense"), true);
    QVERIFY(license);
    QCOMPARE(license->property("displayText").toString(), s("注明作者即可转载、改编（CC BY 4.0）"));
    QCOMPARE(license->property("currentValue").toString(), QStringLiteral("CC-BY-4.0"));

    // Only repeats chosen: nothing to send.
    QVERIFY(scene.click(QStringLiteral("dutyCommunitySubmissionCancel")));
    QTRY_COMPARE(dialog->property("mode").toString(), QStringLiteral("view"));
    QVERIFY(QMetaObject::invokeMethod(dialog, "submitRows", Q_ARG(QVariant, QStringList{idOf(4)})));
    QTRY_COMPARE(dialog->property("mode").toString(), QStringLiteral("submit"));
    scene.item(QStringLiteral("dutyCommunityConsent"), true)->setProperty("checked", true);
    QTRY_VERIFY(!scene.item(QStringLiteral("dutyCommunitySubmitBrowser"), true)->property("enabled").toBool());

    // An edit of one's own published entry that keeps its text goes out even when an older
    // entry shares that text - the publisher takes it as a title change, not a repeat.
    QVERIFY(scene.click(QStringLiteral("dutyCommunitySubmissionCancel")));
    QTRY_COMPARE(dialog->property("mode").toString(), QStringLiteral("view"));
    QVariantMap oldTitle = contentOf(0);
    oldTitle.insert(QStringLiteral("title"), QStringLiteral("Old title"));
    QVariantMap olderTwin = publicEntry(902, "macro", "", "", 0);
    QVariantMap olderTwinContent = olderTwin.value(QStringLiteral("content")).toMap();
    olderTwinContent.insert(QStringLiteral("body"), contentOf(0).value(QStringLiteral("body")));
    olderTwin.insert(QStringLiteral("content"), olderTwinContent);
    dialog->setProperty("publicRows", QVariantList{asPublished(oldTitle, 903), olderTwin});
    QVERIFY(QMetaObject::invokeMethod(dialog, "submitRows", Q_ARG(QVariant, QVariant(QStringList{idOf(0)}))));
    QTRY_COMPARE(dialog->property("mode").toString(), QStringLiteral("submit"));
    QCOMPARE(rowIds(submission->property("submittable")), QStringList{idOf(0)});
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

// 「点了后……ta 会再次复制宏」(owner, 2026-11-23): after 点此复制并粘贴到 GitHub 当中 the paste steps stay,
// 再次复制 puts the submission back, and 回到粘贴步骤 returns to them from another entry until 完成.
void DutyCommunitySubmissionTests::communityBrowserSubmissionWaitsForThePaste()
{
    opened.clear();
    Scene scene;
    QObject *dialog = openCommunityDialog(scene);
    QVERIFY2(dialog, qPrintable(scene.errors));
    QObject *submission = dialog->findChild<QObject *>(QStringLiteral("dutyCommunitySubmission"));
    QDesktopServices::setUrlHandler(QStringLiteral("https"), this, "rememberUrl");
    QVERIFY(QMetaObject::invokeMethod(dialog, "submitRows", Q_ARG(QVariant, QVariant(rowIds(dialog->property("filtered")).mid(0, 1)))));
    QTRY_COMPARE(dialog->property("mode").toString(), QStringLiteral("submit"));
    scene.item(QStringLiteral("dutyCommunityConsent"), true)->setProperty("checked", true);
    QVERIFY(scene.click(QStringLiteral("dutyCommunitySubmitBrowser")));
    QTRY_VERIFY(submission && submission->property("copied").toBool());
    QVERIFY(scene.item(QStringLiteral("dutyCommunityPasteWarning"), true));
    QCOMPARE(QGuiApplication::clipboard()->text(), QStringLiteral("mock submission"));
    QVERIFY(opened.size() == 1 && opened.at(0).path().endsWith(QStringLiteral("/issues/new")));
    QGuiApplication::clipboard()->setText(QStringLiteral("/p 123123123"));
    QVERIFY(scene.click(QStringLiteral("dutyCommunityCopyAgain")));
    QCOMPARE(QGuiApplication::clipboard()->text(), QStringLiteral("mock submission"));
    QVERIFY(QMetaObject::invokeMethod(dialog, "select", Q_ARG(QVariant, rowIds(dialog->property("filtered")).at(1))));
    QVERIFY(scene.click(QStringLiteral("dutyCommunityBackToPaste")));
    QTRY_VERIFY(scene.item(QStringLiteral("dutyCommunityPasteWarning"), true));
    QVERIFY(scene.click(QStringLiteral("dutyCommunitySubmissionDone")));
    QVERIFY(!submission->property("copied").toBool() && dialog->property("mode").toString() == QStringLiteral("view"));
    QDesktopServices::unsetUrlHandler(QStringLiteral("https"));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void DutyCommunitySubmissionTests::communitySubmissionUsesAdoptedOrConfirmedRevision()
{
    Scene scene;
    QObject *dialog = openCommunityDialog(scene);
    QVERIFY2(dialog, qPrintable(scene.errors));
    QObject *submission = dialog->findChild<QObject *>(QStringLiteral("dutyCommunitySubmission"));
    QVERIFY(submission);
    QVariantMap row = qmlList(dialog->property("localRows")).first().toMap();
    const auto idKey = QStringLiteral("id");
    const auto contentKey = QStringLiteral("content");
    const auto basesKey = QStringLiteral("base_revisions");
    QVariantMap draft = row.value(contentKey).toMap();
    const QString id = draft.value(idKey).toString();
    const QVariant chosen = QStringList{id};
    row.insert(QStringLiteral("upstream"), asPublished(draft, 901).value(contentKey));
    QVariantMap latest = draft;
    latest.insert(QStringLiteral("title"), QStringLiteral("Title on the other device"));
    draft.insert(QStringLiteral("role"), QStringLiteral("PLD"));
    row.insert(contentKey, draft);
    dialog->setProperty("localRows", QVariantList{row});
    dialog->setProperty("publicRows", QVariantList{asPublished(latest, 901, 2)});
    QVERIFY(QMetaObject::invokeMethod(dialog, "submitRows", Q_ARG(QVariant, chosen)));
    QTRY_VERIFY(scene.item(QStringLiteral("dutyCommunityOutdatedBase"), true));
    const auto browser = [&] { return scene.item(QStringLiteral("dutyCommunitySubmitBrowser"), true); };
    auto *consent = scene.item(QStringLiteral("dutyCommunityConsent"), true);
    QVERIFY(browser() && consent);
    consent->setProperty("checked", true);
    QVERIFY(!browser()->property("enabled").toBool());
    QVERIFY(QMetaObject::invokeMethod(submission, "submit", Q_ARG(QVariant, false)));
    QVERIFY(scene.backend.lastCommunitySubmission.isEmpty());

    // No cached entry: the real adopted revision still goes into the browser payload.
    QDesktopServices::setUrlHandler(QStringLiteral("https"), this, "rememberUrl");
    const auto urlGuard = qScopeGuard([] { QDesktopServices::unsetUrlHandler(QStringLiteral("https")); });
    dialog->setProperty("publicRows", QVariantList{});
    consent->setProperty("checked", true);
    QTRY_VERIFY(browser()->property("enabled").toBool());
    QVERIFY(scene.click(QStringLiteral("dutyCommunitySubmitBrowser")));
    QTRY_VERIFY(submission->property("copied").toBool());
    QCOMPARE(scene.backend.lastCommunitySubmission.value(basesKey).toObject().value(id).toInt(), 1);
    QVERIFY(scene.click(QStringLiteral("dutyCommunitySubmissionDone")));

    // An authored draft without an adopted source must be compared and confirmed for this revision.
    row.remove(QStringLiteral("upstream"));
    dialog->setProperty("localRows", QVariantList{row});
    dialog->setProperty("publicRows", QVariantList{asPublished(latest, 901, 2)});
    dialog->setProperty("configured", true);
    submission->setProperty("account", QVariantMap{{idKey, 75}, {QStringLiteral("login"), QStringLiteral("player")}});
    QVERIFY(QMetaObject::invokeMethod(dialog, "submitRows", Q_ARG(QVariant, chosen)));
    auto *compare = scene.item(QStringLiteral("dutyCommunityCompareRevision"), true);
    auto *confirm = scene.item(QStringLiteral("dutyCommunityConfirmRevision"), true);
    auto *native = scene.item(QStringLiteral("dutyCommunitySubmitNative"), true);
    QVERIFY(compare && confirm && native);
    QVERIFY(!confirm->property("enabled").toBool());
    QVERIFY(!native->property("enabled").toBool());
    QVERIFY(QMetaObject::invokeMethod(compare, "click"));
    auto *difference = scene.item(QStringLiteral("dutyCommunityRevisionComparison"), true);
    QVERIFY(difference);
    const QString text = difference->property("text").toString();
    QVERIFY(text.contains(QStringLiteral("Title on the other device")));
    QVERIFY(text.contains(QStringLiteral("PLD")));
    QVERIFY(QMetaObject::invokeMethod(confirm, "click"));
    QTRY_VERIFY(confirm->property("checked").toBool());
    consent->setProperty("checked", true);
    QTRY_VERIFY(native->property("enabled").toBool());
    latest.insert(QStringLiteral("title"), QStringLiteral("Third revision"));
    dialog->setProperty("publicRows", QVariantList{asPublished(latest, 901, 3)});
    QTRY_VERIFY(!confirm->property("checked").toBool() && !native->property("enabled").toBool());
    QVERIFY(!consent->property("checked").toBool());

    QVERIFY(QMetaObject::invokeMethod(compare, "click"));
    QVERIFY(QMetaObject::invokeMethod(confirm, "click"));
    QTRY_VERIFY(confirm->property("checked").toBool());
    auto *license = scene.item(QStringLiteral("dutyCommunityLicense"), true);
    QVERIFY(license);
    license->setProperty("currentIndex", 1);
    QTRY_VERIFY(!confirm->property("checked").toBool());
    QVERIFY(QMetaObject::invokeMethod(compare, "click"));
    difference = scene.item(QStringLiteral("dutyCommunityRevisionComparison"), true);
    QVERIFY(difference && difference->property("text").toString().contains(QStringLiteral("CC-BY-SA-4.0")));
    QVERIFY(QMetaObject::invokeMethod(confirm, "click"));
    consent->setProperty("checked", true);
    QTRY_VERIFY(native->property("enabled").toBool());
    QVERIFY(QMetaObject::invokeMethod(native, "click"));
    QCOMPARE(scene.backend.lastCommunitySubmission.value(QStringLiteral("action")).toString(), QStringLiteral("submit"));
    QCOMPARE(scene.backend.lastCommunitySubmission.value(basesKey).toObject().value(id).toInt(), 3);
    QCOMPARE(scene.backend.lastCommunitySubmission.value(QStringLiteral("items")).toArray().first().toObject(),
             QJsonObject::fromVariantMap(draft));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void DutyCommunitySubmissionTests::communitySubmissionComparesAgainstTheWholeBatch()
{
    Scene scene;
    QObject *dialog = openCommunityDialog(scene);
    QVERIFY2(dialog, qPrintable(scene.errors));
    const auto idKey = QStringLiteral("id");
    const auto bodyKey = QStringLiteral("body");
    const auto contentKey = QStringLiteral("content");
    const auto linksKey = QStringLiteral("links");
    QVariantList rows = qmlList(dialog->property("localRows"));
    auto revised = rows.at(0).toMap();
    auto reminder = rows.at(1).toMap();
    auto fresh = rows.at(2).toMap();
    auto revisedDraft = revised.value(contentKey).toMap();
    auto reminderDraft = reminder.value(contentKey).toMap();
    auto freshDraft = fresh.value(contentKey).toMap();
    const auto published = asPublished(revisedDraft, 901);
    const QString revisedId = revisedDraft.value(idKey).toString();
    const QString reminderId = reminderDraft.value(idKey).toString();
    const QString freshId = freshDraft.value(idKey).toString();
    auto repeatDraft = freshDraft;
    const auto repeatId = QStringLiteral("00000000-0000-4000-8000-000000000006");
    repeatDraft.insert(idKey, repeatId);
    repeatDraft.insert(bodyKey, revisedDraft.value(bodyKey));
    freshDraft.insert(bodyKey, revisedDraft.value(bodyKey));
    revisedDraft.insert(bodyKey, QStringLiteral("/p New body for the published macro"));
    reminderDraft.insert(linksKey, QVariantList{freshId, repeatId});
    revised.insert(contentKey, revisedDraft);
    revised.insert(QStringLiteral("upstream"), published.value(contentKey));
    reminder.insert(contentKey, reminderDraft);
    fresh.insert(contentKey, freshDraft);
    auto repeat = fresh;
    repeat.insert(contentKey, repeatDraft);
    dialog->setProperty("publicRows", QVariantList{published});
    dialog->setProperty("localRows", QVariantList{revised, reminder, fresh, repeat});
    QObject *submission = dialog->findChild<QObject *>(QStringLiteral("dutyCommunitySubmission"));
    QVERIFY(submission);
    QDesktopServices::setUrlHandler(QStringLiteral("https"), this, "rememberUrl");
    const auto urlGuard = qScopeGuard([] { QDesktopServices::unsetUrlHandler(QStringLiteral("https")); });
    for (const QStringList &order : {QStringList{revisedId, freshId, reminderId},
                                      QStringList{freshId, revisedId, reminderId}}) {
        QVERIFY(QMetaObject::invokeMethod(dialog, "submitRows", Q_ARG(QVariant, QVariant(order))));
        QCOMPARE(rowIds(submission->property("submittable")), order);
        scene.item(QStringLiteral("dutyCommunityConsent"), true)->setProperty("checked", true);
        QVERIFY(scene.click(QStringLiteral("dutyCommunitySubmitBrowser")));
        QTRY_VERIFY(submission->property("copied").toBool());
        const auto sent = scene.backend.lastCommunitySubmission.value(QStringLiteral("items")).toArray();
        QCOMPARE(sent.size(), 3);
        for (qsizetype i = 0; i < sent.size(); ++i)
            QCOMPARE(sent.at(i).toObject().value(idKey).toString(), order.at(i));
        QCOMPARE(sent.last().toObject().value(linksKey).toArray(), QJsonArray{freshId});
        QVERIFY(scene.click(QStringLiteral("dutyCommunitySubmissionDone")));
    }
    // Redirecting the new duplicate back to the existing link makes this revision unchanged.
    auto publicReminder = reminderDraft;
    publicReminder.insert(linksKey, QVariantList{revisedId});
    reminderDraft.insert(linksKey, QVariantList{freshId});
    reminder.insert(contentKey, reminderDraft);
    const auto unrelated = rows.at(3).toMap();
    const QString unrelatedId = unrelated.value(contentKey).toMap().value(idKey).toString();
    dialog->setProperty("publicRows", QVariantList{published, asPublished(publicReminder, 902)});
    dialog->setProperty("localRows", QVariantList{reminder, fresh, unrelated});
    const QVariant chosen = QStringList{reminderId, unrelatedId};
    QVERIFY(QMetaObject::invokeMethod(dialog, "submitRows", Q_ARG(QVariant, chosen)));
    QCOMPARE(rowIds(submission->property("submittable")), QStringList{unrelatedId});
    scene.item(QStringLiteral("dutyCommunityConsent"), true)->setProperty("checked", true);
    QTRY_VERIFY(scene.item(QStringLiteral("dutyCommunitySubmitBrowser"), true)->property("enabled").toBool());
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void DutyCommunitySubmissionTests::communitySubmissionWaitsForCurrentPublicSnapshot()
{
    Scene scene;
    QObject *dialog = openCommunityDialog(scene);
    QVERIFY2(dialog, qPrintable(scene.errors));
    auto *submission = dialog->findChild<QObject *>(QStringLiteral("dutyCommunitySubmission"));
    QVERIFY(submission);
    const auto row = qmlList(dialog->property("localRows")).first().toMap();
    auto existing = row.value(QStringLiteral("content")).toMap();
    const QVariant chosen = QStringList{existing.value(QStringLiteral("id")).toString()};
    existing.insert(QStringLiteral("title"), QStringLiteral("Published title"));
    scene.backend.holdCommunityBrowse = true;
    scene.backend.failCommunityList = true;
    QVERIFY(QMetaObject::invokeMethod(dialog, "reload"));
    QVERIFY(QMetaObject::invokeMethod(dialog, "reload"));
    QCOMPARE(scene.backend.pendingCommunityBrowse.size(), 2);
    QVERIFY(QMetaObject::invokeMethod(dialog, "submitRows", Q_ARG(QVariant, chosen)));
    auto *consent = scene.item(QStringLiteral("dutyCommunityConsent"), true);
    auto *browser = scene.item(QStringLiteral("dutyCommunitySubmitBrowser"), true);
    QVERIFY(consent && browser);
    consent->setProperty("checked", true);
    QTRY_COMPARE(dialog->property("note").toString(), QStringLiteral("Fixture list failure"));
    auto first = QJsonObject{{QStringLiteral("status"), QStringLiteral("ready")},
                             {QStringLiteral("entries"), QJsonArray{}}, {QStringLiteral("online"), true},
                             {QStringLiteral("generation"), 6}, {QStringLiteral("next"), 24}};
    scene.backend.pendingCommunityBrowse.at(1)->succeed(first);
    QTRY_COMPARE(scene.backend.pendingCommunityBrowse.size(), 3);
    first.insert(QStringLiteral("next"), QJsonValue::Null);
    scene.backend.pendingCommunityBrowse.at(0)->fail(QStringLiteral("test_old_browse"),
                                                     QStringLiteral("Superseded browse failure"));
    QVERIFY(dialog->property("refreshing").toBool());
    QVERIFY(!browser->property("enabled").toBool());
    QCOMPARE(dialog->property("note").toString(), QStringLiteral("Fixture list failure"));
    QVERIFY(QMetaObject::invokeMethod(submission, "submit", Q_ARG(QVariant, false)));
    QVERIFY(scene.backend.lastCommunitySubmission.isEmpty());
    const auto published = asPublished(existing, 901, 2).value(QStringLiteral("content")).toMap();
    first.insert(QStringLiteral("entries"), QJsonArray{QJsonObject::fromVariantMap(published)});
    scene.backend.pendingCommunityBrowse.at(2)->succeed(first);
    QTRY_VERIFY(scene.item(QStringLiteral("dutyCommunityCompareRevision"), true));
    QVERIFY(!dialog->property("refreshing").toBool());
    QVERIFY(!consent->property("checked").toBool());
    QVERIFY(!browser->property("enabled").toBool());
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void DutyCommunitySubmissionTests::communitySubmissionRestoresRejectedRevisionText()
{
    Scene scene;
    QObject *dialog = openCommunityDialog(scene);
    QVERIFY2(dialog, qPrintable(scene.errors));
    const auto rows = qmlList(dialog->property("localRows"));
    const auto ids = rowIds(dialog->property("localRows"));
    const auto bodyKey = QStringLiteral("body");
    const auto contentKey = QStringLiteral("content");
    const auto first = rows.at(0).toMap().value(contentKey).toMap();
    const auto second = rows.at(2).toMap().value(contentKey).toMap();
    const auto third = rows.at(4).toMap().value(contentKey).toMap();
    const auto firstPublished = asPublished(first, 901);
    const auto secondPublished = asPublished(second, 902);
    auto editFirst = rows.at(0).toMap();
    auto editSecond = rows.at(2).toMap();
    auto fresh = rows.at(4).toMap();
    auto firstDraft = first;
    auto secondDraft = second;
    auto freshDraft = third;
    const auto freshId = QStringLiteral("00000000-0000-4000-8000-000000000006");
    firstDraft.insert(bodyKey, second.value(bodyKey));
    secondDraft.insert(bodyKey, third.value(bodyKey));
    freshDraft.insert(bodyKey, first.value(bodyKey));
    freshDraft.insert(QStringLiteral("id"), freshId);
    editFirst.insert(contentKey, firstDraft);
    editFirst.insert(QStringLiteral("upstream"), firstPublished.value(contentKey));
    editSecond.insert(contentKey, secondDraft);
    editSecond.insert(QStringLiteral("upstream"), secondPublished.value(contentKey));
    fresh.insert(contentKey, freshDraft);
    dialog->setProperty("publicRows", QVariantList{firstPublished, secondPublished, asPublished(third, 903)});
    dialog->setProperty("localRows", QVariantList{editFirst, editSecond, fresh});
    const QVariant chosen = QStringList{freshId, ids.at(2), ids.at(0)};
    QVERIFY(QMetaObject::invokeMethod(dialog, "submitRows", Q_ARG(QVariant, chosen)));
    auto *submission = dialog->findChild<QObject *>(QStringLiteral("dutyCommunitySubmission"));
    QVERIFY(submission);
    // C rejects the B edit, restoring B's text. Then B rejects the A edit. The new draft
    // is checked only after A's original text has also returned to the public view.
    QCOMPARE(rowIds(submission->property("submittable")), QStringList{});
    const auto twins = qmlMap(submission->property("twins"));
    const auto twinId = [&](const QString &id) {
        return twins.value(id).toMap().value(QStringLiteral("entry")).toMap().value(QStringLiteral("id")).toString();
    };
    QCOMPARE(twinId(ids.at(2)), ids.at(4));
    QCOMPARE(twinId(ids.at(0)), ids.at(2));
    QCOMPARE(twinId(freshId), ids.at(0));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

void DutyCommunitySubmissionTests::communitySubmissionIgnoresLatePreparation()
{
    Scene scene;
    QObject *dialog = openCommunityDialog(scene);
    QVERIFY2(dialog, qPrintable(scene.errors));
    auto *submission = dialog->findChild<QObject *>(QStringLiteral("dutyCommunitySubmission"));
    QVERIFY(submission);
    const auto rows = qmlList(dialog->property("localRows"));
    const auto ids = rowIds(dialog->property("localRows"));
    const QVariant first = QStringList{ids.at(0)};
    const QVariant second = QStringList{ids.at(1)};
    scene.backend.holdCommunitySubmission = true;
    opened.clear();
    QDesktopServices::setUrlHandler(QStringLiteral("https"), this, "rememberUrl");
    const auto urlGuard = qScopeGuard([] { QDesktopServices::unsetUrlHandler(QStringLiteral("https")); });
    const QString clipboard = QStringLiteral("text from another action");
    QGuiApplication::clipboard()->setText(clipboard);

    QVERIFY(QMetaObject::invokeMethod(dialog, "submitRows", Q_ARG(QVariant, first)));
    auto *consent = scene.item(QStringLiteral("dutyCommunityConsent"), true);
    QVERIFY(consent);
    consent->setProperty("checked", true);
    QVERIFY(scene.click(QStringLiteral("dutyCommunitySubmitBrowser")));
    QTRY_COMPARE(scene.backend.pendingCommunitySubmissions.size(), 1);
    QVERIFY(QMetaObject::invokeMethod(dialog, "select", Q_ARG(QVariant, ids.at(1))));
    dialog->setProperty("basket", second);
    QVERIFY(QMetaObject::invokeMethod(dialog, "submitRows", Q_ARG(QVariant, second)));
    consent->setProperty("checked", true);
    QVERIFY(scene.click(QStringLiteral("dutyCommunitySubmitBrowser")));
    QTRY_COMPARE(scene.backend.pendingCommunitySubmissions.size(), 2);

    const QJsonObject prepared{{QStringLiteral("status"), QStringLiteral("prepared")},
                               {QStringLiteral("body"), QStringLiteral("obsolete submission")}};
    scene.backend.pendingCommunitySubmissions.at(0)->succeed(prepared);
    QVERIFY(submission->property("posting").toBool());
    QVERIFY(!submission->property("copied").toBool());
    QCOMPARE(qmlList(dialog->property("basket")), QVariantList{ids.at(1)});
    QCOMPARE(QGuiApplication::clipboard()->text(), clipboard);
    QVERIFY(opened.isEmpty());

    auto published = rows.at(1).toMap().value(QStringLiteral("content")).toMap();
    published.insert(QStringLiteral("title"), QStringLiteral("New public revision"));
    dialog->setProperty("publicRows", QVariantList{asPublished(published, 902, 2)});
    scene.backend.pendingCommunitySubmissions.at(1)->succeed(prepared);
    QTRY_VERIFY(!submission->property("posting").toBool());
    QTRY_VERIFY(scene.item(QStringLiteral("dutyCommunityCompareRevision"), true));
    QVERIFY(!submission->property("copied").toBool());
    QCOMPARE(qmlList(dialog->property("basket")), QVariantList{ids.at(1)});
    QCOMPARE(QGuiApplication::clipboard()->text(), clipboard);
    QVERIFY(opened.isEmpty());
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

MR_DESKTOP_TEST_SUITE(DutyCommunitySubmission)
{
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QApplication app(argc, argv);
    if (!mr::registerBundledFonts()) return 2;
    QApplication::setFont(QFont(QStringLiteral("Noto Sans SC")));
    DutyCommunitySubmissionTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "DutyCommunitySubmissionTests.moc"
