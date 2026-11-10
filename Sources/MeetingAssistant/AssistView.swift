import MeetingAssistantCore
import SwiftUI

/// Assist mode: prepare a meeting, run the live assistant, review and upload afterwards.
struct AssistHomeView: View {
    @EnvironmentObject var state: AppState

    var body: some View {
        AssistLayout(assist: state.assist)
    }
}

/// Past meetings on the left, except during a meeting and before Aya is connected.
private struct AssistLayout: View {
    @ObservedObject var assist: AssistController
    var body: some View {
        if assist.phase == .running || !assist.isConnected {
            AssistRouter(assist: assist)
        } else {
            HStack(spacing: 8) {
                AssistMeetingsPanel(assist: assist).frame(width: 270)
                AssistRouter(assist: assist)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .clipShape(RoundedRectangle(cornerRadius: Aya.radiusSurface, style: .continuous))
            }
        }
    }
}

private struct AssistRouter: View {
    @EnvironmentObject var state: AppState
    @ObservedObject var assist: AssistController

    var body: some View {
        Group {
            switch assist.phase {
            case .prepare:
                if assist.isConnected { AssistPrepView(assist: assist) } else { AssistConnectView(assist: assist) }
            case .running:
                AssistLiveView(assist: assist)
            case .review(let id):
                AssistReviewView(assist: assist, sessionID: id)
            }
        }
        .task { if assist.claudeBinary == nil { await assist.checkClaude() } }
    }
}

// MARK: - Shared bits

struct StepLine: View {
    let step: StepState
    var body: some View {
        switch step {
        case .idle: EmptyView()
        case .working(let m): HStack(spacing: 6) { ProgressView().controlSize(.small); Text(m) }.font(.system(size: 12)).foregroundStyle(Aya.textMuted)
        case .done(let m): if let m { Label(m, systemImage: "checkmark.circle.fill").labelStyle(AyaLabelStyle()).font(.system(size: 12)).foregroundStyle(Aya.success) }
        case .failed(let m): Label(m, systemImage: "exclamationmark.triangle.fill").labelStyle(AyaLabelStyle()).font(.system(size: 12)).foregroundStyle(Aya.warning).textSelection(.enabled)
        }
    }
}

/// An Aya card with an uppercase eyebrow title.
private struct PanelBox<Content: View>: View {
    let title: String
    let icon: String
    var trailing: AnyView? = nil
    @ViewBuilder let content: Content
    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                AyaEyebrow(text: title, icon: icon)
                Spacer()
                if let trailing { trailing }
            }
            content
        }
        .padding(14)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .ayaSurface()
    }
}

/// Count shown at the right of a panel title.
private func panelCount(_ s: String) -> AnyView {
    AnyView(Text(s).font(.system(size: 11, weight: .medium).monospacedDigit()).foregroundStyle(Aya.textMuted))
}

/// Briefly tints items that the latest update changed, in the agent's teal.
struct Highlight: ViewModifier {
    let on: Bool
    func body(content: Content) -> some View {
        content
            .padding(.horizontal, 5).padding(.vertical, 2)
            .background(on ? Aya.agent.opacity(0.16) : .clear, in: RoundedRectangle(cornerRadius: Aya.radiusControlSm, style: .continuous))
            .animation(.easeOut(duration: 0.6), value: on)
    }
}

/// Screen title as on Aya's pages: eyebrow, title, subtitle.
private struct PageHeader<Trailing: View>: View {
    let eyebrow: String
    let title: String
    var subtitle: String? = nil
    @ViewBuilder var trailing: Trailing
    var body: some View {
        HStack(alignment: .bottom) {
            VStack(alignment: .leading, spacing: 4) {
                AyaEyebrow(text: eyebrow, color: Aya.agentTextMid)
                Text(title).font(.system(size: 24, weight: .semibold)).lineLimit(1)
                if let subtitle { Text(subtitle).font(.system(size: 13)).foregroundStyle(Aya.textMuted).lineLimit(1) }
            }
            Spacer()
            trailing
        }
    }
}

// MARK: - Not connected

private struct AssistConnectView: View {
    @ObservedObject var assist: AssistController
    @State private var org = ""
    @State private var key = ""

