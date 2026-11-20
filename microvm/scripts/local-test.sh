#!/usr/bin/env bash
#
# End-to-end test with no AWS: the native raptor2 CLI -> a local stand-in for the gateway -> N local copies of the
# native MicroVM app (one per slot group) -> a directory standing in for S3. Checks every file byte for byte.
#
# Usage: microvm/scripts/local-test.sh [loss=0.03] [vms=4]
#   Needs microvm/scripts/publish.sh to have run (it builds both native executables).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
LOSS="${1:-0.03}"
VMS="${2:-4}"
WORK="$(mktemp -d)"
VM="$ROOT/microvm/build/image/raptor2-microvm"
CLI="$ROOT/microvm/build/cli/raptor2"
PIDS=()
cleanup() { kill "${PIDS[@]}" 2>/dev/null || true; rm -rf "$WORK"; }
trap cleanup EXIT

[[ -x "$VM" && -x "$CLI" ]] || { echo "Run microvm/scripts/publish.sh first" >&2; exit 1; }

mkdir -p "$WORK/in" "$WORK/sink"
for size in 0 1 1000 300000 5000000 40000000; do head -c "$size" /dev/urandom > "$WORK/in/f$size.bin"; done
mkdir -p "$WORK/in/small"
for i in $(seq 1 100); do head -c 1024 /dev/urandom > "$WORK/in/small/s$i.bin"; done

URLS=""
for i in $(seq 1 "$VMS"); do
  port=$((18080 + i))
  RAPTOR_SINK_DIR="$WORK/sink" RAPTOR_PORT=$port "$VM" > "$WORK/vm$i.log" 2>&1 &
  PIDS+=($!)
  URLS+="${URLS:+,}http://127.0.0.1:$port/"
done
GW_DIR="$WORK/gw"
# Build from the Linux copy that publish.sh made, so this never writes obj/ folders into a Windows checkout.
dotnet build "${WORK_DIR:-$HOME/raptor2-build}/src/raptor2-localgw" -c Release -o "$GW_DIR" --nologo -v q > /dev/null
dotnet "$GW_DIR/raptor2-localgw.dll" 15001 "$URLS" 1000 50 "$LOSS" > "$WORK/gw.log" 2>&1 &
PIDS+=($!)
for _ in $(seq 1 30); do grep -q "^raptor2-localgw:" "$WORK/gw.log" 2>/dev/null && break; sleep 0.5; done

cd "$WORK/in"
echo "== files, loss $LOSS"
"$CLI" . udp://127.0.0.1:15001 --rate-mbps 200 --prefix run1 --timeout 30 --max-size 64MiB   # includes multipart lanes
echo "== 100 x 1 KiB, recursive"
"$CLI" small udp://127.0.0.1:15001 --rate-mbps 200 --prefix run2 --timeout 30

fail=0
for f in "$WORK"/in/*.bin; do cmp -s "$f" "$WORK/sink/run1/$(basename "$f")" || { echo "MISMATCH $(basename "$f")"; fail=1; }; done
for f in "$WORK"/in/small/*.bin; do cmp -s "$f" "$WORK/sink/run2/small/$(basename "$f")" || { echo "MISMATCH small/$(basename "$f")"; fail=1; }; done
[[ $fail == 0 ]] && echo "PASS: every file matches" || { echo "FAIL"; tail -5 "$WORK"/vm*.log; exit 1; }
