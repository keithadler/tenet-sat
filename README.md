# Tenet-SAT

A parallel LRAT proof checker in C#. Given a CNF formula and an LRAT certificate of
unsatisfiability, it checks the certificate across all cores.

Status: Phase 1 core works. It agrees with `cake_lpr` on every proof and corrupted proof
tried so far, on proofs up to 380 MB. It has only been run on an 8-core laptop.

## Use

```bash
dotnet build -c Release
```

```bash
dotnet src/TenetSat/bin/Release/net10.0/tenetsat.dll check formula.cnf proof.lrat --threads 8
```

Prints `s VERIFIED` (exit 0) or `s NOT VERIFIED` (exit 1) with the first failing step.

To make a test proof without drat-trim (small instances only; the converter is a slow reference):

```bash
kissat --no-binary formula.cnf proof.drat
```

```bash
dotnet src/TenetSat/bin/Release/net10.0/tenetsat.dll drat2lrat formula.cnf proof.drat proof.lrat
```

## How it works

The proof is memory-mapped and consumed in batches. For each batch:

1. **Parse**, in parallel. Text proofs are split at line boundaries. Binary proofs have none, so chunk starts are guessed and then confirmed.
2. **Register.** Additions go into the clause store, and each deleted clause is stamped with the index of the earliest step that deletes it. Parallel: clause IDs are distinct, and because IDs increase through the proof, "was this clause added before this deletion" is a comparison of IDs.
3. **Check** every addition in parallel. A step reads only the clauses its hints name, so steps are independent. The stamp tells a step whether a hint was deleted before it.
4. **Delete** the clauses the batch removed, in parallel.

Memory is the live clause set plus one batch.

## Acceptance rules

- Every hint must be a clause that is live at that step and is unit or falsified under the current assignment. A satisfied or non-unit hint rejects the step.
- Addition IDs must strictly increase (true of drat-trim, lrat-trim and CaDiCaL output).
- Text proofs hold one step per line.
- RAT steps are supported. Checking that all clauses with the negated pivot are listed scans the whole clause store, so RAT-heavy proofs will be slow.
- The verdict does not depend on thread count or batch size.

## First measurements

Apple silicon laptop, 4 performance + 4 efficiency cores, one run each, wall-clock seconds
for the whole process. Proofs are kissat 4.0.4 DRAT converted by drat-trim. Treat these as
a sanity check, not a benchmark: five synthetic instances, no repeats, no variance.

| Instance | Text LRAT | drat-trim (DRAT to LRAT) | cake_lpr | lrat-check | tenetsat, 8 threads |
|---|---|---|---|---|---|
| r250 (random 3-SAT) | 6 MB | 1.0 | 0.22 | 0.29 | 0.13 |
| php9 (pigeonhole) | 5 MB | 0.7 | 0.16 | 0.11 | 0.13 |
| r300 | 50 MB | 7.2 | 1.66 | 0.73 | 0.27 |
| php10 | 33 MB | 5.0 | 0.99 | 0.46 | 0.19 |
| r350 | 380 MB (1.45M additions) | 71.7 | 14.02 | 5.40 | 1.28 |

Thread scaling on r350 (binary), tenetsat's own timers. "Register" is adding clauses,
stamping deletions and dropping deleted clauses.

| Threads | Parse | Register | Check | Single-threaded | Total |
|---|---|---|---|---|---|
| 1 | 0.85 | 0.12 | 1.59 | 0.005 | 2.56 |
| 2 | 0.38 | 0.06 | 0.85 | 0.004 | 1.29 |
| 4 | 0.26 | 0.05 | 0.57 | 0.004 | 0.88 |
| 8 | 0.20 | 0.10 | 0.43 | 0.004 | 0.73 |

What this says:

