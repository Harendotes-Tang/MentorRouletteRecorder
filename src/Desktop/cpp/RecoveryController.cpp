#include "RecoveryController.h"
#include "AppSettings.h"
#include "IBackend.h"

#include <QCoreApplication>
#include <QCryptographicHash>
#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QLockFile>
#include <QSaveFile>
#include <QSignalBlocker>
#include <QTimer>
#include <QUuid>
#include <algorithm>
#include <utility>

namespace {
QString runId(const QVariantMap &run) { return run.value(QStringLiteral("run_id")).toString(); }
QString fingerprint(const QVariant &reflection)
{
    // Only what the user wrote. The Collector's RunReflection also carries its
    // timestamps, which an editor's own copy after a save does not; hashing them
    // made every reopened draft a false conflict (review L2-7).
    const QVariantMap map = reflection.toMap();
    QJsonObject json;
    for (const QString &key : {QStringLiteral("text"), QStringLiteral("mood")}) {
        if (map.contains(key))
            json.insert(key, QJsonValue::fromVariant(map.value(key)));
    }
    return QString::fromLatin1(QCryptographicHash::hash(QJsonDocument(json).toJson(QJsonDocument::Compact),
                                                     QCryptographicHash::Sha256).toHex());
}
bool active(const QVariantMap &entry)
{
    return entry.value(QStringLiteral("result")).toBool()
        || entry.value(QStringLiteral("reflection")).toBool()
        || !entry.value(QStringLiteral("draft")).toMap().isEmpty();
}
QString scopedDatabaseKey(const QString &path)
{
    QString normalized = QDir::cleanPath(QDir::fromNativeSeparators(QFileInfo(path).absoluteFilePath()));
#ifdef Q_OS_WIN
    normalized = normalized.toCaseFolded();
#endif
    return QString::fromLatin1(QCryptographicHash::hash(normalized.toUtf8(), QCryptographicHash::Sha256).toHex());
}
bool identityChanged(const QVariantMap &stored, const QVariantMap &current)
{
    const QString identity = stored.value(QStringLiteral("created_at_utc")).toString();
    return !identity.isEmpty() && identity != current.value(QStringLiteral("created_at_utc")).toString();
}
bool statisticallyEligible(const QVariantMap &run)
{
    const QString source = run.value(QStringLiteral("source")).toString();
    return source == QLatin1String("MANUAL")
        || (source == QLatin1String("AUTO_NETWORK")
            && run.contains(QStringLiteral("mentor_roulette_id"))
            && !run.value(QStringLiteral("mentor_roulette_id")).isNull());
}
} // namespace

