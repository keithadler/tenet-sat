#!/usr/bin/env bash
# Time tenetsat at increasing thread counts on one proof, with optional baselines.
# usage: bench.sh <formula.cnf> <proof.lrat> [--baselines] [--batch-mb N]
# Writes results to bench-<proof name>.csv and prints them.
# Copy the CSV off the machine as you go: a spot instance can be reclaimed at any time,
# and tenetsat has no checkpointing, so an interrupted run starts over.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
cnf="$1"; proof="$2"; shift 2
baselines=0; batch=256
while [ $# -gt 0 ]; do case "$1" in --baselines) baselines=1;; --batch-mb) batch="$2"; shift;; esac; shift; done
cores=$(nproc 2>/dev/null || sysctl -n hw.ncpu)
out="bench-$(basename "$proof").csv"
echo "tool,threads,wall_s,parse_s,register_s,check_s,sequential_s,verdict" > "$out"
now() { date +%s.%N 2>/dev/null | grep -v N || python3 -c 'import time;print(time.time())'; }
cat "$proof" > /dev/null   # warm the page cache so the first run is not charged for disk reads
"$here/tenetsat" >/dev/null 2>&1 || true   # first launch unpacks the runtime; keep that out of the timings

t=1; counts=()
while [ "$t" -lt "$cores" ]; do counts+=("$t"); t=$((t * 2)); done; counts+=("$cores")
for t in "${counts[@]}"; do
  s=$(now); log=$("$here/tenetsat" check "$cnf" "$proof" --threads "$t" --batch-mb "$batch" || true); e=$(now)
  line=$(echo "$log" | grep '^c parse')
  f() { echo "$line" | sed -E "s/.*$1 ([0-9.]+)s.*/\1/"; }
  echo "tenetsat,$t,$(echo "$e - $s" | bc),$(f parse),$(f register),$(f check),$(f sequential),$(echo "$log" | grep '^s ' | cut -c3-)" | tee -a "$out"
done
if [ "$baselines" = 1 ]; then
  for tool in cake_lpr/cake_lpr drat-trim/lrat-check; do
    [ -x "$here/tools/$tool" ] || continue
    # lrat-check reads text LRAT only; a text proof starts with a digit.
    if [ "$(basename $tool)" = lrat-check ] && ! head -c 1 "$proof" | grep -q '[0-9]'; then continue; fi
    s=$(now); log=$("$here/tools/$tool" "$cnf" "$proof" 2>&1 || true); e=$(now)
    echo "$(basename $tool),1,$(echo "$e - $s" | bc),,,,,$(echo "$log" | grep -E 'VERIFIED' | head -1 | sed 's/^[sc] //')" | tee -a "$out"
  done
fi
