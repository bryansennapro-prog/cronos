using InstaPanel.App.Controls;
using InstaPanel.App.Services;
using InstaPanel.Cloud;
using InstaPanel.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace InstaPanel.App.Dialogs;

/// <summary>Detalhes de um post: remarcar, editar legenda, cancelar, tentar de novo e linha do tempo.</summary>
public sealed partial class PostDetailsDialog : ContentDialog
{
    private PostInfo _post;
    private bool _confirmingCancel;

    private PostDetailsDialog(PostInfo post, string? thumbnailPath)
    {
        InitializeComponent();
        _post = post;
        if (thumbnailPath is not null && File.Exists(thumbnailPath)) Thumb.Source = new BitmapImage(new Uri(thumbnailPath));
        Render();
    }

    /// <summary>True se o post foi alterado (quem abriu deve recarregar a lista).</summary>
    public bool Changed { get; private set; }

    /// <summary>Abre os detalhes do post. Devolve true se algo mudou.</summary>
    public static async Task<bool> OpenAsync(XamlRoot root, PostInfo post)
    {
        AppServices.Library.GetPostThumbnails().TryGetValue(post.Id, out var thumb);
        var dialog = new PostDetailsDialog(post, thumb) { XamlRoot = root };
        _ = dialog.LoadEventsAsync();
        await dialog.ShowAsync();
        if (dialog.Changed) AppServices.NotifyPostsChanged();
        return dialog.Changed;
    }

    private void Render()
    {
        var editable = PostStates.IsEditable(_post.State);
        Title = editable ? "Editar post" : "Detalhes do post";
        TypeIcon.Glyph = Formatting.PostTypeGlyph(_post.Type);
        TypeIcon.Visibility = Thumb.Source is null ? Visibility.Visible : Visibility.Collapsed;
        TypeText.Text = Formatting.PostType(_post.Type);
        StateText.Text = Formatting.PostState(_post.State);
        StateBadge.Background = Formatting.PostStateBrush(_post.State);
        WhenText.Text = Formatting.PostWhen(_post);

        if (Uri.TryCreate(_post.Permalink, UriKind.Absolute, out var link))
        {
            PermalinkLink.NavigateUri = link;
            PermalinkLink.Visibility = Visibility.Visible;
        }

        ErrorBar.IsOpen = _post.State == PostStates.Failed && _post.Error is not null;
        ErrorBar.Message = _post.Error ?? "";

        EditPanel.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
        CaptionReadOnly.Visibility = editable ? Visibility.Collapsed : Visibility.Visible;
        CaptionReadOnly.Text = string.IsNullOrWhiteSpace(_post.Caption) ? "(sem legenda)" : _post.Caption;
        if (editable)
        {
            When.Value = _post.ScheduledAt > DateTimeOffset.Now.Add(DateTimeField.MinAhead) ? _post.ScheduledAt : DateTimeField.SuggestedDefault();
            CaptionBox.Text = _post.Caption ?? "";
        }

        PrimaryButtonText = editable ? (_post.State == PostStates.Failed ? "Reagendar" : "Salvar alterações") : "";
        SecondaryButtonText = editable ? "Cancelar post" : "";
        DefaultButton = editable ? ContentDialogButton.Primary : ContentDialogButton.Close;
    }

    private async Task LoadEventsAsync()
    {
        using var client = AppServices.TryCreateClient();
        if (client is null) return;
        try
        {
            var full = await client.GetPostAsync(_post.Id);
            EventsPanel.Children.Clear();
            foreach (var e in full.Events ?? [])
            {
                EventsPanel.Children.Add(new TextBlock
                {
                    Text = $"{e.At.ToLocalTime().ToString("dd/MM HH:mm:ss", Formatting.PtBr)} — {DescribeEvent(e)}",
                    TextWrapping = TextWrapping.Wrap,
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                });
            }
        }
        catch (WorkerApiException)
        {
            EventsPanel.Children.Add(new TextBlock { Text = "Não foi possível carregar a linha do tempo." });
        }
    }

