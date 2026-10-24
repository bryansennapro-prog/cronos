using System.Globalization;

namespace InstaPanel.Core;

public enum CompatibilityStatus
{
    /// <summary>Vai como está (vídeo só é reempacotado para início rápido).</summary>
    Ready,
    /// <summary>O painel converte automaticamente antes de enviar.</summary>
    NeedsConversion,
    /// <summary>A Meta não aceita e não dá para corrigir automaticamente.</summary>
    Unsupported,
}

public sealed record Compatibility(CompatibilityStatus Status, IReadOnlyList<string> Notes)
{
    public string Summary => Status switch
    {
        CompatibilityStatus.Ready => "Pronto para publicar",
        CompatibilityStatus.NeedsConversion => "Será convertido automaticamente",
        _ => "Não compatível",
    };
}

/// <summary>
/// Requisitos da Instagram Graph API (Content Publishing). Os números estão aqui num lugar só
/// para facilitar ajustes se a Meta mudar os limites.
/// </summary>
public static class MediaRules
{
    public const double MinImageAspect = 4.0 / 5.0;      // 4:5 (retrato)
    public const double MaxImageAspect = 1.91;           // 1.91:1 (paisagem)
    public const long MaxImageBytes = 8 * 1024 * 1024;
    public const int MaxImageWidth = 1440;

    public static readonly TimeSpan MinReelDuration = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan MaxReelDuration = TimeSpan.FromMinutes(15);
    public const long MaxReelBytes = 300L * 1024 * 1024;
    public const int MaxReelSide = 1920;
    public const double MaxReelFrameRate = 60;
    public const long MaxReelBitRate = 25_000_000;

    public static readonly TimeSpan MaxShortVideoDuration = TimeSpan.FromSeconds(60); // Story e item de carrossel
    public const long MaxStoryVideoBytes = 100L * 1024 * 1024;

    public static PostKind PostKindFor(MediaKind kind) => kind == MediaKind.Image ? PostKind.Image : PostKind.Reel;

    /// <summary>Compatibilidade "padrão" (imagem → post, vídeo → Reel), usada na Biblioteca.</summary>
    public static Compatibility Evaluate(MediaInfo info) =>
        info.Kind == MediaKind.Image ? EvaluateImage(info) : EvaluateReel(info);

    /// <summary>
    /// Compatibilidade para um formato específico. <paramref name="carouselAspect"/> é a proporção comum
    /// do carrossel (o Instagram corta todos os itens pela proporção do primeiro).
    /// </summary>
    public static Compatibility Evaluate(MediaInfo info, PostKind kind, double? carouselAspect = null)
    {
        if (!kind.Accepts(info.Kind))
            return new Compatibility(CompatibilityStatus.Unsupported,
                [kind == PostKind.Reel ? "Reels precisam de um vídeo." : "Post no feed precisa de uma imagem (para vídeo, use Reel)."]);

        return (kind, info.Kind) switch
        {
            (PostKind.Image, _) => EvaluateImage(info),
            (PostKind.Reel, _) => EvaluateReel(info),
            (PostKind.Story, MediaKind.Image) => EvaluateImage(info, targetAspect: null, story: true),
            (PostKind.Story, _) => EvaluateShortVideo(info, MaxStoryVideoBytes, "Stories", recommend916: true),
            (_, MediaKind.Image) => EvaluateImage(info, carouselAspect ?? CarouselAspectFor(info)),
            _ => EvaluateShortVideo(info, MaxReelBytes, "Vídeos de carrossel", recommend916: false),
        };
    }

    /// <summary>Proporção do carrossel a partir do primeiro item (limitada à faixa do feed).</summary>
    public static double CarouselAspectFor(MediaInfo first) => Math.Clamp(first.AspectRatio, MinImageAspect, MaxImageAspect);

    public static Compatibility EvaluateImage(MediaInfo info) => EvaluateImage(info, targetAspect: null, story: false);

    /// <param name="targetAspect">Proporção exata exigida (carrossel). Null = faixa do feed, ou nenhuma em Stories.</param>
    private static Compatibility EvaluateImage(MediaInfo info, double? targetAspect, bool story = false)
    {
        var notes = new List<string>();
        var convert = false;

        if (info.VideoCodec != "mjpeg")
        {
            convert = true;
            notes.Add($"Formato {Describe(info.VideoCodec)} → JPEG.");
        }
        if (targetAspect is { } target)
        {
            if (Math.Abs(info.AspectRatio - target) > 0.01)
            {
                convert = true;
                notes.Add($"Será recortada para {FormatAspect(target)}, a proporção do primeiro item do carrossel.");
            }
        }
        else if (story)
        {
            if (Math.Abs(info.AspectRatio - 9.0 / 16.0) > 0.02)
                notes.Add($"Proporção {FormatAspect(info.AspectRatio)}: Stories são 9:16 (vertical); o Instagram completa com fundo.");
        }
        else if (info.AspectRatio < MinImageAspect - 0.005 || info.AspectRatio > MaxImageAspect + 0.005)
        {
            convert = true;
            notes.Add($"Proporção {FormatAspect(info.AspectRatio)} fora do aceito (4:5 a 1.91:1): será recortada no centro.");
        }
        if (info.Width > MaxImageWidth)
        {
            convert = true;
            notes.Add($"Largura {info.Width}px → {MaxImageWidth}px.");
        }
        if (info.SizeBytes > MaxImageBytes)
        {
            convert = true;
            notes.Add("Acima de 8 MB: será comprimida.");
        }

        return new Compatibility(convert ? CompatibilityStatus.NeedsConversion : CompatibilityStatus.Ready, notes);
    }

