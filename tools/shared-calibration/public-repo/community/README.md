# Community reminders and macros

MentorRecorder can read this directory without signing in to GitHub. Use **Public library → Refresh** to see available entries. Import a reminder or macro to use it offline, and compare revisions before applying updates.

## Contribute

1. In MentorRecorder, select reminders and macros and review the public preview.
2. Select CC BY 4.0 or CC BY-SA 4.0. Only share work you may license under that choice, and keep author and source attribution for adaptations. Existing calibration data uses its own license.
3. Submit with your GitHub account from the app, or copy the complete structured block into the **Share a mentor reminder or macro** Issue form. The Issue and content are public.
4. Check the Issue for its processing result. Community submissions have not been verified for gameplay accuracy. A maintainer can independently review an item through the community workflow.

The same text is published once. An item whose body matches a live item of the same type (after unifying line breaks and dropping spaces at line ends and blank lines at either end) is left out, the rest of the submission is published, and the receipt names the published item to import instead; links to a left-out new item point at the published one. An author's edit whose new text repeats another item keeps the published version. A submission that publishes nothing new and keeps nothing unchanged is labelled `community-duplicate`. Text that reads almost the same (a Dice coefficient of at least 0.9 over neighbouring characters, after NFKC normalization, dropping sound cues and spacing, and lower-casing) is published, listed in the receipt and labelled `community-similar` with the first receipt; a maintainer may remove that label once reviewed.

The stable ID stays unchanged when the author edits a title. For an update from a new Issue, refresh the public library before previewing the submission. For an adaptation, start from a separate ID and keep the original source and license. Closing an unprocessed Issue prevents its publication. For a published item, request a withdrawal or status correction from a maintainer. Imported notes remain with their users.

## Publication

`v1/index.json` points to an immutable commit and a SHA-256 for `v1/snapshot.json`. The snapshot bundles items for anonymous downloads, and `v1/reminders/` and `v1/macros/` mirror the entries for review. Hashes validate the downloaded bytes. Verification of gameplay advice requires maintainer review. Formats are defined in [the schema](../schemas/community-content-v1.schema.json).

The workflow detects a structured marker in Issue text even if GitHub omitted the label. It reads the current Issue and authenticates its author from GitHub metadata. Scheduled runs recover missed events. Community jobs queue in their own concurrency group, so edits to open Issues never hold up calibration publishing, and they rebuild from current main when a push loses a race with another writer. GitCode mirroring runs after successful or failed publication jobs finish and on a 30-minute schedule.

This repository distributes player-contributed text. The external 新大陆见闻录 links used by MentorRecorder are maintained separately.
