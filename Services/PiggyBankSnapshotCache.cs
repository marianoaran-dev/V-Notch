using VNotch.Models;

namespace VNotch.Services;

internal sealed record PiggyBankSnapshotCacheResult(
    PiggyBankSnapshot Snapshot,
    bool StateChanged);

internal static class PiggyBankSnapshotCache
{
    public static PiggyBankSnapshotCacheResult Resolve(
        PiggyBankSnapshot snapshot,
        NotchSettings settings,
        DateTimeOffset now)
    {
        settings.PiggyCachedBankedResets ??= new List<PiggyBankedReset>();

        if (snapshot.BankedResetDataAvailable)
        {
            var active = snapshot.BankedResets
                .Where(reset => reset.ExpiresAt is null || reset.ExpiresAt > now)
                .ToList();
            int count = Math.Max(snapshot.BankedResetCount, active.Count);

            bool changed = settings.PiggyCachedBankedResetCount != count
                           || !ResetListsMatch(settings.PiggyCachedBankedResets, active);
            if (changed)
            {
                settings.PiggyCachedBankedResets = active;
                settings.PiggyCachedBankedResetCount = count;
            }

            return new PiggyBankSnapshotCacheResult(snapshot, changed);
        }

        // Null/missing reset-credit data is not an authoritative "zero". Keep only
        // cached entries that have not expired, then surface those until Codex sends
        // the reset-credit object again.
        var cached = settings.PiggyCachedBankedResets
            .Where(reset => reset.ExpiresAt is null || reset.ExpiresAt > now)
            .ToList();

        bool cachePruned = !ResetListsMatch(settings.PiggyCachedBankedResets, cached);
        if (cachePruned)
        {
            settings.PiggyCachedBankedResets = cached;
            settings.PiggyCachedBankedResetCount = Math.Min(
                Math.Max(settings.PiggyCachedBankedResetCount, cached.Count),
                cached.Count);
        }

        int cachedCount = Math.Max(settings.PiggyCachedBankedResetCount, cached.Count);
        if (cachedCount <= 0)
            return new PiggyBankSnapshotCacheResult(snapshot, cachePruned);

        var resolved = snapshot with
        {
            BankedResets = cached,
            BankedResetCount = cachedCount,
            MissingResetDetailCount = Math.Max(cachedCount - cached.Count, 0)
        };
        return new PiggyBankSnapshotCacheResult(resolved, cachePruned);
    }

    private static bool ResetListsMatch(
        IReadOnlyList<PiggyBankedReset> left,
        IReadOnlyList<PiggyBankedReset> right)
    {
        if (left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++)
        {
            if (!Equals(left[i], right[i])) return false;
        }
        return true;
    }
}
