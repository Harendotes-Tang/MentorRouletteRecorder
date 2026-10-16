# MentorRecorder Shared Calibrations

[简体中文](README.md) | [繁體中文](README.zh-TW.md) | [English](README.en.md) | [日本語](README.ja.md)

This repository stores **shared calibration codes** contributed by players using MentorRecorder.

Each game update may change message numbers (opcodes). For a given region and game client version, these numbers are compiled into the client and are the same for every player.
Once one player completes calibration in the app, they can share the result so that other players do not each need to repeat the calibration.

## Where Processing Runs and How It Is Deployed

Automatic intake, quarantine, recovery, and candidate archiving currently run as **Python rules on GitHub Actions**. They require no large language model, model API key, or always-on maintainer computer.
Players' computers collect and verify evidence locally. GitHub receives the previewed summary only after the player confirms submission. Recording and Npcap capture remain functions of the Windows recorder;
the Python scripts in this repository do not turn the recorder into a macOS game capture client.

A Mac mini can be configured separately for local research or model-assisted work, but this repository **has no local or cloud language model integration and no self-hosted runner configured**.
The current deployment does not depend on a Mac mini being powered on. A model cannot reconstruct an unknown region's actual protocol from a calibration code or summary, or replace local verification.

## Compatibility with Older Clients

The updated repository retains `schema_version: 1` in `index.json`, the `MRC1.` format, calibration code paths, and the original submission form.
Older clients that already support shared calibrations, including the released `v1.7.2`, can continue reading the index and downloading and verifying codes that match their region, game version, and bundled template.
Earlier clients without shared calibration support do not gain that capability from a repository update. A mismatched template or unsupported game version is still rejected under the existing rules.

| Capability | Older clients with shared calibration support | Clients supporting the new evidence workflow |
|---|---|---|
| Read existing index fields and `MRC1.` calibration codes | Remains compatible | Supported |
| Submit calibration codes through the original form | Remains compatible | Supported |
| Enforce reversible quarantine and full-verification requirements | Ignore `quarantined` / `quarantine_epoch` and follow their existing rules | Enforce the new requirements while retaining profiles already fully verified locally |
| Preview and submit minimal evidence; guided observation for regions without templates | No new entry point | Requires matching updates to both the client and capture service |
| Automatically support a new region without an official template | Unsupported | Also unsupported; candidate observations must be collected first |

**Read compatibility does not mean the new rules take effect in older software.** Automatic quarantine is not disguised as permanent `revoked` status to force older software to stop using a code.
The evidence feature has been implemented in source code but has not yet been released in a software installer. Updating this repository or reading this README does not add new entry points or quarantine requirements to an installed client.

## How the App Uses This Repository

- **Read-only downloads.** If no usable local profile is available after a game update, the app reads `index.json`, then downloads calibration code files for the same region and game client version at their recorded commit.
  When an active profile comes from a shared calibration that has not yet been fully verified, or its match detection is inferred from queueing, the app checks the index again at the same minimum interval—at most once every 6 hours—to learn about revocations or more accurate codes.
  The app uploads nothing here.
- **Verify before use.** A downloaded calibration code is not a profile. The app first verifies its structure against traffic on the player's computer. At minimum, the zone-change message observed during login must pass verification before a profile is generated and recording begins.
  Queueing and duty-entry messages continue to be verified after recording starts. If verification fails, local calibration continues without losing the calibration evidence already collected.
  Before an incorrect code is identified, it may cause missing or incorrect records. Once the app detects a contradiction with local traffic, it disables the code and marks records generated from it as needing review.
- **Optional.** Fetching shared calibrations is one of the app's two types of outbound request enabled by default. The other checks for app updates by reading a version file from the main project's releases; it is unrelated to this repository.
  Both can be disabled in settings. Online speech is another player-enabled feature, disabled by default and also unrelated to this repository.
  The app's privacy-boundary documentation explains when requests are made, what they contain, and how to disable them.

## What a Calibration Code Contains

A calibration code consists of `MRC1.` followed by compressed, canonical JSON.

