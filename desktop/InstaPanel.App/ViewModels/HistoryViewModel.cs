using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstaPanel.App.Services;
using InstaPanel.Cloud;

namespace InstaPanel.App.ViewModels;

public sealed partial class HistoryViewModel : ObservableObject
{
    private const int PageSize = 30;

    private static readonly Dictionary<string, IReadOnlyList<string>?> Filters = new()
    {
        ["all"] = null,
        ["pending"] = PostStates.Pending,
        ["published"] = [PostStates.Published],
        ["failed"] = [PostStates.Failed],
        ["canceled"] = [PostStates.Canceled],
    };

    public ObservableCollection<PostItemViewModel> Items { get; } = [];

    /// <summary>"all", "pending", "published", "failed" ou "canceled".</summary>
    [ObservableProperty] public partial string Filter { get; set; } = "all";

    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool HasMore { get; set; }
    [ObservableProperty] public partial bool IsEmpty { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    partial void OnFilterChanged(string value) => _ = LoadAsync();

    [RelayCommand]
    public Task LoadAsync() => FetchAsync(reset: true);

    [RelayCommand]
    private Task LoadMoreAsync() => FetchAsync(reset: false);

    private async Task FetchAsync(bool reset)
    {
        if (IsLoading) return;
        using var client = AppServices.TryCreateClient();
        if (client is null)
        {
            ErrorMessage = "Configure o Worker em Configurações para ver o histórico.";
            return;
        }

        IsLoading = true;
        try
        {
            var before = !reset && Items.Count > 0 ? Items[^1].Post.CreatedAt : (DateTimeOffset?)null;
            var page = await client.ListPostsAsync(new PostQuery { States = Filters.GetValueOrDefault(Filter), Before = before, Limit = PageSize });
            var thumbs = AppServices.Library.GetPostThumbnails();
            if (reset) Items.Clear();
            foreach (var p in page) Items.Add(new PostItemViewModel(p, thumbs.GetValueOrDefault(p.Id)));
            HasMore = page.Count == PageSize;
            IsEmpty = Items.Count == 0;
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
    }
}
