#!/usr/bin/env bash
# Build self-contained tenetsat binaries. The target machine needs no .NET install.
# usage: scripts/publish.sh [rid ...]     default: linux-x64 linux-arm64
set -euo pipefail
cd "$(dirname "$0")/.."
rids=("$@"); [ ${#rids[@]} -eq 0 ] && rids=(linux-x64 linux-arm64)
for rid in "${rids[@]}"; do
  dotnet publish src/TenetSat -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=true -p:InvariantGlobalization=true -o "dist/$rid" >/dev/null
  cp scripts/remote-setup.sh scripts/bench.sh "dist/$rid/"
  tar -czf "dist/tenetsat-$rid.tar.gz" -C dist "$rid"
  echo "dist/tenetsat-$rid.tar.gz"
done
