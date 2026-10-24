using InstaPanel.App.Dialogs;
using InstaPanel.App.Services;
using InstaPanel.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace InstaPanel.App.Pages;

public sealed partial class CalendarPage : Page
{
    private const string DragFormat = "InstaPanel.PostId:";

    public CalendarPage()
    {
        InitializeComponent();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(CalendarViewModel.Mode)) return;
            ModeBar.SelectedItem = ModeBar.Items[ViewModel.Mode == CalendarMode.Week ? 1 : 0];
        };
        ViewModel.DaysBuilt += (_, _) =>
        {
            SizeCells();
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ScrollToToday);
        };
    }

    private const double MonthCellHeight = 122;
    private const double WeekChipHeight = 176;

    /// <summary>
    /// O UniformGridLayout dá a mesma altura a todos os dias. No mês cabem 3 cartões (+N mais);
    /// na semana a altura acompanha o dia mais cheio, para nenhum cartão ficar cortado.
    /// </summary>
    private void SizeCells()
    {
        var layout = (UniformGridLayout)Resources["DaysLayout"];
        if (ViewModel.Mode == CalendarMode.Month)
        {
            layout.MinItemHeight = MonthCellHeight;
            return;
        }
        var busiest = ViewModel.Days.Count == 0 ? 0 : ViewModel.Days.Max(d => d.VisiblePosts.Count);
        layout.MinItemHeight = Math.Max(460, 44 + busiest * WeekChipHeight);
    }

    /// <summary>No mês, deixa a semana de hoje à vista (a grade tem 6 semanas e não cabe inteira na tela).</summary>
    private void ScrollToToday()
    {
        if (ViewModel.Mode != CalendarMode.Month)
        {
            DaysScroll.ChangeView(null, 0, null, disableAnimation: true);
            return;
        }
        var index = ViewModel.Days.ToList().FindIndex(d => d.IsToday);
        if (index < 0)
        {
            DaysScroll.ChangeView(null, 0, null, disableAnimation: true);
            return;
        }
        var layout = (UniformGridLayout)Resources["DaysLayout"];
        var rowHeight = layout.MinItemHeight + layout.MinRowSpacing;
        var row = index / 7;
        // Mostra a semana anterior também, para dar contexto.
        DaysScroll.ChangeView(null, Math.Max(0, (row - 1) * rowHeight), null, disableAnimation: true);
    }

    public CalendarViewModel ViewModel { get; } = new();

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

    private void ModeBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) =>
        ViewModel.Mode = sender.SelectedItem?.Tag as string == "week" ? CalendarMode.Week : CalendarMode.Month;

    private async void Chip_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.Tag is PostItemViewModel item)
        {
            await PostDetailsDialog.OpenAsync(XamlRoot, item.Post); // se mudar, PostsChanged recarrega
        }
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is CalendarDayViewModel day) ViewModel.ShowWeekOf(day.Date);
    }

    // ---------- Arrastar para remarcar ----------

    private void Chip_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is not PostItemViewModel item) return;
        args.Data.SetText(DragFormat + item.Id);
        args.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void Day_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.Text)) return;
        if ((sender as FrameworkElement)?.Tag is not CalendarDayViewModel day || day.IsPast) return;
        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.Caption = $"Mover para {day.Date:dd/MM}";
        if (sender is Border b) b.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
    }

    private void Day_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border b) b.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
    }

    private async void Day_Drop(object sender, DragEventArgs e)
    {
        Day_DragLeave(sender, e);
        if ((sender as FrameworkElement)?.Tag is not CalendarDayViewModel day) return;
        var text = await e.DataView.GetTextAsync();        if (!text.StartsWith(DragFormat, StringComparison.Ordinal)) return;

        var error = await ViewModel.MoveToDayAsync(text[DragFormat.Length..], day.Date);
        MoveError.Message = error ?? "";
        MoveError.IsOpen = error is not null;
    }
}
