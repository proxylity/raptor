# Raptor on Lambda MicroVMs: design

Goal: the same "blast away" UDP upload to S3 as the original raptor, with no DynamoDB, no SQS, no per-packet storage,
and no handshake before data flows. Small files (up to 1 MiB by default) are the target; the protocol also carries
multipart objects up to the S3 limit (5 TiB), see "File size limit, and why".

## Shape

```
raptor CLI --UDP--> Proxylity listener --(batches per tenant = slot)--> MicroVM "slot N" --> S3
                                          <-- HAVE replies (matched by Tag) --
```

* The MicroVM destination's `TenantIdExpression` is `[1:2]` (hex). **The tenant is the slot byte**
  that the client writes at byte 1 of every datagram. A slot is one long-lived, shared MicroVM.
* A MicroVM keeps its state in memory, keyed by `(transferId, objectIdx)`. There is no external state store.
* S3 is the only shared state, and only for multipart objects (see "Sharing the upload").

## Layout (a pure function of the object size F)

`Layout.For(F)` is computed identically by the client and the MicroVM, so packets don't carry it.

| Item | Rule |
|---|---|
| Block | 512 KiB. Blocks are numbered 0..N-1 across the object (3 bytes, so up to 8 TiB). |
| Symbols | `F <= 512 KiB`: one block, `K = ceil(F/1280)`, `T = ceil(F/K)`. Larger: `K = 512`, `T = 1024`; the last block is short (`K = ceil(rest/1024)`). |
| Segment | `2^shift` consecutive blocks, `shift = max(5, ceilLog2(ceil(N/9000)))`. A segment is one S3 multipart part (16 MiB minimum, at most 9000 parts). Part number is `segment + 1`. |
| Single-part objects | one segment: committed with a plain `PutObject`. |

The client never sends or tracks part numbers. It sends blocks. The segment size only matters to the
slot calculation and to the order in which the client interleaves its sends.

## Routing (the "clever tenant")

```
salt  = hash(transferId)
small (one segment):  slot = salt + objectIdx            mod SmallSlots(4)
large:                slot = SmallSlots + (salt + segment) mod LargeSlots(60)
```

* Consecutive segments land on different slots, so an object spreads over up to 60 VMs by itself.
* **Parallelism is the client's send order.** The client keeps `W` segments in flight and
  round-robins them. `W` is the send rate divided by about 300 Mbps (the rate one slot can ingest
  at 40 batches per second of 1000 packets), so a 20 Mbps link uses one VM and 10 Gbps uses about 34.
* Slots are shared across transfers, so the VMs stay warm. Per-transfer state is keyed inside the VM.
* Each slot in use is one MicroVM. The pool size is the client's choice (`--small-slots`, `--large-slots`) and has to
  fit the account's MicroVM memory quota (8 GB by default: four 2 GB MicroVMs).
* The batch `Count` is 1000 and the timeout 100 ms. A slot sees at most 10 batches/s from the timer,
  and 40 batches/s at 40k packets/s.

## Packet format (client to VM)

```
 0      type(4) | version(4)       1 = DATA, 2 = STATUS; version 2
 1      slot                       tenant, see above
 2-9    transferId                 random per invocation
 10-12  objectIdx                  index of the file within the transfer
 13-18  F                          object size in bytes (48 bits)
 19-21  blockNo                    global block number
 22-23  ESI                        encoding symbol id (STATUS: 0)
 24     keyLen                     <= 95; 0 = key omitted (DATA only)
 25..   key                        UTF-8 object key (relative path, with the timestamp prefix)
 ..     symbol                     T bytes (DATA only)
```

Every packet is self-describing apart from the key, so any packet can start the state for an object, including
the first packet to reach a freshly started MicroVM. To save bytes, DATA packets carry the key only for every 16th
source symbol, every repair symbol, every packet of a block with K <= 16, and every packet of a repair round
(`ClientPacket.CarriesKey`). `STATUS` always carries it. A MicroVM that has a full segment but no key yet holds it,
and commits it when the first keyed packet or `STATUS` arrives. Version 1 packets are rejected, so a client and a
MicroVM image from different versions fail loudly rather than silently. With a 45-byte key this cuts the header
from about 70 to about 28 bytes per packet.

## Replies (VM to client): `HAVE`

```
 0      type(4)=3 | version(4)
 1-8    transferId
 9-11   objectIdx
 12     flags: bit0 = object complete (durable in S3)
 13-15  scopeStart   } STATUS replies: the runs are a full snapshot of this block range (one segment),
 16-18  scopeCount   } so a block that is not listed is not held. 0 for incremental replies.
 19-20  runCount
 21..   runs: blockStart(3) blockCount(3)   blocks decoded, or committed
 ..     needCount(2), then needs: block(3) need(2)    STATUS replies only (may be absent)
```

`needs` lists, for each incomplete block in the segment, how many more distinct symbols the MicroVM needs:
K minus what it holds (at least 1), or K if it holds nothing for the block.

Sent in the HTTP response to every batch with packets for the object (listing the blocks that batch touched that
are now held), and in answer to `STATUS`. A `STATUS` names a block and asks about that block's segment. A reply can only travel in a batch response, so durable completion of a multipart object is
learned by polling with `STATUS`. Single-part objects are committed before the response is returned, so
a small file is confirmed in the response to its last packet.

## Decoding

Source symbols (ESI < K) are copied straight into the segment buffer at their final offset. A block is
complete when all K source symbols have arrived (no decoding). Otherwise it is decoded as soon as at
least K distinct symbols have arrived (attempts at K, K+1, K+2, then every 8). The client sends the K source
symbols first, then the repair symbols, so there is no encoding on the critical path.

