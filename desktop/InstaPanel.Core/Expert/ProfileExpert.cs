using System.Globalization;
using System.Text.RegularExpressions;

namespace InstaPanel.Core.Expert;

/// <summary>
/// Especialista do perfil: aplica as regras das 9 skills de Instagram aos dados que o Cronos já tem
/// (perfil e posts lidos pela API da Meta, fila do Worker e biblioteca local) e devolve um relatório.
/// Não inventa números: tudo o que aparece vem dos dados; o que a API não mostra vira item "conferir no app".
/// </summary>
public static partial class ProfileExpert
{
    internal static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>Quantos posts recentes entram na leitura de legendas, ganchos e hashtags.</summary>
    public const int RecentWindow = 30;

    private const int MaxName = 30;
    private const int MaxBio = 150;
    private const int RepurposeMinAgeDays = 45;

    public static ExpertReport Analyze(ExpertInput input)
    {
        var ctx = new Context(input);
        var plan = BuildWeekPlan(ctx);
        var sections = new List<ExpertSection>
        {
            Profile(ctx),
            Audience(ctx),
            Captions(ctx),
            Hooks(ctx),
            Hashtags(ctx),
            Humanizer(ctx),
            Carousels(ctx),
            Planner(ctx),
            Repurpose(ctx),
        };

        var scored = sections.Where(s => s.Score is not null).ToList();
        var score = scored.Count == 0 ? 0 : (int)Math.Round(scored.Average(s => s.Score!.Value));

        var priorities = sections
            .SelectMany((s, i) => s.Findings
                .Where(f => f.Action is not null && f.Level is FindingLevel.Problem or FindingLevel.Warning)
                .Select(f => (Order: i, Item: new Priority(s.Key, s.Title, f))))
            .OrderByDescending(x => x.Item.Finding.Level)
            .ThenBy(x => x.Order)
            .Take(5)
            .Select(x => x.Item)
            .ToList();

        var top = ctx.Scored
            .OrderByDescending(p => p.Interactions)
            .Take(5)
            .Select(ctx.Rank)
            .ToList();

        return new ExpertReport(
            input.Now,
            input.Profile.Username,
            input.Goal,
            score,
            Headline(score),
            Highlights(ctx),
            priorities,
            sections,
            top,
            plan,
            ctx.Feed.Count);
    }

    private static string Headline(int score) => score switch
    {
        >= 80 => "Perfil forte: faltam ajustes finos.",
        >= 60 => "Bom caminho, com pontos claros para melhorar.",
        >= 40 => "Há ajustes importantes a fazer.",
        _ => "O perfil precisa de atenção.",
    };

    /// <summary>Os 4 números do resumo, cada um com o que significa em palavras simples.</summary>
    private static List<Highlight> Highlights(Context ctx)
    {
        var p = ctx.Input.Profile;
        var delta = FollowerDiff(ctx);
        var list = new List<Highlight>
        {
            new("followers", "Seguidores", p.Followers is { } f ? Num(f) : "—",
                FollowerDelta(ctx) ?? "Na próxima análise você vê quanto mudou",
                delta switch { > 0 => FindingLevel.Good, < 0 => FindingLevel.Warning, _ => FindingLevel.Info }),
        };

        if (ctx.MedianRate is { } rate)
        {
            var (meaning, tone) = rate switch
            {
                < 1 => ("Abaixo da média (o normal é de 1% a 3%)", FindingLevel.Warning),
                < 3 => ("Na média (o normal é de 1% a 3%)", FindingLevel.Info),
                _ => ("Acima da média (o normal é de 1% a 3%)", FindingLevel.Good),
            };
            list.Add(new("engagement", "Engajamento", Pct(rate, 1), meaning, tone));
        }
        else
        {
            list.Add(new("engagement", "Interações por post", ctx.Median is { } m ? Num(m) : "—", "Curtidas + comentários de um post típico", FindingLevel.Info));
        }

        var perWeek = ctx.PostsPerWeek;
        var (cadence, cadenceTone) = perWeek switch
        {
            < 2 => ("Pouco: o ideal é de 3 a 5 por semana", FindingLevel.Problem),
            < 3 => ("Abaixo do ideal (de 3 a 5 por semana)", FindingLevel.Warning),
            <= 10 => ("No ritmo ideal (de 3 a 5 por semana)", FindingLevel.Good),
            _ => ("Muito volume: qualidade conta mais", FindingLevel.Info),
        };
        list.Add(new("cadence", "Posts por semana", perWeek.ToString("0.0", Pt), cadence, cadenceTone));

        list.Add(ctx.BestFormat is { } best
            ? new("format", "Formato que mais funciona", FormatName(best.Format, plural: true), $"Rendem {Times(best.Ratio)} mais que um post típico seu", FindingLevel.Good)
            : new("format", "Formato que mais funciona", "—", "Poste em formatos diferentes para comparar", FindingLevel.Info));
        return list;
    }

    private static int? FollowerDiff(Context ctx) =>
        ctx.Input.Previous is { Followers: { } before } && ctx.Input.Profile.Followers is { } now ? now - before : null;

    private static string? FollowerDelta(Context ctx)
    {
        if (FollowerDiff(ctx) is not { } diff) return null;
        var sign = diff > 0 ? "+" : "";
        return $"{sign}{Num(diff)} desde {Local(ctx, ctx.Input.Previous!.At):dd/MM}";
    }

    // ---------- 1. Perfil (ig-profile-optimizer) ----------

