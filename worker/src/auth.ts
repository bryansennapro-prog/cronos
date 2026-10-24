import type { Env } from "./env";
import { HttpError } from "./http";

const MIN_KEY_LENGTH = 32;

async function sha256(value: string): Promise<ArrayBuffer> {
  return crypto.subtle.digest("SHA-256", new TextEncoder().encode(value));
}

/** Exige `Authorization: Bearer <PANEL_API_KEY>`. Falha fechado se o secret não estiver configurado. */
export async function requirePanelKey(request: Request, env: Env): Promise<void> {
  const expected = env.PANEL_API_KEY;
  if (!expected || expected.length < MIN_KEY_LENGTH) {
    throw new HttpError(500, "misconfigured", "PANEL_API_KEY ausente ou curto demais no Worker.");
  }

  const header = request.headers.get("authorization") ?? "";
  const match = /^Bearer\s+(.+)$/i.exec(header);
  const provided = match?.[1]?.trim() ?? "";

  // Compara os hashes (mesmo tamanho) em tempo constante.
  const [a, b] = await Promise.all([sha256(provided), sha256(expected)]);
  if (!crypto.subtle.timingSafeEqual(a, b)) {
    throw new HttpError(401, "unauthorized", "Chave do painel inválida.");
  }
}
