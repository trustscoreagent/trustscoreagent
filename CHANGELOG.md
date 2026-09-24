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
- **Merkle v2.** New ratings get a leaf that commits to what they reported (metrics, quality
  score, receipt and signature verification, weight), not just to their id, service and time; new
  anchors use an RFC 6962-style tree with leaf/node domain separation and no odd-node duplication.
  Existing ratings keep their v1 leaf and stay provable. `GET /v1/audit/proof` now returns the
  committed fields and versions, `POST /v1/rate` returns the `rating_id` to ask for it, and
  `tools/verify-proof` verifies a proof independently (migration 013, `docs/MERKLE-SPEC.md`)
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
- Rotating `did:key` minted fresh rating quota: signed ratings now also spend a per-IP bucket
- A Redis outage rejected every signed rating as a replay; a valid signature is now accepted and
  counted as unsigned while the nonce store is down
- `/v1/agent/trust` returned a stale pre-split value for agents that never signed; it now reports
  both identities (`trust_score`, `unsigned_trust_score`) (migration 011)
- Probe quarantine is decided by how long a target has been failing (3 days), not by a pass count
  that changed meaning with the schedule (migration 012), and a `probe_target_health` error no
  longer aborts the probe pass or drops a measurement
- Three probe targets had no conformity check, so conformity always read valid: `date.nager.at`
  and `api.github.com` now check a JSON field (and no longer use a year-pinned URL or a
  plain-text endpoint), and arXiv, which answers Atom XML, uses the new `ExpectText` check
- MCP server 0.2.2: without a usable key it keeps a stable fallback DID in
  `~/.trustscoreagent/agent-id` instead of a new one per restart, and several instances starting
  at once no longer race to write different keys

### Known limitations
- Ratings written before Merkle v2 keep a leaf that commits to `(id, service_did, created_at)`
  only: for those, the audit log proves a rating existed, not what it claimed
- Roots are not yet published on chain, so the log protects against edits after a root was
  observed, not against the operator rewriting history before anyone recorded a root
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
