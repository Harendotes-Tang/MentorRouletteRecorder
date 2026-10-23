#pragma once

// ---------------------------------------------------------------------------
// Test support for the Collector's sentences by key (contracts/collector-texts.md).
//
// The compiled catalogues live in the executable, not in the library the tests
// link, so a test that needs "another language" installs MarkingTranslator: every
// CollectorText source gets a recognisable translation, "[<key>] <Chinese>", that
// keeps its placeholders. A sentence shown that way was rendered from its key; one
// shown in plain Chinese was the Collector's fallback.
// ---------------------------------------------------------------------------

#include <QCoreApplication>
#include <QString>
#include <QTranslator>
#include <QVariantMap>

namespace mrtest {

class MarkingTranslator : public QTranslator
{
public:
    QString translate(const char *context, const char *sourceText, const char *disambiguation,
                      int n) const override
    {
        Q_UNUSED(n)
        if (qstrcmp(context, "CollectorText") != 0 || !sourceText)
            return {};
        return QStringLiteral("[") + QString::fromUtf8(disambiguation) + QStringLiteral("] ")
               + QString::fromUtf8(sourceText);
    }
    bool isEmpty() const override { return false; }
};

/// Installs a MarkingTranslator for the lifetime of the object.
class MarkedLanguage
{
public:
    MarkedLanguage() { QCoreApplication::installTranslator(&m_translator); }
    ~MarkedLanguage() { QCoreApplication::removeTranslator(&m_translator); }
    MarkedLanguage(const MarkedLanguage &) = delete;
    MarkedLanguage &operator=(const MarkedLanguage &) = delete;

private:
    MarkingTranslator m_translator;
};

/// What MarkingTranslator makes of \a chinese, the source of \a key.
inline QString marked(const char *key, const QString &chinese)
{
    return QStringLiteral("[") + QString::fromUtf8(key) + QStringLiteral("] ") + chinese;
}

/// A $defs/TextRef, as the Collector sends it.
inline QVariantMap textRef(const char *key, const QVariantMap &args = {})
{
    QVariantMap ref{{QStringLiteral("key"), QString::fromUtf8(key)}};
    if (!args.isEmpty())
        ref.insert(QStringLiteral("args"), args);
    return ref;
}

} // namespace mrtest
