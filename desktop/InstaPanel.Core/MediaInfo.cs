namespace InstaPanel.Core;

public enum MediaKind
{
    Image,
    Video,
}

/// <summary>Dados técnicos de um arquivo, lidos pelo ffprobe. Largura/altura já consideram a rotação.</summary>
public sealed record MediaInfo
{
    public required MediaKind Kind { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required long SizeBytes { get; init; }
    public string? Container { get; init; }
    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }
    public string? PixelFormat { get; init; }
    public double FrameRate { get; init; }
    public TimeSpan Duration { get; init; }
    public long BitRate { get; init; }

    public double AspectRatio => Height == 0 ? 0 : (double)Width / Height;
}

public enum PostKind
{
    /// <summary>Uma imagem no feed.</summary>
    Image,
    Reel,
    Carousel,
    Story,
}

public static class PostKinds
{
    /// <summary>Valor de "type" na API do Worker.</summary>
    public static string ApiType(this PostKind kind) => kind switch
    {
        PostKind.Image => "IMAGE",
        PostKind.Reel => "REELS",
        PostKind.Carousel => "CAROUSEL",
        _ => "STORIES",
    };

    public static (int Min, int Max) MediaCount(this PostKind kind) => kind == PostKind.Carousel ? (2, 10) : (1, 1);

    public static bool Accepts(this PostKind kind, MediaKind media) => kind switch
    {
        PostKind.Image => media == MediaKind.Image,
        PostKind.Reel => media == MediaKind.Video,
        _ => true,
    };

    public static bool HasCaption(this PostKind kind) => kind != PostKind.Story;
}
