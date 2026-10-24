import type { Env } from "../env";

/** Erro retornado pela Graph API ({ error: { message, type, code, error_subcode, fbtrace_id } }). */
export class MetaError extends Error {
  constructor(
    readonly httpStatus: number,
    message: string,
    readonly code?: number,
    readonly subcode?: number,
    readonly type?: string,
    readonly fbtraceId?: string,
    readonly isTransient = false,
    readonly userMessage?: string,
  ) {
    super(message);
  }

  /** Vale tentar de novo? (limites, instabilidade, mídia ainda processando) */
  get retriable(): boolean {
    if (this.isTransient || this.httpStatus >= 500 || this.httpStatus === 0) return true;
    return [1, 2, 4, 17, 32, 341, 613, 9007].includes(this.code ?? -1);
  }

  toJSON() {
    return {
      message: this.userMessage ?? this.message,
      code: this.code,
      subcode: this.subcode,
      type: this.type,
      fbtrace_id: this.fbtraceId,
    };
  }
}

export type ContainerStatus = "IN_PROGRESS" | "FINISHED" | "ERROR" | "EXPIRED" | "PUBLISHED";

/** Mídia de um container: a Meta baixa de uma destas URLs. */
export type MediaUrl = { imageUrl: string; videoUrl?: never } | { videoUrl: string; imageUrl?: never };

export type ContainerParams =
  | { kind: "IMAGE"; imageUrl: string; caption?: string }
  | { kind: "REELS"; videoUrl: string; caption?: string; shareToFeed?: boolean; thumbOffsetMs?: number }
  | ({ kind: "STORIES" } & MediaUrl)
  | ({ kind: "CAROUSEL_ITEM" } & MediaUrl)
  | { kind: "CAROUSEL"; children: string[]; caption?: string };

export interface MetaProfile {
  user_id: string;
  username: string;
  account_type?: string;
  profile_picture_url?: string;
}

/** Campos públicos do perfil (permissão instagram_business_basic). */
export interface MetaProfileDetails {
  user_id: string;
  username: string;
  name?: string;
  biography?: string;
  website?: string;
  account_type?: string;
  profile_picture_url?: string;
  followers_count?: number;
  follows_count?: number;
  media_count?: number;
}

/** Um post publicado. like_count some quando o dono oculta as curtidas. */
export interface MetaMedia {
  id: string;
  caption?: string;
  media_type: "IMAGE" | "VIDEO" | "CAROUSEL_ALBUM" | string;
  media_product_type?: "FEED" | "REELS" | "STORY" | "AD" | string;
  timestamp: string;
  like_count?: number;
  comments_count?: number;
  permalink?: string;
  media_url?: string;
  thumbnail_url?: string;
}

const PROFILE_FIELDS =
  "user_id,username,name,biography,website,account_type,profile_picture_url,followers_count,follows_count,media_count";
const MEDIA_FIELDS =
  "id,caption,media_type,media_product_type,timestamp,like_count,comments_count,permalink,media_url,thumbnail_url";
const MEDIA_PAGE_SIZE = 50;

export interface MetaToken {
  access_token: string;
  token_type?: string;
  expires_in: number; // segundos
}

export interface PublishingLimit {
  quota_usage: number;
  quota_total?: number;
  quota_duration?: number; // segundos
}

export class MetaClient {
  private readonly base: string;
  private readonly version: string;

  constructor(env: Env) {
    this.base = env.META_GRAPH_BASE.replace(/\/+$/, "");
    this.version = env.META_GRAPH_VERSION;
  }

  getMe(token: string): Promise<MetaProfile> {
    return this.get(`/${this.version}/me`, {
      fields: "user_id,username,account_type,profile_picture_url",
      access_token: token,
    });
  }

  getProfileDetails(token: string): Promise<MetaProfileDetails> {
    return this.get(`/${this.version}/me`, { fields: PROFILE_FIELDS, access_token: token });
  }

  /** Posts mais recentes primeiro, seguindo a paginação até `max` itens. */
  async listMedia(igUserId: string, token: string, max: number): Promise<MetaMedia[]> {
    const items: MetaMedia[] = [];
    let after: string | undefined;
    while (items.length < max) {
      const params: Record<string, string> = {
        fields: MEDIA_FIELDS,
        limit: String(Math.min(MEDIA_PAGE_SIZE, max - items.length)),
        access_token: token,
      };
      if (after) params.after = after;
      const page = await this.get<{ data?: MetaMedia[]; paging?: { cursors?: { after?: string }; next?: string } }>(
        `/${this.version}/${encodeURIComponent(igUserId)}/media`,
        params,
      );
      items.push(...(page.data ?? []));
      after = page.paging?.cursors?.after;
      if (!page.paging?.next || !after || !page.data?.length) break;
    }
    return items.slice(0, max);
  }

