using System.Runtime.CompilerServices;

namespace TenetSat;

/// <summary>
/// A view of one stored clause. <c>c[0]</c> is the deletion stamp (the batch-local index of
/// the step that deletes it, or <see cref="ClauseDb.Live"/>), <c>c[1..Length-1]</c> are the
/// literals. The clause lives inside a large shared int array (a slab), so millions of
/// clauses cost the garbage collector a handful of objects instead of millions.
/// </summary>
public readonly struct Clause
{
    internal readonly int[]? Slab;
    // Layout in the slab: [Off] = Length (1 + literal count), [Off + 1] = stamp, [Off + 2 ..] = literals.
    internal readonly int Off;

    internal Clause(int[] slab, int off) { Slab = slab; Off = off; }

    public bool IsNull => Slab is null;
    /// <summary>1 + the number of literals, as for the old int[] form.</summary>
    public int Length => Slab![Off];

    public ref int this[int k]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref Slab![Off + 1 + k];
    }

    /// <summary>Copy as [stamp, literals...].</summary>
    public int[] ToArray()
    {
        var a = new int[Length];
        Array.Copy(Slab!, Off + 1, a, 0, a.Length);
        return a;
    }
}

/// <summary>
/// Clauses written one after another by a single thread. While it is being written it may
/// grow (and move); once handed to <see cref="ClauseDb.RegisterSlab"/> it never changes size.
/// </summary>
internal sealed class ClauseBuffer
{
    public int[] Data;
    public int Used;
    public int Clauses;

    public ClauseBuffer(int capacity = 1 << 16) => Data = GC.AllocateUninitializedArray<int>(Math.Max(16, capacity));

    /// <summary>Starts empty on an existing array (a recycled slab); its contents are overwritten.</summary>
    public ClauseBuffer(int[] reuse) => Data = reuse;

    /// <summary>Starts a clause; returns its offset.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Begin()
    {
        if (Used + 2 > Data.Length) Grow(2);
        int off = Used;
        Data[off + 1] = ClauseDb.Live;
        Used += 2;
        return off;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push(int lit)
    {
        if (Used == Data.Length) Grow(1);
        Data[Used++] = lit;
    }

    /// <summary>Finishes the clause started at <paramref name="off"/>.</summary>
    public void End(int off)
    {
        Data[off] = Used - off - 1;
        Clauses++;
    }

    private void Grow(int need)
    {
        long size = Math.Max((long)Data.Length * 2, (long)Used + need);
        if (size > Array.MaxLength) throw new InsufficientMemoryException("a parse chunk needs more than 2^31 ints; use a smaller --batch-mb");
        var bigger = GC.AllocateUninitializedArray<int>((int)size);
        Array.Copy(Data, bigger, Used);
        Data = bigger;
    }

    /// <summary>Offset of the clause after the one at <paramref name="off"/>.</summary>
    public int Next(int off) => off + 1 + Data[off];
}

/// <summary>
/// Live clauses indexed by clause ID, in pages so that fully deleted ID ranges give their
/// memory back. A page holds 64-bit handles (slab number + 1, offset), not object
/// references, so the garbage collector has nothing to scan in it. A slab is dropped once
/// every clause in it has been removed.
///
/// Threading contract: a batch runs an add phase, a stamp phase, a check phase and a remove
/// phase, one after another. Within a phase many threads may work at once, but adds, removes
/// and reads never overlap each other. Slabs are registered single-threaded, before the adds
/// that use them. <see cref="Add"/> and <see cref="Remove"/> are the single-threaded forms.
/// </summary>
public sealed class ClauseDb
{
    public const int Live = int.MaxValue;

    private const int PageBits = 16;
    private const int PageSize = 1 << PageBits;
    private const int PageMask = PageSize - 1;

    private long[]?[] _pages = new long[]?[64];
    // One counter per page and per slab, spaced a cache line apart so threads working on
    // neighbouring ones do not contend.
    private const int CounterShift = 4;
    private int[] _liveInPage = new int[64 << CounterShift];

    private int[]?[] _slabs = new int[]?[256];
    /// <summary>Arrays of slabs whose clauses have all been removed, for reuse by later parses.</summary>
    // One bin per power-of-two size: chunk buffers are allocated in those sizes, so a dead
    // slab always fits a later chunk of the same class.
    private readonly System.Collections.Concurrent.ConcurrentBag<int[]>[] _recycled =
        Enumerable.Range(0, 32).Select(_ => new System.Collections.Concurrent.ConcurrentBag<int[]>()).ToArray();
    public long SlabsRecycled, SlabsReused;
    private int[] _liveInSlab = new int[256 << CounterShift];
    private int _slabCount;

    // Single-threaded adds go into a fixed-size slab that is registered when it is started.
    private const int SeqSlabInts = 1 << 22;
    private int[]? _seq;
    private int _seqIndex, _seqUsed;

