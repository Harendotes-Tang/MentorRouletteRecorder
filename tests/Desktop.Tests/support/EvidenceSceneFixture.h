// The scene the 采证 page suites share: the shipping EvidencePage over a mock that a test
// scripts (the round the Collector reports, the game's connections, refused markers), and
// the helpers to read and press what the page shows. Everything is in an anonymous
// namespace: each suite gets its own copy inside the shared test executable.
#pragma once

#include "AppController.h"
#include "AppSettings.h"
#include "EvidenceSubmissionController.h"
#include "Formatters.h"
#include "JobCatalog.h"
#include "MockBackend.h"

#include <QDateTime>
#include <QHash>
#include <QJsonObject>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickWindow>
#include <QSignalSpy>
#include <QTest>
#include <QTimer>

#include <algorithm>
#include <memory>

namespace {

[[maybe_unused]] QQuickItem *findItem(QQuickItem *root, const QString &name)
{
    if (!root) return nullptr;
    if (root->objectName() == name) return root;
    for (auto *child : root->childItems())
        if (auto *found = findItem(child, name)) return found;
    return nullptr;
}

/// The mock, with what a test scripts on top of it: the round the Collector reports
/// (GetCaptureValidationStatus), the game's TCP connections in GetStatus.capture.network_path,
/// and markers the Collector refuses.
class EvidenceBackend final : public mr::MockBackend
{
public:
    bool scripted = false;
    QJsonObject validation;
    QJsonObject networkPath;
    bool refuseMarkers = false;
    bool legacyCandidateRounds = false;
    bool dropPlayerStopReply = false;
    QPointer<mr::BackendReply> withheldStop;
    QJsonObject withheldStopPayload;
    QHash<QString, int> counts;

    bool isConnected() const override { return connected; }

    void applyServerCaptureSettings(const QJsonObject &payload)
    {
        mr::MockBackend::request(QStringLiteral("UpdateCaptureSettings"), payload);
    }

    void setMaintainerRound(bool enabled)
    {
        applyServerCaptureSettings({{QStringLiteral("candidate_validation_enabled"), enabled},
                                    {QStringLiteral("research_payload_opcodes"), QJsonArray()}});
    }

    void setConnected(bool value)
    {
        if (connected == value)
            return;
        connected = value;
        Q_EMIT connectionChanged();
    }

    mr::BackendReply *request(const QString &type, const QJsonObject &payload = {}) override
    {
        ++counts[type];
        if (!connected) {
            auto *reply = new mr::BackendReply(type, type, this);
            QTimer::singleShot(0, reply, [reply] {
                reply->failUnsent(QStringLiteral("ERR_INTERNAL"), QStringLiteral("disconnected"));
            });
            return reply;
        }
        if (scripted && type == QLatin1String("GetCaptureValidationStatus"))
            return later(type, true, validation);
        if (refuseMarkers && type == QLatin1String("AddCaptureValidationMarker"))
            return later(type, false, {});
        // A taken marker answers with the scripted round, never the mock's own.
        if (scripted && type == QLatin1String("AddCaptureValidationMarker"))
            return later(type, true, validation);
        mr::BackendReply *answer = mr::MockBackend::request(type, payload);
        if (type == QLatin1String("UpdateCaptureSettings") && dropPlayerStopReply
            && payload.contains(QStringLiteral("candidate_round_id"))
            && !payload.value(QStringLiteral("candidate_validation_enabled")).toBool(true)) {
            dropPlayerStopReply = false;
            auto *reply = new mr::BackendReply(type, type, this);
            answer->whenDone(reply, [this, reply](bool ok, const QVariantMap &settings,
                                                const QString &code, const QString &message) {
                if (!ok) {
                    reply->fail(code, message);
                    return;
                }
                withheldStop = reply;
                withheldStopPayload = QJsonObject::fromVariantMap(settings);
                setConnected(false);
            });
            return reply;
        }
        if (type == QLatin1String("GetCaptureSettings") && legacyCandidateRounds) {
            auto *reply = new mr::BackendReply(type, type, this);
            answer->whenDone(reply, [reply](bool ok, const QVariantMap &settings,
                                          const QString &code, const QString &message) {
                if (!ok) {
                    reply->fail(code, message);
                    return;
                }
                QJsonObject legacy = QJsonObject::fromVariantMap(settings);
                legacy.remove(QStringLiteral("candidate_round_id"));
                legacy.remove(QStringLiteral("candidate_round_profile_id"));
                reply->succeed(legacy);
            });
            return reply;
        }
        if (type != QLatin1String("GetStatus") || networkPath.isEmpty())
            return answer;
        // The same answer, with the game's connections as the Collector counts them.
        auto *reply = new mr::BackendReply(type, type, this);
        const QJsonObject path = networkPath;
        answer->whenDone(reply, [reply, path](bool ok, const QVariantMap &status, const QString &code,
                                              const QString &message) {
            if (!ok) {
                reply->fail(code, message);
                return;
            }
            QJsonObject withPath = QJsonObject::fromVariantMap(status);
            QJsonObject capture = withPath.value(QStringLiteral("capture")).toObject();
            capture.insert(QStringLiteral("network_path"), path);
            withPath.insert(QStringLiteral("capture"), capture);
            reply->succeed(withPath);
        });
        return reply;
    }

private:
    bool connected = true;

