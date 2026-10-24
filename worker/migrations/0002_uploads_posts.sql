-- Fase 3: upload multipart para o R2 e fila de publicação.
ALTER TABLE uploads ADD COLUMN kind TEXT NOT NULL DEFAULT 'IMAGE';          -- IMAGE | VIDEO
ALTER TABLE uploads ADD COLUMN status TEXT NOT NULL DEFAULT 'UPLOADING';    -- UPLOADING | READY
ALTER TABLE uploads ADD COLUMN upload_id TEXT;                              -- id do multipart no R2
ALTER TABLE uploads ADD COLUMN file_name TEXT;                              -- nome original (só exibição)

ALTER TABLE posts ADD COLUMN ig_status TEXT;                                -- último status_code do container na Meta

CREATE INDEX uploads_orphans ON uploads (post_id, created_at);