    public long MaxId { get; private set; }
    public long LiveCount { get; private set; }
    public long PeakLiveCount { get; private set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Clause Get(long id)
    {
        if (id <= 0) return default;
        long p = id >> PageBits;
        var pages = _pages;
        if (p >= pages.Length) return default;
        var page = pages[p];
        if (page is null) return default;
        long h = page[(int)(id & PageMask)];
        if (h == 0) return default;
        var slab = _slabs[(int)(h >> 32) - 1];
        return slab is null ? default : new Clause(slab, (int)h);
    }

    /// <summary>
    /// Single-threaded. Makes <paramref name="data"/> a slab holding <paramref name="clauses"/>
    /// clauses that are about to be added; returns its number.
    /// </summary>
    internal int RegisterSlab(int[] data, int clauses)
    {
        int si = _slabCount++;
        if (si == _slabs.Length)
        {
            Array.Resize(ref _slabs, si * 2);
            Array.Resize(ref _liveInSlab, (si * 2) << CounterShift);
        }
        _slabs[si] = data;
        _liveInSlab[si << CounterShift] = clauses;
        return si;
    }

    /// <summary>Single-threaded add of a clause given by its literals.</summary>
    public void Add(long id, ReadOnlySpan<int> literals)
    {
        int need = literals.Length + 2;
        if (_seq is null || _seqUsed + need > _seq.Length)
        {
            _seq = new int[Math.Max(SeqSlabInts, need)];
            _seqUsed = 0;
            _seqIndex = RegisterSlab(_seq, 0);
        }
        int off = _seqUsed;
        _seq[off] = literals.Length + 1;
        _seq[off + 1] = Live;
        literals.CopyTo(_seq.AsSpan(off + 2));
        _seqUsed += need;
        _liveInSlab[_seqIndex << CounterShift]++;

        EnsureCapacity(id);
        var page = _pages[id >> PageBits] ??= new long[PageSize];
        int i = (int)(id & PageMask);
        if (page[i] == 0)
        {
            _liveInPage[(id >> PageBits) << CounterShift]++;
            if (++LiveCount > PeakLiveCount) PeakLiveCount = LiveCount;
        }
        else ReleaseFromSlab(page[i]);
        page[i] = Handle(_seqIndex, off);
    }

    public void Remove(long id)
    {
        if (id <= 0) return;
        long p = id >> PageBits;
        if (p >= _pages.Length) return;
        var page = _pages[p];
        if (page is null) return;
        int i = (int)(id & PageMask);
        long h = page[i];
        if (h == 0) return;
        page[i] = 0;
        LiveCount--;
        ReleaseFromSlab(h);
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Handle(int slab, int off) => ((long)(slab + 1) << 32) | (uint)off;

    /// <summary>
    /// Thread-safe against other concurrent adds of different IDs. Needs
    /// <see cref="EnsureCapacity"/> and <see cref="RegisterSlab"/> first. The caller counts its
    /// adds per page in <paramref name="runPage"/>/<paramref name="runCount"/> (start both at 0)
    /// and must call <see cref="FlushRun"/> at the end; IDs arrive in increasing order, so this
    /// turns one shared-counter update per clause into one per page.
    /// </summary>
    internal void AddConcurrent(long id, int slab, int off, ref long runPage, ref int runCount)
    {
        long p = id >> PageBits;
        if (p != runPage) { FlushRun(runPage, runCount); runPage = p; runCount = 0; }
        var page = Volatile.Read(ref _pages[p]);
        if (page is null)
        {
            var fresh = new long[PageSize];
            page = Interlocked.CompareExchange(ref _pages[p], fresh, null) ?? fresh;
        }
        page[(int)(id & PageMask)] = Handle(slab, off);
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
        long h = Interlocked.Exchange(ref page[(int)(id & PageMask)], 0);
        if (h == 0) return false;
        ReleaseFromSlab(h);
        if (Interlocked.Decrement(ref _liveInPage[p << CounterShift]) == 0) Volatile.Write(ref _pages[p], null);
        return true;
    }

    private void ReleaseFromSlab(long h)
    {
        int si = (int)(h >> 32) - 1;
        // The single-threaded slab still being filled is never dropped.
        if (Interlocked.Decrement(ref _liveInSlab[si << CounterShift]) == 0 && !(si == _seqIndex && _seq is not null))
        {
            // Nothing can reach this slab any more: every handle into it has been removed.
            var dead = Interlocked.Exchange(ref _slabs[si], null);
            if (dead is not null && (dead.Length & (dead.Length - 1)) == 0)
            {
                _recycled[System.Numerics.BitOperations.Log2((uint)dead.Length)].Add(dead);
                Interlocked.Increment(ref SlabsRecycled);
            }
        }
    }

    /// <summary>A recycled slab array with room for at least <paramref name="ints"/> ints, or a new one.</summary>
    internal int[] TakeArray(int ints)
    {
        int bin = System.Numerics.BitOperations.Log2(System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, ints)));
        // An exact-size bin first, then one size up (wastes at most half of it).
        for (int b = bin; b <= Math.Min(30, bin + 1); b++)
            if (_recycled[b].TryTake(out var a)) { Interlocked.Increment(ref SlabsReused); return a; }
        return GC.AllocateUninitializedArray<int>(1 << bin);
    }

    /// <summary>Single-threaded. Accounts for clauses added or removed by the concurrent forms.</summary>
    internal void NoteLiveDelta(long delta)
    {
        LiveCount += delta;
        if (LiveCount > PeakLiveCount) PeakLiveCount = LiveCount;
    }

    /// <summary>Visits every stored clause with ID below <paramref name="idLimit"/>.</summary>
    internal bool All(long idLimit, Func<long, Clause, bool> predicate)
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
                long h = page[i];
                if (h == 0) continue;
                long id = baseId + i;
                if (id >= idLimit) return true;
                var slab = _slabs[(int)(h >> 32) - 1];
                if (slab is null) continue;
                if (!predicate(id, new Clause(slab, (int)h))) return false;
            }
        }
        return true;
    }
}
