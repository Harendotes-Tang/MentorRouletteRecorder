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
// ---------------------------------------------------------------------------

#include <QString>

namespace mr::ipc {

/// Number of SHA-256 bytes kept, hex-encoded, in the pipe name.
inline constexpr int kSidHashBytes = 16;

/// "MentorRecorder.<32 hex chars>.v1". Returns an empty string when
/// \a sidString is empty.
QString pipeNameForSid(const QString &sidString);

/// Prefix \a pipeName with the Win32 local pipe namespace.
QString localSocketServerName(const QString &pipeName);

/// The current process token's user SID in string form ("S-1-5-21-..."),
/// or an empty string when it cannot be obtained (or off Windows).
QString currentUserSidString();

/// Convenience: the full server name for the current user, or an empty
/// string when the SID could not be determined.
QString currentUserServerName();

/// Just the pipe name (no \.\pipe\ prefix) for the current user, or an
/// empty string when the SID could not be determined. The Collector derives
/// the name of its graceful-stop event from exactly this string, so the
/// Desktop has to be able to spell it without the transport prefix.
QString currentUserPipeName();

} // namespace mr::ipc
