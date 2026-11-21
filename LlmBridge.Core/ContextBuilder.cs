using System.Text;

namespace LlmBridge.Core;

public sealed record ContextOptions(
    int ContextLength,
    int MaxHistoryMessages = 50,
    int? ReplyReserve = null,
    int? ReplyCap = null,
    double TokenScale = 1.0);

/// <summary>Modele gönderilecek mesajlar ve bütçe dökümü.</summary>
public sealed record ContextPlan(
    IReadOnlyList<ChatMessageDto> Messages,
    int EstimatedPromptTokens,
    int MaxReplyTokens,
    int ContextLength,
    int DroppedMessages,
    bool CurrentMessageTrimmed,
    int UnscaledPromptTokens = 0)
{
    /// <summary>Bağlamın en az yarısı yanıta kalacak şekilde düşünme payı genişletilebilir mi.</summary>
    public int RemainingForReply => ContextLength - EstimatedPromptTokens - ContextBuilder.SafetyMargin(ContextLength);
}

/// <summary>
/// Sohbet geçmişini token bütçesine göre kırpar. LM Studio bağlam dolunca hata vermeden geçmişin
/// ortasını sessizce siler (ölçüldü: 3911 token'lık istem 1919'a düştü); bu yüzden kırpmayı biz,
/// bilerek ve kullanıcıya göstererek yaparız.
/// </summary>
public static class ContextBuilder
{
    private const int SummaryMaxChars = 700;
    private const string TrimNote = "\n\n[... metnin ortası bağlama sığmadığı için çıkarıldı ...]\n\n";

    public static int SafetyMargin(int contextLength) => Math.Max(64, contextLength / 32);

    /// <summary>Yanıt için ayrılan en az token. Düşünen modeller (Gemma 4) kısa bir cevaptan önce ~500 token düşünür.</summary>
    public static int DefaultReplyReserve(int contextLength) => Math.Clamp(contextLength / 4, 256, 2048);

    public static int DefaultReplyCap(int contextLength) => Math.Clamp(contextLength / 2, 512, 4096);

