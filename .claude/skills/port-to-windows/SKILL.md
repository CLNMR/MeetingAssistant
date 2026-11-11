---
name: port-to-windows
description: Port the latest Mac app changes to the Windows app in windows/, using the spec in docs/spec/ (or build the Windows app from the spec on the first run). Use when the user asks to bring changes to Windows, update or sync the Windows app, port a feature to Windows, or start the Windows version.
---

# Port Mac changes to the Windows app

The Windows app in `windows/` is built only from the specification in `docs/spec/`. The Mac code is the reference implementation: read it when the spec leaves something open, but do not translate Swift line by line. Then fix the spec gap too, so the next port does not need the Mac code for it.

`docs/spec/windows-sync.md` records how far the Windows app has caught up.

## 1. Find what to port

1. Read `docs/spec/windows-sync.md`. It holds the last ported changelog entry (date and title) and the commit of `docs/spec/` it was ported from.
2. Read `docs/spec/CHANGELOG.md`. Every entry above the last ported one is still to do.
3. Run `git diff <last ported commit>..HEAD -- docs/spec/` and also look at uncommitted spec changes (`git diff -- docs/spec/`). The diff shows the details behind each changelog entry.
   - If the spec changed but no changelog entry covers it, port it anyway and mention it in your reply.
4. If `windows/` does not exist yet, this is the first run. Build the app from the whole spec (step 2b) instead of from the changelog.
5. Tell the user the list of changes you are going to port before starting. Ask only if a change is ambiguous.

## 2a. Port the changes

For each changelog entry, oldest first:

- Read the spec sections it names, including their "Windows notes".
- Change the matching C# code. Keep the same names for concepts as the spec (AssistState, AssistRecord, recap, ProposedValue, speaker labels such as `me` and `mic-1`, …). Data files on disk must keep the **same JSON format** as on the Mac, so the two apps and their test fixtures stay compatible.
- Add or update unit tests in `MeetingAssistant.Core.Tests` for every rule with numbers in it (thresholds, timings, matching rules). Port the Swift test cases from `Tests/MeetingAssistantCoreTests` where one exists.

## 2b. First run: build the app from the spec

Use this structure unless `docs/spec/README.md` says otherwise:

```
windows/
  MeetingAssistant.slnx, Directory.Build.props, global.json (.NET 10 SDK)
  src/MeetingAssistant.Core/              net10.0: models, JSON, storage, text rules, pipeline logic, capture orchestration, Aya, Claude CLI, Assist (no Windows APIs)
  src/MeetingAssistant.Speech/            net10.0: sherpa-onnx models (VAD, ASR, diarization, embeddings), transcription pipeline, live transcriber (runs on macOS too)
  src/MeetingAssistant.Platform.Windows/  net10.0-windows: WASAPI capture and loopback, playback, hotkeys, paste, UI Automation, Credential Manager
  src/MeetingAssistant.App/               WPF app (net10.0-windows)
  src/msctl/                              console tool, same commands as the Mac msctl where they make sense
  tests/MeetingAssistant.Core.Tests/      xUnit, runs on macOS too (dotnet test)
```

The UI is **WPF**, not WinUI 3: WPF builds on macOS with `EnableWindowsTargeting`, so the whole solution compiles on the Mac. WinUI 3 does not. sherpa-onnx ships macOS runtimes, so transcription can be tested on the Mac with real audio.

On the Mac, install the SDK once with `curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir ~/.dotnet` and use `export PATH=$HOME/.dotnet:$PATH`.

Recommended building blocks. Check that each one is current before relying on it.

| Spec area | Windows choice |
|---|---|
| Mic capture | WASAPI capture (NAudio `WasapiCapture`), resample to 16 kHz mono |
| Call audio capture | WASAPI process loopback (`ActivateAudioInterfaceAsync` with `AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS`, excluding the app's own process), fall back to plain `WasapiLoopbackCapture` |
| VAD | Silero VAD ONNX via sherpa-onnx |
| Speech recognition | NVIDIA Parakeet TDT 0.6B v3 ONNX via sherpa-onnx (word timestamps) |
| Speaker separation after the meeting | sherpa-onnx offline speaker diarization (pyannote segmentation plus speaker embeddings) |
| Voice embeddings ("Me", known voices) | 3D-Speaker CAM++ ONNX via sherpa-onnx (the same model family as the Mac) |
| Live speaker separation | Start with a sliding-window re-run of offline diarization every few seconds. Use a streaming model (e.g. Sortformer ONNX) once one is available. Keep the spec's output format (`mic-N`, `system-N`, `me`, nil while unknown) |
| Claude | the `claude` CLI as a child process (`claude.exe`, or `claude.cmd` from npm). The arguments are those in the spec. **Never** the Anthropic API or an API key |
| Aya key | Windows Credential Manager (`CredWrite`/`CredRead`, target `MeetingAssistant/aya-api-key`) |
| Global hotkeys, including modifier-only ones (right Alt) | low-level keyboard hook (`SetWindowsHookEx` with `WH_KEYBOARD_LL`) |
| Paste | clipboard plus `SendInput` Ctrl+V |
| Text around the cursor, password fields | UI Automation (`TextPattern`, `IsPassword`) |
| App data | `%LOCALAPPDATA%\MeetingAssistant\`, with the same sub-folders and files as on the Mac. `MEETINGASSISTANT_HOME` overrides it |
| Floating live window | an always-on-top WPF window (`Topmost`) |
| Theme | the Aya colour tokens from the spec, as a XAML resource dictionary |
| UI | WPF on .NET 10 (see above for why not WinUI 3) |

Build in this order, so something works early:

1. Core models and data files.
2. Recording, then transcription after the meeting.
3. The Scribe interface.
4. The Aya client and Assist after the meeting (recap, upload).
5. Live transcription and live Assist.
6. Dictation and Scribe+.
7. Settings.

## 3. Check

- Run `dotnet build windows/MeetingAssistant.slnx` and `dotnet test windows/tests/MeetingAssistant.Core.Tests` (both work on macOS).
- Use `msctl` on the Mac to run what is cross-platform with real data (transcription, Assist replay, Aya), with `MEETINGASSISTANT_HOME` pointing to a scratch folder.
- The Windows projects compile on the Mac but only run on Windows. If you are not on Windows:
  - say so;
  - list exactly what needs testing on a Windows machine;
  - keep a Windows CI job (`windows-latest`, `dotnet build` plus `dotnet test`) in `.github/workflows/` working.
- Scan the diff for API keys, Aya ids and real names (the repository is public).

## 4. Record the sync

Update `docs/spec/windows-sync.md` with:

- the newest ported changelog entry (date and title);
- the current commit of `docs/spec/` (`git log -1 --format=%h -- docs/spec/`, or "uncommitted" plus the date);
- what was ported in this run, and what is left with a reason (not possible on Windows yet, needs testing on Windows, …).

Then reply with:

- what was ported;
- what was left;
- what the user should test on Windows;
- whether the spec was missing details, and which spec sections you filled in.

Commit only when the user asks.
