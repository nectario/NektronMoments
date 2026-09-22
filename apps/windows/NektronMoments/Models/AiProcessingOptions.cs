using System.Globalization;
using System.Text.Json;

namespace NektronMoments.Models;

public sealed record SceneModelOption(string Id, string Label, decimal InputRate, decimal OutputRate);

public sealed record AiProcessingOptions(int Limit = 64, string Model = "gpt-5.6-terra")
{
    // Technical per-run guard only. Future subscription entitlements belong
    // on the server and must not be inferred from the 64-asset request size.
    public const int MaximumRunLimit = 1_000_000;
    public const int CatchUpLimit = 10_000;
    public static string RateSummary => "Standard rates per 1M tokens · " + string.Join("  |  ", Models.Select(model =>
        $"{model.Label}: ${model.InputRate} input / ${model.OutputRate} output")) + " · Verified 2026-09-21";
    // Standard USD / million tokens, verified 2026-09-21. Flex can cost less.
    public static readonly SceneModelOption[] Models = [
        new("gpt-5.6-terra", "Terra — default", 2m, 12m),
        new("gpt-5.6-luna", "Luna", .2m, 1.2m),
        new("gpt-5.6-sol", "Sol", 4m, 20m),
    ];
    // Comparison-only entries must not silently enter the execution allowlist.
    // Astra standard short-context rates verified 2026-09-22:
    // https://developers.openai.com/api/docs/models/gpt-6-astra
    public static readonly SceneModelOption[] PricingModels = [..Models,
        new("gpt-6-astra", "Astra — pricing only", 10m, 50m),
    ];
    public void Validate()
    {
        if (Limit is < 1 or > MaximumRunLimit || !Models.Any(item => item.Id == Model))
            throw new ArgumentException("Choose 1–1,000,000 assets and a supported AI model.");
    }
    public static AiProcessingOptions Resolve(string? saved)
    {
        try {
            var result = saved is null ? new() : JsonSerializer.Deserialize<AiProcessingOptions>(saved) ?? new();
            result.Validate(); return result;
        } catch (Exception error) when (error is JsonException or ArgumentException) { return new(); }
    }
    public decimal Estimate(int sources = 1)
    {
        Validate();
        if (sources < 0) throw new ArgumentOutOfRangeException(nameof(sources));
        var model = Models.Single(item => item.Id == Model);
        return Limit * (decimal)sources * (2560 * model.InputRate + 100 * model.OutputRate) / 1_000_000m;
    }
    public string EstimateSummary() => $"US${Estimate().ToString(Estimate() < .01m ? "F4" : "F2", CultureInfo.InvariantCulture)} estimated per source\n{Model} · Up to {Limit:N0} photos";
    public string Preview(int sources = 1) => $"{Model} · Up to {Limit:N0} assets per source\n" +
        $"Illustrative AI cost for {Limit * (long)sources:N0} descriptions: US${Estimate(sources).ToString("F3", CultureInfo.InvariantCulture)}. " +
        "Assumes 2,560 input + 100 output tokens per image at standard rates (2026-09-21). " +
        "Not a quote or spending cap: actual tokens, retries and pending jobs vary; Flex/cache reuse can cost less. Excludes address lookup and AWS costs.";
}
