namespace NektronMoments.Services;

public static class DiagnosticLog
{
    public static void Write(Exception error)
    {
        try {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NektronMoments");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "startup-error.txt"), error.ToString());
        } catch { }
    }
}
