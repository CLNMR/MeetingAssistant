import AppKit
import SwiftUI

/// The Aya look, taken from the Aya web app's design system
/// (`packages/ui-components/src/styles/theme.css` and `tokens.css`):
/// the shadcn palette for light and dark, the blue brand gradient, the teal agent accent
/// with its glow, 8 px controls, 16 px surfaces and pill-shaped chips.
enum Aya {
    // MARK: Palette (light / dark)

    static let page = dyn(0xF7F8F9, 0x0A0A0A)
    static let card = dyn(0xFFFFFF, 0x0E0E0E)
    static let popover = dyn(0xFFFFFF, 0x111111)
    static let rail = dyn(0xFFFFFF, 0x0F0F0F)
    static let text = dyn(rgba(24, 24, 27, 1), rgba(255, 255, 255, 0.96))
    static let textMuted = dyn(rgba(24, 24, 27, 0.76), rgba(255, 255, 255, 0.68))
    static let textSubtle = dyn(rgba(24, 24, 27, 0.55), rgba(255, 255, 255, 0.45))
    static let muted = dyn(0xF1F1F3, 0x27272A)
    static let hover = dyn(0xE9E9ED, 0x27272A)
    static let field = dyn(rgba(24, 24, 27, 0.04), rgba(255, 255, 255, 0.04))
    static let border = dyn(rgba(24, 24, 27, 0.13), rgba(255, 255, 255, 0.10))
    static let input = dyn(rgba(24, 24, 27, 0.22), rgba(255, 255, 255, 0.14))
    static let primary = dyn(0x225FE7, 0x2563EB)
    static let destructive = dyn(0xCE0D18, 0xF87171)
    static let destructiveFg = dyn(0xFFFFFF, 0x0A0A0A)
    static let success = dyn(0x1E8A4C, 0x4CC97A)
    static let warning = dyn(0x9A6A12, 0xE5B04A)

    /// Brand gradient of the logo and app icon.
    static let brandStart = Color(hex: 0x0F7DFF)
    static let brandEnd = Color(hex: 0x0EAFFF)
    static var brand: LinearGradient {
        LinearGradient(colors: [brandStart, brandEnd], startPoint: .topLeading, endPoint: .bottomTrailing)
    }

    /// Agent accent (Aya's brand teal). The text steps mix it toward the text color,
    /// so they keep their contrast in both themes, exactly like `--agent-text-*`.
    static let agent = Color(hex: 0x35D2C1)
    static let agentTextMid = mixDyn(0x35D2C1, 0.55)
    static let agentTextBright = mixDyn(0x35D2C1, 0.25)
    static let agentForeground = Color(.sRGB, red: 0, green: 0, blue: 0, opacity: 0.78)
    static let agentGlowSoft = agent.opacity(0.15)
    static let agentGlow = agent.opacity(0.65)

    // MARK: Radii and metrics

    static let radiusTag: CGFloat = 4
    static let radiusControlSm: CGFloat = 6
    static let radiusControl: CGFloat = 8
    static let radiusContainer: CGFloat = 10
    static let radiusOverlay: CGFloat = 14
    static let radiusSurface: CGFloat = 16
    static let controlHeight: CGFloat = 32

    // MARK: Helpers

    private struct RGBA { var r, g, b, a: CGFloat }
    private static func rgba(_ r: CGFloat, _ g: CGFloat, _ b: CGFloat, _ a: CGFloat) -> RGBA { RGBA(r: r / 255, g: g / 255, b: b / 255, a: a) }
    private static func hex(_ v: UInt32) -> RGBA {
        RGBA(r: CGFloat((v >> 16) & 0xFF) / 255, g: CGFloat((v >> 8) & 0xFF) / 255, b: CGFloat(v & 0xFF) / 255, a: 1)
    }
    private static func ns(_ c: RGBA) -> NSColor { NSColor(srgbRed: c.r, green: c.g, blue: c.b, alpha: c.a) }

