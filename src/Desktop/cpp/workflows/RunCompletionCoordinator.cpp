#include "RunCompletionCoordinator.h"

#include <QPointer>
#include <QTimer>
#include <QTimeZone>

#include <utility>

namespace mr {

RunCompletionCoordinator::RunCompletionCoordinator(Hooks hooks, QObject *parent, QDateTime cutoffUtc)
    : QObject(parent), m_hooks(std::move(hooks)), m_cutoffUtc(std::move(cutoffUtc))
{
    Q_ASSERT(m_hooks.refreshHistory && m_hooks.refreshCurrentRun && m_hooks.refreshReflections
             && m_hooks.refreshRecovery && m_hooks.adoptRunRevision && m_hooks.requestDashboard
             && m_hooks.databaseKey && m_hooks.identityVerified && m_hooks.reflectionPromptEnabled
             && m_hooks.resultPromptEnabled && m_hooks.offerRecovery);
}

RunCompletionCoordinator::Context RunCompletionCoordinator::captureContext() const
{
    return {m_hooks.databaseKey(), m_generation};
}

bool RunCompletionCoordinator::isCurrentContext(const Context &context) const
{
    return context.generation == m_generation
        && (context.databaseKey.isEmpty() || context.databaseKey == m_hooks.databaseKey());
}

void RunCompletionCoordinator::invalidateContext(bool clearPrompts)
{
    ++m_generation;
    if (!clearPrompts)
        return;
    m_queuedResultRuns.clear();
    m_queuedReflectionRuns.clear();
    m_offeredResultRunIds.clear();
    m_offeredReflectionRunIds.clear();
    m_confirmedRunIds.clear();
    m_promptedRunIds.clear();
    m_resultConfirmationBusy = false;
    m_reflectionPromptBusy = false;
}

void RunCompletionCoordinator::stateChanged(const QString &state, const QJsonObject &run)
{
    const QPointer<RunCompletionCoordinator> guard(this);
    const auto context = captureContext();
    m_hooks.requestDashboard({});
    if (guard && isCurrentContext(context) && state == QLatin1String("COMPLETED"))
        offerReflection(run);
}

void RunCompletionCoordinator::runChanged(const QString &state, const QJsonObject &run, bool finished)
{
    const QPointer<RunCompletionCoordinator> guard(this);
    const auto context = captureContext();
    // Each synchronous operation can notify listeners that close the owner or switch its
    // database. Stop that old flow before it can touch another scope's models or prompts.
    m_hooks.refreshHistory();
    if (!guard || !isCurrentContext(context)) return;
    m_hooks.refreshCurrentRun();
    if (!guard || !isCurrentContext(context)) return;
    m_hooks.refreshReflections();
    if (!guard || !isCurrentContext(context)) return;
    m_hooks.refreshRecovery();
    if (!guard || !isCurrentContext(context)) return;
    m_hooks.adoptRunRevision(run);
    if (!guard || !isCurrentContext(context)) return;

    if (run.value(QStringLiteral("result")).toString() == QLatin1String("COMPLETED")
        && run.value(QStringLiteral("ended_at_utc")).isString())
        offerReflection(run);
    if (!guard || !isCurrentContext(context) || !finished)
        return;

    const QString terminalState = state.isEmpty() ? run.value(QStringLiteral("state")).toString() : state;
    // Questions are registered while the live run is available; only speech waits for
    // fresh statistics. Recovery may defer opening the question until identity is known.
    offerResult(terminalState, run);
    if (!guard || !isCurrentContext(context))
        return;
    m_hooks.requestDashboard([guard, context, terminalState, run](bool numbersKnown) {
        if (!guard || !guard->isCurrentContext(context)
            || !guard->endedDuringThisSession(run) || announcementKind(terminalState).isEmpty())
            return;
        if (guard->claimAnnouncement(run.value(QStringLiteral("run_id")).toString(), terminalState))
            Q_EMIT guard->announcementRequested(terminalState, run, numbersKnown);
    });
}

bool RunCompletionCoordinator::endedDuringThisSession(const QJsonObject &run) const
{
    const QDateTime ended = QDateTime::fromString(
        run.value(QStringLiteral("ended_at_utc")).toString(), Qt::ISODateWithMs);
    // An absent/unparseable end time is left to the caller's existing eligibility rules.
    return !(ended.isValid() && m_cutoffUtc.isValid() && ended < m_cutoffUtc);
}

bool RunCompletionCoordinator::emittedBeforeThisSession(const QVariantMap &event) const
{
    const QJsonValue value = QJsonValue::fromVariant(event.value(QStringLiteral("emitted_at_utc")));
    if (!value.isString())
        return false;
    QDateTime emitted = QDateTime::fromString(value.toString(), Qt::ISODateWithMs);
    if (!emitted.isValid())
        emitted = QDateTime::fromString(value.toString(), Qt::ISODate);
    if (emitted.isValid() && emitted.timeSpec() == Qt::LocalTime)
        emitted.setTimeZone(QTimeZone::UTC);
    return emitted.isValid() && m_cutoffUtc.isValid() && emitted < m_cutoffUtc;
}

QString RunCompletionCoordinator::announcementKind(const QString &state)
{
    if (state == QLatin1String("MENTOR_MATCHED")) return QStringLiteral("matched");
    if (state == QLatin1String("ENTERED_DUTY")) return QStringLiteral("entered");
    // Only an observed victory is completed; UNKNOWN_FINAL_STATE asks for confirmation.
    if (state == QLatin1String("COMPLETED")) return QStringLiteral("completed");
    if (state == QLatin1String("UNKNOWN_FINAL_STATE")) return QStringLiteral("finished");
    if (state == QLatin1String("LEFT_OR_ABANDONED") || state == QLatin1String("DISCONNECTED")
        || state == QLatin1String("INTERRUPTED") || state == QLatin1String("INTERRUPTED_PENDING_REVIEW"))
        return QStringLiteral("aborted");
    return {}; // CANCELLED_BEFORE_ENTRY is ordinary play and stays silent.
}

bool RunCompletionCoordinator::claimAnnouncement(const QString &runId, const QString &state, int matchOffer)
{
    if (runId.isEmpty())
        return true;
    const QString key = runId + QLatin1Char('|') + state
        + (matchOffer > 1 ? QLatin1Char('#') + QString::number(matchOffer) : QString());
    if (m_announcedRunStates.contains(key))
        return false;
    m_announcedRunStates.insert(key);
    return true;
}

void RunCompletionCoordinator::offerReflection(const QJsonObject &run)
{
    if (!m_hooks.reflectionPromptEnabled()) return;
    const QString runId = run.value(QStringLiteral("run_id")).toString();
    if (runId.isEmpty() || m_promptedRunIds.contains(runId) || m_confirmedRunIds.contains(runId)
        || run.value(QStringLiteral("reflection")).isObject() || !endedDuringThisSession(run))
        return;
    const QPointer<RunCompletionCoordinator> guard(this);
    const auto context = captureContext();
    m_hooks.offerRecovery(run.toVariantMap(), QStringLiteral("reflection"));
    if (!guard || !isCurrentContext(context) || !m_hooks.identityVerified()) return;
    for (const QJsonObject &queued : std::as_const(m_queuedReflectionRuns))
        if (queued.value(QStringLiteral("run_id")).toString() == runId) return;
    m_queuedReflectionRuns.append(run);
    while (m_queuedReflectionRuns.size() > kMaximumQueuedPrompts) {
        m_offeredReflectionRunIds.remove(m_queuedReflectionRuns.constFirst().value(QStringLiteral("run_id")).toString());
        m_queuedReflectionRuns.removeFirst();
    }
    emitNextReflectionPrompt();
}

void RunCompletionCoordinator::emitNextReflectionPrompt()
{
    if (m_reflectionPromptBusy || m_resultConfirmationBusy) return;
    if (!m_hooks.reflectionPromptEnabled()) {
        m_queuedReflectionRuns.clear();
        m_offeredReflectionRunIds.clear();
        return;
    }
    for (const QJsonObject &run : std::as_const(m_queuedReflectionRuns)) {
        const QString runId = run.value(QStringLiteral("run_id")).toString();
        if (m_offeredReflectionRunIds.contains(runId)) continue;
        m_offeredReflectionRunIds.insert(runId);
        Q_EMIT reflectionPromptRequested(run.toVariantMap());
        return;
    }
}

void RunCompletionCoordinator::reflectionPromptShown(const QString &runId)
{
    if (runId.isEmpty()) return;
    m_reflectionPromptBusy = true;
    m_queuedReflectionRuns.removeIf([&runId](const QJsonObject &run) {
        return run.value(QStringLiteral("run_id")).toString() == runId;
    });
    m_offeredReflectionRunIds.remove(runId);
    m_promptedRunIds.insert(runId);
}

void RunCompletionCoordinator::reflectionPromptClosed()
{
    m_reflectionPromptBusy = false;
    m_offeredReflectionRunIds.clear();
    emitNextReflectionPrompt();
}

void RunCompletionCoordinator::reflectionPromptDeferred(const QString &runId)
{
    if (runId.isEmpty()) return;
    m_promptedRunIds.insert(runId);
    m_offeredReflectionRunIds.remove(runId);
    m_queuedReflectionRuns.removeIf([&runId](const QJsonObject &run) {
        return run.value(QStringLiteral("run_id")).toString() == runId;
    });
    const auto context = captureContext();
    // QML can defer inside the offer signal; the next offer must leave that callback first.
    QTimer::singleShot(0, this, [this, context] {
        if (isCurrentContext(context)) emitNextReflectionPrompt();
    });
}

void RunCompletionCoordinator::offerResult(const QString &state, const QJsonObject &run)
{
    if (!m_hooks.resultPromptEnabled() || !run.value(QStringLiteral("pending_review")).toBool(false)
        || state != QLatin1String("UNKNOWN_FINAL_STATE")) return;
    const QString runId = run.value(QStringLiteral("run_id")).toString();
    if (runId.isEmpty() || m_confirmedRunIds.contains(runId) || !endedDuringThisSession(run)) return;
    const QPointer<RunCompletionCoordinator> guard(this);
    const auto context = captureContext();
    m_hooks.offerRecovery(run.toVariantMap(), QStringLiteral("result"));
    if (!guard || !isCurrentContext(context) || !m_hooks.identityVerified()) return;
    for (const QJsonObject &queued : std::as_const(m_queuedResultRuns))
        if (queued.value(QStringLiteral("run_id")).toString() == runId) return;
    m_queuedResultRuns.append(run);
    while (m_queuedResultRuns.size() > kMaximumQueuedPrompts) {
        m_offeredResultRunIds.remove(m_queuedResultRuns.constFirst().value(QStringLiteral("run_id")).toString());
        m_queuedResultRuns.removeFirst();
    }
    emitNextResultConfirmation();
}

void RunCompletionCoordinator::emitNextResultConfirmation()
{
    if (m_resultConfirmationBusy) return;
    if (!m_hooks.resultPromptEnabled()) {
        m_queuedResultRuns.clear();
        m_offeredResultRunIds.clear();
        return;
    }
    for (const QJsonObject &run : std::as_const(m_queuedResultRuns)) {
        const QString runId = run.value(QStringLiteral("run_id")).toString();
        if (m_offeredResultRunIds.contains(runId)) continue;
        m_offeredResultRunIds.insert(runId);
        Q_EMIT resultConfirmationRequested(run.toVariantMap());
        return;
    }
}

void RunCompletionCoordinator::resultConfirmationShown(const QString &runId)
{
    if (runId.isEmpty()) return;
    m_resultConfirmationBusy = true;
    m_queuedResultRuns.removeIf([&runId](const QJsonObject &run) {
        return run.value(QStringLiteral("run_id")).toString() == runId;
    });
    m_offeredResultRunIds.remove(runId);
    m_confirmedRunIds.insert(runId);
}

void RunCompletionCoordinator::resultConfirmationClosed()
{
    m_resultConfirmationBusy = false;
    m_offeredResultRunIds.clear();
    emitNextResultConfirmation();
}

void RunCompletionCoordinator::resultConfirmationDeferred(const QString &runId)
{
    if (runId.isEmpty()) return;
    m_confirmedRunIds.insert(runId);
    m_offeredResultRunIds.remove(runId);
    m_queuedResultRuns.removeIf([&runId](const QJsonObject &run) {
        return run.value(QStringLiteral("run_id")).toString() == runId;
    });
    const auto context = captureContext();
    QTimer::singleShot(0, this, [this, context] {
        if (isCurrentContext(context)) emitNextResultConfirmation();
    });
}

void RunCompletionCoordinator::runReviewed(const QString &runId)
{
    if (runId.isEmpty()) return;
    const auto sameRun = [&runId](const QJsonObject &run) {
        return run.value(QStringLiteral("run_id")).toString() == runId;
    };
    m_queuedResultRuns.removeIf(sameRun);
    m_queuedReflectionRuns.removeIf(sameRun);
    m_offeredResultRunIds.remove(runId);
    m_offeredReflectionRunIds.remove(runId);
    m_confirmedRunIds.insert(runId);
}

void RunCompletionCoordinator::reflectionSaved(const QString &runId)
{
    m_promptedRunIds.insert(runId);
    m_queuedReflectionRuns.removeIf([&runId](const QJsonObject &run) {
        return run.value(QStringLiteral("run_id")).toString() == runId;
    });
}

} // namespace mr
