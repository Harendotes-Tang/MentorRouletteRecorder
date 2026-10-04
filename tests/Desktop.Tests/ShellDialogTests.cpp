// The shell's own dialogs and headers, in the shipping Main.qml.
//
// Main.qml owns the 原因 dialog (软删除 / 恢复 / 撤销修正 / 确认复核), the first-run
// baseline guide and the wiring between the record wizard and AppController. All
// three answer to replies that carry little identity - a kind and a run id - so
// these tests drive the real window on a scripted backend whose replies the test
// releases, and check that each dialog acts only on its own.
//
// Also here, because they need the whole shell rather than one page: the 总览 date
// in a tray app that stays up for days, the 职业占比 donut after a style switch and
// the 关于 tab's Oodle facts.

#include "TestCollectorGuard.h"
#include "AppController.h"
#include "AppSettings.h"
#include "Formatters.h"
#include "IBackend.h"
#include "JobCatalog.h"
#include "RoleCatalog.h"
#include "RunFormValidator.h"

#include <QDateTime>
#include <QDir>
#include <QGuiApplication>
#include <QJSValue>
#include <QJsonArray>
#include <QJsonObject>
#include <QPointer>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QScopeGuard>
#include <QSignalSpy>
#include <QStandardPaths>
#include <QTest>
#include <QTimer>
#include <QUuid>

#include <functional>
#include <memory>

namespace {

/// Reads finish on the next turn of the event loop; the message types in
/// `holdTypes` wait until the test settles them, so "late" is reproducible.
class ShellBackend final : public mr::IBackend
{
public:
    struct Call {
        QString type;
        QJsonObject payload;
        QString requestId;
        QPointer<mr::BackendReply> reply;
    };

    QString backendName() const override { return QStringLiteral("mock"); }
    bool isConnected() const override { return connected; }

    /// Drops or restores the pipe the way IpcClient reports it.
    void setConnected(bool value)
    {
        connected = value;
        Q_EMIT connectionChanged();
    }

    mr::BackendReply *request(const QString &type, const QJsonObject &payload = {}) override
    {
        return requestWithId(type, payload, QString());
    }

    mr::BackendReply *requestWithId(const QString &type, const QJsonObject &payload,
                                    const QString &requestId) override
    {
        auto *reply = new mr::BackendReply(QString::number(++serial), type, this);
        // As IpcBackend does: the caller's id, or a fresh one.
        const QString id = requestId.isEmpty() ? QUuid::createUuid().toString(QUuid::WithoutBraces)
                                               : requestId;
        calls.append({type, payload, id, reply});
        if (unsentTypes.contains(type)) {
            // As IpcClient does with a pipe that is down: refused inside the call,
            // before anything is written.
            reply->failUnsent(QStringLiteral("ERR_INTERNAL"), QString::fromUtf8("Collector 未连接。"));
            return reply;
        }
        if (holdTypes.contains(type))
            return reply;
        const QJsonObject answer = answers.value(type);
        QTimer::singleShot(0, reply, [reply, answer] { reply->succeed(answer); });
        return reply;
    }

    QList<Call> callsOf(const QString &type) const
    {
        QList<Call> out;
        for (const Call &call : calls) {
            if (call.type == type)
                out.append(call);
        }
        return out;
    }

    /// The oldest unanswered call of \a type.
    mr::BackendReply *held(const QString &type) const
    {
        for (const Call &call : calls) {
            if (call.type == type && call.reply && !call.reply->isFinished())
                return call.reply;
        }
        return nullptr;
    }

