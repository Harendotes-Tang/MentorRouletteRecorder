#include "TestCollectorGuard.h"
#include "AppSettings.h"
#include "IBackend.h"
#include "RecoveryController.h"

#include <QCoreApplication>
#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QJsonArray>
#include <QLockFile>
#include <QSignalSpy>
#include <QStandardPaths>
#include <QTemporaryDir>
#include <QTest>
#include <QTimer>
#include <utility>

namespace {
QVariantMap run(const QString &id, bool pending = false)
{
    return {{QStringLiteral("run_id"), id}, {QStringLiteral("created_at_utc"), QStringLiteral("2026-10-20T01:00:00Z")},
            {QStringLiteral("matched_at_utc"), QStringLiteral("2026-10-20T01:01:00Z")},
            {QStringLiteral("duty_name"), QStringLiteral("test duty")}, {QStringLiteral("job_id"), 19},
            {QStringLiteral("source"), QStringLiteral("AUTO_NETWORK")}, {QStringLiteral("pending_review"), pending},
            {QStringLiteral("mentor_roulette_id"), 9},
            {QStringLiteral("result"), pending ? QStringLiteral("UNKNOWN") : QStringLiteral("COMPLETED")}};
}
QVariantMap fields(const QString &text)
{
    return {{QStringLiteral("text"), text}, {QStringLiteral("mood"), QStringLiteral("good")}, {QStringLiteral("jobId"), 19}};
}
QString testRoot()
{
    const QString root = QDir(QString::fromUtf8(MR_SOURCE_DIR))
        .absoluteFilePath(QStringLiteral("artifacts/recoverable-prompts-20261020"));
    QDir().mkpath(root);
    return root + QStringLiteral("/core-XXXXXX");
}
QString journal(const QString &root)
{
    QDir folder(root + QStringLiteral("/recovery"));
    const auto files = folder.entryList({QStringLiteral("*.json")}, QDir::Files);
    return files.isEmpty() ? QString() : folder.absoluteFilePath(files.first());
}
class Backend final : public mr::IBackend
{
public:
    struct Waiting { mr::BackendReply *reply; QJsonObject payload; };
    QVariantList rows;
    QList<Waiting> waiting;
    bool hold = false;
    bool failNext = false;
    /// Answer soft-deleted rows too, as a run_id lookup may.
    bool listDeleted = false;
    int mutations = 0;
    int queries = 0;
    QString backendName() const override { return QStringLiteral("recovery-test"); }
    bool isConnected() const override { return true; }
    mr::BackendReply *request(const QString &type, const QJsonObject &payload) override
    {
        auto *reply = new mr::BackendReply(QString::number(++serial), type, this);
        if (type != QLatin1String("QueryRuns"))
            ++mutations;
        ++queries;
        if (hold) {
            waiting.append({reply, payload});
        } else {
            const bool fail = std::exchange(failNext, false);
            const QJsonObject answer = result(payload);
            QTimer::singleShot(0, reply, [reply, answer, fail] {
                if (fail) reply->fail(QStringLiteral("ERR_INTERNAL"), QStringLiteral("test read failure"));
                else reply->succeed(answer);
            });
        }
        return reply;
    }
    QJsonObject result(const QJsonObject &payload) const
    {
        const auto filter = payload.value(QStringLiteral("filter")).toObject();
        const auto ids = filter.value(QStringLiteral("run_id")).toArray();
        const auto sources = filter.value(QStringLiteral("source")).toArray();
        QVariantList selected;
        for (const QVariant &value : rows) {
            const auto row = value.toMap();
            if (!ids.isEmpty() && !ids.contains(row.value(QStringLiteral("run_id")).toString())) continue;
            if (!sources.isEmpty() && !sources.contains(row.value(QStringLiteral("source")).toString())) continue;
            if (filter.value(QStringLiteral("pending_review")).toBool() && !row.value(QStringLiteral("pending_review")).toBool()) continue;
            if (row.value(QStringLiteral("soft_deleted")).toBool() && !listDeleted) continue;
            selected.append(row);
        }
        const int page = payload.value(QStringLiteral("page")).toInt(1);
        const int size = payload.value(QStringLiteral("page_size")).toInt(200);
        QJsonArray answer;
        for (int i = (page - 1) * size; i < qMin(page * size, int(selected.size())); ++i)
            answer.append(QJsonObject::fromVariantMap(selected.at(i).toMap()));
        return {{QStringLiteral("items"), answer}, {QStringLiteral("page_info"),
            QJsonObject{{QStringLiteral("page"), page}, {QStringLiteral("page_size"), size}, {QStringLiteral("total"), selected.size()}}}};
    }
    void finish(int index, const QJsonArray &items) const
    {
        waiting.at(index).reply->succeed({{QStringLiteral("items"), items},
            {QStringLiteral("page_info"), QJsonObject{{QStringLiteral("total"), items.size()}}}});
    }
private:
    int serial = 0;
};
} // namespace

