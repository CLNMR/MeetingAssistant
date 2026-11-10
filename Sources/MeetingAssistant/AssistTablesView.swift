import MeetingAssistantCore
import SwiftUI

// The template's fill-in tables: a compact progress card while the meeting runs, the full grid
// in a sheet (live) and in the review, where cells can be corrected before saving to Aya.

/// One editable grid. Name columns are text; answer columns are fields that commit on Return or
/// when focus leaves. Values from Claude are tinted, with the quote as tooltip.
struct TemplateTableGrid: View {
    let table: TemplateTable
    var onlyAnswered = false
    var changed: Set<String> = []
    let onSet: (_ row: String, _ column: String, _ value: String) -> Void

    private var rows: [TemplateTable.Row] { onlyAnswered ? table.rows.filter(table.isFilled) : table.rows }

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 8) {
                ForEach(table.columns.indices, id: \.self) { i in
                    Text(table.columns[i])
                        .font(.system(size: 11, weight: .semibold)).foregroundStyle(Aya.textMuted)
                        .lineLimit(2)
                        .frame(maxWidth: table.answerColumns.contains(i) ? 180 : .infinity, alignment: .leading)
                }
            }
            .padding(.horizontal, 8).padding(.vertical, 6)
            Divider().overlay(Aya.border)
            LazyVStack(spacing: 0) {
                ForEach(rows) { r in
                    row(r)
                    Divider().overlay(Aya.border.opacity(0.5))
                }
            }
            if rows.isEmpty {
                Text("Nothing answered yet.").font(.system(size: 12)).foregroundStyle(Aya.textSubtle).padding(10)
            }
        }
        .background(Aya.card, in: RoundedRectangle(cornerRadius: Aya.radiusControl, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: Aya.radiusControl, style: .continuous).strokeBorder(Aya.border, lineWidth: 1))
    }

    private func row(_ r: TemplateTable.Row) -> some View {
        HStack(alignment: .center, spacing: 8) {
            ForEach(table.columns.indices, id: \.self) { i in
                let col = table.columns[i]
                if table.answerColumns.contains(i) {
                    let fromClaude = !r.cells[i].isEmpty && !r.userSet.contains(col)
                    CellField(value: r.cells[i], fromClaude: fromClaude) { onSet(r.id, col, $0) }
                        .help(r.evidence[col].map { "“\($0)”" } ?? "")
                        .frame(maxWidth: 180)
                } else if let base = r.openKeys[i] {
                    // "Eigener Vorschlag:" — the proposal itself is filled in.
                    HStack(spacing: 4) {
                        Text(base).font(.system(size: 12)).foregroundStyle(Aya.textMuted).fixedSize()
                        CellField(value: String(r.cells[i].dropFirst(base.count)).trimmingCharacters(in: .whitespaces),
                                  fromClaude: false) { onSet(r.id, col, $0) }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                } else {
                    Text(r.cells[i]).font(.system(size: 12)).foregroundStyle(i == table.keyColumns.first ? Aya.textMuted : Aya.text)
                        .lineLimit(2).frame(maxWidth: .infinity, alignment: .leading)
                }
            }
        }
        .padding(.horizontal, 8).padding(.vertical, 3)
        .background(changed.contains("t:\(r.id)") ? Aya.agent.opacity(0.10) : .clear)
    }
}

/// A text field that reports its value once: on Return or when it loses focus.
private struct CellField: View {
    let value: String
    let fromClaude: Bool
    let commit: (String) -> Void
    @State private var text = ""
    @FocusState private var focused: Bool

    var body: some View {
        TextField("", text: $text, prompt: Text("—"))
            .textFieldStyle(.plain)
            .font(.system(size: 12))
            .padding(.horizontal, 6).padding(.vertical, 4)
            .background(fromClaude ? Aya.agent.opacity(0.10) : Aya.field, in: RoundedRectangle(cornerRadius: Aya.radiusTag, style: .continuous))
            .focused($focused)
            .onSubmit(save)
            .onChange(of: focused) { _, f in if !f { save() } }
            .onAppear { text = value }
            .onChange(of: value) { _, v in if !focused { text = v } }
    }

    private func save() {
        let t = text.trimmingCharacters(in: .whitespaces)
        if t != value { commit(t) }
    }
}

/// Progress of the tables during the meeting, with the latest answers.
struct LiveTablesCard: View {
    @ObservedObject var assist: AssistController
    @State private var open: String?

    var body: some View {
        let tables = assist.state.tables ?? []
        VStack(alignment: .leading, spacing: 8) {
            AyaEyebrow(text: "Tables", icon: "tablecells", color: Aya.agentTextMid)
            HStack(alignment: .top, spacing: 10) {
                ForEach(tables) { t in tableSummary(t) }
            }
        }
        .padding(12)
        .ayaSurface()
        .sheet(item: Binding(get: { open.map { IDBox(id: $0) } }, set: { open = $0?.id })) { box in
            LiveTableSheet(assist: assist, tableID: box.id)
        }
    }

