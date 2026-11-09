import AppKit
import SwiftUI

// Aya's select (packages/ui-components/src/form/shadcn/select.tsx): a 32 px field-style
// trigger with a muted chevron, opening a raised list with 8 px corners, 6 px item
// highlights and a check mark on the chosen item. The list lives in its own borderless
// child window so it can overlap anything, like Radix's portal.

struct AyaSelect<T: Hashable>: View {
    @Binding var selection: T
    let options: [(value: T, title: String)]
    var placeholder = "Select…"

    @Environment(\.isEnabled) private var enabled
    @State private var anchor = AyaAnchor()
    @State private var hover = false
    @State private var open = false

    private var current: Int? { options.firstIndex { $0.value == selection } }

    var body: some View {
        Button(action: toggle) {
            HStack(spacing: 6) {
                Text(current.map { options[$0].title } ?? placeholder)
                    .foregroundStyle(current == nil ? Aya.textMuted : Aya.text)
                    .lineLimit(1).truncationMode(.tail)
                Spacer(minLength: 4)
                Image(systemName: "chevron.down")
                    .font(.system(size: 10, weight: .semibold))
                    .foregroundStyle(Aya.textSubtle)
            }
            .font(.system(size: 13))
            .padding(.horizontal, 12)
            .frame(minHeight: Aya.controlHeight)
            .background(hover && enabled ? Aya.hover.opacity(0.7) : Aya.field,
                        in: RoundedRectangle(cornerRadius: Aya.radiusControl, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: Aya.radiusControl, style: .continuous)
                .strokeBorder(open ? Aya.primary.opacity(0.7) : Aya.input, lineWidth: 1))
            .shadow(color: .black.opacity(0.04), radius: 1, y: 1)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .opacity(enabled ? 1 : 0.5)
        .onHover { hover = $0 }
        .background(AyaAnchorView(anchor: anchor))
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(.isButton)
    }

    private func toggle() {
        // A click on the trigger while the list is open first closes it (the list loses key
        // status), so ignore the click that caused that instead of reopening right away.
        if Date().timeIntervalSince(AyaPopup.lastClose) < 0.25 { return }
        guard let view = anchor.view, let window = view.window else { return }
        let rect = window.convertToScreen(view.convert(view.bounds, to: nil))
        let items = options.enumerated().map { (index: $0.offset, title: $0.element.title) }
        open = true
        AyaPopup.show(below: rect, parent: window, count: items.count,
                      content: AyaSelectList(items: items, selected: current) { i in
                          selection = options[i].value
                          AyaPopup.close()
                      },
                      onClose: { open = false })
    }
}

/// Keeps a reference to the trigger's NSView so its screen position can be read on click.
final class AyaAnchor { weak var view: NSView? }

private struct AyaAnchorView: NSViewRepresentable {
    let anchor: AyaAnchor
    func makeNSView(context: Context) -> NSView { let v = NSView(); anchor.view = v; return v }
    func updateNSView(_ nsView: NSView, context: Context) { anchor.view = nsView }
}

// MARK: - List

private struct AyaSelectList: View {
    let items: [(index: Int, title: String)]
    let selected: Int?
    let pick: (Int) -> Void

    @State private var highlighted: Int?
    @FocusState private var focused: Bool

    var body: some View {
        ScrollViewReader { proxy in
            ScrollView {
                VStack(spacing: 0) {
                    ForEach(items, id: \.index) { item in
                        row(item).id(item.index)
                    }
                }
                .padding(4)
            }
            .scrollIndicators(.automatic)
            .onAppear {
                highlighted = selected ?? items.first?.index
                if let selected { proxy.scrollTo(selected, anchor: .center) }
                focused = true
            }
            .onChange(of: highlighted) { _, h in if let h { proxy.scrollTo(h) } }
        }
        .background(Aya.popover, in: RoundedRectangle(cornerRadius: Aya.radiusControl, style: .continuous))
        .clipShape(RoundedRectangle(cornerRadius: Aya.radiusControl, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: Aya.radiusControl, style: .continuous).strokeBorder(Aya.border, lineWidth: 1))
        .focusable()
        .focused($focused)
        .focusEffectDisabled()
        .onKeyPress(.downArrow) { move(1); return .handled }
        .onKeyPress(.upArrow) { move(-1); return .handled }
        .onKeyPress(.return) { if let h = highlighted { pick(h) }; return .handled }
        .onKeyPress(.space) { if let h = highlighted { pick(h) }; return .handled }
        .onKeyPress(.escape) { AyaPopup.close(); return .handled }
        .onKeyPress(phases: .down) { press in
            // Type-ahead: jump to the first item starting with the typed letter.
            guard let c = press.characters.first, c.isLetter || c.isNumber else { return .ignored }
            let s = String(c).lowercased()
            if let hit = items.first(where: { $0.title.lowercased().hasPrefix(s) }) { highlighted = hit.index }
            return .handled
        }
    }

