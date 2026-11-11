using MeetingAssistant.Core.Assist;
using MeetingAssistant.Core.Model;
using Xunit;
using static MeetingAssistant.Core.Tests.Assist.AssistTestData;

namespace MeetingAssistant.Core.Tests.Assist;

/// The tick loop, ops while streaming, restarts and errors (spec 02 §2.4–2.7), with a fake Claude and clock.
public class AssistEngineTests
{
    sealed class Rig
    {
        public readonly ManualClock Clock = new();
        public readonly FakeConversation.Log Log = new();
        public readonly List<(AssistState State, AssistStatus Status)> Published = new();
        public readonly AssistEngine Engine;
        public Rig(AssistContext? ctx = null, AssistSettings? s = null)
        {
            Engine = new AssistEngine(ctx ?? Context(), s ?? new AssistSettings(), FakeConversation.Factory(Log),
                                      (st, status) => { lock (Published) Published.Add((st, status)); }, Clock);
        }
        public FakeConversation Conv => Log.Last;
        public async Task<AssistState> State() => await Engine.GetStateAsync();
        public async Task Answer(string ops, double cost = 0.01, int tokens = 1000)
        {
            Conv.Text(ops);
            Conv.Done(cost: cost, tokens: tokens);
            await Engine.GetStateAsync();     // drains the queue
        }
        public async Task Tick(double seconds = 0)
        {
            Clock.Advance(TimeSpan.FromSeconds(seconds));
            await Engine.TickAsync();
        }
        public Task Lines(params (double Start, string Speaker, string Text)[] lines) =>
            Engine.IngestLinesAsync(lines.Select(l => new AssistLine(l.Start, l.Speaker, l.Text)));
    }

    const string Q = """{"op":"next_questions","items":[{"text":"Wie groß ist das Lager?"}]}""";

    [Fact]
    public async Task StartSendsTheContextMessageWithTheLiveSettings()
    {
        var r = new Rig(s: new AssistSettings { Model = "sonnet", Effort = "low" });
        await r.Engine.StartAsync(runLoop: false);
        Assert.Equal(AssistPrompts.System, r.Conv.System);
        Assert.Equal(("sonnet", "low"), (r.Conv.Model, r.Conv.Effort));
        Assert.Equal(AssistPrompts.ContextMessage(Context()), Assert.Single(r.Conv.Sent));
        Assert.IsType<AssistStatus.Thinking>(r.Engine.Status);
    }

    [Fact]
    public async Task OpsApplyWhileStreamingAndTheTurnCounts()
    {
        var r = new Rig();
        await r.Engine.StartAsync(false);
        r.Conv.Text(Q + "\n{\"op\":\"summary_ap");
        var s = await r.State();
        Assert.Equal("u0-0", Assert.Single(s.Questions).Id);         // applied before the turn ends
        Assert.Empty(s.Summary);
        r.Conv.Text("pend\",\"bullets\":[\"Lager: zu klein\"]}");   // the tail is applied at the end of the turn
        r.Conv.Done(cost: 0.02);
        s = await r.State();
        Assert.Equal(new[] { "Lager: zu klein" }, s.Summary);
        Assert.Equal(1, s.Updates);
        Assert.Equal(0.02, s.CostUSD);
        Assert.IsType<AssistStatus.Listening>(r.Engine.Status);
        Assert.Contains("questions", s.Changed);
        Assert.Contains("s0", s.Changed);
    }

    [Fact]
    public async Task UnknownContactIdsAreDropped()
    {
        var ctx = Context();
        ctx.Contacts.Add(new ContactInfo { Id = "k1", Name = "Erika Musterfrau" });
        var r = new Rig(ctx);
        await r.Engine.StartAsync(false);
        await r.Answer("""
            {"op":"attendee","name":"Erika Musterfrau","contact_id":"k1"}
            {"op":"attendee","name":"Max Beispiel","contact_id":"zz"}
            """);
        var s = await r.State();
        Assert.Equal("k1", s.Attendees[0].ContactID);
        Assert.Null(s.Attendees[1].ContactID);
    }

