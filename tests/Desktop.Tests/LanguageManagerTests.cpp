#include "AppSettings.h"
#include "LanguageManager.h"
#include "TtsService.h"

#include <QFont>
#include <QFontDatabase>
#include <QGuiApplication>
#include <QLocale>
#include <QQmlEngine>
#include <QSettings>
#include <QSignalSpy>
#include <QStandardPaths>
#include <QTest>

// 界面语言: the choice, its persistence and what a switch does to the
// application font. The compiled catalogues live in the executable, not in the
// library these tests link; the screenshot tests MentorRecorderQmlLanguage_*
// (tests/Desktop.Tests/CMakeLists.txt) run the executable with --ui-language and
// find the translated text on screen.
class LanguageManagerTests : public QObject
{
    Q_OBJECT
private Q_SLOTS:
    void init()
    {
        QSettings settings(mr::AppSettings::filePath(), QSettings::IniFormat);
        settings.remove(QStringLiteral("ui/language"));
        settings.sync();
    }

    void languagesInDisplayOrder()
    {
        QCOMPARE(mr::LanguageManager::supportedCodes(),
                 (QStringList{"zh-CN", "zh-TW", "en", "ja"}));
        mr::AppSettings settings;
        mr::LanguageManager manager(&settings);
        const QVariantList languages = manager.languages();
        QCOMPARE(languages.size(), 4);
        QCOMPARE(languages.at(1).toMap().value("code").toString(), QStringLiteral("zh-TW"));
        QCOMPARE(languages.at(1).toMap().value("label").toString(),
                 QString::fromUtf8("繁體中文"));
        QCOMPARE(languages.at(3).toMap().value("label").toString(),
                 QString::fromUtf8("日本語"));
        QCOMPARE(mr::LanguageManager::speechLocaleFor("en"), QStringLiteral("en-US"));
        QCOMPARE(mr::LanguageManager::speechLocaleFor("ja"), QStringLiteral("ja-JP"));
        QVERIFY(!mr::LanguageManager::isSupported("fr"));
        QVERIFY(mr::LanguageManager::nativeNameFor("fr").isEmpty());
    }

    void defaultsToSimplifiedChinese()
    {
        mr::AppSettings settings;
        mr::LanguageManager manager(&settings);
        manager.initialize();
        QCOMPARE(manager.code(), QStringLiteral("zh-CN"));
        QCOMPARE(mr::LanguageManager::currentCode(), QStringLiteral("zh-CN"));
        QCOMPARE(manager.nativeName(), QString::fromUtf8("简体中文"));
        QVERIFY(!manager.pinned());
        QCOMPARE(mr::LanguageManager::instance(), &manager);
    }

    void switchPersistsAndSignalsOnce()
    {
        {
            mr::AppSettings settings;
            mr::LanguageManager manager(&settings);
            manager.initialize();
            QSignalSpy changed(&manager, &mr::LanguageManager::languageChanged);
            const int revision = manager.revision();

            manager.setLanguage("ja");
            QCOMPARE(changed.count(), 1);
            QCOMPARE(manager.revision(), revision + 1);
            QCOMPARE(mr::LanguageManager::currentCode(), QStringLiteral("ja"));
            QCOMPARE(QLocale().name(), QStringLiteral("ja_JP"));

            manager.setLanguage("ja");   // already on screen
            manager.setLanguage("fr");   // not offered
            manager.setLanguage(QString());
            QCOMPARE(changed.count(), 1);
            QCOMPARE(manager.code(), QStringLiteral("ja"));
        }
        mr::AppSettings reloaded;
        QCOMPARE(reloaded.value(QStringLiteral("ui/language"), QString()), QStringLiteral("ja"));
        mr::LanguageManager next(&reloaded);
        next.initialize();
        QCOMPARE(next.code(), QStringLiteral("ja"));
        next.setLanguage("zh-CN");
    }

