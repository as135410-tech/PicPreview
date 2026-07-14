using System.Windows.Media;

namespace QuickLooker.App;

internal sealed record GifPreviewAnimation(
    IReadOnlyList<GifPreviewFrame> Frames,
    int IterationCount);

internal sealed record GifPreviewFrame(
    ImageSource Image,
    TimeSpan Delay);