    var body: some View {
        ZStack {
            AyaBackdrop()
            VStack(alignment: .leading, spacing: 18) {
                HStack(spacing: 12) {
                    AyaLogoTile(size: 44)
                    VStack(alignment: .leading, spacing: 2) {
                        AyaEyebrow(text: "Assist", color: Aya.agentTextMid)
                        Text("Connect Aya").font(.system(size: 22, weight: .semibold))
                    }
                }
                Text("Pick a planned meeting, or a template, client and project from Aya, start the meeting, and Claude suggests the next question, keeps a running summary, and tracks attendees, open points and the template's checklist while you talk. Afterwards it saves the recap to the meeting in Aya.")
                    .font(.system(size: 13)).foregroundStyle(Aya.textMuted).fixedSize(horizontal: false, vertical: true)
                VStack(alignment: .leading, spacing: 6) {
                    Text("Organization id").font(.system(size: 12, weight: .medium))
                    TextField("", text: $org, prompt: Text("from app.hey-aya.ai/org/<id>/…")).textFieldStyle(AyaFieldStyle())
                }
                VStack(alignment: .leading, spacing: 6) {
                    Text("API key").font(.system(size: 12, weight: .medium))
                    SecureField("", text: $key, prompt: Text(assist.hasAPIKey ? "stored in the Keychain" : "aya_key_…")).textFieldStyle(AyaFieldStyle(icon: "key"))
                }
                HStack(spacing: 10) {
                    Button("Connect") { Task { await assist.connect(orgId: org, apiKey: key); key = "" } }
                        .buttonStyle(AyaButtonStyle(kind: .primary))
                        .keyboardShortcut(.defaultAction)
                        .disabled(org.trimmingCharacters(in: .whitespaces).isEmpty || (key.isEmpty && !assist.hasAPIKey) || assist.connectState.isWorking)
                    StepLine(step: assist.connectState)
                }
                Text("The lists (clients, contacts, projects, templates, meetings) can be changed later in Settings › Assist.")
                    .font(.system(size: 11)).foregroundStyle(Aya.textSubtle)
            }
            .padding(28)
            .frame(width: 500)
            .ayaSurface(radius: Aya.radiusOverlay)
            .shadow(color: .black.opacity(0.08), radius: 24, y: 12)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .onAppear { org = assist.config.orgId }
    }
}

// MARK: - Prepare

private struct AssistPrepView: View {
    @EnvironmentObject var state: AppState
    @ObservedObject var assist: AssistController
    @State private var clientFilter = ""
    @State private var remote: [AyaRecord] = []
    @State private var searchTask: Task<Void, Never>?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            PageHeader(eyebrow: "Assist", title: "Prepare meeting",
                       subtitle: "Pick a meeting planned in Aya, or choose a template, client and project. Claude reads them before the meeting starts.") {
                HStack(spacing: 8) {
                    if let d = assist.listsFetchedAt {
                        Text("Aya lists updated \(d.formatted(.relative(presentation: .named)))").font(.system(size: 11)).foregroundStyle(Aya.textSubtle)
                    }
                    Button { Task { await assist.refreshLists() } } label: { Label("Refresh", systemImage: "arrow.clockwise") }
                        .buttonStyle(AyaButtonStyle(kind: .secondary))
                        .help("Reload planned meetings, templates, clients and projects from Aya")
                        .disabled(assist.listState.isWorking)
                }
            }
            plannedStrip
            HStack(alignment: .top, spacing: 8) {
                clientColumn.frame(minWidth: 260, idealWidth: 300, maxWidth: 340)
                VStack(alignment: .leading, spacing: 8) {
                    HStack(alignment: .top, spacing: 8) {
                        templateCard
                        projectCard
                    }
                    .fixedSize(horizontal: false, vertical: true)
                    meetingSettings
                    previewBox
                }
                .frame(minWidth: 380)
            }
            footer
        }
        .padding(20)
        .background(AyaBackdrop())
        .task { await assist.refreshUpcoming() }
    }

    // MARK: Planned meetings

    private var plannedStrip: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                AyaEyebrow(text: "Planned in Aya", icon: "calendar")
                Text("today and the next 14 days").font(.system(size: 11)).foregroundStyle(Aya.textSubtle)
                Spacer()
                if assist.meetingID != nil {
                    Text("The recap is saved to this meeting afterwards.").font(.system(size: 11)).foregroundStyle(Aya.textMuted)
                }
            }
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 8) {
                    plannedCard(nil)
                    ForEach(assist.upcoming, id: \.id) { m in plannedCard(m) }
                }
                .padding(1)
            }
        }
        .padding(14)
        .ayaSurface()
    }

    private func plannedCard(_ m: AyaRecord?) -> some View {
        let on = assist.meetingID == m?.id
        let cfg = assist.config
        let date = m?.date(cfg.meetingDateField)
        let links = m.map { r in
            [cfg.meetingClientField, cfg.meetingProjectField, cfg.meetingTemplateField].compactMap { $0.flatMap { r.linkedTitle($0) } }
        } ?? []
        let hasNotes = m.map { r in ["summary__value", "decisions__value", "next_steps__value"].contains { !(r[$0]?.isEmpty ?? true) } } ?? false
        return Button { assist.meetingID = m?.id } label: {
            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 6) {
                    Image(systemName: m == nil ? "plus" : "calendar").font(.system(size: 10, weight: .semibold))
                    Text(m == nil ? "New meeting" : date.map(plannedDate) ?? "No date")
                        .font(.system(size: 11, weight: .semibold))
                    Spacer(minLength: 0)
                    if hasNotes { AyaTag(text: "Notes", color: Aya.textMuted) }
                }
                .foregroundStyle(on ? Aya.primary : Aya.textMuted)
                Text(m?.title ?? "Choose template, client and project yourself")
                    .font(.system(size: 13, weight: .medium)).foregroundStyle(m == nil ? Aya.textMuted : Aya.text)
                    .lineLimit(2).multilineTextAlignment(.leading)
                if !links.isEmpty {
                    Text(links.joined(separator: " · ")).font(.system(size: 11)).foregroundStyle(Aya.textMuted).lineLimit(1)
                }
                Spacer(minLength: 0)
            }
            .padding(10)
            .frame(width: 210, height: 84, alignment: .topLeading)
            .background(on ? Aya.primary.opacity(0.10) : Aya.card, in: RoundedRectangle(cornerRadius: Aya.radiusContainer, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: Aya.radiusContainer, style: .continuous)
                .strokeBorder(on ? Aya.primary.opacity(0.6) : Aya.border, lineWidth: 1))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(m.map { "\($0.title)\(hasNotes ? " · already has notes in Aya" : "")" } ?? "A new meeting is created in Aya afterwards")
    }

    private func plannedDate(_ d: Date) -> String {
        let cal = Calendar.current
        let time = d.formatted(date: .omitted, time: .shortened)
        if cal.isDateInToday(d) { return "Today \(time)" }
        if cal.isDateInTomorrow(d) { return "Tomorrow \(time)" }
        return d.formatted(.dateTime.weekday(.abbreviated).day().month(.abbreviated)) + " " + time
    }

    // MARK: Client

    private var filteredClients: [AyaRecord] {
        let f = clientFilter.trimmingCharacters(in: .whitespaces)
        guard !f.isEmpty else { return assist.clients }
        let local = assist.clients.filter { $0.title.localizedCaseInsensitiveContains(f) }
        let extra = remote.filter { r in !local.contains { $0.id == r.id } }
        return local + extra
    }

    private var clientColumn: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                AyaEyebrow(text: "Client", icon: "building.2")
                Spacer()
                Text("\(assist.clients.count)").font(.system(size: 11, weight: .medium)).foregroundStyle(Aya.textMuted)
            }
            TextField("Search", text: $clientFilter)
                .textFieldStyle(AyaFieldStyle(icon: "magnifyingglass"))
                .onChange(of: clientFilter) { _, text in
                    searchTask?.cancel()
                    remote = []
                    // Ask Aya only when the cached list has no match.
                    guard text.count >= 2, !assist.clients.contains(where: { $0.title.localizedCaseInsensitiveContains(text) }) else { return }
                    searchTask = Task {
                        try? await Task.sleep(nanoseconds: 250_000_000)
                        guard !Task.isCancelled else { return }
                        remote = await assist.searchClients(text)
                    }
                }
            ScrollView {
                LazyVStack(spacing: 2) {
                    if clientFilter.isEmpty { clientRow(nil) }
                    ForEach(filteredClients, id: \.id) { c in clientRow(c) }
                }
            }
            StepLine(step: assist.listState)
        }
        .padding(14)
        .frame(maxHeight: .infinity, alignment: .top)
        .ayaSurface()
    }

    private func clientRow(_ c: AyaRecord?) -> some View {
        let on = assist.clientID == c?.id
        let sub = c.map { c in
            [c["branche__value"]?.description, c["domain__value"]?.description, c["region__value"]?.description]
                .compactMap { $0 }.filter { !$0.isEmpty }.joined(separator: " · ")
        } ?? "Internal meeting, or project only"
        return Button { assist.clientID = c?.id } label: {
            HStack(spacing: 10) {
                Group {
                    if let c { Text(ayaInitial(c.title)) } else { Image(systemName: "minus") }
                }
                .font(.system(size: 12, weight: .semibold))
                .foregroundStyle(on ? .white : Aya.primary)
                .frame(width: 28, height: 28)
                .background(on ? AnyShapeStyle(Aya.brand) : AnyShapeStyle(Aya.primary.opacity(0.10)),
                            in: RoundedRectangle(cornerRadius: Aya.radiusControl, style: .continuous))
                VStack(alignment: .leading, spacing: 1) {
                    Text(c?.title ?? "No client").font(.system(size: 13, weight: on ? .semibold : .medium)).lineLimit(1)
                    if !sub.isEmpty { Text(sub).font(.system(size: 11)).foregroundStyle(Aya.textMuted).lineLimit(1) }
                }
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 8).padding(.vertical, 6)
            .background(on ? Aya.primary.opacity(0.10) : .clear, in: RoundedRectangle(cornerRadius: Aya.radiusControl, style: .continuous))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }

    // MARK: Template, project, meeting

    private var templateCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            AyaEyebrow(text: "Template", icon: "checklist")
            AyaSelect(selection: $assist.templateID,
                      options: [(String?.none, "No template")] + assist.templates.map { (String?.some($0.id), $0.title) })
            if let tid = assist.templateID, let t = assist.templates.first(where: { $0.id == tid }) {
                HStack(spacing: 6) {
                    if let type = t[assist.config.templateTypeField]?.string, !type.isEmpty { AyaTag(text: type, color: Aya.primary) }
                    let n = PlaybookParser.items(t[assist.config.templateAgendaField]?.string ?? "").count
                    if n > 0 { AyaTag(text: "\(n) checklist items", color: Aya.agentTextMid, icon: "checklist") }
                }
                if let d = t[assist.config.templateGoalField]?.string, !d.isEmpty {
                    Text(d).font(.system(size: 12)).foregroundStyle(Aya.textMuted).lineLimit(3)
                }
            }
            Spacer(minLength: 0)
        }
        .padding(14)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .ayaSurface()
    }

    private var projectCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            AyaEyebrow(text: "Project", icon: "folder")
            if assist.config.list(.projects) == nil {
                Text("No project list chosen in Settings › Assist.").font(.system(size: 12)).foregroundStyle(Aya.textMuted)
            } else {
                AyaSelect(selection: $assist.projectID,
                          options: [(String?.none, "No project")] + assist.projects.map { (String?.some($0.id), $0.title) })
                if let pid = assist.projectID, let p = assist.projects.first(where: { $0.id == pid }),
                   let st = p["status__value"]?.string, !st.isEmpty {
                    AyaTag(text: st, color: Aya.textMuted)
                }
            }
            Spacer(minLength: 0)
        }
        .padding(14)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .ayaSurface()
    }

    private var meetingSettings: some View {
        HStack(alignment: .center, spacing: 10) {
            AyaEyebrow(text: "Meeting", icon: "person.2")
            AyaSegmented(selection: $assist.mode, options: MeetingMode.allCases.map { ($0, $0.title, nil) })
                .frame(maxWidth: 300)
            TextField("", text: $assist.goal, prompt: Text("Goal for this meeting (optional)"), axis: .vertical)
                .lineLimit(1...3).textFieldStyle(AyaFieldStyle(icon: "target"))
        }
        .padding(14)
        .frame(maxWidth: .infinity, alignment: .topLeading)
        .ayaSurface()
    }

    // MARK: Preview

    private var previewBox: some View {
        VStack(alignment: .leading, spacing: 10) {
            AyaEyebrow(text: "What Claude will know", icon: "sparkles", color: Aya.agentTextMid)
            if let p = assist.preview {
                ScrollView {
                    VStack(alignment: .leading, spacing: 12) {
                        if let m = p.meeting {
                            previewSection("Meeting") { Text(m.title).font(.system(size: 15, weight: .semibold)) }
                        }
                        if let c = p.client {
                            previewSection("Client") {
                                Text(c.title).font(.system(size: 15, weight: .semibold))
                                if let profile = c.values.first(where: { $0.key.contains("profil") })?.value.string, !profile.isEmpty {
                                    Text(profile).font(.system(size: 13)).foregroundStyle(Aya.text).lineLimit(5)
                                }
                            }
                        }
                        if let pr = p.project {
                            previewSection("Project") { Text(pr.title).font(.system(size: 14, weight: .medium)) }
                        }
                        if let t = p.template {
                            previewSection("Template") {
                                Text(t.title).font(.system(size: 14, weight: .medium))
                                let items = PlaybookParser.items(t[assist.config.templateAgendaField]?.string ?? "")
                                if !items.isEmpty {
                                    Text(items.prefix(6).map(\.text).joined(separator: " · ") + (items.count > 6 ? " …" : ""))
                                        .font(.system(size: 12)).foregroundStyle(Aya.textMuted).lineLimit(3)
                                }
                            }
                        }
                        if !p.contacts.isEmpty {
                            previewSection("Contacts") { FlowChips(items: p.contacts.map { c in (c.title, c["position__value"]?.string) }) }
                        }
                        previewSection("Earlier meetings") {
                            if p.recaps.isEmpty { Text("None found").font(.system(size: 13)).foregroundStyle(Aya.textSubtle) }
                            ForEach(p.recaps, id: \.id) { r in
                                let d = String((r[assist.config.meetingDateField]?.string ?? r.values["createdAt"]?.string ?? "").prefix(10))
                                HStack(spacing: 8) {
                                    Text(d).font(.system(size: 11).monospacedDigit()).foregroundStyle(Aya.textSubtle)
                                    Text(r.title).font(.system(size: 13)).lineLimit(1)
                                }
                            }
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
            } else if !assist.previewState.isWorking {
                Text("Nothing chosen yet: Claude then works from the conversation alone. Pick a planned meeting, a template, a client or a project to give it context.")
                    .font(.system(size: 13)).foregroundStyle(Aya.textMuted).fixedSize(horizontal: false, vertical: true)
            }
            StepLine(step: assist.previewState)
        }
        .padding(14)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .ayaAgentSurface(active: assist.preview != nil)
    }

    private func previewSection<C: View>(_ title: String, @ViewBuilder _ content: () -> C) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title).font(.system(size: 12, weight: .semibold)).foregroundStyle(Aya.textMuted)
            content()
        }
    }

    private var footer: some View {
        HStack(spacing: 12) {
            claudeLine
            Spacer()
            StepLine(step: assist.startState)
            Button {
                Task { await assist.start() }
            } label: {
                Label("Start Assist", systemImage: "mic.fill")
            }
            .buttonStyle(AyaButtonStyle(kind: .agent, size: .large, pill: true))
            .disabled(!assist.canStart)
            .help(state.isRecording ? "A recording is already running." : "Starts the recording with live transcript and the assistant.")
        }
    }

    @ViewBuilder private var claudeLine: some View {
        switch assist.claudeState {
        case .failed(let m):
            HStack {
                Label(m, systemImage: "exclamationmark.triangle.fill").labelStyle(AyaLabelStyle()).foregroundStyle(Aya.warning).font(.system(size: 12))
                Button("Check Again") { Task { await assist.checkClaude() } }.buttonStyle(AyaButtonStyle(kind: .secondary, size: .small))
            }
        case .working(let m): StepLine(step: .working(m))
        default:
            Label("Claude \(assist.settings.model.capitalized) via your Claude subscription · transcript text is sent to Claude, audio stays on this Mac",
                  systemImage: "lock").labelStyle(AyaLabelStyle()).font(.system(size: 11)).foregroundStyle(Aya.textSubtle)
        }
    }
}