class RecoveryTests : public QObject
{
    Q_OBJECT
private Q_SLOTS:
    void presentationIsValidatedAndUsesInstanceIni()
    {
        QTemporaryDir dir(testRoot()); QVERIFY(dir.isValid());
        const QString path = dir.filePath(QStringLiteral("desktop.ini"));
        mr::AppSettings settings(path, nullptr);
        QCOMPARE(settings.instanceFilePath(), QFileInfo(path).absoluteFilePath());
        QCOMPARE(settings.promptPresentation(), QStringLiteral("window"));
        QSignalSpy changed(&settings, &mr::AppSettings::generalChanged);
        settings.setPromptPresentation(QStringLiteral("floating"));
        settings.setPromptPresentation(QStringLiteral("invalid"));
        QCOMPARE(settings.promptPresentation(), QStringLiteral("floating"));
        QCOMPARE(changed.size(), 1);
    }
    void provisionalWorkAndDraftSurviveRestart()
    {
        QTemporaryDir dir(testRoot()); QVERIFY(dir.isValid());
        mr::AppSettings settings(dir.filePath(QStringLiteral("desktop.ini")), nullptr);
        Backend backend;
        const auto data = run(QStringLiteral("draft-run"));
        {
            mr::RecoveryController first(&backend, &settings);
            first.offer(data, QStringLiteral("reflection"));
            QVERIFY(first.saveDraft(data, fields(QStringLiteral("unfinished note"))));
            QVERIFY(journal(dir.path()).isEmpty());
            first.setDatabasePath(dir.filePath(QStringLiteral("records.db")));
            QTRY_VERIFY(!first.busy());
            QCOMPARE(first.count(), 1);
        }
        mr::RecoveryController reopened(&backend, &settings);
        reopened.setDatabasePath(dir.filePath(QStringLiteral("records.db")));
        QTRY_VERIFY(!reopened.busy());
        QCOMPARE(reopened.count(), 1);
        const auto draft = reopened.draft(data);
        QCOMPARE(draft.value(QStringLiteral("text")).toString(), QStringLiteral("unfinished note"));
        QVERIFY(!draft.value(QStringLiteral("conflict")).toBool());
        QCOMPARE(backend.mutations, 0);
    }
    void databaseIsolationAndNullSettingsMemoryOnly()
    {
        QTemporaryDir dir(testRoot()); QVERIFY(dir.isValid());
        mr::AppSettings settings(dir.filePath(QStringLiteral("desktop.ini")), nullptr);
        Backend backend;
        mr::RecoveryController controller(&backend, &settings);
        controller.setDatabasePath(dir.filePath(QStringLiteral("first.db")));
        QTRY_VERIFY(!controller.busy());
        const auto data = run(QStringLiteral("same-id"));
        QVERIFY(controller.saveDraft(data, fields(QStringLiteral("first database"))));
        controller.setDatabasePath(dir.filePath(QStringLiteral("second.db")));
        QTRY_VERIFY(!controller.busy());
        QVERIFY(controller.draft(data).isEmpty());
        QCOMPARE(controller.count(), 0);
        controller.setDatabasePath(dir.filePath(QStringLiteral("first.db")));
        QTRY_VERIFY(!controller.busy());
        QCOMPARE(controller.draft(data).value(QStringLiteral("text")).toString(), QStringLiteral("first database"));
        mr::RecoveryController memory(&backend, nullptr);
        memory.setDatabasePath(dir.filePath(QStringLiteral("first.db")));
        QTRY_VERIFY(!memory.busy());
        QVERIFY(memory.draft(data).isEmpty());
        memory.saveDraft(data, fields(QStringLiteral("not persisted")));
        QCOMPARE(controller.draft(data).value(QStringLiteral("text")).toString(), QStringLiteral("first database"));
    }
    void concurrentWritersMergeAndOldSaveCannotDeleteNewDraft()
    {
        QTemporaryDir dir(testRoot()); QVERIFY(dir.isValid());
        mr::AppSettings settings(dir.filePath(QStringLiteral("desktop.ini")), nullptr);
        Backend backend;
        mr::RecoveryController a(&backend, &settings);
        mr::RecoveryController b(&backend, &settings);
        const QString db = dir.filePath(QStringLiteral("records.db"));
        a.setDatabasePath(db); b.setDatabasePath(db);
        QTRY_VERIFY(!a.busy() && !b.busy());
        const auto one = run(QStringLiteral("one"));
        const auto two = run(QStringLiteral("two"));
        a.offer(one, QStringLiteral("reflection"));
        b.offer(two, QStringLiteral("reflection"));
        a.saveDraft(one, fields(QStringLiteral("submitted note")));
        const QString oldToken = a.draft(one).value(QStringLiteral("token")).toString();
        b.refresh(); QTRY_VERIFY(!b.busy());
        b.saveDraft(one, fields(QStringLiteral("newer unsent note")));
        a.markReflectionSaved(QStringLiteral("one"), oldToken);
        QCOMPARE(a.draft(one).value(QStringLiteral("text")).toString(), QStringLiteral("newer unsent note"));
        QCOMPARE(a.count(), 2);
        mr::RecoveryController c(&backend, &settings); c.setDatabasePath(db);
        QTRY_VERIFY(!c.busy()); QCOMPARE(c.count(), 2);
        QCOMPARE(c.draft(one).value(QStringLiteral("text")).toString(), QStringLiteral("newer unsent note"));
    }
    void sameInputKeepsTokenAndPartialCompletionKeepsOtherWork()
    {
        Backend backend;
        const auto data = run(QStringLiteral("pending"), true);
        backend.rows = {data};
        mr::RecoveryController controller(&backend, nullptr);
        controller.offer(data, QStringLiteral("result"));
        controller.offer(data, QStringLiteral("reflection"));
        controller.saveDraft(data, fields(QStringLiteral("first")));
        const QString token = controller.draft(data).value(QStringLiteral("token")).toString();
        controller.saveDraft(data, fields(QStringLiteral("first")));
        QCOMPARE(controller.draft(data).value(QStringLiteral("token")).toString(), token);
        controller.refresh(); QTRY_VERIFY(!controller.busy());
        controller.markReflectionSaved(QStringLiteral("pending"), token);
        QVERIFY(controller.draft(data).isEmpty());
        QCOMPARE(controller.count(), 1);
        controller.markResultSaved(QStringLiteral("pending"));
        QCOMPARE(controller.count(), 1); // Cached pending is not optimistically dismissed.
        backend.rows.clear(); controller.refresh(); QTRY_VERIFY(!controller.busy());
        QCOMPARE(controller.count(), 0);
    }
    void corruptJournalRetainsInputAndOriginalBytes()
    {
        QTemporaryDir dir(testRoot()); QVERIFY(dir.isValid());
        mr::AppSettings settings(dir.filePath(QStringLiteral("desktop.ini")), nullptr);
        Backend backend;
        mr::RecoveryController controller(&backend, &settings);
        controller.setDatabasePath(dir.filePath(QStringLiteral("records.db")));
        QTRY_VERIFY(!controller.busy());
        const auto data = run(QStringLiteral("corrupt"));
        controller.offer(data, QStringLiteral("reflection"));
        const QString path = journal(dir.path()); QVERIFY(!path.isEmpty());
        QFile file(path); QVERIFY(file.open(QIODevice::WriteOnly));
        QCOMPARE(file.write("{broken"), qint64(7)); file.close();
        QVERIFY(!controller.saveDraft(data, fields(QStringLiteral("keep this input"))));
        QCOMPARE(controller.draft(data).value(QStringLiteral("text")).toString(), QStringLiteral("keep this input"));
        QVERIFY(!controller.error().isEmpty());
        controller.refresh(); QTRY_VERIFY(!controller.busy());
        QVERIFY(!controller.error().isEmpty());
        QVERIFY(file.open(QIODevice::ReadOnly)); QCOMPARE(file.readAll(), QByteArray("{broken"));
    }
    void lockedWriterRetriesWithoutLosingInput()
    {
        QTemporaryDir dir(testRoot()); QVERIFY(dir.isValid());
        mr::AppSettings settings(dir.filePath(QStringLiteral("desktop.ini")), nullptr);
        Backend backend;
        mr::RecoveryController controller(&backend, &settings);
        const QString db = dir.filePath(QStringLiteral("records.db"));
        controller.setDatabasePath(db); QTRY_VERIFY(!controller.busy());
        const auto data = run(QStringLiteral("locked")); controller.offer(data, QStringLiteral("reflection"));
        QLockFile lock(journal(dir.path()) + QStringLiteral(".lock")); QVERIFY(lock.tryLock(0));
        QVERIFY(!controller.saveDraft(data, fields(QStringLiteral("retry me"))));
        QCOMPARE(controller.draft(data).value(QStringLiteral("text")).toString(), QStringLiteral("retry me"));
        lock.unlock(); controller.refresh(); QTRY_VERIFY(!controller.busy());
        QVERIFY(controller.error().isEmpty());
        mr::RecoveryController reopened(&backend, &settings); reopened.setDatabasePath(db);
        QTRY_VERIFY(!reopened.busy());
        QCOMPARE(reopened.draft(data).value(QStringLiteral("text")).toString(), QStringLiteral("retry me"));
    }
    void failedWriteSurvivesDatabaseRoundTripInMemory()
    {
        QTemporaryDir dir(testRoot()); QVERIFY(dir.isValid());
        mr::AppSettings settings(dir.filePath(QStringLiteral("desktop.ini")), nullptr);
        Backend backend;
        mr::RecoveryController controller(&backend, &settings);
        const QString db = dir.filePath(QStringLiteral("first.db"));
        controller.setDatabasePath(db); QTRY_VERIFY(!controller.busy());
        const auto data = run(QStringLiteral("keep-on-switch"));
        controller.offer(data, QStringLiteral("reflection"));
        QLockFile lock(journal(dir.path()) + QStringLiteral(".lock")); QVERIFY(lock.tryLock(0));
        QVERIFY(!controller.saveDraft(data, fields(QStringLiteral("not yet on disk"))));
        controller.setDatabasePath(dir.filePath(QStringLiteral("second.db"))); QTRY_VERIFY(!controller.busy());
        QVERIFY(controller.draft(data).isEmpty());
        controller.setDatabasePath(db); QTRY_VERIFY(!controller.busy());
        QCOMPARE(controller.draft(data).value(QStringLiteral("text")).toString(), QStringLiteral("not yet on disk"));
        lock.unlock(); controller.refresh(); QTRY_VERIFY(!controller.busy());
        QVERIFY(controller.error().isEmpty());
    }
    void scopeChangingFlushesEditorIntoOldDatabase()
    {
        QTemporaryDir dir(testRoot()); QVERIFY(dir.isValid());
        mr::AppSettings settings(dir.filePath(QStringLiteral("desktop.ini")), nullptr);
        Backend backend;
        mr::RecoveryController controller(&backend, &settings);
        const QString first = dir.filePath(QStringLiteral("first.db"));
        controller.setDatabasePath(first); QTRY_VERIFY(!controller.busy());
        const QString oldKey = controller.databaseKey();
        const auto data = run(QStringLiteral("open-editor"));
        QSignalSpy changed(&controller, &mr::RecoveryController::scopeChanged);
        QMetaObject::Connection flush = connect(&controller, &mr::RecoveryController::scopeChanging,
            &controller, [&] {
                QCOMPARE(controller.databaseKey(), oldKey);
                QVERIFY(controller.saveDraft(data, fields(QStringLiteral("last old-scope keystroke"))));
            });
        controller.setDatabasePath(dir.filePath(QStringLiteral("second.db"))); QTRY_VERIFY(!controller.busy());
        disconnect(flush);
        QCOMPARE(changed.size(), 1);
        QVERIFY(controller.databaseKey() != oldKey);
        QVERIFY(controller.draft(data).isEmpty());
        controller.setDatabasePath(first); QTRY_VERIFY(!controller.busy());
        QCOMPARE(controller.draft(data).value(QStringLiteral("text")).toString(), QStringLiteral("last old-scope keystroke"));
    }
    void quitFlushesLastInputAndRetriesInactiveFailedJournal()
    {
        QTemporaryDir dir(testRoot()); QVERIFY(dir.isValid());
        mr::AppSettings settings(dir.filePath(QStringLiteral("desktop.ini")), nullptr);
        Backend backend;
        mr::RecoveryController controller(&backend, &settings);
        const QString first = dir.filePath(QStringLiteral("first.db"));
        controller.setDatabasePath(first); QTRY_VERIFY(!controller.busy());
        const auto data = run(QStringLiteral("inactive-dirty"));
        controller.offer(data, QStringLiteral("reflection"));
        QLockFile lock(journal(dir.path()) + QStringLiteral(".lock")); QVERIFY(lock.tryLock(0));
        QVERIFY(!controller.saveDraft(data, fields(QStringLiteral("retry inactive on quit"))));
        controller.setDatabasePath(dir.filePath(QStringLiteral("second.db"))); QTRY_VERIFY(!controller.busy());
        lock.unlock();
        const auto current = run(QStringLiteral("last-keystroke"));
        connect(&controller, &mr::RecoveryController::flushRequested, &controller, [&] {
            QVERIFY(controller.saveDraft(current, fields(QStringLiteral("captured at quit"))));
        });
        QVERIFY(QMetaObject::invokeMethod(QCoreApplication::instance(), "aboutToQuit", Qt::DirectConnection));
        QCOMPARE(controller.draft(current).value(QStringLiteral("text")).toString(), QStringLiteral("captured at quit"));
        mr::RecoveryController reopened(&backend, &settings); reopened.setDatabasePath(first);
        QTRY_VERIFY(!reopened.busy());
        QCOMPARE(reopened.draft(data).value(QStringLiteral("text")).toString(), QStringLiteral("retry inactive on quit"));
    }
    void paginatesPendingAndFailedReadRetainsSnapshot()
    {
        Backend backend;
        for (int i = 0; i < 205; ++i) backend.rows.append(run(QString::number(i), true));
        auto imported = run(QStringLiteral("imported"), true); imported.insert(QStringLiteral("source"), QStringLiteral("IMPORT"));
        backend.rows.append(imported);
        backend.rows.append(run(QStringLiteral("old completed without note")));
        mr::RecoveryController controller(&backend, nullptr);
        controller.refresh(); QTRY_VERIFY(!controller.busy());
        QCOMPARE(controller.count(), 205); QCOMPARE(backend.queries, 2);
        backend.failNext = true; backend.rows.clear(); controller.refresh(); QTRY_VERIFY(!controller.busy());
        QCOMPARE(controller.count(), 205); QVERIFY(!controller.error().isEmpty());
        controller.refresh(); QTRY_VERIFY(!controller.busy()); QCOMPARE(controller.count(), 0);
    }
    void pendingProjectionUsesStatisticalEligibilityAndRawPagination()
    {
        Backend backend;
        for (int i = 0; i < 200; ++i) {
            auto unconfirmed = run(QStringLiteral("unconfirmed-") + QString::number(i), true);
            unconfirmed.insert(QStringLiteral("mentor_roulette_id"), QVariant());
            backend.rows.append(unconfirmed);
        }
        auto manual = run(QStringLiteral("manual"), true);
        manual.insert(QStringLiteral("source"), QStringLiteral("MANUAL"));
        manual.insert(QStringLiteral("mentor_roulette_id"), QVariant());
        backend.rows.append(manual);
        backend.rows.append(run(QStringLiteral("mentor"), true));
        mr::RecoveryController controller(&backend, nullptr);
        controller.refresh(); QTRY_VERIFY(!controller.busy());
        QCOMPARE(backend.queries, 2);
        QCOMPARE(controller.count(), 2);
        auto invalidOffer = run(QStringLiteral("not-mentor"));
        invalidOffer.insert(QStringLiteral("mentor_roulette_id"), QVariant());
        controller.offer(invalidOffer, QStringLiteral("reflection"));
        QCOMPARE(controller.count(), 2);
    }
    void staleQueriesCannotCrossDatabase()
    {
        Backend backend; backend.hold = true;
        mr::RecoveryController controller(&backend, nullptr);
        controller.setDatabasePath(QStringLiteral("first.db"));
        controller.setDatabasePath(QStringLiteral("second.db"));
        QCOMPARE(backend.waiting.size(), 2);
        backend.finish(0, {QJsonObject::fromVariantMap(run(QStringLiteral("first"), true))});
        QVERIFY(controller.busy()); QCOMPARE(controller.count(), 0);
        backend.finish(1, {QJsonObject::fromVariantMap(run(QStringLiteral("second"), true))});
        QVERIFY(!controller.busy()); QCOMPARE(controller.count(), 1);
        QCOMPARE(controller.items().first().toMap().value(QStringLiteral("runId")).toString(), QStringLiteral("second"));
    }
    void freshOpenDetectsChangedNoteAndMissingRecord()
    {
        Backend backend;
        auto data = run(QStringLiteral("conflict"));
        data.insert(QStringLiteral("reflection"), QVariantMap{{QStringLiteral("text"), QStringLiteral("old committed")}});
        backend.rows = {data};
        mr::RecoveryController controller(&backend, nullptr);
        controller.saveDraft(data, fields(QStringLiteral("local unsent")));
        auto latest = data;
        latest.insert(QStringLiteral("reflection"), QVariantMap{{QStringLiteral("text"), QStringLiteral("new external note")}});
        backend.rows = {latest};
        QSignalSpy opened(&controller, &mr::RecoveryController::openRequested);
        controller.requestOpen(QStringLiteral("conflict")); QTRY_COMPARE(opened.size(), 1);
        auto selected = opened.takeFirst().at(0).toMap();
        QVERIFY(selected.value(QStringLiteral("recoveryConflict")).toBool());
        QCOMPARE(selected.value(QStringLiteral("recoveryDraft")).toMap().value(QStringLiteral("text")).toString(), QStringLiteral("local unsent"));
        QCOMPARE(selected.value(QStringLiteral("reflection")).toMap().value(QStringLiteral("text")).toString(), QStringLiteral("new external note"));
        backend.rows.clear(); controller.requestOpen(QStringLiteral("conflict")); QTRY_COMPARE(opened.size(), 1);
        QVERIFY(opened.first().at(0).toMap().value(QStringLiteral("recoveryUnavailable")).toBool());
        QCOMPARE(controller.count(), 1); QCOMPARE(backend.mutations, 0);
    }
    void aVerifiedMissingRecordEndsItsRemindersButKeepsAnExplicitDraft_data()
    {
        QTest::addColumn<bool>("softDeleted");
        QTest::newRow("purged") << false;
        QTest::newRow("soft-deleted") << true;
    }
    void aVerifiedMissingRecordEndsItsRemindersButKeepsAnExplicitDraft()
    {
        // Review I2-1 / P-2: a reminder for a record deleted or purged later could
        // never be cleared. A verified read ends the reminders; a draft the user
        // typed stays until they remove it.
        QFETCH(bool, softDeleted);
        Backend backend;
        backend.listDeleted = true;
        const auto noteOnly = run(QStringLiteral("note-only"));
        const auto withDraft = run(QStringLiteral("with-draft"));
        const auto review = run(QStringLiteral("review"), true);
        backend.rows = {noteOnly, withDraft, review};
        mr::RecoveryController controller(&backend, nullptr);
        controller.offer(noteOnly, QStringLiteral("reflection"));
        controller.offer(withDraft, QStringLiteral("reflection"));
        controller.offer(review, QStringLiteral("result"));
        QVERIFY(controller.saveDraft(withDraft, fields(QStringLiteral("keep me"))));
        controller.refresh(); QTRY_VERIFY(!controller.busy());
        QCOMPARE(controller.count(), 3);
        if (softDeleted) {
            QVariantList deleted;
            for (const QVariant &row : std::as_const(backend.rows)) {
                auto copy = row.toMap();
                copy.insert(QStringLiteral("soft_deleted"), true);
                deleted.append(copy);
            }
            backend.rows = deleted;
        } else {
            backend.rows.clear();
        }
        QSignalSpy opened(&controller, &mr::RecoveryController::openRequested);
        controller.requestOpen(QStringLiteral("note-only")); QTRY_COMPARE(opened.size(), 1);
        QVERIFY(opened.last().at(0).toMap().value(QStringLiteral("recoveryUnavailable")).toBool());
        QCOMPARE(controller.count(), 2);
        controller.requestOpen(QStringLiteral("review")); QTRY_COMPARE(opened.size(), 2);
        QCOMPARE(controller.count(), 1);
        controller.requestOpen(QStringLiteral("with-draft")); QTRY_COMPARE(opened.size(), 3);
        QCOMPARE(controller.count(), 1);
        const auto item = controller.items().first().toMap();
        QCOMPARE(item.value(QStringLiteral("runId")).toString(), QStringLiteral("with-draft"));
        QVERIFY(!item.value(QStringLiteral("needsReflection")).toBool());
        QVERIFY(item.value(QStringLiteral("hasDraft")).toBool());
        QVERIFY(item.value(QStringLiteral("recoveryUnavailable")).toBool());
        QCOMPARE(controller.draft(withDraft).value(QStringLiteral("text")).toString(), QStringLiteral("keep me"));
        controller.dismiss(QStringLiteral("with-draft"));
        QCOMPARE(controller.count(), 0);
        backend.listDeleted = false; // The pending list never holds deleted records.
        controller.refresh(); QTRY_VERIFY(!controller.busy());
        QCOMPARE(controller.count(), 0);
        QCOMPARE(backend.mutations, 0);
    }
    void aFailedReadEndsNoReminderAndOnlyUnavailableItemsCanBeRemoved()
    {
        Backend backend;
        const auto data = run(QStringLiteral("still-there"));
        backend.rows = {data};
        mr::RecoveryController controller(&backend, nullptr);
        controller.offer(data, QStringLiteral("reflection"));
        controller.dismiss(QStringLiteral("still-there"));
        QCOMPARE(controller.count(), 1); // Not unavailable: nothing to remove by hand.
        QSignalSpy opened(&controller, &mr::RecoveryController::openRequested);
        backend.failNext = true;
        controller.requestOpen(QStringLiteral("still-there")); QTRY_COMPARE(opened.size(), 1);
        QVERIFY(opened.first().at(0).toMap().value(QStringLiteral("recoveryUnavailable")).toBool());
        QCOMPARE(controller.count(), 1);
        QVERIFY(controller.items().first().toMap().value(QStringLiteral("needsReflection")).toBool());
        // An item that could not be read may still be put away explicitly.
        controller.dismiss(QStringLiteral("still-there"));
        QCOMPARE(controller.count(), 0);
    }
    void aSavedNoteIsNotAConflictWithTheCollectorsCopyOfIt()
    {
        // Review L2-7: after a save the editor holds {text, mood}; the Collector's
        // RunReflection also carries its timestamps. Reopening the draft from
        // 待处理 must not report 「已保存的笔记有变化」 for the very same note.
        Backend backend;
        auto local = run(QStringLiteral("saved-note"));
        local.insert(QStringLiteral("reflection"), QVariantMap{{QStringLiteral("text"), QStringLiteral("saved")},
                                                               {QStringLiteral("mood"), QStringLiteral("good")}});
        mr::RecoveryController controller(&backend, nullptr);
        QVERIFY(controller.saveDraft(local, fields(QStringLiteral("more after saving"))));
        auto stored = local;
        stored.insert(QStringLiteral("reflection"), QVariantMap{{QStringLiteral("text"), QStringLiteral("saved")},
            {QStringLiteral("mood"), QStringLiteral("good")},
            {QStringLiteral("created_at_utc"), QStringLiteral("2026-10-20T01:30:00.000Z")},
            {QStringLiteral("updated_at_utc"), QStringLiteral("2026-10-20T01:31:00.000Z")}});
        QVERIFY(!controller.draft(stored).value(QStringLiteral("conflict")).toBool());
        auto edited = stored;
        edited.insert(QStringLiteral("reflection"), QVariantMap{{QStringLiteral("text"), QStringLiteral("changed elsewhere")},
            {QStringLiteral("mood"), QStringLiteral("good")},
            {QStringLiteral("created_at_utc"), QStringLiteral("2026-10-20T01:30:00.000Z")},
            {QStringLiteral("updated_at_utc"), QStringLiteral("2026-10-20T01:40:00.000Z")}});
        QVERIFY(controller.draft(edited).value(QStringLiteral("conflict")).toBool());
        auto remooded = stored;
        remooded.insert(QStringLiteral("reflection"), QVariantMap{{QStringLiteral("text"), QStringLiteral("saved")},
            {QStringLiteral("mood"), QStringLiteral("bad")}});
        QVERIFY(controller.draft(remooded).value(QStringLiteral("conflict")).toBool());
    }
    void freshOpenUsesCanonicalUnknownResultAndPreservesCompletedReview()
    {
        Backend backend;
        const auto unknown = run(QStringLiteral("unknown"), true);
        auto completed = run(QStringLiteral("completed-review"), true);
        completed.insert(QStringLiteral("result"), QStringLiteral("COMPLETED"));
        backend.rows = {unknown, completed};
        mr::RecoveryController controller(&backend, nullptr);
        QSignalSpy opened(&controller, &mr::RecoveryController::openRequested);
        controller.requestOpen(QStringLiteral("unknown")); QTRY_COMPARE(opened.size(), 1);
        QCOMPARE(opened.first().at(1).toString(), QStringLiteral("result"));
        controller.requestOpen(QStringLiteral("completed-review")); QTRY_COMPARE(opened.size(), 2);
        QCOMPARE(opened.last().at(1).toString(), QStringLiteral("reflection"));
        controller.refresh(); QTRY_VERIFY(!controller.busy());
        QCOMPARE(controller.count(), 2);
    }
    void refreshDoesNotInvalidateManualOpenButIdentityChangeDoes()
    {
        Backend backend; backend.hold = true;
        mr::RecoveryController controller(&backend, nullptr);
        const auto data = run(QStringLiteral("identity")); controller.saveDraft(data, fields(QStringLiteral("local draft")));
        QSignalSpy opened(&controller, &mr::RecoveryController::openRequested);
        controller.requestOpen(QStringLiteral("identity"));
        controller.refresh();
        backend.finish(0, {QJsonObject::fromVariantMap(data)});
        QCOMPARE(opened.size(), 1);
        backend.finish(1, {});
        auto replacement = data; replacement.insert(QStringLiteral("created_at_utc"), QStringLiteral("2026-10-20T02:00:00Z"));
        controller.requestOpen(QStringLiteral("identity"));
        backend.finish(2, {QJsonObject::fromVariantMap(replacement)});
        QCOMPARE(opened.size(), 2);
        QVERIFY(opened.last().at(0).toMap().value(QStringLiteral("recoveryUnavailable")).toBool());
        QVERIFY(!controller.saveDraft(replacement, fields(QStringLiteral("must not attach"))));
        QCOMPARE(controller.draft(data).value(QStringLiteral("text")).toString(), QStringLiteral("local draft"));
    }
    void reconnectRequiresFreshDatabaseIdentityBeforeOpening()
    {
        QTemporaryDir dir(testRoot()); QVERIFY(dir.isValid());
        mr::AppSettings settings(dir.filePath(QStringLiteral("desktop.ini")), nullptr);
        Backend backend;
        mr::RecoveryController controller(&backend, &settings);
        QVERIFY(!controller.identityVerified());
        const QString path = dir.filePath(QStringLiteral("database.db"));
        controller.setDatabasePath(path); QTRY_VERIFY(!controller.busy());
        const auto data = run(QStringLiteral("reconnect")); backend.rows = {data};
        controller.saveDraft(data, fields(QStringLiteral("old database input")));
        QSignalSpy opened(&controller, &mr::RecoveryController::openRequested);
        const int issued = backend.queries;
        Q_EMIT backend.connectionChanged();
        QVERIFY(!controller.identityVerified());
        controller.requestOpen(QStringLiteral("reconnect"));
        QCOMPARE(backend.queries, issued);
        QCOMPARE(opened.size(), 1);
        QVERIFY(opened.first().at(0).toMap().value(QStringLiteral("recoveryUnavailable")).toBool());
        controller.setDatabasePath(path); QTRY_VERIFY(!controller.busy());
        QVERIFY(controller.identityVerified());
        controller.requestOpen(QStringLiteral("reconnect")); QTRY_COMPARE(opened.size(), 2);
        QVERIFY(!opened.last().at(0).toMap().value(QStringLiteral("recoveryUnavailable")).toBool());
    }
    void reconnectOffersAreBoundOnlyAfterNewIdentityVerified()
    {
        QTemporaryDir dir(testRoot()); QVERIFY(dir.isValid());
        mr::AppSettings settings(dir.filePath(QStringLiteral("desktop.ini")), nullptr);
        Backend backend;
        mr::RecoveryController controller(&backend, &settings);
        const QString a = dir.filePath(QStringLiteral("a.db"));
        const QString b = dir.filePath(QStringLiteral("b.db"));
        controller.setDatabasePath(a); QTRY_VERIFY(!controller.busy());
        controller.offer(run(QStringLiteral("from-a")), QStringLiteral("reflection"));
        QSignalSpy ready(&controller, &mr::RecoveryController::promptReady);
        const QString aPath = journal(dir.path());
        QFile old(aPath); QVERIFY(old.open(QIODevice::ReadOnly));
        const QByteArray before = old.readAll(); old.close();
        Q_EMIT backend.connectionChanged();
        controller.offer(run(QStringLiteral("from-b")), QStringLiteral("reflection"));
        QCOMPARE(ready.size(), 0);
        QVERIFY(old.open(QIODevice::ReadOnly)); QCOMPARE(old.readAll(), before); old.close();
        QCOMPARE(controller.count(), 1);
        controller.setDatabasePath(b); QTRY_VERIFY(!controller.busy());
        QCOMPARE(ready.size(), 1);
        QCOMPARE(ready.first().at(0).toMap().value(QStringLiteral("run_id")).toString(), QStringLiteral("from-b"));
        QCOMPARE(ready.first().at(1).toString(), QStringLiteral("reflection"));
        QCOMPARE(controller.count(), 1);
        QCOMPARE(controller.items().first().toMap().value(QStringLiteral("runId")).toString(), QStringLiteral("from-b"));
        controller.setDatabasePath(a); QTRY_VERIFY(!controller.busy());
        QCOMPARE(controller.count(), 1);
        QCOMPARE(controller.items().first().toMap().value(QStringLiteral("runId")).toString(), QStringLiteral("from-a"));
    }
    void sameInputMergeDoesNotProvideTokenForDifferentSubmittedFields()
    {
        QTemporaryDir dir(testRoot()); QVERIFY(dir.isValid());
        mr::AppSettings settings(dir.filePath(QStringLiteral("desktop.ini")), nullptr);
        Backend backend;
        mr::RecoveryController a(&backend, &settings);
        mr::RecoveryController b(&backend, &settings);
        const QString db = dir.filePath(QStringLiteral("records.db"));
        const auto data = run(QStringLiteral("same-run"));
        a.setDatabasePath(db); b.setDatabasePath(db); QTRY_VERIFY(!a.busy() && !b.busy());
        a.saveDraft(data, fields(QStringLiteral("editor-a")));
        const auto editorA = a.draft(data);
        b.refresh(); QTRY_VERIFY(!b.busy());
        b.saveDraft(data, fields(QStringLiteral("editor-b")));
        QVERIFY(a.saveDraft(data, fields(QStringLiteral("editor-a"))));
        // A save shortcut must not lend B's token to A's submitted content.
        QCOMPARE(a.draftTokenForFields(QStringLiteral("same-run"), fields(QStringLiteral("editor-a"))), QString());
        a.markReflectionSaved(QStringLiteral("same-run"), editorA.value(QStringLiteral("token")).toString());
        QCOMPARE(a.draft(data).value(QStringLiteral("text")).toString(), QStringLiteral("editor-b"));
        mr::RecoveryController restarted(&backend, &settings); restarted.setDatabasePath(db); QTRY_VERIFY(!restarted.busy());
        QCOMPARE(restarted.draft(data).value(QStringLiteral("text")).toString(), QStringLiteral("editor-b"));
    }
};

int main(int argc, char **argv)
{
    mrtest::disableCollectorLaunch();
    QCoreApplication app(argc, argv);
    QStandardPaths::setTestModeEnabled(true);
    RecoveryTests tests;
    return QTest::qExec(&tests, argc, argv);
}
#include "RecoveryTests.moc"
