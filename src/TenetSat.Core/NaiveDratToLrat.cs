using System.Text;

namespace TenetSat;

/// <summary>
/// Reference DRAT -> LRAT converter: forward, single-threaded, no watch lists, no trimming,
/// RUP lemmas only, text DRAT only. It exists to make test proofs without external tools and
/// as the baseline that the real Phase 2 converter has to beat. Do not benchmark with it.
/// </summary>
public static unsafe class NaiveDratToLrat
{
    private sealed class Entry { public long Id; public int[] Lits = []; public bool Dead; }

    public static void Convert(string cnfPath, string dratPath, TextWriter output)
    {
        var entries = new List<Entry>();
        var byKey = new Dictionary<string, Stack<Entry>>();
        int maxVar = 0;

        void Add(long id, int[] lits)
        {
            var e = new Entry { Id = id, Lits = lits };
            entries.Add(e);
            string key = Key(lits);
            if (!byKey.TryGetValue(key, out var stack)) byKey[key] = stack = new Stack<Entry>();
            stack.Push(e);
            foreach (int l in lits) maxVar = Math.Max(maxVar, Math.Abs(l));
        }

        using (var cnf = new MappedFile(cnfPath))
            CnfParser.Load(cnf.Ptr, cnf.Length, (id, c) => Add(id, c.AsSpan(1).ToArray()), out _);
        long nextId = entries.Count + 1;
        long lastId = entries.Count;
        int dead = 0;

        foreach (string raw in File.ReadLines(dratPath))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == 'c') continue;
            bool delete = line[0] == 'd';
            var tokens = (delete ? line[1..] : line).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 || tokens[^1] != "0") throw new FormatException($"Malformed DRAT line: {raw}");
            int[] lits = new int[tokens.Length - 1];
            for (int i = 0; i < lits.Length; i++) lits[i] = int.Parse(tokens[i]);

            if (delete)
            {
                if (byKey.TryGetValue(Key(lits), out var stack) && stack.Count > 0)
                {
                    var e = stack.Pop();
                    e.Dead = true;
                    output.WriteLine($"{lastId} d {e.Id} 0");
                    if (++dead > entries.Count / 2) { entries.RemoveAll(x => x.Dead); dead = 0; }
                }
                continue;
            }

            foreach (int l in lits) maxVar = Math.Max(maxVar, Math.Abs(l));
            var hints = Derive(entries, lits, maxVar)
                ?? throw new NotSupportedException($"Lemma '{line}' is not RUP (RAT lemmas are not supported by the naive converter).");
            long id = nextId++;
            var sb = new StringBuilder();
            sb.Append(id);
            foreach (int l in lits) sb.Append(' ').Append(l);
            sb.Append(" 0");
            foreach (long h in hints) sb.Append(' ').Append(h);
            sb.Append(" 0");
            output.WriteLine(sb.ToString());
            lastId = id;
            Add(id, lits);
            if (lits.Length == 0) return;
        }
    }

    private static string Key(int[] lits)
    {
        var sorted = (int[])lits.Clone();
        Array.Sort(sorted);
        return string.Join(' ', sorted);
    }

    /// <summary>Hints showing the lemma by unit propagation, or null if it is not RUP.</summary>
    private static List<long>? Derive(List<Entry> entries, int[] lemma, int maxVar)
    {
        var value = new sbyte[maxVar + 1];           // +1 true, -1 false
        var trail = new List<(int Var, Entry? Reason)>();
        foreach (int l in lemma)
        {
            int v = Math.Abs(l);
            sbyte want = (sbyte)(l > 0 ? -1 : 1);
            if (value[v] == -want) return [];        // tautology: nothing to show
            if (value[v] == 0) { value[v] = want; trail.Add((v, null)); }
        }

        Entry? conflict = null;
        bool changed = true;
        while (changed && conflict is null)
        {
            changed = false;
            foreach (var e in entries)
            {
                if (e.Dead) continue;
                int unit = 0;
                bool skip = false;
                foreach (int l in e.Lits)
                {
                    sbyte val = value[Math.Abs(l)];
                    if (val == 0)
                    {
                        if (unit != 0 && unit != l) { skip = true; break; }
                        unit = l;
                    }
                    else if ((val > 0) == (l > 0)) { skip = true; break; }
                }
                if (skip) continue;
                if (unit == 0) { conflict = e; break; }
                value[Math.Abs(unit)] = (sbyte)(unit > 0 ? 1 : -1);
                trail.Add((Math.Abs(unit), e));
                changed = true;
            }
        }
        if (conflict is null) return null;

        // Keep only the reasons the conflict depends on, in propagation order.
        var needed = new bool[maxVar + 1];
        foreach (int l in conflict.Lits) needed[Math.Abs(l)] = true;
        var hints = new List<long> { conflict.Id };
        for (int i = trail.Count - 1; i >= 0; i--)
        {
            var (v, reason) = trail[i];
            if (!needed[v] || reason is null) continue;
            hints.Add(reason.Id);
            foreach (int l in reason.Lits) needed[Math.Abs(l)] = true;
        }
        hints.Reverse();
        return hints;
    }
}
