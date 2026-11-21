using System.Diagnostics;
using System.Globalization;

namespace LlmBridge.Core;

public enum GpuHealth
{
    Unknown,
    Ok,
    Tight,
    Spilling,
    Hot,
}

/// <summary>GPU'nun anlık durumu. Paylaşılan bellek: GPU belleği dolunca Windows'un sistem RAM'ine taşıdığı kısım.</summary>
public sealed record GpuSnapshot(
    string Name,
    int UsedMb,
    int TotalMb,
    double TemperatureC,
    double PowerW,
    int ClockMhz,
    int UtilizationPercent,
    int? SharedMb)
{
    // Ölçüldü (RTX 4060 Laptop, 8 GB): taşma yokken paylaşılan kullanım 45–220 MB; Qwen3-VL-8B 12288 bağlamla
    // yüklenince GPU'da 445 MB boş kalıp paylaşılan 722 MB'a çıktı ve hız düştü.
    public const int SpillSharedMb = 400;
    public const int TightFreeMb = 600;
    public const double HotC = 83;

    public int FreeMb => TotalMb - UsedMb;

    public GpuHealth Health =>
        TemperatureC >= HotC ? GpuHealth.Hot
        : SharedMb >= SpillSharedMb && FreeMb < TightFreeMb ? GpuHealth.Spilling
        : FreeMb < TightFreeMb ? GpuHealth.Tight
        : GpuHealth.Ok;

    public string Describe() => Health switch
    {
        GpuHealth.Hot => $"Sıcak ({TemperatureC:0}°C): dizüstü GPU'lar ~87°C'de saat hızını düşürür. Havalandırmayı kontrol edin.",
        GpuHealth.Spilling => $"TAŞMA: GPU belleği dolu, ~{SharedMb - 200} MB sistem RAM'ine taşmış olabilir. Yanıtlar yavaşlar; " +
                              "bağlam uzunluğunu düşürün ya da ikinci modeli kaldırın.",
        GpuHealth.Tight => $"Sınırda: GPU'da yalnızca {FreeMb} MB boş. Tarayıcı / kamera da GPU kullanırsa taşma başlar.",
        GpuHealth.Ok => $"Normal: {FreeMb} MB boş, {TemperatureC:0}°C.",
        _ => "Bilinmiyor",
    };
}

/// <summary>nvidia-smi ve Windows performans sayaçlarıyla GPU ölçümü. NVIDIA yoksa null döner.</summary>
public static class GpuMonitor
{
    public static async Task<GpuSnapshot?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var line = await RunAsync("nvidia-smi",
            "--query-gpu=name,memory.used,memory.total,temperature.gpu,power.draw,clocks.sm,utilization.gpu --format=csv,noheader,nounits",
            cancellationToken);
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }
        var parts = line.Split('\n')[0].Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 7)
        {
            return null;
        }
        return new GpuSnapshot(
            parts[0],
            (int)Num(parts[1]),
            (int)Num(parts[2]),
            Num(parts[3]),
            Num(parts[4]),
            (int)Num(parts[5]),
            (int)Num(parts[6]),
            await ReadSharedUsageMbAsync(cancellationToken));
    }

    /// <summary>Tüm GPU bağdaştırıcılarının paylaşılan bellek kullanımı (Windows "GPU Adapter Memory" sayacı).</summary>
    public static async Task<int?> ReadSharedUsageMbAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        var output = await RunAsync("powershell",
            "-NoProfile -Command \"$v=((Get-Counter '\\GPU Adapter Memory(*)\\Shared Usage').CounterSamples | Measure-Object CookedValue -Sum).Sum; [int]($v/1MB)\"",
            cancellationToken);
        return int.TryParse(output?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mb) ? mb : null;
    }

    private static double Num(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static async Task<string?> RunAsync(string file, string arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return null;
            }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var output = await process.StandardOutput.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException)
        {
            return null;
        }
    }
}
