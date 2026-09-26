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

### Cursor

Add to `~/.cursor/mcp.json` (global) or `.cursor/mcp.json` in your project:

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

### Windsurf

Add to `~/.codeium/windsurf/mcp_config.json`:

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

Any MCP-compatible client works: the server speaks MCP over stdio. Configs differ only
in file location; the `command`/`args` are identical.

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
- `service_did` (required): the service, as a domain, URL or DID (e.g., `api.example.com`)

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
- `service_did` (required): the service, as a domain, URL or DID
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
- `limit` (optional): 1-100 (default 20)
- `min_score` (optional): only return services at or above this score
- `min_ratings` (optional): only return services with at least this many ratings

## Agent Identity

On first run the server generates an Ed25519 keypair in `~/.trustscoreagent/agent-key.pem`
(mode `0600`) and uses the matching `did:key` as its identity. Every rating is signed with
that key (`X-Agent-Signature`), so the registry attributes it to this installation instead of
taking the DID header on faith, and signed ratings count at full weight.

Ratings are sent **unsigned, at half weight**, if the key cannot be read or written (the server
then keeps a stable fallback id in `~/.trustscoreagent/agent-id`), or if you set
`TRUSTSCORE_AGENT_DID` to anything other than the derived `did:key`:

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

Leave it unset unless you need a specific identifier and accept that trade-off.

> **Upgrading from 0.1.x:** earlier versions identified themselves with a
> `did:web:mcp.trustscoreagent.com:...` id that no key backs. 0.2.x generates a key and moves
> to a `did:key`, so the previous identity's reputation history does not carry over.

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
