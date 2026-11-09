import AVFoundation
import Foundation
import MeetingAssistantCore

// Command-line companion used for testing the capture and the pipeline without the GUI.
//   msctl record <seconds> [call|inPerson|hybrid] [--mute]
//   msctl devices

setvbuf(stdout, nil, _IONBF, 0)
let args = CommandLine.arguments.dropFirst()
func usage() -> Never {
    print("""
    usage:
      msctl devices | perm | taptest <seconds> [--mute]
      msctl record <seconds> [call|inPerson|hybrid] [--mute]
      msctl import <mic.wav> [system.wav] [call|inPerson|hybrid] [--speakers N]
      msctl transcribe <session-id> [qwen|parakeet] [auto|german|english]
      msctl enroll <voice.wav> [name]
      msctl export <session-id> md|txt|srt|json
      msctl trim <session-id> <start> <end> | split <session-id> <seconds>
      msctl aya …      (Aya connection, see `msctl aya`)
      msctl assist …   (Assist engine and recaps, see `msctl assist`)
    """)
    exit(2)
}

switch args.first {
case "aya":
    runAya(Array(args.dropFirst()))
case "assist":
    runAssist(Array(args.dropFirst()))
case "perm":
    print("microphone: \(Permissions.microphone.rawValue)")
case "taptest":
    let secs = args.count >= 2 ? Double(args[args.startIndex + 1]) ?? 5 : 5
    try AppPaths.ensure()
    let url = AppPaths.root.appendingPathComponent("taptest.caf")
    let writer = try TrackWriter(url: url, sessionStartHostTime: mach_absolute_time())
    let tap = SystemAudioTap()
    tap.muted = args.contains("--mute")
    try tap.start { buf, time in writer.append(buf, time: time) }
    print("tap running")
    let end = Date().addingTimeInterval(secs)
    while Date() < end { Thread.sleep(forTimeInterval: 1); print(String(format: "  level=%.3f dur=%.1f", writer.level, writer.durationSeconds)) }
    tap.stop()
    print("wrote \(url.path)")
case "import":
    // msctl import <mic.wav> [system.wav] --mode call|inPerson|hybrid [--speakers N]
    let rest = Array(args.dropFirst())
    let files = rest.filter { !$0.hasPrefix("--") && MeetingMode(rawValue: $0) == nil && Int($0) == nil }
    guard let micPath = files.first else { usage() }
    let mode = rest.compactMap { MeetingMode(rawValue: $0) }.first ?? (files.count > 1 ? .call : .inPerson)
    let speakers = rest.firstIndex(of: "--speakers").flatMap { Int(rest[$0 + 1]) }
    let info = try SessionStore.importFiles(mic: URL(fileURLWithPath: micPath),
                                            system: files.count > 1 ? URL(fileURLWithPath: files[1]) : nil,
                                            mode: mode, expectedSpeakers: speakers)
    print(info.id)
case "transcribe":
    // msctl transcribe <session-id> [qwen|parakeet] [auto|german|english]
    guard args.count >= 2 else { usage() }
    let id = args[args.startIndex + 1]
    var settings = PipelineSettings()
    if args.contains("parakeet") { settings.engine = .parakeetUltra }
    if let l = args.compactMap({ TranscriptLanguage(rawValue: $0) }).first { settings.language = l }
    let sem = DispatchSemaphore(value: 0)
    Task {
        do {
            let last = LockedBox("")
            let t = try await TranscriptionPipeline.run(sessionID: id, settings: settings) { p, msg in
                if msg != last.value { last.value = msg; print(String(format: "  [%3.0f%%] %@", p * 100, msg)) }
            }
            let info = try JSONIO.read(SessionInfo.self, from: AppPaths.info(id))
            print(try Exporter.render(t, info: info, as: .text))
            print(String(format: "processing %.1fs", t.processingSeconds))
        } catch { print("error: \(error)") }
        sem.signal()
    }
    sem.wait()
case "enroll":
    // msctl enroll <voice.wav> [name]
    guard args.count >= 2 else { usage() }
    let url = URL(fileURLWithPath: args[args.startIndex + 1])
    let name = args.count >= 3 ? args[args.startIndex + 2] : "Me"
    let sem = DispatchSemaphore(value: 0)
    Task {
        do {
            let v = try await VoiceprintStore.enroll(name: name, samples: try AudioIO.load16k(url))
            print(String(format: "enrolled %@ from %.1fs of speech", v.name, v.seconds))
        } catch { print("error: \(error.localizedDescription)") }
        sem.signal()
    }
    sem.wait()
case "export":
    guard args.count >= 3, let f = ExportFormat(rawValue: args[args.startIndex + 2]) else { usage() }
    let id = args[args.startIndex + 1]
    let info = try JSONIO.read(SessionInfo.self, from: AppPaths.info(id))
    guard let t = SessionStore.transcript(id) else { print("no transcript"); exit(1) }
    print(try Exporter.render(t, info: info, as: f))
case "asr":
    // msctl asr qwen|parakeet auto|german|english <files...>  -> "<file>\t<text>" per line
    guard args.count >= 4 else { usage() }
    let engine: ASREngineKind = args[args.startIndex + 1] == "parakeet" ? .parakeetUltra : .qwen3Large
    let lang = TranscriptLanguage(rawValue: args[args.startIndex + 2]) ?? .auto
    let files = Array(args.dropFirst(3))
    let sem = DispatchSemaphore(value: 0)
    Task {
        do {
            if engine == .qwen3Large { _ = try await ModelStore.shared.qwenModels(progress: nil) }
            let t0 = Date(); var dur = 0.0
            for f in files {
                let s = try AudioIO.load16k(URL(fileURLWithPath: f))
                dur += Double(s.count) / 16_000
                let words = try await ModelStore.shared.transcribe(s, offset: 0, engine: engine, language: lang)
                print("\((f as NSString).lastPathComponent)\t\(words.map(\.w).joined(separator: " "))")
            }
            FileHandle.standardError.write("RTFx \(dur / Date().timeIntervalSince(t0))\n".data(using: .utf8)!)
        } catch { print("error: \(error)") }
        sem.signal()
    }
    sem.wait()
case "dictate":
    // msctl dictate <file> [seconds]  -> push-to-talk path on the first N seconds, with latency
    guard args.count >= 2 else { usage() }
    let sem = DispatchSemaphore(value: 0)
    Task {
        do {
            var s = try AudioIO.load16k(URL(fileURLWithPath: args[args.startIndex + 1]))
            if args.count >= 3, let n = Double(args[args.startIndex + 2]) { s = Array(s.prefix(Int(n * 16_000))) }
            _ = try await ModelStore.shared.parakeetManager()
            let t0 = Date()
            let text = try await ModelStore.shared.dictate(s)
            print(text)
            FileHandle.standardError.write(String(format: "%.1f s audio -> %.0f ms\n", Double(s.count) / 16_000, Date().timeIntervalSince(t0) * 1000).data(using: .utf8)!)
        } catch { print("error: \(error)") }
        sem.signal()
    }
    sem.wait()
case "livetest":
    // msctl livetest <mic.wav> [system.wav] [call|inPerson|hybrid]  (feeds audio at 4x speed)
    let rest = Array(args.dropFirst())
    let files = rest.filter { MeetingMode(rawValue: $0) == nil }
    let mode = rest.compactMap { MeetingMode(rawValue: $0) }.first ?? (files.count > 1 ? .call : .inPerson)
    let sem = DispatchSemaphore(value: 0)
    Task {
        let latest = LockedBox<[LiveLine]>([])
        let live = LiveTranscriber(mode: mode) { latest.value = $0 }
        let tracks: [Track] = files.count > 1 ? [.mic, .system] : [.mic]
        await live.start(tracks: tracks)
        let audio = try! files.map { try AudioIO.load16k(URL(fileURLWithPath: $0)) }
        let block = 1600
        let n = audio.map(\.count).max() ?? 0
        let t0 = Date()
        var i = 0
        while i < n {
            for (k, a) in audio.enumerated() where i < a.count {
                live.feed(tracks[k], Array(a[i..<min(a.count, i + block)]))
            }
            i += block
            try? await Task.sleep(nanoseconds: 25_000_000)   // 100 ms of audio every 25 ms = 4x
            if i % (16_000 * 10) < block {
                let lines = latest.value
                print(String(format: "t=%5.1fs audio, %d lines, last: %@", Double(i) / 16_000, lines.count, lines.last?.text ?? "-"))
            }
        }
        await live.stop()
        print(String(format: "fed %.1fs in %.1fs", Double(n) / 16_000, Date().timeIntervalSince(t0)))
        for l in latest.value {
            print(String(format: "[%5.1f] %@ %@: %@", l.start, l.isFinal ? " " : "~", l.speaker ?? "?", l.text))
        }
        sem.signal()
    }
    sem.wait()
case "trim":
    // msctl trim <session-id> <start-seconds> <end-seconds>
    guard args.count >= 4, let a = Double(args[args.startIndex + 2]), let b = Double(args[args.startIndex + 3]) else { usage() }
    let info = try SessionEdit.trim(args[args.startIndex + 1], start: a, end: b)
    print(String(format: "%@ now %.1fs", info.id, info.durationSec))
case "split":
    // msctl split <session-id> <seconds>
    guard args.count >= 3, let t = Double(args[args.startIndex + 2]) else { usage() }
    let second = try SessionEdit.split(args[args.startIndex + 1], at: t)
    print(String(format: "new %@ (%.1fs)", second.id, second.durationSec))
case "memtest":
    // msctl memtest <wav>  -> GPU memory with Qwen3 loaded and after unloading
    guard args.count >= 2 else { usage() }
    let sem = DispatchSemaphore(value: 0)
    Task {
        let s = try! AudioIO.load16k(URL(fileURLWithPath: args[args.startIndex + 1]))
        _ = try? await ModelStore.shared.transcribe(Array(s.prefix(16_000 * 8)), offset: 0, engine: .qwen3Large, language: .german)
        print("loaded footprint MB:", footprintMB())
        await ModelStore.shared.unloadTranscriptionModels()
        try? await Task.sleep(nanoseconds: 500_000_000)
        print("unloaded footprint MB:", footprintMB())
        sem.signal()
    }
    sem.wait()
case "mictest":
    // msctl mictest <seconds>  -> captures the mic the live listener would use
    let secs = args.count >= 2 ? Double(args[args.startIndex + 1]) ?? 5 : 5
    let dev = args.contains("default") ? nil : AudioDevices.nonBluetoothInput(instead: nil)
    print("using device \(dev.map(String.init) ?? "default")")
    let m = MicCapture()
    let n = LockedBox(0)
    try m.start(deviceID: dev) { buf, _ in n.value += Int(buf.frameLength) }
    Thread.sleep(forTimeInterval: secs)
    m.stop()
    print("frames \(n.value)")
case "devices":
    for d in AudioDevices.inputDevices() { print("\(d.id)\t\(d.name)\t\(d.uid)") }
case "record":
    guard args.count >= 2, let secs = Double(args[args.startIndex + 1]) else { usage() }
    let mode = args.dropFirst(2).first.flatMap { MeetingMode(rawValue: $0) } ?? .call
    let mute = args.contains("--mute")
    let rec = try SessionRecorder(mode: mode)
    try rec.start(inputDeviceID: nil, muteSystemAudio: mute)
    print("recording \(rec.info.id) mode=\(mode.rawValue) for \(secs)s ...")
    let end = Date().addingTimeInterval(secs)
    while Date() < end {
        Thread.sleep(forTimeInterval: 1)
        print(String(format: "  t=%4.1fs  mic=%.3f  system=%.3f", rec.elapsed, rec.micLevel, rec.systemLevel))
    }
    let info = try rec.stop()
    print("saved \(AppPaths.session(info.id).path) duration=\(info.durationSec)")
default:
    usage()
}

final class LockedBox<T>: @unchecked Sendable {
    private var v: T
    private let lock = NSLock()
    init(_ v: T) { self.v = v }
    var value: T {
        get { lock.lock(); defer { lock.unlock() }; return v }
        set { lock.lock(); v = newValue; lock.unlock() }
    }
}

func footprintMB() -> Int {
    var info = task_vm_info_data_t()
    var count = mach_msg_type_number_t(MemoryLayout<task_vm_info_data_t>.size / MemoryLayout<natural_t>.size)
    _ = withUnsafeMutablePointer(to: &info) {
        $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) { task_info(mach_task_self_, task_flavor_t(TASK_VM_INFO), $0, &count) }
    }
    return Int(info.phys_footprint / 1_048_576)
}
