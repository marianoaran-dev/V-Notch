using Windows.Storage;
using Windows.Storage.Search;
using VNotch.Models;

namespace VNotch.Services.Spotlight.Providers;

internal sealed class WindowsSearchProvider : ISpotlightProvider
{
    private const int QueryTimeoutMilliseconds = 1500;

    public bool IsAvailable { get; private set; } = true;

    public async Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        string sanitizedQuery = SanitizeQuery(query);
        if (sanitizedQuery.Length == 0 || limit <= 0) return Array.Empty<SpotlightSearchItem>();
        limit = Math.Min(limit, 50);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(QueryTimeoutMilliseconds);
        try
        {
            string rootPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var root = await StorageFolder.GetFolderFromPathAsync(rootPath).AsTask(timeoutCts.Token);

            var options = new QueryOptions(CommonFileQuery.DefaultQuery, Array.Empty<string>())
            {
                FolderDepth = FolderDepth.Deep,
                IndexerOption = IndexerOption.OnlyUseIndexer,
                UserSearchFilter = sanitizedQuery
            };

            var result = root.CreateItemQueryWithOptions(options);
            var items = await result.GetItemsAsync(0, (uint)Math.Max(limit * 3, limit))
                .AsTask(timeoutCts.Token);
            IsAvailable = true;

            return items
                .Select(ToSearchItem)
                .Select(item => item with { Score = SpotlightRanker.Score(item, query) })
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
                .Take(limit)
                .ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            IsAvailable = false;
            return Array.Empty<SpotlightSearchItem>();
        }
        catch
        {
            IsAvailable = false;
            throw;
        }
    }

    internal static string SanitizeQuery(string query) =>
        new string(query.Where(character => character is not ('"' or '\'' or '\\' or '(' or ')' or ':' or '*'))
            .Take(256).ToArray()).Trim();

    private static SpotlightSearchItem ToSearchItem(IStorageItem item)
    {
        bool isFolder = item is StorageFolder;
        return new SpotlightSearchItem(
            $"{(isFolder ? "folder" : "file")}:{item.Path}",
            isFolder ? SpotlightResultKind.Folder : SpotlightResultKind.File,
            item.Name,
            item.Path,
            item.Path,
            item.Path);
    }
}