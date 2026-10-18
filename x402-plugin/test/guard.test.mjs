// Runs the guard inside the real x402 SDK (x402Client, x402HTTPClient, wrapFetchWithPayment)
// against an in-memory paid endpoint and an in-memory registry. Nothing is paid, nothing leaves
// the process.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { createPublicKey, verify, createHash } from "node:crypto";
import { x402Client, x402HTTPClient } from "@x402/core/client";
import { encodePaymentRequiredHeader, encodePaymentResponseHeader } from "@x402/core/http";
import { wrapFetchWithPayment } from "@x402/fetch";
import { trustScoreGuard, decide, endpointUrl, VERSION } from "../dist/index.js";

const PAY_TO = "0x589a2314a2E05F45e40C4823Da3Ba58D421dB3d8";
const OTHER = "0x000000000000000000000000000000000000dEaD";
const USDC = "0x833589fCD6eDb6E08f4c7C32D4f71b54bdA02913";
const ENDPOINT = "https://paid.example/x402/ping";
const API = "https://registry.test";

function requirement(overrides = {}) {
  return {
    scheme: "exact",
    network: "eip155:8453",
    asset: USDC,
    amount: "5000",
    payTo: PAY_TO,
    maxTimeoutSeconds: 60,
    extra: {},
    ...overrides,
  };
}

function terms(overrides = {}) {
  return {
    version: 2,
    accepts: [{ scheme: "exact", network: "eip155:8453", asset: USDC, amount: "5000", pay_to: PAY_TO }],
    first_seen: new Date(Date.now() - 86_400_000 * 3).toISOString(),
    last_seen: new Date(Date.now() - 3_600_000).toISOString(),
    observations: 12,
    pay_to_changed_at: null,
    pay_to_changes: 0,
    pay_to_rotates: false,
    price_changed_at: null,
    price_changes: 0,
    price_varies: false,
    ...overrides,
  };
}

function scoreOf(overrides = {}) {
  return { service: "paid.example/x402/ping", score: 0.8, confidence: 0.5, ratings_count: 40, known: true, provider: null, x402: terms(), ...overrides };
}

// A paid endpoint: 402 with a v2 challenge, then 200 with a settlement header once paid.
function paidServer({ payTo = PAY_TO, declaredUrl = ENDPOINT, paidStatus = 200 } = {}) {
  const calls = [];
  const fetch = async (input, init) => {
    const request = input instanceof Request ? input : new Request(input, init);
    calls.push(request);
    if (!request.headers.has("PAYMENT-SIGNATURE")) {
      const challenge = { x402Version: 2, resource: { url: declaredUrl }, accepts: [requirement({ payTo })] };
      return new Response("{}", { status: 402, headers: { "PAYMENT-REQUIRED": encodePaymentRequiredHeader(challenge) } });
    }
    const settle = encodePaymentResponseHeader({ success: true, transaction: "0xabc", network: "eip155:8453", payer: "0x1" });
    return new Response("pong", { status: paidStatus, headers: { "PAYMENT-RESPONSE": settle, "Content-Length": "4" } });
  };
  return { fetch, calls };
}

// The registry: GET /v1/score answers `score`, POST /v1/rate records what it receives.
function registry(score) {
  const lookups = [];
  const ratings = [];
  const fetch = async (url, init = {}) => {
    const u = new URL(url);
    if (u.pathname === "/v1/score") {
      lookups.push(u.searchParams.get("service"));
      if (score === "down") throw new Error("connect ECONNREFUSED");
      return Response.json(score);
    }
    if (u.pathname === "/v1/rate") {
      ratings.push({ headers: new Headers(init.headers), body: init.body });
      return Response.json({ accepted: true, rating_id: "r-1", agent_identity: "signed" });
    }
    return new Response("not found", { status: 404 });
  };
  return { fetch, lookups, ratings };
}

// A scheme that "signs" without touching any chain.
const fakeScheme = {
  scheme: "exact",
  createPaymentPayload: async (x402Version) => ({ x402Version, payload: { signature: "0xfake" } }),
};

function setup(score, guardOptions = {}, serverOptions = {}) {
  const server = paidServer(serverOptions);
  const reg = registry(score);
  const client = new x402Client();
  client.register("eip155:8453", fakeScheme);
  client.setSpendControls(false);
  const http = new x402HTTPClient(client);
  const decisions = [];
  const ratingResults = [];
  const guard = trustScoreGuard({
    apiUrl: API,
    fetch: reg.fetch,
    onDecision: (d) => void decisions.push(d),
    onRating: (r) => ratingResults.push(r),
    ...guardOptions,
  });
  guard.attach(client, http);
  const pay = wrapFetchWithPayment(guard.wrapFetch(server.fetch), http);
  return { pay, server, reg, decisions, ratingResults, guard, client };
}

