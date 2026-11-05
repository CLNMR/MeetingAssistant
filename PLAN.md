# Local Meeting Transcriber for macOS — Research & Plan

> **Status (2026-11-01): implemented.** See README.md for usage and measured results.
> Phase 0 changed two decisions from the original plan:
> - **Default ASR is Parakeet Ultra, not Qwen3-ASR.** On 60 real German FLEURS utterances both scored ~4 % WER
>   (4.05 vs 4.19), Parakeet is ~4× faster on the Neural Engine and was better in the meeting simulation.
>   Qwen3-ASR 1.7B remains selectable.
> - **Voice matching uses CAM++ embeddings** (FluidAudio), because Nemotron 3 has no embeddings and CAM++ works on
>   any-length audio. "Me" requires similarity ≥ 0.5 *and* a 0.08 margin over the next speaker; a fixed threshold
>   alone produced false positives on a long real recording.
> - Live mode uses Silero-VAD utterances + Parakeet instead of FluidAudio's sliding-window manager (simpler, accurate
>   timestamps), with streaming Nemotron 3 (`fast32`) for live speaker labels.

Target machine: MacBook Pro, Apple M4 Pro, 48 GB RAM, macOS 26.6.2, Xcode 27.2.
Goal: a small standalone Mac app with a simple GUI that records meetings, transcribes them with the best model that runs locally, and labels speakers. Two situations must work:

- **Online call** (Teams/Zoom/Meet): microphone **and** system audio are captured.
- **In-person meeting**: everyone in the room is picked up by the microphone only.

Offline processing after the meeting is the baseline; live transcription is a bonus.

Assumption: meetings are mostly German and English, sometimes mixed. Model choice below is driven by German accuracy; adjust if that is wrong.

---

## 1. Research summary

### 1.1 Speech-to-text models that run locally on Apple Silicon (Nov 2026)

| Model | Runtime on Mac | Languages | Accuracy | Speed on M4/M5 Pro | Notes |
|---|---|---|---|---|---|
| **Qwen3-ASR 1.7B** | MLX (GPU); Swift via `speech-swift`, Python via `mlx-qwen3-asr` | 30 languages incl. German | Best open model on German: FLEURS DE WER 6.8 % (8-bit) vs Parakeet 12.3 %; English Open-ASR-Leaderboard 5.76 % | RTF ≈ 0.03 (1 h audio ≈ 2 min); ≈ 2.7 GB RAM | Word timestamps via forced aligner; Apache 2.0 |
| **Parakeet TDT 0.6B v3 / Parakeet Ultra** | CoreML on the Neural Engine via FluidAudio | 25 European languages incl. German | English leaderboard 6.32 %; German weaker (FLEURS DE ≈ 12 %) | ≈ 150× realtime; ≈ 1 GB RAM | Token timestamps, true streaming, very mature Swift package |
| Whisper large-v3-turbo | CoreML via WhisperKit / Argmax OSS SDK | 99 languages | FLEURS DE ≈ 4.05 on one Mac test but generally behind Qwen3 on German; slower | ≈ 12× realtime | Safe fallback, biggest ecosystem |
| Cohere Transcribe 2B, Granite Speech 4.1, Canary-Qwen | MLX ports exist (speech-swift has Cohere Transcribe) | 14 / 6 / EN | Top of English leaderboard (5.3–5.6 %) | — | No convincing German data; keep as optional engines |
| Voxtral Mini 3B | MLX via speech-swift | 8 languages incl. German | Leaderboard 8.4 % (realtime variant) | RTF 0.074 | Not better than Qwen3 on German |
| Apple SpeechAnalyzer (macOS 26 built-in) | Native | Limited set | Beats Whisper small on English; no German benchmark found | Very fast | Zero-dependency fallback, not best-in-class |

Decision: **Qwen3-ASR 1.7B for the final (offline) transcript, Parakeet (CoreML) for the live/preview transcript.** Both fit easily in 48 GB.

