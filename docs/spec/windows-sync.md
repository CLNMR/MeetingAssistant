# Windows port: sync state

The `port-to-windows` skill keeps this file up to date.

| | |
|---|---|
| Last ported changelog entry | 2026-11-11: Markdown editor for the recap fields |
| Spec commit ported from | uncommitted spec changes of 2026-11-11 (after 8308d10) |
| Last run | 2026-11-11 |

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

## Open items

Not yet run on Windows (needs a Windows machine):
- The WPF app as a whole: startup, theme in light and dark, every screen.
- WASAPI mic capture and process loopback (call audio), device changes, privacy-blocked microphone.
- Global hotkeys (Right Ctrl, Right Ctrl + Right Shift, AltGr layouts), paste into common apps, UI Automation reading of the focused field, muting.
- Credential Manager, single instance, tray icon, Media Foundation import of mp3/m4a, Windows spell checker.
- Hiding the floating window and the dictation HUD from screen sharing (Teams whole-screen share, Snipping Tool), and switching the setting off and on with the floating window open.
- Ghost mode: fade and click-through under the cursor, Ctrl to use the window, and that the layered window still renders correctly and stays hidden from screen sharing.
- Markdown field: preview layout (lists, tables, checkboxes with Segoe Fluent glyphs), switching modes, the toolbar glyphs, heading sizes and fonts in the AvalonEdit highlighting, the editor growing from 90 to 420 px, Enter/Tab/Shift+Tab/Ctrl+B/Ctrl+I/Esc, undo, typing with an IME, and that the review autosave still fires while typing.

Known differences, by design or for now:
- Qwen3-ASR and the language check are not available (no word-timestamp aligner in sherpa-onnx).
- Transcription runs on the CPU, about 5× slower than the Mac. Live speaker separation re-diarizes a 20 s window every 3 s and is less accurate than the Mac's streaming model.
- Word recall on quiet room audio is lower than on the Mac (about 78 % of the Mac's words on one in-person meeting; 97 % on a call).
- Claude child processes are stopped by closing their input; a hard kill on app exit is not guaranteed.
