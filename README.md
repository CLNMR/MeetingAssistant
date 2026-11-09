# MeetingAssistant

A native macOS app for meetings, with two modes (switch with the toolbar, ⌘1 / ⌘2):

- **Scribe** records meetings and turns them into speaker-labelled transcripts. Everything runs locally on Apple Silicon: no cloud, no account, audio never leaves the Mac.
- **Assist** reads the live transcript together with client context from Aya and suggests the next question, keeps a running summary, and tracks attendees, open points and the playbook. It uses the local Claude Code login (your Claude subscription) and sends the transcript text, not audio, to Claude.

The app was called MeetingScribe until November 2026. It keeps the same bundle identifier, so permissions and settings carry over, and it moves `~/Library/Application Support/MeetingScribe` to `…/MeetingAssistant` on first launch.

**Scribe:**

- **Online calls (Teams, Zoom, Meet, browser):** records your microphone and the call audio as two separate tracks. Your mic is "Me", remote participants are separated automatically.
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
3. Settings › Assist: enter the Aya organization id (the part after `app.hey-aya.ai/org/` in the URL) and an org API key (`aya_key_…`, stored in the Keychain), then *Connect*. The app picks the lists *Companies*, *Contacts*, *Playbooks* and *Meetings*; each can be changed there, together with the link fields (contact → company, recap → company, recap → contacts).

**A meeting:**
1. Assist mode (⌘2): pick the client and the playbook, optionally a goal, and *Start Assist*. This starts a normal recording with live transcription.
2. Claude reads the client record, its contacts, the playbook and the last recaps, and keeps five panels up to date: the **next question** (with alternatives; *Asked*, *Other*, *Suggest now* ⇧⌘Space), a running **summary**, **open points**, the **attendees** (matched to speakers and Aya contacts) and the **playbook** checklist. Questions and summary follow the language of the meeting. Ticking items by hand is passed on to Claude. The floating live transcript window shows the next question too.
3. *Stop*: the final transcript is made as usual, then Claude drafts the **recap** from the recap list's fields. Edit it and *Upload to Aya* (uploading again updates the same record).
4. *Propose Updates*: Claude compares the client and contact records with what was said and proposes changes, each with a reason, plus new contacts for attendees not yet in Aya. Nothing is written until you tick it and *Apply*.

A recording made with Assist has an *Assist Recap* button in Scribe, to do steps 3–4 later.

**How it talks to Claude:** one `claude -p --input-format stream-json` process per meeting, with no tools, no MCP servers and none of your Claude Code settings. Every 20–60 s (Settings › Assist › Update frequency) it receives the newly finished transcript lines and answers with JSON lines that each change one panel; they are applied as they stream in, so the next question appears about 3 s after an update starts. After an hour the conversation is restarted with a summary of the panels. Each update counts against your Claude plan's usage limits; when the limit is reached, Assist pauses and retries while the recording continues. Only transcript text and the Aya context are sent to Claude, never audio. Details and measurements: [MEETINGASSIST_PLAN.md](MEETINGASSIST_PLAN.md).

## Trimming and splitting

- **Forgot to stop?** While recording, click **Stop Earlier…**, pick where the meeting ended (the time of the last speech is suggested, or go back 5–60 minutes) and everything after it is deleted.
- **Edit Recording** (scissors in a recording's toolbar): **Trim…** keeps only a chosen start–end range; **Split…** turns everything after a point into a separate recording "… (Part 2)". The playback position can be used as the cut point.
- The audio is cut sample-exactly and an existing transcript is cut with it, so speaker names are kept and nothing is transcribed again.

## Push-to-talk dictation

Hold the dictation shortcut in any app, speak, and let go. The text is transcribed locally with
Parakeet Ultra (about 0.1–0.2 s for a 10 s clip), copied to the clipboard and pasted at the cursor.

- Choose the shortcut in **Settings › Dictation**. It can be a combination such as ⌃⌥Space or modifier
  keys only, such as right ⌥ (the default) or fn + left ⌃. With modifier-only shortcuts, typing another key
  while holding them cancels the dictation, so ⌥ shortcuts such as ⌥L for @ keep working.
- Pasting into other apps and detecting modifier-only shortcuts both need **Accessibility** permission.
  Grant it from the Dictation tab. Without it, a key combination still works, but the text is only copied.
- Taps shorter than 0.3 s are ignored.

## How it works

| Stage | Model / API | Runs on |
|---|---|---|
| Call audio capture | Core Audio process tap (macOS 14.2+), own process excluded, follows output-device changes | – |
| Mic capture | AVAudioEngine, any input device | – |
| Speech detection | Silero VAD | Neural Engine |
| Transcription (default) | NVIDIA Parakeet TDT 0.6B "Ultra", 25 European languages | Neural Engine |
| Transcription (optional) | Qwen3-ASR 1.7B + Qwen3 forced aligner, 30 languages | GPU (MLX) |
| Speaker diarization | NVIDIA Nemotron 3 Diarization, up to 8 speakers (offline + streaming) | Neural Engine |
| Voice matching ("Me", known people) | CAM++ speaker embeddings | Neural Engine |

Libraries: [FluidAudio](https://github.com/FluidInference/FluidAudio) (Parakeet, Nemotron 3, Silero, CAM++) and [speech-swift](https://github.com/soniqo/speech-swift) (Qwen3-ASR).

Pipeline per recording: VAD → chunks of up to 14 s → ASR with word timestamps → Nemotron 3 on the tracks that need it → speaker embeddings → clusters merged when nearly identical or above the participant count → "Me" by track (Call) or voiceprint (In person/Hybrid) → words assigned to speakers → echo of the call removed from the mic track → turns.

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

```bash
.build/release/msctl import mic.wav system.wav call     # -> session id
.build/release/msctl transcribe <id> [parakeet|qwen] [auto|german|english]
.build/release/msctl export <id> md
.build/release/msctl livetest mic.wav system.wav call   # simulate live mode at 4x speed
.build/release/msctl taptest 5 --mute                   # check system-audio capture
.build/release/msctl trim <id> <start> <end>            # keep only start..end (seconds)
.build/release/msctl split <id> <seconds>               # split into two recordings
```

Assist and Aya (`AYA_API_KEY` overrides the Keychain):

```
.build/release/msctl aya setup <orgId>                  # connect, pick default lists, read fields
.build/release/msctl aya list clients|playbooks         # reload a picker list
.build/release/msctl aya context <clientId> [playbookId]   # what Claude is told before a meeting
.build/release/msctl assist check                       # Claude Code installed and logged in?
.build/release/msctl assist replay <id> --client <clientId> [--playbook <id>] [--speed 4] [--minutes 5] [--save file]
.build/release/msctl assist live <id> --client <clientId> [--minutes 3]   # real audio → live transcriber → Claude
.build/release/msctl assist recap|crm <id> [--record file] # draft the recap / CRM proposal (prints only)
```

Set `MEETINGASSISTANT_HOME=/some/dir` to keep test data out of the app's storage.

## Known limitations

- Speaker separation works best with up to 8 speakers and clear turn-taking; heavy crosstalk lowers accuracy.
- Live speaker labels are provisional; the final transcript replaces them.
- If System Audio Recording permission is denied, the call track is silent and only your side is transcribed.
