#!/usr/bin/env bash
#
# Benchmarks uploads to S3 with:
#   raptor1   the original raptor CLI (WireGuard -> SQS/Lambda/DynamoDB stack, template.json)
#   raptor2   the MicroVM-based raptor2 CLI (microvm/template.yaml)
#   awscli    `aws s3 cp` from the AWS CLI native (Rust) preview, forced onto the native path with the CRT client
#   awspy     `aws s3 cp` on the classic Python path (optional baseline)
#
# It creates no AWS resources: it only uploads objects under a per-run prefix and checks that they arrived.
# Each tool/workload pair runs REPEAT times; results go to a CSV and a Markdown summary (median of the runs).
#
# Usage: bench/benchmark.sh
#
# Configuration (environment variables):
#   TOOLS="raptor1 raptor2 awscli"            which tools to run (add awspy for the Python CLI baseline)
#   WORKLOADS="100x1KiB 500x1KiB 1000x1KiB 100x10KiB 500x10KiB 1000x10KiB"
#                                             NxSIZE = N files of SIZE in one folder; SIZE = one file.
#                                             Units: B, KiB, MiB, GiB. raptor2 refuses files over 1 MiB unless
#                                             RAPTOR2_ARGS includes --max-size.
#   REPEAT=3                                  runs per tool and workload
#   RATE_MBPS=100                             send rate for the raptor tools (the AWS CLI is not rate limited)
#   VERIFY=size                               size: HEAD each object; sha256: also download and compare
#   DATA_DIR=./bench-data  OUT_DIR=./bench-results/<timestamp>
#   CLEANUP=0                                 1 deletes this run's uploaded objects afterwards
#   DURABLE_TIMEOUT=120                       seconds to wait, after a CLI exits, for every file to be in S3
#
# Two times are recorded per run: wall_s, until the CLI exits, and durable_s, from the start until every file is
# verified in S3. They differ when a tool exits before its backend has written the objects (the original raptor
# writes them asynchronously). The summary ranks by durable_s.
#
#   raptor2:  RAPTOR2_STACK=raptor2 (reads endpoint, bucket and prefix from its outputs), or set
#             RAPTOR2_ENDPOINT=udp://host:port RAPTOR2_BUCKET=... RAPTOR2_PREFIX=decoded/
#             RAPTOR2_BIN=microvm/build/cli/raptor2   RAPTOR2_ARGS=""   (extra CLI options)
#   raptor1:  RAPTOR1_STACK=raptorq, or RAPTOR1_ENDPOINT=wg://host:port RAPTOR1_BUCKET=... RAPTOR1_PREFIX=decoded/
#             RAPTOR1_SERVER_KEY=<listener public key, default from the stack>  RAPTOR1_CLIENT_KEY=<your private key>
#             RAPTOR1_BIN=publish/raptor   RAPTOR1_ARGS="--confirm BLOCK" (wait for block completion, not just
#             "enough packets")
#   awscli:   AWS_NATIVE_BIN=aws (a native preview build)  AWSCLI_BUCKET (default: raptor2's bucket)
#             AWSCLI_PREFIX=bench-awscli/   TARGET_THROUGHPUT=1Gb/s   AWSPY_BIN=aws
#   AWS_REGION (default us-west-2), AWS_PROFILE: used for everything. The AWS CLI runs get its credentials exported
#   as environment variables (aws configure export-credentials) and a minimal config with the CRT client settings.
set -uo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
export AWS_REGION="${AWS_REGION:-us-west-2}" AWS_DEFAULT_REGION="${AWS_REGION:-us-west-2}"
TOOLS="${TOOLS:-raptor1 raptor2 awscli}"
WORKLOADS="${WORKLOADS:-100x1KiB 500x1KiB 1000x1KiB 100x10KiB 500x10KiB 1000x10KiB}"
REPEAT="${REPEAT:-3}"
RATE_MBPS="${RATE_MBPS:-100}"
VERIFY="${VERIFY:-size}"
DATA_DIR="$(mkdir -p "${DATA_DIR:-./bench-data}" && cd "${DATA_DIR:-./bench-data}" && pwd)"
RUN_ID="$(date -u +%Y%m%dT%H%M%SZ)"
OUT_DIR="$(mkdir -p "${OUT_DIR:-./bench-results/$RUN_ID}" && cd "${OUT_DIR:-./bench-results/$RUN_ID}" && pwd)"
CSV="$OUT_DIR/results.csv"
TARGET_THROUGHPUT="${TARGET_THROUGHPUT:-1Gb/s}"
VERIFY_AWS="${VERIFY_AWS:-aws}"   # the CLI used for checking results (not timed)

