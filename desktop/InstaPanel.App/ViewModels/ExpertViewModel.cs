using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstaPanel.App.Services;
using InstaPanel.Cloud;
using InstaPanel.Core.Expert;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace InstaPanel.App.ViewModels;

/// <summary>
/// Tela Especialista: junta o perfil e os posts lidos da Meta (via Worker), a fila de agendados e a
/// biblioteca local, e roda o <see cref="ProfileExpert"/> com as regras das 9 skills de Instagram.
/// </summary>
public sealed partial class ExpertViewModel : ObservableObject
{
    private const int MediaToFetch = 100;

    /// <summary>Dados da última leitura (sobrevive à troca de tela; trocar o objetivo não busca de novo).</summary>
    private static Gathered? _cache;

    private static readonly ExpertHistoryStore History = new();

    private bool _loadingGoal;

    public ExpertViewModel()
    {
        _loadingGoal = true;
        SelectedGoalIndex = (int)AppServices.Settings.Load().ExpertGoal;
        _loadingGoal = false;
    }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsLoading { get; set; }

    public bool IsIdle => !IsLoading;

    [ObservableProperty] public partial bool HasReport { get; set; }
    [ObservableProperty] public partial bool NeedsSetup { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    [ObservableProperty] public partial int SelectedGoalIndex { get; set; }

    [ObservableProperty] public partial string Username { get; set; } = "";
    [ObservableProperty] public partial ImageSource? ProfilePicture { get; set; }
    [ObservableProperty] public partial double Score { get; set; }
    [ObservableProperty] public partial string ScoreText { get; set; } = "";
    [ObservableProperty] public partial string Headline { get; set; } = "";
    [ObservableProperty] public partial string AnalyzedText { get; set; } = "";
    [ObservableProperty] public partial bool HasNoPriorities { get; set; }
    [ObservableProperty] public partial bool HasNoTopPosts { get; set; }

    // ---------- Análise com IA (só quando o usuário pede) ----------

    /// <summary>Última análise com IA desta sessão; some quando os dados são lidos de novo.</summary>
    private static AiResult? _aiCache;

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
        }
        catch (Exception ex)
        {
            AiError = AppServices.Describe(ex);
        }
        finally
        {
            IsAiRunning = false;
        }
    }

    private static string LocalStamp(DateTimeOffset when) => when.ToLocalTime().ToString("yyyy-MM-dd HH:mm", Formatting.PtBr);

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

    public ObservableCollection<ExpertMetricViewModel> Highlights { get; } = [];
    public ObservableCollection<ExpertFindingViewModel> Priorities { get; } = [];
    public ObservableCollection<ExpertSectionViewModel> Sections { get; } = [];
    public ObservableCollection<ExpertTopPostViewModel> TopPosts { get; } = [];
    public ObservableCollection<ExpertPlanSlotViewModel> WeekPlan { get; } = [];

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
            return;
        }

        IsLoading = true;
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

    public string? BuildMarkdown()
    {
        if (Report is null) return null;
        var markdown = ExpertMarkdown.From(Report);
        if (_aiCache is { } ai) markdown += $"\n# Análise com IA\n\n_{AiInfo}_\n\n{ai.Analysis.Analysis}\n";
        return markdown;
    }

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

    private void Show(ExpertReport report)
    {
        Report = report;
        var profile = _cache?.Insights.Profile;
        Username = "@" + report.Username;
        ProfilePicture = Uri.TryCreate(profile?.ProfilePictureUrl, UriKind.Absolute, out var pic) ? new BitmapImage(pic) : null;
        Score = report.Score;
        ScoreText = report.Score.ToString(Formatting.PtBr);
        Headline = report.Headline;
        AnalyzedText = $"Analisado às {report.GeneratedAt.ToLocalTime():HH:mm} · {report.PostsAnalyzed} posts · objetivo: {ProfileExpert.GoalName(report.Goal)}";

        Replace(Highlights, report.Highlights.Select(m => new ExpertMetricViewModel(m)));
        Replace(Priorities, report.Priorities.Select(f => new ExpertFindingViewModel(f)));
        Replace(Sections, report.Sections.Select(s => new ExpertSectionViewModel(s)));
        Replace(TopPosts, report.TopPosts.Select(p => new ExpertTopPostViewModel(p)));
        Replace(WeekPlan, report.WeekPlan.Select(p => new ExpertPlanSlotViewModel(p)));
        HasNoPriorities = Priorities.Count == 0;
        HasNoTopPosts = TopPosts.Count == 0;
        HasReport = true;
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    private sealed record Gathered(ProfileInsights Insights, IReadOnlyList<PostInfo> Planned, int UnusedLibraryItems, ExpertSnapshot? Previous);

    private sealed record AiResult(AiAnalysis Analysis, ProfileGoal Goal, DateTimeOffset At);
}
