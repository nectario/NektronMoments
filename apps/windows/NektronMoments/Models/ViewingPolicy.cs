namespace NektronMoments.Models;

public readonly record struct ImageFit(double Width, double Height, double Scale);
public static class ViewingPolicy
{
    public static ImageFit Fit(double pixelWidth, double pixelHeight, double canvasWidth,
        double canvasHeight, double rasterScale, bool allowUpscale)
    {
        if (!double.IsFinite(pixelWidth + pixelHeight + canvasWidth + canvasHeight + rasterScale) ||
            pixelWidth <= 0 || pixelHeight <= 0 || canvasWidth <= 0 || canvasHeight <= 0 || rasterScale <= 0)
            return new(0, 0, 0);
        var scale = Math.Min(canvasWidth * rasterScale / pixelWidth, canvasHeight * rasterScale / pixelHeight);
        if (!allowUpscale) scale = Math.Min(1, scale);
        return new(pixelWidth * scale / rasterScale, pixelHeight * scale / rasterScale, scale);
    }
    public static double ThumbnailWidth(double value) => Math.Clamp(double.IsFinite(value) ? value : 240, 112, 480);
    public static uint ThumbnailPixels(double dips, double rasterScale) =>
        (uint)(ThumbnailWidth(dips) * Math.Clamp(rasterScale, 1, 4) <= 256 ? 256 :
        ThumbnailWidth(dips) * Math.Clamp(rasterScale, 1, 4) <= 512 ? 512 : 1024);
}

public sealed record GalleryBudget(string Name, int ThumbnailAhead, double ViewportCache, long EncodedBytes, long DecodedBytes, long DiskBytes,
    int ForegroundWorkers, int PrefetchWorkers)
{
    public static GalleryBudget ForMemory(ulong physicalBytes)
    {
        const long MiB = 1024 * 1024;
        if (physicalBytes >= 96UL * 1024 * 1024 * 1024)
            return new("Workstation", 8192, 8, 1024 * MiB, 12288 * MiB, 8192 * MiB, 12, 4);
        if (physicalBytes >= 24UL * 1024 * 1024 * 1024)
            return new("Enhanced", 2048, 5, 512 * MiB, 2048 * MiB, 4096 * MiB, 8, 2);
        return new("Balanced", 512, 3, 128 * MiB, 384 * MiB, 1024 * MiB, 4, 1);
    }
}
