using MeetingAssistant.Core.Text;
using Xunit;

namespace MeetingAssistant.Core.Tests.Text;

public class MarkdownBlockTests
{
    static MarkdownItem Item(int d, MarkdownMarker m, string t) => new(d, m, t);

    [Fact]
    public void HeadingsParagraphsAndRules() =>
        Assert.Equal(new MarkdownBlock[]
        {
            new MarkdownHeading(2, "Ausgangslage"),
            new MarkdownParagraph("Erste Zeile\nzweite Zeile"),
            new MarkdownRule(),
            new MarkdownParagraph("#Kein Titel"),
        }, Markdown.Blocks("## Ausgangslage ##\nErste Zeile\nzweite Zeile\n\n---\n#Kein Titel"));

    [Fact]
    public void ListsWithDepthNumbersTasksAndContinuation() =>
        Assert.Equal(new MarkdownBlock[]
        {
            Item(0, MarkdownMarker.Bullet, "Festival"),
            Item(1, MarkdownMarker.Bullet, "2.000 Crew\nweiter"),
            Item(0, MarkdownMarker.Bullet, "Excel"),
            Item(0, MarkdownMarker.Num(1), "Eins"),
            Item(0, MarkdownMarker.Num(2), "Zwei"),
            Item(0, MarkdownMarker.Task(false), "offen"),
            Item(0, MarkdownMarker.Task(true), "erledigt"),
        }, Markdown.Blocks("- Festival\n  - 2.000 Crew\n    weiter\n- Excel\n1. Eins\n2) Zwei\n- [ ] offen\n* [x] erledigt"));

    [Fact]
    public void FourSpaceNesting() =>
        Assert.Equal(new[] { 0, 1, 2, 1 }, Markdown.Blocks("1. A\n    - B\n        - C\n    - D").Select(b => ((MarkdownItem)b).Depth));

    [Fact]
    public void QuotesAndCode() =>
        Assert.Equal(new MarkdownBlock[] { new MarkdownQuote("Zitat\nweiter"), new MarkdownCode("let a = 1\n\n**nicht fett**"), new MarkdownParagraph("Danach") },
                     Markdown.Blocks("> Zitat\n> weiter\n```swift\nlet a = 1\n\n**nicht fett**\n```\nDanach"));

    [Fact]
    public void UnclosedFenceRunsToTheEnd() =>
        Assert.Equal(new MarkdownBlock[] { new MarkdownCode("a\nb") }, Markdown.Blocks("```\na\nb"));

    static void AssertTable(MarkdownBlock b, string[] header, string[][] rows)
    {
        var t = Assert.IsType<MarkdownTable>(b);
        Assert.Equal(header, t.Header);
        Assert.Equal(rows.Length, t.Rows.Count);
        for (int i = 0; i < rows.Length; i++) Assert.Equal(rows[i], t.Rows[i]);
    }

    [Fact]
    public void Tables()
    {
        var b = Markdown.Blocks("| Frage | Antwort |\n|---|:--:|\n| Budget | 10 \\| 20 |\n| Start |\n\nText");
        Assert.Equal(2, b.Count);
        AssertTable(b[0], ["Frage", "Antwort"], [["Budget", "10 | 20"], ["Start", ""]]);
        Assert.Equal(new MarkdownParagraph("Text"), b[1]);
        var b2 = Markdown.Blocks("a | b\n--- | ---\n1 | 2");
        AssertTable(Assert.Single(b2), ["a", "b"], [["1", "2"]]);
        // A pipe without a delimiter row is ordinary text.
        Assert.Equal(new MarkdownBlock[] { new MarkdownParagraph("a | b") }, Markdown.Blocks("a | b"));
    }

    [Fact]
    public void CrlfAndEmpty()
    {
        Assert.Empty(Markdown.Blocks(""));
        Assert.Equal(new MarkdownBlock[] { new MarkdownHeading(1, "A"), Item(0, MarkdownMarker.Bullet, "b") }, Markdown.Blocks("# A\r\n- b\r\n"));
    }
}

