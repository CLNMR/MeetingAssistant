using System.Text.Json.Nodes;
using MeetingAssistant.Core.Json;
using MeetingAssistant.Core.Model;

namespace MeetingAssistant.Core.Storage;

/// Why an edit was refused; `Message` is the text shown to the user.
public sealed class SessionEditException(string message) : Exception(message)
{
    public static SessionEditException Busy() => new("The recording is being recorded or transcribed right now.");
    public static SessionEditException InvalidRange() => new("The chosen part is empty.");
}

/// <summary>
/// Cutting recordings (spec 01 "Editing recordings: trim and split"): audio is rewritten
/// sample-exactly in its own container (a `.wav` stays a `.wav`, a Mac `.caf` stays a `.caf`), and
/// an existing transcript and `assist.json` are cut the same way with times shifted, so names
/// and manual fixes survive without transcribing again.
/// </summary>
public static class SessionEdit
{
    /// Shortest part that may remain (s).
    public const double MinimumPart = 1;

    /// Statuses in which a session must not be edited (or deleted).
    public static bool IsBusy(SessionStatus s) => s is SessionStatus.Recording or SessionStatus.Queued or SessionStatus.Transcribing;

    /// Keep only `start ..&lt; end` (seconds). Returns the updated session.
    public static SessionInfo Trim(string id, double start, double end)
    {
        var info = SessionStore.Load(id);
        if (IsBusy(info.Status)) throw SessionEditException.Busy();
        double s = Math.Max(0, start), e = Math.Min(end, info.DurationSec);
        if (e - s < MinimumPart) throw SessionEditException.InvalidRange();

        foreach (var t in info.Tracks)
        {
            var path = AppPaths.TrackFile(id, t);
            if (!File.Exists(path)) continue;
            // Same container as the original.
            var tmp = Path.Combine(Path.GetDirectoryName(path)!, $".trim-{t.Raw()}{Path.GetExtension(path)}");
            AudioContainer.CopyRange(path, tmp, s, e);
            File.Move(tmp, path, overwrite: true);
        }
        if (SessionStore.LoadTranscript(id) is { } tr) SessionStore.SaveTranscript(Slice(tr, s, e), id);
        if (LoadAssist(id) is { } record)
        {
            // Live lines can start a moment after the saved length; keep them when the end stays.
            record["lines"] = SliceLines(record["lines"] as JsonArray, s, e >= info.DurationSec ? double.PositiveInfinity : e);
            JsonIO.WriteText(AppPaths.AssistFile(id), JsonIO.Format(record));
        }
        info.StartedAt = info.StartedAt.AddSeconds(s);
        info.DurationSec = e - s;
        SessionStore.Save(info);
        return info;
    }

    /// Split at `time` seconds: the original keeps the first part, a new session gets the rest.
    /// Returns the new (second) session.
    public static SessionInfo Split(string id, double time)
    {
        var info = SessionStore.Load(id);
        if (IsBusy(info.Status)) throw SessionEditException.Busy();
        if (time < MinimumPart || info.DurationSec - time < MinimumPart) throw SessionEditException.InvalidRange();

        var startedAt = info.StartedAt.AddSeconds(time);
        var newId = SessionStore.CreateSessionFolder(SessionStore.FolderName(startedAt));
        try
        {
            foreach (var t in info.Tracks)
            {
                var path = AppPaths.TrackFile(id, t);
                if (!File.Exists(path)) continue;
                // Part 2 is a new track: written as WAV (the Windows container), same sample format.
                AudioContainer.CopyRangeAsWav(path, AppPaths.NewTrack(newId, t), time, info.DurationSec);
            }
            var transcript = SessionStore.LoadTranscript(id);
            if (transcript is not null) SessionStore.SaveTranscript(Slice(transcript, time, info.DurationSec), newId);
            var second = info.Clone();
            second.Id = newId;
            second.Title = info.Title + " (Part 2)";
            second.StartedAt = startedAt;
            second.DurationSec = info.DurationSec - time;
            if (transcript is null && second.Status == SessionStatus.Done) second.Status = SessionStatus.Recorded;
            if (LoadAssist(id) is { } record)
                JsonIO.WriteText(AppPaths.AssistFile(newId), JsonIO.Format(SecondPart(record, time, double.PositiveInfinity)));
            SessionStore.Save(second);
            Trim(id, 0, time);
            return second;
        }
        catch
        {
            try { Directory.Delete(AppPaths.Session(newId), recursive: true); } catch { }
            throw;
        }
    }

