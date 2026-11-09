import AppKit
import MeetingAssistantCore
import SwiftUI

/// Progress of one step (Aya call, Claude call) for the UI.
enum StepState: Equatable {
    case idle
    case working(String)
    case done(String?)
    case failed(String)

    var isWorking: Bool { if case .working = self { return true }; return false }
}

/// Assist mode: the Aya connection, meeting preparation, the live assistant and the review afterwards.
@MainActor
final class AssistController: ObservableObject {
    enum Phase: Equatable { case prepare, running, review(String) }

    unowned let app: AppState

    // Settings
    @Published var config = AyaConfig.load() { didSet { if config != oldValue { config.save() } } }
    @Published var settings = AssistSettings.load() { didSet { if settings != oldValue { settings.save() } } }
    @Published var hasAPIKey = AyaConfig.apiKey != nil
    @Published var schemas = AyaCache.schemas()
    @Published var connectState: StepState = .idle
    @Published var workspaces: [AyaWorkspace] = []
    @Published var specs: [AyaRecordSpec] = []

    // Pickers
    @Published var clients: [AyaRecord] = AyaCache.list(.clients)?.records ?? []
    @Published var projects: [AyaRecord] = AyaCache.list(.projects)?.records ?? []
    @Published var templates: [AyaRecord] = AyaCache.list(.templates)?.records ?? []
    @Published var members: [AyaRecord] = AyaCache.list(.members)?.records ?? []
    @Published var upcoming: [AyaRecord] = []
    @Published var listsFetchedAt: Date? = AyaCache.list(.clients)?.fetchedAt
    @Published var listState: StepState = .idle

    // Preparation: a planned meeting from Aya, or template / client / project chosen freely.
    @Published var meetingID: String? { didSet { if meetingID != oldValue { meetingChosen() } } }
    @Published var clientID: String? { didSet { if clientID != oldValue { loadPreview() } } }
    @Published var projectID: String? { didSet { if projectID != oldValue { loadPreview() } } }
    @Published var templateID: String? { didSet { if templateID != oldValue { loadPreview() } } }
    @Published var goal = ""
    @Published var mode: MeetingMode = .call
    @Published var preview: AyaMeetingContext?
    @Published var previewState: StepState = .idle
    private var previewTask: Task<Void, Never>?

    // Claude
    @Published var claudeBinary: URL?
    @Published var claudeAuth: ClaudeCLI.AuthStatus?
    @Published var claudeState: StepState = .idle

    // Meeting
    @Published var phase: Phase = .prepare
    @Published var state = AssistState()
    @Published var status: AssistStatus = .idle
    @Published var paused = false
    @Published var startState: StepState = .idle
    var context: AssistContext?
    private var engine: AssistEngine?
    private var sessionID: String?
    private let sequence = PublishSequence()

    // Review
    @Published var record: AssistRecord?
    /// Where the recap goes: an existing Aya meeting, or nil for a new one.
    @Published var targetMeetingID: String?
    @Published var targetRecord: AyaRecord?
    /// Meetings in Aya on the recording's day, to attach the recap to.
    @Published var dayMeetings: [AyaRecord] = []
    @Published var recapFields: [ProposedValue] = []
    @Published var recapState: StepState = .idle
    @Published var crm: CRMProposal?
    @Published var crmState: StepState = .idle
    private var waitTask: Task<Void, Never>?

    init(app: AppState) {
        self.app = app
        guard !config.orgId.isEmpty, hasAPIKey else { return }
        if !config.isComplete || templates.isEmpty {
            // First start after meeting templates were introduced: pick the new lists.
            Task { await upgradeLists() }
        } else {
            Task { await refreshLists() }
        }
    }

    /// Fills in lists that are not chosen yet (and follows lists that moved workspace).
    func upgradeLists() async {
        guard let svc = service else { return }
        connectState = .working("Updating the Aya lists…")
        do {
            config = try await svc.autoSelectLists()
            schemas = try await AyaService(config: config, client: svc.client).refreshSchemas()
            await refreshLists()
            connectState = .done(nil)
        } catch {
            connectState = .failed(error.localizedDescription)
        }
    }

