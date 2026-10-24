using InstaPanel.App.Services;
using InstaPanel.Cloud;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace InstaPanel.App.ViewModels;

/// <summary>Um post como aparece em listas (Início, Histórico) e no Calendário.</summary>
public sealed class PostItemViewModel
{
    private readonly string? _thumbnailPath;
    private ImageSource? _thumbnail;

    public PostItemViewModel(PostInfo post, string? thumbnailPath = null, bool detailed = false)
    {
        Post = post;
        _thumbnailPath = thumbnailPath;
        Detailed = detailed;
    }

    public PostInfo Post { get; }

    public string Id => Post.Id;

    /// <summary>No calendário semanal os cartões mostram miniatura e mais texto.</summary>
    public bool Detailed { get; }

    public string TypeText => Formatting.PostType(Post.Type);

    public string TypeGlyph => Formatting.PostTypeGlyph(Post.Type);

    public string StateText => Formatting.PostState(Post.State);

    public string StateGlyph => Formatting.PostStateGlyph(Post.State);

    public Brush StateBrush => Formatting.PostStateBrush(Post.State);

    public string Caption => string.IsNullOrWhiteSpace(Post.Caption) ? "(sem legenda)" : Post.Caption.ReplaceLineEndings(" ");

    public DateTime LocalScheduled => Post.ScheduledAt.ToLocalTime().DateTime;

    public string TimeText => LocalScheduled.ToString("HH:mm", Formatting.PtBr);

    public string ChipText => $"{TimeText} · {Caption}";

    public string WhenText => Formatting.PostWhen(Post);

    public string? Error => Post.State == PostStates.Failed ? Post.Error : null;

    public bool HasError => Error is not null;

    public Uri? Permalink => Uri.TryCreate(Post.Permalink, UriKind.Absolute, out var u) ? u : null;

    public bool HasPermalink => Permalink is not null;

    public bool IsEditable => PostStates.IsEditable(Post.State);

    public double ChipOpacity => Post.State is PostStates.Published ? 0.75 : 1;

    public string ToolTip => $"{TypeText} · {StateText}\n{WhenText}\n{Caption}" + (HasError ? $"\n⚠ {Error}" : "") +
                             (IsEditable ? "\nArraste para outro dia para remarcar." : "");

    public ImageSource? Thumbnail => _thumbnail ??=
        _thumbnailPath is { } p && File.Exists(p) ? new BitmapImage(new Uri(p)) { DecodePixelWidth = 120 } : null;

    public bool HasThumbnail => _thumbnailPath is { } p && File.Exists(p);

    public bool HasNoThumbnail => !HasThumbnail;
}
