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
        ViewModel.MascotSpoke += (_, _) => Mascot.Wave();
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

    private double _scrollWhenBubbleOpened = -1;

    // Quando a pessoa rola para ler, o Tico recolhe o balão (clicar nele traz de volta).
    private void PageScroll_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!ViewModel.IsBubbleOpen)
        {
            _scrollWhenBubbleOpened = -1;
            return;
        }
        if (_scrollWhenBubbleOpened < 0) _scrollWhenBubbleOpened = PageScroll.VerticalOffset;
        else if (Math.Abs(PageScroll.VerticalOffset - _scrollWhenBubbleOpened) > 240) ViewModel.QuietMascot();
    }

    private void CopiedBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) => sender.Visibility = Visibility.Collapsed;

    private void AreaTile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ExpertAreaViewModel area) ViewModel.SelectArea(area);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.BuildMarkdown() is not { } markdown) return;
        var package = new DataPackage();
        package.SetText(markdown);
        Clipboard.SetContent(package);
        CopiedBar.Visibility = Visibility.Visible;
        CopiedBar.IsOpen = true;
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => App.MainWindow?.NavigateToSettings();

    private void OpenCompose_Click(object sender, RoutedEventArgs e) => App.MainWindow?.NavigateTo("compose");
}
