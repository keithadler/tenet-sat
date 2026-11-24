using System.Collections.Concurrent;
using System.Diagnostics;

namespace TenetSat;

public sealed class CheckOptions
{
    public int Threads { get; init; } = Environment.ProcessorCount;
    /// <summary>Proof bytes per batch.</summary>
    public long BatchBytes { get; init; } = 32L << 20;
}

public sealed class CheckResult
{
    public bool Verified { get; init; }
    public string Message { get; init; } = "";
    public long OriginalClauses { get; init; }
    public long Additions { get; init; }
    public long Deletions { get; init; }
    public long RatSteps { get; init; }
    public long PeakLiveClauses { get; init; }
    public bool Binary { get; init; }
    public TimeSpan ParseTime { get; init; }
    public TimeSpan CheckTime { get; init; }
    /// <summary>Time registering additions, stamping deletions and dropping deleted clauses (parallel).</summary>
    public TimeSpan RegisterTime { get; init; }
    /// <summary>Time in the single-threaded per-batch bookkeeping.</summary>
    public TimeSpan SequentialTime { get; init; }
    /// <summary>Time loading the formula, which is single-threaded.</summary>
    public TimeSpan CnfTime { get; init; }
    /// <summary>Binary chunks whose guessed start was wrong and were parsed again.</summary>
    public long BinaryReparses { get; init; }
    public TimeSpan TotalTime { get; init; }
}

