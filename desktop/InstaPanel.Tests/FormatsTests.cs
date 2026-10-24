using InstaPanel.Core;
using InstaPanel.Data;
using InstaPanel.Media;

namespace InstaPanel.Tests;

/// <summary>Fase 5: carrossel, Stories e capa do Reel.</summary>
public sealed class FormatsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "instapanel-formats-" + Guid.NewGuid().ToString("N"));
    private readonly FfmpegTools _tools = FfmpegTools.TryLocate() ?? throw new InvalidOperationException("FFmpeg não encontrado.");

    public FormatsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static MediaInfo Image(int w, int h, string codec = "mjpeg") => new()
    {
        Kind = MediaKind.Image, Width = w, Height = h, SizeBytes = 500_000, VideoCodec = codec,
    };

    private static MediaInfo Video(double seconds, int w = 1080, int h = 1920) => new()
    {
        Kind = MediaKind.Video, Width = w, Height = h, SizeBytes = 20_000_000, VideoCodec = "h264", AudioCodec = "aac",
        PixelFormat = "yuv420p", FrameRate = 30, Duration = TimeSpan.FromSeconds(seconds), BitRate = 5_000_000,
    };

    private async Task<string> Generate(string name, params string[] args)
    {
        var path = Path.Combine(_dir, name);
        var r = await _tools.RunFfmpegAsync(["-y", "-v", "error", .. args, path], null, CancellationToken.None);
        Assert.True(r.ExitCode == 0, r.StdErrTail);
        return path;
    }

    // ---------- Regras ----------

    [Fact]
    public void Kinds_accept_the_right_media()
    {
        Assert.Equal(CompatibilityStatus.Unsupported, MediaRules.Evaluate(Video(10), PostKind.Image).Status);
        Assert.Equal(CompatibilityStatus.Unsupported, MediaRules.Evaluate(Image(1080, 1080), PostKind.Reel).Status);
        Assert.Equal(CompatibilityStatus.Ready, MediaRules.Evaluate(Image(1080, 1920), PostKind.Story).Status);
        Assert.Equal(CompatibilityStatus.Ready, MediaRules.Evaluate(Video(20), PostKind.Story).Status);
        Assert.Equal(CompatibilityStatus.Ready, MediaRules.Evaluate(Video(20), PostKind.Carousel, 0.8).Status);
        Assert.Equal((2, 10), PostKind.Carousel.MediaCount());
        Assert.False(PostKind.Story.HasCaption());
        Assert.Equal("STORIES", PostKind.Story.ApiType());
    }

    [Fact]
    public void Story_and_carousel_videos_are_limited_to_60_seconds()
    {
        Assert.Equal(CompatibilityStatus.Unsupported, MediaRules.Evaluate(Video(61), PostKind.Story).Status);
        Assert.Equal(CompatibilityStatus.Unsupported, MediaRules.Evaluate(Video(90), PostKind.Carousel, 1).Status);
        Assert.Equal(CompatibilityStatus.Ready, MediaRules.Evaluate(Video(90), PostKind.Reel).Status);
    }

    [Fact]
    public void Story_images_are_not_cropped_but_warn_when_not_vertical()
    {
        var c = MediaRules.Evaluate(Image(1280, 720), PostKind.Story); // até 1440 px não precisa reduzir
        Assert.Equal(CompatibilityStatus.Ready, c.Status);
        Assert.Contains(c.Notes, n => n.Contains("9:16"));
    }

    [Fact]
    public void Carousel_images_follow_the_first_item_aspect()
    {
        var first = Image(1080, 1350); // 4:5
        var aspect = MediaRules.CarouselAspectFor(first);
        Assert.Equal(0.8, aspect, 3);
        Assert.Equal(CompatibilityStatus.Ready, MediaRules.Evaluate(first, PostKind.Carousel, aspect).Status);
        var square = MediaRules.Evaluate(Image(1080, 1080), PostKind.Carousel, aspect);
        Assert.Equal(CompatibilityStatus.NeedsConversion, square.Status);
        Assert.Contains(square.Notes, n => n.Contains("4:5"));
        Assert.Equal((864, 1080), MediaRules.ImageCropToAspect(1080, 1080, 0.8));
        Assert.Equal(1.91, MediaRules.CarouselAspectFor(Image(3000, 1000)), 3); // limitado à faixa do feed
    }

    // ---------- Conversão ----------

    [Fact]
    public async Task Carousel_square_image_is_cropped_to_4x5()
    {
        var jpg = await Generate("quadrada.jpg", "-f", "lavfi", "-i", "testsrc2=size=1080x1080", "-frames:v", "1");
        var info = await MediaProbe.ProbeAsync(_tools, jpg);
        var prepared = await MediaConverter.PrepareAsync(_tools, jpg, info, _dir, PostKind.Carousel, 0.8);
        var outInfo = await MediaProbe.ProbeAsync(_tools, prepared.Path);
        Assert.True(prepared.IsTemporary);
        Assert.Equal((864, 1080), (outInfo.Width, outInfo.Height));
    }

    [Fact]
    public async Task Story_png_keeps_its_aspect()
    {
        var png = await Generate("story.png", "-f", "lavfi", "-i", "testsrc2=size=1080x1920", "-frames:v", "1");
        var info = await MediaProbe.ProbeAsync(_tools, png);
        var prepared = await MediaConverter.PrepareAsync(_tools, png, info, _dir, PostKind.Story, null);
        var outInfo = await MediaProbe.ProbeAsync(_tools, prepared.Path);
        Assert.Equal("mjpeg", outInfo.VideoCodec);
        Assert.Equal((1080, 1920), (outInfo.Width, outInfo.Height)); // feed recortaria para 4:5
    }

    [Fact]
    public async Task Long_video_is_rejected_for_story_before_converting()
    {
        var mp4 = await Generate("longo.mp4", "-f", "lavfi", "-i", "color=c=black:size=320x568:rate=5:duration=62", "-c:v", "libx264", "-pix_fmt", "yuv420p");
        var info = await MediaProbe.ProbeAsync(_tools, mp4);
        await Assert.ThrowsAsync<MediaToolException>(() => MediaConverter.PrepareAsync(_tools, mp4, info, _dir, PostKind.Story, null));
    }

    [Fact]
    public async Task Cover_frame_is_extracted_at_the_offset()
    {
        var mp4 = await Generate("capa.mp4", "-f", "lavfi", "-i", "testsrc2=size=1080x1920:rate=30:duration=4", "-c:v", "libx264", "-pix_fmt", "yuv420p");
        var frame = Path.Combine(_dir, "quadro.jpg");
        await MediaConverter.ExtractFrameAsync(_tools, mp4, TimeSpan.FromSeconds(2.5), frame);
        var info = await MediaProbe.ProbeAsync(_tools, frame);
        Assert.Equal(360, info.Width);
    }

    // ---------- Banco local ----------

    [Fact]
    public void Carousel_post_keeps_all_media_and_marks_them_published()
    {
        var db = new LibraryDatabase(_dir);
        foreach (var id in new[] { "a", "b", "c" })
        {
            db.AddMediaItem(new MediaItem
            {
                Id = id,
                Path = Path.Combine(_dir, id + ".jpg"),
                ThumbnailPath = Path.Combine(_dir, id + "-thumb.jpg"),
                AddedAt = DateTimeOffset.Now,
                Info = Image(1080, 1080),
            });
        }

        db.UpsertPost(new LocalPost { Id = "p", MediaItemId = "b", MediaItemIds = ["b", "a", "c"], Type = "CAROUSEL", State = "SCHEDULED", CreatedAt = DateTimeOffset.Now, UpdatedAt = DateTimeOffset.Now });
        Assert.Empty(db.GetPublishedMediaIds());
        Assert.EndsWith("b-thumb.jpg", db.GetPostThumbnails()["p"]); // capa = primeiro item

        // A sincronização atualiza só o estado: não pode perder os itens.
        var known = db.GetLocalPosts()["p"];
        db.UpsertPost(known with { State = "PUBLISHED", UpdatedAt = DateTimeOffset.Now });
        Assert.Equal(["a", "b", "c"], db.GetPublishedMediaIds().Order());
    }

    [Fact]
    public void Old_database_is_migrated_to_multi_media()
    {
        var dbPath = Path.Combine(_dir, "instapanel.db");
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE media_items (id TEXT PRIMARY KEY, path TEXT NOT NULL UNIQUE COLLATE NOCASE, kind TEXT NOT NULL,
                  width INTEGER NOT NULL, height INTEGER NOT NULL, size_bytes INTEGER NOT NULL, duration_ms INTEGER NOT NULL DEFAULT 0,
                  container TEXT, video_codec TEXT, audio_codec TEXT, pixel_format TEXT, frame_rate REAL NOT NULL DEFAULT 0,
                  bit_rate INTEGER NOT NULL DEFAULT 0, thumbnail_path TEXT, added_at INTEGER NOT NULL);
                CREATE TABLE local_posts (id TEXT PRIMARY KEY, media_item_id TEXT REFERENCES media_items(id) ON DELETE SET NULL,
                  type TEXT NOT NULL, caption TEXT, state TEXT NOT NULL, permalink TEXT, error TEXT,
                  created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL);
                INSERT INTO media_items (id, path, kind, width, height, size_bytes, added_at) VALUES ('m1', 'C:\x.jpg', 'Image', 1, 1, 1, 0);
                INSERT INTO local_posts (id, media_item_id, type, state, created_at, updated_at) VALUES ('old', 'm1', 'IMAGE', 'PUBLISHED', 0, 0);
                PRAGMA user_version = 1;
                """;
            cmd.ExecuteNonQuery();
        }

        var db = new LibraryDatabase(_dir);
        Assert.Contains("m1", db.GetPublishedMediaIds());
    }
}
