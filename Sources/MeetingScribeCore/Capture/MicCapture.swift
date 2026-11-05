import AVFoundation
import CoreAudio
import Foundation

public struct AudioInputDevice: Identifiable, Hashable, Sendable {
    public let id: AudioObjectID
    public let uid: String
    public let name: String
}

/// Lists input devices (built-in mic, USB conference mics, headsets, ...).
public enum AudioDevices {
    public static func inputDevices() -> [AudioInputDevice] {
        var addr = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDevices,
                                              mScope: kAudioObjectPropertyScopeGlobal,
                                              mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(AudioObjectID(kAudioObjectSystemObject), &addr, 0, nil, &size) == noErr else { return [] }
        var ids = [AudioObjectID](repeating: 0, count: Int(size) / MemoryLayout<AudioObjectID>.size)
        guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &addr, 0, nil, &size, &ids) == noErr else { return [] }
        return ids.compactMap { id in
            guard inputChannelCount(id) > 0, let uid = try? SystemAudioTap.deviceUID(id) else { return nil }
            // Skip our own private aggregate devices and virtual call-app devices.
            if uid.hasPrefix("de.local.MeetingScribe") { return nil }
            let name = deviceName(id) ?? uid
            if name.contains("Microsoft Teams Audio") || name.contains("ZoomAudioDevice") { return nil }
            return AudioInputDevice(id: id, uid: uid, name: name)
        }
    }

    public static func defaultInputDevice() -> AudioObjectID? {
        var id = AudioObjectID(kAudioObjectUnknown)
        var size = UInt32(MemoryLayout<AudioObjectID>.size)
        var addr = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDefaultInputDevice,
                                              mScope: kAudioObjectPropertyScopeGlobal,
                                              mElement: kAudioObjectPropertyElementMain)
        let st = AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &addr, 0, nil, &size, &id)
        return st == noErr && id != kAudioObjectUnknown ? id : nil
    }

    public static func transportType(_ id: AudioObjectID) -> UInt32 {
        var t: UInt32 = 0
        var size = UInt32(MemoryLayout<UInt32>.size)
        var addr = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyTransportType,
                                              mScope: kAudioObjectPropertyScopeGlobal,
                                              mElement: kAudioObjectPropertyElementMain)
        _ = AudioObjectGetPropertyData(id, &addr, 0, nil, &size, &t)
        return t
    }

    public static func isBluetooth(_ id: AudioObjectID) -> Bool {
        let t = transportType(id)
        return t == kAudioDeviceTransportTypeBluetooth || t == kAudioDeviceTransportTypeBluetoothLE
    }

    /// Opening a Bluetooth headset's microphone switches it into call mode (mono, 16 kHz),
    /// which ruins music playback. For always-on listening, use another microphone instead:
    /// the built-in one first, then USB. Returns nil when no alternative exists.
    public static func nonBluetoothInput(instead id: AudioObjectID?) -> AudioObjectID? {
        guard let dev = id ?? defaultInputDevice(), isBluetooth(dev) else { return id }
        let candidates = inputDevices().filter { !isBluetooth($0.id) }
        return candidates.first { transportType($0.id) == kAudioDeviceTransportTypeBuiltIn }?.id
            ?? candidates.first { transportType($0.id) == kAudioDeviceTransportTypeUSB }?.id
            ?? id
    }

    static func inputChannelCount(_ id: AudioObjectID) -> Int {
        var addr = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyStreamConfiguration,
                                              mScope: kAudioObjectPropertyScopeInput,
                                              mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(id, &addr, 0, nil, &size) == noErr, size > 0 else { return 0 }
        let raw = UnsafeMutableRawPointer.allocate(byteCount: Int(size), alignment: 16)
        defer { raw.deallocate() }
        guard AudioObjectGetPropertyData(id, &addr, 0, nil, &size, raw) == noErr else { return 0 }
        let list = UnsafeMutableAudioBufferListPointer(raw.assumingMemoryBound(to: AudioBufferList.self))
        return list.reduce(0) { $0 + Int($1.mNumberChannels) }
    }

    static func deviceName(_ id: AudioObjectID) -> String? {
        var name: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        var addr = AudioObjectPropertyAddress(mSelector: kAudioObjectPropertyName,
                                              mScope: kAudioObjectPropertyScopeGlobal,
                                              mElement: kAudioObjectPropertyElementMain)
        guard AudioObjectGetPropertyData(id, &addr, 0, nil, &size, &name) == noErr else { return nil }
        return name?.takeRetainedValue() as String?
    }
}

