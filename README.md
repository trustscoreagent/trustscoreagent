# TrustScoreAgent

Free, open reputation registry for AI microservices. Agents check trust scores before calling any service.

> **Status: Phase 1 (early).** The API, scoring (Beta + EigenTrust), receipt verification,
> Merkle audit trail and MCP server are implemented and tested — but the public dataset is
> still small, some services (`*.example.com`) are demo seed data, and parts of the design
> (on-chain anchoring, x402 payments, mandatory agent signatures) are Phase 2. We publish
> early and openly on purpose: the trust layer for the agentic economy should exist, be
> auditable, and be adoptable *before* it becomes critical. See the trust model in
> [SECURITY.md](SECURITY.md).

## What is this?

AI agents increasingly rely on paid microservices. TrustScoreAgent lets any agent:
- **Check** the reputation of a service before calling it
- **Rate** a service after calling it
- **Discover** which services are reliable

No account needed. No API key. Identify services by URL, domain, or DID.

## Quick start

```bash
# Check a service's trust score (any format works)
curl "https://api.trustscoreagent.com/v1/score?service=api.example.com"
curl "https://api.trustscoreagent.com/v1/score?service=https://api.example.com/v1/translate"

# Unknown services return a neutral score (0.5) — no errors
curl "https://api.trustscoreagent.com/v1/score?service=never-seen-before.com"

# Rate a service after calling it
curl -X POST "https://api.trustscoreagent.com/v1/rate" \
  -H "Content-Type: application/json" \
  -H "X-Agent-DID: my-agent.example.com" \
  -d '{
    "service": "api.example.com",
    "metrics": {
      "status_code": 200,
      "latency_ms": 143,
      "schema_valid": true
    }
  }'

# List top-rated services
curl "https://api.trustscoreagent.com/v1/services?sort_by=score&min_ratings=10"
```

## Local development

```bash
# Start PostgreSQL and Redis
docker compose up -d

# Run the API
dotnet run --project src/TrustScore.Api

# Run tests
dotnet test

# Swagger UI
open http://localhost:5000/swagger
```

## Architecture

- **C# / .NET 8** — ASP.NET Core Minimal API
- **PostgreSQL** — Ratings and service scores
- **Redis** — Score caching, rate limiting, nonce tracking
- **Beta Reputation System** — Bayesian scoring (per-dimension: availability, latency, conformity)
- **EigenTrust** — Anti-Sybil agent trust scoring
- **Merkle Tree** — Cryptographic audit log with inclusion proofs
- **Ed25519 Receipt Verification** — Cryptographic proof of service interaction
- **MCP Server** — Integration with Claude, Cursor, and MCP-compatible agents

## API Reference

### Core (free, always)

| Endpoint | Description |
|----------|-------------|
| `GET /v1/score?service=` | Trust score for a service (0.5 neutral for unknown) |
| `POST /v1/rate` | Submit a rating after calling a service |
| `GET /v1/services` | List rated services (pagination, sorting, filtering) |
| `GET /v1/agent/trust?did=` | Check your agent's trust score |
| `GET /v1/audit/root` | Latest Merkle tree root |
| `GET /v1/audit/proof/{id}` | Cryptographic inclusion proof for a rating |

### Premium (free for now, x402 micropayments later)

| Endpoint | Description |
|----------|-------------|
| `GET /v1/score/history?service=` | Daily aggregated score history |
| `GET /v1/score/detailed?service=` | Latency percentiles, quality distribution |
| `POST /v1/scores/bulk` | Up to 100 scores in one request |

### Service identification

All endpoints accept services in any format — they are normalized internally:
- `api.example.com` (domain)
- `https://api.example.com/v1/translate` (URL)
- `did:web:api.example.com` (DID)

All three resolve to the same service.

## MCP Server

TrustScoreAgent is available as an MCP server for Claude, Cursor, and other agents.

```bash
# Add to Claude Code
claude mcp add trustscoreagent npx -y @trustscoreagent/mcp-server
```

See [docs/mcp.md](docs/mcp.md) for full setup instructions.

## Documentation

- [API Reference](docs/api.md)
- [Receipt Standard](docs/receipts.md)
- [MCP Server Setup](docs/mcp.md)
- [Why Trust Matters for Agents](docs/why.md)

## Project status & trust model

TrustScoreAgent is **Phase 1 (early)**. What that means in practice:

- **Baseline data is real and auditable.** Initial scores come from a transparent operated
  probe (`did:web:trustscoreagent.com:probe`) that measures the availability, latency and
  conformity of a curated list of public, free APIs — real, Merkle-audited measurements, not
  fabricated numbers. Community and receipt-verified ratings accumulate on top. (The earlier
  fictitious `*.example.com` seeds have been removed.)
- **Single operator.** Neutrality currently rests on open-source scoring code and a
  verifiable Merkle audit trail, not on decentralization. Federation is a later phase.
- **Agent identity is self-asserted.** Sybil resistance in Phase 1 is rate limiting +
  hourly EigenTrust; verified service **receipts** are the trustworthy signal. Mandatory
  agent signatures and on-chain Merkle anchoring are Phase 2.

See [SECURITY.md](SECURITY.md) for the full trust model and how to report vulnerabilities.

## License

Apache-2.0. See [LICENSE](LICENSE).