## Sharing the upload (no handshake)

A segment's `UploadPart` needs the multipart upload ID. The first VM that needs it:

1. `GetObject <prefix>.raptor/<transferId>/<objectIdx>.upload`. If it exists, use the ID in it.
2. Otherwise `CreateMultipartUpload`, then `PutObject` the claim with `If-None-Match: *`.
3. If the claim PUT loses (412), abort its own upload and read the winner's.

After each part commit near the tail (and on every `STATUS`), the VM lists parts. When all are present
it calls `CompleteMultipartUpload` and writes `<prefix>.raptor/<transferId>/<objectIdx>.done`. Complete is
idempotent. A lifecycle rule expires `<prefix>.raptor/` and aborts stale multipart uploads. The execution role needs
`s3:ListBucket` on that prefix; without it, reading a marker that does not exist yet returns 403 instead of 404.

## Reliability and resume

* **Up-front repair adapts to the measured loss** (`--overhead auto`, the default; a number fixes it). For each
  segment the client records how many blocks it sent, how many repair symbols each carried, and how many still
  needed a repair round. Losses per block are modelled as Poisson(p K), and p is the maximum-likelihood fit over
  recent segments, weighted toward the latest. A segment far worse than predicted clears the history, so the
  estimate follows sudden changes. Each block then gets the fewest repair symbols that keep the chance of needing
  a repair round under 5% (0.5% for an object's last segment, which holds up its completion). Blocks with
  K <= 64 always get at least one. On a clean link the count reaches 0 within a few segments, and then blocks
  need no encoding at all.
* The client repairs any block that is not in the `HAVE` runs, with a growing timeout (250 ms doubling, capped
  by `--block-timeout`). Before each repair pass it sends one `STATUS` for the segment and waits for the
  snapshot. The repair round sends exactly what the snapshot says each block needs, plus a margin of
  max(25%, 3p), at least 2 symbols, all with new ESIs. When the MicroVM holds nothing for a block, or there is no
  snapshot, it resends the source symbols plus 25% of K. `STATUS` returns the VM's runs, or the committed runs from `ListParts` if the VM has
  no state, so a replaced VM (8 hour limit, termination) recovers from S3.
* Resume (later): the client persists `transferId` and the object list. On restart it sends `STATUS`,
  gets the committed and held runs, and sends only the rest. Nothing in the protocol changes.
* A MicroVM that is terminated mid-segment loses that segment's buffer. The client sees the runs
  disappear and resends those blocks.

## Client sending

One dedicated thread owns the socket. Block workers read the file, build each block's datagrams into one
pooled buffer, and hand the whole block over, so there is one handoff per block. The wire thread paces with a
token bucket, sends up to 32 datagrams per `sendmmsg(2)` call on Linux (one `send` at a time elsewhere), and
sleeps only once it is a quantum ahead (about 16 packets' worth, 2-8 ms). At 30 Mbps this halved the
client's CPU time against async sends paced with `Task.Delay`, whose timer wake-ups dominated.

## Limits to keep in mind

* Key length is at most 95 bytes (the symbol size leaves room within 1400 bytes).
* A slot holds one segment buffer (16 to 128 MiB) per segment in flight on it. The VM caps the total
  in-flight bytes and drops packets beyond it.
* The VM lives at most 8 hours. A transfer slower than 8 hours per segment cannot complete a segment.

## File size limit, and why

The CLI refuses files over 1 MiB by default (`--max-size` raises it). Up to 1 MiB a file is one or two blocks: it
goes to a single small-file slot, is committed with one `PutObject`, and is confirmed in the reply to its last packet.
That is where the design is fastest, simplest and cheapest, and it is what the benchmark measures.

Larger files work (multipart parts spread over the large-file lanes, see "Routing"), but they stop paying off:

* **A MicroVM accepts a limited number of requests per second.** The account quotas list 10 requests per second per
  vCPU (40 for a 4 vCPU MicroVM, 160 for 16), and a MicroVM's vCPU count is set by its memory baseline
  (2 GB = 1 vCPU, bursting to 4x). Throughput per MicroVM is that request rate times the packets per batch.
* **In practice that was about 30 Mbps per 2 GB MicroVM.** From EC2 in us-west-2, three 2 GB large-file MicroVMs
  moved a 100 MiB file in 9.3 s at 100 Mbps; at 300 Mbps the extra traffic turned into repair rounds and took 11.6 s.
  Larger batches (2,000 packets, 250 ms) did not raise the ceiling.
* **More throughput means more MicroVM baseline, paid for while it runs.** At the published Graviton rates
  ($0.0000276944 per vCPU-second and $0.0000036667 per GB-second, us-east-1), a 2 GB MicroVM is about $0.13 an hour.
  A few Gbps would need about 256 GB of MicroVMs: about $16 an hour, and a quota increase. A direct S3 multipart
  upload (`aws s3 cp`) gets that speed for almost nothing.

## Status of this design (what is verified)

Verified locally, without AWS (`src/raptor2-tests`, `microvm/scripts/local-test.sh`, `bench/selftest.sh`): the wire
format, layout and routing; decoding under loss, duplicates and reordering; single-part and multipart commits with
several MicroVMs racing on one object; a replacement MicroVM recovering state from the sink; the memory cap; key
validation; NativeAOT builds of the CLI and the MicroVM app.

Verified on AWS (us-west-2): the `[1:2]` tenant expression with the hex formatter; 1,000-packet batches; S3 calls from
the NativeAOT app inside a MicroVM; small-file uploads from 1 KiB to 1 MiB; multipart uploads to 127 MiB.