| Included | Not included |
|---|---|
| Format version, region (`CN` / `GLOBAL`), and game client version | Character name, server, account, or IP address |
| ID and hash of the bundled template used | Timestamps, sample counts, or which random duties were queued |
| Match-detection method and the message lengths, byte offsets, or state values it requires | Descriptions, file paths, or any message payload |
| The learned message numbers for match prompts, zone changes, duties, and jobs | |

A code's identifier, `code_sha256`, is the SHA-256 of its canonical content. Its filename in this repository uses the first 12 characters.

## How to Submit

After calibration succeeds and you have checked the calibration timeline in the app, click “Share with other players” (「分享给其他玩家」). This button must be provided by the corresponding app version. Your browser opens a prefilled Issue in this repository.
The source field (「这份校准码的来源」) is filled with “Calibrated locally, with every item checked against the calibration timeline” (「本机校准得到，并逐条核对过校准时间线」). Check the confirmation boxes and submit. GitHub Actions validates and publishes the code automatically,
replies with the result, and closes the Issue. Maintainer involvement is normally unnecessary.

When using another player's calibration, after the app verifies it locally and fully records one duty, it offers “This calibration works—tell other players” (「这份校准好用，告诉其他玩家」), which opens the same form.
The source is filled with “Shared by another player, verified in my app and used for successful recording” (「其他玩家分享，已在我的软件里核实通过并正常记录」). Submitting the same code adds you to its submitter count, giving it higher priority in other players' apps.

The app fills in the source field; please do not change it. Submissions with other source text are rejected. A blank source field—for example, when submitting on someone else's behalf or using an app version that does not support it—is treated as a local calibration.

Players without a GitHub account can give the copied code to someone else to submit. The rules below apply to the submitting account.

## Acceptance Rules

- The submitting GitHub account must be **at least 30 days old**.
- The game client version in the code must follow the game's version format, such as `2026.09.01.0000.0000`.
  Its date must be no later than the day after submission, measured in UTC.
- **Each GitHub account can have only one active calibration code per region and game client version.** Accounts are identified by their numeric IDs, so renaming an account does not increase this allowance.
  Resubmitting the same code does not count again. Submitting a different one **replaces** the previous submission, as described below.
- **Each account has submission limits:** at most 3 different codes submitted in succession for the same region and game client version, and submissions for at most 2 region-and-version combinations per day.
  Days are measured in UTC, starting at 08:00 China Standard Time. Every app instance with shared calibrations enabled downloads the index, whose size is bounded; these limits prevent a single account from filling it.
- If a maintainer revokes a code submitted by an account, that account's submissions for the corresponding region and game client version are **suspended** until the maintainer reviews them. See “Revocation.”
- **There is no slot limit.** Identical codes submitted by different accounts are merged into one entry. `submitters` is the number of distinct accounts that submitted the code or reported successful verification.
  Locally calibrated submissions and submissions of shared codes verified in the app follow the same rules and count equally.
  The app downloads and verifies codes in descending submitter count. Ties favor entries without `conflicting`, then those matching the previous version's structure (`shape_prior: true`, a structural prior),
  then earlier publication. See “When Calibration Codes Differ.” At most 8 codes are verified concurrently for each version.
- A code must be structurally reconstructible using a bundled template in `templates/`. Invalid formats, template mismatches, and invalid structures are rejected, with the reason explained in the Issue.
- Rejected Issues are closed. Submit a new Issue after correcting the problem; editing a closed Issue does not trigger processing again.

## Correcting Your Own Submission

If your submitted code is incorrect, or you later obtain a better calibration, **you do not need to contact a maintainer**. Click “Share with other players” (「分享给其他玩家」) again
and submit a new Issue as usual. The Action replaces your previous submission for that region and version and explains the outcome in its reply:

- If you were the only submitter of the previous code, it is revoked (`revoked: true`, with the entry retained). Other players' apps stop using it.
- If other accounts also submitted the previous code, it remains available; your submission is removed from its count, reducing `submitters` by one.
- Revoked codes are never republished. Resubmitting one cannot restore publication; submit a new code instead.
- Replacing a code this way does not prevent further submissions. However, if a **maintainer** revoked your code, submissions by your account for that region and version are suspended
  until the maintainer reviews and lifts the suspension. If you believe the revocation was mistaken, explain the situation in the Issue where the Action rejected your submission.

