using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;

namespace HarmonyStartupOptimizer;

internal sealed class ReferenceIndex<T, TKey> : IDisposable where T : class where TKey : notnull
{
    private readonly IList<T> source;
    private readonly INotifyCollectionChanged notifications;
    private readonly Func<T, TKey> key;
    private readonly Dictionary<TKey, T> first = new Dictionary<TKey, T>();
    private readonly HashSet<T> members = new HashSet<T>(ReferenceComparer<T>.Instance);
    private readonly NotifyCollectionChangedEventHandler changed;
    private int indexed;
    private bool dirty;

    internal ReferenceIndex(IList<T> source, INotifyCollectionChanged notifications, Func<T, TKey> key)
    {
        this.source = source; this.notifications = notifications; this.key = key;
        var weak = new WeakReference<ReferenceIndex<T, TKey>>(this);
        NotifyCollectionChangedEventHandler? handler = null;
        handler = (sender, args) =>
        {
            if (weak.TryGetTarget(out ReferenceIndex<T, TKey>? index))
            {
                // Appended items are indexed incrementally on the next query.
                if (args.Action != NotifyCollectionChangedAction.Add || args.NewStartingIndex < index.indexed) index.dirty = true;
            }
            else if (sender is INotifyCollectionChanged events) events.CollectionChanged -= handler;
        };
        changed = handler;
        notifications.CollectionChanged += changed;
    }

    internal T? Find(TKey wanted)
    {
        if (dirty)
        { first.Clear(); members.Clear(); indexed = 0; dirty = false; }
        while (indexed < source.Count)
        {
            T item = source[indexed++];
            TKey itemKey = key(item);
            if (!first.ContainsKey(itemKey)) first.Add(itemKey, item);
            members.Add(item);
        }
        return first.TryGetValue(wanted, out T? value) ? value : null;
    }

    internal void InvalidateItem(object item) { if (item is T value && members.Contains(value)) dirty = true; }

    public void Dispose()
    {
        notifications.CollectionChanged -= changed;
        first.Clear(); members.Clear(); indexed = 0;
    }
}

internal sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
{
    internal static readonly ReferenceComparer<T> Instance = new ReferenceComparer<T>();
    public bool Equals(T? x, T? y) => ReferenceEquals(x, y);
    public int GetHashCode(T value) => RuntimeHelpers.GetHashCode(value);
}