/// Wrapping row of small pill chips (name and optional detail).
private struct FlowChips: View {
    let items: [(String, String?)]
    var body: some View {
        FlowLayout(spacing: 6) {
            ForEach(Array(items.enumerated()), id: \.offset) { _, it in
                HStack(spacing: 4) {
                    Text(it.0).fontWeight(.medium)
                    if let d = it.1, !d.isEmpty { Text(d).foregroundStyle(Aya.textMuted) }
                }
                .font(.system(size: 12))
                .padding(.horizontal, 10).padding(.vertical, 4)
                .background(Aya.card, in: Capsule())
                .overlay(Capsule().strokeBorder(Aya.border, lineWidth: 1))
            }
        }
    }
}

/// Minimal flow layout: places views left to right and wraps.
struct FlowLayout: Layout {
    var spacing: CGFloat = 6
    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let w = proposal.width ?? .infinity
        var x: CGFloat = 0, y: CGFloat = 0, row: CGFloat = 0, maxX: CGFloat = 0
        for s in subviews {
            let sz = s.sizeThatFits(.unspecified)
            if x > 0 && x + sz.width > w { x = 0; y += row + spacing; row = 0 }
            x += sz.width + spacing; row = max(row, sz.height); maxX = max(maxX, x - spacing)
        }
        return CGSize(width: min(maxX, w), height: y + row)
    }
    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        var x = bounds.minX, y = bounds.minY, row: CGFloat = 0
        for s in subviews {
            let sz = s.sizeThatFits(.unspecified)
            if x > bounds.minX && x + sz.width > bounds.maxX { x = bounds.minX; y += row + spacing; row = 0 }
            s.place(at: CGPoint(x: x, y: y), proposal: ProposedViewSize(sz))
            x += sz.width + spacing; row = max(row, sz.height)
        }
    }
}

// MARK: - Live

private struct AssistLiveView: View {
    @EnvironmentObject var state: AppState
    @ObservedObject var assist: AssistController
    /// Width shares of Summary and Open points (Playbook gets the rest).
    @AppStorage("assistLive.summaryShare") private var summaryShare = 0.4
    @AppStorage("assistLive.openShare") private var openShare = 0.3
    /// Height of the transcript at the bottom; the panels take what is left above it.
    @AppStorage("assistLive.transcriptHeight") private var transcriptHeight = 240.0
    @State private var dragStart: Double?
    @State private var questionHeight: CGFloat = 0
    @State private var tablesHeight: CGFloat = 0
    @State private var speakerPopover: String?
    @State private var attendeePopover: String?

    var body: some View {
        GeometryReader { g in
            let maxTranscript = max(90, g.size.height - 360)
            VStack(spacing: 8) {
                statusBar
                if assist.catchUp != nil || assist.catchUpState != .idle { catchUpCard }
                upperArea
                // Attendees sit directly on the transcript; the handle in the gap above resizes both.
                VStack(spacing: 8) {
                    attendeesRow
                    transcriptPanel.frame(height: min(transcriptHeight, maxTranscript))
                }
                .overlay(alignment: .top) {
                    ResizeHandle(axis: .vertical) { delta, ended in
                        let start = dragStart ?? min(transcriptHeight, maxTranscript)
                        dragStart = ended ? nil : start
                        transcriptHeight = min(max(90, start - delta), maxTranscript)
                    }
                    .offset(y: -ResizeHandle.thickness)
                    .help("Drag to make the transcript taller or shorter")
                }
            }
        }
    }

    /// Question, panels and tables. The panels fill the height that is left; when the window is too
    /// small for that, this part scrolls instead of pushing the transcript away.
    private var upperArea: some View {
        GeometryReader { up in
            let hasTables = !(assist.state.tables ?? []).isEmpty
            let rest = up.size.height - questionHeight - 8 - (hasTables ? tablesHeight + 8 : 0)
            ScrollView {
                VStack(spacing: 8) {
                    questionCard
                        .onGeometryChange(for: CGFloat.self, of: { $0.size.height }) { questionHeight = $0 }
                    panelsRow.frame(height: max(160, rest))
                    if hasTables {
                        LiveTablesCard(assist: assist)
                            .onGeometryChange(for: CGFloat.self, of: { $0.size.height }) { tablesHeight = $0 }
                    }
                }
            }
            .scrollIndicators(rest < 160 ? .automatic : .never)
        }
    }

    /// The three panels side by side, with handles between them.
    private var panelsRow: some View {
        GeometryReader { g in
            let hasPlaybook = !assist.state.playbook.isEmpty
            let handles = hasPlaybook ? 2.0 : 1.0
            let avail = max(300, g.size.width - handles * ResizeHandle.thickness)
            let (sh, oh) = shares(hasPlaybook)
            let summaryW = avail * sh
            let openW = hasPlaybook ? avail * oh : avail - summaryW
            HStack(spacing: 0) {
                summaryBox.frame(width: summaryW)
                ResizeHandle(axis: .horizontal) { delta, ended in
                    resize(.afterSummary, by: delta / avail, ended: ended, hasPlaybook: hasPlaybook)
                }
                openPointsBox.frame(width: openW)
                if hasPlaybook {
                    ResizeHandle(axis: .horizontal) { delta, ended in
                        resize(.afterOpenPoints, by: delta / avail, ended: ended, hasPlaybook: true)
                    }
                    playbookBox.frame(maxWidth: .infinity)
                }
            }
        }
    }

