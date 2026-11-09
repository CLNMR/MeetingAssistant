import Foundation
import os

private let log = Logger(subsystem: "de.local.MeetingScribe", category: "Assist")

public struct AssistSettings: Codable, Equatable, Sendable {
    public var model = "opus"            // opus | sonnet | haiku (Claude Code aliases)
    public var effort = "low"
    public var claudePath = ""           // empty = find automatically
    public var minWords = 60             // send when this many new words …
    public var minInterval = 20.0        // … and at least this many seconds since the last update
    public var maxInterval = 60.0        // send any new text after this long
    public var labelWait = 3.0           // wait this long for a speaker label
    public var restartTokens = 80_000    // start a fresh conversation above this context size …
    public var restartMinutes = 60.0     // … or after this long
    public init() {}

    /// How often updates are sent. More updates react faster but use more of the subscription.
    public enum Frequency: String, Codable, CaseIterable, Identifiable, Sendable {
        case frequent, normal, economical
        public var id: String { rawValue }
        public var title: String { self == .frequent ? "Frequent" : self == .normal ? "Normal" : "Economical" }
        public var explanation: String {
            switch self {
            case .frequent: return "About every 15–45 s. Fastest reactions, uses the most."
            case .normal: return "About every 20–60 s."
            case .economical: return "About every 40–120 s. Uses the least."
            }
        }
    }

    public var frequency: Frequency {
        get { minWords <= 40 ? .frequent : minWords >= 100 ? .economical : .normal }
        set {
            switch newValue {
            case .frequent: minWords = 40; minInterval = 15; maxInterval = 45
            case .normal: minWords = 60; minInterval = 20; maxInterval = 60
            case .economical: minWords = 100; minInterval = 40; maxInterval = 120
            }
        }
    }

    static let defaultsKey = "assist.settings.v1"
    public static func load() -> AssistSettings {
        guard let s = AppDefaults.shared.string(forKey: defaultsKey),
              let v = try? JSONDecoder().decode(AssistSettings.self, from: Data(s.utf8)) else { return AssistSettings() }
        return v
    }
    public func save() {
        if let d = try? JSONEncoder().encode(self), let s = String(data: d, encoding: .utf8) { AppDefaults.shared.set(s, forKey: Self.defaultsKey) }
    }

    public static let models: [(id: String, title: String)] = [("opus", "Opus (best)"), ("sonnet", "Sonnet (lighter)"), ("haiku", "Haiku (lightest)")]
}

public enum AssistStatus: Equatable, Sendable {
    case idle
    case starting
    case listening                 // waiting for enough new transcript
    case thinking                  // an update is being generated
    case reconnecting(String)
    case limited(String)           // subscription usage limit; retries later
    case paused
    case failed(String)
    case stopped

    public var text: String {
        switch self {
        case .idle: return "Not started"
        case .starting: return "Starting Claude…"
        case .listening: return "Listening"
        case .thinking: return "Thinking…"
        case .reconnecting(let m): return "Reconnecting… \(m)"
        case .limited(let m): return "Paused: Claude usage limit reached. \(m)"
        case .paused: return "Paused"
        case .failed(let m): return m
        case .stopped: return "Stopped"
        }
    }
}

/// A transcript line ready for Claude.
public struct AssistLine: Codable, Hashable, Sendable {
    public var start: Double
    public var speaker: String
    public var text: String
    public init(start: Double, speaker: String, text: String) { self.start = start; self.speaker = speaker; self.text = text }
    public var rendered: String {
        let s = Int(start)
        return String(format: "[%02d:%02d:%02d] ", s / 3600, (s / 60) % 60, s % 60) + "\(speaker): \(text)"
    }
}

