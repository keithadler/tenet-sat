using System.Text;

namespace TenetSat.Tests;

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("tenetsat-").FullName;
    public string File(string name, string content)
    {
        string p = System.IO.Path.Combine(Path, name);
        System.IO.File.WriteAllText(p, content);
        return p;
    }
    public string File(string name, byte[] content)
    {
        string p = System.IO.Path.Combine(Path, name);
        System.IO.File.WriteAllBytes(p, content);
        return p;
    }
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

internal static class Gen
{
    public static List<int[]> Random3Sat(Random rng, int vars, int clauses)
    {
        var result = new List<int[]>();
        for (int i = 0; i < clauses; i++)
        {
            var c = new int[3];
            for (int k = 0; k < 3; k++)
            {
                int v;
                do v = rng.Next(1, vars + 1); while (Array.Exists(c, x => Math.Abs(x) == v));
                c[k] = rng.Next(2) == 0 ? v : -v;
            }
            result.Add(c);
        }
        return result;
    }

    public static string Cnf(List<int[]> clauses, int vars)
    {
        var sb = new StringBuilder($"p cnf {vars} {clauses.Count}\n");
        foreach (var c in clauses) sb.Append(string.Join(' ', c)).Append(" 0\n");
        return sb.ToString();
    }

    /// <summary>
    /// Plain DPLL that writes a DRAT proof: each refuted branch adds the negation of its
    /// decisions. Returns null if the formula is satisfiable.
    /// </summary>
    public static string? DpllDrat(List<int[]> clauses, int vars)
    {
        var proof = new StringBuilder();
        var decisions = new List<int>();
        return Refute() ? proof.ToString() : null;

        bool Refute()
        {
            var value = new sbyte[vars + 1];
            foreach (int d in decisions) value[Math.Abs(d)] = (sbyte)(d > 0 ? 1 : -1);
            bool conflict = false, changed = true;
            while (changed && !conflict)
            {
                changed = false;
                foreach (var c in clauses)
                {
                    int unit = 0, free = 0;
                    bool sat = false;
                    foreach (int l in c)
                    {
                        sbyte v = value[Math.Abs(l)];
                        if (v == 0) { free++; unit = l; }
                        else if ((v > 0) == (l > 0)) { sat = true; break; }
                    }
                    if (sat) continue;
                    if (free == 0) { conflict = true; break; }
                    if (free == 1) { value[Math.Abs(unit)] = (sbyte)(unit > 0 ? 1 : -1); changed = true; }
                }
            }
            if (!conflict)
            {
                int pick = 0;
                for (int v = 1; v <= vars && pick == 0; v++) if (value[v] == 0) pick = v;
                if (pick == 0) return false; // satisfying assignment
                foreach (int d in new[] { pick, -pick })
                {
                    decisions.Add(d);
                    bool refuted = Refute();
                    decisions.RemoveAt(decisions.Count - 1);
                    if (!refuted) return false;
                }
            }
            proof.Append(string.Join(' ', decisions.Select(d => -d))).Append(decisions.Count > 0 ? " 0\n" : "0\n");
            return true;
        }
    }

    public static byte[] ToBinaryLrat(string text)
    {
        var ms = new MemoryStream();
        void Num(long x)
        {
            ulong u = 2 * (ulong)Math.Abs(x) + (x < 0 ? 1UL : 0UL);
            while (u >= 0x80) { ms.WriteByte((byte)(u | 0x80)); u >>= 7; }
            ms.WriteByte((byte)u);
        }
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (t[1] == "d")
            {
                ms.WriteByte((byte)'d');
                for (int i = 2; i < t.Length; i++) Num(long.Parse(t[i]));
            }
            else
            {
                ms.WriteByte((byte)'a');
                foreach (string s in t) Num(long.Parse(s));
            }
        }
        return ms.ToArray();
    }
}

