using InstaPanel.Core;
using Microsoft.Data.Sqlite;

namespace InstaPanel.Data;

public sealed record MediaItem
{
    public required string Id { get; init; }
    public required string Path { get; init; }
    public required MediaInfo Info { get; init; }
    public string? ThumbnailPath { get; init; }
    public DateTimeOffset AddedAt { get; init; }

    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>Registro local dos posts criados por este painel (o estado oficial fica no Worker).</summary>
public sealed record LocalPost
{
    public required string Id { get; init; }

    /// <summary>Primeira mídia (capa do post nas listas).</summary>
    public string? MediaItemId { get; init; }

    /// <summary>Todas as mídias, na ordem do post. Null ao atualizar só o estado.</summary>
    public IReadOnlyList<string>? MediaItemIds { get; init; }
    public required string Type { get; init; }
    public string? Caption { get; init; }
    public required string State { get; init; }
    public string? Permalink { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Banco SQLite local em %LOCALAPPDATA%\InstaPanel\instapanel.db (biblioteca e posts deste PC).</summary>
public sealed class LibraryDatabase
{
    private const int SchemaVersion = 2;
    private readonly string _connectionString;

    public LibraryDatabase(string? directory = null)
    {
        var dir = directory ?? AppPaths.DataDirectory;
        Directory.CreateDirectory(dir);
        FilePath = System.IO.Path.Combine(dir, "instapanel.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = FilePath, Pooling = false }.ToString();
        Migrate();
    }

    public string FilePath { get; }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var pragma = c.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;";
        pragma.ExecuteNonQuery();
        return c;
    }

    private void Migrate()
    {
        using var c = Open();
        using var get = c.CreateCommand();
        get.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(get.ExecuteScalar());
        if (version >= SchemaVersion) return;

        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        if (version < 1)
        {
            cmd.CommandText = """
                CREATE TABLE media_items (
                  id             TEXT PRIMARY KEY,
                  path           TEXT NOT NULL UNIQUE COLLATE NOCASE,
                  kind           TEXT NOT NULL,
                  width          INTEGER NOT NULL,
                  height         INTEGER NOT NULL,
                  size_bytes     INTEGER NOT NULL,
                  duration_ms    INTEGER NOT NULL DEFAULT 0,
                  container      TEXT,
                  video_codec    TEXT,
                  audio_codec    TEXT,
                  pixel_format   TEXT,
                  frame_rate     REAL NOT NULL DEFAULT 0,
                  bit_rate       INTEGER NOT NULL DEFAULT 0,
                  thumbnail_path TEXT,
                  added_at       INTEGER NOT NULL
                );
                CREATE TABLE local_posts (
                  id            TEXT PRIMARY KEY,
                  media_item_id TEXT REFERENCES media_items(id) ON DELETE SET NULL,
                  type          TEXT NOT NULL,
                  caption       TEXT,
                  state         TEXT NOT NULL,
                  permalink     TEXT,
                  error         TEXT,
                  created_at    INTEGER NOT NULL,
                  updated_at    INTEGER NOT NULL
                );
                CREATE INDEX local_posts_media ON local_posts (media_item_id);
                """;
            cmd.ExecuteNonQuery();
        }
        if (version < 2)
        {
            // Carrossel: um post pode ter várias mídias da biblioteca.
            cmd.CommandText = """
                CREATE TABLE local_post_media (
                  post_id       TEXT NOT NULL REFERENCES local_posts(id) ON DELETE CASCADE,
                  position      INTEGER NOT NULL,
                  media_item_id TEXT NOT NULL REFERENCES media_items(id) ON DELETE CASCADE,
                  PRIMARY KEY (post_id, position)
                );
                CREATE INDEX local_post_media_item ON local_post_media (media_item_id);
                INSERT INTO local_post_media (post_id, position, media_item_id)
                  SELECT id, 0, media_item_id FROM local_posts
                  WHERE media_item_id IS NOT NULL AND media_item_id IN (SELECT id FROM media_items);
                """;
            cmd.ExecuteNonQuery();
        }
        cmd.CommandText = $"PRAGMA user_version = {SchemaVersion}";
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    // ---------- Biblioteca ----------

    public IReadOnlyList<MediaItem> GetMediaItems()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM media_items ORDER BY added_at DESC";
        using var r = cmd.ExecuteReader();
        var list = new List<MediaItem>();
        while (r.Read()) list.Add(ReadMedia(r));
        return list;
    }

    public MediaItem? GetMediaItem(string id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM media_items WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadMedia(r) : null;
    }

    public MediaItem? FindByPath(string path)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM media_items WHERE path = $path";
        cmd.Parameters.AddWithValue("$path", System.IO.Path.GetFullPath(path));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadMedia(r) : null;
    }

    public void AddMediaItem(MediaItem item)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO media_items (id, path, kind, width, height, size_bytes, duration_ms, container, video_codec,
                                     audio_codec, pixel_format, frame_rate, bit_rate, thumbnail_path, added_at)
            VALUES ($id, $path, $kind, $w, $h, $size, $dur, $container, $vc, $ac, $pf, $fps, $br, $thumb, $added)
            """;
        var i = item.Info;
        cmd.Parameters.AddWithValue("$id", item.Id);
        cmd.Parameters.AddWithValue("$path", System.IO.Path.GetFullPath(item.Path));
        cmd.Parameters.AddWithValue("$kind", i.Kind.ToString());
        cmd.Parameters.AddWithValue("$w", i.Width);
        cmd.Parameters.AddWithValue("$h", i.Height);
        cmd.Parameters.AddWithValue("$size", i.SizeBytes);
        cmd.Parameters.AddWithValue("$dur", (long)i.Duration.TotalMilliseconds);
        cmd.Parameters.AddWithValue("$container", (object?)i.Container ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$vc", (object?)i.VideoCodec ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ac", (object?)i.AudioCodec ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pf", (object?)i.PixelFormat ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fps", i.FrameRate);
        cmd.Parameters.AddWithValue("$br", i.BitRate);
        cmd.Parameters.AddWithValue("$thumb", (object?)item.ThumbnailPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$added", item.AddedAt.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }

    public void RemoveMediaItem(string id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM media_items WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private static MediaItem ReadMedia(SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        Path = r.GetString(r.GetOrdinal("path")),
        ThumbnailPath = NullableString(r, "thumbnail_path"),
        AddedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(r.GetOrdinal("added_at"))),
        Info = new MediaInfo
        {
            Kind = Enum.Parse<MediaKind>(r.GetString(r.GetOrdinal("kind"))),
            Width = r.GetInt32(r.GetOrdinal("width")),
            Height = r.GetInt32(r.GetOrdinal("height")),
            SizeBytes = r.GetInt64(r.GetOrdinal("size_bytes")),
            Duration = TimeSpan.FromMilliseconds(r.GetInt64(r.GetOrdinal("duration_ms"))),
            Container = NullableString(r, "container"),
            VideoCodec = NullableString(r, "video_codec"),
            AudioCodec = NullableString(r, "audio_codec"),
            PixelFormat = NullableString(r, "pixel_format"),
            FrameRate = r.GetDouble(r.GetOrdinal("frame_rate")),
            BitRate = r.GetInt64(r.GetOrdinal("bit_rate")),
        },
    };

    // ---------- Posts locais ----------

    public void UpsertPost(LocalPost post)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO local_posts (id, media_item_id, type, caption, state, permalink, error, created_at, updated_at)
            VALUES ($id, $media, $type, $caption, $state, $permalink, $error, $created, $updated)
            ON CONFLICT (id) DO UPDATE SET
              state = excluded.state, permalink = excluded.permalink, error = excluded.error, updated_at = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$id", post.Id);
        cmd.Parameters.AddWithValue("$media", (object?)post.MediaItemId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$type", post.Type);
        cmd.Parameters.AddWithValue("$caption", (object?)post.Caption ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$state", post.State);
        cmd.Parameters.AddWithValue("$permalink", (object?)post.Permalink ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$error", (object?)post.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", post.CreatedAt.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$updated", post.UpdatedAt.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();

        if (post.MediaItemIds is { } ids)
        {
            using var del = c.CreateCommand();
            del.CommandText = "DELETE FROM local_post_media WHERE post_id = $id";
            del.Parameters.AddWithValue("$id", post.Id);
            del.ExecuteNonQuery();
            for (var i = 0; i < ids.Count; i++)
            {
                using var ins = c.CreateCommand();
                ins.CommandText = """
                    INSERT INTO local_post_media (post_id, position, media_item_id)
                    SELECT $id, $pos, $media WHERE EXISTS (SELECT 1 FROM media_items WHERE id = $media)
                    """;
                ins.Parameters.AddWithValue("$id", post.Id);
                ins.Parameters.AddWithValue("$pos", i);
                ins.Parameters.AddWithValue("$media", ids[i]);
                ins.ExecuteNonQuery();
            }
        }
        else if (post.MediaItemId is { } first)
        {
            // Atualização só de estado: garante a capa sem mexer nos demais itens.
            using var ins = c.CreateCommand();
            ins.CommandText = """
                INSERT OR IGNORE INTO local_post_media (post_id, position, media_item_id)
                SELECT $id, 0, $media WHERE EXISTS (SELECT 1 FROM media_items WHERE id = $media)
                """;
            ins.Parameters.AddWithValue("$id", post.Id);
            ins.Parameters.AddWithValue("$media", first);
            ins.ExecuteNonQuery();
        }
    }

    /// <summary>Posts deste PC por id (para comparar estados na sincronização).</summary>
    public IReadOnlyDictionary<string, LocalPost> GetLocalPosts()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM local_posts";
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, LocalPost>();
        while (r.Read())
        {
            var post = new LocalPost
            {
                Id = r.GetString(r.GetOrdinal("id")),
                MediaItemId = NullableString(r, "media_item_id"),
                Type = r.GetString(r.GetOrdinal("type")),
                Caption = NullableString(r, "caption"),
                State = r.GetString(r.GetOrdinal("state")),
                Permalink = NullableString(r, "permalink"),
                Error = NullableString(r, "error"),
                CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(r.GetOrdinal("created_at"))),
                UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(r.GetOrdinal("updated_at"))),
            };
            map[post.Id] = post;
        }
        return map;
    }

    /// <summary>Miniatura local de cada post criado neste PC (post id → caminho do .jpg).</summary>
    public IReadOnlyDictionary<string, string> GetPostThumbnails()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT pm.post_id, m.thumbnail_path FROM local_post_media pm
            JOIN media_items m ON m.id = pm.media_item_id
            WHERE pm.position = 0 AND m.thumbnail_path IS NOT NULL
            """;
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, string>();
        while (r.Read()) map[r.GetString(0)] = r.GetString(1);
        return map;
    }

    /// <summary>Ids de mídias que já foram usadas em um post publicado.</summary>
    public IReadOnlySet<string> GetPublishedMediaIds()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT pm.media_item_id FROM local_post_media pm
            JOIN local_posts p ON p.id = pm.post_id
            WHERE p.state = 'PUBLISHED'
            """;
        using var r = cmd.ExecuteReader();
        var set = new HashSet<string>();
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }

    private static string? NullableString(SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }
}
