# Windows port: sync state

The `port-to-windows` skill keeps this file up to date.

| | |
|---|---|
| Last ported changelog entry | 2026-11-18: Every meeting gets its recap; speaker names reach the recap |
| Spec commit ported from | uncommitted spec changes of 2026-11-18 (after 29296dd) |
| Last run | 2026-11-18 |

## What the first run built

All features in the spec: recording (Call, In person, Hybrid), live and final transcription with speakers, voiceprint and speaker library, trim/split/export/import, the floating live transcript, Assist (connect, prepare, live panels and tables, review, recap upload, CRM proposals, past meetings, import), dictation and Scribe+, vocabulary, settings, tray icon and msctl. The "Windows implementation" section at the end of each spec file lists the differences.

Validated on the Mac:
- 434 unit tests (Core, Speech, Assist, dictation controller, Scribe rules).
- Data files written byte-for-byte like the Mac's (session, transcript, assist.json, Aya cache, vocabulary, voiceprint, library, trim/split/export results).
- Real transcription with sherpa-onnx on recorded meetings; live transcription; enrollment.
- Real Claude calls (Assist replay, recap, tables, Scribe+ dictation) and read-only Aya calls.
- The WPF app and Windows platform code compile with 0 warnings; a script checks XAML resources and bindings.

## Runs of 2026-11-11 (screen sharing, ghost mode, Markdown editor)

Ported "Hide the overlays from screen sharing": preference `hideFromScreenSharing` (default on), the General settings switch, and `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` on the floating window and the HUD, re-applied when the setting changes.

Ported "Ghost mode for the floating live transcript": rule `GhostMode` (Core, with tests), `GhostWindow` (layered alpha, click-through, polling of cursor and Ctrl), the "⋯" and tray menu items.

Ported "Markdown editor for the recap fields": Core `Markdown` (blocks, inline runs, highlighting, editing commands; the same 21 test cases as the Mac) and the `MarkdownField` control (preview built from WPF elements, AvalonEdit editor with a colouriser, formatting bar, keys). New package: AvalonEdit 6.3.1.120.

## Run of 2026-11-17 (nine entries, 2026-11-13 … 2026-11-17)

- **Open at login:** `LoginItem` (Platform: `HKCU\…\Run` value, `StartupApproved` = needs approval), `LaunchAtLogin` (App), the first-open question after the main window shows, and the General toggle with the approval caption, *Open Startup Apps* and the red error.
- **"What did I miss?" in the meeting's language:** new `catchUpSystem` text, `LANGUAGE:` line and `Answer in …` in the prompt; Core `LanguageGuess` (common-word lists, no package) behind `AssistPrompts.SpokenLanguage`.
- **Floating window shows Assist's speaker names:** `TranscriptSegment.Names`, `SpeakerNames.ForFloatingWindow`, `ListenController.SetRecordingNames`, fed from the live session's state while recording.
- **Fewer, shorter open points:** system prompt item 3, op `open_point_merge` (parse and apply), `CURRENT OPEN POINTS` in the tick message. The prompt texts match the Swift literals again (the prompt test was red).
- **Sidebar meetings carry their Aya title:** `RecapBuilder.Title`/`SessionTitle`, `AssistReviewController.NameSession` after a draft and after saving, `NameSavedMeetings` at start; `IAssistHost.RenameSession`.
- **Checklist answers and escaped templates:** `PlaybookItem.answer`, the `answer` of the `playbook` op, the resume message's `→ <answer>`, the answer under the item in the Checklist panel, `CHECKLIST ANSWERS` in the recap context, `TemplateParser.ChecklistLine`, the checklist part of `Block`, and `TemplateParser.Unescape`.
- **"Open in Aya" links:** `AyaWebLink.Url` (`/record/<type>/<id>`, `/list/<type>/`); logo and review link use it.
- **Saved-to-Aya badge:** `AssistRecord.SavedToAya` (reads only `recapRecordID`), saved ids in `ScribeController`, cloud badge with tooltips in both lists, updated right after a save.
- **Meeting shortcut:** `meetingHotKey` (default Ctrl+Alt+Win+R, explicit null = none), a third hook slot in `DictationService` (registered even with dictation off), the press handler in `App.Meeting.cs`, and the General shortcut row with caption and error.
- Tests: 475 (all ported Swift cases of these entries, plus language, merge-status, naming, floating-name and preference cases).

## 2026-11-18: releases

