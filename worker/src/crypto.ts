import { HttpError } from "./http";

const AAD = new TextEncoder().encode("instapanel:account-token:v1");

function fromBase64(value: string): Uint8Array {
  const bin = atob(value);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

function toBase64(bytes: ArrayBuffer | Uint8Array): string {
  const view = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes);
  let bin = "";
  for (const b of view) bin += String.fromCharCode(b);
  return btoa(bin);
}

async function importKey(secret: string): Promise<CryptoKey> {
  let raw: Uint8Array;
  try {
    raw = fromBase64(secret ?? "");
  } catch {
    raw = new Uint8Array();
  }
  if (raw.length !== 32) {
    throw new HttpError(500, "misconfigured", "TOKEN_ENC_KEY deve ter 32 bytes em base64.");
  }
  return crypto.subtle.importKey("raw", raw, "AES-GCM", false, ["encrypt", "decrypt"]);
}

export interface Sealed {
  ciphertext: string;
  iv: string;
}

export async function seal(plaintext: string, secret: string): Promise<Sealed> {
  const key = await importKey(secret);
  const iv = crypto.getRandomValues(new Uint8Array(12));
  const data = await crypto.subtle.encrypt(
    { name: "AES-GCM", iv, additionalData: AAD },
    key,
    new TextEncoder().encode(plaintext),
  );
  return { ciphertext: toBase64(data), iv: toBase64(iv) };
}

export async function open(sealed: Sealed, secret: string): Promise<string> {
  const key = await importKey(secret);
  const data = await crypto.subtle.decrypt(
    { name: "AES-GCM", iv: fromBase64(sealed.iv), additionalData: AAD },
    key,
    fromBase64(sealed.ciphertext),
  );
  return new TextDecoder().decode(data);
}
