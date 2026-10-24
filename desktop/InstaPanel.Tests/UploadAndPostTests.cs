using System.Net;
using System.Text;
using System.Text.Json;
using InstaPanel.Cloud;

namespace InstaPanel.Tests;

public sealed class UploadAndPostTests : IDisposable
{
    private readonly string _file = Path.GetTempFileName();

    public void Dispose() => File.Delete(_file);

    private sealed class Recorder(Func<HttpRequestMessage, byte[]?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, byte[]? Body, long? Length)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(ct);
            Calls.Add((request.Method, request.RequestUri!.PathAndQuery, body, request.Content?.Headers.ContentLength));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Upload_sends_parts_in_order_and_completes_with_etags()
    {
        // 25 bytes com partes de 10 → 3 partes (10, 10, 5).
        var data = Enumerable.Range(0, 25).Select(i => (byte)i).ToArray();
        await File.WriteAllBytesAsync(_file, data);

        var handler = new Recorder((req, body) => req.RequestUri!.AbsolutePath switch
        {
            "/api/uploads" => Json("""{"key":"vid/k.mp4","upload_id":"u1","part_size":10,"part_count":3}""", HttpStatusCode.Created),
            "/api/uploads/part" => Json($$"""{"part_number":{{req.RequestUri.Query.Split("part=")[1]}},"etag":"e{{body![0]}}"}"""),
            "/api/uploads/complete" => Json("""{"key":"vid/k.mp4","size_bytes":25,"kind":"VIDEO"}"""),
            _ => Json("{}", HttpStatusCode.NotFound),
        });
        using var client = new WorkerClient(new Uri("https://w.dev"), "k", handler);
        var progress = new List<UploadProgress>();

        var key = await client.UploadFileAsync(_file, "video/mp4", new SyncProgress(progress.Add));

        Assert.Equal("vid/k.mp4", key);
        var parts = handler.Calls.Where(c => c.Path.StartsWith("/api/uploads/part")).ToList();
        Assert.Equal(3, parts.Count);
        Assert.Equal([10L, 10L, 5L], parts.Select(p => p.Length!.Value));
        Assert.Equal(data, parts.SelectMany(p => p.Body!).ToArray());
        Assert.Contains("part=3", parts[2].Path);

        var create = JsonDocument.Parse(handler.Calls[0].Body!).RootElement;
        Assert.Equal("video/mp4", create.GetProperty("content_type").GetString());
        Assert.Equal(25, create.GetProperty("size_bytes").GetInt64());

        var complete = JsonDocument.Parse(handler.Calls[^1].Body!).RootElement;
        Assert.Equal(["e0", "e10", "e20"], complete.GetProperty("parts").EnumerateArray().Select(p => p.GetProperty("etag").GetString()!));
        Assert.Equal(25, progress[^1].BytesSent);
    }

    [Fact]
    public async Task Failed_upload_is_deleted_on_the_worker()
    {
        await File.WriteAllBytesAsync(_file, new byte[5]);
        var handler = new Recorder((req, _) => req.RequestUri!.AbsolutePath switch
        {
            "/api/uploads" when req.Method == HttpMethod.Post => Json("""{"key":"img/k.jpg","upload_id":"u1","part_size":10,"part_count":1}""", HttpStatusCode.Created),
            "/api/uploads/part" => Json("""{"error":{"code":"invalid_part_size","message":"tamanho errado"}}""", HttpStatusCode.BadRequest),
            _ => Json("""{"ok":true}"""),
        });
        using var client = new WorkerClient(new Uri("https://w.dev"), "k", handler);

        var ex = await Assert.ThrowsAsync<WorkerApiException>(() => client.UploadFileAsync(_file, "image/jpeg"));

        Assert.Equal("invalid_part_size", ex.Code);
        Assert.Contains(handler.Calls, c => c.Method == HttpMethod.Delete && c.Path == "/api/uploads?key=img%2Fk.jpg");
    }

    [Fact]
    public async Task Part_is_retried_after_server_error()
    {
        await File.WriteAllBytesAsync(_file, new byte[5]);
        var failures = 1;
        var handler = new Recorder((req, _) => req.RequestUri!.AbsolutePath switch
        {
            "/api/uploads" => Json("""{"key":"img/k.jpg","upload_id":"u1","part_size":10,"part_count":1}""", HttpStatusCode.Created),
            "/api/uploads/part" when failures-- > 0 => Json("{}", HttpStatusCode.BadGateway),
            "/api/uploads/part" => Json("""{"part_number":1,"etag":"ok"}"""),
            _ => Json("""{"key":"img/k.jpg"}"""),
        });
        using var client = new WorkerClient(new Uri("https://w.dev"), "k", handler);

        Assert.Equal("img/k.jpg", await client.UploadFileAsync(_file, "image/jpeg"));
        Assert.Equal(2, handler.Calls.Count(c => c.Path.StartsWith("/api/uploads/part")));
    }

    [Fact]
    public async Task Create_post_sends_snake_case_without_nulls_and_parses_response()
    {
        const string response = """
            {"post":{"id":"0b0f4c2e-1111-2222-3333-444455556666","type":"REELS","caption":"Oi","options":{"share_to_feed":false},
             "scheduled_at":"2026-10-21T12:00:00.000Z","state":"SCHEDULED","attempts":0,"error":null,"permalink":null,
             "ig_media_id":null,"created_at":"2026-10-21T12:00:00.000Z","updated_at":"2026-10-21T12:00:00.000Z",
             "media":[{"key":"vid/k.mp4","kind":"VIDEO"}]}}
            """;
        var handler = new Recorder((_, _) => Json(response, HttpStatusCode.Created));
        using var client = new WorkerClient(new Uri("https://w.dev"), "k", handler);

        var post = await client.CreatePostAsync(new CreatePostRequest("REELS", "Oi", [new PostMediaKey("vid/k.mp4")], new PostOptions(ShareToFeed: false)));

        Assert.Equal("""{"type":"REELS","caption":"Oi","media":[{"key":"vid/k.mp4"}],"options":{"share_to_feed":false}}""",
            Encoding.UTF8.GetString(handler.Calls[0].Body!));
        Assert.Equal(PostStates.Scheduled, post.State);
        Assert.False(post.Options!.ShareToFeed);
        Assert.Equal("VIDEO", Assert.Single(post.Media).Kind);
    }

    private sealed class SyncProgress(Action<UploadProgress> report) : IProgress<UploadProgress>
    {
        public void Report(UploadProgress value) => report(value);
    }
}
