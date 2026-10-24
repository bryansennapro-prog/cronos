import type { Env } from "./env";
import { connectWithToken } from "./account";
import { HttpError, json } from "./http";
import { MetaError } from "./meta/client";
import { randomToken } from "./util";

/*
 * "Entrar com Instagram" (Instagram API com Login do Instagram, fluxo OAuth com código).
 *
 *   painel ── POST /api/oauth/start ──▶ Worker cria um "state" e devolve a URL de login
 *   painel abre a URL numa janela WebView2; a pessoa entra no Instagram e autoriza
 *   Instagram ──▶ GET /oauth/callback?code&state (no Worker): troca o código pelo token,
 *                 troca por token longo, grava a conta e mostra "pode fechar"
 *   painel ── GET /api/oauth/status?state ──▶ sabe que terminou (e com qual @)
 *
 * O App Secret fica só no Worker; o painel nunca vê o token.
 */

const STATE_TTL_MS = 10 * 60 * 1000;
const SCOPES = ["instagram_business_basic", "instagram_business_content_publish"];

interface StateRow {
  state: string;
  status: "PENDING" | "CONNECTED" | "ERROR";
  username: string | null;
  error: string | null;
  created_at: number;
}

const redirectUri = (env: Env) => `${env.PUBLIC_BASE_URL.replace(/\/+$/, "")}/oauth/callback`;

/** POST /api/oauth/start */
export async function handleOAuthStart(env: Env): Promise<Response> {
  if (!env.INSTAGRAM_APP_ID || !env.META_APP_SECRET) {
    throw new HttpError(409, "oauth_not_configured",
      "Login pelo app ainda não configurado: falta o ID do app do Instagram (INSTAGRAM_APP_ID) ou a chave secreta no Worker.");
  }
  const state = randomToken();
  await env.DB.prepare("INSERT INTO oauth_states (state, status, created_at) VALUES (?1, 'PENDING', ?2)")
    .bind(state, Date.now())
    .run();

  const url = new URL(env.OAUTH_AUTHORIZE_URL);
  url.searchParams.set("client_id", env.INSTAGRAM_APP_ID);
  url.searchParams.set("redirect_uri", redirectUri(env));
  url.searchParams.set("response_type", "code");
  url.searchParams.set("scope", SCOPES.join(","));
  url.searchParams.set("state", state);
  return json({ authorize_url: url.toString(), state, redirect_uri: redirectUri(env) }, 201);
}

/** GET /api/oauth/status?state=… */
export async function handleOAuthStatus(request: Request, env: Env): Promise<Response> {
  const state = new URL(request.url).searchParams.get("state") ?? "";
  const row = await env.DB.prepare("SELECT * FROM oauth_states WHERE state = ?1").bind(state).first<StateRow>();
  if (!row) throw new HttpError(404, "state_not_found", "Login não encontrado ou expirado.");
  return json({ status: row.status, username: row.username, error: row.error });
}

/** GET /oauth/callback?code=…&state=… — rota pública para onde o Instagram redireciona. */
export async function handleOAuthCallback(request: Request, env: Env): Promise<Response> {
  const params = new URL(request.url).searchParams;
  const state = params.get("state") ?? "";
  const row = await env.DB.prepare("SELECT * FROM oauth_states WHERE state = ?1").bind(state).first<StateRow>();
  if (!row || row.status !== "PENDING" || Date.now() - row.created_at > STATE_TTL_MS) {
    return page("Link de login inválido ou expirado", "Volte ao Cronos e clique em “Entrar com Instagram” de novo.", false);
  }

  const finish = async (status: StateRow["status"], username: string | null, error: string | null) =>
    env.DB.prepare("UPDATE oauth_states SET status = ?2, username = ?3, error = ?4 WHERE state = ?1")
      .bind(state, status, username, error)
      .run();

  if (params.get("error")) {
    const reason = params.get("error_reason") === "user_denied" ? "Você cancelou a autorização." : (params.get("error_description") ?? "A Meta recusou o login.");
    await finish("ERROR", null, reason);
    return page("Login não concluído", reason, false);
  }

  // O Instagram às vezes acrescenta "#_" ao final do código.
  const code = (params.get("code") ?? "").replace(/#_$/, "");
  if (!code) {
    await finish("ERROR", null, "A Meta não enviou o código de autorização.");
    return page("Login não concluído", "A Meta não enviou o código de autorização.", false);
  }

  try {
    const shortToken = await exchangeCode(env, code);
    const username = await connectWithToken(env, shortToken, "oauth");
    await finish("CONNECTED", username, null);
    return page(`Conta @${username} conectada`, "Pode fechar esta janela e voltar ao Cronos.", true);
  } catch (err) {
    const message = err instanceof HttpError || err instanceof MetaError ? err.message : `Erro: ${String(err)}`;
    await finish("ERROR", null, message);
    return page("Não foi possível conectar", message, false);
  }
}

async function exchangeCode(env: Env, code: string): Promise<string> {
  const res = await fetch(env.OAUTH_TOKEN_URL, {
    method: "POST",
    headers: { accept: "application/json" },
    body: new URLSearchParams({
      client_id: env.INSTAGRAM_APP_ID!,
      client_secret: env.META_APP_SECRET!,
      grant_type: "authorization_code",
      redirect_uri: redirectUri(env),
      code,
    }),
  });
  const body: any = await res.json().catch(() => ({}));
  // Formato atual: { access_token, user_id, permissions }; formato antigo: { data: [ {...} ] }.
  const token = body?.access_token ?? body?.data?.[0]?.access_token;
  if (!res.ok || !token) {
    const message = body?.error_message ?? body?.error?.message ?? `HTTP ${res.status}`;
    throw new HttpError(502, "oauth_exchange_failed", `A Meta não aceitou o código de login: ${message}`);
  }
  return token as string;
}

function page(title: string, message: string, ok: boolean): Response {
  const esc = (s: string) => s.replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]!);
  const html = `<!doctype html><html lang="pt-BR"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width">
<title>${esc(title)}</title>
<style>body{font-family:"Segoe UI",system-ui,sans-serif;display:grid;place-items:center;min-height:100vh;margin:0;background:#f3f3f3;color:#1b1b1b}
@media (prefers-color-scheme:dark){body{background:#202020;color:#fff}}
main{text-align:center;padding:24px;max-width:420px}.i{font-size:48px}</style></head>
<body><main data-oauth-result="${ok ? "ok" : "error"}"><div class="i">${ok ? "✅" : "⚠️"}</div><h1>${esc(title)}</h1><p>${esc(message)}</p></main></body></html>`;
  return new Response(html, { status: ok ? 200 : 400, headers: { "content-type": "text/html; charset=utf-8", "cache-control": "no-store" } });
}

/** Manutenção diária. */
export async function cleanupOAuthStates(env: Env): Promise<void> {
  await env.DB.prepare("DELETE FROM oauth_states WHERE created_at < ?1").bind(Date.now() - 24 * 60 * 60 * 1000).run();
}
