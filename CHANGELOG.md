# Changelog

All notable changes to TrustScoreAgent will be documented in this file.

## [Unreleased]

### Added
- **Agent signatures (`X-Agent-Signature`).** An agent identified by a `did:key` signs each
  rating with its Ed25519 key. The signature covers the method, path, a SHA-256 of the body, a
  timestamp, a single-use nonce, and the registry being addressed, so it authorises one request
  at one registry and cannot be replayed or relayed. Unsigned ratings are still accepted at half
  weight, so existing clients keep working; a signature that is present but invalid is rejected
  with `401` rather than downgraded. The MCP server (npm 0.2.0) generates and stores the key.
- Probe target quarantine: a target that fails every pass for three days is treated as a bad URL
  rather than as evidence about the service, and stops producing ratings until it answers again
  (migration 010)
- `tools/ProbeCandidates`: verifies a probe target against the prober's own HTTP configuration
  and `ValidateBody` before it is allowed into the config
- Seed prober: measurements of real public APIs under a transparent probe agent, widened from 21
  to 49 targets
- Migrations 006 to 010

### Changed
- The machine-readable surfaces (MCP tool descriptions, `llms.txt`, the A2A agent card, OpenAPI
  descriptions) now explain *why* checking and reporting are rational for an agent, instead of
  only describing mechanics. `submit_rating` previously asked for altruism ("this helps other
  agents"), which gave a model optimising its user's task no reason to call it.
- Unsigned ratings accrue reputation under a namespaced identity, so ratings filed under a DID
  the sender never proved can neither damage nor borrow that agent's standing
- The per-service rating quota is keyed on a proven identity; unsigned callers are bucketed by
  IP, so an asserted DID cannot exhaust another agent's quota
- Seed probe runs its targets concurrently (bounded)
- Rate limiter now **fails open** when Redis is unavailable (per the "never fail if Redis is
  down" convention), falling back to a bounded per-instance limiter; the receipt nonce
  anti-replay stays fail-closed
- Merkle anchoring is reproducible under concurrent writes (cutoff-based snapshot)
- Receipt verification accepts standard DID key encodings (multibase multicodec, base58, JWK)

### Fixed
- A correctly signed request whose URL differed only in case was rejected with `401`, because the
  raw request path went into the signed payload and routing is case-insensitive

### Known limitations
- The Merkle leaf commits to `(id, service_did, created_at)` only, so the audit log proves a
  rating existed, not what it claimed. Neither `receipt_verified` nor `signature_verified` is in
  the commitment.
- Signing is not mandatory, and key possession proves identity rather than uniqueness, so it
  stops impersonation but is not Sybil resistance on its own

### Security
- Receipts are bound to the submitting agent; SSRF guard also covers the seed prober and NAT64
- EigenTrust matrix is capped to bound the hourly job's memory

## [0.1.0] - 2026-04-04

### Added
- Core API endpoints: GET /v1/score, POST /v1/rate, GET /v1/services
- Beta Reputation System with per-dimension scoring (availability, latency, conformity)
- EigenTrust anti-Sybil agent trust scoring
- Receipt verification (Ed25519 JWT) with DID resolution
- Merkle tree audit log with inclusion proofs (GET /v1/audit/root, /v1/audit/proof/{id})
- Premium endpoints: score history, detailed breakdown, bulk scores
- Agent trust endpoint: GET /v1/agent/trust
- MCP server with 3 tools (check_reputation, submit_rating, list_services)
- Flexible service identification: accepts URLs, domains, and DIDs
- Unknown services return neutral score (0.5) instead of 404
- Redis-based rate limiting (per-agent and global per-IP)
- CI/CD: GitHub Actions (build, test, lint, security), staging auto-deploy, production canary
- GCP infrastructure: Cloud Run, Cloud SQL, Redis, Artifact Registry

### Security
- Admin endpoints require API key authentication
- SSRF protection in DID resolver (blocks private IPs)
- Rate limiter (fail-open on Redis outage as of Unreleased; see above)
- Admin key compared in constant time
- Input validation: length limits, range checks on all fields
- Request body size limited to 1MB
- Swagger disabled in production
- Global rate limiting: 120 requests/minute per IP
