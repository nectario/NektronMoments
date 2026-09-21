using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace NektronMoments.Models;

/// <summary>Publish a complete catalog with one layout reset, not one per photo.</summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        CheckReentrancy();
        // Materialize before mutation: self-replacement is safe, and a failed
        // enumeration leaves the existing, usable library untouched.
        var snapshot = values.ToArray();
        Items.Clear();
        foreach (var value in snapshot) Items.Add(value);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
