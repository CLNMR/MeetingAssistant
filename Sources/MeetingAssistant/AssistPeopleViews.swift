import AppKit
import MeetingAssistantCore
import SwiftUI

// MARK: - Person popover

/// What Aya knows about a person, and (for speakers) a field to name them.
struct PersonPopover: View {
    @ObservedObject var assist: AssistController
    let name: String
    var contactID: String? = nil
    /// The label the transcript uses ("Speaker 2"), shown when it differs from the name.
    var label: String? = nil
    var onRename: ((String) -> Void)? = nil
    @State private var newName = ""
    @State private var loaded: AssistController.PersonCard?
    @Environment(\.dismiss) private var dismiss

    private var card: AssistController.PersonCard? {
        SpeakerNames.isGeneric(name) ? nil : (assist.personCard(name: name, contactID: contactID) ?? loaded)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 10) {
                Text(ayaInitial(card?.name ?? name))
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(card != nil ? Aya.agentForeground : Aya.primary)
                    .frame(width: 32, height: 32)
                    .background(card != nil ? Aya.agent : Aya.primary.opacity(0.12), in: Circle())
                VStack(alignment: .leading, spacing: 1) {
                    Text(card?.name ?? name).font(.system(size: 14, weight: .semibold))
                    Text(subtitle).font(.system(size: 11)).foregroundStyle(Aya.textMuted)
                }
            }
            if let card, !card.details.isEmpty {
                Grid(alignment: .leadingFirstTextBaseline, horizontalSpacing: 10, verticalSpacing: 5) {
                    ForEach(card.details, id: \.key) { d in
                        GridRow {
                            Text(d.label).foregroundStyle(Aya.textMuted).lineLimit(1)
                            Text(d.value).textSelection(.enabled).lineLimit(4).fixedSize(horizontal: false, vertical: true)
                        }
                    }
                }
                .font(.system(size: 12))
            }
            if let onRename { renameBox(onRename) }
        }
        .padding(14)
        .frame(width: 330, alignment: .leading)
        .task(id: contactID) {
            if let id = contactID, assist.personCard(name: name, contactID: id) == nil { loaded = await assist.loadContact(id) }
        }
    }

    private var subtitle: String {
        var parts: [String] = []
        if let card { parts.append(card.source) } else if !SpeakerNames.isGeneric(name) { parts.append("Not found in Aya") }
        if let label, label != name { parts.append("speaks as “\(label)”") }
        return parts.isEmpty ? "Not named yet" : parts.joined(separator: " · ")
    }

    private func renameBox(_ save: @escaping (String) -> Void) -> some View {
        let q = newName.trimmingCharacters(in: .whitespaces)
        let all = assist.nameSuggestions.filter { $0 != name }
        let hits = q.isEmpty ? Array(all.prefix(8))
            : Array(all.filter { $0.range(of: q, options: [.caseInsensitive, .diacriticInsensitive]) != nil }.prefix(8))
        return VStack(alignment: .leading, spacing: 8) {
            Divider().overlay(Aya.border)
            Text("Who is this?").font(.system(size: 12, weight: .medium)).foregroundStyle(Aya.textMuted)
            TextField("", text: $newName, prompt: Text("Name"))
                .textFieldStyle(AyaFieldStyle())
                .onSubmit { commit(q, save) }
            if !hits.isEmpty {
                FlowLayout(spacing: 6) {
                    ForEach(hits, id: \.self) { n in
                        Button(n) { commit(n, save) }.buttonStyle(AyaChipStyle())
                    }
                }
            }
            HStack {
                Spacer()
                Button("Save") { commit(q, save) }
                    .buttonStyle(AyaButtonStyle(kind: .primary, size: .small))
                    .disabled(q.isEmpty)
            }
        }
    }

    private func commit(_ n: String, _ save: (String) -> Void) {
        let t = n.trimmingCharacters(in: .whitespaces)
        guard !t.isEmpty else { return }
        save(t)
        dismiss()
    }
}

// MARK: - Meetings list

/// Assist's left column outside a meeting: a new meeting, importing a recording, and past meetings.
struct AssistMeetingsPanel: View {
    @EnvironmentObject var state: AppState
    @ObservedObject var assist: AssistController
    @State private var filter = ""
    @State private var importing = false
    @State private var confirmDelete: SessionInfo?