/// Captures the microphone (or any input device) through a Core Audio HAL input unit.
///
/// Unlike AVAudioEngine, this never touches the system default input when another device
/// is chosen. (AVAudioEngine briefly opens the default input on start; with a Bluetooth
/// headset as default that flips the headset into call mode and ruins its sound.)
///
/// When the device changes (unplugged, rate change because a call app switched a headset
/// into call mode, new default input) the unit is rebuilt on the current device and
/// recording continues; the TrackWriter pads the short dropout with silence.
public final class MicCapture: @unchecked Sendable {
    public typealias Handler = @Sendable (AVAudioPCMBuffer, AVAudioTime?) -> Void

    /// Everything the IO thread needs, immutable for the lifetime of one unit.
    private final class Context {
        let unit: AudioUnit
        let format: AVAudioFormat
        let handler: Handler
        init(unit: AudioUnit, format: AVAudioFormat, handler: @escaping Handler) {
            self.unit = unit; self.format = format; self.handler = handler
        }
    }

    private var context: Unmanaged<Context>?
    private var handler: Handler?
    private var preferred: AudioObjectID?
    private var currentDevice = AudioObjectID(kAudioObjectUnknown)
    private let lock = NSLock()
    private let listenerQueue = DispatchQueue(label: "MicCapture.listeners")
    private let restartQueue = DispatchQueue(label: "MicCapture.restart")
    private var running = false
    private var restartPending = false   // only touched on restartQueue
    private var listeners: [(AudioObjectID, AudioObjectPropertyAddress, AudioObjectPropertyListenerBlock)] = []

    public init() {}

    public func start(deviceID: AudioObjectID?, handler: @escaping Handler) throws {
        lock.lock(); defer { lock.unlock() }
        self.handler = handler
        preferred = deviceID
        try startUnit()
        running = true
    }

    public func stop() {
        lock.lock(); defer { lock.unlock() }
        running = false
        stopUnit()
        handler = nil
    }

    /// The chosen device while it exists, otherwise the system default input.
    private func resolveDevice() -> AudioObjectID? {
        if let p = preferred, AudioDevices.inputDevices().contains(where: { $0.id == p }) { return p }
        return AudioDevices.defaultInputDevice()
    }

