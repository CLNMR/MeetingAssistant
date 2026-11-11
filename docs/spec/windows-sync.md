# Windows port: sync state

The `port-to-windows` skill keeps this file up to date.

| | |
|---|---|
| Last ported changelog entry | 2026-11-11: Windows app, and spec corrections found while building it (the first run built the app from the whole spec) |
| Spec commit ported from | 9fc7295 plus uncommitted spec changes of 2026-11-11 |
| Last run | 2026-11-11 |

## What the first run built

All features in the spec: recording (Call, In person, Hybrid), live and final transcription with speakers, voiceprint and speaker library, trim/split/export/import, the floating live transcript, Assist (connect, prepare, live panels and tables, review, recap upload, CRM proposals, past meetings, import), dictation and Scribe+, vocabulary, settings, tray icon and msctl. The "Windows implementation" section at the end of each spec file lists the differences.

Validated on the Mac:
- 434 unit tests (Core, Speech, Assist, dictation controller, Scribe rules).
- Data files written byte-for-byte like the Mac's (session, transcript, assist.json, Aya cache, vocabulary, voiceprint, library, trim/split/export results).
- Real transcription with sherpa-onnx on recorded meetings; live transcription; enrollment.
- Real Claude calls (Assist replay, recap, tables, Scribe+ dictation) and read-only Aya calls.
- The WPF app and Windows platform code compile with 0 warnings; a script checks XAML resources and bindings.

## Open items

Not yet run on Windows (needs a Windows machine):
- The WPF app as a whole: startup, theme in light and dark, every screen.
- WASAPI mic capture and process loopback (call audio), device changes, privacy-blocked microphone.
- Global hotkeys (Right Ctrl, Right Ctrl + Right Shift, AltGr layouts), paste into common apps, UI Automation reading of the focused field, muting.
- Credential Manager, single instance, tray icon, Media Foundation import of mp3/m4a, Windows spell checker.

Known differences, by design or for now:
- Qwen3-ASR and the language check are not available (no word-timestamp aligner in sherpa-onnx).
- Transcription runs on the CPU, about 5× slower than the Mac. Live speaker separation re-diarizes a 20 s window every 3 s and is less accurate than the Mac's streaming model.
- Word recall on quiet room audio is lower than on the Mac (about 78 % of the Mac's words on one in-person meeting; 97 % on a call).
- Claude child processes are stopped by closing their input; a hard kill on app exit is not guaranteed.
