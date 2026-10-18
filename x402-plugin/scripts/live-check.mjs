// Live check against a real x402 endpoint and the production registry. Nothing is paid: the
// payment scheme below refuses to sign, after the guard has decided. Run: npm run live-check
import assert from "node:assert/strict";
import { x402Client, x402HTTPClient } from "@x402/core/client";
import { wrapFetchWithPayment } from "@x402/fetch";
import { trustScoreGuard } from "../dist/index.js";

const ENDPOINT = process.argv[2] ?? "https://www.api-xpay.com/x402/ping";
const API = process.env.TRUSTSCORE_API_URL ?? "https://api.trustscoreagent.com";

class NotPaying extends Error {}
const refuseToSign = {
  scheme: "exact",
  createPaymentPayload: async () => {
    throw new NotPaying("live check: not paying");
  },
};

async function attempt(label, rewritePayTo) {
  const client = new x402Client();
  client.register("eip155:8453", refuseToSign);
  client.setSpendControls(false);
  // Simulates a compromised server by swapping the payee in the challenge it sent.
  if (rewritePayTo) client.registerPolicy((_, reqs) => reqs.map((r) => ({ ...r, payTo: rewritePayTo })));
  const http = new x402HTTPClient(client);
  let decision;
  trustScoreGuard({ apiUrl: API, onDecision: (d) => void (decision = d) }).attach(client, http);

  const error = await wrapFetchWithPayment(fetch, http)(ENDPOINT).then(
    () => null,
    (e) => e,
  );
  assert.ok(decision, `${label}: the guard was not consulted`);
  console.log(`${label}: ${decision.action} (${decision.code}, payee ${decision.payToCheck}) ${decision.reason}`);
  for (const w of decision.warnings) console.log(`  warning: ${w}`);
  return { decision, error };
}

const genuine = await attempt("genuine payee");
assert.equal(genuine.decision.urlSource, "request");
assert.equal(genuine.decision.action, "allow");
assert.match(String(genuine.error?.message), /not paying/, "the payment should have reached the scheme");

const swapped = await attempt("swapped payee", "0x000000000000000000000000000000000000dEaD");
if (genuine.decision.payToCheck === "match") {
  assert.equal(swapped.decision.code, "pay_to_mismatch");
  assert.match(String(swapped.error?.message), /TrustScore: payee/);
} else {
  console.log(`(payee check is ${genuine.decision.payToCheck} on ${ENDPOINT}: the swap cannot be detected there)`);
}
console.log("live check passed");
