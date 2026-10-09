#!/usr/bin/env node
// Independent verifier for TrustScoreAgent audit proofs.
//
// Written from docs/MERKLE-SPEC.md, not from the server code, and with no dependencies beyond
// Node 18+, so that checking the registry does not require trusting the registry's own code.
//
//   node verify-proof.mjs <rating_id> [--api https://api.trustscoreagent.com]
//   node verify-proof.mjs --history [count] [--api ...]
//   node verify-proof.mjs --ots <anchor_id> [--api ...] [--explorer https://blockstream.info/api]
//   node verify-proof.mjs --self-test
//
// --history checks that each of the most recent v2 anchors extends the one before it (consistency
// proofs), i.e. that the log only grew. Compare the roots it prints with any you recorded earlier.
//
// --ots checks an anchor's OpenTimestamps proof: that the .ots file is about that anchor's root,
// and that following its operations leads to the Merkle root of a Bitcoin block header, read from
// a public block explorer (or your own node's Esplora API). That block's time is then the latest
// moment the root, and every rating under it, could have been written.
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

// RFC 9162 §2.1.4.2: is the tree (first, firstRoot) a prefix of (second, secondRoot)?
function verifyConsistency(first, second, firstRoot, secondRoot, proofHex) {
  if (first < 1 || first > second) return false;
  let path = proofHex.map((h) => Buffer.from(h, "hex"));
  if (first === second) return path.length === 0 && firstRoot.equals(secondRoot);
  if (path.length === 0) return false;
  if ((first & (first - 1)) === 0) path = [firstRoot, ...path];
  let fn = first - 1;
  let sn = second - 1;
  while (fn & 1) {
    fn >>= 1;
    sn >>= 1;
  }
  let fr = path[0];
  let sr = path[0];
  for (const c of path.slice(1)) {
    if (sn === 0) return false;
    if (fn & 1 || fn === sn) {
      fr = nodeHash(2, c, fr);
      sr = nodeHash(2, c, sr);
      if (!(fn & 1)) {
        while (!(fn & 1) && fn !== 0) {
          fn >>= 1;
          sn >>= 1;
        }
      }
    } else {
      sr = nodeHash(2, sr, c);
    }
    fn >>= 1;
    sn >>= 1;
  }
  return sn === 0 && fr.equals(firstRoot) && sr.equals(secondRoot);
}

// Reference tree builder, only used by the self-test: RFC 6962 MTH for v2.
function rfc6962Root(leaves) {
  if (leaves.length === 1) return leaves[0];
  let k = 1;
  while (k * 2 < leaves.length) k *= 2;
  return nodeHash(2, rfc6962Root(leaves.slice(0, k)), rfc6962Root(leaves.slice(k)));
}

// --- OpenTimestamps (https://opentimestamps.org), written from the format, not from the server ---

const OTS_MAGIC = Buffer.concat([
  Buffer.from([0x00]), Buffer.from("OpenTimestamps"), Buffer.from([0x00, 0x00]), Buffer.from("Proof"),
  Buffer.from([0x00, 0xbf, 0x89, 0xe2, 0xe8, 0x84, 0xe8, 0x92, 0x94]),
]);
const OTS_PENDING = "83dfe30d2ef90c8e";
const OTS_BITCOIN = "0588960d73d71901";

function otsReader(buf) {
  let pos = 0;
  const byte = () => {
    if (pos >= buf.length) throw new Error("truncated .ots");
    return buf[pos++];
  };
  const bytes = (n) => {
    if (pos + n > buf.length) throw new Error("truncated .ots");
    const out = buf.subarray(pos, pos + n);
    pos += n;
    return out;
  };
  const varuint = () => {
    let value = 0;
    let shift = 0;
    let b;
    do {
      b = byte();
      value += (b & 0x7f) * 2 ** shift;
      shift += 7;
      if (shift > 63) throw new Error("bad varuint in .ots");
    } while (b & 0x80);
    return value;
  };
  const varbytes = (max) => {
    const n = varuint();
    if (n > max) throw new Error("oversized field in .ots");
    return bytes(n);
  };
  return { byte, bytes, varuint, varbytes, atEnd: () => pos === buf.length };
}