    private var items: [SessionInfo] {
        let all = state.sessions.filter { state.assistSessionIDs.contains($0.id) && $0.id != state.recordingSessionID }
        let f = filter.trimmingCharacters(in: .whitespaces)
        return f.isEmpty ? all : all.filter { $0.title.localizedCaseInsensitiveContains(f) }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text("Meetings").font(.system(size: 17, weight: .semibold))
                Spacer()
                Button { importing = true } label: { Label("Import Recording", systemImage: "square.and.arrow.down") }
                    .buttonStyle(AyaIconButtonStyle())
                    .help("Import a recording, link it to a meeting in Aya or create one, and get the recap")
            }
            newMeetingRow
            TextField("Search", text: $filter).textFieldStyle(AyaFieldStyle(icon: "magnifyingglass"))
            ScrollView {
                LazyVStack(spacing: 2) {
                    ForEach(items) { s in
                        SessionRow(info: s, progress: state.progress[s.id], selected: assist.reviewSessionID == s.id)
                            .onTapGesture { if assist.reviewSessionID != s.id { assist.openReview(s.id) } }
                            .contextMenu {
                                Button("Show Transcript") { state.appMode = .scribe; state.selection = .session(s.id) }
                                Button("Show in Finder") { NSWorkspace.shared.activateFileViewerSelecting([AppPaths.session(s.id)]) }
                                Divider()
                                Button("Delete…", role: .destructive) { confirmDelete = s }
                                    .disabled(AssistMeetingsPanel.isBusy(s))
                            }
                    }
                    if items.isEmpty {
                        Text(filter.isEmpty ? "Meetings with Assist appear here." : "Nothing matches.")
                            .font(.system(size: 13)).foregroundStyle(Aya.textMuted).padding(.top, 20)
                    }
                }
            }
        }
        .padding(14)
        .frame(maxHeight: .infinity, alignment: .top)
        .ayaSurface()
        .sheet(isPresented: $importing) { ImportMeetingSheet(assist: assist) }
        .deleteMeetingDialog(assist: assist, item: $confirmDelete)
    }

    static func isBusy(_ s: SessionInfo) -> Bool { [.recording, .queued, .transcribing].contains(s.status) }

    private var newMeetingRow: some View {
        let on = assist.phase == .prepare
        return Button { assist.finishReview() } label: {
            HStack(spacing: 10) {
                ZStack {
                    Circle().fill(Aya.agent).frame(width: 26, height: 26)
                    Image(systemName: "plus").font(.system(size: 12, weight: .bold)).foregroundStyle(Aya.agentForeground)
                }
                Text("New Meeting").font(.system(size: 13, weight: .semibold))
                Spacer()
            }
            .padding(8)
            .background(on ? Aya.muted : .clear, in: RoundedRectangle(cornerRadius: Aya.radiusContainer, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: Aya.radiusContainer, style: .continuous).strokeBorder(Aya.border, lineWidth: 1))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}

extension View {
    /// Asks before deleting a meeting's recording, transcript and notes.
    func deleteMeetingDialog(assist: AssistController, item: Binding<SessionInfo?>) -> some View {
        confirmationDialog("Delete “\(item.wrappedValue?.title ?? "")”?",
                           isPresented: Binding(get: { item.wrappedValue != nil }, set: { if !$0 { item.wrappedValue = nil } }),
                           titleVisibility: .visible, presenting: item.wrappedValue) { s in
            Button("Delete", role: .destructive) { assist.deleteMeeting(s.id) }
        } message: { _ in
            Text("The recording, the transcript and the notes on this Mac are deleted. What was saved in Aya stays there.")
        }
    }
}

// MARK: - Import

/// Import a recording: pick the file(s), link a meeting in Aya or describe a new one.
struct ImportMeetingSheet: View {
    @ObservedObject var assist: AssistController
    @Environment(\.dismiss) private var dismiss
    @State private var files: [URL] = []
    @State private var date = Date()
    @State private var dayMeetings: [AyaRecord] = []
    @State private var meetingID: String?
    @State private var title = ""
    @State private var twoTrackMode: MeetingMode = .call
    @State private var clientID: String?
    @State private var projectID: String?
    @State private var templateID: String?
    @State private var step: StepState = .idle

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Import Recording").font(.system(size: 18, weight: .semibold))
            Text("The recording is transcribed on this Mac. Claude then drafts the recap for the meeting in Aya.")
                .font(.system(size: 12)).foregroundStyle(Aya.textMuted)
            Grid(alignment: .leadingFirstTextBaseline, horizontalSpacing: 12, verticalSpacing: 12) {
                GridRow {
                    label("Recording")
                    VStack(alignment: .leading, spacing: 4) {
                        HStack {
                            Button(files.isEmpty ? "Choose…" : "Change…") { choose() }.buttonStyle(AyaButtonStyle(kind: .secondary, size: .small))
                            Text(files.map(\.lastPathComponent).joined(separator: " + ")).font(.system(size: 12)).lineLimit(1).truncationMode(.middle)
                        }
                        Text("One file, or two: first your microphone, then the call audio.").font(.system(size: 11)).foregroundStyle(Aya.textSubtle)
                    }
                }
                if files.count > 1 {
                    GridRow {
                        label("Mode")
                        VStack(alignment: .leading, spacing: 4) {
                            AyaSegmented(selection: $twoTrackMode, options: [(.call, MeetingMode.call.title, nil), (.hybrid, MeetingMode.hybrid.title, nil)])
                                .fixedSize()
                            Text(twoTrackMode.explanation).font(.system(size: 11)).foregroundStyle(Aya.textSubtle)
                        }
                    }
                }
                GridRow {
                    label("Date")
                    DatePicker("", selection: $date, displayedComponents: [.date, .hourAndMinute]).labelsHidden().fixedSize()
                }
                GridRow {
                    label("Meeting in Aya")
                    AyaSelect(selection: $meetingID, options: meetingOptions).frame(maxWidth: 360)
                }
                if meetingID == nil {
                    GridRow {
                        label("Title")
                        TextField("", text: $title, prompt: Text("Optional")).textFieldStyle(AyaFieldStyle()).frame(maxWidth: 360)
                    }
                    GridRow {
                        label("Client")
                        AyaSelect(selection: $clientID, options: [(nil, "None")] + assist.clients.map { ($0.id, $0.title) }).frame(maxWidth: 360)
                    }
                    GridRow {
                        label("Project")
                        AyaSelect(selection: $projectID, options: [(nil, "None")] + assist.projects.map { ($0.id, $0.title) }).frame(maxWidth: 360)
                    }
                    GridRow {
                        label("Template")
                        AyaSelect(selection: $templateID, options: [(nil, "None")] + assist.templates.map { ($0.id, $0.title) }).frame(maxWidth: 360)
                    }
                }
            }
            HStack {
                StepLine(step: step)
                Spacer()
                Button("Cancel") { dismiss() }.buttonStyle(AyaButtonStyle(kind: .secondary)).keyboardShortcut(.cancelAction)
                Button("Import") { Task { await run() } }
                    .buttonStyle(AyaButtonStyle(kind: .primary))
                    .keyboardShortcut(.defaultAction)
                    .disabled(files.isEmpty || step.isWorking)
            }
        }
        .padding(20)
        .frame(width: 560)
        .task(id: Calendar.current.startOfDay(for: date)) { await loadDay() }
        .onChange(of: meetingID) { _, id in
            // A planned meeting brings its own time.
            if let m = dayMeetings.first(where: { $0.id == id }), let d = m.date(assist.config.meetingDateField) { date = d }
        }
    }

    private var meetingOptions: [(String?, String)] {
        [(nil, "New meeting in Aya")] + dayMeetings.map { m in
            let time = m.date(assist.config.meetingDateField).map { $0.formatted(date: .omitted, time: .shortened) } ?? ""
            return (m.id, [time, m.title].filter { !$0.isEmpty }.joined(separator: " · "))
        }
    }

    private func label(_ s: String) -> some View {
        Text(s).font(.system(size: 13, weight: .medium)).gridColumnAlignment(.trailing)
    }

    private func choose() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.audio, .movie]
        panel.allowsMultipleSelection = true
        panel.message = "Choose one recording, or two files: first your microphone, then the call audio."
        guard panel.runModal() == .OK, !panel.urls.isEmpty else { return }
        files = Array(panel.urls.prefix(2))
        if let d = try? files[0].resourceValues(forKeys: [.creationDateKey]).creationDate { date = d }
    }

    private func loadDay() async {
        guard let svc = assist.service else { return }
        let list = (try? await svc.meetings(onDayOf: date)) ?? []
        dayMeetings = list
        if let id = meetingID, !list.contains(where: { $0.id == id }) { meetingID = nil }
    }

    private func run() async {
        step = .working(files.count > 0 ? "Importing…" : "")
        do {
            try await assist.importMeeting(files: files, mode: files.count > 1 ? twoTrackMode : .inPerson, date: date,
                                           title: meetingID == nil ? title : "", meetingID: meetingID,
                                           clientID: meetingID == nil ? clientID : nil,
                                           projectID: meetingID == nil ? projectID : nil,
                                           templateID: meetingID == nil ? templateID : nil)
            dismiss()
        } catch {
            step = .failed(error.localizedDescription)
        }
    }
}

