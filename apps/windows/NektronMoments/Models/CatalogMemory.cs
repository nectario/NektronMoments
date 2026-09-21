namespace NektronMoments.Models;

/// <summary>Preserve values while reducing the live catalog graph scanned during GC.</summary>
public static class CatalogMemory
{
    public static void Compact(IEnumerable<MediaItem> items)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        string Label(string value) {
            if (value.Length == 0) return "";
            if (labels.TryGetValue(value, out var existing)) return existing;
            labels[value] = value; return value;
        }
        foreach (var item in items) {
            if (item.Hash == item.Key) item.Hash = item.Key;
            for (var index = 0; index < item.Paths.Length; index++)
                if (item.Paths[index] == item.Path) item.Paths[index] = item.Path;
            item.Source = Label(item.Source);
            item.DateSource = Label(item.DateSource);
            item.MediaType = Label(item.MediaType);
        }
    }
}
