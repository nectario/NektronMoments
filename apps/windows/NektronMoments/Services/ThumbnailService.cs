using System.Security.Cryptography;
using System.Text;
using NektronMoments.Models;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace NektronMoments.Services;

public sealed class ThumbnailService
{
    public static ThumbnailService Shared { get; } = new();
    private readonly SemaphoreSlim _workers = new(PerformanceProfile.Current.ForegroundWorkers);
    private readonly SemaphoreSlim _prefetchWorkers = new(PerformanceProfile.Current.PrefetchWorkers);
    private readonly WeightedCache<byte[]> _memory = new(PerformanceProfile.Current.EncodedBytes);
    private static readonly long CacheLimit = PerformanceProfile.Current.DiskBytes;
    private long _epoch;
    public string Key(MediaItem item, uint size) => $"{_epoch}|{item.Key}|{item.Path}|{item.ByteSize}|{item.ModifiedNs}|{size}";
    public long EncodedBytes => _memory.Bytes;
    public void ClearMemory() { Interlocked.Increment(ref _epoch); _memory.Clear(); }
    private long _cacheBytes;
    private readonly Task _maintenance;
    private readonly string _cache = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NektronMoments", "Thumbnails");
    public ThumbnailService()
    {
        Directory.CreateDirectory(_cache);
        _maintenance = Task.Run(() => _cacheBytes = Prune());
    }
    public async Task<byte[]?> LoadAsync(MediaItem item, uint size, CancellationToken cancellation, bool prefetch = false)
    {
        var key = Key(item, size);
        if (_memory.TryGet(key, out var ready)) return ready;
        var workers = prefetch ? _prefetchWorkers : _workers;
        await workers.WaitAsync(cancellation);
        try
        {
            if (_memory.TryGet(key, out ready)) return ready;
            await _maintenance.WaitAsync(cancellation);
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
                        if (File.Exists(cached)) return await File.ReadAllBytesAsync(cached, cancellation);
                        var file = await StorageFile.GetFileFromPathAsync(path);
                        using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, size, ThumbnailOptions.ResizeThumbnail);
                        if (thumbnail is null || thumbnail.Type != ThumbnailType.Image || thumbnail.Size > 16 * 1024 * 1024) continue;
                        using var reader = new Windows.Storage.Streams.DataReader(thumbnail);
                        await reader.LoadAsync((uint)thumbnail.Size);
                        var bytes = new byte[(int)thumbnail.Size];
                        reader.ReadBytes(bytes);
                        cancellation.ThrowIfCancellationRequested();
                        if (Interlocked.Add(ref _cacheBytes, bytes.Length) <= CacheLimit) {
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
                        } else { Interlocked.Add(ref _cacheBytes, -bytes.Length); }
                        return bytes;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
                }
                return null;
            }, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (loaded is not null) _memory.Put(key, loaded, loaded.Length);
            return loaded;
        }
        finally { workers.Release(); }
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