    private static string DescribeEvent(PostEvent e)
    {
        var detailMessage = e.Detail is { ValueKind: System.Text.Json.JsonValueKind.Object } d && d.TryGetProperty("message", out var m)
            ? $": {m.GetString()}"
            : "";
        return e.Kind switch
        {
            "post.created" => "Post criado",
            "container.created" => "Mídia enviada para a Meta",
            "container.ready" => "Meta terminou de processar a mídia",
            "container.expired" => "Preparação expirou na Meta; refeita",
            "publish.limit_reached" => "Limite de publicação atingido; aguardando",
            "post.published" => "Publicado no Instagram",
            "post.retry_scheduled" => "Erro temporário, nova tentativa agendada" + detailMessage,
            "post.failed" => "Falhou" + detailMessage,
            "post.retry" => "Nova tentativa pedida pelo painel",
            "post.updated" => "Remarcado/editado",
            "post.canceled" => "Cancelado",
            _ => e.Kind,
        };
    }

    private void CaptionBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var s = CaptionStats.From(CaptionBox.Text);
        CaptionCounter.Text = $"{s.Characters}/{CaptionStats.MaxCharacters} caracteres · {s.Hashtags}/{CaptionStats.MaxHashtags} hashtags";
        CaptionCounter.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[s.IsValid ? "TextFillColorSecondaryBrush" : "SystemFillColorCriticalBrush"];
    }

    private async void Save_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true; // só fecha se der certo
        if (!When.IsValid)
        {
            ShowError("Escolha um horário válido.");
            return;
        }
        if (!CaptionStats.From(CaptionBox.Text).IsValid)
        {
            ShowError("A legenda passa dos limites do Instagram.");
            return;
        }

        var caption = CaptionBox.Text.Trim();
        var timeChanged = Math.Abs((When.Value - _post.ScheduledAt).TotalMinutes) >= 1 || _post.State == PostStates.Failed;
        var captionChanged = caption != (_post.Caption ?? "");
        if (!timeChanged && !captionChanged)
        {
            Hide();
            return;
        }

        var deferral = args.GetDeferral();
        IsPrimaryButtonEnabled = IsSecondaryButtonEnabled = false;
        try
        {
            using var client = AppServices.TryCreateClient() ?? throw new InvalidOperationException("Configure o Worker em Configurações.");
            _post = await client.UpdatePostAsync(_post.Id, new UpdatePostRequest(
                timeChanged ? When.Value : null,
                captionChanged ? caption : null));
            Changed = true;
            Hide();
        }
        catch (Exception ex)
        {
            ShowError(AppServices.Describe(ex));
        }
        finally
        {
            IsPrimaryButtonEnabled = IsSecondaryButtonEnabled = true;
            deferral.Complete();
        }
    }

    private async void Cancel_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (!_confirmingCancel)
        {
            // Confirmação no próprio diálogo (não dá para abrir outro ContentDialog por cima).
            _confirmingCancel = true;
            SecondaryButtonText = "Sim, cancelar";
            ShowError("O post não será publicado e a mídia será apagada da nuvem. Clique de novo para confirmar.", InfoBarSeverity.Warning);
            return;
        }

        var deferral = args.GetDeferral();
        IsPrimaryButtonEnabled = IsSecondaryButtonEnabled = false;
        try
        {
            using var client = AppServices.TryCreateClient() ?? throw new InvalidOperationException("Configure o Worker em Configurações.");
            _post = await client.CancelPostAsync(_post.Id);
            Changed = true;
            Hide();
        }
        catch (Exception ex)
        {
            ShowError(AppServices.Describe(ex));
        }
        finally
        {
            IsPrimaryButtonEnabled = IsSecondaryButtonEnabled = true;
            deferral.Complete();
        }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        RetryButton.IsEnabled = false;
        try
        {
            using var client = AppServices.TryCreateClient() ?? throw new InvalidOperationException("Configure o Worker em Configurações.");
            _post = await client.RetryPostAsync(_post.Id);
            Changed = true;
            Render();
        }
        catch (Exception ex)
        {
            ShowError(AppServices.Describe(ex));
        }
        finally
        {
            RetryButton.IsEnabled = true;
        }
    }

    private void ShowError(string message, InfoBarSeverity severity = InfoBarSeverity.Error)
    {
        ActionError.Severity = severity;
        ActionError.Message = message;
        ActionError.IsOpen = true;
    }
}
