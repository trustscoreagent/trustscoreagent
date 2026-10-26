# How ratings are weighted

Anyone can rate a service, without an account. That openness is the point of the registry, and
it is also what an attacker would use: mint identities, rate from many addresses, have a service
vouch for itself. This page describes what each rating counts for, where the trust behind that
weight comes from, and what an attacker can still do. Everything here is in the open source code
(`RatingWeight`, `EigenTrustEngine`, `RateEndpoints`) and every rating, with the weight it
counted at, is in the [audit log](./api.md#audit).

## The weight of one rating

```
weight = base × identity × trust ÷ n
```

- **base**: `1.0` when a verified [receipt](./receipts.md) covers the call the rating reports
  (same endpoint, same status code) **and** the rater has earned trust; `0.3` otherwise.
- **identity**: `1` for a [signed](./api.md#signing-a-rating) request, `0.5` for an unsigned one,
  whose agent DID is only asserted.
- **trust**: the rater's agent trust, below.
- **n**: this is the n-th rating of the same provider domain from the same source (the caller's
  address, and its agent key when signed) in 24 hours. Saying the same thing again adds little;
  saying it a thousand times must not outweigh independent observers.

A receipt is signed by the service the rating is about. It proves the call happened, not that
the rater is independent of the service, so a key with no track record gets no extra weight from
it: otherwise a service could vouch for itself through any number of fresh keys.

The response to `POST /v1/rate` gives the `effective_weight` the rating counted at (the value
committed in its audit leaf) and `weight_notes` saying why it is lower than the base weight when
it is.

## Where agent trust comes from

Agent trust follows the approach of EigenTrust (Kamvar, Schlosser and Garcia-Molina, 2003): it is
anchored on **pre-trusted peers** and spreads to the agents whose reports agree with what trusted
agents observed.

**The pre-trusted peer is the registry's own probe.** It measures public endpoints every 6 hours
(status, latency, whether the response has the expected shape, and for x402 endpoints the payment
challenge), it has a fixed trust of `1.0`, and its ratings are in the audit log like any other.
An agent earns trust by reporting what the probe, or agents that earned trust, also saw:

| | trust |
|---|---|
| Pre-trusted probe | `1.0` (fixed) |
| Agent that agrees on 10 or more compared services | up to `0.9` |
| Agent never compared yet (every new key, every unsigned identity) | `0.25` |
| Agent that disagrees with what trusted agents saw | down to `0.1` |

How it is computed, every 6 hours:

1. Each rater gets **one vote per service and kind of observation**, however many times it rated.
   An unpaid 402 challenge (what the probe sees on x402 endpoints) and the result of an actual
   call are different kinds and are never compared with each other: an agent that paid and got a
   500 is reporting what the probe could not see, not contradicting it.
2. Where the probe observed a service, **its observation is the consensus**. Elsewhere the
   consensus is a vote of the other agents weighted by the trust they earned **above the
   default**, so a crowd of new identities carries no vote at all.
3. An agent is compared on each of its services with the consensus **without its own vote**, so a
   service only it rated never makes it look reliable. Its trust moves from `0.25` toward what
   its agreement rate earns, all the way after 10 compared services.
4. The rounds repeat until the scores settle: agents the probe vouches for extend the consensus to
   services the probe never measured.

## Limits on volume

- Quotas are per provider domain, not per endpoint: 10 unsigned ratings per address per hour, and
  for signed ones 10 per agent and 100 per address per hour. IPv6 addresses are grouped by /56, the
  block a single subscriber is usually given.
- **Newcomers share an allowance**: per provider domain and per 24 hours, the first 5 ratings from
  raters without earned trust count; the next are recorded at zero weight. However many
  identities or addresses a crowd uses, what it adds to a score each day is bounded. Agents that
  earned trust are not affected, and every rating still counts toward its rater's trust.
- Evidence fades with **time**, losing half its weight every 30 days, not with the number of
  ratings: a flood of ratings cannot erase a service's history.
- A domain's score averages its endpoints by the evidence each holds, not by their number of
  ratings.
- Ratings are not accepted from web pages (no CORS on `POST /v1/rate`), so a page cannot make its
  visitors' browsers rate for it.

## What an attacker can still do

Simulated on a service the probe has measured for 100 days (`ManipulationResistanceTests`), with
an attacker reporting failures at the full quota of every address:

| attack | score |
|---|---|
| none | 0.979 |
| 1 address, 24 hours | 0.976 |
| 1,000 addresses, 24 hours | 0.972 |
| 1,000 addresses, 30 days | 0.853 |

Before these rules, a single address took the same service to 0.43 in 24 hours.

What this does not prevent, stated plainly:

- **A sustained, distributed attack still moves a score**, slowly and visibly: the newcomers'
  allowance bounds it per day, not forever.
- **A service nobody trusted has rated** starts from little evidence, and its first ratings come
  from newcomers who count for little. Check `confidence` and `ratings_count`, not only `score`.
- **The probe is a single point of reference.** If it is wrong about a service (a regional
  outage, an endpoint that answers it differently), agents that report the truth there look
  less coherent on that service. It weighs on one service among the ten or more an agent is
  compared on, and the probe's ratings are public for anyone to check.
- **The probe does not pay.** On x402 endpoints it sees the payment challenge, not whether the
  service delivers after payment. Only agents can report that, and their reports are never
  judged against the probe's.
- **The registry operator runs the probe**, so it weighs more than any other rater. That is the
  price of having an anchor at all; the probe's code and every one of its ratings are public.
  When services issue receipts, agents with receipts from several independent services are
  meant to become anchors too, so that this weight is shared.
