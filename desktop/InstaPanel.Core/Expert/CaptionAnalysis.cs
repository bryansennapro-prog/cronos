using System.Text;
using System.Text.RegularExpressions;

namespace InstaPanel.Core.Expert;

public enum CtaKind
{
    None,
    Save,
    Share,
    Comment,
    Follow,
    Link,
}

/// <summary>
/// Leitura de uma legenda com as regras das skills ig-caption-writer, ig-hashtag-strategist e
/// ig-humanizer (--mode audit): gancho antes do "mais" (125 caracteres), CTA, hashtags e marcas de texto de IA.
/// Os padrões cobrem português e inglês.
/// </summary>
public sealed partial record CaptionAnalysis
{
    public const int FoldLength = 125;
    public const int MaxEmoji = 3;
    public const int MaxHashtags = 5;
    public const int MarkersPerParagraph = 3;

    public required string Text { get; init; }
    public required string Hook { get; init; }
    public int HookLength { get; init; }
    public bool HookFitsFold { get; init; }
    public bool HookHasNumber { get; init; }
    public int Characters { get; init; }
    public int Words { get; init; }
    public IReadOnlyList<string> Hashtags { get; init; } = [];
    public bool HashtagsMidText { get; init; }
    public IReadOnlyList<string> SpamHashtags { get; init; } = [];
    public IReadOnlyList<string> BroadHashtags { get; init; } = [];
    public int Emojis { get; init; }
    public int EmDashes { get; init; }
    public int AllowedEmDashes { get; init; }
    public IReadOnlyList<string> AiMarkers { get; init; } = [];
    public int MaxMarkersInParagraph { get; init; }
    public IReadOnlyList<string> RevealBridges { get; init; } = [];
    public IReadOnlyList<string> Sincerity { get; init; } = [];
    public IReadOnlyList<string> EngagementBait { get; init; } = [];
    public string? DeadCloser { get; init; }
    public string? ClicheOpener { get; init; }
    public bool HasStaccato { get; init; }
    public CtaKind Cta { get; init; }
    public IReadOnlyList<string> OffPlatform { get; init; } = [];

    /// <summary>Reprova a legenda (skill ig-humanizer, "Blockers").</summary>
    public IReadOnlyList<string> Blockers { get; init; } = [];

    /// <summary>Passa, mas com ressalvas ("Warnings").</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public bool Passes => Blockers.Count == 0;

    public bool IsEmpty => Characters == 0;

    public static CaptionAnalysis Analyze(string? caption)
    {
        var text = (caption ?? "").Replace("\r\n", "\n").Trim();
        var lower = text.ToLowerInvariant();
        var lines = text.Split('\n', StringSplitOptions.TrimEntries).Where(l => l.Length > 0).ToList();
        var hook = lines.FirstOrDefault() ?? "";
        var hookLength = RuneCount(hook);
        var firstSentenceEnd = SentenceEndRegex().Match(text);
        var fits = hookLength > 0 && (hookLength <= FoldLength ||
                                      (firstSentenceEnd.Success && RuneCount(text[..(firstSentenceEnd.Index + 1)]) <= FoldLength));

        var hashtags = HashtagRegex().Matches(text).Select(m => m.Value.ToLowerInvariant()).ToList();
        var body = TrailingTagsRegex().Replace(text, "");
        var midText = HashtagRegex().IsMatch(body);
        var words = WordRegex().Count(HashtagRegex().Replace(text, ""));

        var emDashes = text.Count(c => c == '—') + EnDashClauseRegex().Count(text) + text.Split("--").Length - 1;
        var allowedDashes = Math.Max(1, (int)Math.Ceiling(words / 100.0));

        var paragraphs = ParagraphRegex().Split(text).Where(p => p.Trim().Length > 0).ToList();
        var markersByParagraph = paragraphs.Select(p => AiMarkerRegex().Matches(p.ToLowerInvariant()).Count).ToList();
        var markers = AiMarkerRegex().Matches(lower).Select(m => m.Value).Distinct().ToList();

        var lastLine = lines.Count > 0 ? TrailingTagsRegex().Replace(lines[^1], "").Trim().ToLowerInvariant() : "";
        if (lastLine.Length == 0 && lines.Count > 1) lastLine = lines[^2].ToLowerInvariant();
        var dead = DeadCloserRegex().Match(lastLine);
        var opener = ClicheOpenerRegex().Match(hook.ToLowerInvariant());

        var analysis = new CaptionAnalysis
        {
            Text = text,
            Hook = hook,
            HookLength = hookLength,
            HookFitsFold = fits,
            HookHasNumber = hook.Any(char.IsAsciiDigit),
            Characters = RuneCount(text),
            Words = words,
            Hashtags = hashtags,
            HashtagsMidText = midText,
            SpamHashtags = hashtags.Where(t => SpamTags.Contains(t[1..])).Distinct().ToList(),
            BroadHashtags = hashtags.Where(t => BroadTags.Contains(t[1..])).Distinct().ToList(),
            Emojis = CountEmoji(text),
            EmDashes = emDashes,
            AllowedEmDashes = allowedDashes,
            AiMarkers = markers,
            MaxMarkersInParagraph = markersByParagraph.Count == 0 ? 0 : markersByParagraph.Max(),
            RevealBridges = RevealBridgeRegex().Matches(lower).Select(m => m.Value.Trim()).Distinct().ToList(),
            Sincerity = SincerityRegex().Matches(lower).Select(m => m.Value).Distinct().ToList(),
            EngagementBait = BaitRegex().Matches(lower).Select(m => m.Value.Trim()).Distinct().ToList(),
            DeadCloser = dead.Success ? dead.Value : null,
            ClicheOpener = opener.Success ? opener.Value : null,
            HasStaccato = HasStaccatoRun(text),
            Cta = DetectCta(lower),
            OffPlatform = OffPlatformRegex().Matches(lower).Select(m => m.Value.Trim()).Distinct().ToList(),
        };
        return analysis with { Blockers = BuildBlockers(analysis), Warnings = BuildWarnings(analysis) };
    }

