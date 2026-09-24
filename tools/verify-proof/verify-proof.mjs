#!/usr/bin/env node
// Independent verifier for TrustScoreAgent audit proofs.
//
// Written from docs/MERKLE-SPEC.md, not from the server code, and with no dependencies beyond
// Node 18+, so that checking the registry does not require trusting the registry's own code.
//
//   node verify-proof.mjs <rating_id> [--api https://api.trustscoreagent.com]
//   node verify-proof.mjs --self-test
//
// For a rating it checks, in order:
//   1. the proof is about the rating that was asked for;
//   2. the committed fields hash to the leaf in the tree (for v2 leaves: the rating has not been
//      edited since it was written);
//   3. the proof path from that leaf reaches merkle_root;
//   4. merkle_root is the root the registry currently publishes at /v1/audit/root.
// Exit code 0 when all hold, 1 otherwise.

import { createHash } from "node:crypto";

const sha256 = (...parts) => {
  const h = createHash("sha256");
  for (const p of parts) h.update(p);
  return h.digest();
};

const LEAF_PREFIX = Buffer.from([0x00]);
const NODE_PREFIX = Buffer.from([0x01]);

// --- leaves (MERKLE-SPEC "Leaf v1" / "Leaf v2") ---

function leafV1(c) {
  return sha256(Buffer.from(`${c.id}:${c.service}:${c.created_at}`, "utf8"));
}

function canonicalV2(c) {
  const int = (v) => {
    if (!Number.isInteger(v)) throw new Error(`expected an integer, got ${JSON.stringify(v)}`);
    return String(v);
  };
  const bool = (v) => {
    if (typeof v !== "boolean") throw new Error(`expected a boolean, got ${JSON.stringify(v)}`);
    return v ? "true" : "false";
  };
  const opt = (v, f) => (v === null || v === undefined ? "" : f(v));
  if (!/^\d+\.\d{6}$/.test(c.weight)) throw new Error(`weight must have exactly 6 decimals: ${c.weight}`);
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}Z$/.test(c.created_at))
    throw new Error(`created_at must be UTC with microseconds: ${c.created_at}`);

  const fields = [
    "trustscore-leaf-v2",
    c.id,
    c.service,
    c.created_at,
    int(c.status_code),
    int(c.latency_ms),
    opt(c.response_size_bytes, int),
    opt(c.schema_valid, bool),
    opt(c.quality_score, int),
    bool(c.has_receipt),
    bool(c.receipt_verified),
    bool(c.signature_verified),
    c.weight,
  ];
  if (fields.some((f) => typeof f !== "string" || f.includes("\n")))
    throw new Error("a committed field is missing or contains a newline");
  return fields.join("\n");
}

function leafV2(c) {
  return sha256(LEAF_PREFIX, Buffer.from(canonicalV2(c), "utf8"));
}

function leafHash(version, committed) {
  if (version === 1) return leafV1(committed);
  if (version === 2) return leafV2(committed);
  throw new Error(`unknown leaf_version ${version}`);
}

// --- tree (MERKLE-SPEC "Tree v1" / "Tree v2") ---

function nodeHash(version, left, right) {
  if (version === 1) return sha256(left, right);
  if (version === 2) return sha256(NODE_PREFIX, left, right);
  throw new Error(`unknown tree_version ${version}`);
}

function foldProof(version, leaf, proof) {
  let current = leaf;
  for (const node of proof) {
    const sibling = Buffer.from(node.hash, "hex");
    current = node.is_right ? nodeHash(version, current, sibling) : nodeHash(version, sibling, current);
  }
  return current;
}

// Reference tree builder, only used by the self-test: RFC 6962 MTH for v2.
function rfc6962Root(leaves) {
  if (leaves.length === 1) return leaves[0];
  let k = 1;
  while (k * 2 < leaves.length) k *= 2;
  return nodeHash(2, rfc6962Root(leaves.slice(0, k)), rfc6962Root(leaves.slice(k)));
}

// --- self-test against the golden vectors pinned in the server's test suite ---

