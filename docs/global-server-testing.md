# Global Server Testing and Evidence Collection

<a id="current-test"></a>
## Current test — automatic recording in normal play

The evidence for Global client build **2026.11.03** is complete. Test build **1.10.0-beta.1**
(Pre-release, <https://github.com/Harendotes-Tang/MentorRouletteRecorder/releases/tag/v1.10.0-beta.1>)
and newer record Mentor Roulette runs on this client version automatically and tell your characters
apart, the same way as on the Chinese server. What is
left is to see it work in normal play. No evidence round, capture or payload consent is needed.

1. Install 1.10.0-beta.1 (or a newer test build listed on the releases page). The second-round card no longer appears for 2026.11.03; that is expected.
2. Play the Mentor Roulette as usual. After leaving the duty, open **History**: the run should be there
   with the duty name, your job and the result (a cleared duty shows as completed).
3. If you play more than one character, log in with another one: the Overview asks once whether it is
   a new character. A character you have already answered for is switched to automatically. If the
   Overview asks again about a character you already answered for (for example on another day or
   after maintenance), please tell us: it would mean the identification does not hold across days,
   which has only been checked on one day so far.
4. Send a short note privately: whether each Mentor run appeared, and anything that looked wrong — a
   missing run, a wrong duty or job, a run recorded for a roulette that was not the Mentor Roulette, or
   the wrong character. If something went wrong, attach the diagnostics report
   (**捕获诊断 → 工具 → 导出诊断报告**); it contains no packet content.

The last evidence round (one completed non-Mentor roulette, 2026-11-22) and the character
identification round of the same day are on file.

When the Global client updates to a new version, see [section 8](#self-calibration): test builds that
include Global self-calibration can re-learn the new version on your PC instead of waiting for a new
release.

## Earlier rounds (completed)

This guide asks you to help confirm three things on the Global (international) version of
FINAL FANTASY XIV: whether the Global client is recognised, whether its game data can be
decoded, and which signals appear from queueing until you leave the duty. Please do one full
round first and send the files back; once the data is confirmed usable, a second round
follows.

Download the **1.9.1-beta.4** test build (marked Pre-release) from the GitHub releases page,
<https://github.com/Harendotes-Tang/MentorRouletteRecorder/releases/tag/v1.9.1-beta.4>, and run
`MentorRecorder-1.9.1-beta.4-setup.exe` to install it (the setup wizard offers English; its default
language is Simplified Chinese); if a newer test build is listed there, use the newest one. The page also has a `.sha256` file beside each download, which you can check with
PowerShell's `Get-FileHash -Algorithm SHA256`. The in-app update check only offers formal releases,
so test builds have to be downloaded by hand. Automatic
recording for the Global server currently shows as "unsupported"; during evidence
collection no real mentor roulette records are created automatically. That is expected for
this round.

Patch cadence note: the Chinese, Traditional Chinese and Global clients are separate builds
even when they release the same patch on the same day. A profile learned on one of them
never applies to another, which is why this round is needed at all.

## 1. Before you start

1. If you already have real records, back them up first with the version you are using:
   **Settings → Data → Back up now**. Keep the backup on your own PC; you do not need to
   send it. The test build upgrades the record database, and older versions cannot open the
   upgraded database. If you need to roll back, contact me and we will restore from the
   backup.
2. Note the app version, your Windows version, the Global client version, and whether you
   use a VPN or a gaming accelerator. Take the game version from the game client, not the
   launcher.
3. Make sure Npcap works. **WinPcap API-compatible Mode** must be enabled when Npcap is
   installed. If the app says Npcap is missing, compatibility mode is off, or permissions
   are insufficient, send a screenshot of that message first and fix it before collecting.
4. Fully quit the game, then open the test build. If you prefer an English interface, pick
   it under **Settings → General → Interface language**. In Settings, set **Game region**
   explicitly to **Global (GLOBAL)**; do not leave it on **Automatic (installation path)**
   and do not pick another region. If the region cannot be changed, make sure the game is
   closed and wait for listening or collection to stop. The choice is bound to the first game
   client that runs after you make it, which for this round is your Global client. A client
   whose installation path clearly names another region is never relabelled as Global;
   instead the app shows a notice and does not record it automatically. If you also play on
   another region's client on this PC, set **Game region** back to **Automatic (installation
   path)** when testing is over.
5. Keep your usual network and accelerator setup for the whole round. If you change the
   setup or lose the connection midway, write it in the report; do not treat two different
   setups as one normal round.

## 2. Interface checks first (no duty needed)

- Switch the interface language and confirm **Game region** stays on GLOBAL. The interface
  language and the game region are independent settings.
- On the Overview page in the **All regions** view, confirm the Global achievement progress
  is shown on its own and not added into one total. If you already had records from
  another region, confirm those numbers are still there.
- If you are willing to test the achievement settings: write down the Global
  **Goal** and **Starting count**, change them once with Global selected, and enter "Global
  test" as the reason. Confirm the other regions did not change, then restore the original
  Global values. After saving, restart the app and check that the values were kept. If you
  would rather not touch real data, skip this item and say so.
- Open **Add record**, set the record's region to Global, and check that the duty list and
  names look right. Checking the list is enough; you do not need to save. If a name differs
  from the game, note the app's name and the game's name.
- If you have test records, also check the **All regions / CN / TC / GLOBAL** filter on the
  History and Statistics pages. With no records, write "empty data, not tested"; there is no
  need to supply your whole history for this.

For anything odd, a screenshot and the steps to reproduce are enough.

## 3. Start collecting

1. Go to **Capture diagnostics → Tools → Help support other server regions → Collection
   steps**. This opens the **New region capture** page; while a round is under way or has
   just ended, the button reads **Continue** instead. The page walks through five steps,
   numbered ① to ⑤ below.
2. Step ①, **Pick the region and adapter**: confirm the region shows **Global (GLOBAL)**
   again. Leave the network adapter on **Auto (the one recommended adapter)** first; only if
   the app cannot pick a single recommended adapter, choose the adapter the game is using, as
   the screen suggests. If an adapter you chose
   earlier is no longer available, the page asks you to pick again and will not start until
   you do.
3. **Quit the game completely first.** While the game is running, **Start capture** cannot
   be clicked and the page says **The game is running: quit it completely before you
   start**; after quitting, click **I've quit, check again**. Once the page says **The game
   isn't running, ready to start**, click **Start capture**, then launch the game and stop at
   the title screen.
4. Step ②: wait until the page shows **Capturing · you can log in now**, then log in to your
   character. If it says it is waiting for the game, for a client choice or for an adapter,
   follow that prompt first; markers cannot be added while it is waiting, and **Cancel
   wait** gives up the round.
5. **If it says a connection already exists and the game must be restarted: keep the
   collection waiting, close the game client completely and start it again.** Logging out
   to the title screen or teleporting does not replace this restart. If it still never
   reaches **Capturing**, send a screenshot; there is no need to keep retrying.
6. After logging in, walk around normally for about 30 to 60 seconds and watch whether the
   **Packets** counter keeps increasing; once the page says **Data is readable, go ahead and
   queue**, move on to step ③. If the page shows **Data problem** (**Packets stay at 0**, or
   decode failures climbing quickly), take a screenshot and click **Stop and export
   diagnostics**; you do not need to queue a full duty. **Packets stay at 0** only appears
   once the game has been connected to a server for 60 seconds without any packet, so
   waiting at the title screen does not trigger it. Step ② also has **Stop capture** to end
   the round at any time. The parse and record counts in the real-recording area
   staying at 0 does not by itself mean the collection failed. The Global client is the one
   the capture library was written against, so decoding is expected to work; if it does
   not, the second round will include a separate tool and steps for it.

## 4. One complete run from queue to leaving the duty

If you can queue **Duty Roulette: Mentor**, please collect that one. If you are not
eligible, say so; you can also collect one ordinary duty roulette for comparison, but it
cannot stand in as mentor roulette evidence. Note the roulette type, the duty you actually
entered and your job.

First pick the **Roulette** type (**Mentor roulette** or **Other roulette**) in step ③,
**Queue one roulette and mark each event**. At the moment each event happens in the game,
switch to that step and click the matching button, ideally within about 3 seconds:

1. After you press the game's queue button → click **Queued**.
2. When the game shows the match found / duty confirmation window → click **Matched**.
3. When loading finishes and you are inside the duty → click **Entered**.
4. When the duty completion message appears → click **Victory**.
5. After you leave the duty and are back outside → click **Left**.

Missed clicks, duplicate clicks, long delays, a withdrawn queue, a declined match, joining
in progress, a disconnect or an uncleared duty should all be written in the report exactly
as they happened. Joining midway, not clearing, a missed or late click and a disconnect can
also be ticked under **This round**; they go into the note for the maintainer. Do not click
the remaining buttons afterwards to make a complete-looking sequence, and do not click
**Victory** if the duty was not cleared.

One collection may last at most 2 hours including waiting and restarts. Save each round
separately. If the first round is usable and you have time, collect a second round the same
way. Different duties or jobs should be recorded as they really were.

## 5. Stop and save

1. After leaving the duty and placing the last marker, click **Stop capture** and wait
   for the status to become **Saved**. Do not quit the app while it shows **Saving**.
2. Take a screenshot of the collection page including the save status and the four
   counters **Packets**, **Markers written**, **Decode failures** and **Queue drops**. If it
   shows **Capture failed** or **Ended; file incomplete**, keep that screenshot and say so;
   do not count it as a normal completion. Step ④ still lists whatever files the round left;
   send them all the same, and the missing ones are named in the note for the maintainer.
3. In step ④'s file list, click **Open folder** on the `trace.jsonl` row and copy the whole
   collection folder for this round to your desktop. It should contain `trace.jsonl` and
   `trace.jsonl.sha256`. Keep the original file names and contents and keep both files
   together; do not edit the JSONL by hand or regenerate the checksum file.
4. Click **Export report** on the **Diagnostic report (JSON)** row and choose where to save
   it. Saving it into the same return folder is easiest. The app suggests a name like
   `mentor_diagnostics_<date-time>.json`; keep whatever name it generated. When the
   `collector-*.log` row is listed as required, or logs are otherwise needed, **Open logs** on
   that row opens their folder. A log the row marks as required can contain a few short excerpts of
   the game program's instruction bytes, read from the game file on disk (no packets, addresses
   or player data).
