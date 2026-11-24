using TenetSat;

const string Usage = """
    usage:
      tenetsat check <formula.cnf> <proof.lrat> [--threads N] [--batch-mb M]
      tenetsat drat2lrat <formula.cnf> <proof.drat> <out.lrat>     (slow reference converter, text DRAT, RUP only)

    exit codes: 0 verified, 1 not verified, 2 usage or I/O error
    """;

if (args.Length < 3) { Console.Error.WriteLine(Usage); return 2; }

try
{
    switch (args[0])
    {
        case "check":
        {
            int threads = Environment.ProcessorCount;
            long batchMb = 32;
            for (int i = 3; i < args.Length; i++)
            {
                if (args[i] == "--threads" && i + 1 < args.Length) threads = int.Parse(args[++i]);
                else if (args[i] == "--batch-mb" && i + 1 < args.Length) batchMb = long.Parse(args[++i]);
                else { Console.Error.WriteLine(Usage); return 2; }
            }
            var r = LratChecker.Check(args[1], args[2], new CheckOptions { Threads = threads, BatchBytes = batchMb << 20 });
            Console.WriteLine($"c format            {(r.Binary ? "binary" : "text")} LRAT");
            Console.WriteLine($"c threads           {threads}");
            Console.WriteLine($"c original clauses  {r.OriginalClauses}");
            Console.WriteLine($"c additions         {r.Additions} ({r.RatSteps} RAT)");
            Console.WriteLine($"c deletion steps    {r.Deletions}");
            Console.WriteLine($"c peak live clauses {r.PeakLiveClauses}");
            Console.WriteLine($"c parse {r.ParseTime.TotalSeconds:F3}s  register {r.RegisterTime.TotalSeconds:F3}s  check {r.CheckTime.TotalSeconds:F3}s  sequential {r.SequentialTime.TotalSeconds:F3}s  cnf {r.CnfTime.TotalSeconds:F3}s  total {r.TotalTime.TotalSeconds:F3}s");
            if (r.Binary) Console.WriteLine($"c binary reparses   {r.BinaryReparses}");
            Console.WriteLine($"c {r.Message}");
            Console.WriteLine(r.Verified ? "s VERIFIED" : "s NOT VERIFIED");
            return r.Verified ? 0 : 1;
        }
        case "drat2lrat" when args.Length == 4:
        {
            using var output = new StreamWriter(args[3]);
            NaiveDratToLrat.Convert(args[1], args[2], output);
            return 0;
        }
        default:
            Console.Error.WriteLine(Usage);
            return 2;
    }
}
catch (Exception e) when (e is IOException or FormatException or NotSupportedException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 2;
}