    enum Border { case afterSummary, afterOpenPoints }

    /// Width shares in use. Without the playbook, Summary and Open points fill the row; with it,
    /// the playbook keeps at least 15 %.
    private func shares(_ hasPlaybook: Bool) -> (Double, Double) {
        let s = max(0.05, summaryShare), o = max(0.05, openShare)
        if !hasPlaybook { return (s / (s + o), o / (s + o)) }
        let scale = s + o > 0.85 ? 0.85 / (s + o) : 1
        return (s * scale, o * scale)
    }

    /// Moves the border right of Summary or right of Open points. Every panel keeps at least
    /// 15 % of the width.
    private func resize(_ border: Border, by delta: Double, ended: Bool, hasPlaybook: Bool) {
        let minShare = 0.15
        let (sh, oh) = shares(hasPlaybook)
        switch border {
        case .afterSummary:
            let start = dragStart ?? sh
            dragStart = ended ? nil : start
            // Summary and Open points trade width; the playbook (if any) stays as it is.
            let pair = sh + oh
            let new = min(max(start + delta, minShare), pair - minShare)
            summaryShare = new
            openShare = pair - new
        case .afterOpenPoints:
            let start = dragStart ?? oh
            dragStart = ended ? nil : start
            summaryShare = sh
            openShare = min(max(start + delta, minShare), 1 - sh - minShare)
        }
    }

    private var statusBar: some View {
        HStack(spacing: 12) {
            AyaAgentDot(icon: assist.paused ? "pause.fill" : "mic.fill", size: 30)
            VStack(alignment: .leading, spacing: 1) {
                AyaEyebrow(text: assist.paused ? "Paused" : "Listening", color: Aya.agentTextMid)
                Text(Exporter.clock(state.elapsed)).font(.system(size: 15, weight: .semibold).monospacedDigit())
            }
            AyaWaveform(level: assist.paused ? 0 : ayaLevel(max(state.micLevel, state.systemLevel)))
            Rectangle().fill(Aya.border).frame(width: 1, height: 28).padding(.horizontal, 4)
            VStack(alignment: .leading, spacing: 1) {
                Text(assist.context?.title ?? "").font(.system(size: 14, weight: .semibold)).lineLimit(1)
                if let p = assist.context?.playbookTitle { Text(p).font(.system(size: 11)).foregroundStyle(Aya.textMuted).lineLimit(1) }
            }
            Spacer()
            statusText
            AyaTag(text: "\(assist.state.updates) updates", color: Aya.agentTextMid)
                .help(String(format: "Each update uses some of your Claude subscription (API equivalent so far: $%.2f).", assist.state.costUSD))
            Menu {
                ForEach([2, 5, 10], id: \.self) { m in
                    Button("Last \(m) minutes") { Task { await assist.whatDidIMiss(minutes: m) } }
                }
            } label: {
                Label("What did I miss?", systemImage: "clock.arrow.circlepath")
            }
            .menuStyle(.borderlessButton).fixedSize()
            .disabled(assist.catchUpState.isWorking)
            .help("A short summary of the last minutes, for when you were distracted")
            Button { assist.togglePause() } label: { Label(assist.paused ? "Resume" : "Pause", systemImage: assist.paused ? "play.fill" : "pause.fill") }
                .buttonStyle(AyaButtonStyle(kind: .secondary, pill: true))
                .help("Pause the assistant; recording and transcript continue.")
            Button { Task { await assist.stop() } } label: { Label("Stop", systemImage: "stop.fill") }
                .buttonStyle(AyaButtonStyle(kind: .destructive, pill: true))
        }
        .padding(.horizontal, 14).padding(.vertical, 10)
        .ayaAgentSurface(active: !assist.paused)
    }

