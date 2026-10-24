import type { Env } from "./env";
import { HttpError, json, readJson } from "./http";
import { open, seal } from "./crypto";
import { MetaClient, MetaError, type MetaProfile } from "./meta/client";

const DAY_MS = 24 * 60 * 60 * 1000;
const ESTIMATED_LIFETIME_MS = 60 * DAY_MS;
const REFRESH_WHEN_LEFT_MS = 10 * DAY_MS;
const MIN_TOKEN_AGE_MS = DAY_MS; // a Meta só renova tokens com mais de 24h

interface AccountRow {
  ig_user_id: string;
  username: string;
  account_type: string | null;
  profile_picture_url: string | null;
  token_ciphertext: string;
  token_iv: string;
  token_expires_at: number;
  token_expiry_known: number;
  token_obtained_at: number;
  last_error: string | null;
  created_at: number;
  updated_at: number;
}

async function loadAccount(env: Env): Promise<AccountRow | null> {
  return env.DB.prepare("SELECT * FROM account WHERE id = 1").first<AccountRow>();
}

/** Token da Meta em texto puro. Usado pela fila de publicação nas próximas fases. */
export async function getAccessToken(env: Env): Promise<{ token: string; igUserId: string }> {
  const row = await loadAccount(env);
  if (!row) throw new HttpError(409, "not_connected", "Nenhuma conta Instagram conectada.");
  const token = await open({ ciphertext: row.token_ciphertext, iv: row.token_iv }, env.TOKEN_ENC_KEY);
  return { token, igUserId: row.ig_user_id };
}

async function saveAccount(
  env: Env,
  profile: MetaProfile,
  token: string,
  expiresAt: number,
  expiryKnown: boolean,
): Promise<void> {
  const sealed = await seal(token, env.TOKEN_ENC_KEY);
  const now = Date.now();
  await env.DB.prepare(
    `INSERT INTO account (id, ig_user_id, username, account_type, profile_picture_url,
                          token_ciphertext, token_iv, token_expires_at, token_expiry_known,
                          token_obtained_at, last_error, created_at, updated_at)
     VALUES (1, ?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, NULL, ?9, ?9)
     ON CONFLICT (id) DO UPDATE SET
       ig_user_id = excluded.ig_user_id,
       username = excluded.username,
       account_type = excluded.account_type,
       profile_picture_url = excluded.profile_picture_url,
       token_ciphertext = excluded.token_ciphertext,
       token_iv = excluded.token_iv,
       token_expires_at = excluded.token_expires_at,
       token_expiry_known = excluded.token_expiry_known,
       token_obtained_at = excluded.token_obtained_at,
       last_error = NULL,
       updated_at = excluded.updated_at`,
  )
    .bind(
      profile.user_id,
      profile.username,
      profile.account_type ?? null,
      profile.profile_picture_url ?? null,
      sealed.ciphertext,
      sealed.iv,
      expiresAt,
      expiryKnown ? 1 : 0,
      now,
    )
    .run();
}

async function logEvent(env: Env, kind: string, detail?: unknown): Promise<void> {
  await env.DB.prepare("INSERT INTO events (post_id, kind, detail, created_at) VALUES (NULL, ?1, ?2, ?3)")
    .bind(kind, detail === undefined ? null : JSON.stringify(detail), Date.now())
    .run();
}

function metaToHttp(err: unknown, fallbackMessage: string): never {
  if (err instanceof MetaError) {
    throw new HttpError(502, "meta_error", `${fallbackMessage} ${err.message}`, err.toJSON());
  }
  throw err;
}

function accountView(row: AccountRow) {
  const now = Date.now();
  return {
    ig_user_id: row.ig_user_id,
    username: row.username,
    account_type: row.account_type,
    profile_picture_url: row.profile_picture_url,
    token: {
      expires_at: new Date(row.token_expires_at).toISOString(),
      expiry_known: row.token_expiry_known === 1,
      days_left: Math.max(0, Math.floor((row.token_expires_at - now) / DAY_MS)),
      obtained_at: new Date(row.token_obtained_at).toISOString(),
      last_error: row.last_error,
    },
  };
}

// ---------- Rotas ----------

