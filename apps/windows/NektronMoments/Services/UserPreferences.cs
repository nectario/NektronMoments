using Microsoft.Win32;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NektronMoments.Services;

/// <summary>Non-secret preferences that also work without MSIX package identity.</summary>
public static class UserPreferences
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NektronMoments");
    private static readonly string SettingsPath = Path.Combine(DirectoryPath, "preferences.json");

    public static string Theme
    {
        get
        {
            try {
                if (File.Exists(SettingsPath)) {
                    using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                    if (document.RootElement.TryGetProperty("theme", out var theme))
                        return theme.GetString() == "Dark" ? "Dark" : "Light";
                }
            } catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
            // Keep the original packaged-preview preference when it is available.
            try { return Windows.Storage.ApplicationData.Current.LocalSettings.Values["Theme"] as string == "Dark" ? "Dark" : "Light"; }
            catch (Exception) { return "Light"; } // An unpackaged install has no ApplicationData.Current.
        }
        set
        {
            Directory.CreateDirectory(DirectoryPath);
            var temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                var preferences = Read();
                preferences["theme"] = value == "Dark" ? "Dark" : "Light";
                File.WriteAllText(temporary, preferences.ToJsonString());
                File.Move(temporary, SettingsPath, overwrite: true);
            } finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private static JsonObject Read()
    {
        try { return JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject ?? new(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public static double Number(string key, double fallback)
    {
        try { return Read()[key]?.GetValue<double>() is { } value && double.IsFinite(value) ? value : fallback; }
        catch (InvalidOperationException) { return fallback; }
    }
    public static bool Flag(string key, bool fallback = false)
    {
        try { return Read()[key]?.GetValue<bool>() ?? fallback; }
        catch (InvalidOperationException) { return fallback; }
    }
    public static void Set<T>(string key, T value)
    {
        var preferences = Read();
        preferences[key] = JsonSerializer.SerializeToNode(value);
        Directory.CreateDirectory(DirectoryPath);
        var temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, preferences.ToJsonString()); File.Move(temporary, SettingsPath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static string? InstalledWorkspace
    {
        get
        {
            try {
                return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Nektron\NektronMoments", "Workspace", null) as string;
            } catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return null; }
        }
    }
}