log()  { printf '[%s] %s\n' "$(date +%T)" "$*" >&2; }
die()  { log "ERROR: $*"; exit 1; }
have() { [[ " $TOOLS " == *" $1 "* ]]; }

# ---- Stack outputs ---------------------------------------------------------------------------------------------

stack_output() { # STACK KEY
  aws cloudformation describe-stacks --stack-name "$1" --query "Stacks[0].Outputs[?OutputKey=='$2'].OutputValue" \
    --output text 2>/dev/null | sed 's/^None$//'
}

if have raptor2; then
  RAPTOR2_BIN="${RAPTOR2_BIN:-$ROOT/microvm/build/cli/raptor2}"
  if [[ -z "${RAPTOR2_ENDPOINT:-}" ]]; then
    S="${RAPTOR2_STACK:-raptor2}"
    RAPTOR2_ENDPOINT="udp://$(stack_output "$S" DecoderListenerHost):$(stack_output "$S" DecoderListenerPort)"
    RAPTOR2_BUCKET="${RAPTOR2_BUCKET:-$(stack_output "$S" BucketName)}"
    RAPTOR2_PREFIX="${RAPTOR2_PREFIX:-$(stack_output "$S" BucketPrefix)}"
  fi
  RAPTOR2_PREFIX="${RAPTOR2_PREFIX-decoded/}"
  [[ -x "$RAPTOR2_BIN" ]] || die "raptor2 CLI not found at $RAPTOR2_BIN (microvm/scripts/publish.sh builds it)"
  [[ "$RAPTOR2_ENDPOINT" =~ ^udp://[^:]+:[0-9]+$ ]] || die "raptor2 endpoint not found (set RAPTOR2_STACK or RAPTOR2_ENDPOINT)"
  [[ -n "${RAPTOR2_BUCKET:-}" ]] || die "RAPTOR2_BUCKET is not set"
fi

if have raptor1; then
  RAPTOR1_BIN="${RAPTOR1_BIN:-$ROOT/publish/raptor}"
  if [[ -z "${RAPTOR1_ENDPOINT:-}" ]]; then
    S="${RAPTOR1_STACK:-raptorq}"
    RAPTOR1_ENDPOINT="wg://$(stack_output "$S" DecoderListenerHost):$(stack_output "$S" DecoderListenerPort)"
    RAPTOR1_SERVER_KEY="${RAPTOR1_SERVER_KEY:-$(stack_output "$S" DecoderListenerPublicKey)}"
    RAPTOR1_BUCKET="${RAPTOR1_BUCKET:-$(stack_output "$S" BucketName)}"
  fi
  RAPTOR1_PREFIX="${RAPTOR1_PREFIX-decoded/}"
  [[ -x "$RAPTOR1_BIN" ]] || die "raptor CLI not found at $RAPTOR1_BIN (dotnet publish -o ./publish -c Release ./src/raptor-cli/raptor-cli.csproj)"
  [[ "$RAPTOR1_ENDPOINT" =~ ^(udp|wg)://[^:]+:[0-9]+$ ]] || die "raptor1 endpoint not found (set RAPTOR1_STACK or RAPTOR1_ENDPOINT)"
  [[ "$RAPTOR1_ENDPOINT" != wg://* || ( -n "${RAPTOR1_SERVER_KEY:-}" && -n "${RAPTOR1_CLIENT_KEY:-}" ) ]] ||
    die "raptor1 uses WireGuard: set RAPTOR1_CLIENT_KEY (and RAPTOR1_SERVER_KEY if the stack has no output for it)"
  [[ -n "${RAPTOR1_BUCKET:-}" ]] || die "RAPTOR1_BUCKET is not set"
fi

if have awscli || have awspy; then
  AWS_NATIVE_BIN="${AWS_NATIVE_BIN:-aws}"
  AWSPY_BIN="${AWSPY_BIN:-aws}"
  AWSCLI_BUCKET="${AWSCLI_BUCKET:-${RAPTOR2_BUCKET:-${RAPTOR1_BUCKET:-}}}"
  AWSCLI_PREFIX="${AWSCLI_PREFIX:-bench-awscli/}"
  [[ -n "$AWSCLI_BUCKET" ]] || die "set AWSCLI_BUCKET"
  command -v "$AWS_NATIVE_BIN" >/dev/null || die "AWS CLI not found: $AWS_NATIVE_BIN"
  # The AWS CLI runs get a minimal config of their own (region and the CRT client settings), so the user's config is
  # never changed, and credentials exported from the current profile as environment variables. The native path
  # does not support every credential form (for example `aws login` sessions), so both CLI paths get the same plain
  # credentials. They are re-exported before each run, in case they are short-lived.
  BENCH_AWS_CONFIG="$OUT_DIR/aws-config"
  printf '[default]\nregion = %s\ns3 =\n  preferred_transfer_client = crt\n  target_throughput = %s\n' \
    "$AWS_REGION" "$TARGET_THROUGHPUT" > "$BENCH_AWS_CONFIG"
  "$VERIFY_AWS" configure export-credentials --format env-no-export > /dev/null ||
    die "could not export credentials for the AWS CLI runs (aws configure export-credentials)"
fi

# ---- Workload data ---------------------------------------------------------------------------------------------

to_bytes() { # 1KiB -> 1024
  local n="${1%%[A-Za-z]*}" u="${1##*[0-9.]}"
  case "$u" in
    ""|B) echo "$n" ;; KiB) echo $((n * 1024)) ;; MiB) echo $((n * 1024 * 1024)) ;; GiB) echo $((n * 1024 * 1024 * 1024)) ;;
    KB) echo $((n * 1000)) ;; MB) echo $((n * 1000000)) ;; GB) echo $((n * 1000000000)) ;;
    *) die "unknown size unit in $1" ;;
  esac
}

