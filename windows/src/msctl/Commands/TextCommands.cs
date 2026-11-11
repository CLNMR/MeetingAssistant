using System.Globalization;
using MeetingAssistant.Core;
using MeetingAssistant.Core.Audio;
using MeetingAssistant.Core.Json;
using MeetingAssistant.Core.Storage;
using MeetingAssistant.Core.Text;

namespace Msctl.Commands;

/// Vocabulary and text clean-up (spec 04 §2–§5): the Mac's `vocab <session-id>` dry run, plus
/// commands to show/edit the vocabulary and to try spoken forms, fit, the invented-text check and
/// the Scribe+ prompt and answer checks without a Claude process.
public sealed class TextCommands : ICommandGroup
{
    /// msctl has no system spell checker: word lists from MEETINGASSISTANT_WORDLISTS (paths separated
    /// by the platform's path separator; plain lists or Hunspell .dic), else /usr/share/dict/words if present.
    static void UseWordLists()
    {
        if (SpellCheck.Checker is not null) return;
        var env = Environment.GetEnvironmentVariable("MEETINGASSISTANT_WORDLISTS");
        var paths = env is { Length: > 0 } ? env.Split(Path.PathSeparator) : new[] { "/usr/share/dict/words" };
        var checker = new WordListSpellChecker(paths);
        if (checker.Count > 0) SpellCheck.Checker = checker.IsKnown;
    }

    static string Joined(List<string> words) => string.Join(" ", words);