    mr::BackendReply *later(const QString &type, bool ok, const QJsonObject &payload)
    {
        auto *reply = new mr::BackendReply(type, type, this);
        QTimer::singleShot(0, reply, [reply, ok, payload] {
            if (ok)
                reply->succeed(payload);
            else
                reply->fail(QStringLiteral("ERR_BAD_REQUEST"), QString::fromUtf8("仅在验证录制期间可以添加标记。"));
        });
        return reply;
    }
};

/// One scripted $defs/CaptureValidationStatus. \a messages < 0 leaves the counts null
/// (nothing recorded yet); \a startedSecondsAgo < 0 leaves the start null.
[[maybe_unused]] QJsonObject roundStatus(const QString &state, const QString &session, int messages = -1,
                        int startedSecondsAgo = -1, const QString &trace = {})
{
    const bool active = state == QLatin1String("WAITING") || state == QLatin1String("RECORDING")
                        || state == QLatin1String("STOPPING");
    const bool failed = state == QLatin1String("FAILED");
    const QJsonValue null(QJsonValue::Null);
    const QDateTime now = QDateTime::currentDateTimeUtc();
    const auto count = [&](int value) { return messages < 0 ? null : QJsonValue(value); };
    const auto text = [&](const QString &value) { return value.isEmpty() ? null : QJsonValue(value); };
    return {{QStringLiteral("state"), state},
            {QStringLiteral("active"), active},
            {QStringLiteral("reason"), state == QLatin1String("WAITING") ? QStringLiteral("WAITING_GAME") : state},
            {QStringLiteral("message"), failed ? QString::fromUtf8("验证失败，文件可能不完整。详情见本机诊断日志。") : QString()},
            {QStringLiteral("session_id"), text(session)},
            {QStringLiteral("started_at_utc"), startedSecondsAgo < 0
                 ? null : QJsonValue(now.addSecs(-startedSecondsAgo).toString(Qt::ISODateWithMs))},
            {QStringLiteral("ended_at_utc"), active ? null : QJsonValue(now.toString(Qt::ISODateWithMs))},
            {QStringLiteral("message_count"), count(messages)},
            {QStringLiteral("marker_count"), count(0)},
            {QStringLiteral("decode_error_count"), count(0)},
            {QStringLiteral("queue_dropped"), count(0)},
            {QStringLiteral("truncated"), false},
            {QStringLiteral("trace_path"), text(trace)},
            {QStringLiteral("sha256_path"), null},
            {QStringLiteral("sha256"), null},
            {QStringLiteral("error_code"), failed ? QJsonValue(QStringLiteral("ERR_TRACE_WRITE")) : null}};
}

struct EvidenceScene
{
    EvidenceBackend backend;
    bool reduceMotion = true;
    std::unique_ptr<mr::AppSettings> settings;
    std::unique_ptr<mr::AppController> app;
    mr::Formatters formatters;
    // The bundled job table, so a job known only by its id is named as in the app.
    mr::JobCatalog jobs;
    std::unique_ptr<mr::EvidenceSubmissionController> evidence;
    std::unique_ptr<QQmlEngine> engine;
    std::unique_ptr<QSignalSpy> warnings;
    std::unique_ptr<QObject> root;
    QQuickWindow *window = nullptr;
    QQuickItem *page = nullptr;