    QStringList holdTypes;
    /// Message types refused the way IpcClient refuses a request it never sent.
    QStringList unsentTypes;
    QHash<QString, QJsonObject> answers;
    QList<Call> calls;
    int serial = 0;
    bool connected = true;
};

QQuickItem *findItem(QQuickItem *from, const QString &name)
{
    if (!from)
        return nullptr;
    if (from->objectName() == name)
        return from;
    for (QQuickItem *child : from->childItems()) {
        if (QQuickItem *match = findItem(child, name))
            return match;
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
    for (QQuickItem *child : item->childItems())
        collectVisibleText(child, texts);
}

QVariantMap asMap(const QVariant &value)
{
    return value.canConvert<QJSValue>() ? value.value<QJSValue>().toVariant().toMap()
                                        : value.toMap();
}

QJsonObject dashboard(int goal, int baseline)
{
    return {{QStringLiteral("goal_count"), goal},
            {QStringLiteral("baseline_completed_count"), baseline},
            {QStringLiteral("achievement_progress"), baseline},
            {QStringLiteral("remaining"), qMax(0, goal - baseline)}};
}

QVariantMap selectedRun(const QString &id)
{
    return {{QStringLiteral("run_id"), id},
            {QStringLiteral("revision"), 3},
            {QStringLiteral("duty_name"), QString::fromUtf8("水晶塔")},
            {QStringLiteral("result"), QStringLiteral("COMPLETED")},
            {QStringLiteral("pending_review"), true}};
}

/// The shipping window, onboarding suppressed, on a scripted backend.
struct Shell {
    mr::AppSettings settings;
    ShellBackend backend;
    std::unique_ptr<mr::AppController> controller;
    mr::Formatters formatters;
    mr::JobCatalog jobs;
    mr::RoleCatalog roles;
    mr::RunFormValidator validator;
    std::unique_ptr<QQmlEngine> engine;
    std::unique_ptr<QObject> root;
    QString errors;

    ~Shell()
    {
        root.reset();
        engine.reset();
        controller.reset();
    }

    bool create()
    {
        if (!backend.answers.contains(QStringLiteral("GetDashboardStats")))
            backend.answers.insert(QStringLiteral("GetDashboardStats"), dashboard(2000, 0));
        controller = std::make_unique<mr::AppController>(&backend, nullptr);
        engine = std::make_unique<QQmlEngine>();
        QQmlContext *context = engine->rootContext();
        context->setContextProperty(QStringLiteral("App"), controller.get());
        context->setContextProperty(QStringLiteral("Fmt"), &formatters);
        context->setContextProperty(QStringLiteral("Jobs"), &jobs);
        context->setContextProperty(QStringLiteral("Roles"), &roles);
        context->setContextProperty(QStringLiteral("RunForm"), &validator);
        context->setContextProperty(QStringLiteral("Settings"), &settings);
        context->setContextProperty(QStringLiteral("SuppressOnboarding"), true);
        context->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QQmlComponent component(engine.get(), QUrl::fromLocalFile(
            QString::fromUtf8(MR_DESKTOP_QML_DIR) + QStringLiteral("/Main.qml")));
        root.reset(component.create());
        for (const auto &error : component.errors())
            errors += error.toString() + QLatin1Char('\n');
        return root != nullptr;
    }

    QQuickWindow *window() const { return qobject_cast<QQuickWindow *>(root.get()); }
    QObject *named(const QString &name) const { return root->findChild<QObject *>(name); }
    /// Popups and Repeater delegates live in the visual tree only; the popups'
    /// overlay is a child of the window's root item.
    QQuickItem *item(const QString &name) const { return findItem(window()->contentItem(), name); }
    /// The visible item whose `text` is \a text (a button the shell names only in words).
    QQuickItem *itemWithText(const QString &text) const
    {
        std::function<QQuickItem *(QQuickItem *)> walk = [&](QQuickItem *at) -> QQuickItem * {
            if (!at || !at->isVisible())
                return nullptr;
            if (at->property("text").toString() == text)
                return at;
            for (QQuickItem *child : at->childItems()) {
                if (QQuickItem *found = walk(child))
                    return found;
            }
            return nullptr;
        };
        return walk(window()->contentItem());
    }
    bool call(const char *function, const QVariant &argument = QVariant())
    {
        if (!argument.isValid())
            return QMetaObject::invokeMethod(root.get(), function);
        return QMetaObject::invokeMethod(root.get(), function, Q_ARG(QVariant, argument));
    }
    bool click(const QString &name) const
    {
        if (!QTest::qWaitForWindowExposed(window()))
            return false;
        // Let a popup that just opened finish its layout first.
        QTest::qWait(50);
        QQuickItem *button = item(name);
        if (!button || !button->isVisible() || !button->isEnabled())
            return false;
        const QPointF centre = button->mapToScene(QPointF(button->width() / 2, button->height() / 2));
        QTest::mouseClick(window(), Qt::LeftButton, Qt::NoModifier, centre.toPoint());
        return true;
    }
    QStringList visibleTexts() const
    {
        QStringList texts;
        collectVisibleText(window()->contentItem(), texts);
        return texts;
    }
};

/// One refusal, as the place that explains it receives it.
struct Refusal {
    const char *place;
    const char *code;
    const char *sentence;
};

/// Every place that explains a refusal, and the refusal each one gets below: the Collector's
/// sentence, or the form's own check.
const Refusal kRefusals[] = {
    {"reason", "ERR_REVISION_CONFLICT", "该记录已被修改，请刷新后重试。"},
    {"wizardCheck", "ERR_REASON_REQUIRED", "必须填写新增原因，请求已拒绝。"},
    {"wizard", "ERR_TIME_ORDER", "匹配时间不能晚于进入副本的时间。"},
    {"guide", "ERR_BAD_REQUEST", "基数必须是大于等于 0 的整数。"},
    {"cardCheck", "ERR_REASON_REQUIRED", "修改成就进度前必须填写原因。"},
    {"card", "ERR_DB_BUSY", "本地数据库正忙，请稍后重试。"},
    {"result", "ERR_NOT_FOUND", "找不到该记录，请刷新列表后重试。"},
    {"note", "ERR_DB_BUSY", "本地数据库正忙，笔记没有保存。"},
};

QString refusalSentence(const char *place)
{
    for (const Refusal &refusal : kRefusals) {
        if (qstrcmp(refusal.place, place) == 0)
            return QString::fromUtf8(refusal.sentence);
    }
    return {};
}

/// Refuses the unanswered request of \a type the way \a place expects; false when none is out.
bool refuse(ShellBackend &backend, const QString &type, const char *place)
{
    mr::BackendReply *reply = backend.held(type);
    for (const Refusal &refusal : kRefusals) {
        if (reply && qstrcmp(refusal.place, place) == 0) {
            reply->fail(QString::fromLatin1(refusal.code), QString::fromUtf8(refusal.sentence));
            return true;
        }
    }
    return false;
}

/// Refuses each place of kRefusals once on the shipping window - the 原因 dialog, the
/// record wizard (its own check, then the Collector), the first-run guide, the 成就 card
/// (its own check, then the Collector) and 本次导随结果 (the result, then the note) - and
/// collects what each one shows.
void refuseEverywhere(bool maintainer, QHash<QString, QString> &shown)
{
    Shell shell;
    shell.backend.holdTypes << QStringLiteral("SoftDeleteRun") << QStringLiteral("CreateManualRun")
                            << QStringLiteral("UpdateAchievementBaseline") << QStringLiteral("CorrectRun")
                            << QStringLiteral("SetRunReflection");
    QVERIFY2(shell.create(), qPrintable(shell.errors));
    shell.controller->setMaintainerToolsVisible(maintainer);
    QTRY_VERIFY(shell.controller->achievementSettingsLoaded());

    // 原因: a 软删除 the Collector refuses.
    shell.controller->selectRun(selectedRun(QStringLiteral("run-a")));
    QVERIFY(shell.call("openReasonDialog", QStringLiteral("delete")));
    auto *reason = shell.named(QStringLiteral("reasonDialog"));
    QVERIFY(reason);
    QTRY_VERIFY(reason->property("visible").toBool());
    shell.item(QStringLiteral("reasonField"))->setProperty("text", QString::fromUtf8("重复记录"));
    QVERIFY(shell.click(QStringLiteral("reasonConfirmButton")));
    QVERIFY(refuse(shell.backend, QStringLiteral("SoftDeleteRun"), "reason"));
    QTRY_VERIFY(!shell.root->property("reasonSubmitting").toBool());
    shown.insert(QStringLiteral("reason"), shell.root->property("reasonDialogError").toString());
    QVERIFY(QMetaObject::invokeMethod(reason, "close"));
    QTRY_VERIFY(!reason->property("visible").toBool());

    // 新增遗漏记录: refused by its own check, then by the Collector.
    auto *wizard = shell.named(QStringLiteral("editRunDialog"));
    QVERIFY(wizard);
    QVERIFY(shell.call("openCreateDialog"));
    QTRY_VERIFY(wizard->property("visible").toBool());
    wizard->setProperty("matchedTime", QStringLiteral("20:00:00"));
    wizard->setProperty("enteredTime", QStringLiteral("20:01:00"));
    wizard->setProperty("endedTime", QStringLiteral("20:20:00"));
    wizard->setProperty("reasonText", QString());
    QVERIFY(QMetaObject::invokeMethod(wizard, "submit"));
    QCOMPARE(wizard->property("errorCode").toString(), QStringLiteral("ERR_REASON_REQUIRED"));
    shown.insert(QStringLiteral("wizardCheck"), wizard->property("errorText").toString());
    wizard->setProperty("reasonText", QString::fromUtf8("补录"));
    QVERIFY(QMetaObject::invokeMethod(wizard, "submit"));
    QCOMPARE(shell.backend.callsOf(QStringLiteral("CreateManualRun")).size(), 1);
    QVERIFY(refuse(shell.backend, QStringLiteral("CreateManualRun"), "wizard"));
    QTRY_VERIFY(!wizard->property("submitting").toBool());
    shown.insert(QStringLiteral("wizard"), wizard->property("errorText").toString());
    QVERIFY(QMetaObject::invokeMethod(wizard, "close"));
    QTRY_VERIFY(!wizard->property("visible").toBool());

    // The first-run guide, reopened: its save is refused.
    auto *guide = shell.named(QStringLiteral("baselineDialog"));
    QVERIFY(guide);
    QVERIFY(QMetaObject::invokeMethod(guide, "openDialog", Q_ARG(QVariant, true)));
    QTRY_VERIFY(guide->property("visible").toBool());
    QVERIFY(shell.click(QStringLiteral("baselineSaveButton")));
    QVERIFY(refuse(shell.backend, QStringLiteral("UpdateAchievementBaseline"), "guide"));
    QTRY_VERIFY(!guide->property("saving").toBool());
    shown.insert(QStringLiteral("guide"), guide->property("errorText").toString());
    QVERIFY(shell.click(QStringLiteral("baselineCancelButton")));
    QTRY_VERIFY(!guide->property("visible").toBool());

    // 设置 · 成就: refused by its own check (no reason), then by the Collector.
    shell.controller->navigate(5);
    auto *goalTab = shell.item(QStringLiteral("settingsTab_goal"));
    QVERIFY(goalTab);
    QVERIFY(QMetaObject::invokeMethod(goalTab, "clicked"));
    QQuickItem *line = nullptr;
    QTRY_VERIFY((line = shell.item(QStringLiteral("baselineErrorText"))));
    QVERIFY(shell.click(QStringLiteral("saveAchievementButton")));
    QCOMPARE(shell.backend.callsOf(QStringLiteral("UpdateAchievementBaseline")).size(), 1);
    shown.insert(QStringLiteral("cardCheck"), line->property("text").toString());
    shell.item(QStringLiteral("baselineReasonField"))->setProperty("text", QString::fromUtf8("核对"));
    QVERIFY(shell.click(QStringLiteral("saveAchievementButton")));
    QVERIFY(refuse(shell.backend, QStringLiteral("UpdateAchievementBaseline"), "card"));
    QTRY_VERIFY(!shell.controller->baselineSaving());
    shown.insert(QStringLiteral("card"), line->property("text").toString());

    // 本次导随结果: the 通关 answer is refused, then the note.
    auto *result = shell.named(QStringLiteral("reflectionDialog"));
    QVERIFY(result);
    QVariantMap runB = selectedRun(QStringLiteral("run-b"));
    runB.insert(QStringLiteral("job_id"), 24);
    QVERIFY(QMetaObject::invokeMethod(result, "openForResult", Q_ARG(QVariant, runB)));
    QTRY_VERIFY(result->property("visible").toBool());
    QVERIFY(QMetaObject::invokeMethod(result, "resolveWith",
                                      Q_ARG(QVariant, QStringLiteral("COMPLETED")),
                                      Q_ARG(QVariant, QString::fromUtf8("用户确认通关"))));
    QTRY_COMPARE(shell.backend.callsOf(QStringLiteral("CorrectRun")).size(), 1);
    QVERIFY(refuse(shell.backend, QStringLiteral("CorrectRun"), "result"));
    QTRY_VERIFY(!result->property("resolving").toBool());
    shown.insert(QStringLiteral("result"), result->property("errorText").toString());
    QVERIFY(QMetaObject::invokeMethod(result, "close"));
    QTRY_VERIFY(!result->property("visible").toBool());

    QVERIFY(QMetaObject::invokeMethod(result, "openForRun", Q_ARG(QVariant, runB),
                                      Q_ARG(QVariant, QString())));
    QTRY_VERIFY(result->property("visible").toBool());
    QVERIFY(shell.click(QStringLiteral("saveReflectionButton")));
    QTRY_COMPARE(shell.backend.callsOf(QStringLiteral("SetRunReflection")).size(), 1);
    QVERIFY(refuse(shell.backend, QStringLiteral("SetRunReflection"), "note"));
    QTRY_VERIFY(!result->property("submitting").toBool());
    shown.insert(QStringLiteral("note"), result->property("errorText").toString());
}

} // namespace

class ShellDialogTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void initTestCase()
    {
        const QString qmlRoot = QString::fromUtf8(MR_DESKTOP_QML_DIR);
        QVERIFY(QDir(qmlRoot).exists());
        qmlRegisterSingletonType(QUrl::fromLocalFile(qmlRoot + QStringLiteral("/Theme.qml")),
                                 "MentorRecorder", 1, 0, "Theme");
        for (const auto &directory : {QStringLiteral("/charts"), QStringLiteral("/components"),
                                      QStringLiteral("/dialogs"), QStringLiteral("/pages")}) {
            for (const auto &file :
                 QDir(qmlRoot + directory).entryList({QStringLiteral("*.qml")}, QDir::Files)) {
                const QByteArray name = file.chopped(4).toUtf8();
                qmlRegisterType(QUrl::fromLocalFile(qmlRoot + directory + QLatin1Char('/') + file),
                                "MentorRecorder", 1, 0, name.constData());
            }
        }
    }

