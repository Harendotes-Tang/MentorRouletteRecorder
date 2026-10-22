#pragma once

#include <QMetaObject>
#include <QObject>
#include <QString>
#include <QVariant>
#include <QtQmlIntegration/qqmlintegration.h>

#include <array>
#include <cstddef>
#include <cstdint>

class QJSEngine;
class QQmlEngine;

namespace mr {

/**
 * @brief 为单个 QML 引擎提供已存在的业务对象和启动参数。
 *
 * 引擎通过 QObject 父子关系拥有此对象；业务服务仅借用，不更改其 parent、
 * 启动顺序或数据归属。服务槽保留 QVariant，以支持生产 QObject、QVariantMap
 * 和测试的 QQmlPropertyMap 等替身，而不伪造具体控制器类型。
 * 所有读取、配置及服务销毁必须发生在所属引擎线程。
 */
class QmlRuntime final : public QObject
{
    Q_OBJECT
    QML_NAMED_ELEMENT(Runtime)
    QML_SINGLETON

    Q_PROPERTY(QVariant lang READ lang NOTIFY langChanged)
    Q_PROPERTY(QVariant names READ names NOTIFY namesChanged)
    Q_PROPERTY(QVariant app READ app NOTIFY appChanged)
    Q_PROPERTY(QVariant fmt READ fmt NOTIFY fmtChanged)
    Q_PROPERTY(QVariant jobs READ jobs NOTIFY jobsChanged)
    Q_PROPERTY(QVariant roles READ roles NOTIFY rolesChanged)
    Q_PROPERTY(QVariant runForm READ runForm NOTIFY runFormChanged)
    Q_PROPERTY(QVariant noteImages READ noteImages NOTIFY noteImagesChanged)
    Q_PROPERTY(QVariant reflections READ reflections NOTIFY reflectionsChanged)
    Q_PROPERTY(QVariant reflectionShare READ reflectionShare NOTIFY reflectionShareChanged)
    Q_PROPERTY(QVariant importRecords READ importRecords NOTIFY importRecordsChanged)
    Q_PROPERTY(QVariant settings READ settings NOTIFY settingsChanged)
    Q_PROPERTY(QVariant releaseNotes READ releaseNotes NOTIFY releaseNotesChanged)
    Q_PROPERTY(QVariant calibrationEvidence READ calibrationEvidence NOTIFY calibrationEvidenceChanged)
    Q_PROPERTY(bool suppressReleaseNotes READ suppressReleaseNotes NOTIFY suppressReleaseNotesChanged)
    Q_PROPERTY(bool forceReleaseNotes READ forceReleaseNotes NOTIFY forceReleaseNotesChanged)
    Q_PROPERTY(QVariant tts READ tts NOTIFY ttsChanged)
    Q_PROPERTY(bool graphsAvailable READ graphsAvailable NOTIFY graphsAvailableChanged)
    Q_PROPERTY(bool suppressOnboarding READ suppressOnboarding NOTIFY suppressOnboardingChanged)
    Q_PROPERTY(bool forceBaselineDialog READ forceBaselineDialog NOTIFY forceBaselineDialogChanged)
    Q_PROPERTY(bool forceDisclosure READ forceDisclosure NOTIFY forceDisclosureChanged)
    Q_PROPERTY(QString forceUiStyle READ forceUiStyle NOTIFY forceUiStyleChanged)
    Q_PROPERTY(QString forceSettingsTab READ forceSettingsTab NOTIFY forceSettingsTabChanged)
    Q_PROPERTY(QString forceSpeechConfirm READ forceSpeechConfirm NOTIFY forceSpeechConfirmChanged)
    Q_PROPERTY(bool reduceMotion READ reduceMotion NOTIFY reduceMotionChanged)
    Q_PROPERTY(QString forceDetailTab READ forceDetailTab NOTIFY forceDetailTabChanged)
    Q_PROPERTY(bool forceOpenReflection READ forceOpenReflection NOTIFY forceOpenReflectionChanged)
    Q_PROPERTY(bool forceOpenShare READ forceOpenShare NOTIFY forceOpenShareChanged)
    Q_PROPERTY(bool forceOpenImport READ forceOpenImport NOTIFY forceOpenImportChanged)
    Q_PROPERTY(bool forceOpenDetail READ forceOpenDetail NOTIFY forceOpenDetailChanged)
    Q_PROPERTY(bool forceOpenEdit READ forceOpenEdit NOTIFY forceOpenEditChanged)
    Q_PROPERTY(bool forceOpenCreate READ forceOpenCreate NOTIFY forceOpenCreateChanged)
    Q_PROPERTY(int forceWizardStep READ forceWizardStep NOTIFY forceWizardStepChanged)

public:
    /// 与迁移前的上下文入口逐项对应，仅供 C++ 配置入口使用。
    enum class Field : std::uint8_t {
        Lang,
        Names,
        App,
        Fmt,
        Jobs,
        Roles,
        RunForm,
        NoteImages,
        Reflections,
        ReflectionShare,
        ImportRecords,
        Settings,
        ReleaseNotes,
        CalibrationEvidence,
        SuppressReleaseNotes,
        ForceReleaseNotes,
        Tts,
        GraphsAvailable,
        SuppressOnboarding,
        ForceBaselineDialog,
        ForceDisclosure,
        ForceUiStyle,
        ForceSettingsTab,
        ForceSpeechConfirm,
        ReduceMotion,
        ForceDetailTab,
        ForceOpenReflection,
        ForceOpenShare,
        ForceOpenImport,
        ForceOpenDetail,
        ForceOpenEdit,
        ForceOpenCreate,
        ForceWizardStep
    };

