#!/usr/bin/env bash
# Download the 2023 main-track UNSAT instances and let CaDiCaL write LRAT for each, 15 min limit, 96 at a time.
set -u
mkdir -p ~/inst ~/proofs && cd ~/inst
curl -s "https://benchmark-database.de/getinstances?query=track%3Dmain_2023%20and%20result%3Dunsat&context=cnf" > urls.txt
cat urls.txt | xargs -P 32 -I{} sh -c "h=\$(basename {}); [ -s \$h.cnf ] || curl -s -L {} | xz -dc > \$h.cnf 2>/dev/null || curl -s -L {} > \$h.cnf"
ls *.cnf | wc -l
solve() { h=$(basename $1 .cnf); timeout 900 ~/tools/cadical/build/cadical -q --lrat --no-binary $1 ~/proofs/$h.lrat > ~/proofs/$h.out 2>&1; r=$?; if [ $r -ne 20 ]; then rm -f ~/proofs/$h.lrat; fi; echo "$h $r $(stat -c %s ~/proofs/$h.lrat 2>/dev/null || echo 0)" >> ~/proofs/results.txt; }
export -f solve
ls ~/inst/*.cnf | xargs -P 96 -I{} bash -c "solve {}"
echo GEN-DONE
