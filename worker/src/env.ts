export interface Env {
  DB: D1Database;
  MEDIA: R2Bucket;

  META_GRAPH_BASE: string;
  META_GRAPH_VERSION: string;
  /** URL pública deste Worker; a Meta baixa as mídias de `${PUBLIC_BASE_URL}/m/<chave>`. */
  PUBLIC_BASE_URL: string;

  /** ID do app do Instagram (público). Vazio = "Entrar com Instagram" desativado. */
  INSTAGRAM_APP_ID?: string;
  OAUTH_AUTHORIZE_URL: string;
  OAUTH_TOKEN_URL: string;

  // Secrets
  PANEL_API_KEY: string;
  TOKEN_ENC_KEY: string;
  META_APP_SECRET?: string;
  /** Chave da API da Anthropic (análise com IA). Vazio = botão "Analisar com IA" avisa que falta configurar. */
  ANTHROPIC_API_KEY?: string;
  /** Só para testes locais (servidor falso). Em produção fica vazio e o SDK usa a API oficial. */
  ANTHROPIC_BASE_URL?: string;
}