# Prints "<relative path> <file count> <total bytes>" for a workload, creating its data once.
prepare() {
  local w="$1" count size path
  if [[ "$w" == *x* ]]; then count="${w%%x*}"; size=$(to_bytes "${w#*x}"); else count=1; size=$(to_bytes "$w"); fi
  if (( count == 1 )); then
    path="w-$w.bin"
    [[ -f "$DATA_DIR/$path" && $(stat -c %s "$DATA_DIR/$path") == "$size" ]] ||
      head -c "$size" /dev/urandom > "$DATA_DIR/$path"
  else
    path="w-$w"
    if [[ ! -d "$DATA_DIR/$path" || $(find "$DATA_DIR/$path" -type f | wc -l) != "$count" ]]; then
      rm -rf "${DATA_DIR:?}/$path"; mkdir -p "$DATA_DIR/$path"
      for i in $(seq -w 1 "$count"); do head -c "$size" /dev/urandom > "$DATA_DIR/$path/f$i.bin"; done
    fi
  fi
  echo "$path $count $((count * size))"
}

# ---- Running and checking --------------------------------------------------------------------------------------

# run_tool TOOL PATH KEYPREFIX LOGFILE -> runs the upload; KEYPREFIX is the per-run part of the S3 key
run_tool() {
  local tool="$1" path="$2" tag="$3" logf="$4" dest
  case "$tool" in
    raptor2)
      # shellcheck disable=SC2086
      "$RAPTOR2_BIN" "$path" "$RAPTOR2_ENDPOINT" --rate-mbps "$RATE_MBPS" --prefix "$tag" --recursive ${RAPTOR2_ARGS:-} ;;
    raptor1)
      local keys=()
      [[ "$RAPTOR1_ENDPOINT" == wg://* ]] && keys=(--server-key "$RAPTOR1_SERVER_KEY" --client-key "$RAPTOR1_CLIENT_KEY")
      # shellcheck disable=SC2086
      "$RAPTOR1_BIN" "$path" "$RAPTOR1_ENDPOINT" "${keys[@]}" --rate-mbps "$RATE_MBPS" --prefix "$tag" --recursive ${RAPTOR1_ARGS---confirm BLOCK} ;;
    awscli|awspy)
      local bin="$AWS_NATIVE_BIN" router=native
      [[ "$tool" == awspy ]] && { bin="$AWSPY_BIN"; router=python; }
      dest="s3://$AWSCLI_BUCKET/$AWSCLI_PREFIX$tag/$path"
      local creds recursive=()
      mapfile -t creds < <("$VERIFY_AWS" configure export-credentials --format env-no-export)
      [[ -d "$path" ]] && recursive=(--recursive)
      env -u AWS_PROFILE -u AWS_DEFAULT_PROFILE -u AWS_SHARED_CREDENTIALS_FILE "${creds[@]}" \
        AWS_CONFIG_FILE="$BENCH_AWS_CONFIG" AWS_SHARED_CREDENTIALS_FILE=/dev/null AWS_CLI_NATIVE_ROUTER=$router \
        "$bin" s3 cp "$path" "$dest" "${recursive[@]}" --only-show-errors ;;
  esac > "$logf" 2>&1
}

