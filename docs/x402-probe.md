# The x402 probe

Before an agent pays an x402 endpoint it has never used, it needs to know whether that endpoint
is up, answers fast, and asks for payment the way the protocol says it should. The registry
measures this itself, so a score exists for the endpoints agents actually pay, not only for free
public APIs.

## Which endpoints

Once a day, the batch job reads the public [x402 Bazaar](https://docs.cdp.coinbase.com/x402/bazaar)
catalog (`GET https://api.cdp.coinbase.com/platform/v2/x402/discovery/resources`, no key needed)
and selects:

- HTTPS endpoints of type `http`, on the default port, called with `GET`, `POST` or `HEAD`;
- that had **more than 10 distinct payers over the last 30 days**, according to the catalog;
- the most used first, **at most 20 per host** so one provider listing thousands of endpoints
  cannot crowd out the rest, and **500 in all**.

The catalog's payer counts only decide what gets probed. They never enter a score.

If the catalog cannot be read, the previous selection stays in use. An endpoint that drops out of
the selection stops being probed; the ratings it already has stay in the audit log.

## What a probe sends

One request per endpoint every 6 hours, from `TrustScoreAgent-Probe/1.0 (+https://trustscoreagent.com)`:

- the method the catalog declares, to the URL exactly as listed;
- **no payment**: no `PAYMENT-SIGNATURE` or `X-PAYMENT` header, and the registry holds no wallet
  or key. A `POST` carries an empty JSON object `{}`;
- no redirects followed, and only to public addresses (the URLs come from third parties, so the
  same SSRF guard as `did:web` resolution applies);
- a 10 second timeout, at most 8 requests at a time, interleaved across hosts.

## What gets recorded

Each answer becomes an ordinary rating from the registry's probe agent
(`did:web:trustscoreagent.com:probe`), at the normal unverified weight, Merkle-audited like every
other rating:

| Answer | `status_code` | `schema_valid` | Effect |
|---|---|---|---|
| `402` with a valid x402 challenge | 402 | `true` | availability and conformity up |
| `402` without one | 402 | `false` | conformity down, availability unchanged |
| `2xx` (no payment asked) | as received | `null` | availability up |
| other `4xx` | as received | `null` | availability unchanged |
| `5xx`, timeout, connection failure | 503 for no answer, else as received | `null` | availability down |

Latency counts in every case, around the 2 second threshold; for an unanswered request it is the
full timeout, so a fast refusal is never rewarded.

A challenge is valid when the `PAYMENT-REQUIRED` header (x402 v2), or the JSON body (v1), names
version 1 or 2 and at least one complete way to pay: `scheme`, `network`, `asset`, `payTo`, and
an integer `amount` (`maxAmountRequired` in v1).

An endpoint that gives no sign of being there (no `402` and no `2xx`) for 3 days in a row stops
being recorded until it answers again: by then a moved URL is likelier than an outage, and the
registry does not publish accusations based on its own stale data. An invalid challenge is not a
reason to stop: it is exactly what a payer needs to know.

## Opting out

If you run an x402 endpoint and do not want it probed, email **security@trustscoreagent.com**
with the host name. It goes on the probe's exclusion list (`X402Probe:ExcludedHosts`, which also
covers subdomains) at the next deploy. Ratings already recorded stay in the audit log, which only
grows.
