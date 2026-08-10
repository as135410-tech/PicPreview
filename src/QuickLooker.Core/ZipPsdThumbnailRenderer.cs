using System.IO.Compression;
using ImageMagick;
using ImageMagick.Drawing;

namespace QuickLooker.Core;

public static class ZipPsdThumbnailRenderer
{
    public const int MaxArchiveEntries = 4096;
    public const long MaxCompressedEntryBytes = 256L * 1024 * 1024;
    public const long MaxUncompressedEntryBytes = 512L * 1024 * 1024;
    public const int MaxPreviewImages = 3;

    private static readonly HashSet<string> PreviewExtensions = new(StringComparer.OrdinalIgnoreCase)
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

    private static readonly HashSet<string> PhotoshopExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".psd",
        ".psb"
    };

    private static readonly string[] PreferredNameParts =
    {
        "cover",
        "preview",
        "thumbnail",
        "thumb",
        "封面",
        "预览"
    };

    public static bool IsZipFile(string inputPath)
    {
        try
        {
            Span<byte> header = stackalloc byte[4];

            using var stream = new FileStream(
                inputPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: header.Length,
                FileOptions.SequentialScan);

            return stream.Read(header) == header.Length && IsZipSignature(header);
        }
        catch
        {
            return false;
        }
    }

    public static async Task<RenderedImage> RenderThumbnailAsync(
        string inputPath,
        string outputPath,
        int maxPixel,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var archiveStream = new FileStream(
            inputPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);

        if (archive.Entries.Count > MaxArchiveEntries)
        {
            throw new InvalidDataException($"ZIP 中的文件数量超过限制（{MaxArchiveEntries}）。");
        }

        var imageEntries = archive.Entries
            .Where(IsSafePreviewEntry)
            .ToArray();

        if (imageEntries.Length == 0)
        {
            throw new InvalidDataException("ZIP 中没有可用于缩略图的图片文件。");
        }

        var distinctImageCount = imageEntries
            .Select(GetLogicalImageKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var candidates = imageEntries
            .OrderByDescending(GetNamePriority)
            .ThenBy(GetFormatPriority)
            .ThenBy(GetDirectoryDepth)
            .ThenBy(candidate => candidate.Length)
            .ThenBy(candidate => candidate.FullName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var canvasSize = Math.Max(maxPixel, 256);
        var cardWidth = (int)Math.Round(canvasSize * 0.40);
        var cardHeight = (int)Math.Round(canvasSize * 0.56);
        var borderWidth = Math.Max(2, (int)Math.Round(canvasSize * 0.012));
        var previews = new List<MagickImage>(MaxPreviewImages);
        var selectedImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var entry in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var logicalImageKey = GetLogicalImageKey(entry);

                if (selectedImages.Contains(logicalImageKey))
                {
                    continue;
                }

                try
                {
                    var preview = await LoadPreviewCardAsync(
                        entry,
                        cardWidth,
                        cardHeight,
                        borderWidth,
                        cancellationToken).ConfigureAwait(false);

                    previews.Add(preview);
                    selectedImages.Add(logicalImageKey);

                    if (previews.Count == MaxPreviewImages)
                    {
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // A damaged or unsupported entry should not prevent another image
                    // in the archive from becoming the preview.
                }
            }

            if (previews.Count == 0)
            {
                throw new InvalidDataException("ZIP 中的图片均无法生成缩略图。");
            }

            return RenderArchiveComposite(
                previews,
                distinctImageCount,
                outputPath,
                maxPixel,
                canvasSize);
        }
        finally
        {
            foreach (var preview in previews)
            {
                preview.Dispose();
            }
        }
    }

    private static bool IsZipSignature(ReadOnlySpan<byte> header)
    {
        return header.SequenceEqual(new byte[] { 0x50, 0x4B, 0x03, 0x04 }) ||
               header.SequenceEqual(new byte[] { 0x50, 0x4B, 0x05, 0x06 }) ||
               header.SequenceEqual(new byte[] { 0x50, 0x4B, 0x07, 0x08 }) ||
               header.SequenceEqual(new byte[] { 0x50, 0x4B, 0x06, 0x06 }) ||
               header.SequenceEqual(new byte[] { 0x50, 0x4B, 0x06, 0x07 });
    }

    private static bool IsSafePreviewEntry(ZipArchiveEntry entry)
    {
        if (entry.Length <= 0 ||
            entry.Length > MaxUncompressedEntryBytes ||
            entry.CompressedLength <= 0 ||
            entry.CompressedLength > MaxCompressedEntryBytes ||
            !PreviewExtensions.Contains(Path.GetExtension(entry.FullName)))
        {
            return false;
        }

        return true;
    }

    private static int GetFormatPriority(ZipArchiveEntry entry)
    {
        return PhotoshopExtensions.Contains(Path.GetExtension(entry.FullName)) ? 1 : 0;
    }

    private static string GetLogicalImageKey(ZipArchiveEntry entry)
    {
        var extensionLength = Path.GetExtension(entry.FullName).Length;
        return extensionLength == 0
            ? entry.FullName
            : entry.FullName[..^extensionLength];
    }

    private static int GetNamePriority(ZipArchiveEntry entry)
    {
        var name = Path.GetFileNameWithoutExtension(entry.FullName);

        foreach (var preferredPart in PreferredNameParts)
        {
            if (name.Equals(preferredPart, StringComparison.OrdinalIgnoreCase))
            {
                return 3;
            }

            if (name.Contains(preferredPart, StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }
        }

        return name.Equals("main", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    private static int GetDirectoryDepth(ZipArchiveEntry entry)
    {
        return entry.FullName.Count(character => character is '/' or '\\');
    }

    private static async Task<MagickImage> LoadPreviewCardAsync(
        ZipArchiveEntry entry,
        int width,
        int height,
        int borderWidth,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(entry.FullName).ToLowerInvariant();
        var tempPath = Path.Combine(Path.GetTempPath(), $"picpreview-zip-{Guid.NewGuid():N}{extension}");

        try
        {
            await using (var source = entry.Open())
            await using (var destination = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await CopyWithLimitAsync(
                    source,
                    destination,
                    MaxUncompressedEntryBytes,
                    cancellationToken).ConfigureAwait(false);
            }

            using var image = new MagickImage(tempPath);
            image.AutoOrient();
            image.Strip();
            image.Resize(new MagickGeometry((uint)width, (uint)height)
            {
                FillArea = true
            });
            image.Crop((uint)width, (uint)height, Gravity.Center);

            var card = new MagickImage(
                MagickColors.White,
                checked((uint)(width + borderWidth * 2)),
                checked((uint)(height + borderWidth * 2)));
            card.Composite(image, borderWidth, borderWidth, CompositeOperator.Over);
            return card;
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static RenderedImage RenderArchiveComposite(
        IReadOnlyList<MagickImage> previews,
        int imageCount,
        string outputPath,
        int maxPixel,
        int canvasSize)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        using var canvas = new MagickImage(
            MagickColors.Transparent,
            checked((uint)canvasSize),
            checked((uint)canvasSize));

        DrawArchiveBack(canvas, canvasSize);
        CompositePreviewCards(canvas, previews, canvasSize);
        DrawArchiveFront(canvas, canvasSize, imageCount);

        if (canvas.Width > maxPixel || canvas.Height > maxPixel)
        {
            canvas.Resize(new MagickGeometry((uint)maxPixel, (uint)maxPixel)
            {
                IgnoreAspectRatio = false
            });
        }

        canvas.Strip();
        canvas.Format = MagickFormat.Png;
        canvas.Write(outputPath);

        return new RenderedImage(
            outputPath,
            checked((int)canvas.Width),
            checked((int)canvas.Height));
    }

    private static void DrawArchiveBack(MagickImage canvas, int size)
    {
        new Drawables()
            .FillColor(new MagickColor("#39253D"))
            .RoundRectangle(
                size * 0.06,
                size * 0.24,
                size * 0.94,
                size * 0.65,
                size * 0.045,
                size * 0.045)
            .Draw(canvas);

        DrawArchiveBand(canvas, size, 0.08, 0.25, 0.92, 0.37, "#8E44AD");
        DrawArchiveBand(canvas, size, 0.08, 0.35, 0.92, 0.49, "#2F80D1");
        DrawArchiveBand(canvas, size, 0.08, 0.47, 0.92, 0.63, "#2DAA63");
    }

    private static void CompositePreviewCards(
        MagickImage canvas,
        IReadOnlyList<MagickImage> previews,
        int size)
    {
        var layouts = new[]
        {
            new CardLayout(0.50, 0.43, 0),
            new CardLayout(0.68, 0.45, 8),
            new CardLayout(0.32, 0.45, -8)
        };

        for (var index = previews.Count - 1; index >= 0; index--)
        {
            using var card = (MagickImage)previews[index].Clone();
            var layout = layouts[index];

            if (layout.Rotation != 0)
            {
                card.BackgroundColor = MagickColors.Transparent;
                card.Rotate(layout.Rotation);
            }

            var x = (int)Math.Round(size * layout.CenterX - card.Width / 2.0);
            var y = (int)Math.Round(size * layout.CenterY - card.Height / 2.0);

            canvas.Composite(card, x, y, CompositeOperator.Over);
        }
    }

    private static void DrawArchiveFront(MagickImage canvas, int size, int imageCount)
    {
        new Drawables()
            .FillColor(new MagickColor("#39253D"))
            .RoundRectangle(
                size * 0.055,
                size * 0.59,
                size * 0.945,
                size * 0.925,
                size * 0.05,
                size * 0.05)
            .Draw(canvas);

        DrawArchiveBand(canvas, size, 0.07, 0.60, 0.93, 0.70, "#8E44AD");
        DrawArchiveBand(canvas, size, 0.07, 0.69, 0.93, 0.80, "#2F80D1");
        DrawArchiveBand(canvas, size, 0.07, 0.79, 0.93, 0.91, "#2DAA63");

        new Drawables()
            .FillColor(new MagickColor("#5D3419"))
            .RoundRectangle(
                size * 0.425,
                size * 0.565,
                size * 0.575,
                size * 0.94,
                size * 0.025,
                size * 0.025)
            .Draw(canvas);

        new Drawables()
            .FillColor(new MagickColor("#A8692D"))
            .RoundRectangle(
                size * 0.445,
                size * 0.565,
                size * 0.555,
                size * 0.94,
                size * 0.018,
                size * 0.018)
            .Draw(canvas);

        new Drawables()
            .FillColor(new MagickColor("#F3C458"))
            .RoundRectangle(
                size * 0.385,
                size * 0.64,
                size * 0.615,
                size * 0.76,
                size * 0.025,
                size * 0.025)
            .Draw(canvas);

        new Drawables()
            .FillColor(new MagickColor("#5D3419"))
            .RoundRectangle(
                size * 0.425,
                size * 0.67,
                size * 0.575,
                size * 0.73,
                size * 0.012,
                size * 0.012)
            .Draw(canvas);

        DrawArchiveBadge(canvas, size, 0.09, 0.37, "ZIP");
        DrawArchiveBadge(canvas, size, 0.63, 0.91, $"×{imageCount}");
    }

    private static void DrawArchiveBand(
        MagickImage canvas,
        int size,
        double left,
        double top,
        double right,
        double bottom,
        string color)
    {
        new Drawables()
            .FillColor(new MagickColor(color))
            .RoundRectangle(
                size * left,
                size * top,
                size * right,
                size * bottom,
                size * 0.035,
                size * 0.035)
            .Draw(canvas);
    }

    private static void DrawArchiveBadge(
        MagickImage canvas,
        int size,
        double left,
        double right,
        string label)
    {
        var top = size * 0.815;
        var bottom = size * 0.895;
        var availableWidth = size * (right - left - 0.035);
        var fontSize = CalculateBadgeFontSize(label, size, availableWidth);

        new Drawables()
            .FillColor(new MagickColor("#274635"))
            .RoundRectangle(
                size * left,
                top,
                size * right,
                bottom,
                size * 0.025,
                size * 0.025)
            .Draw(canvas);

        new Drawables()
            .Font("Arial")
            .FontPointSize(fontSize)
            .FillColor(MagickColors.White)
            .TextAlignment(TextAlignment.Center)
            .Text(size * ((left + right) / 2.0), size * 0.876, label)
            .Draw(canvas);
    }

    private static double CalculateBadgeFontSize(
        string label,
        int size,
        double availableWidth)
    {
        var fontSize = size * 0.065;
        var estimatedWidth = label.Length * fontSize * 0.62;

        if (estimatedWidth > availableWidth)
        {
            fontSize *= availableWidth / estimatedWidth;
        }

        return Math.Max(size * 0.04, fontSize);
    }

    private static async Task CopyWithLimitAsync(
        Stream source,
        Stream destination,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        long totalBytes = 0;

        while (true)
        {
            var bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (bytesRead == 0)
            {
                break;
            }

            totalBytes += bytesRead;

            if (totalBytes > maxBytes)
            {
                throw new InvalidDataException($"ZIP 中的图片解压后超过限制（{maxBytes} 字节）。");
            }

            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup for temporary archive entries.
        }
    }

    private readonly record struct CardLayout(double CenterX, double CenterY, double Rotation);
}
