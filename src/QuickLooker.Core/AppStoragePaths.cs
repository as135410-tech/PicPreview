namespace QuickLooker.Core;

public static class AppStoragePaths
{
    public static string CacheRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PicPreview");

    public static string ThumbnailDirectory => Path.Combine(CacheRoot, "thumbs");

    public static string CacheDatabasePath => Path.Combine(CacheRoot, "picpreview-cache.db");
}
