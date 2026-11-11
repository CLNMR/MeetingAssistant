using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeetingAssistant.Core.Json;

namespace MeetingAssistant.Core.Claude;

/// <summary>A finished turn of the streaming conversation (spec 02 §1.5, <c>result</c> event).</summary>
/// <param name="TotalCostUsd">Cumulative for this process.</param>
/// <param name="ContextTokens">input + cache read + cache write tokens of the last request.</param>
public sealed record ClaudeTurnResult(bool IsError, string Text, double TotalCostUsd, int ContextTokens, int DurationMs);

/// <summary>An event of the streaming conversation.</summary>
public abstract record ClaudeStreamEvent
{
    /// A streamed text delta (or the whole text when no deltas came this turn).
    public sealed record TextDelta(string Text) : ClaudeStreamEvent;
    public sealed record TurnDone(ClaudeTurnResult Result) : ClaudeStreamEvent;
    /// The process ended; <paramref name="Stderr"/> is the last 2000 bytes of its error output.
    public sealed record Exited(int Status, string Stderr) : ClaudeStreamEvent;
}

/// <summary>One long-running conversation with Claude. Implemented by <see cref="ClaudeStream"/>; tests use fakes.</summary>
public interface IClaudeConversation
{
    /// Launches the process. Throws <see cref="ClaudeException"/> (not installed) when it cannot start.
    void Start();
    /// Sends one user message. Throws when stdin cannot be written.
    void Send(string text);
    /// Closes stdin; the process is killed when it is still running 2 s later.
    void Stop();
    bool IsRunning { get; }
}

/// <summary>Creates a conversation for a system prompt, model and effort, delivering its events to the callback.</summary>
public delegate IClaudeConversation ClaudeConversationFactory(string system, string model, string effort, Action<ClaudeStreamEvent> onEvent);

/// <summary>
/// A long-running <c>claude -p --input-format stream-json</c> process: one conversation, fed one user
/// message per line on stdin, answering with streamed events on stdout (spec 02 §1.5, 04 §2.4).
/// Events are delivered in order on a background thread.
/// </summary>
public sealed class ClaudeStream : IClaudeConversation
{
    readonly string _binary;
    readonly List<string> _args;
    readonly Action<ClaudeStreamEvent> _onEvent;
    readonly object _lock = new();
    Process? _process;
    Stream? _stdin;
    bool _sawDeltaThisTurn;
    readonly StderrTail _stderr = new(2000);

    public ClaudeStream(string binary, string system, string model, string effort, Action<ClaudeStreamEvent> onEvent)
    {
        _binary = binary;
        _args = ClaudeCli.StreamArgs(system, model, effort);
        _onEvent = onEvent;
    }

    /// <summary>The factory for real processes of <paramref name="binary"/>.</summary>
    public static ClaudeConversationFactory Factory(string binary) =>
        (system, model, effort, onEvent) => new ClaudeStream(binary, system, model, effort, onEvent);

    public bool IsRunning
    {
        get { lock (_lock) { try { return _process is { HasExited: false }; } catch { return false; } } }
    }

    public void Start()
    {
        var p = new Process { StartInfo = ClaudeCli.StartInfo(_binary, _args) };
        try { if (!p.Start()) throw ClaudeException.NotInstalled(); }
        catch (Exception e) when (e is not ClaudeException) { throw ClaudeException.NotInstalled(); }
        lock (_lock) { _process = p; _stdin = p.StandardInput.BaseStream; }
        var stdout = p.StandardOutput.BaseStream;
        var stderr = p.StandardError.BaseStream;
        var errTask = Task.Run(async () =>
        {
            var buf = new byte[4096];
            try { int n; while ((n = await stderr.ReadAsync(buf)) > 0) _stderr.Append(buf.AsSpan(0, n)); } catch { }
        });
        _ = Task.Run(async () =>
        {
            var pending = new List<byte>();
            var buf = new byte[8192];
            try
            {
                int n;
                while ((n = await stdout.ReadAsync(buf)) > 0)
                {
                    for (int i = 0; i < n; i++)
                    {
                        if (buf[i] == (byte)'\n')
                        {
                            if (pending.Count > 0) HandleLine(pending.ToArray());
                            pending.Clear();
                        }
                        else pending.Add(buf[i]);
                    }
                }
            }
            catch { }
            try { await p.WaitForExitAsync(); } catch { }
            try { await errTask; } catch { }
            int code;
            try { code = p.ExitCode; } catch { code = -1; }
            _onEvent(new ClaudeStreamEvent.Exited(code, _stderr.Text));
        });
    }

