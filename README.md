# MeetingAssistant

A native macOS app for meetings, with two modes (switch with the toolbar, ⌘1 / ⌘2):

- **Scribe** records meetings and turns them into speaker-labelled transcripts. Everything runs locally on Apple Silicon: no cloud, no account, audio never leaves the Mac.
- **Assist** reads the live transcript together with the meeting's context from Aya (planned meeting, template, client, project) and suggests the next question, keeps a running summary, and tracks attendees, open points and the template's checklist. It uses the local Claude Code login (your Claude subscription) and sends the transcript text, not audio, to Claude.

The app was called MeetingScribe until November 2026. It keeps the same bundle identifier, so permissions and settings carry over, and it moves `~/Library/Application Support/MeetingScribe` to `…/MeetingAssistant` on first launch.

**Scribe:**

- **Online calls (Teams, Zoom, Meet, browser):** records your microphone and the call audio as two separate tracks. Remote participants are separated automatically, and so are people sitting next to you at your microphone; the main voice on the mic is "Me".
- **In-person meetings:** records the microphone only and separates the speakers in the room. A one-time voice sample lets the app recognise you.
- **Hybrid:** a call with several people in the room. Both tracks are diarized.
- **Live transcript** while recording, then an accurate final transcript a few seconds after you stop.
- Rename speakers once and the app remembers their voices for future meetings.
- Click any line to play the audio from there. Export Markdown, plain text, SRT subtitles or JSON.

## Install

Requirements: Apple Silicon Mac, macOS 15 or newer, Xcode 16+ command-line tools.

```bash
./scripts/build_app.sh --install     # builds, signs and copies to /Applications
```

The script installs the Metal toolchain on first run if it is missing. Without `--install` the app is left in `build/MeetingAssistant.app`.

On first launch click **Download Models** (about 1.5 GB, one time). On the first recording macOS asks for two permissions:

1. **Microphone**.
2. **System Audio Recording** (needed for Call and Hybrid mode). If you dismissed it, enable MeetingAssistant under System Settings › Privacy & Security › Screen & System Audio Recording › System Audio Recording Only.

For in-person meetings, open **Settings › Voices** and record a 20-second voice sample.

## Using it

1. Pick **Call**, **In person** or **Hybrid**. Optionally set a title and the number of participants (helps in rooms).
2. Press **Start Recording** (⇧⌘R, or from the menu-bar icon).
3. Press **Stop**. The full transcript is created in the background; a sound plays when it is ready.
4. Click a speaker chip to rename it, mark it as you, or merge two speakers. Right-click a line to move it to another speaker.

Existing recordings can be transcribed with **Import Audio** (⌘I): one file for an in-person meeting, or two files (mic, then call audio) for a call.

Recordings live in `~/Library/Application Support/MeetingAssistant/Sessions/<id>/` as `mic.caf`, `system.caf`, `session.json` and `transcript.json`.

## Floating live transcript

File › Floating Live Transcript (⇧⌘L, also in the menu bar) opens a small always-on-top
window that can be moved and resized freely and stays visible on every Space and over
full-screen apps. While it is open, MeetingAssistant listens to the microphone and the
call audio and shows a live transcript **without saving anything**. The ⏺ button starts
a real recording (the window keeps showing its live transcript, marked with a red dot),
⏸ pauses listening, and ⋯ has Copy, Clear and text size. Closing the window stops
listening; it reopens at the next launch if it was open. If your microphone is a Bluetooth
headset, the window listens through the Mac's built-in microphone instead: opening a
headset's mic switches it into call mode, which makes music sound bad. The transcriber is replaced
every ~10 minutes so memory stays flat when it runs all day.

## Assist