5. Click **Next: write the note** to reach step ⑤. Fill in the duty, your job and the VPN or
   accelerator, check **This round**, then click **Copy note**. The note already carries the
   versions, times, region, markers and counters; paste it at the top of `test-report.txt`
   and complete the rest of the template below.
6. Copy this round's files before clicking **Start another round**, restarting the app or
   changing networks. The app keeps only the latest 10 collection folders and nothing older
   than 7 days; keep your desktop copy until I confirm it arrived.

**The candidate summary is optional; do not get stuck on it:**

- After **Saved**, you can open **Candidate summary (optional)** in step ⑤ and click
  **Preview summary**. It only reflects candidate clues and does not prove that automatic
  recording works.
- If you are willing to use **Copy summary and open submission page**, it copies the full
  JSON and opens a GitHub form that may be pre-filled with the summary. You do not need to
  submit an issue this round. Paste the clipboard into Notepad, save it as UTF-8 with "All
  files" as the type under the name `global_observation_01.json`, and send it back. Do not
  truncate or edit the content.
- If you would rather not open GitHub, or the preview fails, skip this item. Note the
  message shown and send the collection files and the diagnostics. The summary is only
  offered for the most recent collection; handle it before the next round or before
  restarting the app.

## 6. What to send back

Put everything into one folder, zip it, and email it to **mentorrecorder@gmail.com** with the
subject `[GLOBAL evidence] <date> round <N>`, for example `[GLOBAL evidence] 2026-11-05 round 1`.
Attachments are limited to 25 MB; split the files over two emails or attach a Google Drive link
if you exceed it. Do not post the files in a public issue or chat:

