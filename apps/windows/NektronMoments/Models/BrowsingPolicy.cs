namespace NektronMoments.Models;

/// <summary>Separates the native scrollbar's exposed items from prepared thumbnail memory.</summary>
public static class BrowsingPolicy
{
    public const int BatchSize = 200;
    public static int DisplayWarmCount(int available, ulong physicalBytes) => Math.Clamp(available, 0,
        physicalBytes >= 96UL * 1024 * 1024 * 1024 ? 1000 : physicalBytes >= 24UL * 1024 * 1024 * 1024 ? 400 : 160);
    public static int BatchFor(double thumbnailWidth)
    {
        if (!double.IsFinite(thumbnailWidth) || thumbnailWidth <= 0) return BatchSize;
        var desired = 200 * Math.Pow(240 / Math.Max(100, thumbnailWidth), 2);
        return (int)Math.Clamp(Math.Round(desired / 50) * 50, 200, 500);
    }

    public static int InitialCount(int total, int batch = BatchSize) => Math.Clamp(total, 0, Math.Clamp(batch, 200, 500));

    public static int NextCount(int total, int current, int batch = BatchSize)
    {
        total = Math.Max(0, total);
        current = Math.Clamp(current, 0, total);
        return current + Math.Min(Math.Clamp(batch, 200, 500), total - current);
    }

    public static bool ShouldExtend(int total, int current, int lastVisible, int visibleCount, bool dragging, int batch = BatchSize)
    {
        if (dragging || current <= 0 || current >= total || lastVisible < 0 || visibleCount <= 0) return false;
        // Prepare a new range about one viewport before its end, without making
        // a dense/maximized viewport's threshold bigger than half a batch.
        var remaining = current - 1L - Math.Min(lastVisible, current - 1);
        return remaining <= Math.Clamp(visibleCount, 24, Math.Clamp(batch, 200, 500) / 2);
    }

    /// <summary>
    /// Visible first, then the next batch in the direction of travel. Keep a
    /// small reverse-side buffer before warming farther ahead. ExposedCount
    /// only bounds the visible viewport; lookahead uses the complete catalog.
    /// </summary>
    public static int[] PrefetchIndexes(int total, int firstVisible, int lastVisible,
        int exposedCount, int direction, int maximumCount)
    {
        if (total <= 0 || exposedCount <= 0 || maximumCount <= 0) return [];
        var exposed = Math.Min(total, exposedCount);
        var first = Math.Clamp(firstVisible, 0, exposed - 1);
        var last = Math.Clamp(lastVisible, first, exposed - 1);
        var maximum = Math.Min(total, maximumCount);
        var indexes = new List<int>(maximum);

        void Add(int start, int end, int step)
        {
            for (long index = start; indexes.Count < maximum &&
                (step > 0 ? index <= end : index >= end); index += step)
                indexes.Add((int)index);
        }

        Add(first, last, 1);
        var reverseReserve = Math.Min(BatchSize, Math.Max(0, maximum - indexes.Count) / 8);
        if (direction >= 0) {
            var nearEnd = (int)Math.Min(total - 1L, last + (long)BatchSize);
            var reverseEnd = Math.Max(0, first - reverseReserve);
            Add(last + 1, nearEnd, 1);
            Add(first - 1, reverseEnd, -1);
            Add(nearEnd + 1, total - 1, 1);
            Add(reverseEnd - 1, 0, -1);
        }
        else {
            var nearStart = Math.Max(0, first - BatchSize);
            var reverseEnd = (int)Math.Min(total - 1L, last + (long)reverseReserve);
            Add(first - 1, nearStart, -1);
            Add(last + 1, reverseEnd, 1);
            Add(nearStart - 1, 0, -1);
            Add(reverseEnd + 1, total - 1, 1);
        }
        return indexes.ToArray();
    }

    /// <summary>Leave at least half the decoded-image cache for recently viewed/hot thumbnails.</summary>
    public static int WarmCount(int thumbnailAhead, uint pixels, long decodedBudgetBytes)
    {
        if (thumbnailAhead <= 0 || pixels == 0 || decodedBudgetBytes <= 0) return 0;
        var pixelCount = (ulong)pixels * pixels;
        if (pixelCount > ulong.MaxValue / 4) return 0;
        var bytesPerThumbnail = pixelCount * 4;
        return (int)Math.Min((ulong)thumbnailAhead, (ulong)(decodedBudgetBytes / 2) / bytesPerThumbnail);
    }
}
