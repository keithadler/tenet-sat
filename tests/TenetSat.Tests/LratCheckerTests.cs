namespace TenetSat.Tests;

public class LratCheckerTests
{
    private static bool Verify(string cnf, string lrat, int threads = 4, long batchBytes = 1 << 20)
    {
        using var dir = new TempDir();
        string f = dir.File("f.cnf", cnf), p = dir.File("p.lrat", lrat);
        var mapped = LratChecker.Check(f, p, new CheckOptions { Threads = threads, BatchBytes = batchBytes });
        // Reading the proof with file reads must give exactly the same answer as the memory map.
        var read = LratChecker.Check(f, p, new CheckOptions { Threads = threads, BatchBytes = batchBytes, ReadIntoBuffer = true });
        Assert.Equal(mapped.Verified, read.Verified);
        Assert.Equal(mapped.Message, read.Message);
        return mapped.Verified;
    }

    private const string XAndNotX = "p cnf 1 2\n1 0\n-1 0\n";

    [Fact]
    public void AcceptsTrivialRefutation() => Assert.True(Verify(XAndNotX, "3 0 1 2 0\n"));

    [Fact]
    public void RejectsMissingHint() => Assert.False(Verify(XAndNotX, "3 0 1 0\n"));

    [Fact]
    public void RejectsProofWithoutEmptyClause() => Assert.False(Verify("p cnf 2 2\n1 2 0\n-1 2 0\n", "3 2 0 1 2 0\n"));

    [Fact]
    public void RejectsEmptyProof() => Assert.False(Verify(XAndNotX, ""));

    [Fact]
    public void AcceptsFormulaContainingEmptyClause() => Assert.True(Verify("p cnf 1 1\n0\n", ""));

    [Fact]
    public void RejectsHintDeletedEarlier() => Assert.False(Verify(XAndNotX, "2 d 1 0\n3 0 1 2 0\n"));

    [Fact]
    public void AcceptsHintDeletedLater() => Assert.True(Verify(XAndNotX, "3 0 1 2 0\n3 d 1 0\n"));

    [Fact]
    public void RejectsHintAddedLater() =>
        Assert.False(Verify("p cnf 2 3\n1 2 0\n-1 2 0\n-2 0\n", "4 0 5 3 0\n5 2 0 1 2 0\n"));

    [Fact]
    public void RejectsNonIncreasingIds() =>
        Assert.False(Verify("p cnf 2 3\n1 2 0\n-1 2 0\n-2 0\n", "5 2 0 1 2 0\n4 0 5 3 0\n"));

    [Fact]
    public void RejectsNonUnitHint() =>
        Assert.False(Verify("p cnf 2 3\n1 2 0\n-1 2 0\n-2 0\n", "4 0 1 3 0\n"));

    // A hint that is already satisfied adds nothing and is skipped, as lrat-check, lrat-trim
    // and cake_lpr do. CaDiCaL wrote one into a real 2.1 GB proof (SAT Competition 2023).
    [Fact]
    public void SkipsSatisfiedHint() => Assert.True(Verify("p cnf 2 3\n1 0\n-1 0\n1 2 0\n", "4 0 1 3 2 0\n"));

    [Fact]
    public void RejectsHintWithTwoFreeLiterals() => Assert.False(Verify("p cnf 2 3\n1 0\n-1 0\n1 2 0\n", "4 0 3 1 2 0\n"));

    /// <summary>A link's own size is the length of its target path, not of the file: read the file.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(5000)]
    public void FollowsSymbolicLinks(int padding)
    {
        if (OperatingSystem.IsWindows()) return; // creating links there needs a privilege
        using var dir = new TempDir();
        string pad = padding == 0 ? "" : "c " + new string('x', padding) + "\n";
        dir.File("f.cnf", pad + XAndNotX);
        dir.File("p.lrat", pad + "3 0 1 2 0\n");
        string cnf = System.IO.Path.Combine(dir.Path, "link.cnf"), lrat = System.IO.Path.Combine(dir.Path, "link.lrat");
        File.CreateSymbolicLink(cnf, "f.cnf");
        File.CreateSymbolicLink(lrat, "p.lrat");
        using (var mapped = new MappedFile(cnf)) Assert.Equal(pad.Length + XAndNotX.Length, mapped.Length);
        foreach (bool read in new[] { false, true })
        {
            var r = LratChecker.Check(cnf, lrat, new CheckOptions { ReadIntoBuffer = read });
            Assert.True(r.Verified, r.Message);
        }
    }

