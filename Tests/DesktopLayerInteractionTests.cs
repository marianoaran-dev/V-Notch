using Xunit;

namespace VNotch.Tests;

public class DesktopLayerInteractionTests
{
    [Theory]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, false, false, true, false)]
    [InlineData(false, false, false, false, true)]
    [InlineData(true, true, true, true, true)]
    public void ShouldKeepDesktopPromotion_WhenAnyInteractionIsActive(
        bool pointerInHoverZone,
        bool pointerOverNotch,
        bool inputCapturedWithin,
        bool keyboardFocusWithin,
        bool ownedWindowInteractionActive)
    {
        bool keepPromoted = MainWindow.ShouldKeepDesktopPromotion(
            pointerInHoverZone,
            pointerOverNotch,
            inputCapturedWithin,
            keyboardFocusWithin,
            ownedWindowInteractionActive);

        Assert.True(keepPromoted);
    }

    [Fact]
    public void ShouldKeepDesktopPromotion_WhenInteractionEnds_ReturnsFalse()
    {
        bool keepPromoted = MainWindow.ShouldKeepDesktopPromotion(
            pointerInHoverZone: false,
            pointerOverNotch: false,
            inputCapturedWithin: false,
            keyboardFocusWithin: false,
            ownedWindowInteractionActive: false);

        Assert.False(keepPromoted);
    }
}
