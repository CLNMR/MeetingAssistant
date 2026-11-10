import Foundation

/// One Scribe+ rewrite through the local Claude Code CLI (the user's subscription, no API key).
///
/// Starting `claude` takes about two seconds, so the process is started as soon as the shortcut
/// is pressed and waits while the user speaks. When the transcript is ready only the model call
/// remains (about one second with Haiku). Each dictation gets a fresh process, so nothing of one
/// dictation leaks into the next.
public final class ScribePlusSession: @unchecked Sendable {
    private let stream: ClaudeStream
    public let model: String
    private let lock = NSLock()
    private var text = ""
    private var waiter: CheckedContinuation<String, Error>?
    private var result: Result<String, Error>?

    public init(binary: URL, model: String = ScribePlus.model) throws {
        self.model = model
        let box = WeakBox()
        stream = ClaudeStream(binary: binary, system: ScribePlus.system, model: model, effort: "low") { event in
            box.session?.handle(event)
        }
        box.session = self
        try stream.start()
    }

    private final class WeakBox: @unchecked Sendable { weak var session: ScribePlusSession? }

    /// Sends the prompt and waits for the answer.
    public func rewrite(_ prompt: String, timeout: TimeInterval = 8) async throws -> String {
        try stream.send(prompt)
        return try await withCheckedThrowingContinuation { (c: CheckedContinuation<String, Error>) in
            lock.lock()
            if let r = result { lock.unlock(); c.resume(with: r); return }
            waiter = c
            lock.unlock()
            DispatchQueue.global().asyncAfter(deadline: .now() + timeout) { [weak self] in
                self?.finish(.failure(ClaudeCLI.CLIError.failed("Claude took too long")))
            }
        }
    }

    public func close() { stream.stop() }

    /// Still running and unused.
    public var isUsable: Bool {
        lock.lock(); defer { lock.unlock() }
        return result == nil && waiter == nil && stream.isRunning
    }

    private func handle(_ event: ClaudeStream.Event) {
        switch event {
        case .text(let t):
            lock.lock(); text += t; lock.unlock()
        case .turnDone(let r):
            if r.isError {
                finish(.failure(ClaudeCLI.isUsageLimit(r.text) ? ClaudeCLI.CLIError.usageLimit(r.text) : ClaudeCLI.CLIError.failed(r.text)))
            } else {
                lock.lock(); let t = r.text.isEmpty ? text : r.text; lock.unlock()
                finish(.success(t))
            }
            stream.stop()
        case .exited(let status, let stderr):
            let e = stderr.lowercased()
            finish(.failure(e.contains("login") || e.contains("auth") ? ClaudeCLI.CLIError.notLoggedIn
                            : ClaudeCLI.CLIError.failed("claude exited (\(status)) \(stderr.suffix(200))")))
        }
    }

    private func finish(_ r: Result<String, Error>) {
        lock.lock()
        guard result == nil else { lock.unlock(); return }
        result = r
        let w = waiter; waiter = nil
        lock.unlock()
        w?.resume(with: r)
    }
}
