# MeetingAssistant — Research & Plan

> Status (2026-11-08): **implemented and installed (phases 0–5).** See §8 for what was built, how it was tested and what was measured. Usage: README › Assist.
> Builds on MeetingScribe (PLAN.md, README.md). The live transcription, the floating panel and the recording pipeline stay as they are.

**Decisions:**
- **Name.** The app is renamed **MeetingAssistant**, with two modes:
  - **Scribe:** today's MeetingScribe, local only.
  - **Assist:** the new live assistant.
- **Claude.** Assist runs on the local Claude subscription through the `claude` CLI, not the Claude API.
- **Aya lists.** Clients = `firma`, people = `kontakt`, recaps = `pmMeetings`, playbooks = `wikiPlaybooks`. These are the defaults; **all of them can be changed in settings**.
- **Language.** Questions and summaries follow the language of the meeting.
- **Attendees** come from the conversation and from Aya (the company's contacts). Calendar is left out for now.

**Goal:** while a meeting is recorded and transcribed live, a Claude instance reads along. It knows the client (from the Aya CRM), the playbook for this kind of meeting, and earlier meeting recaps. It keeps these views up to date in real time:

- the next question to go deeper,
- a running summary,
- the attendees and what we know about them,
- points that came up and are still open,
- the playbook checklist (what is covered, what is missing).

After the meeting the app writes a new meeting recap into Aya and proposes updates to the client's CRM record.

---

## 1. What was verified against the Aya API

I tested the key at `https://api.hey-aya.ai` (read-only calls only).

| Call | Result |
|---|---|
| `GET /api/workspaces` (no orgId) | `400 {"error":"orgId required","code":"invalid_input"}` |
| `GET /api/workspaces?orgId=<wrong id>` | **`200 []`**: a wrong org id is not an error, just an empty list |
| `GET /api/auth/me` | `200 {"userId":"78347ee3-…"}`. The key works, but this returns no org id |
| `/api/me`, `/api/organizations`, `/api/orgs`, `/openapi.json`, … | 404 |
| `/mcp` with this key | `403 "An MCP connection needs a workspace-scoped key"` |

Then, with the correct org id:

| Call | Result |
|---|---|
| `GET /api/workspaces?orgId=…` | 14 workspaces. Each has `id`, `slug`, `localizedLabel` (sometimes only `de`), `organizationId`, `isPrivate`, … |
| `GET …/<ws>/record-specs` | 89 record types, the same set in every workspace: `techName`, `localizedLabel`, `icon`, `extra`, `_links.openapi`. **No field definitions.** `extra` only has the title formula, preview layout and default sort. |
| `GET …/<ws>/record-specs/<techName>` | The same object, plus `rules`. `/bricks` returns `[]`. |
| `GET …/<ws>/openapi.json` (with or without key) | **404** `"Workspace … not found"` at first: API documentation was off. Since it was switched on: 200 (§1.1). |
| `GET …/records/firma?query={"pagination":{"limit":1}}` | Works (a Companies record from Aya CRM). |
| `GET …/records/pmMeetings?…` | Works (a Meetings record from Projektmanagement). |

**How records look.** Field keys carry a suffix that tells their kind. The record type's `extra` uses camelCase techNames (`kundePraefix`), while record keys use snake_case (`kunde_praefix__value`).

| Key | Meaning |
|---|---|
| `<field>__value` | Plain value: string, number, bool or date string |
| `<field>__id`, `<field>__computed_title` | Single relation |
| `<field>__ids`, `<field>__computed_titles` | Multi relation |
| `<field>__file_count` | Attachments |

Select fields return the option **key**, not a label: `branche__value: "freiberuflichTechnisch"`, `ma_range__value: "size21to50"`.

**Candidate lists in this organization:**

- **Clients:** `firma` (Companies; rich AI-enriched profile fields such as `firmenprofil`, `anknuepfungspunkte`, `icp_score`), plus `kontakt` (Contacts) for attendees.
- **Recaps:** `pmMeetings` (Meetings in Projektmanagement). It has `title`, `meeting_date`, `meeting_type`, `participants`, `external_participants`, `summary`, `decisions`, `risks` and `next_steps` (markdown), `project`, and `external_meeting_id`. **It has no relation to `firma`**: the link to a client would go through `project` or `participants`, or a new field would be needed.
- **Playbooks:** none at first. You then created `wikiPlaybooks` in the Wiki workspace (§1.1).

What follows from this:

- **The org id must be typed in by the user.** The public API has no way to look it up from the key. The Aya web app shows it in the URL: `app.hey-aya.ai/org/<orgId>/…`. In settings, an empty workspace list right after saving must be reported as "no workspaces found: check the organization id". It must not show as an empty picker.
- `GET /api/auth/me` is a cheap "is the key valid?" check (Test Connection button).
### 1.1 After API documentation was switched on

`GET /api/workspaces/<ws>/openapi.json` now returns 200 without a key. It is an OpenAPI 3.1 document of ~1.2 MB per workspace: 364 paths and three schemas per record type (`<Type>Record`, `<Type>CreateBody`, `<Type>UpdateBody`).

**Endpoints per record type:**

| Method and path | Purpose |
|---|---|
| `GET /records/<t>?query=<Query JSON>` | List |
| `POST /records/<t>` | Create; returns **201** and the record |
| `GET /records/<t>/<id>` | Fetch one record |
| `PUT /records/<t>/<id>` | Update; returns 200 and the record |
| `DELETE /records/<t>/<id>` | Delete |
| `POST /records/<t>/export`, `POST /records/<t>/import` | Flat JSON export and import |

**Write body:** `{"values": {"<field>__value": …, "<rel>__id": "<ULID>", "<rel>__ids": [...]}}`.

**Verified with a test record** in Meetings ("TEST MeetingAssistant API check", deleted afterwards):
- `POST` returns 201 and fills defaults (e.g. `meeting_type: "internal"`).
- `PUT` with only `summary__value` changed that field and **left all others as they were**: updates are partial.
- An unknown key gives `400 {"code":"unknown_column", "unknownColumns":[…]}`.
- `DELETE` returns 200; a `GET` afterwards returns 404.

**What the schema gives per field:** the key, the JSON type, writability, and a kind description such as "Text content", "GitHub-Flavored Markdown text", "Selected option techName", "Referenced record ULID", "ISO datetime string", "Email address" or "Phone number".

**What it doesn't give:**
- **Field labels.** The UI shows a readable version of the key (`next_steps` becomes "Next steps").
- **The options of select fields.** The app learns them from values seen in existing records. For select fields, Claude may only choose from those values, or leave the field empty.
- **Which record type a relation points to.** The relations the app needs are set in settings.

**Filters work** (verified, contacts of one company):

```json
{"filter": {"nodeType": "condition", "brickTechName": "firma", "brickValueKey": "id",
            "comparator": "equal", "valueType": "constant", "value": "<ULID>"},
 "sorting": [], "pagination": {"limit": 50, "offset": 0}}
```

- `brickTechName` is the **camelCase** name (`kundePraefix`), not the snake_case record key.
- `value` is a plain string; a `{"value": …}` object silently matches nothing.
- `sorting` uses `{brickTechName, brickValueKey: "value", descending}`, or `"system"` for `createdAt` and similar columns.

**The four lists:**

| Role | List (workspace) | Fields used |
|---|---|---|
| Clients | `firma` (Aya CRM) | All text fields as context, especially `firmenprofil`, `anknuepfungspunkte`, `branche`, `sub_branche`, `ma_zahl`, `icp_*` and the `*_note` fields. CRM updates go to text, number and boolean fields. |
| People | `kontakt` (Aya CRM) | `first_name`, `last_name`, `position`, `rolle_hintergrund`, `email`, the `bant_*` fields, `gespraechsaufhaenger`. Linked to the company via **`firma__id`** (filter verified). |
| Playbooks | `wikiPlaybooks` (Wiki) | `title`, `area`, `status`, `goal`, `steps` (markdown). One record so far: "Erstgespräch mit Neukunden" (draft). Its steps are `### n. Section` headings with `- item` bullets; each bullet becomes a checklist item (`P3.2` = section 3, item 2). |
| Recaps | `pmMeetings` (Projektmanagement) | `title`, `meeting_date`, `meeting_type` (select), `summary`, `decisions`, `risks`, `next_steps` (markdown), `external_participants`, `external_meeting_id`. |

**Gap: a meeting can't be linked to a company.** `pmMeetings` has no relation to `firma`; its `project` points to `pmProjects`, which has none either. Without a link, the app can't attach the recap to the client or load earlier recaps for that client. **Recommendation:** add a relation field (e.g. "Firma") on Meetings pointing to Companies, and optionally a "Contacts" relation (to `kontakt`) for the external attendees. Until then, the app:
- puts the company name in the title and in `external_participants`;
- searches earlier recaps by company name;
- stores the Aya record id locally, so a recap is never uploaded twice.

---

## 2. How to stream a live transcript into Claude

**Constraint: use the local Claude subscription, not the Claude API.** The app talks to the `claude` CLI (Claude Code, installed at `~/.local/bin/claude`, logged in with your subscription). It does not call `api.anthropic.com` with an API key. Nothing is billed per token; usage counts against the subscription's usage limits.

### 2.1 Options considered

| Option | How it works | Verdict |
|---|---|---|
| **A. One `claude -p` call per tick** | Start the CLI for every update, with the whole transcript so far. | Pays the start-up time (~1 s) on every tick and resends everything. Too slow and wasteful. |
| **B. One long-running `claude` process per meeting, fed over stdin** ✅ | Start `claude -p --input-format stream-json --output-format stream-json` once at meeting start. Each tick writes one user message (the new transcript lines) to stdin. Claude streams back short JSON lines that each change one panel. | **Chosen, and tested** (§2.2). One conversation; Claude Code caches the history automatically; the first output arrives in under 1.5 s. |
| C. Claude Agent SDK (TypeScript/Python) | The same engine as a library. | Would add Node or Python to a Swift app, for no gain over the CLI. Its subscription login is meant for Claude Code itself; the CLI is the supported path. |
| D. Claude API with an API key | Direct HTTP calls. | Ruled out: costs per token, and you want the subscription. Kept as a possible later setting only. |
| E. Let Claude query Aya itself (MCP) | Claude looks up records during the meeting. | Not for v1. Slower, and anyone in the call could steer it. The app loads the Aya context itself before the meeting. |

### 2.2 The chosen design in detail (option B)

**Tested on 2026-11-08** with Claude Code 2.1.319 and a two-chunk German sample meeting:

| Model, effort | First text after a chunk | Update complete | Auth |
|---|---|---|---|
| Sonnet 5.5, low | 0.7–1.7 s | 4.4–5.9 s | subscription (`apiKeySource: none`) |
| Opus 5.5, low | 0.7–1.4 s | 4.8–5.0 s | subscription |

The output was useful: the next questions, the attendees mapped to speaker labels (for example "Anna Müller, Geschäftsführerin = Speaker 2"), summary bullets and open points such as the budget.

**How the process is started.** `AssistSession` starts the CLI with `Process` once per meeting:

```
claude -p --input-format stream-json --output-format stream-json --verbose
       --include-partial-messages
       --system-prompt <MeetingAssist instructions>
       --tools "" --strict-mcp-config --mcp-config '{"mcpServers":{}}'
       --setting-sources "" --disable-slash-commands --no-session-persistence
       --model <opus|sonnet|haiku> --effort low
```

It runs with an empty working directory in Application Support. What the flags do:

- `--system-prompt` **replaces** Claude Code's own coding prompt, so Claude acts only as the meeting assistant.
- `--tools ""` and the empty MCP config: **no tools at all.** Claude can't read files, run commands or reach the web. This is the main protection against someone in the call saying "ignore your instructions…": the worst a hostile transcript can do is put odd text in the panels.
- `--setting-sources ""`, `--disable-slash-commands`, the empty working directory: your own Claude Code settings, hooks, CLAUDE.md files and skills don't leak into the meeting assistant. The test showed `tools=[]` and `mcp=[]` in the init event.
- `--no-session-persistence`: meeting conversations don't show up in your Claude Code session history.
- `--bare` can't be used: it turns off the subscription login and only accepts an API key.
- **The binary is found** via a setting with the default `~/.local/bin/claude`, falling back to `PATH`. At start, the app checks that it exists and is logged in, and otherwise says "Install Claude Code and run `claude` once to log in".

**What is sent.** Each message is one line on stdin: `{"type":"user","message":{"role":"user","content":"…"}}`.

1. **First message, at meeting start:**
   - the client record, as compact `field: value` text;
   - the playbook, with items numbered `P1…Pn`;
   - the last 3 recaps for this client;
   - the meeting mode, the date and the meeting goal.

   Claude answers with initial questions. Pre-meeting questions are useful on their own, and this also warms the cache.
2. **Each tick:** the new **final** transcript lines, for example:
   ```
   [00:12:31] Them (Speaker 2): Wir haben aktuell drei Standorte, aber nur einer nutzt das System.
   [00:12:40] Me: Woran liegt das?
   ```
   Optional user events are added, such as `[user marked Q3 as asked]`, `[user dismissed O2]` or `[user asks: suggest a question now]`.

**What comes back: JSON lines.** Custom tools would need an MCP server, so instead the system prompt asks for **one JSON object per line, nothing else**:

| Line | Effect |
|---|---|
| `{"op":"next_questions","items":[{"id","text","why","playbook_item"?}]}` | Replaces the suggestions. The first one is shown large. Emitted first. |
| `{"op":"summary_append","bullets":[…]}` and `{"op":"summary_replace","index":n,"text":"…"}` | Running summary |
| `{"op":"open_point","id","text","raised_by","status":"open\|answered\|parked"}` | Open points (upsert by id) |
| `{"op":"attendee","name","role"?,"company"?,"notes"?,"speaker"?}` | Attendees; maps "Speaker 2" to a real name |
| `{"op":"playbook","item":"P3","status":"missing\|partial\|covered","evidence"?}` | Playbook checklist |

- `--include-partial-messages` delivers the text as it is generated. The app buffers it and **applies each line as soon as its newline arrives**, so the next question appears ~1–2 s after a tick, before the rest of the update is written.
- Each line is decoded with `Codable`. Lines that don't parse are ignored and logged. The `result` event marks the end of the update.

**When a tick is sent.** One scheduler, with at most one update in flight. While Claude is answering, new lines accumulate for the next tick.

- Only `LiveLine`s with `isFinal == true` are sent. Partial lines still change.
- A tick fires when **≥ 40 new words and ≥ 15 s since the last tick**, or when **≥ 45 s have passed with any new text**.
- A tick also fires right after the other side finishes a turn that ends in a question, or after a long monologue. That is when a follow-up question is needed.
- **"Suggest now"** (button and hotkey) sends immediately.
- Speaker labels can be missing at first (the diarizer lags). The app waits up to ~3 s for a label before sending a line. After that, it sends `Them (?)`.

**Language:** the system prompt tells Claude to write everything in the language currently spoken in the meeting. If the language switches, new output switches too.

**Model:** a setting, Opus (default), Sonnet or Haiku, with `--effort low`. Opus at low effort was as fast as Sonnet in the test. Haiku saves the most usage.

### 2.3 Subscription usage limits instead of cost

There is no per-token bill, but each update counts against your plan's usage limit (a rolling 5-hour window plus a weekly limit, shared with your normal Claude and Claude Code use).

- **Size of a meeting hour:** ~140 updates, each reading the cached history (~25k tokens on average) and writing ~500 tokens. Over a meeting hour, that is a meaningful part of a 5-hour window. Exactly how much depends on the plan (Pro vs Max) and has to be measured. On an API key, the same work would cost about $1–3 per hour; the CLI reports this as `total_cost_usd` (an equivalent, not billed).
- **How the app keeps usage down:**
  - updates are spaced out (§2.2);
  - the model is a setting (Sonnet or Haiku use less);
  - long meetings are restarted with a short summary (§2.4);
  - a "pause assist" button stops updates while recording continues.
- **When the limit is hit,** the CLI returns an error result. The app shows "Claude usage limit reached; assist paused until HH:MM". Recording and live transcription continue, and the recap can be generated later.
- The status bar shows the update count and the API-equivalent cost of this meeting as a rough usage indicator.

### 2.4 Long meetings: restarting with a summary

The transcript itself is small (one hour ≈ 15–20k tokens), but every update re-reads the whole history. Claude Code would also compact on its own near its context limit, in a way the app doesn't control.

At **~60 minutes or ~80k tokens of context**, the app starts a fresh `claude` process instead. Its first message holds:

- the same meeting context;
- the **current state of all panels** (as JSON);
- the **last 5 minutes of transcript**.

Then it continues as before. The switch happens between ticks, so the user doesn't notice.

### 2.5 Errors, privacy and safety

- **The process dies or hangs** (no output for 60 s): restart it with the context and the panel state, as in §2.4. Lines keep accumulating and the next tick catches up. The window shows "Assist reconnecting".
- **Not logged in, or the CLI is missing:** shown on the prep screen before the meeting starts, not in the middle of it.
- **Usage limit:** see §2.3.
- **The transcript is untrusted input.** Claude has no tools, its output only changes the app's own panels, and **nothing is written to Aya during the meeting**. Every Aya write after the meeting goes through a review screen.
- **Privacy:** this mode sends the transcript and the Aya context to Anthropic, under your subscription's terms. Until now MeetingScribe was strictly local. MeetingAssist will show this clearly (a cloud badge and a one-time explanation), and the normal recording mode stays local-only.
- **Keys:** the Aya key is stored in the macOS **Keychain**. No Anthropic key is needed. The repo is public, so no key may ever appear in source, tests or eval files.

## 3. Aya integration

### 3.1 Settings → Aya

- **Fields:** Organization ID; API key (Keychain, shown masked). "Test connection" calls `GET /api/auth/me`, then `GET /api/workspaces?orgId=`.
- **Four slots,** pre-filled with the defaults above and all changeable: **Clients**, **Contacts**, **Playbooks**, **Meeting recaps**. Each slot has a workspace picker (`localizedLabel` in the system language, falling back to `en`, then `slug`) and a list picker (`record-specs` → `localizedLabel`, storing `techName`).
- **On save, for each slot:**
  - fetch the workspace's `openapi.json` and store the slot's fields: key, kind, type and writability;
  - learn the select options from up to 200 existing records;
  - store everything as `aya-schema.json` in Application Support;
  - offer "Refresh schemas".
- **Link fields,** chosen from the relation fields of the matching list. All are optional:
  - **Contacts → client** (default `firma__id`).
  - **Recap → client.** None exists today, see §1.1. If it is empty, the app falls back to the company name.
  - **Recap → contacts.** Optional.
- **Field roles for recaps,** pre-filled for `pmMeetings` and editable: which field holds the title, the date, the summary, and so on. Every other writable text field is offered to Claude by its key.

### 3.2 `AyaClient` (Swift actor, MeetingScribeCore)

- `workspaces()`, `recordSpecs(ws)`, `records(ws, techName, query)`, `search(ws, techName, q, limit)`, `record(ws, techName, id)`, `openAPI(ws)`, then `create(...)` and `update(...)` once verified.
- **Errors** become a typed `AyaError`, decoding `{error, code}`:
  - **403:** "No permission for this list or field".
  - **404:** an unknown workspace, list or record; the app offers to re-pick it in settings.
  - **402 or 429 `limit_exceeded`:** "Aya API credits used up", with no retry.
  - **429 otherwise:** wait for `Retry-After`, up to 3 times.
  - **503:** retry with backoff, up to 3 times.
- **Credits:** every call costs Aya API credits, so record lists are cached on disk and only refreshed on demand (the refresh button) or when older than 24 h. The pickers use `/search?q=` while typing, with a 250 ms debounce.

---

## 4. MeetingAssist mode (UI)

### 4.1 Prepare

A new sidebar entry or tab: **MeetingAssist**.

- **Client picker:** cached list, type-ahead search, refresh button.
- **Playbook picker:** same.
- **Optional "Goal for this meeting"** free text. It goes into the context and costs nothing extra.
- **Preview:** the client's key fields and the last recap date, so the user sees that the right record was picked.
- **Start Assist.** It fetches the full client record, the company's **contacts**, the playbook and the last 3 recaps. Then it starts the recording with live transcription forced on and opens the Assist window.
- **The contacts are pre-loaded as likely attendees,** with position, background and BANT notes. Claude matches them to the speakers as people introduce themselves. Attendees that aren't in Aya yet are shown as "new", and the post-meeting step offers to create them as contacts.
- **The `claude` process starts while the recording starts.** Its first message (the context) produces the opening questions, so they are ready before anyone speaks.
- **The prep screen checks the Claude CLI** (installed and logged in) and shows a problem before the meeting, not during it.

### 4.2 During the meeting: the Assist window

One resizable window with panes. The layout is saved.

```
┌──────────────────────────────────────────────────────────────┐
│ ● 00:23:41  Client: Müller GmbH · Playbook: Discovery  ⟳ 57 │  status bar (rec, assist state, updates)
├──────────────────────────────────────────────────────────────┤
│  NEXT QUESTION                                    [Asked][⟳]  │  large, 2 alternatives below
│  "Was müsste passieren, damit die anderen zwei Standorte …"   │  + "why" in small text
├──────────────────────┬───────────────────┬───────────────────┤
│ SUMMARY              │ OPEN POINTS       │ PLAYBOOK          │
│ • …                  │ ○ Budget 2027     │ ✓ Ist-Situation   │
│ • …                  │ ○ Entscheider?    │ ◐ Pain points     │
│                      │ ✓ Rollout-Datum   │ ○ Budget          │
├──────────────────────┴───────────────────┴───────────────────┤
│ ATTENDEES: Anna Müller (GF, Speaker 2) · Tom Weber (IT) …    │
├──────────────────────────────────────────────────────────────┤
│ live transcript (the existing view, 3–4 lines, collapsible)  │
└──────────────────────────────────────────────────────────────┘
```

- **Changes are briefly highlighted,** so the user sees what is new at a glance.
- **Question actions:**
  - **"Asked"** marks a question used.
  - **"⟳"** means suggest something else.
  - Clicking an alternative promotes it to the top.

  Each action becomes a user event in the next tick, so Claude learns what was used.
- **Open points and playbook items can be ticked by hand.** These also become events.
- **The floating panel** gets a compact MeetingAssist variant: the next question plus the open-point count, for full-screen calls.

### 4.3 After the meeting

1. **Stop.** The normal pipeline runs: offline transcription and diarization, which give a better transcript than the live one. The window shows "Preparing recap…".
2. **Recap draft.** One separate one-shot call, `claude -p --output-format json --json-schema <schema> --effort medium`, with the same no-tools isolation flags. Tested: the result comes back as schema-valid JSON in `structured_output`. The call receives:
   - the final transcript;
   - the context;
   - the final panel state;
   - the **recap field schema**, as the `--json-schema` argument.

   The JSON schema is generated from the stored Aya schema:
   - only writable field types are included: text, long text, number, date, single and multi select (as `enum` from the options), and boolean;
   - the client relation is filled by the app, not by Claude.

   The result is guaranteed to match the schema.
3. **Review screen.** An editable form with one row per recap field. The full transcript can be attached if the recap has a long-text field for it. **Upload** creates the record and stores the Aya record id in `session.json`, so it can't be uploaded twice. The app then offers "Open in Aya".
4. **CRM update.** A second structured call proposes changes. They are shown as a diff with a checkbox per item, nothing pre-applied. "Apply selected" sends the writes.
   - **Client record:** `[{field, old, new, reason}]`, applied with one `PUT` containing only the changed keys.
   - **Known contacts:** the same kind of diff (e.g. `position`, `rolle_hintergrund`, `bant_*`).
   - **New people:** proposed as new contacts (`POST`, with `firma__id` set).
5. **Later.** Both steps can be repeated from the session detail view, for example after renaming speakers.

---

## 4a. Renaming MeetingScribe to MeetingAssistant

- **The app** becomes `MeetingAssistant.app`, with the display name "MeetingAssistant". A mode switch in the sidebar or toolbar shows **Scribe** (record, sessions, dictation: everything that exists today) and **Assist**. Swift targets: `MeetingScribe` becomes `MeetingAssistant`, `MeetingScribeCore` becomes `MeetingAssistantCore`, `msctl` stays.
**Done on 2026-11-08:**
- **The bundle id stays `de.local.MeetingScribe`** (changed from the original plan). macOS ties the microphone, system-audio and accessibility (dictation) permissions and all settings to it. Keeping it means nothing has to be granted again or migrated. It is invisible to the user. The log subsystem and the internal audio device ids keep the old prefix too.
- **Data.** `AppPaths` moves `~/Library/Application Support/MeetingScribe` to `…/MeetingAssistant` on first use. If the move fails, or only an empty new folder exists, it keeps using the old folder, so recordings are never hidden. Covered by unit tests. Verified: all 12 recordings were moved. `MEETINGASSISTANT_HOME` replaces `MEETINGSCRIBE_HOME`; the old name still works.
- **Install.** `scripts/build_app.sh --install` installs `/Applications/MeetingAssistant.app` and removes `/Applications/MeetingScribe.app`.
- **Mode switch:** a toolbar picker, plus View › Scribe (⌘1) and Assist (⌘2). The choice is remembered. Assist shows a placeholder page.
- **Repo** renamed to https://github.com/CLNMR/MeetingAssistant (the old URL redirects), and the local `origin` is updated.

---

## 5. Code structure

**New in `MeetingScribeCore`** (testable with `msctl`, no UI):

- `Aya/AyaClient.swift`: HTTP, errors and retry, described in §3.2.
- `Aya/AyaSchema.swift`: field schema model, parsing from openapi.json (or inference from sample records), and JSON-schema generation for `--json-schema`.
- `Aya/AyaConfig.swift`: org id, the four slots, link fields and field roles, cached lists; Keychain access.
- `Claude/ClaudeCLI.swift`:
  - finds the `claude` binary and checks the login;
  - starts the long-running stream-json process (`Process` with stdin/stdout pipes);
  - parses the NDJSON events: `system/init`, `stream_event` text deltas, `assistant`, `result`;
  - one-shot `--json-schema` calls for the recap and the CRM update;
  - detects a crash, a hang or the usage limit.
- `Claude/AssistSession.swift`:
  - tick scheduler, restart with summary (§2.4), process restart;
  - JSON-line parser that applies each op to `AssistState`;
  - update count and API-equivalent cost from the `result` events.
- `Claude/AssistState.swift`: questions, summary, open points, attendees, playbook items (Codable, saved into the session folder).
- `Claude/Prompts.swift`: the system prompt, the context renderer, and the recap/CRM prompts.

**New in the app:**

- `MeetingAssistPrepView.swift`, `MeetingAssistWindow.swift`, `RecapReviewView.swift`.
- `SettingsView`: an Aya section, and a Claude section (path to the `claude` binary, model, update interval).
- `AppState`:
  - an `assist: AssistSession?`;
  - `startRecording` gets an assist variant;
  - the live `onUpdate` also feeds `assist.ingest(lines)`.

**`msctl` additions:**

- `msctl aya workspaces | specs <ws> | records <ws> <techName> | get …`
- `msctl assist-replay <session-dir> [--speed 4]`: plays an existing recorded session's transcript through `AssistSession` at real time (or faster). It prints every tick's ops, latency (time to first op, total) and the API-equivalent cost as a usage measure. This is the main tool for tuning prompts, cadence and model choice without sitting in a live meeting, and it gives the latency and cost numbers in §2.

---

## 6. Phases

**Phase 0: Rename (about half a day)**

Rename the app, the targets and the folders, migrate the data folder and the settings, and add the Scribe/Assist mode switch with an empty Assist page. *Done when:* the installed MeetingAssistant shows all old sessions and records exactly as before.

**Phase 1: Aya connection (about 1 day)**

1. ~~Probe the read endpoints, `openapi.json`, filters and writes~~ (done, §1.1).
2. Build `AyaClient`, `AyaSchema` and the `msctl aya` commands, with unit tests on recorded JSON (no key in fixtures).
3. Build Settings → Aya: key in Keychain, org id, the three slots, schema storage.

*Done when:* the four lists are pre-selected and changeable in settings, and `msctl aya` lists companies, the contacts of one company, and playbooks.

**Phase 2: Assist engine, headless (about 1–2 days)**

1. Build `ClaudeCLI`, `AssistSession`, the JSON-line protocol and the prompts.
2. Run `msctl assist-replay` on 2–3 real recorded meetings. Measure:
   - time to the first op;
   - usage per meeting hour (from the `result` events, and from how far the subscription's usage display moves);
   - the cache hit rate (`cache_read_input_tokens`).

   Read the suggestions and iterate on the prompt.
3. Decide the defaults: model, effort and tick interval.

*Done when:* a replayed 30-minute meeting gives useful questions, with a median time to the first op under ~2 s and acceptable usage of the subscription limit.

**Phase 3: MeetingAssist UI (about 1–2 days)**

1. Build the prep screen with pickers, refresh and preview.
2. Build the Assist window, the user events, the compact floating-panel variant, and the status bar.
3. Test end to end with a real call.

**Phase 4: After the meeting (about 1 day)**

1. Recap generation with structured output from the Aya schema, the review form and the upload.
2. CRM diff and patch.
3. Repeat from session detail; record ids in `session.json`.

**Phase 5: Polish**

Restart with summary for long meetings, a clear message when the usage limit is hit, the privacy notice, README.

---

## 7. Open questions for you

1. **A company link on Meetings:** later. For now, recaps are matched to a client by name (§1.1).

---

## 8. Implementation (2026-11-08)

**Code:**

| Area | Files |
|---|---|
| Aya | `Sources/MeetingAssistantCore/Aya/` — `AyaClient` (HTTP, typed errors, retries), `AyaSchema` (fields from OpenAPI, options learned from records, value coercion), `AyaConfig` (settings, Keychain), `AyaService` (default lists, schemas, picker cache, meeting context, recaps by link or by name) |
| Assist | `Sources/MeetingAssistantCore/Assist/` — `ClaudeCLI` (finding/logging-in check, long-running stream-json process, one-shot `--json-schema` calls), `AssistModels` (context, panels, JSON-line ops, playbook parser), `AssistPrompts`, `AssistEngine` (utterance batching, scheduling, streaming apply, restarts, usage-limit pause), `Recap` (recap draft, CRM proposal, `assist.json`) |
| App | `AssistController`, `AssistView` (prepare, live, review, floating-panel banner), `AssistSettingsView`, hooks in `AppState`, *Assist Recap* button in the session view |
| CLI | `msctl aya …`, `msctl assist check / replay / live / recap / crm` |
| Tests | `Tests/MeetingAssistantCoreTests/AssistTests.swift` (op parsing and merging, playbook parsing, OpenAPI parsing, coercion, query shape, recap schema, live-line batching) — 28 tests in total pass |

**Deviations from the plan:**
- Lists are picked as one list per slot (shown with its workspace); a list is always used in its home workspace, which is where its fields are defined. There is no separate workspace picker.
- Select options can't be read from Aya's OpenAPI; they are learned from up to 200 records per list. Claude may only choose values seen there.
- Attendees carry a `present` flag, so people who are only mentioned (or expected but absent) are shown separately.
- Default update frequency is "Normal" (≥ 60 new words and ≥ 20 s, or any new text after 60 s); "Frequent" and "Economical" are settings.
- Debug: launching the app with `-assistSnapshot <dir>` renders the Assist screens offscreen to PNGs (used for checking the layout without a visible screen).

**Tested against the live services:**
- Aya: connect, default lists, schemas of all four lists, picker lists (74 companies, 1 playbook), search, contacts filtered by company, full meeting context; recap created with all fields (title, date, type `customer`, summary/decisions/risks/next steps as markdown, attendees, external id) and deleted again (title prefixed `TEST`).
- Claude Code: subscription login detected (`apiKeySource: none`); also works with a minimal GUI-like environment.
- Replay of a real recorded call (4 min at 4×): 9 updates, median first op 3.7 s, median update 5.8 s.
- Synthetic German first-contact sales call (4 min, real time, "Frequent"): 18 updates, median first op 3.3 s, median update 7.2 s; questions, summary, open points, attendee matching (Aya contact recognized from the introduction) and playbook progress all correct. API-equivalent cost $0.47 for 4 minutes at that frequency (reported by Claude Code; nothing is billed on a subscription), which is why "Normal" became the default.
- Recap draft: 18 s; CRM proposal: client fields with reasons, contact updates and new contacts.
- Real audio of a recorded call through the live transcriber into the engine (`msctl assist live`): finished utterances batched with stable speaker names, 7 updates in 2.4 min.

**Not tested:** a real live meeting in the app (start, stop, review by clicking through the UI), because the screen was locked while building; the UI was checked with offscreen renders instead.