    // 审查 OK-6 / OL-5：原因对话框此前认领任何一条修改回应——别处（本次导随结果）
    // 的「通关」成功会把它关掉、丢掉写了一半的原因；别处的失败写进它的错误栏；请求
    // 在途时 Esc 与「取消」都能关掉它，迟到的回应随后落在重开的窗口上。
    void aReasonDialogActsOnlyOnTheReplyToItsOwnRequest()
    {
        Shell shell;
        shell.backend.holdTypes << QStringLiteral("SoftDeleteRun");
        QVERIFY2(shell.create(), qPrintable(shell.errors));
        shell.controller->selectRun(selectedRun(QStringLiteral("run-a")));
        QVERIFY(shell.controller->hasSelection());

        QVERIFY(shell.call("openReasonDialog", QStringLiteral("delete")));
        auto *dialog = shell.named(QStringLiteral("reasonDialog"));
        QVERIFY(dialog);
        QTRY_VERIFY(dialog->property("visible").toBool());
        auto *field = shell.item(QStringLiteral("reasonField"));
        QVERIFY(field);
        field->setProperty("text", QString::fromUtf8("重复记录"));

        // The user is still typing. A 通关 answered in the result window and a refusal
        // from somewhere else must leave this window, its text and its banner alone.
        Q_EMIT shell.controller->mutationSucceeded(QStringLiteral("review"), QStringLiteral("run-b"),
                                                   2, QStringLiteral("audit-b"));
        Q_EMIT shell.controller->mutationFailed(QStringLiteral("ERR_REVISION_CONFLICT"),
                                                QString::fromUtf8("别处的拒绝"),
                                                QStringLiteral("review"), QStringLiteral("run-b"));
        QVERIFY(dialog->property("visible").toBool());
        QCOMPARE(field->property("text").toString(), QString::fromUtf8("重复记录"));
        QVERIFY(shell.root->property("reasonDialogError").toString().isEmpty());

        const int closeOnEscape = dialog->property("closePolicy").toInt();
        QVERIFY(shell.click(QStringLiteral("reasonConfirmButton")));
        QCOMPARE(shell.backend.callsOf(QStringLiteral("SoftDeleteRun")).size(), 1);
        QVERIFY(shell.root->property("reasonSubmitting").toBool());

        // In flight: neither Esc nor 取消 closes it.
        QVERIFY(dialog->property("closePolicy").toInt() != closeOnEscape);
        QTest::keyClick(shell.window(), Qt::Key_Escape);
        QVERIFY(dialog->property("visible").toBool());
        QVERIFY(!shell.item(QStringLiteral("reasonCancelButton"))->isEnabled());

        // Another kind, or the same kind for another run, is still not this reply.
        Q_EMIT shell.controller->mutationSucceeded(QStringLiteral("review"), QStringLiteral("run-a"),
                                                   4, QStringLiteral("audit-r"));
        Q_EMIT shell.controller->mutationSucceeded(QStringLiteral("delete"), QStringLiteral("run-b"),
                                                   4, QStringLiteral("audit-d"));
        QVERIFY(dialog->property("visible").toBool());
        QVERIFY(shell.root->property("reasonSubmitting").toBool());

        // Its own reply closes it.
        auto *reply = shell.backend.held(QStringLiteral("SoftDeleteRun"));
        QVERIFY(reply);
        reply->succeed({{QStringLiteral("run_id"), QStringLiteral("run-a")},
                        {QStringLiteral("revision"), 4},
                        {QStringLiteral("audit_event_id"), QStringLiteral("audit-own")},
                        {QStringLiteral("run"), QJsonObject::fromVariantMap(selectedRun(QStringLiteral("run-a")))}});
        QTRY_VERIFY(!dialog->property("visible").toBool());
        QVERIFY(!shell.root->property("reasonSubmitting").toBool());
    }

    // DT3-X1：mutationFailed 既不说是哪一种请求、也不说是哪条记录。原因对话框与「本次
    // 导随结果」各有一个请求在途时，任何一条拒绝都会同时落到两个窗口里；设置里基数
    // 被拒，也会写进原因对话框。每个窗口只认领自己那一次请求的拒绝。
    void eachDialogTakesOnlyTheRefusalOfItsOwnRequest()
    {
        Shell shell;
        shell.backend.holdTypes << QStringLiteral("SoftDeleteRun") << QStringLiteral("CorrectRun")
                                << QStringLiteral("UpdateAchievementBaseline");
        QVERIFY2(shell.create(), qPrintable(shell.errors));
        shell.controller->selectRun(selectedRun(QStringLiteral("run-a")));

        // 原因: 软删除 run-a, in flight.
        QVERIFY(shell.call("openReasonDialog", QStringLiteral("delete")));
        auto *reason = shell.named(QStringLiteral("reasonDialog"));
        QVERIFY(reason);
        QTRY_VERIFY(reason->property("visible").toBool());
        shell.item(QStringLiteral("reasonField"))->setProperty("text", QString::fromUtf8("重复记录"));
        QVERIFY(shell.click(QStringLiteral("reasonConfirmButton")));
        QTRY_COMPARE(shell.backend.callsOf(QStringLiteral("SoftDeleteRun")).size(), 1);

        // 本次导随结果: 通关 for run-b, in flight. The 原因 window stays modal on top;
        // the question is driven through its own functions.
        auto *result = shell.named(QStringLiteral("reflectionDialog"));
        QVERIFY(result);
        QVariantMap runB = selectedRun(QStringLiteral("run-b"));
        runB.insert(QStringLiteral("job_id"), 24);
        QVERIFY(QMetaObject::invokeMethod(result, "openForResult", Q_ARG(QVariant, runB)));
        QTRY_VERIFY(result->property("visible").toBool());
        QVERIFY(QMetaObject::invokeMethod(result, "resolveWith",
                                          Q_ARG(QVariant, QStringLiteral("COMPLETED")),
                                          Q_ARG(QVariant, QString::fromUtf8("用户确认通关"))));
        QTRY_COMPARE(shell.backend.callsOf(QStringLiteral("CorrectRun")).size(), 1);
        QVERIFY(result->property("resolving").toBool());

        // A refusal from 设置 · 目标 is neither window's.
        shell.controller->updateAchievementBaseline(2000, 10, QString::fromUtf8("设置"));
        auto *baseline = shell.backend.held(QStringLiteral("UpdateAchievementBaseline"));
        QVERIFY(baseline);
        baseline->fail(QStringLiteral("ERR_BAD_REQUEST"), QString::fromUtf8("基数被拒"));
        QTest::qWait(50);
        QVERIFY(shell.root->property("reasonSubmitting").toBool());
        QVERIFY(shell.root->property("reasonDialogError").toString().isEmpty());
        QVERIFY(result->property("resolving").toBool());
        QVERIFY(result->property("errorText").toString().isEmpty());

        // The 原因 dialog's own refusal: shown there, and only there.
        shell.backend.held(QStringLiteral("SoftDeleteRun"))
            ->fail(QStringLiteral("ERR_REVISION_CONFLICT"), QString::fromUtf8("删除被拒"));
        QTRY_VERIFY(!shell.root->property("reasonSubmitting").toBool());
        QVERIFY(shell.root->property("reasonDialogError").toString().contains(QString::fromUtf8("删除被拒")));
        QVERIFY(result->property("resolving").toBool());
        QVERIFY(result->property("errorText").toString().isEmpty());

        // The result window's own refusal: shown there, and only there.
        shell.backend.held(QStringLiteral("CorrectRun"))
            ->fail(QStringLiteral("ERR_BAD_REQUEST"), QString::fromUtf8("结果被拒"));
        QTRY_VERIFY(!result->property("resolving").toBool());
        QVERIFY(result->property("errorText").toString().contains(QString::fromUtf8("结果被拒")));
        QVERIFY(!shell.root->property("reasonDialogError").toString().contains(QString::fromUtf8("结果被拒")));
    }

    // 审查 OL-1 / OK-7：引导窗的「保存并开始」把 goalCount 绑定写断了，之后在设置
    // 里改过目标，再从设置重开引导，预填的还是旧目标，保存就悄悄改回去；从设置重开
    // 时也没有「取消」，只能二选一，「从 0 开始」连基数一起清零。
    void theBaselineGuideFollowsTheCurrentGoalAndCanBeCancelledFromSettings()
    {
        Shell shell;
        QVERIFY2(shell.create(), qPrintable(shell.errors));
        auto *dialog = shell.named(QStringLiteral("baselineDialog"));
        QVERIFY(dialog);
        // 2000 is also the default before any answer: wait for the answer itself.
        QTRY_VERIFY(shell.controller->achievementSettingsLoaded());
        QCOMPARE(shell.controller->goalCount(), 2000);

        // First run: the guide is answered with the goal it showed.
        QVERIFY(QMetaObject::invokeMethod(dialog, "openDialog", Q_ARG(QVariant, QVariant())));
        QTRY_VERIFY(dialog->property("visible").toBool());
        QCOMPARE(dialog->property("goalText").toString(), QStringLiteral("2000"));
        // The first run has to be answered: no 取消 there.
        QVERIFY(!shell.item(QStringLiteral("baselineCancelButton"))->isVisible());
        dialog->setProperty("baselineText", QStringLiteral("120"));
        QVERIFY(shell.click(QStringLiteral("baselineSaveButton")));
        QTRY_VERIFY(!dialog->property("visible").toBool());
        auto updates = shell.backend.callsOf(QStringLiteral("UpdateAchievementBaseline"));
        QCOMPARE(updates.size(), 1);
        QCOMPARE(updates.last().payload.value(QStringLiteral("goal_count")).toInt(), 2000);
        QCOMPARE(updates.last().payload.value(QStringLiteral("baseline_completed_count")).toInt(), 120);

        // The goal is changed in 设置 · 目标 afterwards.
        shell.backend.answers.insert(QStringLiteral("GetDashboardStats"), dashboard(1000, 120));
        shell.controller->refreshDashboard();
        QTRY_COMPARE(shell.controller->goalCount(), 1000);

        // Reopened from settings: it shows the goal in force and can be left unchanged.
        shell.controller->navigate(5);
        auto *goalTab = shell.item(QStringLiteral("settingsTab_goal"));
        QVERIFY(goalTab);
        QVERIFY(QMetaObject::invokeMethod(goalTab, "clicked"));
        QQuickItem *reopen = nullptr;
        QTRY_VERIFY((reopen = shell.itemWithText(QString::fromUtf8("重新打开首次引导"))));
        QVERIFY(QMetaObject::invokeMethod(reopen, "clicked"));
        QTRY_VERIFY(dialog->property("visible").toBool());
        QCOMPARE(dialog->property("goalText").toString(), QStringLiteral("1000"));
        QCOMPARE(dialog->property("baselineText").toString(), QStringLiteral("120"));
        QVERIFY(shell.click(QStringLiteral("baselineCancelButton")));
        QTRY_VERIFY(!dialog->property("visible").toBool());
        QCOMPARE(shell.backend.callsOf(QStringLiteral("UpdateAchievementBaseline")).size(), 1);

        // Saved untouched from settings: the goal stays 1000, it is not reverted to 2000.
        QVERIFY(QMetaObject::invokeMethod(reopen, "clicked"));
        QTRY_VERIFY(dialog->property("visible").toBool());
        QVERIFY(shell.click(QStringLiteral("baselineSaveButton")));
        QTRY_VERIFY(!dialog->property("visible").toBool());
        updates = shell.backend.callsOf(QStringLiteral("UpdateAchievementBaseline"));
        QCOMPARE(updates.size(), 2);
        QCOMPARE(updates.last().payload.value(QStringLiteral("goal_count")).toInt(), 1000);
        QCOMPARE(updates.last().payload.value(QStringLiteral("baseline_completed_count")).toInt(), 120);
    }

