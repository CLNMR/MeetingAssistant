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
                    relationPicker("Recap → client", slot: .recaps, value: $assist.config.recapClientField,
                                   noneHint: "Without a link, earlier recaps are found by the client's name.")
                    relationPicker("Recap → contacts", slot: .recaps, value: $assist.config.recapContactsField)
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
                AyaSelect(selection: Binding<String>(
                    get: { current?.techName ?? "" },
                    set: { tech in
                        guard let spec = assist.specs.first(where: { $0.techName == tech }) else { return }
                        Task { await assist.setList(slot, spec: spec) }
                    }),
                    options: [("", "—")] + assist.specs.map { ($0.techName, "\($0.label) · \(assist.workspaceLabel($0.workspaceId))") })
                .frame(maxWidth: 300)
            }
        } label: {
            VStack(alignment: .leading, spacing: 1) {
                Text(slot.title)
                Text(slot.explanation).font(.caption).foregroundStyle(.secondary)
            }
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
