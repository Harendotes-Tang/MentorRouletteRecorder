#pragma once

// ---------------------------------------------------------------------------
// 界面语言.
//
// Simplified Chinese is the source text of every qsTr() / tr() string; the other
// three languages are Qt Linguist translations compiled into the binary
// (src/Desktop/i18n/*.ts -> :/i18n/*.qm). Switching installs the matching
// translator, puts the language's CJK face in front of the application font and
// asks the QML engine to re-evaluate its translated bindings, so the window
// changes language in place without a restart.
//
// The choice lives in desktop.ini (key ui/language) like every other UI
// preference. Nothing here leaves the machine.
// ---------------------------------------------------------------------------

#include <QObject>
#include <QPointer>
#include <QString>
#include <QStringList>
#include <QVariantList>

#include <memory>

class QQmlEngine;
class QTranslator;

namespace mr {

class AppSettings;

class LanguageManager : public QObject
{
    Q_OBJECT

    /// "zh-CN", "zh-TW", "en" or "ja".
    Q_PROPERTY(QString code READ code NOTIFY languageChanged)
    /// The language's own name: 简体中文 / 繁體中文 / English / 日本語.
    Q_PROPERTY(QString nativeName READ nativeName NOTIFY languageChanged)
    /// [{ code, label }] in display order; each label is written in its own language.
    Q_PROPERTY(QVariantList languages READ languages CONSTANT)
    /// Bumped on every switch. A binding whose text comes from C++ (Fmt.*, a
    /// model role) reads it to be re-evaluated, which qsTr() bindings are anyway.
    Q_PROPERTY(int revision READ revision NOTIFY languageChanged)
    /// True when --ui-language pinned the language for this run: the settings
    /// card still switches, but nothing is written to desktop.ini.
    Q_PROPERTY(bool pinned READ pinned CONSTANT)

public:
    explicit LanguageManager(AppSettings *settings, QObject *parent = nullptr);
    ~LanguageManager() override;

    static QStringList supportedCodes();
    static bool isSupported(const QString &code);
    static QString defaultCode();
    /// 简体中文 / 繁體中文 / English / 日本語; empty for an unknown code.
    static QString nativeNameFor(const QString &code);
    /// The BCP 47 tag speech voices are matched against: zh-CN, zh-TW, en-US, ja-JP.
    static QString speechLocaleFor(const QString &code);
    /// Families put in front of the application font for \a code, so Han
    /// characters take the shapes the language expects (empty for zh-CN and en,
    /// which keep the base stack).
    static QStringList preferredFamiliesFor(const QString &code);

    /// The language on screen right now, for C++ that formats text outside a
    /// QObject tr() context (duty names, speech lines). zh-CN before any manager
    /// exists.
    static QString currentCode();
    /// The manager main() created, or nullptr (unit tests, before start-up).
    /// C++ that caches translated text connects to its languageChanged().
    static LanguageManager *instance();

    QString code() const { return m_code; }
    QString nativeName() const { return nativeNameFor(m_code); }
    QVariantList languages() const;
    int revision() const { return m_revision; }
    bool pinned() const { return m_pinned; }

    /// Apply the persisted language (or \a pinnedCode, without persisting it).
    /// Call once after QApplication exists and before any controller builds text.
    void initialize(const QString &pinnedCode = QString());
    /// The engine whose bindings are re-evaluated on a switch.
    void attachEngine(QQmlEngine *engine);

    /// The face a label written in \a code is drawn in (the settings card shows
    /// 繁體中文 / 日本語 in their own shapes whatever the interface language).
    Q_INVOKABLE QString displayFamily(const QString &code) const;

    /// Persist (unless pinned) and apply. Unknown codes are ignored.
    Q_INVOKABLE void setLanguage(const QString &code);

Q_SIGNALS:
    void languageChanged();

private:
    void apply(const QString &code);
    void applyFont(const QString &code);

    AppSettings *m_settings = nullptr;
    QPointer<QQmlEngine> m_engine;
    std::unique_ptr<QTranslator> m_translator;
    QStringList m_baseFamilies;  // main.cpp's stack, behind the alias
    QString m_code;
    int m_revision = 0;
    bool m_pinned = false;
};

} // namespace mr
