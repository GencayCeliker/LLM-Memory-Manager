using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LlmBridge.Core;

/// <summary>
/// LM Studio (OpenAI uyumlu) istemcisi.
/// <list type="bullet">
/// <item>Yanıtı akışla alır: ilk token hemen görünür, zaman aşımı "toplam süre" değil "sessizlik" süresidir.</item>
/// <item>Düşünen modellerin <c>reasoning_content</c> parçalarını cevaptan ayırır.</item>
/// <item>Aynı işlemdeki tüm istekler sıraya girer: sohbet ve webhook aynı anda GPU'ya yüklenmez.</item>
/// </list>
/// </summary>
public sealed partial class LmStudioClient
{
    // İşlem genelinde tek üretim: paralel istekler aynı GPU belleğini (KV cache) paylaşır ve hepsi yavaşlar.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly HttpClient SharedHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    // Arayüzü her token'da güncellemek yerine saniyede ~10 kez.
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    private readonly HttpClient _http;
    private readonly Uri _baseUri;

    public LmStudioClient(string baseUrl = "http://127.0.0.1:1234", HttpClient? http = null)
    {
        _baseUri = new Uri(baseUrl.TrimEnd('/') + "/");
        _http = http ?? SharedHttp;
    }

    /// <summary>Bu süre boyunca sunucudan hiç veri gelmezse istek iptal edilir.</summary>
    public TimeSpan InactivityTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>Sıradaki istek sayısı (arayüzde "sırada bekliyor" göstermek için).</summary>
    public static bool IsBusy => Gate.CurrentCount == 0;

