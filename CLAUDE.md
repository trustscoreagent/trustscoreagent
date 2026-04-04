# CLAUDE.md — Instructions for Claude Code

## Project
TrustScoreAgent — Free, open reputation registry for AI microservices.

## Stack
- C# / .NET 8, ASP.NET Core Minimal API
- PostgreSQL (via Dapper + Npgsql)
- Redis (via StackExchange.Redis)
- xUnit + FluentAssertions for tests
- MCP server in TypeScript

## Commands
- Build: `dotnet build`
- Test: `dotnet test`
- Run locally: `docker compose up -d && dotnet run --project src/TrustScore.Api`
- Format: `dotnet format`
- Build MCP: `cd mcp-server && npm run build`

## Architecture
- `src/TrustScore.Core/` — Models, interfaces, MerkleTree (no dependencies)
- `src/TrustScore.Api/` — HTTP endpoints, data access, scoring, receipts
- `src/TrustScore.Tests/` — Unit and integration tests (75+)
- `migrations/` — SQL migration files (run by DbUp on startup)
- `mcp-server/` — TypeScript MCP server (3 tools)

## API Endpoints
- `GET /v1/score?service=` — Score of a service (returns 0.5 for unknown)
- `POST /v1/rate` — Submit a rating
- `GET /v1/services` — List rated services
- `GET /v1/audit/root` — Merkle tree root
- `GET /v1/audit/proof/{id}` — Inclusion proof
- `GET /v1/score/history?service=` — Score history (premium, free for now)
- `GET /v1/score/detailed?service=` — Detailed breakdown (premium, free for now)
- `POST /v1/scores/bulk` — Bulk scores (premium, free for now)
- `GET /v1/agent/trust?did=` — Agent trust score
- `POST /v1/admin/eigentrust` — Trigger EigenTrust recalculation

## Conventions
- Minimal API style (no controllers)
- Dapper with raw SQL (no EF Core)
- Redis as cache with PostgreSQL fallback (never fail if Redis is down)
- All endpoints return JSON with snake_case property names
- Services identified by ?service= (URL, domain, or DID — all normalized to domain)
- ServiceIdentifier.Normalize() canonicalizes all formats to lowercase domain
- Unknown services return neutral score 0.5 with known=false (no 404)
- Tests use xUnit [Fact] and FluentAssertions .Should()
- Feature branches merged with --no-ff, conventional commits