    [Fact]
    public void RejectsMalformedProof() => Assert.False(Verify(XAndNotX, "3 0 1 2\n"));

    [Fact]
    public void RejectsStepSplitAcrossLines() => Assert.False(Verify(XAndNotX, "3 0 1\n2 0\n"));

    // x=1, b=2, a=3.  Clause 3 is "1 2", RAT on pivot 1 against clause 1 ("-1 3").
    private const string RatCnf = "p cnf 3 2\n-1 3 0\n3 2 0\n";

    [Fact]
    public void AcceptsRatStep()
    {
        using var dir = new TempDir();
        var r = LratChecker.Check(dir.File("f.cnf", RatCnf + "0\n"), dir.File("p.lrat", "4 1 2 0 -1 2 0\n"));
        Assert.True(r.Verified, r.Message);
        Assert.Equal(1, r.RatSteps);
    }

    [Fact]
    public void RejectsRatStepWithUnlistedCandidate() => Assert.False(Verify(RatCnf + "0\n", "4 1 2 0 0\n"));

    [Fact]
    public void AcceptsRatOnFreshVariable() => Assert.True(Verify(RatCnf + "0\n", "4 7 0 0\n"));

    [Fact]
    public void BinaryMatchesText()
    {
        using var dir = new TempDir();
        string cnf = dir.File("f.cnf", "p cnf 2 3\n1 2 0\n-1 2 0\n-2 0\n");
        string good = "4 2 0 1 2 0\n4 d 1 2 0\n5 0 4 3 0\n";
        string bad = "4 2 0 1 2 0\n4 d 4 0\n5 0 4 3 0\n";
        foreach (long batchBytes in new[] { 1L, 5L, 1L << 20 })
        {
            var options = new CheckOptions { Threads = 2, BatchBytes = batchBytes };
            var ok = LratChecker.Check(cnf, dir.File("good.lrat", Gen.ToBinaryLrat(good)), options);
            Assert.True(ok.Binary);
            Assert.True(ok.Verified, ok.Message);
            Assert.False(LratChecker.Check(cnf, dir.File("bad.lrat", Gen.ToBinaryLrat(bad)), options).Verified);
        }
    }

    /// <summary>
    /// Every step's first hint is clause 50, which encodes as the byte 'd' right after the
    /// zero that ends the literals. That is exactly what a guessed step start looks like, so
    /// the parallel binary parse guesses wrong here and has to recover.
    /// </summary>
    [Fact]
    public void BinaryParseRecoversFromWrongGuesses()
    {
        using var dir = new TempDir();
        var cnf = new System.Text.StringBuilder("p cnf 60 51\n");
        for (int i = 0; i < 49; i++) cnf.Append($"{i + 2} {i + 3} 0\n");
        cnf.Append("1 0\n-1 0\n");
        var good = new System.Text.StringBuilder();
        for (int id = 52; id < 400; id++) good.Append($"{id} 1 0 50 0\n");
        good.Append("400 0 50 51 0\n");
        string bad = good.ToString().Replace("300 1 0 50 0", "300 -1 0 50 0");
        string cnfPath = dir.File("f.cnf", cnf.ToString());
        long reparses = 0;
        foreach (int threads in new[] { 1, 2, 8 })
            foreach (long batch in new[] { 1L, 13L, 200L, 1L << 20 })
            {
                var options = new CheckOptions { Threads = threads, BatchBytes = batch };
                var ok = LratChecker.Check(cnfPath, dir.File("good.lrat", Gen.ToBinaryLrat(good.ToString())), options);
                Assert.True(ok.Verified, $"threads={threads} batch={batch}: {ok.Message}");
                Assert.Equal(349, ok.Additions);
                reparses += ok.BinaryReparses;
                Assert.False(LratChecker.Check(cnfPath, dir.File("bad.lrat", Gen.ToBinaryLrat(bad)), options).Verified);
            }
        Assert.True(reparses > 0, "the test never exercised a wrong guess");
    }

