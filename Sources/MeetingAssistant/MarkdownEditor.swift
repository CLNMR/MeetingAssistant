import AppKit
import MeetingAssistantCore
import SwiftUI

/// A Markdown field of the recap (and of CRM changes): shows the formatted text and switches to an
/// editor with a formatting bar and live highlighting on click (spec 03 §8.2).
struct MarkdownField: View {
    @Binding var text: String
    @State private var editing: Bool
    @State private var hover = false
    @State private var height: CGFloat = MarkdownField.minHeight
    @State private var proxy = MarkdownEditorProxy()

    static let minHeight: CGFloat = 90
    static let maxHeight: CGFloat = 420

    /// Starts in the editor when the text is empty (or when `editing` says so), else in the preview.
    init(text: Binding<String>, editing: Bool? = nil) {
        _text = text
        _editing = State(initialValue: editing ?? text.wrappedValue.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            if editing {
                toolbar
                Divider().overlay(Aya.border)
                MarkdownTextView(text: $text, height: $height, proxy: proxy, onDone: { editing = false })
                    .frame(height: min(max(height, Self.minHeight), Self.maxHeight))
            } else {
                preview
            }
        }
        .background(Aya.card, in: RoundedRectangle(cornerRadius: Aya.radiusControl, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: Aya.radiusControl, style: .continuous)
            .strokeBorder(editing ? Aya.primary.opacity(0.6) : Aya.input, lineWidth: 1))
        .clipShape(RoundedRectangle(cornerRadius: Aya.radiusControl, style: .continuous))
    }

    private var preview: some View {
        ZStack(alignment: .topTrailing) {
            Group {
                if text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                    Text("Empty. Click to write.").font(.system(size: 13)).foregroundStyle(Aya.textSubtle)
                } else {
                    MarkdownPreview(text: text)
                }
            }
            .frame(maxWidth: .infinity, minHeight: 40, alignment: .topLeading)
            .padding(.horizontal, 12).padding(.vertical, 10)
            .contentShape(Rectangle())
            .onTapGesture { editing = true }
            if hover {
                Button { editing = true } label: { Label("Edit", systemImage: "pencil") }
                    .buttonStyle(AyaButtonStyle(kind: .secondary, size: .small))
                    .padding(6)
                    .help("Edit the Markdown text")
            }
        }
        .onHover { hover = $0 }
    }

    private var toolbar: some View {
        HStack(spacing: 2) {
            Menu {
                Button("Heading 1") { proxy.run(.lines(.heading(1))) }
                Button("Heading 2") { proxy.run(.lines(.heading(2))) }
                Button("Heading 3") { proxy.run(.lines(.heading(3))) }
            } label: { Image(systemName: "textformat.size") }
                .menuStyle(.borderlessButton).menuIndicator(.hidden).fixedSize().tint(Aya.textMuted)
                .frame(width: 28, height: 24).help("Heading")
            tool("bold", "Bold (⌘B)") { proxy.run(.wrap("**")) }
            tool("italic", "Italic (⌘I)") { proxy.run(.wrap("*")) }
            tool("strikethrough", "Strikethrough (⇧⌘X)") { proxy.run(.wrap("~~")) }
            tool("chevron.left.forwardslash.chevron.right", "Code (⌘E)") { proxy.run(.wrap("`")) }
            Divider().frame(height: 16).padding(.horizontal, 4)
            tool("list.bullet", "Bulleted list") { proxy.run(.lines(.bullet)) }
            tool("list.number", "Numbered list") { proxy.run(.lines(.numbered)) }
            tool("checklist", "Checklist") { proxy.run(.lines(.task)) }
            tool("text.quote", "Quote") { proxy.run(.lines(.quote)) }
            Spacer(minLength: 8)
            Button("Done") { editing = false }
                .buttonStyle(AyaButtonStyle(kind: .secondary, size: .small))
                .help("Show the formatted text (Esc)")
        }
        .padding(.horizontal, 6).padding(.vertical, 4)
        .background(Aya.field)
    }

    private func tool(_ icon: String, _ help: String, _ action: @escaping () -> Void) -> some View {
        Button(action: action) { Image(systemName: icon).frame(width: 24, height: 22) }
            .buttonStyle(MarkdownToolStyle())
            .help(help)
    }
}

