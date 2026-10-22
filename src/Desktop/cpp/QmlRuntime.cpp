#include "QmlRuntime.h"

#include <QJSEngine>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQmlExpression>
#include <QThread>

namespace mr {

QmlRuntime::QmlRuntime(QQmlEngine *engine)
    : QObject(engine)
{
    for (std::size_t index = 0; index < FieldCount; ++index)
        m_values[index] = normalizedValue(static_cast<Field>(index), QVariant{});
}

QmlRuntime *QmlRuntime::forEngine(QQmlEngine *engine)
{
    if (!engine)
        return nullptr;
    Q_ASSERT_X(engine->thread() == QThread::currentThread(), "QmlRuntime::forEngine",
               "Runtime must be obtained on its QML engine thread");
    if (engine->thread() != QThread::currentThread())
        return nullptr;

    // 查找直接子对象，不在动态属性或进程静态变量中缓存可能失效的裸指针。
    if (auto *runtime = engine->findChild<QmlRuntime *>(QString(), Qt::FindDirectChildrenOnly))
        return runtime;

    auto *runtime = new QmlRuntime(engine);
    QJSEngine::setObjectOwnership(runtime, QJSEngine::CppOwnership);
    return runtime;
}

QmlRuntime *QmlRuntime::create(QQmlEngine *engine, QJSEngine *scriptEngine)
{
    Q_ASSERT_X(engine == scriptEngine, "QmlRuntime::create",
               "The singleton factory must use the same QML and JavaScript engine");
    if (engine != scriptEngine)
        return nullptr;
    return forEngine(engine);
}

QVariant QmlRuntime::normalizedValue(Field field, const QVariant &value)
{
    switch (field) {
    case Field::SuppressReleaseNotes:
    case Field::ForceReleaseNotes:
    case Field::GraphsAvailable:
    case Field::SuppressOnboarding:
    case Field::ForceBaselineDialog:
    case Field::ForceDisclosure:
    case Field::ReduceMotion:
    case Field::ForceOpenReflection:
    case Field::ForceOpenShare:
    case Field::ForceOpenImport:
    case Field::ForceOpenDetail:
    case Field::ForceOpenEdit:
    case Field::ForceOpenCreate:
        return value.toBool();
    case Field::ForceUiStyle:
    case Field::ForceSettingsTab:
    case Field::ForceSpeechConfirm:
    case Field::ForceDetailTab:
        return value.toString();
    case Field::ForceWizardStep:
        return value.isValid() ? value.toInt() : 1;
    default:
        return value;
    }
}

void QmlRuntime::setValue(Field field, const QVariant &incomingValue)
{
    Q_ASSERT_X(thread() == QThread::currentThread(), "QmlRuntime::setValue",
               "Runtime may only be configured on its QML engine thread");
    const auto index = static_cast<std::size_t>(field);
    Q_ASSERT_X(index < FieldCount, "QmlRuntime::setValue", "Invalid Runtime field");
    if (thread() != QThread::currentThread() || index >= FieldCount)
        return;

    const QVariant normalized = normalizedValue(field, incomingValue);
    QObject *borrowed = normalized.canConvert<QObject *>() ? normalized.value<QObject *>() : nullptr;
    Q_ASSERT_X(!borrowed || borrowed->thread() == thread(), "QmlRuntime::setValue",
               "Borrowed QML services must live on the QML engine thread");
    if (borrowed && borrowed->thread() != thread())
        return;
    if (m_values[index] == normalized)
        return;

    QObject::disconnect(m_destroyedConnections[index]);
    m_destroyedConnections[index] = {};
    m_values[index] = normalized;
    if (borrowed) {
        m_destroyedConnections[index] = connect(borrowed, &QObject::destroyed, this,
            [this, field] {
                // 不再向 QML 暴露已析构对象；清空只影响当前服务槽。
                setValue(field, QVariant{});
            });
    }
    notifyChanged(field);
}

void QmlRuntime::setValue(Field field, QObject *service)
{
    setValue(field, service ? QVariant::fromValue(service) : QVariant{});
}

void QmlRuntime::enableLegacyContextAdapter()
{
    Q_ASSERT_X(thread() == QThread::currentThread(), "QmlRuntime::enableLegacyContextAdapter",
               "The test context adapter must be installed on the QML engine thread");
    if (thread() != QThread::currentThread() || m_legacyAdapterEnabled)
        return;
    m_legacyAdapterEnabled = true;

    static constexpr std::array<const char *, FieldCount> legacyNames = {
        "Lang", "Names", "App", "Fmt", "Jobs", "Roles", "RunForm", "NoteImages",
        "Reflections", "ReflectionShare", "ImportRecords", "Settings", "ReleaseNotes",
        "CalibrationEvidence", "SuppressReleaseNotes", "ForceReleaseNotes", "Tts",
        "GraphsAvailable", "SuppressOnboarding", "ForceBaselineDialog", "ForceDisclosure",
        "ForceUiStyle", "ForceSettingsTab", "ForceSpeechConfirm", "ReduceMotion", "ForceDetailTab",
        "ForceOpenReflection", "ForceOpenShare", "ForceOpenImport", "ForceOpenDetail", "ForceOpenEdit",
        "ForceOpenCreate", "ForceWizardStep"
    };
    auto *engine = qobject_cast<QQmlEngine *>(parent());
    Q_ASSERT(engine);
    if (!engine)
        return;

    for (std::size_t index = 0; index < FieldCount; ++index) {
        const auto field = static_cast<Field>(index);
        const QString name = QString::fromLatin1(legacyNames[index]);
        // 缺失名字的 typeof 表达式没有可订阅依赖。仅在测试上下文中预声明
        // undefined 值，让后续 setContextProperty 更新可以触发表达式通知。
        if (!engine->rootContext()->contextProperty(name).isValid())
            engine->rootContext()->setContextProperty(name, QVariant{});
        auto *expression = new QQmlExpression(engine->rootContext(), nullptr,
            QStringLiteral("typeof %1 === 'undefined' ? undefined : %1").arg(name), this);
        expression->setNotifyOnValueChanged(true);
        const auto update = [this, expression, field] {
            bool undefined = false;
            const QVariant evaluated = expression->evaluate(&undefined);
            setValue(field, undefined ? QVariant{} : evaluated);
        };
        connect(expression, &QQmlExpression::valueChanged, this, update);
        update();
    }
}

void QmlRuntime::notifyChanged(Field field)
{
    switch (field) {
    case Field::Lang: emit langChanged(); break;
    case Field::Names: emit namesChanged(); break;
    case Field::App: emit appChanged(); break;
    case Field::Fmt: emit fmtChanged(); break;
    case Field::Jobs: emit jobsChanged(); break;
    case Field::Roles: emit rolesChanged(); break;
    case Field::RunForm: emit runFormChanged(); break;
    case Field::NoteImages: emit noteImagesChanged(); break;
    case Field::Reflections: emit reflectionsChanged(); break;
    case Field::ReflectionShare: emit reflectionShareChanged(); break;
    case Field::ImportRecords: emit importRecordsChanged(); break;
    case Field::Settings: emit settingsChanged(); break;
    case Field::ReleaseNotes: emit releaseNotesChanged(); break;
    case Field::CalibrationEvidence: emit calibrationEvidenceChanged(); break;
    case Field::SuppressReleaseNotes: emit suppressReleaseNotesChanged(); break;
    case Field::ForceReleaseNotes: emit forceReleaseNotesChanged(); break;
    case Field::Tts: emit ttsChanged(); break;
    case Field::GraphsAvailable: emit graphsAvailableChanged(); break;
    case Field::SuppressOnboarding: emit suppressOnboardingChanged(); break;
    case Field::ForceBaselineDialog: emit forceBaselineDialogChanged(); break;
    case Field::ForceDisclosure: emit forceDisclosureChanged(); break;
    case Field::ForceUiStyle: emit forceUiStyleChanged(); break;
    case Field::ForceSettingsTab: emit forceSettingsTabChanged(); break;
    case Field::ForceSpeechConfirm: emit forceSpeechConfirmChanged(); break;
    case Field::ReduceMotion: emit reduceMotionChanged(); break;
    case Field::ForceDetailTab: emit forceDetailTabChanged(); break;
    case Field::ForceOpenReflection: emit forceOpenReflectionChanged(); break;
    case Field::ForceOpenShare: emit forceOpenShareChanged(); break;
    case Field::ForceOpenImport: emit forceOpenImportChanged(); break;
    case Field::ForceOpenDetail: emit forceOpenDetailChanged(); break;
    case Field::ForceOpenEdit: emit forceOpenEditChanged(); break;
    case Field::ForceOpenCreate: emit forceOpenCreateChanged(); break;
    case Field::ForceWizardStep: emit forceWizardStepChanged(); break;
    }
}

} // namespace mr
