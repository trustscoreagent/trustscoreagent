# Security Policy

## Reporting a vulnerability

Please **do not** open a public issue for security vulnerabilities.

Report privately via one of:

1. **GitHub** → the repository's **Security** tab → **Report a vulnerability**
   (private advisory). This is the preferred channel.
2. Email **trustscoreagent@gmail.com** with `[SECURITY]` in the subject.

Please include: a description, reproduction steps, affected endpoint/component, and the
potential impact. We aim to acknowledge within **72 hours** and to agree on a disclosure
timeline with you. We support coordinated disclosure and will credit reporters who wish
to be named.

## Scope

In scope:

- The API (`src/TrustScore.Api`) and core library (`src/TrustScore.Core`)
- The MCP server (`mcp-server/`)
- Receipt verification, DID resolution, the Merkle audit trail, and rate limiting
- Deployment/CI configuration in `.github/workflows/` and `infra/`

Out of scope:

- Denial-of-service via raw request volume (rate limits are best-effort in Phase 1)
- Findings that require a compromised operator or privileged cloud access

## Project maturity and trust model

TrustScoreAgent is in **Phase 1 (early)**. Be aware of the current trust model:

- **Single operator.** The registry is currently run by one operator. Neutrality rests
  on the scoring code being open source and the audit trail being verifiable
  (`GET /v1/audit/proof/{id}` against `GET /v1/audit/root`), not on decentralization.
  Federation/multi-operator is a later phase.
- **Agent identity is self-asserted in Phase 1.** The `X-Agent-DID` header is not yet
  cryptographically bound to the rater. Sybil resistance currently comes from rate
  limiting plus the hourly EigenTrust recompute (inconsistent raters converge toward low
  trust). Mandatory per-request agent signatures (`X-Agent-Signature`) are planned for
  Phase 2. Ratings backed by a verified service **receipt** are the trustworthy signal
  today.
- **Baseline scores come from an operated probe.** A single transparent probe agent
  (`did:web:probe.trustscoreagent.com`) measures public APIs and records real
  availability/latency/conformity ratings (no receipts, normal unverified weight). These are
  genuine, Merkle-audited measurements — not fabricated — and community/receipt ratings layer
  on top. The probe is clearly identified, never pretends to be multiple agents, and only hits
  public endpoints designed for unauthenticated access.
- **Blockchain anchoring of the Merkle root is Phase 2.** Until then the audit log is
  append-only and internally verifiable, but not yet externally anchored.

We document these limits deliberately — knowing the trust boundaries is part of using the
registry responsibly.

## Supported versions

Phase 1 is pre-1.0 and ships from `main`. Security fixes are applied to `main`; there are
no long-term support branches yet.
