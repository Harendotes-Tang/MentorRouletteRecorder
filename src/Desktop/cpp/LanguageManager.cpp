#include "LanguageManager.h"

#include "AppSettings.h"

#include <QCoreApplication>
#include <QCryptographicHash>
#include <QFont>
#include <QFontDatabase>
#include <QGuiApplication>
#include <QHash>
#include <QLocale>
#include <QQmlEngine>
#include <QSettings>
#include <QTranslator>
#include <QVariantMap>

namespace {

constexpr char kSettingsKey[] = "ui/language";
// Prefix for immutable, language-specific font stacks; see applyFont().
constexpr char kFontAlias[] = "MentorRecorder Interface";
// Prefix for a face followed by the interface stack; see withInterfaceFallback().
constexpr char kFallbackAlias[] = "MentorRecorder Fallback";

QString stackKey(const QStringList &stack)
{
    return QString::fromLatin1(QCryptographicHash::hash(
        stack.join(QChar::Null).toUtf8(), QCryptographicHash::Sha256).toHex());
}

// Qt's font cache tells the engines of one face apart by how many families wait
// behind it, not by which (QFontCache::Key::operator< in qfont_p.h). Two stacks of
// equal length behind the same face would share the engine built first, so a switch
// from 繁體中文 to 日本語 could keep JhengHei. A stack that would take a length
// another stack behind its face already has gets names no font has until it is longer.
QStringList withOwnLength(QStringList stack, const QString &key)
{
    static QHash<QString, QHash<qsizetype, QString>> stacksByFace;  // face -> length -> key
    QHash<qsizetype, QString> &lengths = stacksByFace[stack.constFirst().toLower()];
    for (int n = 1; lengths.contains(stack.size()) && lengths.value(stack.size()) != key; ++n)
        stack.append(QStringLiteral("MentorRecorder Padding %1").arg(n));
    lengths.insert(stack.size(), key);
    return stack;
}

// A name no font has, fixed by the stack it stands for. Qt keys its engine cache by
// family name, so a stack never changes behind a name already in use. \a ownLength
// pads the stack for an alias whose first face other aliases share (see withOwnLength).
QString stackAlias(const QString &prefix, const QStringList &stack, bool ownLength = false)
{
    const QString key = stackKey(stack);
    const QString alias = prefix + QLatin1Char(' ') + key;
    if (QFont::substitutes(alias).isEmpty())
        QFont::insertSubstitutions(alias, ownLength ? withOwnLength(stack, key) : stack);
    return alias;
}

// The language on screen, readable without a manager (see currentCode()).
QString &currentCodeStorage()
{
    static QString code = QStringLiteral("zh-CN");
    return code;
}
mr::LanguageManager *g_instance = nullptr;

struct LanguageInfo
{
    const char *code;
    const char *qmSuffix;  // :/i18n/mentor_recorder_<suffix>.qm; empty = source text
    const char *locale;    // QLocale / speech tag
    const char *nativeName;
};

// Display order of the settings card: 简体中文 · 繁體中文 · English · 日本語.
const LanguageInfo kLanguages[] = {
    {.code = "zh-CN", .qmSuffix = "", .locale = "zh-CN", .nativeName = "简体中文"},
    {.code = "zh-TW", .qmSuffix = "zh_TW", .locale = "zh-TW", .nativeName = "繁體中文"},
    {.code = "en", .qmSuffix = "en", .locale = "en-US", .nativeName = "English"},
    {.code = "ja", .qmSuffix = "ja", .locale = "ja-JP", .nativeName = "日本語"},
};

const LanguageInfo *find(const QString &code)
{
    for (const LanguageInfo &info : kLanguages) {
        if (code == QLatin1String(info.code))
            return &info;
    }
    return nullptr;
}

} // namespace

