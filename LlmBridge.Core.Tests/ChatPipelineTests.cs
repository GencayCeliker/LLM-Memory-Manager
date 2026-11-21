using LlmBridge.Core;

namespace LlmBridge.Core.Tests;

public class ChatPipelineTests
{
    private static ChatTurnOptions Options(string? model = "google/gemma-4-e4b") => new(model, 0.7, 4096, 10);

    [Fact]
    public async Task Uses_the_context_length_the_model_is_actually_loaded_with()
    {
        var server = new FakeServer();
        server.ChatResponses.Enqueue(() => FakeServer.Sse(FakeServer.Delta(content: "Tamam.", finish: "stop"), FakeServer.Usage(40, 3)));
        var pipeline = new ChatPipeline(new LmStudioClient(http: new HttpClient(server)));

        var result = await pipeline.RunAsync("x", [], "selam", Options());

        Assert.Equal(8192, result.Plan.ContextLength);   // ayardaki 4096 değil, LM Studio'daki 8192
        Assert.Equal("Tamam.", result.Reply.Content);
        Assert.Empty(result.Warnings);
        Assert.Contains("bağlam 40/8192", result.Summary);
    }

    [Fact]
    public async Task Exhausted_thinking_is_retried_once_with_the_remaining_budget()
    {
        var server = new FakeServer();
        server.ChatResponses.Enqueue(() => FakeServer.Sse(FakeServer.Delta(reasoning: "..."), FakeServer.Delta(finish: "length"), FakeServer.Usage(50, 2048, 2040)));
        server.ChatResponses.Enqueue(() => FakeServer.Sse(FakeServer.Delta(reasoning: "..."), FakeServer.Delta(content: "Sonunda cevap.", finish: "stop"), FakeServer.Usage(50, 2600, 2500)));
        var pipeline = new ChatPipeline(new LmStudioClient(http: new HttpClient(server)));

        var result = await pipeline.RunAsync("x", [], "zor soru", Options());

        Assert.True(result.Retried);
        Assert.Equal("Sonunda cevap.", result.Reply.Content);
        var first = server.ChatRequests[0].RootElement.GetProperty("max_tokens").GetInt32();
        var second = server.ChatRequests[1].RootElement.GetProperty("max_tokens").GetInt32();
        Assert.True(second > first * 3 / 2, $"{first} -> {second}");
    }

    [Fact]
    public async Task Real_prompt_tokens_teach_the_estimator_for_the_next_turn()
    {
        var server = new FakeServer();
        var history = Enumerable.Range(0, 30)
            .SelectMany(i => new ChatMessageDto[] { new("user", new string('ş', 1200)), new("assistant", new string('ğ', 1200)) })
            .ToList();
        // Sunucu, gönderilen istemin tahminin 2 katı token tuttuğunu bildirir.
        server.ChatResponses.Enqueue(() =>
        {
            var sent = server.ChatRequests[^1].RootElement.GetProperty("messages").EnumerateArray()
                .Sum(m => TokenEstimator.Estimate(m.GetProperty("content").GetString()) + TokenEstimator.MessageOverhead);
            return FakeServer.Sse(FakeServer.Delta(content: "ok", finish: "stop"), FakeServer.Usage(sent * 2, 2));
        });
        server.ChatResponses.Enqueue(() => FakeServer.Sse(FakeServer.Delta(content: "ok", finish: "stop"), FakeServer.Usage(100, 2)));
        var pipeline = new ChatPipeline(new LmStudioClient(http: new HttpClient(server)));

        var first = await pipeline.RunAsync("x", history, "soru", Options());
        Assert.True(pipeline.ScaleFor("google/gemma-4-e4b").Value > 1.9);
        var second = await pipeline.RunAsync("x", history, "soru", Options());

        Assert.True(second.Plan.Messages.Count < first.Plan.Messages.Count);   // daha az geçmiş: gerçek boyut sığsın diye
        Assert.True(second.Plan.EstimatedPromptTokens + second.Plan.MaxReplyTokens <= second.Plan.ContextLength);
    }

    [Fact]
    public void Two_loaded_models_produce_a_gpu_memory_warning()
    {
        var models = new List<ModelInfo>
        {
            new("qwen2.5-coder-7b-instruct", "llm", "loaded", 8192, 32768),
            new("qwen/qwen2.5-vl-7b", "vlm", "loaded", 4096, 128000),
        };
        var (model, warnings) = ChatPipeline.ResolveModel("qwen2.5-coder-7b-instruct", models);
        Assert.Equal("qwen2.5-coder-7b-instruct", model!.Id);
        Assert.Contains(warnings, w => w.Contains("2 model aynı anda yüklü"));
    }

    [Fact]
    public void Unknown_model_name_falls_back_to_the_loaded_model_with_a_warning()
    {
        var models = new List<ModelInfo> { new("qwen3-vl-8b-instruct", "vlm", "loaded", 4096, 262144) };
        var (model, warnings) = ChatPipeline.ResolveModel("stefancosma/qwen2.5-coder-7b-instruct", models);
        Assert.Equal("qwen3-vl-8b-instruct", model!.Id);
        Assert.Contains(warnings, w => w.Contains("bulunamadı"));
    }

    [Fact]
    public void No_loaded_model_is_a_clear_error()
    {
        var models = new List<ModelInfo> { new("google/gemma-4-e4b", "vlm", "not-loaded", null, 131072) };
        Assert.Throws<LlmException>(() => ChatPipeline.ResolveModel("yok", models));
    }

    [Fact]
    public void Embedding_models_are_not_chat_models()
    {
        var models = new List<ModelInfo>
        {
            new("text-embedding-nomic-embed-text-v1.5", "embeddings", "loaded", 2048, 2048),
            new("google/gemma-4-e4b", "vlm", "loaded", 8192, 131072),
        };
        var (model, warnings) = ChatPipeline.ResolveModel(null, models);
        Assert.Equal("google/gemma-4-e4b", model!.Id);
        Assert.Empty(warnings);
    }
}