    private static List<string> BuildBlockers(CaptionAnalysis a)
    {
        var list = new List<string>();
        if (a.IsEmpty) return list;
        if (!a.HookFitsFold) list.Add($"o gancho passa dos {FoldLength} caracteres visíveis antes do \"mais\" ({a.HookLength})");
        if (a.Characters > CaptionStats.MaxCharacters) list.Add($"legenda com {a.Characters} caracteres (máximo {CaptionStats.MaxCharacters})");
        if (a.Hashtags.Count > MaxHashtags) list.Add($"{a.Hashtags.Count} hashtags (use de 3 a 5)");
        if (a.HashtagsMidText) list.Add("hashtags no meio do texto (deixe num bloco no fim)");
        if (a.EngagementBait.Count > 0) list.Add($"isca de engajamento (\"{a.EngagementBait[0]}\") é rebaixada pelo Instagram");
        if (a.ClicheOpener is { } o) list.Add($"abertura clichê (\"{o}\")");
        if (a.RevealBridges.Count > 0) list.Add($"ponte de revelação típica de IA (\"{a.RevealBridges[0]}\")");
        if (a.Sincerity.Count > 0) list.Add($"sinceridade anunciada (\"{a.Sincerity[0]}\")");
        if (a.DeadCloser is { } d) list.Add($"fechamento vazio (\"{d}\"): peça para salvar ou enviar, com um motivo");
        if (a.MaxMarkersInParagraph >= MarkersPerParagraph)
            list.Add($"parágrafo com {a.MaxMarkersInParagraph} palavras típicas de IA ({string.Join(", ", a.AiMarkers.Take(4))})");
        if (a.EmDashes > a.AllowedEmDashes) list.Add($"{a.EmDashes} travessões (limite ~1 a cada 100 palavras)");
        return list;
    }

    private static List<string> BuildWarnings(CaptionAnalysis a)
    {
        var list = new List<string>();
        if (a.IsEmpty)
        {
            list.Add("sem legenda: o post perde o gancho e as palavras que o Instagram usa para classificar o tema");
            return list;
        }
        if (a.Emojis > MaxEmoji) list.Add($"{a.Emojis} emojis (use de 0 a 3, com intenção)");
        if (a.Cta == CtaKind.None) list.Add("sem chamada para ação (salvar, enviar, comentar ou seguir)");
        if (a.SpamHashtags.Count > 0) list.Add($"hashtags de spam ({string.Join(" ", a.SpamHashtags)})");
        if (a.Hashtags.Count > 0 && a.BroadHashtags.Count > 1) list.Add($"{a.BroadHashtags.Count} hashtags genéricas demais ({string.Join(" ", a.BroadHashtags.Take(3))})");
        if (a.HasStaccato) list.Add("frases curtas empilhadas (\"Curto. Direto. Pronto.\") soam artificiais");
        if (a.OffPlatform.Count > 0) list.Add($"resto de outra plataforma (\"{a.OffPlatform[0]}\")");
        if (a.Characters > 150 && !a.Text.Any(char.IsAsciiDigit)) list.Add("nenhum número específico: um dado exato deixa o texto mais concreto");
        return list;
    }