private struct MarkdownToolStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View { ToolBody(configuration: configuration) }

    private struct ToolBody: View {
        let configuration: ButtonStyle.Configuration
        @State private var hover = false
        var body: some View {
            configuration.label
                .font(.system(size: 12, weight: .medium))
                .foregroundStyle(Aya.textMuted)
                .background(configuration.isPressed ? Aya.hover : hover ? Aya.muted : .clear,
                            in: RoundedRectangle(cornerRadius: Aya.radiusControlSm, style: .continuous))
                .contentShape(Rectangle())
                .onHover { hover = $0 }
        }
    }
}

// MARK: - Preview

/// Formatted Markdown: headings, paragraphs, lists (nested, numbered, tasks), quotes, code, tables, rules.
struct MarkdownPreview: View {
    let text: String
    var size: CGFloat = 13

    var body: some View {
        let blocks = Markdown.blocks(text)
        VStack(alignment: .leading, spacing: 4) {
            ForEach(Array(blocks.enumerated()), id: \.offset) { i, b in
                block(b).padding(.top, i > 0 && gapBefore(b, after: blocks[i - 1]) ? 6 : 0)
            }
        }
        .textSelection(.enabled)
    }

    private func gapBefore(_ b: MarkdownBlock, after prev: MarkdownBlock) -> Bool {
        if case .item = b, case .item = prev { return false }
        return true
    }

    @ViewBuilder
    private func block(_ b: MarkdownBlock) -> some View {
        switch b {
        case .heading(let level, let t):
            Text(Self.attributed(t, size: Self.headingSize(level, base: size), weight: .semibold))
                .padding(.top, level <= 2 ? 4 : 2)
        case .paragraph(let t):
            Text(Self.attributed(t, size: size))
        case .item(let depth, let marker, let t):
            HStack(alignment: .firstTextBaseline, spacing: 6) {
                markerView(marker, depth: depth).frame(minWidth: 14, alignment: .trailing)
                Text(Self.attributed(t, size: size))
                    .foregroundStyle(marker == .task(done: true) ? Aya.textMuted : Aya.text)
            }
            .padding(.leading, CGFloat(depth) * 18)
        case .quote(let t):
            HStack(alignment: .top, spacing: 8) {
                RoundedRectangle(cornerRadius: 1.5).fill(Aya.border).frame(width: 3)
                Text(Self.attributed(t, size: size)).foregroundStyle(Aya.textMuted)
            }
            .fixedSize(horizontal: false, vertical: true)
        case .code(let t):
            Text(t).font(.system(size: size - 1, design: .monospaced))
                .padding(8).frame(maxWidth: .infinity, alignment: .leading)
                .background(Aya.field, in: RoundedRectangle(cornerRadius: Aya.radiusControlSm, style: .continuous))
        case .table(let header, let rows):
            table(header, rows)
        case .rule:
            Divider().overlay(Aya.border).padding(.vertical, 4)
        }
    }

    @ViewBuilder
    private func markerView(_ m: MarkdownMarker, depth: Int) -> some View {
        switch m {
        case .bullet:
            Text(["•", "◦", "▪︎"][depth % 3]).font(.system(size: size, weight: .bold)).foregroundStyle(Aya.primary)
        case .number(let n):
            Text("\(n).").font(.system(size: size).monospacedDigit()).foregroundStyle(Aya.textMuted)
        case .task(let done):
            Image(systemName: done ? "checkmark.square.fill" : "square")
                .font(.system(size: size - 1)).foregroundStyle(done ? Aya.primary : Aya.textSubtle)
        }
    }

