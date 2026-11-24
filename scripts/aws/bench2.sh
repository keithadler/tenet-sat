#!/usr/bin/env bash
# usage: bench2.sh <cnf> <proof>   appends rows to ~/results.csv: instance,tool,threads,wall_s,maxrss_kb,exit,verdict
cnf=$1; p=$2; h=$(basename $p .lrat); T=~/tools; R=~/results.csv
[ -f $R ] || echo "instance,proof_bytes,tool,threads,wall_s,maxrss_kb,exit,verdict" > $R
sz=$(stat -c %s $p)
cat $cnf $p > /dev/null   # warm the page cache
run() { tool=$1; th=$2; shift 2; /usr/bin/time -f "%e %M" -o /tmp/t.$$ timeout 3600 "$@" > /tmp/o.$$ 2>&1; ex=$?; read w m < <(tail -1 /tmp/t.$$); v=$(grep -m1 -E "^s |VERIFIED|FAIL|rror" /tmp/o.$$ | tr -d "," | cut -c1-60); echo "$h,$sz,$tool,$th,$w,$m,$ex,$v" | tee -a $R; }
~/ts/linux-x64/tenetsat >/dev/null 2>&1
for t in 1 2 4 8 16 32 64 128 192; do run tenetsat $t ~/ts/linux-x64/tenetsat check $cnf $p --threads $t; done
run lrat-trim-check 1 $T/lrat-trim/lrat-trim --no-trim $cnf $p
run lrat-trim 1 $T/lrat-trim/lrat-trim $cnf $p
run lrat-check 1 $T/drat-trim/lrat-check $cnf $p
run cake_lpr 1 $T/cake_lpr/cake_lpr $cnf $p