public class MarkdownInlineTests
{
    static MarkdownRun R(string t, MarkdownStyle s = MarkdownStyle.None, string? link = null) => new(t, s, link);
    const MarkdownStyle B = MarkdownStyle.Bold, I = MarkdownStyle.Italic;

    [Fact]
    public void Styles() =>
        Assert.Equal(new[]
        {
            R("a "), R("fett", B), R(" und "), R("kursiv", I), R(" und "), R("weg", MarkdownStyle.Strike), R(" und "), R("code", MarkdownStyle.Code),
        }, Markdown.Inline("a **fett** und *kursiv* und ~~weg~~ und `code`"));

    [Fact]
    public void NestingAndBoldItalic()
    {
        Assert.Equal(new[] { R("a ", B), R("b", B | I), R(" c", B) }, Markdown.Inline("**a *b* c**"));
        Assert.Equal(new[] { R("a ", I), R("b", B | I), R(" c", I) }, Markdown.Inline("*a **b** c*"));
        Assert.Equal(new[] { R("x", B | I) }, Markdown.Inline("***x***"));
    }

    [Fact]
    public void LiteralsStayText()
    {
        Assert.Equal(new[] { R("snake_case_name and 2 * 3 * 4") }, Markdown.Inline("snake_case_name and 2 * 3 * 4"));
        Assert.Equal(new[] { R("**open") }, Markdown.Inline("**open"));
        Assert.Equal(new[] { R("*kein* "), R("**x**", MarkdownStyle.Code) }, Markdown.Inline("\\*kein\\* `**x**`"));
        Assert.Equal(new[] { R("kursiv", I) }, Markdown.Inline("_kursiv_"));
    }

    [Fact]
    public void Links()
    {
        Assert.Equal(new[]
        {
            R("siehe "), R("die ", link: "https://example.com/a"), R("Doku", B, "https://example.com/a"), R(" hier"),
        }, Markdown.Inline("siehe [die **Doku**](https://example.com/a) hier"));
        Assert.Equal(new[] { R("[kein link] (x)") }, Markdown.Inline("[kein link] (x)"));
    }

    [Fact]
    public void NonAsciiOffsets() =>
        Assert.Equal(new[] { R("Größe "), R("über", B), R(" 😀 "), R("ä", I) }, Markdown.Inline("Größe **über** 😀 *ä*"));
}

public class MarkdownHighlightTests
{
    [Fact]
    public void HeadingListAndInline() =>
        Assert.Equal(new[]
        {
            new MarkdownSpan(0, 8, MarkdownSpanKind.Heading, Level: 2), new MarkdownSpan(0, 3, MarkdownSpanKind.Syntax),
            new MarkdownSpan(9, 2, MarkdownSpanKind.ListMarker),
            new MarkdownSpan(11, 2, MarkdownSpanKind.Syntax), new MarkdownSpan(13, 1, MarkdownSpanKind.Styled, Style: MarkdownStyle.Bold),
            new MarkdownSpan(14, 2, MarkdownSpanKind.Syntax),
        }, Markdown.Highlight("## Titel\n- **a**"));

    [Fact]
    public void FencesAndTables()
    {
        var spans = Markdown.Highlight("```\n**x**\n```\n|a|\n|-|");
        Assert.Equal(new[] { 0, 4, 10 }, spans.Where(s => s.Kind == MarkdownSpanKind.CodeBlock).Select(s => s.Start));
        Assert.DoesNotContain(spans, s => s.Kind == MarkdownSpanKind.Styled);
        Assert.Equal(new[] { 14, 18 }, spans.Where(s => s.Kind == MarkdownSpanKind.Table).Select(s => s.Start));
    }
}

public class MarkdownEditTests
{
    static MarkdownEditResult E(string t, int s, int l = 0) => new(t, s, l);

