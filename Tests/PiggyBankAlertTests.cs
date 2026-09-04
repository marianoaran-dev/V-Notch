using System.IO;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class PiggyBankAlertTests
{
    [Fact]
    public void Evaluate_AlertsOncePerThresholdWithinSameQuotaCycle()
    {
        var now = new DateTimeOffset(2026, 9, 4, 9, 0, 0, TimeSpan.Zero);
        var settings = NewSettings();

        var first = PiggyBankAlertEngine.Evaluate(
            Snapshot(now, weeklyRemaining: 49, weeklyReset: now.AddDays(3)),
            settings,
            now);

        Assert.Single(first.Alerts);
        Assert.Contains("49% remaining", first.Alerts[0].Message);
        Assert.Contains(50, settings.PiggyWeeklyAlertedThresholds);

        var duplicate = PiggyBankAlertEngine.Evaluate(
            Snapshot(now.AddMinutes(5), weeklyRemaining: 48, weeklyReset: now.AddDays(3)),
            settings,
            now.AddMinutes(5));

        Assert.Empty(duplicate.Alerts);

        var nextThreshold = PiggyBankAlertEngine.Evaluate(
            Snapshot(now.AddMinutes(10), weeklyRemaining: 24, weeklyReset: now.AddDays(3)),
            settings,
            now.AddMinutes(10));

        Assert.Single(nextThreshold.Alerts);
        Assert.Contains("alert at 25%", nextThreshold.Alerts[0].Message);
    }

    [Fact]
    public void Evaluate_NewQuotaCycleCanAlertAgain()
    {
        var now = new DateTimeOffset(2026, 9, 4, 9, 0, 0, TimeSpan.Zero);
        var settings = NewSettings();

        _ = PiggyBankAlertEngine.Evaluate(
            Snapshot(now, weeklyRemaining: 49, weeklyReset: now.AddDays(1)),
            settings,
            now);

        var nextCycle = PiggyBankAlertEngine.Evaluate(
            Snapshot(now.AddDays(2), weeklyRemaining: 49, weeklyReset: now.AddDays(8)),
            settings,
            now.AddDays(2));

        Assert.Single(nextCycle.Alerts);
        Assert.Contains("alert at 50%", nextCycle.Alerts[0].Message);
    }

    [Fact]
    public void Evaluate_CustomThresholdUsesMostRelevantCurrentAlert()
    {
        var now = new DateTimeOffset(2026, 9, 4, 9, 0, 0, TimeSpan.Zero);
        var settings = NewSettings();
        settings.PiggyCustomAlertEnabled = true;
        settings.PiggyCustomAlertPercent = 60;

        var result = PiggyBankAlertEngine.Evaluate(
            Snapshot(now, weeklyRemaining: 59, weeklyReset: now.AddDays(3)),
            settings,
            now);

        Assert.Single(result.Alerts);
        Assert.Contains("alert at 60%", result.Alerts[0].Message);
    }

    [Fact]
    public void Evaluate_BankedResetExpiryAlertIsDurablyDeduplicated()
    {
        var now = new DateTimeOffset(2026, 9, 4, 9, 0, 0, TimeSpan.Zero);
        var settings = NewSettings();
        settings.PiggyUsageAlertsEnabled = false;
        settings.PiggyBankedResetExpiryAlerts = true;
        settings.PiggyBankedResetReminderHours = 48;

        var reset = new PiggyBankedReset(
            "banked-1",
            "Reset",
            now.AddDays(-1),
            now.AddHours(24),
            "codexRateLimits",
            "available");
        var snapshot = new PiggyBankSnapshot(now, null, null, [reset], 1, 0);

        var first = PiggyBankAlertEngine.Evaluate(snapshot, settings, now);
        Assert.Single(first.Alerts);
        Assert.Single(settings.PiggyBankedResetAlertedKeys);

        var second = PiggyBankAlertEngine.Evaluate(snapshot with { FetchedAt = now.AddMinutes(5) }, settings, now.AddMinutes(5));
        Assert.Empty(second.Alerts);
    }

    [Fact]
    public void Evaluate_MasterDisableSuppressesAllPiggyAlerts()
    {
        var now = new DateTimeOffset(2026, 9, 4, 9, 0, 0, TimeSpan.Zero);
        var settings = NewSettings();
        settings.EnablePiggyNotifications = false;

        var result = PiggyBankAlertEngine.Evaluate(
            Snapshot(now, weeklyRemaining: 9, weeklyReset: now.AddDays(3)),
            settings,
            now);

        Assert.Empty(result.Alerts);
        Assert.Empty(settings.PiggyWeeklyAlertedThresholds);
    }

    [Fact]
    public void Evaluate_WeeklyAlertsDoNotFireForFiveHourQuota()
    {
        var now = new DateTimeOffset(2026, 9, 4, 9, 0, 0, TimeSpan.Zero);
        var settings = NewSettings();
        var snapshot = new PiggyBankSnapshot(
            now,
            new PiggyQuotaWindow(51, 49, 300, now.AddHours(2), "primary"),
            new PiggyQuotaWindow(0, 100, 10080, now.AddDays(3), "secondary"),
            [],
            0,
            0);

        var result = PiggyBankAlertEngine.Evaluate(snapshot, settings, now);

        Assert.Empty(result.Alerts);
        Assert.Empty(settings.PiggyFiveHourAlertedThresholds);
    }

    [Fact]
    public void SettingsService_RoundTripsPiggyAlertPreferencesAndDeduplicationState()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"vnotch-piggy-alerts-{Guid.NewGuid():N}");
        string path = Path.Combine(folder, "settings.json");
        Directory.CreateDirectory(folder);

        try
        {
            var service = new SettingsService(path, _ => { });
            var settings = NewSettings();
            settings.PiggyCustomAlertEnabled = true;
            settings.PiggyCustomAlertPercent = 63;
            settings.PiggyBankedResetExpiryAlerts = true;
            settings.PiggyBankedResetReminderHours = 24;
            settings.PiggyWeeklyAlertCycleKey = "cycle-a";
            settings.PiggyWeeklyAlertedThresholds.Add(50);
            settings.PiggyBankedResetAlertedKeys.Add("banked-a");

            service.Save(settings);
            var loaded = service.Load();

            Assert.True(loaded.PiggyCustomAlertEnabled);
            Assert.Equal(63, loaded.PiggyCustomAlertPercent);
            Assert.True(loaded.PiggyBankedResetExpiryAlerts);
            Assert.Equal(24, loaded.PiggyBankedResetReminderHours);
            Assert.Equal("cycle-a", loaded.PiggyWeeklyAlertCycleKey);
            Assert.Contains(50, loaded.PiggyWeeklyAlertedThresholds);
            Assert.Contains("banked-a", loaded.PiggyBankedResetAlertedKeys);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }

    private static NotchSettings NewSettings() => new()
    {
        EnablePiggyNotifications = true,
        PiggyUsageAlertsEnabled = true,
        PiggyAlertAt50 = true,
        PiggyAlertAt25 = true,
        PiggyAlertAt10 = true,
        PiggyCustomAlertEnabled = false,
        PiggyBankedResetExpiryAlerts = false
    };

    private static PiggyBankSnapshot Snapshot(
        DateTimeOffset fetchedAt,
        int weeklyRemaining,
        DateTimeOffset weeklyReset)
        => new(
            fetchedAt,
            null,
            new PiggyQuotaWindow(100 - weeklyRemaining, weeklyRemaining, 10080, weeklyReset, "test"),
            [],
            0,
            0);
}
