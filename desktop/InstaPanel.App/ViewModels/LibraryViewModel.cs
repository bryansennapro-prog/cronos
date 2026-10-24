using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using InstaPanel.App.Services;
using InstaPanel.Core;
using InstaPanel.Data;
using InstaPanel.Media;

namespace InstaPanel.App.ViewModels;

public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly List<MediaItemViewModel> _all = [];

    public ObservableCollection<MediaItemViewModel> Items { get; } = [];

    /// <summary>"all", "image" ou "video".</summary>
    [ObservableProperty] public partial string Filter { get; set; } = "all";

    [ObservableProperty] public partial bool IsEmpty { get; set; } = true;
    [ObservableProperty] public partial string CountText { get; set; } = "";

    [ObservableProperty] public partial bool IsImporting { get; set; }
    [ObservableProperty] public partial string ImportText { get; set; } = "";
    [ObservableProperty] public partial double ImportProgress { get; set; }

    [ObservableProperty] public partial bool FfmpegMissing { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasImportErrors))]
    public partial string? ImportErrors { get; set; }

    public bool HasImportErrors => !string.IsNullOrEmpty(ImportErrors);

    partial void OnFilterChanged(string value) => ApplyFilter();

    public async Task LoadAsync()
    {
        FfmpegMissing = AppServices.Ffmpeg is null;
        var (items, published) = await Task.Run(() =>
            (AppServices.Library.GetMediaItems(), AppServices.Library.GetPublishedMediaIds()));
        _all.Clear();
        _all.AddRange(items.Select(i => new MediaItemViewModel(i, published.Contains(i.Id))));
        ApplyFilter();
    }

    public async Task ImportAsync(IReadOnlyList<string> paths)
    {
        var tools = AppServices.Ffmpeg;
        FfmpegMissing = tools is null;
        if (tools is null || IsImporting) return;

        var supported = paths.Where(MediaProbe.IsSupportedFile).ToList();
        var errors = new List<string>();
        errors.AddRange(paths.Except(supported).Select(p => $"{Path.GetFileName(p)}: formato não suportado."));

        IsImporting = true;
        ImportErrors = null;
        try
        {
            for (var i = 0; i < supported.Count; i++)
            {
                var path = supported[i];
                ImportText = $"Importando {i + 1} de {supported.Count}: {Path.GetFileName(path)}";
                ImportProgress = (double)i / supported.Count * 100;
                try
                {
                    var item = await Task.Run(() => ImportOneAsync(tools, path));
                    if (item is not null)
                    {
                        _all.Insert(0, new MediaItemViewModel(item, isPublished: false));
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"{Path.GetFileName(path)}: {AppServices.Describe(ex)}");
                }
            }
        }
        finally
        {
            IsImporting = false;
            ImportErrors = errors.Count > 0 ? string.Join("\n", errors) : null;
            ApplyFilter();
        }
    }

    /// <summary>Lê o arquivo com ffprobe, gera miniatura e grava no banco. Null se já estava na biblioteca.</summary>
    private static async Task<MediaItem?> ImportOneAsync(FfmpegTools tools, string path)
    {
        if (AppServices.Library.FindByPath(path) is not null) return null;

        var info = await MediaProbe.ProbeAsync(tools, path);
        var id = Guid.NewGuid().ToString("N");
        string? thumb = Path.Combine(AppServices.ThumbnailsDirectory, id + ".jpg");
        try
        {
            await MediaConverter.CreateThumbnailAsync(tools, path, info, thumb);
        }
        catch (MediaToolException)
        {
            thumb = null; // Sem miniatura, mas o arquivo pode ser usado.
        }

        var item = new MediaItem { Id = id, Path = Path.GetFullPath(path), Info = info, ThumbnailPath = thumb, AddedAt = DateTimeOffset.Now };
        AppServices.Library.AddMediaItem(item);
        return item;
    }

    public void Remove(MediaItemViewModel vm)
    {
        AppServices.Library.RemoveMediaItem(vm.Id);
        if (vm.Item.ThumbnailPath is { } thumb)
        {
            try { File.Delete(thumb); } catch (IOException) { }
        }
        _all.Remove(vm);
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        Items.Clear();
        foreach (var vm in _all.Where(v => Filter switch
                 {
                     "image" => !v.IsVideo,
                     "video" => v.IsVideo,
                     _ => true,
                 }))
        {
            Items.Add(vm);
        }

        IsEmpty = _all.Count == 0;
        var images = _all.Count(v => !v.IsVideo);
        var videos = _all.Count - images;
        CountText = $"{images} {(images == 1 ? "imagem" : "imagens")} · {videos} {(videos == 1 ? "vídeo" : "vídeos")}";
    }
}
