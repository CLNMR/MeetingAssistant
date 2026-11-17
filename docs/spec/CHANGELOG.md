# Spec changelog

Newest first. Each entry says what changed for the user and which spec sections changed. The `port-to-windows` skill ports every entry above the one recorded in `windows-sync.md`.

Format:

```
## YYYY-MM-DD: short title
What changed for the user, in one to three sentences.
Sections: 01-recording (Live transcription), 03-assist-ui (Review)
```

## 2026-11-17: Global shortcut to start or stop a meeting
New setting *Start or stop a meeting* in Settings › General, default ⌃⌥⌘R, works in any app. It stops the running recording, or starts a meeting (Assist when Assist is open and ready, otherwise a Scribe recording), and always brings MeetingAssistant to the front.
Sections: 04-dictation-settings (3.4 Global hotkeys: meeting shortcut, 6.1 General)

## 2026-11-17: Meeting list shows which meetings are saved to Aya
A transcribed meeting whose recap was saved to Aya now shows a cloud-upload badge in the accent colour ("Transcribed and saved to Aya") instead of the green check, which now means "transcribed only". The badge appears in both the Assist meetings list and the Recordings list, and right after saving.
Sections: 01-recording (Transcription queue and session statuses), 03-assist-ui (Meetings list: Session row, Mac implementation)

## 2026-11-17: "Open in Aya" links work again
Links to Aya used `…/recordlist/<record type>/record/<id>`, but the web app's `recordlist` pages are named after navigation entries, not record types, so links broke (e.g. type `pmMeetings`, page `meetings`). Links now use the web app's record-type routes: `/workspace/<ws>/record/<type>/<id>` and `/workspace/<ws>/list/<type>/`.
Sections: 02-assist-core (Web URLs), 03-assist-ui (Rail logo, Open in Aya)

## 2026-11-17: Checklist items are filled in; escaped templates load
Fill-in checklist items (text ending in ":" or with "%" blanks, e.g. "Erika: Win: % / Pain: %") now get a short answer from Claude (a few words, no sentences). It shows under the item in the Checklist panel, goes to the recap draft as CHECKLIST ANSWERS, and is saved with the template tables under the template heading. Templates whose Markdown comes escaped from Aya (`\- Name:&#x20;`) now produce their checklist instead of none.
Sections: 02-assist-core (3.1 live system prompt, 3.4 resume message, 3.6 ops, 4.2 PlaybookItem, 6.1 parser unescaping, 6.3 block and checklistLine, 7.3 recap context, 7.5 saving the recap), 03-assist-ui (Checklist panel)

## 2026-11-17: Meetings in the sidebar carry their Aya title
Assist meetings in the sidebar are named after the title written to Aya instead of "Meeting": after saving to Aya (always), after a recap draft (only while the name is still automatic), and once at app start for meetings already saved to Aya.
Sections: 03-assist-ui (8.2 Upload: Session name = Aya title, Mac implementation)

## 2026-11-16: Fewer, shorter open points
Open points are now kept like the summary: one short line per issue (about 12 words, starting with a topic label), at most about 6 open. Every tick shows Claude the current open points, so it rewrites an existing point instead of adding a new one, and it can combine points about the same issue with the new op `open_point_merge`.
Sections: 02-assist-core (3.1 live system prompt, 3.5 tick message, 3.6 ops and applying ops)

## 2026-11-16: Floating window shows the speaker names given in Assist
Speakers named during an Assist meeting (rename, or matched to an attendee) now carry that name in the floating live transcript too, instead of "Speaker n" / "Remote n". The names stay on the recording's lines after it ends.
Sections: 01-recording (Floating live transcript window: Paragraphs), 03-assist-ui (Live rename)

## 2026-11-16: "What did I miss?" answers in the meeting's language
The catch-up summary came back in English in German meetings. The prompt now names the language detected in the transcript window (`LANGUAGE: German`, ending with `Answer in German.`), and the system prompt asks for the whole answer in that language, including the "Now:" label ("Jetzt:").
Sections: 02-assist-core (3.7 catchUpSystem, 3.8 catch-up prompt and language detection)

## 2026-11-13: Open at login
A new General setting, *Open MeetingAssistant at login*, registers the app as a login item. The first time the main window opens, an alert asks whether to turn it on (*Open at Login* / *Not Now*); it is asked only once.
Sections: 04-dictation-settings (3.5 Permissions and onboarding, 6.1 General)

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
