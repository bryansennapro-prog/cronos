// Meta simulada para testes locais (sem token real).
// Uso: npm run fake-meta   → escuta em http://127.0.0.1:8788
// No .dev.vars, aponte META_GRAPH_BASE=http://127.0.0.1:8788
// Tokens aceitos: qualquer valor que comece com "IGTEST".
// Como a Meta real, baixa a mídia de image_url/video_url ao criar o container.
// Legenda contendo "#falhar" faz o container terminar em ERROR.
import { createServer } from "node:http";

const PORT = Number(process.env.PORT ?? 8788);
const containers = new Map(); // id → { status, polls, kind, published }
let seq = 1000;
let published = 0;

// Posts publicados de exemplo (mais recentes primeiro), no formato da Graph API.
const FAKE_CAPTIONS = [
  ["CAROUSEL_ALBUM", "FEED", "7 erros que travam o crescimento do seu perfil (o #4 eu cometi por 2 anos)\n\nArrasta pro lado e salva pra revisar antes do próximo post.\n\n#crescimentoinstagram #dicasdeinstagram #marketingdigital"],
  ["VIDEO", "REELS", "Como eu editei esse Reel em 18 minutos usando só o celular\n\nPasso a passo no vídeo. Manda pra quem vive dizendo que não tem tempo.\n\n#reelsdicas #ediçãodevideo"],
  ["IMAGE", "FEED", "Bom dia! ☀️ O que vocês acham?"],
  ["CAROUSEL_ALBUM", "FEED", "Mito: postar todo dia faz o perfil crescer.\nVerdade: constância com qualidade ganha de volume.\n\nSalva esse carrossel e manda pro amigo que posta 3x por dia.\n\n#estrategiadeconteudo #instagramparanegocios #criadordeconteudo #marketing"],
  ["VIDEO", "REELS", "Pare de usar 30 hashtags. Em 2026 isso não ajuda mais — e pode atrapalhar.\n\n#hashtags #instagram #love #instagood #follow4follow #like4like #marketing #brasil #reels #viral #explore #fyp"],
  ["IMAGE", "FEED", "No mundo acelerado de hoje, é crucial alavancar sua presença digital de forma robusta e significativa. Vamos mergulhar nessa jornada transformadora juntos! Comenta SIM se você concorda 🚀🔥💯✨🙌"],
  ["CAROUSEL_ALBUM", "FEED", "Antes e depois: de 312 para 1.800 seguidores em 5 meses.\n\nO que mudou foi o nome do perfil e a bio. Salva pra aplicar hoje.\n\n#crescimentoorganico #biodoinstagram #dicasdeinstagram"],
  ["VIDEO", "REELS", "Sabe quando você posta e ninguém vê? Já passei por isso em março de 2025.\n\nMe conta nos comentários qual foi seu pior post.\n\n#criadordeconteudo #reels"],
  ["IMAGE", "FEED", "Novidade chegando... fiquem ligados! #novidade #instagood #love #photooftheday #happy #fashion #style #beautiful"],
  ["CAROUSEL_ALBUM", "FEED", "O método 3-2-1 que uso pra planejar a semana inteira em 40 minutos\n\nSalva e testa no domingo.\n\n#planejamentodeconteudo #produtividade #marketingdigital"],
];
const FAKE_MEDIA = Array.from({ length: 36 }, (_, i) => {
  const [media_type, media_product_type, caption] = FAKE_CAPTIONS[i % FAKE_CAPTIONS.length];
  const when = new Date(Date.UTC(2026, 9, 22, 14 + (i % 3) * 4, 0) - i * 3.2 * 86400000);
  const base = { CAROUSEL_ALBUM: 140, VIDEO: 210, IMAGE: 45 }[media_type];
  const likes = Math.round(base * (1 + ((i * 37) % 11) / 10) * (i % 10 === 5 ? 0.3 : 1));
  return {
    id: `179000000000${String(i).padStart(5, "0")}`,
    caption,
    media_type,
    media_product_type,
    timestamp: when.toISOString().replace(/\.\d{3}Z$/, "+0000"),
    ...(i % 13 === 7 ? {} : { like_count: likes }), // um ou outro com curtidas ocultas
    comments_count: Math.round(likes / 12),
    permalink: `https://www.instagram.com/p/FAKEMEDIA${i}/`,
    media_url: media_type === "VIDEO" ? undefined : `https://picsum.photos/seed/ip${i}/400/500`,
    thumbnail_url: media_type === "VIDEO" ? `https://picsum.photos/seed/ip${i}/400/700` : undefined,
  };
});

