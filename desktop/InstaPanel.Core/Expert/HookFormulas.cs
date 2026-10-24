using System.Text.RegularExpressions;

namespace InstaPanel.Core.Expert;

/// <summary>As 10 fórmulas de gancho de 2026 (skill ig-hook-extractor) e a classificação de um gancho.</summary>
public static partial class HookFormulas
{
    public const string Saves = "salvamentos";
    public const string Shares = "envios";
    public const string Comments = "comentários";
    public const string Follows = "seguidores";

    public static readonly HookFormula NumberFirst = new("IG1", "Resultado com número", Saves,
        "{resultado com número exato} em {prazo}. O que eu fiz: {passo 1}, {passo 2}. Salva pra testar.");
    public static readonly HookFormula Contrarian = new("IG2", "Verdade contrária", Shares,
        "{conselho que todo mundo repete} não funciona pra {público}. O que funciona: {alternativa}.");
    public static readonly HookFormula Relatable = new("IG3", "Momento identificável", Comments,
        "Sabe quando você {situação específica do nicho}? {o que isso revela}. Me conta a sua.");
    public static readonly HookFormula Confession = new("IG4", "Confissão em mini-história", Comments,
        "Em {data}, eu {erro ou decisão com custo}. {o que aprendi}.");
    public static readonly HookFormula Listicle = new("IG5", "Carrossel em lista", Saves,
        "{N} {erros/dicas} de {tema} (o #{k} quase ninguém faz). Um item por slide, resumo no último.");
    public static readonly HookFormula BeforeAfter = new("IG6", "Antes e depois", Follows,
        "De {antes} para {depois} em {prazo}. Slide 2: o ponto de partida. Depois: os passos.");
    public static readonly HookFormula MythBuster = new("IG7", "Quebra de mito", Shares,
        "Mito: {crença do nicho}. Verdade: {o que os dados mostram}. Um par por slide.");
    public static readonly HookFormula Framework = new("IG8", "Método para copiar", Saves,
        "O método {nome} que uso pra {resultado}. Um passo por slide, o método inteiro no último.");
    public static readonly HookFormula PatternInterrupt = new("IG9", "Reel com quebra de padrão", Shares,
        "Texto na tela no 1º segundo: {afirmação inesperada}. Sem introdução, direto ao ponto.");
    public static readonly HookFormula HowI = new("IG10", "Reel \"como eu fiz\"", Saves,
        "Como eu {resultado} em {prazo}: {passo 1} → {passo 2} → {passo 3}, com legenda na tela.");
    public static readonly HookFormula Unclear = new("", "Sem fórmula clara", "",
        "Reescreva a primeira linha com uma promessa, um número ou uma tensão.");

    public static IReadOnlyList<HookFormula> All { get; } =
        [NumberFirst, Contrarian, Relatable, Confession, Listicle, BeforeAfter, MythBuster, Framework, PatternInterrupt, HowI];

    /// <summary>Classifica pelo gancho (primeira linha), pelo começo da legenda e pelo formato.</summary>
    public static HookFormula Classify(string hook, string caption, MediaFormat format)
    {
        var h = hook.Trim().ToLowerInvariant();
        var start = caption.Length > 300 ? caption[..300].ToLowerInvariant() : caption.ToLowerInvariant();
        if (h.Length == 0) return format == MediaFormat.Reel ? PatternInterrupt : Unclear;

        if (MythRegex().IsMatch(start)) return MythBuster;
        if (BeforeAfterRegex().IsMatch(start)) return BeforeAfter;
        if (format == MediaFormat.Reel && HowIRegex().IsMatch(h)) return HowI;
        if (FrameworkRegex().IsMatch(h)) return Framework;
        if (ListRegex().IsMatch(h)) return format == MediaFormat.Carousel ? Listicle : NumberFirst;
        if (ContrarianRegex().IsMatch(h)) return Contrarian;
        // O gancho (1ª linha) manda: "Sabe quando…" seguido de uma confissão continua sendo identificável.
        if (RelatableRegex().IsMatch(h)) return Relatable;
        if (FirstPersonRegex().IsMatch(start) && TimeMarkerRegex().IsMatch(start)) return Confession;
        if (h.Any(char.IsAsciiDigit)) return NumberFirst;
        if (format == MediaFormat.Reel) return PatternInterrupt;
        return Unclear;
    }

    public static bool IsSaveBait(HookFormula f) => f == NumberFirst || f == Listicle || f == BeforeAfter || f == Framework || f == HowI;

    public static bool IsSendBait(HookFormula f) => f == Contrarian || f == MythBuster || f == PatternInterrupt;

    [GeneratedRegex(@"\b(mito|mitos|myth|myths)\b")]
    private static partial Regex MythRegex();

    [GeneratedRegex(@"antes e depois|antes/depois|\bbefore\b.{0,40}\bafter\b|\bde (zero|0|\d[\d.,]*)\s*(\p{L}+\s+)?(para|pra|a)\s+\d|from \d[\d,]* to \d")]
    private static partial Regex BeforeAfterRegex();

    [GeneratedRegex(@"^(como eu|how i)\b")]
    private static partial Regex HowIRegex();

    [GeneratedRegex(@"\b(método|metodo|framework|fórmula|formula|checklist|passo a passo|regra d[aoe]s?|sistema)\b")]
    private static partial Regex FrameworkRegex();

    [GeneratedRegex(@"^\W{0,3}(\p{L}+\s){0,2}\d+\s+(\p{L}+\s)?(erros?|dicas?|formas?|maneiras?|passos?|coisas?|motivos?|ferramentas?|ideias?|apps?|livros?|segredos?|hábitos?|habitos?|perguntas?|truques?|mistakes?|tips?|ways?|steps?|things?|reasons?|tools?|ideas?)\b")]
    private static partial Regex ListRegex();

    [GeneratedRegex(@"^(pare|parem|esqueça|esqueca|chega de|ninguém|ninguem|você não precisa|voce nao precisa|opinião impopular|opiniao impopular|é mentira|e mentira|não (faça|poste|use)|stop|nobody|unpopular opinion|you don'?t need)\b|ninguém te (conta|fala)")]
    private static partial Regex ContrarianRegex();

    [GeneratedRegex(@"\b(eu|meu|minha|me|i|my)\b")]
    private static partial Regex FirstPersonRegex();

    [GeneratedRegex(@"\b(19|20)\d{2}\b|ano passado|semana passada|mês passado|mes passado|\bontem\b|há \d+ (anos?|meses|dias|semanas)|\bem (janeiro|fevereiro|março|marco|abril|maio|junho|julho|agosto|setembro|outubro|novembro|dezembro)\b|last year|yesterday")]
    private static partial Regex TimeMarkerRegex();

    [GeneratedRegex(@"^(sabe quando|quando você|quando voce|aquele momento|todo mundo que|ninguém:|ninguem:|pov:|when you|that moment)")]
    private static partial Regex RelatableRegex();
}
