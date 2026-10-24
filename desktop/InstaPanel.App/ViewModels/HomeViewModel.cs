using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstaPanel.App.Services;
using InstaPanel.Cloud;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace InstaPanel.App.ViewModels;

public sealed partial class HomeViewModel : ObservableObject
{
    private const int TokenLifetimeDays = 60;
    private const int TokenWarningDays = 10;

    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool HasData { get; set; }
    [ObservableProperty] public partial bool NeedsSetup { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    [ObservableProperty] public partial string Username { get; set; } = "";
    [ObservableProperty] public partial string AccountTypeText { get; set; } = "";
    [ObservableProperty] public partial ImageSource? ProfilePicture { get; set; }

    [ObservableProperty] public partial string TokenDaysText { get; set; } = "—";
    [ObservableProperty] public partial string TokenExpiresText { get; set; } = "";
    [ObservableProperty] public partial string TokenNote { get; set; } = "";
    [ObservableProperty] public partial double TokenProgress { get; set; }
    [ObservableProperty] public partial bool TokenWarning { get; set; }

    [ObservableProperty] public partial string QuotaText { get; set; } = "—";
    [ObservableProperty] public partial string QuotaNote { get; set; } = "";
    [ObservableProperty] public partial double QuotaProgress { get; set; }
    [ObservableProperty] public partial double QuotaMax { get; set; } = 100;

    [ObservableProperty] public partial string WorkerText { get; set; } = "—";
    [ObservableProperty] public partial string WorkerNote { get; set; } = "";
    [ObservableProperty] public partial string LastUpdatedText { get; set; } = "";

    public ObservableCollection<PostItemViewModel> RecentPosts { get; } = [];

    [ObservableProperty] public partial bool HasNoRecentPosts { get; set; } = true;

    public ObservableCollection<PostItemViewModel> UpcomingPosts { get; } = [];

    [ObservableProperty] public partial bool HasNoUpcoming { get; set; } = true;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        using var client = AppServices.TryCreateClient();
        NeedsSetup = client is null;
        ErrorMessage = null;
        if (client is null)
        {
            HasData = false;
            return;
        }

        IsLoading = true;
        try
        {
            var health = await client.GetHealthAsync();
            WorkerText = "Online";
            WorkerNote = $"Worker v{health.Version} · Cloudflare";

            var status = await client.GetAccountAsync();
            Apply(status);
            HasData = true;

            var thumbs = AppServices.Library.GetPostThumbnails();
            var upcoming = await client.ListPostsAsync(new PostQuery
            {
                From = DateTimeOffset.Now.AddHours(-1),
                To = DateTimeOffset.Now.AddDays(76),
                States = PostStates.Pending,
                Limit = 5,
            });
            UpcomingPosts.Clear();
            foreach (var p in upcoming) UpcomingPosts.Add(new PostItemViewModel(p, thumbs.GetValueOrDefault(p.Id)));
            HasNoUpcoming = UpcomingPosts.Count == 0;

            var recent = await client.ListPostsAsync(new PostQuery
            {
                States = [PostStates.Published, PostStates.Failed],
                Limit = 5,
            });
            RecentPosts.Clear();
            foreach (var p in recent) RecentPosts.Add(new PostItemViewModel(p, thumbs.GetValueOrDefault(p.Id)));
            HasNoRecentPosts = RecentPosts.Count == 0;
            LastUpdatedText = $"Atualizado às {DateTime.Now:HH:mm}";
        }
        catch (Exception ex)
        {
            if (ex is WorkerApiException { StatusCode: 0 })
            {
                WorkerText = "Offline";
                WorkerNote = "Sem resposta do Worker";
            }
            ErrorMessage = AppServices.Describe(ex);
            HasData = false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Apply(AccountStatus status)
    {
        var account = status.Account;
        Username = "@" + account.Username;
        AccountTypeText = account.AccountType switch
        {
            "MEDIA_CREATOR" => "Conta de criador",
            "BUSINESS" => "Conta comercial",
            _ => "Conta profissional",
        };
        ProfilePicture = Uri.TryCreate(account.ProfilePictureUrl, UriKind.Absolute, out var pic)
            ? new BitmapImage(pic)
            : null;

        var token = account.Token;
        TokenDaysText = token.DaysLeft == 1 ? "1 dia" : $"{token.DaysLeft} dias";
        TokenExpiresText = $"Vence em {token.ExpiresAt.ToLocalTime():dd/MM/yyyy}";
        TokenProgress = Math.Clamp(token.DaysLeft, 0, TokenLifetimeDays);
        TokenWarning = token.DaysLeft < TokenWarningDays || token.LastError is not null;
        TokenNote = token.LastError
            ?? (token.ExpiryKnown
                ? "Renovação automática ativa"
                : "Data estimada · confirmada na 1ª renovação automática");

        var limit = status.PublishingLimit;
        if (limit.Error is { } err)
        {
            QuotaText = "—";
            QuotaProgress = 0;
            QuotaNote = $"A Meta não informou o limite: {err.Message}";
        }
        else
        {
            var total = limit.QuotaTotal ?? 100;
            QuotaMax = total;
            QuotaProgress = Math.Min(limit.QuotaUsage, total);
            QuotaText = $"{limit.QuotaUsage} de {total}";
            var hours = (limit.QuotaDuration ?? 86400) / 3600;
            QuotaNote = $"posts publicados pela API nas últimas {hours}h";
        }
    }
}
