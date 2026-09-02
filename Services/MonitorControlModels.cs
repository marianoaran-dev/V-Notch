using System.Collections.ObjectModel;

namespace VNotch.Services;

public enum MonitorControlKind
{
    Brightness,
    Contrast
}

public sealed record MonitorFeatureSnapshot(
    bool IsSupported,
    double CurrentPercent,
    int CurrentNative,
    int MaximumNative,
    string? Error = null)
{
    public static MonitorFeatureSnapshot Unsupported(string? error = null) =>
        new(false, 0, 0, 0, error);
}

public sealed record PhysicalMonitorSnapshot(
    string Id,
    string DisplayName,
    string LogicalDisplayName,
    string Description,
    int PhysicalIndex,
    MonitorFeatureSnapshot Brightness,
    MonitorFeatureSnapshot Contrast);

public sealed record MonitorWriteResult(bool Succeeded, bool WasStale, string? Error = null)
{
    public static MonitorWriteResult Success() => new(true, false);
    public static MonitorWriteResult Stale() => new(false, true);
    public static MonitorWriteResult Failed(string error) => new(false, false, error);
}

public interface IMonitorControlService : IDisposable
{
    Task<IReadOnlyList<PhysicalMonitorSnapshot>> EnumerateAsync(CancellationToken cancellationToken = default);

    Task<MonitorWriteResult> SetValueAsync(
        PhysicalMonitorSnapshot monitor,
        MonitorControlKind control,
        double percentage,
        CancellationToken cancellationToken = default);
}

public static class MonitorValueNormalizer
{
    public static double ToPercentage(uint current, uint maximum)
    {
        if (maximum == 0) return 0;
        return MonitorLinkEngine.ClampPercentage(current * 100d / maximum);
    }

    public static uint ToNative(double percentage, uint maximum)
    {
        if (maximum == 0) return 0;
        var clamped = MonitorLinkEngine.ClampPercentage(percentage);
        return (uint)Math.Round(clamped / 100d * maximum, MidpointRounding.AwayFromZero);
    }
}

public readonly record struct MonitorWriteRequest(
    string MonitorId,
    MonitorControlKind Control,
    double Percentage);
