using LLM_Memory_Manager.Database;
using LLM_Memory_Manager.Models;
using LLM_Memory_Manager.Services;
using LlmBridge.Core;
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
        private readonly ConversationService _conversation;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly WhatsappWebhookService _whatsappWebhookService;
        private User? _currentUser;
        private Session? _currentSession;
        private AppSettings? _currentSettings;
        private bool _isRefreshingConversationList;

        public MainWindow()
        {
           
            InitializeComponent();

            _conversation = new ConversationService(_chatRepository, new ChatPipeline(new LmStudioClient()));
            _whatsappWebhookService = new WhatsappWebhookService(_chatRepository, _conversation);
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
            Border? replyBubble = null;
            try
            {
                await EnsureConversationAsync();
                await EnsureSettingsAsync();

                AddMessageBubble(messageText, isUser: true);
                MessageInputBox.Clear();

                replyBubble = AddMessageBubble("…", isUser: false);
                ContextStatusTextBlock.Text = LmStudioClient.IsBusy
                    ? "Sırada: önce webhook yanıtı bitiyor…"
                    : "Model yanıtlıyor…";
                var progress = new Progress<LlmProgress>(p =>
                {
                    if (p.ContentSoFar.Length > 0)
                    {
                        SetBubbleMarkdown(replyBubble, p.ContentSoFar);
                    }
                    else
                    {
                        SetBubbleText(replyBubble, $"Düşünüyor… ({p.ReasoningChunks} token)");
                    }
                    MessagesScrollViewer.ScrollToEnd();
                });

                var result = await _conversation.ReplyAsync(_currentSession!.Session_ID, _currentUser!.User_ID, messageText,
                    _currentSettings!, ConversationService.DefaultSystemPrompt, progress);

                if (string.IsNullOrWhiteSpace(result.Reply.Content))
                {
                    SetBubbleText(replyBubble, "(Model boş cevap döndürdü.)");
                }
                else
                {
                    SetBubbleMarkdown(replyBubble, result.Reply.Content);
                }
                ModelTitleTextBlock.Text = result.ModelId;
                ContextStatusTextBlock.Text = result.Summary + (result.Retried ? " · düşünme payı genişletildi" : "");
                foreach (var warning in result.Warnings)
                {
                    AddNoticeBubble(warning);
                }
            }
            catch (LlmException ex)
            {
                // Bağlantı / model / zaman aşımı: kullanıcıya anlaşılır açıklama, pencere açmadan.
                if (replyBubble is not null)
                {
                    SetBubbleText(replyBubble, "⚠ " + ex.Message);
                }
                ContextStatusTextBlock.Text = "Yanıt alınamadı";
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

        private void AddNoticeBubble(string text)
        {
            MessagesPanel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(58, 48, 20)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(204, 167, 0)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, -8, 0, 15),
                HorizontalAlignment = HorizontalAlignment.Left,
                MaxWidth = 600,
                Child = new TextBlock
                {
                    Text = "⚠ " + text,
                    Foreground = new SolidColorBrush(Color.FromRgb(255, 214, 102)),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12.5
                }
            });
            MessagesScrollViewer.ScrollToEnd();
        }

        private Border AddMessageBubble(string text, bool isUser)
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

            // Kullanıcının yazdığı olduğu gibi kalır; model yanıtındaki Markdown biçimlendirilir.
            if (isUser)
            {
                SetBubbleText(bubble, text);
            }
            else
            {
                SetBubbleMarkdown(bubble, text);
            }

            MessagesPanel.Children.Add(bubble);
            MessagesScrollViewer.ScrollToEnd();
            return bubble;
        }

        private const double MessageFontSize = 14;

        private static void SetBubbleText(Border bubble, string text) =>
            bubble.Child = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                FontSize = MessageFontSize
            };

        private static void SetBubbleMarkdown(Border bubble, string markdown) =>
            bubble.Child = MarkdownRenderer.Build(markdown, MessageFontSize, Brushes.White);
    }
}
