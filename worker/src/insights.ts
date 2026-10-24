import type { Env } from "./env";
import { HttpError, json } from "./http";
import { getAccessToken } from "./account";
import { MetaClient, MetaError, type MetaMedia } from "./meta/client";

const DEFAULT_MEDIA = 60;
const MAX_MEDIA = 100;

function toIso(timestamp: string): string {
  // A Meta manda "2026-10-20T14:00:00+0000" (sem ":" no fuso), que o .NET não lê.
  const ms = Date.parse(timestamp);
  return Number.isNaN(ms) ? timestamp : new Date(ms).toISOString();
}

function mediaView(m: MetaMedia) {
  const isVideo = m.media_type === "VIDEO";
  return {
    id: m.id,
    caption: m.caption ?? null,
    media_type: m.media_type,
    media_product_type: m.media_product_type ?? null,
    timestamp: toIso(m.timestamp),
    like_count: m.like_count ?? null,
    comments_count: m.comments_count ?? null,
    permalink: m.permalink ?? null,
    thumbnail_url: (isVideo ? m.thumbnail_url : m.media_url ?? m.thumbnail_url) ?? null,
  };
}

/**
 * GET /api/insights?limit=60
 * Perfil + posts publicados, lidos ao vivo da Meta. A análise ("Especialista") roda no painel.
 */
export async function handleGetInsights(request: Request, env: Env): Promise<Response> {
  const requested = Number(new URL(request.url).searchParams.get("limit") ?? DEFAULT_MEDIA);
  const limit = Number.isFinite(requested) ? Math.min(MAX_MEDIA, Math.max(1, Math.floor(requested))) : DEFAULT_MEDIA;

  const { token, igUserId } = await getAccessToken(env);
  const meta = new MetaClient(env);
  try {
    const [profile, media] = await Promise.all([
      meta.getProfileDetails(token),
      meta.listMedia(igUserId, token, limit),
    ]);
    return json({
      profile: {
        ig_user_id: profile.user_id ?? igUserId,
        username: profile.username,
        name: profile.name ?? null,
        biography: profile.biography ?? null,
        website: profile.website ?? null,
        account_type: profile.account_type ?? null,
        profile_picture_url: profile.profile_picture_url ?? null,
        followers_count: profile.followers_count ?? null,
        follows_count: profile.follows_count ?? null,
        media_count: profile.media_count ?? null,
      },
      media: media.map(mediaView),
      fetched_at: new Date().toISOString(),
    });
  } catch (err) {
    if (err instanceof MetaError) {
      throw new HttpError(502, "meta_error", `A Meta não devolveu os dados do perfil. ${err.message}`, err.toJSON());
    }
    throw err;
  }
}
