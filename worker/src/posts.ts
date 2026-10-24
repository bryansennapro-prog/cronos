import type { Env } from "./env";
import { HttpError, json, readJson } from "./http";
import { deletePostMedia } from "./uploads";
import { logEvent } from "./util";

export type PostType = "IMAGE" | "REELS" | "CAROUSEL" | "STORIES";
export type PostState =
  | "SCHEDULED"
  | "PREPARING"
  | "PROCESSING"
  | "READY"
  | "PUBLISHING"
  | "PUBLISHED"
  | "FAILED"
  | "CANCELED";

/** Estados em que o post ainda pode ser editado, remarcado ou cancelado (a Meta ainda não publicou). */
const EDITABLE_STATES: PostState[] = ["SCHEDULED", "PREPARING", "PROCESSING", "READY", "FAILED"];

export interface PostOptions {
  share_to_feed?: boolean;
  thumb_offset_ms?: number;
}

export interface PostRow {
  id: string;
  type: PostType;
  caption: string | null;
  options_json: string;
  scheduled_at: number;
  state: PostState;
  attempts: number;
  next_run_at: number | null;
  locked_until: number;
  ig_container_id: string | null;
  ig_media_id: string | null;
  ig_status: string | null;
  permalink: string | null;
  error: string | null;
  created_at: number;
  updated_at: number;
}

/** O container é criado este tempo antes do horário, para a Meta processar o vídeo a tempo. */
export const PREPARE_LEAD_MS = 15 * 60 * 1000;
const MAX_CAPTION = 2200;
const MAX_HASHTAGS = 30;
const MAX_MENTIONS = 20;
const MAX_SCHEDULE_AHEAD_MS = 75 * 24 * 60 * 60 * 1000;

const POST_TYPES: PostType[] = ["IMAGE", "REELS", "CAROUSEL", "STORIES"];

/** Quantas mídias cada tipo aceita (carrossel: limite da API, que é menor que o do app). */
const MEDIA_COUNT: Record<PostType, [number, number]> = { IMAGE: [1, 1], REELS: [1, 1], STORIES: [1, 1], CAROUSEL: [2, 10] };

const ACCEPTED_KINDS: Record<PostType, string[]> = {
  IMAGE: ["IMAGE"],
  REELS: ["VIDEO"],
  STORIES: ["IMAGE", "VIDEO"],
  CAROUSEL: ["IMAGE", "VIDEO"],
};