    // CS7-D3：第一份统计读回之前，「设置 · 成就」与引导窗里的目标与基数只是默认的 2000 与 0。
    // 这时保存，只改目标也会把 0 当作新基数发出去，抹掉采集服务里已存的基数。读回之前
    // 不能保存，读回之后显示读到的值；连接断开后，在重新读回之前同样不能保存。
    void achievementSettingsCannotBeSavedBeforeTheyHaveBeenRead()
    {
        Shell shell;
        shell.backend.holdTypes << QStringLiteral("GetDashboardStats");
        QVERIFY2(shell.create(), qPrintable(shell.errors));
        shell.controller->navigate(5);
        auto *goalTab = shell.item(QStringLiteral("settingsTab_goal"));
        QVERIFY(goalTab);
        QVERIFY(QMetaObject::invokeMethod(goalTab, "clicked"));
        QQuickItem *save = nullptr;
        QTRY_VERIFY((save = shell.item(QStringLiteral("saveAchievementButton"))) && save->isVisible());
        auto *goal = shell.item(QStringLiteral("goalField"));
        auto *baseline = shell.item(QStringLiteral("baselineField"));
        auto *reason = shell.item(QStringLiteral("baselineReasonField"));
        QVERIFY(goal && baseline && reason);

        // Nothing read yet: the fields hold defaults, so a goal-only save must not go out.
        reason->setProperty("text", QString::fromUtf8("只改目标"));
        shell.click(QStringLiteral("saveAchievementButton"));
        // Nor does a direct call put the default on the wire.
        shell.controller->updateAchievementBaseline(1500, 0, QString::fromUtf8("只改目标"));
        QTest::qWait(50);
        QCOMPARE(shell.backend.callsOf(QStringLiteral("UpdateAchievementBaseline")).size(), 0);
        auto *waiting = shell.item(QStringLiteral("achievementSettingsWaiting"));
        QVERIFY(waiting);
        QVERIFY(waiting->isVisible());
        QVERIFY(!save->isEnabled());
        QVERIFY(!goal->isEnabled());
        QVERIFY(!baseline->isEnabled());

        // The guide reopened from settings waits as well; 从 0 开始 is a save too.
        auto *dialog = shell.named(QStringLiteral("baselineDialog"));
        QVERIFY(dialog);
        QQuickItem *reopen = nullptr;
        QTRY_VERIFY((reopen = shell.itemWithText(QString::fromUtf8("重新打开首次引导"))));
        QVERIFY(QMetaObject::invokeMethod(reopen, "clicked"));
        QTRY_VERIFY(dialog->property("visible").toBool());
        QVERIFY(!shell.click(QStringLiteral("baselineSaveButton")));
        QVERIFY(!shell.click(QStringLiteral("baselineSkipButton")));
        QVERIFY(shell.item(QStringLiteral("baselineWaitingText")));
        QVERIFY(shell.item(QStringLiteral("baselineWaitingText"))->isVisible());
        QVERIFY(shell.click(QStringLiteral("baselineCancelButton")));
        QTRY_VERIFY(!dialog->property("visible").toBool());
        QCOMPARE(shell.backend.callsOf(QStringLiteral("UpdateAchievementBaseline")).size(), 0);

        // The stored settings arrive: the fields show them, and 保存 sends them.
        shell.backend.held(QStringLiteral("GetDashboardStats"))->succeed(dashboard(1000, 640));
        QTRY_VERIFY(save->isEnabled());
        QVERIFY(!waiting->isVisible());
        QCOMPARE(goal->property("text").toString(), QStringLiteral("1000"));
        QCOMPARE(baseline->property("text").toString(), QStringLiteral("640"));
        QVERIFY(shell.click(QStringLiteral("saveAchievementButton")));
        auto updates = shell.backend.callsOf(QStringLiteral("UpdateAchievementBaseline"));
        QCOMPARE(updates.size(), 1);
        QCOMPARE(updates.last().payload.value(QStringLiteral("goal_count")).toInt(), 1000);
        QCOMPARE(updates.last().payload.value(QStringLiteral("baseline_completed_count")).toInt(), 640);
        updates.last().reply->succeed({{QStringLiteral("goal_count"), 1000},
                                       {QStringLiteral("baseline_completed_count"), 640}});
        QTRY_VERIFY(shell.backend.held(QStringLiteral("GetDashboardStats")));
        shell.backend.held(QStringLiteral("GetDashboardStats"))->succeed(dashboard(1000, 640));

        // The pipe drops: what is stored is unknown again until it has been re-read.
        shell.backend.setConnected(false);
        QTRY_VERIFY(!save->isEnabled());
        QVERIFY(waiting->isVisible());
        QVERIFY(!baseline->isEnabled());
        shell.backend.setConnected(true);
        QTRY_VERIFY(shell.backend.held(QStringLiteral("GetDashboardStats")));
        QTest::qWait(50);
        QVERIFY(!save->isEnabled());
        shell.backend.held(QStringLiteral("GetDashboardStats"))->succeed(dashboard(1200, 700));
        QTRY_VERIFY(save->isEnabled());
        QCOMPARE(goal->property("text").toString(), QStringLiteral("1200"));
        QCOMPARE(baseline->property("text").toString(), QStringLiteral("700"));
    }

    // CS7-D3：首次启动时引导窗在启动那一刻打开，通常早于第一份统计。读回之前不能保存，
    // 也不能把首次启动答成「从 0 开始」；连不上采集服务时，它也不能把整个窗口锁住。
    void theFirstRunGuideWaitsForTheStoredSettings()
    {
        Shell shell;
        shell.backend.holdTypes << QStringLiteral("GetDashboardStats");
        QVERIFY2(shell.create(), qPrintable(shell.errors));
        auto *dialog = shell.named(QStringLiteral("baselineDialog"));
        QVERIFY(dialog);
        QVERIFY(QMetaObject::invokeMethod(dialog, "openDialog", Q_ARG(QVariant, QVariant())));
        QTRY_VERIFY(dialog->property("visible").toBool());
        shell.click(QStringLiteral("baselineSaveButton"));
        shell.click(QStringLiteral("baselineSkipButton"));
        QTest::qWait(50);
        QCOMPARE(shell.backend.callsOf(QStringLiteral("UpdateAchievementBaseline")).size(), 0);
        QVERIFY(dialog->property("visible").toBool());
        QVERIFY(shell.item(QStringLiteral("baselineWaitingText")));
        QVERIFY(shell.item(QStringLiteral("baselineWaitingText"))->isVisible());

        // It can be left for later; nothing is saved and the guide comes back.
        auto *later = shell.item(QStringLiteral("baselineCancelButton"));
        QVERIFY(later);
        QCOMPARE(later->property("text").toString(), QString::fromUtf8("稍后填写"));
        QVERIFY(shell.click(QStringLiteral("baselineCancelButton")));
        QTRY_VERIFY(!dialog->property("visible").toBool());
        QCOMPARE(shell.backend.callsOf(QStringLiteral("UpdateAchievementBaseline")).size(), 0);

        // Opened again, it takes the stored settings when they arrive and is the
        // first-run guide once more: answered, not cancelled.
        QVERIFY(QMetaObject::invokeMethod(dialog, "openDialog", Q_ARG(QVariant, QVariant())));
        QTRY_VERIFY(dialog->property("visible").toBool());
        shell.backend.held(QStringLiteral("GetDashboardStats"))->succeed(dashboard(1000, 640));
        QTRY_VERIFY(shell.item(QStringLiteral("baselineSaveButton"))->isEnabled());
        QCOMPARE(dialog->property("baselineText").toString(), QStringLiteral("640"));
        QCOMPARE(dialog->property("goalText").toString(), QStringLiteral("1000"));
        QVERIFY(!shell.item(QStringLiteral("baselineWaitingText"))->isVisible());
        QVERIFY(!shell.item(QStringLiteral("baselineCancelButton"))->isVisible());
        QVERIFY(shell.click(QStringLiteral("baselineSaveButton")));
        const auto updates = shell.backend.callsOf(QStringLiteral("UpdateAchievementBaseline"));
        QCOMPARE(updates.size(), 1);
        QCOMPARE(updates.last().payload.value(QStringLiteral("goal_count")).toInt(), 1000);
        QCOMPARE(updates.last().payload.value(QStringLiteral("baseline_completed_count")).toInt(), 640);
    }

