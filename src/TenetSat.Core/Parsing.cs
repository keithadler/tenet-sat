namespace TenetSat;

/// <summary>A run of parsed proof steps. Additions carry a clause; deletions carry null.</summary>
internal sealed class StepChunk
{
    public long[] Ids;
    /// <summary>Offset of each addition's clause in <see cref="Lits"/>, or -1 for a deletion.</summary>
    public int[] ClauseOff;
    /// <summary>The clauses this chunk adds, back to back; registered as one slab.</summary>
    public ClauseBuffer Lits;
    /// <summary>Slab number of <see cref="Lits"/> once registered.</summary>
    public int Slab;
    public int[] RefEnd;
    /// <summary>Hints for additions, deleted IDs for deletions.</summary>
    public long[] Refs;

    public StepChunk(int steps = 1024, int refs = 8192, int lits = 1 << 16, ClauseDb? db = null)
    {
        Ids = GC.AllocateUninitializedArray<long>(steps);
        ClauseOff = GC.AllocateUninitializedArray<int>(steps);
        RefEnd = GC.AllocateUninitializedArray<int>(steps);
        Refs = GC.AllocateUninitializedArray<long>(refs);
        Lits = db is null ? new ClauseBuffer(lits) : new ClauseBuffer(db.TakeArray(lits));
    }

    /// <summary>
    /// Empties the chunk for the next batch, keeping its step and hint arrays (grown to at
    /// least the given sizes). <see cref="Lits"/> is always new: the old one now belongs to
    /// the clause database as a slab.
    /// </summary>
    public void Reset(int steps, int refs, int lits, ClauseDb db)
    {
        if (Ids.Length < steps)
        {
            Ids = GC.AllocateUninitializedArray<long>(steps);
            ClauseOff = GC.AllocateUninitializedArray<int>(steps);
            RefEnd = GC.AllocateUninitializedArray<int>(steps);
        }
        if (Refs.Length < refs) Refs = GC.AllocateUninitializedArray<long>(refs);
        Lits = new ClauseBuffer(db.TakeArray(lits));
        Slab = 0; Count = 0; RefCount = 0; Base = 0; Error = null;
        AddCount = 0; FirstAddId = LastAddId = PrevAddId = 0; BadId = BadPrev = 0; HasEmpty = false;
    }
    public int Count;
    public int RefCount;
    /// <summary>Batch-local index of this chunk's first step.</summary>
    public int Base;
    public string? Error;

    // Summary of this chunk's additions, kept while parsing so that ordering can be
    // validated across chunks without walking the steps again.
    public int AddCount;
    public long FirstAddId, LastAddId;
    /// <summary>Last addition ID before this chunk in proof order.</summary>
    public long PrevAddId;
    /// <summary>An addition whose ID does not exceed the one before it in this chunk, or 0.</summary>
    public long BadId, BadPrev;
    public bool HasEmpty;

    public void AddRef(long r)
    {
        if (RefCount == Refs.Length) Grow(ref Refs, RefCount * 2);
        Refs[RefCount++] = r;
    }

    /// <summary>Ends a step; <paramref name="off"/> is the clause offset for an addition, -1 for a deletion.</summary>
    public void EndStep(long id, int off)
    {
        if (Count == Ids.Length)
        {
            Grow(ref Ids, Count * 2);
            Grow(ref ClauseOff, Count * 2);
            Grow(ref RefEnd, Count * 2);
        }
        if (off >= 0)
        {
            Lits.End(off);
            if (AddCount == 0) FirstAddId = id;
            else if (id <= LastAddId && BadId == 0) { BadId = id; BadPrev = LastAddId; }
            LastAddId = id;
            AddCount++;
            if (Lits.Data[off] == 1) HasEmpty = true;
        }
        Ids[Count] = id;
        ClauseOff[Count] = off;
        RefEnd[Count] = RefCount;
        Count++;
    }

