# MeetingAssistant for Windows

The Windows version of MeetingAssistant, built from the specification in [`docs/spec/`](../docs/spec/README.md). The spec's "Windows implementation" notes list where it differs from the Mac app. [`docs/spec/windows-sync.md`](../docs/spec/windows-sync.md) records which spec changes it includes.

## Projects

| Project | Target | Contents |
|---|---|---|
| `src/MeetingAssistant.Core` | net10.0 | Models and data files (same JSON as the Mac), storage, trim/split/export, text rules, vocabulary, pipeline logic, recording orchestration, Aya client, Claude CLI, Assist engine and controllers, dictation controller |
| `src/MeetingAssistant.Speech` | net10.0 | sherpa-onnx: model download, VAD, Parakeet, diarization, CAM++ embeddings, transcription pipeline and queue, live transcriber, voiceprint enrollment |
| `src/MeetingAssistant.Platform.Windows` | net10.0-windows | WASAPI mic and process-loopback capture, playback, muting, global hotkeys, paste, UI Automation, Credential Manager |
| `src/MeetingAssistant.App` | net10.0-windows, WPF | The app: Scribe, Assist, dictation HUD, floating transcript, settings, tray icon |
| `src/msctl` | net10.0 | Command-line tool for testing (`msctl help`) |
| `tests/MeetingAssistant.Core.Tests` | net10.0 | xUnit tests for Core and Speech |

Core, Speech, msctl and the tests run on Windows, macOS and Linux. The Windows projects build on macOS too (`EnableWindowsTargeting`) but only run on Windows.

## Install

Download the setup or the portable zip from the [Releases](https://github.com/CLNMR/MeetingAssistant/releases) page. The main [README](../README.md#windows) explains installing, SmartScreen and updating.

## Build and run

Requires the .NET 10 SDK.

```powershell
dotnet build windows/MeetingAssistant.slnx
dotnet test windows/tests/MeetingAssistant.Core.Tests
dotnet run --project windows/src/MeetingAssistant.App          # Windows only
dotnet run --project windows/src/msctl -- help
```

On first start the app downloads the speech models (about 530 MB) into `%LOCALAPPDATA%\MeetingAssistant\Models`. Set `MEETINGASSISTANT_HOME` to keep test data elsewhere.

Assist and Scribe+ need the [Claude Code](https://claude.com/claude-code) CLI, logged in with a Claude subscription. The app never uses the Anthropic API.

## Graphics card (DirectML)

The app runs Parakeet, CAM++ and the pyannote segmentation on the graphics card through DirectML when Settings › Models › *Use the graphics card for transcription* is on (default), and on the processor otherwise. The `org.k2fsa.sherpa.onnx` NuGet package ships CPU-only DLLs, so `dotnet build`/`dotnet run` always use the processor (Settings shows "Running on the processor: this build has no DirectML support."). Releases replace those DLLs with a DirectML build:

```powershell
pwsh windows/native/build-directml.ps1            # → windows/native/directml/*.dll (about 20–40 minutes)
Copy-Item windows/native/directml/*.dll windows/publish -Force   # after dotnet publish
```

[`native/build-directml.ps1`](native/build-directml.ps1) clones sherpa-onnx at the tag of the NuGet version (`-SherpaVersion`, default 2.0.1), downloads `Microsoft.ML.OnnxRuntime.DirectML` (`-OnnxRuntimeVersion`, default 1.24.4) and `Microsoft.AI.DirectML` (`-DirectMLVersion`, default 1.15.4) from nuget.org, points sherpa-onnx's `cmake/onnxruntime-win-x64-directml.cmake` at them (URL and SHA-256), builds with `-DSHERPA_ONNX_ENABLE_DIRECTML=ON -DBUILD_SHARED_LIBS=ON` (C API, TTS and diarization on, binaries, PortAudio, websocket, Python and tests off) and collects every DLL into `windows/native/directml`. It checks that ONNX Runtime has the DirectML provider, that sherpa-onnx was compiled with it, and (with `dumpbin` on the PATH, i.e. in a Developer PowerShell) that every DLL import resolves. Needs Windows x64, Git, CMake and Visual Studio 2022 or later (Build Tools) with the C++ workload and a Windows SDK. When the NuGet version of sherpa-onnx changes, the release workflow rebuilds the DLLs for it (its cache key contains the version and the script's hash).

## Package a release locally

```powershell
dotnet publish windows/src/MeetingAssistant.App -c Release -r win-x64 --self-contained true -p:Version=1.2.0 -o windows/publish
Copy-Item windows/native/directml/*.dll windows/publish -Force     # optional: graphics card support (see above)
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" /DAppVersion=1.2.0 /DSourceDir="$pwd\windows\publish" /DOutputDir="$pwd\windows\dist" windows\installer\MeetingAssistant.iss
```

The installer script is [`installer/MeetingAssistant.iss`](installer/MeetingAssistant.iss) ([Inno Setup 6](https://jrsoftware.org/isinfo.php)). It installs per user without administrator rights, refuses to update while MeetingAssistant is running (so a recording is never cut off), and its uninstaller keeps the app data and removes the "open at login" entry.
