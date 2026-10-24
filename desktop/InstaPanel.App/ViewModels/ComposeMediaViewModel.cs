using InstaPanel.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace InstaPanel.App.ViewModels;

/// <summary>Uma mídia escolhida no Novo post, com a compatibilidade calculada para o formato atual.</summary>
public sealed class ComposeMediaViewModel(MediaItemViewModel media, int index, int count, Compatibility compat)
{
    public MediaItemViewModel Media { get; } = media;

    public int Index { get; } = index;

    public Compatibility Compat { get; } = compat;

    public string PositionText => (Index + 1).ToString();

    public bool ShowPosition => count > 1;

    public bool CanMoveUp => Index > 0;

    public bool CanMoveDown => Index < count - 1;

    public bool IsBlocked => !Media.FileExists || Compat.Status == CompatibilityStatus.Unsupported;

    public ImageSource? Thumbnail => Media.Thumbnail;

    public string FileName => Media.FileName;

    public string DetailsText => $"{Media.KindLabel} · {Media.DetailsText}";

    public string CompatText => !Media.FileExists
        ? "Arquivo não encontrado no disco"
        : Compat.Notes.Count == 0 ? Compat.Summary : $"{Compat.Summary}: {string.Join(" ", Compat.Notes)}";

    public string CompatGlyph => IsBlocked ? "" : Compat.Status == CompatibilityStatus.Ready ? "" : "";

    public Brush CompatBrush => (Brush)Application.Current.Resources[IsBlocked
        ? "SystemFillColorCriticalBrush"
        : Compat.Status == CompatibilityStatus.Ready ? "SystemFillColorSuccessBrush" : "SystemFillColorAttentionBrush"];
}
