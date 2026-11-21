using System.Net;
using System.Text;
using System.Text.Json;
using LlmBridge.Core;

namespace LlmBridge.Core.Tests;

/// <summary>Gerçek LM Studio yerine istekleri kaydeden ve hazır yanıt dönen sahte sunucu.</summary>
internal sealed class FakeServer : HttpMessageHandler
{
    public List<JsonDocument> ChatRequests { get; } = [];
    public Queue<Func<HttpResponseMessage>> ChatResponses { get; } = new();
    public string ModelsJson { get; set; } = """{"data":[{"id":"google/gemma-4-e4b","type":"vlm","state":"loaded","loaded_context_length":8192,"max_context_length":131072}]}""";
    public TimeSpan Delay { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("/api/v0/models"))
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ModelsJson) };
        }
        ChatRequests.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)));
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }
        return ChatResponses.Dequeue()();
    }

    public static HttpResponseMessage Sse(params string[] events)
    {
        var body = new StringBuilder();
        foreach (var e in events)
        {
            body.Append("data: ").Append(e).Append("\n\n");
        }
        body.Append("data: [DONE]\n\n");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToString(), Encoding.UTF8, "text/event-stream") };
    }

    public static string Delta(string? content = null, string? reasoning = null, string? finish = null) =>
        JsonSerializer.Serialize(new
        {
            choices = new[] { new { delta = new { content, reasoning_content = reasoning }, finish_reason = finish } },
        });

    public static string Usage(int prompt, int completion, int reasoning = 0) =>
        JsonSerializer.Serialize(new
        {
            choices = Array.Empty<object>(),
            usage = new { prompt_tokens = prompt, completion_tokens = completion, completion_tokens_details = new { reasoning_tokens = reasoning } },
        });
}

public class LmStudioClientTests
{
    private static LlmRequest Request(int maxTokens = 512) =>
        new("google/gemma-4-e4b", [new("user", "selam")], 0.7, maxTokens);

    [Fact]
    public async Task Streams_content_separately_from_reasoning_and_reads_usage()
    {
        var server = new FakeServer();
        server.ChatResponses.Enqueue(() => FakeServer.Sse(
            FakeServer.Delta(reasoning: "Kullanıcı selam verdi."),
            FakeServer.Delta(content: "Merhaba"),
            FakeServer.Delta(content: "!", finish: "stop"),
            FakeServer.Usage(12, 30, 25)));
        var client = new LmStudioClient(http: new HttpClient(server));
        var seen = new List<LlmProgress>();

        var reply = await client.ChatAsync(Request(), new SyncProgress<LlmProgress>(seen.Add));

        Assert.Equal("Merhaba!", reply.Content);
        Assert.Equal("stop", reply.FinishReason);
        Assert.Equal((12, 30, 25), (reply.PromptTokens, reply.CompletionTokens, reply.ReasoningTokens));
        Assert.False(reply.ThinkingExhausted);
        Assert.Equal("Merhaba!", seen[^1].ContentSoFar);
        var body = server.ChatRequests[0].RootElement;
        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.Equal(512, body.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task Thinking_that_eats_the_whole_budget_is_detected()
    {
        // Ölçülen durum: Gemma 4, max_tokens=512 ile 509 token düşünüp boş cevap döndü.
        var server = new FakeServer();
        server.ChatResponses.Enqueue(() => FakeServer.Sse(
            FakeServer.Delta(reasoning: "uzun düşünce"),
            FakeServer.Delta(finish: "length"),
            FakeServer.Usage(60, 512, 509)));
        var reply = await new LmStudioClient(http: new HttpClient(server)).ChatAsync(Request());
        Assert.Equal("", reply.Content);
        Assert.True(reply.ThinkingExhausted);
    }

    [Fact]
    public async Task Inline_think_tags_are_removed()
    {
        var server = new FakeServer();
        server.ChatResponses.Enqueue(() => FakeServer.Sse(FakeServer.Delta(content: "<think>plan</think>Cevap.", finish: "stop")));
        var reply = await new LmStudioClient(http: new HttpClient(server)).ChatAsync(Request());
        Assert.Equal("Cevap.", reply.Content);
    }

    [Fact]
    public async Task Context_overflow_error_is_explained_in_turkish()
    {
        var server = new FakeServer();
        server.ChatResponses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":"Trying to keep the first 9000 tokens when context the overflows. However, the model is loaded with context length of only 8192 tokens"}"""),
        });
        var ex = await Assert.ThrowsAsync<LlmException>(() => new LmStudioClient(http: new HttpClient(server)).ChatAsync(Request()));
        Assert.Contains("bağlam uzunluğunu", ex.Message);
    }

    [Fact]
    public async Task Silent_server_hits_inactivity_timeout_with_clear_message()
    {
        var server = new FakeServer { Delay = TimeSpan.FromSeconds(5) };
        server.ChatResponses.Enqueue(() => FakeServer.Sse(FakeServer.Delta(content: "geç")));
        var client = new LmStudioClient(http: new HttpClient(server)) { InactivityTimeout = TimeSpan.FromMilliseconds(200) };
        var ex = await Assert.ThrowsAsync<LlmException>(() => client.ChatAsync(Request()));
        Assert.Contains("saniye", ex.Message);
    }

    [Fact]
    public async Task Requests_from_the_same_process_never_run_in_parallel()
    {
        var server = new ConcurrencyServer();
        var client = new LmStudioClient(http: new HttpClient(server));
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => client.ChatAsync(Request())));
        Assert.Equal(1, server.MaxConcurrent);
    }

    private sealed class ConcurrencyServer : HttpMessageHandler
    {
        private int _current;
        public int MaxConcurrent;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref _current);
            MaxConcurrent = Math.Max(MaxConcurrent, now);
            await Task.Delay(50, cancellationToken);
            Interlocked.Decrement(ref _current);
            return FakeServer.Sse(FakeServer.Delta(content: "ok", finish: "stop"));
        }
    }
}

/// <summary>Progress&lt;T&gt; geri çağrıyı başka iş parçacığına atar; testte anında çalışsın.</summary>
internal sealed class SyncProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}
