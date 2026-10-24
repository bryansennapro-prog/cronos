import type { Env } from "./env";
import { HttpError, json, readJson } from "./http";
import { randomToken } from "./util";

/** Partes de 50 MB: abaixo do limite de 100 MB por requisição do plano grátis. */
export const PART_SIZE = 50 * 1024 * 1024;
const ORPHAN_AGE_MS = 48 * 60 * 60 * 1000;

const TYPES: Record<string, { kind: "IMAGE" | "VIDEO"; ext: string; maxBytes: number }> = {
  "image/jpeg": { kind: "IMAGE", ext: "jpg", maxBytes: 8 * 1024 * 1024 },
  "video/mp4": { kind: "VIDEO", ext: "mp4", maxBytes: 1024 * 1024 * 1024 },
  "video/quicktime": { kind: "VIDEO", ext: "mov", maxBytes: 1024 * 1024 * 1024 },
};

export const MEDIA_KEY_PATTERN = /^(img|vid)\/[A-Za-z0-9_-]{43}\.(jpg|mp4|mov)$/;

interface UploadRow {
  r2_key: string;
  content_type: string;
  size_bytes: number;
  kind: "IMAGE" | "VIDEO";
  status: "UPLOADING" | "READY";
  upload_id: string | null;
  post_id: string | null;
}

async function loadUpload(env: Env, key: string | null): Promise<UploadRow> {
  if (!key || !MEDIA_KEY_PATTERN.test(key)) throw new HttpError(400, "invalid_key", "Chave de upload inválida.");
  const row = await env.DB.prepare("SELECT * FROM uploads WHERE r2_key = ?1").bind(key).first<UploadRow>();
  if (!row) throw new HttpError(404, "upload_not_found", "Upload não encontrado.");
  return row;
}

/** POST /api/uploads  { content_type, size_bytes, file_name? } */
export async function handleCreateUpload(request: Request, env: Env): Promise<Response> {
  const body = await readJson<{ content_type?: unknown; size_bytes?: unknown; file_name?: unknown }>(request);
  const type = typeof body.content_type === "string" ? TYPES[body.content_type] : undefined;
  if (!type) {
    throw new HttpError(400, "unsupported_type", "Tipo não suportado. Envie JPEG, MP4 ou MOV (o painel converte antes).");
  }
  const size = Number(body.size_bytes);
  if (!Number.isSafeInteger(size) || size <= 0) throw new HttpError(400, "invalid_size", "size_bytes inválido.");
  if (size > type.maxBytes) {
    throw new HttpError(413, "too_large", `Arquivo grande demais (máx. ${Math.round(type.maxBytes / 1024 / 1024)} MB).`);
  }

  const key = `${type.kind === "IMAGE" ? "img" : "vid"}/${randomToken()}.${type.ext}`;
  const fileName = typeof body.file_name === "string" ? body.file_name.slice(0, 200) : null;
  const mpu = await env.MEDIA.createMultipartUpload(key, {
    httpMetadata: { contentType: body.content_type as string },
  });

  await env.DB.prepare(
    `INSERT INTO uploads (r2_key, content_type, size_bytes, kind, status, upload_id, file_name, created_at)
     VALUES (?1, ?2, ?3, ?4, 'UPLOADING', ?5, ?6, ?7)`,
  )
    .bind(key, body.content_type, size, type.kind, mpu.uploadId, fileName, Date.now())
    .run();

  return json(
    { key, upload_id: mpu.uploadId, part_size: PART_SIZE, part_count: Math.ceil(size / PART_SIZE) },
    201,
  );
}

/** PUT /api/uploads/part?key=…&part=N   (corpo = bytes da parte) */
export async function handleUploadPart(request: Request, env: Env): Promise<Response> {
  const url = new URL(request.url);
  const row = await loadUpload(env, url.searchParams.get("key"));
  if (row.status !== "UPLOADING" || !row.upload_id) {
    throw new HttpError(409, "upload_closed", "Este upload já foi concluído.");
  }

  const partNumber = Number(url.searchParams.get("part"));
  const partCount = Math.ceil(row.size_bytes / PART_SIZE);
  if (!Number.isInteger(partNumber) || partNumber < 1 || partNumber > partCount) {
    throw new HttpError(400, "invalid_part", `Parte deve estar entre 1 e ${partCount}.`);
  }

  const expected = partNumber < partCount ? PART_SIZE : row.size_bytes - PART_SIZE * (partCount - 1);
  const length = Number(request.headers.get("content-length"));
  if (length !== expected || !request.body) {
    throw new HttpError(400, "invalid_part_size", `A parte ${partNumber} deve ter ${expected} bytes.`);
  }

  // FixedLengthStream informa o tamanho ao R2 sem carregar a parte na memória.
  const { readable, writable } = new FixedLengthStream(length);
  const pipe = request.body.pipeTo(writable);
  const mpu = env.MEDIA.resumeMultipartUpload(row.r2_key, row.upload_id);
  const part = await mpu.uploadPart(partNumber, readable);
  await pipe;

  return json({ part_number: part.partNumber, etag: part.etag });
}