### 1.2 Speaker diarization on Apple Silicon

| Pipeline | Runtime | Max speakers | Mode | DER on AMI meetings | Notes |
|---|---|---|---|---|---|
| **pyannote Community-1 (offline)** | FluidAudio (CoreML, ANE) | unlimited (clustering) | offline | 10.6 % | Recommended default by FluidAudio; 323× realtime; CC BY 4.0 models |
| **NVIDIA Nemotron 3 Diarization** | FluidAudio (merged 2026-09-23), speech-swift, `nemotron-diar-mlx` (Python) | 8 | offline + streaming | 9.4–9.8 % (1 s to 30 s latency) | Best accuracy and real streaming; NVIDIA Open Model License (OpenMDW) |
| LS-EEND | FluidAudio | 10 | streaming | 20.7 % | Fast, lower accuracy |
| Sortformer (4-speaker) | FluidAudio / speech-swift | 4 | streaming | 26–32 % | Superseded by Nemotron 3 |
| Argmax SpeakerKit (pyannote 4) | Argmax OSS SDK | unlimited | offline | not published | Alternative if we end up on WhisperKit |

Decision: **Nemotron 3 (offline profile) as primary, pyannote Community-1 as fallback/comparison.** Both ship in FluidAudio.

Key design trick for calls: record **mic and system audio as separate tracks**. In a pure call the mic track is "me" by definition and needs no diarization; diarization only runs on the system track (remote participants). This removes the hardest case (identifying the local speaker) entirely.

In-person meetings have no such shortcut: the mic track contains every participant, so the diarizer runs on the mic track. Two things make this work well:
- **Voice enrollment for "Me"**: the user records 20–30 s of their own voice once. The Community-1 pipeline produces a 256-d centroid per speaker; the cluster closest to the enrolled centroid is labelled "Me" automatically. Nemotron 3 does not produce embeddings, so enrollment and speaker memory use the Community-1/WeSpeaker embedding model even when Nemotron 3 does the segmentation.
- **Speaker-count hint**: the user can enter the number of participants; Community-1 accepts speaker bounds, which noticeably reduces over- or under-splitting in rooms.

Far-field room audio is harder than call audio (reverb, distance, overlapping talk). The MacBook Pro mic array is acceptable for a small meeting room; an external USB conference mic is the single biggest upgrade for larger rooms and the app should let the user pick any input device.

### 1.3 Capturing system audio on macOS

- **Core Audio process taps** (`CATapDescription` + `AudioHardwareCreateProcessTap`, macOS 14.2+) are the official, driver-free way. A global tap on the default output device, attached to a private aggregate device, delivers everything that is played through the speakers/headphones, including Teams, Zoom and browser calls. Requires `NSAudioCaptureUsageDescription` in Info.plist; macOS shows a one-time "System Audio Recording" permission prompt. Reference implementations: AudioTee (Swift, MIT), AudioCap.
- Taps do **not** include the microphone. Mic is captured separately with `AVAudioEngine` (needs `NSMicrophoneUsageDescription`). The two streams have independent clocks, so we timestamp both against `mach_absolute_time` at start and resample to 16 kHz mono.
- Per-process taps (PID-based) are possible but brittle for browsers; a global tap excluding our own process is simpler and sufficient.
- Echo: if the user is on laptop speakers, the mic also hears the remote side. Mitigation: recommend headphones in the UI, and de-duplicate overlapping text between tracks in the merge step (phase 2.4).
- Virtual devices (BlackHole) are not needed and are explicitly avoided.

### 1.4 Swift package options