    /**
     * @brief 返回该引擎唯一的 Runtime，必要时创建为引擎的直接子对象。
     * @param engine 调用线程所属的引擎；空指针返回空，不创建全局实例。
     * @return 引擎拥有的借用指针，生命周期不超过 engine；跨线程调用不受支持。
     */
    static QmlRuntime *forEngine(QQmlEngine *engine);

    /// QML singleton 工厂；返回 forEngine 的同一实例，使用 C++ 所有权。
    static QmlRuntime *create(QQmlEngine *engine, QJSEngine *scriptEngine);

    /**
     * @brief 更新指定服务或参数，只有归一化后的值变化时才发出对应通知。
     * @param field 原上下文入口；必须为有效的 Field 枚举。
     * @param incomingValue 服务按 QVariant 保留；顶层 QObject 指针仅借用并跟踪销毁。
     *              参数转换为声明的 bool、QString 或 int；无效参数恢复默认值。
     * @note QObject 服务必须与引擎同线程；不接管 parent 或删除服务。
     *       QVariantMap 中的嵌套对象仍遵循调用方原有生命周期约定。
     */
    void setValue(Field field, const QVariant &incomingValue);

    /// 借用同线程对象；传入空指针会将服务槽清为空 QVariant。
    void setValue(Field field, QObject *service);

    /**
     * @brief 仅供源 QML 测试工厂使用：跟踪原 rootContext 入口及其后续变动。
     *
     * 每个表达式由本对象拥有；引擎销毁时一起释放。重复调用不增加适配器。
     * 生产启动通过 setValue 显式配置，不启用此兼容路径。未注入服务保持
     * 无效 QVariant，使 QML 的 typeof 检查继续得到 undefined。
     */
    void enableLegacyContextAdapter();

