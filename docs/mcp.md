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
- `pay_to` (optional): the `payTo` address of an x402 Payment Required you received, compared
  with the payee the registry has seen on that endpoint

**Example response:**
```
Trust Score for api.example.com: 0.87/1.0 (HIGH)
Confidence: 0.94 (based on 2341 ratings)

Dimensions:
  Availability: 0.99
  Latency:      0.82
  Conformity:   0.91

This service supports trust receipts (verified ratings)
```

Incidents are not tracked yet, so no incident line is shown (0.2.3 and later).

Since 0.2.6, for a paid x402 endpoint the registry probes, the result adds the payment terms it
saw and their stability, and with `pay_to` a comparison:

```
x402 payment terms seen by the registry (last seen 2026-10-17T15:24:58Z):
  5000 of 0x8335...2913 on eip155:8453 (exact), payee 0x7284...299c
  Payee unchanged over 33 observation(s).

WARNING: the payee 0x0000...dead is NOT the one the registry saw on this endpoint.
Do not pay before checking: the server or its domain may have been compromised.
```

`pay_to_check` is `match`, `mismatch`, `rotating` (the endpoint issues a new payee per request,
so no comparison is possible) or `no_data`. An unknown endpoint whose domain has rated endpoints
also gets `provider`, the domain's aggregate score, as a hint about the operator.

Since 0.2.4 every tool also returns its data as `structuredContent` matching a declared
`outputSchema` (for `check_reputation`: `service`, `known`, `score`, `trust_level`, `confidence`,
`ratings_count`, `dimensions`, `recent_incidents`, `service_supports_receipts`, `last_rated`, and
since 0.2.6 `provider`, `x402` and `pay_to_check`), and
declares annotations: `check_reputation` and `list_services` are read-only, `submit_rating` writes a
rating (not destructive, not idempotent). `submit_rating` returns the `rating_id` to fetch the
rating's [audit proof](./api.md#audit).

Since 0.2.5 the server identifies itself with `User-Agent: trustscoreagent-mcp/<version>`, so the
registry can count MCP traffic in its aggregate [usage stats](./api.md#get-v1stats). Nothing else
about the installation is sent beyond the ratings themselves.

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
- `x402` (optional): `true` for only the paid x402 endpoints whose payment terms the registry
  records, `false` for only the others
- `offset` (optional): number of results to skip

Since 0.2.7 the result gives `total`, the number of services matching the filters across all
pages, and the `offset` it starts from: sorted by score, endpoints with few ratings sit near the
neutral 0.5, far down the list, so page with `offset` or sort by `last_rated` to reach them.

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
