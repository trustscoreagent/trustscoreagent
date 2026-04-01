# TrustScoreAgent

Free, open reputation registry for AI microservices. Agents check trust scores before calling any service.

## What is this?

AI agents increasingly rely on paid microservices. TrustScoreAgent lets any agent:
- **Check** the reputation of a service before calling it
- **Rate** a service after calling it

Two endpoints. Both free. No account needed.

## Quick start

```bash
# Check a service's trust score
curl "https://api.trustscoreagent.com/v1/score?did=did:web:api.example.com"

# Rate a service after calling it
curl -X POST "https://api.trustscoreagent.com/v1/rate" \
  -H "Content-Type: application/json" \
  -H "X-Agent-DID: did:web:my-agent.example.com" \
  -d '{
    "service_did": "did:web:api.example.com",
    "metrics": {
      "status_code": 200,
      "latency_ms": 143,
      "response_size_bytes": 2048,
      "schema_valid": true
    },
    "quality_score": 4
  }'
```

## Local development

```bash
# Start PostgreSQL and Redis
docker compose up -d

# Run the API
cd src/TrustScore.Api
dotnet run

# Run tests
dotnet test
```

The API starts at `http://localhost:5000`. Swagger UI available at `http://localhost:5000/swagger` in development mode.

## Architecture

- **C# / .NET 8** — ASP.NET Core Minimal API
- **PostgreSQL** — Ratings and service scores
- **Redis** — Score caching and rate limiting
- **Beta Reputation System** — Bayesian scoring algorithm
- **Cloudflare** — CDN, DDoS protection, edge caching

## API Reference

### GET /v1/score?did={service_did}

Returns the trust score for a service.

**Response:**
```json
{
  "service": "did:web:api.example.com",
  "score": 0.87,
  "confidence": 0.94,
  "ratings_count": 2341,
  "dimensions": {
    "availability": 0.99,
    "latency": 0.82,
    "conformity": 0.91
  },
  "recent_incidents": 0,
  "last_rated": "2026-03-29T14:23:01Z",
  "service_supports_receipts": true
}
```

### POST /v1/rate

Submit a rating for a service. Include `X-Agent-DID` header.

**Response:**
```json
{
  "accepted": true,
  "rating_weight": "verified",
  "new_score": 0.87
}
```

### GET /health

Health check for infrastructure monitoring.

## MCP Server

TrustScoreAgent is available as an MCP server. See [docs/mcp.md](docs/mcp.md) for setup.

## License

Apache-2.0. See [LICENSE](LICENSE).
