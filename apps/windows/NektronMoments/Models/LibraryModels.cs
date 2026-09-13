using System.Text.Json;
using System.Text.Json.Serialization;

namespace NektronMoments.Models;

public sealed class MediaItem
{
    public string Key { get; set; } = "";
    public string Hash { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public List<string> Paths { get; set; } = [];
    public string Source { get; set; } = "";
    public int Occurrences { get; set; }
    public string Captured { get; set; } = "";
    public string DateSource { get; set; } = "";
    public string MediaType { get; set; } = "";
    public long? ByteSize { get; set; }
    public long? ModifiedNs { get; set; }
    public JsonElement Metadata { get; set; }
    public string Description { get; set; } = "";
    public string Address { get; set; } = "";
    public string AssetId { get; set; } = "";
    [JsonIgnore] public string DateLabel => DateTime.TryParse(Captured, out var date) ? date.ToString("dd MMM yyyy") : "Date pending";
    [JsonIgnore] public string Subtitle => MediaType + " · " + DateLabel;
    [JsonIgnore] public string AccessibleName => Name + ", " + Subtitle;
}

public sealed class LibrarySource
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}
public sealed class LibraryOverview
{
    public int Total { get; set; }
    public int Photos { get; set; }
    public int Videos { get; set; }
    public int Pending { get; set; }
    public List<LibrarySource> Sources { get; set; } = [];
}
public sealed class MediaPage
{
    public List<MediaItem> Items { get; set; } = [];
    public int Total { get; set; }
    public int NextOffset { get; set; }
    public bool HasMore { get; set; }
    public bool Bounded { get; set; }
    public string Notice { get; set; } = "";
}
public sealed class MediaDetail
{
    public MediaItem Item { get; set; } = new();
    public JsonElement? Remote { get; set; }
    public string Notice { get; set; } = "";
}
