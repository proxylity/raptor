#!/usr/bin/env bash
#
# Runs bench/benchmark.sh against a local raptor2 stack (native CLI -> local gateway stand-in -> native MicroVM app)
# with a fake `aws` that stores "S3" in a directory. Checks the benchmark's own plumbing: timing, verification,
# CSV and summary. No AWS account needed.
#
# Usage: bench/selftest.sh     (after microvm/scripts/publish.sh)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORK="$(mktemp -d)"
PIDS=()
cleanup() { kill "${PIDS[@]}" 2>/dev/null || true; rm -rf "$WORK"; }
trap cleanup EXIT

export FAKE_S3="$WORK/s3"
mkdir -p "$FAKE_S3/local-bucket" "$WORK/bin"

cat > "$WORK/bin/aws" <<'EOF'
#!/usr/bin/env bash
# Just enough of the AWS CLI for the benchmark: s3 cp/rm, s3api list-objects-v2, configure export-credentials, --version.
set -euo pipefail
args=("$@"); bucket=""; prefix=""
case "${1:-}" in
  --version) echo "aws-cli/fake"; exit 0 ;;
  configure) printf "AWS_ACCESS_KEY_ID=fake\nAWS_SECRET_ACCESS_KEY=fake\n" ;;
  s3api)
    for ((i = 0; i < ${#args[@]}; i++)); do
      [[ ${args[i]} == --bucket ]] && bucket=${args[i+1]}
      [[ ${args[i]} == --prefix ]] && prefix=${args[i+1]}
    done
    (cd "$FAKE_S3/$bucket" && find . -type f -path "./$prefix*" -printf '%P\t%s\n' 2>/dev/null) || true ;;
  s3)
    op=$2; src=$3; dst=${4:-}
    case "$op" in
      cp)
        if [[ $src == s3://* ]]; then cat "$FAKE_S3/${src#s3://}"
        elif [[ -d $src ]]; then mkdir -p "$FAKE_S3/${dst#s3://}"; cp -r "$src"/. "$FAKE_S3/${dst#s3://}/"
        else mkdir -p "$(dirname "$FAKE_S3/${dst#s3://}")"; cp "$src" "$FAKE_S3/${dst#s3://}"; fi ;;
      rm) rm -rf "${FAKE_S3:?}/${src#s3://}" ;;
    esac ;;
  *) echo "fake aws: unsupported: $*" >&2; exit 1 ;;
esac
EOF
chmod +x "$WORK/bin/aws"
export PATH="$WORK/bin:$PATH"

VM="$ROOT/microvm/build/image/raptor2-microvm"
[[ -x "$VM" && -x "$ROOT/microvm/build/cli/raptor2" ]] || { echo "Run microvm/scripts/publish.sh first" >&2; exit 1; }
URLS=""
for i in 1 2 3 4; do
  RAPTOR_SINK_DIR="$FAKE_S3/local-bucket" RAPTOR_PORT=$((18180 + i)) "$VM" > "$WORK/vm$i.log" 2>&1 &
  PIDS+=($!); URLS+="${URLS:+,}http://127.0.0.1:$((18180 + i))/"
done
dotnet build "${WORK_DIR:-$HOME/raptor2-build}/src/raptor2-localgw" -c Release -o "$WORK/gw" --nologo -v q > /dev/null
dotnet "$WORK/gw/raptor2-localgw.dll" 15101 "$URLS" 1000 50 0.01 > "$WORK/gw.log" 2>&1 &
PIDS+=($!)
for _ in $(seq 1 30); do grep -q "^raptor2-localgw:" "$WORK/gw.log" 2>/dev/null && break; sleep 0.5; done

TOOLS="raptor2 awscli" WORKLOADS="100x1KiB 100x10KiB 1MiB" REPEAT=2 RATE_MBPS=200 VERIFY=sha256 \
  RAPTOR2_ENDPOINT=udp://127.0.0.1:15101 RAPTOR2_BUCKET=local-bucket RAPTOR2_PREFIX="" \
  AWSCLI_BUCKET=local-bucket DATA_DIR="$WORK/data" OUT_DIR="$WORK/out" \
  bash "$ROOT/bench/benchmark.sh"

ok=$(awk -F, 'NR > 1 && $9 == 0 && $10 == "ok"' "$WORK/out/results.csv" | wc -l)
all=$(( $(wc -l < "$WORK/out/results.csv") - 1 ))
echo "selftest: $ok of $all runs succeeded and verified"
[[ "$ok" == "$all" && "$all" -gt 0 ]]
