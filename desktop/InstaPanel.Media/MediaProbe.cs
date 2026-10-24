using System.Globalization;
using System.Text.Json;
using InstaPanel.Core;

namespace InstaPanel.Media;

public static class MediaProbe
{
    public static readonly IReadOnlySet<string> ImageExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".tif", ".tiff" };

    public static readonly IReadOnlySet<string> VideoExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".m4v", ".mkv", ".webm", ".avi", ".wmv" };

    public static bool IsSupportedFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ImageExtensions.Contains(ext) || VideoExtensions.Contains(ext);
    }

    public static async Task<MediaInfo> ProbeAsync(FfmpegTools tools, string path, CancellationToken ct = default)
    {
        var result = await tools.RunFfprobeAsync(
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path], ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new MediaToolException($"Não foi possível ler o arquivo: {LastLine(result.StdErrTail)}");

        var kind = ImageExtensions.Contains(Path.GetExtension(path)) ? MediaKind.Image : MediaKind.Video;
        return Parse(result.StdOut, kind, new FileInfo(path).Length);
    }

    /// <summary>Interpreta a saída JSON do ffprobe. Separado para testes.</summary>
    public static MediaInfo Parse(string ffprobeJson, MediaKind kind, long sizeBytes)
    {
        using var doc = JsonDocument.Parse(ffprobeJson);
        var root = doc.RootElement;

        JsonElement? video = null, audio = null;
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = Str(s, "codec_type");
                var isCover = s.TryGetProperty("disposition", out var disp)
                              && disp.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1;
                if (type == "video" && video is null && !isCover) video = s;
                else if (type == "audio" && audio is null) audio = s;
            }
        }
        if (video is not { } v) throw new MediaToolException("O arquivo não tem imagem/vídeo.");

        var width = Int(v, "width");
        var height = Int(v, "height");
        var rotation = Math.Abs(Rotation(v)) % 180;
        if (rotation == 90) (width, height) = (height, width);

        root.TryGetProperty("format", out var format);
        var duration = Dbl(format, "duration");
        if (duration <= 0) duration = Dbl(v, "duration");

        return new MediaInfo
        {
            Kind = kind,
            Width = width,
            Height = height,
            SizeBytes = sizeBytes,
            Container = Str(format, "format_name"),
            VideoCodec = Str(v, "codec_name"),
            AudioCodec = audio is { } a ? Str(a, "codec_name") : null,
            PixelFormat = kind == MediaKind.Video ? Str(v, "pix_fmt") : null,
            FrameRate = kind == MediaKind.Video ? Rate(Str(v, "avg_frame_rate")) is > 0 and var r ? r : Rate(Str(v, "r_frame_rate")) : 0,
            Duration = kind == MediaKind.Video && duration > 0 ? TimeSpan.FromSeconds(duration) : TimeSpan.Zero,
            BitRate = (long)Dbl(format, "bit_rate"),
        };
    }

    private static int Rotation(JsonElement stream)
    {
        if (stream.TryGetProperty("side_data_list", out var sides))
        {
            foreach (var side in sides.EnumerateArray())
            {
                if (side.TryGetProperty("rotation", out var rot) && rot.TryGetDouble(out var deg)) return (int)Math.Round(deg);
            }
        }
        if (stream.TryGetProperty("tags", out var tags) && int.TryParse(Str(tags, "rotate"), out var tagRot)) return tagRot;
        return 0;
    }

    private static double Rate(string? fraction)
    {
        if (string.IsNullOrEmpty(fraction)) return 0;
        var parts = fraction.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0)
            return n / d;
        return double.TryParse(fraction, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : 0;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p)
            ? p.ValueKind == JsonValueKind.String ? p.GetString() : p.ToString()
            : null;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.TryGetInt32(out var i) ? i : 0;

    private static double Dbl(JsonElement e, string name) =>
        double.TryParse(Str(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    internal static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "erro desconhecido";
}
