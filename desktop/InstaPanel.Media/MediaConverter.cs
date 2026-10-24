using System.Globalization;
using InstaPanel.Core;

namespace InstaPanel.Media;

public sealed record PreparedMedia(string Path, string ContentType, bool IsTemporary);

/// <summary>Deixa o arquivo no formato que a Meta aceita (JPEG para imagem, MP4 H.264/AAC para vídeo).</summary>
public static class MediaConverter
{
    private const int ThumbnailWidth = 360;

    /// <summary>Prepara para o formato padrão da mídia (imagem → post, vídeo → Reel).</summary>
    public static Task<PreparedMedia> PrepareAsync(
        FfmpegTools tools, string input, MediaInfo info, string workDir,
        IProgress<double>? progress = null, CancellationToken ct = default) =>
        PrepareAsync(tools, input, info, workDir, MediaRules.PostKindFor(info.Kind), null, progress, ct);

    /// <param name="carouselAspect">Proporção comum do carrossel (imagens são recortadas para ela).</param>
    public static async Task<PreparedMedia> PrepareAsync(
        FfmpegTools tools, string input, MediaInfo info, string workDir, PostKind kind, double? carouselAspect,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(workDir);
        var compat = MediaRules.Evaluate(info, kind, carouselAspect);
        if (compat.Status == CompatibilityStatus.Unsupported)
            throw new MediaToolException(string.Join(" ", compat.Notes));

        if (info.Kind == MediaKind.Image)
        {
            var isJpeg = Path.GetExtension(input).ToLowerInvariant() is ".jpg" or ".jpeg";
            if (compat.Status == CompatibilityStatus.Ready && isJpeg)
            {
                progress?.Report(1);
                return new PreparedMedia(input, "image/jpeg", IsTemporary: false);
            }
            var crop = kind switch
            {
                PostKind.Story => null,
                PostKind.Carousel => MediaRules.ImageCropToAspect(info.Width, info.Height, carouselAspect ?? MediaRules.CarouselAspectFor(info)),
                _ => MediaRules.ImageCropFor(info.Width, info.Height),
            };
            var jpg = Path.Combine(workDir, $"{Guid.NewGuid():N}.jpg");
            await ConvertImageAsync(tools, input, info, crop, jpg, ct).ConfigureAwait(false);
            progress?.Report(1);
            return new PreparedMedia(jpg, "image/jpeg", IsTemporary: true);
        }

        var mp4 = Path.Combine(workDir, $"{Guid.NewGuid():N}.mp4");
        var args = compat.Status == CompatibilityStatus.Ready ? RemuxArgs(input, mp4) : TranscodeArgs(input, info, mp4);
        await RunWithProgressAsync(tools, args, info.Duration, progress, ct).ConfigureAwait(false);

        var limit = kind == PostKind.Story ? MediaRules.MaxStoryVideoBytes : MediaRules.MaxReelBytes;
        if (new FileInfo(mp4).Length > limit)
        {
            File.Delete(mp4);
            throw new MediaToolException($"Mesmo convertido, o vídeo passou de {limit / 1024 / 1024} MB. Encurte o vídeo.");
        }
        return new PreparedMedia(mp4, "video/mp4", IsTemporary: true);
    }

    /// <summary>Extrai um quadro do vídeo (prévia da capa do Reel).</summary>
    public static async Task ExtractFrameAsync(FfmpegTools tools, string input, TimeSpan at, string output, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var result = await tools.RunFfmpegAsync(
            ["-y", "-v", "error", "-ss", at.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", input,
             "-frames:v", "1", "-vf", $"scale='min({ThumbnailWidth},iw)':-2", "-q:v", "4", output], null, ct).ConfigureAwait(false);
        if (result.ExitCode != 0 || !File.Exists(output))
            throw new MediaToolException($"Falha ao extrair o quadro: {MediaProbe.LastLine(result.StdErrTail)}");
    }

