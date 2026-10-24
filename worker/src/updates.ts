import type { Env } from "./env";

/**
 * GET /updates/<arquivo> — rota pública com os arquivos de atualização do painel (Velopack):
 * releases.win.json, *.nupkg e o instalador. Ficam no R2 com o prefixo "updates/" e são
 * enviados pelo desktop/scripts/release.ps1.
 */
export async function handleUpdates(request: Request, env: Env, file: string): Promise<Response> {
  if (request.method !== "GET" && request.method !== "HEAD") return new Response("Method not allowed", { status: 405 });
  if (!/^[A-Za-z0-9._-]{1,200}$/.test(file)) return new Response("Not found", { status: 404 });

  const obj = await env.MEDIA.get(`updates/${file}`, { range: request.headers });
  if (!obj) return new Response("Not found", { status: 404, headers: { "cache-control": "no-store" } });

  const headers = new Headers({ etag: obj.httpEtag, "accept-ranges": "bytes" });
  headers.set("content-type",
    file.endsWith(".json") ? "application/json" : file.endsWith(".exe") ? "application/vnd.microsoft.portable-executable" : "application/octet-stream");
  // O índice muda a cada versão; os pacotes nunca mudam.
  headers.set("cache-control", file.endsWith(".json") || file === "RELEASES" ? "no-cache" : "public, max-age=31536000, immutable");
  if (file.endsWith(".exe")) headers.set("content-disposition", `attachment; filename="${file}"`);

  if (obj.range && request.headers.has("range")) {
    const r = obj.range as { offset?: number; length?: number; suffix?: number };
    const offset = r.suffix !== undefined ? obj.size - r.suffix : (r.offset ?? 0);
    const length = r.suffix !== undefined ? r.suffix : (r.length ?? obj.size - offset);
    headers.set("content-range", `bytes ${offset}-${offset + length - 1}/${obj.size}`);
    headers.set("content-length", String(length));
    return new Response(request.method === "HEAD" ? null : obj.body, { status: 206, headers });
  }
  headers.set("content-length", String(obj.size));
  return new Response(request.method === "HEAD" ? null : obj.body, { headers });
}
