using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstaPanel.App.Controls;
using InstaPanel.App.Services;
using InstaPanel.Cloud;
using InstaPanel.Core.Expert;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace InstaPanel.App.ViewModels;

/// <summary>
/// Tela Especialista: junta o perfil e os posts lidos da Meta (via Worker), a fila de agendados e a
/// biblioteca local, roda o <see cref="ProfileExpert"/> (regras das 9 skills de Instagram) e apresenta
/// o resultado em linguagem simples, com o Tico (mascote) explicando os próximos passos.
/// </summary>
public sealed partial class ExpertViewModel : ObservableObject
{
    private const int MediaToFetch = 100;
    private const int VisibleSteps = 3;

    /// <summary>Dados da última leitura (sobrevive à troca de tela; trocar o objetivo não busca de novo).</summary>
    private static Gathered? _cache;

    /// <summary>Última análise com IA desta sessão; some quando os dados são lidos de novo.</summary>
    private static AiResult? _aiCache;

    private static readonly ExpertHistoryStore History = new();

    private readonly List<string> _tips = [];
    private bool _loadingGoal;

    public ExpertViewModel()
    {
        _loadingGoal = true;
        SelectedGoalIndex = (int)AppServices.Settings.Load().ExpertGoal;
        _loadingGoal = false;
        Say(MascotMood.Happy, "Oi! Eu sou o Tico, o especialista do Cronos. Vou ler seu perfil e explicar tudo de um jeito simples.");
    }

    /// <summary>O Tico trouxe uma fala nova (a tela faz ele acenar).</summary>
    public event EventHandler? MascotSpoke;

    // ---------- Estado da tela ----------

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsLoading { get; set; }

    public bool IsIdle => !IsLoading;

