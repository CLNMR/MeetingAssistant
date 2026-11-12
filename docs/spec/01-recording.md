## Recording and transcription

This section covers "Scribe" mode: recording a meeting, the live transcript while recording, the always-on floating live transcript, the offline (final) transcription pipeline, the session store on disk, editing (trim, split, speakers) and export. Behaviour is described platform-neutrally. Each subsection ends with a **Mac implementation** note and, where the Mac code depends on an Apple-only API, a **Windows notes** line.

Conventions used throughout:

- All audio inside the app is **16 kHz, mono, 32-bit float**, sample values in -1…1. "Samples" below always means 16 kHz samples (1 s = 16 000 samples).
- Times in transcripts are **seconds from the start of the recording** (Double).
- `clock(t)` = `mm:ss` below one hour, `h:mm:ss` from one hour on (truncated to whole seconds). Used everywhere a time is shown.

---

### Meeting modes

The user picks one of three modes before recording. The mode decides which tracks are recorded and which tracks are diarized (split into speakers).

| Mode (JSON value) | UI title | Tracks recorded | Diarized tracks (live and final) | Who is "me" |
|---|---|---|---|---|
| `call` | Call | `mic` + `system` | **both** | The mic voice that matches the voiceprint; otherwise the mic speaker with the most words |
| `inPerson` | In person | `mic` only | `mic` | Only via voiceprint match |
| `hybrid` | Hybrid | `mic` + `system` | both | Only via voiceprint match. "Participants" is ignored |

Explanation text shown under the mode picker:

- Call: "Online meeting. Records your microphone and the call audio separately. Your voice is labelled "Me"; other people at your microphone are told apart too."
- In person: "Everyone is in the room. Records the microphone only and separates speakers from it."
- Hybrid: "Call with several people in the room. Records both and separates speakers on both tracks."

Mode hints shown in a tinted box under the settings:

- Call: "Use headphones if you can. With speakers, your mic also hears the other side; MeetingAssistant removes most of that echo, but headphones give the cleanest result."
- In person, no voiceprint: "Tip: record a voice sample in Settings so MeetingAssistant can label you as "<myName>". For bigger rooms an external conference microphone helps a lot." With voiceprint: "Your voice sample is set up, so you will be labelled "<myName>". …"
- Hybrid: "Remote and in-room speakers are separated on both tracks. Setting the number of participants is not used in this mode."

Note (change history): Call mode used to label the whole mic track as a fixed speaker `me`. It now diarizes the mic too, so colleagues sitting next to the user get their own labels. Transcripts made before that change may still contain the speaker id `me`; a reader must accept any speaker id.

**Mac implementation:** `MeetingMode` in `Sources/MeetingAssistantCore/Model/Models.swift` (`recordsSystemAudio` = mode ≠ inPerson). Hints in `RecorderView.swift`.

---

### Audio capture

#### Microphone

