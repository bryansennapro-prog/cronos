import type { Env } from "./env";
import { getAccessToken } from "./account";
import { HttpError } from "./http";
import { MetaClient, MetaError } from "./meta/client";
import { loadPost, PREPARE_LEAD_MS, resetChildContainers, type PostOptions, type PostRow, type PostState } from "./posts";
import { deletePostMedia } from "./uploads";
import { logEvent, sleep } from "./util";

/*
 * Máquina de estados de publicação. Cada chamada de runQueue avança os posts vencidos o máximo
 * possível dentro do orçamento de tempo; o que precisa esperar (vídeo processando, horário futuro)
 * ganha um next_run_at e é retomado pelo cron de minuto em minuto.
 *
 *   SCHEDULED → PREPARING → PROCESSING → READY → PUBLISHING → PUBLISHED
 *                    (qualquer erro) → nova tentativa com espera crescente → FAILED
 */

const ACTIVE_STATES: PostState[] = ["SCHEDULED", "PREPARING", "PROCESSING", "READY", "PUBLISHING"];
const LOCK_MS = 5 * 60 * 1000;
const BACKOFF_MS = [60_000, 5 * 60_000, 15 * 60_000];
const MAX_ATTEMPTS = BACKOFF_MS.length;
const POLL_INTERVAL_MS = 4_000;
const NEXT_POLL_MS = 60_000;
const LIMIT_WAIT_MS = 10 * 60_000;
const DEFAULT_BUDGET_MS = 25_000;

class PermanentError extends Error {}

export async function runQueue(env: Env, budgetMs = DEFAULT_BUDGET_MS): Promise<void> {
  const deadline = Date.now() + budgetMs;
  const now = Date.now();
  const { results } = await env.DB.prepare(
    `SELECT id FROM posts
     WHERE state IN (${ACTIVE_STATES.map((s) => `'${s}'`).join(",")})
       AND next_run_at <= ?1 AND locked_until < ?1
     ORDER BY next_run_at LIMIT 10`,
  )
    .bind(now)
    .all<{ id: string }>();

  for (const { id } of results) {
    if (Date.now() > deadline - 2_000) break;
    try {
      await processPost(env, id, deadline);
    } catch (err) {
      console.error("queue: erro inesperado", id, err);
    }
  }
}

async function update(env: Env, id: string, fields: Partial<Omit<PostRow, "id">>): Promise<void> {
  const entries = Object.entries({ ...fields, updated_at: Date.now() });
  const sets = entries.map(([k], i) => `${k} = ?${i + 2}`).join(", ");
  await env.DB.prepare(`UPDATE posts SET ${sets} WHERE id = ?1`)
    .bind(id, ...entries.map(([, v]) => v ?? null))
    .run();
}

