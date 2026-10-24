using System.Diagnostics;
using InstaPanel.App.Dialogs;
using InstaPanel.App.Services;
using InstaPanel.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace InstaPanel.App.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; } = new();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        NotificationStatus.Text = PostSync.NotificationProblem ?? "Ativas.";
        AppUpdates.StateChanged += OnUpdateStateChanged;
        RenderUpdateState();
        await ViewModel.LoadAsync();
    }

    // ---------- Atualizações ----------

    /// <summary>O que o botão faz agora — decidido ao desenhar, para nunca instalar quando ele diz "procurar".</summary>
    private bool _buttonInstalls;

    private void OnUpdateStateChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() => RenderUpdateState());

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        AppUpdates.StateChanged -= OnUpdateStateChanged;
        base.OnNavigatedFrom(e);
    }

    private void RenderUpdateState(string? message = null)
    {
        VersionLabel.Text = $"Cronos {AppUpdates.CurrentVersion}";
        if (!AppUpdates.IsInstalled)
        {
            UpdateStatus.Text = "Rodando fora do instalador (modo desenvolvimento): atualização automática desligada.";
            UpdateButton.IsEnabled = false;
            return;
        }
        UpdateButton.IsEnabled = true;
        _buttonInstalls = AppUpdates.UpdateAvailable;
        UpdateButton.Content = _buttonInstalls ? $"Atualizar para {AppUpdates.AvailableVersion}" : "Procurar atualizações";
        UpdateStatus.Text = message ?? (AppUpdates.UpdateAvailable
            ? $"Versão {AppUpdates.AvailableVersion} disponível. O painel reinicia em poucos segundos."
            : "O painel procura versões novas sozinho ao abrir e a cada 6 horas.");
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        try
        {
            if (!_buttonInstalls)
            {
                UpdateStatus.Text = "Procurando…";
                var found = await AppUpdates.CheckAsync();
                RenderUpdateState(found is null ? $"Você já está na versão mais recente (verificado às {DateTime.Now:HH:mm})." : null);
                return;
            }

            UpdateProgress.Visibility = Visibility.Visible;
            UpdateStatus.Text = $"Baixando a versão {AppUpdates.AvailableVersion}…";
            await AppUpdates.DownloadAndRestartAsync(p => DispatcherQueue.TryEnqueue(() => UpdateProgress.Value = p));
        }
        catch (Exception ex)
        {
            UpdateProgress.Visibility = Visibility.Collapsed;
            RenderUpdateState($"Não foi possível atualizar: {ex.Message}");
        }
    }

    private async void InstagramLogin_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoginWithInstagramAsync(client => InstagramLoginDialog.RunAsync(XamlRoot, client));
    }

    private void TestNotification_Click(object sender, RoutedEventArgs e)
    {
        var problem = PostSync.ShowTest();
        NotificationStatus.Text = problem ?? $"Notificação de teste enviada às {DateTime.Now:HH:mm:ss}. Se não apareceu, confira o modo Não incomodar do Windows.";
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Desconectar a conta?",
            Content = $"O token de @{ViewModel.ConnectedUsername} será apagado do Worker. "
                    + "Posts agendados não serão publicados até você conectar um novo token.",
            PrimaryButtonText = "Desconectar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.DisconnectAsync();
        }
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ViewModel.DataDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ViewModel.DataDirectory}\"") { UseShellExecute = true });
    }
}