  /** Token curto (1h) → token longo (60 dias). Exige o App Secret do app Instagram. */
  exchangeForLongLived(shortToken: string, appSecret: string): Promise<MetaToken> {
    return this.get(`/access_token`, {
      grant_type: "ig_exchange_token",
      client_secret: appSecret,
      access_token: shortToken,
    });
  }

  /** Renova um token longo por mais 60 dias. O token precisa ter mais de 24h e estar válido. */
  refreshLongLived(token: string): Promise<MetaToken> {
    return this.get(`/refresh_access_token`, {
      grant_type: "ig_refresh_token",
      access_token: token,
    });
  }

  async getPublishingLimit(igUserId: string, token: string): Promise<PublishingLimit> {
    const res = await this.get<{
      data?: Array<{ quota_usage?: number; config?: { quota_total?: number; quota_duration?: number } }>;
    }>(`/${this.version}/${encodeURIComponent(igUserId)}/content_publishing_limit`, {
      fields: "config,quota_usage",
      access_token: token,
    });
    const item = res.data?.[0];
    return {
      quota_usage: item?.quota_usage ?? 0,
      quota_total: item?.config?.quota_total,
      quota_duration: item?.config?.quota_duration,
    };
  }

  /** Cria o container de mídia. A Meta baixa a mídia da URL informada. */
  async createContainer(igUserId: string, token: string, p: ContainerParams): Promise<string> {
    const params: Record<string, string> = { access_token: token };
    switch (p.kind) {
      case "IMAGE":
        params.image_url = p.imageUrl;
        if (p.caption) params.caption = p.caption;
        break;
      case "REELS":
        params.media_type = "REELS";
        params.video_url = p.videoUrl;
        params.share_to_feed = String(p.shareToFeed ?? true);
        if (p.thumbOffsetMs !== undefined) params.thumb_offset = String(Math.round(p.thumbOffsetMs));
        if (p.caption) params.caption = p.caption;
        break;
      case "STORIES":
        // Stories não têm legenda pela API.
        params.media_type = "STORIES";
        if (p.imageUrl) params.image_url = p.imageUrl;
        else params.video_url = p.videoUrl!;
        break;
      case "CAROUSEL_ITEM":
        // Itens não levam legenda: ela vai no container do carrossel.
        params.is_carousel_item = "true";
        if (p.imageUrl) params.image_url = p.imageUrl;
        else {
          params.media_type = "VIDEO";
          params.video_url = p.videoUrl!;
        }
        break;
      case "CAROUSEL":
        params.media_type = "CAROUSEL";
        params.children = p.children.join(",");
        if (p.caption) params.caption = p.caption;
        break;
    }
    const res = await this.post<{ id: string }>(`/${this.version}/${encodeURIComponent(igUserId)}/media`, params);
    return res.id;
  }

  async getContainerStatus(containerId: string, token: string): Promise<{ statusCode: ContainerStatus; status?: string }> {
    const res = await this.get<{ status_code: ContainerStatus; status?: string }>(
      `/${this.version}/${encodeURIComponent(containerId)}`,
      { fields: "status_code,status", access_token: token },
    );
    return { statusCode: res.status_code, status: res.status };
  }

  async publishContainer(igUserId: string, containerId: string, token: string): Promise<string> {
    const res = await this.post<{ id: string }>(`/${this.version}/${encodeURIComponent(igUserId)}/media_publish`, {
      creation_id: containerId,
      access_token: token,
    });
    return res.id;
  }

  async getPermalink(mediaId: string, token: string): Promise<string | undefined> {
    const res = await this.get<{ permalink?: string }>(`/${this.version}/${encodeURIComponent(mediaId)}`, {
      fields: "permalink",
      access_token: token,
    });
    return res.permalink;
  }

  private get<T>(path: string, params: Record<string, string>): Promise<T> {
    const url = new URL(this.base + path);
    for (const [k, v] of Object.entries(params)) url.searchParams.set(k, v);
    return this.send<T>(url, { headers: { accept: "application/json" } });
  }

  private post<T>(path: string, params: Record<string, string>): Promise<T> {
    return this.send<T>(new URL(this.base + path), {
      method: "POST",
      headers: { accept: "application/json" },
      body: new URLSearchParams(params),
    });
  }

  private async send<T>(url: URL, init: RequestInit): Promise<T> {
    let res: Response;
    try {
      res = await fetch(url, init);
    } catch (err) {
      throw new MetaError(0, `Sem conexão com a Meta: ${String(err)}`);
    }
    const text = await res.text();
    let body: any;
    try {
      body = text ? JSON.parse(text) : {};
    } catch {
      throw new MetaError(res.status, `Resposta inesperada da Meta (HTTP ${res.status}).`);
    }

    if (!res.ok || body?.error) {
      const e = body?.error ?? {};
      throw new MetaError(
        res.status,
        e.message ?? `Erro da Meta (HTTP ${res.status}).`,
        e.code,
        e.error_subcode,
        e.type,
        e.fbtrace_id,
        e.is_transient === true,
        e.error_user_msg,
      );
    }
    return body as T;
  }
}
