using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;

namespace NektronMoments.Models;

public sealed class CatalogCollection : IReadOnlyList<MediaItem>, INotifyCollectionChanged, INotifyPropertyChanged
{
    private CatalogView _view = CatalogView.FromItems([]);
    public int Count => _view.Count;
    public int PreparedPrefixCount => _view.PreparedPrefixCount;
    public int MaterializedCount => _view.MaterializedCount;
    public MediaItem this[int index] => _view[index];
    public event NotifyCollectionChangedEventHandler? CollectionChanged;
    public event PropertyChangedEventHandler? PropertyChanged;
    public CatalogView Capture() => _view;
    public bool TryGetReady(int index, out MediaItem? item) => _view.TryGetReady(index, out item);
    public Task PreparePrefixAsync(int count, CancellationToken cancellation = default) => _view.PreparePrefixAsync(count, cancellation);
    public int IndexOf(MediaItem item) => _view.IndexOf(item);
    public void ReplaceSnapshot(CompactCatalog snapshot) => ReplaceView(CatalogView.FromCompact(snapshot));
    public void ReplaceView(CatalogView view, bool notify = true)
    {
        if (view.PreparedPrefixCount < Math.Min(200, view.Count))
            throw new InvalidOperationException("Prepare the first browsing range before publishing its catalog.");
        _view = view;
        if (notify) Reset();
    }
    public void ReplaceAll(IEnumerable<MediaItem> items) { _view = CatalogView.FromItems(items); Reset(); }
    public void Clear() => ReplaceAll([]);
    public void Add(MediaItem item)
    {
        if (_view.IsCompact) throw new InvalidOperationException("Replace the catalog snapshot to add indexed items.");
        _view = CatalogView.FromItems(_view.Append(item)); PropertiesChanged();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, item, Count - 1));
    }
    private void Reset() { PropertiesChanged(); CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset)); }
    private void PropertiesChanged() { PropertyChanged?.Invoke(this, new(nameof(Count))); PropertyChanged?.Invoke(this, new("Item[]")); }
    public IEnumerator<MediaItem> GetEnumerator() => _view.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
