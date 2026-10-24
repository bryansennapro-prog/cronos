import type { Env } from "./env";
import { requirePanelKey } from "./auth";
import { HttpError, errorResponse, json } from "./http";
import { handleDeleteAccount, handleGetAccount, handlePutToken, refreshTokenIfNeeded } from "./account";
import { handleAiAnalysis } from "./ai";
import { handleGetInsights } from "./insights";
import { handleMedia } from "./media";
import { cleanupOAuthStates, handleOAuthCallback, handleOAuthStart, handleOAuthStatus } from "./oauth";
import { handleUpdates } from "./updates";
import {
  cleanupStaleFailedMedia,
  handleCancelPost,
  handleCreatePost,
  handleGetPost,
  handleListPosts,
  handleRetryPost,
  handleUpdatePost,
} from "./posts";
import { runQueue } from "./queue";
import {
  cleanupOrphanUploads,
  handleCompleteUpload,
  handleCreateUpload,
  handleDeleteUpload,
  handleUploadPart,
} from "./uploads";

const VERSION = "0.7.0";
const EVERY_MINUTE = "* * * * *";
const DAILY = "0 6 * * *";

async function route(request: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
  const { pathname } = new URL(request.url);
  const method = request.method;

  if (pathname.startsWith("/m/")) return handleMedia(request, env, pathname.slice(3));
  if (pathname.startsWith("/updates/")) return handleUpdates(request, env, pathname.slice("/updates/".length));
  if (pathname === "/oauth/callback" && method === "GET") return handleOAuthCallback(request, env);

  if (pathname === "/api/health" && method === "GET") {
    return json({ ok: true, version: VERSION });
  }

  if (pathname.startsWith("/api/")) {
    await requirePanelKey(request, env);

    if (pathname === "/api/account" && method === "GET") return handleGetAccount(env);
    if (pathname === "/api/account" && method === "DELETE") return handleDeleteAccount(env);
    if (pathname === "/api/account/token" && method === "PUT") return handlePutToken(request, env);
    if (pathname === "/api/oauth/start" && method === "POST") return handleOAuthStart(env);
    if (pathname === "/api/oauth/status" && method === "GET") return handleOAuthStatus(request, env);
    if (pathname === "/api/insights" && method === "GET") return handleGetInsights(request, env);
    if (pathname === "/api/insights/ai" && method === "POST") return handleAiAnalysis(request, env);

    if (pathname === "/api/uploads" && method === "POST") return handleCreateUpload(request, env);
    if (pathname === "/api/uploads" && method === "DELETE") return handleDeleteUpload(request, env);
    if (pathname === "/api/uploads/part" && method === "PUT") return handleUploadPart(request, env);
    if (pathname === "/api/uploads/complete" && method === "POST") return handleCompleteUpload(request, env);

    const queue = (e: Env) => runQueue(e);
    if (pathname === "/api/posts" && method === "POST") return handleCreatePost(request, env, ctx, queue);
    if (pathname === "/api/posts" && method === "GET") return handleListPosts(request, env);
    const postMatch = /^\/api\/posts\/([0-9a-f-]{36})(\/retry)?$/.exec(pathname);
    if (postMatch) {
      const [, id, retry] = postMatch;
      if (!retry && method === "GET") return handleGetPost(env, id!);
      if (!retry && method === "PATCH") return handleUpdatePost(request, env, id!, ctx, queue);
      if (!retry && method === "DELETE") return handleCancelPost(env, id!);
      if (retry && method === "POST") return handleRetryPost(env, id!, ctx, queue);
    }
  }

  throw new HttpError(404, "not_found", "Rota não encontrada.");
}

export default {
  async fetch(request, env, ctx): Promise<Response> {
    try {
      return await route(request, env, ctx);
    } catch (err) {
      return errorResponse(err);
    }
  },

  async scheduled(controller, env, ctx): Promise<void> {
    if (controller.cron === EVERY_MINUTE) {
      ctx.waitUntil(runQueue(env, 50_000));
    } else if (controller.cron === DAILY) {
      ctx.waitUntil(Promise.all([
        refreshTokenIfNeeded(env),
        cleanupOrphanUploads(env),
        cleanupStaleFailedMedia(env),
        cleanupOAuthStates(env),
      ]));
    }
  },
} satisfies ExportedHandler<Env>;
