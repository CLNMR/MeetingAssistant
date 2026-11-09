import Foundation
import MeetingAssistantCore

/// Runs an async command body from the synchronous top-level code and exits with its result.
func runBlocking(_ body: @escaping @Sendable () async throws -> Void) -> Never {
    let done = DispatchSemaphore(value: 0)
    let failure = LockedBox<Error?>(nil)
    Task {
        do { try await body() } catch { failure.value = error }
        done.signal()
    }
    done.wait()
    if let e = failure.value {
        FileHandle.standardError.write(Data("error: \((e as? LocalizedError)?.errorDescription ?? String(describing: e))\n".utf8))
        exit(1)
    }
    exit(0)
}

func ayaService() throws -> AyaService {
    guard let s = AyaService() else { throw AyaError.notConfigured }
    return s
}

/// msctl aya …  (the API key comes from $AYA_API_KEY or the Keychain)
func runAya(_ args: [String]) -> Never {
    let sub = args.first ?? ""
    let rest = Array(args.dropFirst())
    runBlocking {
        switch sub {
        case "setup":
            // msctl aya setup <orgId>: stores the org id and picks the default lists.
            guard let org = rest.first else { throw AyaError.badRequest("usage: msctl aya setup <orgId>") }
            var cfg = AyaConfig.load()
            cfg.orgId = org
            cfg.save()
            let svc = try ayaService()
            cfg = try await svc.autoSelectLists()
            cfg.save()
            for slot in AyaSlot.allCases {
                let r = cfg.list(slot)
                print("\(slot.title.padding(toLength: 15, withPad: " ", startingAt: 0)) \(r.map { "\($0.label) (\($0.techName)) in \($0.workspaceLabel)" } ?? "—")")
            }
            let schemas = try await AyaService(config: cfg, client: svc.client).refreshSchemas()
            for (k, s) in schemas.sorted(by: { $0.key < $1.key }) {
                print("schema \(k): \(s.fields.count) fields, \(s.writableContent.count) writable content, relations: \(s.relations.map(\.key).joined(separator: ", "))")
            }
        case "config":
            let cfg = AyaConfig.load()
            print(String(data: try JSONIO.encoder.encode(cfg), encoding: .utf8)!)
            print("api key: \(AyaConfig.apiKey == nil ? "missing" : "set")")
        case "workspaces":
            for w in try await ayaService().client.workspaces() { print("\(w.id)  \(w.label)") }
        case "specs":
            guard let ws = rest.first else { throw AyaError.badRequest("usage: msctl aya specs <workspaceId>") }
            for s in try await ayaService().client.recordSpecs(workspace: ws) { print("\(s.techName)  \(s.label)  home=\(s.workspaceId ?? "-")") }
        case "schema":
            guard let slot = rest.first.flatMap(AyaSlot.init(rawValue:)), let s = AyaCache.schema(slot) else {
                throw AyaError.badRequest("usage: msctl aya schema \(AyaSlot.allCases.map(\.rawValue).joined(separator: "|")) (after setup)")
            }
            for f in s.fields {
                let opts = f.options.isEmpty ? "" : "  options: \(f.options.prefix(8).joined(separator: ", "))\(f.options.count > 8 ? " …" : "")"
                print("\(f.writable ? "w" : "r") \(f.kind.rawValue.padding(toLength: 9, withPad: " ", startingAt: 0)) \(f.key)  [\(f.label)]\(opts)")
            }
        case "list":
            guard let slot = rest.first.flatMap(AyaSlot.init(rawValue:)) else { throw AyaError.badRequest("usage: msctl aya list <slot>") }
            let r = try await ayaService().refreshList(slot)
            for x in r.prefix(40) { print("\(x.id)  \(x.title)") }
            print("(\(r.count) records, cached)")
        case "search":
            guard rest.count >= 2, let slot = AyaSlot(rawValue: rest[0]) else { throw AyaError.badRequest("usage: msctl aya search <slot> <text>") }
            for x in try await ayaService().search(slot, text: rest.dropFirst().joined(separator: " ")) { print("\(x.id)  \(x.title)") }
        case "upcoming":
            let svc = try ayaService()
            let df = DateFormatter(); df.dateFormat = "EEE dd.MM. HH:mm"
            for m in try await svc.upcomingMeetings(days: Int(rest.first ?? "14") ?? 14) {
                let links = [svc.config.meetingClientField, svc.config.meetingProjectField, svc.config.meetingTemplateField]
                    .compactMap { $0.flatMap { m.linkedTitle($0) } }.joined(separator: " · ")
                print("\(m.id)  \(m.date(svc.config.meetingDateField).map(df.string) ?? "?")  \(m.title)\(links.isEmpty ? "" : "  [\(links)]")")
            }
        case "context":
            // msctl aya context [--meeting id] [--client id] [--project id] [--template id]
            let ctx = try await ayaService().meetingContext(meetingID: option(rest, "--meeting"), clientID: option(rest, "--client"),
                                                            projectID: option(rest, "--project"), templateID: option(rest, "--template"))
            let rendered = AssistPrompts.contextMessage(AssistContext(aya: ctx, config: AyaConfig.load(), goal: "", mode: .call,
                                                                      myName: "Me", date: Date()))
            print(rendered)
        default:
            print("""
            usage: msctl aya setup <orgId> | config | workspaces | specs <ws> | schema <slot>
                   msctl aya list <slot> | search <slot> <text> | upcoming [days]
                   msctl aya context [--meeting id] [--client id] [--project id] [--template id]
            slots: \(AyaSlot.allCases.map(\.rawValue).joined(separator: " ")) · API key from $AYA_API_KEY or the Keychain
            """)
        }
    }
}
