using MeetingAssistant.Core.Claude;

namespace MeetingAssistant.Core.Tests.Assist;

/// A test clock.
public sealed class ManualClock : TimeProvider
{
    DateTimeOffset _now = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan t) => _now += t;
}

/// A scripted Claude conversation: records what is sent; the test pushes events.
public sealed class FakeConversation : IClaudeConversation
{
    public sealed class Log
    {
        public List<FakeConversation> Conversations { get; } = new();
        public FakeConversation Last => Conversations[^1];
        public bool FailStart { get; set; }
        public bool FailSend { get; set; }
    }

    readonly Log _log;
    readonly Action<ClaudeStreamEvent> _onEvent;
    public string System { get; }
    public string Model { get; }
    public string Effort { get; }
    public List<string> Sent { get; } = new();
    public bool Running { get; private set; }
    public bool Stopped { get; private set; }

    FakeConversation(Log log, string system, string model, string effort, Action<ClaudeStreamEvent> onEvent)
    {
        _log = log; System = system; Model = model; Effort = effort; _onEvent = onEvent;
    }

    public static ClaudeConversationFactory Factory(Log log) => (system, model, effort, onEvent) =>
    {
        var c = new FakeConversation(log, system, model, effort, onEvent);
        log.Conversations.Add(c);
        return c;
    };

    public bool IsRunning => Running;
    public void Start() { if (_log.FailStart) throw ClaudeException.NotInstalled(); Running = true; }
    public void Send(string text) { if (_log.FailSend) throw ClaudeException.Failed("write"); Sent.Add(text); }
    public void Stop() { Stopped = true; Running = false; }

    public void Emit(ClaudeStreamEvent e) => _onEvent(e);
    public void Text(string t) => Emit(new ClaudeStreamEvent.TextDelta(t));
    public void Done(bool error = false, string text = "", double cost = 0.01, int tokens = 1000) =>
        Emit(new ClaudeStreamEvent.TurnDone(new ClaudeTurnResult(error, text, cost, tokens, 100)));
    public void Exit(int status, string stderr = "") => Emit(new ClaudeStreamEvent.Exited(status, stderr));
}
