using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace LLM_Memory_Manager
{
    public partial class DiagnosticsWindow : Window
    {
        private static readonly HttpClient HttpClient = new()
        {
            BaseAddress = new Uri("http://127.0.0.1:1234")
        };

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

            logLines.Add(await CheckLmStudioAsync());
            logLines.Add(await CheckGpuMemoryAsync());

            DiagnosticsListBox.ItemsSource = logLines;
        }

        private async Task<string> CheckLmStudioAsync()
        {
            try
            {
                using var response = await HttpClient.GetAsync("/v1/models");
                var body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    LmStudioStatusTextBlock.Text = "Kapalı";
                    return $"LM Studio: Hata {response.StatusCode} - {body}";
                }

                using var document = JsonDocument.Parse(body);
                var count = document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                    ? data.GetArrayLength()
                    : 0;

                LmStudioStatusTextBlock.Text = "Aktif";
                return $"LM Studio: Aktif, model sayısı {count}";
            }
            catch (Exception ex)
            {
                LmStudioStatusTextBlock.Text = "Ulaşılamıyor";
                return $"LM Studio kontrolü başarısız: {ex.Message}";
            }
        }

        private async Task<string> CheckGpuMemoryAsync()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "--query-gpu=memory.used,memory.total --format=csv,noheader,nounits",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    GpuMemoryTextBlock.Text = "Yok";
                    return "GPU belleği: nvidia-smi başlatılamadı";
                }

                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                {
                    GpuMemoryTextBlock.Text = "Yok";
                    return string.IsNullOrWhiteSpace(error)
                        ? "GPU belleği: bilgi alınamadı"
                        : $"GPU belleği: {error.Trim()}";
                }

                var parts = output.Trim().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 2 &&
                    double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var usedMb) &&
                    double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var totalMb))
                {
                    GpuMemoryTextBlock.Text = $"{usedMb:0.0} / {totalMb:0.0} MB";
                    return $"GPU belleği: {usedMb:0.0} / {totalMb:0.0} MB";
                }

                GpuMemoryTextBlock.Text = "Bilinmiyor";
                return $"GPU belleği: çözümlenemedi ({output.Trim()})";
            }
            catch (Exception ex)
            {
                GpuMemoryTextBlock.Text = "Yok";
                return $"GPU belleği okunamadı: {ex.Message}";
            }
        }
    }
}
