using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using VNotch.Models;
using Xunit;

namespace VNotch.Tests;

public sealed class HoverLauncherShellTests
{
    [Fact]
    public void LauncherLivesOutsideClippedNotchContentAndSeparatesNavigationFromUtilities()
    {
        var document = XDocument.Load(Path.Combine(FindRepositoryRoot(), "Windows", "MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var innerClip = document.Descendants(presentation + "Border")
            .Single(element => (string?)element.Attribute(xaml + "Name") == "InnerClipBorder");
        var launcher = document.Descendants(presentation + "Grid")
            .Single(element => (string?)element.Attribute(xaml + "Name") == "HoverLauncherDock");

        Assert.DoesNotContain(launcher, innerClip.DescendantsAndSelf());
        Assert.Equal("Collapsed", (string?)launcher.Attribute("Visibility"));

        string[] expectedButtons =
        [
            "HoverLauncherHomeButton",
            "HoverLauncherShelfButton",
            "HoverLauncherTimerButton",
            "HoverLauncherAudioButton",
            "HoverLauncherDisplayButton",
            "HoverLauncherPiggyButton"
        ];

        var primaryDock = launcher.Descendants(presentation + "Border")
            .Single(element => (string?)element.Attribute(xaml + "Name") == "HoverLauncherPrimaryDock");
        var utilityDock = launcher.Descendants(presentation + "Border")
            .Single(element => (string?)element.Attribute(xaml + "Name") == "HoverLauncherUtilityDock");

        var primaryButtons = primaryDock.Descendants(presentation + "Button").ToArray();
        Assert.Equal(expectedButtons.Length, primaryButtons.Length);
        Assert.Equal(expectedButtons, primaryButtons.Select(button => (string?)button.Attribute(xaml + "Name")));
        Assert.All(primaryButtons, button => Assert.False(string.IsNullOrWhiteSpace((string?)button.Attribute("ToolTip"))));
        Assert.All(primaryButtons.Take(5), button => Assert.Equal("0,0,10,0", (string?)button.Attribute("Margin")));

        var utilityButtons = utilityDock.Descendants(presentation + "Button").ToArray();
        Assert.Equal(
            new[] { "HoverLauncherSettingsButton", "HoverLauncherExitButton" },
            utilityButtons.Select(button => (string?)button.Attribute(xaml + "Name")));
        Assert.All(utilityButtons, button => Assert.False(string.IsNullOrWhiteSpace((string?)button.Attribute("ToolTip"))));

        var columns = launcher.Element(presentation + "Grid.ColumnDefinitions")!
            .Elements(presentation + "ColumnDefinition")
            .ToArray();
        Assert.Equal("28", (string?)columns[1].Attribute("Width"));

        var launcherButtonStyle = document.Descendants(presentation + "Style")
            .Single(element => (string?)element.Attribute(xaml + "Key") == "HoverLauncherButton");
        var launcherSetters = launcherButtonStyle.Elements(presentation + "Setter").ToArray();
        Assert.Equal("48", (string?)launcherSetters.Single(x => (string?)x.Attribute("Property") == "Width").Attribute("Value"));
        Assert.Equal("48", (string?)launcherSetters.Single(x => (string?)x.Attribute("Property") == "Height").Attribute("Value"));
        Assert.Contains(launcherButtonStyle.Descendants(presentation + "Trigger"), trigger =>
            (string?)trigger.Attribute("Property") == "Tag" && (string?)trigger.Attribute("Value") == "True");

        var legacyNavHost = document.Descendants(presentation + "Grid")
            .Single(element => (string?)element.Attribute(xaml + "Name") == "LegacyNavHost");
        Assert.Equal("Collapsed", (string?)legacyNavHost.Attribute("Visibility"));
        Assert.Equal("False", (string?)legacyNavHost.Attribute("IsHitTestVisible"));

        var legacySettings = document.Descendants(presentation + "Border")
            .Single(element => (string?)element.Attribute(xaml + "Name") == "SettingsButton");
        var legacyExit = document.Descendants(presentation + "Border")
            .Single(element => (string?)element.Attribute(xaml + "Name") == "ExitButton");
        Assert.Equal("Collapsed", (string?)legacySettings.Attribute("Visibility"));
        Assert.Equal("False", (string?)legacySettings.Attribute("IsHitTestVisible"));
        Assert.Equal("Collapsed", (string?)legacyExit.Attribute("Visibility"));
        Assert.Equal("False", (string?)legacyExit.Attribute("IsHitTestVisible"));
    }

    [Fact]
    public void IdleShellUsesV14QuotaOnlyLayoutAndLargerMinimumSize()
    {
        var document = XDocument.Load(Path.Combine(FindRepositoryRoot(), "Windows", "MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var quotaPanel = document.Descendants(presentation + "Grid")
            .Single(element => (string?)element.Attribute(xaml + "Name") == "QuotaGlancePanel");
        var buttons = quotaPanel.Descendants(presentation + "Button").ToArray();

        Assert.Equal(2, buttons.Length);
        Assert.Equal(
            new[] { "FiveHourQuotaGlanceButton", "WeeklyQuotaGlanceButton" },
            buttons.Select(button => (string?)button.Attribute(xaml + "Name")));
        Assert.Equal("Right", (string?)quotaPanel.Attribute("HorizontalAlignment"));
        Assert.Equal(340, MainWindow.FloatingControlsIdleMinWidth);
        Assert.Equal(52, MainWindow.FloatingControlsIdleMinHeight);
        Assert.Equal(420, new NotchSettings().Width);
        Assert.Equal(52, new NotchSettings().Height);
    }

    [Fact]
    public void ActiveLauncherDestinationTracksAllSixViews()
    {
        Assert.Equal(MainWindow.HoverLauncherDestination.Home,
            MainWindow.ResolveActiveHoverLauncherDestination(false, false, false, false, false));
        Assert.Equal(MainWindow.HoverLauncherDestination.FileShelf,
            MainWindow.ResolveActiveHoverLauncherDestination(true, false, false, false, false));
        Assert.Equal(MainWindow.HoverLauncherDestination.Timer,
            MainWindow.ResolveActiveHoverLauncherDestination(false, true, false, false, false));
        Assert.Equal(MainWindow.HoverLauncherDestination.Audio,
            MainWindow.ResolveActiveHoverLauncherDestination(false, false, true, false, false));
        Assert.Equal(MainWindow.HoverLauncherDestination.Display,
            MainWindow.ResolveActiveHoverLauncherDestination(false, false, false, true, false));
        Assert.Equal(MainWindow.HoverLauncherDestination.PiggyBank,
            MainWindow.ResolveActiveHoverLauncherDestination(false, false, false, false, true));
    }

    [Theory]
    [InlineData(147, 294)]
    [InlineData(154, 308)]
    public void FileShelfPanelUsesDoublePrimaryHeight(double primaryHeight, double expected)
    {
        Assert.Equal(expected, MainWindow.CalculateSecondaryViewHeight(primaryHeight));
    }

    [Fact]
    public void CollapsedNotchClickDefaultsToPiggyBank()
    {
        Assert.Equal(MainWindow.HoverLauncherDestination.PiggyBank, MainWindow.DefaultIdleClickDestination);
    }

    [Fact]
    public void CompactSizeAndBehaviourSettingsRemainInteractive()
    {
        var root = FindRepositoryRoot();
        var settingsXaml = XDocument.Load(Path.Combine(root, "Windows", "SettingsWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement Named(string name) => settingsXaml.Descendants()
            .Single(element => (string?)element.Attribute(xaml + "Name") == name);

        var width = Named("WidthSlider");
        var height = Named("HeightSlider");
        var hover = Named("HoverExpandCheck");
        var reopen = Named("ReopenLastViewCheck");
        var settingsWindow = settingsXaml.Root!;

        Assert.Equal("340", (string?)width.Attribute("Minimum"));
        Assert.Equal("600", (string?)width.Attribute("Maximum"));
        Assert.Equal("52", (string?)height.Attribute("Minimum"));
        Assert.Equal("80", (string?)height.Attribute("Maximum"));
        Assert.Equal("HoverExpandCheck_Changed", (string?)hover.Attribute("Checked"));
        Assert.Equal("HoverExpandCheck_Changed", (string?)hover.Attribute("Unchecked"));
        Assert.Equal("ReopenLastViewCheck_Changed", (string?)reopen.Attribute("Checked"));
        Assert.Equal("ReopenLastViewCheck_Changed", (string?)reopen.Attribute("Unchecked"));
        Assert.Equal("CanResize", (string?)settingsWindow.Attribute("ResizeMode"));
        Assert.Equal("CenterScreen", (string?)settingsWindow.Attribute("WindowStartupLocation"));
        Assert.Equal("720", (string?)settingsWindow.Attribute("MinWidth"));
        Assert.Equal("500", (string?)settingsWindow.Attribute("MinHeight"));

        var main = File.ReadAllText(Path.Combine(root, "Windows", "MainWindow.xaml.cs"));
        var launcher = File.ReadAllText(Path.Combine(root, "Windows", "MainWindow.HoverLauncher.cs"));
        Assert.Contains("ArmHoverExpand();", main, StringComparison.Ordinal);
        Assert.Contains("ResolveIdleExpandDestination()", launcher, StringComparison.Ordinal);
    }

    [Fact]
    public void PendingLauncherDestinationIsPreparedBeforeFirstExpandedFrame()
    {
        var animation = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Windows", "MainWindow.Animation.cs"));

        Assert.Contains("HoverLauncherDestination directExpandDestination", animation, StringComparison.Ordinal);
        Assert.Contains("revealContent = ExpandedContent", animation, StringComparison.Ordinal);
        Assert.Contains("revealContent = SecondaryContent", animation, StringComparison.Ordinal);
        Assert.Contains("revealContent = TimerContent", animation, StringComparison.Ordinal);
        Assert.Contains("revealContent = AudioContent", animation, StringComparison.Ordinal);
        Assert.Contains("revealContent = DisplayContent", animation, StringComparison.Ordinal);
        Assert.Contains("revealContent = PiggyBankContent", animation, StringComparison.Ordinal);
        Assert.Contains("revealContent.BeginAnimation(OpacityProperty, fadeInAnim)", animation, StringComparison.Ordinal);
        Assert.DoesNotContain("CompletePendingHoverLauncherNavigation()", animation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Home", "Home")]
    [InlineData("FileShelf", "FileShelf")]
    [InlineData("Timer", "Timer")]
    [InlineData("Audio", "Audio")]
    [InlineData("Display", "Display")]
    [InlineData("PiggyBank", "PiggyBank")]
    [InlineData("piggybank", "PiggyBank")]
    public void PersistedLauncherDestinationParsesAllSupportedValues(string persisted, string expected)
    {
        Assert.Equal(expected, MainWindow.ParsePersistedHoverLauncherDestination(persisted).ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("UnknownPanel")]
    public void InvalidPersistedLauncherDestinationFallsBackToHome(string? persisted)
    {
        Assert.Equal(
            MainWindow.HoverLauncherDestination.Home,
            MainWindow.ParsePersistedHoverLauncherDestination(persisted));
    }

    [Theory]
    [InlineData("Home")]
    [InlineData("FileShelf")]
    [InlineData("Timer")]
    [InlineData("Audio")]
    [InlineData("Display")]
    [InlineData("PiggyBank")]
    public void ReopenLastViewUsesRememberedDestinationWhenEnabled(string rememberedName)
    {
        var remembered = MainWindow.ParsePersistedHoverLauncherDestination(rememberedName);
        Assert.Equal(
            remembered,
            MainWindow.ResolveIdleExpandDestination(
                reopenLastViewOnExpand: true,
                remembered));
    }

    [Fact]
    public void ReopenLastViewDisabledPreservesPiggyDefaultExpansion()
    {
        Assert.Equal(
            MainWindow.HoverLauncherDestination.PiggyBank,
            MainWindow.ResolveIdleExpandDestination(
                reopenLastViewOnExpand: false,
                MainWindow.HoverLauncherDestination.Display));
    }

    [Fact]
    public void FileShelfToTimerTransitionPersistsCompletedDestination()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Windows", "MainWindow.Timer.cs"));
        int methodStart = source.IndexOf("private void SwitchFromSecondaryToTimerView()", StringComparison.Ordinal);
        int nextMethod = source.IndexOf("private void SwitchFromTimerToPrimaryView()", methodStart, StringComparison.Ordinal);
        string method = source[methodStart..nextMethod];

        Assert.Contains("RememberActiveHoverLauncherDestination();", method, StringComparison.Ordinal);
    }

    [Fact]
    public void RememberedDisplayColdOpenUsesLatestDisplayHeightForHost()
    {
        var height = MainWindow.ResolveDirectExpandSettledHostHeight(
            MainWindow.HoverLauncherDestination.Display,
            primaryHeight: 147,
            timerHeight: 310,
            audioHeight: 420,
            displayHeight: 612,
            piggyBankHeight: 326);

        Assert.Equal(612, height);
    }

    [Fact]
    public void RememberedFileShelfColdOpenUsesDoublePrimaryHeightForHost()
    {
        var height = MainWindow.ResolveDirectExpandSettledHostHeight(
            MainWindow.HoverLauncherDestination.FileShelf,
            primaryHeight: 154,
            timerHeight: 310,
            audioHeight: 420,
            displayHeight: 612,
            piggyBankHeight: 326);

        Assert.Equal(308, height);
    }

    [Fact]
    public void DisplayToPiggyTransitionKeepsPiggyPreparedGeometryPinned()
    {
        Assert.False(MainWindow.ShouldResetIncomingAutoSize(
            incomingIsAudio: false,
            incomingIsPiggyBank: true,
            incomingIsExpandedContent: false,
            notchFromWidth: 660,
            notchToWidth: 650));

        Assert.True(MainWindow.ShouldResetIncomingAutoSize(
            incomingIsAudio: false,
            incomingIsPiggyBank: false,
            incomingIsExpandedContent: false,
            notchFromWidth: 660,
            notchToWidth: 650));
    }

    [Fact]
    public void PiggyPanelNoLongerContainsPreviewSliders()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "Windows", "MainWindow.xaml"));
        var piggy = File.ReadAllText(Path.Combine(root, "Windows", "MainWindow.PiggyBank.cs"));

        Assert.DoesNotContain("PiggyPreviewSlider", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildPiggyPreviewSection", piggy, StringComparison.Ordinal);
        Assert.DoesNotContain("PREVIEW", piggy, StringComparison.Ordinal);
    }

    [Fact]
    public void AllLauncherDestinationsHaveAUsefulEmptyOrIdleState()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "Windows", "MainWindow.xaml"));
        var shelf = File.ReadAllText(Path.Combine(root, "Windows", "MainWindow.Secondary.cs"));
        var timer = File.ReadAllText(Path.Combine(root, "Windows", "MainWindow.Timer.cs"));
        var audio = File.ReadAllText(Path.Combine(root, "Windows", "MainWindow.AudioView.Builders.cs"));
        var display = File.ReadAllText(Path.Combine(root, "Windows", "MainWindow.DisplayView.Builders.cs"));
        var piggy = File.ReadAllText(Path.Combine(root, "Windows", "MainWindow.PiggyBank.cs"));

        Assert.Contains("No media playing", xaml, StringComparison.Ordinal);
        Assert.Contains("ShelfPlaceholderPanel.Visibility = shelfEmpty", shelf, StringComparison.Ordinal);
        Assert.Contains("UpdateTimerDisplay", timer, StringComparison.Ordinal);
        Assert.Contains("No active application audio sessions.", audio, StringComparison.Ordinal);
        Assert.Contains("No compatible external displays detected.", display, StringComparison.Ordinal);
        Assert.Contains("Quota unavailable", piggy, StringComparison.Ordinal);
    }

    [Fact]
    public void HoverPolicyKeepsDockEngagedAcrossNotchToLauncherTransition()
    {
        Assert.True(MainWindow.ShouldRevealHoverLauncher(false, false, false, false, false, false, true));
        Assert.False(MainWindow.ShouldRevealHoverLauncher(true, false, false, false, false, false, true));
        Assert.False(MainWindow.ShouldRevealHoverLauncher(false, true, false, false, false, false, true));
        Assert.False(MainWindow.ShouldRevealHoverLauncher(false, false, true, false, false, false, true));
        Assert.False(MainWindow.ShouldRevealHoverLauncher(false, false, false, true, false, false, true));
        Assert.False(MainWindow.ShouldRevealHoverLauncher(false, false, false, false, true, false, true));
        Assert.False(MainWindow.ShouldRevealHoverLauncher(false, false, false, false, false, true, true));
        Assert.False(MainWindow.ShouldRevealHoverLauncher(false, false, false, false, false, false, false));

        Assert.True(MainWindow.ShouldKeepHoverLauncherEngaged(true, false));
        Assert.True(MainWindow.ShouldKeepHoverLauncherEngaged(false, true));
        Assert.False(MainWindow.ShouldKeepHoverLauncherEngaged(false, false));
    }

    [Fact]
    public void HoverCollapseDelayRearmsThroughExpansionGrace()
    {
        var now = new DateTime(2026, 9, 4, 7, 30, 0, DateTimeKind.Utc);

        Assert.Equal(800,
            MainWindow.ResolveHoverCollapseTimerDelayMs(500, now.AddMilliseconds(800), now), 3);
        Assert.Equal(500,
            MainWindow.ResolveHoverCollapseTimerDelayMs(500, now.AddMilliseconds(-1), now), 3);
    }

    [Fact]
    public void LauncherLeaveParticipatesInExpandedAutoCollapse()
    {
        var launcher = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Windows", "MainWindow.HoverLauncher.cs"));

        Assert.Contains("_hoverCollapseTimer.Stop();", launcher, StringComparison.Ordinal);
        Assert.Contains("RequestHoverCollapseAfterPointerExit(\"HoverLauncherDock_MouseLeave\")", launcher, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateNotificationIconIsNeverRenderedInNotchPanels()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Windows", "MainWindow.UpdateNotification.cs"));
        int showStart = source.IndexOf("private void ShowUpdateNotification()", StringComparison.Ordinal);
        int hideStart = source.IndexOf("private void HideUpdateNotification()", showStart, StringComparison.Ordinal);
        string showMethod = source[showStart..hideStart];

        Assert.Contains("Visibility = Visibility.Collapsed", showMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("Visibility = Visibility.Visible", showMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void PiggyShellRefreshUsesFiveMinuteStalenessWindow()
    {
        var now = new DateTime(2026, 9, 3, 7, 0, 0, DateTimeKind.Utc);

        Assert.True(MainWindow.IsPiggyShellRefreshDue(DateTime.MinValue, now));
        Assert.False(MainWindow.IsPiggyShellRefreshDue(now.AddMinutes(-4).AddSeconds(-59), now));
        Assert.True(MainWindow.IsPiggyShellRefreshDue(now.AddMinutes(-5), now));
    }

    [Fact]
    public void PhysicalHoverBoundsTrackDpiScaledNotchPosition()
    {
        var bounds = MainWindow.CalculatePhysicalHoverBounds(
            fixedX: 1308,
            fixedY: 0,
            windowWidth: 1224,
            dpiScale: 1.5,
            notchWidthDip: 230,
            notchHeightDip: 34,
            containerTopDip: 0,
            containerTranslateYDip: 0);

        Assert.Equal(1747.5, bounds.Left, 3);
        Assert.Equal(0, bounds.Top, 3);
        Assert.Equal(345, bounds.Width, 3);
        Assert.Equal(51, bounds.Height, 3);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "V-Notch.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the V-Notch repository root.");
    }
}
