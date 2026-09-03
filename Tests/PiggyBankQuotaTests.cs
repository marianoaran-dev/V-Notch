using System.Text.Json;
using System.Windows.Media;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class PiggyBankQuotaTests
{
    [Fact]
    public void Parse_MapsFiveHourWeeklyAndBankedResetFromCodexRateLimits()
    {
        using var document = JsonDocument.Parse("""
        {
          "id": 1,
          "result": {
            "rateLimits": {
              "primary": {
                "usedPercent": 0,
                "windowDurationMins": 300,
                "resetsAt": 1788429279
              },
              "secondary": {
                "usedPercent": 40,
                "windowDurationMins": 10080,
                "resetsAt": 1788754416
              }
            },
            "rateLimitResetCredits": {
              "availableCount": 1,
              "credits": [
                {
                  "id": "reset-1",
                  "resetType": "codexRateLimits",
                  "status": "available",
                  "grantedAt": 1787352432,
                  "expiresAt": 1789944432,
                  "title": "Full reset (Weekly + 5 hr)"
                }
              ]
            }
          }
        }
        """);

        var snapshot = PiggyBankRateLimitParser.Parse(document.RootElement, DateTimeOffset.UnixEpoch);

        Assert.NotNull(snapshot.FiveHour);
        Assert.Equal(100, snapshot.FiveHour!.RemainingPercent);
        Assert.Equal(300, snapshot.FiveHour.WindowDurationMinutes);
        Assert.NotNull(snapshot.Weekly);
        Assert.Equal(60, snapshot.Weekly!.RemainingPercent);
        Assert.Equal(10080, snapshot.Weekly.WindowDurationMinutes);
        Assert.Equal(1, snapshot.BankedResetCount);
        Assert.Single(snapshot.BankedResets);
        Assert.Equal("reset-1", snapshot.BankedResets[0].StableId);
        Assert.Equal(0, snapshot.MissingResetDetailCount);
    }

    [Fact]
    public void Parse_ClampsQuotaRangeAndKeepsReportedMissingResetDetails()
    {
        using var document = JsonDocument.Parse("""
        {
          "result": {
            "rateLimits": {
              "primary": { "usedPercent": -20, "windowDurationMins": 300 },
              "secondary": { "usedPercent": 140, "windowDurationMins": 10080 }
            },
            "rateLimitResetCredits": {
              "availableCount": 2,
              "credits": [
                { "id": "wrong-type", "resetType": "somethingElse", "status": "available" },
                { "id": "used", "resetType": "codexRateLimits", "status": "used" }
              ]
            }
          }
        }
        """);

        var snapshot = PiggyBankRateLimitParser.Parse(document.RootElement, DateTimeOffset.UnixEpoch);

        Assert.Equal(100, snapshot.FiveHour!.RemainingPercent);
        Assert.Equal(0, snapshot.Weekly!.RemainingPercent);
        Assert.Empty(snapshot.BankedResets);
        Assert.Equal(2, snapshot.BankedResetCount);
        Assert.Equal(2, snapshot.MissingResetDetailCount);
    }

    [Fact]
    public void QuotaColour_UsesApprovedHealthyToCriticalSemanticRange()
    {
        Assert.Equal(Color.FromRgb(48, 209, 88), PiggyBankFormatting.QuotaColour(100));
        Assert.Equal(Color.FromRgb(255, 69, 58), PiggyBankFormatting.QuotaColour(0));

        var amber = PiggyBankFormatting.QuotaColour(40);
        var orange = PiggyBankFormatting.QuotaColour(15);

        Assert.True(amber.R > amber.G);
        Assert.True(orange.R > orange.G);
        Assert.NotEqual(amber, orange);
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(0, 0)]
    [InlineData(50.4, 50)]
    [InlineData(50.5, 51)]
    [InlineData(100, 100)]
    [InlineData(125, 100)]
    public void ClampRemaining_CoversFullPreviewRange(double input, int expected)
    {
        Assert.Equal(expected, PiggyBankFormatting.ClampRemaining(input));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(0.1, 1)]
    [InlineData(1.0, 1)]
    [InlineData(1.01, 2)]
    [InlineData(4.0, 4)]
    [InlineData(6.99, 7)]
    [InlineData(8.0, 7)]
    public void WeeklyRemainingDays_MapsWindowTimeToSevenBlockIndicator(double daysUntilReset, int expected)
    {
        var now = DateTimeOffset.UnixEpoch;
        var resetAt = now.AddDays(daysUntilReset);

        Assert.Equal(expected, PiggyBankFormatting.WeeklyRemainingDays(resetAt, now));
    }

    [Fact]
    public void ResetExpiryParts_FormatBankedResetAsThreeCompactLines()
    {
        var local = new DateTimeOffset(2026, 9, 21, 8, 47, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 21, 8, 47, 0)));

        Assert.Equal("Mon 21 Sept", PiggyBankFormatting.ResetExpiryDate(local));
        Assert.Equal("8:47 am", PiggyBankFormatting.ResetExpiryTime(local));
        Assert.Equal("Unavailable", PiggyBankFormatting.ResetExpiryDate(null));
        Assert.Equal(string.Empty, PiggyBankFormatting.ResetExpiryTime(null));
    }
}