    public static async Task CreateThumbnailAsync(FfmpegTools tools, string input, MediaInfo info, string output, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var args = new List<string> { "-y", "-v", "error" };
        if (info.Kind == MediaKind.Video)
        {
            var at = Math.Min(1.0, info.Duration.TotalSeconds * 0.1);
            args.AddRange(["-ss", at.ToString("0.###", CultureInfo.InvariantCulture)]);
        }
        args.AddRange(["-i", input, "-frames:v", "1", "-vf", $"scale='min({ThumbnailWidth},iw)':-2", "-q:v", "4", output]);

        var result = await tools.RunFfmpegAsync(args, null, ct).ConfigureAwait(false);
        if (result.ExitCode != 0 || !File.Exists(output))
            throw new MediaToolException($"Falha ao gerar miniatura: {MediaProbe.LastLine(result.StdErrTail)}");
    }

    private static async Task ConvertImageAsync(
        FfmpegTools tools, string input, MediaInfo info, (int Width, int Height)? crop, string output, CancellationToken ct)
    {
        var filters = new List<string>();
        var width = info.Width;
        if (crop is { } c)
        {
            filters.Add($"crop={c.Width}:{c.Height}");
            width = c.Width;
        }
        if (width > MediaRules.MaxImageWidth) filters.Add($"scale={MediaRules.MaxImageWidth}:-2");
        filters.Add("format=yuvj420p");

        foreach (var quality in new[] { 2, 5, 8 })
        {
            var args = new List<string> { "-y", "-v", "error", "-i", input, "-frames:v", "1", "-vf", string.Join(",", filters), "-q:v", quality.ToString(CultureInfo.InvariantCulture), "-map_metadata", "-1", output };
            var result = await tools.RunFfmpegAsync(args, null, ct).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new MediaToolException($"Falha ao converter a imagem: {MediaProbe.LastLine(result.StdErrTail)}");
            if (new FileInfo(output).Length <= MediaRules.MaxImageBytes) return;
        }
        throw new MediaToolException("Não foi possível deixar a imagem abaixo de 8 MB.");
    }

    internal static List<string> RemuxArgs(string input, string output) =>
    [
        "-y", "-v", "error", "-nostats", "-progress", "pipe:1",
        "-i", input,
        "-map", "0:v:0", "-map", "0:a:0?",
        "-c", "copy",
        "-movflags", "+faststart",
        output,
    ];

    internal static List<string> TranscodeArgs(string input, MediaInfo info, string output)
    {
        var side = MediaRules.MaxReelSide;
        var filters = new List<string>
        {
            $"scale=w='if(gte(iw,ih),min(iw,{side}),-2)':h='if(gte(iw,ih),-2,min(ih,{side}))'",
        };
        if (info.FrameRate > MediaRules.MaxReelFrameRate + 0.5) filters.Add("fps=60");
        filters.Add("format=yuv420p");

        return
        [
            "-y", "-v", "error", "-nostats", "-progress", "pipe:1",
            "-i", input,
            "-map", "0:v:0", "-map", "0:a:0?",
            "-vf", string.Join(",", filters),
            "-c:v", "libx264", "-preset", "medium", "-profile:v", "high", "-crf", "21",
            "-maxrate", "15M", "-bufsize", "30M",
            "-c:a", "aac", "-b:a", "128k", "-ar", "48000", "-ac", "2",
            "-movflags", "+faststart",
            output,
        ];
    }

    private static async Task RunWithProgressAsync(FfmpegTools tools, List<string> args, TimeSpan duration, IProgress<double>? progress, CancellationToken ct)
    {
        var totalUs = duration.TotalMicroseconds;
        var result = await tools.RunFfmpegAsync(args, line =>
        {
            if (progress is null || totalUs <= 0) return;
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
                && long.TryParse(line.AsSpan("out_time_us=".Length), out var us) && us > 0)
                progress.Report(Math.Clamp(us / totalUs, 0, 1));
            else if (line == "progress=end")
                progress.Report(1);
        }, ct).ConfigureAwait(false);

        if (result.ExitCode != 0)
            throw new MediaToolException($"Falha ao converter o vídeo: {MediaProbe.LastLine(result.StdErrTail)}");
    }
}