// MARK: - Speakers in the review

/// The speakers of the final transcript, to name them before the recap goes to Aya.
struct ReviewSpeakersSection: View {
    @EnvironmentObject var state: AppState
    @ObservedObject var assist: AssistController
    let sessionID: String
    @State private var popover: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            VStack(alignment: .leading, spacing: 2) {
                Text("Speakers").font(.system(size: 16, weight: .semibold))
                Text("Names are used in the transcript and the recap. Click a speaker to name them.")
                    .font(.system(size: 12)).foregroundStyle(Aya.textMuted)
            }
            if let t = assist.reviewTranscript, !t.speakers.isEmpty {
                VStack(alignment: .leading, spacing: 6) {
                    ForEach(ordered(t)) { sp in row(sp, t) }
                }
            } else {
                Text(waiting).font(.system(size: 13)).foregroundStyle(Aya.textMuted)
            }
        }
        .padding(18)
        .frame(maxWidth: .infinity, alignment: .leading)
        .ayaSurface()
    }

    private var waiting: String {
        switch state.sessions.first(where: { $0.id == sessionID })?.status {
        case .transcribing: return "The transcript is being made" + (state.progress[sessionID].map { " (\(Int($0.fraction * 100)) %)" } ?? "") + ". Speakers can be named when it is ready."
        case .queued: return "Waiting to be transcribed. Speakers can be named when the transcript is ready."
        case .failed: return "Transcription failed. Try “Transcribe Again” in Scribe."
        default: return "No transcript yet."
        }
    }

    /// Speakers in the order they first speak.
    private func ordered(_ t: Transcript) -> [Speaker] {
        let first = Dictionary(t.turns.map { ($0.speaker, $0.start) }, uniquingKeysWith: min)
        return t.speakers.sorted { (first[$0.id] ?? .infinity) < (first[$1.id] ?? .infinity) }
    }

    private func row(_ sp: Speaker, _ t: Transcript) -> some View {
        let turns = t.turns.filter { $0.speaker == sp.id }
        let talk = turns.reduce(0) { $0 + ($1.end - $1.start) }
        let quote = turns.max { $0.text.count < $1.text.count }?.text ?? ""
        let generic = SpeakerNames.isGeneric(sp.label)
        return HStack(alignment: .firstTextBaseline, spacing: 10) {
            Circle().fill(speakerColor(sp.id, isMe: sp.isMe)).frame(width: 8, height: 8)
            Button { popover = sp.id } label: {
                HStack(spacing: 4) {
                    Text(sp.label).font(.system(size: 13, weight: .semibold)).foregroundStyle(generic ? Aya.textMuted : Aya.text)
                    if !sp.isMe { Image(systemName: "pencil").font(.system(size: 10)).foregroundStyle(Aya.textSubtle) }
                }
            }
            .buttonStyle(.plain)
            .pointerStyle(.link)
            .disabled(sp.isMe)
            .frame(minWidth: 140, alignment: .leading)
            .popover(isPresented: Binding(get: { popover == sp.id }, set: { if !$0 { popover = nil } }), arrowEdge: .trailing) {
                PersonPopover(assist: assist, name: sp.label) { assist.renameFinalSpeaker(sp.id, to: $0) }
            }
            Text("“\(String(quote.prefix(110)))\(quote.count > 110 ? "…" : "")”")
                .font(.system(size: 12)).foregroundStyle(Aya.textMuted).lineLimit(1)
            Spacer(minLength: 8)
            Text(Exporter.clock(talk)).font(.system(size: 11).monospacedDigit()).foregroundStyle(Aya.textSubtle)
        }
    }
}