    private static ExpertSection Profile(Context ctx)
    {
        var p = ctx.Input.Profile;
        var goal = ctx.Input.Goal;
        var f = new List<Finding>();
        var name = p.Name?.Trim() ?? "";
        var bio = p.Biography?.Trim() ?? "";
        var bioLength = CaptionAnalysis.RuneCount(bio);

        f.Add(p.HasProfilePicture
            ? new(FindingLevel.Info, "Foto de perfil", "Confira no app: rosto nítido ocupando o círculo, fundo simples e contraste alto. Ela aparece pequena, então detalhe se perde; contraste não.")
            : new(FindingLevel.Problem, "Sem foto de perfil", "Perfil sem foto passa a impressão de conta abandonada.", "Coloque uma foto de rosto recente, de perto, com fundo simples."));

        const string nameAction = "Use \"Seu nome | tema que as pessoas buscam\" em até 30 caracteres (ex.: \"Ana Lima | Confeitaria\").";
        if (name.Length == 0)
            f.Add(new(FindingLevel.Problem, "Campo Nome vazio", "O campo Nome (não o @) é o que a busca do Instagram indexa. Vazio, você não aparece quando procuram pelo seu tema.", nameAction));
        else if (CaptionAnalysis.RuneCount(name) > MaxName)
            f.Add(new(FindingLevel.Problem, "Nome longo demais", $"\"{name}\" tem {CaptionAnalysis.RuneCount(name)} caracteres; o limite é {MaxName}.", nameAction));
        else if (NameSeparatorRegex().IsMatch(name))
            f.Add(new(FindingLevel.Good, "Nome com palavra-chave", $"\"{name}\" combina nome e tema, que é o que a busca lê."));
        else if (Squash(name) == Squash(p.Username))
            f.Add(new(FindingLevel.Warning, "Nome repete o @", $"\"{name}\" é igual ao @ e não acrescenta uma palavra que as pessoas digitam na busca.", nameAction));
        else
            f.Add(new(FindingLevel.Warning, "Nome sem palavra-chave", $"\"{name}\" não diz o tema do perfil. A busca indexa o Nome, então ele é a correção de SEO mais forte do perfil.", nameAction));

        var handle = p.Username;
        var symbols = handle.Count(c => c is '_' or '.');
        if (handle.Length > 20 || handle.Any(char.IsAsciiDigit) || symbols >= 2)
            f.Add(new(FindingLevel.Info, "@ difícil de lembrar", $"@{handle} tem números, símbolos ou é longo. Se o @ já é conhecido, não troque; se está começando, um @ curto ajuda a ser lembrado."));

        if (bio.Length == 0)
        {
            f.Add(new(FindingLevel.Problem, "Bio vazia", "Sem bio, quem visita não sabe para quem é o perfil nem por que seguir.",
                "Escreva em até 150 caracteres: para quem você fala + o que posta + uma prova concreta."));
        }
        else
        {
            var bioAnalysis = CaptionAnalysis.Analyze(bio);
            var issues = 0;
            if (BioClicheRegex().IsMatch(bio.ToLowerInvariant()))
            {
                issues++;
                f.Add(new(FindingLevel.Warning, "Bio começa com \"Eu ajudo…\"", "A abertura mais repetida do Instagram. Comece pelo que o seguidor ganha.",
                    "Reescreva começando pelo benefício: tema + para quem + uma prova (ex.: um número real)."));
            }
            if (bioAnalysis.Emojis > 4)
            {
                issues++;
                f.Add(new(FindingLevel.Warning, "Emojis demais na bio", $"{bioAnalysis.Emojis} emojis em {bioLength} caracteres. Um emoji âncora ajuda; uma fileira deles tira a clareza.",
                    "Deixe no máximo 1 ou 2 emojis, com função (marcar uma linha ou o link)."));
            }
            if (bioAnalysis.AiMarkers.Count >= 2)
            {
                issues++;
                f.Add(new(FindingLevel.Warning, "Bio com cara de texto genérico", $"Palavras como {string.Join(", ", bioAnalysis.AiMarkers.Take(3))} soam como texto de IA.",
                    "Troque por palavras do seu dia a dia e um fato específico."));
            }
            if (!bio.Any(char.IsAsciiDigit))
                f.Add(new(FindingLevel.Info, "Bio sem prova concreta", "Um número real (anos de experiência, clientes atendidos, alunos, resultados) faz a bio ser lembrada. Use só se for verdade."));
            if (bioLength < 40)
                f.Add(new(FindingLevel.Info, "Bio curta", $"{bioLength} caracteres. Cabe dizer para quem é e o que você posta (limite {MaxBio})."));
            if (issues == 0)
                f.Add(new(FindingLevel.Good, "Bio sem vícios", $"{bioLength} de {MaxBio} caracteres, sem abertura clichê nem excesso de emojis."));
        }

        var site = p.Website?.Trim() ?? "";
        if (site.Length == 0)
        {
            var level = goal is ProfileGoal.Sell or ProfileGoal.Clients ? FindingLevel.Problem : FindingLevel.Warning;
            f.Add(new(level, "Sem link no perfil", "O link é o único caminho clicável para fora do Instagram.", LinkAction(goal)));
        }
        else if (LinkHubRegex().IsMatch(site))
        {
            f.Add(new(FindingLevel.Good, "Link agrupador", $"{site}. Garanta que o primeiro botão seja o do seu objetivo: {GoalName(goal)}."));
        }
        else
        {
            f.Add(new(FindingLevel.Good, "Link no perfil", $"{site}. Confira se ele leva direto ao que importa para o seu objetivo ({GoalName(goal)})."));
        }

        f.Add(new(FindingLevel.Info, "Categoria e destaques (conferir no app)",
            "A API não mostra a categoria nem os destaques. Categoria: um rótulo claro do nicho, que reforça a bio. Destaques: 4 a 6, na ordem da próxima pergunta do visitante (Comece aqui, Provas, Oferta, Dúvidas, Sobre), capas no mesmo estilo e nomes curtos."));

        var grid = ctx.Feed.Take(9).ToList();
        var metrics = new List<Metric>
        {
            new("Nome", name.Length == 0 ? "(vazio)" : name, $"{CaptionAnalysis.RuneCount(name)}/{MaxName}"),
            new("Bio", bioLength == 0 ? "(vazia)" : $"{bioLength}/{MaxBio} caracteres"),
            new("Link", site.Length == 0 ? "(sem link)" : site),
            new("Tipo de conta", AccountTypeName(p.AccountType)),
        };

        if (grid.Count > 0 && ctx.Median is { } median)
        {
            var weak = grid.Count(g => g.Interactions is { } i && i < median * 0.5);
            metrics.Add(new("Primeiros 9 do grid", $"{weak} abaixo da metade da mediana", $"{grid.Select(g => g.Format).Distinct().Count()} formato(s)"));
            if (weak >= 3)
                f.Add(new(FindingLevel.Warning, "Posts fracos no topo do grid", $"{weak} dos 9 primeiros posts tiveram menos da metade das interações de um post típico seu. O visitante julga o grid como um conjunto.",
                    "Fixe até 3 posts fortes (veja a sugestão abaixo) para cobrir os mais fracos."));
            else if (grid.Count == 9 && grid.Select(g => g.Format).Distinct().Count() == 1)
                f.Add(new(FindingLevel.Info, "Grid de um formato só", $"Os 9 primeiros são todos {FormatName(grid[0].Format, plural: true).ToLowerInvariant()}. Misturar formatos mostra variedade sem perder o estilo."));
            else
                f.Add(new(FindingLevel.Good, "Topo do grid consistente", "Poucos posts fracos entre os 9 primeiros."));
        }

        var pins = PinCandidates(ctx);
        if (pins.Count > 0)
        {
            f.Add(new(FindingLevel.Info, "Sugestão de posts fixados",
                "A API não informa os fixados. Pelos seus números, os melhores candidatos são: " +
                string.Join("; ", pins.Select(x => $"{FormatName(x.Format)} de {Local(ctx, x.PublishedAt):dd/MM} ({Times(ctx.VsMedian(x) ?? 1)} a mediana)")) + ".",
                null));
        }

        return Section("profile", "ig-profile-optimizer",
            "Foto, Nome, @, bio, link e o topo do grid: é aqui que o visitante decide seguir.", f, metrics);
    }

    private static List<PublishedPost> PinCandidates(Context ctx)
    {
        // Só posts acima da mediana: fixado é vitrine, não preenchimento.
        var ordered = ctx.Scored.Where(p => ctx.VsMedian(p) is >= 1.0).OrderByDescending(p => p.Interactions).ToList();
        var picks = new List<PublishedPost>();
        foreach (var p in ordered)
        {
            if (picks.Count == 3) break;
            if (picks.All(x => x.Format != p.Format)) picks.Add(p);
        }
        foreach (var p in ordered)
        {
            if (picks.Count == 3) break;
            if (!picks.Contains(p)) picks.Add(p);
        }
        return picks;
    }

    private static string LinkAction(ProfileGoal goal) => goal switch
    {
        ProfileGoal.Sell => "Coloque o link direto da oferta (ou um agrupador com a oferta no primeiro botão).",
        ProfileGoal.Clients => "Coloque o link de contato ou agendamento (WhatsApp, formulário ou agenda).",
        ProfileGoal.Authority => "Coloque o link do seu conteúdo principal (portfólio, site ou newsletter).",
        _ => "Coloque um link útil para quem acabou de chegar (newsletter, material gratuito ou seu melhor conteúdo).",
    };

    // ---------- 2. Desempenho e audiência (ig-audience-insights) ----------

