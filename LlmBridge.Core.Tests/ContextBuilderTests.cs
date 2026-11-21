using LlmBridge.Core;

namespace LlmBridge.Core.Tests;

public class ContextBuilderTests
{
    private static List<ChatMessageDto> Conversation(int turns, int charsPerMessage)
    {
        var list = new List<ChatMessageDto>();
        for (var i = 0; i < turns; i++)
        {
            list.Add(new ChatMessageDto("user", $"soru {i} " + new string('a', charsPerMessage)));
            list.Add(new ChatMessageDto("assistant", $"cevap {i} " + new string('b', charsPerMessage)));
        }
        return list;
    }

    [Fact]
    public void Short_conversation_is_sent_whole()
    {
        var plan = ContextBuilder.Build("Kısa cevap ver.", Conversation(3, 50), new("user", "yeni soru"), new(8192));
        Assert.Equal(8, plan.Messages.Count);              // system + 6 geçmiş + güncel
        Assert.Equal(0, plan.DroppedMessages);
        Assert.Equal("system", plan.Messages[0].Role);
        Assert.Equal("yeni soru", plan.Messages[^1].Content);
    }

    [Fact]
    public void Long_history_is_trimmed_to_budget_and_never_exceeds_context()
    {
        var plan = ContextBuilder.Build("Kısa cevap ver.", Conversation(60, 2000), new("user", "son soru"), new(8192));
        Assert.True(plan.DroppedMessages > 0);
        Assert.True(plan.EstimatedPromptTokens + plan.MaxReplyTokens <= plan.ContextLength);
        Assert.True(plan.MaxReplyTokens >= ContextBuilder.DefaultReplyReserve(8192));
        Assert.Equal("son soru", plan.Messages[^1].Content);
        Assert.Contains("Bağlama sığmayan", plan.Messages[0].Content);   // not system mesajının içinde
        Assert.Single(plan.Messages, m => m.Role == "system");
    }

    [Fact]
    public void Kept_history_starts_with_user_and_roles_alternate()
    {
        var plan = ContextBuilder.Build("x", Conversation(40, 1500), new("user", "soru"), new(4096));
        var turns = plan.Messages.Skip(1).ToList();
        Assert.Equal("user", turns[0].Role);
        for (var i = 1; i < turns.Count; i++)
        {
            Assert.NotEqual(turns[i - 1].Role, turns[i].Role);
        }
    }

    [Fact]
    public void Unanswered_user_message_is_merged_instead_of_sent_twice_in_a_row()
    {
        var history = new List<ChatMessageDto>
        {
            new("user", "ilk soru"), new("assistant", "ilk cevap"), new("user", "yanıtsız kalan soru"),
        };
        var plan = ContextBuilder.Build("x", history, new("user", "tekrar soruyorum"), new(8192));
        Assert.Equal(new[] { "system", "user", "assistant", "user" }, plan.Messages.Select(m => m.Role));
        Assert.Contains("yanıtsız kalan soru", plan.Messages[^1].Content);
        Assert.Contains("tekrar soruyorum", plan.Messages[^1].Content);
    }

    [Fact]
    public void Huge_current_message_is_trimmed_in_the_middle_keeping_head_and_tail()
    {
        var huge = "BAŞ " + new string('x', 60_000) + " SON SORU?";
        var plan = ContextBuilder.Build("x", [], new("user", huge), new(4096));
        Assert.True(plan.CurrentMessageTrimmed);
        var sent = plan.Messages[^1].Content;
        Assert.StartsWith("BAŞ", sent);
        Assert.EndsWith("SON SORU?", sent);
        Assert.True(plan.EstimatedPromptTokens + plan.MaxReplyTokens <= 4096);
    }

    [Fact]
    public void Max_history_messages_limit_is_respected()
    {
        var plan = ContextBuilder.Build("x", Conversation(20, 10), new("user", "s"), new(32768, MaxHistoryMessages: 4));
        Assert.Equal(1 + 4 + 1, plan.Messages.Count);
    }

    [Theory]
    // Gerçek sayılar LM Studio + Gemma 4 ile ölçüldü; temel tahmin çoğu metinde gerçeğin %15 yakınında ya da üstünde.
    [InlineData("Merhaba, bugün projemde karşılaştığım bir sorunu anlatmak istiyorum. Uygulamam açıldığında veritabanına bağlanıyor ama bazen bağlantı zaman aşımına uğruyor ve kullanıcılar bekletiliyor.", 47)]
    [InlineData("public async Task<List<ChatMessage>> GetRecentMessagesAsync(int sessionId, int limit) { using var ctx = new AppDbContext(); return await ctx.ChatMessages.Where(m => m.Session_ID == sessionId).Take(limit).ToListAsync(); }", 54)]
    [InlineData("def merge(a, b):\n    result = []\n    for x, y in zip(a, b):\n        result.append(x + y)\n    return result\n", 39)]
    public void Base_estimate_is_close_to_measured_token_count(string text, int measured)
    {
        Assert.InRange(TokenEstimator.Estimate(text), measured * 85 / 100, measured * 14 / 10);
    }

    [Fact]
    public void Token_scale_grows_at_once_when_underestimated_and_shrinks_slowly()
    {
        var scale = new TokenScale();
        scale.Observe(estimatedUnscaled: 5000, actual: 7500);      // JSON / ekli Türkçe: tahmin %33 düşük
        Assert.InRange(scale.Value, 1.5, 1.6);
        scale.Observe(estimatedUnscaled: 5000, actual: 4000);      // tek düşük örnek çarpanı hemen düşürmez
        Assert.True(scale.Value > 1.3);
        scale.Observe(estimatedUnscaled: 10, actual: 100);         // çok kısa istem yok sayılır
        Assert.True(scale.Value < 2);
    }

    [Fact]
    public void Scaled_estimate_keeps_more_room_for_the_same_history()
    {
        var history = Conversation(40, 1500);
        var normal = ContextBuilder.Build("x", history, new("user", "s"), new(8192));
        var scaled = ContextBuilder.Build("x", history, new("user", "s"), new(8192, TokenScale: 1.5));
        Assert.True(scaled.Messages.Count < normal.Messages.Count);
        Assert.True(scaled.EstimatedPromptTokens + scaled.MaxReplyTokens <= 8192);
    }
}
