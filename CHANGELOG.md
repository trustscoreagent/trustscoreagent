# Changelog

All notable changes to TrustScoreAgent will be documented in this file. The API, the agent card
and the MCP server share one version number per release.

## [Unreleased]

### Added
- `GET /v1/stats?days=7`: how much the registry is used. Ratings split between the registry's own
  probe and other agents (distinct agents, signed, receipt-backed), and API calls per endpoint and
  client family, from daily aggregate counters (no IP, agent DID or request content)
- Merkle roots are timestamped in Bitcoin with OpenTimestamps: the batch job submits each v2
  anchor's root to public calendars and later stores the completed proof, served as a standard
  `.ots` file at `GET /v1/audit/anchors/{id}/ots`; anchors carry an `opentimestamps` status, and
  `tools/verify-proof --ots <id>` checks a proof against the Bitcoin block header
- The MCP server sends `User-Agent: trustscoreagent-mcp/<version>`, and the LangChain and CrewAI
  tools `trustscoreagent-langchain/1.0.0` and `trustscoreagent-crewai/1.0.0`, so their use can be
  counted

## [0.2.4] - 2026-09-27

### Added
- MCP tools declare an `outputSchema` and return their data as `structuredContent` alongside the
  text (score, trust level, dimensions; the `rating_id` of a submitted rating), so clients read
  numbers instead of parsing prose
- MCP tool annotations: `check_reputation` and `list_services` are read-only, `submit_rating`
  writes but is not destructive
- MCP server info carries a title, website and icon; the `.mcpb` bundle has an icon

### Changed
- `submit_rating` output includes the `rating_id` to fetch the rating's audit proof, and the
  unknown-service message no longer asks the agent to help others
- Real Redis tests, no silent test skips in CI, the MCP server and proof verifier built and
  self-tested in CI, CodeQL enabled and its first findings (log forging) fixed

## [0.2.3] - 2026-09-27

### Fixed
- `GET /v1/services?sort_by=score` sorted, and `min_score` filtered, by the aggregate
  `alpha/(alpha+beta)` instead of the score it displays, so the list came out of order
- `recent_incidents` is `null` instead of a hard-coded `0`: incidents are not tracked yet, and the
  MCP server and Python integrations no longer print "No recent incidents" on no evidence
- The rate limiter no longer logs the client IP (inside its bucket key) when Redis is down
- `/health` reports the release version and commit instead of `0.1.0.0`, and the agent card its
  real version
- `trustscoreagent.com/llms.txt` and `/.well-known/agent.json` served the landing's HTML; the
  landing deploy now publishes both from `public/`

### Changed
- Services created by tests against the live API are kept (their ratings are anchored) but no
  longer listed (`services.listed`, migration 014)
- Application logs carry a Cloud Logging `severity`, so errors (including `Merkle integrity` and
  `Merkle consistency`) reach alerts; alerts now fire on any API or batch-job error and when the
  batch job has not succeeded for 7 hours
- Database connections per instance are capped (3, `Database:MaxPoolSize`) and instances limited
  (prod 4, staging 2) to stay within the database's connection limit
- Production and staging run as dedicated, least-privilege service accounts instead of the
  default account with project-wide Editor; staging has its own database user, admin key and
  signature audience; deployments are accepted from `main` only
- A test fails CI when the API, the agent card and the MCP listings disagree on the version

## [0.2.2] - 2026-09-26

### Added
- **Agent signatures (`X-Agent-Signature`).** An agent identified by a `did:key` signs each
  rating with its Ed25519 key. The signature covers the method, path, a SHA-256 of the body, a
  timestamp, a single-use nonce, and the registry being addressed, so it authorises one request
  at one registry and cannot be replayed or relayed. Unsigned ratings are still accepted at half
  weight, so existing clients keep working; a signature that is present but invalid is rejected
  with `401` rather than downgraded. The MCP server (npm 0.2.0) generates and stores the key.
- **Merkle v2.** New ratings get a leaf that commits to what they reported (metrics, quality
  score, receipt and signature verification, weight), not just to their id, service and time; new
  anchors use an RFC 6962-style tree with leaf/node domain separation and no odd-node duplication.
  Existing ratings keep their v1 leaf and stay provable. `GET /v1/audit/proof` returns the
  committed fields and versions, `POST /v1/rate` returns the `rating_id` to ask for it, and
  `tools/verify-proof` verifies a proof independently (migration 013, `docs/MERKLE-SPEC.md`)
- **Consistency proofs.** `GET /v1/audit/consistency?from=&to=` proves a later v2 anchor extends
  an earlier one (RFC 6962 / RFC 9162), `GET /v1/audit/anchors` lists the root history, the
  anchoring job logs a `Merkle consistency` error if a new anchor does not extend the previous one,
  and `tools/verify-proof --history` checks the chain. Erasure clears the agent DID and comment
  instead of deleting a rating, which would break the chain