    private static ExpertSection Audience(Context ctx)
    {
        var p = ctx.Input.Profile;
        var f = new List<Finding>();
        var metrics = new List<Metric>
        {
            new("Seguidores", p.Followers is { } fol ? Num(fol) : "—", FollowerDelta(ctx)),
            new("Seguindo", p.Follows is { } fw ? Num(fw) : "—"),
            new("Posts no perfil", p.MediaCount is { } mc ? Num(mc) : "—", $"{ctx.Feed.Count} analisados"),
            new("Interações medianas por post", ctx.Median is { } md ? Num(md) : "—", ctx.MedianRate is { } mr ? $"{Pct(mr, 1)} dos seguidores" : null),
            new("Posts por semana", ctx.PostsPerWeek.ToString("0.0", Pt), "últimos 30 dias"),
        };
        if (ctx.Feed.Count > 0) metrics.Add(new("Último post", DaysAgo((ctx.Input.Now - ctx.Feed[0].PublishedAt).TotalDays)));

        if (ctx.Feed.Count == 0)
        {
            f.Add(new(FindingLevel.Warning, "Nenhum post publicado", "Sem posts, não há o que medir. Comece pelo plano da semana.", "Publique o primeiro post do plano sugerido."));
            return Section("audience", "ig-audience-insights", "Sem posts para analisar ainda.", f, metrics);
        }

        if (ctx.Scored.Count == 0)
        {
            f.Add(new(FindingLevel.Info, "Curtidas ocultas", "Todos os posts estão com curtidas ocultas, então não dá para medir o desempenho por post."));
        }
        else
        {
            if (ctx.MedianRate is { } rate)
            {
                if (rate < 1)
                    f.Add(new(FindingLevel.Warning, "Engajamento baixo para o tamanho do perfil", $"{Pct(rate, 1)} dos seguidores interagem com um post típico (referência aproximada de mercado: 1% a 3% é a média).",
                        "Troque posts de aviso e bom-dia por conteúdo que alguém salvaria ou enviaria a um amigo."));
                else if (rate < 3)
                    f.Add(new(FindingLevel.Info, "Engajamento na média", $"{Pct(rate, 1)} dos seguidores interagem com um post típico (referência aproximada: 1% a 3%)."));
                else
                    f.Add(new(FindingLevel.Good, "Engajamento acima da média", $"{Pct(rate, 1)} dos seguidores interagem com um post típico (referência aproximada: 1% a 3%)."));
            }

            if (ctx.BestFormat is { } best)
                f.Add(new(FindingLevel.Good, $"Formato mais forte: {FormatName(best.Format, plural: true)}", $"Mediana de {Num(best.Median)} interações, {Times(best.Ratio)} a mediana geral ({best.Count} posts).",
                    null));
            foreach (var weak in ctx.FormatStats.Where(s => s.Count >= 3 && s.Ratio < 0.6))
                f.Add(new(FindingLevel.Warning, $"{FormatName(weak.Format, plural: true)} rendem pouco", $"Mediana de {Num(weak.Median)} interações, {Times(weak.Ratio)} a mediana geral ({weak.Count} posts).",
                    weak.Format == MediaFormat.Image
                        ? "Use imagem só para uma ideia de uma frase; para ensinar, prefira carrossel ou Reel."
                        : $"Reveja o gancho dos {FormatName(weak.Format, plural: true).ToLowerInvariant()} antes de cortar o formato."));
            foreach (var s in ctx.FormatStats) metrics.Add(new($"Mediana · {FormatName(s.Format, plural: true)}", Num(s.Median), $"{s.Count} posts · {Times(s.Ratio)}"));

            if (ctx.BestWindow is { } win)
                f.Add(new(FindingLevel.Good, $"Melhor horário no seu perfil: {win.Label}", $"Mediana de {Num(win.Median)} interações ({win.Count} posts nesse horário)."));
            else
                f.Add(new(FindingLevel.Info, "Poucos posts por horário", "Ainda não há posts suficientes em cada faixa de horário para comparar. Ponto de partida: dias úteis das 11h às 13h e das 19h às 21h, e domingo à noite."));
            if (ctx.BestDay is { } day)
                metrics.Add(new("Melhor dia", Pt.DateTimeFormat.GetDayName(day.Day), $"mediana {Num(day.Median)} · {day.Count} posts"));

            if (ctx.Trend is { } trend)
            {
                if (trend >= 1.15)
                    f.Add(new(FindingLevel.Good, "Desempenho subindo", $"Os 10 posts mais recentes têm mediana {Times(trend)} a dos 10 anteriores."));
                else if (trend <= 0.85)
                    f.Add(new(FindingLevel.Warning, "Desempenho caindo", $"Os 10 posts mais recentes têm mediana {Times(trend)} a dos 10 anteriores.",
                        "Volte ao formato e ao tipo de gancho dos seus melhores posts (veja Ganchos)."));
            }
        }

        var perWeek = ctx.PostsPerWeek;
        if (perWeek < 2)
            f.Add(new(FindingLevel.Problem, "Pouca constância", $"{perWeek.ToString("0.0", Pt)} posts por semana nos últimos 30 dias.", "Mantenha 3 a 5 posts por semana, todo mês. Constância vale mais que volume."));
        else if (perWeek < 3)
            f.Add(new(FindingLevel.Warning, "Constância abaixo do ideal", $"{perWeek.ToString("0.0", Pt)} posts por semana nos últimos 30 dias.", "Suba para 3 a 5 posts por semana (o plano abaixo tem 5)."));
        else if (perWeek > 10)
            f.Add(new(FindingLevel.Info, "Volume alto", $"{perWeek.ToString("0.0", Pt)} posts por semana. 4 a 5 posts bons com stories diários costumam render mais que muitos posts medianos."));
        else
            f.Add(new(FindingLevel.Good, "Boa constância", $"{perWeek.ToString("0.0", Pt)} posts por semana nos últimos 30 dias."));

        var idle = (ctx.Input.Now - ctx.Feed[0].PublishedAt).TotalDays;
        if (idle > 7)
            f.Add(new(FindingLevel.Warning, $"{(int)idle} dias sem postar", "Pausas longas esfriam a distribuição para os seguidores.", "Agende o próximo post ainda esta semana."));

        var hidden = ctx.Feed.Count(x => x.Likes is null);
        if (hidden > 0 && ctx.Scored.Count > 0)
            f.Add(new(FindingLevel.Info, "Alguns posts com curtidas ocultas", $"{hidden} post(s) ficaram de fora das medianas, porque curtida oculta não é zero."));

        f.Add(new(FindingLevel.Info, "Salvamentos e envios não entram aqui",
            "São os sinais que mais pesam em 2026, mas a Meta só os libera com a permissão de insights (instagram_business_manage_insights), que o Cronos não pede hoje. Esta leitura usa curtidas + comentários."));

        var summary = ctx.Median is { } m2
            ? $"Um post típico seu recebe {Num(m2)} interações" + (ctx.MedianRate is { } r2 ? $" ({Pct(r2, 1)} dos seguidores)." : ".")
            : "Sem curtidas visíveis para medir.";
        return Section("audience", "ig-audience-insights", summary, f, metrics);
    }

    // ---------- 3. Legendas (ig-caption-writer) ----------

    private static ExpertSection Captions(Context ctx)
    {
        var f = new List<Finding>();
        var recent = ctx.Recent;
        if (recent.Count == 0)
            return Section("captions", "ig-caption-writer", "Sem posts para analisar.", f, [], scoreWhenEmpty: false);

        var withText = recent.Where(p => !ctx.Caption(p).IsEmpty).ToList();
        var empty = recent.Count - withText.Count;
        var a = withText.Select(ctx.Caption).ToList();
        double Share(Func<CaptionAnalysis, bool> pred) => a.Count == 0 ? 0 : a.Count(pred) * 100.0 / a.Count;

        var fits = Share(x => x.HookFitsFold);
        var withNumber = Share(x => x.HookHasNumber);
        var withCta = Share(x => x.Cta != CtaKind.None);
        var topCta = a.Where(x => x.Cta != CtaKind.None).GroupBy(x => x.Cta).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        var metrics = new List<Metric>
        {
            new("Legendas analisadas", $"{withText.Count} de {recent.Count}", "posts mais recentes"),
            new("Gancho cabe antes do \"mais\"", Pct(fits), $"até {CaptionAnalysis.FoldLength} caracteres"),
            new("Gancho com número", Pct(withNumber)),
            new("Com chamada para ação", Pct(withCta), topCta == CtaKind.None ? null : $"mais comum: {CaptionAnalysis.Describe(topCta)}"),
            new("Tamanho mediano", a.Count == 0 ? "—" : $"{Num(Median(a.Select(x => (double)x.Characters)) ?? 0)} caracteres"),
        };

        if (empty > 0)
            f.Add(new(FindingLevel.Warning, $"{empty} post(s) sem legenda", "Sem legenda o post perde o gancho e as palavras que o Instagram usa para entender o tema.",
                "Escreva pelo menos a primeira linha: uma promessa, um número ou uma tensão."));

        if (a.Count > 0)
        {
            if (fits < 50)
                f.Add(new(FindingLevel.Problem, "Ganchos longos demais", $"Só {Pct(fits)} das legendas fecham a ideia principal antes do \"mais\".", "Termine a primeira frase em até 125 caracteres; o resto fica escondido."));
            else if (fits < 80)
                f.Add(new(FindingLevel.Warning, "Alguns ganchos passam do \"mais\"", $"{Pct(100 - fits)} das legendas só fazem sentido depois do corte de 125 caracteres.", "Encurte a primeira linha: ela precisa funcionar sozinha."));

            if (withCta < 50)
                f.Add(new(FindingLevel.Warning, "Poucas chamadas para ação", $"Só {Pct(withCta)} das legendas pedem algo.", "Feche com um pedido com motivo: \"salva pra usar no próximo post\" ou \"manda pra quem vive fazendo isso\"."));

            var dead = a.Where(x => x.DeadCloser is not null).ToList();
            if (dead.Count > 0)
                f.Add(new(FindingLevel.Warning, $"{dead.Count} legenda(s) fecham com pergunta vazia", $"Ex.: \"{dead[0].DeadCloser}\". Perguntas genéricas quase não geram resposta.",
                    "Troque por uma pergunta específica ou por um pedido de salvar/enviar."));

            var bait = a.Where(x => x.EngagementBait.Count > 0).ToList();
            if (bait.Count > 0)
                f.Add(new(FindingLevel.Problem, "Isca de engajamento", $"{bait.Count} legenda(s) com pedidos como \"{bait[0].EngagementBait[0]}\". O Instagram rebaixa esse tipo de pedido.",
                    "Remova \"comenta SIM\", \"marque 3 amigos\" e afins; peça para salvar ou enviar com um motivo."));

            AddComparison(ctx, f, withText, p => ctx.Caption(p).HookHasNumber, "Ganchos com número", "dos ganchos sem número");
            AddComparison(ctx, f, withText, p => ctx.Caption(p).Cta is CtaKind.Save or CtaKind.Share, "Legendas que pedem para salvar ou enviar", "das outras");

            if (fits >= 80 && withCta >= 50 && bait.Count == 0)
                f.Add(new(FindingLevel.Good, "Legendas bem estruturadas", "Gancho antes do \"mais\" e chamada para ação na maioria dos posts."));
        }

        return Section("captions", "ig-caption-writer",
            "Os primeiros 125 caracteres fazem todo o trabalho; o fechamento pede um salvamento ou um envio.", f, metrics);
    }

