using LlmBridge.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace LLM_Memory_Manager
{
    public partial class DiagnosticsWindow : Window
    {
        public DiagnosticsWindow()
        {
            InitializeComponent();
            Loaded += DiagnosticsWindow_Loaded;
        }

        private async void DiagnosticsWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await RefreshDiagnosticsAsync();
        }

        private async void BtnYenile_Click(object sender, RoutedEventArgs e)
        {
            await RefreshDiagnosticsAsync();
        }

        private void BtnKopyala_Click(object sender, RoutedEventArgs e)
        {
            if (DiagnosticsListBox.SelectedItem is null)
            {
                MessageBox.Show("Kopyalamak için bir satır seçin.", "Bilgi", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Clipboard.SetText(DiagnosticsListBox.SelectedItem.ToString() ?? string.Empty);
            MessageBox.Show("Seçili satır kopyalandı.", "Bilgi", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnKapat_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private async Task RefreshDiagnosticsAsync()
        {
            var logLines = new List<string>();

            try
            {
                var currentProcess = Process.GetCurrentProcess();
                var workingSetMb = currentProcess.WorkingSet64 / 1024d / 1024d;
                var privateMemoryMb = currentProcess.PrivateMemorySize64 / 1024d / 1024d;

                ProcessMemoryTextBlock.Text = $"{privateMemoryMb:0.0} MB";
                WorkingSetTextBlock.Text = $"{workingSetMb:0.0} MB";
                logLines.Add($"İşlem belleği: {privateMemoryMb:0.0} MB, Working Set: {workingSetMb:0.0} MB");
            }
            catch (Exception ex)
            {
                ProcessMemoryTextBlock.Text = "Hata";
                WorkingSetTextBlock.Text = "Hata";
                logLines.Add($"Bellek okunamadı: {ex.Message}");
            }

            logLines.AddRange(await CheckLmStudioAsync());
            logLines.AddRange(await CheckGpuAsync());

            DiagnosticsListBox.ItemsSource = logLines;
        }

        private async Task<IEnumerable<string>> CheckLmStudioAsync()
        {
            try
            {
                var models = await new LmStudioClient().GetModelsAsync();
                var loaded = models.Where(m => m.IsChatModel && m.IsLoaded).ToList();
                LmStudioStatusTextBlock.Text = loaded.Count switch
                {
                    0 => "Model yok",
                    1 => "Aktif",
                    _ => $"{loaded.Count} model!"
                };
                var lines = new List<string> { $"LM Studio: aktif, {models.Count} model kayıtlı, {loaded.Count} sohbet modeli yüklü" };
                lines.AddRange(loaded.Select(m =>
                    $"  Yüklü: {m.Id} ({m.Type}), bağlam {m.LoadedContextLength?.ToString() ?? "?"} / en fazla {m.MaxContextLength?.ToString() ?? "?"} token"));
                if (loaded.Count > 1)
                {
                    lines.Add("  UYARI: Birden çok model aynı anda yüklü; 8 GB GPU'da bellek taşar, yanıtlar ~6 kat yavaşlar.");
                }
                return lines;
            }
            catch (LlmException ex)
            {
                LmStudioStatusTextBlock.Text = "Ulaşılamıyor";
                return new[] { "LM Studio: " + ex.Message };
            }
        }

        private async Task<IEnumerable<string>> CheckGpuAsync()
        {
            var gpu = await GpuMonitor.ReadAsync();
            if (gpu is null)
            {
                GpuMemoryTextBlock.Text = "Yok";
                return new[] { "GPU: nvidia-smi bulunamadı ya da yanıt vermedi" };
            }

            GpuMemoryTextBlock.Text = $"{gpu.UsedMb} / {gpu.TotalMb} MB";
            GpuMemoryTextBlock.Foreground = gpu.Health switch
            {
                GpuHealth.Spilling or GpuHealth.Hot => Brushes.OrangeRed,
                GpuHealth.Tight => Brushes.Gold,
                _ => Brushes.White
            };
            return new[]
            {
                $"GPU: {gpu.Name}, bellek {gpu.UsedMb} / {gpu.TotalMb} MB (boş {gpu.FreeMb} MB)",
                $"GPU: paylaşılan (sistem RAM'i) {gpu.SharedMb?.ToString() ?? "?"} MB, {gpu.TemperatureC:0}°C, {gpu.PowerW:0} W, {gpu.ClockMhz} MHz, kullanım %{gpu.UtilizationPercent}",
                "Durum: " + gpu.Describe()
            };
        }
    }
}
