namespace QuickLooker.Core;

public sealed record CacheMaintenanceOptions(
    long MaxThumbnailBytes,
    TimeSpan MaxEntryAge,
    TimeSpan MinimumCleanupInterval)
{
    public static CacheMaintenanceOptions Default { get; } = new(
        MaxThumbnailBytes: 512L * 1024 * 1024,
        MaxEntryAge: TimeSpan.FromDays(90),
        MinimumCleanupInterval: TimeSpan.FromDays(1));
}
