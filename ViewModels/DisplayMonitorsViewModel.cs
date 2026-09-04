using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.ViewModels;

public sealed class DisplayMonitorRowViewModel : ObservableObject
{
    private double _brightness;
    private double _contrast;
    private bool _isLinkEnabled;
    private string _statusText;

    public PhysicalMonitorSnapshot Monitor { get; }
    public string Id => Monitor.Id;
    public string DisplayName => Monitor.DisplayName;
    public string Description => Monitor.Description;
    public bool IsBrightnessSupported => Monitor.Brightness.IsSupported;
    public bool IsContrastSupported => Monitor.Contrast.IsSupported;

    public double Brightness
    {
        get => _brightness;
        set
        {
            var normalized = MonitorLinkEngine.ClampPercentage(value);
            if (SetProperty(ref _brightness, normalized)) OnPropertyChanged(nameof(BrightnessText));
        }
    }

    public double Contrast
    {
        get => _contrast;
        set
        {
            var normalized = MonitorLinkEngine.ClampPercentage(value);
            if (SetProperty(ref _contrast, normalized)) OnPropertyChanged(nameof(ContrastText));
        }
    }

    public string BrightnessText => IsBrightnessSupported ? $"{Brightness:0}%" : "Unavailable";
    public string ContrastText => IsContrastSupported ? $"{Contrast:0}%" : "Unavailable";

