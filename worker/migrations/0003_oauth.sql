-- Fase 6: "Entrar com Instagram" (OAuth). Cada tentativa de login tem um state aleatório.
CREATE TABLE oauth_states (
  state      TEXT PRIMARY KEY,
  status     TEXT NOT NULL DEFAULT 'PENDING',  -- PENDING | CONNECTED | ERROR
  username   TEXT,
  error      TEXT,
  created_at INTEGER NOT NULL
);
