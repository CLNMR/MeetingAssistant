# Privacy: what leaves the Mac

MeetingAssistant has no server and no account of its own and collects no usage data. What it sends, and where:

| What | Where it goes | When |
|---|---|---|
| Audio of recordings and dictations | Nowhere. Transcribed on the Mac (Parakeet, Qwen3-ASR, speaker models). | Never sent. |
| Speech models | Downloaded once from Hugging Face. | First start (*Download Models*). |
| Live transcript text and the Aya context of a meeting (client, contacts, project, template, earlier meetings) | Anthropic, through your local Claude Code login. | Assist mode only, during a meeting and for recap, tables and CRM proposals. |
| A dictation's text (and vocabulary terms that match it) | Anthropic, through Claude Code. | Scribe+ only. With *Use the text around the cursor as context*, also the name of the app you dictate into and up to 1,200 characters before and 300 after the cursor (never from password fields). Without that option, only the dictated text. |
| The last 2–10 minutes of a meeting's transcript | Anthropic, through Claude Code. | When you click *What did I miss?*. |
| Meeting recaps, tables, CRM changes | Your Aya organization (api.hey-aya.ai). | Only when you click *Save* or *Apply*. |
| Aya lists (companies, contacts, projects, templates, meetings, members) | Read from Aya, cached on the Mac. | Assist mode and the vocabulary refresh. |

Claude requests use your Claude subscription and fall under Anthropic's terms for it. Claude Code runs with no tools, no MCP servers and none of your Claude Code settings, so it only sees what the app sends.

## On the Mac

- Recordings (`mic.caf`, `system.caf`) and transcripts live in `~/Library/Application Support/MeetingAssistant/Sessions/`. Delete a recording in the app to remove them.
- A dictation's audio is kept only until its text has arrived. If transcription fails, it stays in `…/MeetingAssistant/Dictations/` for at most 24 hours (excluded from Time Machine) and can be transcribed again or deleted from the menu-bar menu.
- The vocabulary, including names loaded from Aya, is in `…/MeetingAssistant/vocabulary.json`.
- Voice samples (your voiceprint and the voices you named) are in `me-voiceprint.json` and `speaker-library.json`.
- The Aya API key is in the macOS Keychain.
- Dictated text is pasted through the clipboard and stays there, like copied text.

Please ask everyone before you record a meeting.