    private var catchUpCard: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                AyaEyebrow(text: assist.catchUp.map { "The last \($0.minutes) minutes" } ?? "What did I miss?",
                           icon: "clock.arrow.circlepath", color: Aya.agentTextMid)
                Spacer()
                Button { assist.catchUp = nil; assist.catchUpState = .idle } label: { Image(systemName: "xmark") }
                    .buttonStyle(AyaButtonStyle(kind: .ghost, size: .small)).help("Close")
            }
            StepLine(step: assist.catchUpState)
            if let c = assist.catchUp {
                Text(c.text).font(.system(size: 14)).textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
            }
        }
        .padding(14)
        .frame(maxWidth: .infinity, alignment: .leading)
        .ayaSurface()
    }

    @ViewBuilder private var statusText: some View {
        switch assist.status {
        case .thinking: HStack(spacing: 5) { ProgressView().controlSize(.mini); Text("Thinking") }.font(.system(size: 11)).foregroundStyle(Aya.agentTextMid)
        case .limited, .failed, .reconnecting: Text(assist.status.text).font(.system(size: 11)).foregroundStyle(Aya.warning).lineLimit(2)
        default: Text(assist.status.text).font(.system(size: 11)).foregroundStyle(Aya.textMuted)
        }
    }

    private var questionCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                AyaEyebrow(text: "Next question", icon: "sparkles", color: Aya.agentTextMid)
                Spacer()
                Button { assist.suggestNow() } label: { Label("Suggest now", systemImage: "arrow.triangle.2.circlepath") }
                    .buttonStyle(AyaButtonStyle(kind: .ghost, size: .small))
                    .keyboardShortcut(.space, modifiers: [.command, .shift]).help("Ask for a fresh suggestion now (⇧⌘Space)")
            }
            if let q = assist.state.questions.first {
                Text(q.text).font(.system(size: 21, weight: .medium)).textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
                    .modifier(Highlight(on: assist.state.changed.contains(q.id)))
                HStack(alignment: .center, spacing: 8) {
                    if let p = q.playbookItem { AyaTag(text: p, color: Aya.agentTextMid, icon: "checklist") }
                    if let why = q.why { Text(why).font(.system(size: 12)).foregroundStyle(Aya.textMuted).lineLimit(2) }
                    Spacer()
                    Button { assist.dismiss(q.id) } label: { Label("Other", systemImage: "arrow.uturn.right") }
                        .buttonStyle(AyaButtonStyle(kind: .secondary, pill: true))
                        .help("Not this one; suggest something else")
                    Button { assist.markAsked(q.id) } label: { Label("Asked", systemImage: "checkmark") }
                        .buttonStyle(AyaButtonStyle(kind: .agent, pill: true))
                        .help("I asked this; it won't be suggested again")
                }
                if assist.state.questions.count > 1 {
                    FlowLayout(spacing: 6) {
                        ForEach(assist.state.questions.dropFirst()) { alt in
                            Button { assist.promote(alt.id) } label: {
                                Label(alt.text, systemImage: "arrow.turn.down.right").multilineTextAlignment(.leading)
                            }
                            .buttonStyle(AyaChipStyle())
                            .help("Show this one first")
                        }
                    }
                }
            } else {
                Text(assist.status == .starting || assist.status == .thinking ? "Claude is reading the client context…" : "Waiting for the conversation…")
                    .font(.system(size: 17)).foregroundStyle(Aya.textMuted)
            }
        }
        .padding(16)
        .ayaAgentSurface()
    }

    /// One paragraph per topic; the topic label before the colon in bold.
    private var summaryBox: some View {
        PanelBox(title: "Summary", icon: "text.alignleft", trailing: panelCount("\(assist.state.summary.count)")) {
            ScrollViewReader { proxy in
                ScrollView {
                    VStack(alignment: .leading, spacing: 10) {
                        ForEach(Array(assist.state.summary.enumerated()), id: \.offset) { i, p in
                            Self.paragraph(p)
                                .font(.system(size: 13))
                                .textSelection(.enabled)
                                .fixedSize(horizontal: false, vertical: true)
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .modifier(Highlight(on: assist.state.changed.contains("s\(i)"))).id(i)
                        }
                    }
                }
                // Follow the paragraph that just changed (new or rewritten).
                .onChange(of: assist.state.changed) { _, changed in
                    let idx = changed.compactMap { $0.hasPrefix("s") ? Int($0.dropFirst()) : nil }.max()
                    if let idx { withAnimation { proxy.scrollTo(idx, anchor: .center) } }
                }
            }
        }
    }

    /// "Lager: Bestände unbekannt …" → topic in bold. Text without a short label stays as it is.
    static func paragraph(_ p: String) -> Text {
        if let r = p.range(of: ": "), p.distance(from: p.startIndex, to: r.lowerBound) <= 40 {
            let topic = String(p[..<r.lowerBound]).trimmingCharacters(in: CharacterSet(charactersIn: "*# "))
            return Text(topic + ": ").fontWeight(.semibold) + Text(String(p[r.upperBound...]))
        }
        return Text(p)
    }

    private var openPointsBox: some View {
        PanelBox(title: "Open points", icon: "circle.dashed",
                 trailing: panelCount("\(assist.state.openPoints.filter { $0.status == .open }.count) open")) {
            ScrollView {
                VStack(alignment: .leading, spacing: 6) {
                    ForEach(assist.state.openPoints.sorted { ($0.status == .open ? 0 : 1) < ($1.status == .open ? 0 : 1) }) { p in
                        HStack(alignment: .top, spacing: 8) {
                            Button {
                                assist.setOpenPoint(p.id, p.status == .answered ? .open : .answered)
                            } label: {
                                Image(systemName: p.status == .answered ? "checkmark.circle.fill" : p.status == .parked ? "pause.circle" : "circle")
                                    .font(.system(size: 14))
                                    .foregroundStyle(p.status == .answered ? Aya.success : Aya.textSubtle)
                            }
                            .buttonStyle(.plain).help(p.status == .answered ? "Mark open" : "Mark answered")
                            Text(p.text).font(.system(size: 13)).foregroundStyle(p.status == .open ? Aya.text : Aya.textMuted)
                                .strikethrough(p.status == .answered, color: Aya.textSubtle)
                                .frame(maxWidth: .infinity, alignment: .leading)
                        }
                        .modifier(Highlight(on: assist.state.changed.contains("o:\(p.id)")))
                    }
                }
            }
        }
    }

    private var playbookBox: some View {
        let items = assist.state.playbook
        let covered = items.filter { $0.status == .covered }.count
        return PanelBox(title: "Checklist", icon: "checklist", trailing: panelCount("\(covered)/\(items.count)")) {
            VStack(alignment: .leading, spacing: 8) {
                GeometryReader { g in
                    ZStack(alignment: .leading) {
                        Capsule().fill(Aya.muted)
                        Capsule().fill(Aya.agent).frame(width: items.isEmpty ? 0 : g.size.width * CGFloat(covered) / CGFloat(items.count))
                    }
                }
                .frame(height: 4)
                ScrollView {
                    VStack(alignment: .leading, spacing: 5) {
                        ForEach(Array(items.enumerated()), id: \.element.id) { i, item in
                            if i == 0 || items[i - 1].section != item.section, !item.section.isEmpty {
                                Text(item.section).font(.system(size: 11, weight: .semibold)).foregroundStyle(Aya.textMuted).padding(.top, i == 0 ? 0 : 6)
                            }
                            HStack(alignment: .top, spacing: 8) {
                                Button {
                                    assist.setPlaybook(item.id, item.status == .covered ? .missing : .covered)
                                } label: {
                                    Image(systemName: item.status == .covered ? "checkmark.circle.fill" : item.status == .partial ? "circle.lefthalf.filled" : "circle")
                                        .font(.system(size: 14))
                                        .foregroundStyle(item.status == .covered ? Aya.success : item.status == .partial ? Aya.warning : Aya.textSubtle)
                                }
                                .buttonStyle(.plain)
                                Text(item.text).font(.system(size: 13)).foregroundStyle(item.status == .covered ? Aya.textMuted : Aya.text)
                                    .help(item.evidence ?? "")
                            }
                            .modifier(Highlight(on: assist.state.changed.contains("p:\(item.id)")))
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
            }
        }
    }

    private var attendeesRow: some View {
        let present = assist.state.attendees.filter { $0.present != false }
        let other = assist.state.attendees.filter { $0.present == false }
        return HStack(alignment: .center, spacing: 10) {
            AyaEyebrow(text: "Attendees", icon: "person.2")
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 6) {
                    if present.isEmpty && other.isEmpty {
                        Text("Recognised from the conversation and from Aya contacts").font(.system(size: 12)).foregroundStyle(Aya.textSubtle)
                    }
                    ForEach(present) { a in chip(a, dim: false) }
                    if !other.isEmpty {
                        Text("Mentioned").font(.system(size: 11, weight: .medium)).foregroundStyle(Aya.textSubtle).padding(.leading, 6)
                        ForEach(other) { a in chip(a, dim: true) }
                    }
                }
                .padding(.vertical, 2)
            }
        }
        .padding(.horizontal, 14).padding(.vertical, 8)
        .ayaSurface()
    }

    private func chip(_ a: AssistState.Attendee, dim: Bool) -> some View {
        let raw = assist.rawSpeaker(for: a)
        return Button { attendeePopover = a.id } label: { chipLabel(a, dim: dim) }
            .buttonStyle(.plain)
            .pointerStyle(.link)
            .popover(isPresented: Binding(get: { attendeePopover == a.id }, set: { if !$0 { attendeePopover = nil } }), arrowEdge: .bottom) {
                PersonPopover(assist: assist, name: a.name, contactID: a.contactID, label: a.speaker,
                              onRename: raw.map { r in { assist.renameSpeaker(raw: r, to: $0) } })
            }
    }

    private func chipLabel(_ a: AssistState.Attendee, dim: Bool) -> some View {
        HStack(spacing: 5) {
            Text(ayaInitial(a.name))
                .font(.system(size: 10, weight: .semibold))
                .foregroundStyle(a.contactID != nil ? Aya.agentForeground : Aya.primary)
                .frame(width: 20, height: 20)
                .background(a.contactID != nil ? Aya.agent : Aya.primary.opacity(0.12), in: Circle())
                .help(a.contactID != nil ? "Known contact in Aya" : "Not in Aya yet")
            Text(a.name).fontWeight(.medium)
            if let r = a.role { Text(r).foregroundStyle(Aya.textMuted) }
            if let s = a.speaker, s != a.name { Text("= \(s)").foregroundStyle(Aya.textSubtle) }
        }
        .font(.system(size: 12))
        .padding(.leading, 3).padding(.trailing, 10).padding(.vertical, 3)
        .background(Aya.card, in: Capsule())
        .overlay(Capsule().strokeBorder(Aya.border, lineWidth: 1))
        .opacity(dim ? 0.6 : 1)
        .help(a.notes ?? "Click for details from Aya")
        .modifier(Highlight(on: assist.state.changed.contains("a:\(a.id)")))
    }

    /// The live transcript: all lines, scrolled to the newest, taking the rest of the height.
    private var transcriptPanel: some View {
        VStack(alignment: .leading, spacing: 6) {
            AyaEyebrow(text: "Transcript", icon: "text.quote")
            ScrollViewReader { proxy in
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 4) {
                        ForEach(state.liveLines.suffix(300)) { l in
                            HStack(alignment: .firstTextBaseline, spacing: 8) {
                                speakerButton(l)
                                Text(l.text).foregroundColor(l.isFinal ? Aya.text : Aya.textSubtle)
                                    .textSelection(.enabled)
                                    .fixedSize(horizontal: false, vertical: true)
                                    .frame(maxWidth: .infinity, alignment: .leading)
                            }
                            .font(.system(size: 12.5))
                            .id(l.id)
                        }
                    }
                    .padding(.trailing, 6)
                }
                .onChange(of: state.liveLines.last?.id) { _, id in
                    if let id { withAnimation(.easeOut(duration: 0.15)) { proxy.scrollTo(id, anchor: .bottom) } }
                }
                .onAppear {
                    // After the first layout, otherwise the scroll position is lost.
                    DispatchQueue.main.async { if let id = state.liveLines.last?.id { proxy.scrollTo(id, anchor: .bottom) } }
                }
            }
        }
        .padding(.horizontal, 14).padding(.vertical, 10)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .ayaSurface(fill: Aya.card.opacity(0.7), shadow: false)
    }

    /// The speaker's name; a click shows what Aya knows about them and lets you name them.
    @ViewBuilder private func speakerButton(_ l: LiveLine) -> some View {
        let name = assist.liveName(l)
        let color = l.track == .mic ? Aya.primary : Aya.agentTextMid
        if let raw = l.speaker, let name {
            Button { speakerPopover = l.id } label: {
                Text(name).fontWeight(.semibold).foregroundColor(color)
                    .underline(SpeakerNames.isGeneric(name), pattern: .dot, color: color.opacity(0.5))
            }
            .buttonStyle(.plain)
            .pointerStyle(.link)
            .help(SpeakerNames.isGeneric(name) ? "Who is this? Click to name the speaker." : "Click for details from Aya, or to rename")
            .popover(isPresented: Binding(get: { speakerPopover == l.id }, set: { if !$0 { speakerPopover = nil } }), arrowEdge: .trailing) {
                PersonPopover(assist: assist, name: name,
                              contactID: assist.state.attendees.first { $0.name == name }?.contactID,
                              label: assist.state.speakerNames?[raw]) { assist.renameSpeaker(raw: raw, to: $0) }
            }
        } else {
            Text("…").fontWeight(.semibold).foregroundColor(Aya.textSubtle).help("Speaker not recognised yet")
        }
    }
}

