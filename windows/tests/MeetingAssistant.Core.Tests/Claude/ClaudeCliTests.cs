using System.Text;
using System.Text.Json.Nodes;
using MeetingAssistant.Core.Claude;
using Xunit;

namespace MeetingAssistant.Core.Tests.Claude;

/// Argument building, environment, output parsing and errors of the Claude CLI wrapper (spec 02 §1, 04 §2.3–2.4).
public class ClaudeCliTests
{
    [Fact]
    public void IsolationArgumentsInOrder()
    {
        Assert.Equal(new[]
        {
            "--system-prompt", "SYS", "--tools", "", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}",
            "--setting-sources", "", "--disable-slash-commands", "--no-session-persistence", "--model", "opus", "--effort", "low",
        }, ClaudeCli.IsolationArgs("SYS", "opus", "low"));
    }

    [Fact]
    public void OneShotAndStreamArguments()
    {
        var schema = JsonNode.Parse("""{"type":"object","properties":{"b":{"type":"string"},"a":{"type":"number"}}}""");
        var a = ClaudeCli.OneShotArgs("S", "sonnet", "medium", schema);
        Assert.Equal(new[] { "-p", "--output-format", "json" }, a.Take(3));
        Assert.Equal("--json-schema", a[^2]);
        Assert.Equal("""{"properties":{"a":{"type":"number"},"b":{"type":"string"}},"type":"object"}""", a[^1]);
        Assert.DoesNotContain("--json-schema", ClaudeCli.OneShotArgs("S", "haiku", "low", null));
        Assert.Equal(new[] { "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages", "--system-prompt" },
                     ClaudeCli.StreamArgs("S", "haiku", "low").Take(8));
    }

    [Fact]
    public void EnvironmentDropsApiKeysAndClaudeCodeVariables()
    {
        var env = new Dictionary<string, string?>
        {
            ["PATH"] = "/x", ["ANTHROPIC_API_KEY"] = "k", ["ANTHROPIC_AUTH_TOKEN"] = "t", ["CLAUDECODE"] = "1",
            ["CLAUDE_CODE_ENTRYPOINT"] = "cli", ["CLAUDE_AGENT_SDK"] = "1", ["CLAUDE_CONFIG_DIR"] = "keep", ["OTHER"] = "o",
        };
        ClaudeCli.ApplyEnvironment(env, windows: false, home: "/Users/test");
        Assert.Equal(new[] { "CLAUDE_CONFIG_DIR", "HOME", "OTHER", "PATH" }, env.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("/Users/test/.local/bin:/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/x", env["PATH"]);
        Assert.Equal("/Users/test", env["HOME"]);

        var win = new Dictionary<string, string?> { ["PATH"] = @"C:\x" };
        ClaudeCli.ApplyEnvironment(win, windows: true, home: @"C:\Users\test");
        Assert.StartsWith(Path.Combine(@"C:\Users\test", ".local", "bin") + ";", win["PATH"]);
        Assert.Equal(@"C:\Users\test", win["USERPROFILE"]);
        Assert.Equal(@"C:\Users\test", win["HOME"]);
    }

    [Fact]
    public void CandidatesFollowTheLookupOrder()
    {
        var mac = ClaudeCli.Candidates("~/bin/claude", windows: false, home: "/Users/t", pathVar: "/a:/b");
        Assert.Equal(new[] { "/Users/t/bin/claude", "/Users/t/.local/bin/claude", "/Users/t/.claude/local/claude", "/opt/homebrew/bin/claude", "/usr/local/bin/claude", "/a/claude", "/b/claude" }, mac);
        var win = ClaudeCli.Candidates(null, windows: true, home: "H", pathVar: "P1;P2", appData: "AD", localAppData: "LAD");
        Assert.Equal(Path.Combine("H", ".local", "bin", "claude.exe"), win[0]);
        // Every .exe before the npm .cmd wrappers.
        Assert.True(win.FindLastIndex(p => p.EndsWith(".exe")) < win.FindIndex(p => p.EndsWith(".cmd")));
        Assert.Contains(Path.Combine("AD", "npm", "claude.cmd"), win);
        Assert.Contains(Path.Combine("P2", "claude.cmd"), win);
    }

    [Fact]
    public void LocateFindsAnExecutableFile()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Path.GetTempPath(), "claude-loc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var f = Path.Combine(dir, "claude");
            File.WriteAllText(f, "#!/bin/sh\n");
            Assert.False(ClaudeCli.IsExecutable(f));
            File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Assert.True(ClaudeCli.IsExecutable(f));
            Assert.Equal(f, ClaudeCli.Locate(f));
        }
        finally { Directory.Delete(dir, true); }
    }

    static ClaudeProcessOutput Out(string stdout, string stderr = "", int code = 0, bool timedOut = false) =>
        new(Encoding.UTF8.GetBytes(stdout), Encoding.UTF8.GetBytes(stderr), code, timedOut);

    [Fact]
    public void OneShotOutputParsing()
    {
        var ok = ClaudeCli.ParseOneShot(Out("""{"type":"result","subtype":"success","is_error":false,"result":"Hallo","structured_output":{"a":1},"total_cost_usd":0.25}"""), 600);
        Assert.Equal("Hallo", ok.Text);
        Assert.Equal(1, ok.Structured?["a"]?.GetValue<double>());
        Assert.Equal(0.25, ok.CostUsd);

        ClaudeErrorKind Kind(ClaudeProcessOutput o) => Assert.Throws<ClaudeException>(() => ClaudeCli.ParseOneShot(o, 60)).Kind;
        Assert.Equal(ClaudeErrorKind.TimedOut, Kind(Out("", timedOut: true)));
        Assert.Equal("Claude did not answer within 60 s.", Assert.Throws<ClaudeException>(() => ClaudeCli.ParseOneShot(Out("", timedOut: true), 60)).Message);
        Assert.Equal(ClaudeErrorKind.NotLoggedIn, Kind(Out("", "Please run /login")));
        Assert.Equal(ClaudeErrorKind.NotLoggedIn, Kind(Out("not json", "Auth failed")));
        Assert.Equal("Claude failed: no answer (exit 3)", Assert.Throws<ClaudeException>(() => ClaudeCli.ParseOneShot(Out("", "", 3), 60)).Message);
        Assert.Equal(400, Assert.Throws<ClaudeException>(() => ClaudeCli.ParseOneShot(Out("", new string('e', 1000)), 60)).Detail.Length);
        Assert.Equal(ClaudeErrorKind.UsageLimit, Kind(Out("""{"subtype":"success","is_error":true,"result":"5-hour limit reached ∙ resets 3pm"}""")));
        var failed = Assert.Throws<ClaudeException>(() => ClaudeCli.ParseOneShot(Out("""{"subtype":"error_max_turns","is_error":false,"result":""}"""), 60));
        Assert.Equal("Claude failed: error_max_turns", failed.Message);
    }

    [Fact]
    public void ErrorTexts()
    {
        Assert.Equal("Claude Code is not installed. Install it from claude.com/code, then run `claude` once in Terminal to log in.", ClaudeException.NotInstalled().Message);
        Assert.Equal("Claude Code is not logged in. Run `claude` in Terminal and log in with your Claude account.", ClaudeException.NotLoggedIn().Message);
        Assert.Equal("Claude usage limit reached. x", ClaudeException.UsageLimit("x").Message);
        Assert.Equal("Claude failed: x", ClaudeException.Failed("x").Message);
    }

    [Fact]
    public void UsageLimitWords()
    {
        foreach (var t in new[] { "Usage limit hit", "limit reached", "Rate limit", "You're out of extra usage" }) Assert.True(ClaudeCli.IsUsageLimit(t));
        Assert.False(ClaudeCli.IsUsageLimit("overloaded"));
    }

    [Fact]
    public void AuthStatus()
    {
        var s = ClaudeCli.ParseAuthStatus("""{"loggedIn":true,"authMethod":"claude.ai","subscriptionType":"max","email":"erika@example.com"}""");
        Assert.Equal(new ClaudeAuthStatus(true, "claude.ai", "max", "erika@example.com"), s);
        Assert.False(ClaudeCli.ParseAuthStatus("{}")!.LoggedIn);
        Assert.Null(ClaudeCli.ParseAuthStatus("nope"));
    }

    [Fact]
    public void StreamEventParsing()
    {
        bool saw = false;
        var delta = JsonNode.Parse("""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"ab"}}}""")!.AsObject();
        Assert.Equal(new ClaudeStreamEvent.TextDelta("ab"), Assert.Single(ClaudeStream.Parse(delta, ref saw)));
        Assert.True(saw);
        var assistant = JsonNode.Parse("""{"type":"assistant","message":{"content":[{"type":"text","text":"x"},{"type":"tool_use"},{"type":"text","text":"y"}]}}""")!.AsObject();
        Assert.Empty(ClaudeStream.Parse(assistant, ref saw));                  // deltas came this turn
        saw = false;
        Assert.Equal(new ClaudeStreamEvent.TextDelta("xy"), Assert.Single(ClaudeStream.Parse(assistant, ref saw)));
        var result = JsonNode.Parse("""{"type":"result","is_error":false,"result":"r","total_cost_usd":0.5,"duration_ms":1200,"usage":{"input_tokens":10,"cache_read_input_tokens":200,"cache_creation_input_tokens":3000}}""")!.AsObject();
        var done = Assert.IsType<ClaudeStreamEvent.TurnDone>(Assert.Single(ClaudeStream.Parse(result, ref saw)));
        Assert.Equal(new ClaudeTurnResult(false, "r", 0.5, 3210, 1200), done.Result);
        Assert.Empty(ClaudeStream.Parse(JsonNode.Parse("""{"type":"system"}""")!.AsObject(), ref saw));
    }
}

