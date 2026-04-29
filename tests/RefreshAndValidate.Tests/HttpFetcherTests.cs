using System.Net;
using FluentAssertions;
using Xunit;

namespace Mithril.Tools.RefreshAndValidate.Tests;

public class HttpFetcherTests
{
    [Fact]
    public async Task Retries_once_on_transient_failure_then_succeeds()
    {
        var handler = new ScriptedHandler(new[]
        {
            new ScriptedResponse(throwException: true),
            new ScriptedResponse(content: "{\"ok\":true}"),
        });
        using var http = new HttpClient(handler);
        var fetcher = new HttpFetcher(http, "https://example.test/", "v1", TimeSpan.Zero);

        var body = await fetcher.FetchAsync("test.json");

        body.Should().Be("{\"ok\":true}");
        handler.CallCount.Should().Be(2, "transient failure should trigger exactly one retry");
    }

    [Fact]
    public async Task Throws_aggregated_exception_when_both_attempts_fail()
    {
        var handler = new ScriptedHandler(new[]
        {
            new ScriptedResponse(throwException: true),
            new ScriptedResponse(throwException: true),
        });
        using var http = new HttpClient(handler);
        var fetcher = new HttpFetcher(http, "https://example.test/", "v1", TimeSpan.Zero);

        var act = async () => await fetcher.FetchAsync("test.json");

        await act.Should().ThrowAsync<HttpRequestException>()
            .Where(ex => ex.Message.Contains("Both attempts"));
        handler.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task Single_success_on_first_attempt_does_not_retry()
    {
        var handler = new ScriptedHandler(new[]
        {
            new ScriptedResponse(content: "{\"ok\":true}"),
        });
        using var http = new HttpClient(handler);
        var fetcher = new HttpFetcher(http, "https://example.test/", "v1", TimeSpan.Zero);

        var body = await fetcher.FetchAsync("test.json");

        body.Should().Be("{\"ok\":true}");
        handler.CallCount.Should().Be(1);
    }

    private sealed record ScriptedResponse(string? content = null, bool throwException = false);

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly IReadOnlyList<ScriptedResponse> _responses;
        public int CallCount { get; private set; }

        public ScriptedHandler(IReadOnlyList<ScriptedResponse> responses) => _responses = responses;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var idx = CallCount++;
            if (idx >= _responses.Count)
                throw new InvalidOperationException($"unexpected extra fetch (call {idx + 1})");

            var script = _responses[idx];
            if (script.throwException)
                throw new HttpRequestException("simulated transient failure");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(script.content ?? ""),
            });
        }
    }
}
