using ImageMagick;

namespace QuickLooker.Core;

public static class ImageRenderer
{
    public static Task<RenderedImage> RenderThumbnailAsync(
        string inputPath,
        string outputPath,
        int maxPixel,
        CancellationToken cancellationToken = default)
    {
        return RenderToPngAsync(inputPath, outputPath, maxPixel, cancellationToken);
    }

    public static async Task<byte[]> RenderPreviewPngAsync(
        string inputPath,
        int maxPixel,
        CancellationToken cancellationToken = default)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"picpreview-preview-{Guid.NewGuid():N}.png");

        try
        {
            await RenderToPngAsync(inputPath, tempPath, maxPixel, cancellationToken).ConfigureAwait(false);
            return await File.ReadAllBytesAsync(tempPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static Task<RenderedImage> RenderToPngAsync(
        string inputPath,
        string outputPath,
        int maxPixel,
        CancellationToken cancellationToken)
    {
        if (maxPixel <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPixel), "尺寸必须大于 0。");
        }

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

            using var image = new MagickImage(inputPath);
            image.AutoOrient();

            if (image.Width > maxPixel || image.Height > maxPixel)
            {
                image.Resize(new MagickGeometry((uint)maxPixel, (uint)maxPixel)
                {
                    IgnoreAspectRatio = false
                });
            }

            image.Strip();
            image.Format = MagickFormat.Png;
            image.Write(outputPath);

            cancellationToken.ThrowIfCancellationRequested();
            return new RenderedImage(outputPath, checked((int)image.Width), checked((int)image.Height));
        }, cancellationToken);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup for temp previews.
        }
    }
}