    public IEnumerable<Command> Commands => new[]
    {
        new Command("vocab", "vocab <session-id>                     what the vocabulary would change in a transcript (nothing is saved)",
            args =>
            {
                if (args.Length < 1) throw new UsageException();
                var t = SessionStore.LoadTranscript(args[0]) ?? throw new UsageException("no transcript");
                UseWordLists();
                var v = Vocabulary.Load();
                Console.WriteLine($"{v.ActiveTerms.Count} terms, {v.Replacements.Count} replacements");
                var m = v.Matcher();
                var changes = new Dictionary<string, int>();
                foreach (var turn in t.Turns)
                {
                    var before = turn.Words.Select(w => w.W).ToList();
                    var after = m.Apply(turn.Words).Select(w => w.W).ToList();
                    if (before.SequenceEqual(after)) continue;
                    var wa = Joined(before).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var wb = Joined(after).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    int i = 0;
                    while (i < wa.Length && i < wb.Length && wa[i] == wb[i]) i++;
                    int ja = wa.Length - 1, jb = wb.Length - 1;
                    while (ja >= i && jb >= i && wa[ja] == wb[jb]) { ja--; jb--; }
                    var from = ja >= i ? string.Join(" ", wa[i..(ja + 1)]) : "";
                    var to = jb >= i ? string.Join(" ", wb[i..(jb + 1)]) : "";
                    var key = $"{from} → {to}";
                    changes[key] = changes.GetValueOrDefault(key) + 1;
                }
                foreach (var (c, n) in changes.OrderByDescending(x => x.Value)) Console.WriteLine($"{n}× {c}");
                if (changes.Count == 0) Console.WriteLine("no changes");
                return Task.FromResult(0);
            }),

        new Command("vocab-show", "vocab-show                             counts and settings of vocabulary.json (terms with --terms)",
            args =>
            {
                var v = Vocabulary.Load();
                Console.WriteLine($"terms {v.Terms.Count}, replacements {v.Replacements.Count}, aya terms {v.AyaTerms.Count} (version {v.AyaTermsVersion}, fetched {(v.AyaFetchedAt is { } d ? IsoDateConverter.ToText(d) : "never")})");
                Console.WriteLine($"useAya {v.UseAya}, fuzzy {v.Fuzzy}, needs Aya refresh {v.NeedsAyaRefresh(DateTime.UtcNow)}");
                if (Args.Flag(args, "--terms"))
                {
                    foreach (var t in v.Terms) Console.WriteLine($"  {t}");
                    foreach (var r in v.Replacements) Console.WriteLine($"  {r.Spoken} → {r.Written}");
                }
                return Task.FromResult(0);
            }),

        new Command("vocab-add", "vocab-add <term> | vocab-add <spoken> --written <written>   add a term or a replacement",
            args =>
            {
                var written = Args.Option(args, "--written");
                var rest = Args.Positional(args, "--written");
                if (rest.Count < 1) throw new UsageException();
                var v = Vocabulary.Load();
                var added = written is null ? v.AddTerm(string.Join(" ", rest)) : v.AddReplacement(string.Join(" ", rest), written);
                if (added) v.Save();
                Console.WriteLine(added ? "added" : "unchanged");
                return Task.FromResult(0);
            }),

        new Command("vocab-apply", "vocab-apply <text…>                    the text with the vocabulary applied",
            args =>
            {
                if (args.Length < 1) throw new UsageException();
                UseWordLists();
                Console.WriteLine(Vocabulary.Load().Matcher().Apply(string.Join(" ", args)));
                return Task.FromResult(0);
            }),

        new Command("vocab-aya", "vocab-aya [--companies a;b] [--people a;b] [--projects a;b]   terms built from Aya titles (dry run)",
            args =>
            {
                static string[] List(string? s) => s?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
                var terms = Vocabulary.TermsFromAya(List(Args.Option(args, "--companies")), List(Args.Option(args, "--people")), List(Args.Option(args, "--projects")));
                foreach (var t in terms) Console.WriteLine(t);
                return Task.FromResult(0);
            }),

        new Command("vocab-roundtrip", "vocab-roundtrip [vocabulary.json]      read with the Vocabulary type and print as written",
            args =>
            {
                Console.Write(JsonIO.Serialize(JsonIO.Read<Vocabulary>(args.Length > 0 ? args[0] : Vocabulary.FilePath)));
                return Task.FromResult(0);
            }),

        new Command("spoken", "spoken <text…>                         hesitations, e-mail addresses and times (dictation clean-up)",
            args =>
            {
                Console.WriteLine(SpokenForms.Apply(string.Join(" ", args)));
                return Task.FromResult(0);
            }),

        new Command("fit", "fit <text…> --before <text> [--after <text>]   fit dictated text into the text around the cursor",
            args =>
            {
                var rest = Args.Positional(args, "--before", "--after");
                Console.WriteLine("[" + InsertionFit.Fit(string.Join(" ", rest), Args.Option(args, "--before") ?? "", Args.Option(args, "--after") ?? "") + "]");
                return Task.FromResult(0);
            }),

        new Command("invented", "invented <text…> [--voiced <s>] [--audio <file>]   is the text invented by the speech model?",
            args =>
            {
                var rest = Args.Positional(args, "--voiced", "--audio");
                double voiced = Args.Option(args, "--audio") is { } audio
                    ? InventedText.LongestVoicedRun(AudioFile.Read(audio))
                    : double.Parse(Args.Option(args, "--voiced") ?? "1", CultureInfo.InvariantCulture);
                var text = string.Join(" ", rest);
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "voiced run {0:F2} s, credit {1}, invented {2}",
                    voiced, InventedText.IsCredit(text), InventedText.IsInvented(text, voiced)));
                return Task.FromResult(0);
            }),

        new Command("scribeplus-prompt", "scribeplus-prompt <text…> [--app a] [--before b] [--after c] [--no-context] [--system]   the Scribe+ prompt",
            args =>
            {
                if (Args.Flag(args, "--system")) { Console.WriteLine(ScribePlus.System); return Task.FromResult(0); }
                var rest = Args.Positional(args, "--app", "--before", "--after");
                var text = string.Join(" ", rest);
                var v = Vocabulary.Load();
                var terms = ScribePlus.RelevantTerms(text, v.Terms, v.UseAya ? v.AyaTerms : new List<string>());
                Console.WriteLine(ScribePlus.Prompt(text, Args.Option(args, "--app"), Args.Option(args, "--before"), Args.Option(args, "--after"),
                    useContext: !Args.Flag(args, "--no-context"), terms));
                return Task.FromResult(0);
            }),

        new Command("scribeplus-check", "scribeplus-check <answer…> --source <dictation> [--before <context>]   post-processing and acceptance check",
            args =>
            {
                var source = Args.Option(args, "--source") ?? throw new UsageException();
                var rest = Args.Positional(args, "--source", "--before");
                var r = ScribePlus.Check(string.Join(" ", rest).Replace("\\n", "\n"), source, Args.Option(args, "--before")?.Replace("\\n", "\n"));
                Console.WriteLine(r.Problem is { } p ? $"(rejected: {p.ToString().ToLowerInvariant()}) {r.Text}" : $"plus:  {r.Text}");
                return Task.FromResult(0);
            }),
    };
}
