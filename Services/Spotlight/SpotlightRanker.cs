using VNotch.Models;

namespace VNotch.Services.Spotlight;

internal static class SpotlightRanker
{
    public static double Score(SpotlightSearchItem item, string query)
    {
        string normalizedQuery = SettingsSearchMatcher.Normalize(query);
        string title = SettingsSearchMatcher.Normalize(item.Title);
        string subtitle = SettingsSearchMatcher.Normalize(item.Subtitle);
        if (normalizedQuery.Length == 0 || title.Length == 0) return 0;

        if (title == normalizedQuery) return 1000;
        if (title.StartsWith(normalizedQuery, StringComparison.Ordinal))
            return 900 - Math.Min(100, title.Length - normalizedQuery.Length);

        string[] words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(word => word == normalizedQuery)) return 850;
        if (words.Any(word => word.StartsWith(normalizedQuery, StringComparison.Ordinal))) return 800;
        if (title.Contains(normalizedQuery, StringComparison.Ordinal)) return 700;
        if (SettingsSearchMatcher.IsNormalizedMatch(title, normalizedQuery)) return 500;
        if (subtitle.Contains(normalizedQuery, StringComparison.Ordinal)) return 350;
        if (SettingsSearchMatcher.IsNormalizedMatch(subtitle, normalizedQuery)) return 250;
        return 0;
    }
}