    private func table(_ header: [String], _ rows: [[String]]) -> some View {
        Grid(alignment: .topLeading, horizontalSpacing: 0, verticalSpacing: 0) {
            GridRow {
                ForEach(Array(header.enumerated()), id: \.offset) { _, c in cell(c, weight: .semibold).background(Aya.muted) }
            }
            ForEach(Array(rows.enumerated()), id: \.offset) { _, r in
                GridRow { ForEach(Array(r.enumerated()), id: \.offset) { _, c in cell(c) } }
            }
        }
        .overlay(RoundedRectangle(cornerRadius: Aya.radiusControlSm, style: .continuous).strokeBorder(Aya.border, lineWidth: 1))
        .clipShape(RoundedRectangle(cornerRadius: Aya.radiusControlSm, style: .continuous))
    }

    private func cell(_ t: String, weight: Font.Weight = .regular) -> some View {
        Text(Self.attributed(t, size: size - 1, weight: weight))
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.horizontal, 8).padding(.vertical, 5)
            .border(Aya.border.opacity(0.7), width: 0.5)
    }

    static func headingSize(_ level: Int, base: CGFloat) -> CGFloat {
        switch level { case 1: return base + 5; case 2: return base + 3; case 3: return base + 1.5; default: return base }
    }

    /// Inline runs as an attributed string (bold, italic, strikethrough, code, links).
    static func attributed(_ text: String, size: CGFloat, weight: Font.Weight = .regular) -> AttributedString {
        var out = AttributedString()
        for r in Markdown.inline(text) {
            var a = AttributedString(r.text)
            var font: Font = r.style.contains(.code)
                ? .system(size: size - 1, design: .monospaced)
                : .system(size: size, weight: r.style.contains(.bold) ? .semibold : weight)
            if r.style.contains(.italic) { font = font.italic() }
            a.font = font
            if r.style.contains(.code) { a.backgroundColor = Aya.muted }
            if r.style.contains(.strike) { a.strikethroughStyle = .single }
            if let l = r.link, let url = URL(string: l) {
                a.link = url; a.foregroundColor = Aya.primary; a.underlineStyle = .single
            }
            out += a
        }
        return out
    }
}

// MARK: - Editor

enum MarkdownCommand {
    case wrap(String)
    case lines(Markdown.LineStyle)
}

/// Lets the formatting bar reach the text view.
final class MarkdownEditorProxy {
    weak var textView: MarkdownNSTextView?
    func run(_ c: MarkdownCommand) { textView?.run(c) }
}

/// Re-measures the text when the width changes (first layout, window resize).
final class MarkdownScrollView: NSScrollView {
    var onWidthChange: (() -> Void)?
    private var lastWidth: CGFloat = 0
    override func layout() {
        super.layout()
        if abs(bounds.width - lastWidth) > 0.5 { lastWidth = bounds.width; onWidthChange?() }
    }
}

final class MarkdownNSTextView: NSTextView {
    var onDone: (() -> Void)?

    func run(_ c: MarkdownCommand) {
        window?.makeFirstResponder(self)
        switch c {
        case .wrap(let m): apply(Markdown.wrap(string, selectedRange(), marker: m))
        case .lines(let s): apply(Markdown.toggleLines(string, selectedRange(), style: s))
        }
    }

    /// Replaces only the changed part, as one undoable edit, then sets the selection.
    func apply(_ r: MarkdownEditResult) {
        let old = string as NSString, new = r.text as NSString
        let n = min(old.length, new.length)
        var p = 0
        while p < n, old.character(at: p) == new.character(at: p) { p += 1 }
        var s = 0
        while s < n - p, old.character(at: old.length - 1 - s) == new.character(at: new.length - 1 - s) { s += 1 }
        let range = NSRange(location: p, length: old.length - p - s)
        let repl = new.substring(with: NSRange(location: p, length: new.length - p - s))
        if range.length > 0 || !repl.isEmpty, shouldChangeText(in: range, replacementString: repl) {
            textStorage?.replaceCharacters(in: range, with: repl)
            didChangeText()
        }
        setSelectedRange(NSRange(location: min(r.selection.location, new.length), length: min(r.selection.length, new.length - min(r.selection.location, new.length))))
    }

