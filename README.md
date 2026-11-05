# MeetingScribe

A native macOS app that records meetings and turns them into speaker-labelled transcripts. Everything runs locally on Apple Silicon: no cloud, no account, audio never leaves the Mac.

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

The script installs the Metal toolchain on first run if it is missing. Without `--install` the app is left in `build/MeetingScribe.app`.

On first launch click **Download Models** (about 1.5 GB, one time). On the first recording macOS asks for two permissions:

1. **Microphone**.
2. **System Audio Recording** (needed for Call and Hybrid mode). If you dismissed it, enable MeetingScribe under System Settings › Privacy & Security › Screen & System Audio Recording › System Audio Recording Only.

For in-person meetings, open **Settings › Voices** and record a 20-second voice sample.

## Using it

1. Pick **Call**, **In person** or **Hybrid**. Optionally set a title and the number of participants (helps in rooms).
2. Press **Start Recording** (⇧⌘R, or from the menu-bar icon).
3. Press **Stop**. The full transcript is created in the background; a sound plays when it is ready.
4. Click a speaker chip to rename it, mark it as you, or merge two speakers. Right-click a line to move it to another speaker.

Existing recordings can be transcribed with **Import Audio** (⌘I): one file for an in-person meeting, or two files (mic, then call audio) for a call.

Recordings live in `~/Library/Application Support/MeetingScribe/Sessions/<id>/` as `mic.caf`, `system.caf`, `session.json` and `transcript.json`.

## Floating live transcript

File › Floating Live Transcript (⇧⌘L, also in the menu bar) opens a small always-on-top
window that can be moved and resized freely and stays visible on every Space and over
full-screen apps. While it is open, MeetingScribe listens to the microphone and the
call audio and shows a live transcript **without saving anything**. The ⏺ button starts
a real recording (the window keeps showing its live transcript, marked with a red dot),
⏸ pauses listening, and ⋯ has Copy, Clear and text size. Closing the window stops
listening; it reopens at the next launch if it was open. If your microphone is a Bluetooth
headset, the window listens through the Mac's built-in microphone instead: opening a
headset's mic switches it into call mode, which makes music sound bad. The transcriber is replaced
every ~10 minutes so memory stays flat when it runs all day.

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
Sources/MeetingScribeCore/Capture    system-audio tap, mic, resampling, aligned track writer
Sources/MeetingScribeCore/Pipeline   models, VAD chunking, diarization, speaker ID, merger, live mode, export
Sources/MeetingScribe                SwiftUI app (recorder, transcript view, settings, menu-bar extra)
Sources/msctl                        command-line tool for testing (record, import, transcribe, livetest, asr)
Tests/                               unit tests for the pure logic (swift test)
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

Set `MEETINGSCRIBE_HOME=/some/dir` to keep test data out of the app's storage.

## Known limitations

- Speaker separation works best with up to 8 speakers and clear turn-taking; heavy crosstalk lowers accuracy.
- Live speaker labels are provisional; the final transcript replaces them.
- If System Audio Recording permission is denied, the call track is silent and only your side is transcribed.
