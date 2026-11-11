using System.Globalization;
using System.Text.RegularExpressions;
using MeetingAssistant.Core.Model;

namespace MeetingAssistant.Core.Assist;

/// <summary>Speaker names across the live meeting and the final transcript (spec 02 §5).</summary>
public static class SpeakerNames
{
    /// <summary>Labels the app makes up before anyone is named: empty, "?", "…", "Them…", "Speaker &lt;integer&gt;".</summary>
    public static bool IsGeneric(string name)
    {
        var n = AssistText.TrimWs(name);
        if (n.Length == 0 || n == "?" || n == "…" || n.StartsWith("Them", StringComparison.Ordinal)) return true;
        return n.StartsWith("Speaker ", StringComparison.Ordinal)
               && int.TryParse(n[8..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);
    }

    /// <summary>The name a live label stands for: a non-generic label as is, else the attendee Claude matched to it.</summary>
    public static string Resolved(string label, IEnumerable<Attendee> attendees)
    {
        if (!IsGeneric(label)) return label;
        return attendees.FirstOrDefault(a => a.Speaker == label && !IsGeneric(a.Name))?.Name ?? label;
    }

    /// <summary>Minimum votes for a speaker to get a name (spec 02 §5.3).</summary>
    public const int MinVotes = 4;
    /// <summary>Minimum share of the best name (spec 02 §5.3).</summary>
    public const double MinShare = 0.6;

    /// <summary>
    /// Names for the speakers of the final transcript, taken from the live lines said at the same time
    /// (spec 02 §5.3). Speakers that already have a name (or are "me") are left alone.
    /// </summary>
    public static Dictionary<string, string> CarryOver(Transcript t, IEnumerable<AssistLine> lines, IReadOnlyList<Attendee> attendees)
    {
        var named = lines.Select(l => (Line: l, Name: Resolved(l.Speaker, attendees))).Where(x => !IsGeneric(x.Name)).ToList();
        var output = new Dictionary<string, string>();
        if (named.Count == 0) return output;
        var votes = new Dictionary<string, Dictionary<string, int>>();
        var order = new List<string>();
        foreach (var turn in t.Turns)
        {
            var sp = t.Speakers.FirstOrDefault(s => s.Id == turn.Speaker);
            if (sp is null || sp.IsMe || !IsGeneric(sp.Label)) continue;
            foreach (var (line, name) in named)
            {
                if (line.Start < turn.Start - 1 || line.Start > turn.End + 0.5) continue;
                if (!votes.TryGetValue(turn.Speaker, out var v)) { votes[turn.Speaker] = v = new(); order.Add(turn.Speaker); }
                v[name] = v.GetValueOrDefault(name) + Math.Max(1, AssistText.WordCount(line.Text));
            }
        }
        // Most certain speakers first, so one name goes to the speaker it fits best.
        var ranked = order.Select(id =>
        {
            var v = votes[id];
            var total = v.Values.Sum();
            var best = v.Aggregate((a, b) => b.Value > a.Value ? b : a);
            return (Id: id, Name: best.Key, Share: (double)best.Value / total, Total: total);
        }).Where(x => x.Total >= MinVotes).OrderByDescending(x => x.Share).ToList();
        var taken = new HashSet<string>();
        foreach (var (id, name, share, _) in ranked)
        {
            if (share < MinShare || taken.Contains(name)) continue;
            output[id] = name;
            taken.Add(name);
        }
        return output;
    }

    /// <summary>Replaces a generic label ("Speaker 2") with a name in text, as a whole word only (spec 02 §5.2).</summary>
    public static string Replacing(string old, string @new, string text)
    {
        if (old.Length == 0 || old == @new) return text;
        var pattern = @"(?<![\p{L}\p{N}])" + Regex.Escape(old) + @"(?![\p{L}\p{N}])";
        return Regex.Replace(text, pattern, @new.Replace("$", "$$"));
    }
}

/// <summary>The few facts about a person worth showing at a glance (spec 02 §5.4).</summary>
public static class PersonInfo
{
    /// Field groups in the order they are shown; the first matching field of each group is kept.
    public static readonly IReadOnlyList<string[]> Groups = new[]
    {
        new[] { "position", "rolle", "funktion", "job", "titel", "title", "role" },
        new[] { "firma", "unternehmen", "company", "organisation", "kunde" },
        new[] { "abteilung", "department", "bereich", "team" },
        new[] { "e-mail", "email", "mail" },
        new[] { "mobil", "mobile", "handy" },
        new[] { "telefon", "phone", "tel" },
        new[] { "linkedin" },
        new[] { "entscheider", "decision", "einfluss" },
        new[] { "notiz", "notes", "note", "beschreibung", "description", "info", "bemerkung" },
    };

    public static List<LabeledValue> Important(IEnumerable<LabeledValue> details, int limit = 8)
    {
        var list = details.ToList();
        var output = new List<LabeledValue>();
        var used = new HashSet<string>();
        foreach (var g in Groups)
        {
            var hit = list.FirstOrDefault(d =>
            {
                if (used.Contains(d.Key)) return false;
                var l = d.Label.ToLowerInvariant();
                var k = d.Key.ToLowerInvariant();
                return g.Any(w => l.Contains(w) || k.StartsWith(w, StringComparison.Ordinal));
            });
            if (hit is not null && AssistText.TrimAll(hit.Value).Length > 0)
            {
                output.Add(new LabeledValue(hit.Key, hit.Label, AssistText.Prefix(hit.Value, 300)));
                used.Add(hit.Key);
            }
            if (output.Count >= limit) break;
        }
        return output;
    }
}

/// <summary>Matching typed or spoken names to people (spec 03 §6.7 "Name matching").</summary>
public static class PersonMatch
{
    /// <summary>
    /// Same name ignoring case and accents, else the only entry whose name parts each start with a typed
    /// word ("Erika" → "Erika Musterfrau", but not when there are two Erikas). Generic names never match.
    /// </summary>
    public static T? Match<T>(string name, IEnumerable<T> items, Func<T, string> key) where T : class
    {
        var n = AssistText.TrimWs(name);
        if (n.Length == 0 || SpeakerNames.IsGeneric(n)) return null;
        var list = items.ToList();
        var hit = list.FirstOrDefault(i => AssistText.SameName(key(i), n));
        if (hit is not null) return hit;
        var words = n.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var ci = CultureInfo.InvariantCulture.CompareInfo;
        const CompareOptions opts = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        var hits = list.Where(i =>
        {
            var parts = key(i).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return words.All(w => parts.Any(p => ci.IsPrefix(p, w, opts)));
        }).ToList();
        return hits.Count == 1 ? hits[0] : null;
    }
}
