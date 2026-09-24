# Audit log: Merkle tree specification

Every accepted rating becomes a leaf of an append-only Merkle tree. The registry periodically
anchors the tree's root and serves an inclusion proof for any anchored rating. This document is
the normative description: an independent verifier should be implementable from it alone, and
[`tools/verify-proof/verify-proof.mjs`](../tools/verify-proof/verify-proof.mjs) is one, written
from this text with no dependency on the server code.

## What a proof establishes

For a rating stored with a **v2 leaf**, a valid proof shows that the rating, with exactly the
committed fields below, is part of the anchored log. Changing any committed field afterwards
(the status code, the weight, whether a signature was verified) makes the fields stop hashing to
the leaf that is in the tree.

For a rating stored with a **v1 leaf** (every rating written before v2), a proof only shows that a
rating with that id, service and timestamp is in the log, not what it reported.

What a proof does not establish, whatever the version:

- **When** the root was fixed. Until on-chain anchoring is active (see below), roots are published
  by the registry itself, so the guarantee is against silent edits after publication, observable
  by anyone who kept an earlier root, not against the operator rewriting history before anyone
  looked.
- **Who** rated. The agent DID is not committed (see [Privacy](#privacy)).
- That the rating is **honest**. The log records what was submitted and how it was weighted.

## Leaf v1 (legacy)

```
leaf = SHA256( UTF8( id + ":" + service + ":" + created_at ) )
```

- `id`: the rating id, lowercase, hyphenated (`3f2b8c1e-5d4a-4b7e-9c2f-0a1b2c3d4e5f`)
- `service`: the normalized service id
- `created_at`: ISO 8601 round-trip form with seven fractional digits and an offset, e.g.
  `2026-09-01T12:00:00.0000000+00:00`

No prefix, no domain separation. Kept only so that existing ratings stay provable.

## Leaf v2

```
leaf = SHA256( 0x00 || UTF8( canonical ) )
```

`canonical` is the following lines joined by a single `\n` (0x0A), with no trailing newline:

| # | Field | Spelling |
|---|---|---|
| 1 | domain | the literal `trustscore-leaf-v2` |
| 2 | `id` | lowercase, hyphenated |
| 3 | `service` | normalized service id |
| 4 | `created_at` | UTC, microseconds, `Z`: `2026-09-24T16:02:55.123456Z` |
| 5 | `status_code` | decimal integer |
| 6 | `latency_ms` | decimal integer |
| 7 | `response_size_bytes` | decimal integer, or empty if null |
| 8 | `schema_valid` | `true` / `false`, or empty if null |
| 9 | `quality_score` | decimal integer, or empty if null |
| 10 | `has_receipt` | `true` / `false` |
| 11 | `receipt_verified` | `true` / `false` |
| 12 | `signature_verified` | `true` / `false` |
| 13 | `weight` | decimal with exactly six fractional digits: `0.300000` |

The weight is rounded to six decimals before the rating is stored, so the stored value and the
committed one are the same number. No field may contain a newline.

The 0x00 prefix separates leaves from interior nodes (0x01), and the domain string separates this
record from anything else the project hashes.

### Golden vector

```
trustscore-leaf-v2
3f2b8c1e-5d4a-4b7e-9c2f-0a1b2c3d4e5f
api.example.com
2026-09-24T16:02:55.123456Z
200
143
2048
true
4
false
false
true
0.300000
```

`leaf = 1c6614309f5d94d31ad7a328181c9a3a38b632655fc7395b613c5a285b712a65`

## Tree v1 (legacy)

`node = SHA256(left || right)`. A level with an odd number of nodes pairs the last node with a
copy of itself. This allows two different leaf lists to share a root (the pattern of
CVE-2012-2459) and does not separate leaves from nodes, which is why v2 replaces it. Anchors built
this way keep `tree_version = 1` and remain verifiable.

## Tree v2

`node = SHA256(0x01 || left || right)`. Nodes are paired left to right; a level with an odd number
of nodes carries its last node up unchanged. The root is identical to the Merkle Tree Hash of
[RFC 6962 §2.1](https://www.rfc-editor.org/rfc/rfc6962#section-2.1) over the same leaf hashes.

Golden vector: for the three leaves `SHA256("leaf-0")`, `SHA256("leaf-1")`, `SHA256("leaf-2")`,
the root is `3fd64e951bb292c4cc9ea78ea50e1115c0b754f0ca8b4a4f4a9610bd2c258877`.

A v2 tree may contain both v1 and v2 leaves: every rating is in it, with the leaf version it was
stored with. The two cannot be confused, since a v1 preimage is text and never starts with 0x00
or 0x01.

## Which ratings an anchor covers

An anchor covers every rating with `created_at <= cutoff_at`, ordered by `(created_at, id)`. The
cutoff is set a few minutes before the anchor is computed, longer than any write transaction, so
the set is fixed by the time it is read.

The leaf of a v2 rating is the hash stored when the rating was inserted, not one recomputed at
anchoring time: a row edited afterwards keeps its original leaf in the tree, and its proof exposes
the edit instead of certifying it. The anchoring job also recomputes every v2 leaf and logs any
mismatch. v1 leaves are recomputed from `(id, service, created_at)`, as every v1 anchor did.

## Verifying a proof

`GET /v1/audit/proof/{rating_id}` (the `rating_id` is returned by `POST /v1/rate`):

```json
{
  "rating_id": "3f2b8c1e-5d4a-4b7e-9c2f-0a1b2c3d4e5f",
  "leaf_version": 2,
  "committed": {
    "id": "3f2b8c1e-5d4a-4b7e-9c2f-0a1b2c3d4e5f",
    "service": "api.example.com",
    "created_at": "2026-09-24T16:02:55.123456Z",
    "status_code": 200, "latency_ms": 143, "response_size_bytes": 2048,
    "schema_valid": true, "quality_score": 4,
    "has_receipt": false, "receipt_verified": false, "signature_verified": true,
    "weight": "0.300000"
  },
  "leaf_hash": "1c66...2a65",
  "tree_version": 2,
  "merkle_root": "…",
  "proof": [ { "hash": "…", "is_right": true }, … ],
  "leaf_index": 4121,
  "total_leaves": 11230
}
```

For a v1 leaf, `committed` holds only `id`, `service` and `created_at` (in the v1 spelling).

1. Check `rating_id` and `committed.id` are the rating you asked about.
2. Recompute the leaf from `committed` under `leaf_version`. It must equal `leaf_hash`; if it
   does not, the rating has been modified since it was written.
3. Starting from `leaf_hash`, for each proof node in order: if `is_right`, `current = node(current,
   hash)`, else `current = node(hash, current)`, with `node` from `tree_version`. The result must
   equal `merkle_root`.
4. `merkle_root` must equal the root published by `GET /v1/audit/root` (and, once on-chain
   anchoring is active, the root recorded on chain).

Proofs are always against the latest anchor. A rating newer than its cutoff is not provable yet
and returns `404`.

```
node tools/verify-proof/verify-proof.mjs <rating_id>
node tools/verify-proof/verify-proof.mjs --self-test
```

## Privacy

The agent DID and the free-text comment are not committed. Both can be personal data and must
remain erasable, and nothing in a published hash tree can be erased. Deleting an agent's data
removes the row; the leaf stays in the tree but no longer links to anyone.

## Anchoring cadence and on-chain publication

Roots are computed by the batch job every 6 hours and stored in `merkle_anchors` with their
`cutoff_at` and `tree_version`.

Publishing each root on a public chain (Base L2 is the candidate: a 32-byte hash per anchor, a few
cents a month) is planned but not active: `blockchain`, `transaction_hash` and `block_number` in
`/v1/audit/root` are `null` until it is. Until then, anyone who wants protection against history
being rewritten should record roots themselves over time.

Consistency proofs between two anchors (showing the later tree extends the earlier one) are not
implemented yet.
