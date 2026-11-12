# Spec changelog

Newest first. Each entry says what changed for the user and which spec sections changed. The `port-to-windows` skill ports every entry above the one recorded in `windows-sync.md`.

Format:

```
## YYYY-MM-DD: short title
What changed for the user, in one to three sentences.
Sections: 01-recording (Live transcription), 03-assist-ui (Review)
```

## 2026-11-11: Markdown editor for the recap fields
Markdown fields of the recap (and Markdown CRM changes) now show the formatted text: headings, lists, checklists, quotes, code, tables and inline styles. Clicking switches to an editor with a formatting bar (heading, bold, italic, strikethrough, code, lists, checklist, quote), shortcuts, list continuation on Return, Tab/Shift-Tab nesting and live highlighting; Done or Esc returns to the formatted view. The editor grows to 420 px.
Sections: 03-assist-ui (8.2 Meeting recap: Markdown field, Mac implementation; 13 snapshot renderer; Windows implementation)

## 2026-11-11: Ghost mode for the floating live transcript
A new Ghost Mode (in the floating window's "⋯" menu and the menu-bar menu, off by default) makes the floating window fade to 25 % and let clicks through while the cursor is over it. Holding ⌘ (Windows: Ctrl) makes it usable again while held.
Sections: 01-recording (Floating live transcript window, Menu-bar (tray) menu, Windows implementation)

## 2026-11-11: Hide the overlays from screen sharing
The floating live transcript and the dictation overlay are left out of screen sharing, recordings and screenshots, so others in a call don't see them. A new General setting turns this off; it is on by default.
Sections: 01-recording (Floating live transcript window, Windows implementation), 04-dictation-settings (1.7 HUD, 6.1 General, Windows implementation)

## 2026-11-11: Windows app, and spec corrections found while building it
The Windows app (`windows/`) was built from the whole spec. No behaviour of the Mac app changed; the spec now states details the port needed.
- JSON rules: string escaping (`/` as `\/` in files), number and empty-array format.
- "Waiting to be transcribed…" shows only while queued; the Unfinished Dictations label is `<short time> · <mm:ss>`; the vocabulary refreshes on a version change even with Aya names off; not every dictation writes a log line; the speaker-colour hash start value; times use `clock(t)`.
- A "Windows implementation" section at the end of each section file.
Sections: README (Architecture), 01-recording (JSON encoding rules, Scribe window, Speaker colours, Windows implementation), 02-assist-core (Windows implementation), 03-assist-ui (Live, Meetings, Speaker colour, Windows implementation), 04-dictation-settings (1.4, 1.8, 5.1, Windows implementation)

## 2026-11-10: Fixes from the spec review
Problems found while writing the spec are fixed.

Recording and Scribe:
- Speakers on the mic and on the call audio are never merged.
- New speaker labels never repeat.
- "Transcribe Again" with another engine no longer changes the setting.
- A recording cut off by a crash gets its real length.
- Two recordings started in the same second get separate folders.
- Deleting a recording asks first.
- Splitting a recording gives part 2 its own Assist record.
- Sessions with `.wav` tracks (from Windows) open on the Mac.
- Loading Qwen3 for the language check shows in the progress.
- The voice sample texts agree (15 s of recording, 8 s of speech).
- Live views use the user's name, and "Speaker" and "Remote" are counted separately.

Assist:
- The Playbook panel is called Checklist.
- "Draft Again" and "Propose Again" ask before replacing edits.
- Return no longer closes the review.
- The recap draft is saved on quit.
- "New meeting" drops the client, project and template a planned meeting filled in.
- Import can choose Call or Hybrid for two files, and a typed title wins.
- "During the meeting" also shows with only a checklist.
- Start needs Aya to be connected.
- Live tables show from the start.
- Events the user sent are kept when the Claude conversation restarts.
- The resume message is built without string replacement.
- Yes/no table answers follow the meeting's language (ja/nein).
- One list of technical fields is used for both the context and CRM edits.
- New contacts link to the client in the right shape (an id list for `__ids` fields).
- Meetings by date are paged.
- Lists from all workspaces can be chosen and are found automatically.
- A Claude timeout has its own error.
- The system prompt says six panels and names a real event.

Dictation:
- Scribe+ sends the app name only with the context option on.
- Retrying an unfinished dictation runs the same clean-up (and Scribe+ for Scribe+ dictations); it still only copies.
- The msctl help lists every command.

Sections: 01-recording (Track writer, Recording lifecycle, Live transcript, Pipeline, Cluster merging, Speakers, Trim and split, Session storage, Scribe window), 02-assist-core (1.4, 1.7, 2.4, 2.5, 3.1, 3.2, 3.4, 3.6, 4.1, 7.4, 7.8, 8.5, 8.8), 03-assist-ui (Rail, Prepare, Panels row, Review, Import, Settings, Notes for the port), 04-dictation-settings (1.4, 1.8, 2.1, 2.2, 7.2, 8, 10)

## 2026-11-10: Spec created
The spec describes the whole app as of this date, including:
- Scribe: call, in-person and hybrid recording, live and final transcripts, editing and export;
- Assist: live copilot with Aya and Claude, recap, CRM proposals, template tables, past meetings, import, speaker renaming, person cards from Aya;
- dictation (word for word and Scribe+), vocabulary and settings.

The first Windows port builds from the whole spec.
Sections: all
