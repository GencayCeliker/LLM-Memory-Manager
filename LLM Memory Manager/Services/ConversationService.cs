using LLM_Memory_Manager.Database;
using LLM_Memory_Manager.Models;
using LlmBridge.Core;

namespace LLM_Memory_Manager.Services;

/// <summary>
/// Sohbet ekranı ve WhatsApp webhook'unun ortak akışı: geçmişi okur, kullanıcı mesajını kaydeder,
/// token bütçesine sığan bağlamla modele sorar ve cevabı gerçek token sayısıyla kaydeder.
/// </summary>
public sealed class ConversationService(ChatRepository repository, ChatPipeline pipeline)
{
    public const string DefaultSystemPrompt =
        "Sen LLM Memory Manager içindeki yerel asistanısın. Türkçe, kısa, net ve yardımcı cevap ver.";

    public const string WhatsappSystemPrompt =
        "Sen WhatsApp üzerinden Türkçe konuşan, zeki ve kısa cevaplar veren bir asistansın.";

    /// <summary>Mesaj sınırının ötesinde okunan mesajlar: sığmayanlardan kısa bir not çıkarılır.</summary>
    private const int ExtraHistoryForSummary = 10;

    public ChatPipeline Pipeline => pipeline;

    public async Task<ChatTurnResult> ReplyAsync(
        int sessionId,
        int userId,
        string userMessage,
        AppSettings settings,
        string systemPrompt,
        IProgress<LlmProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Geçmiş, güncel mesaj kaydedilmeden önce okunur; güncel mesaj ayrıca eklenir.
        var history = await repository.GetRecentMessagesAsync(sessionId, Math.Max(1, settings.Max_Memory_Messages) + ExtraHistoryForSummary);
        await repository.SaveMessageAsync(sessionId, userId, "user", userMessage, TokenEstimator.Estimate(userMessage));

        var dtos = history
            .Where(m => !string.IsNullOrWhiteSpace(m.Message_Content))
            .Select(m => new ChatMessageDto(m.Role ?? "user", m.Message_Content!))
            .ToList();

        var options = new ChatTurnOptions(settings.Model_Name, settings.Temperature, settings.Context_Length, settings.Max_Memory_Messages);
        var result = await pipeline.RunAsync(systemPrompt, dtos, userMessage, options, progress, cancellationToken);

        if (!string.IsNullOrWhiteSpace(result.Reply.Content))
        {
            // Düşünme token'ları cevabın parçası değildir; kaydedilen sayı bir sonraki bağlam hesabında kullanılır.
            var tokens = result.Reply.CompletionTokens is int completion
                ? completion - (result.Reply.ReasoningTokens ?? 0)
                : TokenEstimator.Estimate(result.Reply.Content);
            await repository.SaveMessageAsync(sessionId, userId, "assistant", result.Reply.Content, Math.Max(1, tokens));
        }
        return result;
    }
}
