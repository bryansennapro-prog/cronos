using InstaPanel.Core;

namespace InstaPanel.Tests;

public class MediaRulesTests
{
    private static MediaInfo Image(int w, int h, string codec = "mjpeg", long size = 1_000_000) => new()
    {
        Kind = MediaKind.Image, Width = w, Height = h, SizeBytes = size, VideoCodec = codec,
    };

    private static MediaInfo Video(
        int w = 1080, int h = 1920, double seconds = 30, string vcodec = "h264", string? acodec = "aac",
        double fps = 30, string pix = "yuv420p", long size = 50_000_000, long bitrate = 8_000_000) => new()
    {
        Kind = MediaKind.Video, Width = w, Height = h, SizeBytes = size, VideoCodec = vcodec, AudioCodec = acodec,
        FrameRate = fps, PixelFormat = pix, Duration = TimeSpan.FromSeconds(seconds), BitRate = bitrate,
    };

    [Fact]
    public void Jpeg_in_range_is_ready()
    {
        Assert.Equal(CompatibilityStatus.Ready, MediaRules.Evaluate(Image(1080, 1350)).Status);
        Assert.Equal(CompatibilityStatus.Ready, MediaRules.Evaluate(Image(1080, 1080)).Status);
        Assert.Equal(CompatibilityStatus.Ready, MediaRules.Evaluate(Image(1080, 566)).Status); // ~1.91:1
    }

    [Theory]
    [InlineData("png")]
    [InlineData("webp")]
    public void Non_jpeg_needs_conversion(string codec)
    {
        var c = MediaRules.Evaluate(Image(1080, 1080, codec));
        Assert.Equal(CompatibilityStatus.NeedsConversion, c.Status);
        Assert.Contains(c.Notes, n => n.Contains("JPEG"));
    }

    [Fact]
    public void Tall_image_is_cropped_to_4x5()
    {
        Assert.Equal(CompatibilityStatus.NeedsConversion, MediaRules.Evaluate(Image(1080, 1920)).Status);
        Assert.Equal((1080, 1350), MediaRules.ImageCropFor(1080, 1920));
    }

    [Fact]
    public void Wide_image_is_cropped_to_191()
    {
        var crop = MediaRules.ImageCropFor(3000, 1000);
        Assert.NotNull(crop);
        Assert.Equal(1000, crop.Value.Height);
        Assert.InRange(crop.Value.Width / 1000.0, 1.90, 1.91);
        Assert.Null(MediaRules.ImageCropFor(1080, 1080));
    }

    [Fact]
    public void Large_or_heavy_image_needs_conversion()
    {
        Assert.Equal(CompatibilityStatus.NeedsConversion, MediaRules.Evaluate(Image(4000, 4000)).Status);
        Assert.Equal(CompatibilityStatus.NeedsConversion, MediaRules.Evaluate(Image(1080, 1080, size: 9_000_000)).Status);
    }

    [Fact]
    public void Standard_vertical_h264_reel_is_ready()
    {
        var c = MediaRules.Evaluate(Video());
        Assert.Equal(CompatibilityStatus.Ready, c.Status);
        Assert.Empty(c.Notes);
    }

    [Fact]
    public void Reel_without_audio_is_ready()
    {
        Assert.Equal(CompatibilityStatus.Ready, MediaRules.Evaluate(Video(acodec: null)).Status);
    }

    [Theory]
    [InlineData("hevc", "aac", 30, "yuv420p", 1080, 1920)]
    [InlineData("vp9", "opus", 30, "yuv420p", 1080, 1920)]
    [InlineData("h264", "mp3", 30, "yuv420p", 1080, 1920)]
    [InlineData("h264", "aac", 120, "yuv420p", 1080, 1920)]
    [InlineData("h264", "aac", 30, "yuv420p10le", 1080, 1920)]
    [InlineData("h264", "aac", 30, "yuv420p", 2160, 3840)]
    public void Reel_conversions(string vcodec, string acodec, double fps, string pix, int w, int h)
    {
        Assert.Equal(CompatibilityStatus.NeedsConversion, MediaRules.Evaluate(Video(w, h, vcodec: vcodec, acodec: acodec, fps: fps, pix: pix)).Status);
    }

    [Fact]
    public void Reel_duration_limits_are_unsupported()
    {
        Assert.Equal(CompatibilityStatus.Unsupported, MediaRules.Evaluate(Video(seconds: 2)).Status);
        Assert.Equal(CompatibilityStatus.Unsupported, MediaRules.Evaluate(Video(seconds: 16 * 60)).Status);
    }

    [Fact]
    public void Horizontal_reel_only_warns_about_aspect()
    {
        var c = MediaRules.Evaluate(Video(1920, 1080));
        Assert.Equal(CompatibilityStatus.Ready, c.Status);
        Assert.Contains(c.Notes, n => n.Contains("9:16"));
    }

    [Fact]
    public void Caption_stats_match_worker_rules()
    {
        var s = CaptionStats.From("  Olá 🚀 #viagem #praia_2026 @amigo.um @outra  ");
        Assert.Equal(2, s.Hashtags);
        Assert.Equal(2, s.Mentions);
        Assert.Equal("Olá 🚀 #viagem #praia_2026 @amigo.um @outra".EnumerateRunes().Count(), s.Characters);
        Assert.True(s.IsValid);

        Assert.False(CaptionStats.From(new string('a', 2201)).IsValid);
        Assert.False(CaptionStats.From(string.Join(' ', Enumerable.Range(0, 31).Select(i => $"#t{i}"))).IsValid);
    }
}