- **The conversion dominates.** On r350, drat-trim takes 72 s to produce the LRAT and every checker takes far less. Phase 2 is where the time is.
- **Scaling is 3.5x total on 8 threads.** Four of the eight cores are efficiency cores, and repeat runs at 8 threads varied between 0.71 s and 1.02 s.
- **Nothing measurable is single-threaded any more.** Per batch, the only single-threaded work is validating one summary per chunk; loading the formula is the rest (5 ms here). Earlier builds spent 0.14 s per run registering and deleting clauses on one thread.
- **The register phase stops improving past 4 threads on this laptop** (0.05 s at 4, 0.07 to 0.10 s at 8). It is random memory access, so it may be limited by memory rather than cores. Whether it and the check phase keep scaling on a many-core machine is unknown until it is run on one.
- **Binary LRAT parses in parallel.** Chunk starts are guessed, parsed in parallel, and confirmed against the real step boundaries; no guess was wrong on any real proof here.
- cake_lpr is formally verified; tenetsat is not. The times are not a like-for-like comparison of trust.

## Agreement with other checkers

- All five proofs: `cake_lpr` (text and binary), `lrat-check` (text) and tenetsat (text and binary) all verify. The two pigeonhole proofs contain RAT steps (198 and 290).
- CaDiCaL 3.1.0 native LRAT (`--lrat`, text and binary, r300): tenetsat and `cake_lpr` both verify.
- 300 corrupted proofs (`scripts/fuzz_vs_cake_lpr.py` on php9 and r250): tenetsat and `cake_lpr` gave the same verdict on all 300 (175 rejected, 125 accepted). Repeated after each change to the checker.

## Running on another machine

```bash
scripts/publish.sh
```

This writes `dist/tenetsat-linux-x64.tar.gz` and `dist/tenetsat-linux-arm64.tar.gz`: self-contained
binaries plus two scripts. The Linux builds have not been run anywhere yet. On the target machine,
unpack, then:

```bash
./remote-setup.sh
```

```bash
./bench.sh formula.cnf proof.lrat --baselines
```

`remote-setup.sh` builds drat-trim, cake_lpr and kissat. `bench.sh` times tenetsat at 1, 2, 4, ...
up to all cores and writes a CSV. Cost of a run is the wall time times the instance's hourly price.
There is no checkpointing, so a reclaimed spot instance loses the run in progress.

## Not done yet


- No run on a many-core machine, and no SAT Competition or Pythagorean-triples inputs.
- The `cake_lpr` fuzz comparison is a script run by hand, not part of `dotnet test`.
- DRAT to LRAT conversion (Phase 2) is only the naive reference converter: forward, no watch lists, no trimming, RUP only.
- No Lean export, no compressed input, no sharding.

## Prior art

Checked before claiming anything is "first". This came from three quick searches, so it is a starting list, not a survey.

- **Parallel backward DRAT checking already exists.** Manthey and Philipp, [Checking Unsatisfiability Proofs in Parallel](https://easychair.org/publications/paper/FftK), and Lammich's `gratgen` in the [GRAT tool chain](https://www21.in.tum.de/~lammich/pub/sat2017.pdf) (SAT 2017), which has a multithreaded backward phase. Phase 2's "parallel backward checking inside a single proof" is not new ground by itself.
- **PalRUP** ([SAT 2026](https://drops.dagstuhl.de/entities/document/10.4230/LIPIcs.SAT.2026.17)): an LRUP-based proof format designed for decentralized parallel checking. The closest competitor to the Phase 1 idea.
- **cake_lpr** supports a [compositional proof format](https://link.springer.com/10.1007/s10009-022-00690-y) so that parallel instances can check very large proofs. This is how cube-and-conquer proofs are already checked in parallel with a verified checker.
- **Lammich, IJCAR 2024**: a [verified LRAT checker](https://www21.in.tum.de/~lammich/isabelle_llvm_par/IJCAR2024.pdf) reported as fast as `lrat-trim`.
- **Native LRAT from solvers**: [CaDiCaL emits LRAT directly](https://drops.dagstuhl.de/entities/document/10.4230/LIPIcs.SAT.2023.21) at about 5% overhead, which shrinks the DRAT to LRAT bottleneck for solvers that adopt it.
- A KIT paper found in the same search states that, to the authors' knowledge, all LRAT checkers so far were sequential. I have not read it to see its date or whether PalRUP supersedes that claim.