/// <summary>
/// Deliberately plain sequential LRAT checker with the same acceptance rules as the real
/// one, written independently (dictionary + hash set) to serve as the fuzzing oracle.
/// </summary>
internal static class ReferenceChecker
{
    public static bool Check(List<int[]> cnf, string lrat)
    {
        var db = new Dictionary<long, int[]>();
        bool empty = false;
        for (int i = 0; i < cnf.Count; i++) { db[i + 1] = cnf[i]; empty |= cnf[i].Length == 0; }
        long lastId = cnf.Count;

        foreach (string raw in lrat.Split('\n'))
        {
            var t = raw.Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
            int pos = 0;
            while (pos < t.Length)
            {
                if (!Long(t[pos++], out long id) || id <= 0) return false;
                if (pos < t.Length && t[pos] == "d")
                {
                    pos++;
                    while (true)
                    {
                        if (pos >= t.Length || !Long(t[pos++], out long r) || r < 0) return false;
                        if (r == 0) break;
                        db.Remove(r);
                    }
                    continue;
                }
                var lits = new List<int>();
                while (true)
                {
                    if (pos >= t.Length || !Long(t[pos++], out long v) || Math.Abs(v) >= 1 << 30) return false;
                    if (v == 0) break;
                    lits.Add((int)v);
                }
                var hints = new List<long>();
                while (true)
                {
                    if (pos >= t.Length || !Long(t[pos++], out long h)) return false;
                    if (h == 0) break;
                    hints.Add(h);
                }
                if (id <= lastId) return false;
                lastId = id;
                if (!Step(db, lits, hints)) return false;
                db[id] = lits.ToArray();
                empty |= lits.Count == 0;
            }
        }
        return empty;
    }

    private static bool Long(string s, out long v)
    {
        v = 0;
        string digits = s.StartsWith('-') ? s[1..] : s;
        return digits.Length is > 0 and <= 18 && digits.All(char.IsAsciiDigit) && long.TryParse(s, out v);
    }

    private static bool Step(Dictionary<long, int[]> db, List<int> lits, List<long> hints)
    {
        var truth = new HashSet<int>();
        foreach (int l in lits)
        {
            if (truth.Contains(l)) return true;
            truth.Add(-l);
        }
        int i = 0;
        for (; i < hints.Count && hints[i] > 0; i++)
        {
            int r = Propagate(db, truth, hints[i]);
            if (r == 0) return true;
            if (r < 0) return false;
        }
        if (lits.Count == 0) return false;
        int negPivot = -lits[0];
        var listed = hints.Skip(i).Where(h => h < 0).Select(h => -h).ToHashSet();
        foreach (var (cid, c) in db)
            if (c.Contains(negPivot) && !listed.Contains(cid)) return false;
        while (i < hints.Count)
        {
            long cid = -hints[i++];
            if (!db.TryGetValue(cid, out var cand)) return false;
            var local = new HashSet<int>(truth);
            bool conflict = false;
            foreach (int l in cand)
            {
                if (l == negPivot) continue;
                if (local.Contains(l)) { conflict = true; break; }
                local.Add(-l);
            }
            for (; i < hints.Count && hints[i] > 0; i++)
            {
                if (conflict) continue;
                int r = Propagate(db, local, hints[i]);
                if (r < 0) return false;
                if (r == 0) conflict = true;
            }
            if (!conflict) return false;
        }
        return true;
    }

    /// <summary>0 conflict, 1 unit, -1 unusable hint.</summary>
    private static int Propagate(Dictionary<long, int[]> db, HashSet<int> truth, long hint)
    {
        if (!db.TryGetValue(hint, out var c)) return -1;
        int unit = 0;
        foreach (int l in c)
        {
            if (truth.Contains(-l)) continue;
            if (truth.Contains(l)) return -1;
            if (unit != 0 && unit != l) return -1;
            unit = l;
        }
        if (unit == 0) return 0;
        truth.Add(unit);
        return 1;
    }
}