    [Fact]
    public async Task SendsWhenEnoughWordsAndTimeOrAfterMaxInterval()
    {
        var r = new Rig();
        await r.Engine.StartAsync(false);
        await r.Answer(Q);
        await r.Lines((1, "Speaker 1", "eins zwei drei"));
        await r.Tick(19);
        await r.Tick(40.5);                                     // 59.5 s: below maxInterval, too few words
        Assert.Single(r.Conv.Sent);
        await r.Tick(0.6);                                      // 60.1 s ≥ maxInterval 60
        Assert.Equal(2, r.Conv.Sent.Count);
        Assert.Equal("CURRENT SUMMARY: (empty)\nTRANSCRIPT (new):\n[00:00:01] Speaker 1: eins zwei drei", r.Conv.Sent[1]);
        await r.Answer(Q);

        await r.Lines((70, "Speaker 1", string.Join(" ", Enumerable.Repeat("wort", 60))));
        await r.Tick(19.9);
        Assert.Equal(2, r.Conv.Sent.Count);                     // 60 words, but only 19.9 s
        await r.Tick(0.2);                                      // ≥ minInterval 20
        Assert.Equal(3, r.Conv.Sent.Count);
    }

    [Fact]
    public async Task AQuestionFromTheOtherSideIsAnsweredAfterSixSeconds()
    {
        var r = new Rig();
        await r.Engine.StartAsync(false);
        await r.Answer(Q);
        await r.Lines((1, "Speaker 1", "Was kostet das?"));
        await r.Tick(5.9);
        Assert.Single(r.Conv.Sent);
        await r.Tick(0.2);
        Assert.Equal(2, r.Conv.Sent.Count);
        // From me it waits.
        await r.Answer(Q);
        await r.Lines((2, "Erika", "Was kostet das?"));
        await r.Tick(7);
        Assert.Equal(2, r.Conv.Sent.Count);
    }

    [Fact]
    public async Task EventsAloneWaitForMinIntervalAndSuggestNowForces()
    {
        var r = new Rig();
        await r.Engine.StartAsync(false);
        await r.Answer(Q);
        await r.Engine.SetOpenPointAsync("O9", OpenPointStatus.Parked);   // unknown: nothing queued
        await r.Answer("""{"op":"open_point","id":"O1","text":"Budget"}""");
        await r.Engine.SetOpenPointAsync("O1", OpenPointStatus.Parked);
        await r.Tick(19);
        Assert.Single(r.Conv.Sent);
        await r.Tick(1.1);
        Assert.Equal("[user set open point O1 to parked]", r.Conv.Sent[1]);   // no summary without lines
        await r.Answer(Q);
        await r.Engine.SuggestNowAsync();
        await r.Tick();
        Assert.Equal("[user asks: suggest a question now]", r.Conv.Sent[2]);
    }

    [Fact]
    public async Task PausedSendsNothingAndResumeSendsPending()
    {
        var r = new Rig();
        await r.Engine.StartAsync(false);
        await r.Answer(Q);
        await r.Engine.SetPausedAsync(true);
        Assert.IsType<AssistStatus.Paused>(r.Engine.Status);
        await r.Lines((1, "Speaker 1", "eins"));
        await r.Tick(500);
        Assert.Single(r.Conv.Sent);
        await r.Engine.SetPausedAsync(false);
        await r.Tick();
        Assert.Equal(2, r.Conv.Sent.Count);
    }

