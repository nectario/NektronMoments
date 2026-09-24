using System.Globalization;
using System.Text.RegularExpressions;

namespace NektronMoments.Models;

public sealed class ProcessingOperation(int number, string name, string state, string phase, long? completed, long? total)
{
    public int Number { get; } = number;
    public string Name { get; } = name;
    public string State { get; } = state;
    public string Phase { get; } = phase;
    public long? Completed { get; } = completed;
    public long? Total { get; } = total;
    public string NumberText => Number.ToString(CultureInfo.InvariantCulture);
    public string CountText => Total > 0 && Completed.HasValue ? $"{Completed:N0} / {Total:N0}" : "—";
}
public sealed class ProcessingLogEntry(long sequence, DateTimeOffset time, string stage, string message)
{
    public long Sequence { get; } = sequence;
    public DateTimeOffset Time { get; } = time;
    public string Stage { get; } = stage;
    public string Message { get; } = message;
    public string TimeText => Time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}

public sealed record ProcessingSnapshot(string State, string Phase, string Message, string Source,
    int SourceNumber, int SourceCount, long? Completed, long? Total, DateTimeOffset Started,
    DateTimeOffset? Ended)
{
    public ProcessingOperation[] Operations { get; init; } = [];
    public long LogSequence { get; init; }
    public double? ItemsPerSecond { get; init; }
    public bool EnrichmentEnabled { get; init; }
    public int CompletedSources => Operations.Count(operation => operation.State == "Complete");
    public double? OverallPercent => SourceCount > 0 ? 100d * CompletedSources / SourceCount : State == "Complete" ? 100 : null;
    public TimeSpan? PhaseRemaining => Running && ItemsPerSecond is > 0 && Total > 0 && Completed.HasValue
        ? TimeSpan.FromSeconds(Math.Clamp((Total.Value - Completed.Value) / ItemsPerSecond.Value, 0, 31536000)) : null;
    public bool Running => State is "Running" or "Stopping";
    public double? Percent => Total > 0 && Completed.HasValue ? Math.Clamp(100d * Completed.Value / Total.Value, 0, 100) : null;
    public TimeSpan Elapsed => (Ended ?? DateTimeOffset.UtcNow) - Started;
}