// Walks the timestamp from `msg`, returning every attestation with the message it commits to.
function otsAttestations(r, msg, depth = 0, out = []) {
  if (depth > 256) throw new Error(".ots nested too deeply");
  const item = (tag) => {
    if (tag === 0x00) {
      const kind = Buffer.from(r.bytes(8)).toString("hex");
      const payload = otsReader(r.varbytes(8192));
      if (kind === OTS_PENDING) out.push({ pending: Buffer.from(payload.varbytes(1000)).toString("utf8"), msg });
      else if (kind === OTS_BITCOIN) out.push({ bitcoin: payload.varuint(), msg });
      else out.push({ unknown: kind, msg });
      return;
    }
    let next;
    if (tag === 0xf0) next = Buffer.concat([msg, r.varbytes(4096)]);
    else if (tag === 0xf1) next = Buffer.concat([r.varbytes(4096), msg]);
    else if (tag === 0x08) next = sha256(msg);
    else if (tag === 0x02) next = createHash("sha1").update(msg).digest();
    else if (tag === 0x03) next = createHash("ripemd160").update(msg).digest();
    else throw new Error(`unsupported .ots operation 0x${tag.toString(16)}`);
    otsAttestations(r, next, depth + 1, out);
  };
  let tag = r.byte();
  while (tag === 0xff) {
    item(r.byte());
    tag = r.byte();
  }
  item(tag);
  return out;
}

function parseOtsFile(buf) {
  const r = otsReader(buf);
  if (!Buffer.from(r.bytes(OTS_MAGIC.length)).equals(OTS_MAGIC)) throw new Error("not an .ots file");
  if (r.varuint() !== 1) throw new Error("unsupported .ots version");
  if (r.byte() !== 0x08) throw new Error(".ots file is not over a SHA-256 digest");
  const digest = Buffer.from(r.bytes(32));
  const attestations = otsAttestations(r, digest);
  if (!r.atEnd()) throw new Error("trailing bytes in .ots");
  return { digest, attestations };
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
  // Consistency 3 -> 7 over SHA256("leaf-0") .. SHA256("leaf-6"), computed independently.
  const leaves = [0, 1, 2, 3, 4, 5, 6].map((i) => sha256(Buffer.from(`leaf-${i}`)));
  const proof3to7 = [
    "649837ddcb7e1967086d7d35aaef7b975c513815d96fc6e70015e93a2bfe0f9a",
    "9fde56c376760bd399b82eb8569229a2dff19219411ac71154dfeab2cf502454",
    "c76c1321b98ab0ea04447b38d8daeb85fa04df66731a8f25a60c84a1548d9831",
    "c28121395ace509462b8b9f255e9811949c00c347032fdf4004e53d1da650cbb",
  ];
  const root3 = rfc6962Root(leaves.slice(0, 3));
  const root7 = rfc6962Root(leaves);
  checks.push(["consistency 3 -> 7 verifies",
    String(verifyConsistency(3, 7, root3, root7, proof3to7)), "true"]);
  const tampered = [...leaves];
  tampered[1] = sha256(Buffer.from("edited"));
  checks.push(["consistency rejects an edited prefix",
    String(verifyConsistency(3, 7, root3, rfc6962Root(tampered), proof3to7)), "false"]);
  // A real .ots (three calendars' answers, merged by python-opentimestamps), as in OtsVectors.cs.
  const ots = parseOtsFile(Buffer.from(OTS_REFERENCE_FILE, "hex"));
  checks.push(["ots digest", ots.digest.toString("hex"),
    "92118c14134d054006368a227bdc3309b30a3a40386b822e0e5d4f1d3eecec45"]);
  checks.push(["ots pending message (alice)",
    ots.attestations.find((a) => a.pending === "https://alice.btc.calendar.opentimestamps.org")?.msg.toString("hex"),
    "6ac8b1e4289f82fdbeac11b865ab603666edd03ca741915f6ee46586de55155b179abf631994148b05894c29"]);
  checks.push(["ots attestations", String(ots.attestations.length), "3"]);
  let ok = true;
  for (const [name, got, want] of checks) {
    const pass = got === want;
    ok &&= pass;
    console.log(`${pass ? "ok  " : "FAIL"} ${name}${pass ? "" : `\n     got  ${got}\n     want ${want}`}`);
  }
  return ok;
}