/// The Scribe+ process lifecycle (spec 04 §2.3) with a fake conversation.
public class ClaudeRewriteSessionTests
{
    sealed class Fake : IClaudeConversation
    {
        public Action<ClaudeStreamEvent>? OnEvent;
        public bool Running = true;
        public List<string> Sent = new();
        public bool IsRunning => Running;
        public void Start() { }
        public void Send(string text) { Sent.Add(text); }
        public void Stop() { Running = false; }
    }

    static (ClaudeRewriteSession, Fake) Make(string model = "sonnet", DateTime? now = null)
    {
        var f = new Fake();
        var s = new ClaudeRewriteSession("claude", "SYS", model, factory: (_, _, _, on) => { f.OnEvent = on; return f; }, now: now);
        return (s, f);
    }

    [Fact]
    public async Task AnswersWithTheResultOrTheStreamedText()
    {
        var (s, f) = Make();
        Assert.True(s.IsUsable);
        var t = s.RewriteAsync("prompt");
        Assert.False(s.IsUsable);
        f.OnEvent!(new ClaudeStreamEvent.TextDelta("Hal"));
        f.OnEvent!(new ClaudeStreamEvent.TextDelta("lo"));
        f.OnEvent!(new ClaudeStreamEvent.TurnDone(new ClaudeTurnResult(false, "", 0, 0, 0)));
        Assert.Equal("Hallo", await t);
        Assert.False(f.Running);
    }