    QVariant lang() const { return value(Field::Lang); }
    QVariant names() const { return value(Field::Names); }
    QVariant app() const { return value(Field::App); }
    QVariant fmt() const { return value(Field::Fmt); }
    QVariant jobs() const { return value(Field::Jobs); }
    QVariant roles() const { return value(Field::Roles); }
    QVariant runForm() const { return value(Field::RunForm); }
    QVariant noteImages() const { return value(Field::NoteImages); }
    QVariant reflections() const { return value(Field::Reflections); }
    QVariant reflectionShare() const { return value(Field::ReflectionShare); }
    QVariant importRecords() const { return value(Field::ImportRecords); }
    QVariant settings() const { return value(Field::Settings); }
    QVariant releaseNotes() const { return value(Field::ReleaseNotes); }
    QVariant calibrationEvidence() const { return value(Field::CalibrationEvidence); }
    bool suppressReleaseNotes() const { return value(Field::SuppressReleaseNotes).toBool(); }
    bool forceReleaseNotes() const { return value(Field::ForceReleaseNotes).toBool(); }
    QVariant tts() const { return value(Field::Tts); }
    bool graphsAvailable() const { return value(Field::GraphsAvailable).toBool(); }
    bool suppressOnboarding() const { return value(Field::SuppressOnboarding).toBool(); }
    bool forceBaselineDialog() const { return value(Field::ForceBaselineDialog).toBool(); }
    bool forceDisclosure() const { return value(Field::ForceDisclosure).toBool(); }
    QString forceUiStyle() const { return value(Field::ForceUiStyle).toString(); }
    QString forceSettingsTab() const { return value(Field::ForceSettingsTab).toString(); }
    QString forceSpeechConfirm() const { return value(Field::ForceSpeechConfirm).toString(); }
    bool reduceMotion() const { return value(Field::ReduceMotion).toBool(); }
    QString forceDetailTab() const { return value(Field::ForceDetailTab).toString(); }
    bool forceOpenReflection() const { return value(Field::ForceOpenReflection).toBool(); }
    bool forceOpenShare() const { return value(Field::ForceOpenShare).toBool(); }
    bool forceOpenImport() const { return value(Field::ForceOpenImport).toBool(); }
    bool forceOpenDetail() const { return value(Field::ForceOpenDetail).toBool(); }
    bool forceOpenEdit() const { return value(Field::ForceOpenEdit).toBool(); }
    bool forceOpenCreate() const { return value(Field::ForceOpenCreate).toBool(); }
    int forceWizardStep() const { return value(Field::ForceWizardStep).toInt(); }

signals:
    void langChanged();
    void namesChanged();
    void appChanged();
    void fmtChanged();
    void jobsChanged();
    void rolesChanged();
    void runFormChanged();
    void noteImagesChanged();
    void reflectionsChanged();
    void reflectionShareChanged();
    void importRecordsChanged();
    void settingsChanged();
    void releaseNotesChanged();
    void calibrationEvidenceChanged();
    void suppressReleaseNotesChanged();
    void forceReleaseNotesChanged();
    void ttsChanged();
    void graphsAvailableChanged();
    void suppressOnboardingChanged();
    void forceBaselineDialogChanged();
    void forceDisclosureChanged();
    void forceUiStyleChanged();
    void forceSettingsTabChanged();
    void forceSpeechConfirmChanged();
    void reduceMotionChanged();
    void forceDetailTabChanged();
    void forceOpenReflectionChanged();
    void forceOpenShareChanged();
    void forceOpenImportChanged();
    void forceOpenDetailChanged();
    void forceOpenEditChanged();
    void forceOpenCreateChanged();
    void forceWizardStepChanged();

private:
    explicit QmlRuntime(QQmlEngine *engine);

    static constexpr std::size_t FieldCount = static_cast<std::size_t>(Field::ForceWizardStep) + 1;

    QVariant value(Field field) const { return m_values[static_cast<std::size_t>(field)]; }
    static QVariant normalizedValue(Field field, const QVariant &value);
    void notifyChanged(Field field);

    // 所有服务引用只存值，不拥有对象；销毁连接绑定 this，避免引擎销毁后回调。
    std::array<QVariant, FieldCount> m_values;
    std::array<QMetaObject::Connection, FieldCount> m_destroyedConnections;
    bool m_legacyAdapterEnabled = false;
};

} // namespace mr
