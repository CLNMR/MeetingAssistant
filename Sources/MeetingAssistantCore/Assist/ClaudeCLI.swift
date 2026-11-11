import Foundation
import os

private let log = Logger(subsystem: "de.local.MeetingScribe", category: "ClaudeCLI")

/// Talks to Claude through the locally installed Claude Code CLI (`claude`), which uses the
/// user's Claude subscription login. No API key is involved.
public enum ClaudeCLI {
    public struct AuthStatus: Sendable, Equatable {
        public var loggedIn: Bool
        public var method: String?
        public var subscription: String?
        public var email: String?
        public init(loggedIn: Bool, method: String?, subscription: String?, email: String?) {
            self.loggedIn = loggedIn; self.method = method; self.subscription = subscription; self.email = email
        }
    }

    public enum CLIError: LocalizedError, Equatable {
        case notInstalled
        case notLoggedIn
        case usageLimit(String)
        case failed(String)
        /// A one-shot call that got no answer within its time limit (seconds).
        case timedOut(Int)
        public var errorDescription: String? {
            switch self {
            case .notInstalled: return "Claude Code is not installed. Install it from claude.com/code, then run `claude` once in Terminal to log in."
            case .notLoggedIn: return "Claude Code is not logged in. Run `claude` in Terminal and log in with your Claude account."
            case .usageLimit(let m): return "Claude usage limit reached. \(m)"
            case .failed(let m): return "Claude failed: \(m)"
            case .timedOut(let s): return "Claude did not answer within \(s) s."
            }
        }
    }

    /// The `claude` binary: the configured path, then the usual install locations, then $PATH.
    public static func locate(preferred: String? = nil) -> URL? {
        let fm = FileManager.default
        let home = fm.homeDirectoryForCurrentUser.path
        var candidates: [String] = []
        if let p = preferred?.trimmingCharacters(in: .whitespaces), !p.isEmpty { candidates.append((p as NSString).expandingTildeInPath) }
        candidates += ["\(home)/.local/bin/claude", "\(home)/.claude/local/claude", "/opt/homebrew/bin/claude", "/usr/local/bin/claude"]
        candidates += (ProcessInfo.processInfo.environment["PATH"] ?? "").split(separator: ":").map { "\($0)/claude" }
        return candidates.first { fm.isExecutableFile(atPath: $0) }.map { URL(fileURLWithPath: $0) }
    }

    /// Runs `claude auth status` (JSON).
    public static func authStatus(binary: URL) async -> AuthStatus? {
        guard let out = try? await run(binary, ["auth", "status"], stdin: nil, timeout: 20),
              let v = try? JSONValue.parse(out.stdout) else { return nil }
        return AuthStatus(loggedIn: v["loggedIn"]?.bool ?? false, method: v["authMethod"]?.string,
                          subscription: v["subscriptionType"]?.string, email: v["email"]?.string)
    }

    /// Flags that turn Claude Code into a plain model call: our own system prompt, no tools,
    /// no MCP servers, none of the user's settings, hooks, CLAUDE.md or skills, no session history.
    static func isolationArgs(system: String, model: String, effort: String) -> [String] {
        ["--system-prompt", system,
         "--tools", "",
         "--strict-mcp-config", "--mcp-config", #"{"mcpServers":{}}"#,
         "--setting-sources", "",
         "--disable-slash-commands",
         "--no-session-persistence",
         "--model", model,
         "--effort", effort]
    }

    /// An empty working directory, so no project files or CLAUDE.md are picked up.
    static var workDir: URL {
        let u = AppPaths.root.appendingPathComponent("ClaudeWork", isDirectory: true)
        try? FileManager.default.createDirectory(at: u, withIntermediateDirectories: true)
        return u
    }

    /// The child's environment: forces the subscription login (drops API keys) and hides that we
    /// may ourselves run inside Claude Code.
    static var environment: [String: String] {
        var env = ProcessInfo.processInfo.environment
        for k in env.keys where k.hasPrefix("CLAUDE_CODE") || k == "CLAUDECODE" || k == "ANTHROPIC_API_KEY"
            || k == "ANTHROPIC_AUTH_TOKEN" || k.hasPrefix("CLAUDE_AGENT") { env[k] = nil }
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        env["PATH"] = "\(home)/.local/bin:/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:" + (env["PATH"] ?? "")
        env["HOME"] = home
        return env
    }

    struct ProcessOutput { var stdout: Data; var stderr: Data; var status: Int32; var timedOut = false }

