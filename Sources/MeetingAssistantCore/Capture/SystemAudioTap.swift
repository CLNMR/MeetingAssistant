import AVFoundation
import CoreAudio
import Foundation
import os

private let log = Logger(subsystem: "de.local.MeetingScribe", category: "SystemAudioTap")

public enum CaptureError: LocalizedError {
    case coreAudio(String, OSStatus)
    case noOutputDevice
    case formatUnavailable
    case micUnavailable(String)

    public var errorDescription: String? {
        switch self {
        case .coreAudio(let what, let status): return "\(what) failed (OSStatus \(status))"
        case .noOutputDevice: return "No default output device found"
        case .formatUnavailable: return "Could not read the audio tap format"
        case .micUnavailable(let why): return "Microphone unavailable: \(why)"
        }
    }
}

@inline(__always)
func check(_ status: OSStatus, _ what: String) throws {
    if status != noErr { throw CaptureError.coreAudio(what, status) }
}

/// Captures everything played on the Mac (Teams, Zoom, browsers, ...) through a
/// Core Audio process tap (macOS 14.2+). Our own process is excluded so playback
/// inside the app never ends up in the recording.
///
/// The tap is attached to a private aggregate device whose main sub-device is the
/// current default output device. When the default output changes (AirPods
/// connected mid-call), the tap is rebuilt transparently.
public final class SystemAudioTap: @unchecked Sendable {
    public typealias Handler = @Sendable (AVAudioPCMBuffer, AVAudioTime?) -> Void

    private let queue = DispatchQueue(label: "SystemAudioTap.io", qos: .userInitiated)
    /// Property listeners and rebuilds run here, never on the IO queue: stopping the IO
    /// proc waits for the IO queue, so rebuilding from it would deadlock.
    private let controlQueue = DispatchQueue(label: "SystemAudioTap.control", qos: .userInitiated)
    private var tapID = AudioObjectID(kAudioObjectUnknown)
    private var aggregateID = AudioObjectID(kAudioObjectUnknown)
    private var ioProcID: AudioDeviceIOProcID?
    private var format: AVAudioFormat?
    private var handler: Handler?
    private var deviceListener: AudioObjectPropertyListenerBlock?
    private var rateListener: AudioObjectPropertyListenerBlock?
    private var rateListenerDevice = AudioObjectID(kAudioObjectUnknown)
    // Rate watchdog: frames received vs. host time elapsed since the tap started.
    private var watchStartHost: UInt64 = 0
    private var watchFrames: Int64 = 0
    private let lock = NSLock()
    public private(set) var isRunning = false

    /// Mute the tapped audio (useful for silent self-tests only).
    public var muted = false

    public init() {}

    public func start(handler: @escaping Handler) throws {
        lock.lock(); defer { lock.unlock() }
        self.handler = handler
        try buildAndStart()
        installDefaultDeviceListener()
        isRunning = true
    }

    public func stop() {
        lock.lock(); defer { lock.unlock() }
        isRunning = false
        removeDefaultDeviceListener()
        teardown()
        isRunning = false
        handler = nil
    }

    // MARK: - Build / teardown

