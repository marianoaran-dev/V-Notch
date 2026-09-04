using VNotch.Models;

namespace VNotch.Services;

internal sealed record PiggyBankAlert(string Title, string Message);

internal sealed record PiggyBankAlertEvaluation(
    IReadOnlyList<PiggyBankAlert> Alerts,
    bool StateChanged);

internal static class PiggyBankAlertEngine
{
    public static PiggyBankAlertEvaluation Evaluate(
        PiggyBankSnapshot snapshot,
        NotchSettings settings,
        DateTimeOffset now)
    {
        var alerts = new List<PiggyBankAlert>();
        bool changed = false;

        if (!settings.EnablePiggyNotifications)
            return new PiggyBankAlertEvaluation(alerts, false);

        if (settings.PiggyUsageAlertsEnabled)
        {
            var thresholds = BuildThresholds(settings);

            EvaluateQuota(
                "Weekly quota",
                snapshot.Weekly,
                thresholds,
                settings.PiggyWeeklyAlertCycleKey,
                settings.PiggyWeeklyAlertedThresholds,
                now,
                alerts,
                ref changed,
                out var weeklyCycleKey);
            settings.PiggyWeeklyAlertCycleKey = weeklyCycleKey;
        }

        if (settings.PiggyBankedResetExpiryAlerts)
        {
            foreach (var reset in snapshot.BankedResets)
            {
                if (reset.ExpiresAt is not { } expiresAt)
                    continue;

                var remaining = expiresAt - now;
                if (remaining <= TimeSpan.Zero || remaining > TimeSpan.FromHours(settings.PiggyBankedResetReminderHours))
                    continue;

                string key = $"{reset.StableId}|{expiresAt.ToUniversalTime():O}|{settings.PiggyBankedResetReminderHours}";
                if (settings.PiggyBankedResetAlertedKeys.Contains(key, StringComparer.Ordinal))
                    continue;

                settings.PiggyBankedResetAlertedKeys.Add(key);
                changed = true;

                string when = remaining.TotalHours >= 2
                    ? $"in {Math.Ceiling(remaining.TotalHours):0} hours"
                    : remaining.TotalMinutes >= 2
                        ? $"in {Math.Ceiling(remaining.TotalMinutes):0} minutes"
                        : "very soon";

                alerts.Add(new PiggyBankAlert(
                    "Piggy Bank · Banked reset",
                    $"A banked reset expires {when} ({expiresAt.ToLocalTime():ddd d MMM, h:mm tt})."));
            }
        }

        // Prevent stale reset IDs from growing the durable settings forever while
        // retaining enough history to avoid duplicate alerts across app restarts.
        if (settings.PiggyBankedResetAlertedKeys.Count > 96)
        {
            settings.PiggyBankedResetAlertedKeys = settings.PiggyBankedResetAlertedKeys.TakeLast(64).ToList();
            changed = true;
        }

        return new PiggyBankAlertEvaluation(alerts, changed);
    }

    private static List<int> BuildThresholds(NotchSettings settings)
    {
        var thresholds = new List<int>(4);
        if (settings.PiggyAlertAt50) thresholds.Add(50);
        if (settings.PiggyAlertAt25) thresholds.Add(25);
        if (settings.PiggyAlertAt10) thresholds.Add(10);
        if (settings.PiggyCustomAlertEnabled)
            thresholds.Add(Math.Clamp(settings.PiggyCustomAlertPercent, 1, 99));

        return thresholds.Distinct().OrderByDescending(value => value).ToList();
    }

    private static void EvaluateQuota(
        string label,
        PiggyQuotaWindow? quota,
        IReadOnlyList<int> thresholds,
        string cycleKey,
        List<int> firedThresholds,
        DateTimeOffset now,
        List<PiggyBankAlert> alerts,
        ref bool changed,
        out string updatedCycleKey)
    {
        updatedCycleKey = cycleKey;
        if (quota is null || thresholds.Count == 0)
            return;

        string currentCycleKey = quota.ResetsAt is { } resetsAt
            ? resetsAt.ToUniversalTime().ToString("O")
            : $"{quota.Source}|{quota.WindowDurationMinutes?.ToString() ?? "unknown"}";

        if (!string.Equals(cycleKey, currentCycleKey, StringComparison.Ordinal))
        {
            updatedCycleKey = currentCycleKey;
            firedThresholds.Clear();
            changed = true;
        }

        var newlyReached = thresholds
            .Where(threshold => quota.RemainingPercent <= threshold && !firedThresholds.Contains(threshold))
            .ToList();

        if (newlyReached.Count == 0)
            return;

        // If the app was closed while several thresholds were crossed, surface
        // only the most relevant current alert instead of showing a burst of old
        // notifications, while still marking every crossed level as handled.
        int alertThreshold = newlyReached.Min();
        foreach (int threshold in newlyReached)
            firedThresholds.Add(threshold);
        changed = true;

        string resetText = quota.ResetsAt is { } nextReset
            ? $" Resets {FormatReset(nextReset, now)}."
            : string.Empty;

        alerts.Add(new PiggyBankAlert(
            $"Piggy Bank · {label}",
            $"{quota.RemainingPercent}% remaining (alert at {alertThreshold}%).{resetText}"));
    }

    private static string FormatReset(DateTimeOffset resetAt, DateTimeOffset now)
    {
        var remaining = resetAt - now;
        if (remaining <= TimeSpan.Zero)
            return "soon";

        if (remaining.TotalDays >= 1)
            return resetAt.ToLocalTime().ToString("ddd d MMM, h:mm tt");

        if (remaining.TotalHours >= 1)
            return $"in {(int)remaining.TotalHours}h {remaining.Minutes}m";

        return $"in {Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))}m";
    }
}
