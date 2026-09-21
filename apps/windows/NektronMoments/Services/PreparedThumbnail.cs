using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace NektronMoments.Services;

/// <summary>Worker-decoded pixels; XAML presentation is paced for visible tiles or nearby idle warming.</summary>
public sealed class PreparedThumbnail
{
    private SoftwareBitmap? _pixels;
    private Task<SoftwareBitmapSource>? _presentation;
    public SoftwareBitmapSource? Source { get; private set; } // UI-thread access only.
    public int Width { get; }
    public int Height { get; }
    public long Bytes { get; }
    public int DecodeThreadId { get; }
    private static long _sourceCreations;
    public static long SourceCreations => Interlocked.Read(ref _sourceCreations);

    public PreparedThumbnail(SoftwareBitmap pixels, int decodeThreadId)
    {
        _pixels = pixels;
        Width = pixels.PixelWidth; Height = pixels.PixelHeight;
        // Reserve for worker pixels plus a possible presentation copy. The cache
        // is a byte budget, not an assumption that XAML immediately releases data.
        Bytes = Math.Max(1L, (long)Width * Height * 8);
        DecodeThreadId = decodeThreadId;
    }

    // Called only by the paced UI presentation queue. Other callers reuse its Task.
    public Task<SoftwareBitmapSource> PresentAsync()
    {
        if (Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread() is not { HasThreadAccess: true })
            throw new InvalidOperationException("Image presentation requires its UI dispatcher.");
        if (Source is { } ready) return Task.FromResult(ready);
        if (_presentation is { IsFaulted: false, IsCanceled: false }) return _presentation;
        return _presentation = UploadAsync();
    }

    private async Task<SoftwareBitmapSource> UploadAsync()
    {
        var source = new SoftwareBitmapSource();
        Interlocked.Increment(ref _sourceCreations);
        try {
            await source.SetBitmapAsync(_pixels ?? throw new ObjectDisposedException(nameof(PreparedThumbnail)));
            Source = source;
            // Retain the immutable pixels for the lifetime of the cached source.
            // A recycled Image can reconnect this source on a later layout pass.
            // Do not explicitly Close a bitmap still referenced by native rendering.
            return source;
        } catch {
            source.Dispose();
            _presentation = null;
            throw;
        }
    }

    public void DiscardUnpublishedPixels()
    {
        Interlocked.Exchange(ref _pixels, null)?.Dispose();
    }

    // For published images, COM reference lifetime (including native consumers)
    // owns pixel release. Explicit Close is reserved for unpublished failures.
}
