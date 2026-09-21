using System.Security.Cryptography;
using System.Text;
using NektronMoments.Models;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace NektronMoments.Services;

public sealed class ThumbnailService
{
    private static readonly long CacheLimit = PerformanceProfile.Current.DiskBytes;
    public static ThumbnailService Shared { get; } = new();
    private readonly ThumbnailDecodeLimiter _workers = new(
        PerformanceProfile.Current.ForegroundWorkers, PerformanceProfile.Current.PrefetchWorkers);
    private readonly WeightedCache<byte[]> _memory = new(PerformanceProfile.Current.EncodedBytes);
    private readonly ThumbnailKeyCache _keys = new();
    private long _epoch;
    public string Key(MediaItem item, uint size) => _keys.Get(item, Volatile.Read(ref _epoch), size);
    public long EncodedBytes => _memory.Bytes;
    public void ClearMemory() { Interlocked.Increment(ref _epoch); _memory.Clear(); }
    private long _cacheBytes;
    private readonly Task _maintenance;
    private readonly string _cache = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NektronMoments", "Thumbnails");
    public ThumbnailService()
    {
        _maintenance = Task.Run(() => {
            try { Directory.CreateDirectory(_cache); _cacheBytes = Prune(); }
            catch (IOException) { _cacheBytes = CacheLimit; }
            catch (UnauthorizedAccessException) { _cacheBytes = CacheLimit; }
        });
    }
    public async Task<byte[]?> LoadAsync(MediaItem item, uint size, CancellationToken cancellation,
        bool prefetch = false, Task? promoted = null)
    {
        var key = Key(item, size);
        if (_memory.TryGet(key, out var ready)) return ready;
        using (await _workers.AcquireAsync(!prefetch, promoted, cancellation).ConfigureAwait(false)) {
            if (_memory.TryGet(key, out ready)) return ready;
            // Cache housekeeping is independent of the first visible thumbnails.
            // Reads and extraction proceed immediately; disk writes start only once
            // pruning has established its byte count and finished deleting old files.
            var loaded = await Task.Run(async () => {
                foreach (var path in item.Paths.Prepend(item.Path).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    cancellation.ThrowIfCancellationRequested();
                    try
                    {
                        var info = new FileInfo(path);
                        if (!info.Exists) continue;
                        var identity = path + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks + "|" + size;
                        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
                        var cached = System.IO.Path.Combine(_cache, "v2-" + name + ".thumb");
                        if (File.Exists(cached)) {
                            try { return await File.ReadAllBytesAsync(cached, cancellation); }
                            catch (IOException) { /* Pruning may have removed this disposable preview. */ }
                        }
                        var file = await StorageFile.GetFileFromPathAsync(path);
                        using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, size, ThumbnailOptions.ResizeThumbnail);
                        if (thumbnail is null || thumbnail.Type != ThumbnailType.Image || thumbnail.Size > 16 * 1024 * 1024) continue;
                        using var reader = new Windows.Storage.Streams.DataReader(thumbnail);
                        await reader.LoadAsync((uint)thumbnail.Size);
                        var bytes = new byte[(int)thumbnail.Size];
                        reader.ReadBytes(bytes);
                        cancellation.ThrowIfCancellationRequested();
                        var maintainReady = _maintenance.IsCompletedSuccessfully;
                        if (maintainReady && Interlocked.Add(ref _cacheBytes, bytes.Length) <= CacheLimit) {
                            var temporary = cached + "." + Guid.NewGuid().ToString("N") + ".tmp";
                            var written = false;
                            try {
                                await File.WriteAllBytesAsync(temporary, bytes, cancellation);
                                File.Move(temporary, cached, overwrite: true);
                                written = true;
                            } catch (IOException) { }
                            catch (UnauthorizedAccessException) { }
                            finally {
                                if (!written) Interlocked.Add(ref _cacheBytes, -bytes.Length);
                                try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { }
                            }
                        } else if (maintainReady) { Interlocked.Add(ref _cacheBytes, -bytes.Length); }
                        return bytes;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
                }
                return null;
            }, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (loaded is not null && Key(item, size) == key) _memory.Put(key, loaded, loaded.Length);
            return loaded;
        }
    }
    private long Prune()
    {
        try
        {
            var files = new DirectoryInfo(_cache).EnumerateFiles("*.thumb").OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
            long retained = 0;
            foreach (var file in files) {
                // Earlier Windows thumbnails could contain full-size image streams.
                // These are disposable previews, never original media.
                if (!file.Name.StartsWith("v2-", StringComparison.Ordinal)) { file.Delete(); continue; }
                if (retained + file.Length > CacheLimit) file.Delete();
                else retained += file.Length;
            }
            return retained;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return CacheLimit;
    }
}