const paidCalls = (server) => server.calls.filter((r) => r.headers.has("PAYMENT-SIGNATURE")).length;
const settle = () => new Promise((resolve) => setTimeout(resolve, 20));

test("the version constant matches package.json", () => {
  const pkg = JSON.parse(readFileSync(new URL("../package.json", import.meta.url), "utf-8"));
  assert.equal(VERSION, pkg.version);
});

test("a known payee is paid, and the lookup uses the requested URL without its query", async () => {
  const { pay, server, reg, decisions } = setup(scoreOf());
  const response = await pay(`${ENDPOINT}?q=secret#frag`);
  assert.equal(response.status, 200);
  assert.equal(paidCalls(server), 1);
  assert.deepEqual(reg.lookups, [ENDPOINT]);
  assert.equal(decisions[0].code, "ok");
  assert.equal(decisions[0].payToCheck, "match");
  assert.equal(decisions[0].urlSource, "request");
});

test("an unknown payee on an endpoint seen recently is not paid", async () => {
  const { pay, server, decisions } = setup(scoreOf(), {}, { payTo: OTHER });
  await assert.rejects(pay(ENDPOINT), /TrustScore: payee 0x0+dEaD is not the one TrustScore saw/);
  assert.equal(paidCalls(server), 0);
  assert.equal(decisions[0].code, "pay_to_mismatch");
  assert.equal(decisions[0].action, "block");
});

test("warn mode reports the mismatch but pays", async () => {
  const { pay, server, decisions } = setup(scoreOf(), { mode: "warn" }, { payTo: OTHER });
  assert.equal((await pay(ENDPOINT)).status, 200);
  assert.equal(paidCalls(server), 1);
  assert.equal(decisions[0].code, "pay_to_mismatch");
  assert.equal(decisions[0].action, "allow");
});

test("onDecision can override a block", async () => {
  const { pay, server } = setup(scoreOf(), { onDecision: () => "allow" }, { payTo: OTHER });
  assert.equal((await pay(ENDPOINT)).status, 200);
  assert.equal(paidCalls(server), 1);
});

test("an unreachable registry never blocks by default, and blocks with failClosed", async () => {
  const open = setup("down");
  assert.equal((await open.pay(ENDPOINT)).status, 200);
  assert.equal(open.decisions[0].code, "registry_unavailable");

  const closed = setup("down", { failClosed: true });
  await assert.rejects(closed.pay(ENDPOINT), /TrustScore: TrustScore could not be reached/);
  assert.equal(paidCalls(closed.server), 0);
});

test("a declared resource on another host is reported, and the lookup still uses the request URL", async () => {
  const { pay, reg, decisions } = setup(scoreOf(), {}, { declaredUrl: "https://reputable.example/api" });
  await pay(ENDPOINT);
  assert.deepEqual(reg.lookups, [ENDPOINT]);
  assert.match(decisions[0].warnings.join("\n"), /declares reputable\.example as the resource but the request went to paid\.example/);
});

test("without the HTTP client the declared URL is used, and said so", async () => {
  const server = paidServer();
  const reg = registry(scoreOf());
  const client = new x402Client();
  client.register("eip155:8453", fakeScheme);
  client.setSpendControls(false);
  const decisions = [];
  trustScoreGuard({ apiUrl: API, fetch: reg.fetch, onDecision: (d) => void decisions.push(d) }).attach(client);
  await wrapFetchWithPayment(server.fetch, client)(ENDPOINT);
  assert.equal(decisions[0].urlSource, "declared");
  assert.deepEqual(reg.lookups, [ENDPOINT]);
});

test("lookups are cached per endpoint", async () => {
  const { pay, reg } = setup(scoreOf());
  await pay(ENDPOINT);
  await pay(ENDPOINT);
  assert.equal(reg.lookups.length, 1);
});

test("no rating is sent unless rate is on, and wrapFetch is then the fetch itself", async () => {
  const { pay, reg, guard } = setup(scoreOf());
  await pay(ENDPOINT);
  await settle();
  assert.equal(reg.ratings.length, 0);
  assert.equal(guard.wrapFetch(globalThis.fetch), globalThis.fetch);
});

