# Changelog

All notable changes to TrustScoreAgent will be documented in this file.

## [0.1.0] — 2026-04-04

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
- Rate limiter fails closed when Redis is unavailable
- Input validation: length limits, range checks on all fields
- Request body size limited to 1MB
- Swagger disabled in production
- Global rate limiting: 120 requests/minute per IP
