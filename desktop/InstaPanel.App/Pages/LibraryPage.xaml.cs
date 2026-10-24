using System.Diagnostics;
using InstaPanel.App.ViewModels;
using InstaPanel.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace InstaPanel.App.Pages;

public sealed partial class LibraryPage : Page
{
    public LibraryPage()
    {
        InitializeComponent();
    }

    public LibraryViewModel ViewModel { get; } = new();

    /// <summary>Arquivos recebidos pela linha de comando, importados ao abrir a tela.</summary>
    public static IReadOnlyList<string>? PendingImport { get; set; }

    // A página fica em cache; recarrega a cada visita para refletir posts publicados no meio-tempo.
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (ViewModel.IsImporting) return;
        await ViewModel.LoadAsync();
        if (PendingImport is { } files)
        {
            PendingImport = null;
            await ViewModel.ImportAsync(files);
        }
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.Thumbnail, SuggestedStartLocation = PickerLocationId.VideosLibrary };
        foreach (var ext in MediaProbe.ImageExtensions.Concat(MediaProbe.VideoExtensions)) picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

        var files = await picker.PickMultipleFilesAsync();
        if (files.Count > 0) await ViewModel.ImportAsync(files.Select(f => f.Path).ToList());
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Adicionar à biblioteca";
        DropOverlay.Visibility = Visibility.Visible;
    }

    private void Root_DragLeave(object sender, DragEventArgs e) => DropOverlay.Visibility = Visibility.Collapsed;

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        var items = await e.DataView.GetStorageItemsAsync();
        var paths = new List<string>();
        foreach (var item in items)
        {
            if (item is StorageFile file) paths.Add(file.Path);
            else if (item is StorageFolder folder)
                paths.AddRange(Directory.EnumerateFiles(folder.Path).Where(MediaProbe.IsSupportedFile));
        }
        if (paths.Count > 0) await ViewModel.ImportAsync(paths);
    }

    private void FilterBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) =>
        ViewModel.Filter = sender.SelectedItem?.Tag as string ?? "all";

    private void Grid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MediaItemViewModel vm) App.MainWindow?.NavigateToCompose(vm.Id);
    }

    private void CreatePost_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is MediaItemViewModel vm) App.MainWindow?.NavigateToCompose(vm.Id);
    }

    private void ShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is MediaItemViewModel { FileExists: true } vm)
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{vm.Item.Path}\"") { UseShellExecute = true });
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is MediaItemViewModel vm) ViewModel.Remove(vm);
    }
}
