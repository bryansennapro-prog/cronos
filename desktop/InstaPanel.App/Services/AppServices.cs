using InstaPanel.Cloud;
using InstaPanel.Core;
using InstaPanel.Data;
using InstaPanel.Media;

namespace InstaPanel.App.Services;

/// <summary>Serviços compartilhados pelas telas. Um único processo, um único usuário: estático basta.</summary>
public static class AppServices
{
    private static readonly Lazy<LibraryDatabase> LazyLibrary = new(() => new LibraryDatabase());
    private static FfmpegTools? _ffmpeg;

    public static SettingsStore Settings { get; } = new();

    public static PanelKeyStore PanelKey { get; } = new();

    public static LibraryDatabase Library => LazyLibrary.Value;

    public static string ThumbnailsDirectory { get; } = Path.Combine(AppPaths.DataDirectory, "thumbs");

    /// <summary>Arquivos convertidos aguardando envio; apagados depois do upload.</summary>
    public static string CacheDirectory { get; } = Path.Combine(AppPaths.DataDirectory, "cache");

    /// <summary>FFmpeg localizado (procura de novo enquanto não encontrar, caso seja instalado com o app aberto).</summary>
    public static FfmpegTools? Ffmpeg => _ffmpeg ??= FfmpegTools.TryLocate();

    /// <summary>Disparado quando a URL ou a chave mudam, ou a conta é conectada/desconectada.</summary>
    public static event EventHandler? ConnectionChanged;

    public static void NotifyConnectionChanged() => ConnectionChanged?.Invoke(null, EventArgs.Empty);

    /// <summary>Disparado quando um post é criado ou muda de estado neste painel.</summary>
    public static event EventHandler? PostsChanged;

    public static void NotifyPostsChanged() => PostsChanged?.Invoke(null, EventArgs.Empty);

    /// <summary>Cria um cliente com a configuração salva, ou null se faltar URL válida ou chave.</summary>
    public static WorkerClient? TryCreateClient()
    {
        var key = PanelKey.Read();
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (!SettingsStore.TryNormalizeWorkerUrl(Settings.Load().WorkerUrl, out var url)) return null;
        return new WorkerClient(url, key);
    }

    /// <summary>Mensagem amigável para exibir ao usuário.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        WorkerApiException { IsUnauthorized: true } =>
            "O Worker recusou a chave do painel. Confira a chave em Configurações.",
        WorkerApiException { IsNotConnected: true } =>
            "Nenhuma conta Instagram conectada ao Worker.",
        WorkerApiException w => w.Message,
        MediaToolException m => m.Message,
        IOException io => $"Erro ao ler o arquivo: {io.Message}",
        _ => $"Erro inesperado: {ex.Message}",
    };
}
