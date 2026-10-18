import type { x402Client, x402HTTPClient } from "@x402/core/client";
import type { PaymentRequired, PaymentRequirements } from "@x402/core/types";
import type { KeyObject } from "node:crypto";
import { loadIdentity, signRequest, type AgentIdentity } from "./identity.js";

export { DEFAULT_KEY_FILE } from "./identity.js";

export const VERSION = "0.1.0";

const DEFAULT_API_URL = "https://api.trustscoreagent.com";
// Identifies this client in the registry's aggregate usage counts (GET /v1/stats). Nothing else
// about the caller is sent or kept.
const USER_AGENT = `trustscoreagent-x402/${VERSION}`;

// ---------------------------------------------------------------------------------------------
// What the registry returns (GET /v1/score), limited to the fields the guard reads.

export interface X402Accept {
  scheme: string;
  network: string;
  asset: string;
  amount: string;
  pay_to: string;
}

export interface X402Terms {
  version: number;
  accepts: X402Accept[];
  first_seen: string;
  last_seen: string;
  observations: number;
  pay_to_changed_at: string | null;
  pay_to_changes: number;
  pay_to_rotates: boolean;
  price_changed_at: string | null;
  price_changes: number;
  price_varies: boolean;
}

export interface ScoreResponse {
  service: string;
  score: number;
  confidence: number;
  ratings_count: number;
  known: boolean;
  provider?: { score: number; ratings_count: number } | null;
  x402?: X402Terms | null;
}

// ---------------------------------------------------------------------------------------------
// Decisions

/**
 * - `match`: the payee is one the registry saw on this endpoint.
 * - `mismatch`: it is not. The server or its domain may have been compromised.
 * - `rotating`: the endpoint issues a new payee per request, so addresses cannot be compared.
 * - `no_data`: the registry has no payment terms for this endpoint.
 */
export type PayToCheck = "match" | "mismatch" | "rotating" | "no_data";

/**
 * Why the guard decided what it did. Only `pay_to_mismatch`, `low_score` and, with
 * `failClosed`, `registry_unavailable` / `no_url` can block a payment.
 */
export type DecisionCode = "ok" | "pay_to_mismatch" | "low_score" | "registry_unavailable" | "no_url";

export interface Decision {
  action: "allow" | "block";
  code: DecisionCode;
  /** One sentence, also used as the abort reason when the payment is blocked. */
  reason: string;
  /** The endpoint looked up, without query string or fragment. Null when none was known. */
  url: string | null;
  /**
   * `request`: the URL the agent actually called. `declared`: the `resource.url` the server put in
   * its 402, used only when the transport did not pass the request URL (attach without an
   * x402HTTPClient). A server can declare any URL, so prefer `request`.
   */
  urlSource: "request" | "declared" | null;
  payTo: string;
  network: string;
  asset: string;
  amount: string;
  payToCheck: PayToCheck | null;
  score: ScoreResponse | null;
  /** Things worth logging that did not block the payment. */
  warnings: string[];
}

export type DecisionOverride = "allow" | "block" | void;

export interface RateOptions {
  /** Ed25519 private key (KeyObject or PKCS#8 PEM). Defaults to the key in `keyFile`. */
  privateKey?: KeyObject | string;
  /** Where the key is kept. Defaults to ~/.trustscoreagent/agent-key.pem, shared with the MCP server. */
  keyFile?: string;
}

export interface RatingResult {
  service: string;
  statusCode: number;
  latencyMs: number;
  accepted: boolean;
  ratingId: string | null;
  /** `signed` when the registry verified the signature, as it should with this plugin. */
  agentIdentity: string | null;
  error: string | null;
}

export interface GuardOptions {
  /** Registry to consult. Default https://api.trustscoreagent.com. */
  apiUrl?: string;
  /** `enforce` (default) blocks as described below; `warn` never blocks and only reports. */
  mode?: "enforce" | "warn";
  /** Block when the payee is not one the registry saw on the endpoint. Default true. */
  blockOnPayToMismatch?: boolean;
  /**
   * A mismatch only blocks when the registry saw the endpoint's terms within this many hours,
   * so a payee that changed legitimately is not refused on stale data. Default 24.
   */
  maxTermsAgeHours?: number;
  /** Block a known endpoint scoring below this. Off by default: the score alone never blocks. */
  minScore?: number;
  /** `minScore` only applies to endpoints with at least this many ratings. Default 10. */
  minRatings?: number;
  /** Block when the registry cannot be reached or no endpoint URL is known. Default false. */
  failClosed?: boolean;
  /** Time allowed for the registry lookup before giving up. Default 1500 ms. */
  timeoutMs?: number;
  /** How long a lookup is reused for the same endpoint. Default 60 000 ms. */
  cacheTtlMs?: number;
  /** Called with every decision. Return "allow" or "block" to override it. */
  onDecision?: (decision: Decision) => DecisionOverride | Promise<DecisionOverride>;
  /**
   * After each paid request, submit a rating (status, latency, settlement) on the endpoint to
   * the registry. Off by default: a rating is public and permanent, and names what you paid for.
   */
  rate?: boolean | RateOptions;
  /** Called after each rating attempt. */
  onRating?: (result: RatingResult) => void;
  /** fetch used to reach the registry. Defaults to globalThis.fetch. */
  fetch?: typeof globalThis.fetch;
}