    private static void Grow<T>(ref T[] a, int size)
    {
        var bigger = GC.AllocateUninitializedArray<T>(size);
        Array.Copy(a, bigger, a.Length);
        a = bigger;
    }

    public bool IsAddition(int j) => ClauseOff[j] >= 0;

    /// <summary>The clause added by step <paramref name="j"/>; null view for a deletion.</summary>
    public Clause ClauseAt(int j) => ClauseOff[j] < 0 ? default : new Clause(Lits.Data, ClauseOff[j]);

    public ReadOnlySpan<long> RefsOf(int j)
    {
        int start = j == 0 ? 0 : RefEnd[j - 1];
        return Refs.AsSpan(start, RefEnd[j] - start);
    }
}

internal static unsafe class Scan
{
    /// <summary>Largest variable we accept, so that literal codes fit in an int.</summary>
    public const int MaxVar = (1 << 30) - 1;

    /// <summary>
    /// Reads a signed decimal integer, skipping spaces, tabs and CRs first. When
    /// <paramref name="crossLines"/> is false a newline before the number is a failure,
    /// which keeps LRAT steps strictly one per line.
    /// </summary>
    public static bool ReadLong(ref byte* p, byte* end, bool crossLines, out long value)
    {
        value = 0;
        byte* q = p;
        while (q < end && (*q == ' ' || *q == '\t' || *q == '\r' || (crossLines && *q == '\n'))) q++;
        bool neg = false;
        if (q < end && *q == '-') { neg = true; q++; }
        if (q >= end || *q < '0' || *q > '9') { p = q; return false; }
        long v = 0;
        int digits = 0;
        while (q < end && *q >= '0' && *q <= '9')
        {
            if (++digits > 18) { p = q; return false; }
            v = v * 10 + (*q - '0');
            q++;
        }
        // A number must end at whitespace or end of input.
        if (q < end && !(*q == ' ' || *q == '\t' || *q == '\r' || *q == '\n')) { p = q; return false; }
        p = q;
        value = neg ? -v : v;
        return true;
    }

    public static bool ReadVarint(ref byte* p, byte* end, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (p < end)
        {
            byte b = *p++;
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
            if (shift > 63) return false;
        }
        return false;
    }
}

/// <summary>DIMACS CNF. Clauses get IDs 1, 2, 3, ... in file order.</summary>
public static unsafe class CnfParser
{
    public static void Load(string path, ClauseDb db, out bool hasEmptyClause)
    {
        using var file = new MappedFile(path);
        LoadInto(file.Ptr, file.Length, db, out hasEmptyClause);
    }

    public delegate void ClauseSink(long id, ReadOnlySpan<int> literals);

    private static void LoadInto(byte* start, long length, ClauseDb db, out bool hasEmptyClause) =>
        Load(start, length, (long id, ReadOnlySpan<int> lits) => db.Add(id, lits), out hasEmptyClause);

    /// <summary>Calls <paramref name="onClause"/> with (id, clause) for each clause, clause[0] = Live.</summary>
    public static void Load(byte* start, long length, Action<long, int[]> onClause, out bool hasEmptyClause) =>
        Load(start, length, (long id, ReadOnlySpan<int> lits) =>
        {
            var clause = new int[lits.Length + 1];
            clause[0] = ClauseDb.Live;
            lits.CopyTo(clause.AsSpan(1));
            onClause(id, clause);
        }, out hasEmptyClause);

    /// <summary>
    /// Same result as <see cref="Load(string, ClauseDb, out bool)"/>, parsed on several threads.
    /// The file is cut at line starts and the pieces are parsed at once; a clause that runs
    /// across a cut is joined back together, and IDs are numbered in file order. Anything
    /// unusual (malformed input, a comment or header line at the very start of a piece, a
    /// '%' end marker) falls back to the sequential loader, so results and error messages
    /// are exactly those of <see cref="Load(string, ClauseDb, out bool)"/>.
    /// </summary>
    public static void Load(string path, ClauseDb db, int threads, out bool hasEmptyClause, long minParallelBytes = 4 << 20)
    {
        using var file = new MappedFile(path);
        if (threads <= 1 || file.Length < minParallelBytes || !TryLoadParallel(file.Ptr, file.Length, db, threads, out hasEmptyClause))
            LoadInto(file.Ptr, file.Length, db, out hasEmptyClause);
    }

