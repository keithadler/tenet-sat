# Benchmark run, 2026-11-24, AWS c7a.48xlarge

Raw data behind [docs/PERFORMANCE.md](../../docs/PERFORMANCE.md). Times are wall-clock seconds for the whole
process unless a line says otherwise.

## Machine

- AWS c7a.48xlarge, on-demand, us-east-2: 2 x AMD EPYC 9R14 (96 cores each, no SMT, 192 cores), 2 NUMA nodes,
  369 GiB RAM, 400 GB gp3 root disk (6000 IOPS, 500 MB/s).
- Ubuntu 24.04 (kernel 7.0.0-1018-aws). Transparent huge pages `madvise` (the Ubuntu default) unless noted.
- tenet-sat: self-contained linux-x64 single-file build (.NET 10), published by `scripts/publish.sh`.
- Proof and formula files were read once with `cat` before each set of runs, so they come from the page cache.

## Tools (built by `setup.log`'s script, at these commits)

| Tool | Commit | Role |
|---|---|---|
| CaDiCaL | e4d9bd9 | wrote the proofs (`--lrat --no-binary`) |
| lrat-trim | 4231949 | baseline; `lrat-trim cnf proof` checks and trims, `--no-trim` only checks |
| lrat-check (drat-trim repo) | 2e3b2dc | baseline |
| cake_lpr | 033aad5 | baseline, formally verified; built with a 256 GiB heap |

## Proofs (`scripts/aws/gen.sh`; `proof-generation.txt`, `solved.txt`)

The 218 UNSAT instances of the SAT Competition 2023 main track, from the Global Benchmark Database
(`track=main_2023 and result=unsat`). CaDiCaL ran on each with a 15-minute limit, 96 at a time. A disk guard stopped
the CaDiCaL runs with the largest proofs when free space fell below 50 GB. Outcome for the 219 files downloaded:
77 solved (exit 20, text LRAT kept), 86 timed out (exit 124), 56 stopped by the guard (exit 143). Instances are named by their
GBD hash; the six timed ones are in `bench-list.txt`.

| Short name | GBD hash | Text LRAT |
|---|---|---|
| 036b | 036b541db9d0839b0591d408ff4f77ae | 0.53 GB |
| abe8 | abe802007c9aed4eab2e758fb130174b | 1.24 GB |
| 65b1 | 65b1e2253626af4a72b954f8186a5880 | 2.45 GB |
| 953e | 953e9ff735bdf5ada27b0ab2b908b6af | 5.49 GB |
| bc7d | bc7dc7a83b442a2bf4cf8cae3f0e0faa | 6.48 GB |
| e5a7 | e5a72ac8f4880702a4deacc7854318e8 | 7.11 GB |

## Files

| File | What |
|---|---|
| `bench.log`, `results.csv` (made by `scripts/aws/bench2.sh`) | first comparison: tenet-sat as committed in 11c9e8e (1 to 192 threads, 32 MB batches) against lrat-trim, lrat-check, cake_lpr |
| `tune.log`, `tune2.log` | same build, batch sizes 32 MB / 256 MB / 1 GB with per-phase timers (the binary-LRAT rows are empty: the conversion failed) |
| `tune3.log` | first rework: 1 GB batches, parallel formula loading, garbage-collector counters, `--read`, and a larger GC nursery |
| `tune4.log` | after clauses moved into shared slabs |
| `final-scaling.txt` | final build, best of 3 runs (1 run at 1 thread) |
| `fuzz-vs-cake_lpr.txt` | final build against cake_lpr on 300 corrupted copies each of two small CaDiCaL proofs (`scripts/small_instances.py`, `scripts/fuzz_vs_cake_lpr.py`) |
| `final-verify-all.txt` | final build: every one of the 77 proofs at 1 and 64 threads |

## Intermediate measurements not in the logs above (copied from the session)

Best of 3, final build, larger thread counts:

| Proof | 32 | 48 | 64 | 96 | 128 | 192 |
|---|---|---|---|---|---|---|
| 953e | 2.55 | 1.90 | 1.73 | 1.88 | 1.78 | 2.56 |
| bc7d | 3.51 | 3.40 | 3.59 | 3.81 | 4.59 | 6.50 |
| e5a7 | 3.67 | 3.64 | 3.82 | 4.62 | 5.08 | 8.36 |

953e at 192 threads, before the slab and allocation fixes, profiled with `perf record -a -g`: 59% of all samples in
three adjacent instructions of `libcoreclr.so`, outside tenet-sat's own code. Garbage-collector settings changed
little (default 3.29 s; `DOTNET_GCHeapCount` 48: 2.86 s, 16: 3.31 s; workstation GC: 2.68 s; 512 MB gen0: 2.65 s).

Thread-seconds inside the parse phase on 953e, measured inside tenet-sat:

| Threads | Getting chunk memory | Parsing |
|---|---|---|
| 48 | 0.39 | 16.8 |
| 96 | 4.0 | 32.1 |
| 192 | 149.5 | 116.7 |

After memory was handed out on one thread before each parallel parse: 0.02 s, 0.03 s and 0.05 s in total at 48,
96 and 192 threads.

Pinning to one processor (`numactl --cpunodebind=0 --membind=0`, 96 threads) against 96 and 192 unpinned threads,
best of 2: 953e 2.32 / 1.66 / 2.64 s; bc7d 4.91 / 4.63 / 8.31 s; e5a7 5.68 / 4.88 / 8.52 s.

Transparent huge pages `always` on 953e (build before slabs): page faults 2.40 M to 0.88 M at 64 threads and 2.48 M
to 1.72 M at 192; parse time at 192 threads 3.92 s to 3.06 s.

## The proof tenet-sat used to reject

`a931fb0e1490883ee7719eab96ed558a` (2.15 GB). Step 22703433 lists hint 22703430, which is already satisfied under
the step's assignment. lrat-check, lrat-trim and cake_lpr all accept the proof; tenet-sat up to 11c9e8e rejected
it ("hint 22703430 is already satisfied"). The checker now skips such a hint, as they do.