    override func performKeyEquivalent(with e: NSEvent) -> Bool {
        guard window?.firstResponder === self else { return super.performKeyEquivalent(with: e) }
        let f = e.modifierFlags.intersection([.command, .shift, .option, .control])
        switch (f, e.charactersIgnoringModifiers?.lowercased()) {
        case ([.command], "b"): run(.wrap("**")); return true
        case ([.command], "i"): run(.wrap("*")); return true
        case ([.command], "e"): run(.wrap("`")); return true
        case ([.command, .shift], "x"): run(.wrap("~~")); return true
        default: return super.performKeyEquivalent(with: e)
        }
    }
}

struct MarkdownTextView: NSViewRepresentable {
    @Binding var text: String
    @Binding var height: CGFloat
    let proxy: MarkdownEditorProxy
    var onDone: () -> Void

    func makeCoordinator() -> Coordinator { Coordinator(self) }

    func makeNSView(context: Context) -> MarkdownScrollView {
        let scroll = MarkdownScrollView()
        scroll.drawsBackground = false
        scroll.hasVerticalScroller = true
        scroll.autohidesScrollers = true
        scroll.borderType = .noBorder

        let tv = MarkdownNSTextView()
        tv.delegate = context.coordinator
        tv.drawsBackground = false
        tv.isRichText = false
        tv.allowsUndo = true
        tv.isAutomaticQuoteSubstitutionEnabled = false
        tv.isAutomaticDashSubstitutionEnabled = false
        tv.isAutomaticTextReplacementEnabled = false
        tv.isAutomaticLinkDetectionEnabled = false
        tv.isContinuousSpellCheckingEnabled = true
        tv.textContainerInset = NSSize(width: 8, height: 8)
        tv.isVerticallyResizable = true
        tv.isHorizontallyResizable = false
        tv.autoresizingMask = [.width]
        tv.textContainer?.widthTracksTextView = true
        tv.textContainer?.containerSize = NSSize(width: 0, height: CGFloat.greatestFiniteMagnitude)
        tv.minSize = .zero
        tv.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        tv.insertionPointColor = NSColor(Aya.primary)
        tv.onDone = onDone
        tv.string = text
        scroll.documentView = tv
        proxy.textView = tv
        context.coordinator.restyle(tv)
        scroll.onWidthChange = { [weak tv, weak coordinator = context.coordinator] in
            if let tv { coordinator?.measure(tv) }
        }
        RunLoop.main.perform {
            tv.window?.makeFirstResponder(tv)
            tv.setSelectedRange(NSRange(location: (tv.string as NSString).length, length: 0))
        }
        return scroll
    }

    func updateNSView(_ scroll: MarkdownScrollView, context: Context) {
        context.coordinator.parent = self
        guard let tv = scroll.documentView as? MarkdownNSTextView else { return }
        tv.onDone = onDone
        proxy.textView = tv
        if tv.string != text, !tv.hasMarkedText() {
            tv.string = text
            context.coordinator.restyle(tv)
            context.coordinator.measure(tv)
        }
    }

    final class Coordinator: NSObject, NSTextViewDelegate {
        var parent: MarkdownTextView
        init(_ p: MarkdownTextView) { parent = p }

        func textDidChange(_ n: Notification) {
            guard let tv = n.object as? MarkdownNSTextView else { return }
            parent.text = tv.string
            restyle(tv)
            measure(tv)
        }

