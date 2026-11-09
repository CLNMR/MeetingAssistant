import Foundation
import MeetingAssistantCore

/// Prints what changed between two assist states (for replays).
final class StatePrinter: @unchecked Sendable {
    private var last = AssistState()
    private var lastStatus: AssistStatus = .idle
    private let lock = NSLock()
    private let t0 = Date()

    func show(_ s: AssistState, _ status: AssistStatus) {
        lock.lock(); defer { lock.unlock() }
        let ts = String(format: "%6.1fs", Date().timeIntervalSince(t0))
        if status != lastStatus { print("\(ts) · \(status.text)"); lastStatus = status }
        if s.questions != last.questions, let q = s.questions.first {
            print("\(ts) ❓ \(q.text)\(q.why.map { "   (\($0))" } ?? "")")
            for alt in s.questions.dropFirst() { print("          · \(alt.text)") }
        }
        for b in s.summary.dropFirst(last.summary.count) { print("\(ts) 📝 \(b)") }
        for p in s.openPoints where !last.openPoints.contains(p) { print("\(ts) ⭕ \(p.id) [\(p.status.rawValue)] \(p.text)") }
        for a in s.attendees where !last.attendees.contains(a) {
            print("\(ts) 👤 \(a.name)\(a.role.map { ", \($0)" } ?? "")\(a.speaker.map { " = \($0)" } ?? "")\(a.contactID.map { " [Aya \($0)]" } ?? "")")
        }
        for i in s.playbook where !last.playbook.contains(i) { print("\(ts) ✅ \(i.id) \(i.status.rawValue)\(i.evidence.map { " — \($0)" } ?? "")") }
        last = s
    }
}

func option(_ args: [String], _ name: String) -> String? {
    args.firstIndex(of: name).flatMap { $0 + 1 < args.count ? args[$0 + 1] : nil }
}

func claudeBinary() throws -> URL {
    guard let b = ClaudeCLI.locate(preferred: ProcessInfo.processInfo.environment["CLAUDE_PATH"]) else { throw ClaudeCLI.CLIError.notInstalled }
    return b
}

/// Builds the context for a session from Aya (meeting, client, project and template ids from the
/// arguments, else from assist.json).
func replayContext(_ args: [String], sessionID: String, mode: MeetingMode) async throws -> AssistContext {
    let ids = ["--meeting", "--client", "--project", "--template"].map { option(args, $0) }
    if let rec = AssistRecord.load(sessionID), ids.allSatisfy({ $0 == nil }) { return rec.context }
    let svc = try ayaService()
    let aya = try await svc.meetingContext(meetingID: ids[0], clientID: ids[1], projectID: ids[2], templateID: ids[3])
    return AssistContext(aya: aya, config: svc.config, goal: option(args, "--goal") ?? "", mode: mode,
                         myName: option(args, "--me") ?? "Me", date: Date())
}

