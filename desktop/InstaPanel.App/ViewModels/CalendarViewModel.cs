using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstaPanel.App.Services;
using InstaPanel.Cloud;
using InstaPanel.Core;

namespace InstaPanel.App.ViewModels;

public enum CalendarMode
{
    Month,
    Week,
}

public sealed class CalendarDayViewModel
{
    public CalendarDayViewModel(DateTime date, bool isOtherMonth, IReadOnlyList<PostItemViewModel> posts, bool weekMode)
    {
        Date = date;
        IsOtherMonth = isOtherMonth;
        AllPosts = posts;
        ShowWeekHeader = weekMode;
        VisiblePosts = posts.Take(weekMode ? 50 : 3).ToList();
    }

    public bool ShowWeekHeader { get; }

    public DateTime Date { get; }

    public bool IsOtherMonth { get; }

    public IReadOnlyList<PostItemViewModel> AllPosts { get; }

    public IReadOnlyList<PostItemViewModel> VisiblePosts { get; }

    public bool IsToday => Date == DateTime.Today;

    public bool IsNotToday => !IsToday;

    public bool IsPast => Date < DateTime.Today;

    public string DayNumber => Date.Day.ToString(Formatting.PtBr);

    public string WeekHeader => Date.ToString("ddd, dd/MM", Formatting.PtBr);

    public double DayOpacity => IsOtherMonth ? 0.45 : 1;

    public int HiddenCount => AllPosts.Count - VisiblePosts.Count;

    public bool HasMore => HiddenCount > 0;

    public string MoreText => $"+{HiddenCount} mais";

    public bool IsEmpty => AllPosts.Count == 0;
}

public sealed partial class CalendarViewModel : ObservableObject
{
    private DateTime _anchor = DateTime.Today;
    private List<PostInfo> _posts = [];

    public ObservableCollection<CalendarDayViewModel> Days { get; } = [];

    public IReadOnlyList<string> WeekdayNames { get; } = ["dom", "seg", "ter", "qua", "qui", "sex", "sáb"];

    [ObservableProperty] public partial CalendarMode Mode { get; set; } = CalendarMode.Month;
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial bool IsLoading { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>A grade foi refeita (a tela usa para rolar até hoje).</summary>
    public event EventHandler? DaysBuilt;

    partial void OnModeChanged(CalendarMode value) => _ = LoadAsync();

    [RelayCommand]
    private Task PreviousAsync()
    {
        _anchor = Mode == CalendarMode.Month ? _anchor.AddMonths(-1) : _anchor.AddDays(-7);
        return LoadAsync();
    }

    [RelayCommand]
    private Task NextAsync()
    {
        _anchor = Mode == CalendarMode.Month ? _anchor.AddMonths(1) : _anchor.AddDays(7);
        return LoadAsync();
    }

    [RelayCommand]
    private Task TodayAsync()
    {
        _anchor = DateTime.Today;
        return LoadAsync();
    }

    /// <summary>Abre a semana de um dia (usado pelo "+N mais" do mês).</summary>
    public void ShowWeekOf(DateTime day)
    {
        _anchor = day;
        if (Mode == CalendarMode.Week) _ = LoadAsync();
        else Mode = CalendarMode.Week;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        var (start, end) = Mode == CalendarMode.Week ? CalendarMath.WeekOf(_anchor) : CalendarMath.MonthGridOf(_anchor);
        var monthTitle = _anchor.ToString("MMMM 'de' yyyy", Formatting.PtBr);
        Title = Mode == CalendarMode.Month
            ? char.ToUpper(monthTitle[0], Formatting.PtBr) + monthTitle[1..]
            : $"{start:dd/MM} – {end.AddDays(-1):dd/MM/yyyy}";

        using var client = AppServices.TryCreateClient();
        if (client is null)
        {
            ErrorMessage = "Configure o Worker em Configurações para ver o calendário.";
            Build(start, end);
            return;
        }

        IsLoading = true;
        try
        {
            _posts = (await client.ListPostsAsync(new PostQuery
            {
                From = new DateTimeOffset(start),
                To = new DateTimeOffset(end),
                Limit = 200,
            })).Where(p => p.State != PostStates.Canceled).ToList();
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = AppServices.Describe(ex);
        }
        finally
        {
            IsLoading = false;
        }
        Build(start, end);
    }

    private void Build(DateTime start, DateTime end)
    {
        var thumbs = AppServices.Library.GetPostThumbnails();
        var detailed = Mode == CalendarMode.Week;
        var byDay = _posts
            .Select(p => new PostItemViewModel(p, thumbs.GetValueOrDefault(p.Id), detailed))
            .GroupBy(p => p.LocalScheduled.Date)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.LocalScheduled).ToList());

        Days.Clear();
        for (var d = start; d < end; d = d.AddDays(1))
        {
            var posts = byDay.GetValueOrDefault(d) ?? [];
            Days.Add(new CalendarDayViewModel(d, Mode == CalendarMode.Month && d.Month != _anchor.Month, posts, detailed));
        }

        DaysBuilt?.Invoke(this, EventArgs.Empty);

        var pending = _posts.Count(p => PostStates.Pending.Contains(p.State) || p.State == PostStates.Failed);
        var published = _posts.Count(p => p.State == PostStates.Published);
        Summary = $"{pending} agendado(s) · {published} publicado(s) neste período";
    }

    /// <summary>Move um post para outro dia mantendo o horário. Devolve uma mensagem de erro, ou null.</summary>
    public async Task<string?> MoveToDayAsync(string postId, DateTime day)
    {
        var post = _posts.FirstOrDefault(p => p.Id == postId);
        if (post is null) return "Post não encontrado neste período.";
        if (!PostStates.IsEditable(post.State)) return "Este post não pode mais ser remarcado.";

        var when = CalendarMath.MoveToDay(post.ScheduledAt, day, TimeZoneInfo.Local);
        if (when.Date == post.ScheduledAt.ToLocalTime().Date) return null;
        if (when < DateTimeOffset.Now.AddMinutes(2)) return $"{Formatting.DateTime(when)} já passou. Abra o post para escolher outro horário.";

        using var client = AppServices.TryCreateClient();
        if (client is null) return "Configure o Worker em Configurações.";
        try
        {
            await client.UpdatePostAsync(postId, new UpdatePostRequest(ScheduledAt: when));
            AppServices.NotifyPostsChanged();
            await LoadAsync();
            return null;
        }
        catch (Exception ex)
        {
            return AppServices.Describe(ex);
        }
    }

    public PostInfo? FindPost(string id) => _posts.FirstOrDefault(p => p.Id == id);
}
