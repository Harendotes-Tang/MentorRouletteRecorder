#include "QmlRuntime.h"
#include "TestCollectorGuard.h"

#include <QCoreApplication>
#include <QMetaProperty>
#include <QPointer>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQmlPropertyMap>
#include <QSignalSpy>
#include <QStandardPaths>
#include <QTest>
#include <QVariantMap>

#include <memory>

namespace {

using Runtime = mr::QmlRuntime;
using Field = Runtime::Field;

class TestPropertyMap final : public QQmlPropertyMap
{
    Q_OBJECT

public:
    explicit TestPropertyMap(QObject *parent)
        : QQmlPropertyMap(this, parent)
    {
    }
};

class QmlRuntimeTests final : public QObject
{
    Q_OBJECT

private slots:
    void generatedPropertiesRetainTypedDefaults()
    {
        QQmlEngine engine;
        auto *runtime = Runtime::forEngine(&engine);
        QVERIFY(runtime);

        const char *services[] = {
            "lang", "names", "app", "fmt", "jobs", "roles", "runForm",
            "noteImages", "reflections", "reflectionShare", "importRecords",
            "settings", "releaseNotes", "calibrationEvidence", "tts"
        };
        for (const auto *name : services) {
            const auto property = runtime->metaObject()->property(
                runtime->metaObject()->indexOfProperty(name));
            QVERIFY2(property.isValid(), name);
            QCOMPARE(property.metaType().id(), QMetaType::QVariant);
            QVERIFY2(property.hasNotifySignal(), name);
            QVERIFY2(!property.read(runtime).isValid(), name);
        }

        const char *flags[] = {
            "suppressReleaseNotes", "forceReleaseNotes", "graphsAvailable",
            "suppressOnboarding", "forceBaselineDialog", "forceDisclosure",
            "reduceMotion", "forceOpenReflection",
            "forceOpenShare", "forceOpenImport", "forceOpenDetail",
            "forceOpenEdit", "forceOpenCreate"
        };
        for (const auto *name : flags) {
            const auto property = runtime->metaObject()->property(
                runtime->metaObject()->indexOfProperty(name));
            QVERIFY2(property.isValid(), name);
            QCOMPARE(property.metaType().id(), QMetaType::Bool);
            QVERIFY2(property.hasNotifySignal(), name);
            QVERIFY2(!property.read(runtime).toBool(), name);
        }

        for (const auto *name : {"forceUiStyle", "forceSettingsTab", "forceDetailTab",
                                 "forceSpeechConfirm"}) {
            const auto property = runtime->metaObject()->property(
                runtime->metaObject()->indexOfProperty(name));
            QVERIFY2(property.isValid(), name);
            QCOMPARE(property.metaType().id(), QMetaType::QString);
            QVERIFY2(property.hasNotifySignal(), name);
            QVERIFY2(property.read(runtime).toString().isEmpty(), name);
        }

        const auto wizardStep = runtime->metaObject()->property(
            runtime->metaObject()->indexOfProperty("forceWizardStep"));
        QVERIFY(wizardStep.isValid());
        QCOMPARE(wizardStep.metaType().id(), QMetaType::Int);
        QVERIFY(wizardStep.hasNotifySignal());
        QCOMPARE(wizardStep.read(runtime).toInt(), 1);
    }

    void enginesAndProductionFactoriesRemainIndependent()
    {
        QObject owner;
        QObject firstService(&owner);
        QObject secondService(&owner);
        QQmlEngine firstEngine;
        QQmlEngine secondEngine;

        auto *first = Runtime::forEngine(&firstEngine);
        auto *second = Runtime::forEngine(&secondEngine);
        QVERIFY(first);
        QVERIFY(second);
        QVERIFY(first != second);
        QCOMPARE(Runtime::forEngine(&firstEngine), first);
        QCOMPARE(Runtime::create(&firstEngine, &firstEngine), first);
        QCOMPARE(Runtime::create(&secondEngine, &secondEngine), second);
        QCOMPARE(first->parent(), &firstEngine);
        QCOMPARE(second->parent(), &secondEngine);

        first->setValue(Field::App, &firstService);
        first->setValue(Field::ReduceMotion, true);
        first->setValue(Field::ForceUiStyle, QStringLiteral("classic"));
        first->setValue(Field::ForceSpeechConfirm, QStringLiteral("openai:zh-CN:alloy"));
        second->setValue(Field::App, &secondService);
        second->setValue(Field::ForceUiStyle, QStringLiteral("eorzea"));

        QCOMPARE(first->app().value<QObject *>(), &firstService);
        QCOMPARE(second->app().value<QObject *>(), &secondService);
        QVERIFY(first->reduceMotion());
        QVERIFY(!second->reduceMotion());
        QCOMPARE(first->forceUiStyle(), QStringLiteral("classic"));
        QCOMPARE(second->forceUiStyle(), QStringLiteral("eorzea"));
        QCOMPARE(first->forceSpeechConfirm(), QStringLiteral("openai:zh-CN:alloy"));
        QVERIFY(second->forceSpeechConfirm().isEmpty());

        second->setValue(Field::ReduceMotion, true);
        first->setValue(Field::ReduceMotion, false);
        QVERIFY(!first->reduceMotion());
        QVERIFY(second->reduceMotion());
    }

