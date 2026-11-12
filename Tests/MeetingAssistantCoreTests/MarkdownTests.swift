import Foundation
import Testing
@testable import MeetingAssistantCore

@Suite struct MarkdownBlockTests {
    @Test func headingsParagraphsAndRules() {
        let b = Markdown.blocks("## Ausgangslage ##\nErste Zeile\nzweite Zeile\n\n---\n#Kein Titel")
        #expect(b == [
            .heading(level: 2, text: "Ausgangslage"),
            .paragraph("Erste Zeile\nzweite Zeile"),
            .rule,
            .paragraph("#Kein Titel"),
        ])
    }

    @Test func listsWithDepthNumbersTasksAndContinuation() {
        let b = Markdown.blocks("- Festival\n  - 2.000 Crew\n    weiter\n- Excel\n1. Eins\n2) Zwei\n- [ ] offen\n* [x] erledigt")
        #expect(b == [
            .item(depth: 0, marker: .bullet, text: "Festival"),
            .item(depth: 1, marker: .bullet, text: "2.000 Crew\nweiter"),
            .item(depth: 0, marker: .bullet, text: "Excel"),
            .item(depth: 0, marker: .number(1), text: "Eins"),
            .item(depth: 0, marker: .number(2), text: "Zwei"),
            .item(depth: 0, marker: .task(done: false), text: "offen"),
            .item(depth: 0, marker: .task(done: true), text: "erledigt"),
        ])
    }

    @Test func fourSpaceNesting() {
        let b = Markdown.blocks("1. A\n    - B\n        - C\n    - D")
        #expect(b.map { if case .item(let d, _, _) = $0 { return d } else { return -1 } } == [0, 1, 2, 1])
    }

    @Test func quotesAndCode() {
        let b = Markdown.blocks("> Zitat\n> weiter\n```swift\nlet a = 1\n\n**nicht fett**\n```\nDanach")
        #expect(b == [.quote("Zitat\nweiter"), .code("let a = 1\n\n**nicht fett**"), .paragraph("Danach")])
    }

    @Test func unclosedFenceRunsToTheEnd() {
        #expect(Markdown.blocks("```\na\nb") == [.code("a\nb")])
    }

    @Test func tables() {
        let b = Markdown.blocks("| Frage | Antwort |\n|---|:--:|\n| Budget | 10 \\| 20 |\n| Start |\n\nText")
        #expect(b == [
            .table(header: ["Frage", "Antwort"], rows: [["Budget", "10 | 20"], ["Start", ""]]),
            .paragraph("Text"),
        ])
        #expect(Markdown.blocks("a | b\n--- | ---\n1 | 2") == [.table(header: ["a", "b"], rows: [["1", "2"]])])
        // A pipe without a delimiter row is ordinary text.
        #expect(Markdown.blocks("a | b") == [.paragraph("a | b")])
    }

    @Test func crlfAndEmpty() {
        #expect(Markdown.blocks("") == [])
        #expect(Markdown.blocks("# A\r\n- b\r\n") == [.heading(level: 1, text: "A"), .item(depth: 0, marker: .bullet, text: "b")])
    }
}

@Suite struct MarkdownInlineTests {
    @Test func styles() {
        #expect(Markdown.inline("a **fett** und *kursiv* und ~~weg~~ und `code`") == [
            MarkdownRun("a "), MarkdownRun("fett", .bold), MarkdownRun(" und "), MarkdownRun("kursiv", .italic),
            MarkdownRun(" und "), MarkdownRun("weg", .strike), MarkdownRun(" und "), MarkdownRun("code", .code),
        ])
    }

    @Test func nestingAndBoldItalic() {
        #expect(Markdown.inline("**a *b* c**") == [MarkdownRun("a ", .bold), MarkdownRun("b", [.bold, .italic]), MarkdownRun(" c", .bold)])
        #expect(Markdown.inline("*a **b** c*") == [MarkdownRun("a ", .italic), MarkdownRun("b", [.bold, .italic]), MarkdownRun(" c", .italic)])
        #expect(Markdown.inline("***x***") == [MarkdownRun("x", [.bold, .italic])])
    }

    @Test func literalsStayText() {
        #expect(Markdown.inline("snake_case_name and 2 * 3 * 4") == [MarkdownRun("snake_case_name and 2 * 3 * 4")])
        #expect(Markdown.inline("**open") == [MarkdownRun("**open")])
        #expect(Markdown.inline("\\*kein\\* `**x**`") == [MarkdownRun("*kein* "), MarkdownRun("**x**", .code)])
        #expect(Markdown.inline("_kursiv_") == [MarkdownRun("kursiv", .italic)])
    }

    @Test func links() {
        #expect(Markdown.inline("siehe [die **Doku**](https://example.com/a) hier") == [
            MarkdownRun("siehe "), MarkdownRun("die ", link: "https://example.com/a"),
            MarkdownRun("Doku", .bold, link: "https://example.com/a"), MarkdownRun(" hier"),
        ])
        #expect(Markdown.inline("[kein link] (x)") == [MarkdownRun("[kein link] (x)")])
    }

    @Test func nonASCIIOffsets() {
        #expect(Markdown.inline("Größe **über** 😀 *ä*") == [MarkdownRun("Größe "), MarkdownRun("über", .bold), MarkdownRun(" 😀 "), MarkdownRun("ä", .italic)])
    }
}

