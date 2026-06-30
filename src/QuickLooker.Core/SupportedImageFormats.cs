namespace QuickLooker.Core;

public static class SupportedImageFormats
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".bmp",
        ".gif",
        ".tif",
        ".tiff",
        ".webp",
        ".psd",
        ".psb",
        ".tga",
        ".ico",
        ".heic",
        ".heif",
        ".avif"
    };

    public static IReadOnlyCollection<string> AllExtensions => Extensions;

    public static bool IsSupported(string path)
    {
        return Extensions.Contains(Path.GetExtension(path));
    }

    public static IEnumerable<string> EnumerateSupportedFiles(string folder)
    {
        return Directory.EnumerateFiles(folder)
            .Where(IsSupported)
            .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase);
    }

    public static string FileDialogFilter =>
        "图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.psd;*.psb;*.tga;*.ico;*.heic;*.heif;*.avif|所有文件|*.*";
}