## When Calibration Codes Differ

When multiple codes exist for the same region and game client version, the Action compares them after each publication, submitter-count update, or revocation and writes two optional flags to entries in `index.json`.
Older software ignores these flags and retains a valid selection order.

- **`conflicting`.** Two codes are compared field by field: zone-change messages, territory messages, job messages, and match messages—including the match-detection method and its associated lengths, offsets, or state values.
  The first three fields are compared regardless of match-detection method; match messages are compared only within the same method. Fields absent from a code do not participate. If any field differs from
  another non-revoked code for the same version and template, both codes are marked `conflicting: true`. This flag indicates a difference; it cannot establish which code is wrong. When submitter counts are equal, the player's app favors a code whose match-message structure
  agrees with a profile it verified locally for the previous version. Without that evidence, both codes must pass full local verification before activation, until one gains more submissions or reports of successful verification.
- **`shape_prior` (structural prior).** Compared with the most recent earlier version that has codes for the same region and match-detection method, this is `true` when the match-message structure—length, roulette offset, or state values—
  matches, and `false` when earlier codes exist but none has a matching structure. It is omitted when no earlier version is comparable. For accounts that submitted a code for the current version,
  their submissions for earlier versions are excluded from this comparison, preventing one account from inventing version numbers to create a prior for its own code.
  This flag only affects download and verification order. Activation is based on the profile the app itself verified locally for the previous version, not on this repository field.
  It is never grounds for rejection: when the game really changes a message structure, the matching structure may be the incorrect one, so local verification is still required.

When differences are found, the Action's reply on the new submission identifies the other Issue and the differing fields. It also comments once on the other Issue—only when a new code is published, not when another account is added to the same code—
and labels both Issues `conflicting` so maintainers can find them. Replies describe the difference without deciding which code is correct.
If you are sure your code is correct, no action is needed. If you find an error in it, share a new code from the app to replace it.

## How Calibration Conflicts Are Resolved

Routine resolution uses **local verification in the app plus fixed repository rules**. Maintainers do not need to install games from other regions or reproduce every report. Players first preview a minimal summary in the app
and explicitly submit it. The repository validates and deduplicates it, records the latest position, and waits automatically when evidence is insufficient. There is no background upload or language model.

### Automatic Quarantine and Recovery

- Use the [shared calibration evidence form](../../issues/new?template=evidence-calibration.yml). Only `calibration-evidence-v1` is accepted;
  its sole public format is defined in `schemas/calibration-evidence-v1.schema.json`. Unknown or private fields, duplicate JSON keys, exceeded limits, and invalid numbers are rejected.
- Numeric account IDs returned live by GitHub establish identity, and accounts must be at least 30 days old for their votes to count. Within the same region, game version, template, code, rules version, and field,
  each account has only its latest complete, valid vote. A vote requires the same conclusion for the same field from at least two distinct, complete, healthy sessions. Duplicate summaries or sessions, edits to old Issues, and failed retries add no votes.
  A waiting summary that has not yet produced a vote may be supplemented with a second real session. Once any field in an Issue produces a vote, that Issue is frozen; submit a new evidence Issue from the app for a new position or other fields.
- When at least **3 accounts × 2 healthy sessions each** report explicit contradictions for the same field, and negative accounts strictly outnumber positive accounts, the system sets `quarantined` and increments `quarantine_epoch`.
  Newer software still downloads and observes the code, but must fully verify it before activation. Profiles already fully verified locally are not withdrawn because of remote quarantine.
- Each quarantine reason requires positive evidence at the same scale, **newly received after the current quarantine** and belonging to the current epoch, with positive accounts outnumbering negative accounts, before automatic recovery.
  After recovery, negative evidence from the previous round cannot immediately trigger quarantine again. Repackaging old sessions cannot create recovery evidence.
- Automatic quarantine does not set `revoked`, delete codes, or freeze accounts. Multiple candidates can coexist. Permanent revocation remains a separate maintenance operation described below; ordinary reports do not trigger it.

