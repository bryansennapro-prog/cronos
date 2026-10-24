using InstaPanel.Core;
using InstaPanel.Data;
using InstaPanel.Media;

namespace InstaPanel.Tests;

/// <summary>Testes com o FFmpeg de verdade (gera mídias sintéticas). Exigem ffmpeg/ffprobe instalados.</summary>
public sealed class MediaToolsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "instapanel-media-" + Guid.NewGuid().ToString("N"));
    private readonly FfmpegTools _tools = FfmpegTools.TryLocate() ?? throw new InvalidOperationException("FFmpeg não encontrado.");

    public MediaToolsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private async Task<string> Generate(string name, params string[] args)
    {
        var path = Path.Combine(_dir, name);
        var result = await _tools.RunFfmpegAsync(["-y", "-v", "error", .. args, path], null, CancellationToken.None);
        Assert.True(result.ExitCode == 0, result.StdErrTail);
        return path;
    }

    [Fact]
    public async Task Png_tall_image_becomes_cropped_jpeg_under_limits()
    {
        var png = await Generate("alta.png", "-f", "lavfi", "-i", "testsrc2=size=1080x1920", "-frames:v", "1");
        var info = await MediaProbe.ProbeAsync(_tools, png);
        Assert.Equal(MediaKind.Image, info.Kind);
        Assert.Equal("png", info.VideoCodec);
        Assert.Equal((1080, 1920), (info.Width, info.Height));

        var prepared = await MediaConverter.PrepareAsync(_tools, png, info, _dir);
        Assert.True(prepared.IsTemporary);
        Assert.Equal("image/jpeg", prepared.ContentType);

        var outInfo = await MediaProbe.ProbeAsync(_tools, prepared.Path);
        Assert.Equal("mjpeg", outInfo.VideoCodec);
        Assert.Equal((1080, 1350), (outInfo.Width, outInfo.Height));
        Assert.Equal(CompatibilityStatus.Ready, MediaRules.Evaluate(outInfo).Status);
    }

    [Fact]
    public async Task Ready_jpeg_is_sent_as_is()
    {
        var jpg = await Generate("quadrada.jpg", "-f", "lavfi", "-i", "testsrc2=size=1080x1080", "-frames:v", "1");
        var info = await MediaProbe.ProbeAsync(_tools, jpg);
        var prepared = await MediaConverter.PrepareAsync(_tools, jpg, info, _dir);
        Assert.False(prepared.IsTemporary);
        Assert.Equal(jpg, prepared.Path);
    }

    [Fact]
    public async Task Webm_vp9_becomes_h264_aac_mp4_with_progress()
    {
        var webm = await Generate("clipe.webm",
            "-f", "lavfi", "-i", "testsrc2=size=720x1280:rate=30:duration=4",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=4",
            "-c:v", "libvpx-vp9", "-deadline", "realtime", "-c:a", "libopus", "-shortest");
        var info = await MediaProbe.ProbeAsync(_tools, webm);
        Assert.Equal(MediaKind.Video, info.Kind);
        Assert.Equal("vp9", info.VideoCodec);
        Assert.Equal("opus", info.AudioCodec);
        Assert.InRange(info.Duration.TotalSeconds, 3.9, 4.2);
        Assert.Equal(CompatibilityStatus.NeedsConversion, MediaRules.Evaluate(info).Status);

        var reports = new List<double>();
        var prepared = await MediaConverter.PrepareAsync(_tools, webm, info, _dir, new SyncProgress(reports.Add));
        var outInfo = await MediaProbe.ProbeAsync(_tools, prepared.Path);

        Assert.Equal("video/mp4", prepared.ContentType);
        Assert.Equal("h264", outInfo.VideoCodec);
        Assert.Equal("aac", outInfo.AudioCodec);
        Assert.Equal("yuv420p", outInfo.PixelFormat);
        Assert.Equal(CompatibilityStatus.Ready, MediaRules.Evaluate(outInfo).Status);
        Assert.NotEmpty(reports);
        Assert.Equal(1, reports[^1], 2);
    }

    [Fact]
    public async Task Rotated_phone_video_reports_display_orientation()
    {
        var mp4 = await Generate("rotacionado.mp4",
            "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30:duration=3", "-c:v", "libx264", "-pix_fmt", "yuv420p");
        var rotated = Path.Combine(_dir, "rot90.mp4");
        var r = await _tools.RunFfmpegAsync(["-y", "-v", "error", "-display_rotation", "90", "-i", mp4, "-c", "copy", rotated], null, CancellationToken.None);
        Assert.True(r.ExitCode == 0, r.StdErrTail);

        var info = await MediaProbe.ProbeAsync(_tools, rotated);
        Assert.Equal((720, 1280), (info.Width, info.Height));
    }

    [Fact]
    public async Task Thumbnail_is_created_for_video()
    {
        var mp4 = await Generate("thumb.mp4", "-f", "lavfi", "-i", "testsrc2=size=1080x1920:rate=30:duration=3", "-c:v", "libx264", "-pix_fmt", "yuv420p");
        var info = await MediaProbe.ProbeAsync(_tools, mp4);
        var thumb = Path.Combine(_dir, "t.jpg");
        await MediaConverter.CreateThumbnailAsync(_tools, mp4, info, thumb);
        var t = await MediaProbe.ProbeAsync(_tools, thumb);
        Assert.Equal(360, t.Width);
    }

    [Fact]
    public async Task Unsupported_short_reel_is_rejected_before_converting()
    {
        var mp4 = await Generate("curto.mp4", "-f", "lavfi", "-i", "testsrc2=size=1080x1920:rate=30:duration=1", "-c:v", "libx264", "-pix_fmt", "yuv420p");
        var info = await MediaProbe.ProbeAsync(_tools, mp4);
        await Assert.ThrowsAsync<MediaToolException>(() => MediaConverter.PrepareAsync(_tools, mp4, info, _dir));
    }

    [Fact]
    public void Library_database_roundtrip()
    {
        var db = new LibraryDatabase(_dir);
        var file = Path.Combine(_dir, "x.mp4");
        var item = new MediaItem
        {
            Id = "abc",
            Path = file,
            AddedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000),
            Info = new MediaInfo { Kind = MediaKind.Video, Width = 1080, Height = 1920, SizeBytes = 123, VideoCodec = "h264", AudioCodec = null, FrameRate = 29.97, Duration = TimeSpan.FromMilliseconds(15_500), BitRate = 42 },
        };
        db.AddMediaItem(item);

        var loaded = Assert.Single(new LibraryDatabase(_dir).GetMediaItems());
        Assert.Equal(item.Info, loaded.Info);
        Assert.Equal("abc", db.FindByPath(file.ToUpperInvariant())?.Id); // caminho sem diferenciar maiúsculas

        db.UpsertPost(new LocalPost { Id = "p1", MediaItemId = "abc", Type = "REELS", State = "PROCESSING", CreatedAt = DateTimeOffset.Now, UpdatedAt = DateTimeOffset.Now });
        Assert.Empty(db.GetPublishedMediaIds());
        db.UpsertPost(new LocalPost { Id = "p1", MediaItemId = "abc", Type = "REELS", State = "PUBLISHED", CreatedAt = DateTimeOffset.Now, UpdatedAt = DateTimeOffset.Now });
        Assert.Contains("abc", db.GetPublishedMediaIds());

        db.RemoveMediaItem("abc");
        Assert.Empty(db.GetMediaItems());
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
