# Privacy & Data Handling

TrustScoreAgent is a public registry. This page documents exactly what data the
service stores, what it does **not** store, and how the data is used. It reflects the
behavior of the code in this repository — if you find a discrepancy, please
[report it](../SECURITY.md).

## TL;DR

- **No accounts, no API keys, no signup.** Nothing personal is required to use the API.
- The registry stores **service reputation data** and the **ratings** that produce it.
- It does **not** store IP addresses, request bodies, credentials, or the payloads of
  the services you rate.
- A rating you submit is **public** and part of an append-only audit log — treat it as
  a public statement.

## What is stored

When you call `POST /v1/rate`, the following is persisted in PostgreSQL (see
`migrations/001_initial_schema.sql`):

| Field | Source | Notes |
|-------|--------|-------|
| `service_did` | your request | The service being rated (normalized to a domain/DID). |
| `agent_did` | your `X-Agent-DID` header | **Self-asserted** identifier — see below. |
| `status_code`, `latency_ms`, `response_size_bytes`, `schema_valid` | your request | Interaction metrics. |
| `quality_score` | your request (optional) | 1–5 subjective rating. |
| `comment` | your request (optional) | Free text, ≤ 500 chars. **Public.** |
| `has_receipt`, `receipt_verified`, `weight` | derived | Whether a valid service receipt backed the rating. |
| `created_at` | server | Timestamp. |

Aggregate, non-identifying data is also kept: per-service Beta reputation parameters,
rating counts, and per-agent EigenTrust scores.

The **Merkle audit log** (`GET /v1/audit/root`, `/v1/audit/proof/{id}`) records a hash
of each rating so the history is tamper-evident. This is by design: auditability is a
core feature. It means ratings are effectively **permanent and public**.

## What is *not* stored

- **No IP addresses in the database.** Your IP is used only transiently, in Redis, for
  rate limiting, and is never written to durable storage or linked to a rating.
- **No request/response bodies** of the services you rate — only the metrics you send.
- **No credentials, tokens, or API keys.** The API has none to collect.
- **No cookies, no tracking, no analytics pixels** on the API.

## The `agent_did` identifier

`X-Agent-DID` is **self-asserted** in Phase 1 — it is not yet cryptographically bound to
the rater (mandatory agent signatures are a Phase 2 item; see [SECURITY.md](../SECURITY.md)).

- The MCP server auto-generates a **random** agent id on first run and stores it locally
  at `~/.trustscoreagent/agent-id`. It contains no personal information.
- **Do not put personal or sensitive information in your `agent_did` or in `comment`.**
  Both are stored and served publicly and are part of the permanent audit log.

## Receipts

A receipt is a JWT signed by the **service** proving you interacted with it. TrustScoreAgent
verifies the signature and stores only *whether* verification succeeded plus the anti-replay
nonce — not the receipt's contents beyond what is needed to validate and de-duplicate it.

## Data location & retention

Data is hosted on Google Cloud (Cloud SQL for PostgreSQL, Memorystore for Redis) in the
EU. Because the audit log is append-only, ratings are retained indefinitely. There is no
self-service deletion in Phase 1; if you believe a rating contains information that must be
removed, contact **security@trustscoreagent.com**.

## Changes

This document evolves with the project. Material changes will be noted in
[CHANGELOG.md](../CHANGELOG.md).