const OTS_REFERENCE_FILE =
  "004f70656e54696d657374616d7073000050726f6f6600bf89e2e884e89294010892118c14134d054006368a227bdc3309b30a3a40386b822e0e5d4f1d3eecec45fff00809b157f7da8bec0d08f010996de24e521f7deaf26aaa292f8cc48808f0207ac46edb56cffcabc3f842e0b6484768c6189aa37909e38f499eafecfd70880808f1209b4c96ab4120909acd6ccc3f5ea51745e01812408ec3e9ed17c7fec4aaf503fb08f020a63b0f6dc04a3e6de5d3d497362fab64d4f0a5ce83de79df312e65cf4ae1c50b08f1046ac8b1e5f008c7d0f34af1c9e7910083dfe30d2ef90c8e2c2b68747470733a2f2f626f622e6274632e63616c656e6461722e6f70656e74696d657374616d70732e6f7267fff00898cdd1b16b2743a208f0104254e5a0dbac880bf806b996acffd52e08f120ce4ab4464a6a4c4e33c59c0e59b356849e8e92e29a127bfa5c935569d56870f308f020b8ab22b6b1ca7ae5b8a36426d4ba8151b3f683792230e4907b389e5b748b5f0308f1046ac8b1e4f0081994148b05894c290083dfe30d2ef90c8e2e2d68747470733a2f2f616c6963652e6274632e63616c656e6461722e6f70656e74696d657374616d70732e6f7267f010e177595da810a33babc4d94b800686a608f1207e0124e49c95023c9c1715ccf7b28aca5bb79925a70b80112425da17e2f5147e08f020bae0252d1cca8cf2a4dbc0d303adc6221f270f973fb1be670de810ff1959a25508f020193a48f2bc6fc39d3f85ad5e919dbd2e58919b70dad25d18684a2ec9bad122ae08f1046ac8b1e6f00857bed714cd517feb0083dfe30d2ef90c8e292868747470733a2f2f66696e6e65792e63616c656e6461722e657465726e69747977616c6c2e636f6d";

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

async function verifyHistory(count, api) {
  // Oldest first, v2 only: v1 anchors predate consistency proofs and cannot have one.
  const { anchors } = await getJson(`${api}/v1/audit/anchors?limit=${count}`);
  const chain = anchors.filter((a) => a.tree_version === 2).reverse();
  if (chain.length < 2) {
    console.log(`only ${chain.length} v2 anchor(s) so far: nothing to compare yet`);
    return true;
  }
  let ok = true;
  for (let i = 1; i < chain.length; i++) {
    const [a, b] = [chain[i - 1], chain[i]];
    const label = `#${a.id} (${a.leaf_count}) -> #${b.id} (${b.leaf_count})`;
    const res = await fetch(`${api}/v1/audit/consistency?from=${a.id}&to=${b.id}`);
    const body = await res.json();
    if (!res.ok) {
      console.log(`FAIL ${label}: HTTP ${res.status} ${body.error ?? ""} ${body.message ?? ""}`);
      ok = false;
      continue;
    }
    // Roots and sizes from the anchor list, never from the proof response.
    const good = verifyConsistency(a.leaf_count, b.leaf_count,
      Buffer.from(a.merkle_root, "hex"), Buffer.from(b.merkle_root, "hex"), body.proof);
    console.log(`${good ? "ok  " : "FAIL"} ${label}`);
    ok &&= good;
  }
  const last = chain[chain.length - 1];
  console.log(`latest root ${last.merkle_root} (${last.leaf_count} leaves, ${last.anchored_at})`);
  return ok;
}

