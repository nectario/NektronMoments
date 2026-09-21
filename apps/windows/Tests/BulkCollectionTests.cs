using System.Collections.Specialized;
using System.ComponentModel;
using NektronMoments.Models;

internal static class BulkCollectionTests
{
    public static void Run(Action<bool, string> check)
    {
        var items = new BulkObservableCollection<int>();
        var changes = new List<NotifyCollectionChangedAction>();
        var properties = new List<string?>();
        var publishedCounts = new List<int>();
        items.CollectionChanged += (_, args) => { changes.Add(args.Action); publishedCounts.Add(items.Count); };
        ((INotifyPropertyChanged)items).PropertyChanged += (_, args) => properties.Add(args.PropertyName);
        items.ReplaceAll(Enumerable.Range(0, 144_135));
        check(items.Count == 144_135 && items[0] == 0 && items[^1] == 144_134,
            "Complete library keeps its ordered identities");
        check(changes.SequenceEqual(new[] { NotifyCollectionChangedAction.Reset }) && publishedCounts.SequenceEqual(new[] { 144_135 }),
            "144k metadata rows produce only one complete layout reset; no growing scrollbar extent");
        check(properties.SequenceEqual(new[] { "Count", "Item[]" }), "Count and indexer bindings update once");
        items.ReplaceAll(items);
        check(items.Count == 144_135 && items[19] == 19, "Replacing from the same collection preserves every item");
        static IEnumerable<int> Broken() { yield return 7; throw new InvalidOperationException("fixture"); }
        var eventsBefore = changes.Count;
        try { items.ReplaceAll(Broken()); check(false, "Failed enumeration must throw"); }
        catch (InvalidOperationException) { }
        check(items.Count == 144_135 && items[0] == 0 && changes.Count == eventsBefore,
            "Failed catalog enumeration leaves current gallery and notifications unchanged");
        items.ReplaceAll(Array.Empty<int>());
        check(items.Count == 0 && changes[^1] == NotifyCollectionChangedAction.Reset, "Empty search resets the timeline");
        items.Add(42);
        check(items.Single() == 42 && changes[^1] == NotifyCollectionChangedAction.Add,
            "Normal ObservableCollection mutations remain compatible with fixtures and existing code");
    }
}