function selfTest() {
  const committed = {
    id: "3f2b8c1e-5d4a-4b7e-9c2f-0a1b2c3d4e5f",
    service: "api.example.com",
    created_at: "2026-09-24T16:02:55.123456Z",
    status_code: 200,
    latency_ms: 143,
    response_size_bytes: 2048,
    schema_valid: true,
    quality_score: 4,
    has_receipt: false,
    receipt_verified: false,
    signature_verified: true,
    weight: "0.300000",
  };
  const checks = [
    ["leaf v2 golden hash", leafV2(committed).toString("hex"),
      "1c6614309f5d94d31ad7a328181c9a3a38b632655fc7395b613c5a285b712a65"],
    ["tree v2 golden root (3 leaves)",
      rfc6962Root([0, 1, 2].map((i) => sha256(Buffer.from(`leaf-${i}`)))).toString("hex"),
      "3fd64e951bb292c4cc9ea78ea50e1115c0b754f0ca8b4a4f4a9610bd2c258877"],
  ];
  let ok = true;
  for (const [name, got, want] of checks) {
    const pass = got === want;
    ok &&= pass;
    console.log(`${pass ? "ok  " : "FAIL"} ${name}${pass ? "" : `\n     got  ${got}\n     want ${want}`}`);
  }
  return ok;
}

// --- live verification ---

async function getJson(url) {
  const res = await fetch(url, { headers: { accept: "application/json" } });
  if (!res.ok) throw new Error(`${url} answered HTTP ${res.status}: ${(await res.text()).slice(0, 200)}`);
  return res.json();
}

async function verify(ratingId, api) {
  const p = await getJson(`${api}/v1/audit/proof/${encodeURIComponent(ratingId)}`);
  const fail = (msg) => {
    console.log(`FAIL ${msg}`);
    return false;
  };

  console.log(`rating      ${p.rating_id}`);
  console.log(`leaf        v${p.leaf_version}, tree v${p.tree_version}, index ${p.leaf_index} of ${p.total_leaves}`);
  console.log(`committed   ${JSON.stringify(p.committed)}`);

  if (p.rating_id !== ratingId.toLowerCase() || p.committed?.id !== ratingId.toLowerCase())
    return fail("the proof is about a different rating than the one requested");

  const leaf = leafHash(p.leaf_version, p.committed);
  if (leaf.toString("hex") !== p.leaf_hash)
    return fail(
      `the committed fields hash to ${leaf.toString("hex")}, not to the leaf in the tree (${p.leaf_hash}). ` +
        "The rating was modified after it was written.",
    );
  console.log("ok   committed fields hash to the leaf");

  const root = foldProof(p.tree_version, Buffer.from(p.leaf_hash, "hex"), p.proof);
  if (root.toString("hex") !== p.merkle_root)
    return fail(`the proof path leads to ${root.toString("hex")}, not to merkle_root ${p.merkle_root}`);
  console.log("ok   proof path reaches merkle_root");

  const published = await getJson(`${api}/v1/audit/root`);
  if (published.merkle_root !== p.merkle_root)
    return fail(
      `merkle_root ${p.merkle_root} is not the published root ${published.merkle_root}. ` +
        "If an anchor was just made, run again.",
    );
  console.log(`ok   merkle_root is the published root (anchored ${published.anchored_at})`);
  if (published.transaction_hash)
    console.log(`     on-chain: ${published.blockchain} tx ${published.transaction_hash}`);

  if (p.leaf_version === 1)
    console.log("note leaf v1 commits to (id, service, created_at) only, not to what the rating reported.");
  return true;
}

const args = process.argv.slice(2);
if (args[0] === "--self-test") {
  process.exit(selfTest() ? 0 : 1);
}
const apiIndex = args.indexOf("--api");
const api = (apiIndex >= 0 ? args[apiIndex + 1] : "https://api.trustscoreagent.com").replace(/\/+$/, "");
const ratingId = args.find((a, i) => !a.startsWith("--") && i !== apiIndex + 1);
if (!ratingId) {
  console.error("usage: node verify-proof.mjs <rating_id> [--api <base url>] | --self-test");
  process.exit(2);
}
try {
  process.exit((await verify(ratingId, api)) ? 0 : 1);
} catch (e) {
  console.error(`error: ${e.message}`);
  process.exit(1);
}