/** POST /api/uploads/complete  { key, parts: [{ part_number, etag }] } */
export async function handleCompleteUpload(request: Request, env: Env): Promise<Response> {
  const body = await readJson<{ key?: unknown; parts?: unknown }>(request);
  const row = await loadUpload(env, typeof body.key === "string" ? body.key : null);
  if (row.status === "READY") return json({ key: row.r2_key, size_bytes: row.size_bytes, kind: row.kind });
  if (!row.upload_id) throw new HttpError(409, "upload_closed", "Upload sem multipart ativo.");

  const parts = Array.isArray(body.parts) ? body.parts : [];
  const uploaded: R2UploadedPart[] = parts.map((p: any) => ({ partNumber: Number(p?.part_number), etag: String(p?.etag ?? "") }));
  const partCount = Math.ceil(row.size_bytes / PART_SIZE);
  if (uploaded.length !== partCount || uploaded.some((p) => !p.etag)) {
    throw new HttpError(400, "invalid_parts", `Informe as ${partCount} partes com seus etags.`);
  }

  const mpu = env.MEDIA.resumeMultipartUpload(row.r2_key, row.upload_id);
  const obj = await mpu.complete(uploaded.sort((a, b) => a.partNumber - b.partNumber));
  if (obj.size !== row.size_bytes) {
    await env.MEDIA.delete(row.r2_key);
    await env.DB.prepare("DELETE FROM uploads WHERE r2_key = ?1").bind(row.r2_key).run();
    throw new HttpError(422, "size_mismatch", "O arquivo recebido não tem o tamanho informado. Envie de novo.");
  }

  await env.DB.prepare("UPDATE uploads SET status = 'READY', upload_id = NULL WHERE r2_key = ?1").bind(row.r2_key).run();
  return json({ key: row.r2_key, size_bytes: row.size_bytes, kind: row.kind });
}

/** DELETE /api/uploads?key=…  (só uploads ainda não usados em um post) */
export async function handleDeleteUpload(request: Request, env: Env): Promise<Response> {
  const row = await loadUpload(env, new URL(request.url).searchParams.get("key"));
  if (row.post_id) throw new HttpError(409, "upload_in_use", "Este arquivo já pertence a um post.");
  await removeUpload(env, row);
  return json({ ok: true });
}

async function removeUpload(env: Env, row: Pick<UploadRow, "r2_key" | "upload_id">): Promise<void> {
  if (row.upload_id) {
    try {
      await env.MEDIA.resumeMultipartUpload(row.r2_key, row.upload_id).abort();
    } catch {
      // Multipart já expirado ou concluído.
    }
  }
  await env.MEDIA.delete(row.r2_key);
  await env.DB.prepare("DELETE FROM uploads WHERE r2_key = ?1").bind(row.r2_key).run();
}

/** Remove do R2 e do D1 as mídias de um post (após publicar). */
export async function deletePostMedia(env: Env, postId: string): Promise<void> {
  const { results } = await env.DB.prepare("SELECT r2_key, upload_id FROM uploads WHERE post_id = ?1")
    .bind(postId)
    .all<Pick<UploadRow, "r2_key" | "upload_id">>();
  if (results.length === 0) return;
  await env.MEDIA.delete(results.map((r) => r.r2_key));
  await env.DB.prepare("DELETE FROM uploads WHERE post_id = ?1").bind(postId).run();
}

/** Manutenção diária: uploads nunca usados em um post depois de 48h. */
export async function cleanupOrphanUploads(env: Env): Promise<number> {
  const { results } = await env.DB.prepare(
    "SELECT r2_key, upload_id FROM uploads WHERE post_id IS NULL AND created_at < ?1 LIMIT 100",
  )
    .bind(Date.now() - ORPHAN_AGE_MS)
    .all<Pick<UploadRow, "r2_key" | "upload_id">>();
  for (const row of results) await removeUpload(env, row);
  return results.length;
}
