using ImageMagick;
using ImageMagick.Drawing;

namespace QuickLooker.Core;

public static class ImageRenderer
{
    public const string RenderCacheVersion = "3";

    private static readonly TimeSpan DefaultAnimationFrameDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MinimumAnimationFrameDelay = TimeSpan.FromMilliseconds(10);

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

    public static Task<RenderedAnimation> RenderGifAnimationAsync(
        string inputPath,
        int maxPixel,
        CancellationToken cancellationToken = default)
    {
        if (maxPixel <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPixel), "尺寸必须大于 0。");
        }

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var images = new MagickImageCollection(inputPath);

            if (images.Count == 0)
            {
                throw new InvalidDataException("GIF 中没有可显示的帧。");
            }

            var iterationCount = checked((int)images[0].AnimationIterations);
            images.Coalesce();

            var frames = new List<RenderedAnimationFrame>(images.Count);

            foreach (var image in images)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var delay = GetAnimationFrameDelay(image);

                image.AutoOrient();

                if (image.Width > maxPixel || image.Height > maxPixel)
                {
                    image.Resize(new MagickGeometry((uint)maxPixel, (uint)maxPixel)
                    {
                        IgnoreAspectRatio = false
                    });
                }

                image.Strip();
                frames.Add(new RenderedAnimationFrame(image.ToByteArray(MagickFormat.Png), delay));
            }

            return new RenderedAnimation(frames, iterationCount);
        }, cancellationToken);
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
            image.Strip();

            if (image.Width > maxPixel || image.Height > maxPixel)
            {
                image.Resize(new MagickGeometry((uint)maxPixel, (uint)maxPixel)
                {
                    IgnoreAspectRatio = false
                });
            }

            using var outputImage = AddFormatBadgeIfNeeded(inputPath, image, maxPixel);
            outputImage.Format = MagickFormat.Png;
            outputImage.Write(outputPath);

            cancellationToken.ThrowIfCancellationRequested();
            return new RenderedImage(outputPath, checked((int)outputImage.Width), checked((int)outputImage.Height));
        }, cancellationToken);
    }

    private static MagickImage AddFormatBadgeIfNeeded(string inputPath, MagickImage image, int maxPixel)
    {
        var formatBadge = SupportedImageFormats.GetFormatBadge(inputPath);

        if (formatBadge is null)
        {
            return (MagickImage)image.Clone();
        }

        var imageWidth = checked((int)image.Width);
        var imageHeight = checked((int)image.Height);
        var badgeHeight = CalculateBadgeHeight(imageWidth, imageHeight);
        var fontSize = CalculateFontSize(formatBadge, imageWidth, badgeHeight);
        var minimumBadgeWidth = CalculateMinimumBadgeWidth(formatBadge, fontSize, maxPixel);
        var canvasWidth = Math.Max(imageWidth, minimumBadgeWidth);
        var canvasHeight = imageHeight + badgeHeight;
        var imageX = (canvasWidth - imageWidth) / 2;
        var canvas = new MagickImage(MagickColors.Transparent, checked((uint)canvasWidth), checked((uint)canvasHeight));

        new Drawables()
            .FillColor(new MagickColor("#e14191"))
            .Rectangle(0, 0, canvasWidth, badgeHeight)
            .Draw(canvas);

        new Drawables()
            .Font("Arial")
            .FontPointSize(fontSize)
            .FillColor(MagickColors.White)
            .TextAlignment(TextAlignment.Center)
            .Text(canvasWidth / 2.0, badgeHeight * 0.72, formatBadge)
            .Draw(canvas);

        canvas.Composite(image, imageX, badgeHeight, CompositeOperator.Over);

        if (canvas.Width > maxPixel || canvas.Height > maxPixel)
        {
            canvas.Resize(new MagickGeometry((uint)maxPixel, (uint)maxPixel)
            {
                IgnoreAspectRatio = false
            });
        }

        return canvas;
    }

    private static TimeSpan GetAnimationFrameDelay(IMagickImage<ushort> image)
    {
        if (image.AnimationDelay == 0 || image.AnimationTicksPerSecond == 0)
        {
            return DefaultAnimationFrameDelay;
        }

        var delay = TimeSpan.FromSeconds((double)image.AnimationDelay / image.AnimationTicksPerSecond);
        return delay < MinimumAnimationFrameDelay ? MinimumAnimationFrameDelay : delay;
    }

    private static int CalculateBadgeHeight(int imageWidth, int imageHeight)
    {
        var referencePixel = Math.Max(imageWidth, imageHeight);
        return Math.Clamp((int)Math.Round(referencePixel * 0.14), 20, 96);
    }

    private static double CalculateFontSize(string label, int imageWidth, int badgeHeight)
    {
        var fontSize = badgeHeight * 0.58;
        var availableWidth = Math.Max(imageWidth - 10, 12);
        var estimatedWidth = label.Length * fontSize * 0.68;

        if (estimatedWidth > availableWidth)
        {
            fontSize *= availableWidth / estimatedWidth;
        }

        return Math.Clamp(fontSize, 9, 56);
    }

    private static int CalculateMinimumBadgeWidth(string label, double fontSize, int maxPixel)
    {
        var estimatedTextWidth = (int)Math.Ceiling(label.Length * fontSize * 0.75);
        return Math.Min(maxPixel, Math.Max(48, estimatedTextWidth + 18));
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