    void engineOwnsRuntimeAndBorrowsExternalServices()
    {
        QObject owner;
        auto service = std::make_unique<QObject>(&owner);
        QPointer<QObject> serviceGuard(service.get());
        QPointer<Runtime> runtimeGuard;
        QSignalSpy destroyed(service.get(), &QObject::destroyed);
        {
            QQmlEngine engine;
            auto *runtime = Runtime::forEngine(&engine);
            runtimeGuard = runtime;
            runtime->setValue(Field::App, service.get());
            QCOMPARE(service->parent(), &owner);
            QCOMPARE(runtime->app().value<QObject *>(), service.get());
        }
        QVERIFY(runtimeGuard.isNull());
        QVERIFY(!serviceGuard.isNull());
        QCOMPARE(service->parent(), &owner);
        QCOMPARE(destroyed.count(), 0);
    }

    void borrowedObjectDestructionClearsOnlyItsFieldAndNotifies()
    {
        QObject owner;
        auto service = std::make_unique<QObject>(&owner);
        QObject otherService(&owner);
        QQmlEngine engine;
        auto *runtime = Runtime::forEngine(&engine);
        runtime->setValue(Field::Settings, &otherService);
        QSignalSpy appChanged(runtime, &Runtime::appChanged);
        QSignalSpy settingsChanged(runtime, &Runtime::settingsChanged);

        // Exercise the QVariant overload too: it must preserve the same borrowed
        // QObject lifetime contract as setValue(Field, QObject *).
        runtime->setValue(Field::App, QVariant::fromValue(service.get()));
        QCOMPARE(runtime->app().value<QObject *>(), service.get());
        QCOMPARE(service->parent(), &owner);
        QCOMPARE(appChanged.count(), 1);
        service.reset();
        QVERIFY(!runtime->app().isValid());
        QCOMPARE(appChanged.count(), 2);
        QCOMPARE(runtime->settings().value<QObject *>(), &otherService);
        QCOMPARE(settingsChanged.count(), 0);
    }

    void replacingServiceDisconnectsOldDestructionNotification()
    {
        QObject owner;
        auto previous = std::make_unique<QObject>(&owner);
        QObject replacement(&owner);
        QQmlEngine engine;
        auto *runtime = Runtime::forEngine(&engine);
        runtime->setValue(Field::App, previous.get());
        runtime->setValue(Field::App, &replacement);
        QSignalSpy changed(runtime, &Runtime::appChanged);

        previous.reset();
        QCOMPARE(runtime->app().value<QObject *>(), &replacement);
        QCOMPARE(replacement.parent(), &owner);
        QCOMPARE(changed.count(), 0);
        runtime->setValue(Field::App, &replacement);
        QCOMPARE(changed.count(), 0);
    }

    void dynamicServiceVariantsKeepTheirOriginalValues()
    {
        QObject owner;
        TestPropertyMap speech(&owner);
        speech.insert(QStringLiteral("volume"), 45);
        const QVariantMap appearance{
            {QStringLiteral("uiStyle"), QStringLiteral("classic")},
            {QStringLiteral("nested"), QVariantMap{{QStringLiteral("enabled"), true}}}
        };
        QQmlEngine engine;
        auto *runtime = Runtime::forEngine(&engine);
        runtime->setValue(Field::Settings, appearance);
        runtime->setValue(Field::Tts, &speech);

        QCOMPARE(runtime->settings().metaType().id(), QMetaType::QVariantMap);
        QCOMPARE(runtime->settings().toMap(), appearance);
        QCOMPARE(runtime->tts().value<QObject *>(), &speech);
        QCOMPARE(speech.parent(), &owner);
        speech.insert(QStringLiteral("volume"), 70);
        QCOMPARE(qobject_cast<QQmlPropertyMap *>(runtime->tts().value<QObject *>())
                     ->value(QStringLiteral("volume")).toInt(), 70);
    }

