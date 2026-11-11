using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeetingAssistant.Core.Json;

namespace MeetingAssistant.Core.Claude;

/// <summary>The kinds of Claude CLI failures (spec 02 §1.7).</summary>
public enum ClaudeErrorKind { NotInstalled, NotLoggedIn, UsageLimit, Failed, TimedOut }

/// <summary>A Claude CLI failure. <see cref="Exception.Message"/> is the text shown to the user (spec 02 §1.7).</summary>
public sealed class ClaudeException : Exception
{
    public ClaudeErrorKind Kind { get; }
    /// The detail: the usage-limit or failure text, or the timeout in seconds.
    public string Detail { get; }

    public ClaudeException(ClaudeErrorKind kind, string detail = "") : base(Describe(kind, detail))
    {
        Kind = kind;
        Detail = detail;
    }

    public static ClaudeException NotInstalled() => new(ClaudeErrorKind.NotInstalled);
    public static ClaudeException NotLoggedIn() => new(ClaudeErrorKind.NotLoggedIn);
    public static ClaudeException UsageLimit(string m) => new(ClaudeErrorKind.UsageLimit, m);
    public static ClaudeException Failed(string m) => new(ClaudeErrorKind.Failed, m);
    public static ClaudeException TimedOut(int seconds) => new(ClaudeErrorKind.TimedOut, seconds.ToString());

    /// The user-facing texts of spec 02 §1.7.
    public static string Describe(ClaudeErrorKind kind, string detail) => kind switch
    {
        ClaudeErrorKind.NotInstalled => "Claude Code is not installed. Install it from claude.com/code, then run `claude` once in Terminal to log in.",
        ClaudeErrorKind.NotLoggedIn => "Claude Code is not logged in. Run `claude` in Terminal and log in with your Claude account.",
        ClaudeErrorKind.UsageLimit => $"Claude usage limit reached. {detail}",
        ClaudeErrorKind.Failed => $"Claude failed: {detail}",
        _ => $"Claude did not answer within {detail} s.",
    };
}

/// <summary>The result of <c>claude auth status</c> (spec 02 §1.2).</summary>
public sealed record ClaudeAuthStatus(bool LoggedIn, string? Method, string? Subscription, string? Email);

/// <summary>A one-shot answer (spec 02 §1.4).</summary>
public sealed record ClaudeOneShotResult(string Text, JsonNode? Structured, double CostUsd);

/// <summary>Captured output of a finished CLI process.</summary>
public sealed record ClaudeProcessOutput(byte[] Stdout, byte[] Stderr, int ExitCode, bool TimedOut);

/// <summary>
/// Talks to Claude through the locally installed Claude Code CLI (<c>claude</c>), which uses the
/// user's Claude subscription login. Never the Anthropic API, never an API key (spec 02 §1).
/// </summary>
public static class ClaudeCli
{
    /// <summary>Claude Code model aliases with their menu titles (spec 02 §1.8).</summary>
    public static readonly IReadOnlyList<(string Id, string Title)> Models =
        new[] { ("opus", "Opus (best)"), ("sonnet", "Sonnet (lighter)"), ("haiku", "Haiku (lightest)") };

