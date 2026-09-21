using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NektronMoments.Services;

public sealed record LibraryArrangement(string Sort, string[] Keys, string[]? Paths = null, string BaseSort = "newest");

/// <summary>Non-secret presentation preferences; originals and metadata are never changed.</summary>
public sealed class LibraryOrderStore(string? directory = null)
{
    private readonly string _directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NektronMoments", "arrangements");
    private readonly SemaphoreSlim _writes = new(1);
    private string FileFor(string scope) => Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope))) + ".json");
    public Task<LibraryArrangement> LoadAsync(string scope, CancellationToken token = default) => Task.Run(() => {
        token.ThrowIfCancellationRequested();
        try {
            var state = JsonSerializer.Deserialize<LibraryArrangement>(File.ReadAllText(FileFor(scope)));
            return state is { Keys: not null, Sort: "custom" or "oldest" or "newest" } ? state : new("newest", []);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new LibraryArrangement("newest", []); }
    }, token);
    public async Task SaveAsync(string scope, string sort, string[]? keys = null, string[]? paths = null, string? baseSort = null)
    {
        await _writes.WaitAsync().ConfigureAwait(false);
        try {
            var current = keys is null ? await LoadAsync(scope).ConfigureAwait(false) : null;
            var snapshot = new LibraryArrangement(sort, keys ?? current!.Keys, keys is null ? current!.Paths : paths,
                baseSort ?? current?.BaseSort ?? "newest");
            await Task.Run(() => {
                Directory.CreateDirectory(_directory);
                var file = FileFor(scope); var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot)); File.Move(temporary, file, true); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }).ConfigureAwait(false);
        } finally { _writes.Release(); }
    }
}