    ~EvidenceScene() { close(); }

    void close()
    {
        page = nullptr;
        window = nullptr;
        root.reset();
        warnings.reset();
        engine.reset();
        evidence.reset();
        app.reset();
        settings.reset();
    }

    bool open(const QString &validationState, const QString &settingsPath = {})
    {
        backend.setValidationFixture(validationState);
        if (!settingsPath.isEmpty())
            settings = std::make_unique<mr::AppSettings>(settingsPath, nullptr);
        app = std::make_unique<mr::AppController>(&backend, settings.get());
        app->recording()->setUserObservation(true);
        evidence = std::make_unique<mr::EvidenceSubmissionController>(&backend);
        engine = std::make_unique<QQmlEngine>();
        engine->rootContext()->setContextProperty(QStringLiteral("App"), app.get());
        engine->rootContext()->setContextProperty(QStringLiteral("Fmt"), &formatters);
        engine->rootContext()->setContextProperty(QStringLiteral("Jobs"), &jobs);
        engine->rootContext()->setContextProperty(QStringLiteral("CalibrationEvidence"), evidence.get());
        engine->rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), reduceMotion);
        warnings = std::make_unique<QSignalSpy>(engine.get(), &QQmlEngine::warnings);
        QQmlComponent component(engine.get());
        component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 1180; height: 900; visible: true; color: Theme.contentBackground
    ScrollView { anchors.fill: parent
        contentWidth: availableWidth
        EvidencePage { objectName: "evidencePage"; width: parent.width }
    }
})", QUrl());
        root.reset(component.create());
        if (!root) {
            qWarning("%s", qPrintable(component.errorString()));
            return false;
        }
        window = qobject_cast<QQuickWindow *>(root.get());
        page = window ? findItem(window->contentItem(), QStringLiteral("evidencePage")) : nullptr;
        return page != nullptr;
    }

    QQuickItem *item(const QString &name) const { return findItem(page, name); }
    QString text(const QString &name) const
    {
        auto *found = item(name);
        return found ? found->property("text").toString() : QString();
    }
    QString stepStatus(int number) const
    {
        auto *step = item(QStringLiteral("evidenceStep_%1").arg(number));
        return step ? step->property("status").toString() : QString();
    }
    bool stepOpen(int number) const
    {
        auto *step = item(QStringLiteral("evidenceStep_%1").arg(number));
        return step && step->property("open").toBool();
    }
    QObject *session() const { return page ? page->property("session").value<QObject *>() : nullptr; }
    QVariant sessionValue(const char *name) const
    {
        auto *object = session();
        return object ? object->property(name) : QVariant();
    }
    bool click(const QString &name) const
    {
        auto *button = item(name);
        return button && button->isVisible() && button->isEnabled()
               && QMetaObject::invokeMethod(button, "clicked");
    }
    /// A real press and release on the item's centre, for controls driven by a TapHandler.
    bool tap(const QString &name) const
    {
        auto *target = item(name);
        if (!target || !target->isVisible() || !window || !QTest::qWaitForWindowExposed(window))
            return false;
        QTest::mouseClick(window, Qt::LeftButton, Qt::NoModifier,
                          target->mapToScene(QPointF(target->width() / 2, target->height() / 2)).toPoint());
        return true;
    }
    /// Replace the round the Collector reports, and read it now.
    void report(const QJsonObject &status)
    {
        backend.scripted = true;
        backend.validation = status;
        app->refreshCaptureValidation();
    }
    bool marksAreEmpty() const
    {
        const QVariantList marks = sessionValue("marks").toList();
        return marks.size() == 5 && std::ranges::all_of(marks,
                                                [](const QVariant &mark) { return mark.isNull(); });
    }
};

[[maybe_unused]] double nowMs()
{
    return static_cast<double>(QDateTime::currentMSecsSinceEpoch());
}

} // namespace