    /// <summary>Default one-shot timeout in seconds (spec 02 §1.4).</summary>
    public const double DefaultTimeoutSeconds = 600;
    /// <summary>Timeout of "What did I miss?" in seconds (spec 02 §1.8).</summary>
    public const double CatchUpTimeoutSeconds = 60;
    /// <summary>Timeout of <c>claude auth status</c> in seconds (spec 02 §1.2).</summary>
    public const double AuthTimeoutSeconds = 20;

    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// The places to look for the binary, in order (spec 02 §1.2; Windows notes): the configured path,
    /// the usual install locations, then every PATH entry.
    /// </summary>
    public static List<string> Candidates(string? preferred, bool? windows = null, string? home = null, string? pathVar = null,
                                         string? appData = null, string? localAppData = null)
    {
        var win = windows ?? OperatingSystem.IsWindows();
        home ??= Home;
        pathVar ??= Environment.GetEnvironmentVariable("PATH") ?? "";
        var list = new List<string>();
        var p = preferred?.Trim();
        if (!string.IsNullOrEmpty(p)) list.Add(ExpandHome(p, home));
        if (win)
        {
            appData ??= Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            localAppData ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            // The native installer puts claude.exe in %USERPROFILE%\.local\bin; npm gives %APPDATA%\npm\claude.cmd.
            list.Add(Path.Combine(home, ".local", "bin", "claude.exe"));
            list.Add(Path.Combine(home, ".claude", "local", "claude.exe"));
            list.Add(Path.Combine(localAppData, "Programs", "claude", "claude.exe"));
            list.Add(Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "claude.exe"));
            var dirs = pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(d => d.Trim().Trim('"')).ToList();
            // Prefer a real .exe anywhere before a .cmd wrapper.
            foreach (var d in dirs) list.Add(Path.Combine(d, "claude.exe"));
            list.Add(Path.Combine(appData, "npm", "claude.cmd"));
            foreach (var d in dirs) list.Add(Path.Combine(d, "claude.cmd"));
        }
        else
        {
            list.Add($"{home}/.local/bin/claude");
            list.Add($"{home}/.claude/local/claude");
            list.Add("/opt/homebrew/bin/claude");
            list.Add("/usr/local/bin/claude");
            foreach (var d in pathVar.Split(':')) list.Add($"{d}/claude");
        }
        return list;
    }

    /// <summary>The <c>claude</c> binary: the first executable candidate, or null (spec 02 §1.2).</summary>
    public static string? Locate(string? preferred = null) => Candidates(preferred).FirstOrDefault(IsExecutable);

    static string ExpandHome(string p, string home)
    {
        if (p == "~") return home;
        // Keep the separator the user typed ("~/bin/claude" stays a "/" path on any host).
        if (p.StartsWith("~/") || p.StartsWith("~\\")) return home.TrimEnd('/', '\\') + p[1..];
        return Environment.ExpandEnvironmentVariables(p);
    }

    /// <summary>True when the file exists and can be run (an execute bit on Unix; .exe/.cmd/.bat on Windows).</summary>
    public static bool IsExecutable(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            if (OperatingSystem.IsWindows())
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                return ext is ".exe" or ".cmd" or ".bat" or ".com";
            }
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// How to start the binary. A <c>.cmd</c> from npm would go through cmd.exe, whose quoting breaks
    /// the system prompt and the JSON arguments, so when the CLI script sits next to it, node runs the
    /// script directly (spec 02 §1.5 Windows notes).
    /// </summary>
    public static (string FileName, List<string> PrefixArgs) LaunchCommand(string binary)
    {
        var ext = Path.GetExtension(binary).ToLowerInvariant();
        if (ext is ".cmd" or ".bat")
        {
            var dir = Path.GetDirectoryName(binary) ?? "";
            var script = Path.Combine(dir, "node_modules", "@anthropic-ai", "claude-code", "cli.js");
            if (File.Exists(script))
            {
                var node = Path.Combine(dir, "node.exe");
                if (!File.Exists(node))
                    node = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
                        .Select(d => Path.Combine(d.Trim().Trim('"'), "node.exe")).FirstOrDefault(File.Exists) ?? "node";
                return (node, new List<string> { script });
            }
        }
        return (binary, new List<string>());
    }

    /// <summary>
    /// Flags that turn Claude Code into a plain model call: our own system prompt, no tools, no MCP
    /// servers, none of the user's settings, hooks, CLAUDE.md or skills, no session history (spec 02 §1.3).
    /// </summary>
    public static List<string> IsolationArgs(string system, string model, string effort) => new()
    {
        "--system-prompt", system,
        "--tools", "",
        "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}",
        "--setting-sources", "",
        "--disable-slash-commands",
        "--no-session-persistence",
        "--model", model,
        "--effort", effort,
    };

    /// <summary>Arguments of a one-shot call (spec 02 §1.4).</summary>
    public static List<string> OneShotArgs(string system, string model, string effort, JsonNode? schema)
    {
        var args = new List<string> { "-p", "--output-format", "json" };
        args.AddRange(IsolationArgs(system, model, effort));
        if (schema is not null) { args.Add("--json-schema"); args.Add(JsonIO.Compact(schema)); }
        return args;
    }

    /// <summary>Arguments of the streaming conversation (spec 02 §1.5, 04 §2.4).</summary>
    public static List<string> StreamArgs(string system, string model, string effort)
    {
        var args = new List<string> { "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages" };
        args.AddRange(IsolationArgs(system, model, effort));
        return args;
    }

    /// <summary>An empty working directory, so no project files or CLAUDE.md are picked up (spec 02 §1.3).</summary>
    public static string WorkDir
    {
        get
        {
            var d = AppPaths.ClaudeWork;
            Directory.CreateDirectory(d);
            return d;
        }
    }

    /// <summary>True for variables removed from the child's environment (spec 02 §1.3).</summary>
    public static bool IsStrippedVariable(string name) =>
        name.StartsWith("CLAUDE_CODE", StringComparison.Ordinal) || name.StartsWith("CLAUDE_AGENT", StringComparison.Ordinal)
        || name is "CLAUDECODE" or "ANTHROPIC_API_KEY" or "ANTHROPIC_AUTH_TOKEN";

    /// <summary>
    /// The child's environment: forces the subscription login (drops API keys), hides that we may
    /// ourselves run inside Claude Code, and puts the usual install folders on PATH (spec 02 §1.3).
    /// </summary>
    public static void ApplyEnvironment(IDictionary<string, string?> env, bool? windows = null, string? home = null)
    {
        var win = windows ?? OperatingSystem.IsWindows();
        home ??= Home;
        foreach (var k in env.Keys.ToList()) if (IsStrippedVariable(k)) env.Remove(k);
        env.TryGetValue("PATH", out var path);
        if (win)
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            env["PATH"] = $"{Path.Combine(home, ".local", "bin")};{Path.Combine(appData, "npm")};" + (path ?? "");
            env["USERPROFILE"] = home;
        }
        else
        {
            env["PATH"] = $"{home}/.local/bin:/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:" + (path ?? "");
        }
        env["HOME"] = home;
    }

    /// <summary>A process start description for the CLI with the isolation environment and working directory.</summary>
    public static ProcessStartInfo StartInfo(string binary, IEnumerable<string> args)
    {
        var (file, prefix) = LaunchCommand(binary);
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = WorkDir,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var a in prefix) psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);
        ApplyEnvironment(psi.Environment);
        return psi;
    }

    /// <summary>Runs the CLI once: writes <paramref name="stdin"/>, closes it, collects the output; kills the process tree on timeout.</summary>
    public static async Task<ClaudeProcessOutput> RunAsync(string binary, IEnumerable<string> args, string? stdin, double timeoutSeconds,
                                                           CancellationToken ct = default)
    {
        using var p = new Process { StartInfo = StartInfo(binary, args) };
        try { if (!p.Start()) throw ClaudeException.NotInstalled(); }
        catch (Exception e) when (e is not ClaudeException) { throw ClaudeException.NotInstalled(); }
        var outTask = ReadAllAsync(p.StandardOutput.BaseStream);
        var errTask = ReadAllAsync(p.StandardError.BaseStream);
        try
        {
            if (stdin is not null)
            {
                var bytes = new UTF8Encoding(false).GetBytes(stdin);
                await p.StandardInput.BaseStream.WriteAsync(bytes, ct);
                await p.StandardInput.BaseStream.FlushAsync(ct);
            }
            p.StandardInput.Close();
        }
        catch (IOException) { /* the process ended early; its output says why */ }

        bool timedOut = false;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { p.Kill(entireProcessTree: true); } catch { }
            await p.WaitForExitAsync(CancellationToken.None);
            ct.ThrowIfCancellationRequested();
        }
        var stdout = await outTask;
        var stderr = await errTask;
        return new ClaudeProcessOutput(stdout, stderr, p.ExitCode, timedOut);
    }

    static async Task<byte[]> ReadAllAsync(Stream s)
    {
        using var ms = new MemoryStream();
        try { await s.CopyToAsync(ms); } catch (IOException) { }
        return ms.ToArray();
    }

    /// <summary>Runs <c>claude auth status</c> and reads its JSON (spec 02 §1.2). Null when it cannot be read.</summary>
    public static async Task<ClaudeAuthStatus?> AuthStatusAsync(string binary)
    {
        try
        {
            var o = await RunAsync(binary, new[] { "auth", "status" }, null, AuthTimeoutSeconds);
            return ParseAuthStatus(Encoding.UTF8.GetString(o.Stdout));
        }
        catch { return null; }
    }

    /// <summary>Reads the JSON printed by <c>claude auth status</c>.</summary>
    public static ClaudeAuthStatus? ParseAuthStatus(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject v) return null;
            return new ClaudeAuthStatus(v["loggedIn"].AsBool() ?? false, v["authMethod"].AsString(),
                                        v["subscriptionType"].AsString(), v["email"].AsString());
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// One request: prompt in (stdin), answer out. With <paramref name="schema"/>, the answer is JSON
    /// matching it (Claude Code's <c>--json-schema</c> structured output). Spec 02 §1.4.
    /// </summary>
    public static async Task<ClaudeOneShotResult> OneShotAsync(string binary, string system, string prompt, string model, string effort,
                                                               JsonNode? schema = null, double timeoutSeconds = DefaultTimeoutSeconds,
                                                               CancellationToken ct = default)
    {
        var o = await RunAsync(binary, OneShotArgs(system, model, effort, schema), prompt, timeoutSeconds, ct);
        return ParseOneShot(o, timeoutSeconds);
    }

    /// <summary>Turns the output of a one-shot run into its answer or the matching error (spec 02 §1.4).</summary>
    public static ClaudeOneShotResult ParseOneShot(ClaudeProcessOutput o, double timeoutSeconds)
    {
        JsonObject? v = null;
        try { v = JsonNode.Parse(o.Stdout) as JsonObject; } catch (JsonException) { }
        if (v is null)
        {
            if (o.TimedOut) throw ClaudeException.TimedOut((int)timeoutSeconds);
            var err = Encoding.UTF8.GetString(o.Stderr);
            var low = err.ToLowerInvariant();
            if (low.Contains("login") || low.Contains("auth")) throw ClaudeException.NotLoggedIn();
            throw ClaudeException.Failed(err.Length == 0 ? $"no answer (exit {o.ExitCode})" : (err.Length > 400 ? err[..400] : err));
        }
        var text = v["result"].AsString() ?? "";
        var subtype = v["subtype"].AsString();
        if (v["is_error"].AsBool() == true || subtype != "success")
        {
            if (IsUsageLimit(text)) throw ClaudeException.UsageLimit(text);
            throw ClaudeException.Failed(text.Length == 0 ? (subtype ?? "error") : text);
        }
        return new ClaudeOneShotResult(text, v["structured_output"]?.DeepClone(), v["total_cost_usd"].AsNumber() ?? 0);
    }

    /// <summary>A usage-limit message (spec 02 §1.6).</summary>
    public static bool IsUsageLimit(string text)
    {
        var t = text.ToLowerInvariant();
        return t.Contains("usage limit") || t.Contains("limit reached") || t.Contains("rate limit") || t.Contains("out of extra usage");
    }
}
