using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace InstaPanel.Cloud;

/// <summary>Cliente HTTP da API do Worker (Cloudflare). Uma instância por par URL + chave.</summary>
public sealed class WorkerClient : IDisposable
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PartTimeout = TimeSpan.FromMinutes(15);
    private const int PartAttempts = 3;

    private readonly HttpClient _http;

    public WorkerClient(Uri baseUrl, string panelKey, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(panelKey);

        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        // Sem a barra final, "api/..." substituiria o último segmento da URL.
        _http.BaseAddress = baseUrl.AbsoluteUri.EndsWith('/') ? baseUrl : new Uri(baseUrl.AbsoluteUri + "/");
        _http.Timeout = Timeout.InfiniteTimeSpan; // cada chamada define o seu (uploads demoram mais)
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", panelKey.Trim());
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("InstaPanel/0.3");
    }

    // ---------- Conta ----------

    public Task<HealthInfo> GetHealthAsync(CancellationToken ct = default) =>
        SendAsync<HealthInfo>(new HttpRequestMessage(HttpMethod.Get, "api/health"), ct);

    /// <summary>Conta conectada + limite de publicação. Lança <see cref="WorkerApiException"/> com IsNotConnected se não houver conta.</summary>
    public Task<AccountStatus> GetAccountAsync(CancellationToken ct = default) =>
        SendAsync<AccountStatus>(new HttpRequestMessage(HttpMethod.Get, "api/account"), ct);

    public async Task<AccountInfo> ConnectTokenAsync(string accessToken, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        var request = new HttpRequestMessage(HttpMethod.Put, "api/account/token")
        {
            Content = JsonContent.Create(new ConnectTokenRequest(accessToken.Trim()), options: Json),
        };
        var response = await SendAsync<ConnectTokenResponse>(request, ct).ConfigureAwait(false);
        return response.Account;
    }

    public async Task DisconnectAsync(CancellationToken ct = default) =>
        await SendAsync<JsonElement>(new HttpRequestMessage(HttpMethod.Delete, "api/account"), ct).ConfigureAwait(false);

    /// <summary>Começa o login OAuth. Lança WorkerApiException "oauth_not_configured" se o Worker não tiver o ID do app.</summary>
    public Task<OAuthStart> StartOAuthAsync(CancellationToken ct = default) =>
        SendAsync<OAuthStart>(new HttpRequestMessage(HttpMethod.Post, "api/oauth/start"), ct);

    public Task<OAuthStatus> GetOAuthStatusAsync(string state, CancellationToken ct = default) =>
        SendAsync<OAuthStatus>(new HttpRequestMessage(HttpMethod.Get, $"api/oauth/status?state={Uri.EscapeDataString(state)}"), ct);

    /// <summary>Perfil e posts publicados, lidos ao vivo da Meta (até 100 posts, mais recentes primeiro).</summary>
    public Task<ProfileInsights> GetInsightsAsync(int mediaLimit = 60, CancellationToken ct = default) =>
        SendAsync<ProfileInsights>(new HttpRequestMessage(HttpMethod.Get, $"api/insights?limit={mediaLimit}"), ct, TimeSpan.FromSeconds(60));

    /// <summary>
    /// Análise do relatório pelo Claude (pago por uso, só quando o usuário pede).
    /// Lança WorkerApiException "ai_not_configured" se o Worker não tiver a chave da Anthropic.
    /// </summary>
    public Task<AiAnalysis> RequestAiAnalysisAsync(AiAnalysisRequest request, CancellationToken ct = default) =>
        SendAsync<AiAnalysis>(new HttpRequestMessage(HttpMethod.Post, "api/insights/ai")
        {
            Content = JsonContent.Create(request, options: Json),
        }, ct, TimeSpan.FromMinutes(6));

    // ---------- Upload ----------

    /// <summary>Envia o arquivo ao R2 em partes (multipart) e devolve a chave para usar no post.</summary>
    public async Task<string> UploadFileAsync(
        string path, string contentType, IProgress<UploadProgress>? progress = null, CancellationToken ct = default)
    {
        var size = new FileInfo(path).Length;
        var session = await SendAsync<UploadSession>(new HttpRequestMessage(HttpMethod.Post, "api/uploads")
        {
            Content = JsonContent.Create(new CreateUploadRequest(contentType, size, Path.GetFileName(path)), options: Json),
        }, ct).ConfigureAwait(false);

        try
        {
            var parts = new List<UploadedPart>(session.PartCount);
            var buffer = new byte[Math.Min(session.PartSize, size)];
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            long sentBefore = 0;

            for (var n = 1; n <= session.PartCount; n++)
            {
                var length = (int)Math.Min(session.PartSize, size - sentBefore);
                file.Position = sentBefore;
                await file.ReadExactlyAsync(buffer.AsMemory(0, length), ct).ConfigureAwait(false);

                var part = await UploadPartWithRetryAsync(session.Key, n, buffer, length, sentBefore, size, progress, ct).ConfigureAwait(false);
                parts.Add(part);
                sentBefore += length;
            }

            await SendAsync<JsonElement>(new HttpRequestMessage(HttpMethod.Post, "api/uploads/complete")
            {
                Content = JsonContent.Create(new CompleteUploadRequest(session.Key, parts), options: Json),
            }, ct).ConfigureAwait(false);
            progress?.Report(new UploadProgress(size, size));
            return session.Key;
        }
        catch
        {
            await TryDeleteUploadAsync(session.Key).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Apaga um upload que não chegou a virar post.</summary>
    public Task DeleteUploadAsync(string key, CancellationToken ct = default) =>
        SendAsync<JsonElement>(new HttpRequestMessage(HttpMethod.Delete, $"api/uploads?key={Uri.EscapeDataString(key)}"), ct);

    private async Task TryDeleteUploadAsync(string key)
    {
        try
        {
            await DeleteUploadAsync(key, CancellationToken.None).ConfigureAwait(false);
        }
        catch (WorkerApiException)
        {
            // A limpeza diária do Worker remove o que sobrar.
        }
    }

    private async Task<UploadedPart> UploadPartWithRetryAsync(
        string key, int partNumber, byte[] buffer, int length, long offset, long total,
        IProgress<UploadProgress>? progress, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var request = new HttpRequestMessage(HttpMethod.Put, $"api/uploads/part?key={Uri.EscapeDataString(key)}&part={partNumber}")
            {
                Content = new ProgressContent(buffer, length, sent => progress?.Report(new UploadProgress(offset + sent, total))),
            };
            try
            {
                return await SendAsync<UploadedPart>(request, ct, PartTimeout).ConfigureAwait(false);
            }
            catch (WorkerApiException ex) when (attempt < PartAttempts && (ex.StatusCode == 0 || ex.StatusCode >= 500))
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    // ---------- Posts ----------

    public async Task<PostInfo> CreatePostAsync(CreatePostRequest post, CancellationToken ct = default)
    {
        var res = await SendAsync<PostEnvelope>(new HttpRequestMessage(HttpMethod.Post, "api/posts")
        {
            Content = JsonContent.Create(post, options: Json),
        }, ct).ConfigureAwait(false);
        return res.Post;
    }

    public async Task<PostInfo> GetPostAsync(string id, CancellationToken ct = default) =>
        (await SendAsync<PostEnvelope>(new HttpRequestMessage(HttpMethod.Get, $"api/posts/{Uri.EscapeDataString(id)}"), ct).ConfigureAwait(false)).Post;

    public Task<IReadOnlyList<PostInfo>> ListPostsAsync(int limit = 50, CancellationToken ct = default) =>
        ListPostsAsync(new PostQuery { Limit = limit }, ct);

    public async Task<IReadOnlyList<PostInfo>> ListPostsAsync(PostQuery query, CancellationToken ct = default) =>
        (await SendAsync<PostListEnvelope>(new HttpRequestMessage(HttpMethod.Get, $"api/posts?{query.ToQueryString()}"), ct).ConfigureAwait(false)).Posts;

    public async Task<PostInfo> RetryPostAsync(string id, CancellationToken ct = default) =>
        (await SendAsync<PostEnvelope>(new HttpRequestMessage(HttpMethod.Post, $"api/posts/{Uri.EscapeDataString(id)}/retry"), ct).ConfigureAwait(false)).Post;

    /// <summary>Remarca e/ou edita a legenda. Campos nulos não mudam; legenda "" apaga a legenda.</summary>
    public async Task<PostInfo> UpdatePostAsync(string id, UpdatePostRequest update, CancellationToken ct = default) =>
        (await SendAsync<PostEnvelope>(new HttpRequestMessage(HttpMethod.Patch, $"api/posts/{Uri.EscapeDataString(id)}")
        {
            Content = JsonContent.Create(update, options: Json),
        }, ct).ConfigureAwait(false)).Post;

    /// <summary>Cancela um post ainda não publicado (a mídia é apagada da nuvem).</summary>
    public async Task<PostInfo> CancelPostAsync(string id, CancellationToken ct = default) =>
        (await SendAsync<PostEnvelope>(new HttpRequestMessage(HttpMethod.Delete, $"api/posts/{Uri.EscapeDataString(id)}"), ct).ConfigureAwait(false)).Post;

    // ---------- HTTP ----------

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken ct, TimeSpan? timeout = null)
    {
        using (request)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout ?? DefaultTimeout);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new WorkerApiException(0, "timeout", "O Worker demorou demais para responder.", ex);
            }
            catch (HttpRequestException ex)
            {
                throw new WorkerApiException(0, "unreachable",
                    "Não foi possível conectar ao Worker. Verifique a internet e a URL.", ex);
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false);
                    return body ?? throw new WorkerApiException((int)response.StatusCode, "empty_response",
                        "O Worker respondeu sem conteúdo.");
                }

                throw await ToExceptionAsync(response, ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task<WorkerApiException> ToExceptionAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        ErrorBody? error = null;
        try
        {
            error = (await response.Content.ReadFromJsonAsync<ErrorEnvelope>(Json, ct).ConfigureAwait(false))?.Error;
        }
        catch (JsonException)
        {
            // Resposta fora do formato do Worker (ex.: página de erro do Cloudflare).
        }

        var code = error?.Code ?? $"http_{status}";
        var message = error?.Message ?? (response.StatusCode == HttpStatusCode.RequestEntityTooLarge
            ? "Arquivo grande demais para o Worker."
            : $"O Worker respondeu com erro HTTP {status}.");
        return new WorkerApiException(status, code, message);
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Corpo com tamanho conhecido que informa o progresso enquanto é enviado.</summary>
    private sealed class ProgressContent(byte[] buffer, int length, Action<long> onProgress) : HttpContent
    {
        private const int Chunk = 256 * 1024;

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            await SerializeToStreamAsync(stream, context, CancellationToken.None).ConfigureAwait(false);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            for (var sent = 0; sent < length;)
            {
                var n = Math.Min(Chunk, length - sent);
                await stream.WriteAsync(buffer.AsMemory(sent, n), ct).ConfigureAwait(false);
                sent += n;
                onProgress(sent);
            }
        }

        protected override bool TryComputeLength(out long len)
        {
            len = length;
            return true;
        }
    }
}
