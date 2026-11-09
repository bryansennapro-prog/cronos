namespace InstaPanel.Core.Expert;

// Entrada e saída do Especialista do perfil. As regras vêm das 9 skills de Instagram
// (perfil, audiência, legenda, gancho, hashtag, anti-IA, planejamento, carrossel, reaproveitamento).

/// <summary>Objetivo do perfil: muda o link, os posts fixados e o peso da promoção no plano.</summary>
public enum ProfileGoal
{
    Grow,
    Sell,
    Clients,
    Authority,
}

public enum MediaFormat
{
    Image,
    Carousel,
    Reel,
    Story,
}

public sealed record ProfileFacts(
    string Username,
    string? Name,
    string? Biography,
    string? Website,
    string? AccountType,
    bool HasProfilePicture,
    int? Followers,
    int? Follows,
    int? MediaCount);

/// <summary>Post já publicado no Instagram.</summary>
public sealed record PublishedPost(
    string Id,
    MediaFormat Format,
    string? Caption,
    DateTimeOffset PublishedAt,
    int? Likes,
    int? Comments,
    string? Permalink = null,
    string? ThumbnailUrl = null)
{
    /// <summary>Curtidas + comentários. Nulo quando as curtidas estão ocultas (não é zero).</summary>
    public int? Interactions => Likes is null ? null : Likes + (Comments ?? 0);
}

/// <summary>Post na fila do Worker, ainda não publicado.</summary>
public sealed record PlannedPost(MediaFormat Format, string? Caption, DateTimeOffset ScheduledAt);

public sealed record ExpertInput
{
    public required ProfileFacts Profile { get; init; }

    /// <summary>Mais recentes primeiro (como a Meta devolve).</summary>
    public required IReadOnlyList<PublishedPost> Posts { get; init; }

    public IReadOnlyList<PlannedPost> Planned { get; init; } = [];

    public ProfileGoal Goal { get; init; } = ProfileGoal.Grow;

    public DateTimeOffset Now { get; init; } = DateTimeOffset.Now;

    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;

    /// <summary>Mídias da biblioteca local que nunca viraram post.</summary>
    public int UnusedLibraryItems { get; init; }

    /// <summary>Seguidores na análise anterior, para mostrar a variação.</summary>
    public ExpertSnapshot? Previous { get; init; }
}

public enum FindingLevel
{
    Good,
    Info,
    Warning,
    Problem,
}

public sealed record Finding(FindingLevel Level, string Title, string Detail, string? Action = null);

public sealed record Metric(string Label, string Value, string? Note = null);

/// <summary>Indicador do resumo, já com o que ele significa ("acima da média") e o tom (bom/ruim).</summary>
public sealed record Highlight(string Key, string Label, string Value, string Meaning, FindingLevel Tone);

/// <summary>Um dos próximos passos, com a área de onde veio.</summary>
public sealed record Priority(string AreaKey, string Area, Finding Finding);

public sealed record ExpertSection(
    string Key,
    string Title,
    string Skill,
    string Summary,
    string Why,
    int? Score,
    IReadOnlyList<Metric> Metrics,
    IReadOnlyList<Finding> Findings);

/// <summary>Fórmula de gancho (IG1–IG10) e o sinal que ela busca.</summary>
public sealed record HookFormula(string Code, string Name, string Goal, string Template)
{
    public string Label => Code.Length == 0 ? Name : $"{Code} {Name}";
}

public sealed record RankedPost(PublishedPost Post, double? EngagementRate, double? VsMedian, HookFormula Formula);

public sealed record PlanSlot(
    DateOnly Date,
    string Time,
    MediaFormat Format,
    string Pillar,
    HookFormula Formula,
    string Goal,
    string Idea,
    string? AlreadyPlanned,
    string? Inspiration = null);

public sealed record ExpertReport(
    DateTimeOffset GeneratedAt,
    string Username,
    ProfileGoal Goal,
    int Score,
    string Headline,
    IReadOnlyList<Highlight> Highlights,
    IReadOnlyList<Priority> Priorities,
    IReadOnlyList<ExpertSection> Sections,
    IReadOnlyList<RankedPost> TopPosts,
    IReadOnlyList<PlanSlot> WeekPlan,
    int PostsAnalyzed);

/// <summary>O que fica salvo entre análises (para comparar seguidores).</summary>
public sealed record ExpertSnapshot(DateTimeOffset At, int? Followers, int Score);
