#pragma once

// ---------------------------------------------------------------------------
// Interface text that follows the language (界面语言).
//
// tr() fixes a string in the language on screen when it runs. A controller
// that keeps text for later - a status line, the last error, a feedback
// sentence - would therefore keep showing the old language after a switch. It
// keeps a LiveText instead: the recipe that builds the text, run again on every
// read, so the same message comes out in the language now on screen. The owner
// re-emits its NOTIFY signal from onLanguageChanged() so bindings read it again.
//
// Text that is the same in every language (a sentence the Collector wrote, a
// file name) is kept with LiveText::verbatim().
// ---------------------------------------------------------------------------

#include <QObject>
#include <QString>

#include <functional>
#include <type_traits>
#include <utility>

#include "LanguageManager.h"

namespace mr {

class LiveText
{
public:
    LiveText() = default;

    /// \a build returns the text in the language on screen when it is called.
    template <typename Build,
              typename = std::enable_if_t<std::is_invocable_r_v<QString, Build &>>>
    LiveText(Build build) // NOLINT(google-explicit-constructor): a recipe is the text
        : m_build(std::move(build))
    {
    }

    /// Text that is the same in every language. Empty text stays empty.
    static LiveText verbatim(const QString &text)
    {
        if (text.isEmpty())
            return {};
        return LiveText([text] { return text; });
    }

    /// The text in the language on screen now.
    QString text() const { return m_build ? m_build() : QString(); }
    /// True when no text is held (not when a held recipe happens to build "").
    bool isEmpty() const { return !m_build; }
    void clear() { m_build = nullptr; }

private:
    std::function<QString()> m_build;
};

/// Calls \a slot on \a receiver after every interface-language switch. Does
/// nothing without a LanguageManager: unit tests build controllers bare, and
/// their text stays in the source language.
template <typename Receiver, typename Slot>
inline void onLanguageChanged(Receiver *receiver, Slot &&slot)
{
    if (LanguageManager *manager = LanguageManager::instance())
        QObject::connect(manager, &LanguageManager::languageChanged, receiver,
                         std::forward<Slot>(slot));
}

} // namespace mr
