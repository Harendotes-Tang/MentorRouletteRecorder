#include "GameSelectionController.h"
#include "CollectorText.h"
#include <QDateTime>
#include <QPointer>

#ifdef Q_OS_WIN
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#endif

namespace mr {
GameSelectionController::GameSelectionController(IBackend *backend, QObject *parent)
    : QObject(parent), m_backend(backend)
{
    m_picker.setInterval(150);
    connect(&m_picker, &QTimer::timeout, this, [this] {
        if (++m_ticks >= 100) {
            cancelPick();
            m_message = [] { return tr("未选中游戏窗口，当前记录对象没有改变。可重试或从列表选择。"); };
            emit changed();
            return;
        }
#ifdef Q_OS_WIN
        DWORD pid = 0;
        GetWindowThreadProcessId(GetForegroundWindow(), &pid);
        observeForegroundProcess(static_cast<int>(pid));
#endif
    });
    connect(backend, &IBackend::connectionChanged, this, [this] {
        ++m_generation;
        m_picker.stop();
        m_busy = false;
        m_choices.clear();
        m_pickChoices.clear();
        m_currentLabel.clear();
        m_message.clear();
        m_selectionMessage.clear();
        m_required = false;
        m_observedPid = 0;
        m_confirmedToken.clear();
        m_confirmedAt.invalidate();
        emit choicesChanged();
        emit changed();
    });
    onLanguageChanged(this, [this] {
        relabel();
        emit choicesChanged();
        emit changed();
    });
}

QString GameSelectionController::choiceLabel(const QVariantMap &choice, int number)
{
    const auto started = QDateTime::fromString(
        choice.value(QStringLiteral("started_at_utc")).toString(), Qt::ISODateWithMs);
    return started.isValid()
        ? tr("游戏 %1 · %2 启动").arg(number).arg(started.toLocalTime().toString(QStringLiteral("HH:mm:ss")))
        : tr("游戏 %1 · 启动时间暂不可读").arg(number);
}

void GameSelectionController::relabel()
{
    const auto relabelList = [](QVariantList &list) {
        for (int index = 0; index < list.size(); ++index) {
            auto choice = list.at(index).toMap();
            choice.insert(QStringLiteral("label"), choiceLabel(choice, index + 1));
            list[index] = choice;
        }
    };
    relabelList(m_choices);
    relabelList(m_pickChoices);
    m_currentLabel.clear();
    for (const auto &value : std::as_const(m_choices)) {
        const auto choice = value.toMap();
        if (choice.value(QStringLiteral("process_id")).toInt() == m_observedPid)
            m_currentLabel = choice.value(QStringLiteral("label")).toString();
    }
}

bool GameSelectionController::predatesConfirmedChoice(const QVariantMap &capture) const
{
    if (m_confirmedToken.isEmpty() || !m_confirmedAt.isValid()
        || m_confirmedAt.hasExpired(kSelectionSettleMs))
        return false;
    if (!capture.value(QStringLiteral("game_selection_required")).toBool()
        && capture.value(QStringLiteral("ffxiv_process_id")).toInt() == m_confirmedPid)
        return false;
    // The Collector does not ask again about a client it has just pinned while
    // that client is still listed; such a snapshot was taken before the switch
    // (a status event published mid-switch can arrive after the reply).
    for (const auto &value : capture.value(QStringLiteral("game_processes")).toList()) {
        if (value.toMap().value(QStringLiteral("selection_token")).toString() == m_confirmedToken)
            return true;
    }
    return false;
}

void GameSelectionController::observe(const QVariantMap &capture)
{
    if (predatesConfirmedChoice(capture))
        return;
    QVariantList choices;
    const auto processes = capture.value(QStringLiteral("game_processes")).toList();
    const bool wasRequired = m_required;
    // Nothing listed is nothing to choose: the only client closed, or every one
    // did. That is waiting for the game, not a selection (review OD-1).
    m_required = capture.value(QStringLiteral("game_selection_required")).toBool()
                 && !processes.isEmpty();
    m_selectionMessage.clear();
    if (m_required) {
        const auto reason = capture.value(QStringLiteral("game_selection_reason")).toString();
        m_selectionMessage = [reason, several = processes.size() > 1] {
            return reason == QLatin1String("EXITED")
                ? tr("所选游戏已退出，记录已暂停。单开时重新启动同一游戏会自动接续；无法确认时请重新选择游戏窗口。")
                : reason == QLatin1String("IDENTITY_UNAVAILABLE")
                ? tr("暂时无法确认游戏的启动时间，记录已暂停。请重新检测后选择游戏窗口。")
                : several
                ? tr("检测到多个游戏客户端，请选择要记录的游戏窗口。")
                : tr("尚未确定要记录的游戏，请选择要记录的游戏窗口。");
        };
    }
    m_currentLabel.clear();
    const int selectedPid = capture.value(QStringLiteral("ffxiv_process_id")).toInt();
    // "已锁定…" or a refusal speaks about the target as it was; once the Collector
    // reports another one, the line would sit beside a contradiction (review OD-2).
    if ((m_required != wasRequired || selectedPid != m_observedPid) && !m_busy && !picking())
        m_message.clear();
    m_observedPid = selectedPid;
    for (const auto &value : processes) {
        auto choice = value.toMap();
        const QString label = choiceLabel(choice, int(choices.size()) + 1);
        choice.insert(QStringLiteral("label"), label);
        choices.append(choice);
        if (choice.value(QStringLiteral("process_id")).toInt() == selectedPid)
            m_currentLabel = label;
    }
    if (m_choices != choices) {
        m_choices = choices;
        emit choicesChanged();
    }
    emit changed();
}

void GameSelectionController::beginPick()
{
    if (!m_backend || !m_backend->isConnected() || m_busy || picking()) return;
    m_busy = true;
    m_message.clear();
    emit changed();
    const auto generation = m_generation;
    BackendReply *reply = m_backend->getCaptureStatus();
    const QPointer<BackendReply> guard(reply);
    reply->whenDone(this,
        [this, guard, generation](bool ok, const QVariantMap &capture, const QString &, const QString &error) {
        if (generation != m_generation) return;
        m_busy = false;
        if (!ok) m_message = collector_text::replyErrorText(guard, error);
        else {
            observe(capture);
            m_pickChoices = m_choices;
            if (m_pickChoices.isEmpty()) m_message = [] { return tr("请先启动要记录的游戏。"); };
            else {
                m_ticks = 0;
                m_picker.start();
                m_message = [] { return tr("请在 15 秒内点击要记录的 FF14 窗口；选中后会锁定它。"); };
            }
        }
        emit changed();
    });
}

void GameSelectionController::cancelPick()
{
    m_picker.stop();
    m_pickChoices.clear();
    m_message.clear();
    emit changed();
}

void GameSelectionController::observeForegroundProcess(int processId)
{
    if (!picking() || m_busy) return;
    for (const auto &value : m_pickChoices) {
        const auto choice = value.toMap();
        if (choice.value(QStringLiteral("process_id")).toInt() == processId) {
            cancelPick();
            choose(choice);
            return;
        }
    }
}

void GameSelectionController::select(int index)
{
    if (m_busy || picking() || index < 0 || index >= m_choices.size()) return;
    choose(m_choices[index].toMap());
}

void GameSelectionController::choose(const QVariantMap &choice)
{
    if (!m_backend || !m_backend->isConnected()) {
        // The player did pick; dropping it without a word reads as a dead
        // button (review S2-4).
        m_message = [] { return tr("采集服务未连接，记录对象没有改变。连接恢复后请重新选择。"); };
        emit changed();
        return;
    }
    m_busy = true;
    m_message = [] { return tr("正在切换记录对象…"); };
    emit changed();
    const auto generation = m_generation;
    const int processId = choice.value(QStringLiteral("process_id")).toInt();
    const QString token = choice.value(QStringLiteral("selection_token")).toString();
    BackendReply *reply = m_backend->selectGameProcess(processId, token);
    const QPointer<BackendReply> guard(reply);
    reply->whenDone(this,
        [this, guard, generation, processId, token](bool ok, const QVariantMap &capture, const QString &, const QString &error) {
        if (generation != m_generation) return;
        m_busy = false;
        if (ok) {
            m_confirmedPid = processId;
            m_confirmedToken = token;
            m_confirmedAt.start();
            observe(capture);
        }
        m_message = ok ? LiveText([] { return tr("已锁定所选游戏。切换前台窗口不会改变记录对象。"); })
                       : collector_text::replyErrorText(guard, error);
        if (ok) emit selected(capture);
        emit changed();
    });
}
}