namespace mr {
RecoveryController::RecoveryController(IBackend *backend, AppSettings *settings, QObject *parent)
    : QObject(parent), m_backend(backend), m_settings(settings)
{
    m_identityVerified = !settings;
    if (QCoreApplication::instance())
        connect(QCoreApplication::instance(), &QCoreApplication::aboutToQuit,
                this, &RecoveryController::flushOnQuit, Qt::DirectConnection);
    if (backend) {
        connect(backend, &IBackend::connectionChanged, this, [this] {
            invalidateIdentity();
            if (m_identityVerified && m_backend && m_backend->isConnected())
                refresh();
        });
    }
}

void RecoveryController::invalidateIdentity()
{
    ++m_generation;
    ++m_scopeGeneration;
    ++m_openGeneration;
    m_busy = false;
    m_refreshAgain = false;
    m_identityVerified = !m_settings;
    m_provisionalOffers.clear();
    Q_EMIT changed();
}

void RecoveryController::flushOnQuit()
{
    Q_EMIT flushRequested();
    flushJournal();
    if (!m_settings)
        return;
    const Scope current{m_entries, m_edits, m_journalError};
    const QString currentKey = m_databaseKey;
    const QString currentPath = m_journalPath;
    const QString currentError = m_error;
    bool failed = false;
    {
        // No event loop is entered here. Keep temporary journal identities private
        // while retrying dirty inactive databases; the open editor was flushed above.
        const QSignalBlocker block(this);
        for (auto it = m_scopes.begin(); it != m_scopes.end(); ++it) {
            if (it.key() == currentKey || it->edits.isEmpty())
                continue;
            m_databaseKey = it.key();
            m_journalPath = QDir(QFileInfo(m_settings->instanceFilePath()).absolutePath())
                                .absoluteFilePath(QStringLiteral("recovery/") + it.key() + QStringLiteral(".json"));
            m_entries = it->entries;
            m_edits = it->edits;
            m_journalError = it->journalError;
            failed |= !flushJournal();
            *it = {m_entries, m_edits, m_journalError};
        }
        m_databaseKey = currentKey;
        m_journalPath = currentPath;
        m_entries = current.entries;
        m_edits = current.edits;
        m_journalError = current.journalError;
        m_error = currentError;
    }
    if (failed && m_error.isEmpty())
        updateError(tr("无法保存待办草稿；请保留当前窗口，稍后重试。"));
}

void RecoveryController::updateError(const QString &message)
{
    if (m_error == message)
        return;
    m_error = message;
    Q_EMIT changed();
}

void RecoveryController::updateJournalError(const QString &message)
{
    m_journalError = message;
    updateError(message);
}

void RecoveryController::setDatabasePath(const QString &path)
{
    if (path.trimmed().isEmpty())
        return;
    const QString key = scopedDatabaseKey(path);
    if (key == m_databaseKey) {
        if (!m_identityVerified) {
            m_identityVerified = true;
            adoptProvisionalOffers();
            Q_EMIT changed();
            refresh();
        }
        return;
    }
    const bool firstIdentity = m_databaseKey.isEmpty();
    if (!firstIdentity) {
        Q_EMIT scopeChanging();
        flushJournal();
        m_scopes.insert(m_databaseKey, {m_entries, m_edits, m_journalError});
    }
    ++m_generation;
    ++m_scopeGeneration;
    ++m_openGeneration;
    m_busy = false;
    m_refreshAgain = false;
    m_pending.clear();
    if (!firstIdentity) {
        const Scope cached = m_scopes.value(key);
        m_entries = cached.entries;
        m_edits = cached.edits;
        m_journalError = cached.journalError;
    }
    m_databaseKey = key;
    m_identityVerified = true;
    m_journalPath.clear();
    m_error = m_journalError;
    if (m_settings) {
        const QString root = QDir(QFileInfo(m_settings->instanceFilePath()).absolutePath())
                                 .absoluteFilePath(QStringLiteral("recovery"));
        m_journalPath = QDir(root).absoluteFilePath(key + QStringLiteral(".json"));
        // First verified identity claims only this process's pre-status work;
        // changing an established identity always starts from its own journal.
        flushJournal();
    }
    adoptProvisionalOffers();
    Q_EMIT scopeChanged();
    Q_EMIT changed();
    refresh();
}

void RecoveryController::adoptProvisionalOffers()
{
    const auto offers = std::exchange(m_provisionalOffers, QHash<QString, QVariantMap>());
    for (auto it = offers.cbegin(); it != offers.cend(); ++it) {
        persist({it.key(), it.value(), {}, false});
        const QVariantMap run = it.value().value(QStringLiteral("run")).toMap();
        if (it.value().value(QStringLiteral("result")).toBool())
            Q_EMIT promptReady(run, QStringLiteral("result"));
        if (it.value().value(QStringLiteral("reflection")).toBool())
            Q_EMIT promptReady(run, QStringLiteral("reflection"));
    }
}

bool RecoveryController::readJournal(QHash<QString, QVariantMap> &entries)
{
    QFile file(m_journalPath);
    if (!file.exists())
        return true;
    if (!file.open(QIODevice::ReadOnly)) {
        updateJournalError(tr("无法读取待办草稿；当前输入仍保留在窗口中。"));
        return false;
    }
    QJsonParseError parseError;
    const QJsonDocument json = QJsonDocument::fromJson(file.readAll(), &parseError);
    const QJsonObject root = json.object();
    if (parseError.error != QJsonParseError::NoError || !json.isObject()
        || root.value(QStringLiteral("version")).toInt() != 1
        || root.value(QStringLiteral("database")).toString() != m_databaseKey
        || !root.value(QStringLiteral("entries")).isObject()) {
        updateJournalError(tr("待办草稿文件无法识别，已保留原文件；当前输入仍在内存中。"));
        return false;
    }
    const QJsonObject stored = root.value(QStringLiteral("entries")).toObject();
    for (auto it = stored.begin(); it != stored.end(); ++it) {
        if (!it.value().isObject()) {
            updateJournalError(tr("待办草稿文件无法识别，已保留原文件；当前输入仍在内存中。"));
            return false;
        }
        const QJsonObject object = it.value().toObject();
        const QJsonObject storedDraft = object.value(QStringLiteral("draft")).toObject();
        const bool malformedDraft = object.contains(QStringLiteral("draft"))
            && (!object.value(QStringLiteral("draft")).isObject()
                || !storedDraft.value(QStringLiteral("text")).isString()
                || !storedDraft.value(QStringLiteral("mood")).isString()
                || storedDraft.value(QStringLiteral("token")).toString().isEmpty()
                || !storedDraft.value(QStringLiteral("baseReflection")).isString());
        const QVariantMap entry = object.toVariantMap();
        if (runId(entry.value(QStringLiteral("run")).toMap()) != it.key()
            || !object.value(QStringLiteral("run")).isObject() || malformedDraft
            || (object.contains(QStringLiteral("result")) && !object.value(QStringLiteral("result")).isBool())
            || (object.contains(QStringLiteral("reflection")) && !object.value(QStringLiteral("reflection")).isBool())) {
            updateJournalError(tr("待办草稿文件无法识别，已保留原文件；当前输入仍在内存中。"));
            return false;
        }
        if (active(entry))
            entries.insert(it.key(), entry);
    }
    return true;
}

bool RecoveryController::flushJournal()
{
    if (m_journalPath.isEmpty())
        return true; // Memory-only harness, or no verified database yet.
    if (!QDir().mkpath(QFileInfo(m_journalPath).absolutePath())) {
        updateJournalError(tr("无法保存待办草稿；请保留当前窗口，稍后重试。"));
        return false;
    }
    QLockFile lock(m_journalPath + QStringLiteral(".lock"));
    if (!lock.tryLock(0)) {
        updateJournalError(tr("另一个窗口正在保存待办草稿，请稍后重试。"));
        return false;
    }
    QHash<QString, QVariantMap> merged;
    if (!readJournal(merged))
        return false; // Never replace corrupt/unreadable bytes with a new journal.
    for (const Edit &edit : std::as_const(m_edits)) {
        QVariantMap entry = merged.value(edit.id);
        for (auto it = edit.fields.cbegin(); it != edit.fields.cend(); ++it)
            entry.insert(it.key(), it.value());
        if (edit.clearDraft && (edit.clearToken.isEmpty()
                || entry.value(QStringLiteral("draft")).toMap().value(QStringLiteral("token")).toString() == edit.clearToken))
            entry.remove(QStringLiteral("draft"));
        if (active(entry))
            merged.insert(edit.id, entry);
        else
            merged.remove(edit.id);
    }
    if (!m_edits.isEmpty()) {
        QJsonObject rows;
        for (auto it = merged.cbegin(); it != merged.cend(); ++it)
            rows.insert(it.key(), QJsonObject::fromVariantMap(it.value()));
        const QByteArray data = QJsonDocument(QJsonObject{
            {QStringLiteral("version"), 1}, {QStringLiteral("database"), m_databaseKey},
            {QStringLiteral("entries"), rows}}).toJson(QJsonDocument::Compact);
        QSaveFile file(m_journalPath);
        if (!file.open(QIODevice::WriteOnly) || file.write(data) != data.size() || !file.commit()) {
            updateJournalError(tr("无法保存待办草稿；请保留当前窗口，稍后重试。"));
            return false;
        }
    }
    m_entries = std::move(merged);
    m_edits.clear();
    m_error.clear();
    m_journalError.clear();
    return true;
}

bool RecoveryController::persist(const Edit &edit)
{
    QVariantMap entry = m_entries.value(edit.id);
    for (auto it = edit.fields.cbegin(); it != edit.fields.cend(); ++it)
        entry.insert(it.key(), it.value());
    if (edit.clearDraft && (edit.clearToken.isEmpty()
            || entry.value(QStringLiteral("draft")).toMap().value(QStringLiteral("token")).toString() == edit.clearToken))
        entry.remove(QStringLiteral("draft"));
    if (active(entry))
        m_entries.insert(edit.id, entry);
    else
        m_entries.remove(edit.id);
    if (m_settings)
        m_edits.append(edit);
    const bool ok = flushJournal();
    Q_EMIT changed();
    return ok;
}

void RecoveryController::offer(const QVariantMap &run, const QString &kind)
{
    const QString id = runId(run);
    if (id.isEmpty() || !statisticallyEligible(run)
        || (kind != QLatin1String("result") && kind != QLatin1String("reflection")))
        return;
    if (!m_identityVerified) {
        QVariantMap provisional = m_provisionalOffers.value(id);
        provisional.insert(QStringLiteral("run"), run);
        provisional.insert(kind, true);
        m_provisionalOffers.insert(id, provisional);
        return;
    }
    if (m_busy && kind == QLatin1String("result"))
        m_refreshAgain = true;
    persist({id, {{QStringLiteral("run"), run}, {kind, true}}, {}, false});
}

bool RecoveryController::saveDraft(const QVariantMap &run, const QVariantMap &fields)
{
    const QString id = runId(run);
    if (id.isEmpty())
        return false;
    const QVariantMap reflection = run.value(QStringLiteral("reflection")).toMap();
    const QString text = fields.value(QStringLiteral("text")).toString();
    const QString mood = fields.value(QStringLiteral("mood"), QStringLiteral("ok")).toString();
    const QVariant job = fields.value(QStringLiteral("jobId"), run.value(QStringLiteral("job_id")));
    const QVariantMap old = m_entries.value(id).value(QStringLiteral("draft")).toMap();
    if (!old.isEmpty() && identityChanged(old, run)) {
        updateError(tr("记录身份已改变；原草稿已保留，请先复制内容。"));
        return false;
    }
    if (text == reflection.value(QStringLiteral("text")).toString()
        && mood == reflection.value(QStringLiteral("mood"), QStringLiteral("ok")).toString()
        && job.toString() == run.value(QStringLiteral("job_id")).toString()) {
        return old.isEmpty() ? flushJournal()
                             : persist({id, {}, old.value(QStringLiteral("token")).toString(), true});
    }
    if (!old.isEmpty() && old.value(QStringLiteral("text")).toString() == text
        && old.value(QStringLiteral("mood")).toString() == mood
        && old.value(QStringLiteral("jobId")).toString() == job.toString()
        && !fields.value(QStringLiteral("resetBaseline")).toBool())
        return flushJournal();
    QVariantMap next{{QStringLiteral("text"), text}, {QStringLiteral("mood"), mood},
                     {QStringLiteral("jobId"), job},
                     {QStringLiteral("token"), QUuid::createUuid().toString(QUuid::WithoutBraces)},
                     {QStringLiteral("baseReflection"), !old.isEmpty() && !fields.value(QStringLiteral("resetBaseline")).toBool()
                         ? old.value(QStringLiteral("baseReflection")) : fingerprint(run.value(QStringLiteral("reflection")))},
                     {QStringLiteral("created_at_utc"), run.value(QStringLiteral("created_at_utc"))}};
    return persist({id, {{QStringLiteral("run"), run}, {QStringLiteral("draft"), next}}, {}, false});
}

QVariantMap RecoveryController::draft(const QVariantMap &run) const
{
    QVariantMap saved = m_entries.value(runId(run)).value(QStringLiteral("draft")).toMap();
    if (saved.isEmpty())
        return {};
    const bool identityConflict = identityChanged(saved, run);
    saved.insert(QStringLiteral("identityConflict"), identityConflict);
    saved.insert(QStringLiteral("conflict"), identityConflict
        || saved.value(QStringLiteral("baseReflection")).toString() != fingerprint(run.value(QStringLiteral("reflection"))));
    saved.insert(QStringLiteral("latest"), run.value(QStringLiteral("reflection")).toMap());
    return saved;
}

QString RecoveryController::draftTokenForFields(const QString &id, const QVariantMap &fields) const
{
    const QVariantMap stored = m_entries.value(id).value(QStringLiteral("draft")).toMap();
    if (stored.isEmpty()
        || stored.value(QStringLiteral("text")).toString() != fields.value(QStringLiteral("text")).toString()
        || stored.value(QStringLiteral("mood")).toString() != fields.value(QStringLiteral("mood"), QStringLiteral("ok")).toString()
        || stored.value(QStringLiteral("jobId")).toString() != fields.value(QStringLiteral("jobId")).toString())
        return {};
    return stored.value(QStringLiteral("token")).toString();
}

void RecoveryController::clearDraft(const QString &id, const QString &token)
{
    if (!id.isEmpty())
        persist({id, {}, token, true});
}
void RecoveryController::markReflectionSaved(const QString &id, const QString &token)
{
    if (!id.isEmpty())
        persist({id, {{QStringLiteral("reflection"), false}}, token, !token.isEmpty()});
}
void RecoveryController::markResultSaved(const QString &id)
{
    if (!id.isEmpty())
        persist({id, {{QStringLiteral("result"), false}}, {}, false});
}

void RecoveryController::dismiss(const QString &id)
{
    if (id.isEmpty() || !m_entries.value(id).value(QStringLiteral("unavailable")).toBool())
        return;
    persist({id, {{QStringLiteral("result"), false}, {QStringLiteral("reflection"), false}}, {}, true});
}

QVariantMap RecoveryController::mergedRun(const QString &id) const
{
    const auto local = m_entries.value(id).value(QStringLiteral("run")).toMap();
    return m_pending.contains(id) ? m_pending.value(id) : local;
}

QVariantList RecoveryController::items() const
{
    QStringList ids = m_entries.keys();
    for (auto it = m_pending.cbegin(); it != m_pending.cend(); ++it)
        if (!ids.contains(it.key()))
            ids.append(it.key());
    QVariantList result;
    for (const QString &id : ids) {
        const QVariantMap entry = m_entries.value(id);
        const bool needsResult = m_pending.contains(id) || entry.value(QStringLiteral("result")).toBool();
        const bool needsReflection = entry.value(QStringLiteral("reflection")).toBool();
        const bool hasDraft = !entry.value(QStringLiteral("draft")).toMap().isEmpty();
        if (!needsResult && !needsReflection && !hasDraft)
            continue;
        QVariantMap row = mergedRun(id);
        row.insert(QStringLiteral("run"), row);
        row.insert(QStringLiteral("runId"), id);
        row.insert(QStringLiteral("needsResult"), needsResult);
        row.insert(QStringLiteral("needsReflection"), needsReflection);
        row.insert(QStringLiteral("hasDraft"), hasDraft);
        row.insert(QStringLiteral("draft"), draft(mergedRun(id)));
        row.insert(QStringLiteral("recoveryUnavailable"), entry.value(QStringLiteral("unavailable")).toBool());
        row.insert(QStringLiteral("conflict"), draft(mergedRun(id)).value(QStringLiteral("conflict")).toBool());
        result.append(row);
    }
    std::sort(result.begin(), result.end(), [](const QVariant &a, const QVariant &b) {
        const auto left = a.toMap();
        const auto right = b.toMap();
        const QString lt = left.value(QStringLiteral("matched_at_utc")).toString();
        const QString rt = right.value(QStringLiteral("matched_at_utc")).toString();
        return lt == rt ? left.value(QStringLiteral("runId")).toString() < right.value(QStringLiteral("runId")).toString()
                        : lt > rt;
    });
    return result;
}

void RecoveryController::refresh()
{
    if (m_busy) {
        m_refreshAgain = true;
        return;
    }
    flushJournal();
    if (!m_identityVerified || !m_backend || !m_backend->isConnected()) {
        Q_EMIT changed();
        return;
    }
    m_busy = true;
    m_refreshAgain = false;
    Q_EMIT changed();
    fetchPage(++m_generation, 1, {});
}

void RecoveryController::fetchPage(quint64 generation, int page, QHash<QString, QVariantMap> collected)
{
    if (!m_backend || generation != m_generation)
        return;
    const QJsonObject filter{{QStringLiteral("pending_review"), true},
        {QStringLiteral("source"), QJsonArray{QStringLiteral("AUTO_NETWORK"), QStringLiteral("MANUAL")}}};
    const QJsonObject sort{{QStringLiteral("field"), QStringLiteral("matched_at_utc")},
                           {QStringLiteral("direction"), QStringLiteral("desc")}};
    m_backend->queryRuns(filter, page, 200, sort)->whenDone(this,
        [this, generation, page, collected = std::move(collected)]
        (bool ok, const QVariantMap &payload, const QString &, const QString &) mutable {
        if (generation != m_generation)
            return;
        bool totalValid = false;
        const int total = payload.value(QStringLiteral("page_info")).toMap()
                              .value(QStringLiteral("total")).toInt(&totalValid);
        ok = ok && payload.contains(QStringLiteral("items"))
            && payload.value(QStringLiteral("items")).canConvert<QVariantList>() && totalValid && total >= 0;
        if (!ok) {
            m_busy = false;
            updateError(tr("待办暂时无法更新，已保留上次记录和草稿。"));
            Q_EMIT changed();
        } else {
            const QVariantList rows = payload.value(QStringLiteral("items")).toList();
            for (const QVariant &value : rows) {
                const auto row = value.toMap();
                if (!runId(row).isEmpty() && statisticallyEligible(row))
                    collected.insert(runId(row), row);
            }
            if (rows.size() == 200 && qint64(page) * 200 < total) {
                QTimer::singleShot(0, this, [this, generation, page, collected = std::move(collected)]() mutable {
                    fetchPage(generation, page + 1, std::move(collected));
                });
                return;
            }
            m_pending = std::move(collected);
            m_busy = false;
            // Only a complete successful pending snapshot proves a registered
            // result was handled elsewhere. An offer during this read gets a new read.
            if (!m_refreshAgain) {
                const auto ids = m_entries.keys();
                for (const QString &id : ids)
                    if (m_entries.value(id).value(QStringLiteral("result")).toBool() && !m_pending.contains(id))
                        persist({id, {{QStringLiteral("result"), false}}, {}, false});
            }
            m_error = m_journalError;
            Q_EMIT changed();
        }
        if (m_refreshAgain) {
            m_refreshAgain = false;
            QTimer::singleShot(0, this, &RecoveryController::refresh);
        }
    });
}

void RecoveryController::requestInbox() { Q_EMIT inboxRequested(); }

void RecoveryController::publishOpen(const QVariantMap &run, bool unavailable)
{
    const QString id = runId(run);
    QVariantMap selected = run;
    const QVariantMap saved = draft(run);
    const bool identityConflict = saved.value(QStringLiteral("identityConflict")).toBool()
        || identityChanged(m_entries.value(id).value(QStringLiteral("run")).toMap(), run);
    selected.insert(QStringLiteral("recoveryUnavailable"), unavailable || identityConflict);
    selected.insert(QStringLiteral("recoveryDraft"), saved);
    selected.insert(QStringLiteral("recoveryConflict"), saved.value(QStringLiteral("conflict")));
    if (m_entries.contains(id))
        persist({id, {{QStringLiteral("unavailable"), unavailable || identityConflict}}, {}, false});
    const QString kind = !unavailable && !identityConflict
            && statisticallyEligible(run)
            && run.value(QStringLiteral("pending_review")).toBool()
            && run.value(QStringLiteral("result")).toString() == QLatin1String("UNKNOWN")
        ? QStringLiteral("result") : QStringLiteral("reflection");
    Q_EMIT openRequested(selected, kind);
}

void RecoveryController::requestOpen(const QString &id)
{
    if (id.isEmpty())
        return;
    const QVariantMap fallback = mergedRun(id);
    const quint64 generation = ++m_openGeneration;
    const quint64 databaseGeneration = m_scopeGeneration;
    if (!m_identityVerified || !m_backend || !m_backend->isConnected()) {
        updateError(tr("当前无法读取最新记录；草稿仍可查看和复制。"));
        if (!fallback.isEmpty())
            publishOpen(fallback, true);
        return;
    }
    const QJsonObject filter{{QStringLiteral("run_id"), QJsonArray{id}}};
    m_backend->queryRuns(filter, 1, 1)->whenDone(this,
        [this, id, fallback, generation, databaseGeneration]
        (bool ok, const QVariantMap &payload, const QString &, const QString &) {
        if (generation != m_openGeneration || databaseGeneration != m_scopeGeneration)
            return;
        const QVariantList rows = payload.value(QStringLiteral("items")).toList();
        if (!ok || rows.isEmpty() || rows.first().toMap().value(QStringLiteral("soft_deleted")).toBool()) {
            updateError(ok ? tr("这条记录已不存在或已删除；保留的草稿仍可复制。")
                           : tr("当前无法读取最新记录；草稿仍可查看和复制。"));
            if (ok) {
                // A verified answer: the record is gone, and its reminders with it.
                // A draft the user typed stays until they remove it (review I2-1).
                const bool wasPending = m_pending.remove(id) > 0;
                if (m_entries.contains(id))
                    persist({id, {{QStringLiteral("result"), false}, {QStringLiteral("reflection"), false}}, {}, false});
                else if (wasPending)
                    Q_EMIT changed();
            }
            if (!fallback.isEmpty())
                publishOpen(fallback, true);
            return;
        }
        const QVariantMap latest = rows.first().toMap();
        if (runId(latest) != id) {
            updateError(tr("当前无法读取最新记录；草稿仍可查看和复制。"));
            if (!fallback.isEmpty())
                publishOpen(fallback, true);
            return;
        }
        publishOpen(latest, false);
    });
}
} // namespace mr
