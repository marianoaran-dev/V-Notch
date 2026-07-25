using VNotch.Models;
using VNotch.Services.Spotlight;
using Xunit;

namespace VNotch.Tests;

public sealed class SpotlightRankerTests
{
    [Fact]
    public void Score_IsCaseAndAccentInsensitive()
    {
        var item = Item("Ứng Dụng");

        Assert.Equal(1000, SpotlightRanker.Score(item, "ung dung"));
    }

    [Fact]
    public void Score_PrioritizesExactPrefixAndFuzzyMatches()
    {
        var exact = SpotlightRanker.Score(Item("Calculator"), "calculator");
        var prefix = SpotlightRanker.Score(Item("Calculator Plus"), "calc");
        var fuzzy = SpotlightRanker.Score(Item("Calculator"), "calcualtor");

        Assert.True(exact > prefix);
        Assert.True(prefix > fuzzy);
        Assert.True(fuzzy > 0);
    }

    [Fact]
    public void Score_CanMatchTheSubtitle()
    {
        var item = Item("Report", @"C:\Users\me\Documents");

        Assert.True(SpotlightRanker.Score(item, "documents") > 0);
    }

    private static SpotlightSearchItem Item(string title, string subtitle = "Application") =>
        new("id", SpotlightResultKind.Application, title, subtitle, "target");
}