    static func run(_ binary: URL, _ args: [String], stdin: Data?, timeout: TimeInterval) async throws -> ProcessOutput {
        try await withCheckedThrowingContinuation { cont in
            let p = Process()
            p.executableURL = binary
            p.arguments = args
            p.environment = environment
            p.currentDirectoryURL = workDir
            let out = Pipe(), err = Pipe(), inp = Pipe()
            p.standardOutput = out; p.standardError = err; p.standardInput = inp
            let outBuf = DataBox(), errBuf = DataBox()
            out.fileHandleForReading.readabilityHandler = { h in outBuf.append(h.availableData) }
            err.fileHandleForReading.readabilityHandler = { h in errBuf.append(h.availableData) }
            let finished = DataBox(), killed = DataBox()
            p.terminationHandler = { proc in
                out.fileHandleForReading.readabilityHandler = nil
                err.fileHandleForReading.readabilityHandler = nil
                outBuf.append(out.fileHandleForReading.readDataToEndOfFile())
                errBuf.append(err.fileHandleForReading.readDataToEndOfFile())
                if finished.markOnce() {
                    cont.resume(returning: ProcessOutput(stdout: outBuf.data, stderr: errBuf.data, status: proc.terminationStatus,
                                                         timedOut: killed.isMarked))
                }
            }
            do { try p.run() } catch {
                if finished.markOnce() { cont.resume(throwing: CLIError.notInstalled) }
                return
            }
            if let stdin { inp.fileHandleForWriting.write(stdin) }
            try? inp.fileHandleForWriting.close()
            DispatchQueue.global().asyncAfter(deadline: .now() + timeout) {
                if p.isRunning { _ = killed.markOnce(); p.terminate() }
            }
        }
    }

    public struct OneShotResult: Sendable {
        public var text: String
        public var structured: JSONValue?
        public var costUSD: Double
    }

    /// One request: prompt in, answer out. With `schema`, the answer is JSON matching it
    /// (Claude Code's `--json-schema` structured output).
    public static func oneShot(binary: URL, system: String, prompt: String, model: String, effort: String,
                               schema: JSONValue? = nil, timeout: TimeInterval = 600) async throws -> OneShotResult {
        var args = ["-p", "--output-format", "json"] + isolationArgs(system: system, model: model, effort: effort)
        if let schema { args += ["--json-schema", schema.compactJSON] }
        let out = try await run(binary, args, stdin: Data(prompt.utf8), timeout: timeout)
        guard let v = try? JSONValue.parse(out.stdout) else {
            if out.timedOut { throw CLIError.timedOut(Int(timeout)) }
            let err = String(data: out.stderr, encoding: .utf8) ?? ""
            if err.lowercased().contains("login") || err.lowercased().contains("auth") { throw CLIError.notLoggedIn }
            throw CLIError.failed(err.isEmpty ? "no answer (exit \(out.status))" : String(err.prefix(400)))
        }
        let text = v["result"]?.string ?? ""
        if v["is_error"]?.bool == true || v["subtype"]?.string != "success" {
            if ClaudeCLI.isUsageLimit(text) { throw CLIError.usageLimit(text) }
            throw CLIError.failed(text.isEmpty ? (v["subtype"]?.string ?? "error") : text)
        }
        return OneShotResult(text: text, structured: v["structured_output"], costUSD: v["total_cost_usd"]?.number ?? 0)
    }

    static func isUsageLimit(_ text: String) -> Bool {
        let t = text.lowercased()
        return t.contains("usage limit") || t.contains("limit reached") || t.contains("rate limit") || t.contains("out of extra usage")
    }
}

/// A thread-safe byte buffer.
final class DataBox: @unchecked Sendable {
    private var d = Data()
    private var marked = false
    private let lock = NSLock()
    func append(_ x: Data) { lock.lock(); d.append(x); lock.unlock() }
    var data: Data { lock.lock(); defer { lock.unlock() }; return d }
    func take() -> Data { lock.lock(); defer { lock.unlock() }; let x = d; d = Data(); return x }
    func markOnce() -> Bool { lock.lock(); defer { lock.unlock() }; if marked { return false }; marked = true; return true }
    var isMarked: Bool { lock.lock(); defer { lock.unlock() }; return marked }
}

/// A long-running `claude -p --input-format stream-json` process: one conversation, fed one user
/// message per line on stdin, answering with streamed events on stdout.
public final class ClaudeStream: @unchecked Sendable {
    public struct TurnResult: Sendable {
        public var isError: Bool
        public var text: String
        public var totalCostUSD: Double          // cumulative for this process
        public var contextTokens: Int            // input + cache read + cache write of the last request
        public var durationMs: Int
    }
    public enum Event: Sendable {
        case text(String)                        // streamed text delta
        case turnDone(TurnResult)
        case exited(status: Int32, stderr: String)
    }

    private let binary: URL
    private let args: [String]
    private let onEvent: @Sendable (Event) -> Void
    private var process: Process?
    private var stdinHandle: FileHandle?
    private let lineBuffer = DataBox()
    private let errBuffer = DataBox()
    private let lock = NSLock()
    private var sawDeltaThisTurn = false

