import Foundation
import Testing
@testable import MeetingAssistantCore

@Suite struct AssistOpTests {
    @Test func parsesEveryOp() {
        #expect(AssistOp.parse(#"{"op":"next_questions","items":[{"text":"Wer entscheidet?","why":"Entscheider fehlt","playbook_item":"P3.3"}]}"#)
                == .nextQuestions([.init(id: "Q1", text: "Wer entscheidet?", why: "Entscheider fehlt", playbookItem: "P3.3")]))
        #expect(AssistOp.parse(#"{"op":"summary_append","bullets":["A","  ","B"]}"#) == .summaryAppend(["A", "B"]))
        #expect(AssistOp.parse(#"{"op":"summary_replace","index":2,"text":"X"}"#) == .summaryReplace(index: 2, text: "X"))
        #expect(AssistOp.parse(#"{"op":"open_point","id":"O1","text":"Budget","status":"answered"}"#)
                == .openPoint(.init(id: "O1", text: "Budget", raisedBy: nil, status: .answered)))
        #expect(AssistOp.parse(#"{"op":"playbook","item":"P1.2","status":"covered"}"#) == .playbook(item: "P1.2", status: .covered, evidence: nil))
        if case .attendee(let a) = AssistOp.parse(#"{"op":"attendee","name":"Anna","speaker":"Speaker 2","present":true}"#) {
            #expect(a.name == "Anna" && a.speaker == "Speaker 2" && a.present == true)
        } else { Issue.record("attendee not parsed") }
    }

    @Test func ignoresNoise() {
        for line in ["", "```json", "Here are the updates:", #"{"op":"unknown"}"#, #"{"op":"playbook","item":"P1","status":"done"}"#, "{\"op\":\"summary_ap"] {
            #expect(AssistOp.parse(line) == nil)
        }
        #expect(AssistOp.parse(#"{"op":"summary_append","bullets":["x"]},"#) == .summaryAppend(["x"]))   // trailing comma
    }

    @Test func applyMergesAttendeesAndOpenPoints() {
        var s = AssistState()
        s.apply(.attendee(.init(name: "Speaker 2", speaker: "Speaker 2")))
        s.apply(.attendee(.init(name: "Anna Müller", role: "GF", speaker: "Speaker 2")))
        #expect(s.attendees.count == 1)
        #expect(s.attendees[0].name == "Anna Müller" && s.attendees[0].role == "GF" && s.attendees[0].present == true)
        s.apply(.openPoint(.init(id: "O1", text: "Budget", raisedBy: "Anna")))
        s.apply(.openPoint(.init(id: "O1", text: "Budget 2027", raisedBy: nil, status: .answered)))
        #expect(s.openPoints.count == 1 && s.openPoints[0].raisedBy == "Anna" && s.openPoints[0].status == .answered)
        s.playbook = [PlaybookItem(id: "P1.1", section: "A", text: "x")]
        #expect(s.apply(.playbook(item: "P1.1", status: .partial, evidence: "e")) == ["p:P1.1"])
        #expect(s.apply(.playbook(item: "P9", status: .covered, evidence: nil)).isEmpty)
    }
}

@Suite struct PlaybookParserTests {
    @Test func sectionsAndBullets() {
        let md = """
        ### 1. Vorbereitung

        - Firma recherchieren
        - Ziel festlegen

        ### 2. Bedarf ermitteln
        1. Welche Herausforderungen gibt es?
        * Wer entscheidet mit?
        """
        let items = PlaybookParser.items(md)
        #expect(items.map(\.id) == ["P1.1", "P1.2", "P2.1", "P2.2"])
        #expect(items[0].section == "Vorbereitung" && items[2].text == "Welche Herausforderungen gibt es?")
    }

    @Test func flatList() {
        #expect(PlaybookParser.items("- a\n- b\nSome prose\n- c").map(\.id) == ["P1", "P2", "P3"])
    }
}

