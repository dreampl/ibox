using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace JackiVpn.Mobile.ViewModels;

public class BulkObservableCollection<T> : ObservableCollection<T>
{
    public BulkObservableCollection() { }
    public BulkObservableCollection(IEnumerable<T> items) : base(items) { }

    public void ReplaceAll(IEnumerable<T> items)
    {
        var snapshot = items.ToArray();
        if (this.SequenceEqual(snapshot)) { return; }
        Items.Clear();
        foreach (var item in snapshot) { Items.Add(item); }
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
