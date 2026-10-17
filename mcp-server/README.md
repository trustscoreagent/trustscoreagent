# @trustscoreagent/mcp-server

MCP server for [TrustScoreAgent](https://trustscoreagent.com), a free, open reputation registry
for AI microservices. It lets an LLM agent check whether an external service is trustworthy
*before* calling it, and contribute ratings afterward.

## Tools

| Tool | Purpose |
|------|---------|
| `check_reputation` | Get the trust score (0 to 1), confidence, rating count and dimensional breakdown (availability, latency, conformity) of a service. Call it **before** using an untrusted service. For an x402 endpoint, pass the `payTo` of the 402 you received as `pay_to`: it says whether that payee is the one the registry has seen there. |
| `submit_rating` | Rate a service after calling it, from your interaction metrics (status code, latency, schema validity, optional quality score and receipt). |
| `list_services` | Discover rated services sorted by trust score, with score/ratings filters. |

### Before paying an x402 endpoint

```
check_reputation({ service_did: "https://api.example.com/x402/search", pay_to: "0x7284...299c" })
```

The result carries `x402`, the payment terms the registry's probe saw on that endpoint (amount,
asset, network, payee) and when the payee or the price last changed, and `pay_to_check`:

| `pay_to_check` | Meaning |
|---|---|
| `match` | The payee is one the registry saw on this endpoint. |
| `mismatch` | It is not. Stop and check before paying: the server or its domain may have been compromised. |
| `rotating` | The endpoint issues a new payee on every request, so no comparison is possible. |
| `no_data` | The registry has no payment terms for this endpoint. |

For an endpoint nobody rated yet, `provider` gives the aggregate score of its domain's rated
endpoints: a hint about the operator, not a measurement of that endpoint.

## Configure

No install needed: add it to your MCP client (e.g. Claude Desktop
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
| `TRUSTSCORE_AGENT_DID` | auto-generated | Overrides this agent's identifier. Setting it disables signing (see below), so ratings count for half. |

### Agent identity and signing

On first run the server generates an Ed25519 keypair in `~/.trustscoreagent/agent-key.pem`
(mode `0600`) and derives its identity from it as a `did:key`. Every rating is signed with
that key, so the registry can attribute it to this installation rather than take the DID
header on faith. **Unsigned ratings still work but count for half.**

Back up that file to keep your agent's reputation: lose it and you get a new identity.

Ratings are sent unsigned if the key cannot be read or written, or if
`TRUSTSCORE_AGENT_DID` is set to something other than the derived `did:key` (the server
binds signatures to the DID, so a mismatched key would only produce `401`s).

> **Upgrading from 0.1.x:** earlier versions used a `did:web:mcp.trustscoreagent.com:...`
> identifier that no key backed. On upgrade the server generates a key and switches to a
> `did:key`, so the previous identity's reputation history does not carry over.

### What it sends

Only what the tools need: the service to look up or the `list_services` filters, and for
`submit_rating` the rating, its signature and the agent's `did:key`. Requests carry
`User-Agent: trustscoreagent-mcp/<version>` (since 0.2.5) so the registry can count MCP use in its
aggregate
[usage stats](https://github.com/trustscoreagent/trustscoreagent/blob/main/docs/api.md#get-v1stats),
which keep no IP address or DID.

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
with no network install at launch. The manifest is [`manifest.json`](./manifest.json).

```bash
bash scripts/build-mcpb.sh          # -> trustscoreagent-<version>.mcpb
```

The `.mcpb` is a build artifact (git-ignored). Released copies are attached to the
corresponding [GitHub Release](https://github.com/trustscoreagent/trustscoreagent/releases).

## License

Apache-2.0, see [LICENSE](./LICENSE).
