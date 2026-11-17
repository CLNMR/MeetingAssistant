using System.Text;

namespace MeetingAssistant.Core.Text;

/// <summary>
/// A small text language identifier for the catch-up prompt (spec 02 §3.8). It scores the text's words
/// against lists of very common words per language (a word shared by several languages counts for each
/// of them in part) plus a few letters that only some languages use. The Mac uses Apple's
/// NLLanguageRecognizer; this needs no platform API and no model.
/// </summary>
public static class LanguageGuess
{
    sealed record Lang(string Code, string Name, string Letters, string[] Words);

    static readonly Lang[] Langs =
    {
        new("en", "English", "", Split("the and of to a in is it that for on with as was be are this have not you we they but at by from or an will can would there what so if about which when one all our your their been has had do does did were me my he she him her them us no yes just also more than then into out up because only how very need otherwise")),
        new("de", "German", "äöüß", Split("der die das und ist nicht ich wir sie es ein eine einen dem den des zu mit auf für von im auch sich noch nur wie aber oder wenn dann schon bis sonst wird werden haben hat sind war mal ja nein doch kann können muss müssen also eigentlich jetzt hier dass was wer uns euch ihr mir mich dir dich kein keine bei nach über unter vor genau gut")),
        new("fr", "French", "éèêàçùœâîô", Split("le la les et est un une des du de que qui pas je nous vous ils elle il sur pour dans avec ce cette mais ou au aux sont été avoir fait on oui non très plus bien aussi donc alors")),
        new("es", "Spanish", "ñáíóúé¿¡", Split("el la los las y es un una unos de que en no por para con se lo le su al del como pero más muy sí también está están ser hay tiene nosotros ellos esto eso ya")),
        new("it", "Italian", "àèéìòù", Split("il lo la gli le e è un una di che non per con su del della dei sono questo questa ma anche molto più come io noi voi loro hanno ha fare ci si già")),
        new("nl", "Dutch", "", Split("de het een en is van in niet dat die op te met voor zijn er ik we wij ze maar ook dan nog wel naar bij om als heeft hebben wordt worden kan geen")),
        new("pt", "Portuguese", "ãõçáâêó", Split("o a os as e é um uma de que não em para com por se do da dos das mais muito também está são ser tem eu nós eles isso já mas")),
        new("sv", "Swedish", "åäö", Split("och är det att en ett som inte jag vi de på för med till av har var den om men så också kan ska nu här")),
        new("pl", "Polish", "ąćęłńóśźż", Split("i w na nie się że jest to z do jak ale co tak czy jestem mamy są być po od już tylko bardzo")),
    };

    static string[] Split(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// How many languages list each word (shared words count less).
    static readonly Dictionary<string, int> Shares = Langs.SelectMany(l => l.Words.Distinct()).GroupBy(w => w)
        .ToDictionary(g => g.Key, g => g.Count());

    /// <summary>The most likely language code and its share of all evidence (0…1), or null without any evidence.</summary>
    public static (string Code, double Probability)? Detect(string text)
    {
        var scores = new double[Langs.Length];
        foreach (var w in Words(text.ToLowerInvariant()))
            for (int i = 0; i < Langs.Length; i++)
                if (Array.IndexOf(Langs[i].Words, w) >= 0) scores[i] += 1.0 / Shares[w];
        var lower = text.ToLowerInvariant();
        for (int i = 0; i < Langs.Length; i++)
            foreach (var ch in Langs[i].Letters)
                if (lower.Contains(ch)) scores[i] += 0.5;
        var total = scores.Sum();
        if (total <= 0) return null;
        var best = Array.IndexOf(scores, scores.Max());
        return (Langs[best].Code, scores[best] / total);
    }

    /// <summary>The English name of a language code from <see cref="Detect"/> ("de" → "German"), or the code.</summary>
    public static string EnglishName(string code) => Langs.FirstOrDefault(l => l.Code == code)?.Name ?? code;

    static IEnumerable<string> Words(string s)
    {
        var w = new StringBuilder();
        foreach (var ch in s)
        {
            if (char.IsLetter(ch)) { w.Append(ch); continue; }
            if (w.Length > 0) { yield return w.ToString(); w.Clear(); }
        }
        if (w.Length > 0) yield return w.ToString();
    }
}
