using InstaPanel.Cloud;
using Microsoft.UI.Dispatching;

namespace InstaPanel.App.Services;

/// <summary>
/// Enquanto o painel está aberto, confere a cada minuto os posts feitos neste PC. Quando um muda para
/// publicado ou falhou, atualiza o banco local, avisa as telas e mostra uma notificação do Windows.
/// Ao abrir, também avisa o que aconteceu enquanto o painel estava fechado.
/// </summary>
public static class PostSync
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static DispatcherQueueTimer? _timer;
    private static bool _syncing;

    /// <summary>Clique em uma notificação: o app abre o Histórico.</summary>
    public static event EventHandler? NotificationClicked;

    /// <summary>Por que as notificações não funcionam (null = funcionando).</summary>
    public static string? NotificationProblem { get; private set; }

    public static void Start(DispatcherQueue dispatcher)
    {
        NotificationProblem = WindowsToast.Register();
        WindowsToast.Clicked += (_, _) => dispatcher.TryEnqueue(() => NotificationClicked?.Invoke(null, EventArgs.Empty));

        _timer = dispatcher.CreateTimer();
        _timer.Interval = Interval;
        _timer.Tick += async (_, _) => await SyncAsync();
        _timer.Start();
        dispatcher.TryEnqueue(DispatcherQueuePriority.Low, async () => await SyncAsync());
    }

    public static void Stop() => _timer?.Stop();

    public static async Task SyncAsync()
    {
        if (_syncing) return;
        using var client = AppServices.TryCreateClient();
        if (client is null) return;

        _syncing = true;
        try
        {
            var remote = await client.ListPostsAsync(100);
            var local = await Task.Run(AppServices.Library.GetLocalPosts);
            var finished = new List<PostInfo>();
            var anyChange = false;

            foreach (var post in remote)
            {
                if (!local.TryGetValue(post.Id, out var known) || (known.State == post.State && known.Permalink == post.Permalink)) continue;
                anyChange = true;
                AppServices.Library.UpsertPost(known with
                {
                    State = post.State,
                    Permalink = post.Permalink,
                    Error = post.Error,
                    UpdatedAt = post.UpdatedAt,
                });
                if (known.State != post.State && post.State is PostStates.Published or PostStates.Failed) finished.Add(post);
            }

            if (anyChange) AppServices.NotifyPostsChanged();
            Notify(finished);
        }
        catch (WorkerApiException)
        {
            // Sem internet ou Worker fora do ar: tenta no próximo minuto.
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Notificação de teste (Configurações). Devolve o problema, ou null se foi enviada.</summary>
    public static string? ShowTest() =>
        NotificationProblem ?? WindowsToast.Show("Teste do Cronos", "As notificações estão funcionando. Você será avisado quando um post sair.");

    private static void Notify(IReadOnlyList<PostInfo> finished)
    {
        if (NotificationProblem is not null || finished.Count == 0) return;

        if (finished.Count > 3)
        {
            var ok = finished.Count(p => p.State == PostStates.Published);
            WindowsToast.Show($"{finished.Count} posts atualizados", $"{ok} publicado(s), {finished.Count - ok} com falha. Veja no Histórico.");
            return;
        }

        foreach (var p in finished)
        {
            var caption = string.IsNullOrWhiteSpace(p.Caption) ? "(sem legenda)" : p.Caption.ReplaceLineEndings(" ");
            if (caption.Length > 80) caption = caption[..80] + "…";
            var what = Formatting.PostType(p.Type);
            var suffix = p.Type == "IMAGE" ? "a" : "o"; // a imagem; o Reel, o carrossel, o story
            if (p.State == PostStates.Published)
                WindowsToast.Show($"{what} publicad{suffix} ✅", caption);
            else
                WindowsToast.Show($"{what} não foi publicad{suffix} ❌", p.Error ?? caption);
        }
    }
}
