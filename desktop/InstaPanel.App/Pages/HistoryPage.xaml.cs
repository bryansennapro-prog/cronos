using InstaPanel.App.Dialogs;
using InstaPanel.App.Services;
using InstaPanel.App.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace InstaPanel.App.Pages;

public sealed partial class HistoryPage : Page
{
    public HistoryPage()
    {
        InitializeComponent();
    }

    public HistoryViewModel ViewModel { get; } = new();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        AppServices.PostsChanged += OnPostsChanged;
        await ViewModel.LoadAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        AppServices.PostsChanged -= OnPostsChanged;
        base.OnNavigatedFrom(e);
    }

    private void OnPostsChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(() => _ = ViewModel.LoadAsync());

    private void FilterBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) =>
        ViewModel.Filter = sender.SelectedItem?.Tag as string ?? "all";

    private async void List_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PostItemViewModel item) await PostDetailsDialog.OpenAsync(XamlRoot, item.Post);
    }
}