    public static Compatibility EvaluateReel(MediaInfo info)
    {
        if (info.Duration < MinReelDuration)
            return new Compatibility(CompatibilityStatus.Unsupported, ["Reels precisam ter pelo menos 3 segundos."]);
        if (info.Duration > MaxReelDuration)
            return new Compatibility(CompatibilityStatus.Unsupported, ["Reels podem ter no máximo 15 minutos."]);
        return EvaluateVideoEncoding(info, MaxReelBytes, recommend916: true, what: "Reels");
    }

    /// <summary>Vídeo de Story ou de carrossel: 3 a 60 segundos.</summary>
    private static Compatibility EvaluateShortVideo(MediaInfo info, long maxBytes, string what, bool recommend916)
    {
        if (info.Duration < MinReelDuration)
            return new Compatibility(CompatibilityStatus.Unsupported, [$"{what} precisam ter pelo menos 3 segundos."]);
        if (info.Duration > MaxShortVideoDuration + TimeSpan.FromMilliseconds(500))
            return new Compatibility(CompatibilityStatus.Unsupported, [$"{what} podem ter no máximo 60 segundos (este tem {info.Duration.TotalSeconds:0} s)."]);
        return EvaluateVideoEncoding(info, maxBytes, recommend916, what);
    }

    private static Compatibility EvaluateVideoEncoding(MediaInfo info, long maxBytes, bool recommend916, string what)
    {
        var notes = new List<string>();
        var convert = false;
        if (info.VideoCodec != "h264")
        {
            convert = true;
            notes.Add($"Vídeo {Describe(info.VideoCodec)} → H.264.");
        }
        if (info.AudioCodec is not null && info.AudioCodec != "aac")
        {
            convert = true;
            notes.Add($"Áudio {Describe(info.AudioCodec)} → AAC.");
        }
        if (info.PixelFormat is not null && info.PixelFormat != "yuv420p")
        {
            convert = true;
            notes.Add($"Cores {info.PixelFormat} → yuv420p.");
        }
        if (Math.Max(info.Width, info.Height) > MaxReelSide)
        {
            convert = true;
            notes.Add($"Resolução {info.Width}×{info.Height} → máx. {MaxReelSide}px no lado maior.");
        }
        if (info.FrameRate > MaxReelFrameRate + 0.5)
        {
            convert = true;
            notes.Add($"{info.FrameRate:0.#} fps → 60 fps.");
        }
        if (info.SizeBytes > maxBytes || info.BitRate > MaxReelBitRate)
        {
            convert = true;
            notes.Add("Arquivo pesado demais: será comprimido.");
        }

        if (recommend916 && Math.Abs(info.AspectRatio - 9.0 / 16.0) > 0.02)
        {
            notes.Add($"Proporção {FormatAspect(info.AspectRatio)}: o Instagram recomenda 9:16 (vertical) para {what}.");
        }

        return new Compatibility(convert ? CompatibilityStatus.NeedsConversion : CompatibilityStatus.Ready, notes);
    }

    /// <summary>Recorte central para uma proporção exata (itens de carrossel). Null se já está nela.</summary>
    public static (int Width, int Height)? ImageCropToAspect(int width, int height, double aspect)
    {
        var current = (double)width / height;
        if (Math.Abs(current - aspect) <= 0.01) return null;
        return current > aspect ? (Even(height * aspect), height) : (width, Even(width / aspect));
    }

    /// <summary>Recorte central para caber em 4:5–1.91:1. Null se a imagem já está na faixa.</summary>
    public static (int Width, int Height)? ImageCropFor(int width, int height)
    {
        var aspect = (double)width / height;
        if (aspect < MinImageAspect - 0.005) return (width, Even(width / MinImageAspect));
        if (aspect > MaxImageAspect + 0.005) return (Even(height * MaxImageAspect), height);
        return null;
    }

    private static int Even(double v) => (int)Math.Floor(v / 2) * 2;

    private static string Describe(string? codec) => codec switch
    {
        null => "desconhecido",
        "mjpeg" => "JPEG",
        "png" => "PNG",
        "webp" => "WebP",
        "hevc" => "HEVC/H.265",
        "h264" => "H.264",
        _ => codec.ToUpperInvariant(),
    };

    public static string FormatAspect(double aspect)
    {
        (string Name, double Value)[] common = [("9:16", 9 / 16.0), ("4:5", 0.8), ("1:1", 1), ("4:3", 4 / 3.0), ("16:9", 16 / 9.0), ("3:4", 0.75), ("2:3", 2 / 3.0), ("3:2", 1.5)];
        foreach (var (name, value) in common)
        {
            if (Math.Abs(aspect - value) < 0.01) return name;
        }
        return aspect.ToString("0.##", CultureInfo.InvariantCulture) + ":1";
    }
}
