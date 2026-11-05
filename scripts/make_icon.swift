// Renders the app icon (1024 px PNG): a rounded square with a waveform and speech bubble.
import AppKit

let size = 1024.0
let img = NSImage(size: NSSize(width: size, height: size))
img.lockFocus()
let rect = NSRect(x: 100, y: 100, width: 824, height: 824)
let path = NSBezierPath(roundedRect: rect, xRadius: 185, yRadius: 185)
NSGradient(starting: NSColor(calibratedRed: 0.16, green: 0.36, blue: 0.95, alpha: 1),
           ending: NSColor(calibratedRed: 0.47, green: 0.22, blue: 0.86, alpha: 1))!.draw(in: path, angle: -60)
let cfg = NSImage.SymbolConfiguration(pointSize: 470, weight: .semibold).applying(.init(paletteColors: [.white]))
if let sym = NSImage(systemSymbolName: "waveform.and.person.filled", accessibilityDescription: nil)?.withSymbolConfiguration(cfg)
    ?? NSImage(systemSymbolName: "waveform", accessibilityDescription: nil)?.withSymbolConfiguration(cfg) {
    let s = sym.size
    sym.draw(in: NSRect(x: (size - s.width) / 2, y: (size - s.height) / 2, width: s.width, height: s.height))
}
img.unlockFocus()
let rep = NSBitmapImageRep(data: img.tiffRepresentation!)!
try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: CommandLine.arguments[1]))
