namespace NektronMoments.Models;

/// <summary>Bounds live XAML work independently of the much larger photo/metadata caches.</summary>
public static class GalleryWorkPolicy
{
    public const int ForegroundDecodes = 2;
    public const int LookaheadDecodes = 1;
    public const int OffscreenContainers = 240;

    public static double CacheLength(int columns, double rowHeight, double viewportHeight, double maximum)
    {
        maximum = double.IsFinite(maximum) ? Math.Max(.5, maximum) : .5;
        if (columns <= 0 || !double.IsFinite(rowHeight) || rowHeight <= 0 ||
            !double.IsFinite(viewportHeight) || viewportHeight <= 0) return .5;
        // CacheLength counts the combined offscreen buffers, in viewport units.
        // One spare row covers the partially visible rows at either end.
        var visible = Math.Max(1, (Math.Ceiling(viewportHeight / rowHeight) + 1) * columns);
        return Math.Clamp(OffscreenContainers / visible, .5, maximum);
    }

    public static bool IsNearViewport(double width, double height, double distanceX, double distanceY) =>
        double.IsFinite(width + height + distanceX + distanceY) && width > 0 && height > 0 &&
        distanceX >= 0 && distanceY >= 0 && distanceX < width + 128 && distanceY < height + 128;
}