    // CS7-D2：引导窗总写「生效时间 今天 · 之前的自动记录不会重复计入」。只有填入一个
    // 与已存基数不同、且大于 0 的基数时才是这样：基数不变时沿用原来的生效时间，基数为
    // 0 时不论记录何时结束都计入进度。已存的生效时间没有消息带回来，不能编一个。
    void theBaselineGuideSaysWhatTheSaveDoesToTheEffectiveTime()
    {
        Shell shell;
        shell.backend.answers.insert(QStringLiteral("GetDashboardStats"), dashboard(2000, 640));
        QVERIFY2(shell.create(), qPrintable(shell.errors));
        QTRY_COMPARE(shell.controller->baselineCount(), 640);
        auto *dialog = shell.named(QStringLiteral("baselineDialog"));
        QVERIFY(dialog);
        QVERIFY(QMetaObject::invokeMethod(dialog, "openDialog", Q_ARG(QVariant, true)));
        QTRY_VERIFY(dialog->property("visible").toBool());
        auto *line = shell.item(QStringLiteral("baselineEffectiveText"));
        QVERIFY(line);
        const QString today = mr::Formatters::localDate(
            QDateTime::currentDateTimeUtc().toString(Qt::ISODateWithMs));

        // The stored baseline, unchanged: its time stays, and today is not named.
        QTRY_VERIFY(line->isVisible());
        QString text = line->property("text").toString();
        QVERIFY2(!text.contains(today), qPrintable(text));
        QVERIFY2(text.contains(QString::fromUtf8("沿用原有的生效时间")), qPrintable(text));

        // A new baseline above 0 takes effect now and already holds what ended before.
        dialog->setProperty("baselineText", QStringLiteral("700"));
        text = line->property("text").toString();
        QVERIFY2(text.contains(today), qPrintable(text));
        QVERIFY2(text.contains(QString::fromUtf8("不会重复计入")), qPrintable(text));

        // 0 holds nothing, so no record is left out, whenever it ended.
        dialog->setProperty("baselineText", QStringLiteral("0"));
        text = line->property("text").toString();
        QVERIFY2(!text.contains(today), qPrintable(text));
        QVERIFY2(text.contains(QString::fromUtf8("不论何时结束")), qPrintable(text));
    }

    // DT6-X1：引导窗按下「保存并开始」，请求刚发出就把首次启动记为完成并关窗。保存若
    // 失败（读完之后连接断开，或采集服务拒绝），首次启动照样算完成，填好的基数只剩一条
    // 提示。引导只在自己那次保存成功后结束：在途时 Esc、点窗外都关不掉它；失败时留着
    // 填好的内容并说明原因；别处的拒绝不算它的回答。
    void theBaselineGuideFinishesOnlyWhenItsOwnSaveSucceeds()
    {
        Shell shell;
        shell.backend.holdTypes << QStringLiteral("UpdateAchievementBaseline");
        QVERIFY2(shell.create(), qPrintable(shell.errors));
        QTRY_VERIFY(shell.controller->achievementSettingsLoaded());
        auto *dialog = shell.named(QStringLiteral("baselineDialog"));
        QVERIFY(dialog);
        QSignalSpy completed(shell.controller.get(), &mr::AppController::firstRunChanged);

        QVERIFY(QMetaObject::invokeMethod(dialog, "openDialog", Q_ARG(QVariant, QVariant())));
        QTRY_VERIFY(dialog->property("visible").toBool());
        dialog->setProperty("baselineText", QStringLiteral("1374"));
        dialog->setProperty("goalText", QStringLiteral("1500"));
        QVERIFY(shell.click(QStringLiteral("baselineSaveButton")));
        QCOMPARE(shell.backend.callsOf(QStringLiteral("UpdateAchievementBaseline")).size(), 1);

        // In flight: the first run is not answered yet, and neither Esc nor a click
        // outside the window closes it; nothing can be sent twice.
        QTest::qWait(50);
        QVERIFY(dialog->property("visible").toBool());
        QCOMPARE(completed.count(), 0);
        QTest::keyClick(shell.window(), Qt::Key_Escape);
        QTest::mouseClick(shell.window(), Qt::LeftButton, Qt::NoModifier, QPoint(40, 760));
        QTest::qWait(50);
        QVERIFY(dialog->property("visible").toBool());
        QVERIFY(!shell.item(QStringLiteral("baselineSaveButton"))->isEnabled());
        QVERIFY(!shell.item(QStringLiteral("baselineSkipButton"))->isEnabled());

        // A refusal of another request is not its answer.
        Q_EMIT shell.controller->mutationFailed(QStringLiteral("ERR_REVISION_CONFLICT"),
                                                QString::fromUtf8("别处的拒绝"),
                                                QStringLiteral("review"), QStringLiteral("run-b"));
        QVERIFY(dialog->property("errorText").toString().isEmpty());
        QVERIFY(!shell.item(QStringLiteral("baselineSaveButton"))->isEnabled());

        // Its own save fails: it stays open with what was typed and says why.
        shell.backend.held(QStringLiteral("UpdateAchievementBaseline"))
            ->fail(QStringLiteral("ERR_INTERNAL"), QString::fromUtf8("与采集服务的连接已断开。"));
        QTRY_VERIFY(dialog->property("errorText").toString().contains(
            QString::fromUtf8("与采集服务的连接已断开。")));
        QVERIFY(dialog->property("visible").toBool());
        QCOMPARE(completed.count(), 0);
        QCOMPARE(dialog->property("baselineText").toString(), QStringLiteral("1374"));
        QCOMPARE(dialog->property("goalText").toString(), QStringLiteral("1500"));
        QVERIFY(shell.item(QStringLiteral("baselineSaveButton"))->isEnabled());

        // Sent again and accepted: now it closes and the first run is complete.
        QVERIFY(shell.click(QStringLiteral("baselineSaveButton")));
        auto updates = shell.backend.callsOf(QStringLiteral("UpdateAchievementBaseline"));
        QCOMPARE(updates.size(), 2);
        QCOMPARE(updates.last().payload.value(QStringLiteral("goal_count")).toInt(), 1500);
        QCOMPARE(updates.last().payload.value(QStringLiteral("baseline_completed_count")).toInt(), 1374);
        QTest::qWait(50);
        QVERIFY(dialog->property("visible").toBool());
        updates.last().reply->succeed({{QStringLiteral("goal_count"), 1500},
                                       {QStringLiteral("baseline_completed_count"), 1374}});
        QTRY_VERIFY(!dialog->property("visible").toBool());
        QCOMPARE(completed.count(), 1);

        // Reopened from settings it can be left with Esc or 取消 - but not while its save is out.
        QVERIFY(QMetaObject::invokeMethod(dialog, "openDialog", Q_ARG(QVariant, true)));
        QTRY_VERIFY(dialog->property("visible").toBool());
        const int closeOnEscape = dialog->property("closePolicy").toInt();
        QVERIFY(shell.click(QStringLiteral("baselineSaveButton")));
        QCOMPARE(shell.backend.callsOf(QStringLiteral("UpdateAchievementBaseline")).size(), 3);
        QVERIFY(dialog->property("closePolicy").toInt() != closeOnEscape);
        QVERIFY(!shell.item(QStringLiteral("baselineCancelButton"))->isEnabled());
        QTest::keyClick(shell.window(), Qt::Key_Escape);
        QTest::qWait(50);
        QVERIFY(dialog->property("visible").toBool());
        shell.backend.held(QStringLiteral("UpdateAchievementBaseline"))
            ->fail(QStringLiteral("ERR_BAD_REQUEST"), QString::fromUtf8("基数被拒"));
        QTRY_VERIFY(shell.item(QStringLiteral("baselineCancelButton"))->isEnabled());
        QCOMPARE(dialog->property("closePolicy").toInt(), closeOnEscape);
        QVERIFY(dialog->property("errorText").toString().contains(QString::fromUtf8("基数被拒")));
        QCOMPARE(completed.count(), 1);
        QVERIFY(shell.click(QStringLiteral("baselineCancelButton")));
        QTRY_VERIFY(!dialog->property("visible").toBool());
    }

    // 审查 V4-2：首次启动、已读到已存的目标与基数后，引导窗不提供「稍后填写」。保存一直
    // 失败（连接还在，采集服务却一再拒绝）时，模态的引导窗就再也关不掉，只能从托盘退出。
    // 保存失败过一次后，「稍后填写」重新出现：不保存就关闭，下次启动时再问。
    void aFirstRunGuideWhoseSaveFailedCanBeLeftForLater()
    {
        Shell shell;
        shell.backend.holdTypes << QStringLiteral("UpdateAchievementBaseline");
        QVERIFY2(shell.create(), qPrintable(shell.errors));
        QTRY_VERIFY(shell.controller->achievementSettingsLoaded());
        auto *dialog = shell.named(QStringLiteral("baselineDialog"));
        QVERIFY(dialog);
        QSignalSpy completed(shell.controller.get(), &mr::AppController::firstRunChanged);
        QVERIFY(QMetaObject::invokeMethod(dialog, "openDialog", Q_ARG(QVariant, QVariant())));
        QTRY_VERIFY(dialog->property("visible").toBool());
        auto *later = shell.item(QStringLiteral("baselineCancelButton"));
        QVERIFY(later);
        // The stored values are read and nothing has failed: a first run is answered.
        QVERIFY(!later->isVisible());

        dialog->setProperty("baselineText", QStringLiteral("1374"));
        QVERIFY(shell.click(QStringLiteral("baselineSaveButton")));
        shell.backend.held(QStringLiteral("UpdateAchievementBaseline"))
            ->fail(QStringLiteral("ERR_DB_BUSY"), QString::fromUtf8("本地数据库正忙。"));
        QTRY_VERIFY(dialog->property("errorText").toString().contains(QString::fromUtf8("本地数据库正忙。")));

        // Now there is a way out, and it stays while the answer is edited.
        QTRY_VERIFY(later->isVisible());
        QCOMPARE(later->property("text").toString(), QString::fromUtf8("稍后填写"));
        dialog->setProperty("baselineText", QStringLiteral("1375"));
        QVERIFY(dialog->property("errorText").toString().isEmpty());
        QVERIFY(later->isVisible());

        // It closes without saving, and the first run is not answered: the guide comes back.
        QVERIFY(shell.click(QStringLiteral("baselineCancelButton")));
        QTRY_VERIFY(!dialog->property("visible").toBool());
        QCOMPARE(shell.backend.callsOf(QStringLiteral("UpdateAchievementBaseline")).size(), 1);
        QCOMPARE(completed.count(), 0);

        // Asked again on the next start, it is a first run once more.
        QVERIFY(QMetaObject::invokeMethod(dialog, "openDialog", Q_ARG(QVariant, QVariant())));
        QTRY_VERIFY(dialog->property("visible").toBool());
        QVERIFY(!later->isVisible());
    }