@Suite struct AyaSchemaTests {
    static let openAPI = #"""
    {"paths": {"/api/workspaces/W/records/pmMeetings": {"post": {"requestBody": {"content": {"application/json": {"schema": {"$ref": "#/components/schemas/PmMeetingsCreateBody"}}}}}}},
     "components": {"schemas": {
       "PmMeetingsCreateBody": {"properties": {"values": {"properties": {}}}},
       "PmMeetingsUpdateBody": {"properties": {"values": {"properties": {
          "title__value": {"anyOf": [{"type": "string"}, {"type": "null"}], "description": "Text content"},
          "summary__value": {"anyOf": [{"type": "string"}, {"type": "null"}], "description": "GitHub-Flavored Markdown text"},
          "meeting_type__value": {"anyOf": [{"type": "string"}, {"type": "null"}], "description": "Selected option techName"},
          "meeting_date__value": {"anyOf": [{"type": "string"}, {"type": "null"}], "description": "ISO datetime string"},
          "participants__ids": {"type": "array", "description": "Referenced record ULIDs (insertion order)"},
          "project__id": {"anyOf": [{"type": "string"}, {"type": "null"}], "description": "Referenced record ULID"},
          "done__value": {"type": "boolean", "description": "Boolean value"},
          "count__value": {"anyOf": [{"type": "number"}, {"type": "null"}], "description": "Numeric value"}}}}},
       "PmMeetingsRecord": {"properties": {
          "id": {"type": "string"}, "title__value": {}, "task_count__value": {"anyOf": [{"type": "number"}, {"type": "null"}], "description": "Numeric value"},
          "project__computed_title": {"readOnly": true}}}}}}
    """#

    @Test func parsesKindsAndWritability() throws {
        var s = try AyaListSchema.parse(openAPI: Data(Self.openAPI.utf8), workspaceId: "W", techName: "pmMeetings")
        #expect(s.field("summary__value")?.kind == .markdown)
        #expect(s.field("meeting_type__value")?.kind == .select)
        #expect(s.field("meeting_date__value")?.kind == .datetime)
        #expect(s.field("participants__ids")?.kind == .relations)
        #expect(s.field("project__id")?.kind == .relation)
        #expect(s.field("done__value")?.kind == .boolean)
        #expect(s.field("task_count__value")?.writable == false)
        #expect(s.field("project__computed_title") == nil)
        #expect(s.field("meeting_type__value")?.label == "Meeting type")
        #expect(AyaField(key: "kunde_praefix__value", kind: .text, writable: true).brickTechName == "kundePraefix")

        s.learnOptions(from: [AyaRecord(values: ["meeting_type__value": .string("customer")]),
                              AyaRecord(values: ["meeting_type__value": .string("internal")])])
        #expect(s.field("meeting_type__value")?.options == ["customer", "internal"])
        #expect(s.coerce(.string("customer"), for: "meeting_type__value") == .string("customer"))
        #expect(s.coerce(.string("sales"), for: "meeting_type__value") == nil)
        #expect(s.coerce(.string("ja"), for: "done__value") == .bool(true))
        #expect(s.coerce(.string("1,5"), for: "count__value") == .number(1.5))
        #expect(s.coerce(.string("x"), for: "nope__value") == nil)
    }

    @Test func unknownListIsReported() {
        #expect(throws: AyaError.self) { try AyaListSchema.parse(openAPI: Data(Self.openAPI.utf8), workspaceId: "W", techName: "firma") }
    }

    @Test func queryJSONShape() {
        let q = AyaQuery(condition: .init(field: "firma", valueKey: "id", value: "01ABC"), sortField: "system", sortValueKey: "updatedAt", descending: true, limit: 5)
        let j = q.json
        #expect(j["filter"]?["value"] == .string("01ABC"))           // a plain string, not {"value": …}
        #expect(j["filter"]?["valueType"] == .string("constant"))
        #expect(j["sorting"]?.array?.first?["descending"] == .bool(true))
        #expect(j["pagination"]?["limit"] == .number(5))
    }

    @Test func searchNameDropsLegalForm() {
        #expect(AyaService.searchName("Müller Maschinenbau GmbH & Co. KG") == "Müller Maschinenbau")
        #expect(AyaService.searchName("Beispilop GmbH") == "Beispilop")
    }
}

@Suite struct RecapSchemaTests {
    @Test func recapFieldsSkipAppFilledAndUnknownSelects() {
        let s = AyaListSchema(workspaceId: "W", techName: "pmMeetings", fields: [
            AyaField(key: "title__value", kind: .text, writable: true),
            AyaField(key: "summary__value", kind: .markdown, writable: true),
            AyaField(key: "meeting_date__value", kind: .datetime, writable: true),
            AyaField(key: "external_meeting_id__value", kind: .text, writable: true),
            AyaField(key: "meeting_type__value", kind: .select, writable: true, options: ["customer"]),   // from the template
            AyaField(key: "status__value", kind: .select, writable: true, options: ["done"]),
            AyaField(key: "mood__value", kind: .select, writable: true),
            AyaField(key: "project__id", kind: .relation, writable: true),
        ])
        let keys = RecapBuilder.recapFields(s, config: AyaConfig()).map(\.key)
        #expect(keys == ["title__value", "summary__value", "status__value"])
        let schema = RecapBuilder.objectSchema(RecapBuilder.recapFields(s, config: AyaConfig()))
        #expect(schema["required"]?.array?.count == 3)
        #expect(schema["properties"]?["status__value"]?["enum"] == .array([.string("done"), .null]))
    }
}

@Suite struct MeetingTargetTests {
    static let schema = AyaListSchema(workspaceId: "W", techName: "pmMeetings", fields: [
        AyaField(key: "title__value", kind: .text, writable: true),
        AyaField(key: "summary__value", kind: .markdown, writable: true),
        AyaField(key: "meeting_date__value", kind: .datetime, writable: true),
        AyaField(key: "external_meeting_id__value", kind: .text, writable: true),
        AyaField(key: "meeting_type__value", kind: .select, writable: true, options: ["customer", "internal"]),
        AyaField(key: "customer__id", kind: .relation, writable: true),
        AyaField(key: "project__id", kind: .relation, writable: true),
        AyaField(key: "load_template__id", kind: .relation, writable: true),
        AyaField(key: "participants__ids", kind: .relations, writable: true),
    ])

    static func record() -> AssistRecord {
        var c = AssistIngestTests.context()
        c.clientID = "C1"; c.clientTitle = "Acme"
        c.projectID = "P1"; c.projectTitle = "P0001 – Acme rollout"
        c.playbookID = "T1"; c.playbookTitle = "Projekt-Kickoff"
        c.meetingType = "customer"
        return AssistRecord(context: c, state: AssistState(), lines: [])
    }

    static func fields() -> [ProposedValue] {
        [ProposedValue(key: "title__value", label: "Title", kind: .text, options: [], old: "", value: "Kickoff Acme", reason: nil, include: true),
         ProposedValue(key: "summary__value", label: "Summary", kind: .markdown, options: [], old: "", value: "- went well", reason: nil, include: true)]
    }

    @Test func newMeetingGetsLinksTypeDateAndParticipants() {
        let v = RecapBuilder.recapValues(Self.fields(), record: Self.record(), sessionID: "S", startedAt: Date(timeIntervalSince1970: 0),
                                         schema: Self.schema, config: AyaConfig(), memberIDs: ["M1"])
        #expect(v["customer__id"] == .string("C1"))
        #expect(v["project__id"] == .string("P1"))
        #expect(v["load_template__id"] == .string("T1"))
        #expect(v["meeting_type__value"] == .string("customer"))
        #expect(v["participants__ids"] == .array([.string("M1")]))
        #expect(v["meeting_date__value"] == .string("1970-01-01T00:00:00Z"))
        #expect(v["external_meeting_id__value"] == .string("meetingassistant:S"))
        #expect(v["title__value"] == .string("Kickoff Acme"))
    }

    @Test func existingMeetingKeepsWhatItHas() {
        let existing = AyaRecord(values: ["id": .string("X"), "title__value": .string("Kickoff (planned)"),
                                          "customer__id": .string("C9"), "project__id": .null,
                                          "meeting_date__value": .string("2026-11-10T09:00:00+00:00"),
                                          "meeting_type__value": .string("internal"),
                                          "participants__ids": .array([.string("M0")])])
        // The planned title is kept unless the user ticks the title field.
        var f = RecapBuilder.target(Self.fields(), existing: existing)
        #expect(f[0].old == "Kickoff (planned)" && !f[0].include)
        #expect(f[1].old.isEmpty && f[1].include)
        f[0].include = false
        let v = RecapBuilder.recapValues(f, record: Self.record(), sessionID: "S", startedAt: Date(),
                                         schema: Self.schema, config: AyaConfig(), existing: existing, memberIDs: ["M1", "M0"])
        #expect(v["title__value"] == nil)
        #expect(v["summary__value"] == .string("- went well"))
        #expect(v["customer__id"] == nil)                 // already linked elsewhere: left alone
        #expect(v["project__id"] == .string("P1"))        // empty: filled
        #expect(v["meeting_date__value"] == nil)
        #expect(v["meeting_type__value"] == nil)
        #expect(v["participants__ids"] == .array([.string("M0"), .string("M1")]))
    }

    @Test func membersMatchByFullOrUniqueFirstName() {
        let members = [AyaRecord(values: ["id": .string("1"), "computedTitle": .string("Erika Musterfrau")]),
                       AyaRecord(values: ["id": .string("2"), "computedTitle": .string("Lena Beispiel")]),
                       AyaRecord(values: ["id": .string("3"), "computedTitle": .string("Max Muster")]),
                       AyaRecord(values: ["id": .string("4"), "computedTitle": .string("Max Beispiel")])]
        #expect(RecapBuilder.memberIDs(attendees: ["erika musterfrau", "Lena", "Max", "Someone Else"], members: members) == ["1", "2"])
    }

    @Test func titleFallsBackFromMeetingToClientAndTemplate() {
        var c = Self.record().context
        #expect(c.title == "Acme – Projekt-Kickoff")
        c.clientID = nil
        #expect(c.title == "P0001 – Acme rollout – Projekt-Kickoff")
        c.meetingTitle = "Weekly"
        #expect(c.title == "Weekly")
    }

    @Test func oldAssistRecordsStillLoad() throws {
        // assist.json from before templates: clientID always set, no project/meeting keys.
        var json = try JSONSerialization.jsonObject(with: JSONIO.encoder.encode(Self.record().context)) as! [String: Any]
        for k in ["projectID", "projectTitle", "projectFieldList", "meetingID", "meetingTitle", "meetingType"] { json.removeValue(forKey: k) }
        let c = try JSONIO.decoder.decode(AssistContext.self, from: JSONSerialization.data(withJSONObject: json))
        #expect(c.clientID == "C1" && c.projectID == nil && c.meetingID == nil)
    }
}

@Suite struct AyaConfigMigrationTests {
    @Test func legacyConfigKeepsConnectionAndClientLists() {
        let legacy = #"{"orgId":"ORG","contactsClientField":"firma__id","recapClientField":null,"lists":{"#
            + #""clients":{"workspaceId":"W1","workspaceLabel":"CRM","techName":"firma","label":"Companies"},"#
            + #""playbooks":{"workspaceId":"W2","workspaceLabel":"Wiki","techName":"wikiPlaybooks","label":"Playbooks"},"#
            + #""recaps":{"workspaceId":"W3","workspaceLabel":"PM","techName":"pmMeetings","label":"Meetings"}}}"#
        let c = AyaConfig.migrate(legacy: Data(legacy.utf8))
        #expect(c.orgId == "ORG")
        #expect(c.list(.clients)?.techName == "firma")
        #expect(c.list(.meetings) == nil && c.list(.templates) == nil)   // picked again from Aya
        #expect(c.lists["playbooks"] == nil && c.lists["recaps"] == nil)
        #expect(!c.isComplete)
        #expect(c.meetingClientField == "customer__id")
    }

    @Test func explicitNoLinkSurvivesSaving() throws {
        var c = AyaConfig()
        c.meetingProjectField = nil
        let back = try JSONDecoder().decode(AyaConfig.self, from: JSONEncoder().encode(c))
        #expect(back.meetingProjectField == nil)
        #expect(back.meetingClientField == "customer__id")
    }
}

@Suite struct AssistIngestTests {
    static func context() -> AssistContext {
        AssistContext(clientID: "c", clientTitle: "Client", clientFieldList: [], contacts: [], playbookID: nil, playbookTitle: nil,
                      playbookGoal: nil, playbookItems: [], recaps: [], goal: "", mode: .call, myName: "Erika", date: Date())
    }

    @Test func sendsFinishedUtterancesOnceWithStableSpeakerNames() async {
        var s = AssistSettings(); s.labelWait = 60
        let e = AssistEngine(context: Self.context(), settings: s, binary: URL(fileURLWithPath: "/usr/bin/false")) { _, _ in }
        func line(_ id: String, _ t: Track, _ start: Double, _ text: String, _ spk: String?, final: Bool) -> LiveLine {
            LiveLine(id: id, track: t, start: start, end: start + 1, text: text, speaker: spk, isFinal: final)
        }
        // Not final yet → nothing.
        await e.ingest([line("mic-1.0", .mic, 1, "Hallo", "me", final: false)])
        #expect(await e.transcript.isEmpty)
        // Final, but the system line has no speaker yet → wait.
        await e.ingest([line("mic-1.0", .mic, 1, "Hallo zusammen", "me", final: true),
                        line("system-1.0", .system, 2, "Hi", nil, final: true)])
        #expect(await e.transcript.map(\.rendered) == ["[00:00:01] Erika: Hallo zusammen"])
        // Label arrives; the utterance is split into two speakers.
        await e.ingest([line("mic-1.0", .mic, 1, "Hallo zusammen", "me", final: true),
                        line("system-1.0", .system, 2, "Hi", "system-3", final: true),
                        line("system-1.1", .system, 3, "und hallo", "system-5", final: true)])
        await e.ingest([line("system-1.0", .system, 2, "Hi", "system-3", final: true)])   // repeated publish
        #expect(await e.transcript.map(\.rendered) == ["[00:00:01] Erika: Hallo zusammen", "[00:00:02] Speaker 1: Hi", "[00:00:03] Speaker 2: und hallo"])
    }
}
