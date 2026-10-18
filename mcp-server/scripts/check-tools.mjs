// End-to-end check of the built server with the official MCP client, which validates every
// structuredContent against the tool's outputSchema and throws when they disagree.
//
//   npm run build && TRUSTSCORE_API_URL=<staging url> node scripts/check-tools.mjs
//
// Point it at staging: submit_rating writes a real rating. Uses a throwaway HOME so the run does
// not create or reuse the machine's agent key.
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const api = process.env.TRUSTSCORE_API_URL;
// Compare the parsed host exactly: a substring test would let "api.trustscoreagent.com.evil.net"
// through, or refuse a staging URL that merely mentions the production name.
const host = (() => {
  try {
    return new URL(api ?? "").hostname.toLowerCase();
  } catch {
    return "";
  }
})();
if (!host || host === "api.trustscoreagent.com" || host === "trustscoreagent.com") {
  console.error("Set TRUSTSCORE_API_URL to a staging (non-production) registry.");
  process.exit(2);
}
const home = mkdtempSync(join(tmpdir(), "tsa-mcp-"));
const transport = new StdioClientTransport({
  command: process.execPath,
  args: ["dist/index.js"],
  env: { ...process.env, TRUSTSCORE_API_URL: api, HOME: home, USERPROFILE: home },
});
const client = new Client({ name: "check-tools", version: "1" });
await client.connect(transport);

const { tools } = await client.listTools();
for (const t of tools)
  console.log(`${t.name}: annotations=${JSON.stringify(t.annotations)} outputSchema=${t.outputSchema ? "yes" : "NO"}`);

const call = async (name, args) => {
  const r = await client.callTool({ name, arguments: args });
  if (r.isError) throw new Error(`${name} returned an error: ${JSON.stringify(r.content)}`);
  if (!r.structuredContent) throw new Error(`${name} returned no structuredContent`);
  return r.structuredContent;
};

const known = await call("check_reputation", { service_did: "api.open-meteo.com" });
console.log("check known  :", known.trust_level, known.score, known.ratings_count);
const unknown = await call("check_reputation", { service_did: `never-seen-${Date.now()}.example.net` });
console.log("check unknown:", unknown.trust_level, unknown.known);
// A paid endpoint the probe records: its own payee matches, any other one does not.
const paid = "https://www.api-xpay.com/x402/ping";
const terms = await call("check_reputation", { service_did: paid });
if (!terms.x402?.accepts?.length) throw new Error(`no x402 terms on ${paid}`);
const payee = terms.x402.accepts[0].pay_to;
const match = await call("check_reputation", { service_did: paid, pay_to: payee.toUpperCase().replace("0X", "0x") });
const mismatch = await call("check_reputation", { service_did: paid, pay_to: "0x000000000000000000000000000000000000dead" });
console.log("x402 pay_to  :", match.pay_to_check, mismatch.pay_to_check);
if (match.pay_to_check !== "match" || mismatch.pay_to_check !== "mismatch")
  throw new Error("pay_to comparison is wrong");
const sibling = await call("check_reputation", { service_did: "https://www.api-xpay.com/x402/never-rated" });
console.log("provider hint:", sibling.known, sibling.provider?.service, sibling.provider?.endpoints_rated);

const list = await call("list_services", { limit: 3 });
console.log("list         :", list.count, "of", list.total, list.services.map((s) => `${s.service}=${s.score}`).join(", "));
if (!(list.total > list.count)) throw new Error("list_services should report a total beyond the page");
const paidList = await call("list_services", { x402: true, limit: 2, offset: 1, sort_by: "last_rated" });
console.log("list x402    :", paidList.count, "of", paidList.total, "from", paidList.offset, paidList.services.map((s) => s.service).join(", "));
if (paidList.offset !== 1 || !(paidList.total < list.total)) throw new Error("x402 filter or offset ignored");
const rated = await call("submit_rating", { service_did: "api.open-meteo.com", status_code: 200, latency_ms: 120 });
console.log("submit       :", rated.accepted, rated.agent_identity, rated.rating_id);

await client.close();
console.log("All structured results matched their output schemas.");
