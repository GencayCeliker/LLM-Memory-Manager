namespace LlmBridge.Core;

public sealed record ChatTurnOptions(
    string? Model,
    double Temperature,
    int FallbackContextLength,
    int MaxHistoryMessages);

public sealed record ChatTurnResult(
    LlmReply Reply,
    ContextPlan Plan,
    string ModelId,
    IReadOnlyList<string> Warnings,
    bool Retried)
{
    /// <summary>Arayüzde gösterilecek kısa bütçe özeti.</summary>
    public string Summary
    {
        get
        {
            var prompt = Reply.PromptTokens ?? Plan.EstimatedPromptTokens;
            var parts = new List<string>
            {
                $"bağlam {prompt}/{Plan.ContextLength} token",
                $"yanıt {Reply.CompletionTokens ?? 0}",
            };
            if (Reply.ReasoningTokens is > 0)
            {
                parts.Add($"düşünme {Reply.ReasoningTokens}");
            }
            if (Reply.TokensPerSecond > 0)
            {
                parts.Add($"{Reply.TokensPerSecond:0} token/sn");
            }
            if (Plan.DroppedMessages > 0)
            {
                parts.Add($"{Plan.DroppedMessages} eski mesaj bağlam dışı");
            }
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// Bir sohbet turunu baştan sona yürütür: modeli ve gerçek bağlam uzunluğunu LM Studio'dan okur,
/// geçmişi token bütçesine sığdırır, yanıtı akışla alır, düşünmeye bütçe yetmediyse bir kez genişletip yeniden dener.
/// </summary>
public sealed class ChatPipeline(LmStudioClient client)
{
    private static readonly TimeSpan ModelCacheTtl = TimeSpan.FromSeconds(10);
    private IReadOnlyList<ModelInfo>? _models;
    private DateTime _modelsAt;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TokenScale> _scales = new(StringComparer.OrdinalIgnoreCase);

    public LmStudioClient Client => client;

    /// <summary>Modelin token tahmini düzeltme çarpanı (1.0 = temel tahmin doğru).</summary>
    public TokenScale ScaleFor(string modelId) => _scales.GetOrAdd(modelId, _ => new TokenScale());

    public async Task<IReadOnlyList<ModelInfo>> GetModelsAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (force || _models is null || DateTime.UtcNow - _modelsAt > ModelCacheTtl)
        {
            _models = await client.GetModelsAsync(cancellationToken);
            _modelsAt = DateTime.UtcNow;
        }
        return _models;
    }

    public async Task<ChatTurnResult> RunAsync(
        string systemPrompt,
        IReadOnlyList<ChatMessageDto> history,
        string userMessage,
        ChatTurnOptions options,
        IProgress<LlmProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var models = await GetModelsAsync(cancellationToken: cancellationToken);
        var (model, modelWarnings) = ResolveModel(options.Model, models);
        warnings.AddRange(modelWarnings);

        var modelId = model?.Id ?? options.Model ?? "";
        var scale = ScaleFor(modelId);
        var contextLength = model?.LoadedContextLength ?? options.FallbackContextLength;
        var plan = ContextBuilder.Build(systemPrompt, history, new ChatMessageDto("user", userMessage),
            new ContextOptions(contextLength, Math.Max(1, options.MaxHistoryMessages), TokenScale: scale.Value));
        if (plan.CurrentMessageTrimmed)
        {
            warnings.Add("Mesajınız bağlama sığmadığı için ortası kısaltılarak gönderildi.");
        }

        var reply = await client.ChatAsync(
            new LlmRequest(modelId, plan.Messages, options.Temperature, plan.MaxReplyTokens), progress, cancellationToken);
        if (reply.PromptTokens is int actualPrompt)
        {
            scale.Observe(plan.UnscaledPromptTokens, actualPrompt);
        }

        var retried = false;
        if (reply.ThinkingExhausted && plan.RemainingForReply >= plan.MaxReplyTokens * 3 / 2)
        {
            // Düşünen model (ör. Gemma 4) cevabı yazamadan bütçeyi bitirdi: kalan bağlamın tamamıyla bir kez daha.
            retried = true;
            reply = await client.ChatAsync(
                new LlmRequest(modelId, plan.Messages, options.Temperature, plan.RemainingForReply), progress, cancellationToken);
        }
        if (reply.ThinkingExhausted)
        {
            warnings.Add("Model cevap yazmadan önce düşünmeye tüm token bütçesini harcadı. Soruyu daraltın, " +
                         "eski mesajları azaltın ya da düşünmeyen bir model (ör. qwen3-vl-8b-instruct) seçin.");
        }
        else if (reply.FinishReason == "length")
        {
            warnings.Add("Yanıt token sınırında kesildi.");
        }

        return new ChatTurnResult(reply, plan, modelId, warnings, retried);
    }

    /// <summary>Ayarlardaki model adını LM Studio'daki modellerle eşleştirir ve GPU belleği risklerini bildirir.</summary>
    public static (ModelInfo? Model, List<string> Warnings) ResolveModel(string? requested, IReadOnlyList<ModelInfo> models)
    {
        var warnings = new List<string>();
        var loaded = models.Where(m => m.IsChatModel && m.IsLoaded).ToList();
        if (loaded.Count > 1)
        {
            warnings.Add($"LM Studio'da {loaded.Count} model aynı anda yüklü ({string.Join(", ", loaded.Select(m => m.Id))}). " +
                         "8 GB GPU belleğine sığmazlar; yanıtlar ~6 kat yavaşlar. Kullanmadığınızı kaldırın.");
        }

        ModelInfo? match = null;
        if (!string.IsNullOrWhiteSpace(requested))
        {
            match = models.FirstOrDefault(m => string.Equals(m.Id, requested, StringComparison.OrdinalIgnoreCase))
                    ?? models.FirstOrDefault(m => m.IsChatModel && m.Id.Contains(requested, StringComparison.OrdinalIgnoreCase));
        }

        if (match is null)
        {
            var fallback = loaded.FirstOrDefault();
            if (fallback is null)
            {
                if (models.Count > 0 && models.All(m => m.State == "unknown"))
                {
                    return (null, warnings);   // yükleme durumu bilinmeyen sunucu: istenen adla dene
                }
                throw new LlmException("LM Studio'da yüklü bir sohbet modeli yok. LM Studio'da bir model yükleyin.");
            }
            if (!string.IsNullOrWhiteSpace(requested))
            {
                warnings.Add($"Ayarlardaki model '{requested}' bulunamadı; yüklü model '{fallback.Id}' kullanıldı.");
            }
            return (fallback, warnings);
        }

        if (!match.IsLoaded && match.State != "unknown" && loaded.Count > 0)
        {
            warnings.Add($"'{match.Id}' yüklü değil; LM Studio onu yüklerken '{loaded[0].Id}' de bellekte kalırsa " +
                         "GPU belleği taşar. Ayarlarda yüklü modeli seçmeniz daha hızlı olur.");
        }
        return (match, warnings);
    }
}
