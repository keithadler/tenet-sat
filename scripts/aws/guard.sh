#!/usr/bin/env bash
# Run next to gen.sh: keeps free disk above 50 GB by stopping the CaDiCaL run with the biggest proof
# (its partial proof is then deleted by gen.sh).
while pgrep -x cadical >/dev/null; do
  free=$(df --output=avail -BG / | tail -1 | tr -dc 0-9)
  if [ "$free" -lt 50 ]; then
    big=$(ls -S ~/proofs/*.lrat | while read f; do h=$(basename $f .lrat); pgrep -f "cadical.*$h" >/dev/null && { echo $h; break; }; done)
    [ -n "$big" ] && { pkill -f "cadical.*$big"; echo "$(date +%T) free ${free}G: stopped $big"; }
    sleep 5
  else sleep 10; fi
done