| Package | Stars / last push | License | Gives us |
|---|---|---|---|
| **FluidAudio** (FluidInference) | 3.2k / 2026-10-31, v0.18.0 | Apache 2.0 | Parakeet v3/Ultra CoreML, streaming ASR manager, Silero VAD, pyannote Community-1, Nemotron 3 diarization, LS-EEND |
| **speech-swift** (soniqo) | 1.3k / 2026-10-30, v0.0.29 | Apache 2.0 | Qwen3-ASR (MLX), Whisper, Parakeet, Cohere Transcribe, Voxtral, Nemotron 3 + Community-1 diarization; macOS 15+ |
| Argmax OSS SDK (WhisperKit + SpeakerKit) | v1.1.1 2026-10-08 | Apache 2.0 (Parakeet/Sortformer are Pro-only) | Whisper CoreML + pyannote 4 diarization |

Decision: **FluidAudio** for capture-adjacent pieces (VAD, streaming Parakeet, Nemotron 3 / Community-1 diarization) and **speech-swift** for the Qwen3-ASR 1.7B accuracy pass. Both are SPM packages and coexist in one target. If the two-package setup causes trouble, speech-swift alone covers every model in the plan.

---

## 2. Architecture

Native macOS app, Swift 6 + SwiftUI, one Xcode project, no Python at runtime. Rationale: system-audio capture is a native Core Audio API, the best local models are Swift/CoreML/MLX packages, and a `.app` bundle is what "standalone" means on a Mac.

```
┌──────────────────────────────── MeetingScribe.app ────────────────────────────────┐
│  SwiftUI                                                                          │
│  ├ RecorderView   (mode: Call / In person, mic picker, levels, Record/Stop, live) │
│  ├ TranscriptView (speaker-coloured turns, rename speakers, search, export)       │
│  └ LibraryView    (past recordings + status: recorded / transcribing / done)      │
│                                                                                   │
│  Capture layer                                                                    │
│  ├ SystemAudioTap   CATapDescription → aggregate device → IOProc → 16 kHz mono    │
│  ├ MicCapture       AVAudioEngine input node → 16 kHz mono                        │
│  └ SessionWriter    two CAF/WAV tracks + session.json (start time, devices)       │
│                                                                                   │
│  Processing layer (actors, run off the main thread)                              │
│  ├ VAD              Silero (FluidAudio)                                           │
│  ├ LiveASR          Parakeet streaming (FluidAudio SlidingWindowAsrManager)       │
│  ├ FinalASR         Qwen3-ASR 1.7B 8-bit (speech-swift, MLX) with word timestamps │
│  ├ Diarizer         Nemotron 3 offline (FluidAudio); which tracks depends on mode │
│  ├ SpeakerID        Community-1 embeddings: enrolled "Me" voice, speaker memory   │
│  └ Merger           words → speaker turns; mode-specific labelling; echo de-dup   │
│                                                                                   │
│  Storage: ~/Library/Application Support/MeetingScribe/<session-id>/               │
│           mic.caf, system.caf, transcript.json, exports/                          │
│  Models:  downloaded once from Hugging Face into Application Support/models       │
└───────────────────────────────────────────────────────────────────────────────────┘
```

Meeting modes, chosen in the recorder before pressing Record:

| Mode | Tracks recorded | Diarization runs on | "Me" is identified by |
|---|---|---|---|
| **Call** (default when a call app is playing audio) | mic + system | system track | being on the mic track |
| **In person** | mic only | mic track | enrolled voice embedding; otherwise "Speaker 1..N" |
| **Hybrid** (call with several people in the room) | mic + system | both tracks | enrolled voice embedding on the mic track |

Transcript data model (JSON): `session { id, startedAt, durationSec, mode, sources[] }`, `turns[] { speaker, start, end, text, words[] { w, start, end, conf } }`, `speakers[] { id, label, track, embedding? }`.

Permissions / entitlements: `NSMicrophoneUsageDescription`, `NSAudioCaptureUsageDescription`; App Sandbox off (local tool, simpler) or on with `com.apple.security.device.audio-input` + network client for model downloads. Ad-hoc / Developer ID signing is enough for personal use.

---

## 3. Phases

