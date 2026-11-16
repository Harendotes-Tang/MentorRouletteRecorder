import QtQuick
import MentorRecorder 1.0

// How a character profile is named on screen. The profile the software created
// by itself - the one that took over a region's records when profiles arrived,
// or a region's first - has no name of its own (display_name is empty) and is
// shown as 「原有记录」 until the player renames it.
QtObject {
    function label(profile) {
        const name = profile && profile.display_name !== undefined ? String(profile.display_name) : ""
        return name.length > 0 ? name : qsTr("原有记录")
    }

    // The name of the profile with this id; empty when there is none (a run
    // assigned to no character, or a profile removed since).
    function labelOf(profileId) {
        Theme.trackBindingDependency(Runtime.app.characterProfiles)
        const profile = Runtime.app.characterProfile(Number(profileId) || 0)
        return profile.profile_id !== undefined ? label(profile) : ""
    }
}
