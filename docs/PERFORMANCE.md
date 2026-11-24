# Performance

How fast tenet-sat checks real proofs, compared with the other LRAT checkers, on one 192-core machine.
Every number here comes from the raw logs in
[`bench-results/2026-11-24-c7a.48xlarge`](../bench-results/2026-11-24-c7a.48xlarge/), and the scripts that
produced them are in [`scripts/aws`](../scripts/aws/).

## In one paragraph

On six real proofs of SAT Competition 2023 problems (0.5 to 7.1 GB of text LRAT written by CaDiCaL),
tenet-sat with its default settings checks each one **3.9 to 18.7 times faster than lrat-trim**, the fastest of
the established checkers, and **25 to 104 times faster than cake_lpr**, the formally verified one. All four
checkers agree on all 77 proofs that were generated. The speed comes from using many cores: on one core
tenet-sat is about as fast as lrat-trim, sometimes 2 times slower. It is fastest at 48 to 64 threads; past that,
on this two-processor machine, it gets slower again.

## Setup

- **Machine:** AWS c7a.48xlarge: 2 x AMD EPYC 9R14, 96 cores each (192 cores, no SMT), 2 NUMA nodes, 369 GiB RAM.
  Ubuntu 24.04. Files were read once before timing, so they come from the page cache.
- **Proofs:** the 218 UNSAT problems of the SAT Competition 2023 main track, from the Global Benchmark Database.
  CaDiCaL (`--lrat --no-binary`) ran on each for up to 15 minutes; 77 finished with a proof. Six of them, chosen
  for size, were timed.