    var isConnected: Bool { hasAPIKey && config.isComplete }
    /// The organization in the Aya web app, once an organization id is set.
    var orgURL: URL? {
        let id = config.orgId.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !id.isEmpty, let enc = id.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) else { return nil }
        return URL(string: "https://app.hey-aya.ai/org/\(enc)")
    }
    var service: AyaService? { AyaService(config: config) }

    // MARK: - Connection (Settings)

    func connect(orgId: String, apiKey: String) async {
        connectState = .working("Connecting to Aya…")
        let key = apiKey.trimmingCharacters(in: .whitespacesAndNewlines)
        if !key.isEmpty { AyaConfig.setAPIKey(key) }
        hasAPIKey = AyaConfig.apiKey != nil
        var cfg = config
        cfg.orgId = orgId.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let client = cfg.client() else { connectState = .failed("Enter the organization id and the API key."); return }
        do {
            _ = try await client.me()
            cfg = try await AyaService(config: cfg, client: client).autoSelectLists()
            config = cfg
            connectState = .working("Reading the lists' fields…")
            schemas = try await AyaService(config: cfg, client: client).refreshSchemas()
            connectState = .working("Loading clients, projects and templates…")
            await refreshLists()
            await loadCatalog()
            connectState = .done("Connected. \(clients.count) clients, \(projects.count) projects, \(templates.count) templates.")
        } catch {
            connectState = .failed(error.localizedDescription)
        }
    }

    func disconnect() {
        AyaConfig.setAPIKey(nil)
        hasAPIKey = false
        AyaCache.clear()
        clients = []; projects = []; templates = []; members = []; upcoming = []; schemas = [:]
        connectState = .idle
    }

    /// Workspaces and record types for the list pickers in Settings.
    func loadCatalog() async {
        guard let svc = service else { return }
        do {
            workspaces = try await svc.client.workspaces()
            if let first = workspaces.first {
                specs = try await svc.client.recordSpecs(workspace: first.id)
                    .filter { $0.isSystem != true }
                    .sorted { $0.label.localizedCaseInsensitiveCompare($1.label) == .orderedAscending }
            }
        } catch { connectState = .failed(error.localizedDescription) }
    }

    func workspaceLabel(_ id: String?) -> String { workspaces.first { $0.id == id }?.label ?? "" }

    /// Changes the list for a slot and reloads what depends on it.
    func setList(_ slot: AyaSlot, spec: AyaRecordSpec) async {
        let ws = workspaces.first { $0.id == spec.workspaceId } ?? workspaces.first
        guard let ws else { return }
        config.setList(slot, AyaListRef(workspaceId: ws.id, workspaceLabel: ws.label, techName: spec.techName, label: spec.label))
        if slot == .contacts { config.contactsClientField = nil }
        await refreshSchemas()
        if slot == .contacts, let link = schemas[AyaSlot.contacts.rawValue]?.relations.first(where: { $0.kind == .relation && $0.name.contains("firm") }) {
            config.contactsClientField = link.key
        }
        if slot != .contacts { await refreshLists() }
    }

    func refreshSchemas() async {
        guard let svc = service else { return }
        connectState = .working("Reading the lists' fields…")
        do { schemas = try await svc.refreshSchemas(); connectState = .done("Fields updated.") }
        catch { connectState = .failed(error.localizedDescription) }
    }

    func refreshLists() async {
        guard let svc = service else { return }
        listState = .working("Loading from Aya…")
        do {
            clients = try await svc.refreshList(.clients)
            templates = try await svc.refreshList(.templates)
            if config.list(.projects) != nil { projects = try await svc.refreshList(.projects) }
            if config.list(.members) != nil { members = (try? await svc.refreshList(.members)) ?? members }
            upcoming = try await svc.upcomingMeetings()
            listsFetchedAt = Date()
            listState = .done(nil)
            if let t = templateID, !templates.contains(where: { $0.id == t }) { templateID = nil }
            if let m = meetingID, !upcoming.contains(where: { $0.id == m }) { meetingID = nil }
        } catch {
            listState = .failed(error.localizedDescription)
        }
    }

    /// Just the planned meetings (cheap; done each time the preparation screen opens).
    func refreshUpcoming() async {
        guard let svc = service, config.list(.meetings) != nil else { return }
        if let u = try? await svc.upcomingMeetings() { upcoming = u }
    }

    /// Server-side search, for clients not in the cached list.
    func searchClients(_ text: String) async -> [AyaRecord] {
        guard let svc = service, text.count >= 2 else { return [] }
        return (try? await svc.search(.clients, text: text)) ?? []
    }

    // MARK: - Claude

    func checkClaude() async {
        claudeState = .working("Checking Claude Code…")
        guard let b = ClaudeCLI.locate(preferred: settings.claudePath) else {
            claudeBinary = nil; claudeAuth = nil
            claudeState = .failed(ClaudeCLI.CLIError.notInstalled.errorDescription!)
            return
        }
        claudeBinary = b
        claudeAuth = await ClaudeCLI.authStatus(binary: b)
        if claudeAuth?.loggedIn == true {
            claudeState = .done("Claude Code logged in\(claudeAuth?.subscription.map { " (\($0) plan)" } ?? "")")
        } else {
            claudeState = .failed(ClaudeCLI.CLIError.notLoggedIn.errorDescription!)
        }
    }

    var claudeReady: Bool { claudeBinary != nil && claudeAuth?.loggedIn == true }

    // MARK: - Preparation

    var plannedMeeting: AyaRecord? { meetingID.flatMap { id in upcoming.first { $0.id == id } } }

    /// Picking a planned meeting takes over its client, project and template.
    private func meetingChosen() {
        if let m = plannedMeeting {
            clientID = config.meetingClientField.flatMap { m.linkedID($0) }
            projectID = config.meetingProjectField.flatMap { m.linkedID($0) }
            templateID = config.meetingTemplateField.flatMap { m.linkedID($0) }
        }
        loadPreview()
    }

    private var selectionKey: [String?] { [meetingID, clientID, projectID, templateID] }

    private func loadPreview() {
        previewTask?.cancel()
        let key = selectionKey
        guard key.contains(where: { $0 != nil }), let svc = service else { preview = nil; previewState = .idle; return }
        previewState = .working("Loading from Aya…")
        previewTask = Task {
            try? await Task.sleep(nanoseconds: 300_000_000)
            guard !Task.isCancelled else { return }
            do {
                let ctx = try await svc.meetingContext(meetingID: key[0], clientID: key[1], projectID: key[2], templateID: key[3])
                guard !Task.isCancelled, selectionKey == key else { return }
                preview = ctx
                previewState = .done(nil)
            } catch {
                guard !Task.isCancelled else { return }
                previewState = .failed(error.localizedDescription)
            }
        }
    }

    var canStart: Bool { claudeReady && !app.isRecording && phase == .prepare && !startState.isWorking && !previewState.isWorking }

    func start() async {
        guard let svc = service else { return }
        guard let binary = claudeBinary, claudeReady else { startState = .failed("Claude Code is not ready."); return }
        startState = .working("Loading the meeting context from Aya…")
        do {
            let aya: AyaMeetingContext
            if let p = preview, selectionKey.contains(where: { $0 != nil }) { aya = p }
            else if selectionKey.contains(where: { $0 != nil }) {
                aya = try await svc.meetingContext(meetingID: meetingID, clientID: clientID, projectID: projectID, templateID: templateID)
            } else { aya = AyaMeetingContext() }
            let me = app.prefs.myName.trimmingCharacters(in: .whitespaces)
            let ctx = AssistContext(aya: aya, config: config, goal: goal, mode: mode, myName: me.isEmpty ? "Me" : me, date: Date())
            context = ctx

            startState = .working("Starting the recording…")
            app.recordingMode = mode
            app.recordingTitle = ctx.title
            app.forceLive = true
            app.liveLinesHook = { [weak self] lines in self?.ingest(lines) }
            app.onRecordingStopped = { [weak self] id in self?.recordingStopped(id) }
            await app.startRecording()
            guard app.isRecording, let sid = app.recordingSessionID else {
                app.forceLive = false; app.liveLinesHook = nil; app.onRecordingStopped = nil
                startState = .failed(app.errorMessage ?? "The recording did not start.")
                app.errorMessage = nil
                return
            }
            sessionID = sid
            try? AssistRecord(context: ctx, state: AssistState(), lines: []).save(sid)

            state = AssistState()
            state.playbook = ctx.playbookItems
            status = .starting
            paused = false
            phase = .running
            startState = .idle
            let seq = sequence
            let e = AssistEngine(context: ctx, settings: settings, binary: binary) { [weak self] s, st in
                let n = seq.next()
                Task { @MainActor in
                    guard let self, seq.isLatest(n) else { return }
                    self.state = s; self.status = st
                }
            }
            engine = e
            await e.start()
        } catch {
            startState = .failed(error.localizedDescription)
        }
    }

    private func ingest(_ lines: [LiveLine]) {
        guard let e = engine else { return }
        Task { await e.ingest(lines) }
    }

    // MARK: - During the meeting

    func markAsked(_ id: String) { Task { await engine?.markAsked(id) } }
    func dismiss(_ id: String) { Task { await engine?.dismiss(id) } }
    func promote(_ id: String) { Task { await engine?.promote(id) } }
    func suggestNow() { Task { await engine?.suggestNow() } }
    func setOpenPoint(_ id: String, _ s: AssistState.OpenPoint.Status) { Task { await engine?.setOpenPoint(id, s) } }
    func setPlaybook(_ id: String, _ s: PlaybookItem.Status) { Task { await engine?.setPlaybook(id, s) } }
    func togglePause() {
        paused.toggle()
        let p = paused
        Task { await engine?.setPaused(p) }
    }

    func stop() async { await app.stopRecording() }

    private func recordingStopped(_ id: String) {
        app.onRecordingStopped = nil
        let e = engine
        engine = nil
        Task {
            let final = await e?.stop() ?? state
            let lines = await e?.transcript ?? []
            guard !id.isEmpty, let ctx = context else { phase = .prepare; return }
            state = final
            let rec = AssistRecord(context: ctx, state: final, lines: lines)
            try? rec.save(id)
            openReview(id)
        }
    }

    // MARK: - Review

    func openReview(_ id: String) {
        waitTask?.cancel()
        guard let rec = AssistRecord.load(id) else { return }
        record = rec
        state = rec.state
        context = rec.context
        crm = nil; crmState = .idle
        recapFields = []
        recapState = rec.recapRecordID != nil ? .done("Saved in Aya.") : .idle
        dayMeetings = []
        targetRecord = nil
        targetMeetingID = rec.recapRecordID ?? rec.context.meetingID
        phase = .review(id)
        app.appMode = .assist
        loadTargets(id)
        waitForTranscriptThenDraft(id)
    }

    /// The meetings in Aya on the recording's day, and the current target meeting.
    private func loadTargets(_ id: String) {
        guard let svc = service else { return }
        let day = app.sessions.first { $0.id == id }?.startedAt ?? record?.context.date ?? Date()
        Task {
            dayMeetings = (try? await svc.meetings(onDayOf: day)) ?? []
            await loadTargetRecord()
        }
    }

    private func loadTargetRecord() async {
        guard let id = targetMeetingID else { targetRecord = nil; return }
        var r = dayMeetings.first { $0.id == id }
        if r == nil { r = try? await service?.record(.meetings, id: id) }
        guard targetMeetingID == id else { return }
        targetRecord = r
        if !recapFields.isEmpty { recapFields = RecapBuilder.target(recapFields, existing: r) }
    }

    /// Choose where the recap is saved: an existing meeting or a new one.
    func setTarget(_ id: String?) {
        targetMeetingID = id
        if id == nil { targetRecord = nil; recapFields = RecapBuilder.target(recapFields, existing: nil); return }
        Task { await loadTargetRecord() }
    }

    /// Choices for the target, the planned meeting and the one already written first.
    var targetChoices: [AyaRecord] {
        var out: [AyaRecord] = []
        for r in [targetRecord].compactMap({ $0 }) + dayMeetings where !out.contains(where: { $0.id == r.id }) { out.append(r) }
        return out
    }

    /// The recap uses the final transcript, so wait for transcription first (falls back to the live one).
    private func waitForTranscriptThenDraft(_ id: String) {
        waitTask = Task {
            while !Task.isCancelled {
                let s = app.sessions.first { $0.id == id }?.status
                if s == .done || s == .failed || s == .recorded || s == nil { break }
                recapState = .working("Waiting for the final transcript… \(app.progress[id].map { "\(Int($0.fraction * 100)) %" } ?? "")")
                try? await Task.sleep(nanoseconds: 1_000_000_000)
            }
            guard !Task.isCancelled, recapFields.isEmpty else { return }
            if record?.recapRecordID == nil { await draftRecap() } else { recapState = .done("Already uploaded to Aya. Draft again to change it.") }
        }
    }

    var reviewSessionID: String? { if case .review(let id) = phase { return id }; return nil }

    func draftRecap() async {
        guard let id = reviewSessionID, let rec = record else { return }
        guard let schema = schemas[AyaSlot.meetings.rawValue] else { recapState = .failed("Connect Aya in Settings › Assist first."); return }
        if claudeBinary == nil { await checkClaude() }
        guard let b = claudeBinary else { recapState = .failed(ClaudeCLI.CLIError.notInstalled.errorDescription!); return }
        recapState = .working("Claude is writing the recap…")
        do {
            if targetMeetingID != nil, targetRecord == nil { await loadTargetRecord() }
            recapFields = try await RecapBuilder.draftRecap(binary: b, model: settings.model, record: rec, sessionID: id, schema: schema,
                                                            config: config, existing: targetRecord)
            recapState = .idle
        } catch {
            recapState = .failed(error.localizedDescription)
        }
    }

    func uploadRecap() async {
        guard let id = reviewSessionID, var rec = record, let svc = service, let ref = config.list(.meetings),
              let schema = schemas[AyaSlot.meetings.rawValue] else { return }
        let started = app.sessions.first { $0.id == id }?.startedAt ?? rec.context.date
        let people = [rec.context.myName] + rec.state.attendees.filter { $0.present != false }.map(\.name)
        let memberIDs = RecapBuilder.memberIDs(attendees: people, members: members)
        recapState = .working("Saving in Aya…")
        do {
            if let target = targetMeetingID {
                let current = try await svc.record(.meetings, id: target)
                let values = RecapBuilder.recapValues(recapFields, record: rec, sessionID: id, startedAt: started, schema: schema,
                                                      config: config, existing: current, memberIDs: memberIDs)
                targetRecord = try await svc.client.update(workspace: ref.workspaceId, list: ref.techName, id: target, values: values)
                rec.recapRecordID = target
            } else {
                let values = RecapBuilder.recapValues(recapFields, record: rec, sessionID: id, startedAt: started, schema: schema,
                                                      config: config, memberIDs: memberIDs)
                let created = try await svc.client.create(workspace: ref.workspaceId, list: ref.techName, values: values)
                rec.recapRecordID = created.id
                targetMeetingID = created.id
                targetRecord = created
            }
            rec.recapUploadedAt = Date()
            try? rec.save(id)
            record = rec
            recapState = .done("Saved in Aya: “\(targetRecord?.title ?? "meeting")” (\(ref.label)).")
        } catch {
            recapState = .failed(error.localizedDescription)
        }
    }

    func proposeCRM() async {
        guard let id = reviewSessionID, let rec = record, let svc = service,
              let cRef = config.list(.clients), let cs = schemas[AyaSlot.clients.rawValue] else { return }
        guard let clientID = rec.context.clientID else { crmState = .done("This meeting has no client."); return }
        if claudeBinary == nil { await checkClaude() }
        guard let b = claudeBinary else { crmState = .failed(ClaudeCLI.CLIError.notInstalled.errorDescription!); return }
        crmState = .working("Claude is reading the CRM records and the transcript…")
        do {
            let client = try await svc.client.record(workspace: cRef.workspaceId, list: cRef.techName, id: clientID)
            var contacts: [AyaRecord] = []
            if let pRef = config.list(.contacts) {
                for c in rec.context.contacts {
                    if let r = try? await svc.client.record(workspace: pRef.workspaceId, list: pRef.techName, id: c.id) { contacts.append(r) }
                }
            }
            crm = try await RecapBuilder.proposeCRM(binary: b, model: settings.model, record: rec, sessionID: id, client: client,
                                                    contacts: contacts, clientSchema: cs, contactSchema: schemas[AyaSlot.contacts.rawValue])
            crmState = (crm?.client.isEmpty ?? true) && (crm?.contacts.isEmpty ?? true) ? .done("No changes proposed.") : .idle
        } catch {
            crmState = .failed(error.localizedDescription)
        }
    }

    func applyCRM() async {
        guard let id = reviewSessionID, var rec = record, let p = crm, let svc = service, let cRef = config.list(.clients),
              let clientID = rec.context.clientID else { return }
        crmState = .working("Writing to Aya…")
        var done: [String] = []
        do {
            if let cs = schemas[AyaSlot.clients.rawValue] {
                var values: [String: JSONValue] = [:]
                for c in p.client where c.include { if let v = cs.coerce(.string(c.value), for: c.key) { values[c.key] = v } }
                if !values.isEmpty {
                    _ = try await svc.client.update(workspace: cRef.workspaceId, list: cRef.techName, id: clientID, values: values)
                    done.append("\(values.count) client field\(values.count == 1 ? "" : "s")")
                }
            }
            if let pRef = config.list(.contacts), let ps = schemas[AyaSlot.contacts.rawValue] {
                var updated = 0, created = 0
                for c in p.contacts where c.include {
                    var values: [String: JSONValue] = [:]
                    for ch in c.changes where ch.include { if let v = ps.coerce(.string(ch.value), for: ch.key) { values[ch.key] = v } }
                    guard !values.isEmpty else { continue }
                    if let cid = c.contactID {
                        _ = try await svc.client.update(workspace: pRef.workspaceId, list: pRef.techName, id: cid, values: values)
                        updated += 1
                    } else {
                        if let link = config.contactsClientField { values[link] = .string(clientID) }
                        let r = try await svc.client.create(workspace: pRef.workspaceId, list: pRef.techName, values: values)
                        rec.createdContactIDs.append(r.id)
                        created += 1
                    }
                }
                if updated > 0 { done.append("\(updated) contact\(updated == 1 ? "" : "s") updated") }
                if created > 0 { done.append("\(created) contact\(created == 1 ? "" : "s") created") }
            }
            rec.crmAppliedAt = Date()
            try? rec.save(id)
            record = rec
            crm = nil
            crmState = .done(done.isEmpty ? "Nothing selected." : "Saved in Aya: " + done.joined(separator: ", ") + ".")
        } catch {
            crmState = .failed(error.localizedDescription + (done.isEmpty ? "" : " (already saved: \(done.joined(separator: ", ")))"))
        }
    }

    func finishReview() {
        waitTask?.cancel()
        phase = .prepare
        record = nil; recapFields = []; crm = nil
        recapState = .idle; crmState = .idle
        state = AssistState(); status = .idle
        meetingID = nil; clientID = nil; projectID = nil; templateID = nil; goal = ""
        targetMeetingID = nil; targetRecord = nil; dayMeetings = []
        Task { await refreshUpcoming() }
    }
}

/// Drops out-of-order state updates when they hop to the main actor.
final class PublishSequence: @unchecked Sendable {
    private var issued = 0
    private var latest = 0
    private let lock = NSLock()
    func next() -> Int { lock.lock(); defer { lock.unlock() }; issued += 1; return issued }
    func isLatest(_ n: Int) -> Bool {
        lock.lock(); defer { lock.unlock() }
        if n < latest { return false }
        latest = n
        return true
    }
}
