# Privacy & Data Handling

TrustScoreAgent is a public registry. This page documents exactly what data the
service stores, what it does **not** store, and how the data is used. It reflects the
behavior of the code in this repository. If you find a discrepancy, please
[report it](../SECURITY.md).

## TL;DR

- **No accounts, no API keys, no signup.** Nothing personal is required to use the API.
- The registry stores **service reputation data** and the **ratings** that produce it.
- It does **not** store IP addresses, request bodies, credentials, or the payloads of
  the services you rate.
- A rating you submit is **public** and part of an append-only audit log: treat it as
  a public statement.

## What is stored

When you call `POST /v1/rate`, the following is persisted in PostgreSQL (see
`RatingRepository.cs` and migrations 001, 009 and 013):

| Field | Source | Notes |
|-------|--------|-------|
| `id` | server | The `rating_id` returned to you, used to fetch the audit proof. |
| `service_did` | your request | The service being rated (normalized to a domain or domain/path). |
| `agent_did` | your `X-Agent-DID` header | **Self-asserted** identifier, see below. |
| `status_code`, `latency_ms`, `response_size_bytes`, `schema_valid` | your request | Interaction metrics. |
| `quality_score` | your request (optional) | 1–5 subjective rating. |
| `comment` | your request (optional) | Free text, ≤ 500 chars. **Public.** |
| `has_receipt`, `receipt_verified`, `signature_verified`, `weight` | derived | Whether a valid service receipt and a valid agent signature backed the rating, and the weight it counted at. |
| `merkle_leaf_hash`, `leaf_version` | derived | The rating's audit leaf (see [MERKLE-SPEC.md](./MERKLE-SPEC.md)). |
| `created_at` | server | Timestamp. |

Aggregate, non-identifying data is also kept: per-service Beta reputation parameters,
rating counts, and per-agent EigenTrust scores.

The **Merkle audit log** (`GET /v1/audit/root`, `/v1/audit/proof/{id}`) records a hash
of each rating's metrics, flags and weight (not of the agent DID or comment) so the history
is tamper-evident. This is by design: auditability is a
core feature. It means ratings are effectively **permanent and public**.

## What is *not* stored

- **No IP addresses in the database.** Your IP is used only transiently, in Redis, for
  rate limiting, and is never written to durable storage or linked to a rating.
- **No request/response bodies** of the services you rate, only the metrics you send.
- **No credentials, tokens, or API keys.** The API has none to collect.
- **No cookies, no tracking, no analytics pixels** on the API.

## The `agent_did` identifier

`X-Agent-DID` is bound to the rater only when the request is **signed**
(`X-Agent-Signature`); unsigned ratings carry an asserted DID and count for half. Signing is
not yet mandatory, so existing clients keep working. See [SECURITY.md](../SECURITY.md).

- The MCP server generates a **random** Ed25519 keypair on first run, stores it locally at
  `~/.trustscoreagent/agent-key.pem`, and derives its `did:key` identity from it. The key is
  generated on your machine and never leaves it; only the public half appears in the DID,
  and neither contains personal information.
- **Do not put personal or sensitive information in your `agent_did` or in `comment`.**
  Both are stored and served publicly. Neither is part of the audit log's hashes, which is what
  allows them to be erased: an erasure clears these two fields and keeps the rating itself.

## Receipts

A receipt is a JWT signed by the **service** proving you interacted with it. TrustScoreAgent
verifies the signature and stores only *whether* verification succeeded plus the anti-replay
nonce, not the receipt's contents beyond what is needed to validate and de-duplicate it.

## Data location & retention

Data is hosted on Google Cloud (Cloud SQL for PostgreSQL, Redis) in the EU. Because the audit
log is append-only, ratings are retained indefinitely; an erasure request clears the agent DID
and comment of the ratings concerned rather than deleting them, since removing an anchored rating
would break the log's consistency proofs. There is no self-service erasure in Phase 1; contact
**security@trustscoreagent.com**.

## Changes

This document evolves with the project. Material changes will be noted in
[CHANGELOG.md](../CHANGELOG.md).
