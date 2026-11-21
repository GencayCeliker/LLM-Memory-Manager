namespace LlmBridge.Core;

/// <summary>
/// Modele göndermeden önce token sayısını tahmin eder.
/// <para>
/// Ölçüm (LM Studio, Gemma 4), karakter/token: İngilizce 5,5 · Türkçe sohbet 3,9 · ekli Türkçe 3,4 ·
/// C# 4,1 · girintili Python 2,7 · tekrar eden Türkçe 2,6 · JSON 2,1. Kelime saymak gerçeğin yarısını verir.
/// </para>
/// Tek bir oran her metne uymadığı için temel tahmin 3,0 ile yapılır, <see cref="TokenScale"/> ise
/// her yanıttaki gerçek <c>prompt_tokens</c> ile model başına ayarlanır.
/// </summary>
public static class TokenEstimator
{
    public const double CharsPerToken = 3.0;

    /// <summary>Sohbet şablonunun her mesaja eklediği rol/ayraç token'ları.</summary>
    public const int MessageOverhead = 8;

    public static int Estimate(string? text, double scale = 1.0) =>
        string.IsNullOrEmpty(text) ? 0 : (int)Math.Ceiling(text.Length / CharsPerToken * scale);

    public static int Estimate(ChatMessageDto message, double scale = 1.0) =>
        Estimate(message.Content, scale) + MessageOverhead;

    public static int Estimate(IEnumerable<ChatMessageDto> messages, double scale = 1.0) =>
        messages.Sum(m => Estimate(m, scale));
}

/// <summary>
/// Gerçek token sayılarından öğrenen düzeltme çarpanı. Tahmin düşük kaldıysa hemen büyür
/// (taşmayı önlemek önemli), yüksek kaldıysa yavaşça küçülür (gereksiz kırpmayı azaltmak için).
/// </summary>
public sealed class TokenScale
{
    private const double Margin = 1.05;
    private double _value = 1.0;
    private readonly object _lock = new();

    public double Value
    {
        get { lock (_lock) { return _value; } }
    }

    public void Observe(int estimatedUnscaled, int actual)
    {
        if (estimatedUnscaled < 32 || actual <= 0)
        {
            return;   // çok kısa istemde oran gürültülüdür
        }
        var observed = (double)actual / estimatedUnscaled * Margin;
        lock (_lock)
        {
            _value = observed > _value ? observed : _value * 0.8 + observed * 0.2;
            _value = Math.Clamp(_value, 0.5, 3.0);
        }
    }
}