        func textView(_ textView: NSTextView, doCommandBy sel: Selector) -> Bool {
            guard let tv = textView as? MarkdownNSTextView else { return false }
            switch sel {
            case #selector(NSResponder.insertNewline(_:)):
                if let r = Markdown.newline(tv.string, tv.selectedRange()) { tv.apply(r); return true }
            case #selector(NSResponder.insertTab(_:)):
                if let r = Markdown.indent(tv.string, tv.selectedRange(), outdent: false) { tv.apply(r); return true }
            case #selector(NSResponder.insertBacktab(_:)):
                if let r = Markdown.indent(tv.string, tv.selectedRange(), outdent: true) { tv.apply(r); return true }
            case #selector(NSResponder.cancelOperation(_:)):
                tv.onDone?(); return true
            default: break
            }
            return false
        }

        func measure(_ tv: NSTextView) {
            guard let lm = tv.layoutManager, let tc = tv.textContainer else { return }
            lm.ensureLayout(for: tc)
            let h = ceil(lm.usedRect(for: tc).height + tv.textContainerInset.height * 2)
            // Not during the view update that called this; RunLoop (not the main queue) also runs in nested run loops.
            if abs(h - parent.height) > 0.5 { RunLoop.main.perform { self.parent.height = h } }
        }

        /// Highlighting: headings larger, markup characters faint, list markers in the accent colour,
        /// inline styles applied, code and tables in a monospaced font.
        func restyle(_ tv: NSTextView) {
            guard let ts = tv.textStorage else { return }
            let base: CGFloat = 13
            let body = NSFont.systemFont(ofSize: base)
            let mono = NSFont.monospacedSystemFont(ofSize: base - 1, weight: .regular)
            let text = NSColor(Aya.text), subtle = NSColor(Aya.textSubtle), muted = NSColor(Aya.textMuted)
            let primary = NSColor(Aya.primary), codeBg = NSColor(Aya.muted)
            let para = NSMutableParagraphStyle(); para.lineSpacing = 2
            let all = NSRange(location: 0, length: ts.length)
            let fm = NSFontManager.shared

            ts.beginEditing()
            ts.setAttributes([.font: body, .foregroundColor: text, .paragraphStyle: para], range: all)
            for sp in Markdown.highlight(tv.string) {
                let r = sp.range
                guard r.location >= 0, r.location + r.length <= ts.length, r.length > 0 else { continue }
                switch sp.kind {
                case .heading(let l):
                    ts.addAttribute(.font, value: NSFont.systemFont(ofSize: MarkdownPreview.headingSize(l, base: base), weight: .semibold), range: r)
                case .quote:
                    ts.addAttribute(.foregroundColor, value: muted, range: r)
                case .codeBlock, .table:
                    ts.addAttribute(.font, value: mono, range: r)
                case .syntax:
                    ts.addAttribute(.foregroundColor, value: subtle, range: r)
                case .listMarker:
                    ts.addAttribute(.foregroundColor, value: primary, range: r)
                    ts.addAttribute(.font, value: NSFont.systemFont(ofSize: base, weight: .semibold), range: r)
                case .styled(let st, let link):
                    let cur = (ts.attribute(.font, at: r.location, effectiveRange: nil) as? NSFont) ?? body
                    var f = st.contains(.code) ? NSFont.monospacedSystemFont(ofSize: cur.pointSize - 1, weight: .regular) : cur
                    if st.contains(.bold) { f = fm.convert(f, toHaveTrait: .boldFontMask) }
                    if st.contains(.italic) { f = fm.convert(f, toHaveTrait: .italicFontMask) }
                    ts.addAttribute(.font, value: f, range: r)
                    if st.contains(.code) { ts.addAttribute(.backgroundColor, value: codeBg, range: r) }
                    if st.contains(.strike) { ts.addAttribute(.strikethroughStyle, value: NSUnderlineStyle.single.rawValue, range: r) }
                    if link {
                        ts.addAttribute(.foregroundColor, value: primary, range: r)
                        ts.addAttribute(.underlineStyle, value: NSUnderlineStyle.single.rawValue, range: r)
                    }
                }
            }
            ts.endEditing()
            tv.typingAttributes = [.font: body, .foregroundColor: text, .paragraphStyle: para]
        }
    }
}