export function countHashtags(caption: string): number {
  return caption.match(/#[\p{L}\p{N}_]+/gu)?.length ?? 0;
}

export function countMentions(caption: string): number {
  return caption.match(/@[\p{L}\p{N}._]+/gu)?.length ?? 0;
}

export async function loadPost(env: Env, id: string): Promise<PostRow | null> {
  return env.DB.prepare("SELECT * FROM posts WHERE id = ?1").bind(id).first<PostRow>();
}

async function postView(env: Env, row: PostRow, withEvents = false) {
  const { results: media } = await env.DB.prepare(
    "SELECT position, r2_key, media_kind FROM post_media WHERE post_id = ?1 ORDER BY position",
  )
    .bind(row.id)
    .all<{ position: number; r2_key: string; media_kind: string }>();

  const view: Record<string, unknown> = {
    id: row.id,
    type: row.type,
    caption: row.caption,
    options: JSON.parse(row.options_json || "{}"),
    scheduled_at: new Date(row.scheduled_at).toISOString(),
    state: row.state,
    attempts: row.attempts,
    error: row.error,
    permalink: row.permalink,
    ig_media_id: row.ig_media_id,
    created_at: new Date(row.created_at).toISOString(),
    updated_at: new Date(row.updated_at).toISOString(),
    media: media.map((m) => ({ key: m.r2_key, kind: m.media_kind })),
  };

  if (withEvents) {
    const { results } = await env.DB.prepare(
      "SELECT kind, detail, created_at FROM events WHERE post_id = ?1 ORDER BY id DESC LIMIT 30",
    )
      .bind(row.id)
      .all<{ kind: string; detail: string | null; created_at: number }>();
    view.events = results.map((e) => ({
      kind: e.kind,
      detail: e.detail ? JSON.parse(e.detail) : null,
      at: new Date(e.created_at).toISOString(),
    }));
  }
  return view;
}

interface CreatePostBody {
  type?: unknown;
  caption?: unknown;
  media?: unknown;
  options?: unknown;
  scheduled_at?: unknown;
}

function validateCaption(raw: unknown): string {
  const caption = typeof raw === "string" ? raw.trim() : "";
  if ([...caption].length > MAX_CAPTION) {
    throw new HttpError(400, "caption_too_long", `A legenda passa de ${MAX_CAPTION} caracteres.`);
  }
  if (countHashtags(caption) > MAX_HASHTAGS) {
    throw new HttpError(400, "too_many_hashtags", `Use no máximo ${MAX_HASHTAGS} hashtags.`);
  }
  if (countMentions(caption) > MAX_MENTIONS) {
    throw new HttpError(400, "too_many_mentions", `Use no máximo ${MAX_MENTIONS} menções.`);
  }
  return caption;
}

/** Converte scheduled_at (ISO) em epoch ms; null/undefined = agora. */
function validateSchedule(raw: unknown, now: number): number {
  if (raw === undefined || raw === null) return now;
  const at = Date.parse(String(raw));
  if (!Number.isFinite(at)) throw new HttpError(400, "invalid_date", "scheduled_at inválido (use ISO 8601).");
  if (at < now - 60_000) throw new HttpError(400, "date_in_past", "O horário já passou.");
  if (at > now + MAX_SCHEDULE_AHEAD_MS) throw new HttpError(400, "date_too_far", "Agende com no máximo 75 dias.");
  return Math.max(at, now);
}

function parseOptions(type: PostType, raw: unknown, base: PostOptions = {}): PostOptions {
  if (type !== "REELS") return {};
  const r = (raw ?? {}) as Record<string, unknown>;
  const options: PostOptions = { ...base };
  if (r.share_to_feed !== undefined) options.share_to_feed = r.share_to_feed !== false;
  else if (options.share_to_feed === undefined) options.share_to_feed = true;
  if (r.thumb_offset_ms !== undefined) {
    const thumb = Number(r.thumb_offset_ms);
    if (Number.isFinite(thumb) && thumb >= 0) options.thumb_offset_ms = Math.round(thumb);
    else delete options.thumb_offset_ms;
  }
  return options;
}

/** POST /api/posts */
export async function handleCreatePost(
  request: Request,
  env: Env,
  ctx: ExecutionContext,
  runQueue: (env: Env) => Promise<void>,
): Promise<Response> {
  const body = await readJson<CreatePostBody>(request);

  const type = body.type as PostType;
  if (!POST_TYPES.includes(type)) {
    throw new HttpError(400, "invalid_type", "type deve ser IMAGE, REELS, CAROUSEL ou STORIES.");
  }

  // Stories não têm legenda pela API: descartamos em vez de recusar.
  const caption = type === "STORIES" ? "" : validateCaption(body.caption);

  const keys = (Array.isArray(body.media) ? body.media : []).map((m: any) => (typeof m?.key === "string" ? (m.key as string) : ""));
  const [min, max] = MEDIA_COUNT[type];
  if (keys.length < min || keys.length > max || keys.some((k) => !k)) {
    throw new HttpError(400, "invalid_media",
      min === max ? `Envie exatamente ${min} mídia em media: [{ key }].` : `Um carrossel tem de ${min} a ${max} itens.`);
  }
  if (new Set(keys).size !== keys.length) throw new HttpError(400, "duplicate_media", "A mesma mídia aparece duas vezes.");

  const kinds: string[] = [];
  for (const key of keys) {
    const upload = await env.DB.prepare("SELECT kind, status, post_id FROM uploads WHERE r2_key = ?1")
      .bind(key)
      .first<{ kind: string; status: string; post_id: string | null }>();
    if (!upload || upload.status !== "READY") {
      throw new HttpError(400, "media_not_ready", "Uma das mídias não foi enviada por completo.");
    }
    if (upload.post_id) throw new HttpError(409, "media_in_use", "Esta mídia já pertence a outro post.");
    if (!ACCEPTED_KINDS[type].includes(upload.kind)) {
      throw new HttpError(400, "media_kind_mismatch", type === "IMAGE" ? "Post de imagem precisa de um JPEG." : "Reel precisa de um vídeo.");
    }
    kinds.push(upload.kind);
  }

  const options = parseOptions(type, body.options);
  const now = Date.now();
  const scheduledAt = validateSchedule(body.scheduled_at, now);

  const id = crypto.randomUUID();
  const nextRunAt = Math.max(now, scheduledAt - PREPARE_LEAD_MS);

  // Lote atômico: cria o post e "reserva" as mídias. Se alguma já foi reservada por outro post
  // nesse meio-tempo, o UPDATE dela não altera nada e desfazemos tudo.
  const results = await env.DB.batch([
    env.DB.prepare(
      `INSERT INTO posts (id, type, caption, options_json, scheduled_at, state, attempts, next_run_at,
                          locked_until, created_at, updated_at)
       VALUES (?1, ?2, ?3, ?4, ?5, 'SCHEDULED', 0, ?6, 0, ?7, ?7)`,
    ).bind(id, type, caption || null, JSON.stringify(options), scheduledAt, nextRunAt, now),
    ...keys.map((key, i) =>
      env.DB.prepare("INSERT INTO post_media (post_id, position, r2_key, media_kind) VALUES (?1, ?2, ?3, ?4)")
        .bind(id, i, key, kinds[i]),
    ),
    ...keys.map((key) =>
      env.DB.prepare("UPDATE uploads SET post_id = ?1 WHERE r2_key = ?2 AND post_id IS NULL AND status = 'READY'")
        .bind(id, key),
    ),
  ]);
  const claimed = results.slice(1 + keys.length).every((r) => r.meta.changes === 1);
  if (!claimed) {
    await env.DB.batch([
      env.DB.prepare("UPDATE uploads SET post_id = NULL WHERE post_id = ?1").bind(id),
      env.DB.prepare("DELETE FROM post_media WHERE post_id = ?1").bind(id),
      env.DB.prepare("DELETE FROM posts WHERE id = ?1").bind(id),
    ]);
    throw new HttpError(409, "media_in_use", "Uma das mídias já pertence a outro post.");
  }

  await logEvent(env, id, "post.created", { type, scheduled_at: new Date(scheduledAt).toISOString() });
  if (nextRunAt <= now) ctx.waitUntil(runQueue(env));

  const row = (await loadPost(env, id))!;
  return json({ post: await postView(env, row) }, 201);
}

/**
 * GET /api/posts
 *   ?from=ISO&to=ISO   posts com horário (scheduled_at) no intervalo, em ordem de horário — calendário
 *   ?before=ISO        paginação do histórico (criados antes desta data), mais recentes primeiro
 *   ?state=A,B         filtra por estado
 *   ?limit=50
 */
export async function handleListPosts(request: Request, env: Env): Promise<Response> {
  const params = new URL(request.url).searchParams;
  const limit = Math.min(Math.max(Number(params.get("limit")) || 50, 1), 200);
  const where: string[] = [];
  const binds: unknown[] = [];
  const bind = (v: unknown) => {
    binds.push(v);
    return `?${binds.length}`;
  };

  const parseDate = (name: string): number | null => {
    const raw = params.get(name);
    if (!raw) return null;
    const t = Date.parse(raw);
    if (!Number.isFinite(t)) throw new HttpError(400, "invalid_date", `${name} inválido (use ISO 8601).`);
    return t;
  };

  const from = parseDate("from");
  const to = parseDate("to");
  const before = parseDate("before");
  if (from !== null) where.push(`scheduled_at >= ${bind(from)}`);
  if (to !== null) where.push(`scheduled_at < ${bind(to)}`);
  if (before !== null) where.push(`created_at < ${bind(before)}`);

  const states = (params.get("state") ?? "")
    .split(",")
    .map((s) => s.trim().toUpperCase())
    .filter(Boolean);
  if (states.length > 0) where.push(`state IN (${states.map((s) => bind(s)).join(", ")})`);

  const order = from !== null || to !== null ? "scheduled_at ASC" : "created_at DESC";
  const sql = `SELECT * FROM posts ${where.length ? `WHERE ${where.join(" AND ")}` : ""} ORDER BY ${order} LIMIT ${bind(limit)}`;
  const { results } = await env.DB.prepare(sql).bind(...binds).all<PostRow>();
  const posts = await Promise.all(results.map((r) => postView(env, r)));
  return json({ posts });
}

/** Trava o post para edição; falha se a fila estiver mexendo nele neste instante. */
async function lockForEdit(env: Env, id: string): Promise<PostRow> {
  const row = await loadPost(env, id);
  if (!row) throw new HttpError(404, "post_not_found", "Post não encontrado.");
  if (row.state === "PUBLISHED") throw new HttpError(409, "already_published", "Este post já foi publicado.");
  if (row.state === "CANCELED") throw new HttpError(409, "canceled", "Este post foi cancelado.");
  if (!EDITABLE_STATES.includes(row.state)) {
    throw new HttpError(409, "publishing", "O post está sendo publicado agora e não pode mais ser alterado.");
  }
  const now = Date.now();
  const lock = await env.DB.prepare(
    `UPDATE posts SET locked_until = ?2 WHERE id = ?1 AND locked_until < ?3 AND state = ?4`,
  )
    .bind(id, now + 60_000, now, row.state)
    .run();
  if (lock.meta.changes !== 1) {
    throw new HttpError(409, "busy", "O post está sendo processado neste instante. Tente de novo em alguns segundos.");
  }
  return row;
}

/** Descarta os containers dos itens de carrossel (serão recriados na próxima preparação). */
export async function resetChildContainers(env: Env, id: string): Promise<void> {
  await env.DB.prepare("UPDATE post_media SET ig_child_container_id = NULL WHERE post_id = ?1").bind(id).run();
}

async function hasMedia(env: Env, id: string): Promise<boolean> {
  return (await env.DB.prepare("SELECT 1 FROM uploads WHERE post_id = ?1 AND status = 'READY' LIMIT 1").bind(id).first()) !== null;
}

/**
 * PATCH /api/posts/:id  { scheduled_at?, caption?, options? }
 * Remarca e/ou edita. Se a Meta já tinha um container pronto, ele é descartado e recriado no novo horário.
 * Num post com falha, remarcar também o recoloca na fila.
 */
export async function handleUpdatePost(
  request: Request,
  env: Env,
  id: string,
  ctx: ExecutionContext,
  runQueue: (env: Env) => Promise<void>,
): Promise<Response> {
  const body = await readJson<{ scheduled_at?: unknown; caption?: unknown; options?: unknown }>(request);
  if (body.scheduled_at === undefined && body.caption === undefined && body.options === undefined) {
    throw new HttpError(400, "nothing_to_update", "Informe scheduled_at, caption ou options.");
  }

  const row = await lockForEdit(env, id);
  let runNow = false;
  try {
    const now = Date.now();
    const scheduledAt = body.scheduled_at !== undefined ? validateSchedule(body.scheduled_at, now) : Math.max(row.scheduled_at, now);
    const caption = body.caption !== undefined ? validateCaption(body.caption) || null : row.caption;
    const options = body.options !== undefined
      ? parseOptions(row.type, body.options, JSON.parse(row.options_json || "{}"))
      : JSON.parse(row.options_json || "{}");
    if (row.state === "FAILED" && !(await hasMedia(env, id))) {
      throw new HttpError(409, "media_expired", "A mídia deste post já foi apagada da nuvem. Crie o post de novo.");
    }

    const nextRunAt = Math.max(now, scheduledAt - PREPARE_LEAD_MS);
    // Qualquer mudança volta o post para SCHEDULED: legenda/opções novas exigem outro container,
    // e o horário novo pode estar a mais de 24h do container antigo.
    await env.DB.prepare(
      `UPDATE posts SET scheduled_at = ?2, caption = ?3, options_json = ?4, state = 'SCHEDULED', attempts = 0,
                        error = NULL, ig_container_id = NULL, ig_status = NULL, next_run_at = ?5, updated_at = ?6
       WHERE id = ?1`,
    )
      .bind(id, scheduledAt, caption, JSON.stringify(options), nextRunAt, now)
      .run();
    await resetChildContainers(env, id);
    await logEvent(env, id, "post.updated", {
      scheduled_at: new Date(scheduledAt).toISOString(),
      caption_changed: body.caption !== undefined,
      from_state: row.state,
    });
    runNow = nextRunAt <= now;
  } finally {
    await env.DB.prepare("UPDATE posts SET locked_until = 0 WHERE id = ?1").bind(id).run();
  }

  // Só depois de destravar: senão a fila encontra o post travado e o pula até o próximo cron.
  if (runNow) ctx.waitUntil(runQueue(env));
  return json({ post: await postView(env, (await loadPost(env, id))!) });
}

/** DELETE /api/posts/:id — cancela um post ainda não publicado e apaga a mídia da nuvem. */
export async function handleCancelPost(env: Env, id: string): Promise<Response> {
  await lockForEdit(env, id);
  try {
    const now = Date.now();
    await env.DB.prepare(
      `UPDATE posts SET state = 'CANCELED', next_run_at = NULL, error = NULL, updated_at = ?2 WHERE id = ?1`,
    )
      .bind(id, now)
      .run();
    await deletePostMedia(env, id);
    await logEvent(env, id, "post.canceled");
  } finally {
    await env.DB.prepare("UPDATE posts SET locked_until = 0 WHERE id = ?1").bind(id).run();
  }
  return json({ post: await postView(env, (await loadPost(env, id))!) });
}

/** Manutenção diária: posts com falha há mais de 7 dias perdem a mídia (não dá mais para tentar de novo). */
export async function cleanupStaleFailedMedia(env: Env): Promise<number> {
  const { results } = await env.DB.prepare(
    `SELECT DISTINCT p.id FROM posts p JOIN uploads u ON u.post_id = p.id
     WHERE p.state = 'FAILED' AND p.updated_at < ?1 LIMIT 50`,
  )
    .bind(Date.now() - 7 * 24 * 60 * 60 * 1000)
    .all<{ id: string }>();
  for (const { id } of results) await deletePostMedia(env, id);
  return results.length;
}

/** GET /api/posts/:id */
export async function handleGetPost(env: Env, id: string): Promise<Response> {
  const row = await loadPost(env, id);
  if (!row) throw new HttpError(404, "post_not_found", "Post não encontrado.");
  return json({ post: await postView(env, row, true) });
}

/** POST /api/posts/:id/retry — recomeça um post que falhou (a mídia continua no R2). */
export async function handleRetryPost(
  env: Env,
  id: string,
  ctx: ExecutionContext,
  runQueue: (env: Env) => Promise<void>,
): Promise<Response> {
  const row = await loadPost(env, id);
  if (!row) throw new HttpError(404, "post_not_found", "Post não encontrado.");
  if (row.state !== "FAILED") throw new HttpError(409, "not_failed", "Só posts com falha podem ser tentados de novo.");
  if (!(await hasMedia(env, id))) {
    throw new HttpError(409, "media_expired", "A mídia deste post já foi apagada da nuvem. Crie o post de novo.");
  }

  const now = Date.now();
  const scheduledAt = Math.max(now, row.scheduled_at);
  await env.DB.prepare(
    `UPDATE posts SET state = 'SCHEDULED', attempts = 0, error = NULL, ig_container_id = NULL, ig_status = NULL,
                      scheduled_at = ?2, next_run_at = ?3, locked_until = 0, updated_at = ?4
     WHERE id = ?1 AND state = 'FAILED'`,
  )
    .bind(id, scheduledAt, Math.max(now, scheduledAt - PREPARE_LEAD_MS), now)
    .run();
  await resetChildContainers(env, id);
  await logEvent(env, id, "post.retry");
  ctx.waitUntil(runQueue(env));

  return json({ post: await postView(env, (await loadPost(env, id))!) });
}