    [Fact]
    public async Task AHungUpdateRestartsAndCarriesUnansweredEvents()
    {
        var r = new Rig();
        await r.Engine.StartAsync(false);
        await r.Answer(Q);
        await r.Answer("""{"op":"open_point","id":"O1","text":"Budget"}""");
        await r.Engine.SetOpenPointAsync("O1", OpenPointStatus.Answered);
        await r.Tick(20);                                   // sends the event; no answer comes
        Assert.Equal(2, r.Conv.Sent.Count);
        await r.Engine.MarkAskedAsync("u0-0");              // queued while Claude hangs
        await r.Lines((400, "Speaker 1", "neue Zeile"));
        await r.Tick(90);
        Assert.Single(r.Log.Conversations);                 // exactly 90 s: still waiting
        await r.Tick(0.5);
        Assert.Equal(2, r.Log.Conversations.Count);
        Assert.True(r.Log.Conversations[0].Stopped);
        var resume = Assert.Single(r.Conv.Sent);
        Assert.Contains("THE MEETING IS ALREADY RUNNING", resume);
        Assert.Contains("[user set open point O1 to answered]\n[user marked question as asked: Wie groß ist das Lager?]", resume);
        Assert.Contains("[00:06:40] Speaker 1: neue Zeile", resume);
        Assert.Contains(r.Published, p => p.Status is AssistStatus.Reconnecting { Message: "no answer" });
        Assert.Contains("[00:06:40] Speaker 1: neue Zeile", (await r.Engine.GetTranscriptAsync()).Select(l => l.Rendered));
    }

    [Fact]
    public async Task UsageLimitWaitsFiveMinutesAndResendsTheEvents()
    {
        var r = new Rig();
        await r.Engine.StartAsync(false);
        await r.Answer(Q);
        await r.Engine.SuggestNowAsync();
        await r.Tick();
        r.Conv.Done(error: true, text: "Claude AI usage limit reached|1731000000");
        await r.State();
        Assert.Equal("Paused: Claude usage limit reached. Recording continues; retrying in 5 minutes.", r.Engine.Status.Text);
        await r.Tick(299);
        Assert.Equal(2, r.Conv.Sent.Count);
        await r.Tick(1);                                    // 300 s: forced update with the old events
        Assert.Equal(3, r.Conv.Sent.Count);
        Assert.Equal("[user asks: suggest a question now]", r.Conv.Sent[2]);
        Assert.Single(r.Log.Conversations);
    }

    [Fact]
    public async Task AnErrorAnswerRestartsOnTheNextTick()
    {
        var r = new Rig();
        await r.Engine.StartAsync(false);
        r.Conv.Done(error: true, text: "overloaded");
        await r.State();
        Assert.Equal("Claude: overloaded", r.Engine.Status.Text);
        await r.Tick();
        Assert.Equal(2, r.Log.Conversations.Count);
        Assert.StartsWith("MEETING CONTEXT", r.Conv.Sent[0]);
    }

    [Fact]
    public async Task OldConversationsRestartSilently()
    {
        var r = new Rig(s: new AssistSettings { RestartTokens = 80_000, RestartMinutes = 60 });
        await r.Engine.StartAsync(false);
        await r.Answer(Q, tokens: 80_001);
        await r.Tick();
        Assert.Equal(2, r.Log.Conversations.Count);
        Assert.DoesNotContain(r.Published, p => p.Status is AssistStatus.Reconnecting);
        await r.Answer(Q, cost: 0.03);
        Assert.Equal(0.04, (await r.State()).CostUSD, 6);            // cost summed across conversations
        await r.Tick(60 * 60 + 1);
        Assert.Equal(3, r.Log.Conversations.Count);
    }

    [Fact]
    public async Task ExitsWithLoginErrorsStop()
    {
        var r = new Rig();
        await r.Engine.StartAsync(false);
        r.Conv.Exit(1, "Invalid API key · Please run /login");
        await r.State();
        Assert.Equal(MeetingAssistant.Core.Claude.ClaudeException.NotLoggedIn().Message, r.Engine.Status.Text);
    }

    [Fact]
    public async Task EventsOfOldConversationsAreIgnored()
    {
        var r = new Rig();
        await r.Engine.StartAsync(false);
        r.Conv.Done(error: true, text: "x");
        await r.Tick();
        var old = r.Log.Conversations[0];
        old.Text(Q + "\n");
        old.Done();
        Assert.Empty((await r.State()).Questions);
    }

