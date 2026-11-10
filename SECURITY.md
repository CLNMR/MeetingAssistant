# Security

MeetingAssistant records meetings, keeps transcripts on the Mac and, in Assist mode, reads and writes CRM data in Aya. Please report security problems privately, not in a public issue.

**How to report:** open a [private security advisory](https://github.com/CLNMR/MeetingAssistant/security/advisories/new) on GitHub. Describe what is affected and how to reproduce it. Please do not include real meeting content, customer data or API keys.

**What counts:** anything that could expose recordings, transcripts, the Aya API key or CRM data to other apps, users or services; anything that makes the app write to Aya without the user confirming it; code paths where transcript text could make Claude do something other than what the app asks (prompt injection with an effect).

**Good to know:**

- The Aya API key is stored in the macOS Keychain (service `MeetingAssistant`). It is never written to files or logs.
- Claude is called through the locally installed Claude Code CLI with no tools, no MCP servers and none of the user's Claude Code settings, so text in a meeting cannot make it run commands or read files.
- Recordings and transcripts are plain files in `~/Library/Application Support/MeetingAssistant/`, protected by the macOS user account and FileVault, not by the app.

This is a personal project without a support promise; reports are read and fixed on a best-effort basis.