**Counts and format checks are operational thresholds. They cannot prove that a submitter played the game, that the content is truthful, or that accounts are independent.** The app ultimately still relies on full local verification.
Older software ignores quarantine fields and continues using its existing local rules; updating this repository alone adds no new requirements to old software.
All repository writes share a publication concurrency lock, and push conflicts are recalculated from the latest `main`. Automatic replies use one bot comment per Issue, updating it on retries instead of adding duplicates.
A scheduled sweep uses a persistent cursor to process missed events in batches. The ledger has capacity limits and waits when full; replay-prevention records are not deleted to make old evidence appear new.
Current global limits are 4096 summaries, 32768 session aliases, and 16384 latest field positions, with a ledger no larger than 8 MiB, plus up to 4096 receipts for ordinary text reports.
Each account is limited to 128 summaries, 1024 session aliases, and 64 text reports. Exceeding these limits returns `ACCOUNT_CAPACITY_WAIT` without allocating a new record, freezing the account, or changing existing votes.
Exhausted global capacity returns `LEDGER_CAPACITY_WAIT`. Unlimited history or further acceptance after capacity is reached is not guaranteed; replay-prevention records are not automatically discarded to free space.

### How the App Handles Disagreements

Field-level consensus and local priors require an app version that supports these mechanisms. Updating only this repository does not add them to older software, which continues verifying candidates under its own rules.
In versions that support them, candidates for the same region, game client version, and template are handled as follows:

- **Vote by field.** Zone, territory, and job fields are compared separately; match messages are compared only within the same match-detection method. Undeclared fields do not vote.
  For each field, the `submitters` counts of codes supporting the same value are added together. A code needs a basis for resolving every disputed field; a higher count in one field cannot bypass the others.
- **Higher counts affect priority and verification requirements only.** A count exceeding those of other values can provide a consensus basis, but does not prove correctness. Codes with fewer votes are neither revoked by the repository nor permanently rejected by the app for that reason.
  Activation still requires the other prerequisites of the app version in use and verification against local traffic.
- **Ties are not broken arbitrarily.** A profile verified on this computer for the previous version may break a tie only in the match-message field.
  It can do so only when exactly one side matches the local prior and none of the other tied sides has that basis. Priors cannot resolve ties in other fields, ties where both sides have that basis, or ties on a newly installed machine with no profile history.
  The repository's `shape_prior` affects only download and verification order; it cannot replace local evidence.
- **Unresolved candidates require full verification.** Without sufficient consensus or a local prior, a candidate must complete the required local checks for queueing, matching, duty entry, and related events before activation.
  If a contradiction is detected during use, the app disables the candidate and continues trying alternatives or calibrating under its own rules. It does not combine fields from different codes into an uncalibrated “consensus code.”

For example, suppose two MARKER_OFFSET codes each have one submitter. Their zone, territory, and job fields agree, but their match messages are 24 and 40 bytes long.
The first three fields are undisputed; the match field is tied one to one. A locally verified shape from the previous version may break that local tie if it supports exactly one side.
Without that local basis, both codes require full verification. The repository cannot revoke the 40-byte code simply because the 24-byte shape is more common.

### Optional Manual Review and Permanent Revocation

1. **Identify the candidates.** Start from a submission Issue labeled `conflicting` or an “Incorrect calibration” report. Check the region, game client version, template, and full `code_sha256`,
   and record the specific fields that differ. Do not mix different game versions, templates, or match-detection methods and judge entire codes as wrong.
2. **Check local evidence.** In the relevant game client version, inspect the app's candidate-verification results, actual match prompts, duty entry and exit, and recording timeline. Rule out missing relogins, insufficient observations, or capture failures.
   “Not yet observed” does not mean “contradicted.” Submitter counts, account identity, publication order, or a single impression of use cannot replace this check.
3. **Act on the evidence.** Revoke only the code confirmed to contradict actual behavior. If evidence is insufficient, keep the candidates and explain which observations are still missing.
   If both pass verification, keep both and document the reproducible results instead of forcing a single winner to remove the label. `conflicting` continues to be calculated automatically from field differences.
4. **Explain the outcome publicly.** Record the region, version, full candidate identifiers, differing fields, verification basis, conclusion, and action in the relevant Issues so submitters can review them.
   Raw messages, character details, and other personal information are not required. Add an explanation if new evidence later emerges.