test("with rate on, a paid call is rated once, signed, without the query string", async () => {
  const keyFile = join(mkdtempSync(join(tmpdir(), "ts-x402-")), "agent-key.pem");
  const { pay, reg, ratingResults } = setup(scoreOf(), { rate: { keyFile } });
  await pay(`${ENDPOINT}?q=secret`);
  await settle();

  assert.equal(reg.ratings.length, 1);
  const { headers, body } = reg.ratings[0];
  const sent = JSON.parse(body);
  assert.equal(sent.service, ENDPOINT);
  assert.equal(sent.metrics.status_code, 200);
  assert.equal(sent.metrics.schema_valid, true);
  assert.equal(sent.metrics.response_size_bytes, 4);
  assert.ok(sent.metrics.latency_ms >= 1);
  assert.deepEqual(ratingResults.map((r) => [r.ratingId, r.agentIdentity]), [["r-1", "signed"]]);

  // The signature verifies against the DID's key, over the canonical string the API rebuilds.
  const did = headers.get("X-Agent-DID");
  assert.match(did, /^did:key:z6Mk/);
  const canonical = [
    "trustscore-v1", "registry.test", "POST", "/v1/rate", did,
    headers.get("X-Agent-Timestamp"), headers.get("X-Agent-Nonce"),
    createHash("sha256").update(body).digest("hex"),
  ].join("\n");
  const publicKey = createPublicKey(readFileSync(keyFile, "utf-8"));
  const signature = Buffer.from(headers.get("X-Agent-Signature"), "base64url");
  assert.ok(verify(null, Buffer.from(canonical), publicKey, signature));
});

test("a 402 after paying is not rated: it is usually the payer's failure", async () => {
  const keyFile = join(mkdtempSync(join(tmpdir(), "ts-x402-")), "agent-key.pem");
  const { pay, reg } = setup(scoreOf(), { rate: { keyFile } }, { paidStatus: 402 });
  await pay(ENDPOINT).catch(() => {});
  await settle();
  assert.equal(reg.ratings.length, 0);
});

// ----------------------------------------------------------------------------- policy only

const input = (score, overrides = {}) => ({ url: ENDPOINT, urlSource: "request", requirements: requirement(), score, ...overrides });

test("a rotating payee is never treated as a mismatch", () => {
  const d = decide(input(scoreOf({ x402: terms({ pay_to_rotates: true }) }), { requirements: requirement({ payTo: OTHER }) }));
  assert.equal(d.payToCheck, "rotating");
  assert.equal(d.action, "allow");
});

test("a mismatch on terms older than maxTermsAgeHours only warns", () => {
  const old = terms({ last_seen: new Date(Date.now() - 48 * 3_600_000).toISOString() });
  const d = decide(input(scoreOf({ x402: old }), { requirements: requirement({ payTo: OTHER }) }));
  assert.equal(d.code, "ok");
  assert.equal(d.action, "allow");
  assert.match(d.warnings.join(), /too long ago to block/);
});

test("the score alone never blocks unless minScore is set", () => {
  assert.equal(decide(input(scoreOf({ score: 0.1 }))).action, "allow");
  assert.equal(decide(input(scoreOf({ score: 0.1 })), { minScore: 0.4 }).code, "low_score");
});

test("minScore spares endpoints with few ratings and unknown ones", () => {
  assert.equal(decide(input(scoreOf({ score: 0.1, ratings_count: 3 })), { minScore: 0.4 }).action, "allow");
  const unknown = decide(input(scoreOf({ score: 0.5, known: false, ratings_count: 0, x402: null, provider: { score: 0.9, ratings_count: 30 } })), { minScore: 0.6 });
  assert.equal(unknown.action, "allow");
  assert.equal(unknown.payToCheck, "no_data");
  assert.match(unknown.warnings.join(), /no rating .* its domain scores 0\.9 over 30/);
});

test("a price never seen is reported, in v1 terms too", () => {
  const v1 = { ...requirement({ amount: undefined }), maxAmountRequired: "9000" };
  const d = decide(input(scoreOf(), { requirements: v1 }));
  assert.equal(d.action, "allow");
  assert.equal(d.amount, "9000");
  assert.match(d.warnings.join(), /price asked \(9000/);
});

test("endpointUrl keeps scheme, host and path only", () => {
  assert.equal(endpointUrl("https://A.example/p/q?x=1#y"), "https://a.example/p/q");
  assert.equal(endpointUrl("ftp://a.example/x"), null);
  assert.equal(endpointUrl("not a url"), null);
});
