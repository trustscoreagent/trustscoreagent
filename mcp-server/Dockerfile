# Container image for running the TrustScoreAgent MCP server over stdio.
#
# Used by MCP registries/inspectors (e.g. Glama) to start the server and run the
# standard introspection exchange (initialize, tools/list). It mirrors exactly
# what end users run via `npx -y @trustscoreagent/mcp-server`: the published npm
# package, launched on stdio. JSON-RPC goes to stdout; logs go to stderr (safe
# for MCP clients). No COPY steps, so it builds regardless of build context.
FROM node:20-slim

# Install the published server globally; its bin is `trustscoreagent-mcp`.
RUN npm install -g @trustscoreagent/mcp-server

# The registry defaults to production; override with TRUSTSCORE_API_URL if needed.
ENV TRUSTSCORE_API_URL=https://api.trustscoreagent.com

# stdio transport: the MCP host spawns the container and talks over stdin/stdout.
ENTRYPOINT ["trustscoreagent-mcp"]