/// <summary>Worker-written progress; the UI polls one immutable snapshot instead of queuing every log line.</summary>
public sealed class MetadataProgress
{
    private static readonly Regex Counts = new(@"(?<done>[\d,]+)\s*/\s*(?<total>[\d,]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Ansi = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly Regex Rate = new(@"(?<rate>[\d,.]+)\s+(?:files|rows|items)/s", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly object _gate = new();
    private bool _byok;
    private string? _pauseMessage;
    private bool _hasPhotoFailures;
    private readonly Queue<ProcessingLogEntry> _log = new();
    private ProcessingSnapshot _current = new("Running", "Starting", "Preparing metadata processing…", "", 0, 0, null, null, DateTimeOffset.UtcNow, null);
    public ProcessingSnapshot Snapshot { get { lock (_gate) return _current; } }
    public ProcessingLogEntry[] LogAfter(long sequence, int maximum = 500) {
        lock (_gate) return _log.Where(entry => entry.Sequence > sequence).TakeLast(Math.Clamp(maximum, 1, 1000)).ToArray();
    }
    public void ConfigureSources(IReadOnlyList<string> names, bool withEnrichment = false) {
        lock (_gate) {
            _current = _current with { SourceCount = names.Count, EnrichmentEnabled = withEnrichment,
                Operations = names.Select((name, index) => new ProcessingOperation(index + 1, name, "Waiting", "Not started", null, null)).ToArray() };
            AddLog("Session", withEnrichment
                ? "Full processing started: file metadata plus bounded AI/address enrichment, including older pending photos. API charges may apply; quotas remain in effect."
                : "Metadata processing started. Originals stay in place; no paid enrichment is enabled.");
        }
    }
    private void AddLog(string stage, string message) {
        var sequence = _current.LogSequence + 1;
        _log.Enqueue(new(sequence, DateTimeOffset.UtcNow, stage, message));
        while (_log.Count > 1000) _log.Dequeue();
        _current = _current with { LogSequence = sequence };
    }
    private void UpdateOperation(string state) {
        if (_current.SourceNumber < 1) return;
        var rows = _current.Operations.ToList();
        while (rows.Count < _current.SourceCount) rows.Add(new(rows.Count + 1, "Source " + (rows.Count + 1), "Waiting", "Not started", null, null));
        if (_current.SourceNumber <= rows.Count) rows[_current.SourceNumber - 1] = new(_current.SourceNumber, _current.Source, state, _current.Phase, _current.Completed, _current.Total);
        _current = _current with { Operations = rows.ToArray() };
    }
    public void CompleteSource() {
        lock (_gate) { UpdateOperation("Complete"); AddLog("Source complete", _current.Source + " · Metadata pass finished"); }
    }
    public void Source(string name, int number, int count) {
        lock (_gate) {
            _current = _current with { Source = name, SourceNumber = number, SourceCount = count, Phase = "Starting", Message = "Preparing " + name, Completed = null, Total = null, ItemsPerSecond = null };
            UpdateOperation("Running"); AddLog("Starting", "Preparing " + name);
        }
    }
    public void Report(string line)
    {
        line = Ansi.Replace(line, "").Trim();
        if (line.Length == 0) return;
        var phase = line.StartsWith("Discovering media") || line.StartsWith("Scanning ") ? "Discovering photos and videos" :
            line.StartsWith("Reading file metadata") ? "Reading file metadata" :
            line.StartsWith("Processing media") ? "Extracting metadata and hashing" :
            line.StartsWith("Bulk metadata") ? "Updating indexed metadata" :
            line.StartsWith("Accepted manifest") || line.StartsWith("Resuming ") || line.StartsWith("Sending ") ? "Saving metadata" :
            line.StartsWith("BYOK ") ? "Direct AI analysis (your key)" :
            line.StartsWith("Preparing explicit enrichment") || line.StartsWith("Explicit enrichment prepared") || line.StartsWith("Enrichment catch-up") ? "Preparing AI and address enrichment" :
            line.StartsWith("Preparing ") && line.Contains("scene preview") || line.StartsWith("Staged scene preview") || line.StartsWith("Scene preview") || line.StartsWith("Scene description") ? "Preparing scene descriptions" : null;
        if (phase is null) return; // Rich summary borders and unrelated output are not progress.
        var match = Counts.Match(line);
        long? done = null, total = null;
        double? rate = null;
        var rateMatch = Rate.Match(line);
        if (rateMatch.Success && double.TryParse(rateMatch.Groups["rate"].Value, NumberStyles.AllowThousands | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var speed) && double.IsFinite(speed) && speed > 0) rate = speed;
        if (match.Success && long.TryParse(match.Groups["done"].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var d) &&
            long.TryParse(match.Groups["total"].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var t) && t > 0) {
            done = Math.Clamp(d, 0, t); total = t;
        }
        lock (_gate) {
            if (_current.State != "Running") return;
            if (line.StartsWith("BYOK ")) _byok = true;
            if (line.StartsWith("BYOK failed photo") || line.StartsWith("BYOK completed with failures")) _hasPhotoFailures = true;
            if (line.StartsWith("BYOK paused")) _pauseMessage = PauseExplanation(line);
            if (_byok && phase == _current.Phase && done is null) { done = _current.Completed; total = _current.Total; }
            if (_byok && phase == _current.Phase && rate is null) rate = _current.ItemsPerSecond;
            _current = _current with { Phase = phase, Message = line, Completed = done, Total = total, ItemsPerSecond = rate };
            UpdateOperation("Running"); AddLog(phase, line);
        }
    }
    public void Refreshing() { lock (_gate) { _current = _current with { Phase = "Refreshing your library", Message = "Loading the updated metadata…", Completed = null, Total = null, ItemsPerSecond = null }; AddLog("Refresh", _current.Message); } }
    public void StopRequested() { lock (_gate) if (_current.Running) { _current = _current with { State = "Stopping", Message = "Stopping safely · Keeping completed work…" }; UpdateOperation("Stopping"); AddLog("Stopping", _current.Message); } }
    public void Finish(bool stopped, bool failed) {
        lock (_gate) {
            var paused = failed && !stopped && _pauseMessage is not null;
            var state = paused ? "Paused" : failed ? "Failed" : stopped ? "Stopped" : "Complete";
            if (_current.SourceNumber > 0 && _current.Operations.ElementAtOrDefault(_current.SourceNumber - 1)?.State != "Complete") UpdateOperation(state);
            _current = _current with { State = state,
            Phase = paused ? "AI processing paused" : failed ? "Needs attention" : stopped ? "Stopped safely" : _hasPhotoFailures ? "Complete with photo failures" : _current.EnrichmentEnabled ? "Processing pass complete" : "Metadata pass complete",
            Message = paused ? _pauseMessage! : failed ? "The operation could not finish. Saved progress is retained; retry when ready." : stopped ? "Completed work is saved. Queued server-side jobs may continue; unfinished local work remains resumable." :
                _hasPhotoFailures ? "Pass complete. Individual photo failures are saved in Saved queues > Failed photos. Successful descriptions are retained; failed or uncertain calls are not automatically billed again." :
                _byok ? "BYOK pass complete. Finished descriptions are saved locally and synchronized. Remaining photos can be resumed; separate address jobs may still be queued." :
                _current.EnrichmentEnabled ? "Library refreshed. Requested AI/address jobs may still be queued, processing or quota-deferred. Older pending photos remain eligible for later bounded passes." :
                "Library refreshed. Saved or server-side work may still appear in the processing queues. No paid enrichment was started.",
            Completed = failed || stopped ? _current.Completed : 1, Total = failed || stopped ? _current.Total : 1, Ended = DateTimeOffset.UtcNow, ItemsPerSecond = null };
            AddLog(_current.State, _current.Message);
        }
    }
    private static string PauseExplanation(string line) => line.Contains("OpenAICreditsExhausted")
        ? "OpenAI API credits are exhausted. Add credits in OpenAI billing, then start processing again. Completed descriptions are saved; unprocessed photos remain queued. This is an account issue, not a failed photo."
        : line.Contains("LOCAL_MONTHLY_LIMIT") ? "Your local monthly AI spending limit was reached. Completed work is saved. Review your spending limit before resuming."
        : line.Contains("OpenAIQuotaDeferred") ? "OpenAI quota or spending limit reached. Review your OpenAI billing and limits before resuming. Completed work is saved."
        : line.Contains("OpenAIAuthenticationFailed") || line.Contains("OpenAICredentialUnavailable") ? "OpenAI rejected or could not load your API credential. Check your key and access before resuming. Completed work is saved."
        : line.Contains("OpenAIRateLimited") ? "OpenAI temporarily rate limited requests. Wait before resuming, or reduce concurrency. Completed work is saved. " + line
        : line + ". Completed work is saved; resolve this shared provider issue before resuming.";
}
