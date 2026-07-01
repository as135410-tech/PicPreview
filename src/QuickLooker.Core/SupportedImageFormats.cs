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

    private static readonly Dictionary<string, string> FormatBadges = new(StringComparer.OrdinalIgnoreCase)
    {
        [".psd"] = "PSD",
        [".psb"] = "PSB"
    };

    public static IReadOnlyCollection<string> AllExtensions => Extensions;

    public static bool IsSupported(string path)
    {
        return Extensions.Contains(Path.GetExtension(path));
    }

    public static string? GetFormatBadge(string path)
    {
        if (FormatBadges.TryGetValue(Path.GetExtension(path), out var badge))
        {
            return badge;
        }

        return TryGetPhotoshopFormatBadgeFromHeader(path);
    }

    public static IEnumerable<string> EnumerateSupportedFiles(string folder)
    {
        return Directory.EnumerateFiles(folder)
            .Where(IsSupported)
            .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase);
    }

    public static string FileDialogFilter =>
        "图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.psd;*.psb;*.tga;*.ico;*.heic;*.heif;*.avif|所有文件|*.*";

    private static string? TryGetPhotoshopFormatBadgeFromHeader(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[6];

            using var stream = File.OpenRead(path);

            if (stream.Read(header) != header.Length)
            {
                return null;
            }

            if (header[0] != (byte)'8' ||
                header[1] != (byte)'B' ||
                header[2] != (byte)'P' ||
                header[3] != (byte)'S')
            {
                return null;
            }

            var version = (header[4] << 8) | header[5];

            return version switch
            {
                1 => "PSD",
                2 => "PSB",
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }
}