/// A thin bar between two panels that resizes them when dragged. Reports the drag distance
/// since the start, and once more when the drag ends.
private struct ResizeHandle: View {
    enum Axis { case horizontal, vertical }   // horizontal: a vertical bar that moves left/right
    static let thickness: CGFloat = 8
    let axis: Axis
    let onDrag: (_ delta: Double, _ ended: Bool) -> Void
    @State private var hovering = false

    var body: some View {
        ZStack {
            Color.clear
            Capsule()
                .fill(hovering ? Aya.primary.opacity(0.6) : Aya.border)
                .frame(width: axis == .horizontal ? 3 : 36, height: axis == .horizontal ? 36 : 3)
        }
        .frame(width: axis == .horizontal ? Self.thickness : nil, height: axis == .vertical ? Self.thickness : nil)
        .frame(maxWidth: axis == .vertical ? .infinity : nil, maxHeight: axis == .horizontal ? .infinity : nil)
        .contentShape(Rectangle())
        .onHover { h in
            hovering = h
            if h { (axis == .horizontal ? NSCursor.resizeLeftRight : NSCursor.resizeUpDown).push() } else { NSCursor.pop() }
        }
        .gesture(
            DragGesture(minimumDistance: 1, coordinateSpace: .global)
                .onChanged { v in onDrag(axis == .horizontal ? v.translation.width : v.translation.height, false) }
                .onEnded { v in onDrag(axis == .horizontal ? v.translation.width : v.translation.height, true) }
        )
    }
}

// MARK: - Review

private struct AssistReviewView: View {
    @EnvironmentObject var state: AppState
    @ObservedObject var assist: AssistController
    let sessionID: String
    @State private var confirmDelete: SessionInfo?
    @State private var attendeePopover: String?
    @State private var confirmRedraft = false
    @State private var confirmRepropose = false

    var body: some View {
        VStack(spacing: 14) {
            PageHeader(eyebrow: "Meeting review", title: assist.record?.context.title ?? "Meeting",
                       subtitle: [assist.record?.context.playbookTitle, assist.record.map { $0.context.date.formatted(date: .abbreviated, time: .shortened) }]
                        .compactMap { $0 }.joined(separator: " · ")) {
                HStack(spacing: 8) {
                    Button { confirmDelete = state.sessions.first { $0.id == sessionID } } label: { Label("Delete", systemImage: "trash") }
                        .buttonStyle(AyaButtonStyle(kind: .ghost))
                        .disabled(state.sessions.first { $0.id == sessionID }.map(AssistMeetingsPanel.isBusy) ?? true)
                        .help("Delete the recording, transcript and notes on this Mac")
                    Button { state.appMode = .scribe; state.selection = .session(sessionID) } label: { Label("Show Transcript", systemImage: "text.bubble") }
                        .buttonStyle(AyaButtonStyle(kind: .secondary))
                    Button("Done") { assist.finishReview() }
                        .buttonStyle(AyaButtonStyle(kind: .primary))
                }
            }
            ScrollView {
                VStack(alignment: .leading, spacing: 12) {
                    ReviewSpeakersSection(assist: assist, sessionID: sessionID)
                    recapSection
                    if !assist.reviewTables.isEmpty { ReviewTablesSection(assist: assist) }
                    if hasMeetingState { meetingStateSection }
                    if assist.record?.context.clientID != nil { crmSection }
                }
                .frame(maxWidth: 920, alignment: .leading)
                .frame(maxWidth: .infinity)
                .padding(.bottom, 12)
            }
        }
        .padding(20)
        .background(AyaBackdrop())
        .deleteMeetingDialog(assist: assist, item: $confirmDelete)
        .confirmationDialog("Draft the recap again?", isPresented: $confirmRedraft) {
            Button("Draft Again", role: .destructive) { Task { await assist.draftRecap() } }
        } message: { Text("Claude writes a new recap. Your edits to the current one are replaced.") }
        .confirmationDialog("Propose the updates again?", isPresented: $confirmRepropose) {
            Button("Propose Again", role: .destructive) { Task { await assist.proposeCRM() } }
        } message: { Text("Claude makes a new proposal. Your ticks and edits in the current one are replaced.") }
    }

    // The meeting as it ended: summary, open points and who was there.

    private var hasMeetingState: Bool {
        let s = assist.state
        return !s.summary.isEmpty || !s.openPoints.isEmpty || !s.attendees.isEmpty || !s.playbook.isEmpty
    }

