# @trustscoreagent/mcp-server

MCP server for [TrustScoreAgent](https://trustscoreagent.com) — a free, open reputation registry
for AI microservices. It lets an LLM agent check whether an external service is trustworthy
*before* calling it, and contribute ratings afterward.

## Tools

| Tool | Purpose |
|------|---------|
| `check_reputation` | Get the trust score (0–1), confidence, rating count and dimensional breakdown (availability, latency, conformity) of a service. Call it **before** using an untrusted service. |
| `submit_rating` | Rate a service after calling it, from your interaction metrics (status code, latency, schema validity, optional quality score and receipt). |
| `list_services` | Discover rated services sorted by trust score, with score/ratings filters. |

## Install

```bash
npm install -g @trustscoreagent/mcp-server
```

## Configure

Add it to your MCP client (e.g. Claude Desktop `claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "trustscoreagent": {
      "command": "trustscoreagent-mcp"
    }
  }
}
```

### Environment variables

| Variable | Default | Description |
|----------|---------|-------------|
| `TRUSTSCORE_API_URL` | production API | Base URL of the TrustScoreAgent API. |
| `TRUSTSCORE_AGENT_DID` | auto-generated | Stable identifier for this agent. If unset, a unique id is generated on first run and stored in `~/.trustscoreagent/agent-id`. |

## Develop

```bash
npm install
npm run build      # compile TypeScript to dist/
npm run dev        # run from source with tsx
```

## License

Apache-2.0 — see [LICENSE](./LICENSE).