    [Fact]
    public void WrapAndUnwrap()
    {
        Assert.Equal(E("ab **cd**", 5, 2), Markdown.Wrap("ab cd", 3, 2, "**"));
        // Selected with the markers, or just inside them: both remove.
        Assert.Equal(E("ab cd", 3, 2), Markdown.Wrap("ab **cd**", 3, 6, "**"));
        Assert.Equal(E("ab cd", 3, 2), Markdown.Wrap("ab **cd**", 5, 2, "**"));
        // Trailing space of a double-clicked word stays outside.
        Assert.Equal(E("ab *cd* ef", 4, 2), Markdown.Wrap("ab cd ef", 3, 3, "*"));
        // Italic inside bold is added, not mistaken for an existing marker.
        Assert.Equal(E("***cd***", 3, 2), Markdown.Wrap("**cd**", 2, 2, "*"));
        Assert.Equal(E("**cd**", 2, 2), Markdown.Wrap("***cd***", 3, 2, "*"));
    }

    [Fact]
    public void WrapEmptySelection()
    {
        Assert.Equal(E("ab****", 4), Markdown.Wrap("ab", 2, 0, "**"));
        Assert.Equal(E("ab", 2), Markdown.Wrap("ab****", 4, 0, "**"));
    }

    [Fact]
    public void ToggleBulletsAndNumbers()
    {
        Assert.Equal(E("- a\n- b", 0, 7), Markdown.ToggleLines("a\nb", 0, 3, MarkdownLineStyle.Bullet));
        Assert.Equal(E("a\nb", 0, 3), Markdown.ToggleLines("- a\n- b", 0, 7, MarkdownLineStyle.Bullet));
        Assert.Equal(E("1. a\n\n2. b", 0, 10), Markdown.ToggleLines("- a\n\n* b", 0, 8, MarkdownLineStyle.Numbered));
        // Cursor stays in the content.
        Assert.Equal(E("x\n- [ ] abc", 10), Markdown.ToggleLines("x\nabc", 4, 0, MarkdownLineStyle.Task));
        // A selection ending at the start of the next line leaves that line out.
        Assert.Equal("> a\nb", Markdown.ToggleLines("a\nb", 0, 2, MarkdownLineStyle.Quote).Text);
    }

    [Fact]
    public void ToggleHeading()
    {
        Assert.Equal(E("## Titel", 5), Markdown.ToggleLines("- Titel", 4, 0, MarkdownLineStyle.Heading, 2));
        Assert.Equal(E("Titel", 2), Markdown.ToggleLines("## Titel", 5, 0, MarkdownLineStyle.Heading, 2));
        Assert.Equal(E("## Titel", 3), Markdown.ToggleLines("# Titel", 0, 0, MarkdownLineStyle.Heading, 2));
    }

    [Fact]
    public void NewlineContinuesLists()
    {
        Assert.Equal(E("- a\n- ", 6), Markdown.Newline("- a", 3, 0));
        Assert.Equal(E("  9. a\n  10. ", 13), Markdown.Newline("  9. a", 6, 0));
        Assert.Equal(E("* [x] a\n* [ ] ", 14), Markdown.Newline("* [x] a", 7, 0));
        Assert.Equal(E("> a\n> ", 6), Markdown.Newline("> a", 3, 0));
        // Splitting an item moves the rest to the new one.
        Assert.Equal(E("- a\n- b", 6), Markdown.Newline("- ab", 3, 0));
        // An empty item ends the list.
        Assert.Equal(E("- a\n", 4), Markdown.Newline("- a\n- ", 6, 0));
        // Not in a list, inside the marker, or with a selection: ordinary return.
        Assert.Null(Markdown.Newline("text", 4, 0));
        Assert.Null(Markdown.Newline("- a", 1, 0));
        Assert.Null(Markdown.Newline("- abc", 2, 2));
    }

    [Fact]
    public void IndentAndOutdent()
    {
        Assert.Equal(E("- a\n  - b", 8), Markdown.Indent("- a\n- b", 6, 0, outdent: false));
        Assert.Equal(E("1. a\n   1. b", 12), Markdown.Indent("1. a\n1. b", 9, 0, outdent: false));
        Assert.Equal(E("- a\n- b", 7), Markdown.Indent("- a\n  - b", 9, 0, outdent: true));
        Assert.Equal(E("- a\n- b\n    - c", 4, 11), Markdown.Indent("- a\n    - b\n      - c", 4, 17, outdent: true));
        Assert.Null(Markdown.Indent("text", 0, 0, outdent: false));
    }
}
