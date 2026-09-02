namespace VNotch.Services;

public sealed record MonitorLinkValues(
    string Id,
    double Brightness,
    double Contrast,
    bool IsBrightnessSupported,
    bool IsContrastSupported,
    bool IsLinked);

public sealed record MonitorControlUpdate(
    string MonitorId,
    MonitorControlKind Control,
    double Value);

public sealed record MonitorLinkPlan(
    double Delta,
    IReadOnlyList<MonitorControlUpdate> Updates);

/// <summary>
/// Pure percentage-point propagation for local and all-monitor links.  A plan is
/// built from one user event, so applying the plan cannot recursively re-enter it.
/// </summary>
public static class MonitorLinkEngine
{
    private const double Epsilon = 0.0001;

    public static MonitorLinkPlan BuildPlan(
        IReadOnlyList<MonitorLinkValues> monitors,
        string sourceId,
        MonitorControlKind sourceControl,
        double requestedValue,
        bool allMonitorsLinked)
    {
        var source = monitors.FirstOrDefault(monitor =>
            string.Equals(monitor.Id, sourceId, StringComparison.Ordinal));
        if (source == null || !IsSupported(source, sourceControl))
            return new MonitorLinkPlan(0, Array.Empty<MonitorControlUpdate>());

        var participants = new List<(MonitorLinkValues Monitor, MonitorControlKind Control)>
        {
            (source, sourceControl)
        };

        if (source.IsLinked)
        {
            var pairedControl = Pair(sourceControl);
            if (IsSupported(source, pairedControl))
                participants.Add((source, pairedControl));
        }

        if (allMonitorsLinked)
        {
            foreach (var monitor in monitors)
            {
                if (string.Equals(monitor.Id, source.Id, StringComparison.Ordinal)) continue;
                if (!IsSupported(monitor, sourceControl)) continue;

                participants.Add((monitor, sourceControl));
                if (monitor.IsLinked)
                {
                    var pairedControl = Pair(sourceControl);
                    if (IsSupported(monitor, pairedControl))
                        participants.Add((monitor, pairedControl));
                }
            }
        }

        var sourceCurrent = GetValue(source, sourceControl);
        var requestedDelta = ClampPercentage(requestedValue) - sourceCurrent;

        // Linked controls move as one rigid group. Constrain the requested delta
        // to the first 0/100 boundary reached by any participant instead of
        // clamping each value independently, which would progressively destroy
        // the relationship between brightness and contrast (and between monitors).
        var minimumDelta = participants.Max(participant => -GetValue(participant.Monitor, participant.Control));
        var maximumDelta = participants.Min(participant => 100 - GetValue(participant.Monitor, participant.Control));
        var delta = Math.Clamp(requestedDelta, minimumDelta, maximumDelta);

        if (Math.Abs(requestedDelta) <= Epsilon)
            return new MonitorLinkPlan(0, Array.Empty<MonitorControlUpdate>());

        // Even when the group is already against a boundary, return the current
        // participant values. WPF TwoWay slider binding may have applied the
        // user's out-of-range group movement to the source row before this plan
        // runs; replaying the constrained values snaps the UI back immediately.
        var updates = participants
            .Select(participant => new MonitorControlUpdate(
                participant.Monitor.Id,
                participant.Control,
                ClampPercentage(GetValue(participant.Monitor, participant.Control) + delta)))
            .DistinctBy(update => (update.MonitorId, update.Control))
            .ToArray();

        return new MonitorLinkPlan(delta, updates);
    }

    public static double ClampPercentage(double value)
    {
        if (double.IsNaN(value)) return 0;
        if (double.IsNegativeInfinity(value)) return 0;
        if (double.IsPositiveInfinity(value)) return 100;
        return Math.Clamp(value, 0, 100);
    }

    public static MonitorControlKind Pair(MonitorControlKind control) =>
        control == MonitorControlKind.Brightness
            ? MonitorControlKind.Contrast
            : MonitorControlKind.Brightness;

    private static bool IsSupported(MonitorLinkValues monitor, MonitorControlKind control) =>
        control == MonitorControlKind.Brightness
            ? monitor.IsBrightnessSupported
            : monitor.IsContrastSupported;

    private static double GetValue(MonitorLinkValues monitor, MonitorControlKind control) =>
        control == MonitorControlKind.Brightness ? monitor.Brightness : monitor.Contrast;
}
