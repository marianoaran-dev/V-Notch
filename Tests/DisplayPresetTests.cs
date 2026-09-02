using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

public sealed class DisplayPresetTests
{
    [Fact]
    public void NotchSettingsClone_DeepCopiesDisplayPresets()
    {
        var settings = new NotchSettings();
        settings.DisplayPresets["day"] = new DisplayPresetSettings
        {
            Name = "Day",
            Monitors = new Dictionary<string, DisplayPresetMonitorSettings>
            {
                ["DISPLAY1:0"] = new() { Brightness = 80, Contrast = 46 }
            }
        };

        var clone = settings.Clone();
        clone.DisplayPresets["day"].Monitors["DISPLAY1:0"].Brightness = 22;

        Assert.Equal(80, settings.DisplayPresets["day"].Monitors["DISPLAY1:0"].Brightness);
        Assert.Equal(22, clone.DisplayPresets["day"].Monitors["DISPLAY1:0"].Brightness);
    }

    [Fact]
    public async Task PresetApply_UsesExactStoredValuesRegardlessOfLinkState()
    {
        using var monitorService = new FakeMonitorControlService(
            Monitor("DISPLAY1:0", 80, 46),
            Monitor("DISPLAY2:0", 80, 47));
        using var viewModel = new DisplayMonitorsViewModel(
            monitorService,
            new FakeDispatcherService());

        await viewModel.RefreshAsync();
        viewModel.IsAllMonitorsLinked = true;
        viewModel.Monitors[0].IsLinkEnabled = true;
        viewModel.Monitors[1].IsLinkEnabled = true;

        var preset = new Dictionary<string, DisplayPresetMonitorSettings>
        {
            ["DISPLAY1:0"] = new() { Brightness = 62, Contrast = 55 },
            ["DISPLAY2:0"] = new() { Brightness = 58, Contrast = 52 }
        };

        Assert.True(viewModel.ApplyPresetValues(preset));
        await viewModel.CommitPendingWritesAsync();

        Assert.Equal(62, viewModel.Monitors[0].Brightness);
        Assert.Equal(55, viewModel.Monitors[0].Contrast);
        Assert.Equal(58, viewModel.Monitors[1].Brightness);
        Assert.Equal(52, viewModel.Monitors[1].Contrast);
        Assert.Equal(4, monitorService.Writes.Count);
        Assert.Contains(monitorService.Writes, write => write.MonitorId == "DISPLAY1:0" && write.Control == MonitorControlKind.Brightness && write.Percentage == 62);
        Assert.Contains(monitorService.Writes, write => write.MonitorId == "DISPLAY2:0" && write.Control == MonitorControlKind.Contrast && write.Percentage == 52);
    }

    [Fact]
    public async Task PresetCapture_SnapshotsCurrentPerMonitorValues()
    {
        using var monitorService = new FakeMonitorControlService(
            Monitor("DISPLAY1:0", 80, 46),
            Monitor("DISPLAY2:0", 75, 44));
        using var viewModel = new DisplayMonitorsViewModel(
            monitorService,
            new FakeDispatcherService());

        await viewModel.RefreshAsync();
        viewModel.Monitors[0].Brightness = 71;
        viewModel.Monitors[1].Contrast = 49;

        var captured = viewModel.CapturePresetValues();

        Assert.Equal(71, captured["DISPLAY1:0"].Brightness);
        Assert.Equal(46, captured["DISPLAY1:0"].Contrast);
        Assert.Equal(75, captured["DISPLAY2:0"].Brightness);
        Assert.Equal(49, captured["DISPLAY2:0"].Contrast);
    }

    private static PhysicalMonitorSnapshot Monitor(string id, double brightness, double contrast) =>
        new(
            id,
            id,
            id.Split(':')[0],
            "LG 27UP850K-W",
            0,
            new MonitorFeatureSnapshot(true, brightness, (int)brightness, 100),
            new MonitorFeatureSnapshot(true, contrast, (int)contrast, 100));

    private sealed class FakeMonitorControlService : IMonitorControlService
    {
        private readonly IReadOnlyList<PhysicalMonitorSnapshot> _monitors;

        public List<MonitorWriteRequest> Writes { get; } = new();

        public FakeMonitorControlService(params PhysicalMonitorSnapshot[] monitors)
            => _monitors = monitors;

        public Task<IReadOnlyList<PhysicalMonitorSnapshot>> EnumerateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_monitors);

        public Task<MonitorWriteResult> SetValueAsync(
            PhysicalMonitorSnapshot monitor,
            MonitorControlKind control,
            double percentage,
            CancellationToken cancellationToken = default)
        {
            Writes.Add(new MonitorWriteRequest(monitor.Id, control, percentage));
            return Task.FromResult(MonitorWriteResult.Success());
        }

        public void Dispose() { }
    }
}