    private sealed class Piece
    {
        public bool Fallback;
        /// <summary>Literals before this piece's first terminating 0 (pieces after the first).</summary>
        public List<int> Head = new();
        public bool HasZero;
        public ClauseBuffer Clauses = new();
        public int Slab;
        /// <summary>Literals of a clause still open at the end of the piece.</summary>
        public List<int>? Tail;
        public bool HasEmpty;
        public long BaseId;
    }

    private static bool TryLoadParallel(byte* start, long length, ClauseDb db, int threads, out bool hasEmptyClause)
    {
        hasEmptyClause = false;
        int pieces = threads * 4;
        var bounds = new long[pieces + 1];
        for (int k = 1; k < pieces; k++)
        {
            long off = Math.Max(bounds[k - 1], length * k / pieces);
            while (off < length && off > 0 && start[off - 1] != '\n') off++;
            bounds[k] = off;
        }
        bounds[pieces] = length;
        var parts = new Piece[pieces];
        Parallel.For(0, pieces, new ParallelOptions { MaxDegreeOfParallelism = threads },
            k => parts[k] = ParsePiece(start + bounds[k], start + bounds[k + 1], first: k == 0));
        foreach (var part in parts) if (part.Fallback) return false;

        // Stitch in file order: a clause open at the end of one piece continues into the next.
        long id = 0;
        List<int>? carry = null;
        var joined = new List<(long Id, int[] Literals)>();
        foreach (var part in parts)
        {
            if (!part.HasZero)
            {
                // No clause ends in this piece: all of it belongs to the clause still open
                // (collected as the head in later pieces, as the tail in the first).
                (carry ??= new()).AddRange(part.Head);
                if (part.Tail is not null) carry.AddRange(part.Tail);
                continue;
            }
            if (!ReferenceEquals(part, parts[0]))
            {
                int n = (carry?.Count ?? 0) + part.Head.Count;
                var literals = new int[n];
                carry?.CopyTo(literals, 0);
                part.Head.CopyTo(literals, carry?.Count ?? 0);
                if (n == 0) hasEmptyClause = true;
                joined.Add((++id, literals));
            }
            part.BaseId = id;
            id += part.Clauses.Clauses;
            hasEmptyClause |= part.HasEmpty;
            carry = part.Tail;
        }
        if (carry is { Count: > 0 }) return false; // unterminated clause at the end: let the sequential loader report it

        db.EnsureCapacity(id);
        foreach (var part in parts)
            if (part.Clauses.Clauses > 0) part.Slab = db.RegisterSlab(part.Clauses.Data, part.Clauses.Clauses);
        Parallel.For(0, pieces, new ParallelOptions { MaxDegreeOfParallelism = threads }, k =>
        {
            var part = parts[k];
            long runPage = 0;
            int runCount = 0;
            int off = 0;
            for (int j = 0; j < part.Clauses.Clauses; j++, off = part.Clauses.Next(off))
                db.AddConcurrent(part.BaseId + 1 + j, part.Slab, off, ref runPage, ref runCount);
            db.FlushRun(runPage, runCount);
        });
        db.NoteLiveDelta(id - joined.Count);
        foreach (var (cid, literals) in joined) db.Add(cid, literals);
        return true;
    }

