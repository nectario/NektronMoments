using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NektronMoments.Models;
using NektronMoments.Services;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;

namespace NektronMoments.Controls;

public sealed partial class MediaThumbnail : UserControl
{
    public static readonly WeightedCache<PreparedThumbnail> Decoded = new(PerformanceProfile.Current.DecodedBytes);
    private static readonly SharedThumbnailPreparation<PreparedThumbnail> Preparing = new();
    private static readonly ThumbnailDecodeLimiter DecodeSlots = new(
        GalleryWorkPolicy.ForegroundDecodes, Math.Clamp(PerformanceProfile.Current.PrefetchWorkers, 1, 4));
    private static readonly uint[] LargerReadyTiers = [256, 512, 1024];
    private static DispatcherQueue? _dispatcher;
    private static ThumbnailPresentationQueue? _presentation;
    private static readonly ThumbnailWorkGate BackgroundWork = new();
    private static readonly Task NeverPromoted = new TaskCompletionSource().Task;
    public static async Task WarmEncodedAsync(MediaItem item, uint pixels, CancellationToken token)
    {
        await BackgroundWork.WaitAsync(NeverPromoted, token).ConfigureAwait(false);
        await ThumbnailService.Shared.LoadAsync(item, pixels, token, prefetch: true).ConfigureAwait(false);
    }
    private static bool _thumbInputHeld;
    public static void NotifyScrollInput() => BackgroundWork.Pulse();
    public static bool IsInputActive => BackgroundWork.IsPaused;
    public static void SetThumbInput(bool held) {
        _thumbInputHeld = held; BackgroundWork.Hold(held || _resizePreview);
        if (_presentation is not null) _presentation.InteractionActive = held;
    }
    private static long _preparationCount, _readyCacheHits, _uiDecodeAttempts;
    public static int PendingLoads => Preparing.Count;
    public static long PreparationCount => Interlocked.Read(ref _preparationCount);
    public static long ReadyCacheHits => Interlocked.Read(ref _readyCacheHits);
    public static long UiDecodeAttempts => Interlocked.Read(ref _uiDecodeAttempts);
    public static long SourceCreationCount => PreparedThumbnail.SourceCreations;
    public static int PendingPresentations => _presentation?.Pending ?? 0; // UI diagnostics only.
    public static double MaximumPresentationBatchMs => _presentation?.MaximumBatchMs ?? 0;
    private CancellationTokenSource? _loading;
    private long _requestVersion;
    private string _loadingKey = "", _displayedKey = "", _displayedIdentity = "";
    private uint _displayedPixels;
    private bool _viewportKnown, _nearViewport, _observingViewport;
    internal bool ObservesViewport => _observingViewport; // Native regression checks, never polled while scrolling.
    internal object CaptureDiagnosticState() => new {
        isLoaded = IsLoaded, item = Item?.Key, targetPixels = TargetPixels, displayedPixels = _displayedPixels,
        viewportKnown = _viewportKnown, nearViewport = _nearViewport, nearGeometry = NearViewport(),
        observing = _observingViewport, loading = _loading is { IsCancellationRequested: false },
        requestVersion = _requestVersion, hasSource = Picture.Source is not null,
        width = ActualWidth, height = ActualHeight,
    };
    public static uint TargetPixels { get; set; } = 512;
    public static event Action? ResolutionChanged;
    private static bool _resizePreview;
    public static void SetResizePreview(bool active) {
        _resizePreview = active; BackgroundWork.Hold(active || _thumbInputHeld);
        if (!active) ResolutionChanged?.Invoke();
    }
    public static void SetResolution(uint pixels) { if (TargetPixels == pixels) return; TargetPixels = pixels; ResolutionChanged?.Invoke(); }
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item), typeof(MediaItem), typeof(MediaThumbnail), new PropertyMetadata(null, ItemChanged));
    public MediaItem? Item { get => (MediaItem?)GetValue(ItemProperty); set => SetValue(ItemProperty, value); }

    public static void InitializeDispatcher(DispatcherQueue dispatcher)
    {
        if (!dispatcher.HasThreadAccess) throw new InvalidOperationException("Register thumbnail presentation on the UI thread.");
        if (_dispatcher is not null && !ReferenceEquals(_dispatcher, dispatcher)) {
            // This preview has one UI thread; all controls share its queue.
            if (!_dispatcher.HasThreadAccess) throw new InvalidOperationException("Thumbnail cache belongs to another UI thread.");
        }
        _dispatcher = dispatcher;
        _presentation ??= new ThumbnailPresentationQueue(dispatcher, () => IsInputActive);
    }
    public static async Task WarmDisplayAsync(PreparedThumbnail bitmap, CancellationToken token)
    {
        if (_presentation is null) return;
        try { await _presentation.Enqueue(bitmap, () => !token.IsCancellationRequested, token, background: true); }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Optional warming cannot fail a foreground photo. */ }
    }
    public static void ShutdownPresentation() => _presentation?.Close();

    // No dispatcher hop, XAML object creation, or publication is part of prefetch.
    public static Task<PreparedThumbnail?> PrepareAsync(MediaItem item, uint pixels,
        CancellationToken callerToken, bool prefetch = true)
    {
        callerToken.ThrowIfCancellationRequested();
        var key = ThumbnailService.Shared.Key(item, pixels);
        if (TryGetReady(item, pixels, out var ready, out _)) {
            Interlocked.Increment(ref _readyCacheHits);
            return Task.FromResult<PreparedThumbnail?>(ready);
        }
        return Preparing.GetAsync(key, !prefetch, promoted => PrepareCoreAsync(item, pixels, key, promoted), callerToken);
    }

    private static async Task<PreparedThumbnail?> PrepareCoreAsync(MediaItem item, uint pixels, string key, Task promoted)
    {
        // SharedThumbnailPreparation starts this on Task.Run. Every native decode
        // continuation stays on worker threads, never the window's synchronization context.
        if (_dispatcher?.HasThreadAccess == true) {
            Interlocked.Increment(ref _uiDecodeAttempts);
            throw new InvalidOperationException("Thumbnail decoding must not run on the UI thread.");
        }
        if (TryGetReady(item, pixels, out var ready, out _)) return ready;
        await BackgroundWork.WaitAsync(promoted).ConfigureAwait(false);
        var ioTrace = DiagnosticTrace.Start(1);
        byte[]? bytes;
        try { bytes = await ThumbnailService.Shared.LoadAsync(item, pixels, CancellationToken.None,
            prefetch: true, promoted).ConfigureAwait(false); }
        finally { DiagnosticTrace.Stop(ioTrace, 1); }
        if (bytes is null || ThumbnailService.Shared.Key(item, pixels) != key) return null;
        await BackgroundWork.WaitAsync(promoted).ConfigureAwait(false);
        using var slot = await DecodeSlots.AcquireAsync(false, promoted, CancellationToken.None).ConfigureAwait(false);
        if (TryGetReady(item, pixels, out ready, out _)) return ready;
        if (ThumbnailService.Shared.Key(item, pixels) != key) return null;
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream)) {
            writer.WriteBytes(bytes); await writer.StoreAsync(); writer.DetachStream();
        }
        stream.Seek(0);
        var decodeTrace = DiagnosticTrace.Start(2);
        try {
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var scale = Math.Min(1d, pixels / (double)Math.Max(1u, Math.Max(decoder.PixelWidth, decoder.PixelHeight)));
        var transform = new BitmapTransform {
            ScaledWidth = Math.Max(1u, (uint)Math.Round(decoder.PixelWidth * scale)),
            ScaledHeight = Math.Max(1u, (uint)Math.Round(decoder.PixelHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        var decoded = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        // Detach decoded pixels before publishing. Create only one SoftwareBitmap,
        // avoiding a temporary bitmap's WinRT memory-pressure/reference-tracker churn.
        var pixelsData = decoded.DetachPixelData();
        var width = (int)transform.ScaledWidth; var height = (int)transform.ScaledHeight;
        if (decoder.OrientedPixelWidth != decoder.PixelWidth) (width, height) = (height, width);
        if (pixelsData.Length != checked(width * height * 4)) throw new InvalidDataException("Unexpected oriented thumbnail dimensions.");
        var prepared = new PreparedThumbnail(SoftwareBitmap.CreateCopyFromBuffer(pixelsData.AsBuffer(),
            BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied), Environment.CurrentManagedThreadId);
        Interlocked.Increment(ref _preparationCount);
        if (ThumbnailService.Shared.Key(item, pixels) != key) { prepared.DiscardUnpublishedPixels(); return null; }
        Decoded.Put(key, prepared, prepared.Bytes);
        return prepared;
        } finally { DiagnosticTrace.Stop(decodeTrace, 2); }
    }

    public MediaThumbnail()
    {
        InitializeComponent();
        InitializeDispatcher(DispatcherQueue);
        Loaded += (_, _) => {
            ResolutionChanged += ResolutionUpdated;
            ObserveViewport();
            Load();
        };
        Unloaded += (_, _) => {
            ResolutionChanged -= ResolutionUpdated; StopObservingViewport();
            ++_requestVersion; _loading?.Cancel();
            Picture.Source = null; Placeholder.Visibility = Visibility.Visible;
            _displayedKey = ""; _viewportKnown = false;
        };
    }
    private static void ItemChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (MediaThumbnail)sender;
        ++control._requestVersion; control._loading?.Cancel();
        control.Picture.Source = null; control.Placeholder.Visibility = Visibility.Visible;
        control._displayedKey = "";
        if (control.IsLoaded) { control.ObserveViewport(); control.Load(); }
    }

    private void ObserveViewport()
    {
        // Recycled items and resolution changes must not inherit the previous
        // item's last visible position while waiting for the next native event.
        _viewportKnown = false; _nearViewport = false;
        if (_observingViewport) return;
        EffectiveViewportChanged += ViewportChanged;
        _observingViewport = true;
    }
    private void StopObservingViewport()
    {
        if (!_observingViewport) return;
        EffectiveViewportChanged -= ViewportChanged;
        _observingViewport = false;
    }
    private void ResolutionUpdated()
    {
        ObserveViewport();
        Load();
    }

    private void ViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        var near = GalleryWorkPolicy.IsNearViewport(ActualWidth, ActualHeight,
            args.BringIntoViewDistanceX, args.BringIntoViewDistanceY);
        _viewportKnown = true;
        if (!near) { _nearViewport = false; _loading?.Cancel(); return; }
        var entered = !_nearViewport;
        _nearViewport = true;
        if (entered || (Picture.Source is null && _loading is not { IsCancellationRequested: false })) Load();
    }

    private async void Load()
    {
        var item = Item;
        if (item is null || !IsLoaded) return;
        var pixels = TargetPixels;
        var key = ThumbnailService.Shared.Key(item, pixels);
        var identity = ThumbnailService.Shared.Key(item, 0);
        if (Picture.Source is not null && _displayedIdentity == identity && _displayedPixels >= pixels) {
            StopObservingViewport();
            return;
        }
        if (_loading is { IsCancellationRequested: false } && _loadingKey == key) return;
        var version = ++_requestVersion;
        _loading?.Cancel();
        if (!NearViewport()) return; // Global worker lookahead handles offscreen pixels, without UI tasks.
        if (TryGetReady(item, pixels, out var cached, out var readyPixels) && cached.Source is { } source) {
            Interlocked.Increment(ref _readyCacheHits);
            Show(source, key, identity, readyPixels);
            return;
        }
        if (_resizePreview) return;
        var loading = new CancellationTokenSource(); _loading = loading; _loadingKey = key;
        bool IsCurrent() => !loading.IsCancellationRequested && version == _requestVersion && IsLoaded &&
            ReferenceEquals(Item, item) && pixels == TargetPixels &&
            ThumbnailService.Shared.Key(item, pixels) == key && !_resizePreview && NearViewport();
        try {
            Placeholder.Visibility = Picture.Source is null ? Visibility.Visible : Visibility.Collapsed;
            // Do not post one UI continuation per completed background preview:
            // only near-viewport tiles await a worker result for presentation.
            var prepared = cached ?? await PrepareAsync(item, pixels, loading.Token, prefetch: false);
            if (prepared is null || !IsCurrent()) return;
            var image = await _presentation!.Enqueue(prepared, IsCurrent, loading.Token);
            if (image is not null && IsCurrent()) Show(image, key, identity, pixels);
        } catch (OperationCanceledException) { }
        catch (Exception) { /* Missing/unsupported originals retain a labeled placeholder. */ }
        finally {
            if (ReferenceEquals(_loading, loading)) { _loading = null; _loadingKey = ""; }
            loading.Dispose();
        }
    }

    private static bool TryGetReady(MediaItem item, uint pixels, out PreparedThumbnail ready, out uint readyPixels)
    {
        readyPixels = pixels;
        if (Decoded.TryGet(ThumbnailService.Shared.Key(item, pixels), out ready)) return true;
        foreach (var larger in LargerReadyTiers) {
            if (larger <= pixels || !Decoded.TryGet(ThumbnailService.Shared.Key(item, larger), out ready)) continue;
            readyPixels = larger; return true;
        }
        ready = null!; return false;
    }
    private void Show(ImageSource source, string key, string identity, uint pixels)
    {
        Picture.Source = source; Placeholder.Visibility = Visibility.Collapsed;
        _displayedKey = key; _displayedIdentity = identity; _displayedPixels = pixels;
        // A ready tile needs no more native viewport event wrappers during each
        // scroll frame. Item/load/resolution changes explicitly re-arm observation.
        StopObservingViewport();
    }
    private bool NearViewport()
    {
        if (_viewportKnown) return _nearViewport;
        if (!IsLoaded || ActualWidth <= 0 || ActualHeight <= 0) return false;
        DependencyObject? parent = this;
        while ((parent = VisualTreeHelper.GetParent(parent)) is not null) {
            if (parent is not ScrollViewer viewport) continue;
            var origin = TransformToVisual(viewport).TransformPoint(new Windows.Foundation.Point());
            return origin.X + ActualWidth > -128 && origin.X < viewport.ActualWidth + 128 &&
                origin.Y + ActualHeight > -128 && origin.Y < viewport.ActualHeight + 128;
        }
        return true;
    }
}
