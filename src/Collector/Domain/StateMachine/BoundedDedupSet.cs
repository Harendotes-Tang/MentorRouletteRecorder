namespace MentorRecorder.Collector.Domain.StateMachine;

/// <summary>
/// Fixed-capacity set with first-in, first-out eviction.
///
/// Used by the state machine to ignore duplicated observations without ever growing without
/// bound: a long capture session must not turn deduplication into a memory leak
/// (docs/architecture.md section 4, rule 2 applies the same principle to the capture queue).
/// </summary>
public sealed class BoundedDedupSet
{
    private readonly HashSet<string> _set;
    private readonly Queue<string> _order;

    /// <summary>Creates a set holding at most <paramref name="capacity"/> keys.</summary>
    /// <param name="capacity">Maximum number of retained keys; must be positive.</param>
    public BoundedDedupSet(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        _set = new HashSet<string>(StringComparer.Ordinal);
        _order = new Queue<string>(Math.Min(capacity, 1024));
    }

    /// <summary>Maximum number of retained keys.</summary>
    public int Capacity { get; }

    /// <summary>Number of currently retained keys; never exceeds <see cref="Capacity"/>.</summary>
    public int Count => _set.Count;

    /// <summary>True when the key was seen and is still retained.</summary>
    /// <param name="key">Deduplication key.</param>
    public bool Contains(string key) => _set.Contains(key);

    /// <summary>
    /// Records the key. Returns true when it was new, false when it was a duplicate.
    /// When the set is full the oldest key is evicted first.
    /// </summary>
    /// <param name="key">Deduplication key.</param>
    public bool Add(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (!_set.Add(key))
        {
            return false;
        }

        _order.Enqueue(key);
        while (_order.Count > Capacity)
        {
            _set.Remove(_order.Dequeue());
        }

        return true;
    }

    /// <summary>Forgets everything.</summary>
    public void Clear()
    {
        _set.Clear();
        _order.Clear();
    }

    /// <summary>Copies the retained keys in FIFO order so a caller can restore them later.</summary>
    public string[] Snapshot() => _order.ToArray();

    /// <summary>Replaces the retained keys with a previously captured FIFO snapshot.</summary>
    /// <param name="keys">Keys to restore, oldest first.</param>
    public void Restore(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        Clear();
        foreach (var key in keys)
        {
            Add(key);
        }
    }
}