    /// <summary>
    /// End to end on random unsatisfiable 3-SAT: DPLL writes DRAT, the naive converter makes
    /// LRAT, and the checker must accept it at every thread count and batch size. Then each
    /// proof is corrupted many ways and the verdict must match the sequential reference checker.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void FuzzAgainstReference(int seed)
    {
        var rng = new Random(seed);
        using var dir = new TempDir();
        int proofs = 0, rejected = 0, accepted = 0;
        while (proofs < 6)
        {
            int vars = rng.Next(8, 16);
            var clauses = Gen.Random3Sat(rng, vars, vars * 6);
            string? drat = Gen.DpllDrat(clauses, vars);
            if (drat is null) continue;
            proofs++;

            string cnfPath = dir.File("f.cnf", Gen.Cnf(clauses, vars));
            var writer = new StringWriter { NewLine = "\n" };
            NaiveDratToLrat.Convert(cnfPath, dir.File("p.drat", drat), writer);
            string lrat = writer.ToString();
            Assert.True(ReferenceChecker.Check(clauses, lrat), "reference rejects a genuine proof");

            var configs = new[] { (1, 1L), (1, 1L << 20), (4, 1L), (4, 97L), (8, 1L << 20) };
            foreach (var (threads, batch) in configs)
            {
                var r = LratChecker.Check(cnfPath, dir.File("p.lrat", lrat), new CheckOptions { Threads = threads, BatchBytes = batch });
                Assert.True(r.Verified, $"threads={threads} batch={batch}: {r.Message}");
                var rb = LratChecker.Check(cnfPath, dir.File("p.blrat", Gen.ToBinaryLrat(lrat)), new CheckOptions { Threads = threads, BatchBytes = batch });
                Assert.True(rb.Binary && rb.Verified && rb.Additions == r.Additions, $"binary threads={threads} batch={batch}: {rb.Message}");
            }

            for (int m = 0; m < 150; m++)
            {
                string mutant = Mutate(lrat, rng);
                bool expected = ReferenceChecker.Check(clauses, mutant);
                if (expected) accepted++; else rejected++;
                string path = dir.File("m.lrat", mutant);
                foreach (var (threads, batch) in configs)
                {
                    var r = LratChecker.Check(cnfPath, path, new CheckOptions { Threads = threads, BatchBytes = batch });
                    Assert.True(expected == r.Verified,
                        $"verdict mismatch (reference={expected}, threads={threads}, batch={batch}, {r.Message})\n{Gen.Cnf(clauses, vars)}\n{mutant}");
                }
            }
        }
        // The mutations must actually exercise both verdicts.
        Assert.True(rejected > 100 && accepted > 0, $"rejected={rejected} accepted={accepted}");
    }

    private static string Mutate(string lrat, Random rng)
    {
        var lines = lrat.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        int li = rng.Next(lines.Count);
        var tokens = lines[li].Split(' ').ToList();
        int ti = rng.Next(tokens.Count);
        switch (rng.Next(9))
        {
            case 0: tokens.RemoveAt(ti); break;                                     // drop a token
            case 1: tokens[ti] = rng.Next(-20, 21).ToString(); break;               // small literal / id
            case 2: tokens[ti] = rng.Next(1, 400).ToString(); break;                // plausible hint id
            case 3: tokens.Insert(ti, rng.Next(-20, 400).ToString()); break;        // extra token
            case 4: lines.RemoveAt(li); return Join(lines);                         // drop a step
            case 5:                                                                 // swap two steps
                int lj = rng.Next(lines.Count);
                (lines[li], lines[lj]) = (lines[lj], lines[li]);
                return Join(lines);
            case 6: lines.Insert(li, $"{tokens[0]} d {rng.Next(1, 400)} 0"); return Join(lines); // early deletion
            case 7: if (ti + 1 < tokens.Count) (tokens[ti], tokens[ti + 1]) = (tokens[ti + 1], tokens[ti]); break;
            case 8: tokens[ti] = "x"; break;                                        // garbage
        }
        lines[li] = string.Join(' ', tokens);
        return Join(lines);

        static string Join(List<string> l) => string.Join('\n', l) + "\n";
    }
}
