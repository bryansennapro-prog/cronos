using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstaPanel.App.Services;
using InstaPanel.Cloud;
using InstaPanel.Core;
using Microsoft.UI.Xaml.Controls;

namespace InstaPanel.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty] public partial string WorkerUrl { get; set; } = "";
    [ObservableProperty] public partial string PanelKey { get; set; } = "";
    [ObservableProperty] public partial string KeySourceText { get; set; } = "";

    [ObservableProperty] public partial bool IsBusy { get; set; }

    [ObservableProperty] public partial bool StatusOpen { get; set; }
    [ObservableProperty] public partial string StatusTitle { get; set; } = "";
    [ObservableProperty] public partial string StatusMessage { get; set; } = "";
    [ObservableProperty] public partial InfoBarSeverity StatusSeverity { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsAccountConnected))]
    public partial string? ConnectedUsername { get; set; }

    public bool IsAccountConnected => ConnectedUsername is not null;

    [ObservableProperty] public partial string AccountText { get; set; } = "Verificando…";
    [ObservableProperty] public partial string NewToken { get; set; } = "";

    public string DataDirectory => AppPaths.DataDirectory;

    public string VersionText =>
        $"Cronos {typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3)}";

    public async Task LoadAsync()
    {
        WorkerUrl = AppServices.Settings.Load().WorkerUrl;
        var key = AppServices.PanelKey.Read();
        PanelKey = key ?? "";
        KeySourceText = key is not null
            ? "Chave salva neste PC, protegida pelo Windows (DPAPI)."
            : AppServices.PanelKey.Exists
                ? "Há uma chave salva, mas ela não pôde ser lida por este usuário do Windows. Cole a chave novamente."
                : "Cole a chave do painel mostrada pelo script configurar.ps1.";
        await RefreshAccountAsync();
    }

    [RelayCommand]
    private async Task SaveAndTestAsync()
    {
        if (!SettingsStore.TryNormalizeWorkerUrl(WorkerUrl, out var url))
        {
            ShowStatus(InfoBarSeverity.Error, "URL inválida", "Use o endereço https:// do Worker.");
            return;
        }
        if (string.IsNullOrWhiteSpace(PanelKey))
        {
            ShowStatus(InfoBarSeverity.Error, "Chave ausente", "Cole a chave do painel.");
            return;
        }

        IsBusy = true;
        try
        {
            using var client = new WorkerClient(url, PanelKey);
            await client.GetHealthAsync();
            try
            {
                await client.GetAccountAsync();
            }
            catch (WorkerApiException ex) when (ex.IsNotConnected)
            {
                // A chave é válida; só falta conectar a conta.
            }

            AppServices.Settings.Save(AppServices.Settings.Load() with { WorkerUrl = url.AbsoluteUri.TrimEnd('/') });
            AppServices.PanelKey.Write(PanelKey);
            WorkerUrl = url.AbsoluteUri.TrimEnd('/');
            KeySourceText = "Chave salva neste PC, protegida pelo Windows (DPAPI).";
            ShowStatus(InfoBarSeverity.Success, "Conectado ao Worker", "URL e chave salvas.");
            AppServices.NotifyConnectionChanged();
            await RefreshAccountAsync();
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Não foi possível conectar", AppServices.Describe(ex));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ConnectTokenAsync()
    {
        if (NewToken.Trim().Length < 20)
        {
            ShowStatus(InfoBarSeverity.Error, "Token inválido", "Cole o token de acesso gerado no painel da Meta.");
            return;
        }

        using var client = AppServices.TryCreateClient();
        if (client is null)
        {
            ShowStatus(InfoBarSeverity.Error, "Worker não configurado", "Salve a URL e a chave primeiro.");
            return;
        }

        IsBusy = true;
        try
        {
            var account = await client.ConnectTokenAsync(NewToken);
            NewToken = "";
            ShowStatus(InfoBarSeverity.Success, "Conta conectada", $"Token salvo para @{account.Username}.");
            AppServices.NotifyConnectionChanged();
            await RefreshAccountAsync();
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Token recusado", AppServices.Describe(ex));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>"Entrar com Instagram": abre o login (a página passa o diálogo) e atualiza a conta.</summary>
    public async Task LoginWithInstagramAsync(Func<WorkerClient, Task<string?>> showLogin)
    {
        using var client = AppServices.TryCreateClient();
        if (client is null)
        {
            ShowStatus(InfoBarSeverity.Error, "Worker não configurado", "Salve a URL e a chave primeiro.");
            return;
        }

        try
        {
            var username = await showLogin(client);
            if (username is null) return; // cancelou (ou o erro já apareceu na janela de login)
            ShowStatus(InfoBarSeverity.Success, "Conta conectada", $"Conectado como @{username}. O token fica guardado no Worker e é renovado sozinho.");
            AppServices.NotifyConnectionChanged();
            await RefreshAccountAsync();
        }
        catch (WorkerApiException ex) when (ex.Code == "oauth_not_configured")
        {
            ShowStatus(InfoBarSeverity.Warning, "Login pelo app ainda não configurado",
                "Falta o ID do app do Instagram no Worker e o endereço de retorno no painel da Meta. Veja worker/SETUP.md (parte D). Enquanto isso, use \"colar um token\".");
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Não foi possível abrir o login", AppServices.Describe(ex));
        }
    }

    public async Task DisconnectAsync()
    {
        using var client = AppServices.TryCreateClient();
        if (client is null) return;

        IsBusy = true;
        try
        {
            await client.DisconnectAsync();
            ShowStatus(InfoBarSeverity.Informational, "Conta desconectada", "O token foi apagado do Worker.");
            AppServices.NotifyConnectionChanged();
            await RefreshAccountAsync();
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Falha ao desconectar", AppServices.Describe(ex));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshAccountAsync()
    {
        using var client = AppServices.TryCreateClient();
        if (client is null)
        {
            ConnectedUsername = null;
            AccountText = "Configure o Worker acima para ver a conta.";
            return;
        }

        try
        {
            var status = await client.GetAccountAsync();
            ConnectedUsername = status.Account.Username;
            AccountText = $"Conectado como @{status.Account.Username}";
        }
        catch (WorkerApiException ex) when (ex.IsNotConnected)
        {
            ConnectedUsername = null;
            AccountText = "Nenhuma conta conectada. Cole um token abaixo.";
        }
        catch (Exception ex)
        {
            ConnectedUsername = null;
            AccountText = AppServices.Describe(ex);
        }
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusSeverity = severity;
        StatusTitle = title;
        StatusMessage = message;
        StatusOpen = true;
    }
}
