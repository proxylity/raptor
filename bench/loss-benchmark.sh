#!/usr/bin/env bash
#
# Runs bench/benchmark.sh at several levels of simulated packet loss. The loss is applied with tc netem to every
# packet leaving this host's default interface, so every tool sees the same loss: raptor's UDP datagrams and the AWS
# CLI's TCP segments alike. Inbound packets (replies, ACKs) are not dropped.
#
# Needs root for tc (it runs sudo once). The netem qdisc is removed on exit, even after an error or Ctrl-C.
#
# Usage: bench/loss-benchmark.sh
#   LOSSES="0 1 5 10"        loss percentages to test
#   IFACE=eth0               interface (default: the one with the default route)
#   OUT_DIR=./bench-results/loss-<timestamp>
#   Everything else (TOOLS, WORKLOADS, REPEAT, RATE_MBPS, AWS_NATIVE_BIN, RAPTOR2_STACK, ...) is passed through to
#   benchmark.sh. Defaults here: TOOLS="raptor2 awscli" WORKLOADS="500x10KiB" REPEAT=3.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
LOSSES="${LOSSES:-0 1 5 10}"
IFACE="${IFACE:-$(ip route show default | awk '{ for (i = 1; i < NF; i++) if ($i == "dev") { print $(i + 1); exit } }')}"
RUN_ID="$(date -u +%Y%m%dT%H%M%SZ)"
OUT_DIR="$(mkdir -p "${OUT_DIR:-./bench-results/loss-$RUN_ID}" && cd "${OUT_DIR:-./bench-results/loss-$RUN_ID}" && pwd)"
export TOOLS="${TOOLS:-raptor2 awscli}" WORKLOADS="${WORKLOADS:-500x10KiB}" REPEAT="${REPEAT:-3}"

[[ -n "$IFACE" ]] || { echo "No default interface; set IFACE" >&2; exit 1; }
sudo -v                                   # ask for the password once, up front
sudo modprobe sch_netem 2>/dev/null || true
clear_loss() { sudo tc qdisc del dev "$IFACE" root 2>/dev/null || true; }
trap clear_loss EXIT
clear_loss

echo "tool,workload,run,files,bytes,wall_s,user_s,sys_s,exit,verify,durable_s,loss_pct" > "$OUT_DIR/results.csv"
for loss in $LOSSES; do
  clear_loss
  if [[ "$loss" != 0 ]]; then
    sudo tc qdisc add dev "$IFACE" root netem loss "${loss}%"
  fi
  echo "== ${loss}% loss on $IFACE: $(tc qdisc show dev "$IFACE" | head -1)"
  OUT_DIR="$OUT_DIR/loss-$loss" bash "$HERE/benchmark.sh" 2>&1 | grep -E 'KiB:|MiB:|B:' || true
  tail -n +2 "$OUT_DIR/loss-$loss/results.csv" | sed "s/\$/,$loss/" >> "$OUT_DIR/results.csv"
  sudo -v                                 # keep the sudo timestamp fresh between levels
done
clear_loss
echo "== netem removed: $(tc qdisc show dev "$IFACE" | head -1)"
echo "Results: $OUT_DIR/results.csv"
