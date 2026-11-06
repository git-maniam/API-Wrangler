using System.Collections;

namespace ApiWrangler.Application;

public sealed record DatasetRow(int Index, string CaseId, string Email, int ExpectedStatus);

// Deterministic phase-0 fixture: at most four pages of rows are retained, regardless of Count.
public sealed class PagedDataset(int count = 100_000) : IList
{
    private const int PageSize = 256;
    private readonly Dictionary<int, DatasetRow[]> pages = [];
    private readonly Queue<int> insertionOrder = [];
    public int Count { get; } = count >= 0 ? count : throw new ArgumentOutOfRangeException(nameof(count));
    public int CachedRowCount => pages.Values.Sum(p => p.Length);

    public object? this[int index]
    {
        get
        {
            if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
            var page = index / PageSize;
            if (!pages.TryGetValue(page, out var rows))
            {
                if (pages.Count == 4) pages.Remove(insertionOrder.Dequeue());
                rows = Enumerable.Range(page * PageSize, Math.Min(PageSize, Count - page * PageSize))
                    .Select(i => new DatasetRow(i, $"case-{i:D6}", $"tester{i}@example.test", 200)).ToArray();
                pages.Add(page, rows);
                insertionOrder.Enqueue(page);
            }
            return rows[index % PageSize];
        }
        set => throw new NotSupportedException();
    }

    public bool IsReadOnly => true;
    public bool IsFixedSize => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public int IndexOf(object? value) => value is DatasetRow row && row.Index >= 0 && row.Index < Count ? row.Index : -1;
    public bool Contains(object? value) => IndexOf(value) >= 0;
    public IEnumerator GetEnumerator() { for (var i = 0; i < Count; i++) yield return this[i]!; }
    public void CopyTo(Array array, int index) { for (var i = 0; i < Count; i++) array.SetValue(this[i], index + i); }
    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
}
