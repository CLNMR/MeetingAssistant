using MeetingAssistant.Core.Scribe;
using Xunit;

namespace MeetingAssistant.Core.Tests.Scribe;

public class GhostModeTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void OffIsAlwaysNormal(bool inside, bool key) =>
        Assert.Equal(new GhostMode.Look(1, false), GhostMode.Compute(false, inside, key));

    [Fact]
    public void OnFadesUnderTheCursorAndPassesClicks()
    {
        Assert.Equal(new GhostMode.Look(0.25, true), GhostMode.Compute(true, cursorInside: true, keyHeld: false));
        Assert.Equal(new GhostMode.Look(1, true), GhostMode.Compute(true, cursorInside: false, keyHeld: false));
    }

    [Fact]
    public void HoldingTheKeyMakesItUsable()
    {
        Assert.Equal(new GhostMode.Look(1, false), GhostMode.Compute(true, cursorInside: true, keyHeld: true));
        Assert.Equal(new GhostMode.Look(1, false), GhostMode.Compute(true, cursorInside: false, keyHeld: true));
    }
}
