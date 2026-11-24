#!/usr/bin/env bash
# Run once on the benchmark machine (Amazon Linux 2023 or Ubuntu). Builds the baseline
# tools next to this script: drat-trim, lrat-check, cake_lpr, kissat.
# These are cloned at their current head, so record the commits printed at the end.
set -euo pipefail
cd "$(dirname "$0")"
if command -v dnf >/dev/null; then sudo dnf install -y -q gcc gcc-c++ make git
elif command -v apt-get >/dev/null; then sudo apt-get update -qq && sudo apt-get install -y -qq build-essential git
fi
mkdir -p tools && cd tools
[ -d drat-trim ] || git clone -q https://github.com/marijnheule/drat-trim.git
[ -d cake_lpr ]  || git clone -q https://github.com/tanyongkiam/cake_lpr.git
[ -d kissat ]    || git clone -q https://github.com/arminbiere/kissat.git
make -s -C drat-trim
# 64 GiB heap for cake_lpr instead of the 4 GiB default; large proofs exhaust the default.
make -s -B -C cake_lpr CFLAGS="-O2 -DCML_HEAP_SIZE=65536 -DCML_STACK_SIZE=16384"
(cd kissat && ./configure >/dev/null && make -s -j"$(nproc)")
for d in drat-trim cake_lpr kissat; do echo "$d $(git -C $d rev-parse --short HEAD)"; done
echo "cores: $(nproc)  memory: $(free -g 2>/dev/null | awk '/Mem:/{print $2" GiB"}')"
