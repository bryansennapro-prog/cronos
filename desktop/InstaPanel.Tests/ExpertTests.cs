using InstaPanel.Core.Expert;

namespace InstaPanel.Tests;

public class CaptionAnalysisTests
{
    [Fact]
    public void Clean_caption_passes_with_save_cta()
    {
        var a = CaptionAnalysis.Analyze("""
            7 erros que travam o seu perfil (o #4 eu cometi por 2 anos)

            Arrasta pro lado e salva pra revisar antes do próximo post.

            #crescimentoinstagram #dicasdeinstagram #marketingdigital
            """);

        Assert.True(a.HookFitsFold);
        Assert.True(a.HookHasNumber);
        Assert.Equal(CtaKind.Save, a.Cta);
        Assert.Equal(3, a.Hashtags.Count);
        Assert.False(a.HashtagsMidText);
        Assert.True(a.Passes, string.Join("; ", a.Blockers));
    }

    [Fact]
    public void Ai_vocabulary_bait_and_emoji_storm_fail()
    {
        var a = CaptionAnalysis.Analyze(
            "No mundo acelerado de hoje, é crucial alavancar sua presença digital de forma robusta e significativa. " +
            "Vamos mergulhar nessa jornada! Comenta SIM se você concorda 🚀🔥💯✨🙌");

        Assert.False(a.Passes);
        Assert.NotNull(a.ClicheOpener);
        Assert.NotEmpty(a.EngagementBait);
        Assert.True(a.MaxMarkersInParagraph >= CaptionAnalysis.MarkersPerParagraph);
        Assert.Equal(5, a.Emojis);
        Assert.Contains(a.Warnings, w => w.Contains("emojis"));
    }

    [Fact]
    public void Dead_closer_long_hook_and_hashtag_wall_are_blockers()
    {
        var hook = new string('a', 140);
        var tags = string.Join(" ", Enumerable.Range(1, 12).Select(i => $"#tag{i}"));
        var a = CaptionAnalysis.Analyze($"{hook}\n\nBom dia pra vocês. O que vocês acham?\n\n{tags}");

        Assert.False(a.HookFitsFold);
        Assert.Equal("o que vocês acham?", a.DeadCloser);
        Assert.Equal(12, a.Hashtags.Count);
        Assert.Equal(3, a.Blockers.Count);
    }

    [Fact]
    public void Long_first_line_fits_when_first_sentence_ends_before_fold()
    {
        var a = CaptionAnalysis.Analyze("Pare de postar todo dia. " + new string('x', 200));
        Assert.True(a.HookFitsFold);
    }

    [Fact]
    public void Hashtags_in_the_middle_and_spam_tags_are_detected()
    {
        var a = CaptionAnalysis.Analyze("Hoje tem #novidade no perfil, fiquem ligados.\n\n#follow4follow #like4like");

        Assert.True(a.HashtagsMidText);
        Assert.Equal(new[] { "#follow4follow", "#like4like" }, a.SpamHashtags);
        Assert.False(a.Passes);
    }

    [Fact]
    public void Em_dashes_are_capped_not_banned()
    {
        Assert.True(CaptionAnalysis.Analyze("Uma ideia — só uma, com um travessão.").Passes);
        Assert.False(CaptionAnalysis.Analyze("Uma — duas — três ideias curtas — demais.").Passes);
    }

    [Fact]
    public void Staccato_stack_is_a_warning()
    {
        var a = CaptionAnalysis.Analyze("Sem desculpas. Sem atalhos. Só trabalho. Salva esse post pra lembrar amanhã cedo.");
        Assert.True(a.HasStaccato);
    }

    [Fact]
    public void Empty_caption_has_no_blockers_but_warns()
    {
        var a = CaptionAnalysis.Analyze(null);
        Assert.True(a.IsEmpty);
        Assert.Empty(a.Blockers);
        Assert.Single(a.Warnings);
    }
}

public class HookFormulaTests
{
    [Theory]
    [InlineData("7 erros que travam o seu perfil", MediaFormat.Carousel, "IG5")]
    [InlineData("7 erros que travam o seu perfil", MediaFormat.Image, "IG1")]
    [InlineData("Pare de usar 30 hashtags", MediaFormat.Reel, "IG2")]
    [InlineData("Mito: postar todo dia faz crescer", MediaFormat.Carousel, "IG7")]
    [InlineData("Antes e depois: de 312 para 1.800 seguidores", MediaFormat.Carousel, "IG6")]
    [InlineData("O método 3-2-1 que uso pra planejar a semana", MediaFormat.Carousel, "IG8")]
    [InlineData("Como eu editei esse Reel em 18 minutos", MediaFormat.Reel, "IG10")]
    [InlineData("Sabe quando você posta e ninguém vê?", MediaFormat.Image, "IG3")]
    [InlineData("Em março de 2024 eu perdi meu maior cliente", MediaFormat.Image, "IG4")]
    [InlineData("Olha esse lugar", MediaFormat.Reel, "IG9")]
    [InlineData("Bom dia!", MediaFormat.Image, "")]
    public void Classifies_hooks(string hook, MediaFormat format, string code)
    {
        Assert.Equal(code, HookFormulas.Classify(hook, hook, format).Code);
    }
}