function send(res, status, body) {
  res.writeHead(status, { "content-type": "application/json" });
  res.end(JSON.stringify(body));
}

function metaError(res, status, message, code) {
  send(res, status, { error: { message, type: "OAuthException", code, fbtrace_id: "FAKE" } });
}

async function readBody(req) {
  const chunks = [];
  for await (const c of req) chunks.push(c);
  return Buffer.concat(chunks).toString();
}


createServer(async (req, res) => {
  const url = new URL(req.url, `http://${req.headers.host}`);
  const raw = req.method === "POST" ? await readBody(req) : "";
  const form = new URLSearchParams(raw);
  const param = (k) => url.searchParams.get(k) ?? form.get(k);
  const token = param("access_token") ?? "";
  console.log(req.method, url.pathname);

  // ---- OAuth ("Entrar com Instagram") ----
  // Tela de login simulada: autoriza na hora e volta para o redirect_uri com um código
  // (com o "#_" que o Instagram real às vezes acrescenta). ?deny=1 simula "cancelar".
  if (url.pathname === "/oauth/authorize") {
    const back = new URL(url.searchParams.get("redirect_uri"));
    back.searchParams.set("state", url.searchParams.get("state") ?? "");
    if (url.searchParams.get("client_id") !== "123456") return send(res, 400, { error_message: "Invalid client_id" });
    if (process.env.FAKE_DENY === "1") {
      back.searchParams.set("error", "access_denied");
      back.searchParams.set("error_reason", "user_denied");
    } else {
      back.searchParams.set("code", "FAKECODE123");
    }
    res.writeHead(302, { location: back.toString() + (process.env.FAKE_DENY === "1" ? "" : "#_") });
    return res.end();
  }
  if (req.method === "POST" && url.pathname === "/oauth/access_token") {
    const ok = form.get("client_id") === "123456" && form.get("client_secret") === "fake-app-secret"
      && form.get("grant_type") === "authorization_code" && form.get("code") === "FAKECODE123" && form.get("redirect_uri");
    if (!ok) return send(res, 400, { error_type: "OAuthException", code: 400, error_message: "Invalid authorization code" });
    return send(res, 200, { access_token: "IGTEST-SHORT-oauth" + Date.now(), user_id: 17841400000000000, permissions: ["instagram_business_basic", "instagram_business_content_publish"] });
  }

  // ---- Claude simulado (análise com IA) ----
  // No .dev.vars: ANTHROPIC_BASE_URL=http://127.0.0.1:8788 e ANTHROPIC_API_KEY=sk-fake (outra chave = 401).
  if (req.method === "POST" && url.pathname === "/v1/messages") {
    const body = JSON.parse(raw || "{}");
    // O SDK 1.x manda a chave como "Authorization: Bearer"; versões antigas usavam x-api-key.
    const apiKey = req.headers["x-api-key"] ?? (req.headers.authorization ?? "").replace(/^Bearer\s+/i, "");
    if (apiKey !== "sk-fake") {
      return send(res, 401, { type: "error", error: { type: "authentication_error", message: "invalid x-api-key" } });
    }
    const userText = body.messages?.[0]?.content ?? "";
    console.log(`  claude: model=${body.model} effort=${body.output_config?.effort} fallbacks=${JSON.stringify(body.fallbacks)} beta=${req.headers["anthropic-beta"]} chars=${userText.length}`);
    const refuse = userText.includes("#recusar");
    return send(res, 200, {
      id: "msg_fake", type: "message", role: "assistant", model: body.model,
      content: refuse ? [] : [
        { type: "thinking", thinking: "", signature: "fake" },
        { type: "text", text: `## Diagnóstico\nO perfil recebeu **${userText.length} caracteres** de dados. Reels são o formato mais forte.\n\n## O que fazer primeiro\n1. **Troque o Nome** para incluir o tema.\n2. Remova as hashtags de spam.\n\n## Nome e bio\n- Nome: Conta Teste | Instagram\n- Bio: Dicas de crescimento para [seu público] · [seu número] alunos\n\n## Próximos 7 dias\n- sáb: Reel \"como eu fiz\"` },
      ],
      stop_reason: refuse ? "refusal" : "end_turn",
      stop_details: refuse ? { type: "refusal", category: null, explanation: "Recusado no teste." } : null,
      usage: { input_tokens: Math.round(userText.length / 4), output_tokens: 180 },
    });
  }

  if (!token.startsWith("IGTEST")) return metaError(res, 400, "Invalid OAuth access token - Cannot parse access token", 190);

  if (url.pathname === "/access_token") {
    if (!token.startsWith("IGTEST-SHORT")) return metaError(res, 400, "Session key invalid.", 452);
    return send(res, 200, { access_token: "IGTEST-LONG-" + Date.now(), token_type: "bearer", expires_in: 5183944 });
  }
  if (url.pathname === "/refresh_access_token") {
    return send(res, 200, { access_token: "IGTEST-LONG-R" + Date.now(), token_type: "bearer", expires_in: 5183944 });
  }

  let m;
  if (/^\/v[\d.]+\/me$/.test(url.pathname)) {
    const profile = {
      user_id: "17841400000000000",
      username: "conta_teste",
      account_type: "MEDIA_CREATOR",
      profile_picture_url: "https://example.com/avatar.jpg",
      id: "1234567890",
    };
    // Campos extras só quando pedidos (como a Meta real): usados pelo Especialista.
    const fields = (param("fields") ?? "").split(",");
    if (fields.includes("biography")) {
      Object.assign(profile, {
        name: "Conta Teste",
        biography: "Eu ajudo pessoas a crescer no Instagram 🚀✨🔥💯🙌",
        website: "",
        followers_count: 1840,
        follows_count: 912,
        media_count: FAKE_MEDIA.length,
      });
    }
    return send(res, 200, profile);
  }

  // Lista de posts publicados, paginada por cursor como a Graph API.
  if (req.method === "GET" && /^\/v[\d.]+\/\d+\/media$/.test(url.pathname)) {
    const limit = Math.min(50, Number(param("limit") ?? 25));
    const start = Number(param("after") ?? 0);
    const data = FAKE_MEDIA.slice(start, start + limit);
    const next = start + limit < FAKE_MEDIA.length ? String(start + limit) : undefined;
    return send(res, 200, {
      data,
      paging: { cursors: { before: String(start), after: next ?? String(start + data.length) }, ...(next ? { next: `${url.origin}${url.pathname}?after=${next}` } : {}) },
    });
  }

  if (/^\/v[\d.]+\/\d+\/content_publishing_limit$/.test(url.pathname)) {
    return send(res, 200, { data: [{ config: { quota_total: 100, quota_duration: 86400 }, quota_usage: published }] });
  }

  // Criar container: baixa a mídia como a Meta faria.
  if (req.method === "POST" && /^\/v[\d.]+\/\d+\/media$/.test(url.pathname)) {
    const kind = form.get("media_type") ?? "IMAGE";
    const caption = form.get("caption") ?? "";
    const isItem = form.get("is_carousel_item") === "true";

    // Carrossel: exige itens existentes, marcados como item e já processados.
    if (kind === "CAROUSEL") {
      const children = (form.get("children") ?? "").split(",").filter(Boolean);
      if (children.length < 2 || children.length > 10) return metaError(res, 400, "Carousel must have 2-10 children", 100);
      for (const cid of children) {
        const child = containers.get(cid);
        if (!child?.isItem) return metaError(res, 400, `Invalid carousel child ${cid}`, 100);
        if (child.status !== "OK" || !child.done) return metaError(res, 400, `Child ${cid} not finished`, 9007);
      }
      const id = String(++seq);
      containers.set(id, { kind, polls: 0, status: caption.includes("#falhar") ? "ERROR_FORMAT" : "OK", children });
      console.log("  carrossel com", children.length, "itens");
      return send(res, 200, { id });
    }

    if (isItem && caption) return metaError(res, 400, "Caption is not supported on carousel items", 100);
    if (kind === "STORIES" && caption) return metaError(res, 400, "Caption is not supported on stories", 100);

    const mediaUrl = form.get("image_url") ?? form.get("video_url");
    if (!mediaUrl) return metaError(res, 400, "The parameter image_url is required", 100);
    const isVideo = form.has("video_url");
    let fetched;
    try {
      const r = await fetch(mediaUrl, { headers: isVideo ? { range: "bytes=0-1023" } : {} });
      const body = Buffer.from(await r.arrayBuffer());
      fetched = { status: r.status, type: r.headers.get("content-type"), bytes: body.length, range: r.headers.get("content-range") };
    } catch (e) {
      fetched = { status: 0, error: String(e) };
    }
    console.log(`  mídia baixada (${kind}${isItem ? ", item" : ""}):`, fetched.status, fetched.type);
    const okType = isVideo ? /^video\//.test(fetched.type ?? "") : fetched.type === "image/jpeg";
    const okStatus = isVideo ? fetched.status === 206 : fetched.status === 200;
    const id = String(++seq);
    containers.set(id, {
      kind,
      isItem,
      isVideo,
      thumbOffset: form.get("thumb_offset"),
      polls: 0,
      status: !okStatus || !okType ? "ERROR_FETCH" : caption.includes("#falhar") ? "ERROR_FORMAT" : "OK",
    });
    return send(res, 200, { id });
  }

  // Inspeção para os testes: GET /_debug/<id>
  if ((m = /^\/_debug\/(\d+)$/.exec(url.pathname))) {
    return send(res, 200, containers.get(m[1]) ?? {});
  }

  if (req.method === "POST" && /^\/v[\d.]+\/\d+\/media_publish$/.test(url.pathname)) {
    const c = containers.get(form.get("creation_id"));
    if (!c) return metaError(res, 400, "Invalid creation_id", 100);
    if (c.published) return metaError(res, 400, "Media already published", 9007);
    c.published = true;
    published++;
    return send(res, 200, { id: "9" + form.get("creation_id") });
  }

  if ((m = /^\/v[\d.]+\/(\d+)$/.exec(url.pathname))) {
    const id = m[1];
    if (id.startsWith("9")) {
      return send(res, 200, { id, permalink: `https://www.instagram.com/p/FAKE${id}/` });
    }
    const c = containers.get(id);
    if (!c) return metaError(res, 400, "Unsupported get request", 100);
    c.polls++;
    if (c.published) return send(res, 200, { id, status_code: "PUBLISHED", status: "Published" });
    if (c.status === "ERROR_FETCH") return send(res, 200, { id, status_code: "ERROR", status: "Error: Media download has failed. (2207052)" });
    if (c.status === "ERROR_FORMAT") return send(res, 200, { id, status_code: "ERROR", status: "Error: Unsupported format (2207026)" });
    // Vídeo: primeiro poll ainda em processamento.
    const done = !(c.isVideo || c.kind === "REELS") || c.polls > 1;
    if (done) c.done = true;
    return send(res, 200, { id, status_code: done ? "FINISHED" : "IN_PROGRESS", status: done ? "Finished" : "In Progress" });
  }

  metaError(res, 404, "Unknown path components", 2500);
}).listen(PORT, "127.0.0.1", () => console.log(`fake-meta em http://127.0.0.1:${PORT}`));