### Phase 0 — Spike (≈ half a day)
Goal: de-risk the two unknowns before building UI.
1. Build a 100-line CLI from the AudioTee approach: global tap + mic, write two 16 kHz WAVs. Verify it captures a Teams/Zoom test call on macOS 26.6 and that the permission prompt appears.
2. Record a real 10-minute German/English call **and** a 10-minute in-person meeting with 3+ people (or use existing recordings).
3. Run it through: `swift run fluidaudiocli` (Parakeet Ultra), `mlx-qwen3-asr` (Qwen3 1.7B 8-bit, quickest way to try it), and FluidAudio Nemotron 3 + Community-1 diarization.
4. Decide the final engines from the spike output. Exit criteria: capture works; one ASR engine is clearly acceptable on German; diarization gives sane speaker counts on the system track.

### Phase 1 — Capture app (≈ 1–2 days)
- Xcode project, SwiftUI shell, menu-bar-free single window.
- `SystemAudioTap` and `MicCapture` as actors, `SessionWriter` writing both tracks plus `session.json`.
- UI: mode picker (Call / In person / Hybrid), input-device picker, level meters, Record/Stop, elapsed time, "use headphones" hint in Call mode.
- In-person mode records the mic only and skips the system tap entirely (no audio-capture permission prompt needed for that mode).
- Follow default-output-device changes (AirPods connect mid-call): listen to `kAudioHardwarePropertyDefaultOutputDevice` and rebuild the tap.
Deliverable: reliable two-track recordings of real calls.

### Phase 2 — Offline pipeline (≈ 2–3 days)
1. Model manager: first-run download of Qwen3-ASR 1.7B (8-bit), Parakeet Ultra, Nemotron 3, Silero VAD; progress UI; disk ≈ 4 GB.
2. `FinalASR`: VAD-segment each track, transcribe with Qwen3-ASR with word timestamps, language auto-detect per segment (handles German/English switching).
3. `Diarizer`: Nemotron 3 offline → speaker segments on the track(s) the mode requires; optional "number of participants" hint from the user (passed as speaker bounds).
4. `SpeakerID`: one-time voice enrollment screen in Settings (record 20–30 s, store the Community-1 centroid). After diarization, compute a centroid per found speaker and label the closest match "Me" if the cosine distance is below a threshold. Same mechanism later powers speaker memory across meetings.
5. `Merger`: assign each diarized word to the most-overlapping speaker; in Call mode mic-track words → "Me" directly; merge consecutive same-speaker words into turns; drop near-duplicate text that appears on both tracks within ±1 s (echo, Call/Hybrid only).
6. `TranscriptView`: colour per speaker, click-to-rename (persisted), click a turn to play that audio span, search.
7. Export: Markdown (`**Speaker:** text` with timestamps), SRT, JSON. Copy-to-clipboard.
8. Processing runs as a background task with a queue so several recordings can be transcribed after the fact.
Deliverable: record a call or an in-person meeting, press "Transcribe", get a speaker-labelled transcript. This is the MVP.

### Phase 3 — Live mode (bonus, ≈ 1–2 days)
- `LiveASR`: Parakeet streaming on both tracks in parallel, rolling text shown in `RecorderView` with ~1–2 s latency.
- Provisional speakers: Nemotron 3 streaming `fast32` profile (≈ 3 s latency, 9.5 % DER) on the system track in Call mode, on the mic track in In-person mode.
- On Stop, Phase 2 pipeline reruns and replaces the live transcript with the accurate one.
- Optional: live transcript also as a floating always-on-top panel.

### Phase 4 — Polish (as needed)
- Speaker memory across meetings using the same Community-1 centroids as the "Me" enrollment ("this voice was Anna last time"); especially valuable for recurring in-person meetings.
- Auto-start recording when a Teams/Zoom process begins playing audio.
- Summary/action items via a local LLM or the Claude API (explicitly opt-in, since the point of this app is local processing).
- Launch-at-login, Sparkle updates, notarisation if the app is ever shared.