    /// Split at a pause: part 2 starts just before speech resumes. With `removePause`, part 1 is
    /// also cut right after the last speech when that removes at least 1 s.
    public static SessionInfo Split(string id, Pause pause, bool removePause)
    {
        var second = Split(id, pause.SplitPoint);
        if (removePause && pause.SplitPoint - pause.KeepUntil >= MinimumPart) Trim(id, 0, pause.KeepUntil);
        return second;
    }

    /// The pause whose split point lies within 0.5 s of `time`, if any (the split sheet then
    /// offers "Remove the pause").
    public static Pause? PauseAt(IEnumerable<Pause> pauses, double time) =>
        pauses.Where(p => p.Kind == PauseKind.Inner && Math.Abs(p.SplitPoint - time) <= Pauses.SnapDistance).Cast<Pause?>().FirstOrDefault();

    /// The part of a transcript between `start` and `end`, moved to start at 0: words whose middle
    /// lies in [start, end); turns without word timings kept when their middle lies inside;
    /// speakers filtered to those used; normalized.
    public static Transcript Slice(Transcript t, double start, double end)
    {
        var output = t.Clone();
        var turns = new List<Turn>();
        foreach (var turn in t.Turns)
        {
            if (turn.Words.Count == 0)
            {
                var mid = (turn.Start + turn.End) / 2;
                if (!(mid >= start && mid < end)) continue;
                var c = turn.Clone();
                c.Start = Math.Max(0, turn.Start - start);
                c.End = Math.Min(end, turn.End) - start;
                turns.Add(c);
                continue;
            }
            var words = turn.Words
                .Where(w => (w.Start + w.End) / 2 >= start && (w.Start + w.End) / 2 < end)
                .Select(w => new Word(w.W, Math.Max(0, w.Start - start), Math.Min(end, w.End) - start))
                .ToList();
            if (words.Count == 0) continue;
            var k = turn.Clone();
            k.Words = words;
            k.Start = words[0].Start;
            k.End = words[^1].End;
            k.Text = string.Join(" ", words.Select(w => w.W));
            turns.Add(k);
        }
        output.Turns = turns;
        var used = turns.Select(x => x.Speaker).ToHashSet();
        output.Speakers = output.Speakers.Where(s => used.Contains(s.Id)).ToList();
        output.Normalize();
        return output;
    }

    // MARK: assist.json (free-form JSON, so this does not depend on the Assist types)

    static JsonObject? LoadAssist(string id)
    {
        var path = AppPaths.AssistFile(id);
        if (!File.Exists(path)) return null;
        try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject; } catch { return null; }
    }

    /// Assist lines (`{start, speaker, text}`) starting in [start, end), moved to start at 0.
    public static JsonArray SliceLines(JsonArray? lines, double start, double end)
    {
        var output = new JsonArray();
        if (lines is null) return output;
        foreach (var line in lines.OfType<JsonObject>())
        {
            var s = Number(line["start"]);
            if (!(s >= start && s < end)) continue;
            output.Add(new JsonObject
            {
                ["start"] = s - start,
                ["speaker"] = line["speaker"]?.DeepClone(),
                ["text"] = line["text"]?.DeepClone(),
            });
        }
        return output;
    }

    static double Number(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number
            ? double.Parse(v.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture) : 0;

    /// The `assist.json` for part 2 of a split: the same context and live panels, the lines from
    /// `time` on, and nothing of the first meeting's write-up (no recap meeting, upload or CRM times,
    /// created contacts, tables or recap draft); `speakersCarried` is kept.
    public static JsonObject SecondPart(JsonObject record, double time, double end)
    {
        var r = new JsonObject
        {
            ["context"] = record["context"]?.DeepClone(),
            ["state"] = record["state"]?.DeepClone(),
            ["lines"] = SliceLines(record["lines"] as JsonArray, time, end),
            ["createdContactIDs"] = new JsonArray(),
        };
        if (record["speakersCarried"] is { } carried) r["speakersCarried"] = carried.DeepClone();
        return r;
    }
}
