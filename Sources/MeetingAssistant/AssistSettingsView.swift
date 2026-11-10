import MeetingAssistantCore
import SwiftUI

/// Settings › Assist: the Aya connection, which lists are used, and the Claude settings.
struct AssistSettingsView: View {
    @ObservedObject var assist: AssistController
    @State private var org = ""
    @State private var key = ""

    var body: some View {
        Form {
            Section {
                HStack(spacing: 12) {
                    AyaLogoTile(size: 36)
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Aya").font(.system(size: 15, weight: .semibold))
                        Text(assist.isConnected ? "Connected · lists, schemas and recaps come from your workspaces" : "Not connected")
                            .font(.system(size: 12)).foregroundStyle(assist.isConnected ? Aya.success : Aya.textMuted)
                    }
                }
                TextField("Organization id", text: $org, prompt: Text("app.hey-aya.ai/org/<id>/…"))
                SecureField("API key", text: $key, prompt: Text(assist.hasAPIKey ? "stored in the Keychain" : "aya_key_…"))
                HStack {
                    Button(assist.isConnected ? "Reconnect" : "Connect") { Task { await assist.connect(orgId: org, apiKey: key); key = "" } }
                        .buttonStyle(AyaButtonStyle(kind: .primary))
                    .disabled(org.trimmingCharacters(in: .whitespaces).isEmpty || (key.isEmpty && !assist.hasAPIKey) || assist.connectState.isWorking)
                    if assist.hasAPIKey { Button("Remove Key") { assist.disconnect() }.buttonStyle(AyaButtonStyle(kind: .secondary)) }
                    Spacer()
                }
                StepLine(step: assist.connectState)
            }

            if assist.hasAPIKey && !assist.config.orgId.isEmpty {
                Section {
                    ForEach(AyaSlot.allCases) { slot in listPicker(slot) }
                    HStack {
                        Button("Refresh Fields") { Task { await assist.refreshSchemas() } }.disabled(assist.connectState.isWorking)
                        if assist.specs.isEmpty { Button("Show All Lists") { Task { await assist.loadCatalog() } } }
                    }
                } header: { Text("Lists") } footer: {
                    Text("A list is used in the workspace where it is defined. Field details come from the workspace's API documentation.").font(.caption)
                }

                Section("Links") {
                    relationPicker("Contact → client", slot: .contacts, value: $assist.config.contactsClientField)
                    relationPicker("Meeting → client", slot: .meetings, value: $assist.config.meetingClientField,
                                   noneHint: "Without a link, earlier meetings are found by the client's name.")
                    relationPicker("Meeting → project", slot: .meetings, value: $assist.config.meetingProjectField)
                    relationPicker("Meeting → template", slot: .meetings, value: $assist.config.meetingTemplateField)
                    relationPicker("Meeting → team members", slot: .meetings, value: $assist.config.meetingParticipantsField,
                                   noneHint: "Team members who attended are added here, matched by name.")
                    tablesFieldPicker
                }
            }

            Section {
                HStack(spacing: 12) {
                    AyaAgentDot(icon: "sparkles", size: 30)
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Claude").font(.system(size: 15, weight: .semibold))
                        Text("Runs through Claude Code with your subscription").font(.system(size: 12)).foregroundStyle(Aya.textMuted)
                    }
                }
                LabeledContent("Claude Code") {
                    VStack(alignment: .trailing, spacing: 2) {
                        Text(assist.claudeBinary?.path ?? "not found").font(.callout.monospaced()).foregroundStyle(.secondary)
                        StepLine(step: assist.claudeState)
                    }
                }
                TextField("Path (optional)", text: $assist.settings.claudePath, prompt: Text("found automatically"))
                Button("Check") { Task { await assist.checkClaude() } }
                LabeledContent("Model") {
                    AyaSelect(selection: $assist.settings.model, options: AssistSettings.models.map { ($0.id, $0.title) }).frame(maxWidth: 240)
                }
                LabeledContent("Update frequency") {
                    AyaSelect(selection: $assist.settings.frequency, options: AssistSettings.Frequency.allCases.map { ($0, $0.title) }).frame(maxWidth: 240)
                }
                Text(assist.settings.frequency.explanation + " Assist uses your Claude subscription through Claude Code, not the API: each update counts against your plan's usage limits.")
                    .font(.caption).foregroundStyle(.secondary)
                Text("In Assist mode the transcript text and the client context from Aya are sent to Claude (Anthropic). Audio never leaves this Mac. Scribe mode stays fully local.")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .onAppear { org = assist.config.orgId }
        .task {
            if assist.claudeBinary == nil { await assist.checkClaude() }
            if assist.hasAPIKey, assist.specs.isEmpty, !assist.config.orgId.isEmpty { await assist.loadCatalog() }
        }
    }

