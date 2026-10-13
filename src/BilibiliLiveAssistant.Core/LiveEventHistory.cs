using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace BilibiliLiveAssistant.Core;

/// <summary>Newest-first bounded history, with one notification per incoming batch.</summary>
public sealed class LiveEventHistory(int capacity = 200) : ObservableCollection<LiveEvent>
{
    private readonly int _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));

    public void PrependBatch(IReadOnlyList<LiveEvent> batch)
    {
        if (batch.Count == 0) return;
        var newest = new List<LiveEvent>(_capacity);
        for (var index = batch.Count - 1; index >= 0 && newest.Count < _capacity; index--)
            newest.Add(batch[index]);
        for (var index = 0; index < Count && newest.Count < _capacity; index++)
            newest.Add(this[index]);
        CheckReentrancy();
        Items.Clear();
        foreach (var item in newest) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