@Suite struct MarkdownHighlightTests {
    @Test func headingListAndInline() {
        let spans = Markdown.highlight("## Titel\n- **a**")
        #expect(spans == [
            MarkdownSpan(0, 8, .heading(2)), MarkdownSpan(0, 3, .syntax),
            MarkdownSpan(9, 2, .listMarker),
            MarkdownSpan(11, 2, .syntax), MarkdownSpan(13, 1, .styled(.bold, link: false)), MarkdownSpan(14, 2, .syntax),
        ])
    }

    @Test func fencesAndTables() {
        let spans = Markdown.highlight("```\n**x**\n```\n|a|\n|-|")
        #expect(spans.filter { $0.kind == .codeBlock }.map(\.location) == [0, 4, 10])
        #expect(!spans.contains { if case .styled = $0.kind { return true } else { return false } })
        #expect(spans.filter { $0.kind == .table }.map(\.location) == [14, 18])
    }
}

@Suite struct MarkdownEditTests {
    func r(_ l: Int, _ n: Int = 0) -> NSRange { NSRange(location: l, length: n) }

    @Test func wrapAndUnwrap() {
        #expect(Markdown.wrap("ab cd", r(3, 2), marker: "**") == .init(text: "ab **cd**", selection: r(5, 2)))
        // Selected with the markers, or just inside them: both remove.
        #expect(Markdown.wrap("ab **cd**", r(3, 6), marker: "**") == .init(text: "ab cd", selection: r(3, 2)))
        #expect(Markdown.wrap("ab **cd**", r(5, 2), marker: "**") == .init(text: "ab cd", selection: r(3, 2)))
        // Trailing space of a double-clicked word stays outside.
        #expect(Markdown.wrap("ab cd ef", r(3, 3), marker: "*") == .init(text: "ab *cd* ef", selection: r(4, 2)))
        // Italic inside bold is added, not mistaken for an existing marker.
        #expect(Markdown.wrap("**cd**", r(2, 2), marker: "*") == .init(text: "***cd***", selection: r(3, 2)))
        #expect(Markdown.wrap("***cd***", r(3, 2), marker: "*") == .init(text: "**cd**", selection: r(2, 2)))
    }

    @Test func wrapEmptySelection() {
        #expect(Markdown.wrap("ab", r(2), marker: "**") == .init(text: "ab****", selection: r(4)))
        #expect(Markdown.wrap("ab****", r(4), marker: "**") == .init(text: "ab", selection: r(2)))
    }

    @Test func toggleBulletsAndNumbers() {
        #expect(Markdown.toggleLines("a\nb", r(0, 3), style: .bullet) == .init(text: "- a\n- b", selection: r(0, 7)))
        #expect(Markdown.toggleLines("- a\n- b", r(0, 7), style: .bullet) == .init(text: "a\nb", selection: r(0, 3)))
        #expect(Markdown.toggleLines("- a\n\n* b", r(0, 8), style: .numbered) == .init(text: "1. a\n\n2. b", selection: r(0, 10)))
        // Cursor stays in the content.
        #expect(Markdown.toggleLines("x\nabc", r(4), style: .task) == .init(text: "x\n- [ ] abc", selection: r(10)))
        // A selection ending at the start of the next line leaves that line out.
        #expect(Markdown.toggleLines("a\nb", r(0, 2), style: .quote).text == "> a\nb")
    }

    @Test func toggleHeading() {
        #expect(Markdown.toggleLines("- Titel", r(4), style: .heading(2)) == .init(text: "## Titel", selection: r(5)))
        #expect(Markdown.toggleLines("## Titel", r(5), style: .heading(2)) == .init(text: "Titel", selection: r(2)))
        #expect(Markdown.toggleLines("# Titel", r(0), style: .heading(2)) == .init(text: "## Titel", selection: r(3)))
    }

    @Test func newlineContinuesLists() {
        #expect(Markdown.newline("- a", r(3)) == .init(text: "- a\n- ", selection: r(6)))
        #expect(Markdown.newline("  9. a", r(6)) == .init(text: "  9. a\n  10. ", selection: r(13)))
        #expect(Markdown.newline("* [x] a", r(7)) == .init(text: "* [x] a\n* [ ] ", selection: r(14)))
        #expect(Markdown.newline("> a", r(3)) == .init(text: "> a\n> ", selection: r(6)))
        // Splitting an item moves the rest to the new one.
        #expect(Markdown.newline("- ab", r(3)) == .init(text: "- a\n- b", selection: r(6)))
        // An empty item ends the list.
        #expect(Markdown.newline("- a\n- ", r(6)) == .init(text: "- a\n", selection: r(4)))
        // Not in a list, inside the marker, or with a selection: ordinary return.
        #expect(Markdown.newline("text", r(4)) == nil)
        #expect(Markdown.newline("- a", r(1)) == nil)
        #expect(Markdown.newline("- abc", r(2, 2)) == nil)
    }

    @Test func indentAndOutdent() {
        #expect(Markdown.indent("- a\n- b", r(6), outdent: false) == .init(text: "- a\n  - b", selection: r(8)))
        #expect(Markdown.indent("1. a\n1. b", r(9), outdent: false) == .init(text: "1. a\n   1. b", selection: r(12)))
        #expect(Markdown.indent("- a\n  - b", r(9), outdent: true) == .init(text: "- a\n- b", selection: r(7)))
        #expect(Markdown.indent("- a\n    - b\n      - c", r(4, 17), outdent: true) == .init(text: "- a\n- b\n    - c", selection: r(4, 11)))
        #expect(Markdown.indent("text", r(0), outdent: false) == nil)
    }
}