    /// <summary>Compara a mediana de dois grupos (só com 3+ posts em cada) e registra se a diferença for clara.</summary>
    private static void AddComparison(Context ctx, List<Finding> f, IReadOnlyList<PublishedPost> posts, Func<PublishedPost, bool> pred, string yesLabel, string noLabel)
    {
        var yes = posts.Where(p => p.Interactions is not null && pred(p)).Select(p => (double)p.Interactions!.Value).ToList();
        var no = posts.Where(p => p.Interactions is not null && !pred(p)).Select(p => (double)p.Interactions!.Value).ToList();
        if (yes.Count < 3 || no.Count < 3) return;
        var my = Median(yes)!.Value;
        var mn = Median(no)!.Value;
        if (mn <= 0) return;
        var ratio = my / mn;
        if (ratio >= 1.2)
            f.Add(new(FindingLevel.Good, $"{yesLabel} rendem mais", $"No seu perfil, {yesLabel.ToLowerInvariant()} têm mediana {Times(ratio)} a {noLabel} ({yes.Count} contra {no.Count} posts)."));
        else if (ratio <= 0.8)
            f.Add(new(FindingLevel.Info, $"{yesLabel} não fizeram diferença", $"Mediana {Times(ratio)} a {noLabel} ({yes.Count} contra {no.Count} posts)."));
    }

    // ---------- 4. Ganchos (ig-hook-extractor) ----------

    private static ExpertSection Hooks(Context ctx)
    {
        var f = new List<Finding>();
        var recent = ctx.Recent;
        if (recent.Count == 0)
            return Section("hooks", "ig-hook-extractor", "Sem posts para analisar.", f, [], scoreWhenEmpty: false);

        var groups = recent.GroupBy(ctx.Formula).OrderByDescending(g => g.Count()).ToList();
        var metrics = groups.Select(g =>
        {
            var med = Median(g.Where(p => p.Interactions is not null).Select(p => (double)p.Interactions!.Value));
            var note = med is { } m && ctx.Median is { } all && all > 0 ? $"{Times(m / all)} a mediana" : null;
            return new Metric(g.Key.Label, g.Count() == 1 ? "1 post" : $"{g.Count()} posts", note);
        }).ToList();

        var top = ctx.Scored.OrderByDescending(p => p.Interactions).Take(5).ToList();
        var topFormula = top.GroupBy(ctx.Formula).Where(g => g.Key != HookFormulas.Unclear && g.Count() >= 2)
            .OrderByDescending(g => g.Count()).FirstOrDefault();
        if (topFormula is not null)
            f.Add(new(FindingLevel.Good, $"Seus melhores posts usam {topFormula.Key.Label}", $"{topFormula.Count()} dos 5 posts com mais interações seguem essa fórmula (busca {topFormula.Key.Goal}).",
                null));
        else if (top.Count > 0)
        {
            var best = top[0];
            var formula = ctx.Formula(best);
            f.Add(new(FindingLevel.Info, "Seu melhor post", $"{FormatName(best.Format)} de {Local(ctx, best.PublishedAt):dd/MM}: \"{CaptionAnalysis.Snippet(best.Caption)}\" ({formula.Label})."));
        }

        var bestFormula = topFormula?.Key ?? (top.Count > 0 ? ctx.Formula(top[0]) : null);
        if (bestFormula is not null && bestFormula != HookFormulas.Unclear)
            f.Add(new(FindingLevel.Info, $"Modelo para repetir: {bestFormula.Label}", bestFormula.Template));

        var unclear = recent.Count(p => ctx.Formula(p) == HookFormulas.Unclear);
        if (unclear * 100.0 / recent.Count > 40)
            f.Add(new(FindingLevel.Warning, $"{unclear} ganchos sem fórmula clara", $"{Pct(unclear * 100.0 / recent.Count)} dos posts recentes começam sem promessa, número ou tensão.",
                "Escolha a fórmula pelo objetivo: salvamentos (IG1, IG5, IG8), envios (IG2, IG7, IG9), comentários (IG3, IG4)."));

        var dominant = groups.FirstOrDefault(g => g.Key != HookFormulas.Unclear && g.Count() * 100.0 / recent.Count > 50);
        if (dominant is not null)
            f.Add(new(FindingLevel.Info, $"{dominant.Key.Label} em mais da metade dos posts", "Repetir sempre a mesma fórmula cansa quem já segue. Alterne com outra que busque um sinal diferente."));

        var last10 = recent.Take(10).Select(ctx.Formula).ToList();
        if (last10.Count >= 5 && !last10.Any(HookFormulas.IsSendBait))
            f.Add(new(FindingLevel.Warning, "Nenhum post recente feito para ser enviado", "Envios por alcance são o sinal mais forte de 2026, e nenhum dos últimos 10 posts usa uma fórmula de envio.",
                "Teste uma verdade contrária (IG2) ou uma quebra de mito em carrossel (IG7)."));
        if (last10.Count >= 5 && !last10.Any(HookFormulas.IsSaveBait))
            f.Add(new(FindingLevel.Warning, "Nenhum post recente feito para ser salvo", "Listas, métodos e passo a passo continuam rendendo alcance por dias.",
                "Faça um carrossel em lista (IG5) ou um método para copiar (IG8)."));

        return Section("hooks", "ig-hook-extractor",
            "Qual das 10 fórmulas de gancho cada post usa, e quais funcionam no seu perfil.", f, metrics);
    }

    // ---------- 5. Hashtags (ig-hashtag-strategist) ----------

    private static ExpertSection Hashtags(Context ctx)
    {
        var f = new List<Finding>();
        var posts = ctx.Recent.Where(p => !ctx.Caption(p).IsEmpty).ToList();
        if (posts.Count == 0)
            return Section("hashtags", "ig-hashtag-strategist", "Sem legendas para analisar.", f, [], scoreWhenEmpty: false);

        var a = posts.Select(ctx.Caption).ToList();
        var counts = a.Select(x => x.Hashtags.Count).ToList();
        var sized = counts.Count(c => c is >= 3 and <= 5);
        var over = counts.Count(c => c > 5);
        var none = counts.Count(c => c == 0);
        var spam = a.SelectMany(x => x.SpamHashtags).Distinct().ToList();
        var repeated = a.Where(x => x.Hashtags.Count >= 3)
            .GroupBy(x => string.Join(" ", x.Hashtags.Distinct().Order()))
            .Where(g => g.Count() >= 3)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();

        var metrics = new List<Metric>
        {
            new("Média por post", counts.Average().ToString("0.0", Pt)),
            new("Posts com 3 a 5", Pct(sized * 100.0 / posts.Count)),
            new("Posts com mais de 5", Pct(over * 100.0 / posts.Count)),
            new("Posts sem hashtag", Pct(none * 100.0 / posts.Count)),
        };

        if (over * 100.0 / posts.Count > 30)
            f.Add(new(FindingLevel.Problem, "Hashtags demais", $"{over} de {posts.Count} posts usam mais de 5. Em 2026 o bloco de 30 hashtags parece spam e não traz alcance.",
                "Use 3 a 5 por post: 2-3 de nicho (menos de 50 mil posts), 1-2 médias e no máximo 1 ampla."));
        else if (over > 0)
            f.Add(new(FindingLevel.Warning, $"{over} post(s) com mais de 5 hashtags", "Passar de 5 não aumenta o alcance.", "Corte para 3 a 5, mantendo as mais específicas."));

        if (spam.Count > 0)
            f.Add(new(FindingLevel.Problem, "Hashtags de spam", $"{string.Join(" ", spam.Take(6))}. Tags de \"troca de curtidas\" podem limitar o alcance.", "Remova essas hashtags de todos os posts futuros."));

        var broadHeavy = a.Count(x => x.BroadHashtags.Count >= 2);
        if (broadHeavy > 0)
            f.Add(new(FindingLevel.Warning, "Hashtags genéricas demais", $"{broadHeavy} post(s) com 2+ hashtags gigantes (ex.: {string.Join(" ", a.SelectMany(x => x.BroadHashtags).Distinct().Take(4))}). Um perfil pequeno some nelas em segundos.",
                "Troque por hashtags de nicho, que só quem é do seu meio digitaria."));

        if (repeated is not null)
            f.Add(new(FindingLevel.Warning, $"Mesmo bloco de hashtags em {repeated.Count()} posts", $"\"{Truncate(repeated.Key, 70)}\". Blocos idênticos em muitos posts parecem automação.",
                "Gire as hashtags de nicho conforme o tema de cada post."));

        var mid = a.Count(x => x.HashtagsMidText);
        if (mid > 0)
            f.Add(new(FindingLevel.Warning, $"{mid} post(s) com hashtag no meio do texto", "Hashtag no meio da frase atrapalha a leitura.", "Deixe as hashtags num bloco no fim da legenda ou no primeiro comentário."));

        if (none * 100.0 / posts.Count > 70)
            f.Add(new(FindingLevel.Info, "Quase não usa hashtags", "Hoje elas funcionam como etiqueta de tema. 3 a 5 de nicho ajudam o Instagram a entender para quem mostrar o post."));

        var topTags = ctx.Scored.OrderByDescending(p => p.Interactions).Take(5)
            .SelectMany(p => ctx.Caption(p).Hashtags)
            .Where(t => !CaptionAnalysis.BroadTags.Contains(t[1..]) && !CaptionAnalysis.SpamTags.Contains(t[1..]))
            .GroupBy(t => t).OrderByDescending(g => g.Count()).Take(5).Select(g => g.Key).ToList();
        if (topTags.Count > 0)
            f.Add(new(FindingLevel.Info, "Hashtags dos seus melhores posts", $"{string.Join(" ", topTags)}. Confira o tamanho de cada uma na busca do app: abaixo de 50 mil posts é nicho."));

        if (over == 0 && spam.Count == 0 && broadHeavy == 0 && repeated is null && mid == 0)
            f.Add(new(FindingLevel.Good, "Hashtags no tamanho certo", "Sem excesso, sem spam e sem bloco repetido."));

        return Section("hashtags", "ig-hashtag-strategist",
            "3 a 5 hashtags no tamanho certo valem mais que 30 genéricas.", f, metrics);
    }

