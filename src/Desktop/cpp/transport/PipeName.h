#pragma once

// ---------------------------------------------------------------------------
// Per-user Named Pipe name derivation.
//
//   name        = "MentorRecorder." + <sid hash> + ".v1"
//   <sid hash>  = lowercase hex of the first 16 bytes of
//                 SHA-256(UTF-8 bytes of the user's SID string)
//   server name = \\.\pipe\<name>          (what QLocalSocket connects to)
//
// The hash keeps two users on the same machine apart without putting a raw
// SID into a globally visible object name.
//
// A development build (BuildFlavour.h) serves and connects to
// "MentorRecorder." + <sid hash> + ".dev.v1" instead, so that it and the
// installed software never meet on the same account; the Collector is told
// that name with --pipe, because it derives only the plain form itself.
// ---------------------------------------------------------------------------

#include "BuildFlavour.h"

#include <QString>

namespace mr::ipc {

/// Number of SHA-256 bytes kept, hex-encoded, in the pipe name.
inline constexpr int kSidHashBytes = 16;

/// "MentorRecorder.<32 hex chars>.v1" - the name the Collector derives for
/// this SID itself (src/Collector/Ipc/PipeNaming.cs). Returns an empty string
/// when \a sidString is empty.
QString pipeNameForSid(const QString &sidString);

/// The per-user pipe name as a build of \a flavour serves and connects to it:
/// pipeNameForSid() for a distribution build; for a development build the same
/// name with ".dev" before ".v1" ("MentorRecorder.<32 hex chars>.dev.v1"), so a
/// development Desktop and the installed one on the same account never meet -
/// not through the pipe, nor through the serve lease, the stop event and the
/// single-instance key, which are all derived from this string. Empty when
/// \a sidString is empty.
QString pipeNameForSid(const QString &sidString, build::Flavour flavour);

/// Prefix \a pipeName with the Win32 local pipe namespace.
QString localSocketServerName(const QString &pipeName);

/// The current process token's user SID in string form ("S-1-5-21-..."),
/// or an empty string when it cannot be obtained (or off Windows).
QString currentUserSidString();

/// Convenience: the full server name for the current user, or an empty
/// string when the SID could not be determined.
QString currentUserServerName();

/// Just the pipe name (no \.\pipe\ prefix) for the current user in this
/// build's flavour, or an empty string when the SID could not be determined.
/// The Collector derives the name of its graceful-stop event from exactly this
/// string, so the Desktop has to be able to spell it without the transport
/// prefix.
QString currentUserPipeName();

} // namespace mr::ipc
