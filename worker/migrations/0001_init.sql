-- Conta Instagram conectada (sempre uma linha, id = 1).
CREATE TABLE account (
  id                  INTEGER PRIMARY KEY CHECK (id = 1),
  ig_user_id          TEXT NOT NULL,
  username            TEXT NOT NULL,
  account_type        TEXT,
  profile_picture_url TEXT,
  token_ciphertext    TEXT NOT NULL,
  token_iv            TEXT NOT NULL,
  token_expires_at    INTEGER NOT NULL,          -- epoch ms
  token_expiry_known  INTEGER NOT NULL DEFAULT 1, -- 0 = estimado (60 dias a partir do cadastro)
  token_obtained_at   INTEGER NOT NULL,           -- quando este token foi salvo (troca, cadastro ou renovação)
  last_error          TEXT,
  created_at          INTEGER NOT NULL,
  updated_at          INTEGER NOT NULL
);

-- Arquivos enviados ao R2 pelo painel (orfãos são limpos pela manutenção).
CREATE TABLE uploads (
  r2_key       TEXT PRIMARY KEY,
  content_type TEXT NOT NULL,
  size_bytes   INTEGER,
  post_id      TEXT REFERENCES posts(id) ON DELETE SET NULL,
  created_at   INTEGER NOT NULL
);

CREATE TABLE posts (
  id              TEXT PRIMARY KEY,
  type            TEXT NOT NULL CHECK (type IN ('IMAGE', 'CAROUSEL', 'REELS', 'STORIES')),
  caption         TEXT,
  options_json    TEXT NOT NULL DEFAULT '{}',
  scheduled_at    INTEGER NOT NULL,              -- epoch ms (UTC)
  state           TEXT NOT NULL DEFAULT 'SCHEDULED',
  attempts        INTEGER NOT NULL DEFAULT 0,
  next_run_at     INTEGER,
  locked_until    INTEGER NOT NULL DEFAULT 0,
  ig_container_id TEXT,
  ig_media_id     TEXT,
  permalink       TEXT,
  error           TEXT,
  created_at      INTEGER NOT NULL,
  updated_at      INTEGER NOT NULL
);
CREATE INDEX posts_due ON posts (state, next_run_at);
CREATE INDEX posts_updated ON posts (updated_at);

CREATE TABLE post_media (
  post_id                TEXT NOT NULL REFERENCES posts(id) ON DELETE CASCADE,
  position               INTEGER NOT NULL,
  r2_key                 TEXT NOT NULL,
  media_kind             TEXT NOT NULL CHECK (media_kind IN ('IMAGE', 'VIDEO')),
  ig_child_container_id  TEXT,
  PRIMARY KEY (post_id, position)
);

-- Log de transições e erros (histórico e depuração).
CREATE TABLE events (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  post_id    TEXT,
  kind       TEXT NOT NULL,
  detail     TEXT,
  created_at INTEGER NOT NULL
);
CREATE INDEX events_post ON events (post_id, created_at);
