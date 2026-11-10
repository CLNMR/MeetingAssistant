## Assist user interface

Assist is the second of the app's two modes (Scribe, Assist). It walks through three phases in one content area: **Prepare**, then **Running** (live), then **Review**. A **Meetings** panel on the left lists past Assist meetings. Aya is the user's CRM/project tool (web app at `app.hey-aya.ai`), and Claude runs locally through the Claude Code CLI. This section describes the screens and the controller behind them. The engine, prompts, the Aya API and recap building are covered in other sections and only referenced here.

All user-visible strings in the app are English and are quoted verbatim below. Strings marked *(dynamic)* are composed at runtime.

---

### 1. Window, rail and mode switch

**Window.** One main window titled "MeetingAssistant" with a hidden title bar: the content runs under the traffic-light buttons. Minimum size is 980 × 620. The page background is token `page`. The content has 8 px padding left, right and bottom and 2 px at the top.

**Layout.** `[Rail 48 px] 8 px gap [Mode content, fills the rest]`.

**Rail** (48 px wide, full height, surface with radius 14, fill token `rail`, hairline border). From top to bottom:

| Element | Behaviour |
|---|---|
| Logo tile (32 × 32 blue gradient rounded square with a white rounded-triangle mark) | If Aya is configured, clicking it opens the **Aya meetings list** in the browser, at `https://app.hey-aya.ai/workspace/<meetingsWorkspaceId>/recordlist/<meetingsListTechName>`. If no meetings list is chosen, it opens the organization at `https://app.hey-aya.ai/org/<orgId>`. Tooltip: "Open the meetings in Aya", or "Open Aya" when it falls back to the organization. With no org id the logo is inert, with the tooltip "Aya is not connected yet. Connect it in Settings › Assist." Hand cursor when it is a link. 8 px below it: |
| Mode button "Scribe" (icon-only 32 px, borderless) | Switches to Scribe. Tooltip "Scribe: record and transcribe on this Mac (⌘1)". Shows the active style (primary tint 12 % fill, primary icon) when selected. |
| Mode button "Assist" | Switches to Assist. Tooltip "Assist: live meeting assistant with Aya and Claude (⌘2)". |
| *(spacer)* | |
| "Floating Live Transcript" icon button | Toggles the floating transcript window (⇧⌘L). |
| "Settings" icon button | Opens Settings (⌘,). |
| Initials circle (28 px, primary 12 % fill, primary text, 11 pt semibold) | First letters of the first two words of "My name" from preferences, or "ME". Tooltip: the full name. |

**Recording dot on mode buttons.** A 7 px red dot sits at the top-right of the **Scribe** button while any recording runs and the user is in a different mode. It sits on the **Assist** button while an Assist meeting runs (phase Running) and the user is in Scribe.

**Mode persistence.** The selected mode is stored (key `appMode`) and restored on launch.

**Keyboard shortcuts (menu commands):** ⌘1 Scribe, ⌘2 Assist, ⇧⌘R Start/Stop Recording, ⌘I Import Audio…, ⇧⌘L Floating Live Transcript. The live view adds ⇧⌘Space "Suggest now". In the review, Return triggers "Done" (default button).

**Global error alert.** Any `errorMessage` on the app state shows a modal alert titled "MeetingAssistant" with an "OK" button. If the message mentions "System Settings", a second button "Open System Settings" opens the microphone privacy pane.

*Mac implementation:* `MeetingAssistantApp.swift` (WindowGroup, `.windowStyle(.hiddenTitleBar)`, commands), `ContentView.swift` (`ContentView`, `AyaRail`, `RailLogo`), `AppState.appMode` (`AppMode` enum: `scribe`, `assist`). The logo URL comes from `AssistController.logoURL`, which uses `ayaURL(.meetings)` and falls back to `orgURL`. Path components are percent-encoded.

*Windows notes:* Use a custom title bar (`ExtendsContentIntoTitleBar`) to get the edge-to-edge look. Rail icons: map SF Symbols to Segoe Fluent Icons. Use `KeyboardAccelerator`s with Ctrl in place of ⌘ (Ctrl+1, Ctrl+2, Ctrl+Shift+R, Ctrl+I, Ctrl+Shift+L, Ctrl+Shift+Space). Open links with `Launcher.LaunchUriAsync`.

---

### 2. Assist layout and routing

| Condition | What fills the Assist content area |
|---|---|
| Aya not connected (no API key, or the config is incomplete) | **Connect** screen, full width, no Meetings panel |
| Phase = Running | **Live** screen, full width, no Meetings panel |
| Otherwise | `[Meetings panel 270 px] 8 px [Router]`. The router area is clipped to radius 16. |

Router: Prepare shows the **Prepare** screen when connected and the **Connect** screen when not. Running shows **Live**. Review(sessionId) shows **Review**.

Whenever the router appears and no Claude binary is known yet, it runs **checkClaude** in the background (see §10).

"Connected" means an API key is stored **and** all required lists are set. The required lists are clients, contacts, templates and meetings. Projects and members are optional.

*Mac implementation:* `AssistView.swift`: `AssistHomeView` → `AssistLayout` → `AssistRouter`. State lives in `AssistController` (an `ObservableObject` on the main actor), owned lazily by `AppState.assist` and created at app launch so the Aya lists load early.

---

### 3. Shared building blocks

| Component | Spec |
|---|---|
| **StepLine** (status line for one async step) | It has four states. *idle* shows nothing. *working(msg)* shows a small spinner and the message (12 pt, `textMuted`). *done(msg?)* shows a check-circle icon and the message in `success`, or nothing when the message is empty. *failed(msg)* shows a warning-triangle icon and the message in `warning`, and the text is selectable. Every async action in Assist reports through one of these. |
| **PanelBox** | Card (surface) with 14 px padding and an eyebrow title with an icon. An optional trailing element (usually a count, 11 pt medium monospaced digits, `textMuted`) sits at the right of the title. The content fills the box. |
| **PageHeader** | Eyebrow (in `agentTextMid`), then the title (24 pt semibold, one line), then an optional subtitle (13 pt `textMuted`, one line). Trailing controls are bottom-aligned on the right. |
| **Eyebrow** | Uppercase text, 11 pt semibold, letter spacing 1.2, optional 11 pt icon with a 6 px gap, default colour `textMuted`. |
| **Tag** | Small label, 11 pt medium, in the given colour on a 12 % fill of that colour, radius 6, padding 7 × 3, optional 10 pt icon. |
| **Highlight** | Items that the latest Claude update changed get a brief `agent` 16 % background (radius 6, 5 × 2 padding). It fades out over 0.6 s ease-out when the item leaves the "changed" set. |
| **FlowLayout** | Wrapping horizontal layout with 6 px spacing. Used for chips. |
| **FlowChips** | Pill chips showing the name (medium weight) plus optional detail (`textMuted`). 12 pt, padding 10 × 4, `card` fill, `border` hairline. |
| **Initial avatar** | The first letter or digit of a name, uppercased, or "•" if there is none. |
| **Speaker colour** (used in the review) | "Me" is always `primary`. Other speakers get a colour from a fixed 10-colour palette (orange, purple, green, pink, teal, brown, indigo, mint, red, cyan), picked by FNV-1a 64-bit hash of the speaker id modulo 10. |

*Mac implementation:* `StepState` enum in `AssistController.swift`. Views in `AssistView.swift` and `AyaTheme.swift`. `speakerColor` is in `ContentView.swift`.

---

### 4. Connect screen (Aya not connected)