/// The live assistant: turns live transcript lines into Claude updates and applies Claude's ops.
public actor AssistEngine {
    public typealias Publish = @Sendable (AssistState, AssistStatus) -> Void

    private let context: AssistContext
    private let settings: AssistSettings
    private let binary: URL
    private let publish: Publish

    public private(set) var state = AssistState()
    public private(set) var status: AssistStatus = .idle

    // Conversation
    private var stream: ClaudeStream?
    private var generation = 0
    private var conversationStart = Date()
    private var lastContextTokens = 0
    private var costBase = 0.0                 // cost of earlier conversations (restarts)
    private var costThisConversation = 0.0
    private var busy = false
    private var lastSend = Date.distantPast
    private var outputBuffer = ""
    private var restarts = 0
    private var stopping = false
    private var limitedUntil: Date?
    private var pendingRestart = false
    public private(set) var paused = false

    // Transcript
    private var sentUtterances = Set<String>()
    private var finalSeen: [String: Date] = [:]
    private var speakerNames: [String: String] = [:]
    private var pending: [AssistLine] = []
    private var history: [AssistLine] = []
    private var events: [String] = []
    private var forceNow = false
    private var loop: Task<Void, Never>?
    private var eventPump: Task<Void, Never>?
    private var continuation: AsyncStream<(Int, ClaudeStream.Event)>.Continuation?

    // Timing measurements (for msctl replay)
    public private(set) var latencies: [(firstOp: Double?, total: Double)] = []
    private var firstOpAt: Date?

    public init(context: AssistContext, settings: AssistSettings, binary: URL, publish: @escaping Publish) {
        self.context = context
        self.settings = settings
        self.binary = binary
        self.publish = publish
        state.playbook = context.playbookItems
        // Known contacts are expected attendees until Claude says otherwise.
        state.attendees = []
    }

    // MARK: Lifecycle

    public func start() async {
        guard stream == nil else { return }
        stopping = false
        let (s, c) = AsyncStream<(Int, ClaudeStream.Event)>.makeStream()
        continuation = c
        eventPump = Task { [weak self] in
            for await (gen, ev) in s { await self?.handle(ev, generation: gen) }
        }
        setStatus(.starting)
        await openConversation(first: AssistPrompts.contextMessage(context))
        loop = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 500_000_000)
                await self?.tick()
            }
        }
    }

    public func stop() async -> AssistState {
        stopping = true
        loop?.cancel(); loop = nil
        stream?.stop(); stream = nil
        continuation?.finish()
        eventPump = nil
        setStatus(.stopped)
        return state
    }

    private func openConversation(first message: String) async {
        generation += 1
        let gen = generation
        let cont = continuation
        let s = ClaudeStream(binary: binary, system: AssistPrompts.system, model: settings.model, effort: settings.effort) { ev in
            cont?.yield((gen, ev))
        }
        do {
            try s.start()
            stream = s
            conversationStart = Date()
            costBase += costThisConversation
            costThisConversation = 0
            lastContextTokens = 0
            try send(message)
        } catch {
            setStatus(.failed((error as? LocalizedError)?.errorDescription ?? error.localizedDescription))
        }
    }

    private func send(_ text: String) throws {
        guard let s = stream else { return }
        busy = true
        lastSend = Date()
        firstOpAt = nil
        outputBuffer = ""
        try s.send(text)
        setStatus(.thinking)
    }

    // MARK: Input

    /// Feed the full current list of live lines (as published by `LiveTranscriber`).
    /// Finished utterances are sent once, when all their speaker labels are known (or after a short wait).
    public func ingest(_ lines: [LiveLine]) {
        let now = Date()
        var groups: [String: [LiveLine]] = [:]
        for l in lines {
            let utt = l.id.split(separator: ".").dropLast().joined(separator: ".")
            groups[utt.isEmpty ? l.id : utt, default: []].append(l)
        }
        var ready: [(String, [LiveLine])] = []
        for (utt, ls) in groups where !sentUtterances.contains(utt) {
            guard ls.allSatisfy(\.isFinal) else { continue }
            let seen = finalSeen[utt] ?? now
            finalSeen[utt] = seen
            let labelled = ls.allSatisfy { $0.speaker != nil }
            if labelled || now.timeIntervalSince(seen) >= settings.labelWait { ready.append((utt, ls)) }
        }
        for (utt, ls) in ready.sorted(by: { ($0.1.first?.start ?? 0) < ($1.1.first?.start ?? 0) }) {
            sentUtterances.insert(utt)
            finalSeen[utt] = nil
            for l in ls.sorted(by: { $0.start < $1.start }) {
                let text = l.text.trimmingCharacters(in: .whitespaces)
                guard !text.isEmpty else { continue }
                pending.append(AssistLine(start: l.start, speaker: speakerName(l), text: text))
            }
        }
    }

    /// Lines from a finished transcript (for replays and tests).
    public func ingest(lines: [AssistLine]) { pending += lines }

    private func speakerName(_ l: LiveLine) -> String {
        guard let raw = l.speaker else { return context.mode == .call && l.track == .system ? "Them (?)" : "?" }
        if raw == "me" { return context.myName.isEmpty ? "Me" : context.myName }
        if let n = speakerNames[raw] { return n }
        let n = "Speaker \(speakerNames.count + 1)"
        speakerNames[raw] = n
        return n
    }

    // MARK: User actions

    public func suggestNow() { events.append("user asks: suggest a question now"); forceNow = true }

    /// While paused no updates are sent; the transcript keeps collecting and is sent on resume.
    public func setPaused(_ p: Bool) {
        paused = p
        if !p { forceNow = !pending.isEmpty }
        setStatus(p ? .paused : (busy ? .thinking : .listening))
    }

    public func markAsked(_ id: String) {
        guard let i = state.questions.firstIndex(where: { $0.id == id }) else { return }
        var q = state.questions.remove(at: i)
        q.status = .asked
        state.askedQuestions.append(q)
        events.append("user marked question as asked: \(q.text)")
        state.changed = []
        setStatus(status)
    }

    public func dismiss(_ id: String) {
        guard let i = state.questions.firstIndex(where: { $0.id == id }) else { return }
        let q = state.questions.remove(at: i)
        events.append("user dismissed question: \(q.text) — suggest something else")
        if state.questions.isEmpty { forceNow = true }
        state.changed = []
        setStatus(status)
    }

    public func promote(_ id: String) {
        guard let i = state.questions.firstIndex(where: { $0.id == id }), i > 0 else { return }
        let q = state.questions.remove(at: i)
        state.questions.insert(q, at: 0)
        state.changed = []
        setStatus(status)
    }

    public func setOpenPoint(_ id: String, _ st: AssistState.OpenPoint.Status) {
        guard let i = state.openPoints.firstIndex(where: { $0.id == id }) else { return }
        state.openPoints[i].status = st
        events.append("user set open point \(id) to \(st.rawValue)")
        state.changed = []
        setStatus(status)
    }

    public func setPlaybook(_ id: String, _ st: PlaybookItem.Status) {
        guard let i = state.playbook.firstIndex(where: { $0.id == id }) else { return }
        state.playbook[i].status = st
        events.append("user set playbook item \(id) to \(st.rawValue)")
        state.changed = []
        setStatus(status)
    }

    /// The transcript lines sent so far.
    public var transcript: [AssistLine] { history + pending }

    // MARK: Scheduling

    private func tick() async {
        guard !stopping, stream != nil, !paused else { return }
        if let until = limitedUntil {
            guard Date() >= until else { return }
            limitedUntil = nil
            forceNow = true
        }
        guard !busy else {
            // A hung update: no answer for 90 s.
            if Date().timeIntervalSince(lastSend) > 90 { await restart(reason: "no answer") }
            return
        }
        if pendingRestart || needsFreshConversation { pendingRestart = false; await restart(reason: nil); return }

        let since = Date().timeIntervalSince(lastSend)
        let words = pending.reduce(0) { $0 + $1.text.split(separator: " ").count }
        let me = context.myName.isEmpty ? "Me" : context.myName
        let questionFromThem = pending.last.map { $0.speaker != me && $0.text.hasSuffix("?") } ?? false
        let due = forceNow
            || (words >= settings.minWords && since >= settings.minInterval)
            || (!pending.isEmpty && since >= settings.maxInterval)
            || (questionFromThem && since >= 6)
            || (!events.isEmpty && pending.isEmpty && since >= settings.minInterval)
        guard due, !(pending.isEmpty && events.isEmpty) || forceNow else { return }

        let lines = pending
        let evs = events
        pending = []; events = []; forceNow = false
        history += lines
        do { try send(AssistPrompts.tickMessage(lines: lines.map(\.rendered), events: evs.isEmpty && lines.isEmpty ? ["user asks: suggest a question now"] : evs)) }
        catch {
            pending = lines + pending; events = evs + events
            history.removeLast(lines.count)
            await restart(reason: "write failed")
        }
    }

    private var needsFreshConversation: Bool {
        lastContextTokens > settings.restartTokens
            || Date().timeIntervalSince(conversationStart) > settings.restartMinutes * 60
    }

    /// Starts a new conversation that carries the panels and the recent transcript over.
    private func restart(reason: String?) async {
        if let reason { setStatus(.reconnecting(reason)); log.info("assist restart: \(reason)") }
        stream?.stop(); stream = nil
        busy = false
        restarts += 1
        let cutoff = (history.last?.start ?? 0) - 300
        let recent = history.filter { $0.start >= cutoff }.map(\.rendered)
        // Lines that were never answered go into the new conversation as recent transcript.
        let carry = pending.map(\.rendered)
        history += pending
        pending = []
        events = []
        await openConversation(first: AssistPrompts.resumeMessage(context, state: state, recent: recent + carry))
    }

    // MARK: Output

    private func handle(_ ev: ClaudeStream.Event, generation gen: Int) async {
        guard gen == generation else { return }     // events from an old conversation
        switch ev {
        case .text(let t):
            outputBuffer += t
            while let nl = outputBuffer.firstIndex(of: "\n") {
                let line = String(outputBuffer[..<nl])
                outputBuffer = String(outputBuffer[outputBuffer.index(after: nl)...])
                applyLine(line)
            }
        case .turnDone(let r):
            if !outputBuffer.isEmpty { applyLine(outputBuffer); outputBuffer = "" }
            busy = false
            latencies.append((firstOpAt.map { $0.timeIntervalSince(lastSend) }, Date().timeIntervalSince(lastSend)))
            costThisConversation = r.totalCostUSD
            state.costUSD = costBase + costThisConversation
            lastContextTokens = r.contextTokens
            if r.isError {
                if ClaudeCLI.isUsageLimit(r.text) {
                    limitedUntil = Date().addingTimeInterval(300)
                    setStatus(.limited("Recording continues; retrying in 5 minutes."))
                } else {
                    setStatus(.failed("Claude: \(r.text.prefix(200))"))
                    pendingRestart = true
                }
                return
            }
            state.updates += 1
            restarts = 0
            setStatus(paused ? .paused : .listening)
        case .exited(let code, let err):
            guard !stopping else { return }
            stream = nil
            busy = false
            let msg = err.trimmingCharacters(in: .whitespacesAndNewlines)
            log.error("claude exited \(code): \(msg)")
            if msg.lowercased().contains("login") || msg.lowercased().contains("not logged in") {
                setStatus(.failed(ClaudeCLI.CLIError.notLoggedIn.errorDescription!)); return
            }
            if restarts >= 5 { setStatus(.failed("Claude keeps stopping (exit \(code)). \(msg.prefix(200))")); return }
            try? await Task.sleep(nanoseconds: 2_000_000_000)
            if !stopping { await restart(reason: "Claude stopped (exit \(code))") }
        }
    }

    private func applyLine(_ line: String) {
        guard var op = AssistOp.parse(line) else {
            let t = line.trimmingCharacters(in: .whitespaces)
            if !t.isEmpty { log.debug("ignored output line: \(t.prefix(200))") }
            return
        }
        if firstOpAt == nil { firstOpAt = Date() }
        if case .nextQuestions = op { state.changed = [] }
        // Keep Claude's attendee matched to the Aya contact id if it named one we know.
        if case .attendee(var a) = op, let cid = a.contactID, !context.contacts.contains(where: { $0.id == cid }) {
            a.contactID = nil; op = .attendee(a)
        }
        state.changed.formUnion(state.apply(op))
        setStatus(busy ? .thinking : status)
    }

    private func setStatus(_ s: AssistStatus) {
        status = s
        publish(state, s)
    }
}
