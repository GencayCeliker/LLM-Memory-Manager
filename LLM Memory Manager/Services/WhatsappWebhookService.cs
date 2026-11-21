using LLM_Memory_Manager.Database;
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using System.Collections.Generic;
namespace LLM_Memory_Manager.Services;

public sealed class WhatsappWebhookService : IDisposable
{
    private readonly ChatRepository _chatRepository;
    private readonly object _syncRoot = new();
    private readonly string _prefix = "http://localhost:5000/api/message/";
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _workerTask;

    public event Action<string>? LogReceived;

    public bool IsRunning
    {
        get
        {
            lock (_syncRoot)
            {
                return _listener?.IsListening == true;
            }
        }
    }

    private readonly ConversationService _conversation;

    public WhatsappWebhookService(ChatRepository chatRepository, ConversationService conversation)
    {
        _chatRepository = chatRepository;
        _conversation = conversation;
    }

    public Task StartAsync()
    {
        lock (_syncRoot)
        {
            if (_listener?.IsListening == true)
            {
                return Task.CompletedTask;
            }

            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            _listener?.Close();
            _listener = new HttpListener();
            _listener.Prefixes.Add(_prefix);
            _listener.Start();

            Log($"Whatsapp webhook dinleyicisi başlatıldı: {_prefix}");
            _workerTask = Task.Run(() => ListenLoopAsync(_cts.Token), _cts.Token);
            return Task.CompletedTask;
        }
    }

    public async Task StopAsync()
    {
        Task? workerTask;
        CancellationTokenSource? cts;
        HttpListener? listener;

        lock (_syncRoot)
        {
            workerTask = _workerTask;
            cts = _cts;
            listener = _listener;
            _workerTask = null;
            _cts = null;
            _listener = null;
        }

        if (listener == null)
        {
            return;
        }

        try
        {
            cts?.Cancel();
            if (listener.IsListening)
            {
                listener.Stop();
            }

            listener.Close();

            if (workerTask != null)
            {
                try
                {
                    await workerTask;
                }
                catch (OperationCanceledException)
                {
                }
                catch (HttpListenerException)
                {
                }
            }
        }
        finally
        {
            cts?.Dispose();
            Log("Whatsapp webhook dinleyicisi durduruldu.");
        }
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        HttpListener? listenerSnapshot;

        lock (_syncRoot)
        {
            listenerSnapshot = _listener;
        }

        if (listenerSnapshot == null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext? context = null;

            try
            {
                context = await listenerSnapshot.GetContextAsync();
                await HandleRequestAsync(context);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"Webhook döngüsü hatası: {ex.Message}");
                if (context != null)
                {
                    await WriteResponseAsync(context.Response, 500, "Internal Server Error");
                }
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
        {
            await WriteResponseAsync(context.Response, 405, "Only POST is allowed");
            return;
        }

        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
        var body = await reader.ReadToEndAsync();

        if (string.IsNullOrWhiteSpace(body))
        {
            await WriteResponseAsync(context.Response, 400, "Request body is empty");
            return;
        }

        if (!TryParseIncomingMessage(body, out var incomingMessage, out var parseError))
        {
            Log($"Webhook JSON çözümlenemedi: {parseError}");
            await WriteResponseAsync(context.Response, 400, parseError ?? "Invalid JSON");
            return;
        }

        await SaveIncomingMessageAsync(incomingMessage);
        await WriteResponseAsync(context.Response, 200, "OK");
    }

    private async Task SaveIncomingMessageAsync(WhatsappIncomingMessage incomingMessage)
    {
        var phoneNumber = incomingMessage.PhoneNumber.Trim();
        var message = incomingMessage.Message.Trim();

        if (string.IsNullOrWhiteSpace(phoneNumber) || string.IsNullOrWhiteSpace(message))
        {
            Log("Webhook mesajı boş telefon numarası veya içerik içeriyor.");
            return;
        }

        var user = await _chatRepository.GetUserByPhoneAsync(phoneNumber);
        if (user == null)
        {
            user = await _chatRepository.GetUserByIdAsync(1);
            if (user == null)
            {
                Log($"Webhook mesajı reddedildi: tanınmayan numara {phoneNumber} ve varsayılan kullanıcı ID 1 bulunamadı.");
                return;
            }

            Log($"Tanınmayan numara {phoneNumber} için kullanıcı ID 1 kullanılıyor.");
        }

        var session = await _chatRepository.GetOrCreateLatestSessionAsync(user.User_ID);

        Log($"Webhook mesajı alındı: {phoneNumber} -> {message}");

        // Yanıt arka planda üretilir. Ortak akış mesajı kaydeder, sohbet geçmişini token bütçesiyle ekler ve
        // istekleri sıraya koyar: art arda gelen mesajlar GPU'ya aynı anda yüklenmez.
        _ = Task.Run(async () =>
        {
            try
            {
                var settings = await _chatRepository.GetOrCreateSettingsAsync();
                var result = await _conversation.ReplyAsync(session.Session_ID, user.User_ID, message, settings,
                    ConversationService.WhatsappSystemPrompt);
                foreach (var warning in result.Warnings)
                {
                    Log("Uyarı: " + warning);
                }
                if (string.IsNullOrWhiteSpace(result.Reply.Content))
                {
                    Log("Model boş cevap döndürdü; WhatsApp'a gönderilmedi.");
                    return;
                }
                Log("Yanıt hazır: " + result.Summary);
                await SendReplyToWhatsAppAsync(phoneNumber, result.Reply.Content);
            }
            catch (Exception ex)
            {
                Log($"[Sistem Hatası] LLM veya Gönderim Hatası: {ex.Message}");
            }
        });
    }

    private static bool TryParseIncomingMessage(string body, out WhatsappIncomingMessage message, out string? error)
    {
        message = default!;
        error = null;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (!root.TryGetProperty("phoneNumber", out var phoneElement) || phoneElement.ValueKind != JsonValueKind.String)
            {
                error = "phoneNumber alanı bulunamadı.";
                return false;
            }

            if (!root.TryGetProperty("message", out var messageElement) || messageElement.ValueKind != JsonValueKind.String)
            {
                error = "message alanı bulunamadı.";
                return false;
            }

            message = new WhatsappIncomingMessage(
                phoneElement.GetString() ?? string.Empty,
                messageElement.GetString() ?? string.Empty);

            return true;
        }
        catch (JsonException ex)
        {
            error = $"Geçersiz JSON: {ex.Message}";
            return false;
        }
    }