const BLOCKING_CODES: ReadonlySet<DecisionCode> = new Set(["pay_to_mismatch", "low_score"]);

// EVM addresses are case-insensitive (the mixed case is only a checksum); others are not.
function sameAddress(a: string, b: string): boolean {
  return a.startsWith("0x") && b.startsWith("0x") ? a.toLowerCase() === b.toLowerCase() : a === b;
}

function sameAmount(a: string, b: string): boolean {
  const trim = (s: string) => s.replace(/^0+(?=\d)/, "");
  return trim(a) === trim(b);
}

export function payToCheck(x402: X402Terms | null | undefined, payTo: string): PayToCheck {
  if (!x402?.accepts?.length) return "no_data";
  if (x402.pay_to_rotates) return "rotating";
  return x402.accepts.some((a) => sameAddress(a.pay_to, payTo)) ? "match" : "mismatch";
}

/** Strips query string and fragment: they can carry the agent's data, and the registry ignores them. */
export function endpointUrl(url: string): string | null {
  try {
    const u = new URL(url);
    if (u.protocol !== "http:" && u.protocol !== "https:") return null;
    u.search = "";
    u.hash = "";
    return u.toString();
  } catch {
    return null;
  }
}

function hostOf(url: string | null | undefined): string | null {
  if (!url) return null;
  try {
    return new URL(url).host.toLowerCase();
  } catch {
    return null;
  }
}

/** Amount asked by a requirement: `amount` in x402 v2, `maxAmountRequired` in v1. */
function amountOf(req: PaymentRequirements): string {
  const r = req as PaymentRequirements & { maxAmountRequired?: string };
  return String(r.amount ?? r.maxAmountRequired ?? "");
}

export interface DecideInput {
  url: string | null;
  urlSource: Decision["urlSource"];
  requirements: PaymentRequirements;
  /** The registry's answer, or null when it could not be reached. */
  score: ScoreResponse | null;
  warnings?: string[];
  now?: Date;
}

/** The policy, without I/O: what to do with this payment given what the registry knows. */
export function decide(input: DecideInput, options: GuardOptions = {}): Decision {
  const { url, urlSource, requirements, score } = input;
  const warnings = [...(input.warnings ?? [])];
  const payTo = requirements.payTo;
  const base = {
    url,
    urlSource,
    payTo,
    network: requirements.network,
    asset: requirements.asset,
    amount: amountOf(requirements),
    score,
    warnings,
  };
  const enforce = (options.mode ?? "enforce") === "enforce";
  const failClosed = options.failClosed ?? false;

  const result = (code: DecisionCode, reason: string, check: PayToCheck | null): Decision => {
    const blocks = BLOCKING_CODES.has(code) || (failClosed && (code === "registry_unavailable" || code === "no_url"));
    return { ...base, code, reason, payToCheck: check, action: enforce && blocks ? "block" : "allow" };
  };

  if (!url) return result("no_url", "no endpoint URL to look up, so the payee could not be checked", null);
  if (!score) return result("registry_unavailable", `TrustScore could not be reached to check ${url}`, null);

  const x402 = score.x402 ?? null;
  const check = payToCheck(x402, payTo);

  if (check === "mismatch") {
    const maxAgeMs = (options.maxTermsAgeHours ?? 24) * 3_600_000;
    const ageMs = (input.now ?? new Date()).getTime() - Date.parse(x402!.last_seen);
    const fresh = Number.isFinite(ageMs) && ageMs <= maxAgeMs;
    const seen = x402!.accepts.map((a) => a.pay_to).join(", ");
    if (fresh && (options.blockOnPayToMismatch ?? true))
      return result(
        "pay_to_mismatch",
        `payee ${payTo} is not the one TrustScore saw on ${url} (${seen}, last seen ${x402!.last_seen}); ` +
          `the server or its domain may have been compromised`,
        check,
      );
    warnings.push(
      fresh
        ? `payee ${payTo} is not the one TrustScore saw on ${url} (${seen})`
        : `payee ${payTo} differs from what TrustScore saw on ${url}, but that was on ${x402!.last_seen}, too long ago to block`,
    );
  }

  if (
    x402?.accepts?.length &&
    !x402.price_varies &&
    !x402.accepts.some(
      (a) => a.network === requirements.network && sameAddress(a.asset, requirements.asset) && sameAmount(a.amount, amountOf(requirements)),
    )
  )
    warnings.push(`the price asked (${amountOf(requirements)} of ${requirements.asset} on ${requirements.network}) is not one TrustScore saw on ${url}`);

  if (!score.known) {
    const hint = score.provider
      ? `; its domain scores ${score.provider.score} over ${score.provider.ratings_count} rating(s)`
      : "";
    warnings.push(`TrustScore has no rating for ${url} yet${hint}`);
  }

  const minRatings = options.minRatings ?? 10;
  if (options.minScore !== undefined && score.known && score.ratings_count >= minRatings && score.score < options.minScore)
    return result(
      "low_score",
      `${url} scores ${score.score} over ${score.ratings_count} ratings on TrustScore, below the minimum of ${options.minScore}`,
      check,
    );

  return result("ok", `TrustScore found nothing against paying ${url}`, check);
}