async function verifyOts(anchorId, api, explorer) {
  const { anchors } = await getJson(`${api}/v1/audit/anchors?limit=1&before=${anchorId + 1}`);
  const anchor = anchors[0];
  if (!anchor || anchor.id !== anchorId) throw new Error(`no anchor #${anchorId}`);
  console.log(`anchor      #${anchor.id}, root ${anchor.merkle_root} (${anchor.leaf_count} leaves, ${anchor.anchored_at})`);

  const res = await fetch(`${api}/v1/audit/anchors/${anchorId}/ots`);
  if (!res.ok)
    throw new Error(`no timestamp for anchor #${anchorId} yet (HTTP ${res.status}); anchors are stamped within 6 hours`);
  const { digest, attestations } = parseOtsFile(Buffer.from(await res.arrayBuffer()));
  if (digest.toString("hex") !== anchor.merkle_root) {
    console.log(`FAIL the .ots file is over ${digest.toString("hex")}, not over the anchor's root`);
    return false;
  }
  console.log("ok   the .ots file is over the anchor's root");

  for (const a of attestations.filter((x) => x.pending)) console.log(`     pending at ${a.pending}`);
  const bitcoin = attestations.filter((a) => a.bitcoin !== undefined);
  if (bitcoin.length === 0) {
    console.log("note not in Bitcoin yet: calendars commit within a few hours, and the registry fetches the " +
      "completed proof on its next run. Try again later.");
    return true;
  }

  let ok = true;
  for (const a of bitcoin) {
    const hashRes = await fetch(`${explorer}/block-height/${a.bitcoin}`);
    if (!hashRes.ok) throw new Error(`${explorer} has no block ${a.bitcoin} (HTTP ${hashRes.status})`);
    const block = await getJson(`${explorer}/block/${(await hashRes.text()).trim()}`);
    // The attested message is the header's Merkle root in internal byte order; explorers print it reversed.
    const expected = Buffer.from(a.msg).reverse().toString("hex");
    const good = block.merkle_root === expected;
    console.log(`${good ? "ok  " : "FAIL"} committed in Bitcoin block ${a.bitcoin} ` +
      `(${new Date(block.timestamp * 1000).toISOString()})` +
      (good ? "" : `: the proof leads to ${expected}, the block's Merkle root is ${block.merkle_root}`));
    ok &&= good;
  }
  return ok;
}

const args = process.argv.slice(2);
if (args[0] === "--self-test") {
  process.exit(selfTest() ? 0 : 1);
}
const apiIndex = args.indexOf("--api");
const api = (apiIndex >= 0 ? args[apiIndex + 1] : "https://api.trustscoreagent.com").replace(/\/+$/, "");
if (args[0] === "--history") {
  const n = Number.parseInt(args[1], 10);
  try {
    process.exit((await verifyHistory(Number.isInteger(n) ? Math.min(n, 100) : 20, api)) ? 0 : 1);
  } catch (e) {
    console.error(`error: ${e.message}`);
    process.exit(1);
  }
}
if (args[0] === "--ots") {
  const id = Number.parseInt(args[1], 10);
  const explorerIndex = args.indexOf("--explorer");
  const explorer = (explorerIndex >= 0 ? args[explorerIndex + 1] : "https://blockstream.info/api").replace(/\/+$/, "");
  if (!Number.isInteger(id)) {
    console.error("usage: node verify-proof.mjs --ots <anchor_id> [--api <base url>] [--explorer <esplora api url>]");
    process.exit(2);
  }
  try {
    process.exit((await verifyOts(id, api, explorer)) ? 0 : 1);
  } catch (e) {
    console.error(`error: ${e.message}`);
    process.exit(1);
  }
}
const ratingId = args.find((a, i) => !a.startsWith("--") && i !== apiIndex + 1);
if (!ratingId) {
  console.error("usage: node verify-proof.mjs <rating_id> | --history [count] | --ots <anchor_id> | --self-test  [--api <base url>]");
  process.exit(2);
}
try {
  process.exit((await verify(ratingId, api)) ? 0 : 1);
} catch (e) {
  console.error(`error: ${e.message}`);
  process.exit(1);
}
