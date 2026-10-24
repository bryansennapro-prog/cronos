using InstaPanel.App.Services;
using InstaPanel.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace InstaPanel.App.Pages;

public sealed partial class ExpertPage : Page
{
    public ExpertPage()
    {
        InitializeComponent();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ExpertViewModel.AiText)) MarkdownView.Render(AiBlock, ViewModel.AiText);
        };
    }

    public ExpertViewModel ViewModel { get; } = new();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        AppServices.ConnectionChanged += OnConnectionChanged;
        await ViewModel.ShowAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        AppServices.ConnectionChanged -= OnConnectionChanged;
        base.OnNavigatedFrom(e);
    }

    private void OnConnectionChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(() => ViewModel.AnalyzeCommand.Execute(null));

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.BuildMarkdown() is not { } markdown) return;
        var package = new DataPackage();
        package.SetText(markdown);
        Clipboard.SetContent(package);
        CopiedBar.IsOpen = true;
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => App.MainWindow?.NavigateToSettings();

    private void OpenCompose_Click(object sender, RoutedEventArgs e) => App.MainWindow?.NavigateTo("compose");
}