    void unknownPersistedValueFallsBack()
    {
        mr::AppSettings settings;
        settings.setValue(QStringLiteral("ui/language"), QStringLiteral("klingon"));
        mr::LanguageManager manager(&settings);
        manager.initialize();
        QCOMPARE(manager.code(), QStringLiteral("zh-CN"));
    }

    void pinnedLanguageIsNeverWritten()
    {
        mr::AppSettings settings;
        mr::LanguageManager manager(&settings);
        manager.initialize("en");
        QVERIFY(manager.pinned());
        QCOMPARE(manager.code(), QStringLiteral("en"));
        manager.setLanguage("zh-TW");
        QCOMPARE(manager.code(), QStringLiteral("zh-TW"));
        QVERIFY(settings.value(QStringLiteral("ui/language"), QString()).isEmpty());
        manager.setLanguage("zh-CN");
    }

    void fontAliasFollowsLanguage()
    {
        mr::AppSettings settings;
        mr::LanguageManager manager(&settings);
        manager.initialize();
        const QString alias = QStringLiteral("MentorRecorder Interface");
        // Every text item copies the application font once; it must name the
        // alias, or a later switch could not reach the items already on screen.
        QCOMPARE(QGuiApplication::font().families(), QStringList{alias});
        const QStringList base = QFont::substitutes(alias);
        QVERIFY(!base.isEmpty());

        manager.setLanguage("ja");
        const QStringList ja = QFont::substitutes(alias);
        QCOMPARE(QGuiApplication::font().families(), QStringList{alias});
        // This language's faces (those installed) in front, the base stack behind.
        QVERIFY(ja.size() >= base.size());
        QCOMPARE(ja.mid(ja.size() - base.size()), base);
        if (QFontDatabase::hasFamily(QStringLiteral("Yu Gothic UI")))
            QCOMPARE(ja.constFirst(), QStringLiteral("Yu Gothic UI"));

        manager.setLanguage("zh-TW");
        const QStringList tw = QFont::substitutes(alias);
        QVERIFY(!tw.contains(QStringLiteral("Yu Gothic UI")));

        manager.setLanguage("en");
        QCOMPARE(QFont::substitutes(alias), base);
        manager.setLanguage("zh-CN");
    }

    // 播报跟随界面语言: each language keeps its own voice and templates;
    // 简体中文 keeps the keys earlier builds wrote.
    void speechSettingsArePerLanguage()
    {
        QSettings raw(mr::AppSettings::filePath(), QSettings::IniFormat);
        for (const QString &key : raw.allKeys()) {
            if (key.startsWith(QLatin1String("tts/")))
                raw.remove(key);
        }
        raw.sync();

        mr::AppSettings settings;
        mr::LanguageManager manager(&settings);
        manager.initialize();
        QCOMPARE(settings.templateMatched(), QString::fromUtf8("指导者任务匹配成功"));
        QVERIFY(!settings.templatesCustomised());
        settings.setTemplateEntered(QString::fromUtf8("进本 {duty}"));
        settings.setTtsVoice(QStringLiteral("local:Huihui"));
        QVERIFY(settings.templatesCustomised());

        QSignalSpy changed(&settings, &mr::AppSettings::ttsChanged);
        manager.setLanguage("en");
        // The library carries no catalogue, so the English default reads as its
        // source text here; what matters is that nothing of 简体中文 leaks over.
        QCOMPARE(settings.templateEntered(), QString::fromUtf8("进入 {duty}"));
        QVERIFY(settings.ttsVoice().isEmpty());
        QVERIFY(!settings.templatesCustomised());
        settings.setTemplateEntered(QStringLiteral("Entering {duty}"));
        settings.setTtsVoice(QStringLiteral("local:Zira"));
        QVERIFY(changed.count() >= 2);

        QSettings stored(mr::AppSettings::filePath(), QSettings::IniFormat);
        QCOMPARE(stored.value(QStringLiteral("tts/tpl_entered")).toString(),
                 QString::fromUtf8("进本 {duty}"));
        QCOMPARE(stored.value(QStringLiteral("tts/en/tpl_entered")).toString(),
                 QStringLiteral("Entering {duty}"));
        QCOMPARE(stored.value(QStringLiteral("tts/voice")).toString(), QStringLiteral("local:Huihui"));
        QCOMPARE(stored.value(QStringLiteral("tts/en/voice")).toString(), QStringLiteral("local:Zira"));

        settings.resetTemplates();
        QCOMPARE(settings.templateEntered(), QString::fromUtf8("进入 {duty}"));
        QCOMPARE(settings.ttsVoice(), QStringLiteral("local:Zira")); // the voice is not a template

        manager.setLanguage("zh-CN");
        QCOMPARE(settings.templateEntered(), QString::fromUtf8("进本 {duty}"));
        QCOMPARE(settings.ttsVoice(), QStringLiteral("local:Huihui"));
        // Put back to its default, a line counts as unedited again.
        settings.setTemplateEntered(QString::fromUtf8("进入 {duty}"));
        QVERIFY(!settings.templatesCustomised());
    }

