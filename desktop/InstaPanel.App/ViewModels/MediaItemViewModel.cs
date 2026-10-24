using InstaPanel.App.Services;
using InstaPanel.Core;
using InstaPanel.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace InstaPanel.App.ViewModels;

/// <summary>Um arquivo da biblioteca, pronto para exibir.</summary>
public sealed class MediaItemViewModel
{
    private ImageSource? _thumbnail;

    public MediaItemViewModel(MediaItem item, bool isPublished)
    {
        Item = item;
        IsPublished = isPublished;
        Compatibility = MediaRules.Evaluate(item.Info);
        FileExists = File.Exists(item.Path);
    }

    public MediaItem Item { get; }

    public string Id => Item.Id;

    public string FileName => Item.FileName;

    public bool IsVideo => Item.Info.Kind == MediaKind.Video;

    public bool IsPublished { get; }

    public bool FileExists { get; }

    public Compatibility Compatibility { get; }

    public PostKind PostKind => MediaRules.PostKindFor(Item.Info.Kind);

    public string KindLabel => IsVideo ? $"VÍDEO · {Formatting.Duration(Item.Info.Duration)}" : "IMAGEM";

    public string DetailsText => $"{Formatting.Dimensions(Item.Info)} · {MediaRules.FormatAspect(Item.Info.AspectRatio)} · {Formatting.Size(Item.Info.SizeBytes)}";

    public string PostKindText => PostKind == PostKind.Reel ? "Reel" : "Imagem no feed";

    public string CompatibilityText => !FileExists
        ? "Arquivo não encontrado no disco"
        : Compatibility.Notes.Count == 0
            ? Compatibility.Summary
            : $"{Compatibility.Summary}\n• {string.Join("\n• ", Compatibility.Notes)}";

    public string CompatibilityGlyph => !FileExists ? "" : Compatibility.Status switch
    {
        CompatibilityStatus.Ready => "",
        CompatibilityStatus.NeedsConversion => "",
        _ => "",
    };

    public Brush CompatibilityBrush => (Brush)Application.Current.Resources[!FileExists ? "SystemFillColorCriticalBrush" : Compatibility.Status switch
    {
        CompatibilityStatus.Ready => "SystemFillColorSuccessBrush",
        CompatibilityStatus.NeedsConversion => "SystemFillColorAttentionBrush",
        _ => "SystemFillColorCriticalBrush",
    }];

    public bool CanPublish => FileExists && Compatibility.Status != CompatibilityStatus.Unsupported;

    public double PickerOpacity => CanPublish ? 1 : 0.45;

    public ImageSource? Thumbnail => _thumbnail ??=
        Item.ThumbnailPath is { } p && File.Exists(p) ? new BitmapImage(new Uri(p)) { DecodePixelWidth = 360 } : null;

    /// <summary>Altura da prévia no "celular" (300 px de largura), respeitando a proporção que o Instagram vai mostrar.</summary>
    public double PreviewHeight(double width)
    {
        if (PostKind == PostKind.Reel) return width * 16 / 9;
        var aspect = Math.Clamp(Item.Info.AspectRatio, MediaRules.MinImageAspect, MediaRules.MaxImageAspect);
        return width / aspect;
    }
}
