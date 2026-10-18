# @trustscoreagent/x402

A plugin for the [x402](https://github.com/coinbase/x402) client SDK (`@x402/core`) that checks an
endpoint with [TrustScoreAgent](https://trustscoreagent.com) before your agent pays it.

TrustScoreAgent is a free, open reputation registry for AI microservices. Its probe calls the
x402 endpoints agents pay the most, without paying, and records the payee address and price each
one asks for. Before every payment the plugin compares the payee your agent is about to pay with
the one the registry saw on that endpoint. If they differ, the server or its domain may have been
compromised, and the payment is stopped before anything is signed.

No account, no API key. The plugin runs in your process: your requests never go through
TrustScoreAgent, which is only consulted.

## Install

```bash
npm install @trustscoreagent/x402
```

Requires `@x402/core` 2.23 or later (already there if you use `@x402/fetch` or `@x402/axios`)
and Node.js 18 or later.

## Use

```ts
import { x402Client, x402HTTPClient } from "@x402/core/client";
import { wrapFetchWithPayment } from "@x402/fetch";
import { registerExactEvmScheme } from "@x402/evm/exact/client";
import { trustScoreGuard } from "@trustscoreagent/x402";

const client = new x402Client();
registerExactEvmScheme(client, { signer });
const http = new x402HTTPClient(client);

const guard = trustScoreGuard();
guard.attach(client, http);

const fetchWithPayment = wrapFetchWithPayment(guard.wrapFetch(fetch), http);
const response = await fetchWithPayment("https://api.example.com/x402/search?q=...");
```

A blocked payment rejects like any other aborted payment, with a reason starting with
`TrustScore:`. Pass `http` to `attach`: the guard then looks up the URL your agent actually
called. Without it, it falls back to the `resource.url` the server declares in its 402, which a
server can set to anything.

## What blocks a payment

By default, one thing only: **a payee the registry did not see on that endpoint**, when the
registry saw the endpoint's terms within the last 24 hours. Everything else lets the payment go:

| Situation | Default |
|---|---|
| The payee is one the registry saw on the endpoint | pays |
| The payee differs, terms seen within `maxTermsAgeHours` (24) | **blocks** (`pay_to_mismatch`) |
| The payee differs, terms older than that | pays, with a warning |
| The endpoint issues a new payee on every request | pays (no comparison possible) |
| The price is not one the registry saw | pays, with a warning |
| The endpoint is unknown to the registry | pays, with a warning (and its domain's score if any) |
| The registry is unreachable or slow (`timeoutMs`, 1.5 s) | pays, unless `failClosed` |
| A low score | pays, unless you set `minScore` |

The score never blocks on its own: a registry that turned new or rarely used services away by
default would be a gatekeeper, not a reference. Set `minScore` to refuse known endpoints that
score badly; it only applies to endpoints with at least `minRatings` (10) ratings.

A service that changes its payee legitimately is blocked until the registry's next probe pass
(every 6 hours) sees the new one. If you trust that service anyway, override the decision in
`onDecision`.

## Options

```ts
trustScoreGuard({
  mode: "enforce",            // or "warn": never block, only report
  blockOnPayToMismatch: true,
  maxTermsAgeHours: 24,
  minScore: undefined,        // e.g. 0.4
  minRatings: 10,
  failClosed: false,          // block when the registry cannot answer
  timeoutMs: 1500,
  cacheTtlMs: 60_000,         // lookups are reused per endpoint
  onDecision: (d) => {
    console.log(d.action, d.code, d.reason, d.warnings);
    // return "allow" or "block" to override
  },
  rate: false,                // see below
  apiUrl: "https://api.trustscoreagent.com",
});
```

Every decision carries `action`, `code` (`ok`, `pay_to_mismatch`, `low_score`,
`registry_unavailable`, `no_url`), `reason`, the endpoint `url` and where it came from
(`urlSource`), the payment (`payTo`, `network`, `asset`, `amount`), `payToCheck` (`match`,
`mismatch`, `rotating`, `no_data`), the registry's full answer (`score`) and `warnings`.

## Rating what you paid for (opt-in)

With `rate: true`, after each paid request the plugin submits a rating on the endpoint: status
code, latency, response size, whether the payment settled, and the service's receipt if it sent
one. It is sent in the background and never delays or changes the response.

```ts
const guard = trustScoreGuard({ rate: true, onRating: (r) => console.log(r) });
```

Only outcomes the service owns are rated: a 2xx (it delivered) and a 5xx (it failed to). A 402
after paying usually means the payment itself was refused, so it is not held against the service.

Ratings are signed with an Ed25519 key kept in `~/.trustscoreagent/agent-key.pem` (the same file
the TrustScoreAgent MCP server uses), created on first use. Signed ratings count at full weight;
the key never leaves your machine and only its public half appears in your `did:key`. Use
`rate: { keyFile }` or `rate: { privateKey }` to choose another key.

It is off by default because **a rating is public and permanent**: it says that your agent's
`did:key` paid that endpoint. The query string and fragment are stripped before anything is
sent, to the lookup as to the rating. Turn it on if you are fine with that: every rating makes
the scores more reliable for the next agent. See
[privacy](https://github.com/trustscoreagent/trustscoreagent/blob/main/docs/privacy.md).

## What is sent to the registry

- Before paying: `GET /v1/score?service=<endpoint URL without query string>`.
- After paying, only with `rate`: `POST /v1/rate` with the endpoint URL, the metrics above and
  your `did:key`.
- `User-Agent: trustscoreagent-x402/<version>`, counted in the registry's public
  [usage stats](https://api.trustscoreagent.com/v1/stats). No IP address is stored.

## Without the fetch wrapper

`guard.check(paymentRequired, requirements, requestUrl?)` runs the same check and returns the
decision, for agents that drive payments themselves. `decide()` is the policy alone, without
I/O.

## License

Apache-2.0. Source: [trustscoreagent/trustscoreagent](https://github.com/trustscoreagent/trustscoreagent/tree/main/x402-plugin).