async function processPost(env: Env, id: string, deadline: number): Promise<void> {
  const now = Date.now();
  const lock = await env.DB.prepare("UPDATE posts SET locked_until = ?2 WHERE id = ?1 AND locked_until < ?3")
    .bind(id, now + LOCK_MS, now)
    .run();
  if (lock.meta.changes !== 1) return; // outra execução já está cuidando deste post

  let post = await loadPost(env, id);
  try {
    if (!post) return;
    const { token, igUserId } = await getAccessToken(env);
    const meta = new MetaClient(env);

    for (;;) {
      post = (await loadPost(env, id))!;
      const t = Date.now();

      switch (post.state) {
        case "SCHEDULED": {
          if (t < post.scheduled_at - PREPARE_LEAD_MS) {
            await update(env, id, { next_run_at: post.scheduled_at - PREPARE_LEAD_MS });
            return;
          }
          await update(env, id, { state: "PREPARING" });
          continue;
        }

        case "PREPARING": {
          if (post.type === "CAROUSEL") {
            // Carrossel: primeiro um container por item; o do carrossel só depois que todos ficarem prontos.
            const created = await createCarouselItems(env, meta, post, igUserId, token);
            await update(env, id, { state: "PROCESSING", ig_container_id: null, ig_status: "IN_PROGRESS", next_run_at: t });
            await logEvent(env, id, "carousel.items_created", { created });
            continue;
          }
          const containerId = await createContainer(env, meta, post, igUserId, token);
          await update(env, id, { state: "PROCESSING", ig_container_id: containerId, ig_status: "IN_PROGRESS", next_run_at: t });
          await logEvent(env, id, "container.created", { container_id: containerId });
          continue;
        }

        case "PROCESSING": {
          if (post.type === "CAROUSEL" && !post.ig_container_id) {
            const children = await loadChildren(env, id);
            const statuses = await Promise.all(children.map((c) => meta.getContainerStatus(c.ig_child_container_id!, token)));
            const failedAt = statuses.findIndex((s) => s.statusCode === "ERROR");
            if (failedAt >= 0) {
              throw new PermanentError(`Item ${failedAt + 1} do carrossel: ${describeContainerError(statuses[failedAt]!.status)}`);
            }
            if (statuses.some((s) => s.statusCode === "EXPIRED")) {
              await resetChildContainers(env, id);
              await update(env, id, { state: "PREPARING", ig_status: null });
              await logEvent(env, id, "container.expired");
              continue;
            }
            if (statuses.every((s) => s.statusCode === "FINISHED")) {
              const parentId = await meta.createContainer(igUserId, token, {
                kind: "CAROUSEL",
                children: children.map((c) => c.ig_child_container_id!),
                caption: post.caption ?? undefined,
              });
              await update(env, id, { ig_container_id: parentId, ig_status: "IN_PROGRESS" });
              await logEvent(env, id, "container.created", { container_id: parentId, items: children.length });
              continue;
            }
            if (Date.now() + POLL_INTERVAL_MS < deadline - 3_000) {
              await sleep(POLL_INTERVAL_MS);
              continue;
            }
            await update(env, id, { next_run_at: Date.now() + NEXT_POLL_MS });
            return;
          }

          const { statusCode, status } = await meta.getContainerStatus(post.ig_container_id!, token);
          await update(env, id, { ig_status: statusCode });
          if (statusCode === "FINISHED") {
            await update(env, id, { state: "READY" });
            await logEvent(env, id, "container.ready");
            continue;
          }
          if (statusCode === "IN_PROGRESS") {
            if (Date.now() + POLL_INTERVAL_MS < deadline - 3_000) {
              await sleep(POLL_INTERVAL_MS);
              continue;
            }
            await update(env, id, { next_run_at: Date.now() + NEXT_POLL_MS });
            return;
          }
          if (statusCode === "EXPIRED") {
            // Passou 24h sem publicar: cria outro container (e outros itens, se for carrossel).
            await resetChildContainers(env, id);
            await update(env, id, { state: "PREPARING", ig_container_id: null, ig_status: null });
            await logEvent(env, id, "container.expired");
            continue;
          }
          throw new PermanentError(describeContainerError(status));
        }

        case "READY": {
          if (t < post.scheduled_at) {
            await update(env, id, { next_run_at: post.scheduled_at });
            return;
          }
          const limit = await meta.getPublishingLimit(igUserId, token);
          if (limit.quota_total !== undefined && limit.quota_usage >= limit.quota_total) {
            await update(env, id, { next_run_at: Date.now() + LIMIT_WAIT_MS, error: "Limite de publicação da Meta atingido; tentando de novo em 10 min." });
            await logEvent(env, id, "publish.limit_reached", limit);
            return;
          }
          await update(env, id, { state: "PUBLISHING", error: null });
          continue;
        }

        case "PUBLISHING": {
          // Numa nova tentativa, confere antes se a publicação anterior não chegou a acontecer.
          if (post.attempts > 0) {
            const { statusCode } = await meta.getContainerStatus(post.ig_container_id!, token);
            if (statusCode === "PUBLISHED") {
              await finish(env, id, null, null);
              return;
            }
          }
          const mediaId = await meta.publishContainer(igUserId, post.ig_container_id!, token);
          await update(env, id, { ig_media_id: mediaId });
          let permalink: string | null = null;
          try {
            permalink = (await meta.getPermalink(mediaId, token)) ?? null;
          } catch {
            // O post já saiu; o link é só conveniência.
          }
          await finish(env, id, mediaId, permalink);
          return;
        }

        default:
          return;
      }
    }
  } catch (err) {
    await handleFailure(env, id, post, err);
  } finally {
    await env.DB.prepare("UPDATE posts SET locked_until = 0 WHERE id = ?1").bind(id).run();
  }
}

interface ChildRow {
  position: number;
  r2_key: string;
  media_kind: "IMAGE" | "VIDEO";
  ig_child_container_id: string | null;
}

async function loadChildren(env: Env, postId: string): Promise<ChildRow[]> {
  const { results } = await env.DB.prepare(
    "SELECT position, r2_key, media_kind, ig_child_container_id FROM post_media WHERE post_id = ?1 ORDER BY position",
  )
    .bind(postId)
    .all<ChildRow>();
  if (results.length === 0) throw new PermanentError("O post não tem mídia.");
  return results;
}

