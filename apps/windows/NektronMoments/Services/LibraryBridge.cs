using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace NektronMoments.Services;

/// <summary>Development adapter: credentials stay in the existing Ubuntu CLI session.</summary>
public sealed class LibraryBridge : IDisposable
{
    private Process? _process;
    private readonly SemaphoreSlim _requests = new(1);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    public string Workspace { get; }
    public LibraryBridge()
    {
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
    private void EnsureStarted()
    {
        if (_process is { HasExited: false }) return;
        _process?.Dispose();
        _process = Process.Start(StartInfo("-m", "cli.nektron_moments_cli.desktop_bridge"))
            ?? throw new InvalidOperationException("Could not start the Ubuntu library connection.");
        // Drain diagnostics to avoid a blocked pipe; never expose raw SDK/credential errors in the UI.
        _ = _process.StandardError.ReadToEndAsync();
    }
    public async Task<T> CallAsync<T>(object request, CancellationToken cancellation = default)
    {
        await _requests.WaitAsync(cancellation);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            EnsureStarted();
            await _process!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, Json));
            await _process.StandardInput.FlushAsync();
            var line = await _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(75));
            if (line is null) throw new InvalidOperationException("The CLI library connection closed. Check Ubuntu and your saved CLI sign-in, then refresh.");
            return await Task.Run(() => {
                using var document = JsonDocument.Parse(line);
                if (!document.RootElement.GetProperty("ok").GetBoolean())
                    throw new InvalidOperationException(document.RootElement.GetProperty("error").GetString());
                cancellation.ThrowIfCancellationRequested();
                return document.RootElement.GetProperty("result").Deserialize<T>(Json)!;
            }, cancellation);
        }
        catch (TimeoutException)
        {
            Stop();
            throw new InvalidOperationException("The library connection took too long. Your files are safe; use Refresh to reconnect.");
        }
        finally { _requests.Release(); }
    }
    public async Task<string> RunCliAsync(string[] args, Action<string> progress, CancellationToken cancellation)
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
    private void Stop()
    {
        if (_process is null) return;
        try { _process.StandardInput.Close(); if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        _process.Dispose();
        _process = null;
    }
    public void Dispose() => Stop();
}