    private func tableSummary(_ t: TemplateTable) -> some View {
        let latest = t.rows.filter(t.isFilled).suffix(3)
        return Button { open = t.id } label: {
            VStack(alignment: .leading, spacing: 5) {
                HStack {
                    Text(t.section.isEmpty ? t.id : t.section).font(.system(size: 12, weight: .semibold)).lineLimit(1)
                    Spacer()
                    Text("\(t.filledCount)/\(t.rows.count)").font(.system(size: 11).monospacedDigit()).foregroundStyle(Aya.textMuted)
                }
                ProgressView(value: Double(t.filledCount), total: Double(max(t.rows.count, 1))).tint(Aya.agent)
                ForEach(latest) { r in
                    let answers = t.answerColumns.map { r.cells[$0] }.filter { !$0.isEmpty }.joined(separator: " · ")
                    Text("\(t.label(r)) → \(answers)").font(.system(size: 11)).foregroundStyle(Aya.textMuted).lineLimit(1)
                        .modifier(Highlight(on: assist.state.changed.contains("t:\(r.id)")))
                }
            }
            .padding(10)
            .frame(maxWidth: .infinity, alignment: .topLeading)
            .background(Aya.page.opacity(0.5), in: RoundedRectangle(cornerRadius: Aya.radiusContainer, style: .continuous))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help("Open the table to see and correct all cells")
    }
}

private struct IDBox: Identifiable { let id: String }

/// The full table during the meeting. Corrections go to Claude, which keeps them.
private struct LiveTableSheet: View {
    @ObservedObject var assist: AssistController
    let tableID: String
    @Environment(\.dismiss) private var dismiss
    @State private var onlyAnswered = false

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            if let t = assist.state.tables?.first(where: { $0.id == tableID }) {
                TableHeader(table: t, onlyAnswered: $onlyAnswered)
                ScrollView {
                    TemplateTableGrid(table: t, onlyAnswered: onlyAnswered, changed: assist.state.changed) { row, col, v in
                        assist.setTableCell(row: row, column: col, value: v)
                    }
                }
            }
            HStack { Spacer(); Button("Close") { dismiss() }.buttonStyle(AyaButtonStyle(kind: .primary)).keyboardShortcut(.defaultAction) }
        }
        .padding(20)
        .frame(minWidth: 760, minHeight: 560)
        .background(Aya.page)
    }
}

private struct TableHeader: View {
    let table: TemplateTable
    @Binding var onlyAnswered: Bool
    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text(table.section.isEmpty ? table.id : table.section).font(.system(size: 15, weight: .semibold))
                AyaTag(text: "\(table.filledCount)/\(table.rows.count) answered", color: Aya.agentTextMid)
                Spacer()
                Toggle("Only answered rows", isOn: $onlyAnswered).toggleStyle(.checkbox).font(.system(size: 12))
            }
            if !table.prompt.isEmpty {
                Text(table.prompt).font(.system(size: 12)).foregroundStyle(Aya.textMuted).fixedSize(horizontal: false, vertical: true)
            }
        }
    }
}

/// The tables in the review: completed from the final transcript, editable, saved with the recap.
struct ReviewTablesSection: View {
    @ObservedObject var assist: AssistController
    @State private var showAll: Set<String> = []

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .center) {
                VStack(alignment: .leading, spacing: 2) {
                    Text("Template tables").font(.system(size: 16, weight: .semibold))
                    Text(assist.tablesFieldLabel.map { "Saved with the recap into “\($0)”. Long tables only with the rows that were answered." }
                         ?? "Not saved to Aya (Settings › Assist › Template tables).")
                        .font(.system(size: 12)).foregroundStyle(Aya.textMuted)
                }
                Spacer()
                Button { Task { await assist.completeTables() } } label: {
                    Label(assist.record?.tables == nil ? "Complete from Transcript" : "Complete Again", systemImage: "sparkles")
                }
                .buttonStyle(AyaButtonStyle(kind: assist.record?.tables == nil ? .agent : .secondary, pill: true))
                .disabled(assist.tablesState.isWorking)
                .help("Claude fills the tables from the final transcript. Cells you edited stay as they are.")
            }
            StepLine(step: assist.tablesState)
            ForEach(assist.reviewTables) { t in
                let long = t.rows.count > TemplateTable.writeAllRowsUpTo
                VStack(alignment: .leading, spacing: 8) {
                    TableHeader(table: t, onlyAnswered: Binding(get: { long && !showAll.contains(t.id) },
                                                                 set: { if $0 { showAll.remove(t.id) } else { showAll.insert(t.id) } }))
                    TemplateTableGrid(table: t, onlyAnswered: long && !showAll.contains(t.id)) { row, col, v in
                        assist.setReviewCell(row: row, column: col, value: v)
                    }
                }
            }
        }
        .padding(18)
        .ayaSurface()
    }
}