    private static Piece ParsePiece(byte* p, byte* end, bool first)
    {
        var part = new Piece();
        // In the first piece the parser is known to start between clauses. In any other piece
        // it starts at a line start that may be inside a clause, so everything up to the first
        // 0 is collected as the head and joined to the previous piece's open clause.
        bool head = !first;
        var lits = new List<int>();
        while (true)
        {
            while (p < end && (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n')) p++;
            if (p >= end) return part;
            if (*p == 'c' || *p == 'p' || *p == '%')
            {
                if (head || *p == '%') { part.Fallback = true; return part; }
                while (p < end && *p != '\n') p++;
                continue;
            }
            lits.Clear();
            while (true)
            {
                if (!Scan.ReadLong(ref p, end, crossLines: true, out long v))
                {
                    // Out of input inside a clause: the rest of it is in the next piece.
                    byte* q = p;
                    while (q < end && (*q == ' ' || *q == '\t' || *q == '\r' || *q == '\n')) q++;
                    if (q < end) { part.Fallback = true; return part; }
                    if (head) part.Head.AddRange(lits);
                    else part.Tail = new List<int>(lits);
                    return part;
                }
                if (v == 0) break;
                if (v > Scan.MaxVar || v < -Scan.MaxVar) { part.Fallback = true; return part; }
                lits.Add((int)v);
            }
            if (head)
            {
                part.Head.AddRange(lits);
                part.HasZero = true;
                head = false;
                continue;
            }
            part.HasZero = true;
            int off = part.Clauses.Begin();
            foreach (int l in lits) part.Clauses.Push(l);
            part.Clauses.End(off);
            if (lits.Count == 0) part.HasEmpty = true;
        }
    }

    /// <summary>Calls <paramref name="onClause"/> with each clause's ID and literals.</summary>
    public static void Load(byte* start, long length, ClauseSink onClause, out bool hasEmptyClause)
    {
        hasEmptyClause = false;
        byte* p = start, end = start + length;
        var lits = new int[64];
        long id = 0;
        while (true)
        {
            while (p < end && (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n')) p++;
            if (p >= end) return;
            if (*p == 'c' || *p == 'p' || *p == '%')
            {
                if (*p == '%') return; // SATLIB end marker
                while (p < end && *p != '\n') p++;
                continue;
            }
            int n = 1;
            while (true)
            {
                if (!Scan.ReadLong(ref p, end, crossLines: true, out long v))
                    throw new FormatException($"Malformed CNF near byte {p - start}.");
                if (v == 0) break;
                if (v > Scan.MaxVar || v < -Scan.MaxVar)
                    throw new FormatException($"CNF literal {v} out of range near byte {p - start}.");
                if (n == lits.Length) Array.Resize(ref lits, n * 2);
                lits[n++] = (int)v;
            }
            if (n == 1) hasEmptyClause = true;
            onClause(++id, lits.AsSpan(1, n - 1));
        }
    }
}

/// <summary>
/// Text LRAT, one step per line:
///   &lt;id&gt; &lt;lits&gt; 0 &lt;hints&gt; 0
///   &lt;id&gt; d &lt;ids&gt; 0
/// </summary>
internal static unsafe class LratTextParser
{
    public static void Parse(byte* fileStart, byte* p, byte* end, StepChunk chunk)
    {
        while (true)
        {
            while (p < end && (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n')) p++;
            if (p >= end) return;
            if (*p == 'c')
            {
                while (p < end && *p != '\n') p++;
                continue;
            }
            if (!Scan.ReadLong(ref p, end, false, out long id) || id <= 0) { Fail(chunk, fileStart, p); return; }
            while (p < end && (*p == ' ' || *p == '\t')) p++;
            if (p < end && *p == 'd')
            {
                p++;
                while (true)
                {
                    if (!Scan.ReadLong(ref p, end, false, out long r) || r < 0) { Fail(chunk, fileStart, p); return; }
                    if (r == 0) break;
                    chunk.AddRef(r);
                }
                chunk.EndStep(id, -1);
                continue;
            }
            int off = chunk.Lits.Begin();
            while (true)
            {
                if (!Scan.ReadLong(ref p, end, false, out long v) || v > Scan.MaxVar || v < -Scan.MaxVar)
                { Fail(chunk, fileStart, p); return; }
                if (v == 0) break;
                chunk.Lits.Push((int)v);
            }
            while (true)
            {
                if (!Scan.ReadLong(ref p, end, false, out long r)) { Fail(chunk, fileStart, p); return; }
                if (r == 0) break;
                chunk.AddRef(r);
            }
            chunk.EndStep(id, off);
        }
    }

    private static void Fail(StepChunk chunk, byte* fileStart, byte* p) =>
        chunk.Error = $"malformed LRAT text near byte {p - fileStart}";
}

/// <summary>
/// Binary LRAT as written by drat-trim / lrat-trim / CaDiCaL:
///   'a' id lits 0 hints 0      'd' ids 0
/// Every number, IDs included, is a varint of 2*|x| + (x &lt; 0).
/// </summary>
internal static unsafe class LratBinaryParser
{
    public static bool LooksBinary(byte* p, long length)
    {
        long n = Math.Min(length, 4096);
        for (long i = 0; i < n; i++)
        {
            byte b = p[i];
            if (b == 0 || b >= 0x80) return true;
            if (b < 0x20 && b != '\n' && b != '\r' && b != '\t') return true;
        }
        return false;
    }

    /// <summary>
    /// A guess at a step start at or after <paramref name="from"/>: a tag byte that follows a
    /// zero byte. It can be wrong (a hint list may begin with the same byte), so callers must
    /// confirm it against the true end of the preceding step.
    /// </summary>
    public static long GuessStepStart(byte* b, long from, long limit)
    {
        for (long i = Math.Max(from, 1); i < limit; i++)
            if (b[i - 1] == 0 && (b[i] == (byte)'a' || b[i] == (byte)'d')) return i;
        return limit;
    }

    /// <summary>
    /// Parses whole steps starting at <paramref name="p"/> until the position reaches
    /// <paramref name="softEnd"/>; the last step may run past it, up to <paramref name="end"/>.
    /// Returns the position after the last step parsed.
    /// </summary>
    public static byte* Parse(byte* fileStart, byte* p, byte* softEnd, byte* end, StepChunk chunk)
    {
        while (p < softEnd)
        {
            byte tag = *p++;
            if (tag == (byte)'d')
            {
                while (true)
                {
                    if (!Scan.ReadVarint(ref p, end, out ulong u) || (u & 1) != 0) return Fail(chunk, fileStart, p, end);
                    if (u == 0) break;
                    chunk.AddRef((long)(u >> 1));
                }
                chunk.EndStep(0, -1);
            }
            else if (tag == (byte)'a')
            {
                if (!Scan.ReadVarint(ref p, end, out ulong uid) || (uid & 1) != 0 || uid == 0) return Fail(chunk, fileStart, p, end);
                int off = chunk.Lits.Begin();
                while (true)
                {
                    if (!Scan.ReadVarint(ref p, end, out ulong u) || (u >> 1) > Scan.MaxVar) return Fail(chunk, fileStart, p, end);
                    if (u == 0) break;
                    int v = (int)(u >> 1);
                    chunk.Lits.Push((u & 1) != 0 ? -v : v);
                }
                while (true)
                {
                    if (!Scan.ReadVarint(ref p, end, out ulong u)) return Fail(chunk, fileStart, p, end);
                    if (u == 0) break;
                    long h = (long)(u >> 1);
                    chunk.AddRef((u & 1) != 0 ? -h : h);
                }
                chunk.EndStep((long)(uid >> 1), off);
            }
            else return Fail(chunk, fileStart, p, end);
        }
        return p;
    }

    private static byte* Fail(StepChunk chunk, byte* fileStart, byte* p, byte* end)
    {
        chunk.Error = $"malformed binary LRAT near byte {p - fileStart}";
        return end;
    }
}