    // DT6-X1：设置里的一次保存还在途时从设置重开引导，那一次的回答也是「基数」的回答。
    // 引导不在它旁边发出自己的保存，所以它等到的回答只会是自己的。
    void theBaselineGuideDoesNotSaveBesideAnotherBaselineSave()
    {
        Shell shell;
        shell.backend.holdTypes << QStringLiteral("UpdateAchievementBaseline");
        QVERIFY2(shell.create(), qPrintable(shell.errors));
        QTRY_VERIFY(shell.controller->achievementSettingsLoaded());
        shell.controller->updateAchievementBaseline(2000, 10, QString::fromUtf8("设置"));
        QVERIFY(shell.controller->baselineSaving());

        auto *dialog = shell.named(QStringLiteral("baselineDialog"));
        QVERIFY(dialog);
        QVERIFY(QMetaObject::invokeMethod(dialog, "openDialog", Q_ARG(QVariant, true)));
        QTRY_VERIFY(dialog->property("visible").toBool());
        QVERIFY(!shell.click(QStringLiteral("baselineSaveButton")));
        QVERIFY(!shell.click(QStringLiteral("baselineSkipButton")));
        QCOMPARE(shell.backend.callsOf(QStringLiteral("UpdateAchievementBaseline")).size(), 1);

        // That save's refusal is not the guide's: nothing is shown in it, and it may send now.
        shell.backend.held(QStringLiteral("UpdateAchievementBaseline"))
            ->fail(QStringLiteral("ERR_BAD_REQUEST"), QString::fromUtf8("基数被拒"));
        QTRY_VERIFY(!shell.controller->baselineSaving());
        QVERIFY(dialog->property("visible").toBool());
        QVERIFY(dialog->property("errorText").toString().isEmpty());
        QVERIFY(shell.click(QStringLiteral("baselineSaveButton")));
        QCOMPARE(shell.backend.callsOf(QStringLiteral("UpdateAchievementBaseline")).size(), 2);
        QVERIFY(dialog->property("saving").toBool());
    }

    // S33-4：设置 · 成就卡片的错误行显示每一次基数保存的拒绝，连从设置重开的首次引导自己那次
    // 保存的拒绝也显示：同一个原因同时出现在引导窗和它背后的卡片上。卡片只显示它自己那次
    // 保存的拒绝；引导窗照旧认领自己的拒绝。
    void theAchievementCardShowsOnlyTheRefusalOfItsOwnSave()
    {
        Shell shell;
        shell.backend.holdTypes << QStringLiteral("UpdateAchievementBaseline");
        QVERIFY2(shell.create(), qPrintable(shell.errors));
        QTRY_VERIFY(shell.controller->achievementSettingsLoaded());
        shell.controller->navigate(5);
        auto *goalTab = shell.item(QStringLiteral("settingsTab_goal"));
        QVERIFY(goalTab);
        QVERIFY(QMetaObject::invokeMethod(goalTab, "clicked"));
        QQuickItem *line = nullptr;
        QTRY_VERIFY((line = shell.item(QStringLiteral("baselineErrorText"))));
        auto *dialog = shell.named(QStringLiteral("baselineDialog"));
        QVERIFY(dialog);

        // The guide, reopened from the card, saves and is refused: its own refusal.
        QQuickItem *reopen = nullptr;
        QTRY_VERIFY((reopen = shell.itemWithText(QString::fromUtf8("重新打开首次引导"))));
        QVERIFY(QMetaObject::invokeMethod(reopen, "clicked"));
        QTRY_VERIFY(dialog->property("visible").toBool());
        QVERIFY(shell.click(QStringLiteral("baselineSaveButton")));
        shell.backend.held(QStringLiteral("UpdateAchievementBaseline"))
            ->fail(QStringLiteral("ERR_BAD_REQUEST"), QString::fromUtf8("引导的保存被拒"));
        QTRY_VERIFY(dialog->property("errorText").toString().contains(QString::fromUtf8("引导的保存被拒")));
        QVERIFY2(line->property("text").toString().isEmpty(), qPrintable(line->property("text").toString()));
        QVERIFY(!line->isVisible());
        QVERIFY(shell.click(QStringLiteral("baselineCancelButton")));
        QTRY_VERIFY(!dialog->property("visible").toBool());

        // A save the card did not send is not the card's either.
        shell.controller->updateAchievementBaseline(2000, 10, QString::fromUtf8("别处"));
        shell.backend.held(QStringLiteral("UpdateAchievementBaseline"))
            ->fail(QStringLiteral("ERR_BAD_REQUEST"), QString::fromUtf8("别处的保存被拒"));
        QTRY_VERIFY(!shell.controller->baselineSaving());
        QVERIFY(line->property("text").toString().isEmpty());

        // The card's own save is refused: shown on the card, and only there.
        shell.item(QStringLiteral("baselineReasonField"))->setProperty("text", QString::fromUtf8("核对"));
        QVERIFY(shell.click(QStringLiteral("saveAchievementButton")));
        shell.backend.held(QStringLiteral("UpdateAchievementBaseline"))
            ->fail(QStringLiteral("ERR_DB_BUSY"), QString::fromUtf8("卡片的保存被拒"));
        QTRY_VERIFY(line->property("text").toString().contains(QString::fromUtf8("卡片的保存被拒")));
        QVERIFY(line->isVisible());
        QVERIFY(!dialog->property("errorText").toString().contains(QString::fromUtf8("卡片的保存被拒")));

        // Accepted the next time: what the card waits for is answered, so a later guide's
        // refusal is the guide's again.
        QVERIFY(shell.click(QStringLiteral("saveAchievementButton")));
        shell.backend.held(QStringLiteral("UpdateAchievementBaseline"))
            ->succeed({{QStringLiteral("goal_count"), 2000}, {QStringLiteral("baseline_completed_count"), 0}});
        QTRY_VERIFY(!shell.controller->baselineSaving());
        QVERIFY(QMetaObject::invokeMethod(reopen, "clicked"));
        QTRY_VERIFY(dialog->property("visible").toBool());
        QVERIFY(shell.click(QStringLiteral("baselineSaveButton")));
        shell.backend.held(QStringLiteral("UpdateAchievementBaseline"))
            ->fail(QStringLiteral("ERR_BAD_REQUEST"), QString::fromUtf8("又一次引导的保存被拒"));
        QTRY_VERIFY(dialog->property("errorText").toString().contains(QString::fromUtf8("又一次引导的保存被拒")));
        QVERIFY(!line->property("text").toString().contains(QString::fromUtf8("又一次引导的保存被拒")));
    }

    // DT6-X2：「从 0 开始」发出的是已存的目标，引导窗里改过的目标被悄悄丢掉。它发出的
    // 应是窗里显示的目标，并像「保存并开始」一样先校验。
    void startingFromZeroSendsTheGoalTheGuideShows()
    {
        Shell shell;
        shell.backend.holdTypes << QStringLiteral("UpdateAchievementBaseline");
        QVERIFY2(shell.create(), qPrintable(shell.errors));
        QTRY_VERIFY(shell.controller->achievementSettingsLoaded());
        QCOMPARE(shell.controller->goalCount(), 2000);
        auto *dialog = shell.named(QStringLiteral("baselineDialog"));
        QVERIFY(dialog);
        QVERIFY(QMetaObject::invokeMethod(dialog, "openDialog", Q_ARG(QVariant, QVariant())));
        QTRY_VERIFY(dialog->property("visible").toBool());

        // A goal the save path refuses is refused here as well, and nothing goes out.
        dialog->setProperty("goalText", QString());
        QVERIFY(shell.click(QStringLiteral("baselineSkipButton")));
        QTest::qWait(50);
        QCOMPARE(shell.backend.callsOf(QStringLiteral("UpdateAchievementBaseline")).size(), 0);
        QCOMPARE(dialog->property("errorText").toString(), QString::fromUtf8("目标值必须是大于 0 的整数。"));

        dialog->setProperty("baselineText", QStringLiteral("640"));
        dialog->setProperty("goalText", QStringLiteral("1200"));
        QVERIFY(shell.click(QStringLiteral("baselineSkipButton")));
        const auto updates = shell.backend.callsOf(QStringLiteral("UpdateAchievementBaseline"));
        QCOMPARE(updates.size(), 1);
        QCOMPARE(updates.last().payload.value(QStringLiteral("goal_count")).toInt(), 1200);
        QCOMPARE(updates.last().payload.value(QStringLiteral("baseline_completed_count")).toInt(), 0);
    }

