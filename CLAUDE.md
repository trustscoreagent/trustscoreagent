# CLAUDE.md — Instructions for Claude Code

## Project
TrustScoreAgent — Free, open reputation registry for AI microservices.

## Stack
- C# / .NET 8, ASP.NET Core Minimal API
- PostgreSQL (via Dapper + Npgsql)
- Redis (via StackExchange.Redis)
- xUnit + FluentAssertions for tests

## Commands
- Build: `dotnet build`
- Test: `dotnet test`
- Run locally: `docker compose up -d && dotnet run --project src/TrustScore.Api`
- Format: `dotnet format`

## Architecture
- `src/TrustScore.Core/` — Models and interfaces (no dependencies)
- `src/TrustScore.Api/` — HTTP endpoints, data access, scoring engine
- `src/TrustScore.Tests/` — Unit and integration tests
- `migrations/` — SQL migration files (run by DbUp on startup)

## Conventions
- Minimal API style (no controllers)
- Dapper with raw SQL (no EF Core)
- Redis as cache with PostgreSQL fallback (never fail if Redis is down)
- All endpoints return JSON with snake_case property names
- DIDs are passed as query parameters, not path parameters
- Tests use xUnit [Fact] and FluentAssertions .Should()
