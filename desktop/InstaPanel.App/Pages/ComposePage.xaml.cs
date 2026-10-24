using InstaPanel.App.Controls;
using InstaPanel.App.ViewModels;
using InstaPanel.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace InstaPanel.App.Pages;

public sealed partial class ComposePage : Page
{
    private bool _syncingKind;

    public ComposePage()
    {
        InitializeComponent();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ComposeViewModel.Kind)) SyncKindBar();

            // Rola até o progresso (fica abaixo da legenda) quando a publicação começa, termina ou falha.
            if (e.PropertyName != nameof(ComposeViewModel.Stage) || !ViewModel.ShowProgressPanel) return;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                ProgressCard.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true }));
        };
    }

    public ComposeViewModel ViewModel { get; } = new();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync();
    }

    // ---------- Formato ----------

    private void KindBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_syncingKind || sender.SelectedItem?.Tag is not string tag) return;
        ViewModel.Kind = Enum.Parse<PostKind>(tag);
    }

    /// <summary>O formato também muda pelo código (ex.: "Criar post" na Biblioteca com um vídeo → Reel).</summary>
    private void SyncKindBar()
    {
        var item = KindBar.Items.FirstOrDefault(i => i.Tag as string == ViewModel.Kind.ToString());
        if (item is null || ReferenceEquals(KindBar.SelectedItem, item)) return;
        _syncingKind = true;
        KindBar.SelectedItem = item;
        _syncingKind = false;
    }

    // ---------- Mídias ----------

    private async void ChooseMedia_Click(object sender, RoutedEventArgs e)
    {
        var items = ViewModel.GetLibraryItems();
        if (items.Count == 0)
        {
            var go = await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Nada na biblioteca para este formato",
                Content = ViewModel.IsReel
                    ? "Reels precisam de um vídeo. Adicione vídeos na Biblioteca."
                    : "Adicione vídeos e imagens na Biblioteca primeiro.",
                PrimaryButtonText = "Ir para a Biblioteca",
                CloseButtonText = "Fechar",
                DefaultButton = ContentDialogButton.Primary,
            }.ShowAsync();
            if (go == ContentDialogResult.Primary) App.MainWindow?.NavigateTo("library");
            return;
        }

        var multiple = ViewModel.IsCarousel;
        var grid = new GridView
        {
            ItemsSource = items,
            SelectionMode = multiple ? ListViewSelectionMode.Multiple : ListViewSelectionMode.Single,
            ItemTemplate = (DataTemplate)Resources["PickerItemTemplate"],
            MaxHeight = 480,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(grid, "Mídias da biblioteca");
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = multiple ? "Escolher itens do carrossel" : "Escolher mídia",
            Content = grid,
            PrimaryButtonText = multiple ? "Adicionar" : "Usar esta",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        var doubleTapped = false;
        grid.SelectionChanged += (_, _) =>
        {
            dialog.IsPrimaryButtonEnabled = grid.SelectedItems.Count > 0;
            if (multiple) dialog.PrimaryButtonText = grid.SelectedItems.Count > 0 ? $"Adicionar {grid.SelectedItems.Count}" : "Adicionar";
        };
        grid.DoubleTapped += (_, _) =>
        {
            if (multiple || grid.SelectedItem is not MediaItemViewModel) return;
            doubleTapped = true;
            dialog.Hide();
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary && !doubleTapped) return;
        // Mantém a ordem em que aparecem na biblioteca (a ordem do carrossel se ajusta depois com as setas).
        var chosen = items.Where(grid.SelectedItems.Contains).Select(m => m.Id).ToList();
        ViewModel.AddMedia(chosen);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ComposeMediaViewModel item) ViewModel.Move(item, -1);
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ComposeMediaViewModel item) ViewModel.Move(item, +1);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ComposeMediaViewModel item) ViewModel.Remove(item);
    }

    // ---------- Publicar / agendar ----------

    private string WhatText => ViewModel.Kind switch
    {
        PostKind.Reel => "o Reel",
        PostKind.Carousel => "o carrossel",
        PostKind.Story => "o story",
        _ => "a imagem",
    };

    private async void Schedule_Click(object sender, RoutedEventArgs e)
    {
        var field = new DateTimeField();
        var what = WhatText;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Agendar {what[2..]}",
            Content = field,
            PrimaryButtonText = "Agendar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += (_, args) => args.Cancel = !field.IsValid;
        field.ValueChanged += (_, _) => dialog.IsPrimaryButtonEnabled = field.IsValid;
        dialog.IsPrimaryButtonEnabled = field.IsValid;

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.PublishAsync(field.Value);
    }

    private void OpenCalendar_Click(object sender, RoutedEventArgs e) => App.MainWindow?.NavigateTo("calendar");

    private async void Publish_Click(object sender, RoutedEventArgs e)
    {
        var what = WhatText;
        var feminine = what.StartsWith("a ", StringComparison.Ordinal);
        var extra = ViewModel.IsStory ? " O story fica no ar por 24 horas." : "";
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Publicar agora?",
            Content = $"{char.ToUpper(what[0])}{what[1..]} vai ser publicad{(feminine ? "a" : "o")} em @{ViewModel.Username} e ficará visível para seus seguidores imediatamente.{extra}",
            PrimaryButtonText = "Publicar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.PublishNowAsync();
    }
}
