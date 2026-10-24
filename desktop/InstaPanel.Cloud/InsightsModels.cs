namespace InstaPanel.Cloud;

// Espelham GET /api/insights (worker/src/insights.ts).

public sealed record ProfileInsights(IgProfile Profile, IReadOnlyList<IgMedia> Media, DateTimeOffset FetchedAt);

public sealed record IgProfile(
    string IgUserId,
    string Username,
    string? Name,
    string? Biography,
    string? Website,
    string? AccountType,
    string? ProfilePictureUrl,
    int? FollowersCount,
    int? FollowsCount,
    int? MediaCount);

/// <summary>Pedido de POST /api/insights/ai (worker/src/ai.ts): o relatório do Especialista + os posts.</summary>
public sealed record AiAnalysisRequest(
    string Goal,
    string ReportMarkdown,
    IReadOnlyList<AiPostInput> Posts,
    IReadOnlyList<AiPlannedInput> Planned);

public sealed record AiPostInput(string Format, string Date, int? Likes, int? Comments, string? Caption);

public sealed record AiPlannedInput(string Format, string ScheduledAt, string? Caption);

public sealed record AiAnalysis(string Analysis, string Model, string StopReason, bool Truncated, AiUsage Usage);

public sealed record AiUsage(int InputTokens, int OutputTokens);

/// <summary>Post publicado. <see cref="LikeCount"/> nulo = curtidas ocultas pelo dono, não zero.</summary>
public sealed record IgMedia(
    string Id,
    string? Caption,
    string MediaType,
    string? MediaProductType,
    DateTimeOffset Timestamp,
    int? LikeCount,
    int? CommentsCount,
    string? Permalink,
    string? ThumbnailUrl);
