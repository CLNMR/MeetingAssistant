# Spec changelog

Newest first. Each entry says what changed for the user and which spec sections changed. The `port-to-windows` skill ports every entry above the one recorded in `windows-sync.md`.

Format:

```
## YYYY-MM-DD: short title
What changed for the user, in one to three sentences.
Sections: 01-recording (Live transcription), 03-assist-ui (Review)
```

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
