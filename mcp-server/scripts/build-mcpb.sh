#!/usr/bin/env bash
# Build the TrustScoreAgent MCP server as a self-contained .mcpb bundle
# (MCP Bundle — for Smithery "local" install and Claude Desktop drag-and-drop).
#
# The bundle contains dist/ plus production-only node_modules, so it runs with a
# plain `node dist/index.js` — no npx / network install at launch. The resulting
# .mcpb is a build artifact and is intentionally NOT committed (see .gitignore);
# regenerate it here or in CI and attach it to a GitHub Release.
#
# Usage:  cd mcp-server && bash scripts/build-mcpb.sh
set -euo pipefail

cd "$(dirname "$0")/.."          # -> mcp-server/
ROOT="$(pwd)"

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

echo "==> Compiling TypeScript"
npm ci
npm run build

echo "==> Staging bundle contents"
cp -r dist "$STAGE/dist"
cp manifest.json package.json package-lock.json README.md LICENSE "$STAGE/"

echo "==> Installing production dependencies into the bundle"
( cd "$STAGE" && npm ci --omit=dev )

VERSION="$(node -p "require('./package.json').version")"
OUT="$ROOT/trustscoreagent-${VERSION}.mcpb"

echo "==> Packing"
npx --yes @anthropic-ai/mcpb pack "$STAGE" "$OUT"

echo "==> Done: $OUT"
echo "    Validate/inspect: npx @anthropic-ai/mcpb info \"$OUT\""
