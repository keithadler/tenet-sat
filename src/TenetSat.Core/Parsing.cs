namespace TenetSat;

/// <summary>A run of parsed proof steps. Additions carry a clause; deletions carry null.</summary>
internal sealed class StepChunk
{
    public long[] Ids = new long[1024];
    public int[]?[] Clauses = new int[]?[1024];
    public int[] RefEnd = new int[1024];
    /// <summary>Hints for additions, deleted IDs for deletions.</summary>
    public long[] Refs = new long[8192];
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
        if (RefCount == Refs.Length) Array.Resize(ref Refs, RefCount * 2);
        Refs[RefCount++] = r;
    }

    public void EndStep(long id, int[]? clause)
    {
        if (Count == Ids.Length)
        {
            Array.Resize(ref Ids, Count * 2);
            Array.Resize(ref Clauses, Count * 2);
            Array.Resize(ref RefEnd, Count * 2);
        }
        if (clause is not null)
        {
            if (AddCount == 0) FirstAddId = id;
            else if (id <= LastAddId && BadId == 0) { BadId = id; BadPrev = LastAddId; }
            LastAddId = id;
            AddCount++;
            if (clause.Length == 1) HasEmpty = true;
        }
        Ids[Count] = id;
        Clauses[Count] = clause;
        RefEnd[Count] = RefCount;
        Count++;
    }

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
        Load(file.Ptr, file.Length, (id, clause) => db.Add(id, clause), out hasEmptyClause);
    }

    /// <summary>Calls <paramref name="onClause"/> with (id, clause) for each clause, clause[0] = Live.</summary>
    public static void Load(byte* start, long length, Action<long, int[]> onClause, out bool hasEmptyClause)
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
            var clause = new int[n];
            clause[0] = ClauseDb.Live;
            Array.Copy(lits, 1, clause, 1, n - 1);
            if (n == 1) hasEmptyClause = true;
            onClause(++id, clause);
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
        var lits = new int[64];
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
                chunk.EndStep(id, null);
                continue;
            }
            int n = 1;
            while (true)
            {
                if (!Scan.ReadLong(ref p, end, false, out long v) || v > Scan.MaxVar || v < -Scan.MaxVar)
                { Fail(chunk, fileStart, p); return; }
                if (v == 0) break;
                if (n == lits.Length) Array.Resize(ref lits, n * 2);
                lits[n++] = (int)v;
            }
            var clause = new int[n];
            clause[0] = ClauseDb.Live;
            Array.Copy(lits, 1, clause, 1, n - 1);
            while (true)
            {
                if (!Scan.ReadLong(ref p, end, false, out long r)) { Fail(chunk, fileStart, p); return; }
                if (r == 0) break;
                chunk.AddRef(r);
            }
            chunk.EndStep(id, clause);
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
        var lits = new int[64];
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
                chunk.EndStep(0, null);
            }
            else if (tag == (byte)'a')
            {
                if (!Scan.ReadVarint(ref p, end, out ulong uid) || (uid & 1) != 0 || uid == 0) return Fail(chunk, fileStart, p, end);
                int n = 1;
                while (true)
                {
                    if (!Scan.ReadVarint(ref p, end, out ulong u) || (u >> 1) > Scan.MaxVar) return Fail(chunk, fileStart, p, end);
                    if (u == 0) break;
                    if (n == lits.Length) Array.Resize(ref lits, n * 2);
                    int v = (int)(u >> 1);
                    lits[n++] = (u & 1) != 0 ? -v : v;
                }
                var clause = new int[n];
                clause[0] = ClauseDb.Live;
                Array.Copy(lits, 1, clause, 1, n - 1);
                while (true)
                {
                    if (!Scan.ReadVarint(ref p, end, out ulong u)) return Fail(chunk, fileStart, p, end);
                    if (u == 0) break;
                    long h = (long)(u >> 1);
                    chunk.AddRef((u & 1) != 0 ? -h : h);
                }
                chunk.EndStep((long)(uid >> 1), clause);
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
