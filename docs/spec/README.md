# MeetingAssistant specification

This spec describes what the app does and how, so that it can be rebuilt on another platform (a Windows version is planned) and kept in sync with the macOS app. Each section first describes behaviour independent of the platform, then has a **Mac implementation** note (files, types, frameworks). Where the Mac version relies on an Apple-only API, a **Windows notes** line names the equivalent.

The Mac code is the reference implementation. Where the spec and the code disagree, the code wins: fix the spec.

| File | Contents |
|---|---|
| [01-recording.md](01-recording.md) | Meeting modes, audio capture, live and final transcription, speakers, sessions on disk, Scribe UI, trimming, export |
| [02-assist-core.md](02-assist-core.md) | Assist engine: Claude CLI, ticks, prompts, state, recap, CRM proposals, tables, Aya API and configuration |
| [03-assist-ui.md](03-assist-ui.md) | Assist screens: setup, live view, review, past meetings, import, person cards, settings, theme |
| [04-dictation-settings.md](04-dictation-settings.md) | Dictation and Scribe+, text cleanup, vocabulary, hotkeys and paste, settings, msctl, privacy, build |
| [CHANGELOG.md](CHANGELOG.md) | What changed when; the input for porting |
| [windows-sync.md](windows-sync.md) | How far the Windows app has caught up |

## The product in one page

MeetingAssistant is a desktop app for people who spend their day in meetings. It has three parts.

**Scribe** records meetings and turns them into transcripts with speaker labels. Everything runs locally: no cloud, and audio never leaves the computer.
- **Call:** an online call (Teams, Zoom, Meet, browser). The mic and the call audio are recorded as two tracks.
- **In person:** a meeting in a room. Only the mic is recorded, and the people in the room are separated.
- **Hybrid:** a call with several people in the room. Both tracks are separated by speaker.

A live transcript shows while recording. A few seconds after stopping, a more accurate final transcript replaces it. Speakers can be renamed, and the app learns their voices for later meetings. Recordings can be trimmed, split and exported.

**Assist** is a live copilot for sales and customer meetings, built on Aya, a no-code CRM/workspace platform (app.hey-aya.ai). Before a meeting the user picks a planned Aya meeting, or a client, project and meeting template. During the meeting, Claude reads the live transcript together with that Aya context. It keeps these up to date:
- the next question to ask;
- a summary with one paragraph per topic;
- open points;
- attendees, matched to speakers and Aya contacts;
- the template's checklist and fill-in tables.

Afterwards Claude drafts a recap in the fields of the Aya meetings list. The user reviews it and saves it to a new or existing Aya meeting. Claude also proposes CRM updates for the client and its contacts, which the user approves one by one. Past Assist meetings can be opened again and edited, re-uploaded or deleted. Recordings made elsewhere can be imported and go through the same flow.

**Dictation** works in any app. Hold a hotkey, speak, release, and the text is pasted at the cursor.
- **Word for word** runs fully locally.
- **Scribe+** also has Claude rewrite the speech into clean written text that fits the text around the cursor.

A vocabulary (names from Aya plus the user's own terms) fixes spelling in dictations and transcripts.

**Claude is always used through the local Claude Code CLI**, which runs on the user's Claude subscription. The app never calls the Anthropic API directly and never asks for an API key. Only text (transcript, Aya context, dictation text) is sent to Claude, never audio.

## Architecture

```
┌──────────────────────── App (UI) ──────────────────────────────┐
│ Scribe views · Assist views · Dictation · Settings · menu bar   │
│ AppState (sessions, queue) · AssistController · DictationCtrl   │
└───────────────┬────────────────────────────────┬────────────────┘
                │                                │
┌───────────────▼──────────── Core library ──────▼────────────────┐
│ Capture      mic, system audio, aligned track writer            │
│ Pipeline     VAD, ASR, diarization, speaker ID, merger, live    │
│              transcriber, session store, export, edit           │
│ Assist       engine (tick loop), prompts, state, recap, tables, │
│              Claude CLI wrapper, speakers                       │
│ Aya          HTTP client, schema, config (list mapping), service│
│ Text         vocabulary, spoken forms, invented text, Scribe+   │
└─────────────────────────────────────────────────────────────────┘
        │ local models (ASR, VAD, diarization, embeddings)
        │ `claude` CLI subprocess (subscription)
        │ Aya REST API (org API key)
```

- **Local models:**
  - VAD: Silero.
  - Speech recognition: NVIDIA Parakeet TDT 0.6B, with Qwen3-ASR as an option.
  - Speaker separation: NVIDIA Nemotron 3 Diarization (offline and streaming).
  - Speaker embeddings: CAM++.

  On the Mac they run on the Neural Engine and GPU through FluidAudio and speech-swift. On Windows the same model families are available as ONNX (via sherpa-onnx).
- **Data on disk:** all data lives in one app data folder: sessions, dictations, vocabulary, speaker library, voiceprint and Aya cache. It is described in 01-recording and 04-dictation-settings. Files are JSON. A port must keep the same formats so the two apps can share test fixtures.
- **Command-line tool:** `msctl` runs most features headless for testing (record, import, transcribe, live test, Assist replay, Aya).

## Terms

| Term | Meaning |
|---|---|
| Session | One recording: a folder with audio tracks, `session.json`, `transcript.json` and, for Assist meetings, `assist.json` |
| Track | `mic` or `system` (call audio), recorded separately and time-aligned |
| Live line | A line of the live transcript; its speaker label is raw (`me`, `mic-2`, `system-1`) or unknown |
| Turn | A line of the final transcript: one speaker, words with timestamps |
| Me | The user. In a call, the main voice on the mic; otherwise found by the voiceprint |
| Tick | One Assist update: new transcript lines sent to Claude, Claude's changes applied to the panels |
| Recap | The meeting notes Claude drafts after the meeting, field by field, for the Aya meetings list |
| Slot | One of the Aya lists the app uses: companies, contacts, projects, meetings, meeting templates, members |
