namespace LlmBridge.Core;

/// <summary>Modele gönderilen tek bir sohbet mesajı (OpenAI biçimi: system / user / assistant).</summary>
public sealed record ChatMessageDto(string Role, string Content);

/// <summary>Yerel sunucudaki bir modelin durumu (LM Studio <c>/api/v0/models</c>).</summary>
public sealed record ModelInfo(
    string Id,
    string Type,
    string State,
    int? LoadedContextLength,
    int? MaxContextLength)
{
    public bool IsLoaded => string.Equals(State, "loaded", StringComparison.OrdinalIgnoreCase);

    /// <summary>Sohbet edebilen model mi (embedding modelleri hariç).</summary>
    public bool IsChatModel => Type is "llm" or "vlm";
}

public sealed record LlmRequest(string Model, IReadOnlyList<ChatMessageDto> Messages, double Temperature, int MaxTokens);

/// <summary>Akış sırasında arayüze bildirilen ilerleme.</summary>
public sealed record LlmProgress(string ContentSoFar, int ContentChunks, int ReasoningChunks);

public sealed record LlmReply(
    string Content,
    string? FinishReason,
    int? PromptTokens,
    int? CompletionTokens,
    int? ReasoningTokens,
    TimeSpan Elapsed,
    TimeSpan TimeToFirstToken)
{
    /// <summary>Model yanıt yazmadan önce düşünmeye tüm token bütçesini harcadı (cevap boş ya da yarım).</summary>
    public bool ThinkingExhausted => FinishReason == "length" && (ReasoningTokens ?? 0) > 0 &&
                                     Content.Length < 40;

    public double TokensPerSecond
    {
        get
        {
            var generation = (Elapsed - TimeToFirstToken).TotalSeconds;
            return CompletionTokens is > 0 && generation > 0.05 ? CompletionTokens.Value / generation : 0;
        }
    }
}

/// <summary>Kullanıcıya gösterilebilecek Türkçe açıklama taşıyan hata.</summary>
public sealed class LlmException(string message, Exception? inner = null) : Exception(message, inner);
