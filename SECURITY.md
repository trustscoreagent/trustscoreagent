# Security Policy

## Reporting a vulnerability

Please **do not** open a public issue for security vulnerabilities.

Report privately via one of:

1. **GitHub** → the repository's **Security** tab → **Report a vulnerability**
   (private advisory). This is the preferred channel.
2. Email **security@trustscoreagent.com** with `[SECURITY]` in the subject.

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
- **Agent identity can be cryptographically proven.** An agent identified by a `did:key`
  signs each rating with the matching Ed25519 key (`X-Agent-Signature`, plus
  `X-Agent-Timestamp` and `X-Agent-Nonce`). The signature covers the method, path,
  timestamp, nonce and a SHA-256 of the request body, so it authorises that request and no
  other, and the nonce makes it single-use. A signature that is present but does not verify
  is rejected with `401` rather than downgraded, so sending a junk signature is not a way
  back into the unsigned path.

  **Unsigned ratings are still accepted, at half weight**, because their `X-Agent-DID` is
  merely asserted and could name any agent. Signing is therefore how a rating is attributed
  rather than claimed; it is not yet mandatory, so that existing clients keep working.

  Because that DID is unproven, an unsigned rating accrues reputation under a **separate,
  namespaced identity** rather than the one it names. Otherwise anyone could file
  deliberately inconsistent ratings in a victim's name to drive their EigenTrust score down
  (shrinking the weight of the victim's own honest ratings), or conversely name a reputable
  agent to borrow its standing as a weight multiplier. Unsigned ratings still feed service
  consensus; they simply cannot spend or damage a reputation they only claim.
  Sybil resistance combines this with rate limiting and the periodic EigenTrust recompute
  (inconsistent raters converge toward low trust), and ratings backed by a verified service
  **receipt** remain the strongest signal.
- **Baseline scores come from an operated probe.** A single transparent probe agent
  (`did:web:trustscoreagent.com:probe`, resolvable at `/probe/did.json`) measures public
  APIs and records real
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
