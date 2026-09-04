namespace VNotch.Models;

public sealed record PiggyBankSnapshot(
    DateTimeOffset FetchedAt,
    PiggyQuotaWindow? FiveHour,
    PiggyQuotaWindow? Weekly,
    IReadOnlyList<PiggyBankedReset> BankedResets,
    int BankedResetCount,
    int MissingResetDetailCount)
{
    // Codex can transiently return rateLimitResetCredits as null even when reset
    // credits were present on an earlier read. Callers use this to distinguish an
    // authoritative empty list from a temporarily unavailable reset-credit field.
    public bool BankedResetDataAvailable { get; init; }
}

public sealed record PiggyQuotaWindow(
    int UsedPercent,
    int RemainingPercent,
    int? WindowDurationMinutes,
    DateTimeOffset? ResetsAt,
    string Source);

public sealed record PiggyBankedReset(
    string StableId,
    string Title,
    DateTimeOffset? GrantedAt,
    DateTimeOffset? ExpiresAt,
    string ResetType,
    string Status);
