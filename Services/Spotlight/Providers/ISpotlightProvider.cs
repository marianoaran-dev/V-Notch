using VNotch.Models;

namespace VNotch.Services.Spotlight.Providers;

internal interface ISpotlightProvider
{
    bool IsAvailable { get; }

    Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken);
}
