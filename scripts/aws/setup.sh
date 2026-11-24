#!/usr/bin/env bash
# Run once on the benchmark machine (Ubuntu 24.04): compilers, then the baseline checkers and CaDiCaL,
# cloned at their current head (record the commits it prints).
set -e
sudo apt-get -o Acquire::http::Timeout=20 -o Acquire::Retries=5 update -qq && sudo apt-get -o Acquire::http::Timeout=20 -o Acquire::Retries=5 install -y -qq build-essential git bc python3 xz-utils zlib1g-dev time numactl >/dev/null
mkdir -p ~/tools && cd ~/tools
for r in arminbiere/cadical arminbiere/lrat-trim marijnheule/drat-trim tanyongkiam/cake_lpr; do [ -d $(basename $r) ] || git clone -q --depth 1 https://github.com/$r.git; done
(cd cadical && ./configure >/dev/null && make -s -j64 >/dev/null)
(cd lrat-trim && (./configure >/dev/null 2>&1 || true) && make -s >/dev/null)
make -s -C drat-trim >/dev/null 2>&1 || true
# 256 GiB heap for cake_lpr; multi-GB proofs exhaust the default.
make -s -B -C cake_lpr CFLAGS="-O2 -DCML_HEAP_SIZE=262144 -DCML_STACK_SIZE=16384" >/dev/null 2>&1 || true
ls -la cadical/build/cadical lrat-trim/lrat-trim drat-trim/lrat-check cake_lpr/cake_lpr
for d in cadical lrat-trim drat-trim cake_lpr; do echo "$d $(git -C $d rev-parse --short HEAD)"; done