Assist turns a recording into a live sales/meeting copilot and writes the results back into [Aya](https://hey-aya.ai).

**Setup (once):**
1. Install [Claude Code](https://claude.com/code) and run `claude` once in Terminal to log in with your Claude account. Assist uses that login (your subscription), not an API key.
2. In Aya, switch on *API documentation* in the organization settings (the app reads each list's fields from it).
3. Settings › Assist: enter the Aya organization id (the part after `app.hey-aya.ai/org/` in the URL) and an org API key (`aya_key_…`, stored in the Keychain), then *Connect*. The app picks the lists *Companies*, *Contacts*, *Projects*, *Meeting templates*, *Meetings* and *Members*; each can be changed there, together with the link fields (contact → company; meeting → client, project, template and team members). A list follows its record type when it moves to another workspace.

**A meeting:**
1. Assist mode (⌘2): either pick a meeting planned in Aya (today and the next 14 days; its client, project and template are taken over), or choose a template and/or client and/or project yourself. Optionally a goal, then *Start Assist*. This starts a normal recording with live transcription.
2. Claude reads the planned meeting, the client record and its contacts, the project, the template (agenda and questions become the checklist) and the last meetings with that client or project, and keeps six panels up to date: the **next question** (with alternatives; *Asked*, *Other*, *Suggest now* ⇧⌘Space), a running **summary**, **open points**, the **attendees** (matched to speakers and Aya contacts) the template's **checklist** and its **tables**. Markdown tables in a template (e.g. "which modules do you use, in which system") are filled in as the answers come up; the *Tables* card shows progress and opens the full table for corrections. Questions and summary follow the language of the meeting. Ticking items by hand is passed on to Claude. The floating live transcript window shows the next question too.
3. *Stop*: the final transcript is made as usual, then Claude drafts the **recap** from the meeting list's fields. Choose where it goes: the planned meeting, another Aya meeting from that day (for example one a call bot created), or a new meeting. For an existing meeting its current values are shown and fields that already have content are only overwritten when ticked; links (client, project, template), date and type are filled only where empty, and team members who attended are added as participants. Saving again updates the same meeting. The template's tables are completed from the final transcript, can be edited in the review, and are saved under a heading with the template's name into the field chosen in Settings (automatic: a field named like Protokoll/Leitfaden/Notes, else the summary); tables with more than 30 rows keep only the rows that were answered.
4. *Propose Updates*: Claude compares the client and contact records with what was said and proposes changes, each with a reason, plus new contacts for attendees not yet in Aya. Nothing is written until you tick it and *Apply*.

A recording made with Assist has an *Assist Recap* button in Scribe, to do steps 3–4 later.

*What did I miss?* in the live view summarizes the last 2, 5 or 10 minutes (Claude Haiku, a few seconds), with questions directed at you first. Deadlines in the recap that nobody said ("Angebot schicken (bis Freitag)") are taken out again, and the review says which.

**How it talks to Claude:** one `claude -p --input-format stream-json` process per meeting, with no tools, no MCP servers and none of your Claude Code settings. Every 20–60 s (Settings › Assist › Update frequency) it receives the newly finished transcript lines and answers with JSON lines that each change one panel; they are applied as they stream in, so the next question appears about 3 s after an update starts. After an hour the conversation is restarted with a summary of the panels. Each update counts against your Claude plan's usage limits; when the limit is reached, Assist pauses and retries while the recording continues. Only transcript text and the Aya context are sent to Claude, never audio. Details and measurements: [MEETINGASSIST_PLAN.md](MEETINGASSIST_PLAN.md).

## Trimming and splitting

- **Forgot to stop?** While recording, click **Stop Earlier…**, pick where the meeting ended (the time of the last speech is suggested, or go back 5–60 minutes) and everything after it is deleted.
- **Edit Recording** (scissors in a recording's toolbar): **Trim…** keeps only a chosen start–end range; **Split…** turns everything after a point into a separate recording "… (Part 2)". The playback position can be used as the cut point.
- The audio is cut sample-exactly and an existing transcript is cut with it, so speaker names are kept and nothing is transcribed again.

## Push-to-talk dictation

Hold a dictation shortcut in any app, speak, and let go. The text is transcribed locally with
Parakeet Ultra (about 0.1–0.4 s for a 10 s clip) and pasted at the cursor.

- **Two modes.** *Word for word* (right ⌥ by default) runs entirely on the Mac. *Scribe+* (right ⌥ + right ⌘)
  also has Claude turn the speech into clean written text: misheard words, filler words and false starts are fixed,
  self-corrections are applied ("Montag, nein Dienstag" → "Dienstag"), punctuation is fixed, and the tone follows the
  text around the cursor. Adding ⌘ while a plain dictation runs switches it to Scribe+. Scribe+ uses the local Claude
  Code login (your subscription) with Sonnet (2–4 s, the default) or Haiku (about 1 s, changes words more often). With
  *Keep Claude ready* a `claude` process waits in the background, which saves about 3 s. If Claude fails, takes longer
  than 8 s, or its answer is not an edit of what was said (a reply, a refusal), the word-for-word text is pasted
  instead. Each dictation's timings are written to the system log (`log show --last 1h --predicate
  'subsystem == "de.local.MeetingScribe" AND category == "Dictation"'`).
- **Context for Scribe+.** With *Use the text around the cursor as context*, Scribe+ also sends Claude the app's name
  and up to 1,200 characters before and 300 after the cursor. Without it, only the dictated text (and matching
  vocabulary) is sent.
- **Fits where you type.** Spaces and capitals follow the text around the cursor (also mid-sentence), and a final
  period is dropped when the sentence goes on. Password fields are never read.
- **Clipboard.** The text stays on the clipboard after pasting. If you click into another field before it arrives,
  it is only copied, not pasted.
- **Nothing gets lost.** The recording is kept until its text has arrived. If transcription fails, it stays for 24 hours
  under *Unfinished Dictations* in the menu-bar menu and can be transcribed again. The retry cleans up the text like a
  normal dictation (Scribe+ again for a Scribe+ dictation, without the text around the cursor) and copies it to the
  clipboard; it is not pasted. *Copy Last Dictation* is there too.
- **No invented text.** A shortcut pressed by accident, a cough or a click gives no "Thank you." or "Moment.": taps
  shorter than 0.3 s are ignored silently, and presses shorter than 3 s without at least 0.15 s of continuous voice
  show "No speech detected" before the model runs, and known subtitle credits
  ("Untertitel im Auftrag des ZDF") never come through, in dictations or transcripts.
- **Spoken forms.** "ähm" and "äh" are dropped, "16 Uhr 45" becomes "16:45 Uhr", and "max punkt mustermann at
  beispiel punkt de" becomes an e-mail address.
- Music or a podcast is muted while you speak (not during a recording) and comes back right after.
- Pasting, reading the text around the cursor and modifier-only shortcuts need **Accessibility** permission
  (Settings › Dictation). Without it, the text is only copied.

## Vocabulary

Settings › Vocabulary holds names and terms spelled your way. They apply to dictations and to final transcripts:
the same letters ("hubspot", "Hub Spot" → "HubSpot") and, optionally, words that sound very much alike
("Clawd" → "Claude", "Musterman" → "Mustermann"). A correctly spelled German or English word is never turned into a
name, so a contact called Klein does not capitalize every "klein". Replacements are fixed rules
("n acht n" → "n8n"). With Aya connected, company names (without GmbH, AG …), contacts, team members and short
project names are added automatically and refreshed twice a day.

## How it works

| Stage | Model / API | Runs on |
|---|---|---|
| Call audio capture | Core Audio process tap (macOS 14.2+), own process excluded, follows output-device changes | – |
| Mic capture | Core Audio HAL input unit, any input device | – |
| Speech detection | Silero VAD | Neural Engine |
| Transcription (default) | NVIDIA Parakeet TDT 0.6B "Ultra", 25 European languages | Neural Engine |
| Transcription (optional) | Qwen3-ASR 1.7B + Qwen3 forced aligner, 30 languages | GPU (MLX) |
| Speaker diarization | NVIDIA Nemotron 3 Diarization, up to 8 speakers (offline + streaming) | Neural Engine |
| Voice matching ("Me", known people) | CAM++ speaker embeddings | Neural Engine |

Libraries: [FluidAudio](https://github.com/FluidInference/FluidAudio) (Parakeet, Nemotron 3, Silero, CAM++) and [speech-swift](https://github.com/soniqo/speech-swift) (Qwen3-ASR).

Pipeline per recording: VAD → chunks of up to 14 s → ASR with word timestamps → Nemotron 3 on the tracks that need it → speaker embeddings → clusters merged when nearly identical or above the participant count → "Me" by voiceprint (in Call mode, without a match, the main voice on the mic) → words assigned to speakers → echo of the call removed from the mic track → turns.

Live mode cuts each track into utterances with Silero VAD, transcribes each with Parakeet (≈150× faster than real time), and labels speakers with streaming Nemotron 3 about 3 s behind.

Source layout:

```
Sources/MeetingAssistantCore/Capture   system-audio tap, mic, resampling, aligned track writer
Sources/MeetingAssistantCore/Pipeline  models, VAD chunking, diarization, speaker ID, merger, live mode, export
Sources/MeetingAssistant              SwiftUI app (recorder, transcript view, settings, menu-bar extra)
Sources/msctl                         command-line tool for testing (record, import, transcribe, livetest, asr)
Tests/                                unit tests for the pure logic (swift test)
eval/                                scripts used for the model comparison below
```

## Measured results (M4 Pro, 48 GB)

Word error rate on 60 real German utterances from Google FLEURS:

| Engine | WER | Speed |
|---|---|---|
| Parakeet Ultra | 4.05 % | ~40× real time incl. loading |
| Qwen3-ASR 1.7B | 4.19 % | ~10× real time incl. alignment |

Full pipeline on a synthetic 5-person German/English meeting (TTS voices, ground truth known):

| Scenario | Parakeet WER | Qwen3 WER | Speakers |
|---|---|---|---|
| Call (mic + call audio) | 3.3 % | 8.1 % | all correct except two very similar synthetic male voices merged |
| In person (simulated room, RT60 0.5 s) | 13.8 % | 17.1 % | same; "Me" found by voiceprint |

Diarization on the same test: Nemotron 3 15 % DER vs. pyannote Community-1 33 % (room), 23 % vs. 43 % (call track).
A 14-minute recording is fully processed in about 9 seconds using 340 MB of memory.

Caveat: the meeting test uses synthetic voices. Real meetings with crosstalk and distant speakers will score worse; the FLEURS numbers are real speech.

## Command-line tool

`msctl` without arguments prints all commands.

```bash
.build/release/msctl devices | perm                     # input devices, microphone permission
.build/release/msctl record 30 call [--mute]            # record a test session
.build/release/msctl import mic.wav system.wav call     # -> session id
.build/release/msctl transcribe <id> [parakeet|qwen] [auto|german|english]
.build/release/msctl enroll voice.wav [name]            # your voiceprint
.build/release/msctl export <id> md|txt|srt|json
.build/release/msctl livetest mic.wav system.wav call   # simulate live mode at 4x speed
.build/release/msctl taptest 5 --mute                   # check system-audio capture
.build/release/msctl mictest 5 [default]                # check the mic the live listener uses
.build/release/msctl trim <id> <start> <end>            # keep only start..end (seconds)
.build/release/msctl split <id> <seconds>               # split into two recordings
.build/release/msctl asr parakeet|qwen auto|german|english <files...>   # plain ASR, one line per file
.build/release/msctl memtest <audio>                    # GPU memory with Qwen3 loaded and unloaded
.build/release/msctl dictate <audio> [seconds] [--plus] [--before "text"] [--app Mail] [--model sonnet|haiku] [--warm 2.5]   # dictation path with timings
.build/release/msctl vocab <id>                         # what the vocabulary would change in a transcript
```

Assist and Aya (`AYA_API_KEY` overrides the Keychain):

```
.build/release/msctl aya setup <orgId>                  # connect, pick default lists, read fields
.build/release/msctl aya list clients|projects|templates|members   # reload a picker list
.build/release/msctl aya upcoming [days]                  # planned meetings
.build/release/msctl aya context [--meeting id] [--client id] [--project id] [--template id]   # what Claude is told
.build/release/msctl assist check                       # Claude Code installed and logged in?
.build/release/msctl assist replay <id> [--meeting|--client|--project|--template <id>] [--speed 4] [--minutes 5] [--model opus|sonnet|haiku] [--save file]
.build/release/msctl assist live <id> [--meeting|--client|--project|--template <id>] [--minutes 3]   # real audio → live transcriber → Claude
.build/release/msctl assist recap|crm <id> [--record file] # draft the recap / CRM proposal (prints only)
.build/release/msctl assist tables <id> [--record file]    # complete the template's tables from the transcript
```

Set `MEETINGASSISTANT_HOME=/some/dir` to keep test data out of the app's storage.

## Privacy

What leaves the Mac, and when: [docs/privacy.md](docs/privacy.md). Reporting a security problem: [SECURITY.md](SECURITY.md).

## Known limitations

- Speaker separation works best with up to 8 speakers and clear turn-taking; heavy crosstalk lowers accuracy.
- Live speaker labels are provisional; the final transcript replaces them.
- If System Audio Recording permission is denied, the call track is silent and only your side is transcribed.