/// <summary>
/// Streaming parallel LRAT checker.
///
/// The proof is consumed in batches. For each batch:
///   1. parse                                                     (parallel)
///   2. register additions and stamp deletions                    (parallel)
///   3. check every addition step                                 (parallel)
///   4. drop the clauses the batch deleted                        (parallel)
/// The only single-threaded work per batch is validating one summary per chunk.
///
/// A step only reads the clauses named by its hints, so steps in a batch are independent
/// once the database holds every clause the batch can refer to. Deletions are deferred to
/// the end of the batch; the stamp written in phase 2 lets a step tell that a hint was
/// deleted before it. Memory is the live clause set plus one batch.
///
/// Requirement: addition IDs strictly increase through the proof (true of drat-trim,
/// lrat-trim and CaDiCaL output). Text proofs must hold one step per line.
/// </summary>
public static unsafe class LratChecker
{
    public static CheckResult Check(string cnfPath, string lratPath, CheckOptions? options = null)
    {
        options ??= new CheckOptions();
        int threads = Math.Max(1, options.Threads);
        var po = new ParallelOptions { MaxDegreeOfParallelism = threads };
        var total = Stopwatch.StartNew();
        var parseTime = new Stopwatch();
        var checkTime = new Stopwatch();

        var db = new ClauseDb();
        bool sawEmpty;
        try { CnfParser.Load(cnfPath, db, out sawEmpty); }
        catch (FormatException e) { return new CheckResult { Message = e.Message, TotalTime = total.Elapsed }; }
        long original = db.MaxId;
        var cnfTime = total.Elapsed;

        using var proof = new MappedFile(lratPath);
        byte* start = proof.Ptr;
        long length = proof.Length;
        bool binary = length > 0 && LratBinaryParser.LooksBinary(start, length);

        long lastId = original, additions = 0, deletions = 0, ratSteps = 0;
        string? failure = null;
        var checkers = new ConcurrentBag<StepChecker>();
        int pieces = threads == 1 ? 1 : threads * 4;
        long offset = 0;
        long binaryReparses = 0;
        var sequentialTime = new Stopwatch();
        var registerTime = new Stopwatch();

        while (offset < length && failure is null)
        {
            // 1. parse
            parseTime.Start();
            StepChunk[] chunks;
            if (binary)
            {
                // Binary LRAT has no line breaks to split on, so chunk starts are guessed, the
                // chunks are parsed in parallel, and each guess is then confirmed against where
                // the previous chunk really ended. A chunk with a wrong guess is parsed again.
                long soft = Math.Min(length, offset + Math.Max(1, options.BatchBytes));
                var starts = new long[pieces + 1];
                starts[0] = offset;
                for (int k = 1; k < pieces; k++)
                    starts[k] = Math.Max(starts[k - 1], LratBinaryParser.GuessStepStart(start, offset + (soft - offset) * k / pieces, soft));
                starts[pieces] = soft;
                var parsed = new StepChunk?[pieces];
                var ends = new long[pieces];
                var file = proof;
                long fileLength = length;
                Parallel.For(0, pieces, po, k =>
                {
                    if (starts[k] >= starts[k + 1]) return;
                    var chunk = new StepChunk();
                    byte* b = file.Ptr;
                    ends[k] = LratBinaryParser.Parse(b, b + starts[k], b + starts[k + 1], b + fileLength, chunk) - b;
                    parsed[k] = chunk;
                });
                var list = new List<StepChunk>(pieces);
                long pos = offset;
                for (int k = 0; k < pieces; k++)
                {
                    if (pos >= starts[k + 1]) continue; // an earlier chunk already covered this range
                    StepChunk chunk;
                    if (parsed[k] is { } ready && starts[k] == pos) { chunk = ready; pos = ends[k]; }
                    else
                    {
                        chunk = new StepChunk();
                        pos = LratBinaryParser.Parse(start, start + pos, start + starts[k + 1], start + length, chunk) - start;
                        binaryReparses++;
                    }
                    list.Add(chunk);
                    if (chunk.Error is not null) break;
                }
                offset = pos;
                chunks = list.ToArray();
            }
            else
            {
                long batchEnd = AfterNewline(start, Math.Min(length, offset + Math.Max(1, options.BatchBytes)), length);
                var bounds = new long[pieces + 1];
                bounds[0] = offset;
                for (int k = 1; k < pieces; k++)
                    bounds[k] = Math.Max(bounds[k - 1], AfterNewline(start, offset + (batchEnd - offset) * k / pieces, batchEnd));
                bounds[pieces] = batchEnd;
                chunks = new StepChunk[pieces];
                var file = proof;
                Parallel.For(0, pieces, po, k =>
                {
                    var chunk = new StepChunk();
                    byte* b = file.Ptr;
                    LratTextParser.Parse(b, b + bounds[k], b + bounds[k + 1], chunk);
                    chunks[k] = chunk;
                });
                offset = batchEnd;
            }
            parseTime.Stop();

            // 2a. validate ordering across chunks. This is the only single-threaded work in a
            //     batch and it touches one summary per chunk, not the steps.
            sequentialTime.Start();
            int index = 0;
            long batchAdds = 0;
            foreach (var chunk in chunks)
            {
                chunk.Base = index;
                chunk.PrevAddId = lastId;
                index += chunk.Count;
                if (chunk.AddCount > 0)
                {
                    if (chunk.FirstAddId <= lastId) { failure = IdOrder(chunk.FirstAddId, lastId); break; }
                    if (chunk.BadId != 0) { failure = IdOrder(chunk.BadId, chunk.BadPrev); break; }
                    lastId = chunk.LastAddId;
                    batchAdds += chunk.AddCount;
                    sawEmpty |= chunk.HasEmpty;
                }
                if (chunk.Error is not null) { failure = chunk.Error; break; }
            }
            // Nothing in a batch with a structural failure is checked: the proof is rejected either way.
            if (failure is not null) { sequentialTime.Stop(); break; }
            db.EnsureCapacity(lastId);
            additions += batchAdds;
            sequentialTime.Stop();

            // 2b. register every addition. IDs are distinct, so chunks never write the same slot.
            registerTime.Start();
            Parallel.For(0, chunks.Length, po, k =>
            {
                var chunk = chunks[k];
                long runPage = 0;
                int runCount = 0;
                for (int j = 0; j < chunk.Count; j++)
                    if (chunk.Clauses[j] is { } clause) db.AddConcurrent(chunk.Ids[j], clause, ref runPage, ref runCount);
                db.FlushRun(runPage, runCount);
            });
            db.NoteLiveDelta(batchAdds);

            // 2c. stamp each deleted clause with the index of the earliest step that deletes it.
            //     A deletion only counts if the clause was added before it; since IDs increase
            //     in proof order, that is the same as its ID not exceeding the last ID added
            //     before the deletion step.
            long deleteSteps = 0;
            Parallel.For(0, chunks.Length, po, k =>
            {
                var chunk = chunks[k];
                long last = chunk.PrevAddId;
                int steps = 0;
                for (int j = 0; j < chunk.Count; j++)
                {
                    if (chunk.Clauses[j] is not null) { last = chunk.Ids[j]; continue; }
                    steps++;
                    int stamp = chunk.Base + j;
                    foreach (long r in chunk.RefsOf(j))
                    {
                        if (r > last) continue;
                        var c = db.Get(r);
                        if (c is null) continue;
                        int seen = Volatile.Read(ref c[0]);
                        while (stamp < seen)
                        {
                            int was = Interlocked.CompareExchange(ref c[0], stamp, seen);
                            if (was == seen) break;
                            seen = was;
                        }
                    }
                }
                Interlocked.Add(ref deleteSteps, steps);
            });
            deletions += deleteSteps;
            registerTime.Stop();

            // 3. check additions in parallel
            checkTime.Start();
            int failIndex = int.MaxValue;
            object gate = new();
            Parallel.For(0, chunks.Length, po,
                () => checkers.TryTake(out var c) ? c : new StepChecker(db),
                (k, loop, checker) =>
                {
                    var chunk = chunks[k];
                    for (int j = 0; j < chunk.Count; j++)
                    {
                        var clause = chunk.Clauses[j];
                        if (clause is null) continue;
                        int stepIndex = chunk.Base + j;
                        if (stepIndex > Volatile.Read(ref failIndex)) break;
                        if (checker.Check(chunk.Ids[j], clause, chunk.RefsOf(j), stepIndex)) continue;
                        lock (gate)
                        {
                            if (stepIndex < failIndex)
                            {
                                failIndex = stepIndex;
                                failure = $"step {chunk.Ids[j]}: {checker.Error}";
                            }
                        }
                        break;
                    }
                    return checker;
                },
                checker => checkers.Add(checker));
            checkTime.Stop();
            if (failure is not null) break;

            // 4. drop the clauses the batch deleted
            registerTime.Start();
            long removed = 0;
            Parallel.For(0, chunks.Length, po, k =>
            {
                var chunk = chunks[k];
                long count = 0;
                for (int j = 0; j < chunk.Count; j++)
                {
                    if (chunk.Clauses[j] is not null) continue;
                    foreach (long r in chunk.RefsOf(j))
                    {
                        var c = db.Get(r);
                        if (c is not null && c[0] != ClauseDb.Live && db.RemoveConcurrent(r)) count++;
                    }
                }
                Interlocked.Add(ref removed, count);
            });
            db.NoteLiveDelta(-removed);
            registerTime.Stop();
        }

        foreach (var c in checkers) ratSteps += c.RatSteps;
        bool verified = failure is null && sawEmpty;
        return new CheckResult
        {
            Verified = verified,
            Message = failure ?? (sawEmpty ? "all steps check and the empty clause is derived"
                                           : "all steps check but the empty clause is never derived"),
            OriginalClauses = original,
            Additions = additions,
            Deletions = deletions,
            RatSteps = ratSteps,
            PeakLiveClauses = db.PeakLiveCount,
            Binary = binary,
            ParseTime = parseTime.Elapsed,
            CheckTime = checkTime.Elapsed,
            SequentialTime = sequentialTime.Elapsed,
            RegisterTime = registerTime.Elapsed,
            CnfTime = cnfTime,
            BinaryReparses = binaryReparses,
            TotalTime = total.Elapsed,
        };
    }

    private static string IdOrder(long id, long previous) =>
        $"step {id}: clause IDs must strictly increase (previous was {previous})";

    /// <summary>The first offset at or after <paramref name="off"/> that starts a line.</summary>
    private static long AfterNewline(byte* b, long off, long limit)
    {
        if (off <= 0) return 0;
        while (off < limit && b[off - 1] != '\n') off++;
        return Math.Min(off, limit);
    }
}
