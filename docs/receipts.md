# TrustScoreAgent — Receipt Standard

A **receipt** is a JWT signed by a *service*, proving that an agent actually called it.
Receipts are **optional** but they raise a rating's weight from `0.3` (unverified) to
`1.0` (verified) — a verified rating has roughly 3× the influence on a score.

This page documents the format and verification rules as implemented today (Phase 1).

## Why receipts exist

Anyone can POST a rating. Without proof of a real interaction, scores would be trivial
to manipulate. A receipt — signed by the service the rating is about — cryptographically
ties the rating to a genuine call. Services that emit receipts get more accurate,
higher-trust scores, which is a direct, measurable advantage when agents compare
services.

## Format

The receipt is a JWS/JWT (`header.payload.signature`) signed with the service's
**Ed25519** key. The payload:

```json
{
  "version": "1.0",
  "service_did": "did:web:api.example.com",
  "agent_did": "did:web:agent.example.com",
  "timestamp": "2026-03-29T14:23:01Z",
  "endpoint": "/v1/analyze",
  "method": "POST",
  "status_code": 200,
  "response_hash": "sha256:a1b2c3d4...",
  "latency_ms": 143,
  "nonce": "f47ac10b-58cc-4372-a567-0e02b2c3d479"
}
```

| Field | Required | Description |
|-------|----------|-------------|
| `version` | yes | Format version (`"1.0"`). |
| `service_did` | yes | DID of the service issuing the receipt. |
| `agent_did` | yes | DID of the calling agent. |
| `timestamp` | yes | ISO-8601 time of the response. Must be < 5 minutes old. |
| `endpoint` | yes | Path called. |
| `method` | yes | HTTP method. |
| `status_code` | yes | HTTP status returned. |
| `response_hash` | recommended | SHA-256 of the response body. |
| `latency_ms` | recommended | Processing time measured by the service. |
| `nonce` | yes | UUID v4. Prevents replay. |

### Transmission

The service returns the JWT in a response header:

```
HTTP/1.1 200 OK
X-Trust-Receipt: eyJhbGciOiJFZERTQSIsInR5cCI6IkpXVCJ9.eyJ2ZXJzaW9uIj...

{ "result": "..." }
```

The agent then forwards that token in the `receipt` field of `POST /v1/rate`.

## How a service issues receipts

1. Generate an Ed25519 key pair.
2. Publish a DID document at `https://<your-domain>/.well-known/did.json` exposing the
   public key (see below).
3. For each response, build the payload above and sign it as an EdDSA JWT.
4. Return it in the `X-Trust-Receipt` header.

### DID document

`did:web:api.example.com` resolves to `https://api.example.com/.well-known/did.json`:

```json
{
  "@context": "https://www.w3.org/ns/did/v1",
  "id": "did:web:api.example.com",
  "verificationMethod": [{
    "id": "did:web:api.example.com#key-1",
    "type": "Ed25519VerificationKey2020",
    "controller": "did:web:api.example.com",
    "publicKeyMultibase": "z6Mk..."
  }],
  "assertionMethod": ["did:web:api.example.com#key-1"]
}
```

`Ed25519VerificationKey2020` (`publicKeyMultibase`), `Ed25519VerificationKey2018`, and
`JsonWebKey2020` with a `publicKeyBase64` are all accepted.

## Verification rules

When a rating arrives with a receipt, TrustScoreAgent:

1. Parses the JWT and decodes the payload.
2. Checks `service_did` matches the rated service.
3. Checks the timestamp is < 5 minutes old.
4. Atomically claims the `nonce` (anti-replay, 10-minute window).
5. Resolves the service DID to its public key (cached 1h).
6. Verifies the Ed25519 signature. **The algorithm is forced to Ed25519** — the JWT
   `alg` header is ignored, so `alg:none` and algorithm-confusion attacks do not apply.

### Outcome table

| Situation | Weight |
|-----------|--------|
| Valid signature, nonce fresh | **1.0** (verified) |
| Invalid signature | **0.3** (unverified) |
| Timestamp expired (> 5 min) | **0.3** (unverified) |
| DID resolution failed | **0.3** (unverified) |
| No receipt at all | **0.3** (unverified) |
| Nonce already used | **rejected** (`400 nonce_replay`) |

The system is **tolerant for early adopters, strict on replay**: a malformed or
unverifiable receipt is downgraded, not punished; only a replayed nonce is rejected.

> SSRF note: DID resolution only follows `https`, refuses redirects, and validates every
> resolved IP against private/loopback/link-local/cloud-metadata ranges before
> connecting.

## Audit trail

Every accepted rating is hashed into an append-only **Merkle tree**. The root is anchored
periodically (hourly), and `GET /v1/audit/proof/{rating_id}` returns an inclusion proof
that verifies against the anchored root from `GET /v1/audit/root`. On-chain anchoring to
Base L2 is Phase 2.

To verify a proof: start from `leaf_hash`; for each proof node, if `is_right` then
`hash(current || node.hash)`, else `hash(node.hash || current)`. The result must equal
`merkle_root`.
