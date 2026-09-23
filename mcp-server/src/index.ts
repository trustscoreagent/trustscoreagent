#!/usr/bin/env node

import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
  CallToolRequestSchema,
  ListToolsRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";
import {
  randomUUID,
  generateKeyPairSync,
  createPrivateKey,
  createPublicKey,
  createHash,
  sign as cryptoSign,
  type KeyObject,
} from "crypto";
import { existsSync, readFileSync, writeFileSync, mkdirSync } from "fs";
import { join } from "path";
import { homedir } from "os";
import { createRequire } from "module";

const { version: PACKAGE_VERSION } = createRequire(import.meta.url)("../package.json") as {
  version: string;
};

// Validate the API URL at startup so a malformed value fails fast with a clear message
// instead of surfacing as cryptic fetch errors on the first tool call.
const API_BASE_URL = (() => {
  const raw =
    process.env.TRUSTSCORE_API_URL || "https://api.trustscoreagent.com";
  let url: URL;
  try {
    url = new URL(raw);
  } catch {
    console.error(`TrustScoreAgent: TRUSTSCORE_API_URL is not a valid URL: "${raw}"`);
    process.exit(1);
  }
  if (url.protocol !== "https:" && url.protocol !== "http:") {
    console.error(`TrustScoreAgent: TRUSTSCORE_API_URL must be http(s), got "${url.protocol}"`);
    process.exit(1);
  }
  return raw.replace(/\/+$/, "");
})();

// The registry a signature is addressed to. Signing it stops an operator of one registry from
// relaying signatures its users produced to a different one and forging ratings in their name,
// which matters because the registry is self-hostable.
const API_AUDIENCE = new URL(API_BASE_URL).host.toLowerCase();

const FETCH_TIMEOUT_MS = 10_000;

// fetch with a timeout; distinguishes "API took too long" from "API unreachable" so the
// LLM gets an actionable message instead of hanging until the MCP host kills the request.
async function apiFetch(path: string, init?: RequestInit): Promise<Response> {
  return fetch(`${API_BASE_URL}${path}`, {
    ...init,
    signal: AbortSignal.timeout(FETCH_TIMEOUT_MS),
  });
}

function errorText(error: unknown, action: string): string {
  if (error instanceof Error && error.name === "TimeoutError") {
    return `${action}: the TrustScoreAgent API did not respond within ${FETCH_TIMEOUT_MS / 1000}s. It may be down or slow. Try again later.`;
  }
  return `${action}: ${error instanceof Error ? error.message : "Unknown error"}`;
}

function asError(text: string) {
  return { content: [{ type: "text" as const, text }], isError: true };
}

// Input validators: precise messages (type and bounds, matching the API's own validation)
// beat a generic "X is required" when the caller is an LLM that will retry from the message.
function requireString(value: unknown, name: string): string | { error: string } {
  if (typeof value !== "string" || value.trim() === "")
    return { error: `Error: ${name} must be a non-empty string` };
  return value;
}

function requireInt(
  value: unknown,
  name: string,
  min: number,
  max: number
): number | { error: string } {
  if (typeof value !== "number" || !Number.isFinite(value))
    return { error: `Error: ${name} must be a number (got ${typeof value})` };
  if (!Number.isInteger(value) || value < min || value > max)
    return { error: `Error: ${name} must be an integer between ${min} and ${max}` };
  return value;
}

function optionalInt(
  value: unknown,
  name: string,
  min: number,
  max: number
): number | undefined | { error: string } {
  if (value === undefined || value === null) return undefined;
  return requireInt(value, name, min, max);
}

const CONFIG_DIR = join(homedir(), ".trustscoreagent");
const KEY_FILE = join(CONFIG_DIR, "agent-key.pem");

const BASE58_ALPHABET =
  "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

