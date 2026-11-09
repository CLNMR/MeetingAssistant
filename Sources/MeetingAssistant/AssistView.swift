import MeetingAssistantCore
import SwiftUI

/// Assist mode: prepare a meeting, run the live assistant, review and upload afterwards.
struct AssistHomeView: View {
    @EnvironmentObject var state: AppState

    var body: some View {
        AssistRouter(assist: state.assist)
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
private struct Highlight: ViewModifier {
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
                Text("Pick a client and a playbook from Aya, start the meeting, and Claude suggests the next question, keeps a running summary, and tracks attendees, open points and the playbook while you talk. Afterwards it writes the recap into Aya.")
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
                Text("The lists (clients, contacts, playbooks, meeting recaps) can be changed later in Settings › Assist.")
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
        VStack(alignment: .leading, spacing: 14) {
            PageHeader(eyebrow: "Assist", title: "Prepare meeting",
                       subtitle: "Choose the client and playbook; Claude reads them before the meeting starts.") {
                HStack(spacing: 8) {
                    if let d = assist.listsFetchedAt {
                        Text("Aya lists updated \(d.formatted(.relative(presentation: .named)))").font(.system(size: 11)).foregroundStyle(Aya.textSubtle)
                    }
                    Button { Task { await assist.refreshLists() } } label: { Label("Refresh", systemImage: "arrow.clockwise") }
                        .buttonStyle(AyaButtonStyle(kind: .secondary))
                        .help("Reload clients and playbooks from Aya")
                        .disabled(assist.listState.isWorking)
                }
            }
            HStack(alignment: .top, spacing: 8) {
                clientColumn.frame(minWidth: 260, idealWidth: 320, maxWidth: 360)
                VStack(alignment: .leading, spacing: 8) {
                    HStack(alignment: .top, spacing: 8) {
                        playbookPicker
                        meetingSettings
                    }
                    .fixedSize(horizontal: false, vertical: true)
                    previewBox
                }
                .frame(minWidth: 360)
            }
            footer
        }
        .padding(20)
        .background(AyaBackdrop())
    }

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
                    ForEach(filteredClients, id: \.id) { c in clientRow(c) }
                }
            }
            StepLine(step: assist.listState)
        }
        .padding(14)
        .frame(maxHeight: .infinity, alignment: .top)
        .ayaSurface()
    }

    private func clientRow(_ c: AyaRecord) -> some View {
        let on = assist.clientID == c.id
        let sub = [c["branche__value"]?.description, c["domain__value"]?.description, c["region__value"]?.description]
            .compactMap { $0 }.filter { !$0.isEmpty }.joined(separator: " · ")
        return Button { assist.clientID = c.id } label: {
            HStack(spacing: 10) {
                Text(ayaInitial(c.title))
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundStyle(on ? .white : Aya.primary)
                    .frame(width: 28, height: 28)
                    .background(on ? AnyShapeStyle(Aya.brand) : AnyShapeStyle(Aya.primary.opacity(0.10)),
                                in: RoundedRectangle(cornerRadius: Aya.radiusControl, style: .continuous))
                VStack(alignment: .leading, spacing: 1) {
                    Text(c.title).font(.system(size: 13, weight: on ? .semibold : .medium)).lineLimit(1)
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

    private var playbookPicker: some View {
        VStack(alignment: .leading, spacing: 10) {
            AyaEyebrow(text: "Playbook", icon: "checklist")
            AyaSelect(selection: $assist.playbookID,
                      options: [(String?.none, "None")] + assist.playbooks.map { (String?.some($0.id), $0.title) })
            if let pid = assist.playbookID, let p = assist.playbooks.first(where: { $0.id == pid }),
               let goal = p["goal__value"]?.string, !goal.isEmpty {
                Text(goal).font(.system(size: 12)).foregroundStyle(Aya.textMuted).lineLimit(3)
            }
            Spacer(minLength: 0)
        }
        .padding(14)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .ayaSurface()
    }

    private var meetingSettings: some View {
        VStack(alignment: .leading, spacing: 10) {
            AyaEyebrow(text: "Meeting", icon: "person.2")
            AyaSegmented(selection: $assist.mode, options: MeetingMode.allCases.map { ($0, $0.title, nil) })
            TextField("", text: $assist.goal, prompt: Text("Goal for this meeting (optional)"), axis: .vertical)
                .lineLimit(1...3).textFieldStyle(AyaFieldStyle(icon: "target"))
        }
        .padding(14)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .ayaSurface()
    }

    private var previewBox: some View {
        VStack(alignment: .leading, spacing: 10) {
            AyaEyebrow(text: "What Claude will know", icon: "sparkles", color: Aya.agentTextMid)
            if assist.clientID == nil {
                Text("Select a client to see what Claude will know about it.").font(.system(size: 13)).foregroundStyle(Aya.textMuted)
            } else if let p = assist.preview {
                Text(p.client.title).font(.system(size: 18, weight: .semibold))
                ScrollView {
                    VStack(alignment: .leading, spacing: 12) {
                        if let profile = p.client.values.first(where: { $0.key.contains("profil") })?.value.string, !profile.isEmpty {
                            Text(profile).font(.system(size: 13)).foregroundStyle(Aya.text).lineLimit(6)
                        }
                        if !p.contacts.isEmpty {
                            VStack(alignment: .leading, spacing: 6) {
                                Text("Contacts").font(.system(size: 12, weight: .semibold)).foregroundStyle(Aya.textMuted)
                                FlowChips(items: p.contacts.map { c in (c.title, c["position__value"]?.string) })
                            }
                        }
                        VStack(alignment: .leading, spacing: 4) {
                            Text("Earlier meetings").font(.system(size: 12, weight: .semibold)).foregroundStyle(Aya.textMuted)
                            if p.recaps.isEmpty { Text("None found").font(.system(size: 13)).foregroundStyle(Aya.textSubtle) }
                            ForEach(p.recaps, id: \.id) { r in
                                let d = String((r[assist.config.recapDateField]?.string ?? r.values["createdAt"]?.string ?? "").prefix(10))
                                HStack(spacing: 8) {
                                    Text(d).font(.system(size: 11).monospacedDigit()).foregroundStyle(Aya.textSubtle)
                                    Text(r.title).font(.system(size: 13)).lineLimit(1)
                                }
                            }
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
            }
            StepLine(step: assist.previewState)
        }
        .padding(14)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .ayaAgentSurface(active: assist.preview != nil)
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

    var body: some View {
        VStack(spacing: 8) {
            statusBar
            questionCard
            HStack(spacing: 8) {
                summaryBox.frame(minWidth: 220)
                openPointsBox.frame(minWidth: 200)
                if !assist.state.playbook.isEmpty { playbookBox.frame(minWidth: 200) }
            }
            .frame(minHeight: 200)
            attendeesRow
            transcriptStrip
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
                Text(assist.context?.clientTitle ?? "").font(.system(size: 14, weight: .semibold)).lineLimit(1)
                if let p = assist.context?.playbookTitle { Text(p).font(.system(size: 11)).foregroundStyle(Aya.textMuted).lineLimit(1) }
            }
            Spacer()
            statusText
            AyaTag(text: "\(assist.state.updates) updates", color: Aya.agentTextMid)
                .help(String(format: "Each update uses some of your Claude subscription (API equivalent so far: $%.2f).", assist.state.costUSD))
            Button { assist.togglePause() } label: { Label(assist.paused ? "Resume" : "Pause", systemImage: assist.paused ? "play.fill" : "pause.fill") }
                .buttonStyle(AyaButtonStyle(kind: .secondary, pill: true))
                .help("Pause the assistant; recording and transcript continue.")
            Button { Task { await assist.stop() } } label: { Label("Stop", systemImage: "stop.fill") }
                .buttonStyle(AyaButtonStyle(kind: .destructive, pill: true))
        }
        .padding(.horizontal, 14).padding(.vertical, 10)
        .ayaAgentSurface(active: !assist.paused)
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

    private var summaryBox: some View {
        PanelBox(title: "Summary", icon: "text.alignleft", trailing: panelCount("\(assist.state.summary.count)")) {
            ScrollViewReader { proxy in
                ScrollView {
                    VStack(alignment: .leading, spacing: 6) {
                        ForEach(Array(assist.state.summary.enumerated()), id: \.offset) { i, b in
                            HStack(alignment: .firstTextBaseline, spacing: 8) {
                                Circle().fill(Aya.agent).frame(width: 5, height: 5).offset(y: -2)
                                Text(b).font(.system(size: 13)).textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading)
                            }
                            .modifier(Highlight(on: assist.state.changed.contains("s\(i)"))).id(i)
                        }
                    }
                }
                .onChange(of: assist.state.summary.count) { _, n in withAnimation { proxy.scrollTo(n - 1, anchor: .bottom) } }
            }
        }
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
        return PanelBox(title: "Playbook", icon: "checklist", trailing: panelCount("\(covered)/\(items.count)")) {
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
        .help(a.notes ?? "")
        .modifier(Highlight(on: assist.state.changed.contains("a:\(a.id)")))
    }

    private var transcriptStrip: some View {
        VStack(alignment: .leading, spacing: 3) {
            ForEach(state.liveLines.suffix(3)) { l in
                let who = l.track == .mic && state.recordingMode == .call ? (state.prefs.myName.isEmpty ? "Me" : state.prefs.myName) : "Them"
                (Text(who + "  ").fontWeight(.semibold).foregroundColor(l.track == .mic ? Aya.primary : Aya.agentTextMid)
                 + Text(l.text).foregroundColor(l.isFinal ? Aya.textMuted : Aya.textSubtle))
                    .font(.system(size: 12)).lineLimit(1)
            }
        }
        .padding(.horizontal, 14).padding(.vertical, 8)
        .frame(maxWidth: .infinity, minHeight: 56, alignment: .topLeading)
        .ayaSurface(fill: Aya.card.opacity(0.7), shadow: false)
    }
}

// MARK: - Review

private struct AssistReviewView: View {
    @EnvironmentObject var state: AppState
    @ObservedObject var assist: AssistController
    let sessionID: String

    var body: some View {
        VStack(spacing: 14) {
            PageHeader(eyebrow: "Meeting review", title: assist.record?.context.clientTitle ?? "Meeting",
                       subtitle: [assist.record?.context.playbookTitle, assist.record.map { $0.context.date.formatted(date: .abbreviated, time: .shortened) }]
                        .compactMap { $0 }.joined(separator: " · ")) {
                HStack(spacing: 8) {
                    Button { state.appMode = .scribe; state.selection = .session(sessionID) } label: { Label("Show Transcript", systemImage: "text.bubble") }
                        .buttonStyle(AyaButtonStyle(kind: .secondary))
                    Button("Done") { assist.finishReview() }
                        .buttonStyle(AyaButtonStyle(kind: .primary))
                        .keyboardShortcut(.defaultAction)
                }
            }
            ScrollView {
                VStack(alignment: .leading, spacing: 12) {
                    recapSection
                    crmSection
                }
                .frame(maxWidth: 920, alignment: .leading)
                .frame(maxWidth: .infinity)
                .padding(.bottom, 12)
            }
        }
        .padding(20)
        .background(AyaBackdrop())
    }

    // Recap

    private var recapSection: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .center) {
                VStack(alignment: .leading, spacing: 2) {
                    Text("Meeting recap").font(.system(size: 16, weight: .semibold))
                    if let ref = assist.config.list(.recaps) { Text("→ \(ref.label) in \(ref.workspaceLabel)").font(.system(size: 12)).foregroundStyle(Aya.textMuted) }
                }
                Spacer()
                if let id = assist.record?.recapRecordID, !id.isEmpty { AyaTag(text: "In Aya", color: Aya.success, icon: "checkmark") }
                Button { Task { await assist.draftRecap() } } label: {
                    Label(assist.recapFields.isEmpty ? "Draft Recap" : "Draft Again", systemImage: "sparkles")
                }
                .buttonStyle(AyaButtonStyle(kind: assist.recapFields.isEmpty ? .agent : .secondary, pill: true))
                .disabled(assist.recapState.isWorking)
            }
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
                        Label(assist.record?.recapRecordID == nil ? "Upload to Aya" : "Update in Aya", systemImage: "arrow.up.circle")
                    }
                    .buttonStyle(AyaButtonStyle(kind: .primary)).disabled(assist.recapState.isWorking)
                }
            }
        }
        .padding(18)
        .ayaSurface()
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
                Button { Task { await assist.proposeCRM() } } label: {
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
            Toggle(isOn: $field.include) { Text(field.label).font(.system(size: 13, weight: .semibold)) }.toggleStyle(.checkbox)
            if field.include { editor(for: $field).padding(.leading, 20) }
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
        a.claudeState = .done("ok"); a.claudeBinary = URL(fileURLWithPath: "/usr/bin/true"); a.claudeAuth = .init(loggedIn: true, method: nil, subscription: "team", email: nil)
        render(ContentView(), app: app, size: CGSize(width: 1280, height: 800), to: dir.appendingPathComponent("1-prepare.png"), wait: 6)

        // 2. Live
        if let rec {
            a.context = rec.context
            var s = rec.state
            s.changed = Set(["s\(s.summary.count - 1)", s.questions.first?.id ?? ""])
            a.state = s
            a.status = .listening
            app.elapsed = 1342
            a.phase = .running
            render(ContentView(), app: app, size: CGSize(width: 1280, height: 860), to: dir.appendingPathComponent("2-live.png"), wait: 0.5)
            render(AssistBanner(assist: a).frame(width: 420), app: app, size: CGSize(width: 420, height: 90), to: dir.appendingPathComponent("4-banner.png"), wait: 0.3)

            // 3. Review
            a.record = rec
            a.recapFields = [
                ProposedValue(key: "title__value", label: "Title", kind: .text, options: [], old: "", value: "MUSTER SOLAR – Erstgespräch Testlauf", reason: nil, include: true),
                ProposedValue(key: "meeting_type__value", label: "Meeting type", kind: .select, options: ["customer", "internal"], old: "", value: "customer", reason: nil, include: true),
                ProposedValue(key: "summary__value", label: "Summary", kind: .markdown, options: [], old: "", value: "## Ausgangslage\n- Festival mit über 2.000 Crew-Mitgliedern\n- Excel und WhatsApp", reason: nil, include: true),
            ]
            a.crm = CRMProposal(client: [ProposedValue(key: "field_ops__value", label: "Field ops", kind: .boolean, options: [], old: "no", value: "true",
                                                       reason: "2.000 Crew-Mitglieder vor Ort", include: true)],
                                contacts: [ContactProposal(contactID: nil, name: "Tobias Brandt", changes: [
                                    ProposedValue(key: "position__value", label: "Position", kind: .text, options: [], old: "", value: "IT, interne Tools", reason: nil, include: true)], include: true)])
            a.phase = .review("snapshot")
            render(ContentView(), app: app, size: CGSize(width: 1200, height: 900), to: dir.appendingPathComponent("3-review.png"), wait: 0.5)
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
