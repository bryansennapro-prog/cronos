using InstaPanel.App.Dialogs;
using InstaPanel.App.Services;
using InstaPanel.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace InstaPanel.App.Pages;

public sealed partial class HomePage : Page
{
    public HomePage()
    {
        InitializeComponent();
    }

    public HomeViewModel ViewModel { get; } = new();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        AppServices.ConnectionChanged += OnConnectionChanged;
        AppServices.PostsChanged += OnConnectionChanged;
        AppUpdates.StateChanged += OnUpdateStateChanged;
        ShowUpdate();
        ViewModel.RefreshCommand.Execute(null);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        AppServices.ConnectionChanged -= OnConnectionChanged;
        AppServices.PostsChanged -= OnConnectionChanged;
        AppUpdates.StateChanged -= OnUpdateStateChanged;
        base.OnNavigatedFrom(e);
    }

    private void OnConnectionChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(() => ViewModel.RefreshCommand.Execute(null));

    private void OnUpdateStateChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(ShowUpdate);

    private void ShowUpdate()
    {
        UpdateBar.IsOpen = AppUpdates.UpdateAvailable;
        UpdateBar.Message = $"A versão {AppUpdates.AvailableVersion} do Cronos está pronta para instalar (você está na {AppUpdates.CurrentVersion}).";
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => App.MainWindow?.NavigateToSettings();

    private void OpenCalendar_Click(object sender, RoutedEventArgs e) => App.MainWindow?.NavigateTo("calendar");

    private void OpenHistory_Click(object sender, RoutedEventArgs e) => App.MainWindow?.NavigateTo("history");

    private async void Post_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PostItemViewModel item) await PostDetailsDialog.OpenAsync(XamlRoot, item.Post);
    }
}
