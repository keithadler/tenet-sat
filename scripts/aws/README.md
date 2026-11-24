# Benchmark scripts for a many-core Linux machine

Used for the measurements in [docs/PERFORMANCE.md](../../docs/PERFORMANCE.md). Run in the home folder of an
Ubuntu 24.04 machine with a few hundred GB of disk.

1. `bash setup.sh`: compilers, CaDiCaL, lrat-trim, lrat-check, cake_lpr in `~/tools`.
2. `nohup bash gen.sh > gen.log &` and `nohup bash guard.sh > guard.log &`: downloads the SAT Competition 2023
   main-track UNSAT instances into `~/inst` and has CaDiCaL write a text LRAT proof for each into `~/proofs`
   (15 minutes each, 96 at a time; `~/proofs/results.txt` lists `hash exit-code bytes`).
3. Unpack `dist/tenetsat-linux-x64.tar.gz` (from `scripts/publish.sh`) into `~/ts`, then
   `bash bench2.sh ~/inst/HASH.cnf ~/proofs/HASH.lrat` per proof. It appends to `~/results.csv`.

`bench2.sh` runs each tool once from a warm page cache. For thread-scaling numbers, take the best of several runs:
on this machine the same run varied by up to 20%.