    public init(binary: URL, system: String, model: String, effort: String, onEvent: @escaping @Sendable (Event) -> Void) {
        self.binary = binary
        self.args = ["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
                     "--include-partial-messages"] + ClaudeCLI.isolationArgs(system: system, model: model, effort: effort)
        self.onEvent = onEvent
    }

    public var isRunning: Bool { lock.lock(); defer { lock.unlock() }; return process?.isRunning == true }

    public func start() throws {
        let p = Process()
        p.executableURL = binary
        p.arguments = args
        p.environment = ClaudeCLI.environment
        p.currentDirectoryURL = ClaudeCLI.workDir
        let out = Pipe(), err = Pipe(), inp = Pipe()
        p.standardOutput = out; p.standardError = err; p.standardInput = inp
        out.fileHandleForReading.readabilityHandler = { [weak self] h in
            let d = h.availableData
            if d.isEmpty { h.readabilityHandler = nil; return }
            self?.consume(d)
        }
        err.fileHandleForReading.readabilityHandler = { [weak self] h in
            let d = h.availableData
            if d.isEmpty { h.readabilityHandler = nil; return }
            self?.errBuffer.append(d)
        }
        p.terminationHandler = { [weak self] proc in
            guard let self else { return }
            let e = String(data: self.errBuffer.data.suffix(2000), encoding: .utf8) ?? ""
            self.onEvent(.exited(status: proc.terminationStatus, stderr: e))
        }
        do { try p.run() } catch { throw ClaudeCLI.CLIError.notInstalled }
        lock.lock(); process = p; stdinHandle = inp.fileHandleForWriting; lock.unlock()
        log.info("claude stream started pid=\(p.processIdentifier)")
    }

    /// Sends one user message.
    public func send(_ text: String) throws {
        let msg = JSONValue.object(["type": .string("user"),
                                    "message": .object(["role": .string("user"), "content": .string(text)])])
        lock.lock(); let h = stdinHandle; sawDeltaThisTurn = false; lock.unlock()
        guard let h else { throw ClaudeCLI.CLIError.failed("not running") }
        do { try h.write(contentsOf: Data((msg.compactJSON + "\n").utf8)) }
        catch { throw ClaudeCLI.CLIError.failed("could not write to claude: \(error.localizedDescription)") }
    }

    public func stop() {
        lock.lock(); let p = process; let h = stdinHandle; stdinHandle = nil; lock.unlock()
        try? h?.close()
        if let p, p.isRunning {
            DispatchQueue.global().asyncAfter(deadline: .now() + 2) { if p.isRunning { p.terminate() } }
        }
    }

    private func consume(_ data: Data) {
        lineBuffer.append(data)
        var buf = lineBuffer.take()
        while let nl = buf.firstIndex(of: 0x0A) {
            let line = buf[buf.startIndex..<nl]
            buf = Data(buf[buf.index(after: nl)...])
            if !line.isEmpty { handle(line: Data(line)) }
        }
        if !buf.isEmpty { lineBuffer.append(buf) }
    }

    private func handle(line: Data) {
        guard let v = try? JSONValue.parse(line), let type = v["type"]?.string else { return }
        switch type {
        case "stream_event":
            let e = v["event"]
            if e?["type"]?.string == "content_block_delta", e?["delta"]?["type"]?.string == "text_delta",
               let t = e?["delta"]?["text"]?.string {
                lock.lock(); sawDeltaThisTurn = true; lock.unlock()
                onEvent(.text(t))
            }
        case "assistant":
            // Fallback when partial messages are not streamed: emit the whole text once.
            lock.lock(); let saw = sawDeltaThisTurn; lock.unlock()
            if !saw, let blocks = v["message"]?["content"]?.array {
                let t = blocks.filter { $0["type"]?.string == "text" }.compactMap { $0["text"]?.string }.joined()
                if !t.isEmpty { onEvent(.text(t)) }
            }
        case "result":
            // Split into steps: as one expression it is too slow to type-check on CI.
            let u = v["usage"]
            let input: Double = u?["input_tokens"]?.number ?? 0
            let cacheRead: Double = u?["cache_read_input_tokens"]?.number ?? 0
            let cacheCreation: Double = u?["cache_creation_input_tokens"]?.number ?? 0
            let ctx = Int(input + cacheRead + cacheCreation)
            let isError: Bool = v["is_error"]?.bool ?? false
            let text: String = v["result"]?.string ?? ""
            let cost: Double = v["total_cost_usd"]?.number ?? 0
            let durationMs = Int(v["duration_ms"]?.number ?? 0)
            onEvent(.turnDone(TurnResult(isError: isError, text: text, totalCostUSD: cost,
                                         contextTokens: ctx, durationMs: durationMs)))
        default:
            break
        }
    }
}