const mediaUrl = (env: Env, key: string) => `${env.PUBLIC_BASE_URL.replace(/\/+$/, "")}/m/${key}`;

const urlParam = (env: Env, child: ChildRow) =>
  child.media_kind === "VIDEO" ? { videoUrl: mediaUrl(env, child.r2_key) } : { imageUrl: mediaUrl(env, child.r2_key) };

async function createContainer(env: Env, meta: MetaClient, post: PostRow, igUserId: string, token: string): Promise<string> {
  const [media] = await loadChildren(env, post.id);
  const caption = post.caption ?? undefined;
  const options = JSON.parse(post.options_json || "{}") as PostOptions;

  switch (post.type) {
    case "IMAGE":
      return meta.createContainer(igUserId, token, { kind: "IMAGE", imageUrl: mediaUrl(env, media!.r2_key), caption });
    case "STORIES":
      return meta.createContainer(igUserId, token, { kind: "STORIES", ...urlParam(env, media!) });
    default:
      return meta.createContainer(igUserId, token, {
        kind: "REELS",
        videoUrl: mediaUrl(env, media!.r2_key),
        caption,
        shareToFeed: options.share_to_feed,
        thumbOffsetMs: options.thumb_offset_ms,
      });
  }
}

/** Cria (ou completa) os containers dos itens do carrossel. Devolve quantos foram criados agora. */
async function createCarouselItems(env: Env, meta: MetaClient, post: PostRow, igUserId: string, token: string): Promise<number> {
  let created = 0;
  for (const child of await loadChildren(env, post.id)) {
    if (child.ig_child_container_id) continue;
    const childId = await meta.createContainer(igUserId, token, { kind: "CAROUSEL_ITEM", ...urlParam(env, child) });
    await env.DB.prepare("UPDATE post_media SET ig_child_container_id = ?3 WHERE post_id = ?1 AND position = ?2")
      .bind(post.id, child.position, childId)
      .run();
    created++;
  }
  return created;
}

async function finish(env: Env, id: string, mediaId: string | null, permalink: string | null): Promise<void> {
  await update(env, id, {
    state: "PUBLISHED",
    ig_media_id: mediaId,
    permalink,
    ig_status: "PUBLISHED",
    error: null,
    next_run_at: null,
  });
  await logEvent(env, id, "post.published", { media_id: mediaId, permalink });
  await deletePostMedia(env, id);
}

async function handleFailure(env: Env, id: string, post: PostRow | null, err: unknown): Promise<void> {
  const message =
    err instanceof MetaError
      ? `Meta: ${err.userMessage ?? err.message}`
      : err instanceof HttpError || err instanceof PermanentError
        ? err.message
        : `Erro: ${String(err)}`;
  const retriable = err instanceof MetaError ? err.retriable : !(err instanceof HttpError || err instanceof PermanentError);
  const attempts = (post?.attempts ?? 0) + 1;
  const detail = err instanceof MetaError ? err.toJSON() : { message };

  if (retriable && attempts < MAX_ATTEMPTS + 1) {
    const wait = BACKOFF_MS[attempts - 1] ?? BACKOFF_MS[BACKOFF_MS.length - 1]!;
    await update(env, id, { attempts, error: message, next_run_at: Date.now() + wait });
    await logEvent(env, id, "post.retry_scheduled", { ...detail, attempt: attempts, wait_ms: wait, state: post?.state });
    return;
  }

  await update(env, id, { state: "FAILED", attempts, error: message, next_run_at: null });
  await logEvent(env, id, "post.failed", { ...detail, state: post?.state });
}

function describeContainerError(status?: string): string {
  const code = status?.match(/\d{7}/)?.[0];
  const known: Record<string, string> = {
    "2207026": "A Meta não aceitou o formato do vídeo. Converta para MP4 (H.264 + AAC).",
    "2207052": "A Meta não conseguiu baixar a mídia do Worker.",
    "2207004": "A imagem é grande demais (máx. 8 MB).",
    "2207009": "Proporção da imagem não aceita (use entre 4:5 e 1.91:1).",
    "2207003": "A Meta demorou demais para baixar a mídia.",
  };
  if (code && known[code]) return `${known[code]} (código ${code})`;
  return `A Meta recusou a mídia${status ? `: ${status}` : "."}`;
}
