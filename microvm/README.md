# Deploying and operating the MicroVM backend

This folder holds the backend for raptor: a SAM template (`template.yaml`), the MicroVM image's `Dockerfile`
(`image/`), and the build, deploy and test scripts (`scripts/`). The [readme](../readme.md) explains what raptor does,
and [docs/design-microvm.md](../docs/design-microvm.md) explains how.

## Prerequisites

- Linux or WSL 2, with the .NET 10 SDK and gcc or clang (for NativeAOT).
- `jq`, `zip`, `curl`, SAM CLI, and an AWS CLI with the `lambda-microvms` commands.
- A [Proxylity UDP Gateway](https://aws.amazon.com/marketplace/pp/prodview-cpvl5wgt2yo2e) subscription, and Lambda
  MicroVMs in the region.

## Try it locally (no AWS)

```bash
dotnet test src/raptor2-tests
microvm/scripts/publish.sh            # native MicroVM app and native CLI into microvm/build/
microvm/scripts/local-test.sh 0.03    # CLI -> local gateway stand-in (3% loss) -> 4 local MicroVM processes -> a folder
bench/selftest.sh                     # the benchmark against the local stack, with a fake `aws`
```

## Deploy

```bash
microvm/scripts/deploy.sh                                       # stack raptor2 in us-west-2; allows your public IP
microvm/scripts/deploy.sh Protocol=wg PeerPublicKey=<base64>    # a WireGuard listener instead of plain UDP
```

`deploy.sh` publishes the NativeAOT app, uploads the image zip (SAM CLI can't upload a local `CodeUri` for
`AWS::Serverless::MicrovmImage` yet, [aws-sam-cli#9283](https://github.com/aws/aws-sam-cli/issues/9283)), picks the
newest base image version for the architecture, deploys, and prints the endpoint. Settings:

| Variable / parameter | Default | Notes |
|---|---|---|
| `STACK_NAME`, `AWS_REGION` | `raptor2`, `us-west-2` | |
| `CLIENT_CIDR` | your public IPv4 /32 | Only this range can send to the listener |
| `SKIP_PUBLISH=1` | | Reuse `build/image/`, for example to change only parameters |
| `RID` | `linux-x64` | `linux-arm64` builds for ARM64 MicroVMs |
| `MemoryMiB` | 2048 | MicroVM baseline: 2 GB = 1 vCPU, bursting to 4x |
| `IdleDurationSeconds` | 900 | Idle time before a MicroVM suspends; the next packet resumes it |
| `BatchCount` | 1000 | Packets per delivery; a batch is also sent 100 ms after its first packet |
| `ExistingBucketName`, `BucketPrefix` | a new bucket, `decoded/` | |

`microvm/scripts/teardown.sh` terminates the MicroVMs (Proxylity starts them, so CloudFormation doesn't know about
them) and deletes the stack.

## Operating notes

- **MicroVM memory quota.** Each slot in use is one MicroVM. The default quota (Max allocated X86_64 MicroVM memory)
  is 8 GB, which is four 2 GB MicroVMs: the CLI's default of 4 small-file slots. Use `--small-slots` and
  `--large-slots` to match a different quota. Slots beyond the quota fail to start, and the delivery log says "The
  base maximum allocated memory limit has been reached for this account".
- **A new image version doesn't replace running MicroVMs.** After a deploy that changes the app, terminate them (see
  `teardown.sh` for the commands). A MicroVM terminated within 5 minutes of starting is not replaced for 5 minutes.
- **Cold starts.** The first packets for a slot start its MicroVM, and they are lost or answered late; the client
  repairs them. A suspended MicroVM resumes on the next packet, and the first batch may get a 502 while it does.
- **Logs.** The MicroVMs write one JSON line per commit and a stats line every 30 s to the `MicrovmLogGroup` output.
  Delivery errors go to `DeliveryLogGroup`.
