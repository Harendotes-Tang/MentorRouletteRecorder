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
#include <QSignalSpy>
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
    void communitySubmissionShowsEverythingItPublishes();
    void communityLinksAndSourceUpdatesReadPlainly();
    void communityMockAnswersAsTheCollectorDoes();
    void communityRefusedSaveIsOfferedBack();
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
    // Stored codes read as the dialog shows them elsewhere (audit 2026-11-26 RE-6): PLD by its name.
    const auto shown = [dialog](const char *helper, const QString &code) {
        QVariant out;
        QMetaObject::invokeMethod(dialog, helper, Q_RETURN_ARG(QVariant, out), Q_ARG(QVariant, code));
        return out.toString();
    };
    const QString paladin = shown("jobsLabel", QStringLiteral("PLD"));
    QVERIFY2(!paladin.isEmpty() && paladin != QStringLiteral("PLD"), qPrintable(paladin));
    QVERIFY2(text.contains(QString::fromUtf8("职业\n")) && text.contains(paladin), qPrintable(text));
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
    QVERIFY(difference && difference->property("text").toString().contains(shown("licenseLabel", QStringLiteral("CC-BY-SA-4.0"))));
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

// Everything a submission makes public is named before it is sent: 来源 and 标签, which the form
// does not edit, are shown in the local detail and in the submission, and the summary lists every
// field. A 来源 the attribution pushes past 240 characters stops the submission with a reason. A
// build without a GitHub App offers the browser route alone and says nothing of setting one up
// (audit 2026-11-26 R2-2, R2-6, Q-5).
void DutyCommunitySubmissionTests::communitySubmissionShowsEverythingItPublishes()
{
    const auto s = [](const char *text) { return QString::fromUtf8(text); };
    Scene scene;
    QObject *dialog = openCommunityDialog(scene);
    QVERIFY2(dialog, qPrintable(scene.errors));
    QVariantList local = qmlList(dialog->property("localRows"));
    QVariantMap row = local.at(0).toMap();
    QVariantMap content = row.value(QStringLiteral("content")).toMap();
    const QString id = content.value(QStringLiteral("id")).toString();
    content.insert(QStringLiteral("source"), s("群友 张三"));
    content.insert(QStringLiteral("tags"), QVariantList{s("开荒")});
    row.insert(QStringLiteral("content"), content);
    local.replace(0, row);
    dialog->setProperty("localRows", local);
    QVERIFY(QMetaObject::invokeMethod(dialog, "select", Q_ARG(QVariant, id)));
    QTRY_VERIFY(visibleText(scene.mainWindow()->contentItem(), s("群友 张三")));
    QVERIFY(visibleText(scene.mainWindow()->contentItem(), s("开荒")));

    const QVariant chosen = QStringList{id};
    QVERIFY(QMetaObject::invokeMethod(dialog, "submitRows", Q_ARG(QVariant, chosen)));
    QTRY_COMPARE(dialog->property("mode").toString(), QStringLiteral("submit"));
    QQuickItem *pane = scene.item(QStringLiteral("dutyCommunitySubmission"), true);
    QVERIFY(pane);
    QTRY_VERIFY(visibleText(pane, s("来源：群友 张三")));
    QVERIFY(visibleText(pane, s("标签：开荒")));
    QVERIFY(visibleText(pane, s("会公开这 1 项的类型、标题、正文、副本、BOSS / 阶段、职业、游戏版本、区服、语言、标签、来源和关联项，"
                                "以及所选许可。只投稿你有权分享的文字，避免包含个人信息。")));
    // No GitHub App: no sign-in, no native button, no sentence about one not being set up.
    QVERIFY(!dialog->property("configured").toBool());
    QVERIFY(!scene.item(QStringLiteral("dutyCommunitySubmitNative"), true));
    QVERIFY(!visibleText(pane, s("开始设备授权")));
    QVERIFY(!visibleText(pane, s("原生投稿尚未配置 GitHub App，可使用浏览器投稿。")));
    QVERIFY(scene.item(QStringLiteral("dutyCommunitySubmitBrowser"), true));
    // An answer that says so turns the native route off even without the flag beside it.
    dialog->setProperty("configured", true);
    QTRY_VERIFY(scene.item(QStringLiteral("dutyCommunitySubmitNative"), true));
    QObject *submission = dialog->findChild<QObject *>(QStringLiteral("dutyCommunitySubmission"));
    QObject *service = dialog->property("service").value<QObject *>();
    QVERIFY(submission && service);
    QVERIFY(QMetaObject::invokeMethod(submission, "auth", Q_ARG(QVariant, QStringLiteral("sign_in"))));
    const int ticket = dialog->property("serial").toInt();
    const QVariantMap refused{{QStringLiteral("status"), QStringLiteral("app_not_configured")}};
    QVERIFY(QMetaObject::invokeMethod(service, "answered", Q_ARG(int, ticket), Q_ARG(QVariantMap, refused),
                                      Q_ARG(QString, QString())));
    QVERIFY(!dialog->property("configured").toBool());
    QTRY_VERIFY(!scene.item(QStringLiteral("dutyCommunitySubmitNative"), true));

    // A personal version of a public entry: the attribution added to 来源 passes 240 characters.
    content.insert(QStringLiteral("source"), QString(200, QLatin1Char('x')));
    row.insert(QStringLiteral("content"), content);
    row.insert(QStringLiteral("upstream"), publicEntry(905, "macro", "/p upstream", "", 0).value(QStringLiteral("content")));
    local.replace(0, row);
    dialog->setProperty("localRows", local);
    QVERIFY(QMetaObject::invokeMethod(dialog, "submitRows", Q_ARG(QVariant, chosen)));
    QTRY_VERIFY(scene.item(QStringLiteral("dutyCommunityLongSource"), true));
    scene.item(QStringLiteral("dutyCommunityConsent"), true)->setProperty("checked", true);
    QVERIFY(!scene.item(QStringLiteral("dutyCommunitySubmitBrowser"), true)->property("enabled").toBool());
    // The long 来源 alone holds the submission back (audit 2026-11-26 RE-9): nothing to review,
    // no outdated base, consent given.
    QVERIFY(pane->property("hasLongSource").toBool());
    QVERIFY(!pane->property("needsReview").toBool());
    QVERIFY(!pane->property("hasOutdatedBase").toBool());
    QVERIFY(!pane->property("canSubmit").toBool());

    // The local detail shows the whole 来源, wrapped rather than cut at the column's width (RE-8).
    QVERIFY(scene.click(QStringLiteral("dutyCommunitySubmissionCancel")));
    QTRY_COMPARE(dialog->property("mode").toString(), QStringLiteral("view"));
    QVERIFY(QMetaObject::invokeMethod(dialog, "select", Q_ARG(QVariant, id)));
    QQuickItem *longSource = nullptr;
    QTRY_VERIFY((longSource = visibleText(scene.mainWindow()->contentItem(), QString(200, QLatin1Char('x')))) != nullptr);
    QVERIFY(!longSource->property("truncated").toBool());
    QVERIFY(longSource->property("lineCount").toInt() > 1);
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

// 关联项 opens the linked entry even when the directory's range does not hold it, and 来源更新对比
// names what changed in words, content fields and the licence only (audit 2026-11-26 R2-4, R2-8).
void DutyCommunitySubmissionTests::communityLinksAndSourceUpdatesReadPlainly()
{
    const auto s = [](const char *text) { return QString::fromUtf8(text); };
    const auto contentKey = QStringLiteral("content");
    Scene scene;
    QObject *dialog = openCommunityDialog(scene);
    QVERIFY2(dialog, qPrintable(scene.errors));
    QVariantList local = qmlList(dialog->property("localRows"));
    // A starred reminder links 打完道谢, which is not starred.
    QVariantMap reminder = local.at(1).toMap();
    QVariantMap reminderContent = reminder.value(contentKey).toMap();
    const QString reminderId = reminderContent.value(QStringLiteral("id")).toString();
    const QString macroId = local.at(2).toMap().value(contentKey).toMap().value(QStringLiteral("id")).toString();
    reminderContent.insert(QStringLiteral("links"), QVariantList{macroId});
    reminder.insert(contentKey, reminderContent);
    reminder.insert(QStringLiteral("favorite"), true);
    local.replace(1, reminder);
    dialog->setProperty("localRows", local);
    QVERIFY(QMetaObject::invokeMethod(dialog, "pickScope", Q_ARG(QVariant, QStringLiteral("fav"))));
    QVERIFY(QMetaObject::invokeMethod(dialog, "select", Q_ARG(QVariant, reminderId)));
    QTRY_COMPARE(dialog->property("currentId").toString(), reminderId);
    QQuickItem *link = nullptr;
    QTRY_VERIFY((link = visibleText(scene.mainWindow()->contentItem(), s("关联项 · 打完道谢"))) != nullptr);
    // The row of links is laid out a frame after its text appears: click once it has its place.
    QQuickItem *linkRow = link;
    while (linkRow && !linkRow->inherits("QQuickFlow"))
        linkRow = linkRow->parentItem();
    QVERIFY(linkRow);
    QTRY_VERIFY(linkRow->height() >= link->height());
    QTest::mouseClick(scene.mainWindow(), Qt::LeftButton, {},
                      link->mapToScene(QPointF(link->width() / 2, link->height() / 2)).toPoint());
    QTRY_COMPARE(dialog->property("currentId").toString(), macroId);

    // A copy of public entry 910, which has since been revised.
    const QVariantMap upstream = publicEntry(910, "macro", "/p old", "", 0).value(contentKey).toMap();
    QVariantMap newer = upstream;
    newer.insert(QStringLiteral("title"), QStringLiteral("Entry 910 v2"));
    newer.insert(QStringLiteral("tags"), QVariantList{s("开荒")});
    newer.insert(QStringLiteral("region"), QStringLiteral("CN"));
    newer.insert(QStringLiteral("language"), QStringLiteral("en"));
    newer.insert(QStringLiteral("revision"), 2);
    newer.insert(QStringLiteral("updated_at"), QStringLiteral("2026-11-25T10:00:00Z"));
    QVariantMap copy = local.at(4).toMap();
    copy.insert(QStringLiteral("upstream"), upstream);
    local.replace(4, copy);
    dialog->setProperty("localRows", local);
    dialog->setProperty("publicRows", QVariantList{QVariantMap{{contentKey, newer}}});
    QVERIFY(QMetaObject::invokeMethod(dialog, "pickScope", Q_ARG(QVariant, QStringLiteral("all"))));
    QVERIFY(QMetaObject::invokeMethod(dialog, "select",
                                      Q_ARG(QVariant, copy.value(contentKey).toMap().value(QStringLiteral("id")))));
    QTRY_VERIFY(!dialog->property("sourceChanges").toString().isEmpty());
    const QString changes = dialog->property("sourceChanges").toString();
    QVERIFY2(changes.contains(s("标题\n− Entry 910\n+ Entry 910 v2")), qPrintable(changes));
    QVERIFY2(changes.contains(s("标签\n− （空）\n+ 开荒")), qPrintable(changes));
    // Stored codes read as the dialog shows them elsewhere (audit 2026-11-26 RE-6).
    QVERIFY2(changes.contains(s("区服\n− 全部区服\n+ 国服 CN")), qPrintable(changes));
    QVERIFY2(changes.contains(s("语言\n− 简体中文\n+ English")), qPrintable(changes));
    QVERIFY2(!changes.contains(s("− ALL")) && !changes.contains(s("zh-Hans")), qPrintable(changes));
    for (const char *raw : {"updated_at", "revision", "title", "[", "\""})
        QVERIFY2(!changes.contains(QString::fromLatin1(raw)), raw);
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

// The mock answers the local actions as the Collector's CommunityWorkspace does, so the dialog's
// refusal paths can be reached in these suites (audit 2026-11-26 R2-7).
void DutyCommunitySubmissionTests::communityMockAnswersAsTheCollectorDoes()
{
    Scene scene;
    QObject *dialog = openCommunityDialog(scene);
    QVERIFY2(dialog, qPrintable(scene.errors));
    // Under `requestId` when one is given, as the Collector keys a change.
    const auto askAs = [&](QJsonObject payload, const QString &action, const QString &requestId) {
        payload.insert(QStringLiteral("action"), action);
        QVariantMap answer;
        bool done = false;
        const QString type = QStringLiteral("DutyCommunity");
        mr::BackendReply *reply = requestId.isEmpty() ? scene.backend.request(type, payload)
                                                      : scene.backend.requestWithId(type, payload, requestId);
        reply->whenDone(
            dialog, [&](bool, const QVariantMap &result, const QString &, const QString &) { answer = result; done = true; });
        if (!QTest::qWaitFor([&] { return done; }))
            return QVariantMap();
        return answer;
    };
    const auto ask = [&](const QJsonObject &payload, const QString &action) { return askAs(payload, action, QString()); };
    const auto status = [&](const QJsonObject &payload, const char *action) {
        return ask(payload, QString::fromLatin1(action)).value(QStringLiteral("status")).toString();
    };
    const auto rows = [&] { return ask({}, QStringLiteral("list")).value(QStringLiteral("items")).toList(); };
    const auto idKey = QStringLiteral("id");
    const auto contentKey = QStringLiteral("content");
    const auto revisionKey = QStringLiteral("revision");
    QCOMPARE(rows().size(), 5);
    const QVariantMap first = rows().at(0).toMap();
    QJsonObject content = QJsonObject::fromVariantMap(first.value(contentKey).toMap());
    const QString firstId = content.value(idKey).toString();
    const auto save = [&](const QJsonObject &item, int revision) {
        return QJsonObject{{contentKey, item}, {revisionKey, revision}, {QStringLiteral("position"), 0},
                           {QStringLiteral("favorite"), false}};
    };

    // save: a stale revision, a line over 180 UTF-8 bytes, a link to the same type.
    QCOMPARE(status(save(content, 7), "save"), QStringLiteral("local_conflict"));
    QJsonObject longLine = content;
    longLine.insert(QStringLiteral("body"), QStringLiteral("/p ") + QString(70, QChar(0x6C49)));
    QCOMPARE(status(save(longLine, 1), "save"), QStringLiteral("macro_length"));
    const QString otherMacro = rows().at(2).toMap().value(contentKey).toMap().value(idKey).toString();
    QJsonObject linked = content;
    linked.insert(QStringLiteral("links"), QJsonArray{otherMacro});
    QCOMPARE(status(save(linked, 1), "save"), QStringLiteral("invalid_links"));
    // The whole of CommunityContent.Validate, not only the fields the form edits (RE-4).
    const auto with = [&](const char *key, const QJsonValue &value) {
        QJsonObject changed = content;
        changed.insert(QString::fromLatin1(key), value);
        return save(changed, 1);
    };
    QCOMPARE(status(with("author", QStringLiteral("x")), "save"), QStringLiteral("invalid_content"));
    QCOMPARE(status(with("region", QStringLiteral("EU")), "save"), QStringLiteral("invalid_content"));
    QCOMPARE(status(with("content_id", 70000), "save"), QStringLiteral("invalid_content"));
    QCOMPARE(status(with("id", QStringLiteral("not-an-id")), "save"), QStringLiteral("invalid_content"));
    QCOMPARE(status(with("boss", QString(81, QLatin1Char('b'))), "save"), QStringLiteral("invalid_boss"));
    QCOMPARE(status(with("source", QString(241, QLatin1Char('s'))), "save"), QStringLiteral("invalid_source"));
    QCOMPARE(status(with("tags", QJsonArray{QStringLiteral("a"), QStringLiteral("a")}), "save"), QStringLiteral("invalid_tags"));
    QCOMPARE(status(with("links", QJsonArray{firstId}), "save"), QStringLiteral("invalid_links"));
    // A changed field is held to the character rule of audit 2026-11-26 R1-4.
    QCOMPARE(status(with("phase", QStringLiteral("a") + QChar(0x200B)), "save"), QStringLiteral("invalid_phase"));
    QCOMPARE(status(with("title", QStringLiteral("a") + QChar(0x202E)), "save"), QStringLiteral("invalid_title"));
    QJsonObject noRevision = save(content, 1);
    noRevision.remove(revisionKey);
    QCOMPARE(status(noRevision, "save"), QStringLiteral("invalid_revision"));
    QJsonObject farPosition = save(content, 1);
    farPosition.insert(QStringLiteral("position"), 10000);
    QCOMPARE(status(farPosition, "save"), QStringLiteral("invalid_position"));
    QJsonObject noFavorite = save(content, 1);
    noFavorite.remove(QStringLiteral("favorite"));
    QCOMPARE(status(noFavorite, "save"), QStringLiteral("invalid_favorite"));
    QCOMPARE(rows().at(0).toMap().value(revisionKey).toInt(), 1);

    // copy: a chat message is one /p or /a line, and the body is held to the character rule.
    const QJsonObject twoLines{{QStringLiteral("mode"), QStringLiteral("chat")}, {QStringLiteral("body"), QStringLiteral("/p a\n/p b")}};
    QCOMPARE(status(twoLines, "copy"), QStringLiteral("single_chat_required"));
    const QJsonObject reordered{{QStringLiteral("mode"), QStringLiteral("macro")},
                                {QStringLiteral("body"), QStringLiteral("/p a") + QChar(0x202E)}};
    QCOMPARE(status(reordered, "copy"), QStringLiteral("invalid_body"));
    // fork: only an entry taken from the public library, and to a valid new id.
    const QJsonObject fork{{idKey, firstId}, {QStringLiteral("local_id"), QStringLiteral("00000000-0000-4000-8000-000000000077")},
                           {revisionKey, 1}};
    QCOMPARE(status(fork, "fork"), QStringLiteral("local_conflict"));
    QCOMPARE(status({{idKey, firstId}, {revisionKey, 1}}, "fork"), QStringLiteral("invalid_local_id"));
    QCOMPARE(status({{idKey, firstId}, {QStringLiteral("local_id"), QString()}, {revisionKey, 1}}, "fork"),
             QStringLiteral("invalid_id"));

    // install: nothing before the library was read, as the Collector without a snapshot (RE-5).
    const QString published = QStringLiteral("00000000-0000-4000-8000-000000000101");
    QCOMPARE(status({{idKey, published}}, "install"), QStringLiteral("source_unavailable"));
    QCOMPARE(status({{QStringLiteral("enabled"), true}}, "online"), QStringLiteral("not_refreshed"));
    QCOMPARE(status({}, "refresh"), QStringLiteral("ready"));
    // install keeps the content alone; the published fields stay with `upstream`.
    QCOMPARE(status({{idKey, published}}, "install"), QStringLiteral("installed"));
    const QVariantMap installed = rows().at(0).toMap();
    QCOMPARE(installed.value(contentKey).toMap().size(), 15);
    QVERIFY(!installed.value(contentKey).toMap().contains(QStringLiteral("license")));
    QCOMPARE(installed.value(QStringLiteral("upstream")).toMap().value(QStringLiteral("license")).toString(),
             QStringLiteral("CC-BY-4.0"));
    // apply: there is no newer source revision to apply.
    const QJsonObject apply{{idKey, published}, {revisionKey, 1}, {QStringLiteral("source_revision"), 1},
                            {QStringLiteral("mode"), QStringLiteral("keep")}};
    QCOMPARE(status(apply, "apply"), QStringLiteral("source_changed"));

    // delete: a stale revision is refused; deleting drops the links to the entry.
    const QVariantMap reminder = rows().at(2).toMap();
    QJsonObject reminderContent = QJsonObject::fromVariantMap(reminder.value(contentKey).toMap());
    reminderContent.insert(QStringLiteral("links"), QJsonArray{firstId});
    QCOMPARE(status(save(reminderContent, reminder.value(revisionKey).toInt()), "save"), QStringLiteral("saved"));
    QCOMPARE(status({{idKey, firstId}, {revisionKey, 9}}, "delete"), QStringLiteral("local_conflict"));
    QCOMPARE(status({{idKey, firstId}, {revisionKey, 1}}, "delete"), QStringLiteral("deleted"));
    for (const QVariant &row : rows())
        QVERIFY(!row.toMap().value(contentKey).toMap().value(QStringLiteral("links")).toList().contains(firstId));

    // install brings the entry an entry links to, above it, and the copy links to it (RE-5).
    const QString linkedReminder = QStringLiteral("00000000-0000-4000-8000-000000000105");
    const QString linkedMacro = QStringLiteral("00000000-0000-4000-8000-000000000104");
    QCOMPARE(status({{idKey, linkedReminder}}, "install"), QStringLiteral("installed"));
    const QVariantList afterLinked = rows();
    QCOMPARE(afterLinked.at(0).toMap().value(contentKey).toMap().value(idKey).toString(), linkedReminder);
    QCOMPARE(afterLinked.at(1).toMap().value(contentKey).toMap().value(idKey).toString(), linkedMacro);
    QCOMPARE(afterLinked.at(0).toMap().value(contentKey).toMap().value(QStringLiteral("links")).toList(),
             QVariantList{linkedMacro});
    QCOMPARE(status({{idKey, linkedMacro}}, "install"), QStringLiteral("already_installed"));

    // Published before the newer character rule (a title holding U+200D): it installs, and its
    // copy can be edited while that title is left as it is (audit 2026-11-26 RC-1, RC-6).
    QCOMPARE(status({{idKey, QStringLiteral("00000000-0000-4000-8000-000000000106")}}, "install"),
             QStringLiteral("installed"));
    QJsonObject older = QJsonObject::fromVariantMap(rows().at(0).toMap().value(contentKey).toMap());
    QVERIFY(older.value(QStringLiteral("title")).toString().contains(QChar(0x200D)));
    older.insert(QStringLiteral("body"), QStringLiteral("/p edited"));
    QJsonObject retitled = older;
    retitled.insert(QStringLiteral("title"), QStringLiteral("b") + QChar(0x200D));
    QCOMPARE(status(save(retitled, 1), "save"), QStringLiteral("invalid_title"));
    QCOMPARE(status(save(older, 1), "save"), QStringLiteral("saved"));

    // A request_id answers once: its retry replays that answer, and another request under the
    // same id is refused (CommunityRepository.Change).
    const QString requestId = QStringLiteral("community-request-1");
    QJsonObject starred = save(older, 2);
    starred.insert(QStringLiteral("favorite"), true);
    const auto statusAs = [&](const QJsonObject &payload) {
        return askAs(payload, QStringLiteral("save"), requestId).value(QStringLiteral("status")).toString();
    };
    QCOMPARE(statusAs(starred), QStringLiteral("saved"));
    QCOMPARE(statusAs(starred), QStringLiteral("saved"));
    QCOMPARE(rows().at(0).toMap().value(revisionKey).toInt(), 3);
    QJsonObject moved = starred;
    moved.insert(QStringLiteral("position"), 5);
    QCOMPARE(statusAs(moved), QStringLiteral("request_conflict"));
    QVERIFY2(scene.errors.isEmpty(), qPrintable(scene.errors));
}

// A save the Collector refuses after the player opened another form is not dropped: the dialog
// says which entry was not saved and why, and offers it back once that form is left (audit
// 2026-11-26 RE-7). An entry holding a character refused only since then can be edited without
// retyping that field (RC-6), and two refusals say only what happened (RC-4, RC-5).
void DutyCommunitySubmissionTests::communityRefusedSaveIsOfferedBack()
{
    const auto s = [](const char *text) { return QString::fromUtf8(text); };
    const auto contentKey = QStringLiteral("content");
    Scene scene;
    QObject *dialog = openCommunityDialog(scene);
    QVERIFY2(dialog, qPrintable(scene.errors));
    const auto field = [&](const char *name) { return scene.item(QString::fromLatin1(name), true); };
    const auto textOf = [&](const char *name) {
        QQuickItem *shown = field(name);
        return shown ? shown->property("text").toString() : QStringLiteral("<not shown>");
    };
    // Read at another revision than the one the mock holds: it answers local_conflict.
    QVariantMap row = qmlList(dialog->property("localRows")).constFirst().toMap();
    const QString title = qmlMap(row.value(contentKey)).value(QStringLiteral("title")).toString();
    row.insert(QStringLiteral("revision"), row.value(QStringLiteral("revision")).toInt() + 1);
    QVERIFY(QMetaObject::invokeMethod(dialog, "startEdit", Q_ARG(QVariant, row)));
    QTRY_VERIFY(field("dutyCommunityEditorSave"));
    // The mock answers on the next event-loop turn, which the click already spins: hold the
    // answer back so that 新建宏 really comes before the refusal arrives.
    const auto communityType = QStringLiteral("DutyCommunity");
    scene.backend.setReplyDelay(400, communityType);
    QVERIFY(scene.click(QStringLiteral("dutyCommunityEditorSave")));
    QVERIFY(!dialog->property("refusedSave").isValid() || dialog->property("refusedSave").isNull());
    QVERIFY(QMetaObject::invokeMethod(dialog, "editNew", Q_ARG(QVariant, QStringLiteral("macro")),
                                      Q_ARG(QVariant, QVariant())));
    QTRY_VERIFY(field("dutyCommunityRefusedSave"));
    scene.backend.setReplyDelay(0, communityType);
    QCOMPARE(dialog->property("mode").toString(), QStringLiteral("edit"));
    QCOMPARE(textOf("dutyCommunityEditorHeading"), s("新建宏"));
    QCOMPARE(textOf("dutyCommunityRefusedSaveText"),
             s("「") + title + s("」没有保存：本地有同一条目或更新的修改，请重新加载。 保存或取消当前表单后可以恢复。"));
    QQuickItem *restore = field("dutyCommunityRestoreRefused");
    QVERIFY(restore);
    QVERIFY(!restore->property("enabled").toBool());
    // The new form is left: the refused one comes back with its reason.
    dialog->setProperty("mode", QStringLiteral("view"));
    QTRY_VERIFY(restore->property("enabled").toBool());
    QVERIFY(QMetaObject::invokeMethod(dialog, "restoreRefused"));
    QCOMPARE(dialog->property("mode").toString(), QStringLiteral("edit"));
    QTRY_COMPARE(textOf("dutyCommunityEditorRefusal"), s("本地有同一条目或更新的修改，请重新加载。"));
    QCOMPARE(textOf("dutyCommunityEditorTitle"), title);
    QVERIFY(!field("dutyCommunityRefusedSave"));

    // Saved by an older version with U+200B in the title: the body changes, the title stays.
    QVariantMap older = qmlList(dialog->property("localRows")).at(1).toMap();
    QVariantMap olderContent = qmlMap(older.value(contentKey));
    olderContent.insert(QStringLiteral("title"), QStringLiteral("a") + QChar(0x200B) + QStringLiteral("b"));
    older.insert(contentKey, olderContent);
    QVERIFY(QMetaObject::invokeMethod(dialog, "startEdit", Q_ARG(QVariant, older)));
    QQuickItem *editor = field("dutyCommunityEditor");
    QQuickItem *body = field("dutyCommunityEditorBody");
    QVERIFY(editor && body);
    QSignalSpy saved(editor, SIGNAL(saved(QVariant)));
    body->setProperty("text", QStringLiteral("edited body"));
    QVERIFY(scene.click(QStringLiteral("dutyCommunityEditorSave")));
    QCOMPARE(saved.count(), 1);
    QVERIFY(!field("dutyCommunityEditorTitleProblem"));

    QVariant sentence;
    QVERIFY(QMetaObject::invokeMethod(dialog, "statusText", Q_RETURN_ARG(QVariant, sentence),
                                      Q_ARG(QVariant, QStringLiteral("source_unavailable"))));
    QCOMPARE(sentence.toString(), s("这条内容已从公共共享库撤回或不再提供，请刷新公共共享库后再试。"));
    QVERIFY(QMetaObject::invokeMethod(dialog, "statusText", Q_RETURN_ARG(QVariant, sentence),
                                      Q_ARG(QVariant, QStringLiteral("submit_not_sent"))));
    QCOMPARE(sentence.toString(), s("投稿没有发出，什么都没有提交，请稍后再试。"));
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
