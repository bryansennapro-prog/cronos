import type { Env } from "./env";
import { MEDIA_KEY_PATTERN } from "./uploads";

const notFound = () => new Response("Not found", { status: 404, headers: { "cache-control": "no-store" } });

/**
 * GET/HEAD /m/<chave> — rota pública de onde a Meta baixa a mídia.
 * Só serve arquivos já anexados a um post ainda não publicado (depois de publicar, o arquivo é apagado).
 * Suporta Range, que a Meta usa ao baixar vídeos.
 */
export async function handleMedia(request: Request, env: Env, key: string): Promise<Response> {
  if (request.method !== "GET" && request.method !== "HEAD") {
    return new Response("Method not allowed", { status: 405, headers: { allow: "GET, HEAD" } });
  }
  if (!MEDIA_KEY_PATTERN.test(key)) return notFound();

  const row = await env.DB.prepare(
    "SELECT 1 FROM uploads WHERE r2_key = ?1 AND status = 'READY' AND post_id IS NOT NULL",
  )
    .bind(key)
    .first();
  if (!row) return notFound();

  const headers = new Headers({
    "cache-control": "no-store",
    "x-robots-tag": "noindex",
    "accept-ranges": "bytes",
  });

  if (request.method === "HEAD") {
    const head = await env.MEDIA.head(key);
    if (!head) return notFound();
    head.writeHttpMetadata(headers);
    headers.set("etag", head.httpEtag);
    headers.set("content-length", String(head.size));
    return new Response(null, { headers });
  }

  const obj = await env.MEDIA.get(key, { range: request.headers });
  if (!obj) return notFound();
  obj.writeHttpMetadata(headers);
  headers.set("etag", obj.httpEtag);

  const range = obj.range as { offset?: number; length?: number; suffix?: number } | undefined;
  if (range && request.headers.has("range")) {
    const offset = range.suffix !== undefined ? obj.size - range.suffix : (range.offset ?? 0);
    const length = range.suffix !== undefined ? range.suffix : (range.length ?? obj.size - offset);
    headers.set("content-range", `bytes ${offset}-${offset + length - 1}/${obj.size}`);
    headers.set("content-length", String(length));
    return new Response(obj.body, { status: 206, headers });
  }

  headers.set("content-length", String(obj.size));
  return new Response(obj.body, { headers });
}
