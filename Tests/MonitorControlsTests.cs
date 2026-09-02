using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class MonitorControlsTests
{
    [Theory]
    [InlineData(50u, 100u, 50d)]
    [InlineData(25u, 200u, 12.5d)]
    [InlineData(100u, 0u, 0d)]
    public void VcpValues_NormalizeToPercentage(uint current, uint maximum, double expected)
    {
        Assert.Equal(expected, MonitorValueNormalizer.ToPercentage(current, maximum), 6);
    }

    [Theory]
    [InlineData(62.0, 100u, 62u)]
    [InlineData(-10.0, 100u, 0u)]
    [InlineData(120.0, 100u, 100u)]
    [InlineData(50.0, 200u, 100u)]
    public void PercentageValues_MapBackToNativeMaximum(double percentage, uint maximum, uint expected)
    {
        Assert.Equal(expected, MonitorValueNormalizer.ToNative(percentage, maximum));
    }

    [Fact]
    public void LocalLink_PreservesPercentagePointDelta()
    {
        var monitor = Monitor("A", 62, 70, linked: true);

        var plan = MonitorLinkEngine.BuildPlan(
            new[] { monitor }, "A", MonitorControlKind.Brightness, 70, allMonitorsLinked: false);

        Assert.Equal(8, plan.Delta, 6);
        Assert.Contains(plan.Updates, update =>
            update.MonitorId == "A" && update.Control == MonitorControlKind.Brightness && update.Value == 70);
        Assert.Contains(plan.Updates, update =>
            update.MonitorId == "A" && update.Control == MonitorControlKind.Contrast && update.Value == 78);
    }

    [Fact]
    public void LocalLink_StopsAtFirstBoundaryAndPreservesOffset()
    {
        var monitor = Monitor("A", 2, 3, linked: true);

        var plan = MonitorLinkEngine.BuildPlan(
            new[] { monitor }, "A", MonitorControlKind.Contrast, -10, allMonitorsLinked: false);

        Assert.Equal(-2, plan.Delta, 6);
        Assert.Contains(plan.Updates, update =>
            update.Control == MonitorControlKind.Contrast && update.Value == 1);
        Assert.Contains(plan.Updates, update =>
            update.Control == MonitorControlKind.Brightness && update.Value == 0);
    }

    [Fact]
    public void LocalLink_HighBoundaryStopsBothControlsWithoutCollapsingDifference()
    {
        var monitor = Monitor("A", 73, 42, linked: true);

        var plan = MonitorLinkEngine.BuildPlan(
            new[] { monitor }, "A", MonitorControlKind.Contrast, 100, allMonitorsLinked: false);

        Assert.Equal(27, plan.Delta, 6);
        Assert.Contains(plan.Updates, update =>
            update.Control == MonitorControlKind.Brightness && update.Value == 100);
        Assert.Contains(plan.Updates, update =>
            update.Control == MonitorControlKind.Contrast && update.Value == 69);
    }

    [Fact]
    public void LocalLink_AtBoundaryReturnsCurrentValuesSoBoundSliderCanSnapBack()
    {
        var monitor = Monitor("A", 100, 49, linked: true);

        var plan = MonitorLinkEngine.BuildPlan(
            new[] { monitor }, "A", MonitorControlKind.Contrast, 60, allMonitorsLinked: false);

        Assert.Equal(0, plan.Delta, 6);
        Assert.Contains(plan.Updates, update =>
            update.Control == MonitorControlKind.Brightness && update.Value == 100);
        Assert.Contains(plan.Updates, update =>
            update.Control == MonitorControlKind.Contrast && update.Value == 49);
    }

    [Fact]
    public void AllMonitorsLink_PropagatesDeltaWithoutEqualisingValues()
    {
        var monitors = new[]
        {
            Monitor("A", 60, 40, linked: false),
            Monitor("B", 48, 35, linked: false)
        };

        var plan = MonitorLinkEngine.BuildPlan(
            monitors, "A", MonitorControlKind.Brightness, 70, allMonitorsLinked: true);

        Assert.Contains(plan.Updates, update =>
            update.MonitorId == "A" && update.Control == MonitorControlKind.Brightness && update.Value == 70);
        Assert.Contains(plan.Updates, update =>
            update.MonitorId == "B" && update.Control == MonitorControlKind.Brightness && update.Value == 58);
        Assert.DoesNotContain(plan.Updates, update =>
            update.MonitorId == "B" && update.Control == MonitorControlKind.Brightness && update.Value == 70);
    }

    [Fact]
    public void NestedLink_PropagatesPairOnEachLocallyLinkedTargetOnce()
    {
        var monitors = new[]
        {
            Monitor("A", 60, 50, linked: true),
            Monitor("B", 48, 98, linked: true),
            Monitor("C", 30, 20, linked: false)
        };

        var plan = MonitorLinkEngine.BuildPlan(
            monitors, "A", MonitorControlKind.Brightness, 70, allMonitorsLinked: true);

        Assert.Equal(2, plan.Delta, 6);
        Assert.Equal(5, plan.Updates.Count);
        Assert.Contains(plan.Updates, update => update.MonitorId == "A" && update.Control == MonitorControlKind.Brightness && update.Value == 62);
        Assert.Contains(plan.Updates, update => update.MonitorId == "A" && update.Control == MonitorControlKind.Contrast && update.Value == 52);
        Assert.Contains(plan.Updates, update => update.MonitorId == "B" && update.Control == MonitorControlKind.Brightness && update.Value == 50);
        Assert.Contains(plan.Updates, update => update.MonitorId == "B" && update.Control == MonitorControlKind.Contrast && update.Value == 100);
        Assert.Contains(plan.Updates, update => update.MonitorId == "C" && update.Control == MonitorControlKind.Brightness && update.Value == 32);
        Assert.Equal(plan.Updates.Count, plan.Updates.Select(update => (update.MonitorId, update.Control)).Distinct().Count());
    }

    [Fact]
    public void UnsupportedFeature_IsSkippedWithoutBreakingSupportedTarget()
    {
        var monitors = new[]
        {
            Monitor("A", 50, 50, linked: true),
            Monitor("B", 40, 40, linked: true, brightnessSupported: false)
        };

        var plan = MonitorLinkEngine.BuildPlan(
            monitors, "A", MonitorControlKind.Brightness, 60, allMonitorsLinked: true);

        Assert.DoesNotContain(plan.Updates, update => update.MonitorId == "B");
        Assert.Contains(plan.Updates, update =>
            update.MonitorId == "A" && update.Control == MonitorControlKind.Contrast && update.Value == 60);
    }

    [Fact]
    public async Task WriteScheduler_CoalescesDragValuesAndFlushesFinalValue()
    {
        var writes = new List<MonitorWriteRequest>();
        using var scheduler = new MonitorWriteScheduler(
            request =>
            {
                lock (writes) writes.Add(request);
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(5));

        scheduler.Queue(new MonitorWriteRequest("A", MonitorControlKind.Brightness, 40));
        scheduler.Queue(new MonitorWriteRequest("A", MonitorControlKind.Brightness, 62));
        scheduler.Queue(new MonitorWriteRequest("A", MonitorControlKind.Contrast, 71));

        await scheduler.FlushAsync();

        Assert.Equal(2, writes.Count);
        Assert.Contains(writes, write =>
            write.MonitorId == "A" && write.Control == MonitorControlKind.Brightness && write.Percentage == 62);
        Assert.Contains(writes, write =>
            write.MonitorId == "A" && write.Control == MonitorControlKind.Contrast && write.Percentage == 71);
    }

    [Fact]
    public void DisplayView_IsPartOfShellViewContract()
    {
        Assert.True(Enum.IsDefined(NotchView.DisplayMonitors));
    }

    private static MonitorLinkValues Monitor(
        string id,
        double brightness,
        double contrast,
        bool linked,
        bool brightnessSupported = true,
        bool contrastSupported = true) =>
        new(id, brightness, contrast, brightnessSupported, contrastSupported, linked);
}
