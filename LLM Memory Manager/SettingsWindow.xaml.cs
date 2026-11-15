using LLM_Memory_Manager.Database;
using LLM_Memory_Manager.Models;
using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;

namespace LLM_Memory_Manager
{
    public partial class SettingsWindow : Window
    {
        private readonly ChatRepository _chatRepository = new();
        private AppSettings? _settings;

        public SettingsWindow()
        {
            InitializeComponent();
            Loaded += SettingsWindow_Loaded;
        }

        private async void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _settings = await _chatRepository.GetOrCreateSettingsAsync();
                ModelNameTextBox.Text = _settings.Model_Name ?? string.Empty;
                ContextLengthTextBox.Text = _settings.Context_Length.ToString(CultureInfo.InvariantCulture);
                MaxMemoryMessagesTextBox.Text = _settings.Max_Memory_Messages.ToString(CultureInfo.InvariantCulture);
                CpuThreadsTextBox.Text = _settings.Cpu_Threads.ToString(CultureInfo.InvariantCulture);
                GpuLayersTextBox.Text = _settings.Gpu_Layers.ToString(CultureInfo.InvariantCulture);
                TemperatureTextBox.Text = _settings.Temperature.ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Ayarlar yüklenemedi:\n\n{ex.Message}\n\nDetay:\n{ex.InnerException?.Message}",
                    "Hata",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async void BtnKaydet_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(ContextLengthTextBox.Text, out var contextLength) ||
                !int.TryParse(MaxMemoryMessagesTextBox.Text, out var maxMemoryMessages) ||
                !int.TryParse(CpuThreadsTextBox.Text, out var cpuThreads) ||
                !int.TryParse(GpuLayersTextBox.Text, out var gpuLayers) ||
                !double.TryParse(TemperatureTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature))
            {
                MessageBox.Show(
                    "Lütfen sayısal alanları doğru formatta girin.",
                    "Doğrulama Hatası",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            try
            {
                var settings = new AppSettings
                {
                    Setting_ID = _settings?.Setting_ID ?? 1,
                    Model_Name = ModelNameTextBox.Text.Trim(),
                    Context_Length = contextLength,
                    Max_Memory_Messages = maxMemoryMessages,
                    Cpu_Threads = cpuThreads,
                    Gpu_Layers = gpuLayers,
                    Temperature = temperature
                };

                await _chatRepository.SaveSettingsAsync(settings);
                MessageBox.Show(
                    "Ayarlar kaydedildi.",
                    "Başarılı",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Ayarlar kaydedilemedi:\n\n{ex.Message}\n\nDetay:\n{ex.InnerException?.Message}",
                    "Hata",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void BtnVazgec_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