    private var meetingStateSection: some View {
        let s = assist.state
        return VStack(alignment: .leading, spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                Text("During the meeting").font(.system(size: 16, weight: .semibold))
                Text("The live notes as they were when the meeting ended.").font(.system(size: 12)).foregroundStyle(Aya.textMuted)
            }
            if !s.attendees.isEmpty {
                FlowLayout(spacing: 6) {
                    ForEach(s.attendees) { a in
                        Button { attendeePopover = a.id } label: {
                            HStack(spacing: 5) {
                                Text(ayaInitial(a.name)).font(.system(size: 10, weight: .semibold))
                                    .foregroundStyle(a.contactID != nil ? Aya.agentForeground : Aya.primary)
                                    .frame(width: 20, height: 20)
                                    .background(a.contactID != nil ? Aya.agent : Aya.primary.opacity(0.12), in: Circle())
                                Text(a.name).fontWeight(.medium)
                                if let r = a.role { Text(r).foregroundStyle(Aya.textMuted) }
                            }
                            .font(.system(size: 12))
                            .padding(.leading, 3).padding(.trailing, 10).padding(.vertical, 3)
                            .background(Aya.card, in: Capsule())
                            .overlay(Capsule().strokeBorder(Aya.border, lineWidth: 1))
                            .opacity(a.present == false ? 0.6 : 1)
                        }
                        .buttonStyle(.plain)
                        .pointerStyle(.link)
                        .popover(isPresented: Binding(get: { attendeePopover == a.id }, set: { if !$0 { attendeePopover = nil } }), arrowEdge: .bottom) {
                            PersonPopover(assist: assist, name: a.name, contactID: a.contactID)
                        }
                    }
                }
            }
            if !s.summary.isEmpty {
                VStack(alignment: .leading, spacing: 8) {
                    AyaEyebrow(text: "Summary", icon: "text.alignleft")
                    ForEach(Array(s.summary.enumerated()), id: \.offset) { _, p in
                        AssistLiveView.paragraph(p).font(.system(size: 13)).textSelection(.enabled)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
            }
            if !s.openPoints.isEmpty {
                VStack(alignment: .leading, spacing: 5) {
                    AyaEyebrow(text: "Open points", icon: "circle.dashed")
                    ForEach(s.openPoints) { p in
                        HStack(alignment: .firstTextBaseline, spacing: 8) {
                            Image(systemName: p.status == .answered ? "checkmark.circle.fill" : p.status == .parked ? "pause.circle" : "circle")
                                .foregroundStyle(p.status == .answered ? Aya.success : Aya.textSubtle)
                            Text(p.text).foregroundStyle(p.status == .open ? Aya.text : Aya.textMuted)
                                .strikethrough(p.status == .answered, color: Aya.textSubtle)
                                .textSelection(.enabled)
                        }
                        .font(.system(size: 13))
                    }
                }
            }
            let pb = s.playbook
            if !pb.isEmpty {
                let covered = pb.filter { $0.status == .covered }.count
                Text("Template: \(covered) of \(pb.count) points covered" + (pb.contains { $0.status == .missing } ? " · missing: " + pb.filter { $0.status == .missing }.prefix(5).map(\.text).joined(separator: "; ") : ""))
                    .font(.system(size: 12)).foregroundStyle(Aya.textMuted).fixedSize(horizontal: false, vertical: true)
            }
        }
        .padding(18)
        .frame(maxWidth: .infinity, alignment: .leading)
        .ayaSurface()
    }

    // Recap

    private var recapSection: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .center) {
                VStack(alignment: .leading, spacing: 2) {
                    Text("Meeting recap").font(.system(size: 16, weight: .semibold))
                    if let ref = assist.config.list(.meetings) { Text("→ \(ref.label) in \(ref.workspaceLabel)").font(.system(size: 12)).foregroundStyle(Aya.textMuted) }
                }
                Spacer()
                if let id = assist.record?.recapRecordID, !id.isEmpty {
                    AyaTag(text: "In Aya", color: Aya.success, icon: "checkmark")
                    if let url = assist.ayaURL(.meetings, record: id) {
                        Link(destination: url) { Label("Open in Aya", systemImage: "arrow.up.right.square") }
                            .buttonStyle(AyaButtonStyle(kind: .secondary, pill: true))
                            .help("Open this meeting in Aya")
                    }
                }
                Button {
                    if assist.recapFields.isEmpty { Task { await assist.draftRecap() } } else { confirmRedraft = true }
                } label: {
                    Label(assist.recapFields.isEmpty ? "Draft Recap" : "Draft Again", systemImage: "sparkles")
                }
                .buttonStyle(AyaButtonStyle(kind: assist.recapFields.isEmpty ? .agent : .secondary, pill: true))
                .disabled(assist.recapState.isWorking)
            }
            targetRow
            StepLine(step: assist.recapState)
            if !assist.recapFields.isEmpty {
                VStack(alignment: .leading, spacing: 0) {
                    ForEach(Array($assist.recapFields.enumerated()), id: \.offset) { i, $f in
                        if i > 0 { Divider().overlay(Aya.border) }
                        FieldEditor(field: $f).padding(.vertical, 10)
                    }
                }
                HStack {
                    Spacer()
                    Button { Task { await assist.uploadRecap() } } label: {
                        Label(assist.targetMeetingID == nil ? "Create Meeting in Aya" : "Save to Meeting", systemImage: "arrow.up.circle")
                    }
                    .buttonStyle(AyaButtonStyle(kind: .primary)).disabled(assist.recapState.isWorking)
                }
            }
        }
        .padding(18)
        .ayaSurface()
    }

    /// Where the recap goes: the planned meeting, another meeting of that day, or a new one.
    private var targetRow: some View {
        let cfg = assist.config
        let options: [(String?, String)] = [(nil, "New meeting in Aya")] + assist.targetChoices.map { m in
            let time = m.date(cfg.meetingDateField).map { $0.formatted(date: .omitted, time: .shortened) } ?? ""
            let planned = m.id == assist.record?.context.meetingID ? " (planned)" : ""
            return (m.id, [time, m.title].filter { !$0.isEmpty }.joined(separator: " · ") + planned)
        }
        return HStack(spacing: 10) {
            Text("Save to").font(.system(size: 13, weight: .medium))
            AyaSelect(selection: Binding(get: { assist.targetMeetingID }, set: { assist.setTarget($0) }), options: options)
                .frame(maxWidth: 420)
            if assist.targetMeetingID != nil {
                Text("Fields that already have content in Aya are only overwritten when you tick them.")
                    .font(.system(size: 11)).foregroundStyle(Aya.textSubtle).fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 0)
        }
    }

    // CRM

    private var crmSection: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                VStack(alignment: .leading, spacing: 2) {
                    Text("Update the CRM").font(.system(size: 16, weight: .semibold))
                    Text("Claude compares the client and contact records with what was said. Nothing is changed until you tick it and apply.")
                        .font(.system(size: 12)).foregroundStyle(Aya.textMuted)
                }
                Spacer()
                Button {
                    if assist.crm == nil { Task { await assist.proposeCRM() } } else { confirmRepropose = true }
                } label: {
                    Label(assist.crm == nil ? "Propose Updates" : "Propose Again", systemImage: "sparkles")
                }
                .buttonStyle(AyaButtonStyle(kind: assist.crm == nil ? .agent : .secondary, pill: true))
                .disabled(assist.crmState.isWorking)
            }
            StepLine(step: assist.crmState)
            if let p = assist.crm {
                if !p.client.isEmpty {
                    HStack(spacing: 8) {
                        Image(systemName: "building.2").foregroundStyle(Aya.primary)
                        Text(assist.record?.context.clientTitle ?? "Client").font(.system(size: 14, weight: .semibold))
                    }
                    ForEach(Binding(get: { assist.crm!.client }, set: { assist.crm!.client = $0 })) { $c in ChangeRow(change: $c) }
                }
                ForEach(Binding(get: { assist.crm!.contacts }, set: { assist.crm!.contacts = $0 })) { $c in
                    VStack(alignment: .leading, spacing: 8) {
                        Toggle(isOn: $c.include) {
                            HStack(spacing: 6) {
                                Text(c.name).font(.system(size: 14, weight: .semibold))
                                if c.contactID == nil { AyaTag(text: "New contact", color: Aya.agentTextMid, icon: "plus") }
                            }
                        }
                        .toggleStyle(.checkbox)
                        if c.include {
                            ForEach($c.changes) { $ch in ChangeRow(change: $ch, showToggle: c.contactID != nil) }
                                .padding(.leading, 20)
                        }
                    }
                    .padding(12)
                    .ayaSurface(radius: Aya.radiusContainer, fill: Aya.page.opacity(0.5), shadow: false)
                }
                HStack {
                    Spacer()
                    let n = p.client.filter(\.include).count + p.contacts.filter(\.include).count
                    Button { Task { await assist.applyCRM() } } label: { Label("Apply Selected (\(n))", systemImage: "checkmark.circle") }
                        .buttonStyle(AyaButtonStyle(kind: .primary)).disabled(n == 0 || assist.crmState.isWorking)
                }
            }
        }
        .padding(18)
        .ayaSurface()
    }
}

/// One editable recap field.
private struct FieldEditor: View {
    @Binding var field: ProposedValue
    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Toggle(isOn: $field.include) {
                HStack(spacing: 6) {
                    Text(field.label).font(.system(size: 13, weight: .semibold))
                    if !field.old.isEmpty { AyaTag(text: field.include ? "Replaces the current text" : "Has content in Aya", color: field.include ? Aya.warning : Aya.textMuted) }
                }
            }
            .toggleStyle(.checkbox)
            if !field.old.isEmpty {
                HStack(alignment: .top, spacing: 6) {
                    Text("Now").font(.system(size: 10, weight: .semibold)).foregroundStyle(Aya.textSubtle)
                    Text(field.old).font(.system(size: 12)).foregroundStyle(Aya.textMuted).lineLimit(field.include ? 2 : 4)
                }
                .padding(.leading, 20)
            }
            if field.include { editor(for: $field).padding(.leading, 20) }
            if let note = field.reason {
                Label(note, systemImage: "calendar.badge.exclamationmark")
                    .font(.system(size: 11)).foregroundStyle(Aya.warning).padding(.leading, 20)
            }
        }
    }
}

/// One proposed CRM change: old value, editable new value, reason.
private struct ChangeRow: View {
    @Binding var change: ProposedValue
    var showToggle = true
    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            if showToggle {
                Toggle(isOn: $change.include) { Text(change.label).font(.system(size: 13, weight: .medium)) }.toggleStyle(.checkbox)
            } else {
                Text(change.label).font(.system(size: 13, weight: .medium))
            }
            if !change.old.isEmpty {
                HStack(alignment: .top, spacing: 6) {
                    Text("Now").font(.system(size: 10, weight: .semibold)).foregroundStyle(Aya.textSubtle)
                    Text(change.old).font(.system(size: 12)).foregroundStyle(Aya.textMuted).lineLimit(3)
                }
                .padding(.leading, 20)
            }
            editor(for: $change).padding(.leading, 20)
            if let r = change.reason {
                Label(r, systemImage: "quote.opening").labelStyle(AyaLabelStyle())
                    .font(.system(size: 11)).foregroundStyle(Aya.agentTextMid).padding(.leading, 20)
            }
        }
        .padding(.vertical, 4)
    }
}

@ViewBuilder
private func editor(for value: Binding<ProposedValue>) -> some View {
    switch value.wrappedValue.kind {
    case .boolean:
        AyaSegmented(selection: value.value, options: [("true", "Yes", nil), ("false", "No", nil)])
            .frame(width: 160)
            .onAppear { let v = value.wrappedValue.value.lowercased(); value.wrappedValue.value = ["true", "yes", "ja", "1"].contains(v) ? "true" : "false" }
    case .select:
        AyaSelect(selection: value.value, options: [("", "—")] + value.wrappedValue.options.map { ($0, $0) })
            .frame(maxWidth: 260)
    case .markdown:
        TextEditor(text: value.value).font(.system(size: 13)).frame(minHeight: 90, maxHeight: 260)
            .scrollContentBackground(.hidden).padding(6)
            .background(Aya.card, in: RoundedRectangle(cornerRadius: Aya.radiusControl, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: Aya.radiusControl, style: .continuous).strokeBorder(Aya.input, lineWidth: 1))
    default:
        TextField("", text: value.value, axis: .vertical).lineLimit(1...6).textFieldStyle(AyaFieldStyle())
    }
}

// MARK: - Floating panel banner

