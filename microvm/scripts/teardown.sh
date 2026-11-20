#!/usr/bin/env bash
# Terminates the MicroVMs (Proxylity starts them, so CloudFormation doesn't know about them), then deletes the stack.
# A bucket the stack created must be empty before it can be deleted; this script leaves objects alone.
set -euo pipefail
STACK_NAME="${STACK_NAME:-raptor2}"
export AWS_REGION="${AWS_REGION:-us-west-2}" AWS_DEFAULT_REGION="${AWS_REGION:-us-west-2}"
IMAGE_ARN=$(aws cloudformation describe-stacks --stack-name "$STACK_NAME" \
  --query "Stacks[0].Outputs[?OutputKey=='MicrovmImageArn'].OutputValue" --output text)
for id in $(aws lambda-microvms list-microvms --image-identifier "$IMAGE_ARN" \
    --query "items[?state!='TERMINATED' && state!='TERMINATING'].microvmId" --output text); do
  echo "Terminating MicroVM $id"
  aws lambda-microvms terminate-microvm --microvm-identifier "$id" >/dev/null
done
echo "Deleting stack $STACK_NAME..."
aws cloudformation delete-stack --stack-name "$STACK_NAME"
aws cloudformation wait stack-delete-complete --stack-name "$STACK_NAME"
echo "Done."