This is a centred card, 500 px wide, with 28 px padding, radius 14 and a soft shadow (black 8 %, blur 24, y 12). It sits on the **backdrop**: two soft radial glows, one blue at (35 %, 60 %) and one teal at (90 %, 20 %).

Contents:
1. A 44 px logo tile, the eyebrow "Assist" and the title "Connect Aya" (22 pt semibold).
2. Explanatory text: "Pick a planned meeting, or a template, client and project from Aya, start the meeting, and Claude suggests the next question, keeps a running summary, and tracks attendees, open points and the template's checklist while you talk. Afterwards it saves the recap to the meeting in Aya."
3. **Organization id** text field (placeholder "from app.hey-aya.ai/org/<id>/…"). Prefilled with the stored org id.
4. **API key** secure field with a key icon. The placeholder is "stored in the Keychain" when a key exists, else "aya_key_…".
5. **Connect** button (primary, default/Return). It is disabled when the org id is empty, when no key is typed and none is stored, or while connecting. Next to it is the connect StepLine.
6. Footnote (11 pt `textSubtle`): "The lists (clients, contacts, projects, templates, meetings) can be changed later in Settings › Assist."

**Connect flow:** store the key (if one was typed) in the secure store, then verify it with `me`, auto-select the lists, then read the schemas ("Reading the lists' fields…"), then load clients, projects and templates ("Loading clients, projects and templates…"), then load the workspace/list catalogue. On success the StepLine shows "Connected. N clients, N projects, N templates." On error it shows the error text. The key field is cleared after every attempt. If the org id or key is missing, the StepLine shows "Enter the organization id and the API key."

**On app start** (if an org id and key exist): when the config is incomplete or no templates are cached, run "upgrade lists" (auto-select the missing lists, refresh schemas, refresh lists, with the status "Updating the Aya lists…"). Otherwise just refresh the lists.

**Refresh lists:** the StepLine shows "Loading from Aya…". It loads clients, templates, projects (if configured), members (if configured, errors ignored) and the planned meetings, then sets "lists updated at". A selected template or planned meeting that has disappeared is deselected. Afterwards, if the vocabulary feature "use Aya names" is on and either 12 h have passed since the last fetch or the term version changed, it refreshes the vocabulary from company, contact, member and project names. Person names that contain "@" are skipped.

*Mac implementation:* `AssistConnectView` in `AssistView.swift`. `AssistController.connect / upgradeLists / refreshLists / refreshVocabulary`. Lists are cached on disk (`AyaCache`) so the pickers fill instantly at launch.

*Windows notes:* Store the key with `PasswordVault` (Credential Locker) or DPAPI. The UI says "Keychain", so change the wording to "stored securely".

---

### 5. Prepare screen

Padding is 20 px on the backdrop. Every time the screen appears, the planned meetings are reloaded (a cheap call).

```
┌ PageHeader ─────────────────────────────────────────────────────────────┐
│ ASSIST / "Prepare meeting" / subtitle      "Aya lists updated 5 min ago" [⟳ Refresh] │
├ Planned strip (card) ───────────────────────────────────────────────────┤
│ 📅 PLANNED IN AYA  today and the next 14 days        (hint on selection)│
│ [+ New meeting] [card] [card] [card] …  (horizontal scroll)             │
├──────────────┬──────────────────────────────────────────────────────────┤
│ Client column│ [Template card] [Project card]   (equal height)          │
│ 260–340 px   │ [Meeting: Call|In person|Hybrid   Goal…]                 │
│ (ideal 300)  │ [What Claude will know (preview, fills height)]          │
│              │ min width 380                                            │
├──────────────┴──────────────────────────────────────────────────────────┤
│ footer: Claude status line …                     StepLine [🎤 Start Assist] │
└──────────────────────────────────────────────────────────────────────────┘
```

**Header.** Eyebrow "Assist", title "Prepare meeting", subtitle "Pick a meeting planned in Aya, or choose a template, client and project. Claude reads them before the meeting starts." On the right: "Aya lists updated <relative time>" (11 pt `textSubtle`, shown once lists have been fetched) and a **Refresh** button (secondary, arrow icon). Its tooltip is "Reload planned meetings, templates, clients and projects from Aya", and it is disabled while lists load.

**Planned strip** (card, 14 px padding). Eyebrow "Planned in Aya" with a calendar icon and the caption "today and the next 14 days". When a planned meeting is chosen, a right-aligned hint reads "The recap is saved to this meeting afterwards." Below is a horizontal scrolling row of **cards** (210 × 84, radius 10, 8 px spacing):
- The first card is always **"New meeting"**, with a plus icon and the text "Choose template, client and project yourself" (muted). Its tooltip is "A new meeting is created in Aya afterwards".
- Then one card per planned meeting. The top line has a calendar icon and the date: "Today 14:00", "Tomorrow 09:30", or "Wed 12 Nov 10:00" (weekday abbreviated, day, month abbreviated, time), or "No date". A **"Notes"** tag (muted) appears if the meeting already has a summary, decisions or next steps. The title is 13 pt medium with up to 2 lines. A third line lists the linked client · project · template titles (11 pt muted, 1 line). The tooltip is the title plus " · already has notes in Aya" when applicable.
- Selected card: primary 10 % fill, primary 60 % border, primary top line. Unselected: `card` fill, `border`, `textMuted` top line.
- **Picking a planned meeting** copies its linked client, project and template into the selections. Which link fields are used is configured in Settings. Picking "New meeting" after a planned meeting clears the meeting id and every client, project or template that still has the value the planned meeting filled in; values the user changed are kept.

**Client column** (card, fills the height). Eyebrow "Client" with a building icon and the client count on the right. A **Search** field with a magnifier icon. A scrolling list of rows (2 px spacing):
- The first row (only while the search is empty) is **"No client"**, with a "minus" avatar and the subtitle "Internal meeting, or project only".
- Each client row has a 28 px rounded-square avatar with the initial, the title (13 pt, semibold when selected), and a subtitle of industry · domain · region where present (11 pt muted, 1 line).
- Selected row: primary 10 % background, and the avatar uses the brand gradient with white text. Unselected avatar: primary 10 % with primary text.
- **Search:** the cached list is filtered locally (case-insensitive contains). If the text has ≥ 2 characters **and nothing local matches**, Aya is searched server-side after a 250 ms debounce. Remote hits not already present are appended below the local hits.
- The list-loading StepLine sits at the bottom.

**Template card.** Eyebrow "Template" with a checklist icon, then an Aya select with "No template" plus all templates. When a template is selected, tags show: the template type (primary) if set, and "N checklist items" (agent colour, checklist icon) counted from the template's agenda field. Below the tags is the template's goal/description (12 pt muted, max 3 lines).

**Project card.** Eyebrow "Project" with a folder icon. If no project list is configured it shows "No project list chosen in Settings › Assist.". Otherwise an Aya select with "No project" plus all projects, and a muted tag with the project status when one is set.

**Meeting row** (card). Eyebrow "Meeting" with a two-people icon, then a segmented control **Call | In person | Hybrid** (max 300 px), then the **Goal** field (placeholder "Goal for this meeting (optional)", target icon, grows from 1 to 3 lines).

