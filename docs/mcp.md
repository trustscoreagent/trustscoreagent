# TrustScoreAgent MCP Server

Use TrustScoreAgent directly from Claude, Cursor, or any MCP-compatible agent.

## Installation

### Claude Desktop

Add to your `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "trustscoreagent": {
      "command": "npx",
      "args": ["-y", "@trustscoreagent/mcp-server"]
    }
  }
}
```

### Claude Code (CLI)

```bash
claude mcp add trustscoreagent -- npx -y @trustscoreagent/mcp-server
```

### Manual / Development

```bash
cd mcp-server
npm install
npm run build
node dist/index.js
```

## Available Tools

### check_reputation

Check the trust score of any AI microservice before calling it.

**Parameters:**
- `service_did` (required): DID of the service (e.g., `did:web:api.example.com`)

**Example response:**
```
Trust Score for did:web:api.example.com: 0.87/1.0 (HIGH)
Confidence: 0.94 (based on 2341 ratings)

Dimensions:
  Availability: 0.99
  Latency:      0.82
  Conformity:   0.91

No recent incidents
This service supports trust receipts (verified ratings)
```

### submit_rating

Rate a microservice after calling it.

**Parameters:**
- `service_did` (required): DID of the service
- `status_code` (required): HTTP status code (e.g., 200)
- `latency_ms` (required): Response time in ms
- `response_size_bytes` (optional): Response size
- `schema_valid` (optional): Whether response matched expected format
- `quality_score` (optional): 1-5 subjective quality
- `receipt` (optional): JWT from X-Trust-Receipt header

### list_services

List rated services, most-trusted first.

**Parameters:**
- `sort_by` (optional): `score` (default), `ratings_count`, or `last_rated`
- `limit` (optional): 1–100 (default 20)
- `min_score` (optional): only return services at or above this score

## Agent Identity

Each MCP installation automatically generates a unique agent ID on first run, stored in `~/.trustscoreagent/agent-id`. This ensures:
- Rate limiting is per-user, not shared
- EigenTrust tracks each user independently
- Ratings are attributable to individual agents

You can override the agent ID via environment variable:

```json
{
  "mcpServers": {
    "trustscoreagent": {
      "command": "npx",
      "args": ["-y", "@trustscoreagent/mcp-server"],
      "env": {
        "TRUSTSCORE_AGENT_DID": "did:web:my-custom-agent.example.com"
      }
    }
  }
}
```

## Configuration

Set `TRUSTSCORE_API_URL` environment variable to point to a different API instance:

```json
{
  "mcpServers": {
    "trustscoreagent": {
      "command": "npx",
      "args": ["-y", "@trustscoreagent/mcp-server"],
      "env": {
        "TRUSTSCORE_API_URL": "https://api.trustscoreagent.com"
      }
    }
  }
}
```
