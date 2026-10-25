using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PDF_simple_edit.Helpers;

/// <summary>Small LRU cache. Evicted resources live until their last borrower returns.</summary>
internal sealed class PdfSnapshotCache<T>(Func<byte[], Task<T>> load, int capacity = 2) where T : IDisposable
{
    private sealed class Entry(byte[] snapshot, Task<T> value)
    {
        internal readonly byte[] Snapshot = snapshot;
        internal readonly Task<T> Value = value;
        internal int Users;
        internal bool Retired;
    }

    internal sealed class Lease(T value, Action release) : IDisposable
    {
        internal T Value { get; } = value;
        private Action? _release = release;
        public void Dispose() => System.Threading.Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    private readonly List<Entry> _entries = new();
    private readonly object _gate = new();

    internal async Task<Lease> AcquireAsync(byte[] snapshot)
    {
        Entry entry;
        lock (_gate)
        {
            entry = _entries.Find(e => ReferenceEquals(e.Snapshot, snapshot)) ?? new Entry(snapshot, load(snapshot));
            _entries.Remove(entry);
            _entries.Insert(0, entry);
            entry.Users++;
            while (_entries.Count > capacity)
            {
                var oldest = _entries[^1];
                _entries.RemoveAt(_entries.Count - 1);
                Retire(oldest);
            }
        }
        try { return new Lease(await entry.Value, () => Release(entry)); }
        catch
        {
            lock (_gate) { _entries.Remove(entry); Retire(entry); }
            Release(entry);
            throw;
        }
    }

    internal void Clear()
    {
        lock (_gate)
        {
            foreach (var entry in _entries) Retire(entry);
            _entries.Clear();
        }
    }

    private void Retire(Entry entry)
    {
        if (entry.Retired) return;
        entry.Retired = true;
        if (entry.Users == 0) DisposeWhenLoaded(entry);
    }

    private void Release(Entry entry)
    {
        lock (_gate)
            if (--entry.Users == 0 && entry.Retired) DisposeWhenLoaded(entry);
    }

    private static async void DisposeWhenLoaded(Entry entry)
    {
        try { (await entry.Value.ConfigureAwait(false)).Dispose(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Renderer cleanup: {ex.Message}"); }
    }
}
