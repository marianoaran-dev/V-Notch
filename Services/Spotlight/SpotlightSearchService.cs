using System.IO;
using VNotch.Models;
using VNotch.Services.Spotlight.Providers;

namespace VNotch.Services.Spotlight;

internal sealed class SpotlightSearchService
{
    private const int MaxQueryLength = 256;
    private const int MaxResults = 50;
    private readonly IReadOnlyList<ISpotlightProvider> _providers;

    public bool IsWindowsSearchAvailable =>
        _providers.OfType<WindowsSearchProvider>().FirstOrDefault()?.IsAvailable ?? false;

    public SpotlightSearchService(IEnumerable<ISpotlightProvider> providers)
    {
        _providers = providers.ToArray();
    }

    internal Task WarmupAsync() =>
        Task.WhenAll(_providers.OfType<AppSearchProvider>().Select(provider => provider.WarmupAsync()));

    internal async Task<IReadOnlyList<SpotlightSearchItem>> SearchApplicationsAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeInput(query, limit, out query, out limit))
            return Array.Empty<SpotlightSearchItem>();

        var results = await Task.WhenAll(_providers.OfType<AppSearchProvider>().Select(provider =>
            SearchProviderAsync(provider, query, limit, cancellationToken))).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return Merge(results, limit);
    }

    internal async Task<IReadOnlyList<SpotlightSearchItem>> SearchNonApplicationsAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeInput(query, limit, out query, out limit))
            return Array.Empty<SpotlightSearchItem>();

        var results = await Task.WhenAll(_providers.Where(provider => provider is not AppSearchProvider).Select(provider =>
            SearchProviderAsync(provider, query, limit, cancellationToken))).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return Merge(results, limit);
    }

    public async Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeInput(query, limit, out query, out limit))
            return Array.Empty<SpotlightSearchItem>();

        var providerTasks = _providers.Select(provider =>
            SearchProviderAsync(provider, query, limit, cancellationToken));
        var providerResults = await Task.WhenAll(providerTasks).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return Merge(providerResults, limit);
    }

    internal static IReadOnlyList<SpotlightSearchItem> Merge(
        IEnumerable<IReadOnlyList<SpotlightSearchItem>> providerResults,
        int limit)
    {
        var results = providerResults
            .SelectMany(result => result)
            .GroupBy(item => item.Target, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.Score).First())
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();

        return results.Select(LoadIcon).ToArray();
    }

    private static bool TryNormalizeInput(
        string query,
        int limit,
        out string normalizedQuery,
        out int normalizedLimit)
    {
        normalizedQuery = query?.Trim() ?? string.Empty;
        if (normalizedQuery.Length > MaxQueryLength) normalizedQuery = normalizedQuery[..MaxQueryLength];
        normalizedLimit = Math.Clamp(limit, 0, MaxResults);
        return normalizedQuery.Length > 0 && normalizedLimit > 0;
    }

    private static async Task<IReadOnlyList<SpotlightSearchItem>> SearchProviderAsync(
        ISpotlightProvider provider,
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        try
        {
            return await provider.SearchAsync(query, limit, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("SPOTLIGHT-SEARCH", ex, $"{provider.GetType().Name} failed");
            return Array.Empty<SpotlightSearchItem>();
        }
    }

    private static SpotlightSearchItem LoadIcon(SpotlightSearchItem item)
    {
        string? path = item.IconPath;
        if (item.Icon != null) return item;
        if (string.IsNullOrEmpty(path) || (!File.Exists(path) && !Directory.Exists(path))) return item;
        return item with { Icon = FileIconProvider.GetFileIcon(path) };
    }
}