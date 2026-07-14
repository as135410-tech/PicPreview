namespace QuickLooker.Core;

public sealed record RenderedAnimation(
    IReadOnlyList<RenderedAnimationFrame> Frames,
    int IterationCount);

public sealed record RenderedAnimationFrame(
    byte[] PngBytes,
    TimeSpan Delay);