    [Fact]
    public async Task ErrorsAndTimeout()
    {
        var (s, f) = Make();
        var t = s.RewriteAsync("p");
        f.OnEvent!(new ClaudeStreamEvent.Exited(1, "please login"));
        Assert.Equal(ClaudeErrorKind.NotLoggedIn, (await Assert.ThrowsAsync<ClaudeException>(() => t)).Kind);

        var (s2, _) = Make();
        var e = await Assert.ThrowsAsync<ClaudeException>(() => s2.RewriteAsync("p", timeoutSeconds: 0.05));
        Assert.Equal("Claude failed: Claude took too long", e.Message);
    }

    [Fact]
    public void SpareIsReplacedWhenUsedTooOldOrOtherModel()
    {
        var now = new DateTime(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);
        var started = new List<ClaudeRewriteSession>();
        var spare = new ClaudeSpare(m => { var (s, _) = Make(m, now); started.Add(s); return s; }, () => now);
        spare.Refresh(wanted: true, "sonnet");
        spare.Refresh(wanted: true, "sonnet");
        Assert.Single(started);                                     // still fits
        now = now.AddMinutes(30);
        spare.Refresh(wanted: true, "sonnet");
        Assert.Equal(2, started.Count);                             // 30 minutes old: replaced
        spare.Refresh(wanted: true, "haiku");
        Assert.Equal(3, started.Count);                             // other model
        Assert.Same(started[2], spare.Take("haiku"));
        Assert.NotSame(started[2], spare.Take("haiku"));            // a spare is used once
        spare.Refresh(wanted: false, "haiku");
        Assert.Equal(4, started.Count);
    }
}
