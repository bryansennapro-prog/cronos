using InstaPanel.Core;
using Velopack;

namespace InstaPanel.App.Services;

/// <summary>
/// Atualização automática (Velopack). As versões ficam no R2 e são servidas pelo Worker em
/// &lt;URL do Worker&gt;/updates. INSTAPANEL_UPDATE_URL troca a origem (pasta local ou outra URL), para testes.
/// </summary>
public static class AppUpdates
{
    private static UpdateInfo? _pending;

    public static event EventHandler? StateChanged;

    public static string FeedUrl =>
        Environment.GetEnvironmentVariable("INSTAPANEL_UPDATE_URL") is { Length: > 0 } custom
            ? custom
            : AppServices.Settings.Load().WorkerUrl.TrimEnd('/') + "/updates";

    private static UpdateManager Manager() => new(FeedUrl);

    /// <summary>Instalado pelo instalador (false quando roda direto da pasta de build).</summary>
    public static bool IsInstalled
    {
        get
        {
            try { return Manager().IsInstalled; }
            catch (Exception) { return false; }
        }
    }

    public static string CurrentVersion
    {
        get
        {
            try
            {
                if (Manager().CurrentVersion is { } v) return v.ToString();
            }
            catch (Exception)
            {
                // Fora do instalador.
            }
            return typeof(AppUpdates).Assembly.GetName().Version?.ToString(3) ?? "?";
        }
    }

    /// <summary>Versão nova já encontrada (ou null).</summary>
    public static string? AvailableVersion => _pending?.TargetFullRelease.Version.ToString();

    public static bool UpdateAvailable => _pending is not null;

    /// <summary>Procura versão nova. Devolve a versão, ou null se já está na última. Fora do instalador não faz nada.</summary>
    public static async Task<string?> CheckAsync()
    {
        var mgr = Manager();
        if (!mgr.IsInstalled) return null;
        _pending = await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
        StateChanged?.Invoke(null, EventArgs.Empty);
        return AvailableVersion;
    }

    /// <summary>Baixa a versão nova (progresso 0–100), aplica e reinicia o painel.</summary>
    public static async Task DownloadAndRestartAsync(Action<int>? progress = null)
    {
        if (_pending is null) return;
        var mgr = Manager();
        await mgr.DownloadUpdatesAsync(_pending, progress).ConfigureAwait(false);
        mgr.ApplyUpdatesAndRestart(_pending.TargetFullRelease);
    }

    /// <summary>Verificação silenciosa ao abrir e a cada 6 horas.</summary>
    public static void StartBackgroundChecks(Microsoft.UI.Dispatching.DispatcherQueue dispatcher)
    {
        if (!IsInstalled) return;
        var timer = dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromHours(6);
        timer.Tick += async (_, _) => await TryCheckAsync();
        timer.Start();
        dispatcher.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            await TryCheckAsync();
        });
    }

    private static async Task TryCheckAsync()
    {
        try
        {
            await CheckAsync();
        }
        catch (Exception)
        {
            // Sem internet: tenta na próxima.
        }
    }
}