    private static CtaKind DetectCta(string lower)
    {
        if (SaveCtaRegex().IsMatch(lower)) return CtaKind.Save;
        if (ShareCtaRegex().IsMatch(lower)) return CtaKind.Share;
        if (CommentCtaRegex().IsMatch(lower)) return CtaKind.Comment;
        if (FollowCtaRegex().IsMatch(lower)) return CtaKind.Follow;
        if (lower.Contains("link na bio") || lower.Contains("link in bio")) return CtaKind.Link;
        return CtaKind.None;
    }

    /// <summary>3+ frases seguidas de até 3 palavras ("Curto. Direto. Pronto.").</summary>
    private static bool HasStaccatoRun(string text)
    {
        var run = 0;
        foreach (var sentence in SentenceSplitRegex().Split(text))
        {
            var count = WordRegex().Count(sentence);
            if (count == 0) continue;
            run = count <= 3 ? run + 1 : 0;
            if (run >= 3) return true;
        }
        return false;
    }

    public static int RuneCount(string s) => s.EnumerateRunes().Count();

    public static int CountEmoji(string s) => s.EnumerateRunes().Count(r =>
        r.Value is >= 0x1F300 and <= 0x1FAFF or >= 0x2600 and <= 0x27BF or >= 0x1F000 and <= 0x1F2FF or 0x2B50 or 0x2B55);

    public static string Describe(CtaKind cta) => cta switch
    {
        CtaKind.Save => "salvar",
        CtaKind.Share => "enviar",
        CtaKind.Comment => "comentar",
        CtaKind.Follow => "seguir",
        CtaKind.Link => "link na bio",
        _ => "nenhuma",
    };

