import QtQuick
import MentorRecorder 1.0

// Picks one character profile of a region. `selection` is a profile id; 0 is
// 「全部角色」 and -1 is 「未分配」, each offered only where the owner asks for it.
// With no region it lists the characters of every region that tells its
// characters apart, each under its region's name when there is more than one.
StyledComboBox {
    id: control

    property string region: ""
    property int selection: 0
    property bool includeAll: false
    // What the row with value 0 says; a picker that must not choose for the
    // player words it as a prompt instead.
    property string allLabel: qsTr("全部角色")
    property bool includeUnassigned: false
    // Marks the character new records are filed under.
    property bool markCurrent: true
    signal profilePicked(int profileId)

    readonly property CharacterNames names: CharacterNames {}
    readonly property var profiles: {
        Theme.trackBindingDependency(Runtime.app.characterProfiles)
        if (control.region.length > 0)
            return Runtime.app.characterProfilesForRegion(control.region)
        // Which regions tell their characters apart also follows the records and the
        // characters nobody has answered for.
        Theme.trackBindingDependency(Runtime.app.dashboard)
        Theme.trackBindingDependency(Runtime.app.characterIdentities)
        const rows = []
        for (const profile of Runtime.app.characterProfiles)
            if (Runtime.app.characterFeaturesVisible(String(profile.region)))
                rows.push(profile)
        return rows
    }
    readonly property bool spansRegions: {
        for (const profile of control.profiles)
            if (profile.region !== control.profiles[0].region) return true
        return false
    }
    readonly property var choices: {
        const rows = control.includeAll ? [{ value: 0, label: control.allLabel }] : []
        for (const profile of control.profiles) {
            const own = control.names.label(profile)
            const name = control.spansRegions
                ? Theme.live(Runtime.fmt.regionLabel(String(profile.region))) + " · " + own : own
            rows.push({
                value: Number(profile.profile_id),
                label: control.markCurrent && profile.is_current ? qsTr("%1（当前）").arg(name) : name
            })
        }
        if (control.includeUnassigned)
            rows.push({ value: -1, label: qsTr("未分配") })
        return rows
    }

    function indexOfProfile(profileId) {
        for (let index = 0; index < choices.length; ++index)
            if (choices[index].value === profileId) return index
        return 0
    }

    model: choices
    textRole: "label"
    valueRole: "value"
    currentIndex: Theme.live(indexOfProfile(selection))
    onActivated: function(index) {
        control.profilePicked(control.choices[index].value)
        // Picking a row moves the box by itself and cuts it loose from `selection`. Put the
        // binding back: an owner that takes the pick at once shows it at once, and one that
        // waits for the Collector (the overview card) shows the character in force until the
        // answer arrives - also when the answer is a refusal.
        control.currentIndex = Qt.binding(function() { return Theme.live(control.indexOfProfile(control.selection)) })
    }
    Accessible.name: qsTr("角色")
}