    private static func dyn(_ light: UInt32, _ dark: UInt32) -> Color { dyn(hex(light), hex(dark)) }
    private static func dyn(_ light: RGBA, _ dark: RGBA) -> Color {
        Color(nsColor: NSColor(name: nil) { ap in
            ap.bestMatch(from: [.darkAqua, .vibrantDark, .accessibilityHighContrastDarkAqua]) != nil ? ns(dark) : ns(light)
        })
    }
    /// `color-mix(in srgb, accent p, var(--foreground))`.
    private static func mixDyn(_ accent: UInt32, _ p: CGFloat) -> Color {
        let a = hex(accent)
        func mix(_ f: RGBA) -> RGBA { RGBA(r: a.r * p + f.r * (1 - p), g: a.g * p + f.g * (1 - p), b: a.b * p + f.b * (1 - p), a: 1) }
        return dyn(mix(rgba(24, 24, 27, 1)), mix(rgba(245, 245, 245, 1)))
    }
}

extension Color {
    init(hex: UInt32, opacity: Double = 1) {
        self.init(.sRGB, red: Double((hex >> 16) & 0xFF) / 255, green: Double((hex >> 8) & 0xFF) / 255,
                  blue: Double(hex & 0xFF) / 255, opacity: opacity)
    }
}

// MARK: - Logo

/// The Aya logo mark (the rounded triangle), from `apps/web/public/logo.svg`.
struct AyaMark: Shape {
    func path(in rect: CGRect) -> Path {
        var p = Path()
        // Absolute coordinates of the SVG path (relative segments resolved).
        var cur = CGPoint(x: 323.0818, y: 113.0949)
        p.move(to: cur)
        func l(_ dx: CGFloat, _ dy: CGFloat) { cur = CGPoint(x: cur.x + dx, y: cur.y + dy); p.addLine(to: cur) }
        func c(_ x1: CGFloat, _ y1: CGFloat, _ x2: CGFloat, _ y2: CGFloat, _ x: CGFloat, _ y: CGFloat) {
            let c1 = CGPoint(x: cur.x + x1, y: cur.y + y1), c2 = CGPoint(x: cur.x + x2, y: cur.y + y2)
            cur = CGPoint(x: cur.x + x, y: cur.y + y)
            p.addCurve(to: cur, control1: c1, control2: c2)
        }
        l(105.2897, 210.5788)
        c(29.3945, 58.7888, -25.6772, 124.2649, -88.6331, 105.3781)
        l(-40.636, -12.1908)
        c(-28.1158, -8.4347, -58.0886, -8.4347, -86.2044, 0)
        l(-40.6364, 12.1909)
        c(-62.9559, 18.8868, -118.0275, -46.5891, -88.6332, -105.3779)
        l(105.2893, -210.579)
        c(27.6393, -55.2786, 106.5247, -55.2787, 134.1641, -0.0001)
        p.closeSubpath()
        let b = p.boundingRect
        let s = min(rect.width / b.width, rect.height / b.height)
        return p.applying(CGAffineTransform(translationX: -b.midX, y: -b.midY))
            .applying(CGAffineTransform(scaleX: s, y: s))
            .applying(CGAffineTransform(translationX: rect.midX, y: rect.midY))
    }
}

/// The blue rounded square with the white mark, as at the top of Aya's sidebar.
struct AyaLogoTile: View {
    var size: CGFloat = 32
    var body: some View {
        RoundedRectangle(cornerRadius: size * 0.28, style: .continuous)
            .fill(Aya.brand)
            .overlay(AyaMark().fill(.white).frame(width: size * 0.5, height: size * 0.5).offset(y: size * 0.02))
            .frame(width: size, height: size)
            .shadow(color: Aya.brandStart.opacity(0.25), radius: 4, y: 1)
    }
}

// MARK: - Surfaces

extension View {
    /// A raised card: white (or near-black) with a hairline border, 16 px corners.
    func ayaSurface(radius: CGFloat = Aya.radiusSurface, fill: Color = Aya.card, shadow: Bool = true) -> some View {
        background(fill, in: RoundedRectangle(cornerRadius: radius, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: radius, style: .continuous).strokeBorder(Aya.border, lineWidth: 1))
            .shadow(color: .black.opacity(shadow ? 0.05 : 0), radius: 1.5, y: 1)
    }