### Performing Revocation and Review

In an up-to-date local checkout of **this public repository**, run the following for a code confirmed to be incorrect:

```bash
python tools/publish.py revoke --repo . --code-sha256 <full-identifier-of-confirmed-incorrect-code>
```

The script modifies local files. Review the changes to `index.json` and other files, then commit and push them to this repository's `main` to update the online index.
`revoke` retains the revoked entry, recalculates related flags, and suspends submissions by accounts that submitted the code for the corresponding region and version. See “Revocation” for details.
Do not edit only `conflicting` or `submitters` to manufacture a resolution; subsequent publication and recalculation regenerate them.

`remark` only recalculates flags. It does not decide correctness or revoke codes. `lift` only removes an account's submission suspension for a specified region and version; it does not restore a revoked code.
Players who need a correction should share a new, correct code. Index refresh intervals and download caches affect when clients learn about revocation; running a maintenance command does not immediately change every machine.

## Reporting an Incorrect Calibration

If a code causes problems—false match detection when a prompt appears, missing records, or incorrect jobs or duties—use the
[“Incorrect calibration” form](../../issues/new?template=report-calibration.yml). Enter the region, game client version, and symptoms;
the calibration code is not required.

Ordinary text reports are automatically classified as `evidence-waiting` and do not vote. The system directs players to create a new evidence Issue using the minimal summary previewed in the app, which is then processed automatically under the rules above.
**Report counts do not automatically revoke or quarantine any code, or freeze accounts.** There is no need to wait for a maintainer to own the game for that region.

## Starting a Region Without a Template

In the app's guided observation workflow for regions without templates, explicitly start capture, mark actions such as login, queueing, matching, and duty entry or exit, then stop. Preview the summary and actively submit
[new-region candidate observations](../../issues/new?template=template-observation.yml). The summary contains only fixed metadata such as actions, directions, opcodes, lengths, and counts inside and outside observation windows.
It contains no raw payloads, payload hashes, connection tags, characters, IP addresses, device identifiers, or precise timelines.
The repository deduplicates observations by metadata hash and archives them as `candidates/<hash>.json`. They always remain **CANDIDATE / BLOCKED**, listing missing evidence such as field offsets, roulette identity,
zone-change and duty-completion semantics, and verification on the actual region's game client. It does not reuse Chinese-region constants, invent a complete protocol, produce VERIFIED status, or activate templates dynamically.
Official templates still ship with the app once the evidence is complete. If capture or decoding is unavailable, the actual failure reason is retained; insufficient evidence means the region remains unsupported.

## Revocation

Maintainers can set `revoked` to `true` on the corresponding `index.json` entry, or run:

```bash
python tools/publish.py revoke --repo . --code-sha256 <full-identifier>
```

Revocation does not delete the entry. An app that has not yet activated the code stops using it after reading the revocation flag. An app using the code without full verification learns of revocation when it next reads the index,
disables the code, and marks records generated from it as needing review. A fully verified shared profile that no longer reads the index is disabled when the app detects a contradiction in local traffic.
`revoke` recalculates `conflicting` and `shape_prior` for that version and later versions in the same region: revoked codes no longer participate in comparisons and no longer carry either flag.
Download sources are cached: changes appear on `raw.githubusercontent.com` within minutes, while jsDelivr can take approximately 12 hours. jsDelivr's cache cannot be cleared on demand, so the Action does not attempt it.

Accounts that submitted a revoked code have submissions suspended for that region and game client version. When the Action rejects a submission, it asks the submitter to explain the situation in the Issue. After review, a maintainer can run:

```bash
python tools/publish.py lift --repo . --account id:<numeric-id> --region <CN-or-GLOBAL> --build <game-client-version>
```

The account can then submit again, but the revoked code itself will never be republished. Another code may reuse the filename—the first 12 characters of the identifier—previously occupied by a revoked code.

After flag rules change, maintainers can recalculate both flags for all versions or specified versions to backfill existing entries:

```bash
python tools/publish.py remark --repo . [--region <CN-or-GLOBAL> --build <game-client-version> …]
```

