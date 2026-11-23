#pragma once

#include <QByteArrayView>
#include <QString>
#include <QTranslator>

#include <cstdint>

namespace mrtest {

/// One interface language for the three mood words only (Theme.moodLabel),
/// without the build's .qm files.
class MoodWordTranslator final : public QTranslator
{
public:
    enum class Language : std::uint8_t { English, Japanese };
    explicit MoodWordTranslator(Language language) : m_language(language) {}

    QString translate(const char *context, const char *sourceText, const char *, int) const override
    {
        if (QByteArrayView(context) != "Theme")
            return {};
        const bool english = m_language == Language::English;
        const QString source = QString::fromUtf8(sourceText);
        if (source == QString::fromUtf8("顺利"))
            return english ? QStringLiteral("Smooth") : QString::fromUtf8("順調");
        if (source == QString::fromUtf8("一般"))
            return english ? QStringLiteral("So-so") : QString::fromUtf8("ふつう");
        if (source == QString::fromUtf8("糟心"))
            return english ? QStringLiteral("Rough") : QString::fromUtf8("しんどい");
        return {};
    }
    bool isEmpty() const override { return false; }

private:
    Language m_language;
};

} // namespace mrtest