public class ProfileExpertTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private static ProfileFacts Profile(string? name = "Ana Lima | Confeitaria", string? bio = "Bolos de festa sem glúten em SP · 9 anos e 2.300 encomendas", string? site = "https://linktr.ee/ana") =>
        new("analima.doces", name, bio, site, "BUSINESS", true, 2000, 300, 40);

    private static List<PublishedPost> Posts(int count, Func<int, MediaFormat>? format = null, Func<int, string>? caption = null, Func<int, int?>? likes = null) =>
        Enumerable.Range(0, count).Select(i => new PublishedPost(
            $"p{i}",
            format?.Invoke(i) ?? (i % 3 == 0 ? MediaFormat.Carousel : i % 3 == 1 ? MediaFormat.Reel : MediaFormat.Image),
            caption?.Invoke(i) ?? "5 dicas para seu bolo não afundar\n\nSalva pra usar na próxima fornada.\n\n#bolosemgluten #confeitariaartesanal #bolodefesta",
            Now.AddDays(-i * 2).AddHours(-3),
            likes is null ? 100 + i : likes(i),
            10)).ToList();

    private static ExpertReport Run(ProfileFacts? profile = null, List<PublishedPost>? posts = null, List<PlannedPost>? planned = null,
        ProfileGoal goal = ProfileGoal.Grow) =>
        ProfileExpert.Analyze(new ExpertInput
        {
            Profile = profile ?? Profile(),
            Posts = posts ?? Posts(20),
            Planned = planned ?? [],
            Goal = goal,
            Now = Now,
            TimeZone = TimeZoneInfo.Utc,
        });

    private static ExpertSection Section(ExpertReport r, string key) => r.Sections.Single(s => s.Key == key);

    [Fact]
    public void Report_has_the_nine_skill_sections_and_a_week_plan()
    {
        var r = Run();

        Assert.Equal(
            new[] { "profile", "audience", "captions", "hooks", "hashtags", "humanizer", "carousel", "planner", "repurpose" },
            r.Sections.Select(s => s.Key));
        Assert.Equal(9, r.Sections.Select(s => s.Skill).Distinct().Count());
        Assert.Equal(7, r.WeekPlan.Count);
        Assert.Equal(new DateOnly(2026, 10, 24), r.WeekPlan[0].Date);
        Assert.InRange(r.Score, 0, 100);
        Assert.True(r.Priorities.Count <= 5);
        Assert.Equal(5, r.TopPosts.Count);
    }

    [Fact]
    public void Empty_name_bio_and_link_are_profile_problems_and_lead_priorities()
    {
        var r = Run(Profile(name: "", bio: "", site: ""), goal: ProfileGoal.Sell);
        var problems = Section(r, "profile").Findings.Where(f => f.Level == FindingLevel.Problem).Select(f => f.Title).ToList();

        Assert.Contains("Campo Nome vazio", problems);
        Assert.Contains("Bio vazia", problems);
        Assert.Contains("Sem link no perfil", problems);
        Assert.StartsWith("Perfil:", r.Priorities[0].Title);
    }

    [Fact]
    public void Name_without_keyword_and_cliche_bio_are_warnings()
    {
        var r = Run(Profile(name: "Ana Lima", bio: "Eu ajudo mulheres a empreender ✨🚀🔥💯🙌"));
        var warnings = Section(r, "profile").Findings.Where(f => f.Level == FindingLevel.Warning).Select(f => f.Title).ToList();

        Assert.Contains("Nome sem palavra-chave", warnings);
        Assert.Contains("Bio começa com \"Eu ajudo…\"", warnings);
        Assert.Contains("Emojis demais na bio", warnings);
    }

    [Fact]
    public void Hidden_likes_are_left_out_of_the_median()
    {
        var r = Run(posts: Posts(10, likes: i => i < 5 ? null : 200));
        var median = Section(r, "audience").Metrics.Single(m => m.Label == "Interações medianas por post");
        Assert.Equal("210", median.Value);
    }

    [Fact]
    public void Hashtag_walls_and_spam_tags_are_flagged()
    {
        var wall = "Novidade!\n\n" + string.Join(" ", Enumerable.Range(1, 20).Select(i => $"#bolo{i}")) + " #follow4follow";
        var r = Run(posts: Posts(12, caption: _ => wall));
        var titles = Section(r, "hashtags").Findings.Select(f => f.Title).ToList();

        Assert.Contains("Hashtags demais", titles);
        Assert.Contains("Hashtags de spam", titles);
        Assert.Contains(titles, t => t.StartsWith("Mesmo bloco de hashtags", StringComparison.Ordinal));
    }

    [Fact]
    public void Failing_scheduled_caption_is_a_problem_with_the_reason()
    {
        var planned = new List<PlannedPost>
        {
            new(MediaFormat.Image, "Bom dia! Comenta SIM se você quer a receita", Now.AddDays(1)),
            new(MediaFormat.Reel, "3 passos pra cobertura lisa\n\nSalva pra testar no sábado.", Now.AddDays(2)),
            new(MediaFormat.Story, null, Now.AddDays(2)),
        };
        var section = Section(Run(planned: planned), "humanizer");

        var problem = Assert.Single(section.Findings, f => f.Level == FindingLevel.Problem);
        Assert.Contains("isca de engajamento", problem.Detail);
        Assert.Equal("1 de 2 aprovadas", section.Metrics[0].Value);
    }

    [Fact]
    public void Best_format_and_low_cadence_come_from_the_data()
    {
        // Reels com o dobro das interações; um post a cada 10 dias.
        var posts = Enumerable.Range(0, 12).Select(i => new PublishedPost(
            $"p{i}", i % 2 == 0 ? MediaFormat.Reel : MediaFormat.Image, "Como eu faço bolo de pote em 20 minutos",
            Now.AddDays(-10 * i - 1), i % 2 == 0 ? 400 : 100, 0)).ToList();
        var r = Run(posts: posts);
        var audience = Section(r, "audience");

        Assert.Contains(audience.Findings, f => f.Title == "Formato mais forte: Reels");
        Assert.Contains(audience.Findings, f => f.Title == "Pouca constância" && f.Level == FindingLevel.Problem);
        Assert.Contains(r.Highlights, m => m.Label == "Formato mais forte" && m.Value == "Reels");
    }

    [Fact]
    public void Old_standout_posts_become_repurpose_candidates()
    {
        var posts = Posts(30, likes: i => i == 25 ? 2000 : 100);
        var section = Section(Run(posts: posts), "repurpose");
        Assert.Contains(section.Findings, f => f.Title.StartsWith("Reaproveite:", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_marks_days_that_already_have_posts()
    {
        var planned = new List<PlannedPost> { new(MediaFormat.Reel, "Como eu decoro em 5 minutos", new DateTimeOffset(2026, 10, 25, 18, 0, 0, TimeSpan.Zero)) };
        var r = Run(planned: planned);
        Assert.Equal("Já agendado: Reel 18:00", r.WeekPlan.Single(p => p.Date == new DateOnly(2026, 10, 25)).AlreadyPlanned);
    }

    [Fact]
    public void Account_without_posts_still_produces_a_report()
    {
        var r = Run(posts: []);
        Assert.Equal(9, r.Sections.Count);
        Assert.Empty(r.TopPosts);
        Assert.Contains(Section(r, "audience").Findings, f => f.Title == "Nenhum post publicado");
    }

    [Fact]
    public void Follower_delta_uses_the_previous_snapshot()
    {
        var r = ProfileExpert.Analyze(new ExpertInput
        {
            Profile = Profile(),
            Posts = Posts(5),
            Now = Now,
            TimeZone = TimeZoneInfo.Utc,
            Previous = new ExpertSnapshot(Now.AddDays(-7), 1880, 70),
        });
        Assert.Equal("+120 desde 16/10", r.Highlights[0].Note);
    }

    [Fact]
    public void Markdown_contains_every_section()
    {
        var r = Run();
        var md = ExpertMarkdown.From(r, TimeZoneInfo.Utc);
        Assert.StartsWith("# Especialista do perfil · @analima.doces", md);
        foreach (var s in r.Sections) Assert.Contains($"## {s.Title}", md);
        Assert.Contains("## Plano da semana", md);
    }

    [Fact]
    public void History_store_round_trips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ip-expert-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ExpertHistoryStore(dir);
            Assert.Null(store.Load());
            store.Save(new ExpertSnapshot(Now, 1234, 77));
            Assert.Equal(new ExpertSnapshot(Now, 1234, 77), store.Load());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
