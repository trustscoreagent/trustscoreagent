# TrustScoreAgent — API Reference

Base URL (production): `https://api.trustscoreagent.com`

No account, no API key. All core endpoints are free. Responses are JSON with
`snake_case` fields.

> **Status: Phase 1 (early).** The API is live and stable in shape, but the dataset is
> still small and some features (blockchain anchoring, x402 payments, mandatory agent
> signatures) are Phase 2. See the [project README](https://github.com/trustscoreagent/trustscoreagent#readme).

## Service identifiers

Every endpoint that takes a service accepts **any** of these forms — they are
normalized internally to the same canonical id:

- `api.example.com` (domain → provider level)
- `api.example.com/v1/translate` (domain + path → endpoint level)
- `https://api.example.com/v1/translate` (URL)
- `did:web:api.example.com` (DID)

Query parameters and fragments are always stripped.

---

## GET /v1/score

Trust score for a service. Unknown services return a neutral score of `0.5` with
`known: false` (never a 404).

```
GET /v1/score?service=api.example.com
```

| Query param | Required | Description |
|-------------|----------|-------------|
| `service`   | yes\*    | Service identifier (any format above). |
| `did`       | —        | Legacy alias for `service` (kept for backwards compatibility). |

\* Provide `service` (preferred) or `did`.

**Response 200**

```json
{
  "service": "api.example.com",
  "level": "provider",
  "known": true,
  "score": 0.87,
  "confidence": 0.94,
  "ratings_count": 2341,
  "dimensions": { "availability": 0.99, "latency": 0.82, "conformity": 0.91 },
  "recent_incidents": 0,
  "last_rated": "2026-03-29T14:23:01Z",
  "service_supports_receipts": true
}
```

`level` is `provider` (domain only) or `endpoint` (with path). Unknown service:

```json
{ "service": "never-seen.com", "known": false, "score": 0.5, "ratings_count": 0 }
```

---

## POST /v1/rate

Submit a rating after calling a service.

```
POST /v1/rate
Content-Type: application/json
X-Agent-DID: did:key:z6Mk...                   # required
X-Agent-Signature: <base64url Ed25519>         # optional, doubles the rating's weight
X-Agent-Timestamp: 2026-08-29T12:00:00Z        # required if signing
X-Agent-Nonce: <random, 8-100 chars>           # required if signing
```

Signing is optional but strongly recommended: see
[Signing a rating](#signing-a-rating) below. An unsigned rating is accepted at **half
weight**, because its `X-Agent-DID` is merely asserted. A signature that is present but
does not verify is rejected with `401`.

```json
{
  "service": "api.example.com",
  "metrics": {
    "status_code": 200,
    "latency_ms": 143,
    "response_size_bytes": 2048,
    "schema_valid": true
  },
  "quality_score": 4,
  "comment": "fast and accurate",
  "receipt": "eyJhbGciOiJFZERTQS[...]"
}
```

| Field | Required | Notes |
|-------|----------|-------|
| `service` (or `service_did`) | yes | Service identifier. |
| `metrics.status_code` | yes | 100–599. |
| `metrics.latency_ms` | yes | 1–600000. |
| `metrics.response_size_bytes` | no | |
| `metrics.schema_valid` | no | Did the response match the expected format. |
| `quality_score` | no | 1–5 subjective rating (capped at 25% of the score). |
| `comment` | no | ≤ 500 chars. |
| `receipt` | no | JWT from the service's `X-Trust-Receipt` header. See [receipts](./receipts.md). |

**Response 200**

```json
{
  "accepted": true,
  "rating_id": "3f2b8c1e-5d4a-4b7e-9c2f-0a1b2c3d4e5f",
  "rating_weight": "verified",
  "agent_identity": "signed",
  "new_score": 0.87
}
```

`rating_weight` is `verified` (valid receipt) or `unverified` (no/invalid receipt).
`agent_identity` is `signed` or `unsigned`. Keep `rating_id`: it is how you later fetch the
[audit proof](#audit) that your rating is in the log as you submitted it.

The two are independent: a receipt says the *service* saw the call, a signature says we know
*who is reporting it*. Weights combine as:

| | signed | unsigned |
|---|---|---|
| **receipt verified** | 1.0 | 0.5 |
| **no receipt** | 0.3 | 0.15 |

The result is then scaled by the rater's agent trust score (EigenTrust).

`agent_identity_note` appears only when a valid signature could not be counted as signed
because replay protection was temporarily unavailable.

**Errors:** `400` (validation, including `nonce_replay` for a reused receipt or while the
receipt nonce store is unreachable), `401` (`invalid_agent_signature`), `429` (rate limit:
unsigned ratings, 10 per IP per service per hour; signed ratings, 10 per agent per service
per hour and 100 per IP per service per hour). Every endpoint except `/health` is also limited
to 120 requests per minute per IP.

### Signing a rating

Generate an Ed25519 keypair and publish nothing: your DID *is* your public key, encoded as a
`did:key` (base58btc of the `0xED01` multicodec prefix followed by the 32-byte key).

Sign these eight fields, joined by `\n`:

```
trustscore-v1
api.trustscoreagent.com
POST
/v1/rate
did:key:z6Mk...
2026-08-29T12:00:00.000Z
<nonce>
<sha256(request body), lowercase hex>
```

The second field is the **audience**: the host of the registry you are addressing,
lowercased (`new URL(apiBaseUrl).host` in JavaScript).

Send the signature base64url-encoded in `X-Agent-Signature`, with the same timestamp and
nonce you signed.

Details that matter in practice:

- **Hash the exact bytes you send.** Serialise the body once and reuse that string. Signing
  a re-serialised copy is the most common way this fails, and it surfaces only as a `401`.
- **Address the audience to the registry you are actually calling.** This registry is
  self-hostable, so signing without it would let the operator of one instance relay the
  signatures its users produce to another instance and forge ratings in their name. If you
  self-host, set `AgentSignature:Audience` (env `AgentSignature__Audience`) to your canonical
  host; leaving it unset falls back to the request's `Host` header, which an attacker
  relaying a signature controls.
- The signature is bound to method, path and body, so it authorises that one request.
- The nonce is single-use (10 minute window) and scoped to your DID.
- The timestamp must be within 5 minutes, and not more than 1 minute in the future.
- `trustscore-v1` is domain separation: it stops a signature made for another purpose from
  verifying here, and a future `v2` can change the scheme without a flag day.

The [MCP server](https://www.npmjs.com/package/@trustscoreagent/mcp-server) does all of this
for you and stores its key in `~/.trustscoreagent/agent-key.pem`.

---

## GET /v1/services

List rated services.

```
GET /v1/services?sort_by=score&order=desc&min_score=0.7&min_ratings=10&limit=20&offset=0
```

| Query param | Default | Values |
|-------------|---------|--------|
| `sort_by`   | `score` | `score`, `ratings_count`, `last_rated` |
| `order`     | `desc`  | `asc`, `desc` |
| `min_score` | `0`     | 0.0–1.0 |
| `min_ratings` | `0`   | integer |
| `limit`     | `20`    | 1–100 |
| `offset`    | `0`     | integer |

---

## GET /v1/agent/trust

Look up an agent's EigenTrust score by DID. This endpoint is read-only and unauthenticated, so
any DID can be queried; trust scores are public by design.

The score returned for a DID reflects only ratings that were **signed** by it. Unsigned
ratings naming that DID accumulate separately (under `unsigned:<did>`) and cannot move it,
so nobody can raise or damage your standing by rating in your name.

```
GET /v1/agent/trust?did=did:key:z6Mk...
```

**Response 200**

```json
{
  "agent": "did:web:my-agent.example.com",
  "trust_score": 0.78,
  "interpretation": "MODERATE",
  "unsigned_trust_score": 0.34,
  "unsigned_interpretation": "LOW"
}
```

`trust_score` is the signed identity; `unsigned_trust_score` is what the unsigned ratings naming
this DID have earned, reported apart so an agent that has not started signing still sees a live
value. Passing `unsigned:<did>` as `did` returns the same response. New agents start at `0.5`
(neutral). The score is recomputed every 6 hours by EigenTrust
based on how consistent the agent's ratings are with the consensus.

---

## Audit

```
GET /v1/audit/root
```
Returns the latest anchored Merkle root (and, in Phase 2, its on-chain reference).

```
GET /v1/audit/proof/{rating_id}
```
Returns a Merkle inclusion proof for a rating (the `rating_id` from `POST /v1/rate`), with the
fields its leaf commits to (`committed`), the leaf and tree versions, and the path to the anchored
root. Returns `404` if the rating is not yet included in an anchor. Leaf and tree formats and the
verification steps: [MERKLE-SPEC.md](./MERKLE-SPEC.md); an independent verifier:
`node tools/verify-proof/verify-proof.mjs <rating_id>`.

```
GET /v1/audit/anchors?limit=20&before=<id>
```
Anchored roots, newest first, each with its `leaf_count` and `tree_version`. `next_before` pages
further back. Record them: they are what consistency proofs are checked against.

```
GET /v1/audit/consistency?from=<older id>&to=<newer id>
```
RFC 6962 consistency proof that anchor `from` is a prefix of anchor `to` (both v2): the log only
grew between them. `409 not_consistent` if it did not, `422` for a v1 anchor or `from` larger than
`to`, `503` if the later anchor cannot be reproduced right now. `node tools/verify-proof/verify-proof.mjs --history` checks the recent chain.

---

## Premium endpoints

Free during Phase 1; metered via [x402](https://x402.org/) micropayments in Phase 2.

| Endpoint | Description |
|----------|-------------|
| `GET /v1/score/history?service=` | Daily aggregated score history. |
| `GET /v1/score/detailed?service=` | Latency percentiles, quality distribution. |
| `POST /v1/scores/bulk` | Up to 100 service scores in one request (`{ "dids": [...] }`). |

---

## GET /health

Liveness/readiness probe (not for public use). Returns `200` when healthy or degraded (Redis down
but the DB is up) and `503` only when the database is unavailable, with per-dependency checks.

---

## OpenAPI

A machine-readable OpenAPI document is served in every environment, including production,
at `/swagger/v1/swagger.json`:

```
https://api.trustscoreagent.com/swagger/v1/swagger.json
```

The interactive Swagger **UI** (`/swagger`) is available in local/dev runs only, to keep
the production surface minimal.
