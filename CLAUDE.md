# MeetingAssistant

macOS app (Swift 6 toolchain, Swift 5 language mode, SwiftUI) for meetings:

- **Scribe** records and transcribes meetings locally.
- **Assist** is a live meeting copilot. It uses Aya (CRM) and Claude through the local Claude Code CLI.
- **Dictation** (word for word and Scribe+) works in any app.

The app was called MeetingScribe until November 2026; the bundle id stays `de.local.MeetingScribe`.

The full specification is in `docs/spec/`. It is the source for a planned Windows port (`windows/`, skill `port-to-windows`).

## Commands

```bash
swift build                      # debug build (app + msctl)
swift test                       # unit tests for MeetingAssistantCore
./scripts/build_app.sh           # signed app in build/MeetingAssistant.app
.build/debug/msctl …             # command-line tool for testing, see README
MEETINGASSISTANT_HOME=<dir> …    # keep test data out of the real app storage

# Windows app (windows/, .NET 10 SDK; on the Mac in ~/.dotnet)
dotnet build windows/MeetingAssistant.slnx
dotnet test windows/tests/MeetingAssistant.Core.Tests
```

## Hard rules

- **This repository is public.** Never put any of these into code, tests, docs or the spec:
  - API keys (`aya_key_…`), Aya organization, workspace or list ids;
  - real customer, company or person names;
  - the user's Aya template text.

  Use invented names ("MUSTER SOLAR", "Beispilop", "Erika Musterfrau", "Max Beispiel"). Scan the diff before every commit.
- **Claude through the subscription only.** The app talks to Claude through the local `claude` CLI (the user's subscription), never through the Anthropic API or an API key.
- **Never quit or reinstall the app while it records.** Gate the quit and reinstall on the active-session check in one command:

  ```bash
  cd ~/Library/Application\ Support/MeetingAssistant/Sessions && if grep -l -E '"status" : "(recording|transcribing|queued)"' */session.json; then echo ABORT; else osascript -e 'quit app "MeetingAssistant"'; sleep 2; rm -rf /Applications/MeetingAssistant.app && cp -R build/MeetingAssistant.app /Applications/ && open /Applications/MeetingAssistant.app; fi
  ```
- Commit or push only when asked.

## Keep the spec up to date (required)

The spec in `docs/spec/` must describe the app as it is. Update it in the same change as the code, without being asked, whenever behaviour changes. Behaviour includes UI, rules, thresholds, timings, prompts, data formats, files on disk, Aya calls and settings.

1. Edit the matching section:

   | Section | File |
   |---|---|
   | Overview and architecture | `docs/spec/README.md` |
   | Recording, transcription, sessions | `docs/spec/01-recording.md` |
   | Assist engine, Claude, Aya | `docs/spec/02-assist-core.md` |
   | Assist UI | `docs/spec/03-assist-ui.md` |
   | Dictation, vocabulary, settings, tooling | `docs/spec/04-dictation-settings.md` |

   Write behaviour platform-neutrally first, then the "Mac implementation" note. When a new Apple-only API is used, add a "Windows notes" line.
2. Add an entry at the top of `docs/spec/CHANGELOG.md`: the date, what changed for the user, and the spec sections touched. The `port-to-windows` skill ports these entries, so each one should be something a port can act on.
3. Refactors, renames and test-only changes do not need a changelog entry, unless a documented file, type or format changes.
4. The same public-repo rules apply to the spec.

When you finish a task that changed behaviour, say in your reply which spec sections you updated.