- **Checkers:** lrat-trim, lrat-check (from the drat-trim repository) and cake_lpr, built from their current heads
  (commits in the [raw data](../bench-results/2026-11-24-c7a.48xlarge/README.md#tools-built-by-setuplogs-script-at-these-commits)).
  lrat-trim checks and trims by default; `--no-trim` only checks and was slightly slower, so the faster
  default is used as the baseline. cake_lpr was built with a 256 GiB heap.
- **Timing:** wall-clock time of the whole process, start to exit. tenet-sat: best of 3 runs. The other
  checkers are single-threaded and were run once each.

## Against the other checkers

Seconds, lower is better. tenet-sat at its default (64 threads here) and at its best thread count.

| Proof | Text LRAT | **tenet-sat** default | **tenet-sat** best | lrat-trim | lrat-check | cake_lpr (verified) |
|---|---:|---:|---:|---:|---:|---:|
| 036b | 0.53 GB | **0.66** | **0.60** (32) | 2.54 | 5.97 | 16.78 |
| abe8 | 1.24 GB | **0.90** | **0.90** (48) | 6.98 | 16.61 | 41.43 |
| 65b1 | 2.45 GB | **1.63** | **1.58** (48) | 8.31 | 26.56 | 85.10 |
| 953e | 5.49 GB | **1.77** | **1.77** (64) | 33.16 | 74.65 | 184.66 |
| bc7d | 6.48 GB | **3.65** | **3.48** (32) | 22.22 | 66.91 | 243.89 |
| e5a7 | 7.11 GB | **4.23** | **3.72** (32) | 23.82 | 73.73 | 252.85 |

The same, as speedups of tenet-sat at its default:

| Proof | vs lrat-trim | vs lrat-check | vs cake_lpr |
|---|---:|---:|---:|
| 036b | 3.9x | 9.0x | 25x |
| abe8 | 7.8x | 18x | 46x |
| 65b1 | 5.1x | 16x | 52x |
| 953e | **18.7x** | 42x | **104x** |
| bc7d | 6.1x | 18x | 67x |
| e5a7 | 5.6x | 17x | 60x |

Read these with care:

- **cake_lpr is formally verified; tenet-sat is not.** The speedup over cake_lpr is a difference in trust as well
  as in speed. People who need a verified answer should still run cake_lpr.
- **One machine, one solver, six proofs.** Proofs from other solvers, binary LRAT, and other hardware were not
  timed on this machine. Binary LRAT was checked for correctness (see below), not for speed.
- **The spread between proofs is real.** 953e has few long-lived clauses and parallelizes well; bc7d and e5a7 carry
  45 to 49 million original clauses, which tenet-sat loads and stores before checking starts.

## One core

tenet-sat at 1 thread against lrat-trim:

| Proof | tenet-sat, 1 thread | lrat-trim | tenet-sat relative speed |
|---|---:|---:|---:|
| 036b | 3.69 | 2.54 | 0.69x |
| abe8 | 13.58 | 6.98 | 0.51x |
| 65b1 | 8.90 | 8.31 | 0.93x |
| 953e | 43.33 | 33.16 | 0.77x |
| bc7d | 20.98 | 22.22 | 1.06x |
| e5a7 | 22.97 | 23.82 | 1.04x |

On one core tenet-sat is between 2 times slower and slightly faster. Its advantage is parallelism, so it pays off on
machines with many cores and on large proofs.

## Threads

Best of 3, seconds. The default is all cores, capped at 64.

| Proof | 1 | 8 | 16 | 32 | 48 | 64 | 96 | 128 | 192 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 036b | 3.69 | 0.71 | 0.65 | **0.60** | 0.61 | 0.66 | | | |
| abe8 | 13.58 | 1.82 | 1.24 | 0.98 | **0.90** | 0.90 | | | |
| 65b1 | 8.90 | 1.78 | 1.88 | 1.74 | **1.58** | 1.63 | | | |
| 953e | 43.33 | 5.79 | 3.47 | 2.21 | 1.90 | **1.77** | 1.88 | 1.78 | 2.56 |
| bc7d | 20.98 | 4.33 | 4.20 | **3.48** | 3.59 | 3.65 | 3.81 | 4.59 | 6.50 |
| e5a7 | 22.97 | 4.66 | 4.16 | **3.72** | 3.82 | 4.23 | 4.62 | 5.08 | 8.36 |

(96, 128 and 192 were measured in a separate best-of-3 run with the same build.)

Why it stops at about 64 on this machine:

- **Two processors.** Each has its own memory. Pinning tenet-sat to one processor and its memory
  (`numactl --cpunodebind=0 --membind=0`, 96 threads) was slower than letting 96 threads spread over both
  (953e: 2.32 s against 1.66 s), because one processor has half the memory bandwidth. Using all 192 cores makes
  threads on both processors work on the same clause store, and cross-processor traffic grows faster than the work
  is divided.
- **Fresh memory.** At 192 threads the parse phase takes about 7 times the thread-time it takes at 48, because
  each batch writes into memory the kernel has to map in first.
- **Fixed costs.** About 0.3 to 0.7 s of each run is starting the process and returning memory at exit, which no
  number of threads shortens.

Scaling past one processor would need each processor to keep the clauses it parsed and check the steps that use
them. That is not done.

## What changed to get here

The first version (commit 11c9e8e) was 1.1 to 3 times faster than lrat-trim on these proofs and got slower past
8 to 32 threads. Each change below was measured on this machine; the logs are in the raw data.

| Proof | First version, best | Now, best | Faster by |
|---|---:|---:|---:|
| 036b | 1.89 | 0.60 | 3.2x |
| abe8 | 2.35 | 0.90 | 2.6x |
| 65b1 | 7.56 | 1.58 | 4.8x |
| 953e | 11.00 | 1.77 | 6.2x |
| bc7d | 18.11 | 3.48 | 5.2x |
| e5a7 | 20.55 | 3.72 | 5.5x |

1. **1 GB batches instead of 32 MB.** Fewer points where all threads wait for each other. On 953e at 64 threads:
   11.1 s to 6.0 s.
2. **Parallel formula loading.** Loading bc7d's 45 million clauses took 4.9 s on one thread.
3. **Garbage-collector counters**, added to find what was left. They showed the collector holding all threads for
   2 to 9 s per run, 40 to 50% of the total, because every clause was its own small object.
4. **Clauses in shared slabs.** Each parse chunk writes its clauses into one large array, and the store keeps
   numeric handles into it. Collector pauses fell from seconds to hundredths of a second: 953e at 64 threads, 5.97 s
   to 2.66 s.
5. **Chunks reused and sized in advance** from what the previous batch showed, instead of grown while parsing.
6. **Chunk memory handed out on one thread.** At 192 threads, getting memory for parse chunks took 150
   thread-seconds, more than the parsing itself: large allocations from many threads at once serialize inside
   the .NET runtime. Handing it out on one thread before each parallel parse takes 0.05 s in total. Slabs whose
   clauses have all been deleted are recycled by size.

Tried and not kept as defaults: reading the proof with file reads instead of a memory map (`--read`, no gain);
transparent huge pages (fewer page faults, mixed timing); larger collector nurseries and fewer collector heaps
(small and inconsistent effects once slabs were in).

## Correctness on the same data

- **All 77 proofs** verify with the final build at 1 thread and at 64 threads (154 runs,
  `final-verify-all.txt`), and with lrat-check, lrat-trim and cake_lpr.
- **One of them used to be rejected.** Proof a931fb0e contains a hint that is already satisfied when it is used.
  The first version rejected that; lrat-check, lrat-trim and cake_lpr skip such a hint, and tenet-sat now does
  too. Skipping a hint can never make an invalid step pass, since the step still has to reach a conflict from
  the hints that remain.
- **600 corrupted text proofs** (300 each from two small CaDiCaL proofs, `fuzz-vs-cake_lpr.txt`): tenet-sat and
  cake_lpr gave the same verdict on every one (392 rejected, 208 accepted).
- **1,000 corrupted binary proofs** (500 each from the same two problems as binary LRAT,
  `fuzz-binary-vs-cake_lpr.txt`, run on a Mac): no mutant was accepted by tenet-sat and rejected by cake_lpr.
  Both rejected 785 and both accepted 139. The other 76 repeat a step with the same clause ID, which tenet-sat
  rejects by rule and cake_lpr accepts by letting the copy replace the original. Mutations: dropped, flipped,
  replaced and swapped hints and literals, a hint's clause deleted just before use, dropped and repeated steps,
  single flipped bits and truncated files; each mutant was checked at 1 and 8 threads with 1 MB and 1 GB batches.
- **32 unit tests**, including a fuzz comparison against an independent reference checker, a check that the
  parallel formula loader matches the sequential one on awkward input, and a check that `--read` and the memory
  map give the same verdict and message on every test.

## Earlier laptop measurements

From the first version on an 8-core Apple silicon laptop (4 performance, 4 efficiency cores): small synthetic
proofs converted from DRAT by drat-trim. One run each, seconds.

| Instance | Text LRAT | drat-trim (DRAT to LRAT) | cake_lpr | lrat-check | tenet-sat, 8 threads |
|---|---|---:|---:|---:|---:|
| r250 (random 3-SAT) | 6 MB | 1.0 | 0.22 | 0.29 | 0.13 |
| php9 (pigeonhole) | 5 MB | 0.7 | 0.16 | 0.11 | 0.13 |
| r300 | 50 MB | 7.2 | 1.66 | 0.73 | 0.27 |
| php10 | 33 MB | 5.0 | 0.99 | 0.46 | 0.19 |
| r350 | 380 MB | 71.7 | 14.02 | 5.40 | 1.28 |

They show what is still true for DRAT proofs: converting DRAT to LRAT (drat-trim, 72 s on r350) costs far more than
checking the result. Solvers that write LRAT directly, as CaDiCaL does, skip that step.

## Reproducing

1. `scripts/publish.sh` builds `dist/tenetsat-linux-x64.tar.gz`.
2. On an Ubuntu machine with a few hundred GB of disk, follow [`scripts/aws/README.md`](../scripts/aws/README.md):
   `setup.sh` builds the other checkers and CaDiCaL, `gen.sh` with `guard.sh` makes the proofs, `bench2.sh` times
   one proof with every checker.
3. For thread scaling, take the best of several runs: the same run varied by up to 20% on this machine.