    /// <summary>Hashtags de "engajamento" que podem limitar o alcance.</summary>
    public static readonly HashSet<string> SpamTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "follow4follow", "f4f", "like4like", "l4l", "likeforlike", "likeforlikes", "followforfollow", "followback",
        "tagsforlikes", "followme", "instalike", "likes", "follow", "seguidores", "sigoavolta", "sdv", "curtidas",
        "seguesegue", "segueeuqueeusigo", "comment4comment", "c4c", "spam4spam",
    };

    /// <summary>Hashtags com milhões de posts: no máximo uma, como rótulo.</summary>
    public static readonly HashSet<string> BroadTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "love", "instagood", "photooftheday", "beautiful", "happy", "fashion", "photography", "art", "nature",
        "travel", "food", "fitness", "instagram", "brasil", "brazil", "amor", "vida", "style", "cute", "picoftheday",
        "selfie", "me", "explore", "viral", "fyp", "reels", "reel", "trending", "marketing", "music", "life",
        "motivation", "motivacao", "motivação", "explorepage", "insta", "foto", "photo", "model", "beauty", "moda",
    };

    // Precisa de ao menos uma letra: "#4" (como em "o #4 da lista") não é hashtag para o Instagram.
    [GeneratedRegex(@"#(?=[\p{N}_]*\p{L})[\p{L}\p{N}_]+")]
    private static partial Regex HashtagRegex();

    // Bloco final de hashtags (pode ter pontos ou "·" separando as linhas).
    [GeneratedRegex(@"(\s*(#(?=[\p{N}_]*\p{L})[\p{L}\p{N}_]+|[.·•]))+\s*$")]
    private static partial Regex TrailingTagsRegex();

    [GeneratedRegex(@"[\p{L}\p{N}]+(['’][\p{L}]+)?")]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"[.!?…](\s|$)")]
    private static partial Regex SentenceEndRegex();

    [GeneratedRegex(@"(?<=[.!?…])\s+|\n+")]
    private static partial Regex SentenceSplitRegex();

    [GeneratedRegex(@"\n\s*\n")]
    private static partial Regex ParagraphRegex();

    [GeneratedRegex(@"\p{L} – \p{L}")]
    private static partial Regex EnDashClauseRegex();

    // Vocabulário que leitores associam a texto de IA em 2026: conta por densidade (3+ no parágrafo).
    [GeneratedRegex(@"\b(crucial|cruciais|fundamentalmente|significativ[oa]s?|significativamente|notavelmente|abrangente|robust[oa]s?|alavanc\w*|potencializ\w*|foment\w*|cenário|panorama|empoder\w*|mergulh\w*|jornada|essencialmente|em última análise|nuances?|insights?|desbloque\w*|transformador[a]?|divisor de águas|sinergia|holístic[oa]|game.?changer|significant|notably|particularly|comprehensive|robust|leverag\w*|foster\w*|landscape|nuanced|streamlin\w*|elevat\w*|empower\w*|delv\w*|unlock\w*|harness\w*|fundamentally|essentially|ultimately|level up|dive in)\b")]
    private static partial Regex AiMarkerRegex();

    [GeneratedRegex(@"(e )?o resultado\?|sabe o que (aconteceu|mudou)\?|aqui está o (segredo|ponto)|vamos falar sobre|deixa eu te contar|reviravolta:|não é (só |apenas )?(sobre )?[^.,!?\n]{1,40}[,;] é (sobre )?|the result\?|here'?s the thing|let'?s talk about|plot twist|it'?s not [^.,!?\n]{1,30}, it'?s")]
    private static partial Regex RevealBridgeRegex();

    [GeneratedRegex(@"papo reto|real talk|vou ser (sincer[oa]|honest[oa])|sendo (sincer[oa]|honest[oa])|não vou mentir|nao vou mentir|deixa eu ser (sincer[oa]|honest[oa])|not gonna lie|let me be honest")]
    private static partial Regex SincerityRegex();

    [GeneratedRegex(@"coment[ae]\s+[""“]?(sim|eu quero|eu|quero)\b|marqu?[ea]\s+(\d+|um|uma|dois|duas|três|tres)\s+amig|d[áêe] (dois|2) toques|curta se|double tap|comment yes|tag \d+ friends|like if you")]
    private static partial Regex BaitRegex();

    [GeneratedRegex(@"(o que (vocês|voces|você|voce|vcs|vc) (acham|acha|acharam|achou)\??|concordam\?|deixe sua opinião|deixem suas opiniões|reflita sobre isso|thoughts\?|what do you think\??|let that sink in)\s*\W*$")]
    private static partial Regex DeadCloserRegex();

    [GeneratedRegex(@"^(no mundo (acelerado|atual|de hoje)|nos dias (de hoje|atuais)|hoje em dia,|in today'?s fast.paced|in today'?s world)")]
    private static partial Regex ClicheOpenerRegex();

    [GeneratedRegex(@"\b(salv[ae]|salvem|salvar|guard[ae] (esse|este|isso|esta)|save this|save it)\b")]
    private static partial Regex SaveCtaRegex();

    [GeneratedRegex(@"\b(mand[ae] (pr[ao]|para|esse|isso)|envi[ae] (pr[ao]|para|esse|isso)|compartilh[ae]|send this|share this|send it)\b")]
    private static partial Regex ShareCtaRegex();

    [GeneratedRegex(@"\b(me conta|me cont[ae]m|me diz|comenta|comente|responde|responda|nos comentários|in the comments)\b")]
    private static partial Regex CommentCtaRegex();

    [GeneratedRegex(@"\b(sig[ae] (o perfil|a página|@|pra mais|para mais)|segue (o perfil|@|pra mais)|follow for)")]
    private static partial Regex FollowCtaRegex();

    [GeneratedRegex(@"inscreva-se|se inscreva|ativ[ae] o sininho|\bretweet\b|🧵|\bthread\b|link (abaixo|na descrição)|clique no link|\bsubscribe\b|smash that|no meu canal|(postei|escrevi) no (linkedin|twitter|youtube)|originalmente (postado|publicado)")]
    private static partial Regex OffPlatformRegex();

    /// <summary>Texto curto para listas (primeira linha, até 80 caracteres).</summary>
    public static string Snippet(string? caption, int max = 80)
    {
        var first = (caption ?? "").Replace("\r\n", "\n").Split('\n', StringSplitOptions.TrimEntries).FirstOrDefault(l => l.Length > 0) ?? "";
        if (first.Length == 0) return "(sem legenda)";
        var sb = new StringBuilder();
        foreach (var r in first.EnumerateRunes())
        {
            if (sb.Length >= max) return sb.ToString().TrimEnd() + "…";
            sb.Append(r.ToString());
        }
        return sb.ToString();
    }
}
