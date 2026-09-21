using System.Text.Json;
using NektronMoments.Models;
using NektronMoments.Services;

namespace NektronMoments;

public sealed partial class MainPage
{
    private async Task VerifyRequestResponsivenessAsync(string output, string path, Action<bool, string> check)
    {
        var overview = _overview; var store = _orderStore; var sort = _sortChoice;
        var calls = 0; var canceled = 0; var overviews = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try {
            _orderStore = new(Path.Combine(output, "request-arrangements"));
            _overview = new() { Total = 2, Photos = 2, LibraryId = "request-fixture" };
            _catalogLoaderForVerification = async (request, token) => {
                var call = ++calls;
                if (call == 1) {
                    started.TrySetResult();
                    try { await Task.Delay(5000, token); }
                    catch (OperationCanceledException) { ++canceled; throw; }
                }
                return await FixtureAsync(call, token);
            };
            _sortChoice = "newest"; var older = LoadCatalogAsync(); await started.Task;
            _sortChoice = "oldest"; var latest = LoadCatalogAsync();
            await Task.WhenAll(older, latest).WaitAsync(TimeSpan.FromSeconds(4));
            check(canceled == 1 && calls == 2 && Items[0].Key == "request-2-0", "A newer catalog request cancels obsolete loading and is the only result published");
            check(_catalogLoads == 0 && _catalogRequest is null, "Cancelled catalog requests release their gate and request ownership");
            _overviewLoaderForVerification = async _ => { ++overviews; await Task.Delay(80); return new() { Total = 2, Photos = 2, LibraryId = "request-fixture" }; };
            await Task.WhenAll(ReloadAsync(true, false), ReloadAsync(true, false), ReloadAsync(true, false));
            check(overviews == 1 && calls == 3, "Repeated refresh requests share one overview and catalog operation");
            await LoadCatalogAsync();
            check(calls == 3, "An unchanged library view reuses its bounded compact snapshot without another bridge transfer");
            await ReloadAsync(true, false);
            check(calls == 4 && overviews == 2, "Explicit refresh invalidates cached library views");
            await Task.WhenAll(Viewer.OpenAsync(0), Viewer.MoveAsync(1));
            check(Viewer.CurrentIndex == 1 && Viewer.CurrentItemKey == "request-4-1", "Rapid viewer navigation advances the requested index instead of losing the next command");
            Viewer.Close(); LibraryCanvas.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            var videoPath = Path.Combine(output, "generated-silent.mp4");
            await File.WriteAllBytesAsync(videoPath, VerificationMedia.SilentVideo());
            Items.ReplaceAll([new MediaItem { Key = "video-fixture", Name = "Generated silent video", Path = videoPath, Paths = [videoPath], MediaType = "Video" }]);
            await Viewer.OpenAsync(0);
            check(Viewer.HasVideoSource && Viewer.UnavailableMessage is null, "Generated video opens with an owned media source");
            Viewer.PlayVideoForVerification();
            var videoWait = DateTime.UtcNow.AddSeconds(5);
            while (!Viewer.VideoIsPlaying && DateTime.UtcNow < videoWait) await Task.Delay(25);
            check(Viewer.VideoIsPlaying, "Native playback starts for the silent generated video");
            Viewer.Close();
            check(!Viewer.HasVideoSource, "Closing video releases both player and media source ownership");
            var until = DateTime.UtcNow.AddSeconds(8);
            while (_sizingRetirement?.Pending > 0 && DateTime.UtcNow < until) await Task.Delay(25);
            check((_sizingRetirement?.Pending ?? 0) == 0 && (_sizingRetirement?.Failures ?? 0) == 0 && _sizingOwned.Count == 0,
                "Detached resize resources retire fully without disposal errors or retained ownership");
        } finally {
            _catalogLoaderForVerification = null; _overviewLoaderForVerification = null;
            _overview = overview; _orderStore = store; _sortChoice = sort;
            _catalogSnapshots.Clear(); ++_catalogCacheEpoch;
        }
        async Task<CompactCatalog> FixtureAsync(int call, CancellationToken token) {
            string[] frames = [
                "{\"ok\":true,\"catalogStart\":{\"version\":1,\"total\":2}}",
                Row(0), Row(1), "{\"ok\":true,\"catalogEnd\":{\"count\":2}}"
            ];
            var index = 0;
            var result = await Task.Run(() => CompactCatalog.ReadStreamAsync(_ => Task.FromResult<string?>(index < frames.Length ? frames[index++] : null), token), token);
            await result.PreparePrefixAsync(2, token); return result;
            string Row(int number) => JsonSerializer.Serialize(new { ok = true, item = new {
                key = $"request-{call}-{number}", hash = "", name = "Generated.png", path, paths = new[] { path }, source = "Generated",
                occurrences = 1, captured = "2026-09-20", dateSource = "", mediaType = "Photo", byteSize = (long?)null, modifiedNs = (long?)null
            } });
        }
    }
}