- Input device: the device chosen in the recorder ("Microphone" picker, stored as the device's stable UID in preferences `inputDeviceUID`; empty = "System default").
- Device list: all devices with at least one input channel, **excluding** the app's own private aggregate devices (UID prefix `de.local.MeetingScribe`) and virtual call-app devices whose name contains `Microsoft Teams Audio` or `ZoomAudioDevice`. The list is refreshed when the recorder screen appears.
- Captured in the device's native format (all channels, float), then downmixed and resampled to 16 kHz mono (high-quality resampler).
- If the chosen device disappears, the system default input is used.
- **Self-repair on device changes.** The app watches: the device's nominal sample rate, "is alive", input stream configuration, the system device list, and the default input device. Change events come in bursts, so they are debounced (**0.4 s**). Then: if the target device (chosen-or-default) is the same as the current one and its rate (±1 Hz) and channel count are unchanged, nothing happens. Otherwise capture is stopped and rebuilt on the target device, with up to **10 attempts 0.5 s apart** (a new device may need a moment before it reports a valid format). The gap is filled with silence by the track writer (see below), so the recording stays time-aligned.
- Capture must never briefly open the system default input when another device was chosen (opening a Bluetooth headset's mic switches it into low-quality call mode).

**Mac implementation:** `MicCapture` / `AudioDevices` in `Capture/MicCapture.swift`, a Core Audio HAL output unit (`kAudioUnitSubType_HALOutput`) with input enabled, not AVAudioEngine (the README still says AVAudioEngine; that is outdated). Permission via `Capture/Permissions.swift` (`AVCaptureDevice.requestAccess(for: .audio)`).
**Windows notes:** WASAPI shared-mode capture on the chosen `IMMDevice` (by endpoint ID), `IMMNotificationClient` for default-device/device-state changes, `AUDCLNT_E_DEVICE_INVALIDATED` triggers the rebuild. Skip virtual devices by friendly name the same way. Microphone privacy: check `Windows.Media.Capture` access / Settings › Privacy › Microphone and show the equivalent error.

#### System audio ("call audio")

Recorded in Call and Hybrid mode: everything played on the computer (Teams, Zoom, browser, …) **except the app's own playback**, as one track.

- Captured as a stereo global tap of the current default output device, then downmixed/resampled to 16 kHz mono.
- **Sample-rate trust:** if the tap reports a rate different from the output device's actual nominal rate (Bluetooth headsets drop to 16/24 kHz when a call app opens their mic), the device rate is used.
- **Rate watchdog:** every ≥ 2 s of buffers, measured frames/second is compared with the assumed rate; if off by more than **15 %**, the format is switched to the nearest standard rate (8000, 11025, 16000, 22050, 24000, 32000, 44100, 48000, 88200, 96000).
- When the output device also delivers its own mic stream (headset in call mode), only the **last** N streams (the tap's channels) are used.
- **Self-repair (rebuild = tear down tap and recreate it on the current default output):**

| Trigger | Rule |
|---|---|
| Default output device changed | Rebuild immediately |
| Output device nominal sample rate changed | Rebuild immediately |
| Audio IO overload reported on the capture device | Rebuild 1 s later, only if ≥ 30 s since the last rebuild |
| Stuck tap: exact digital zeros (every 4th sample checked, all channels) for **30 s** | Rebuild, only if ≥ 30 s since last rebuild; at most **3** such rebuilds until non-zero audio arrives again (after that the silence is considered real) |
| Dead IO: no buffers at all for **5 s** (checked every 5 s) | Rebuild; at most **3** in a row; counter resets when any buffer arrives |

  Rebuild failures are logged and do not stop the recording; the mic keeps recording.
- A "mute" option (tapped audio not audible) exists only for silent self-tests.
- If the user denied the system-audio permission, the system track is silent and only the user's side is transcribed (no error is shown).

**Mac implementation:** `SystemAudioTap` in `Capture/SystemAudioTap.swift`: Core Audio process tap (`CATapDescription(stereoGlobalTapButExcludeProcesses:)`, macOS 14.2+) attached to a private aggregate device (name "MeetingAssistant Tap", UID `de.local.MeetingScribe.tap.<uuid>`, drift compensation on). Needs the macOS "System Audio Recording" permission.
**Windows notes:** WASAPI loopback on the default render endpoint. To exclude the app's own playback use process loopback (`ActivateAudioInterfaceAsync` with `AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`, `PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE`, Windows 10 2004+); plain endpoint loopback is acceptable if the app plays nothing while recording. Rebuild on `OnDefaultDeviceChanged(eRender)` and on device invalidation. **Important difference:** WASAPI loopback delivers *no packets* while nothing plays, so the "no buffers for 5 s" watchdog must not rebuild in that case (or play a silent stream to keep it ticking); the track writer fills the gap with silence anyway.

#### Track writer (time alignment and files)

Each track is written by a track writer that keeps it aligned to the **session clock** (a monotonic host clock captured when the session object is created):

1. Each incoming buffer is resampled to 16 kHz mono.
2. If the buffer carries a valid host timestamp later than the session start: `expected = (bufferTime − sessionStart) × 16000`; if `expected − written > 1600` samples (**> 100 ms**), that many zero samples are written first. Smaller jitter is ignored; the writer never drops samples when ahead.
3. The samples are written and forwarded to the live callback (`onSamples`) if any.
4. On stop, both tracks are padded with zeros to the **longer** of the two durations, so `mic` and `system` always end together.

Effect: silence is inserted for the delay before the first buffer, for device-switch dropouts and for tap rebuilds, so word times on both tracks share one clock.

Level meter value: RMS of each buffer, peak-hold `level = max(rms, previous × 0.8)`; reads as 0 if no buffer arrived within 0.3 s.

| Track file | Format |
|---|---|
| `mic.caf` | CAF container, 16 000 Hz, 1 channel, 16-bit signed little-endian integer PCM |
| `system.caf` | same; only in Call/Hybrid |

**Track file format across platforms (decided):** each platform writes its native container — the Mac writes `mic.caf`/`system.caf`, Windows writes `mic.wav`/`system.wav` (RIFF WAVE, same PCM spec). Every reader accepts both: for each track it uses `<track>.caf` if it exists, else `<track>.wav`. So a session folder written on either platform can be opened, played, transcribed, trimmed and split on the other. Trim keeps the file's container (a `.wav` stays a `.wav`); new tracks (recording, import, part 2 of a split) are written in the platform's own container.

A listen-only writer (no file) exists for the floating live transcript; it only forwards samples.

**Mac implementation:** `AppPaths.track(id, track)` resolves the file to read (CAF, else WAV); `AppPaths.newTrack(id, track)` is the CAF path writers use. `TrackWriter` (`Capture/TrackWriter.swift`), `Resampler16k` (`Capture/Resampler.swift`, `AVAudioConverter`, downmix on, quality high), `SessionRecorder` (`Capture/SessionRecorder.swift`). Host time = `mach_absolute_time`.
**Windows notes:** use the QPC timestamp returned by `IAudioCaptureClient::GetBuffer` (`pu64QPCPosition`, 100 ns units) and `QueryPerformanceCounter` for the session start. Resample with Media Foundation's resampler DSP or a library resampler (NAudio `WdlResamplingSampleProvider`, libsamplerate). File format: write **WAV** (`mic.wav`/`system.wav`, 16 kHz mono 16-bit PCM) and read both `.caf` and `.wav` with the same preference (CAF first), see "Track file format across platforms" above. A CAF reader needs only the `caff` header, the `desc` chunk (`lpcm`) and the `data` chunk.

---

### Recording lifecycle

**Start** (button "Start Recording", ⌘↩ on the recorder screen, ⇧⌘R anywhere, menu-bar "Record <Mode>", ⏺ in the floating window):

1. Request microphone permission. If denied: error "MeetingAssistant has no access to the microphone. Allow it in System Settings › Privacy & Security › Microphone, then try again." The error alert then has an extra button "Open System Settings" (shown whenever the message contains "System Settings").
2. Create the session: id = local start time formatted `yyyy-MM-dd_HH-mm-ss`; title = trimmed title field, or `Meeting <medium date, short time>` (locale formatting, e.g. "Meeting 10 Nov 2026 at 14:03"); tracks per mode; `expectedSpeakers` = participants picker (nil when "Detect automatically"); `language` = preference unless "Automatic" (then omitted); status `recording`. The session folder is created; if a folder with that name already exists (two recordings started in the same second, e.g. app and `msctl`), the id gets `_2`, `_3`, … — creating the folder is what reserves the id.
3. If floating live listening is running, it pauses its own capture (the recording takes over the mic).
4. If live transcription is wanted (preference *Show live transcript while recording* **or** the floating window is listening **or** Assist forces it), a live transcriber is created for the session's tracks and fed by the track writers.
5. `session.json` is written, the mic starts, then the system tap (if the tap fails, the mic is stopped and the error "Could not start recording: …" is shown).
6. UI: the recorder screen switches to the recording view; a 0.1 s timer updates elapsed time (published in 0.5 s steps) and the two meters (quantized to 2 dB steps; below −60 dB shows 0, to avoid needless redraws).

**Stop** ("Stop", ⌘↩, ⇧⌘R, menu bar, floating window):

1. Capture stops; tracks are padded to equal length and closed; `session.json` gets `durationSec` and status `recorded`.
2. The live transcriber is drained (last utterance finalized) and dropped. Floating listening resumes if it was paused.
3. With "Stop Earlier" (`keepUntil`): if `keepUntil < duration − 0.5`, the session is trimmed to `0…keepUntil` (see Editing).
4. The new session is selected. If *Create full transcript automatically* is on (default) or the recording was made by Assist, it is queued for transcription.

**Stop Earlier… sheet** ("Stop and Remove the End"): "Forgot to stop? Choose where the meeting ended. The recording stops now and everything after that point is deleted." A time chooser (slider + editable `h:mm:ss`/`mm:ss`/seconds field) from 0 to the elapsed time; quick buttons "Last speech (<t>)" (end of the last live line + 2 s, capped at elapsed; also the initial value, else elapsed) and "−5 min", "−15 min", "−30 min", "−60 min" (shown only when the recording is longer). Caption "Keeps <t> of <total>." Button "Stop and Keep <t>" (disabled below 1 s), Cancel.

**Crash recovery at launch:** sessions found with status `recording` are set to `recorded`; because the final write never happened, a `durationSec` of 0 is replaced by the length of the longest track file on disk (frames ÷ sample rate; if the header is unreadable, estimated from the file size as 16 kHz 16-bit mono), so the recording can be played, trimmed and split. Sessions with `queued` or `transcribing` are queued again (with the engine preference; a one-off engine from "Transcribe Again" is not remembered).

**Mac implementation:** `AppState.startRecording/stopRecording` (`Sources/MeetingAssistant/AppState.swift`), `StopEarlierSheet` and `TimeChooser` (`EditSheets.swift`).

---

### Live transcription (while recording, and in the floating window)

The live transcript is a provisional preview. It is never saved; after recording the offline pipeline produces the real transcript.

#### Per-track utterance cutting (VAD)

Each track is processed independently:

| Constant | Value |
|---|---|
| VAD block | 4096 samples (256 ms), streaming Silero VAD with carried state |
| Speech | VAD probability ≥ 0.5 |
| Pre-roll | the last 4096 samples before speech starts are prepended to the utterance |
| End of utterance | 3 consecutive non-speech blocks (≈ 768 ms) **or** utterance length ≥ 14 s |
| Partial (grey) re-transcription | every time the open utterance has grown by 24 000 samples (1.5 s) |
| ASR engine | Parakeet, language "auto" (no language forcing live) |
| UI publish throttle | at most once per 0.7 s from audio processing, plus on every finalized utterance |

Utterance ids: `<track>-<counter>` (counter increments per finalized utterance). A partial updates the same id; a final replaces it. An utterance whose final transcription is empty is removed.

#### Live diarization

- Diarized tracks: as in the mode table (Call and Hybrid: both; In person: mic).
- Streaming diarizer (NVIDIA Nemotron 3, "fast" streaming config) outputs per **10 ms frame** a probability for each of **8 speaker slots**; it runs roughly 3 s behind real time.
- Speaker of a word: sum the 8 slot probabilities over the word's frames widened by 5 frames (50 ms) on each side; the slot with the highest sum wins if that sum > 0.1 × frame count. If the diarizer has not reached the word's end yet → speaker unknown (`nil`, shown as "…").
- Track without diarizer (model failed to load): fixed speaker `<track>-0`, or `me` for the mic in Call mode.
- Live speaker ids: `me`, or `<track>-<slot>` (e.g. `system-2`, `mic-1`).

#### The "me" slot rule (Call mode only)

- On each publish, all mic words that are not echo (see below) are counted by slot. The slot with the most words becomes `meSlot`.
- It is **locked** (never changes again for this transcriber) once it has ≥ 25 words and at least twice as many as the runner-up. Until then it may move, so early lines can change label.
- A mic word whose slot is `meSlot`, or any mic word while `meSlot` is still unknown, is labelled `me`. Other mic slots get `mic-<slot>` (colleagues in the room).

#### Live echo removal

In Call and Hybrid mode, mic words that echo the system track (user on loudspeakers) are hidden, using the same echo mask as the offline pipeline (see "Echo removal"), computed over all live words of both tracks on every publish.

#### Line building

For each utterance: words are tagged with speakers; a word with unknown speaker inherits the previous word's speaker (except the first word); the utterance is split into lines at every speaker change. Line id `<utteranceId>.<part>`; each line has track, start/end (of its words), text (words joined with spaces), speaker (may be nil), `isFinal`. All lines are sorted by start time and delivered to the UI as a full replacement list.

#### Live transcript view (recorder screen)

- Shown only when the *Show live transcript* preference is on. Header "Live transcript", note "Preview · speaker labels follow a few seconds behind".
- Row: `clock(start)` · label (80 pt column, speaker colour) · text (normal colour if final, muted if partial). Auto-scrolls to the newest line.
- Labels (same rules as the floating window): `me` → the user's name (preference, trimmed; "Me" if empty); other ids in order of first appearance get "Speaker n", except in Call mode `system-*` ids get "Remote n". Speaker and Remote have **separate counters** ("Speaker 1", "Remote 1", "Remote 2", "Speaker 2"); nil → "…".

**Mac implementation:** `LiveTranscriber` actor (`Pipeline/LiveTranscriber.swift`), labels via `LiveSpeakerLabels` (same file, used by both views), models via `ModelStore` (`Pipeline/Engines.swift`: `vadProbability`, `liveDiarizer()` = `Nemotron3Diarizer(config: .fast32)`), view `LiveTranscriptView` in `RecorderView.swift`. Libraries: FluidAudio (Silero VAD, Parakeet, Nemotron 3 on Core ML / Neural Engine).
**Windows notes:** Silero VAD ONNX (sherpa-onnx or onnxruntime directly; keep the 4096-sample block and threshold). Parakeet TDT 0.6B v3 via sherpa-onnx (int8) for both live and final. Nemotron 3 / Sortformer-style streaming diarization has no ready sherpa-onnx package: options are an ONNX export of NVIDIA's streaming Sortformer, or a fallback of sherpa-onnx speaker embeddings (CAM++) on each utterance with online clustering. Whatever is used must produce "slot per word or nil while lagging" so the rules above stay the same.

---

### Floating live transcript window (always-on listening)

A small always-on-top window that transcribes the mic (and call audio, depending on mode) **without saving anything**.

- Open/close: File › Floating Live Transcript (⇧⌘L), menu-bar item, rail button, button on the recorder screen. Opening starts listening. Closing the window (by the user) stops listening. If it was open when the app quit, it reopens at the next launch.
- Window: 420×320 initially, min 260×120, resizable, movable by its background, first placed near the top-right of the main screen, position remembered. Floats above other windows, visible on all virtual desktops and over full-screen apps, does not steal focus from the call app. Opaque background. While *Hide the floating transcript and the dictation overlay from screen sharing* is on (default, see 04 §6.1) the window is left out of screen sharing, recordings and screenshots: it stays visible to the user, and viewers see what is behind it. Changing the setting applies to an open window at once.
- Requirements: microphone permission ("No microphone access. Allow it in System Settings › Privacy & Security › Microphone.") and downloaded models ("Download the speech models first (main window)."). Errors appear in orange in the transcript area.
- Mode used: the mode currently selected on the recorder screen (system audio captured if that mode records it).
- **Bluetooth rule:** if the chosen (or default) input is a Bluetooth device, listening uses another mic instead: first built-in, then USB; if none exists, the Bluetooth one. (Recordings themselves use the chosen device.)
- **Segment rotation** (keeps memory flat when running all day): every 0.25 s a tick checks the current segment; when it is older than **10 min** and either the last line ended more than 3 s ago or the segment is older than 12 min, a fresh live transcriber is started on the same capture (times restart at 0); the old one is drained and released. Finished segments are dropped from the oldest while the total exceeds **4000 lines**.
- **During a recording** the window's own capture is paused and the recording's live transcript is shown in the same list, so it reads as one continuous transcript; recorded paragraphs carry a small red ⏺ marker. The pause/resume button is disabled while recording.
- **Paragraphs:** consecutive lines of the same speaker in the same segment are joined if they start within 120 s of the paragraph's start. Final text first, then pending (partial) text in muted colour. Speaker names as in the recorder's live view (`me` → user's name or "Me"; "Speaker n" and "Remote n" (Call, system track) with separate counters), numbered per segment; nil → "…". Each paragraph shows speaker (in colour), `HH:mm`. Only the last **150** paragraphs are drawn. UI updates are coalesced (0.4 s).
- **Ghost mode** (off by default, remembered under `floatingPanel.ghost`; switched in the "⋯" menu and the menu-bar menu): while the cursor is over the window it fades to **25 %** opacity (0.15 s fade) and clicks and scrolling go to the app underneath. Holding **⌘** (Windows: **Ctrl**) makes it fully visible and usable while held, so the user can press its buttons, scroll, drag it or open "⋯" to turn ghost mode off. With the cursor elsewhere the window looks normal; clicks still pass through until the key is held, so a click right after the cursor enters never lands on the window. The cursor and the key are checked every **0.1 s** (a click-through window gets no mouse events). Rule: off or key held → opacity 1, clickable; otherwise click-through, opacity 0.25 with the cursor inside, else 1.
- **Controls (top bar):** ⏺ start recording (or ⏹ + elapsed time while recording), ⏸/👂 pause/resume listening, two level dots (mic; speaker if the mode records system audio) that light when the level > 0.0056 (≈ −45 dB). "⋯" menu: Copy Transcript (all paragraphs as `[HH:mm:ss] Speaker: final pending`, one per line), Clear (hides everything before now), Larger Text / Smaller Text (font 9–28 pt, default 13, remembered), Ghost Mode (hold ⌘ to use the window) (checkmark), Open MeetingAssistant.
- Scrolling follows new lines until the user scrolls up; a "jump to latest" button appears; scrolling back to the bottom resumes following.
- Empty states: "Listening… nothing is saved unless you press record." / "Paused."
- The Assist banner (next question) is shown at the top when Assist runs (see Assist section).

**Mac implementation:** `ListenController`, `FloatingPanelController`, `FloatingTranscriptView` (`Sources/MeetingAssistant/LiveListening.swift`); capture `LiveListener` (`Capture/LiveListener.swift`). Window is an `NSPanel` (non-activating, `.floating` level, `canJoinAllSpaces`, `fullScreenAuxiliary`). Screen-sharing privacy: `sharingType = .none` (else `.readOnly`), applied by `ScreenSharePrivacy` (`AppState.swift`) to every window registered as an overlay; on macOS 26.6 both ScreenCaptureKit and `screencapture` leave such a window out. Ghost mode: rule `GhostMode` (`MeetingAssistantCore/Util/GhostMode.swift`); `FloatingPanelController` polls `NSEvent.mouseLocation` and `NSEvent.modifierFlags` with a timer in the common run-loop modes and sets `ignoresMouseEvents` and `alphaValue` (animated). Preference keys: `floatingPanel.open`, `floatingPanel.fontSize`.
**Windows notes:** a borderless/tool window with `WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`; screen-sharing privacy with `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` (Windows 10 2004+), `WDA_NONE` when off; ghost mode with `WS_EX_LAYERED` + `SetLayeredWindowAttributes` (alpha) and `WS_EX_TRANSPARENT` (click-through), polling `GetCursorPos`/`GetWindowRect` and `GetAsyncKeyState(VK_CONTROL)`; virtual desktops: pin via the window being a topmost tool window (or `IVirtualDesktopManager` workarounds). Bluetooth detection: endpoint `PKEY_Device_EnumeratorName == "BTHENUM"`/`BTHLEDevice`; built-in vs USB via enumerator (`HDAUDIO`/`USB`).

---

### Offline transcription pipeline (final transcript)

Runs per session from the files on disk. Input: `session.json` + track files. Output: `transcript.json`. Progress is reported as (fraction 0…1, message).

#### Settings used

| Setting | Default | Meaning |
|---|---|---|
| engine | `parakeet-ultra` | ASR engine (preference, or chosen in "Transcribe Again") |
| language | session's `language`, else preference (`auto`) | `auto`, `german`, `english` |
| myName | preference, default "Me" (empty → "Me") | label for the user |
| meThreshold | 0.50 | cosine needed to call a cluster "me" via voiceprint |
| libraryThreshold | 0.55 | cosine needed to name a cluster from the speaker library |
| matchMargin | 0.08 | best match must beat the runner-up by this much |
| autoMergeThreshold | 0.80 | clusters at least this similar are always merged |

#### Steps

1. If engine is Qwen3: load it first (progress 0.01–0.05 "Loading models").
2. Tracks = the session's tracks whose file exists; none → error "The recording contains no audio." Each track gets an equal share of progress 0.05…0.95. Messages use "microphone" / "call audio": "Reading …", "Detecting speech in …", "Transcribing … (i/n)", "Identifying speakers in …", "Finished …", finally "Done". When the Parakeet language rescue (see ASR engines) redoes a chunk with Qwen3, the message becomes "Checking the language with Qwen3… · <track> (i/n)", or, when Qwen3 is not loaded yet, "Checking the language with Qwen3 (loading the model, about 4 GB)… · <track> (i/n)", so a long pause while the ~4 GB model loads is explained.
3. Per track:
   1. Load and convert to 16 kHz mono float. If the track's overall RMS ≤ 1e-5 it is treated as silent and skipped.
   2. **VAD regions** (Silero, threshold 0.5): min speech 0.2 s, min silence 0.5 s, max speech 60 s, speech padding 0.1 s; audio processed in 300 s pieces (offsets added).
   3. **Chunking** (see table) → transcribe each chunk with word timestamps (absolute = chunk start + word time). Chunks ≤ 1600 samples (0.1 s) give no words. Cancellation is checked before each chunk.
   4. **Diarization** if the track is diarized in this mode and has words: offline Nemotron 3 over the whole track with a speech mask (10 ms frames, true within each VAD region ±1 s); segment threshold 0.5, minimum segment 0.25 s. Segment speaker ids become `<track>-<index>` (e.g. `mic-0`, `system-3`).
   5. **Speaker embeddings** per diarized speaker: take that speaker's segments longest first, skip any segment overlapping other speakers' segments by more than 20 % of its own length, concatenate until more than 45 s; if at least 1 s was collected, compute a CAM++ embedding (192-d, L2-normalised). Weight = total duration of all that speaker's segments.
   6. Track without diarization, or a diarized track for which the diarizer returned no segments → all words get speaker `<track>-0`.
4. **Cluster merging** within each track (see below). Target = `expectedSpeakers` (counts the clusters of all tracks), except Hybrid (no target).
5. **Word → speaker assignment** per track (see below), mapped to the merged ids.
6. **Echo removal** (Call and Hybrid): drop mic words that echo the system track.
7. Final centroids = normalised mean of the centroids mapped to each merged id.
8. **"Me" detection:** if a voiceprint exists, best match among the final centroids whose id starts with `mic-` (threshold 0.50, margin 0.08, runner-up also considers every other final centroid). Otherwise, in **Call mode only**, the mic speaker with the most (non-echo) words is "me".
9. **Turns** from all tagged words (see below), then **cleanup**: drop turns whose whole text is a known subtitle credit; apply the vocabulary to the turn's words (text rebuilt from words); renumber ids.
10. **Labels**, in order of first appearance in the turns: "me" → myName (`isMe = true`); else a speaker-library match (threshold 0.55, margin 0.08, runner-up = next library entry) whose name is not already used in this transcript; else "Speaker n" (n counts only unnamed speakers, from 1).
11. Write `transcript.json` (engine raw value, `createdAt` now, `processingSeconds` = wall time).

#### Chunking

| Parameter | Value |
|---|---|
| Max chunk length | 14 s (Parakeet), 28 s (Qwen3) |
| Max gap to join neighbouring regions | 1.2 s |
| Padding added on each side of a region | 0.15 s (clamped to 0…duration) |
| Ignored regions | ≤ 0.05 s |

Regions sorted by start, padded; a region joins the previous chunk if `gap ≤ 1.2 s` and the joined chunk stays ≤ max length. Any chunk still longer than max is split into `ceil(len/max)` equal parts.

#### ASR engines

| Engine id | UI title | Notes |
|---|---|---|
| `parakeet-ultra` | "Parakeet Ultra — fast, 25 European languages (recommended)" | NVIDIA Parakeet TDT 0.6B. Cannot be told the language. Word timings from token timings; if none but text exists, words are spread evenly over the chunk. **Language rescue:** if the user chose German or English and a language detector says the chunk's text is another language with probability ≥ 0.5 (text has ≥ 2 letters), the chunk is redone with Qwen3 forced to that language; this is reported through the progress message (see Steps). |
| `qwen3-1.7b` | "Qwen3-ASR 1.7B — slower, 30 languages incl. Asian" | Qwen3-ASR 1.7B, max 1024 tokens, language name passed when not auto, no-repeat n-gram size 4 for long input; then the Qwen3 forced aligner (language German if german, else English) gives word times. |

**Aligner attach rule** (keeps ASR punctuation/casing): split the ASR text on whitespace; for each token, normalise (lowercase, letters/digits only) and search the next 4 aligner words for an equal word or a prefix match either way; consume following aligner pieces while their concatenation is still a prefix of the token (end time = last piece). Tokens without a match get interpolated times between the previous end and the next known start (`step = gap / (missingRun+1)`, min 0.01 s). Finally start times are made monotonic.

Language list: `auto` "Automatic", `german` "German", `english` "English". Settings note: ""Automatic" handles meetings that switch between German and English."

#### Cluster merging

Greedy: while more than one cluster remains, find the most similar pair **of the same track** (`mic-*` with `mic-*`, `system-*` with `system-*`; the track is the id part before the first `-`; cosine of centroids; ties by sorted id order). Merge if the total cluster count (all tracks) is above the target, or the similarity ≥ 0.80; otherwise stop — also when no same-track pair is left, even if the count is still above the target. A voice at the microphone and a voice on the call audio are never merged: they are different people (echo is handled separately). The cluster with more speech seconds survives; its centroid becomes the speech-weighted average (renormalised) and weights add up. Clusters without an embedding (< 1 s clean speech) never merge.

#### Word → speaker assignment

For each word (span widened to at least 20 ms): the diarization segment with the largest overlap (segments within 10 ms tolerance considered). With no positive overlap: the nearest segment if its distance from the word's midpoint is < 1.5 s; else the previous word's speaker; else the first segment's speaker. Then **singleton smoothing**: a single word whose two neighbours share another speaker is moved to that speaker if the gap between the neighbours is < 1.0 s. (A track without segments uses its fixed speaker `<track>-0`.)

#### Echo removal

For mic words sorted by time, against all system-track words:

1. A mic word **matches** if some system word starting between 2.0 s before and 1.0 s after the mic word's start is *similar*: equal after normalisation, or (both ≥ 4 characters, length difference ≤ ¼ of the longer) Levenshtein distance ≤ max(1, longer/4).
2. Between two matched mic words that are 2–3 positions apart and less than 2 s apart (end of first to start of second), the 1–2 words in between are also echo (garbled echo).
3. A run of echo words that contains fewer than 2 real matches and whose first word has fewer than 4 characters is kept (people say "ja" themselves).

Echo words are removed from the mic track (offline) or hidden (live).

#### Turns

Words are grouped per speaker first: sorted by time, a new turn starts when the gap to the previous word of the same speaker is ≥ 1.5 s. All turns are then sorted by start and numbered 0…n-1. A turn's `track` is the track of its first word. This avoids shredding overlapping talk on two tracks into alternating fragments. (Turns of different speakers may overlap in time.)

#### Invented-text cleanup

A turn is dropped when its whole text, normalised (lowercase, non-letters to spaces, collapsed, so years/punctuation vanish), is one of the known subtitle credits, e.g. "untertitel im auftrag des zdf", "untertitel der amara org community", "copyright wdr", "vielen dank fürs zuschauen", "thanks for watching", "please subscribe", … (full list in `Text/InventedText.swift`, `credits`). The "doubtful phrases" list is used only by dictation, not here.

#### Vocabulary

The user's vocabulary (`vocabulary.json`, see Vocabulary section) is applied to each turn's words: fixed replacements and term spellings, optionally fuzzy; words joined into one term keep the first start and last end. Correctly spelled dictionary words are never changed by a fuzzy match.

**Mac implementation:** `TranscriptionPipeline.run` (`Pipeline/TranscriptionPipeline.swift`), `ModelStore` actor (`Pipeline/Engines.swift`, all inference serialised; Parakeet via FluidAudio `AsrManager`, Qwen3 via speech-swift `Qwen3ASRModel` + `Qwen3ForcedAligner` on MLX/GPU, Nemotron 3 offline via FluidAudio, CAM++ via FluidAudio `CampPlusEmbedder`, language check via Apple `NLLanguageRecognizer`), `Chunking.swift`, `Merger.swift`, `SpeakerID.swift` (`SpeakerMath`, `SpeakerMatch`), `WordAlignment.swift`, `Text/InventedText.swift`, `Text/Vocabulary.swift`.
**Windows notes:** sherpa-onnx offers Parakeet TDT 0.6B v3 (transducer, token timestamps), Silero VAD, and 3D-Speaker CAM++ embedding models (192-d) — use those to keep thresholds meaningful (re-validate 0.50/0.55/0.80 on Windows embeddings; numeric outputs will differ slightly). Offline diarization: Nemotron 3 is not packaged for ONNX; nearest alternative is sherpa-onnx offline diarization (pyannote segmentation 3.0 + CAM++ clustering) producing `(speakerIndex, start, end)` segments, which plugs into the same steps 3.4–3.5. Qwen3-ASR is optional on Windows (could be omitted; then the language rescue is skipped). Language detection: any small text language-ID (e.g. Lingua for .NET, fastText lid.176). Spell check for the vocabulary: Windows `ISpellChecker` (de-DE, en-US).

---

### Transcription queue and session statuses

| Status (JSON) | Meaning | List badge |
|---|---|---|
| `recording` | being recorded | red ⏺ |
| `recorded` | audio saved, no current transcription job | dashed circle |
| `queued` | waiting in the queue | clock |
| `transcribing` | pipeline running | waveform (accent colour) |
| `done` | `transcript.json` written | green check |
| `failed` | last pipeline run threw | amber warning |

- One serial FIFO queue; a session is never queued twice. Enqueue sets `queued`; the worker sets `transcribing`, shows progress (list row progress bar + detail view), then `done` (plays a short "Glass" sound and requests user attention if the app is in the background) or `failed` (alert "Transcription failed: …").
- **Cancel** (detail view button while processing) cancels the running job and empties the queue: the running session and all queued ones go back to `recorded`.
- When the queue is empty, the large final-pass models (Qwen3, aligner, offline Nemotron) are unloaded to free memory (Qwen3 alone ≈ 4 GB).
- Enqueued by: stop recording (auto-transcribe or Assist), Import, "Transcribe Now" (empty detail view), "Transcribe Again" (list context menu: current preferences; detail toolbar menu: choose engine × language).
- **Transcribe Again with engine/language** stores the language in the session (`auto` → removed) and uses the chosen engine **for that run only**; the engine preference is not changed. The menu's checkmark marks the session's language under the engine of the current transcript (or the preference if there is none).
- Editing (trim/split) refuses sessions in `recording`, `queued` or `transcribing` ("The recording is being recorded or transcribed right now.").

**Mac implementation:** `AppState.enqueue/drain/cancelProcessing/retranscribe` (one-off engines in `engineOverride`, keyed by session id, cleared on use and on Cancel).

---

### Model download

- On first use a banner on the recorder screen: "Download the speech models once (about 1.5 GB). Everything runs on this Mac afterwards; no audio ever leaves it." Button "Download Models" → progress with message.
- Steps and progress fractions: 0.02 "Voice activity detection" (Silero), 0.08 "Speaker diarization (Nemotron 3)" (offline + streaming configs), 0.2 "Speaker embeddings (CAM++)", 0.3 "Parakeet Ultra (live transcription)", if the engine preference is Qwen3: 0.45–1.0 Qwen3-ASR (80 %) + aligner (20 %), then 1.0 "Ready". Failure: "Model download failed: …".
- "Models ready" = the four core model folders exist in the model cache (`parakeet-ultra`, `silero-vad`, `campplus`, `nemotron-3-diarization`), or after the first successful transcription/download in this run. Models come from Hugging Face; Qwen3 is otherwise downloaded on first use.
- Parakeet is pre-loaded in the background at launch when dictation is on and models are ready.

**Mac implementation:** `ModelStore.prepareAll`, `coreModelsCached()` (checks `~/Library/Application Support/FluidAudio/Models/…`, independent of `MEETINGASSISTANT_HOME`); `ModelSetupBanner` in `RecorderView.swift`. The app does not create a `Models/` folder in its data directory (the libraries keep their own caches); an existing one from older versions is left alone.
**Windows notes:** store models under `<AppData>\Models\<name>` and use the same four-folder readiness check.

---

### Speakers: voiceprint, library, renaming

**Voiceprint ("me")** — Settings › Voices › "Your voice": "MeetingAssistant recognises you by your voice. Record a voice sample once: read the text aloud for at least 15 seconds (8 seconds of speech are needed)." The user reads a provided German+English text; "Finish" is enabled after **15 s** of recording ("Keep reading… (n s)", n rounded up), which leaves room for pauses. Enrollment: VAD speech regions concatenated; at least **8 s** of speech required, else the error "Only <n> s of speech detected; at least 8 s are needed. Please record again and keep reading without long pauses."; embeddings of consecutive 6 s windows (a window is used if at least 3 s remain) averaged and normalised. Saved as `me-voiceprint.json`. Shows "<n> s recorded <date>" and Delete.

**Speaker library ("Remembered voices")** — learned from renames: when the user renames a speaker that has an embedding and is not "me", the name's entry is created or refined (case-insensitive name match; running average `(old×count + new)/(count+1)`, renormalised; count+1). Listed with "from N meeting(s)" and "Forget" (removes the entries with that name, matched like learning: ignoring case and surrounding spaces).

**In the transcript view** each speaker chip (colour dot, label, talk share = speaker's summed turn durations / all turn durations, rounded %) opens a menu:
- **Rename…** — alert "Rename speaker" with name field (pre-filled with the label unless it starts with "Speaker "), message "MeetingAssistant remembers this voice and uses the name automatically in future meetings." Empty names are ignored. Also teaches the library (see above).
- **This is me** (not on the current me) — this speaker gets `isMe = true` and label myName ("Me" if empty); a previous "me" loses `isMe` and gets the lowest unused "Speaker N" (N = 1, 2, … not yet used as a label in this transcript, compared ignoring case). (Does not change the voiceprint or library.)
- **Merge into ▸ <other speaker>** — all turns move to the other speaker; if both have embeddings the target's becomes their normalised mean; the source speaker is removed; then **normalize**.

Per line (context menu): **Change speaker to ▸** <each speaker> / "New speaker" (creates id `manual-<6 hex chars>`, label = the lowest unused "Speaker N" as above, no embedding) and **Copy** (`<label>: <text>`).

**Normalize** (after reassign/merge/slice): sort turns by start; join a turn into the previous one if same speaker and gap < 1.5 s (text joined with a space, words appended, end = max); renumber ids; drop speakers no longer used.

Every edit is saved to `transcript.json` immediately.

**Speaker colours:** "me" = accent colour; others = 10-colour palette (orange, purple, green, pink, teal, brown, indigo, mint, red, cyan) indexed by 64-bit FNV-1a hash of the speaker id's UTF-8 bytes mod 10 (stable across runs and platforms). The hash starts from **1469598103934665603** (the Mac's constant, one digit shorter than the standard FNV offset basis; keep it so colours match across platforms) and multiplies by the FNV prime 1099511628211.

**Mac implementation:** `VoiceprintStore`, `SpeakerLibrary` (`Pipeline/SpeakerID.swift`), `Transcript.reassign/merge/normalize/markMe/unusedSpeakerLabel` (`Model/Models.swift`), `SessionDetailView` speaker bar, `VoiceSettings` + `EnrollmentRecorder` in `SettingsView.swift`, `speakerColor` in `ContentView.swift`.

---

### Pauses and splitting at pauses

Pauses are computed from the transcript (not stored).

| Constant | Value |
|---|---|
| Minimum marked pause | 60 s |
| Meeting break (suggest split) | 180 s |
| Margin kept on each side when cutting | 2 s |
| Minor turns ignored | turns of ≤ 3 words (stray "ne", "Thank you" during a break) |

Rules: speech spans = non-minor turns sorted by start. Leading pause (0 → first speech) only if ≥ 180 s. Inner pause between the running maximum end and the next start if ≥ 60 s. Trailing pause (last end → duration) if ≥ 60 s. No speech at all: one leading pause over the whole recording if duration ≥ 180 s. An inner pause ≥ 180 s "suggests a split". `splitPoint = max(start, end − 2)`; `keepUntil = min(end, start + 2)`.

UI:
- **Timeline** (22 px bar under the header): speech blocks (turns merged when gap < max(2 s, duration/600)), pauses in amber (stronger when they suggest a split, length label when wider than 44 px), playhead; click/drag seeks.
- **Pause rows** inserted in the transcript list where each pause begins (hidden while searching): dashed line, `clock(start)` (plays from there), "Pause · <len>" / "Quiet for the first <len>" / "Quiet for the last <len>", range, action button "Split Here…" (inner → split sheet at the pause), "Trim Start…" (leading → trim sheet with start = end − 2), "Trim End…" (trailing → trim sheet with end = keepUntil). Length text: "<n> s" below 120 s, else "<n> min".
- **Split hint** banner for the longest split-suggesting pause: "Two meetings in one recording?" / "No conversation for <len>, from <a> to <b>." Buttons Play (from `end − 2`), Dismiss (remembered per session id in the comma-separated preference `dismissedSplitHints`), "Split Here…".

**Mac implementation:** `Pipeline/Pauses.swift`, `RecordingTimeline`, `PauseRow` in `SessionDetailView.swift`.

---

### Editing recordings: trim and split

Both rewrite the audio sample-exactly (frame positions = seconds × file rate) and cut an existing transcript the same way, so names and manual fixes survive without re-transcribing.

**Trim** (keep `start…end`): clamp to 0…duration; must keep ≥ 1 s ("The chosen part is empty."). Each track is copied to a temp file `.trim-<track>.<ext>` (same container as the track) in the session folder and swapped in. Transcript is sliced (below). If `assist.json` exists, its lines are sliced too (lines starting in `[start, end)`, shifted by −start; when the end stays at the full length, later lines are kept). `startedAt += start`, `durationSec = end − start`. Sheet "Trim Recording": "Keep only the part between start and end. The rest of the audio is deleted; the transcript is cut to match." Start/End time choosers with "Playhead" buttons, "End after last speech (<t>)" (last turn end + 2 s, capped), "Keeps <t> of <total>." "Trim" is disabled if < 1 s would remain or nothing would change (start < 0.5 s and end > duration − 0.5 s).

**Split at time t** (needs t ≥ 1 s and duration − t ≥ 1 s): new session id = part-2 start time (`startedAt + t`) formatted `yyyy-MM-dd_HH-mm-ss`, with `_2`, `_3`, … if taken. Tracks `t…end` copied into it; transcript sliced; `session.json` copied with new id, title "<title> (Part 2)", new `startedAt`, `durationSec = duration − t`, same status except `done` without transcript → `recorded`. Then the original is trimmed to `0…t`. If the session has `assist.json`, part 2 gets its own `assist.json`: the same context and live panels, the lines starting at or after `t` (times shifted by −t), and nothing of the first meeting's write-up — no linked recap meeting, upload times, CRM time, created contacts, filled tables or recap draft (it is a new meeting); "speakers carried" is kept. Part 1 keeps only its lines before `t` (through the trim). On any error the new folder is deleted. The new session is selected. Sheet "Split Recording": optional pause text, "Everything after the split point becomes a separate recording, "<title> (Part 2)". The transcript is split too.", time chooser + Playhead (initial: pause split point, else playhead if > 1 s, else half), "Part 1: … · Part 2: …".

**Split at a pause** (when the chosen time is within 0.5 s of the pause's split point): split at `splitPoint`; if "Remove the pause (<len>) from part 1" is on (default) and `splitPoint − keepUntil ≥ 1 s`, part 1 is additionally trimmed to `0…keepUntil`.

**Transcript slice (start, end):** keep words whose midpoint is in `[start, end)`, shift by −start (start clamped ≥ 0, end clamped to `end − start`); turn start/end/text rebuilt from its kept words; turns without word timings are kept if their midpoint is inside; speakers filtered to those used; normalize.

**Mac implementation:** `Pipeline/SessionEdit.swift` (`slice`, `secondPart(of:from:end:)` for `assist.json`), `Pipeline/Pauses.swift` (`split(_:at:removePause:)`), `TrimSheet`, `SplitSheet` in `EditSheets.swift`.

---

### Playback

- All track files of the session play together, synchronised (both scheduled to start at the same device time + 50 ms); current time polled every 0.2 s; stops when all players finished.
- Header play/pause button (Space). Clicking a line's time plays from the turn start (seek if already playing). Clicking a pause row's time plays from the pause start. The current line (last turn with `start ≤ t + 0.05`) is highlighted while playing and kept centred (unless searching). Header shows `clock(current) / clock(duration)`. Playback pauses when leaving the session or opening an edit sheet.

**Mac implementation:** `SessionPlayer` (`Sources/MeetingAssistant/Player.swift`, one `AVAudioPlayer` per track).
**Windows notes:** NAudio: one `AudioFileReader` per track → `MixingSampleProvider` → `WaveOutEvent`/`WasapiOut`; seeking sets the position on each reader.

---

### Exports

Toolbar "Export" menu (disabled without transcript): "Copy as Markdown", "Copy as Plain Text", then "Save as Markdown…", "Save as Plain text…", "Save as Subtitles (SRT)…", "Save as JSON…" (save dialog, default name `<title>.<ext>`, UTF-8).

| Format | ext | Content |
|---|---|---|
| Markdown | `md` | `# <title>` ⏎⏎ `*<full date, short time> · <clock(duration)> · <mode title>*` ⏎⏎ `**Participants:** <labels, comma-separated>` (if any) ⏎⏎ then per turn `**<label>** [<clock(start)>]: <text>` followed by a blank line |
| Plain text | `txt` | one line per turn: `[<clock(start)>] <label>: <text>`, trailing newline |
| SRT | `srt` | each turn's words in cues of 12 words: index, `HH:MM:SS,mmm --> HH:MM:SS,mmm` (end = max(last word end, first word start + 0.5 s)), `<label>: <words>`, blank line. Turns without words produce no cue |
| JSON | `json` | the `transcript.json` object (pretty, sorted keys) |

Line context menu "Copy" copies `<label>: <text>`.

**Mac implementation:** `Pipeline/Exporter.swift`; menu in `SessionDetailView.swift`.

---

### Import

"Import Audio…" (⌘I, list header button): file dialog for audio files, multiple selection, message "Choose one recording (in-person meeting), or two files: first your microphone, then the call audio." One file → In person; two → Call (first = mic, second = system). Files are decoded and converted to 16 kHz mono and written as track files (no padding to equal length). Session id `import_<yyyy-MM-dd_HH-mm-ss>` (now), `_2`, `_3` … on collision; title = first file name without extension; `startedAt` = now; duration = longer track; status `recorded`; then selected and queued. Error: "Import failed: …".

**Mac implementation:** `SessionStore.importFiles`, `AppState.importAudio`. Decoding via `AVAudioFile` (any format Core Audio reads).
**Windows notes:** Media Foundation (`MediaFoundationReader`) for mp3/m4a/wav/etc.

---

### Session storage

#### App data directory

| Platform | Default root |
|---|---|
| Mac | `~/Library/Application Support/MeetingAssistant/` |
| Windows (suggested) | `%LOCALAPPDATA%\MeetingAssistant\` |

- Override: environment variable **`MEETINGASSISTANT_HOME`** (legacy name `MEETINGSCRIBE_HOME` also honoured) = absolute folder used as root. Used to keep test data separate.
- Migration (Mac): the app was called MeetingScribe. If `…/MeetingScribe/` exists and `…/MeetingAssistant/` does not, it is moved once; if the move fails the old folder keeps being used. If both exist, the new one is used unless it is empty.
- `Sessions/` is created at start (no `Models/` folder; one left by older versions is not touched).

```
<root>/
  Sessions/<sessionId>/
    session.json          SessionInfo
    mic.caf               16 kHz mono 16-bit PCM (Windows: mic.wav; readers accept both, .caf first)
    system.caf            (Call/Hybrid only; Windows: system.wav)
    transcript.json       Transcript (after transcription)
    assist.json           (Assist recordings only; see Assist section)
    .trim-<track>.<ext>   (temporary during trim)
  me-voiceprint.json      Voiceprint
  speaker-library.json    SpeakerLibrary
  vocabulary.json         Vocabulary (see Vocabulary section)
  Dictations/             (dictation recovery; see Dictation section)
  Aya/, ClaudeWork/       (Assist; see Assist section)
```

The session list is built by reading every `Sessions/*/session.json` (unreadable folders are ignored), sorted by `startedAt` descending.

#### JSON encoding rules (must match exactly for cross-platform files)

- UTF-8, pretty-printed, **keys sorted** alphabetically, written atomically (temp file + rename).
- Dates: ISO 8601 in UTC **without fractional seconds**, e.g. `"2026-11-10T13:03:22Z"`. The Mac reader rejects fractional seconds.
- Optional fields that are nil are **omitted** (not `null`).
- Numbers: Double/Float as JSON numbers; embeddings as arrays of numbers. Integral values have no decimal point (`3`), exponents are lower-case (`-4.335016e-05`).
- Strings: only `"`, `\` and control characters are escaped; everything else (umlauts, emoji, U+202F) is written as UTF-8. In these files `/` is written as `\/` (the Mac encoder's default); compact JSON inside prompts does not escape `/`.
- Empty arrays and objects are written as `[` newline, blank line, indent `]` (Swift's pretty format), e.g. `"turns" : [\n\n  ]`.
- Pretty format: two-space indent and `"key" : value` (space before the colon). The reinstall guard greps for `"status" : "recording"`.

#### `session.json` (SessionInfo)

| Field | Type | Notes |
|---|---|---|
| `id` | string | Folder name. `yyyy-MM-dd_HH-mm-ss` (local time), `import_…`, or with `_n` suffix when the name is taken (recording, split, import) |
| `title` | string | User-editable (detail header, Enter to save) |
| `startedAt` | date | Start of the audio (shifted by trims/splits) |
| `durationSec` | number | Seconds; 0 while recording (set from the audio files when a crashed recording is recovered) |
| `mode` | `"call"` \| `"inPerson"` \| `"hybrid"` | |
| `tracks` | array of `"mic"` \| `"system"` | Tracks the session should have |
| `expectedSpeakers` | int, optional | Participants picker 2–10; omitted = automatic |
| `language` | `"auto"` \| `"german"` \| `"english"`, optional | Omitted = use the preference at transcription time |
| `status` | see statuses table | |

#### `transcript.json` (Transcript)

| Field | Type | Notes |
|---|---|---|
| `turns` | array of Turn | Sorted by start, ids 0…n-1 |
| `speakers` | array of Speaker | Order of first appearance |
| `engine` | string | `parakeet-ultra` or `qwen3-1.7b` |
| `createdAt` | date | |
| `processingSeconds` | number | Pipeline wall time |

Turn:

| Field | Type | Notes |
|---|---|---|
| `id` | int | Index |
| `speaker` | string | Speaker id: `mic-<n>`, `system-<n>`, `manual-<hex6>`, or legacy `me` / `unknown` (older transcripts) |
| `start`, `end` | number | Seconds |
| `text` | string | Words joined with single spaces (ASR punctuation kept) |
| `track` | `"mic"` \| `"system"` | Track of the first word |
| `words` | array of Word | May be empty |

Word: `w` (string, a token incl. punctuation), `start`, `end` (seconds).

Speaker:

| Field | Type | Notes |
|---|---|---|
| `id` | string | As above; after cluster merging the surviving id |
| `label` | string | Display name ("Me"/myName, library name, "Speaker n", user rename) |
| `track` | `"mic"` \| `"system"` | Track of its first turn |
| `embedding` | number[192], optional | CAM++ centroid (normalised); absent for manual/no-embedding speakers |
| `isMe` | bool | |

Any label lookup for an id not in `speakers` falls back to the id itself.

#### `me-voiceprint.json` (Voiceprint)

`name` (string, myName at enrollment), `embedding` (number[192]), `seconds` (speech seconds used), `createdAt` (date).

#### `speaker-library.json` (SpeakerLibrary)

`{ "entries": [ { "name": "Erika Musterfrau", "embedding": [...192], "count": 3 } ] }` — `count` = number of renames that contributed.

#### Preferences relevant to Scribe

Stored outside the data root (Mac: user defaults key `preferences.v1` as a JSON blob; missing keys fall back to defaults).

| Key | Default | |
|---|---|---|
| `engine` | `parakeet-ultra` | |
| `language` | `auto` | Also the recorder's "Language" picker |
| `myName` | `"Me"` | |
| `inputDeviceUID` | absent = system default | |
| `liveTranscription` | true | "Show live transcript while recording" |
| `autoTranscribe` | true | "Create full transcript automatically after recording" |
| `defaultMode` | `call` | Mode preselected at launch |

Other UI state: `appMode` (`scribe`/`assist`), `dismissedSplitHints`, `floatingPanel.open`, `floatingPanel.fontSize`.

**Mac implementation:** `AppPaths`, `JSONIO` (`Model/Models.swift`), `SessionStore` (`Pipeline/SessionStore.swift`), `Preferences` (`AppState.swift`).
**Windows notes:** `System.Text.Json` with a custom `DateTime` converter (`yyyy-MM-dd'T'HH:mm:ss'Z'`, UTC), `DefaultIgnoreCondition = WhenWritingNull`, property names exactly as above (camelCase, `inPerson`), and sorted keys (write via a `SortedDictionary`/ordered properties). Atomic write: write `*.tmp` then `File.Replace`/`File.Move(overwrite: true)`.

---

### Scribe window, menus and shortcuts

**Layout:** left rail (logo — tooltip "Open the meetings in Aya" when a meetings list is set up, else "Open Aya", see the Assist UI section —, Scribe ⌘1, Assist ⌘2, floating transcript, settings, user initials from myName, "ME" if empty; a red dot on the Scribe icon while recording and Assist is shown). Scribe = recordings panel (290 pt) + main area (recorder or session detail).

**Recordings panel:** title "Recordings", Import button (⌘I); "New Recording" row (shows "⇧⌘R", or "Recording…" + elapsed · mode while recording; selects the recorder); search field (filters titles, case-insensitive); list (excluding the session being recorded), each row: title, status badge, mode icon, start "d MMM, HH:mm", `· clock(duration)`, progress bar while processing. Up/Down arrows move the selection. Row context menu: "Transcribe Again", "Show in Finder", "Delete…" (disabled while the session is recording, queued or transcribing). Delete asks first, in the same style as Assist: title "Delete “<title>”?", message "The recording, the transcript and the notes on this Mac are deleted." plus, for sessions with `assist.json`, " What was saved in Aya stays there."; a destructive "Delete" button and Cancel. Confirming deletes the session folder. Empty: "No recordings yet." / "Nothing matches.".

**Recorder screen (not recording):** eyebrow "Scribe", "New recording", "Everything is recorded and transcribed on this Mac."; mode segmented control + explanation; rows: Title (placeholder "Optional, e.g. Weekly with Anna"), Microphone, Participants ("Detect automatically", "2 people" … "10 people"), Language, Live transcript switch; mode hint; buttons "Floating Live Transcript" and "Start Recording" (⌘↩).

**Recorder screen (recording):** "Recording · <mode>", large elapsed clock, waveform of max(mic, system) level, "Stop Earlier…", "Stop" (⌘↩); meters "Microphone" and (Call/Hybrid) "Call audio" (−60…0 dB mapped to 0…1, red above 90 %); "When you stop, the full transcript with speaker labels is created automatically." (if auto-transcribe); live transcript.

**Session detail:** header (play/pause Space, editable title, mode, date "weekday, d month year, HH:mm", "· N speakers", position/duration, "Assist Recap" button if `assist.json` exists and Assist is not running), search field (filters turns by text or speaker label), toolbar Export / Transcribe Again / Edit (Trim…, Split…) — the last two disabled while processing. Body: progress + Cancel while processing; else timeline, split hint, speaker bar ("Click a speaker to rename or merge. Right-click a line to change its speaker."), transcript list; else empty state ("Not transcribed yet." / "Waiting to be transcribed…" (only while `queued`) / "Transcription failed.") with "Transcribe Now".

**Main menu:** File (after New): "Start Recording"/"Stop Recording" ⇧⌘R, "Import Audio…" ⌘I, "Floating Live Transcript" ⇧⌘L. View (before sidebar): "Scribe" ⌘1, "Assist" ⌘2.

**Menu-bar (tray) menu:** icon ⏺ while recording, waveform otherwise. Recording: "Recording <clock> · <mode>", "Stop Recording". Not recording: "Record Call", "Record In person", "Record Hybrid" (sets mode and starts). Dictation entries (see Dictation section). "Transcribing N recording(s)…" while jobs run. "Floating Live Transcript", "Floating Transcript Ghost Mode" (checkmark), "Open MeetingAssistant", "Quit" ⌘Q.

**Launch argument** `-select <sessionId>` opens that session directly.

**Mac implementation:** `MeetingAssistantApp.swift` (commands, `MenuBarExtra`), `ContentView.swift` (`AyaRail`, `RecordingsPanel`, `SessionRow`, `StatusBadge`), `RecorderView.swift`, `SessionDetailView.swift`. Developer CLI `msctl` (`Sources/msctl/main.swift`): `import`, `transcribe`, `export`, `livetest`, `taptest`, `trim`, `split`.
**Windows notes:** menu-bar extra → notification-area (tray) icon with the same context menu; "Show in Finder" → "Show in Explorer" (`explorer /select,`); ⌘ shortcuts → Ctrl; attention request → `FlashWindowEx`; system settings link → `ms-settings:privacy-microphone`.

### Windows implementation

The Windows app (`windows/`, see `windows-sync.md`) follows this section with these differences:

- **Files:** new tracks are `mic.wav` / `system.wav` (16 kHz mono 16-bit PCM). The WAV header is rewritten after every second of audio, so a crash loses at most about one second. Readers take `.caf` first, then `.wav`, so Mac sessions open on Windows and the other way round. Trim and split write WAV for parts they create.
- **Mic capture:** WASAPI shared mode on the chosen endpoint (`inputDeviceUID` holds the Windows endpoint id), resampled to 16 kHz mono. When the device disappears or the default input changes, capture is rebuilt after 0.4 s and the gap is padded with silence. A blocked microphone in Windows privacy settings shows the global error with the button "Open Windows Settings" (`ms-settings:privacy-microphone`).
- **Call audio:** WASAPI process loopback that excludes the app's own process tree (Windows 10 2004+; format chosen by the app: 48 kHz stereo 16-bit with automatic conversion, else 44.1 kHz). Fallback: classic loopback of the default output device. Both are rebuilt when the default output changes. The Mac's rate and stuck-silence watchdogs are not needed. A source without timestamps is treated as "buffer ended now" for alignment.
- **Playback:** both tracks are mixed into one stream (NAudio) instead of two synchronised players.
- **Models:** sherpa-onnx with ONNX models downloaded into `<root>/Models/` (about 530 MB): `silero-vad`, `pyannote-segmentation-3-0`, `campplus` (3D-Speaker CAM++ `3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced`), `parakeet-ultra` (`sherpa-onnx-nemo-parakeet-tdt-0.6b-ultra-int8`, the same Parakeet as the Mac). Download steps: "Silero", "Speaker diarization (pyannote segmentation 3.0)", "CAM++", "Parakeet". A folder is renamed into place only when complete.
- **Engines:** only Parakeet. Qwen3-ASR has no word-timestamp aligner in sherpa-onnx, so the Qwen3 option and the language check are not offered; `transcript.json` always says `parakeet-ultra`.
- **Words:** Parakeet tokens that start with a space begin a new word.
- **Gain:** quiet recordings are boosted before VAD, ASR and diarization so that speech reaches a level of 0.1 (at most 20×, never turned down). Without it, quiet room audio lost about a quarter of its words.
- **Offline diarization:** sherpa-onnx pyannote segmentation + CAM++ embeddings, clustering threshold 1.1. VAD runs over the whole track, not in 300 s pieces. Cluster merging, word assignment, echo removal and the speaker thresholds (0.50 / 0.55 / 0.80) are as above.
- **Live diarization:** there is no streaming diarizer. Every 3 s the last 20 s of a track are diarized again, and each local speaker is matched to up to 8 fixed slots by CAM++ similarity (≥ 0.5). A word's label is set once and never changes. Output format as above (`me`, `mic-N`, `system-N`, nil while unknown); labels arrive about 3–5 s late.
- **Live VAD:** sherpa-onnx does not expose Silero's probability, so a 4096-sample block counts as speech when any of its 512-sample windows is speech. The VAD is reset every 100–200 blocks.
- **Speed:** CPU only, about 5× slower than the Mac (a 30-minute meeting takes about 2 minutes).
- **Ghost mode:** the interact key is Ctrl (either side); the window is layered (`WS_EX_LAYERED`, alpha via `SetLayeredWindowAttributes`) only while ghost mode is on, plus `WS_EX_TRANSPARENT` while click-through (`GhostWindow`). Holding Right Ctrl to dictate also makes the window usable while held.
- **Screen-sharing privacy:** `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)` on the floating window and the dictation HUD (`ScreenSharePrivacy`); older Windows versions ignore it.
- **Window state:** the floating window's and main window's positions are kept under `floatingPanel.frame` and `mainWindow.frame` ("left,top,width,height[,max]").
- **Preferences:** all Mac user-defaults keys (`preferences.v1`, `assist.settings.v1`, `aya.config.v2`, `appMode`, `dismissedSplitHints`, `floatingPanel.*`, …) live in one file, `<root>/preferences.json`.
- **msctl record** records from files only (`--mic`, `--system`, `--system-delay`, `--speed`); live capture happens in the app.
- **Recording start failure:** the empty session folder is deleted (the Mac leaves it with status `recording`). On quit or Windows shutdown a running recording is finished first; with auto-transcribe on it is saved as `queued`, so the next launch transcribes it.
- **UI:** the model banner says "about 530 MB"; Settings › Models lists per-model sizes and has "Delete Models…" (blocked while recording or transcribing). Settings › General also has the microphone picker. Import takes at most two files. A finished transcription plays the "Asterisk" system sound and flashes the taskbar (the Mac plays "Glass").