    private func buildAndStart() throws {
        let outputID = try Self.defaultOutputDevice()
        let outputUID = try Self.deviceUID(outputID)

        let excluded = Self.ownProcessObject().map { [$0] } ?? []
        let desc = CATapDescription(stereoGlobalTapButExcludeProcesses: excluded)
        desc.uuid = UUID()
        desc.name = "MeetingAssistant system audio"
        desc.isPrivate = true
        desc.muteBehavior = muted ? .muted : .unmuted

        var tap = AudioObjectID(kAudioObjectUnknown)
        try check(AudioHardwareCreateProcessTap(desc, &tap), "AudioHardwareCreateProcessTap")
        tapID = tap

        let aggregateDesc: [String: Any] = [
            kAudioAggregateDeviceNameKey: "MeetingAssistant Tap",
            kAudioAggregateDeviceUIDKey: "de.local.MeetingScribe.tap.\(UUID().uuidString)",
            kAudioAggregateDeviceMainSubDeviceKey: outputUID,
            kAudioAggregateDeviceIsPrivateKey: true,
            kAudioAggregateDeviceIsStackedKey: false,
            kAudioAggregateDeviceTapAutoStartKey: true,
            kAudioAggregateDeviceSubDeviceListKey: [[kAudioSubDeviceUIDKey: outputUID]],
            kAudioAggregateDeviceTapListKey: [[
                kAudioSubTapDriftCompensationKey: true,
                kAudioSubTapUIDKey: desc.uuid.uuidString,
            ]],
        ]
        var agg = AudioObjectID(kAudioObjectUnknown)
        try check(AudioHardwareCreateAggregateDevice(aggregateDesc as CFDictionary, &agg),
                  "AudioHardwareCreateAggregateDevice")
        aggregateID = agg

        var asbd = AudioStreamBasicDescription()
        var size = UInt32(MemoryLayout<AudioStreamBasicDescription>.size)
        var addr = AudioObjectPropertyAddress(mSelector: kAudioTapPropertyFormat,
                                              mScope: kAudioObjectPropertyScopeGlobal,
                                              mElement: kAudioObjectPropertyElementMain)
        try check(AudioObjectGetPropertyData(tapID, &addr, 0, nil, &size, &asbd), "read tap format")
        // The tap reports the format it was created with, but the buffers arrive at the
        // aggregate device's current rate. Bluetooth headsets drop to 16/24 kHz when a call
        // app opens their microphone, so trust the device rate.
        if let rate = Self.nominalRate(agg), rate > 0, abs(rate - asbd.mSampleRate) > 1 {
            log.info("Tap reports \(asbd.mSampleRate) Hz but device runs at \(rate) Hz")
            asbd.mSampleRate = rate
        }
        guard let fmt = AVAudioFormat(streamDescription: &asbd) else { throw CaptureError.formatUnavailable }
        format = fmt
        watchStartHost = 0
        watchFrames = 0
        log.info("Tap format: \(fmt.sampleRate) Hz, \(fmt.channelCount) ch, output device \(outputUID)")

        var procID: AudioDeviceIOProcID?
        try check(AudioDeviceCreateIOProcIDWithBlock(&procID, aggregateID, queue) { [weak self] _, inData, inTime, _, _ in
            guard let self, let handler = self.handler, let format = self.format,
                  let copy = Self.tapBuffer(inData, format: format) else { return }
            self.checkRate(frames: Int64(copy.frameLength), hostTime: inTime.pointee.mHostTime)
            let time = AVAudioTime(hostTime: inTime.pointee.mHostTime)
            handler(copy, time)
        }, "AudioDeviceCreateIOProcIDWithBlock")
        ioProcID = procID
        try check(AudioDeviceStart(aggregateID, ioProcID), "AudioDeviceStart")
        installRateListener(outputID)
    }

    /// Safety net for rate changes nobody announces: compare the frames actually delivered
    /// with the wall clock and switch to the nearest standard rate if they disagree.
    private func checkRate(frames: Int64, hostTime: UInt64) {
        guard let format, hostTime > 0 else { return }
        if watchStartHost == 0 { watchStartHost = hostTime; watchFrames = 0; return }
        watchFrames += frames
        let elapsed = AVAudioTime.seconds(forHostTime: hostTime - watchStartHost)
        guard elapsed >= 2 else { return }
        let measured = Double(watchFrames) / elapsed
        watchStartHost = hostTime; watchFrames = 0
        guard abs(measured / format.sampleRate - 1) > 0.15 else { return }
        let standard: [Double] = [8000, 11025, 16000, 22050, 24000, 32000, 44100, 48000, 88200, 96000]
        let rate = standard.min { abs($0 - measured) < abs($1 - measured) }!
        var asbd = format.streamDescription.pointee
        asbd.mSampleRate = rate
        if let f = AVAudioFormat(streamDescription: &asbd) {
            log.info("Measured \(measured) Hz instead of \(format.sampleRate) Hz, switching to \(rate) Hz")
            self.format = f
        }
    }

