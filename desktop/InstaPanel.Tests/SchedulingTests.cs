using System.Net;
using System.Text;
using InstaPanel.Cloud;
using InstaPanel.Core;

namespace InstaPanel.Tests;

public class SchedulingTests
{
    private sealed class Recorder(string response) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string PathAndQuery, string? Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request.Method, request.RequestUri!.PathAndQuery, request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }

    private const string PostJson = """
        {"post":{"id":"11111111-2222-3333-4444-555555555555","type":"IMAGE","caption":"Oi","options":{},
         "scheduled_at":"2026-10-25T21:00:00.000Z","state":"SCHEDULED","attempts":0,"error":null,"permalink":null,
         "ig_media_id":null,"created_at":"2026-10-22T12:00:00.000Z","updated_at":"2026-10-22T12:00:00.000Z","media":[]}}
        """;

    [Fact]
    public void Week_starts_on_sunday()
    {
        var (start, end) = CalendarMath.WeekOf(new DateTime(2026, 10, 22)); // quinta
        Assert.Equal(new DateTime(2026, 10, 18), start);                    // domingo
        Assert.Equal(DayOfWeek.Sunday, start.DayOfWeek);
        Assert.Equal(7, (end - start).Days);
    }

    [Fact]
    public void Month_grid_has_six_weeks_covering_the_month()
    {
        var (start, end) = CalendarMath.MonthGridOf(new DateTime(2026, 10, 22));
        Assert.Equal(new DateTime(2026, 9, 27), start); // 01/10/2026 é quinta
        Assert.Equal(42, (end - start).Days);
        Assert.True(end > new DateTime(2026, 10, 31));
    }

    [Fact]
    public void Moving_to_another_day_keeps_local_time_across_offsets()
    {
        // Fuso com horário de verão: o horário de parede se mantém mesmo mudando o offset.
        var ny = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var at = new DateTimeOffset(2026, 10, 30, 18, 0, 0, ny.GetUtcOffset(new DateTime(2026, 10, 30, 18, 0, 0)));
        var moved = CalendarMath.MoveToDay(at, new DateTime(2026, 11, 3), ny);
        Assert.Equal(new DateTime(2026, 11, 3, 18, 0, 0), moved.DateTime);
        Assert.NotEqual(at.Offset, moved.Offset);
    }

    [Fact]
    public void Editable_states_match_worker()
    {
        Assert.True(PostStates.IsEditable(PostStates.Scheduled));
        Assert.True(PostStates.IsEditable(PostStates.Ready));
        Assert.True(PostStates.IsEditable(PostStates.Failed));
        Assert.False(PostStates.IsEditable(PostStates.Publishing));
        Assert.False(PostStates.IsEditable(PostStates.Published));
        Assert.False(PostStates.IsEditable(PostStates.Canceled));
        Assert.True(PostStates.IsFinal(PostStates.Canceled));
    }

    [Fact]
    public async Task List_query_sends_range_states_and_cursor_in_utc()
    {
        var handler = new Recorder("""{"posts":[]}""");
        using var client = new WorkerClient(new Uri("https://w.dev"), "k", handler);

        await client.ListPostsAsync(new PostQuery
        {
            From = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(-3)),
            To = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(-3)),
            States = [PostStates.Scheduled, PostStates.Failed],
            Limit = 200,
        });

        var q = Uri.UnescapeDataString(handler.Calls[0].PathAndQuery);
        Assert.Contains("limit=200", q);
        Assert.Contains("from=2026-10-01T03:00:00.0000000Z", q);
        Assert.Contains("to=2026-11-01T03:00:00.0000000Z", q);
        Assert.Contains("state=SCHEDULED,FAILED", q);
    }

    [Fact]
    public async Task Update_sends_only_changed_fields_with_patch()
    {
        var handler = new Recorder(PostJson);
        using var client = new WorkerClient(new Uri("https://w.dev"), "k", handler);

        await client.UpdatePostAsync("11111111-2222-3333-4444-555555555555",
            new UpdatePostRequest(ScheduledAt: new DateTimeOffset(2026, 10, 25, 18, 0, 0, TimeSpan.FromHours(-3))));
        await client.UpdatePostAsync("11111111-2222-3333-4444-555555555555", new UpdatePostRequest(Caption: ""));

        Assert.Equal(HttpMethod.Patch, handler.Calls[0].Method);
        Assert.Equal("""{"scheduled_at":"2026-10-25T18:00:00-03:00"}""", handler.Calls[0].Body);
        Assert.Equal("""{"caption":""}""", handler.Calls[1].Body); // legenda vazia = apagar
    }

    [Fact]
    public async Task Cancel_uses_delete_and_parses_post()
    {
        var handler = new Recorder(PostJson.Replace("\"SCHEDULED\"", "\"CANCELED\""));
        using var client = new WorkerClient(new Uri("https://w.dev"), "k", handler);

        var post = await client.CancelPostAsync("11111111-2222-3333-4444-555555555555");

        Assert.Equal(HttpMethod.Delete, handler.Calls[0].Method);
        Assert.Equal("/api/posts/11111111-2222-3333-4444-555555555555", handler.Calls[0].PathAndQuery);
        Assert.Equal(PostStates.Canceled, post.State);
    }
}
