using System.Text;
using Windows.Devices.Enumeration;

namespace BeatSaber_DGLAB;

internal class Program
{
    private static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        PrintHeader();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            // 阻止立即退出,让我们有机会停止 watcher 再退出
            e.Cancel = true;
            cts.Cancel();
        };

        // Windows 会同时触发 Added 和 Updated,用 HashSet 按 Id 去重
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var scanner = new BluetoothScanner(includePaired: false);

        scanner.DeviceAdded += device => PrintDevice("[+] ADD  ", device, seen);
        scanner.DeviceUpdated += device => PrintDevice("[~] UPDATE", device, seen);

        scanner.EnumerationCompleted += () =>
            Console.WriteLine("[i] 初始枚举完成,继续监听广播 ...\n");

        scanner.Start();

        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (TaskCanceledException)
        {
            // Ctrl+C 正常退出
        }

        scanner.Stop();
        Console.WriteLine("\n[i] 已停止扫描。再见。");
    }

    private static void PrintHeader()
    {
        Console.WriteLine("===========================================");
        Console.WriteLine(" Beat Saber DGLAB  -  Bluetooth Scanner   ");
        Console.WriteLine("===========================================");
        Console.WriteLine(" 扫描附近的蓝牙设备。Ctrl+C 停止。\n");
    }

    private static void PrintDevice(string tag, DeviceInformation device, HashSet<string> seen)
    {
        // DeviceUpdated 走的是 CreateFromIdAsync 回查,Name 可能仍为空,做兜底
        var name = string.IsNullOrWhiteSpace(device.Name) ? "(未命名)" : device.Name;
        var address = Prop(device, "System.Devices.Aep.DeviceAddress") ?? "?";
        var rssi = Prop(device, "System.Devices.Aep.SignalStrength") ?? "?";
        var paired = Prop(device, "System.Devices.Aep.IsPaired") ?? "?";
        var connectable = Prop(device, "System.Devices.Aep.Bluetooth.Le.IsConnectable") ?? "?";

        var isDglab = BluetoothScanner.DglabNameHints
            .Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase));

        // 每个 Id 只在第一次出现时打印;但疑似 DGLAB 设备每次更新都打印(关注 RSSI 变化)
        if (isDglab || seen.Add(device.Id))
        {
            var marker = isDglab ? " ★ DGLAB?" : "  ";
            Console.WriteLine($"{tag} {marker}{name,-24} {address,-20} RSSI:{rssi,4} dBm  paired:{paired}  conn:{connectable}");
        }
    }

    private static string? Prop(DeviceInformation device, string key)
        => device.Properties.TryGetValue(key, out var v) ? v?.ToString() : null;
}