    @ViewBuilder private func listPicker(_ slot: AyaSlot) -> some View {
        let current = assist.config.list(slot)
        LabeledContent {
            if assist.specs.isEmpty {
                Text(current.map { "\($0.label) · \($0.workspaceLabel)" } ?? "—").foregroundStyle(.secondary)
            } else {
                // Record types of all workspaces; the same name can exist in several, so the key includes the workspace.
                AyaSelect(selection: Binding<String>(
                    get: { current.map { "\($0.workspaceId)/\($0.techName)" } ?? "" },
                    set: { key in
                        guard let spec = assist.specs.first(where: { specKey($0) == key }) else { return }
                        Task { await assist.setList(slot, spec: spec) }
                    }),
                    options: [("", "—")] + assist.specs.map { (specKey($0), "\($0.label) · \(assist.workspaceLabel($0.workspaceId))") })
                .frame(maxWidth: 300)
            }
        } label: {
            VStack(alignment: .leading, spacing: 1) {
                Text(slot.title)
                Text(slot.explanation).font(.caption).foregroundStyle(.secondary)
            }
        }
    }

    private func specKey(_ s: AyaRecordSpec) -> String { "\(s.workspaceId ?? "")/\(s.techName)" }

    /// Where filled template tables go: a Markdown field of the meeting.
    @ViewBuilder private var tablesFieldPicker: some View {
        let schema = assist.schemas[AyaSlot.meetings.rawValue]
        let fields = schema?.writableContent.filter { $0.kind == .markdown } ?? []
        let auto = schema.flatMap { s -> String? in
            var c = assist.config; c.meetingTablesField = nil
            return c.tablesField(in: s).map { s.field($0)?.label ?? $0 }
        }
        VStack(alignment: .leading, spacing: 2) {
            LabeledContent("Template tables → field") {
                AyaSelect(selection: $assist.config.meetingTablesField,
                          options: [(String?.none, "Automatic\(auto.map { " (\($0))" } ?? "")")]
                            + fields.map { (String?.some($0.key), $0.label) } + [(String?.some("-"), "Don't save tables")])
                    .frame(maxWidth: 240)
            }
            Text("Filled tables from a template are saved under a heading with the template's name. Automatic uses a field named like Protokoll, Leitfaden or Notes, else the summary.")
                .font(.caption).foregroundStyle(.secondary)
        }
    }

    @ViewBuilder private func relationPicker(_ title: String, slot: AyaSlot, value: Binding<String?>, noneHint: String? = nil) -> some View {
        let relations = assist.schemas[slot.rawValue]?.relations ?? []
        VStack(alignment: .leading, spacing: 2) {
            LabeledContent(title) {
                AyaSelect(selection: value, options: [(String?.none, "None")] + relations.map { (String?.some($0.key), $0.label) })
                    .frame(maxWidth: 240)
            }
            if value.wrappedValue == nil, let noneHint { Text(noneHint).font(.caption).foregroundStyle(.secondary) }
        }
    }
}