Without a version filter, all regions and versions in the index are recalculated. The command prints one JSON line listing the recalculated versions, the number of changed entries, and versions written without flags because of the index size limit.
If a specified version is absent from the index, no files are changed.

## Cleaning Up Old Versions

The index has a size limit, and submissions do not delete entries. Maintainers can explicitly select game client versions to remove:

```bash
python tools/publish.py prune --repo . --region <CN-or-GLOBAL> --build <game-client-version> [--build <game-client-version> …]
```

Without `--apply`, the command only lists the versions, entry counts, submission-ledger record counts, and code files that would be removed; it changes no files. Add `--apply` after checking the preview.
The selected versions, their entries, submission-ledger records, and code files are removed together. Original files remain in commit history. Both flags for later versions in the same region are recalculated
without using the removed versions as a basis.
The script does not choose which versions to retain by date because version numbers are supplied by submitters, and versions dated today are accepted. Remove only old versions that no players still use.

## Repository Layout

| Path | Contents |
|---|---|
| `index.json` | Published code index downloaded by the app; limited to 64 KB and 512 entries; may include `conflicting`, `shape_prior`, and reversible `quarantined` / `quarantine_epoch` |
| `submissions.json` | Submission ledger: which account (numeric ID) submitted which code for which version, whether it followed successful verification (`basis`), and the previous code it replaced; not downloaded by the app |
| `cn/<game-client-version>/<first-12-identifier-characters>.mrc` | Chinese-region code text, downloaded by the app at the commit that added it |
| `global/<game-client-version>/<first-12-identifier-characters>.mrc` | Global-region code text |
| `templates/<template-id>.json` | Byte-for-byte copies of the app's bundled templates, used by the Action for structural reconstruction and validation |
| `tools/` | Validation and publication scripts copied from the main MentorRecorder repository; do not edit them manually here |
| `.github/ISSUE_TEMPLATE/share-calibration.yml` | Submission form |
| `.github/ISSUE_TEMPLATE/report-calibration.yml` | Incorrect-calibration report form |
| `.github/workflows/publish-calibration.yml` | Automatic validation, publication, and replies; catches up on missed submissions every 30 minutes |
| `.github/workflows/report-calibration.yml` | Automatic waiting status for ordinary reports, instructions to provide evidence, and persistent prevention of duplicate replies |
| `.github/ISSUE_TEMPLATE/evidence-calibration.yml`, `template-observation.yml` | Forms for minimal public summaries after preview |
| `.github/workflows/evidence-calibration.yml` | Automatic evidence intake, quarantine and recovery, candidate archiving, and scheduled catch-up processing |
| `evidence-ledger.json`, `evidence-sweep.json` | Bounded evidence replay prevention, latest account positions, epoch reasons, and catch-up cursor; not downloaded by clients |
| `schemas/`, `candidates/` | Copy of the sole evidence format and unsupported candidate drafts |
| `LICENSE.md`, `LICENSES/` | Licenses |

## Data and Privacy

- `submissions.json` records the submitting account's numeric GitHub ID, the selected source (`basis` is recorded only for successful verification), and checksums of that account's previously replaced codes. These are used only to enforce account-based acceptance rules—one active code per account and version, submission and daily limits, and suspension after revocation—and to find the other Issue when explaining a conflict. The ID and source are already public in the corresponding Issue.
  The app does not download this file, and `index.json` contains no account information.
- The Action uses only GitHub's default `GITHUB_TOKEN`; no other secrets are required.
- Preview the allowlisted summary before submission. A public Issue also exposes the account and GitHub submission time. Its body, summary hash, random session aliases, and numeric-account ledger may remain in history and mirrors.
  The repository does not request characters, IP addresses, raw messages, device identifiers, or precise gameplay timelines. Deleting an Issue is not guaranteed to remove every historical copy.

## License

- **Scripts, workflows, forms, and `templates/`:** GPL-3.0-or-later, matching the main MentorRecorder project.
- **Player-submitted data** (`index.json`, `submissions.json`, and calibration codes in version directories):
  [CC0-1.0](https://creativecommons.org/publicdomain/zero/1.0/legalcode). Submitting means agreeing to publish it under this license.

See [LICENSE.md](LICENSE.md) for details.
