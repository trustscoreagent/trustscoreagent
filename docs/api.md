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
X-Agent-DID: did:web:my-agent.example.com      # required
```

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
{ "accepted": true, "rating_weight": "verified", "new_score": 0.87 }
```

`rating_weight` is `verified` (valid receipt) or `unverified` (no/invalid receipt).
A verified rating carries full weight (1.0); an unverified one carries 0.3. The base
weight is then scaled by the rater's agent trust score.

**Errors:** `400` (validation, including `nonce_replay` for a reused receipt),
`429` (rate limit: max 10 ratings per agent per service per hour).

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

An agent can look up **its own** trust score (you cannot query other agents').

```
GET /v1/agent/trust?did=did:web:my-agent.example.com
```

**Response 200**

```json
{ "agent": "did:web:my-agent.example.com", "trust_score": 0.78, "interpretation": "MODERATE" }
```

New agents start at `0.5` (neutral). The score is recomputed hourly by EigenTrust
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
Returns a Merkle inclusion proof for a rating, verifiable against the anchored root.
Returns `404` if the rating is not yet included in an anchor. See [receipts &
audit](./receipts.md#audit-trail).

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

Liveness/readiness probe (not for public use). Returns `200` healthy or `503` degraded
with per-dependency checks.

---

## OpenAPI

When not running in production, an interactive OpenAPI/Swagger UI is available at
`/swagger`. A machine-readable OpenAPI document is served at `/swagger/v1/swagger.json`.