    // CS5-D1：采集服务拒绝撤销程序为未完结记录写下的修订（ERR_UNDO_NOT_ALLOWED）。桌面端
    // 没有可靠的依据预先判断，所以按钮照常提供；拒绝必须留在原因对话框里显示，且不
    // 留下「提交中」。
    void aRefusedUndoIsShownInTheReasonDialog()
    {
        Shell shell;
        shell.backend.holdTypes << QStringLiteral("UndoRevision");
        // The newest revision is the program's own: the restart that closed the run.
        QJsonArray chain;
        for (int revision = 1; revision <= 3; ++revision) {
            chain.append(QJsonObject{{QStringLiteral("revision"), revision},
                                     {QStringLiteral("actor"), revision == 2 ? QStringLiteral("USER")
                                                                             : QStringLiteral("SYSTEM")},
                                     {QStringLiteral("change_kind"), revision == 1
                                                                         ? QStringLiteral("CREATE_AUTO")
                                                                         : QStringLiteral("CORRECT")}});
        }
        shell.backend.answers.insert(
            QStringLiteral("GetRunRevisions"),
            QJsonObject{{QStringLiteral("items"), chain},
                        {QStringLiteral("page_info"), QJsonObject{{QStringLiteral("page"), 1},
                                                                  {QStringLiteral("page_size"), 50},
                                                                  {QStringLiteral("total"), 3}}}});
        QVERIFY2(shell.create(), qPrintable(shell.errors));
        shell.controller->selectRun(selectedRun(QStringLiteral("run-a")));
        // The button is offered: nothing the Desktop holds says this undo is refused.
        QTRY_VERIFY(shell.controller->selectedRunCanUndo());
        QVERIFY(QMetaObject::invokeMethod(shell.root.get(), "openReasonDialog",
                                          Q_ARG(QVariant, QStringLiteral("undo"))));
        auto *dialog = shell.named(QStringLiteral("reasonDialog"));
        QVERIFY(dialog);
        QTRY_VERIFY(dialog->property("visible").toBool());
        shell.item(QStringLiteral("reasonField"))->setProperty("text", QString::fromUtf8("判断有误"));
        QVERIFY(shell.click(QStringLiteral("reasonConfirmButton")));
        QCOMPARE(shell.backend.callsOf(QStringLiteral("UndoRevision")).size(), 1);
        QVERIFY(shell.root->property("reasonSubmitting").toBool());

        const QString refusal = QString::fromUtf8(
            "这条修订是程序为未完结的记录自动写下的。撤销它会让记录回到无法统计、也无法确认的状态，"
            "因此不能撤销；如果判断有误，请直接更正这条记录。");
        shell.backend.held(QStringLiteral("UndoRevision"))
            ->fail(QStringLiteral("ERR_UNDO_NOT_ALLOWED"), refusal);
        QTRY_VERIFY(!shell.root->property("reasonSubmitting").toBool());
        QVERIFY(dialog->property("visible").toBool());
        QVERIFY(shell.root->property("reasonDialogError").toString().contains(refusal));
        QVERIFY(shell.item(QStringLiteral("reasonConfirmButton"))->isEnabled());
        QVERIFY(shell.item(QStringLiteral("reasonCancelButton"))->isEnabled());
        QCOMPARE(shell.controller->toastMessage(), refusal);
    }

    // DT2-X1：新增记录超时而采集服务其实已写入时，再按「保存」若带着改过的内容，
    // 就换了新的 request_id，于是多出第二条记录。超时之后窗口只能原样重试（同一个
    // request_id，由采集服务的幂等回答）或关闭。
    void aSubmissionWithoutAnAnswerIsRetriedUnchangedUnderItsRequestId()
    {
        Shell shell;
        shell.backend.holdTypes << QStringLiteral("CreateManualRun");
        QVERIFY2(shell.create(), qPrintable(shell.errors));
        auto *dialog = shell.named(QStringLiteral("editRunDialog"));
        QVERIFY(dialog);
        QVERIFY(shell.call("openCreateDialog"));
        QTRY_VERIFY(dialog->property("visible").toBool());
        dialog->setProperty("matchedTime", QStringLiteral("20:00:00"));
        dialog->setProperty("enteredTime", QStringLiteral("20:01:00"));
        dialog->setProperty("endedTime", QStringLiteral("20:20:00"));
        dialog->setProperty("noteText", QString::fromUtf8("第一次填写"));
        QVERIFY(QMetaObject::invokeMethod(dialog, "submit"));
        QCOMPARE(shell.backend.callsOf(QStringLiteral("CreateManualRun")).size(), 1);

        // The client stops waiting (IpcClient's timeout); the Collector may have saved it.
        auto *first = shell.backend.held(QStringLiteral("CreateManualRun"));
        QVERIFY(first);
        first->fail(QStringLiteral("ERR_INTERNAL"), QString::fromUtf8("Collector 未在超时时间内响应。"));
        QTRY_VERIFY(!dialog->property("submitting").toBool());
        QVERIFY(dialog->property("visible").toBool());
        // DT-10：同一句话也用于采集服务自己答复了内部错误的情形，所以它说的是「发出了、结果不明」，
        // 而不是「没有回应」。
        const QString shown = dialog->property("errorText").toString();
        QVERIFY2(shown.contains(QString::fromUtf8(
                     "这次提交已经发出，但没有收到能说明是否保存成功的回应，记录可能已经保存。"
                     "为免重复，只能原样重试或关闭窗口；关闭后请先在历史记录中确认，再决定是否重新填写。")),
                 qPrintable(shown));
        QVERIFY2(!shown.contains(QString::fromUtf8("没有收到采集服务的回应")), qPrintable(shown));

        // The user changes the note and presses 保存 again.
        dialog->setProperty("noteText", QString::fromUtf8("改过的备注"));
        QVERIFY(QMetaObject::invokeMethod(dialog, "submit"));
        const auto creates = shell.backend.callsOf(QStringLiteral("CreateManualRun"));
        QCOMPARE(creates.size(), 2);
        // The same request, under the same id: the Collector answers it once.
        QCOMPARE(creates.at(1).requestId, creates.at(0).requestId);
        QCOMPARE(creates.at(1).payload, creates.at(0).payload);

        // The Collector had saved it: its idempotent answer closes the wizard.
        creates.at(1).reply->succeed({{QStringLiteral("run_id"), QStringLiteral("created-run")},
                                      {QStringLiteral("revision"), 1},
                                      {QStringLiteral("audit_event_id"), QStringLiteral("audit-1")}});
        QTRY_VERIFY(!dialog->property("visible").toBool());

        // A fresh wizard is a fresh form again.
        QVERIFY(shell.call("openCreateDialog"));
        QTRY_VERIFY(dialog->property("visible").toBool());
        QVERIFY(!dialog->property("awaitingRetry").toBool());
    }

    // 审查 V4-1：管道没连上时，「保存」根本没有发出去（IpcClient 当场以 ERR_INTERNAL 拒绝），
    // 向导却和超时一样冻结表单，并说记录「可能已经保存」。没发出的提交不可能已经保存：表单
    // 照常可改，提示如实说明；之前那次已发出却没有回应的提交，仍然让表单保持冻结。
    void aSubmissionThatNeverLeftTheDesktopLeavesTheFormEditable()
    {
        Shell shell;
        shell.backend.holdTypes << QStringLiteral("CreateManualRun");
        shell.backend.unsentTypes << QStringLiteral("CreateManualRun");
        QVERIFY2(shell.create(), qPrintable(shell.errors));
        auto *dialog = shell.named(QStringLiteral("editRunDialog"));
        QVERIFY(dialog);
        QVERIFY(shell.call("openCreateDialog"));
        QTRY_VERIFY(dialog->property("visible").toBool());
        dialog->setProperty("matchedTime", QStringLiteral("20:00:00"));
        dialog->setProperty("enteredTime", QStringLiteral("20:01:00"));
        dialog->setProperty("endedTime", QStringLiteral("20:20:00"));
        dialog->setProperty("noteText", QString::fromUtf8("第一次填写"));
        QVERIFY(QMetaObject::invokeMethod(dialog, "submit"));
        QCOMPARE(shell.backend.callsOf(QStringLiteral("CreateManualRun")).size(), 1);

        // Refused before it was written: nothing can have been saved, and the form stays a form.
        QVERIFY(!dialog->property("submitting").toBool());
        QVERIFY(!dialog->property("awaitingRetry").toBool());
        QString shown = dialog->property("errorText").toString();
        QVERIFY2(shown.contains(QString::fromUtf8("Collector 未连接。")), qPrintable(shown));
        QVERIFY2(shown.contains(QString::fromUtf8("这次提交没有发给采集服务，记录没有保存。")),
                 qPrintable(shown));
        QVERIFY2(!shown.contains(QString::fromUtf8("可能已经保存")), qPrintable(shown));

        // Edited and saved again, it goes out as what it now says.
        shell.backend.unsentTypes.clear();
        dialog->setProperty("noteText", QString::fromUtf8("改过的备注"));
        QVERIFY(QMetaObject::invokeMethod(dialog, "submit"));
        auto creates = shell.backend.callsOf(QStringLiteral("CreateManualRun"));
        QCOMPARE(creates.size(), 2);
        QCOMPARE(creates.at(1).payload.value(QStringLiteral("note")).toString(),
                 QString::fromUtf8("改过的备注"));

        // That one went out and got no answer: now it may have been saved, so the form freezes.
        creates.at(1).reply->fail(QStringLiteral("ERR_INTERNAL"),
                                  QString::fromUtf8("Collector 未在超时时间内响应。"));
        QTRY_VERIFY(dialog->property("awaitingRetry").toBool());

        // 原样重试 while the pipe is down again tells nothing about the earlier request: still frozen.
        shell.backend.unsentTypes << QStringLiteral("CreateManualRun");
        QVERIFY(QMetaObject::invokeMethod(dialog, "submit"));
        creates = shell.backend.callsOf(QStringLiteral("CreateManualRun"));
        QCOMPARE(creates.size(), 3);
        QCOMPARE(creates.at(2).requestId, creates.at(1).requestId);
        QVERIFY(!dialog->property("submitting").toBool());
        QVERIFY(dialog->property("awaitingRetry").toBool());
        shown = dialog->property("errorText").toString();
        QVERIFY2(shown.contains(QString::fromUtf8("可能已经保存")), qPrintable(shown));
        QVERIFY2(!shown.contains(QString::fromUtf8("记录没有保存")), qPrintable(shown));
    }