---

## 4. Risks and mitigations

| Risk | Mitigation |
|---|---|
| Core Audio tap behaves differently on macOS 26 (sparse docs, API marked unstable by third parties) | Phase 0 spike tests it first; AudioTee/AudioCap as reference; ScreenCaptureKit audio capture as fallback |
| German accuracy of Qwen3-ASR not as good on real meeting audio as on FLEURS | Spike compares three engines on the user's own recording; engine is a setting, not hard-wired |
| Two SPM packages (FluidAudio + speech-swift) pulling conflicting MLX/CoreML dependencies | Start with FluidAudio only (Parakeet); add speech-swift in Phase 2; if it conflicts, use speech-swift for everything |
| Echo when using speakers duplicates remote speech on the mic track | Headphones hint; text de-dup in Merger; later true AEC if needed |
| In-person audio is far-field: reverb and overlapping talk raise both WER and DER | Spike includes a real room recording; speaker-count hint; recommend an external conference mic for larger rooms; Qwen3-ASR is robust on noisy audio relative to Parakeet |
| "Me" mis-identified in a room (similar voices, short enrollment) | Enrollment quality check (min 20 s of speech); distance threshold with manual override; renaming is one click and remembered |
| Nemotron 3 license (NVIDIA OpenMDW) for a personal tool | Fine for personal/internal use; Community-1 (CC BY 4.0) is the drop-in alternative |
| Model downloads need Hugging Face access from the office network | Models cached in Application Support; manual import path in settings |

---

## 5. Sources

- Open ASR Leaderboard (Hugging Face): https://huggingface.co/spaces/hf-audio/open_asr_leaderboard and blog https://huggingface.co/blog/open-asr-leaderboard
- Qwen3-ASR vs Parakeet vs Whisper per-language WER on Mac: https://soniqo.audio/benchmarks ; https://whispernotes.app/blog/qwen3-asr-vs-whisper
- mlx-qwen3-asr (sizes, RTF on M4 Pro 48 GB, word timestamps): https://pypi.org/project/mlx-qwen3-asr/
- FluidAudio README and Benchmarks (Parakeet, Community-1, LS-EEND, Sortformer): https://github.com/FluidInference/FluidAudio , https://github.com/FluidInference/FluidAudio/blob/main/Documentation/Benchmarks.md
- Nemotron 3 diarization in FluidAudio (PR #883, merged 2026-09-23, DER table): https://github.com/FluidInference/FluidAudio/pull/883
- speech-swift (Qwen3-ASR, diarization engines): https://github.com/soniqo/speech-swift , https://github.com/soniqo/speech-swift/blob/main/docs/inference/speaker-diarization.md
- nemotron-diar-mlx (Python alternative, words→turns alignment): https://github.com/9clg6/nemotron-diar-mlx
- Argmax OSS SDK releases (WhisperKit, SpeakerKit, Pro-only Parakeet/Sortformer): https://github.com/argmaxinc/WhisperKit/releases ; Pro SDK 3: https://www.argmaxinc.com/blog/argmax-sdk-3
- Core Audio taps: Apple docs https://developer.apple.com/documentation/coreaudio/capturing-system-audio-with-core-audio-taps ; AudioTee https://github.com/makeusabrew/audiotee ; Recall.ai deep-dive https://www.recall.ai/blog/core-audio-taps
- SpeechAnalyzer vs Whisper (English only): https://mjtsai.com/blog/2026/07/22/whisper-and-speechanalyzer/
- Model roundups: https://pinggy.io/blog/best_open_source_self_hosted_speech_to_text_models/ ; https://openwhispr.com/blog/parakeet-vs-whisper-vs-nemotron ; https://www.marktechpost.com/2026/07/23/best-open-speech-recognition-asr-models-in-2026-wer-languages-latency-and-license-compared/
