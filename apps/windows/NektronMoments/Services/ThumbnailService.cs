using System.Security.Cryptography;
using System.Text;
using NektronMoments.Models;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace NektronMoments.Services;

public sealed class ThumbnailService
{
    private readonly SemaphoreSlim _workers = new(8);
    private const long CacheLimit = 512L * 1024 * 1024;
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
    public async Task<byte[]?> LoadAsync(MediaItem item, uint size, CancellationToken cancellation)
    {
        await _workers.WaitAsync(cancellation);
        try
        {
            await _maintenance.WaitAsync(cancellation);
            return await Task.Run(async () => {
                foreach (var path in item.Paths.Prepend(item.Path).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    cancellation.ThrowIfCancellationRequested();
                    try
                    {
                        var info = new FileInfo(path);
                        if (!info.Exists) continue;
                        var identity = path + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks + "|" + size;
                        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
                        var cached = System.IO.Path.Combine(_cache, name + ".thumb");
                        if (File.Exists(cached)) return await File.ReadAllBytesAsync(cached, cancellation);
                        var file = await StorageFile.GetFileFromPathAsync(path);
                        using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, size);
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
        }
        finally { _workers.Release(); }
    }
    private long Prune()
    {
        try
        {
            var files = new DirectoryInfo(_cache).EnumerateFiles("*.thumb").OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
            long retained = 0;
            foreach (var file in files) {
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
