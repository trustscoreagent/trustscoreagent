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

## Configure

No install needed — add it to your MCP client (e.g. Claude Desktop
`claude_desktop_config.json`) and it runs via `npx`:

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

For Claude Code: `claude mcp add trustscoreagent -- npx -y @trustscoreagent/mcp-server`

<details>
<summary>Prefer a global install?</summary>

```bash
npm install -g @trustscoreagent/mcp-server
```

Then use `"command": "trustscoreagent-mcp"` (no `args`) in the config above.
</details>

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

## MCPB bundle

For one-click / offline installation (Smithery "local" install, Claude Desktop
drag-and-drop), the server can be packaged as a self-contained `.mcpb` bundle. The
bundle embeds `dist/` plus production dependencies and runs with `node dist/index.js`
— no network install at launch. The manifest is [`manifest.json`](./manifest.json).

```bash
bash scripts/build-mcpb.sh          # -> trustscoreagent-<version>.mcpb
```

The `.mcpb` is a build artifact (git-ignored). Released copies are attached to the
corresponding [GitHub Release](https://github.com/trustscoreagent/trustscoreagent/releases).

## License

Apache-2.0 — see [LICENSE](./LICENSE).
