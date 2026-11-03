# Global Server Testing and Evidence Collection

This guide asks you to help confirm three things on the Global (international) version of
FINAL FANTASY XIV: whether the Global client is recognised, whether its game data can be
decoded, and which signals appear from queueing until you leave the duty. Please do one full
round first and send the files back; once the data is confirmed usable, a second round
follows.

Use the complete "Global server test build" that is provided to you separately. If you have
not received it yet, do not start, and do not substitute a public release. Automatic
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
   `mentor_diagnostics_<date-time>.json`; keep whatever name it generated. If logs are
   needed, **Open logs** on the `collector-*.log` row opens their folder.
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
- **Only for startup or collection problems: the `collector-*.log` diagnostic logs from
  the same time.** Click **Open logs** in step ④, or reach the database folder via
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
