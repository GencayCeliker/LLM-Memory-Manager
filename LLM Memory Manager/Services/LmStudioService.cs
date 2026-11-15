using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LLM_Memory_Manager.Services;

public sealed class LmStudioService
{
    private static readonly HttpClient HttpClient = new()
    {
        BaseAddress = new Uri("http://127.0.0.1:1234")
    };

    private const string DefaultModel = "google/gemma-4-e4b";

    public async Task<string> GetChatReplyAsync(
        IReadOnlyList<ChatMessageDto> messages,
        LmStudioRequestOptions options,
        CancellationToken cancellationToken = default)
    {
        var request = new
        {
            model = string.IsNullOrWhiteSpace(options.ModelName) ? DefaultModel : options.ModelName,
            messages,
            temperature = options.Temperature,
            max_tokens = options.MaxTokens,
            stream = false
        };

        var endpointCandidates = new[]
        {
            "/v1/chat/completions",
            "/api/v1/chat/completions",
            "/api/v1/chat"
        };

        string? lastFailure = null;

        foreach (var endpoint in endpointCandidates)
        {
            using var response = await HttpClient.PostAsJsonAsync(endpoint, request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                lastFailure = $"{endpoint}: {(int)response.StatusCode} {response.ReasonPhrase}\n{responseBody}";
                continue;
            }

            if (TryParseReply(responseBody, out var reply))
            {
                return reply;
            }

            lastFailure = $"{endpoint}: Yanıt alındı ancak içerik ayrıştırılamadı.\n{responseBody}";
        }

        throw new InvalidOperationException(
            $"LM Studio yanıtı alınamadı. Endpoint veya istek formatı uyumsuz olabilir.\n\nSon hata:\n{lastFailure}");
    }

    public sealed record LmStudioRequestOptions(string? ModelName, double Temperature, int MaxTokens);

    private static bool TryParseReply(string responseBody, out string reply)
    {
        reply = string.Empty;

        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return false;
        }

        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;

        if (TryGetString(root, "content", out var directContent))
        {
            reply = directContent;
            return true;
        }

        if (root.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0)
        {
            var firstChoice = choices[0];

            if (firstChoice.TryGetProperty("message", out var message) &&
                TryGetString(message, "content", out var messageContent))
            {
                reply = messageContent;
                return true;
            }

            if (TryGetString(firstChoice, "text", out var textContent))
            {
                reply = textContent;
                return true;
            }
        }

        if (TryGetString(root, "response", out var responseContent))
        {
            reply = responseContent;
            return true;
        }

        return false;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;

        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }
}

public sealed record ChatMessageDto(string Role, string Content);