func runAssist(_ args: [String]) -> Never {
    let sub = args.first ?? ""
    let rest = Array(args.dropFirst())
    runBlocking {
        switch sub {
        case "check":
            let b = try claudeBinary()
            print("claude: \(b.path)")
            if let s = await ClaudeCLI.authStatus(binary: b) {
                print("logged in: \(s.loggedIn) · \(s.method ?? "?") · plan: \(s.subscription ?? "?") · \(s.email ?? "")")
            } else { print("could not read `claude auth status`") }

        case "replay":
            // msctl assist replay <session-id> [--meeting|--client|--project|--template <id>] [--speed 8] [--model opus] [--minutes 10]
            guard let sid = rest.first else { throw AyaError.badRequest("usage: msctl assist replay <session-id> [--meeting|--client|--project|--template <id>] [--speed N] [--minutes M]") }
            let info = try JSONIO.read(SessionInfo.self, from: AppPaths.info(sid))
            let transcript = try JSONIO.read(Transcript.self, from: AppPaths.session(sid).appendingPathComponent("transcript.json"))
            let speed = Double(option(rest, "--speed") ?? "1") ?? 1
            let limit = Double(option(rest, "--minutes") ?? "0").map { $0 * 60 } ?? 0
            let context = try await replayContext(rest, sessionID: sid, mode: info.mode)
            var settings = AssistSettings()
            if let m = option(rest, "--model") { settings.model = m }
            if let e = option(rest, "--effort") { settings.effort = e }
            settings.minInterval /= speed; settings.maxInterval /= speed
            let printer = StatePrinter()
            let engine = AssistEngine(context: context, settings: settings, binary: try claudeBinary()) { s, st in printer.show(s, st) }
            print("replaying \(info.title) (\(Int(info.durationSec / 60)) min) at \(speed)× with \(settings.model)/\(settings.effort)")
            print("context: \(context.title) · \(context.contacts.count) contacts · template \(context.playbookTitle ?? "—") (\(context.playbookItems.count) items) · \(context.recaps.count) recaps")
            await engine.start()
            let me = info.mode == .call ? (transcript.speakers.first { $0.isMe }?.id) : nil
            let start = Date()
            for turn in transcript.turns {
                if limit > 0, turn.start > limit { break }
                let due = start.addingTimeInterval(turn.end / speed)
                let wait = due.timeIntervalSinceNow
                if wait > 0 { try await Task.sleep(nanoseconds: UInt64(wait * 1e9)) }
                let name = turn.speaker == me ? "Me" : transcript.label(for: turn.speaker)
                await engine.ingest(lines: [AssistLine(start: turn.start, speaker: name, text: turn.text)])
            }
            // Let the last update finish.
            try await Task.sleep(nanoseconds: UInt64(max(settings.maxInterval, 5) * 1e9))
            for _ in 0..<60 where await engine.status == .thinking { try await Task.sleep(nanoseconds: 1_000_000_000) }
            let final = await engine.stop()
            let lat = await engine.latencies
            let firsts = lat.compactMap(\.firstOp).sorted(), totals = lat.map(\.total).sorted()
            func med(_ a: [Double]) -> String { a.isEmpty ? "-" : String(format: "%.1fs", a[a.count / 2]) }
            print("\nupdates: \(final.updates) · median first op \(med(firsts)) · median total \(med(totals)) · API-equivalent cost $\(String(format: "%.3f", final.costUSD))")
            if let out = option(rest, "--save") {
                let rec = AssistRecord(context: context, state: final, lines: await engine.transcript)
                try JSONIO.write(rec, to: URL(fileURLWithPath: out))
                print("saved \(out)")
            }

        case "live":
            // msctl assist live <session-id> --client <id> [--playbook <id>] [--minutes M]
            // Plays the session's audio through the real LiveTranscriber (2× speed) into the engine.
            guard let sid = rest.first else { throw AyaError.badRequest("usage: msctl assist live <session-id> --client <id> [--minutes M]") }
            let info = try JSONIO.read(SessionInfo.self, from: AppPaths.info(sid))
            let context = try await replayContext(rest, sessionID: sid, mode: info.mode)
            let speed = 2.0
            var settings = AssistSettings()
            settings.minInterval /= speed; settings.maxInterval /= speed; settings.labelWait /= speed
            let printer = StatePrinter()
            let engine = AssistEngine(context: context, settings: settings, binary: try claudeBinary()) { s, st in printer.show(s, st) }
            await engine.start()
            let live = LiveTranscriber(mode: info.mode) { lines in Task { await engine.ingest(lines) } }
            await live.start(tracks: info.tracks)
            let limit = Double(option(rest, "--minutes") ?? "3").map { $0 * 60 } ?? 180
            let audio = try info.tracks.map { try AudioIO.load16k(AppPaths.track(sid, $0)) }
            let block = 1600, n = min(audio.map(\.count).max() ?? 0, Int(limit * 16_000))
            var i = 0
            while i < n {
                for (k, a) in audio.enumerated() where i < a.count { live.feed(info.tracks[k], Array(a[i..<min(a.count, i + block)])) }
                i += block
                try await Task.sleep(nanoseconds: UInt64(100_000_000 / speed))
            }
            await live.stop()
            try await Task.sleep(nanoseconds: 8_000_000_000)
            for _ in 0..<60 where await engine.status == .thinking { try await Task.sleep(nanoseconds: 1_000_000_000) }
            let final = await engine.stop()
            let sent = await engine.transcript
            print("\nlines sent to Claude: \(sent.count) · updates: \(final.updates)")
            for l in sent.prefix(12) { print("  " + l.rendered) }

        case "recap", "crm":
            // msctl assist recap|crm <session-id> [--record assist.json]
            guard let sid = rest.first else { throw AyaError.badRequest("usage: msctl assist recap|crm <session-id> [--record file]") }
            let rec: AssistRecord
            if let f = option(rest, "--record") { rec = try JSONIO.read(AssistRecord.self, from: URL(fileURLWithPath: f)) }
            else if let r = AssistRecord.load(sid) { rec = r }
            else { throw AyaError.badRequest("no assist.json for this session; pass --record <file> from `replay --save`") }
            let cfg = AyaConfig.load()
            let model = option(rest, "--model") ?? "opus"
            if sub == "recap" {
                guard let schema = AyaCache.schema(.meetings) else { throw AyaError.notConfigured }
                let t0 = Date()
                let fields = try await RecapBuilder.draftRecap(binary: try claudeBinary(), model: model, record: rec, sessionID: sid, schema: schema, config: cfg)
                print("recap draft in \(String(format: "%.1f", Date().timeIntervalSince(t0)))s")
                for f in fields { print("── \(f.key) [\(f.kind.rawValue)]\(f.include ? "" : " (empty)")\n\(f.value)\n") }
                if rest.contains("--upload-test") {
                    // Creates the recap with a TEST title, prints it, and deletes it again.
                    let svc = try ayaService()
                    let info = try JSONIO.read(SessionInfo.self, from: AppPaths.info(sid))
                    var values = RecapBuilder.recapValues(fields, record: rec, sessionID: sid, startedAt: info.startedAt, schema: schema, config: cfg)
                    values[cfg.meetingTitleField] = .string("TEST " + (values[cfg.meetingTitleField]?.description ?? ""))
                    let ref = cfg.list(.meetings)!
                    let created = try await svc.client.create(workspace: ref.workspaceId, list: ref.techName, values: values)
                    print("created \(created.id): \(created.title)")
                    for k in values.keys.sorted() { print("  \(k) = \(AssistPrompts.clipPublic(created[k]?.description ?? "∅", 120))") }
                    try await svc.client.delete(workspace: ref.workspaceId, list: ref.techName, id: created.id)
                    print("deleted again")
                }
            } else {
                let svc = try ayaService()
                guard let cRef = cfg.list(.clients), let cs = AyaCache.schema(.clients) else { throw AyaError.notConfigured }
                guard let clientID = rec.context.clientID else { throw AyaError.badRequest("this meeting has no client") }
                let client = try await svc.client.record(workspace: cRef.workspaceId, list: cRef.techName, id: clientID)
                var contacts: [AyaRecord] = []
                if let pRef = cfg.list(.contacts) {
                    for c in rec.context.contacts { if let r = try? await svc.client.record(workspace: pRef.workspaceId, list: pRef.techName, id: c.id) { contacts.append(r) } }
                }
                let t0 = Date()
                let p = try await RecapBuilder.proposeCRM(binary: try claudeBinary(), model: model, record: rec, sessionID: sid, client: client,
                                                          contacts: contacts, clientSchema: cs, contactSchema: AyaCache.schema(.contacts))
                print("CRM proposal in \(String(format: "%.1f", Date().timeIntervalSince(t0)))s")
                for c in p.client { print("client \(c.key): \(AssistPrompts.clipPublic(c.old, 80)) → \(AssistPrompts.clipPublic(c.value, 200))\n   because: \(c.reason ?? "")") }
                for c in p.contacts {
                    print("\(c.contactID == nil ? "NEW contact" : "contact") \(c.name):")
                    for ch in c.changes { print("   \(ch.key): \(AssistPrompts.clipPublic(ch.old, 60)) → \(AssistPrompts.clipPublic(ch.value, 160))") }
                }
            }

        default:
            print("""
            usage: msctl assist check
                   msctl assist replay <session-id> --client <id> [--playbook <id>] [--speed N] [--minutes M] [--model opus|sonnet|haiku] [--save file]
                   msctl assist recap <session-id> [--record file] [--upload-test]
                   msctl assist crm <session-id> [--record file]
            """)
        }
    }
}
