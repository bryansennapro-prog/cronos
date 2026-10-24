using InstaPanel.App.Pages;
using InstaPanel.App.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Graphics;

namespace InstaPanel.App;

public sealed partial class MainWindow : Window
{
    private const string SettingsTag = "settings";

    private static readonly Dictionary<string, Type> Pages = new()
    {
        ["home"] = typeof(HomePage),
        ["compose"] = typeof(ComposePage),
        ["library"] = typeof(LibraryPage),
        ["calendar"] = typeof(CalendarPage),
        ["history"] = typeof(HistoryPage),
        ["expert"] = typeof(ExpertPage),
        [SettingsTag] = typeof(SettingsPage),
    };

    private string? _currentTag;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new SizeInt32(1280, 840));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 900;
            presenter.PreferredMinimumHeight = 600;
        }

        NavView.SelectedItem = NavView.MenuItems[0];
    }

    /// <summary>Usado pelas telas para pular para Configurações (ex.: "configure a chave").</summary>
    public void NavigateToSettings() => NavView.SelectedItem = NavView.SettingsItem;

    public void NavigateTo(string tag)
    {
        if (tag == SettingsTag)
        {
            NavigateToSettings();
            return;
        }
        var item = NavView.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => Equals(i.Tag, tag));
        if (item is null) return;
        if (ReferenceEquals(NavView.SelectedItem, item) && Pages.TryGetValue(tag, out var page)) NavFrame.Navigate(page, tag);
        else NavView.SelectedItem = item;
    }

    /// <summary>Abre "Novo post" com uma mídia da biblioteca já escolhida.</summary>
    public void NavigateToCompose(string mediaId)
    {
        ComposeViewModel.PendingMediaId = mediaId;
        NavigateTo("compose");
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args) =>
        NavView.IsPaneOpen = !NavView.IsPaneOpen;

    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        if (NavFrame.CanGoBack) NavFrame.GoBack();
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = args.IsSettingsSelected ? SettingsTag : (args.SelectedItem as NavigationViewItem)?.Tag as string;
        if (tag is null || tag == _currentTag || !Pages.TryGetValue(tag, out var page)) return;
        NavFrame.Navigate(page, tag);
    }

    // O parâmetro de navegação é sempre a tag do item; mantém o menu em sincronia ao voltar.
    private void NavFrame_Navigated(object sender, NavigationEventArgs e)
    {
        _currentTag = e.Parameter as string;
        NavView.SelectedItem = _currentTag == SettingsTag
            ? NavView.SettingsItem
            : NavView.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => Equals(i.Tag, _currentTag));
    }
}