    [ObservableProperty] public partial bool HasReport { get; set; }
    [ObservableProperty] public partial bool NeedsSetup { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    [ObservableProperty] public partial int SelectedGoalIndex { get; set; }

    // ---------- Resumo ----------

    [ObservableProperty] public partial string Username { get; set; } = "";
    [ObservableProperty] public partial ImageSource? ProfilePicture { get; set; }
    [ObservableProperty] public partial double Score { get; set; }
    [ObservableProperty] public partial string ScoreText { get; set; } = "";
    [ObservableProperty] public partial string GradeText { get; set; } = "";
    [ObservableProperty] public partial Brush? GradeBrush { get; set; }
    [ObservableProperty] public partial Brush? GradeBackground { get; set; }
    [ObservableProperty] public partial string Headline { get; set; } = "";
    [ObservableProperty] public partial string AnalyzedText { get; set; } = "";

    public ObservableCollection<ExpertStatViewModel> Stats { get; } = [];

    // ---------- Comece por aqui ----------

    public ObservableCollection<ExpertStepViewModel> Steps { get; } = [];
    public ObservableCollection<ExpertStepViewModel> ExtraSteps { get; } = [];

    [ObservableProperty] public partial bool HasNoSteps { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowMoreStepsVisible), nameof(MoreStepsText))]
    public partial bool ShowExtraSteps { get; set; }

    public bool ShowMoreStepsVisible => ExtraSteps.Count > 0;

    public string MoreStepsText => ShowExtraSteps
        ? "Mostrar menos"
        : ExtraSteps.Count == 1 ? "Ver mais 1 passo" : $"Ver mais {ExtraSteps.Count} passos";

    [RelayCommand]
    private void ToggleExtraSteps() => ShowExtraSteps = !ShowExtraSteps;

    // ---------- Raio-X por área ----------

    public ObservableCollection<ExpertAreaViewModel> Areas { get; } = [];

    [ObservableProperty] public partial ExpertAreaDetailViewModel? SelectedArea { get; set; }

    public void SelectArea(ExpertAreaViewModel area)
    {
        foreach (var a in Areas) a.IsSelected = ReferenceEquals(a, area);
        SelectedArea = new ExpertAreaDetailViewModel(area.Section);
    }

    // ---------- Semana e melhores posts ----------

    public ObservableCollection<ExpertPlanDayViewModel> WeekPlan { get; } = [];
    public ObservableCollection<ExpertTopPostViewModel> TopPosts { get; } = [];

    [ObservableProperty] public partial bool HasNoTopPosts { get; set; }

    // ---------- Tico ----------

    [ObservableProperty] public partial MascotMood MascotMood { get; set; } = MascotMood.Happy;
    [ObservableProperty] public partial string MascotMessage { get; set; } = "";
    [ObservableProperty] public partial string MascotCounter { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowTipBadge))]
    public partial bool IsBubbleOpen { get; set; } = true;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowTipBadge))]
    public partial bool HasMoreTips { get; set; }

    /// <summary>Balão fechado com dicas guardadas: um sinal no Tico convida a clicar.</summary>
    public bool ShowTipBadge => !IsBubbleOpen && MascotMessage.Length > 0;

    /// <summary>O usuário começou a ler a página: o Tico se recolhe para não cobrir o conteúdo.</summary>
    public void QuietMascot()
    {
        if (IsBubbleOpen && !IsLoading && !IsAiRunning) IsBubbleOpen = false;
    }

    private int _tipIndex;

    [RelayCommand]
    private void NextTip()
    {
        if (_tips.Count == 0) return;
        ShowTip((_tipIndex + 1) % _tips.Count);
    }

    [RelayCommand]
    private void CloseBubble() => IsBubbleOpen = false;

    /// <summary>Clique no Tico: abre o balão; se já está aberto, passa para a próxima dica.</summary>
    [RelayCommand]
    private void MascotClicked()
    {
        if (!IsBubbleOpen) IsBubbleOpen = true;
        else NextTip();
        MascotSpoke?.Invoke(this, EventArgs.Empty);
    }

    private void Say(MascotMood mood, string message)
    {
        _tips.Clear();
        MascotMood = mood;
        MascotMessage = message;
        MascotCounter = "";
        HasMoreTips = false;
        IsBubbleOpen = true;
        MascotSpoke?.Invoke(this, EventArgs.Empty);
    }

    private void ShowTip(int index)
    {
        _tipIndex = index;
        MascotMessage = _tips[index];
        MascotCounter = $"{index + 1} de {_tips.Count}";
        IsBubbleOpen = true;
        MascotSpoke?.Invoke(this, EventArgs.Empty);
    }

    private void SetTips(ExpertReport report)
    {
        _tips.Clear();
        var friendly = report.Score switch
        {
            >= 80 => "Seu perfil está ótimo! Separei só alguns ajustes finos.",
            >= 60 => "Você está no caminho certo. Separei os passos que mais fazem diferença.",
            >= 40 => "Tem coisas importantes para ajustar, mas nada difícil. Vamos por partes!",
            _ => "Vamos arrumar a casa juntos? Comece pelos passos que separei.",
        };
        _tips.Add($"Li {report.PostsAnalyzed} posts do @{report.Username}. Sua nota é {report.Score} de 100 ({ProfileExpert.Grade(report.Score).ToLowerInvariant()}). {friendly}");
        var n = 1;
        foreach (var p in report.Priorities.Take(VisibleSteps))
            _tips.Add($"Passo {n++}: {p.Finding.Title}. {p.Finding.Action}");
        _tips.Add("Toque numa área do Raio-X para ver, em detalhe, o que está bom e o que dá para melhorar.");
        _tips.Add("Quer uma opinião ainda mais detalhada? Clique em \"Analisar com IA\" e eu escrevo nome, bio e ganchos novos para você.");

        MascotMood = report.Score switch
        {
            >= 70 => MascotMood.Happy,
            >= 45 => MascotMood.Neutral,
            _ => MascotMood.Worried,
        };
        HasMoreTips = _tips.Count > 1;
        ShowTip(0);
    }

    // ---------- Carregar e analisar ----------

    public ExpertReport? Report { get; private set; }

    /// <summary>Mostra a última análise desta sessão ou, se não houver, analisa.</summary>
    public async Task ShowAsync()
    {
        if (_cache is not null) Show(await Task.Run(() => ProfileExpert.Analyze(BuildInput(_cache))));
        else await AnalyzeAsync();
        ShowAi(_aiCache);
    }

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        using var client = AppServices.TryCreateClient();
        NeedsSetup = client is null;
        ErrorMessage = null;
        if (client is null)
        {
            HasReport = false;
            Say(MascotMood.Neutral, "Para eu analisar sua conta, primeiro conecte o painel em Configurações.");
            return;
        }

        IsLoading = true;
        Say(MascotMood.Thinking, "Estou lendo seu perfil e seus últimos posts. Leva só alguns segundos…");
        try
        {
            var insightsTask = client.GetInsightsAsync(MediaToFetch);
            var plannedTask = client.ListPostsAsync(new PostQuery
            {
                From = DateTimeOffset.Now,
                To = DateTimeOffset.Now.AddDays(7),
                States = PostStates.Pending,
                Limit = 50,
            });
            var unusedTask = Task.Run(() =>
            {
                var published = AppServices.Library.GetPublishedMediaIds();
                return AppServices.Library.GetMediaItems().Count(m => !published.Contains(m.Id));
            });
            await Task.WhenAll(insightsTask, plannedTask, unusedTask);

            var previous = History.Load();
            _cache = new Gathered(insightsTask.Result, plannedTask.Result, unusedTask.Result, previous);
            var report = await Task.Run(() => ProfileExpert.Analyze(BuildInput(_cache)));
            // Guarda no máximo uma foto por dia, para a variação de seguidores comparar com um dia anterior.
            if (previous is null || report.GeneratedAt - previous.At >= TimeSpan.FromDays(1))
                History.Save(new ExpertSnapshot(report.GeneratedAt, _cache.Insights.Profile.FollowersCount, report.Score));
            Show(report);
            // Dados novos: a análise com IA anterior ficou velha (só roda de novo se o usuário pedir).
            _aiCache = null;
            ShowAi(null);
            AiError = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = AppServices.Describe(ex);
            Say(MascotMood.Worried, "Não consegui ler seus dados agora. O aviso no topo da tela diz o que aconteceu.");
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSelectedGoalIndexChanged(int value)
    {
        if (_loadingGoal || value < 0) return;
        var settings = AppServices.Settings.Load();
        AppServices.Settings.Save(settings with { ExpertGoal = (ProfileGoal)value });
        if (_cache is not null) Show(ProfileExpert.Analyze(BuildInput(_cache)));
    }

    private void Show(ExpertReport report)
    {
        Report = report;
        var profile = _cache?.Insights.Profile;
        Username = "@" + report.Username;
        ProfilePicture = Uri.TryCreate(profile?.ProfilePictureUrl, UriKind.Absolute, out var pic) ? new BitmapImage(pic) : null;
        Score = report.Score;
        ScoreText = report.Score.ToString(Formatting.PtBr);
        GradeText = ProfileExpert.Grade(report.Score);
        GradeBrush = ExpertLook.ToneBrush(ExpertLook.ScoreTone(report.Score));
        GradeBackground = ExpertLook.ToneBackground(ExpertLook.ScoreTone(report.Score));
        Headline = report.Headline;
        AnalyzedText = $"{report.PostsAnalyzed} posts analisados às {report.GeneratedAt.ToLocalTime():HH:mm} · objetivo: {ProfileExpert.GoalName(report.Goal)}";

        Replace(Stats, report.Highlights.Select(h => new ExpertStatViewModel(h)));

        var steps = report.Priorities.Select((p, i) => new ExpertStepViewModel(i + 1, p)).ToList();
        Replace(Steps, steps.Take(VisibleSteps));
        Replace(ExtraSteps, steps.Skip(VisibleSteps));
        HasNoSteps = steps.Count == 0;
        ShowExtraSteps = false;
        OnPropertyChanged(nameof(ShowMoreStepsVisible));
        OnPropertyChanged(nameof(MoreStepsText));

        // Mantém a área escolhida ao trocar o objetivo; na primeira vez, abre a de nota mais baixa.
        var previousKey = Areas.FirstOrDefault(a => a.IsSelected)?.Section.Key;
        Replace(Areas, report.Sections.Select(s => new ExpertAreaViewModel(s)));
        var selected = Areas.FirstOrDefault(a => a.Section.Key == previousKey)
                       ?? Areas.Where(a => a.Section.Score is not null).OrderBy(a => a.Section.Score).FirstOrDefault()
                       ?? Areas.FirstOrDefault();
        if (selected is not null) SelectArea(selected);

        Replace(WeekPlan, report.WeekPlan.Select(p => new ExpertPlanDayViewModel(p)));
        Replace(TopPosts, report.TopPosts.Select(p => new ExpertTopPostViewModel(p)));
        HasNoTopPosts = TopPosts.Count == 0;
        HasReport = true;
        SetTips(report);
    }

    // ---------- Análise com IA (só quando o usuário pede) ----------

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanRunAi))]
    public partial bool IsAiRunning { get; set; }

    public bool CanRunAi => !IsAiRunning;

    [ObservableProperty] public partial string AiText { get; set; } = "";
    [ObservableProperty] public partial bool HasAi { get; set; }
    [ObservableProperty] public partial string AiInfo { get; set; } = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasAiError))]
    public partial string? AiError { get; set; }

    public bool HasAiError => !string.IsNullOrEmpty(AiError);

    [RelayCommand]
    private async Task AnalyzeWithAiAsync()
    {
        if (_cache is null || Report is null) return;
        using var client = AppServices.TryCreateClient();
        if (client is null)
        {
            NeedsSetup = true;
            return;
        }

        IsAiRunning = true;
        AiError = null;
        Say(MascotMood.Thinking, "Estou escrevendo a análise detalhada com a IA. Leva de 1 a 3 minutos, pode continuar olhando a tela.");
        try
        {
            var data = _cache;
            var report = Report;
            var request = new AiAnalysisRequest(
                ProfileExpert.GoalName(report.Goal),
                ExpertMarkdown.From(report),
                data.Insights.Media.Where(m => FormatOf(m) != MediaFormat.Story).Take(ProfileExpert.RecentWindow)
                    .Select(m => new AiPostInput(ProfileExpert.FormatName(FormatOf(m)), LocalStamp(m.Timestamp), m.LikeCount, m.CommentsCount, m.Caption))
                    .ToList(),
                data.Planned.Select(p => new AiPlannedInput(ProfileExpert.FormatName(FormatOf(p.Type)), LocalStamp(p.ScheduledAt), p.Caption)).ToList());
            var ai = await client.RequestAiAnalysisAsync(request);
            _aiCache = new AiResult(ai, report.Goal, DateTimeOffset.Now);
            ShowAi(_aiCache);
            Say(MascotMood.Happy, "Pronto! A análise detalhada está no cartão da IA. Dá para copiar tudo em \"Copiar relatório\".");
        }
        catch (Exception ex)
        {
            AiError = AppServices.Describe(ex);
            Say(MascotMood.Worried, "A análise com IA não deu certo. O motivo está no cartão da IA.");
        }
        finally
        {
            IsAiRunning = false;
        }
    }

    private void ShowAi(AiResult? result)
    {
        HasAi = result is not null;
        AiText = result?.Analysis.Analysis ?? "";
        if (result is null)
        {
            AiInfo = "";
            return;
        }
        var u = result.Analysis.Usage;
        AiInfo = $"{result.Analysis.Model} · {result.At:HH:mm} · objetivo: {ProfileExpert.GoalName(result.Goal)} · " +
                 $"{u.InputTokens.ToString("N0", Formatting.PtBr)} tokens de entrada, {u.OutputTokens.ToString("N0", Formatting.PtBr)} de saída" +
                 (result.Analysis.Truncated ? " · resposta cortada no limite de tamanho" : "");
    }

    public string? BuildMarkdown()
    {
        if (Report is null) return null;
        var markdown = ExpertMarkdown.From(Report);
        if (_aiCache is { } ai) markdown += $"\n# Análise com IA\n\n_{AiInfo}_\n\n{ai.Analysis.Analysis}\n";
        return markdown;
    }

    // ---------- Dados ----------

    private static string LocalStamp(DateTimeOffset when) => when.ToLocalTime().ToString("yyyy-MM-dd HH:mm", Formatting.PtBr);

    private ExpertInput BuildInput(Gathered data) => new()
    {
        Profile = new ProfileFacts(
            data.Insights.Profile.Username,
            data.Insights.Profile.Name,
            data.Insights.Profile.Biography,
            data.Insights.Profile.Website,
            data.Insights.Profile.AccountType,
            !string.IsNullOrEmpty(data.Insights.Profile.ProfilePictureUrl),
            data.Insights.Profile.FollowersCount,
            data.Insights.Profile.FollowsCount,
            data.Insights.Profile.MediaCount),
        Posts = data.Insights.Media.Select(m => new PublishedPost(
            m.Id, FormatOf(m), m.Caption, m.Timestamp, m.LikeCount, m.CommentsCount, m.Permalink, m.ThumbnailUrl)).ToList(),
        Planned = data.Planned.Select(p => new PlannedPost(FormatOf(p.Type), p.Caption, p.ScheduledAt)).ToList(),
        Goal = (ProfileGoal)Math.Max(0, SelectedGoalIndex),
        Now = DateTimeOffset.Now,
        UnusedLibraryItems = data.UnusedLibraryItems,
        Previous = data.Previous,
    };

    private static MediaFormat FormatOf(IgMedia m) => m switch
    {
        { MediaProductType: "STORY" } => MediaFormat.Story,
        { MediaProductType: "REELS" } or { MediaType: "VIDEO" } => MediaFormat.Reel,
        { MediaType: "CAROUSEL_ALBUM" } => MediaFormat.Carousel,
        _ => MediaFormat.Image,
    };

    private static MediaFormat FormatOf(string postType) => postType switch
    {
        "REELS" => MediaFormat.Reel,
        "CAROUSEL" => MediaFormat.Carousel,
        "STORIES" => MediaFormat.Story,
        _ => MediaFormat.Image,
    };

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    private sealed record Gathered(ProfileInsights Insights, IReadOnlyList<PostInfo> Planned, int UnusedLibraryItems, ExpertSnapshot? Previous);

    private sealed record AiResult(AiAnalysis Analysis, ProfileGoal Goal, DateTimeOffset At);
}
