using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace VNotch.Services;

/// <summary>
/// Native monitor configuration transport. No physical monitor handle leaves
/// this class; every acquired array is destroyed in a finally-backed lease.
/// </summary>
public sealed class DdcMonitorService : IMonitorControlService
{
    private const byte BrightnessVcpCode = 0x10;
    private const byte ContrastVcpCode = 0x12;

    private static readonly TimeSpan MinimumWriteSpacing = TimeSpan.FromMilliseconds(35);
    private static readonly TimeSpan TransientRetryDelay = TimeSpan.FromMilliseconds(45);

    private readonly ConcurrentDictionary<WriteKey, long> _latestWrites = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private DateTime _lastWriteCompletedUtc = DateTime.MinValue;
    private long _writeGeneration;
    private int _disposed;

    public Task<IReadOnlyList<PhysicalMonitorSnapshot>> EnumerateAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        return EnumerateAndDisposeCancellationAsync(linked);
    }

    private static async Task<IReadOnlyList<PhysicalMonitorSnapshot>> EnumerateAndDisposeCancellationAsync(
        CancellationTokenSource linked)
    {
        try
        {
            return await Task.Run(() => EnumerateCore(linked.Token), linked.Token).ConfigureAwait(false);
        }
        finally
        {
            linked.Dispose();
        }
    }

    public Task<MonitorWriteResult> SetValueAsync(
        PhysicalMonitorSnapshot monitor,
        MonitorControlKind control,
        double percentage,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var key = new WriteKey(monitor.Id, control);
        var generation = Interlocked.Increment(ref _writeGeneration);
        _latestWrites[key] = generation;
        var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);

        return Task.Run(
            () => WriteAndDisposeCancellationAsync(linked, monitor, control, percentage, key, generation),
            CancellationToken.None);
    }


    private async Task<MonitorWriteResult> WriteAndDisposeCancellationAsync(
        CancellationTokenSource linked,
        PhysicalMonitorSnapshot monitor,
        MonitorControlKind control,
        double percentage,
        WriteKey key,
        long generation)
    {
        try
        {
            await _writeGate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                if (!IsLatest(key, generation)) return MonitorWriteResult.Stale();

                await WaitForWriteWindowAsync(linked.Token).ConfigureAwait(false);
                var result = WriteCore(monitor, control, percentage, linked.Token);
                _lastWriteCompletedUtc = DateTime.UtcNow;

                if (!result.Succeeded && IsTransientWriteFailure(result.Error) && IsLatest(key, generation))
                {
                    RuntimeLog.Warn(
                        "DISPLAY-DDC",
                        $"Transient {control} write failed for {monitor.Id}; retrying once: {result.Error}");
                    await Task.Delay(TransientRetryDelay, linked.Token).ConfigureAwait(false);
                    result = WriteCore(monitor, control, percentage, linked.Token);
                    _lastWriteCompletedUtc = DateTime.UtcNow;
                }

                if (!IsLatest(key, generation)) return MonitorWriteResult.Stale();
                return result;
            }
            finally
            {
                _writeGate.Release();
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return MonitorWriteResult.Failed("The monitor operation was cancelled.");
        }
        catch (DllNotFoundException ex)
        {
            return MonitorWriteResult.Failed($"Native monitor API unavailable: {ex.Message}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or EntryPointNotFoundException)
        {
            return MonitorWriteResult.Failed($"Native monitor API unavailable: {ex.Message}");
        }
        finally
        {
            linked.Dispose();
        }
    }

    private bool IsLatest(WriteKey key, long generation) =>
        _latestWrites.TryGetValue(key, out var current) && current == generation;

    private async Task WaitForWriteWindowAsync(CancellationToken cancellationToken)
    {
        if (_lastWriteCompletedUtc == DateTime.MinValue) return;

        var remaining = MinimumWriteSpacing - (DateTime.UtcNow - _lastWriteCompletedUtc);
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsTransientWriteFailure(string? error) =>
        !string.IsNullOrWhiteSpace(error) &&
        error.Contains("write failed", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<PhysicalMonitorSnapshot> EnumerateCore(CancellationToken cancellationToken)
    {
        var displays = new List<LogicalDisplay>();
        Exception? enumerationError = null;

        try
        {
            var callback = new MonitorEnumProc((IntPtr hMonitor, IntPtr hdcMonitor, ref NativeRect rect, IntPtr data) =>
            {
                if (cancellationToken.IsCancellationRequested) return false;

                try
                {
                    var info = new MonitorInfoEx { CbSize = Marshal.SizeOf<MonitorInfoEx>() };
                    if (GetMonitorInfo(hMonitor, ref info) && !string.IsNullOrWhiteSpace(info.DeviceName))
                    {
                        displays.Add(new LogicalDisplay(hMonitor, info.DeviceName, rect));
                    }
                }
                catch (Exception ex)
                {
                    enumerationError = ex;
                    return false;
                }

                return true;
            });

            if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero) &&
                enumerationError == null)
            {
                enumerationError = new InvalidOperationException(
                    $"EnumDisplayMonitors failed with Win32 error {Marshal.GetLastWin32Error()}.");
            }
        }
        catch (DllNotFoundException)
        {
            return Array.Empty<PhysicalMonitorSnapshot>();
        }
        catch (EntryPointNotFoundException)
        {
            return Array.Empty<PhysicalMonitorSnapshot>();
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (enumerationError != null) return Array.Empty<PhysicalMonitorSnapshot>();

        var result = new List<PhysicalMonitorSnapshot>();
        var displayOrdinal = 0;
        foreach (var display in displays.OrderBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var lease = TryAcquirePhysicalMonitors(display, cancellationToken);
            if (lease == null) continue;

            for (var index = 0; index < lease.Monitors.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var native = lease.Monitors[index];
                var description = string.IsNullOrWhiteSpace(native.Description)
                    ? "Physical monitor"
                    : native.Description.Trim();
                var id = $"{display.DeviceName}:{index}";
                var displayName = $"Display {displayOrdinal + 1} · {description} ({display.DeviceName})";

                result.Add(new PhysicalMonitorSnapshot(
                    id,
                    displayName,
                    display.DeviceName,
                    description,
                    index,
                    ReadFeature(lease.Handles[index], BrightnessVcpCode),
                    ReadFeature(lease.Handles[index], ContrastVcpCode)));
            }

            displayOrdinal++;
        }

        return result;
    }

    private static MonitorWriteResult WriteCore(
        PhysicalMonitorSnapshot monitor,
        MonitorControlKind control,
        double percentage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var display = FindDisplay(monitor.LogicalDisplayName, cancellationToken);
        if (display == null)
            return MonitorWriteResult.Failed("The logical display is no longer available.");

        using var lease = TryAcquirePhysicalMonitors(display.Value, cancellationToken);
        if (lease == null || monitor.PhysicalIndex < 0 || monitor.PhysicalIndex >= lease.Handles.Length)
            return MonitorWriteResult.Failed("The physical monitor is no longer available.");

        var vcpCode = control == MonitorControlKind.Brightness ? BrightnessVcpCode : ContrastVcpCode;
        var feature = control == MonitorControlKind.Brightness ? monitor.Brightness : monitor.Contrast;
        if (!feature.IsSupported || feature.MaximumNative <= 0)
            return MonitorWriteResult.Failed($"VCP 0x{vcpCode:X2} is unavailable.");

        // The VCP maximum is stable monitor capability data captured during
        // enumeration. Reusing it avoids an extra DDC read before every write,
        // reducing bus traffic and the chance of transient monitor timeouts.
        var nativeValue = MonitorValueNormalizer.ToNative(percentage, (uint)feature.MaximumNative);
        if (!SetVCPFeature(lease.Handles[monitor.PhysicalIndex], vcpCode, nativeValue))
        {
            return MonitorWriteResult.Failed(
                $"VCP 0x{vcpCode:X2} write failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        return MonitorWriteResult.Success();
    }


    private static MonitorFeatureSnapshot ReadFeature(IntPtr handle, byte code)
    {
        try
        {
            if (!GetVcp(handle, code, out _, out var current, out var maximum) || maximum == 0)
                return MonitorFeatureSnapshot.Unsupported($"VCP 0x{code:X2} is unavailable.");

            var currentNative = current > int.MaxValue ? int.MaxValue : (int)current;
            var maximumNative = maximum > int.MaxValue ? int.MaxValue : (int)maximum;
            return new MonitorFeatureSnapshot(
                true,
                MonitorValueNormalizer.ToPercentage(current, maximum),
                currentNative,
                maximumNative);
        }
        catch (DllNotFoundException ex)
        {
            return MonitorFeatureSnapshot.Unsupported($"Native monitor API unavailable: {ex.Message}");
        }
        catch (EntryPointNotFoundException ex)
        {
            return MonitorFeatureSnapshot.Unsupported($"Native monitor API unavailable: {ex.Message}");
        }
    }

    private static LogicalDisplay? FindDisplay(string deviceName, CancellationToken cancellationToken)
    {
        LogicalDisplay? found = null;
        var callback = new MonitorEnumProc((IntPtr hMonitor, IntPtr hdcMonitor, ref NativeRect rect, IntPtr data) =>
        {
            if (cancellationToken.IsCancellationRequested) return false;

            var info = new MonitorInfoEx { CbSize = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(hMonitor, ref info) &&
                string.Equals(info.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                found = new LogicalDisplay(hMonitor, info.DeviceName, rect);
            }

            // Keep enumerating rather than returning FALSE when a match is found.
            // EnumDisplayMonitors reports FALSE when the callback stops enumeration,
            // which the old implementation then incorrectly treated as a lookup failure.
            return true;
        });

        var enumerationCompleted = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        cancellationToken.ThrowIfCancellationRequested();

        if (!enumerationCompleted && found == null) return null;
        return found;
    }

    private static PhysicalMonitorLease? TryAcquirePhysicalMonitors(
        LogicalDisplay display,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!GetNumberOfPhysicalMonitorsFromHMONITOR(display.Handle, out var count) || count == 0)
            return null;

        var monitors = new NativePhysicalMonitor[checked((int)count)];
        if (!GetPhysicalMonitorsFromHMONITOR(display.Handle, count, monitors)) return null;
        return new PhysicalMonitorLease(monitors);
    }

    private static bool GetVcp(
        IntPtr handle,
        byte code,
        out McVcpCodeType type,
        out uint current,
        out uint maximum) =>
        GetVCPFeatureAndVCPFeatureReply(handle, code, out type, out current, out maximum);

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(DdcMonitorService));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _writeGate.Dispose();
        _latestWrites.Clear();
    }

    private readonly record struct WriteKey(string MonitorId, MonitorControlKind Control);
    private readonly record struct LogicalDisplay(IntPtr Handle, string DeviceName, NativeRect Bounds);

    private sealed class PhysicalMonitorLease : IDisposable
    {
        private int _disposed;

        public NativePhysicalMonitor[] Monitors { get; }
        public IntPtr[] Handles { get; }

        public PhysicalMonitorLease(NativePhysicalMonitor[] monitors)
        {
            Monitors = monitors;
            Handles = monitors.Select(monitor => monitor.Handle).ToArray();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                if (Monitors.Length > 0) DestroyPhysicalMonitors((uint)Monitors.Length, Monitors);
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
        }
    }

    private delegate bool MonitorEnumProc(
        IntPtr hMonitor,
        IntPtr hdcMonitor,
        ref NativeRect monitorRect,
        IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int CbSize;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativePhysicalMonitor
    {
        public IntPtr Handle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
    }

    private enum McVcpCodeType : uint
    {
        Momentary = 0,
        SetParameter = 1
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDisplayMonitors(
        IntPtr hdc,
        IntPtr clipRect,
        MonitorEnumProc callback,
        IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetMonitorInfo(
        IntPtr hMonitor,
        ref MonitorInfoEx monitorInfo);

    [DllImport("Dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(
        IntPtr hMonitor,
        out uint numberOfPhysicalMonitors);

    [DllImport("Dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(
        IntPtr hMonitor,
        uint physicalMonitorArraySize,
        [Out] NativePhysicalMonitor[] physicalMonitorArray);

    [DllImport("Dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitors(
        uint physicalMonitorArraySize,
        [In] NativePhysicalMonitor[] physicalMonitorArray);

    [DllImport("Dxva2.dll", SetLastError = true)]
    private static extern bool GetVCPFeatureAndVCPFeatureReply(
        IntPtr physicalMonitorHandle,
        byte vcpCode,
        out McVcpCodeType vcpCodeType,
        out uint currentValue,
        out uint maximumValue);

    [DllImport("Dxva2.dll", SetLastError = true)]
    private static extern bool SetVCPFeature(
        IntPtr physicalMonitorHandle,
        byte vcpCode,
        uint newValue);
}
