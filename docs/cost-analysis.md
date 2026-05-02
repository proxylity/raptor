# Raptor — Cloud Cost Analysis

> S3 API costs are excluded; only the incremental costs introduced by the RaptorQ
> pipeline (DynamoDB, Lambda, Proxylity) are considered.

---

## Encoding Parameters

All figures use the default 5% repair overhead (`--overhead 0.05`).
`CalculateSymbolSize` in the CLI finds the smallest power-of-2 K such that each symbol
fits within `MAX_SYMBOL_SIZE` (1,280 bytes). For a full 512 KiB block that yields
K = 512 and a **1,024-byte symbol**. Block size is capped at `MAX_BLOCK_BYTES` = 512 KiB.
The SBN field is 1 byte, so the maximum supported file is 255 blocks ≈ **~127 MiB**.

| Parameter | 1 MiB | 100 MiB | ~127 MiB (max) |
|---|---|---|---|
| Symbol size | 1,024 B | 1,024 B | 1,024 B |
| Blocks | 2 | 200 | 255 |
| K per block | 512 | 512 | 512 |
| T per block (K × 1.05) | 538 | 538 | 538 |
| **Total packets sent** | **1,076** | **107,600** | **137,190** |
| Ingestor Lambda invocations (÷ 100 batch) | 11 | 1,076 | 1,372 |

---

## DynamoDB

Every received symbol is written as a `PACKET|` item. The item payload is
Base64(1,024 B) = 1,368 B of symbol data plus attribute overhead ≈ 1,550 B total,
costing **2 WRUs** to write and **2 RRUs** to read back in the BlockCompleter.

### Operation Counts

| Operation | 1 MiB | 100 MiB | ~127 MiB (max) |
|---|---|---|---|
| `PACKET` `BatchWriteItem` (T × 2 WRU) | 2,152 | 215,200 | 274,380 |
| `PACKET` `Query` in BlockCompleter (K × 2 RRU) | 2,048 | 204,800 | 261,120 |
| `METADATA` `UpdateItem` per Lambda invoke (1 WRU) | 11 | 1,076 | 1,372 |
| LOCK / OVERALL / ETag items (~4 WRU / block) | 8 | 800 | 1,020 |
| `IsBlockCommitted` `GetItem` per invoke (1 RRU) | 11 | 1,076 | 1,372 |
| **Total WRUs** | **~2,171** | **~217,076** | **~276,772** |
| **Total RRUs** | **~2,059** | **~205,876** | **~262,492** |

### DynamoDB Cost

Pricing: $1.25/M WRUs, $0.25/M RRUs (on-demand, `PAY_PER_REQUEST`).

| | 1 MiB | 100 MiB | ~127 MiB (max) |
|---|---|---|---|
| WRU cost | $0.0027 | $0.2714 | $0.3460 |
| RRU cost | $0.0005 | $0.0515 | $0.0656 |
| **DDB total** | **$0.0032** | **$0.3229** | **$0.4116** |

DDB storage (items held 4h via TTL) is negligible at all three sizes.

---

## Lambda

All three Lambda functions are configured at 1,024 MB RAM. Durations are measured
warm-start times.

| Function | Memory | Invocations | Duration (warm) | 1 MiB | 100 MiB | ~127 MiB (max) |
|---|---|---|---|---|---|---|
| PacketIngestor | 1 GB | T ÷ 100 | ~27.5 ms | 0.30 GB-s | 29.6 GB-s | 37.7 GB-s |
| CompletionEvaluator | 1 GB | 1 per block | ~1.5 ms | 0.003 GB-s | 0.300 GB-s | 0.383 GB-s |
| BlockCompleter | 1 GB | 1 per block | ~1,500 ms | 3.0 GB-s | 300.0 GB-s | 382.5 GB-s |
| **Lambda total** @ $0.0000167/GB-s | | | | **$0.000055** | **$0.0055** | **$0.0070** |