    /// Rebuild when the output device changes its sample rate (e.g. headset switching
    /// between music and call mode).
    private func installRateListener(_ device: AudioObjectID) {
        removeRateListener()
        var addr = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyNominalSampleRate,
                                              mScope: kAudioObjectPropertyScopeGlobal,
                                              mElement: kAudioObjectPropertyElementMain)
        let block: AudioObjectPropertyListenerBlock = { [weak self] _, _ in
            self?.controlQueue.async { self?.rebuild("Output sample rate changed") }
        }
        if AudioObjectAddPropertyListenerBlock(device, &addr, controlQueue, block) == noErr {
            rateListener = block
            rateListenerDevice = device
        }
    }

    private func removeRateListener() {
        guard let block = rateListener else { return }
        var addr = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyNominalSampleRate,
                                              mScope: kAudioObjectPropertyScopeGlobal,
                                              mElement: kAudioObjectPropertyElementMain)
        AudioObjectRemovePropertyListenerBlock(rateListenerDevice, &addr, controlQueue, block)
        rateListener = nil
        rateListenerDevice = AudioObjectID(kAudioObjectUnknown)
    }

    /// Copy the tap's audio out of the IO buffer list. The tap's streams come last; a
    /// headset in call mode adds its own microphone stream in front of them.
    static func tapBuffer(_ inData: UnsafePointer<AudioBufferList>, format: AVAudioFormat) -> AVAudioPCMBuffer? {
        let list = UnsafeMutableAudioBufferListPointer(UnsafeMutablePointer(mutating: inData))
        let needed = format.isInterleaved ? 1 : Int(format.channelCount)
        let bpf = Int(format.streamDescription.pointee.mBytesPerFrame)
        guard list.count >= needed, bpf > 0 else { return nil }
        let src = Array(list.suffix(needed))
        let frames = src.map { Int($0.mDataByteSize) / bpf }.min() ?? 0
        guard frames > 0, let out = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(frames)) else { return nil }
        out.frameLength = AVAudioFrameCount(frames)
        let dst = UnsafeMutableAudioBufferListPointer(out.mutableAudioBufferList)
        for (s, d) in zip(src, dst) {
            guard let sp = s.mData, let dp = d.mData else { continue }
            memcpy(dp, sp, frames * bpf)
        }
        return out
    }

    static func nominalRate(_ id: AudioObjectID) -> Double? {
        var rate: Float64 = 0
        var size = UInt32(MemoryLayout<Float64>.size)
        var addr = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyNominalSampleRate,
                                              mScope: kAudioObjectPropertyScopeGlobal,
                                              mElement: kAudioObjectPropertyElementMain)
        return AudioObjectGetPropertyData(id, &addr, 0, nil, &size, &rate) == noErr ? rate : nil
    }

    private func teardown() {
        removeRateListener()
        if aggregateID != kAudioObjectUnknown {
            if let ioProcID {
                AudioDeviceStop(aggregateID, ioProcID)
                AudioDeviceDestroyIOProcID(aggregateID, ioProcID)
            }
            AudioHardwareDestroyAggregateDevice(aggregateID)
        }
        if tapID != kAudioObjectUnknown { AudioHardwareDestroyProcessTap(tapID) }
        ioProcID = nil
        aggregateID = AudioObjectID(kAudioObjectUnknown)
        tapID = AudioObjectID(kAudioObjectUnknown)
    }

    private func rebuild(_ reason: String) {
        lock.lock(); defer { lock.unlock() }
        guard isRunning else { return }
        log.info("\(reason), rebuilding tap")
        teardown()
        do { try buildAndStart() } catch { log.error("Tap rebuild failed: \(error.localizedDescription)") }
    }

    // MARK: - Default output device changes

    private func installDefaultDeviceListener() {
        var addr = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDefaultOutputDevice,
                                              mScope: kAudioObjectPropertyScopeGlobal,
                                              mElement: kAudioObjectPropertyElementMain)
        let block: AudioObjectPropertyListenerBlock = { [weak self] _, _ in
            self?.controlQueue.async { self?.rebuild("Default output device changed") }
        }
        deviceListener = block
        AudioObjectAddPropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &addr, controlQueue, block)
    }

    private func removeDefaultDeviceListener() {
        guard let block = deviceListener else { return }
        var addr = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDefaultOutputDevice,
                                              mScope: kAudioObjectPropertyScopeGlobal,
                                              mElement: kAudioObjectPropertyElementMain)
        AudioObjectRemovePropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &addr, controlQueue, block)
        deviceListener = nil
    }

    // MARK: - Helpers

    static func defaultOutputDevice() throws -> AudioObjectID {
        var id = AudioObjectID(kAudioObjectUnknown)
        var size = UInt32(MemoryLayout<AudioObjectID>.size)
        var addr = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDefaultOutputDevice,
                                              mScope: kAudioObjectPropertyScopeGlobal,
                                              mElement: kAudioObjectPropertyElementMain)
        try check(AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &addr, 0, nil, &size, &id),
                  "read default output device")
        guard id != kAudioObjectUnknown else { throw CaptureError.noOutputDevice }
        return id
    }

    static func deviceUID(_ id: AudioObjectID) throws -> String {
        var uid: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        var addr = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyDeviceUID,
                                              mScope: kAudioObjectPropertyScopeGlobal,
                                              mElement: kAudioObjectPropertyElementMain)
        try check(AudioObjectGetPropertyData(id, &addr, 0, nil, &size, &uid), "read device UID")
        return uid!.takeRetainedValue() as String
    }

    static func ownProcessObject() -> AudioObjectID? {
        var pid = getpid()
        var obj = AudioObjectID(kAudioObjectUnknown)
        var size = UInt32(MemoryLayout<AudioObjectID>.size)
        var addr = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyTranslatePIDToProcessObject,
                                              mScope: kAudioObjectPropertyScopeGlobal,
                                              mElement: kAudioObjectPropertyElementMain)
        let status = AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &addr,
                                                UInt32(MemoryLayout<pid_t>.size), &pid, &size, &obj)
        return status == noErr && obj != kAudioObjectUnknown ? obj : nil
    }
}

extension AVAudioPCMBuffer {
    /// Copy a buffer that wraps memory owned by Core Audio (valid only inside the IO callback).
    func deepCopy() -> AVAudioPCMBuffer? {
        guard let out = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: frameLength) else { return nil }
        out.frameLength = frameLength
        let src = UnsafeMutableAudioBufferListPointer(UnsafeMutablePointer(mutating: audioBufferList))
        let dst = UnsafeMutableAudioBufferListPointer(out.mutableAudioBufferList)
        for (s, d) in zip(src, dst) {
            guard let sp = s.mData, let dp = d.mData else { continue }
            memcpy(dp, sp, Int(min(s.mDataByteSize, d.mDataByteSize)))
        }
        return out
    }
}
