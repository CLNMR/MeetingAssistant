import Testing
@testable import MeetingAssistantCore

@Suite struct GhostModeTests {
    @Test func offIsAlwaysNormal() {
        for inside in [false, true] { for key in [false, true] {
            #expect(GhostMode.look(enabled: false, cursorInside: inside, keyHeld: key) == .init(opacity: 1, clickThrough: false))
        } }
    }

    @Test func onFadesUnderTheCursorAndPassesClicks() {
        #expect(GhostMode.look(enabled: true, cursorInside: true, keyHeld: false) == .init(opacity: 0.25, clickThrough: true))
        #expect(GhostMode.look(enabled: true, cursorInside: false, keyHeld: false) == .init(opacity: 1, clickThrough: true))
    }

    @Test func holdingTheKeyMakesItUsable() {
        #expect(GhostMode.look(enabled: true, cursorInside: true, keyHeld: true) == .init(opacity: 1, clickThrough: false))
        #expect(GhostMode.look(enabled: true, cursorInside: false, keyHeld: true) == .init(opacity: 1, clickThrough: false))
    }
}