    public bool IsLinkEnabled
    {
        get => _isLinkEnabled;
        set => SetProperty(ref _isLinkEnabled, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public DisplayMonitorRowViewModel(PhysicalMonitorSnapshot monitor, bool isLinkEnabled)
    {
        Monitor = monitor;
        _brightness = MonitorLinkEngine.ClampPercentage(monitor.Brightness.CurrentPercent);
        _contrast = MonitorLinkEngine.ClampPercentage(monitor.Contrast.CurrentPercent);
        _isLinkEnabled = isLinkEnabled;
        _statusText = GetInitialStatus(monitor);
    }

    internal void SetWriteStatus(string status) => StatusText = status;

    private static string GetInitialStatus(PhysicalMonitorSnapshot monitor)
    {
        if (!monitor.Brightness.IsSupported && !monitor.Contrast.IsSupported)
            return "DDC/CI controls unavailable";
        if (!monitor.Brightness.IsSupported || !monitor.Contrast.IsSupported)
            return "Some controls unavailable";
        return "Ready";
    }
}

public sealed class DisplayMonitorsViewModel : ObservableObject, IDisposable
{
    private readonly IMonitorControlService _monitorService;
    private readonly IDispatcherService _dispatcher;
    private readonly MonitorWriteScheduler _writeScheduler;
    private readonly TimeSpan _operationTimeout;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _refreshTask;
    private Task<IReadOnlyList<PhysicalMonitorSnapshot>>? _enumerationTask;
    private int _refreshGeneration;
    private bool _isApplyingPlan;
    private bool _isAllMonitorsLinked;
    private readonly HashSet<string> _linkedMonitorIds = new(StringComparer.Ordinal);
    private bool _isLoading;
    private string _statusText = "Open Display to detect monitors";
    private bool _disposed;

    public ObservableCollection<DisplayMonitorRowViewModel> Monitors { get; } = new();
    public event Action<bool, IReadOnlyCollection<string>>? LinkStateChanged;

    public bool IsAllMonitorsLinked
    {
        get => _isAllMonitorsLinked;
        set
        {
            if (SetProperty(ref _isAllMonitorsLinked, value))
                PublishLinkState();
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public DisplayMonitorsViewModel(
        IMonitorControlService monitorService,
        IDispatcherService dispatcher,
        TimeSpan? operationTimeout = null)
    {
        _monitorService = monitorService;
        _dispatcher = dispatcher;
        _operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(5);
        _writeScheduler = new MonitorWriteScheduler(WriteRequestAsync);
    }

    public void LoadLinkState(bool allMonitorsLinked, IEnumerable<string>? linkedMonitorIds)
    {
        _isAllMonitorsLinked = allMonitorsLinked;
        _linkedMonitorIds.Clear();
        if (linkedMonitorIds != null)
        {
            foreach (var id in linkedMonitorIds.Where(id => !string.IsNullOrWhiteSpace(id)))
                _linkedMonitorIds.Add(id);
        }
    }

    public void SetMonitorLink(DisplayMonitorRowViewModel row, bool enabled)
    {
        if (!Monitors.Contains(row)) return;

        row.IsLinkEnabled = enabled;
        if (enabled)
            _linkedMonitorIds.Add(row.Id);
        else
            _linkedMonitorIds.Remove(row.Id);
        PublishLinkState();
    }

    private void PublishLinkState()
        => LinkStateChanged?.Invoke(_isAllMonitorsLinked, _linkedMonitorIds.ToArray());

    public Task RefreshAsync()
    {
        if (_disposed) return Task.CompletedTask;
        if (_refreshTask is { IsCompleted: false }) return _refreshTask;

        var generation = Interlocked.Increment(ref _refreshGeneration);
        IsLoading = true;
        StatusText = "Detecting physical monitors…";
        _refreshTask = RefreshCoreAsync(generation);
        return _refreshTask;
    }

    private async Task RefreshCoreAsync(int generation)
    {
        try
        {
            // A manual refresh should observe the values the user just selected,
            // not race the scheduler's short debounce window and read stale DDC
            // state immediately before a pending write reaches the monitor.
            var flushTask = _writeScheduler.FlushAsync();
            if (!await CompletesWithinAsync(flushTask).ConfigureAwait(false))
            {
                PublishRefreshTimeout(generation,
                    "Monitor writes are still recovering; try refresh again shortly.");
                return;
            }
            await flushTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(
                "DISPLAY-DDC",
                $"Pending display write could not be committed before refresh: {ex.Message}");
        }

        try
        {
            // A Dxva2 call can remain blocked while a display is waking. Keep one
            // outstanding native enumeration and bound how long the UI refresh
            // waits for it. Subsequent refreshes reuse that task instead of
            // spawning an unbounded pile of native calls.
            if (_enumerationTask == null || _enumerationTask.IsCompleted)
            {
                _enumerationTask = _monitorService.EnumerateAsync(_lifetime.Token);
                _ = _enumerationTask.ContinueWith(
                    task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            var enumerationTask = _enumerationTask;
            if (!await CompletesWithinAsync(enumerationTask).ConfigureAwait(false))
            {
                PublishRefreshTimeout(generation,
                    "Monitor detection timed out while the displays were waking. Try refresh again shortly.");
                return;
            }

            var monitors = await enumerationTask.ConfigureAwait(false);
            Publish(generation, monitors, null);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Publish(generation, Array.Empty<PhysicalMonitorSnapshot>(), ex.Message);
        }
    }

    private async Task<bool> CompletesWithinAsync(Task operation)
    {
        if (operation.IsCompleted) return true;

        var timeoutTask = Task.Delay(_operationTimeout, _lifetime.Token);
        var completed = await Task.WhenAny(operation, timeoutTask).ConfigureAwait(false);
        return completed == operation;
    }

    private void PublishRefreshTimeout(int generation, string message)
    {
        void Apply()
        {
            if (_disposed || generation != _refreshGeneration) return;
            IsLoading = false;
            StatusText = message;
            RuntimeLog.Warn("DISPLAY-DDC", message);
        }

        if (_dispatcher.CheckAccess()) Apply();
        else _dispatcher.BeginInvoke(Apply);
    }

    private void Publish(
        int generation,
        IReadOnlyList<PhysicalMonitorSnapshot> monitors,
        string? error)
    {
        void Apply()
        {
            if (_disposed || generation != _refreshGeneration) return;

            // Preserve any current in-memory changes before replacing monitor rows,
            // then merge them with the durable link-state set loaded at startup.
            foreach (var row in Monitors)
            {
                if (row.IsLinkEnabled)
                    _linkedMonitorIds.Add(row.Id);
                else
                    _linkedMonitorIds.Remove(row.Id);
            }
            Monitors.Clear();
            foreach (var monitor in monitors)
            {
                Monitors.Add(new DisplayMonitorRowViewModel(
                    monitor,
                    _linkedMonitorIds.Contains(monitor.Id)));
            }

            IsLoading = false;
            if (error != null)
            {
                StatusText = "Monitor detection failed; controls remain unavailable.";
            }
            else if (Monitors.Count == 0)
            {
                StatusText = "No physical monitors with DDC/CI were detected.";
            }
            else
            {
                var unsupported = Monitors.Count(row =>
                    !row.IsBrightnessSupported || !row.IsContrastSupported);
                StatusText = unsupported == 0
                    ? $"{Monitors.Count} monitor{(Monitors.Count == 1 ? "" : "s")} ready"
                    : $"{Monitors.Count} monitor{(Monitors.Count == 1 ? "" : "s")} detected · unavailable controls are disabled";
            }
        }

        if (_dispatcher.CheckAccess()) Apply();
        else _dispatcher.BeginInvoke(Apply);
    }

    public void ApplyUserChange(
        DisplayMonitorRowViewModel source,
        MonitorControlKind control,
        double oldValue,
        double requestedValue)
    {
        if (_disposed || _isApplyingPlan || !Monitors.Contains(source)) return;
        if (control == MonitorControlKind.Brightness && !source.IsBrightnessSupported) return;
        if (control == MonitorControlKind.Contrast && !source.IsContrastSupported) return;

        var values = BuildLinkValues();
        var sourceIndex = values.FindIndex(value => value.Id == source.Id);
        if (sourceIndex < 0) return;

        // A Slider.ValueChanged event can run after TwoWay binding has already
        // updated the source property. Reinsert the event's old value so the
        // planner always computes the intended percentage-point delta.
        values[sourceIndex] = control == MonitorControlKind.Brightness
            ? values[sourceIndex] with { Brightness = oldValue }
            : values[sourceIndex] with { Contrast = oldValue };

        var plan = MonitorLinkEngine.BuildPlan(
            values,
            source.Id,
            control,
            requestedValue,
            IsAllMonitorsLinked);
        if (plan.Updates.Count == 0) return;

        _isApplyingPlan = true;
        try
        {
            foreach (var update in plan.Updates)
            {
                var row = Monitors.FirstOrDefault(candidate => candidate.Id == update.MonitorId);
                if (row == null) continue;

                if (update.Control == MonitorControlKind.Brightness)
                {
                    if (!row.IsBrightnessSupported) continue;
                    row.Brightness = update.Value;
                }
                else
                {
                    if (!row.IsContrastSupported) continue;
                    row.Contrast = update.Value;
                }

                if (Math.Abs(plan.Delta) > 0.0001)
                {
                    _writeScheduler.Queue(new MonitorWriteRequest(
                        row.Id,
                        update.Control,
                        update.Value));
                }
            }

            StatusText = Math.Abs(plan.Delta) > 0.0001
                ? "Writing monitor changes…"
                : "Linked controls reached their shared limit";
        }
        finally
        {
            _isApplyingPlan = false;
        }
    }

    public Dictionary<string, DisplayPresetMonitorSettings> CapturePresetValues()
    {
        var captured = new Dictionary<string, DisplayPresetMonitorSettings>(StringComparer.Ordinal);
        foreach (var row in Monitors)
        {
            if (!row.IsBrightnessSupported && !row.IsContrastSupported) continue;
            captured[row.Id] = new DisplayPresetMonitorSettings
            {
                Brightness = row.Brightness,
                Contrast = row.Contrast
            };
        }
        return captured;
    }

    public bool ApplyPresetValues(IReadOnlyDictionary<string, DisplayPresetMonitorSettings> presetValues)
    {
        if (_disposed || presetValues.Count == 0) return false;

        var appliedAny = false;
        _isApplyingPlan = true;
        try
        {
            foreach (var row in Monitors)
            {
                if (!presetValues.TryGetValue(row.Id, out var values)) continue;

                if (row.IsBrightnessSupported)
                {
                    row.Brightness = values.Brightness;
                    _writeScheduler.Queue(new MonitorWriteRequest(
                        row.Id,
                        MonitorControlKind.Brightness,
                        row.Brightness));
                    appliedAny = true;
                }

                if (row.IsContrastSupported)
                {
                    row.Contrast = values.Contrast;
                    _writeScheduler.Queue(new MonitorWriteRequest(
                        row.Id,
                        MonitorControlKind.Contrast,
                        row.Contrast));
                    appliedAny = true;
                }
            }

            StatusText = appliedAny
                ? "Applying display preset…"
                : "Preset does not match the connected monitors";
        }
        finally
        {
            _isApplyingPlan = false;
        }

        return appliedAny;
    }

    public void ReportPresetSaved(string name)
        => StatusText = $"{name} preset saved";

    public Task CommitPendingWritesAsync() => _writeScheduler.FlushAsync();

    private List<MonitorLinkValues> BuildLinkValues() => Monitors.Select(row =>
        new MonitorLinkValues(
            row.Id,
            row.Brightness,
            row.Contrast,
            row.IsBrightnessSupported,
            row.IsContrastSupported,
            row.IsLinkEnabled)).ToList();

    private async Task WriteRequestAsync(MonitorWriteRequest request)
    {
        PhysicalMonitorSnapshot? monitor = null;
        void FindMonitor() => monitor = Monitors.FirstOrDefault(row => row.Id == request.MonitorId)?.Monitor;
        if (_dispatcher.CheckAccess()) FindMonitor();
        else _dispatcher.Invoke(FindMonitor);
        if (monitor == null) return;

        var result = await _monitorService.SetValueAsync(
            monitor,
            request.Control,
            request.Percentage,
            _lifetime.Token).ConfigureAwait(false);
        if (result.WasStale || _disposed) return;

        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            var row = Monitors.FirstOrDefault(candidate => candidate.Id == request.MonitorId);
            if (row == null) return;

            if (result.Succeeded)
            {
                row.SetWriteStatus("Ready");
                StatusText = "Monitor changes applied";
            }
            else
            {
                row.SetWriteStatus("Write unavailable");
                StatusText = "A monitor change was unavailable; other controls remain active.";
                RuntimeLog.Warn(
                    "DISPLAY-DDC",
                    $"{row.DisplayName} {request.Control} {request.Percentage:F0}% failed: {result.Error ?? "Unknown monitor error"}");
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _writeScheduler.Dispose();
        _lifetime.Dispose();
    }
}
