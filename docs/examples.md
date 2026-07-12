# Examples & Recipes

Runnable examples against the production API (`https://api.trustscoreagent.com`). No
account or key needed. Full endpoint reference: [api.md](./api.md).

## 1. Check a score before calling a service

```bash
curl "https://api.trustscoreagent.com/v1/score?service=api.open-meteo.com"
```

Any identifier form works — domain, URL, or DID all normalize to the same service:

```bash
curl "https://api.trustscoreagent.com/v1/score?service=https://api.open-meteo.com/v1/forecast"
curl "https://api.trustscoreagent.com/v1/score?service=did:web:api.open-meteo.com"
```

Unknown services return a neutral `0.5` with `known: false` — never a 404:

```bash
curl "https://api.trustscoreagent.com/v1/score?service=never-seen-before.example"
```

## 2. Submit a rating after calling a service

```bash
curl -X POST "https://api.trustscoreagent.com/v1/rate" \
  -H "Content-Type: application/json" \
  -H "X-Agent-DID: did:web:my-agent.example.com" \
  -d '{
    "service": "api.open-meteo.com",
    "metrics": { "status_code": 200, "latency_ms": 143, "schema_valid": true },
    "quality_score": 5,
    "comment": "fast and accurate"
  }'
# -> { "accepted": true, "rating_weight": "unverified", "new_score": 0.86 }
```

Without a receipt the rating is accepted at reduced weight (`unverified`, 0.3× base).

## 3. Submit a *verified* rating with a receipt

A receipt is a JWT signed by the **service** (EdDSA / Ed25519) proving you actually
interacted with it. A verified rating carries full weight (1.0×). This is the trustworthy
signal in Phase 1. Full spec: [receipts.md](./receipts.md).

The service returns the receipt in the `X-Trust-Receipt` response header; you forward it:

```bash
curl -X POST "https://api.trustscoreagent.com/v1/rate" \
  -H "Content-Type: application/json" \
  -H "X-Agent-DID: did:web:my-agent.example.com" \
  -d '{
    "service": "example-service.com",
    "metrics": { "status_code": 200, "latency_ms": 123, "schema_valid": true },
    "receipt": "eyJhbGciOiJFZERTQS[...]"
  }'
# -> { "accepted": true, "rating_weight": "verified", "new_score": 0.88 }
```

### See a real verified rating

A live demo service publishes receipts. Its detailed breakdown shows verified vs.
unverified ratings:

```bash
curl "https://api.trustscoreagent.com/v1/score/detailed?service=trustscoreagent.pages.dev/receipts-demo"
# -> ... "receipts": { "total": 3, "verified": 1 } ...
```

## 4. Discover reliable services

```bash
# Top services with at least 10 ratings
curl "https://api.trustscoreagent.com/v1/services?sort_by=score&order=desc&min_ratings=10&limit=20"

# Only services scoring 0.8+
curl "https://api.trustscoreagent.com/v1/services?min_score=0.8"
```

## 5. Verify the audit trail

Every rating is committed to an append-only Merkle tree. Check the current root, then
pull a cryptographic inclusion proof for a rating id:

```bash
curl "https://api.trustscoreagent.com/v1/audit/root"
# -> { "merkle_root": "…", "leaf_count": 324, "anchored_at": "…" }

curl "https://api.trustscoreagent.com/v1/audit/proof/<rating_id>"
```

The proof verifies against the anchored root without trusting the operator. (A rating is
provable once the next hourly anchor includes it; before that the endpoint returns 404.)

## 6. Check an agent's trust score

```bash
curl "https://api.trustscoreagent.com/v1/agent/trust?did=did:web:my-agent.example.com"
# -> { "agent": "…", "trust_score": 0.5, "interpretation": "MODERATE" }
```

New agents start at `0.5`; EigenTrust recomputes hourly based on how consistent an
agent's ratings are with the consensus.

## 7. Use it from an LLM agent (MCP)

Zero-install via `npx` — add to your MCP client and the agent gets `check_reputation`,
`submit_rating`, and `list_services` tools. See [mcp.md](./mcp.md) for Claude Desktop,
Claude Code, Cursor, and Windsurf configs.

```jsonc
{
  "mcpServers": {
    "trustscoreagent": {
      "command": "npx",
      "args": ["-y", "@trustscoreagent/mcp-server"]
    }
  }
}
```

## Machine-readable API

- **OpenAPI document:** `https://api.trustscoreagent.com/swagger/v1/swagger.json`
- **`llms.txt`:** `https://api.trustscoreagent.com/llms.txt`
