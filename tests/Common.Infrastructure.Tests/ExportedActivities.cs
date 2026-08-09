using System.Collections;
using System.Diagnostics;

namespace Common.Infrastructure.Tests;

/// <summary>
/// A thread-safe sink for <c>AddInMemoryExporter</c>. The Redis
/// instrumentation exports from its flush timer while the test thread polls
/// <see cref="Count"/> and then enumerates, and <c>List&lt;T&gt;</c> does
/// not support a concurrent reader and writer — the flake would be rare and
/// unattributable, which is the worst kind.
/// </summary>
internal sealed class ExportedActivities : ICollection<Activity>
{
    private readonly List<Activity> _items = [];
    private readonly Lock _lock = new();

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _items.Count;
            }
        }
    }

    public bool IsReadOnly => false;

    public void Add(Activity item)
    {
        lock (_lock)
        {
            _items.Add(item);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _items.Clear();
        }
    }

    public bool Contains(Activity item)
    {
        lock (_lock)
        {
            return _items.Contains(item);
        }
    }

    public void CopyTo(Activity[] array, int arrayIndex)
    {
        lock (_lock)
        {
            _items.CopyTo(array, arrayIndex);
        }
    }

    public bool Remove(Activity item)
    {
        lock (_lock)
        {
            return _items.Remove(item);
        }
    }

    /// <summary>Enumerates a snapshot, so a flush mid-assertion cannot
    /// invalidate the enumerator.</summary>
    public IEnumerator<Activity> GetEnumerator()
    {
        lock (_lock)
        {
            Activity[] snapshot = [.. _items];
            return ((IEnumerable<Activity>)snapshot).GetEnumerator();
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
