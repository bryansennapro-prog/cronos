using InstaPanel.App.Services;
using InstaPanel.Cloud;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace InstaPanel.App.Dialogs;

/// <summary>
/// Login OAuth do Instagram num navegador interno (WebView2). Quem troca o código pelo token é o
/// Worker (que tem a chave secreta); aqui só abrimos a página da Meta e acompanhamos o resultado.
/// </summary>
public sealed partial class InstagramLoginDialog : ContentDialog
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly WorkerClient _client;
    private readonly OAuthStart _start;
    private bool _checking;

    private InstagramLoginDialog(WorkerClient client, OAuthStart start)
    {
        InitializeComponent();
        _client = client;
        _start = start;
        Loaded += async (_, _) => await OpenAsync();
        Closed += (_, _) => Browser.Close();
    }

    /// <summary>@usuario conectado, ou null se a pessoa cancelou ou deu erro.</summary>
    public string? ConnectedUsername { get; private set; }

    /// <summary>Abre o login. Devolve o @usuario conectado ou null. Erros de configuração viram exceção.</summary>
    public static async Task<string?> RunAsync(XamlRoot root, WorkerClient client)
    {
        var start = await client.StartOAuthAsync();
        var dialog = new InstagramLoginDialog(client, start) { XamlRoot = root };
        await dialog.ShowAsync();
        return dialog.ConnectedUsername;
    }

    private async Task OpenAsync()
    {
        try
        {
            await Browser.EnsureCoreWebView2Async();
        }
        catch (Exception ex)
        {
            LoadingRing.IsActive = false;
            Show(InfoBarSeverity.Error, "Navegador interno indisponível",
                $"O WebView2 do Windows não abriu ({ex.Message}). Use \"colar token\" em Configurações.");
            return;
        }

        var core = Browser.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        // Links que abririam outra janela (termos, ajuda) vão para o navegador padrão.
        core.NewWindowRequested += (sender, e) =>
        {
            e.Handled = true;
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps)
                _ = Windows.System.Launcher.LaunchUriAsync(u);
        };
        core.NavigationCompleted += async (sender, e) =>
        {
            LoadingRing.IsActive = false;
            if (core.Source.StartsWith(_start.RedirectUri, StringComparison.OrdinalIgnoreCase)) await CheckResultAsync();
        };
        core.Navigate(_start.AuthorizeUrl);
    }

    /// <summary>A Meta devolveu para o Worker; pergunta a ele como terminou.</summary>
    private async Task CheckResultAsync()
    {
        if (_checking) return;
        _checking = true;
        try
        {
            for (var i = 0; i < 15; i++)
            {
                var status = await _client.GetOAuthStatusAsync(_start.State);
                if (status.IsConnected)
                {
                    ConnectedUsername = status.Username;
                    Show(InfoBarSeverity.Success, $"Conectado como @{status.Username}", "Pronto! Fechando…");
                    CloseButtonText = "Fechar";
                    await Task.Delay(1200);
                    Hide();
                    return;
                }
                if (status.IsError)
                {
                    Show(InfoBarSeverity.Error, "Não foi possível conectar", status.Error ?? "A Meta recusou o login.");
                    CloseButtonText = "Fechar";
                    return;
                }
                await Task.Delay(PollInterval);
            }
            Show(InfoBarSeverity.Warning, "Ainda processando", "A Meta está demorando. Feche e confira a conta em Configurações.");
        }
        catch (WorkerApiException ex)
        {
            Show(InfoBarSeverity.Error, "Não foi possível conferir o login", AppServices.Describe(ex));
        }
        finally
        {
            _checking = false;
        }
    }

    private void Show(InfoBarSeverity severity, string title, string message)
    {
        Status.Severity = severity;
        Status.Title = title;
        Status.Message = message;
        Status.IsOpen = true;
    }
}