    public void Send(string text)
    {
        var msg = new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = text },
        };
        Stream? s;
        lock (_lock) { s = _stdin; _sawDeltaThisTurn = false; }
        if (s is null) throw ClaudeException.Failed("not running");
        try
        {
            var bytes = new UTF8Encoding(false).GetBytes(JsonIO.Compact(msg) + "\n");
            s.Write(bytes);
            s.Flush();
        }
        catch (Exception e) { throw ClaudeException.Failed($"could not write to claude: {e.Message}"); }
    }

    public void Stop()
    {
        Process? p; Stream? s;
        lock (_lock) { p = _process; s = _stdin; _stdin = null; }
        try { s?.Close(); } catch { }
        if (p is null) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(2000);
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        });
    }

    void HandleLine(byte[] line)
    {
        JsonObject? v;
        try { v = JsonNode.Parse(line) as JsonObject; } catch (JsonException) { return; }
        if (v is null) return;
        foreach (var ev in Parse(v, ref _sawDeltaThisTurn)) _onEvent(ev);
    }

    /// <summary>
    /// Handles one stdout line by its <c>type</c> (spec 02 §1.5 table). <paramref name="sawDelta"/> tracks
    /// whether this turn streamed deltas, so the <c>assistant</c> fallback emits nothing twice.
    /// </summary>
    public static IEnumerable<ClaudeStreamEvent> Parse(JsonObject v, ref bool sawDelta)
    {
        var list = new List<ClaudeStreamEvent>();
        switch (v["type"].AsString())
        {
            case "stream_event":
                {
                    var e = v["event"];
                    if (e?["type"].AsString() == "content_block_delta" && e["delta"]?["type"].AsString() == "text_delta"
                        && e["delta"]?["text"].AsString() is { } t)
                    {
                        sawDelta = true;
                        list.Add(new ClaudeStreamEvent.TextDelta(t));
                    }
                    break;
                }
            case "assistant":
                if (!sawDelta && v["message"]?["content"] is JsonArray blocks)
                {
                    var t = string.Concat(blocks.Where(b => b?["type"].AsString() == "text").Select(b => b?["text"].AsString() ?? ""));
                    if (t.Length > 0) list.Add(new ClaudeStreamEvent.TextDelta(t));
                }
                break;
            case "result":
                {
                    var u = v["usage"];
                    var ctx = (int)((u?["input_tokens"].AsNumber() ?? 0) + (u?["cache_read_input_tokens"].AsNumber() ?? 0)
                                    + (u?["cache_creation_input_tokens"].AsNumber() ?? 0));
                    list.Add(new ClaudeStreamEvent.TurnDone(new ClaudeTurnResult(
                        v["is_error"].AsBool() ?? false, v["result"].AsString() ?? "", v["total_cost_usd"].AsNumber() ?? 0, ctx,
                        (int)(v["duration_ms"].AsNumber() ?? 0))));
                    break;
                }
        }
        return list;
    }

    /// Keeps the last N bytes of a stream.
    sealed class StderrTail(int max)
    {
        readonly List<byte> _bytes = new();
        public void Append(ReadOnlySpan<byte> b)
        {
            lock (_bytes)
            {
                _bytes.AddRange(b.ToArray());
                if (_bytes.Count > max) _bytes.RemoveRange(0, _bytes.Count - max);
            }
        }
        public string Text { get { lock (_bytes) return Encoding.UTF8.GetString(_bytes.ToArray()); } }
    }
}