- **Always: `test-report.txt`**, filled in using the template below. Not tested, failed,
  or not eligible are all fine to write as they are.
- **When it could be exported: `mentor_diagnostics_*.json`**, the diagnostic report
  exported from the app. If the export itself fails, send a screenshot of the error instead
  and say so.
- **When a collection file was produced: each round's `trace.jsonl` and
  `trace.jsonl.sha256`.** Put the two rounds in different subfolders so they do not
  overwrite each other. Keep files from failed rounds too and mark why they failed. A
  failed round may have only the JSONL and no checksum file; send what exists and say so.
  If nothing was produced, write "not produced"; do not create empty files.
- **Screenshots: the final collection status, plus any abnormal screens.** If convenient,
  add the match confirmation window, the in-game duty name or the completion message, and
  label which round they belong to. Blur character names, chat and anything else personal.
- **If available: `global_observation_01.json` and any further candidate summaries.**
- **If you would like to be credited:** put the name you want listed (for example
  `Character@World` or a nickname) in the email, and it will be added to the README
  acknowledgements. Without one, nothing is listed.
- **Whenever step ④ lists it as required, and otherwise for startup or collection
  problems: the `collector-*.log` diagnostic logs from the same time.** The page marks this
  row required when the round used a reading method the app found on your computer, or
  when it could not find one; the row says why. The maintainer needs the search
  result, which only this log contains. In that case the log can also contain a few short excerpts
  of the game program's instruction bytes (each at most 384 bytes, about 10 KB in all), taken from
  the game file on disk; they are not packets and carry no addresses or player data. Click **Open logs** in step ④, or reach the database folder via
  **Settings → Data → Open folder** and open the `logs` subfolder; copy only the logs for this round, not the database. If you
  cannot find them, send the rest and say so.
