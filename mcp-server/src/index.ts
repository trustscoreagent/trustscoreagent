#!/usr/bin/env node

import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
  CallToolRequestSchema,
  ListToolsRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";
import { randomUUID } from "crypto";
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
    process.env.TRUSTSCORE_API_URL || "https://trustscoreagent-api-staging-xhunhkdtfa-ew.a.run.app";
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
    return `${action}: the TrustScoreAgent API did not respond within ${FETCH_TIMEOUT_MS / 1000}s. It may be down or slow — try again later.`;
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

// Each MCP installation gets a unique persistent agent ID.
// Stored in ~/.trustscoreagent/agent-id so it survives restarts.
// Can be overridden via TRUSTSCORE_AGENT_DID env var.
function getAgentDid(): string {
  if (process.env.TRUSTSCORE_AGENT_DID) {
    return process.env.TRUSTSCORE_AGENT_DID;
  }

  const configDir = join(homedir(), ".trustscoreagent");
  const idFile = join(configDir, "agent-id");

  if (existsSync(idFile)) {
    return readFileSync(idFile, "utf-8").trim();
  }

  // Generate a unique DID on first run
  const agentId = `mcp-${randomUUID().slice(0, 8)}`;
  const did = `did:web:mcp.trustscoreagent.com:${agentId}`;

  try {
    mkdirSync(configDir, { recursive: true });
    writeFileSync(idFile, did, "utf-8");
    console.error(`TrustScoreAgent: generated agent ID ${did} (stored in ${idFile})`);
  } catch {
    console.error(`TrustScoreAgent: using ephemeral agent ID ${did} (could not write to ${idFile})`);
  }

  return did;
}

const AGENT_DID = getAgentDid();

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
        "Check the trust score and reputation of an AI microservice before calling it. " +
        "Returns a score between 0 and 1, confidence level, number of ratings, " +
        "and dimensional breakdown (availability, latency, conformity). " +
        "Use this BEFORE calling any untrusted external service to verify its reliability.",
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
        "Rate an AI microservice after calling it. " +
        "Provide the technical metrics from your interaction. " +
        "This helps other agents know if the service is reliable. " +
        "Include the receipt from the X-Trust-Receipt header if the service provided one.",
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
        "List AI microservices that have been rated, sorted by trust score. " +
        "Use this to discover reliable services or find alternatives. " +
        "Returns a paginated list with scores and dimensional breakdown.",
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

      const response = await apiFetch(`/v1/rate`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "X-Agent-DID": AGENT_DID,
        },
        body: JSON.stringify(body),
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
        return `${i + 1}. ${s.service} — ${s.score}/1.0 (${trustLevel}) — ${s.ratings_count} ratings${receipt}`;
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
