namespace QuickLooker.Core;

public sealed record FileFingerprint(string FullPath, long Length, long LastWriteUtcTicks)
{
    public static FileFingerprint FromPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var fileInfo = new FileInfo(fullPath);

        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("图片文件不存在。", fullPath);
        }

        return new FileFingerprint(fullPath, fileInfo.Length, fileInfo.LastWriteTimeUtc.Ticks);
    }
}