- **Only if the game version could not be identified: the small text file
  `ffxivgame.ver` from the client's `game` folder.** Just that file, not the game folder.

No game EXE or DLL, no complete record database, no account credentials and no raw
Wireshark packets are needed. The built-in collection file stores no packet payloads, only
timing and the information linking a round together; the full `trace.jsonl` goes back
privately and is never pasted into a public issue. The diagnostic report cannot replace the
collection file, and screenshots cannot replace the original JSONL and checksum file.

## Report template

```text
App version / test build name:
Windows version:
Game client version:
Test date, start and end time (with time zone, e.g. UTC+1):
Able to queue Duty Roulette: Mentor:
Network: wired / Wi-Fi; VPN or accelerator name, and whether it stayed the same throughout:

Interface checks:
GLOBAL region kept after saving / switching language: OK / problem / not tested
Per-region achievement shown separately, settings independent: OK / problem / not tested
Global duty names, record region filter: OK / problem / not tested

Round 1:
Collection subfolder:
Roulette type / actual duty name / job:
Normal start or joined in progress:
Match window shown, duty cleared, left normally:
Missed / delayed / duplicate markers, declined match, disconnect or network change:
Final collection status and the four counters (a screenshot is fine):
Candidate summary: attached / not obtained; if not, the message shown:
Anything abnormal or worth adding:

Round 2: copy the block above; write "not tested" if there was no second round.
```

This round first confirms that the data can be read and locates the candidate signals. If
fields inside the messages still need confirming, a narrower follow-up collection will be
provided. Completing this collection does not by itself mean Global automatic recording has
been verified.

## 7. Second round: message content (one round, about half an hour)

> Since 2026-11-22 client version 2026.11.03 records automatically, so this card no longer
> appears for it. The section stays for later client versions that only have a candidate profile.

Round 1 told us *which* messages matter. Round 2 records the *content* of those few messages,
so the maintainer can find where the roulette, the duty and the job sit inside them. It needs
the 1.9.1-beta.4 test build or newer (in 1.9.1-beta.3 the recording stops after the first
queue request, so a round done with it has to be repeated) and the Global client version 2026.11.03; with any other
client version the section below does not appear.

**What is different from round 1.** Round 1 stored no packet content at all. This round stores
the full content of the handful of messages named in the Global candidate profile
(`protocol-profiles/global/global.2026.11.03.candidate.json`, at most 512 bytes each). Some of
them are guesses and may turn out to be a different message than expected, so they could contain
information about your character. The file stays on your PC; nothing is uploaded, and you decide
whether to send it. See [privacy-boundary.md §5.1.1](privacy-boundary.md).

1. **Fully exit the game.** Open **Capture diagnostics → Tools → evidence steps** (the same page as
   round 1). A card titled "第二轮：确定任务、副本和职业的位置" appears at the top of the page.
2. Read the explanation, tick **我已了解并同意这一轮记录这些报文的内容** and click **开始**. The card
   switches to its running state. You do not need the maintainer tools and you do not need to
   start a round-1 collection; the two cannot run at the same time.
3. Start the game and log in. The card shows **正在记录** once the game is running. Then do these
   four things in order, ticking each one on the card when done (the ticks are only for you):
   1. Queue for any roulette that is **not** the Mentor Roulette, wait 5 seconds, then withdraw.
   2. Change job once.
   3. Fully exit the game, start it again and log in. The app keeps recording on its own after
      the restart; do not click anything in the app.
   4. Queue for the **Mentor Roulette**, finish the duty and leave it.

   Below the four items the card shows what the app believes happened: the roulette of step 1,
   the job before and after step 2, and the duty of step 4. These are guesses read from the
   recorded messages. Compare them with what you actually did; where a line is wrong or empty,
   type the real name over it (any language is fine).
4. Click **结束并导出**. The card lists the folder and the two files to send:
   `candidate-evidence-<date>-<id>.json` and the matching `.sha256`. **打开所在文件夹** opens the
   folder (`%LOCALAPPDATA%\MentorRecorder\exports` by default).
5. Email the two files privately as in section 6, subject `[GLOBAL evidence] <date> round 2`.
   The roulette, the jobs and the duty shown on the card, with your corrections, are saved inside
   the file, so you do not need to repeat them in the email. You can delete the two files after
   sending; the app's own copy of the recorded content is removed automatically after at most
   30 days.

<a id="self-calibration"></a>
## 8. After the next game update: self-calibration