    /// The agent's lit surface: teal hairline and a soft teal glow (`--agent-shadow-recording`).
    func ayaAgentSurface(radius: CGFloat = Aya.radiusSurface, active: Bool = true) -> some View {
        background(Aya.card, in: RoundedRectangle(cornerRadius: radius, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: radius, style: .continuous)
                .strokeBorder(active ? Aya.agent.opacity(0.6) : Aya.border, lineWidth: 1))
            .shadow(color: active ? Aya.agentGlow.opacity(0.35) : .black.opacity(0.05), radius: active ? 14 : 1.5, y: active ? 0 : 1)
    }

    /// Page background that runs under the transparent title bar.
    func ayaPage() -> some View {
        background(Aya.page.ignoresSafeArea())
            .foregroundStyle(Aya.text)
            .tint(Aya.primary)
    }
}

/// The soft blue/teal glow behind Aya's home screen.
struct AyaBackdrop: View {
    var body: some View {
        GeometryReader { g in
            let r = max(g.size.width, g.size.height)
            ZStack {
                RadialGradient(colors: [Aya.brandEnd.opacity(0.10), .clear], center: UnitPoint(x: 0.35, y: 0.6),
                               startRadius: 0, endRadius: r * 0.45)
                RadialGradient(colors: [Aya.agent.opacity(0.09), .clear], center: UnitPoint(x: 0.9, y: 0.2),
                               startRadius: 0, endRadius: r * 0.35)
            }
        }
        .allowsHitTesting(false)
    }
}

/// First letter or digit of a name, for avatar tiles ("🤝 Muster" → "M").
func ayaInitial(_ s: String) -> String {
    s.first(where: { $0.isLetter || $0.isNumber }).map { String($0).uppercased() } ?? "•"
}

// MARK: - Type

/// Small uppercase label with wide tracking, as Aya's eyebrows and the "LISTENING" label.
struct AyaEyebrow: View {
    let text: String
    var icon: String? = nil
    var color: Color = Aya.textMuted
    var body: some View {
        HStack(spacing: 6) {
            if let icon { Image(systemName: icon).font(.system(size: 11, weight: .semibold)) }
            Text(text.uppercased()).font(.system(size: 11, weight: .semibold)).tracking(1.2)
        }
        .foregroundStyle(color)
    }
}

/// Small tinted tag ("1 of 11"): primary text on a 10 % primary fill.
struct AyaTag: View {
    let text: String
    var color: Color = Aya.primary
    var icon: String? = nil
    var body: some View {
        HStack(spacing: 4) {
            if let icon { Image(systemName: icon).font(.system(size: 10, weight: .semibold)) }
            Text(text)
        }
        .font(.system(size: 11, weight: .medium))
        .foregroundStyle(color)
        .padding(.horizontal, 7).padding(.vertical, 3)
        .background(color.opacity(0.12), in: RoundedRectangle(cornerRadius: Aya.radiusControlSm, style: .continuous))
    }
}

// MARK: - Buttons

struct AyaButtonStyle: ButtonStyle {
    enum Kind { case primary, secondary, ghost, destructive, agent }
    enum Size { case small, regular, large }
    var kind: Kind = .secondary
    var size: Size = .regular
    var pill = false

    func makeBody(configuration: Configuration) -> some View {
        AyaButtonBody(configuration: configuration, kind: kind, size: size, pill: pill)
    }
}

