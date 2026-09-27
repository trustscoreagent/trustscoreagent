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
const list = await call("list_services", { limit: 3 });
console.log("list         :", list.count, list.services.map((s) => `${s.service}=${s.score}`).join(", "));
const rated = await call("submit_rating", { service_did: "api.open-meteo.com", status_code: 200, latency_ms: 120 });
console.log("submit       :", rated.accepted, rated.agent_identity, rated.rating_id);

await client.close();
console.log("All structured results matched their output schemas.");