/** GET /api/account */
export async function handleGetAccount(env: Env): Promise<Response> {
  const row = await loadAccount(env);
  if (!row) throw new HttpError(404, "not_connected", "Nenhuma conta Instagram conectada.");

  const meta = new MetaClient(env);
  const token = await open({ ciphertext: row.token_ciphertext, iv: row.token_iv }, env.TOKEN_ENC_KEY);

  let publishingLimit: unknown;
  try {
    publishingLimit = await meta.getPublishingLimit(row.ig_user_id, token);
  } catch (err) {
    if (!(err instanceof MetaError)) throw err;
    publishingLimit = { error: err.toJSON() };
  }

  return json({ account: accountView(row), publishing_limit: publishingLimit });
}

/** PUT /api/account/token  { "access_token": "..." } */
export async function handlePutToken(request: Request, env: Env): Promise<Response> {
  const body = await readJson<{ access_token?: unknown }>(request);
  const input = typeof body.access_token === "string" ? body.access_token.trim() : "";
  if (input.length < 20) {
    throw new HttpError(400, "invalid_token", "Envie o campo access_token com o token da Meta.");
  }

  await connectWithToken(env, input, "manual");
  const row = (await loadAccount(env))!;
  return json({ account: accountView(row) }, 200);
}

/**
 * Valida o token na Meta, troca por um token longo quando possível e grava a conta.
 * Usado pelo token colado à mão e pelo login "Entrar com Instagram" (OAuth).
 * Devolve o @usuario conectado.
 */
export async function connectWithToken(env: Env, input: string, source: "manual" | "oauth"): Promise<string> {
  const meta = new MetaClient(env);
  const now = Date.now();

  let token = input;
  let expiresAt = now + ESTIMATED_LIFETIME_MS;
  let expiryKnown = false;

  // Se o App Secret estiver configurado, tenta trocar por um token longo (caso seja um token curto).
  // Tokens gerados no painel da Meta já são longos e a troca falha; nesse caso seguimos com o original.
  if (env.META_APP_SECRET) {
    try {
      const long = await meta.exchangeForLongLived(input, env.META_APP_SECRET);
      token = long.access_token;
      expiresAt = now + long.expires_in * 1000;
      expiryKnown = true;
    } catch (err) {
      if (!(err instanceof MetaError)) throw err;
    }
  }

  let profile: MetaProfile;
  try {
    profile = await meta.getMe(token);
  } catch (err) {
    metaToHttp(err, "A Meta recusou o token.");
  }
  if (!profile.user_id || !profile.username) {
    throw new HttpError(502, "meta_error", "A Meta não retornou user_id/username para este token.");
  }

  await saveAccount(env, profile, token, expiresAt, expiryKnown);
  await logEvent(env, "account.connected", { username: profile.username, expiry_known: expiryKnown, source });
  return profile.username;
}

/** DELETE /api/account */
export async function handleDeleteAccount(env: Env): Promise<Response> {
  await env.DB.prepare("DELETE FROM account WHERE id = 1").run();
  await logEvent(env, "account.disconnected");
  return json({ ok: true });
}

// ---------- Manutenção (cron diário) ----------

export async function refreshTokenIfNeeded(env: Env): Promise<void> {
  const row = await loadAccount(env);
  if (!row) return;

  const now = Date.now();
  const oldEnough = now - row.token_obtained_at >= MIN_TOKEN_AGE_MS;
  const nearExpiry = row.token_expires_at - now < REFRESH_WHEN_LEFT_MS;
  if (!oldEnough || (row.token_expiry_known === 1 && !nearExpiry)) return;

  const meta = new MetaClient(env);
  try {
    const current = await open({ ciphertext: row.token_ciphertext, iv: row.token_iv }, env.TOKEN_ENC_KEY);
    const fresh = await meta.refreshLongLived(current);
    const profile: MetaProfile = {
      user_id: row.ig_user_id,
      username: row.username,
      account_type: row.account_type ?? undefined,
      profile_picture_url: row.profile_picture_url ?? undefined,
    };
    await saveAccount(env, profile, fresh.access_token, now + fresh.expires_in * 1000, true);
    await logEvent(env, "token.refreshed", { expires_in: fresh.expires_in });
  } catch (err) {
    const detail = err instanceof MetaError ? err.toJSON() : { message: String(err) };
    await env.DB.prepare("UPDATE account SET last_error = ?1, updated_at = ?2 WHERE id = 1")
      .bind(`Falha ao renovar token: ${detail.message}`, now)
      .run();
    await logEvent(env, "token.refresh_failed", detail);
  }
}
