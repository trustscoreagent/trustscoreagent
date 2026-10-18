import {
  createHash,
  createPrivateKey,
  createPublicKey,
  generateKeyPairSync,
  randomUUID,
  sign as cryptoSign,
  type KeyObject,
} from "node:crypto";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, join } from "node:path";

/**
 * Same file as the MCP server's, so an agent that uses both rates under one did:key.
 * The key is generated locally and never leaves the machine; only its public half is in the DID.
 */
export const DEFAULT_KEY_FILE = join(homedir(), ".trustscoreagent", "agent-key.pem");

export interface AgentIdentity {
  did: string;
  privateKey: KeyObject;
}

const BASE58_ALPHABET = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

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

// did:key: base58btc of the 0xED01 multicodec prefix followed by the raw 32-byte Ed25519 key.
export function deriveDidKey(publicKey: KeyObject): string {
  const jwk = publicKey.export({ format: "jwk" }) as { x?: string };
  if (!jwk.x) throw new Error("public key is not an OKP/Ed25519 JWK");
  const raw = Buffer.from(jwk.x, "base64url");
  return "did:key:z" + base58Encode(Buffer.concat([Buffer.from([0xed, 0x01]), raw]));
}

function identityOf(privateKey: KeyObject): AgentIdentity {
  return { did: deriveDidKey(createPublicKey(privateKey)), privateKey };
}

/**
 * Loads the key from `keyFile`, creating it on first use. If the file can be neither read nor
 * written, falls back to a key held in memory: ratings stay signed, under a DID that lasts as
 * long as the process.
 */
export function loadIdentity(source?: { privateKey?: KeyObject | string; keyFile?: string }): AgentIdentity {
  if (source?.privateKey)
    return identityOf(
      typeof source.privateKey === "string" ? createPrivateKey(source.privateKey) : source.privateKey,
    );

  const keyFile = source?.keyFile ?? DEFAULT_KEY_FILE;
  try {
    return identityOf(createPrivateKey(readFileSync(keyFile, "utf-8")));
  } catch {
    // Missing or unreadable: create one below.
  }

  const { privateKey } = generateKeyPairSync("ed25519");
  try {
    mkdirSync(dirname(keyFile), { recursive: true });
    writeFileSync(keyFile, privateKey.export({ format: "pem", type: "pkcs8" }) as string, {
      mode: 0o600,
      flag: "wx",
    });
  } catch {
    // Another process may have created it meanwhile; prefer that one so both share a DID.
    try {
      return identityOf(createPrivateKey(readFileSync(keyFile, "utf-8")));
    } catch {
      // Keep the in-memory key.
    }
  }
  return identityOf(privateKey);
}

/**
 * Signs a request the way the registry verifies it: eight newline-joined fields, with the body
 * bound by its SHA-256. `body` must be the exact string sent on the wire.
 */
export function signRequest(
  identity: AgentIdentity,
  audience: string,
  method: string,
  path: string,
  body: string,
): Record<string, string> {
  const timestamp = new Date().toISOString();
  const nonce = randomUUID().replace(/-/g, "");
  const bodyHash = createHash("sha256").update(body, "utf-8").digest("hex");

  const canonical = [
    "trustscore-v1",
    audience,
    method.toUpperCase(),
    path,
    identity.did,
    timestamp,
    nonce,
    bodyHash,
  ].join("\n");

  // Ed25519 signs the message directly, hence the null algorithm.
  const signature = cryptoSign(null, Buffer.from(canonical, "utf-8"), identity.privateKey);

  return {
    "X-Agent-DID": identity.did,
    "X-Agent-Signature": base64url(signature),
    "X-Agent-Timestamp": timestamp,
    "X-Agent-Nonce": nonce,
  };
}