/// Compact assist line at the top of the floating transcript window.
struct AssistBanner: View {
    @ObservedObject var assist: AssistController
    var body: some View {
        if assist.phase == .running {
            VStack(alignment: .leading, spacing: 5) {
                HStack(alignment: .top, spacing: 8) {
                    Image(systemName: "sparkles").font(.system(size: 12, weight: .semibold)).foregroundStyle(Aya.agentTextMid).padding(.top, 2)
                    Text(assist.state.questions.first?.text ?? assist.status.text)
                        .font(.system(size: 13, weight: .medium)).lineLimit(3).textSelection(.enabled)
                    Spacer(minLength: 0)
                }
                let open = assist.state.openPoints.filter { $0.status == .open }.count
                HStack(spacing: 8) {
                    if open > 0 { Text("\(open) open point\(open == 1 ? "" : "s")") }
                    if assist.status == .thinking { Text("updating…").foregroundStyle(Aya.agentTextMid) }
                    Spacer()
                    if let q = assist.state.questions.first {
                        Button("Other") { assist.dismiss(q.id) }.buttonStyle(AyaButtonStyle(kind: .ghost, size: .small, pill: true))
                        Button("Asked") { assist.markAsked(q.id) }.buttonStyle(AyaButtonStyle(kind: .agent, size: .small, pill: true))
                    }
                }
                .font(.system(size: 11)).foregroundStyle(Aya.textMuted)
            }
            .padding(10)
            .ayaAgentSurface(radius: Aya.radiusContainer)
            .padding(8)
        }
    }
}

// MARK: - Snapshots (debug)

/// Renders the Assist screens offscreen with sample data, for checking the layout without a visible screen.
/// Launch with `-assistSnapshot <dir> [-assistSnapshotRecord <assist.json>]`.
@MainActor
enum AssistSnapshots {
    static func run(app: AppState, dir: URL, record: URL?) {
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let a = app.assist
        let rec = record.flatMap { try? JSONIO.read(AssistRecord.self, from: $0) }

        // 0. Whole window in Scribe mode
        let mode = app.appMode
        app.appMode = .scribe
        render(ContentView(), app: app, size: CGSize(width: 1280, height: 800), to: dir.appendingPathComponent("0-scribe.png"), wait: 1.5)
        if let first = app.sessions.first(where: { $0.status == .done }) {
            app.selection = .session(first.id)
            render(ContentView(), app: app, size: CGSize(width: 1280, height: 800), to: dir.appendingPathComponent("0-session.png"), wait: 1.5)
            app.selection = .recorder
        }
        app.appMode = .assist

        // 1. Preparation
        a.phase = .prepare
        if let first = a.clients.first(where: { $0.title.contains("MUSTER") }) ?? a.clients.first { a.clientID = first.id }
        a.templateID = a.templates.first(where: { $0.title.contains("Kickoff") })?.id ?? a.templates.first?.id
        a.projectID = a.projects.first(where: { $0.title.contains("P0182") })?.id
        a.claudeState = .done("ok"); a.claudeBinary = URL(fileURLWithPath: "/usr/bin/true"); a.claudeAuth = .init(loggedIn: true, method: nil, subscription: "team", email: nil)
        // Network calls don't finish while rendering here, so planned meetings can come from a file.
        if let f = ProcessInfo.processInfo.environment["ASSIST_SNAPSHOT_UPCOMING"],
           let list = try? JSONIO.read([AyaRecord].self, from: URL(fileURLWithPath: f)) {
            a.upcoming = list
            if let pne = list.first(where: { a.config.meetingProjectField.flatMap($0.linkedID) != nil }) { a.meetingID = pne.id }
        }
        render(ContentView(), app: app, size: CGSize(width: 1280, height: 800), to: dir.appendingPathComponent("1-prepare.png"), wait: 15)

        // 2. Live
        if let rec {
            a.context = rec.context
            var s = rec.state
            s.changed = Set(["s\(s.summary.count - 1)", s.questions.first?.id ?? ""])
            a.state = s
            a.status = .listening
            app.elapsed = 1342
            var raws: [String: String] = [:]
            app.liveLines = rec.lines.suffix(40).enumerated().map { i, l in
                let mine = l.speaker == rec.context.myName || l.speaker == "Me"
                let raw = mine ? "me" : (raws[l.speaker] ?? "system-\(raws.count)")
                if !mine { raws[l.speaker] = raw }
                return LiveLine(id: "snap\(i)", track: mine ? .mic : .system, start: l.start, end: l.start + 3,
                                text: l.text, speaker: raw, isFinal: true)
            }
            a.state.speakerNames = Dictionary(uniqueKeysWithValues: raws.map { ($0.value, $0.key) })
            a.phase = .running
            render(ContentView(), app: app, size: CGSize(width: 1280, height: 860), to: dir.appendingPathComponent("2-live.png"), wait: 0.5)
            render(AssistBanner(assist: a).frame(width: 420), app: app, size: CGSize(width: 420, height: 90), to: dir.appendingPathComponent("4-banner.png"), wait: 0.3)

            // 3. Review
            a.record = rec
            a.recapFields = [
                ProposedValue(key: "title__value", label: "Title", kind: .text, options: [], old: "", value: "MUSTER SOLAR – Erstgespräch Testlauf", reason: nil, include: true),
                ProposedValue(key: "summary__value", label: "Summary", kind: .markdown, options: [], old: "", value: "## Ausgangslage\n- Festival mit über 2.000 Crew-Mitgliedern\n- Excel und WhatsApp", reason: nil, include: true),
            ]
            a.crm = CRMProposal(client: [ProposedValue(key: "field_ops__value", label: "Field ops", kind: .boolean, options: [], old: "no", value: "true",
                                                       reason: "2.000 Crew-Mitglieder vor Ort", include: true)],
                                contacts: [ContactProposal(contactID: nil, name: "Tobias Brandt", changes: [
                                    ProposedValue(key: "position__value", label: "Position", kind: .text, options: [], old: "", value: "IT, interne Tools", reason: nil, include: true)], include: true)])
            a.reviewTables = rec.tables ?? rec.state.tables ?? []
            var sp: [String: Speaker] = [:]
            let turns = rec.lines.enumerated().map { i, l in
                let id = sp[l.speaker]?.id ?? "s\(sp.count)"
                if sp[l.speaker] == nil {
                    let me = l.speaker == rec.context.myName
                    sp[l.speaker] = Speaker(id: id, label: me ? l.speaker : "Speaker \(sp.count + 1)", track: .system, embedding: nil, isMe: me)
                }
                return Turn(id: i, speaker: id, start: l.start, end: l.start + 4, text: l.text, track: .system, words: [])
            }
            a.reviewTranscript = Transcript(turns: turns, speakers: Array(sp.values), engine: "snapshot", createdAt: Date(), processingSeconds: 0)
            a.phase = .review("snapshot")
            render(ContentView(), app: app, size: CGSize(width: 1200, height: 900), to: dir.appendingPathComponent("3-review.png"), wait: 0.5)
            render(ContentView(), app: app, size: CGSize(width: 1200, height: 1600), to: dir.appendingPathComponent("3-review-long.png"), wait: 0.5)
            let person = rec.context.contacts.first?.name ?? "Speaker 2"
            render(PersonPopover(assist: a, name: person, label: "Speaker 2", onRename: { _ in }), app: app,
                   size: CGSize(width: 330, height: 420), to: dir.appendingPathComponent("7-person.png"), wait: 0.5)
            render(ImportMeetingSheet(assist: a), app: app, size: CGSize(width: 560, height: 520), to: dir.appendingPathComponent("8-import.png"), wait: 0.5)
            if !a.reviewTables.isEmpty {
                render(ReviewTablesSection(assist: a).frame(width: 1000), app: app, size: CGSize(width: 1000, height: 1250),
                       to: dir.appendingPathComponent("6-review-tables.png"), wait: 0.5)
            }
        }
        app.appMode = mode
        render(AssistSettingsView(assist: a), app: app, size: CGSize(width: 620, height: 900), to: dir.appendingPathComponent("5-settings.png"), wait: 1.5)
    }

    static func render<V: View>(_ view: V, app: AppState, size: CGSize, to url: URL, wait: TimeInterval) {
        let host = NSHostingView(rootView: view.environmentObject(app).frame(width: size.width, height: size.height)
            .ayaPage())
        let window = NSWindow(contentRect: NSRect(origin: .zero, size: size), styleMask: [.borderless], backing: .buffered, defer: false)
        window.contentView = host
        window.setFrameOrigin(NSPoint(x: -10_000, y: -10_000))
        window.orderFrontRegardless()
        RunLoop.main.run(until: Date().addingTimeInterval(wait))
        host.layoutSubtreeIfNeeded()
        guard let rep = host.bitmapImageRepForCachingDisplay(in: host.bounds) else { return }
        host.cacheDisplay(in: host.bounds, to: rep)
        try? rep.representation(using: .png, properties: [:])?.write(to: url)
        window.orderOut(nil)
    }
}