Each game update renumbers the messages the app reads, so a profile only fits the client version it
was written for. On the Chinese server the app re-learns a new version on the player's own PC
("self-calibration"); test builds newer than 1.10.0-beta.1 that mention Global self-calibration in
their release notes do the same for the Global client, using the 2026.11.03 profile as the template.
Nothing has to be sent for this to work. This section is for the first Global update after such a
build is installed, so that we learn whether it works on a real update.

On the Global client the queue reply and the match popup are two different messages (on the Chinese
client they are one), so the Global template describes them separately. What the app looks for is only
the *shape* the 2026.11.03 version had: the queue reply is a 40-byte message carrying the roulette at
byte 16, the popup a 40-byte message carrying it at offset 2, and the zone change a 136-byte message
carrying the territory. If the update changes one of these shapes, self-calibration may fall back to
weaker methods or not finish at all, in which case a new release is needed; that is also worth knowing.

1. After the game update, start the app before the game, then start the game and log in. The capture
   page shows the **本机校准** card with "游戏更新了，正在重新校准". Nothing is recorded yet.
2. Queue for any roulette (the Mentor Roulette is fine), accept the popup, play the duty to the end and
   leave it. A queue that matches at once, as a tank often does, is fine, and so is teleporting or
   entering a house while you wait. Please queue yourself (not as a party member); the app learns from
   your own queue.
3. When the card says **校准待核对**, open **核对并启用**. Check each line of the short timeline (queue,
   popup, entering the duty, leaving it) against what you did and answer **对** or **错**. If every line is
   right, the app writes a profile for the new version on your PC and records from then on, in the same
   session.
4. Send a short note privately: whether the card reached **校准待核对**, whether the timeline matched
   what you did, and whether the next Mentor run appeared in **History** with the duty, your job and the
   result. If the card stays on "正在重新校准" after a whole duty, or shows a problem, attach the
   diagnostics report (**捕获诊断 → 工具 → 导出诊断报告**); it contains no packet content. Besides message
   numbers, lengths and counts, it carries the fixed list of status fields described in
   [capture-diagnostics.md](capture-diagnostics.md) §9, such as the adapter's address count and a short
   fingerprint of it, the state of each kind of outbound request, and the latest hang of the app's window
   in the last 7 days as recorded in the Windows Application event log (event number, time, app version
   and hang signature only).

If you mark a popup line **错**, the app stops trusting that message. To go on, it then needs you to
queue a different roulette once (you can withdraw right after the queue starts); it can then record
from your own queue requests instead of the popup, and keeps looking for the popup in the background.
Refusing a duty-finder popup in the game is never needed for any of this.

<a id="calibration-rehearsal"></a>
### 8.1 Rehearsal on the current version (only when asked)

Self-calibration normally waits for a game update. To try it earlier, the app can be told to treat
the current version, 2026.11.03, as if no profile existed for it. It then calibrates exactly as it
would after an update, and the profile it learns can be compared with the one that ships with the
app. This needs test build 1.10.0-beta.2 or newer, and it changes nothing for anyone who does not
create the file below. Create and delete the file only while the app is closed: the app reads it
again whenever it switches profiles, so changing it while the app runs can leave the learned profile
unused.

1. Close the app completely (right-click the tray icon → 退出).
2. Open PowerShell and run this one line. It creates the switch file in the app's data folder:

   ```powershell
   Set-Content -Path "$env:LOCALAPPDATA\MentorRecorder\calibration-rehearsal.json" -Value '{"region": "GLOBAL", "game_build": "2026.11.03.0000.0000"}'
   ```

3. Start the app, then the game, and follow steps 1–3 of section 8: the capture page shows the
   **本机校准** card, one Mentor Roulette (or any roulette) teaches the app, and after you check the
   timeline it records with the profile it learned. Runs recorded this way are ordinary runs.
4. Send privately the learned profile, the file
   `%LOCALAPPDATA%\MentorRecorder\protocol-profiles\global\global.2026.11.03.0000.0000.local.json`,
   together with the note from step 4 of section 8. The file holds the message numbers, lengths and
   field positions the app learned, how many samples it saw, and when you confirmed the timeline;
   nothing about your character, chat or the packets themselves.
   If the app offers **分享给其他玩家** afterwards, you may try it: the shared code is removed again
   after the comparison, and other players' apps never download it because they already have a
   profile for this version.
5. To turn the rehearsal off, close the app, delete the file, and start the app again:

   ```powershell
   Remove-Item "$env:LOCALAPPDATA\MentorRecorder\calibration-rehearsal.json"
   ```

   The shipped profile is used again; the learned one stays on disk unused. The switch also stops
   on its own at the next game update, because it names this one version.
