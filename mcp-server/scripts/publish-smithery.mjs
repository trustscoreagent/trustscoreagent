// Publishes the MCP server's .mcpb bundle to Smithery (trustscoreagent/trustscoreagent).
//
// Why a script: Smithery's web form only accepts HTTP servers, and `smithery mcp publish` fails
// with "400 expected object" because it builds the server card from manifest.json, whose tools
// carry no inputSchema. This starts the bundled server, takes the real tools/list (with
// schemas), and PUTs bundle + card to the Smithery API, as done for 0.1.1 and 0.2.3.
//
//   gh release download vX.Y.Z -p "trustscoreagent-X.Y.Z.mcpb"
//   node scripts/publish-smithery.mjs trustscoreagent-X.Y.Z.mcpb            # dry run: builds payload.json
//   node scripts/publish-smithery.mjs trustscoreagent-X.Y.Z.mcpb --publish  # uploads
//
// The API key is read from SMITHERY_API_KEY or from the Smithery CLI's settings
// (%APPDATA%/smithery/settings.json after `smithery auth login`). It is never printed.
import { spawn, execFileSync } from "node:child_process";
import { readFileSync, writeFileSync, mkdtempSync, existsSync } from "node:fs";
import { join, resolve } from "node:path";
import { tmpdir, homedir } from "node:os";

const [bundlePath, flag] = process.argv.slice(2);
if (!bundlePath || !existsSync(bundlePath)) {
  console.error("usage: node scripts/publish-smithery.mjs <bundle.mcpb> [--publish]");
  process.exit(2);
}
const here = mkdtempSync(join(tmpdir(), "smithery-"));
const bundleDir = join(here, "bundle");
// .mcpb is a zip archive.
execFileSync("python", ["-c", "import sys,zipfile;zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])",
  resolve(bundlePath), bundleDir]);
const manifest = JSON.parse(readFileSync(join(bundleDir, "manifest.json"), "utf8"));

const child = spawn(process.execPath, [join(bundleDir, "dist", "index.js")], {
  cwd: bundleDir,
  // Throwaway identity: this run only lists tools, it must not create a key in the real home.
  env: { ...process.env, HOME: join(here, "home"), USERPROFILE: join(here, "home") },
  stdio: ["pipe", "pipe", "inherit"],
});

let buffer = "";
const pending = new Map();
child.stdout.on("data", (chunk) => {
  buffer += chunk;
  let i;
  while ((i = buffer.indexOf("\n")) >= 0) {
    const line = buffer.slice(0, i).trim();
    buffer = buffer.slice(i + 1);
    if (!line) continue;
    const msg = JSON.parse(line);
    pending.get(msg.id)?.(msg);
  }
});
const send = (id, method, params) =>
  new Promise((resolve) => {
    pending.set(id, resolve);
    child.stdin.write(JSON.stringify({ jsonrpc: "2.0", id, method, params }) + "\n");
  });

const init = await send(1, "initialize", {
  protocolVersion: "2025-06-18",
  capabilities: {},
  clientInfo: { name: "smithery-payload-builder", version: "1" },
});
child.stdin.write(JSON.stringify({ jsonrpc: "2.0", method: "notifications/initialized" }) + "\n");
const list = await send(2, "tools/list", {});
child.kill();

const serverInfo = init.result.serverInfo;
if (serverInfo.version !== manifest.version)
  throw new Error(`bundle reports ${serverInfo.version}, manifest says ${manifest.version}`);

const configSchema = {
  type: "object",
  properties: Object.fromEntries(
    Object.entries(manifest.user_config).map(([key, c], order) => [
      key,
      { type: c.type, title: c.title, description: c.description, ...(c.default ? { default: c.default } : {}), "x-order": order },
    ]),
  ),
  required: [],
};

const payload = {
  type: "stdio",
  runtime: "node",
  serverCard: {
    serverInfo: {
      name: serverInfo.name,
      version: serverInfo.version,
      description: manifest.description,
      websiteUrl: manifest.homepage,
    },
    tools: list.result.tools,
  },
  configSchema,
};
writeFileSync("payload.json", JSON.stringify(payload, null, 2));
console.log(`server ${serverInfo.name} ${serverInfo.version}, ${payload.serverCard.tools.length} tools:`);
for (const t of payload.serverCard.tools)
  console.log(`  ${t.name}: inputSchema ${t.inputSchema ? "yes" : "NO"} | ${t.description.slice(0, 70)}...`);
console.log("config:", Object.keys(configSchema.properties).join(", "));

if (flag !== "--publish") {
  console.log("Dry run: payload.json written. Re-run with --publish to upload.");
  process.exit(0);
}

const settings = join(process.env.APPDATA ?? join(homedir(), ".config"), "smithery", "settings.json");
const key = process.env.SMITHERY_API_KEY ?? (existsSync(settings) ? JSON.parse(readFileSync(settings, "utf8")).apiKey : undefined);
if (!key) throw new Error("No Smithery API key: set SMITHERY_API_KEY or run `smithery auth login`");

const form = new FormData();
form.append("payload", new Blob([JSON.stringify(payload)], { type: "application/json" }));
form.append("bundle", new Blob([readFileSync(bundlePath)]), "server.mcpb");
const res = await fetch("https://api.smithery.ai/servers/trustscoreagent%2Ftrustscoreagent/releases", {
  method: "PUT",
  headers: { Authorization: `Bearer ${key}` },
  body: form,
});
const body = await res.json().catch(() => ({}));
console.log(`HTTP ${res.status}: status ${body.status ?? "?"}, release ${body.deploymentId ?? "?"}`);
if (!res.ok || body.status !== "SUCCESS") process.exit(1);
console.log("Published. The public registry caches listings for up to 4 hours.");