    private async Task WriteResponseAsync(HttpListenerResponse response, int statusCode, string message)
    {
        try
        {
            var buffer = Encoding.UTF8.GetBytes(message);
            response.StatusCode = statusCode;
            response.ContentType = "text/plain; charset=utf-8";
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
        }
        finally
        {
            response.OutputStream.Close();
        }
    }
    //Ben ekledim: Token sayısını tahmin etmek için basit bir yöntem. Bu, kelime sayısını temel alır ve daha karmaşık bir tokenizasyon algoritması yerine geçmez.
    private async Task SendReplyToWhatsAppAsync(string phoneNumber, string aiReply)
    {
        try
        {
            var nodePayload = new
            {
                phoneNumber = phoneNumber,
                message = aiReply
            };

            string nodeJson = JsonSerializer.Serialize(nodePayload);
            var nodeContent = new StringContent(nodeJson, Encoding.UTF8, "application/json");

            using var httpClient = new HttpClient();
            var response = await httpClient.PostAsync("http://localhost:3000/api/send", nodeContent);

            if (response.IsSuccessStatusCode)
            {
                Log("🚀 Gemma'nın cevabı WhatsApp'a başarıyla iletildi!");
            }
            else
            {
                Log($"⚠️ WhatsApp iletimi başarısız oldu. Durum Kodu: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Log($"❌ WhatsApp'a cevap gönderilirken hata oluştu: {ex.Message}");
        }
    }

    private void Log(string message)
    {
        LogReceived?.Invoke(message);
    }

    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
            _listener?.Close();
            _workerTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }
        finally
        {
            _cts?.Dispose();
            _listener = null;
            _cts = null;
            _workerTask = null;
        }
    }

}

public sealed record WhatsappIncomingMessage(string PhoneNumber, string Message);
