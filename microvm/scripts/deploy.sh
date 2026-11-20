#!/usr/bin/env bash
#
# Builds and deploys raptor2:
#   1. publish the NativeAOT MicroVM app into microvm/build/image/ (publish.sh), plus the native CLI
#   2. zip build/image/ and upload it to S3 (named by content hash)
#   3. deploy microvm/template.yaml with CodeUri pointing at the upload
#
# Usage: microvm/scripts/deploy.sh [extra --parameter-overrides, e.g. Protocol=wg PeerPublicKey=... MemoryMiB=4096]
#   STACK_NAME   default raptor2
#   AWS_REGION   default us-west-2
#   CLIENT_CIDR  default: your current public IPv4 address /32
#   BUCKET       artifact bucket in the region (default: the SAM managed bucket)
#   RID          linux-x64 (default) or linux-arm64
#   SKIP_PUBLISH=1  reuse build/image/ as is
#
# SAM CLI can't yet upload a local CodeUri for AWS::Serverless::MicrovmImage (aws/aws-sam-cli#9283), so this script
# does that part itself.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
STACK_NAME="${STACK_NAME:-raptor2}"
export AWS_REGION="${AWS_REGION:-us-west-2}" AWS_DEFAULT_REGION="${AWS_REGION:-us-west-2}"
ARCH=X86_64; [[ "${RID:-linux-x64}" == linux-arm64 ]] && ARCH=ARM_64

[[ "${SKIP_PUBLISH:-0}" == 1 ]] || bash "$HERE/publish.sh"
IMAGE="$ROOT/microvm/build/image"
[[ -x "$IMAGE/raptor2-microvm" ]] || { echo "$IMAGE/raptor2-microvm is missing; run publish.sh" >&2; exit 1; }

ACCOUNT=$(aws sts get-caller-identity --query Account --output text)
STATUS=$(aws cloudformation describe-stacks --stack-name "$STACK_NAME" --query 'Stacks[0].StackStatus' --output text 2>/dev/null || true)
if [[ "$STATUS" == ROLLBACK_COMPLETE ]]; then
  echo "Stack $STACK_NAME is in ROLLBACK_COMPLETE from a failed first deploy; deleting it (it holds no resources)."
  aws cloudformation delete-stack --stack-name "$STACK_NAME"
  aws cloudformation wait stack-delete-complete --stack-name "$STACK_NAME"
fi

CLIENT_CIDR="${CLIENT_CIDR:-$(curl -fs https://checkip.amazonaws.com | tr -d '[:space:]')/32}"
echo "Account $ACCOUNT, region $AWS_REGION, stack $STACK_NAME, allowing $CLIENT_CIDR"

BUCKET="${BUCKET:-$(aws cloudformation describe-stacks --stack-name aws-sam-cli-managed-default \
  --query "Stacks[0].Outputs[?OutputKey=='SourceBucket'].OutputValue" --output text 2>/dev/null || true)}"
if [[ -z "$BUCKET" || "$BUCKET" == None ]]; then
  echo "No artifact bucket: set BUCKET, or run any 'sam deploy --guided' once to create the SAM managed bucket." >&2
  exit 1
fi

mkdir -p "$ROOT/microvm/.aws-sam"
HASH=$(cat "$IMAGE"/* | sha256sum | cut -c1-16)
ZIP="$ROOT/microvm/.aws-sam/raptor2-image-${HASH}.zip"
KEY="raptor2/image-${HASH}.zip"
rm -f "$ZIP"
(cd "$IMAGE" && zip -q -X "$ZIP" Dockerfile raptor2-microvm)
aws s3 cp "$ZIP" "s3://${BUCKET}/${KEY}" --only-show-errors
echo "Uploaded s3://${BUCKET}/${KEY} ($(du -h "$ZIP" | cut -f1))"

# Newest al2023-1 base image version for the architecture (x86_64 needs 3 or later). BASE_IMAGE_VERSION overrides.
BASE_VERSION="${BASE_IMAGE_VERSION:-}"
if [[ -z "$BASE_VERSION" ]]; then
  MIN=0; [[ $ARCH == X86_64 ]] && MIN=3
  BASE_VERSION=$(aws lambda-microvms list-managed-microvm-image-versions \
    --image-identifier "arn:aws:lambda:${AWS_REGION}:aws:microvm-image:al2023-1" --output json |
    jq -r --arg a "$ARCH" --argjson min "$MIN" '[.items[]
        | select((.status // "AVAILABLE") == "AVAILABLE")
        | select(if .supportedCpuConfigurations then any(.supportedCpuConfigurations[]; .architecture == $a)
                 else (.imageVersion | tonumber) >= $min end)
        | .imageVersion] | sort_by(tonumber) | last // empty')
fi
[[ -n "$BASE_VERSION" ]] || { echo "No al2023-1 base image version supports $ARCH in $AWS_REGION." >&2; exit 1; }
echo "Base image al2023-1 version $BASE_VERSION ($ARCH)"

sed -E "s#^([[:space:]]*CodeUri:).*\$#\1 s3://${BUCKET}/${KEY}#" "$ROOT/microvm/template.yaml" > "$ROOT/microvm/.aws-sam/packaged.yaml"
sam deploy \
  --template-file "$ROOT/microvm/.aws-sam/packaged.yaml" \
  --stack-name "$STACK_NAME" \
  --region "$AWS_REGION" \
  --capabilities CAPABILITY_IAM CAPABILITY_AUTO_EXPAND \
  --no-fail-on-empty-changeset \
  --parameter-overrides ClientCidrToAllow="$CLIENT_CIDR" Architecture="$ARCH" BaseImageVersion="$BASE_VERSION" "$@"

HOST=$(aws cloudformation describe-stacks --stack-name "$STACK_NAME" --query "Stacks[0].Outputs[?OutputKey=='DecoderListenerHost'].OutputValue" --output text)
PORT=$(aws cloudformation describe-stacks --stack-name "$STACK_NAME" --query "Stacks[0].Outputs[?OutputKey=='DecoderListenerPort'].OutputValue" --output text)
echo
echo "Listener: udp://$HOST:$PORT"
echo "Try:      microvm/build/cli/raptor2 somefile udp://$HOST:$PORT --rate-mbps 100"
