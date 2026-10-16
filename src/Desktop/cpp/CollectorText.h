#pragma once

// ---------------------------------------------------------------------------
// Sentences the Collector wrote, in the interface language.
//
// The Collector sends every player-facing sentence twice: as Simplified
// Chinese in the field it always used (`message`, `hint`, `blockers`, ...)
// and as a $defs/TextRef - `{ key, args }` - in the companion `*_ref` field
// (contracts/collector-texts.md). render() turns a TextRef into the language
// on screen with the Simplified Chinese source of contracts/collector-texts.json
// as tr() text (context CollectorText, disambiguated by the key). Anything it
// cannot render - no reference (an older Collector), a key this build does
// not know (a newer one), a missing or mistyped argument - shows the
// Collector's own Chinese instead, which is what every Desktop before the keys
// showed. Simplified Chinese renders exactly that Chinese.
// ---------------------------------------------------------------------------

#include <QString>
#include <QStringList>
#include <QVariant>
#include <QVariantMap>

#include "LiveText.h"

namespace mr {
class BackendReply;
}

namespace mr::collector_text {

/// The sentence \a ref names, in the interface language; \a fallback - the
/// Collector's Chinese for the same sentence - when \a ref cannot be rendered.
QString render(const QVariant &ref, const QString &fallback = QString());

/// The Simplified Chinese \a ref renders, whatever the interface language:
/// what the Collector writes into the legacy field. Empty when it cannot be
/// rendered. For the offline backend, which answers in the Collector's shapes.
QString chinese(const QVariant &ref);

/// A TextRef for \a key, as the Collector would send it.
QVariantMap textRef(const char *key, const QVariantMap &args = {});

/// render(), kept as a recipe so the text follows a later language switch.
LiveText live(const QVariant &ref, const QString &fallback);

/// render() over a `*_refs` array and the Chinese array it parallels: entry i
/// is refs[i] rendered, or fallbacks[i]. A refs array of another length is
/// ignored as a whole (the Chinese is shown), so the two never disagree on
/// how many lines there are.
QStringList renderList(const QVariant &refs, const QVariant &fallbacks);

/// The error \a message a failed \a reply handed its handler, kept for later:
/// the reply's `message_ref` rendered on every read, or \a message itself when
/// there is none or \a reply is already gone. A handler passes the
/// QPointer<BackendReply> it captured; whenDone() runs before the reply is
/// deleted, so the pointer is still set there.
LiveText replyErrorText(const BackendReply *reply, const QString &message);

/// True when this build has a table entry for \a key.
bool knows(const QString &key);

/// Number of keys in the table, for tests.
int size();

} // namespace mr::collector_text