private struct AyaButtonBody: View {
    let configuration: ButtonStyle.Configuration
    let kind: AyaButtonStyle.Kind
    let size: AyaButtonStyle.Size
    let pill: Bool
    @Environment(\.isEnabled) private var enabled
    @State private var hovering = false

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: pill ? 999 : Aya.radiusControl, style: .continuous)
        configuration.label
            .labelStyle(AyaLabelStyle())
            .font(.system(size: size == .small ? 12 : 13, weight: kind == .ghost || kind == .secondary ? .medium : .semibold))
            .lineLimit(1)
            .foregroundStyle(foreground)
            .padding(.horizontal, size == .small ? 9 : size == .large ? 18 : (pill ? 14 : 12))
            .frame(height: size == .small ? 26 : size == .large ? 40 : Aya.controlHeight)
            .background(fill, in: shape)
            .overlay(shape.strokeBorder(kind == .secondary ? Aya.border : .clear, lineWidth: 1))
            .shadow(color: kind == .agent && enabled ? Aya.agentGlow.opacity(0.55) : .clear, radius: 7)
            .contentShape(shape)
            .opacity(enabled ? (configuration.isPressed ? 0.8 : 1) : 0.45)
            .onHover { hovering = $0 }
            .animation(.easeOut(duration: 0.12), value: hovering)
    }

    private var foreground: Color {
        switch kind {
        case .primary: return .white
        case .destructive: return Aya.destructiveFg
        case .agent: return Aya.agentForeground
        case .secondary, .ghost: return Aya.text
        }
    }

    private var fill: Color {
        let h = hovering && enabled
        switch kind {
        case .primary: return h ? Aya.primary.opacity(0.88) : Aya.primary
        case .destructive: return h ? Aya.destructive.opacity(0.88) : Aya.destructive
        case .agent: return h ? Aya.agent.opacity(0.85) : Aya.agent
        case .secondary: return h ? Aya.hover : Aya.card
        case .ghost: return h ? Aya.hover : .clear
        }
    }
}

/// Icon and title with Aya's 6 px gap.
struct AyaLabelStyle: LabelStyle {
    func makeBody(configuration: Configuration) -> some View {
        HStack(spacing: 6) {
            configuration.icon.font(.system(size: 12, weight: .semibold))
            configuration.title
        }
    }
}

/// Square icon button with a hairline border (the agent panel's header buttons).
struct AyaIconButtonStyle: ButtonStyle {
    var size: CGFloat = 32
    var bordered = true
    var active = false
    func makeBody(configuration: Configuration) -> some View { IconBody(configuration: configuration, size: size, bordered: bordered, active: active) }

    struct IconBody: View {
        let configuration: ButtonStyle.Configuration
        let size: CGFloat
        let bordered: Bool
        let active: Bool
        @Environment(\.isEnabled) private var enabled
        @State private var hovering = false
        var body: some View {
            let shape = RoundedRectangle(cornerRadius: Aya.radiusControl, style: .continuous)
            configuration.label
                .labelStyle(.iconOnly)
                .font(.system(size: 14, weight: .medium))
                .foregroundStyle(active ? Aya.primary : Aya.textMuted)
                .frame(width: size, height: size)
                .background(active ? Aya.primary.opacity(0.12) : (hovering && enabled ? Aya.hover : (bordered ? Aya.card : .clear)), in: shape)
                .overlay(shape.strokeBorder(bordered && !active ? Aya.border : .clear, lineWidth: 1))
                .contentShape(shape)
                .opacity(enabled ? (configuration.isPressed ? 0.75 : 1) : 0.4)
                .onHover { hovering = $0 }
        }
    }
}

/// Pill chip with a hairline border, like the agent's suggestion chips.
struct AyaChipStyle: ButtonStyle {
    var selected = false
    func makeBody(configuration: Configuration) -> some View { ChipBody(configuration: configuration, selected: selected) }

    struct ChipBody: View {
        let configuration: ButtonStyle.Configuration
        let selected: Bool
        @State private var hovering = false
        var body: some View {
            configuration.label
                .labelStyle(AyaLabelStyle())
                .font(.system(size: 13))
                .foregroundStyle(Aya.text)
                .padding(.horizontal, 12).padding(.vertical, 6)
                .background(selected ? Aya.agent.opacity(0.14) : (hovering ? Aya.hover : Aya.card), in: Capsule())
                .overlay(Capsule().strokeBorder(selected ? Aya.agent.opacity(0.6) : Aya.border, lineWidth: 1))
                .contentShape(Capsule())
                .opacity(configuration.isPressed ? 0.8 : 1)
                .onHover { hovering = $0 }
        }
    }
}

// MARK: - Inputs