The Windows side of "Installable releases" was built together with the Mac side: the Inno Setup script `windows/installer/MeetingAssistant.iss` and the `windows` job of `.github/workflows/release.yml`. A self-contained `win-x64` publish was checked on the Mac (about 220 MB, includes the sherpa-onnx and onnxruntime DLLs); the installer itself is only built on the Windows runner.

## 2026-11-18: graphics card (DirectML)

Built on the Windows side only (no Mac change): `SpeechAcceleration` (Speech: probe output parsing, provider and threads, status line, cache fingerprint; with tests), `SpeechEngine.ProviderSource` and `RunGpuProbe`, `SpeechGpu` and `AppLog` (App: child process, cache, registry adapters), the `-gpuProbe` start argument, the Settings › Models switch and status, `speechOnGpu` in the preferences, `windows/native/build-directml.ps1` and the DirectML steps of the release workflow. Checked on the Mac: build, tests, and that the NuGet sherpa-onnx library prints `DirectML is for Windows only. Fallback to cpu!` to stderr for provider `directml` and keeps working on the CPU.

## Open items

Not yet run on Windows (needs a Windows machine):
- Graphics card: the first CI run of `build-directml.ps1` (sherpa-onnx 2.0.1 compiling against ONNX Runtime DirectML 1.24.4 headers, the DLL set and the import check), the `-gpuProbe` child (that sherpa-onnx's stderr reaches the parent from the windowless app, the 90 s timeout, the cache), DirectML on NVIDIA, AMD and Intel graphics (including int8 Parakeet on DirectML and same transcripts as on the CPU), CPU load and speed compared with the CPU path (live transcript with re-diarization, final pass), the fallback on a machine without DirectX 12 and in a development build (status "this build has no DirectML support"), and the status line and restart hint in Settings › Models.
- The release installer: install without admin rights, Start menu entry, SmartScreen prompt, update over a running app (must ask to quit, never close it), uninstall (keeps app data, removes the Run value), and the portable zip.
- The WPF app as a whole: startup, theme in light and dark, every screen.
- WASAPI mic capture and process loopback (call audio), device changes, privacy-blocked microphone.
- Global hotkeys (Right Ctrl, Right Ctrl + Right Shift, AltGr layouts), paste into common apps, UI Automation reading of the focused field, muting.
- Credential Manager, single instance, tray icon, Media Foundation import of mp3/m4a, Windows spell checker.
- Hiding the floating window and the dictation HUD from screen sharing (Teams whole-screen share, Snipping Tool), and switching the setting off and on with the floating window open.
- Ghost mode: fade and click-through under the cursor, Ctrl to use the window, and that the layered window still renders correctly and stays hidden from screen sharing.
- Open at login: the first-open question, the toggle writing and removing the Run value, starting at sign-in, and switching it off in Settings › Apps › Startup (the toggle then shows the approval caption).
- Meeting shortcut Ctrl+Alt+Win+R: start and stop in Scribe and in Assist (also from a Review), the app coming to the front from the tray or minimised, Start not opening when Win is released, the conflict error, and recording a new shortcut in Settings › General.
- Saved-to-Aya badge (Cloud glyph U+E753 in Segoe Fluent Icons/MDL2) in both lists, and the sidebar title changing after a draft and an upload.
- The checklist answer under the item in the live Checklist panel (selectable, wraps), and the floating window labelling recorded lines with names given in Assist.
- Markdown field: preview layout (lists, tables, checkboxes with Segoe Fluent glyphs), switching modes, the toolbar glyphs, heading sizes and fonts in the AvalonEdit highlighting, the editor growing from 90 to 420 px, Enter/Tab/Shift+Tab/Ctrl+B/Ctrl+I/Esc, undo, typing with an IME, and that the review autosave still fires while typing.

Known differences, by design or for now:
- The catch-up language is guessed from lists of common words of nine languages instead of a trained recognizer; short or mixed windows fall back to "the language of the transcript".
- Qwen3-ASR and the language check are not available (no word-timestamp aligner in sherpa-onnx).
- Without a usable graphics card transcription runs on the CPU, about 5× slower than the Mac. Live speaker separation re-diarizes a 20 s window every 3 s and is less accurate than the Mac's streaming model.
- Word recall on quiet room audio is lower than on the Mac (about 78 % of the Mac's words on one in-person meeting; 97 % on a call).
- Claude child processes are stopped by closing their input; a hard kill on app exit is not guaranteed.