    private func row(_ item: (index: Int, title: String)) -> some View {
        let on = highlighted == item.index
        return HStack(spacing: 8) {
            Text(item.title).lineLimit(1).truncationMode(.tail)
            Spacer(minLength: 8)
            Image(systemName: "checkmark")
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(Aya.textMuted)
                .opacity(item.index == selected ? 1 : 0)
        }
        .font(.system(size: 13))
        .foregroundStyle(Aya.text)
        .padding(.horizontal, 8)
        .frame(height: AyaPopup.rowHeight)
        .background(on ? Aya.hover : .clear, in: RoundedRectangle(cornerRadius: Aya.radiusControlSm, style: .continuous))
        .contentShape(Rectangle())
        .onHover { if $0 { highlighted = item.index } }
        .onTapGesture { pick(item.index) }
    }

    private func move(_ d: Int) {
        guard !items.isEmpty else { return }
        let pos = items.firstIndex { $0.index == highlighted } ?? (d > 0 ? -1 : items.count)
        highlighted = items[max(0, min(items.count - 1, pos + d))].index
    }
}

// MARK: - Window

private final class AyaPopupPanel: NSPanel {
    override var canBecomeKey: Bool { true }
}

/// The one open dropdown list. It closes when an item is picked, on Escape, or as soon as
/// the user clicks anywhere else (the list window loses key status).
@MainActor enum AyaPopup {
    static let rowHeight: CGFloat = 28
    static let maxHeight: CGFloat = 320
    static private(set) var lastClose = Date.distantPast
    private static var panel: AyaPopupPanel?
    private static var onClose: (() -> Void)?
    private static var observer: NSObjectProtocol?

    static func show<V: View>(below trigger: NSRect, parent: NSWindow, count: Int, content: V, onClose: @escaping () -> Void) {
        close()
        let width = max(trigger.width, 180)
        let height = min(CGFloat(max(count, 1)) * rowHeight + 8, maxHeight)
        let screen = (parent.screen ?? NSScreen.main)?.visibleFrame ?? .infinite
        var y = trigger.minY - 4 - height
        if y < screen.minY, trigger.maxY + 4 + height <= screen.maxY { y = trigger.maxY + 4 }
        let x = min(max(trigger.minX, screen.minX + 4), screen.maxX - width - 4)

        let p = AyaPopupPanel(contentRect: NSRect(x: x, y: y, width: width, height: height),
                              styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        p.isOpaque = false
        p.backgroundColor = .clear
        p.hasShadow = true
        p.level = parent.level
        p.isReleasedWhenClosed = false
        p.appearance = parent.effectiveAppearance
        let host = NSHostingView(rootView: content.frame(width: width, height: height))
        host.frame = NSRect(x: 0, y: 0, width: width, height: height)
        p.contentView = host
        parent.addChildWindow(p, ordered: .above)
        p.makeKeyAndOrderFront(nil)
        p.invalidateShadow()

        panel = p
        Self.onClose = onClose
        observer = NotificationCenter.default.addObserver(forName: NSWindow.didResignKeyNotification, object: p, queue: .main) { _ in
            MainActor.assumeIsolated { close() }
        }
    }

    static func close() {
        guard let p = panel else { return }
        panel = nil
        if let observer { NotificationCenter.default.removeObserver(observer) }
        observer = nil
        let parent = p.parent
        let wasKey = p.isKeyWindow
        parent?.removeChildWindow(p)
        p.orderOut(nil)
        if wasKey { parent?.makeKey() }   // picked or Escape: hand focus back to the window
        lastClose = Date()
        let cb = onClose
        onClose = nil
        cb?()
    }
}
