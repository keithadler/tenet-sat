using System.Text;

namespace TenetSat.Tests;

/// <summary>The parallel formula loader must produce exactly what the sequential one does.</summary>
public class CnfParallelTests
{
    private static (List<int[]?> Clauses, bool Empty, string? Error) Load(string path, int threads)
    {
        var db = new ClauseDb();
        try
        {
            bool empty;
            if (threads == 0) CnfParser.Load(path, db, out empty);
            else CnfParser.Load(path, db, threads, out empty, minParallelBytes: 0);
            var list = new List<int[]?>();
            for (long id = 1; id <= db.MaxId; id++) { var c = db.Get(id); list.Add(c.IsNull ? null : c.ToArray()); }
            return (list, empty, null);
        }
        catch (FormatException e) { return (new(), false, e.Message); }
    }

    private static void Same(string cnf)
    {
        using var dir = new TempDir();
        string f = dir.File("f.cnf", cnf);
        var expected = Load(f, 0);
        foreach (int threads in new[] { 2, 3, 7, 16, 64 })
        {
            var got = Load(f, threads);
            Assert.Equal(expected.Error, got.Error);
            Assert.Equal(expected.Empty, got.Empty);
            Assert.Equal(expected.Clauses.Count, got.Clauses.Count);
            for (int i = 0; i < expected.Clauses.Count; i++) Assert.Equal(expected.Clauses[i], got.Clauses[i]);
        }
    }

    [Fact]
    public void RandomFormulasWithAwkwardLayout()
    {
        var rng = new Random(7);
        for (int round = 0; round < 300; round++)
        {
            var sb = new StringBuilder();
            if (rng.Next(2) == 0) sb.Append("c a comment\n");
            sb.Append("p cnf 50 0\n");
            int clauses = rng.Next(0, 60);
            for (int c = 0; c < clauses; c++)
            {
                int len = rng.Next(0, 7);
                for (int k = 0; k < len; k++)
                {
                    int v = rng.Next(1, 51) * (rng.Next(2) == 0 ? 1 : -1);
                    sb.Append(v).Append(rng.Next(5) == 0 ? "\n" : rng.Next(4) == 0 ? "  \t" : " ");
                }
                sb.Append('0');
                int tail = rng.Next(6);
                if (tail == 0) sb.Append(" c trailing comment\n");
                else if (tail == 1) sb.Append("\nc comment line\n");
                else if (tail == 2) sb.Append(' ');
                else sb.Append('\n');
            }
            Same(sb.ToString());
        }
    }

    [Fact]
    public void LargeFormulaSplitsCleanly()
    {
        var rng = new Random(3);
        var clauses = Gen.Random3Sat(rng, 2000, 200_000);
        Same(Gen.Cnf(clauses, 2000));
    }

    [Theory]
    [InlineData("p cnf 2 1\n1 2\n")]                 // unterminated last clause
    [InlineData("p cnf 2 2\n1 2 0\n1 x 0\n")]        // garbage token
    [InlineData("p cnf 2 2\n1\nc mid-clause comment\n2 0\n")]
    [InlineData("p cnf 2 1\n1 2 0\n%\n0\n")]          // SATLIB end marker
    [InlineData("p cnf 2 1\n2000000000 0\n")]        // variable out of range
    [InlineData("")]
    [InlineData("0\n0\n0\n")]
    public void EdgeCases(string cnf) => Same(cnf);
}