    // 审查 V4-1：修正走的是同一条路，没发出的修正同样不冻结表单。
    void aCorrectionThatNeverLeftTheDesktopLeavesTheFormEditable()
    {
        Shell shell;
        shell.backend.unsentTypes << QStringLiteral("CorrectRun");
        QVERIFY2(shell.create(), qPrintable(shell.errors));
        QVariantMap run = selectedRun(QStringLiteral("run-a"));
        run.insert(QStringLiteral("matched_at_utc"), QStringLiteral("2026-10-01T12:00:00.000Z"));
        run.insert(QStringLiteral("entered_at_utc"), QStringLiteral("2026-10-01T12:01:00.000Z"));
        run.insert(QStringLiteral("ended_at_utc"), QStringLiteral("2026-10-01T12:20:00.000Z"));
        shell.controller->selectRun(run);
        auto *dialog = shell.named(QStringLiteral("editRunDialog"));
        QVERIFY(dialog);
        QVERIFY(shell.call("openCorrectDialog"));
        QTRY_VERIFY(dialog->property("visible").toBool());
        dialog->setProperty("noteText", QString::fromUtf8("补一句备注"));
        QVERIFY(QMetaObject::invokeMethod(dialog, "submit"));
        QVERIFY2(shell.backend.callsOf(QStringLiteral("CorrectRun")).size() == 1,
                 qPrintable(dialog->property("errorText").toString()));

        QVERIFY(!dialog->property("submitting").toBool());
        QVERIFY(!dialog->property("awaitingRetry").toBool());
        const QString shown = dialog->property("errorText").toString();
        QVERIFY2(shown.contains(QString::fromUtf8("这次提交没有发给采集服务，记录没有保存。")),
                 qPrintable(shown));
        QVERIFY2(!shown.contains(QString::fromUtf8("可能已经保存")), qPrintable(shown));
    }

    // DT-10：拒绝的机器码（ERR_…）是给维护者看的。原因对话框、新增记录向导、首次引导、
    // 成就卡片与「本次导随结果」此前在那句话后面一律加上括号里的错误码，普通用户也看得到。
    // 维护者工具没有打开时，每一处只显示那句话。
    void aRefusalShowsAPlayerOnlyItsSentence()
    {
        QHash<QString, QString> shown;
        refuseEverywhere(false, shown);
        if (QTest::currentTestFailed())
            return;
        for (const Refusal &refusal : kRefusals) {
            QCOMPARE(shown.value(QString::fromLatin1(refusal.place)), refusalSentence(refusal.place));
        }
    }

    // DT-10：维护者工具打开时，同样每一处都在那句话之后用括号附上机器码。
    void aRefusalShowsAMaintainerItsCodeAfterTheSentence()
    {
        QHash<QString, QString> shown;
        refuseEverywhere(true, shown);
        if (QTest::currentTestFailed())
            return;
        for (const Refusal &refusal : kRefusals) {
            QCOMPARE(shown.value(QString::fromLatin1(refusal.place)),
                     refusalSentence(refusal.place) + QStringLiteral(" (")
                         + QString::fromLatin1(refusal.code) + QLatin1Char(')'));
        }
    }

    // DT-10：一句没有说明原因的拒绝不会把机器码当作说明交给普通用户。
    void aRefusalWithoutASentenceShowsNoCodeToAPlayer()
    {
        mr::AppSettings settings;
        ShellBackend backend;
        mr::AppController controller(&backend, &settings);
        QCOMPARE(controller.errorText(QString::fromUtf8("该记录已被修改，请刷新后重试。"),
                                      QStringLiteral("ERR_REVISION_CONFLICT")),
                 QString::fromUtf8("该记录已被修改，请刷新后重试。"));
        QCOMPARE(controller.errorText(QString(), QStringLiteral("ERR_INTERNAL")),
                 QString::fromUtf8("操作没有完成，原因不明。"));
        controller.setMaintainerToolsVisible(true);
        QCOMPARE(controller.errorText(QString(), QStringLiteral("ERR_INTERNAL")),
                 QString::fromUtf8("操作没有完成，原因不明。 (ERR_INTERNAL)"));
        QCOMPARE(controller.errorText(QString::fromUtf8("与采集服务的连接已断开。"), QString()),
                 QString::fromUtf8("与采集服务的连接已断开。"));
    }

    // DT-10：采集服务不认识读取捕获设置的请求时，设置页「校准」一栏说明开关不可用，此前句中
    // 总带着机器码（当前采集器不支持捕获设置（ERR_UNKNOWN_MESSAGE）……）。
    void aCollectorWithoutCaptureSettingsIsExplainedWithTheCodeForAMaintainerOnly()
    {
        for (const bool maintainer : {false, true}) {
            ShellBackend backend;
            backend.holdTypes << QStringLiteral("GetCaptureSettings");
            mr::AppController controller(&backend, nullptr);
            controller.setMaintainerToolsVisible(maintainer);
            controller.refreshCaptureSettings();
            QVERIFY(backend.held(QStringLiteral("GetCaptureSettings")));
            while (mr::BackendReply *reply = backend.held(QStringLiteral("GetCaptureSettings")))
                reply->fail(QStringLiteral("ERR_UNKNOWN_MESSAGE"), QStringLiteral("unknown message_type"));
            QTRY_VERIFY(!controller.captureSettingsError().isEmpty());
            QVERIFY(!controller.captureSettingsSupported());
            const QString sentence = QString::fromUtf8("当前采集器不支持捕获设置，以下开关不可用。");
            QCOMPARE(controller.captureSettingsError(),
                     maintainer ? sentence + QStringLiteral(" (ERR_UNKNOWN_MESSAGE)") : sentence);
        }
    }

    // 审查 OK-9：总览页头的日期只在启动时算一次，托盘里挂到第二天仍显示昨天。
    void theDashboardDateFollowsTheCalendar()
    {
        Shell shell;
        QVERIFY2(shell.create(), qPrintable(shell.errors));
        auto *header = shell.item(QStringLiteral("dashboardHeader"));
        QVERIFY(header);
        const QString today = mr::Formatters::dateWithWeekday(
            QDateTime::currentDateTimeUtc().toString(Qt::ISODateWithMs));
        QTRY_COMPARE(header->property("subtitle").toString(), today);

        // The page computed its date yesterday; the next refresh replaces it.
        auto *page = shell.named(QStringLiteral("dashboardPage"));
        QVERIFY(page);
        QVERIFY(page->setProperty("todayText", QString::fromUtf8("2000-01-01 周六")));
        QCOMPARE(header->property("subtitle").toString(), QString::fromUtf8("2000-01-01 周六"));
        auto *timer = shell.named(QStringLiteral("dashboardDateTimer"));
        QVERIFY(timer);
        QVERIFY(timer->property("running").toBool());
        QVERIFY(QMetaObject::invokeMethod(timer, "triggered"));
        QCOMPARE(header->property("subtitle").toString(), today);
    }

    // 审查 OK-9：职业占比的圆环只在深浅色切换时重画，换界面风格后仍是旧配色。
    void theJobDonutRepaintsWhenTheStyleChanges()
    {
        Shell shell;
        const QString before = shell.settings.uiStyle();
        const auto restore = qScopeGuard([&] { shell.settings.setUiStyle(before); });
        shell.settings.setUiStyle(QStringLiteral("classic"));
        QVERIFY2(shell.create(), qPrintable(shell.errors));
        shell.controller->navigate(3);
        auto *donut = shell.item(QStringLiteral("jobsDonut"));
        QVERIFY(donut);
        QTRY_VERIFY(donut->isVisible());
        QTest::qWait(200);
        QSignalSpy paints(donut, SIGNAL(paint(QRect)));
        QVERIFY(paints.isValid());

        shell.settings.setUiStyle(QStringLiteral("eorzea"));
        QTRY_VERIFY_WITH_TIMEOUT(paints.count() > 0, 2000);
    }

    // 审查 OK-8：关于页把 C# 枚举名 FfxivTcp 与决策编号 DEC-OODLE-01 直接给玩家看。
    void theAboutTabNamesTheOodleModeInWords()
    {
        Shell shell;
        shell.backend.answers.insert(QStringLiteral("GetStatus"),
                                     QJsonObject{{QStringLiteral("oodle_mode"), QStringLiteral("FfxivTcp")},
                                                 {QStringLiteral("reads_game_executable"), true}});
        QVERIFY2(shell.create(), qPrintable(shell.errors));
        QTRY_COMPARE(shell.controller->oodleMode(), QStringLiteral("FfxivTcp"));
        shell.controller->navigate(5);
        auto *tab = shell.item(QStringLiteral("settingsTab_about"));
        QVERIFY(tab);
        QVERIFY(QMetaObject::invokeMethod(tab, "clicked"));
        QTRY_VERIFY(shell.item(QStringLiteral("readsGameExecutableTile"))
                    && shell.item(QStringLiteral("readsGameExecutableTile"))->isVisible());
        const QString page = shell.visibleTexts().join(QLatin1Char('\n'));
        QVERIFY2(!page.contains(QStringLiteral("FfxivTcp")), qPrintable(page));
        QVERIFY2(!page.contains(QStringLiteral("DEC-OODLE")), qPrintable(page));
        QVERIFY2(page.contains(QString::fromUtf8("游戏自带的解压函数")), qPrintable(page));
    }
};

int main(int argc, char *argv[])
{
    // Never run against the user's own Collector, database or serve lease - see
    // TestCollectorGuard.h. Must precede any construction.
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication app(argc, argv);
    ShellDialogTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "ShellDialogTests.moc"
