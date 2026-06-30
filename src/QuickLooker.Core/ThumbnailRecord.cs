namespace QuickLooker.Core;

public sealed record ThumbnailRecord(
    string SourcePath,
    string ThumbnailPath,
    int Size,
    int Width,
    int Height,
    long SourceLength,
    long SourceLastWriteUtcTicks);
