using System.Runtime.CompilerServices;

namespace TenetSat;

/// <summary>
/// Live clauses indexed by clause ID, in pages so that fully deleted ID ranges give
/// their memory back.
///
/// A clause is an int[]: element 0 is the deletion stamp (the batch-local index of
/// the step that deletes it, or <see cref="Live"/>), elements 1.. are the literals.
///
/// Threading contract: a batch runs an add phase, a stamp phase, a check phase and a remove
/// phase, one after another. Within a phase many threads may work at once, but adds, removes
/// and reads never overlap each other. <see cref="Add"/> and <see cref="Remove"/> are the
/// single-threaded forms.
/// </summary>
public sealed class ClauseDb
{
    public const int Live = int.MaxValue;

    private const int PageBits = 16;
    private const int PageSize = 1 << PageBits;
    private const int PageMask = PageSize - 1;

    private int[]?[]?[] _pages = new int[]?[]?[64];
    // One counter per page, spaced a cache line apart so threads working on neighbouring
    // pages do not contend.
    private const int CounterShift = 4;
    private int[] _liveInPage = new int[64 << CounterShift];

    public long MaxId { get; private set; }
    public long LiveCount { get; private set; }
    public long PeakLiveCount { get; private set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int[]? Get(long id)
    {
        if (id <= 0) return null;
        long p = id >> PageBits;
        var pages = _pages;
        if (p >= pages.Length) return null;
        var page = pages[p];
        return page?[(int)(id & PageMask)];
    }

    public void Add(long id, int[] clause)
    {
        long p = id >> PageBits;
        if (p >= _pages.Length)
        {
            int n = _pages.Length;
            while (n <= p) n = checked(n * 2);
            Array.Resize(ref _pages, n);
            Array.Resize(ref _liveInPage, n << CounterShift);
        }
        var page = _pages[p] ??= new int[]?[PageSize];
        int i = (int)(id & PageMask);
        if (page[i] is null)
        {
            _liveInPage[p << CounterShift]++;
            if (++LiveCount > PeakLiveCount) PeakLiveCount = LiveCount;
        }
        page[i] = clause;
        if (id > MaxId) MaxId = id;
    }

    public void Remove(long id)
    {
        if (id <= 0) return;
        long p = id >> PageBits;
        if (p >= _pages.Length) return;
        var page = _pages[p];
        if (page is null) return;
        int i = (int)(id & PageMask);
        if (page[i] is null) return;
        page[i] = null;
        LiveCount--;
        if (--_liveInPage[p << CounterShift] == 0) _pages[p] = null;
    }

    /// <summary>Single-threaded. Makes room so that concurrent adds up to this ID never resize.</summary>
    public void EnsureCapacity(long maxId)
    {
        long p = maxId >> PageBits;
        if (p >= _pages.Length)
        {
            int n = _pages.Length;
            while (n <= p) n = checked(n * 2);
            Array.Resize(ref _pages, n);
            Array.Resize(ref _liveInPage, n << CounterShift);
        }
        if (maxId > MaxId) MaxId = maxId;
    }

    /// <summary>
    /// Thread-safe against other concurrent adds of different IDs. Needs
    /// <see cref="EnsureCapacity"/> first. The caller counts its adds per page in
    /// <paramref name="runPage"/>/<paramref name="runCount"/> (start both at 0) and must call
    /// <see cref="FlushRun"/> at the end; IDs arrive in increasing order, so this turns one
    /// shared-counter update per clause into one per page.
    /// </summary>
    internal void AddConcurrent(long id, int[] clause, ref long runPage, ref int runCount)
    {
        long p = id >> PageBits;
        if (p != runPage) { FlushRun(runPage, runCount); runPage = p; runCount = 0; }
        var page = Volatile.Read(ref _pages[p]);
        if (page is null)
        {
            var fresh = new int[]?[PageSize];
            page = Interlocked.CompareExchange(ref _pages[p], fresh, null) ?? fresh;
        }
        page[(int)(id & PageMask)] = clause;
        runCount++;
    }

    internal void FlushRun(long runPage, int runCount)
    {
        if (runCount != 0) Interlocked.Add(ref _liveInPage[runPage << CounterShift], runCount);
    }

    /// <summary>Thread-safe against other concurrent removes. Returns whether the clause was there.</summary>
    internal bool RemoveConcurrent(long id)
    {
        if (id <= 0) return false;
        long p = id >> PageBits;
        if (p >= _pages.Length) return false;
        var page = Volatile.Read(ref _pages[p]);
        if (page is null) return false;
        if (Interlocked.Exchange(ref page[(int)(id & PageMask)], null) is null) return false;
        if (Interlocked.Decrement(ref _liveInPage[p << CounterShift]) == 0) Volatile.Write(ref _pages[p], null);
        return true;
    }

    /// <summary>Single-threaded. Accounts for clauses added or removed by the concurrent forms.</summary>
    internal void NoteLiveDelta(long delta)
    {
        LiveCount += delta;
        if (LiveCount > PeakLiveCount) PeakLiveCount = LiveCount;
    }

    /// <summary>Visits every stored clause with ID below <paramref name="idLimit"/>.</summary>
    internal bool All(long idLimit, Func<long, int[], bool> predicate)
    {
        var pages = _pages;
        long lastPage = Math.Min(pages.Length - 1, (idLimit - 1) >> PageBits);
        for (long p = 0; p <= lastPage; p++)
        {
            var page = pages[p];
            if (page is null) continue;
            long baseId = p << PageBits;
            for (int i = 0; i < PageSize; i++)
            {
                var c = page[i];
                if (c is null) continue;
                long id = baseId + i;
                if (id >= idLimit) return true;
                if (!predicate(id, c)) return false;
            }
        }
        return true;
    }
}