# Bucket and key prefix where TOOL's upload of PATH with TAG lands.
dest_of() {
  case "$1" in
    raptor2) echo "$RAPTOR2_BUCKET ${RAPTOR2_PREFIX}$2/" ;;
    raptor1) echo "$RAPTOR1_BUCKET ${RAPTOR1_PREFIX}$2/" ;;
    awscli|awspy) echo "$AWSCLI_BUCKET $AWSCLI_PREFIX$2/" ;;
  esac
}

# verify TOOL PATH TAG -> prints "ok <ns>" (when the listing that proved it was taken) or the reason it failed.
# Every file must exist with the right size (and hash).
verify() {
  local tool="$1" path="$2" tag="$3" bucket prefix listing missing=0 f key size got
  read -r bucket prefix <<<"$(dest_of "$tool" "$tag")"
  local listed_ns; listed_ns=$(date +%s%N)
  listing=$("$VERIFY_AWS" s3api list-objects-v2 --bucket "$bucket" --prefix "$prefix" \
    --query 'Contents[].[Key,Size]' --output text 2>&1) || { echo "list failed: $listing"; return; }
  while IFS= read -r f; do
    key="$prefix$f"; size=$(stat -c %s "$DATA_DIR/$f")
    got=$(awk -v k="$key" -F'\t' '$1 == k { print $2 }' <<<"$listing")
    if [[ "$got" != "$size" ]]; then missing=$((missing + 1)); continue; fi
    if [[ "$VERIFY" == sha256 ]]; then
      [[ "$("$VERIFY_AWS" s3 cp "s3://$bucket/$key" - --only-show-errors | sha256sum | cut -d' ' -f1)" == \
         "$(sha256sum < "$DATA_DIR/$f" | cut -d' ' -f1)" ]] || missing=$((missing + 1))
    fi
  done < <(cd "$DATA_DIR" && find "$path" -type f | sed 's#^\./##' | sort)
  (( missing == 0 )) && echo "ok $listed_ns" || echo "$missing file(s) missing or wrong"
}

# ---- Main ------------------------------------------------------------------------------------------------------

{
  echo "# Benchmark $RUN_ID"
  echo
  echo "- Host: $(uname -srm), $(nproc) CPU(s); region $AWS_REGION; raptor rate limit ${RATE_MBPS} Mbps; AWS CLI target ${TARGET_THROUGHPUT:-n/a}"
  have raptor2 && echo "- raptor2: $RAPTOR2_ENDPOINT -> s3://$RAPTOR2_BUCKET/$RAPTOR2_PREFIX"
  have raptor1 && echo "- raptor1: $RAPTOR1_ENDPOINT -> s3://$RAPTOR1_BUCKET/$RAPTOR1_PREFIX"
  { have awscli || have awspy; } && echo "- awscli: $("$AWS_NATIVE_BIN" --version 2>&1 | head -1) -> s3://$AWSCLI_BUCKET/$AWSCLI_PREFIX"
} > "$OUT_DIR/summary.md"
echo "tool,workload,run,files,bytes,wall_s,user_s,sys_s,exit,verify,durable_s" > "$CSV"
log "Results in $OUT_DIR"