    public async Task<IReadOnlyList<ModelInfo>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var response = await _http.GetAsync(new Uri(_baseUri, "api/v0/models"), cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return await GetOpenAiModelsAsync(cts.Token);
            }
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
            var list = new List<ModelInfo>();
            foreach (var item in document.RootElement.GetProperty("data").EnumerateArray())
            {
                list.Add(new ModelInfo(
                    item.GetProperty("id").GetString() ?? "",
                    GetString(item, "type") ?? "llm",
                    GetString(item, "state") ?? "unknown",
                    GetInt(item, "loaded_context_length"),
                    GetInt(item, "max_context_length")));
            }
            return list;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new LlmException("LM Studio'ya ulaşılamıyor. LM Studio açık mı ve Local Server 1234 portunda başlatıldı mı?", ex);
        }
    }

    private async Task<IReadOnlyList<ModelInfo>> GetOpenAiModelsAsync(CancellationToken cancellationToken)
    {
        // Eski LM Studio / başka OpenAI uyumlu sunucu: yükleme durumu ve bağlam bilinmez.
        var body = await _http.GetStringAsync(new Uri(_baseUri, "v1/models"), cancellationToken);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("data").EnumerateArray()
            .Select(item => new ModelInfo(item.GetProperty("id").GetString() ?? "", "llm", "unknown", null, null))
            .ToList();
    }

    public async Task<LlmReply> ChatAsync(
        LlmRequest request,
        IProgress<LlmProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            return await ChatCoreAsync(request, progress, cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<LlmReply> ChatCoreAsync(LlmRequest request, IProgress<LlmProgress>? progress, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["messages"] = request.Messages.Select(m => new { role = m.Role, content = m.Content }),
            ["temperature"] = request.Temperature,
            ["max_tokens"] = request.MaxTokens,
            ["stream"] = true,
            ["stream_options"] = new { include_usage = true },
        };

        using var inactivity = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        inactivity.CancelAfter(InactivityTimeout);
        var watch = Stopwatch.StartNew();

        HttpResponseMessage response;
        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "v1/chat/completions"))
            {
                Content = JsonContent.Create(payload),
            };
            response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, inactivity.Token);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException("LM Studio'ya ulaşılamıyor. LM Studio açık mı ve Local Server 1234 portunda başlatıldı mı?", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LlmException($"Model {InactivityTimeout.TotalSeconds:0} saniye içinde yanıt vermeye başlamadı (model yükleniyor ya da GPU belleği taşmış olabilir).", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new LlmException(DescribeHttpError((int)response.StatusCode, errorBody));
            }

            var content = new StringBuilder();
            string? finishReason = null;
            int? promptTokens = null, completionTokens = null, reasoningTokens = null;
            int contentChunks = 0, reasoningChunks = 0;
            TimeSpan? firstToken = null;
            var lastReport = TimeSpan.Zero;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            try
            {
                while (true)
                {
                    var line = await reader.ReadLineAsync(inactivity.Token);
                    if (line is null)
                    {
                        break;
                    }
                    inactivity.CancelAfter(InactivityTimeout);   // veri geldi: sessizlik sayacı sıfırlanır
                    if (!line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    var data = line[5..].Trim();
                    if (data == "[DONE]")
                    {
                        break;
                    }

                    using var chunk = JsonDocument.Parse(data);
                    var root = chunk.RootElement;
                    if (root.TryGetProperty("error", out var error))
                    {
                        throw new LlmException("Model hata döndürdü: " + error.ToString());
                    }
                    if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                    {
                        promptTokens = GetInt(usage, "prompt_tokens");
                        completionTokens = GetInt(usage, "completion_tokens");
                        if (usage.TryGetProperty("completion_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object)
                        {
                            reasoningTokens = GetInt(details, "reasoning_tokens");
                        }
                    }
                    if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                    {
                        continue;
                    }
                    var choice = choices[0];
                    if (GetString(choice, "finish_reason") is { } reason)
                    {
                        finishReason = reason;
                    }
                    if (!choice.TryGetProperty("delta", out var delta))
                    {
                        continue;
                    }
                    var piece = GetString(delta, "content");
                    var thought = GetString(delta, "reasoning_content") ?? GetString(delta, "reasoning");
                    if (!string.IsNullOrEmpty(piece) || !string.IsNullOrEmpty(thought))
                    {
                        firstToken ??= watch.Elapsed;
                    }
                    if (!string.IsNullOrEmpty(piece))
                    {
                        content.Append(piece);
                        contentChunks++;
                    }
                    if (!string.IsNullOrEmpty(thought))
                    {
                        reasoningChunks++;
                    }
                    if (progress is not null && watch.Elapsed - lastReport > ProgressInterval)
                    {
                        lastReport = watch.Elapsed;
                        progress.Report(new LlmProgress(VisibleText(content.ToString()), contentChunks, reasoningChunks));
                    }
                }
                progress?.Report(new LlmProgress(VisibleText(content.ToString()), contentChunks, reasoningChunks));
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new LlmException($"Model {InactivityTimeout.TotalSeconds:0} saniye boyunca hiçbir şey üretmedi; istek durduruldu.", ex);
            }

            var text = StripThinking(content.ToString());
            if (reasoningTokens is null && reasoningChunks > 0)
            {
                reasoningTokens = reasoningChunks;   // sunucu ayrıntı vermediyse parça sayısı yaklaşık token sayısıdır
            }
            return new LlmReply(text, finishReason, promptTokens, completionTokens, reasoningTokens,
                watch.Elapsed, firstToken ?? watch.Elapsed);
        }
    }

    /// <summary>Bazı modeller düşünmeyi <c>&lt;think&gt;</c> etiketiyle cevabın içine yazar.</summary>
    public static string StripThinking(string text)
    {
        text = ThinkBlock().Replace(text, "");
        var open = text.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        if (open >= 0)
        {
            text = text[..open];   // kapanmamış düşünme: cevap henüz başlamamış
        }
        return text.Trim();
    }

    private static string VisibleText(string text) => StripThinking(text);

    private static string DescribeHttpError(int status, string body)
    {
        var lower = body.ToLowerInvariant();
        if (lower.Contains("context") && (lower.Contains("length") || lower.Contains("overflow") || lower.Contains("exceed")))
        {
            return "İstek modelin bağlam uzunluğunu aşıyor. Daha kısa bir mesaj deneyin ya da LM Studio'da Context Length'i artırın.";
        }
        if (lower.Contains("no models loaded") || lower.Contains("model not found") || lower.Contains("not loaded"))
        {
            return "Model LM Studio'da yüklü değil ya da adı yanlış. Ayarlardaki model adını ve LM Studio'yu kontrol edin.\n" + Shorten(body);
        }
        return $"LM Studio hata döndürdü ({status}): {Shorten(body)}";
    }

    private static string Shorten(string text) => text.Length > 300 ? text[..300] + "…" : text;

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    [GeneratedRegex("<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlock();
}