/// Text field with Aya's 8 px rounded outline.
struct AyaFieldStyle: TextFieldStyle {
    var icon: String? = nil
    func _body(configuration: TextField<_Label>) -> some View {
        HStack(spacing: 6) {
            if let icon { Image(systemName: icon).font(.system(size: 12)).foregroundStyle(Aya.textSubtle) }
            configuration.textFieldStyle(.plain).font(.system(size: 13))
        }
        .padding(.horizontal, 10).padding(.vertical, 7)
        .frame(minHeight: Aya.controlHeight)
        .background(Aya.card, in: RoundedRectangle(cornerRadius: Aya.radiusControl, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: Aya.radiusControl, style: .continuous).strokeBorder(Aya.input, lineWidth: 1))
    }
}

/// Segmented control as in Aya ("Kosten | Nutzer je Limit"): bordered group, the selected
/// segment in light primary with primary text.
struct AyaSegmented<T: Hashable>: View {
    @Binding var selection: T
    let options: [(value: T, title: String, icon: String?)]
    var body: some View {
        HStack(spacing: 0) {
            ForEach(Array(options.enumerated()), id: \.offset) { i, o in
                let on = o.value == selection
                Button { selection = o.value } label: {
                    HStack(spacing: 5) {
                        if let icon = o.icon { Image(systemName: icon).font(.system(size: 11, weight: .semibold)) }
                        Text(o.title)
                    }
                    .font(.system(size: 13, weight: on ? .semibold : .regular))
                    .foregroundStyle(on ? Aya.primary : Aya.text)
                    .padding(.horizontal, 12)
                    .frame(maxWidth: .infinity, minHeight: Aya.controlHeight - 2)
                    .background(on ? Aya.primary.opacity(0.10) : .clear)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                if i < options.count - 1 { Rectangle().fill(Aya.border).frame(width: 1) }
            }
        }
        .fixedSize(horizontal: false, vertical: true)
        .background(Aya.card)
        .clipShape(RoundedRectangle(cornerRadius: Aya.radiusControl, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: Aya.radiusControl, style: .continuous).strokeBorder(Aya.border, lineWidth: 1))
    }
}

/// Labelled form row inside an Aya card: label on the left, control on the right.
struct AyaRow<Content: View>: View {
    let label: String
    var hint: String? = nil
    @ViewBuilder let content: Content
    var body: some View {
        HStack(alignment: .center, spacing: 16) {
            VStack(alignment: .leading, spacing: 2) {
                Text(label).font(.system(size: 13, weight: .medium))
                if let hint { Text(hint).font(.system(size: 11)).foregroundStyle(Aya.textMuted) }
            }
            Spacer(minLength: 12)
            content
        }
        .padding(.vertical, 6)
    }
}

// MARK: - Agent bits

/// Live waveform of the agent's recording input: teal bars scaled by the level.
struct AyaWaveform: View {
    var level: Double
    private static let shape: [Double] = [35, 65, 85, 50, 75, 40, 90, 100, 70, 45, 80, 55, 70, 40]
    var body: some View {
        HStack(alignment: .center, spacing: 2) {
            ForEach(Array(Self.shape.enumerated()), id: \.offset) { _, h in
                Capsule()
                    .fill(LinearGradient(colors: [Aya.agentTextMid, Aya.agent], startPoint: .top, endPoint: .bottom))
                    .frame(width: 3, height: max(3, 22 * max(0.12, h / 100 * level)))
            }
        }
        .frame(height: 24)
        .animation(.linear(duration: 0.1), value: level)
    }
}

/// Round teal button with a glow; the agent's mic button.
struct AyaAgentDot: View {
    var icon = "mic.fill"
    var size: CGFloat = 32
    var body: some View {
        Circle().fill(Aya.agent)
            .overlay(Image(systemName: icon).font(.system(size: size * 0.42, weight: .semibold)).foregroundStyle(Aya.agentForeground))
            .frame(width: size, height: size)
            .shadow(color: Aya.agentGlow, radius: 7)
    }
}

/// Map an RMS level to 0…1 (-60 dB … 0 dB).
func ayaLevel(_ rms: Float) -> Double {
    let db = 20 * log10(max(Double(rms), 1e-6))
    return max(0, min(1, (db + 60) / 60))
}
