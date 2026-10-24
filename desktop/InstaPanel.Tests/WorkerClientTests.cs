using System.Net;
using System.Text;
using InstaPanel.Cloud;

namespace InstaPanel.Tests;

public class WorkerClientTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request, body));
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private const string AccountJson = """
        {
          "account": {
            "ig_user_id": "1784",
            "username": "obryansenna",
            "account_type": "MEDIA_CREATOR",
            "profile_picture_url": "https://example.com/a.jpg",
            "token": {
              "expires_at": "2026-12-20T10:11:17.000Z",
              "expiry_known": false,
              "days_left": 59,
              "obtained_at": "2026-10-21T10:11:17.000Z",
              "last_error": null
            }
          },
          "publishing_limit": { "quota_usage": 3, "quota_total": 100, "quota_duration": 86400 }
        }
        """;

    [Fact]
    public async Task GetAccount_parses_snake_case_and_sends_bearer_key()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, AccountJson));
        using var client = new WorkerClient(new Uri("https://w.example.dev"), "minha-chave", handler);

        var status = await client.GetAccountAsync();

        Assert.Equal("obryansenna", status.Account.Username);
        Assert.Equal(59, status.Account.Token.DaysLeft);
        Assert.False(status.Account.Token.ExpiryKnown);
        Assert.Equal(3, status.PublishingLimit.QuotaUsage);
        Assert.Equal(100, status.PublishingLimit.QuotaTotal);

        var req = Assert.Single(handler.Calls).Request;
        Assert.Equal("https://w.example.dev/api/account", req.RequestUri!.ToString());
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal("minha-chave", req.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task Base_url_with_path_keeps_its_last_segment()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, """{"ok":true,"version":"0.1.0"}"""));
        using var client = new WorkerClient(new Uri("https://example.dev/instapanel"), "k", handler);

        await client.GetHealthAsync();

        Assert.Equal("https://example.dev/instapanel/api/health", handler.Calls[0].Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task Publishing_limit_error_is_exposed_instead_of_failing()
    {
        const string json = """
            {
              "account": {
                "ig_user_id": "1", "username": "u", "account_type": null, "profile_picture_url": null,
                "token": { "expires_at": "2026-12-20T00:00:00Z", "expiry_known": true, "days_left": 5,
                           "obtained_at": "2026-10-21T00:00:00Z", "last_error": null }
              },
              "publishing_limit": { "error": { "message": "Rate limited", "code": 4 } }
            }
            """;
        using var client = new WorkerClient(new Uri("https://w.dev"), "k", new FakeHandler(_ => Json(HttpStatusCode.OK, json)));

        var status = await client.GetAccountAsync();

        Assert.Equal("Rate limited", status.PublishingLimit.Error?.Message);
        Assert.Equal(4, status.PublishingLimit.Error?.Code);
    }

    [Fact]
    public async Task Worker_error_envelope_becomes_WorkerApiException()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.NotFound,
            """{"error":{"code":"not_connected","message":"Nenhuma conta Instagram conectada."}}"""));
        using var client = new WorkerClient(new Uri("https://w.dev"), "k", handler);

        var ex = await Assert.ThrowsAsync<WorkerApiException>(() => client.GetAccountAsync());

        Assert.Equal(404, ex.StatusCode);
        Assert.True(ex.IsNotConnected);
        Assert.Equal("Nenhuma conta Instagram conectada.", ex.Message);
    }

    [Fact]
    public async Task Non_json_error_still_maps_to_status_code()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html>cloudflare</html>", Encoding.UTF8, "text/html"),
        });
        using var client = new WorkerClient(new Uri("https://w.dev"), "k", handler);

        var ex = await Assert.ThrowsAsync<WorkerApiException>(() => client.GetHealthAsync());

        Assert.Equal(502, ex.StatusCode);
        Assert.Equal("http_502", ex.Code);
    }

    [Fact]
    public async Task Network_failure_is_reported_as_unreachable()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("DNS"));
        using var client = new WorkerClient(new Uri("https://w.dev"), "k", handler);

        var ex = await Assert.ThrowsAsync<WorkerApiException>(() => client.GetHealthAsync());

        Assert.Equal(0, ex.StatusCode);
        Assert.Equal("unreachable", ex.Code);
    }

    [Fact]
    public async Task ConnectToken_puts_trimmed_token_as_snake_case_json()
    {
        const string json = """
            {
              "account": {
                "ig_user_id": "1784", "username": "obryansenna", "account_type": "MEDIA_CREATOR", "profile_picture_url": null,
                "token": { "expires_at": "2026-12-20T00:00:00Z", "expiry_known": true, "days_left": 60,
                           "obtained_at": "2026-10-21T00:00:00Z", "last_error": null }
              }
            }
            """;
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, json));
        using var client = new WorkerClient(new Uri("https://w.dev"), "k", handler);

        var account = await client.ConnectTokenAsync("  IGAAtoken123  ");

        Assert.Equal("obryansenna", account.Username);
        var (req, body) = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Put, req.Method);
        Assert.Equal("/api/account/token", req.RequestUri!.AbsolutePath);
        Assert.Equal("""{"access_token":"IGAAtoken123"}""", body);
    }

    [Fact]
    public async Task RequestAiAnalysis_posts_snake_case_and_parses_result()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, """
            { "analysis": "## Diagnóstico\nok", "model": "claude-opus-5-5", "stop_reason": "end_turn",
              "truncated": false, "usage": { "input_tokens": 1200, "output_tokens": 800 } }
            """));
        using var client = new WorkerClient(new Uri("https://w.example.dev"), "minha-chave", handler);

        var result = await client.RequestAiAnalysisAsync(new AiAnalysisRequest(
            "vender um produto", "# Relatório", [new AiPostInput("Reel", "2026-10-20 14:00", 10, 2, "oi")], []));

        var (req, body) = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("/api/insights/ai", req.RequestUri!.AbsolutePath);
        Assert.Equal("# Relatório", System.Text.Json.JsonDocument.Parse(body!).RootElement.GetProperty("report_markdown").GetString());
        Assert.Contains("\"likes\":10", body);
        Assert.Equal("## Diagnóstico\nok", result.Analysis);
        Assert.Equal(800, result.Usage.OutputTokens);
    }

    [Fact]
    public async Task RequestAiAnalysis_surfaces_not_configured()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.Conflict,
            """{ "error": { "code": "ai_not_configured", "message": "Cadastre a chave" } }"""));
        using var client = new WorkerClient(new Uri("https://w.example.dev"), "minha-chave", handler);

        var ex = await Assert.ThrowsAsync<WorkerApiException>(() =>
            client.RequestAiAnalysisAsync(new AiAnalysisRequest("crescer", "# R", [], [])));
        Assert.Equal("ai_not_configured", ex.Code);
    }

    [Fact]
    public async Task GetInsights_parses_profile_and_media_with_hidden_likes()
    {
        const string json = """
            {
              "profile": {
                "ig_user_id": "1784", "username": "obryansenna", "name": "Bryan | Fotografia",
                "biography": "Fotos de casamento em SP", "website": null, "account_type": "MEDIA_CREATOR",
                "profile_picture_url": null, "followers_count": 1840, "follows_count": 912, "media_count": 36
              },
              "media": [
                { "id": "1", "caption": "7 erros", "media_type": "CAROUSEL_ALBUM", "media_product_type": "FEED",
                  "timestamp": "2026-10-20T14:00:00.000Z", "like_count": 140, "comments_count": 12,
                  "permalink": "https://www.instagram.com/p/X/", "thumbnail_url": "https://cdn/x.jpg" },
                { "id": "2", "caption": null, "media_type": "VIDEO", "media_product_type": "REELS",
                  "timestamp": "2026-10-18T10:00:00.000Z", "like_count": null, "comments_count": 3,
                  "permalink": null, "thumbnail_url": null }
              ],
              "fetched_at": "2026-10-23T12:00:00.000Z"
            }
            """;
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, json));
        using var client = new WorkerClient(new Uri("https://w.example.dev"), "minha-chave", handler);

        var insights = await client.GetInsightsAsync(80);

        Assert.Equal("/api/insights?limit=80", handler.Calls[0].Request.RequestUri!.PathAndQuery);
        Assert.Equal(1840, insights.Profile.FollowersCount);
        Assert.Null(insights.Profile.Website);
        Assert.Equal(2, insights.Media.Count);
        Assert.Equal(140, insights.Media[0].LikeCount);
        Assert.Null(insights.Media[1].LikeCount);
        Assert.Equal("REELS", insights.Media[1].MediaProductType);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 14, 0, 0, TimeSpan.Zero), insights.Media[0].Timestamp);
    }
}
