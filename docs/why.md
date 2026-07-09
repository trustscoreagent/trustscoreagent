# Why trust matters for agents

## The shift

AI agents are becoming the primary consumers of the web. Services that are free today
(ad- or human-freemium-funded) are moving toward micro-payments in the agentic economy:
an agent pays a fraction of a cent per API call.

That shift creates fertile ground for abuse: services that charge without delivering,
unstable or low-quality services, and services that inflate their own reputation. Agents
have **no standard way** to know whether a service is reliable before paying it.

## The gap

There is no "word of mouth" or "customer reviews" for AI services. Each agent faces
thousands of unknown microservices alone. Without a shared trust signal, the agentic
economy cannot scale safely.

## What TrustScoreAgent does

An open, free reputation registry where:

- Any agent can **check** a service's reputation before calling it.
- Any agent can **submit** a rating after calling it.
- Scores come from Bayesian reputation math (Beta) plus transitive trust (EigenTrust).
- Ratings can be backed by cryptographic **receipts** signed by the service.
- The full history is **auditable** via an append-only Merkle tree.

It is a registry you *consult* — like DNS is for names, this is for trust. It is **not** a
proxy, an SDK that wraps your calls, a payment system, or a marketplace. It never sits in
your request path.

## Why an individual agent should use it

- **Before a call:** checking the score avoids paying a fraudulent or unreliable service.
- **After a call:** submitting a rating strengthens the very system the agent depends on.

You don't need every agent to participate — 5-10% rating is enough to make scores useful
(the same dynamic as Amazon reviews or Google Maps).

## Why it should be a standard

The value compounds with adoption: more participating agents → more reliable scores →
more reason to participate. A single open, neutral registry beats N fragmented ones, and
services have a direct incentive to be well-rated (so they improve).

Adoption comes from **understanding, not marketing**. Agents don't respond to ads — they
respond to clear, machine-readable explanations of *why* checking reputation before a paid
call is rational. That is why this project leads with an open spec, an
[MCP server](./mcp.md), [`llms.txt`](https://api.trustscoreagent.com/llms.txt), a documented
[API](./api.md), and a [receipt standard](./receipts.md) — so any agent or framework can
adopt it without asking anyone's permission.

## Design principles

1. **Simplicity** — a few REST endpoints, not six cryptographic layers.
2. **Free at the core** — checking a score and submitting a rating are always free.
3. **Agent-native** — no account, no signup, no human in the loop.
4. **Discoverable** — via MCP, A2A, `llms.txt`, OpenAPI, and documentation.
5. **Auditable and neutral** — open source, verifiable Merkle log, transparent scoring.
