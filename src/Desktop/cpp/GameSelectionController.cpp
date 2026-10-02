#include "GameSelectionController.h"
#include <QDateTime>

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
            m_message = tr("未选中游戏窗口，当前记录对象没有改变。可重试或从列表选择。");
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
        emit choicesChanged();
        emit changed();
    });
}

void GameSelectionController::observe(const QVariantMap &capture)
{
    QVariantList choices;
    m_required = capture.value(QStringLiteral("game_selection_required")).toBool();
    m_selectionMessage.clear();
    if (m_required) {
        const auto reason = capture.value(QStringLiteral("game_selection_reason")).toString();
        m_selectionMessage = reason == QLatin1String("EXITED")
            ? tr("所选游戏已退出，记录已暂停。单开时重新启动同一游戏会自动接续；无法确认时请重新选择游戏窗口。")
            : reason == QLatin1String("IDENTITY_UNAVAILABLE")
            ? tr("暂时无法确认游戏的启动时间，记录已暂停。请重新检测后选择游戏窗口。")
            : tr("检测到多个游戏客户端，请选择要记录的游戏窗口。");
    }
    m_currentLabel.clear();
    const int selectedPid = capture.value(QStringLiteral("ffxiv_process_id")).toInt();
    const auto processes = capture.value(QStringLiteral("game_processes")).toList();
    for (const auto &value : processes) {
        auto choice = value.toMap();
        const auto started = QDateTime::fromString(
            choice.value(QStringLiteral("started_at_utc")).toString(), Qt::ISODateWithMs);
        const QString label = started.isValid()
            ? tr("游戏 %1 · %2 启动").arg(choices.size() + 1).arg(started.toLocalTime().toString(QStringLiteral("HH:mm:ss")))
            : tr("游戏 %1 · 启动时间暂不可读").arg(choices.size() + 1);
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
    m_backend->getCaptureStatus()->whenDone(this,
        [this, generation](bool ok, const QVariantMap &capture, const QString &, const QString &error) {
        if (generation != m_generation) return;
        m_busy = false;
        if (!ok) m_message = error;
        else {
            observe(capture);
            m_pickChoices = m_choices;
            if (m_pickChoices.isEmpty()) m_message = tr("请先启动要记录的游戏。");
            else {
                m_ticks = 0;
                m_picker.start();
                m_message = tr("请在 15 秒内点击要记录的 FF14 窗口；选中后会锁定它。");
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
    if (!m_backend || !m_backend->isConnected()) return;
    m_busy = true;
    m_message = tr("正在切换记录对象…");
    emit changed();
    const auto generation = m_generation;
    m_backend->selectGameProcess(choice.value(QStringLiteral("process_id")).toInt(),
        choice.value(QStringLiteral("selection_token")).toString())->whenDone(this,
        [this, generation](bool ok, const QVariantMap &capture, const QString &, const QString &error) {
        if (generation != m_generation) return;
        m_busy = false;
        m_message = ok ? tr("已锁定所选游戏。切换前台窗口不会改变记录对象。") : error;
        if (ok) { observe(capture); emit selected(capture); }
        emit changed();
    });
}
}
