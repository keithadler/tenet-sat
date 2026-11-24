using System.Runtime.CompilerServices;

namespace TenetSat;

/// <summary>
/// Checks single LRAT addition steps. One instance per worker thread; it only reads
/// the shared <see cref="ClauseDb"/>.
///
/// Every hint must be a clause that is live at this step. Under the current assignment it
/// must be unit or falsified, or satisfied: a satisfied hint adds nothing and is skipped,
/// as lrat-check, lrat-trim and cake_lpr do (CaDiCaL proofs contain such hints). A hint
/// with two unassigned literals and no true one rejects the step.
/// </summary>
internal sealed class StepChecker
{
    private enum Hint { Unit, Conflict, Satisfied, Bad }

    private readonly ClauseDb _db;
    private int[] _mark = new int[4096];   // _mark[code(lit)] == _gen  <=>  lit is true
    private int _gen;
    private int[] _trail = new int[256];   // codes assigned in the current step, in order
    private int _trailLen;
    private readonly HashSet<long> _candidates = new();

    public string? Error;
    public long RatSteps;

    public StepChecker(ClauseDb db) => _db = db;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Code(int lit) => lit > 0 ? lit << 1 : (-lit << 1) | 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsTrue(int lit)
    {
        int c = Code(lit);
        var m = _mark;
        return c < m.Length && m[c] == _gen;
    }

    private void Assign(int lit)
    {
        int c = Code(lit);
        if (c >= _mark.Length) Array.Resize(ref _mark, Math.Max(c + 1, _mark.Length * 2));
        _mark[c] = _gen;
        if (_trailLen == _trail.Length) Array.Resize(ref _trail, _trailLen * 2);
        _trail[_trailLen++] = c;
    }

    /// <summary>A clause usable by the step with this ID at this batch-local index, or null.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Clause LiveAt(long hintId, long stepId, int stepIndex)
    {
        if (hintId >= stepId) return default;          // IDs increase, so this means "added later"
        var c = _db.Get(hintId);
        if (c.IsNull || c[0] <= stepIndex) return default; // never existed, or deleted earlier
        return c;
    }

    private Hint Propagate(long hintId, long stepId, int stepIndex)
    {
        var c = LiveAt(hintId, stepId, stepIndex);
        if (c.IsNull) { Error = $"hint {hintId} is not a live clause"; return Hint.Bad; }
        int unit = 0;
        bool twoFree = false;
        int len = c.Length;
        for (int k = 1; k < len; k++)
        {
            int l = c[k];
            if (IsTrue(-l)) continue;
            if (IsTrue(l)) return Hint.Satisfied; // adds nothing; skipping it cannot help a step pass
            if (unit != 0 && unit != l) twoFree = true;
            else unit = l;
        }
        if (twoFree) { Error = $"hint {hintId} is not unit"; return Hint.Bad; }
        if (unit == 0) return Hint.Conflict;
        Assign(unit);
        return Hint.Unit;
    }

    public bool Check(long id, Clause clause, ReadOnlySpan<long> hints, int stepIndex)
    {
        if (++_gen == int.MaxValue) { Array.Clear(_mark); _gen = 1; }
        _trailLen = 0;

        // Assume the negation of the clause. A tautology is trivially implied.
        for (int k = 1; k < clause.Length; k++)
        {
            int l = clause[k];
            if (IsTrue(l)) return true;
            Assign(-l);
        }

        int i = 0;
        for (; i < hints.Length && hints[i] > 0; i++)
        {
            switch (Propagate(hints[i], id, stepIndex))
            {
                case Hint.Conflict: return true;
                case Hint.Bad: return false;
            }
        }

        // No conflict from the plain hints: this must be a RAT step on the first literal.
        if (clause.Length == 1) { Error = "hints do not derive the empty clause"; return false; }
        RatSteps++;
        int negPivot = -clause[1];

        _candidates.Clear();
        for (int k = i; k < hints.Length; k++)
            if (hints[k] < 0) _candidates.Add(-hints[k]);

        // Every live clause containing the negated pivot must be listed as a candidate.
        // This scans the whole database, so RAT-heavy proofs are slow here.
        long missing = 0;
        bool complete = _db.All(id, (cid, c) =>
        {
            if (c[0] <= stepIndex) return true;
            for (int k = 1; k < c.Length; k++)
                if (c[k] == negPivot)
                {
                    if (_candidates.Contains(cid)) return true;
                    missing = cid;
                    return false;
                }
            return true;
        });
        if (!complete) { Error = $"RAT check: clause {missing} contains the negated pivot but is not listed"; return false; }

        int baseTrail = _trailLen;
        while (i < hints.Length)
        {
            long cid = -hints[i++];
            var c = LiveAt(cid, id, stepIndex);
            if (c.IsNull) { Error = $"RAT candidate {cid} is not a live clause"; return false; }

            bool conflict = false;
            for (int k = 1; k < c.Length; k++)
            {
                int l = c[k];
                if (l == negPivot) continue;
                if (IsTrue(l)) { conflict = true; break; } // resolvent already satisfied
                if (!IsTrue(-l)) Assign(-l);
            }
            for (; i < hints.Length && hints[i] > 0; i++)
            {
                if (conflict) continue;
                switch (Propagate(hints[i], id, stepIndex))
                {
                    case Hint.Conflict: conflict = true; break;
                    case Hint.Bad: return false;
                }
            }
            if (!conflict) { Error = $"RAT candidate {cid}: hints do not reach a conflict"; return false; }

            while (_trailLen > baseTrail) _mark[_trail[--_trailLen]] = 0;
        }
        return true;
    }
}