    public static ContextPlan Build(
        string systemPrompt,
        IReadOnlyList<ChatMessageDto> history,
        ChatMessageDto current,
        ContextOptions options)
    {
        var contextLength = Math.Max(512, options.ContextLength);
        var reserve = options.ReplyReserve ?? DefaultReplyReserve(contextLength);
        var cap = Math.Max(reserve, options.ReplyCap ?? DefaultReplyCap(contextLength));
        var margin = SafetyMargin(contextLength);
        var promptBudget = contextLength - reserve - margin;
        var scale = Math.Clamp(options.TokenScale, 0.5, 3.0);

        var used = TokenEstimator.Estimate(systemPrompt, scale) + TokenEstimator.MessageOverhead;

        // Güncel mesaj tek başına sığmıyorsa ortası kesilir; baş ve son (genelde soru) korunur.
        var trimmed = false;
        var currentTokens = TokenEstimator.Estimate(current, scale);
        if (used + currentTokens > promptBudget)
        {
            var allowedTokens = promptBudget - used - TokenEstimator.MessageOverhead - TokenEstimator.Estimate(TrimNote, scale);
            var allowedChars = (int)(Math.Max(64, allowedTokens) * TokenEstimator.CharsPerToken / scale);
            current = current with { Content = TrimMiddle(current.Content, allowedChars) };
            currentTokens = TokenEstimator.Estimate(current, scale);
            trimmed = true;
        }
        used += currentTokens;

        // Geçmiş yeniden eskiye eklenir; bütçe ya da mesaj sınırı dolunca durur.
        var normalized = Normalize(history);
        var willDrop = normalized.Count > options.MaxHistoryMessages ||
                       used + TokenEstimator.Estimate(normalized, scale) > promptBudget;
        // Bir kısmı sığmayacaksa çıkarılanlar notu için baştan yer ayrılır.
        var historyBudget = promptBudget - (willDrop ? TokenEstimator.Estimate(new string(' ', SummaryMaxChars + 1), scale) : 0);
        var kept = new List<ChatMessageDto>();
        var index = normalized.Count - 1;
        for (; index >= 0 && kept.Count < options.MaxHistoryMessages; index--)
        {
            var tokens = TokenEstimator.Estimate(normalized[index], scale);
            if (used + tokens > historyBudget)
            {
                break;
            }
            kept.Add(normalized[index]);
            used += tokens;
        }
        kept.Reverse();

        // Sohbet bir kullanıcı mesajıyla başlamalı (bazı şablonlar asistanla başlamayı reddeder).
        var droppedCount = index + 1;
        while (kept.Count > 0 && kept[0].Role != "user")
        {
            used -= TokenEstimator.Estimate(kept[0], scale);
            kept.RemoveAt(0);
            droppedCount++;
        }

        // Sığmayan eski mesajlardan kısa not; ayrı bir system mesajı yerine ana talimata eklenir
        // (birden çok system mesajını kabul etmeyen şablonlar var).
        var system = systemPrompt;
        if (droppedCount > 0)
        {
            var summary = BuildSummary(normalized.Take(droppedCount).ToList(), droppedCount);
            var summaryTokens = TokenEstimator.Estimate(summary, scale);
            if (used + summaryTokens <= promptBudget)
            {
                system += "\n\n" + summary;
                used += summaryTokens;
            }
        }

        var messages = new List<ChatMessageDto>(kept.Count + 2) { new("system", system) };
        messages.AddRange(kept);
        if (messages[^1].Role == "user")
        {
            // Son kayıtlı mesaj da kullanıcıdansa (önceki istek yanıtsız kaldıysa) güncel mesajla birleştir.
            messages[^1] = messages[^1] with { Content = messages[^1].Content + "\n\n" + current.Content };
        }
        else
        {
            messages.Add(current);
        }

        var maxReply = Math.Min(cap, contextLength - used - margin);
        return new ContextPlan(messages, used, maxReply, contextLength, droppedCount, trimmed,
            UnscaledPromptTokens: TokenEstimator.Estimate(messages));
    }

    /// <summary>Boş mesajları atar, rolleri küçük harfe çevirir, art arda gelen aynı rolleri birleştirir.</summary>
    public static List<ChatMessageDto> Normalize(IEnumerable<ChatMessageDto> history)
    {
        var result = new List<ChatMessageDto>();
        foreach (var message in history)
        {
            if (string.IsNullOrWhiteSpace(message.Content))
            {
                continue;
            }
            var role = message.Role.Trim().ToLowerInvariant() == "assistant" ? "assistant" : "user";
            if (result.Count > 0 && result[^1].Role == role)
            {
                result[^1] = result[^1] with { Content = result[^1].Content + "\n\n" + message.Content };
            }
            else
            {
                result.Add(new ChatMessageDto(role, message.Content));
            }
        }
        return result;
    }

    public static string TrimMiddle(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }
        var keep = Math.Max(32, maxChars - TrimNote.Length);
        var head = keep * 6 / 10;
        var tail = keep - head;
        return text[..head] + TrimNote + text[^tail..];
    }

    private static string BuildSummary(IReadOnlyList<ChatMessageDto> dropped, int count)
    {
        var builder = new StringBuilder($"Not: Bağlama sığmayan {count} eski mesaj çıkarıldı. Son kısımları:");
        foreach (var message in dropped.TakeLast(4))
        {
            var who = message.Role == "assistant" ? "Asistan" : "Kullanıcı";
            var text = message.Content.ReplaceLineEndings(" ");
            builder.Append($" {who}: {(text.Length > 150 ? text[..150] + "…" : text)}");
        }
        var summary = builder.ToString();
        return summary.Length > SummaryMaxChars ? summary[..SummaryMaxChars] + "…" : summary;
    }
}