cd "$DATA_DIR"
TIMEFORMAT='%R %U %S'
for w in $WORKLOADS; do
  read -r path count bytes <<<"$(prepare "$w")"
  for run in $(seq 1 "$REPEAT"); do
    for tool in $TOOLS; do          # tools alternate within a round, so drift affects them equally
      tag="bench/$RUN_ID/$tool/$w/$run"
      logf="$OUT_DIR/$tool-$w-$run.log"
      log "$tool $w run $run..."
      start_ns=$(date +%s%N)
      t=$( { time run_tool "$tool" "$path" "$tag" "$logf"; echo "exit=$?" >> "$logf"; } 2>&1 )
      code=$(grep -o 'exit=[0-9]*' "$logf" | tail -1 | cut -d= -f2)
      read -r wall user sys <<<"$t"
      # Poll S3 until every file is there (and right), or the timeout passes.
      deadline=$(( $(date +%s) + ${DURABLE_TIMEOUT:-120} ))
      while true; do
        result=$(verify "$tool" "$path" "$tag")
        [[ "$result" == ok\ * || $(date +%s) -ge $deadline ]] && break
        sleep 0.5
      done
      durable="-"
      if [[ "$result" == ok\ * ]]; then
        durable=$(awk -v a="$start_ns" -v b="${result#ok }" 'BEGIN { printf "%.3f", (b - a) / 1e9 }')
        result=ok
      fi
      log "  $tool $w: ${wall}s wall, ${durable}s until in S3, ${user}s user, ${sys}s sys, exit $code, $result"
      echo "$tool,$w,$run,$count,$bytes,$wall,$user,$sys,$code,$result,$durable" >> "$CSV"
    done
  done
done

# Median per tool and workload; only successful, verified runs count.
{
  echo
  echo "| Workload | Tool | Runs ok | Until in S3, s (median) | CLI exit, s (median) | Client CPU s (median) | Goodput Mbps |"
  echo "|---|---|---|---|---|---|---|"
  awk -F, 'NR > 1 { k = $2 "|" $1; n[k]++; if ($10 == "ok") { ok[k]++; w[k] = w[k] " " $11; x[k] = x[k] " " $6; c[k] = c[k] " " ($7 + $8); b[k] = $5 } }
    function median(s,   a, m, i, j, t) { m = split(s, a, " "); for (i = 1; i <= m; i++) for (j = i + 1; j <= m; j++) if (a[j] < a[i]) { t = a[i]; a[i] = a[j]; a[j] = t }
      return m == 0 ? "" : (m % 2 ? a[(m + 1) / 2] : (a[m / 2] + a[m / 2 + 1]) / 2) }
    END { for (k in n) { split(k, p, "|"); mw = median(w[k]); mx = median(x[k]); mc = median(c[k]);
      printf "| %s | %s | %d/%d | %s | %s | %s | %s |\n", p[1], p[2], ok[k], n[k], mw == "" ? "-" : sprintf("%.2f", mw),
        mx == "" ? "-" : sprintf("%.2f", mx), mc == "" ? "-" : sprintf("%.2f", mc),
        mw == "" || mw == 0 ? "-" : sprintf("%.1f", b[k] * 8 / mw / 1e6) } }' "$CSV" | sort
} >> "$OUT_DIR/summary.md"
cat "$OUT_DIR/summary.md"

if [[ "${CLEANUP:-0}" == 1 ]]; then
  log "Deleting this run's objects..."
  for tool in $TOOLS; do
    read -r bucket prefix <<<"$(dest_of "$tool" "bench/$RUN_ID")"
    "$VERIFY_AWS" s3 rm "s3://$bucket/$prefix" --recursive --only-show-errors
  done
fi
