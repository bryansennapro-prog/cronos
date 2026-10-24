using System.Text.Json;

namespace InstaPanel.Cloud;

// Espelham worker/src/posts.ts e worker/src/uploads.ts.

public static class PostStates
{
    public const string Scheduled = "SCHEDULED";
    public const string Preparing = "PREPARING";
    public const string Processing = "PROCESSING";
    public const string Ready = "READY";
    public const string Publishing = "PUBLISHING";
    public const string Published = "PUBLISHED";
    public const string Failed = "FAILED";
    public const string Canceled = "CANCELED";

    /// <summary>Estados "na fila": ainda vão ser publicados.</summary>
    public static readonly IReadOnlyList<string> Pending = [Scheduled, Preparing, Processing, Ready, Publishing];

    public static bool IsFinal(string state) => state is Published or Failed or Canceled;

    /// <summary>Pode remarcar, editar ou cancelar (mesma regra do Worker).</summary>
    public static bool IsEditable(string state) => state is Scheduled or Preparing or Processing or Ready or Failed;
}

public sealed record UpdatePostRequest(DateTimeOffset? ScheduledAt = null, string? Caption = null, PostOptions? Options = null);

/// <summary>Filtros de GET /api/posts.</summary>
public sealed record PostQuery
{
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public DateTimeOffset? Before { get; init; }
    public IReadOnlyList<string>? States { get; init; }
    public int Limit { get; init; } = 50;

    internal string ToQueryString()
    {
        var parts = new List<string> { $"limit={Limit}" };
        if (From is { } f) parts.Add("from=" + Uri.EscapeDataString(f.UtcDateTime.ToString("O")));
        if (To is { } t) parts.Add("to=" + Uri.EscapeDataString(t.UtcDateTime.ToString("O")));
        if (Before is { } b) parts.Add("before=" + Uri.EscapeDataString(b.UtcDateTime.ToString("O")));
        if (States is { Count: > 0 } s) parts.Add("state=" + Uri.EscapeDataString(string.Join(",", s)));
        return string.Join("&", parts);
    }
}

public sealed record PostInfo(
    string Id,
    string Type,
    string? Caption,
    PostOptions? Options,
    DateTimeOffset ScheduledAt,
    string State,
    int Attempts,
    string? Error,
    string? Permalink,
    string? IgMediaId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<PostMediaRef> Media,
    IReadOnlyList<PostEvent>? Events);

public sealed record PostOptions(bool? ShareToFeed = null, int? ThumbOffsetMs = null);

public sealed record PostMediaRef(string Key, string Kind);

public sealed record PostEvent(string Kind, JsonElement? Detail, DateTimeOffset At);

public sealed record CreatePostRequest(
    string Type,
    string? Caption,
    IReadOnlyList<PostMediaKey> Media,
    PostOptions? Options = null,
    DateTimeOffset? ScheduledAt = null);

public sealed record PostMediaKey(string Key);

public sealed record UploadProgress(long BytesSent, long TotalBytes)
{
    public double Fraction => TotalBytes == 0 ? 1 : (double)BytesSent / TotalBytes;
}

internal sealed record PostEnvelope(PostInfo Post);

internal sealed record PostListEnvelope(IReadOnlyList<PostInfo> Posts);

internal sealed record UploadSession(string Key, string UploadId, long PartSize, int PartCount);

internal sealed record CreateUploadRequest(string ContentType, long SizeBytes, string? FileName);

internal sealed record UploadedPart(int PartNumber, string Etag);

internal sealed record CompleteUploadRequest(string Key, IReadOnlyList<UploadedPart> Parts);