    private func startUnit() throws {
        guard let handler else { return }
        guard let dev = resolveDevice() else { throw CaptureError.micUnavailable("no input device") }
        var desc = AudioComponentDescription(componentType: kAudioUnitType_Output,
                                             componentSubType: kAudioUnitSubType_HALOutput,
                                             componentManufacturer: kAudioUnitManufacturer_Apple,
                                             componentFlags: 0, componentFlagsMask: 0)
        guard let comp = AudioComponentFindNext(nil, &desc) else { throw CaptureError.micUnavailable("no HAL input unit") }
        var maybeUnit: AudioUnit?
        try check(AudioComponentInstanceNew(comp, &maybeUnit), "create input unit")
        guard let unit = maybeUnit else { throw CaptureError.micUnavailable("no HAL input unit") }
        do {
            var one: UInt32 = 1, zero: UInt32 = 0
            let u32 = UInt32(MemoryLayout<UInt32>.size)
            try check(AudioUnitSetProperty(unit, kAudioOutputUnitProperty_EnableIO, kAudioUnitScope_Input, 1, &one, u32), "enable input")
            try check(AudioUnitSetProperty(unit, kAudioOutputUnitProperty_EnableIO, kAudioUnitScope_Output, 0, &zero, u32), "disable output")
            var d = dev
            try check(AudioUnitSetProperty(unit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0,
                                           &d, UInt32(MemoryLayout<AudioObjectID>.size)), "select input device")
            var hw = AudioStreamBasicDescription()
            var size = UInt32(MemoryLayout<AudioStreamBasicDescription>.size)
            try check(AudioUnitGetProperty(unit, kAudioUnitProperty_StreamFormat, kAudioUnitScope_Input, 1, &hw, &size), "read input format")
            guard hw.mSampleRate > 0, hw.mChannelsPerFrame > 0,
                  let format = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: hw.mSampleRate,
                                             channels: hw.mChannelsPerFrame, interleaved: false) else {
                throw CaptureError.micUnavailable("input format is empty (permission denied or no device)")
            }
            var client = format.streamDescription.pointee
            try check(AudioUnitSetProperty(unit, kAudioUnitProperty_StreamFormat, kAudioUnitScope_Output, 1, &client, size), "set client format")
            let ctx = Unmanaged.passRetained(Context(unit: unit, format: format, handler: handler))
            var cb = AURenderCallbackStruct(inputProc: micInputProc, inputProcRefCon: ctx.toOpaque())
            do {
                try check(AudioUnitSetProperty(unit, kAudioOutputUnitProperty_SetInputCallback, kAudioUnitScope_Global, 0,
                                               &cb, UInt32(MemoryLayout<AURenderCallbackStruct>.size)), "set input callback")
                try check(AudioUnitInitialize(unit), "initialize input")
                try check(AudioOutputUnitStart(unit), "start input")
            } catch {
                ctx.release()
                throw error
            }
            context = ctx
            currentDevice = dev
            installListeners(dev)
        } catch {
            AudioUnitUninitialize(unit)
            AudioComponentInstanceDispose(unit)
            throw error
        }
    }

    private func stopUnit() {
        removeListeners()
        guard let ctx = context else { return }
        context = nil
        let unit = ctx.takeUnretainedValue().unit
        AudioOutputUnitStop(unit)
        AudioUnitUninitialize(unit)
        AudioComponentInstanceDispose(unit)
        ctx.release()
        currentDevice = AudioObjectID(kAudioObjectUnknown)
    }

    // MARK: Device changes

    private func installListeners(_ dev: AudioObjectID) {
        let block: AudioObjectPropertyListenerBlock = { [weak self] _, _ in self?.scheduleRestart() }
        let watch: [(AudioObjectID, AudioObjectPropertySelector, AudioObjectPropertyScope)] = [
            (dev, kAudioDevicePropertyNominalSampleRate, kAudioObjectPropertyScopeGlobal),
            (dev, kAudioDevicePropertyDeviceIsAlive, kAudioObjectPropertyScopeGlobal),
            (dev, kAudioDevicePropertyStreamConfiguration, kAudioObjectPropertyScopeInput),
            (AudioObjectID(kAudioObjectSystemObject), kAudioHardwarePropertyDevices, kAudioObjectPropertyScopeGlobal),
            (AudioObjectID(kAudioObjectSystemObject), kAudioHardwarePropertyDefaultInputDevice, kAudioObjectPropertyScopeGlobal),
        ]
        for (obj, sel, scope) in watch {
            var addr = AudioObjectPropertyAddress(mSelector: sel, mScope: scope, mElement: kAudioObjectPropertyElementMain)
            if AudioObjectAddPropertyListenerBlock(obj, &addr, listenerQueue, block) == noErr {
                listeners.append((obj, addr, block))
            }
        }
    }

    private func removeListeners() {
        for (obj, addr, block) in listeners {
            var a = addr
            AudioObjectRemovePropertyListenerBlock(obj, &a, listenerQueue, block)
        }
        listeners = []
    }

    /// Changes come in bursts; react once they settle. Never takes the lock on the
    /// listener queue, so removing listeners while holding the lock cannot deadlock.
    private func scheduleRestart() {
        restartQueue.async { [weak self] in
            guard let self, !self.restartPending else { return }
            self.restartPending = true
            self.restartQueue.asyncAfter(deadline: .now() + 0.4) { [weak self] in
                self?.restartPending = false
                self?.restartIfNeeded()
            }
        }
    }

    private func restartIfNeeded() {
        lock.lock(); defer { lock.unlock() }
        guard running else { return }
        if let ctx = context, let target = resolveDevice(), target == currentDevice {
            let rate = SystemAudioTap.nominalRate(target) ?? 0
            let fmt = ctx.takeUnretainedValue().format
            let channels = AudioDevices.inputChannelCount(target)
            if abs(rate - fmt.sampleRate) < 1 && channels == Int(fmt.channelCount) { return }   // nothing relevant changed
        }
        NSLog("MicCapture: input device changed, restarting microphone")
        stopUnit()
        // The new device may need a moment before it delivers a valid format.
        for attempt in 0..<10 {
            do { try startUnit(); return }
            catch {
                NSLog("MicCapture: restart attempt \(attempt + 1) failed: \(error.localizedDescription)")
                stopUnit()
                Thread.sleep(forTimeInterval: 0.5)
            }
        }
    }
}

private let micInputProc: AURenderCallback = { refCon, flags, timeStamp, bus, frames, _ in
    let ctx = Unmanaged<AnyObject>.fromOpaque(refCon).takeUnretainedValue()
    return MicCapture.render(ctx, flags, timeStamp, bus, frames)
}

extension MicCapture {
    fileprivate static func render(_ obj: AnyObject, _ flags: UnsafeMutablePointer<AudioUnitRenderActionFlags>,
                                   _ ts: UnsafePointer<AudioTimeStamp>, _ bus: UInt32, _ frames: UInt32) -> OSStatus {
        guard let ctx = obj as? Context,
              let buf = AVAudioPCMBuffer(pcmFormat: ctx.format, frameCapacity: frames) else { return noErr }
        buf.frameLength = frames
        let list = UnsafeMutableAudioBufferListPointer(buf.mutableAudioBufferList)
        for i in 0..<list.count { list[i].mDataByteSize = frames * UInt32(MemoryLayout<Float>.size) }
        let st = AudioUnitRender(ctx.unit, flags, ts, bus, frames, buf.mutableAudioBufferList)
        guard st == noErr else { return st }
        let time = ts.pointee.mFlags.contains(.hostTimeValid) ? AVAudioTime(hostTime: ts.pointee.mHostTime) : nil
        ctx.handler(buf, time)
        return noErr
    }
}