**"What Claude will know" preview** (agent surface: teal border and glow while a preview exists). Eyebrow "What Claude will know" with a sparkles icon, in the agent colour.
- Whenever the meeting, client, project or template selection changes, any pending load is cancelled. After a 300 ms debounce the meeting context is loaded from Aya (StepLine "Loading from Aya…"). Results for an outdated selection are discarded. With nothing selected the preview is empty and the state idle.
- Content scrolls and appears in this order: **Meeting** (title, 15 pt semibold). **Client** (title, plus the first field whose key contains "profil", up to 5 lines). **Project** (title). **Template** (title, plus the first 6 checklist items joined by " · ", with " …" if there are more, max 3 lines). **Contacts** (FlowChips of name + position). **Earlier meetings** (rows of date `YYYY-MM-DD` in monospaced 11 pt plus title, or "None found").
- Empty state (nothing chosen, not loading): "Nothing chosen yet: Claude then works from the conversation alone. Pick a planned meeting, a template, a client or a project to give it context."
- Errors appear in the StepLine at the bottom.

**Footer.**
- Left, the **Claude line**:
  - When the Claude check failed: a warning icon, the message, and a small secondary **"Check Again"** button.
  - While checking: a spinner with "Checking Claude Code…".
  - Otherwise, with a lock icon (11 pt `textSubtle`): "Claude <Model> via your Claude subscription · transcript text is sent to Claude, audio stays on this Mac" *(dynamic: model capitalised, e.g. "Opus")*.
- Right: the start StepLine and the **Start Assist** button (agent style, large, pill, mic icon). It is disabled unless Aya is connected, Claude is ready (binary found and logged in), no recording is running, the phase is Prepare, start is not already running, and the preview is not loading. Tooltip: "A recording is already running." while recording, else "Starts the recording with live transcript and the assistant."

**Start flow:**
1. StepLine "Loading the meeting context from Aya…". Use the loaded preview if there is a selection, load it fresh if the preview is missing, or use an empty context if nothing is selected.
2. Build the meeting context from the Aya data, goal, mode, "my name" (default "Me") and now.
3. StepLine "Starting the recording…". Set the recording mode and title (the context title). Force live transcription on, register a hook that receives finished live lines and forwards them to the engine, and register a "recording stopped" callback. Then start a normal recording.
4. If the recording did not start, undo the hooks and show the app's error message (or "The recording did not start.") in the StepLine. The global error is cleared so no alert appears.
5. Otherwise save an initial Assist record for the session, reset the live state with the template checklist and the template tables, set the status to Starting, unpause, switch the phase to **Running** and start the engine. Engine state updates carry a sequence number, and updates that arrive out of order on the UI thread are dropped.

*Mac implementation:* `AssistPrepView` in `AssistView.swift`. `AssistController.meetingID/clientID/projectID/templateID` (each `didSet` triggers `loadPreview`, and `meetingID` triggers `meetingChosen`). Also `start()`, `canStart`, `searchClients`, `refreshUpcoming`, and `PublishSequence`.

*Windows notes:* Debounce with a `CancellationTokenSource` per selection change. The horizontal card strip is a `ScrollViewer` with a horizontal `StackPanel`/`ItemsRepeater`.

---

### 6. Live screen (phase Running)

The live screen is full width. Vertical stack with 8 px gaps:

```
┌ Status bar (agent surface) ─────────────────────────────────────────────┐
├ [Catch-up card]  (only when used)                                       │
├ Upper area (scrolls only if too small) ─────────────────────────────────┤
│  Next-question card (agent surface)                                     │
│  Panels row: [Summary] ┃ [Open points] ┃ [Checklist]  (resizable widths) │
│  [Tables card]  (only if the template has tables)                       │
├ ═══ resize handle (vertical) ═══ ───────────────────────────────────────┤
│  Attendees row                                                          │
│  Transcript panel (height adjustable, default 240)                      │
└──────────────────────────────────────────────────────────────────────────┘
```

#### 6.1 Status bar
Agent surface, lit while not paused, padding 14 × 10, 12 px spacing. From left to right:
- **Agent dot** (30 px teal circle with glow) showing a mic icon, or a pause icon when paused.
- Eyebrow "Listening" or "Paused" (agent colour) above the elapsed time `H:MM:SS` (15 pt semibold, monospaced digits).
- **Waveform**: 14 teal bars, 3 px wide, 2 px apart, max 22 px high, scaled by the level. The level is the max of the mic and system RMS mapped from −60…0 dB to 0…1, and it is 0 while paused.
- A 1 px vertical divider, 28 px high.
- The meeting title (14 pt semibold), with the template title below it (11 pt muted).
- *(spacer)*
- **Status text** (11 pt): while thinking, a mini spinner and "Thinking" in the agent colour. While limited, failed or reconnecting, the status text in `warning` (max 2 lines). Otherwise the status text in muted. Status texts: "Not started", "Starting Claude…", "Listening", "Thinking…", "Reconnecting… <msg>", "Paused: Claude usage limit reached. <msg>", "Paused", "<error>", "Stopped".
- Tag "**N updates**" (agent colour). Tooltip: "Each update uses some of your Claude subscription (API equivalent so far: $X.XX)."
- Menu button **"What did I miss?"** (clock-arrow icon) with the items "Last 2 minutes", "Last 5 minutes", "Last 10 minutes". It is disabled while a catch-up runs. Tooltip: "A short summary of the last minutes, for when you were distracted".
- **Pause / Resume** (secondary pill, pause/play icon). Tooltip: "Pause the assistant; recording and transcript continue." Only the assistant is paused; the recording keeps running.
- **Stop** (destructive pill, stop icon). It stops the recording (see §7).

#### 6.2 "What did I miss?" catch-up card
The card is shown while a catch-up is loading, has failed, or has an answer. It is a card with the eyebrow "The last N minutes" (or "What did I miss?" while loading), the agent colour and a clock icon, plus a small ghost **×** button (tooltip "Close") that clears the answer and the state. Below are the StepLine ("Catching up on the last N minutes…", or an error) and the answer text (14 pt, selectable, wraps).

Behaviour: take the engine's transcript so far. If nothing falls in the window, fail with "Nothing was said yet.". Otherwise make a one-shot Claude call with the fast model (**haiku**, effort low, 60 s timeout) whatever the configured model is.

#### 6.3 Next-question card
Agent surface, 16 px padding. The header has the eyebrow "Next question" (sparkles icon, agent colour) and, on the right, a ghost small button **"Suggest now"** (⇧⌘Space; tooltip "Ask for a fresh suggestion now (⇧⌘Space)").
- With at least one question: the first question text (21 pt medium, selectable, highlighted when changed). Below it, a row with the related checklist item as an agent tag (if any), the "why" text (12 pt muted, 2 lines), then **Other** (secondary pill, tooltip "Not this one; suggest something else") and **Asked** (agent pill, check icon, tooltip "I asked this; it won't be suggested again").
- The alternatives (questions 2…n) are chips in a flow layout, each with a "turn-down-right" icon. Clicking one **promotes** it to first (tooltip "Show this one first").
- Empty state: "Claude is reading the client context…" while the status is Starting or Thinking, else "Waiting for the conversation…" (17 pt muted).

#### 6.4 Panels row (Summary, Open points, Checklist)
The three cards sit side by side, separated by **horizontal resize handles** (8 px wide hit area, a 3 × 36 capsule that turns primary 60 % on hover, resize cursor). The Checklist card exists only if the template produced checklist items.

- **Width shares** are stored per user (`summaryShare` default 0.40, `openShare` default 0.30). The checklist gets the rest.
  - Without the checklist, Summary and Open points are normalised to fill the row.
  - With the checklist, if summary + open exceeds 0.85, both are scaled down so the checklist keeps ≥ 15 %.
  - Dragging the handle after Summary trades width between Summary and Open points only, each keeping ≥ 15 %.
  - Dragging the handle after Open points changes only Open points, clamped so that Open points ≥ 15 % and the checklist ≥ 15 %.
  - The drag delta is measured from the start of the gesture. The available width is the row width minus the handles, with a minimum of 300.