    // ---------- 6. Revisão anti-IA (ig-humanizer --mode audit) ----------

    private static ExpertSection Humanizer(Context ctx)
    {
        var f = new List<Finding>();
        var planned = ctx.Upcoming.Where(p => p.Format != MediaFormat.Story && !string.IsNullOrWhiteSpace(p.Caption)).ToList();
        var published = ctx.Feed.Where(p => !ctx.Caption(p).IsEmpty).Take(10).ToList();
        var plannedA = planned.Select(p => (Post: p, A: CaptionAnalysis.Analyze(p.Caption))).ToList();
        var publishedA = published.Select(ctx.Caption).ToList();

        var metrics = new List<Metric>
        {
            new("Agendadas revisadas", plannedA.Count == 0 ? "nenhuma" : $"{plannedA.Count(x => x.A.Passes)} de {plannedA.Count} aprovadas", "próximos 7 dias"),
            new("Últimas publicadas", publishedA.Count == 0 ? "nenhuma" : $"{publishedA.Count(x => x.Passes)} de {publishedA.Count} aprovadas"),
        };

        foreach (var (post, a) in plannedA.Where(x => !x.A.Passes).Take(5))
            f.Add(new(FindingLevel.Problem, $"Agendada para {Friendly(ctx, post.ScheduledAt)} reprovou",
                $"\"{CaptionAnalysis.Snippet(post.Caption, 60)}\": {string.Join("; ", a.Blockers)}.",
                "Edite a legenda no Calendário antes do horário."));
        if (plannedA.Count > 0 && plannedA.All(x => x.A.Passes))
            f.Add(new(FindingLevel.Good, "Legendas agendadas aprovadas", $"As {plannedA.Count} legendas da fila passam na revisão."));

        var failed = publishedA.Where(x => !x.Passes).ToList();
        if (failed.Count > 0)
        {
            var reasons = failed.SelectMany(x => x.Blockers.Select(ReasonKey)).GroupBy(r => r).OrderByDescending(g => g.Count())
                .Take(3).Select(g => $"{g.Key} ({g.Count()})");
            f.Add(new(failed.Count * 2 >= publishedA.Count ? FindingLevel.Warning : FindingLevel.Info,
                $"{failed.Count} das últimas {publishedA.Count} legendas reprovariam",
                $"Motivos mais comuns: {string.Join(", ", reasons)}.",
                failed.Count * 2 >= publishedA.Count ? "Antes de agendar, leia só a primeira linha e o fechamento: corte clichês, iscas e excesso de hashtags." : null));
        }
        else if (publishedA.Count > 0)
        {
            f.Add(new(FindingLevel.Good, "Últimas legendas aprovadas", "Nenhuma das últimas legendas tem vícios que reprovam."));
        }

        var all = publishedA.Concat(plannedA.Select(x => x.A)).ToList();
        var aiHeavy = all.Count(x => x.MaxMarkersInParagraph >= CaptionAnalysis.MarkersPerParagraph);
        if (aiHeavy > 0)
            f.Add(new(FindingLevel.Warning, "Texto com cara de IA", $"{aiHeavy} legenda(s) com 3+ palavras típicas de IA no mesmo parágrafo (crucial, alavancar, jornada, robusto…).",
                "Reescreva o parágrafo com palavras do seu dia a dia e um fato com data ou número."));
        var emoji = all.Count(x => x.Emojis > CaptionAnalysis.MaxEmoji);
        if (emoji > 0)
            f.Add(new(FindingLevel.Info, $"{emoji} legenda(s) com mais de 3 emojis", "Emoji espalhado em todo lugar é a assinatura de post genérico. Use até 3, com função."));
        var staccato = all.Count(x => x.HasStaccato);
        if (staccato > 0)
            f.Add(new(FindingLevel.Info, $"{staccato} legenda(s) com frases curtas empilhadas", "\"Sem X. Sem Y. Só Z.\" virou marca de texto artificial. Junte as ideias numa frase normal."));

        if (all.Count == 0)
            f.Add(new(FindingLevel.Info, "Nada para revisar", "Não há legendas publicadas nem agendadas."));

        return Section("humanizer", "ig-humanizer",
            "Revisão antes de publicar: gancho, tamanho, hashtags, emojis, fechamento e vícios de texto gerado.", f, metrics,
            scoreWhenEmpty: false);
    }

    private static string ReasonKey(string blocker) => blocker switch
    {
        _ when blocker.StartsWith("o gancho", StringComparison.Ordinal) => "gancho longo",
        _ when blocker.Contains("hashtags no meio", StringComparison.Ordinal) => "hashtag no meio do texto",
        _ when blocker.Contains("hashtags", StringComparison.Ordinal) => "hashtags demais",
        _ when blocker.StartsWith("isca", StringComparison.Ordinal) => "isca de engajamento",
        _ when blocker.StartsWith("fechamento", StringComparison.Ordinal) => "fechamento vazio",
        _ when blocker.StartsWith("abertura", StringComparison.Ordinal) => "abertura clichê",
        _ when blocker.StartsWith("ponte", StringComparison.Ordinal) => "ponte de revelação",
        _ when blocker.StartsWith("sinceridade", StringComparison.Ordinal) => "sinceridade anunciada",
        _ when blocker.StartsWith("parágrafo", StringComparison.Ordinal) => "vocabulário de IA",
        _ when blocker.Contains("travess", StringComparison.Ordinal) => "travessões",
        _ => "tamanho",
    };

    // ---------- 7. Carrosséis (ig-carousel-planner) ----------