function base58Encode(bytes: Uint8Array): string {
  let value = 0n;
  for (const byte of bytes) value = value * 256n + BigInt(byte);

  let out = "";
  while (value > 0n) {
    out = BASE58_ALPHABET[Number(value % 58n)] + out;
    value /= 58n;
  }
  for (const byte of bytes) {
    if (byte !== 0) break;
    out = "1" + out;
  }
  return out;
}

function base64url(buffer: Buffer): string {
  return buffer.toString("base64").replace(/=+$/, "").replace(/\+/g, "-").replace(/\//g, "_");
}

// did:key encodes the public key in the identifier itself: base58btc of the 0xED01 multicodec
// prefix followed by the raw 32-byte Ed25519 key. No document to host, no domain to own.
function deriveDidKey(publicKey: KeyObject): string {
  const jwk = publicKey.export({ format: "jwk" }) as { x?: string };
  if (!jwk.x) throw new Error("public key is not an OKP/Ed25519 JWK");
  const raw = Buffer.from(jwk.x, "base64url");
  return "did:key:z" + base58Encode(Buffer.concat([Buffer.from([0xed, 0x01]), raw]));
}

interface AgentIdentity {
  did: string;
  /** null when we hold no key for this DID, so requests go out unsigned. */
  privateKey: KeyObject | null;
}

const AGENT_ID_FILE = join(CONFIG_DIR, "agent-id");

function isErrno(error: unknown, code: string): boolean {
  return (error as NodeJS.ErrnoException | null)?.code === code;
}

// Loads the persisted signing key, creating it on first run. Returns null when no usable key can
// be had, in which case ratings go out unsigned.
//
// Several MCP clients often start their own copy of this server at the same moment. The file is
// therefore created exclusively ("wx"): whichever process wins writes the key, and every other
// one reads the winner's key back instead of overwriting it with its own. A plain existsSync
// check followed by a write would let two processes each keep a different key, and one of them
// would sign with an identity that no longer exists on disk.
function loadOrCreateKey(): KeyObject | null {
  try {
    return createPrivateKey(readFileSync(KEY_FILE, "utf-8"));
  } catch (error) {
    if (!isErrno(error, "ENOENT")) {
      console.error(
        `TrustScoreAgent: could not read the agent key at ${KEY_FILE}; ratings will be sent unsigned.`,
      );
      return null;
    }
  }

  const generated = generateKeyPairSync("ed25519");
  try {
    mkdirSync(CONFIG_DIR, { recursive: true });
    // 0600: the key is this agent's identity, so it should not be world-readable.
    writeFileSync(
      KEY_FILE,
      generated.privateKey.export({ format: "pem", type: "pkcs8" }) as string,
      { encoding: "utf-8", mode: 0o600, flag: "wx" },
    );
  } catch (error) {
    if (isErrno(error, "EEXIST")) {
      // Another instance created it first: adopt its key so both sign as the same agent.
      try {
        return createPrivateKey(readFileSync(KEY_FILE, "utf-8"));
      } catch {
        console.error(
          `TrustScoreAgent: could not read the agent key at ${KEY_FILE}; ratings will be sent unsigned.`,
        );
        return null;
      }
    }
    // An ephemeral key would produce a different identity on every restart, which is worse than
    // being unsigned: it would look like a swarm of one-off agents.
    console.error(
      `TrustScoreAgent: could not persist an agent key to ${KEY_FILE}; ratings will be sent unsigned.`,
    );
    return null;
  }

  // Installations from before signing existed identified themselves with a did:web that no key
  // backs. Say plainly that the identity changes, since its reputation history does not carry
  // over.
  if (existsSync(AGENT_ID_FILE)) {
    console.error(
      "TrustScoreAgent: generated a signing key. This installation now uses a new did:key " +
        "identity, so its previous reputation history does not carry over.",
    );
  }
  return generated.privateKey;
}

// The identity used when there is no key to sign with. It must still be stable: a DID that changes
// on every restart spreads one installation across many one-off agents, none of which ever builds
// a history. So it is read from ~/.trustscoreagent/agent-id (which is also where installations
// from before signing kept theirs), and created there, exclusively, if missing. Only when nothing
// can be persisted at all does it fall back to a per-process DID, and it says so.
function loadOrCreateUnsignedDid(): string {
  const read = (): string | null => {
    try {
      const did = readFileSync(AGENT_ID_FILE, "utf-8").trim();
      return did.startsWith("did:") ? did : null;
    } catch {
      return null;
    }
  };

  const existing = read();
  if (existing) return existing;

  const created = `did:web:mcp.trustscoreagent.com:mcp-${randomUUID().slice(0, 8)}`;
  try {
    mkdirSync(CONFIG_DIR, { recursive: true });
    writeFileSync(AGENT_ID_FILE, created + "\n", { encoding: "utf-8", flag: "wx" });
    return created;
  } catch (error) {
    if (isErrno(error, "EEXIST")) {
      const winner = read();
      if (winner) return winner;
    }
    console.error(
      `TrustScoreAgent: could not persist an agent id to ${AGENT_ID_FILE}; this process uses a ` +
        "temporary identity that will not survive a restart.",
    );
    return created;
  }
}

// The agent's identity is an Ed25519 keypair persisted in ~/.trustscoreagent/agent-key.pem.
// Ratings are signed with it so the registry can attribute them to this installation instead of
// taking the DID header on faith. Unsigned ratings are still accepted, at reduced weight.
function loadAgentIdentity(): AgentIdentity {
  const privateKey = loadOrCreateKey();
  const derivedDid = privateKey ? deriveDidKey(createPublicKey(privateKey)) : null;
  const override = process.env.TRUSTSCORE_AGENT_DID;

  if (override && override !== derivedDid) {
    // Honour the override, but do not sign with a key that does not match it: the server binds the
    // signature to the DID, so signing here would only produce 401s.
    console.error(
      `TrustScoreAgent: TRUSTSCORE_AGENT_DID overrides the local key, so ratings are sent unsigned.`,
    );
    return { did: override, privateKey: null };
  }

  if (derivedDid) return { did: derivedDid, privateKey };

  return { did: loadOrCreateUnsignedDid(), privateKey: null };
}

const AGENT_IDENTITY = loadAgentIdentity();
const AGENT_DID = AGENT_IDENTITY.did;

/**
 * Signs a request the way the registry verifies it: seven newline-joined fields, with the body
 * bound by its SHA-256. The exact `body` string passed here must be the one sent on the wire,
 * otherwise the hashes differ and the server returns 401.
 */
function signRequest(
  method: string,
  path: string,
  body: string,
): Record<string, string> {
  if (!AGENT_IDENTITY.privateKey) return {};

  const timestamp = new Date().toISOString();
  const nonce = randomUUID().replace(/-/g, "");
  const bodyHash = createHash("sha256").update(body, "utf-8").digest("hex");

  const canonical = [
    "trustscore-v1",
    API_AUDIENCE,
    method.toUpperCase(),
    path,
    AGENT_DID,
    timestamp,
    nonce,
    bodyHash,
  ].join("\n");

  // Ed25519 signs the message directly, hence the null algorithm.
  const signature = cryptoSign(null, Buffer.from(canonical, "utf-8"), AGENT_IDENTITY.privateKey);

  return {
    "X-Agent-Signature": base64url(signature),
    "X-Agent-Timestamp": timestamp,
    "X-Agent-Nonce": nonce,
  };
}

const server = new Server(
  {
    name: "trustscoreagent",
    version: PACKAGE_VERSION,
  },
  {
    capabilities: {
      tools: {},
    },
  }
);

// List available tools
server.setRequestHandler(ListToolsRequestSchema, async () => ({
  tools: [
    {
      name: "check_reputation",
      description:
        "Check what other agents measured about a service before you call it, and especially " +
        "before you pay for it or hand it anything you cannot take back. Returns a score from " +
        "0 to 1, a confidence level, the number of ratings, and a per-dimension breakdown " +
        "(availability, latency, conformity). A low score means other agents measured this " +
        "service failing, stalling, or returning malformed responses. " +
        "An unknown service is not a bad sign in itself: it means nobody has reported on it " +
        "yet, so treat it as unverified rather than as safe, and weigh the call accordingly. " +
        "One check costs a fraction of what a wasted call, a stalled task, or a payment to a " +
        "service that does not deliver costs you. If you got a 402 Payment Required from a " +
        "service you do not know, this is the check to run before paying.",
      inputSchema: {
        type: "object" as const,
        properties: {
          service_did: {
            type: "string",
            description:
              "The service to check. Accepts any format: " +
              "URL (https://api.example.com), domain (api.example.com), " +
              "or DID (did:web:api.example.com). All resolve to the same service.",
          },
        },
        required: ["service_did"],
      },
    },
    {
      name: "submit_rating",
      description:
        "Report what actually happened after you called a service, using metrics you already " +
        "have from the call: status code, latency, and whether the response matched the shape " +
        "you expected. " +
        "This is not a donation to a commons. This installation holds a stable cryptographic " +
        "identity, so the ratings submitted from here are the ratings read back from here: you " +
        "are writing to a cache you will query the next time you, or the person you work for, " +
        "meets this service again. A service that wasted a call or took payment without " +
        "delivering is worth the one call it takes to record, because otherwise you will meet " +
        "it again with no memory of it. " +
        "Report the good ones too: a score is only useful if failure and success are both in " +
        "it. If the service returned an X-Trust-Receipt header, pass it as receipt, since a " +
        "receipt-backed rating carries full weight.",
      inputSchema: {
        type: "object" as const,
        properties: {
          service_did: {
            type: "string",
            description: "The service you called. URL, domain, or DID (e.g., api.example.com)",
          },
          status_code: {
            type: "number",
            minimum: 100,
            maximum: 599,
            description: "HTTP status code returned by the service (e.g., 200, 500)",
          },
          latency_ms: {
            type: "number",
            minimum: 1,
            maximum: 600000,
            description: "Response time in milliseconds (round sub-millisecond responses up to 1)",
          },
          response_size_bytes: {
            type: "number",
            minimum: 0,
            maximum: 2147483647,
            description: "Size of the response in bytes (optional)",
          },
          schema_valid: {
            type: "boolean",
            description: "Whether the response matched the expected format (optional)",
          },
          quality_score: {
            type: "number",
            minimum: 1,
            maximum: 5,
            description: "Subjective quality rating from 1 (poor) to 5 (excellent) (optional)",
          },
          receipt: {
            type: "string",
            description: "JWT receipt from the service's X-Trust-Receipt header (optional)",
          },
        },
        required: ["service_did", "status_code", "latency_ms"],
      },
    },
    {
      name: "list_services",
      description:
        "List services that have been rated, sorted by trust score, with their per-dimension " +
        "breakdown. Use it to pick a service that others have already measured rather than " +
        "gambling on an unknown one, or to find an alternative when the service you were going " +
        "to use scores badly. " +
        "Filter on min_score and min_ratings together: a high score backed by one rating is an " +
        "opinion, the same score backed by many is evidence.",
      inputSchema: {
        type: "object" as const,
        properties: {
          sort_by: {
            type: "string",
            enum: ["score", "ratings_count", "last_rated"],
            description: "Sort field (default: score)",
          },
          min_score: {
            type: "number",
            minimum: 0,
            maximum: 1,
            description: "Minimum trust score filter (0.0-1.0)",
          },
          min_ratings: {
            type: "number",
            minimum: 0,
            description: "Minimum number of ratings filter",
          },
          limit: {
            type: "number",
            minimum: 1,
            maximum: 100,
            description: "Number of results (default 20, max 100)",
          },
        },
        required: [],
      },
    },
  ],
}));

// Handle tool calls
server.setRequestHandler(CallToolRequestSchema, async (request) => {
  const { name, arguments: args } = request.params;

  if (name === "check_reputation") {
    const serviceDid = requireString(args?.service_did, "service_did");
    if (typeof serviceDid !== "string") return asError(serviceDid.error);

    try {
      const response = await apiFetch(`/v1/score?service=${encodeURIComponent(serviceDid)}`);

      // API always returns 200 (unknown services get neutral score 0.5)

      if (!response.ok) {
        const body = await response.text();
        return asError(`Error checking reputation (HTTP ${response.status}): ${body}`);
      }

      const score = await response.json();

      // Unknown service — neutral score, no data
      if (score.known === false) {
        return {
          content: [
            {
              type: "text" as const,
              text: [
                `Service ${score.service}: UNKNOWN (no ratings yet)`,
                `Score: 0.5/1.0 (neutral default)`,
                `Confidence: 0 (no data)`,
                ``,
                `This service has never been rated. Proceed with caution.`,
                `After calling it, consider submitting a rating to help other agents.`,
              ].join("\n"),
            },
          ],
        };
      }

      const trustLevel =
        score.score >= 0.8
          ? "HIGH"
          : score.score >= 0.5
            ? "MODERATE"
            : "LOW";

      return {
        content: [
          {
            type: "text" as const,
            text: [
              `Trust Score for ${score.service}: ${score.score}/1.0 (${trustLevel})`,
              `Confidence: ${score.confidence} (based on ${score.ratings_count} ratings)`,
              ``,
              `Dimensions:`,
              `  Availability: ${score.dimensions?.availability ?? "n/a"}`,
              `  Latency:      ${score.dimensions?.latency ?? "n/a"}`,
              `  Conformity:   ${score.dimensions?.conformity ?? "n/a"}`,
              ``,
              score.recent_incidents > 0
                ? `⚠ ${score.recent_incidents} incidents in the last 30 days`
                : `No recent incidents`,
              score.service_supports_receipts
                ? `This service supports trust receipts (verified ratings)`
                : `This service does not yet support trust receipts`,
              ``,
              `Last rated: ${score.last_rated || "never"}`,
            ].join("\n"),
          },
        ],
      };
    } catch (error) {
      return asError(errorText(error, "Failed to check reputation"));
    }
  }

  if (name === "submit_rating") {
    const serviceDid = requireString(args?.service_did, "service_did");
    if (typeof serviceDid !== "string") return asError(serviceDid.error);

    const statusCode = requireInt(args?.status_code, "status_code", 100, 599);
    if (typeof statusCode !== "number") return asError(statusCode.error);

    const latencyMs = requireInt(args?.latency_ms, "latency_ms", 1, 600_000);
    if (typeof latencyMs !== "number") return asError(latencyMs.error);

    const qualityScore = optionalInt(args?.quality_score, "quality_score", 1, 5);
    if (typeof qualityScore === "object" && qualityScore !== undefined)
      return asError(qualityScore.error);

    const responseSizeBytes = optionalInt(
      args?.response_size_bytes,
      "response_size_bytes",
      0,
      // The API stores this as a 32-bit int; cap here so an out-of-range value fails with a clear
      // MCP validation error instead of a raw 400 from .NET deserialization.
      2_147_483_647
    );
    if (typeof responseSizeBytes === "object" && responseSizeBytes !== undefined)
      return asError(responseSizeBytes.error);

    if (args?.schema_valid !== undefined && typeof args.schema_valid !== "boolean")
      return asError("Error: schema_valid must be a boolean");

    if (args?.receipt !== undefined && typeof args.receipt !== "string")
      return asError("Error: receipt must be a string (the JWT from the X-Trust-Receipt header)");

    try {
      const body = {
        // "service" is the canonical field; "service_did" is only kept server-side for
        // backwards compatibility.
        service: serviceDid,
        metrics: {
          status_code: statusCode,
          latency_ms: latencyMs,
          response_size_bytes: responseSizeBytes,
          schema_valid: args?.schema_valid as boolean | undefined,
        },
        quality_score: qualityScore,
        receipt: args?.receipt as string | undefined,
      };

      // Serialise once and reuse the exact string: the signature covers a hash of these bytes, so
      // re-stringifying for the request could silently produce a different body than the one signed.
      const payload = JSON.stringify(body);

      const response = await apiFetch(`/v1/rate`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "X-Agent-DID": AGENT_DID,
          ...signRequest("POST", "/v1/rate", payload),
        },
        body: payload,
      });

      if (!response.ok) {
        const body2 = await response.text();
        return asError(`Error submitting rating (HTTP ${response.status}): ${body2}`);
      }

      const result = await response.json();
      return {
        content: [
          {
            type: "text" as const,
            text: [
              `Rating submitted successfully for ${serviceDid}`,
              `Rating weight: ${result.rating_weight ?? "unknown"}`,
              `Agent identity: ${result.agent_identity ?? "unknown"}`,
              `Updated score: ${result.new_score ?? "unknown"}`,
            ].join("\n"),
          },
        ],
      };
    } catch (error) {
      return asError(errorText(error, "Failed to submit rating"));
    }
  }

  if (name === "list_services") {
    if (
      args?.sort_by !== undefined &&
      !["score", "ratings_count", "last_rated"].includes(args.sort_by as string)
    )
      return asError("Error: sort_by must be one of 'score', 'ratings_count', 'last_rated'");

    try {
      const params = new URLSearchParams();
      // Explicit undefined checks: 0 is a legitimate filter value and must not be dropped.
      if (args?.sort_by !== undefined) params.set("sort_by", String(args.sort_by));
      if (args?.min_score !== undefined) params.set("min_score", String(args.min_score));
      if (args?.min_ratings !== undefined) params.set("min_ratings", String(args.min_ratings));
      if (args?.limit !== undefined) params.set("limit", String(args.limit));

      const response = await apiFetch(`/v1/services?${params.toString()}`);

      if (!response.ok) {
        const body = await response.text();
        return asError(`Error listing services (HTTP ${response.status}): ${body}`);
      }

      const data = (await response.json()) as {
        services: Array<{
          service: string;
          score: number;
          ratings_count: number;
          dimensions: { availability: number; latency: number; conformity: number };
          service_supports_receipts: boolean;
        }>;
        pagination: { count: number; limit: number; offset: number };
      };

      if (!Array.isArray(data.services) || data.services.length === 0) {
        return {
          content: [{ type: "text" as const, text: "No services found matching the criteria." }],
        };
      }

      const lines = data.services.map((s, i) => {
        const trustLevel = s.score >= 0.8 ? "HIGH" : s.score >= 0.5 ? "MODERATE" : "LOW";
        const receipt = s.service_supports_receipts ? " [receipts]" : "";
        return `${i + 1}. ${s.service}: ${s.score}/1.0 (${trustLevel}), ${s.ratings_count} ratings${receipt}`;
      });

      return {
        content: [
          {
            type: "text" as const,
            text: [
              `Found ${data.pagination?.count ?? data.services.length} service(s):`,
              "",
              ...lines,
            ].join("\n"),
          },
        ],
      };
    } catch (error) {
      return asError(errorText(error, "Failed to list services"));
    }
  }

  return {
    content: [{ type: "text" as const, text: `Unknown tool: ${name}` }],
    isError: true,
  };
});

// Start the server
async function main() {
  const transport = new StdioServerTransport();
  await server.connect(transport);
  console.error("TrustScoreAgent MCP server running on stdio");
}

main().catch((error) => {
  console.error("Fatal error:", error);
  process.exit(1);
});