- **Panel height**: the upper area's height minus the question card and the tables card, with a minimum of 160. If the window is too small for that, the whole upper area scrolls (scroll indicators appear only then) rather than squeezing the transcript.

**Summary** (PanelBox "Summary", icon text-align-left, count = number of paragraphs). One paragraph per topic, 13 pt, selectable, 10 px apart. If a paragraph has ": " within its first 40 characters, the part before it is the **topic** and is shown bold (with leading/trailing `*`, `#` and spaces stripped). When an update changes a paragraph (new or rewritten), the list scrolls to centre the highest changed paragraph, and that paragraph is highlighted.

**Open points** (PanelBox "Open points", dashed-circle icon, trailing "N open"). Open items are listed first. Each row has a status icon button and the text (13 pt):
- open: empty circle (`textSubtle`), normal text.
- answered: filled check circle (`success`), muted text with strikethrough.
- parked: pause circle, muted text.
- Clicking the icon toggles between answered and open (tooltip "Mark answered" / "Mark open"). A parked item becomes answered. The change goes to the engine, which tells Claude.

**Checklist** (PanelBox "Checklist", checklist icon, trailing "covered/total"). This is the template's checklist (called "playbook" in the code and data for historical reasons). A 4 px progress capsule (track `muted`, fill `agent`) shows covered/total. Below it, the items are grouped by section: a section header (11 pt semibold muted) appears whenever the section changes. Each item has an icon button and the text:
- missing: empty circle.
- partial: half-filled circle in `warning`.
- covered: filled check circle in `success`, with muted text.
- Clicking toggles covered ↔ missing. The item's tooltip is Claude's evidence quote.

#### 6.5 Tables card (template fill-in tables)
Shown when the live state has tables. It is a card with the eyebrow "Tables" (table icon, agent colour) and one summary tile per table, side by side. Each tile has a `page` 50 % background and radius 10, and shows:
- The table name (its section, else its id) and "filled/total" (monospaced).
- A teal progress bar.
- The last 3 filled rows as "<row label> → <answers joined by ' · '>" (11 pt muted, highlighted when changed).