    void voicesOrderedForTheLanguage()
    {
        using Voice = mr::TtsService::VoiceEntry;
        const QList<Voice> voices{
            {QStringLiteral("Zira"), QLocale(QLocale::English, QLocale::UnitedStates)},
            {QStringLiteral("Haruka"), QLocale(QLocale::Japanese, QLocale::Japan)},
            {QStringLiteral("Hanhan"), QLocale(QLocale::Chinese, QLocale::Taiwan)},
            {QStringLiteral("Huihui"), QLocale(QLocale::Chinese, QLocale::China)},
            {QStringLiteral("Hazel"), QLocale(QLocale::English, QLocale::UnitedKingdom)},
        };
        const auto names = [](const QList<Voice> &list) {
            QStringList out;
            for (const Voice &voice : list)
                out.append(voice.name);
            return out;
        };
        // Default: zh-CN first, as every earlier build did.
        QCOMPARE(names(mr::TtsService::orderVoices(voices)).constFirst(), QStringLiteral("Huihui"));
        QCOMPARE(names(mr::TtsService::orderVoices(voices, QLocale(QStringLiteral("en-US")))),
                 (QStringList{"Zira", "Hazel", "Huihui", "Haruka", "Hanhan"}));
        QCOMPARE(names(mr::TtsService::orderVoices(voices, QLocale(QStringLiteral("zh-TW")))),
                 (QStringList{"Hanhan", "Huihui", "Zira", "Haruka", "Hazel"}));
        QCOMPARE(names(mr::TtsService::orderVoices(voices, QLocale(QStringLiteral("ja-JP")))).constFirst(),
                 QStringLiteral("Haruka"));

        const QList<Voice> chineseOnly{voices.at(3)};
        QVERIFY(mr::TtsService::missesLanguage(chineseOnly, QLocale(QStringLiteral("ja-JP"))));
        // A mainland voice reads 繁體中文 too.
        QVERIFY(!mr::TtsService::missesLanguage(chineseOnly, QLocale(QStringLiteral("zh-TW"))));
        QVERIFY(!mr::TtsService::missesLanguage({}, QLocale(QStringLiteral("ja-JP"))));
    }

    void engineFollowsLanguage()
    {
        mr::AppSettings settings;
        mr::LanguageManager manager(&settings);
        manager.initialize();
        QQmlEngine engine;
        manager.attachEngine(&engine);
        QCOMPARE(engine.uiLanguage(), QStringLiteral("zh-CN"));
        manager.setLanguage("en");
        QCOMPARE(engine.uiLanguage(), QStringLiteral("en"));
        manager.setLanguage("zh-CN");
    }
};

int main(int argc, char **argv)
{
    QStandardPaths::setTestModeEnabled(true);
    QGuiApplication app(argc, argv);
    LanguageManagerTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "LanguageManagerTests.moc"