// ---------------------------------------------------------------------------------------------
// The guard

interface CacheEntry {
  at: number;
  score: ScoreResponse;
}

export interface TrustScoreGuard {
  /**
   * Registers the check on the client. Pass the x402HTTPClient as well so the guard looks up the
   * URL the agent actually called rather than the one the server declares.
   */
  attach(client: x402Client, http?: x402HTTPClient): void;
  /**
   * Wraps the fetch the x402 transport uses, to rate each paid request after it completes.
   * Returns `fetch` unchanged when rating is off. Put it under the payment wrapper:
   * `wrapFetchWithPayment(guard.wrapFetch(fetch), http)`.
   */
  wrapFetch(fetch?: typeof globalThis.fetch): typeof globalThis.fetch;
  /** The check the hook runs, for callers that drive payments themselves. */
  check(paymentRequired: PaymentRequired, requirements: PaymentRequirements, requestUrl?: string): Promise<Decision>;
}

export function trustScoreGuard(options: GuardOptions = {}): TrustScoreGuard {
  const apiUrl = (options.apiUrl ?? DEFAULT_API_URL).replace(/\/+$/, "");
  const audience = new URL(apiUrl).host.toLowerCase();
  const registryFetch = options.fetch ?? globalThis.fetch;
  const timeoutMs = options.timeoutMs ?? 1500;
  const cacheTtlMs = options.cacheTtlMs ?? 60_000;
  const cache = new Map<string, CacheEntry>();
  // The x402HTTPClient hands over the request URL with the same PaymentRequired object that later
  // reaches onBeforePaymentCreation; the WeakMap links the two without holding either alive.
  const requestUrls = new WeakMap<object, string>();

  const rateOptions = options.rate === true ? {} : options.rate || null;
  let identity: AgentIdentity | null = null;

  async function lookup(url: string): Promise<ScoreResponse | null> {
    const cached = cache.get(url);
    if (cached && Date.now() - cached.at < cacheTtlMs) return cached.score;
    try {
      const response = await registryFetch(`${apiUrl}/v1/score?service=${encodeURIComponent(url)}`, {
        headers: { "User-Agent": USER_AGENT, Accept: "application/json" },
        signal: AbortSignal.timeout(timeoutMs),
      });
      if (!response.ok) return null;
      const score = (await response.json()) as ScoreResponse;
      if (typeof score?.score !== "number") return null;
      cache.set(url, { at: Date.now(), score });
      if (cache.size > 1000) cache.delete(cache.keys().next().value!);
      return score;
    } catch {
      return null;
    }
  }

  async function check(
    paymentRequired: PaymentRequired,
    requirements: PaymentRequirements,
    requestUrl?: string,
  ): Promise<Decision> {
    const warnings: string[] = [];
    // v2 declares the resource once; v1 puts it on each requirement.
    const declared =
      paymentRequired.resource?.url ?? (requirements as PaymentRequirements & { resource?: string }).resource;
    const fromRequest = requestUrl ? endpointUrl(requestUrl) : null;
    const url = fromRequest ?? (declared ? endpointUrl(declared) : null);
    const urlSource = fromRequest ? "request" : url ? "declared" : null;

    const requestHost = hostOf(fromRequest);
    const declaredHost = hostOf(declared);
    if (requestHost && declaredHost && requestHost !== declaredHost)
      warnings.push(`the 402 declares ${declaredHost} as the resource but the request went to ${requestHost}`);

    const score = url ? await lookup(url) : null;
    const decision = decide({ url, urlSource, requirements, score, warnings }, options);

    if (options.onDecision) {
      try {
        const override = await options.onDecision(decision);
        if (override === "allow" || override === "block") decision.action = override;
      } catch {
        // A failing callback must not change the payment.
      }
    }
    return decision;
  }

  function attach(client: x402Client, http?: x402HTTPClient): void {
    http?.onPaymentRequired(async ({ paymentRequired, requestUrl }) => {
      if (requestUrl) requestUrls.set(paymentRequired, requestUrl);
    });
    client.onBeforePaymentCreation(async ({ paymentRequired, selectedRequirements }) => {
      const decision = await check(paymentRequired, selectedRequirements, requestUrls.get(paymentRequired));
      if (decision.action === "block") return { abort: true, reason: `TrustScore: ${decision.reason}` };
    });
  }

  function settled(response: Response): boolean | undefined {
    const header = response.headers.get("PAYMENT-RESPONSE") ?? response.headers.get("X-PAYMENT-RESPONSE");
    if (!header) return undefined;
    try {
      const decoded = JSON.parse(Buffer.from(header, "base64").toString("utf-8")) as { success?: unknown };
      return typeof decoded.success === "boolean" ? decoded.success : undefined;
    } catch {
      return undefined;
    }
  }

  async function submitRating(service: string, response: Response, latencyMs: number): Promise<void> {
    const result: RatingResult = {
      service,
      statusCode: response.status,
      latencyMs,
      accepted: false,
      ratingId: null,
      agentIdentity: null,
      error: null,
    };
    try {
      identity ??= loadIdentity(rateOptions ?? undefined);
      const size = Number(response.headers.get("Content-Length"));
      const settlement = settled(response);
      const receipt = response.headers.get("X-Trust-Receipt");
      const body = JSON.stringify({
        service,
        metrics: {
          status_code: response.status,
          latency_ms: latencyMs,
          ...(Number.isFinite(size) && size >= 0 && response.headers.has("Content-Length") ? { response_size_bytes: size } : {}),
          ...(settlement !== undefined ? { schema_valid: settlement } : {}),
        },
        ...(receipt ? { receipt } : {}),
      });
      const rated = await registryFetch(`${apiUrl}/v1/rate`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "User-Agent": USER_AGENT,
          ...signRequest(identity, audience, "POST", "/v1/rate", body),
        },
        body,
        signal: AbortSignal.timeout(5000),
      });
      const json = (await rated.json().catch(() => ({}))) as {
        accepted?: boolean;
        rating_id?: string;
        agent_identity?: string;
        message?: string;
      };
      result.accepted = rated.ok && json.accepted === true;
      result.ratingId = json.rating_id ?? null;
      result.agentIdentity = json.agent_identity ?? null;
      if (!rated.ok) result.error = `HTTP ${rated.status}${json.message ? `: ${json.message}` : ""}`;
    } catch (error) {
      result.error = error instanceof Error ? error.message : String(error);
    }
    try {
      options.onRating?.(result);
    } catch {
      // Ignore callback failures.
    }
  }

  function wrapFetch(fetch: typeof globalThis.fetch = globalThis.fetch): typeof globalThis.fetch {
    if (!rateOptions) return fetch;
    return async (input, init) => {
      const headers = input instanceof Request && !init?.headers ? input.headers : new Headers(init?.headers);
      const paid = headers.has("PAYMENT-SIGNATURE") || headers.has("X-PAYMENT");
      if (!paid) return fetch(input, init);

      const target = endpointUrl(input instanceof Request ? input.url : String(input));
      const started = performance.now();
      const response = await fetch(input, init);
      const latencyMs = Math.min(600_000, Math.max(1, Math.round(performance.now() - started)));

      // Only outcomes the service owns: a 2xx delivered the paid content, a 5xx failed to.
      // A 402 after paying usually means the payment itself was refused (funds, expiry), which
      // says more about the payer than about the service.
      const owned = (response.status >= 200 && response.status < 300) || response.status >= 500;
      if (target && owned) void submitRating(target, response, latencyMs);
      return response;
    };
  }

  return { attach, wrapFetch, check };
}
