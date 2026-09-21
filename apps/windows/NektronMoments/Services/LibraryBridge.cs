using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace NektronMoments.Services;

/// <summary>Development adapter: credentials stay in the existing Ubuntu CLI session.</summary>
public sealed class LibraryBridge : IDisposable
{
    private Process? _process;
    private ProtocolLines? _lines;
    private readonly object _processGate = new();
    private bool _disposed;
    private readonly SemaphoreSlim _requests = new(1);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    public string Workspace { get; }
    private readonly bool _indexOnly;
    public LibraryBridge(bool indexOnly = false)
    {
        _indexOnly = indexOnly;
        Workspace = Environment.GetEnvironmentVariable("NEKTRON_MOMENTS_WORKSPACE") ?? FindWorkspace();
    }
    private static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "pyproject.toml")) &&
                Directory.Exists(System.IO.Path.Combine(directory.FullName, "cli")))
                return directory.FullName;
        var installedWorkspace = UserPreferences.InstalledWorkspace;
        return !string.IsNullOrWhiteSpace(installedWorkspace) &&
            File.Exists(System.IO.Path.Combine(installedWorkspace, "cli/nektron_moments_cli/desktop_bridge.py"))
                ? installedWorkspace : "";
    }
    public static string ToWslPath(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        if (full.Length < 3 || full[1] != ':')
            throw new InvalidOperationException("Choose a folder on a Windows drive for this preview.");
        return "/mnt/" + char.ToLowerInvariant(full[0]) + full[2..].Replace('\\', '/');
    }
    public ProcessStartInfo StartInfo(params string[] arguments)
    {
        if (string.IsNullOrWhiteSpace(Workspace))
            throw new InvalidOperationException("Your library workspace was not found. Rerun the Moments installer to reconnect it, or launch with scripts/windows.ps1.");
        var info = new ProcessStartInfo("wsl.exe") {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "-d", "Ubuntu", "--cd", ToWslPath(Workspace), "--exec", ".venv/bin/python", "-B", "-u" }.Concat(arguments))
            info.ArgumentList.Add(arg);
        return info;
    }
    private (Process Process, ProtocolLines Lines) Connection()
    {
        lock (_processGate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_process is { HasExited: false }) return (_process, _lines!);
            _process?.Dispose();
            _process = null; _lines = null;
            _process = Process.Start(StartInfo(_indexOnly ? ["-m", "cli.nektron_moments_cli.desktop_bridge", "--index-only"] : ["-m", "cli.nektron_moments_cli.desktop_bridge"]))
                ?? throw new InvalidOperationException("Could not start the Ubuntu library connection.");
            _lines = new ProtocolLines(_process.StandardOutput);
            // Drain diagnostics to avoid a blocked pipe; never expose raw SDK/credential errors in the UI.
            _ = _process.StandardError.ReadToEndAsync();
            return (_process, _lines);
        }
    }
    public Task<T> CallAsync<T>(object request, CancellationToken cancellation = default) =>
        Task.Run(() => CallCoreAsync<T>(request, cancellation), cancellation);
    private async Task<T> CallCoreAsync<T>(object request, CancellationToken cancellation)
    {
        await _requests.WaitAsync(cancellation);
        Process? connection = null;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var (process, lines) = Connection(); connection = process;
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, Json));
            await process.StandardInput.FlushAsync();
            var line = await lines.ReadAsync(64 * 1024 * 1024, cancellation).WaitAsync(TimeSpan.FromSeconds(75), cancellation);
            if (line is null) throw new InvalidOperationException("The CLI library connection closed. Check Ubuntu and your saved CLI sign-in, then refresh.");
            return await Task.Run(() => {
                using var document = JsonDocument.Parse(line);
                if (!document.RootElement.GetProperty("ok").GetBoolean())
                    throw new InvalidOperationException(document.RootElement.GetProperty("error").GetString());
                cancellation.ThrowIfCancellationRequested();
                var result = document.RootElement.GetProperty("result").Deserialize<T>(Json)!;
                if (result is Models.MediaPage page) Models.CatalogMemory.Compact(page.Items);
                return result;
            }, cancellation);
        }
        catch (TimeoutException)
        {
            Stop(connection);
            throw new InvalidOperationException("The library connection took too long. Your files are safe; use Refresh to reconnect.");
        }
        catch { Stop(connection); throw; }
        finally { _requests.Release(); }
    }
    /// <summary>Stream and prepare the initial range entirely away from the XAML dispatcher.</summary>
    public Task<Models.CompactCatalog> LoadCatalogAsync(object request, CancellationToken cancellation = default) =>
        Task.Run(async () => {
            await _requests.WaitAsync(cancellation).ConfigureAwait(false);
            Process? connection = null;
            try {
                cancellation.ThrowIfCancellationRequested();
                var (process, lines) = Connection(); connection = process;
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, Json)).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(cancellation).ConfigureAwait(false);
                var catalog = await Models.CompactCatalog.ReadStreamAsync(
                    token => lines.ReadAsync(Models.CompactCatalog.ChunkBytes, token).WaitAsync(TimeSpan.FromSeconds(75), token),
                    cancellation).ConfigureAwait(false);
                await catalog.PreparePrefixAsync(Math.Min(200, catalog.Count), cancellation).ConfigureAwait(false);
                return catalog;
            }
            catch (TimeoutException) {
                Stop(connection);
                throw new InvalidOperationException("The library connection took too long. Your files are safe; use Refresh to reconnect.");
            }
            catch {
                // A cancelled/invalid stream leaves unread frames. Never let the next
                // request consume them as its response; refresh starts a clean child.
                Stop(connection); throw;
            }
            finally { _requests.Release(); }
        }, cancellation);
    public Task<string> RunCliAsync(string[] args, Action<string> progress, CancellationToken cancellation) =>
        Task.Run(() => RunCliCoreAsync(args, progress, cancellation), cancellation);
    private async Task<string> RunCliCoreAsync(string[] args, Action<string> progress, CancellationToken cancellation)
    {
        using var process = Process.Start(StartInfo(["-m", "cli.nektron_moments_cli.desktop_job", ..args]))
            ?? throw new InvalidOperationException("Could not start the CLI.");
        var errors = process.StandardError.ReadToEndAsync();
        using var registration = cancellation.Register(() => {
            try { process.StandardInput.WriteLine("cancel"); process.StandardInput.Flush(); } catch (Exception) { }
        });
        var output = new StringBuilder();
        try {
            while (await process.StandardOutput.ReadLineAsync(cancellation) is { } line) {
                if (output.Length < 65536) output.AppendLine(line);
                progress(line);
            }
            await process.WaitForExitAsync(cancellation);
        } catch (OperationCanceledException) {
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (TimeoutException) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
            throw;
        }
        await errors;
        if (process.ExitCode != 0)
            throw new InvalidOperationException("The CLI could not complete this operation. Saved sync progress is retained; check ./scripts/cli.sh status.");
        return output.ToString();
    }
    private void Stop(Process? expected = null, bool disposing = false)
    {
        Process? process;
        lock (_processGate) {
            if (disposing) _disposed = true;
            if (expected is not null && !ReferenceEquals(_process, expected)) return;
            process = _process; _process = null; _lines = null;
        }
        if (process is null) return;
        try { process.StandardInput.Close(); }
        catch (Exception error) when (error is InvalidOperationException or IOException) { }
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or IOException or System.ComponentModel.Win32Exception) { }
        finally { process.Dispose(); }
    }
    public void Dispose() => Stop(disposing: true);

    /// <summary>Bound each frame before constructing its string, preserving read-ahead between requests.</summary>
    private sealed class ProtocolLines(StreamReader reader)
    {
        private readonly char[] _buffer = new char[8192];
        private int _offset, _length;
        public async Task<string?> ReadAsync(int maximumLength, CancellationToken cancellation)
        {
            StringBuilder? collected = null;
            while (true) {
                if (_offset == _length) {
                    _length = await reader.ReadAsync(_buffer.AsMemory(), cancellation).ConfigureAwait(false); _offset = 0;
                    if (_length == 0) return collected?.ToString();
                }
                var newline = Array.IndexOf(_buffer, '\n', _offset, _length - _offset);
                var end = newline < 0 ? _length : newline;
                var length = end - _offset;
                if ((collected?.Length ?? 0) + length > maximumLength)
                    throw new InvalidDataException("A library response exceeded the supported frame size.");
                if (newline >= 0 && collected is null) {
                    var result = new string(_buffer, _offset, length > 0 && _buffer[end - 1] == '\r' ? length - 1 : length);
                    _offset = newline + 1; return result;
                }
                collected ??= new StringBuilder(Math.Min(maximumLength, _buffer.Length));
                collected.Append(_buffer, _offset, length); _offset = end;
                if (newline >= 0) {
                    _offset++; if (collected.Length > 0 && collected[^1] == '\r') collected.Length--;
                    return collected.ToString();
                }
            }
        }
    }
}
