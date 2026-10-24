import type { Env } from "./env";

/** 32 bytes aleatórios em base64url (43 caracteres): impossível de adivinhar. */
export function randomToken(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(32));
  let bin = "";
  for (const b of bytes) bin += String.fromCharCode(b);
  return btoa(bin).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

export async function logEvent(env: Env, postId: string | null, kind: string, detail?: unknown): Promise<void> {
  await env.DB.prepare("INSERT INTO events (post_id, kind, detail, created_at) VALUES (?1, ?2, ?3, ?4)")
    .bind(postId, kind, detail === undefined ? null : JSON.stringify(detail), Date.now())
    .run();
}

export const sleep = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));
