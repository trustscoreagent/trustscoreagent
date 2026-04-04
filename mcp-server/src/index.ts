#!/usr/bin/env node

import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
  CallToolRequestSchema,
  ListToolsRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";

const API_BASE_URL =
  process.env.TRUSTSCORE_API_URL || "https://trustscoreagent-api-staging-xhunhkdtfa-ew.a.run.app";

const server = new Server(
  {
    name: "trustscoreagent",
    version: "0.1.0",
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
              "The DID (Decentralized Identifier) of the service to check. " +
              "Format: did:web:domain.com (e.g., did:web:api.example.com)",
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
            description: "The DID of the service you called (e.g., did:web:api.example.com)",
          },
          status_code: {
            type: "number",
            description: "HTTP status code returned by the service (e.g., 200, 500)",
          },
          latency_ms: {
            type: "number",
            description: "Response time in milliseconds",
          },
          response_size_bytes: {
            type: "number",
            description: "Size of the response in bytes (optional)",
          },
          schema_valid: {
            type: "boolean",
            description: "Whether the response matched the expected format (optional)",
          },
          quality_score: {
            type: "number",
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
            description: "Sort field: 'score' (default), 'ratings_count', or 'last_rated'",
          },
          min_score: {
            type: "number",
            description: "Minimum trust score filter (0.0-1.0)",
          },
          min_ratings: {
            type: "number",
            description: "Minimum number of ratings filter",
          },
          limit: {
            type: "number",
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
    const serviceDid = args?.service_did as string;
    if (!serviceDid) {
      return {
        content: [{ type: "text" as const, text: "Error: service_did is required" }],
        isError: true,
      };
    }

    try {
      const response = await fetch(
        `${API_BASE_URL}/v1/score?did=${encodeURIComponent(serviceDid)}`
      );

      // API always returns 200 (unknown services get neutral score 0.5)

      if (!response.ok) {
        const errorText = await response.text();
        return {
          content: [{ type: "text" as const, text: `Error checking reputation: ${errorText}` }],
          isError: true,
        };
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
              `  Availability: ${score.dimensions.availability}`,
              `  Latency:      ${score.dimensions.latency}`,
              `  Conformity:   ${score.dimensions.conformity}`,
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
      return {
        content: [
          {
            type: "text" as const,
            text: `Failed to reach TrustScoreAgent API: ${error instanceof Error ? error.message : "Unknown error"}`,
          },
        ],
        isError: true,
      };
    }
  }

  if (name === "submit_rating") {
    const serviceDid = args?.service_did as string;
    const statusCode = args?.status_code as number;
    const latencyMs = args?.latency_ms as number;

    if (!serviceDid || !statusCode || !latencyMs) {
      return {
        content: [
          {
            type: "text" as const,
            text: "Error: service_did, status_code, and latency_ms are required",
          },
        ],
        isError: true,
      };
    }

    try {
      const body = {
        service_did: serviceDid,
        metrics: {
          status_code: statusCode,
          latency_ms: latencyMs,
          response_size_bytes: args?.response_size_bytes as number | undefined,
          schema_valid: args?.schema_valid as boolean | undefined,
        },
        quality_score: args?.quality_score as number | undefined,
        receipt: args?.receipt as string | undefined,
      };

      const response = await fetch(`${API_BASE_URL}/v1/rate`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "X-Agent-DID": "did:web:mcp-client.local",
        },
        body: JSON.stringify(body),
      });

      if (!response.ok) {
        const errorText = await response.text();
        return {
          content: [{ type: "text" as const, text: `Error submitting rating: ${errorText}` }],
          isError: true,
        };
      }

      const result = await response.json();
      return {
        content: [
          {
            type: "text" as const,
            text: [
              `Rating submitted successfully for ${serviceDid}`,
              `Rating weight: ${result.rating_weight}`,
              `Updated score: ${result.new_score}`,
            ].join("\n"),
          },
        ],
      };
    } catch (error) {
      return {
        content: [
          {
            type: "text" as const,
            text: `Failed to submit rating: ${error instanceof Error ? error.message : "Unknown error"}`,
          },
        ],
        isError: true,
      };
    }
  }

  if (name === "list_services") {
    try {
      const params = new URLSearchParams();
      if (args?.sort_by) params.set("sort_by", args.sort_by as string);
      if (args?.min_score) params.set("min_score", String(args.min_score));
      if (args?.min_ratings) params.set("min_ratings", String(args.min_ratings));
      if (args?.limit) params.set("limit", String(args.limit));

      const response = await fetch(`${API_BASE_URL}/v1/services?${params.toString()}`);

      if (!response.ok) {
        const errorText = await response.text();
        return {
          content: [{ type: "text" as const, text: `Error listing services: ${errorText}` }],
          isError: true,
        };
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

      if (data.services.length === 0) {
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
              `Found ${data.pagination.count} service(s):`,
              "",
              ...lines,
            ].join("\n"),
          },
        ],
      };
    } catch (error) {
      return {
        content: [
          {
            type: "text" as const,
            text: `Failed to list services: ${error instanceof Error ? error.message : "Unknown error"}`,
          },
        ],
        isError: true,
      };
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