    void qmlImportPreservesMapServicesAndLiveContextNotifications()
    {
        QObject owner;
        TestPropertyMap speech(&owner);
        speech.insert(QStringLiteral("volume"), 45);
        QQmlEngine engine;
        engine.rootContext()->setContextProperty(QStringLiteral("Settings"),
            QVariantMap{{QStringLiteral("uiStyle"), QStringLiteral("classic")}});
        engine.rootContext()->setContextProperty(QStringLiteral("Tts"), &speech);
        engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), false);
        engine.rootContext()->setContextProperty(QStringLiteral("ForceWizardStep"), 3);
        engine.rootContext()->setContextProperty(QStringLiteral("ForceSpeechConfirm"),
                                                 QStringLiteral("openai:zh-CN:alloy"));
        QSignalSpy warnings(&engine, &QQmlEngine::warnings);
        QQmlComponent component(&engine);
        component.setData(R"(import QtQml
import MentorRecorder 1.0
QtObject {
    property string style: Runtime.settings.uiStyle
    property int volume: Runtime.tts.volume
    property bool reduceMotion: Runtime.reduceMotion
    property int wizardStep: Runtime.forceWizardStep
    property string speechVoiceId: Runtime.forceSpeechConfirm
    property bool missingServiceIsUndefined: Runtime.app === undefined
    property bool defaultOpenDetail: Runtime.forceOpenDetail
})", QUrl(QStringLiteral("runtime-live-context.qml")));
        std::unique_ptr<QObject> root(component.create());
        QVERIFY2(root, qPrintable(component.errorString()));
        auto *runtime = Runtime::forEngine(&engine);
        QCOMPARE(root->property("style").toString(), QStringLiteral("classic"));
        QCOMPARE(root->property("volume").toInt(), 45);
        QVERIFY(!root->property("reduceMotion").toBool());
        QCOMPARE(root->property("wizardStep").toInt(), 3);
        QCOMPARE(root->property("speechVoiceId").toString(), QStringLiteral("openai:zh-CN:alloy"));
        QVERIFY(root->property("missingServiceIsUndefined").toBool());
        QVERIFY(!root->property("defaultOpenDetail").toBool());
        QCOMPARE(speech.parent(), &owner);

        QSignalSpy changed(runtime, &Runtime::reduceMotionChanged);
        engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QTRY_VERIFY(runtime->reduceMotion());
        QTRY_VERIFY(root->property("reduceMotion").toBool());
        QCOMPARE(changed.count(), 1);
        engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), false);
        QTRY_VERIFY(!runtime->reduceMotion());
        QTRY_VERIFY(!root->property("reduceMotion").toBool());
        QCOMPARE(changed.count(), 2);

        engine.rootContext()->setContextProperty(QStringLiteral("Settings"),
            QVariantMap{{QStringLiteral("uiStyle"), QStringLiteral("eorzea")}});
        QTRY_COMPARE(root->property("style").toString(), QStringLiteral("eorzea"));
        speech.insert(QStringLiteral("volume"), 80);
        QTRY_COMPARE(root->property("volume").toInt(), 80);
        engine.rootContext()->setContextProperty(QStringLiteral("ForceSpeechConfirm"),
                                                 QStringLiteral("azure:ja-JP:NanamiNeural"));
        QTRY_COMPARE(root->property("speechVoiceId").toString(),
                     QStringLiteral("azure:ja-JP:NanamiNeural"));
        QCOMPARE(warnings.count(), 0);
    }

    void legacyAdapterObservesFieldsAddedAfterSingletonCreation()
    {
        QQmlEngine engine;
        QQmlComponent component(&engine);
        component.setData(R"(import QtQml
import MentorRecorder 1.0
QtObject {
    property bool reduceMotion: Runtime.reduceMotion
    property string style: Runtime.forceUiStyle
})", QUrl(QStringLiteral("runtime-late-context.qml")));
        std::unique_ptr<QObject> root(component.create());
        QVERIFY2(root, qPrintable(component.errorString()));
        auto *runtime = Runtime::forEngine(&engine);
        QVERIFY(!runtime->reduceMotion());
        QVERIFY(root->property("style").toString().isEmpty());
        QSignalSpy changed(runtime, &Runtime::reduceMotionChanged);

        engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
        engine.rootContext()->setContextProperty(QStringLiteral("ForceUiStyle"),
                                                 QStringLiteral("harendotes"));
        QTRY_VERIFY(root->property("reduceMotion").toBool());
        QTRY_COMPARE(root->property("style").toString(), QStringLiteral("harendotes"));
        QCOMPARE(changed.count(), 1);
    }
};

} // namespace

int main(int argc, char **argv)
{
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QCoreApplication app(argc, argv);
    QmlRuntimeTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "QmlRuntimeTests.moc"