namespace mr {

LanguageManager::LanguageManager(AppSettings *settings, QObject *parent)
    : QObject(parent)
    , m_settings(settings)
    , m_code(defaultCode())
{
    if (!g_instance)
        g_instance = this;
}

LanguageManager::~LanguageManager()
{
    if (g_instance == this)
        g_instance = nullptr;
    if (m_translator)
        QCoreApplication::removeTranslator(m_translator.get());
}

QStringList LanguageManager::supportedCodes()
{
    QStringList codes;
    for (const LanguageInfo &info : kLanguages)
        codes.append(QString::fromLatin1(info.code));
    return codes;
}

bool LanguageManager::isSupported(const QString &code)
{
    return find(code) != nullptr;
}

QString LanguageManager::defaultCode()
{
    return QStringLiteral("zh-CN");
}

QString LanguageManager::nativeNameFor(const QString &code)
{
    const LanguageInfo *info = find(code);
    return info ? QString::fromUtf8(info->nativeName) : QString();
}

QString LanguageManager::speechLocaleFor(const QString &code)
{
    const LanguageInfo *info = find(code);
    return QString::fromLatin1(info ? info->locale : "zh-CN");
}

QStringList LanguageManager::preferredFamiliesFor(const QString &code)
{
    // The base stack (main.cpp) leads with Microsoft YaHei UI, whose Han
    // characters take the mainland shapes. Traditional Chinese and Japanese put
    // their own UI face in front; Latin text keeps falling through to the same
    // faces as before, so English reads exactly like the Chinese UI's Latin.
    if (code == QLatin1String("zh-TW")) {
        return {QStringLiteral("Microsoft JhengHei UI"), QStringLiteral("Microsoft JhengHei"),
                QStringLiteral("Noto Sans TC"), QStringLiteral("PingFang TC")};
    }
    if (code == QLatin1String("ja")) {
        return {QStringLiteral("Yu Gothic UI"), QStringLiteral("Meiryo UI"),
                QStringLiteral("Meiryo"), QStringLiteral("Noto Sans JP"),
                QStringLiteral("Hiragino Sans")};
    }
    return {};
}

QString LanguageManager::currentCode()
{
    return currentCodeStorage();
}

LanguageManager *LanguageManager::instance()
{
    return g_instance;
}

QVariantList LanguageManager::languages() const
{
    QVariantList list;
    for (const LanguageInfo &info : kLanguages) {
        list.append(QVariantMap{{QStringLiteral("code"), QString::fromLatin1(info.code)},
                                {QStringLiteral("label"), QString::fromUtf8(info.nativeName)}});
    }
    return list;
}

QString LanguageManager::displayFamily(const QString &code) const
{
    for (const QString &family : preferredFamiliesFor(code)) {
        if (QFontDatabase::hasFamily(family))
            return family;
    }
    // zh-CN and en: the base stack's first face, which the alias leads with for them.
    if (!m_baseFamilies.isEmpty())
        return m_baseFamilies.constFirst();
    return QGuiApplication::font().family();
}

QString LanguageManager::withInterfaceFallback(const QString &family) const
{
    // Qt resolves generic names (monospace) without listing them; leave those to Qt.
    if (m_fontFamily.isEmpty() || !QFontDatabase::hasFamily(family))
        return family;
    // Qt tries an alias's substitutes in order for every character, so the face
    // draws what it has and the interface stack the rest.
    QStringList stack{family};
    for (const QString &face : QFont::substitutes(m_fontFamily)) {
        if (!stack.contains(face, Qt::CaseInsensitive))
            stack.append(face);
    }
    // Every such alias leads with the same face, so each needs a length of its own.
    return stackAlias(QString::fromLatin1(kFallbackAlias), stack, true);
}

void LanguageManager::initialize(const QString &pinnedCode,
                                 const QString &installerDefaultsPath)
{
    QString code;
    if (!pinnedCode.isEmpty() && isSupported(pinnedCode)) {
        m_pinned = true;
        code = pinnedCode;
    } else if (m_settings) {
        code = m_settings->value(QString::fromLatin1(kSettingsKey), QString());
        if (code.isEmpty() && !installerDefaultsPath.isEmpty()) {
            QSettings defaults(installerDefaultsPath, QSettings::IniFormat);
            const QString installedCode = defaults.value(QStringLiteral("Installer/Language")).toString();
            if (defaults.status() == QSettings::NoError && isSupported(installedCode)) {
                code = installedCode;
                m_settings->setValue(QString::fromLatin1(kSettingsKey), code);
            }
        }
    }
    if (!isSupported(code))
        code = defaultCode();
    apply(code);
}

void LanguageManager::attachEngine(QQmlEngine *engine)
{
    m_engine = engine;
    if (m_engine)
        m_engine->setUiLanguage(m_code);
}

void LanguageManager::setLanguage(const QString &code)
{
    if (!isSupported(code) || code == m_code)
        return;
    if (m_settings && !m_pinned)
        m_settings->setValue(QString::fromLatin1(kSettingsKey), code);
    apply(code);
}

void LanguageManager::apply(const QString &code)
{
    const LanguageInfo *info = find(code);
    if (!info)
        return;

    if (m_translator) {
        QCoreApplication::removeTranslator(m_translator.get());
        m_translator.reset();
    }
    if (info->qmSuffix[0] != '\0') {
        auto translator = std::make_unique<QTranslator>();
        const QString path = QStringLiteral(":/i18n/mentor_recorder_%1.qm")
                                 .arg(QString::fromLatin1(info->qmSuffix));
        if (translator->load(path)) {
            QCoreApplication::installTranslator(translator.get());
            m_translator = std::move(translator);
        } else {
            // A build without the compiled catalogue still runs, in the source text.
            qWarning("Cannot load the %s interface translation (%s)", info->code,
                     qPrintable(path));
        }
    }

    m_code = code;
    currentCodeStorage() = code;
    if (m_settings)
        m_settings->setSpeechLanguage(code);
    QLocale::setDefault(QLocale(QString::fromLatin1(info->locale)));
    applyFont(code);
    ++m_revision;

    if (m_engine) {
        m_engine->setUiLanguage(code);
        m_engine->retranslate();
    }
    Q_EMIT languageChanged();
}

void LanguageManager::applyFont(const QString &code)
{
    if (!qobject_cast<QGuiApplication *>(QCoreApplication::instance()))
        return;

    const QString aliasPrefix = QString::fromLatin1(kFontAlias);
    QFont font = QGuiApplication::font();
    if (m_baseFamilies.isEmpty()) {
        // An earlier manager may have installed a language stack already.
        // Recover its real base families, never another alias or its CJK prefix.
        QStringList current = font.families();
        if (current.size() == 1 && current.constFirst().startsWith(aliasPrefix, Qt::CaseInsensitive)) {
            const QString currentAlias = current.constFirst();
            QStringList preferred;
            for (const LanguageInfo &info : kLanguages)
                preferred.append(preferredFamiliesFor(QString::fromLatin1(info.code)));
            current.clear();
            for (const QString &family : QFont::substitutes(currentAlias)) {
                if (!preferred.contains(family, Qt::CaseInsensitive))
                    current.append(family);
            }
        } else if (current.isEmpty()) {
            current = {font.family()};
        }
        for (const QString &family : std::as_const(current)) {
            if (!family.startsWith(aliasPrefix, Qt::CaseInsensitive) && !family.isEmpty()
                && !m_baseFamilies.contains(family, Qt::CaseInsensitive))
                m_baseFamilies.append(family);
        }
    }

    QStringList substitutes;
    for (const QString &family : preferredFamiliesFor(code)) {
        if (QFontDatabase::hasFamily(family))
            substitutes.append(family);
    }
    for (const QString &family : std::as_const(m_baseFamilies)) {
        if (!substitutes.contains(family, Qt::CaseInsensitive))
            substitutes.append(family);
    }
    // Never change a mapping used by live text/render nodes. Clearing Qt's font
    // database on each switch can leave their glyph indexes tied to another face.
    // Include the stack in the key so another manager cannot overwrite it either.
    const QString alias = stackAlias(aliasPrefix + QLatin1Char(' ') + code, substitutes);
    m_fontFamily = alias;
    if (font.families() != QStringList{alias}) {
        font.setFamilies({alias});
        QGuiApplication::setFont(font);
    }
}

} // namespace mr