    private static ExpertSection Carousels(Context ctx)
    {
        var f = new List<Finding>();
        var recent = ctx.Recent;
        if (recent.Count == 0)
            return Section("carousel", "ig-carousel-planner", "Sem posts para analisar.", f, [], scoreWhenEmpty: false);

        var carousels = recent.Where(p => p.Format == MediaFormat.Carousel).ToList();
        var share = carousels.Count * 100.0 / recent.Count;
        var stat = ctx.FormatStats.FirstOrDefault(s => s.Format == MediaFormat.Carousel);
        var plannedCount = ctx.Upcoming.Count(p => p.Format == MediaFormat.Carousel);
        var metrics = new List<Metric>
        {
            new("Carrosséis recentes", $"{carousels.Count} de {recent.Count}", Pct(share)),
            new("Mediana dos carrosséis", stat is null ? "—" : Num(stat.Median), stat is null ? null : $"{Times(stat.Ratio)} a mediana geral"),
            new("Agendados (7 dias)", plannedCount.ToString(Pt)),
        };

        if (carousels.Count == 0)
        {
            var candidate = ctx.Scored.Where(p => p.Format != MediaFormat.Carousel)
                .OrderByDescending(p => p.Interactions).FirstOrDefault();
            f.Add(new(FindingLevel.Warning, "Nenhum carrossel recente", "Carrossel é o formato dos salvamentos: listas, métodos e antes/depois.",
                candidate is null
                    ? "Faça um carrossel de 6 a 10 slides sobre o que você mais ensina."
                    : $"Transforme {Possessive(candidate.Format)} de {Local(ctx, candidate.PublishedAt):dd/MM} (\"{CaptionAnalysis.Snippet(candidate.Caption, 50)}\") num carrossel de 6 a 10 slides."));
        }
        else if (stat is { Count: >= 2 } s)
        {
            if (s.Ratio >= 1.2 && share < 30)
                f.Add(new(FindingLevel.Warning, $"Carrosséis rendem {Times(s.Ratio)} e são só {Pct(share)} dos posts", "No seu perfil, o carrossel está entre os formatos que mais funcionam.",
                    "Suba para 2 carrosséis por semana (o plano abaixo já inclui)."));
            else if (s.Ratio >= 1.2)
                f.Add(new(FindingLevel.Good, $"Carrosséis rendem {Times(s.Ratio)} a mediana", "Continue: é um formato forte no seu perfil."));
            else if (s.Ratio < 0.7 && s.Count >= 3)
                f.Add(new(FindingLevel.Warning, "Carrosséis abaixo da mediana", $"Rendem {Times(s.Ratio)} a mediana geral.",
                    "Capriche no slide 1 (promessa + curiosidade, ex.: \"o #4 quase ninguém faz\") e coloque o melhor ponto nos slides 2 e 3."));
            else
                f.Add(new(FindingLevel.Info, "Carrosséis na média", $"Rendem {Times(s.Ratio)} a mediana geral."));
        }

        var weakHooks = carousels.Count(p => !ctx.Caption(p).IsEmpty && !ctx.Caption(p).HookFitsFold);
        if (weakHooks > 0)
            f.Add(new(FindingLevel.Info, $"{weakHooks} carrossel(éis) com gancho longo na legenda", "A legenda do carrossel pode ser curta: repita a promessa do slide 1 na primeira linha."));

        f.Add(new(FindingLevel.Info, "Checklist do carrossel (a API não lê os slides)",
            "Slide 1 com promessa e curiosidade, nunca só um título. O melhor ponto nos slides 2-3. Um ponto por slide, legível em 2 segundos no formato 4:5. Último slide com o resumo e um único pedido (salvar ou seguir). 6 a 10 slides, sem misturar foto e vídeo."));

        return Section("carousel", "ig-carousel-planner",
            "O slide 1 ganha o deslize; o último ganha o salvamento.", f, metrics);
    }

    // ---------- 8. Planejamento (ig-content-planner) ----------

    private static ExpertSection Planner(Context ctx)
    {
        var f = new List<Finding>();
        var up = ctx.Upcoming;
        var feed = up.Where(p => p.Format != MediaFormat.Story).ToList();
        var reels = up.Count(p => p.Format == MediaFormat.Reel);
        var carousels = up.Count(p => p.Format == MediaFormat.Carousel);
        var stories = up.Count(p => p.Format == MediaFormat.Story);
        var formulas = feed.Select(p => HookFormulas.Classify(CaptionAnalysis.Analyze(p.Caption).Hook, p.Caption ?? "", p.Format)).ToList();

        var metrics = new List<Metric>
        {
            new("Feed agendado (7 dias)", feed.Count.ToString(Pt), "ideal: 4 a 5"),
            new("Reels", reels.ToString(Pt), "ideal: 2+"),
            new("Carrosséis", carousels.ToString(Pt), "ideal: 2+"),
            new("Stories", stories.ToString(Pt), "ideal: diários"),
            new("Para salvar / enviar", $"{formulas.Count(HookFormulas.IsSaveBait)} / {formulas.Count(HookFormulas.IsSendBait)}", "ideal: 3 / 2"),
        };

        if (feed.Count == 0)
            f.Add(new(FindingLevel.Warning, "Nada agendado para os próximos 7 dias", "Sem fila, a constância depende de lembrar de postar todo dia.",
                "Use o plano da semana abaixo e agende os posts em Novo post."));
        else if (feed.Count < 4)
            f.Add(new(FindingLevel.Info, $"{feed.Count} post(s) no feed na fila", "4 a 5 por semana é um ritmo sustentável. Os dias livres do plano abaixo mostram onde encaixar."));
        else if (feed.Count > 7)
            f.Add(new(FindingLevel.Info, $"{feed.Count} posts no feed em 7 dias", "Constância vale mais que volume: confira se todos são bons o bastante."));

        if (feed.Count > 0)
        {
            if (reels < 2) f.Add(new(FindingLevel.Info, "Menos de 2 Reels na semana", "Reels são o formato que mais alcança quem ainda não segue."));
            if (carousels < 2) f.Add(new(FindingLevel.Info, "Menos de 2 carrosséis na semana", "Carrosséis são o formato que mais gera salvamentos."));
        }
        if (stories < 3)
            f.Add(new(FindingLevel.Info, "Poucos stories na fila", "Stories diários (2 a 4 telas: bastidor, enquete e repost do último post) mantêm você no topo para quem já segue."));

        var repeated = formulas.Where(x => x != HookFormulas.Unclear).GroupBy(x => x).FirstOrDefault(g => g.Count() > 2);
        if (repeated is not null)
            f.Add(new(FindingLevel.Warning, $"{repeated.Key.Label} repetida {repeated.Count()} vezes", "Usar a mesma fórmula mais de 2 vezes na semana deixa o feed previsível.",
                "Troque uma delas por uma fórmula que busque outro sinal (veja o plano abaixo)."));

        if (feed.Count is >= 4 and <= 7 && reels >= 2 && carousels >= 2)
            f.Add(new(FindingLevel.Good, "Semana bem equilibrada", "Ritmo e mistura de formatos dentro do recomendado."));

        f.Add(new(FindingLevel.Info, "Plano sugerido para os próximos 7 dias",
            "Pilares: 40% educativo, 30% história, 20% engajamento, 10% promoção. Horários pelos seus dados quando há posts suficientes; senão, as janelas de referência."));

        return Section("planner", "ig-content-planner",
            "Reels para alcance, carrosséis para salvamentos, stories para relacionamento.", f, metrics, scoreWhenEmpty: true);
    }

    private static readonly HookFormula StoryPoll = new("", "Enquete e caixa de perguntas", HookFormulas.Comments,
        "2 a 4 telas: um bastidor, uma enquete sobre o tema da semana e o repost do último post.");

    private static readonly HookFormula StoryPromo = new("", "Repost + link", HookFormulas.Follows,
        "Repost do melhor post da semana e uma tela com o próximo passo (link ou mensagem).");