    [Fact]
    public async Task RenamingASpeakerRelabelsAndTellsClaude()
    {
        var ctx = Context();
        ctx.Contacts.Add(new ContactInfo { Id = "k1", Name = "Max Beispiel" });
        var r = new Rig(ctx, new AssistSettings { LabelWait = 0 });
        await r.Engine.StartAsync(false);
        await r.Engine.IngestAsync(new[] { new LiveLine("system-1.0", Track.System, 3, 4, "Hallo", "system-1", true) });
        await r.Answer("""{"op":"attendee","name":"Someone","speaker":"Speaker 1","contact_id":"k1"}""");
        await r.Engine.RenameSpeakerAsync("system-1", "  Max Beispiel ");
        var s = await r.State();
        Assert.Equal("Max Beispiel", s.SpeakerNames!["system-1"]);
        Assert.Equal("[00:00:03] Max Beispiel: Hallo", Assert.Single(await r.Engine.GetTranscriptAsync()).Rendered);
        var max = s.Attendees.Single(a => a.Name == "Max Beispiel");
        Assert.True(max.Speaker == "Max Beispiel" && max.ContactID == "k1" && max.Present == true);
        Assert.Null(s.Attendees.Single(a => a.Name == "Someone").Speaker);
        Assert.Contains("speakers", s.Changed);
        var ev = Assert.Single(await r.Engine.GetQueuedEventsAsync());
        Assert.Equal("user: the speaker labelled \"Speaker 1\" is Max Beispiel. Earlier lines of \"Speaker 1\" were said by Max Beispiel; later lines use the name. Update the attendees and anything you attributed to \"Speaker 1\".", ev);

        await r.Engine.RenameSpeakerAsync("system-7", "Lena Beispiel");
        Assert.Equal("user: a speaker is Lena Beispiel.", (await r.Engine.GetQueuedEventsAsync())[1]);
    }

    [Fact]
    public async Task UserActionsOnQuestionsAndTables()
    {
        var ctx = Context();
        var parsed = TemplateParser.Parse(TemplateMarkdown);
        ctx.TemplateTables = parsed.Tables;
        ctx.PlaybookItems = parsed.Items;
        var r = new Rig(ctx);
        await r.Engine.StartAsync(false);
        await r.Answer("""{"op":"next_questions","items":[{"text":"A"},{"text":"B"},{"text":"C"}]}""");
        await r.Engine.PromoteAsync("u0-2");
        Assert.Equal(new[] { "C", "A", "B" }, (await r.State()).Questions.Select(q => q.Text));
        await r.Engine.MarkAskedAsync("u0-2");
        await r.Engine.DismissAsync("u0-0");
        var s = await r.State();
        Assert.Equal(QuestionStatus.Asked, Assert.Single(s.AskedQuestions).Status);
        Assert.Equal("B", Assert.Single(s.Questions).Text);
        await r.Engine.SetTableCellAsync("T1.1", "Relevant", "ja");
        await r.Engine.SetTableCellAsync("T1.1", "Relevant", "ja");       // unchanged: no second event
        await r.Engine.SetPlaybookAsync("P2.1", PlaybookStatus.Covered);
        Assert.Equal(new[]
        {
            "user marked question as asked: C", "user dismissed question: A — suggest something else",
            "user set table cell T1.1 «Relevant» to «ja»", "user set playbook item P2.1 to covered",
        }, await r.Engine.GetQueuedEventsAsync());
        Assert.Contains("Relevant", (await r.State()).Tables![0].Rows[0].UserSet);
    }

    [Fact]
    public async Task StopReturnsTheFinalState()
    {
        var r = new Rig();
        await r.Engine.StartAsync(false);
        await r.Answer(Q);
        var final = await r.Engine.StopAsync();
        Assert.Single(final.Questions);
        Assert.IsType<AssistStatus.Stopped>(r.Engine.Status);
        Assert.True(r.Conv.Stopped);
    }
}
