namespace NektronMoments.Models;

public enum StartupProcessingMode { None, FileMetadata, Full }

public static class StartupProcessing
{
    public static string[] Arguments(StartupProcessingMode mode, string sourceId, AiProcessingOptions options)
    {
        if (mode != StartupProcessingMode.Full) return Arguments(mode, sourceId);
        options.Validate();
        return ["sync", sourceId, "--with-enrichment", "--enrichment-limit",
            options.Limit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--description-model", options.Model, "--no-input"];
    }
    public static string[] Arguments(StartupProcessingMode mode, string sourceId) => mode switch {
        StartupProcessingMode.Full => ["sync", sourceId, "--with-enrichment", "--no-input"],
        StartupProcessingMode.FileMetadata => ["sync", sourceId, "--no-input"],
        _ => throw new InvalidOperationException("Off does not start a processing job."),
    };
    public static StartupProcessingMode Resolve(string? saved, bool legacyEnabled = true) => saved switch {
        "Full" => StartupProcessingMode.Full,
        "FileMetadata" => StartupProcessingMode.FileMetadata,
        "None" => StartupProcessingMode.None,
        _ => legacyEnabled ? StartupProcessingMode.FileMetadata : StartupProcessingMode.None,
    };
    public static string Label(StartupProcessingMode mode) => mode switch {
        StartupProcessingMode.Full => "Full — metadata + AI",
        StartupProcessingMode.None => "Off",
        _ => "File metadata only",
    };
}