    private static List<PlanSlot> BuildWeekPlan(Context ctx)
    {
        var goal = ctx.Input.Goal;
        var reelStrong = ctx.FormatStats.FirstOrDefault(s => s.Format == MediaFormat.Reel) is { Ratio: >= 1.1 };
        var imageWeak = ctx.FormatStats.FirstOrDefault(s => s.Format == MediaFormat.Image) is { Count: >= 3, Ratio: < 0.6 };
        var carouselStrong = ctx.FormatStats.FirstOrDefault(s => s.Format == MediaFormat.Carousel) is { Ratio: >= 1.2 };
        var feedTime = ctx.BestWindow?.PostTime;

        var promoIdea = goal switch
        {
            ProfileGoal.Sell => "Lembrete da oferta com o link, depois de mostrar um resultado real.",
            ProfileGoal.Clients => "Bastidor de um trabalho para cliente e uma tela com o contato.",
            ProfileGoal.Authority => "Bastidor da sua rotina e o link para o seu conteúdo principal.",
            _ => "Repost do post que mais rendeu na semana, com um convite para seguir.",
        };

        var template = new Dictionary<DayOfWeek, (MediaFormat Format, string Pillar, HookFormula Formula, string Time, string Idea)>
        {
            [DayOfWeek.Monday] = (MediaFormat.Reel, "Educativo", HookFormulas.PatternInterrupt, "12:00",
                "Um erro comum do seu nicho, dito no 1º segundo com texto na tela."),
            [DayOfWeek.Tuesday] = goal == ProfileGoal.Authority
                ? (MediaFormat.Carousel, "Educativo", HookFormulas.Framework, "11:30", "O seu método, com nome, um passo por slide e o resumo no último.")
                : (MediaFormat.Carousel, "Educativo", HookFormulas.Listicle, "11:30", "Lista de 6 a 8 itens sobre o que você mais ensina; o melhor item no slide 2."),
            [DayOfWeek.Wednesday] = (MediaFormat.Story, "Engajamento", StoryPoll, "19:00",
                "Enquete sobre a dúvida mais comum do seu público."),
            [DayOfWeek.Thursday] = (MediaFormat.Carousel, "História", HookFormulas.BeforeAfter, "11:00",
                "Antes e depois de um resultado seu (só números reais), com os passos no meio."),
            [DayOfWeek.Friday] = reelStrong && imageWeak
                ? (MediaFormat.Reel, "Engajamento", HookFormulas.Relatable, "13:00", "Um momento que todo mundo do seu nicho já viveu, encenado em 10 segundos.")
                : (MediaFormat.Image, "Engajamento", HookFormulas.Relatable, "13:00", "Um momento que todo mundo do seu nicho já viveu, numa frase forte na imagem."),
            [DayOfWeek.Saturday] = carouselStrong && !reelStrong
                ? (MediaFormat.Carousel, "Educativo", HookFormulas.Framework, "10:00", "Um passo a passo que dá para salvar e seguir depois.")
                : (MediaFormat.Reel, "Educativo", HookFormulas.HowI, "10:00", "Como você faz uma tarefa do seu nicho, em 3 passos com legenda na tela."),
            [DayOfWeek.Sunday] = (MediaFormat.Story, "Promoção", StoryPromo, "18:00", promoIdea),
        };

        var topByFormat = ctx.Scored.GroupBy(p => p.Format)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.Interactions).First());

        var today = DateOnly.FromDateTime(Local(ctx, ctx.Input.Now).Date);
        var plan = new List<PlanSlot>();
        for (var i = 1; i <= 7; i++)
        {
            var date = today.AddDays(i);
            var t = template[date.DayOfWeek];
            var time = t.Format != MediaFormat.Story ? feedTime ?? t.Time : t.Time;
            string? inspiration = null;
            if (t.Format != MediaFormat.Story && topByFormat.TryGetValue(t.Format, out var reference) && ctx.VsMedian(reference) is >= 1.3 and var ratio)
                inspiration = $"Inspire-se {(t.Format == MediaFormat.Image ? "na" : "no")} {Possessive(t.Format)} de {Local(ctx, reference.PublishedAt):dd/MM}, que rendeu {Times(ratio)} um post típico.";

            var already = ctx.Upcoming.Where(p => DateOnly.FromDateTime(Local(ctx, p.ScheduledAt).Date) == date)
                .Select(p => $"{FormatName(p.Format)} {Local(ctx, p.ScheduledAt):HH:mm}").ToList();
            plan.Add(new PlanSlot(date, time, t.Format, t.Pillar, t.Formula, t.Formula.Goal, t.Idea,
                already.Count == 0 ? null : "Já agendado: " + string.Join(", ", already), inspiration));
        }
        return plan;
    }

    // ---------- 9. Reaproveitamento (ig-repurposer) ----------

    private static ExpertSection Repurpose(Context ctx)
    {
        var f = new List<Finding>();
        var cutoff = ctx.Input.Now.AddDays(-RepurposeMinAgeDays);
        var candidates = ctx.Scored
            .Where(p => p.PublishedAt < cutoff && ctx.VsMedian(p) is >= 1.5)
            .OrderByDescending(p => p.Interactions)
            .Take(3)
            .ToList();

        foreach (var p in candidates)
        {
            var target = p.Format switch
            {
                MediaFormat.Reel => "um carrossel (o ponto central de cada cena vira um slide)",
                MediaFormat.Carousel => "um Reel falando para a câmera (o slide 1 vira o texto do 1º segundo)",
                _ => "um carrossel (a ideia vira o slide 1, os detalhes viram os slides seguintes)",
            };
            f.Add(new(FindingLevel.Info, $"Reaproveite: {Noun(p.Format)} de {Local(ctx, p.PublishedAt):dd/MM} ({Times(ctx.VsMedian(p)!.Value)} a mediana)",
                $"\"{CaptionAnalysis.Snippet(p.Caption)}\". Refaça como {target}. Mantenha a ideia e os números; reescreva o gancho para os 125 caracteres."));
        }
        if (candidates.Count == 0)
            f.Add(new(FindingLevel.Info, "Sem post antigo de destaque ainda", $"Aparecem aqui os posts com mais de {RepurposeMinAgeDays} dias que renderam 1,5× a mediana ou mais."));

        var artifacts = ctx.Feed.Select(ctx.Caption).Where(a => a.OffPlatform.Count > 0).ToList();
        if (artifacts.Count > 0)
            f.Add(new(FindingLevel.Warning, $"{artifacts.Count} legenda(s) com resto de outra plataforma", $"Ex.: \"{artifacts[0].OffPlatform[0]}\". Post reaproveitado não deve parecer copiado.",
                "Remova \"inscreva-se\", \"thread\", \"clique no link abaixo\" e afins; no Instagram o link fica na bio."));

        var unused = ctx.Input.UnusedLibraryItems;
        if (unused > 0)
            f.Add(new(FindingLevel.Info, $"{unused} mídia(s) da biblioteca nunca publicadas", "Fotos e vídeos que você já importou no Cronos e ainda não viraram post.",
                null));

        var metrics = new List<Metric>
        {
            new("Candidatos a reaproveitar", candidates.Count.ToString(Pt), $"mais de {RepurposeMinAgeDays} dias e 1,5× a mediana"),
            new("Mídias paradas na biblioteca", unused.ToString(Pt)),
        };
        return Section("repurpose", "ig-repurposer",
            "O que já funcionou pode voltar em outro formato, sem parecer repetido.", f, metrics);
    }

    // ---------- Helpers ----------

    /// <summary>Nome de cada área em linguagem simples e por que ela importa (texto didático da tela).</summary>
    private static readonly Dictionary<string, (string Title, string Why)> Areas = new()
    {
        ["profile"] = ("Perfil", "É a primeira coisa que alguém vê ao visitar você. Em poucos segundos a pessoa decide se vai seguir."),
        ["audience"] = ("Resultados", "Mostra como seus posts estão sendo recebidos e qual formato funciona melhor no seu perfil."),
        ["captions"] = ("Legendas", "O Instagram só mostra o começo da legenda. Se a primeira linha não prende, ninguém toca em \"mais\"."),
        ["hooks"] = ("Primeira frase", "A primeira frase (o \"gancho\") decide se a pessoa para de rolar. Existem 10 jeitos testados de começar bem."),
        ["hashtags"] = ("Hashtags", "Hoje a hashtag serve para dizer o tema do post. Poucas e certeiras valem mais que muitas."),
        ["humanizer"] = ("Texto natural", "Texto com cara de robô ou de propaganda afasta as pessoas. Aqui revisamos suas legendas antes de sair."),
        ["carousel"] = ("Carrosséis", "Carrossel é o formato que as pessoas mais salvam, e salvamento ajuda o post a chegar mais longe."),
        ["planner"] = ("Sua semana", "Postar com constância vale mais que postar muito. Aqui está sua fila e um plano pronto para seguir."),
        ["repurpose"] = ("Reaproveitar", "O que já deu certo pode voltar em outro formato, com menos trabalho do que criar do zero."),
    };

    /// <summary>A nota em uma palavra.</summary>
    public static string Grade(int score) => score switch
    {
        >= 80 => "Ótimo",
        >= 60 => "Bom",
        >= 40 => "Atenção",
        _ => "Cuidado",
    };

    private static ExpertSection Section(string key, string skill, string summary, List<Finding> findings,
        List<Metric> metrics, bool scoreWhenEmpty = true)
    {
        var (title, why) = Areas[key];
        int? score = findings.Count == 0 && !scoreWhenEmpty
            ? null
            : Math.Clamp(100 - 30 * findings.Count(x => x.Level == FindingLevel.Problem) - 12 * findings.Count(x => x.Level == FindingLevel.Warning), 0, 100);
        var ordered = findings.OrderByDescending(x => x.Level switch
        {
            FindingLevel.Problem => 3,
            FindingLevel.Warning => 2,
            FindingLevel.Good => 1,
            _ => 0,
        }).ToList();
        return new ExpertSection(key, title, skill, summary, why, score, metrics, ordered);
    }

    internal static double? Median(IEnumerable<double> values)
    {
        var list = values.Order().ToList();
        if (list.Count == 0) return null;
        var mid = list.Count / 2;
        return list.Count % 2 == 1 ? list[mid] : (list[mid - 1] + list[mid]) / 2;
    }

    internal static string Num(double n) => Math.Round(n).ToString("N0", Pt);

    internal static string Pct(double value, int decimals = 0) => value.ToString(decimals == 0 ? "0" : "0.0", Pt) + "%";

    internal static string Times(double ratio) => ratio.ToString("0.0", Pt) + "×";

    private static string DaysAgo(double days) => days < 1 ? "hoje" : (int)days == 1 ? "há 1 dia" : $"há {(int)days} dias";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";

    private static string Squash(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    internal static DateTime Local(Context ctx, DateTimeOffset when) => TimeZoneInfo.ConvertTime(when, ctx.Input.TimeZone).DateTime;

    private static string Friendly(Context ctx, DateTimeOffset when)
    {
        var local = Local(ctx, when);
        return local.ToString("ddd, dd/MM 'às' HH:mm", Pt);
    }

    public static string FormatName(MediaFormat format, bool plural = false) => (format, plural) switch
    {
        (MediaFormat.Reel, false) => "Reel",
        (MediaFormat.Reel, true) => "Reels",
        (MediaFormat.Carousel, false) => "Carrossel",
        (MediaFormat.Carousel, true) => "Carrosséis",
        (MediaFormat.Story, false) => "Story",
        (MediaFormat.Story, true) => "Stories",
        (_, false) => "Imagem",
        _ => "Imagens",
    };

    /// <summary>"Reel", "carrossel", "imagem" (Reel é nome de produto e fica com maiúscula).</summary>
    private static string Noun(MediaFormat format) => format == MediaFormat.Reel ? "Reel" : FormatName(format).ToLowerInvariant();

    /// <summary>"seu Reel", "seu carrossel", "sua imagem".</summary>
    private static string Possessive(MediaFormat format) => (format == MediaFormat.Image ? "sua " : "seu ") + Noun(format);

    public static string GoalName(ProfileGoal goal) => goal switch
    {
        ProfileGoal.Sell => "vender um produto",
        ProfileGoal.Clients => "conseguir clientes",
        ProfileGoal.Authority => "construir autoridade",
        _ => "crescer em seguidores",
    };

    private static string AccountTypeName(string? type) => type switch
    {
        "MEDIA_CREATOR" => "Criador",
        "BUSINESS" => "Comercial",
        null or "" => "—",
        _ => type,
    };

    [GeneratedRegex(@"[|•·\-–—/]")]
    private static partial Regex NameSeparatorRegex();

    [GeneratedRegex(@"^\W*(eu ajudo|ajudo (você|voce|pessoas|mulheres|empresas|empreendedores)|i help)")]
    private static partial Regex BioClicheRegex();

    [GeneratedRegex(@"linktr\.ee|beacons\.ai|bio\.link|linkin\.bio|lnk\.bio|taplink|linkbio|campsite\.bio|hoo\.be", RegexOptions.IgnoreCase)]
    private static partial Regex LinkHubRegex();

    // ---------- Contexto: dados derivados, calculados uma vez ----------

    internal sealed record FormatStat(MediaFormat Format, int Count, double Median, double Ratio);

    internal sealed record TimeWindow(string Label, string PostTime, int Count, double Median);

    internal sealed record DayStat(DayOfWeek Day, int Count, double Median);

    internal sealed class Context
    {
        private static readonly (int From, int To, string Label, string PostTime)[] Windows =
        [
            (6, 11, "6h às 11h", "09:00"),
            (11, 14, "11h às 14h", "12:00"),
            (14, 18, "14h às 18h", "16:00"),
            (18, 22, "18h às 22h", "19:00"),
            (22, 30, "22h às 6h", "22:00"),
        ];

        private readonly Dictionary<string, CaptionAnalysis> _captions = [];
        private readonly Dictionary<string, HookFormula> _formulas = [];

        public Context(ExpertInput input)
        {
            Input = input;
            Feed = input.Posts.Where(p => p.Format != MediaFormat.Story).OrderByDescending(p => p.PublishedAt).ToList();
            Recent = Feed.Take(RecentWindow).ToList();
            Scored = Feed.Where(p => p.Interactions is not null).ToList();
            Median = ProfileExpert.Median(Scored.Select(p => (double)p.Interactions!.Value));
            MedianRate = Median is { } m && input.Profile.Followers is > 0 and var fol ? m * 100.0 / fol : null;
            Upcoming = input.Planned.Where(p => p.ScheduledAt >= input.Now && p.ScheduledAt < input.Now.AddDays(7))
                .OrderBy(p => p.ScheduledAt).ToList();

            var last30 = Feed.Count(p => p.PublishedAt >= input.Now.AddDays(-30));
            PostsPerWeek = last30 / (30 / 7.0);

            if (Median is { } overall && overall > 0)
            {
                FormatStats = Scored.GroupBy(p => p.Format)
                    .Where(g => g.Count() >= 2)
                    .Select(g =>
                    {
                        var med = ProfileExpert.Median(g.Select(p => (double)p.Interactions!.Value))!.Value;
                        return new FormatStat(g.Key, g.Count(), med, med / overall);
                    })
                    .OrderByDescending(s => s.Median)
                    .ToList();
                BestFormat = FormatStats.Count >= 2 && FormatStats[0] is { Count: >= 3, Ratio: >= 1.1 } best ? best : null;

                BestWindow = Windows
                    .Select(w =>
                    {
                        var items = Scored.Where(p => InWindow(LocalHour(p.PublishedAt), w.From, w.To)).ToList();
                        var med = ProfileExpert.Median(items.Select(p => (double)p.Interactions!.Value));
                        return new TimeWindow(w.Label, w.PostTime, items.Count, med ?? 0);
                    })
                    .Where(w => w.Count >= 3)
                    .OrderByDescending(w => w.Median)
                    .FirstOrDefault();

                BestDay = Scored.GroupBy(p => TimeZoneInfo.ConvertTime(p.PublishedAt, input.TimeZone).DayOfWeek)
                    .Where(g => g.Count() >= 3)
                    .Select(g => new DayStat(g.Key, g.Count(), ProfileExpert.Median(g.Select(p => (double)p.Interactions!.Value))!.Value))
                    .OrderByDescending(d => d.Median)
                    .FirstOrDefault();

                var ordered = Scored.OrderByDescending(p => p.PublishedAt).ToList();
                if (ordered.Count >= 16)
                {
                    var last = ProfileExpert.Median(ordered.Take(10).Select(p => (double)p.Interactions!.Value))!.Value;
                    var prev = ProfileExpert.Median(ordered.Skip(10).Take(10).Select(p => (double)p.Interactions!.Value))!.Value;
                    if (prev > 0) Trend = last / prev;
                }
            }
        }

        public ExpertInput Input { get; }
        public List<PublishedPost> Feed { get; }
        public List<PublishedPost> Recent { get; }
        public List<PublishedPost> Scored { get; }
        public List<PlannedPost> Upcoming { get; }
        public double? Median { get; }
        public double? MedianRate { get; }
        public double PostsPerWeek { get; }
        public List<FormatStat> FormatStats { get; } = [];
        public FormatStat? BestFormat { get; }
        public TimeWindow? BestWindow { get; }
        public DayStat? BestDay { get; }
        public double? Trend { get; }

        public CaptionAnalysis Caption(PublishedPost p)
        {
            if (!_captions.TryGetValue(p.Id, out var a)) _captions[p.Id] = a = CaptionAnalysis.Analyze(p.Caption);
            return a;
        }

        public HookFormula Formula(PublishedPost p)
        {
            if (!_formulas.TryGetValue(p.Id, out var f))
                _formulas[p.Id] = f = HookFormulas.Classify(Caption(p).Hook, Caption(p).Text, p.Format);
            return f;
        }

        public double? VsMedian(PublishedPost p) => p.Interactions is { } i && Median is > 0 and var m ? i / m : null;

        public RankedPost Rank(PublishedPost p) => new(
            p,
            p.Interactions is { } i && Input.Profile.Followers is > 0 and var fol ? i * 100.0 / fol : null,
            VsMedian(p),
            Formula(p));

        private int LocalHour(DateTimeOffset when) => TimeZoneInfo.ConvertTime(when, Input.TimeZone).Hour;

        private static bool InWindow(int hour, int from, int to) => to <= 24 ? hour >= from && hour < to : hour >= from || hour < to - 24;
    }
}