Clicking a tile (tooltip "Open the table to see and correct all cells") opens the **Live table sheet**. The sheet is a modal of at least 760 × 560 on the `page` background. It contains the table header (name in 15 pt semibold, tag "N/M answered", a checkbox "Only answered rows", and the table's prompt text below), the scrollable **grid** (§9.3), and a **Close** button (primary, Return). A cell edit is sent to the engine, which keeps user-set values and tells Claude.

#### 6.6 Attendees row and transcript
These two sit together at the bottom. A **vertical resize handle** (36 × 3 capsule, 8 px tall hit area) in the gap above them sets the transcript height. Tooltip: "Drag to make the transcript taller or shorter". The height is stored per user (`transcriptHeight` default 240), clamped between 90 and max(90, window height − 360).

**Attendees row** (card, padding 14 × 8). Eyebrow "Attendees" with a two-people icon, then a horizontally scrolling chip row:
- Empty state: "Recognised from the conversation and from Aya contacts" (12 pt `textSubtle`).
- Attendees who are present come first. Then, after a small label "Mentioned", the people only mentioned (chips at 60 % opacity).
- Each **chip** (capsule, `card` fill, `border`, 12 pt) has a 20 px initial circle (teal with dark text if linked to an Aya contact, tooltip "Known contact in Aya"; primary 12 % otherwise, tooltip "Not in Aya yet"), the name (medium), the role (muted) if known, and "= <speaker label>" (`textSubtle`) if the speaker label differs from the name. The chip tooltip is Claude's notes or "Click for details from Aya". It is highlighted when changed.
- Clicking a chip opens the **Person popover** (§6.7) below it. Rename is offered only if the attendee is mapped to a live speaker label.

**Transcript panel** (surface with `card` at 70 % opacity and no shadow, padding 14 × 10). Eyebrow "Transcript" with a quote icon. It shows the **last 300 live lines**, auto-scrolling to the newest line with a 0.15 s ease-out. On first appearance it jumps to the bottom after the first layout. Each line is a first-baseline row:
- **Speaker button.** The display name is resolved like this: a name assigned to the raw label (`speakerNames[raw]`), matched to an attendee's full name if possible. Failing that, "my name" (or "Me") for the mic label. Failing that, unknown.
  - Colour: `primary` for the mic track, the agent text colour for the system/call track. Semibold.
  - **Generic** names ("Speaker N", "Them…", "?", "…", empty) get a dotted underline and the tooltip "Who is this? Click to name the speaker.". Other names get the tooltip "Click for details from Aya, or to rename".
  - Clicking opens the Person popover to the right, with rename.
  - If the speaker is not known yet, a grey "…" is shown with the tooltip "Speaker not recognised yet".
- **Text**, 12.5 pt, selectable, wraps. Final lines use `text`; provisional lines use `textSubtle`.

#### 6.7 Person popover (live and review)
The popover is 330 px wide with 14 px padding.
- **Header**: a 32 px initial circle (teal if Aya knows the person, else primary 12 %), the name (14 pt semibold) and a subtitle (11 pt muted) made of:
  - the source: "Contact at <client>", "Contact in Aya" or "Team member in Aya", or "Not found in Aya" for a non-generic unknown name;
  - plus " · speaks as “<label>”" when the transcript label differs;
  - or "Not named yet" when nothing applies.
- **Details grid** (12 pt): label (muted) and value (selectable, max 4 lines), showing the "important" fields of the Aya record.
- **Lookup order**: first the meeting's contacts (by contact id, else by name); then the team members by name; then, if a contact id is given and nothing matched, the contact is loaded from Aya asynchronously. Generic names never look anything up.
- **Name matching**: an exact match ignoring case and accents wins. Otherwise the match is the **only** entry whose name parts each start with a typed word ("Erika" → "Erika Musterfrau", but not if there are two Erikas).
- **Rename box** (only when renaming is allowed): a divider, the label "Who is this?", a **Name** text field (Return saves), up to 8 suggestion chips, and a small primary **Save** button (disabled when empty). Suggestions come from attendees, then the client's contacts, then team members, without generic names, without my name and without duplicates, excluding the current name. Typing filters them (contains, case- and accent-insensitive). Clicking a chip saves immediately. Saving trims the name, calls rename and closes the popover.
- **Live rename** sends `renameSpeaker(rawLabel, name)` to the engine. The engine updates `speakerNames` and Claude's context.

*Mac implementation:* `AssistLiveView`, `ResizeHandle`, `Highlight` in `AssistView.swift`; `LiveTablesCard` and `LiveTableSheet` in `AssistTablesView.swift`; `PersonPopover` in `AssistPeopleViews.swift`; `liveName`, `rawSpeaker`, `renameSpeaker`, `nameSuggestions`, `personCard`, `match`, `loadContact` in `AssistMeetings.swift`; `markAsked`, `dismiss`, `promote`, `suggestNow`, `setOpenPoint`, `setPlaybook`, `setTableCell`, `whatDidIMiss`, `togglePause` in `AssistController.swift`. Width and height shares are in `@AppStorage` keys `assistLive.summaryShare`, `assistLive.openShare`, `assistLive.transcriptHeight`.

*Windows notes:* Build the panels row as a `Grid` with star columns driven by the stored shares, and implement the 15 % clamps yourself (a stock `GridSplitter` will not enforce them). Popovers become `Flyout`s (Placement Bottom or Right). Live transcript: an `ItemsRepeater` capped at 300 items with scroll-to-end. Persist the shares in `ApplicationData.LocalSettings`.

#### 6.8 Floating window banner
While Assist is Running, the floating live transcript window shows a compact banner at its top (agent surface, radius 10, 8 px outer padding):
- A sparkles icon, then the first question (or the status text), 13 pt medium, max 3 lines.
- A footer row (11 pt muted): "N open point(s)", "updating…" (agent colour) while thinking, and small **Other** (ghost) and **Asked** (agent) pills.

*Mac:* `AssistBanner` in `AssistView.swift`, hosted by `LiveListening.swift`.

---

### 7. Stopping a meeting

**Stop** stops the app's recording. When the recording finishes, the "recording stopped" callback fires once with the session id (empty on failure):
1. Detach the engine and stop it. Take its final state and transcript lines.
2. If the id is empty or there is no context, return to **Prepare**.
3. Otherwise save the Assist record (context, final state, lines) with the session and **open the Review** for it.

The app always queues an Assist recording for final transcription, whatever the "auto-transcribe" preference says, because the recap needs it.

*Mac:* `AssistController.stop / recordingStopped`; `AppState.stopRecording` (the `assisted` flag forces `enqueue`).

---

### 8. Review screen

The Review opens after Stop. It also opens from the Meetings panel, from Import, and from the "Assist Recap" button in Scribe's session detail (that button is disabled while an Assist meeting is running). Opening it always switches the app to Assist mode.

**Open review(id):** cancel any pending wait and flush the unsaved recap draft of the previous review. Then load the Assist record. If there is none, do nothing. Then set:
- state and context from the record;
- the recap fields from the saved **draft**;
- the transcript from disk;
- the recap StepLine to "Saved in Aya." if the record was already uploaded;
- the tables from the record's completed tables, else the live tables;
- the target meeting to the uploaded record id, else the planned meeting id.

Then switch the phase to Review, load the day's Aya meetings and the target record, and start **wait-then-draft**.

**Wait-then-draft:** poll every 1 s while the session is queued, transcribing or recording. The recap StepLine meanwhile reads "Waiting for the final transcript… NN %". Then **carry speaker names** (once per record): map the live speaker names onto the final transcript's speakers and save. Then, if the draft is empty:
- If not uploaded yet, **draft the recap** and (if the tables were never completed and tables exist) **complete the tables**, both in parallel.
- If already uploaded: "Already saved in Aya. Draft again to change it."

**Layout:** padding 20 on the backdrop. The PageHeader is fixed at the top. Below it is a vertical scroll with the content max 920 px wide, centred, sections 12 px apart. Each section is a card with 18 px padding.

**Header:** eyebrow "Meeting review", the title (meeting title or "Meeting"), and the subtitle "<template title> · <date, abbreviated + time>". Buttons on the right:
- **Delete** (ghost, trash icon). Disabled while the session is busy (recording, queued or transcribing) or missing. Tooltip "Delete the recording, transcript and notes on this Mac". Opens the delete confirmation (§8.7).
- **Show Transcript** (secondary). Switches to Scribe with this session selected.
- **Done** (primary; deliberately **not** bound to Return, so pressing Return in the review never closes it). **Finish review**: flush the draft, reset everything (phase Prepare, selections, goal, review data) and reload the planned meetings.

Sections in order: Speakers, Meeting recap, Template tables (if any), During the meeting (if there is any summary, open point, attendee or checklist item), Update the CRM (only if the meeting has a client).

#### 8.1 Speakers
Title "Speakers" (16 pt semibold), subtitle "Names are used in the transcript and the recap. Click a speaker to name them."
- While no transcript is available, a status text depends on the session state:
  - transcribing: "The transcript is being made (NN %). Speakers can be named when it is ready."
  - queued: "Waiting to be transcribed. Speakers can be named when the transcript is ready."
  - failed: "Transcription failed. Try “Transcribe Again” in Scribe."
  - otherwise: "No transcript yet."
- Otherwise, one row per speaker, in order of first speaking:
  - an 8 px speaker-colour dot;
  - the name button (13 pt semibold, min 140 px; muted when generic) with a pencil icon. It is disabled for "Me". Clicking opens the Person popover (right) with rename;
  - the longest utterance as a quote, truncated at 110 characters with "…" (12 pt muted, 1 line);
  - right-aligned total talk time `H:MM:SS`.
- **Rename (final transcript):** set the speaker's label and save the transcript. If the speaker has a voice embedding and is not me, teach the speaker library this name (future auto-recognition). If the old name was generic, replace it with the new name in all recap field values.

#### 8.2 Meeting recap
Header: the title "Meeting recap" and the subtitle "→ <meetings list label> in <workspace label>". On the right:
- When uploaded: tag **"In Aya"** (success, check icon) and an **"Open in Aya"** link button (secondary pill, external-link icon). It opens `https://app.hey-aya.ai/workspace/<ws>/recordlist/<list>/record/<recordId>`; tooltip "Open this meeting in Aya".
- **Draft Recap** (agent pill) when there is no draft, or **Draft Again** (secondary pill) when there is. It is disabled while working. **Draft Again** first asks "Draft the recap again?" ("Claude writes a new recap. Your edits to the current one are replaced.", destructive button "Draft Again", Cancel).

**Target row:** the label "Save to", then an Aya select (max 420 px) with these options:
- "New meeting in Aya";
- the current target record (first), then that day's Aya meetings, de-duplicated, each as "HH:MM · Title", with " (planned)" when it is the meeting picked at preparation.

When the target is an existing meeting, a hint reads "Fields that already have content in Aya are only overwritten when you tick them." Changing the target re-applies the "existing" values to the draft fields: their current content in Aya, and which fields are ticked.

**Draft recap:** needs the meetings schema (else it fails with "Connect Aya in Settings › Assist first.") and the Claude binary (checked on demand). StepLine: "Claude is writing the recap…".

**Field list** (when the draft is not empty): fields separated by dividers. Each **FieldEditor** has:
- A checkbox with the field label (13 pt semibold). If the field already has content in Aya, a tag shows "Replaces the current text" (warning) when ticked, or "Has content in Aya" (muted) when not.
- If there is existing content: "Now" (10 pt semibold, subtle) and the old text (12 pt muted, 2 lines when ticked, 4 when not), indented 20 px.
- When ticked, an editor (indented 20 px) chosen by field kind:
  - boolean: a Yes/No segmented control (160 px; the value is normalised so "true", "yes", "ja" or "1" become true);
  - select: an Aya select with "—" plus the known options (max 260 px);
  - markdown: a multi-line text editor (90–260 px tall, 13 pt, `card` fill, `input` border, radius 8);
  - anything else: a text field that grows to 6 lines.
- If the field has a note (e.g. "Removed deadlines not said in the meeting: “…”"), it shows in warning colour with a calendar-exclamation icon.

**Autosave of the draft:** every change to the recap fields (edits, ticks, target changes, speaker renames) schedules a save **600 ms** after the last change into the session's Assist record (`recapDraft`; empty saves as none). Opening another review, finishing the review, re-opening or quitting the app flushes the pending save immediately. Draft writes only apply to the review they were scheduled for.

**Upload:** button **Create Meeting in Aya** (no target) or **Save to Meeting** (target set). Primary, up-arrow icon, right-aligned, disabled while working. StepLine "Saving in Aya…".
- Participants: my name plus the present attendees, matched to team members by name.
- With a target: re-read the record, build the values against it (fill links, date and type only where empty; add participants), update it.
- Without a target: create a new record, which then becomes the target.
- Template tables go into the configured field.
- Save `recapRecordID` and the upload time. The StepLine then shows "Saved in Aya: “<title>” (<list label>)." Saving again updates the same meeting.

#### 8.3 Template tables (review)
Title "Template tables". The subtitle is "Saved with the recap into “<field label>”. Long tables only with the rows that were answered." or, when no field is configured, "Not saved to Aya (Settings › Assist › Template tables).".
- Button **Complete from Transcript** (agent pill; shows **Complete Again** in secondary style once completed). Tooltip "Claude fills the tables from the final transcript. Cells you edited stay as they are.". StepLine "Claude is completing the tables…". The result is saved to the record immediately.
- Each table has the table header (§6.5) and the grid. Tables with **more than 30 rows** show only answered rows by default. The "Only answered rows" checkbox toggles this per table. Short tables show all rows by default.
- Cell edits are marked user-set and saved to the record immediately. The row id prefix before "." identifies the table.

#### 8.4 During the meeting
Title "During the meeting", subtitle "The live notes as they were when the meeting ended.". It contains:
- Attendee chips (as in live, without the "= label" part; "mentioned" chips at 60 % opacity). Clicking opens a Person popover **without** rename.
- Summary (eyebrow plus topic-bold paragraphs).
- Open points (eyebrow plus read-only status icons, the answered ones struck through).
- If there is a playbook, the line "Template: X of N points covered · missing: a; b; c…" (up to 5 missing items).

#### 8.5 Update the CRM
Title "Update the CRM", subtitle "Claude compares the client and contact records with what was said. Nothing is changed until you tick it and apply."
- Button **Propose Updates** (agent pill; **Propose Again** in secondary style once a proposal exists). **Propose Again** first asks "Propose the updates again?" ("Claude makes a new proposal. Your ticks and edits in the current one are replaced.", destructive "Propose Again", Cancel). If the meeting has no client: "This meeting has no client.". StepLine "Claude is reading the CRM records and the transcript…". When nothing is proposed: "No changes proposed.".
- **Client changes**: a header with a building icon and the client name, then one **ChangeRow** per field. A ChangeRow has a checkbox with the label, "Now" plus the old value (3 lines), the editor (same kinds as above) and the reason (agent colour, quote icon). Proposed changes start unticked.
- **Contacts**: one box per contact (radius 10, `page` 50 % fill, no shadow). It has a checkbox with the name and, for new contacts, the tag "New contact" (agent, plus icon). When ticked, the contact's changes are listed indented. Changes of existing contacts have their own checkboxes; the fields of a new contact have no per-field checkbox.
- **Apply Selected (N)** (primary, right-aligned) counts the ticked client fields plus the ticked contacts. It is disabled at 0 or while working. StepLine "Writing to Aya…". Client fields are written in one update. Ticked existing contacts are updated. New contacts are created and linked to the client through the configured contact→client field. Created contact ids are kept in the record.
- Result: "Saved in Aya: 2 client fields, 1 contact updated, 1 contact created." or "Nothing selected.". On a partial failure the error is shown plus " (already saved: …)". After a successful apply the proposal is cleared.

#### 8.6 Data persisted per session (Assist record)
context, final live state, live lines, recap draft, completed tables, uploaded meeting id plus time, CRM apply time, created contact ids, and the "speakers carried" flag. The full schema is in the data-model section.

#### 8.7 Delete meeting (Review header or Meetings context menu)
A confirmation dialog titled "Delete “<title>”?" with the message "The recording, the transcript and the notes on this Mac are deleted. What was saved in Aya stays there." and a destructive **Delete** button (plus the system Cancel). Deleting the meeting under review first finishes the review, then deletes the session folder.

*Mac implementation:* `AssistReviewView`, `FieldEditor`, `ChangeRow`, `editor(for:)` in `AssistView.swift`; `ReviewSpeakersSection`, `deleteMeetingDialog` in `AssistPeopleViews.swift`; `ReviewTablesSection` in `AssistTablesView.swift`. Controller: `openReview`, `loadTargets`, `setTarget`, `targetChoices`, `waitForTranscriptThenDraft`, `draftRecap`, `completeTables`, `setReviewCell`, `tablesFieldLabel`, `uploadRecap`, `proposeCRM`, `applyCRM`, `finishReview` (`AssistController.swift`); `saveDraftSoon`, `flushDraft`, `carrySpeakerNames`, `renameFinalSpeaker`, `deleteMeeting` (`AssistMeetings.swift`).

*Windows notes:* Use a `ContentDialog` for delete (PrimaryButton "Delete", styled as danger). Debounce the autosave with a timer reset on every `PropertyChanged`. Flush on navigation and on window close (the Mac does not flush explicitly on quit; Windows should).

---

### 9. Meetings panel and import

#### 9.1 Meetings panel (left, 270 px, card)
- Header "Meetings" (17 pt semibold) with an icon button **Import Recording** (download icon). Tooltip "Import a recording, link it to a meeting in Aya or create one, and get the recap".
- **New Meeting** row: a teal 26 px circle with a plus and the text "New Meeting". It is highlighted (`muted` fill) while in Prepare. Clicking it finishes the review and returns to Prepare.
- **Search** field (magnifier). Filters by title, case-insensitive contains.
- **List**: all sessions that have an Assist record, excluding the one currently recording. Order follows the global session order (newest first).
  - **Session row**: the title (semibold when selected), a status badge (done ✓ success, failed ⚠ warning, queued clock, transcribing waveform in primary, recording red dot, recorded dashed circle), a mode icon (call phone, in-person three people, hybrid), the date "12 Nov, 14:05" and " · H:MM:SS", and a progress bar while transcribing.
  - Selected row (the one under review): primary 10 % fill. Hover: `hover` 60 %.
  - Clicking a row opens its review (ignored if it is already open).
  - **Context menu**: "Show Transcript" (switches to Scribe with this session), "Show in Finder", a divider, then "Delete…" (destructive, disabled while busy; opens the confirmation in §8.7).
- Empty states: "Meetings with Assist appear here." or, with a filter, "Nothing matches.".
- **Busy rule**: a session is busy while recording, queued or transcribing. Delete is disabled for busy sessions everywhere.

#### 9.2 Import Recording sheet (560 px wide, 20 px padding)
Title "Import Recording" (18 pt semibold), intro text "The recording is transcribed on this Mac. Claude then drafts the recap for the meeting in Aya.". A two-column form with right-aligned labels:

| Label | Control |
|---|---|
| Recording | Small secondary button "Choose…" ("Change…" once chosen), followed by the file names joined by " + " (truncated in the middle). Hint: "One file, or two: first your microphone, then the call audio." The file dialog accepts audio and video, multi-select, keeps at most 2 files, and shows the message "Choose one recording, or two files: first your microphone, then the call audio." The first file's creation date becomes the Date. |
| Date | Date and time picker. Changing the **day** reloads that day's Aya meetings, and a selected meeting not on the new day is cleared. |
| Mode *(only with two files)* | Segmented "Call" / "Hybrid" (default Call) with the mode's explanation below. With one file the mode is In person. |
| Meeting in Aya | Aya select: "New meeting in Aya" plus that day's meetings as "HH:MM · Title". Picking a meeting sets the Date to the meeting's time. |
| Title *(only for a new meeting)* | Text field, placeholder "Optional" |
| Client / Project / Template *(only for a new meeting)* | Aya selects with "None" plus the cached lists |

Footer: the StepLine ("Importing…" or the error), **Cancel** (secondary, Esc) and **Import** (primary, Return; disabled without files or while working).

**Import behaviour:**
1. If any link is set, load the meeting context from Aya. Client, project and template are passed only when no Aya meeting is chosen. Without a connection this fails with a "not configured" error.
2. The mode is the chosen Call or Hybrid with two files, In person with one.
3. A typed title (only possible for a new meeting) is used as the meeting title. The session title is the context title, or the file name if the title is the generic "Meeting".
4. Copy the files in as a new session (file 1 = mic, file 2 = system) with the chosen date.
5. Save an Assist record with an empty live state, apart from the template checklist and tables.
6. Reload the sessions, queue the session for transcription and open its Review, which waits for the transcript and then drafts.

*Mac implementation:* `AssistMeetingsPanel`, `ImportMeetingSheet` in `AssistPeopleViews.swift`; `SessionRow`, `StatusBadge`, `modeIcon` in `ContentView.swift`; `importMeeting` in `AssistMeetings.swift`. Assist sessions are the ones with an `assist.json` (`AppState.assistSessionIDs`).

*Windows notes:* "Show in Finder" becomes "Show in Explorer" (`explorer.exe /select,"<path>"`). Use `FileOpenPicker` with audio/video file types and `PickMultipleFilesAsync`.

#### 9.3 Table grid (live sheet and review)
- A rounded box (radius 8, `card`, `border`). The header row shows the column names (11 pt semibold muted, max 2 lines). **Answer columns** are at most 180 px wide; the other columns flex. Rows are separated by hairlines (`border` at 50 %).
- Cells:
  - Answer columns are inline text fields (12 pt, placeholder "—", radius 4). A value from Claude (non-empty and not user-set) gets an `agent` 10 % fill and Claude's evidence quote as its tooltip. Otherwise the fill is `field`.
  - **"Open" keys** (e.g. a cell "Own proposal:") show the fixed prefix as muted text followed by a field for the rest.
  - Other columns are plain text (2 lines). The first key column is muted.
- A cell commits **once**, on Return or when focus leaves, and only if the trimmed value changed. While the field is focused, external updates do not overwrite what the user is typing.
- A row changed by the latest live update gets an `agent` 10 % row background.
- Empty state (filtered to answered rows with none answered): "Nothing answered yet."

*Mac:* `TemplateTableGrid`, `CellField`, `TableHeader` in `AssistTablesView.swift`.

---

### 10. Aya select (dropdown component)

A custom dropdown modelled on Aya's web select.
- **Trigger**: 32 px minimum height, padding 12, radius 8, `field` fill (`hover` 70 % on hover), 1 px `input` border (`primary` 70 % while open), shadow 4 % at y 1. It shows the selected title (13 pt, one line, tail-truncated) or the placeholder "Select…" in muted, and a 10 pt chevron-down in `textSubtle`. 50 % opacity when disabled.
- **List**: a separate borderless floating window, so it can extend beyond the parent:
  - Width is max(trigger width, 180). Height is min(rows × 28 + 8, 320), plus a 32 px search bar if the list is searchable.
  - It opens 4 px below the trigger, flips above if it would leave the screen, and is clamped horizontally within the screen with a 4 px margin.
  - `popover` fill, radius 8, `border`, system shadow.
- **Rows**: 28 px high, 8 px padding, 13 pt. The highlighted row gets a `hover` fill with radius 6, and a check mark marks the selected row.
- **Keyboard**:
  - The list takes focus on open; the selected row (or the first row) is highlighted and scrolled to the centre.
  - ↑/↓ move the highlight. Return picks. Space picks, unless a search query is being typed, where it adds a space. Esc closes.
- **Search**: lists with **more than 12** items get type-to-search. The search bar shows "Type to search" or the query plus a match count. Typing letters, digits, punctuation or symbols appends to the query; Backspace deletes. The filter is contains, case- and accent-insensitive. "No match" shows when nothing matches. The highlight resets to the first match.
- **Type-ahead** (short lists): a letter or digit jumps to the first item starting with it.
- Hovering a row highlights it; clicking picks it.
- **Closing**: the list closes on pick, on Esc, or when it loses focus (any click elsewhere). After a pick or Esc, focus returns to the parent window. A click on the trigger within 250 ms after a close is ignored, so clicking the trigger closes the list instead of reopening it. Only one list is open at a time.

*Mac implementation:* `AyaSelect`, `AyaSelectList`, `AyaPopup` (a child `NSPanel` that can become key and closes on `didResignKey`) in `AyaSelect.swift`.

*Windows notes:* A restyled `ComboBox` (with `IsEditable` for long lists) or a `Button` plus a `Popup`/`Flyout` with `LightDismiss` is enough. Keep the >12 search threshold and the keyboard behaviour.

---

### 11. Assist settings (Settings › Assist tab)

The tab uses a grouped form.

**Aya section**
- A 36 px logo tile, "Aya", and a status line: "Connected · lists, schemas and recaps come from your workspaces" (success) or "Not connected" (muted).
- **Organization id** field (placeholder "app.hey-aya.ai/org/<id>/…") and **API key** secure field (same placeholders as the Connect screen).
- **Connect** (or **Reconnect** when connected), primary, with the same enabling rule and flow as §4. **Remove Key** (secondary, only when a key is stored) deletes the key, clears the caches and all lists, and resets the state; the org id is kept.
- Connect StepLine.

**Lists section** (only when a key and org id exist)
- One row per slot. The label is the slot title plus an explanation caption:
  - Clients: "Companies you meet with (your CRM)."
  - Contacts: "People at those companies; used to recognise attendees."
  - Projects: "Projects a meeting can belong to."
  - Meeting templates: "Agenda and questions; they become the meeting's checklist."
  - Meetings: "Planned meetings, and where the recap is saved."
  - Team members: "Your team; internal attendees are linked as participants."
- The control is an Aya select (max 300 px) with "—" plus every non-system record type of all the organization's workspaces, labelled "<label> · <workspace label>" and sorted by label. Before the catalogue has loaded, the current value is shown as plain text instead.
- Changing a list stores the workspace and list and refreshes the schemas. For **Contacts**, the contact→client link resets and is auto-picked as the first relation whose key contains "firm". For any list other than Contacts, the lists are also reloaded.
- **Refresh Fields** button re-reads the schemas ("Reading the lists' fields…", then "Fields updated."). **Show All Lists** appears only when the catalogue is empty and loads it.
- Footer: "A list is used in the workspace where it is defined. Field details come from the workspace's API documentation."

**Links section**: Aya selects (max 240 px) with "None" plus the slot's relation fields.
- Contact → client.
- Meeting → client. Hint when None: "Without a link, earlier meetings are found by the client's name."
- Meeting → project.
- Meeting → template.
- Meeting → team members. Hint when None: "Team members who attended are added here, matched by name."
- **Template tables → field**: "Automatic (<resolved field>)", the meeting list's writable Markdown fields, or "Don't save tables" (stored as "-"). Caption: "Filled tables from a template are saved under a heading with the template's name. Automatic uses a field named like Protokoll, Leitfaden or Notes, else the summary."

**Claude section**
- A 30 px agent dot with a sparkles icon, "Claude", and "Runs through Claude Code with your subscription".
- **Claude Code**: the detected binary path (monospaced, or "not found") with the check StepLine below it.
- **Path (optional)** text field (placeholder "found automatically") overrides detection.
- **Check** button: locate the binary (the preferred path first, then `~/.local/bin/claude`, `~/.claude/local/claude`, `/opt/homebrew/bin/claude`, `/usr/local/bin/claude`, then each PATH entry) and run `claude auth status` (JSON, 20 s timeout). The results are:
  - "Claude Code logged in (<plan> plan)";
  - the "not installed" error;
  - the "not logged in" error.
- **Model**: Aya select with "Opus (best)", "Sonnet (lighter)", "Haiku (lightest)" (ids opus, sonnet, haiku; default opus).
- **Update frequency**: Frequent / Normal / Economical. The explanation text, followed by " Assist uses your Claude subscription through Claude Code, not the API: each update counts against your plan's usage limits.":
  - Frequent: "About every 15–45 s. Fastest reactions, uses the most."
  - Normal: "About every 20–60 s."
  - Economical: "About every 40–120 s. Uses the least."
  - The mapping (min words / min s / max s) is 40/15/45, 60/20/60 and 100/40/120. The frequency shown is derived from min words: ≤ 40 is Frequent, ≥ 100 is Economical, anything else is Normal.
- Privacy caption: "In Assist mode the transcript text and the client context from Aya are sent to Claude (Anthropic). Audio never leaves this Mac. Scribe mode stays fully local."
- On appear: check Claude if it is unknown, and load the catalogue if a key and org exist and it is empty.

Settings and config **save automatically** on every change.

*Mac implementation:* `AssistSettingsView.swift`; `AssistSettings` (Core, `AssistEngine.swift`); `AyaSlot`, `AyaConfig` (Core); `AssistController.connect / disconnect / loadCatalog / setList / refreshSchemas / checkClaude`.

*Windows notes:* Detect `claude.exe`/`claude.cmd` on PATH, in `%APPDATA%\npm`, and in `%USERPROFILE%\.local\bin`. Read the login state with `claude auth status`, the same as on the Mac.

---

### 12. Theme tokens

Colours are given as light / dark. "rgba(24,24,27,a)" is zinc-900 at alpha a.

| Token | Light | Dark | Use |
|---|---|---|---|
| page | #F7F8F9 | #0A0A0A | window/page background |
| card | #FFFFFF | #0E0E0E | cards, chips, secondary buttons |
| popover | #FFFFFF | #111111 | dropdown list |
| rail | #FFFFFF | #0F0F0F | left rail |
| text | rgba(24,24,27,1) | rgba(255,255,255,.96) | body text |
| textMuted | rgba(24,24,27,.76) | rgba(255,255,255,.68) | secondary text |
| textSubtle | rgba(24,24,27,.55) | rgba(255,255,255,.45) | hints, placeholders |
| muted | #F1F1F3 | #27272A | progress tracks, selected "new" rows |
| hover | #E9E9ED | #27272A | hover fills |
| field | rgba(24,24,27,.04) | rgba(255,255,255,.04) | select trigger, table cells |
| border | rgba(24,24,27,.13) | rgba(255,255,255,.10) | hairlines |
| input | rgba(24,24,27,.22) | rgba(255,255,255,.14) | text field outlines |
| primary | #225FE7 | #2563EB | selection, primary buttons, my speaker colour |
| destructive / destructiveFg | #CE0D18 / #FFFFFF | #F87171 / #0A0A0A | Stop button |
| success | #1E8A4C | #4CC97A | done, covered, "In Aya" |
| warning | #9A6A12 | #E5B04A | errors in StepLine, partial items, "Replaces…" |
| brandStart → brandEnd | #0F7DFF → #0EAFFF | same | logo gradient (top-left → bottom-right), selected client avatar |
| agent | #35D2C1 | same | Assist accent (teal): agent buttons, glow, progress fills, highlights |
| agentTextMid | 55 % agent mixed with #18181B | 55 % agent mixed with #F5F5F5 | agent-coloured text and eyebrows |
| agentTextBright | 25 % mix, same bases | | (reserved) |
| agentForeground | black at 78 % | same | text on teal |
| agentGlow | agent at 65 % | | glows |

**Radii:** tag 4, small control 6, control 8, container 10, overlay 14, surface 16. **Control height** 32.

**Surfaces:**
- Card: `card` fill, 1 px `border`, radius 16, shadow black 5 % blur 1.5 y 1.
- Agent surface: `card` fill. While active it has a 1 px agent 60 % border and a teal glow (agentGlow × 0.35, blur 14); while inactive it looks like a card.

**Buttons** (radius 8, or 999 for pills; heights small 26 / regular 32 / large 40; horizontal padding 9 / 12 (14 for pills) / 18; 12 pt for small, else 13 pt; weight medium for secondary/ghost, semibold otherwise; 45 % opacity when disabled, 80 % while pressed):

| Kind | Fill | Hover fill | Text | Extra |
|---|---|---|---|---|
| primary | primary | 88 % | white | |
| secondary | card | hover | text | `border` outline |
| ghost | clear | hover | text | |
| destructive | destructive | 88 % | destructiveFg | |
| agent | agent | 85 % | agentForeground | teal glow (55 %, blur 7) |

**Icon button:** 32 px square, radius 8. **Chip:** capsule, 13 pt, padding 12 × 6, `card`/`hover` fill, `border` outline (selected: agent 14 % fill, agent 60 % outline). **Text field:** min height 32, padding 10 × 7, `card` fill, `input` outline, optional 12 pt leading icon in `textSubtle`. **Segmented control:** bordered group with 1 px dividers; the selected segment has primary 10 % fill, primary semibold text. **Backdrop:** two radial gradients (brandEnd 10 % and agent 9 %). The global tint is `primary`.

*Mac implementation:* `AyaTheme.swift` (enum `Aya`, `ayaSurface`, `ayaAgentSurface`, `AyaButtonStyle`, `AyaIconButtonStyle`, `AyaChipStyle`, `AyaFieldStyle`, `AyaSegmented`, `AyaEyebrow`, `AyaTag`, `AyaWaveform`, `AyaAgentDot`, `AyaLogoTile` with the `AyaMark` path, `AyaBackdrop`). Dynamic colours switch on the system appearance.

*Windows notes:* Put the tokens into `ThemeDictionaries` (Light/Dark) as `SolidColorBrush` resources. Shadows via `ThemeShadow` or the `DropShadow` composition API. Export the logo mark path as XAML `PathGeometry` from the same SVG.

---

### 13. Dev tool: snapshot renderer
Launching with `-assistSnapshot <dir> [-assistSnapshotRecord <assist.json>]` (optionally the env var `ASSIST_SNAPSHOT_UPCOMING=<json>` to inject planned meetings) renders the screens offscreen to PNGs and quits:
- Scribe and session views;
- Prepare (1280 × 800);
- Live (1280 × 860), from a saved record with fake live lines;
- the floating banner;
- Review (normal and long);
- the person popover;
- the Import sheet;
- the review tables;
- Settings.

It is used for layout checks without a display. The Windows port only needs it as an optional test aid. *Mac:* `AssistSnapshots` at the end of `AssistView.swift`, triggered from `AppState`.

---

### Notes for the port

- The code and data files still say "playbook" for the template checklist (`playbook`, `playbookID`, `setPlaybook`). Keep these names in data files for compatibility; the UI says "Checklist".
- Draft autosave must also flush when the app quits (the Mac does this on `willTerminate`).