Lambda request charges ($0.20/M invocations) are < $0.001 even at the maximum file size and are
absorbed in the figures above.

---

## Proxylity Gateway

Proxylity receives the raw UDP stream and batches it into Lambda invocations.

### Traffic Figures

| | 1 MiB | 100 MiB | ~127 MiB (max) |
|---|---|---|---|
| Packets ingressed | 1,076 | 107,600 | 137,190 |
| Wire bytes (~1,050 B/pkt typical) | ~1.1 MB | ~113 MB | ~144 MB |
| Lambda dispatches | 11 | 1,076 | 1,372 |

### Proxylity Cost (first paid tier, $1.25/M)

| | 1 MiB | 100 MiB | ~127 MiB (max) |
|---|---|---|---|
| Proxylity cost | $0.0013 | $0.1345 | $0.1715 |

Even at the maximum supported file size (~137K packets), the transfer is well within the
1M free-tier boundary. Proxylity costs under $0.20 at all supported file sizes.

### Enterprise Pricing (per batch)

Under the enterprise plan, charges are per **batch delivered to the Lambda destination**
rather than per packet. The destination batching configuration sets a maximum of
**100 packets per batch**, so batch count equals the ingestor Lambda dispatch count.

All three file sizes fall well within the 10M first tier.

| | 1 MiB | 100 MiB | ~127 MiB (max) |
|---|---|---|---|
| Batches dispatched | 11 | 1,076 | 1,372 |
| Enterprise cost @ $2.50/M | $0.0000275 | $0.00269 | $0.00343 |

Enterprise batch pricing is approximately **50× cheaper** than per-packet pricing for
this workload. The economics favour enterprise pricing for any use case where the
batch size is consistently close to the configured maximum (100 packets here), because
you pay once per batch regardless of how many packets it contains.

---

## Summary

| Cost Component | 1 MiB | 100 MiB | ~127 MiB (max) |
|---|---|---|---|
| DynamoDB | $0.0032 | $0.3229 | $0.4116 |
| Lambda | $0.0001 | $0.0055 | $0.0070 |
| Proxylity | $0.0013 | $0.1345 | $0.1715 |
| **Total** | **$0.0046** | **$0.4629** | **$0.5901** |
| **Cost per MiB** | | | **~$0.0046** |

### Cost Share at 100 MiB

| Component | Share |
|---|---|
| DynamoDB | ~69.8% |
| Proxylity | ~29.1% |
| Lambda | ~1.2% |

---

## Key Observations

**DynamoDB and Proxylity together account for ~99% of the bill at 100 MiB**, with DynamoDB
at ~70% and Proxylity at ~29%. Lambda is ~1%. The DynamoDB cost is driven by a single
design choice: every symbol (one per received packet) is written individually as a ~1.5 KB
item, then read back in bulk at decode time — 2 WRUs + 2 RRUs per packet.

**Cost scales linearly with file size** at roughly **$0.0046/MiB** of source data.
At 1,024-byte symbols and 5% overhead there are 538 packets per 512 KiB block
(~1,076 packets/MiB). Each packet costs ~$3.00/M in DDB WRU + RRU charges and
~$1.25/M in Proxylity charges, for a combined ~$4.25/M packets or **~$0.0046/MiB**.

**For comparison**, a direct S3 `PutObject` or multipart upload has zero DDB cost and
negligible Lambda cost. The entire incremental cost of the fountain-code pipeline
comes from DynamoDB acting as a temporary symbol store between the ingestor and the
block completer.

**Enterprise batch pricing reduces the Proxylity line item by ~50×** (from ~$0.13 to
~$0.003 for a 100 MiB file assuming 100 packets per batch), making DynamoDB an even
more dominant cost driver at ~98% of the total under that plan.

**Replace DDB with ValKey** to bring the inflight storage cost down to reasonable levels. Doing so requires a VPC and complicates the stack, but would make sense if the service were used regularly or at high data volumes. Sending 1000 1KB files costs about $0.015.

