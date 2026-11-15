using LLM_Memory_Manager.Database;
using LLM_Memory_Manager.Models;
using LLM_Memory_Manager.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LLM_Memory_Manager
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly ChatRepository _chatRepository = new();
        private readonly LmStudioService _lmStudioService = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly WhatsappWebhookService _whatsappWebhookService;
        private User? _currentUser;
        private Session? _currentSession;
        private AppSettings? _currentSettings;
        private bool _isRefreshingConversationList;

        public MainWindow()
        {
           
            InitializeComponent();
           
            _whatsappWebhookService = new WhatsappWebhookService(_chatRepository);
            _whatsappWebhookService.LogReceived += message => Dispatcher.Invoke(() => WebhookStatusTextBlock.Text = $"Webhook: {message}");
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await InitializeWorkspaceAsync();
            await StartWebhookServiceAsync();
        }

        private async void BtnYeniSohbet_Click(object sender, RoutedEventArgs e)
        {
            await StartNewConversationAsync();
        }

        private async void ConversationListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isRefreshingConversationList)
            {
                return;
            }

            if (ConversationListBox.SelectedItem is Session selectedSession &&
                (_currentSession == null || selectedSession.Session_ID != _currentSession.Session_ID))
            {
                await LoadConversationAsync(selectedSession);
            }
        }

        private async void BtnGonder_Click(object sender, RoutedEventArgs e)
        {
            var messageText = MessageInputBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(messageText))
            {
                return;
            }

            await _sendLock.WaitAsync();
            try
            {
                await EnsureConversationAsync();
                await EnsureSettingsAsync();

                AddMessageBubble(messageText, isUser: true);
                await _chatRepository.SaveMessageAsync(_currentSession!.Session_ID, _currentUser!.User_ID, "user", messageText, EstimateTokenCount(messageText));

                MessageInputBox.Clear();

                var history = await _chatRepository.GetRecentMessagesAsync(_currentSession.Session_ID, Math.Max(1, _currentSettings!.Max_Memory_Messages));
                var llmMessages = BuildLmStudioMessages(history, messageText, _currentSettings);
                var requestOptions = BuildRequestOptions(_currentSettings);
                var assistantReply = await _lmStudioService.GetChatReplyAsync(llmMessages, requestOptions);

                AddMessageBubble(assistantReply, isUser: false);
                await _chatRepository.SaveMessageAsync(_currentSession.Session_ID, _currentUser.User_ID, "assistant", assistantReply, EstimateTokenCount(assistantReply));
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Mesaj gönderilirken hata oluştu:\n\n{ex.Message}\n\nDetay:\n{ex.InnerException?.Message}",
                    "Hata",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task InitializeWorkspaceAsync()
        {
            try
            {
                _currentUser = await _chatRepository.GetOrCreateUserAsync("05550000000", "Gencay");
                await EnsureSettingsAsync();
                await RefreshConversationListAsync();

                if (ConversationListBox.Items.Count == 0)
                {
                    await StartNewConversationAsync();
                    return;
                }

                if (ConversationListBox.SelectedItem is Session selectedSession)
                {
                    await LoadConversationAsync(selectedSession);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Uygulama başlatılamadı:\n\n{ex.Message}\n\nDetay:\n{ex.InnerException?.Message}",
                    "Hata",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async Task StartNewConversationAsync()
        {
            try
            {
                await EnsureCurrentUserAsync();
                _currentSession = await _chatRepository.CreateSessionAsync(_currentUser!.User_ID);

                await RefreshConversationListAsync(_currentSession.Session_ID);
                MessagesPanel.Children.Clear();
                AddMessageBubble("Yeni sohbet başlatıldı. Mesaj yazıp Gönder butonuna basabilirsiniz.", isUser: false);
                MessageInputBox.Clear();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Yeni sohbet başlatılamadı:\n\n{ex.Message}\n\nDetay:\n{ex.InnerException?.Message}",
                    "Hata",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async Task LoadConversationAsync(Session session)
        {
            await EnsureCurrentUserAsync();
            await EnsureSettingsAsync();

            _currentSession = session;
            MessagesPanel.Children.Clear();

            var messages = await _chatRepository.GetMessagesBySessionAsync(session.Session_ID);
            if (messages.Count == 0)
            {
                AddMessageBubble("Bu sohbet için henüz mesaj yok.", isUser: false);
                return;
            }

            foreach (var message in messages)
            {
                if (string.IsNullOrWhiteSpace(message.Message_Content))
                {
                    continue;
                }

                AddMessageBubble(message.Message_Content, string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase));
            }
        }

        private async Task RefreshConversationListAsync(int? selectedSessionId = null)
        {
            await EnsureCurrentUserAsync();

            _isRefreshingConversationList = true;
            try
            {
                var sessions = await _chatRepository.GetSessionsAsync(_currentUser!.User_ID);
                ConversationListBox.ItemsSource = sessions;

                if (selectedSessionId.HasValue)
                {
                    ConversationListBox.SelectedItem = sessions.FirstOrDefault(s => s.Session_ID == selectedSessionId.Value);
                }
                else
                {
                    ConversationListBox.SelectedItem = sessions.FirstOrDefault();
                }
            }
            finally
            {
                _isRefreshingConversationList = false;
            }
        }

        private async Task EnsureCurrentUserAsync()
        {
            if (_currentUser is null)
            {
                _currentUser = await _chatRepository.GetOrCreateUserAsync("05550000000", "Gencay");
            }
        }

        private async Task EnsureSettingsAsync()
        {
            if (_currentSettings is null)
            {
                _currentSettings = await _chatRepository.GetOrCreateSettingsAsync();
            }
        }

        private async void BtnAyarlar_Click(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow
            {
                Owner = this
            };

            settingsWindow.ShowDialog();
            await EnsureSettingsAsync();
            _currentSettings = await _chatRepository.GetOrCreateSettingsAsync();
        }

        private async void BtnWebhook_Click(object sender, RoutedEventArgs e)
        {
            if (_whatsappWebhookService.IsRunning)
            {
                await StopWebhookServiceAsync();
                return;
            }

            await StartWebhookServiceAsync();
        }

        private async Task StartWebhookServiceAsync()
        {
            try
            {
                await _whatsappWebhookService.StartAsync();
                WebhookStatusTextBlock.Text = "Webhook: Dinliyor";
            }
            catch (Exception ex)
            {
                WebhookStatusTextBlock.Text = "Webhook: Hata";
                MessageBox.Show($"Webhook başlatılamadı:\n\n{ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task StopWebhookServiceAsync()
        {
            try
            {
                await _whatsappWebhookService.StopAsync();
                WebhookStatusTextBlock.Text = "Webhook: Durdu";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Webhook durdurulamadı:\n\n{ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            await StopWebhookServiceAsync();
            _whatsappWebhookService.Dispose();
        }

        private void BtnTanilama_Click(object sender, RoutedEventArgs e)
        {
            var diagnosticsWindow = new DiagnosticsWindow
            {
                Owner = this
            };

            diagnosticsWindow.ShowDialog();
        }

        private async Task EnsureConversationAsync()
        {
            await EnsureCurrentUserAsync();
            await EnsureSettingsAsync();

            if (_currentSession is null)
            {
                _currentSession = await _chatRepository.CreateSessionAsync(_currentUser!.User_ID);
                await RefreshConversationListAsync(_currentSession.Session_ID);
            }
        }

        private static IReadOnlyList<ChatMessageDto> BuildLmStudioMessages(IEnumerable<ChatMessage> history, string currentUserMessage, AppSettings settings)
        {
            var memoryLimit = Math.Max(1, settings.Max_Memory_Messages);
            var filteredHistory = history
                .Where(item => !string.IsNullOrWhiteSpace(item.Role) && !string.IsNullOrWhiteSpace(item.Message_Content))
                .ToList();

            var messages = new List<ChatMessageDto>
            {
                new("system", BuildSystemPrompt(settings))
            };

            if (filteredHistory.Count > memoryLimit)
            {
                var olderMessages = filteredHistory.Take(filteredHistory.Count - memoryLimit).ToList();
                var summary = BuildConversationSummary(olderMessages);
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    messages.Add(new ChatMessageDto("system", $"Önceki konuşma özeti: {summary}"));
                }

                filteredHistory = filteredHistory.Skip(filteredHistory.Count - memoryLimit).ToList();
            }

            if (filteredHistory.Count > 0 &&
                string.Equals(filteredHistory[^1].Role, "user", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(filteredHistory[^1].Message_Content, currentUserMessage, StringComparison.Ordinal))
            {
                filteredHistory = filteredHistory.Take(filteredHistory.Count - 1).ToList();
            }

            foreach (var item in filteredHistory)
            {
                messages.Add(new ChatMessageDto(item.Role ?? "user", item.Message_Content ?? string.Empty));
            }

            messages.Add(new ChatMessageDto("user", currentUserMessage));
            return messages;
        }

        private static LmStudioService.LmStudioRequestOptions BuildRequestOptions(AppSettings settings)
        {
            var maxTokens = Math.Clamp(settings.Context_Length / 16, 128, 1024);
            return new LmStudioService.LmStudioRequestOptions(settings.Model_Name, settings.Temperature, maxTokens);
        }

        private static string BuildSystemPrompt(AppSettings settings)
        {
            return $"Sen LLM Memory Manager içindeki yerel asistanısın. Kısa, net ve yardımcı cevap ver. Gereksiz uzun düşünme. Model: {settings.Model_Name}, sıcaklık: {settings.Temperature:0.##}, bellek mesaj limiti: {settings.Max_Memory_Messages}.";
        }

        private static string BuildConversationSummary(IReadOnlyList<ChatMessage> olderMessages)
        {
            var snippets = olderMessages
                .Where(message => !string.IsNullOrWhiteSpace(message.Message_Content))
                .TakeLast(4)
                .Select(message => $"{(string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "Asistan" : "Kullanıcı")}: {Truncate(message.Message_Content!, 120)}");

            return string.Join(" ", snippets).Trim();
        }

        private static string Truncate(string text, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length <= maxLength)
            {
                return text;
            }

            return text[..maxLength].Trim() + "...";
        }

        private void AddMessageBubble(string text, bool isUser)
        {
            var bubble = new Border
            {
                Background = isUser ? new SolidColorBrush(Color.FromRgb(0, 122, 204)) : new SolidColorBrush(Color.FromRgb(45, 45, 48)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(15),
                Margin = new Thickness(0, 0, 0, 15),
                HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                MaxWidth = 600
            };

            bubble.Child = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14
            };

            MessagesPanel.Children.Add(bubble);
            MessagesScrollViewer.ScrollToEnd();
        }

        private static int EstimateTokenCount(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return 0;
            }

            return text
                .Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Length;
        }
    }
}