- Probe target quarantine: a target that fails for three days is treated as a bad URL rather than
  as evidence about the service, and stops producing ratings until it answers again (migrations
  010 and 012); `ExpectText` conformity check for non-JSON targets
- `tools/ProbeCandidates`: verifies a probe target against the prober's own HTTP configuration
  and `ValidateBody` before it is allowed into the config
- LangChain and CrewAI tools (`integrations/`)
- MCP server as an `.mcpb` bundle, attached to each GitHub release; a Dockerfile for registries
  such as Glama; the official MCP Registry listing is published from CI
- Staging has its own database and Redis key prefix (`Redis:KeyPrefix`)
- Migrations 009 to 013

### Changed
- Seed prober widened from 21 to 49 targets, run concurrently (bounded), every 6 hours
- The machine-readable surfaces (MCP tool descriptions, `llms.txt`, the A2A agent card, OpenAPI
  descriptions) now explain *why* checking and reporting are rational for an agent, instead of
  only describing mechanics. `submit_rating` previously asked for altruism ("this helps other
  agents"), which gave a model optimising its user's task no reason to call it.
- Unsigned ratings accrue reputation under a namespaced identity, so ratings filed under a DID
  the sender never proved can neither damage nor borrow that agent's standing
- The per-service rating quota is keyed on a proven identity; unsigned callers are bucketed by
  IP, so an asserted DID cannot exhaust another agent's quota
- Infrastructure: Redis moved to Upstash (the VPC connector and Memorystore are gone), Cloud Run
  scales to zero, and the landing deploys to Cloudflare Pages from GitHub Actions

### Fixed
- A correctly signed request whose URL differed only in case was rejected with `401`, because the
  raw request path went into the signed payload and routing is case-insensitive
- Rotating `did:key` minted fresh rating quota: signed ratings now also spend a per-IP bucket
- A Redis outage rejected every signed rating as a replay; a valid signature is now accepted and
  counted as unsigned while the nonce store is down
- `/v1/agent/trust` returned a stale pre-split value for agents that never signed; it now reports
  both identities (`trust_score`, `unsigned_trust_score`) (migration 011)
- A `probe_target_health` error no longer aborts the probe pass or drops a measurement
- Three probe targets had no conformity check, so conformity always read valid
- MCP server 0.2.2: without a usable key it keeps a stable fallback DID in
  `~/.trustscoreagent/agent-id` instead of a new one per restart, and several instances starting
  at once no longer race to write different keys

### Known limitations
- Ratings written before Merkle v2 keep a leaf that commits to `(id, service_did, created_at)`
  only: for those, the audit log proves a rating existed, not what it claimed
- Roots are not yet published on chain: consistency proofs show each root extends the ones before
  it, but a root nobody recorded could have been replaced before anyone looked
- Signing is not mandatory, and key possession proves identity rather than uniqueness, so it
  stops impersonation but is not Sybil resistance on its own

## [0.1.1] - 2026-07-11

### Added
- Seed prober: measurements of real public APIs under a transparent probe agent
- Migrations 006 to 008 (removed fictitious seeds, performance indexes, anchor cutoff)

### Changed
- Rate limiter now **fails open** when Redis is unavailable (per the "never fail if Redis is
  down" convention), falling back to a bounded per-instance limiter; the receipt nonce
  anti-replay stays fail-closed
- Merkle anchoring is reproducible under concurrent writes (cutoff-based snapshot)
- Receipt verification accepts standard DID key encodings (multibase multicodec, base58, JWK)

### Security
- Receipts are bound to the submitting agent; SSRF guard also covers the seed prober and NAT64
- EigenTrust matrix is capped to bound the batch job's memory

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
- Redis-based rate limiter
- Admin key compared in constant time
- Input validation: length limits, range checks on all fields
- Request body size limited to 1MB
- Swagger disabled in production
- Global rate limiting: 120 requests/minute per IP

[Unreleased]: https://github.com/trustscoreagent/trustscoreagent/compare/v0.2.4...HEAD
[0.2.4]: https://github.com/trustscoreagent/trustscoreagent/compare/v0.2.3...v0.2.4
[0.2.3]: https://github.com/trustscoreagent/trustscoreagent/compare/v0.2.2...v0.2.3
[0.2.2]: https://github.com/trustscoreagent/trustscoreagent/compare/v0.1.1...v0.2.2
[0.1.1]: https://github.com/trustscoreagent/trustscoreagent/releases/tag/v0.1.1
[0.1.0]: https://github.com/trustscoreagent/trustscoreagent/commits/v0.1.1
